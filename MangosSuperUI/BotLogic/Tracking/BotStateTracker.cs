using System.Collections.Concurrent;
using MangosSuperUI.Services;

namespace MangosSuperUI.BotLogic.Tracking;

/// <summary>
/// Tracks live positions of all bots in-memory for proximity/zone queries.
/// Fed by BotBridgeService STATE updates — no DB queries.
/// Used by LiveStateModifiers for zone population and overcrowding checks.
/// </summary>
public class BotStateTracker
{
    private readonly BotBridgeService _bridge;

    // Zone ID → set of bot GUIDs in that zone
    private readonly ConcurrentDictionary<int, HashSet<int>> _zonePopulation = new();

    // Bot GUID → last known position
    private readonly ConcurrentDictionary<int, BotPosition> _positions = new();

    // Town zone IDs — hardcoded for vanilla WoW starting areas and capitals
    private static readonly HashSet<int> TownZoneIds = new()
    {
        // Alliance
        1519, // Stormwind
        1537, // Ironforge
        1657, // Darnassus
        // Horde
        1637, // Orgrimmar
        1638, // Thunder Bluff
        1497, // Undercity
        // Neutral
        1377, // Silithus (Cenarion Hold area)
        // Starting areas (towns within)
        12,   // Elwynn Forest (Goldshire)
        14,   // Dun Morogh (Kharanos)
        141,  // Teldrassil (Dolanaar)
        85,   // Tirisfal Glades (Brill)
        14,   // Durotar (Razor Hill)
        215,  // Mulgore (Bloodhoof)
    };

    public BotStateTracker(BotBridgeService bridge)
    {
        _bridge = bridge;
    }

    /// <summary>
    /// Update a bot's tracked position. Called on every STATE message.
    /// </summary>
    public void UpdatePosition(int guid, int zoneId, int mapId, float x, float y, float z)
    {
        // Remove from old zone if changed
        if (_positions.TryGetValue(guid, out var old) && old.ZoneId != zoneId)
        {
            if (_zonePopulation.TryGetValue(old.ZoneId, out var oldSet))
                lock (oldSet) { oldSet.Remove(guid); }
        }

        // Update position
        _positions[guid] = new BotPosition
        {
            Guid = guid,
            ZoneId = zoneId,
            MapId = mapId,
            X = x,
            Y = y,
            Z = z,
            UpdatedAt = DateTime.UtcNow
        };

        // Add to new zone
        var zoneSet = _zonePopulation.GetOrAdd(zoneId, _ => new HashSet<int>());
        lock (zoneSet) { zoneSet.Add(guid); }
    }

    /// <summary>
    /// Remove a bot from tracking (on disconnect).
    /// </summary>
    public void Remove(int guid)
    {
        if (_positions.TryRemove(guid, out var pos))
        {
            if (_zonePopulation.TryGetValue(pos.ZoneId, out var set))
                lock (set) { set.Remove(guid); }
        }
    }

    /// <summary>
    /// How many bots are currently in a given zone.
    /// </summary>
    public int GetBotCountInZone(int zoneId)
    {
        if (_zonePopulation.TryGetValue(zoneId, out var set))
            lock (set) { return set.Count; }
        return 0;
    }

    /// <summary>
    /// How many bots are within range (yards) of a given position on a given map.
    /// </summary>
    public int GetBotsWithinRange(float x, float y, int mapId, float range)
    {
        float rangeSq = range * range;
        int count = 0;

        foreach (var kvp in _positions)
        {
            var pos = kvp.Value;
            if (pos.MapId != mapId) continue;

            float dx = pos.X - x;
            float dy = pos.Y - y;
            if (dx * dx + dy * dy <= rangeSq)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Is the given zone a town zone?
    /// </summary>
    public static bool IsNearTown(int zoneId)
    {
        return TownZoneIds.Contains(zoneId);
    }

    /// <summary>
    /// Get all tracked positions (for dashboard map visualization).
    /// </summary>
    public IReadOnlyCollection<BotPosition> GetAllPositions()
    {
        return _positions.Values.ToList();
    }
}

public class BotPosition
{
    public int Guid { get; set; }
    public int ZoneId { get; set; }
    public int MapId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public DateTime UpdatedAt { get; set; }
}
