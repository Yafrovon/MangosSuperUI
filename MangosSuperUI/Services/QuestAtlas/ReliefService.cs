using System.Collections.Concurrent;

namespace MangosSuperUI.Services.QuestAtlas;

/// <summary>
/// Compiles the Quest Atlas tabletop relief: a coarse heightfield per ADT tile read straight from
/// the client MPQs (every 4th outer MCVT vertex → 33×33 samples, ~16.7 yd apart) plus a per-chunk
/// water level. Cached in memory for the life of the process; nothing is written to disk.
///
/// Grid law (same as WorldEditorController / AdtTerrainReader):
///   gx = floor(32 - worldX / 533.33)   (row of tiles, north → south)
///   gy = floor(32 - worldY / 533.33)   (column of tiles, west → east)
///   sample (i, j): i walks west → east (worldY falling), j walks north → south (worldX falling).
/// The minimap tile map{gy}_{gx} covers the same square with the same orientation.
/// </summary>
public class ReliefService
{
    public const float TileYards = 533.33333f;
    public const int Samples = 33;                       // per side, per tile
    private const float Cell = TileYards / 128f;         // outer-vertex spacing
    private const int Step = 4;                          // 128 / 4 = 32 intervals

    private readonly IConfiguration _config;
    private readonly ILogger<ReliefService> _logger;
    private readonly ConcurrentDictionary<(int map, int gx, int gy), TileRelief?> _cache = new();

    public ReliefService(IConfiguration config, ILogger<ReliefService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public record TileRelief(
        int MapId, int Gx, int Gy,
        float NorthX, float WestY,
        float MinZ, float MaxZ,
        float[] Heights,          // Samples × Samples, row-major [j * Samples + i]
        float?[] Water,           // 16 × 16 chunk water level, row-major [chunkRow * 16 + chunkCol]
        List<Placement> Wmos,     // buildings (MODF) referenced by this tile
        List<Placement> Doodads); // props/trees (MDDF) referenced by this tile

    /// <summary>
    /// A static model placed by the ADT, in WORLD coordinates (X north, Y west, Z up).
    /// RotY is the raw MODF/MDDF yaw in degrees, used exactly as the 3D World Editor uses it
    /// (WMO: yaw as-is; M2 doodad: yaw − 90°). A WMO spanning tiles appears in each tile that
    /// references it — the page de-duplicates.
    /// </summary>
    public record Placement(string Model, float X, float Y, float Z, float RotY, float Scale, string Kind);

    // ADT placement space → world: posX runs with -worldY, posZ with -worldX, posY is height.
    private const float AdtOrigin = 32 * TileYards;

    public static string? MapName(int mapId) => mapId switch
    {
        0 => "Azeroth",
        1 => "Kalimdor",
        _ => null
    };

    public TileRelief? GetTile(int mapId, int gx, int gy)
    {
        if (gx < 0 || gx > 63 || gy < 0 || gy > 63) return null;
        return _cache.GetOrAdd((mapId, gx, gy), k => Compile(k.map, k.gx, k.gy));
    }

    private TileRelief? Compile(int mapId, int gx, int gy)
    {
        var mapName = MapName(mapId);
        var clientData = ClientDataPath();
        if (mapName == null || clientData == null) return null;

        AdtTerrainReader.AdtResult? adt;
        try { adt = AdtTerrainReader.ReadFromMpq(clientData, mapName, gx, gy); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuestAtlas relief: ADT {Map} ({GX},{GY}) failed", mapName, gx, gy);
            return null;
        }
        if (adt?.Chunks == null || adt.Chunks.Length == 0) return null;

        float northX = (32 - gx) * TileYards;
        float westY = (32 - gy) * TileYards;

        // Full-resolution outer grid first (129 × 129), then decimate. Chunks are placed by their
        // own world origin, so the chunk index order in the file does not matter.
        const int Full = 129;
        var full = new float[Full * Full];
        var have = new bool[Full * Full];
        var water = new float?[256];

        // The MCNK header stores the origin in an order the reader's own comments disagree on
        // (and a wrong guess drops every chunk off the tile). Decide per tile from the data:
        // whichever reading puts the chunk origins inside this tile's square is the right one.
        bool InTile(float north, float west) =>
            north <= northX + 1 && north >= northX - TileYards - 1 && west <= westY + 1 && west >= westY - TileYards - 1;
        int straight = adt.Chunks.Count(ch => InTile(ch.BaseX, ch.BaseY));
        int swapped = adt.Chunks.Count(ch => InTile(ch.BaseY, ch.BaseX));
        bool swap = swapped > straight;

        foreach (var ch in adt.Chunks)
        {
            if (ch.Heights == null) continue;
            float originNorth = swap ? ch.BaseY : ch.BaseX;
            float originWest = swap ? ch.BaseX : ch.BaseY;
            int c0 = (int)MathF.Round((westY - originWest) / Cell);
            int r0 = (int)MathF.Round((northX - originNorth) / Cell);
            for (int r = 0; r <= 8; r++)
            for (int c = 0; c <= 8; c++)
            {
                int fi = c0 + c, fj = r0 + r;
                if ((uint)fi >= Full || (uint)fj >= Full) continue;
                full[fj * Full + fi] = ch.WorldHeightAt(c, r);
                have[fj * Full + fi] = true;
            }

            if (ch.Liquid is { Count: > 0 })
            {
                int cc = c0 / 8, cr = r0 / 8;
                if ((uint)cc < 16 && (uint)cr < 16)
                {
                    var l = ch.Liquid[0];
                    water[cr * 16 + cc] = l.MaxHeight;
                }
            }
        }

        var heights = new float[Samples * Samples];
        float min = float.MaxValue, max = float.MinValue;
        for (int j = 0; j < Samples; j++)
        for (int i = 0; i < Samples; i++)
        {
            int fi = i * Step, fj = j * Step;
            float h = have[fj * Full + fi] ? full[fj * Full + fi] : float.NaN;
            heights[j * Samples + i] = h;
            if (!float.IsNaN(h)) { if (h < min) min = h; if (h > max) max = h; }
        }
        if (min == float.MaxValue)
        {
            _logger.LogWarning("QuestAtlas relief: ADT {Map} ({GX},{GY}) had no chunk inside the tile (straight {S}, swapped {W})",
                mapName, gx, gy, straight, swapped);
            return null;
        }

        // Fill any gap (chunk without MCVT) with the tile minimum so the mesh stays closed.
        for (int k = 0; k < heights.Length; k++)
            if (float.IsNaN(heights[k])) heights[k] = min;

        var wmos = (adt.Wmos ?? new()).Select(w => new Placement(
            w.ModelPath, AdtOrigin - w.PosZ, AdtOrigin - w.PosX, w.PosY, w.RotY, 1f, "wmo")).ToList();
        var doodads = (adt.Doodads ?? new()).Select(d => new Placement(
            d.ModelPath, AdtOrigin - d.PosZ, AdtOrigin - d.PosX, d.PosY, d.RotY, d.Scale,
            AdtTerrainReader.ClassifyDoodad(d.ModelPath))).ToList();

        return new TileRelief(mapId, gx, gy, northX, westY, min, max, heights, water, wmos, doodads);
    }

    private string? ClientDataPath()
    {
        foreach (var c in new[] { _config.GetValue<string>("Vmangos:ClientDataPath") ?? "", "/home/wowvmangos/wowclient/Data" })
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c;
        return null;
    }
}
