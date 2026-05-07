using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MangosSuperUI.BotLogic.Core;
using Microsoft.Extensions.Logging;

namespace MangosSuperUI.BotLogic.Tracking;

// ════════════════════════════════════════════════════════════════════
// Issue Types — every category of "something went wrong"
// ════════════════════════════════════════════════════════════════════

public enum DiagnosticIssueType
{
    PathUnsafe,          // C++ rejected a MOVE_TO (dangerous path)
    QuestDeferred,       // Bot shelved a quest (death, stuck, PATH_UNSAFE)
    Death,               // Bot died
    DeathLoop,           // Bot died 3+ times on same quest/location
    StuckDetected,       // Bot hasn't moved in 30+ seconds during travel
    WatchdogReset,       // BotBrainService watchdog force-reset to Idle
    QuestInteractFail,   // C++ rejected a QUEST_INTERACT (requirements, bags full, etc.)
    NoQuestsAvailable,   // Bot entered NoQuestsAvailable — nothing safe to do
    SellFail,            // C++ SELL_FAIL event
    PathBlacklistFull,   // Bot has 10+ blacklisted destinations — probably boxed in
}

// ════════════════════════════════════════════════════════════════════
// Issue Entry — one thing that went wrong
// ════════════════════════════════════════════════════════════════════

public class DiagnosticIssue
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public DiagnosticIssueType Type { get; set; }
    public int BotGuid { get; set; }
    public string BotName { get; set; } = "";
    public int BotLevel { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public int MapId { get; set; }
    public int? QuestId { get; set; }
    public string? QuestTitle { get; set; }
    public string Detail { get; set; } = "";
}

// ════════════════════════════════════════════════════════════════════
// Fleet Health Snapshot — periodic aggregate of all bot state
// ════════════════════════════════════════════════════════════════════

public class FleetHealthSnapshot
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int TotalBots { get; set; }
    public int DeadBots { get; set; }
    public int StuckBots { get; set; }                // haven't moved in 5+ min
    public Dictionary<string, int> BotsByActivity { get; set; } = new();
    public Dictionary<string, int> BotsBySubPhase { get; set; } = new();
    public int TotalQuestsCompleted { get; set; }
    public int TotalDeaths { get; set; }              // deaths this session
    public int TotalPathUnsafe { get; set; }          // PATH_UNSAFE events this session
    public int TotalQuestDeferred { get; set; }       // quest deferrals this session
    public int BotsInNoQuestsAvailable { get; set; }
    public int BotsInDeathLoop { get; set; }          // 3+ deaths since quest start
    public List<string> TopDeferredQuests { get; set; } = new();   // most-deferred quest IDs + counts
    public List<string> TopDeathLocations { get; set; } = new();   // most-deadly coordinates
    public List<string> TopBlacklistedCoords { get; set; } = new(); // most PATH_UNSAFE'd destinations
    public List<string> ProblematicBots { get; set; } = new();     // bots with 3+ issues in last 10 min
}

// ════════════════════════════════════════════════════════════════════
// BotFleetDiagnostics — the service
// ════════════════════════════════════════════════════════════════════

/// <summary>
/// Fleet-level diagnostics for the AiBot system. Two responsibilities:
///
/// 1. ISSUE LEDGER — Any domain or service calls RecordIssue() when something
///    goes wrong. Entries accumulate in memory and flush to a JSONL file every
///    30 seconds. After a session, you can sort/filter/grep the file to see
///    patterns (which quests fail most, which coords kill bots, etc.)
///
/// 2. HEALTH SNAPSHOTS — Every 60 seconds, GenerateHealthSnapshot() aggregates
///    all bot state into a one-glance summary. Logged to console AND written
///    to a separate file. Shows fleet-wide patterns: how many bots are stuck,
///    dead, looping, or productive.
///
/// Files written to: /opt/mangossuperui/diagnostics/
///   issues_{date}.jsonl    — one JSON object per line, per issue
///   health_{date}.jsonl    — one JSON object per line, per snapshot
///
/// DI: Register as singleton in Program.cs:
///   builder.Services.AddSingleton&lt;BotFleetDiagnostics&gt;();
/// </summary>
public class BotFleetDiagnostics
{
    private readonly ILogger<BotFleetDiagnostics> _logger;

    // Issue ledger — accumulates until flushed
    private readonly ConcurrentBag<DiagnosticIssue> _pendingIssues = new();

    // Session-lifetime counters (never reset, used for health snapshots)
    private int _sessionDeaths;
    private int _sessionPathUnsafe;
    private int _sessionQuestDeferred;

    // Rolling window for "problematic bot" detection
    private readonly ConcurrentDictionary<int, List<DateTime>> _recentIssuesByBot = new();

    // Flush tracking
    private DateTime _lastFlush = DateTime.UtcNow;
    private DateTime _lastSnapshot = DateTime.UtcNow;
    private const int FLUSH_INTERVAL_SECONDS = 30;
    private const int SNAPSHOT_INTERVAL_SECONDS = 60;

    // Output directory
    private const string DIAGNOSTICS_DIR = "/opt/mangossuperui/diagnostics";

    // JSON options — compact, no indentation for JSONL
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BotFleetDiagnostics(ILogger<BotFleetDiagnostics> logger)
    {
        _logger = logger;

        try
        {
            Directory.CreateDirectory(DIAGNOSTICS_DIR);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotFleetDiagnostics: could not create {Dir}, will log only", DIAGNOSTICS_DIR);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // RecordIssue — call this from anywhere something goes wrong
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reset all session-lifetime counters and rolling issue windows.
    /// Call when brain is toggled off to get clean data on next enable.
    /// </summary>
    public void ResetSessionCounters()
    {
        Interlocked.Exchange(ref _sessionDeaths, 0);
        Interlocked.Exchange(ref _sessionPathUnsafe, 0);
        Interlocked.Exchange(ref _sessionQuestDeferred, 0);
        _recentIssuesByBot.Clear();
        _logger.LogInformation("BotFleetDiagnostics: session counters reset");
    }

    /// <summary>
    /// Record a diagnostic issue. Thread-safe, non-blocking.
    /// Call from any domain, service, or event handler.
    /// </summary>
    public void RecordIssue(DiagnosticIssueType type, BotIdentity bot,
        float x, float y, int mapId,
        int? questId = null, string? questTitle = null, string detail = "")
    {
        var issue = new DiagnosticIssue
        {
            Type = type,
            BotGuid = bot.Guid,
            BotName = bot.Name,
            BotLevel = bot.Level,
            X = x,
            Y = y,
            MapId = mapId,
            QuestId = questId,
            QuestTitle = questTitle,
            Detail = detail
        };

        _pendingIssues.Add(issue);

        // Update session counters
        switch (type)
        {
            case DiagnosticIssueType.Death:
            case DiagnosticIssueType.DeathLoop:
                Interlocked.Increment(ref _sessionDeaths);
                break;
            case DiagnosticIssueType.PathUnsafe:
                Interlocked.Increment(ref _sessionPathUnsafe);
                break;
            case DiagnosticIssueType.QuestDeferred:
                Interlocked.Increment(ref _sessionQuestDeferred);
                break;
        }

        // Track rolling window for this bot
        var timestamps = _recentIssuesByBot.GetOrAdd(bot.Guid, _ => new List<DateTime>());
        lock (timestamps)
        {
            timestamps.Add(DateTime.UtcNow);
            // Trim entries older than 10 minutes
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            timestamps.RemoveAll(t => t < cutoff);
        }
    }

    /// <summary>
    /// Convenience overload using BotStateSnapshot for coordinates.
    /// </summary>
    public void RecordIssue(DiagnosticIssueType type, BotIdentity bot, BotStateSnapshot state,
        int? questId = null, string? questTitle = null, string detail = "")
    {
        RecordIssue(type, bot, state.X, state.Y, state.MapId, questId, questTitle, detail);
    }

    // ════════════════════════════════════════════════════════════════════
    // GenerateHealthSnapshot — fleet-wide aggregate
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build a fleet health snapshot from all active bots.
    /// Called by BotBrainService every 60 seconds.
    /// </summary>
    public FleetHealthSnapshot GenerateHealthSnapshot(
        IReadOnlyDictionary<int, BotIdentity> bots,
        Func<int, BotStateSnapshot?> getState)
    {
        // Filter to only active bots — skip ghosts with no bridge state
        var activeBots = bots
            .Where(kvp => getState(kvp.Key) != null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var snapshot = new FleetHealthSnapshot
        {
            TotalBots = activeBots.Count,
            TotalDeaths = _sessionDeaths,
            TotalPathUnsafe = _sessionPathUnsafe,
            TotalQuestDeferred = _sessionQuestDeferred
        };

        // Per-quest deferral frequency (across all bots)
        var questDeferCounts = new Dictionary<int, (int count, string title)>();
        // Death location clustering (rounded to 50yd grid)
        var deathLocCounts = new Dictionary<(int x, int y, int map), int>();
        // Blacklist coord frequency
        var blacklistCounts = new Dictionary<(int x, int y), int>();

        foreach (var kvp in activeBots)
        {
            var bot = kvp.Value;
            var state = getState(bot.Guid);

            // Activity breakdown
            string actKey = bot.CurrentActivity.Type.ToString();
            snapshot.BotsByActivity.TryGetValue(actKey, out int actCount);
            snapshot.BotsByActivity[actKey] = actCount + 1;

            // Sub-phase breakdown
            string subKey = $"{actKey}:{bot.CurrentActivity.SubPhase ?? "none"}";
            snapshot.BotsBySubPhase.TryGetValue(subKey, out int subCount);
            snapshot.BotsBySubPhase[subKey] = subCount + 1;

            // Dead bots
            if (state is { } s && s.IsDead)
                snapshot.DeadBots++;

            // Death loop detection
            if (bot.DeathsSinceQuestStart >= 3)
                snapshot.BotsInDeathLoop++;

            // NoQuestsAvailable
            if (bot.CurrentActivity.SubPhase == "NoQuestsAvailable")
                snapshot.BotsInNoQuestsAvailable++;

            // Stuck detection (5+ minutes in a travel phase without movement)
            if (bot.CurrentActivity.SubPhase?.StartsWith("Traveling") == true
                && bot.CurrentActivity.MinutesInState > 5.0)
                snapshot.StuckBots++;

            // Total quests completed
            snapshot.TotalQuestsCompleted += bot.CompletedQuestIds.Count;

            // Aggregate deferred quests
            foreach (var dq in bot.DeferredQuestIds)
            {
                if (questDeferCounts.TryGetValue(dq.Key, out var existing))
                    questDeferCounts[dq.Key] = (existing.count + 1, existing.title);
                else
                    questDeferCounts[dq.Key] = (1, $"quest#{dq.Key}");
            }

            // Aggregate death locations
            if (bot.LastDeathTime > DateTime.UtcNow.AddHours(-2) && bot.LastDeathLocation.X != 0)
            {
                var locKey = (
                    (int)(MathF.Round(bot.LastDeathLocation.X / 50f) * 50),
                    (int)(MathF.Round(bot.LastDeathLocation.Y / 50f) * 50),
                    bot.LastDeathLocation.Map);
                deathLocCounts.TryGetValue(locKey, out int dlCount);
                deathLocCounts[locKey] = dlCount + 1;
            }

            // Aggregate blacklisted coords
            foreach (var bl in bot.PathBlacklist)
            {
                blacklistCounts.TryGetValue(bl.Key, out int blCount);
                blacklistCounts[bl.Key] = blCount + 1;
            }
        }

        // Top deferred quests (top 5 by frequency)
        snapshot.TopDeferredQuests = questDeferCounts
            .OrderByDescending(kv => kv.Value.count)
            .Take(5)
            .Select(kv => $"[{kv.Key}] x{kv.Value.count}")
            .ToList();

        // Top death locations (top 5)
        snapshot.TopDeathLocations = deathLocCounts
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"({kv.Key.x},{kv.Key.y} map={kv.Key.map}) x{kv.Value}")
            .ToList();

        // Top blacklisted coords (top 5)
        snapshot.TopBlacklistedCoords = blacklistCounts
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"({kv.Key.x},{kv.Key.y}) x{kv.Value}")
            .ToList();

        // Problematic bots (3+ issues in last 10 min)
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var kvp in _recentIssuesByBot)
        {
            var list = kvp.Value;
            List<DateTime> timestamps;
            lock (list) { timestamps = list.ToList(); }

            int recent = timestamps.Count(t => t >= cutoff);
            if (recent >= 3 && bots.TryGetValue(kvp.Key, out var pBot))
            {
                snapshot.ProblematicBots.Add($"{pBot.Name}(lvl{pBot.Level}) x{recent}");
            }
        }

        snapshot.ProblematicBots = snapshot.ProblematicBots
            .OrderByDescending(s => s)
            .Take(10)
            .ToList();

        return snapshot;
    }

    // ════════════════════════════════════════════════════════════════════
    // LogHealthSnapshot — console + file output
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Format and log the health snapshot as a readable console block,
    /// then write the raw JSON to the health JSONL file.
    /// </summary>
    public void LogHealthSnapshot(FleetHealthSnapshot snapshot)
    {
        // ── Console output: compact readable block ──
        var sb = new StringBuilder();
        sb.AppendLine("┌─────────────── FLEET HEALTH ───────────────┐");
        sb.AppendLine($"│ Bots: {snapshot.TotalBots} total, {snapshot.DeadBots} dead, " +
                       $"{snapshot.StuckBots} stuck, {snapshot.BotsInDeathLoop} in death loop");
        sb.AppendLine($"│ Quests completed: {snapshot.TotalQuestsCompleted} | " +
                       $"NoQuests: {snapshot.BotsInNoQuestsAvailable}");
        sb.AppendLine($"│ Session totals — Deaths: {snapshot.TotalDeaths}, " +
                       $"PathUnsafe: {snapshot.TotalPathUnsafe}, Deferred: {snapshot.TotalQuestDeferred}");

        if (snapshot.BotsByActivity.Count > 0)
        {
            var activities = string.Join(", ",
                snapshot.BotsByActivity.OrderByDescending(kv => kv.Value)
                    .Select(kv => $"{kv.Key}={kv.Value}"));
            sb.AppendLine($"│ Activities: {activities}");
        }

        if (snapshot.TopDeferredQuests.Count > 0)
            sb.AppendLine($"│ Top deferred: {string.Join(", ", snapshot.TopDeferredQuests)}");

        if (snapshot.TopDeathLocations.Count > 0)
            sb.AppendLine($"│ Death hotspots: {string.Join(", ", snapshot.TopDeathLocations)}");

        if (snapshot.TopBlacklistedCoords.Count > 0)
            sb.AppendLine($"│ Blacklisted paths: {string.Join(", ", snapshot.TopBlacklistedCoords)}");

        if (snapshot.ProblematicBots.Count > 0)
            sb.AppendLine($"│ Problem bots: {string.Join(", ", snapshot.ProblematicBots)}");

        sb.Append("└────────────────────────────────────────────┘");

        _logger.LogInformation("[FLEET-HEALTH] {Snapshot}", sb.ToString());

        // ── File output: raw JSON for post-session analysis ──
        WriteToFile("health", snapshot);
    }

    // ════════════════════════════════════════════════════════════════════
    // Periodic Flush — call from BotBrainService main loop
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Flush pending issues to disk and generate health snapshot if due.
    /// Called from BotBrainService main loop (~250ms cadence).
    /// Returns a health snapshot if one was generated, null otherwise.
    /// </summary>
    public FleetHealthSnapshot? TickDiagnostics(
        IReadOnlyDictionary<int, BotIdentity> bots,
        Func<int, BotStateSnapshot?> getState)
    {
        var now = DateTime.UtcNow;

        // Flush issues to disk
        if ((now - _lastFlush).TotalSeconds >= FLUSH_INTERVAL_SECONDS)
        {
            FlushIssuesToDisk();
            _lastFlush = now;
        }

        // Generate health snapshot
        FleetHealthSnapshot? snapshot = null;
        if ((now - _lastSnapshot).TotalSeconds >= SNAPSHOT_INTERVAL_SECONDS)
        {
            snapshot = GenerateHealthSnapshot(bots, getState);
            LogHealthSnapshot(snapshot);
            _lastSnapshot = now;
        }

        return snapshot;
    }

    // ════════════════════════════════════════════════════════════════════
    // File I/O
    // ════════════════════════════════════════════════════════════════════

    private void FlushIssuesToDisk()
    {
        var issues = new List<DiagnosticIssue>();
        while (_pendingIssues.TryTake(out var issue))
            issues.Add(issue);

        if (issues.Count == 0) return;

        try
        {
            var path = GetFilePath("issues");
            using var writer = new StreamWriter(path, append: true);
            foreach (var issue in issues)
            {
                var json = JsonSerializer.Serialize(issue, _jsonOpts);
                writer.WriteLine(json);
            }

            _logger.LogDebug("BotFleetDiagnostics: flushed {Count} issues to {Path}", issues.Count, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotFleetDiagnostics: failed to flush {Count} issues to disk", issues.Count);
        }
    }

    private void WriteToFile(string prefix, object data)
    {
        try
        {
            var path = GetFilePath(prefix);
            var json = JsonSerializer.Serialize(data, _jsonOpts);
            File.AppendAllText(path, json + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotFleetDiagnostics: failed to write {Prefix} to disk", prefix);
        }
    }

    private static string GetFilePath(string prefix)
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Path.Combine(DIAGNOSTICS_DIR, $"{prefix}_{date}.jsonl");
    }
}