using MangosSuperUI.BotLogic.Core;

namespace MangosSuperUI.BotLogic.Domains;

/// <summary>
/// The most complex domain. Manages the quest lifecycle: pick quest → travel to giver → accept →
/// travel to objectives → complete objectives → travel to turn-in → turn in.
/// Also handles the tension between "follow the guide" and "get distracted."
/// </summary>
public class QuestingDomain : IBotDomain
{
    public ActivityType[] OwnedActivities => new[]
    {
        ActivityType.Questing,
        ActivityType.TravelingToQuest,
        ActivityType.Idle
    };

    public Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state)
    {
        var weights = new Dictionary<ActivityType, float>();
        var personality = bot.Personality;
        var minutesInActivity = bot.CurrentActivity.MinutesInState;

        // === BASE: Stay questing ===
        float stayWeight = 1.0f;

        // Near quest completion? Strong pull to finish.
        if (bot.CurrentQuestProgress >= 0.7f)
            stayWeight *= 1.5f;

        // Close to level-up? Strong pull to keep going.
        if (bot.XPPercent > 0.85f)
            stayWeight *= 1.4f;

        // Boredom escalation — domain-level (DecisionEngine also applies its own)
        float boredomRate = Lerp(0.03f, 0.01f, personality.Patience);
        float boredomPenalty = 1.0f - (float)(minutesInActivity * boredomRate);
        stayWeight *= Math.Max(0.3f, boredomPenalty);

        weights[ActivityType.Questing] = stayWeight;

        // === ALTERNATIVES ===
        weights[ActivityType.Grinding] = 0.1f;

        // Vendor — shadow inventory building up?
        int inventoryCount = bot.ShadowInventory.Count;
        float vendorUrgency = inventoryCount > 10 ? 0.3f : inventoryCount > 5 ? 0.15f : 0.05f;
        weights[ActivityType.Vendoring] = vendorUrgency;

        // Training — just leveled up and haven't trained?
        if (bot.HasUnlearnedSpells)
            weights[ActivityType.TravelingToTrainer] = 0.4f;

        // AH — check if there are items worth posting
        if (bot.ShadowInventory.Any(i => i.Quality >= 2)) // Uncommon+
            weights[ActivityType.AuctionHouse] = 0.1f;

        // Exploration — random itch to wander
        weights[ActivityType.Exploring] = 0.05f;

        // Socializing — if in/near a town
        if (state.IsNearTown)
            weights[ActivityType.Socializing] = 0.08f;

        // Eating — HP or mana low
        if (state.HealthPercent < 0.5f || state.ManaPercent < 0.3f)
            weights[ActivityType.Eating] = 0.6f;

        return weights;
    }

    public List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state)
    {
        // When entering questing, set sub-phase to PickingQuest
        bot.CurrentActivity.SubPhase = "PickingQuest";
        bot.CurrentActivity.ContextTag = bot.ActiveQuestId.HasValue ? $"quest:{bot.ActiveQuestId}" : "quest:picking";

        // If we have an active quest, resume it — otherwise the guide loader
        // would provide the next task (stubbed for now)
        return new List<BridgeCommand>();
    }

    public List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state)
    {
        // Sub-phase sequencer — drive quest steps
        // For now returns empty; the guide loader integration will populate this
        var commands = new List<BridgeCommand>();

        // Increment ticks since last trained (for training pressure)
        if (bot.HasUnlearnedSpells)
            bot.TicksSinceLastTrained++;

        return commands;
    }

    public List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt)
    {
        var commands = new List<BridgeCommand>();

        switch (evt.EventType)
        {
            case "QUEST_UPDATE":
                if (evt.QuestStatus == "COMPLETE" && evt.QuestId == bot.ActiveQuestId)
                {
                    bot.CompletedQuestIds.Add(evt.QuestId);
                    bot.ActiveQuestId = null;
                    bot.CurrentQuestProgress = 0f;
                    bot.CurrentActivity.SubPhase = "PickingQuest";
                }
                break;

            case "TASK_COMPLETE":
                // Movement arrived — advance to next quest phase
                var phase = bot.CurrentActivity.SubPhase;
                if (phase == "TravelingToGiver")
                    bot.CurrentActivity.SubPhase = "AcceptingQuest";
                else if (phase == "TravelingToArea")
                    bot.CurrentActivity.SubPhase = "DoingObjective";
                else if (phase == "TravelingToTurnIn")
                    bot.CurrentActivity.SubPhase = "TurningIn";
                break;
        }

        return commands;
    }

    private static float Lerp(float min, float max, float t) => min + (max - min) * t;
}
