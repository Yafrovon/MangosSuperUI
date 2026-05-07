using System.Collections.Concurrent;
using MangosSuperUI.BotLogic.Core;
using MangosSuperUI.BotLogic.Domains;
using MangosSuperUI.BotLogic.Data;
using MangosSuperUI.BotLogic.Tracking;

using MangosSuperUI.Hubs;
using MangosSuperUI.Services;
using Microsoft.AspNetCore.SignalR;
using Dapper;
using MangosSuperUI.Models;

namespace MangosSuperUI.Services;

/// <summary>
/// The behavioral engine orchestrator. Maintains one DecisionEngine per bot,
/// subscribes to BotBridgeService events, and drives the decision loop.
///
/// Wiring:
///   - Subscribes to BotBridgeService.BotStates changes (polling via the main loop)
///   - Routes KILL events to EconomyDomain.ProcessKillLoot (always-on)
///   - Emits BotDecision via SignalR for dashboard "Brain" panel
///   - Batches activity log writes via BotActivityLog
///   - Persists/loads personality via bot_personality table (admin DB)
/// </summary>
public class BotBrainService : BackgroundService
{
    private readonly BotBridgeService _bridge;
    private readonly IHubContext<BotBridgeHub> _hub;
    private readonly ConnectionFactory _db;
    private readonly BotStateTracker _tracker;
    private readonly BotActivityLog _activityLog;
    private readonly QuirkLoader _quirkLoader;
    private readonly ILogger<BotBrainService> _logger;
    private readonly OllamaChatService _ollama;
    private readonly QuestGraphLoader _questGraph;
    private readonly ZoneSafetyMap _safetyMap;
    private readonly BotFleetDiagnostics _diagnostics;

    // Per-bot instances
    private readonly ConcurrentDictionary<int, DecisionEngine> _engines = new();
    private readonly ConcurrentDictionary<int, BotIdentity> _bots = new();

    // Domain engines (singleton, stateless — per-bot state is in BotIdentity)
    private readonly QuestingDomain _questing;

    private readonly CombatDomain _combat = new();
    private readonly EconomyDomain _economy;
    private readonly SocialDomain _social = new();
    private readonly ExplorationDomain _exploration = new();
    private readonly TrainingDomain _training;
    private readonly MaintenanceDomain _maintenance;
    private readonly LiveStateModifiers _liveState;
    private readonly BotBrainDbInit _dbInit;
    private readonly ZoneDataLoader _zoneDataLoader;
    private readonly SpellProgressionLoader _spellLoader;
    private readonly GroupManager _groupManager;

    // Track which bot GUIDs we've already initialized (to detect new connections)
    private readonly HashSet<int> _initializedGuids = new();

    // Fleet-level shared "known-good" destinations — if ANY bot successfully interacted
    // at these coordinates, other bots should NOT blacklist them on MOVE_FAILED.
    // Persisted to vmangos_admin.known_good_destinations so they survive restarts.
    // Key = rounded (X/5*5, Y/5*5).
    private static readonly HashSet<(int, int)> _knownGoodDestinations = new();
    private static readonly object _knownGoodLock = new();

    private static (int, int) RoundDest(float x, float y) => ((int)MathF.Round(x / 5) * 5, (int)MathF.Round(y / 5) * 5);

    private static bool IsKnownGoodDestination(float x, float y)
    {
        lock (_knownGoodLock) return _knownGoodDestinations.Contains(RoundDest(x, y));
    }

    private async Task MarkDestinationGoodAsync(float x, float y, int map, string source)
    {
        var key = RoundDest(x, y);
        bool isNew;
        lock (_knownGoodLock) isNew = _knownGoodDestinations.Add(key);

        try
        {
            using var conn = _db.Admin();
            await conn.ExecuteAsync(@"
                INSERT INTO known_good_destinations (grid_x, grid_y, map_id, source, first_seen, last_seen)
                VALUES (@GridX, @GridY, @Map, @Source, NOW(), NOW())
                ON DUPLICATE KEY UPDATE last_seen = NOW(), hit_count = hit_count + 1",
                new { GridX = key.Item1, GridY = key.Item2, Map = map, Source = source });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist known-good destination ({X},{Y})", key.Item1, key.Item2);
        }

        if (isNew)
            _logger.LogInformation("[BOT-PATH] New known-good destination ({X},{Y}) map={Map} source={Source}",
                key.Item1, key.Item2, map, source);
    }

    private async Task LoadKnownGoodDestinationsAsync()
    {
        try
        {
            using var conn = _db.Admin();

            // Auto-create table if it doesn't exist
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS known_good_destinations (
                    grid_x INT NOT NULL,
                    grid_y INT NOT NULL,
                    map_id INT NOT NULL DEFAULT 0,
                    source VARCHAR(64) DEFAULT '',
                    first_seen DATETIME DEFAULT CURRENT_TIMESTAMP,
                    last_seen DATETIME DEFAULT CURRENT_TIMESTAMP,
                    hit_count INT DEFAULT 1,
                    PRIMARY KEY (grid_x, grid_y)
                )");

            var rows = await conn.QueryAsync<(int grid_x, int grid_y)>(
                "SELECT grid_x, grid_y FROM known_good_destinations");

            lock (_knownGoodLock)
            {
                foreach (var row in rows)
                    _knownGoodDestinations.Add((row.grid_x, row.grid_y));
            }

            _logger.LogInformation("BotBrain: loaded {Count} known-good destinations from DB", _knownGoodDestinations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load known-good destinations — starting fresh");
        }
    }

    // ── Session 31: Grouping mode persistence ──

    private async Task LoadGroupingModeAsync()
    {
        try
        {
            using var conn = _db.Admin();
            var value = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT setting_value FROM bot_settings WHERE setting_key = 'grouping_mode'");

            if (value != null && int.TryParse(value, out int mode) && Enum.IsDefined(typeof(GroupingMode), mode))
            {
                _groupManager.Mode = (GroupingMode)mode;
                _logger.LogInformation("BotBrain: loaded grouping mode from DB: {Mode}", _groupManager.Mode);
            }
            else
            {
                _groupManager.Mode = GroupingMode.Off;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBrain: failed to load grouping mode, defaulting to Off");
            _groupManager.Mode = GroupingMode.Off;
        }
    }

    /// <summary>Set grouping mode from dashboard and persist to DB.</summary>
    public async Task SetGroupingModeAsync(GroupingMode mode)
    {
        _groupManager.Mode = mode;
        try
        {
            using var conn = _db.Admin();
            await conn.ExecuteAsync(@"
                INSERT INTO bot_settings (setting_key, setting_value)
                VALUES ('grouping_mode', @Value)
                ON DUPLICATE KEY UPDATE setting_value = @Value",
                new { Value = ((int)mode).ToString() });
            await _groupManager.SaveGroupsToDbAsync();
            _groupManager.EnrichAllBots(_bots.Values);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BotBrain: failed to persist grouping mode");
        }
    }

    /// <summary>Form a group from the dashboard. Sends FORM_GROUP to C++ leader.</summary>
    public async Task<BotGroup?> FormGroupAsync(int leaderGuid, params int[] followerGuids)
    {
        var group = _groupManager.FormGroup(leaderGuid, followerGuids);
        if (group == null) return null;

        foreach (var guid in group.MemberGuids)
            if (_bots.TryGetValue(guid, out var bot))
                _groupManager.EnrichBotIdentity(bot);

        await _bridge.SendToBotAsync(leaderGuid, "FORM_GROUP", new
        {
            member_guids = group.GetFollowers()
        });

        await _groupManager.SaveGroupsToDbAsync();
        return group;
    }

    /// <summary>Disband a group from the dashboard.</summary>
    public async Task DisbandGroupAsync(int groupId)
    {
        var group = _groupManager.GetAllGroups().FirstOrDefault(g => g.GroupId == groupId);
        if (group == null) return;

        int leaderGuid = group.LeaderGuid;
        var members = group.MemberGuids.ToList();

        _groupManager.DisbandGroup(groupId);
        foreach (var guid in members)
            if (_bots.TryGetValue(guid, out var bot))
                _groupManager.EnrichBotIdentity(bot);

        await _bridge.SendToBotAsync(leaderGuid, "DISBAND_GROUP", new { });
        await _groupManager.SaveGroupsToDbAsync();
    }

    /// <summary>Auto-form groups from dashboard. Returns formed groups.</summary>
    public async Task<List<BotGroup>> AutoFormGroupsAsync()
    {
        var formed = _groupManager.AutoFormGroups(AllBots,
            guid => _tracker.GetAllPositions().FirstOrDefault(p => p.Guid == guid));
        foreach (var group in formed)
        {
            foreach (var guid in group.MemberGuids)
                if (_bots.TryGetValue(guid, out var bot))
                    _groupManager.EnrichBotIdentity(bot);

            await _bridge.SendToBotAsync(group.LeaderGuid, "FORM_GROUP", new
            {
                member_guids = group.GetFollowers()
            });
        }
        if (formed.Count > 0)
            await _groupManager.SaveGroupsToDbAsync();
        return formed;
    }

    // Track when each bot was first seen disconnected (for stale eviction)
    private readonly ConcurrentDictionary<int, DateTime> _disconnectedAt = new();

    // Brain enabled flag (can be toggled from UI)
    private volatile bool _brainEnabled = false;




    public BotBrainService(
        BotBridgeService bridge,
        IHubContext<BotBridgeHub> hub,
        ConnectionFactory db,
        BotStateTracker tracker,
        BotActivityLog activityLog,
        QuirkLoader quirkLoader,
        LiveStateModifiers liveState,
        BotBrainDbInit dbInit,
        ILogger<BotBrainService> logger,
        OllamaChatService ollama,
        QuestGraphLoader questGraph,
        ZoneDataLoader zoneDataLoader,
        ZoneSafetyMap safetyMap,
        SpellProgressionLoader spellLoader,
        BotFleetDiagnostics diagnostics,
        ILoggerFactory loggerFactory)
    {
        _bridge = bridge;
        _hub = hub;
        _db = db;
        _tracker = tracker;
        _activityLog = activityLog;
        _quirkLoader = quirkLoader;
        _liveState = liveState;
        _dbInit = dbInit;
        _logger = logger;
        _ollama = ollama;
        _questGraph = questGraph;
        _zoneDataLoader = zoneDataLoader;
        _safetyMap = safetyMap;
        _diagnostics = diagnostics;
        _spellLoader = spellLoader;
        _questing = new QuestingDomain(_questGraph, _safetyMap, loggerFactory.CreateLogger<QuestingDomain>(), db, _diagnostics);
        _economy = new EconomyDomain(_zoneDataLoader, loggerFactory.CreateLogger<EconomyDomain>());
        _maintenance = new MaintenanceDomain(_safetyMap, loggerFactory.CreateLogger<MaintenanceDomain>());
        _training = new TrainingDomain(_spellLoader, loggerFactory.CreateLogger<TrainingDomain>());
        _groupManager = new GroupManager(_db, loggerFactory.CreateLogger<GroupManager>());
    }

    // ==================== Public API (for controller/hub) ====================

    /// <summary>Group manager — exposed for dashboard controller.</summary>
    public GroupManager GroupManager => _groupManager;

    public bool BrainEnabled
    {
        get => _brainEnabled;
        set
        {
            _brainEnabled = value;
            _logger.LogInformation("BotBrain: engine {State}", value ? "ENABLED" : "DISABLED");

            if (!value)
            {
                var count = _bots.Count;
                _bots.Clear();
                _engines.Clear();
                _initializedGuids.Clear();
                _disconnectedAt.Clear();
                _diagnostics.ResetSessionCounters();
                _logger.LogInformation("BotBrain: cleared {Count} bot entries on disable — next enable starts clean", count);
            }
        }
    }

    public IReadOnlyDictionary<int, BotIdentity> AllBots => _bots;

    public BotIdentity? GetBotIdentity(int guid) =>
        _bots.TryGetValue(guid, out var bot) ? bot : null;

    public int ActiveBotCount => _bots.Count;

    /// <summary>
    /// Get the last decision result summary for a bot (for dashboard).
    /// </summary>
    public object? GetBotBrainSummary(int guid)
    {
        if (!_bots.TryGetValue(guid, out var bot)) return null;
        var bs = _bridge.GetBotState(guid);
        return new
        {
            guid = bot.Guid,
            name = bot.Name,
            activity = bot.CurrentActivity.Type.ToString(),
            activityDuration = bot.CurrentActivity.MinutesInState,
            subPhase = bot.CurrentActivity.SubPhase,
            contextTag = bot.CurrentActivity.ContextTag,
            personality = bot.Personality.ToSummary(),
            tickBase = bot.Personality.DecisionTickBase,
            nextTick = bot.NextDecisionTick,
            copper = bs?.Copper ?? 0,
            freeSlots = bs?.FreeSlots ?? 16,
            totalSlots = bs?.TotalSlots ?? 16,
            inventoryCount = bot.ShadowInventory.Count,
            hasUnlearnedSpells = bot.HasUnlearnedSpells,
            questProgress = bot.CurrentQuestProgress,
            activeQuestId = bot.ActiveQuestId,
            pendingAction = bot.PendingAction != null ? new
            {
                returnTo = bot.PendingAction.ReturnTo.ToString(),
                subPhase = bot.PendingAction.SubPhase,
                questId = bot.PendingAction.QuestId
            } : null
        };
    }

    // ==================== Lifecycle ====================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BotBrain: service started (engine disabled by default — enable from dashboard)");

        // Initialize DB tables
        await _dbInit.InitializeAsync();

        // Load persistent known-good destinations
        await LoadKnownGoodDestinationsAsync();

        // Load quirk tables
        _quirkLoader.Load();

        // Load quest graph from mangos DB
        await _questGraph.LoadAsync();

        // Load zone safety map (creature level grid for path safety checks)
        await _safetyMap.LoadAsync();

        // Load zone data (vendors, innkeepers) from mangos DB
        await _zoneDataLoader.LoadAsync();

        await _spellLoader.LoadAsync();

        // Load group assignments + grouping mode from DB (Session 31)
        await _groupManager.LoadGroupsFromDbAsync();
        await LoadGroupingModeAsync();

        // Wire event routing from bridge → brain (avoids circular DI)
        _bridge.SetBrainService(this);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Check for new bot connections
                await SyncBotRosterAsync();

                // 2. Run decision ticks (only if brain is enabled)
                if (_brainEnabled)
                {
                    await RunDecisionTicksAsync();
                }

                // 3. Flush activity log
                await _activityLog.FlushIfDueAsync();

                // 4. Fleet diagnostics — flush issues to disk, generate health snapshot if due
                _diagnostics.TickDiagnostics(_bots, guid =>
                {
                    var bs = _bridge.GetBotState(guid);
                    if (bs == null) return (BotStateSnapshot?)null;
                    return BotStateSnapshot.FromBridgeState(bs);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BotBrain: main loop error");
            }

            await Task.Delay(250, stoppingToken);
        }
    }

    // ==================== Bot Roster Sync ====================

    /// <summary>
    /// Detect new bot connections from BotBridgeService and initialize BotIdentity + DecisionEngine.
    /// Also detect disconnections and clean up.
    /// </summary>
    private async Task SyncBotRosterAsync()
    {
        var bridgeStates = _bridge.BotStates;

        // New connections
        foreach (var kvp in bridgeStates)
        {
            int guid = kvp.Key;
            var bs = kvp.Value;

            if (bs.TaskState == "DISCONNECTED") continue;

            if (!_initializedGuids.Contains(guid))
            {
                await InitializeBotAsync(guid, bs);
                _initializedGuids.Add(guid);
            }
            else if (_bots.TryGetValue(guid, out var bot))
            {
                // Update live state from bridge
                bot.Level = bs.Level;
                _tracker.UpdatePosition(guid, bs.ZoneId, bs.MapId, bs.X, bs.Y, bs.Z);
            }
        }

        // Detect disconnections — evict after 60s of disconnect
        var disconnected = _initializedGuids
            .Where(g => !bridgeStates.ContainsKey(g) || bridgeStates[g].TaskState == "DISCONNECTED")
            .ToList();

        foreach (var guid in disconnected)
        {
            // Record first-seen disconnect time
            _disconnectedAt.TryAdd(guid, DateTime.UtcNow);

            if (_disconnectedAt.TryGetValue(guid, out var dcTime) &&
                (DateTime.UtcNow - dcTime).TotalSeconds >= 60)
            {
                // Session 31: Remove from group before evicting
                _groupManager.RemoveFromGroup(guid);

                _bots.TryRemove(guid, out _);
                _engines.TryRemove(guid, out _);
                _initializedGuids.Remove(guid);
                _disconnectedAt.TryRemove(guid, out _);
                _tracker.Remove(guid);
                _logger.LogInformation("BotBrain: evicted stale bot {Guid} (disconnected 60s+)", guid);
            }
        }

        // Clear disconnect timer for bots that reconnected
        var reconnected = _disconnectedAt.Keys
            .Where(g => bridgeStates.ContainsKey(g) && bridgeStates[g].TaskState != "DISCONNECTED")
            .ToList();
        foreach (var guid in reconnected)
            _disconnectedAt.TryRemove(guid, out _);
    }

    /// <summary>
    /// Initialize a new bot from HELLO data. Load or roll personality, create DecisionEngine.
    /// </summary>
    private async Task InitializeBotAsync(int guid, BotState bs)
    {
        // Try to load persisted personality from admin DB
        BotPersonality? personality = null;
        try
        {
            using var conn = _db.Admin();
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM bot_personality WHERE bot_guid = @Guid", new { Guid = guid });

            if (row != null)
            {
                personality = new BotPersonality
                {
                    Patience = (float)row.patience,
                    Greed = (float)row.greed,
                    Curiosity = (float)row.curiosity,
                    Sociability = (float)row.sociability,
                    Aggression = (float)row.aggression,
                    Efficiency = (float)row.efficiency,
                    Cautiousness = (float)row.cautiousness,
                    Indecisiveness = (float)row.indecisiveness,
                    Spontaneity = (float)row.spontaneity,
                    ChatStyle = (string)row.chat_style,
                    Temperament = (string)row.temperament,
                    Quirks = _quirkLoader.ResolveQuirkIds((string?)row.quirk_ids)
                };
                _logger.LogInformation("BotBrain: loaded persisted personality for {Name} (guid={Guid})", bs.Name, guid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBrain: failed to load personality for bot {Guid} — will roll new", guid);
        }

        // Roll new personality if not persisted
        if (personality == null)
        {
            personality = PersonalityRoller.Roll(_quirkLoader.AllQuirks.ToList());
            await PersistPersonalityAsync(guid, personality);
            _logger.LogInformation("BotBrain: rolled new personality for {Name} (guid={Guid}): " +
                "Pat={Pat:F2} Gre={Gre:F2} Cur={Cur:F2} Soc={Soc:F2} Agg={Agg:F2} Eff={Eff:F2} " +
                "style={Style} temp={Temp} quirks=[{Quirks}]",
                bs.Name, guid,
                personality.Patience, personality.Greed, personality.Curiosity,
                personality.Sociability, personality.Aggression, personality.Efficiency,
                personality.ChatStyle, personality.Temperament,
                string.Join(",", personality.Quirks.Select(q => q.Id)));
        }

        var bot = new BotIdentity
        {
            Guid = guid,
            Name = bs.Name,
            Race = bs.Race,
            ClassId = bs.ClassId,
            Faction = BotIdentity.FactionForRace(bs.Race),
            Level = bs.Level,
            Personality = personality,
            CurrentActivity = new ActivityState { Type = ActivityType.Idle, StartedAt = DateTime.UtcNow },
            NextDecisionTick = DateTime.UtcNow.AddSeconds(2)
        };

        bot.StuckDetector = new StuckDetector();

        _bots[guid] = bot;

        var domains = new Dictionary<ActivityType, IBotDomain>
        {
            { ActivityType.Questing, _questing },
            { ActivityType.TravelingToQuest, _questing },
            { ActivityType.Grinding, _combat },
            { ActivityType.Vendoring, _economy },
            { ActivityType.TravelingToVendor, _economy },
            { ActivityType.AuctionHouse, _economy },
            { ActivityType.Exploring, _exploration },
            { ActivityType.Socializing, _social },
            { ActivityType.Loitering, _social },
            { ActivityType.TravelingToTrainer, _training },
            { ActivityType.Training, _training },
            { ActivityType.Eating, _maintenance },
            { ActivityType.CorpseRunning, _maintenance },
            { ActivityType.Idle, _questing },
        };

        _engines[guid] = new DecisionEngine(bot, domains, _liveState, _questGraph, _logger);
        _tracker.UpdatePosition(guid, bs.ZoneId, bs.MapId, bs.X, bs.Y, bs.Z);

        // Stamp group membership onto the new bot (from DB-loaded groups)
        _groupManager.EnrichBotIdentity(bot);

        // Auto-register in characters.playerbot for restart persistence
        try
        {
            using var charConn = _db.Characters();
            var existing = await charConn.QueryFirstOrDefaultAsync<int?>(
                "SELECT char_guid FROM playerbot WHERE char_guid = @Guid", new { Guid = guid });

            if (existing == null)
            {
                await charConn.ExecuteAsync(@"
                    INSERT INTO playerbot (char_guid, chance, ai, name, race, `class`, level, map, position_x, position_y, position_z)
                    VALUES (@Guid, 100, 'AiBotAI', @Name, @Race, @Class, @Level, @Map, @X, @Y, @Z)",
                    new
                    {
                        Guid = guid,
                        Name = bs.Name,
                        Race = bs.Race,
                        Class = bs.ClassId,
                        Level = bs.Level,
                        Map = bs.MapId,
                        X = bs.X,
                        Y = bs.Y,
                        Z = bs.Z
                    });
                _logger.LogInformation("BotBrain: auto-registered {Name} (guid={Guid}) in playerbot table", bs.Name, guid);
            }
            else
            {
                // Update position/level on reconnect
                await charConn.ExecuteAsync(@"
                    UPDATE playerbot SET name=@Name, level=@Level, map=@Map,
                           position_x=@X, position_y=@Y, position_z=@Z
                    WHERE char_guid = @Guid",
                    new
                    {
                        Guid = guid,
                        Name = bs.Name,
                        Level = bs.Level,
                        Map = bs.MapId,
                        X = bs.X,
                        Y = bs.Y,
                        Z = bs.Z
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBrain: failed to auto-register bot {Guid} in playerbot table", guid);
        }

        // Hydrate completed quests from character_queststatus
        await HydrateQuestStateAsync(bot);
    }

    // ==================== Decision Loop ====================

    private async Task RunDecisionTicksAsync()
    {
        var now = DateTime.UtcNow;

        // Session 31: Process leaders before followers so quest picks are available
        IEnumerable<int> tickOrder;
        if (_groupManager.Mode != GroupingMode.Off)
        {
            tickOrder = _engines.Keys
                .OrderByDescending(g => _groupManager.IsLeader(g))
                .ThenBy(g => g);
        }
        else
        {
            tickOrder = _engines.Keys;
        }

        foreach (var guid in tickOrder)
        {
            if (!_engines.TryGetValue(guid, out var engine)) continue;

            if (!_bots.TryGetValue(guid, out var bot)) continue;

            // Check if this bot's tick interval has elapsed
            if (now < bot.NextDecisionTick) continue;

            // Get bridge state
            var bs = _bridge.GetBotState(guid);
            if (bs == null || bs.TaskState == "DISCONNECTED") continue;

            // ── GATE: Don't tick until at least one real STATE message has arrived ──
            // HELLO creates BotState with placeholder Health=100/MaxHealth=100 but
            // IsDead, FreeSlots, position etc. may not reflect reality. The first
            // real STATE from C++ arrives ~5s after connect. Without this gate,
            // critical triggers fire on stale/default data and bots roll into
            // CorpseRunning or Eating before they've ever moved.
            if (!bs.HasReceivedState)
            {
                // Push the tick forward so we don't log-spam "skipped" every 250ms
                bot.NextDecisionTick = now.AddSeconds(1);
                continue;
            }

            // ── WATCHDOG: Kick bots stuck in a domain for an unreasonable duration ──
            // If a bot is stuck in CorpseRunning/Eating/any domain far beyond what's
            // normal, force-reset to Idle. This catches initialization bugs, missing
            // events (RESPAWN never arrived), corrupt PhaseData, and any other failure
            // mode that leaves a bot spinning in a domain forever.
            var activity = bot.CurrentActivity;
            float stuckMinutes = (float)activity.MinutesInState;

            bool isStuck = false;
            string stuckReason = "";

            // CorpseRunning: max timer is 45s + 20s timeout = 65s. 2 min is generous.
            if (activity.Type == ActivityType.CorpseRunning && stuckMinutes > 2.0f)
            {
                isStuck = true;
                stuckReason = $"CorpseRunning for {stuckMinutes:F1}min (max expected: ~1.1min)";
            }
            // Eating: eat to 80% HP / 60% MP. Should never take > 2 min even from 1% HP.
            else if (activity.Type == ActivityType.Eating && stuckMinutes > 3.0f)
            {
                isStuck = true;
                stuckReason = $"Eating for {stuckMinutes:F1}min (max expected: ~1min)";
            }
            // Vendoring sub-phases: travel + sell + return should be < 5 min total.
            else if ((activity.Type == ActivityType.Vendoring || activity.Type == ActivityType.TravelingToVendor)
                     && stuckMinutes > 8.0f)
            {
                isStuck = true;
                stuckReason = $"{activity.Type} for {stuckMinutes:F1}min (max expected: ~5min)";
            }
            // Generic: any activity stuck > 20 min with high tick count = something is wrong.
            else if (stuckMinutes > 20.0f && activity.TicksInState > 60)
            {
                isStuck = true;
                stuckReason = $"{activity.Type}:{activity.SubPhase ?? "none"} for {stuckMinutes:F1}min, " +
                              $"{activity.TicksInState} ticks — probable stuck";
            }

            if (isStuck)
            {
                _logger.LogWarning(
                    "[BOT-WATCHDOG] {Name}({Guid}) STUCK: {Reason}. Force-resetting to Idle.",
                    bot.Name, bot.Guid, stuckReason);

                _diagnostics.RecordIssue(DiagnosticIssueType.WatchdogReset, bot,
                    bs.X, bs.Y, bs.MapId,
                    questId: bot.ActiveQuestId,
                    detail: stuckReason);

                // Clear all transient state that could re-trigger the stuck domain
                bot.CurrentActivity = new ActivityState
                {
                    Type = ActivityType.Idle,
                    StartedAt = DateTime.UtcNow,
                    IsInterruptible = true
                };
                bot.NextStrategicEval = DateTime.UtcNow;
                bot.PendingAction = null;
                bot.CorpseX = null;
                bot.CorpseY = null;
                bot.CorpseZ = null;
                bot.CorpseMapId = null;

                // Clear any C++ grind task that might still be running
                await _bridge.SendSetTaskIdleAsync(guid);

                // Emit watchdog event to dashboard
                await _hub.Clients.All.SendAsync("BotDecision", new
                {
                    guid,
                    name = bot.Name,
                    decision = $"[WATCHDOG] {stuckReason} → reset to Idle",
                    newActivity = "Idle",
                    activityChanged = true,
                    weights = new Dictionary<string, double>(),
                    roll = 0.0,
                    timestamp = DateTime.UtcNow
                });

                // Schedule next tick soon so the fresh strategic eval fires quickly
                bot.NextDecisionTick = now.AddSeconds(2);
                continue; // skip this tick, let the next one do a clean strategic eval
            }

            // ── Normal decision tick ──
            try
            {
                var state = BotStateSnapshot.FromBridgeState(bs);

                // Enrich snapshot
                state.XP = bot.XP;
                state.XPToNextLevel = bot.XPToNextLevel;
                state.IsNearTown = BotStateTracker.IsNearTown(state.ZoneId);
                state.NearbyBotCount = _tracker.GetBotsWithinRange(state.X, state.Y, state.MapId, 50f);

                // ── Session 35: Band of Brothers — group state stamped on ALL members ──
                // Every grouped bot is a fully autonomous quester. The group synchronizes
                // via flags stamped here that QuestingDomain reads at its sync gates.
                state.GroupId = bot.GroupId;
                state.GroupLeaderGuid = bot.GroupLeaderGuid;

                if (_groupManager.Mode != GroupingMode.Off && bot.GroupId.HasValue)
                {
                    var group = _groupManager.GetGroup(bot.Guid);
                    if (group != null)
                    {
                        // ── Min member level (used by pace-setter for quest selection) ──
                        int minLevel = bot.Level;
                        foreach (var mg in group.MemberGuids)
                            if (_bots.TryGetValue(mg, out var m))
                                minLevel = Math.Min(minLevel, m.Level);
                        bot.GroupMinMemberLevel = minLevel;

                        // ── All objectives done? (gate: DoingObjectives → TravelingToTurnIn) ──
                        // A member counts as "done" if:
                        //   - Not in Questing at all (vendoring/training/eating — don't block)
                        //   - In Questing but all their quest objectives are complete
                        //   - Disconnected/unknown (don't block the living)
                        bool allObjectivesDone = true;
                        foreach (var mg in group.MemberGuids)
                        {
                            if (!_bots.TryGetValue(mg, out var member)) continue;
                            var mAct = member.CurrentActivity.Type;
                            // Non-questing activities don't block the objective gate
                            if (mAct != ActivityType.Questing) continue;
                            var memberQuests = _questing.GetActiveQuests(member);
                            if (memberQuests.Count == 0) continue;
                            if (!memberQuests.All(q => q.TurnedIn || q.ServerComplete
                                || _questing.AllObjectivesCompletePublic(q)))
                            {
                                allObjectivesDone = false;
                                break;
                            }
                        }
                        bot.GroupAllObjectivesDone = allObjectivesDone;

                        // ── All turned in? (gate: BatchComplete → PickingQuests) ──
                        // A member counts as "turned in" if:
                        //   - Has no active quests with pending turn-ins
                        //   - Is not in Questing (vendoring etc — will turn in when they return)
                        //     BUT: if they're NOT questing, they might still have pending turn-ins
                        //     from before they left. So we check quest state regardless.
                        bool allTurnedIn = true;
                        foreach (var mg in group.MemberGuids)
                        {
                            if (!_bots.TryGetValue(mg, out var member)) continue;
                            var memberQuests = _questing.GetActiveQuests(member);
                            if (memberQuests.Any(q => !q.TurnedIn
                                && (q.ServerComplete || _questing.AllObjectivesCompletePublic(q))))
                            {
                                allTurnedIn = false;
                                break;
                            }
                        }
                        bot.GroupAllMembersTurnedIn = allTurnedIn;

                        // ── All members questing? (soft gate: don't advance while someone is away) ──
                        bool allQuesting = true;
                        foreach (var mg in group.MemberGuids)
                        {
                            if (!_bots.TryGetValue(mg, out var member)) continue;
                            var mAct = member.CurrentActivity.Type;
                            if (mAct != ActivityType.Questing && mAct != ActivityType.Idle)
                            {
                                allQuesting = false;
                                break;
                            }
                        }
                        bot.GroupAllMembersQuesting = allQuesting;
                    }
                }

                // Run decision tick
                var result = await engine.Tick(bot, state);

                // ── DIAGNOSTIC: One-line bot status per tick ──
                var subPhase = bot.CurrentActivity.SubPhase ?? "none";
                var ctx = bot.CurrentActivity.ContextTag ?? "";
                var nextStrat = bot.NextStrategicEval > DateTime.UtcNow
                    ? $"{(bot.NextStrategicEval - DateTime.UtcNow).TotalSeconds:F0}s"
                    : "NOW";
                _logger.LogInformation(
                    "[BOT] {Name}({Guid}) | {Activity}:{SubPhase} | {Context} | " +
                    "Pos=({X:F0},{Y:F0}) HP={HP:P0} | Tick#{Tick} | NextStrat={NextStrat} | " +
                    "Cmds={CmdCount} | {Reason}",
                    bot.Name, bot.Guid,
                    bot.CurrentActivity.Type, subPhase, ctx,
                    state.X, state.Y, state.HealthPercent,
                    bot.CurrentActivity.TicksInState, nextStrat,
                    result.Commands.Count, result.Reason);

                // If transitioning AWAY from an activity that may have a C++ grind task
                // running, clear it. Both Grinding and Questing (DoingObjective sub-phase)
                // use SET_TASK GRIND — if we don't clear it, the bot keeps killing autonomously
                // even after C# switched to Vendoring/Training/etc.
                if (result.ActivityChanged)
                {
                    var prevType = bot.PreviousActivity?.Type;
                    var prevSubPhase = bot.PreviousActivity?.SubPhase ?? "";

                    bool hadGrindTask = prevType == ActivityType.Grinding
                        || (prevType == ActivityType.Questing && (prevSubPhase == "DoingObjective" || prevSubPhase == "DoingObjectives"));

                    if (hadGrindTask && result.NewActivity != ActivityType.Grinding)
                    {
                        await _bridge.SendSetTaskIdleAsync(guid);
                    }
                }

                // Execute resulting commands via bridge
                foreach (var cmd in result.Commands)
                {
                    await _bridge.SendToBotAsync(guid, cmd.Type, cmd.Payload);
                }

                // Log decision
                _activityLog.LogDecision(guid, result);
                if (result.ActivityChanged)
                {
                    _activityLog.LogActivityEnd(guid, bot.PreviousActivity!);
                }

                // Emit to dashboard via SignalR
                await _hub.Clients.All.SendAsync("BotDecision", new
                {
                    guid,
                    name = bot.Name,
                    decision = result.Reason,
                    newActivity = result.NewActivity.ToString(),
                    activityChanged = result.ActivityChanged,
                    weights = result.WeightBreakdown.ToDictionary(
                        kv => kv.Key.ToString(),
                        kv => Math.Round(kv.Value, 3)),
                    roll = Math.Round(result.RollValue, 3),
                    timestamp = DateTime.UtcNow
                });

                // Schedule next tick (with per-bot jitter)
                float tickInterval = bot.Personality.DecisionTickBase;
                tickInterval *= WeightedRoller.Range(0.8f, 1.2f);
                bot.NextDecisionTick = now.AddSeconds(tickInterval);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BotBrain: decision tick failed for bot {Guid} ({Name})", guid, bot.Name);
                bot.NextDecisionTick = now.AddSeconds(5); // retry after 5s
            }
        }
    }

    // ==================== Event Handling ====================

    /// <summary>
    /// Called externally when BotBridgeService receives an EVENT from C++.
    /// Routes to appropriate domain handlers.
    /// </summary>
    public async Task HandleBridgeEventAsync(int guid, BotEvent evt)
    {
        if (!_bots.TryGetValue(guid, out var bot)) return;
        if (!_engines.TryGetValue(guid, out var engine)) return;

        // KILL events always go to economy (shadow loot) regardless of activity
        if (evt.EventType == "KILL")
        {
            // Only run probabilistic shadow loot if we DON'T have real loot coming.
            // The LOOT event (from C++ DoAutoLoot) will arrive shortly after KILL
            // with actual items. If C++ looting is active, skip the shadow roll.
            // For now, keep shadow loot as fallback — it will be superseded by
            // the LOOT event's ProcessLootEvent call which replaces shadow items
            // from the same creature.
            int estLevel = Math.Max(1, bot.Level + WeightedRoller.RangeInt(-2, 1));
            _economy.ProcessKillLoot(bot, evt.CreatureEntry, estLevel);
        }

        // LOOT — real items looted from C++ DoAutoLoot (replaces shadow economy guessing)
        if (evt.EventType == "LOOT" && !string.IsNullOrEmpty(evt.Data))
        {
            // ProcessLootEvent handles both QuestItemProgress updates and shadow inventory
            _economy.ProcessLootEvent(bot, evt.Data);
            bot.VendorCooldownUntil = null;
        }

        // LEVEL_UP — flag for training, clear deferrals/blacklists (bot is stronger now)
        if (evt.EventType == "LEVEL_UP")
        {
            bot.StuckDetector?.Reset();
            bot.Level = evt.NewLevel;
            bot.HasUnlearnedSpells = true;
            bot.ClearAllDeferrals();
            bot.ClearPathBlacklist();
            bot.VendorCooldownUntil = null;
            _liveState.InvalidateCache(guid);
        }

        // PATH_UNSAFE — C++ rejected a MOVE_TO because the mmap path crossed
        // through high-level creature spawns. Blacklist the destination so C# stops
        // trying it. Format: "dest_x=1234.5|dest_y=-5678.9|dest_z=100.0|unsafe_x=...|danger_level=25|bot_level=5"
        if (evt.EventType == "PATH_UNSAFE" && !string.IsNullOrEmpty(evt.Data))
        {
            var parts = evt.Data.Split('|')
                .Select(s => s.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

            float destX = 0, destY = 0, destZ = 0;
            float unsafeX = 0, unsafeY = 0;
            int dangerLevel = 0, botLevel = 0;

            if (parts.TryGetValue("dest_x", out var dxs))
                float.TryParse(dxs, System.Globalization.CultureInfo.InvariantCulture, out destX);
            if (parts.TryGetValue("dest_y", out var dys))
                float.TryParse(dys, System.Globalization.CultureInfo.InvariantCulture, out destY);
            if (parts.TryGetValue("dest_z", out var dzs))
                float.TryParse(dzs, System.Globalization.CultureInfo.InvariantCulture, out destZ);
            if (parts.TryGetValue("unsafe_x", out var uxs))
                float.TryParse(uxs, System.Globalization.CultureInfo.InvariantCulture, out unsafeX);
            if (parts.TryGetValue("unsafe_y", out var uys))
                float.TryParse(uys, System.Globalization.CultureInfo.InvariantCulture, out unsafeY);
            if (parts.TryGetValue("danger_level", out var dls))
                int.TryParse(dls, out dangerLevel);
            if (parts.TryGetValue("bot_level", out var bls))
                int.TryParse(bls, out botLevel);

            bot.AddPathBlacklist(destX, destY, dangerLevel);

            _logger.LogWarning(
                "[BOT-PATH] {Name}({Guid}) PATH_UNSAFE — dest=({DestX:F0},{DestY:F0},{DestZ:F0}) " +
                "danger at ({UnsafeX:F0},{UnsafeY:F0}) level={DangerLevel} (bot level={BotLevel}) | " +
                "Blacklisted ({Count} total)",
                bot.Name, bot.Guid, destX, destY, destZ,
                unsafeX, unsafeY, dangerLevel, botLevel,
                bot.PathBlacklist.Count);

            _diagnostics.RecordIssue(DiagnosticIssueType.PathUnsafe, bot,
                destX, destY, 0,
                detail: $"danger_level={dangerLevel} at ({unsafeX:F0},{unsafeY:F0}), blacklist={bot.PathBlacklist.Count}");

            // Flag if bot is getting boxed in
            if (bot.PathBlacklist.Count >= 10)
                _diagnostics.RecordIssue(DiagnosticIssueType.PathBlacklistFull, bot,
                    destX, destY, 0,
                    detail: $"{bot.PathBlacklist.Count} blacklisted destinations — bot may be boxed in");
        }

        // MOVE_FAILED — C++ could not pathfind to destination (PATHFIND_NOPATH/INCOMPLETE).
        // Without this handler, bots stuck in a nudge loop re-sending MOVE_TO to the same
        // unreachable destination until the 8-min watchdog fires. Session 26 fix.
        // Format: "dest_x=1234.5|dest_y=-5678.9|dest_z=100.0"
        if (evt.EventType == "MOVE_FAILED")
        {
            float destX = 0, destY = 0, destZ = 0;

            if (!string.IsNullOrEmpty(evt.Data))
            {
                var parts = evt.Data.Split('|')
                    .Select(s => s.Split('=', 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

                if (parts.TryGetValue("dest_x", out var dxs))
                    float.TryParse(dxs, System.Globalization.CultureInfo.InvariantCulture, out destX);
                if (parts.TryGetValue("dest_y", out var dys))
                    float.TryParse(dys, System.Globalization.CultureInfo.InvariantCulture, out destY);
                if (parts.TryGetValue("dest_z", out var dzs))
                    float.TryParse(dzs, System.Globalization.CultureInfo.InvariantCulture, out destZ);
            }

            // Blacklist the unreachable destination — UNLESS another bot has
            // successfully reached it (fleet-level shared knowledge).
            if (IsKnownGoodDestination(destX, destY))
            {
                _logger.LogInformation(
                    "[BOT-PATH] {Name}({Guid}) MOVE_FAILED to KNOWN-GOOD dest ({DestX:F0},{DestY:F0}) — " +
                    "NOT blacklisting, will retry from different angle",
                    bot.Name, bot.Guid, destX, destY);
            }
            else
            {
                bot.AddPathBlacklist(destX, destY, 0);

                _logger.LogWarning(
                    "[BOT-PATH] {Name}({Guid}) MOVE_FAILED — dest=({DestX:F0},{DestY:F0},{DestZ:F0}) | " +
                    "Blacklisted ({Count} total). Bot was in {Activity}:{SubPhase}",
                    bot.Name, bot.Guid, destX, destY, destZ,
                    bot.PathBlacklist.Count,
                    bot.CurrentActivity.Type, bot.CurrentActivity.SubPhase);

                _diagnostics.RecordIssue(DiagnosticIssueType.PathUnsafe, bot,
                    destX, destY, 0,
                    detail: $"MOVE_FAILED (unreachable destination), blacklist={bot.PathBlacklist.Count}, " +
                            $"activity={bot.CurrentActivity.Type}:{bot.CurrentActivity.SubPhase}");
            }
        }

        if (evt.EventType == "TRAIN_ACK")
        {
            _logger.LogInformation("[BOT-TRAIN] {Name}({Guid}) TRAIN_ACK: {Data}",
            bot.Name, bot.Guid, evt.Data);

            // Clear the training pressure — bot visited the trainer, 
            // whatever was learnable has been learned. If the bot levels
            // again, LEVEL_UP will re-set HasUnlearnedSpells to true.
            bot.HasUnlearnedSpells = false;
            bot.TicksSinceLastTrained = 0;
        }
        if (evt.EventType == "TRAIN_FAIL")
        {
            _logger.LogWarning("[BOT-TRAIN] {Name}({Guid}) TRAIN_FAIL: {Data}",
                bot.Name, bot.Guid, evt.Data);

            // Reset training pressure — don't keep retrying a trainer
            // that can't be found. Next LEVEL_UP will re-trigger.
            bot.HasUnlearnedSpells = false;
            bot.TicksSinceLastTrained = 0;
        }

        // Fleet-level known-good destinations: when ANY bot successfully interacts with
        // an NPC or GO, mark that location so other bots don't blacklist it on MOVE_FAILED.
        if (evt.EventType is "TRAIN_ACK" or "QUEST_ACCEPT_ACK" or "QUEST_COMPLETE_ACK"
            or "SELL_ACK" or "USE_GO_ACK")
        {
            var successState = _bridge.GetBotState(guid);
            if (successState != null)
            {
                _ = MarkDestinationGoodAsync(successState.X, successState.Y,
                    successState.MapId, $"{evt.EventType}:{bot.Name}");
            }
        }


        // DEATH — store corpse position on BotIdentity (survives domain switch to CorpseRunning).
        // C++ now sends: DEATH "x=123.4|y=456.7|z=78.9|map=0"
        // MaintenanceDomain.OnEnter reads bot.CorpseX/Y/Z/CorpseMapId to send MOVE_TO corpse.
        if (evt.EventType == "DEATH" && !string.IsNullOrEmpty(evt.Data))
        {
            var parts = evt.Data.Split('|')
                .Select(s => s.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

            if (parts.TryGetValue("x", out var xs) && float.TryParse(xs, System.Globalization.CultureInfo.InvariantCulture, out float dx))
                bot.CorpseX = dx;
            if (parts.TryGetValue("y", out var ys) && float.TryParse(ys, System.Globalization.CultureInfo.InvariantCulture, out float dy))
                bot.CorpseY = dy;
            if (parts.TryGetValue("z", out var zs) && float.TryParse(zs, System.Globalization.CultureInfo.InvariantCulture, out float dz))
                bot.CorpseZ = dz;
            if (parts.TryGetValue("map", out var ms) && int.TryParse(ms, out int dm))
                bot.CorpseMapId = dm;

            _logger.LogInformation(
                "BotBrain: {Name} DEATH — corpse at ({X:F1},{Y:F1},{Z:F1}) map={Map}",
                bot.Name, bot.CorpseX, bot.CorpseY, bot.CorpseZ, bot.CorpseMapId);

            var issueType = bot.DeathsSinceQuestStart >= 3
                ? DiagnosticIssueType.DeathLoop
                : DiagnosticIssueType.Death;
            _diagnostics.RecordIssue(issueType, bot,
                bot.CorpseX ?? 0, bot.CorpseY ?? 0, bot.CorpseMapId ?? 0,
                questId: bot.ActiveQuestId,
                detail: $"deaths_since_quest_start={bot.DeathsSinceQuestStart}, " +
                        $"activity={bot.CurrentActivity.Type}:{bot.CurrentActivity.SubPhase}");
        }

        // CHAT_RECV — generate LLM response via Ollama
        if (evt.EventType == "CHAT_RECV" && !string.IsNullOrEmpty(evt.Message))
        {
            // Don't reply to other bots — only real players
            bool senderIsBot = _bots.Values.Any(b => b.Name == evt.Sender);
            if (senderIsBot)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var reply = await _ollama.GetChatResponseAsync(
                        bot.Name,
                        ClassIdToName(bot.ClassId),
                        bot.Level,
                        bot.Personality.Temperament,
                        bot.Personality.ChatStyle,
                        evt.Sender,
                        evt.Message);

                    if (!string.IsNullOrEmpty(reply))
                    {
                        // Whisper back if whispered, channel if channel, say otherwise
                        if (evt.ChatType == "whisper")
                        {
                            await _bridge.SendSayTextAsync(guid, reply, 7, evt.Sender);
                        }
                        else if (evt.ChatType == "channel" && !string.IsNullOrEmpty(evt.ChannelName))
                        {
                            await _bridge.SendSayTextAsync(guid, reply, 14, channel: evt.ChannelName);
                        }
                        else
                        {
                            await _bridge.SendSayTextAsync(guid, reply, 0);
                        }

                        _logger.LogInformation("BotBrain: {BotName} replied to {Sender} via Ollama: \"{Reply}\"",
                            bot.Name, evt.Sender, reply);
                    }
                    else
                    {
                        _logger.LogWarning("BotBrain: Ollama returned no reply for {BotName}", bot.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BotBrain: Ollama chat error for {BotName}", bot.Name);
                }
            });
        }

        // Route to current domain
        var bs = _bridge.GetBotState(guid);
        if (bs != null)
        {
            var state = BotStateSnapshot.FromBridgeState(bs);
            var commands = engine.CurrentDomain.OnEvent(bot, state, evt);
            foreach (var cmd in commands)
                await _bridge.SendToBotAsync(guid, cmd.Type, cmd.Payload);
        }
    }

    // ==================== Helpers ====================

    private static string ClassIdToName(int classId) => classId switch
    {
        1 => "Warrior",
        2 => "Paladin",
        3 => "Hunter",
        4 => "Rogue",
        5 => "Priest",
        7 => "Shaman",
        8 => "Mage",
        9 => "Warlock",
        11 => "Druid",
        _ => "Adventurer"
    };

    /// <summary>
    /// On bot connect, query character_queststatus to populate CompletedQuestIds
    /// and HydratedActiveQuests. CompletedQuestIds only gets rewarded=1 quests.
    /// HydratedActiveQuests gets in-progress quests (rewarded=0) with their
    /// mob_count/item_count progress so QuestingDomain can verify objectives
    /// are truly done before marking ServerComplete.
    ///
    /// VMaNGOS character_queststatus.status values:
    ///   0 = NONE (quest available but not accepted)
    ///   1 = INCOMPLETE (accepted, objectives not done)
    ///   3 = COMPLETE (objectives done, ready to turn in)
    ///   6 = FAILED
    /// rewarded: 0 = not turned in, 1 = turned in and reward given
    /// </summary>
    private async Task HydrateQuestStateAsync(BotIdentity bot)
    {
        try
        {
            using var conn = _db.Characters();
            var rows = await conn.QueryAsync<dynamic>(
                @"SELECT quest, status, rewarded,
                         mob_count1, mob_count2, mob_count3, mob_count4,
                         item_count1, item_count2, item_count3, item_count4
                  FROM character_queststatus WHERE guid = @Guid",
                new { Guid = bot.Guid });

            int completed = 0;
            int active = 0;
            int skippedDupes = 0;
            var activeQuests = new List<HydratedQuest>();
            var seenQuestIds = new HashSet<int>();

            // Pass 1: collect all rewarded quest IDs first.
            // DB can have duplicate rows per quest (observed: quest 182 x2).
            // A quest with row A (rewarded=1) and row B (rewarded=0) must NOT
            // end up in the active list — the rewarded row is authoritative.
            foreach (var row in rows)
            {
                int questId = (int)(uint)row.quest;
                int rewarded = (int)(byte)row.rewarded;
                if (rewarded == 1)
                {
                    bot.CompletedQuestIds.Add(questId);
                    completed++;
                }
            }

            // Pass 2: collect active quests, skipping anything already rewarded or duplicate
            foreach (var row in rows)
            {
                int questId = (int)(uint)row.quest;
                int status = (int)(uint)row.status;
                int rewarded = (int)(byte)row.rewarded;

                if (rewarded == 1) continue;                          // already handled in pass 1
                if (!seenQuestIds.Add(questId)) { skippedDupes++; continue; } // duplicate row
                if (bot.CompletedQuestIds.Contains(questId)) continue;        // rewarded via another row

                if (status == 1 || status == 3)
                {
                    if (_questGraph.IsLoaded && _questGraph.GetQuest(questId) != null)
                    {
                        activeQuests.Add(new HydratedQuest
                        {
                            QuestId = questId,
                            Status = status,
                            MobCounts = new[]
                            {
                                (int)(uint)row.mob_count1, (int)(uint)row.mob_count2,
                                (int)(uint)row.mob_count3, (int)(uint)row.mob_count4
                            },
                            ItemCounts = new[]
                            {
                                (int)(uint)row.item_count1, (int)(uint)row.item_count2,
                                (int)(uint)row.item_count3, (int)(uint)row.item_count4
                            }
                        });
                        active++;
                    }
                }
            }

            if (activeQuests.Count > 0)
                bot.HydratedActiveQuests = activeQuests;

            _logger.LogInformation(
                "BotBrain: hydrated quest state for {Name} (guid={Guid}) — {Completed} completed, {Active} active" +
                (skippedDupes > 0 ? ", {Dupes} duplicate rows skipped" : ""),
                bot.Name, bot.Guid, completed, active, skippedDupes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBrain: failed to hydrate quest state for bot {Guid}", bot.Guid);
        }
    }

    // ==================== Persistence ====================

    private async Task PersistPersonalityAsync(int guid, BotPersonality p)
    {
        try
        {
            using var conn = _db.Admin();
            await conn.ExecuteAsync(@"
                INSERT INTO bot_personality
                    (bot_guid, patience, greed, curiosity, sociability, aggression, efficiency,
                     cautiousness, indecisiveness, spontaneity, chat_style, temperament, quirk_ids)
                VALUES
                    (@Guid, @Patience, @Greed, @Curiosity, @Sociability, @Aggression, @Efficiency,
                     @Cautiousness, @Indecisiveness, @Spontaneity, @ChatStyle, @Temperament, @QuirkIds)
                ON DUPLICATE KEY UPDATE
                    patience=@Patience, greed=@Greed, curiosity=@Curiosity,
                    sociability=@Sociability, aggression=@Aggression, efficiency=@Efficiency,
                    cautiousness=@Cautiousness, indecisiveness=@Indecisiveness,
                    spontaneity=@Spontaneity, chat_style=@ChatStyle, temperament=@Temperament,
                    quirk_ids=@QuirkIds",
                new
                {
                    Guid = guid,
                    p.Patience,
                    p.Greed,
                    p.Curiosity,
                    p.Sociability,
                    p.Aggression,
                    p.Efficiency,
                    p.Cautiousness,
                    p.Indecisiveness,
                    p.Spontaneity,
                    p.ChatStyle,
                    p.Temperament,
                    QuirkIds = string.Join(",", p.Quirks.Select(q => q.Id))
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBrain: failed to persist personality for bot {Guid}", guid);
        }
    }
}