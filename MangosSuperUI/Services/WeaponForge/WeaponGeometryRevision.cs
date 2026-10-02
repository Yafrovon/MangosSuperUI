using System.Buffers.Binary;
using System.Security.Cryptography;
using MangosSuperUI.Services.WeaponForge.RawM2;

namespace MangosSuperUI.Services.WeaponForge;

/// <summary>Geometry-only revision of a registered opaque, rigid GLB weapon. No ID or texture authoring.</summary>
internal static class WeaponGeometryRevision
{
    internal const int MaxSourceBytes = 16 * 1024 * 1024;
    internal const int MaxTriangles = 4000;
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static void RequireHash(string? value, string label)
    {
        if (value is not { Length: 64 } || value.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException($"{label} must be a 64-character SHA-256.");
    }

    internal static void ValidateTarget(WeaponRevisionRow row, long displayId, long itemEntry, string expectedHash, int permittedAuxiliaryCount = 0)
    {
        RequireHash(expectedHash, "expectedModelSha256");
        if (displayId < WeaponIdReservationService.ItemDisplayFloor || itemEntry <= 0 ||
            row.DisplayId != displayId || row.ItemEntry != itemEntry || row.ModelId != displayId)
            throw new InvalidOperationException("The item, display and model must identify the same custom weapon.");
        if (row.SourceKind != "glb_import" || row.ManifestCount != 1 || row.DisplayCount != 1 || row.EffectTextureCount != permittedAuxiliaryCount)
            throw new InvalidOperationException("Revision requires one unshared GLB-authored model/display/item without effect textures.");
        if (row.M2 is not { Length: > 0 } || row.Blp is not { Length: > 0 })
            throw new InvalidOperationException("The registry is missing compiled model or texture bytes.");
        if (!string.Equals(Hash(row.M2), row.ModelSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Hash(row.Blp), row.TextureSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Stored registry bytes do not match their recorded hashes.");
        if (!string.Equals(row.ModelSha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The model changed since inspection. Load the current target and preview again.");
        string? textureError = WeaponAssetCompiler.ValidateBlp2Envelope(row.Blp);
        if (textureError is not null) throw new InvalidOperationException(textureError);
    }

    internal static WeaponRevisionPlan Compile(WeaponRevisionRow row, RigidWeaponMesh mesh, byte[] glb, bool replaceExplicitMaterial = false)
    {
        if (glb.Length is < 12 or > MaxSourceBytes || !glb.AsSpan(0, 4).SequenceEqual("glTF"u8))
            throw new InvalidOperationException("Revision requires a binary GLB of at most 16 MiB.");
        if (mesh.Passes is { Count: > 0 } || mesh.Material.BlendMode != WeaponBlendMode.Opaque || mesh.Material.TwoSided)
            throw new InvalidOperationException("Geometry revision currently supports opaque, single-pass, single-sided rigid meshes.");
        var meshDiagnostics = RigidWeaponMeshValidator.Validate(mesh, new MeshValidationOptions
        { Topology = WeaponTopologyMode.Variable, VariableHardCeiling = MaxTriangles });
        RequireValid(meshDiagnostics, "Mesh");
        var quality = EquipmentMeshAudit.Analyze(mesh);
        if (quality.Errors.Count > 0)
            throw new InvalidOperationException("Mesh audit failed: " + string.Join("; ", quality.Errors));
        var old = RawM2Document.Parse(row.M2, out var error)
            ?? throw new InvalidOperationException(error);
        RequireValid(M2BinaryValidator.Validate(row.M2, expectedViews: 4), "Existing M2");
        if (old.Views.Any(view => view.Submeshes.Count != 1 || view.Batches.Count != 1) ||
            old.FindArray("textures")?.Count != 1 ||
            old.FindArray("particleEmitters")?.Count > 0 || old.FindArray("ribbonEmitters")?.Count > 0 ||
            old.FindArray("collisionTriangles")?.Count > 0 || old.FindArray("collisionVertices")?.Count > 0)
            throw new InvalidOperationException("Existing model is outside the single-pass rigid revision contract.");
        var texture = old.FindArray("textures")!;
        if (BinaryPrimitives.ReadUInt32LittleEndian(row.M2.AsSpan((int)texture.Offset, 4)) != 2)
            throw new InvalidOperationException("Existing model must use its registered DBC texture.");
        var flags = old.FindArray("renderFlags")!;
        foreach (var view in old.Views)
        {
            int batch = checked((int)view.Batches.Offset);
            int material = BinaryPrimitives.ReadUInt16LittleEndian(row.M2.AsSpan(batch + 10, 2));
            int record = checked((int)flags.Offset + material * 4);
            if (!replaceExplicitMaterial && (BinaryPrimitives.ReadUInt16LittleEndian(row.M2.AsSpan(record + 2, 2)) != 0 ||
                (BinaryPrimitives.ReadUInt16LittleEndian(row.M2.AsSpan(record, 2)) & 4) != 0))
                throw new InvalidOperationException("Existing model is not opaque and single-sided.");
        }
        var vertices = old.FindArray("vertices")!;
        for (int i = 0; i < old.VertexCount; i++)
        {
            int offset = checked((int)vertices.Offset + i * 48);
            if (row.M2[offset + 12] != 255 || row.M2[offset + 16] != 0)
                throw new InvalidOperationException("Existing model is not rigidly bound to bone zero.");
        }
        // Reuse the current compiled scaffold, not a freshly resolved donor. The builder preserves
        // its animations, attachments, material tables, internal name and every nested pointer.
        byte[] revised = M2VariableTopologyBuilder.Build(row.M2,
            mesh.Positions.Select(CoordinateContract.MeshToWoW).ToArray(),
            mesh.Normals.Select(CoordinateContract.MeshNormalToWoW).ToArray(), mesh.Uv0, mesh, viewCount: 4,
            material: replaceExplicitMaterial ? mesh.Material : null);
        if (replaceExplicitMaterial)
            revised = M2GeometryPatcher.RewriteInternalName(revised, $"SUI_W_{row.DisplayId}.mdx");
        RequireValid(M2BinaryValidator.Validate(revised, mesh.VertexCount, 4), "Revised M2");
        // Enforce preservation independently of the builder: only geometry/view pointers and
        // the vertex bounds may change inside the existing file prefix.
        for (int i = 0; i < row.M2.Length; i++)
            if (i is not (>= 0x44 and < 0x54) and not (>= 0xb4 and < 0xd0) &&
                !(replaceExplicitMaterial && (i is >= 8 and < 16 || i >= flags.Offset && i < flags.Offset + flags.Count * 4)) && row.M2[i] != revised[i])
                throw new InvalidOperationException($"Revision changed preserved scaffold byte 0x{i:X}.");
        return new(row.ItemEntry, row.DisplayId, row.ModelId, row.BuildId, row.ModelSha256,
            Hash(revised), row.TextureSha256, Hash(glb), revised, quality, mesh.VertexCount, mesh.TriangleCount);
    }

    internal static void ValidateApplyHash(WeaponRevisionPlan plan, string? expectedRevisionModelSha256)
    {
        RequireHash(expectedRevisionModelSha256, "expectedRevisionModelSha256");
        if (!string.Equals(plan.ModelSha256, expectedRevisionModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The compiled revision differs from the reviewed preview. Preview again before applying.");
    }

    internal static void RequireUnchanged(WeaponRevisionRow before, WeaponRevisionRow current, int permittedAuxiliaryCount = 0)
    {
        ValidateTarget(current, before.DisplayId, before.ItemEntry, before.ModelSha256, permittedAuxiliaryCount);
        if (before.BuildId != current.BuildId || before.GameplayJson != current.GameplayJson ||
            before.DbcFieldsJson != current.DbcFieldsJson || before.TextureSha256 != current.TextureSha256 ||
            before.GeneratorParamsJson != current.GeneratorParamsJson || before.SourceSha256 != current.SourceSha256 ||
            before.ModelMpqPath != current.ModelMpqPath || before.TextureMpqPath != current.TextureMpqPath ||
            before.ItemVisual != current.ItemVisual || before.IconStem != current.IconStem || before.DonorDisplayId != current.DonorDisplayId)
            throw new InvalidOperationException("Weapon metadata changed during preview/apply. Inspect again.");
    }

    private static void RequireValid(ForgeDiagnostics diagnostics, string label)
    {
        if (diagnostics.HasErrors) throw new InvalidOperationException(label + " validation failed: " +
            string.Join("; ", diagnostics.Items.Where(x => x.Severity == ForgeSeverity.Error).Select(x => x.Message)));
    }
}

internal sealed class WeaponRevisionRow
{
    public long ItemEntry { get; set; }
    public long DisplayId { get; set; }
    public long ModelId { get; set; }
    public string BuildId { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string ModelSha256 { get; set; } = "";
    public string TextureSha256 { get; set; } = "";
    public string? SourceSha256 { get; set; }
    public string? GeneratorParamsJson { get; set; }
    public string? GameplayJson { get; set; }
    public string? DbcFieldsJson { get; set; }
    public string ModelMpqPath { get; set; } = "";
    public string TextureMpqPath { get; set; } = "";
    public string? IconStem { get; set; }
    public int ItemVisual { get; set; }
    public long DonorDisplayId { get; set; }
    public byte[] M2 { get; set; } = [];
    public byte[] Blp { get; set; } = [];
    public int ManifestCount { get; set; }
    public int DisplayCount { get; set; }
    public int EffectTextureCount { get; set; }
}

internal sealed record WeaponRevisionPlan(long ItemEntry, long DisplayId, long ModelId, string BuildId,
    string PreviousModelSha256, string ModelSha256, string TextureSha256, string SourceSha256,
    byte[] M2, EquipmentMeshAuditReport QualityAudit, int VertexCount, int TriangleCount);
