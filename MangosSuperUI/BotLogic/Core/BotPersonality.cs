using System.Text.Json;

namespace MangosSuperUI.BotLogic.Core;

// ======================== Personality ========================

public class BotPersonality
{
    // --- Core behavioral traits (0.0 to 1.0) ---
    public float Patience { get; set; } = 0.5f;
    public float Greed { get; set; } = 0.5f;
    public float Curiosity { get; set; } = 0.5f;
    public float Sociability { get; set; } = 0.5f;
    public float Aggression { get; set; } = 0.5f;
    public float Efficiency { get; set; } = 0.5f;
    public float Cautiousness { get; set; } = 0.5f;

    // --- Meta traits (affect the decision loop itself) ---
    public float Indecisiveness { get; set; } = 0.5f;
    public float Spontaneity { get; set; } = 0.5f;

    // --- Cosmetic/chat traits ---
    public string ChatStyle { get; set; } = "casual";
    public string Temperament { get; set; } = "friendly";

    // --- Quirks (0-3 random quirks per bot) ---
    public List<BotQuirk> Quirks { get; set; } = new();

    // --- Computed ---
    public float DecisionTickBase => 10f + (Patience * 20f) - (Indecisiveness * 8f);

    /// <summary>
    /// Returns a serializable summary for dashboard display.
    /// </summary>
    public object ToSummary() => new
    {
        patience = Patience,
        greed = Greed,
        curiosity = Curiosity,
        sociability = Sociability,
        aggression = Aggression,
        efficiency = Efficiency,
        cautiousness = Cautiousness,
        indecisiveness = Indecisiveness,
        spontaneity = Spontaneity,
        chatStyle = ChatStyle,
        temperament = Temperament,
        quirks = Quirks.Select(q => new { q.Id, q.Name, q.Description }).ToList(),
        tickBase = DecisionTickBase
    };
}

// ======================== Quirk ========================

public class BotQuirk
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, JsonElement> Modifiers { get; set; } = new();

    /// <summary>
    /// Apply quirk overrides to the weight dictionary.
    /// Quirks use dotted keys like "Combat.FleeThreshold", "Economy.AHVisitWeight".
    /// For activity weight modifiers, we map known keys to ActivityType multipliers.
    /// </summary>
    public void ApplyModifiers(Dictionary<ActivityType, float> weights, BotIdentity bot)
    {
        foreach (var kv in Modifiers)
        {
            switch (kv.Key)
            {
                // Economy domain
                case "Economy.AHVisitWeight":
                    ModWeight(weights, ActivityType.AuctionHouse, kv.Value.GetSingle());
                    break;
                case "Economy.VendorUrgency":
                    ModWeight(weights, ActivityType.Vendoring, kv.Value.GetSingle());
                    break;

                // Social domain
                case "Social.TownLoiterWeight":
                    ModWeight(weights, ActivityType.Socializing, kv.Value.GetSingle());
                    ModWeight(weights, ActivityType.Loitering, kv.Value.GetSingle());
                    break;

                // Questing domain
                case "Questing.QuestWeight":
                    ModWeight(weights, ActivityType.Questing, kv.Value.GetSingle());
                    break;
                case "Questing.GrindPreference":
                    ModWeight(weights, ActivityType.Grinding, kv.Value.GetSingle());
                    break;

                // Exploration
                case "Exploration.RandomWanderChance":
                    // Handled by ExplorationDomain directly — stored in quirk for domain to read
                    break;

                // Trait overrides (set the trait value directly)
                case "Efficiency":
                    bot.Personality.Efficiency = Math.Clamp(kv.Value.GetSingle(), 0f, 1f);
                    break;
                case "Aggression":
                    bot.Personality.Aggression = Math.Clamp(kv.Value.GetSingle(), 0f, 1f);
                    break;
            }
        }
    }

    /// <summary>
    /// Get a float modifier value, or return default.
    /// </summary>
    public float GetFloat(string key, float defaultValue = 0f)
    {
        if (Modifiers.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetSingle();
        return defaultValue;
    }

    /// <summary>
    /// Get a bool modifier value, or return default.
    /// </summary>
    public bool GetBool(string key, bool defaultValue = false)
    {
        if (Modifiers.TryGetValue(key, out var el))
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    private static void ModWeight(Dictionary<ActivityType, float> w, ActivityType t, float multiplier)
    {
        if (w.ContainsKey(t)) w[t] *= multiplier;
    }
}

// ======================== Personality Roller ========================

public static class PersonalityRoller
{
    private static readonly string[] ChatStyles = { "terse", "chatty", "leetspeak", "rp", "newbie", "veteran", "casual" };
    private static readonly string[] Temperaments = { "friendly", "grumpy", "helpful", "sarcastic", "quiet" };

    public static BotPersonality Roll(List<BotQuirk>? availableQuirks = null)
    {
        var personality = new BotPersonality
        {
            Patience = WeightedRoller.Normal(0.5f, 0.18f),
            Greed = WeightedRoller.Normal(0.5f, 0.18f),
            Curiosity = WeightedRoller.Normal(0.5f, 0.18f),
            Sociability = WeightedRoller.Normal(0.5f, 0.18f),
            Aggression = WeightedRoller.Normal(0.5f, 0.18f),
            Efficiency = WeightedRoller.Normal(0.5f, 0.18f),
            Cautiousness = WeightedRoller.Normal(0.5f, 0.18f),
            Indecisiveness = WeightedRoller.Normal(0.4f, 0.15f),
            Spontaneity = WeightedRoller.Normal(0.4f, 0.15f),
            ChatStyle = ChatStyles[WeightedRoller.RangeInt(0, ChatStyles.Length - 1)],
            Temperament = Temperaments[WeightedRoller.RangeInt(0, Temperaments.Length - 1)]
        };

        // Quirks — roll 0 to 3
        if (availableQuirks != null && availableQuirks.Count > 0)
        {
            var (quirkCount, _) = WeightedRoller.Roll(new Dictionary<int, float>
            {
                { 0, 0.30f },
                { 1, 0.35f },
                { 2, 0.25f },
                { 3, 0.10f },
            });

            var pool = availableQuirks.ToList();
            for (int i = 0; i < quirkCount && pool.Count > 0; i++)
            {
                int idx = WeightedRoller.RangeInt(0, pool.Count - 1);
                personality.Quirks.Add(pool[idx]);
                pool.RemoveAt(idx);
            }
        }

        return personality;
    }
}
