using MangosSuperUI.BotLogic.Tracking;

namespace MangosSuperUI.BotLogic.Core;

/// <summary>
/// Fetches live data from VMaNGOS DB and admin DB to modulate decision weights.
/// Results are cached for 30 seconds to avoid hammering the database.
/// </summary>
public class LiveStateModifiers
{
    private readonly BotStateTracker _tracker;
    private readonly ILogger<LiveStateModifiers> _logger;

    // Cache: one set of modifiers per bot, refreshed every 30 seconds
    private readonly Dictionary<int, (LiveModifierSet mods, DateTime fetchedAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public LiveStateModifiers(BotStateTracker tracker, ILogger<LiveStateModifiers> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    public Task<LiveModifierSet> GetModifiers(BotIdentity bot, BotStateSnapshot state)
    {
        // Check cache
        if (_cache.TryGetValue(bot.Guid, out var cached) &&
            DateTime.UtcNow - cached.fetchedAt < CacheDuration)
        {
            return Task.FromResult(cached.mods);
        }

        var mods = new LiveModifierSet();

        try
        {
            // Zone population from tracker (in-memory, no DB hit)
            int zonePopulation = _tracker.GetBotCountInZone(state.ZoneId);
            if (zonePopulation > 10)
                mods.SocialWeight *= 1.3f;
            if (zonePopulation > 20)
                mods.GrindWeight *= 0.7f;

            // Overcrowding at current position
            int nearbyBots = _tracker.GetBotsWithinRange(state.X, state.Y, state.MapId, 100f);
            if (nearbyBots > 3)
                mods.ExploreWeight *= 1.5f;

            // Gold pressure (from BotIdentity shadow wallet — no DB query needed)
            // Session 9 fix: Only apply vendoring pressure if the bot has items to sell.
            // A level 1 bot with 0 copper and empty bags was getting VendorWeight *= 1.5
            // which pushed it into vendoring loops with nothing to sell.
            float goldPerLevel = bot.CopperBalance / (float)Math.Max(1, bot.Level * 100);
            bool hasBagContents = (state.TotalSlots - state.FreeSlots) > 2;
            if (goldPerLevel < 0.5f && hasBagContents)
                mods.VendorWeight *= 1.5f;
            if (goldPerLevel > 3.0f)
                mods.AHBuyWeight *= 1.3f;

            // XP proximity to level-up
            float xpPercent = bot.XPPercent;
            if (xpPercent > 0.9f)
            {
                mods.QuestWeight *= 1.5f;
                mods.SocialWeight *= 0.3f;
            }

            // Post-level-up training pressure
            if (bot.HasUnlearnedSpells && bot.Level > 1)
                mods.TrainingWeight *= 2.0f + (bot.TicksSinceLastTrained * 0.1f);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiveStateModifiers: error computing modifiers for bot {Guid}", bot.Guid);
        }

        _cache[bot.Guid] = (mods, DateTime.UtcNow);
        return Task.FromResult(mods);
    }

    /// <summary>
    /// Clear cached modifiers for a specific bot (e.g., on major state change).
    /// </summary>
    public void InvalidateCache(int botGuid)
    {
        _cache.Remove(botGuid);
    }
}

/// <summary>
/// Set of weight multipliers computed from live data.
/// Default is 1.0 (no modification).
/// </summary>
public class LiveModifierSet
{
    public float QuestWeight { get; set; } = 1.0f;
    public float GrindWeight { get; set; } = 1.0f;
    public float VendorWeight { get; set; } = 1.0f;
    public float AHBuyWeight { get; set; } = 1.0f;
    public float SocialWeight { get; set; } = 1.0f;
    public float ExploreWeight { get; set; } = 1.0f;
    public float TrainingWeight { get; set; } = 1.0f;
}