using System.Reflection;
using System.Text.Json;
using MangosSuperUI.Services;
using MangosSuperUI.Services.WeaponForge;
using Xunit;

namespace MangosSuperUI.Tests;

public class WandDisplayVisualTests
{
    [Fact]
    public void DefaultWandUsesStockArcaneDisplayAndSchool()
    {
        var profile = WeaponTypeCatalog.Get("wand");
        Assert.Equal(21096u, profile.PinnedDisplayRow);
        Assert.Equal("6", profile.ItemTemplateOverrides()["dmg_type1"]);
    }

    [Fact]
    public void RepairPreservesUnrelatedFieldsAndIsIdempotent()
    {
        const string original = "{\"spellVisualId\":0,\"groupSoundIndex\":3,\"mirrorModelName2\":false,\"future\":{\"keep\":true}}";
        var repair = Plan(original, 2799);
        using var result = JsonDocument.Parse(repair.Json);
        Assert.Equal(2799u, result.RootElement.GetProperty("spellVisualId").GetUInt32());
        Assert.Equal(3, result.RootElement.GetProperty("groupSoundIndex").GetInt32());
        Assert.False(result.RootElement.GetProperty("mirrorModelName2").GetBoolean());
        Assert.True(result.RootElement.GetProperty("future").GetProperty("keep").GetBoolean());
        Assert.True(repair.Changed);
        Assert.False(Plan(repair.Json, 2799).Changed);
    }

    [Theory]
    [InlineData("{\"spellVisualId\":225}", 2799u)]
    [InlineData("{}", 0u)]
    [InlineData("[]", 2799u)]
    public void RepairRejectsConflictingOrInvalidMetadata(string json, uint desired)
        => Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Plan(json, desired)).InnerException);

    [Fact]
    public void WandVisualRequiresExistingPrecastCastAndMissileReferences()
    {
        var visuals = Dbc([2799, 372, 2973, 0, 0, 0, 1, 405]);
        var kits = Dbc([372, 0, 111], [2973, 0, 107]);
        var effects = Dbc([405, 0, 0]);
        Validate(2799, visuals, kits, effects);
        AssertBad(0, visuals, kits, effects);
        AssertBad(2799, visuals, Dbc([372, 0, 111]), effects);
        AssertBad(2799, visuals, kits, Dbc([406, 0, 0]));
        AssertBad(2799, Dbc([2799, 372, 2973, 0, 0, 0, 0, 405]), kits, effects);
    }

    private static (string Json, uint Previous, bool Changed) Plan(string json, uint visual)
        => ((string, uint, bool))typeof(CustomWeaponBuildService).GetMethod("PlanWandVisualRepair",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [json, visual])!;
    private static void Validate(uint id, DbcWriterService visuals, DbcWriterService kits, DbcWriterService effects)
        => typeof(WeaponDonorResolver).GetMethod("ValidateWandVisualRows", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [id, visuals, kits, effects]);
    private static void AssertBad(uint id, DbcWriterService visuals, DbcWriterService kits, DbcWriterService effects)
        => Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Validate(id, visuals, kits, effects)).InnerException);
    private static DbcWriterService Dbc(params uint[][] rows)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("WDBC"u8);
        writer.Write(rows.Length); writer.Write(rows[0].Length); writer.Write(rows[0].Length * 4); writer.Write(1);
        foreach (var row in rows) foreach (uint value in row) writer.Write(value);
        writer.Write((byte)0);
        return DbcWriterService.ReadDbc(stream.ToArray());
    }
}
