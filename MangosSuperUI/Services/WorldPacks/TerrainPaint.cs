using System.Numerics;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// Ground texture painting for one ADT chunk (World Builder road surfaces; MSUIClient shared_docs/WORLD_BUILDER.md).
/// A VERBATIM port of MSUIClient Engine/UI/TerrainPaintLaw.cs: the client paints its live preview with that class,
/// so any change here must be made there too (WorldPackPaintTests pins the shared behaviour).
///
/// A vanilla chunk has up to 4 texture layers. Layer 0 is the base; layers 1-3 each carry a 64x64 alpha that blends OVER
/// everything below it. Painting works in VISIBLE-WEIGHT space (how much of each texture shows at a pixel; weights sum
/// to 1), so the result does not depend on layer order: raise the painted texture's weight, scale the others down, then
/// convert back to per-layer alphas in the existing order. A texture the chunk lacks takes a free slot, or replaces the
/// least-visible layer (its weight goes to the strongest remaining layer at each pixel). Alphas are quantized to the
/// 4-bit precision vanilla MCAL stores (value * 17 in 8-bit terms).
///
/// Alpha layout: [row * 64 + column]; the row runs along world X (north to south), the column along world Y.
/// </summary>
public static class TerrainPaint
{
    public const int AlphaSize = 64, Pixels = AlphaSize * AlphaSize, MaxLayers = 4;
    public const float ChunkSize = WorldCoords.Tile / 16f;

    /// <summary>World position (X, Y) of an alpha pixel's centre. Chunk (ix, iy): ix runs along world Y, iy along world X.</summary>
    public static Vector2 PixelWorld(int col, int row, int chunkX, int chunkY, int pixelRow, int pixelCol) =>
        new((32 - row) * WorldCoords.Tile - chunkY * ChunkSize - (pixelRow + 0.5f) * ChunkSize / AlphaSize,
            (32 - col) * WorldCoords.Tile - chunkX * ChunkSize - (pixelCol + 0.5f) * ChunkSize / AlphaSize);

    /// <summary>A road's paint amount at a distance from its centre line: full within half the width, smoothstep to 0 across the soft edge.</summary>
    public static float RoadAmount(float distance, float width, float softEdge, float strength)
    {
        float half = width / 2f;
        if (distance <= half) return strength;
        if (distance >= half + softEdge) return 0f;
        float t = (distance - half) / MathF.Max(softEdge, 0.001f);
        return strength * (1f - t * t * (3f - 2f * t));
    }

    /// <summary>Visible weight of every layer at every pixel. <paramref name="alphas"/>[0] (the base) is ignored.</summary>
    public static float[][] Weights(IReadOnlyList<byte[]?> alphas)
    {
        int n = alphas.Count;
        var w = new float[n][];
        for (int i = 0; i < n; i++) w[i] = new float[Pixels];
        for (int p = 0; p < Pixels; p++)
        {
            float remaining = 1f;
            for (int i = n - 1; i >= 1; i--)
            {
                float a = alphas[i] is { Length: >= Pixels } map ? map[p] / 255f : 0f;
                w[i][p] = a * remaining;
                remaining *= 1f - a;
            }
            if (n > 0) w[0][p] = remaining;
        }
        return w;
    }

    /// <summary>Per-layer alphas (layer 0 null) reproducing the weights in the given order, 4-bit quantized.</summary>
    public static byte[]?[] Alphas(float[][] weights)
    {
        int n = weights.Length;
        var alphas = new byte[]?[n];
        for (int i = 1; i < n; i++) alphas[i] = new byte[Pixels];
        for (int p = 0; p < Pixels; p++)
        {
            float remaining = 1f;
            for (int i = n - 1; i >= 1; i--)
            {
                float a = remaining > 1e-6f ? Math.Clamp(weights[i][p] / remaining, 0f, 1f) : 0f;
                int q = (int)MathF.Round(a * 15f);          // 4-bit, like vanilla MCAL
                alphas[i]![p] = (byte)(q * 17);
                remaining *= 1f - q / 15f;
            }
        }
        return alphas;
    }

    /// <summary>
    /// Paint <paramref name="texture"/> into one chunk. <paramref name="textures"/> and <paramref name="alphas"/> are the
    /// chunk's layers bottom first (same length; alphas[0] unused) and are replaced in place. <paramref name="amount"/>
    /// (pixelRow, pixelCol) gives 0..1 per pixel. Returns false (lists untouched) when nothing is painted.
    /// </summary>
    public static bool PaintChunk(List<int> textures, List<byte[]?> alphas, int texture, Func<int, int, float> amount)
    {
        var paint = new float[Pixels];
        bool any = false;
        for (int r = 0; r < AlphaSize; r++)
            for (int c = 0; c < AlphaSize; c++)
                if (Math.Clamp(amount(r, c), 0f, 1f) is var a && a > 0f) { paint[r * AlphaSize + c] = a; any = true; }
        if (!any) return false;

        var weights = Weights(alphas).ToList();
        var tex = textures.ToList();
        if (tex.Count == 0) { tex.Add(texture); weights.Add(Filled(1f)); }
        int slot = tex.IndexOf(texture);
        if (slot < 0)
        {
            if (tex.Count < MaxLayers) { tex.Add(texture); weights.Add(new float[Pixels]); slot = tex.Count - 1; }
            else
            {
                // Replace the least-visible layer; each pixel's share goes to the strongest remaining layer there.
                slot = Enumerable.Range(0, tex.Count).OrderBy(i => weights[i].Sum()).First();
                for (int p = 0; p < Pixels; p++)
                {
                    int best = -1;
                    for (int i = 0; i < tex.Count; i++)
                        if (i != slot && (best < 0 || weights[i][p] > weights[best][p])) best = i;
                    weights[best][p] += weights[slot][p];
                    weights[slot][p] = 0f;
                }
                tex[slot] = texture;
            }
        }
        for (int p = 0; p < Pixels; p++)
        {
            float a = paint[p];
            if (a <= 0f) continue;
            float old = weights[slot][p], now = old + (1f - old) * a;
            float scale = old < 1f - 1e-6f ? (1f - now) / (1f - old) : 0f;
            for (int i = 0; i < tex.Count; i++) if (i != slot) weights[i][p] *= scale;
            weights[slot][p] = now;
        }
        var result = Alphas(weights.ToArray());
        textures.Clear(); textures.AddRange(tex);
        alphas.Clear(); alphas.AddRange(result);
        return true;
    }

    private static float[] Filled(float v) { var a = new float[Pixels]; Array.Fill(a, v); return a; }
}
