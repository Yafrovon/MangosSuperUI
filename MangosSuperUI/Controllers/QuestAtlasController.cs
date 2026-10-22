using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services.QuestAtlas;

namespace MangosSuperUI.Controllers;

/// <summary>
/// Quest Atlas — every quest on a tabletop relief model of its zone. Read-only for now.
/// Design guiderail: QUEST_ATLAS.md at the repo root.
/// </summary>
public class QuestAtlasController : Controller
{
    private readonly QuestAtlasService _atlas;
    private readonly ReliefService _relief;

    public QuestAtlasController(QuestAtlasService atlas, ReliefService relief)
    {
        _atlas = atlas;
        _relief = relief;
    }

    public IActionResult Index() => View();

    /// <summary>GET /QuestAtlas/Zones — top-level zones that own quests (subzones folded in).</summary>
    [HttpGet]
    public async Task<IActionResult> Zones() => Json(await _atlas.ZonesAsync());

    /// <summary>GET /QuestAtlas/Zone?areaId=40 — the full bundle for one zone's table.</summary>
    [HttpGet]
    public async Task<IActionResult> Zone(int areaId)
    {
        var bundle = await _atlas.ZoneAsync(areaId);
        return bundle == null ? NotFound(new { error = $"area {areaId} not in area_template" }) : Json(bundle);
    }

    /// <summary>
    /// GET /QuestAtlas/Relief?mapId=0&gx=51&gy=29 — one tile of the compiled relief.
    /// heights = base64 little-endian float32[33*33], row-major north→south, west→east.
    /// The matching paint is /WorldMap/Tile?map=Azeroth&row={gy}&col={gx}.
    /// </summary>
    [HttpGet]
    public IActionResult Relief(int mapId, int gx, int gy)
    {
        var t = _relief.GetTile(mapId, gx, gy);
        if (t == null) return Json(new { exists = false, mapId, gx, gy });

        var bytes = new byte[t.Heights.Length * 4];
        Buffer.BlockCopy(t.Heights, 0, bytes, 0, bytes.Length);
        Response.Headers["Cache-Control"] = "public, max-age=3600";
        return Json(new
        {
            exists = true,
            mapId, gx, gy,
            mapName = ReliefService.MapName(mapId),
            samples = ReliefService.Samples,
            tileYards = ReliefService.TileYards,
            northX = t.NorthX,
            westY = t.WestY,
            minZ = t.MinZ,
            maxZ = t.MaxZ,
            heights = Convert.ToBase64String(bytes),
            water = t.Water
        });
    }

    /// <summary>
    /// GET /QuestAtlas/Objects?mapId=0&gx=51&gy=29 — buildings and props the ADT places on one tile,
    /// in world coordinates. Geometry comes from /WorldEditor/WmoModel and /WorldEditor/DoodadModel.
    /// </summary>
    [HttpGet]
    public IActionResult Objects(int mapId, int gx, int gy)
    {
        var t = _relief.GetTile(mapId, gx, gy);
        if (t == null) return Json(new { exists = false, mapId, gx, gy });
        Response.Headers["Cache-Control"] = "public, max-age=3600";
        static object Row(ReliefService.Placement p) => new
        {
            m = p.Model, x = MathF.Round(p.X, 2), y = MathF.Round(p.Y, 2), z = MathF.Round(p.Z, 2),
            r = MathF.Round(p.RotY, 2), s = MathF.Round(p.Scale, 3), k = p.Kind
        };
        return Json(new
        {
            exists = true, mapId, gx, gy,
            wmos = t.Wmos.Select(Row),
            doodads = t.Doodads.Select(Row)
        });
    }

    /// <summary>
    /// GET /QuestAtlas/Creatures?mapId=0&top=..&bottom=..&left=..&right=.. — every creature spawn
    /// standing on a board rectangle, for the figurine layer.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Creatures(int mapId, float top, float bottom, float left, float right)
        => Json(await _atlas.CreaturesOnBoardAsync(mapId, top, bottom, left, right));
}
