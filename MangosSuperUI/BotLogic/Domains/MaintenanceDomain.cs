using MangosSuperUI.BotLogic.Core;
using Microsoft.Extensions.Logging;

namespace MangosSuperUI.BotLogic.Domains;

/// <summary>
/// Eating/drinking and death recovery.
///
/// Death flow (Session 12 — simplified, no ghost walk):
///   C++ death tick: captures death position, emits DEATH event.
///     NO BuildPlayerRepop, NO RepopAtGraveyard. Bot stays dead at death spot.
///   BotBrainService.HandleBridgeEventAsync: parses DEATH data, stores on
///     BotIdentity.CorpseX/Y/Z/CorpseMapId.
///   DecisionEngine critical trigger (IsDead && !CorpseRunning) → switches here.
///   OnEnter: calculates a fake "corpse run" delay (15-45s), stores rez timer.
///   OnTick: waits for timer, sends RESURRECT. Bot rezzes at death position.
///   C++ RESURRECT handler: ResurrectPlayer(0.5f) + SpawnCorpseBones(), emits RESPAWN.
///   OnEvent(RESPAWN): marks interruptible, forces immediate strategic re-eval.
///   Bot eats (50% HP from revive), then resumes previous activity.
///
/// Why no ghost walk: RepopAtGraveyard picks graveyards that can be thousands of
///   yards from the corpse (cross-zone). MovePoint can't path that far. Bots get
///   stuck as ghosts at graveyards forever. Timer-based rez is simple and reliable.
/// </summary>
public class MaintenanceDomain : IBotDomain
{
    private readonly ILogger<MaintenanceDomain> _logger;
    private readonly Data.ZoneSafetyMap _safetyMap;

    // Safety timeout if RESPAWN never arrives after sending RESURRECT
    private const float ResurrectTimeoutSeconds = 20f;

    // How far to offset from corpse when seeking a safe rez spot.
    // WoW allows resurrection within ~36yd of corpse.
    private const float REZ_OFFSET_DISTANCE = 25f;

    public MaintenanceDomain(Data.ZoneSafetyMap safetyMap, ILogger<MaintenanceDomain> logger)
    {
        _safetyMap = safetyMap;
        _logger = logger;
    }

    public ActivityType[] OwnedActivities => new[]
    {
        ActivityType.Eating,
        ActivityType.CorpseRunning
    };

    public bool IsOperational => true;

    // ════════════════════════════════════════════════════════════════════
    // EvaluateTransitions
    // ════════════════════════════════════════════════════════════════════

    public Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state)
    {
        var weights = new Dictionary<ActivityType, float>();

        if (bot.CurrentActivity.Type == ActivityType.CorpseRunning)
        {
            if (bot.CurrentActivity.IsInterruptible)
            {
                // Alive again. Bot is at 50% HP from ResurrectPlayer(0.5f).
                weights[ActivityType.CorpseRunning] = 0.05f;
                weights[ActivityType.Eating] = 5.0f;
                weights[ActivityType.Questing] = 1.0f;
                weights[ActivityType.Grinding] = 0.8f;
            }
            else
            {
                // Still dead / waiting to rez — stay locked
                weights[ActivityType.CorpseRunning] = 10.0f;
            }
            return weights;
        }

        // Eating: stay until HP > 80% and Mana > 60%
        if (state.HealthPercent < 0.8f || state.ManaPercent < 0.6f)
        {
            weights[ActivityType.Eating] = 2.0f;
        }
        else
        {
            // Done eating — go back to what we were doing
            weights[ActivityType.Eating] = 0.1f;
            weights[bot.PreviousActivity?.Type ?? ActivityType.Questing] = 1.5f;
        }

        return weights;
    }

    // ════════════════════════════════════════════════════════════════════
    // OnEnter
    // ════════════════════════════════════════════════════════════════════

    public List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        if (bot.CurrentActivity.Type == ActivityType.CorpseRunning)
        {
            bot.CurrentActivity.IsInterruptible = false;

            // Calculate fake "corpse run" delay.
            // Personality-modulated: impatient bots rez faster (they'd sprint).
            // Range: 15-45 seconds.
            float baseDelay = 25.0f;
            float patienceMod = 0.7f + (bot.Personality.Patience * 0.6f); // 0.7–1.3
            float delay = baseDelay * patienceMod;
            delay = Math.Clamp(delay, 15.0f, 45.0f);

            var rezAt = DateTime.UtcNow.AddSeconds(delay);
            bot.CurrentActivity.PhaseData["rez_at_utc"] = rezAt;
            bot.CurrentActivity.SubPhase = "WaitingToRez";

            float corpseX = bot.CorpseX ?? state.X;
            float corpseY = bot.CorpseY ?? state.Y;
            float corpseZ = bot.CorpseZ ?? state.Z;
            int corpseMap = bot.CorpseMapId ?? state.MapId;

            _logger.LogInformation(
                "[BOT-MAINT] {Name} died at ({X:F0},{Y:F0},{Z:F0}) map={Map}. " +
                "Will resurrect in {Delay:F0}s (timer-based, no ghost walk).",
                bot.Name, corpseX, corpseY, corpseZ, corpseMap, delay);

            // No MOVE_TO — bot stays at death position, C++ doesn't ghost them.
            return commands;
        }

        // Eating
        bot.CurrentActivity.ContextTag = $"eat:hp{(int)(state.HealthPercent * 100)}";
        bot.CurrentActivity.SubPhase = "Sitting";

        return commands;
    }

    // ════════════════════════════════════════════════════════════════════
    // OnTick
    // ════════════════════════════════════════════════════════════════════

    public List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        if (bot.CurrentActivity.Type == ActivityType.CorpseRunning)
        {
            var subPhase = bot.CurrentActivity.SubPhase ?? "";

            switch (subPhase)
            {
                case "WaitingToRez":
                    commands.AddRange(ProcessWaitingToRez(bot, state));
                    break;

                case "GhostWalkingToSafeSpot":
                    commands.AddRange(ProcessGhostWalkToSafeSpot(bot, state));
                    break;

                case "WaitingForResurrect":
                    ProcessWaitingForResurrect(bot);
                    break;

                case "Alive":
                    // RESPAWN received — waiting for strategic eval to switch out
                    break;

                default:
                    // Unknown sub-phase — reset to WaitingToRez with immediate rez
                    _logger.LogWarning(
                        "[BOT-MAINT] {Name} unknown corpse sub-phase '{Sub}', resurrecting now.",
                        bot.Name, subPhase);
                    bot.CurrentActivity.PhaseData["rez_at_utc"] = DateTime.UtcNow;
                    bot.CurrentActivity.SubPhase = "WaitingToRez";
                    break;
            }

            return commands;
        }

        // Eating: update context tag
        if (bot.CurrentActivity.Type == ActivityType.Eating)
        {
            bot.CurrentActivity.ContextTag =
                $"eat:hp{(int)(state.HealthPercent * 100)}:mp{(int)(state.ManaPercent * 100)}";
        }

        return commands;
    }

    // ════════════════════════════════════════════════════════════════════
    // OnEvent
    // ════════════════════════════════════════════════════════════════════

    public List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt)
    {
        var commands = new List<BridgeCommand>();

        // Eating: allow transition out if attacked
        if (evt.EventType == "COMBAT_START" || state.InCombat)
        {
            if (bot.CurrentActivity.Type == ActivityType.Eating)
                bot.CurrentActivity.IsInterruptible = true;
        }

        // RESPAWN: bot is alive again
        if (evt.EventType == "RESPAWN" && bot.CurrentActivity.Type == ActivityType.CorpseRunning)
        {
            _logger.LogInformation(
                "[BOT-MAINT] {Name} RESPAWN received — alive! Forcing re-eval.",
                bot.Name);

            bot.CurrentActivity.IsInterruptible = true;
            bot.CurrentActivity.SubPhase = "Alive";
            bot.CurrentActivity.ContextTag = "corpse:alive";
            bot.NextStrategicEval = DateTime.UtcNow;

            // Clear corpse position
            bot.CorpseX = null;
            bot.CorpseY = null;
            bot.CorpseZ = null;
            bot.CorpseMapId = null;
        }

        return commands;
    }

    // ════════════════════════════════════════════════════════════════════
    // Sub-phase processors
    // ════════════════════════════════════════════════════════════════════

    private List<BridgeCommand> ProcessWaitingToRez(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        if (bot.CurrentActivity.PhaseData.TryGetValue("rez_at_utc", out var obj)
            && obj is DateTime rezAt)
        {
            if (DateTime.UtcNow >= rezAt)
            {
                float corpseX = bot.CorpseX ?? state.X;
                float corpseY = bot.CorpseY ?? state.Y;
                float corpseZ = bot.CorpseZ ?? state.Z;
                int corpseMap = bot.CorpseMapId ?? state.MapId;

                // Session 32: Before rezzing, find a safe spot near the corpse.
                // Check the corpse location itself — if it's safe, rez immediately.
                // If not, ghost-walk to a safer offset within rez range (~36yd).
                var safeSpot = FindSafeRezSpot(bot, corpseX, corpseY, corpseZ, corpseMap);

                if (safeSpot == null)
                {
                    // Corpse location is already safe (or no safety data) — rez in place
                    _logger.LogInformation(
                        "[BOT-MAINT] {Name} rez timer expired. Corpse area is safe — resurrecting in place.",
                        bot.Name);

                    bot.CurrentActivity.SubPhase = "WaitingForResurrect";
                    bot.CurrentActivity.PhaseData["resurrect_sent_at"] = DateTime.UtcNow;
                    commands.Add(new BridgeCommand("RESURRECT"));
                }
                else
                {
                    // Corpse area has hostiles — ghost-walk to safer spot first
                    _logger.LogInformation(
                        "[BOT-MAINT] {Name} rez timer expired. Hostiles near corpse " +
                        "(maxLvl={CorpseMax} at corpse vs {SafeMax} at safe spot). " +
                        "Ghost-walking {Dist:F0}yd to ({SX:F0},{SY:F0}) before rezzing.",
                        bot.Name, safeSpot.Value.corpseMaxLevel, safeSpot.Value.safeMaxLevel,
                        Distance2D(corpseX, corpseY, safeSpot.Value.x, safeSpot.Value.y),
                        safeSpot.Value.x, safeSpot.Value.y);

                    bot.CurrentActivity.SubPhase = "GhostWalkingToSafeSpot";
                    bot.CurrentActivity.PhaseData["safe_x"] = safeSpot.Value.x;
                    bot.CurrentActivity.PhaseData["safe_y"] = safeSpot.Value.y;
                    bot.CurrentActivity.PhaseData["safe_z"] = safeSpot.Value.z;
                    bot.CurrentActivity.PhaseData["ghost_walk_started"] = DateTime.UtcNow;

                    commands.Add(new BridgeCommand("MOVE_TO", new
                    {
                        mapId = corpseMap,
                        x = safeSpot.Value.x,
                        y = safeSpot.Value.y,
                        z = safeSpot.Value.z
                    }));
                }
            }
            // else: still waiting, do nothing
        }
        else
        {
            // PhaseData missing/corrupt — resurrect immediately
            _logger.LogWarning(
                "[BOT-MAINT] {Name} rez_at_utc missing from PhaseData, resurrecting now.",
                bot.Name);
            bot.CurrentActivity.SubPhase = "WaitingForResurrect";
            bot.CurrentActivity.PhaseData["resurrect_sent_at"] = DateTime.UtcNow;
            commands.Add(new BridgeCommand("RESURRECT"));
        }

        return commands;
    }

    /// <summary>
    /// Ghost is walking to a safe rez spot. Check if arrived (within 5yd)
    /// or if timeout exceeded (ghost might be stuck — rez anyway).
    /// </summary>
    private List<BridgeCommand> ProcessGhostWalkToSafeSpot(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        float targetX = bot.CurrentActivity.PhaseData.TryGetValue("safe_x", out var sx) && sx is float fx ? fx : state.X;
        float targetY = bot.CurrentActivity.PhaseData.TryGetValue("safe_y", out var sy) && sy is float fy ? fy : state.Y;

        float dist = Distance2D(state.X, state.Y, targetX, targetY);

        // Check timeout — don't let ghost walk take more than 15 seconds
        bool timedOut = false;
        if (bot.CurrentActivity.PhaseData.TryGetValue("ghost_walk_started", out var gwObj)
            && gwObj is DateTime started)
        {
            timedOut = (DateTime.UtcNow - started).TotalSeconds > 15.0;
        }

        if (dist <= 5f || timedOut)
        {
            if (timedOut && dist > 5f)
            {
                _logger.LogWarning(
                    "[BOT-MAINT] {Name} ghost walk timed out ({Dist:F0}yd from safe spot). Rezzing at current position.",
                    bot.Name, dist);
            }
            else
            {
                _logger.LogInformation(
                    "[BOT-MAINT] {Name} arrived at safe rez spot ({Dist:F1}yd from corpse). Resurrecting.",
                    bot.Name, Distance2D(state.X, state.Y, bot.CorpseX ?? state.X, bot.CorpseY ?? state.Y));
            }

            bot.CurrentActivity.SubPhase = "WaitingForResurrect";
            bot.CurrentActivity.PhaseData["resurrect_sent_at"] = DateTime.UtcNow;
            commands.Add(new BridgeCommand("RESURRECT"));
        }

        return commands;
    }

    /// <summary>
    /// Find a safe resurrection spot near the corpse. Samples 8 directions at
    /// REZ_OFFSET_DISTANCE yards (within WoW's ~36yd rez-at-corpse range).
    /// Returns the spot with the lowest max creature level, or null if the
    /// corpse location has no hostile creature spawns at all.
    ///
    /// This mimics a real player backing away from mobs before accepting the
    /// rez — you'd ghost-walk to the edge of the rez radius and rez there
    /// instead of on top of the mob that killed you.
    ///
    /// We ghost-walk whenever there are ANY hostile creature spawns in the
    /// corpse cell — even same-level mobs will aggro a 50% HP bot and
    /// potentially chain into a death loop.
    /// </summary>
    private (float x, float y, float z, int corpseMaxLevel, int safeMaxLevel)?
        FindSafeRezSpot(BotIdentity bot, float corpseX, float corpseY, float corpseZ, int mapId)
    {
        if (!_safetyMap.IsLoaded)
            return null;

        int corpseMaxLevel = _safetyMap.GetMaxCreatureLevel(mapId, corpseX, corpseY);

        // No creature spawns at all in this cell — safe to rez in place
        if (corpseMaxLevel == 0)
            return null;

        // Any hostiles near corpse → find the safest direction to ghost-walk
        // Sample 8 directions (N, NE, E, SE, S, SW, W, NW)
        int bestLevel = corpseMaxLevel;
        float bestX = corpseX, bestY = corpseY;
        bool foundBetter = false;

        for (int dir = 0; dir < 8; dir++)
        {
            float angle = dir * MathF.PI / 4f; // 0, 45, 90, ... degrees
            float testX = corpseX + MathF.Cos(angle) * REZ_OFFSET_DISTANCE;
            float testY = corpseY + MathF.Sin(angle) * REZ_OFFSET_DISTANCE;

            int levelAtSpot = _safetyMap.GetMaxCreatureLevel(mapId, testX, testY);

            if (levelAtSpot < bestLevel)
            {
                bestLevel = levelAtSpot;
                bestX = testX;
                bestY = testY;
                foundBetter = true;
            }
        }

        if (!foundBetter)
        {
            // Every direction is equally dangerous or worse — still pick the
            // direction with the fewest spawns. Since GetMaxCreatureLevel only
            // gives us max level, pick any direction with level 0 (empty cell).
            // If none are empty, just pick the first direction with equal level
            // to at least get 25yd of distance from the exact death spot.
            for (int dir = 0; dir < 8; dir++)
            {
                float angle = dir * MathF.PI / 4f;
                float testX = corpseX + MathF.Cos(angle) * REZ_OFFSET_DISTANCE;
                float testY = corpseY + MathF.Sin(angle) * REZ_OFFSET_DISTANCE;

                int levelAtSpot = _safetyMap.GetMaxCreatureLevel(mapId, testX, testY);
                if (levelAtSpot == 0)
                {
                    // Found an empty cell — great, rez there
                    return (testX, testY, corpseZ, corpseMaxLevel, 0);
                }
            }

            // All directions have spawns — just move away from corpse anyway.
            // Even if we can't find a perfectly safe spot, 25yd of distance
            // means the mob that killed us has to re-path to us, buying time.
            bestX = corpseX + REZ_OFFSET_DISTANCE; // default: move east
            bestY = corpseY;
            bestLevel = _safetyMap.GetMaxCreatureLevel(mapId, bestX, bestY);
        }

        return (bestX, bestY, corpseZ, corpseMaxLevel, bestLevel);
    }

    private void ProcessWaitingForResurrect(BotIdentity bot)
    {
        if (bot.CurrentActivity.PhaseData.TryGetValue("resurrect_sent_at", out var obj)
            && obj is DateTime sentAt)
        {
            float waitTime = (float)(DateTime.UtcNow - sentAt).TotalSeconds;
            if (waitTime > ResurrectTimeoutSeconds)
            {
                _logger.LogWarning(
                    "[BOT-MAINT] {Name} RESURRECT timeout ({Wait:F0}s). " +
                    "Forcing interruptible for strategic eval recovery.",
                    bot.Name, waitTime);

                bot.CurrentActivity.IsInterruptible = true;
                bot.CurrentActivity.SubPhase = "Alive";
                bot.CorpseX = null;
                bot.CorpseY = null;
                bot.CorpseZ = null;
                bot.CorpseMapId = null;
                bot.NextStrategicEval = DateTime.UtcNow;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    private static float Distance2D(float x1, float y1, float x2, float y2)
    {
        float dx = x1 - x2, dy = y1 - y2;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}