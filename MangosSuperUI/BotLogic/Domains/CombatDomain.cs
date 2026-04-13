using MangosSuperUI.BotLogic.Core;

namespace MangosSuperUI.BotLogic.Domains;

/// <summary>
/// Thin on the C# side since combat execution is in C++.
/// C# controls engagement decisions and post-combat behavior.
/// On enter: sends SET_TASK GRIND to C++ so the bot autonomously patrols + kills.
/// On exit: the orchestrator (BotBrainService) sends SET_TASK IDLE to clear the grind.
/// </summary>
public class CombatDomain : IBotDomain
{
    public ActivityType[] OwnedActivities => new[] { ActivityType.Grinding };

    public Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state)
    {
        var weights = new Dictionary<ActivityType, float>();

        if (state.InCombat)
        {
            // Can't transition during combat
            weights[ActivityType.Grinding] = 1.0f;
            return weights;
        }

        // Post-combat: eat if low
        if (state.HealthPercent < GetEatThreshold(bot))
            weights[ActivityType.Eating] = 1.5f;

        // Continue grinding
        float minutesGrinding = (float)bot.CurrentActivity.MinutesInState;
        float stayWeight = 0.8f;

        // Aggression → loves grinding, stays longer
        stayWeight *= Lerp(0.6f, 1.5f, bot.Personality.Aggression);

        // Boredom still applies
        float boredomPenalty = 1.0f - (minutesGrinding * Lerp(0.04f, 0.015f, bot.Personality.Patience));
        stayWeight *= Math.Max(0.2f, boredomPenalty);

        weights[ActivityType.Grinding] = stayWeight;
        weights[ActivityType.Questing] = 0.3f;
        weights[ActivityType.Vendoring] = 0.1f;
        weights[ActivityType.Exploring] = 0.05f;

        return weights;
    }

    public List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state)
    {
        bot.CurrentActivity.IsInterruptible = !state.InCombat;
        bot.CurrentActivity.ContextTag = $"zone:{state.ZoneId}:grind";

        // Tell C++ to grind in the bot's current area
        var commands = new List<BridgeCommand>();
        commands.Add(new BridgeCommand("SET_TASK", new
        {
            task = "GRIND",
            x = state.X,
            y = state.Y,
            z = state.Z,
            radius = 60.0f,
            creature_entry = 0,   // kill anything hostile
            kill_count = 0        // indefinite — C# transitions away via decision engine
        }));

        return commands;
    }

    public List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state)
    {
        // Update interruptibility based on combat state
        bot.CurrentActivity.IsInterruptible = !state.InCombat;
        return new List<BridgeCommand>();
    }

    public List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt)
    {
        var commands = new List<BridgeCommand>();

        if (evt.EventType == "KILL")
        {
            // Note the kill for activity context
            bot.CurrentActivity.ContextTag = $"zone:{state.ZoneId}:kill:{evt.CreatureEntry}";
        }
        else if (evt.EventType == "TASK_COMPLETE")
        {
            // Grind task finished (kill_count reached) — mark for transition
            bot.CurrentActivity.ContextTag = $"zone:{state.ZoneId}:grind:complete";
        }

        return commands;
    }

    /// <summary>
    /// HP threshold at which bot decides to eat. Modified by Cautiousness.
    /// </summary>
    public static float GetEatThreshold(BotIdentity bot)
    {
        return Lerp(0.3f, 0.7f, bot.Personality.Cautiousness);
    }

    /// <summary>
    /// Whether this bot should engage a creature at the given level delta.
    /// </summary>
    public static bool ShouldEngage(BotIdentity bot, int creatureLevel, int botLevel)
    {
        int delta = creatureLevel - botLevel;
        int maxDelta = 2;

        maxDelta += (int)(bot.Personality.Aggression * 2);
        maxDelta -= (int)(bot.Personality.Cautiousness * 1.5f);

        // Quirk overrides
        foreach (var quirk in bot.Personality.Quirks)
        {
            float quirkDelta = quirk.GetFloat("Combat.MaxLevelDelta", -1f);
            if (quirkDelta >= 0) { maxDelta = (int)quirkDelta; break; }
        }

        return delta <= maxDelta;
    }

    private static float Lerp(float min, float max, float t) => min + (max - min) * t;
}