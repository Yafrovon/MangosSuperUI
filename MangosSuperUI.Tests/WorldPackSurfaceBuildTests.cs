using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

public class WorldPackSurfaceBuildTests
{
    private static string? DataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.GetFullPath(Path.Combine(dir.FullName, "..", "MSUIClient", "GameData", "Data"));
            if (File.Exists(Path.Combine(candidate, "terrain.MPQ"))) return candidate;
        }
        return null;
    }

    [Fact]
    public void EmptySurfaceLayerDoesNotReencodeExistingTerrain()
    {
        string? data = DataDir(); if (data is null) return;
        using var stock = new VanillaArchiveSet(data);
        byte[] bytes = stock.ReadFile(WorldCoords.AdtPath("Azeroth", 32, 48))!;
        var adt = AdtDocument.Parse(bytes, 32, 48);
        var built = new Dictionary<(int map, int col, int row), AdtDocument> { [(0, 32, 48)] = adt };
        WorldPackSculptLayers.ApplySurface(built, new Dictionary<(int, int, int), Dictionary<int, float>>());
        Assert.Equal(bytes, adt.Write());
    }

    [Fact]
    public void FinalSurfaceDeltaSurvivesAbsoluteShapingAndSerialization()
    {
        string? data = DataDir(); if (data is null) return;
        using var stock = new VanillaArchiveSet(data);
        var adt = AdtDocument.Parse(stock.ReadFile(WorldCoords.AdtPath("Azeroth", 32, 48))!, 32, 48);
        const int index = 64 * 129 + 64;
        // An absolute path/coast stage flattened the finished surface to six. A new brush must be
        // added after that stage, even when its raw source terrain was well above or below six.
        adt.ApplySculpt(new Dictionary<int, float> { [index] = 6 - adt.OuterHeight(64, 64) });
        byte[] shaped = adt.Write();
        float innerBefore = adt.InnerHeight(64, 64);
        var built = new Dictionary<(int map, int col, int row), AdtDocument> { [(0, 32, 48)] = adt };
        var surface = new Dictionary<(int map, int col, int row), Dictionary<int, float>>
            { [(0, 32, 48)] = new() { [index] = 3.098056f } };
        WorldPackSculptLayers.ApplySurface(built, surface);
        var written = AdtDocument.Parse(adt.Write(), 32, 48);
        Assert.InRange(written.OuterHeight(64, 64), 9.0979f, 9.0982f);
        Assert.Equal(innerBefore + 3.098056f / 4, written.InnerHeight(64, 64), 3);
        Assert.False(AdtDocument.Parse(shaped, 32, 48).NormalsOf(8 * 16 + 8)
            .SequenceEqual(written.NormalsOf(8 * 16 + 8)), "Normals must describe the final edited surface.");
        Assert.Equal(AdtDocument.Parse(shaped, 32, 48).OuterHeight(20, 20), written.OuterHeight(20, 20));
    }

    [Fact]
    public void SurfaceOnlySharedEdgeRemainsJoinedAfterWritingBothTiles()
    {
        string? data = DataDir(); if (data is null) return;
        using var stock = new VanillaArchiveSet(data);
        var built = new Dictionary<(int map, int col, int row), AdtDocument>();
        foreach (int col in new[] { 31, 32 })
            built[(0, col, 48)] = AdtDocument.Parse(stock.ReadFile(WorldCoords.AdtPath("Azeroth", col, 48))!, col, 48);
        var normalized = WorldPackSculptLayers.SurfaceStroke(new[] { new SculptTile
            { Col = 32, Row = 48, Deltas = new() { [64 * 129] = 7.25f } } });
        var changes = normalized.ToDictionary(t => (map: 0, col: t.Col, row: t.Row), t => t.Deltas);
        float before = built[(0, 32, 48)].OuterHeight(64, 0);
        WorldPackSculptLayers.ApplySurface(built, changes);
        var west = AdtDocument.Parse(built[(0, 31, 48)].Write(), 31, 48);
        var east = AdtDocument.Parse(built[(0, 32, 48)].Write(), 32, 48);
        Assert.Equal(before + 7.25f, east.OuterHeight(64, 0), 3);
        Assert.Equal(west.OuterHeight(64, 128), east.OuterHeight(64, 0), 3);
    }
}
