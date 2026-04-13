using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text.Json;

namespace MangosSuperUI.Controllers;

public class InstancesController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;

    // Instance metadata: map ID → (name, category, level range)
    private static readonly List<InstanceInfo> INSTANCES = new()
    {
        // Dungeons
        new(389, "Ragefire Chasm", "dungeon", "13-18"),
        new(36,  "Deadmines", "dungeon", "17-21"),
        new(43,  "Wailing Caverns", "dungeon", "17-24"),
        new(34,  "The Stockade", "dungeon", "22-30"),
        new(33,  "Shadowfang Keep", "dungeon", "22-30"),
        new(48,  "Blackfathom Deeps", "dungeon", "24-32"),
        new(47,  "Razorfen Kraul", "dungeon", "29-38"),
        new(90,  "Gnomeregan", "dungeon", "29-38"),
        new(189, "Scarlet Monastery", "dungeon", "28-45"),
        new(129, "Razorfen Downs", "dungeon", "37-46"),
        new(70,  "Uldaman", "dungeon", "41-51"),
        new(209, "Zul'Farrak", "dungeon", "44-54"),
        new(349, "Maraudon", "dungeon", "46-55"),
        new(109, "Sunken Temple", "dungeon", "50-56"),
        new(230, "Blackrock Depths", "dungeon", "52-60"),
        new(229, "Blackrock Spire", "dungeon", "55-60"),
        new(429, "Dire Maul", "dungeon", "55-60"),
        new(329, "Stratholme", "dungeon", "58-60"),
        new(289, "Scholomance", "dungeon", "58-60"),
        // Raids
        new(249, "Onyxia's Lair", "raid", "60"),
        new(409, "Molten Core", "raid", "60"),
        new(469, "Blackwing Lair", "raid", "60"),
        new(309, "Zul'Gurub", "raid", "60"),
        new(509, "Ruins of Ahn'Qiraj", "raid", "60"),
        new(531, "Temple of Ahn'Qiraj", "raid", "60"),
        new(533, "Naxxramas", "raid", "60")
    };

    public InstancesController(ConnectionFactory db, DbcService dbc, AuditService audit)
    {
        _db = db;
        _dbc = dbc;
        _audit = audit;
    }

    public IActionResult Index() => View();

    // ===================== INSTANCE LIST =====================

    /// <summary>
    /// GET /Instances/List — Returns all instances with boss counts from curated list.
    /// </summary>
    [HttpGet]
    public IActionResult List([FromServices] IWebHostEnvironment env = null!)
    {
        var bossMap = GetBossMap(env);

        var result = INSTANCES.Select(inst => new
        {
            mapId = inst.MapId,
            name = inst.Name,
            category = inst.Category,
            levelRange = inst.LevelRange,
            bossCount = bossMap.GetValueOrDefault(inst.MapId)?.Count ?? 0
        });

        return Json(result);
    }

    // ── Curated boss list (loaded from JSON) ────────────────────────────
    private static Dictionary<int, List<BossEntry>>? _bossMap;
    private static readonly object _bossLock = new();

    private Dictionary<int, List<BossEntry>> GetBossMap(IWebHostEnvironment? env = null)
    {
        if (_bossMap != null) return _bossMap;
        lock (_bossLock)
        {
            if (_bossMap != null) return _bossMap;
            _bossMap = new Dictionary<int, List<BossEntry>>();

            // Load from wwwroot/data/instance-bosses.json
            var path = Path.Combine(
                env?.WebRootPath ?? "wwwroot",
                "data", "instance-bosses.json");

            if (System.IO.File.Exists(path))
            {
                var json = System.IO.File.ReadAllText(path);
                var doc = JsonDocument.Parse(json);
                foreach (var inst in doc.RootElement.GetProperty("instances").EnumerateArray())
                {
                    var mapId = inst.GetProperty("mapId").GetInt32();
                    var bosses = new List<BossEntry>();
                    foreach (var b in inst.GetProperty("bosses").EnumerateArray())
                    {
                        bosses.Add(new BossEntry
                        {
                            Entry = b.GetProperty("entry").GetInt32(),
                            Name = b.GetProperty("name").GetString() ?? "",
                            Order = b.GetProperty("order").GetInt32(),
                            Optional = b.TryGetProperty("optional", out var opt) && opt.GetBoolean()
                        });
                    }
                    _bossMap[mapId] = bosses;
                }
            }
            return _bossMap;
        }
    }

    // ===================== INSTANCE CREATURES =====================

    /// <summary>
    /// GET /Instances/Creatures?mapId=409&showTrash=false
    /// Returns bosses from curated list + optionally trash mobs from DB.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Creatures(int mapId, bool showTrash = false,
        [FromServices] IWebHostEnvironment env = null!)
    {
        using var conn = _db.Mangos();
        var bossMap = GetBossMap(env);
        var inst = INSTANCES.FirstOrDefault(i => i.MapId == mapId);

        // Get curated boss entries for this instance
        var bossList = bossMap.GetValueOrDefault(mapId, new List<BossEntry>());
        var bossEntryIds = bossList.Select(b => b.Entry).ToHashSet();

        // Query boss creatures from DB (to get loot_id, level, rank, loot row count)
        var bossCreatures = new List<dynamic>();
        if (bossEntryIds.Count > 0)
        {
            bossCreatures = (await conn.QueryAsync<dynamic>(
                @"SELECT ct.entry, ct.name, ct.rank, ct.level_min, ct.level_max, ct.loot_id,
                         (SELECT COUNT(*) FROM creature_loot_template clt WHERE clt.entry = ct.loot_id) AS lootRowCount
                  FROM creature_template ct
                  WHERE ct.entry IN @Ids
                    AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)",
                new { Ids = bossEntryIds.ToArray() }
            )).ToList();
        }

        // Merge with curated data (order, optional flag) and sort by curated order
        var bossResults = bossList.Select(b =>
        {
            var db = bossCreatures.FirstOrDefault(c => (int)c.entry == b.Entry);
            return new
            {
                entry = b.Entry,
                name = db != null ? (string)db.name : b.Name,
                rank = db != null ? (int)db.rank : 1,
                level_min = db != null ? (int)db.level_min : 0,
                level_max = db != null ? (int)db.level_max : 0,
                loot_id = db != null ? (int)db.loot_id : 0,
                lootRowCount = db != null ? (int)db.lootRowCount : 0,
                isBoss = true,
                optional = b.Optional,
                order = b.Order
            };
        }).OrderBy(b => b.order).ToList();

        // Optionally get trash mobs (everything in the instance NOT in the boss list)
        var trashResults = new List<dynamic>();
        if (showTrash)
        {
            trashResults = (await conn.QueryAsync<dynamic>(
                @"SELECT DISTINCT ct.entry, ct.name, ct.rank, ct.level_min, ct.level_max, ct.loot_id,
                         (SELECT COUNT(*) FROM creature_loot_template clt WHERE clt.entry = ct.loot_id) AS lootRowCount
                  FROM creature_template ct
                  JOIN creature c ON c.id = ct.entry
                  WHERE c.map = @MapId AND ct.loot_id > 0
                    AND ct.entry NOT IN @BossIds
                    AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)
                  ORDER BY ct.level_min DESC, ct.name",
                new { MapId = mapId, BossIds = bossEntryIds.Count > 0 ? bossEntryIds.ToArray() : new[] { -1 } }
            )).ToList();
        }

        return Json(new
        {
            mapId,
            instanceName = inst?.Name ?? $"Map {mapId}",
            category = inst?.Category ?? "unknown",
            levelRange = inst?.LevelRange ?? "",
            bosses = bossResults,
            trash = trashResults
        });
    }

    // ===================== CREATURE LOOT TABLE =====================

    /// <summary>
    /// GET /Instances/Loot?creatureEntry=11502
    /// Returns the full loot table for a creature, with references expanded.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Loot(int creatureEntry)
    {
        using var conn = _db.Mangos();

        // Get loot_id from creature_template
        var ct = await conn.QueryFirstOrDefaultAsync<dynamic>(
            @"SELECT entry, name, rank, level_min, level_max, loot_id
              FROM creature_template WHERE entry = @Entry
              ORDER BY patch DESC LIMIT 1",
            new { Entry = creatureEntry });

        if (ct == null)
            return Json(new { found = false });

        int lootId = (int)(ct.loot_id ?? 0);
        if (lootId == 0)
            return Json(new { found = true, creature = ct, directItems = new object[0], referenceGroups = new object[0] });

        // Get all rows from the loot table
        var allRows = (await conn.QueryAsync<dynamic>(
            @"SELECT entry, item, ChanceOrQuestChance AS chance, groupid, mincountOrRef, maxcount,
                     patch_min AS patchMin, patch_max AS patchMax
              FROM creature_loot_template
              WHERE entry = @LootId
              ORDER BY groupid, ChanceOrQuestChance DESC",
            new { LootId = lootId })).ToList();

        // Separate direct items from reference pointers
        var directItemIds = allRows
            .Where(r => (int)r.mincountOrRef > 0)
            .Select(r => (int)r.item)
            .Distinct().ToList();

        var refPointers = allRows
            .Where(r => (int)r.mincountOrRef < 0)
            .ToList();

        // Resolve direct items
        var directItems = new List<object>();
        if (directItemIds.Count > 0)
        {
            var items = (await conn.QueryAsync<dynamic>(
                @"SELECT entry, name, quality, class, display_id, inventory_type, required_level
                  FROM item_template
                  WHERE entry IN @Ids
                    AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = item_template.entry)",
                new { Ids = directItemIds }
            )).ToDictionary(i => (int)i.entry);

            foreach (var row in allRows.Where(r => (int)r.mincountOrRef > 0))
            {
                int itemId = (int)row.item;
                var item = items.GetValueOrDefault(itemId);
                uint displayId = item != null ? (uint)(item.display_id ?? 0) : 0;

                directItems.Add(new
                {
                    itemEntry = itemId,
                    itemName = item != null ? (string)item.name : $"Item #{itemId}",
                    quality = item != null ? (int)item.quality : 0,
                    displayId,
                    iconPath = _dbc.GetItemIconPath(displayId),
                    chance = (float)row.chance,
                    minCount = (int)row.mincountOrRef,
                    maxCount = (int)row.maxcount,
                    groupId = (int)row.groupid,
                    patchMin = (int)row.patchMin,
                    patchMax = (int)row.patchMax,
                    isQuest = (float)row.chance < 0,
                    source = "direct"
                });
            }
        }

        // Resolve reference loot tables
        var referenceGroups = new List<object>();
        foreach (var refRow in refPointers)
        {
            int refEntry = Math.Abs((int)refRow.mincountOrRef);
            float refChance = (float)refRow.chance;
            int refMaxCount = (int)refRow.maxcount;

            // Get items from the reference table
            var refItems = await conn.QueryAsync<dynamic>(
                @"SELECT rlt.item, rlt.ChanceOrQuestChance AS chance, rlt.mincountOrRef, rlt.maxcount, rlt.groupid,
                         it.name, it.quality, it.display_id, it.inventory_type
                  FROM reference_loot_template rlt
                  JOIN item_template it ON it.entry = rlt.item
                    AND it.patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = it.entry)
                  WHERE rlt.entry = @RefEntry AND rlt.mincountOrRef > 0
                  ORDER BY rlt.ChanceOrQuestChance DESC, it.quality DESC",
                new { RefEntry = refEntry });

            var resolvedItems = refItems.Select(ri =>
            {
                uint did = (uint)(ri.display_id ?? 0);
                return new
                {
                    itemEntry = (int)ri.item,
                    itemName = (string)ri.name,
                    quality = (int)ri.quality,
                    displayId = did,
                    iconPath = _dbc.GetItemIconPath(did),
                    chance = (float)ri.chance,
                    minCount = (int)ri.mincountOrRef,
                    maxCount = (int)ri.maxcount,
                    groupId = (int)ri.groupid,
                    source = "reference"
                };
            }).ToList();

            referenceGroups.Add(new
            {
                refEntry,
                refChance,
                refMaxCount,
                patchMin = (int)refRow.patchMin,
                patchMax = (int)refRow.patchMax,
                // Pointer row identity — needed to UPDATE the pointer itself
                pointerItem = (int)refRow.item,
                pointerGroupId = (int)refRow.groupid,
                pointerLootEntry = (int)refRow.entry,
                itemCount = resolvedItems.Count,
                items = resolvedItems
            });
        }

        return Json(new
        {
            found = true,
            creature = ct,
            lootId,
            directItems,
            referenceGroups,
            totalDirectItems = directItems.Count,
            totalRefGroups = referenceGroups.Count
        });
    }

    // ===================== UPDATE LOOT ROW =====================

    /// <summary>
    /// POST /Instances/UpdateLoot — Update a single loot row's chance and/or count.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> UpdateLoot([FromBody] LootRowUpdate update)
    {
        var tableName = update.source == "reference" ? "reference_loot_template" : "creature_loot_template";

        using var conn = _db.Mangos();

        // Capture before state
        var before = await conn.QueryFirstOrDefaultAsync<dynamic>(
            $@"SELECT * FROM `{tableName}`
               WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                 AND patch_min = @PatchMin AND patch_max = @PatchMax",
            new { update.entry, update.item, update.groupId, update.patchMin, update.patchMax });

        if (before == null)
            return Json(new { success = false, error = "Loot row not found" });

        // Build update
        var setClauses = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("Entry", update.entry);
        parameters.Add("Item", update.item);
        parameters.Add("GroupId", update.groupId);
        parameters.Add("PatchMin", update.patchMin);
        parameters.Add("PatchMax", update.patchMax);

        if (update.newChance.HasValue)
        {
            setClauses.Add("ChanceOrQuestChance = @NewChance");
            parameters.Add("NewChance", update.newChance.Value);
        }

        if (update.newMaxCount.HasValue)
        {
            setClauses.Add("maxcount = @NewMaxCount");
            parameters.Add("NewMaxCount", update.newMaxCount.Value);
        }

        if (update.newMinCount.HasValue)
        {
            setClauses.Add("mincountOrRef = @NewMinCount");
            parameters.Add("NewMinCount", update.newMinCount.Value);
        }

        if (setClauses.Count == 0)
            return Json(new { success = false, error = "No changes specified" });

        var sql = $@"UPDATE `{tableName}`
                     SET {string.Join(", ", setClauses)}
                     WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                       AND patch_min = @PatchMin AND patch_max = @PatchMax";

        await conn.ExecuteAsync(sql, parameters);

        // Audit
        var after = await conn.QueryFirstOrDefaultAsync<dynamic>(
            $@"SELECT * FROM `{tableName}`
               WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                 AND patch_min = @PatchMin AND patch_max = @PatchMax",
            new { update.entry, update.item, update.groupId, update.patchMin, update.patchMax });

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "loot_edit",
            TargetType = "loot_row",
            TargetName = update.itemName ?? $"Item #{update.item}",
            TargetId = update.item,
            StateBefore = JsonSerializer.Serialize((IDictionary<string, object>)before),
            StateAfter = after != null ? JsonSerializer.Serialize((IDictionary<string, object>)after) : null,
            IsReversible = true,
            Success = true,
            Notes = $"Edited loot in {tableName}: entry={update.entry}, item={update.item}"
        });

        return Json(new { success = true });
    }

    // ===================== BULK MULTIPLIER PER CREATURE =====================

    /// <summary>
    /// POST /Instances/MultiplyCreatureLoot — Apply multiplier to all loot for a creature.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> MultiplyCreatureLoot([FromBody] CreatureLootMultiply request)
    {
        if (request.multiplier <= 0 || request.multiplier > 100)
            return Json(new { success = false, error = "Multiplier must be between 0.01 and 100" });

        using var conn = _db.Mangos();

        // Get loot_id
        var lootId = await conn.ExecuteScalarAsync<int?>(
            "SELECT loot_id FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = request.creatureEntry });

        if (!lootId.HasValue || lootId.Value == 0)
            return Json(new { success = false, error = "Creature has no loot table" });

        // Get all direct item rows (not references)
        var rows = await conn.QueryAsync<dynamic>(
            @"SELECT entry, item, ChanceOrQuestChance AS chance, groupid, patch_min, patch_max
              FROM creature_loot_template
              WHERE entry = @LootId AND mincountOrRef > 0 AND ABS(ChanceOrQuestChance) < 100",
            new { LootId = lootId.Value });

        var rowList = rows.ToList();
        if (rowList.Count == 0)
            return Json(new { success = true, totalUpdated = 0 });

        // Capture before state
        var beforeState = rowList.Select(r => new
        {
            item = (int)r.item,
            chance = (float)r.chance
        }).ToList();

        int updated = 0;
        foreach (var row in rowList)
        {
            float oldChance = (float)row.chance;
            float newChance = oldChance < 0
                ? Math.Max(-100f, oldChance * request.multiplier)
                : Math.Min(100f, oldChance * request.multiplier);
            newChance = (float)Math.Round(newChance, 4);

            await conn.ExecuteAsync(
                @"UPDATE creature_loot_template
                  SET ChanceOrQuestChance = @NewChance
                  WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                    AND patch_min = @PatchMin AND patch_max = @PatchMax",
                new
                {
                    NewChance = newChance,
                    Entry = (int)row.entry,
                    Item = (int)row.item,
                    GroupId = (int)row.groupid,
                    PatchMin = (int)row.patch_min,
                    PatchMax = (int)row.patch_max
                });
            updated++;
        }

        // Get creature name for audit
        var creatureName = await conn.ExecuteScalarAsync<string>(
            "SELECT name FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = request.creatureEntry }) ?? $"Creature #{request.creatureEntry}";

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "loot_multiply_creature",
            TargetType = "creature_loot",
            TargetName = creatureName,
            TargetId = request.creatureEntry,
            StateBefore = JsonSerializer.Serialize(beforeState),
            StateAfter = JsonSerializer.Serialize(new { multiplier = request.multiplier, updated }),
            IsReversible = true,
            Success = true,
            Notes = $"Applied {request.multiplier}x to {creatureName}'s loot ({updated} rows)"
        });

        return Json(new { success = true, totalUpdated = updated, creatureName });
    }


    // ===================== ADD ITEM TO LOOT TABLE =====================

    /// <summary>
    /// POST /Instances/AddLootItem — Insert a new item into a creature's loot table.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AddLootItem([FromBody] AddLootItemRequest request)
    {
        using var conn = _db.Mangos();

        // Determine which table and entry to insert into
        string tableName;
        int lootEntry;

        if (request.refEntry.HasValue && request.refEntry.Value > 0)
        {
            // Insert into a reference loot table
            tableName = "reference_loot_template";
            lootEntry = request.refEntry.Value;
        }
        else
        {
            // Insert into the creature's direct loot table
            tableName = "creature_loot_template";
            var lootId = await conn.ExecuteScalarAsync<int?>(
                "SELECT loot_id FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = request.creatureEntry });

            if (!lootId.HasValue || lootId.Value == 0)
                return Json(new { success = false, error = "Creature has no loot table" });

            lootEntry = lootId.Value;
        }

        // Check if this item already exists in the target table+group
        var exists = await conn.ExecuteScalarAsync<int>(
            $@"SELECT COUNT(*) FROM `{tableName}`
                  WHERE entry = @LootEntry AND item = @Item AND groupid = @GroupId",
            new { LootEntry = lootEntry, Item = request.itemEntry, GroupId = request.groupId });

        if (exists > 0)
            return Json(new { success = false, error = "Item already exists in this loot table (same group)" });

        // Insert the new loot row
        await conn.ExecuteAsync(
            $@"INSERT INTO `{tableName}`
                    (entry, item, ChanceOrQuestChance, groupid, mincountOrRef, maxcount, patch_min, patch_max,
                     condition_id)
                  VALUES
                    (@LootEntry, @Item, @Chance, @GroupId, @MinCount, @MaxCount, @PatchMin, @PatchMax, 0)",
            new
            {
                LootEntry = lootEntry,
                Item = request.itemEntry,
                Chance = request.chance,
                GroupId = request.groupId,
                MinCount = request.minCount,
                MaxCount = request.maxCount,
                PatchMin = request.patchMin,
                PatchMax = request.patchMax
            });

        // Get item name for audit
        var itemName = await conn.ExecuteScalarAsync<string>(
            "SELECT name FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = request.itemEntry }) ?? $"Item #{request.itemEntry}";

        var creatureName = await conn.ExecuteScalarAsync<string>(
            "SELECT name FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = request.creatureEntry }) ?? $"Creature #{request.creatureEntry}";

        var targetLabel = request.refEntry.HasValue ? $"ref pool #{request.refEntry.Value}" : "direct loot";

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "loot_add_item",
            TargetType = request.refEntry.HasValue ? "reference_loot" : "creature_loot",
            TargetName = $"{creatureName} → {itemName} ({targetLabel})",
            TargetId = request.creatureEntry,
            StateAfter = JsonSerializer.Serialize(new
            {
                table = tableName,
                lootEntry,
                item = request.itemEntry,
                chance = request.chance,
                groupId = request.groupId,
                minCount = request.minCount,
                maxCount = request.maxCount
            }),
            IsReversible = true,
            Success = true,
            Notes = $"Added {itemName} (#{request.itemEntry}) to {tableName} entry={lootEntry} at {request.chance}%"
        });

        return Json(new { success = true, itemName, creatureName, lootEntry, tableName });
    }

    // ===================== REMOVE ITEM FROM LOOT TABLE =====================

    /// <summary>
    /// POST /Instances/RemoveLootItem — Delete an item from a creature's loot table.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RemoveLootItem([FromBody] RemoveLootItemRequest request)
    {
        var tableName = request.source == "reference" ? "reference_loot_template" : "creature_loot_template";

        using var conn = _db.Mangos();

        // Capture before state
        var before = await conn.QueryFirstOrDefaultAsync<dynamic>(
            $@"SELECT * FROM `{tableName}`
                   WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                     AND patch_min = @PatchMin AND patch_max = @PatchMax",
            new { request.entry, request.item, request.groupId, request.patchMin, request.patchMax });

        if (before == null)
            return Json(new { success = false, error = "Loot row not found" });

        await conn.ExecuteAsync(
            $@"DELETE FROM `{tableName}`
                   WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                     AND patch_min = @PatchMin AND patch_max = @PatchMax",
            new { request.entry, request.item, request.groupId, request.patchMin, request.patchMax });

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "loot_remove_item",
            TargetType = "loot_row",
            TargetName = request.itemName ?? $"Item #{request.item}",
            TargetId = request.item,
            StateBefore = JsonSerializer.Serialize((IDictionary<string, object>)before),
            IsReversible = true,
            Success = true,
            Notes = $"Removed item #{request.item} from {tableName} entry={request.entry}"
        });

        return Json(new { success = true });
    }

    // ===================== LIGHTWEIGHT ITEM SEARCH (for picker) =====================

    /// <summary>
    /// GET /Instances/SearchItems?q=thunderfury&page=1&pageSize=30
    /// Lightweight item search for the add-item picker modal.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> SearchItems(string? q, int? qualityFilter, int page = 1, int pageSize = 30)
    {
        using var conn = _db.Mangos();

        var where = "WHERE patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = item_template.entry)";
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(q))
        {
            if (uint.TryParse(q.Trim(), out var entryId))
            {
                where += " AND entry = @EntryId";
                parameters.Add("EntryId", entryId);
            }
            else
            {
                where += " AND name LIKE @Search";
                parameters.Add("Search", $"%{q.Trim()}%");
            }
        }

        if (qualityFilter.HasValue)
        {
            where += " AND quality = @Quality";
            parameters.Add("Quality", qualityFilter.Value);
        }

        var countSql = $"SELECT COUNT(*) FROM item_template {where}";
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters);

        var offset = (page - 1) * pageSize;
        parameters.Add("Offset", offset);
        parameters.Add("PageSize", pageSize);

        var dataSql = $@"
                SELECT entry, name, quality, display_id AS displayId,
                       inventory_type AS inventoryType, item_level AS itemLevel,
                       required_level AS requiredLevel, class, subclass
                FROM item_template {where}
                ORDER BY quality DESC, item_level DESC, entry ASC
                LIMIT @PageSize OFFSET @Offset";

        var items = (await conn.QueryAsync<dynamic>(dataSql, parameters)).ToList();

        var iconMap = new Dictionary<uint, string>();
        foreach (var item in items)
        {
            uint did = (uint)(item.displayId ?? 0);
            if (did > 0 && !iconMap.ContainsKey(did))
                iconMap[did] = _dbc.GetItemIconPath(did);
        }

        return Json(new
        {
            items,
            icons = iconMap,
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        });
    }

    // ===================== GROUP INFO (for percentage balancing) =====================

    /// <summary>
    /// GET /Instances/GroupInfo?lootEntry=12345&groupId=1&source=direct
    /// Returns all items in a specific group with their chances, for percentage balancing UI.
    /// source=direct → creature_loot_template, source=reference → reference_loot_template
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GroupInfo(int lootEntry, int groupId, string source = "direct")
    {
        using var conn = _db.Mangos();
        var tableName = source == "reference" ? "reference_loot_template" : "creature_loot_template";

        var items = await conn.QueryAsync<dynamic>(
            $@"SELECT t.item, t.ChanceOrQuestChance AS chance, t.maxcount,
                         it.name, it.quality, it.display_id AS displayId
                  FROM `{tableName}` t
                  LEFT JOIN item_template it ON it.entry = t.item
                    AND it.patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = it.entry)
                  WHERE t.entry = @LootEntry AND t.groupid = @GroupId AND t.mincountOrRef > 0
                  ORDER BY ABS(t.ChanceOrQuestChance) DESC",
            new { LootEntry = lootEntry, GroupId = groupId });

        var itemList = items.ToList();
        float totalExplicit = 0;
        int zeroCount = 0;
        foreach (var item in itemList)
        {
            float abs = Math.Abs((float)item.chance);
            if (abs > 0) totalExplicit += abs;
            else zeroCount++;
        }

        var resolvedItems = itemList.Select(i =>
        {
            uint did = (uint)(i.displayId ?? 0);
            float abs = Math.Abs((float)i.chance);
            float effective = abs > 0 ? abs : (zeroCount > 0 ? Math.Max(0, 100 - totalExplicit) / zeroCount : 0);
            return new
            {
                itemEntry = (int)i.item,
                itemName = (string)(i.name ?? $"Item #{i.item}"),
                quality = (int)(i.quality ?? 0),
                iconPath = _dbc.GetItemIconPath(did),
                chance = (float)i.chance,
                effectiveChance = effective,
                maxCount = (int)i.maxcount
            };
        });

        return Json(new
        {
            items = resolvedItems,
            totalExplicit,
            zeroCount,
            remaining = Math.Max(0, 100 - totalExplicit)
        });
    }
}

// ── DTOs ──────────────────────────────────────────────────────

public record InstanceInfo(int MapId, string Name, string Category, string LevelRange);

public class LootRowUpdate
{
    public int entry { get; set; }
    public int item { get; set; }
    public int groupId { get; set; }
    public int patchMin { get; set; }
    public int patchMax { get; set; }
    public float? newChance { get; set; }
    public int? newMaxCount { get; set; }
    public int? newMinCount { get; set; }
    public string? itemName { get; set; }
    public string source { get; set; } = "direct";
}

public class CreatureLootMultiply
{
    public int creatureEntry { get; set; }
    public float multiplier { get; set; } = 1.0f;
}

public class BossEntry
{
    public int Entry { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public bool Optional { get; set; }
}

public class AddLootItemRequest
{
    public int creatureEntry { get; set; }
    public int itemEntry { get; set; }
    public float chance { get; set; }
    public int groupId { get; set; } = 0;
    public int minCount { get; set; } = 1;
    public int maxCount { get; set; } = 1;
    public int patchMin { get; set; } = 0;
    public int patchMax { get; set; } = 10;
    public int? refEntry { get; set; }  // If set, insert into reference_loot_template instead
}

public class RemoveLootItemRequest
{
    public int entry { get; set; }
    public int item { get; set; }
    public int groupId { get; set; }
    public int patchMin { get; set; }
    public int patchMax { get; set; }
    public string? itemName { get; set; }
    public string source { get; set; } = "direct";
}