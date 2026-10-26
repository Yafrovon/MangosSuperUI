using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

// ============================================================================
//  Bot Map Viewer — the spatial twin of Fleet View
// ============================================================================
// Fleet View answers "what is failing and how often." This answers "WHERE."
// It plots the same correlated incidents (from /Bots/FleetDiagnostics — every
// fault already carries x/y/map) as dots on the WorldMap minimap tiles, plus a
// live "where is every bot right now" layer driven by the brain's GetLiveFleet().
//
// Pull-only, front-end heavy: the page reuses /WorldMap/TileIndex (tiles),
// /Bots/FleetDiagnostics (fault dots + hotspots), /Bots/States (class colours),
// and the one new endpoint below (live positions). No spine/wire change.
//
// Self-contained against confirmed accessors only: _brain.GetLiveFleet()
// (public IReadOnlyList<object>, the same projection the Live tab renders).
public partial class BotsController
{
    // Renders Views/Bots/Map.cshtml. Page polls MapBots + FleetDiagnostics + States.
    public IActionResult Map() => View();

    /// <summary>
    /// GET /Bots/MapBots
    /// Live per-bot spine projection for the "current position" layer: pos{x,y,z},
    /// mapId, goal/why, hp/mana, dead/inCombat/stall, current target, last failure.
    /// classId is NOT in BotContext — the client joins it from /Bots/States by guid
    /// (same pattern Fleet View uses). Brain off / no bots => valid empty payload.
    /// </summary>
    [HttpGet]
    public IActionResult MapBots()
    {
        var bots = _brain.GetLiveFleet();
        return Json(new
        {
            ok = true,
            generatedUtc = DateTime.UtcNow,
            count = bots.Count,
            bots
        });
    }

    /// <summary>
    /// GET /Bots/Appearance?guids=1,2,3 (empty = every bot the brain tracks)
    /// What a bot LOOKS like, for the MSUIClient Quest Tabletop's living board (QUEST_TABLETOP.md): race, class, gender,
    /// skin/face/hair/facial hair, and each equipped slot (bag 0, slots 0-18) with the item's display id, inventory
    /// type, class/subclass, material and sheath - everything the client's remote-player renderer needs, so it draws
    /// the bot dressed without querying any server. Read-only; changes only with gear or looks, so the client fetches
    /// it once per bot and refreshes rarely.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Appearance(string? guids = null)
    {
        var ids = (guids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => uint.TryParse(s, out uint g) ? g : 0u).Where(g => g != 0).Distinct().ToList();
        if (ids.Count == 0)
            ids = _brain.GetLiveFleet()
                .Select(b => Convert.ToUInt32(b.GetType().GetProperty("guid")?.GetValue(b) ?? 0))
                .Where(g => g != 0).Distinct().ToList();
        if (ids.Count == 0) return Json(new { ok = true, bots = new Dictionary<string, object>() });
        if (ids.Count > 2000) ids = ids.Take(2000).ToList();

        await using var chars = _db.Characters();
        var people = (await chars.QueryAsync(@"
            SELECT guid, name, race, class, gender, skin, face, hair_style, hair_color, facial_hair
            FROM characters WHERE guid IN @ids", new { ids })).ToList();
        var worn = (await chars.QueryAsync<(uint Guid, uint Slot, uint Item)>(@"
            SELECT guid, slot, item_id FROM character_inventory
            WHERE guid IN @ids AND bag = 0 AND slot < 19", new { ids })).ToList();

        var entries = worn.Select(w => w.Item).Distinct().ToList();
        var templates = new Dictionary<uint, (uint Display, uint InvType, uint Class, uint Sub, int Material, uint Sheath)>();
        if (entries.Count > 0)
        {
            await using var world = _db.Mangos();
            foreach (var t in await world.QueryAsync(@"
                SELECT t.entry, t.display_id, t.inventory_type, t.class, t.subclass, t.material, t.sheath
                FROM item_template t
                JOIN (SELECT entry, MAX(patch) AS p FROM item_template WHERE entry IN @entries GROUP BY entry) m
                  ON m.entry = t.entry AND m.p = t.patch", new { entries }))
            {
                var row = (IDictionary<string, object>)t;
                templates[Convert.ToUInt32(row["entry"])] = (Convert.ToUInt32(row["display_id"]), Convert.ToUInt32(row["inventory_type"]),
                    Convert.ToUInt32(row["class"]), Convert.ToUInt32(row["subclass"]), Convert.ToInt32(row["material"]), Convert.ToUInt32(row["sheath"]));
            }
        }

        var bySlot = worn.GroupBy(w => w.Guid).ToDictionary(g => g.Key, g => g.ToList());
        var bots = new Dictionary<string, object>();
        foreach (var p in people)
        {
            var r = (IDictionary<string, object>)p;
            uint guid = Convert.ToUInt32(r["guid"]);
            var items = bySlot.GetValueOrDefault(guid, new())
                .Where(w => templates.ContainsKey(w.Item))
                .OrderBy(w => w.Slot)
                .Select(w =>
                {
                    var t = templates[w.Item];
                    return new { slot = w.Slot, entry = w.Item, display = t.Display, invType = t.InvType,
                                 itemClass = t.Class, subclass = t.Sub, material = t.Material, sheath = t.Sheath };
                })
                .ToList();
            bots[guid.ToString()] = new
            {
                name = Convert.ToString(r["name"]),
                race = Convert.ToInt32(r["race"]), cls = Convert.ToInt32(r["class"]), gender = Convert.ToInt32(r["gender"]),
                skin = Convert.ToInt32(r["skin"]), face = Convert.ToInt32(r["face"]), hairStyle = Convert.ToInt32(r["hair_style"]),
                hairColor = Convert.ToInt32(r["hair_color"]), facialHair = Convert.ToInt32(r["facial_hair"]),
                items
            };
        }
        return Json(new { ok = true, bots });
    }
}
