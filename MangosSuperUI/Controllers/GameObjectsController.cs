using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text.Json;

namespace MangosSuperUI.Controllers;

public class GameObjectsController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;
    private readonly IWebHostEnvironment _env;
    private readonly GameObjectModelService _goModels;

    // Custom objects start at this entry ID
    private const int CUSTOM_RANGE_START = 900000;

    // Columns we allow editing on gameobject_template.
    // Matches the actual column names in the VMaNGOS schema.
    private static readonly string[] EDITABLE_COLUMNS = new[]
    {
        "name", "type", "displayId", "icon", "faction", "flags", "size",
        "data0", "data1", "data2", "data3", "data4", "data5", "data6", "data7",
        "data8", "data9", "data10", "data11", "data12", "data13", "data14", "data15",
        "data16", "data17", "data18", "data19", "data20", "data21", "data22", "data23"
    };

    public GameObjectsController(ConnectionFactory db, DbcService dbc, AuditService audit,
        IWebHostEnvironment env, GameObjectModelService goModels)
    {
        _db = db;
        _dbc = dbc;
        _audit = audit;
        _env = env;
        _goModels = goModels;
    }

    public IActionResult Index() => View();

    // ===================== SEARCH (existing, unchanged) =====================

    /// <summary>
    /// GET /GameObjects/Search?q=statue&typeFilter=22&page=1&pageSize=50
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search(string? q, int? typeFilter, bool customOnly = false, int page = 1, int pageSize = 50)
    {
        using var conn = _db.Mangos();

        var where = "WHERE patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)";
        var parameters = new DynamicParameters();

        if (customOnly)
        {
            where += " AND entry >= @CustomStart";
            parameters.Add("CustomStart", CUSTOM_RANGE_START);
        }

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

        if (typeFilter.HasValue)
        {
            where += " AND type = @Type";
            parameters.Add("Type", typeFilter.Value);
        }

        // Count
        var countSql = $"SELECT COUNT(*) FROM gameobject_template {where}";
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters);

        // Page
        var offset = (page - 1) * pageSize;
        parameters.Add("Offset", offset);
        parameters.Add("PageSize", pageSize);

        var dataSql = $@"
            SELECT entry, type, displayId, name, faction, flags, size,
                   data0, data1, data2, data3, data4, data5, data6,
                   data7, data8, data9, data10, data11, data12
            FROM gameobject_template {where}
            ORDER BY entry ASC
            LIMIT @PageSize OFFSET @Offset";

        var objects = (await conn.QueryAsync<dynamic>(dataSql, parameters)).ToList();

        // Models are generated on demand from the client MPQs (Detail /
        // ModelExists trigger generation). Here we only flag which display
        // ids CAN have a model — cheap DBC lookup, no generation, so the
        // list page stays fast.
        var modelMap = new Dictionary<uint, string>();
        foreach (var obj in objects)
        {
            uint did = (uint)(obj.displayId ?? 0);
            // Value is the cached GLB path when already generated, or the
            // marker "ondemand" (frontend only checks truthiness).
            if (did > 0 && !modelMap.ContainsKey(did) && _goModels.HasModel(did))
                modelMap[did] = _goModels.TryGetCachedWebPath(did) ?? "ondemand";
        }

        return Json(new
        {
            objects,
            models = modelMap,
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        });
    }

    // ===================== DETAIL (existing, unchanged) =====================

    /// <summary>
    /// GET /GameObjects/Detail?entry=164882 — Full game object details.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Detail(int entry)
    {
        using var conn = _db.Mangos();

        var sql = @"
            SELECT *
            FROM gameobject_template
            WHERE entry = @Entry
            ORDER BY patch DESC
            LIMIT 1";

        var obj = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { Entry = entry });
        if (obj == null)
            return Json(new { found = false });

        // 3D model — generated on demand from the client MPQs if not cached
        uint displayId = (uint)(obj.displayId ?? 0);
        string? modelPath = _goModels.EnsureGlb(displayId);

        // Get spawn count
        var spawnCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM gameobject WHERE id = @Entry", new { Entry = entry });

        // Get spawn locations (first 10)
        var spawns = await conn.QueryAsync<dynamic>(@"
            SELECT guid, map, position_x AS x, position_y AS y, position_z AS z, orientation
            FROM gameobject
            WHERE id = @Entry
            LIMIT 10", new { Entry = entry });

        return Json(new
        {
            found = true,
            obj,
            modelPath,
            spawnCount,
            spawns,
            typeLabel = GetTypeLabel((int)(obj.type ?? 0)),
            dataLabels = GetDataLabels((int)(obj.type ?? 0))
        });
    }

    /// <summary>
    /// GET /GameObjects/ModelExists?displayId=6 — Quick check if a GLB exists.
    /// </summary>
    [HttpGet]
    public IActionResult ModelExists(uint displayId)
    {
        // Generates the GLB from the client MPQs on first request.
        var webPath = _goModels.EnsureGlb(displayId);
        return Json(new { exists = webPath != null, path = webPath });
    }

    /// <summary>
    /// GET /GameObjects/QuestName?questId=N — Resolves a quest ID to its name.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> QuestName(int questId)
    {
        using var conn = _db.Mangos();
        // VMaNGOS uses Title; try it, fall back gracefully
        try
        {
            var name = await conn.ExecuteScalarAsync<string>(
                "SELECT Title FROM quest_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = questId });
            return Json(new { questId, name });
        }
        catch
        {
            // Column might not be named Title — try name
            try
            {
                var name = await conn.ExecuteScalarAsync<string>(
                    "SELECT name FROM quest_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                    new { Entry = questId });
                return Json(new { questId, name });
            }
            catch
            {
                return Json(new { questId, name = (string?)null });
            }
        }
    }

    /// <summary>
    /// GET /GameObjects/CustomSummary — Returns all custom game objects grouped by type with key details.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> CustomSummary()
    {
        using var conn = _db.Mangos();

        var sql = @"
            SELECT entry, type, displayId, name, data0, data1, data2, data3
            FROM gameobject_template
            WHERE entry >= @CustomStart
              AND patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)
            ORDER BY type, name";

        var objects = (await conn.QueryAsync<dynamic>(sql, new { CustomStart = CUSTOM_RANGE_START })).ToList();

        // Resolve spell names for spell-related fields
        var spellIds = new HashSet<int>();
        foreach (var obj in objects)
        {
            int type = (int)(obj.type ?? 0);
            int d0 = (int)(obj.data0 ?? 0);
            int d1 = (int)(obj.data1 ?? 0);
            int d3 = (int)(obj.data3 ?? 0);

            // Spell Caster data0, Trap data3, Goober data10 (not in this select — just d0/d3)
            if (type == 22 && d0 > 0) spellIds.Add(d0);
            if (type == 6 && d3 > 0) spellIds.Add(d3);
            if (type == 10 && d0 > 0) spellIds.Add(d0); // goober lock, but data10 not fetched here
            if (type == 18 && d1 > 0) spellIds.Add(d1);
            if (type == 30 && (int)(obj.data2 ?? 0) > 0) spellIds.Add((int)obj.data2);
        }

        var spellNames = new Dictionary<int, string>();
        if (spellIds.Count > 0)
        {
            var spells = await conn.QueryAsync<dynamic>(
                "SELECT entry, name FROM spell_template WHERE entry IN @Ids AND build = (SELECT MAX(build) FROM spell_template st2 WHERE st2.entry = spell_template.entry)",
                new { Ids = spellIds.ToArray() });
            foreach (var sp in spells)
                spellNames[(int)sp.entry] = (string)sp.name;
        }

        // Get spawn counts
        var spawnCounts = new Dictionary<int, int>();
        if (objects.Count > 0)
        {
            var entries = objects.Select(o => (int)o.entry).ToArray();
            var counts = await conn.QueryAsync<dynamic>(
                "SELECT id, COUNT(*) AS cnt FROM gameobject WHERE id IN @Ids GROUP BY id",
                new { Ids = entries });
            foreach (var c in counts)
                spawnCounts[(int)c.id] = (int)c.cnt;
        }

        return Json(new
        {
            totalCount = objects.Count,
            objects,
            spellNames,
            spawnCounts
        });
    }

    // ===================== NEW — EDIT ENDPOINTS =====================

    /// <summary>
    /// GET /GameObjects/NextCustomId — returns the next available entry in the 900000+ range.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> NextCustomId()
    {
        using var conn = _db.Mangos();
        var maxEntry = await conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(entry) FROM gameobject_template WHERE entry >= @Start",
            new { Start = CUSTOM_RANGE_START });

        var nextId = (maxEntry ?? CUSTOM_RANGE_START - 1) + 1;
        return Json(new { nextId });
    }

    /// <summary>
    /// GET /GameObjects/FullRow?entry=164882 — returns ALL columns for a game object.
    /// Used to populate the edit form (both for cloning and editing).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> FullRow(int entry)
    {
        using var conn = _db.Mangos();

        var sql = @"SELECT * FROM gameobject_template
                    WHERE entry = @Entry
                    ORDER BY patch DESC LIMIT 1";

        var obj = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { Entry = entry });
        if (obj == null)
            return Json(new { found = false });

        uint displayId = (uint)(obj.displayId ?? 0);
        string? modelPath = _goModels.EnsureGlb(displayId);

        return Json(new { found = true, obj, modelPath });
    }

    /// <summary>
    /// POST /GameObjects/Save — Insert (clone) or update (edit) a game object template.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] JsonElement body)
    {
        if (!body.TryGetProperty("entry", out var entryProp))
            return Json(new { success = false, error = "Missing entry field" });

        int entry = entryProp.GetInt32();

        using var conn = _db.Mangos();

        // Check if this entry already exists
        var existing = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT entry, name FROM gameobject_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry });

        // Build state_before for audit
        string? stateBefore = null;
        if (existing != null)
        {
            var beforeRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM gameobject_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });
            if (beforeRow is IDictionary<string, object> beforeState)
                stateBefore = JsonSerializer.Serialize(beforeState);
        }

        bool isInsert = existing == null;
        bool isCustom = entry >= CUSTOM_RANGE_START;

        // Build parameter dictionary from the JSON body
        var parameters = new DynamicParameters();
        parameters.Add("Entry", entry);

        // For new objects, use patch=0 (custom content, no progressive patching)
        if (isInsert)
            parameters.Add("Patch", 0);

        foreach (var col in EDITABLE_COLUMNS)
        {
            if (body.TryGetProperty(col, out var val))
            {
                if (val.ValueKind == JsonValueKind.Null || val.ValueKind == JsonValueKind.Undefined)
                    parameters.Add(col, 0);
                else if (val.ValueKind == JsonValueKind.Number)
                {
                    // size is a float, data fields and most others are int
                    if (col == "size")
                        parameters.Add(col, val.GetDouble());
                    else
                        parameters.Add(col, val.GetInt32());
                }
                else if (val.ValueKind == JsonValueKind.String)
                    parameters.Add(col, val.GetString());
                else
                    parameters.Add(col, val.GetRawText());
            }
            else
            {
                // Default to 0 for missing numeric fields, empty for strings
                if (col == "name")
                    parameters.Add(col, "Custom Object");
                else if (col == "icon")
                    parameters.Add(col, "");
                else if (col == "size")
                    parameters.Add(col, 1.0);
                else
                    parameters.Add(col, 0);
            }
        }

        try
        {
            if (isInsert)
            {
                var columns = "entry, patch, " + string.Join(", ", EDITABLE_COLUMNS);
                var values = "@Entry, @Patch, " + string.Join(", ", EDITABLE_COLUMNS.Select(c => "@" + c));

                var insertSql = $"INSERT INTO gameobject_template ({columns}) VALUES ({values})";
                await conn.ExecuteAsync(insertSql, parameters);
            }
            else
            {
                // UPDATE existing — update the latest patch row
                var patch = await conn.ExecuteScalarAsync<int>(
                    "SELECT MAX(patch) FROM gameobject_template WHERE entry = @Entry",
                    new { Entry = entry });
                parameters.Add("Patch", patch);

                var setClauses = string.Join(", ", EDITABLE_COLUMNS.Select(c => $"{c} = @{c}"));
                var updateSql = $"UPDATE gameobject_template SET {setClauses} WHERE entry = @Entry AND patch = @Patch";
                await conn.ExecuteAsync(updateSql, parameters);
            }

            // Build state_after for audit
            var afterRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM gameobject_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });
            var stateAfter = afterRow != null
                ? JsonSerializer.Serialize((IDictionary<string, object>)afterRow)
                : null;

            string objName = "Unknown";
            if (body.TryGetProperty("name", out var nameProp))
                objName = nameProp.GetString() ?? "Unknown";

            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "content",
                Action = isInsert ? "gameobject_create" : "gameobject_edit",
                TargetType = isCustom ? "gameobject_custom" : "gameobject_base_game",
                TargetName = objName,
                TargetId = entry,
                StateBefore = stateBefore,
                StateAfter = stateAfter,
                IsReversible = true,
                Success = true,
                Notes = isInsert
                    ? $"Created custom game object #{entry}"
                    : (isCustom ? $"Edited custom game object #{entry}" : $"Edited base game object #{entry}")
            });

            return Json(new { success = true, entry, isInsert });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /GameObjects/Delete?entry=N — Delete a custom game object (900000+ only).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Delete(int entry)
    {
        if (entry < CUSTOM_RANGE_START)
            return Json(new { success = false, error = "Cannot delete base game objects" });

        using var conn = _db.Mangos();

        var beforeRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM gameobject_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry });

        if (beforeRow == null)
            return Json(new { success = false, error = "Object not found" });

        string stateBefore = JsonSerializer.Serialize((IDictionary<string, object>)beforeRow);
        string objName = (string)(beforeRow.name ?? "Unknown");

        await conn.ExecuteAsync("DELETE FROM gameobject_template WHERE entry = @Entry", new { Entry = entry });

        // Also clean up any spawns for this custom object
        var spawnsDeleted = await conn.ExecuteAsync("DELETE FROM gameobject WHERE id = @Entry", new { Entry = entry });

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "gameobject_delete",
            TargetType = "gameobject_custom",
            TargetName = objName,
            TargetId = entry,
            StateBefore = stateBefore,
            IsReversible = false,
            Success = true,
            Notes = $"Deleted custom game object #{entry}" + (spawnsDeleted > 0 ? $" (also removed {spawnsDeleted} spawns)" : "")
        });

        return Json(new { success = true, spawnsDeleted });
    }

    // ── Type labels and data field labels ─────────────────────────────────

    private static string GetTypeLabel(int type) => type switch
    {
        0 => "Door",
        1 => "Button",
        2 => "Quest Giver",
        3 => "Chest",
        5 => "Generic / Decoration",
        6 => "Trap",
        7 => "Chair",
        8 => "Spell Focus",
        9 => "Text",
        10 => "Goober (Clickable)",
        11 => "Transport",
        13 => "Camera",
        14 => "Map Object",
        15 => "MO Transport",
        17 => "Fishing Node",
        18 => "Ritual",
        19 => "Mailbox",
        20 => "Auction House",
        22 => "Spell Caster",
        23 => "Meeting Stone",
        24 => "Flag Stand",
        25 => "Fishing Hole",
        26 => "Flag Drop",
        29 => "Capture Point",
        30 => "Aura Generator",
        31 => "Dungeon Difficulty",
        _ => $"Type {type}"
    };

    /// <summary>
    /// Returns human-readable labels for data0-data23 based on game object type.
    /// Complete definitions from the CMaNGOS wiki — authoritative reference for VMaNGOS.
    /// </summary>
    private static Dictionary<string, string> GetDataLabels(int type) => type switch
    {
        0 => new() // DOOR
        {
            ["data0"] = "Start Open",
            ["data1"] = "Lock ID",
            ["data2"] = "Auto Close Time",
            ["data3"] = "No Damage Immune",
            ["data4"] = "Open Text ID",
            ["data5"] = "Close Text ID"
        },
        1 => new() // BUTTON
        {
            ["data0"] = "Start Open",
            ["data1"] = "Lock ID",
            ["data2"] = "Auto Close Time",
            ["data3"] = "Linked Trap",
            ["data4"] = "No Damage Immune",
            ["data5"] = "Large",
            ["data6"] = "Open Text ID",
            ["data7"] = "Close Text ID",
            ["data8"] = "LOS OK"
        },
        2 => new() // QUESTGIVER
        {
            ["data0"] = "Lock ID",
            ["data1"] = "Quest List",
            ["data2"] = "Page Material",
            ["data3"] = "Gossip ID",
            ["data4"] = "Custom Anim",
            ["data5"] = "No Damage Immune",
            ["data6"] = "Open Text ID",
            ["data7"] = "LOS OK",
            ["data8"] = "Allow Mounted",
            ["data9"] = "Large"
        },
        3 => new() // CHEST
        {
            ["data0"] = "Lock ID",
            ["data1"] = "Loot Template",
            ["data2"] = "Restock Time (sec)",
            ["data3"] = "Consumable",
            ["data4"] = "Min Restock",
            ["data5"] = "Max Restock",
            ["data6"] = "Looted Event",
            ["data7"] = "Linked Trap",
            ["data8"] = "Quest ID",
            ["data9"] = "Min Level",
            ["data10"] = "LOS OK",
            ["data11"] = "Leave Loot",
            ["data12"] = "Not In Combat",
            ["data13"] = "Log Loot",
            ["data14"] = "Open Text ID",
            ["data15"] = "Group Loot Rules"
        },
        5 => new() // GENERIC
        {
            ["data0"] = "Floating Tooltip",
            ["data1"] = "Highlight",
            ["data2"] = "Server Only",
            ["data3"] = "Large",
            ["data4"] = "Float On Water",
            ["data5"] = "Quest ID"
        },
        6 => new() // TRAP
        {
            ["data0"] = "Lock ID",
            ["data1"] = "Level",
            ["data2"] = "Diameter",
            ["data3"] = "Spell ID",
            ["data4"] = "Charges",
            ["data5"] = "Cooldown (sec)",
            ["data6"] = "Auto Close",
            ["data7"] = "Start Delay",
            ["data8"] = "Server Only",
            ["data9"] = "Stealthed",
            ["data10"] = "Large",
            ["data11"] = "Stealth Affected",
            ["data12"] = "Open Text ID"
        },
        7 => new() // CHAIR
        {
            ["data0"] = "Chair Slots",
            ["data1"] = "Chair Orientation"
        },
        8 => new() // SPELL_FOCUS
        {
            ["data0"] = "Spell Focus Type",
            ["data1"] = "Diameter",
            ["data2"] = "Linked Trap",
            ["data3"] = "Server Only",
            ["data4"] = "Quest ID",
            ["data5"] = "Large"
        },
        9 => new() // TEXT
        {
            ["data0"] = "Page ID",
            ["data1"] = "Language",
            ["data2"] = "Page Material"
        },
        10 => new() // GOOBER
        {
            ["data0"] = "Lock ID",
            ["data1"] = "Quest ID",
            ["data2"] = "Event ID",
            ["data3"] = "Auto Close",
            ["data4"] = "Custom Anim",
            ["data5"] = "Consumable",
            ["data6"] = "Cooldown (sec)",
            ["data7"] = "Page ID",
            ["data8"] = "Language",
            ["data9"] = "Page Material",
            ["data10"] = "Spell ID",
            ["data11"] = "No Damage Immune",
            ["data12"] = "Linked Trap",
            ["data13"] = "Large",
            ["data14"] = "Open Text ID",
            ["data15"] = "Close Text ID",
            ["data16"] = "LOS OK"
        },
        13 => new() // CAMERA
        {
            ["data0"] = "Lock ID",
            ["data1"] = "Cinematic ID"
        },
        15 => new() // MO_TRANSPORT
        {
            ["data0"] = "Taxi Path ID",
            ["data1"] = "Move Speed",
            ["data2"] = "Accel Rate"
        },
        18 => new() // RITUAL
        {
            ["data0"] = "Required Casters",
            ["data1"] = "Spell ID",
            ["data2"] = "Anim Spell",
            ["data3"] = "Ritual Persistent",
            ["data4"] = "Caster Target Spell",
            ["data5"] = "Caster Target Spell Targets",
            ["data6"] = "Casters Grouped"
        },
        20 => new() // AUCTIONHOUSE
        {
            ["data0"] = "Auction House ID"
        },
        22 => new() // SPELLCASTER
        {
            ["data0"] = "Spell ID",
            ["data1"] = "Charges",
            ["data2"] = "Party Only"
        },
        23 => new() // MEETINGSTONE
        {
            ["data0"] = "Min Level",
            ["data1"] = "Max Level",
            ["data2"] = "Area ID"
        },
        24 => new() // FLAGSTAND
        {
            ["data0"] = "Lock ID",
            ["data1"] = "Pickup Spell",
            ["data2"] = "Radius",
            ["data3"] = "Return Aura",
            ["data4"] = "Return Spell",
            ["data5"] = "No Damage Immune",
            ["data6"] = "Open Text ID",
            ["data7"] = "LOS OK"
        },
        25 => new() // FISHINGHOLE
        {
            ["data0"] = "Radius",
            ["data1"] = "Loot Template",
            ["data2"] = "Min Restock",
            ["data3"] = "Max Restock"
        },
        26 => new() // FLAGDROP
        {
            ["data0"] = "Lock ID",
            ["data1"] = "Event ID",
            ["data2"] = "Pickup Spell",
            ["data3"] = "No Damage Immune"
        },
        29 => new() // CAPTURE_POINT
        {
            ["data0"] = "Radius",
            ["data1"] = "Spell",
            ["data2"] = "World State 1",
            ["data3"] = "World State 2",
            ["data4"] = "Win Event 1",
            ["data5"] = "Win Event 2",
            ["data6"] = "Contested Event 1",
            ["data7"] = "Contested Event 2",
            ["data8"] = "Progress Event 1",
            ["data9"] = "Progress Event 2",
            ["data10"] = "Neutral Event 1",
            ["data11"] = "Neutral Event 2",
            ["data12"] = "Neutral Percent",
            ["data13"] = "World State 3",
            ["data14"] = "Min Superiority",
            ["data15"] = "Max Superiority",
            ["data16"] = "Min Time (sec)",
            ["data17"] = "Max Time (sec)",
            ["data18"] = "Large"
        },
        30 => new() // AURA_GENERATOR
        {
            ["data0"] = "Start Open",
            ["data1"] = "Radius",
            ["data2"] = "Aura ID 1",
            ["data3"] = "Condition ID 1"
        },
        31 => new() // DUNGEON_DIFFICULTY
        {
            ["data0"] = "Map ID",
            ["data1"] = "Difficulty"
        },
        _ => new()
    };
}