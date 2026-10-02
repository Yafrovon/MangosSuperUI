using System.Numerics;
using System.Security.Cryptography;
using SkiaSharp;

namespace MangosSuperUI.Services.WeaponForge;

/// <summary>Observable properties of the final mesh. This is evidence, never an art or in-game approval.
/// Position welding is used only for edge analysis; authored UV seams/hard normals are not modified.</summary>
public static class EquipmentMeshAudit
{
    public static EquipmentMeshAuditReport Analyze(RigidWeaponMesh mesh, byte[]? texturePng = null)
    {
        var r = new EquipmentMeshAuditReport { VertexCount = mesh.VertexCount, TriangleCount = mesh.TriangleCount };
        using (var stream = new MemoryStream())
        {
            using var writer = new BinaryWriter(stream);
            writer.Write(mesh.Positions.Length);
            foreach (var p in mesh.Positions) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
            foreach (var n in mesh.Normals) { writer.Write(n.X); writer.Write(n.Y); writer.Write(n.Z); }
            foreach (var uv in mesh.Uv0) { writer.Write(uv.X); writer.Write(uv.Y); }
            foreach (uint index in mesh.Indices) writer.Write(index);
            r.MeshSha256 = Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        }
        if (mesh.Positions.Length == 0 || mesh.Indices.Length == 0) r.Errors.Add("Mesh has no surface.");
        if (mesh.Indices.Length % 3 != 0) r.Errors.Add("Index buffer is not a triangle list.");
        if (mesh.Normals.Length != mesh.Positions.Length || mesh.Uv0.Length != mesh.Positions.Length)
            r.Errors.Add("Position, normal and UV arrays have different lengths.");
        if (mesh.Positions.Any(p => !Finite(p)) || mesh.Normals.Any(n => !Finite(n)) ||
            mesh.Uv0.Any(uv => !float.IsFinite(uv.X) || !float.IsFinite(uv.Y)))
            r.Errors.Add("Non-finite position, normal or UV.");
        if (mesh.Indices.Any(i => i >= mesh.Positions.Length)) r.Errors.Add("Triangle index is outside the vertex buffer.");
        if (r.Errors.Count > 0) return r;

        var min = mesh.Positions.Aggregate(Vector3.Min);
        var max = mesh.Positions.Aggregate(Vector3.Max);
        r.BoundsMin = [min.X, min.Y, min.Z]; r.BoundsMax = [max.X, max.Y, max.Z];
        float scale = Math.Max((max - min).Length(), 1e-6f);
        r.PositionWeldTolerance = scale * 1e-6f;
        // Quantized welding is deterministic; report the tolerance rather than claiming exact manifoldness.
        var weld = new Dictionary<(long, long, long), int>();
        int[] ids = new int[mesh.VertexCount];
        for (int i = 0; i < ids.Length; i++)
        {
            var p = (mesh.Positions[i] - min) / r.PositionWeldTolerance;
            var key = ((long)Math.Round(p.X), (long)Math.Round(p.Y), (long)Math.Round(p.Z));
            if (!weld.TryGetValue(key, out int id)) weld[key] = id = weld.Count;
            ids[i] = id;
            float normalLength = mesh.Normals[i].Length();
            if (normalLength < 1e-6f) r.ZeroNormals++;
            else if (Math.Abs(normalLength - 1) > .02f) r.NonUnitNormals++;
            var uv = mesh.Uv0[i];
            if (uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1) r.UvOutsideUnitSquare++;
        }
        r.UniquePositions = weld.Count;
        var parents = Enumerable.Range(0, weld.Count).ToArray();
        int Root(int a) { while (parents[a] != a) { parents[a] = parents[parents[a]]; a = parents[a]; } return a; }
        void Join(int a, int b) { a = Root(a); b = Root(b); if (a != b) parents[b] = a; }
        var edges = new Dictionary<(int, int), (int Count, int Direction)>();
        var triangles = new HashSet<(int, int, int)>();
        var density = new List<double>();
        var referenced = new HashSet<int>();
        void Edge(int a, int b)
        {
            Join(a, b);
            var key = (Math.Min(a, b), Math.Max(a, b));
            var prior = edges.GetValueOrDefault(key);
            edges[key] = (prior.Count + 1, prior.Direction + (a < b ? 1 : -1));
        }
        double areaEpsilon = scale * scale * 1e-12;
        for (int t = 0; t < mesh.Indices.Length; t += 3)
        {
            int a = (int)mesh.Indices[t], b = (int)mesh.Indices[t + 1], c = (int)mesh.Indices[t + 2];
            var cross = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            double area = cross.Length() * .5;
            if (area <= areaEpsilon) { r.DegenerateTriangles++; Issue(r, t / 3, "zero-area"); continue; }
            r.SurfaceArea += area;
            int wa = ids[a], wb = ids[b], wc = ids[c];
            referenced.Add(wa); referenced.Add(wb); referenced.Add(wc);
            int lo = Math.Min(wa, Math.Min(wb, wc)), hi = Math.Max(wa, Math.Max(wb, wc));
            if (!triangles.Add((lo, wa + wb + wc - lo - hi, hi)))
            { r.DuplicateSurfaceTriangles++; Issue(r, t / 3, "duplicate-surface"); }
            Edge(wa, wb); Edge(wb, wc); Edge(wc, wa);
            var n = Vector3.Normalize(cross);
            if (Vector3.Dot(n, mesh.Normals[a]) < -.05f && Vector3.Dot(n, mesh.Normals[b]) < -.05f &&
                Vector3.Dot(n, mesh.Normals[c]) < -.05f)
            { r.NormalsOpposeWinding++; Issue(r, t / 3, "normals-oppose-winding"); }
            var u = mesh.Uv0[b] - mesh.Uv0[a]; var v = mesh.Uv0[c] - mesh.Uv0[a];
            double uvArea = Math.Abs((double)u.X * v.Y - (double)u.Y * v.X) * .5;
            r.SummedUvTriangleArea += uvArea;
            if (uvArea <= 1e-12) { r.DegenerateUvTriangles++; Issue(r, t / 3, "zero-uv-area"); }
            else density.Add(Math.Sqrt(uvArea / area));
            double e0 = Vector3.DistanceSquared(mesh.Positions[a], mesh.Positions[b]);
            double e1 = Vector3.DistanceSquared(mesh.Positions[b], mesh.Positions[c]);
            double e2 = Vector3.DistanceSquared(mesh.Positions[c], mesh.Positions[a]);
            double quality = 4 * Math.Sqrt(3) * area / (e0 + e1 + e2);
            if (quality < .01) r.SliverTriangles++;
        }
        r.ConnectedSurfaceComponents = referenced.Select(Root).Distinct().Count();
        r.UnusedPositions = weld.Count - referenced.Count;
        r.BoundaryEdges = edges.Values.Count(e => e.Count == 1);
        r.NonManifoldEdges = edges.Values.Count(e => e.Count > 2);
        r.InconsistentWindingEdges = edges.Values.Count(e => e.Count == 2 && e.Direction != 0);
        density.Sort();
        if (density.Count > 0)
        {
            r.UvDensityP10 = Quantile(density, .1); r.UvDensityMedian = Quantile(density, .5);
            r.UvDensityP90 = Quantile(density, .9);
        }
        if (r.ZeroNormals > 0) r.Errors.Add($"{r.ZeroNormals} zero normals.");
        if (r.DegenerateTriangles > 0) r.Errors.Add($"{r.DegenerateTriangles} zero-area triangles.");
        if (r.NormalsOpposeWinding > 0) r.ReviewNotes.Add("Some face normals oppose their winding; inspect face orientation.");
        if (r.BoundaryEdges > 0) r.ReviewNotes.Add("Open boundaries: inspect for unintended holes; open armor edges may be intentional.");
        if (r.NonManifoldEdges > 0 || r.DuplicateSurfaceTriangles > 0)
            r.ReviewNotes.Add("Layered/coincident geometry needs review; this may be intentional material passes.");
        if (r.InconsistentWindingEdges > 0) r.ReviewNotes.Add("Adjacent faces have inconsistent winding at welded edges.");
        if (r.DegenerateUvTriangles > 0) r.ReviewNotes.Add("Some faces sample a UV line/point; review stretching and detail loss.");
        if (r.ConnectedSurfaceComponents > 1) r.ReviewNotes.Add("Multiple surface components: inspect component placement and connections.");
        if (texturePng is { Length: > 0 }) r.Texture = AnalyzeTexture(texturePng);
        return r;
    }

    public static EquipmentTextureAudit AnalyzeTexture(byte[] png)
    {
        var r = new EquipmentTextureAudit { Sha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() };
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null) { r.Error = "Texture could not be decoded."; return r; }
        r.Width = bitmap.Width; r.Height = bitmap.Height;
        var histogram = new int[256]; long visible = 0, transparent = 0, dark = 0, bright = 0;
        double sum = 0, sumSquared = 0;
        foreach (var p in bitmap.Pixels)
        {
            if (p.Alpha < 128) { transparent++; continue; }
            // Display-code luma, deliberately not physical lighting or a gamma-linear luminance claim.
            double y = (.2126 * p.Red + .7152 * p.Green + .0722 * p.Blue) / 255;
            histogram[Math.Clamp((int)Math.Round(y * 255), 0, 255)]++;
            visible++; sum += y; sumSquared += y * y;
            if (y < .08) dark++; if (y > .95) bright++;
        }
        r.TransparentFraction = transparent / (double)(bitmap.Width * bitmap.Height);
        if (visible > 0)
        {
            r.MeanLuma = sum / visible;
            r.LumaStdDev = Math.Sqrt(Math.Max(0, sumSquared / visible - r.MeanLuma * r.MeanLuma));
            r.NearBlackFraction = dark / (double)visible; r.NearWhiteFraction = bright / (double)visible;
        }
        r.LumaHistogram = histogram;
        return r;
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static double Quantile(List<double> sorted, double q)
    {
        double index = (sorted.Count - 1) * q; int lo = (int)index;
        return sorted[lo] + (sorted[Math.Min(lo + 1, sorted.Count - 1)] - sorted[lo]) * (index - lo);
    }
    private static void Issue(EquipmentMeshAuditReport r, int triangle, string code)
    { if (r.TriangleIssues.Count < 100) r.TriangleIssues.Add(new(triangle, code)); }
}

public sealed class EquipmentMeshAuditReport
{
    public int SchemaVersion => 1;
    public string Stage => "mesh-analysis";
    public bool RuntimeVerified => false;
    public string Limits => "No self-intersection, UV-overlap, attachment, animation or artistic-coherence approval. UV area is summed, not occupied atlas coverage. Edge analysis uses quantized position welding.";
    public string MeshSha256 { get; set; } = "";
    public int VertexCount { get; set; }
    public int TriangleCount { get; set; }
    public int UniquePositions { get; set; }
    public float[] BoundsMin { get; set; } = [];
    public float[] BoundsMax { get; set; } = [];
    public float PositionWeldTolerance { get; set; }
    public int ZeroNormals { get; set; }
    public int NonUnitNormals { get; set; }
    public int UvOutsideUnitSquare { get; set; }
    public int DegenerateTriangles { get; set; }
    public int DuplicateSurfaceTriangles { get; set; }
    public int DegenerateUvTriangles { get; set; }
    public int NormalsOpposeWinding { get; set; }
    public int SliverTriangles { get; set; }
    public int ConnectedSurfaceComponents { get; set; }
    public int UnusedPositions { get; set; }
    public int BoundaryEdges { get; set; }
    public int NonManifoldEdges { get; set; }
    public int InconsistentWindingEdges { get; set; }
    public double SurfaceArea { get; set; }
    public double SummedUvTriangleArea { get; set; }
    public double UvDensityP10 { get; set; }
    public double UvDensityMedian { get; set; }
    public double UvDensityP90 { get; set; }
    public EquipmentTextureAudit? Texture { get; set; }
    public List<string> Errors { get; } = [];
    public List<string> ReviewNotes { get; } = [];
    public List<EquipmentTriangleIssue> TriangleIssues { get; } = [];
}

public sealed record EquipmentTriangleIssue(int Triangle, string Code);
public sealed class EquipmentTextureAudit
{
    public string Sha256 { get; set; } = "";
    public string? Error { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double MeanLuma { get; set; }
    public double LumaStdDev { get; set; }
    public double NearBlackFraction { get; set; }
    public double NearWhiteFraction { get; set; }
    public double TransparentFraction { get; set; }
    public int[] LumaHistogram { get; set; } = [];
}
