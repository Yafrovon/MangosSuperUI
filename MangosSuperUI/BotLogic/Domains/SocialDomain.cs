using MangosSuperUI.BotLogic.Core;

namespace MangosSuperUI.BotLogic.Domains;

/// <summary>
/// Handles the "human flavor" — emotes, town loitering, proximity reactions,
/// and chat initiation (the gateway to the LLM layer in Phase 4).
/// </summary>
public class SocialDomain : IBotDomain
{
    private static readonly string[] TownEmotes = { "/stretch", "/yawn", "/sit", "/dance", "/wave", "/scratch", "/laugh", "/flex", "/kneel", "/cry" };
    private static readonly string[] GreetEmotes = { "/wave", "/hello", "/bow", "/salute" };

    public ActivityType[] OwnedActivities => new[]
    {
        ActivityType.Socializing,
        ActivityType.Loitering
    };

    public Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state)
    {
        var weights = new Dictionary<ActivityType, float>();
        float minutesSocializing = (float)bot.CurrentActivity.MinutesInState;

        // Town loitering — realistic duration based on sociability
        float maxLoiter = Lerp(1f, 8f, bot.Personality.Sociability);
        float stayWeight = minutesSocializing < maxLoiter ? 1.0f : 0.2f;
        weights[ActivityType.Socializing] = stayWeight;

        // Eventually go back to productive activities
        weights[ActivityType.Questing] = 0.4f;
        weights[ActivityType.Grinding] = 0.15f;
        weights[ActivityType.AuctionHouse] = 0.1f;
        weights[ActivityType.TravelingToTrainer] = bot.HasUnlearnedSpells ? 0.3f : 0.0f;

        // Might decide to vendor while in town
        if (bot.ShadowInventory.Count > 5)
            weights[ActivityType.Vendoring] = 0.2f;

        return weights;
    }

    public List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state)
    {
        bot.CurrentActivity.ContextTag = $"zone:{state.ZoneId}:social";
        bot.CurrentActivity.SubPhase = "Loitering";

        var commands = new List<BridgeCommand>();

        // Arrival emote — wave or /hello
        if (WeightedRoller.Check(0.6f * bot.Personality.Sociability))
        {
            string emote = GreetEmotes[WeightedRoller.RangeInt(0, GreetEmotes.Length - 1)];
            commands.Add(new BridgeCommand("SAY_TEXT", new { text = emote, chatType = 0 }));
        }

        return commands;
    }

    public List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        // Random emote while in town (chance scales with sociability)
        if (WeightedRoller.Check(0.08f * bot.Personality.Sociability))
        {
            string emote = TownEmotes[WeightedRoller.RangeInt(0, TownEmotes.Length - 1)];
            commands.Add(new BridgeCommand("SAY_TEXT", new { text = emote, chatType = 0 }));
        }

        // Proximity reaction — if another player/bot is nearby
        if (state.NearbyPlayerCount > 0 && WeightedRoller.Check(0.05f * bot.Personality.Sociability))
        {
            // Check quirk modifiers
            float proximityChance = 0.05f;
            foreach (var q in bot.Personality.Quirks)
            {
                float qVal = q.GetFloat("Social.ProximityEmoteChance", -1f);
                if (qVal >= 0) proximityChance = qVal;
            }

            if (WeightedRoller.Check(proximityChance))
            {
                string emote = GreetEmotes[WeightedRoller.RangeInt(0, GreetEmotes.Length - 1)];
                commands.Add(new BridgeCommand("SAY_TEXT", new { text = emote, chatType = 0 }));
            }
        }

        // Chatty Kathy quirk — initiate conversation unprompted
        foreach (var quirk in bot.Personality.Quirks)
        {
            float chatChance = quirk.GetFloat("Social.InitiateChatChance", -1f);
            if (chatChance > 0 && state.NearbyPlayerCount > 0 && WeightedRoller.Check(chatChance * 0.3f))
            {
                // Phase 4: This will route to Ollama for generated dialogue
                // For now, emit a generic greeting based on temperament
                string greeting = bot.Personality.Temperament switch
                {
                    "friendly" => "Hey there! How's the grind going?",
                    "grumpy" => "...",
                    "helpful" => "Need any help around here?",
                    "sarcastic" => "Oh great, more people.",
                    "quiet" => ".",
                    _ => "Hey"
                };
                commands.Add(new BridgeCommand("SAY_TEXT", new { text = greeting, chatType = 0 }));
            }
        }

        return commands;
    }

    public List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt)
    {
        var commands = new List<BridgeCommand>();

        // If someone chats at us while socializing, react
        if (evt.EventType == "CHAT_RECV" && WeightedRoller.Check(0.5f + bot.Personality.Sociability * 0.4f))
        {
            // Phase 4: Route to Ollama for personality-driven response
            // For now, emote back
            commands.Add(new BridgeCommand("SAY_TEXT", new { text = "/nod", chatType = 0 }));
        }

        return commands;
    }

    private static float Lerp(float min, float max, float t) => min + (max - min) * t;
}
