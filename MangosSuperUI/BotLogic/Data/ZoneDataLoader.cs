using Dapper;
using MangosSuperUI.Models;
using MangosSuperUI.Services;

namespace MangosSuperUI.BotLogic.Data;

/// <summary>
/// Zone metadata, NPC positions (vendors, trainers, flight masters),
/// and grind spot data from the mangos DB.
/// All data is cached after first load — zone geometry doesn't change at runtime.
///
/// Session 8 fix: VMaNGOS creature table has no 'zone' column. Vendors and
/// innkeepers are now indexed by map ID, and lookups use map + distance instead
/// of zone. GetNearestVendor searches all vendors on the same map within range.
/// </summary>
public class ZoneDataLoader
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<ZoneDataLoader> _logger;

    // Cache: mapId → vendor NPC locations
    private readonly Dictionary<int, List<NpcLocation>> _vendorsByMap = new();

    // Cache: mapId → innkeeper/flight master locations (town anchors)
    private readonly Dictionary<int, List<NpcLocation>> _townAnchorsByMap = new();

    private bool _loaded = false;

    public ZoneDataLoader(ConnectionFactory db, ILogger<ZoneDataLoader> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Preload zone and NPC data. Called once at startup.
    /// </summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;

        try
        {
            using var conn = _db.Mangos();

            // Load vendor NPC locations (npc_flags & 128 = vendor)
            // VMaNGOS creature table has: map, position_x/y/z but NO zone column.
            // We index by map and do distance-based lookups.
            var vendors = await conn.QueryAsync<NpcLocationRow>(@"
                SELECT c.map, c.position_x, c.position_y, c.position_z,
                       ct.entry AS npc_entry, ct.name AS npc_name, ct.npc_flags
                FROM creature c
                INNER JOIN creature_template ct ON c.id = ct.entry
                WHERE ct.npc_flags & 128 = 128
                  AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)");

            int vendorCount = 0;
            foreach (var v in vendors)
            {
                if (!_vendorsByMap.ContainsKey(v.map))
                    _vendorsByMap[v.map] = new List<NpcLocation>();

                _vendorsByMap[v.map].Add(new NpcLocation
                {
                    MapId = v.map,
                    ZoneId = 0, // not available from creature table
                    X = v.position_x,
                    Y = v.position_y,
                    Z = v.position_z,
                    NpcEntry = v.npc_entry,
                    NpcName = v.npc_name,
                    CanRepair = (v.npc_flags & 4096) != 0 // UNIT_NPC_FLAG_REPAIR
                });
                vendorCount++;
            }

            // Load innkeepers (npc_flags & 65536 = innkeeper) as town anchors
            var innkeepers = await conn.QueryAsync<NpcLocationRow>(@"
                SELECT c.map, c.position_x, c.position_y, c.position_z,
                       ct.entry AS npc_entry, ct.name AS npc_name, ct.npc_flags
                FROM creature c
                INNER JOIN creature_template ct ON c.id = ct.entry
                WHERE ct.npc_flags & 65536 = 65536
                  AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)");

            int innkeeperCount = 0;
            foreach (var i in innkeepers)
            {
                if (!_townAnchorsByMap.ContainsKey(i.map))
                    _townAnchorsByMap[i.map] = new List<NpcLocation>();

                _townAnchorsByMap[i.map].Add(new NpcLocation
                {
                    MapId = i.map,
                    ZoneId = 0,
                    X = i.position_x,
                    Y = i.position_y,
                    Z = i.position_z,
                    NpcEntry = i.npc_entry,
                    NpcName = i.npc_name
                });
                innkeeperCount++;
            }

            _loaded = true;
            _logger.LogInformation(
                "ZoneDataLoader: cached {VCount} vendors across {VMaps} maps, {ICount} innkeepers across {IMaps} maps",
                vendorCount, _vendorsByMap.Count, innkeeperCount, _townAnchorsByMap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZoneDataLoader: failed to load NPC data");
        }
    }

    /// <summary>
    /// Get the nearest vendor to a position on the same map, within a level-appropriate
    /// distance cap. Prefers repair-capable vendors when one is available within 1.5x
    /// the distance of the absolute nearest vendor — this way bots get their gear
    /// repaired as part of normal vendoring without traveling much further.
    ///
    /// Session 26 fix: previously returned ANY vendor on same map with no distance cap.
    /// Session 32: repair vendor preference added.
    /// </summary>
    public NpcLocation? GetNearestVendor(int zoneId, int mapId, float x, float y, int botLevel = 60)
    {
        if (!_vendorsByMap.TryGetValue(mapId, out var vendors))
            return null;

        float maxDist = ZoneSafetyMap.GetMaxTravelDistance(botLevel, zoneId);
        float maxDistSq = maxDist * maxDist;

        var inRange = vendors
            .Where(v => DistSq(v.X, v.Y, x, y) <= maxDistSq)
            .OrderBy(v => DistSq(v.X, v.Y, x, y))
            .ToList();

        if (inRange.Count == 0)
            return null;

        var nearest = inRange[0];
        float nearestDistSq = DistSq(nearest.X, nearest.Y, x, y);

        // If nearest can already repair, perfect
        if (nearest.CanRepair)
            return nearest;

        // Look for a repair vendor within 1.5x the distance of the nearest vendor.
        // A bot shouldn't walk 3x as far just for repair, but a small detour is worth it.
        float repairThresholdSq = nearestDistSq * 2.25f; // 1.5x distance → 2.25x squared
        var nearestRepair = inRange.FirstOrDefault(v => v.CanRepair && DistSq(v.X, v.Y, x, y) <= repairThresholdSq);

        return nearestRepair ?? nearest;
    }

    /// <summary>
    /// Is the bot near a town? (Within 150 yards of an innkeeper on same map.)
    /// </summary>
    public bool IsNearTown(int zoneId, int mapId, float x, float y)
    {
        if (!_townAnchorsByMap.TryGetValue(mapId, out var anchors))
            return false;

        float nearRange = 150f * 150f; // 150 yards squared
        return anchors.Any(a => DistSq(a.X, a.Y, x, y) < nearRange);
    }

    /// <summary>
    /// Get a random interesting point near the bot's current position.
    /// Uses vendor/innkeeper locations on the same map as POIs.
    /// Falls back to all map POIs if mapId has entries.
    /// </summary>
    public NpcLocation? GetRandomPointOfInterest(int zoneId, int mapId = -1)
    {
        var candidates = new List<NpcLocation>();

        if (mapId >= 0)
        {
            // Prefer same-map POIs
            if (_vendorsByMap.TryGetValue(mapId, out var vendors))
                candidates.AddRange(vendors);
            if (_townAnchorsByMap.TryGetValue(mapId, out var anchors))
                candidates.AddRange(anchors);
        }

        // Fallback: try all maps (original behavior for callers without mapId)
        if (candidates.Count == 0)
        {
            foreach (var list in _vendorsByMap.Values)
                candidates.AddRange(list);
            foreach (var list in _townAnchorsByMap.Values)
                candidates.AddRange(list);
        }

        if (candidates.Count == 0) return null;

        return candidates[Core.WeightedRoller.RangeInt(0, candidates.Count - 1)];
    }

    private static float DistSq(float x1, float y1, float x2, float y2)
    {
        float dx = x1 - x2, dy = y1 - y2;
        return dx * dx + dy * dy;
    }
}

// Internal row DTO for Dapper
internal class NpcLocationRow
{
    public int map { get; set; }
    public float position_x { get; set; }
    public float position_y { get; set; }
    public float position_z { get; set; }
    public int npc_entry { get; set; }
    public string npc_name { get; set; } = "";
    public int npc_flags { get; set; }
}

public class NpcLocation
{
    public int MapId { get; set; }
    public int ZoneId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int NpcEntry { get; set; }
    public string NpcName { get; set; } = "";
    public bool CanRepair { get; set; }
}

public class ZoneInfo
{
    public int ZoneId { get; set; }
    public string Name { get; set; } = "";
    public int MapId { get; set; }
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; }
}