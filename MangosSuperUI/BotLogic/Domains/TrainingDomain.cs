using System.Globalization;
using MangosSuperUI.BotLogic.Core;
using MangosSuperUI.BotLogic.Data;

namespace MangosSuperUI.BotLogic.Domains;

/// <summary>
/// Bot travels to class trainer, sends TRAIN_AT_NPC, waits for TRAIN_ACK.
/// Sub-phases: Traveling → WaitingForTrainAck → Done
/// Pattern mirrors EconomyDomain vendoring sub-phases.
/// </summary>
public class TrainingDomain : IBotDomain
{
    private readonly SpellProgressionLoader _spellLoader;
    private readonly ILogger<TrainingDomain> _logger;

    public ActivityType[] OwnedActivities => new[]
    {
        ActivityType.TravelingToTrainer,
        ActivityType.Training
    };

    public bool IsOperational => _spellLoader.IsLoaded;

    public TrainingDomain(SpellProgressionLoader spellLoader, ILogger<TrainingDomain> logger)
    {
        _spellLoader = spellLoader;
        _logger = logger;
    }

    public Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state)
    {
        var weights = new Dictionary<ActivityType, float>();
        var activity = bot.CurrentActivity;

        if (activity.Type == ActivityType.TravelingToTrainer)
        {
            var subPhase = activity.SubPhase ?? "";

            // MOVE_FAILED or PATH_UNSAFE set subPhase to "Done" — bot should escape
            // back to questing, not stay trapped with weight 8.0 forever.
            if (subPhase == "Done")
            {
                weights[ActivityType.TravelingToTrainer] = 0.1f;
                weights[ActivityType.Questing] = 5.0f;
                weights[ActivityType.Grinding] = 1.0f;
                weights[ActivityType.Eating] = state.HealthPercent < 0.3f ? 5.0f : 0.2f;
                return weights;
            }

            // Actively traveling — high stay weight
            weights[ActivityType.TravelingToTrainer] = 8.0f;
            weights[ActivityType.Questing] = 0.1f;
            weights[ActivityType.Eating] = state.HealthPercent < 0.3f ? 5.0f : 0.1f;
            return weights;
        }

        if (activity.Type == ActivityType.Training)
        {
            var subPhase = activity.SubPhase ?? "";

            // Mid-training: unbeatable (same as mid-vendor pattern)
            if (subPhase == "WaitingForTrainAck")
            {
                weights[ActivityType.Training] = 100.0f;
                return weights;
            }

            // Done — let strategic eval route back to questing
            if (subPhase == "Done" || subPhase == "")
            {
                weights[ActivityType.Training] = 0.1f;
                weights[ActivityType.Questing] = 5.0f;
                weights[ActivityType.Grinding] = 1.0f;
                return weights;
            }

            weights[ActivityType.Training] = 5.0f;
            return weights;
        }

        // Fallback escape weights
        weights[ActivityType.Questing] = 3.0f;
        return weights;
    }

    public List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        if (bot.CurrentActivity.Type == ActivityType.TravelingToTrainer)
        {
            var trainer = _spellLoader.GetNearestTrainer(bot.ClassId, state.MapId, state.X, state.Y);
            if (trainer == null)
            {
                _logger.LogWarning("[BOT-TRAIN] {Name}: no trainer found for class {Class} on map {Map}",
                    bot.Name, bot.ClassId, state.MapId);
                bot.CurrentActivity.SubPhase = "Done";
                bot.HasUnlearnedSpells = false;
                return commands;
            }

            bot.CurrentActivity.SubPhase = "Traveling";
            bot.CurrentActivity.PhaseData["trainer_entry"] = trainer.NpcEntry.ToString();
            bot.CurrentActivity.PhaseData["trainer_name"] = trainer.NpcName;
            bot.CurrentActivity.PhaseData["trainer_x"] = trainer.X.ToString("F1", CultureInfo.InvariantCulture);
            bot.CurrentActivity.PhaseData["trainer_y"] = trainer.Y.ToString("F1", CultureInfo.InvariantCulture);
            bot.CurrentActivity.PhaseData["trainer_z"] = trainer.Z.ToString("F1", CultureInfo.InvariantCulture);

            _logger.LogInformation("[BOT-TRAIN] {Name}: traveling to {Trainer} (entry={Entry})",
                bot.Name, trainer.NpcName, trainer.NpcEntry);

            commands.Add(new BridgeCommand("MOVE_TO", new
            {
                mapId = state.MapId,
                x = trainer.X,
                y = trainer.Y,
                z = trainer.Z
            }));
            return commands;
        }

        // Direct entry into Training (shouldn't happen normally, but handle it)
        bot.CurrentActivity.SubPhase = "Done";
        return commands;
    }

    public List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();
        var subPhase = bot.CurrentActivity.SubPhase ?? "";

        // ── TravelingToTrainer: check arrival via distance ──
        if (bot.CurrentActivity.Type == ActivityType.TravelingToTrainer && subPhase == "Traveling")
        {
            if (!bot.CurrentActivity.PhaseData.TryGetValue("trainer_x", out var txs) || txs == null)
                return commands;

            float tx = float.Parse((string)txs, NumberStyles.Float, CultureInfo.InvariantCulture);
            string tyStr = (string)bot.CurrentActivity.PhaseData["trainer_y"];
            float ty = float.Parse(tyStr, NumberStyles.Float, CultureInfo.InvariantCulture);

            float dx = state.X - tx;
            float dy = state.Y - ty;
            float dist = MathF.Sqrt(dx * dx + dy * dy);

            if (dist <= 8f)
            {
                // Arrived — send TRAIN_AT_NPC
                string entryVal = (string)bot.CurrentActivity.PhaseData["trainer_entry"];
                int trainerEntry = int.Parse(entryVal);
                _logger.LogInformation("[BOT-TRAIN] {Name}: arrived at trainer — sending TRAIN_AT_NPC",
                    bot.Name);

                bot.CurrentActivity = new ActivityState
                {
                    Type = ActivityType.Training,
                    StartedAt = DateTime.UtcNow,
                    SubPhase = "WaitingForTrainAck",
                    IsInterruptible = false
                };
                bot.CurrentActivity.PhaseData["trainer_entry"] = trainerEntry.ToString();

                commands.Add(new BridgeCommand("TRAIN_AT_NPC", new { npc_entry = trainerEntry }));
                return commands;
            }

            // Stuck nudge — re-send MOVE_TO after 2 min of no arrival
            if (bot.CurrentActivity.MinutesInState > 2.0)
            {
                string tzStr = (string)bot.CurrentActivity.PhaseData["trainer_z"];
                float tz = float.Parse(tzStr, NumberStyles.Float, CultureInfo.InvariantCulture);
                _logger.LogInformation("[BOT-TRAIN] {Name}: re-sending MOVE_TO trainer (stuck nudge)",
                    bot.Name);
                commands.Add(new BridgeCommand("MOVE_TO", new
                {
                    mapId = state.MapId,
                    x = tx,
                    y = ty,
                    z = tz
                }));
            }

            return commands;
        }

        // ── WaitingForTrainAck: timeout safety ──
        if (subPhase == "WaitingForTrainAck" && bot.CurrentActivity.MinutesInState > 0.5)
        {
            _logger.LogWarning("[BOT-TRAIN] {Name}: TRAIN_ACK timeout — marking done", bot.Name);
            bot.HasUnlearnedSpells = false;
            bot.CurrentActivity.SubPhase = "Done";
            bot.CurrentActivity.IsInterruptible = true;
            bot.NextStrategicEval = DateTime.UtcNow;
        }

        // ── Done: force re-eval to leave training ──
        if (subPhase == "Done")
        {
            bot.NextStrategicEval = DateTime.UtcNow;
        }

        return commands;
    }

    public List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt)
    {
        var commands = new List<BridgeCommand>();

        // TASK_COMPLETE during travel — arrived at trainer
        if (evt.EventType == "TASK_COMPLETE" &&
            bot.CurrentActivity.Type == ActivityType.TravelingToTrainer)
        {
            if (bot.CurrentActivity.PhaseData.TryGetValue("trainer_entry", out var entryStr))
            {
                int trainerEntry = int.Parse((string)entryStr);
                _logger.LogInformation(
                    "[BOT-TRAIN] {Name}: TASK_COMPLETE at trainer — sending TRAIN_AT_NPC", bot.Name);

                bot.CurrentActivity = new ActivityState
                {
                    Type = ActivityType.Training,
                    StartedAt = DateTime.UtcNow,
                    SubPhase = "WaitingForTrainAck",
                    IsInterruptible = false
                };
                bot.CurrentActivity.PhaseData["trainer_entry"] = trainerEntry.ToString();

                commands.Add(new BridgeCommand("TRAIN_AT_NPC", new { npc_entry = trainerEntry }));
            }
            return commands;
        }

        // MOVE_FAILED or PATH_UNSAFE during travel — trainer unreachable.
        // Abort this attempt but keep HasUnlearnedSpells so it retries later
        // (after level-up clears blacklists, or next strategic eval picks a different trainer).
        // Session 29: fixes Ufutodeq-style infinite MOVE_FAILED spam loop.
        if ((evt.EventType == "MOVE_FAILED" || evt.EventType == "PATH_UNSAFE") &&
            bot.CurrentActivity.Type == ActivityType.TravelingToTrainer)
        {
            _logger.LogWarning("[BOT-TRAIN] {Name}: {Evt} reaching trainer — aborting travel, will retry later",
                bot.Name, evt.EventType);
            // Temporarily clear HasUnlearnedSpells so the DecisionEngine hard override
            // (TicksSinceLastTrained) doesn't immediately shove the bot back into
            // TravelingToTrainer toward the same unreachable trainer. The flag gets
            // re-set on next level-up, and TicksSinceLastTrained resets naturally.
            bot.HasUnlearnedSpells = false;
            bot.CurrentActivity.SubPhase = "Done";
            bot.CurrentActivity.IsInterruptible = true;
            bot.NextStrategicEval = DateTime.UtcNow;
            return commands;
        }

        // TRAIN_ACK — C++ finished training
        if (evt.EventType == "TRAIN_ACK")
        {
            int learned = 0;
            if (!string.IsNullOrEmpty(evt.Data))
            {
                var parts = evt.Data.Split('|')
                    .Select(s => s.Split('=', 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0], p => p[1]);
                if (parts.TryGetValue("learned", out var ls))
                    int.TryParse(ls, out learned);
            }

            _logger.LogInformation(
                "[BOT-TRAIN] {Name}: TRAIN_ACK — learned {Learned} spells", bot.Name, learned);

            bot.HasUnlearnedSpells = false;
            bot.TicksSinceLastTrained = 0;
            bot.CurrentActivity.SubPhase = "Done";
            bot.CurrentActivity.IsInterruptible = true;
            bot.NextStrategicEval = DateTime.UtcNow;
            return commands;
        }

        // TRAIN_FAIL — couldn't train
        if (evt.EventType == "TRAIN_FAIL")
        {
            _logger.LogWarning("[BOT-TRAIN] {Name}: TRAIN_FAIL — {Data}", bot.Name, evt.Data);
            bot.HasUnlearnedSpells = false;
            bot.CurrentActivity.SubPhase = "Done";
            bot.CurrentActivity.IsInterruptible = true;
            bot.NextStrategicEval = DateTime.UtcNow;
            return commands;
        }

        return commands;
    }
}