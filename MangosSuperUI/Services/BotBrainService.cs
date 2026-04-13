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

    // Per-bot instances
    private readonly ConcurrentDictionary<int, DecisionEngine> _engines = new();
    private readonly ConcurrentDictionary<int, BotIdentity> _bots = new();

    // Domain engines (singleton, stateless — per-bot state is in BotIdentity)
    private readonly QuestingDomain _questing = new();
    private readonly CombatDomain _combat = new();
    private readonly EconomyDomain _economy = new();
    private readonly SocialDomain _social = new();
    private readonly ExplorationDomain _exploration = new();
    private readonly TrainingDomain _training = new();
    private readonly MaintenanceDomain _maintenance = new();
    private readonly LiveStateModifiers _liveState;
    private readonly BotBrainDbInit _dbInit;

    // Track which bot GUIDs we've already initialized (to detect new connections)
    private readonly HashSet<int> _initializedGuids = new();

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
        OllamaChatService ollama)
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
    }

    // ==================== Public API (for controller/hub) ====================

    public bool BrainEnabled
    {
        get => _brainEnabled;
        set
        {
            _brainEnabled = value;
            _logger.LogInformation("BotBrain: engine {State}", value ? "ENABLED" : "DISABLED");
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
            copper = bot.CopperBalance,
            inventoryCount = bot.ShadowInventory.Count,
            hasUnlearnedSpells = bot.HasUnlearnedSpells,
            questProgress = bot.CurrentQuestProgress,
            activeQuestId = bot.ActiveQuestId
        };
    }

    // ==================== Lifecycle ====================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BotBrain: service started (engine disabled by default — enable from dashboard)");

        // Initialize DB tables
        await _dbInit.InitializeAsync();

        // Load quirk tables
        _quirkLoader.Load();

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

        // Detect disconnections
        var disconnected = _initializedGuids
            .Where(g => !bridgeStates.ContainsKey(g) || bridgeStates[g].TaskState == "DISCONNECTED")
            .ToList();

        foreach (var guid in disconnected)
        {
            _tracker.Remove(guid);
            // Keep BotIdentity and engine alive for reconnection
        }
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

        _engines[guid] = new DecisionEngine(bot, domains, _liveState);
        _tracker.UpdatePosition(guid, bs.ZoneId, bs.MapId, bs.X, bs.Y, bs.Z);

        // Auto-register in characters.playerbot for restart persistence
        try
        {
            using var charConn = _db.Characters();
            var existing = await charConn.QueryFirstOrDefaultAsync<int?>(
                "SELECT char_guid FROM playerbot WHERE char_guid = @Guid", new { Guid = guid });

            if (existing == null)
            {
                await charConn.ExecuteAsync(@"
                    INSERT INTO playerbot (char_guid, chance, ai, name, race, class, level, map, position_x, position_y, position_z)
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
    }

    // ==================== Decision Loop ====================

    private async Task RunDecisionTicksAsync()
    {
        var now = DateTime.UtcNow;

        foreach (var kvp in _engines)
        {
            int guid = kvp.Key;
            var engine = kvp.Value;

            if (!_bots.TryGetValue(guid, out var bot)) continue;

            // Check if this bot's tick interval has elapsed
            if (now < bot.NextDecisionTick) continue;

            // Get bridge state
            var bs = _bridge.GetBotState(guid);
            if (bs == null || bs.TaskState == "DISCONNECTED") continue;

            try
            {
                var state = BotStateSnapshot.FromBridgeState(bs);

                // Enrich snapshot
                state.XP = bot.XP;
                state.XPToNextLevel = bot.XPToNextLevel;
                state.IsNearTown = BotStateTracker.IsNearTown(state.ZoneId);
                state.NearbyBotCount = _tracker.GetBotsWithinRange(state.X, state.Y, state.MapId, 50f);

                // Check for events since last tick
                // (Events are handled reactively via HandleBridgeEvent below)

                // Run decision tick
                var result = await engine.Tick(bot, state);

                // If transitioning AWAY from Grinding, clear the C++ grind task
                if (result.ActivityChanged &&
                    bot.PreviousActivity?.Type == ActivityType.Grinding &&
                    result.NewActivity != ActivityType.Grinding)
                {
                    await _bridge.SendSetTaskIdleAsync(guid);
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
            // Estimate creature level from bot level (±2)
            int estLevel = Math.Max(1, bot.Level + WeightedRoller.RangeInt(-2, 1));
            _economy.ProcessKillLoot(bot, evt.CreatureEntry, estLevel);
        }

        // LEVEL_UP — flag for training
        if (evt.EventType == "LEVEL_UP")
        {
            bot.Level = evt.NewLevel;
            bot.HasUnlearnedSpells = true;
            _liveState.InvalidateCache(guid);
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