using System.Buffers.Binary;

namespace MangosSuperUI.Services.WeaponForge.RawM2;

/// <summary>
/// Recolors authored RGB animation keys for material color records used exclusively by
/// compositing batches. The edit is offset-stable: it clones the caller's v256 M2 and changes
/// only the twelve bytes of each selected Vector3 value. Track headers, ranges, timestamps,
/// interpolation, global-sequence references, cubic tangents, alpha, and file length are retained
/// byte-for-byte.
/// </summary>
public static class M2MaterialColorHueWriter
{
    private const int ColorRecordSize = 56;
    private const int TrackSize = 28;
    private const int RgbValueSize = 12;
    private const int BatchSize = 24;

    public sealed record Result(
        byte[] M2,
        IReadOnlyList<int> CandidateColorIndices,
        IReadOnlyList<int> ShiftedColorIndices,
        IReadOnlyList<int> SkippedCandidateColorIndices,
        int ColorsChanged,
        int VectorKeysChanged,
        bool IsComplete,
        IReadOnlyList<string> Notes)
    {
        public int ColorsShifted => ColorsChanged;
        public int ColorsHandled => ShiftedColorIndices.Count;
        public int VectorKeysShifted => VectorKeysChanged;
    }

    /// <summary>
    /// Shift every authored RGB key belonging to a color record which is referenced by at least
    /// one blend-mode 3+ batch and by no opaque/invalid batch in any view. When
    /// <paramref name="eligibleTextureIndices"/> is supplied, every sampled texture unit in a
    /// target batch must resolve to one of those explicitly admitted Type-0 slots; use by a
    /// Type-2/Type-3 or otherwise unselected batch makes the shared color record unsafe. The target
    /// saturation is applied uniformly while each key's HSL lightness is preserved. RGB outside
    /// [0,1] and multi-key Hermite/Bezier tracks are deliberately refused rather than clamped or
    /// combined with stale tangent vectors.
    /// </summary>
    public static Result Apply(
        byte[] m2,
        float targetHueDegrees,
        float targetSaturation,
        IReadOnlyCollection<int>? eligibleTextureIndices = null,
        float lightnessScale = 1f)
    {
        ArgumentNullException.ThrowIfNull(m2);

        var notes = new List<string>();
        if (!float.IsFinite(targetHueDegrees) || !float.IsFinite(targetSaturation) || !float.IsFinite(lightnessScale))
            return AdmissionFailure(m2, "target hue, saturation and lightness scale must be finite");

        targetHueDegrees = NormalizeHue(targetHueDegrees);
        targetSaturation = Math.Clamp(targetSaturation, 0f, 1f);
        lightnessScale = Math.Clamp(lightnessScale, 0f, 1f);

        RawM2Document? document = RawM2Document.Parse(m2, out string? parseError);
        if (document is null)
            return AdmissionFailure(m2, parseError ?? "target is not a canonical v256 M2");

        RawM2Array? colors = document.FindArray("colors");
        RawM2Array? renderFlags = document.FindArray("renderFlags");
        RawM2Array? globalLoops = document.FindArray("globalLoops");
        RawM2Array? sequences = document.FindArray("sequences");
        RawM2Array? textures = document.FindArray("textures");
        RawM2Array? textureLookup = document.FindArray("textureLookup");
        if (colors is null || !colors.InBounds)
            return AdmissionFailure(m2, "model carries no valid color record table");
        if (renderFlags is null || !renderFlags.InBounds)
            return AdmissionFailure(m2, "model carries no valid render-flag table");
        if (globalLoops is null || !globalLoops.InBounds || sequences is null || !sequences.InBounds)
            return AdmissionFailure(m2, "model carries malformed animation sequence tables");
        if (colors.Count > int.MaxValue)
            return AdmissionFailure(m2, "color record count is too large to inspect safely");

        HashSet<int>? eligibleTextures = null;
        if (eligibleTextureIndices is not null)
        {
            if (textures is null || !textures.InBounds ||
                textureLookup is null || !textureLookup.InBounds)
                return AdmissionFailure(m2,
                    "model carries a malformed texture table or texture lookup");

            eligibleTextures = eligibleTextureIndices.ToHashSet();
            if (eligibleTextures.Any(index => index < 0 || (uint)index >= textures.Count))
                return AdmissionFailure(m2,
                    "eligible native-effect texture set contains an invalid texture index");
        }

        foreach (RawM2View view in document.Views)
        {
            if (!view.HeaderInBounds || !view.Batches.InBounds)
                return AdmissionFailure(m2, $"view {view.Index} has an invalid header or batch table");
        }

        bool usageGraphComplete = true;
        var usage = new Dictionary<int, ColorUsage>();
        foreach (RawM2View view in document.Views)
        {
            for (uint batchIndex = 0; batchIndex < view.Batches.Count; batchIndex++)
            {
                int batch = checked((int)(view.Batches.Offset + (long)batchIndex * BatchSize));
                short colorIndex = I16(m2, batch + 8);
                if (colorIndex < 0) continue;
                if ((uint)colorIndex >= colors.Count)
                {
                    notes.Add($"view {view.Index} batch {batchIndex} references invalid color {colorIndex}");
                    usageGraphComplete = false;
                    continue;
                }

                if (!usage.TryGetValue(colorIndex, out ColorUsage? colorUsage))
                {
                    colorUsage = new ColorUsage();
                    usage.Add(colorIndex, colorUsage);
                }

                ushort materialIndex = U16(m2, batch + 10);
                if (materialIndex >= renderFlags.Count)
                {
                    colorUsage.UnsafeUse = true;
                    usageGraphComplete = false;
                    notes.Add($"view {view.Index} batch {batchIndex} references invalid material {materialIndex}");
                    continue;
                }

                int material = checked((int)(renderFlags.Offset + (long)materialIndex * 4));
                ushort blendMode = U16(m2, material + 2);
                bool withinNativeEffectBoundary = true;
                if (eligibleTextures is not null)
                {
                    ushort textureCount = U16(m2, batch + 14);
                    ushort textureCombo = U16(m2, batch + 16);
                    if ((long)textureCombo + textureCount > textureLookup!.Count)
                    {
                        colorUsage.UnsafeUse = true;
                        usageGraphComplete = false;
                        notes.Add($"view {view.Index} batch {batchIndex} has an invalid texture span");
                        continue;
                    }

                    withinNativeEffectBoundary = textureCount > 0;
                    bool textureGraphValid = true;
                    for (int unit = 0; unit < textureCount; unit++)
                    {
                        int combo = checked((int)(textureLookup.Offset +
                            ((long)textureCombo + unit) * sizeof(ushort)));
                        ushort textureIndex = U16(m2, combo);
                        if (textureIndex >= textures!.Count)
                        {
                            colorUsage.UnsafeUse = true;
                            usageGraphComplete = false;
                            notes.Add($"view {view.Index} batch {batchIndex} unit {unit} " +
                                      $"references invalid texture {textureIndex}");
                            textureGraphValid = false;
                            break;
                        }

                        if (!eligibleTextures.Contains(textureIndex))
                            withinNativeEffectBoundary = false;
                    }

                    if (!textureGraphValid) continue;
                }

                if (blendMode >= 3 && withinNativeEffectBoundary)
                    colorUsage.HasCompositeUse = true;
                else
                    colorUsage.UnsafeUse = true;
            }
        }

        foreach ((int index, ColorUsage colorUsage) in usage.OrderBy(pair => pair.Key))
        {
            if (colorUsage.HasCompositeUse && colorUsage.UnsafeUse)
                notes.Add($"color {index} skipped because an opaque, invalid, or " +
                          "out-of-boundary batch also uses it");
        }

        int[] candidateIndices = usage
            .Where(pair => pair.Value.HasCompositeUse)
            .Select(pair => pair.Key)
            .OrderBy(index => index)
            .ToArray();
        if (candidateIndices.Length == 0)
            return CreateResult(m2, candidateIndices, Array.Empty<int>(), 0, 0,
                usageGraphComplete, notes);

        var parsedColors = new Dictionary<int, ParsedColor>();
        for (int colorIndex = 0; colorIndex < colors.Count; colorIndex++)
        {
            int record = checked((int)(colors.Offset + (long)colorIndex * ColorRecordSize));
            bool rgbValid = TryReadTrack(m2, record, RgbValueSize,
                globalLoops.Count, sequences.Count, requireFiniteFloats: true,
                out TrackInfo? rgb, out string? rgbError);
            bool alphaValid = TryReadTrack(m2, record + TrackSize, sizeof(short),
                globalLoops.Count, sequences.Count, requireFiniteFloats: false,
                out TrackInfo? alpha, out string? alphaError);

            parsedColors[colorIndex] = new ParsedColor(
                rgbValid ? rgb : null,
                alphaValid ? alpha : null,
                rgbError,
                alphaError,
                ReadDeclaredKeyRange(m2, record, RgbValueSize),
                ReadDeclaredKeyRange(m2, record + TrackSize, sizeof(short)));
        }

        var targets = new Dictionary<int, Target>();
        foreach (int colorIndex in candidateIndices)
        {
            if (usage[colorIndex].UnsafeUse) continue;

            ParsedColor parsed = parsedColors[colorIndex];
            if (parsed.Rgb is null || parsed.Alpha is null)
            {
                notes.Add($"color {colorIndex} skipped because its " +
                    $"{(parsed.Rgb is null ? "RGB" : "alpha")} track is malformed" +
                    $"{FormatReason(parsed.Rgb is null ? parsed.RgbError : parsed.AlphaError)}");
                continue;
            }

            TrackInfo rgb = parsed.Rgb;
            if (rgb.KeyCount == 0)
            {
                notes.Add($"color {colorIndex} skipped because it has no authored RGB keys");
                continue;
            }
            if (rgb.Interpolation is 2 or 3 && rgb.KeyCount > 1)
            {
                notes.Add($"color {colorIndex} skipped because multi-key cubic RGB tracks " +
                          "cannot be hue-shifted without transforming their tangents");
                continue;
            }

            int[] valueOffsets = ValueOffsets(rgb);
            bool normalized = true;
            foreach (int valueOffset in valueOffsets)
            {
                for (int component = 0; component < 3; component++)
                {
                    float value = F32(m2, valueOffset + component * 4);
                    if (value < 0f || value > 1f)
                    {
                        normalized = false;
                        break;
                    }
                }
                if (!normalized) break;
            }
            if (!normalized)
            {
                notes.Add($"color {colorIndex} skipped because an RGB key lies outside [0,1]");
                continue;
            }

            targets.Add(colorIndex, new Target(colorIndex, rgb, valueOffsets));
        }

        if (targets.Count == 0)
            return CreateResult(m2, candidateIndices, Array.Empty<int>(), 0, 0,
                usageGraphComplete, notes);

        List<ByteRange> structuralRanges = StructuralRanges(m2, document, parsedColors.Values);
        var safeTargets = new List<Target>();
        foreach (Target target in targets.Values.OrderBy(target => target.ColorIndex))
        {
            bool unsafeAlias = target.ValueOffsets
                .Select(offset => new ByteRange(offset, offset + RgbValueSize))
                .Any(value => structuralRanges.Any(value.Overlaps));

            if (!unsafeAlias)
            {
                foreach ((int otherIndex, ParsedColor other) in parsedColors)
                {
                    if (otherIndex == target.ColorIndex || other.DeclaredRgbKeys is not { } otherKeys)
                        continue;
                    if (!target.ValueOffsets.Any(offset =>
                            new ByteRange(offset, offset + RgbValueSize).Overlaps(otherKeys)))
                        continue;

                    if (targets.TryGetValue(otherIndex, out Target? otherTarget) &&
                        target.HasIdenticalStorage(otherTarget))
                        continue;

                    unsafeAlias = true;
                    break;
                }
            }

            if (unsafeAlias)
                notes.Add($"color {target.ColorIndex} skipped because its RGB values alias other M2 data");
            else
                safeTargets.Add(target);
        }

        if (safeTargets.Count == 0)
            return CreateResult(m2, candidateIndices, Array.Empty<int>(), 0, 0,
                usageGraphComplete, notes);

        byte[] output = (byte[])m2.Clone();
        var handledOffsets = new HashSet<int>();
        var changedOffsets = new HashSet<int>();
        var shiftedColors = new HashSet<int>();
        foreach (Target target in safeTargets)
        {
            foreach (int valueOffset in target.ValueOffsets)
            {
                if (!handledOffsets.Add(valueOffset)) continue;

                float red = F32(m2, valueOffset);
                float green = F32(m2, valueOffset + 4);
                float blue = F32(m2, valueOffset + 8);
                RgbToHsl(red, green, blue, out _, out _, out float lightness);
                HslToRgb(targetHueDegrees, targetSaturation, lightness * lightnessScale,
                    out red, out green, out blue);
                F32W(output, valueOffset, red);
                F32W(output, valueOffset + 4, green);
                F32W(output, valueOffset + 8, blue);
                if (!m2.AsSpan(valueOffset, RgbValueSize)
                        .SequenceEqual(output.AsSpan(valueOffset, RgbValueSize)))
                    changedOffsets.Add(valueOffset);
            }
            shiftedColors.Add(target.ColorIndex);
        }

        int colorsChanged = safeTargets.Count(target =>
            target.ValueOffsets.Any(changedOffsets.Contains));
        notes.Add($"safely handled {handledOffsets.Count} RGB key(s) across {shiftedColors.Count} " +
                  $"compositing-only material color record(s); changed {changedOffsets.Count} " +
                  $"key(s) across {colorsChanged} record(s)");
        return CreateResult(changedOffsets.Count == 0 ? m2 : output,
            candidateIndices, shiftedColors, colorsChanged, changedOffsets.Count,
            usageGraphComplete, notes);
    }

    private static Result AdmissionFailure(byte[] m2, string note) =>
        new(m2, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0, 0, false,
            new[] { note });

    private static Result CreateResult(
        byte[] m2,
        IEnumerable<int> candidateColorIndices,
        IEnumerable<int> shiftedColorIndices,
        int colorsChanged,
        int vectorKeysChanged,
        bool admissionComplete,
        IReadOnlyList<string> notes)
    {
        int[] candidates = candidateColorIndices.Distinct().OrderBy(index => index).ToArray();
        int[] shifted = shiftedColorIndices.Distinct().OrderBy(index => index).ToArray();
        int[] skipped = candidates.Except(shifted).ToArray();
        return new Result(m2, candidates, shifted, skipped, colorsChanged, vectorKeysChanged,
            admissionComplete && skipped.Length == 0, notes);
    }

    private static bool TryReadTrack(
        byte[] m2,
        int trackOffset,
        int valueStride,
        uint globalSequenceCount,
        uint sequenceCount,
        bool requireFiniteFloats,
        out TrackInfo? track,
        out string? error)
    {
        track = null;
        error = null;
        if (trackOffset < 0 || trackOffset + TrackSize > m2.Length)
            return InvalidTrack("header runs past EOF", out track, out error);

        ushort interpolation = U16(m2, trackOffset);
        short globalSequence = I16(m2, trackOffset + 2);
        uint rangeCount = U32(m2, trackOffset + 4);
        uint rangeOffset = U32(m2, trackOffset + 8);
        uint timeCount = U32(m2, trackOffset + 12);
        uint timeOffset = U32(m2, trackOffset + 16);
        uint keyCount = U32(m2, trackOffset + 20);
        uint keyOffset = U32(m2, trackOffset + 24);

        if (interpolation > 3)
            return InvalidTrack($"has invalid interpolation {interpolation}", out track, out error);
        if (globalSequence < -1 || globalSequence >= 0 && globalSequence >= globalSequenceCount)
            return InvalidTrack($"references invalid global sequence {globalSequence}", out track, out error);
        if (timeCount != keyCount)
            return InvalidTrack($"has {timeCount} timestamps for {keyCount} keys", out track, out error);

        int storedStride = checked(valueStride * (interpolation is 2 or 3 ? 3 : 1));
        if (!TryArrayRange(rangeCount, rangeOffset, 8, m2.Length, out ByteRange? ranges))
            return InvalidTrack("range array runs past EOF", out track, out error);
        if (!TryArrayRange(timeCount, timeOffset, 4, m2.Length, out ByteRange? timestamps))
            return InvalidTrack("timestamp array runs past EOF", out track, out error);
        if (!TryArrayRange(keyCount, keyOffset, storedStride, m2.Length, out ByteRange? keys))
            return InvalidTrack("key array runs past EOF", out track, out error);

        if (keyCount > 0 && globalSequence == -1 && sequenceCount > 0 &&
            rangeCount > 0 && rangeCount < sequenceCount)
            return InvalidTrack($"has {rangeCount} ranges for {sequenceCount} animation sequences", out track, out error);

        for (uint rangeIndex = 0; rangeIndex < rangeCount; rangeIndex++)
        {
            int offset = checked((int)(rangeOffset + (long)rangeIndex * 8));
            uint start = U32(m2, offset);
            uint end = U32(m2, offset + 4);
            if (start > end || end >= keyCount)
                return InvalidTrack($"contains invalid range [{start},{end}] for {keyCount} keys", out track, out error);
        }

        if (requireFiniteFloats && keys is not null)
        {
            long floatCount = (long)keyCount * storedStride / sizeof(float);
            for (long index = 0; index < floatCount; index++)
            {
                float value = F32(m2, checked((int)(keyOffset + index * sizeof(float))));
                if (!float.IsFinite(value))
                    return InvalidTrack("contains a non-finite RGB value", out track, out error);
            }
        }

        track = new TrackInfo(
            interpolation,
            globalSequence,
            rangeCount,
            rangeOffset,
            timeCount,
            timeOffset,
            keyCount,
            keyOffset,
            storedStride,
            ranges,
            timestamps,
            keys);
        return true;
    }

    private static bool InvalidTrack(
        string reason,
        out TrackInfo? track,
        out string? error)
    {
        track = null;
        error = reason;
        return false;
    }

    private static ByteRange? ReadDeclaredKeyRange(byte[] m2, int trackOffset, int valueStride)
    {
        if (trackOffset < 0 || trackOffset + TrackSize > m2.Length) return null;
        ushort interpolation = U16(m2, trackOffset);
        uint keyCount = U32(m2, trackOffset + 20);
        uint keyOffset = U32(m2, trackOffset + 24);
        if (keyCount == 0 || keyOffset == 0) return null;

        int storedStride = valueStride * (interpolation is 2 or 3 ? 3 : 1);
        long end = keyOffset + (long)keyCount * storedStride;
        return end > keyOffset ? new ByteRange(keyOffset, end) : null;
    }

    private static List<ByteRange> StructuralRanges(
        byte[] m2,
        RawM2Document document,
        IEnumerable<ParsedColor> parsedColors)
    {
        var ranges = new List<ByteRange>();
        foreach (RawM2Array array in document.Arrays)
        {
            if (array.Count > 0 && array.InBounds && array.ByteLength is > 0 and var length)
                ranges.Add(new ByteRange(array.Offset, array.Offset + length));
        }

        foreach (RawM2View view in document.Views)
        {
            if (view.HeaderInBounds)
                ranges.Add(new ByteRange(view.HeaderOffset, view.HeaderOffset + RawM2View.HeaderStride));
            AddSubArray(view.VertexLookup);
            AddSubArray(view.Triangles);
            AddSubArray(view.Properties);
            AddSubArray(view.Submeshes);
            AddSubArray(view.Batches);
        }

        foreach (ParsedColor color in parsedColors)
        {
            AddRange(color.Rgb?.Ranges);
            AddRange(color.Rgb?.Timestamps);
            AddRange(color.Alpha?.Ranges);
            AddRange(color.Alpha?.Timestamps);
            AddRange(color.DeclaredAlphaKeys);
        }

        return ranges;

        void AddSubArray(RawM2SubArray array)
        {
            if (array.Count > 0 && array.InBounds)
                ranges.Add(new ByteRange(array.Offset, array.EndOffset));
        }

        void AddRange(ByteRange? range)
        {
            if (range is { } value && value.Start >= 0 && value.End <= m2.Length)
                ranges.Add(value);
        }
    }

    private static int[] ValueOffsets(TrackInfo track)
    {
        var offsets = new int[checked((int)track.KeyCount)];
        for (int key = 0; key < offsets.Length; key++)
            offsets[key] = checked((int)(track.KeyOffset + (long)key * track.StoredStride));
        return offsets;
    }

    private static bool TryArrayRange(
        uint count,
        uint offset,
        int stride,
        int fileLength,
        out ByteRange? range)
    {
        range = null;
        if (count == 0) return true;
        long end = offset + (long)count * stride;
        if (offset == 0 || end > fileLength) return false;
        range = new ByteRange(offset, end);
        return true;
    }

    private static string FormatReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})";

    private static float NormalizeHue(float hue) => ((hue % 360f) + 360f) % 360f;

    private static void RgbToHsl(
        float red,
        float green,
        float blue,
        out float hue,
        out float saturation,
        out float lightness)
    {
        float max = MathF.Max(red, MathF.Max(green, blue));
        float min = MathF.Min(red, MathF.Min(green, blue));
        float delta = max - min;
        lightness = (max + min) * 0.5f;
        if (delta < 0.000001f)
        {
            hue = 0f;
            saturation = 0f;
            return;
        }

        saturation = lightness > 0.5f
            ? delta / (2f - max - min)
            : delta / (max + min);
        if (max == red) hue = ((green - blue) / delta + (green < blue ? 6f : 0f)) * 60f;
        else if (max == green) hue = ((blue - red) / delta + 2f) * 60f;
        else hue = ((red - green) / delta + 4f) * 60f;
    }

    private static void HslToRgb(
        float hue,
        float saturation,
        float lightness,
        out float red,
        out float green,
        out float blue)
    {
        if (saturation < 0.000001f || lightness <= 0f || lightness >= 1f)
        {
            red = green = blue = lightness;
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
        red = r + match;
        green = g + match;
        blue = b + match;
    }

    private sealed class ColorUsage
    {
        public bool HasCompositeUse { get; set; }
        public bool UnsafeUse { get; set; }
    }

    private sealed record TrackInfo(
        ushort Interpolation,
        short GlobalSequence,
        uint RangeCount,
        uint RangeOffset,
        uint TimeCount,
        uint TimeOffset,
        uint KeyCount,
        uint KeyOffset,
        int StoredStride,
        ByteRange? Ranges,
        ByteRange? Timestamps,
        ByteRange? Keys);

    private sealed record ParsedColor(
        TrackInfo? Rgb,
        TrackInfo? Alpha,
        string? RgbError,
        string? AlphaError,
        ByteRange? DeclaredRgbKeys,
        ByteRange? DeclaredAlphaKeys);

    private sealed record Target(int ColorIndex, TrackInfo Rgb, int[] ValueOffsets)
    {
        public bool HasIdenticalStorage(Target other) =>
            Rgb.Interpolation == other.Rgb.Interpolation &&
            Rgb.KeyCount == other.Rgb.KeyCount &&
            Rgb.KeyOffset == other.Rgb.KeyOffset &&
            Rgb.StoredStride == other.Rgb.StoredStride;
    }

    private readonly record struct ByteRange(long Start, long End)
    {
        public bool Overlaps(ByteRange other) => Start < other.End && other.Start < End;
    }

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static short I16(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static float F32(byte[] data, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));

    private static void F32W(byte[] data, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);
}
