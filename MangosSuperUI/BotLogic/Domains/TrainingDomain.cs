using MangosSuperUI.BotLogic.Core;

namespace MangosSuperUI.BotLogic.Domains;

/// <summary>
/// Bot travels to class trainer, learns all available spells for level, leaves.
/// Utility domain — activates on specific triggers and resolves quickly.
/// </summary>
public class TrainingDomain : IBotDomain
{
    public ActivityType[] OwnedActivities => new[]
    {
        ActivityType.TravelingToTrainer,
        ActivityType.Training
    };

    public Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state)
    {
        var weights = new Dictionary<ActivityType, float>();

        if (bot.CurrentActivity.Type == ActivityType.TravelingToTrainer)
        {
            // Traveling to trainer — high commitment
            weights[ActivityType.TravelingToTrainer] = 1.5f;
            weights[ActivityType.Questing] = 0.05f;
            return weights;
        }

        // At trainer (Training) — short activity, learn and leave
        float minutesHere = (float)bot.CurrentActivity.MinutesInState;
        if (minutesHere < 0.5f)
        {
            weights[ActivityType.Training] = 2.0f;
        }
        else
        {
            // Done training — go back to main activity
            weights[ActivityType.Training] = 0.1f;
            weights[bot.PreviousActivity?.Type ?? ActivityType.Questing] = 1.5f;
            weights[ActivityType.Questing] = 0.8f;
        }

        // While near trainer (in town), consider vendor/AH
        weights[ActivityType.Vendoring] = bot.ShadowInventory.Count > 5 ? 0.3f : 0.05f;
        weights[ActivityType.Socializing] = 0.05f;

        return weights;
    }

    public List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state)
    {
        if (bot.CurrentActivity.Type == ActivityType.TravelingToTrainer)
        {
            bot.CurrentActivity.ContextTag = $"trainer:class{bot.ClassId}";
            bot.CurrentActivity.SubPhase = "Traveling";
            // Movement to trainer would be populated by SpellProgressionLoader
            // which queries npc_trainer for nearest trainer of this class
            return new List<BridgeCommand>();
        }

        // At trainer — learn spells
        bot.CurrentActivity.ContextTag = $"trainer:learning";
        bot.CurrentActivity.SubPhase = "Learning";
        return new List<BridgeCommand>();
    }

    public List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state)
    {
        return new List<BridgeCommand>();
    }

    public List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt)
    {
        var commands = new List<BridgeCommand>();

        if (evt.EventType == "TASK_COMPLETE" && bot.CurrentActivity.Type == ActivityType.TravelingToTrainer)
        {
            // Arrived at trainer — transition to Training
            bot.CurrentActivity = new ActivityState
            {
                Type = ActivityType.Training,
                StartedAt = DateTime.UtcNow,
                ContextTag = "trainer:learning",
                SubPhase = "Learning"
            };

            // Learn all available spells (SpellProgressionLoader would provide the list)
            // For now, mark training complete
            bot.HasUnlearnedSpells = false;
            bot.TicksSinceLastTrained = 0;
        }

        return commands;
    }
}
