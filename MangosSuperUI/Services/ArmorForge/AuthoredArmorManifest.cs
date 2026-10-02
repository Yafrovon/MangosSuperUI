using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MangosSuperUI.Services.WeaponForge;

namespace MangosSuperUI.Services.ArmorForge;

/// <summary>Original art package. Coordinates are glTF Y-up, with one unit equal to one WoW unit.
/// Provenance is the author's declaration; accepting it is not proof of originality or runtime fit.</summary>
public sealed class AuthoredArmorManifest
{
    public int SchemaVersion { get; set; }
    public string Name { get; set; } = "";
    public string Material { get; set; } = "";
    public string Author { get; set; } = "";
    public string DesignNotes { get; set; } = "";
    public List<AuthoredArmorPiece> Pieces { get; set; } = new();
}

public sealed class AuthoredArmorPiece
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string IconPng { get; set; } = "";
    public string? SkinPng { get; set; }
    public int[] GeosetGroup { get; set; } = new int[3];
    public uint[] HelmetVis { get; set; } = new uint[2];
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, string>> ShoulderFits { get; set; } = new(StringComparer.Ordinal);
    public List<AuthoredArmorComponent> Components { get; set; } = new();
}

public sealed class AuthoredArmorComponent
{
    public int Slot { get; set; }
    public string Gender { get; set; } = "_U";
    public string Png { get; set; } = "";
}

public sealed record AuthoredArmorIssue(string Severity, string Asset, string Message);
public sealed record AuthoredArmorTextureMetric(string Asset, string Sha256, int Width, int Height,
    double MeanLuminance, double LuminanceDeviation, double OpaqueFraction, double ClippedDarkFraction, double ClippedLightFraction);
public sealed record AuthoredArmorMeshMetric(string Asset, string Sha256, int Vertices, int Triangles,
    float[] Minimum, float[] Maximum, int BoundaryEdges, int NonManifoldEdges, int DegenerateFaces,
    int DegenerateUvFaces, int OpposedNormals, int DuplicateFaces);

public sealed class AuthoredArmorReport
{
    public string PackageSha256 { get; set; } = "";
    public string Name { get; set; } = "";
    public string Material { get; set; } = "";
    public string RuntimeStatus { get; } = "unverified";
    public string RuntimeRequirement { get; } = "Compiled art is not proof of fit. Verify all 16 race/gender bodies, front/back/both sides, and idle/run/attack poses in MSUIClient using the compiled package hash.";
    public List<AuthoredArmorIssue> Issues { get; } = new();
    public List<AuthoredArmorTextureMetric> Textures { get; } = new();
    public List<AuthoredArmorMeshMetric> Meshes { get; } = new();
    public Dictionary<string, EquipmentMeshAuditReport> SurfaceAudit { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Valid => !Issues.Any(i => i.Severity == "error");
    public bool Compiled { get; set; }
    public Dictionary<string, string> CompiledSha256 { get; } = new(StringComparer.OrdinalIgnoreCase);
    public void Error(string asset, string message) => Issues.Add(new("error", asset, message));
    public void Warn(string asset, string message) => Issues.Add(new("warning", asset, message));
}

public sealed class AuthoredArmorPackage
{
    public const int MaxZipBytes = 32 * 1024 * 1024;
    public const int MaxExpandedBytes = 64 * 1024 * 1024;
    public const int MaxMemberBytes = 4 * 1024 * 1024;
    public const int MaxMembers = 128;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public required AuthoredArmorManifest Manifest { get; init; }
    public required IReadOnlyDictionary<string, byte[]> Assets { get; init; }
    public required string Sha256 { get; init; }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static bool SafePath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 160
        && !path.Contains('\\') && !path.Contains(':') && !path.StartsWith('/')
        && path.Split('/').All(p => p.Length > 0 && p != "." && p != ".." && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'));

    /// <summary>Never extracts user paths to disk. Enforces expanded limits before allocating each member.</summary>
    public static AuthoredArmorPackage Read(byte[] zip)
    {
        if (zip.Length == 0 || zip.Length > MaxZipBytes) throw new InvalidDataException("ZIP must be 1 byte to 32 MiB.");
        using var stream = new MemoryStream(zip, false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxMembers) throw new InvalidDataException("ZIP exceeds 128 members.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (!SafePath(entry.FullName)) throw new InvalidDataException($"Unsafe ZIP member path: {entry.FullName}");
            if (entry.Length <= 0 || entry.Length > MaxMemberBytes || (total += entry.Length) > MaxExpandedBytes)
                throw new InvalidDataException("ZIP exceeds the member (4 MiB) or expanded (64 MiB) limit, or has an empty member.");
            if (files.ContainsKey(entry.FullName)) throw new InvalidDataException($"Duplicate ZIP member: {entry.FullName}");
            using var input = entry.Open();
            var bytes = new byte[checked((int)entry.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new InvalidDataException("ZIP member length mismatch.");
            files.Add(entry.FullName, bytes);
        }
        if (!files.Remove("manifest.json", out var manifestBytes) || manifestBytes.Length > 64 * 1024)
            throw new InvalidDataException("A root manifest.json of at most 64 KiB is required.");
        AuthoredArmorManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(manifestBytes);
            ShoulderFitResolver.RejectDuplicateProperties(document.RootElement);
            manifest = JsonSerializer.Deserialize<AuthoredArmorManifest>(manifestBytes, JsonOptions) ?? throw new JsonException("Empty manifest.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid manifest: " + ex.Message); }
        return new() { Manifest = manifest, Assets = files, Sha256 = Hash(zip) };
    }
}
