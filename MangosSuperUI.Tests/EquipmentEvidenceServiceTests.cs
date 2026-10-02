using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MangosSuperUI.Services.WeaponForge;
using Xunit;

namespace MangosSuperUI.Tests;

public sealed class EquipmentEvidenceServiceTests
{
    private static readonly byte[] Compiled = [1, 2, 3, 4];
    private const string Member = "Item\\ObjectComponents\\Weapon\\SUI_W_1.m2";
    private static JsonObject Input() => new()
    {
        ["schemaVersion"] = 1,
        ["summary"] = new JsonObject
        {
            ["evidenceKind"] = EquipmentEvidenceService.EvidenceKind, ["inWorldVerified"] = false,
            ["visualReviewRequired"] = true, ["generatedUtc"] = "2026-09-28T10:00:00Z",
            ["captureComplete"] = true, ["requested"] = 10, ["completed"] = 10,
            ["technicalErrorCases"] = 0, ["error"] = null, ["fullVanillaBodyMatrix"] = false,
            ["limitations"] = new JsonArray("Offline only")
        },
        ["assets"] = new JsonArray(new JsonObject { ["path"] = Member, ["supplier"] = "claimed-patch.MPQ",
            ["byteLength"] = Compiled.Length, ["sha256"] = EquipmentEvidenceService.Hash(Compiled) })
    };
    private static EquipmentEvidenceReport Analyze(JsonObject value, Func<string, byte[]?>? weapon = null,
        Func<string, byte[]?>? armor = null)
        => EquipmentEvidenceService.Analyze(Encoding.UTF8.GetBytes(value.ToJsonString()), weapon ?? (_ => null), armor ?? (_ => null));

    [Fact]
    public void MatchingKnownCompiledBytesVerifiesOnlyHashAndLength()
    {
        var report = Analyze(Input(), path => path == Member ? Compiled : null);
        Assert.Single(report.Matched); Assert.Empty(report.Mismatch); Assert.Empty(report.Missing);
        Assert.Equal("weapon", report.Matched[0].Registry);
        Assert.Equal(10, report.UntrustedClaimedCounts.Completed);
        Assert.False(report.RuntimeVerified); Assert.False(report.InWorldVerified); Assert.True(report.VisualReviewRequired);
        Assert.Equal(EquipmentEvidenceService.Hash(Compiled), report.Matched[0].RegistrySha256);
    }

    [Fact]
    public void ArmorResolverCanMatchWithoutWeaponData()
    {
        var report = Analyze(Input(), armor: _ => Compiled);
        Assert.Equal("armor", Assert.Single(report.Matched).Registry);
    }

    [Fact]
    public void ChangedCompiledBytesAreAMismatchAndRetainBothHashes()
    {
        var report = Analyze(Input(), _ => [1, 2, 3, 5]);
        var member = Assert.Single(report.Mismatch);
        Assert.NotEqual(member.ClaimedSha256, member.RegistrySha256); Assert.Empty(report.Matched);
    }

    [Fact]
    public void IncorrectClaimedLengthCannotPassEvenWhenHashMatches()
    {
        var input = Input(); input["assets"]![0]!["byteLength"] = 7;
        Assert.Single(Analyze(input, _ => Compiled).Mismatch);
    }

    [Fact]
    public void UnresolvedStockDependencyIsExplicitlyNotProofOfClientFailure()
    {
        var report = Analyze(Input());
        Assert.Contains("may be a stock dependency", Assert.Single(report.Missing).Reason);
        Assert.False(report.RuntimeVerified);
    }

    [Fact]
    public void DbcIsNotComparedWithEquipmentRegistry()
    {
        var input = Input(); input["assets"]![0]!["path"] = "DBFilesClient\\ItemDisplayInfo.dbc";
        var result = Analyze(input, _ => throw new Exception("must not query DBC"));
        Assert.Single(result.NotChecked); Assert.Empty(result.Matched);
    }

    [Theory]
    [InlineData("evidenceKind", "live-world")]
    [InlineData("completed", "ten")]
    public void UnknownKindOrMalformedCountsAreRejected(string field, string value)
    {
        var input = Input(); input["summary"]![field] = value;
        Assert.Throws<InvalidDataException>(() => Analyze(input));
    }

    [Fact]
    public void LiveWorldAndHiddenRuntimeApprovalClaimsAreRejected()
    {
        var input = Input(); input["summary"]!["inWorldVerified"] = true;
        Assert.Throws<InvalidDataException>(() => Analyze(input));
        input = Input(); input["summary"]!["runtimeVerified"] = true;
        Assert.Throws<InvalidDataException>(() => Analyze(input));
    }

    [Fact]
    public void ImpossibleCountsAndTooManyAssetsAreRejectedBeforeLookup()
    {
        var input = Input(); input["summary"]!["completed"] = 11;
        Assert.Throws<InvalidDataException>(() => Analyze(input));
        input = Input(); var assets = input["assets"]!.AsArray();
        for (int i = 0; i < EquipmentEvidenceService.MaximumAssets; i++) assets.Add(assets[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => Analyze(input, _ => throw new Exception("must not resolve invalid input")));
    }

    [Theory]
    [InlineData("../private.m2")]
    [InlineData("C:\\private.m2")]
    [InlineData("/Item/model.m2")]
    [InlineData("Item\\model.exe")]
    public void InvalidMemberPathsAreRejected(string path)
    {
        var input = Input(); input["assets"]![0]!["path"] = path;
        Assert.Throws<InvalidDataException>(() => Analyze(input));
    }

    [Fact]
    public void CaseInsensitiveDuplicatePathsAreRejected()
    {
        var input = Input(); var duplicate = input["assets"]![0]!.DeepClone(); duplicate["path"] = Member.ToUpperInvariant();
        input["assets"]!.AsArray().Add(duplicate);
        Assert.Throws<InvalidDataException>(() => Analyze(input));
    }

    [Fact]
    public void MalformedJsonAndOversizeContentAreRejected()
    {
        Assert.ThrowsAny<JsonException>(() => EquipmentEvidenceService.Analyze(Encoding.UTF8.GetBytes("{"), _ => null, _ => null));
        Assert.Throws<InvalidDataException>(() => EquipmentEvidenceService.Analyze(new byte[EquipmentEvidenceService.MaximumBytes + 1], _ => null, _ => null));
    }

    [Fact]
    public void ClientReportedMissingCannotBePromotedByAvailableRegistryBytes()
    {
        var input = Input(); input["assets"] = new JsonArray(new JsonObject { ["path"] = Member, ["error"] = "not-found-in-mounted-archives" });
        var report = Analyze(input, _ => Compiled);
        Assert.Single(report.Missing); Assert.Empty(report.Matched);
        Assert.NotNull(report.Missing[0].RegistrySha256);
    }
}
