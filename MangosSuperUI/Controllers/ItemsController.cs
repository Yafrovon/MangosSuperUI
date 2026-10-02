using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using MangosSuperUI.Services.SpellServices;
using Dapper;
using System.Text.Json;

namespace MangosSuperUI.Controllers;

public class ItemsController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;
    private readonly IWebHostEnvironment _env;
    private readonly ItemTextureService _itemTextures;
    private readonly ItemRetextureService _retexture;
    private readonly CharacterModelService _characterModels;
    private readonly BodyAtlasTextureService _bodyAtlas;
    private readonly MpqReaderService _mpq;
    private readonly PaletteSwapService _palette;
    private readonly ILogger<ItemsController> _logger;
    private readonly IConfiguration _config;
    private readonly VariationRecipeService _variations;
    private readonly ComfyUIUpscaler _upscaler;

    // Custom items start at this entry ID
    private const int CUSTOM_RANGE_START = 900000;

    // Columns we read/write for the full item row.
    // Matches item_template snake_case column names exactly.
    private static readonly string[] EDITABLE_COLUMNS = new[]
    {
        // Identity & display
        "name", "description", "class", "subclass", "quality", "display_id",
        "inventory_type", "flags",
        // Requirements
        "required_level", "item_level", "required_skill", "required_skill_rank",
        "required_spell", "required_honor_rank", "required_city_rank",
        "required_reputation_faction", "required_reputation_rank",
        "allowable_class", "allowable_race",
        // Economics & stacking
        "buy_price", "sell_price", "buy_count", "bonding", "stackable", "max_count",
        // Armor & resistances
        "armor", "block", "holy_res", "fire_res", "nature_res", "frost_res", "shadow_res", "arcane_res",
        // Weapon
        "dmg_min1", "dmg_max1", "dmg_type1", "dmg_min2", "dmg_max2", "dmg_type2",
        "dmg_min3", "dmg_max3", "dmg_type3", "dmg_min4", "dmg_max4", "dmg_type4",
        "dmg_min5", "dmg_max5", "dmg_type5",
        "delay", "range_mod", "ammo_type",
        // Stats
        "stat_type1", "stat_value1", "stat_type2", "stat_value2",
        "stat_type3", "stat_value3", "stat_type4", "stat_value4",
        "stat_type5", "stat_value5", "stat_type6", "stat_value6",
        "stat_type7", "stat_value7", "stat_type8", "stat_value8",
        "stat_type9", "stat_value9", "stat_type10", "stat_value10",
        // Spells (all 5 slots, all fields)
        "spellid_1", "spelltrigger_1", "spellcooldown_1", "spellcharges_1", "spellppmrate_1", "spellcategory_1", "spellcategorycooldown_1",
        "spellid_2", "spelltrigger_2", "spellcooldown_2", "spellcharges_2", "spellppmrate_2", "spellcategory_2", "spellcategorycooldown_2",
        "spellid_3", "spelltrigger_3", "spellcooldown_3", "spellcharges_3", "spellppmrate_3", "spellcategory_3", "spellcategorycooldown_3",
        "spellid_4", "spelltrigger_4", "spellcooldown_4", "spellcharges_4", "spellppmrate_4", "spellcategory_4", "spellcategorycooldown_4",
        "spellid_5", "spelltrigger_5", "spellcooldown_5", "spellcharges_5", "spellppmrate_5", "spellcategory_5", "spellcategorycooldown_5",
        // Physical properties
        "material", "sheath", "max_durability", "container_slots",
        // Misc
        "random_property", "set_id", "disenchant_id",
        "page_text", "page_language", "page_material",
        "start_quest", "lock_id",
        "area_bound", "map_bound", "duration", "bag_family",
        "food_type", "min_money_loot", "max_money_loot", "wrapped_gift",
        "extra_flags", "other_team_entry"
    };

    public ItemsController(ConnectionFactory db, DbcService dbc, AuditService audit,
        IWebHostEnvironment env, ItemTextureService itemTextures, ItemRetextureService retexture,
        CharacterModelService characterModels, BodyAtlasTextureService bodyAtlas,
        MpqReaderService mpq, PaletteSwapService palette, ILogger<ItemsController> logger,
        VariationRecipeService variations,
        ComfyUIUpscaler upscaler,
        IConfiguration config)
    {
        _db = db;
        _dbc = dbc;
        _audit = audit;
        _env = env;
        _itemTextures = itemTextures;
        _retexture = retexture;
        _characterModels = characterModels;
        _bodyAtlas = bodyAtlas;
        _mpq = mpq;
        _palette = palette;
        _logger = logger;
        _variations = variations;
        _upscaler = upscaler;
        _config = config;
    }

    public IActionResult Index() => View();

    // ===================== SEARCH =====================

    /// <summary>
    /// GET /Items/Search
    ///   q                 name substring, or an exact entry id if numeric
    ///   classFilter       item_template.class
    ///   subclassFilter    item_template.subclass  (weapon type / armor material)
    ///   qualityFilter     item_template.quality
    ///   inventoryTypeFilter   equip slot
    ///   minLevel/maxLevel     required_level range
    ///   minItemLevel/maxItemLevel  item_level range
    ///   customOnly        true = only entries in the custom range (900000+)
    ///   hasDisplay        true = only rows with a display_id (things with a visual)
    ///   sort              entry | name | quality | itemLevel | requiredLevel | dps
    ///   dir               asc | desc
    /// Server-side search with pagination.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search(string? q, int? classFilter, int? subclassFilter,
        int? qualityFilter, int? inventoryTypeFilter, int? minLevel, int? maxLevel,
        int? minItemLevel, int? maxItemLevel, bool? customOnly, bool? hasDisplay,
        string? sort = null, string? dir = null,
        int page = 1, int pageSize = 50)
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

        if (classFilter.HasValue)
        {
            where += " AND class = @Class";
            parameters.Add("Class", classFilter.Value);
        }

        if (subclassFilter.HasValue)
        {
            where += " AND subclass = @Subclass";
            parameters.Add("Subclass", subclassFilter.Value);
        }

        if (qualityFilter.HasValue)
        {
            where += " AND quality = @Quality";
            parameters.Add("Quality", qualityFilter.Value);
        }

        if (inventoryTypeFilter.HasValue)
        {
            where += " AND inventory_type = @InvType";
            parameters.Add("InvType", inventoryTypeFilter.Value);
        }

        if (minLevel.HasValue)
        {
            where += " AND required_level >= @MinLevel";
            parameters.Add("MinLevel", minLevel.Value);
        }

        if (maxLevel.HasValue)
        {
            where += " AND required_level <= @MaxLevel";
            parameters.Add("MaxLevel", maxLevel.Value);
        }

        if (minItemLevel.HasValue)
        {
            where += " AND item_level >= @MinIlvl";
            parameters.Add("MinIlvl", minItemLevel.Value);
        }

        if (maxItemLevel.HasValue)
        {
            where += " AND item_level <= @MaxIlvl";
            parameters.Add("MaxIlvl", maxItemLevel.Value);
        }

        if (customOnly == true)
        {
            where += " AND entry >= @CustomStart";
            parameters.Add("CustomStart", CUSTOM_RANGE_START);
        }

        if (hasDisplay == true)
            where += " AND display_id > 0";

        // Sort. Whitelisted — never interpolate a caller string into ORDER BY.
        var sortColumn = (sort ?? "entry").ToLowerInvariant() switch
        {
            "name" => "name",
            "quality" => "quality",
            "itemlevel" => "item_level",
            "requiredlevel" => "required_level",
            // Average weapon damage over swing speed. delay 0 would divide by
            // zero, so non-weapons fall to the bottom via the NULLIF guard.
            "dps" => "((dmg_min1 + dmg_max1) / 2) / (NULLIF(delay, 0) / 1000)",
            _ => "entry"
        };
        var sortDir = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        // Stable tiebreak so pagination can't repeat or drop rows.
        var orderBy = sortColumn == "entry" ? $"entry {sortDir}" : $"{sortColumn} {sortDir}, entry ASC";

        var countSql = $"SELECT COUNT(*) FROM item_template {where}";
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters);

        var offset = (page - 1) * pageSize;
        parameters.Add("Offset", offset);
        parameters.Add("PageSize", pageSize);

        var dataSql = $@"
            SELECT entry, name, class, subclass, quality, display_id AS displayId,
                   inventory_type AS inventoryType, required_level AS requiredLevel,
                   item_level AS itemLevel, description,
                   buy_price AS buyPrice, sell_price AS sellPrice,
                   bonding, stackable, max_count AS maxCount,
                   armor, block,
                   dmg_min1 AS dmgMin1, dmg_max1 AS dmgMax1, dmg_type1 AS dmgType1, delay,
                   stat_type1 AS statType1, stat_value1 AS statValue1,
                   stat_type2 AS statType2, stat_value2 AS statValue2,
                   stat_type3 AS statType3, stat_value3 AS statValue3,
                   stat_type4 AS statType4, stat_value4 AS statValue4,
                   stat_type5 AS statType5, stat_value5 AS statValue5,
                   spellid_1 AS spellId1, spelltrigger_1 AS spellTrigger1,
                   spellid_2 AS spellId2, spelltrigger_2 AS spellTrigger2
            FROM item_template {where}
            ORDER BY {orderBy}
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
            totalPages = (int)Math.Ceiling((double)totalCount / pageSize),
            sort = sortColumn,
            dir = sortDir
        });
    }

    // ===================== SOURCES (where does this item come from?) =========

    /// <summary>
    /// GET /Items/Sources?entry=19019
    /// Every known way to obtain the item: creature kills (including loot behind
    /// reference tables — most dungeon and raid drops), skinning, pickpocketing,
    /// chests and gathering nodes, container items, disenchanting, vendors,
    /// quests (reward, choice, objective, and items that start one), and crafting.
    ///
    /// Probes that fail on this schema are reported in `notes` rather than
    /// swallowed, so a missing source class is visible instead of looking like
    /// an item with no sources.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Sources(int entry)
    {
        if (entry <= 0) return Json(new { success = false, error = "entry required" });

        using var conn = _db.Mangos();
        try
        {
            var result = await ItemSourceResolver.ResolveAsync(conn, entry);
            return Json(new
            {
                success = result.Success,
                entry = result.Entry,
                totalCount = result.TotalCount,
                creatures = result.Creatures,
                objects = result.Objects,
                containers = result.Containers,
                vendors = result.Vendors,
                quests = result.Quests,
                crafted = result.Crafted,
                disenchant = result.Disenchant,
                other = result.Other,
                notes = result.Notes
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Items/Sources failed for entry {Entry}", entry);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ===================== DETAIL (existing, unchanged) =====================

    /// <summary>
    /// GET /Items/Detail?entry=19019 — Full item details for the detail panel.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Detail(int entry)
    {
        using var conn = _db.Mangos();

        var sql = @"
            SELECT *
            FROM item_template
            WHERE entry = @Entry
            ORDER BY patch DESC
            LIMIT 1";

        var item = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { Entry = entry });
        if (item == null)
            return Json(new { found = false });

        uint displayId = (uint)(item.display_id ?? 0);
        var iconPath = _dbc.GetItemIconPath(displayId);

        // Generate GLB on demand from MPQ (falls back to pre-extracted GLB if it exists)
        string? modelPath = _itemTextures.EnsureGlb(displayId);

        // Resolve the item's proc/use spells to human-readable text. The tooltip
        // description (e.g. "Chance on Hit: Increases Strength by $s1 for $d")
        // lives only in Spell.dbc, not in the SQL spell_template, so we pull name
        // + description from DbcService and fall back to spell_template.name for
        // hidden spells that Spell.dbc omits.
        var spells = await ResolveItemSpellsAsync(conn, (IDictionary<string, object>)item);

        return Json(new { found = true, item, iconPath, modelPath, spells });
    }

    /// <summary>
    /// Builds the display list for an item's up-to-5 spell slots: for each
    /// non-empty spellid_N it returns { id, trigger, name, description,
    /// descriptionRaw }. Names/descriptions come from Spell.dbc (DbcService),
    /// with a spell_template.name fallback for spells Spell.dbc hides; the
    /// description's $-tokens ($s1, $d, ...) are resolved to real numbers from
    /// the spell's spell_template effect fields via <see cref="SpellTooltipFormatter"/>.
    /// </summary>
    private async Task<List<object>> ResolveItemSpellsAsync(
        System.Data.IDbConnection conn, IDictionary<string, object> item)
    {
        var refs = new List<(int Id, int Trigger)>();
        for (int i = 1; i <= 5; i++)
        {
            if (!item.TryGetValue($"spellid_{i}", out var sidObj)) continue;
            int sid = Convert.ToInt32(sidObj ?? 0);
            if (sid <= 0) continue;
            item.TryGetValue($"spelltrigger_{i}", out var trigObj);
            refs.Add((sid, Convert.ToInt32(trigObj ?? 0)));
        }

        var spells = new List<object>();
        if (refs.Count == 0) return spells;

        // Full spell_template rows: names for DBC-hidden spells, plus the effect
        // fields needed to resolve tooltip tokens.
        var sqlRows = new Dictionary<int, IDictionary<string, object>>();
        try
        {
            var ids = refs.Select(r => r.Id).Distinct().ToArray();
            var rows = await conn.QueryAsync<dynamic>(
                @"SELECT * FROM spell_template
                  WHERE entry IN @Ids AND build = (
                      SELECT MAX(build) FROM spell_template st2 WHERE st2.entry = spell_template.entry)",
                new { Ids = ids });
            foreach (var row in rows)
            {
                var dict = (IDictionary<string, object>)row;
                int e = Convert.ToInt32(dict.TryGetValue("entry", out var ev) ? ev : 0);
                if (e > 0) sqlRows[e] = dict;
            }
        }
        catch { /* spell_template may lack rows for these ids — DBC still covers most */ }

        foreach (var (id, trigger) in refs)
        {
            string? name = null, rawDescription = null;
            if (_dbc.SpellEntries.TryGetValue((uint)id, out var dbcEntry))
            {
                name = dbcEntry.Name;
                rawDescription = dbcEntry.Description;
            }

            sqlRows.TryGetValue(id, out var row);
            if (string.IsNullOrEmpty(name) && row != null &&
                row.TryGetValue("name", out var nm) && nm is string sqlName && sqlName.Length > 0)
                name = sqlName;

            // Resolve $-tokens when we have both a description and the effect row.
            string? description = rawDescription;
            if (!string.IsNullOrEmpty(rawDescription) && row != null)
                description = SpellTooltipFormatter.Format(rawDescription, BuildSpellNumbers((uint)id, row));

            spells.Add(new { id, trigger, name, description, descriptionRaw = rawDescription });
        }

        return spells;
    }

    /// <summary>Extracts the effect fields the tooltip formatter needs from a spell_template row.</summary>
    private SpellTooltipFormatter.SpellNumbers BuildSpellNumbers(uint entry, IDictionary<string, object> row)
    {
        int GetInt(string col)
        {
            if (row.TryGetValue(col, out var v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { return 0; }
            }
            return 0;
        }

        var n = new SpellTooltipFormatter.SpellNumbers
        {
            Entry = entry,
            ProcChance = GetInt("procChance"),
            ProcCharges = GetInt("procCharges"),
            StackAmount = GetInt("stackAmount"),
            MaxAffectedTargets = GetInt("maxAffectedTargets"),
        };
        for (int i = 0; i < 3; i++)
        {
            int s = i + 1;
            n.BasePoints[i] = GetInt($"effectBasePoints{s}");
            n.DieSides[i] = GetInt($"effectDieSides{s}");
            n.BaseDice[i] = GetInt($"effectBaseDice{s}");
            n.Amplitude[i] = GetInt($"effectAmplitude{s}");
            n.ChainTargets[i] = GetInt($"effectChainTarget{s}");
        }

        uint durationIdx = (uint)GetInt("durationIndex");
        if (_dbc.SpellDurations.TryGetValue(durationIdx, out var dur))
            n.DurationMs = dur.DurationMs;

        return n;
    }

    // ===================== NEW — EDIT ENDPOINTS =====================

    /// <summary>
    /// GET /Items/NextCustomId — returns the next available entry in the 900000+ range.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> NextCustomId()
    {
        using var conn = _db.Mangos();
        var maxEntry = await conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(entry) FROM item_template WHERE entry >= @Start",
            new { Start = CUSTOM_RANGE_START });

        var nextId = (maxEntry ?? CUSTOM_RANGE_START - 1) + 1;
        return Json(new { nextId });
    }

    /// <summary>
    /// GET /Items/FullRow?entry=19019 — returns ALL editable columns for an item.
    /// Used to populate the edit form (both for cloning and editing).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> FullRow(int entry)
    {
        using var conn = _db.Mangos();

        var sql = @"SELECT * FROM item_template
                    WHERE entry = @Entry
                    ORDER BY patch DESC LIMIT 1";

        var item = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { Entry = entry });
        if (item == null)
            return Json(new { found = false });

        uint displayId = (uint)(item.display_id ?? 0);
        var iconPath = _dbc.GetItemIconPath(displayId);

        // Generate GLB on demand from MPQ
        string? modelPath = _itemTextures.EnsureGlb(displayId);

        return Json(new
        {
            found = true,
            item,
            iconPath,
            modelPath,
            isCustom = entry >= CUSTOM_RANGE_START
        });
    }

    /// <summary>
    /// POST /Items/Save — Insert (new custom item) or Update (existing item).
    /// Body: JSON with "entry" and all editable field values using snake_case column names.
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
            "SELECT entry, name FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry });

        // Build state_before for audit
        string? stateBefore = null;
        if (existing != null)
        {
            var beforeRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });
            stateBefore = JsonSerializer.Serialize((IDictionary<string, object>)beforeRow);
        }

        bool isInsert = existing == null;
        bool isCustom = entry >= CUSTOM_RANGE_START;

        // Build parameter dictionary from the JSON body
        var parameters = new DynamicParameters();
        parameters.Add("Entry", entry);

        // For new items, use patch=0 (custom content, no progressive patching)
        if (isInsert)
            parameters.Add("Patch", 0);

        foreach (var col in EDITABLE_COLUMNS)
        {
            // Try to get the value from the JSON body using the column name
            if (body.TryGetProperty(col, out var val))
            {
                if (val.ValueKind == JsonValueKind.Null || val.ValueKind == JsonValueKind.Undefined)
                    parameters.Add(col, 0);
                else if (val.ValueKind == JsonValueKind.Number)
                    parameters.Add(col, val.GetDouble());
                else if (val.ValueKind == JsonValueKind.String)
                    parameters.Add(col, val.GetString());
                else
                    parameters.Add(col, val.GetRawText());
            }
            else
            {
                // Default to 0 for missing numeric fields, empty for strings
                if (col == "name")
                    parameters.Add(col, "Custom Item");
                else if (col == "description")
                    parameters.Add(col, "");
                else
                    parameters.Add(col, 0);
            }
        }

        try
        {
            if (isInsert)
            {
                // INSERT new item
                var columns = "entry, patch, " + string.Join(", ", EDITABLE_COLUMNS);
                var values = "@Entry, @Patch, " + string.Join(", ", EDITABLE_COLUMNS.Select(c => "@" + c));

                var insertSql = $"INSERT INTO item_template ({columns}) VALUES ({values})";
                await conn.ExecuteAsync(insertSql, parameters);
            }
            else
            {
                // UPDATE existing item — update the latest patch row
                var patch = await conn.ExecuteScalarAsync<int>(
                    "SELECT MAX(patch) FROM item_template WHERE entry = @Entry",
                    new { Entry = entry });
                parameters.Add("Patch", patch);

                var setClauses = string.Join(", ", EDITABLE_COLUMNS.Select(c => $"{c} = @{c}"));
                var updateSql = $"UPDATE item_template SET {setClauses} WHERE entry = @Entry AND patch = @Patch";
                await conn.ExecuteAsync(updateSql, parameters);
            }

            // Build state_after for audit
            var afterRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });
            var stateAfter = afterRow != null
                ? JsonSerializer.Serialize((IDictionary<string, object>)afterRow)
                : null;

            // Get the item name for the audit log
            string itemName = "Unknown";
            if (body.TryGetProperty("name", out var nameProp))
                itemName = nameProp.GetString() ?? "Unknown";

            // Audit log
            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "content",
                Action = isInsert ? "item_create" : "item_edit",
                TargetType = isCustom ? "item_custom" : "item_base_game",
                TargetName = itemName,
                TargetId = entry,
                StateBefore = stateBefore,
                StateAfter = stateAfter,
                IsReversible = true,
                Success = true,
                Notes = isInsert
                    ? $"Created custom item #{entry}"
                    : (isCustom ? $"Edited custom item #{entry}" : $"Edited base game item #{entry}")
            });

            return Json(new { success = true, entry, isInsert });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Items/Delete?entry=N — Delete a custom item (900000+ only).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Delete(int entry)
    {
        if (entry < CUSTOM_RANGE_START)
            return Json(new { success = false, error = "Cannot delete base game items" });

        using var conn = _db.Mangos();

        // Get state before for audit
        var beforeRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry });

        if (beforeRow == null)
            return Json(new { success = false, error = "Item not found" });

        string stateBefore = JsonSerializer.Serialize((IDictionary<string, object>)beforeRow);
        string itemName = (string)(beforeRow.name ?? "Unknown");

        await conn.ExecuteAsync("DELETE FROM item_template WHERE entry = @Entry", new { Entry = entry });

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "item_delete",
            TargetType = "item_custom",
            TargetName = itemName,
            TargetId = entry,
            StateBefore = stateBefore,
            IsReversible = false,
            Success = true,
            Notes = $"Deleted custom item #{entry}"
        });

        return Json(new { success = true });
    }

    // ===================== ITEM TEXTURES =====================

    /// <summary>
    /// GET /Items/TextureInfo?displayId=29604
    /// Extracts the item's M2 model from MPQ, decodes all BLP textures to PNG,
    /// returns texture metadata + preview image paths.
    /// Works for ANY item — no pre-extraction needed.
    /// </summary>
    [HttpGet]
    public IActionResult TextureInfo(uint displayId)
    {
        if (displayId == 0)
            return Json(new { found = false, error = "No displayId" });

        try
        {
            var info = _itemTextures.GetTexturesForDisplay(displayId);
            if (info == null)
                return Json(new { found = false });

            return Json(new
            {
                found = true,
                displayId = info.DisplayId,
                modelName = info.ModelName,
                m2Size = info.M2Size,
                vertexCount = info.VertexCount,
                triangleCount = info.TriangleCount,
                textures = info.Textures.Select(t => new
                {
                    index = t.Index,
                    filename = t.Filename,
                    mpqPath = t.MpqPath,
                    width = t.Width,
                    height = t.Height,
                    format = t.Format,
                    alphaDepth = t.AlphaDepth,
                    blpFileSize = t.BlpFileSize,
                    previewUrl = t.PreviewPngPath,
                    hasPreview = t.HasPreview
                })
            });
        }
        catch (Exception ex)
        {
            return Json(new { found = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Items/Retexture
    /// AI-powered texture replacement: Ollama → Flux → BLP → patch MPQ.
    /// Body: { displayId, itemName, originalBlpFilename, originalMpqPath, styleDirection, customPrompt? }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Retexture([FromBody] RetextureRequest request)
    {
        if (request.DisplayId == 0)
            return Json(new { success = false, error = "No displayId" });

        try
        {
            var result = await _retexture.RetextureAsync(request, HttpContext.RequestAborted);
            return Json(new
            {
                success = result.Success,
                error = result.Error,
                prompt = result.Prompt,
                previewUrl = result.GeneratedPngPath,
                patchUrl = result.PatchMpqPath,
                customBlpPath = result.CustomBlpMpqPath,
                customM2Path = result.CustomM2MpqPath,
                newDisplayId = result.NewDisplayId,
                originalWidth = result.OriginalWidth,
                originalHeight = result.OriginalHeight,
                originalFormat = result.OriginalFormat,
                blpSize = result.BlpSizeBytes
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ===================== VISION RECOLOR =====================

    /// <summary>
    /// GET /Items/VisionRecolorStatus
    /// Checks whether a vision model is configured and available.
    /// </summary>
    [HttpGet]
    public IActionResult VisionRecolorStatus()
    {
        return Json(new { available = _palette.IsAvailable });
    }

    /// <summary>
    /// POST /Items/VisionRecolorPreview
    /// Sends texture to vision model with instruction, applies HSL transforms, returns preview.
    /// Does NOT save to DB or rebuild the patch — just a preview.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> VisionRecolorPreview([FromBody] PaletteSwapRequest request)
    {
        if (request.DisplayId == 0)
            return Json(new { success = false, error = "No displayId" });

        if (!_palette.IsAvailable)
            return Json(new { success = false, error = "Vision model not configured. Set Ollama Vision Model in Settings." });

        var texInfo = _itemTextures.GetTexturesForDisplay(request.DisplayId);
        if (texInfo == null)
            return Json(new { success = false, error = "No textures found" });

        var targetTex = texInfo.Textures.FirstOrDefault(t =>
            t.MpqPath.Equals(request.OriginalMpqPath, StringComparison.OrdinalIgnoreCase)
            || t.Filename.Equals(request.OriginalBlpFilename, StringComparison.OrdinalIgnoreCase));
        targetTex ??= texInfo.Textures.FirstOrDefault();

        if (targetTex == null || !targetTex.HasPreview)
            return Json(new { success = false, error = "No texture preview" });

        string previewPath = Path.Combine(_env.WebRootPath,
            targetTex.PreviewPngPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        if (!System.IO.File.Exists(previewPath))
            return Json(new { success = false, error = "Preview PNG not found" });

        var outputDir = Path.Combine(_env.WebRootPath, "item_textures_cache", "palette_preview");
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, $"recolor_{request.DisplayId}_{Guid.NewGuid():N}.png");

        var outputFile = await _palette.RecolorAndSaveAsync(
            previewPath, request.Instruction, outputPath, HttpContext.RequestAborted);

        if (outputFile == null)
            return Json(new { success = false, error = "Vision recolor failed — check server logs" });

        string webPath = $"/item_textures_cache/palette_preview/{Path.GetFileName(outputPath)}";
        return Json(new { success = true, previewUrl = webPath });
    }

    /// <summary>
    /// POST /Items/VisionRecolorRetexture
    /// Vision-guided recolor, optionally chains to AI, saves to DB, rebuilds patch.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> VisionRecolorRetexture([FromBody] PaletteSwapRequest request)
    {
        if (request.DisplayId == 0)
            return Json(new { success = false, error = "No displayId" });

        if (!_palette.IsAvailable)
            return Json(new { success = false, error = "Vision model not configured" });

        try
        {
            var texInfo = _itemTextures.GetTexturesForDisplay(request.DisplayId);
            if (texInfo == null)
                return Json(new { success = false, error = "No textures found" });

            var targetTex = texInfo.Textures.FirstOrDefault(t =>
                t.MpqPath.Equals(request.OriginalMpqPath, StringComparison.OrdinalIgnoreCase)
                || t.Filename.Equals(request.OriginalBlpFilename, StringComparison.OrdinalIgnoreCase));
            targetTex ??= texInfo.Textures.FirstOrDefault();

            if (targetTex == null || !targetTex.HasPreview)
                return Json(new { success = false, error = "No texture preview" });

            string previewPath = Path.Combine(_env.WebRootPath,
                targetTex.PreviewPngPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

            // ── TEST MODE: skip brute force, send ORIGINAL straight to Flux ──
            // Evaluates whether Flux's semantic understanding handles regional
            // color swaps (wood vs gold) better than per-pixel HSL. Uses a
            // region-aware prompt built directly (bypassing the Ollama crafter,
            // which has been deleting materials it shouldn't).
            if (request.SkipBruteForce)
            {
                float testDenoise = request.AIDenoise > 0.01f
                    ? Math.Clamp(request.AIDenoise, 0.1f, 0.8f)
                    : 0.5f;

                string fluxPrompt = BuildRegionAwareFluxPrompt(
                    request.Instruction, targetTex.Filename);

                _logger.LogInformation(
                    "VisionRecolorRetexture: SkipBruteForce test — denoise={D}, prompt=\"{P}\"",
                    testDenoise, fluxPrompt);

                var testReq = new RetextureRequest
                {
                    DisplayId = request.DisplayId,
                    ItemName = request.ItemName,
                    OriginalBlpFilename = targetTex.Filename,
                    OriginalMpqPath = targetTex.MpqPath,
                    ModifyExisting = true,            // img2img from original preview
                    DenoiseStrength = testDenoise,
                    CustomPrompt = fluxPrompt,         // bypass Ollama crafter
                };

                var testResult = await _retexture.RetextureAsync(testReq, HttpContext.RequestAborted);
                return Json(new
                {
                    success = testResult.Success,
                    error = testResult.Error,
                    prompt = testResult.Prompt,
                    previewUrl = testResult.GeneratedPngPath,
                    patchUrl = testResult.PatchMpqPath,
                    newDisplayId = testResult.NewDisplayId,
                    originalWidth = testResult.OriginalWidth,
                    originalHeight = testResult.OriginalHeight,
                    originalFormat = testResult.OriginalFormat,
                    blpSize = testResult.BlpSizeBytes,
                    mode = $"flux_only@{testDenoise:F2}"
                });
            }

            // ── STAGE 1: Brute-force palette swap (deterministic draft) ──
            var recolorDir = Path.Combine(_env.WebRootPath, "item_textures_cache", "palette_swapped");
            Directory.CreateDirectory(recolorDir);
            string recoloredPng = Path.Combine(recolorDir, $"recolored_{request.DisplayId}_{Guid.NewGuid():N}.png");

            var recolorResult = await _palette.RecolorAndSaveAsync(
                previewPath, request.Instruction, recoloredPng, request.Boxes, HttpContext.RequestAborted);

            if (recolorResult == null)
                return Json(new { success = false, error = "Palette swap (draft) step failed" });

            // ── BruteForceOnly: commit the draft directly, skip Flux ──
            // Used by variation "Apply" so the committed result matches the
            // previewed brute-force variant exactly (and is fast).
            if (request.BruteForceOnly)
            {
                var bfReq = new RetextureRequest
                {
                    DisplayId = request.DisplayId,
                    ItemName = request.ItemName,
                    OriginalBlpFilename = targetTex.Filename,
                    OriginalMpqPath = targetTex.MpqPath,
                    StyleDirection = request.Instruction,
                };
                var bf = await _retexture.RetextureFromBitmapAsync(
                    bfReq, recoloredPng, HttpContext.RequestAborted);
                return Json(new
                {
                    success = bf.Success,
                    error = bf.Error,
                    prompt = bf.Prompt,
                    previewUrl = bf.GeneratedPngPath,
                    patchUrl = bf.PatchMpqPath,
                    newDisplayId = bf.NewDisplayId,
                    originalWidth = bf.OriginalWidth,
                    originalHeight = bf.OriginalHeight,
                    originalFormat = bf.OriginalFormat,
                    blpSize = bf.BlpSizeBytes,
                    mode = "palette_only"
                });
            }

            // ── STAGE 2: Flux img2img polish (always runs) ──
            // The brute-force draft gets the colors mostly right while preserving
            // the original sculpting. Flux then refines it — fixing family
            // misclassifications (light brown wrongly recolored, etc.) and
            // restoring a hand-painted look. The user's instruction is passed
            // as the style direction so Flux knows the intended palette.
            //
            // Denoise: default to a modest value that corrects color errors
            // without repainting the whole texture. AIDenoise from the request
            // overrides if the user set the slider.
            float denoise = request.AIDenoise > 0.01f
                ? Math.Clamp(request.AIDenoise, 0.1f, 0.8f)
                : 0.35f;

            var retexRequest = new RetextureRequest
            {
                DisplayId = request.DisplayId,
                ItemName = request.ItemName,
                OriginalBlpFilename = targetTex.Filename,
                OriginalMpqPath = targetTex.MpqPath,
                ModifyExisting = true,
                DenoiseStrength = denoise,
                // Pass the user's recolor instruction so the Flux prompt knows
                // the target palette — this is what lets Flux fix the regions
                // the brute force got wrong.
                StyleDirection = string.IsNullOrWhiteSpace(request.StyleDirection)
                    ? request.Instruction
                    : request.StyleDirection,
            };

            // Swap the preview with the recolored draft, run through the normal
            // retexture pipeline (Flux img2img), then restore the original preview.
            string backupPath = previewPath + ".bak";
            System.IO.File.Copy(previewPath, backupPath, true);
            System.IO.File.Copy(recoloredPng, previewPath, true);

            try
            {
                var result = await _retexture.RetextureAsync(retexRequest, HttpContext.RequestAborted);

                // If Flux failed (node offline, timeout), fall back to the
                // brute-force draft so the user still gets a result.
                if (!result.Success)
                {
                    _logger.LogWarning("VisionRecolorRetexture: Flux polish failed ({Err}), falling back to draft", result.Error);
                    var draftResult = await _retexture.RetextureFromBitmapAsync(
                        retexRequest, recoloredPng, HttpContext.RequestAborted);
                    return Json(new
                    {
                        success = draftResult.Success,
                        error = draftResult.Error,
                        prompt = draftResult.Prompt,
                        previewUrl = draftResult.GeneratedPngPath,
                        patchUrl = draftResult.PatchMpqPath,
                        newDisplayId = draftResult.NewDisplayId,
                        originalWidth = draftResult.OriginalWidth,
                        originalHeight = draftResult.OriginalHeight,
                        originalFormat = draftResult.OriginalFormat,
                        blpSize = draftResult.BlpSizeBytes,
                        mode = "palette_draft_fallback"
                    });
                }

                return Json(new
                {
                    success = result.Success,
                    error = result.Error,
                    prompt = result.Prompt,
                    previewUrl = result.GeneratedPngPath,
                    patchUrl = result.PatchMpqPath,
                    newDisplayId = result.NewDisplayId,
                    originalWidth = result.OriginalWidth,
                    originalHeight = result.OriginalHeight,
                    originalFormat = result.OriginalFormat,
                    blpSize = result.BlpSizeBytes,
                    mode = "palette+flux"
                });
            }
            finally
            {
                if (System.IO.File.Exists(backupPath))
                {
                    System.IO.File.Copy(backupPath, previewPath, true);
                    System.IO.File.Delete(backupPath);
                }
            }
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Build a Flux img2img prompt for a direct recolor of an EXISTING weapon
    /// texture, emphasizing STRUCTURE PRESERVATION and naming material regions
    /// so Flux's semantic understanding can place colors correctly (e.g. keep
    /// the wooden handle, recolor only the metal trim).
    ///
    /// Deliberately does NOT use negative phrasing like "no brown elements" —
    /// that tells Flux to delete materials. Instead it frames every swap as a
    /// material transformation while keeping the object's shapes/details intact.
    /// </summary>
    private static string BuildRegionAwareFluxPrompt(string instruction, string textureFilename)
    {
        // The instruction itself carries the user's intent; we wrap it in a
        // structure-preserving frame. We pass the raw instruction through so
        // Flux sees the exact swaps the user asked for, but contextualize it
        // as "recolor this existing texture, keep the shapes."
        return
            "Recolor this existing World of Warcraft weapon texture (flat 2D UV texture map, " +
            "hand-painted vanilla 2004 style). PRESERVE the exact shapes, layout, sculpting, " +
            "shadows, highlights, and all fine details of the original — only change the COLORS " +
            "of the materials as follows: " +
            instruction.Trim().TrimEnd('.') + ". " +
            "Keep any material not mentioned in its original color. Maintain the original " +
            "light-to-dark shading on every surface so the metal still looks metallic and the " +
            "wood still looks like wood grain. Flat top-down texture, no perspective, no 3D " +
            "rendering, no new objects, same composition as the input image.";
    }

    // ===================== VARIATION MODE =====================

    /// <summary>
    /// Super-res the recolor SOURCE once (cached per source) so variants render
    /// at a higher resolution with real, model-invented detail instead of the
    /// vanilla item's small native size. The recolor is luminance-preserving, so
    /// the enhanced detail survives the color swap untouched.
    ///
    /// Returns a path to the upscaled PNG, or the ORIGINAL source path unchanged
    /// when upscaling is disabled (no model), the multiplier is 1, ComfyUI is
    /// offline, or anything fails — so the whole feature degrades cleanly to the
    /// previous native-resolution behavior. Cached on disk keyed by source name +
    /// size + multiplier, so a gallery of N variants triggers ONE ComfyUI call.
    /// </summary>
    private async Task<string> GetUpscaledSourceAsync(string sourcePngPath, CancellationToken ct)
    {
        if (!_upscaler.IsEnabled || _upscaler.SourceMultiplier <= 1)
            return sourcePngPath;
        try
        {
            var fi = new FileInfo(sourcePngPath);
            if (!fi.Exists) return sourcePngPath;

            var cacheDir = Path.Combine(_env.WebRootPath, "item_textures_cache", "source_upscaled");
            Directory.CreateDirectory(cacheDir);
            // Key on name + byte length + multiplier + denoise radius so a
            // regenerated source OR a changed denoise setting busts the cache.
            string key = $"{Path.GetFileNameWithoutExtension(sourcePngPath)}_{fi.Length}_{_upscaler.SourceMultiplier}x_cd{_upscaler.ChromaDenoiseRadius}";
            string cached = Path.Combine(cacheDir, key + ".png");
            if (System.IO.File.Exists(cached)) return cached;

            string up = await _upscaler.UpscaleSourceAsync(sourcePngPath, key, ct);
            // No-op / failure → upscaler returned the original path: use native.
            if (string.Equals(up, sourcePngPath, StringComparison.OrdinalIgnoreCase)
                || !System.IO.File.Exists(up))
                return sourcePngPath;

            try { System.IO.File.Copy(up, cached, overwrite: true); }
            catch { return up; }   // couldn't cache — still return the upscaled file
            return cached;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                "Items: source upscale failed ({Err}), using native source", ex.Message);
            return sourcePngPath;
        }
    }

    /// <summary>
    /// POST /Items/DetectFamilies
    /// Returns the color families present in an item's texture (deterministic).
    /// Used by the variation UI to show what's there and seed recipe generation.
    /// </summary>
    [HttpPost]
    public IActionResult DetectFamilies([FromBody] PaletteSwapRequest request)
    {
        if (request.DisplayId == 0)
            return Json(new { success = false, error = "No displayId" });

        var texInfo = _itemTextures.GetTexturesForDisplay(request.DisplayId);
        if (texInfo == null) return Json(new { success = false, error = "No textures found" });

        var targetTex = texInfo.Textures.FirstOrDefault(t =>
            t.MpqPath.Equals(request.OriginalMpqPath, StringComparison.OrdinalIgnoreCase)
            || t.Filename.Equals(request.OriginalBlpFilename, StringComparison.OrdinalIgnoreCase))
            ?? texInfo.Textures.FirstOrDefault();
        if (targetTex == null || !targetTex.HasPreview)
            return Json(new { success = false, error = "No texture preview" });

        string previewPath = Path.Combine(_env.WebRootPath,
            targetTex.PreviewPngPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        var families = _palette.DetectFamilies(previewPath);
        return Json(new
        {
            success = true,
            families = families.Select(f => new
            {
                family = f.Family,
                percent = Math.Round(f.Percent, 1),
                meanSat = Math.Round(f.MeanSat, 2),
                meanLightness = Math.Round(f.MeanLightness, 2)
            })
        });
    }

    /// <summary>
    /// POST /Items/GenerateVariations
    /// Generates N coherent recolor variants for a theme. Each recipe runs
    /// through the brute-force engine; optionally finished with Flux. Returns
    /// preview URLs + the recipe used for each, plus the assigned newDisplayIds.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> GenerateVariations([FromBody] VariationRequest request)
    {
        if (request.DisplayId == 0)
            return Json(new { success = false, error = "No displayId" });

        int count = Math.Clamp(request.Count <= 0 ? 4 : request.Count, 1, 8);

        var texInfo = _itemTextures.GetTexturesForDisplay(request.DisplayId);
        if (texInfo == null) return Json(new { success = false, error = "No textures found" });

        var targetTex = texInfo.Textures.FirstOrDefault(t =>
            t.MpqPath.Equals(request.OriginalMpqPath, StringComparison.OrdinalIgnoreCase)
            || t.Filename.Equals(request.OriginalBlpFilename, StringComparison.OrdinalIgnoreCase))
            ?? texInfo.Textures.FirstOrDefault();
        if (targetTex == null || !targetTex.HasPreview)
            return Json(new { success = false, error = "No texture preview" });

        string previewPath = Path.Combine(_env.WebRootPath,
            targetTex.PreviewPngPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        // 1. Detect families, 2. generate recipes
        var families = _palette.DetectFamilies(previewPath);
        var recipes = await _variations.GenerateRecipesAsync(
            request.Theme, families, count, HttpContext.RequestAborted);

        if (recipes.Count == 0)
            return Json(new { success = false, error = "Could not generate recipes" });

        // 3. Render each recipe to a PREVIEW png (brute force only — fast).
        //    Families are detected on the native source (color, not resolution),
        //    but we recolor a super-res'd copy so the gallery — and the preview/
        //    commit that reuse this same recolor — render sharp. One cached
        //    ComfyUI call covers the whole gallery; degrades to native on failure.
        //    We do NOT save to DB or rebuild the patch here; the user picks a
        //    variant first, then applies it via the normal precision endpoint.
        var previewDir = Path.Combine(_env.WebRootPath, "item_textures_cache", "variation_preview");
        Directory.CreateDirectory(previewDir);

        string recolorSrc = await GetUpscaledSourceAsync(previewPath, HttpContext.RequestAborted);

        var outVariants = new List<object>();
        foreach (var recipe in recipes)
        {
            string outPng = Path.Combine(previewDir,
                $"var_{request.DisplayId}_{Guid.NewGuid():N}.png");
            var ok = await _palette.RecolorAndSaveAsync(
                recolorSrc, recipe.Instruction, outPng, null, HttpContext.RequestAborted);
            if (ok == null) continue;

            outVariants.Add(new
            {
                name = recipe.Name,
                instruction = recipe.Instruction,
                swaps = recipe.Swaps,
                previewUrl = $"/item_textures_cache/variation_preview/{Path.GetFileName(outPng)}"
            });
        }

        return Json(new
        {
            success = true,
            theme = request.Theme,
            detectedFamilies = families.Select(f => new { family = f.Family, percent = Math.Round(f.Percent, 1) }),
            variants = outVariants
        });
    }

    /// <summary>
    /// POST /Items/GenerateBodyAtlasVariations
    /// Variations for PAINTED ARMOR (chest, legs, boots, belt, bracers, gloves,
    /// robe, tabard, cape). These items have no standalone model — they paint a
    /// set of component textures into the shared character body atlas. So unlike
    /// GenerateVariations (one model texture → one recolored PNG), this recolors
    /// EVERY component slot with the same recipe and returns the recolored slot
    /// URLs per card. The client paints them via equip.equipBodyAtlasRetextureDirect
    /// → compositor.paintBodyAtlas (the same path normal dressing uses).
    ///
    /// Family detection + recipe generation are shared with the weapon path
    /// (VariationRecipeService), so the marble/obsidian/jewel schemes apply here
    /// identically — one coherent palette across the whole piece.
    ///
    /// Body of request: VariationRequest (displayId, theme, count).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> GenerateBodyAtlasVariations([FromBody] VariationRequest request)
    {
        if (request.DisplayId == 0)
            return Json(new { success = false, error = "No displayId" });

        int count = Math.Clamp(request.Count <= 0 ? 4 : request.Count, 1, 8);

        // Component textures (slot index → on-disk PNG) for this display.
        var atlas = await _bodyAtlas.EnsureAtlasTexturesAsync(request.DisplayId);
        if (atlas == null || atlas.SlotUrls.Count == 0)
            return Json(new
            {
                success = false,
                error = "No body-atlas textures for this display — not painted armor, or the component BLPs aren't in the MPQ."
            });

        // Map a web URL (/body_atlas_cache/..) back to its disk path.
        string DiskOf(string webUrl) => Path.Combine(_env.WebRootPath,
            webUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        // Detect families on a representative slot (prefer TorsoUpper=3, the
        // chest; else the lowest-indexed available slot). One recipe is built
        // from it and applied to every slot so the whole piece recolors coherently.
        int primarySlot = atlas.SlotUrls.ContainsKey(3)
            ? 3 : atlas.SlotUrls.Keys.OrderBy(k => k).First();
        string primaryDisk = DiskOf(atlas.SlotUrls[primarySlot]);
        if (!System.IO.File.Exists(primaryDisk))
            return Json(new { success = false, error = "Body-atlas source PNG missing on disk" });

        var families = _palette.DetectFamilies(primaryDisk);
        var recipes = await _variations.GenerateRecipesAsync(
            request.Theme, families, count, HttpContext.RequestAborted);
        if (recipes.Count == 0)
            return Json(new { success = false, error = "Could not generate recipes" });

        var previewDir = Path.Combine(_env.WebRootPath, "item_textures_cache", "bodyatlas_preview");
        Directory.CreateDirectory(previewDir);

        // Each recipe → recolor EVERY component slot with that recipe. Returns
        // per-card { slotUrls (recolored, for on-character paint), previewUrl
        // (representative thumbnail) }.
        var outVariants = new List<object>();
        foreach (var recipe in recipes)
        {
            var recoloredSlots = new Dictionary<int, string>();
            foreach (var (slot, webUrl) in atlas.SlotUrls)
            {
                string srcDisk = DiskOf(webUrl);
                if (!System.IO.File.Exists(srcDisk)) continue;
                string outName = $"ba_{request.DisplayId}_s{slot}_{Guid.NewGuid():N}.png";
                string outPng = Path.Combine(previewDir, outName);
                var ok = await _palette.RecolorAndSaveAsync(
                    srcDisk, recipe.Instruction, outPng, null, HttpContext.RequestAborted);
                if (ok == null) continue;
                recoloredSlots[slot] = $"/item_textures_cache/bodyatlas_preview/{outName}";
            }
            if (recoloredSlots.Count == 0) continue;

            string thumb = recoloredSlots.TryGetValue(primarySlot, out var t)
                ? t : recoloredSlots.Values.First();
            outVariants.Add(new
            {
                name = recipe.Name,
                swaps = recipe.Swaps,
                slotUrls = recoloredSlots,   // slot → recolored png (client paints these)
                previewUrl = thumb           // representative thumbnail for the gallery card
            });
        }

        if (outVariants.Count == 0)
            return Json(new { success = false, error = "Recolor produced no slots" });

        return Json(new
        {
            success = true,
            theme = request.Theme,
            primarySlot,
            slots = atlas.SlotUrls.Keys.OrderBy(k => k).ToArray(),
            detectedFamilies = families.Select(f => new { family = f.Family, percent = Math.Round(f.Percent, 1) }),
            variants = outVariants
        });
    }


    /// <summary>
    /// POST /Items/DeletePreviewGlb
    /// Clean up a temp preview GLB when the modal closes (and opportunistically
    /// sweep stale ones). Best-effort; always returns success.
    /// Body: { glbUrl: "/item_models/_preview/xxx.glb" }
    /// </summary>
    [HttpPost]
    public IActionResult DeletePreviewGlb([FromBody] JsonElement body)
    {
        string? glbUrl = body.TryGetProperty("glbUrl", out var g) ? g.GetString() : null;
        _itemTextures.DeletePreviewGlb(glbUrl);
        return Json(new { success = true });
    }


    // ═══════════════════════════════════════════════════════════════════
    // UNIVERSAL PREVIEW + COMMIT (May 2026)
    // Same preview-on-character / save-to-commit flow as Segmented, for the
    // Palette / Variations / Scratch / Modify modes. All four converge on
    // CommitStagedRetexture, which takes the temp PNG produced by the
    // matching Preview* endpoint and runs it through RetextureFromBitmapAsync
    // (the same code path the Segmented commit uses) — so the committed
    // texture is byte-identical to what was previewed on the character.
    //
    // Layout:
    //   PreviewPaletteGlb     — palette swap (+ optional Flux polish/test)
    //   PreviewFluxGlb        — txt2img / img2img
    //   PreviewVariationGlb   — apply a chosen Variations gallery card
    //   CommitStagedRetexture — universal commit (validates pngPath is under
    //                           wwwroot/item_textures_cache/ to block traversal)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validate a server-side preview PNG path supplied by the client. The
    /// client receives the path verbatim from a Preview* endpoint and echoes
    /// it back on commit, so we must confirm it still resolves under the
    /// permitted cache root before reading it. Returns the canonical full
    /// path on success, null on failure.
    /// </summary>
    private string? ValidateStagedPngPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var cacheRoot = Path.GetFullPath(
                Path.Combine(_env.WebRootPath, "item_textures_cache"));
            var full = Path.GetFullPath(raw);
            if (!full.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!System.IO.File.Exists(full)) return null;
            // Defensive: only allow PNG files (the only thing Preview* writes).
            if (!full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return null;
            return full;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve the target texture entry for a retexture request, matching by
    /// MpqPath first then Filename, falling back to the model's first
    /// texture. Returns null if no preview exists. Centralizes the lookup
    /// that every Preview* endpoint does.
    /// </summary>
    private (ItemTextureEntry? Tex, string? PreviewPath, string? Error) ResolveTargetTexture(
        uint displayId, string mpqPath, string filename)
    {
        if (displayId == 0) return (null, null, "No displayId");
        var texInfo = _itemTextures.GetTexturesForDisplay(displayId);
        if (texInfo == null) return (null, null, "No textures found");

        // Explicit request from the interactive panel — honour it exactly.
        var targetTex = texInfo.Textures.FirstOrDefault(t =>
            t.MpqPath.Equals(mpqPath, StringComparison.OrdinalIgnoreCase)
            || t.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase));

        if (targetTex == null)
        {
            // ── No explicit texture (the batch path) ──
            //
            // This used to fall back to Textures.FirstOrDefault() — the first
            // texture in the M2's list — and that is wrong for any multi-texture
            // model.
            //
            // The commit patches ItemDisplayInfo field 3 = TextureName1, and that
            // field feeds exactly ONE of the model's texture slots. Every other
            // texture in an M2 is type 0: its path is BAKED INTO THE MODEL and no
            // DBC field can redirect it.
            //
            // Staff of Westfall has two textures — a 64x64 M2-embedded glow and the
            // 128x32 skin supplied by TextureName1. FirstOrDefault() grabbed the
            // GLOW, recolored it pink, and then wrote that recolor's name into
            // TextureName1: a recolor of texture A aimed at the slot that feeds
            // texture B. The staff rendered vanilla green, and the pink went nowhere.
            //
            // Match on TextureName1 so we recolor the texture the DBC actually owns.
            var dbcInfo = _dbc.GetItemModelInfo(displayId);
            string tex1 = dbcInfo?.TextureName1 ?? "";

            // Environment/reflection maps are shared cubemap-style textures the
            // engine applies for shine — they are NOT the item's skin, and
            // recoloring one repaints every reflective item in the game (or, via
            // the DBC redirect, points the skin slot at a reflection map and the
            // weapon renders BLACK — the Haggard's Sword incident, where
            // Textures[0] of Sword_1H_Long_A_02 turned out to be ARMORREFLECT3).
            static bool IsEnvMap(string? name)
            {
                var n = (name ?? "").ToUpperInvariant();
                return n.Contains("REFLECT") || n.Contains("ENVMAP") || n.Contains("GENERICGLOW");
            }

            if (!string.IsNullOrWhiteSpace(tex1) && !IsEnvMap(tex1))
            {
                // Filename may be bare or a full path depending on the M2 —
                // normalize before taking the stem (Linux GetFileName* does not
                // split on backslashes).
                targetTex = texInfo.Textures.FirstOrDefault(t =>
                    Path.GetFileNameWithoutExtension((t.Filename ?? "").Replace('\\', '/'))
                        .Equals(tex1, StringComparison.OrdinalIgnoreCase));

                if (targetTex == null)
                    return (null, null,
                        $"TextureName1 '{tex1}' matches none of the model's {texInfo.Textures.Count} M2 texture(s) — cannot recolor a texture the DBC does not control");
            }
            else if (IsEnvMap(tex1))
            {
                // The DBC's own TextureName1 is a reflection map (some vanilla
                // rows genuinely do this — the skin is baked, only the env map is
                // DBC-supplied). There is nothing recolorable that the DBC
                // controls. Refuse rather than paint the world's shine.
                return (null, null,
                    $"TextureName1 '{tex1}' is an environment/reflection map, not the item's skin — recoloring it is wrong for every item sharing it");
            }
            else
            {
                // Every texture is baked into the M2. Writing a name into
                // TextureName1 would be a silent no-op — fail loudly instead.
                return (null, null,
                    $"model has no DBC-supplied texture (TextureName1 is empty); all {texInfo.Textures.Count} texture(s) are baked into the M2 and cannot be swapped via ItemDisplayInfo");
            }
        }

        // Preview is required for palette/segmented/variation/img2img modes but
        // NOT for Flux txt2img. Return the tex regardless; previewPath stays
        // null when the preview isn't on disk and callers that need it check.
        if (!targetTex.HasPreview)
            return (targetTex, null, null);

        string previewPath = Path.Combine(_env.WebRootPath,
            targetTex.PreviewPngPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(previewPath))
            return (targetTex, null, null);

        return (targetTex, previewPath, null);
    }

    /// <summary>
    /// Wrap a rendered PNG (already on disk under wwwroot/item_textures_cache/)
    /// into throwaway preview GLB assets and return the response shape the panel
    /// expects. Shoulder displays produce their separate authored L/R models;
    /// single-model items continue to produce one primary GLB.
    ///
    /// (May 2026) **Optional upscaler cleanup pass.** Before wrapping, the PNG
    /// can go through ComfyUIUpscaler — 4x upscale via a game-texture-trained
    /// model (PBRify V4 by default), then mipmap-filtered downscale back to
    /// vanilla dims. The intent was to scrub the salt-and-pepper bleed that
    /// per-pixel palette swaps inherit from the source BLP's DXT compression
    /// noise. Cleanup is best-effort: if disabled or failed, the original PNG
    /// passes through unchanged.
    ///
    /// (May 2026) Now gated by <paramref name="skipCleanup"/>. The pass is a
    /// pure 4x-up→downscale round-trip — it ends back at the source resolution,
    /// so it can only SOFTEN; it never adds real detail. For the deterministic
    /// recolor modes (Variations especially) the recolor is luminance-preserving
    /// and already pixel-sharp, so the round-trip is pointless and visibly hurts.
    /// Callers on those paths pass skipCleanup:true so the preview — and the BLP
    /// the commit later encodes from this same PNG — is exactly the recolor.
    ///
    /// When the pass DOES run, the cleaned PNG becomes BOTH the staged GLB
    /// (mounted on the character viewer) AND the file that CommitStagedRetexture
    /// later BLP-encodes — so what you see is what gets persisted.
    /// </summary>
    private async Task<IActionResult> BuildPreviewResponse(uint displayId, string pngPath,
        string mode, string cacheSubdir, string? extra2DPreviewUrl = null, bool skipCleanup = false)
    {
        // ── Upscaler cleanup pass (best-effort, skippable) ──
        // CleanupAsync returns either a new cleaned PNG path or the original
        // path if disabled/failed. When skipCleanup is set we bypass it entirely
        // and the raw recolor PNG flows straight through to the GLB and commit.
        string cleanedPngPath = skipCleanup
            ? pngPath
            : await _upscaler.CleanupAsync(
                pngPath, $"preview_{displayId}_{mode}", HttpContext.RequestAborted);

        var previewGlbs = _itemTextures.BuildPreviewGlbs(displayId, cleanedPngPath);
        if (previewGlbs == null)
            return Json(new { success = false, error = "Preview GLB build failed" });

        // Derive the public URL from the (possibly cleaned) PNG's actual disk
        // directory rather than the original cacheSubdir hint — when cleanup
        // ran, the file lives under item_textures_cache/upscale_cleaned/, not
        // the original subdir.
        string pngUrl = DerivePublicUrl(cleanedPngPath, cacheSubdir);

        return Json(new
        {
            success = true,
            glbUrl = previewGlbs.GlbUrl,
            // Shoulder displays carry the two authored M2s independently.
            // The client mounts these by semantic attachment ID instead of
            // cloning/mirroring ModelName1 into the other slot.
            attachments = previewGlbs.Attachments,
            pngUrl,
            // pngPath is sent back to the client and echoed on commit. It's
            // re-validated server-side then (ValidateStagedPngPath) — the
            // client can't smuggle arbitrary paths through this.
            pngPath = cleanedPngPath,
            previewUrl = extra2DPreviewUrl ?? pngUrl,
            mode
        });
    }

    /// <summary>
    /// Given a PNG path that should live somewhere under wwwroot/, derive its
    /// public /item_textures_cache/... URL. Falls back to the supplied
    /// cacheSubdir hint if the path doesn't resolve under wwwroot.
    /// </summary>
    private string DerivePublicUrl(string pngPath, string fallbackSubdir)
    {
        try
        {
            var webRootFull = Path.GetFullPath(_env.WebRootPath);
            var pngFull = Path.GetFullPath(pngPath);
            if (pngFull.StartsWith(webRootFull, StringComparison.OrdinalIgnoreCase))
            {
                // Strip wwwroot prefix → forward-slash web path
                var rel = pngFull.Substring(webRootFull.Length).Replace('\\', '/');
                if (!rel.StartsWith("/")) rel = "/" + rel;
                return rel;
            }
        }
        catch { /* fall through */ }
        return $"/item_textures_cache/{fallbackSubdir}/" + Path.GetFileName(pngPath);
    }

    /// <summary>
    /// POST /Items/PreviewPaletteGlb
    /// Run a palette swap (optionally chained to Flux polish, or Flux-only
    /// test) and return a throwaway GLB url + the PNG url, WITHOUT writing
    /// the DB row or rebuilding the patch. Mirrors PreviewSegmentedGlb but
    /// for the Palette mode.
    /// Body: PaletteSwapRequest (same shape as VisionRecolorRetexture).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> PreviewPaletteGlb([FromBody] PaletteSwapRequest request)
    {
        if (!_palette.IsAvailable)
            return Json(new { success = false, error = "Vision model not configured" });

        var (targetTex, previewPath, err) = ResolveTargetTexture(
            request.DisplayId, request.OriginalMpqPath, request.OriginalBlpFilename);
        if (targetTex == null)
            return Json(new { success = false, error = err });
        if (previewPath == null)
            return Json(new { success = false, error = "Source texture preview not available" });

        try
        {
            // ── Flux-only test path ──
            // Mirrors the SkipBruteForce branch of VisionRecolorRetexture but
            // stops at "PNG on disk" — no DB, no MPQ. The committed result
            // (later, on Save) is byte-identical because Commit uses this
            // same PNG file.
            if (request.SkipBruteForce)
            {
                float testDenoise = request.AIDenoise > 0.01f
                    ? Math.Clamp(request.AIDenoise, 0.1f, 0.8f) : 0.5f;
                string fluxPrompt = BuildRegionAwareFluxPrompt(
                    request.Instruction, targetTex.Filename);

                var skipReq = new RetextureRequest
                {
                    DisplayId = request.DisplayId,
                    ItemName = request.ItemName,
                    OriginalBlpFilename = targetTex.Filename,
                    OriginalMpqPath = targetTex.MpqPath,
                    ModifyExisting = true,            // img2img from original preview
                    DenoiseStrength = testDenoise,
                    CustomPrompt = fluxPrompt,         // bypass Ollama crafter
                };
                var fluxPngPath = await _retexture.RenderToPngAsync(
                    skipReq, HttpContext.RequestAborted);
                if (fluxPngPath == null)
                    return Json(new { success = false, error = "Flux generation failed" });

                return await BuildPreviewResponse(request.DisplayId, fluxPngPath,
                    $"flux_only@{testDenoise:F2}", "retexture_resized");
            }

            // ── Stage 1: brute-force palette swap (always) ──
            var recolorDir = Path.Combine(_env.WebRootPath,
                "item_textures_cache", "palette_swapped");
            Directory.CreateDirectory(recolorDir);
            string recoloredPng = Path.Combine(recolorDir,
                $"recolored_{request.DisplayId}_{Guid.NewGuid():N}.png");

            // Recolor the cached SUPER-RES source (same one Variations uses) so a
            // custom palette swap is just as sharp as a Variations card — the
            // recolor is luminance-preserving, so the only thing that ever made
            // palette look soft was running it on the low-res native preview.
            // Degrades to native on failure. (Skipped on the Flux-chain path
            // below, which re-renders from the model preview anyway.)
            string paletteSrc = (request.ChainToAI && !request.BruteForceOnly)
                ? previewPath
                : await GetUpscaledSourceAsync(previewPath, HttpContext.RequestAborted);

            // ── LLM-assisted instruction parsing ──
            // Turn the user's free-text request into a clean family→target swap
            // map via the LLM (the "AI helps the recolor" path), instead of the
            // brittle regex parser. The LLM dedupes conflicting families, maps
            // loose phrases ("a dark stone obsidian" → "obsidian black"), and
            // covers every detected family. Falls back to the raw instruction
            // (regex ParseInstruction inside RecolorAndSaveAsync) if the LLM is
            // unavailable or returns nothing.
            string recolorInstruction = request.Instruction;
            try
            {
                var fams = _palette.DetectFamilies(paletteSrc);
                var recipe = await _variations.GenerateSwapsFromInstructionAsync(
                    request.Instruction, fams, HttpContext.RequestAborted);
                if (recipe != null && !string.IsNullOrWhiteSpace(recipe.Instruction))
                    recolorInstruction = recipe.Instruction;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    "PreviewPaletteGlb: LLM instruction parse failed ({Err}), using raw instruction",
                    ex.Message);
            }

            var recolorResult = await _palette.RecolorAndSaveAsync(
                paletteSrc, recolorInstruction, recoloredPng,
                request.Boxes, HttpContext.RequestAborted);
            if (recolorResult == null)
                return Json(new { success = false, error = "Palette swap (draft) step failed" });

            // ── BruteForceOnly: GLB straight from the draft, no Flux ──
            // skipCleanup: the super-res source is already sharp, so the 4x
            // up→down cleanup round-trip is pointless work (same as Variations).
            if (request.BruteForceOnly || !request.ChainToAI)
            {
                return await BuildPreviewResponse(request.DisplayId, recoloredPng,
                    "palette_only", "palette_swapped", skipCleanup: true);
            }

            // ── Stage 2: Flux img2img polish on top of the draft ──
            float denoise = request.AIDenoise > 0.01f
                ? Math.Clamp(request.AIDenoise, 0.1f, 0.8f) : 0.35f;
            string polishStyle = string.IsNullOrWhiteSpace(request.StyleDirection)
                ? request.Instruction : request.StyleDirection;

            // Same trick the immediate-commit path uses: swap the preview
            // with the draft, run Flux, restore. We only need the PNG out
            // (not the BLP/DB/MPQ).
            string backupPath = previewPath + ".bak";
            System.IO.File.Copy(previewPath, backupPath, true);
            System.IO.File.Copy(recoloredPng, previewPath, true);
            try
            {
                var polishReq = new RetextureRequest
                {
                    DisplayId = request.DisplayId,
                    ItemName = request.ItemName,
                    OriginalBlpFilename = targetTex.Filename,
                    OriginalMpqPath = targetTex.MpqPath,
                    ModifyExisting = true,
                    DenoiseStrength = denoise,
                    StyleDirection = polishStyle,
                    // Leave CustomPrompt null → Ollama crafts from polishStyle,
                    // same as the immediate-commit path does.
                };
                var polishedPng = await _retexture.RenderToPngAsync(
                    polishReq, HttpContext.RequestAborted);
                if (polishedPng == null)
                {
                    // Flux failed — fall back to the draft, same behavior as
                    // the immediate-commit path.
                    _logger.LogWarning(
                        "PreviewPaletteGlb: Flux polish failed, falling back to draft");
                    return await BuildPreviewResponse(request.DisplayId, recoloredPng,
                        "palette_draft_fallback", "palette_swapped");
                }
                return await BuildPreviewResponse(request.DisplayId, polishedPng,
                    "palette+flux", "retexture_resized");
            }
            finally
            {
                if (System.IO.File.Exists(backupPath))
                {
                    System.IO.File.Copy(backupPath, previewPath, true);
                    System.IO.File.Delete(backupPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreviewPaletteGlb: failed for displayId {Id}",
                request.DisplayId);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Items/PreviewFluxGlb
    /// Run Flux (txt2img or img2img) and return a throwaway GLB url + PNG
    /// url, WITHOUT writing the DB row or rebuilding the patch. Mirrors the
    /// scratch/modify path of /Items/Retexture but stops at PNG.
    /// Body: RetextureRequest (same as /Items/Retexture).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> PreviewFluxGlb([FromBody] RetextureRequest request)
    {
        var (targetTex, _, err) = ResolveTargetTexture(
            request.DisplayId, request.OriginalMpqPath, request.OriginalBlpFilename);
        if (targetTex == null)
            return Json(new { success = false, error = err });

        try
        {
            // Normalize the request to the target tex we resolved (handles the
            // case where the caller passed a non-canonical mpqPath/filename).
            var req = new RetextureRequest
            {
                DisplayId = request.DisplayId,
                ItemName = request.ItemName,
                OriginalBlpFilename = targetTex.Filename,
                OriginalMpqPath = targetTex.MpqPath,
                ModifyExisting = request.ModifyExisting,
                DenoiseStrength = request.DenoiseStrength,
                StyleDirection = request.StyleDirection,
                CustomPrompt = request.CustomPrompt,
            };
            // RenderToPngAsync internally decides img2img vs txt2img based on
            // ModifyExisting + whether HasPreview holds.
            var fluxPngPath = await _retexture.RenderToPngAsync(
                req, HttpContext.RequestAborted);
            if (fluxPngPath == null)
                return Json(new { success = false, error = "Flux generation failed or timed out" });

            return await BuildPreviewResponse(request.DisplayId, fluxPngPath,
                request.ModifyExisting ? "flux_img2img" : "flux_txt2img",
                "retexture_resized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreviewFluxGlb: failed for displayId {Id}",
                request.DisplayId);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Items/PreviewVariationGlb
    /// Build a throwaway preview GLB for a chosen Variations gallery card.
    /// The card carries the instruction string (same one used to render the
    /// gallery thumbnail); we re-render it through the brute-force palette
    /// swap into a temp PNG and wrap it in a GLB. WITHOUT writing the DB row
    /// or rebuilding the patch.
    /// Body: PaletteSwapRequest with Instruction set to the variant's recipe.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> PreviewVariationGlb([FromBody] PaletteSwapRequest request)
    {
        if (!_palette.IsAvailable)
            return Json(new { success = false, error = "Vision model not configured" });
        if (string.IsNullOrWhiteSpace(request.Instruction))
            return Json(new { success = false, error = "No variant instruction supplied" });

        var (targetTex, previewPath, err) = ResolveTargetTexture(
            request.DisplayId, request.OriginalMpqPath, request.OriginalBlpFilename);
        if (targetTex == null)
            return Json(new { success = false, error = err });
        if (previewPath == null)
            return Json(new { success = false, error = "Source texture preview not available" });

        try
        {
            var dir = Path.Combine(_env.WebRootPath,
                "item_textures_cache", "variation_preview");
            Directory.CreateDirectory(dir);
            string outPng = Path.Combine(dir,
                $"var_{request.DisplayId}_{Guid.NewGuid():N}.png");

            // Recolor the cached super-res source (same one the gallery used) so
            // the 3D preview — and the BLP the commit encodes from this PNG — is
            // sharp at the configured multiple. Degrades to native on failure.
            string recolorSrc = await GetUpscaledSourceAsync(previewPath, HttpContext.RequestAborted);

            var result = await _palette.RecolorAndSaveAsync(
                recolorSrc, request.Instruction, outPng, request.Boxes,
                HttpContext.RequestAborted);
            if (result == null)
                return Json(new { success = false, error = "Variation render failed" });

            return await BuildPreviewResponse(request.DisplayId, outPng,
                "variation_preview", "variation_preview", skipCleanup: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreviewVariationGlb: failed for displayId {Id}",
                request.DisplayId);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Items/CommitStagedRetexture
    /// Universal commit. Takes a server-side PNG path produced by any
    /// Preview* endpoint and runs it through RetextureFromBitmapAsync —
    /// the same code path CommitSegmentedRetexture uses — so the committed
    /// texture is byte-identical to what was previewed on the character.
    /// pngPath is validated to live under wwwroot/item_textures_cache/.
    /// Body: { pngPath, displayId, itemName, originalMpqPath,
    ///         originalBlpFilename, styleDirection, mode }.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CommitStagedRetexture([FromBody] JsonElement body)
    {
        string? rawPath = body.TryGetProperty("pngPath", out var p) ? p.GetString() : null;
        var validatedPath = ValidateStagedPngPath(rawPath);
        if (validatedPath == null)
            return Json(new { success = false, error = "Invalid or missing preview PNG path" });

        uint displayId = body.TryGetProperty("displayId", out var d) && d.TryGetUInt32(out var v) ? v : 0;
        string itemName = body.TryGetProperty("itemName", out var n) ? (n.GetString() ?? "") : "";
        string mpqPath = body.TryGetProperty("originalMpqPath", out var mp) ? (mp.GetString() ?? "") : "";
        string blpName = body.TryGetProperty("originalBlpFilename", out var bn) ? (bn.GetString() ?? "") : "";
        string styleDir = body.TryGetProperty("styleDirection", out var s) ? (s.GetString() ?? "") : "";
        string mode = body.TryGetProperty("mode", out var m) ? (m.GetString() ?? "staged_commit") : "staged_commit";

        var (targetTex, _, err) = ResolveTargetTexture(displayId, mpqPath, blpName);
        if (targetTex == null)
            return Json(new { success = false, error = err });

        var req = new RetextureRequest
        {
            DisplayId = displayId,
            ItemName = itemName,
            OriginalBlpFilename = targetTex.Filename,
            OriginalMpqPath = targetTex.MpqPath,
            StyleDirection = string.IsNullOrEmpty(styleDir) ? $"[{mode}]" : styleDir,
        };
        var result = await _retexture.RetextureFromBitmapAsync(
            req, validatedPath, HttpContext.RequestAborted);

        return Json(new
        {
            success = result.Success,
            error = result.Error,
            previewUrl = result.GeneratedPngPath,
            patchUrl = result.PatchMpqPath,
            newDisplayId = result.NewDisplayId,
            originalWidth = result.OriginalWidth,
            originalHeight = result.OriginalHeight,
            originalFormat = result.OriginalFormat,
            blpSize = result.BlpSizeBytes,
            mode
        });
    }

    // ══════════════════════════════════════════════════════════════
    //  LOOTIFIER RETEXTURE QUEUE
    //
    //  The Lootifiers generate many variants per base item, but a retexture is
    //  slow (recolor → BLP → MPQ patch), so doing it inline would stall a batch
    //  commit. Instead the Lootifiers ENQUEUE one job per (base item × colour
    //  tier) — improved / power / glory / gods — and this processes the queue in
    //  small batches so the UI can drive a progress bar.
    //
    //  One retexture per tier is shared by every variant in that tier: the job
    //  carries the variant entry list, and on success all of them get the new
    //  display_id. Works for items with no 3D model, since it operates on the
    //  BLP texture rather than the GLB.
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Default recolor THEME per canonical tier (rarity-coded). A theme — not a
    /// prose instruction — because it goes through VariationRecipeService exactly
    /// like the Variations gallery, which is the path proven to work with no
    /// vision model configured.
    /// </summary>
    /// <summary>
    /// NO DEFAULT THEME. Blank is the signal for the seeded recolor.
    ///
    /// This used to be a rarity lookup table:
    ///     improved → "polished silver steel"   (green)
    ///     power    → "cobalt blue"             (blue)
    ///     glory    → "royal purple"            (epic)
    ///     gods     → "molten gold and fire"    (legendary)
    ///
    /// which is not a design, it is a `switch`. It painted every item the colour
    /// of its own rarity: every epic purple, every legendary orange, regardless of
    /// what the item actually was. And because an absolute colour target pulls
    /// EVERY family toward one hue, it flattened the hand-painted contrast between
    /// leather, metal and cloth into a single mush.
    ///
    /// A blank theme now routes to PaletteSwapService.RecolorSeededAsync, which
    /// derives the colourway from a (base item × tier) seed. Typing a theme into
    /// the modal still overrides it per tier — the LLM/instruction path is intact.
    /// </summary>
    public static string DefaultTierTheme(string tier) => "";

    /// <summary>
    /// Canonical colour tier for a tracked variant. Mirrors the Lootifiers'
    /// CanonicalTier so grouping here matches the colour ladder they applied.
    /// </summary>
    private static string CanonicalTierOf(string? tierName, float budgetPct)
    {
        var l = (tierName ?? "").ToLowerInvariant();
        if (l.Contains("god") || l.Contains("legend") || l.Contains("immortal") || l.Contains("azeroth")) return "gods";
        if (l.Contains("glory") || l.Contains("fury")) return "glory";
        if (l.Contains("power")) return "power";
        if (l.Contains("improv")) return "improved";
        if (budgetPct >= 98f) return "gods";
        if (budgetPct >= 90f) return "glory";
        if (budgetPct >= 80f) return "power";
        return "improved";
    }

    // The three Lootifiers share lootifier_generated_items, distinguished by the
    // creature_entry sentinel: quest = 0, crafting = -1, loot/ARPG = a real entry.
    private static string SourceFilterSql(string source) => source switch
    {
        "quest" => "gi.creature_entry = 0",
        "crafting" => "gi.creature_entry = -1",
        "loot" => "gi.creature_entry > 0",
        _ => "1=0"
    };

    private static readonly string[] RETEXTURE_SOURCES = { "quest", "crafting", "loot" };

    /// <summary>
    /// GET /Items/LootifierRetextureSources — what's available to retexture, per
    /// Lootifier source, so the modal can show counts before you commit to a run.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> LootifierRetextureSources()
    {
        using var adminConn = _db.Admin();
        await EnsureRetextureQueueTable(adminConn);

        if (!await TableExists(adminConn, "lootifier_generated_items"))
            return Json(new { success = true, sources = Array.Empty<object>(), note = "No lootifier data yet" });

        var outSources = new List<object>();
        foreach (var src in RETEXTURE_SOURCES)
        {
            var stats = await adminConn.QueryFirstOrDefaultAsync<dynamic>($@"
                SELECT COUNT(DISTINCT gi.base_entry) AS bases, COUNT(*) AS variants
                FROM lootifier_generated_items gi
                WHERE {SourceFilterSql(src)}");

            int queued = await adminConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM lootifier_retexture_queue WHERE source = @S", new { S = src });
            int done = await adminConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM lootifier_retexture_queue WHERE source = @S AND status = 'done'", new { S = src });

            outSources.Add(new
            {
                source = src,
                label = src switch { "quest" => "Quest Rewards", "crafting" => "Crafted Items", _ => "Loot / ARPG" },
                bases = stats != null ? (int)(long)stats.bases : 0,
                variants = stats != null ? (int)(long)stats.variants : 0,
                queued,
                done
            });
        }

        return Json(new
        {
            success = true,
            sources = outSources,
            defaultThemes = new
            {
                improved = DefaultTierTheme("improved"),
                power = DefaultTierTheme("power"),
                glory = DefaultTierTheme("glory"),
                gods = DefaultTierTheme("gods")
            }
        });
    }

    /// <summary>
    /// POST /Items/BuildRetextureQueue — scan the selected Lootifier sources and
    /// queue ONE recolor per (base item × colour tier). Every variant in a tier
    /// shares the resulting display_id, so a base with 10 variants queues ≤ 4 jobs.
    /// Body: { sources: ["quest","crafting","loot"], themes: {improved,power,glory,gods}, requeue?: bool }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> BuildRetextureQueue([FromBody] JsonElement body)
    {
        var sources = new List<string>();
        if (body.TryGetProperty("sources", out var sEl) && sEl.ValueKind == JsonValueKind.Array)
            foreach (var s in sEl.EnumerateArray())
            {
                var v = s.GetString();
                if (v != null && RETEXTURE_SOURCES.Contains(v)) sources.Add(v);
            }
        if (sources.Count == 0)
            return Json(new { success = false, error = "No sources selected" });

        bool requeue = body.TryGetProperty("requeue", out var rq) && rq.ValueKind == JsonValueKind.True;

        var themes = new Dictionary<string, string>();
        if (body.TryGetProperty("themes", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
            foreach (var p in tEl.EnumerateObject())
            {
                var v = p.Value.GetString();
                if (!string.IsNullOrWhiteSpace(v)) themes[p.Name] = v!;
            }

        using var adminConn = _db.Admin();
        using var mangosConn = _db.Mangos();
        await EnsureRetextureQueueTable(adminConn);

        if (!await TableExists(adminConn, "lootifier_generated_items"))
            return Json(new { success = false, error = "No lootifier data found" });

        int queued = 0, skipped = 0, basesCovered = 0, noDisplay = 0, ineligible = 0;

        foreach (var src in sources)
        {
            var tracked = (await adminConn.QueryAsync<dynamic>($@"
                SELECT gi.base_entry, gi.generated_entry, gi.tier_name, gi.budget_pct
                FROM lootifier_generated_items gi
                WHERE {SourceFilterSql(src)}")).ToList();
            if (tracked.Count == 0) continue;

            // Existing queue keys for this source, so a re-run doesn't duplicate.
            var existing = new HashSet<string>((await adminConn.QueryAsync<dynamic>(
                "SELECT base_entry, tier FROM lootifier_retexture_queue WHERE source = @S", new { S = src }))
                .Select(r => $"{(int)r.base_entry}|{(string)r.tier}"));

            var baseEntries = tracked.Select(t => (int)t.base_entry).Distinct().ToList();

            // Base display_id + name live in the world DB (cross-database, so a
            // second query rather than a join). Explicitly typed dictionary: a
            // dynamic-inferred one can't be deconstructed later (CS8133).
            var baseInfo = new Dictionary<int, (string name, int displayId, int invType)>();
            foreach (var r in await mangosConn.QueryAsync<dynamic>(@"
                SELECT entry, name, display_id, inventory_type FROM item_template
                WHERE entry IN @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = item_template.entry)",
                new { E = baseEntries }))
            {
                int e = (int)r.entry;
                string nm = (string)r.name;
                int did = (int)(uint)r.display_id;
                int iv = Convert.ToInt32(r.inventory_type);
                baseInfo[e] = (nm, did, iv);
            }

            foreach (var grp in tracked.GroupBy(t => (int)t.base_entry))
            {
                int baseEntry = grp.Key;
                if (!baseInfo.TryGetValue(baseEntry, out var info)) continue;
                var (itemName, displayId, invType) = info;
                if (displayId == 0) { noDisplay++; continue; }   // nothing to recolor

                // Eligibility: necks, rings, trinkets, bags, ammo, quivers and relics
                // have a display_id but NO texture any system can reach — no model, no
                // body-atlas slots, no cape BLP. Queuing them just manufactures
                // failures: 552 of the 857 in the July batch were exactly this, each
                // one burning a job slot to arrive at "No textures found". Skip them
                // at the source.
                if (KindForInventoryType(invType) == KIND_NONE) { ineligible++; continue; }

                basesCovered++;

                var byTier = grp.GroupBy(t =>
                    CanonicalTierOf((string?)t.tier_name, (float)t.budget_pct));

                foreach (var tg in byTier)
                {
                    string tier = tg.Key;
                    string key = $"{baseEntry}|{tier}";

                    if (existing.Contains(key))
                    {
                        if (!requeue) { skipped++; continue; }
                        await adminConn.ExecuteAsync(
                            "DELETE FROM lootifier_retexture_queue WHERE source = @S AND base_entry = @B AND tier = @T",
                            new { S = src, B = baseEntry, T = tier });
                    }

                    string entries = string.Join(",", tg.Select(x => (int)x.generated_entry).Distinct());
                    string name = itemName.Length > 255 ? itemName.Substring(0, 255) : itemName;

                    await adminConn.ExecuteAsync(@"
                        INSERT INTO lootifier_retexture_queue
                            (source, base_entry, base_display_id, item_name, tier, variant_entries,
                             theme, instruction, status, created_at)
                        VALUES (@S, @B, @Did, @Name, @Tier, @Entries, @Theme, '', 'pending', NOW())",
                        new
                        {
                            S = src,
                            B = baseEntry,
                            Did = displayId,
                            Name = name,
                            Tier = tier,
                            Entries = entries,
                            // Blank → ProcessOneRetextureJob substitutes DefaultTierTheme(tier).
                            Theme = themes.GetValueOrDefault(tier, "")
                        });
                    queued++;
                }
            }
        }

        return Json(new { success = true, queued, skipped, basesCovered, noDisplay, ineligible });
    }

    private async Task<bool> TableExists(MySqlConnector.MySqlConnection conn, string table) =>
        await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @T",
            new { T = table }) > 0;

    /// <summary>
    /// Typed row for lootifier_retexture_queue. Strongly typed on purpose: passing
    /// a `dynamic` into ProcessOneRetextureJob would make the call itself dynamic,
    /// so its tuple result comes back as `dynamic` and can't be deconstructed (CS8133).
    /// </summary>
    private class RetextureJobRow
    {
        public int id { get; set; }
        public string source { get; set; } = "";
        public int base_entry { get; set; }
        public int base_display_id { get; set; }
        public string item_name { get; set; } = "";
        public string tier { get; set; } = "";
        public string variant_entries { get; set; } = "";
        public string theme { get; set; } = "";
        public string instruction { get; set; } = "";
        public string status { get; set; } = "";
        public int new_display_id { get; set; }
        public string? error { get; set; }
    }

    private async Task EnsureRetextureQueueTable(MySqlConnector.MySqlConnection adminConn)
    {
        await adminConn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS lootifier_retexture_queue (
                id INT AUTO_INCREMENT PRIMARY KEY,
                source VARCHAR(24) NOT NULL DEFAULT 'quest',
                base_entry INT NOT NULL,
                base_display_id INT NOT NULL,
                item_name VARCHAR(255) NOT NULL DEFAULT '',
                tier VARCHAR(32) NOT NULL,
                variant_entries TEXT NOT NULL,
                theme VARCHAR(128) NOT NULL DEFAULT '',
                instruction VARCHAR(512) NOT NULL DEFAULT '',
                status VARCHAR(16) NOT NULL DEFAULT 'pending',
                new_display_id INT NOT NULL DEFAULT 0,
                error VARCHAR(512) NULL,
                created_at DATETIME NOT NULL,
                processed_at DATETIME NULL,
                INDEX idx_status (status),
                INDEX idx_base (base_entry)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
    }

    /// <summary>GET /Items/RetextureQueueStatus — pending/done/failed counts.</summary>
    [HttpGet]
    public async Task<IActionResult> RetextureQueueStatus()
    {
        using var adminConn = _db.Admin();
        await EnsureRetextureQueueTable(adminConn);

        var rows = (await adminConn.QueryAsync<dynamic>(
            "SELECT status, COUNT(*) AS n FROM lootifier_retexture_queue GROUP BY status")).ToList();

        int pending = 0, done = 0, failed = 0;
        foreach (var r in rows)
        {
            int n = (int)(long)r.n;
            switch ((string)r.status)
            {
                case "pending": pending = n; break;
                case "done": done = n; break;
                case "failed": failed = n; break;
            }
        }

        var failures = (await adminConn.QueryAsync<dynamic>(@"
            SELECT base_entry, item_name, tier, error FROM lootifier_retexture_queue
            WHERE status = 'failed' ORDER BY id DESC LIMIT 20")).ToList();

        return Json(new
        {
            success = true,
            pending,
            done,
            failed,
            // Informational only — the queue does NOT require it. Without the vision
            // model the recolor still runs (hard palette swaps via the regex parser),
            // exactly like the Variations gallery.
            llmAssistAvailable = _palette.IsAvailable,
            failures = failures.Select(f => new
            {
                baseEntry = (int)f.base_entry,
                itemName = (string)f.item_name,
                tier = (string)f.tier,
                error = (string?)f.error
            })
        });
    }

    /// <summary>
    /// POST /Items/ProcessRetextureQueue — process up to `max` pending jobs
    /// (default 3; each is slow). Call repeatedly from the UI until pending = 0.
    /// Body: { max?: int }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ProcessRetextureQueue([FromBody] JsonElement body)
    {
        int max = body.ValueKind == JsonValueKind.Object
                  && body.TryGetProperty("max", out var m) && m.TryGetInt32(out var mv)
            ? Math.Clamp(mv, 1, 25) : 3;

        // NOTE: no _palette.IsAvailable gate. The recolor engine is the same one
        // behind the Variations gallery, which works with NO vision model — the
        // LLM is only an optional instruction-cleanup step, and RecolorAndSaveAsync
        // falls back to its regex instruction parser (hard palette swaps) without it.

        using var adminConn = _db.Admin();
        using var mangosConn = _db.Mangos();
        await EnsureRetextureQueueTable(adminConn);

        var jobs = (await adminConn.QueryAsync<RetextureJobRow>(
            "SELECT * FROM lootifier_retexture_queue WHERE status = 'pending' ORDER BY id LIMIT @Max",
            new { Max = max })).ToList();

        // inventory_type drives the texture-system routing (see KindForInventoryType).
        // It lives in the world DB, so one lookup for the whole batch rather than a
        // query per job. Missing → 0 → KIND_NONE → the job fails loudly instead of
        // being silently misrouted.
        var invTypes = new Dictionary<int, int>();
        if (jobs.Count > 0)
        {
            var baseEntries = jobs.Select(j => j.base_entry).Distinct().ToList();
            foreach (var r in await mangosConn.QueryAsync<dynamic>(
                "SELECT entry, inventory_type FROM item_template WHERE entry IN @E",
                new { E = baseEntries }))
            {
                invTypes[(int)r.entry] = Convert.ToInt32(r.inventory_type);
            }
        }

        int processed = 0, succeeded = 0, failedCount = 0, itemsRestyled = 0;
        var results = new List<object>();

        foreach (var job in jobs)
        {
            int id = job.id;
            processed++;

            try
            {
                int invType = invTypes.GetValueOrDefault(job.base_entry, 0);
                var (ok, err, newDid) = await ProcessOneRetextureJob(job, invType, HttpContext.RequestAborted);

                if (!ok || newDid == 0)
                {
                    failedCount++;
                    string emsg = err ?? "unknown error";
                    if (emsg.Length > 500) emsg = emsg.Substring(0, 500);
                    await adminConn.ExecuteAsync(@"
                        UPDATE lootifier_retexture_queue
                        SET status = 'failed', error = @E, processed_at = NOW() WHERE id = @Id",
                        new { E = emsg, Id = id });
                    results.Add(new { id, tier = job.tier, ok = false, error = err });
                    continue;
                }

                // Every variant in this tier shares the retextured display.
                var entries = ParseEntryCsv(job.variant_entries);
                if (entries.Count > 0)
                {
                    await mangosConn.ExecuteAsync(
                        "UPDATE item_template SET display_id = @Did WHERE entry IN @E",
                        new { Did = newDid, E = entries });
                    itemsRestyled += entries.Count;
                }

                await adminConn.ExecuteAsync(@"
                    UPDATE lootifier_retexture_queue
                    SET status = 'done', new_display_id = @Did, error = NULL, processed_at = NOW()
                    WHERE id = @Id",
                    new { Did = (int)newDid, Id = id });

                succeeded++;
                results.Add(new { id, tier = job.tier, ok = true, newDisplayId = newDid, variants = entries.Count });
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex, "Retexture queue job {Id} failed", id);
                string msg = ex.Message.Length > 500 ? ex.Message.Substring(0, 500) : ex.Message;
                await adminConn.ExecuteAsync(@"
                    UPDATE lootifier_retexture_queue
                    SET status = 'failed', error = @E, processed_at = NOW() WHERE id = @Id",
                    new { E = msg, Id = id });
                results.Add(new { id, tier = job.tier, ok = false, error = ex.Message });
            }
        }

        int remaining = await adminConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM lootifier_retexture_queue WHERE status = 'pending'");

        // ── Rebuild patch-4.MPQ ONCE, when the queue drains ──
        // Individual jobs commit with rebuildPatch:false. The patch is a pure
        // function of the retexture tables, so one rebuild at the end produces
        // exactly what N rebuilds would have — for 1/N the work. If the run is
        // interrupted, the DB still holds every committed row; the next
        // ProcessRetextureQueue call that empties the queue (or an explicit
        // POST /Items/RebuildRetexturePatch) brings the patch back in sync.
        bool patchRebuilt = false;
        string? patchError = null;
        if (remaining == 0 && succeeded > 0)
        {
            var rb = await _retexture.RebuildPatchMAsync();
            patchRebuilt = rb.Success;
            patchError = rb.Error;
            _logger.LogInformation(
                "Retexture queue: drained — patch-4.MPQ rebuild success={Ok} entries={N}",
                rb.Success, rb.TotalEntries);
        }

        return Json(new
        {
            success = true,
            processed,
            succeeded,
            failed = failedCount,
            itemsRestyled,
            remaining,
            patchRebuilt,
            patchError,
            results
        });
    }

    /// <summary>
    /// POST /Items/RebuildRetexturePatch — force a patch-4.MPQ rebuild from the
    /// retexture tables. The queue does this automatically when it drains; this is
    /// the escape hatch for an interrupted run, or after a code change to the
    /// packing step (e.g. the component-BLP gender suffix fix) that requires
    /// re-emitting the archive from already-committed rows.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RebuildRetexturePatch()
    {
        var rb = await _retexture.RebuildPatchMAsync();
        return Json(new
        {
            success = rb.Success,
            error = rb.Error,
            patchUrl = rb.PatchWebPath,
            entries = rb.TotalEntries,     // DB rows / display groups
            mpqFiles = rb.MpqFileCount     // files actually IN the archive — the number that matters
        });
    }

    /// <summary>POST /Items/ResetRetextureQueue — requeue failures, or clear all. Body: { clear?: bool }</summary>
    [HttpPost]
    public async Task<IActionResult> ResetRetextureQueue([FromBody] JsonElement body)
    {
        bool clear = body.ValueKind == JsonValueKind.Object
                     && body.TryGetProperty("clear", out var c) && c.ValueKind == JsonValueKind.True;

        using var adminConn = _db.Admin();
        await EnsureRetextureQueueTable(adminConn);

        int affected = clear
            ? await adminConn.ExecuteAsync("DELETE FROM lootifier_retexture_queue")
            : await adminConn.ExecuteAsync(
                "UPDATE lootifier_retexture_queue SET status = 'pending', error = NULL, processed_at = NULL WHERE status = 'failed'");

        return Json(new { success = true, affected, cleared = clear });
    }

    // ══════════════════════════════════════════════════════════════
    //  ITEM KIND — resolved from inventory_type, NOT from "whatever
    //  resolver happens to answer first".
    //
    //  The old router tried the body atlas, then the model path, then the cape
    //  path, and took the first non-empty answer. That is a guess, and it was
    //  wrong 67 times: a helm or a shoulder whose ItemDisplayInfo row happens to
    //  carry one stray m_texture[] slot got swallowed by the atlas path, had an
    //  arm patch nobody can see recolored, and was marked done — while the actual
    //  pauldron/helm skin kept its vanilla texture. The DB knows exactly what kind
    //  of item this is. Ask it.
    //
    //  Vanilla 1.12 inventory_type:
    //    1 head · 2 neck · 3 shoulders · 4 shirt · 5 chest · 6 waist · 7 legs
    //    8 feet · 9 wrists · 10 hands · 11 finger · 12 trinket · 13 weapon
    //    14 shield · 15 ranged · 16 cloak · 17 2h · 18 bag · 19 tabard · 20 robe
    //    21 mainhand · 22 offhand · 23 holdable · 24 ammo · 25 thrown
    //    26 rangedright · 27 quiver · 28 relic
    // ══════════════════════════════════════════════════════════════

    private const string KIND_ATLAS = "atlas";   // paints m_texture[0..7] into the body atlas
    private const string KIND_MODEL = "model";   // own M2 + texture
    private const string KIND_CAPE = "cape";    // no M2, no atlas — ObjectComponents\Cape\
    private const string KIND_NONE = "none";    // no visual representation at all

    private static string KindForInventoryType(int invType) => invType switch
    {
        4 or 5 or 6 or 7 or 8 or 9 or 10 or 19 or 20 => KIND_ATLAS,
        16 => KIND_CAPE,
        1 or 3 or 13 or 14 or 15 or 17 or 21 or 22 or 23 or 25 or 26 => KIND_MODEL,
        // neck, finger, trinket, bag, ammo, quiver, relic: no model, no atlas
        // slots, no cape BLP. 552 of the July failures were these — they can
        // never be retextured, so they must never be queued.
        _ => KIND_NONE,
    };

    /// <summary>Item\ObjectComponents\{subdir}\ for a model item, by slot.</summary>
    private static string ObjectComponentSubdir(int invType) => invType switch
    {
        1 => "Head",
        3 => "Shoulder",
        14 => "Shield",
        _ => "Weapon",
    };

    /// <summary>
    /// Stable seed for a (base item, tier) pair.
    ///
    /// FNV-1a, NOT string.GetHashCode() — .NET randomizes string hashing per
    /// process, so GetHashCode would recolor every item differently after every
    /// service restart. The whole point of seeding is that a regen reproduces the
    /// same colours; a per-process hash would silently destroy that.
    /// </summary>
    private static int SeedFor(int baseEntry, string tier)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)baseEntry) * 16777619u;
            h = (h ^ (uint)(baseEntry >> 16)) * 16777619u;
            foreach (char c in tier ?? "") h = (h ^ c) * 16777619u;
            return (int)h;
        }
    }

    /// <summary>
    /// How hard a tier pushes. NOT which colour it picks — the hue is seeded per
    /// item, so there is no rarity colour-coding. This only controls how far the
    /// variant deviates from the original: an Improved stays close to the source's
    /// own vividness, a Gods-tier is deliberately more saturated and higher
    /// contrast. The tier reads as more intense without being reduced to a colour.
    /// </summary>
    // RETIRED as the tier axis (kept for reference / themed-path compat):
    // satScale is renormalized by the engine's spread/tent/clamp machinery and
    // lightBias is a uniform brightness shift — neither reads as tier. Measured
    // on 5770: improved→gods differed by ~0.04-0.10 uniform L and clamp-crushed
    // S. TierShape below owns the ladder now.
    private static (float satScale, float lightBias) TierIntensity(string tier) => tier switch
    {
        "improved" => (1.00f, 0.00f),
        "power" => (1.15f, 0.03f),
        "glory" => (1.30f, 0.06f),
        "gods" => (1.50f, 0.10f),
        _ => (1.00f, 0.00f),
    };

    /// <summary>
    /// Tier as VALUE STRUCTURE — the post-tent stage's knobs (see the
    /// POST-TENT TIER STAGE block in PaletteSwapService.ApplySmoothMap):
    /// kd = shadow toe (deepens, cannot crush), ku = highlight drive toward
    /// white, m = saturation headroom curve, pop = specular lift on the top-4%
    /// brightest pixels. Callers pass these WITH satScale=1, lightBias=0 —
    /// the stage owns the tier axis; stacking both double-darkens (verified
    /// on 5770). improved is deliberately all-zero: the base colourway.
    /// </summary>
    private static (float kd, float ku, float m, float pop) TierShape(string tier) => tier switch
    {
        "improved" => (0.00f, 0.00f, 0.00f, 0.00f),
        "power" => (0.05f, 0.25f, 0.20f, 0.02f),
        "glory" => (0.09f, 0.50f, 0.45f, 0.05f),
        "gods" => (0.13f, 0.85f, 0.80f, 0.10f),
        _ => (0.00f, 0.00f, 0.00f, 0.00f),
    };

    /// <summary>
    /// Progressive tier policy — how MUCH of the item each tier may replace.
    /// swapBudget = cumulative pixel share allowed to change, smallest material
    /// first (minimum one always swaps). hueLeash = how far a swapped material
    /// may roll from its own hue (180 = unleashed). improved: trim only, near
    /// its own hue — recognizably the same item. gods: full colourway swap;
    /// the span guard preserves the base↔trim contrast structure. See the
    /// TIER POLICY block in PaletteSwapService.RecolorSeededAsync.
    /// </summary>
    private static (float swapBudget, float hueLeash) TierPolicy(string tier) => tier switch
    {
        "improved" => (0.20f, 40f),
        "power" => (0.40f, 120f),
        "glory" => (0.70f, 180f),
        "gods" => (1.01f, 180f),
        _ => (1.01f, 180f),
    };

    /// <summary>Seeded recolor unless the operator explicitly typed a theme/instruction.</summary>
    private static bool UseSeededRecolor(RetextureJobRow job) =>
        string.IsNullOrWhiteSpace(job.instruction) && string.IsNullOrWhiteSpace(job.theme);

    private static List<int> ParseEntryCsv(string csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .Where(v => v > 0)
            .Distinct()
            .ToList();

    /// <summary>
    /// Run one queued tier retexture. Mirrors GenerateVariations — the path that
    /// works with NO vision model: detect families → recipe from the tier theme →
    /// brute-force palette recolor → commit (BLP → patch MPQ) → new displayId.
    /// The LLM is optional at every step: if the recipe service is unavailable or
    /// returns nothing, we hand the theme straight to RecolorAndSaveAsync, whose
    /// regex instruction parser does the hard palette swap. Operates on the BLP
    /// texture, so items with no 3D model work fine.
    /// </summary>
    private async Task<(bool ok, string? err, uint newDid)> ProcessOneRetextureJob(
        RetextureJobRow job, int invType, CancellationToken ct)
    {
        if (job.base_display_id <= 0) return (false, "base item has no display_id", 0);
        uint baseDid = (uint)job.base_display_id;
        string tier = job.tier ?? "";

        // ── Route by item kind (three distinct texture systems) ──
        // Deterministic, from inventory_type. See KindForInventoryType.
        string kind = KindForInventoryType(invType);

        switch (kind)
        {
            case KIND_ATLAS:
                {
                    var atlas = await _bodyAtlas.EnsureAtlasTexturesAsync(baseDid);
                    if (atlas == null || atlas.SlotUrls.Count == 0)
                        return (false, $"painted armor (invType {invType}) but no body-atlas slots resolved for display {baseDid}", 0);
                    return await ProcessBodyAtlasJob(job, baseDid, tier, atlas.SlotUrls, ct);
                }

            case KIND_CAPE:
                {
                    // Each stage reports its OWN failure. The old code fell through to
                    // `terr` — an error string from a completely different resolver —
                    // so all 156 cape failures said "No textures found" regardless of
                    // whether the BLP was missing, the decode threw, or the preview
                    // path was wrong. Three bugs wearing one error message.
                    var cape = _itemTextures.GetCapeTexture(baseDid);
                    if (cape == null)
                        return (false, $"cloak: no BLP under Item\\ObjectComponents\\Cape\\ for display {baseDid} (check ItemDisplayInfo TextureName1)", 0);

                    if (string.IsNullOrEmpty(cape.PreviewPngPath))
                        return (false, $"cloak: cape BLP resolved ({cape.Filename}) but produced no preview PNG", 0);

                    string capePreview = Path.Combine(_env.WebRootPath,
                        cape.PreviewPngPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    if (!System.IO.File.Exists(capePreview))
                        return (false, $"cloak: preview PNG missing on disk at {capePreview}", 0);

                    return await ProcessSingleTextureJob(job, baseDid, tier, cape, capePreview, ct);
                }

            case KIND_MODEL:
                {
                    // M2 path first — correct for weapons, shields, shoulders.
                    var (tex, previewPath, terr) = ResolveTargetTexture(baseDid, "", "");
                    if (tex != null && !string.IsNullOrEmpty(previewPath))
                        return await ProcessSingleTextureJob(job, baseDid, tier, tex, previewPath, ct);

                    // Fallback: resolve the texture straight from the DBC, no M2 needed.
                    // HELMS ALWAYS LAND HERE — their M2 is race+gender suffixed
                    // (Helm_X_HuM.m2) while ItemDisplayInfo stores the bare stem, so
                    // FindAndExtractItemM2 can never find it. 150 helms failed this way.
                    string subdir = ObjectComponentSubdir(invType);
                    var oc = _itemTextures.GetObjectComponentTexture(baseDid, subdir);
                    if (oc == null)
                        return (false, $"model item (invType {invType}): no M2 texture ({terr}) and no BLP under Item\\ObjectComponents\\{subdir}\\", 0);

                    string ocPreview = Path.Combine(_env.WebRootPath,
                        (oc.PreviewPngPath ?? "").TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    if (string.IsNullOrEmpty(oc.PreviewPngPath) || !System.IO.File.Exists(ocPreview))
                        return (false, $"model item: {subdir} BLP resolved ({oc.Filename}) but preview PNG missing", 0);

                    return await ProcessSingleTextureJob(job, baseDid, tier, oc, ocPreview, ct);
                }

            default:
                // Should be unreachable — BuildRetextureQueue filters these out.
                return (false, $"inventory_type {invType} has no texture to recolor (neck/finger/trinket/bag/ammo/quiver/relic)", 0);
        }
    }

    /// <summary>
    /// GET /Items/TheorySheet?displayId=NNNN
    ///
    /// THE FAST LOOP for judging recolor theories. Renders the item's primary
    /// texture under EVERY theory × every tier into one labeled contact-sheet
    /// PNG and returns its URL. Seconds per iteration, zero client restarts.
    ///
    /// This preview is trustworthy for COLOUR judgment specifically, and only
    /// as of today: the recolor is pure pixel math, so the PNG and the BLP that
    /// ships carry identical pixels — and the transport chain (palettized BLP,
    /// StormLib archive, sorted DBC) is now verified end-to-end in the client.
    /// What the sheet cannot show is in-engine lighting; that's what the
    /// variant archives are for, on the one or two finalists.
    /// </summary>
    /// <param name="ladder">
    /// When true, the SAME seed is used for every tier — so the tier axis shows
    /// one colour identity DEEPENING (richer, higher contrast, louder accent)
    /// instead of four unrelated re-rolls. This is the "better version of
    /// itself" reading of tiers; ladder=false is the "each tier its own
    /// colourway" reading. They are different design games — the sheet exists
    /// so you can look at both before committing a full run to either.
    /// </param>
    [HttpGet]
    public async Task<IActionResult> TheorySheet(uint displayId, int cell = 128, bool ladder = false)
    {
        // Primary source texture: largest atlas slot for painted armor, the
        // DBC-controlled model texture otherwise.
        string? srcPng = null;
        var atlas = await _bodyAtlas.EnsureAtlasTexturesAsync(displayId);
        if (atlas != null && atlas.SlotUrls.Count > 0)
        {
            var best = atlas.SlotUrls.OrderByDescending(kv =>
            {
                var pth = Path.Combine(_env.WebRootPath,
                    kv.Value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                return System.IO.File.Exists(pth) ? new FileInfo(pth).Length : 0;
            }).First();
            srcPng = Path.Combine(_env.WebRootPath,
                best.Value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        }
        else
        {
            var (tex, previewPath, _) = ResolveTargetTexture(displayId, "", "");
            if (tex != null && previewPath != null) srcPng = previewPath;
        }
        if (srcPng == null || !System.IO.File.Exists(srcPng))
            return Json(new { success = false, error = "no resolvable source texture for this display" });

        string[] tiers = { "improved", "power", "glory", "gods" };
        var theories = PaletteSwapService.RecolorTheories;

        var outDir = Path.Combine(_env.WebRootPath, "item_textures_cache", "theory_lab");
        Directory.CreateDirectory(outDir);

        int cols = tiers.Length + 1;               // +1 for the original
        int rows = theories.Length;
        int label = 84, pad = 6, header = 44;   // room for the family-diagnostics line
        int W = label + cols * (cell + pad) + pad;
        int H = header + rows * (cell + pad) + pad;

        using var sheet = new SkiaSharp.SKBitmap(W, H);
        using var canvas = new SkiaSharp.SKCanvas(sheet);
        canvas.Clear(new SkiaSharp.SKColor(24, 24, 28));
        using var text = new SkiaSharp.SKPaint
        { Color = SkiaSharp.SKColors.White, TextSize = 13, IsAntialias = true };

        // Family diagnostics — a single chromatic family makes five of the six
        // theories mathematically near-identical (they all reduce to "seed hue
        // plus a small offset" when there is nothing to arrange into a palette).
        // Say so on the sheet, or a degenerate item reads as "the theories don't
        // work" when it actually means "this texture has nothing to differ ON".
        var fams = _palette.DetectFamilies(srcPng);
        var chromaticFams = fams.Where(f => f.Family != "white" && f.Family != "black").ToList();
        string famLine = $"families: {string.Join(", ", fams.Select(f => $"{f.Family} {f.Percent:F0}%"))}";
        if (chromaticFams.Count <= 1)
            famLine += "   [SINGLE CHROMATIC FAMILY — theories degenerate; judge on a multi-family item]";
        canvas.DrawText(famLine, label + pad + (cell + pad), 14, text);

        // Column headers
        canvas.DrawText("original", label + pad, header - 8, text);
        for (int t = 0; t < tiers.Length; t++)
            canvas.DrawText(tiers[t], label + pad + (t + 1) * (cell + pad), header - 8, text);

        using (var orig = SkiaSharp.SKBitmap.Decode(srcPng))
        {
            for (int r = 0; r < rows; r++)
            {
                string theory = theories[r];
                canvas.DrawText(theory, pad, header + r * (cell + pad) + cell / 2, text);

                var oRect = SkiaSharp.SKRect.Create(label + pad, header + r * (cell + pad), cell, cell);
                if (orig != null) canvas.DrawBitmap(orig, oRect);

                for (int t = 0; t < tiers.Length; t++)
                {
                    var (kd, ku, m, pop) = TierShape(tiers[t]);   // stage owns the tier axis
                    var (budget, leash) = TierPolicy(tiers[t]);   // how much of the item may change
                    // ladder: one identity per item, tiers deepen it.
                    // non-ladder: each tier re-rolls its own colourway.
                    int seed = ladder ? SeedFor((int)displayId, "")
                                      : SeedFor((int)displayId, tiers[t]);
                    string cellPng = Path.Combine(outDir,
                        $"lab_{displayId}_{theory}_{tiers[t]}_{(ladder ? "L" : "R")}.png");

                    var ok = await _palette.RecolorSeededAsync(
                        srcPng, cellPng, seed, 1.0f, 0.0f, false,
                        HttpContext.RequestAborted, theory, kd, ku, m, pop, budget, leash);
                    if (ok == null) continue;

                    using var bmp = SkiaSharp.SKBitmap.Decode(cellPng);
                    if (bmp == null) continue;
                    var rect = SkiaSharp.SKRect.Create(
                        label + pad + (t + 1) * (cell + pad),
                        header + r * (cell + pad), cell, cell);
                    canvas.DrawBitmap(bmp, rect);
                }
            }
        }

        string sheetPath = Path.Combine(outDir, $"sheet_{displayId}{(ladder ? "_ladder" : "")}.png");
        using (var fs = System.IO.File.Create(sheetPath))
            sheet.Encode(fs, SkiaSharp.SKEncodedImageFormat.Png, 95);

        return Json(new
        {
            success = true,
            url = $"/item_textures_cache/theory_lab/sheet_{displayId}{(ladder ? "_ladder" : "")}.png",
            chromaticFamilies = chromaticFams.Count,
            theories,
            note = "rows = theories, columns = original + tiers; same seeds the queue would use"
        });
    }

    // ══════════════════════════════════════════════════════════════
    //  RECIPE CACHE    // ══════════════════════════════════════════════════════════════
    //  RECIPE CACHE
    //
    //  A recipe is a pure function of (theme, colour families). The theme is one
    //  of four fixed strings; the families come out of the texture, and thousands
    //  of items share the same family set (brown leather, grey mail, blue cloth).
    //  So the LLM was being asked the SAME question thousands of times.
    //
    //  It cost ~8 seconds per job. At 6193 jobs that is ~13.8 hours of Ollama
    //  round-trips for a recolor engine whose actual pixel work runs in
    //  milliseconds. The queue was never compute-bound — it was bound on waiting
    //  for a language model to re-derive an answer it had already given.
    //
    //  Worse, GenerateRecipesAsync returns FIVE recipes per call and the old code
    //  used recipes[0] and discarded the other four. So we cache all five and
    //  rotate through them: variety across items is preserved (arguably improved —
    //  five distinct looks instead of one non-deterministic roll), at zero
    //  additional cost.
    //
    //  Process-lifetime cache. Recipes are cosmetic and the themes are stable, so
    //  there is no invalidation concern; a restart re-warms it in a few calls.
    // ══════════════════════════════════════════════════════════════

    private static readonly ConcurrentDictionary<string, string[]> _recipeCache = new();
    private static readonly ConcurrentDictionary<string, int> _recipeCursor = new();

    /// <summary>Resolve the recolor instruction for a job (explicit → cached recipe → theme → tier default).</summary>
    private async Task<string> ResolveJobInstruction(RetextureJobRow job, string tier, string familySourcePng, CancellationToken ct)
    {
        string instruction = job.instruction ?? "";
        if (!string.IsNullOrWhiteSpace(instruction)) return instruction;

        string theme = job.theme ?? "";
        if (string.IsNullOrWhiteSpace(theme)) theme = DefaultTierTheme(tier);

        try
        {
            // DetectFamilies is local pixel work (fast) — always run it, since it
            // is what makes the cache key meaningful.
            var families = _palette.DetectFamilies(familySourcePng);

            string key = theme + "||" + string.Join(",",
                families.Select(f => f.Family ?? "").Where(f => f.Length > 0)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

            if (!_recipeCache.TryGetValue(key, out var cached))
            {
                // Ask for 5 — the service returns ~5 anyway, and we now keep them all.
                var recipes = await _variations.GenerateRecipesAsync(theme, families, 5, ct);
                cached = recipes
                    .Select(r => r.Instruction)
                    .Where(i => !string.IsNullOrWhiteSpace(i))
                    .ToArray();

                if (cached.Length > 0)
                {
                    _recipeCache[key] = cached;
                    _logger.LogInformation(
                        "Retexture queue: cached {N} recipe(s) for key '{Key}' ({Cached} keys warm)",
                        cached.Length, key, _recipeCache.Count);
                }
            }

            if (cached.Length > 0)
            {
                // Rotate so sibling items in the same tier don't all come out identical.
                int idx = _recipeCursor.AddOrUpdate(key, 0, (_, v) => v + 1);
                return cached[(idx % cached.Length + cached.Length) % cached.Length];
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                "Retexture queue: recipe generation failed ({Err}) — falling back to the raw theme", ex.Message);
        }

        // No recipe service / no LLM → the theme itself is the instruction and
        // RecolorAndSaveAsync's regex parser handles it (hard palette swap).
        return theme;
    }

    /// <summary>PAINTED ARMOR: recolor every component slot with one recipe, commit as component BLPs.</summary>
    private async Task<(bool ok, string? err, uint newDid)> ProcessBodyAtlasJob(
        RetextureJobRow job, uint baseDid, string tier,
        IReadOnlyDictionary<int, string> slotUrls, CancellationToken ct)
    {
        string DiskOf(string webUrl) => Path.Combine(_env.WebRootPath,
            webUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        // One recipe from a representative slot (chest = 3 when present), applied
        // to every slot so the whole piece recolors coherently.
        int primarySlot = slotUrls.ContainsKey(3) ? 3 : slotUrls.Keys.OrderBy(k => k).First();
        string primaryDisk = DiskOf(slotUrls[primarySlot]);
        if (!System.IO.File.Exists(primaryDisk))
            return (false, "body-atlas source PNG missing on disk", 0);

        // Seeded unless the operator typed a theme. ONE seed for the whole piece —
        // every slot of a chest/legs/gloves set must land on the same colourway or
        // the armour comes out mismatched between body regions.
        bool seeded = UseSeededRecolor(job);
        int seed = SeedFor(job.base_entry, tier);
        var (kd, ku, m, pop) = TierShape(tier);   // tier axis = post-tent stage
        var (budget, leash) = TierPolicy(tier);   // how much of the item may change

        string instruction = seeded
            ? $"seeded:{seed} shape=({kd:F2},{ku:F2},{m:F2},{pop:F2}) policy=({budget:F2},{leash:F0})"
            : await ResolveJobInstruction(job, tier, primaryDisk, ct);

        // Recolored slot PNGs must live under item_textures_cache/ to pass the
        // same staged-path validation the interactive commit uses.
        var outDir = Path.Combine(_env.WebRootPath, "item_textures_cache", "lootifier_tiers");
        Directory.CreateDirectory(outDir);

        var slotPngPaths = new Dictionary<int, string>();
        foreach (var kv in slotUrls)
        {
            string srcDisk = DiskOf(kv.Value);
            if (!System.IO.File.Exists(srcDisk)) continue;

            string outPng = Path.Combine(outDir, $"ba_{baseDid}_{tier}_s{kv.Key}_{Guid.NewGuid():N}.png");

            var ok = seeded
                ? await _palette.RecolorSeededAsync(srcDisk, outPng, seed, 1.0f, 0.0f, false, ct,
                      _config["Retexture:Theory"] ?? "fan", kd, ku, m, pop, budget, leash)
                : await _palette.RecolorAndSaveAsync(srcDisk, instruction, outPng, null, ct);

            if (ok == null) continue;
            slotPngPaths[kv.Key] = outPng;
        }

        if (slotPngPaths.Count == 0)
            return (false, "body-atlas recolor produced no slots", 0);

        // rebuildPatch: false — the queue rebuilds patch-4.MPQ ONCE when it
        // drains. Rebuilding per job repacks every BLP committed so far, which
        // makes a batch quadratic (the 5-hour run).
        var res = await _retexture.CommitBodyAtlasAsync(
            baseDid, job.item_name ?? "", instruction, slotPngPaths, ct,
            rebuildPatch: false);

        if (!res.Success) return (false, res.Error ?? "body-atlas commit failed", 0);
        return (true, null, (uint)res.NewDisplayId);
    }

    /// <summary>
    /// SINGLE-TEXTURE ITEMS: recolor one BLP and commit it. Serves both model
    /// items (weapons/shields/helms/shoulders) and capes — the texture is already
    /// resolved by the caller, so the only difference is which MPQ folder it came
    /// from, which RetextureRequest carries through OriginalMpqPath.
    /// </summary>
    private async Task<(bool ok, string? err, uint newDid)> ProcessSingleTextureJob(
        RetextureJobRow job, uint baseDid, string tier,
        ItemTextureEntry tex, string previewPath, CancellationToken ct)
    {
        bool seeded = UseSeededRecolor(job);
        int seed = SeedFor(job.base_entry, tier);
        var (kd, ku, m, pop) = TierShape(tier);   // tier axis = post-tent stage
        var (budget, leash) = TierPolicy(tier);   // how much of the item may change

        string instruction = seeded
            ? $"seeded:{seed} shape=({kd:F2},{ku:F2},{m:F2},{pop:F2}) policy=({budget:F2},{leash:F0})"
            : await ResolveJobInstruction(job, tier, previewPath, ct);

        var outDir = Path.Combine(_env.WebRootPath, "item_textures_cache", "lootifier_tiers");
        Directory.CreateDirectory(outDir);
        string outPng = Path.Combine(outDir, $"tier_{baseDid}_{tier}_{Guid.NewGuid():N}.png");

        // Same super-res source the Variations gallery uses, so the committed
        // texture is as sharp as a previewed card.
        string src = await GetUpscaledSourceAsync(previewPath, ct);

        var recolored = seeded
            ? await _palette.RecolorSeededAsync(src, outPng, seed, 1.0f, 0.0f, false, ct,
                  _config["Retexture:Theory"] ?? "fan", kd, ku, m, pop, budget, leash)
            : await _palette.RecolorAndSaveAsync(src, instruction, outPng, null, ct);

        if (recolored == null) return (false, "palette recolor failed", 0);

        var req = new RetextureRequest
        {
            DisplayId = baseDid,
            ItemName = job.item_name ?? "",
            OriginalBlpFilename = tex.Filename,
            OriginalMpqPath = tex.MpqPath,
            StyleDirection = instruction,
        };

        // preResolved: tex — the entry we already resolved above. Without it the
        // commit re-derives the texture through the M2-only path, which returns
        // null for capes and helms and kills the job AFTER the recolor has run.
        // rebuildPatch: false — see ProcessBodyAtlasJob.
        var res = await _retexture.RetextureFromBitmapAsync(
            req, outPng, ct, rebuildPatch: false, preResolved: tex);
        if (!res.Success) return (false, res.Error ?? "retexture commit failed", 0);

        return (true, null, (uint)res.NewDisplayId);
    }

    /// <summary>
    /// POST /Items/CommitBodyAtlasRetexture
    /// Persists a body-atlas (painted armor) retexture to patch-4.MPQ. The
    /// painted-armor analog of CommitStagedRetexture: instead of one staged PNG
    /// → one BLP → DBC field 3, it takes the per-slot recolored PNGs (the
    /// slotUrls a GenerateBodyAtlasVariations card produced and the client
    /// staged) and commits each as its own component BLP under one new
    /// displayId, patched into m_texture[0..7]. Every slot URL is validated to
    /// live under wwwroot/item_textures_cache/ (same guard as the universal
    /// commit), so the client can't smuggle arbitrary paths.
    /// Body: { displayId, itemName, styleDirection, slotUrls: { "5":"/..png", .. } }.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CommitBodyAtlasRetexture([FromBody] JsonElement body)
    {
        uint displayId = body.TryGetProperty("displayId", out var d) && d.TryGetUInt32(out var v) ? v : 0;
        if (displayId == 0)
            return Json(new { success = false, error = "No displayId" });

        string itemName = body.TryGetProperty("itemName", out var n) ? (n.GetString() ?? "") : "";
        string styleDir = body.TryGetProperty("styleDirection", out var s) ? (s.GetString() ?? "") : "";

        if (!body.TryGetProperty("slotUrls", out var slotsEl) || slotsEl.ValueKind != JsonValueKind.Object)
            return Json(new { success = false, error = "No slotUrls supplied" });

        // Map each slot's web URL → validated on-disk PNG path. Reuses
        // ValidateStagedPngPath: requires the file to canonicalize under
        // wwwroot/item_textures_cache/ and end in .png. Any slot that fails
        // validation is skipped (defensive — a missing slot just isn't patched).
        var slotPngPaths = new Dictionary<int, string>();
        foreach (var prop in slotsEl.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out int slot)) continue;
            if (slot < 0 || slot > 7) continue;
            string? webUrl = prop.Value.GetString();
            if (string.IsNullOrEmpty(webUrl)) continue;
            // Web URL (/item_textures_cache/..) → disk path, then validate.
            string diskPath = Path.Combine(_env.WebRootPath,
                webUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            var validated = ValidateStagedPngPath(diskPath);
            if (validated == null)
            {
                _logger.LogWarning("CommitBodyAtlasRetexture: slot {Slot} path failed validation: {Url}", slot, webUrl);
                continue;
            }
            slotPngPaths[slot] = validated;
        }

        if (slotPngPaths.Count == 0)
            return Json(new { success = false, error = "No valid slot PNGs (paths must live under item_textures_cache)" });

        var result = await _retexture.CommitBodyAtlasAsync(
            displayId, itemName, string.IsNullOrEmpty(styleDir) ? "[body-atlas]" : styleDir,
            slotPngPaths, HttpContext.RequestAborted);

        return Json(new
        {
            success = result.Success,
            error = result.Error,
            patchUrl = result.PatchMpqPath,
            newDisplayId = result.NewDisplayId,
            blpSize = result.BlpSizeBytes,
            slotsCommitted = slotPngPaths.Count,
            mode = "bodyatlas"
        });
    }


    // ===================== MODELS =====================

    /// <summary>
    /// GET /Items/ModelExists?displayId=6 — Quick check if an item GLB exists.
    /// Honors RigidGlbVersion versioning so a stale unversioned file
    /// doesn't report exists=true after a writer change.
    /// </summary>
    [HttpGet]
    public IActionResult ModelExists(uint displayId)
    {
        var filename = CacheVersionRegistry.MakeVersioned(
            $"{displayId}.glb", CacheVersionRegistry.RigidGlbVersion);
        var glbFile = Path.Combine(_env.WebRootPath, "item_models", filename);
        return Json(new { exists = System.IO.File.Exists(glbFile), path = $"/item_models/{filename}" });
    }

    /// <summary>
    /// GET /Items/CharacterPreview?race=Human&gender=Male&displayId=29863
    ///
    /// On-demand armory viewer. Triggers generation of the race/gender character
    /// GLB (skinned mesh + bones + Attachment_* nodes) if it doesn't yet exist,
    /// then renders the viewer page. displayId is plumbed through for Session C/D
    /// when armor compositing and weapon attachment kick in.
    ///
    /// Race must be one of: Human, Dwarf, NightElf, Gnome, Orc, Tauren, Troll, Scourge.
    /// Gender must be Male or Female. Anything else returns the view with a null
    /// GLB URL (the view shows an error panel).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> CharacterPreview(
        string race = "Human",
        string gender = "Male",
        uint displayId = 0)
    {
        string? glbUrl = await _characterModels.EnsureCharacterGlbAsync(race, gender);
        // Skin PNG URL is deterministic from (race, gender) once EnsureCharacterGlbAsync
        // has run (it writes both files in the same call). Publishing the
        // URL through the view lets equip.js read it from
        // `data-skin-url` instead of regex-parsing the GLB URL — which
        // would otherwise need to know the SkinPngVersion stamp.
        string? skinUrl = _characterModels.GetSkinPngUrl(race, gender);

        ViewBag.Race = race;
        ViewBag.Gender = gender;
        ViewBag.DisplayId = displayId;
        ViewBag.GlbUrl = glbUrl;
        ViewBag.SkinUrl = skinUrl;

        return View();
    }

    /// <summary>
    /// GET /Items/ItemDressing?displayId=12345[&amp;itemId=2167][&amp;race=Human&amp;gender=Male]
    ///
    /// Returns the dressing payload for one item display — the inventory
    /// type, geosetGroup variants, and body-atlas texture URLs. The
    /// client (equip.js) passes this to dresser.applyItemFilters and
    /// compositor.paintBodyAtlas.
    ///
    /// itemId is optional but strongly recommended. inventoryType is
    /// resolved by:
    ///   1. Exact match on item_template.entry = itemId.
    ///   2. Fallback: first equippable item_template row (inventory_type > 0)
    ///      that uses this display_id, ordered by entry.
    /// If both fail, inventoryType comes back as 0 and equip.js will
    /// refuse to dress — pass opts.inventoryTypeOverride to force.
    ///
    /// race + gender are required only for helms (inventoryType=1).
    /// Helm M2s live at race+gender-suffixed paths like
    /// "Helm_..._HuM.m2" so we need to know which character is wearing
    /// it. Shoulders / body-atlas items don't need these.
    ///
    /// Returns 404 if displayId isn't in ItemDisplayInfo.dbc.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ItemDressing(uint displayId, uint itemId = 0,
        string race = "Human", string gender = "Male")
    {
        var info = _dbc.GetItemModelInfo(displayId);
        if (info == null)
            return NotFound(new { error = $"displayId {displayId} not in ItemDisplayInfo.dbc" });

        // Body atlas textures — slot index → web URL.
        var atlas = await _bodyAtlas.EnsureAtlasTexturesAsync(displayId);

        // inventory_type — from item_template. We don't have it on
        // ItemDisplayInfo itself; it lives on the item that REFERENCES
        // the display.
        //
        // Strategy: prefer exact match on the caller-supplied itemId; if
        // that's missing or resolves to inventory_type=0 (trade goods like
        // Red Dye that share their displayId with armor/etc), fall back
        // to the first equippable item that shares the displayId. The
        // inventory_type=0 filter on the fallback is critical — many
        // displayIds (e.g. 9035) are shared by both gear AND junk-like
        // entries; without the filter, MIN(inventory_type) bias toward 0.
        int inventoryType = 0;
        using (var conn = _db.Mangos())
        {
            if (itemId > 0)
            {
                var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT inventory_type FROM item_template WHERE entry = @Id LIMIT 1",
                    new { Id = itemId });
                if (row != null)
                    inventoryType = (int)row.inventory_type;
            }

            if (inventoryType == 0)
            {
                // Fallback: any equippable item that uses this displayId.
                // ORDER BY entry to make the result stable across calls.
                var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT entry, inventory_type FROM item_template " +
                    "WHERE display_id = @DisplayId AND inventory_type > 0 " +
                    "ORDER BY entry LIMIT 1",
                    new { DisplayId = displayId });
                if (row != null)
                {
                    inventoryType = (int)row.inventory_type;
                    // If the caller didn't pass itemId, echo back the one
                    // we used so the client can see which item supplied
                    // the inventory_type.
                    if (itemId == 0)
                        itemId = (uint)row.entry;
                }
            }
        }

        // Session L: compute attachment GLB URLs for helm (inventoryType=1)
        // and shoulder (inventoryType=3). These are rigid M2 attachments
        // mounted to bones 11 / 5 / 6 respectively, not body-atlas items.
        // Generation is on-demand and disk-cached, same as weapon GLBs —
        // a cached hit is a single File.Exists().
        //
        // We populate only the keys relevant to this item's slot so the
        // client doesn't waste a fetch on (say) shoulder GLBs for a helm.
        // Other inventoryTypes don't have attachments — the dict stays
        // empty and the client falls through to the body-atlas pipeline.
        //
        // === Session M: weapon attachments ===
        // Vanilla handheld-item inventoryTypes:
        //   13 = One-Hand            21 = Main Hand (e.g. Thunderfury)
        //   14 = Shield              22 = Off Hand
        //   17 = Two-Hand            23 = Held in Off-Hand
        //   15 = Ranged (legacy)     26 = Ranged (bows/guns)
        //   25 = Thrown              28 = Relic (paladin librams etc —
        //                                 visually invisible but in DBC)
        //
        // All of these reuse the existing rigid-GLB pipeline (EnsureGlb).
        // GlbWriter.SaveGlb (Session M) bakes the M2's Attachment-0 offset
        // into the scene root, so the weapon's hilt/grip lands at the
        // character's hand bone when the client mounts it on Attachment_1
        // (or Attachment_2 for off-hand).
        //
        // Shields (14): mechanically identical to a one-hand weapon for
        // GLB generation. The M2 lives under Item\ObjectComponents\Shield\
        // rather than \Weapon\, but ItemTextureService.FindAndExtractItemM2
        // already searches both subdirs, so EnsureGlb just works. The
        // client mounts shields on the off-hand attachment point — see
        // equip.js routing for inventoryType==14. Originally missed from
        // this branch in Session M (only weapon types were listed in the
        // comment block, and 14 silently fell through), which manifested
        // as shields silently failing to render on dress-up — attachments
        // came back as `{}` and equip.js had no URL to load.
        //
        // The client (equip.js) reads inventoryType from the response and
        // chooses which hand attachment to mount on. 22 / 23 / 14 → left,
        // everything else → right. Relics (28) we still emit because the
        // GLB itself is harmless to render — just nothing to look at
        // usually.
        //
        // 2H weapons (17) display in the right hand at character-preview
        // scale exactly like 1H — vanilla doesn't have a "both hands"
        // attachment slot, you just hold the 2H with one hand for the
        // dress-up preview. Same convention used by wow.export, WMV, etc.
        var attachments = new Dictionary<string, string>();
        if (inventoryType == 1)
        {
            var helmUrl = _itemTextures.EnsureHelmGlb(displayId, race, gender);
            if (helmUrl != null) attachments["helm"] = helmUrl;
        }
        else if (inventoryType == 3)
        {
            var lUrl = _itemTextures.EnsureShoulderGlb(displayId, ItemTextureService.ShoulderSide.Left, race, gender);
            var rUrl = _itemTextures.EnsureShoulderGlb(displayId, ItemTextureService.ShoulderSide.Right, race, gender);
            if (_itemTextures.ShoulderFitErrors.Count > 0) return UnprocessableEntity(new { message = "Shoulder-fit resolution failed.", errors = _itemTextures.ShoulderFitErrors });
            if (lUrl != null) attachments["shoulderLeft"] = lUrl;
            if (rUrl != null) attachments["shoulderRight"] = rUrl;
        }
        else if (inventoryType is 13 or 14 or 17 or 21 or 22 or 23 or 26 or 15 or 25 or 28)
        {
            // Weapons / shields / held items — single rigid GLB. The
            // client decides right vs left hand from inventoryType
            // (echoed below).
            var weaponUrl = _itemTextures.EnsureGlb(displayId);
            if (weaponUrl != null) attachments["weapon"] = weaponUrl;
        }

        // Cloaks are character geoset 1502, not attached M2s. Their
        // ItemDisplayInfo model texture fills the character M2's type-2 slot.
        string? capeTextureUrl = null;
        if (inventoryType == 16)
            capeTextureUrl = _itemTextures.GetCapeTexture(displayId)?.PreviewPngPath;

        // Session L diagnostic: echo the ItemDisplayInfo.dbc model/texture
        // name fields (fields [1..4] of the DBC record) so the client side
        // can drive helm/shoulder attachment rendering. For body-atlas
        // items these are usually empty; for helms/shoulders/weapons they
        // carry the M2 model filename(s) and the texture name(s).
        //
        //   modelName1 / modelName2  — primary / secondary M2 model name
        //                              (helms: 1 = helm model, 2 = unused/rare;
        //                               shoulders: 1 = left, 2 = right)
        //   textureName1 / textureName2 — texture name(s) referenced by
        //                                 the M2's type-2 texture slot(s)
        return Json(new
        {
            displayId,
            itemId,
            inventoryType,
            geosetGroup = info.Value.GeosetGroup ?? new[] { 0, 0, 0 },
            bodyTextures = info.Value.BodyTextures ?? new string[8],
            slotUrls = atlas?.SlotUrls ?? new Dictionary<int, string>(),
            attachments,
            shoulderFits = _itemTextures.ShoulderFitSelections,
            capeTextureUrl,
            modelName1 = info.Value.ModelName1 ?? "",
            modelName2 = info.Value.ModelName2 ?? "",
            textureName1 = info.Value.TextureName1 ?? "",
            textureName2 = info.Value.TextureName2 ?? "",
            // m_helmetGeosetVis[0..1] — surfaced raw so the client (and
            // anyone looking at the JSON) can see what's in the DBC.
            helmetGeosetVis1 = info.Value.HelmetGeosetVis1,
            helmetGeosetVis2 = info.Value.HelmetGeosetVis2,
            // Computed: should equipping this helm hide hair?
            //
            // Proper decode via HelmetGeosetVisData.dbc: each row has a
            // hairFlags bitmask — if bit (1 << raceId) is set, hair is
            // hidden for that race. v1 is the male row, v2 is the female
            // row; we check the one matching the requested gender.
            //
            // This replaces the Session L v1!=v2 heuristic which failed
            // on helms where both vis IDs point to the same row (e.g.
            // Dreadnaught Helmet: v1=v2=368, hairFlags=0xFFFFFFFF — a
            // full closed plate helm that was incorrectly classified as
            // "open" by the heuristic).
            //
            // Verified empirically May 19 2026:
            //   Row 245 hairFlags=0x00000000 → open (Judgement Circlet)
            //   Row 247 hairFlags=0x00000000 → open (Helm of Might)
            //   Row 248 hairFlags=0xFFFFFFBF → closed (Helm of Wrath)
            //   Row 368 hairFlags=0xFFFFFFFF → closed (Dreadnaught)
            hidesHair = _dbc.DoesHelmHideHair(
                gender.Equals("Female", StringComparison.OrdinalIgnoreCase)
                    ? info.Value.HelmetGeosetVis2
                    : info.Value.HelmetGeosetVis1,
                RaceNameToId(race)),
            // Session N diagnostic: m_itemVisual — indexes ItemVisuals.dbc.
            // Non-zero means this item is supposed to render lightning,
            // glow, ribbons, or other visual effects on top of its base
            // mesh. Zero for most items. Thunderfury display 30606 is also
            // zero; its lightning is native geometry/tracks in the weapon M2.
            itemVisualId = info.Value.ItemVisualId,
        });
    }

    /// <summary>
    /// GET /Items/AttachmentDiag?displayId=X&amp;kind={helm|shoulderLeft|shoulderRight}
    ///
    /// Session L diagnostic — walks every stage of attachment GLB
    /// generation and reports what each one produced. Designed to answer
    /// "why did EnsureHelmGlb / EnsureShoulderGlb return null?" without
    /// needing server log access. Same spirit as MpqProbe / MpqExhaustivePrope:
    /// a self-contained "tell me why this didn't work" endpoint that
    /// stays useful long after this session.
    ///
    /// Stages reported (each populated if the previous succeeded):
    ///   1. dbc         — DBC lookup for displayId. Reports the four
    ///                    relevant name fields per ItemModelDbc.
    ///   2. resolution  — Which (modelName, textureName) pair this kind
    ///                    resolves to.
    ///   3. m2Probe     — Every Item\ObjectComponents\* candidate path
    ///                    tried for the model, with hit/miss + size.
    ///                    NOT a full retry — just shows which path the
    ///                    real EnsureGlb would find.
    ///   4. m2Parse     — Whether M2Reader.Parse returned a valid model;
    ///                    if so, vertex/submesh/texture-array counts.
    ///   5. textureProbe — Every candidate path for the skin BLP.
    ///   6. glb         — Did the cached GLB exist before? Does it now?
    ///                    Did EnsureXGlb return a URL?
    ///
    /// The endpoint REGENERATES the GLB as a side effect (calls
    /// EnsureHelmGlb / EnsureShoulderGlb at the end) so a successful
    /// diagnostic run also fixes the missing GLB.
    /// </summary>
    [HttpGet]
    public IActionResult AttachmentDiag(uint displayId, string kind = "helm",
        string race = "Human", string gender = "Male")
    {
        var report = new Dictionary<string, object?>
        {
            ["displayId"] = displayId,
            ["kind"] = kind,
            ["race"] = race,
            ["gender"] = gender,
        };

        // ── Stage 1: DBC lookup ──
        var info = _dbc.GetItemModelInfo(displayId);
        if (info == null)
        {
            report["stage"] = "dbc";
            report["ok"] = false;
            report["reason"] = $"displayId {displayId} not in ItemDisplayInfo.dbc";
            return Json(report);
        }
        report["dbc"] = new
        {
            modelName1 = info.Value.ModelName1 ?? "",
            modelName2 = info.Value.ModelName2 ?? "",
            textureName1 = info.Value.TextureName1 ?? "",
            textureName2 = info.Value.TextureName2 ?? "",
        };

        // ── Stage 2: kind → (model, texture) resolution ──
        // For helms, append the race+gender suffix to ModelName1's basename.
        // Shoulders don't need this — vanilla shoulder M2s are race-agnostic.
        string? modelName, textureName;
        string kindNormalized = (kind ?? "").ToLowerInvariant();
        string? helmSuffix = null;
        switch (kindNormalized)
        {
            case "helm":
                {
                    // Compute the race+gender suffix the same way
                    // EnsureHelmGlb does so the probe stays honest about
                    // which path will actually be tried.
                    var raceCode = race?.ToLowerInvariant() switch
                    {
                        "human" => "Hu",
                        "dwarf" => "Dw",
                        "gnome" => "Gn",
                        "nightelf" => "Ni",
                        "orc" => "Or",
                        "scourge" or "undead" => "Sc",
                        "tauren" => "Ta",
                        "troll" => "Tr",
                        _ => null,
                    };
                    if (raceCode == null)
                    {
                        report["stage"] = "input";
                        report["ok"] = false;
                        report["reason"] = $"unknown race '{race}'";
                        return Json(report);
                    }
                    char genderCode =
                        (gender ?? "").Equals("Female", StringComparison.OrdinalIgnoreCase) ? 'F' :
                        (gender ?? "").Equals("Male", StringComparison.OrdinalIgnoreCase) ? 'M' :
                        '\0';
                    if (genderCode == '\0')
                    {
                        report["stage"] = "input";
                        report["ok"] = false;
                        report["reason"] = $"unknown gender '{gender}' — use Male | Female";
                        return Json(report);
                    }
                    helmSuffix = $"_{raceCode}{genderCode}";

                    var rawBase = info.Value.ModelName1 ?? "";
                    var bareName = Path.GetFileNameWithoutExtension(rawBase);
                    modelName = string.IsNullOrEmpty(bareName) ? "" : bareName + helmSuffix + ".m2";
                    textureName = info.Value.TextureName1;
                    break;
                }
            case "shoulderleft":
            case "lshoulder":
                modelName = info.Value.ModelName1;
                textureName = info.Value.TextureName1;
                break;
            case "shoulderright":
            case "rshoulder":
                modelName = info.Value.ModelName2;
                textureName = !string.IsNullOrEmpty(info.Value.TextureName2)
                    ? info.Value.TextureName2
                    : info.Value.TextureName1;
                break;
            default:
                report["stage"] = "input";
                report["ok"] = false;
                report["reason"] = $"unknown kind '{kind}' — use helm | shoulderLeft | shoulderRight";
                return Json(report);
        }
        report["resolution"] = new { modelName, textureName, helmSuffix };

        if (string.IsNullOrEmpty(modelName))
        {
            report["stage"] = "resolution";
            report["ok"] = false;
            report["reason"] = $"empty modelName for kind '{kind}' — DBC field is empty";
            return Json(report);
        }

        // ── Stage 3: M2 probe ──
        // Walk the same prefixes ItemTextureService.FindAndExtractItemM2
        // uses, plus the bare path. Try every extension variant so we
        // catch case-sensitivity issues. We don't actually decode — just
        // hash-table-probe each candidate so the report is fast.
        var baseName = Path.GetFileNameWithoutExtension(modelName);
        var prefixes = new[]
        {
            @"Item\ObjectComponents\Head\",
            @"Item\ObjectComponents\Shoulder\",
            @"Item\ObjectComponents\Weapon\",
            @"Item\ObjectComponents\Shield\",
            @"Item\ObjectComponents\Quiver\",
            @"Item\ObjectComponents\Ammo\",
            "", // bare — for when modelName already contains a path
        };
        var exts = new[] { ".m2", ".mdx", ".M2", ".MDX" };

        var m2Candidates = new List<string>();
        foreach (var p in prefixes)
        {
            foreach (var e in exts)
            {
                m2Candidates.Add(p + baseName + e);
            }
        }
        // Also include the model name as-given (in case it already has extension/dir)
        m2Candidates.Add(modelName);

        var m2Hits = _mpq.FindByExactPaths(m2Candidates);
        report["m2Probe"] = new
        {
            candidatesTried = m2Candidates.Count,
            hits = m2Hits.Select(h => new { path = h.Path, archive = h.Archive, size = h.Size }).ToList(),
        };

        if (m2Hits.Count == 0)
        {
            report["stage"] = "m2Probe";
            report["ok"] = false;
            report["reason"] = $"no M2 found for '{modelName}' across {m2Candidates.Count} candidate paths";
            return Json(report);
        }

        // ── Stage 4: M2 parse ──
        // Pull the first hit's bytes and parse.
        var firstHit = m2Hits[0];
        var m2Bytes = _mpq.ExtractFile(firstHit.Path);
        if (m2Bytes == null)
        {
            report["stage"] = "m2Parse";
            report["ok"] = false;
            report["reason"] = $"SFileHasFile=true but ExtractFile returned null for {firstHit.Path}";
            return Json(report);
        }
        var m2 = M2Reader.Parse(m2Bytes);
        if (m2 == null)
        {
            report["stage"] = "m2Parse";
            report["ok"] = false;
            report["reason"] = $"M2Reader.Parse returned null for {firstHit.Path} ({m2Bytes.Length} bytes)";
            // Dump the first 16 bytes hex for triage — magic + version usually tells us what's wrong.
            var head = new System.Text.StringBuilder();
            for (int i = 0; i < Math.Min(16, m2Bytes.Length); i++)
                head.Append(m2Bytes[i].ToString("X2")).Append(' ');
            report["m2HeaderHex"] = head.ToString().TrimEnd();
            return Json(report);
        }
        report["m2Parse"] = new
        {
            valid = m2.IsValid,
            hasSkeleton = m2.HasSkeleton,
            version = m2.Version,
            name = m2.Name,
            vertexCount = m2.Vertices.Count,
            indexCount = m2.Indices.Count,
            submeshCount = m2.Submeshes.Count,
            batchCount = m2.Batches.Count,
            textureCount = m2.Textures.Count,
            textures = m2.Textures.Select(t => new
            {
                type = t.Type,
                flags = t.Flags,
                filename = t.Filename,
            }).ToList(),
        };
        if (!m2.IsValid)
        {
            report["stage"] = "m2Parse";
            report["ok"] = false;
            report["reason"] = "M2 parsed but IsValid=false (vertex count < 1 or index count < 3)";
            return Json(report);
        }

        // ── Stage 5: texture probe ──
        // FindItemBlp tries these dirs in order — we report all of them.
        var texCandidates = new List<string>();
        if (!string.IsNullOrEmpty(textureName))
        {
            string[] texDirs =
            {
                @"Item\ObjectComponents\Head\",
                @"Item\ObjectComponents\Shoulder\",
                @"Item\ObjectComponents\Weapon\",
                @"Item\ObjectComponents\Shield\",
                @"Item\ObjectComponents\Quiver\",
            };
            foreach (var d in texDirs)
                texCandidates.Add($"{d}{textureName}.blp");
        }
        var texHits = texCandidates.Count > 0
            ? _mpq.FindByExactPaths(texCandidates)
            : new List<MpqReaderService.MpqHit>();
        report["textureProbe"] = new
        {
            textureName,
            candidatesTried = texCandidates.Count,
            hits = texHits.Select(h => new { path = h.Path, archive = h.Archive, size = h.Size }).ToList(),
        };

        // ── Stage 6: actually generate the GLB (the real test) ──
        var glbDir = Path.Combine(_env.WebRootPath, "item_models");
        // Both helmets and shoulders include the requested body in their preview cache key.
        // The on-disk filename includes the RigidGlbVersion stamp via
        // CacheVersionRegistry — must match what EnsureHelmGlb /
        // EnsureShoulderGlb write so the existence checks here line up.
        string shoulderBody = ItemTextureService.ShoulderBodyCode(race, gender);
        string suffix = kindNormalized switch
        {
            "helm" => $"_helm{helmSuffix}",
            "shoulderleft" or "lshoulder" => "_lshoulder_" + shoulderBody,
            "shoulderright" or "rshoulder" => "_rshoulder_" + shoulderBody,
            _ => "_unknown",
        };
        var versionedFilename = CacheVersionRegistry.MakeVersioned(
            $"{displayId}{suffix}.glb", CacheVersionRegistry.RigidGlbVersion);
        var expectedGlbPath = Path.Combine(glbDir, versionedFilename);
        bool glbExistedBefore = System.IO.File.Exists(expectedGlbPath);

        // If a stale (failed-half-write or zero-byte) cached file is sitting
        // there, force regeneration by deleting it first. This makes
        // AttachmentDiag idempotent — running it always exercises the real
        // code path rather than short-circuiting on a cache hit.
        try
        {
            if (glbExistedBefore && new FileInfo(expectedGlbPath).Length < 1024)
                System.IO.File.Delete(expectedGlbPath);
        }
        catch { /* best-effort */ }

        string? glbUrl = kindNormalized switch
        {
            "helm" => _itemTextures.EnsureHelmGlb(displayId, race, gender),
            "shoulderleft" or "lshoulder" =>
                _itemTextures.EnsureShoulderGlb(displayId, ItemTextureService.ShoulderSide.Left, race, gender),
            "shoulderright" or "rshoulder" =>
                _itemTextures.EnsureShoulderGlb(displayId, ItemTextureService.ShoulderSide.Right, race, gender),
            _ => null,
        };
        report["shoulderFits"] = _itemTextures.ShoulderFitSelections;
        report["shoulderFitErrors"] = _itemTextures.ShoulderFitErrors;
        bool glbExistsNow = System.IO.File.Exists(expectedGlbPath);
        long glbSize = glbExistsNow ? new FileInfo(expectedGlbPath).Length : 0;

        report["glb"] = new
        {
            existedBefore = glbExistedBefore,
            existsNow = glbExistsNow,
            sizeBytes = glbSize,
            expectedPath = expectedGlbPath,
            urlReturned = glbUrl,
        };

        report["ok"] = glbUrl != null && glbExistsNow && glbSize > 0;
        report["stage"] = report["ok"] is true ? "complete" : "glb";
        if (report["ok"] is false)
        {
            report["reason"] = glbUrl == null
                ? "EnsureXGlb returned null — check server log for ItemTexture/Attachment line"
                : "EnsureXGlb returned a URL but file is missing or zero-sized on disk";
        }

        return Json(report);
    }

    /// <summary>
    /// GET /Items/DownloadPatch?file=patch-4.MPQ
    /// Serves a retexture patch MPQ for download.
    /// </summary>
    [HttpGet]
    [HttpHead]
    public IActionResult DownloadPatch(string file)
    {
        // Retextures no longer have an archive of their own: they ship in the unified patch with
        // forged weapons and armor, and wwwroot/patches/retexture is no longer written. Redirect to
        // the one deployable file rather than 404 on a path nothing produces any more.
        return RedirectToAction("DownloadPatch", "UnifiedPatch");
    }

    /// <summary>
    /// GET /Items/PatchStatus
    /// Reports whether a retexture patch is available to download. Based on the DB
    /// (durable), not the wwwroot file (ephemeral), so the download button shows
    /// whenever a patch can be produced — including right after a redeploy.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> PatchStatus()
    {
        bool available = await _retexture.HasAnyRetexturesAsync();
        return Json(new { available });
    }

    // ===================== ICON SEARCH =====================

    /// <summary>
    /// GET /Items/IconSearch?q=sword&page=1&pageSize=60
    /// Searches icon filenames from the DBC data for the icon picker.
    /// Returns icons with their associated displayIds.
    /// </summary>
    [HttpGet]
    public IActionResult IconSearch(string? q, int page = 1, int pageSize = 60)
    {
        var reverseMap = _dbc.GetIconToDisplayIds();

        IEnumerable<KeyValuePair<string, List<uint>>> filtered = reverseMap;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim().ToLowerInvariant();
            filtered = reverseMap.Where(kv => kv.Key.Contains(search));
        }

        var sorted = filtered.OrderBy(kv => kv.Key).ToList();
        var totalCount = sorted.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize);

        var results = paged.Select(kv => new
        {
            iconName = kv.Key,
            iconPath = $"/Icon/Get?name={kv.Key}",
            displayIds = kv.Value
        });

        return Json(new
        {
            icons = results,
            totalCount,
            page,
            pageSize,
            totalPages
        });
    }

    // ===================== MPQ DIAGNOSTICS =====================

    /// <summary>
    /// GET /Items/MpqProbe?partial=Sleeve_AU
    /// Returns every MPQ path whose filename contains the partial string.
    /// Used to discover the real subdir convention for body-atlas BLPs.
    /// </summary>
    [HttpGet]
    public IActionResult MpqProbe(string? partial, int max = 100)
    {
        if (string.IsNullOrWhiteSpace(partial))
            return BadRequest(new { error = "partial query parameter required" });

        var hits = _mpq.FindByPartialName(partial);
        return Json(new
        {
            partial,
            total = hits.Count,
            truncated = hits.Count > max,
            paths = hits.Take(max).ToList()
        });
    }

    /// <summary>
    /// GET /Items/MpqExhaustiveProbe?partial=Plate_RaidPaladin_A_01Gold_Chest_TU
    ///
    /// Hash-table probe — tries every candidate variant of a body-atlas
    /// texture partial name against every loaded MPQ via TryOpenFile, NOT
    /// via the listfile. Use this when MpqProbe (listfile-based) returns
    /// zero hits and you want to rule out "the archive has no listfile"
    /// before concluding the file doesn't exist.
    ///
    /// Generates 8 subdirs × 4 suffixes = 32 candidate paths under
    /// Item\TextureComponents, plus the bare partial as a fallback.
    /// </summary>
    [HttpGet]
    public IActionResult MpqExhaustiveProbe(string partial)
    {
        if (string.IsNullOrWhiteSpace(partial))
            return BadRequest(new { error = "partial is required" });

        string[] subdirs = {
            "ArmUpperTexture", "ArmLowerTexture", "HandTexture",
            "TorsoUpperTexture", "TorsoLowerTexture",
            "LegUpperTexture", "LegLowerTexture", "FootTexture",
        };
        string[] suffixes = { "_M.blp", "_F.blp", "_U.blp", ".blp" };

        var candidates = new List<string>();
        foreach (var sd in subdirs)
            foreach (var sfx in suffixes)
                candidates.Add($"Item\\TextureComponents\\{sd}\\{partial}{sfx}");

        // Also try the partial as-given (no path mangling) in case the
        // caller passed a fully-qualified path or non-TextureComponents file.
        candidates.Add(partial);
        if (!partial.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            candidates.Add(partial + ".blp");

        var hits = _mpq.FindByExactPaths(candidates);

        return Json(new
        {
            partial,
            candidatesTried = candidates.Count,
            hitCount = hits.Count,
            hits = hits.Select(h => new { h.Path, h.Archive, h.Size }),
        });
    }

    /// <summary>
    /// GET /Items/MpqProbeSample?count=50
    ///
    /// BRUTE-FORCE candidate probing. For each sampled displayId's body
    /// textures, try every plausible MPQ path (multiple subdirs × M/F suffix
    /// × empty suffix) via ExtractFile and record which ones HIT.
    ///
    /// This works WITHOUT a (listfile) — vanilla MPQs don't expose one for
    /// the bulk asset archives. Direct path lookup via ExtractFile uses
    /// MPQ's internal hash table which is O(1) per attempt, so trying 30-40
    /// candidates per slot across hundreds of items is cheap (seconds).
    ///
    /// Aggregate output: a histogram of which (slot, subdir, suffix) tuples
    /// actually exist in your MPQs. The winning candidates per slot become
    /// the new BodyAtlasTextureService.SlotSubdirCandidates dictionary.
    /// </summary>
    [HttpGet]
    public IActionResult MpqProbeSample(int count = 50)
    {
        return RunBruteProbe(count, logPath: "/tmp/mpq_probe_sample.log");
    }

    /// <summary>
    /// GET /Items/MpqProbeAll
    ///
    /// Run brute probing across EVERY displayId with body textures (~24k
    /// records). Can take a minute. Output is the definitive subdir-mapping
    /// table for vanilla 1.12. Result is written to /tmp/mpq_probe_all.log.
    /// </summary>
    [HttpGet]
    public IActionResult MpqProbeAll()
    {
        return RunBruteProbe(count: int.MaxValue, logPath: "/tmp/mpq_probe_all.log");
    }


    /// <summary>
    /// GET /Items/DisplayInfoRow?displayId=30606
    ///
    /// Session N diagnostic — settles the "where does m_itemVisual live in
    /// the DBC row?" question by dumping the raw 23-field record plus a
    /// non-zero-value histogram across the full table. Use this when an
    /// item you expect to have a visual comes back with itemVisualId=0:
    ///
    ///   - histogram[22] should be in the high-hundreds (count of items
    ///     with non-zero m_itemVisual). If it's 0 or way off, the offset
    ///     is wrong. Cross-check histogram[20..24] to spot the real column.
    ///   - row.fields[22] should be non-zero for any item that visibly
    ///     glows / sparkles / has ribbons in-game. If it's 0 for the
    ///     specific item under investigation, the visual is bound
    ///     somewhere else (proc spell SpellVisual, runtime enchant, etc.)
    ///     not on ItemDisplayInfo at all.
    ///   - row.strings[] decodes every uint32 as if it were a stringref;
    ///     fields holding genuine integers come back as empty strings,
    ///     fields holding real strings come back as their text. Useful
    ///     to distinguish at a glance.
    /// </summary>
    [HttpGet]
    public IActionResult DisplayInfoRow(uint displayId)
    {
        return Json(_dbc.DumpItemDisplayInfoRow(displayId));
    }

    /// <summary>
    /// GET /Items/M2HeaderDump?displayId=30606
    ///
    /// Session N diagnostic — dumps every 8-byte (count, offset) pair across
    /// the M2 header region as if each were an M2Array. The output tells us
    /// at a glance which header slots actually point at real data in this
    /// specific M2 file vs which are empty/zero/garbage.
    ///
    ///
    /// What to look for in the response:
    ///   - "Plausible" entries (count between 1 and 1000, offset > 0xC0,
    ///     offset + count*assumed_stride <= fileSize) are real data blocks.
    ///   - Zero pairs are either empty arrays or are slots we don't
    ///     read in vanilla (e.g. blendMapOverrides).
    ///   - The transition point between plausible and zero pairs tells
    ///     us where the header ends.
    /// </summary>
    [HttpGet]
    public IActionResult M2HeaderDump(uint displayId)
    {
        var modelInfo = _dbc.GetItemModelInfo(displayId);
        if (modelInfo == null)
            return NotFound(new { error = $"displayId {displayId} not in ItemDisplayInfo.dbc" });

        var modelName = !string.IsNullOrEmpty(modelInfo.Value.ModelName1)
            ? modelInfo.Value.ModelName1
            : modelInfo.Value.ModelName2;
        if (string.IsNullOrEmpty(modelName))
            return NotFound(new { error = "no model name in DBC" });

        var findMethod = _itemTextures.GetType().GetMethod("FindAndExtractItemM2",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (findMethod == null)
            return StatusCode(500, new { error = "FindAndExtractItemM2 not found via reflection" });

        var m2Bytes = findMethod.Invoke(_itemTextures, new object[] { modelName }) as byte[];
        if (m2Bytes == null)
            return NotFound(new { error = $"M2 not in MPQ: {modelName}" });

        var slots = new List<object>();

        // Scan from 0x000 to 0x140 in 4-byte steps. At each step, read
        // (count, offset) as if it were an M2Array; we tag the result
        // with "plausible" if it looks like a real block: a non-zero
        // offset that's within the file, and a count under 100k.
        for (int hdrOff = 0; hdrOff + 8 <= 0x150 && hdrOff + 8 <= m2Bytes.Length; hdrOff += 4)
        {
            uint count = BitConverter.ToUInt32(m2Bytes, hdrOff);
            uint offset = BitConverter.ToUInt32(m2Bytes, hdrOff + 4);

            bool plausible =
                count > 0 && count < 100000 &&
                offset > 0 && offset < m2Bytes.Length;

            slots.Add(new
            {
                hdrOff = $"0x{hdrOff:X3}",
                count,
                offset,
                offsetHex = $"0x{offset:X}",
                plausible,
            });
        }

        return Json(new
        {
            displayId,
            modelName,
            fileSize = m2Bytes.Length,
            version = BitConverter.ToUInt32(m2Bytes, 4),
            slots,
        });
    }

    /// <summary>
    /// GET /Items/TransparencyDiag?displayId=30606
    ///
    /// Session N diagnostic — for each submesh in a weapon M2, reports the
    /// static alpha resolved via the transparency-track chain:
    ///   batch.TextureWeightIndex (= transparencyIndex)
    ///     → TransparencyLookup[idx]
    ///     → TransparencyStaticAlphas[idx]
    ///
    /// Use this BEFORE deploying GlbWriter's "skip near-zero submesh" logic
    /// to verify the right submeshes are being identified as hidden.
    ///
    /// Expected pattern for Thunderfury (displayId 30606):
    ///   - Hilt / blade / crossguard submeshes: staticAlpha = 1.0 → kept
    ///   - Lightning fin submeshes (textures ZAP1, ZAP1B, LIGHTNINGBALL):
    ///       staticAlpha < 0.01 → would be skipped
    ///   - Outer modulate quad (Geoset0, the dark square): unclear; if it
    ///       has a transparency track at 0 it drops, otherwise stays
    ///
    /// If every submesh reports 1.0 the transparency parse is broken (most
    /// likely: AnimationBlock stride wrong, or transparencyLookup not
    /// populated). If every submesh reports 0.0 the keyframe read is
    /// pointing at the wrong byte.
    /// </summary>
    [HttpGet]
    public IActionResult TransparencyDiag(uint displayId)
    {
        var modelInfo = _dbc.GetItemModelInfo(displayId);
        if (modelInfo == null)
            return NotFound(new { error = $"displayId {displayId} not in ItemDisplayInfo.dbc" });

        var modelName = !string.IsNullOrEmpty(modelInfo.Value.ModelName1)
            ? modelInfo.Value.ModelName1
            : modelInfo.Value.ModelName2;
        if (string.IsNullOrEmpty(modelName))
            return NotFound(new { error = "no model name in DBC" });

        // Same reflection trick as WeaponEmitters — FindAndExtractItemM2 is
        // still private. Promotion of that method to public is a planned
        // cleanup item.
        var findMethod = _itemTextures.GetType().GetMethod("FindAndExtractItemM2",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (findMethod == null)
            return StatusCode(500, new { error = "FindAndExtractItemM2 not found via reflection" });

        var m2Bytes = findMethod.Invoke(_itemTextures, new object[] { modelName }) as byte[];
        if (m2Bytes == null)
            return NotFound(new { error = $"M2 not in MPQ: {modelName}" });

        var m2 = M2Reader.Parse(m2Bytes);
        if (m2 == null)
            return StatusCode(500, new { error = "M2Reader.Parse returned null" });

        // Build a (submeshIdx → first batch) map so we can report what the
        // GlbWriter will see.
        var firstBatchForSubmesh = new Dictionary<int, M2Batch>();
        foreach (var b in m2.Batches)
        {
            if (!firstBatchForSubmesh.ContainsKey(b.SubmeshIndex))
                firstBatchForSubmesh[b.SubmeshIndex] = b;
        }

        var submeshReports = new List<object>();
        for (int subIdx = 0; subIdx < m2.Submeshes.Count; subIdx++)
        {
            var sub = m2.Submeshes[subIdx];
            float staticAlpha = 1.0f;
            int? transparencyIndex = null;
            int? lookedUpTrackIdx = null;
            int? batchTextureIndex = null;
            int? resolvedTextureSlot = null;
            int? materialIndex = null;
            int? blendingMode = null;

            if (firstBatchForSubmesh.TryGetValue(subIdx, out var batch))
            {
                transparencyIndex = batch.TextureWeightIndex;
                if (batch.TextureWeightIndex < m2.TransparencyLookup.Count)
                {
                    lookedUpTrackIdx = m2.TransparencyLookup[batch.TextureWeightIndex];
                }
                staticAlpha = m2.GetStaticAlphaForBatch(batch);

                batchTextureIndex = batch.TextureIndex;
                if (batch.TextureIndex < m2.TextureLookup.Count)
                    resolvedTextureSlot = m2.TextureLookup[batch.TextureIndex];

                materialIndex = batch.MaterialIndex;
                if (batch.MaterialIndex < m2.RenderFlags.Count)
                    blendingMode = m2.RenderFlags[batch.MaterialIndex].BlendingMode;
            }

            string? textureFilename = null;
            if (resolvedTextureSlot.HasValue && resolvedTextureSlot.Value < m2.Textures.Count)
                textureFilename = m2.Textures[resolvedTextureSlot.Value].Filename;

            bool nearlyInvisible = staticAlpha < GlbWriter.SUBMESH_VISIBILITY_THRESHOLD;

            submeshReports.Add(new
            {
                submeshIndex = subIdx,
                geosetId = sub.Id,
                vertexCount = sub.VertexCount,
                indexCount = sub.IndexCount,
                hasBatch = firstBatchForSubmesh.ContainsKey(subIdx),
                transparencyIndex,
                lookedUpTrackIdx,
                staticAlpha,
                wouldSkip = nearlyInvisible,  // legacy field name; no submeshes are actually skipped now — alpha is baked into material instead
                batchTextureIndex,
                resolvedTextureSlot,
                textureFilename,
                materialIndex,
                blendingMode,
            });
        }

        return Json(new
        {
            displayId,
            modelName,
            m2Bytes = m2Bytes.Length,
            transparencyTrackCount = m2.TransparencyStaticAlphas.Count,
            transparencyLookupCount = m2.TransparencyLookup.Count,
            renderFlagCount = m2.RenderFlags.Count,
            submeshCount = m2.Submeshes.Count,
            batchCount = m2.Batches.Count,
            transparencyStaticAlphas = m2.TransparencyStaticAlphas,
            transparencyLookup = m2.TransparencyLookup,
            // Session N follow-up: include the texture table + lookup so we can
            // see whether batch.TextureIndex resolutions point at valid
            // m2.Textures entries or off into "request a DBC texture" sentinel
            // territory. For Thunderfury this revealed lookup values of 21-24
            // referencing slots that don't exist in the 6-entry local table.
            m2TextureEntries = m2.Textures.Select((t, i) => new {
                slot = i,
                type = t.Type,
                flags = t.Flags,
                filename = t.Filename,
            }),
            m2TextureLookup = m2.TextureLookup,
            submeshes = submeshReports,
            visibilityThreshold = GlbWriter.SUBMESH_VISIBILITY_THRESHOLD,
        });
    }

    /// <summary>
    /// GET /Items/WeaponEmitters?displayId=X
    ///
    /// Dumps the M2's emitter inventory for a weapon's display model:
    ///   - Texture table (which BLPs the M2 references)
    ///   - Particle emitter list (header 0x13C — what M2EmitterParser reads)
    ///   - Ribbon emitter list (header 0x144 — separate system)
    ///   - Texture animation count (header 0x06C — UV scroll/transform tracks)
    ///
    /// Diagnostic-only. Used to plan Phase 3/4 of the weapon effects work
    /// — tells us whether Thunderfury's lightning is implemented as
    /// particles, ribbons, UV-animated textures, or some mix.
    /// </summary>
    [HttpGet]
    public IActionResult WeaponEmitters(uint displayId)
    {
        var modelInfo = _dbc.GetItemModelInfo(displayId);
        if (modelInfo == null)
            return NotFound(new { error = $"displayId {displayId} not in ItemDisplayInfo.dbc" });

        var modelName = !string.IsNullOrEmpty(modelInfo.Value.ModelName1)
            ? modelInfo.Value.ModelName1
            : modelInfo.Value.ModelName2;
        if (string.IsNullOrEmpty(modelName))
            return NotFound(new { error = "no model name in DBC" });

        // Use reflection to reach FindAndExtractItemM2 since it's private.
        // For a one-time diagnostic this is fine; if the method gets promoted
        // we'll call it directly.
        var findMethod = _itemTextures.GetType().GetMethod("FindAndExtractItemM2",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (findMethod == null)
            return StatusCode(500, new { error = "FindAndExtractItemM2 not found via reflection" });

        var m2Bytes = findMethod.Invoke(_itemTextures, new object[] { modelName }) as byte[];
        if (m2Bytes == null)
            return NotFound(new { error = $"M2 not in MPQ: {modelName}" });

        // ── Existing parsers (already in services) ──
        var emitters = M2EmitterParser.ReadEmitters(m2Bytes);
        var textures = M2TextureParser.ParseTextures(m2Bytes);

        // ── Ribbon emitter array (header offset 0x144 in v256) ──
        // Layout per ribbon emitter, vanilla v256 (stride ~176 bytes):
        //   +0   uint32  ribbonId
        //   +4   uint32  boneIndex          (which bone the ribbon hangs from)
        //   +8   float[3] position          (offset from bone, model space)
        //  +20   M2Array  textureIndices    (which textures the ribbon cycles through)
        //  +28   M2Array  materialIndices   (renderFlag entries — drives blend mode)
        //  ... animation tracks (color, opacity, above/below extents, etc)
        //
        // Stride note: 176 is the typical vanilla stride but isn't strictly
        // guaranteed across all build flavors. For this diag we read header
        // + bone + position + array counts, which are stable at the start.
        const int RIBBON_STRIDE = 176;
        const int RIBBON_HEADER_OFS = 0x144;

        uint ribbonCount = 0;
        uint ribbonOffset = 0;
        if (m2Bytes.Length >= RIBBON_HEADER_OFS + 8)
        {
            ribbonCount = BitConverter.ToUInt32(m2Bytes, RIBBON_HEADER_OFS + 0);
            ribbonOffset = BitConverter.ToUInt32(m2Bytes, RIBBON_HEADER_OFS + 4);
        }

        var ribbons = new List<object>();
        if (ribbonCount > 0 && ribbonOffset > 0 && ribbonOffset < m2Bytes.Length)
        {
            for (uint i = 0; i < ribbonCount && i < 16; i++)
            {
                int ofs = (int)(ribbonOffset + i * RIBBON_STRIDE);
                if (ofs + 36 > m2Bytes.Length) break;

                ribbons.Add(new
                {
                    index = (int)i,
                    ribbonId = BitConverter.ToUInt32(m2Bytes, ofs + 0),
                    boneIndex = BitConverter.ToUInt32(m2Bytes, ofs + 4),
                    posX = BitConverter.ToSingle(m2Bytes, ofs + 8),
                    posY = BitConverter.ToSingle(m2Bytes, ofs + 12),
                    posZ = BitConverter.ToSingle(m2Bytes, ofs + 16),
                    textureIndicesCount = BitConverter.ToUInt32(m2Bytes, ofs + 20),
                    textureIndicesOffset = BitConverter.ToUInt32(m2Bytes, ofs + 24),
                    materialIndicesCount = BitConverter.ToUInt32(m2Bytes, ofs + 28),
                    materialIndicesOffset = BitConverter.ToUInt32(m2Bytes, ofs + 32),
                });
            }
        }

        // ── Texture animation array (header offset 0x06C in v256) ──
        // Each entry is a M2TextureTransform with translation/rotation/scale
        // animation tracks. M2Batch.TextureTransformIndex references this
        // array. Non-zero count = at least one batch has UV scrolling.
        uint texAnimCount = 0;
        uint texAnimOffset = 0;
        if (m2Bytes.Length >= 0x074)
        {
            texAnimCount = BitConverter.ToUInt32(m2Bytes, 0x06C);
            texAnimOffset = BitConverter.ToUInt32(m2Bytes, 0x070);
        }

        return Json(new
        {
            displayId,
            modelName,
            m2Bytes = m2Bytes.Length,

            // Headlines — answer the "what kind of effect is this" question
            particleEmitterCount = emitters.Count,
            ribbonEmitterCount = (int)ribbonCount,
            textureAnimationCount = (int)texAnimCount,
            textureCount = textures.Count,

            // Details
            textures = textures.Select(t => new
            {
                t.Index,
                t.Filename,
                referencedByEmitters = t.ReferencedByEmitters
            }),
            particleEmitters = emitters.Select(e => new
            {
                e.Index,
                e.BlendMode,
                e.EmitterType,
                e.TextureId,
                colorStart = $"0x{e.ColorStart:X8}",
                colorMid = $"0x{e.ColorMid:X8}",
                colorEnd = $"0x{e.ColorEnd:X8}",
                e.ScaleStart,
                e.ScaleMid,
                e.ScaleEnd,
                tracks = e.TrackValues,
                keyframeCounts = e.TrackKeyframeCounts
            }),
            ribbonEmitters = ribbons,

            // Raw header offsets — keep these visible so any "wait, why
            // did it parse 0 ribbons when it sees 1?" debugging starts
            // with concrete bytes, not theory.
            headerOffsets = new
            {
                textureAnims_0x06C_count = texAnimCount,
                textureAnims_0x070_offset = texAnimOffset,
                particleEmitters_0x13C_count = m2Bytes.Length >= 0x140
                    ? BitConverter.ToUInt32(m2Bytes, 0x13C) : 0,
                particleEmitters_0x140_offset = m2Bytes.Length >= 0x144
                    ? BitConverter.ToUInt32(m2Bytes, 0x140) : 0,
                ribbonEmitters_0x144_count = ribbonCount,
                ribbonEmitters_0x148_offset = ribbonOffset,
            }
        });
    }

    /// <summary>
    /// Shared brute-probe implementation. Tries every plausible candidate
    /// path per slot and counts which ones actually exist in the MPQs.
    /// </summary>
    private IActionResult RunBruteProbe(int count, string logPath)
    {
        var modelInfos = _dbc.ItemModelInfos;
        var rng = new Random(42);

        // Generous candidate set per slot. Includes:
        //   - Best-guess subdir based on TC docs (e.g. ArmUpperTexture)
        //   - Filename-derived subdir guesses (Sleeve, Bracer, Glove, etc.)
        //   - The empty subdir (filename directly under TextureComponents)
        //   - Cross-slot fallbacks (some boots live in BootTexture not Foot)
        //
        // Each candidate is paired with a suffix list: "" (bare), "_M",
        // "_F". Vanilla often has gender-specific BLPs even for armor that
        // looks unisex.
        var slotCandidates = new Dictionary<int, string[]>
        {
            { 0, new[] { "ArmUpperTexture", "SleeveTexture", "Sleeve", "Arm", "ShoulderTexture", "Shoulder", "" } },
            { 1, new[] { "ArmLowerTexture", "SleeveTexture", "BracerTexture", "Sleeve", "Bracer", "Arm", "Glove", "GloveTexture", "" } },
            { 2, new[] { "HandTexture", "GloveTexture", "Glove", "Hand", "" } },
            { 3, new[] { "TorsoUpperTexture", "ChestTexture", "Chest", "Torso", "" } },
            { 4, new[] { "TorsoLowerTexture", "ChestTexture", "Chest", "Torso", "" } },
            { 5, new[] { "LegUpperTexture", "PantTexture", "Pant", "Pants", "Leg", "BeltTexture", "Belt", "" } },
            { 6, new[] { "LegLowerTexture", "PantTexture", "BootTexture", "Pant", "Pants", "Boot", "Leg", "" } },
            { 7, new[] { "FootTexture", "BootTexture", "Boot", "Foot", "" } },
        };
        // Suffix candidates between the partial name and ".blp".
        //   _M / _F  = male / female body anatomy variants (torso, legs)
        //   _U       = unisex (sleeves, pant lower — both genders share)
        //   ""       = bare partial (rare but real for some items)
        // Empirically derived from MpqProbe spot-checks on real robes.
        var suffixCandidates = new[] { "", "_M", "_F", "_U" };

        // Stems we try around the partial name. Real vanilla paths sometimes
        // wrap the filename in different roots. "Item\TextureComponents\"
        // is canonical, but a few variants are worth trying.
        var pathRoots = new[]
        {
            @"Item\TextureComponents",
            @"ITEM\TEXTURECOMPONENTS",   // case variant (MPQ usually case-insensitive but cheap to try)
            @"Item\ObjectComponents\Texture",
        };

        // Output: (slot, fullCandidateTemplate) → hitCount.
        // Template uses {ROOT} and {SUBDIR} placeholders so we know which
        // shape worked, not just which paths.
        var hitsByTemplate = new Dictionary<(int slot, string root, string subdir, string sfx), int>();
        var hitsTotal = new Dictionary<int, int>();   // slot → total slots with at least 1 hit
        var slotsAttempted = new Dictionary<int, int>(); // slot → number of non-empty partials seen

        // Pick the candidate sample.
        IEnumerable<KeyValuePair<uint, ItemModelDbc>> withBodyTex = modelInfos
            .Where(kv => kv.Value.BodyTextures != null &&
                         kv.Value.BodyTextures.Any(s => !string.IsNullOrEmpty(s)));
        if (count != int.MaxValue)
            withBodyTex = withBodyTex.OrderBy(_ => rng.Next()).Take(count);

        var sampled = withBodyTex.ToList();

        // Track per-slot first-hit examples so the log shows what actually
        // works for at least one item.
        var firstHitExample = new Dictionary<int, (uint displayId, string partial, string path)>();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var (displayId, info) in sampled)
        {
            for (int slot = 0; slot < 8 && slot < info.BodyTextures!.Length; slot++)
            {
                var partial = info.BodyTextures[slot];
                if (string.IsNullOrEmpty(partial)) continue;

                slotsAttempted[slot] = slotsAttempted.GetValueOrDefault(slot, 0) + 1;

                bool anyHit = false;
                foreach (var root in pathRoots)
                {
                    foreach (var subdir in slotCandidates[slot])
                    {
                        foreach (var sfx in suffixCandidates)
                        {
                            var path = string.IsNullOrEmpty(subdir)
                                ? $"{root}\\{partial}{sfx}.blp"
                                : $"{root}\\{subdir}\\{partial}{sfx}.blp";
                            var data = _mpq.ExtractFile(path);
                            if (data != null)
                            {
                                var key = (slot, root, subdir, sfx);
                                hitsByTemplate[key] = hitsByTemplate.GetValueOrDefault(key, 0) + 1;
                                if (!firstHitExample.ContainsKey(slot))
                                    firstHitExample[slot] = (displayId, partial, path);
                                anyHit = true;
                            }
                        }
                    }
                }
                if (anyHit) hitsTotal[slot] = hitsTotal.GetValueOrDefault(slot, 0) + 1;
            }
        }

        sw.Stop();

        // Write the report.
        try
        {
            using var fw = new StreamWriter(logPath);
            fw.WriteLine($"# MPQ Brute Probe — {DateTime.UtcNow:o}  sampled={sampled.Count}  elapsed={sw.Elapsed}");
            fw.WriteLine();
            fw.WriteLine("## Hit rate per slot");
            for (int s = 0; s < 8; s++)
            {
                int attempted = slotsAttempted.GetValueOrDefault(s, 0);
                int hit = hitsTotal.GetValueOrDefault(s, 0);
                double pct = attempted == 0 ? 0 : 100.0 * hit / attempted;
                fw.WriteLine($"  slot {s}: hit {hit} / {attempted} ({pct:F1}%)");
                if (firstHitExample.TryGetValue(s, out var ex))
                    fw.WriteLine($"    e.g. displayId={ex.displayId} partial={ex.partial} → {ex.path}");
            }
            fw.WriteLine();
            fw.WriteLine("## Winning templates (which root\\subdir\\partial{sfx}.blp shape hit, and how often)");
            for (int s = 0; s < 8; s++)
            {
                fw.WriteLine($"### slot {s}");
                var slotHits = hitsByTemplate
                    .Where(kv => kv.Key.slot == s)
                    .OrderByDescending(kv => kv.Value)
                    .ToList();
                if (slotHits.Count == 0)
                {
                    fw.WriteLine("  (no hits)");
                }
                else
                {
                    foreach (var kv in slotHits)
                    {
                        var sub = string.IsNullOrEmpty(kv.Key.subdir) ? "(none)" : kv.Key.subdir;
                        var sfx = string.IsNullOrEmpty(kv.Key.sfx) ? "(none)" : kv.Key.sfx;
                        fw.WriteLine($"  {kv.Value,6}  root={kv.Key.root}  subdir={sub}  sfx={sfx}");
                    }
                }
                fw.WriteLine();
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "log write failed", details = ex.Message });
        }

        // Compact JSON summary so the browser doesn't choke.
        var summary = new Dictionary<int, object>();
        for (int s = 0; s < 8; s++)
        {
            var slotHits = hitsByTemplate
                .Where(kv => kv.Key.slot == s)
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => new
                {
                    root = kv.Key.root,
                    subdir = kv.Key.subdir,
                    sfx = kv.Key.sfx,
                    hits = kv.Value
                })
                .ToList();
            summary[s] = new
            {
                attempted = slotsAttempted.GetValueOrDefault(s, 0),
                hit = hitsTotal.GetValueOrDefault(s, 0),
                top = slotHits,
            };
        }

        return Json(new
        {
            sampleSize = sampled.Count,
            elapsedMs = sw.ElapsedMilliseconds,
            logPath,
            summary,
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEMPORARY DIAGNOSTIC — Session "shield grip offset"
    // ═══════════════════════════════════════════════════════════════════
    //
    // Dumps the attachment list of an item M2 (the actual M2 file in the
    // MPQ, not the character's). Lets us see what attachment IDs / bones /
    // positions the shield model carries before deciding which offset to
    // subtract in GlbWriter for the shield case.
    //
    // Usage:
    //   GET /Items/M2AttachmentDump?displayId=34110   (Drillborer Disk)
    //   GET /Items/M2AttachmentDump?displayId=35573   (Shield of Condemnation)
    //   GET /Items/M2AttachmentDump?displayId=30994   (Quel'Serrar — control)
    //
    // Remove once the shield grip offset behavior is validated.

    [HttpGet]
    public IActionResult M2AttachmentDump(uint displayId)
    {
        var infoNullable = _dbc.GetItemModelInfo(displayId);
        if (infoNullable == null)
            return NotFound(new { error = "displayId not in DBC", displayId });

        var info = infoNullable.Value;
        var modelName = !string.IsNullOrEmpty(info.ModelName1)
            ? info.ModelName1 : info.ModelName2;

        if (string.IsNullOrEmpty(modelName))
            return NotFound(new { error = "no modelName in DBC", displayId });

        // Reuse the same MPQ search ItemTextureService uses.
        var m2Bytes = _mpq.ExtractModelFile(modelName);
        if (m2Bytes == null)
        {
            // Try the per-subdir search (Shield\, Weapon\, etc.).
            string[] subdirs = { "Weapon", "Shield", "Head", "Shoulder", "Quiver", "Ammo" };
            var baseName = Path.GetFileNameWithoutExtension(modelName);
            foreach (var sd in subdirs)
            {
                foreach (var ext in new[] { ".m2", ".mdx", ".M2", ".MDX" })
                {
                    m2Bytes = _mpq.ExtractFile($"Item\\ObjectComponents\\{sd}\\{baseName}{ext}");
                    if (m2Bytes != null) goto found;
                }
            }
        found:;
        }

        if (m2Bytes == null)
            return NotFound(new { error = "M2 not found in MPQ", displayId, modelName });

        var m2 = M2Reader.Parse(m2Bytes);
        if (m2 == null || !m2.IsValid)
            return StatusCode(500, new { error = "M2 parse failed", displayId, modelName });

        // Snapshot of every attachment + a parallel view of the lookup
        // table (semantic ID → index into Attachments[]) so we can see
        // which IDs the model actually exposes.
        var attachments = m2.Attachments.Select((a, idx) => new
        {
            arrayIndex = idx,
            id = a.Id,
            boneIndex = (int)a.BoneIndex,
            position = new { x = a.Position.X, y = a.Position.Y, z = a.Position.Z },
            // Magnitude — easy visual flag for "is this attachment far from origin?"
            distanceFromOrigin = Math.Sqrt(
                a.Position.X * a.Position.X +
                a.Position.Y * a.Position.Y +
                a.Position.Z * a.Position.Z),
        }).ToList();

        var lookup = m2.AttachmentLookup.Select((v, idx) => new
        {
            semanticId = idx,
            attachmentArrayIndex = (int)v,    // -1 if absent
        }).ToList();

        return Json(new
        {
            displayId,
            modelName,
            m2Bytes = m2Bytes.Length,
            vertexCount = m2.Vertices.Count,
            submeshCount = m2.Submeshes.Count,
            attachments,
            attachmentLookup = lookup,
            note = "Position is in glTF Y-up coords after M2Reader's Z-up→Y-up conversion. " +
                   "For shields the relevant entry is the one that marks the grip — usually " +
                   "the first non-zero attachment, typically id 0 or 1.",
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Map race display name to ChrRaces.dbc ID. Used by the
    /// HelmetGeosetVisData decode to check the correct bit in hairFlags.
    /// Returns 1 (Human) for unknown race names as a safe default.
    /// </summary>
    private static uint RaceNameToId(string raceName) => raceName?.ToLowerInvariant() switch
    {
        "human" => 1,
        "orc" => 2,
        "dwarf" => 3,
        "nightelf" => 4,
        "undead" or "scourge" => 5,
        "tauren" => 6,
        "gnome" => 7,
        "troll" => 8,
        _ => 1,   // fallback: Human
    };

    // ── Face texture diagnostic ───────────────────────────────────────

    /// <summary>
    /// GET /Items/FaceTexture?race=Human&amp;gender=Male&amp;variation=0&amp;color=0&amp;region=lower
    /// 
    /// Returns the decoded face BLP as a PNG for the given CharSections
    /// variation/color. Used by the diagnostic panel to cycle through
    /// face variations and find which one has open eyes.
    /// 
    /// Also: GET /Items/FaceVariations?race=Human&amp;gender=Male
    /// Returns JSON listing all available (variation, color) combos.
    /// </summary>
    [HttpGet]
    public IActionResult FaceTexture(
        string race = "Human", string gender = "Male",
        uint variation = 0, uint color = 0, string region = "lower")
    {
        uint raceId = RaceNameToId(race);
        uint sexId = gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;

        // Find the matching CharSections Face row
        CharSectionDbc? match = null;
        foreach (var row in _dbc.CharacterSections)
        {
            if (row.Race == raceId && row.Sex == sexId
                && row.BaseSection == 1  // Face
                && row.VariationIndex == variation
                && row.ColorIndex == color)
            {
                match = row;
                break;
            }
        }
        if (match == null)
            return NotFound(new { error = $"No CharSections Face row for race={race} gender={gender} var={variation} col={color}" });

        string partial = region.Equals("upper", StringComparison.OrdinalIgnoreCase)
            ? match.TextureName2 : match.TextureName1;
        if (string.IsNullOrEmpty(partial))
            return NotFound(new { error = $"Empty texture path for region={region}" });

        var blpBytes = CharacterSkinCompositor.ResolveCharacterTextureBlp(
            (MpqReaderService)HttpContext.RequestServices.GetService(typeof(MpqReaderService))!,
            partial, race, gender);
        if (blpBytes == null)
            return NotFound(new { error = $"BLP not in MPQ: {partial}" });

        // Decode BLP → PNG
        try
        {
            var pixels = BlpDecoder.GetPixels(blpBytes, 0, out int w, out int h);
            if (w == 0 || h == 0) return NotFound(new { error = "BLP decoded to 0×0" });

            using var bmp = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
            bmp.NotifyPixelsChanged();
            using var ms = new MemoryStream();
            bmp.Encode(ms, SkiaSharp.SKEncodedImageFormat.Png, 100);
            return File(ms.ToArray(), "image/png");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /Items/FaceVariations?race=Human&amp;gender=Male
    /// Returns all (variation, color) combos for Face rows in CharSections.dbc.
    /// </summary>
    [HttpGet]
    public IActionResult FaceVariations(string race = "Human", string gender = "Male")
    {
        uint raceId = RaceNameToId(race);
        uint sexId = gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;

        var rows = _dbc.CharacterSections
            .Where(r => r.Race == raceId && r.Sex == sexId && r.BaseSection == 1)
            .OrderBy(r => r.VariationIndex).ThenBy(r => r.ColorIndex)
            .Select(r => new {
                variation = r.VariationIndex,
                color = r.ColorIndex,
                lower = r.TextureName1,
                upper = r.TextureName2,
            })
            .ToList();

        return Json(new { race, gender, raceId, sexId, count = rows.Count, rows });
    }

}
