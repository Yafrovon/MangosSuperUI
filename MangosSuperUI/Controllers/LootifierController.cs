using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text.Json;
using MySqlConnector;

namespace MangosSuperUI.Controllers;

// ══════════════════════════════════════════════════════════════════════════
//  LOOTIFIER v5 — weighted pools
//
//  Building on v4 (dedup + scoped refs), variants now form a single WEIGHTED
//  POOL with the base item rather than a scatter of independent direct rows.
//
//  Why: VMaNGOS LootGroup::Roll (LootMgr.cpp) treats a grouped row's
//  ChanceOrQuestChance as an ABSOLUTE percentage of one 0-100 roll, walking
//  members and subtracting until the roll goes negative — the group yields
//  exactly ONE item per Process(). Independent direct rows (the v4 approach)
//  therefore drop as N separate coin-flips, not "pick one of N". To get
//  Diablo-style "one item drops, weighted among base+variants", all members
//  must live in one group whose explicit chances sum to 100.
//
//  Two cases, both converging on a 100%/1 weighted reference pool (identical
//  in shape to a vanilla shared pool, so it renders/edits in Instance Loot):
//
//  1. REF-SOURCED (base already in a reference_loot_template group):
//     variants are added to that SAME ref group as explicit members, then the
//     whole group (base + prior members + variants) is renormalized to sum
//     100 so nothing is diluted into a dead-roll gap. Action: 'pool_joined'.
//
//  2. DIRECT-DROP boss (Hogger, Edwin's legendary — no pool):
//     a fresh reference_loot_template entry is minted; the base row is moved
//     out of creature_loot_template into it; variants are added; all members
//     normalized to 100; and the base's direct row is replaced by a single
//     pointer (ChanceOrQuestChance=100, mincountOrRef=-refEntry, maxcount=1).
//     Action: 'pool_created'.
//
//  Carried over from v4:
//   - BATCH DEDUP: one variant set per distinct base item; each (loot, base)
//     pair pooled once, seeded from tracking for idempotent re-runs.
//   - ROLLBACK: 'pool_created' deletes the minted ref + restores the base's
//     direct row; 'pool_joined' deletes only the added variant members and
//     restores the pre-join member chances. Shared item_template rows are
//     kept if another creature still references them.
// ══════════════════════════════════════════════════════════════════════════

public class LootifierController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;
    private readonly IWebHostEnvironment _env;

    // Per-request collector for pool capacity warnings (families that can't
    // seat every member at MEMBER_FLOOR_PCT). Surfaced in the commit response.
    private List<string>? _poolWarnings;

    private const int LOOTIFIER_ID_START = 950000;
    private const string CHARACTERS_DB = "characters"; // player item_instance lives here (same server, mangos user has rights)

    // ── RECURSION GUARD ──
    // Every item the Lootifier mints lands in item_template at entry >=
    // LOOTIFIER_ID_START, and its pool row lands in reference_loot_template
    // (mincountOrRef > 0) under a ref the creature points at. That makes a
    // generated variant structurally indistinguishable from a vanilla shared-pool
    // drop, so the NEXT batch scan picked it up as a BASE item and lootified it
    // again — "Improved Improved Smite's Reaver", "Mr. Smite's Improved Smite's
    // Reaver", and N^2 growth per run. Anything at or above this floor is our own
    // output and can never be a base.
    private static bool IsGeneratedEntry(int entry) => entry >= LOOTIFIER_ID_START;

    // Loot group used for freshly-minted direct-drop variant pools.
    private const int POOL_GROUP_ID = 98;

    // Reference-table entries the Lootifier mints for direct-drop pools start
    // here, well above vanilla reference_loot_template ids (~35000 in 1.12).
    private const int LOOTIFIER_REF_START = 9500000;

    // creature_template.loot_id values minted for no-loot mobs in additive mode
    // start here (distinct high range so they never collide with vanilla loot_ids).
    private const int LOOTIFIER_LOOTID_START = 9000000;

    // Pool share model (v6): the BASE item becomes a rare fallback and the
    // lootified variants take almost all of the base's original pool share.
    //   - BASE_FLOOR_PCT: base item's fixed share inside its family (special,
    //     exempt from the member floor below).
    //   - MEMBER_FLOOR_PCT: no variant/legendary may sit below this — anything
    //     the tier-split pushes under it snaps up, cost taken proportionally
    //     from the family's above-floor members.
    // If a family has so many members that 0.5 + 0.3*count exceeds its share,
    // it can't be floored legally → we skip flooring for that family and warn.
    private const float BASE_FLOOR_PCT = 0.5f;
    private const float MEMBER_FLOOR_PCT = 0.3f;

    // VMaNGOS skips grouped rows with 0 < chance < 0.000001 (LootMgr.cpp:308).
    private const float MIN_POOL_CHANCE = 0.0005f;

    // ── Additive-generation dials (mirror Quest/Crafting Lootifiers) ──
    private const float MIN_DELTA_BUDGET = 2.0f;   // additive floor: every variant is a real upgrade
    // Minimum stat-budget SEPARATION between consecutive tiers. On a small base
    // (Belt of the Fang: +3 Sta/+2 Agi = 5 weighted) a 10% and a 50% boost are only
    // 0.5 and 2.5 budget apart, and MIN_DELTA_BUDGET floors BOTH to 2.0 — so every
    // tier rolled an identical stat block, the fingerprint dedup killed the repeats,
    // and 9 requested variants collapsed to 2. Each tier now gets a floor one step
    // above the tier below it, guaranteeing the ladder stays distinguishable no
    // matter how weak the base item is.
    private const float MIN_TIER_STEP = 1.5f;

    // Set by the most recent GenerateVariants call on this controller instance.
    private int LastRequestedVariantCount;
    private int LastProducedVariantCount;
    private const float LOOT_BUMP_BIAS = 0.5f;     // delta split: existing-line bumps vs new affixes

    // ── Weapon DPS by tier (damage-only; weapon SPEED/delay is never touched) ──
    // Vanilla (pre-TBC) weapon DPS = Green(iLevel) × qualityMult, so at a FIXED
    // iLevel the only lever between qualities is a flat multiplier on the damage
    // range: blue ×1.105, epic ×1.215 over the green line (Vanilla WoW Wiki item-DPS
    // budget). A tier bump keeps the weapon's iLevel, hand type and delay, so scaling
    // min+max damage by (1+p) raises DPS by exactly p%. Defaults live in Meta;
    // legendary had no vanilla formula slot (Sulfuras ~+7% over an epic 2H, Thunderfury
    // far below), so its bump is a nominal, override-me ceiling.
    private const float DPS_GREEN_SLOPE = 0.6f;
    private const float DPS_GREEN_INTERCEPT = 26.6f;
    private const int DPS_GREEN_ILVL_FLOOR = 45;   // Green1H = (iLevel-45)*0.6 + 26.6 (valid ~iLevel 45-65)

    // ── VERIFIED stat type mapping (confirmed from VMaNGOS item_template) ──
    // Judgement Legplates: 5=27(Int), 6=5(Spi), 7=26(Sta), 4=10(Str) ✓
    // Eye of Rend: 4=13(Str), 7=7(Sta) ✓
    // Corsair's Overshirt: 6=11(Spi), 7=5(Sta) ✓
    private static readonly Dictionary<int, string> STAT_NAMES = new()
    {
        [0] = "None",
        [1] = "Health",
        [3] = "Agility",
        [4] = "Strength",
        [5] = "Intellect",    // VERIFIED: Judgement Legplates type5=27 = +27 Int
        [6] = "Spirit",       // VERIFIED: Corsair's Overshirt type6=11 = +11 Spi
        [7] = "Stamina"       // VERIFIED: Eye of Rend type7=7 = +7 Sta
    };

    // ── Stat families (5=Int, 6=Spi, 7=Sta) ──
    private static readonly Dictionary<string, HashSet<int>> STAT_FAMILIES = new()
    {
        ["physical"] = new HashSet<int> { 3, 4, 7 },       // Agi, Str, Sta
        ["caster"] = new HashSet<int> { 5, 6, 7 },         // Int, Spirit, Sta
        ["hybrid"] = new HashSet<int> { 3, 4, 5, 6, 7 },
    };

    // ── Verified stat budget weights (Blizzard StatMod values) ──
    // Stamina(7) = 2/3 cost. All others = 1.0.
    private static readonly Dictionary<int, float> DEFAULT_STAT_WEIGHTS = new()
    {
        [3] = 1.0f,      // Agility
        [4] = 1.0f,      // Strength
        [5] = 1.0f,      // Intellect
        [6] = 1.0f,      // Spirit
        [7] = 0.6667f    // Stamina (2/3 budget cost per point)
    };

    // ── Spell trigger types ──
    private const int SPELLTRIGGER_USE = 0;
    private const int SPELLTRIGGER_EQUIP = 1;
    private const int SPELLTRIGGER_CHANCE_ON_HIT = 2;

    // ── Item classes — only equippable Weapon/Armor gear is lootifiable ──
    // Recipes (9), consumables (0), trade goods (7), quest items (12), reagents,
    // projectiles, etc. are excluded even when they carry a Use: spell effect.
    private const int ITEM_CLASS_WEAPON = 2;
    private const int ITEM_CLASS_ARMOR = 4;

    // Map IDs for batch mode
    private static readonly Dictionary<int, string> MAP_NAMES = new()
    {
        [33] = "Shadowfang Keep",
        [34] = "The Stockade",
        [36] = "Deadmines",
        [43] = "Wailing Caverns",
        [47] = "Razorfen Kraul",
        [48] = "Blackfathom Deeps",
        [70] = "Uldaman",
        [90] = "Gnomeregan",
        [109] = "Sunken Temple",
        [129] = "Razorfen Downs",
        [189] = "Scarlet Monastery",
        [209] = "Zul'Farrak",
        [229] = "Blackrock Spire",
        [230] = "Blackrock Depths",
        [249] = "Onyxia's Lair",
        [289] = "Scholomance",
        [309] = "Zul'Gurub",
        [329] = "Stratholme",
        [349] = "Maraudon",
        [389] = "Ragefire Chasm",
        [409] = "Molten Core",
        [429] = "Dire Maul",
        [469] = "Blackwing Lair",
        [509] = "Ruins of Ahn'Qiraj",
        [531] = "Temple of Ahn'Qiraj",
        [533] = "Naxxramas"
    };

    public LootifierController(ConnectionFactory db, DbcService dbc, AuditService audit, IWebHostEnvironment env)
    {
        _db = db;
        _dbc = dbc;
        _audit = audit;
        _env = env;
    }

    public IActionResult Index() => View();

    [HttpGet]
    public IActionResult Meta()
    {
        return Json(new
        {
            statNames = STAT_NAMES,
            defaultStatWeights = DEFAULT_STAT_WEIGHTS,
            // minBoostPct/maxBoostPct = how much stronger than the base item this tier
            // rolls, as a % of the base's weighted stat budget. This IS the strength
            // control — same ladder the Crafting Lootifier ships with. minPct/maxPct
            // are legacy and only consulted for a tier with no Boost set.
            defaultNamingTiers = new[]
            {
                new { minPct = 0, maxPct = 79, label = "Improved", position = "prefix", goldBumpPct = 40f, dpsBumpPct = 8f, slots = 5, minBoostPct = (float?)10f, maxBoostPct = (float?)20f },
                new { minPct = 80, maxPct = 89, label = "of Power", position = "suffix", goldBumpPct = 60f, dpsBumpPct = 10.5f, slots = 2, minBoostPct = (float?)20f, maxBoostPct = (float?)30f },
                new { minPct = 90, maxPct = 100, label = "of Glory", position = "suffix", goldBumpPct = 80f, dpsBumpPct = 21.5f, slots = 2, minBoostPct = (float?)30f, maxBoostPct = (float?)45f }
            },
            defaultRuleset = new
            {
                budgetCeilingPct = 35,
                variantsPerItem = 9,   // matches the default slot split 5 + 2 + 2
                allowNewAffixes = true,
                maxAffixCountChange = 1,
                dropChanceStrategy = "preserve",
                poolDropChancePct = 100,
                legendaryGoldBumpPct = 500,
                legendaryDpsBumpPct = 30,
                legendaryBoostMinPct = 55,
                legendaryBoostMaxPct = 75
            },
            // Vanilla weapon-DPS reference so the UI can relate a tier's damage bump
            // to blue/purple/legendary at the base weapon's level.
            dpsReference = new
            {
                qualityMult = new { green = 1.000f, blue = 1.105f, purple = 1.215f, legendary = 1.300f },
                greenOneHand = new { slope = DPS_GREEN_SLOPE, intercept = DPS_GREEN_INTERCEPT, ilvlFloor = DPS_GREEN_ILVL_FLOOR },
                note = "blue +10.5% / purple +21.5% over green at the same level; legendary 1.30 is nominal (vanilla legendaries were hand-tuned)."
            },
            maps = MAP_NAMES.OrderBy(kv => kv.Value).Select(kv => new { id = kv.Key, name = kv.Value })
        });
    }

    // ===================== CREATURE SEARCH =====================

    [HttpGet]
    public async Task<IActionResult> SearchCreature(string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Json(new { results = Array.Empty<object>() });

        using var conn = _db.Mangos();
        var results = await conn.QueryAsync<dynamic>(@"
            SELECT ct.entry, ct.name, ct.rank, ct.level_min, ct.level_max, ct.loot_id
            FROM creature_template ct
            WHERE ct.name LIKE @Q
              AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)
              AND ct.loot_id > 0
            ORDER BY ct.rank DESC, ct.level_max DESC, ct.name
            LIMIT 25", new { Q = $"%{q}%" });

        return Json(new { results });
    }

    // ===================== LOOT TREE =====================

    [HttpGet]
    public async Task<IActionResult> LootTree(int creatureEntry)
    {
        using var conn = _db.Mangos();

        var creature = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT entry, name, rank, level_min, level_max, loot_id
            FROM creature_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = @E)",
            new { E = creatureEntry });

        if (creature == null)
            return Json(new { success = false, error = "Creature not found" });

        int lootId = (int)creature.loot_id;

        var allRows = (await conn.QueryAsync<LootifierLootRow>(@"
            SELECT lt.entry AS lootEntry, lt.item, lt.ChanceOrQuestChance AS chance,
                   lt.groupid AS groupId, lt.mincountOrRef, lt.maxcount,
                   lt.patch_min AS patchMin, lt.patch_max AS patchMax
            FROM creature_loot_template lt
            WHERE lt.entry = @LootId
            ORDER BY lt.groupid, lt.mincountOrRef, lt.ChanceOrQuestChance DESC",
            new { LootId = lootId })).ToList();

        var directItems = allRows.Where(r => r.mincountOrRef > 0).ToList();
        var refPointers = allRows.Where(r => r.mincountOrRef < 0).ToList();

        var refGroups = new List<object>();
        foreach (var ptr in refPointers)
        {
            int refEntry = Math.Abs(ptr.mincountOrRef);
            var refItems = (await conn.QueryAsync<LootifierLootRow>(@"
                SELECT rlt.entry AS lootEntry, rlt.item, rlt.ChanceOrQuestChance AS chance,
                       rlt.groupid AS groupId, rlt.mincountOrRef, rlt.maxcount,
                       rlt.patch_min AS patchMin, rlt.patch_max AS patchMax
                FROM reference_loot_template rlt
                WHERE rlt.entry = @RefEntry
                ORDER BY rlt.groupid, rlt.ChanceOrQuestChance DESC",
                new { RefEntry = refEntry })).ToList();

            var enriched = await EnrichLootRows(conn, refItems);

            refGroups.Add(new
            {
                refEntry,
                pointerChance = ptr.chance,
                pointerGroupId = ptr.groupId,
                items = enriched
            });
        }

        var directEnriched = await EnrichLootRows(conn, directItems);

        var iconMap = new Dictionary<uint, string>();
        void addIcons(IEnumerable<dynamic> rows)
        {
            foreach (var r in rows)
            {
                uint did = (uint)(r.displayId ?? 0);
                if (did > 0 && !iconMap.ContainsKey(did))
                    iconMap[did] = _dbc.GetItemIconPath(did);
            }
        }
        addIcons(directEnriched);
        foreach (var rg in refGroups)
            addIcons(((dynamic)rg).items as IEnumerable<dynamic> ?? Enumerable.Empty<dynamic>());

        return Json(new
        {
            success = true,
            creature = new { creature.entry, creature.name, creature.rank, creature.level_min, creature.level_max, creature.loot_id },
            directItems = directEnriched,
            referenceGroups = refGroups,
            icons = iconMap
        });
    }

    // ===================== ANALYZE ITEM =====================

    [HttpGet]
    public async Task<IActionResult> AnalyzeItem(int entry)
    {
        using var conn = _db.Mangos();
        var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT * FROM item_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
            new { E = entry });

        if (item == null)
            return Json(new { success = false, error = "Item not found" });

        return Json(new { success = true, analysis = AnalyzeItemStats(item) });
    }

    // ===================== GENERATE PREVIEW (single source) =====================

    [HttpPost]
    public async Task<IActionResult> GeneratePreview([FromBody] GenerateRequest request)
    {
        if (request.itemEntries == null || request.itemEntries.Length == 0)
            return Json(new { success = false, error = "No items selected" });

        using var conn = _db.Mangos();
        var ruleset = request.ruleset ?? new RulesetDto();
        var allVariants = new List<object>();

        foreach (var itemEntry in request.itemEntries)
        {
            // Recursion guard — a generated variant can never be a base item.
            if (IsGeneratedEntry(itemEntry)) continue;

            var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT * FROM item_template
                WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
                new { E = itemEntry });

            if (item == null) continue;

            var analysis = AnalyzeItemStats(item);
            bool hasStats = (int)analysis.totalStats > 0;
            bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;

            // Eligibility is EQUIPPABLE, full stop: any weapon/armor in an inventory
            // slot rolls (stat-less whites included — GenerateVariants mints a budget
            // from item_level). Recipes, consumables, reagents etc. never do.
            if (!IsEquippableGear(item)) continue;

            var variants = GenerateVariants(item, analysis, ruleset);
            var wpn = WeaponDpsInfo(item);
            allVariants.Add(new
            {
                baseItem = new
                {
                    entry = (int)item.entry,
                    name = (string)item.name,
                    quality = (int)item.quality,
                    displayId = (uint)item.display_id,
                    weapon = wpn
                },
                analysis,
                variants = VariantsToJson(variants, (bool)wpn.isWeapon, (float)wpn.baseDps, ruleset)
            });
        }

        // Generate legendary preview if enabled
        object? legendaryPreview = null;
        if (ruleset.generateLegendary && request.creatureEntry > 0)
        {
            var eligibleEntries = allVariants.Count > 0
                ? request.itemEntries.ToList()
                : new List<int>();
            legendaryPreview = await PreviewLegendary(conn, request.creatureEntry, eligibleEntries, ruleset);
        }

        return Json(new { success = true, items = allVariants, legendary = legendaryPreview });
    }

    // ===================== CURATED BOSS LIST =====================

    private List<int> GetCuratedBossEntries(int[]? mapIds)
    {
        var path = Path.Combine(_env.WebRootPath, "data", "instance-bosses.json");
        if (!System.IO.File.Exists(path)) return new List<int>();

        var json = System.IO.File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);
        var entries = new List<int>();

        foreach (var instance in doc.RootElement.GetProperty("instances").EnumerateArray())
        {
            int mapId = instance.GetProperty("mapId").GetInt32();
            if (mapIds != null && mapIds.Length > 0 && !mapIds.Contains(mapId))
                continue;

            foreach (var boss in instance.GetProperty("bosses").EnumerateArray())
                entries.Add(boss.GetProperty("entry").GetInt32());
        }

        return entries;
    }

    // ===================== ZONE FILTERING (via DbcService.WorldMapZones) =====================
    //
    // VMaNGOS `creature` spawns have map + position but no zone column, so
    // zone filtering is spatial: each zone's bounding box comes from the
    // client's WorldMapArea.dbc, parsed once at startup by DbcService from
    // the configured Vmangos:DbcPath (same source as icons/spells). Boxes
    // are the world-map rectangles, so mobs right on a zone border can match
    // the neighbouring zone too — visible and uncheckable in the preview.

    /// <summary>Outdoor zones only: maps 0/1, skipping continent overview rows.</summary>
    private IEnumerable<WorldMapZoneDbc> OutdoorZones() =>
        _dbc.WorldMapZones.Where(z => z.AreaId != 0 && (z.MapId == 0 || z.MapId == 1));

    [HttpGet]
    public IActionResult Zones()
    {
        var zones = OutdoorZones()
            .OrderBy(z => z.MapId)
            .ThenBy(z => z.DisplayName)
            .ToList();

        return Json(new
        {
            success = true,
            available = zones.Count > 0,
            dbcPath = _dbc.DbcPath,
            zones = zones.Select(z => new { areaId = z.AreaId, mapId = z.MapId, name = z.DisplayName })
        });
    }

    /// <summary>
    /// Build the spawn-location WHERE clause for batch scans: dungeon map IDs
    /// (parameterized) OR'd with zone bounding boxes (inline literals — values
    /// are floats parsed from the binary DBC, not user strings). Returns null
    /// when no location filter is active. Requires `creature c` to be joined.
    /// </summary>
    private string? BuildLocationWhere(BatchRequest request, string mapParamName)
    {
        var parts = new List<string>();

        if (request.mapIds != null && request.mapIds.Length > 0)
            parts.Add($"c.map IN @{mapParamName}");

        if (request.zoneIds != null && request.zoneIds.Length > 0)
        {
            var zones = OutdoorZones().ToList();
            foreach (var zid in request.zoneIds)
            {
                var z = zones.FirstOrDefault(zb => zb.AreaId == (uint)zid);
                if (z == null) continue;

                float xLo = Math.Min(z.LocBottom, z.LocTop);
                float xHi = Math.Max(z.LocBottom, z.LocTop);
                float yLo = Math.Min(z.LocRight, z.LocLeft);
                float yHi = Math.Max(z.LocRight, z.LocLeft);

                parts.Add(FormattableString.Invariant(
                    $"(c.map = {z.MapId} AND c.position_x BETWEEN {xLo:F1} AND {xHi:F1} AND c.position_y BETWEEN {yLo:F1} AND {yHi:F1})"));
            }
        }

        return parts.Count > 0 ? "(" + string.Join(" OR ", parts) + ")" : null;
    }

    // ===================== BATCH PREVIEW =====================

    [HttpPost]
    public async Task<IActionResult> BatchPreview([FromBody] BatchRequest request)
    {
        using var conn = _db.Mangos();

        string ItemQualityWhere(DynamicParameters p, string prefix)
        {
            if (request.qualities != null && request.qualities.Length > 0)
            {
                p.Add(prefix + "Qualities", request.qualities);
                return $"it.quality IN @{prefix}Qualities";
            }
            return "";
        }
        string ItemLevelWhere(DynamicParameters p, string prefix)
        {
            var parts = new List<string>();
            if (request.levelMin > 0) { p.Add(prefix + "LevelMin", request.levelMin); parts.Add($"it.required_level >= @{prefix}LevelMin"); }
            if (request.levelMax > 0) { p.Add(prefix + "LevelMax", request.levelMax); parts.Add($"it.required_level <= @{prefix}LevelMax"); }
            return string.Join(" AND ", parts);
        }
        // ── Candidate gate: EQUIPPABLE GEAR ONLY ──
        // Equippable is the whole test: any weapon/armor occupying an inventory
        // slot rolls (any slot — trinkets, rings, necks, shirts, tabards, cloaks,
        // shields all count). Everything else does not.
        //
        // The old filter was `stat_type1>0 OR ... OR spellid_1>0`, and that
        // `spellid_1` clause is exactly why plans and recipes were being picked:
        // a recipe's spellid_1 IS its teach-spell. Same for potions, scrolls and
        // food. Class 2 = Weapon, 4 = Armor; recipes (9), consumables (0), trade
        // goods (7), bags (1), ammo (6) and quivers (11) are all excluded by class,
        // and inventory_type > 0 drops anything that can't actually be worn.
        //
        // Note there's deliberately NO stat/spell requirement: a plain white
        // equippable with no stats is still a valid base — GenerateVariants mints
        // a budget from its item level.
        //
        // The `it.entry < LOOTIFIER_ID_START` clause is the recursion guard: our
        // own variants sit in reference_loot_template exactly like a vanilla
        // shared pool, so without it the second run scans run one's output as
        // base items and lootifies the lootified (see IsGeneratedEntry).
        string ItemFilter() => $"(it.class IN (2, 4) AND it.inventory_type > 0 AND it.entry < {LOOTIFIER_ID_START})";

        var p1 = new DynamicParameters();
        var w1 = new List<string> { "lt.mincountOrRef > 0", ItemFilter() };
        var j1 = new List<string>
        {
            @"JOIN item_template it ON it.entry = lt.item
              AND it.patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = it.entry)",
            @"JOIN creature_template ct ON ct.loot_id = lt.entry
              AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)"
        };

        var qw = ItemQualityWhere(p1, "d");
        if (!string.IsNullOrEmpty(qw)) w1.Add(qw);
        var lw = ItemLevelWhere(p1, "d");
        if (!string.IsNullOrEmpty(lw)) w1.Add(lw);

        var hasBossRank = request.creatureRanks != null && request.creatureRanks.Contains(3);
        var curatedBosses = hasBossRank ? GetCuratedBossEntries(request.mapIds) : new List<int>();

        if (request.creatureRanks != null && request.creatureRanks.Length > 0)
        {
            if (curatedBosses.Count > 0)
            {
                w1.Add("(ct.rank IN @dRanks OR ct.entry IN @dBossEntries)");
                p1.Add("dRanks", request.creatureRanks);
                p1.Add("dBossEntries", curatedBosses);
            }
            else
            {
                w1.Add("ct.rank IN @dRanks");
                p1.Add("dRanks", request.creatureRanks);
            }
        }
        var locWhere1 = BuildLocationWhere(request, "dMapIds");
        if (locWhere1 != null)
        {
            j1.Add("JOIN creature c ON c.id = ct.entry");
            w1.Add(locWhere1);
            if (request.mapIds != null && request.mapIds.Length > 0)
                p1.Add("dMapIds", request.mapIds);
        }

        var directSql = $@"SELECT DISTINCT
                it.entry AS itemEntry, it.name AS itemName, it.quality, it.display_id AS displayId,
                it.required_level, it.item_level,
                ct.entry AS creatureEntry, ct.name AS creatureName, ct.rank AS creatureRank,
                ct.level_min, ct.level_max, ct.loot_id AS lootId,
                lt.ChanceOrQuestChance AS chance, lt.groupid
            FROM creature_loot_template lt
            {string.Join(" ", j1)}
            WHERE {string.Join(" AND ", w1)}";

        var directRows = (await conn.QueryAsync<dynamic>(directSql, p1)).ToList();

        var p2 = new DynamicParameters();
        var w2 = new List<string> { "rlt.mincountOrRef > 0", "clt.mincountOrRef < 0", ItemFilter() };
        var j2 = @"JOIN item_template it ON it.entry = rlt.item
                   AND it.patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = it.entry)
                   JOIN creature_template ct ON ct.loot_id = clt.entry
                   AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)";

        var qw2 = ItemQualityWhere(p2, "r");
        if (!string.IsNullOrEmpty(qw2)) w2.Add(qw2);
        var lw2 = ItemLevelWhere(p2, "r");
        if (!string.IsNullOrEmpty(lw2)) w2.Add(lw2);

        if (request.creatureRanks != null && request.creatureRanks.Length > 0)
        {
            if (curatedBosses.Count > 0)
            {
                w2.Add("(ct.rank IN @rRanks OR ct.entry IN @rBossEntries)");
                p2.Add("rRanks", request.creatureRanks);
                p2.Add("rBossEntries", curatedBosses);
            }
            else
            {
                w2.Add("ct.rank IN @rRanks");
                p2.Add("rRanks", request.creatureRanks);
            }
        }

        string mapJoin2 = "";
        var locWhere2 = BuildLocationWhere(request, "rMapIds");
        if (locWhere2 != null)
        {
            mapJoin2 = "JOIN creature c ON c.id = ct.entry";
            w2.Add(locWhere2);
            if (request.mapIds != null && request.mapIds.Length > 0)
                p2.Add("rMapIds", request.mapIds);
        }

        var refSql = $@"SELECT DISTINCT
                it.entry AS itemEntry, it.name AS itemName, it.quality, it.display_id AS displayId,
                it.required_level, it.item_level,
                ct.entry AS creatureEntry, ct.name AS creatureName, ct.rank AS creatureRank,
                ct.level_min, ct.level_max, ct.loot_id AS lootId,
                rlt.ChanceOrQuestChance AS chance, rlt.groupid
            FROM creature_loot_template clt
            JOIN reference_loot_template rlt ON rlt.entry = ABS(clt.mincountOrRef)
            {j2}
            {mapJoin2}
            WHERE {string.Join(" AND ", w2)}";

        var refRows = (await conn.QueryAsync<dynamic>(refSql, p2)).ToList();

        var rows = directRows.Concat(refRows).ToList();

        var byCreature = rows.GroupBy(r => (int)r.creatureEntry).Select(g =>
        {
            var first = g.First();
            return new
            {
                creatureEntry = (int)first.creatureEntry,
                creatureName = (string)first.creatureName,
                creatureRank = (int)first.creatureRank,
                levelMin = (int)first.level_min,
                levelMax = (int)first.level_max,
                lootId = (int)first.lootId,
                items = g.Select(r => new
                {
                    itemEntry = (int)r.itemEntry,
                    itemName = (string)r.itemName,
                    quality = (int)r.quality,
                    displayId = (uint)r.displayId,
                    requiredLevel = (int)r.required_level,
                    chance = (float)r.chance
                }).DistinctBy(x => x.itemEntry).ToList()
            };
        }).ToList();

        var iconMap = new Dictionary<uint, string>();
        foreach (var r in rows)
        {
            uint did = (uint)r.displayId;
            if (did > 0 && !iconMap.ContainsKey(did))
                iconMap[did] = _dbc.GetItemIconPath(did);
        }

        // NOTE: distinctBaseItems is the true item_template creation multiplier
        // now that BatchCommit dedups variant generation per base item.
        int distinctBaseItems = rows.Select(r => (int)r.itemEntry).Distinct().Count();

        return Json(new
        {
            success = true,
            totalItems = distinctBaseItems,
            totalCreatures = byCreature.Count,
            creatures = byCreature,
            icons = iconMap,
            truncated = false
        });
    }

    // ===================== BATCH SAMPLE PREVIEW =====================

    /// <summary>
    /// POST /Lootifier/BatchSamplePreview — Pick representative items from the batch scan
    /// and generate full variant previews for them so the user can see what rolls look like.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> BatchSamplePreview([FromBody] BatchSampleRequest request)
    {
        if (request.itemEntries == null || request.itemEntries.Length == 0)
            return Json(new { success = false, error = "No items provided" });

        using var conn = _db.Mangos();
        var ruleset = request.ruleset ?? new RulesetDto();
        var sampleResults = new List<object>();

        foreach (var itemEntry in request.itemEntries)
        {
            // Recursion guard — previews must show what will actually be built.
            if (IsGeneratedEntry(itemEntry)) continue;

            var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT * FROM item_template
                WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
                new { E = itemEntry });

            if (item == null) continue;

            var analysis = AnalyzeItemStats(item);
            bool hasStats = (int)analysis.totalStats > 0;
            bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;
            if (!IsEquippableGear(item)) continue;

            var variants = GenerateVariants(item, analysis, ruleset);

            uint displayId = (uint)item.display_id;
            string iconPath = _dbc.GetItemIconPath(displayId);
            var wpn = WeaponDpsInfo(item);

            sampleResults.Add(new
            {
                baseItem = new
                {
                    entry = (int)item.entry,
                    name = (string)item.name,
                    quality = (int)item.quality,
                    displayId,
                    iconPath,
                    weapon = wpn
                },
                analysis,
                variants = VariantsToJson(variants, (bool)wpn.isWeapon, (float)wpn.baseDps, ruleset)
            });
        }

        // Generate legendary preview if enabled
        object? legendaryPreview = null;
        if ((request.ruleset?.generateLegendary ?? false) && request.creatureEntry > 0)
        {
            legendaryPreview = await PreviewLegendary(conn, request.creatureEntry, request.itemEntries.ToList(), request.ruleset!);
        }

        return Json(new { success = true, items = sampleResults, legendary = legendaryPreview });
    }

    // ===================== BATCH COMMIT (dedup + scoped refs) =====================

    // Cached column list — fetched once per app lifetime
    private static List<string>? _cachedItemColumns;
    private static readonly SemaphoreSlim _columnCacheLock = new(1, 1);

    private async Task<List<string>> GetItemColumns(MySqlConnector.MySqlConnection conn)
    {
        if (_cachedItemColumns != null) return _cachedItemColumns;
        await _columnCacheLock.WaitAsync();
        try
        {
            if (_cachedItemColumns != null) return _cachedItemColumns;
            _cachedItemColumns = (await conn.QueryAsync<string>(
                "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'mangos' AND TABLE_NAME = 'item_template' ORDER BY ORDINAL_POSITION"
            )).ToList();
            return _cachedItemColumns;
        }
        finally { _columnCacheLock.Release(); }
    }

    [HttpPost]
    public async Task<IActionResult> BatchCommit([FromBody] BatchCommitRequest request)
    {
        // Gives the run a stable identity and title in the Change Graph. Note this commit
        // writes ONE summary audit row, not one per generated item — the per-item detail
        // lives in lootifier_generated_items / lootifier_loot_entries, which is also what
        // Rollback reads. So the batch is a labelled container, not a grouping of many
        // rows; it becomes the latter for free if per-item logging is ever added.
        using var batch = AuditBatch.Begin(
            $"ARPG Lootifier — batch of {request.creatures.Length} creature(s)" +
            (request.regenerate ? " (regenerate)" : ""));

        using var mangosConn = _db.Mangos();
        using var adminConn = _db.Admin();

        await EnsureTrackingTables(adminConn);
        _poolWarnings = new List<string>();

        // Pre-cache column list
        var columns = await GetItemColumns(mangosConn);

        var ruleset = request.ruleset ?? new RulesetDto();
        int totalItemsCreated = 0;
        int totalLootRowsCreated = 0;
        int creaturesProcessed = 0;
        int pairsSkipped = 0;
        int regenReused = 0, regenRemoved = 0, regenRemapped = 0;
        // Slots the fingerprint dedup couldn't fill (roll space exhausted on a weak base).
        int variantsRequested = 0, variantsShort = 0, itemsShort = 0;
        var regenRng = new Random();
        // Regenerate re-expands prior-run pairs; still dedup within THIS run only.
        var thisRunPairs = new HashSet<(int, int)>();

        // ── DEDUP STATE ──
        // One variant set per distinct base item for the whole batch.
        // Value is null when the item was inspected and found ineligible.
        var variantCache = new Dictionary<int, (List<int> entries, CommitRoll[] rolls)?>();

        // Each (lootId, baseItem) pair is expanded at most once — covers
        // creatures sharing a loot_id within this run, and previous runs
        // via the tracking table (idempotent re-commit).
        var expandedPairs = await LoadExpandedPairs(adminConn);

        // Additive mode: independent tunable-chance pool per creature (no dilution).
        bool additive = ruleset.dropChanceStrategy == "additive";
        float poolDropPct = Math.Clamp(ruleset.poolDropChancePct, 0f, 100f);
        var additivized = additive ? await LoadAdditivizedCreatures(adminConn) : new HashSet<int>();

        // Batch tracking rows for bulk insert
        var trackingItemRows = new List<(int genEntry, int baseEntry, int creatureEntry, float budgetPct, string tierName)>();
        var trackingLootRows = new List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)>();

        // Every loot table touched, for the post-write >100% pool sweep.
        var touchedLootIds = new HashSet<int>();

        foreach (var creatureGroup in request.creatures)
        {
            int lootId = await mangosConn.ExecuteScalarAsync<int>(@"
                SELECT loot_id FROM creature_template
                WHERE entry = @E ORDER BY patch DESC LIMIT 1",
                new { E = creatureGroup.creatureEntry });

            // Preserve mode needs an existing loot table; additive mode mints one.
            if (!additive && lootId == 0) continue;
            // Additive is built once per creature — re-run rolls it back first.
            if (additive && additivized.Contains(creatureGroup.creatureEntry)) continue;

            if (lootId > 0) touchedLootIds.Add(lootId);

            // Collected per-creature variant sets for the single additive pool.
            var additiveBatch = new List<(int baseItemEntry, List<int> variantEntries, CommitRoll[] rolls)>();

            foreach (var itemEntry in creatureGroup.itemEntries)
            {
                // Recursion guard: never lootify our own output. A stale client
                // payload or a hand-built request can still carry a generated
                // entry even with the scan filtered, so it's re-checked here.
                if (IsGeneratedEntry(itemEntry)) { pairsSkipped++; continue; }

                // Dedup: additive pools are per-creature; preserve pools per loot_id.
                var dedupKey = additive ? (creatureGroup.creatureEntry, itemEntry) : (lootId, itemEntry);
                if (request.regenerate)
                {
                    // Re-expand pairs from prior runs (idempotent), but not twice in this run.
                    if (!thisRunPairs.Add(dedupKey)) continue;
                }
                else if (!expandedPairs.Add(dedupKey))
                {
                    pairsSkipped++;
                    continue;
                }

                // ── Variant set: generate once per base item, reuse everywhere ──
                if (!variantCache.TryGetValue(itemEntry, out var cached))
                {
                    var item = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT * FROM item_template
                        WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
                        new { E = itemEntry });

                    if (item == null)
                    {
                        variantCache[itemEntry] = null;
                        continue;
                    }

                    var analysis = AnalyzeItemStats(item);
                    bool hasStats = (int)analysis.totalStats > 0;
                    bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;
                    if (!IsEquippableGear(item))
                    {
                        variantCache[itemEntry] = null;
                        continue;
                    }

                    List<int> entriesList;
                    List<CommitRoll> rollsListFinal;

                    bool regenBase = request.regenerate && await BaseHasVariants(adminConn, itemEntry);
                    if (regenBase)
                    {
                        // Overwrite this base's existing variants in place, by tier.
                        // Cast off dynamic (item/analysis are dynamic → the call is
                        // dynamically dispatched); a static List<VariantData> lets us
                        // pass the VariantToCommitRoll method group to Select (CS1976).
                        List<VariantData> gv = GenerateVariants(item, analysis, ruleset);
                        var newRolls = gv.Select(VariantToCommitRoll).ToList();
                        var rec = await ReconcileBandVariants(mangosConn, adminConn, columns, item,
                            itemEntry, creatureGroup.creatureEntry, ruleset, newRolls, regenRng);
                        entriesList = rec.entries;
                        rollsListFinal = rec.rolls;
                        totalItemsCreated += rec.created;
                        regenReused += rec.reused;
                        regenRemoved += rec.removed;
                        regenRemapped += rec.remapped;

                        if (ruleset.generateLegendary)
                        {
                            try
                            {
                                int? reuse = await GetExistingLegendaryEntry(adminConn, itemEntry);
                                var leg = await BuildLegendaryItem(mangosConn, adminConn, columns,
                                    creatureGroup.creatureEntry, itemEntry, ruleset, reuseEntry: reuse);
                                if (leg.HasValue)
                                {
                                    entriesList = new List<int>(entriesList) { leg.Value.entry };
                                    rollsListFinal = new List<CommitRoll>(rollsListFinal) { leg.Value.roll };
                                    if (!reuse.HasValue) totalItemsCreated++;
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Legendary regen failed for item {itemEntry}: {ex.Message}");
                            }
                        }

                        cached = (entriesList, rollsListFinal.ToArray());
                        variantCache[itemEntry] = cached;
                    }
                    else
                    {
                        var variants = GenerateVariants(item, analysis, ruleset);
                        variantsRequested += LastRequestedVariantCount;
                        if (LastProducedVariantCount < LastRequestedVariantCount)
                        {
                            variantsShort += LastRequestedVariantCount - LastProducedVariantCount;
                            itemsShort++;
                        }

                        int nextId = await GetNextLootifierId(adminConn);
                        var createdEntries = new List<int>();
                        var commitRolls = new CommitRoll[variants.Count];

                        for (int vi = 0; vi < variants.Count; vi++)
                        {
                            int newEntry = nextId++;
                            var roll = VariantToCommitRoll(variants[vi]);
                            commitRolls[vi] = roll;

                            await InsertVariantItemFast(mangosConn, columns, item, newEntry, roll, ruleset);

                            // Attribution: the first creature that needed this item.
                            trackingItemRows.Add((newEntry, itemEntry, creatureGroup.creatureEntry, roll.budgetPct, roll.tierLabel ?? ""));
                            createdEntries.Add(newEntry);
                            totalItemsCreated++;
                        }

                        // ONE legendary per BASE ITEM (not per creature). Built here at
                        // cache time so a shared item (e.g. Searing Blade dropped by many
                        // mobs) gets a single legendary, named after the FIRST creature
                        // that drops it, and reused everywhere. Folded into the cached
                        // entries/rolls so every creature's pool includes the same one.
                        var entriesListLocal = createdEntries;
                        var rollsList = commitRolls.ToList();
                        if (ruleset.generateLegendary)
                        {
                            try
                            {
                                var leg = await BuildLegendaryItem(mangosConn, adminConn, columns,
                                    creatureGroup.creatureEntry, itemEntry, ruleset, trackingItemRows);
                                if (leg.HasValue)
                                {
                                    entriesListLocal = new List<int>(createdEntries) { leg.Value.entry };
                                    rollsList = commitRolls.Append(leg.Value.roll).ToList();
                                    totalItemsCreated++;
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Legendary build failed for item {itemEntry}: {ex.Message}");
                            }
                        }

                        cached = (entriesListLocal, rollsList.ToArray());
                        variantCache[itemEntry] = cached;
                    }
                }

                if (cached == null) continue; // ineligible base item

                if (additive)
                {
                    // Defer: collect this base's variant set for the creature's pool.
                    additiveBatch.Add((itemEntry, cached.Value.entries, cached.Value.rolls));
                }
                else
                {
                    // The cached entries/rolls already include this base item's ONE
                    // legendary (built once at cache time), so every creature that
                    // shares the item reuses the same legendary — no per-creature dup.
                    int lootRows = await ExpandLootTable(mangosConn, adminConn, trackingLootRows,
                        lootId, itemEntry, cached.Value.entries, cached.Value.rolls, creatureGroup.creatureEntry);
                    totalLootRowsCreated += lootRows;
                }
            }

            // Build the single additive pool for this creature (minting a loot_id
            // if the mob had none).
            if (additive && additiveBatch.Count > 0)
            {
                int effLootId = lootId;
                bool minted = false;
                if (effLootId == 0)
                {
                    effLootId = await GetNextLootifierLootId(mangosConn);
                    await mangosConn.ExecuteAsync(
                        "UPDATE creature_template SET loot_id = @L WHERE entry = @E",
                        new { L = effLootId, E = creatureGroup.creatureEntry });
                    minted = true;
                }
                if (effLootId > 0) touchedLootIds.Add(effLootId);
                totalLootRowsCreated += await BuildAdditivePool(mangosConn, adminConn, trackingLootRows,
                    creatureGroup.creatureEntry, effLootId, minted, additiveBatch, poolDropPct);
                additivized.Add(creatureGroup.creatureEntry);
            }

            creaturesProcessed++;

            // Flush tracking rows in batches of 500 to avoid huge SQL strings
            if (trackingItemRows.Count >= 500)
            {
                await FlushTrackingItems(adminConn, trackingItemRows);
                trackingItemRows.Clear();
            }
            if (trackingLootRows.Count >= 500)
            {
                await FlushTrackingLoot(adminConn, trackingLootRows);
                trackingLootRows.Clear();
            }
        }

        // Flush remaining
        if (trackingItemRows.Count > 0) await FlushTrackingItems(adminConn, trackingItemRows);
        if (trackingLootRows.Count > 0) await FlushTrackingLoot(adminConn, trackingLootRows);

        // Post-write safety sweep across every loot table touched by the batch.
        var touchedRefs = await GetReferencedPoolsAsync(mangosConn, touchedLootIds);
        var poolViolations = await FindOverfullGroupsAsync(mangosConn, touchedRefs, touchedLootIds);
        foreach (dynamic v in poolViolations)
            _poolWarnings?.Add($"Loot pool over 100%: {v.scope} entry {v.entry} group {v.groupId} sums to {v.total}%. Items past the 100% mark can never drop — roll back and rebuild this pool.");

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "lootifier_batch_commit",
            TargetType = "lootifier",
            TargetName = $"batch:{creaturesProcessed} creatures",
            StateBefore = "{}",
            StateAfter = JsonSerializer.Serialize(new { totalItemsCreated, totalLootRowsCreated, creaturesProcessed, pairsSkipped, regenReused, regenRemoved, regenRemapped }),
            IsReversible = true,
            RevertKind = Services.RevertKind.Registry,
            Success = true,
            Notes = $"Lootifier batch: {totalItemsCreated} variants + {totalLootRowsCreated} loot rows across {creaturesProcessed} creatures ({pairsSkipped} already-expanded pairs skipped)"
                + (request.regenerate ? $"; regenerate: {regenReused} refreshed in place, {regenRemoved} removed, {regenRemapped} owned copies rerolled" : "")
        });

        return Json(new { success = true, totalItemsCreated, totalLootRowsCreated, creaturesProcessed, pairsSkipped, regenReused, regenRemoved, regenRemapped, variantsRequested, variantsShort, itemsShort, warnings = _poolWarnings, poolViolations });
    }

    /// <summary>
    /// Load (lootId, baseItem) pairs already pooled in previous commits so
    /// re-running a batch never double-pools. The dedup key in BatchCommit is
    /// (lootId, baseItemEntry); both pool-creation markers record exactly that:
    /// 'pool_created' (direct → minted ref, loot_entry=lootId, item_entry=base)
    /// and 'pool_joined' would use the ref entry, so we also seed from the
    /// creature-side pointer marker. We therefore match on the creature-side
    /// rows: 'pool_created' carries (lootId, base) directly.
    /// </summary>
    private async Task<HashSet<(int lootEntry, int itemEntry)>> LoadExpandedPairs(MySqlConnector.MySqlConnection adminConn)
    {
        var set = new HashSet<(int, int)>();
        if (!await TableExists(adminConn, "lootifier_loot_entries")) return set;

        // Direct-drop pools record (lootId, base) on the creature-side marker.
        var direct = await adminConn.QueryAsync<dynamic>(@"
            SELECT DISTINCT loot_entry, item_entry FROM lootifier_loot_entries
            WHERE action_type = 'pool_created' AND loot_table = 'creature_loot_template'");
        foreach (var r in direct)
            set.Add(((int)r.loot_entry, (int)r.item_entry));

        return set;
    }

    /// <summary>Bulk insert tracking items using multi-value INSERT.</summary>
    private async Task FlushTrackingItems(MySqlConnector.MySqlConnection adminConn,
        List<(int genEntry, int baseEntry, int creatureEntry, float budgetPct, string tierName)> rows)
    {
        if (rows.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.Append("INSERT INTO lootifier_generated_items (generated_entry, base_entry, creature_entry, budget_pct, tier_name, created_at) VALUES ");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var r = rows[i];
            sb.Append($"({r.genEntry},{r.baseEntry},{r.creatureEntry},{r.budgetPct:F2},'{MySqlHelper.EscapeString(r.tierName)}',NOW())");
        }
        await adminConn.ExecuteAsync(sb.ToString());
    }

    /// <summary>Bulk insert tracking loot entries using multi-value INSERT.</summary>
    private async Task FlushTrackingLoot(MySqlConnector.MySqlConnection adminConn,
        List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)> rows)
    {
        if (rows.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.Append("INSERT INTO lootifier_loot_entries (creature_entry, loot_table, loot_entry, item_entry, action_type, original_chance, new_chance, created_at) VALUES ");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var r = rows[i];
            sb.Append($"({r.creatureEntry},'{MySqlHelper.EscapeString(r.table)}',{r.lootEntry},{r.itemEntry},'{r.action}',{r.origChance:F4},{r.newChance:F4},NOW())");
        }
        await adminConn.ExecuteAsync(sb.ToString());
    }

    /// <summary>
    /// Write one loot tracking row — buffered (batch path) or immediate (single path).
    /// </summary>
    private async Task TrackLoot(MySqlConnector.MySqlConnection adminConn,
        List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)>? buffer,
        int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)
    {
        if (buffer != null)
        {
            buffer.Add((creatureEntry, table, lootEntry, itemEntry, action, origChance, newChance));
            return;
        }

        await adminConn.ExecuteAsync(@"
            INSERT INTO lootifier_loot_entries
                (creature_entry, loot_table, loot_entry, item_entry, action_type, original_chance, new_chance, created_at)
            VALUES (@CE, @Table, @Entry, @Item, @Action, @OrigChance, @NewChance, NOW())",
            new
            {
                CE = creatureEntry,
                Table = table,
                Entry = lootEntry,
                Item = itemEntry,
                Action = action,
                OrigChance = origChance,
                NewChance = newChance
            });
    }

    // ===================== COMMIT (single source) =====================

    [HttpPost]
    public async Task<IActionResult> Commit([FromBody] CommitRequest request)
    {
        if (request.creatureEntry <= 0)
            return Json(new { success = false, error = "Invalid creature entry" });

        using var batch = AuditBatch.Begin(
            $"ARPG Lootifier — creature {request.creatureEntry}" +
            (request.regenerate ? " (regenerate)" : ""));

        using var mangosConn = _db.Mangos();
        using var adminConn = _db.Admin();

        await EnsureTrackingTables(adminConn);
        _poolWarnings = new List<string>();

        int lootId = await mangosConn.ExecuteScalarAsync<int>(@"
            SELECT loot_id FROM creature_template
            WHERE entry = @E ORDER BY patch DESC LIMIT 1",
            new { E = request.creatureEntry });

        int nextId = await GetNextLootifierId(adminConn);
        int totalItemsCreated = 0;
        int totalLootRowsCreated = 0;
        int regenReused = 0, regenRemoved = 0, regenRemapped = 0;
        var commitLog = new List<object>();
        var ruleset = request.ruleset ?? new RulesetDto();
        var columns = await GetItemColumns(mangosConn);
        var regenRng = new Random();

        // Additive mode: one independent tunable-chance pool for this creature.
        bool additive = ruleset.dropChanceStrategy == "additive";
        float poolDropPct = Math.Clamp(ruleset.poolDropChancePct, 0f, 100f);
        var additiveBatch = new List<(int baseItemEntry, List<int> variantEntries, CommitRoll[] rolls)>();

        if (!additive && lootId == 0)
            return Json(new { success = false, error = "Creature has no loot table" });
        if (additive)
        {
            var already = await LoadAdditivizedCreatures(adminConn);
            if (already.Contains(request.creatureEntry))
                return Json(new { success = false, error = "Creature already has an additive pool — roll it back first to rebuild." });
        }

        foreach (var itemGroup in request.variants)
        {
            if (itemGroup.rolls == null || itemGroup.rolls.Length == 0) continue;
            // Recursion guard — a generated variant can never be a base item.
            if (IsGeneratedEntry(itemGroup.baseItemEntry)) continue;

            var baseItem = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT * FROM item_template
                WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
                new { E = itemGroup.baseItemEntry });

            if (baseItem == null) continue;

            List<int> poolEntries;
            List<CommitRoll> poolRollsList;

            bool regen = request.regenerate && await BaseHasVariants(adminConn, itemGroup.baseItemEntry);
            if (regen)
            {
                // Overwrite existing band variants in place, paired by tier.
                var rec = await ReconcileBandVariants(mangosConn, adminConn, columns, baseItem,
                    itemGroup.baseItemEntry, request.creatureEntry, ruleset, itemGroup.rolls.ToList(), regenRng);
                poolEntries = rec.entries;
                poolRollsList = rec.rolls;
                totalItemsCreated += rec.created;
                regenReused += rec.reused;
                regenRemoved += rec.removed;
                regenRemapped += rec.remapped;
            }
            else
            {
                poolEntries = new List<int>();
                poolRollsList = itemGroup.rolls.ToList();
                foreach (var roll in itemGroup.rolls)
                {
                    int newEntry = nextId++;
                    await InsertVariantItem(mangosConn, baseItem, newEntry, roll, ruleset);

                    await adminConn.ExecuteAsync(@"
                        INSERT INTO lootifier_generated_items
                            (generated_entry, base_entry, creature_entry, budget_pct, tier_name, created_at)
                        VALUES (@GenEntry, @BaseEntry, @CreatureEntry, @BudgetPct, @TierName, NOW())",
                        new
                        {
                            GenEntry = newEntry,
                            BaseEntry = itemGroup.baseItemEntry,
                            CreatureEntry = request.creatureEntry,
                            BudgetPct = roll.budgetPct,
                            TierName = roll.tierLabel ?? ""
                        });

                    poolEntries.Add(newEntry);
                    totalItemsCreated++;
                }
            }

            // Per-item legendary (single mode): one boss-named legendary for THIS
            // base item. On regenerate, overwrite the existing legendary in place.
            if (ruleset.generateLegendary)
            {
                try
                {
                    int? reuse = regen ? await GetExistingLegendaryEntry(adminConn, itemGroup.baseItemEntry) : null;
                    var leg = await BuildLegendaryItem(mangosConn, adminConn, columns,
                        request.creatureEntry, itemGroup.baseItemEntry, ruleset, reuseEntry: reuse);
                    if (leg.HasValue)
                    {
                        poolEntries = new List<int>(poolEntries) { leg.Value.entry };
                        poolRollsList = new List<CommitRoll>(poolRollsList) { leg.Value.roll };
                        if (!reuse.HasValue) totalItemsCreated++;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Legendary build failed for creature {request.creatureEntry} item {itemGroup.baseItemEntry}: {ex.Message}");
                }
            }

            var poolRolls = poolRollsList.ToArray();

            int lootRowsAdded = 0;
            if (additive)
            {
                // Defer to a single per-creature pool after the loop.
                additiveBatch.Add((itemGroup.baseItemEntry, poolEntries, poolRolls));
            }
            else
            {
                // NOTE: re-commit of the same creature+item is idempotent: the pool
                // is reused (prior 'pool_created' → same ref entry), members upserted
                // and renormalized to 100. No compounding, no duplicate pointers.
                lootRowsAdded = await ExpandLootTable(mangosConn, adminConn, null,
                    lootId, itemGroup.baseItemEntry, poolEntries, poolRolls, request.creatureEntry);
                totalLootRowsCreated += lootRowsAdded;
            }

            commitLog.Add(new
            {
                baseItem = itemGroup.baseItemEntry,
                baseName = (string)baseItem.name,
                variantsCreated = poolEntries.Count,
                lootRowsAdded
            });
        }

        // Additive: build the single independent pool now (minting a loot_id if none).
        int additiveEffLootId = 0;
        if (additive && additiveBatch.Count > 0)
        {
            int effLootId = lootId;
            bool minted = false;
            if (effLootId == 0)
            {
                effLootId = await GetNextLootifierLootId(mangosConn);
                await mangosConn.ExecuteAsync(
                    "UPDATE creature_template SET loot_id = @L WHERE entry = @E",
                    new { L = effLootId, E = request.creatureEntry });
                minted = true;
            }
            additiveEffLootId = effLootId;
            totalLootRowsCreated += await BuildAdditivePool(mangosConn, adminConn, null,
                request.creatureEntry, effLootId, minted, additiveBatch, poolDropPct);
        }

        // Post-write safety sweep: confirm no grouped pool this creature points at
        // exceeds 100%. The clamps in JoinExistingPool / CreatePoolFromDirect keep
        // this commit from ever creating an overflow, so anything caught here is a
        // pre-existing bad pool — surfaced as a loud warning, not a silent success.
        var touchedLootIds = new List<int> { lootId };
        if (additiveEffLootId > 0) touchedLootIds.Add(additiveEffLootId);
        var touchedRefs = await GetReferencedPoolsAsync(mangosConn, touchedLootIds);
        var poolViolations = await FindOverfullGroupsAsync(mangosConn, touchedRefs, touchedLootIds);
        foreach (dynamic v in poolViolations)
            _poolWarnings?.Add($"Loot pool over 100%: {v.scope} entry {v.entry} group {v.groupId} sums to {v.total}%. Items past the 100% mark can never drop — roll back and rebuild this pool.");

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "lootifier_commit",
            TargetType = "lootifier",
            TargetName = $"creature:{request.creatureEntry}",
            StateBefore = "{}",
            StateAfter = JsonSerializer.Serialize(new { totalItemsCreated, totalLootRowsCreated, regenReused, regenRemoved, regenRemapped, commitLog }),
            IsReversible = true,
            RevertKind = Services.RevertKind.Registry,
            Success = true,
            Notes = $"Lootifier: {totalItemsCreated} variants + {totalLootRowsCreated} loot rows for creature {request.creatureEntry}"
                + (request.regenerate ? $" (regenerate: {regenReused} refreshed in place, {regenRemoved} removed, {regenRemapped} owned copies rerolled)" : "")
        });

        return Json(new { success = true, totalItemsCreated, totalLootRowsCreated, regenReused, regenRemoved, regenRemapped, details = commitLog, warnings = _poolWarnings, poolViolations });
    }

    /// <summary>
    /// GET /Lootifier/ValidatePools?creatureEntry=NNN — read-only pool-health
    /// check. Reports any grouped loot pool the creature points at (or its own
    /// loot table) whose chances sum above 100%. Lets the UI flag a bad table
    /// before/after committing without changing anything.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ValidatePools(int creatureEntry)
    {
        if (creatureEntry <= 0)
            return Json(new { success = false, error = "Invalid creature entry" });

        using var mangosConn = _db.Mangos();
        int lootId = await mangosConn.ExecuteScalarAsync<int>(@"
            SELECT loot_id FROM creature_template
            WHERE entry = @E ORDER BY patch DESC LIMIT 1",
            new { E = creatureEntry });

        if (lootId == 0)
            return Json(new { success = true, creatureEntry, ok = true, violations = Array.Empty<object>(), note = "Creature has no loot table" });

        var lootIds = new[] { lootId };
        var refs = await GetReferencedPoolsAsync(mangosConn, lootIds);
        var violations = await FindOverfullGroupsAsync(mangosConn, refs, lootIds);

        return Json(new { success = true, creatureEntry, lootId, ok = violations.Count == 0, violations });
    }

    // ===================== ROLLBACK =====================

    [HttpPost]
    public async Task<IActionResult> Rollback([FromBody] RollbackRequest request)
    {
        using var batch = AuditBatch.Begin(
            "ARPG Lootifier rollback — " +
            (request.creatureEntry > 0 ? $"creature {request.creatureEntry}" : "all creatures"));

        using var mangosConn = _db.Mangos();
        using var adminConn = _db.Admin();

        if (!await TableExists(adminConn, "lootifier_generated_items"))
            return Json(new { success = false, error = "No lootifier data found" });

        // ── SCOPE GUARD ──
        // All three lootifiers share lootifier_generated_items and are told
        // apart by the creature_entry sentinel:
        //     > 0  ARPG   (a real creature entry)
        //     = 0  Quest  (QuestLootifier)
        //     = -1 Crafting (CraftingLootifier)
        // "Rollback All" used to be WHERE 1=1, which deleted the quest and
        // crafting variants (and their tracking rows) along with the ARPG ones.
        // Scoped to creature_entry > 0 so this endpoint only ever unwinds its
        // own work; quest/crafting have their own rollback endpoints.
        string where = request.creatureEntry > 0
            ? "WHERE creature_entry = @CE"
            : "WHERE creature_entry > 0";

        var generatedItems = (await adminConn.QueryAsync<dynamic>(
            $"SELECT generated_entry, base_entry, creature_entry FROM lootifier_generated_items {where}",
            new { CE = request.creatureEntry })).ToList();

        var lootEntries = (await adminConn.QueryAsync<dynamic>(
            $"SELECT id, creature_entry, loot_table, loot_entry, item_entry, action_type, original_chance, new_chance FROM lootifier_loot_entries {where}",
            new { CE = request.creatureEntry })).ToList();

        int itemsRemoved = 0, lootRowsFixed = 0, itemsKept = 0;

        // Never orphan: variants actually deleted below get their player-owned
        // copies repointed back at the plain base item. Grouped by base so one
        // pass over item_instance covers each base's whole variant set.
        var deletedByBase = new Dictionary<int, List<int>>();

        // Variant membership in a loot table is recorded as pool_member (direct
        // mint) or pool_joined (ref join). Both mean "this item is referenced".
        foreach (var gi in generatedItems)
        {
            int genEntry = (int)gi.generated_entry;

            // Per-creature rollback: keep the item_template row if another
            // creature's pool still points at it (shared variant sets).
            if (request.creatureEntry > 0)
            {
                int otherRefs = await adminConn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM lootifier_loot_entries
                    WHERE item_entry = @E AND action_type IN ('pool_member','pool_joined','add_member')
                      AND creature_entry <> @CE",
                    new { E = genEntry, CE = request.creatureEntry });

                if (otherRefs > 0)
                {
                    itemsKept++;
                    continue;
                }
            }

            await mangosConn.ExecuteAsync("DELETE FROM item_template WHERE entry = @E",
                new { E = genEntry });
            itemsRemoved++;

            int baseEntry = (int)gi.base_entry;
            if (!deletedByBase.TryGetValue(baseEntry, out var lst)) deletedByBase[baseEntry] = lst = new List<int>();
            lst.Add(genEntry);
        }

        // Never orphan: repoint every player-owned copy of a deleted variant back
        // to its plain base item (empty new-pool => fallback to base).
        int ownedRemapped = 0;
        var rerollRng = new Random();
        foreach (var kv in deletedByBase)
            ownedRemapped += await RemapOwnedVariants(mangosConn, kv.Value, new List<int>(), kv.Key, rerollRng);

        // Restore loot in dependency order:
        //   1. delete variant member rows (pool_member / pool_joined)
        //   2. for pool_created: drop the minted pointer, delete the whole
        //      minted ref group, and restore the base's original direct row.
        // The creature-side 'pool_created' breadcrumb for a ref-JOIN pool has
        // original_chance = -1 (sentinel): no direct row to restore, and the
        // ref group is shared vanilla data, so we leave it — only the variant
        // members (deleted in step 1) came from us.
        foreach (var le in lootEntries)
        {
            string table = (string)le.loot_table;
            string action = (string)le.action_type;
            int lootEntry = (int)le.loot_entry;
            int itemEntry = (int)le.item_entry;

            if (action == "pool_member" || action == "pool_joined" || action == "add_member")
            {
                await mangosConn.ExecuteAsync(
                    $"DELETE FROM `{table}` WHERE entry = @Entry AND item = @Item",
                    new { Entry = lootEntry, Item = itemEntry });
                lootRowsFixed++;
            }
        }

        // Restore base items whose share we split when joining an existing pool.
        // original_chance holds the true pre-lootify share (from og_ baseline).
        // Deduplicate: a base may have a pool_base row per creature that touched
        // it — restore once to the recorded original, not cumulatively.
        var restoredBases = new HashSet<(int, int)>();
        foreach (var le in lootEntries)
        {
            if ((string)le.action_type != "pool_base") continue;
            int refEntry = (int)le.loot_entry;
            int baseItem = (int)le.item_entry;
            if (!restoredBases.Add((refEntry, baseItem))) continue;

            float origShare = (float)le.original_chance;
            await mangosConn.ExecuteAsync(
                "UPDATE reference_loot_template SET ChanceOrQuestChance = @Chance WHERE entry = @Entry AND item = @Item",
                new { Chance = origShare, Entry = refEntry, Item = baseItem });
            lootRowsFixed++;
        }

        foreach (var le in lootEntries)
        {
            if ((string)le.action_type != "pool_created") continue;
            if ((string)le.loot_table != "creature_loot_template") continue;

            int lootId = (int)le.loot_entry;         // creature loot_id
            int baseItem = (int)le.item_entry;        // base item entry
            float origChance = (float)le.original_chance;
            // Fetch the pointer to learn the ref entry it points at.
            var ptr = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT mincountOrRef, maxcount, patch_min, patch_max FROM creature_loot_template
                WHERE entry = @LootId AND item = @Item AND mincountOrRef < 0",
                new { LootId = lootId, Item = baseItem });

            if (origChance < 0f)
            {
                // Ref-JOIN breadcrumb: nothing minted, shared ref stays. Done.
                lootRowsFixed++;
                continue;
            }

            // Direct-mint pool: remove pointer, drop minted ref group, restore base.
            if (ptr != null)
            {
                int refEntry = Math.Abs((int)ptr.mincountOrRef);

                await mangosConn.ExecuteAsync(
                    "DELETE FROM creature_loot_template WHERE entry = @LootId AND item = @Item AND mincountOrRef < 0",
                    new { LootId = lootId, Item = baseItem });

                // Only delete the minted ref if it's in our reserved range.
                if (refEntry >= LOOTIFIER_REF_START)
                    await mangosConn.ExecuteAsync(
                        "DELETE FROM reference_loot_template WHERE entry = @Ref",
                        new { Ref = refEntry });

                await mangosConn.ExecuteAsync(@"
                    INSERT IGNORE INTO creature_loot_template (entry, item, ChanceOrQuestChance, groupid, mincountOrRef, maxcount, patch_min, patch_max)
                    VALUES (@LootId, @Item, @Chance, 0, 1, @MaxCount, @PMin, @PMax)",
                    new
                    {
                        LootId = lootId,
                        Item = baseItem,
                        Chance = origChance,
                        MaxCount = ptr.maxcount != null ? (int)ptr.maxcount : 1,
                        PMin = ptr.patch_min != null ? (int)ptr.patch_min : 0,
                        PMax = ptr.patch_max != null ? (int)ptr.patch_max : 10
                    });
            }
            lootRowsFixed++;
        }

        // ── Additive pool rollback ──
        // 1. Remove the independent pointer and drop the whole minted ref pool.
        foreach (var le in lootEntries)
        {
            if ((string)le.action_type != "add_ptr") continue;
            int lootId = (int)le.loot_entry;
            int pointerItem = (int)le.item_entry;
            var ptr = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT mincountOrRef FROM creature_loot_template
                WHERE entry = @LootId AND item = @Item AND mincountOrRef < 0",
                new { LootId = lootId, Item = pointerItem });
            await mangosConn.ExecuteAsync(
                "DELETE FROM creature_loot_template WHERE entry = @LootId AND item = @Item AND mincountOrRef < 0",
                new { LootId = lootId, Item = pointerItem });
            if (ptr != null)
            {
                int refEntry = Math.Abs((int)ptr.mincountOrRef);
                if (refEntry >= LOOTIFIER_REF_START)
                    await mangosConn.ExecuteAsync(
                        "DELETE FROM reference_loot_template WHERE entry = @Ref",
                        new { Ref = refEntry });
            }
            lootRowsFixed++;
        }

        // 2. Restore each base's original direct drop row (if it had one before).
        foreach (var le in lootEntries)
        {
            if ((string)le.action_type != "add_base") continue;
            float origChance = (float)le.original_chance;
            if (origChance <= 0f) continue;   // base had no direct row to restore
            int lootId = (int)le.loot_entry;
            int baseItem = (int)le.item_entry;
            await mangosConn.ExecuteAsync(@"
                INSERT IGNORE INTO creature_loot_template (entry, item, ChanceOrQuestChance, groupid, mincountOrRef, maxcount, patch_min, patch_max)
                VALUES (@LootId, @Item, @Chance, 0, 1, 1, 0, 10)",
                new { LootId = lootId, Item = baseItem, Chance = origChance });
            lootRowsFixed++;
        }

        // 3. Zero any loot_id we minted for a no-loot mob (restore it to 0).
        foreach (var le in lootEntries)
        {
            if ((string)le.action_type != "add_lootid") continue;
            int mintedLootId = (int)le.loot_entry;
            int ce = (int)le.creature_entry;
            await mangosConn.ExecuteAsync(
                "UPDATE creature_template SET loot_id = 0 WHERE entry = @CE AND loot_id = @L",
                new { CE = ce, L = mintedLootId });
            lootRowsFixed++;
        }

        await adminConn.ExecuteAsync($"DELETE FROM lootifier_generated_items {where}", new { CE = request.creatureEntry });
        await adminConn.ExecuteAsync($"DELETE FROM lootifier_loot_entries {where}", new { CE = request.creatureEntry });

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "lootifier_rollback",
            TargetType = "lootifier",
            TargetName = request.creatureEntry > 0 ? $"creature:{request.creatureEntry}" : "all",
            StateBefore = JsonSerializer.Serialize(new { itemsRemoved, itemsKept, lootRowsFixed, ownedRemapped }),
            StateAfter = "{}",
            IsReversible = false,
            Success = true,
            Notes = $"Lootifier rollback: {itemsRemoved} items removed ({itemsKept} kept — shared), {lootRowsFixed} loot entries restored, {ownedRemapped} player-owned copies reverted to base"
        });

        return Json(new { success = true, itemsRemoved, itemsKept, lootRowsFixed, ownedRemapped });
    }

    // ===================== STATUS =====================

    [HttpGet]
    public async Task<IActionResult> Status()
    {
        using var adminConn = _db.Admin();

        if (!await TableExists(adminConn, "lootifier_generated_items"))
            return Json(new { active = false, totalItems = 0, creatures = Array.Empty<object>() });

        // creature_entry > 0 = ARPG only (0 = quest, -1 = crafting — see Rollback).
        var totalItems = await adminConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM lootifier_generated_items WHERE creature_entry > 0");
        var creatures = await adminConn.QueryAsync<dynamic>(@"
            SELECT creature_entry AS creatureEntry, COUNT(*) AS variantCount, MIN(created_at) AS firstCreated
            FROM lootifier_generated_items
            WHERE creature_entry > 0
            GROUP BY creature_entry ORDER BY creature_entry");

        return Json(new { active = totalItems > 0, totalItems, creatures });
    }

    // ══════════════════════════════════════════════════════════════
    //  VARIANT GENERATION ENGINE (v3 — tier-quota + prefix/suffix + spell-effect items)
    // ══════════════════════════════════════════════════════════════

    private List<VariantData> GenerateVariants(dynamic baseItem, dynamic analysis, RulesetDto ruleset)
    {
        var rng = new Random();

        float baseBudget = (float)analysis.weightedBudget;
        int[] presentTypes = (int[])analysis.presentStatTypes;
        string family = (string)analysis.detectedFamily;
        bool hasStats = (int)analysis.totalStats > 0;
        bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;
        int baseQuality = GetPropInt(baseItem, "quality");

        int numVariants = Math.Clamp(ruleset.variantsPerItem, 1, 50);
        var tiers = GetRequiredTiers(ruleset);

        // Any stat-less base (plain white gear, or a spell-only item) has no stat
        // budget to build on, so derive one from item_level. Previously this only
        // fired for spell items, which left a stat-less white equippable at budget
        // 0 — now that eligibility is "equippable, full stop", whites hit this too.
        if (!hasStats)
        {
            int itemLevel = GetPropInt(baseItem, "item_level");
            baseBudget = EstimateBudgetFromItemLevel(itemLevel);
        }

        // ── How strong a variant rolls ──
        // Boost % per tier is THE control, exactly like the Crafting Lootifier:
        // a tier with Boost 30-40 rolls 30-40% above the base item's weighted
        // budget. Nothing global caps it.
        //
        // budgetCeilingPct survives ONLY as a fallback for a tier that has no Boost
        // set — an old saved ruleset, or an API caller still posting the pre-Boost
        // shape. Such a tier keeps the legacy meaning of minPct/maxPct: a percentile
        // slice of the [floor, base+ceiling] headroom. Nothing in the UI produces
        // that shape any more.
        //
        // maxBudget is the top of the whole ladder and must cover every tier, so
        // budgetPct (which feeds quality, gold, DPS and pool rarity) stays a
        // meaningful 0-100 position on it.
        float ceilingBoost = 0f;
        bool anyBoost = false;
        foreach (var t in tiers)
            if (t.HasBoost)
            {
                anyBoost = true;
                ceilingBoost = Math.Max(ceilingBoost, Math.Max(t.minBoostPct!.Value, t.maxBoostPct!.Value));
            }
        // Legacy tiers still need the global ceiling; boost-only rulesets ignore it.
        if (!anyBoost || tiers.Any(t => !t.HasBoost))
            ceilingBoost = Math.Max(ceilingBoost, ruleset.budgetCeilingPct);

        float maxBudget = baseBudget * (1 + ceilingBoost / 100f);
        // Anchor the variant budget floor a hair above base (1.02×) so even the
        // lowest roll is a mild upgrade rather than a below-base sidegrade. Tiers
        // subdivide the headroom [floor, maxBudget] rather than [0, maxBudget].
        float floorBudget = Math.Min(baseBudget * 1.02f, maxBudget);
        float budgetSpan = Math.Max(0f, maxBudget - floorBudget);

        // ── Phase 1: Allocate variant slots per tier (proportional to tier width) ──
        var tierAllocations = AllocateTierSlots(tiers, numVariants);

        // ── Phase 2: Generate variants per tier ──
        var eligible = new HashSet<int>(presentTypes);
        if (ruleset.allowNewAffixes)
        {
            var familyStats = STAT_FAMILIES.GetValueOrDefault(family, STAT_FAMILIES["hybrid"]);
            foreach (var s in familyStats) eligible.Add(s);
        }
        var eligibleList = eligible.ToList();

        // For spell-only items, seed eligible with family-appropriate stats
        if (!hasStats && hasSpellEffects)
        {
            eligibleList = STAT_FAMILIES["hybrid"].ToList();
        }

        var baseFingerprint = hasStats ? BuildFingerprint(analysis) : "";
        var fingerprints = new HashSet<string>();
        fingerprints.Add(baseFingerprint);

        var variants = new List<VariantData>();

        for (int tierIdx = 0; tierIdx < tiers.Count; tierIdx++)
        {
            var tier = tiers[tierIdx];
            int slotsForTier = tierAllocations[tierIdx];

            for (int s = 0; s < slotsForTier; s++)
            {
                // Roll budget within this tier's range.
                //   boost set  -> absolute: base x (1 + boost%), floored at floorBudget
                //                 so a 0% tier is still a hair above base, not below it
                //   boost blank-> legacy: percentile position inside [floor, ceiling]
                float tierMinBudget, tierMaxBudget;
                if (tier.HasBoost)
                {
                    float lo = Math.Min(tier.minBoostPct!.Value, tier.maxBoostPct!.Value);
                    float hi = Math.Max(tier.minBoostPct!.Value, tier.maxBoostPct!.Value);
                    tierMinBudget = Math.Max(floorBudget, baseBudget * (1f + lo / 100f));
                    tierMaxBudget = Math.Max(tierMinBudget, baseBudget * (1f + hi / 100f));
                }
                else
                {
                    tierMinBudget = floorBudget + budgetSpan * (tier.minPct / 100f);
                    tierMaxBudget = floorBudget + budgetSpan * (Math.Min(tier.maxPct, 100f) / 100f);
                }
                float budgetRoll = tierMinBudget + (float)rng.NextDouble() * (tierMaxBudget - tierMinBudget);
                float budgetPct = maxBudget > 0 ? (budgetRoll / maxBudget) * 100f : 0;

                // Ordered floor: tier N is guaranteed at least MIN_TIER_STEP more
                // budget than tier N-1, so a weak base still yields a real ladder
                // instead of every tier flooring to the same MIN_DELTA_BUDGET.
                float tierMinDelta = MIN_DELTA_BUDGET + tierIdx * MIN_TIER_STEP;

                List<StatRoll> stats;
                if (hasStats)
                {
                    stats = RollStats(rng, budgetRoll, presentTypes, eligibleList, analysis, ruleset, tierMinDelta);
                }
                else
                {
                    // Spell-effect-only item: add bonus stats based on tier budget
                    stats = RollStatsForSpellItem(rng, budgetRoll, eligibleList, family);
                }

                string canon = CanonicalTier(tier.label, budgetPct);
                var (tierLabel, tierPosition) = ResolveBandNaming(baseQuality, canon, tier.label, tier.position);
                string baseName = (string)baseItem.name;
                string name = ApplyTierName(baseName, tierLabel, tierPosition);

                var candidate = new VariantData
                {
                    name = name,
                    budgetPct = budgetPct,
                    tierLabel = tierLabel,
                    tierPosition = tierPosition,
                    stats = stats
                };

                var fp = BuildVariantFingerprint(candidate);
                if (fingerprints.Contains(fp))
                {
                    // Retry within same tier (up to 10 attempts)
                    bool found = false;
                    for (int retry = 0; retry < 10; retry++)
                    {
                        budgetRoll = tierMinBudget + (float)rng.NextDouble() * (tierMaxBudget - tierMinBudget);
                        budgetPct = maxBudget > 0 ? (budgetRoll / maxBudget) * 100f : 0;

                        stats = hasStats
                            ? RollStats(rng, budgetRoll, presentTypes, eligibleList, analysis, ruleset, tierMinDelta)
                            : RollStatsForSpellItem(rng, budgetRoll, eligibleList, family);

                        candidate = new VariantData
                        {
                            name = name,
                            budgetPct = budgetPct,
                            tierLabel = tierLabel,
                            tierPosition = tierPosition,
                            stats = stats
                        };
                        fp = BuildVariantFingerprint(candidate);
                        if (!fingerprints.Contains(fp)) { found = true; break; }
                    }
                    if (!found) continue; // skip this slot if truly stuck
                }

                fingerprints.Add(fp);
                variants.Add(candidate);
            }
        }

        // Requested vs produced. The fingerprint dedup drops a slot when the roll
        // space is exhausted (a very weak base has few distinct integer stat
        // allocations), so callers can report the gap rather than silently
        // shipping fewer variants than the operator asked for.
        LastRequestedVariantCount = tierAllocations.Sum();
        LastProducedVariantCount = variants.Count;
        return variants.OrderBy(v => v.budgetPct).ToList();
    }

    /// <summary>Allocate variant slots across tiers with generous upper-tier representation.</summary>
    private int[] AllocateTierSlots(List<TierRange> tiers, int totalVariants)
    {
        int n = tiers.Count;
        var allocations = new int[n];
        if (n == 0) return allocations;

        // Explicit per-tier slots (>0) are honored verbatim; tiers left at 0 ("auto")
        // share whatever's left of totalVariants using the upper-tier-weighted default.
        int explicitSum = 0;
        var auto = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (tiers[i].slots > 0) { allocations[i] = tiers[i].slots; explicitSum += tiers[i].slots; }
            else auto.Add(i);
        }

        if (auto.Count == 0) return allocations;                 // fully manual

        int remaining = Math.Max(0, totalVariants - explicitSum);
        if (auto.Count == 1) { allocations[auto[0]] = Math.Max(explicitSum > 0 ? 0 : 1, remaining); return allocations; }

        // Auto tiers ordered low→high budget: the lowest is the common bucket and
        // takes the rest; higher auto tiers get a small guaranteed slice.
        var ordered = auto.OrderBy(i => tiers[i].minPct).ToList();
        int upperSlots = 0;
        for (int k = 1; k < ordered.Count; k++)
        {
            int idx = ordered[k];
            bool isTopTier = (k == ordered.Count - 1);
            allocations[idx] = isTopTier ? 1 : 2;
            upperSlots += allocations[idx];
        }
        allocations[ordered[0]] = Math.Max(1, remaining - upperSlots);

        // Scale down if the guaranteed minimums overshot a small `remaining`.
        int autoTotal = auto.Sum(i => allocations[i]);
        while (autoTotal > remaining && remaining >= auto.Count)
        {
            int maxIdx = ordered[0];
            foreach (int i in auto) if (allocations[i] > allocations[maxIdx]) maxIdx = i;
            if (allocations[maxIdx] > 1) { allocations[maxIdx]--; autoTotal--; }
            else break;
        }

        return allocations;
    }

    /// <summary>Estimate a stat budget from item_level for spell-effect-only items.</summary>
    private float EstimateBudgetFromItemLevel(int itemLevel)
    {
        // Rough approximation: vanilla items scale roughly linearly
        // A level 60 epic (ilvl ~66-83) typically has ~40-80 total weighted budget
        // A level 60 rare (ilvl ~52-63) typically has ~25-50
        // Simple linear: budget ≈ itemLevel * 0.7
        return Math.Max(5f, itemLevel * 0.7f);
    }

    /// <summary>Roll bonus stats for a spell-effect-only item.</summary>
    private List<StatRoll> RollStatsForSpellItem(Random rng, float budgetRoll, List<int> eligibleList, string family)
    {
        // Spell-effect items get 1-3 bonus stat slots (scaled down — the spell IS the main value)
        // Budget is reduced to 40% since the spell effect is the primary value
        float statBudget = budgetRoll * 0.40f;

        int slotCount = statBudget < 10 ? 1 : (statBudget < 25 ? 2 : 3);
        var chosenTypes = eligibleList.OrderBy(_ => rng.Next()).Take(slotCount).ToList();

        var weights = chosenTypes.Select(t => DEFAULT_STAT_WEIGHTS.GetValueOrDefault(t, 1.0f)).ToArray();
        float totalWeight = weights.Sum();
        var rolledStats = new List<StatRoll>();

        float remaining = statBudget;
        for (int s = 0; s < chosenTypes.Count; s++)
        {
            float share;
            if (s == chosenTypes.Count - 1)
                share = remaining;
            else
            {
                float basePortion = statBudget * (weights[s] / totalWeight);
                float jitter = (float)(rng.NextDouble() * 0.2 - 0.1) * basePortion;
                share = Math.Max(1, basePortion + jitter);
            }

            int statValue = Math.Max(1, (int)Math.Round(share / weights[s]));
            float actualCost = statValue * weights[s];
            remaining -= actualCost;

            rolledStats.Add(new StatRoll
            {
                statType = chosenTypes[s],
                statValue = statValue,
                name = STAT_NAMES.GetValueOrDefault(chosenTypes[s], $"Type{chosenTypes[s]}")
            });
        }

        return rolledStats;
    }

    /// <summary>Apply tier name as prefix or suffix.</summary>
    private string ApplyTierName(string baseName, string tierLabel, string tierPosition)
    {
        if (string.IsNullOrEmpty(tierLabel)) return baseName;

        if (tierPosition == "prefix")
            return tierLabel + " " + baseName;
        else
            return baseName + " " + tierLabel;
    }

    // ── Eligibility: equippable Weapon/Armor gear only ──
    // A Use:/spell effect alone (recipes, potions, scrolls, trinket-charms that
    // aren't equippable, etc.) does NOT make something lootifiable — it must be a
    // Weapon/Armor class item that occupies an inventory slot.
    private bool IsEquippableGear(dynamic item)
    {
        int cls = GetPropInt(item, "class");
        int inv = GetPropInt(item, "inventory_type");
        return (cls == ITEM_CLASS_WEAPON || cls == ITEM_CLASS_ARMOR) && inv > 0;
    }

    // ── Colour ladder (shared with Quest/Crafting Lootifiers) ──
    // Canonical tier token for a naming label, by name first then boost bucket.
    // Recognizes the high-rarity replacement labels so their colour resolves right.
    private static string CanonicalTier(string label, float budgetPct)
    {
        var l = (label ?? "").ToLowerInvariant();
        if (l.Contains("god") || l.Contains("legend") || l.Contains("immortal") || l.Contains("azeroth")) return "gods";
        if (l.Contains("glory") || l.Contains("fury")) return "glory";
        if (l.Contains("power")) return "power";
        if (l.Contains("improv")) return "improved";
        // Numeric fallback for a CUSTOM band name only (nothing matched above).
        // budgetPct is this variant's position on the ladder, 0-100, where 100 is
        // the strongest tier's ceiling — so the buckets are relative, not absolute.
        // Name your bands Improved/Power/Glory/Gods and this never runs.
        if (budgetPct >= 90f) return "gods";
        if (budgetPct >= 70f) return "glory";
        if (budgetPct >= 45f) return "power";
        return "improved";
    }

    // Variant colour anchored at the BASE quality (relative tier ladder, matching
    // the Crafting Lootifier). Improved/Power keep the base colour; of Glory is +1
    // (capped at purple); of the Gods is +2 (legendary). A HARD FLOOR at the base
    // quality guarantees a variant is never a lower rarity than the item it
    // replaces. White floors to green. Per base:
    //   white     → green / green / blue  / purple
    //   green     → green / green / blue  / purple
    //   blue      → blue  / blue  / purple/ orange
    //   purple    → purple/ purple/ purple/ orange
    //   legendary → orange/ orange/ orange/ orange
    // Relative colour ladder: each rung is one quality step above the rung below,
    // so a green base reads green / blue / purple / legendary instead of collapsing
    // the first two rungs onto the base colour (which gave 7 greens + 2 blues on a
    // 5/2/2 split when the operator expected 5 / 2 / 2).
    //   improved -> base      power -> +1      glory -> +2 (capped purple)
    //   gods/legendary -> 5
    private static int VariantQuality(string tier, int baseQuality)
    {
        int b = baseQuality <= 1 ? 2 : baseQuality;      // white + stats floors at green
        int q;
        if (tier == "gods") q = 5;                       // legendary, always orange
        else if (tier == "glory") q = Math.Min(b + 2, 4);// +2, capped at purple
        else if (tier == "power") q = Math.Min(b + 1, 4);// +1, capped at purple
        else q = b;                                      // improved keeps the base colour
        return Math.Clamp(Math.Max(b, q), 2, 5);         // never below the base's own quality
    }

    // Per-base-quality band naming (shared with Quest/Crafting). A purple or
    // legendary base can never carry an "Improved"/"of Power" name:
    //   Purple base : Improved/of Power → "of Fury"   of Glory → "of Glory"
    //                 of the Gods       → "of Azeroth" (the purple→legendary step)
    //   Legendary   : every tier        → "Immortal" (prefix) — can only stay legendary
    // Green/blue/white keep the configured labels.
    private static (string label, string position) ResolveBandNaming(int baseQuality, string canonicalTier,
        string defaultLabel, string defaultPosition)
    {
        if (baseQuality >= 5)
            return ("Immortal", "prefix");

        if (baseQuality == 4)
        {
            return canonicalTier switch
            {
                "improved" => ("of Fury", "suffix"),
                "power" => ("of Fury", "suffix"),
                "glory" => ("of Glory", "suffix"),
                "gods" => ("of Azeroth", "suffix"),
                _ => (defaultLabel, defaultPosition)
            };
        }

        return (defaultLabel, defaultPosition);
    }

    /// <summary>Converts VariantData list to anonymous objects for JSON serialization.</summary>
    private List<object> VariantsToJson(List<VariantData> variants,
        bool isWeapon = false, float baseDps = 0f, RulesetDto? ruleset = null)
    {
        return variants.Select((v, idx) =>
        {
            float? dpsBump = null;
            double? dps = null;
            if (isWeapon)
            {
                float b = ResolveTierDpsBump(ruleset?.namingTiers, v.tierLabel, v.budgetPct) ?? 0f;
                dpsBump = b;
                dps = Math.Round(baseDps * (1.0 + b / 100.0), 1);
            }
            return (object)new
            {
                variantIndex = idx,
                name = v.name,
                budgetPct = Math.Round(v.budgetPct, 1),
                tierLabel = v.tierLabel,
                tierPosition = v.tierPosition,
                dpsBumpPct = dpsBump,
                dps,
                stats = v.stats.Select(s => (object)new { s.statType, s.statValue, s.name }).ToList()
            };
        }).ToList();
    }

    /// <summary>Converts VariantData to CommitRoll for DB writes.</summary>
    private CommitRoll VariantToCommitRoll(VariantData v)
    {
        return new CommitRoll
        {
            budgetPct = v.budgetPct,
            tierLabel = v.tierLabel ?? "",
            tierPosition = v.tierPosition ?? "suffix",
            stats = v.stats.Select(s => new CommitStat
            {
                statType = s.statType,
                statValue = s.statValue
            }).ToArray()
        };
    }

    // ADDITIVE ONLY (matches Quest/Crafting Lootifiers). Preserve every base stat
    // line verbatim, then layer a bonus on top — never reduced, never dropped. The
    // bonus is this roll's budget above the base budget (budgetRoll floors at
    // base×1.02 in GenerateVariants; legendary uses base×1.5), floored at
    // MIN_DELTA_BUDGET so even the lowest tier is a real upgrade. The bonus is
    // split between bumping existing lines and adding new affixes.
    private List<StatRoll> RollStats(Random rng, float budgetRoll, int[] presentTypes,
        List<int> eligibleList, dynamic analysis, RulesetDto ruleset, float minDelta = MIN_DELTA_BUDGET)
    {
        float baseWeighted = (float)analysis.weightedBudget;

        // Seed the variant with the base item's exact stat lines (preserved).
        var lines = new Dictionary<int, int>();
        foreach (var s in (List<object>)analysis.stats)
        {
            int st = (int)((dynamic)s).statType;
            int sv = (int)((dynamic)s).statValue;
            if (st > 0 && sv != 0) lines[st] = sv;
        }

        float delta = Math.Max(minDelta, budgetRoll - baseWeighted);

        int slotRoom = Math.Max(0, 10 - lines.Count);
        var newCandidates = eligibleList.Where(t => !lines.ContainsKey(t)).ToList();
        bool canAddNew = ruleset.allowNewAffixes && slotRoom > 0 && newCandidates.Count > 0;

        float split = (float)Math.Clamp(LOOT_BUMP_BIAS + (rng.NextDouble() * 0.4 - 0.2), 0.0, 1.0);
        if (lines.Count == 0) split = 0f;   // nothing to bump → all bonus to new affixes
        if (!canAddNew) split = 1f;         // no room/permission for new affixes → all bumps

        float existingPortion = delta * split;
        float newPortion = delta - existingPortion;

        // Bump the preserved lines (weight-distributed).
        if (existingPortion > 0f && lines.Count > 0)
        {
            var keys = lines.Keys.ToList();
            float totalW = keys.Sum(k => DEFAULT_STAT_WEIGHTS.GetValueOrDefault(k, 1.0f));
            foreach (var k in keys)
            {
                float w = DEFAULT_STAT_WEIGHTS.GetValueOrDefault(k, 1.0f);
                float share = existingPortion * (w / totalW);
                int add = (int)Math.Round(share / w, MidpointRounding.AwayFromZero);
                if (add > 0) lines[k] += add;
            }
        }

        // Add new affixes with the remaining bonus, or (if none allowed) pour the
        // whole delta back into existing lines so the tier budget still lands.
        if (canAddNew && newPortion > 0f)
        {
            // "hybrid" always seeds the candidate pool alongside the detected family.
            // A physical item's family is Agi/Str/Sta — with two of those already on
            // the base there was exactly ONE new affix to pick from, so every roll
            // produced the same stat line and the dedup threw the rest away.
            if (newCandidates.Count < 2)
            {
                foreach (var t in STAT_FAMILIES["hybrid"])
                    if (!lines.ContainsKey(t) && !newCandidates.Contains(t)) newCandidates.Add(t);
            }
            int maxNew = Math.Min(Math.Max(1, ruleset.maxAffixCountChange), Math.Min(slotRoom, newCandidates.Count));
            int newCount = 1 + rng.Next(maxNew);
            var picks = newCandidates.OrderBy(_ => rng.Next()).Take(newCount).ToList();

            float totalW = picks.Sum(k => DEFAULT_STAT_WEIGHTS.GetValueOrDefault(k, 1.0f));
            foreach (var k in picks)
            {
                float w = DEFAULT_STAT_WEIGHTS.GetValueOrDefault(k, 1.0f);
                float share = newPortion * (w / totalW);
                int val = Math.Max(1, (int)Math.Round(share / w, MidpointRounding.AwayFromZero));
                lines[k] = lines.GetValueOrDefault(k, 0) + val;
            }
        }
        else if (existingPortion < delta && lines.Count > 0)
        {
            var keys = lines.Keys.ToList();
            float totalW = keys.Sum(k => DEFAULT_STAT_WEIGHTS.GetValueOrDefault(k, 1.0f));
            float leftover = delta - existingPortion;
            foreach (var k in keys)
            {
                float w = DEFAULT_STAT_WEIGHTS.GetValueOrDefault(k, 1.0f);
                int add = (int)Math.Round(leftover * (w / totalW) / w, MidpointRounding.AwayFromZero);
                if (add > 0) lines[k] += add;
            }
        }

        return lines.Select(kv => new StatRoll
        {
            statType = kv.Key,
            statValue = kv.Value,
            name = STAT_NAMES.GetValueOrDefault(kv.Key, $"Type{kv.Key}")
        }).ToList();
    }

    private string BuildFingerprint(dynamic analysis)
    {
        var stats = (List<object>)analysis.stats;
        var parts = stats.Select(s => $"{((dynamic)s).statType}:{((dynamic)s).statValue}").OrderBy(x => x);
        return string.Join("|", parts);
    }

    private string BuildVariantFingerprint(VariantData v)
    {
        var parts = v.stats.Select(s => $"{s.statType}:{s.statValue}").OrderBy(x => x);
        return string.Join("|", parts);
    }

    private List<TierRange> GetRequiredTiers(RulesetDto ruleset)
    {
        if (ruleset.namingTiers != null && ruleset.namingTiers.Length > 0)
        {
            return ruleset.namingTiers
                .Where(t => !string.IsNullOrEmpty(t.label))
                .Select(t => new TierRange
                {
                    minPct = t.minPct,
                    maxPct = t.maxPct,
                    label = t.label ?? "",
                    position = t.position ?? "suffix",
                    slots = Math.Max(0, t.slots),
                    minBoostPct = t.minBoostPct,
                    maxBoostPct = t.maxBoostPct
                })
                .ToList();
        }

        return new List<TierRange>
        {
            new() { minPct = 0, maxPct = 79, label = "Improved", position = "prefix" },
            new() { minPct = 80, maxPct = 89, label = "of Power", position = "suffix" },
            new() { minPct = 90, maxPct = 97, label = "of Glory", position = "suffix" },
            new() { minPct = 98, maxPct = 100, label = "of the Gods", position = "suffix" }
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  DB WRITE HELPERS
    // ══════════════════════════════════════════════════════════════

    /// <summary>Fast variant insert using pre-cached column list (no schema query per call).</summary>
    private async Task InsertVariantItemFast(MySqlConnector.MySqlConnection conn, List<string> columns,
        dynamic baseItem, int newEntry, CommitRoll roll, RulesetDto ruleset, bool isLegendary = false)
    {
        int baseEntry = (int)baseItem.entry;
        int basePatch = GetPropInt(baseItem, "patch");

        // Weapon DAMAGE bump for this tier (weapons only; speed/delay untouched, so
        // DPS scales 1:1). Legendary uses its own bump; blank tier => damage verbatim.
        int itemClass = GetPropInt(baseItem, "class");
        float dpsBump = isLegendary
            ? ruleset.legendaryDpsBumpPct
            : (ResolveTierDpsBump(ruleset.namingTiers, roll.tierLabel, roll.budgetPct) ?? 0f);
        double dmgMult = (itemClass == ITEM_CLASS_WEAPON && dpsBump > 0f) ? 1.0 + dpsBump / 100.0 : 1.0;

        var statTypes = new int[10];
        var statValues = new int[10];
        for (int i = 0; i < 10; i++)
        {
            statTypes[i] = GetPropInt(baseItem, $"stat_type{i + 1}");
            statValues[i] = GetPropInt(baseItem, $"stat_value{i + 1}");
        }
        for (int i = 0; i < Math.Min(roll.stats.Length, 10); i++)
        {
            statTypes[i] = roll.stats[i].statType;
            statValues[i] = roll.stats[i].statValue;
        }
        for (int i = roll.stats.Length; i < 10; i++)
        {
            statTypes[i] = 0;
            statValues[i] = 0;
        }

        string tierLabel = roll.tierLabel ?? "";
        string tierPosition = roll.tierPosition ?? "suffix";

        var selectParts = new List<string>();
        foreach (var col in columns)
        {
            if (col == "entry")
                selectParts.Add($"{newEntry} AS `entry`");
            else if (col == "name")
            {
                if (tierPosition == "prefix" && !string.IsNullOrEmpty(tierLabel))
                    selectParts.Add("CONCAT(@TierLabel, ' ', name) AS `name`");
                else if (!string.IsNullOrEmpty(tierLabel))
                    selectParts.Add("CONCAT(name, ' ', @TierLabel) AS `name`");
                else
                    selectParts.Add("`name`");
            }
            else if (col.StartsWith("stat_type") && col.Length <= 11)
            {
                int idx = int.Parse(col.Replace("stat_type", "")) - 1;
                selectParts.Add($"@ST{idx} AS `{col}`");
            }
            else if (col.StartsWith("stat_value") && col.Length <= 12)
            {
                int idx = int.Parse(col.Replace("stat_value", "")) - 1;
                selectParts.Add($"@SV{idx} AS `{col}`");
            }
            else if (dmgMult != 1.0 && (col.StartsWith("dmg_min") || col.StartsWith("dmg_max")))
            {
                // dmg_min1..5 / dmg_max1..5 — scale every damage band by the same
                // factor (keeps min:max feel; delay/dmg_type untouched).
                selectParts.Add($"ROUND(`{col}` * @DmgMult, 2) AS `{col}`");
            }
            else
                selectParts.Add($"`{col}`");
        }

        var sql = $"INSERT IGNORE INTO item_template SELECT {string.Join(", ", selectParts)} FROM item_template WHERE entry = @BaseEntry AND patch = @BasePatch";

        await conn.ExecuteAsync(sql, new
        {
            BaseEntry = baseEntry,
            BasePatch = basePatch,
            TierLabel = tierLabel,
            DmgMult = dmgMult,
            ST0 = statTypes[0],
            SV0 = statValues[0],
            ST1 = statTypes[1],
            SV1 = statValues[1],
            ST2 = statTypes[2],
            SV2 = statValues[2],
            ST3 = statTypes[3],
            SV3 = statValues[3],
            ST4 = statTypes[4],
            SV4 = statValues[4],
            ST5 = statTypes[5],
            SV5 = statValues[5],
            ST6 = statTypes[6],
            SV6 = statValues[6],
            ST7 = statTypes[7],
            SV7 = statValues[7],
            ST8 = statTypes[8],
            SV8 = statValues[8],
            ST9 = statTypes[9],
            SV9 = statValues[9]
        });

        // Per-tier (or legendary) Gold +%, master-scaled — falls back to the legacy
        // budget curve when the tier's Gold +% is blank.
        float goldMult = EffectiveGoldMult(ruleset, roll.tierLabel ?? "", roll.budgetPct, isLegendary);
        if (goldMult > 1.05f)
        {
            await conn.ExecuteAsync(
                "UPDATE item_template SET buy_price = ROUND(buy_price * @Mult), sell_price = ROUND(sell_price * @Mult) WHERE entry = @Entry",
                new { Mult = goldMult, Entry = newEntry });
        }

        // Variant quality via the shared tier ladder, floored at the base's own
        // quality (never below what it replaces). Legendary results lose disenchant.
        int baseQuality = GetPropInt(baseItem, "quality");
        string tier = CanonicalTier(roll.tierLabel ?? "", roll.budgetPct);
        int variantQuality = VariantQuality(tier, baseQuality);
        await conn.ExecuteAsync(
            "UPDATE item_template SET quality = @Q WHERE entry = @Entry",
            new { Q = variantQuality, Entry = newEntry });
        if (variantQuality >= 5)
            await ClearDisenchant(conn, newEntry);
    }

    private async Task InsertVariantItem(MySqlConnector.MySqlConnection conn, dynamic baseItem, int newEntry, CommitRoll roll, RulesetDto ruleset, bool isLegendary = false)
    {
        int baseEntry = (int)baseItem.entry;
        int basePatch = GetPropInt(baseItem, "patch");

        // Weapon DAMAGE bump for this tier (weapons only; speed untouched).
        int itemClass = GetPropInt(baseItem, "class");
        float dpsBump = isLegendary
            ? ruleset.legendaryDpsBumpPct
            : (ResolveTierDpsBump(ruleset.namingTiers, roll.tierLabel, roll.budgetPct) ?? 0f);
        double dmgMult = (itemClass == ITEM_CLASS_WEAPON && dpsBump > 0f) ? 1.0 + dpsBump / 100.0 : 1.0;

        int baseStatCount = 0;
        for (int i = 1; i <= 10; i++)
        {
            if (GetPropInt(baseItem, $"stat_type{i}") > 0) baseStatCount = i;
        }

        var statTypes = new int[10];
        var statValues = new int[10];

        // Copy existing base stats for slots that won't be overwritten
        for (int i = 0; i < 10; i++)
        {
            statTypes[i] = GetPropInt(baseItem, $"stat_type{i + 1}");
            statValues[i] = GetPropInt(baseItem, $"stat_value{i + 1}");
        }

        // Overwrite with rolled stats
        for (int i = 0; i < Math.Min(roll.stats.Length, 10); i++)
        {
            statTypes[i] = roll.stats[i].statType;
            statValues[i] = roll.stats[i].statValue;
        }
        // Clear any remaining slots beyond the rolled count
        for (int i = roll.stats.Length; i < 10; i++)
        {
            statTypes[i] = 0;
            statValues[i] = 0;
        }

        var columns = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'mangos' AND TABLE_NAME = 'item_template' ORDER BY ORDINAL_POSITION"
        )).ToList();

        // Build name with prefix or suffix
        string tierLabel = roll.tierLabel ?? "";
        string tierPosition = roll.tierPosition ?? "suffix";

        var selectParts = new List<string>();

        foreach (var col in columns)
        {
            if (col == "entry")
                selectParts.Add($"{newEntry} AS `entry`");
            else if (col == "name")
            {
                if (tierPosition == "prefix" && !string.IsNullOrEmpty(tierLabel))
                    selectParts.Add("CONCAT(@TierLabel, ' ', name) AS `name`");
                else if (!string.IsNullOrEmpty(tierLabel))
                    selectParts.Add("CONCAT(name, ' ', @TierLabel) AS `name`");
                else
                    selectParts.Add("`name`");
            }
            else if (col.StartsWith("stat_type") && col.Length <= 11)
            {
                int idx = int.Parse(col.Replace("stat_type", "")) - 1;
                selectParts.Add($"@ST{idx} AS `{col}`");
            }
            else if (col.StartsWith("stat_value") && col.Length <= 12)
            {
                int idx = int.Parse(col.Replace("stat_value", "")) - 1;
                selectParts.Add($"@SV{idx} AS `{col}`");
            }
            else if (dmgMult != 1.0 && (col.StartsWith("dmg_min") || col.StartsWith("dmg_max")))
            {
                // dmg_min1..5 / dmg_max1..5 — scale every damage band by the same
                // factor (keeps min:max feel; delay/dmg_type untouched).
                selectParts.Add($"ROUND(`{col}` * @DmgMult, 2) AS `{col}`");
            }
            else
                selectParts.Add($"`{col}`");
        }

        var sql = $"INSERT IGNORE INTO item_template SELECT {string.Join(", ", selectParts)} FROM item_template WHERE entry = @BaseEntry AND patch = @BasePatch";

        await conn.ExecuteAsync(sql, new
        {
            BaseEntry = baseEntry,
            BasePatch = basePatch,
            TierLabel = tierLabel,
            DmgMult = dmgMult,
            ST0 = statTypes[0],
            SV0 = statValues[0],
            ST1 = statTypes[1],
            SV1 = statValues[1],
            ST2 = statTypes[2],
            SV2 = statValues[2],
            ST3 = statTypes[3],
            SV3 = statValues[3],
            ST4 = statTypes[4],
            SV4 = statValues[4],
            ST5 = statTypes[5],
            SV5 = statValues[5],
            ST6 = statTypes[6],
            SV6 = statValues[6],
            ST7 = statTypes[7],
            SV7 = statValues[7],
            ST8 = statTypes[8],
            SV8 = statValues[8],
            ST9 = statTypes[9],
            SV9 = statValues[9]
        });

        // Per-tier (or legendary) Gold +%, master-scaled — falls back to the legacy
        // budget curve when the tier's Gold +% is blank.
        float goldMult = EffectiveGoldMult(ruleset, roll.tierLabel ?? "", roll.budgetPct, isLegendary);
        if (goldMult > 1.0f)
        {
            await conn.ExecuteAsync(
                "UPDATE item_template SET buy_price = ROUND(buy_price * @Mult), sell_price = ROUND(sell_price * @Mult) WHERE entry = @Entry",
                new { Mult = goldMult, Entry = newEntry });
        }

        // Variant quality via the shared tier ladder, floored at the base's own
        // quality (never below what it replaces). Legendary results lose disenchant.
        int baseQuality = GetPropInt(baseItem, "quality");
        string tier = CanonicalTier(roll.tierLabel ?? "", roll.budgetPct);
        int variantQuality = VariantQuality(tier, baseQuality);
        await conn.ExecuteAsync(
            "UPDATE item_template SET quality = @Q WHERE entry = @Entry",
            new { Q = variantQuality, Entry = newEntry });
        if (variantQuality >= 5)
            await ClearDisenchant(conn, newEntry);
    }

    // ══════════════════════════════════════════════════════════════
    //  LEGENDARY GENERATION
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Generate a legendary preview (no DB writes) for display in the variant preview UI.
    /// Returns null if legendary can't be generated for these inputs.
    /// </summary>
    private async Task<object?> PreviewLegendary(
        MySqlConnector.MySqlConnection conn,
        int creatureEntry,
        List<int> eligibleItemEntries,
        RulesetDto ruleset)
    {
        if (eligibleItemEntries.Count == 0 || creatureEntry <= 0) return null;

        var rng = new Random();

        // Pick the item: user-chosen (legendaryItemEntry > 0) or random
        int chosenEntry = ruleset.legendaryItemEntry > 0 && eligibleItemEntries.Contains(ruleset.legendaryItemEntry)
            ? ruleset.legendaryItemEntry
            : eligibleItemEntries[rng.Next(eligibleItemEntries.Count)];

        var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT * FROM item_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
            new { E = chosenEntry });
        if (item == null) return null;

        var creature = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT name FROM creature_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = @E)",
            new { E = creatureEntry });
        string bossName = creature != null ? (string)creature.name : $"Boss #{creatureEntry}";

        var analysis = AnalyzeItemStats(item);
        bool hasStats = (int)analysis.totalStats > 0;
        bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;
        if (!IsEquippableGear(item)) return null;

        float baseBudget = (float)analysis.weightedBudget;
        if (!hasStats && hasSpellEffects)
            baseBudget = EstimateBudgetFromItemLevel(GetPropInt(item, "item_level"));

        // Roll inside the legendary's own Boost band, exactly like a naming tier.
        float legLo = Math.Max(0f, Math.Min(ruleset.legendaryBoostMinPct, ruleset.legendaryBoostMaxPct));
        float legHi = Math.Max(0f, Math.Max(ruleset.legendaryBoostMinPct, ruleset.legendaryBoostMaxPct));
        float legBoost = legLo + (float)rng.NextDouble() * (legHi - legLo);
        float legendaryBudget = baseBudget * (1f + legBoost / 100f);
        int[] presentTypes = (int[])analysis.presentStatTypes;
        string family = (string)analysis.detectedFamily;

        var eligible = new HashSet<int>(presentTypes);
        var familyStats = STAT_FAMILIES.GetValueOrDefault(family, STAT_FAMILIES["hybrid"]);
        foreach (var s in familyStats) eligible.Add(s);
        var eligibleList = eligible.ToList();

        // Floor the legendary one step above the highest naming tier. Without this
        // it rolled at the bare MIN_DELTA_BUDGET while the top tier sat on
        // MIN_DELTA_BUDGET + n*MIN_TIER_STEP, so on a low-budget base the purple
        // came out stronger than the legendary.
        float legMinDelta = MIN_DELTA_BUDGET + Math.Max(1, ruleset.namingTiers?.Length ?? 3) * MIN_TIER_STEP;
        List<StatRoll> stats;
        if (hasStats)
            stats = RollStats(rng, legendaryBudget, presentTypes, eligibleList, analysis, ruleset, legMinDelta);
        else
            stats = RollStatsForSpellItem(rng, legendaryBudget, eligibleList, family);

        string itemName = (string)item.name;
        string legendaryName = BuildLegendaryName(bossName, itemName, family, ruleset);

        uint displayId = (uint)item.display_id;
        string iconPath = _dbc.GetItemIconPath(displayId);

        return new
        {
            baseItemEntry = chosenEntry,
            baseItemName = itemName,
            baseItemQuality = (int)item.quality,
            displayId,
            iconPath,
            legendaryName,
            bossName,
            budgetPct = 150.0,
            dropPct = ruleset.legendaryDropPct,
            quality = 5,
            stats = stats.Select(s => (object)new { s.statType, s.statValue, s.name }).ToList()
        };
    }

    /// <summary>
    /// Build ONE boss-named legendary item for a specific base item and return
    /// its (entry, roll) so the caller can fold it into that base item's pool
    /// family alongside the normal variants — in a SINGLE JoinExistingPool /
    /// CreatePoolFromDirect call. This is what keeps the legendary from
    /// double-claiming the family budget (the old per-creature pass ran a
    /// separate pooling call and grabbed the whole variant remainder).
    ///
    /// New philosophy: one legendary PER lootified item (not one per creature).
    /// The legendary is a 150%-budget variant; PoolWeight(150) floors to 1, so
    /// it lands as the rarest member of its family.
    ///
    /// No loot-table writes here — returns null if the item can't be built.
    /// </summary>
    private async Task<(int entry, CommitRoll roll)?> BuildLegendaryItem(
        MySqlConnector.MySqlConnection mangosConn,
        MySqlConnector.MySqlConnection adminConn,
        List<string>? columns,
        int creatureEntry, int baseItemEntry, RulesetDto ruleset,
        List<(int genEntry, int baseEntry, int creatureEntry, float budgetPct, string tierName)>? trackingItemRows = null,
        bool? isShared = null,
        int? reuseEntry = null)
    {
        var rng = new Random();

        var item = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT * FROM item_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
            new { E = baseItemEntry });
        if (item == null) return null;

        var creature = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT name FROM creature_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = @E)",
            new { E = creatureEntry });
        string bossName = creature != null ? (string)creature.name : $"Boss #{creatureEntry}";

        // Shared = dropped by more than one creature. If the caller didn't decide,
        // count distinct creatures whose loot (direct or via reference) yields this
        // item. Shared items get the generic suffix (no single creature to name).
        bool shared = isShared ?? await IsSharedDropItem(mangosConn, baseItemEntry);

        var analysis = AnalyzeItemStats(item);
        bool hasStats = (int)analysis.totalStats > 0;
        bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;
        if (!IsEquippableGear(item)) return null;

        float baseBudget = (float)analysis.weightedBudget;
        if (!hasStats && hasSpellEffects)
            baseBudget = EstimateBudgetFromItemLevel(GetPropInt(item, "item_level"));

        // Roll inside the legendary's own Boost band, exactly like a naming tier.
        float legLo = Math.Max(0f, Math.Min(ruleset.legendaryBoostMinPct, ruleset.legendaryBoostMaxPct));
        float legHi = Math.Max(0f, Math.Max(ruleset.legendaryBoostMinPct, ruleset.legendaryBoostMaxPct));
        float legBoost = legLo + (float)rng.NextDouble() * (legHi - legLo);
        float legendaryBudget = baseBudget * (1f + legBoost / 100f);
        int[] presentTypes = (int[])analysis.presentStatTypes;
        string family = (string)analysis.detectedFamily;

        var eligible = new HashSet<int>(presentTypes);
        var familyStats = STAT_FAMILIES.GetValueOrDefault(family, STAT_FAMILIES["hybrid"]);
        foreach (var s in familyStats) eligible.Add(s);
        var eligibleList = eligible.ToList();

        // Floor the legendary one step above the highest naming tier. Without this
        // it rolled at the bare MIN_DELTA_BUDGET while the top tier sat on
        // MIN_DELTA_BUDGET + n*MIN_TIER_STEP, so on a low-budget base the purple
        // came out stronger than the legendary.
        float legMinDelta = MIN_DELTA_BUDGET + Math.Max(1, ruleset.namingTiers?.Length ?? 3) * MIN_TIER_STEP;
        List<StatRoll> stats = hasStats
            ? RollStats(rng, legendaryBudget, presentTypes, eligibleList, analysis, ruleset, legMinDelta)
            : RollStatsForSpellItem(rng, legendaryBudget, eligibleList, family);

        string itemName = (string)item.name;
        string legendaryName = BuildLegendaryName(bossName, itemName, family, ruleset, forceSuffix: shared);

        var roll = new CommitRoll
        {
            budgetPct = 150f,
            tierLabel = legendaryName.Contains(itemName)
                ? legendaryName.Replace(itemName, "").Trim()
                : legendaryName,
            tierPosition = "full",
            stats = stats.Select(s => new CommitStat { statType = s.statType, statValue = s.statValue }).ToArray()
        };

        // Regenerate: overwrite the existing legendary entry in place (players keep
        // their copy) instead of minting a new id and orphaning the old one.
        int newEntry = reuseEntry ?? await GetNextLootifierId(adminConn);
        if (reuseEntry.HasValue)
            await mangosConn.ExecuteAsync("DELETE FROM item_template WHERE entry = @E", new { E = newEntry });

        if (columns != null)
            await InsertVariantItemFast(mangosConn, columns, item, newEntry, roll, ruleset, isLegendary: true);
        else
            await InsertVariantItem(mangosConn, item, newEntry, roll, ruleset, isLegendary: true);

        await mangosConn.ExecuteAsync(
            "UPDATE item_template SET name = @Name, quality = 5 WHERE entry = @Entry",
            new { Name = legendaryName, Entry = newEntry });

        // Legendaries aren't disenchantable. Column is `disenchant_id` in VMaNGOS
        // (resolved from the live schema — see ResolveDisenchantColumn).
        await ClearDisenchant(mangosConn, newEntry);

        // Price: the insert already applied ruleset.legendaryGoldBumpPct (default
        // 500% => x6, matching the old 2.0x curve x3 override). No extra bump here.

        if (reuseEntry.HasValue)
        {
            // Tracking row already exists for this entry — refresh its budget.
            await adminConn.ExecuteAsync(
                "UPDATE lootifier_generated_items SET budget_pct = 150, tier_name = 'Legendary' WHERE generated_entry = @E",
                new { E = newEntry });
        }
        else if (trackingItemRows != null)
        {
            trackingItemRows.Add((newEntry, baseItemEntry, creatureEntry, 150f, "Legendary"));
        }
        else
        {
            await adminConn.ExecuteAsync(@"
                INSERT INTO lootifier_generated_items
                    (generated_entry, base_entry, creature_entry, budget_pct, tier_name, created_at)
                VALUES (@GenEntry, @BaseEntry, @CreatureEntry, 150, 'Legendary', NOW())",
                new { GenEntry = newEntry, BaseEntry = baseItemEntry, CreatureEntry = creatureEntry });
        }

        return (newEntry, roll);
    }

    /// <summary>Build the legendary item name from boss name + item name.</summary>
    private string BuildLegendaryName(string bossName, string itemName, string family, RulesetDto ruleset, bool forceSuffix = false)
    {
        // Suffix when either (a) the user picked the "suffix" naming style, or
        // (b) the item is shared / non-unique (dropped by many creatures) so there
        // is no single creature to name it after. The generic family suffix
        // ("of Destruction" etc.) keeps the name stable regardless of how many
        // mobs drop it. Only unique-drop items in "named" style keep the
        // possessive creature name.
        bool suffixStyle = forceSuffix
            || string.Equals(ruleset.legendaryNameStyle, "suffix", StringComparison.OrdinalIgnoreCase);
        if (suffixStyle)
            return itemName + " " + GenericLegendarySuffix(family, ruleset);

        // Check if boss name overlaps with item name (any word ≥ 4 chars in common)
        var bossWords = bossName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\'', '\u2018', '\u2019', ',', '.').ToLowerInvariant())
            .Where(w => w.Length >= 4)
            .ToHashSet();

        var itemWords = itemName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\'', '\u2018', '\u2019', ',', '.').ToLowerInvariant())
            .Where(w => w.Length >= 4)
            .ToHashSet();

        bool hasOverlap = bossWords.Overlaps(itemWords);

        if (hasOverlap)
        {
            // Item already references the boss (e.g., "Smite's Mighty Reaper")
            // Use family-appropriate suffix
            return itemName + " " + GenericLegendarySuffix(family, ruleset);
        }
        else
        {
            // No overlap — prefix with possessive boss name
            // "Edwin VanCleef" → "Edwin VanCleef's"
            string possessive = bossName.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                ? bossName + "'"
                : bossName + "'s";
            return possessive + " " + itemName;
        }
    }

    /// <summary>Generic family-appropriate legendary suffix from the ruleset.</summary>
    private string GenericLegendarySuffix(string family, RulesetDto ruleset) => family switch
    {
        "physical" => ruleset.legendarySuffixMelee,
        "caster" => ruleset.legendarySuffixCaster,
        _ => ruleset.legendarySuffixMelee // hybrid defaults to melee
    };

    /// <summary>
    /// True if a base item is dropped by more than one creature — directly in
    /// creature_loot_template, or via a reference_loot_template pool that any
    /// creature points at. Used to decide legendary naming: shared items get a
    /// generic suffix (no single creature to name), unique drops get the name.
    /// </summary>
    private async Task<bool> IsSharedDropItem(MySqlConnector.MySqlConnection mangosConn, int itemEntry)
    {
        // Loot ids that yield this item directly.
        var directLootIds = (await mangosConn.QueryAsync<int>(
            "SELECT DISTINCT entry FROM creature_loot_template WHERE item = @I AND mincountOrRef > 0",
            new { I = itemEntry })).ToList();

        // Reference entries that contain this item, and the loot ids that point to them.
        var refEntries = (await mangosConn.QueryAsync<int>(
            "SELECT DISTINCT entry FROM reference_loot_template WHERE item = @I",
            new { I = itemEntry })).ToList();

        var refLootIds = new List<int>();
        if (refEntries.Count > 0)
            refLootIds = (await mangosConn.QueryAsync<int>(
                "SELECT DISTINCT entry FROM creature_loot_template WHERE mincountOrRef < 0 AND ABS(mincountOrRef) IN @Refs",
                new { Refs = refEntries })).ToList();

        var allLootIds = directLootIds.Concat(refLootIds).Distinct().ToList();
        if (allLootIds.Count == 0) return false;

        // Distinct creatures using any of those loot ids.
        int creatureCount = await mangosConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(DISTINCT entry) FROM creature_template WHERE loot_id IN @Ids",
            new { Ids = allLootIds });

        return creatureCount > 1;
    }

    private float GetGoldMultiplier(float budgetPct)
    {
        if (budgetPct >= 98) return 2.0f;
        if (budgetPct >= 90) return 1.8f;
        if (budgetPct >= 80) return 1.6f;
        float t = Math.Clamp(budgetPct / 79f, 0f, 1f);
        return 1.01f + t * 0.58f;
    }

    // Per-tier gold bump (%), resolved by exact label then budget-range containment.
    // Null = that tier left Gold +% blank, so the legacy budget curve is used.
    private static float? ResolveTierGoldBump(NamingTierDto[]? tiers, string? tierLabel, float budgetPct)
    {
        if (tiers == null || tiers.Length == 0) return null;
        if (!string.IsNullOrEmpty(tierLabel))
            foreach (var t in tiers)
                if (string.Equals(t.label, tierLabel, StringComparison.OrdinalIgnoreCase))
                    return t.goldBumpPct;
        // Fallback only fires for an unlabelled roll. Skip boost-mode tiers: their
        // minPct/maxPct are no longer percentile windows, so containment is meaningless.
        foreach (var t in tiers)
            if (!(t.minBoostPct.HasValue && t.maxBoostPct.HasValue)
                && budgetPct >= t.minPct && budgetPct <= t.maxPct)
                return t.goldBumpPct;
        return null;
    }

    // Per-tier weapon DAMAGE bump (%), resolved the same way. Null = damage verbatim.
    private static float? ResolveTierDpsBump(NamingTierDto[]? tiers, string? tierLabel, float budgetPct)
    {
        if (tiers == null || tiers.Length == 0) return null;
        if (!string.IsNullOrEmpty(tierLabel))
            foreach (var t in tiers)
                if (string.Equals(t.label, tierLabel, StringComparison.OrdinalIgnoreCase))
                    return t.dpsBumpPct;
        foreach (var t in tiers)
            if (!(t.minBoostPct.HasValue && t.maxBoostPct.HasValue)
                && budgetPct >= t.minPct && budgetPct <= t.maxPct)
                return t.dpsBumpPct;
        return null;
    }

    // Final buy/sell multiplier for a variant. An explicit per-tier (or legendary)
    // Gold +% wins; a blank tier falls back to the legacy budget curve.
    private float EffectiveGoldMult(RulesetDto ruleset, string tierLabel, float budgetPct, bool isLegendary)
    {
        float? bump = isLegendary
            ? (float?)ruleset.legendaryGoldBumpPct
            : ResolveTierGoldBump(ruleset.namingTiers, tierLabel, budgetPct);
        // Gold +% is taken literally — what's typed on the row is what's applied.
        if (bump.HasValue)
            return 1f + (bump.Value / 100f);
        return GetGoldMultiplier(budgetPct);
    }

    // Current melee DPS of a base item (weapons only) from dmg_min/max over delay.
    private dynamic WeaponDpsInfo(dynamic item)
    {
        int itemClass = GetPropInt(item, "class");
        int delay = GetPropInt(item, "delay");
        if (itemClass != ITEM_CLASS_WEAPON || delay <= 0)
            return new { isWeapon = false, baseDps = 0f, delay = 0, twoHand = false };

        float avg = 0f;
        for (int i = 1; i <= 5; i++)
            avg += (GetPropFloat(item, $"dmg_min{i}") + GetPropFloat(item, $"dmg_max{i}")) / 2f;
        float dps = avg / (delay / 1000f);
        bool twoHand = GetPropInt(item, "inventory_type") == 17;   // INVTYPE_2HWEAPON
        return new { isWeapon = true, baseDps = (float)Math.Round(dps, 1), delay, twoHand };
    }

    private float GetPropFloat(dynamic obj, string name)
    {
        var dict = obj as IDictionary<string, object>;
        if (dict != null && dict.TryGetValue(name, out var val))
            return val == null ? 0f : Convert.ToSingle(val);
        return 0f;
    }

    // ══════════════════════════════════════════════════════════════
    //  PLAYER-ITEM REROLL (mirrors Quest/Crafting lootifiers)
    //
    //  Deleting a variant template orphans any copy a player has equipped,
    //  bagged, mailed or listed. Repoint each owned copy at a NEW variant of the
    //  SAME tier (rolled per item); if the tier is gone, fall back to any new
    //  variant, then the plain base item. Rollback passes an empty new-pool, so
    //  owned copies revert to the base item.
    // ══════════════════════════════════════════════════════════════

    private async Task<bool> CharactersDbAvailable(MySqlConnector.MySqlConnection conn) =>
        await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @Db AND TABLE_NAME = 'item_instance' AND COLUMN_NAME = 'item_id'",
            new { Db = CHARACTERS_DB }) > 0;

    private async Task<List<(string table, string guidCol, string entryCol)>> GetCharacterItemRefTables(
        MySqlConnector.MySqlConnection conn)
    {
        var candidates = new (string table, string[] guidCols, string[] entryCols)[]
        {
            ("character_inventory", new[] { "item", "item_guid" }, new[] { "item_template", "item_id", "itemEntry" }),
            ("mail_items",          new[] { "item_guid", "itemguid", "item" }, new[] { "item_template", "item_id", "itemEntry" }),
            ("auction",             new[] { "item_guid", "itemguid" }, new[] { "item_template", "item_id", "itemEntry" }),
        };

        var found = new List<(string, string, string)>();
        foreach (var c in candidates)
        {
            var cols = (await conn.QueryAsync<string>(@"
                SELECT COLUMN_NAME FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @Db AND TABLE_NAME = @T",
                new { Db = CHARACTERS_DB, T = c.table })).ToList();
            if (cols.Count == 0) continue;

            string? g = c.guidCols.FirstOrDefault(x => cols.Contains(x, StringComparer.OrdinalIgnoreCase));
            string? e = c.entryCols.FirstOrDefault(x => cols.Contains(x, StringComparer.OrdinalIgnoreCase));
            if (g != null && e != null) found.Add((c.table, g, e));
        }
        return found;
    }

    private async Task<int> RemapOwnedVariants(MySqlConnector.MySqlConnection mangosConn,
        List<int> oldEntries, List<int> newPool, int fallbackEntry, Random rng,
        Dictionary<int, string>? oldTierByEntry = null,
        Dictionary<string, List<int>>? newByTier = null)
    {
        if (oldEntries == null || oldEntries.Count == 0) return 0;
        if (!await CharactersDbAvailable(mangosConn)) return 0;

        var owned = (await mangosConn.QueryAsync(
            $"SELECT guid, item_id FROM `{CHARACTERS_DB}`.item_instance WHERE item_id IN @Old",
            new { Old = oldEntries })).ToList();
        if (owned.Count == 0) return 0;

        var aux = await GetCharacterItemRefTables(mangosConn);
        int remapped = 0;

        foreach (var row in owned)
        {
            var d = (IDictionary<string, object>)row;
            int itemGuid = Convert.ToInt32(d["guid"]);
            int oldId = Convert.ToInt32(d["item_id"]);
            int target = PickRerollTarget(oldId, newPool, fallbackEntry, rng, oldTierByEntry, newByTier);
            if (target <= 0) continue;

            await mangosConn.ExecuteAsync(
                $"UPDATE `{CHARACTERS_DB}`.item_instance SET item_id = @New WHERE guid = @G",
                new { New = target, G = itemGuid });

            foreach (var t in aux)
                await mangosConn.ExecuteAsync(
                    $"UPDATE `{CHARACTERS_DB}`.`{t.table}` SET `{t.entryCol}` = @New WHERE `{t.guidCol}` = @G",
                    new { New = target, G = itemGuid });

            remapped++;
        }
        return remapped;
    }

    // Same tier as the copy held wins; otherwise any new variant; otherwise base.
    private static int PickRerollTarget(int oldId, List<int> newPool, int fallbackEntry, Random rng,
        Dictionary<int, string>? oldTierByEntry, Dictionary<string, List<int>>? newByTier)
    {
        if (oldTierByEntry != null && newByTier != null &&
            oldTierByEntry.TryGetValue(oldId, out var tier) &&
            newByTier.TryGetValue(tier, out var sameTier) && sameTier.Count > 0)
            return sameTier[rng.Next(sameTier.Count)];
        if (newPool.Count > 0) return newPool[rng.Next(newPool.Count)];
        return fallbackEntry;
    }

    private static void AddToTierPool(Dictionary<string, List<int>> byTier, string tier, int entry)
    {
        if (!byTier.TryGetValue(tier, out var lst)) byTier[tier] = lst = new List<int>();
        lst.Add(entry);
    }

    // ══════════════════════════════════════════════════════════════
    //  OVERWRITE-STYLE IN-PLACE REGENERATE
    //
    //  Re-roll a base's BAND variants while REUSING existing entry IDs, paired by
    //  tier. Overwriting the same entry (delete + reinsert same id) means every
    //  player-owned copy and every loot-pool pointer stays valid — the player just
    //  gets a freshly-rolled item of the SAME tier, never orphaned, never demoted.
    //
    //  Count changes: surplus NEW variants get fresh entries (the idempotent pool
    //  re-expand adds them); surplus OLD entries have their owned copies remapped
    //  to a surviving same-tier variant (then base), and are deleted with their
    //  pool member rows. The legendary is handled by the caller (BuildLegendaryItem
    //  with reuseEntry). Tracking is written immediately (not batched) here.
    // ══════════════════════════════════════════════════════════════

    private async Task<(List<int> entries, List<CommitRoll> rolls, int created, int reused, int removed, int remapped)>
        ReconcileBandVariants(MySqlConnector.MySqlConnection mangosConn, MySqlConnector.MySqlConnection adminConn,
            List<string> columns, dynamic baseItem, int baseEntry, int attributionCreature,
            RulesetDto ruleset, List<CommitRoll> newRolls, Random rng)
    {
        // Existing non-legendary variants for this base, grouped by tier.
        var existing = (await adminConn.QueryAsync(
            "SELECT generated_entry, tier_name FROM lootifier_generated_items WHERE base_entry = @B AND tier_name <> 'Legendary'",
            new { B = baseEntry })).ToList();

        var oldByTier = new Dictionary<string, List<int>>();
        var oldTierByEntry = new Dictionary<int, string>();
        foreach (var r in existing)
        {
            var d = (IDictionary<string, object>)r;
            int e = Convert.ToInt32(d["generated_entry"]);
            string t = (d["tier_name"] as string) ?? "";
            AddToTierPool(oldByTier, t, e);
            oldTierByEntry[e] = t;
        }

        var newByTier = new Dictionary<string, List<CommitRoll>>();
        foreach (var roll in newRolls)
        {
            string t = roll.tierLabel ?? "";
            if (!newByTier.TryGetValue(t, out var lst)) newByTier[t] = lst = new List<CommitRoll>();
            lst.Add(roll);
        }

        var finalEntries = new List<int>();
        var finalRolls = new List<CommitRoll>();
        var survivorsByTier = new Dictionary<string, List<int>>(); // reused+fresh, per tier (remap targets)
        var toRemove = new List<int>();
        int created = 0, reused = 0, removed = 0, remapped = 0;
        int nextId = await GetNextLootifierId(adminConn);

        var allTiers = new HashSet<string>(oldByTier.Keys);
        allTiers.UnionWith(newByTier.Keys);

        foreach (var tier in allTiers)
        {
            var olds = oldByTier.GetValueOrDefault(tier) ?? new List<int>();
            var news = newByTier.GetValueOrDefault(tier) ?? new List<CommitRoll>();
            int pair = Math.Min(olds.Count, news.Count);

            // Overwrite reused entries in place (same id, same tier).
            for (int i = 0; i < pair; i++)
            {
                int entry = olds[i];
                var roll = news[i];
                await mangosConn.ExecuteAsync("DELETE FROM item_template WHERE entry = @E", new { E = entry });
                await InsertVariantItemFast(mangosConn, columns, baseItem, entry, roll, ruleset);
                await adminConn.ExecuteAsync(
                    "UPDATE lootifier_generated_items SET budget_pct = @B, tier_name = @T WHERE generated_entry = @E",
                    new { B = roll.budgetPct, T = roll.tierLabel ?? "", E = entry });
                finalEntries.Add(entry);
                finalRolls.Add(roll);
                AddToTierPool(survivorsByTier, tier, entry);
                reused++;
            }

            // Surplus NEW → fresh entries (pool re-expand adds them idempotently).
            for (int i = pair; i < news.Count; i++)
            {
                int entry = nextId++;
                var roll = news[i];
                await InsertVariantItemFast(mangosConn, columns, baseItem, entry, roll, ruleset);
                await adminConn.ExecuteAsync(@"
                    INSERT INTO lootifier_generated_items
                        (generated_entry, base_entry, creature_entry, budget_pct, tier_name, created_at)
                    VALUES (@G, @B, @C, @Bud, @T, NOW())",
                    new { G = entry, B = baseEntry, C = attributionCreature, Bud = roll.budgetPct, T = roll.tierLabel ?? "" });
                finalEntries.Add(entry);
                finalRolls.Add(roll);
                AddToTierPool(survivorsByTier, tier, entry);
                created++;
            }

            // Surplus OLD → remove (remapped below once survivors are known).
            for (int i = news.Count; i < olds.Count; i++)
                toRemove.Add(olds[i]);
        }

        // Owned copies of removed variants → a surviving SAME-tier variant, else base.
        if (toRemove.Count > 0)
        {
            remapped = await RemapOwnedVariants(mangosConn, toRemove, finalEntries, baseEntry, rng, oldTierByEntry, survivorsByTier);
            foreach (int e in toRemove)
            {
                // Remove the variant's pool member rows (found via our own tracking,
                // so vanilla ref groups joined by ref-sourced bases are handled too).
                var memberRows = (await adminConn.QueryAsync(
                    "SELECT loot_table, loot_entry FROM lootifier_loot_entries WHERE item_entry = @E AND action_type IN ('pool_member','pool_joined','add_member')",
                    new { E = e })).ToList();
                foreach (var mr in memberRows)
                {
                    var d = (IDictionary<string, object>)mr;
                    string table = (string)d["loot_table"];
                    int refE = Convert.ToInt32(d["loot_entry"]);
                    await mangosConn.ExecuteAsync($"DELETE FROM `{table}` WHERE entry = @Ref AND item = @E", new { Ref = refE, E = e });
                }
                await mangosConn.ExecuteAsync("DELETE FROM item_template WHERE entry = @E", new { E = e });
                await adminConn.ExecuteAsync("DELETE FROM lootifier_generated_items WHERE generated_entry = @E", new { E = e });
                await adminConn.ExecuteAsync("DELETE FROM lootifier_loot_entries WHERE item_entry = @E", new { E = e });
                removed++;
            }
        }

        return (finalEntries, finalRolls, created, reused, removed, remapped);
    }

    private async Task<bool> BaseHasVariants(MySqlConnector.MySqlConnection adminConn, int baseEntry) =>
        await adminConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM lootifier_generated_items WHERE base_entry = @B", new { B = baseEntry }) > 0;

    private async Task<int?> GetExistingLegendaryEntry(MySqlConnector.MySqlConnection adminConn, int baseEntry) =>
        await adminConn.ExecuteScalarAsync<int?>(
            "SELECT generated_entry FROM lootifier_generated_items WHERE base_entry = @B AND tier_name = 'Legendary' ORDER BY generated_entry LIMIT 1",
            new { B = baseEntry });

    // ══════════════════════════════════════════════════════════════
    //  WEIGHTED POOL EXPANSION (v5)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Pool a creature's loot for one base item: base + variants become a
    /// single weighted group that always resolves to exactly one pick.
    /// - Ref-sourced (base already in a reference group) → add variants to that
    ///   same ref group, renormalize the whole group to sum 100 ('pool_joined').
    /// - Direct-drop → mint a reference group, move base in, add variants,
    ///   normalize to 100, replace base's direct row with a 100%/1 pointer
    ///   ('pool_created').
    /// trackingRows == null → tracking rows written immediately (single path);
    /// otherwise buffered for bulk flush (batch path).
    /// Returns the number of variant members added.
    /// </summary>
    private async Task<int> ExpandLootTable(MySqlConnector.MySqlConnection mangosConn,
        MySqlConnector.MySqlConnection adminConn,
        List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)>? trackingRows,
        int lootId, int baseItemEntry, List<int> variantEntries, CommitRoll[] rolls, int creatureEntry)
    {
        var directRow = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT entry, item, ChanceOrQuestChance AS chance, groupid, mincountOrRef, maxcount, patch_min, patch_max
            FROM creature_loot_template
            WHERE entry = @LootId AND item = @Item AND mincountOrRef > 0",
            new { LootId = lootId, Item = baseItemEntry });

        if (directRow != null)
            return await CreatePoolFromDirect(mangosConn, adminConn, trackingRows,
                lootId, directRow, baseItemEntry, variantEntries, rolls, creatureEntry);

        // Ref-sourced: find the reference group the base lives in, join it.
        var refPtrs = await mangosConn.QueryAsync<dynamic>(@"
            SELECT mincountOrRef FROM creature_loot_template
            WHERE entry = @LootId AND mincountOrRef < 0",
            new { LootId = lootId });

        foreach (var ptr in refPtrs)
        {
            int refEntry = Math.Abs((int)ptr.mincountOrRef);
            var refRow = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT entry, item, ChanceOrQuestChance AS chance, groupid, mincountOrRef, maxcount, patch_min, patch_max
                FROM reference_loot_template
                WHERE entry = @RefEntry AND item = @Item",
                new { RefEntry = refEntry, Item = baseItemEntry });

            if (refRow != null)
            {
                int added = await JoinExistingPool(mangosConn, adminConn, trackingRows,
                    refRow, baseItemEntry, variantEntries, rolls, creatureEntry);

                // Creature-side breadcrumb so LoadExpandedPairs dedups this
                // (lootId, base) on future runs — mirrors the direct-drop marker.
                await TrackLoot(adminConn, trackingRows, creatureEntry, "creature_loot_template",
                    lootId, baseItemEntry, "pool_created", -1, (int)refRow.entry);

                return added;
            }
        }

        return 0;
    }

    /// <summary>
    /// Relative pool weight for a member from its budget %. Low-tier variants
    /// common, "of the Gods"/legendary rare. Returned as a raw weight; callers
    /// distribute a fixed budget across these, then apply the member floor.
    /// </summary>
    private static float PoolWeight(float budgetPct) => Math.Max(1f, 105f - budgetPct);

    /// <summary>
    /// Distribute a family's variant budget across N members by raw weight,
    /// enforcing MEMBER_FLOOR_PCT: any member the weighted split pushes below
    /// the floor snaps up to it, and the excess is taken proportionally from
    /// the members still above the floor. Iterates until stable.
    ///
    /// Returns null (does NOT floor) if the budget cannot legally seat every
    /// member at the floor (count * floor > budget) — caller warns and falls
    /// back to an unfloored proportional split so the pool stays valid.
    /// The returned array sums to exactly `budget`.
    /// </summary>
    private static float[]? DistributeWithFloor(float[] rawWeights, float budget, float floor)
    {
        int n = rawWeights.Length;
        if (n == 0) return Array.Empty<float>();

        // Capacity check: can't give everyone the floor out of this budget.
        if (floor * n > budget + 1e-4f) return null;

        var share = new float[n];
        var locked = new bool[n]; // locked = pinned at floor
        float rawSum = rawWeights.Sum();
        if (rawSum <= 0f) rawSum = n; // equal split fallback

        // Iteratively pin sub-floor members and redistribute the remainder.
        for (int pass = 0; pass < n + 1; pass++)
        {
            float lockedTotal = 0f, freeWeight = 0f;
            for (int i = 0; i < n; i++)
            {
                if (locked[i]) lockedTotal += floor;
                else freeWeight += (rawSum > 0 ? rawWeights[i] : 1f);
            }
            float freeBudget = budget - lockedTotal;

            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                if (locked[i]) { share[i] = floor; continue; }
                float w = rawSum > 0 ? rawWeights[i] : 1f;
                float s = freeWeight > 0 ? freeBudget * (w / freeWeight) : freeBudget / n;
                if (s < floor)
                {
                    locked[i] = true;
                    changed = true;
                }
                else share[i] = s;
            }
            if (!changed) break;
        }

        // Round to 4dp and fix residual on the largest free (unlocked) member,
        // or the largest overall if all are locked.
        int largest = 0;
        for (int i = 0; i < n; i++)
        {
            share[i] = (float)Math.Round(Math.Max(floor, share[i]), 4);
            if (share[i] > share[largest]) largest = i;
        }
        float total = share.Sum();
        share[largest] = (float)Math.Round(share[largest] + (budget - total), 4);
        if (share[largest] < floor) share[largest] = floor;
        return share;
    }

    /// <summary>
    /// Ref-sourced base: fold this base item's variants into its EXISTING
    /// reference pool, preserving every base item's ORIGINAL share.
    ///
    /// The pool's true vanilla shares come from vmangos_admin.og_reference_
    /// loot_template (the LootTuner baseline), NOT the live table — the live
    /// table may already be mid-lootify and reading it would compound the
    /// dilution across successive base items in the same pool.
    ///
    /// Model ("preserve each base's original share"):
    ///   - Each ORIGINAL pool member owns its og_ share (e.g. Cape 30, Cruel 20).
    ///   - Lootifying base B floors B to BASE_FLOOR_PCT (rare fallback); B's
    ///     variants + legendary split the rest of B's share, no member below
    ///     MEMBER_FLOOR_PCT (floor + redistribute within the family).
    ///   - Every OTHER original member keeps its og_ share untouched.
    ///   - Result still sums to 100 (og_ pool summed to 100; we only
    ///     redistribute within one member's slice).
    ///
    /// Idempotent + rollback-safe: og_ is the source of truth, so re-running
    /// recomputes identically, and rollback restores base shares from og_.
    /// </summary>
    private async Task<int> JoinExistingPool(MySqlConnector.MySqlConnection mangosConn,
        MySqlConnector.MySqlConnection adminConn,
        List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)>? trackingRows,
        dynamic refRow, int baseItemEntry, List<int> variantEntries, CommitRoll[] rolls, int creatureEntry)
    {
        int refEntry = (int)refRow.entry;
        int groupId = (int)refRow.groupid;
        int pMin = (int)refRow.patch_min;
        int pMax = (int)refRow.patch_max;

        // Original vanilla share of THIS base item, from the frozen baseline.
        float baseOrigShare = await GetOgRefShare(mangosConn, refEntry, groupId, baseItemEntry);
        if (baseOrigShare <= 0f)
        {
            // Base not in the og_ pool (or no baseline) — fall back to the live
            // share so we still do something sane rather than nothing.
            var liveChance = await mangosConn.ExecuteScalarAsync<float?>(
                "SELECT ChanceOrQuestChance FROM reference_loot_template WHERE entry=@E AND item=@I AND groupid=@G",
                new { E = refEntry, I = baseItemEntry, G = groupId });
            baseOrigShare = Math.Max(MIN_POOL_CHANCE, Math.Abs(liveChance ?? 100f));
        }

        // Overflow guard: never let this base's expansion push the group over
        // 100%. Tally every OTHER live member (this base + the new variants are
        // (re)written just below, so exclude them), and cap the base's slice to
        // whatever room is actually left. Keeps the pool valid even when earlier
        // members already over-fill it (stale data, orphaned prior variants).
        var tallyExclude = new List<int> { baseItemEntry };
        tallyExclude.AddRange(variantEntries);
        float liveOtherTotal = await SumRefGroupExcludingAsync(mangosConn, refEntry, groupId, tallyExclude);
        float room = Math.Max(0f, 100f - liveOtherTotal);
        if (room < BASE_FLOOR_PCT)
        {
            _poolWarnings?.Add(
                $"Item {baseItemEntry}: reference pool {refEntry} group {groupId} is already at {liveOtherTotal:0.##}% — no room to add its variants without exceeding 100%. Skipped; roll back or repair that pool first.");
            return 0;
        }

        // v6 split: base becomes a RARE fallback at BASE_FLOOR_PCT; the
        // variants + legendary take the rest of this base's original share.
        // The base floor is special — exempt from the member floor.
        float baseKeep = Math.Min(Math.Min(BASE_FLOOR_PCT, baseOrigShare), room); // never exceed the slice or the room
        float intendedVariantPool = Math.Max(0f, baseOrigShare - baseKeep);
        float variantPool = Math.Min(intendedVariantPool, room - baseKeep);
        if (variantPool + 1e-3f < intendedVariantPool)
            _poolWarnings?.Add(
                $"Item {baseItemEntry}: reference pool {refEntry} group {groupId} had only {room:0.##}% free, so its variant share was capped from {intendedVariantPool:0.##}% to {variantPool:0.##}% to keep the pool at 100%. Use Regenerate to reclaim space from earlier variants.");

        int memberCount = Math.Min(variantEntries.Count, rolls.Length);
        var rawWeights = new float[memberCount];
        for (int i = 0; i < memberCount; i++)
            rawWeights[i] = PoolWeight(rolls[i].budgetPct);

        // Floor + redistribute so nothing sits below MEMBER_FLOOR_PCT.
        var shares = DistributeWithFloor(rawWeights, variantPool, MEMBER_FLOOR_PCT);
        if (shares == null)
        {
            // Family can't seat every member at the floor within its share.
            // Warn and fall back to an unfloored proportional split (still valid,
            // just some members below the ideal floor) rather than corrupting.
            _poolWarnings?.Add(
                $"Item {baseItemEntry}: {memberCount} variants can't all reach {MEMBER_FLOOR_PCT}% within its {baseOrigShare:0.##}% pool share — using unfloored split. Lower Variants per Item for this pool.");
            shares = new float[memberCount];
            float rawSum = rawWeights.Sum();
            for (int i = 0; i < memberCount; i++)
                shares[i] = rawSum > 0 ? variantPool * (rawWeights[i] / rawSum) : variantPool / memberCount;
        }

        // Write the base item at its floor share (repairs any prior crush).
        await UpsertRefMember(mangosConn, refEntry, baseItemEntry, RoundChance(baseKeep), groupId, 1, pMin, pMax);
        await TrackLoot(adminConn, trackingRows, creatureEntry, "reference_loot_template",
            refEntry, baseItemEntry, "pool_base", baseOrigShare, baseKeep);

        int added = 0;
        for (int i = 0; i < memberCount; i++)
        {
            float share = Math.Max(MIN_POOL_CHANCE, RoundChance(shares[i]));
            await UpsertRefMember(mangosConn, refEntry, variantEntries[i], share, groupId, 1, pMin, pMax);
            await TrackLoot(adminConn, trackingRows, creatureEntry, "reference_loot_template",
                refEntry, variantEntries[i], "pool_joined", 0, share);
            added++;
        }

        return added;
    }

    /// <summary>Original share of an item in an og_reference_loot_template pool,
    /// or 0 if no baseline row exists. Read cross-schema from vmangos_admin.</summary>
    private async Task<float> GetOgRefShare(MySqlConnector.MySqlConnection mangosConn,
        int refEntry, int groupId, int item)
    {
        try
        {
            var c = await mangosConn.ExecuteScalarAsync<float?>(@"
                SELECT ChanceOrQuestChance FROM `vmangos_admin`.`og_reference_loot_template`
                WHERE entry = @E AND item = @I AND groupid = @G",
                new { E = refEntry, I = item, G = groupId });
            return c.HasValue ? Math.Abs(c.Value) : 0f;
        }
        catch
        {
            return 0f; // og_ table missing → caller falls back to live share
        }
    }

    private static float RoundChance(float v) => (float)Math.Round(v, 4);

    /// <summary>
    /// Insert-or-update one reference_loot_template member. Matches the
    /// codebase's check-then-write style (no reliance on a specific unique
    /// key), so re-committing renormalizes members in place instead of
    /// duplicating rows.
    /// </summary>
    private async Task UpsertRefMember(MySqlConnector.MySqlConnection mangosConn,
        int refEntry, int item, float chance, int groupId, int maxCount, int pMin, int pMax)
    {
        int exists = await mangosConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM reference_loot_template WHERE entry = @Entry AND item = @Item",
            new { Entry = refEntry, Item = item });

        if (exists > 0)
        {
            await mangosConn.ExecuteAsync(@"
                UPDATE reference_loot_template
                SET ChanceOrQuestChance = @Chance, groupid = @GroupId, maxcount = @MaxCount
                WHERE entry = @Entry AND item = @Item",
                new { Entry = refEntry, Item = item, Chance = chance, GroupId = groupId, MaxCount = maxCount });
        }
        else
        {
            await mangosConn.ExecuteAsync(@"
                INSERT INTO reference_loot_template (entry, item, ChanceOrQuestChance, groupid, mincountOrRef, maxcount, patch_min, patch_max)
                VALUES (@Entry, @Item, @Chance, @GroupId, 1, @MaxCount, @PMin, @PMax)",
                new { Entry = refEntry, Item = item, Chance = chance, GroupId = groupId, MaxCount = maxCount, PMin = pMin, PMax = pMax });
        }
    }

    /// <summary>
    /// Live sum of a reference pool group's member chances, optionally excluding
    /// the items we're about to (re)write. Used to compute how much headroom is
    /// left before a grouped pool would exceed 100%.
    /// </summary>
    private static async Task<float> SumRefGroupExcludingAsync(MySqlConnector.MySqlConnection mangosConn,
        int refEntry, int groupId, IEnumerable<int>? exclude)
    {
        var ex = (exclude ?? Enumerable.Empty<int>()).Distinct().ToArray();
        string sql = ex.Length > 0
            ? "SELECT COALESCE(SUM(ChanceOrQuestChance),0) FROM reference_loot_template WHERE entry=@E AND groupid=@G AND item NOT IN @Ex"
            : "SELECT COALESCE(SUM(ChanceOrQuestChance),0) FROM reference_loot_template WHERE entry=@E AND groupid=@G";
        var sum = await mangosConn.ExecuteScalarAsync<double?>(sql, new { E = refEntry, G = groupId, Ex = ex });
        return (float)(sum ?? 0d);
    }

    /// <summary>
    /// Returns every grouped loot pool (reference or creature, groupid &lt;&gt; 0)
    /// among the given ref entries / loot ids whose member chances sum above 100%.
    /// Group 0 rows are independent coin-flips (no sum constraint) and excluded.
    /// A grouped pool that sums over 100 is invalid: VMaNGOS' single-roll walk
    /// can never reach the members past the 100% mark, so they never drop.
    /// </summary>
    private static async Task<List<object>> FindOverfullGroupsAsync(MySqlConnector.MySqlConnection mangosConn,
        IEnumerable<int>? refEntries, IEnumerable<int>? lootIds)
    {
        const double LIMIT = 100.01d;
        var violations = new List<object>();

        var refs = (refEntries ?? Enumerable.Empty<int>()).Where(e => e > 0).Distinct().ToArray();
        if (refs.Length > 0)
        {
            var rows = await mangosConn.QueryAsync<dynamic>(@"
                SELECT entry, groupid, SUM(ChanceOrQuestChance) AS total
                FROM reference_loot_template
                WHERE entry IN @Refs AND groupid <> 0
                GROUP BY entry, groupid HAVING SUM(ChanceOrQuestChance) > @Limit",
                new { Refs = refs, Limit = LIMIT });
            foreach (var r in rows)
                violations.Add(new
                {
                    scope = "reference_loot_template",
                    entry = Convert.ToInt32(r.entry),
                    groupId = Convert.ToInt32(r.groupid),
                    total = Math.Round(Convert.ToDouble(r.total), 2)
                });
        }

        var loots = (lootIds ?? Enumerable.Empty<int>()).Where(e => e > 0).Distinct().ToArray();
        if (loots.Length > 0)
        {
            var rows = await mangosConn.QueryAsync<dynamic>(@"
                SELECT entry, groupid, SUM(ChanceOrQuestChance) AS total
                FROM creature_loot_template
                WHERE entry IN @Loots AND groupid <> 0
                GROUP BY entry, groupid HAVING SUM(ChanceOrQuestChance) > @Limit",
                new { Loots = loots, Limit = LIMIT });
            foreach (var r in rows)
                violations.Add(new
                {
                    scope = "creature_loot_template",
                    entry = Convert.ToInt32(r.entry),
                    groupId = Convert.ToInt32(r.groupid),
                    total = Math.Round(Convert.ToDouble(r.total), 2)
                });
        }

        return violations;
    }

    /// <summary>All reference pools a creature's loot table points at (via negative mincountOrRef).</summary>
    private static async Task<List<int>> GetReferencedPoolsAsync(MySqlConnector.MySqlConnection mangosConn, IEnumerable<int> lootIds)
    {
        var loots = lootIds.Where(e => e > 0).Distinct().ToArray();
        if (loots.Length == 0) return new List<int>();
        var rows = await mangosConn.QueryAsync<int>(
            "SELECT DISTINCT ABS(mincountOrRef) FROM creature_loot_template WHERE entry IN @L AND mincountOrRef < 0",
            new { L = loots });
        return rows.ToList();
    }

    /// <summary>
    /// Direct-drop base with no existing pool: mint a reference_loot_template
    /// group holding base + variants normalized to 100, then replace the base's
    /// direct creature_loot_template row with a single 100%/1 pointer to it.
    /// Structurally identical to a vanilla shared pool → renders in Instance Loot.
    /// Idempotent: a prior 'pool_created' row means the pointer/ref already
    /// exist; we reuse that ref entry and upsert members instead of minting again.
    /// </summary>
    private async Task<int> CreatePoolFromDirect(MySqlConnector.MySqlConnection mangosConn,
        MySqlConnector.MySqlConnection adminConn,
        List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)>? trackingRows,
        int lootId, dynamic directRow, int baseItemEntry, List<int> variantEntries, CommitRoll[] rolls, int creatureEntry)
    {
        int pMin = (int)directRow.patch_min;
        int pMax = (int)directRow.patch_max;
        float baseOrigChance = (float)directRow.chance;
        int baseMaxCount = (int)directRow.maxcount;

        // Reuse a previously-minted ref entry for this (lootId, base) if present.
        var priorCreate = await adminConn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT loot_entry FROM lootifier_loot_entries
            WHERE creature_entry = @CE AND item_entry = @Base AND action_type = 'pool_created'
              AND loot_table = 'creature_loot_template'
            ORDER BY id ASC LIMIT 1",
            new { CE = creatureEntry, Base = baseItemEntry });

        int refEntry;
        // No earlier create row for this creature: mint a fresh reference entry.
        if (priorCreate is null)
        {
            refEntry = await GetNextLootifierRefEntry(mangosConn);

            // Move the base row out of the creature's direct drops and record
            // its original chance so rollback can restore it exactly.
            await TrackLoot(adminConn, trackingRows, creatureEntry, "creature_loot_template",
                lootId, baseItemEntry, "pool_created", baseOrigChance, refEntry);

            await mangosConn.ExecuteAsync(@"
                DELETE FROM creature_loot_template
                WHERE entry = @LootId AND item = @Item AND mincountOrRef > 0",
                new { LootId = lootId, Item = baseItemEntry });

            // Pointer: 100% / 1 pick → group always resolves to one weighted item.
            await mangosConn.ExecuteAsync(@"
                INSERT IGNORE INTO creature_loot_template (entry, item, ChanceOrQuestChance, groupid, mincountOrRef, maxcount, patch_min, patch_max)
                VALUES (@LootId, @Item, 100, 0, @Ref, 1, @PMin, @PMax)",
                new { LootId = lootId, Item = baseItemEntry, Ref = -refEntry, PMin = pMin, PMax = pMax });
        }
        else
        {
            refEntry = (int)priorCreate.loot_entry < 0
                ? Math.Abs((int)priorCreate.loot_entry)
                : (int)priorCreate.loot_entry;
        }

        // v6 split for a minted pool: the pool's full budget is 100 (pointer is
        // 100%/1). Base becomes the rare fallback at BASE_FLOOR_PCT; variants +
        // legendary split the remaining 100 - base with the member floor.
        // On a fresh mint the group is empty so room == 100 and this is a no-op;
        // on reuse (re-commit) it caps against members left from a prior run so
        // the group can never exceed 100%.
        var tallyExclude = new List<int> { baseItemEntry };
        tallyExclude.AddRange(variantEntries);
        float liveOtherTotal = await SumRefGroupExcludingAsync(mangosConn, refEntry, POOL_GROUP_ID, tallyExclude);
        float room = Math.Max(0f, 100f - liveOtherTotal);
        if (room < BASE_FLOOR_PCT)
            _poolWarnings?.Add(
                $"Item {baseItemEntry}: minted pool {refEntry} is already at {liveOtherTotal:0.##}% — variant shares capped to keep it at 100%. Roll back and re-commit to rebuild cleanly.");

        float baseKeep = Math.Min(BASE_FLOOR_PCT, room);
        float variantPool = Math.Max(0f, room - baseKeep);

        int memberCount = Math.Min(variantEntries.Count, rolls.Length);
        var rawWeights = new float[memberCount];
        for (int i = 0; i < memberCount; i++)
            rawWeights[i] = PoolWeight(rolls[i].budgetPct);

        var shares = DistributeWithFloor(rawWeights, variantPool, MEMBER_FLOOR_PCT);
        if (shares == null)
        {
            _poolWarnings?.Add(
                $"Item {baseItemEntry}: {memberCount} variants can't all reach {MEMBER_FLOOR_PCT}% within a minted 100% pool — using unfloored split. Lower Variants per Item.");
            shares = new float[memberCount];
            float rawSum = rawWeights.Sum();
            for (int i = 0; i < memberCount; i++)
                shares[i] = rawSum > 0 ? variantPool * (rawWeights[i] / rawSum) : variantPool / memberCount;
        }

        // Base member at its floor (keeps its maxcount for stack size).
        await UpsertRefMember(mangosConn, refEntry, baseItemEntry, RoundChance(baseKeep),
            POOL_GROUP_ID, Math.Max(1, baseMaxCount), pMin, pMax);

        int added = 0;
        for (int i = 0; i < memberCount; i++)
        {
            float chance = Math.Max(MIN_POOL_CHANCE, RoundChance(shares[i]));
            await UpsertRefMember(mangosConn, refEntry, variantEntries[i], chance, POOL_GROUP_ID, 1, pMin, pMax);
            await TrackLoot(adminConn, trackingRows, creatureEntry, "reference_loot_template",
                refEntry, variantEntries[i], "pool_member", 0, chance);
            added++;
        }

        return added;
    }

    /// <summary>Next free reference_loot_template entry for a minted pool.</summary>
    private async Task<int> GetNextLootifierRefEntry(MySqlConnector.MySqlConnection mangosConn)
    {
        var maxRef = await mangosConn.ExecuteScalarAsync<int?>(
            "SELECT MAX(entry) FROM reference_loot_template WHERE entry >= @Start",
            new { Start = LOOTIFIER_REF_START });
        return maxRef.HasValue ? maxRef.Value + 1 : LOOTIFIER_REF_START;
    }

    // ══════════════════════════════════════════════════════════════
    //  ADDITIVE POOLS (tunable "% that anything drops", no dilution)
    //
    //  Builds ONE independent reference pool per creature holding all of its
    //  lootified variants (+ base at a 0.5% fallback), attached via a groupid=0
    //  pointer whose ChanceOrQuestChance IS the overall drop chance. Because the
    //  pointer is an independent (non-grouped) roll, this ADDS drops without
    //  touching the creature's existing loot. Mobs with no loot table get one
    //  minted. Everything is tracked so Rollback removes it cleanly.
    // ══════════════════════════════════════════════════════════════

    /// <summary>Next free minted creature loot_id (no-loot mobs in additive mode).</summary>
    private async Task<int> GetNextLootifierLootId(MySqlConnector.MySqlConnection mangosConn)
    {
        var maxId = await mangosConn.ExecuteScalarAsync<int?>(
            "SELECT MAX(entry) FROM creature_loot_template WHERE entry >= @Start",
            new { Start = LOOTIFIER_LOOTID_START });
        return maxId.HasValue ? maxId.Value + 1 : LOOTIFIER_LOOTID_START;
    }

    /// <summary>
    /// Creatures that already have an additive pool (idempotent re-runs skip them;
    /// rollback the creature first to rebuild). Keyed by creature_entry.
    /// </summary>
    private async Task<HashSet<int>> LoadAdditivizedCreatures(MySqlConnector.MySqlConnection adminConn)
    {
        var set = new HashSet<int>();
        if (!await TableExists(adminConn, "lootifier_loot_entries")) return set;
        var rows = await adminConn.QueryAsync<int>(
            "SELECT DISTINCT creature_entry FROM lootifier_loot_entries WHERE action_type = 'add_ptr'");
        foreach (var ce in rows) set.Add(ce);
        return set;
    }

    /// <summary>
    /// Build one additive pool for a creature from all of its collected variant
    /// sets. The base items are moved out of the creature's direct drops into the
    /// pool at BASE_FLOOR_PCT (0.5%); the variants split the remaining share to
    /// 100 (member-floored). The pool attaches as an independent groupid=0 pointer
    /// at poolDropPct — the tunable "% chance anything from the pool drops".
    /// Returns the number of pool members written.
    /// </summary>
    private async Task<int> BuildAdditivePool(MySqlConnector.MySqlConnection mangosConn,
        MySqlConnector.MySqlConnection adminConn,
        List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)>? trackingRows,
        int creatureEntry, int lootId, bool lootIdMinted,
        List<(int baseItemEntry, List<int> variantEntries, CommitRoll[] rolls)> batch, float poolDropPct)
    {
        if (batch.Count == 0) return 0;

        int refEntry = await GetNextLootifierRefEntry(mangosConn);

        // Record a minted loot_id so rollback can zero it again.
        if (lootIdMinted)
            await TrackLoot(adminConn, trackingRows, creatureEntry, "creature_template",
                lootId, 0, "add_lootid", 0f, lootId);

        var baseItems = batch.Select(b => b.baseItemEntry).Distinct().ToList();

        // Move each base out of the creature's DIRECT drops (recording its original
        // chance for rollback). Refs/shared pools are left untouched.
        int pMin = 0, pMax = 10;
        foreach (var baseEntry in baseItems)
        {
            var directRow = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT ChanceOrQuestChance AS chance, maxcount, patch_min, patch_max FROM creature_loot_template
                WHERE entry = @LootId AND item = @Item AND mincountOrRef > 0",
                new { LootId = lootId, Item = baseEntry });

            float origChance = 0f;
            if (directRow != null)
            {
                origChance = (float)directRow.chance;
                pMin = (int)directRow.patch_min;
                pMax = (int)directRow.patch_max;
                await mangosConn.ExecuteAsync(
                    "DELETE FROM creature_loot_template WHERE entry = @LootId AND item = @Item AND mincountOrRef > 0",
                    new { LootId = lootId, Item = baseEntry });
            }
            await TrackLoot(adminConn, trackingRows, creatureEntry, "creature_loot_template",
                lootId, baseEntry, "add_base", origChance, 0f);
        }

        // Gather variant weights across all this creature's base items.
        var variants = new List<(int entry, float weight)>();
        foreach (var b in batch)
        {
            int n = Math.Min(b.variantEntries.Count, b.rolls.Length);
            for (int i = 0; i < n; i++)
                variants.Add((b.variantEntries[i], PoolWeight(b.rolls[i].budgetPct)));
        }

        float baseFloorTotal = baseItems.Count * BASE_FLOOR_PCT;
        float variantBudget = Math.Max(0f, 100f - baseFloorTotal);

        var rawWeights = variants.Select(v => v.weight).ToArray();
        var shares = DistributeWithFloor(rawWeights, variantBudget, MEMBER_FLOOR_PCT);
        if (shares == null)
        {
            _poolWarnings?.Add(
                $"Creature {creatureEntry}: {variants.Count} variants can't all reach {MEMBER_FLOOR_PCT}% in the additive pool — using unfloored split. Lower Variants per Item.");
            shares = new float[variants.Count];
            float rawSum = rawWeights.Sum();
            for (int i = 0; i < variants.Count; i++)
                shares[i] = rawSum > 0 ? variantBudget * (rawWeights[i] / rawSum) : variantBudget / Math.Max(1, variants.Count);
        }

        int added = 0;

        // Base members at the rare fallback floor.
        foreach (var baseEntry in baseItems)
        {
            await UpsertRefMember(mangosConn, refEntry, baseEntry, RoundChance(BASE_FLOOR_PCT), POOL_GROUP_ID, 1, pMin, pMax);
            await TrackLoot(adminConn, trackingRows, creatureEntry, "reference_loot_template",
                refEntry, baseEntry, "add_member", 0f, BASE_FLOOR_PCT);
            added++;
        }

        // Variant members.
        for (int i = 0; i < variants.Count; i++)
        {
            float chance = Math.Max(MIN_POOL_CHANCE, RoundChance(shares[i]));
            await UpsertRefMember(mangosConn, refEntry, variants[i].entry, chance, POOL_GROUP_ID, 1, pMin, pMax);
            await TrackLoot(adminConn, trackingRows, creatureEntry, "reference_loot_template",
                refEntry, variants[i].entry, "add_member", 0f, chance);
            added++;
        }

        // Independent pointer: groupid=0 → its own roll at poolDropPct. The item
        // field carries the first base entry (its direct slot is now free), matching
        // the direct-mint pointer convention and keeping (entry,item) unique.
        int pointerItem = baseItems[0];
        await mangosConn.ExecuteAsync(@"
            INSERT INTO creature_loot_template (entry, item, ChanceOrQuestChance, groupid, mincountOrRef, maxcount, patch_min, patch_max)
            VALUES (@LootId, @Item, @Chance, 0, @Ref, 1, @PMin, @PMax)
            ON DUPLICATE KEY UPDATE ChanceOrQuestChance = @Chance, groupid = 0, mincountOrRef = @Ref, maxcount = 1",
            new { LootId = lootId, Item = pointerItem, Chance = RoundChance(Math.Clamp(poolDropPct, 0f, 100f)), Ref = -refEntry, PMin = pMin, PMax = pMax });
        await TrackLoot(adminConn, trackingRows, creatureEntry, "creature_loot_template",
            lootId, pointerItem, "add_ptr", 0f, poolDropPct);

        return added;
    }

    // ══════════════════════════════════════════════════════════════
    //  ANALYSIS
    // ══════════════════════════════════════════════════════════════

    private dynamic AnalyzeItemStats(dynamic item)
    {
        var stats = new List<object>();
        int totalStats = 0;
        float weightedBudget = 0;
        var presentTypes = new HashSet<int>();

        for (int i = 1; i <= 10; i++)
        {
            int statType = GetPropInt(item, $"stat_type{i}");
            int statValue = GetPropInt(item, $"stat_value{i}");
            if (statType > 0 && statValue != 0)
            {
                string name = STAT_NAMES.GetValueOrDefault(statType, $"Type{statType}");
                float weight = DEFAULT_STAT_WEIGHTS.GetValueOrDefault(statType, 1.0f);
                stats.Add(new { slot = i, statType, statValue, name, weight, weightedCost = statValue * weight });
                totalStats += Math.Abs(statValue);
                weightedBudget += Math.Abs(statValue) * weight;
                presentTypes.Add(statType);
            }
        }

        string family = "hybrid";
        if (presentTypes.IsSubsetOf(STAT_FAMILIES["physical"])) family = "physical";
        else if (presentTypes.IsSubsetOf(STAT_FAMILIES["caster"])) family = "caster";

        // Analyze spell effects
        var spellEffects = new List<SpellEffectInfo>();
        for (int i = 1; i <= 5; i++)
        {
            int spellId = GetPropInt(item, $"spellid_{i}");
            int spellTrigger = GetPropInt(item, $"spelltrigger_{i}");
            if (spellId > 0)
            {
                string triggerName = spellTrigger switch
                {
                    SPELLTRIGGER_USE => "Use",
                    SPELLTRIGGER_EQUIP => "Equip",
                    SPELLTRIGGER_CHANCE_ON_HIT => "Chance on Hit",
                    _ => $"Trigger {spellTrigger}"
                };
                spellEffects.Add(new SpellEffectInfo
                {
                    slot = i,
                    spellId = spellId,
                    triggerType = spellTrigger,
                    triggerName = triggerName
                });
            }
        }

        return new
        {
            stats,
            totalStats,
            weightedBudget,
            detectedFamily = family,
            presentStatTypes = presentTypes.ToArray(),
            spellEffects,
            hasSpellEffects = spellEffects.Count > 0
        };
    }

    private async Task<List<dynamic>> EnrichLootRows(MySqlConnector.MySqlConnection conn, List<LootifierLootRow> rows)
    {
        var result = new List<dynamic>();
        foreach (var row in rows)
        {
            var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT entry, name, quality, class, subclass, inventory_type, display_id,
                       stat_type1, stat_value1, stat_type2, stat_value2, stat_type3, stat_value3,
                       stat_type4, stat_value4, stat_type5, stat_value5,
                       stat_type6, stat_value6, stat_type7, stat_value7, stat_type8, stat_value8,
                       stat_type9, stat_value9, stat_type10, stat_value10,
                       spellid_1, spelltrigger_1, spellid_2, spelltrigger_2,
                       spellid_3, spelltrigger_3, spellid_4, spelltrigger_4,
                       spellid_5, spelltrigger_5,
                       required_level, item_level
                FROM item_template WHERE entry = @E
                    AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
                new { E = row.item });

            var analysis = item != null ? AnalyzeItemStats(item) : null;

            result.Add(new
            {
                lootEntry = row.lootEntry,
                itemEntry = row.item,
                chance = row.chance,
                groupId = row.groupId,
                mincountOrRef = row.mincountOrRef,
                maxcount = row.maxcount,
                patchMin = row.patchMin,
                patchMax = row.patchMax,
                itemName = item != null ? (string)item.name : $"Item #{row.item}",
                quality = item != null ? (int)item.quality : 0,
                itemClass = item != null ? (int)item.@class : 0,
                equippable = item != null && IsEquippableGear(item),
                // Our own output. The UI greys these out so a generated variant
                // can't be ticked as a base on the single-creature page.
                isGenerated = IsGeneratedEntry(row.item),
                displayId = item != null ? (uint)item.display_id : 0u,
                requiredLevel = item != null ? (int)item.required_level : 0,
                itemLevel = item != null ? (int)item.item_level : 0,
                totalStats = analysis?.totalStats ?? 0,
                weightedBudget = analysis?.weightedBudget ?? 0f,
                detectedFamily = analysis?.detectedFamily ?? "unknown",
                stats = analysis?.stats ?? new List<object>(),
                hasSpellEffects = analysis?.hasSpellEffects ?? false,
                spellEffects = analysis?.spellEffects ?? new List<SpellEffectInfo>()
            });
        }
        return result;
    }

    // ══════════════════════════════════════════════════════════════
    //  INFRASTRUCTURE
    // ══════════════════════════════════════════════════════════════

    private async Task EnsureTrackingTables(MySqlConnector.MySqlConnection adminConn)
    {
        await adminConn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS lootifier_generated_items (
                id INT AUTO_INCREMENT PRIMARY KEY,
                generated_entry INT NOT NULL,
                base_entry INT NOT NULL,
                creature_entry INT NOT NULL,
                budget_pct FLOAT DEFAULT 0,
                tier_name VARCHAR(64) DEFAULT '',
                created_at DATETIME NOT NULL,
                INDEX idx_creature (creature_entry),
                INDEX idx_generated (generated_entry)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

        await adminConn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS lootifier_loot_entries (
                id INT AUTO_INCREMENT PRIMARY KEY,
                creature_entry INT NOT NULL,
                loot_table VARCHAR(64) NOT NULL,
                loot_entry INT NOT NULL,
                item_entry INT NOT NULL,
                action_type VARCHAR(32) NOT NULL,
                original_chance FLOAT DEFAULT 0,
                new_chance FLOAT DEFAULT 0,
                created_at DATETIME NOT NULL,
                INDEX idx_creature (creature_entry),
                INDEX idx_pair (loot_entry, item_entry),
                INDEX idx_item (item_entry)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

        // Existing installs were created with action_type VARCHAR(16), which is too
        // narrow for newer action names and made MariaDB (strict mode) throw
        // "Data too long for column 'action_type'" mid-commit. CREATE TABLE IF NOT
        // EXISTS won't alter an existing table, so widen it explicitly — idempotent,
        // and skipped entirely when it's already wide enough.
        var actionWidth = await adminConn.ExecuteScalarAsync<int?>(@"
            SELECT CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'lootifier_loot_entries'
              AND COLUMN_NAME = 'action_type'");
        if (actionWidth.HasValue && actionWidth.Value < 32)
        {
            await adminConn.ExecuteAsync(
                "ALTER TABLE lootifier_loot_entries MODIFY action_type VARCHAR(32) NOT NULL");
        }
    }

    private async Task<int> GetNextLootifierId(MySqlConnector.MySqlConnection adminConn)
    {
        int fromTracking = LOOTIFIER_ID_START;
        if (await TableExists(adminConn, "lootifier_generated_items"))
        {
            var maxTracked = await adminConn.ExecuteScalarAsync<int?>("SELECT MAX(generated_entry) FROM lootifier_generated_items");
            if (maxTracked.HasValue)
                fromTracking = maxTracked.Value + 1;
        }

        // Also check item_template directly in case orphaned entries exist from failed commits
        using var mangosConn = _db.Mangos();
        var maxInItems = await mangosConn.ExecuteScalarAsync<int?>(
            "SELECT MAX(entry) FROM item_template WHERE entry >= @Start",
            new { Start = LOOTIFIER_ID_START });
        int fromItems = maxInItems.HasValue ? maxInItems.Value + 1 : LOOTIFIER_ID_START;

        return Math.Max(fromTracking, fromItems);
    }

    private async Task<bool> TableExists(MySqlConnector.MySqlConnection conn, string tableName)
    {
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @T",
            new { T = tableName }) > 0;
    }

    // ── Disenchant column ──
    // Legendary variants should lose disenchant, but the column name is NOT stable
    // across VMaNGOS schema revisions (DisenchantID / disenchant_id / DisenchantId)
    // and some builds don't have it at all. Hardcoding "DisenchantID" threw
    // "Unknown column 'DisenchantID' in 'SET'" and 500'd the whole commit. Resolve
    // it from the live schema instead, and simply skip the update when absent.
    // Cached per request (controllers are request-scoped) so a batch of thousands
    // of inserts costs one information_schema lookup.
    private string? _disenchantCol;
    private bool _disenchantResolved;

    private async Task<string?> ResolveDisenchantColumn(MySqlConnector.MySqlConnection conn)
    {
        if (_disenchantResolved) return _disenchantCol;
        _disenchantCol = await conn.ExecuteScalarAsync<string?>(@"
            SELECT COLUMN_NAME FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'item_template'
              AND COLUMN_NAME IN ('DisenchantID', 'disenchant_id', 'DisenchantId')
            LIMIT 1");
        _disenchantResolved = true;
        return _disenchantCol;
    }

    /// <summary>Clear disenchant on a legendary variant, if the schema has the column.</summary>
    private async Task ClearDisenchant(MySqlConnector.MySqlConnection conn, int entry)
    {
        var col = await ResolveDisenchantColumn(conn);
        if (col == null) return;
        await conn.ExecuteAsync(
            $"UPDATE item_template SET `{col}` = 0 WHERE entry = @Entry", new { Entry = entry });
    }

    private int GetPropInt(dynamic obj, string name)
    {
        var dict = obj as IDictionary<string, object>;
        if (dict != null && dict.TryGetValue(name, out var val))
            return val == null ? 0 : Convert.ToInt32(val);
        return 0;
    }
}

// ══════════════════════════════════════════════════════════════
//  INTERNAL TYPES
// ══════════════════════════════════════════════════════════════

internal class SpellEffectInfo
{
    public int slot { get; set; }
    public int spellId { get; set; }
    public int triggerType { get; set; }
    public string triggerName { get; set; } = "";
}

internal class VariantData
{
    public string name { get; set; } = "";
    public float budgetPct { get; set; }
    public string tierLabel { get; set; } = "";
    public string tierPosition { get; set; } = "suffix";
    public List<StatRoll> stats { get; set; } = new();
}

internal class StatRoll
{
    public int statType { get; set; }
    public int statValue { get; set; }
    public string name { get; set; } = "";
}

internal class TierRange
{
    public float minPct { get; set; }
    public float maxPct { get; set; }
    public string label { get; set; } = "";
    public string position { get; set; } = "suffix";
    public int slots { get; set; } = 0;   // explicit count; 0 = auto
    // Explicit stat boost over base for this tier, as a % of the base item's
    // weighted budget (e.g. 20-30 = this tier rolls 20-30% stronger than base).
    // Null = legacy behaviour: minPct/maxPct are read as percentile positions
    // inside the single global Budget Ceiling headroom.
    public float? minBoostPct { get; set; }
    public float? maxBoostPct { get; set; }
    public bool HasBoost => minBoostPct.HasValue && maxBoostPct.HasValue;
}

// ══════════════════════════════════════════════════════════════
//  DTOs
// ══════════════════════════════════════════════════════════════

public class LootifierLootRow
{
    public int lootEntry { get; set; }
    public int item { get; set; }
    public float chance { get; set; }
    public int groupId { get; set; }
    public int mincountOrRef { get; set; }
    public int maxcount { get; set; }
    public int patchMin { get; set; }
    public int patchMax { get; set; }
}

public class RulesetDto
{
    public float budgetCeilingPct { get; set; } = 35;
    public int variantsPerItem { get; set; } = 9;    // default tier slots are 5 + 2 + 2
    public bool allowNewAffixes { get; set; } = true;
    public int maxAffixCountChange { get; set; } = 1;
    public string dropChanceStrategy { get; set; } = "preserve";  // "preserve" | "additive"
    public float poolDropChancePct { get; set; } = 100f;          // additive: overall % that the pool drops anything
    public NamingTierDto[]? namingTiers { get; set; }
    // Value tuning (mirrors Quest/Crafting lootifiers)
    // RETIRED. Was a master multiplier over every Gold +%, which meant the number
    // on a tier row was never the number applied. Kept only so an older payload
    // still deserialises; it is not read anywhere.
    public float goldValueScalePct { get; set; } = 100f;
    public float legendaryGoldBumpPct { get; set; } = 500f; // legendary price bump above base (%); 500 = the old x6 behaviour
    public float legendaryDpsBumpPct { get; set; } = 30f;   // legendary weapon DAMAGE bump above base (%); nominal — vanilla legendaries were hand-tuned
    // Legendary system
    public bool generateLegendary { get; set; } = false;
    public float legendaryDropPct { get; set; } = 0.2f;
    // Legendary strength, in the SAME unit as a naming tier's Boost %: how far
    // above the base item's weighted stat budget it rolls. Was a hardcoded 1.50f
    // (a flat +50%), which sat INSIDE the default top tier's 40-60 band with no
    // way to see it or change it. Defaults now put it clearly above the ladder.
    public float legendaryBoostMinPct { get; set; } = 55f;
    public float legendaryBoostMaxPct { get; set; } = 75f;
    public string legendarySuffixMelee { get; set; } = "of Destruction";
    public string legendarySuffixRanged { get; set; } = "of the Hunt";
    public string legendarySuffixCaster { get; set; } = "of Arcana";
    // How legendaries are named: "named" = boss/source-named ("Edwin VanCleef's
    // Blade"); "suffix" = generic family suffix ("Blade of Destruction"). Shared
    // drops (no single source) always fall back to the suffix regardless.
    public string legendaryNameStyle { get; set; } = "named";
    public int legendaryItemEntry { get; set; } = 0; // Single mode: user-chosen item. 0 = random.
}

public class NamingTierDto
{
    public float minPct { get; set; }
    public float maxPct { get; set; }
    public string? label { get; set; }
    public string? position { get; set; }
    public float? goldBumpPct { get; set; }   // gold price bump above base (%) for this tier; null = legacy budget curve
    public float? dpsBumpPct { get; set; }     // weapon DAMAGE bump above base (%) for this tier; null = damage copied verbatim
    public int slots { get; set; } = 0;        // explicit variant count for this tier; 0 = auto (formula fills to variantsPerItem)
    // Per-tier stat boost over base, as a % of the base's weighted budget.
    // Both set => this tier ignores budgetCeilingPct and rolls in [min,max]% over
    // base. Either null => legacy percentile-of-ceiling behaviour for this tier.
    public float? minBoostPct { get; set; }
    public float? maxBoostPct { get; set; }
}

public class GenerateRequest
{
    public int creatureEntry { get; set; }
    public int[] itemEntries { get; set; } = Array.Empty<int>();
    public RulesetDto? ruleset { get; set; }
}

public class BatchRequest
{
    public int[]? qualities { get; set; }
    public int levelMin { get; set; }
    public int levelMax { get; set; }
    public int[]? creatureRanks { get; set; }
    public int[]? mapIds { get; set; }
    public int[]? zoneIds { get; set; }   // WorldMapArea areaID — outdoor zone spawn filter
    public RulesetDto? ruleset { get; set; }
}

public class BatchCommitRequest
{
    public BatchCreatureGroup[] creatures { get; set; } = Array.Empty<BatchCreatureGroup>();
    public RulesetDto? ruleset { get; set; }
    public bool regenerate { get; set; } = false;   // replace existing variants in place (same tier), never orphan
}

public class BatchSampleRequest
{
    public int creatureEntry { get; set; }
    public int[] itemEntries { get; set; } = Array.Empty<int>();
    public RulesetDto? ruleset { get; set; }
}

public class BatchCreatureGroup
{
    public int creatureEntry { get; set; }
    public int[] itemEntries { get; set; } = Array.Empty<int>();
}

public class CommitRequest
{
    public int creatureEntry { get; set; }
    public CommitItemGroup[] variants { get; set; } = Array.Empty<CommitItemGroup>();
    public RulesetDto? ruleset { get; set; }
    public bool regenerate { get; set; } = false;   // replace existing variants in place (same tier), never orphan
}

public class CommitItemGroup
{
    public int baseItemEntry { get; set; }
    public CommitRoll[] rolls { get; set; } = Array.Empty<CommitRoll>();
}

public class CommitRoll
{
    public float budgetPct { get; set; }
    public string? tierLabel { get; set; }
    public string? tierPosition { get; set; }
    public CommitStat[] stats { get; set; } = Array.Empty<CommitStat>();
}

public class CommitStat
{
    public int statType { get; set; }
    public int statValue { get; set; }
}

public class RollbackRequest
{
    public int creatureEntry { get; set; }
}