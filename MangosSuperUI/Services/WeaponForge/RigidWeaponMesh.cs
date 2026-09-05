using System.Numerics;

namespace MangosSuperUI.Services.WeaponForge;

/// <summary>
/// The writer-owned mesh AST for a single rigid weapon (WEAPON_GEN.md §2.3, §4.1). This is
/// deliberately NOT the lossy GLB-preview <c>M2Model</c>: it is the one input contract the pure
/// <c>WeaponAssetCompiler</c> accepts, and every route (parametric Route A, donor Route 0,
/// GLB/sketch Route B) produces one of these.
///
/// Authoring space is right-handed **Y-up** (see <see cref="CoordinateContract"/>): +X grip→tip,
/// grip at the origin, one unit == one WoW unit. Positions, normals, and UV0 are parallel arrays;
/// UV1 is optional for imported multi-texture assets. <see cref="Indices"/> is a flat triangle
/// list (multiple of 3), optionally partitioned into submeshes and render passes.
/// </summary>
public sealed class RigidWeaponMesh
{
    /// <summary>Vertex positions, Y-up mesh space.</summary>
    public required Vector3[] Positions { get; init; }

    /// <summary>Per-vertex normals, Y-up mesh space. Must be finite and non-zero (validated).</summary>
    public required Vector3[] Normals { get; init; }

    /// <summary>Per-vertex UV0, top-left image convention (U right, V down).</summary>
    public required Vector2[] Uv0 { get; init; }

    /// <summary>Optional per-vertex UV1, parallel to <see cref="Uv0"/>.</summary>
    public Vector2[]? Uv1 { get; init; }

    /// <summary>Flat triangle-list indices into the vertex arrays. Length is a multiple of 3.</summary>
    public required uint[] Indices { get; init; }

    /// <summary>
    /// Stable per-vertex identity, parallel to <see cref="Positions"/>. Fixed-topology phases
    /// (0–4) require these to match the golden donor's 34 vertex IDs exactly so an offset-preserving
    /// edit can be proven not to have lost/duplicated/reordered a vertex. Null for freshly generated
    /// variable topology (Phase 5+).
    /// </summary>
    public int[]? VertexIds { get; init; }

    /// <summary>The single material contract for v1: one opaque base pass, one Type-2 texture.</summary>
    public required WeaponMaterial Material { get; init; }

    /// <summary>
    /// Optional per-triangle semantic region label (blade/edge/fuller/guard/grip/pommel …),
    /// parallel to the triangle list (one entry per triangle). Route A supplies these so the
    /// compiler can emit region masks; Route B normally leaves it null. The compiler never
    /// fabricates semantic regions from UVs.
    /// </summary>
    public string[]? TriangleRegionIds { get; init; }

    /// <summary>
    /// What the importer/generator did to land this mesh in the canonical envelope — recorded, not
    /// guessed. Copied into the artifact manifest for reproducibility.
    /// </summary>
    public MeshNormalizationRecord Normalization { get; init; } = MeshNormalizationRecord.Identity;

    /// <summary>
    /// Multi-pass structure (TBC imports with glow layers). When present, <see cref="Indices"/> is
    /// laid out submesh-contiguous and each pass draws one submesh range with its own render flags
    /// and texture slot. Null = the whole mesh is one base pass with <see cref="Material"/> — every
    /// pre-existing route (GLB import, parametric) stays on that path untouched.
    /// </summary>
    public IReadOnlyList<WeaponSubmeshRange>? SubmeshRanges { get; init; }

    /// <summary>Render passes over <see cref="SubmeshRanges"/>; null = single-pass (see above).</summary>
    public IReadOnlyList<WeaponPass>? Passes { get; set; }

    /// <summary>Optional per-slot source texture metadata, including M2 wrap flags.</summary>
    public IReadOnlyList<WeaponTextureSlot>? TextureSlots { get; init; }

    public int VertexCount => Positions.Length;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>One contiguous submesh block inside a multi-pass mesh.</summary>
public sealed record WeaponSubmeshRange
{
    public required int IndexStart { get; init; }   // into RigidWeaponMesh.Indices; multiple of 3
    public required int IndexCount { get; init; }
    public required int VertexStart { get; init; }
    public required int VertexCount { get; init; }
}

/// <summary>One render pass of a multi-pass weapon: a submesh drawn with raw M2 render-flag bits
/// and blend mode (carried verbatim from the source — vanilla supports GxBlend 0–6), layered by
/// the M2 batch MaterialLayer, sampling one texture slot.</summary>
public sealed record WeaponPass
{
    public required int SubmeshSlot { get; init; }

    /// <summary>Raw M2 render-flag bits (0x01 unlit, 0x04 two-sided, 0x10 no-z-write …).</summary>
    public required ushort RenderFlags { get; init; }

    /// <summary>Raw M2 blend mode (0 opaque, 1 alpha-key, 2 alpha, 3/4 additive …).</summary>
    public required ushort BlendMode { get; init; }

    /// <summary>Batch MaterialLayer — orders coincident passes without z-fighting.</summary>
    public required int Layer { get; init; }

    /// <summary>0 = the DBC-driven base texture (Type-2 slot); 1.. = effect texture (Type-0
    /// hardcoded SUI_W_####_E0N path packaged alongside the model).</summary>
    public required int TextureSlot { get; init; }

    /// <summary>Original batch order in the source view.</summary>
    public int SourceOrder { get; init; }

    /// <summary>Raw batch flags and signed priority plane.</summary>
    public byte BatchFlags { get; init; }
    public sbyte PriorityPlane { get; init; }

    /// <summary>Raw source shader ID and optional vertex-color track index.</summary>
    public ushort ShaderId { get; init; }
    public short ColorIndex { get; init; } = -1;

    /// <summary>
    /// Evaluated source color/opacity at the deterministic rest sample. The raw
    /// <see cref="ColorIndex"/> remains source provenance; writers allocate a new v256 record.
    /// </summary>
    public WeaponRestColor? RestColor { get; init; }

    /// <summary>
    /// Texture units bound by this batch. Null preserves compatibility with legacy one-texture
    /// passes, which use <see cref="TextureSlot"/> with UV0 and full static alpha.
    /// </summary>
    public IReadOnlyList<WeaponTextureBinding>? TextureBindings { get; init; }
}

/// <summary>One texture unit within an M2 batch.</summary>
public sealed record WeaponTextureBinding
{
    public required int TextureSlot { get; init; }
    public ushort TextureCoordinate { get; init; } = 0;
    public float StaticAlpha { get; init; } = 1f;
    public ushort TextureTransform { get; init; } = 0xFFFF;

    /// <summary>Evaluated source UV transform. Null means the source lookup sentinel was 0xFFFF.</summary>
    public WeaponRestTextureTransform? RestTransform { get; init; }
}

/// <summary>A source material color frozen to a deterministic static sample.</summary>
public sealed record WeaponRestColor(Vector3 Rgb, float Alpha, bool AnimationFrozen);

/// <summary>A range-free global Vector3 track preserved from a source material animation.</summary>
public sealed record WeaponGlobalVectorTrack(
    ushort Interpolation,
    int SourceGlobalSequence,
    uint DurationMs,
    IReadOnlyList<uint> Timestamps,
    IReadOnlyList<Vector3> Keys);

/// <summary>A range-free global texture-quaternion track, decoded to float XYZW keys.</summary>
public sealed record WeaponGlobalQuaternionTrack(
    ushort Interpolation,
    int SourceGlobalSequence,
    uint DurationMs,
    IReadOnlyList<uint> Timestamps,
    IReadOnlyList<Quaternion> Keys);

/// <summary>
/// A deterministic source texture-transform sample plus any supported global animation payload.
/// <see cref="AnimationFrozen"/> is true only when source animation could not be represented.
/// </summary>
public sealed record WeaponRestTextureTransform(
    Vector3 Translation,
    Quaternion Rotation,
    Vector3 Scale,
    bool AnimationFrozen,
    WeaponGlobalVectorTrack? TranslationAnimation = null,
    WeaponGlobalQuaternionTrack? RotationAnimation = null,
    WeaponGlobalVectorTrack? ScaleAnimation = null);

/// <summary>Source metadata for one texture slot.</summary>
public sealed record WeaponTextureSlot
{
    public required uint Flags { get; init; }
}

/// <summary>The single-pass material: one base render pass bound to one Type-2 (empty-filename) M2
/// texture slot whose pixels come from ItemDisplayInfo.TextureName1. Opaque (DXT1) is the default;
/// <see cref="WeaponBlendMode.AlphaKey"/> (DXT3 + blend-mode-1 render flag) exists because many
/// TBC blades cut their silhouette out of a sheet with texture alpha — imported opaque they render
/// as solid black slabs. Imported multi-pass/additive materials use <see cref="WeaponPass"/>.</summary>
public sealed class WeaponMaterial
{
    public WeaponBlendMode BlendMode { get; init; } = WeaponBlendMode.Opaque;

    /// <summary>Two-sided rendering (M2 render-flag bit 0x04). Vanilla weapons are single-sided;
    /// alpha-cut TBC sheet blades are usually authored two-sided.</summary>
    public bool TwoSided { get; init; } = false;
}

public enum WeaponBlendMode
{
    /// <summary>Opaque base pass, no alpha. Maps to M2 blend mode 0 / DXT1.</summary>
    Opaque = 0,

    /// <summary>Alpha-keyed (tested) base pass — texture alpha cuts the silhouette.
    /// Maps to M2 blend mode 1 / DXT3.</summary>
    AlphaKey = 1,
}

/// <summary>Explicit record of the affine normalization applied to bring source geometry into the
/// canonical grip-at-origin, +X-blade, WoW-unit envelope. Identity when the generator authored
/// directly in canonical space (Route A).</summary>
public sealed class MeshNormalizationRecord
{
    public float Scale { get; init; } = 1f;
    public Vector3 Translation { get; init; } = Vector3.Zero;
    /// <summary>True if the importer reversed triangle winding once for a mirrored (negative
    /// determinant) source node transform. Recorded so the operation is auditable.</summary>
    public bool WindingReversed { get; init; }
    /// <summary>Free-form note on how orientation/scale were determined (e.g. "authored canonical",
    /// "PCA long-axis → +X, grip end at min-X", "explicit owner grip marker").</summary>
    public string Method { get; init; } = "identity";

    public static MeshNormalizationRecord Identity { get; } = new() { Method = "identity" };
}
