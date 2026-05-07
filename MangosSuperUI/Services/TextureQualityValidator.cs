using SkiaSharp;

namespace MangosSuperUI.Services;

/// <summary>
/// Session 26: Texture quality diagnostics.
///
/// Measures every generated texture against vanilla-derived constraints
/// and logs the results to the trace JSON. Does NOT modify the texture —
/// gradient clamping (the actual fix) is handled in SpellTextureService.
///
/// Metrics measured:
///   - Fill %: fraction of pixels with luminance > 5
///   - Avg/Max luminance: brightness distribution
///   - Edge harshness: average RGB bounding-box diagonal of 4×4 "edge blocks"
///   - Atlas per-cell fill (for atlas textures)
///
/// All thresholds are empirically derived from vanilla BLP measurements
/// (Session 25 forensic comparison).
/// </summary>
public class TextureQualityValidator
{
    private readonly ILogger<TextureQualityValidator>? _logger;

    public TextureQualityValidator(ILogger<TextureQualityValidator>? logger = null)
    {
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════
    // VANILLA-DERIVED THRESHOLDS
    // ═══════════════════════════════════════════════════════════════════

    public const float FillLuminanceThreshold = 5f;
    public const float Dxt1BodyMinFill = 0.40f;
    public const float Dxt3AtlasMinFill = 0.25f;
    public const float RingMinFill = 0.20f;
    public const float AtlasMinAvgLuminance = 25f;
    public const float Dxt1BodyMinAvgLuminance = 20f;
    public const float MaxEdgeHarshness = 160f;

    // ═══════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Measure a texture against vanilla-derived constraints.
    /// Does NOT modify the bitmap. Returns a diagnostic report.
    /// </summary>
    public QualityReport Measure(SKBitmap bitmap,
        SpellTextureService.TextureRole role,
        SpellTextureService.TextureDensity density,
        bool isDxt1)
    {
        var report = new QualityReport
        {
            Role = role.ToString(),
            Density = density.ToString(),
            IsDxt1 = isDxt1,
            Width = bitmap.Width,
            Height = bitmap.Height,
        };

        var pixels = bitmap.Pixels;
        int w = bitmap.Width;
        int h = bitmap.Height;

        report.FillPercent = MeasureFill(pixels);
        report.AvgLuminance = MeasureAvgLuminance(pixels);
        report.MaxLuminance = MeasureMaxLuminance(pixels);
        report.EdgeHarshness = MeasureEdgeHarshness(pixels, w, h);

        if (role == SpellTextureService.TextureRole.Atlas)
        {
            int gridSize = w >= 256 ? 8 : 4;
            report.AtlasCellFills = MeasureAtlasCellFills(pixels, w, h, gridSize);
            report.AtlasGridSize = gridSize;
            report.AtlasMinCellFill = report.AtlasCellFills.Min();
            report.AtlasAvgCellFill = report.AtlasCellFills.Average();
        }

        // Pass/fail evaluation
        report.Pass = true;

        if (isDxt1 && role == SpellTextureService.TextureRole.Shape
            && density == SpellTextureService.TextureDensity.FullCoverage)
        {
            if (report.FillPercent < Dxt1BodyMinFill)
                report.AddFailure($"DXT1 body fill {report.FillPercent:P0} < {Dxt1BodyMinFill:P0}");
            if (report.AvgLuminance < Dxt1BodyMinAvgLuminance)
                report.AddFailure($"DXT1 body avgLum {report.AvgLuminance:F1} < {Dxt1BodyMinAvgLuminance}");
        }

        if (report.EdgeHarshness > MaxEdgeHarshness)
            report.AddFailure($"Edge harshness {report.EdgeHarshness:F0} > {MaxEdgeHarshness}");

        if (!isDxt1 && role == SpellTextureService.TextureRole.Atlas)
        {
            if (report.FillPercent < Dxt3AtlasMinFill)
                report.AddFailure($"Atlas fill {report.FillPercent:P0} < {Dxt3AtlasMinFill:P0}");
            if (report.AvgLuminance < AtlasMinAvgLuminance)
                report.AddFailure($"Atlas avgLum {report.AvgLuminance:F1} < {AtlasMinAvgLuminance}");
        }

        if (role == SpellTextureService.TextureRole.Ring && report.FillPercent < RingMinFill)
            report.AddFailure($"Ring fill {report.FillPercent:P0} < {RingMinFill:P0}");

        string status = report.Pass ? "PASS" : "FAIL";
        _logger?.LogInformation(
            "Quality: [{W}×{H} {Fmt}] {Role}/{Density} → {Status} | fill={Fill:P0} avgLum={Lum:F1} edgeHarsh={Edge:F0}",
            w, h, isDxt1 ? "DXT1" : "DXT3", role, density, status,
            report.FillPercent, report.AvgLuminance, report.EdgeHarshness);

        return report;
    }

    // ═══════════════════════════════════════════════════════════════════
    // MEASUREMENT FUNCTIONS
    // ═══════════════════════════════════════════════════════════════════

    private static float MeasureFill(SKColor[] pixels)
    {
        int filled = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            float lum = 0.299f * pixels[i].Red + 0.587f * pixels[i].Green + 0.114f * pixels[i].Blue;
            if (lum > FillLuminanceThreshold) filled++;
        }
        return (float)filled / pixels.Length;
    }

    private static float MeasureAvgLuminance(SKColor[] pixels)
    {
        double sum = 0;
        for (int i = 0; i < pixels.Length; i++)
            sum += 0.299f * pixels[i].Red + 0.587f * pixels[i].Green + 0.114f * pixels[i].Blue;
        return (float)(sum / pixels.Length);
    }

    private static float MeasureMaxLuminance(SKColor[] pixels)
    {
        float max = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            float lum = 0.299f * pixels[i].Red + 0.587f * pixels[i].Green + 0.114f * pixels[i].Blue;
            if (lum > max) max = lum;
        }
        return max;
    }

    private static float MeasureEdgeHarshness(SKColor[] pixels, int w, int h)
    {
        int blocksX = Math.Max(1, (w + 3) / 4);
        int blocksY = Math.Max(1, (h + 3) / 4);
        float total = 0;
        int count = 0;

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                byte rMin = 255, gMin = 255, bMin = 255;
                byte rMax = 0, gMax = 0, bMax = 0;
                float lumMin = 255, lumMax = 0;

                for (int py = 0; py < 4; py++)
                {
                    for (int px = 0; px < 4; px++)
                    {
                        int sx = Math.Min(bx * 4 + px, w - 1);
                        int sy = Math.Min(by * 4 + py, h - 1);
                        var c = pixels[sy * w + sx];
                        if (c.Red < rMin) rMin = c.Red;
                        if (c.Red > rMax) rMax = c.Red;
                        if (c.Green < gMin) gMin = c.Green;
                        if (c.Green > gMax) gMax = c.Green;
                        if (c.Blue < bMin) bMin = c.Blue;
                        if (c.Blue > bMax) bMax = c.Blue;
                        float lum = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
                        if (lum < lumMin) lumMin = lum;
                        if (lum > lumMax) lumMax = lum;
                    }
                }

                if (lumMin <= 10f && lumMax >= 30f)
                {
                    int dr = rMax - rMin, dg = gMax - gMin, db = bMax - bMin;
                    total += MathF.Sqrt(dr * dr + dg * dg + db * db);
                    count++;
                }
            }
        }

        return count > 0 ? total / count : 0;
    }

    private static float[] MeasureAtlasCellFills(SKColor[] pixels, int w, int h, int gridSize)
    {
        float cellW = (float)w / gridSize;
        float cellH = (float)h / gridSize;
        var fills = new float[gridSize * gridSize];

        for (int row = 0; row < gridSize; row++)
        {
            for (int col = 0; col < gridSize; col++)
            {
                int startX = (int)(col * cellW), endX = (int)((col + 1) * cellW);
                int startY = (int)(row * cellH), endY = (int)((row + 1) * cellH);
                int filled = 0, total = 0;

                for (int y = startY; y < endY && y < h; y++)
                {
                    for (int x = startX; x < endX && x < w; x++)
                    {
                        var c = pixels[y * w + x];
                        if (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue > FillLuminanceThreshold)
                            filled++;
                        total++;
                    }
                }

                fills[row * gridSize + col] = total > 0 ? (float)filled / total : 0;
            }
        }

        return fills;
    }
}

// ═══════════════════════════════════════════════════════════════════════════

public class QualityReport
{
    public string Role { get; set; } = "";
    public string Density { get; set; } = "";
    public bool IsDxt1 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public float FillPercent { get; set; }
    public float AvgLuminance { get; set; }
    public float MaxLuminance { get; set; }
    public float EdgeHarshness { get; set; }

    public float[]? AtlasCellFills { get; set; }
    public int AtlasGridSize { get; set; }
    public float AtlasMinCellFill { get; set; }
    public float AtlasAvgCellFill { get; set; }

    public bool Pass { get; set; }
    public List<string> Failures { get; set; } = new();

    public void AddFailure(string msg)
    {
        Pass = false;
        Failures.Add(msg);
    }
}
