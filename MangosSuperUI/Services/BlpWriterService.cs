using SkiaSharp;

namespace MangosSuperUI.Services;

/// <summary>
/// Converts PNG → BLP2 for vanilla WoW 1.12.1 particle textures and icons.
/// Supports both DXT3 (alpha textures) and DXT1 (no-alpha additive textures).
///
/// ═══════════════════════════════════════════════════════════════════════
/// Session 17-18: DXT3 encoder with dual-mode vignette.
/// Session 22: Vanilla BLP reference system (dimensions from VanillaBlpService).
/// Session 24: DXT1 encoder for vanilla DXT1 textures (alphaDepth=0).
///             Non-square BLP support (e.g. Ribbon 64×32).
/// Session 27: POST-RESIZE PROCESSING
///             All post-processing (lum-alpha, vignette, gradient clamping)
///             now happens at FINAL vanilla resolution, not at FLUX 512×512.
///             New pixel-array methods: ApplyLuminanceAlphaPixels,
///             ApplyRadialVignettePixels, ApplyRadialVignetteRgbPixels,
///             EncodeBitmapToBlp (direct encode, no resize).
///             Old PNG-file methods retained for backward compatibility.
/// Session 28: BRIGHTNESS FLOOR MASK — Content-Aware Alpha Carving
///             Replaces geometric radial vignette for particle textures.
///             Analyzes actual pixel brightness and kills everything below
///             a percentage of peak. Particle shape follows content, not a circle.
///             ApplyBrightnessFloorMask + ApplyAtlasBrightnessFloorMask.
///
/// BLP2 HEADER LAYOUT (1172 bytes total):
///     [0..3]    ident       = "BLP2"
///     [4..7]    type        = 1   (direct content)
///     [8]       compression = 2   (DXT)
///     [9]       alpha_depth = 0 (DXT1) or 8 (DXT3)
///     [10]      alpha_type  = 0 (DXT1) or 1 (DXT3)
///     [11]      has_mips    = 1
///     [12..15]  width
///     [16..19]  height
///     [20..83]  mipmap_offsets[16]
///     [84..147] mipmap_lengths[16]
///     [148..1171] palette[256] (unused for DXT, zero-filled)
/// ═══════════════════════════════════════════════════════════════════════
/// </summary>
public class BlpWriterService
{
    private const int HEADER_SIZE = 148;        // before palette
    private const int PALETTE_BYTES = 1024;     // unused for DXT but still allocated
    private const int DATA_START = HEADER_SIZE + PALETTE_BYTES;  // 1172
    private const int DXT3_BLOCK_BYTES = 16;
    private const int DXT1_BLOCK_BYTES = 8;

    private readonly ILogger<BlpWriterService>? _logger;

    public BlpWriterService(ILogger<BlpWriterService>? logger = null)
    {
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════
    // PUBLIC API — PNG → BLP CONVERSION (LEGACY — resize inside)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Convert a PNG file to BLP2 DXT3 bytes (alphaDepth=8).
    /// Square output — targetSize applied to both width and height.
    /// LEGACY: resizes internally. Prefer ResizePngToBitmap + post-process + EncodeBitmapToBlp.
    /// </summary>
    public byte[]? ConvertPngToBlp(string pngPath, int targetSize = 64)
    {
        return ConvertPngToBlpInternal(pngPath, targetSize, targetSize, useDxt1: false, applyLumAlpha: false);
    }

    /// <summary>
    /// Convert a PNG to BLP2 DXT3 with luminance-to-alpha applied.
    /// Square output — targetSize applied to both width and height.
    /// LEGACY: resizes internally.
    /// </summary>
    public byte[]? ConvertPngToBlpWithAlpha(string pngPath, int targetSize = 64)
    {
        return ConvertPngToBlpInternal(pngPath, targetSize, targetSize, useDxt1: false, applyLumAlpha: true);
    }

    /// <summary>
    /// Session 24: Convert a PNG to BLP2 with vanilla-matched format and dimensions.
    /// LEGACY: resizes internally. For Session 27+ use ResizePngToBitmap + EncodeBitmapToBlp.
    /// </summary>
    public byte[]? ConvertPngToBlpVanillaMatched(string pngPath, int targetWidth, int targetHeight,
        bool useDxt1, bool applyLumAlpha = false)
    {
        return ConvertPngToBlpInternal(pngPath, targetWidth, targetHeight, useDxt1, applyLumAlpha);
    }

    /// <summary>
    /// Internal conversion — handles all combinations of format and dimensions.
    /// LEGACY path: resize + optional lum-alpha + encode all in one shot.
    /// </summary>
    private byte[]? ConvertPngToBlpInternal(string pngPath, int targetWidth, int targetHeight,
        bool useDxt1, bool applyLumAlpha)
    {
        try
        {
            if (!File.Exists(pngPath))
            {
                _logger?.LogWarning("BlpWriter: PNG not found: {Path}", pngPath);
                return null;
            }

            if (!IsPowerOf2(targetWidth) || targetWidth < 4 || targetWidth > 4096)
            {
                _logger?.LogError("BlpWriter: targetWidth must be power of 2 in [4..4096], got {W}", targetWidth);
                return null;
            }
            if (!IsPowerOf2(targetHeight) || targetHeight < 4 || targetHeight > 4096)
            {
                _logger?.LogError("BlpWriter: targetHeight must be power of 2 in [4..4096], got {H}", targetHeight);
                return null;
            }

            using var sourceBitmap = SKBitmap.Decode(pngPath);
            if (sourceBitmap == null)
            {
                _logger?.LogWarning("BlpWriter: SKBitmap.Decode returned null for {Path}", pngPath);
                return null;
            }

            string fmtName = useDxt1 ? "DXT1" : "DXT3";
            _logger?.LogInformation(
                "BlpWriter: {Path} ({SrcW}×{SrcH}) → {TgtW}×{TgtH} {Fmt}{Lum}",
                pngPath, sourceBitmap.Width, sourceBitmap.Height,
                targetWidth, targetHeight, fmtName,
                applyLumAlpha ? " +lum-alpha" : "");

            // Resize while OPAQUE — safe from premul contamination
            using var resized = sourceBitmap.Resize(
                new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            if (resized == null)
            {
                _logger?.LogError("BlpWriter: Resize to {W}×{H} failed", targetWidth, targetHeight);
                return null;
            }

            // Apply luminance-to-alpha if requested (DXT3 particle textures)
            if (applyLumAlpha && !useDxt1)
            {
                var pixels = resized.Pixels;
                for (int i = 0; i < pixels.Length; i++)
                {
                    var c = pixels[i];
                    float lum = (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;
                    float alpha = MathF.Pow(lum, 0.8f);
                    byte a = (byte)Math.Clamp((int)(alpha * 255f), 0, 255);
                    pixels[i] = new SKColor(c.Red, c.Green, c.Blue, a);
                }
                resized.Pixels = pixels;
            }

            return useDxt1 ? EncodeBlpDxt1(resized) : EncodeBlpDxt3(resized);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "BlpWriter: Conversion failed for {Path}", pngPath);
            return null;
        }
    }

    private static bool IsPowerOf2(int n) => n > 0 && (n & (n - 1)) == 0;

    // ═══════════════════════════════════════════════════════════════
    // SESSION 27: POST-RESIZE PROCESSING API
    //
    // These methods let SpellTextureService do:
    //   1. ResizePngToBitmap (512→32, get pixel array)
    //   2. ApplyLuminanceAlphaPixels (on 32×32 pixels)
    //   3. ApplyRadialVignettePixels (on 32×32 pixels)
    //   4. EncodeBitmapToBlp (DXT encode, no resize)
    //
    // Everything operates at final resolution. No more vignetting
    // at 512×512 then crushing to 32×32.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Session 27: Load a PNG and resize to target dimensions.
    /// Returns an SKBitmap at the target size, ready for pixel-level processing.
    /// Caller is responsible for disposing the returned bitmap.
    /// </summary>
    public SKBitmap? ResizePngToBitmap(string pngPath, int targetWidth, int targetHeight)
    {
        try
        {
            if (!File.Exists(pngPath)) return null;

            using var source = SKBitmap.Decode(pngPath);
            if (source == null) return null;

            var resized = source.Resize(
                new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            _logger?.LogDebug("BlpWriter: Resized {Path} ({SrcW}×{SrcH}) → {TgtW}×{TgtH}",
                pngPath, source.Width, source.Height, targetWidth, targetHeight);

            return resized;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "BlpWriter: ResizePngToBitmap failed for {Path}", pngPath);
            return null;
        }
    }

    /// <summary>
    /// Session 27: Apply luminance-to-alpha on a pixel array in-place.
    /// Operates on the actual bitmap pixels — no file I/O.
    /// </summary>
    public void ApplyLuminanceAlphaPixels(SKBitmap bitmap, float gamma = 0.8f)
    {
        var pixels = bitmap.Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            float lum = (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;
            float alpha = MathF.Pow(lum, gamma);
            byte a = (byte)Math.Clamp((int)(alpha * 255f), 0, 255);
            pixels[i] = new SKColor(c.Red, c.Green, c.Blue, a);
        }
        bitmap.Pixels = pixels;
    }

    /// <summary>
    /// Session 27: Apply radial vignette to ALPHA on a pixel array in-place.
    /// For blendMode=2 (alpha blend) emitters.
    /// </summary>
    public void ApplyRadialVignettePixels(SKBitmap bitmap, float innerRadius = 0.35f, float outerRadius = 0.95f)
    {
        var pixels = bitmap.Pixels;
        int w = bitmap.Width;
        int h = bitmap.Height;
        float cx = w / 2f;
        float cy = h / 2f;
        float maxDist = MathF.Min(cx, cy);
        float innerDist = innerRadius * maxDist;
        float outerDist = outerRadius * maxDist;
        float range = outerDist - innerDist;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                float factor;
                if (dist <= innerDist)
                    factor = 1.0f;
                else if (dist >= outerDist)
                    factor = 0.0f;
                else
                {
                    float t = (dist - innerDist) / range;
                    factor = (1.0f + MathF.Cos(t * MathF.PI)) / 2.0f;
                }

                if (factor < 1.0f)
                {
                    int i = y * w + x;
                    var c = pixels[i];
                    byte a = (byte)(c.Alpha * factor);
                    pixels[i] = new SKColor(c.Red, c.Green, c.Blue, a);
                }
            }
        }
        bitmap.Pixels = pixels;
    }

    /// <summary>
    /// Session 27: Apply radial vignette to RGB on a pixel array in-place.
    /// For blendMode=4 (additive) emitters.
    /// </summary>
    public void ApplyRadialVignetteRgbPixels(SKBitmap bitmap, float innerRadius = 0.35f, float outerRadius = 0.95f)
    {
        var pixels = bitmap.Pixels;
        int w = bitmap.Width;
        int h = bitmap.Height;
        float cx = w / 2f;
        float cy = h / 2f;
        float maxDist = MathF.Min(cx, cy);
        float innerDist = innerRadius * maxDist;
        float outerDist = outerRadius * maxDist;
        float range = outerDist - innerDist;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                float factor;
                if (dist <= innerDist)
                    factor = 1.0f;
                else if (dist >= outerDist)
                    factor = 0.0f;
                else
                {
                    float t = (dist - innerDist) / range;
                    factor = (1.0f + MathF.Cos(t * MathF.PI)) / 2.0f;
                }

                if (factor < 1.0f)
                {
                    int i = y * w + x;
                    var c = pixels[i];
                    byte r = (byte)(c.Red * factor);
                    byte g = (byte)(c.Green * factor);
                    byte b = (byte)(c.Blue * factor);
                    pixels[i] = new SKColor(r, g, b, c.Alpha);
                }
            }
        }
        bitmap.Pixels = pixels;
    }

    /// <summary>
    /// Session 27: Apply radial vignette to BOTH RGB and alpha on a pixel array.
    /// For DXT3 + additive: renderer does dest += src.RGB × src.Alpha,
    /// so both channels must reach zero at edges to avoid visible quads.
    /// </summary>
    public void ApplyRadialVignetteBothPixels(SKBitmap bitmap, float innerRadius = 0.35f, float outerRadius = 0.95f)
    {
        var pixels = bitmap.Pixels;
        int w = bitmap.Width;
        int h = bitmap.Height;
        float cx = w / 2f;
        float cy = h / 2f;
        float maxDist = MathF.Min(cx, cy);
        float innerDist = innerRadius * maxDist;
        float outerDist = outerRadius * maxDist;
        float range = outerDist - innerDist;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                float factor;
                if (dist <= innerDist)
                    factor = 1.0f;
                else if (dist >= outerDist)
                    factor = 0.0f;
                else
                {
                    float t = (dist - innerDist) / range;
                    factor = (1.0f + MathF.Cos(t * MathF.PI)) / 2.0f;
                }

                if (factor < 1.0f)
                {
                    int i = y * w + x;
                    var c = pixels[i];
                    byte r = (byte)(c.Red * factor);
                    byte g = (byte)(c.Green * factor);
                    byte b = (byte)(c.Blue * factor);
                    byte a = (byte)(c.Alpha * factor);
                    pixels[i] = new SKColor(r, g, b, a);
                }
            }
        }
        bitmap.Pixels = pixels;
    }

    /// <summary>
    /// Session 27: Apply per-cell radial vignette (BOTH RGB+alpha) on pixel array.
    /// For atlas textures.
    /// </summary>
    public void ApplyAtlasVignetteBothPixels(SKBitmap bitmap, int gridSize = 4,
        float innerRadius = 0.25f, float outerRadius = 0.90f)
    {
        var pixels = bitmap.Pixels;
        int w = bitmap.Width;
        int h = bitmap.Height;
        float cellW = (float)w / gridSize;
        float cellH = (float)h / gridSize;
        float halfCellW = cellW / 2f;
        float halfCellH = cellH / 2f;
        float maxDist = MathF.Min(halfCellW, halfCellH);
        float innerDist = innerRadius * maxDist;
        float outerDist = outerRadius * maxDist;
        float range = outerDist - innerDist;

        for (int y = 0; y < h; y++)
        {
            int cellRow = Math.Min((int)(y / cellH), gridSize - 1);
            float cellCenterY = (cellRow + 0.5f) * cellH;

            for (int x = 0; x < w; x++)
            {
                int cellCol = Math.Min((int)(x / cellW), gridSize - 1);
                float cellCenterX = (cellCol + 0.5f) * cellW;

                float dx = x - cellCenterX;
                float dy = y - cellCenterY;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                float factor;
                if (dist <= innerDist)
                    factor = 1.0f;
                else if (dist >= outerDist)
                    factor = 0.0f;
                else
                {
                    float t = (dist - innerDist) / range;
                    factor = (1.0f + MathF.Cos(t * MathF.PI)) / 2.0f;
                }

                if (factor < 1.0f)
                {
                    int i = y * w + x;
                    var c = pixels[i];
                    byte r = (byte)(c.Red * factor);
                    byte g = (byte)(c.Green * factor);
                    byte b = (byte)(c.Blue * factor);
                    byte a = (byte)(c.Alpha * factor);
                    pixels[i] = new SKColor(r, g, b, a);
                }
            }
        }
        bitmap.Pixels = pixels;
    }

    // ═══════════════════════════════════════════════════════════════
    // SESSION 28: BRIGHTNESS FLOOR MASK — Content-Aware Alpha Carving
    //
    // Replaces the geometric radial vignette for particle textures.
    // Instead of assuming content is centered and circular, this reads
    // the actual pixel brightness and kills everything below a threshold.
    //
    // The particle's visible shape becomes the shape of the bright
    // content — irregular, organic, blobby — exactly like a hand-painted
    // vanilla texture.
    //
    // Why this works:
    //   FLUX generates images on pure black backgrounds. The bright content
    //   (lightning arcs, fire, glows) IS the particle. The dark surround
    //   (near-black pixels at corners/edges) is NOT the particle — but at
    //   non-zero RGB values, additive blending still renders them, revealing
    //   the billboard quad boundary. Killing those pixels makes the quad
    //   boundary invisible because 0+0+0 contributes nothing additively.
    //
    // For DXT1 (no alpha): zeroing RGB is sufficient — additive does dest += RGB.
    // For DXT3 (has alpha): zeroing BOTH RGB and alpha — additive does
    //   dest += RGB × Alpha, so both must be zero.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Session 28: Content-aware brightness floor mask.
    /// Finds the peak brightness in the image, then kills every pixel below
    /// a percentage of that peak. A smooth "knee" transition prevents hard
    /// aliased edges at the cutoff boundary.
    ///
    /// This replaces the radial vignette for particle textures. The vignette
    /// assumed content was centered and circular. This adapts to whatever
    /// shape the AI actually generated.
    ///
    /// Both RGB and alpha are zeroed for killed pixels, ensuring invisible
    /// quad boundaries under both DXT1 additive (dest += RGB) and DXT3
    /// additive (dest += RGB × Alpha) blending modes.
    /// </summary>
    /// <param name="bitmap">The bitmap to process in-place (at final vanilla resolution).</param>
    /// <param name="floorPercent">Brightness threshold as fraction of peak (0.0-1.0).
    /// Pixels below this percentage of peak brightness are killed. Default 0.12 (12%).</param>
    /// <param name="kneeWidth">Width of the smooth transition band above the floor (0.0-1.0).
    /// Pixels between floor and floor+knee get a smooth fade. Default 0.08 (8%).</param>
    public void ApplyBrightnessFloorMask(SKBitmap bitmap, float floorPercent = 0.12f, float kneeWidth = 0.08f)
    {
        var pixels = bitmap.Pixels;

        // ── Step 1: Find peak brightness across all pixels ──
        float peakLum = 0f;
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            float lum = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
            if (lum > peakLum) peakLum = lum;
        }

        if (peakLum < 1f)
        {
            _logger?.LogDebug("BlpWriter: BrightnessFloor — image is pure black, skipping");
            return;
        }

        float floorLum = floorPercent * peakLum;
        float kneeLum = (floorPercent + kneeWidth) * peakLum;

        int killedCount = 0;
        int kneeCount = 0;

        // ── Step 2: Kill below floor, smooth-fade in knee band ──
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            float lum = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;

            if (lum <= floorLum)
            {
                // Below floor: dead pixel — zero everything
                pixels[i] = new SKColor(0, 0, 0, 0);
                killedCount++;
            }
            else if (lum < kneeLum)
            {
                // In the knee: quadratic ease-in for smooth transition
                float t = (lum - floorLum) / (kneeLum - floorLum);
                float factor = t * t;
                byte r = (byte)(c.Red * factor);
                byte g = (byte)(c.Green * factor);
                byte b = (byte)(c.Blue * factor);
                byte a = (byte)(c.Alpha * factor);
                pixels[i] = new SKColor(r, g, b, a);
                kneeCount++;
            }
            // else: above knee — untouched, full brightness preserved
        }

        bitmap.Pixels = pixels;

        float killPct = 100f * killedCount / pixels.Length;
        float kneePct = 100f * kneeCount / pixels.Length;
        _logger?.LogInformation(
            "BlpWriter: BrightnessFloor applied — peak={Peak:F0} floor={Floor:F1} knee={Knee:F1} " +
            "killed={Killed}/{Total} ({KillPct:F0}%) knee={KneeCount} ({KneePct:F0}%)",
            peakLum, floorLum, kneeLum, killedCount, pixels.Length, killPct, kneeCount, kneePct);
    }

    /// <summary>
    /// Session 28: Per-cell brightness floor for atlas/sprite-sheet textures.
    /// Same logic as ApplyBrightnessFloorMask but applied independently
    /// to each cell in a grid, so each animation frame gets its own peak
    /// brightness reference.
    /// </summary>
    public void ApplyAtlasBrightnessFloorMask(SKBitmap bitmap, int gridSize = 4,
        float floorPercent = 0.12f, float kneeWidth = 0.08f)
    {
        var pixels = bitmap.Pixels;
        int w = bitmap.Width;
        int h = bitmap.Height;
        int cellW = w / gridSize;
        int cellH = h / gridSize;

        int totalKilled = 0;

        for (int cellRow = 0; cellRow < gridSize; cellRow++)
        {
            for (int cellCol = 0; cellCol < gridSize; cellCol++)
            {
                int startX = cellCol * cellW;
                int startY = cellRow * cellH;

                // Find peak brightness in this cell
                float peakLum = 0f;
                for (int y = startY; y < startY + cellH && y < h; y++)
                {
                    for (int x = startX; x < startX + cellW && x < w; x++)
                    {
                        var c = pixels[y * w + x];
                        float lum = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
                        if (lum > peakLum) peakLum = lum;
                    }
                }

                if (peakLum < 1f) continue;

                float floorLum = floorPercent * peakLum;
                float kneeLum = (floorPercent + kneeWidth) * peakLum;

                // Apply floor to this cell
                for (int y = startY; y < startY + cellH && y < h; y++)
                {
                    for (int x = startX; x < startX + cellW && x < w; x++)
                    {
                        int i = y * w + x;
                        var c = pixels[i];
                        float lum = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;

                        if (lum <= floorLum)
                        {
                            pixels[i] = new SKColor(0, 0, 0, 0);
                            totalKilled++;
                        }
                        else if (lum < kneeLum)
                        {
                            float t = (lum - floorLum) / (kneeLum - floorLum);
                            float factor = t * t;
                            byte r = (byte)(c.Red * factor);
                            byte g = (byte)(c.Green * factor);
                            byte b = (byte)(c.Blue * factor);
                            byte a = (byte)(c.Alpha * factor);
                            pixels[i] = new SKColor(r, g, b, a);
                        }
                    }
                }
            }
        }

        bitmap.Pixels = pixels;
        _logger?.LogInformation(
            "BlpWriter: AtlasBrightnessFloor applied — {Grid}x{Grid} cells, killed={Killed}/{Total}",
            gridSize, gridSize, totalKilled, pixels.Length);
    }

    /// <summary>
    /// Session 27: Encode an already-processed SKBitmap directly to BLP2 bytes.
    /// No resize, no luminance-to-alpha — the bitmap must already be at final
    /// dimensions with all post-processing applied.
    /// Caller is responsible for ensuring the bitmap is power-of-2 dimensions.
    /// </summary>
    public byte[]? EncodeBitmapToBlp(SKBitmap bitmap, bool useDxt1)
    {
        try
        {
            int w = bitmap.Width;
            int h = bitmap.Height;

            if (!IsPowerOf2(w) || w < 4 || !IsPowerOf2(h) || h < 4)
            {
                _logger?.LogError("BlpWriter: EncodeBitmapToBlp requires power-of-2 dims ≥4, got {W}×{H}", w, h);
                return null;
            }

            string fmt = useDxt1 ? "DXT1" : "DXT3";
            _logger?.LogInformation("BlpWriter: EncodeBitmapToBlp {W}×{H} {Fmt}", w, h, fmt);

            return useDxt1 ? EncodeBlpDxt1(bitmap) : EncodeBlpDxt3(bitmap);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "BlpWriter: EncodeBitmapToBlp failed");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // BLP2 DXT3 ENCODER (existing — alphaDepth=8, alphaType=1)
    // ═══════════════════════════════════════════════════════════════

    private byte[] EncodeBlpDxt3(SKBitmap image)
    {
        int width = image.Width;
        int height = image.Height;

        var mipmaps = new List<SKBitmap>();
        try
        {
            mipmaps.AddRange(BuildMipmapChain(image));
            _logger?.LogInformation("BlpWriter: DXT3 — {Count} mipmap levels", mipmaps.Count);

            var encodedMips = new List<byte[]>();
            foreach (var mip in mipmaps)
                encodedMips.Add(EncodeDxt3(mip));

            var offsets = new uint[16];
            var lengths = new uint[16];
            uint cursor = DATA_START;
            for (int i = 0; i < encodedMips.Count; i++)
            {
                offsets[i] = cursor;
                lengths[i] = (uint)encodedMips[i].Length;
                cursor += lengths[i];
            }

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // Header — DXT3
            bw.Write(new byte[] { (byte)'B', (byte)'L', (byte)'P', (byte)'2' });
            bw.Write((uint)1);    // type = 1 (direct content)
            bw.Write((byte)2);    // compression = 2 (DXT)
            bw.Write((byte)8);    // alpha_depth = 8
            bw.Write((byte)1);    // alpha_type = 1 (DXT3)
            bw.Write((byte)1);    // has_mips = true
            bw.Write((uint)width);
            bw.Write((uint)height);
            for (int i = 0; i < 16; i++) bw.Write(offsets[i]);
            for (int i = 0; i < 16; i++) bw.Write(lengths[i]);
            bw.Write(new byte[PALETTE_BYTES]);

            foreach (var mipBytes in encodedMips)
                bw.Write(mipBytes);

            var result = ms.ToArray();
            _logger?.LogInformation(
                "BlpWriter: Encoded BLP2 DXT3 — {W}×{H}, {Mips} mips, {Bytes} bytes",
                width, height, encodedMips.Count, result.Length);
            return result;
        }
        finally
        {
            foreach (var mip in mipmaps)
                mip.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // BLP2 DXT1 ENCODER (Session 24 — alphaDepth=0, alphaType=0)
    // ═══════════════════════════════════════════════════════════════

    private byte[] EncodeBlpDxt1(SKBitmap image)
    {
        int width = image.Width;
        int height = image.Height;

        var mipmaps = new List<SKBitmap>();
        try
        {
            mipmaps.AddRange(BuildMipmapChain(image));
            _logger?.LogInformation("BlpWriter: DXT1 — {Count} mipmap levels", mipmaps.Count);

            var encodedMips = new List<byte[]>();
            foreach (var mip in mipmaps)
                encodedMips.Add(EncodeDxt1(mip));

            var offsets = new uint[16];
            var lengths = new uint[16];
            uint cursor = DATA_START;
            for (int i = 0; i < encodedMips.Count; i++)
            {
                offsets[i] = cursor;
                lengths[i] = (uint)encodedMips[i].Length;
                cursor += lengths[i];
            }

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // Header — DXT1 (no alpha)
            bw.Write(new byte[] { (byte)'B', (byte)'L', (byte)'P', (byte)'2' });
            bw.Write((uint)1);    // type = 1 (direct content)
            bw.Write((byte)2);    // compression = 2 (DXT)
            bw.Write((byte)0);    // alpha_depth = 0 (NO ALPHA)
            bw.Write((byte)0);    // alpha_type = 0 (DXT1)
            bw.Write((byte)1);    // has_mips = true
            bw.Write((uint)width);
            bw.Write((uint)height);
            for (int i = 0; i < 16; i++) bw.Write(offsets[i]);
            for (int i = 0; i < 16; i++) bw.Write(lengths[i]);
            bw.Write(new byte[PALETTE_BYTES]);

            foreach (var mipBytes in encodedMips)
                bw.Write(mipBytes);

            var result = ms.ToArray();
            _logger?.LogInformation(
                "BlpWriter: Encoded BLP2 DXT1 — {W}×{H}, {Mips} mips, {Bytes} bytes",
                width, height, encodedMips.Count, result.Length);
            return result;
        }
        finally
        {
            foreach (var mip in mipmaps)
                mip.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // MIPMAP CHAIN (supports non-square — stops when BOTH w and h reach 1)
    // ═══════════════════════════════════════════════════════════════

    private static List<SKBitmap> BuildMipmapChain(SKBitmap source)
    {
        var chain = new List<SKBitmap>();

        var level0 = new SKBitmap(new SKImageInfo(
            source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        source.CopyTo(level0, SKColorType.Rgba8888);
        chain.Add(level0);

        var prevPixels = level0.Pixels;
        int w = source.Width;
        int h = source.Height;

        while (w > 1 || h > 1)
        {
            int newW = Math.Max(1, w / 2);
            int newH = Math.Max(1, h / 2);

            var newPixels = new SKColor[newW * newH];

            for (int y = 0; y < newH; y++)
            {
                for (int x = 0; x < newW; x++)
                {
                    int sx = x * 2;
                    int sy = y * 2;
                    int x0 = sx, x1 = Math.Min(sx + 1, w - 1);
                    int y0 = sy, y1 = Math.Min(sy + 1, h - 1);

                    var p00 = prevPixels[y0 * w + x0];
                    var p10 = prevPixels[y0 * w + x1];
                    var p01 = prevPixels[y1 * w + x0];
                    var p11 = prevPixels[y1 * w + x1];

                    byte r = (byte)((p00.Red + p10.Red + p01.Red + p11.Red) / 4);
                    byte g = (byte)((p00.Green + p10.Green + p01.Green + p11.Green) / 4);
                    byte b = (byte)((p00.Blue + p10.Blue + p01.Blue + p11.Blue) / 4);
                    byte a = (byte)((p00.Alpha + p10.Alpha + p01.Alpha + p11.Alpha) / 4);

                    newPixels[y * newW + x] = new SKColor(r, g, b, a);
                }
            }

            var mipBitmap = new SKBitmap(new SKImageInfo(newW, newH, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            mipBitmap.Pixels = newPixels;
            chain.Add(mipBitmap);

            prevPixels = newPixels;
            w = newW;
            h = newH;
        }

        return chain;
    }

    // ═══════════════════════════════════════════════════════════════
    // DXT3 BLOCK ENCODER (16 bytes: 8 alpha + 8 color)
    // ═══════════════════════════════════════════════════════════════

    private static byte[] EncodeDxt3(SKBitmap mip)
    {
        int w = mip.Width;
        int h = mip.Height;
        var pixels = mip.Pixels;

        int blocksX = Math.Max(1, (w + 3) / 4);
        int blocksY = Math.Max(1, (h + 3) / 4);

        var output = new byte[blocksX * blocksY * DXT3_BLOCK_BYTES];
        int outIdx = 0;

        var blockR = new byte[16];
        var blockG = new byte[16];
        var blockB = new byte[16];
        var blockA = new byte[16];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                for (int py = 0; py < 4; py++)
                {
                    for (int px = 0; px < 4; px++)
                    {
                        int srcX = Math.Min(bx * 4 + px, w - 1);
                        int srcY = Math.Min(by * 4 + py, h - 1);
                        var c = pixels[srcY * w + srcX];
                        int i = py * 4 + px;
                        blockR[i] = c.Red;
                        blockG[i] = c.Green;
                        blockB[i] = c.Blue;
                        blockA[i] = c.Alpha;
                    }
                }

                EncodeDxt3Block(blockR, blockG, blockB, blockA, output, outIdx);
                outIdx += DXT3_BLOCK_BYTES;
            }
        }

        return output;
    }

    private static void EncodeDxt3Block(
        byte[] r, byte[] g, byte[] b, byte[] a,
        byte[] output, int outOffset)
    {
        // Alpha block (8 bytes): 16 × 4-bit alpha values
        for (int i = 0; i < 8; i++)
        {
            int a0 = a[i * 2] >> 4;
            int a1 = a[i * 2 + 1] >> 4;
            output[outOffset + i] = (byte)((a1 << 4) | a0);
        }

        // Color block (8 bytes)
        EncodeColorBlock(r, g, b, output, outOffset + 8);
    }

    // ═══════════════════════════════════════════════════════════════
    // DXT1 BLOCK ENCODER (8 bytes: color only, NO alpha)
    // ═══════════════════════════════════════════════════════════════

    private static byte[] EncodeDxt1(SKBitmap mip)
    {
        int w = mip.Width;
        int h = mip.Height;
        var pixels = mip.Pixels;

        int blocksX = Math.Max(1, (w + 3) / 4);
        int blocksY = Math.Max(1, (h + 3) / 4);

        var output = new byte[blocksX * blocksY * DXT1_BLOCK_BYTES];
        int outIdx = 0;

        var blockR = new byte[16];
        var blockG = new byte[16];
        var blockB = new byte[16];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                for (int py = 0; py < 4; py++)
                {
                    for (int px = 0; px < 4; px++)
                    {
                        int srcX = Math.Min(bx * 4 + px, w - 1);
                        int srcY = Math.Min(by * 4 + py, h - 1);
                        var c = pixels[srcY * w + srcX];
                        int i = py * 4 + px;
                        blockR[i] = c.Red;
                        blockG[i] = c.Green;
                        blockB[i] = c.Blue;
                    }
                }

                // DXT1: just the 8-byte color block, no alpha block
                EncodeColorBlock(blockR, blockG, blockB, output, outIdx);
                outIdx += DXT1_BLOCK_BYTES;
            }
        }

        return output;
    }

    // ═══════════════════════════════════════════════════════════════
    // SHARED COLOR BLOCK ENCODER (8 bytes — used by both DXT1 and DXT3)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Encode 16 pixels as an 8-byte DXT color block (2 RGB565 endpoints + 16×2-bit indices).
    /// Uses bounding-box endpoint selection with 4-color palette (c0 >= c1 → 4 colors).
    /// </summary>
    private static void EncodeColorBlock(byte[] r, byte[] g, byte[] b, byte[] output, int outOffset)
    {
        // Bounding-box endpoints in RGB space
        byte rMin = 255, gMin = 255, bMin = 255;
        byte rMax = 0, gMax = 0, bMax = 0;
        for (int i = 0; i < 16; i++)
        {
            if (r[i] < rMin) rMin = r[i];
            if (r[i] > rMax) rMax = r[i];
            if (g[i] < gMin) gMin = g[i];
            if (g[i] > gMax) gMax = g[i];
            if (b[i] < bMin) bMin = b[i];
            if (b[i] > bMax) bMax = b[i];
        }

        ushort c0_565 = RgbTo565(rMax, gMax, bMax);
        ushort c1_565 = RgbTo565(rMin, gMin, bMin);

        // DXT1 4-color mode requires c0 > c1 (or c0 == c1 for flat blocks)
        // If they're equal the palette still works (all 4 entries identical)
        // If c0 < c1, swap to stay in 4-color mode (avoid DXT1 transparent mode)
        if (c0_565 < c1_565)
        {
            (c0_565, c1_565) = (c1_565, c0_565);
        }

        output[outOffset + 0] = (byte)(c0_565 & 0xFF);
        output[outOffset + 1] = (byte)((c0_565 >> 8) & 0xFF);
        output[outOffset + 2] = (byte)(c1_565 & 0xFF);
        output[outOffset + 3] = (byte)((c1_565 >> 8) & 0xFF);

        // Re-decode endpoints to match what the GPU reconstructs
        Rgb565To888(c0_565, out byte c0r, out byte c0g, out byte c0b);
        Rgb565To888(c1_565, out byte c1r, out byte c1g, out byte c1b);

        // 4-color palette
        Span<byte> pr = stackalloc byte[4];
        Span<byte> pg = stackalloc byte[4];
        Span<byte> pb = stackalloc byte[4];
        pr[0] = c0r; pg[0] = c0g; pb[0] = c0b;
        pr[1] = c1r; pg[1] = c1g; pb[1] = c1b;
        pr[2] = (byte)((2 * c0r + c1r) / 3);
        pg[2] = (byte)((2 * c0g + c1g) / 3);
        pb[2] = (byte)((2 * c0b + c1b) / 3);
        pr[3] = (byte)((c0r + 2 * c1r) / 3);
        pg[3] = (byte)((c0g + 2 * c1g) / 3);
        pb[3] = (byte)((c0b + 2 * c1b) / 3);

        // Best-fit index selection
        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int bestIdx = 0;
            int bestDist = int.MaxValue;
            for (int p = 0; p < 4; p++)
            {
                int dr = pr[p] - r[i];
                int dg = pg[p] - g[i];
                int db = pb[p] - b[i];
                int dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = p;
                    if (dist == 0) break;
                }
            }
            indices |= (uint)(bestIdx << (i * 2));
        }

        output[outOffset + 4] = (byte)(indices & 0xFF);
        output[outOffset + 5] = (byte)((indices >> 8) & 0xFF);
        output[outOffset + 6] = (byte)((indices >> 16) & 0xFF);
        output[outOffset + 7] = (byte)((indices >> 24) & 0xFF);
    }

    private static ushort RgbTo565(byte r, byte g, byte b)
    {
        return (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
    }

    private static void Rgb565To888(ushort c, out byte r, out byte g, out byte b)
    {
        int r5 = (c >> 11) & 0x1F;
        int g6 = (c >> 5) & 0x3F;
        int b5 = c & 0x1F;
        r = (byte)((r5 << 3) | (r5 >> 2));
        g = (byte)((g6 << 2) | (g6 >> 4));
        b = (byte)((b5 << 3) | (b5 >> 2));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // LEGACY POST-PROCESSING (vignette + luminance-alpha on PNG files)
    // Retained for backward compatibility. Session 27+ code uses the
    // pixel-array versions above instead.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Apply a radial vignette to ALPHA ONLY — RGB stays untouched.
    /// For blendMode=2 (alpha blend) emitters.
    /// LEGACY: operates on PNG file. Prefer ApplyRadialVignettePixels.
    /// </summary>
    public void ApplyRadialVignette(string pngPath, float innerRadius = 0.35f, float outerRadius = 0.95f)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(pngPath);
            if (bitmap == null) return;

            using var writable = bitmap.Copy(SKColorType.Rgba8888);
            if (writable == null) return;

            var pixels = writable.Pixels;
            int w = writable.Width;
            int h = writable.Height;
            float cx = w / 2f;
            float cy = h / 2f;
            float maxDist = MathF.Min(cx, cy);
            float innerDist = innerRadius * maxDist;
            float outerDist = outerRadius * maxDist;
            float range = outerDist - innerDist;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);

                    float factor;
                    if (dist <= innerDist)
                        factor = 1.0f;
                    else if (dist >= outerDist)
                        factor = 0.0f;
                    else
                    {
                        float t = (dist - innerDist) / range;
                        factor = (1.0f + MathF.Cos(t * MathF.PI)) / 2.0f;
                    }

                    if (factor < 1.0f)
                    {
                        int i = y * w + x;
                        var c = pixels[i];
                        byte a = (byte)(c.Alpha * factor);
                        pixels[i] = new SKColor(c.Red, c.Green, c.Blue, a);
                    }
                }
            }

            writable.Pixels = pixels;

            using var outStream = File.OpenWrite(pngPath);
            outStream.SetLength(0);
            writable.Encode(outStream, SKEncodedImageFormat.Png, 100);

            _logger?.LogDebug("BlpWriter: Applied alpha-only radial vignette to {Path} ({W}x{H})",
                pngPath, w, h);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "BlpWriter: ApplyRadialVignette failed for {Path}", pngPath);
        }
    }

    /// <summary>
    /// Apply a per-cell radial vignette to a sprite atlas — ALPHA ONLY.
    /// LEGACY: operates on PNG file.
    /// </summary>
    public void ApplyAtlasVignette(string pngPath, int gridSize = 4, float innerRadius = 0.25f, float outerRadius = 0.90f)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(pngPath);
            if (bitmap == null) return;

            using var writable = bitmap.Copy(SKColorType.Rgba8888);
            if (writable == null) return;

            var pixels = writable.Pixels;
            int w = writable.Width;
            int h = writable.Height;
            float cellW = (float)w / gridSize;
            float cellH = (float)h / gridSize;
            float halfCellW = cellW / 2f;
            float halfCellH = cellH / 2f;
            float maxDist = MathF.Min(halfCellW, halfCellH);
            float innerDist = innerRadius * maxDist;
            float outerDist = outerRadius * maxDist;
            float range = outerDist - innerDist;

            for (int y = 0; y < h; y++)
            {
                int cellRow = Math.Min((int)(y / cellH), gridSize - 1);
                float cellCenterY = (cellRow + 0.5f) * cellH;

                for (int x = 0; x < w; x++)
                {
                    int cellCol = Math.Min((int)(x / cellW), gridSize - 1);
                    float cellCenterX = (cellCol + 0.5f) * cellW;

                    float dx = x - cellCenterX;
                    float dy = y - cellCenterY;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);

                    float factor;
                    if (dist <= innerDist)
                        factor = 1.0f;
                    else if (dist >= outerDist)
                        factor = 0.0f;
                    else
                    {
                        float t = (dist - innerDist) / range;
                        factor = (1.0f + MathF.Cos(t * MathF.PI)) / 2.0f;
                    }

                    if (factor < 1.0f)
                    {
                        int i = y * w + x;
                        var c = pixels[i];
                        byte a = (byte)(c.Alpha * factor);
                        pixels[i] = new SKColor(c.Red, c.Green, c.Blue, a);
                    }
                }
            }

            writable.Pixels = pixels;

            using var outStream = File.OpenWrite(pngPath);
            outStream.SetLength(0);
            writable.Encode(outStream, SKEncodedImageFormat.Png, 100);

            _logger?.LogDebug("BlpWriter: Applied alpha-only atlas vignette ({Grid}x{Grid}) to {Path} ({W}x{H})",
                gridSize, gridSize, pngPath, w, h);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "BlpWriter: ApplyAtlasVignette failed for {Path}", pngPath);
        }
    }

    /// <summary>
    /// Apply a radial vignette to RGB channels — fades toward black at edges.
    /// Alpha is left unchanged. For blendMode=4 (additive) emitters.
    /// LEGACY: operates on PNG file. Prefer ApplyRadialVignetteRgbPixels.
    /// </summary>
    public void ApplyRadialVignetteRgb(string pngPath, float innerRadius = 0.35f, float outerRadius = 0.95f)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(pngPath);
            if (bitmap == null) return;

            using var writable = bitmap.Copy(SKColorType.Rgba8888);
            if (writable == null) return;

            var pixels = writable.Pixels;
            int w = writable.Width;
            int h = writable.Height;
            float cx = w / 2f;
            float cy = h / 2f;
            float maxDist = MathF.Min(cx, cy);
            float innerDist = innerRadius * maxDist;
            float outerDist = outerRadius * maxDist;
            float range = outerDist - innerDist;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);

                    float factor;
                    if (dist <= innerDist)
                        factor = 1.0f;
                    else if (dist >= outerDist)
                        factor = 0.0f;
                    else
                    {
                        float t = (dist - innerDist) / range;
                        factor = (1.0f + MathF.Cos(t * MathF.PI)) / 2.0f;
                    }

                    if (factor < 1.0f)
                    {
                        int i = y * w + x;
                        var c = pixels[i];
                        byte r = (byte)(c.Red * factor);
                        byte g = (byte)(c.Green * factor);
                        byte b = (byte)(c.Blue * factor);
                        pixels[i] = new SKColor(r, g, b, c.Alpha);
                    }
                }
            }

            writable.Pixels = pixels;

            using var outStream = File.OpenWrite(pngPath);
            outStream.SetLength(0);
            writable.Encode(outStream, SKEncodedImageFormat.Png, 100);

            _logger?.LogDebug("BlpWriter: Applied RGB radial vignette (additive mode) to {Path} ({W}x{H})",
                pngPath, w, h);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "BlpWriter: ApplyRadialVignetteRgb failed for {Path}", pngPath);
        }
    }

    /// <summary>
    /// Apply a per-cell radial vignette to a sprite atlas — RGB channels only.
    /// For blendMode=4 (additive) atlas emitters.
    /// LEGACY: operates on PNG file.
    /// </summary>
    public void ApplyAtlasVignetteRgb(string pngPath, int gridSize = 4, float innerRadius = 0.25f, float outerRadius = 0.90f)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(pngPath);
            if (bitmap == null) return;

            using var writable = bitmap.Copy(SKColorType.Rgba8888);
            if (writable == null) return;

            var pixels = writable.Pixels;
            int w = writable.Width;
            int h = writable.Height;
            float cellW = (float)w / gridSize;
            float cellH = (float)h / gridSize;
            float halfCellW = cellW / 2f;
            float halfCellH = cellH / 2f;
            float maxDist = MathF.Min(halfCellW, halfCellH);
            float innerDist = innerRadius * maxDist;
            float outerDist = outerRadius * maxDist;
            float range = outerDist - innerDist;

            for (int y = 0; y < h; y++)
            {
                int cellRow = Math.Min((int)(y / cellH), gridSize - 1);
                float cellCenterY = (cellRow + 0.5f) * cellH;

                for (int x = 0; x < w; x++)
                {
                    int cellCol = Math.Min((int)(x / cellW), gridSize - 1);
                    float cellCenterX = (cellCol + 0.5f) * cellW;

                    float dx = x - cellCenterX;
                    float dy = y - cellCenterY;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);

                    float factor;
                    if (dist <= innerDist)
                        factor = 1.0f;
                    else if (dist >= outerDist)
                        factor = 0.0f;
                    else
                    {
                        float t = (dist - innerDist) / range;
                        factor = (1.0f + MathF.Cos(t * MathF.PI)) / 2.0f;
                    }

                    if (factor < 1.0f)
                    {
                        int i = y * w + x;
                        var c = pixels[i];
                        byte r = (byte)(c.Red * factor);
                        byte g = (byte)(c.Green * factor);
                        byte b = (byte)(c.Blue * factor);
                        pixels[i] = new SKColor(r, g, b, c.Alpha);
                    }
                }
            }

            writable.Pixels = pixels;

            using var outStream = File.OpenWrite(pngPath);
            outStream.SetLength(0);
            writable.Encode(outStream, SKEncodedImageFormat.Png, 100);

            _logger?.LogDebug("BlpWriter: Applied RGB atlas vignette (additive mode, {Grid}x{Grid}) to {Path}",
                gridSize, gridSize, pngPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "BlpWriter: ApplyAtlasVignetteRgb failed for {Path}", pngPath);
        }
    }

    /// <summary>
    /// Apply luminance-to-alpha on a PNG file in-place.
    /// LEGACY: operates on PNG file. Prefer ApplyLuminanceAlphaPixels.
    /// </summary>
    public void ApplyLuminanceAlpha(string pngPath)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(pngPath);
            if (bitmap == null)
            {
                _logger?.LogWarning("BlpWriter: ApplyLuminanceAlpha — could not decode {Path}", pngPath);
                return;
            }

            using var writable = bitmap.Copy(SKColorType.Rgba8888);
            if (writable == null) return;

            var pixels = writable.Pixels;

            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                float lum = (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;
                float alpha = MathF.Pow(lum, 0.8f);
                byte a = (byte)Math.Clamp((int)(alpha * 255f), 0, 255);
                pixels[i] = new SKColor(c.Red, c.Green, c.Blue, a);
            }

            writable.Pixels = pixels;

            using var outStream = File.OpenWrite(pngPath);
            outStream.SetLength(0);
            writable.Encode(outStream, SKEncodedImageFormat.Png, 100);

            _logger?.LogDebug("BlpWriter: Applied luminance→alpha to {Path} ({W}x{H})",
                pngPath, bitmap.Width, bitmap.Height);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "BlpWriter: ApplyLuminanceAlpha failed for {Path}", pngPath);
        }
    }
}