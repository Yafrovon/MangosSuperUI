using MangosSuperUI.Services;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>SpellDbcColumnMap: the law that a custom spell's Spell.dbc row and spell_template
/// row agree (MSUIClient shared_docs/SPELL_CREATOR_PLAN.md P0.5). The full table is proven
/// against the live data by /SpellCompleter/ColumnMapCheck; these pin the arithmetic.</summary>
public sealed class SpellDbcColumnMapTests
{
    [Fact]
    public void PointsForMatchesTheVmangosRoll()
    {
        // basePoints + irand(1, dieSides): Fireball R1 rolls 14..22 as base 13, 9 sides.
        Assert.Equal((13, 9), SpellDbcColumnMap.PointsFor(14, 22));
        // A flat value is one side (0 and 1 both roll exactly base + 1).
        Assert.Equal((4, 1), SpellDbcColumnMap.PointsFor(5, 5));
        // Reversed input is normalised, not rolled backwards.
        Assert.Equal((13, 9), SpellDbcColumnMap.PointsFor(22, 14));
    }

    [Fact]
    public void TheTableCoversTheRecordWithoutCollisions()
    {
        var fields = SpellDbcColumnMap.Columns.Select(c => c.Field).ToList();
        Assert.Equal(fields.Count, fields.Distinct().Count());
        Assert.All(fields, f => Assert.InRange(f, 1, SpellDbcColumnMap.FieldCount - 1));
        // The builder-owned fields are never written by the mirror.
        foreach (int owned in new[] { 0, 115, 117 }.Concat(Enumerable.Range(120, 36)))
            Assert.DoesNotContain(owned, fields);
        // The three fields the old builder got wrong.
        Assert.Contains(SpellDbcColumnMap.Columns, c => c.Name == "school" && c.Field == 1);
        Assert.Contains(SpellDbcColumnMap.Columns, c => c.Name == "baseLevel" && c.Field == 28);
        Assert.Contains(SpellDbcColumnMap.Columns, c => c.Name == "spellLevel" && c.Field == 29);
        Assert.Contains(SpellDbcColumnMap.Columns, c => c.Name == "speed" && c.Field == 37 && c.Kind == SpellDbcColumnMap.Kind.Float);
    }

    [Fact]
    public void EncodeKeepsSignsFloatsAndBothHalvesOfAFamilyMask()
    {
        var basePoints = SpellDbcColumnMap.Columns.First(c => c.Name == "effectBasePoints1");
        Assert.Equal(0xFFFFFFFFu, SpellDbcColumnMap.Encode(basePoints, -1));
        var speed = SpellDbcColumnMap.Columns.First(c => c.Name == "speed");
        Assert.Equal(BitConverter.SingleToUInt32Bits(24f), SpellDbcColumnMap.Encode(speed, 24.0));
        var low = SpellDbcColumnMap.Columns.First(c => c.Field == 161);
        var high = SpellDbcColumnMap.Columns.First(c => c.Field == 162);
        ulong mask = 0x0000_0012_8000_0001UL;
        Assert.Equal(0x8000_0001u, SpellDbcColumnMap.Encode(low, mask));
        Assert.Equal(0x12u, SpellDbcColumnMap.Encode(high, mask));
        Assert.Equal(0u, SpellDbcColumnMap.Encode(basePoints, DBNull.Value));
    }

    [Fact]
    public void DiffAgreesWithApplyOnTheSameRow()
    {
        var row = new Dictionary<string, object?>
        {
            ["school"] = 2, ["targets"] = 0x40, ["speed"] = 6.5f, ["effect1"] = 2,
            ["effectImplicitTargetA1"] = 16, ["effectRadiusIndex1"] = 13, ["effectDieSides1"] = 9,
            ["equippedItemClass"] = -1, ["spellFamilyFlags"] = 5UL,
        };
        var dbc = new uint[SpellDbcColumnMap.FieldCount];
        foreach (var column in SpellDbcColumnMap.Columns)
            if (row.TryGetValue(column.Name, out var v)) dbc[column.Field] = SpellDbcColumnMap.Encode(column, v);
        Assert.Empty(SpellDbcColumnMap.Diff(dbc, row));
        dbc[13] = 0;   // the client lost the ground-target flag
        Assert.Equal(new[] { "targets" }, SpellDbcColumnMap.Diff(dbc, row));
    }
}
