using System.Numerics;
using MangosSuperUI.Services.WeaponForge;
using SharpGLTF.Schema2;
using SkiaSharp;

namespace MangosSuperUI.Services.ArmorForge;

/// <summary>Strict authoring checks, independent of world DB and client MPQ availability.
/// Does not silently repair, normalize, decimate, mirror, or substitute one race's mesh for another.</summary>
public sealed class AuthoredArmorAssetValidator
{
    public const int MaxAttachmentTriangles = 1500;

    public AuthoredArmorReport Validate(AuthoredArmorPackage package)
    {
        var m = package.Manifest;
        var report = new AuthoredArmorReport { PackageSha256 = package.Sha256, Name = m.Name, Material = m.Material };
        if (m.SchemaVersion != 1) report.Error("manifest", "schemaVersion must be 1.");
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 100) report.Error("manifest", "Set name must contain 1–100 characters.");
        if (string.IsNullOrWhiteSpace(m.Author) || m.Author.Length > 160 || string.IsNullOrWhiteSpace(m.DesignNotes) || m.DesignNotes.Length > 4000)
            report.Error("manifest", "Author (1–160 characters) and designNotes (1–4000 characters) are required provenance declarations.");
        if (m.Material is not ("plate" or "mail" or "leather" or "cloth")) report.Error("manifest", "Material must be plate, mail, leather, or cloth.");
        if (m.Pieces is null || m.Pieces.Count != 8 || m.Pieces.Any(p => p is null))
        { report.Error("manifest", "Exactly eight pieces are required."); return report; }
        var keys = m.Pieces.Select(p => p.Key).ToArray();
        var expected = new[] { "helm", "shoulder", "legs", "gloves", "boots", "bracers", "belt" };
        if (keys.Distinct().Count() != 8 || expected.Any(k => !keys.Contains(k)) || keys.Count(k => k is "chest" or "robe") != 1)
        { report.Error("manifest", "Required slots: helm, shoulder, chest OR robe, legs, gloves, boots, bracers, belt; no duplicates."); return report; }
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in m.Pieces)
        {
            if (string.IsNullOrWhiteSpace(piece.Name) || piece.Name.Length > 100) report.Error(piece.Key, "Piece name must contain 1–100 characters.");
            if (piece.GeosetGroup is not { Length: 3 } || piece.GeosetGroup.Any(g => g < 0 || g > 10)) report.Error(piece.Key, "geosetGroup must contain three values in 0..10.");
            if (piece.HelmetVis is not { Length: 2 }) report.Error(piece.Key, "helmetVis must contain the two vanilla visibility row IDs.");
            CheckImage(piece.IconPng, 64, 64, false);
            if (piece.Models is null || piece.Components is null) { report.Error(piece.Key, "models and components must not be null."); continue; }
            if (piece.ShoulderFits is null) { report.Error(piece.Key, "shoulderFits must not be null."); continue; }
            if (piece.Key != "shoulder" && piece.ShoulderFits.Count != 0)
                report.Error(piece.Key, "shoulderFits is allowed only for the shoulder piece.");
            var profile = ArmorTypeCatalog.Get(piece.Key);
            if (profile.RenderKind == ArmorRenderKind.Modelled)
            {
                if (piece.Components.Count != 0) report.Error(piece.Key, "Attached models cannot paint body components.");
                CheckImage(piece.SkinPng, 0, 0, true);
                var variants = piece.Key == "helm" ? ArmorNaming.HelmVariantSuffixes : new[] { "L", "R" };
                if (piece.Models.Count != variants.Count || variants.Any(s => !piece.Models.ContainsKey(s)))
                    report.Error(piece.Key, "Explicit models are required for: " + string.Join(", ", variants));
                if (piece.Models.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != piece.Models.Count)
                    report.Error(piece.Key, "Each race/gender or shoulder side must name its own authored GLB.");
                var modelPaths = piece.Models.Values.ToList();
                foreach (var (body, sides) in piece.ShoulderFits)
                {
                    if (!ShoulderFitResolver.BodyCodes.Contains(body, StringComparer.Ordinal) || sides is null || sides.Count is < 1 or > 2)
                    { report.Error(piece.Key, "Each shoulderFits entry needs a supported exact body code and one or two sides."); continue; }
                    foreach (var (side, path) in sides)
                    {
                        if (side is not ("L" or "R")) report.Error(piece.Key, "Fitted shoulder sides must be exactly L or R.");
                        modelPaths.Add(path);
                    }
                }
                if (modelPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != modelPaths.Count)
                    report.Error(piece.Key, "Every default and fitted model must name its own authored GLB.");
                foreach (var path in modelPaths)
                {
                    if (!Reference(path, ".glb", out var data)) continue;
                    try { _ = ReadMesh(data!, path, report); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { report.Error(path, "GLB rejected: " + ex.Message); }
                }
            }
            else
            {
                if (piece.Models.Count != 0 || piece.SkinPng is not null) report.Error(piece.Key, "Painted pieces use components only.");
                if (piece.Components.Count == 0) report.Error(piece.Key, "Painted pieces need at least one visible body component.");
                var pairs = new HashSet<(int, string)>();
                foreach (var c in piece.Components)
                {
                    if (c is null) { report.Error(piece.Key, "Null component."); continue; }
                    if (!profile.PaintedSlots.Contains(c.Slot) || c.Gender is not ("_U" or "_M" or "_F") || !pairs.Add((c.Slot, c.Gender)))
                        report.Error(piece.Key, "Invalid, duplicate, or out-of-slot body component.");
                    var size = LegacyArmorImporter.ComponentRegion(c.Slot);
                    CheckImage(c.Png, size.Width, size.Height, false);
                }
                // PaintedSlots is an allowed-region filter, not mandatory coverage.
                // Shoes can paint only feet; requiring a shin texture overwrites robe hems.
                foreach (int slot in pairs.Select(p => p.Item1).Distinct())
                    if (!pairs.Contains((slot, "_U")) && !(pairs.Contains((slot, "_M")) && pairs.Contains((slot, "_F"))))
                        report.Error(piece.Key, $"Slot {slot} needs _U or both _M and _F textures.");
            }
        }
        foreach (string path in package.Assets.Keys.Where(p => !referenced.Contains(p))) report.Error(path, "Unreferenced ZIP member.");
        report.Warn("runtime", report.RuntimeRequirement);
        return report;

        bool Reference(string? path, string extension, out byte[]? bytes)
        {
            bytes = null;
            if (!AuthoredArmorPackage.SafePath(path) || !path!.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            { report.Error(path ?? "missing path", $"A safe relative {extension} path is required."); return false; }
            referenced.Add(path);
            if (!package.Assets.TryGetValue(path, out bytes)) { report.Error(path, "Referenced member is missing."); return false; }
            return true;
        }
        void CheckImage(string? path, int width, int height, bool modelSkin)
        {
            if (!Reference(path, ".png", out var bytes)) return;
            try
            {
                using var bitmap = DecodePng(bytes!);
                if (modelSkin ? bitmap.Width is not (128 or 256) || bitmap.Height is not (128 or 256) : bitmap.Width != width || bitmap.Height != height)
                    report.Error(path!, modelSkin ? "Model skin must be 128×128, 256×128, 128×256 or 256×256." : $"Expected {width}×{height}, got {bitmap.Width}×{bitmap.Height}.");
                var pixels = bitmap.Pixels;
                double sum = 0, sum2 = 0; int visible = 0, opaque = 0, dark = 0, light = 0;
                foreach (var p in pixels)
                {
                    if (p.Alpha == 255) opaque++;
                    if (p.Alpha == 0) continue;
                    double l = (.2126 * p.Red + .7152 * p.Green + .0722 * p.Blue) / 255;
                    sum += l; sum2 += l * l; visible++; if (l < .02) dark++; if (l > .98) light++;
                }
                if (visible == 0) report.Error(path!, "Texture is fully transparent.");
                if (modelSkin && opaque != pixels.Length) report.Error(path!, "Model skin must be opaque; masked materials need a separate explicit contract.");
                double mean = sum / Math.Max(1, visible), deviation = Math.Sqrt(Math.Max(0, sum2 / Math.Max(1, visible) - mean * mean));
                if (deviation < .015) report.Warn(path!, "Almost flat luminance: review material readability at game distance.");
                if (!report.Textures.Any(t => t.Asset.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    report.Textures.Add(new(path!, AuthoredArmorPackage.Hash(bytes!), bitmap.Width, bitmap.Height, mean, deviation,
                        (double)opaque / pixels.Length, (double)dark / Math.Max(1, visible), (double)light / Math.Max(1, visible)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { report.Error(path!, "PNG rejected: " + ex.Message); }
        }
    }

    public static SKBitmap DecodePng(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Not a PNG.");
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || codec.Info.Width > 256 || codec.Info.Height > 256)
            throw new InvalidDataException("PNG dimensions must be within 256×256.");
        return SKBitmap.Decode(codec) ?? throw new InvalidDataException("PNG pixels could not be decoded.");
    }

    public static RigidWeaponMesh ReadMesh(byte[] glb, string asset, AuthoredArmorReport report)
    {
        // Reject external resource URIs before SharpGLTF can attempt resolution outside the ZIP.
        if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67 || BitConverter.ToUInt32(glb, 4) != 2 || BitConverter.ToUInt32(glb, 8) != glb.Length)
            throw new InvalidDataException("Expected a self-contained glTF 2 GLB.");
        int jsonLength = checked((int)BitConverter.ToUInt32(glb, 12));
        if (jsonLength <= 0 || jsonLength > glb.Length - 20 || BitConverter.ToUInt32(glb, 16) != 0x4E4F534A) throw new InvalidDataException("Invalid GLB JSON chunk.");
        using (var json = System.Text.Json.JsonDocument.Parse(glb.AsMemory(20, jsonLength)))
        {
            foreach (string collection in new[] { "buffers", "images" })
                if (json.RootElement.TryGetProperty(collection, out var entries))
                    foreach (var entry in entries.EnumerateArray())
                        if (entry.TryGetProperty("uri", out _)) throw new InvalidDataException("External/data URIs are not accepted; use embedded GLB buffers.");
            if (json.RootElement.TryGetProperty("materials", out var materials))
                foreach (var material in materials.EnumerateArray())
                {
                    if (new[] { "normalTexture", "occlusionTexture", "emissiveTexture", "extensions" }.Any(k => material.TryGetProperty(k, out _)))
                        throw new InvalidDataException("Normal, occlusion, emissive and extension materials are not part of the painted 1.12 contract.");
                    if (material.TryGetProperty("emissiveFactor", out var emissive) && emissive.EnumerateArray().Any(v => v.GetDouble() != 0))
                        throw new InvalidDataException("Emissive material factors are not supported.");
                    if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.TryGetProperty("baseColorFactor", out var factor)
                        && factor.EnumerateArray().Any(v => v.GetDouble() != 1))
                        throw new InvalidDataException("Bake the base-color factor into the supplied PNG; the compiler does not silently discard tints.");
                }
        }
        var model = ModelRoot.ReadGLB(new MemoryStream(glb, false));
        if (model.LogicalAnimations.Count != 0 || model.LogicalSkins.Count != 0) throw new InvalidDataException("Attachment GLBs must be rigid and static.");
        if (model.LogicalMaterials.Count > 1) throw new InvalidDataException("Use one material with the supplied shared skin PNG.");
        var pos = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>(); var indices = new List<uint>();
        foreach (var node in model.LogicalNodes.Where(n => n.Mesh is not null))
        {
            var world = node.WorldMatrix;
            if (!Matrix4x4.Invert(world, out var inverse)) throw new InvalidDataException("Singular node transform.");
            var nmat = Matrix4x4.Transpose(inverse); bool flip = world.GetDeterminant() < 0;
            foreach (var prim in node.Mesh!.Primitives)
            {
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES || prim.MorphTargetsCount != 0) throw new InvalidDataException("Only unmorphed triangle primitives are accepted.");
                if (prim.Material is { } mat && (mat.Alpha != AlphaMode.OPAQUE || mat.DoubleSided)) throw new InvalidDataException("Only opaque, single-sided attachment materials are supported.");
                var p = prim.GetVertexAccessor("POSITION")?.AsVector3Array() ?? throw new InvalidDataException("POSITION is required.");
                var n = prim.GetVertexAccessor("NORMAL")?.AsVector3Array() ?? throw new InvalidDataException("NORMAL is required.");
                var u = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array() ?? throw new InvalidDataException("TEXCOORD_0 is required.");
                if (p.Count != n.Count || p.Count != u.Count || pos.Count + p.Count > 65535) throw new InvalidDataException("Invalid vertex attribute counts.");
                uint offset = (uint)pos.Count;
                for (int i = 0; i < p.Count; i++)
                {
                    var pp = Vector3.Transform(p[i], world); var nn = Vector3.TransformNormal(n[i], nmat);
                    if (!Finite(pp) || !Finite(nn) || nn.LengthSquared() < 1e-12f || !float.IsFinite(u[i].X) || !float.IsFinite(u[i].Y))
                        throw new InvalidDataException("Nonfinite position/UV or invalid normal.");
                    if (Math.Max(Math.Abs(pp.X), Math.Max(Math.Abs(pp.Y), Math.Abs(pp.Z))) > 8) throw new InvalidDataException("Attachment exceeds the ±8 WoW-unit safety envelope.");
                    if (u[i].X < 0 || u[i].X > 1 || u[i].Y < 0 || u[i].Y > 1) throw new InvalidDataException("UVs must be within the shared skin [0,1] atlas.");
                    pos.Add(pp); normals.Add(Vector3.Normalize(nn)); uv.Add(u[i]);
                }
                var ix = prim.GetIndices();
                uint[] local = ix is null ? Enumerable.Range(0, p.Count).Select(i => (uint)i).ToArray() : ix.ToArray();
                if (local.Length % 3 != 0 || local.Any(i => i >= p.Count)) throw new InvalidDataException("Invalid triangle indices.");
                for (int i = 0; i < local.Length; i += 3)
                { indices.Add(offset + local[i]); indices.Add(offset + local[i + (flip ? 2 : 1)]); indices.Add(offset + local[i + (flip ? 1 : 2)]); }
            }
        }
        if (pos.Count < 3 || indices.Count == 0 || indices.Count / 3 > MaxAttachmentTriangles)
            throw new InvalidDataException($"Attachment must contain 1–{MaxAttachmentTriangles} triangles.");
        var mesh = new RigidWeaponMesh { Positions = pos.ToArray(), Normals = normals.ToArray(), Uv0 = uv.ToArray(), Indices = indices.ToArray(), Material = new WeaponMaterial() };
        AnalyzeMesh(mesh, asset, AuthoredArmorPackage.Hash(glb), report);
        var audit = EquipmentMeshAudit.Analyze(mesh);
        report.SurfaceAudit[asset] = audit;
        if (audit.InconsistentWindingEdges > 0) report.Error(asset, $"{audit.InconsistentWindingEdges} adjacent welded edges have inconsistent winding.");
        return mesh;
    }

    public static void AnalyzeMesh(RigidWeaponMesh mesh, string asset, string hash, AuthoredArmorReport report)
    {
        var p = mesh.Positions; var uv = mesh.Uv0; var n = mesh.Normals; var ix = mesh.Indices;
        int degenerate = 0, degenerateUv = 0, opposed = 0, duplicate = 0;
        // Weld only for topology measurement: UV seams split vertices but not the physical surface.
        var welded = new Dictionary<(long, long, long), int>(); var w = new int[p.Length];
        for (int i = 0; i < p.Length; i++)
        { var key = ((long)Math.Round(p[i].X * 1e6), (long)Math.Round(p[i].Y * 1e6), (long)Math.Round(p[i].Z * 1e6)); if (!welded.TryGetValue(key, out w[i])) { w[i] = welded.Count; welded.Add(key, w[i]); } }
        var edges = new Dictionary<(int, int), int>(); var faces = new HashSet<(int, int, int)>();
        for (int i = 0; i < ix.Length; i += 3)
        {
            int a = (int)ix[i], b = (int)ix[i + 1], c = (int)ix[i + 2];
            var face = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
            if (face.LengthSquared() < 1e-14f) degenerate++;
            else if (Vector3.Dot(face, n[a] + n[b] + n[c]) <= 0) opposed++;
            var u = uv[b] - uv[a]; var v = uv[c] - uv[a]; if (Math.Abs(u.X * v.Y - u.Y * v.X) < 1e-10f) degenerateUv++;
            int[] f = { w[a], w[b], w[c] }; Array.Sort(f); if (!faces.Add((f[0], f[1], f[2]))) duplicate++;
            Edge(w[a], w[b]); Edge(w[b], w[c]); Edge(w[c], w[a]);
        }
        int boundary = edges.Count(e => e.Value == 1), nonmanifold = edges.Count(e => e.Value > 2);
        if (degenerate + degenerateUv + opposed + duplicate + nonmanifold > 0)
            report.Error(asset, $"Mesh defects: {degenerate} zero-area faces, {degenerateUv} collapsed UV faces, {opposed} opposed normals, {duplicate} duplicate faces, {nonmanifold} nonmanifold edges.");
        if (boundary > 0) report.Warn(asset, $"{boundary} open boundary edges; review intentional helm openings and concealed attachment rims.");
        var min = p.Aggregate(Vector3.Min); var max = p.Aggregate(Vector3.Max);
        report.Meshes.Add(new(asset, hash, p.Length, ix.Length / 3, new[] { min.X, min.Y, min.Z }, new[] { max.X, max.Y, max.Z }, boundary, nonmanifold, degenerate, degenerateUv, opposed, duplicate));
        void Edge(int a, int b) { var key = a < b ? (a, b) : (b, a); edges[key] = edges.GetValueOrDefault(key) + 1; }
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
