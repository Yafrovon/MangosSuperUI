using System.Numerics;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>
/// Road surfaces (MSUIClient shared_docs/WORLD_BUILDER.md, 2026-10-14): the shared TerrainPaint rule (a verbatim port of
/// the client's TerrainPaintLaw, which paints the live preview) and AdtDocument.PaintTexture's MCLY/MCAL/MTEX rewrite
/// against the stock Northshire tile. ADT tests are no-ops where the sibling MSUIClient GameData is absent.
/// </summary>
public class WorldPackPaintTests
{
    private const int Col = 32, Row = 48;
    private const string Cobble = "Tileset\\Elwynn\\ElwynnCobbleStoneBase.blp";

    private static string? DataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "..", "MSUIClient", "GameData", "Data");
            if (File.Exists(Path.Combine(candidate, "terrain.MPQ"))) return Path.GetFullPath(candidate);
            dir = dir.Parent;
        }
        return null;
    }

    // ── the shared rule (the same expectations as MSUIClient's WorldBuilder wire check) ──

    [Fact]
    public void Paint_BaseOnlyChunk_AddsOneLayer()
    {
        var tex = new List<int> { 10 };
        var alphas = new List<byte[]?> { null };
        Assert.True(TerrainPaint.PaintChunk(tex, alphas, 20, (r, c) => c < 32 ? 1f : 0f));
        var w = TerrainPaint.Weights(alphas);
        Assert.Equal(new[] { 10, 20 }, tex);
        Assert.True(w[1][5 * 64 + 3] > 0.999f);
        Assert.True(w[0][5 * 64 + 40] > 0.999f);
        Assert.False(TerrainPaint.PaintChunk(tex, alphas, 30, (_, _) => 0f));
        Assert.Equal(2, tex.Count);
    }

    [Fact]
    public void Paint_LowerLayer_IsNotHiddenByLayersAbove()
    {
        var full = Enumerable.Repeat((byte)255, TerrainPaint.Pixels).ToArray();
        var tex = new List<int> { 1, 2, 3 };
        var alphas = new List<byte[]?> { null, (byte[])full.Clone(), (byte[])full.Clone() };
        TerrainPaint.PaintChunk(tex, alphas, 1, (_, _) => 1f);
        Assert.True(TerrainPaint.Weights(alphas)[0][100] > 0.999f);
    }

    [Fact]
    public void Paint_FifthTexture_ReplacesTheLeastVisibleLayer_On4BitAlphas()
    {
        var half = Enumerable.Repeat((byte)136, TerrainPaint.Pixels).ToArray();
        var tex = new List<int> { 1, 2, 3, 4 };
        var alphas = new List<byte[]?> { null, (byte[])half.Clone(), (byte[])half.Clone(), new byte[TerrainPaint.Pixels] };
        TerrainPaint.PaintChunk(tex, alphas, 9, (r, _) => r == 0 ? 1f : 0f);
        Assert.Equal(4, tex.Count);
        Assert.Contains(9, tex);
        Assert.DoesNotContain(4, tex);
        Assert.All(alphas.Skip(1), a => Assert.All(a!, v => Assert.Equal(0, v % 17)));
    }

    [Fact]
    public void RoadAmount_And_PixelWorld_MatchTheClient()
    {
        Assert.Equal(1f, TerrainPaint.RoadAmount(2f, 6f, 2f, 1f));
        Assert.Equal(0f, TerrainPaint.RoadAmount(6f, 6f, 2f, 1f));
        Assert.Equal(0.5f, TerrainPaint.RoadAmount(4f, 6f, 2f, 1f), 5);
        var p0 = TerrainPaint.PixelWorld(32, 48, 0, 0, 0, 0);
        Assert.Equal(-16 * WorldCoords.Tile - TerrainPaint.ChunkSize / 128f, p0.X, 2);
    }

    // ── the ADT writer ──

    [Fact]
    public void PaintTexture_ThatReachesNothing_KeepsTheTileByteIdentical()
    {
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", Col, Row))!;
        var doc = AdtDocument.Parse(bytes, Col, Row);
        Assert.Equal(0, doc.PaintTexture(Cobble, (_, _) => true, (_, _) => 0f));
        Assert.Equal(bytes, doc.Write());
    }

    [Fact]
    public void PaintTexture_StripShowsAfterReparse_AndEverythingElseSurvives()
    {
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", Col, Row))!;
        var before = AdtDocument.Parse(bytes, Col, Row);
        var doc = AdtDocument.Parse(bytes, Col, Row);

        // A 10 yd strip along world Y through the tile's middle row of chunks.
        float x0 = (32 - Row) * WorldCoords.Tile - 8.5f * TerrainPaint.ChunkSize;
        int changed = doc.PaintTexture(Cobble, (cx, _) => MathF.Abs(cx - x0) < 30f, (x, _) => MathF.Abs(x - x0) < 5f ? 1f : 0f);
        Assert.Equal(16, changed);

        var re = AdtDocument.Parse(doc.Write(), Col, Row);
        // Painted: the chunk under the strip now carries the texture at full visible weight on the strip.
        int hit = re.ChunkIndex(5, 8);
        var layers = re.ChunkLayers(hit);
        var road = layers.Single(l => l.texture.Equals(Cobble, StringComparison.OrdinalIgnoreCase));
        int pixelRow = (int)((((32 - Row) * WorldCoords.Tile - 8 * TerrainPaint.ChunkSize) - x0) / (TerrainPaint.ChunkSize / 64f));
        Assert.True(road.alpha[pixelRow * 64 + 10] > 0.99f, $"road weight {road.alpha[pixelRow * 64 + 10]}");
        Assert.True(layers.Count <= 4);
        // Far from the strip: identical layers.
        int far = re.ChunkIndex(5, 1);
        Assert.Equal(before.ChunkTextures(far), re.ChunkTextures(far));
        Assert.Equal(before.ChunkLayers(far).SelectMany(l => l.alpha), re.ChunkLayers(far).SelectMany(l => l.alpha));
        // Sub-chunks after MCLY/MCAL were shifted, not lost: heights, water, holes and props read back unchanged.
        Assert.Equal(before.OuterHeights(), re.OuterHeights());
        Assert.Equal(before.LiquidCells().ToList(), re.LiquidCells().ToList());
        Assert.Equal(Enumerable.Range(0, 256).Select(before.HolesOf), Enumerable.Range(0, 256).Select(re.HolesOf));
        Assert.Equal(before.DoodadCount, re.DoodadCount);
        // The texture went into MTEX once, and painting again reuses it.
        Assert.Equal(16, re.PaintTexture(Cobble, (cx, _) => MathF.Abs(cx - x0) < 30f, (x, _) => MathF.Abs(x - x0) < 5f ? 1f : 0f));
        var again = AdtDocument.Parse(re.Write(), Col, Row);
        Assert.Equal(1, again.ChunkTextures(hit).Count(t => t.Equals(Cobble, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void GradedPath_ReadsGradeAndSurface_OlderDocsStillGrade()
    {
        var old = GradedPath.Parse("old", """{"map":0,"points":[[0,0,1],[10,0,1]],"width":12}""");
        Assert.True(old.Grade);
        Assert.Null(old.Surface);
        var road = GradedPath.Parse("road", """
            {"map":0,"points":[[-8888,-280,80],[-8860,-262,79]],"width":6,"grade":false,
             "surface":{"texture":"Tileset\\Elwynn\\ElwynnCobbleStoneBase.blp","width":6,"softEdge":1.5,"strength":1}}
            """);
        Assert.False(road.Grade);
        Assert.Equal(Cobble, road.Surface!.Texture);
        Assert.Equal(1.5f, road.Surface.SoftEdge);
        Assert.Contains((32, 48), road.TouchedTiles());
    }
}
