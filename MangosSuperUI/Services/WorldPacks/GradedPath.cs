using System.Numerics;
using System.Text.Json.Nodes;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// A graded path (doc kind "path"): a polyline of (x, y, z) world points. Within half the width the ground
/// takes the path's height (linear between the waypoints); beyond it blends back over the falloff
/// (smoothstep). A pass through a ridge, a land bridge across a strait, a road. Applied by the build after
/// seams are stitched, so the result is exact and the same on both sides of every tile border it crosses.
/// Clear (default on): the props standing IN the lane (half the width) are removed - a road has no tree in it
/// (2026-09-27 live: the first walk of greymane-pass stopped at a trunk the grading had left standing).
/// Grade off ("grade": false): a CLEARED LANE - the props in it go, the ground stays exactly as it is. A narrow
/// lane graded onto the ~4-yd ADT grid tilts its own triangles (WoW Karting, 2026-10-04: a 42-degree sideways
/// facet inside an 8-yd lane stopped a kart where the ungraded road had been fine).
/// </summary>
public sealed record GradedPath(string Name, int Map, List<Vector3> Points, float Width, float Falloff, bool Clear = true, bool Grade = true)
{
    public static GradedPath Parse(string name, string body)
    {
        var b = JsonNode.Parse(body)!.AsObject();
        var pts = b["points"]!.AsArray().Select(p => new Vector3((float)p![0]!, (float)p[1]!, (float)p[2]!)).ToList();
        return new GradedPath(name, (int)b["map"]!, pts, (float)b["width"]!, (float?)b["falloff"] ?? 20f, (bool?)b["clear"] ?? true,
            (bool?)b["grade"] ?? true);
    }

    public float Reach => Width / 2f + Falloff;

    /// <summary>A placement-space prop position stands in the lane (within half the width of the polyline).</summary>
    public bool InLane(Vector3 placement)
    {
        var w = WorldCoords.PlacementToWorld(placement);
        return Nearest(new Vector2(w.X, w.Y)).Distance < Width / 2f;
    }

    /// <summary>Distance in the ground plane to the polyline, and the path's height at the closest point.</summary>
    public (float Distance, float Z) Nearest(Vector2 p)
    {
        float best = float.MaxValue, z = Points[0].Z;
        for (int i = 0; i + 1 < Points.Count; i++)
        {
            var a = new Vector2(Points[i].X, Points[i].Y);
            var ab = new Vector2(Points[i + 1].X, Points[i + 1].Y) - a;
            float len2 = ab.LengthSquared();
            float t = len2 < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            float d = Vector2.Distance(p, a + ab * t);
            if (d < best) { best = d; z = Points[i].Z + (Points[i + 1].Z - Points[i].Z) * t; }
        }
        return (best, z);
    }

    /// <summary>Tiles the path can reach.</summary>
    public IEnumerable<(int col, int row)> Tiles()
    {
        float minX = Points.Min(p => p.X) - Reach, maxX = Points.Max(p => p.X) + Reach;
        float minY = Points.Min(p => p.Y) - Reach, maxY = Points.Max(p => p.Y) + Reach;
        for (int col = WorldCoords.TileCol(maxY); col <= WorldCoords.TileCol(minY); col++)
            for (int row = WorldCoords.TileRow(maxX); row <= WorldCoords.TileRow(minX); row++)
                yield return (col, row);
    }

    /// <summary>Sculpt deltas (129x129 outer grid) that grade one tile whose current heights are given.</summary>
    public Dictionary<int, float> Deltas(int col, int row, float[] heights)
    {
        var deltas = new Dictionary<int, float>();
        float unit = WorldCoords.Tile / 128f, half = Width / 2f;
        float x0 = (32 - row) * WorldCoords.Tile, y0 = (32 - col) * WorldCoords.Tile;
        for (int gr = 0; gr <= 128; gr++)
            for (int gc = 0; gc <= 128; gc++)
            {
                var (d, z) = Nearest(new Vector2(x0 - gr * unit, y0 - gc * unit));
                if (d >= Reach) continue;
                float t = d <= half ? 0f : (d - half) / Math.Max(Falloff, 0.001f), w = 1f - t * t * (3f - 2f * t);
                float delta = (z - heights[gr * 129 + gc]) * w;
                if (MathF.Abs(delta) > 0.001f) deltas[gr * 129 + gc] = delta;
            }
        return deltas;
    }
}
