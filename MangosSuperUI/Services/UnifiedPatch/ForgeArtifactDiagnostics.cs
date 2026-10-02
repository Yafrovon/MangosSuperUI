using System.Security.Cryptography;
using MangosSuperUI.Services.ArmorForge;
using MangosSuperUI.Services.Mpq;

namespace MangosSuperUI.Services.UnifiedPatch;

/// <summary>Read-only comparisons of exact on-disk artifacts, never the mounted archive cache.</summary>
public static class ForgeArtifactDiagnostics
{
    public static ForgeFileComparison CompareFiles(string referencePath, string targetPath)
    {
        var result = new ForgeFileComparison { ReferencePath = referencePath, TargetPath = targetPath };
        try
        {
            var reference = ReadStableFile(referencePath);
            result.ExpectedSha256 = Sha256(reference);
            if (!File.Exists(targetPath))
            {
                result.State = "missing-target";
                result.Message = $"configured client Data copy is missing: {targetPath}; reference: {referencePath}";
                return result;
            }
            var target = ReadStableFile(targetPath);
            result.ActualSha256 = Sha256(target);
            result.WrittenUtc = File.GetLastWriteTimeUtc(targetPath);
            result.State = reference.AsSpan().SequenceEqual(target) ? "match" : "different";
            result.Message = $"configured client Data copy {(result.State == "match" ? "matches" : "differs from")} the unified build: {targetPath}; reference: {referencePath}. This does not inspect other clients.";
        }
        catch (Exception ex) { result.Message = $"configured client Data copy comparison unavailable: {ex.Message}"; }
        return result;
    }

    public static ForgeFileComparison CompareServerItemSet(string unifiedPatchPath, string targetPath)
    {
        var result = new ForgeFileComparison
        {
            ReferencePath = unifiedPatchPath,
            ReferenceMember = ArmorNaming.ItemSetMember,
            TargetPath = targetPath,
        };
        try
        {
            var before = Stamp(unifiedPatchPath);
            using var archive = MpqArchive.Open(unifiedPatchPath)
                ?? throw new InvalidDataException("unified patch is not a readable MPQ");
            var reference = archive.ReadFile(ArmorNaming.ItemSetMember)
                ?? throw new InvalidDataException("unified patch has no ItemSet.dbc member; server equality is not established");
            if (before != Stamp(unifiedPatchPath)) throw new IOException("unified patch changed during comparison");
            ValidateItemSet(reference);
            result.ExpectedSha256 = Sha256(reference);
            if (!File.Exists(targetPath))
            {
                result.State = "missing-target";
                result.Message = $"server ItemSet.dbc is missing: {targetPath}; reference: {unifiedPatchPath} :: {ArmorNaming.ItemSetMember}";
                return result;
            }
            var target = ReadStableFile(targetPath);
            ValidateItemSet(target);
            result.ActualSha256 = Sha256(target);
            result.WrittenUtc = File.GetLastWriteTimeUtc(targetPath);
            result.State = reference.AsSpan().SequenceEqual(target) ? "match" : "different";
            result.Message = $"server ItemSet.dbc {(result.State == "match" ? "matches" : "differs from")} the current unified patch member: {targetPath}; reference: {unifiedPatchPath} :: {ArmorNaming.ItemSetMember}. Core startup time is checked separately.";
        }
        catch (Exception ex) { result.Message = $"server ItemSet.dbc comparison unavailable: {ex.Message}"; }
        return result;
    }

    private static (long Length, DateTime WrittenUtc) Stamp(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static byte[] ReadStableFile(string path)
    {
        var before = Stamp(path);
        var bytes = File.ReadAllBytes(path);
        if (before != Stamp(path) || bytes.LongLength != before.Length)
            throw new IOException("artifact changed during comparison: " + path);
        return bytes;
    }

    private static void ValidateItemSet(byte[] bytes)
    {
        if (bytes.Length < 21 || !bytes.AsSpan(0, 4).SequenceEqual("WDBC"u8)
            || BitConverter.ToInt32(bytes, 4) < 0
            || BitConverter.ToInt32(bytes, 8) != ArmorItemSetDbc.FieldCount
            || BitConverter.ToInt32(bytes, 12) != ArmorItemSetDbc.RecordSize
            || BitConverter.ToInt32(bytes, 16) < 1
            || 20L + (long)BitConverter.ToInt32(bytes, 4) * ArmorItemSetDbc.RecordSize + BitConverter.ToInt32(bytes, 16) != bytes.LongLength
            || bytes[^1] != 0)
            throw new InvalidDataException("ItemSet.dbc does not have the complete 1.12 layout");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class ForgeFileComparison
{
    public string State { get; set; } = "unavailable";
    public bool ComparisonKnown => State is "match" or "different" or "missing-target";
    public bool Stale => State is "different" or "missing-target";
    public string ReferencePath { get; init; } = "";
    public string? ReferenceMember { get; init; }
    public string TargetPath { get; init; } = "";
    public string? ExpectedSha256 { get; set; }
    public string? ActualSha256 { get; set; }
    public DateTime? WrittenUtc { get; set; }
    public string Message { get; set; } = "";
}
