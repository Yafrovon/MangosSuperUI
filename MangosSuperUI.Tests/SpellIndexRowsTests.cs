using MangosSuperUI.Services;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>MSUIClient SPELL_CREATOR_PLAN.md D5: a design's new index rows get real ones.</summary>
public class SpellIndexRowsTests
{
    private static DbcWriterService Dbc(int fields, params uint[][] rows)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("WDBC"u8.ToArray());
        w.Write(rows.Length); w.Write(fields); w.Write(fields * 4); w.Write(1);
        foreach (uint[] row in rows)
            for (int f = 0; f < fields; f++) w.Write(f < row.Length ? row[f] : 0u);
        w.Write((byte)0);
        return DbcWriterService.ReadDbc(stream.ToArray(), "test");
    }

    private static uint F(float v) => DbcWriterService.FloatToUint(v);

    [Fact]
    public void AnEqualRowIsReusedAndANewValueAppendsAtTheFloor()
    {
        var tables = new Dictionary<string, DbcWriterService>
        {
            ["SpellCastTimes"] = Dbc(4, new uint[] { 1, 0, 0, 0 }, new uint[] { 5, 2000, 0, 2000 }),
            ["SpellRadius"] = Dbc(4, new uint[] { 8, F(5), 0, F(5) }),
        };
        var map = SpellIndexRows.Resolve(tables, new[]
        {
            new SpellIndexRows.Request("SpellCastTimes", 900_001, 2000),   // stock row 5
            new SpellIndexRows.Request("SpellCastTimes", 900_002, 3300),   // new
            new SpellIndexRows.Request("SpellRadius", 900_003, 7.5),       // new
            new SpellIndexRows.Request("SpellCastTimes", 900_004, 3300),   // the row just added
        }, out var changed);

        Assert.Equal(5u, map[900_001]);
        Assert.Equal(1000u, map[900_002]);
        Assert.Equal(1000u, map[900_003]);
        Assert.Equal(1000u, map[900_004]);
        Assert.Equal(new[] { "SpellCastTimes", "SpellRadius" }, changed.OrderBy(t => t));
        Assert.Equal(new uint[] { 1000, 3300, 0, 3300 }, tables["SpellCastTimes"].GetRow(1000));
        Assert.Equal(7.5f, DbcWriterService.UintToFloat(tables["SpellRadius"].GetRow(1000)![1]));
    }

    [Fact]
    public void ARangeRowIsZeroMinWithTheMax()
    {
        uint[] row = SpellIndexRows.RowFor("SpellRange", 1001, 42, 22);
        Assert.Equal(22, row.Length);
        Assert.Equal(1001u, row[0]);
        Assert.Equal(0f, DbcWriterService.UintToFloat(row[1]));
        Assert.Equal(42f, DbcWriterService.UintToFloat(row[2]));
        Assert.All(row.Skip(3), v => Assert.Equal(0u, v));
    }

    [Fact]
    public void IndexColumnsArePointedAtTheRealRows()
    {
        var rules = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["castingTimeIndex"] = 900_002L, ["effectRadiusIndex1"] = 900_003L, ["rangeIndex"] = 4L, ["effect1"] = 2L,
        };
        Assert.Null(SpellIndexRows.RewriteColumns(rules, new Dictionary<uint, uint> { [900_002] = 1000, [900_003] = 1001 }));
        Assert.Equal(1000L, rules["castingTimeIndex"]);
        Assert.Equal(1001L, rules["effectRadiusIndex1"]);
        Assert.Equal(4L, rules["rangeIndex"]);

        var orphan = new Dictionary<string, object?> { ["durationIndex"] = 900_009L };
        Assert.NotNull(SpellIndexRows.RewriteColumns(orphan, new Dictionary<uint, uint>()));
    }
}
