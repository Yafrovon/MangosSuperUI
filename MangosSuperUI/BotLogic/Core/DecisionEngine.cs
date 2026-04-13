namespace MangosSuperUI.BotLogic.Core;

/// <summary>
/// The main decision engine. One per bot, owned by BotBrainService.
/// Polls the current domain, applies personality + live state + boredom + quirk modifiers,
/// rolls weighted random, and returns a DecisionResult with bridge commands.
/// </summary>
public class DecisionEngine
{
    private readonly BotIdentity _identity;
    private readonly Dictionary<ActivityType, IBotDomain> _domainMap;
    private readonly LiveStateModifiers _liveState;

    public IBotDomain CurrentDomain => _domainMap.TryGetValue(_identity.CurrentActivity.Type, out var d)
        ? d
        : _domainMap[ActivityType.Idle];

    public DecisionEngine(
        BotIdentity identity,
        Dictionary<ActivityType, IBotDomain> domainMap,
        LiveStateModifiers liveState)
    {
        _identity = identity;
        _domainMap = domainMap;
        _liveState = liveState;
    }

    /// <summary>
    /// Main decision loop. Called by BotBrainService when this bot's tick fires.
    /// </summary>
    public async Task<DecisionResult> Tick(BotIdentity bot, BotStateSnapshot state)
    {
        var currentActivity = bot.CurrentActivity;
        currentActivity.TicksInState++;

        // 1. Non-interruptible activities (combat, corpse run) — just drive sub-tasks
        if (!currentActivity.IsInterruptible)
        {
            var tickCmds = CurrentDomain.OnTick(bot, state);
            return new DecisionResult
            {
                NewActivity = currentActivity.Type,
                Reason = $"Non-interruptible: {currentActivity.Type}",
                Commands = tickCmds,
                ActivityChanged = false
            };
        }

        // 2. Get transition weights from current domain
        var weights = CurrentDomain.EvaluateTransitions(bot, state);

        // 3. Apply personality modifiers
        ApplyPersonalityModifiers(weights, bot.Personality);

        // 4. Apply live state modifiers (cached, async)
        var liveMods = await _liveState.GetModifiers(bot, state);
        ApplyLiveModifiers(weights, liveMods);

        // 5. Apply boredom escalation (universal, not domain-specific)
        ApplyBoredomEscalation(weights, bot, currentActivity);

        // 6. Apply quirk overrides
        foreach (var quirk in bot.Personality.Quirks)
            quirk.ApplyModifiers(weights, bot);

        // 7. Roll
        var (selectedActivity, rollValue) = WeightedRoller.Roll(weights);

        // 8. Build result
        var result = new DecisionResult
        {
            NewActivity = selectedActivity,
            WeightBreakdown = new Dictionary<ActivityType, float>(weights),
            RollValue = rollValue,
            ActivityChanged = selectedActivity != currentActivity.Type
        };

        // 9. If activity changed, transition
        if (result.ActivityChanged)
        {
            bot.PreviousActivity = currentActivity;
            bot.CurrentActivity = new ActivityState
            {
                Type = selectedActivity,
                StartedAt = DateTime.UtcNow,
                IsInterruptible = selectedActivity != ActivityType.CorpseRunning
            };

            if (_domainMap.TryGetValue(selectedActivity, out var newDomain))
            {
                result.Commands = newDomain.OnEnter(bot, state);
            }
            result.Reason = $"Switched from {currentActivity.Type} to {selectedActivity} (roll: {rollValue:F2})";
        }
        else
        {
            // Stay — drive sub-tasks in current domain
            result.Commands = CurrentDomain.OnTick(bot, state);
            result.Reason = $"Staying in {currentActivity.Type} (tick #{currentActivity.TicksInState})";
        }

        return result;
    }

    private void ApplyPersonalityModifiers(Dictionary<ActivityType, float> weights, BotPersonality p)
    {
        ModWeight(weights, ActivityType.Questing, Lerp(0.6f, 1.4f, p.Patience));
        ModWeight(weights, ActivityType.Questing, Lerp(0.8f, 1.5f, p.Efficiency));
        ModWeight(weights, ActivityType.Grinding, Lerp(0.7f, 1.3f, p.Patience));
        ModWeight(weights, ActivityType.Grinding, Lerp(0.7f, 1.4f, p.Aggression));
        ModWeight(weights, ActivityType.Vendoring, Lerp(0.5f, 1.5f, p.Greed));
        ModWeight(weights, ActivityType.AuctionHouse, Lerp(0.3f, 2.0f, p.Greed));
        ModWeight(weights, ActivityType.Exploring, Lerp(0.3f, 2.0f, p.Curiosity));
        ModWeight(weights, ActivityType.Exploring, Lerp(1.0f, 0.3f, p.Efficiency));
        ModWeight(weights, ActivityType.Socializing, Lerp(0.2f, 2.5f, p.Sociability));
        ModWeight(weights, ActivityType.Socializing, Lerp(1.0f, 0.3f, p.Efficiency));
        ModWeight(weights, ActivityType.Eating, Lerp(0.8f, 1.3f, p.Cautiousness));

        // Spontaneity: flat bonus to all NON-current activities
        float spontBonus = p.Spontaneity * 0.15f;
        var current = _identity.CurrentActivity.Type;
        foreach (var key in weights.Keys.ToList())
            if (key != current) weights[key] += spontBonus;
    }

    private void ApplyLiveModifiers(Dictionary<ActivityType, float> weights, LiveModifierSet mods)
    {
        ModWeight(weights, ActivityType.Questing, mods.QuestWeight);
        ModWeight(weights, ActivityType.Grinding, mods.GrindWeight);
        ModWeight(weights, ActivityType.Vendoring, mods.VendorWeight);
        ModWeight(weights, ActivityType.AuctionHouse, mods.AHBuyWeight);
        ModWeight(weights, ActivityType.Socializing, mods.SocialWeight);
        ModWeight(weights, ActivityType.Exploring, mods.ExploreWeight);
        ModWeight(weights, ActivityType.TravelingToTrainer, mods.TrainingWeight);
    }

    private void ApplyBoredomEscalation(
        Dictionary<ActivityType, float> weights, BotIdentity bot, ActivityState activity)
    {
        float boredomRate = Lerp(0.04f, 0.012f, bot.Personality.Patience);
        float minutesIn = (float)activity.MinutesInState;
        float boredomPenalty = 1.0f - (minutesIn * boredomRate);
        boredomPenalty = Math.Max(0.15f, boredomPenalty);

        if (weights.ContainsKey(activity.Type))
            weights[activity.Type] *= boredomPenalty;
    }

    private static void ModWeight(Dictionary<ActivityType, float> w, ActivityType t, float multiplier)
    {
        if (w.ContainsKey(t)) w[t] *= multiplier;
    }

    private static float Lerp(float min, float max, float t) => min + (max - min) * t;
}
