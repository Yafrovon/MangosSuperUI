using System.Buffers.Binary;
using MangosSuperUI.Services.WeaponForge.RawM2;
using SkiaSharp;

namespace MangosSuperUI.Services.WeaponForge;

/// <summary>
/// One hardcoded source texture whose complete M2 usage graph is compositing-only. The texture
/// indices are retained because one MPQ member can appear more than once in an M2 texture table.
/// </summary>
internal sealed record NativeWeaponEffectTexture(
    string SourcePath,
    IReadOnlyList<int> TextureIndices);

/// <summary>
/// Pure selection and pixel helpers for recoloring a weapon's own Type-0 effect sheets. This does
/// not rewrite an M2 or package a BLP; callers decide how selected source paths map to emitted
/// texture slots.
/// </summary>
internal static class NativeWeaponEffectRecolor
{
    private const int BatchSize = 24;
    private const int RibbonRecordSize = 220;

    private sealed class Candidate
    {
        public required string SourcePath { get; init; }
        public List<int> TextureIndices { get; } = [];
        public bool Used { get; set; }
        public bool EveryUseComposites { get; set; } = true;
    }

    /// <summary>
    /// Select hardcoded Type-0 textures that are actually sampled and are sampled only by
    /// compositing batches/ribbons (blend modes 3+) or particle emitters. Every batch texture unit
    /// is resolved through
    /// <c>batch.TextureIndex + unit -&gt; TextureLookup</c>; the table index itself is not a texture
    /// index. A repeated hardcoded path is treated as one asset, so an opaque use through any
    /// duplicate texture-table entry disqualifies that path. Source-backed models are inspected
    /// through every raw v256 view and ribbon record; malformed raw usage graphs throw rather than
    /// silently admitting an incompletely inspected texture.
    /// </summary>
    internal static IReadOnlyList<NativeWeaponEffectTexture> SelectEligibleTextures(M2Model model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var byPath = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        var byTextureIndex = new Dictionary<int, Candidate>();

        for (int textureIndex = 0; textureIndex < model.Textures.Count; textureIndex++)
        {
            M2TextureRef texture = model.Textures[textureIndex];
            if (texture.Type != 0 || string.IsNullOrWhiteSpace(texture.Filename)) continue;

            string? sourcePath = WeaponTexturePath.Canonicalize(texture.Filename);
            if (sourcePath is null) continue;

            if (!byPath.TryGetValue(sourcePath, out Candidate? candidate))
            {
                candidate = new Candidate { SourcePath = sourcePath };
                byPath.Add(sourcePath, candidate);
            }

            candidate.TextureIndices.Add(textureIndex);
            byTextureIndex.Add(textureIndex, candidate);
        }

        if (model.SourceBytes is { } sourceBytes)
            MarkRawUses(sourceBytes, model, byTextureIndex);
        else
        {
            if (model.RibbonEmitterCount > 0)
                throw new InvalidOperationException(
                    "Native effect texture selection cannot inspect ribbon texture/material " +
                    "usage because the source M2 bytes are unavailable.");
            ValidateParsedParticleUses(model);
            MarkParsedParticleUses(model, byPath);
            MarkParsedBatchUses(model, byTextureIndex);
        }

        return byPath.Values
            .Where(candidate => candidate.Used && candidate.EveryUseComposites)
            .OrderBy(candidate => candidate.TextureIndices[0])
            .Select(candidate => new NativeWeaponEffectTexture(
                candidate.SourcePath,
                candidate.TextureIndices.OrderBy(index => index).ToArray()))
            .ToArray();
    }

    private static void ValidateParsedParticleUses(M2Model model)
    {
        if (model.ParticleEmitterCount == 0) return;
        if (model.ParticleEmitterCount != model.ParticleEmitters.Count)
            throw new InvalidOperationException(
                $"Native effect texture selection cannot inspect all declared particle emitters: " +
                $"header count {model.ParticleEmitterCount}, parsed count {model.ParticleEmitters.Count}.");

        var inspectablePaths = model.Textures
            .Where(texture => !string.IsNullOrWhiteSpace(texture.Filename))
            .Select(texture => WeaponTexturePath.Canonicalize(texture.Filename)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int emitterIndex = 0; emitterIndex < model.ParticleEmitters.Count; emitterIndex++)
        {
            string? textureName = model.ParticleEmitters[emitterIndex].TextureName;
            if (string.IsNullOrWhiteSpace(textureName) ||
                !inspectablePaths.Contains(WeaponTexturePath.Canonicalize(textureName)!))
                throw new InvalidOperationException(
                    $"Native effect texture selection cannot inspect particle emitter " +
                    $"{emitterIndex}'s texture without the source M2 bytes.");
        }
    }

    private static void MarkParsedParticleUses(
        M2Model model,
        IReadOnlyDictionary<string, Candidate> byPath)
    {
        foreach (M2ParticleEmitterInfo emitter in model.ParticleEmitters)
        {
            if (string.IsNullOrWhiteSpace(emitter.TextureName)) continue;
            string? sourcePath = WeaponTexturePath.Canonicalize(emitter.TextureName);
            if (sourcePath is not null && byPath.TryGetValue(sourcePath, out Candidate? candidate))
                candidate.Used = true;
        }
    }

    private static void MarkParsedBatchUses(
        M2Model model,
        IReadOnlyDictionary<int, Candidate> byTextureIndex)
    {
        foreach (M2Batch batch in model.Batches)
        {
            bool composites = batch.MaterialIndex < model.RenderFlags.Count &&
                              model.RenderFlags[batch.MaterialIndex].BlendingMode >= 3;

            for (int unit = 0; unit < batch.TextureCount; unit++)
            {
                long comboIndex = (long)batch.TextureIndex + unit;
                if (comboIndex < 0 || comboIndex >= model.TextureLookup.Count) continue;

                int textureIndex = model.TextureLookup[(int)comboIndex];
                if (!byTextureIndex.TryGetValue(textureIndex, out Candidate? candidate)) continue;

                candidate.Used = true;
                if (!composites) candidate.EveryUseComposites = false;
            }
        }
    }

    private static void MarkRawUses(
        byte[] sourceBytes,
        M2Model model,
        IReadOnlyDictionary<int, Candidate> byTextureIndex)
    {
        RawM2Document? document = RawM2Document.Parse(sourceBytes, out string? parseError);
        if (document is null)
            throw MalformedRaw(parseError ?? "source is not a canonical v256 M2");

        RawM2Array? textures = document.FindArray("textures");
        RawM2Array? textureLookup = document.FindArray("textureLookup");
        RawM2Array? renderFlags = document.FindArray("renderFlags");
        RawM2Array? globalLoops = document.FindArray("globalLoops");
        RawM2Array? sequences = document.FindArray("sequences");
        RawM2Array? ribbons = document.FindArray("ribbonEmitters");
        RawM2Array? particles = document.FindArray("particleEmitters");
        if (textures is null || !textures.InBounds)
            throw MalformedRaw("texture table is out of bounds");
        if (textures.Count != model.Textures.Count)
            throw MalformedRaw(
                $"raw texture count {textures.Count} does not match parsed count {model.Textures.Count}");
        if (textureLookup is null || !textureLookup.InBounds)
            throw MalformedRaw("texture lookup table is out of bounds");
        if (renderFlags is null || !renderFlags.InBounds)
            throw MalformedRaw("render-flag table is out of bounds");
        if (globalLoops is null || !globalLoops.InBounds ||
            sequences is null || !sequences.InBounds)
            throw MalformedRaw("animation sequence tables are out of bounds");
        if (ribbons is null)
            throw MalformedRaw("ribbon-emitter header is unavailable");
        if (particles is null || !particles.InBounds)
            throw MalformedRaw("particle-emitter table is out of bounds");

        foreach (RawM2View view in document.Views)
        {
            if (!view.HeaderInBounds || !view.Batches.InBounds)
                throw MalformedRaw($"view {view.Index} has an invalid header or batch table");

            for (uint batchIndex = 0; batchIndex < view.Batches.Count; batchIndex++)
            {
                int batch = checked((int)(view.Batches.Offset + (long)batchIndex * BatchSize));
                ushort materialIndex = U16(sourceBytes, batch + 10);
                if (materialIndex >= renderFlags.Count)
                    throw MalformedRaw(
                        $"view {view.Index} batch {batchIndex} references material {materialIndex}, " +
                        $"count {renderFlags.Count}");

                ushort textureCount = U16(sourceBytes, batch + 14);
                ushort textureCombo = U16(sourceBytes, batch + 16);
                if ((long)textureCombo + textureCount > textureLookup.Count)
                    throw MalformedRaw(
                        $"view {view.Index} batch {batchIndex} texture span " +
                        $"[{textureCombo},{(long)textureCombo + textureCount}) exceeds lookup count " +
                        textureLookup.Count);

                int material = checked((int)(renderFlags.Offset + (long)materialIndex * 4));
                bool composites = U16(sourceBytes, material + 2) >= 3;
                for (int unit = 0; unit < textureCount; unit++)
                {
                    int combo = checked((int)(textureLookup.Offset +
                        ((long)textureCombo + unit) * sizeof(ushort)));
                    ushort textureIndex = U16(sourceBytes, combo);
                    if (textureIndex >= textures.Count)
                        throw MalformedRaw(
                            $"view {view.Index} batch {batchIndex} unit {unit} resolves texture " +
                            $"{textureIndex}, count {textures.Count}");

                    MarkUse(textureIndex, composites, byTextureIndex);
                }
            }
        }

        MarkRawRibbonUses(sourceBytes, textures, renderFlags, globalLoops, sequences,
            ribbons, byTextureIndex);
        MarkRawParticleUses(sourceBytes, textures, particles, byTextureIndex);
    }

    private static void MarkRawRibbonUses(
        byte[] sourceBytes,
        RawM2Array textures,
        RawM2Array renderFlags,
        RawM2Array globalLoops,
        RawM2Array sequences,
        RawM2Array ribbons,
        IReadOnlyDictionary<int, Candidate> byTextureIndex)
    {
        if (ribbons.Count == 0) return;
        long ribbonEnd = ribbons.Offset + (long)ribbons.Count * RibbonRecordSize;
        if (ribbons.Offset == 0 || ribbonEnd > sourceBytes.Length)
            throw MalformedRaw(
                $"ribbon record table count={ribbons.Count}, offset=0x{ribbons.Offset:X} is out of bounds");

        for (uint ribbonIndex = 0; ribbonIndex < ribbons.Count; ribbonIndex++)
        {
            int ribbon = checked((int)(ribbons.Offset + (long)ribbonIndex * RibbonRecordSize));
            uint textureCount = U32(sourceBytes, ribbon + 20);
            uint textureOffset = U32(sourceBytes, ribbon + 24);
            uint materialCount = U32(sourceBytes, ribbon + 28);
            uint materialOffset = U32(sourceBytes, ribbon + 32);

            if (!UShortArrayInBounds(textureCount, textureOffset, sourceBytes.Length))
                throw MalformedRaw($"ribbon {ribbonIndex} texture-index array is out of bounds");
            if (!UShortArrayInBounds(materialCount, materialOffset, sourceBytes.Length))
                throw MalformedRaw($"ribbon {ribbonIndex} material-index array is out of bounds");

            bool composites = materialCount > 0;
            for (uint materialSlot = 0; materialSlot < materialCount; materialSlot++)
            {
                ushort materialIndex = U16(sourceBytes,
                    checked((int)(materialOffset + materialSlot * sizeof(ushort))));
                if (materialIndex >= renderFlags.Count)
                    throw MalformedRaw(
                        $"ribbon {ribbonIndex} material slot {materialSlot} references " +
                        $"material {materialIndex}, " +
                        $"count {renderFlags.Count}");

                int material = checked((int)(renderFlags.Offset + (long)materialIndex * 4));
                if (U16(sourceBytes, material + 2) < 3) composites = false;
            }

            bool usesTypeZeroCandidate = false;
            for (uint textureSlot = 0; textureSlot < textureCount; textureSlot++)
            {
                ushort textureIndex = U16(sourceBytes,
                    checked((int)(textureOffset + textureSlot * sizeof(ushort))));
                if (textureIndex >= textures.Count)
                    throw MalformedRaw(
                        $"ribbon {ribbonIndex} texture slot {textureSlot} references " +
                        $"texture {textureIndex}, count {textures.Count}");
                if (byTextureIndex.ContainsKey(textureIndex))
                    usesTypeZeroCandidate = true;
                MarkUse(textureIndex, composites, byTextureIndex);
            }

            // The ribbon texture is multiplied by this authored RGB animation. A chromatic track
            // would steer a newly tinted BLP back toward the stock hue (or black), so texture-only
            // recoloring is admitted only when every authored value/tangent is neutral gray.
            if (composites && usesTypeZeroCandidate)
                ValidateNeutralRibbonColorTrack(
                    sourceBytes, ribbon, ribbonIndex, globalLoops.Count, sequences.Count);
        }
    }

    private static void ValidateNeutralRibbonColorTrack(
        byte[] sourceBytes,
        int ribbon,
        uint ribbonIndex,
        uint globalSequenceCount,
        uint sequenceCount)
    {
        const int track = 36;
        const int trackSize = 28;
        const int vectorSize = 12;
        int trackOffset = checked(ribbon + track);
        if (trackOffset < 0 || trackOffset + trackSize > sourceBytes.Length)
            throw MalformedRaw($"ribbon {ribbonIndex} RGB track header is out of bounds");

        ushort interpolation = U16(sourceBytes, trackOffset);
        short globalSequence = I16(sourceBytes, trackOffset + 2);
        uint rangeCount = U32(sourceBytes, trackOffset + 4);
        uint rangeOffset = U32(sourceBytes, trackOffset + 8);
        uint timeCount = U32(sourceBytes, trackOffset + 12);
        uint timeOffset = U32(sourceBytes, trackOffset + 16);
        uint keyCount = U32(sourceBytes, trackOffset + 20);
        uint keyOffset = U32(sourceBytes, trackOffset + 24);

        if (interpolation > 3)
            throw MalformedRaw($"ribbon {ribbonIndex} has invalid RGB interpolation {interpolation}");
        if (globalSequence < -1 ||
            globalSequence >= 0 && globalSequence >= globalSequenceCount)
            throw MalformedRaw(
                $"ribbon {ribbonIndex} references invalid global sequence {globalSequence}");
        if (timeCount != keyCount)
            throw MalformedRaw(
                $"ribbon {ribbonIndex} has {timeCount} RGB timestamps for {keyCount} keys");
        if (keyCount == 0)
            throw new InvalidOperationException(
                $"Native effect texture selection rejected ribbon {ribbonIndex}: " +
                "its RGB modulation has no authored keys, so a neutral multiplier cannot be proven.");

        int vectorsPerKey = interpolation is 2 or 3 ? 3 : 1;
        int storedStride = checked(vectorSize * vectorsPerKey);
        if (!ArrayInBounds(rangeCount, rangeOffset, 8, sourceBytes.Length) ||
            !ArrayInBounds(timeCount, timeOffset, sizeof(uint), sourceBytes.Length) ||
            !ArrayInBounds(keyCount, keyOffset, storedStride, sourceBytes.Length))
            throw MalformedRaw($"ribbon {ribbonIndex} RGB track arrays are out of bounds");
        if (globalSequence == -1 && sequenceCount > 0 &&
            rangeCount > 0 && rangeCount < sequenceCount)
            throw MalformedRaw(
                $"ribbon {ribbonIndex} has {rangeCount} ranges for {sequenceCount} sequences");

        for (uint rangeIndex = 0; rangeIndex < rangeCount; rangeIndex++)
        {
            int offset = checked((int)(rangeOffset + (long)rangeIndex * 8));
            uint start = U32(sourceBytes, offset);
            uint end = U32(sourceBytes, offset + 4);
            if (start > end || end >= keyCount)
                throw MalformedRaw(
                    $"ribbon {ribbonIndex} contains invalid RGB range [{start},{end}]");
        }

        for (uint key = 0; key < keyCount; key++)
        {
            int value = checked((int)(keyOffset + (long)key * storedStride));
            for (int vector = 0; vector < vectorsPerKey; vector++)
            {
                int offset = checked(value + vector * vectorSize);
                float red = F32(sourceBytes, offset);
                float green = F32(sourceBytes, offset + 4);
                float blue = F32(sourceBytes, offset + 8);
                if (!float.IsFinite(red) || !float.IsFinite(green) || !float.IsFinite(blue) ||
                    red < 0f || red > 1f || green < 0f || green > 1f ||
                    blue < 0f || blue > 1f)
                    throw MalformedRaw(
                        $"ribbon {ribbonIndex} has a non-finite or out-of-range RGB value");
                if (MathF.Abs(red - green) > 0.00001f ||
                    MathF.Abs(red - blue) > 0.00001f)
                    throw new InvalidOperationException(
                        $"Native effect texture selection rejected ribbon {ribbonIndex}: " +
                        "its authored RGB modulation is chromatic and cannot be safely replaced " +
                        "by a texture-only recolor.");
            }
        }
    }

    private static void MarkRawParticleUses(
        byte[] sourceBytes,
        RawM2Array textures,
        RawM2Array particles,
        IReadOnlyDictionary<int, Candidate> byTextureIndex)
    {
        for (uint particleIndex = 0; particleIndex < particles.Count; particleIndex++)
        {
            int particle = checked((int)(particles.Offset + (long)particleIndex * 504));
            ushort textureIndex = U16(sourceBytes, particle + 22);
            if (textureIndex >= textures.Count)
                throw MalformedRaw(
                    $"particle emitter {particleIndex} references texture {textureIndex}, " +
                    $"count {textures.Count}");
            MarkUse(textureIndex, composites: true, byTextureIndex);
        }
    }

    private static void MarkUse(
        int textureIndex,
        bool composites,
        IReadOnlyDictionary<int, Candidate> byTextureIndex)
    {
        if (!byTextureIndex.TryGetValue(textureIndex, out Candidate? candidate)) return;
        candidate.Used = true;
        if (!composites) candidate.EveryUseComposites = false;
    }

    private static bool UShortArrayInBounds(uint count, uint offset, int fileLength) =>
        count == 0 || offset > 0 && offset + (long)count * sizeof(ushort) <= fileLength;

    private static bool ArrayInBounds(uint count, uint offset, int stride, int fileLength) =>
        count == 0 || offset > 0 && offset + (long)count * stride <= fileLength;

    private static InvalidOperationException MalformedRaw(string reason) =>
        new($"Native effect texture selection rejected malformed source M2: {reason}.");

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));

    private static short I16(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, sizeof(short)));

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));

    private static float F32(byte[] data, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, sizeof(float)));

    /// <summary>
    /// Recolor a PNG to one hue and saturation while retaining each source texel's HSL lightness
    /// and alpha. Transparent-edge color is rebuilt from visible neighbours. Skia discards RGB at
    /// alpha zero when encoding PNG, so only edge texels that receive bleed are nudged from alpha
    /// 0 to 1; authored non-zero alpha is never changed.
    /// </summary>
    /// <param name="lightnessScale">Multiplier on each pixel's lightness (0..1). Effects are
    /// additive, so hue and saturation alone cannot express a DARK pick: black desaturates to a
    /// white glow. Scaling lightness down lets a dark pick dim the effect, all the way to none.</param>
    /// <param name="darkAura">Dark glow pick: the sheet will be drawn ALPHA-BLENDED rather than
    /// additively, so its brightness has to become coverage — alpha is multiplied by the source
    /// pixel's brightness (an additive sheet's black background is "nothing", and must stay nothing
    /// under alpha blending too) while the colour goes to the darkened pick.</param>
    internal static byte[]? TintPng(
        byte[] sourcePng,
        float targetHueDegrees,
        float targetSaturation,
        int bleedPasses = 4,
        float lightnessScale = 1f,
        bool darkAura = false)
    {
        if (sourcePng is not { Length: > 0 } ||
            !float.IsFinite(targetHueDegrees) ||
            !float.IsFinite(targetSaturation) ||
            !float.IsFinite(lightnessScale))
            return null;

        targetHueDegrees = ((targetHueDegrees % 360f) + 360f) % 360f;
        targetSaturation = Math.Clamp(targetSaturation, 0f, 1f);
        lightnessScale = Math.Clamp(lightnessScale, 0f, 1f);
        bleedPasses = Math.Clamp(bleedPasses, 0, 32);

        using SKBitmap? bitmap = DecodeStraightAlpha(sourcePng);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return null;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor source = bitmap.GetPixel(x, y);
                RgbToHsl(source.Red, source.Green, source.Blue, out _, out _, out float lightness);
                HslToRgb(targetHueDegrees, targetSaturation, lightness * lightnessScale,
                    out byte red, out byte green, out byte blue);
                byte alpha = source.Alpha;
                if (darkAura)
                {
                    float coverage = Math.Max(source.Red, Math.Max(source.Green, source.Blue)) / 255f;
                    alpha = (byte)Math.Clamp(MathF.Round(source.Alpha * coverage), 0f, 255f);
                }
                bitmap.SetPixel(x, y, new SKColor(red, green, blue, alpha));
            }
        }

        BleedIntoTransparentEdges(bitmap, bleedPasses);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData? encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray();
    }

    private static SKBitmap? DecodeStraightAlpha(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using SKCodec? codec = SKCodec.Create(stream);
            if (codec is null) return null;

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
                SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            if (codec.GetPixels(info, bitmap.GetPixels()) == SKCodecResult.Success)
                return bitmap;

            bitmap.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void BleedIntoTransparentEdges(SKBitmap bitmap, int passes)
    {
        if (passes <= 0) return;

        int width = bitmap.Width;
        int height = bitmap.Height;
        int count = checked(width * height);
        var hasColor = new bool[count];
        var red = new byte[count];
        var green = new byte[count];
        var blue = new byte[count];
        var alpha = new byte[count];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                SKColor color = bitmap.GetPixel(x, y);
                red[index] = color.Red;
                green[index] = color.Green;
                blue[index] = color.Blue;
                alpha[index] = color.Alpha;
                // Every authored non-zero-alpha texel remains an immutable seed. In particular,
                // faint additive edges (alpha 1..15) keep their own lightness instead of being
                // overwritten by an arbitrary visible-alpha threshold.
                hasColor[index] = color.Alpha > 0;
            }
        }

        for (int pass = 0; pass < passes; pass++)
        {
            var additions = new List<(int Index, byte Red, byte Green, byte Blue)>();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (hasColor[index]) continue;

                    int sumRed = 0, sumGreen = 0, sumBlue = 0, neighbours = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

                            int neighbour = ny * width + nx;
                            if (!hasColor[neighbour]) continue;
                            sumRed += red[neighbour];
                            sumGreen += green[neighbour];
                            sumBlue += blue[neighbour];
                            neighbours++;
                        }
                    }

                    if (neighbours > 0)
                    {
                        additions.Add((index,
                            (byte)(sumRed / neighbours),
                            (byte)(sumGreen / neighbours),
                            (byte)(sumBlue / neighbours)));
                    }
                }
            }

            if (additions.Count == 0) break;
            foreach (var addition in additions)
            {
                red[addition.Index] = addition.Red;
                green[addition.Index] = addition.Green;
                blue[addition.Index] = addition.Blue;
                hasColor[addition.Index] = true;
            }
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (alpha[index] != 0 || !hasColor[index]) continue;

                // Alpha 1 is visually negligible but is the minimum value for which Skia's PNG
                // encoder preserves the bled RGB instead of replacing it with black.
                bitmap.SetPixel(x, y, new SKColor(
                    red[index], green[index], blue[index], 1));
            }
        }
    }

    private static void RgbToHsl(byte red, byte green, byte blue,
        out float hue, out float saturation, out float lightness)
    {
        float r = red / 255f, g = green / 255f, b = blue / 255f;
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;
        lightness = (max + min) * 0.5f;

        if (delta < 0.0001f)
        {
            hue = 0f;
            saturation = 0f;
            return;
        }

        saturation = lightness > 0.5f
            ? delta / (2f - max - min)
            : delta / (max + min);

        if (max == r) hue = ((g - b) / delta + (g < b ? 6f : 0f)) * 60f;
        else if (max == g) hue = ((b - r) / delta + 2f) * 60f;
        else hue = ((r - g) / delta + 4f) * 60f;
    }

    private static void HslToRgb(float hue, float saturation, float lightness,
        out byte red, out byte green, out byte blue)
    {
        if (saturation < 0.0001f)
        {
            byte value = (byte)Math.Clamp(MathF.Round(lightness * 255f), 0f, 255f);
            red = green = blue = value;
            return;
        }

        float chroma = (1f - MathF.Abs(2f * lightness - 1f)) * saturation;
        float x = chroma * (1f - MathF.Abs((hue / 60f) % 2f - 1f));
        float match = lightness - chroma * 0.5f;
        float r = 0f, g = 0f, b = 0f;

        if (hue < 60f) { r = chroma; g = x; }
        else if (hue < 120f) { r = x; g = chroma; }
        else if (hue < 180f) { g = chroma; b = x; }
        else if (hue < 240f) { g = x; b = chroma; }
        else if (hue < 300f) { r = x; b = chroma; }
        else { r = chroma; b = x; }

        red = ToByte(r + match);
        green = ToByte(g + match);
        blue = ToByte(b + match);

        static byte ToByte(float value) =>
            (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
    }
}
