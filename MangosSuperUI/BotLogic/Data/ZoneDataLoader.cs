using Dapper;
using MangosSuperUI.Models;
using MangosSuperUI.Services;

namespace MangosSuperUI.BotLogic.Data;

/// <summary>
/// Zone metadata, NPC positions (vendors, trainers, flight masters),
/// and grind spot data from the mangos DB.
/// All data is cached after first load — zone geometry doesn't change at runtime.
/// </summary>
public class ZoneDataLoader
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<ZoneDataLoader> _logger;

    // Cache: zoneId → zone metadata
    private readonly Dictionary<int, ZoneInfo> _zones = new();

    // Cache: zoneId → vendor NPC locations
    private readonly Dictionary<int, List<NpcLocation>> _vendorsByZone = new();

    // Cache: zoneId → innkeeper/flight master locations (town anchors)
    private readonly Dictionary<int, List<NpcLocation>> _townAnchorsByZone = new();

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
            var vendors = await conn.QueryAsync<NpcLocationRow>(@"
                SELECT c.map, c.zone, c.position_x, c.position_y, c.position_z,
                       ct.entry AS npc_entry, ct.name AS npc_name, ct.npc_flags
                FROM creature c
                INNER JOIN creature_template ct ON c.id = ct.entry
                WHERE ct.npc_flags & 128 = 128
                  AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)
                  AND c.zone > 0");

            foreach (var v in vendors)
            {
                if (!_vendorsByZone.ContainsKey(v.zone))
                    _vendorsByZone[v.zone] = new List<NpcLocation>();

                _vendorsByZone[v.zone].Add(new NpcLocation
                {
                    MapId = v.map,
                    ZoneId = v.zone,
                    X = v.position_x,
                    Y = v.position_y,
                    Z = v.position_z,
                    NpcEntry = v.npc_entry,
                    NpcName = v.npc_name
                });
            }

            // Load innkeepers (npc_flags & 65536 = innkeeper) as town anchors
            var innkeepers = await conn.QueryAsync<NpcLocationRow>(@"
                SELECT c.map, c.zone, c.position_x, c.position_y, c.position_z,
                       ct.entry AS npc_entry, ct.name AS npc_name, ct.npc_flags
                FROM creature c
                INNER JOIN creature_template ct ON c.id = ct.entry
                WHERE ct.npc_flags & 65536 = 65536
                  AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)
                  AND c.zone > 0");

            foreach (var i in innkeepers)
            {
                if (!_townAnchorsByZone.ContainsKey(i.zone))
                    _townAnchorsByZone[i.zone] = new List<NpcLocation>();

                _townAnchorsByZone[i.zone].Add(new NpcLocation
                {
                    MapId = i.map,
                    ZoneId = i.zone,
                    X = i.position_x,
                    Y = i.position_y,
                    Z = i.position_z,
                    NpcEntry = i.npc_entry,
                    NpcName = i.npc_name
                });
            }

            _loaded = true;
            _logger.LogInformation("ZoneDataLoader: cached vendors in {VZones} zones, innkeepers in {IZones} zones",
                _vendorsByZone.Count, _townAnchorsByZone.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZoneDataLoader: failed to load NPC data");
        }
    }

    /// <summary>
    /// Get the nearest vendor to a position within a zone.
    /// </summary>
    public NpcLocation? GetNearestVendor(int zoneId, int mapId, float x, float y)
    {
        if (!_vendorsByZone.TryGetValue(zoneId, out var vendors))
            return null;

        return vendors
            .Where(v => v.MapId == mapId)
            .OrderBy(v => DistSq(v.X, v.Y, x, y))
            .FirstOrDefault();
    }

    /// <summary>
    /// Is the bot near a town? (Within 150 yards of an innkeeper.)
    /// </summary>
    public bool IsNearTown(int zoneId, int mapId, float x, float y)
    {
        if (!_townAnchorsByZone.TryGetValue(zoneId, out var anchors))
            return false;

        float nearRange = 150f * 150f; // 150 yards squared
        return anchors.Any(a => a.MapId == mapId && DistSq(a.X, a.Y, x, y) < nearRange);
    }

    /// <summary>
    /// Get a random interesting point near the bot's current position.
    /// Uses vendor/innkeeper locations as POIs since they indicate inhabited areas.
    /// </summary>
    public NpcLocation? GetRandomPointOfInterest(int zoneId)
    {
        var candidates = new List<NpcLocation>();

        if (_vendorsByZone.TryGetValue(zoneId, out var vendors))
            candidates.AddRange(vendors);
        if (_townAnchorsByZone.TryGetValue(zoneId, out var anchors))
            candidates.AddRange(anchors);

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
    public int zone { get; set; }
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
}

public class ZoneInfo
{
    public int ZoneId { get; set; }
    public string Name { get; set; } = "";
    public int MapId { get; set; }
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; }
}
