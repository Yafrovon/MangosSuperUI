using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
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
        MpqReaderService mpq)
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
    }

    public IActionResult Index() => View();

    // ===================== SEARCH (existing, unchanged) =====================

    /// <summary>
    /// GET /Items/Search?q=sword&classFilter=2&qualityFilter=4&page=1&pageSize=50
    /// Server-side search with pagination.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search(string? q, int? classFilter, int? subclassFilter,
        int? qualityFilter, int? inventoryTypeFilter, int page = 1, int pageSize = 50)
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
            ORDER BY entry ASC
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

        return Json(new { found = true, item, iconPath, modelPath });
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
            var lUrl = _itemTextures.EnsureShoulderGlb(displayId, ItemTextureService.ShoulderSide.Left);
            var rUrl = _itemTextures.EnsureShoulderGlb(displayId, ItemTextureService.ShoulderSide.Right);
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
            // Pragmatic open-vs-closed heuristic: closed helms have two
            // distinct HelmetGeosetVisData rows (different hide patterns
            // for hair/facial/ears across races), open helms repeat the
            // same row twice. Verified empirically May 16 2026:
            //
            //   Helm of Wrath    (closed)  v1=248  v2=306   v1 != v2
            //   Lawbringer       (closed)  v1=248  v2=306   v1 != v2
            //   PVP Alliance     (closed)  v1=249  v2=305   v1 != v2
            //   Helm of Might    (open)    v1=247  v2=247   v1 == v2
            //   Judgement Circlet(open)    v1=245  v2=245   v1 == v2
            //   Defias mask      (open)    v1=247  v2=247   v1 == v2
            //
            // Zero on both is treated as "show hair" — vanilla items
            // without HelmetGeosetVis assignments shouldn't hide anything.
            //
            // The proper decode parses HelmetGeosetVisData.dbc (5 fields:
            // id, hairFlags, facialFlags[3], earsFlags; each a bitmask
            // over ChrRaces) and checks (hairFlags >> raceId) & 1 for
            // each of v1 and v2. Deferred — the heuristic agrees with
            // the full decode on every known sample and lands in 5 lines
            // of code vs. a full DBC parser + endpoint wiring.
            hidesHair = info.Value.HelmetGeosetVis1 != 0
                     && info.Value.HelmetGeosetVis1 != info.Value.HelmetGeosetVis2,
            // Session N diagnostic: m_itemVisual — indexes ItemVisuals.dbc.
            // Non-zero means this item is supposed to render lightning,
            // glow, ribbons, or other visual effects on top of its base
            // mesh. Zero for most items. Thunderfury (30606) should come
            // back non-zero — that's Task 1's success criterion.
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
        // Helms cache as {displayId}_helm_RrG.glb (e.g. _helm_HuM); shoulders as
        // {displayId}_lshoulder.glb / _rshoulder.glb (race-independent).
        // The on-disk filename includes the RigidGlbVersion stamp via
        // CacheVersionRegistry — must match what EnsureHelmGlb /
        // EnsureShoulderGlb write so the existence checks here line up.
        string suffix = kindNormalized switch
        {
            "helm" => $"_helm{helmSuffix}",
            "shoulderleft" or "lshoulder" => "_lshoulder",
            "shoulderright" or "rshoulder" => "_rshoulder",
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
                _itemTextures.EnsureShoulderGlb(displayId, ItemTextureService.ShoulderSide.Left),
            "shoulderright" or "rshoulder" =>
                _itemTextures.EnsureShoulderGlb(displayId, ItemTextureService.ShoulderSide.Right),
            _ => null,
        };
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
    /// GET /Items/DownloadPatch?file=patch-M.MPQ
    /// Serves a retexture patch MPQ for download.
    /// </summary>
    [HttpGet]
    public IActionResult DownloadPatch(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return BadRequest("File name required");
        file = Path.GetFileName(file); // sanitize
        var fullPath = Path.Combine(_env.WebRootPath, "patches", "retexture", file);
        if (!System.IO.File.Exists(fullPath)) return NotFound($"Patch '{file}' not found");
        return PhysicalFile(fullPath, "application/octet-stream", file);
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
            iconPath = $"/icons/{kv.Key}.png",
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

}