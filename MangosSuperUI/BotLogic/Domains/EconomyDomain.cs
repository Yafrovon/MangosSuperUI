using MangosSuperUI.BotLogic.Core;

namespace MangosSuperUI.BotLogic.Domains;

/// <summary>
/// The shadow economy minigame. Handles loot rolling (via ProcessKillLoot, called on every KILL),
/// item management, vendoring transitions, and AH participation.
/// </summary>
public class EconomyDomain : IBotDomain
{
    public ActivityType[] OwnedActivities => new[]
    {
        ActivityType.Vendoring,
        ActivityType.AuctionHouse,
        ActivityType.TravelingToVendor
    };

    // ======================== Domain Transitions ========================

    public Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state)
    {
        var weights = new Dictionary<ActivityType, float>();
        float minutesHere = (float)bot.CurrentActivity.MinutesInState;

        // Stay at vendor/AH a realistic amount of time (30s - 3min)
        float stayWeight = minutesHere < 0.5f ? 2.0f : minutesHere < 2f ? 0.8f : 0.2f;
        weights[bot.CurrentActivity.Type] = stayWeight;

        // Go back to questing/grinding
        weights[ActivityType.Questing] = 0.5f;
        weights[ActivityType.Grinding] = 0.15f;

        // While in town — temptation to socialize
        weights[ActivityType.Socializing] = 0.1f;

        // Check for training needs while in town
        if (bot.HasUnlearnedSpells)
            weights[ActivityType.TravelingToTrainer] = 0.5f;

        return weights;
    }

    public List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state)
    {
        if (bot.CurrentActivity.Type == ActivityType.Vendoring)
        {
            bot.CurrentActivity.SubPhase = "Walking";
            bot.CurrentActivity.ContextTag = "vendor:seeking";
        }
        else if (bot.CurrentActivity.Type == ActivityType.AuctionHouse)
        {
            bot.CurrentActivity.SubPhase = "Walking";
            bot.CurrentActivity.ContextTag = "ah:seeking";
        }
        return new List<BridgeCommand>();
    }

    public List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();
        var phase = bot.CurrentActivity.SubPhase ?? "Walking";

        if (phase == "Walking")
        {
            // Would move to nearest vendor/AH NPC — stubbed pending ZoneDataLoader
            bot.CurrentActivity.SubPhase = "Interacting";
        }
        else if (phase == "Interacting")
        {
            // Simulate vendor sell — clear grey/white items from shadow inventory
            if (bot.CurrentActivity.Type == ActivityType.Vendoring)
            {
                var toSell = bot.ShadowInventory.Where(i => i.Quality <= 1).ToList();
                long totalCopper = 0;
                foreach (var item in toSell)
                {
                    totalCopper += item.SellPrice * item.Count;
                    bot.ShadowInventory.Remove(item);
                }
                bot.CopperBalance += totalCopper;
                bot.CurrentActivity.SubPhase = "Done";
            }
            else
            {
                bot.CurrentActivity.SubPhase = "Done";
            }
        }

        return commands;
    }

    public List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt)
    {
        return new List<BridgeCommand>();
    }

    // ======================== Loot Processing (Always-On) ========================

    /// <summary>
    /// Called by BotBrainService on every KILL event, independent of current activity domain.
    /// This is always-on — economy runs in the background.
    /// For now: roll copper drop based on creature level from the event.
    /// Full creature_loot_template queries will be added when Data loaders connect.
    /// </summary>
    public void ProcessKillLoot(BotIdentity bot, int creatureEntry, int creatureLevel)
    {
        // Copper drop: level × 3-6 copper with ±30% variance
        int baseDrop = creatureLevel * WeightedRoller.RangeInt(3, 6);
        float variance = WeightedRoller.Range(0.7f, 1.3f);
        int copperDrop = (int)(baseDrop * variance);
        bot.CopperBalance += copperDrop;

        // Random grey/white junk loot (simplified — no DB query yet)
        if (WeightedRoller.Check(0.4f))
        {
            bot.ShadowInventory.Add(new ShadowInventoryItem
            {
                ItemId = 0, // placeholder
                Count = 1,
                Quality = 0, // grey
                SellPrice = creatureLevel * WeightedRoller.RangeInt(1, 3),
                Source = "loot",
                SourceCreatureEntry = creatureEntry
            });
        }
    }

    // ======================== AH Posting ========================

    /// <summary>
    /// Calculate an AH posting price. Personality-driven.
    /// </summary>
    public static (long buyoutCopper, int durationHours) CalculateAHPosting(
        BotIdentity bot, ShadowInventoryItem item)
    {
        float vendorPrice = Math.Max(item.SellPrice, 1);
        float qualityMultiplier = item.Quality switch
        {
            0 => 1.0f,
            1 => 1.5f + WeightedRoller.Range(0f, 1.0f),
            2 => 3.0f + WeightedRoller.Range(0f, 4.0f),
            3 => 8.0f + WeightedRoller.Range(0f, 12.0f),
            4 => 20.0f + WeightedRoller.Range(0f, 30.0f),
            _ => 1.0f
        };

        float greedMod = Lerp(0.8f, 1.4f, bot.Personality.Greed);
        float patienceMod = Lerp(1.2f, 0.8f, bot.Personality.Patience);

        long buyoutCopper = (long)(vendorPrice * qualityMultiplier * greedMod * patienceMod);
        int durationHours = (int)Lerp(12f, 48f, bot.Personality.Patience);

        return (buyoutCopper, durationHours);
    }

    private static float Lerp(float min, float max, float t) => min + (max - min) * t;
}
