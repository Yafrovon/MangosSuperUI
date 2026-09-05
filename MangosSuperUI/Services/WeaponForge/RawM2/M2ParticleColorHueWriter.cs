using System.Buffers.Binary;

namespace MangosSuperUI.Services.WeaponForge.RawM2;

/// <summary>
/// Recolors the three inline BGRA color keys in selected vanilla particle-emitter records. The edit
/// is deliberately narrow and fail-closed: the complete particle table is validated before a
/// clone is made, and the only bytes that can change are the RGB bytes of the three color keys.
/// Emitter IDs, alpha, timing, motion, nested arrays, and file length are preserved byte-for-byte.
/// </summary>
public static class M2ParticleColorHueWriter
{
    private const int TextureArrayHeaderOffset = 0x5C;
    private const int TextureRecordSize = 16;
    private const int ParticleArrayHeaderOffset = 0x13C;
    private const int ParticleRecordSize = 504;
    private const int ParticleTextureIndexOffset = 22;
    private const int ColorRampOffset = 336;
    private const int ColorKeyCount = 3;
    private const int ColorKeySize = 4;

    public sealed record Result(
        byte[] M2,
        int CandidateEmitters,
        int EmittersHandled,
        int EmittersChanged,
        int ColorKeysHandled,
        int ColorKeysChanged,
        bool IsComplete,
        IReadOnlyList<string> Notes)
    {
        public int EmittersSkipped => Math.Max(0, CandidateEmitters - EmittersHandled);
    }

    /// <summary>
    /// Apply the requested HSL hue and saturation to all three authored color keys in each
    /// selected particle emitter. A null texture filter selects every declared emitter. A supplied
    /// filter first validates the raw texture table and every emitter texture reference, then
    /// selects only emitters whose texture index is eligible. Each key keeps its original HSL
    /// lightness and exact alpha.
    /// A malformed/truncated/non-v256 input is returned unchanged with <see cref="Result.IsComplete"/>
    /// false; a valid model with no particles is a complete no-op.
    /// </summary>
    public static Result Apply(
        byte[] m2,
        float targetHueDegrees,
        float targetSaturation,
        IReadOnlyCollection<int>? eligibleTextureIndices = null,
        float lightnessScale = 1f)
    {
        ArgumentNullException.ThrowIfNull(m2);

        if (!float.IsFinite(targetHueDegrees) || !float.IsFinite(targetSaturation) || !float.IsFinite(lightnessScale))
            return Failure(m2, 0, "target hue, saturation and lightness scale must be finite");

        targetHueDegrees = NormalizeHue(targetHueDegrees);
        targetSaturation = Math.Clamp(targetSaturation, 0f, 1f);
        lightnessScale = Math.Clamp(lightnessScale, 0f, 1f);

        if (m2.Length < RawM2Document.VanillaHeaderSize)
            return Failure(m2, 0,
                $"too small ({m2.Length} bytes) for a v256 M2 header ({RawM2Document.VanillaHeaderSize})");
        if (!m2.AsSpan(0, 4).SequenceEqual("MD20"u8))
            return Failure(m2, 0, "target is not an MD20 M2");
        if (U32(m2, 0x04) != 256)
            return Failure(m2, 0, $"unsupported M2 version {U32(m2, 0x04)} (expected 256)");

        uint declaredCount = U32(m2, ParticleArrayHeaderOffset);
        uint tableOffset = U32(m2, ParticleArrayHeaderOffset + 4);
        if (declaredCount > int.MaxValue)
            return Failure(m2, int.MaxValue, "particle-emitter count is too large to inspect safely");

        int declaredEmitters = (int)declaredCount;
        long tableEnd = (long)tableOffset + (long)declaredCount * ParticleRecordSize;
        if (declaredCount > 0 &&
            (tableOffset < RawM2Document.VanillaHeaderSize || tableEnd > m2.Length))
        {
            return Failure(m2, eligibleTextureIndices is null ? declaredEmitters : 0,
                $"particle-emitter table is malformed or truncated " +
                $"(count {declaredCount}, offset 0x{tableOffset:X}, end 0x{tableEnd:X}, file 0x{m2.Length:X})");
        }

        HashSet<int>? eligibleTextures = null;
        uint textureCount = 0;
        if (eligibleTextureIndices is not null)
        {
            textureCount = U32(m2, TextureArrayHeaderOffset);
            uint textureTableOffset = U32(m2, TextureArrayHeaderOffset + 4);
            long textureTableEnd = (long)textureTableOffset + (long)textureCount * TextureRecordSize;
            if (textureCount > int.MaxValue ||
                (textureCount > 0 &&
                 (textureTableOffset < RawM2Document.VanillaHeaderSize || textureTableEnd > m2.Length)))
            {
                return Failure(m2, 0,
                    $"texture table is malformed or truncated " +
                    $"(count {textureCount}, offset 0x{textureTableOffset:X}, " +
                    $"end 0x{textureTableEnd:X}, file 0x{m2.Length:X})");
            }

            eligibleTextures = new HashSet<int>();
            foreach (int textureIndex in eligibleTextureIndices)
            {
                if (textureIndex < 0 || (uint)textureIndex >= textureCount)
                {
                    return Failure(m2, 0,
                        $"eligible texture index {textureIndex} is outside the raw texture table " +
                        $"(count {textureCount})");
                }
                eligibleTextures.Add(textureIndex);
            }
        }

        var candidateEmitterIndices = new List<int>(
            eligibleTextures is null ? declaredEmitters : Math.Min(declaredEmitters, eligibleTextures.Count));
        for (int emitterIndex = 0; emitterIndex < declaredEmitters; emitterIndex++)
        {
            if (eligibleTextures is null)
            {
                candidateEmitterIndices.Add(emitterIndex);
                continue;
            }

            int recordOffset = checked((int)((long)tableOffset + (long)emitterIndex * ParticleRecordSize));
            ushort textureIndex = U16(m2, recordOffset + ParticleTextureIndexOffset);
            if (textureIndex >= textureCount)
            {
                return Failure(m2, 0,
                    $"particle emitter {emitterIndex} references texture {textureIndex}, " +
                    $"outside the raw texture table (count {textureCount})");
            }
            if (eligibleTextures.Contains(textureIndex))
                candidateEmitterIndices.Add(emitterIndex);
        }

        int candidateEmitters = candidateEmitterIndices.Count;

        // Compute all replacement values before cloning so expected malformed input can never
        // yield a partially changed buffer. The fixed record/table validation above makes every
        // span below safe; emitter IDs are intentionally not interpreted or filtered.
        var replacements = new uint[checked(candidateEmitters * ColorKeyCount)];
        int emittersChanged = 0;
        int colorKeysChanged = 0;
        for (int candidateIndex = 0; candidateIndex < candidateEmitters; candidateIndex++)
        {
            int emitterIndex = candidateEmitterIndices[candidateIndex];
            int recordOffset = checked((int)((long)tableOffset + (long)emitterIndex * ParticleRecordSize));
            bool emitterChanged = false;
            for (int keyIndex = 0; keyIndex < ColorKeyCount; keyIndex++)
            {
                int colorOffset = recordOffset + ColorRampOffset + keyIndex * ColorKeySize;
                uint original = U32(m2, colorOffset);
                uint recolored = RecolorArgb(original, targetHueDegrees, targetSaturation, lightnessScale);
                replacements[candidateIndex * ColorKeyCount + keyIndex] = recolored;
                if (recolored == original) continue;

                emitterChanged = true;
                colorKeysChanged++;
            }

            if (emitterChanged) emittersChanged++;
        }

        int colorKeysHandled = checked(candidateEmitters * ColorKeyCount);
        if (colorKeysChanged == 0)
        {
            return Success(m2, candidateEmitters, emittersChanged, colorKeysHandled,
                colorKeysChanged);
        }

        byte[] output = (byte[])m2.Clone();
        for (int candidateIndex = 0; candidateIndex < candidateEmitters; candidateIndex++)
        {
            int emitterIndex = candidateEmitterIndices[candidateIndex];
            int recordOffset = checked((int)((long)tableOffset + (long)emitterIndex * ParticleRecordSize));
            for (int keyIndex = 0; keyIndex < ColorKeyCount; keyIndex++)
            {
                int colorOffset = recordOffset + ColorRampOffset + keyIndex * ColorKeySize;
                U32W(output, colorOffset, replacements[candidateIndex * ColorKeyCount + keyIndex]);
            }
        }

        return Success(output, candidateEmitters, emittersChanged, colorKeysHandled,
            colorKeysChanged);
    }

    private static Result Success(
        byte[] m2,
        int emitterCount,
        int emittersChanged,
        int colorKeysHandled,
        int colorKeysChanged) =>
        new(m2, emitterCount, emitterCount, emittersChanged, colorKeysHandled, colorKeysChanged,
            true,
            new[]
            {
                $"safely handled {colorKeysHandled} particle color key(s) across " +
                $"{emitterCount} emitter(s); changed {colorKeysChanged} key(s) across " +
                $"{emittersChanged} emitter(s)",
            });

    private static Result Failure(byte[] m2, int candidateEmitters, string note) =>
        new(m2, candidateEmitters, 0, 0, 0, 0, false, new[] { note });

    private static uint RecolorArgb(uint original, float hueDegrees, float saturation, float lightnessScale)
    {
        byte alpha = (byte)(original >> 24);
        byte red = (byte)(original >> 16);
        byte green = (byte)(original >> 8);
        byte blue = (byte)original;

        RgbToHsl(red, green, blue, out float lightness);
        HslToRgb(hueDegrees, saturation, lightness * lightnessScale, out red, out green, out blue);

        return ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
    }

    private static float NormalizeHue(float hue) => ((hue % 360f) + 360f) % 360f;

    private static void RgbToHsl(byte red, byte green, byte blue, out float lightness)
    {
        float redF = red / 255f;
        float greenF = green / 255f;
        float blueF = blue / 255f;
        float max = MathF.Max(redF, MathF.Max(greenF, blueF));
        float min = MathF.Min(redF, MathF.Min(greenF, blueF));
        lightness = (max + min) * 0.5f;
    }

    private static void HslToRgb(
        float hue,
        float saturation,
        float lightness,
        out byte red,
        out byte green,
        out byte blue)
    {
        if (saturation < 0.000001f || lightness <= 0f || lightness >= 1f)
        {
            byte value = ToByte(lightness);
            red = green = blue = value;
            return;
        }

        float chroma = (1f - MathF.Abs(2f * lightness - 1f)) * saturation;
        float x = chroma * (1f - MathF.Abs((hue / 60f) % 2f - 1f));
        float match = lightness - chroma * 0.5f;
        float redF = 0f, greenF = 0f, blueF = 0f;
        if (hue < 60f) { redF = chroma; greenF = x; }
        else if (hue < 120f) { redF = x; greenF = chroma; }
        else if (hue < 180f) { greenF = chroma; blueF = x; }
        else if (hue < 240f) { greenF = x; blueF = chroma; }
        else if (hue < 300f) { redF = x; blueF = chroma; }
        else { redF = chroma; blueF = x; }

        red = ToByte(redF + match);
        green = ToByte(greenF + match);
        blue = ToByte(blueF + match);
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp(value * 255f + 0.5f, 0f, 255f);

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));

    private static void U32W(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)), value);
}
