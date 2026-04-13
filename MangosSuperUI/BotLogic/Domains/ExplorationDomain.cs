using MangosSuperUI.BotLogic.Core;

namespace MangosSuperUI.BotLogic.Domains;

/// <summary>
/// Handles "pointless wandering" — the behavior that makes a bot look like a real player
/// who got curious about what's over that hill.
/// </summary>
public class ExplorationDomain : IBotDomain
{
    public ActivityType[] OwnedActivities => new[] { ActivityType.Exploring };

    public Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state)
    {
        var weights = new Dictionary<ActivityType, float>();

        // Exploration is short-lived — 1-5 minutes based on Curiosity
        float minutesExploring = (float)bot.CurrentActivity.MinutesInState;
        float maxExplore = Lerp(1f, 5f, bot.Personality.Curiosity);
        float stayWeight = minutesExploring < maxExplore ? 0.6f : 0.1f;

        weights[ActivityType.Exploring] = stayWeight;
        weights[ActivityType.Questing] = 0.5f;
        weights[ActivityType.Grinding] = 0.2f;

        // Might stumble into aggro range and start fighting
        if (state.InCombat)
            weights[ActivityType.Grinding] = 2.0f;

        // If near town, might loiter
        if (state.IsNearTown)
            weights[ActivityType.Socializing] = 0.1f;

        return weights;
    }

    public List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state)
    {
        bot.CurrentActivity.ContextTag = $"zone:{state.ZoneId}:explore";

        // Pick a random direction and wander
        float wanderRadius = 100f;

        // Wanderlust quirk increases range
        foreach (var quirk in bot.Personality.Quirks)
        {
            float radius = quirk.GetFloat("Exploration.WanderRadius", -1f);
            if (radius > 0) wanderRadius = radius;
        }

        float angle = WeightedRoller.Range(0f, (float)(Math.PI * 2));
        float distance = WeightedRoller.Range(50f, wanderRadius);
        float newX = state.X + (float)Math.Cos(angle) * distance;
        float newY = state.Y + (float)Math.Sin(angle) * distance;

        return new List<BridgeCommand>
        {
            new BridgeCommand("MOVE_TO", new { mapId = state.MapId, x = newX, y = newY, z = state.Z })
        };
    }

    public List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        // If we've arrived (TASK_COMPLETE would set this), maybe pick a new wander point
        if (bot.CurrentActivity.SubPhase == "Arrived")
        {
            // Small chance to wander again instead of transitioning out
            if (WeightedRoller.Check(0.3f * bot.Personality.Curiosity))
            {
                float angle = WeightedRoller.Range(0f, (float)(Math.PI * 2));
                float distance = WeightedRoller.Range(30f, 80f);
                float newX = state.X + (float)Math.Cos(angle) * distance;
                float newY = state.Y + (float)Math.Sin(angle) * distance;

                commands.Add(new BridgeCommand("MOVE_TO", new { mapId = state.MapId, x = newX, y = newY, z = state.Z }));
                bot.CurrentActivity.SubPhase = "Wandering";
            }
        }

        return commands;
    }

    public List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt)
    {
        if (evt.EventType == "TASK_COMPLETE")
        {
            bot.CurrentActivity.SubPhase = "Arrived";
        }
        return new List<BridgeCommand>();
    }

    private static float Lerp(float min, float max, float t) => min + (max - min) * t;
}
