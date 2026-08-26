using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using MangosSuperUI.Services;
using MangosSuperUI.Models;
using MangosSuperUI.BotLogic.Tracking;
using Dapper;
using System.Text.RegularExpressions;

namespace MangosSuperUI.Controllers;

public partial class BotsController : Controller
{
    private readonly BotBridgeService _bridge;
    private readonly BotBrainService _brain;
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly BotFlightRecorder _recorder;
    private readonly BotLogBuffer _log;
    private readonly RaService _ra;
    private readonly BotSpawnService _spawner;
    private readonly BotTalentVisibilityService _talents;
    private readonly BotSpellbookVisibilityService _spellbook;
    private readonly BotCombatLoadoutService _combatLoadouts;
    private readonly BotCombatLoadoutQueueService _combatLoadoutQueue;

    public BotsController(
        BotBridgeService bridge,
        BotBrainService brain,
        ConnectionFactory db,
        DbcService dbc,
        BotFlightRecorder recorder,
        BotLogBuffer log,
        RaService ra,
        BotSpawnService spawner,
        BotTalentVisibilityService talents,
        BotSpellbookVisibilityService spellbook,
        BotCombatLoadoutService combatLoadouts,
        BotCombatLoadoutQueueService combatLoadoutQueue)
    {
        _bridge = bridge;
        _brain = brain;
        _db = db;
        _dbc = dbc;
        _recorder = recorder;
        _log = log;
        _ra = ra;
        _spawner = spawner;
        _talents = talents;
        _spellbook = spellbook;
        _combatLoadouts = combatLoadouts;
        _combatLoadoutQueue = combatLoadoutQueue;
    }

    public IActionResult Index()
    {
        return View();
    }

    // ==================== Add bots (RA console: .bot addai <class> <race> <name>) ====================
    // Quick-spawn helper for reruns. The Bot Monitor "Add Bots" modal POSTs per-(race, class)
    // counts; we expand them here and hand the batch to BotSpawnService, which draws unique names
    // and fires `.bot addai <class> <race> <name>` once per bot over RA (serialized inside
    // RaService) on a background task, streaming SpawnProgress over the BotBridge hub.
    // Classes are STRICTLY whitelisted — these tokens are concatenated into a live console
    // command, so an unknown token is rejected up front rather than passed through.
    private static readonly HashSet<string> _addBotClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "warrior", "paladin", "hunter", "rogue", "priest", "mage", "warlock", "druid"
    };

    // Valid 1.12 race/class combinations. The C++ GetPlayerInfo(race,class) gate is the final
    // backstop, but we reject bad combos here so nothing illegal is ever sent over RA.
    // Race tokens MUST match ResolveBotRaceToken() in PlayerBotMgr.cpp.
    private static readonly Dictionary<string, HashSet<string>> _raceClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["human"] = new(StringComparer.OrdinalIgnoreCase) { "warrior", "paladin", "rogue", "priest", "mage", "warlock" },
        ["dwarf"] = new(StringComparer.OrdinalIgnoreCase) { "warrior", "paladin", "hunter", "rogue", "priest" },
        ["nightelf"] = new(StringComparer.OrdinalIgnoreCase) { "warrior", "hunter", "rogue", "priest", "druid" },
        ["gnome"] = new(StringComparer.OrdinalIgnoreCase) { "warrior", "rogue", "mage", "warlock" },
        ["orc"] = new(StringComparer.OrdinalIgnoreCase) { "warrior", "hunter", "rogue", "shaman", "warlock" },
        ["undead"] = new(StringComparer.OrdinalIgnoreCase) { "warrior", "rogue", "priest", "mage", "warlock" },
        ["tauren"] = new(StringComparer.OrdinalIgnoreCase) { "warrior", "hunter", "shaman", "druid" },
        ["troll"] = new(StringComparer.OrdinalIgnoreCase) { "warrior", "hunter", "rogue", "priest", "mage", "shaman" },
    };

    // Default race per class for the legacy class-only path (mirrors PlayerBotMgr.cpp defaults).
    private static readonly Dictionary<string, string> _classDefaultRace = new(StringComparer.OrdinalIgnoreCase)
    {
        ["warrior"] = "human",
        ["paladin"] = "human",
        ["hunter"] = "nightelf",
        ["rogue"] = "human",
        ["priest"] = "human",
        ["mage"] = "gnome",
        ["warlock"] = "human",
        ["druid"] = "nightelf",
    };

    [HttpPost]
    public async Task<IActionResult> AddBots([FromBody] AddBotsRequest req)
    {
        // Expand the request into a flat (race, class) spawn list.
        // Preferred shape: Spawns = [{ race, cls, count }]. Legacy shape: Classes = ["mage", ...]
        // (class-only, default race per class) is still accepted so old callers don't break.
        var spawns = new List<BotSpawnService.SpawnRequest>();

        if (req?.Spawns != null && req.Spawns.Length > 0)
        {
            foreach (var s in req.Spawns)
            {
                var race = (s.Race ?? "").Trim().ToLowerInvariant();
                var cls = (s.Cls ?? "").Trim().ToLowerInvariant();
                if (s.Count <= 0) continue;
                if (!_raceClasses.TryGetValue(race, out var validClasses) || !validClasses.Contains(cls))
                    return Json(new { success = false, error = $"Invalid race/class combination: {race} {cls}" });
                for (int i = 0; i < s.Count; i++) spawns.Add(new(race, cls));
            }
        }
        else if (req?.Classes != null && req.Classes.Length > 0)
        {
            foreach (var raw in req.Classes)
            {
                var cls = (raw ?? "").Trim().ToLowerInvariant();
                if (!_addBotClasses.Contains(cls))
                    return Json(new { success = false, error = "Unknown class: " + cls });
                spawns.Add(new(_classDefaultRace[cls], cls));
            }
        }

        if (spawns.Count == 0)
            return Json(new { success = false, error = "Nothing to spawn" });

        // The per-batch ceiling (BotSpawn:MaxPerRequest) and the unused-name pool are checked in
        // StartAsync. The batch itself runs in the background: poll /Bots/AddBotsStatus or listen
        // for SpawnProgress on /hubs/botbridge for its progress.
        var (job, error) = await _spawner.StartAsync(spawns);
        if (error != null)
            return Json(new { success = false, error, job });

        return Json(new { success = true, job, requested = spawns.Count });
    }

    // What the Add Bots modal shows up front so the UI and the server can't disagree: the configured
    // per-batch ceiling, how many unused names are left (the real lifetime ceiling), how many bots the
    // bridge currently sees, and mangosd's PlayerLimit (bot sessions count against it — once bots +
    // players exceed it, non-GM players land in the login queue). Also returns the current/last batch.
    [HttpGet]
    public async Task<IActionResult> AddBotsLimits()
    {
        int? namesAvailable = null;
        string? namesError = null;
        try
        {
            namesAvailable = (await _spawner.LoadAvailableNamesAsync()).Count;
        }
        catch (Exception ex)
        {
            namesError = ex.Message;
        }

        return Json(new
        {
            maxPerRequest = _spawner.MaxPerRequest,
            namesAvailable,
            namesError,
            botsOnline = _bridge.ConnectedCount,
            playerLimit = await _spawner.ReadPlayerLimitAsync(),
            job = _spawner.Snapshot()
        });
    }

    // Polling fallback for the SpawnProgress hub event: the running batch, or the last finished one.
    [HttpGet]
    public IActionResult AddBotsStatus()
    {
        return Json(new { job = _spawner.Snapshot() });
    }

    // Stops the running batch after its in-flight command completes.
    [HttpPost]
    public IActionResult AddBotsCancel()
    {
        return Json(new { success = _spawner.Cancel(), job = _spawner.Snapshot() });
    }

    // ==================== Load all persisted SuperUI bots (RA console: .bot add_all) ====================
    // After a server reset the SuperUI bots still live in the perm DB but aren't in the world.
    // `.bot add_all` re-adds every persisted bot in one shot. This is the same command the server
    // admin console's "Add All" button fires — exposed here so it can be triggered straight from the
    // IBot Monitor without opening the console. Fires over RA (serialized inside RaService).
    [HttpPost]
    public async Task<IActionResult> AddAll()
    {
        try
        {
            var response = await _ra.SendCommandAsync(".bot add_all");
            return Json(new { success = true, response });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ==================== Connect single bot ====================
    [HttpPost]
    public async Task<IActionResult> Connect([FromBody] BotNameRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Json(new { success = false, error = "Missing bot name" });

        var response = await _ra.SendCommandAsync($".bot add {req.Name}");

        return Json(new
        {
            success = true,
            response
        });
    }

    // ==================== REST API ====================

    [HttpGet]
    public IActionResult States()
    {
        var bots = _bridge.GetAllBotStates();
        return Json(new
        {
            connected = _bridge.ConnectedCount,
            totalTracked = _bridge.TotalTracked,
            bots,
            // Per-bot group membership for the dashboards' visual grouping (Map cohesion overlay,
            // Fleet leaderboard badge, roster badge). Group identity lives on BotIdentity in the brain,
            // not on the bridge BotState, so it's joined here by guid. Non-breaking: the existing `bots`
            // array is untouched — consumers that don't need groups just ignore this.
            groups = _brain.AllBots.Values
                .Where(b => b.GroupId > 0)
                .Select(b => new { guid = b.Guid, groupId = b.GroupId, isGroupLeader = b.IsGroupLeader })
        });
    }

    [HttpGet]
    public IActionResult State(int id)
    {
        var state = _bridge.GetBotState(id);
        if (state == null)
            return NotFound(new { error = $"Bot {id} not found" });
        return Json(state);
    }

    /// <summary>
    /// Authoritative talent visibility for one character. The selected profile and
    /// active role come from characters.playerbot; learned ranks come only from exact
    /// character_spell -> Talent.dbc RankID matches. The endpoint is deliberately
    /// lazy/per-bot so opening the cockpit never creates a fleet-sized query storm.
    /// </summary>
    [HttpGet("Bots/Talents/{guid:int}")]
    public async Task<IActionResult> Talents(int guid)
    {
        var result = await _talents.GetAsync(guid, HttpContext.RequestAborted);
        return result.ErrorCode == "not_found" ? NotFound(result) : Json(result);
    }

    /// <summary>
    /// Authoritative spellbook for one character: every enabled character_spell row
    /// resolved against build-5875 Spell/SkillLine DBCs, grouped into the client's own
    /// spellbook tabs, with the highest known rank of each chain marked. It also
    /// cross-references the bot's persisted custom rotation so an instruction naming an
    /// unlearned or outgrown rank is visible here rather than only as a skipped count.
    /// Read-only, and lazy per-bot for the same reason the talent endpoint is.
    /// </summary>
    [HttpGet("Bots/Spellbook/{guid:int}")]
    public async Task<IActionResult> Spellbook(int guid)
    {
        var result = await _spellbook.GetAsync(guid, HttpContext.RequestAborted);
        return result.ErrorCode == "not_found" ? NotFound(result) : Json(result);
    }

    /// <summary>
    /// Unified persisted talent + effective runtime rotation projection. Unlike
    /// the legacy Talents endpoint, this requires a managed playerbot row and
    /// reports what the connected core says it is actually executing.
    /// </summary>
    [HttpGet("Bots/CombatLoadout/{guid:int}")]
    public async Task<IActionResult> CombatLoadout(int guid)
    {
        try
        {
            BotCombatLoadoutView view = await _combatLoadouts.GetAsync(guid, HttpContext.RequestAborted);
            view.QueuedChange = await _combatLoadoutQueue.GetAsync(guid, HttpContext.RequestAborted);
            return Json(view);
        }
        catch (BotCombatLoadoutException ex)
        {
            return StatusCode(ex.StatusCode, new { errorCode = ex.Code, error = ex.Message });
        }
        catch (BotCombatLoadoutQueueException ex)
        {
            return StatusCode(ex.StatusCode, new { errorCode = ex.Code, error = ex.Message });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch
        {
            return StatusCode(500, new
            {
                errorCode = "combat_loadout_unavailable",
                error = "Combat loadout data is temporarily unavailable."
            });
        }
    }

    /// <summary>
    /// Apply talents, active role, and rotation as one correlated core command.
    /// The HTTP request completes only after COMBAT_LOADOUT_ACK or a bounded
    /// timeout; it never retries a destructive talent reset.
    /// </summary>
    [HttpPost("Bots/CombatLoadout/{guid:int}")]
    public async Task<IActionResult> ApplyCombatLoadout(
        int guid,
        [FromBody] BotCombatLoadoutRequest? request)
    {
        if (request == null)
            return BadRequest(new { errorCode = "request_required", error = "A combat loadout request body is required." });

        try
        {
            try
            {
                return Json(await _combatLoadoutQueue.ApplyDirectAsync(
                    guid,
                    request,
                    HttpContext.RequestAborted,
                    User.Identity?.Name ?? "web",
                    HttpContext.Connection.RemoteIpAddress?.ToString()));
            }
            catch (BotCombatLoadoutException ex)
                when (BotCombatLoadoutQueueService.CanQueueAfterDirectRejection(ex.Code))
            {
                // The page can become stale between GET and POST, and several core
                // safety gates (casting, taxi, teleport, possession, battleground)
                // are only known authoritatively at dispatch time. Their rejection
                // occurs before mutation, so preserve the user's exact intent in the
                // one-deep queue instead of making them submit the same build again.
                BotCombatLoadoutQueueMutationResult queued = await _combatLoadoutQueue.EnqueueAsync(
                    guid,
                    request,
                    User.Identity?.Name ?? "web",
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    HttpContext.RequestAborted);
                return Accepted(queued);
            }
        }
        catch (BotCombatLoadoutException ex)
        {
            return StatusCode(ex.StatusCode, new { errorCode = ex.Code, error = ex.Message });
        }
        catch (BotCombatLoadoutQueueException ex)
        {
            return StatusCode(ex.StatusCode, new { errorCode = ex.Code, error = ex.Message });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch
        {
            return StatusCode(500, new
            {
                errorCode = "combat_loadout_apply_failed",
                error = "The combat loadout could not be applied. Refresh runtime state before trying again."
            });
        }
    }

    /// <summary>
    /// Save or replace the one pending combat build for an online bot. The worker
    /// dispatches only in the same live session once the bot is alive and out of
    /// combat. The queued request is never auto-retried after an uncertain send.
    /// </summary>
    [HttpPost("Bots/CombatLoadout/{guid:int}/Queue")]
    public async Task<IActionResult> QueueCombatLoadout(
        int guid,
        [FromBody] BotCombatLoadoutRequest? request)
    {
        if (request == null)
            return BadRequest(new { errorCode = "request_required", error = "A combat loadout request body is required." });

        try
        {
            BotCombatLoadoutQueueMutationResult result = await _combatLoadoutQueue.EnqueueAsync(
                guid,
                request,
                User.Identity?.Name ?? "web",
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                HttpContext.RequestAborted);
            return Accepted(result);
        }
        catch (BotCombatLoadoutException ex)
        {
            return StatusCode(ex.StatusCode, new { errorCode = ex.Code, error = ex.Message });
        }
        catch (BotCombatLoadoutQueueException ex)
        {
            return StatusCode(ex.StatusCode, new { errorCode = ex.Code, error = ex.Message });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch
        {
            return StatusCode(500, new
            {
                errorCode = "combat_loadout_queue_failed",
                error = "The combat loadout could not be queued. Nothing was sent to the bot."
            });
        }
    }

    /// <summary>Cancel an unsent queued combat build.</summary>
    [HttpDelete("Bots/CombatLoadout/{guid:int}/Queue")]
    public async Task<IActionResult> CancelQueuedCombatLoadout(
        int guid,
        [FromQuery] string? expectedQueueId,
        [FromQuery] string? expectedStatus)
    {
        try
        {
            return Json(await _combatLoadoutQueue.CancelAsync(
                guid, expectedQueueId, expectedStatus, HttpContext.RequestAborted));
        }
        catch (BotCombatLoadoutQueueException ex)
        {
            return StatusCode(ex.StatusCode, new { errorCode = ex.Code, error = ex.Message });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch
        {
            return StatusCode(500, new
            {
                errorCode = "combat_loadout_cancel_failed",
                error = "The queued combat loadout could not be cancelled. Refresh its state before trying again."
            });
        }
    }

    [HttpPost]
    public async Task<IActionResult> MoveTo([FromBody] MoveToRequest req)
    {
        await _bridge.SendMoveToAsync(req.Guid, req.MapId, req.X, req.Y, req.Z);
        return Json(new { success = true });
    }

    [HttpPost]
    public async Task<IActionResult> SayText([FromBody] SayTextRequest req)
    {
        await _bridge.SendSayTextAsync(req.Guid, req.Text, req.ChatType);
        return Json(new { success = true });
    }

    // --- Phase 2.5 REST endpoints ---

    [HttpPost]
    public async Task<IActionResult> AcceptQuest([FromBody] QuestRequest req)
    {
        await _bridge.SendAcceptQuestAsync(req.Guid, req.QuestId);
        return Json(new { success = true, command = "ACCEPT_QUEST", req.Guid, req.QuestId });
    }

    [HttpPost]
    public async Task<IActionResult> CompleteQuest([FromBody] QuestRequest req)
    {
        await _bridge.SendCompleteQuestAsync(req.Guid, req.QuestId);
        return Json(new { success = true, command = "COMPLETE_QUEST", req.Guid, req.QuestId });
    }

    [HttpPost]
    public async Task<IActionResult> AbandonQuest([FromBody] QuestRequest req)
    {
        await _bridge.SendAbandonQuestAsync(req.Guid, req.QuestId);
        return Json(new { success = true, command = "ABANDON_QUEST", req.Guid, req.QuestId });
    }

    [HttpPost]
    public async Task<IActionResult> LearnSpell([FromBody] LearnSpellRequest req)
    {
        await _bridge.SendLearnSpellAsync(req.Guid, req.SpellId);
        return Json(new { success = true, command = "LEARN_SPELL", req.Guid, req.SpellId });
    }

    [HttpPost]
    public async Task<IActionResult> AttackTarget([FromBody] TargetRequest req)
    {
        await _bridge.SendAttackTargetAsync(req.Guid, req.TargetGuid);
        return Json(new { success = true, command = "ATTACK_TARGET", req.Guid, req.TargetGuid });
    }

    [HttpPost]
    public async Task<IActionResult> InteractNpc([FromBody] TargetRequest req)
    {
        await _bridge.SendInteractNpcAsync(req.Guid, req.TargetGuid);
        return Json(new { success = true, command = "INTERACT_NPC", req.Guid, req.TargetGuid });
    }

    [HttpPost]
    public async Task<IActionResult> SetTaskGrind([FromBody] SetTaskGrindRequest req)
    {
        await _bridge.SendSetTaskGrindAsync(req.Guid, req.X, req.Y, req.Z, req.Radius, req.CreatureEntry, req.KillCount);
        return Json(new { success = true, command = "SET_TASK_GRIND", req.Guid });
    }

    [HttpPost]
    public async Task<IActionResult> TakeFlight([FromBody] TakeFlightRequest req)
    {
        await _bridge.SendTakeFlightAsync(req.Guid, req.SourceNode, req.DestNode);
        return Json(new { success = true, command = "TAKE_FLIGHT", req.Guid });
    }

    // ==================== BotBrain API ====================

    [HttpPost]
    public IActionResult ToggleBrain(bool enabled)
    {
        _brain.BrainEnabled = enabled;
        return Json(new { success = true, enabled = _brain.BrainEnabled });
    }

    [HttpGet("Bots/BrainState/{guid}")]
    public IActionResult BrainState(int guid)
    {
        var summary = _brain.GetBotBrainSummary(guid);
        if (summary == null)
            return Json(new { guid, error = "No brain data for this bot" });
        return Json(summary);
    }

    // Bulk form of BrainState. The Bot Monitor used to fire one /Bots/BrainState/<guid> request
    // PER BOT on connect — at fleet scale that is thousands of XHRs draining through the browser's
    // 6-per-origin connection limit, each holding a pending deferred. One request returns the lot.
    [HttpGet]
    public IActionResult BrainStates()
    {
        var summaries = _brain.AllBots.Keys
            .Select(guid => _brain.GetBotBrainSummary(guid))
            .Where(s => s != null);
        return Json(new { bots = summaries });
    }

    [HttpGet]
    public IActionResult BrainStatus()
    {
        return Json(new
        {
            enabled = _brain.BrainEnabled,
            activeBots = _brain.ActiveBotCount,
            groupingMode = (int)_brain.GroupManager.Mode,
            groupingModeName = _brain.GroupManager.Mode.ToString(),
            groups = _brain.GroupManager.GetAllGroups().Select(g => new
            {
                groupId = g.GroupId,
                leaderGuid = g.LeaderGuid,
                memberGuids = g.MemberGuids,
                size = g.Size,
                formedAt = g.FormedAt
            }),
            bots = _brain.AllBots.Values.Select(b => new
            {
                guid = b.Guid,
                name = b.Name,
                level = b.Level,
                activity = b.CurrentActivity.Type.ToString(),
                quirks = b.Personality.Quirks.Select(q => q.Name),
                groupId = b.GroupId,
                isGroupLeader = b.IsGroupLeader
            })
        });
    }

    // ==================== Live Spine State (Live tab) ====================
    // Structured per-bot BotContext projection for the dashboard's Live tab — the
    // spine's real-time state (Goal/Step/why/WAIT/Failure/timers/typed scratch),
    // distinct from the old DecisionEngine summary in BrainState.

    [HttpGet("Bots/LiveState/{guid}")]
    public IActionResult LiveState(int guid)
    {
        var live = _brain.GetLiveState(guid);
        if (live == null)
            return Json(new { guid, error = "No live context for this bot" });
        return Json(live);
    }

    [HttpGet]
    public IActionResult LiveFleet()
    {
        return Json(new { bots = _brain.GetLiveFleet() });
    }

    // Per-bot brain log slice for the Live tab. Polled only while a bot is being watched.
    // name = the bot's name (whole-word filter); after = the client's last-seen seq cursor.
    [HttpGet]
    public IActionResult LiveLog(string? name, long after = 0)
    {
        var (lines, lastSeq) = _log.Query(name, after);
        var list = lines.ToList();
        var msgs = EnrichCreatureNames(list.Select(l => l.Message ?? "").ToList());
        return Json(new
        {
            lastSeq,
            lines = list.Select((l, i) => new { seq = l.Seq, t = l.Utc, msg = msgs[i] })
        });
    }

    // ---- creature-name enrichment for the live log ----
    // The KILL event (and friends) log creatures by number only — creature_entry /
    // creature_guid (AIBOTAI_REFERENCE §SendKillEvent). "guid 38" tells you nothing on its
    // own, so we resolve the number to a name here: entries via creature_template, spawn
    // guids via creature→creature_template, appending " (Name)" after the token. Names are
    // static, so resolutions are cached for the process lifetime — after warm-up the 2s
    // poll hits the DB zero times. Bare entry/guid only resolve on a creature-context line
    // so a bot/player guid never gets mislabelled; creature_entry/creature_guid always do.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _creatureEntryNames = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _creatureGuidNames = new();
    private static readonly Regex _entryTok = new(@"\b(?<pre>creature_entry|c_entry|entry)\s*[=:]?\s*(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _guidTok = new(@"\b(?<pre>creature_guid|guid)\s*[=:]?\s*(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _creatureCtx = new(@"KILL|creature|\bmob\b|victim|loot|grind|\btag\b|attack|target|slay|objective", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private List<string> EnrichCreatureNames(List<string> msgs)
    {
        var needEntries = new HashSet<int>();
        var needGuids = new HashSet<int>();
        foreach (var msg in msgs)
        {
            bool ctx = _creatureCtx.IsMatch(msg);
            foreach (Match m in _entryTok.Matches(msg))
            {
                var pre = m.Groups["pre"].Value.ToLowerInvariant();
                if (pre == "entry" && !ctx) continue;                 // bare 'entry' needs creature context
                if (int.TryParse(m.Groups["n"].Value, out var n) && !_creatureEntryNames.ContainsKey(n)) needEntries.Add(n);
            }
            foreach (Match m in _guidTok.Matches(msg))
            {
                var pre = m.Groups["pre"].Value.ToLowerInvariant();
                if (pre == "guid" && !ctx) continue;                  // bare 'guid' needs creature context
                if (int.TryParse(m.Groups["n"].Value, out var n) && !_creatureGuidNames.ContainsKey(n)) needGuids.Add(n);
            }
        }

        if (needEntries.Count > 0 || needGuids.Count > 0)
        {
            try
            {
                using var conn = _db.Mangos();
                if (needEntries.Count > 0)
                {
                    var rows = conn.Query("SELECT entry, name FROM creature_template WHERE entry IN @ids AND patch = 0",
                        new { ids = needEntries });
                    foreach (var r in rows) _creatureEntryNames[Convert.ToInt32(r.entry)] = (string)(r.name ?? "");
                    foreach (var id in needEntries) _creatureEntryNames.TryAdd(id, "");   // negative cache (unknown id)
                }
                if (needGuids.Count > 0)
                {
                    var rows = conn.Query(@"SELECT c.guid AS guid, ct.name AS name
                                            FROM creature c
                                            JOIN creature_template ct ON ct.entry = c.id AND ct.patch = 0
                                            WHERE c.guid IN @ids",
                        new { ids = needGuids });
                    foreach (var r in rows) _creatureGuidNames[Convert.ToInt32(r.guid)] = (string)(r.name ?? "");
                    foreach (var id in needGuids) _creatureGuidNames.TryAdd(id, "");
                }
            }
            catch { /* schema/DB hiccup → leave lines unenriched rather than break the feed */ }
        }

        var outList = new List<string>(msgs.Count);
        foreach (var msg in msgs) outList.Add(RewriteCreatureLine(msg));
        return outList;
    }

    private string RewriteCreatureLine(string msg)
    {
        bool ctx = _creatureCtx.IsMatch(msg);
        msg = _guidTok.Replace(msg, m => DecorateCreature(m, ctx, true));
        msg = _entryTok.Replace(msg, m => DecorateCreature(m, ctx, false));
        return msg;
    }

    private string DecorateCreature(Match m, bool ctx, bool isGuid)
    {
        var pre = m.Groups["pre"].Value.ToLowerInvariant();
        bool bare = isGuid ? (pre == "guid") : (pre == "entry");
        if (bare && !ctx) return m.Value;
        if (!int.TryParse(m.Groups["n"].Value, out var n)) return m.Value;
        var map = isGuid ? _creatureGuidNames : _creatureEntryNames;
        if (!map.TryGetValue(n, out var nm) || string.IsNullOrEmpty(nm)) return m.Value;
        return m.Value + " (" + nm + ")";
    }

    // Quantized "what did this bot just do" report, computed over the in-memory log
    // ring for ONE bot (the cocktail's bounded-output philosophy, on demand from the UI).
    // Counts/distributions only — no raw dump. Category regexes mirror the live-log
    // colorizer so the census keys off strings already proven live this session.
    [HttpGet]
    public IActionResult BotReport(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Json(new { error = "name required" });

        // Pull the WHOLE buffered window for this bot (Query defaults to max=200 — that cap
        // is what made the report mirror only the visible log tail). int.MaxValue → the ring
        // trims nothing; we get every line the buffer still holds for this name.
        var (lines, _) = _log.Query(name, 0, int.MaxValue);
        var rows = lines.ToList();
        int total = rows.Count;

        if (total == 0)
            return Json(new { name, total = 0, empty = true });

        DateTime firstUtc = rows[0].Utc, lastUtc = rows[^1].Utc;
        foreach (var l in rows)
        {
            if (l.Utc < firstUtc) firstUtc = l.Utc;
            if (l.Utc > lastUtc) lastUtc = l.Utc;
        }
        long spanSec = (long)Math.Max(0, (lastUtc - firstUtc).TotalSeconds);

        // The FleetReport heartbeat names every bot in one line, so the per-bot name
        // filter catches it here too. It's not a per-bot event — fold it out of the
        // census/signatures (counted separately) so a single bot's real activity shows.
        // Two shapes leak in: the per-bot census (repeats pick=) and the goals-rollup
        // ("FLEET N bots @ … goals: Questing=…"). Catch both.
        bool IsFleet(string? m)
        {
            if (string.IsNullOrEmpty(m)) return false;
            if (Regex.IsMatch(m, @"\bFLEET\b", RegexOptions.IgnoreCase)) return true;
            return Regex.Matches(m, "pick=").Count >= 2;
        }
        var fleetLines = rows.Count(r => IsFleet(r.Message));
        var perBot = rows.Where(r => !IsFleet(r.Message)).ToList();

        int Count(string pattern)
        {
            var re = new Regex(pattern, RegexOptions.IgnoreCase);
            int n = 0;
            foreach (var l in perBot) if (re.IsMatch(l.Message ?? "")) n++;
            return n;
        }

        // Word-boundary KILL is deliberately strict (per BOT_RUN_DIAGNOSTICS).
        int kills = Count(@"\bKILL\b");
        int completions = Count(@"completed \[");
        int rewarded = Count(@"\brewarded\b");
        int grindFinished = Count(@"GRIND finished");
        int levelUps = Count(@"LEVEL_UP");
        int resurrects = Count(@"RESURRECT");
        int deaths = Count(@"\bDEATH\b|\bDIED\b");
        int relocates = Count(@"\brelocate\b");
        int stalls = Count(@"\bSTALL");
        int noPath = Count(@"no_path|MOVE_FAILED|PATHFIND_NOPATH");
        int pathUnsafe = Count(@"PATH_UNSAFE");
        int shelvings = Count(@"shelving \[|deferring");
        int overflow = Count(@"overflow grind");
        int questEvents = Count(@"\[QUEST\]|QUEST_|seeding|\bbatch\b");
        int repairs = Count(@"\bREPAIR\b");
        int sells = Count(@"\bSELL\b");

        // Top repeated line signatures (≤12). Normalize: drop the leading bot name,
        // collapse digit runs to '#', squeeze whitespace — so "KILL Kobold (3)" and
        // "KILL Kobold (7)" fold into one signature with a count.
        var sigCounts = new Dictionary<string, int>();
        var nameRe = new Regex(@"^\s*" + Regex.Escape(name) + @"\b[:\s-]*", RegexOptions.IgnoreCase);
        var numRe = new Regex(@"\d+");
        var wsRe = new Regex(@"\s+");
        foreach (var l in perBot)
        {
            var m = l.Message ?? "";
            m = nameRe.Replace(m, "");
            m = numRe.Replace(m, "#");
            m = wsRe.Replace(m, " ").Trim();
            if (m.Length > 90) m = m.Substring(0, 90);
            if (m.Length == 0) continue;
            sigCounts[m] = sigCounts.TryGetValue(m, out var c) ? c + 1 : 1;
        }
        var top = sigCounts
            .OrderByDescending(kv => kv.Value)
            .Take(12)
            .Select(kv => new { sig = kv.Key, n = kv.Value })
            .ToList();

        return Json(new
        {
            name,
            total,
            botLines = perBot.Count,
            fleetLines,
            spanSec,
            firstUtc,
            lastUtc,
            census = new
            {
                kills,
                completions,
                rewarded,
                grindFinished,
                levelUps,
                resurrects,
                deaths,
                relocates,
                stalls,
                noPath,
                pathUnsafe,
                shelvings,
                overflow,
                questEvents,
                repairs,
                sells
            },
            // Health proxies straight out of BOT_RUN_DIAGNOSTICS:
            //   kills vs completions  → tag-credit contention (Issue 5)
            //   resurrects vs kills   → death-spiral (Issue 1)
            health = new
            {
                killsVsCompletions = completions == 0 ? (kills > 0 ? "kills, 0 credit" : "idle") : $"{kills}k / {completions}c",
                deathSpiral = kills > 0 && resurrects > kills,
                rezPerKill = kills > 0 ? Math.Round((double)resurrects / kills, 2) : (double?)null
            },
            top
        });
    }

    // ==================== Flight Recorder API ====================

    /// <summary>
    /// Toggle verbose lifecycle tracing for a specific set of bot GUIDs.
    /// Tracing is per-guid: only the listed bots emit timeline records. Persisted to
    /// bot_settings so it survives a restart. POST {"enabled":true,"guids":[4,5,6,7,8]}.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SetTrace([FromBody] SetTraceRequest req)
    {
        var guids = (req.Guids ?? Array.Empty<int>()).ToList();
        await _recorder.SetTargetsAsync(guids, req.Enabled);
        return Json(new { success = true, enabled = _recorder.Enabled, targets = _recorder.Targets });
    }

    [HttpGet]
    public IActionResult TraceStatus()
    {
        return Json(new { enabled = _recorder.Enabled, targets = _recorder.Targets });
    }

    // ==================== Story Rider API (StoryRider I4) ====================
    // Per-bot causal story log. Unlike trace (recorder-owned, DB-persisted), the rider
    // is per-bot and in-memory, so these route through the brain service. POST
    // {"enabled":true,"guids":[9]} enables one bot's story_<guid>_<name>.jsonl.

    [HttpPost]
    public IActionResult SetStory([FromBody] SetStoryRequest req)
    {
        var guids = req.Guids ?? Array.Empty<int>();
        var affected = _brain.SetStoryEnabled(guids, req.Enabled);
        return Json(new { success = true, enabled = req.Enabled, targets = affected });
    }

    [HttpGet]
    public IActionResult StoryStatus()
    {
        return Json(new { bots = _brain.GetStoryStatus() });
    }

    // ==================== Grouping API (Session 31) ====================

    [HttpPost]
    public async Task<IActionResult> SetGroupingMode([FromBody] GroupingModeRequest req)
    {
        if (!Enum.IsDefined(typeof(MangosSuperUI.BotLogic.Core.GroupingMode), req.Mode))
            return Json(new { success = false, error = "Invalid mode. Use 0=Off, 1=Sticky, 2=Opportunistic." });

        var mode = (MangosSuperUI.BotLogic.Core.GroupingMode)req.Mode;
        await _brain.SetGroupingModeAsync(mode);
        return Json(new { success = true, mode = req.Mode, modeName = mode.ToString() });
    }

    [HttpPost]
    public async Task<IActionResult> AutoFormGroups()
    {
        var formed = await _brain.AutoFormGroupsAsync();
        return Json(new
        {
            success = true,
            groupsFormed = formed.Count,
            groups = formed.Select(g => new
            {
                groupId = g.GroupId,
                leaderGuid = g.LeaderGuid,
                memberGuids = g.MemberGuids
            })
        });
    }

    [HttpPost]
    public async Task<IActionResult> FormGroup([FromBody] FormGroupRequest req)
    {
        if (req.LeaderGuid <= 0 || req.FollowerGuids == null || req.FollowerGuids.Length == 0)
            return Json(new { success = false, error = "Need leaderGuid + at least 1 followerGuid" });

        var group = await _brain.FormGroupAsync(req.LeaderGuid, req.FollowerGuids);
        if (group == null)
            return Json(new { success = false, error = "Formation failed — check mode is not Off and bots are not already grouped" });

        return Json(new
        {
            success = true,
            groupId = group.GroupId,
            leaderGuid = group.LeaderGuid,
            memberGuids = group.MemberGuids
        });
    }

    [HttpPost]
    public async Task<IActionResult> DisbandGroup([FromBody] DisbandGroupRequest req)
    {
        await _brain.DisbandGroupAsync(req.GroupId);
        return Json(new { success = true, groupId = req.GroupId });
    }

    /// <summary>Dissolve every active bot group (Manage Bot Groups → Dissolve All).</summary>
    [HttpPost]
    public async Task<IActionResult> DisbandAllGroups()
    {
        var disbanded = await _brain.DisbandAllGroupsAsync();
        return Json(new { success = true, disbanded });
    }

    // ==================== Bot Quest Progress ====================

    /// <summary>
    /// GET /Bots/QuestStatus?guid=8
    /// Returns all quest statuses for a bot from character_queststatus,
    /// joined with quest_template for titles and chain data.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> QuestStatus(int guid)
    {
        try
        {
            using var charConn = _db.Characters();
            using var mangosConn = _db.Mangos();

            var rows = await charConn.QueryAsync<dynamic>(@"
                SELECT quest, status, rewarded,
                       mob_count1, mob_count2, mob_count3, mob_count4,
                       item_count1, item_count2, item_count3, item_count4
                FROM character_queststatus
                WHERE guid = @Guid",
                new { Guid = guid });

            var questIds = rows.Select(r => (int)r.quest).Distinct().ToList();
            if (questIds.Count == 0)
                return Json(new { guid, quests = Array.Empty<object>() });

            var templates = await mangosConn.QueryAsync<dynamic>(@"
                SELECT entry, Title, QuestLevel, MinLevel, ZoneOrSort,
                       PrevQuestId, NextQuestId, NextQuestInChain, ExclusiveGroup,
                       ReqCreatureOrGOId1, ReqCreatureOrGOId2,
                       ReqCreatureOrGOCount1, ReqCreatureOrGOCount2,
                       ReqItemId1, ReqItemId2, ReqItemId3, ReqItemId4,
                       ReqItemCount1, ReqItemCount2, ReqItemCount3, ReqItemCount4
                FROM quest_template
                WHERE entry IN @Ids AND patch = (SELECT MAX(patch) FROM quest_template qt2 WHERE qt2.entry = quest_template.entry)",
                new { Ids = questIds });

            var tplMap = templates.ToDictionary(t => (int)t.entry);

            // Also get giver/turnin NPC names
            var giverRows = await mangosConn.QueryAsync<dynamic>(@"
                SELECT cqr.quest, ct.name AS giver_name, ct.entry AS giver_entry
                FROM creature_questrelation cqr
                JOIN creature_template ct ON ct.entry = cqr.id AND ct.patch = 0
                WHERE cqr.quest IN @Ids",
                new { Ids = questIds });
            var giverMap = giverRows
                .GroupBy(r => (int)r.quest)
                .ToDictionary(g => g.Key, g => g.First());

            var turnInRows = await mangosConn.QueryAsync<dynamic>(@"
                SELECT cir.quest, ct.name AS turnin_name, ct.entry AS turnin_entry
                FROM creature_involvedrelation cir
                JOIN creature_template ct ON ct.entry = cir.id AND ct.patch = 0
                WHERE cir.quest IN @Ids",
                new { Ids = questIds });
            var turnInMap = turnInRows
                .GroupBy(r => (int)r.quest)
                .ToDictionary(g => g.Key, g => g.First());

            var result = rows.Select(r =>
            {
                int qid = (int)r.quest;
                tplMap.TryGetValue(qid, out var tpl);
                giverMap.TryGetValue(qid, out var giver);
                turnInMap.TryGetValue(qid, out var turnIn);

                return new
                {
                    questId = qid,
                    status = (int)r.status,
                    rewarded = (int)r.rewarded,
                    title = (string?)(tpl?.Title) ?? $"Quest #{qid}",
                    questLevel = (int?)(tpl?.QuestLevel) ?? 0,
                    minLevel = (int?)(tpl?.MinLevel) ?? 0,
                    zone = (int?)(tpl?.ZoneOrSort) ?? 0,
                    prevQuestId = (int?)(tpl?.PrevQuestId) ?? 0,
                    exclusiveGroup = (int?)(tpl?.ExclusiveGroup) ?? 0,
                    mobCounts = new[] { (int)r.mob_count1, (int)r.mob_count2, (int)r.mob_count3, (int)r.mob_count4 },
                    mobRequired = tpl != null ? new[] {
                        (int)(tpl.ReqCreatureOrGOCount1 ?? 0), (int)(tpl.ReqCreatureOrGOCount2 ?? 0), 0, 0
                    } : new[] { 0, 0, 0, 0 },
                    itemCounts = new[] { (int)r.item_count1, (int)r.item_count2, (int)r.item_count3, (int)r.item_count4 },
                    itemRequired = tpl != null ? new[] {
                        (int)(tpl.ReqItemCount1 ?? 0), (int)(tpl.ReqItemCount2 ?? 0),
                        (int)(tpl.ReqItemCount3 ?? 0), (int)(tpl.ReqItemCount4 ?? 0)
                    } : new[] { 0, 0, 0, 0 },
                    giverName = (string?)(giver?.giver_name),
                    turnInName = (string?)(turnIn?.turnin_name)
                };
            })
            .OrderByDescending(q => q.rewarded)
            .ThenByDescending(q => q.status)
            .ThenBy(q => q.questLevel)
            .ToList();

            return Json(new { guid, quests = result });
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ==================== Real Inventory ====================

    /// <summary>
    /// GET /Bots/Inventory?guid=25
    /// Queries real character_inventory + item_template for a bot's actual bag contents,
    /// equipped gear, and gold. No shadow economy — all real server data.
    ///
    /// VMaNGOS character_inventory layout:
    ///   bag=0, slot 0-18  → equipped gear (head, neck, shoulders, ... mainhand, offhand, ranged, tabard)
    ///   bag=0, slot 19-22 → equipped bag slots
    ///   bag=0, slot 23-38 → backpack (16 slots)
    ///   bag=N (item guid of equipped bag) → items inside that bag
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Inventory(int guid)
    {
        try
        {
            using var charConn = _db.Characters();
            using var mangosConn = _db.Mangos();

            // 1. Gold from characters table
            var gold = await charConn.ExecuteScalarAsync<long?>(
                "SELECT money FROM characters WHERE guid = @Guid", new { Guid = guid }) ?? 0;

            // 2. All items from character_inventory + item_instance for stack count
            var invRows = (await charConn.QueryAsync<dynamic>(@"
                SELECT ci.bag, ci.slot, ci.item_guid AS itemGuid, ci.item_id AS itemEntry,
                       COALESCE(ii.count, 1) AS stackCount
                FROM character_inventory ci
                LEFT JOIN item_instance ii ON ii.guid = ci.item_guid
                WHERE ci.guid = @Guid
                ORDER BY ci.bag, ci.slot",
                new { Guid = guid })).ToList();

            if (invRows.Count == 0)
                return Json(new { gold, equipped = Array.Empty<object>(), bags = Array.Empty<object>(), backpack = Array.Empty<object>() });

            // 3. Collect all unique item entries to batch-query item_template
            var entries = invRows.Select(r => (int)r.itemEntry).Distinct().ToList();

            var itemDetails = new Dictionary<int, dynamic>();
            if (entries.Count > 0)
            {
                var items = await mangosConn.QueryAsync<dynamic>(@"
                    SELECT entry, name, quality, class, subclass, inventory_type AS inventoryType,
                           item_level AS itemLevel, required_level AS requiredLevel,
                           sell_price AS sellPrice, display_id AS displayId,
                           armor, dmg_min1 AS dmgMin1, dmg_max1 AS dmgMax1, delay,
                           container_slots AS containerSlots, max_count AS maxStack
                    FROM item_template
                    WHERE entry IN @Entries AND patch = 0",
                    new { Entries = entries });

                foreach (var item in items)
                    itemDetails[(int)item.entry] = item;
            }

            // 4. Classify rows
            var equipped = new List<object>();   // slots 0-18
            var bagSlots = new List<object>();   // slots 19-22 (the bags themselves)
            var backpack = new List<object>();    // slots 23-38
            var extraBags = new Dictionary<uint, List<object>>(); // bag itemGuid → items

            foreach (var row in invRows)
            {
                uint bag = (uint)row.bag;
                int slot = (int)(byte)row.slot;
                int entry = (int)row.itemEntry;
                uint itemGuid = (uint)row.itemGuid;
                int stackCount = (int)(row.stackCount ?? 1);

                itemDetails.TryGetValue(entry, out var detail);

                var itemObj = new
                {
                    slot,
                    entry,
                    itemGuid,
                    name = (string?)(detail?.name) ?? $"Item #{entry}",
                    quality = (int?)(detail?.quality) ?? 0,
                    itemClass = (int?)(detail?.@class) ?? 0,
                    subclass = (int?)(detail?.subclass) ?? 0,
                    inventoryType = (int?)(detail?.inventoryType) ?? 0,
                    itemLevel = (int?)(detail?.itemLevel) ?? 0,
                    sellPrice = (int?)(detail?.sellPrice) ?? 0,
                    armor = (int?)(detail?.armor) ?? 0,
                    containerSlots = (int?)(detail?.containerSlots) ?? 0,
                    displayId = (uint?)(detail?.displayId) ?? 0,
                    stackCount,
                    maxStack = (int?)(detail?.maxStack) ?? 1
                };

                if (bag == 0)
                {
                    if (slot <= 18)
                        equipped.Add(itemObj);
                    else if (slot <= 22)
                        bagSlots.Add(itemObj);
                    else
                        backpack.Add(itemObj);
                }
                else
                {
                    if (!extraBags.ContainsKey(bag))
                        extraBags[bag] = new List<object>();
                    extraBags[bag].Add(itemObj);
                }
            }

            // 5. Build bag summary (bag slot → contents)
            var bagSummary = bagSlots.Select(b =>
            {
                var bd = (dynamic)b;
                uint bguid = (uint)bd.itemGuid;
                var contents = extraBags.ContainsKey(bguid) ? extraBags[bguid] : new List<object>();
                return new
                {
                    bag = b,
                    contents,
                    capacity = (int)bd.containerSlots,
                    used = contents.Count
                };
            }).ToList();

            // Total sell value of all bag items (not equipped) — price × stack count
            var totalSellValue = backpack.Cast<dynamic>().Sum(i => (int)i.sellPrice * (int)i.stackCount);
            foreach (var bg in extraBags.Values)
                totalSellValue += bg.Cast<dynamic>().Sum(i => (int)i.sellPrice * (int)i.stackCount);

            var backpackUsed = backpack.Count;
            var extraUsed = extraBags.Values.Sum(b => b.Count);
            var extraCapacity = bagSlots.Cast<dynamic>().Sum(b => (int)b.containerSlots);

            // Build icon map: displayId → icon path (same pattern as ItemsController)
            var iconMap = new Dictionary<uint, string>();
            foreach (var detail in itemDetails.Values)
            {
                uint did = (uint)(detail.displayId ?? 0);
                if (did > 0 && !iconMap.ContainsKey(did))
                    iconMap[did] = _dbc.GetItemIconPath(did);
            }

            return Json(new
            {
                gold,
                equipped,
                backpack,
                bags = bagSummary,
                icons = iconMap,
                totalItems = backpackUsed + extraUsed,
                totalSlots = 16 + extraCapacity,
                freeSlots = (16 + extraCapacity) - (backpackUsed + extraUsed),
                totalSellValue
            });
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }
}

// ==================== Request DTOs ====================

public class TakeFlightRequest
{
    public int Guid { get; set; }
    public int SourceNode { get; set; }
    public int DestNode { get; set; }
}

public class MoveToRequest
{
    public int Guid { get; set; }
    public int MapId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public class SayTextRequest
{
    public int Guid { get; set; }
    public string Text { get; set; } = "";
    public int ChatType { get; set; }
}

public class QuestRequest
{
    public int Guid { get; set; }
    public int QuestId { get; set; }
}

public class LearnSpellRequest
{
    public int Guid { get; set; }
    public int SpellId { get; set; }
}

public class TargetRequest
{
    public int Guid { get; set; }
    public int TargetGuid { get; set; }
}

public class SetTaskGrindRequest
{
    public int Guid { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; } = 60f;
    public int CreatureEntry { get; set; }
    public int KillCount { get; set; }
}

// Session 31 — Grouping DTOs

public class GroupingModeRequest
{
    public int Mode { get; set; }
}

public class FormGroupRequest
{
    public int LeaderGuid { get; set; }
    public int[] FollowerGuids { get; set; } = Array.Empty<int>();
}

public class DisbandGroupRequest
{
    public int GroupId { get; set; }
}

public class SetTraceRequest
{
    public bool Enabled { get; set; }
    public int[] Guids { get; set; } = Array.Empty<int>();
}

public class SetStoryRequest
{
    public bool Enabled { get; set; }
    public int[] Guids { get; set; } = Array.Empty<int>();
}

public class AddBotsRequest
{
    // Preferred: per-(race, class) counts. The Add Bots modal posts this shape.
    public SpawnEntry[]? Spawns { get; set; }

    // Legacy: flat class tokens (class-only, default race per class). Kept for old callers.
    public string[]? Classes { get; set; }
}

public class SpawnEntry
{
    public string? Race { get; set; }
    public string? Cls { get; set; }
    public int Count { get; set; }
}

public class BotNameRequest
{
    public string Name { get; set; } = "";
}
