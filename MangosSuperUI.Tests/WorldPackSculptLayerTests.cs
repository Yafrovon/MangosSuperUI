using System.Text.Json;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

public class WorldPackSculptLayerTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    [Fact]
    public void LegacyPayloadAndRequestStillTargetSourceLayer()
    {
        const string old = """{"mapId":0,"tiles":[{"col":29,"row":35,"deltas":{"0":2.5,"128":-0.75}}]}""";
        var payload = JsonSerializer.Deserialize<SculptPayload>(old, Json)!;
        var request = JsonSerializer.Deserialize<SculptRequest>(old, Json)!;
        Assert.False(payload.Surface); Assert.False(request.Surface);
        Assert.Equal("wp_sculpt", WorldPackSculptLayers.Table(payload.Surface));
        Assert.Equal(2.5f, payload.Tiles[0].Deltas[0]);
        var oldMove = JsonSerializer.Deserialize<RelocatePayload>("""{"spec":{"fromMap":0,"toMap":1,"dCol":1,"dRow":0,"toStockMap":true},"sculptFrom":{"mapId":0,"tiles":[]},"sculptTo":{"mapId":1,"tiles":[]}}""", Json)!;
        Assert.False(oldMove.SculptFrom!.Surface); Assert.False(oldMove.SculptTo!.Surface);
        Assert.Null(oldMove.SurfaceFrom); Assert.Null(oldMove.SurfaceTo);
    }

    [Fact]
    public void SurfaceOperationSurvivesAuditSerializationAndKeepsItsTable()
    {
        var before = Payload(true);
        var restored = JsonSerializer.Deserialize<SculptPayload>(JsonSerializer.Serialize(before, Json), Json)!;
        Assert.True(restored.Surface);
        Assert.Equal("wp_surface_sculpt", WorldPackSculptLayers.Table(restored.Surface));
        Assert.Equal(before.Tiles[0].Deltas, restored.Tiles[0].Deltas);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RelocationPreservesLayerIndicesAndHeightsWithoutAliasing(bool surface)
    {
        var from = Payload(surface);
        var to = WorldPackSculptLayers.Relocate(from, 801, -1, -2);
        Assert.Equal(801, to.MapId); Assert.Equal(surface, to.Surface);
        var movedTile = to.Tiles.Single(t => t.Col == 28 && t.Row == 33);
        Assert.Equal(from.Tiles[0].Deltas, movedTile.Deltas);
        movedTile.Deltas[0] = 99;
        Assert.Equal(2.5f, from.Tiles[0].Deltas[0]);
        Assert.Equal((29, 35), (from.Tiles[0].Col, from.Tiles[0].Row));
    }

    [Fact]
    public void RelocationAndUndoRestoreBothLayersEvenWhenTilesOverlap()
    {
        var state = new Dictionary<(bool surface, int map, int col, int row, int vertex), float>();
        void Apply(SculptPayload p, float sign)
        {
            foreach (var t in p.Tiles) foreach (var v in t.Deltas)
            {
                var key = (p.Surface, p.MapId, t.Col, t.Row, v.Key);
                float next = state.GetValueOrDefault(key) + sign * v.Value;
                if (Math.Abs(next) < .0001f) state.Remove(key); else state[key] = next;
            }
        }
        var source = Payload(false); var surface = Payload(true);
        surface.Tiles[0].Deltas[0] = 7.25f;
        Apply(source, 1); Apply(surface, 1);
        state[(true, 801, 28, 33, 0)] = 19; // unrelated destination-map edit must survive.
        var original = new Dictionary<(bool, int, int, int, int), float>(state);
        foreach (var layer in new[] { source, surface })
        {
            var moved = WorldPackSculptLayers.Relocate(layer, 0, 1, 0); // tile29 lands on tile30, which also moves.
            Apply(layer, -1); Apply(moved, 1);
            Apply(moved, -1); Apply(layer, 1);
        }
        Assert.Equal(original.OrderBy(k => k.Key), state.OrderBy(k => k.Key));
        Apply(surface, -1);
        Assert.Equal(2.5f, state[(false, 0, 29, 35, 0)]);
        Assert.False(state.ContainsKey((true, 0, 29, 35, 0)));
        Apply(source, -1);
        Assert.Single(state);
        Assert.Equal(19, state[(true, 801, 28, 33, 0)]);
    }

    [Theory]
    [InlineData(-30, 0)] [InlineData(35, 0)] [InlineData(0, -36)] [InlineData(0, 29)]
    public void RelocationRejectsLeavingTheTileGrid(int columns, int rows)
    {
        var from = Payload(true);
        Assert.Throws<ArgumentException>(() => WorldPackSculptLayers.Relocate(from, 0, columns, rows));
        Assert.Equal((29, 35), (from.Tiles[0].Col, from.Tiles[0].Row));
    }

    [Fact]
    public void SurfaceCornerMirrorsToAllFourOwnersAndEqualDuplicatesAreNotAdded()
    {
        var input = new List<SculptTile> { new() { Col = 29, Row = 35, Deltas = new() { [0] = 1.25f } },
            new() { Col = 28, Row = 34, Deltas = new() { [16640] = 1.25f } } };
        var tiles = WorldPackSculptLayers.SurfaceStroke(input);
        Assert.Equal(4, tiles.Count);
        Assert.Equal(1.25f, tiles.Single(t => t.Col == 29 && t.Row == 35).Deltas[0]);
        Assert.Equal(1.25f, tiles.Single(t => t.Col == 28 && t.Row == 35).Deltas[128]);
        Assert.Equal(1.25f, tiles.Single(t => t.Col == 29 && t.Row == 34).Deltas[16512]);
        Assert.Equal(1.25f, tiles.Single(t => t.Col == 28 && t.Row == 34).Deltas[16640]);
        Assert.All(tiles, t => Assert.Single(t.Deltas));
        tiles[0].Deltas.Clear(); Assert.Single(input[0].Deltas);
    }

    [Fact]
    public void SurfaceStrokeRejectsConflictingDuplicatesAndClipsOnlyMapBoundaryOwners()
    {
        Assert.Throws<ArgumentException>(() => WorldPackSculptLayers.SurfaceStroke(new[] {
            new SculptTile { Col = 29, Row = 35, Deltas = new() { [0] = 1 } },
            new SculptTile { Col = 28, Row = 34, Deltas = new() { [16640] = 2 } } }));
        var top = Assert.Single(WorldPackSculptLayers.SurfaceStroke(new[] { new SculptTile { Col = 0, Row = 0, Deltas = new() { [0] = 3 } } }));
        Assert.Equal((0, 0), (top.Col, top.Row)); Assert.Equal(3, top.Deltas[0]);
        var bottom = Assert.Single(WorldPackSculptLayers.SurfaceStroke(new[] { new SculptTile { Col = 63, Row = 63, Deltas = new() { [16640] = -2 } } }));
        Assert.Equal((63, 63), (bottom.Col, bottom.Row)); Assert.Equal(-2, bottom.Deltas[16640]);
    }

    [Fact]
    public void MovingAnOuterMapCornerInwardCreatesAllSharedSurfaceOwners()
    {
        var from = new SculptPayload { MapId = 0, Surface = true, Tiles = new() {
            new() { Col = 0, Row = 0, Deltas = new() { [0] = 2 } } } };
        var moved = WorldPackSculptLayers.Relocate(from, 1, 1, 1);
        Assert.Equal(4, moved.Tiles.Count);
        Assert.Equal(2, moved.Tiles.Single(t => t.Col == 1 && t.Row == 1).Deltas[0]);
        Assert.Equal(2, moved.Tiles.Single(t => t.Col == 0 && t.Row == 0).Deltas[16640]);
        Assert.All(moved.Tiles, t => Assert.Equal(2, Assert.Single(t.Deltas).Value));
        Assert.Single(from.Tiles); Assert.Single(from.Tiles[0].Deltas);
    }

    [Fact]
    public void PublishedTotalsSumBothLayersWithoutChangingEitherSource()
    {
        var key = (0, 29, 35);
        var source = new Dictionary<(int, int, int), Dictionary<int, float>> { [key] = new() { [0] = 2, [128] = 5 } };
        var surface = new Dictionary<(int, int, int), Dictionary<int, float>> { [key] = new() { [0] = 7, [128] = -5 }, [(1, 32, 32)] = new() { [4] = 8 } };
        var total = WorldPackSculptLayers.Totals(source, surface);
        Assert.Equal(9, total[key][0]); Assert.Equal(0, total[key][128]); Assert.Equal(8, total[(1, 32, 32)][4]);
        total[key][0] = 99; Assert.Equal(2, source[key][0]); Assert.Equal(7, surface[key][0]);
    }

    private static SculptPayload Payload(bool surface) => new()
    {
        MapId = 0, Surface = surface,
        Tiles = new() { new() { Col = 29, Row = 35, Deltas = new() { [0] = 2.5f, [128] = -.75f } },
                        new() { Col = 30, Row = 35, Deltas = new() { [0] = -.75f, [16640] = -1.5f } } }
    };
}
