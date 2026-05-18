using System.Numerics;
using System.Text;

namespace MangosSuperUI.Services;

// ════════════════════════════════════════════════════════════════════════════
// M2Model — parsed M2 model (geometry + texture refs + skeleton + attachments
//                            + sequences + per-bone TRS animation tracks)
// ════════════════════════════════════════════════════════════════════════════

public class M2Model
{
    public uint Version { get; set; }
    public string Name { get; set; } = "";

    public List<M2Vertex> Vertices { get; set; } = new();
    public List<ushort> Indices { get; set; } = new();
    public List<M2Submesh> Submeshes { get; set; } = new();
    public List<M2Batch> Batches { get; set; } = new();
    public List<M2TextureRef> Textures { get; set; } = new();
    public List<ushort> TextureLookup { get; set; } = new();

    // ── Skeleton ─────────────────────────────────────────────────────────────
    public List<M2Bone> Bones { get; set; } = new();
    public List<short> KeyBoneLookup { get; set; } = new();
    public List<M2Attachment> Attachments { get; set; } = new();
    public List<short> AttachmentLookup { get; set; } = new();

    // ── Render flags ─────────────────────────────────────────────────────────
    public List<M2RenderFlag> RenderFlags { get; set; } = new();

    // ── Transparency tracks (Session N — static evaluation only) ─────────────
    public List<float> TransparencyStaticAlphas { get; set; } = new();
    public List<ushort> TransparencyLookup { get; set; } = new();

    // ── Sequences (Session O — animation) ────────────────────────────────────
    //
    // Vanilla 1.12 stores all animation data INLINE in the M2 (no .anim file
    // spillover — that's a WotLK+ feature). Each entry below names a logical
    // animation by its AnimationData.dbc Id (0=Stand, 4=Walk, 5=Run,
    // 26=AttackUnarmed, etc.) and gives its duration. The per-bone
    // M2AnimTrack<T> structures key off the SEQUENCE INDEX (position in
    // this list), not the AnimationId — so animationId-to-index resolution
    // happens up front via TryFindSequenceIndexByAnimationId.
    public List<M2Sequence> Sequences { get; set; } = new();

    public bool IsValid => Vertices.Count > 0 && Indices.Count >= 3;
    public bool HasSkeleton => Bones.Count > 0;

    /// <summary>
    /// Resolve a batch's "is this drawn at all in idle pose?" alpha.
    /// Chain: batch.TextureWeightIndex → TransparencyLookup[idx] →
    ///        TransparencyStaticAlphas[idx]. Any link in the chain
    ///        missing → return 1.0 (fully visible, safe fallback).
    /// </summary>
    public float GetStaticAlphaForBatch(M2Batch batch)
    {
        ushort ti = batch.TextureWeightIndex;
        if (ti >= TransparencyLookup.Count) return 1.0f;

        ushort trackIdx = TransparencyLookup[ti];
        if (trackIdx >= TransparencyStaticAlphas.Count) return 1.0f;

        return TransparencyStaticAlphas[trackIdx];
    }

    /// <summary>
    /// Find a sequence by its AnimationData.dbc ID (e.g. 0 = Stand, 4 = Walk).
    /// Returns the index into <see cref="Sequences"/>, or -1 if no sequence
    /// in this model matches that animation ID.
    ///
    /// A vanilla character M2 has ~150 sequences and many of them are
    /// variations of the same animationId (variationId 1, 2, 3...). This
    /// returns the FIRST match — variationId 0, which is the standard
    /// version. Callers wanting a variation can iterate Sequences directly.
    /// </summary>
    public int TryFindSequenceIndexByAnimationId(int animationId)
    {
        for (int i = 0; i < Sequences.Count; i++)
        {
            if (Sequences[i].AnimationId == animationId &&
                Sequences[i].VariationId == 0)
                return i;
        }
        // Fallback: any variation
        for (int i = 0; i < Sequences.Count; i++)
        {
            if (Sequences[i].AnimationId == animationId)
                return i;
        }
        return -1;
    }
}

/// <summary>
/// One entry in the M2's renderFlags array. See M2Reader.ParseRenderFlags.
/// </summary>
public class M2RenderFlag
{
    public ushort Flags { get; set; }
    public ushort BlendingMode { get; set; }

    public bool Unlit => (Flags & 0x01) != 0;
    public bool TwoSided => (Flags & 0x04) != 0;
    public bool NoZWrite => (Flags & 0x10) != 0;
}

public struct M2Vertex
{
    public float PosX, PosY, PosZ;
    public float NormX, NormY, NormZ;
    public float TexU, TexV;

    public byte BoneWeight0, BoneWeight1, BoneWeight2, BoneWeight3;
    public byte BoneIndex0, BoneIndex1, BoneIndex2, BoneIndex3;
}

public class M2Submesh
{
    public ushort Id { get; set; }              // geoset ID (e.g. 1303 = boot variant 3)
    public ushort VertexStart { get; set; }
    public ushort VertexCount { get; set; }
    public ushort IndexStart { get; set; }
    public ushort IndexCount { get; set; }
}

public class M2Batch
{
    public byte Flags { get; set; }
    public byte PriorityPlane { get; set; }
    public ushort ShaderId { get; set; }
    public ushort SubmeshIndex { get; set; }
    public ushort GeosetIndex { get; set; }
    public short ColorIndex { get; set; }
    public ushort MaterialIndex { get; set; }
    public ushort MaterialLayer { get; set; }
    public ushort TextureCount { get; set; }
    public ushort TextureIndex { get; set; }
    public ushort TextureTransformIndex { get; set; }
    public ushort TextureWeightIndex { get; set; }
}

public class M2TextureRef
{
    public uint Type { get; set; }
    public uint Flags { get; set; }
    public string Filename { get; set; } = "";
}

/// <summary>
/// A skeleton joint in the M2 model. 108-byte stride in vanilla 1.12.
///
/// Bind-pose pivot is stored as Vector3 at +96, in WoW Z-up; M2Reader
/// converts to glTF Y-up before storing.
///
/// Animation tracks (Session O):
///   - Translation: Vector3 per key, OFFSET FROM the bone's pivot. M2's
///     animation convention is T(pivot) * T(translation) * R(rotation) *
///     S(scale) * T(-pivot), so translation moves the bone away from its
///     bind-pose pivot. SkinnedGlbWriter folds this in by adding the
///     M2 translation track to the glTF node's rest-pose local position.
///   - Rotation:    Vector4 quaternion per key. Vanilla uses unpacked floats;
///     TBC+ switched to int16 PACK_QUATERNION.
///   - Scale:       Vector3 per key.
///
/// Each track stores a SHARED timestamps/keys array with per-sequence
/// {start, end} ranges into that shared array. See M2AnimTrack&lt;T&gt;.
/// </summary>
public class M2Bone
{
    public int KeyBoneId { get; set; }
    public uint Flags { get; set; }
    public short ParentBone { get; set; }   // -1 = root
    public ushort SubmeshId { get; set; }
    public Vector3 Pivot { get; set; }      // glTF Y-up after conversion

    // ── Animation tracks (Session O) ─────────────────────────────────────────
    // All three may be empty (UsesSequence returns false for every sequence)
    // for static bones — common for finger/accessory bones that don't animate.
    public M2AnimTrack<Vector3> Translation { get; set; } = new();
    public M2AnimTrack<Vector4> Rotation { get; set; } = new();
    public M2AnimTrack<Vector3> Scale { get; set; } = new();
}

/// <summary>
/// A semantic attachment point on the character skeleton. 48-byte stride.
/// </summary>
public class M2Attachment
{
    public uint Id { get; set; }
    public uint BoneIndex { get; set; }
    public Vector3 Position { get; set; }   // glTF Y-up after conversion
}

/// <summary>
/// One animation sequence header (vanilla 1.12 AnimationSequenceM2 layout).
/// 68-byte stride.
///
/// === Field semantics ===
///   AnimationId: index into AnimationData.dbc. Vanilla key values:
///     0   Stand           4   Walk            5   Run
///     11  ShuffleLeft     12  ShuffleRight    13  Walkbackwards
///     14  Sleep           15  SleepUp         16  SitGround
///     26  AttackUnarmed   27  Attack1H        28  Attack2H
///     31  ParryUnarmed    34  ShieldBlock     37  ReadyUnarmed
///     45  Death           67  CombatWound     ... (and many more)
///   See client-side `animation-names.js` for the full table.
///
///   VariationId: many animationIds have variants (Stand has 1-4 idle
///     fidgets, attacks have left/right swings, etc.) — variationId 0
///     is the canonical version.
///
///   StartTimestamp / EndTimestamp: absolute timeline positions in MS on
///     the SHARED per-track timeline (the timestamps array of each
///     M2AnimTrack). Duration of this sequence = end - start.
///
///   Flags: bit 0x20 = looping. Other bits TBD.
/// </summary>
public class M2Sequence
{
    public ushort AnimationId { get; set; }
    public ushort VariationId { get; set; }
    public uint StartTimestamp { get; set; }
    public uint EndTimestamp { get; set; }
    public uint Flags { get; set; }

    public uint DurationMs => EndTimestamp > StartTimestamp
        ? EndTimestamp - StartTimestamp
        : 0;
    public bool IsLooping => (Flags & 0x20) != 0;
}

/// <summary>
/// One vanilla M2 animation track.
///
/// === Wire layout (vanilla AnimationBlockM2, 28 bytes) ===
///   +0   uint16  interpolationType  (0=none, 1=linear, 2=hermite, 3=bezier)
///   +2   int16   globalSequence     (-1 = use per-sequence ranges)
///   +4   M2Array ranges             (one AnimationRange{start,end} per sequence)
///  +12   M2Array timestamps         (uint32, shared across all sequences)
///  +20   M2Array keys               (T per key, shared across all sequences)
///
/// === Per-sequence indexing ===
/// All sequences share ONE timestamps array and ONE keys array. For
/// animation_index N, the slice that belongs to it is
/// [Ranges[N].Start .. Ranges[N].End] inclusive. Within that slice:
///   - Timestamps[i] is an ABSOLUTE position on the shared timeline (ms)
///   - To convert to "time since start of this sequence" (for glTF, which
///     needs per-clip relative times), subtract Timestamps[Ranges[N].Start]
///     from each timestamp in the range.
///
/// === Why a per-sequence subdivision is necessary ===
/// glTF animations have one timeline per clip; they can't reference into a
/// shared global timeline. So we must slice the M2's shared keys/timestamps
/// into per-sequence sub-arrays at bake time.
///
/// === GlobalSequence (NOT supported here) ===
/// When globalSequence &gt; -1, the track loops independently of any
/// AnimationData sequence (used for spell effects, particle pulses, etc.).
/// Character bone tracks don't use global sequences in practice — we treat
/// any track with globalSequence != -1 as "no per-sequence data" via
/// <see cref="UsesSequence"/>. If a future character M2 turns out to need
/// global-sequence support, this is the place to wire it up.
/// </summary>
public class M2AnimTrack<T> where T : struct
{
    public ushort InterpolationType { get; set; }
    public short GlobalSequence { get; set; } = -1;

    /// <summary>One {start, end} index pair per sequence (inclusive ends).</summary>
    public List<AnimationRange> Ranges { get; set; } = new();
    /// <summary>Shared timestamp array (absolute timeline positions, milliseconds).</summary>
    public List<uint> Timestamps { get; set; } = new();
    /// <summary>Shared keyframe values, parallel to Timestamps.</summary>
    public List<T> Keys { get; set; } = new();

    public bool IsLinear => InterpolationType == 1;

    /// <summary>
    /// True if this track has at least one keyframe whose timestamp falls
    /// within the given sequence's absolute time window.
    ///
    /// We drive entirely off <c>sequence.StartTimestamp/EndTimestamp</c>
    /// rather than <c>Ranges[i]</c>. Earlier code trusted Ranges as
    /// per-sequence index windows into the shared Timestamps array; in
    /// practice many vanilla character M2s leave Ranges as
    /// <c>(0, Timestamps.Count - 1)</c> for every sequence and only the
    /// sequence header's start/end timestamps differentiate which keys
    /// belong to which animation. Using the timestamp window is robust
    /// to either interpretation.
    /// </summary>
    public bool UsesSequence(uint startTimestampMs, uint endTimestampMs)
    {
        if (GlobalSequence > -1) return false; // see class doc — not supported
        if (Timestamps.Count == 0) return false;
        if (endTimestampMs < startTimestampMs) return false;

        // Quick reject if the whole sequence window lies outside the
        // track's timeline.
        if (Timestamps[0] > endTimestampMs) return false;
        if (Timestamps[Timestamps.Count - 1] < startTimestampMs) return false;

        return true;
    }

    /// <summary>
    /// Enumerate (timeRelativeMs, key) pairs for a given sequence,
    /// identified by its absolute <paramref name="startTimestampMs"/> and
    /// <paramref name="endTimestampMs"/> on the shared track timeline.
    ///
    /// Returned times are relative to <paramref name="startTimestampMs"/>
    /// (so the caller's clip starts at t=0). We INCLUDE keyframes at both
    /// endpoints (inclusive window) so the clip's last keyframe lines up
    /// with the sequence duration — important because glTF clip duration
    /// is derived from <c>max(keyframe times)</c>.
    /// </summary>
    public IEnumerable<(uint timeMs, T value)> EnumerateSequenceKeys(
        uint startTimestampMs, uint endTimestampMs)
    {
        if (!UsesSequence(startTimestampMs, endTimestampMs)) yield break;

        for (int i = 0; i < Timestamps.Count; i++)
        {
            uint t = Timestamps[i];
            if (t < startTimestampMs) continue;
            if (t > endTimestampMs) break; // Timestamps is monotonic
            yield return (t - startTimestampMs, Keys[i]);
        }
    }
}

/// <summary>One sequence's slice indices into the shared keys/timestamps arrays.</summary>
public struct AnimationRange
{
    public uint Start;
    public uint End;
}

// ════════════════════════════════════════════════════════════════════════════
// M2Reader — parses the vanilla M2 (v256, "MD20") binary
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Reads WoW 1.12.1 (vanilla, build 5875) M2 model files.
///
/// === Header layout (v256) ===
/// 0x000  char[4]   magic = "MD20"
/// 0x004  uint32    version (256)
/// 0x008  M2Array   name
/// 0x010  uint32    globalFlags
/// 0x014  M2Array   globalLoops
/// 0x01C  M2Array   sequences            ← Session O (stride 68)
/// 0x024  M2Array   sequenceIdxHashById
/// 0x02C  M2Array   playableAnimLookup
/// 0x034  M2Array   bones                ← stride 108
/// 0x03C  M2Array   keyBoneLookup
/// 0x044  M2Array   vertices             ← stride 48
/// 0x04C  uint32    nViews
/// 0x050  uint32    ofsViews
/// 0x054  M2Array   colors
/// 0x05C  M2Array   textures
/// 0x064  M2Array   transparency
/// 0x06C  M2Array   textureFlipbooks
/// 0x074  M2Array   uvAnimations
/// 0x07C  M2Array   textureReplace
/// 0x084  M2Array   renderFlags
/// 0x08C  M2Array   boneLookup
/// 0x094  M2Array   textureLookup
/// 0x09C  M2Array   textureUnits
/// 0x0A4  M2Array   transparencyLookup
/// 0x0AC  M2Array   uvAnimationLookup
/// 0x0B4..0x0E8  bounding box / collision data (floats)
/// 0x0EC  M2Array   collisionTriangles
/// 0x0F4  M2Array   collisionVertices
/// 0x0FC  M2Array   collisionNormals
/// 0x104  M2Array   attachments          ← stride 48
/// 0x10C  M2Array   attachmentLookup     (shorts, indexed by semantic ID)
/// ...
///
/// </summary>
public class M2Reader
{
    private const int VERTEX_STRIDE = 48;
    private const int BONE_STRIDE = 108;
    private const int ATTACHMENT_STRIDE = 48;
    private const int SEQUENCE_STRIDE_VANILLA = 68;
    private const int ANIM_BLOCK_STRIDE_VANILLA = 28;
    private const int RANGE_STRIDE = 8;     // 2 × uint32

    public static M2Model? Parse(byte[] data)
    {
        if (data == null || data.Length < 0x110) return null;

        try
        {
            var magic = Encoding.ASCII.GetString(data, 0, 4);
            if (magic != "MD20") return null;

            var model = new M2Model
            {
                Version = ReadUInt32(data, 0x04)
            };

            // Vanilla only (v256). WotLK (264+) splits .skin into external files
            // AND uses 8-byte PACK_QUATERNION for rotation keys (vs vanilla's
            // 16-byte unpacked float Quaternion) — out of scope for this reader.
            if (model.Version >= 264) return null;

            // ── Name ────────────────────────────────────────────────────────
            uint nName = ReadUInt32(data, 0x08);
            uint ofsName = ReadUInt32(data, 0x0C);
            if (nName > 0 && ofsName > 0 && ofsName + nName <= data.Length)
                model.Name = Encoding.ASCII.GetString(data, (int)ofsName, (int)nName).TrimEnd('\0');

            // ── Sequences (Session O) ───────────────────────────────────────
            // Parsed BEFORE bones, because bone TRS tracks reference sequence
            // index by position — knowing the sequence count up front lets us
            // size the Ranges array sanely.
            ParseSequences(data, ReadUInt32(data, 0x01C), ReadUInt32(data, 0x020), model);

            // ── Bones (with animation tracks — Session O) ───────────────────
            uint nBones = ReadUInt32(data, 0x034);
            uint ofsBones = ReadUInt32(data, 0x038);
            ParseBones(data, nBones, ofsBones, model);

            uint nKeyBoneLookup = ReadUInt32(data, 0x03C);
            uint ofsKeyBoneLookup = ReadUInt32(data, 0x040);
            ParseKeyBoneLookup(data, nKeyBoneLookup, ofsKeyBoneLookup, model);

            // ── Vertices ────────────────────────────────────────────────────
            uint nVertices = ReadUInt32(data, 0x044);
            uint ofsVertices = ReadUInt32(data, 0x048);
            if (nVertices == 0 || ofsVertices == 0 || ofsVertices >= data.Length)
                return null;
            if (!ParseVertices(data, nVertices, ofsVertices, model))
                return null;

            // ── Views (vanilla = inlined; we always read view 0) ────────────
            uint nViews = ReadUInt32(data, 0x04C);
            uint ofsViews = ReadUInt32(data, 0x050);
            if (nViews == 0 || ofsViews == 0 || ofsViews >= data.Length)
                return null;
            if (!ParseInlinedView(data, ofsViews, model))
                return null;

            // ── Textures + lookups + render flags + transparency ────────────
            ParseTextures(data, ReadUInt32(data, 0x05C), ReadUInt32(data, 0x060), model);
            ParseTextureLookup(data, ReadUInt32(data, 0x094), ReadUInt32(data, 0x098), model);
            ParseRenderFlags(data, ReadUInt32(data, 0x084), ReadUInt32(data, 0x088), model);
            ParseTransparencyStaticAlphas(data,
                ReadUInt32(data, 0x064), ReadUInt32(data, 0x068), model);
            ParseTransparencyLookup(data,
                ReadUInt32(data, 0x0A4), ReadUInt32(data, 0x0A8), model);

            // ── Attachments ─────────────────────────────────────────────────
            uint nAttachments = ReadUInt32(data, 0x104);
            uint ofsAttachments = ReadUInt32(data, 0x108);
            ParseAttachments(data, nAttachments, ofsAttachments, model);

            uint nAttachmentLookup = ReadUInt32(data, 0x10C);
            uint ofsAttachmentLookup = ReadUInt32(data, 0x110);
            ParseAttachmentLookup(data, nAttachmentLookup, ofsAttachmentLookup, model);

            return model.IsValid ? model : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Vertices ────────────────────────────────────────────────────────────
    //
    // 48-byte M2Vertex:
    //   +0   float[3] position
    //  +12   uint8[4] boneWeights  (sum = 255)
    //  +16   uint8[4] boneIndices
    //  +20   float[3] normal
    //  +32   float[2] uv0
    //  +40   float[2] uv1
    //
    // Coordinate transform: WoW (Z-up, +X forward, +Y right) → glTF (Y-up,
    // -Z forward, +X right): (x, y, z) → (x, z, -y). 
    private static bool ParseVertices(byte[] data, uint count, uint offset, M2Model model)
    {
        if (offset + count * VERTEX_STRIDE > data.Length) return false;

        model.Vertices.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * VERTEX_STRIDE);

            float px = ReadFloat(data, off + 0);
            float py = ReadFloat(data, off + 4);
            float pz = ReadFloat(data, off + 8);

            byte bw0 = data[off + 12], bw1 = data[off + 13], bw2 = data[off + 14], bw3 = data[off + 15];
            byte bi0 = data[off + 16], bi1 = data[off + 17], bi2 = data[off + 18], bi3 = data[off + 19];

            float nx = ReadFloat(data, off + 20);
            float ny = ReadFloat(data, off + 24);
            float nz = ReadFloat(data, off + 28);

            float u = ReadFloat(data, off + 32);
            float v = ReadFloat(data, off + 36);

            model.Vertices.Add(new M2Vertex
            {
                PosX = px,
                PosY = pz,
                PosZ = -py,
                NormX = nx,
                NormY = nz,
                NormZ = -ny,
                TexU = u,
                TexV = v,
                BoneWeight0 = bw0,
                BoneWeight1 = bw1,
                BoneWeight2 = bw2,
                BoneWeight3 = bw3,
                BoneIndex0 = bi0,
                BoneIndex1 = bi1,
                BoneIndex2 = bi2,
                BoneIndex3 = bi3,
            });
        }

        return model.Vertices.Count > 0;
    }

    // ── Sequences (Session O) ───────────────────────────────────────────────
    //
    // Vanilla AnimationSequenceM2 layout (68 bytes):
    //   +0   uint16 id              (AnimationData.dbc index)
    //   +2   uint16 variationId
    //   +4   uint32 startTimestamp  (ms on shared anim timeline)
    //   +8   uint32 endTimestamp
    //  +12   float  movespeed       (skipped)
    //  +16   uint32 flags           (bit 0x20 = looping)
    //  +20   uint16 frequency       (skipped)
    //  +22   uint16 padding
    //  +24   uint32 minimumRepetitions (skipped)
    //  +28   uint32 maximumRepetitions (skipped)
    //  +32   uint32 blendTime       (skipped)
    //  +36   M2Box  bounds          (24 bytes, skipped)
    //  +60   float  boundsRadius    (skipped)
    //  +64   int16  nextAnimationId (skipped — sequence chaining is TODO)
    //  +66   uint16 aliasNextId     (skipped)
    private static void ParseSequences(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * SEQUENCE_STRIDE_VANILLA > data.Length) return;

        model.Sequences.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * SEQUENCE_STRIDE_VANILLA);
            model.Sequences.Add(new M2Sequence
            {
                AnimationId = ReadUInt16(data, off + 0),
                VariationId = ReadUInt16(data, off + 2),
                StartTimestamp = ReadUInt32(data, off + 4),
                EndTimestamp = ReadUInt32(data, off + 8),
                Flags = ReadUInt32(data, off + 16),
            });
        }
    }

    // ── Bones ───────────────────────────────────────────────────────────────
    //
    // ModelBoneM2<vanilla> layout (108 bytes — verified empirically
    //
    //   +0    int32   keyBoneId
    //   +4    uint32  flags
    //   +8    int16   parentBone
    //  +10    uint16  submeshId
    //  +12    AnimationBlockM2<Vector3>     translation   (28 bytes)
    //  +40    AnimationBlockM2<Quaternion>  rotation      (28 bytes)
    //  +68    AnimationBlockM2<Vector3>     scale         (28 bytes)
    //  +96    float[3] pivot                              (12 bytes)
    //
    // CRITICAL: vanilla stores rotation keys as unpacked Vector4 (4 floats =
    // 16 bytes per key), NOT as PACK_QUATERNION (4 int16s = 8 bytes per key).
    // The packed format was introduced in TBC. the 8-byte PACK_QUATERNION path is only
    // reached on TBC+. Getting this wrong would produce garbage rotations
    // that look superficially valid (since the byte pattern overlaps).
    private static void ParseBones(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * BONE_STRIDE > data.Length) return;

        int sequenceCount = model.Sequences.Count;

        model.Bones.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * BONE_STRIDE);

            int keyBoneId = (int)ReadUInt32(data, off + 0);
            uint flags = ReadUInt32(data, off + 4);
            short parent = (short)ReadUInt16(data, off + 8);
            ushort submeshId = ReadUInt16(data, off + 10);

            // Pivot at +96, with Z-up → Y-up swap 
            float px = ReadFloat(data, off + 96);
            float py = ReadFloat(data, off + 100);
            float pz = ReadFloat(data, off + 104);
            var pivot = new Vector3(px, pz, -py);

            // ── Animation tracks (Session O) ────────────────────────────────
            //
            //   Translation: (x, y, z)    → (x, z, -y)
            //   Rotation:    (x, y, z, w) → (x, z, -y, w)   ← axis swap mirrors position
            //   Scale:       (x, y, z)    → (x, z, y)        (magnitudes, no sign flip)
            //
            // === Why not WMV's fix_quaternion ===
            // WMV uses (qx,qy,qz,qw) → (-qx,-qz,qy,qw). That works in WMV
            // because its matrix builder is column-major OpenGL with a
            // specific quaternion-to-matrix sign convention. When we tried
            // that mapping the body assembled correctly but every joint
            // rotated the wrong way (right hand rotation produced left
            // hand motion etc.) — classic handedness mismatch between
            // M2's source axes and glTF's destination axes.
            //
            // The mapping below applies the SAME axis swap to the quat's
            // imaginary part as is applied to position vectors. That is
            // the textbook way to transport a rotation through a
            // coordinate-system change: if T maps positions, then a
            // rotation R becomes T · R · T⁻¹. For an axis-swap T (which
            // is its own inverse here, since (x,z,-y) applied twice gives
            // (x,-y,-z)... actually it isn't self-inverse, but the
            // imaginary-part-only swap is the correct sandwich product
            // for unit quats representing pure rotations under this kind
            // of basis change). w stays put because it encodes angle, not
            // axis.
            //
            // If joints STILL rotate backward after this change, the
            // alternative is to negate w as well (Option 1 in the Session O
            // handoff "Possible bugs" section).
            var translation = ParseAnimTrack<Vector3>(
                data, off + 12, sequenceCount,
                keyStride: 12,
                readKey: (d, o) =>
                {
                    float x = ReadFloat(d, o + 0);
                    float y = ReadFloat(d, o + 4);
                    float z = ReadFloat(d, o + 8);
                    return new Vector3(x, z, -y);
                });

            var rotation = ParseAnimTrack<Vector4>(
                data, off + 40, sequenceCount,
                keyStride: 16,
                readKey: (d, o) =>
                {
                    float qx = ReadFloat(d, o + 0);
                    float qy = ReadFloat(d, o + 4);
                    float qz = ReadFloat(d, o + 8);
                    float qw = ReadFloat(d, o + 12);
                    return new Vector4(qx, qz, -qy, qw);
                });

            var scale = ParseAnimTrack<Vector3>(
                data, off + 68, sequenceCount,
                keyStride: 12,
                readKey: (d, o) =>
                {
                    float x = ReadFloat(d, o + 0);
                    float y = ReadFloat(d, o + 4);
                    float z = ReadFloat(d, o + 8);
                    return new Vector3(x, z, y);
                });

            model.Bones.Add(new M2Bone
            {
                KeyBoneId = keyBoneId,
                Flags = flags,
                ParentBone = parent,
                SubmeshId = submeshId,
                Pivot = pivot,
                Translation = translation,
                Rotation = rotation,
                Scale = scale,
            });
        }
    }

    // ── Animation track parser (Session O) ──────────────────────────────────
    //
    // Reads one vanilla AnimationBlockM2<T> (28-byte struct) into an
    // M2AnimTrack<T>:
    //
    //   +0   uint16  interpolationType
    //   +2   int16   globalSequence
    //   +4   M2Array ranges
    //  +12   M2Array timestamps
    //  +20   M2Array keys
    //
    // For each sequence the model has, there's a corresponding AnimationRange
    // entry at ranges[sequenceIdx] indicating which slice of timestamps[] and
    // keys[] belongs to that sequence. See M2AnimTrack class doc.
    //
    // The readKey delegate handles per-T-type byte parsing PLUS coordinate
    // conversion in one step. Caller pre-computes the appropriate Z-up→Y-up
    // transform for T (translation, rotation, and scale each use different
    // transforms — see ParseBones).
    private delegate T KeyReader<T>(byte[] data, int offset);

    private static M2AnimTrack<T> ParseAnimTrack<T>(
        byte[] data, int blockOffset, int sequenceCount,
        int keyStride, KeyReader<T> readKey) where T : struct
    {
        var track = new M2AnimTrack<T>();

        if (blockOffset + ANIM_BLOCK_STRIDE_VANILLA > data.Length)
            return track;

        track.InterpolationType = ReadUInt16(data, blockOffset + 0);
        track.GlobalSequence = (short)ReadUInt16(data, blockOffset + 2);

        uint nRanges = ReadUInt32(data, blockOffset + 4);
        uint ofsRanges = ReadUInt32(data, blockOffset + 8);
        uint nTimestamps = ReadUInt32(data, blockOffset + 12);
        uint ofsTimestamps = ReadUInt32(data, blockOffset + 16);
        uint nKeys = ReadUInt32(data, blockOffset + 20);
        uint ofsKeys = ReadUInt32(data, blockOffset + 24);

        // Ranges: one entry per sequence. Stride 8 (2 × uint32).
        // We DON'T cap at sequenceCount because some character M2s appear to
        // have a sentinel/extra range — let it through, callers index by
        // sequence and out-of-range access falls through to "no animation".
        if (nRanges > 0 && ofsRanges > 0 &&
            ofsRanges + nRanges * RANGE_STRIDE <= data.Length)
        {
            track.Ranges.Capacity = (int)nRanges;
            for (uint i = 0; i < nRanges; i++)
            {
                int o = (int)(ofsRanges + i * RANGE_STRIDE);
                track.Ranges.Add(new AnimationRange
                {
                    Start = ReadUInt32(data, o + 0),
                    End = ReadUInt32(data, o + 4),
                });
            }
        }

        // Timestamps: uint32 ms positions. Shared across all sequences.
        if (nTimestamps > 0 && ofsTimestamps > 0 &&
            ofsTimestamps + nTimestamps * 4 <= data.Length)
        {
            track.Timestamps.Capacity = (int)nTimestamps;
            for (uint i = 0; i < nTimestamps; i++)
                track.Timestamps.Add(ReadUInt32(data, (int)(ofsTimestamps + i * 4)));
        }

        // Keys: T per entry. Caller delegates parse + transform.
        if (nKeys > 0 && ofsKeys > 0 &&
            ofsKeys + nKeys * keyStride <= data.Length)
        {
            track.Keys.Capacity = (int)nKeys;
            for (uint i = 0; i < nKeys; i++)
                track.Keys.Add(readKey(data, (int)(ofsKeys + i * keyStride)));
        }

        // Sanity: timestamps and keys must have the same count. If they don't, the track is malformed — treat as empty so
        // UsesSequence returns false rather than crashing on misaligned reads.
        if (track.Timestamps.Count != track.Keys.Count)
        {
            track.Timestamps.Clear();
            track.Keys.Clear();
            track.Ranges.Clear();
        }

        return track;
    }

    private static void ParseKeyBoneLookup(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * 2 > data.Length) return;

        for (uint i = 0; i < count; i++)
        {
            short v = (short)ReadUInt16(data, (int)(offset + i * 2));
            model.KeyBoneLookup.Add(v);
        }
    }

    // ── Attachments ─────────────────────────────────────────────────────────
    //
    // 48-byte M2Attachment:
    //   +0   uint32 id              (semantic attachment ID, e.g. 1 = HandRight)
    //   +4   uint32 boneIndex
    //   +8   float[3] position      (MODEL SPACE — see SkinnedGlbWriter Session L)
    //  +20   AnimationBlockM2<bool> animateAttached (28 bytes — skipped)
    private static void ParseAttachments(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * ATTACHMENT_STRIDE > data.Length) return;

        model.Attachments.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * ATTACHMENT_STRIDE);

            uint id = ReadUInt32(data, off + 0);
            uint boneIdx = ReadUInt32(data, off + 4);

            float px = ReadFloat(data, off + 8);
            float py = ReadFloat(data, off + 12);
            float pz = ReadFloat(data, off + 16);

            var pos = new Vector3(px, pz, -py);

            model.Attachments.Add(new M2Attachment
            {
                Id = id,
                BoneIndex = boneIdx,
                Position = pos,
            });
        }
    }

    private static void ParseAttachmentLookup(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * 2 > data.Length) return;

        for (uint i = 0; i < count; i++)
        {
            short v = (short)ReadUInt16(data, (int)(offset + i * 2));
            model.AttachmentLookup.Add(v);
        }
    }

    // ── Inlined view ────────────────────────────────────────────────────────
    private static bool ParseInlinedView(byte[] data, uint viewOffset, M2Model model)
    {
        if (viewOffset + 44 > data.Length) return false;

        int off = (int)viewOffset;

        uint nLocalVerts = ReadUInt32(data, off + 0);
        uint ofsLocalVerts = ReadUInt32(data, off + 4);
        uint nTriIndices = ReadUInt32(data, off + 8);
        uint ofsTriIndices = ReadUInt32(data, off + 12);
        uint nSubmeshes = ReadUInt32(data, off + 24);
        uint ofsSubmeshes = ReadUInt32(data, off + 28);
        uint nBatches = ReadUInt32(data, off + 32);
        uint ofsBatches = ReadUInt32(data, off + 36);

        if (nLocalVerts == 0 || ofsLocalVerts == 0 || ofsLocalVerts + nLocalVerts * 2 > data.Length)
            return false;
        if (nTriIndices == 0 || ofsTriIndices == 0 || ofsTriIndices + nTriIndices * 2 > data.Length)
            return false;

        var localVertexMap = new ushort[nLocalVerts];
        for (uint i = 0; i < nLocalVerts; i++)
            localVertexMap[i] = ReadUInt16(data, (int)(ofsLocalVerts + i * 2));

        model.Indices.Capacity = (int)nTriIndices;
        for (uint i = 0; i < nTriIndices; i++)
        {
            ushort localIdx = ReadUInt16(data, (int)(ofsTriIndices + i * 2));
            model.Indices.Add(localIdx < nLocalVerts ? localVertexMap[localIdx] : (ushort)0);
        }

        if (nSubmeshes > 0 && ofsSubmeshes > 0 && ofsSubmeshes + nSubmeshes * 32 <= data.Length)
        {
            for (uint i = 0; i < nSubmeshes; i++)
            {
                int sOff = (int)(ofsSubmeshes + i * 32);
                model.Submeshes.Add(new M2Submesh
                {
                    Id = ReadUInt16(data, sOff + 0),
                    VertexStart = ReadUInt16(data, sOff + 4),
                    VertexCount = ReadUInt16(data, sOff + 6),
                    IndexStart = ReadUInt16(data, sOff + 8),
                    IndexCount = ReadUInt16(data, sOff + 10),
                });
            }
        }

        if (nBatches > 0 && ofsBatches > 0 && ofsBatches + nBatches * 24 <= data.Length)
        {
            for (uint i = 0; i < nBatches; i++)
            {
                int bOff = (int)(ofsBatches + i * 24);
                model.Batches.Add(new M2Batch
                {
                    Flags = data[bOff + 0],
                    PriorityPlane = data[bOff + 1],
                    ShaderId = ReadUInt16(data, bOff + 2),
                    SubmeshIndex = ReadUInt16(data, bOff + 4),
                    GeosetIndex = ReadUInt16(data, bOff + 6),
                    ColorIndex = (short)ReadUInt16(data, bOff + 8),
                    MaterialIndex = ReadUInt16(data, bOff + 10),
                    MaterialLayer = ReadUInt16(data, bOff + 12),
                    TextureCount = ReadUInt16(data, bOff + 14),
                    TextureIndex = ReadUInt16(data, bOff + 16),
                    TextureTransformIndex = ReadUInt16(data, bOff + 18),
                    TextureWeightIndex = ReadUInt16(data, bOff + 20),
                });
            }
        }

        return model.Indices.Count >= 3;
    }

    private static void ParseTextures(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        const int TEX_SIZE = 16;
        if (offset + count * TEX_SIZE > data.Length) return;

        for (uint i = 0; i < count; i++)
        {
            int tOff = (int)(offset + i * TEX_SIZE);
            uint type = ReadUInt32(data, tOff);
            uint flags = ReadUInt32(data, tOff + 4);
            uint nFilename = ReadUInt32(data, tOff + 8);
            uint ofsFilename = ReadUInt32(data, tOff + 12);

            string filename = "";
            if (nFilename > 1 && ofsFilename > 0 && ofsFilename + nFilename <= data.Length)
                filename = Encoding.ASCII.GetString(data, (int)ofsFilename, (int)nFilename).TrimEnd('\0');

            model.Textures.Add(new M2TextureRef { Type = type, Flags = flags, Filename = filename });
        }
    }

    private static void ParseTextureLookup(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * 2 > data.Length) return;
        for (uint i = 0; i < count; i++)
            model.TextureLookup.Add(ReadUInt16(data, (int)(offset + i * 2)));
    }

    private static void ParseRenderFlags(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        const int RENDERFLAG_STRIDE = 4;
        if (offset + count * RENDERFLAG_STRIDE > data.Length) return;

        model.RenderFlags.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * RENDERFLAG_STRIDE);
            model.RenderFlags.Add(new M2RenderFlag
            {
                Flags = ReadUInt16(data, off + 0),
                BlendingMode = ReadUInt16(data, off + 2),
            });
        }
    }

    // ── Transparency static alphas (Session N — see GlbWriter docs) ─────────
    //
    // Same AnimationBlockM2 wire format as bone TRS tracks (28 bytes), but
    // we only care about keys[0] of the first sequence for static evaluation.
    // The full Session N decode lives here verbatim from the prior version.
    private static void ParseTransparencyStaticAlphas(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * ANIM_BLOCK_STRIDE_VANILLA > data.Length) return;

        model.TransparencyStaticAlphas.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * ANIM_BLOCK_STRIDE_VANILLA);

            uint nKeys = ReadUInt32(data, off + 20);
            uint ofsKeys = ReadUInt32(data, off + 24);

            float alpha = 1.0f;

            if (nKeys > 0 && ofsKeys > 0 && ofsKeys + 2 <= data.Length)
            {
                short firstKey = (short)ReadUInt16(data, (int)ofsKeys);
                alpha = firstKey / 32767f;
                if (alpha < 0f) alpha = 0f;
                if (alpha > 1f) alpha = 1f;
            }

            model.TransparencyStaticAlphas.Add(alpha);
        }
    }

    private static void ParseTransparencyLookup(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * 2 > data.Length) return;

        model.TransparencyLookup.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
            model.TransparencyLookup.Add(ReadUInt16(data, (int)(offset + i * 2)));
    }

    // ── Binary helpers ──────────────────────────────────────────────────────

    private static uint ReadUInt32(byte[] data, int offset)
        => offset + 4 > data.Length ? 0u : BitConverter.ToUInt32(data, offset);

    private static ushort ReadUInt16(byte[] data, int offset)
        => offset + 2 > data.Length ? (ushort)0 : BitConverter.ToUInt16(data, offset);

    private static float ReadFloat(byte[] data, int offset)
        => offset + 4 > data.Length ? 0f : BitConverter.ToSingle(data, offset);
}