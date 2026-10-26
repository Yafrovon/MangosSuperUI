using Dapper;
using MangosSuperUI.Models;

namespace MangosSuperUI.Services.QuestAtlas;

/// <summary>
/// Builds the Quest Atlas "zone bundle": every quest that belongs on one zone's table, plus every
/// NPC, object, item, trigger, chain neighbour and spoken line needed to draw and read it.
///
/// Read-only, straight from the world DB on every call (no startup cache), so World Pack quests
/// published a minute ago show up. Author-grade on purpose — unlike the bot QuestGraphLoader it
/// keeps ALL givers/enders (creature and gameobject), all spawns, area-trigger objectives, all
/// patch rows resolved to the highest patch, and the quest text.
///
/// Locations are derived, never stored: 1.12 has no quest POI data (see QUEST_ATLAS.md).
/// </summary>
public class QuestAtlasService
{
    private const int MaxSpawnsPerEntry = 300;
    private const int MaxSourcesPerItem = 25;

    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;

    public QuestAtlasService(ConnectionFactory db, DbcService dbc)
    {
        _db = db;
        _dbc = dbc;
    }

    // ── Zones ────────────────────────────────────────────────────────────

    /// <summary>Top-level zones (area_template.zone_id = 0) that own at least one quest,
    /// counting quests filed under their subzones too.</summary>
    public async Task<List<object>> ZonesAsync()
    {
        using var conn = _db.Mangos();
        var rows = await conn.QueryAsync(@"
            SELECT z.entry, z.map_id, z.name, z.team, COUNT(DISTINCT q.entry) AS quests,
                   MIN(CASE WHEN q.QuestLevel > 0 THEN q.QuestLevel END) AS minLevel, MAX(q.QuestLevel) AS maxLevel
            FROM area_template z
            JOIN area_template a ON a.entry = z.entry OR a.zone_id = z.entry
            JOIN quest_template q ON q.ZoneOrSort = a.entry
            WHERE z.zone_id = 0
            GROUP BY z.entry, z.map_id, z.name, z.team
            ORDER BY minLevel, z.name");

        var rects = _dbc.WorldMapZones.Where(w => w.AreaId != 0).Select(w => (int)w.AreaId).ToHashSet();
        return rows.Select(r => (object)new
        {
            areaId = (int)r.entry,
            mapId = (int)r.map_id,
            name = (string)r.name,
            team = (int)r.team,
            quests = (int)(long)r.quests,
            minLevel = r.minLevel == null ? 0 : (int)r.minLevel,
            maxLevel = r.maxLevel == null ? 0 : (int)r.maxLevel,
            hasMapRect = rects.Contains((int)r.entry)
        }).ToList();
    }

    // ── Everyone standing on the board (figurine layer) ─────────────────

    private const int MaxBoardSpawns = 6000;

    /// <summary>All creature spawns inside a world rectangle, plus the templates they use.
    /// Spawns come back as compact rows [entry, x, y, z, orientation].</summary>
    public async Task<object> CreaturesOnBoardAsync(int mapId, float top, float bottom, float left, float right)
    {
        using var conn = _db.Mangos();
        var p = new { mapId, top, bottom, left, right };
        var spawns = (await conn.QueryAsync<(int id, float x, float y, float z, float o)>(@"
            SELECT id, position_x, position_y, position_z, orientation FROM creature
            WHERE map = @mapId AND position_x BETWEEN @bottom AND @top AND position_y BETWEEN @right AND @left
            LIMIT " + (MaxBoardSpawns + 1), p)).ToList();
        bool truncated = spawns.Count > MaxBoardSpawns;
        if (truncated) spawns.RemoveAt(spawns.Count - 1);

        var ids = spawns.Select(s => s.id).Distinct().ToList();
        var templates = new Dictionary<int, object>();
        if (ids.Count > 0)
            foreach (var t in await conn.QueryAsync(@"
                SELECT t.entry, t.name, t.subname, t.level_min, t.level_max, t.faction, t.npc_flags, t.rank, t.type,
                       t.display_id1, t.display_id2, t.display_id3, t.display_id4,
                       t.display_scale1, t.display_scale2, t.display_scale3, t.display_scale4
                FROM creature_template t
                JOIN (SELECT entry, MAX(patch) AS p FROM creature_template WHERE entry IN @ids GROUP BY entry) m
                  ON m.entry = t.entry AND m.p = t.patch", new { ids }))
            {
                // The model the client draws: the first non-zero display slot and that slot's scale, so the client
                // needs no local creature catalogue (the tabletop's figures work on any machine).
                var row = (IDictionary<string, object>)t;
                uint displayId = 0;
                float scale = 0f;
                for (int i = 1; i <= 4 && displayId == 0; i++)
                {
                    displayId = Convert.ToUInt32(row[$"display_id{i}"] ?? 0u);
                    if (displayId != 0) scale = Convert.ToSingle(row[$"display_scale{i}"] ?? 0f);
                }
                templates[(int)t.entry] = new
                {
                    name = (string)t.name,
                    subname = (string?)t.subname,
                    levelMin = (int)t.level_min,
                    levelMax = (int)t.level_max,
                    faction = (int)t.faction,
                    npcFlags = Convert.ToInt64(t.npc_flags),
                    rank = (int)t.rank,
                    type = (int)t.type,
                    displayId,
                    scale = scale <= 0 ? 1f : scale   // 0 = model default
                };
            }

        return new
        {
            truncated,
            templates,
            spawns = spawns.Select(s => new[] { s.id, MathF.Round(s.x, 1), MathF.Round(s.y, 1), MathF.Round(s.z, 1), MathF.Round(s.o, 2) })
        };
    }

    /// <summary>The MSUIClient creator catalogue (creator-creatures.tsv): one row per template entry at its highest
    /// patch, the first non-zero display slot and its scale; entries without a model are dropped. Tabs and line
    /// breaks inside names become spaces so a row is always one line of nine fields.</summary>
    public async Task<string> CreatureCatalogueTsvAsync()
    {
        using var conn = _db.Mangos();
        var rows = await conn.QueryAsync(@"
            SELECT t.entry, t.name, t.subname, t.level_min, t.level_max, t.rank, t.type,
                   t.display_id1, t.display_id2, t.display_id3, t.display_id4,
                   t.display_scale1, t.display_scale2, t.display_scale3, t.display_scale4
            FROM creature_template t
            JOIN (SELECT entry, MAX(patch) AS p FROM creature_template GROUP BY entry) m
              ON m.entry = t.entry AND m.p = t.patch
            ORDER BY t.entry");
        static string Clean(object? v) => (v as string ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        var sb = new System.Text.StringBuilder(600_000);
        sb.Append("entry\tname\tsubname\tlevel_min\tlevel_max\trank\ttype\tdisplay_id\tdisplay_scale\n");
        foreach (var t in rows)
        {
            var row = (IDictionary<string, object>)t;
            uint displayId = 0;
            float scale = 0f;
            for (int i = 1; i <= 4 && displayId == 0; i++)
            {
                displayId = Convert.ToUInt32(row[$"display_id{i}"] ?? 0u);
                if (displayId != 0) scale = Convert.ToSingle(row[$"display_scale{i}"] ?? 0f);
            }
            if (displayId == 0) continue;
            uint entry = Convert.ToUInt32(row["entry"]);
            int levelMin = Convert.ToInt32(row["level_min"]), levelMax = Convert.ToInt32(row["level_max"]);
            int rank = Convert.ToInt32(row["rank"]), type = Convert.ToInt32(row["type"]);
            string name = Clean(row["name"]), subname = Clean(row["subname"]);
            sb.Append(System.FormattableString.Invariant(
                $"{entry}\t{name}\t{subname}\t{levelMin}\t{levelMax}\t{rank}\t{type}\t{displayId}\t{scale}\n"));
        }
        return sb.ToString();
    }

    // ── Zone bundle ──────────────────────────────────────────────────────

    public async Task<object?> ZoneAsync(int areaId)
    {
        using var conn = _db.Mangos();

        var area = await conn.QueryFirstOrDefaultAsync(
            "SELECT entry, map_id, zone_id, name, team, area_level FROM area_template WHERE entry = @areaId", new { areaId });
        if (area == null) return null;
        int mapId = (int)area.map_id;

        var subAreas = (await conn.QueryAsync<int>(
            "SELECT entry FROM area_template WHERE entry = @areaId OR zone_id = @areaId", new { areaId })).ToList();

        // Board rectangle: the client's own world-map rectangle when there is one.
        var wma = _dbc.WorldMapZones.FirstOrDefault(w => w.AreaId == areaId);
        Rect? rect = wma == null ? null : new Rect(wma.LocTop, wma.LocBottom, wma.LocLeft, wma.LocRight);

        // 1. Quest ids: filed under the zone (or a subzone), or given by someone standing on the board.
        var questIds = (await conn.QueryAsync<int>(
            "SELECT DISTINCT entry FROM quest_template WHERE ZoneOrSort IN @subAreas", new { subAreas })).ToHashSet();
        if (rect is { } r0)
        {
            var p = new { mapId, top = r0.Top, bottom = r0.Bottom, left = r0.Left, right = r0.Right };
            foreach (var id in await conn.QueryAsync<int>(@"
                SELECT DISTINCT r.quest FROM creature_questrelation r JOIN creature c ON c.id = r.id
                WHERE c.map = @mapId AND c.position_x BETWEEN @bottom AND @top AND c.position_y BETWEEN @right AND @left", p))
                questIds.Add(id);
            foreach (var id in await conn.QueryAsync<int>(@"
                SELECT DISTINCT r.quest FROM gameobject_questrelation r JOIN gameobject g ON g.id = r.id
                WHERE g.map = @mapId AND g.position_x BETWEEN @bottom AND @top AND g.position_y BETWEEN @right AND @left", p))
                questIds.Add(id);
        }
        if (questIds.Count == 0)
            return Bundle((object)area, subAreas, rect, mapId, new(), new(), new(), new(), new(), new(), new(), new());

        // 2. Quest rows, highest patch per entry.
        var questRows = (await conn.QueryAsync(@"
            SELECT q.* FROM quest_template q
            JOIN (SELECT entry, MAX(patch) AS p FROM quest_template WHERE entry IN @ids GROUP BY entry) m
              ON m.entry = q.entry AND m.p = q.patch", new { ids = questIds })).Select(r => (IDictionary<string, object>)r).ToList();

        // 3. Relations.
        var qp = new { ids = questIds };
        var npcGive = await conn.QueryAsync<(int id, int quest)>("SELECT DISTINCT id, quest FROM creature_questrelation WHERE quest IN @ids", qp);
        var npcEnd = await conn.QueryAsync<(int id, int quest)>("SELECT DISTINCT id, quest FROM creature_involvedrelation WHERE quest IN @ids", qp);
        var goGive = await conn.QueryAsync<(int id, int quest)>("SELECT DISTINCT id, quest FROM gameobject_questrelation WHERE quest IN @ids", qp);
        var goEnd = await conn.QueryAsync<(int id, int quest)>("SELECT DISTINCT id, quest FROM gameobject_involvedrelation WHERE quest IN @ids", qp);
        var trig = await conn.QueryAsync<(int id, int quest)>("SELECT DISTINCT id, quest FROM areatrigger_involvedrelation WHERE quest IN @ids", qp);

        // 4. Objectives → entity sets.
        var npcIds = new HashSet<int>(npcGive.Select(x => x.id).Concat(npcEnd.Select(x => x.id)));
        var goIds = new HashSet<int>(goGive.Select(x => x.id).Concat(goEnd.Select(x => x.id)));
        var itemIds = new HashSet<int>();
        var neighbourIds = new HashSet<int>();
        var scriptStart = new HashSet<int>();
        var scriptEnd = new HashSet<int>();
        foreach (var q in questRows)
        {
            for (int s = 1; s <= 4; s++)
            {
                int cg = I(q, $"ReqCreatureOrGOId{s}");
                if (cg > 0) npcIds.Add(cg); else if (cg < 0) goIds.Add(-cg);
                int it = I(q, $"ReqItemId{s}");
                if (it > 0) itemIds.Add(it);
                int src = I(q, $"ReqSourceId{s}");
                if (src > 0) itemIds.Add(src);
            }
            foreach (var col in new[] { "RewChoiceItemId1", "RewChoiceItemId2", "RewChoiceItemId3", "RewChoiceItemId4",
                                        "RewChoiceItemId5", "RewChoiceItemId6", "RewItemId1", "RewItemId2", "RewItemId3", "RewItemId4", "SrcItemId" })
                if (I(q, col) > 0) itemIds.Add(I(q, col));
            foreach (var col in new[] { "PrevQuestId", "NextQuestId", "NextQuestInChain", "BreadcrumbForQuestId" })
            {
                int n = Math.Abs(I(q, col));
                if (n > 0 && !questIds.Contains(n)) neighbourIds.Add(n);
            }
            if (I(q, "StartScript") > 0) scriptStart.Add(I(q, "StartScript"));
            if (I(q, "CompleteScript") > 0) scriptEnd.Add(I(q, "CompleteScript"));
        }

        // Quests elsewhere that point INTO this board (their Next/Prev/breadcrumb is one of ours).
        foreach (var n in await conn.QueryAsync<int>(@"
            SELECT DISTINCT entry FROM quest_template
            WHERE (ABS(PrevQuestId) IN @ids OR ABS(NextQuestId) IN @ids OR NextQuestInChain IN @ids OR BreadcrumbForQuestId IN @ids)", qp))
            if (!questIds.Contains(n)) neighbourIds.Add(n);

        // Exclusive-group mates that are not on the board.
        var groups = questRows.Select(q => I(q, "ExclusiveGroup")).Where(g => g != 0).Distinct().ToList();
        if (groups.Count > 0)
            foreach (var n in await conn.QueryAsync<int>("SELECT DISTINCT entry FROM quest_template WHERE ExclusiveGroup IN @groups", new { groups }))
                if (!questIds.Contains(n)) neighbourIds.Add(n);

        // 5. Item drop sources (only for items a quest asks you to collect).
        var collectItems = questRows.SelectMany(q => Enumerable.Range(1, 4).Select(s => I(q, $"ReqItemId{s}"))).Where(i => i > 0).Distinct().ToList();
        var itemSources = new Dictionary<int, List<object>>();
        if (collectItems.Count > 0)
        {
            var npcDrops = await conn.QueryAsync<(int item, int entry, float chance)>(@"
                SELECT DISTINCT l.item, t.entry, l.ChanceOrQuestChance
                FROM creature_loot_template l JOIN creature_template t ON t.loot_id = l.entry
                WHERE l.item IN @items AND l.mincountOrRef >= 0", new { items = collectItems });
            var goDrops = await conn.QueryAsync<(int item, int entry, float chance)>(@"
                SELECT DISTINCT l.item, g.entry, l.ChanceOrQuestChance
                FROM gameobject_loot_template l JOIN gameobject_template g ON g.data1 = l.entry AND g.type = 3
                WHERE l.item IN @items AND l.mincountOrRef >= 0", new { items = collectItems });

            foreach (var grp in npcDrops.Select(d => (d.item, kind: "npc", d.entry, chance: Math.Abs(d.chance)))
                     .Concat(goDrops.Select(d => (d.item, kind: "object", d.entry, chance: Math.Abs(d.chance))))
                     .GroupBy(d => d.item))
            {
                var top = grp.GroupBy(d => (d.kind, d.entry)).Select(g => g.OrderByDescending(x => x.chance).First())
                             .OrderByDescending(d => d.chance).Take(MaxSourcesPerItem).ToList();
                itemSources[grp.Key] = top.Select(d => (object)new { d.kind, d.entry, chance = Math.Round(d.chance, 2) }).ToList();
                foreach (var d in top) { if (d.kind == "npc") npcIds.Add(d.entry); else goIds.Add(d.entry); }
            }
        }

        // 6. Spoken lines (quest start/end scripts → broadcast_text) and greetings.
        var lines = new List<object>();
        async Task LoadLines(string table, string phase, HashSet<int> ids)
        {
            if (ids.Count == 0) return;
            var rows = await conn.QueryAsync($@"
                SELECT s.id, s.delay, s.command, s.target_type, s.target_param1, s.dataint, s.comments,
                       b.male_text, b.female_text, b.chat_type
                FROM {table} s LEFT JOIN broadcast_text b ON b.entry = s.dataint AND s.command = 0
                WHERE s.id IN @ids ORDER BY s.id, s.delay", new { ids });
            foreach (var l in rows)
            {
                string? text = (string?)l.male_text;
                if (string.IsNullOrEmpty(text)) text = (string?)l.female_text;
                lines.Add(new
                {
                    script = (int)l.id,
                    phase,
                    delay = (int)l.delay,
                    command = (int)l.command,
                    targetType = (int)l.target_type,
                    targetParam = (int)l.target_param1,
                    chatType = l.chat_type == null ? (int?)null : (int)l.chat_type,
                    text,
                    comments = (string?)l.comments
                });
            }
        }
        await LoadLines("quest_start_scripts", "start", scriptStart);
        await LoadLines("quest_end_scripts", "end", scriptEnd);

        var greetings = new List<object>();
        var giverNpcs = npcGive.Select(x => x.id).Distinct().ToList();
        var giverGos = goGive.Select(x => x.id).Distinct().ToList();
        if (giverNpcs.Count + giverGos.Count > 0)
        {
            var gr = await conn.QueryAsync(@"
                SELECT entry, type, content_default FROM quest_greeting
                WHERE (type = 0 AND entry IN @n) OR (type = 1 AND entry IN @g)",
                new { n = giverNpcs.DefaultIfEmpty(-1), g = giverGos.DefaultIfEmpty(-1) });
            greetings.AddRange(gr.Select(g => (object)new
            {
                kind = (int)g.type == 0 ? "npc" : "object",
                entry = (int)g.entry,
                text = (string)g.content_default
            }));
        }

        // 7. Entities with spawns.
        var npcs = await LoadNpcsAsync(conn, npcIds);
        var objects = await LoadObjectsAsync(conn, goIds);
        var items = await LoadItemsAsync(conn, itemIds, itemSources);

        var triggers = new Dictionary<int, object>();
        var trigIds = trig.Select(t => t.id).Distinct().ToList();
        if (trigIds.Count > 0)
            foreach (var t in await conn.QueryAsync(
                "SELECT id, name, map_id, x, y, z, radius, box_x, box_y FROM areatrigger_template WHERE id IN @trigIds", new { trigIds }))
                triggers[(int)t.id] = new
                {
                    id = (int)t.id, name = (string)t.name, map = (int)t.map_id,
                    x = (float)t.x, y = (float)t.y, z = (float)t.z,
                    radius = (float)t.radius, boxX = (float)t.box_x, boxY = (float)t.box_y
                };

        // Chain neighbours off the board: enough to draw a labelled arrow.
        var stubs = new List<object>();
        if (neighbourIds.Count > 0)
        {
            var sr = await conn.QueryAsync(@"
                SELECT q.entry, q.Title, q.QuestLevel, q.ZoneOrSort, q.ExclusiveGroup, a.name AS zoneName,
                       CASE WHEN a.zone_id = 0 THEN a.entry ELSE a.zone_id END AS topZone
                FROM quest_template q
                JOIN (SELECT entry, MAX(patch) AS p FROM quest_template WHERE entry IN @ids GROUP BY entry) m
                  ON m.entry = q.entry AND m.p = q.patch
                LEFT JOIN area_template a ON a.entry = q.ZoneOrSort", new { ids = neighbourIds });
            stubs.AddRange(sr.Select(s => (object)new
            {
                id = (int)s.entry, title = (string)s.Title, level = (int)s.QuestLevel,
                zoneOrSort = (int)s.ZoneOrSort, zoneName = (string?)s.zoneName,
                topZone = s.topZone == null ? 0 : (int)s.topZone,
                exclusiveGroup = (int)s.ExclusiveGroup
            }));
        }

        // 8. Shape the quests.
        var bySet = (IEnumerable<(int id, int quest)> rel) => rel.ToLookup(x => x.quest, x => x.id);
        var gN = bySet(npcGive); var eN = bySet(npcEnd); var gG = bySet(goGive); var eG = bySet(goEnd); var tQ = bySet(trig);
        var subSet = subAreas.ToHashSet();

        var quests = questRows.Select(q =>
        {
            int id = I(q, "entry");
            var objectives = new List<object>();
            for (int s = 1; s <= 4; s++)
            {
                int cg = I(q, $"ReqCreatureOrGOId{s}");
                string? otext = S(q, $"ObjectiveText{s}");
                if (cg > 0) objectives.Add(new { kind = "kill", id = cg, count = I(q, $"ReqCreatureOrGOCount{s}"), text = otext, spell = I(q, $"ReqSpellCast{s}") });
                else if (cg < 0) objectives.Add(new { kind = "object", id = -cg, count = I(q, $"ReqCreatureOrGOCount{s}"), text = otext, spell = I(q, $"ReqSpellCast{s}") });
                int it = I(q, $"ReqItemId{s}");
                if (it > 0) objectives.Add(new { kind = "item", id = it, count = I(q, $"ReqItemCount{s}"), text = (string?)null, spell = 0 });
            }
            foreach (var t in tQ[id]) objectives.Add(new { kind = "explore", id = t, count = 1, text = (string?)null, spell = 0 });
            if (I(q, "RepObjectiveFaction") > 0)
                objectives.Add(new { kind = "reputation", id = I(q, "RepObjectiveFaction"), count = I(q, "RepObjectiveValue"), text = (string?)null, spell = 0 });

            var choice = Enumerable.Range(1, 6).Where(s => I(q, $"RewChoiceItemId{s}") > 0)
                .Select(s => new { id = I(q, $"RewChoiceItemId{s}"), count = I(q, $"RewChoiceItemCount{s}") }).ToList();
            var fixedItems = Enumerable.Range(1, 4).Where(s => I(q, $"RewItemId{s}") > 0)
                .Select(s => new { id = I(q, $"RewItemId{s}"), count = I(q, $"RewItemCount{s}") }).ToList();
            var rep = Enumerable.Range(1, 5).Where(s => I(q, $"RewRepFaction{s}") > 0)
                .Select(s => new { faction = I(q, $"RewRepFaction{s}"), value = I(q, $"RewRepValue{s}") }).ToList();

            return new
            {
                id,
                patch = I(q, "patch"),
                custom = id >= 7_000_000,
                title = S(q, "Title"),
                level = I(q, "QuestLevel"),
                minLevel = I(q, "MinLevel"),
                maxLevel = I(q, "MaxLevel"),
                zoneOrSort = I(q, "ZoneOrSort"),
                home = subSet.Contains(I(q, "ZoneOrSort")),
                races = I(q, "RequiredRaces"),
                classes = I(q, "RequiredClasses"),
                type = I(q, "Type"),
                method = I(q, "Method"),
                questFlags = I(q, "QuestFlags"),
                specialFlags = I(q, "SpecialFlags"),
                suggestedPlayers = I(q, "SuggestedPlayers"),
                limitTime = I(q, "LimitTime"),
                prev = I(q, "PrevQuestId"),
                next = I(q, "NextQuestId"),
                nextInChain = I(q, "NextQuestInChain"),
                exclusiveGroup = I(q, "ExclusiveGroup"),
                breadcrumbFor = I(q, "BreadcrumbForQuestId"),
                srcItem = I(q, "SrcItemId"),
                srcSpell = I(q, "SrcSpell"),
                startScript = I(q, "StartScript"),
                completeScript = I(q, "CompleteScript"),
                text = new
                {
                    details = S(q, "Details"),
                    objectives = S(q, "Objectives"),
                    requestItems = S(q, "RequestItemsText"),
                    offerReward = S(q, "OfferRewardText"),
                    end = S(q, "EndText")
                },
                objectives,
                rewards = new
                {
                    xp = I(q, "RewXP"),
                    money = I(q, "RewOrReqMoney"),
                    spell = I(q, "RewSpell"),
                    spellCast = I(q, "RewSpellCast"),
                    choice,
                    items = fixedItems,
                    rep
                },
                givers = gN[id].Select(e => new { kind = "npc", entry = e }).Concat(gG[id].Select(e => new { kind = "object", entry = e })).ToList(),
                enders = eN[id].Select(e => new { kind = "npc", entry = e }).Concat(eG[id].Select(e => new { kind = "object", entry = e })).ToList()
            };
        }).OrderBy(q => q.level).ThenBy(q => q.id).Cast<object>().ToList();

        return Bundle((object)area, subAreas, rect, mapId, quests, stubs, npcs, objects, items, triggers, lines, greetings);
    }

    private static object Bundle(dynamic area, List<int> subAreas, Rect? rect, int mapId,
        List<object> quests, List<object> stubs, Dictionary<int, object> npcs, Dictionary<int, object> objects,
        Dictionary<int, object> items, Dictionary<int, object> triggers, List<object> lines, List<object> greetings) => new
    {
        zone = new
        {
            areaId = (int)area.entry,
            name = (string)area.name,
            mapId,
            team = (int)area.team,
            level = (int)area.area_level,
            subAreas,
            // null when the zone has no WorldMapArea row; the page then frames the board on the spawns.
            rect = rect is { } r ? new { top = r.Top, bottom = r.Bottom, left = r.Left, right = r.Right } : null
        },
        quests,
        stubs,
        npcs,
        objects,
        items,
        triggers,
        lines,
        greetings
    };

    private static async Task<Dictionary<int, object>> LoadNpcsAsync(System.Data.IDbConnection conn, HashSet<int> ids)
    {
        var result = new Dictionary<int, object>();
        if (ids.Count == 0) return result;
        var templates = await conn.QueryAsync(@"
            SELECT t.entry, t.name, t.subname, t.level_min, t.level_max, t.faction, t.npc_flags, t.rank
            FROM creature_template t
            JOIN (SELECT entry, MAX(patch) AS p FROM creature_template WHERE entry IN @ids GROUP BY entry) m
              ON m.entry = t.entry AND m.p = t.patch", new { ids });
        var spawns = (await conn.QueryAsync<(int id, int map, float x, float y, float z)>(
            "SELECT id, map, position_x, position_y, position_z FROM creature WHERE id IN @ids", new { ids }))
            .ToLookup(s => s.id);
        foreach (var t in templates)
        {
            int e = (int)t.entry;
            var sp = spawns[e].ToList();
            result[e] = new
            {
                entry = e,
                name = (string)t.name,
                subname = (string?)t.subname,
                levelMin = (int)t.level_min,
                levelMax = (int)t.level_max,
                faction = (int)t.faction,
                npcFlags = (long)Convert.ToInt64(t.npc_flags),
                rank = (int)t.rank,
                spawnTotal = sp.Count,
                spawns = sp.Take(MaxSpawnsPerEntry).Select(s => new[] { s.map, MathF.Round(s.x, 1), MathF.Round(s.y, 1), MathF.Round(s.z, 1) })
            };
        }
        return result;
    }

    private static async Task<Dictionary<int, object>> LoadObjectsAsync(System.Data.IDbConnection conn, HashSet<int> ids)
    {
        var result = new Dictionary<int, object>();
        if (ids.Count == 0) return result;
        var templates = await conn.QueryAsync(@"
            SELECT t.entry, t.name, t.type
            FROM gameobject_template t
            JOIN (SELECT entry, MAX(patch) AS p FROM gameobject_template WHERE entry IN @ids GROUP BY entry) m
              ON m.entry = t.entry AND m.p = t.patch", new { ids });
        var spawns = (await conn.QueryAsync<(int id, int map, float x, float y, float z)>(
            "SELECT id, map, position_x, position_y, position_z FROM gameobject WHERE id IN @ids", new { ids }))
            .ToLookup(s => s.id);
        foreach (var t in templates)
        {
            int e = (int)t.entry;
            var sp = spawns[e].ToList();
            result[e] = new
            {
                entry = e,
                name = (string)t.name,
                type = (int)t.type,
                spawnTotal = sp.Count,
                spawns = sp.Take(MaxSpawnsPerEntry).Select(s => new[] { s.map, MathF.Round(s.x, 1), MathF.Round(s.y, 1), MathF.Round(s.z, 1) })
            };
        }
        return result;
    }

    private static async Task<Dictionary<int, object>> LoadItemsAsync(System.Data.IDbConnection conn, HashSet<int> ids,
        Dictionary<int, List<object>> sources)
    {
        var result = new Dictionary<int, object>();
        if (ids.Count == 0) return result;
        var rows = await conn.QueryAsync(@"
            SELECT t.entry, t.name, t.quality, t.class, t.subclass, t.display_id
            FROM item_template t
            JOIN (SELECT entry, MAX(patch) AS p FROM item_template WHERE entry IN @ids GROUP BY entry) m
              ON m.entry = t.entry AND m.p = t.patch", new { ids });
        foreach (var r in rows)
        {
            int e = (int)r.entry;
            result[e] = new
            {
                id = e,
                name = (string)r.name,
                quality = (int)r.quality,
                itemClass = (int)r.@class,
                subclass = (int)r.subclass,
                sources = sources.TryGetValue(e, out var s) ? s : new List<object>()
            };
        }
        return result;
    }

    private readonly record struct Rect(float Top, float Bottom, float Left, float Right);

    private static int I(IDictionary<string, object> row, string col) =>
        row.TryGetValue(col, out var v) && v != null && v != DBNull.Value ? Convert.ToInt32(v) : 0;

    private static string? S(IDictionary<string, object> row, string col) =>
        row.TryGetValue(col, out var v) && v != null && v != DBNull.Value ? Convert.ToString(v) : null;
}
