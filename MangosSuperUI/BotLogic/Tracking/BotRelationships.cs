using System.Collections.Concurrent;

namespace MangosSuperUI.BotLogic.Tracking;

/// <summary>
/// Tracks which bots have "met," grouped, chatted.
/// Future system — scaffolded now for relationship-aware social behavior.
/// </summary>
public class BotRelationships
{
    // Bot GUID → set of bot GUIDs they've interacted with
    private readonly ConcurrentDictionary<int, HashSet<int>> _metBots = new();

    // Bot GUID pair → interaction count (for familiarity weighting)
    private readonly ConcurrentDictionary<(int, int), int> _interactionCounts = new();

    /// <summary>
    /// Record that two bots have met / interacted.
    /// </summary>
    public void RecordMeeting(int botA, int botB)
    {
        AddMet(botA, botB);
        AddMet(botB, botA);

        var key = botA < botB ? (botA, botB) : (botB, botA);
        _interactionCounts.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// Have these two bots met before?
    /// </summary>
    public bool HaveMet(int botA, int botB)
    {
        return _metBots.TryGetValue(botA, out var set) && set.Contains(botB);
    }

    /// <summary>
    /// How many times have these two bots interacted?
    /// </summary>
    public int GetInteractionCount(int botA, int botB)
    {
        var key = botA < botB ? (botA, botB) : (botB, botA);
        return _interactionCounts.TryGetValue(key, out var count) ? count : 0;
    }

    /// <summary>
    /// Get all bot GUIDs that this bot has met.
    /// </summary>
    public IReadOnlyCollection<int> GetMetBots(int botGuid)
    {
        return _metBots.TryGetValue(botGuid, out var set)
            ? set.ToList()
            : Array.Empty<int>();
    }

    private void AddMet(int bot, int other)
    {
        var set = _metBots.GetOrAdd(bot, _ => new HashSet<int>());
        lock (set) { set.Add(other); }
    }
}
