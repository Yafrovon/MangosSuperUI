namespace MangosSuperUI.Services.WorldPacks;

/// <summary>Legacy deltas shape source terrain; surface deltas are the last geometric build step.</summary>
public static class WorldPackSculptLayers
{
    public static string Table(bool surface) => surface ? "wp_surface_sculpt" : "wp_sculpt";

    public static SculptPayload Relocate(SculptPayload source, int toMap, int dcol, int drow)
    {
        var result = new SculptPayload
        {
            MapId = toMap, Surface = source.Surface,
            Tiles = source.Tiles.Select(t => new SculptTile
            {
                Col = t.Col + dcol, Row = t.Row + drow, Deltas = new(t.Deltas),
            }).ToList(),
        };
        if (result.Tiles.Any(t => t.Col is < 0 or > 63 || t.Row is < 0 or > 63))
            throw new ArgumentException("the moved sculpt leaves the 64x64 tile grid");
        if (result.Surface) result.Tiles = SurfaceStroke(result.Tiles);
        return result;
    }

    /// <summary>A shared outer vertex is one physical point. Mirror a one-sided API edit to its neighbours;
    /// duplicate samples from the brush must agree, rather than being added twice.</summary>
    public static List<SculptTile> SurfaceStroke(IEnumerable<SculptTile> tiles)
    {
        var vertices = new Dictionary<(int col, int row), float>();
        foreach (var tile in tiles)
            foreach (var (index, delta) in tile.Deltas)
            {
                var key = (tile.Col * 128 + index % 129, tile.Row * 128 + index / 129);
                if (vertices.TryGetValue(key, out float previous) && Math.Abs(previous - delta) > .0001f)
                    throw new ArgumentException("surface stroke has conflicting deltas at a shared tile vertex");
                vertices[key] = delta;
            }
        var result = new Dictionary<(int col, int row), SculptTile>();
        foreach (var (point, delta) in vertices)
        {
            int col = point.col / 128, row = point.row / 128;
            foreach (int c in point.col % 128 == 0 ? new[] { col - 1, col } : new[] { col })
                foreach (int r in point.row % 128 == 0 ? new[] { row - 1, row } : new[] { row })
                {
                    if (c is < 0 or > 63 || r is < 0 or > 63) continue;
                    if (!result.TryGetValue((c, r), out var tile)) result[(c, r)] = tile = new() { Col = c, Row = r };
                    tile.Deltas[(point.row - r * 128) * 129 + point.col - c * 128] = delta;
                }
        }
        return result.OrderBy(x => x.Key).Select(x => x.Value).ToList();
    }

    public static void ApplySurface(Dictionary<(int map, int col, int row), AdtDocument> built,
        IReadOnlyDictionary<(int map, int col, int row), Dictionary<int, float>> surface)
    {
        foreach (var (key, deltas) in surface)
            if (built.TryGetValue(key, out var adt)) adt.ApplySculpt(deltas);
    }

    public static Dictionary<(int map, int col, int row), Dictionary<int, float>> Totals(
        IReadOnlyDictionary<(int map, int col, int row), Dictionary<int, float>> source,
        IReadOnlyDictionary<(int map, int col, int row), Dictionary<int, float>> surface)
    {
        var result = source.ToDictionary(kv => kv.Key, kv => new Dictionary<int, float>(kv.Value));
        foreach (var (key, values) in surface)
        {
            if (!result.TryGetValue(key, out var target)) result[key] = target = new();
            foreach (var (index, delta) in values) target[index] = target.GetValueOrDefault(index) + delta;
        }
        return result;
    }
}
