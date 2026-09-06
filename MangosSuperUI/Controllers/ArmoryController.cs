// ArmoryController.cs
//
// The Armory: look up any character on the realm — a real player or a bot, they are the same
// `characters` row — and see what they are wearing, on a 3D character in their own race and
// gender, laid out slot by slot like the classic armory.
//
// Endpoints:
//   GET /Armory                         -> the page
//   GET /Armory/Search?q=               -> autocomplete (3+ chars) over character names
//   GET /Armory/Character?guid=         -> identity, guild, account, the 19 equipment slots with
//                                          item_template detail + icon, and the race/gender GLB
//                                          the viewer needs
//
// Everything visual is reused: the character GLB comes from CharacterModelService (the same one
// the Items page and both forges mount), and the pieces dress through /Items/ItemDressing inside
// the viewer's equipMultiple — so a forged item renders here exactly as it does everywhere else.

using Dapper;
using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using MangosSuperUI.Models;

namespace MangosSuperUI.Controllers;

public class ArmoryController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly CharacterModelService _characterModels;
    private readonly CharacterSkinCompositor _skins;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ArmoryController> _logger;

    public ArmoryController(ConnectionFactory db, DbcService dbc, CharacterModelService characterModels,
        CharacterSkinCompositor skins, IWebHostEnvironment env, ILogger<ArmoryController> logger)
    {
        _db = db; _dbc = dbc; _characterModels = characterModels; _skins = skins; _env = env; _logger = logger;
    }

    [HttpGet]
    public IActionResult Index(int guid = 0)
    {
        ViewBag.Guid = guid;
        return View();
    }

    /// <summary>GET /Armory/Search?q= — up to 20 characters whose name starts with the query
    /// (3+ characters). Players and bots alike; the account name tells them apart at a glance.</summary>
    [HttpGet]
    public async Task<IActionResult> Search(string? q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Json(Array.Empty<object>());
        try
        {
            using var conn = _db.Characters();
            var rows = await conn.QueryAsync<SearchRow>(
                @"SELECT c.guid, c.name, c.level, c.race, c.class AS classId, c.gender, c.online,
                         a.username AS accountName
                  FROM characters c
                  LEFT JOIN realmd.account a ON a.id = c.account
                  WHERE c.name LIKE @term
                  ORDER BY c.online DESC, c.level DESC, c.name
                  LIMIT 20",
                new { term = q.Trim() + "%" });
            return Json(rows.Select(r => new
            {
                r.Guid, r.Name, r.Level, r.Online, r.AccountName,
                race = RaceName(r.Race), className = ClassName(r.ClassId),
                gender = r.Gender == 1 ? "Female" : "Male",
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Armory: search failed for {Query}", q);
            return Json(Array.Empty<object>());
        }
    }

    /// <summary>GET /Armory/Character?guid= — the character, its equipment and the viewer inputs.</summary>
    [HttpGet]
    public async Task<IActionResult> Character(int guid)
    {
        if (guid <= 0) return Json(new { ok = false, error = "No character selected." });
        try
        {
            using var charConn = _db.Characters();
            var c = await charConn.QueryFirstOrDefaultAsync<CharacterRow>(
                @"SELECT c.guid, c.name, c.level, c.race, c.class AS classId, c.gender, c.online,
                         c.money, c.played_time_total AS playedTotal, c.logout_time AS logoutTime,
                         a.username AS accountName
                  FROM characters c
                  LEFT JOIN realmd.account a ON a.id = c.account
                  WHERE c.guid = @guid",
                new { guid });
            if (c is null) return Json(new { ok = false, error = $"No character with guid {guid}." });

            // Appearance: the character-creation choices. vmangos stores them as their own columns;
            // a schema without them just falls back to the race/gender template.
            // vmangos column names (Player.cpp: "skin, face, hair_style, hair_color, facial_hair").
            AppearanceRow? look = null; string? lookError = null;
            try
            {
                look = await charConn.QueryFirstOrDefaultAsync<AppearanceRow>(
                    @"SELECT skin, face, hair_style AS hairStyle, hair_color AS hairColor, facial_hair AS facialHair
                      FROM characters WHERE guid = @guid", new { guid });
            }
            catch (Exception ex)
            {
                lookError = ex.Message;
                _logger.LogWarning(ex, "Armory: appearance columns unavailable for {Guid} — template look", guid);
            }

            string? guildName = null; int? guildRank = null;
            try
            {
                var g = await charConn.QueryFirstOrDefaultAsync<GuildRow>(
                    @"SELECT g.name AS guildName, gm.rank AS guildRank
                      FROM guild_member gm JOIN guild g ON g.guild_id = gm.guild_id
                      WHERE gm.guid = @guid", new { guid });
                guildName = g?.GuildName; guildRank = g?.GuildRank;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Armory: guild lookup failed for {Guid}", guid); }

            // Equipment: bag 0, slots 0..18 (see BotsController.Inventory for the layout).
            var inv = (await charConn.QueryAsync<InventoryRow>(
                @"SELECT ci.slot, ci.item_guid AS itemGuid, ci.item_id AS itemEntry,
                         COALESCE(ii.count, 1) AS stackCount, ii.enchantments
                  FROM character_inventory ci
                  LEFT JOIN item_instance ii ON ii.guid = ci.item_guid
                  WHERE ci.guid = @guid AND ci.bag = 0 AND ci.slot <= 18
                  ORDER BY ci.slot", new { guid })).ToList();

            var templates = new Dictionary<int, ItemRow>();
            if (inv.Count > 0)
            {
                using var mangosConn = _db.Mangos();
                var entries = inv.Select(i => i.ItemEntry).Distinct().ToArray();
                var items = await mangosConn.QueryAsync<ItemRow>(
                    @"SELECT entry, name, quality, class AS classId, subclass, inventory_type AS inventoryType,
                             item_level AS itemLevel, required_level AS requiredLevel, display_id AS displayId,
                             armor, dmg_min1 AS dmgMin1, dmg_max1 AS dmgMax1, delay, bonding, description,
                             stat_type1, stat_value1, stat_type2, stat_value2, stat_type3, stat_value3,
                             stat_type4, stat_value4, stat_type5, stat_value5, stat_type6, stat_value6,
                             stat_type7, stat_value7, stat_type8, stat_value8, stat_type9, stat_value9,
                             stat_type10, stat_value10,
                             holy_res AS holyRes, fire_res AS fireRes, nature_res AS natureRes,
                             frost_res AS frostRes, shadow_res AS shadowRes, arcane_res AS arcaneRes,
                             max_durability AS maxDurability, set_id AS setId
                      FROM item_template WHERE entry IN @entries AND patch = 0", new { entries });
                foreach (var it in items) templates[it.Entry] = it;
            }

            var slots = new List<object>();
            foreach (var row in inv)
            {
                templates.TryGetValue(row.ItemEntry, out var t);
                uint displayId = (uint)(t?.DisplayId ?? 0);
                string? icon = null;
                try { if (displayId > 0) icon = _dbc.GetItemIconPath(displayId); } catch { }
                slots.Add(new
                {
                    slot = row.Slot,
                    slotName = SlotName(row.Slot),
                    itemGuid = row.ItemGuid,
                    entry = row.ItemEntry,
                    name = t?.Name ?? $"item {row.ItemEntry}",
                    quality = t?.Quality ?? 1,
                    itemLevel = t?.ItemLevel ?? 0,
                    requiredLevel = t?.RequiredLevel ?? 0,
                    inventoryType = t?.InventoryType ?? 0,
                    inventoryTypeName = InventoryTypeName(t?.InventoryType ?? 0),
                    itemClass = t?.ClassId ?? 0,
                    subclass = t?.Subclass ?? 0,
                    displayId,
                    icon,
                    armor = t?.Armor ?? 0,
                    dmgMin = t?.DmgMin1 ?? 0f,
                    dmgMax = t?.DmgMax1 ?? 0f,
                    delay = t?.Delay ?? 0,
                    bonding = t?.Bonding ?? 0,
                    description = t?.Description,
                    setId = t?.SetId ?? 0,
                    stats = t is null ? Array.Empty<object>() : Stats(t),
                    resistances = t is null ? Array.Empty<object>() : Resistances(t),
                    stackCount = row.StackCount,
                    hasEnchant = !string.IsNullOrWhiteSpace(row.Enchantments) && row.Enchantments.Trim().Any(ch => ch != '0' && ch != ' '),
                });
            }

            string race = PreviewRaceName(c.Race), gender = c.Gender == 1 ? "Female" : "Male";
            string? glbUrl = null, skinUrl = null;
            try
            {
                glbUrl = await _characterModels.EnsureCharacterGlbAsync(race, gender);
                skinUrl = _characterModels.GetSkinPngUrl(race, gender);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Armory: character model for {Race} {Gender} unavailable", race, gender); }

            // This character's own look: their skin tone, face, hair colour and beard composited into
            // the body atlas, the hair-colour sheet for the hair geosets, and which hair geoset their
            // style is. Everything falls back to the template the viewer already has.
            object? appearance = null;
            if (look is not null)
            {
                try
                {
                    string dir = Path.Combine(_env.WebRootPath, "character_textures", "armory");
                    Directory.CreateDirectory(dir);
                    string key = $"{race}{gender}_s{look.Skin}_f{look.Face}_h{look.HairStyle}_c{look.HairColor}_b{look.FacialHair}";
                    string skinFile = CacheVersionRegistry.MakeVersioned(key + ".png", CacheVersionRegistry.SkinPngVersion);
                    string skinPath = Path.Combine(dir, skinFile);
                    string? hairPartial = null;
                    if (!System.IO.File.Exists(skinPath))
                    {
                        var png = _skins.ComposeCharacterSkin(race, gender, look.Skin, look.Face, look.HairStyle, look.HairColor, look.FacialHair, out hairPartial);
                        if (png is { Length: > 0 }) await System.IO.File.WriteAllBytesAsync(skinPath, png);
                    }
                    else
                    {
                        // Cached atlas: still need the hair sheet name, which is a pure DBC lookup.
                        _skins.ComposeCharacterSkin(race, gender, look.Skin, look.Face, look.HairStyle, look.HairColor, look.FacialHair, out hairPartial);
                    }

                    string? hairUrl = null;
                    if (!string.IsNullOrEmpty(hairPartial))
                    {
                        string hairFile = CacheVersionRegistry.MakeVersioned($"{race}{gender}_hair_h{look.HairStyle}_c{look.HairColor}.png", CacheVersionRegistry.SkinPngVersion);
                        string hairPath = Path.Combine(dir, hairFile);
                        if (!System.IO.File.Exists(hairPath))
                        {
                            var blp = _skins.ResolveCharacterBlp(hairPartial, race, gender);
                            var png = blp is null ? null : _skins.BlpToPng(blp);
                            if (png is { Length: > 0 }) await System.IO.File.WriteAllBytesAsync(hairPath, png);
                        }
                        if (System.IO.File.Exists(hairPath)) hairUrl = $"/character_textures/armory/{hairFile}";
                    }

                    int hairGeosetId = _dbc.CharacterHairGeosets.TryGetValue(((uint)c.Race, (uint)c.Gender, look.HairStyle), out int g) ? g : 0;
                    appearance = new
                    {
                        skin = look.Skin, face = look.Face, hairStyle = look.HairStyle, hairColor = look.HairColor, facialHair = look.FacialHair,
                        skinUrl = System.IO.File.Exists(skinPath) ? $"/character_textures/armory/{skinFile}" : null,
                        hairUrl,
                        hairGeosetId,
                    };
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Armory: appearance composite failed for {Guid}", guid); }
            }

            return Json(new
            {
                ok = true,
                character = new
                {
                    c.Guid, c.Name, c.Level, c.Online, c.AccountName,
                    raceId = c.Race, race = RaceName(c.Race), previewRace = race,
                    classId = c.ClassId, className = ClassName(c.ClassId),
                    genderId = c.Gender, gender,
                    guildName, guildRank,
                    money = c.Money, playedTotal = c.PlayedTotal, logoutTime = c.LogoutTime,
                },
                slots,
                viewer = new { glbUrl, skinUrl },
                appearance,
                appearanceError = appearance is null ? (lookError ?? "no appearance row — template look") : null,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Armory: character {Guid} failed", guid);
            return Json(new { ok = false, error = ex.Message });
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static object[] Stats(ItemRow t)
    {
        var pairs = new (int Type, int Value)[]
        {
            (t.Stat_type1, t.Stat_value1), (t.Stat_type2, t.Stat_value2), (t.Stat_type3, t.Stat_value3),
            (t.Stat_type4, t.Stat_value4), (t.Stat_type5, t.Stat_value5), (t.Stat_type6, t.Stat_value6),
            (t.Stat_type7, t.Stat_value7), (t.Stat_type8, t.Stat_value8), (t.Stat_type9, t.Stat_value9),
            (t.Stat_type10, t.Stat_value10),
        };
        return pairs.Where(p => p.Value != 0)
            .Select(p => (object)new { type = p.Type, name = StatName(p.Type), value = p.Value })
            .ToArray();
    }

    private static object[] Resistances(ItemRow t)
    {
        var list = new List<object>();
        void Add(string name, int v) { if (v != 0) list.Add(new { name, value = v }); }
        Add("Holy", t.HolyRes); Add("Fire", t.FireRes); Add("Nature", t.NatureRes);
        Add("Frost", t.FrostRes); Add("Shadow", t.ShadowRes); Add("Arcane", t.ArcaneRes);
        return list.ToArray();
    }

    private static string StatName(int type) => type switch
    {
        0 => "Mana", 1 => "Health", 3 => "Agility", 4 => "Strength", 5 => "Intellect", 6 => "Spirit", 7 => "Stamina",
        12 => "Defense", 13 => "Dodge", 14 => "Parry", 15 => "Block", 16 => "Melee Hit", 17 => "Ranged Hit", 18 => "Spell Hit",
        19 => "Melee Crit", 20 => "Ranged Crit", 21 => "Spell Crit", 31 => "Hit", 32 => "Crit",
        35 => "Resilience", 36 => "Haste", 37 => "Expertise", 38 => "Attack Power", 39 => "Ranged Attack Power",
        41 => "Healing", 42 => "Spell Damage", 43 => "Mana Regen", 44 => "Armor Penetration", 45 => "Spell Power",
        _ => $"stat {type}",
    };

    private static string SlotName(int slot) => slot switch
    {
        0 => "Head", 1 => "Neck", 2 => "Shoulders", 3 => "Shirt", 4 => "Chest", 5 => "Waist", 6 => "Legs",
        7 => "Feet", 8 => "Wrists", 9 => "Hands", 10 => "Ring 1", 11 => "Ring 2", 12 => "Trinket 1",
        13 => "Trinket 2", 14 => "Back", 15 => "Main Hand", 16 => "Off Hand", 17 => "Ranged", 18 => "Tabard",
        _ => $"slot {slot}",
    };

    private static string InventoryTypeName(int t) => t switch
    {
        1 => "Head", 2 => "Neck", 3 => "Shoulder", 4 => "Shirt", 5 => "Chest", 6 => "Waist", 7 => "Legs", 8 => "Feet",
        9 => "Wrist", 10 => "Hands", 11 => "Finger", 12 => "Trinket", 13 => "One-Hand", 14 => "Shield", 15 => "Ranged",
        16 => "Back", 17 => "Two-Hand", 18 => "Bag", 19 => "Tabard", 20 => "Robe", 21 => "Main Hand", 22 => "Off Hand",
        23 => "Held In Off-hand", 24 => "Ammo", 25 => "Thrown", 26 => "Ranged", 28 => "Relic", _ => "",
    };

    private static string RaceName(int race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "Night Elf", 5 => "Undead", 6 => "Tauren", 7 => "Gnome", 8 => "Troll",
        _ => $"Race {race}",
    };

    /// <summary>The name CharacterModelService keys its GLBs on (see Items/CharacterPreview).</summary>
    private static string PreviewRaceName(int race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf", 5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll",
        _ => "Human",
    };

    private static string ClassName(int classId) => classId switch
    {
        1 => "Warrior", 2 => "Paladin", 3 => "Hunter", 4 => "Rogue", 5 => "Priest", 7 => "Shaman", 8 => "Mage",
        9 => "Warlock", 11 => "Druid", _ => $"Class {classId}",
    };

    // ── rows ───────────────────────────────────────────────────────────────────

    private sealed class SearchRow
    {
        public int Guid { get; set; } public string Name { get; set; } = ""; public int Level { get; set; }
        public int Race { get; set; } public int ClassId { get; set; } public int Gender { get; set; }
        public int Online { get; set; } public string? AccountName { get; set; }
    }

    private sealed class CharacterRow
    {
        public int Guid { get; set; } public string Name { get; set; } = ""; public int Level { get; set; }
        public int Race { get; set; } public int ClassId { get; set; } public int Gender { get; set; }
        public int Online { get; set; } public long Money { get; set; } public long PlayedTotal { get; set; }
        public long LogoutTime { get; set; } public string? AccountName { get; set; }
    }

    private sealed class GuildRow { public string? GuildName { get; set; } public int GuildRank { get; set; } }

    private sealed class AppearanceRow
    {
        public uint Skin { get; set; } public uint Face { get; set; } public uint HairStyle { get; set; }
        public uint HairColor { get; set; } public uint FacialHair { get; set; }
    }

    private sealed class InventoryRow
    {
        public int Slot { get; set; } public long ItemGuid { get; set; } public int ItemEntry { get; set; }
        public int StackCount { get; set; } public string? Enchantments { get; set; }
    }

    private sealed class ItemRow
    {
        public int Entry { get; set; } public string Name { get; set; } = ""; public int Quality { get; set; }
        public int ClassId { get; set; } public int Subclass { get; set; } public int InventoryType { get; set; }
        public int ItemLevel { get; set; } public int RequiredLevel { get; set; } public int DisplayId { get; set; }
        public int Armor { get; set; } public float DmgMin1 { get; set; } public float DmgMax1 { get; set; }
        public int Delay { get; set; } public int Bonding { get; set; } public string? Description { get; set; }
        public int Stat_type1 { get; set; } public int Stat_value1 { get; set; }
        public int Stat_type2 { get; set; } public int Stat_value2 { get; set; }
        public int Stat_type3 { get; set; } public int Stat_value3 { get; set; }
        public int Stat_type4 { get; set; } public int Stat_value4 { get; set; }
        public int Stat_type5 { get; set; } public int Stat_value5 { get; set; }
        public int Stat_type6 { get; set; } public int Stat_value6 { get; set; }
        public int Stat_type7 { get; set; } public int Stat_value7 { get; set; }
        public int Stat_type8 { get; set; } public int Stat_value8 { get; set; }
        public int Stat_type9 { get; set; } public int Stat_value9 { get; set; }
        public int Stat_type10 { get; set; } public int Stat_value10 { get; set; }
        public int HolyRes { get; set; } public int FireRes { get; set; } public int NatureRes { get; set; }
        public int FrostRes { get; set; } public int ShadowRes { get; set; } public int ArcaneRes { get; set; }
        public int MaxDurability { get; set; } public int SetId { get; set; }
    }
}
