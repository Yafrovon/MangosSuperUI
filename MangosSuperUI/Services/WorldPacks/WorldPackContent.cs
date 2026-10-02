using System.Globalization;
using System.Text.Json.Nodes;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// What a World Content Pack may own beyond terrain and placements (MSUIClient
/// shared_docs/WORLD_BUILDER.md §5): world-DB rows, client DBC rows, new maps and their tiles.
///
/// The one safety law: a pack only ever ADDS rows in reserved id ranges. It never edits a stock
/// row, so disabling a pack is a plain delete of what it added and can never damage vanilla data.
///
/// Doc kinds (wp_doc.kind / doc_key):
///   dbrow:&lt;table&gt;   key = the row's key columns joined by '|'   body = { column: value, ... }
///   dbc:&lt;Name&gt;      key = row id                               body = { cloneFrom?: id, fields: { "index": value } }
///   map              key = map id                               body = { mapId, directory, name, instanceType, areaId }
///   tile             key = "map:col:row"                        body = { map, col, row, sourceMap, sourceCol, sourceRow, keepObjects, areaId }
/// </summary>
public static class WorldPackContent
{
    public const uint TemplateBase = 7_000_000;      // creature/quest/item/text/loot/gossip entries
    /// <summary>
    /// Creature/gameobject spawn guids. Vanilla low guids are 24-bit (0xFFFFFF = 16,777,215) and mangosd
    /// starts its RUNTIME guid counter above the highest guid in the DB: a pack spawn up there leaves no
    /// room, and the first runtime spawn shuts the server down ("Creature guid overflow", 2026-09-26 with
    /// 70,000,000). Stock tops out at 303,722 (creature) / 632,462 (gameobject): packs live in
    /// [1,500,000, 8,000,000), leaving 8.7 million runtime guids.
    /// </summary>
    public const uint SpawnGuidBase = 1_500_000;
    public const uint SpawnGuidCeiling = 8_000_000;
    public const uint MapIdBase = 800;
    public const uint AreaIdBase = 7_000;            // area_template / AreaTable.dbc / AreaTrigger ids
    /// <summary>gossip_menu.entry is SMALLINT UNSIGNED in VMaNGOS (max 65535); stock stops at 60402.</summary>
    public const uint GossipMenuBase = 62_000;

    /// <summary>Table → (key columns, the key column that must sit in a reserved range, its floor).</summary>
    public static readonly IReadOnlyDictionary<string, (string[] Key, string RangeColumn, uint Floor)> Tables =
        new Dictionary<string, (string[], string, uint)>(StringComparer.OrdinalIgnoreCase)
        {
            ["creature_template"] = (new[] { "entry", "patch" }, "entry", TemplateBase),
            ["creature"] = (new[] { "guid" }, "guid", SpawnGuidBase),
            ["creature_addon"] = (new[] { "guid", "patch" }, "guid", SpawnGuidBase),
            ["creature_movement"] = (new[] { "id", "point" }, "id", SpawnGuidBase),
            // Linked packs and patrol formations (leader_guid, member_guid, dist, angle, flags - CreatureGroups.h
            // OPTION_*); the leader lists itself as a member. Both guids must be pack spawns (checked below).
            ["creature_groups"] = (new[] { "member_guid" }, "member_guid", SpawnGuidBase),
            ["gameobject_loot_template"] = (new[] { "entry", "item" }, "entry", TemplateBase),
            ["creature_equip_template"] = (new[] { "entry", "item1", "item2", "item3" }, "entry", TemplateBase),
            ["creature_loot_template"] = (new[] { "entry", "item", "groupid", "patch_min", "patch_max" }, "entry", TemplateBase),
            ["reference_loot_template"] = (new[] { "entry", "item", "patch_min", "patch_max" }, "entry", TemplateBase),
            ["creature_ai_events"] = (new[] { "id" }, "id", TemplateBase),
            ["creature_ai_scripts"] = (new[] { "id", "delay", "command", "datalong" }, "id", TemplateBase),
            ["npc_vendor"] = (new[] { "entry", "item" }, "entry", TemplateBase),
            ["npc_trainer"] = (new[] { "entry", "spell" }, "entry", TemplateBase),
            ["npc_text"] = (new[] { "ID" }, "ID", TemplateBase),
            ["broadcast_text"] = (new[] { "entry" }, "entry", TemplateBase),
            ["gossip_menu"] = (new[] { "entry", "text_id" }, "entry", GossipMenuBase),
            // A creature with its own gossip menu shows ONLY that menu's options (menu 0's generic
            // "browse goods"/"train me" apply to menu-less creatures) — service NPCs need these rows.
            ["gossip_menu_option"] = (new[] { "menu_id", "id" }, "menu_id", GossipMenuBase),
            ["quest_template"] = (new[] { "entry", "patch" }, "entry", TemplateBase),
            ["creature_questrelation"] = (new[] { "id", "quest" }, "quest", TemplateBase),
            ["creature_involvedrelation"] = (new[] { "id", "quest" }, "quest", TemplateBase),
            ["gameobject_questrelation"] = (new[] { "id", "quest" }, "quest", TemplateBase),
            ["gameobject_involvedrelation"] = (new[] { "id", "quest" }, "quest", TemplateBase),
            // Exploration objectives: stepping into the trigger completes the quest (quest SpecialFlags 2).
            ["areatrigger_involvedrelation"] = (new[] { "id", "quest" }, "quest", TemplateBase),
            ["item_template"] = (new[] { "entry", "patch" }, "entry", TemplateBase),
            ["gameobject_template"] = (new[] { "entry", "patch" }, "entry", TemplateBase),
            ["gameobject"] = (new[] { "guid" }, "guid", SpawnGuidBase),
            ["map_template"] = (new[] { "entry", "patch" }, "entry", MapIdBase),
            ["area_template"] = (new[] { "entry" }, "entry", AreaIdBase),
            ["areatrigger_template"] = (new[] { "id", "build" }, "id", AreaIdBase),
            ["areatrigger_teleport"] = (new[] { "id", "patch" }, "id", AreaIdBase),
            ["game_graveyard_zone"] = (new[] { "id", "ghost_zone", "patch_max" }, "ghost_zone", AreaIdBase),
            ["game_tele"] = (new[] { "id" }, "id", AreaIdBase),
        };

    /// <summary>Client DBCs a pack may extend → the floor of the ids it may add.</summary>
    public static readonly IReadOnlyDictionary<string, uint> Dbcs = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["Map"] = MapIdBase,
        ["AreaTable"] = AreaIdBase,
        ["AreaTrigger"] = AreaIdBase,
        ["WorldMapArea"] = AreaIdBase,
        ["LoadingScreens"] = 700,
        ["WorldSafeLocs"] = AreaIdBase,
        ["Light"] = AreaIdBase,   // a pack map needs its own map-wide light (row 1 = Eastern Kingdoms')
    };

    /// <summary>DBCs mangosd reads from its own DataDir (5875/dbc) — installed there too.</summary>
    public static readonly IReadOnlySet<string> ServerDbcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WorldSafeLocs" };

    /// <summary>Validate a DB row and return its canonical doc key. Throws on anything unsafe.</summary>
    public static string RowKey(string table, JsonObject row)
    {
        if (!Tables.TryGetValue(table, out var rule))
            throw new ArgumentException($"table '{table}' is not pack-writable");
        foreach (var (col, _) in row)
            if (col.Length == 0 || col.Length > 64 || !col.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                throw new ArgumentException($"bad column name '{col}'");
        var parts = new List<string>();
        foreach (var k in rule.Key)
        {
            var v = row[k] ?? row.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value;
            parts.Add(v is null ? "0" : Scalar(v));
        }
        var rangeValue = row[rule.RangeColumn] ?? row.FirstOrDefault(kv => kv.Key.Equals(rule.RangeColumn, StringComparison.OrdinalIgnoreCase)).Value;
        // A reserved NPC may retain its stock quests; a stock NPC may offer a reserved quest.
        // A relationship between two stock IDs remains outside pack ownership.
        bool npcRelation = table.Equals("creature_questrelation", StringComparison.OrdinalIgnoreCase) ||
            table.Equals("creature_involvedrelation", StringComparison.OrdinalIgnoreCase);
        var npc = row["id"] ?? row.FirstOrDefault(kv => kv.Key.Equals("id", StringComparison.OrdinalIgnoreCase)).Value;
        bool ownedNpcRelation = npcRelation && npc is not null &&
            uint.TryParse(Scalar(npc), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint npcId) && npcId >= TemplateBase;
        if (ownedNpcRelation && (rangeValue is null ||
            !uint.TryParse(Scalar(rangeValue), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint questId) || questId == 0))
            throw new ArgumentException($"{table}.quest must be a positive quest ID.");
        if (!ownedNpcRelation && (rangeValue is null || !uint.TryParse(Scalar(rangeValue), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id) || id < rule.Floor))
            throw new ArgumentException($"{table}.{rule.RangeColumn} must be >= {rule.Floor} (packs only add rows in reserved ranges)");
        uint rangeId = rangeValue is not null && uint.TryParse(Scalar(rangeValue), out uint parsedRange) ? parsedRange : 0;
        if (rule.Floor == SpawnGuidBase && rangeId >= SpawnGuidCeiling)
            throw new ArgumentException($"{table}.{rule.RangeColumn} must be < {SpawnGuidCeiling}: vanilla spawn guids are 24-bit and mangosd needs runtime headroom above them");
        // A pack links only its own spawns: the leader is a pack guid too (never a stock creature).
        if (table.Equals("creature_groups", StringComparison.OrdinalIgnoreCase) &&
            (row["leader_guid"] is not { } leader || !uint.TryParse(Scalar(leader), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint lead) ||
             lead < SpawnGuidBase || lead >= SpawnGuidCeiling))
            throw new ArgumentException($"creature_groups.leader_guid must be a pack spawn guid in [{SpawnGuidBase}, {SpawnGuidCeiling})");
        return string.Join('|', parts);
    }

    public static string Scalar(JsonNode node) => node is JsonValue v
        ? v.TryGetValue<string>(out var s) ? s : v.ToJsonString()
        : node.ToJsonString();

    public static void ValidateDbc(string name, uint id, JsonObject body)
    {
        if (!Dbcs.TryGetValue(name, out uint floor)) throw new ArgumentException($"DBC '{name}' is not pack-writable");
        if (id < floor) throw new ArgumentException($"{name}.dbc ids added by packs must be >= {floor}");
        if (body["fields"] is not JsonObject) throw new ArgumentException("dbc body needs a 'fields' object");
    }
}
