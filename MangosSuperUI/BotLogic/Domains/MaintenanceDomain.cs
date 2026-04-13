using MangosSuperUI.BotLogic.Core;

namespace MangosSuperUI.BotLogic.Domains;

/// <summary>
/// Eating/drinking, brief pause while HP/mana regens.
/// Utility domain — quick activation and resolution.
/// </summary>
public class MaintenanceDomain : IBotDomain
{
    public ActivityType[] OwnedActivities => new[]
    {
        ActivityType.Eating,
        ActivityType.CorpseRunning
    };

    public Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state)
    {
        var weights = new Dictionary<ActivityType, float>();

        if (bot.CurrentActivity.Type == ActivityType.CorpseRunning)
        {
            // Corpse run is non-interruptible, but just in case:
            weights[ActivityType.CorpseRunning] = 1.0f;
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

    public List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state)
    {
        if (bot.CurrentActivity.Type == ActivityType.CorpseRunning)
        {
            bot.CurrentActivity.IsInterruptible = false;
            bot.CurrentActivity.ContextTag = "corpse:running";
            return new List<BridgeCommand>();
        }

        bot.CurrentActivity.ContextTag = $"eat:hp{(int)(state.HealthPercent * 100)}";
        bot.CurrentActivity.SubPhase = "Sitting";

        // In vanilla WoW, eating requires sitting — the C++ side handles this
        // We just mark the intent
        return new List<BridgeCommand>();
    }

    public List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state)
    {
        // Update context tag with current HP%
        if (bot.CurrentActivity.Type == ActivityType.Eating)
        {
            bot.CurrentActivity.ContextTag = $"eat:hp{(int)(state.HealthPercent * 100)}:mp{(int)(state.ManaPercent * 100)}";
        }

        return new List<BridgeCommand>();
    }

    public List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt)
    {
        var commands = new List<BridgeCommand>();

        // If we get attacked while eating, combat domain takes over
        if (evt.EventType == "COMBAT_START" || state.InCombat)
        {
            bot.CurrentActivity.IsInterruptible = true; // allow transition to combat
        }

        // Corpse run: RESPAWN event means we're alive again
        if (evt.EventType == "RESPAWN" && bot.CurrentActivity.Type == ActivityType.CorpseRunning)
        {
            bot.CurrentActivity.IsInterruptible = true;
        }

        return commands;
    }
}
