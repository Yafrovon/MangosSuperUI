using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Surgically patches particle emitter parameters in existing M2 model files.
/// 
/// VERIFIED OFFSETS (empirically confirmed via hex scan of 8 vanilla spell M2 files):
///
/// M2 Header (vanilla v256, 0x148 bytes total):
///   0x0B4-0x0CC: BoundingBox/CollisionBox floats (NOT lights/cameras as wowdev wiki suggests)
///   0x0D0-0x130: Empty M2Arrays (lights, cameras, ribbons — all zero for spell effects)
///   0x134:       M2Array — secondary emitters (ribbon-like, simpler struct ~0xDC bytes)
///   0x13C:       M2Array — particle emitters (full struct, 0x1F8 = 504 bytes each) ← PRIMARY
///
/// Particle emitter struct (M2Particle, 0x1F8 = 504 bytes):
///
/// ═══════════════════════════════════════════════════════════════════════
/// VANILLA v256 M2Track<float> FORMAT — EMPIRICALLY VERIFIED (Session 11)
/// ═══════════════════════════════════════════════════════════════════════
///
/// Each M2Track<float> in vanilla is 28 bytes, starting with 0x0000FFFF marker:
///   +0x00: 0x0000FFFF (4 bytes) — separator/marker
///   +0x04: 0x00000000 (4 bytes) — padding
///   +0x08: 0x00000000 (4 bytes) — padding
///   +0x0C: M2Array count (uint32) — timestamp count (matches value count)
///   +0x10: M2Array offset (uint32) — abs file offset to timestamp data
///   +0x14: M2Array count (uint32) — value count (usually 1 for static)
///   +0x18: M2Array offset (uint32) — abs file offset to float value(s)
///
/// The M2Track zone starts at emitter+0x034 and contains 10 consecutive
/// 28-byte M2Track blocks. The VALUE M2Array is at bytes 20-27 of each block.
///
/// IMPORTANT: The actual float value lives at a different offset in the file
/// (pointed to by the M2Array offset). To patch, we follow the pointer and
/// write the new float value at that target offset. No structural changes.
///
///   Field layout (relative to emitter base):
///     +0x000: int32   particleId (usually -1)
///     +0x004: uint32  flags
///     +0x008: vec3    position (12 bytes)
///     +0x014: uint16  boneId
///     +0x016: uint16  textureId
///     +0x018: M2Array modelFilename (8 bytes)
///     +0x020: M2Array particleFilename (8 bytes)
///     +0x028: uint8   blendingType
///     +0x029: uint8   emitterType
///     +0x02A: uint16  particleColorIndex
///     +0x02C: 8 bytes — emissionSpeed header/static value
///     +0x034: 10 × 28-byte M2Track blocks (M2Track zone, 280 bytes)
///     +0x14C: float   midpoint (inline fixed)
///     +0x150: uint32[3] colorValues START/MID/END (ARGB) ← PATCHED (existing)
///     +0x15C: float[3]  scaleValues START/MID/END ← PATCHED (existing)
///     +0x168+: headLifespan, headDecay, tailLifespan, tailDecay, etc.
///
/// ═══════════════════════════════════════════════════════════════════════
/// M2TRACK VALUE OFFSETS (confirmed across all 4 Fireball spell M2 files)
/// ═══════════════════════════════════════════════════════════════════════
///
///   Block  FFFF@    ValArr@   Property              Vanilla values
///   0      +0x034   +0x048    emissionSpeed          0.17–6.46 m/s
///   1      +0x050   +0x064    speedVariation         0.07–0.72 (fraction)
///   2      +0x06C   +0x080    verticalRange          0.017–π radians
///   3      +0x088   +0x09C    horizontalRange        0–2π radians
///   4      +0x0A4   +0x0B8    gravity                -3.47 to 6.67
///   5      +0x0C0   +0x0D4    lifespan               0.25–1.0 seconds
///   6      +0x0DC   +0x0F0    emissionRate           10–74 particles/sec ★
///   7      +0x0F8   +0x10C    emissionAreaLength     0.006–0.83 units
///   8      +0x114   +0x128    emissionAreaWidth      0.006–0.83 units
///   9      +0x130   +0x144    zSource/deceleration   rarely non-zero
///
/// ⚠️ SESSION 9: Removed PatchM2TracksInRange — it was corrupting bone IDs.
/// ✅ SESSION 11: Replaced with proper M2Track parser that follows M2Array
///    pointers to the actual float data. No structural changes to the file.
/// </summary>
public class M2ParticlePatcher
{
    /// <summary>Parameters for patching particle emitters.</summary>
    public class ParticlePatchParams
    {
        /// <summary>New start/mid/end colors as 0xAARRGGBB. Null = don't change.
        /// When UseHueShift is false (default), these replace emitter colors directly.
        /// When UseHueShift is true, these are ignored — HueShiftColor is used instead.</summary>
        public uint[]? ColorValues { get; set; }

        /// <summary>When true, use hue-shift mode: compute the hue offset between each emitter's
        /// original dominant color and HueShiftColor, then apply that delta to preserve the
        /// emitter's original luminance/saturation relationships. This keeps multi-emitter M2s
        /// looking natural (bright core vs dark swirl vs subtle accent).</summary>
        public bool UseHueShift { get; set; } = false;

        /// <summary>Target color as 0x00RRGGBB for hue-shift mode. Only used when UseHueShift=true.
        /// The hue of this color becomes the target hue; each emitter's existing colors are
        /// rotated to match while preserving their original brightness/saturation spread.</summary>
        public uint HueShiftColor { get; set; }

        /// <summary>Multiplier for emission rate (M2Track). 1.0 = unchanged. Null = don't change.</summary>
        public float? EmissionRateMultiplier { get; set; }

        /// <summary>Multiplier for emission area length+width (M2Track). 1.0 = unchanged. Null = don't change.</summary>
        public float? EmissionAreaMultiplier { get; set; }

        /// <summary>Multiplier for particle scale values (inline fixed). 1.0 = unchanged. Null = don't change.</summary>
        public float? ScaleMultiplier { get; set; }

        /// <summary>Multiplier for particle lifespan (M2Track). 1.0 = unchanged. Null = don't change.</summary>
        public float? LifespanMultiplier { get; set; }

        /// <summary>Multiplier for emission speed (M2Track). 1.0 = unchanged. Null = don't change.</summary>
        public float? EmissionSpeedMultiplier { get; set; }

        /// <summary>Additive gravity adjustment (M2Track). Positive = downward. Null = don't change.</summary>
        public float? GravityAdd { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // VERIFIED HEADER OFFSETS (from empirical hex scan, April 2026)
    // ═══════════════════════════════════════════════════════════════════════════

    private const int HEADER_OFS_PARTICLES_PRIMARY = 0x13C;
    private const int HEADER_OFS_PARTICLES_SECONDARY = 0x134;
    private const int PARTICLE_STRUCT_SIZE_PRIMARY = 0x1F8; // 504 bytes
    private const int PARTICLE_STRUCT_SIZE_SECONDARY = 0xDC; // 220 bytes
    private const int MIN_HEADER_SIZE = 0x148;

    // ═══════════════════════════════════════════════════════════════════════════
    // VERIFIED INLINE FIXED FIELD OFFSETS (relative to emitter start)
    // ═══════════════════════════════════════════════════════════════════════════

    private const int REL_MIDPOINT = 0x14C;
    private const int REL_COLOR_VALUES = 0x150;  // uint32[3]
    private const int REL_SCALE_VALUES = 0x15C;  // float[3]

    // ═══════════════════════════════════════════════════════════════════════════
    // M2TRACK VALUE M2ARRAY OFFSETS (verified Session 11)
    // Each is the offset within the emitter struct where the M2Array (count+offset)
    // for that property's VALUE data lives. The M2Array points to the actual
    // float value(s) elsewhere in the file.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>M2Track value M2Array for emission speed. Offset relative to emitter base.</summary>
    private const int REL_M2TRACK_EMISSION_SPEED = 0x048;

    /// <summary>M2Track value M2Array for speed variation. Offset relative to emitter base.</summary>
    private const int REL_M2TRACK_SPEED_VARIATION = 0x064;

    /// <summary>M2Track value M2Array for vertical range (radians). Offset relative to emitter base.</summary>
    private const int REL_M2TRACK_VERTICAL_RANGE = 0x080;

    /// <summary>M2Track value M2Array for horizontal range (radians). Offset relative to emitter base.</summary>
    private const int REL_M2TRACK_HORIZONTAL_RANGE = 0x09C;

    /// <summary>M2Track value M2Array for gravity. Offset relative to emitter base.</summary>
    private const int REL_M2TRACK_GRAVITY = 0x0B8;

    /// <summary>M2Track value M2Array for lifespan (seconds). Offset relative to emitter base.</summary>
    private const int REL_M2TRACK_LIFESPAN = 0x0D4;

    /// <summary>M2Track value M2Array for emission rate (particles/sec). Offset relative to emitter base.</summary>
    private const int REL_M2TRACK_EMISSION_RATE = 0x0F0;

    /// <summary>M2Track value M2Array for emission area length. Offset relative to emitter base.</summary>
    private const int REL_M2TRACK_AREA_LENGTH = 0x10C;

    /// <summary>M2Track value M2Array for emission area width. Offset relative to emitter base.</summary>
    private const int REL_M2TRACK_AREA_WIDTH = 0x128;

    // Secondary emitter inline float offsets (unchanged from previous version)
    private const int REL_SEC_EMISSION_FLOAT = 0x94;

    // ═══════════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Patch all particle emitters in an M2 file with the given parameters.
    /// Handles both primary (0x13C) and secondary (0x134) emitter arrays.
    /// Returns a new byte array with modifications applied, or null on failure.
    /// </summary>
    public static byte[]? PatchParticles(byte[] m2Data, ParticlePatchParams patchParams)
    {
        if (m2Data == null || m2Data.Length < MIN_HEADER_SIZE)
            return null;

        if (Encoding.ASCII.GetString(m2Data, 0, 4) != "MD20")
            return null;

        uint version = BitConverter.ToUInt32(m2Data, 4);
        if (version != 256)
            return null;

        byte[] result = (byte[])m2Data.Clone();
        int totalPatched = 0;

        // ── Patch primary emitters (0x13C) — the main particle effects ──
        totalPatched += PatchEmitterArray(result, HEADER_OFS_PARTICLES_PRIMARY,
            PARTICLE_STRUCT_SIZE_PRIMARY, patchParams, isPrimary: true);

        // ── Patch secondary emitters (0x134) — simpler ribbon-like effects ──
        totalPatched += PatchEmitterArray(result, HEADER_OFS_PARTICLES_SECONDARY,
            PARTICLE_STRUCT_SIZE_SECONDARY, patchParams, isPrimary: false);

        return totalPatched > 0 ? result : (byte[])m2Data.Clone();
    }

    private static int PatchEmitterArray(byte[] data, int headerOffset, int structSize,
        ParticlePatchParams p, bool isPrimary)
    {
        if (headerOffset + 8 > data.Length) return 0;

        uint count = BitConverter.ToUInt32(data, headerOffset);
        uint offset = BitConverter.ToUInt32(data, headerOffset + 4);
        if (count == 0 || offset == 0 || offset >= data.Length) return 0;

        int patched = 0;
        for (uint i = 0; i < count; i++)
        {
            int emitterBase = (int)(offset + i * structSize);
            if (emitterBase + structSize > data.Length) break;

            int particleId = BitConverter.ToInt32(data, emitterBase);
            if (particleId != -1 && (particleId < 0 || particleId > 100)) continue;

            if (isPrimary)
            {
                PatchPrimaryEmitter(data, emitterBase, p);
            }
            else
            {
                PatchSecondaryEmitter(data, emitterBase, p);
            }
            patched++;
        }
        return patched;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PRIMARY EMITTER PATCHING
    // ═══════════════════════════════════════════════════════════════════════════

    private static void PatchPrimaryEmitter(byte[] data, int emitterBase, ParticlePatchParams p)
    {
        // ── Inline fixed values (existing — verified Session 10) ──

        // Color values at +0x150 (3 × uint32 ARGB)
        if (p.UseHueShift)
        {
            // Hue-shift mode: read each emitter's existing colors, compute hue delta,
            // apply to preserve luminance/saturation relationships
            int colorOfs = emitterBase + REL_COLOR_VALUES;
            if (colorOfs + 12 <= data.Length)
            {
                for (int c = 0; c < 3; c++)
                {
                    int off = colorOfs + c * 4;
                    uint original = BitConverter.ToUInt32(data, off);
                    uint shifted = HueShiftArgb(original, p.HueShiftColor);
                    WriteUInt32(data, off, shifted);
                }
            }
        }
        else if (p.ColorValues != null && p.ColorValues.Length >= 3)
        {
            for (int c = 0; c < 3; c++)
            {
                int off = emitterBase + REL_COLOR_VALUES + c * 4;
                if (off + 4 <= data.Length)
                    WriteUInt32(data, off, p.ColorValues[c]);
            }
        }

        // Scale values at +0x15C (3 × float)
        if (p.ScaleMultiplier.HasValue)
        {
            for (int s = 0; s < 3; s++)
            {
                int off = emitterBase + REL_SCALE_VALUES + s * 4;
                if (off + 4 <= data.Length)
                {
                    float orig = BitConverter.ToSingle(data, off);
                    WriteFloat(data, off, orig * p.ScaleMultiplier.Value);
                }
            }
        }

        // ── M2Track animated values (NEW — Session 11) ──
        // Follow M2Array pointers to patch the actual float data in the file.
        // No structural changes — just modify the float(s) at the target offset.

        if (p.EmissionRateMultiplier.HasValue)
        {
            PatchM2TrackValues(data, emitterBase, REL_M2TRACK_EMISSION_RATE,
                v => v * p.EmissionRateMultiplier.Value, "emissionRate");
        }

        if (p.LifespanMultiplier.HasValue)
        {
            PatchM2TrackValues(data, emitterBase, REL_M2TRACK_LIFESPAN,
                v => v * p.LifespanMultiplier.Value, "lifespan");
        }

        if (p.EmissionAreaMultiplier.HasValue)
        {
            PatchM2TrackValues(data, emitterBase, REL_M2TRACK_AREA_LENGTH,
                v => v * p.EmissionAreaMultiplier.Value, "areaLength");
            PatchM2TrackValues(data, emitterBase, REL_M2TRACK_AREA_WIDTH,
                v => v * p.EmissionAreaMultiplier.Value, "areaWidth");
        }

        if (p.EmissionSpeedMultiplier.HasValue)
        {
            PatchM2TrackValues(data, emitterBase, REL_M2TRACK_EMISSION_SPEED,
                v => v * p.EmissionSpeedMultiplier.Value, "emissionSpeed");
        }

        if (p.GravityAdd.HasValue)
        {
            PatchM2TrackValues(data, emitterBase, REL_M2TRACK_GRAVITY,
                v => v + p.GravityAdd.Value, "gravity");
        }
    }

    /// <summary>
    /// Follow an M2Track's value M2Array pointer and apply a transform to all float values.
    /// 
    /// The M2Array at (emitterBase + m2ArrayOffset) contains:
    ///   [0:4] uint32 count — number of float values
    ///   [4:8] uint32 offset — absolute file offset to the float array
    /// 
    /// We read each float at the target offset and apply the transform function.
    /// This is safe because we only modify the float data, never the M2Array itself.
    /// </summary>
    private static void PatchM2TrackValues(byte[] data, int emitterBase, int m2ArrayOffset,
        Func<float, float> transform, string fieldName)
    {
        int arrayOff = emitterBase + m2ArrayOffset;
        if (arrayOff + 8 > data.Length) return;

        uint count = BitConverter.ToUInt32(data, arrayOff);
        uint dataOffset = BitConverter.ToUInt32(data, arrayOff + 4);

        // Validate
        if (count == 0 || count > 100) return;
        if (dataOffset == 0 || dataOffset >= data.Length) return;
        if (dataOffset + count * 4 > data.Length) return;

        for (uint i = 0; i < count; i++)
        {
            int valOff = (int)(dataOffset + i * 4);
            float original = BitConverter.ToSingle(data, valOff);

            // Skip zero values (e.g., animated emission rate that starts at 0 before ramping)
            // and NaN/Inf values
            if (float.IsNaN(original) || float.IsInfinity(original))
                continue;

            // For multipliers, skip zero (multiplying zero stays zero)
            // For additive (gravity), always apply
            float newValue = transform(original);

            // Sanity clamp
            if (float.IsNaN(newValue) || float.IsInfinity(newValue))
                continue;

            WriteFloat(data, valOff, newValue);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SECONDARY EMITTER PATCHING (unchanged from Session 9)
    // ═══════════════════════════════════════════════════════════════════════════

    private static void PatchSecondaryEmitter(byte[] data, int emitterBase, ParticlePatchParams p)
    {
        if (p.EmissionRateMultiplier.HasValue)
        {
            int rateOfs = emitterBase + REL_SEC_EMISSION_FLOAT;
            if (rateOfs + 4 <= data.Length)
            {
                float orig = BitConverter.ToSingle(data, rateOfs);
                if (orig > 0.1f && orig < 1000f)
                    WriteFloat(data, rateOfs, orig * p.EmissionRateMultiplier.Value);
            }
        }

        if (p.LifespanMultiplier.HasValue)
        {
            int lifespanOfs = emitterBase + REL_SEC_EMISSION_FLOAT + 4;
            if (lifespanOfs + 4 <= data.Length)
            {
                float orig = BitConverter.ToSingle(data, lifespanOfs);
                if (orig > 0.01f && orig < 100f)
                    WriteFloat(data, lifespanOfs, orig * p.LifespanMultiplier.Value);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DIAGNOSTICS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Read particle emitter info from an M2 for diagnostic purposes.
    /// Now includes M2Track animated values.
    /// </summary>
    public static List<ParticleEmitterInfo> ReadParticleInfo(byte[] m2Data)
    {
        var result = new List<ParticleEmitterInfo>();
        if (m2Data == null || m2Data.Length < MIN_HEADER_SIZE) return result;
        if (Encoding.ASCII.GetString(m2Data, 0, 4) != "MD20") return result;

        ReadEmitterArray(m2Data, HEADER_OFS_PARTICLES_PRIMARY,
            PARTICLE_STRUCT_SIZE_PRIMARY, result, isPrimary: true);

        ReadEmitterArray(m2Data, HEADER_OFS_PARTICLES_SECONDARY,
            PARTICLE_STRUCT_SIZE_SECONDARY, result, isPrimary: false);

        return result;
    }

    private static void ReadEmitterArray(byte[] data, int headerOffset, int structSize,
        List<ParticleEmitterInfo> results, bool isPrimary)
    {
        if (headerOffset + 8 > data.Length) return;

        uint count = BitConverter.ToUInt32(data, headerOffset);
        uint offset = BitConverter.ToUInt32(data, headerOffset + 4);
        if (count == 0 || offset == 0 || offset >= data.Length) return;

        for (uint i = 0; i < count; i++)
        {
            int baseOff = (int)(offset + i * structSize);
            if (baseOff + structSize > data.Length) break;

            int particleId = BitConverter.ToInt32(data, baseOff);
            if (particleId != -1 && (particleId < 0 || particleId > 100)) continue;

            var info = new ParticleEmitterInfo
            {
                Index = results.Count,
                EmitterOffset = baseOff,
                StructSize = structSize,
                IsPrimary = isPrimary,
                ParticleId = particleId,
                Flags = BitConverter.ToUInt32(data, baseOff + 4),
                PositionX = BitConverter.ToSingle(data, baseOff + 8),
                PositionY = BitConverter.ToSingle(data, baseOff + 12),
                PositionZ = BitConverter.ToSingle(data, baseOff + 16),
                BoneId = BitConverter.ToUInt16(data, baseOff + 20),
                TextureId = BitConverter.ToUInt16(data, baseOff + 22),
            };

            if (isPrimary)
            {
                int colorOfs = baseOff + REL_COLOR_VALUES;
                int scaleOfs = baseOff + REL_SCALE_VALUES;
                int midpointOfs = baseOff + REL_MIDPOINT;

                if (colorOfs + 12 <= data.Length)
                {
                    info.Midpoint = BitConverter.ToSingle(data, midpointOfs);
                    info.ColorStart = BitConverter.ToUInt32(data, colorOfs);
                    info.ColorMid = BitConverter.ToUInt32(data, colorOfs + 4);
                    info.ColorEnd = BitConverter.ToUInt32(data, colorOfs + 8);
                }
                if (scaleOfs + 12 <= data.Length)
                {
                    info.ScaleStart = BitConverter.ToSingle(data, scaleOfs);
                    info.ScaleMid = BitConverter.ToSingle(data, scaleOfs + 4);
                    info.ScaleEnd = BitConverter.ToSingle(data, scaleOfs + 8);
                }

                // Read M2Track values
                info.EmissionSpeed = ReadM2TrackFirstValue(data, baseOff, REL_M2TRACK_EMISSION_SPEED);
                info.SpeedVariation = ReadM2TrackFirstValue(data, baseOff, REL_M2TRACK_SPEED_VARIATION);
                info.VerticalRange = ReadM2TrackFirstValue(data, baseOff, REL_M2TRACK_VERTICAL_RANGE);
                info.HorizontalRange = ReadM2TrackFirstValue(data, baseOff, REL_M2TRACK_HORIZONTAL_RANGE);
                info.Gravity = ReadM2TrackFirstValue(data, baseOff, REL_M2TRACK_GRAVITY);
                info.Lifespan = ReadM2TrackFirstValue(data, baseOff, REL_M2TRACK_LIFESPAN);
                info.EmissionRate = ReadM2TrackFirstValue(data, baseOff, REL_M2TRACK_EMISSION_RATE);
                info.AreaLength = ReadM2TrackFirstValue(data, baseOff, REL_M2TRACK_AREA_LENGTH);
                info.AreaWidth = ReadM2TrackFirstValue(data, baseOff, REL_M2TRACK_AREA_WIDTH);
            }

            results.Add(info);
        }
    }

    /// <summary>Read the first float value from an M2Track's value M2Array.</summary>
    private static float? ReadM2TrackFirstValue(byte[] data, int emitterBase, int m2ArrayOffset)
    {
        int arrayOff = emitterBase + m2ArrayOffset;
        if (arrayOff + 8 > data.Length) return null;

        uint count = BitConverter.ToUInt32(data, arrayOff);
        uint dataOffset = BitConverter.ToUInt32(data, arrayOff + 4);

        if (count == 0 || dataOffset == 0 || dataOffset >= data.Length) return null;
        if (dataOffset + 4 > data.Length) return null;

        float value = BitConverter.ToSingle(data, (int)dataOffset);
        if (float.IsNaN(value) || float.IsInfinity(value)) return null;
        return value;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // BINARY HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        if (offset + 4 > data.Length) return;
        byte[] bytes = BitConverter.GetBytes(value);
        Array.Copy(bytes, 0, data, offset, 4);
    }

    private static void WriteFloat(byte[] data, int offset, float value)
    {
        if (offset + 4 > data.Length) return;
        byte[] bytes = BitConverter.GetBytes(value);
        Array.Copy(bytes, 0, data, offset, 4);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HUE-SHIFT COLOR HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shift the hue of an ARGB color to match a target color's hue,
    /// preserving the original alpha, saturation, and lightness.
    /// This makes multi-emitter M2s look natural — a bright core stays bright,
    /// a dark swirl stays dark, just rotated to the new hue.
    /// </summary>
    private static uint HueShiftArgb(uint originalArgb, uint targetRgb)
    {
        byte a = (byte)((originalArgb >> 24) & 0xFF);
        byte or_ = (byte)((originalArgb >> 16) & 0xFF);
        byte og = (byte)((originalArgb >> 8) & 0xFF);
        byte ob = (byte)(originalArgb & 0xFF);

        byte tr = (byte)((targetRgb >> 16) & 0xFF);
        byte tg = (byte)((targetRgb >> 8) & 0xFF);
        byte tb = (byte)(targetRgb & 0xFF);

        RgbToHsl(or_, og, ob, out float oh, out float os, out float ol);
        RgbToHsl(tr, tg, tb, out float th, out float _ts, out float _tl);

        // Use target hue, keep original saturation and lightness
        // For very low saturation (grayscale emitters), boost sat slightly toward target
        float newS = os < 0.1f ? Math.Min(os + 0.15f, _ts) : os;
        HslToRgb(th, newS, ol, out byte nr, out byte ng, out byte nb);

        return ((uint)a << 24) | ((uint)nr << 16) | ((uint)ng << 8) | nb;
    }

    private static void RgbToHsl(byte r, byte g, byte b, out float h, out float s, out float l)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf));
        float min = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;

        l = (max + min) / 2f;

        if (delta < 0.0001f)
        {
            h = 0f;
            s = 0f;
            return;
        }

        s = l > 0.5f ? delta / (2f - max - min) : delta / (max + min);

        if (max == rf)
            h = ((gf - bf) / delta + (gf < bf ? 6f : 0f)) / 6f;
        else if (max == gf)
            h = ((bf - rf) / delta + 2f) / 6f;
        else
            h = ((rf - gf) / delta + 4f) / 6f;
    }

    private static void HslToRgb(float h, float s, float l, out byte r, out byte g, out byte b)
    {
        if (s < 0.0001f)
        {
            byte v = (byte)Math.Clamp(l * 255f + 0.5f, 0f, 255f);
            r = g = b = v;
            return;
        }

        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        r = (byte)Math.Clamp(HueToChannel(p, q, h + 1f / 3f) * 255f + 0.5f, 0f, 255f);
        g = (byte)Math.Clamp(HueToChannel(p, q, h) * 255f + 0.5f, 0f, 255f);
        b = (byte)Math.Clamp(HueToChannel(p, q, h - 1f / 3f) * 255f + 0.5f, 0f, 255f);
    }

    private static float HueToChannel(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}

/// <summary>Diagnostic info about a particle emitter.</summary>
public class ParticleEmitterInfo
{
    public int Index { get; set; }
    public int EmitterOffset { get; set; }
    public int StructSize { get; set; }
    public bool IsPrimary { get; set; }
    public int ParticleId { get; set; }
    public uint Flags { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public ushort BoneId { get; set; }
    public ushort TextureId { get; set; }
    public float Midpoint { get; set; }
    public uint ColorStart { get; set; }
    public uint ColorMid { get; set; }
    public uint ColorEnd { get; set; }
    public float ScaleStart { get; set; }
    public float ScaleMid { get; set; }
    public float ScaleEnd { get; set; }

    // M2Track values (Session 11)
    public float? EmissionSpeed { get; set; }
    public float? SpeedVariation { get; set; }
    public float? VerticalRange { get; set; }
    public float? HorizontalRange { get; set; }
    public float? Gravity { get; set; }
    public float? Lifespan { get; set; }
    public float? EmissionRate { get; set; }
    public float? AreaLength { get; set; }
    public float? AreaWidth { get; set; }
}