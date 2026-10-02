using System.Text;
using MangosSuperUI.Services.ArmorForge;
using MangosSuperUI.Services.Mpq;
using MangosSuperUI.Services.UnifiedPatch;
using Xunit;

namespace MangosSuperUI.Tests;

public sealed class ForgeArtifactDiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forge-status-test-" + Guid.NewGuid());
    public ForgeArtifactDiagnosticsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);
    private string Put(string name, byte[] data)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }
    private static byte[] ItemSet(uint id)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("WDBC"u8); writer.Write(1); writer.Write(45); writer.Write(180); writer.Write(1);
        writer.Write(id); writer.Write(new byte[176]); writer.Write((byte)0);
        return stream.ToArray();
    }
    private string Archive(byte[] data, string member = ArmorNaming.ItemSetMember) =>
        Put("unified.MPQ", MpqArchiveWriter.Build(new[] { KeyValuePair.Create(member, data) }));

    [Fact]
    public void CurrentUnifiedMemberWinsOverStaleLegacySidecar()
    {
        var actual = ItemSet(5199);
        Put("ItemSet.dbc", ItemSet(5100)); // Legacy sidecar must never enter the comparison.
        var archive = Archive(actual);
        var target = Put("server.dbc", actual);
        var beforeArchive = File.ReadAllBytes(archive);
        var beforeSidecar = File.ReadAllBytes(Path.Combine(_dir, "ItemSet.dbc"));
        var result = ForgeArtifactDiagnostics.CompareServerItemSet(archive, target);
        Assert.Equal("match", result.State);
        Assert.True(result.ComparisonKnown); Assert.False(result.Stale);
        Assert.Equal(result.ExpectedSha256, result.ActualSha256);
        Assert.Equal(archive, result.ReferencePath); Assert.Equal(ArmorNaming.ItemSetMember, result.ReferenceMember);
        Assert.Equal(beforeArchive, File.ReadAllBytes(archive));
        Assert.Equal(beforeSidecar, File.ReadAllBytes(Path.Combine(_dir, "ItemSet.dbc")));
    }

    [Fact]
    public void ValidDifferentServerTableIsStale()
    {
        var result = ForgeArtifactDiagnostics.CompareServerItemSet(Archive(ItemSet(5199)), Put("server.dbc", ItemSet(5198)));
        Assert.Equal("different", result.State); Assert.True(result.ComparisonKnown); Assert.True(result.Stale);
        Assert.NotEqual(result.ExpectedSha256, result.ActualSha256);
        Assert.DoesNotContain("Rebuild", result.Message);
    }

    [Fact]
    public void MissingServerTableIsKnownMissingOnlyAfterReferenceValidation()
    {
        var target = Path.Combine(_dir, "missing.dbc");
        var result = ForgeArtifactDiagnostics.CompareServerItemSet(Archive(ItemSet(5199)), target);
        Assert.Equal("missing-target", result.State); Assert.True(result.Stale); Assert.Null(result.WrittenUtc);
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-mpq")]
    [InlineData("missing-member")]
    [InlineData("invalid-dbc")]
    [InlineData("truncated-dbc")]
    [InlineData("wrong-layout")]
    public void InvalidReferenceCannotClaimHealthyOrStale(string failure)
    {
        var archive = Path.Combine(_dir, "absent.MPQ");
        if (failure == "invalid-mpq") archive = Put("invalid.MPQ", new byte[64]);
        if (failure == "missing-member") archive = Archive(ItemSet(5199), "other.dbc");
        if (failure == "invalid-dbc") archive = Archive(new byte[64]);
        if (failure == "truncated-dbc") archive = Archive(ItemSet(5199)[..^1]);
        if (failure == "wrong-layout") { var data = ItemSet(5199); data[8] = 44; archive = Archive(data); }
        Put("ItemSet.dbc", ItemSet(5199)); // Matching legacy sidecar still cannot rescue an unknown reference.
        var result = ForgeArtifactDiagnostics.CompareServerItemSet(archive, Put("server.dbc", ItemSet(5199)));
        Assert.Equal("unavailable", result.State); Assert.False(result.ComparisonKnown); Assert.False(result.Stale);
        Assert.Null(result.WrittenUtc);
    }

    [Fact]
    public void CorruptServerTableDoesNotBecomeHealthy()
    {
        var result = ForgeArtifactDiagnostics.CompareServerItemSet(Archive(ItemSet(5199)), Put("server.dbc", new byte[8]));
        Assert.False(result.ComparisonKnown); Assert.Equal("unavailable", result.State);
    }

    [Fact]
    public void LockedServerReadDoesNotBecomeHealthy()
    {
        var archive = Archive(ItemSet(5199)); var target = Put("server.dbc", ItemSet(5199));
        using var locked = File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = ForgeArtifactDiagnostics.CompareServerItemSet(archive, target);
        Assert.False(result.ComparisonKnown); Assert.Equal("unavailable", result.State);
    }

    [Fact]
    public void FileEpochIsRetainedIndependentlyOfEquality()
    {
        var target = Put("server.dbc", ItemSet(5199));
        var epoch = new DateTime(2026, 9, 29, 1, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, epoch);
        var result = ForgeArtifactDiagnostics.CompareServerItemSet(Archive(ItemSet(5199)), target);
        Assert.Equal("match", result.State); Assert.Equal(epoch, result.WrittenUtc);
    }

    [Theory]
    [InlineData("same", "match", true, false)]
    [InlineData("different", "different", true, true)]
    [InlineData("missing-target", "missing-target", true, true)]
    [InlineData("missing-reference", "unavailable", false, false)]
    [InlineData("locked", "unavailable", false, false)]
    public void ConfiguredCopyIsScopedAndUnknownNeverMatches(string scenario, string state, bool known, bool stale)
    {
        var reference = Put("reference.MPQ", [1, 2, 3]);
        var target = Put("configured.MPQ", scenario == "different" ? [3, 2, 1] : [1, 2, 3]);
        if (scenario == "missing-target") File.Delete(target);
        if (scenario == "missing-reference") File.Delete(reference);
        using var locked = scenario == "locked" ? File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var result = ForgeArtifactDiagnostics.CompareFiles(reference, target);
        Assert.Equal(state, result.State); Assert.Equal(known, result.ComparisonKnown); Assert.Equal(stale, result.Stale);
        Assert.Contains("configured client Data copy", result.Message);
        Assert.DoesNotContain("probably", result.Message); Assert.DoesNotContain("Rebuild", result.Message);
    }
}
