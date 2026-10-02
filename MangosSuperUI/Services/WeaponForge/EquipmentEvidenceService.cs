using System.Security.Cryptography;
using System.Text.Json;

namespace MangosSuperUI.Services.WeaponForge;

/// <summary>Checks supplied offline capture claims against current compiled registry bytes.
/// It neither authenticates a capture run nor approves appearance or live-world behavior.</summary>
public static class EquipmentEvidenceService
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumAssets = 1024;
    public const string EvidenceKind = "offline-production-character-renderer";
    private const long MaximumComparedBytes = 128L * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static EquipmentEvidenceReport Analyze(byte[] bytes, Func<string, byte[]?> weapon,
        Func<string, byte[]?> armor, CancellationToken cancellationToken = default)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw new InvalidDataException("Evidence JSON must be 1 byte to 2 MiB.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        Fields(root, ["schemaVersion", "summary", "assets"]);
        if (Integer(root, "schemaVersion", 1, 1) != 1) throw new InvalidDataException("Unsupported evidence schema.");
        var summary = Required(root, "summary", JsonValueKind.Object);
        Fields(summary, ["evidenceKind", "inWorldVerified", "visualReviewRequired", "generatedUtc", "captureComplete",
            "requested", "completed", "technicalErrorCases", "error", "fullVanillaBodyMatrix", "limitations"]);
        if (Text(summary, "evidenceKind", 80) != EvidenceKind) throw new InvalidDataException("Only offline-production-character-renderer evidence is supported.");
        if (Boolean(summary, "inWorldVerified")) throw new InvalidDataException("In-world verification claims are not accepted by this offline evidence lane.");
        if (!Boolean(summary, "visualReviewRequired")) throw new InvalidDataException("Native capture evidence must retain visualReviewRequired=true.");
        if (!DateTimeOffset.TryParse(Text(summary, "generatedUtc", 64), out _)) throw new InvalidDataException("generatedUtc must be a date/time.");
        int requested = Integer(summary, "requested", 1, 100_000);
        int completed = Integer(summary, "completed", 0, requested);
        int errors = Integer(summary, "technicalErrorCases", 0, completed);
        bool complete = Boolean(summary, "captureComplete"), fullMatrix = Boolean(summary, "fullVanillaBodyMatrix");
        if (complete && completed != requested) throw new InvalidDataException("A complete claim must have completed=requested.");
        NullableText(summary, "error", 4096);
        var limitations = Required(summary, "limitations", JsonValueKind.Array);
        if (limitations.GetArrayLength() is < 1 or > 16) throw new InvalidDataException("limitations must contain 1..16 strings.");
        foreach (var item in limitations.EnumerateArray()) StringValue(item, "limitation", 1024);

        var assets = Required(root, "assets", JsonValueKind.Array);
        if (assets.GetArrayLength() is < 1 or > MaximumAssets) throw new InvalidDataException("Evidence needs 1..1024 assets.");
        var inputs = new List<AssetClaim>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate the entire document before any registry lookup.
        foreach (var asset in assets.EnumerateArray())
        {
            Fields(asset, ["path", "supplier", "byteLength", "sha256", "error"]);
            string path = MemberPath(Text(asset, "path", 260));
            if (!paths.Add(path)) throw new InvalidDataException($"Duplicate asset path: {path}");
            string? error = NullableText(asset, "error", 1024);
            string? supplier = NullableText(asset, "supplier", 1024);
            if (error is not null)
            {
                if (asset.TryGetProperty("sha256", out _) || asset.TryGetProperty("byteLength", out _))
                    throw new InvalidDataException("A missing-asset claim cannot also contain byte/hash claims.");
                inputs.Add(new(path, supplier, null, null, error));
                continue;
            }
            string hash = Text(asset, "sha256", 64).ToLowerInvariant();
            if (!IsHash(hash)) throw new InvalidDataException($"Invalid SHA-256 for {path}.");
            int length = Integer(asset, "byteLength", 1, 64 * 1024 * 1024);
            inputs.Add(new(path, supplier, length, hash, null));
        }

        var members = new List<EquipmentEvidenceMember>();
        long compared = 0;
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Path.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase))
            {
                members.Add(new(input.Path, "not-checked", "DBC bytes are not exposed by the compiled equipment registry resolvers.",
                    null, input.Sha256, null, input.ByteLength, null, input.Supplier, input.Error));
                continue;
            }
            byte[]? current = weapon(input.Path);
            string? registry = current is null ? null : "weapon";
            if (current is null) { current = armor(input.Path); if (current is not null) registry = "armor"; }
            compared += current?.LongLength ?? 0;
            if (compared > MaximumComparedBytes) throw new InvalidDataException("Resolved registry members exceed the 128 MiB comparison limit.");
            string? actual = current is null ? null : Hash(current);
            string status, reason;
            if (input.Error is not null) { status = "missing"; reason = "The submitted client report claims this asset was missing; a capture/hash match cannot be established."; }
            else if (current is null) { status = "missing"; reason = "No member resolved from the current compiled equipment registries. This may be a stock dependency or an unavailable registry lookup; it does not prove the client lacks the asset."; }
            else if (input.Sha256 == actual && input.ByteLength == current.Length) { status = "matched"; reason = "Submitted SHA-256 and byte length match the compiled registry member."; }
            else { status = "mismatch"; reason = "Submitted SHA-256 or byte length differs from the compiled registry member."; }
            members.Add(new(input.Path, status, reason, registry, input.Sha256, actual, input.ByteLength, current?.Length, input.Supplier, input.Error));
        }
        return new(Hash(bytes), DateTimeOffset.UtcNow, new(requested, completed, errors, complete, fullMatrix), summary.Clone(),
            members.Where(m => m.Status == "matched").ToArray(), members.Where(m => m.Status == "missing").ToArray(),
            members.Where(m => m.Status == "mismatch").ToArray(), members.Where(m => m.Status == "not-checked").ToArray());
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string MemberPath(string path)
    {
        path = path.Replace('/', '\\');
        if (path.StartsWith('\\') || path.Any(c => char.IsControl(c) || c == ':') ||
            path.Split('\\').Any(p => p is "" or "." or "..") ||
            !(path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
              path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Asset paths must be relative MPQ M2/MDX/BLP/DBC member names without traversal.");
        return path;
    }
    private static void Fields(JsonElement item, string[] allowed)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a JSON object.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in item.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"Unknown or duplicate evidence field: {property.Name}");
    }
    private static JsonElement Required(JsonElement item, string name, JsonValueKind kind)
        => item.TryGetProperty(name, out var value) && value.ValueKind == kind ? value : throw new InvalidDataException($"Missing or invalid {name}.");
    private static string StringValue(JsonElement value, string name, int max)
        => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= max
            ? text : throw new InvalidDataException($"{name} must be a nonempty string of at most {max} characters.");
    private static string Text(JsonElement item, string name, int max) => StringValue(Required(item, name, JsonValueKind.String), name, max);
    private static string? NullableText(JsonElement item, string name, int max)
        => !item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : StringValue(value, name, max);
    private static int Integer(JsonElement item, string name, int min, int max)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= min && number <= max
            ? number : throw new InvalidDataException($"{name} must be an integer from {min} to {max}.");
    private static bool Boolean(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new InvalidDataException($"{name} must be a boolean.");
    private sealed record AssetClaim(string Path, string? Supplier, int? ByteLength, string? Sha256, string? Error);
}

public sealed record EquipmentEvidenceCounts(int Requested, int Completed, int TechnicalErrorCases, bool CaptureComplete, bool FullVanillaBodyMatrix);
public sealed record EquipmentEvidenceMember(string Path, string Status, string Reason, string? Registry,
    string? ClaimedSha256, string? RegistrySha256, int? ClaimedByteLength, int? RegistryByteLength,
    string? UntrustedSupplierClaim, string? UntrustedClientError);
public sealed record EquipmentEvidenceReport(string InputSha256, DateTimeOffset CheckedAtUtc,
    EquipmentEvidenceCounts UntrustedClaimedCounts, JsonElement UntrustedSummary,
    IReadOnlyList<EquipmentEvidenceMember> Matched, IReadOnlyList<EquipmentEvidenceMember> Missing,
    IReadOnlyList<EquipmentEvidenceMember> Mismatch, IReadOnlyList<EquipmentEvidenceMember> NotChecked)
{
    public int SchemaVersion => 1;
    public string EvidenceKind => EquipmentEvidenceService.EvidenceKind;
    public bool RuntimeVerified => false;
    public bool InWorldVerified => false;
    public bool VisualReviewRequired => true;
    public string Scope => "Only supplied hash/length claims are checked against current compiled weapon/armor registry members (resolver caches may be up to 30 seconds old). Counts, capture execution, image contents, supplier paths and coverage are untrusted claims. This is offline evidence, not live-world verification or artistic approval; matches do not prove that the installed client patch contains or used these bytes.";
}
