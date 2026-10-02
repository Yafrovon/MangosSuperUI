using System.Text;
using System.Text.Json;
using MangosSuperUI.Services.ArmorForge;
using SkiaSharp;

namespace MangosSuperUI.Services.WeaponForge;

/// <summary>ID-preserving complete rigid weapon art. Explicit PNGs replace texture and icon.</summary>
internal static class WeaponArtRevision
{
    internal const int MaxPngBytes = 2 * 1024 * 1024;
    internal static string IconStem(long displayId) => $"INV_SUI_W_{displayId}_RAID";
    internal static string IconPath(long displayId) => $@"Interface\Icons\{IconStem(displayId)}.blp";

    internal static void ValidateTarget(WeaponRevisionRow row, IReadOnlyList<WeaponArtAuxiliary> auxiliary,
        long displayId, long itemEntry, string expectedModel, string expectedTexture)
    {
        WeaponGeometryRevision.RequireHash(expectedTexture, "expectedTextureSha256");
        if (auxiliary.Count > 1 || auxiliary.Any(a => a.Slot != 1 ||
            !string.Equals(a.Path, IconPath(displayId), StringComparison.OrdinalIgnoreCase) ||
            WeaponGeometryRevision.Hash(a.Bytes) != a.Sha256.ToLowerInvariant() ||
            WeaponAssetCompiler.ValidateBlp2Envelope(a.Bytes) is not null))
            throw new InvalidOperationException("Full-art revision accepts no effect textures or unrelated auxiliary members.");
        if (auxiliary.Count == 1 && row.IconStem != IconStem(displayId))
            throw new InvalidOperationException("The registered icon stem does not bind its owned icon member.");
        WeaponGeometryRevision.ValidateTarget(row, displayId, itemEntry, expectedModel, auxiliary.Count);
        if (!string.Equals(row.TextureSha256, expectedTexture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The texture changed since inspection. Inspect and preview again.");
    }

    internal static WeaponArtRevisionPlan Compile(WeaponRevisionRow row, RigidWeaponMesh mesh,
        byte[] glb, byte[] texturePng, byte[] iconPng, BlpWriterService writer)
    {
        var geometry = WeaponGeometryRevision.Compile(row, mesh, glb, replaceExplicitMaterial: true);
        byte[] blp = Encode(texturePng, 256, "Skin", writer, requireOpaque:true);
        byte[] icon = Encode(iconPng, 64, "Icon", writer, requireOpaque:false);
        return new(geometry, blp, icon, WeaponGeometryRevision.Hash(blp), WeaponGeometryRevision.Hash(icon),
            WeaponGeometryRevision.Hash(texturePng), WeaponGeometryRevision.Hash(iconPng), IconStem(row.DisplayId), IconPath(row.DisplayId));
    }

    private static byte[] Encode(byte[] png, int size, string label, BlpWriterService writer, bool requireOpaque)
    {
        if (png.Length > MaxPngBytes) throw new InvalidOperationException($"{label} exceeds the 2 MiB PNG limit.");
        using var bitmap = AuthoredArmorAssetValidator.DecodePng(png);
        if (bitmap.Width != size || bitmap.Height != size || requireOpaque && bitmap.Pixels.Any(p => p.Alpha != 255))
            throw new InvalidOperationException($"{label} must be {(requireOpaque ? "an opaque" : "a")} {size}×{size} PNG; no automatic resizing or alpha flattening.");
        // Equipment skins here are opaque; inventory icons retain authored alpha via DXT3.
        byte[] result = writer.EncodeBitmapToBlp(bitmap, useDxt1: requireOpaque)
            ?? throw new InvalidOperationException($"{label} BLP encoding failed.");
        string? error = WeaponAssetCompiler.ValidateBlp2Envelope(result);
        if (error is not null) throw new InvalidOperationException(error);
        return result;
    }

    internal static string Token(WeaponRevisionRow row, IReadOnlyList<WeaponArtAuxiliary> auxiliary, WeaponArtRevisionPlan plan) =>
        WeaponGeometryRevision.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            row.ItemEntry, row.DisplayId, row.ModelId, row.BuildId, row.SourceKind, row.ModelSha256, oldTextureSha256=row.TextureSha256,
            row.SourceSha256, row.GeneratorParamsJson, row.GameplayJson, row.DbcFieldsJson, row.ModelMpqPath,
            row.TextureMpqPath, oldIconStem=row.IconStem, row.ItemVisual, row.DonorDisplayId,
            auxiliary = auxiliary.Select(a => new { a.Slot, a.Path, a.Sha256 }),
            newModel = plan.Geometry.ModelSha256, newSource = plan.Geometry.SourceSha256,
            plan.TextureSha256, plan.IconSha256, plan.TextureSourceSha256, plan.IconSourceSha256, plan.IconStem, plan.IconMpqPath
        })));

    internal static void ValidateApply(WeaponArtRevisionPlan plan, string actualToken, string? expectedToken,
        string? expectedModel, string? expectedTexture, string? expectedIcon)
    {
        foreach (var pair in new[] { (expectedToken, actualToken), (expectedModel, plan.Geometry.ModelSha256),
                     (expectedTexture, plan.TextureSha256), (expectedIcon, plan.IconSha256) })
        {
            WeaponGeometryRevision.RequireHash(pair.Item1, "Reviewed revision hash/token");
            if (!string.Equals(pair.Item1, pair.Item2, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Art or preserved state differs from the reviewed dry run. No revision was applied.");
        }
    }
}

internal sealed class WeaponArtAuxiliary
{
    public int Slot { get; set; }
    public string Path { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
    public string Sha256 { get; set; } = "";
}

internal sealed record WeaponArtRevisionPlan(WeaponRevisionPlan Geometry, byte[] Blp, byte[] IconBlp,
    string TextureSha256, string IconSha256, string TextureSourceSha256, string IconSourceSha256, string IconStem, string IconMpqPath);
