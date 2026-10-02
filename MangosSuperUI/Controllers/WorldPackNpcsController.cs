using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MangosSuperUI.Models;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace MangosSuperUI.Controllers;

/// <summary>Read-only source NPCs for the offline World Builder. Writes remain audited pack operations.</summary>
[Route("WorldPacks")]
public sealed class WorldPackNpcsController : Controller
{
    private readonly ConnectionFactory _db;
    public WorldPackNpcsController(ConnectionFactory db) => _db = db;

    [HttpGet("NpcsNear")]
    public async Task<IActionResult> NpcsNear(int mapId, double x, double y, double radius = 180)
    {
        if (mapId < 0 || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(radius))
            return BadRequest(new { success = false, error = "A map and finite position are required." });
        radius = Math.Clamp(radius, 10, 400);
        using var conn = _db.Mangos();
        var spawns = (await conn.QueryAsync(@"
SELECT * FROM creature WHERE map=@mapId
AND position_x BETWEEN @minX AND @maxX AND position_y BETWEEN @minY AND @maxY
AND POW(position_x-@x,2)+POW(position_y-@y,2)<=@r2
ORDER BY POW(position_x-@x,2)+POW(position_y-@y,2), guid LIMIT 200",
            new { mapId, x, y, minX = x - radius, maxX = x + radius, minY = y - radius, maxY = y + radius, r2 = radius * radius }))
            .Select(Body).ToList();
        var entries = spawns.Select(s => N(s, "id")).Distinct().ToArray();
        var templates = entries.Length == 0 ? new Dictionary<long, JsonObject>() :
            (await conn.QueryAsync("SELECT * FROM creature_template WHERE entry IN @entries ORDER BY patch DESC", new { entries }))
            .Select(Body).GroupBy(t => N(t, "entry")).ToDictionary(g => g.Key, g => g.First());
        return Json(new { success = true, mapId, radius, truncated = spawns.Count == 200,
            npcs = spawns.Where(s => templates.ContainsKey(N(s, "id"))).Select(s => new { spawn = s, template = templates[N(s, "id")] }) });
    }

    [HttpGet("Npc")]
    public async Task<IActionResult> Npc(uint entry)
    {
        using var conn = _db.Mangos();
        var raw = await conn.QueryFirstOrDefaultAsync("SELECT * FROM creature_template WHERE entry=@entry ORDER BY patch DESC LIMIT 1", new { entry });
        if (raw is null) return NotFound(new { success = false, error = "NPC template not found." });
        JsonObject template = Body((object)raw);
        var copyLimitations = new List<string>();
        string script = template["script_name"]?.ToString() ?? "", ai = template["ai_name"]?.ToString() ?? "";
        if (script.Length > 0) copyLimitations.Add($"This NPC uses the named script '{script}'. Importing its script for a new template is not supported yet.");
        if (ai.Length > 0) copyLimitations.Add($"This NPC uses '{ai}' AI. Its behavior must be imported before creating a pack replacement.");
        if (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM creature_ai_events WHERE creature_id=@entry", new { entry }) > 0)
            copyLimitations.Add("This NPC has entry-specific EventAI events. Importing those events is not supported yet.");
        // Vanilla credit compares the target's template entry directly for kills, casts and talks.
        // This Core has no creature-template alternate-credit field; a reserved clone cannot
        // safely inherit those objectives simply by copying its giver/ender relation rows.
        var objectives = (await conn.QueryAsync(@"
SELECT DISTINCT entry, Title FROM quest_template
WHERE ReqCreatureOrGOId1=@entry OR ReqCreatureOrGOId2=@entry
   OR ReqCreatureOrGOId3=@entry OR ReqCreatureOrGOId4=@entry
ORDER BY entry LIMIT 6", new { entry })).Select(Body).ToList();
        if (objectives.Count > 0)
        {
            string quests = string.Join(", ", objectives.Take(5).Select(q => $"{N(q, "entry")} ({q["Title"]})"));
            if (objectives.Count > 5) quests += ", and more";
            copyLimitations.Add($"This NPC is a kill, cast or talk objective target for quest(s) {quests}. Importing it would change its required creature ID, so Save is disabled until original-entry quest credit is supported.");
        }
        var docs = new JsonArray { Doc("creature_template", template) };
        async Task<List<JsonObject>> Rows(string table, string column, long id) => id == 0 ? new() :
            (await conn.QueryAsync($"SELECT * FROM `{table}` WHERE `{column}`=@id", new { id })).Select(Body).ToList();
        async Task Service(string direct, string inherited, string templateField, string key)
        {
            // Shared lists become direct lists on an imported NPC so changing its shop cannot edit another vendor.
            var rows = await Rows(inherited, "entry", N(template, templateField));
            rows.AddRange(await Rows(direct, "entry", entry));
            foreach (var r in rows.GroupBy(r => N(r, key)).Select(g => g.Last()))
            {
                r["entry"] = entry;
                docs.Add(Doc(direct, r));
            }
        }
        await Service("npc_vendor", "npc_vendor_template", "vendor_id", "item");
        await Service("npc_trainer", "npc_trainer_template", "trainer_id", "spell");
        foreach (var table in new[] { "creature_questrelation", "creature_involvedrelation" })
            foreach (var r in await Rows(table, "id", entry)) docs.Add(Doc(table, r));
        foreach (var r in await Rows("creature_equip_template", "entry", N(template, "equipment_id"))) docs.Add(Doc("creature_equip_template", r));
        foreach (var r in await Rows("creature_loot_template", "entry", N(template, "loot_id"))) docs.Add(Doc("creature_loot_template", r));
        var menus = await Rows("gossip_menu", "entry", N(template, "gossip_menu_id"));
        foreach (var r in menus) docs.Add(Doc("gossip_menu", r));
        foreach (var r in await Rows("gossip_menu_option", "menu_id", N(template, "gossip_menu_id"))) docs.Add(Doc("gossip_menu_option", r));
        foreach (long id in menus.Select(r => N(r, "text_id")).Distinct())
            foreach (var r in await Rows("npc_text", "ID", id))
            {
                docs.Add(Doc("npc_text", r));
                foreach (long broadcast in Enumerable.Range(0, 8).Select(i => N(r, "BroadcastTextID" + i)).Where(n => n > 0).Distinct())
                    foreach (var b in await Rows("broadcast_text", "entry", broadcast)) docs.Add(Doc("broadcast_text", b));
            }
        return Json(new { success = true, entry, docs, copyLimitations });
    }

    private static long N(JsonObject row, string key) => long.TryParse(row[key]?.ToString(), out var value) ? value : 0;
    private static JsonObject Body(object row) => JsonSerializer.SerializeToNode((IDictionary<string, object>)row)!.AsObject();
    private static JsonObject Doc(string table, JsonObject body) => new() { ["kind"] = "dbrow:" + table, ["body"] = body };
}
