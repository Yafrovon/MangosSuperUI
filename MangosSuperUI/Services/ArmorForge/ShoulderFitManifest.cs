using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MangosSuperUI.Services.ArmorForge;

// Contract twin: MSUIClient/Formats/ShoulderFitManifest.cs. Keep behavior identical.
public sealed record ShoulderFitMember(string Path, string Sha256);
public sealed class ShoulderFitManifest
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public string Side { get; set; } = "";
    [JsonRequired] public ShoulderFitMember Default { get; set; } = new("", "");
    [JsonRequired] public ShoulderFitMember Skin { get; set; } = new("", "");
    [JsonRequired] public Dictionary<string, ShoulderFitMember> Variants { get; set; } = new(StringComparer.Ordinal);
}

public sealed record ShoulderFitSelection(string DefaultPath, string Path, string Sha256,
    string BodyCode, bool DeclaredFit, string? ManifestPath, string? ManifestSha256,
    string? SkinPath, string? SkinSha256, [property: JsonIgnore] byte[] Bytes);

/// <summary>Explicit authored model selection only. Never changes a transform or body.</summary>
public static class ShoulderFitResolver
{
    public const string NameMarker = "|MSUI_SHOULDER_FITS_V1";
    public const int MaxManifestBytes = 64 * 1024;
    public static readonly string[] BodyCodes = ["HuM", "HuF", "OrM", "OrF", "DwM", "DwF", "NiM", "NiF", "ScM", "ScF", "TaM", "TaF", "GnM", "GnF", "TrM", "TrF"];
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string CompanionPath(string defaultPath) => defaultPath + ".shoulderfits.json";
    public static string FittedPath(string defaultPath, string bodyCode) => defaultPath[..^3] + "_fit_" + bodyCode + ".m2";
    public static bool RequiresManifest(string internalName) => internalName.EndsWith(NameMarker, StringComparison.Ordinal);
    public static string CacheKey(string dataPath, string folder, string model, string skin, string bodyCode, string side = "") =>
        $"{dataPath}|{folder}|{model}|{skin}|{(folder is "Head" or "Shoulder" ? bodyCode : "")}|{side}";

    public static ShoulderFitSelection Resolve(string defaultPath, byte[] defaultBytes, string internalName,
        string bodyCode, string expectedSkinPath, string expectedSide, Func<string, byte[]?> read)
    {
        string defaultHash = Hash(defaultBytes);
        if (!RequiresManifest(internalName))
        {
            if (internalName.Contains("|MSUI_SHOULDER_FITS_", StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported shoulder-fit model marker.");
            return new(defaultPath, defaultPath, defaultHash, bodyCode, false, null, null, null, null, defaultBytes);
        }
        if (!BodyCodes.Contains(bodyCode, StringComparer.Ordinal)) throw new InvalidDataException("Invalid shoulder-fit body code.");
        string companionPath = CompanionPath(defaultPath);
        byte[] companion = read(companionPath) ?? throw new InvalidDataException($"Required shoulder-fit manifest missing: {companionPath}");
        ShoulderFitManifest manifest = Read(companion);
        if (manifest.Side != expectedSide || !SamePath(manifest.Default.Path, defaultPath) || !SamePath(manifest.Skin.Path, expectedSkinPath))
            throw new InvalidDataException("Shoulder-fit default or shared skin does not match this display.");
        Verify(manifest.Default, defaultBytes);
        Verify(manifest.Skin, read(manifest.Skin.Path));
        byte[] selectedBytes = defaultBytes;
        ShoulderFitMember selected = manifest.Default;
        // Validate the complete declaration before review, including unselected bodies.
        // A partial or stale installation must never appear successfully verified.
        foreach (var (code, member) in manifest.Variants)
        {
            byte[] bytes = read(member.Path) ?? throw new InvalidDataException($"Declared shoulder-fit member missing: {member.Path}");
            Verify(member, bytes);
            if (code == bodyCode) { selected = member; selectedBytes = bytes; }
        }
        return new(defaultPath, selected.Path, selected.Sha256.ToLowerInvariant(), bodyCode,
            manifest.Variants.ContainsKey(bodyCode), companionPath, Hash(companion),
            manifest.Skin.Path, manifest.Skin.Sha256.ToLowerInvariant(), selectedBytes);
    }

    public static ShoulderFitManifest Read(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxManifestBytes) throw new InvalidDataException("Shoulder-fit manifest exceeds its size limit.");
        ShoulderFitManifest m;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            RejectDuplicateProperties(document.RootElement);
            m = JsonSerializer.Deserialize<ShoulderFitManifest>(bytes, JsonOptions) ?? throw new JsonException("Empty manifest.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid shoulder-fit manifest: " + ex.Message, ex); }
        if (m.SchemaVersion != 1 || m.Side is not ("L" or "R") || m.Default is null || m.Skin is null
            || m.Variants is null || m.Variants.Count is < 1 or > 16)
            throw new InvalidDataException("Invalid shoulder-fit version, side, default, shared skin or variant count.");
        ValidateMember(m.Default, ".m2"); ValidateMember(m.Skin, ".blp");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { m.Default.Path };
        foreach (var (code, member) in m.Variants)
        {
            if (!BodyCodes.Contains(code, StringComparer.Ordinal)) throw new InvalidDataException($"Unknown shoulder-fit body: {code}");
            ValidateMember(member, ".m2");
            if (!paths.Add(member.Path)) throw new InvalidDataException("Each declared shoulder fit must have its own model member.");
        }
        return m;
    }

    public static void RejectDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new JsonException($"Duplicate property: {property.Name}");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static bool SamePath(string a, string b) => a.Replace('/', '\\').Equals(b.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    private static void ValidateMember(ShoulderFitMember? member, string extension)
    {
        if (member is null || string.IsNullOrWhiteSpace(member.Path) || member.Path.Length > 200
            || !member.Path.StartsWith(@"Item\ObjectComponents\Shoulder\", StringComparison.OrdinalIgnoreCase)
            || !member.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            || member.Path.Split('\\').Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
            || member.Sha256 is not { Length: 64 } || member.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Unsafe or incomplete shoulder-fit member binding.");
    }
    private static void Verify(ShoulderFitMember member, byte[]? bytes)
    {
        if (bytes is null || bytes.Length is 0 or > 4 * 1024 * 1024 || !Hash(bytes).Equals(member.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Shoulder-fit member missing or SHA-256 mismatch: {member.Path}");
    }
}
