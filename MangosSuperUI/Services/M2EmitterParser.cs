using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Session 29: M2 Emitter Property Reader/Writer
///
/// Reads and writes animated emitter properties from vanilla v256 M2 files.
/// Each emitter is 504 bytes (0x1F8). Animated properties are stored as
/// M2Track structures at fixed offsets within each emitter.
///
/// M2Track layout (28 bytes = 0x1C):
///   +0x00: uint16 interpolationType  (0=none, 1=linear, 2=hermite)
///   +0x02: int16  globalSequenceId   (-1 = no global sequence)
///   +0x04: uint32 timestampCount
///   +0x08: uint32 timestampOffset    (absolute file offset → uint32[] timestamps)
///   +0x0C: uint32 keyframeCount
///   +0x10: uint32 keyframeOffset     (absolute file offset → float[] values)
///
/// For proof-of-concept: patches the FIRST keyframe value (index 0) of each
/// M2Track. This handles the common case of static emitter properties (1 keyframe)
/// and gives a usable approximation for animated ones (overrides the start value).
///
/// Known M2Track property offsets (relative to emitter base):
///   Block 0: +0x034 emissionSpeed        (0.17–6.5 m/s)
///   Block 1: +0x050 speedVariation       (0.07–0.72)
///   Block 2: +0x06C verticalRange        (0.017–π rad)
///   Block 3: +0x088 horizontalRange      (0–2π rad)
///   Block 4: +0x0A4 gravity              (-3.47 to 6.67)
///   Block 5: +0x0C0 lifespan             (0.25–1.0 sec)
///   Block 6: +0x0DC emissionRate         (10–74/sec)
///   Block 7: +0x0F8 emissionAreaLength   (0.006–0.83)
///   Block 8: +0x114 emissionAreaWidth    (0.006–0.83)
///
/// Note: The ValArr@ column in the architecture doc lists the value array
/// absolute file offset, which is at trackBase+0x10. The track base offsets
/// above are computed as: documented_offset - 0x14 to get to the M2Track header.
/// Actually the architecture doc lists ValArr@ as the offset to the value array
/// pointer within the M2Track, which is at trackBase + 0x10 (relative to track).
/// So the TRACK BASE for each block is ValArr@ - 0x10:
///   Block 0: 0x048 - 0x10 = 0x038  → but let's verify empirically.
///
/// IMPORTANT: The exact track base offsets need empirical verification against
/// actual M2 files. The architecture doc lists "ValArr@" which is the offset
/// of the value-array-pointer field within the emitter struct. The M2Track
/// header starts 0x10 bytes before that (since the value array pointer is at
/// +0x10 within the M2Track structure). So:
///   Track base = ValArr@ - 0x10
///   Block 0: 0x048 - 0x10 = 0x038
///   Block 5: 0x0D4 - 0x10 = 0x0C4
///   Block 6: 0x0F0 - 0x10 = 0x0E0
///   etc.
///
/// BUT — M2Track is 0x1C (28) bytes, and the blocks are contiguous starting
/// at some base. Let's use the ValArr@ offsets directly since those are verified:
/// we read the uint32 at emitter_base + ValArr@ to get the absolute file offset
/// of the float keyframe array, then read/write the first float there.
/// </summary>
public static class M2EmitterParser
{
    private const int HEADER_PRIMARY_EMITTERS = 0x13C;
    private const int PRIMARY_EMITTER_SIZE = 0x1F8;  // 504 bytes
    private const int MIN_HEADER_SIZE = 0x148;

    // ═══════════════════════════════════════════════════════════════
    // M2Track VALUE ARRAY POINTER offsets within emitter struct
    // These point to the uint32 that holds the absolute file offset
    // of the float[] keyframe values.
    //
    // Layout within M2Track (28 bytes):
    //   +0x00: uint16 interpolationType
    //   +0x02: int16  globalSeqId
    //   +0x04: uint32 timestampCount
    //   +0x08: uint32 timestampOffset
    //   +0x0C: uint32 keyframeCount      ← we read this
    //   +0x10: uint32 keyframeOffset     ← and this (ValArr@)
    //
    // So keyframeCount is at ValArr@ - 4
    //    keyframeOffset is at ValArr@
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Known emitter property tracks with their ValArr@ offsets.</summary>
    public static readonly EmitterPropertyDef[] Properties = new[]
    {
        new EmitterPropertyDef("emissionSpeed",     0x048, "Particle emission speed (m/s)"),
        new EmitterPropertyDef("speedVariation",    0x064, "Speed variation factor"),
        new EmitterPropertyDef("verticalRange",     0x080, "Vertical spread (radians)"),
        new EmitterPropertyDef("horizontalRange",   0x09C, "Horizontal spread (radians)"),
        new EmitterPropertyDef("gravity",           0x0B8, "Gravity acceleration"),
        new EmitterPropertyDef("lifespan",          0x0D4, "Particle lifespan (seconds)"),
        new EmitterPropertyDef("emissionRate",      0x0F0, "Particles per second"),
        new EmitterPropertyDef("emissionAreaLength", 0x10C, "Emission area length"),
        new EmitterPropertyDef("emissionAreaWidth",  0x128, "Emission area width"),
    };

    // Inline (non-M2Track) properties — directly in emitter struct
    private const int INLINE_BLEND_MODE = 0x028;      // uint8
    private const int INLINE_EMITTER_TYPE = 0x029;     // uint8
    private const int INLINE_TEXTURE_ID = 0x02A;       // uint16
    private const int INLINE_COLOR_START = 0x150;       // 3 × uint32 ARGB (start, mid, end)
    private const int INLINE_SCALE_START = 0x15C;       // 3 × float (start, mid, end)

    // ═══════════════════════════════════════════════════════════════
    // READ: Extract all emitter properties from an M2 file
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Read all emitter properties from an M2 file.
    /// Returns a list of EmitterSnapshot objects, one per emitter.
    /// </summary>
    public static List<EmitterSnapshot> ReadEmitters(byte[] m2Data)
    {
        var result = new List<EmitterSnapshot>();
        if (m2Data == null || m2Data.Length < MIN_HEADER_SIZE) return result;
        if (Encoding.ASCII.GetString(m2Data, 0, 4) != "MD20") return result;

        uint emitCount = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS);
        uint emitOffset = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS + 4);

        if (emitCount == 0 || emitCount > 256) return result;
        if (emitOffset == 0 || emitOffset >= m2Data.Length) return result;

        for (uint e = 0; e < emitCount; e++)
        {
            int emitterBase = (int)(emitOffset + e * PRIMARY_EMITTER_SIZE);
            if (emitterBase + PRIMARY_EMITTER_SIZE > m2Data.Length) break;

            // Validate emitter
            int particleId = BitConverter.ToInt32(m2Data, emitterBase);
            if (particleId != -1 && (particleId < 0 || particleId > 100)) continue;

            var snapshot = new EmitterSnapshot
            {
                Index = (int)e,
                EmitterBase = emitterBase,

                // Inline properties
                BlendMode = m2Data[emitterBase + INLINE_BLEND_MODE],
                EmitterType = m2Data[emitterBase + INLINE_EMITTER_TYPE],
                TextureId = BitConverter.ToUInt16(m2Data, emitterBase + INLINE_TEXTURE_ID),
            };

            // Read inline color values (3 × ARGB uint32)
            snapshot.ColorStart = BitConverter.ToUInt32(m2Data, emitterBase + INLINE_COLOR_START);
            snapshot.ColorMid = BitConverter.ToUInt32(m2Data, emitterBase + INLINE_COLOR_START + 4);
            snapshot.ColorEnd = BitConverter.ToUInt32(m2Data, emitterBase + INLINE_COLOR_START + 8);

            // Read inline scale values (3 × float)
            snapshot.ScaleStart = BitConverter.ToSingle(m2Data, emitterBase + INLINE_SCALE_START);
            snapshot.ScaleMid = BitConverter.ToSingle(m2Data, emitterBase + INLINE_SCALE_START + 4);
            snapshot.ScaleEnd = BitConverter.ToSingle(m2Data, emitterBase + INLINE_SCALE_START + 8);

            // Read M2Track properties (first keyframe value for each)
            foreach (var prop in Properties)
            {
                float? value = ReadFirstKeyframeValue(m2Data, emitterBase, prop.ValArrOffset);
                int keyframeCount = ReadKeyframeCount(m2Data, emitterBase, prop.ValArrOffset);
                snapshot.TrackValues[prop.Name] = value;
                snapshot.TrackKeyframeCounts[prop.Name] = keyframeCount;
            }

            result.Add(snapshot);
        }

        return result;
    }

    /// <summary>
    /// Read a summary of M2 emitter behavior for catalog/library purposes.
    /// Returns a lightweight description of the M2's particle character.
    /// </summary>
    public static M2BehaviorSummary? SummarizeBehavior(byte[] m2Data, string m2Path)
    {
        var emitters = ReadEmitters(m2Data);
        if (emitters.Count == 0) return null;

        var textures = M2TextureParser.ParseTextures(m2Data);

        return new M2BehaviorSummary
        {
            M2Path = m2Path,
            EmitterCount = emitters.Count,
            TextureCount = textures.Count,
            Emitters = emitters.Select(e => new EmitterBehaviorInfo
            {
                Index = e.Index,
                BlendMode = e.BlendMode,
                EmitterType = e.EmitterType,
                TextureId = e.TextureId,
                EmissionSpeed = e.TrackValues.GetValueOrDefault("emissionSpeed"),
                EmissionRate = e.TrackValues.GetValueOrDefault("emissionRate"),
                Lifespan = e.TrackValues.GetValueOrDefault("lifespan"),
                Gravity = e.TrackValues.GetValueOrDefault("gravity"),
                AreaLength = e.TrackValues.GetValueOrDefault("emissionAreaLength"),
                AreaWidth = e.TrackValues.GetValueOrDefault("emissionAreaWidth"),
                ScaleStart = e.ScaleStart,
                ScaleMid = e.ScaleMid,
                ScaleEnd = e.ScaleEnd,
            }).ToList()
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // WRITE: Patch emitter properties into an M2 byte array
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Patch a specific M2Track property on a specific emitter.
    /// Writes the value to the first keyframe slot.
    /// Returns true if the value was successfully written.
    /// </summary>
    public static bool PatchTrackValue(byte[] m2Data, int emitterIndex, string propertyName, float value)
    {
        if (m2Data == null || m2Data.Length < MIN_HEADER_SIZE) return false;

        uint emitCount = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS);
        uint emitOffset = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS + 4);

        if (emitterIndex < 0 || emitterIndex >= (int)emitCount) return false;

        int emitterBase = (int)(emitOffset + (uint)emitterIndex * PRIMARY_EMITTER_SIZE);
        if (emitterBase + PRIMARY_EMITTER_SIZE > m2Data.Length) return false;

        var prop = Array.Find(Properties, p => p.Name == propertyName);
        if (prop == null) return false;

        return WriteFirstKeyframeValue(m2Data, emitterBase, prop.ValArrOffset, value);
    }

    /// <summary>
    /// Patch inline emitter properties (blendMode, emitterType).
    /// </summary>
    public static bool PatchInlineProperty(byte[] m2Data, int emitterIndex, string property, int value)
    {
        if (m2Data == null || m2Data.Length < MIN_HEADER_SIZE) return false;

        uint emitCount = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS);
        uint emitOffset = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS + 4);

        if (emitterIndex < 0 || emitterIndex >= (int)emitCount) return false;

        int emitterBase = (int)(emitOffset + (uint)emitterIndex * PRIMARY_EMITTER_SIZE);
        if (emitterBase + PRIMARY_EMITTER_SIZE > m2Data.Length) return false;

        switch (property.ToLowerInvariant())
        {
            case "blendmode":
                m2Data[emitterBase + INLINE_BLEND_MODE] = (byte)value;
                return true;
            case "emittertype":
                m2Data[emitterBase + INLINE_EMITTER_TYPE] = (byte)value;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Patch inline scale values on an emitter (start, mid, end).
    /// </summary>
    public static bool PatchScaleValues(byte[] m2Data, int emitterIndex, float start, float mid, float end)
    {
        if (m2Data == null || m2Data.Length < MIN_HEADER_SIZE) return false;

        uint emitCount = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS);
        uint emitOffset = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS + 4);

        if (emitterIndex < 0 || emitterIndex >= (int)emitCount) return false;

        int emitterBase = (int)(emitOffset + (uint)emitterIndex * PRIMARY_EMITTER_SIZE);
        if (emitterBase + PRIMARY_EMITTER_SIZE > m2Data.Length) return false;

        int scaleOfs = emitterBase + INLINE_SCALE_START;
        if (scaleOfs + 12 > m2Data.Length) return false;

        Array.Copy(BitConverter.GetBytes(start), 0, m2Data, scaleOfs, 4);
        Array.Copy(BitConverter.GetBytes(mid), 0, m2Data, scaleOfs + 4, 4);
        Array.Copy(BitConverter.GetBytes(end), 0, m2Data, scaleOfs + 8, 4);
        return true;
    }

    /// <summary>
    /// Apply a complete EmitterPatch to an M2 byte array.
    /// Patches all specified properties on the target emitter.
    /// Returns number of properties successfully patched.
    /// </summary>
    public static int ApplyEmitterPatch(byte[] m2Data, EmitterPatch patch)
    {
        int patched = 0;

        if (patch.BlendMode.HasValue)
            if (PatchInlineProperty(m2Data, patch.EmitterIndex, "blendmode", patch.BlendMode.Value)) patched++;

        if (patch.EmitterType.HasValue)
            if (PatchInlineProperty(m2Data, patch.EmitterIndex, "emittertype", patch.EmitterType.Value)) patched++;

        if (patch.EmissionSpeed.HasValue)
            if (PatchTrackValue(m2Data, patch.EmitterIndex, "emissionSpeed", patch.EmissionSpeed.Value)) patched++;

        if (patch.SpeedVariation.HasValue)
            if (PatchTrackValue(m2Data, patch.EmitterIndex, "speedVariation", patch.SpeedVariation.Value)) patched++;

        if (patch.Lifespan.HasValue)
            if (PatchTrackValue(m2Data, patch.EmitterIndex, "lifespan", patch.Lifespan.Value)) patched++;

        if (patch.EmissionRate.HasValue)
            if (PatchTrackValue(m2Data, patch.EmitterIndex, "emissionRate", patch.EmissionRate.Value)) patched++;

        if (patch.Gravity.HasValue)
            if (PatchTrackValue(m2Data, patch.EmitterIndex, "gravity", patch.Gravity.Value)) patched++;

        if (patch.EmissionAreaLength.HasValue)
            if (PatchTrackValue(m2Data, patch.EmitterIndex, "emissionAreaLength", patch.EmissionAreaLength.Value)) patched++;

        if (patch.EmissionAreaWidth.HasValue)
            if (PatchTrackValue(m2Data, patch.EmitterIndex, "emissionAreaWidth", patch.EmissionAreaWidth.Value)) patched++;

        if (patch.VerticalRange.HasValue)
            if (PatchTrackValue(m2Data, patch.EmitterIndex, "verticalRange", patch.VerticalRange.Value)) patched++;

        if (patch.HorizontalRange.HasValue)
            if (PatchTrackValue(m2Data, patch.EmitterIndex, "horizontalRange", patch.HorizontalRange.Value)) patched++;

        if (patch.ScaleStart.HasValue && patch.ScaleMid.HasValue && patch.ScaleEnd.HasValue)
            if (PatchScaleValues(m2Data, patch.EmitterIndex,
                patch.ScaleStart.Value, patch.ScaleMid.Value, patch.ScaleEnd.Value)) patched++;

        return patched;
    }

    // ═══════════════════════════════════════════════════════════════
    // INTERNAL: M2Track keyframe read/write
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Read the first float keyframe value from an M2Track.
    /// valArrOffset = offset within emitter struct where the keyframe count lives.
    /// The keyframe array pointer is at valArrOffset + 4.
    ///
    /// Empirically verified layout (Session 29):
    ///   ValArr@ + 0x00: uint32 keyframeCount (typically 1 for static properties)
    ///   ValArr@ + 0x04: uint32 keyframeOffset (absolute file offset → float[] values)
    /// </summary>
    private static float? ReadFirstKeyframeValue(byte[] m2Data, int emitterBase, int valArrOffset)
    {
        int countOfs = emitterBase + valArrOffset;
        int ptrOfs = emitterBase + valArrOffset + 4;

        if (countOfs + 4 > m2Data.Length || ptrOfs + 4 > m2Data.Length) return null;

        uint count = BitConverter.ToUInt32(m2Data, countOfs);
        uint offset = BitConverter.ToUInt32(m2Data, ptrOfs);

        if (count == 0 || offset == 0 || offset + 4 > m2Data.Length) return null;

        return BitConverter.ToSingle(m2Data, (int)offset);
    }

    /// <summary>Read keyframe count for an M2Track.</summary>
    private static int ReadKeyframeCount(byte[] m2Data, int emitterBase, int valArrOffset)
    {
        int countOfs = emitterBase + valArrOffset;
        if (countOfs + 4 > m2Data.Length) return 0;
        return (int)BitConverter.ToUInt32(m2Data, countOfs);
    }

    /// <summary>
    /// Write a float value to the first keyframe slot of an M2Track.
    /// Does NOT add keyframes — only overwrites existing ones.
    /// Returns false if the track has no keyframes.
    /// </summary>
    private static bool WriteFirstKeyframeValue(byte[] m2Data, int emitterBase, int valArrOffset, float value)
    {
        int countOfs = emitterBase + valArrOffset;
        int ptrOfs = emitterBase + valArrOffset + 4;

        if (countOfs + 4 > m2Data.Length || ptrOfs + 4 > m2Data.Length) return false;

        uint count = BitConverter.ToUInt32(m2Data, countOfs);
        uint offset = BitConverter.ToUInt32(m2Data, ptrOfs);

        if (count == 0 || offset == 0 || offset + 4 > m2Data.Length) return false;

        Array.Copy(BitConverter.GetBytes(value), 0, m2Data, (int)offset, 4);
        return true;
    }
}

// ═══════════════════════════════════════════════════════════════
// DTOs
// ═══════════════════════════════════════════════════════════════

/// <summary>Definition of an M2Track emitter property.</summary>
public class EmitterPropertyDef
{
    public string Name { get; }
    public int ValArrOffset { get; }
    public string Description { get; }

    public EmitterPropertyDef(string name, int valArrOffset, string description)
    {
        Name = name;
        ValArrOffset = valArrOffset;
        Description = description;
    }
}

/// <summary>Complete snapshot of a single emitter's current state.</summary>
public class EmitterSnapshot
{
    public int Index { get; set; }
    public int EmitterBase { get; set; }

    // Inline properties
    public byte BlendMode { get; set; }
    public byte EmitterType { get; set; }
    public ushort TextureId { get; set; }
    public uint ColorStart { get; set; }
    public uint ColorMid { get; set; }
    public uint ColorEnd { get; set; }
    public float ScaleStart { get; set; }
    public float ScaleMid { get; set; }
    public float ScaleEnd { get; set; }

    // M2Track first-keyframe values (null = track has no keyframes)
    public Dictionary<string, float?> TrackValues { get; set; } = new();

    // Keyframe counts per track (for UI: 1 = static, >1 = animated)
    public Dictionary<string, int> TrackKeyframeCounts { get; set; } = new();
}

/// <summary>Patch instructions for a single emitter.</summary>
public class EmitterPatch
{
    public int EmitterIndex { get; set; }

    // Inline properties
    public int? BlendMode { get; set; }
    public int? EmitterType { get; set; }

    // M2Track properties — absolute values, NOT multipliers
    public float? EmissionSpeed { get; set; }
    public float? SpeedVariation { get; set; }
    public float? VerticalRange { get; set; }
    public float? HorizontalRange { get; set; }
    public float? Gravity { get; set; }
    public float? Lifespan { get; set; }
    public float? EmissionRate { get; set; }
    public float? EmissionAreaLength { get; set; }
    public float? EmissionAreaWidth { get; set; }

    // Scale values
    public float? ScaleStart { get; set; }
    public float? ScaleMid { get; set; }
    public float? ScaleEnd { get; set; }
}

/// <summary>Lightweight behavior summary of an M2 file for catalog purposes.</summary>
public class M2BehaviorSummary
{
    public string M2Path { get; set; } = "";
    public int EmitterCount { get; set; }
    public int TextureCount { get; set; }
    public List<EmitterBehaviorInfo> Emitters { get; set; } = new();
}

public class EmitterBehaviorInfo
{
    public int Index { get; set; }
    public byte BlendMode { get; set; }
    public byte EmitterType { get; set; }
    public ushort TextureId { get; set; }
    public float? EmissionSpeed { get; set; }
    public float? EmissionRate { get; set; }
    public float? Lifespan { get; set; }
    public float? Gravity { get; set; }
    public float? AreaLength { get; set; }
    public float? AreaWidth { get; set; }
    public float ScaleStart { get; set; }
    public float ScaleMid { get; set; }
    public float ScaleEnd { get; set; }
}

// ═══════════════════════════════════════════════════════════════
// SPELL TUNING JSON — The "JSON Drop" document
//
// This is the complete visual definition of a custom spell.
// Import it through PatchController.ApplySpellTuning to:
//   1. Reprocess textures from existing PNGs (no ComfyUI)
//   2. Patch emitter properties into M2 files
//   3. Rebuild the unified patch
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Complete visual tuning document for a custom spell.
/// Defines textures and emitter behavior per phase.
/// </summary>
public class SpellTuningPreset
{
    public string PresetName { get; set; } = "";
    public int SourceSpellEntry { get; set; }
    public string? Theme { get; set; }

    /// <summary>Per-phase visual configuration.</summary>
    public Dictionary<string, PhaseTuning> Phases { get; set; } = new();
}

/// <summary>Visual configuration for a single spell phase.</summary>
public class PhaseTuning
{
    /// <summary>M2 file to use for this phase.
    /// Can reference vanilla M2s: "Spells/LightningBolt_Missile.m2" for lightning behavior
    /// instead of "Spells/Fireball_Missile_Low.m2" for fire behavior.</summary>
    public string? SourceM2 { get; set; }

    /// <summary>Texture slot definitions — maps existing PNGs to emitter texture slots.</summary>
    public List<TextureTuning>? Textures { get; set; }

    /// <summary>Emitter property patches — absolute values for each emitter.</summary>
    public List<EmitterPatch>? Emitters { get; set; }
}

/// <summary>Texture assignment for a single slot in a phase.</summary>
public class TextureTuning
{
    /// <summary>Texture slot index in the M2's texture table.</summary>
    public int SlotIndex { get; set; }

    /// <summary>Path to source PNG relative to comfyui_output/ or custom/{SpellName}/.
    /// If this starts with "comfyui_output/", looks in the comfyui output directory.
    /// Otherwise looks in the spell's custom texture directory.</summary>
    public string SourcePng { get; set; } = "";

    /// <summary>Texture role: "Body", "Glow", "Shape", "Ring", "Ribbon", "Atlas", "Bloom".</summary>
    public string Role { get; set; } = "Shape";

    /// <summary>Texture density: "FullCoverage" or "CenteredShape".</summary>
    public string? Density { get; set; }

    /// <summary>Brightness floor override for this slot (0.0-1.0). Null = per-role default.</summary>
    public float? FloorPercent { get; set; }

    /// <summary>Knee width override for this slot (0.0-1.0). Null = 0.08 default.</summary>
    public float? KneeWidth { get; set; }
}