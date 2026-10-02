using System.Runtime.InteropServices;
using System.Numerics;
using System.Text.Json;
using MangosSuperUI.Services.WeaponForge;
using MangosSuperUI.Services.WeaponForge.RawM2;
using SkiaSharp;

namespace MangosSuperUI.Services.ArmorForge;

public sealed record AuthoredArmorCompiledPiece(string Key, int DisplayId, ArmorImportSource Source);
public sealed record AuthoredArmorCompilation(AuthoredArmorPackage Package, AuthoredArmorReport Report,
    IReadOnlyList<AuthoredArmorCompiledPiece> Pieces);

/// <summary>Compiles authored geometry and paint. Stock M2s supply the binary skeleton/view scaffold only;
/// every visible vertex, UV and pixel comes from the submitted package.</summary>
public sealed class AuthoredArmorCompiler(MpqReaderService mpq, BlpWriterService blp)
{
    public AuthoredArmorCompilation Compile(AuthoredArmorPackage package, IReadOnlyDictionary<string, int>? displayIds = null)
    {
        var report = new AuthoredArmorAssetValidator().Validate(package);
        var pieces = new List<AuthoredArmorCompiledPiece>();
        if (!report.Valid) return new(package, report, pieces);
        var material = Enum.Parse<ArmorMaterial>(package.Manifest.Material, true);
        foreach (var piece in package.Manifest.Pieces)
        {
            int display = displayIds is null ? pieces.Count + 1 : displayIds[piece.Key];
            try
            {
                var profile = ArmorTypeCatalog.Get(piece.Key);
                var source = new ArmorImportSource
                {
                    Entry = 0, Name = piece.Name, FamilyKey = piece.Key, RenderKind = profile.RenderKind,
                    Material = material, Quality = 3, ItemLevel = 60, RequiredLevel = 55,
                    IconStem = $"INV_SUI_A_{display:D4}", GeosetGroup = (int[])piece.GeosetGroup.Clone(),
                    HelmetVis0 = piece.HelmetVis[0], HelmetVis1 = piece.HelmetVis[1],
                };
                AddMember($@"Interface\Icons\{source.IconStem}.blp", Encode(piece.IconPng, true));
                foreach (var component in piece.Components)
                {
                    string path = $@"Item\TextureComponents\{ArmorNaming.ComponentSubdirs[component.Slot]}\{ArmorNaming.ComponentStem(display, component.Slot)}{component.Gender}.blp";
                    var bytes = Encode(component.Png, true);
                    source.Components.Add(new() { Slot = component.Slot, GenderSuffix = component.Gender, MpqPath = path, Blp = bytes });
                    report.CompiledSha256[path] = AuthoredArmorPackage.Hash(bytes);
                }
                if (profile.RenderKind == ArmorRenderKind.Modelled)
                {
                    source.TextureName = ArmorNaming.TextureStem(display);
                    source.TextureMpqPath = ArmorNaming.TextureMpqPath(display, profile.ComponentDir!);
                    source.TextureBlp = Encode(piece.SkinPng!, false);
                    report.CompiledSha256[source.TextureMpqPath] = AuthoredArmorPackage.Hash(source.TextureBlp);
                    var models = piece.Models.Select(m => (Side: m.Key, Body: (string?)null, Path: m.Value))
                        .Concat(piece.ShoulderFits.SelectMany(b => b.Value.Select(s => (Side: s.Key, Body: (string?)b.Key, Path: s.Value))));
                    foreach (var (variant, body, path) in models)
                    {
                        // The full report was produced before compilation; this parse repeats exact authored geometry.
                        var mesh = AuthoredArmorAssetValidator.ReadMesh(package.Assets[path], path, new AuthoredArmorReport());
                        string donorPath = piece.Key == "helm"
                            ? $@"{ArmorNaming.HeadDir}\Helm_Leather_D_01_{variant}.m2"
                            : $@"{ArmorNaming.ShoulderDir}\{variant}Shoulder_Leather_A_01.m2";
                        var donor = mpq.ExtractFile(donorPath) ?? throw new InvalidDataException($"Required vanilla scaffold is unavailable in mounted MPQs: {donorPath}");
                        byte[] bytes = M2VariableTopologyBuilder.Build(donor,
                            mesh.Positions.Select(CoordinateContract.MeshToWoW).ToArray(),
                            mesh.Normals.Select(CoordinateContract.MeshNormalToWoW).ToArray(), mesh.Uv0, mesh, 4, mesh.Material);
                        string name = $"{ArmorNaming.ModelStem(display)}_{variant}" + (body is null ? "" : "_fit_" + body);
                        // Only a default side with declarations carries the generic opt-in marker.
                        // The companion hashes these final bytes; the M2 never hashes its companion.
                        bool marked = piece.Key == "shoulder" && body is null && piece.ShoulderFits.Any(b => b.Value.ContainsKey(variant));
                        bytes = M2GeometryPatcher.RewriteInternalName(bytes, name + (marked ? ShoulderFitResolver.NameMarker : ""));
                        var validation = M2BinaryValidator.Validate(bytes, expectedVertexCount: mesh.VertexCount, expectedViews: 4);
                        if (validation.HasErrors) throw new InvalidDataException(string.Join("; ", validation.Items.Select(x => x.ToString())));
                        var parsed = M2Reader.Parse(bytes) ?? throw new InvalidDataException("Compiled M2 failed to reopen.");
                        if (parsed.Vertices.Count != mesh.VertexCount) throw new InvalidDataException("Compiled vertex count differs from authored geometry.");
                        for (int v = 0; v < mesh.VertexCount; v++)
                        {
                            var actual = parsed.Vertices[v];
                            if (Vector3.DistanceSquared(new(actual.PosX, actual.PosY, actual.PosZ), mesh.Positions[v]) > 1e-12f
                                || Vector3.DistanceSquared(new(actual.NormX, actual.NormY, actual.NormZ), mesh.Normals[v]) > 1e-10f
                                || Vector2.DistanceSquared(new(actual.TexU, actual.TexV), mesh.Uv0[v]) > 1e-12f)
                                throw new InvalidDataException($"Compiled vertex {v} changed position, normal or UV during the coordinate round trip.");
                        }
                        if (!parsed.Indices.Select(i => (uint)i).SequenceEqual(mesh.Indices)) throw new InvalidDataException("Compiled triangle order differs from authored topology.");
                        string member = piece.Key == "helm" ? ArmorNaming.HelmVariantMpqPath(display, variant)
                            : variant == "L" ? ArmorNaming.ShoulderLeftMpqPath(display) : ArmorNaming.ShoulderRightMpqPath(display);
                        if (body is not null) member = ShoulderFitResolver.FittedPath(member, body);
                        AddMember(member, bytes);
                    }
                    foreach (string side in new[] { "L", "R" }.Where(s => piece.ShoulderFits.Any(b => b.Value.ContainsKey(s))))
                    {
                        string defaultPath = side == "L" ? ArmorNaming.ShoulderLeftMpqPath(display) : ArmorNaming.ShoulderRightMpqPath(display);
                        var fitManifest = new ShoulderFitManifest { Side = side,
                            Default = new(defaultPath, report.CompiledSha256[defaultPath]),
                            Skin = new(source.TextureMpqPath, report.CompiledSha256[source.TextureMpqPath]) };
                        foreach (string body in piece.ShoulderFits.Where(b => b.Value.ContainsKey(side)).Select(b => b.Key).Order(StringComparer.Ordinal))
                        {
                            string member = ShoulderFitResolver.FittedPath(defaultPath, body);
                            fitManifest.Variants.Add(body, new(member, report.CompiledSha256[member]));
                        }
                        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(fitManifest, ShoulderFitResolver.JsonOptions);
                        ShoulderFitResolver.Read(manifestBytes);
                        AddMember(ShoulderFitResolver.CompanionPath(defaultPath), manifestBytes);
                    }
                    source.ModelName = piece.Key == "helm" ? ArmorNaming.DbcModelName(display) : ArmorNaming.ShoulderLeftDbcName(display);
                    source.ModelName2 = piece.Key == "shoulder" ? ArmorNaming.ShoulderRightDbcName(display) : null;
                }
                pieces.Add(new(piece.Key, display, source));
                void AddMember(string path, byte[] bytes)
                { source.ModelMembers.Add(new() { MpqPath = path, Data = bytes }); report.CompiledSha256[path] = AuthoredArmorPackage.Hash(bytes); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { report.Error(piece.Key, "Compilation failed: " + ex.Message); }
        }
        report.Compiled = report.Valid && pieces.Count == 8;
        return new(package, report, pieces);

        byte[] Encode(string path, bool preserveAlpha)
        {
            using var bitmap = AuthoredArmorAssetValidator.DecodePng(package.Assets[path]);
            var bytes = preserveAlpha ? blp.EncodeBitmapToBlpUncompressed(bitmap) : blp.EncodeBitmapToBlp(bitmap, useDxt1: true);
            if (bytes is null || WeaponAssetCompiler.ValidateBlp2Envelope(bytes) is { } error)
                throw new InvalidDataException($"BLP encoding failed for {path}.");
            return bytes;
        }
    }

    /// <summary>Preview only the re-opened compiled M2 and decoded compiled BLP. No source-preview shortcut.</summary>
    public static void WritePreviews(AuthoredArmorCompilation compilation, string directory)
    {
        if (!compilation.Report.Compiled) throw new InvalidOperationException("Compile successfully before generating previews.");
        Directory.CreateDirectory(directory);
        foreach (var piece in compilation.Pieces)
        {
            var source = piece.Source;
            foreach (var c in source.Components) File.WriteAllBytes(Path.Combine(directory, $"{piece.Key}_slot{c.Slot}{c.GenderSuffix}.png"), DecodeBlp(c.Blp));
            foreach (var m in source.ModelMembers.Where(m => m.MpqPath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)))
            {
                var parsed = M2Reader.Parse(m.Data) ?? throw new InvalidDataException("Compiled M2 failed to reopen for preview.");
                string variant = PreviewVariant(piece, m.MpqPath);
                var path = Path.Combine(directory, $"{piece.Key}_{variant}.glb");
                if (!GlbWriter.SaveGlb(parsed, source.TextureBlp!, path, doubleSided: false)) throw new InvalidDataException("Compiled GLB preview export failed.");
            }
            if (piece.Key == "shoulder" && compilation.Package.Manifest.Pieces.Single(p => p.Key == "shoulder").ShoulderFits.Count > 0)
                AuthoredArmorShoulderPreviews.Write(piece, directory);
        }
    }

    internal static string PreviewVariant(AuthoredArmorCompiledPiece piece, string path)
    {
        if (piece.Key != "shoulder") return Path.GetFileNameWithoutExtension(path.Replace('\\', '/')).Split('_').Last();
        foreach (string side in new[] { "L", "R" })
        {
            string defaultPath = side == "L" ? ArmorNaming.ShoulderLeftMpqPath(piece.DisplayId) : ArmorNaming.ShoulderRightMpqPath(piece.DisplayId);
            if (path.Equals(defaultPath, StringComparison.OrdinalIgnoreCase)) return side;
            foreach (string body in ShoulderFitResolver.BodyCodes)
                if (path.Equals(ShoulderFitResolver.FittedPath(defaultPath, body), StringComparison.OrdinalIgnoreCase)) return side + "_" + body;
        }
        throw new InvalidDataException("Undeclared shoulder preview member.");
    }

    private static byte[] DecodeBlp(byte[] bytes)
    {
        var pixels = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length); bitmap.NotifyPixelsChanged();
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
