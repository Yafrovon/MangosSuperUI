using System.Numerics;
using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using MangosSuperUI.Services.ArmorForge;
using MangosSuperUI.Services.Itemization;
using MangosSuperUI.Services.WeaponForge;
using SkiaSharp;

namespace MangosSuperUI.Controllers;

/// <summary>
/// The Armor Forge (ARMOR_FORGE.md) — imports TBC (2.4.3) and WotLK (3.3.5a) armor into the live
/// vanilla server, as whole sets or single pieces. Painted pieces carry their body-atlas BLPs;
/// helms/shoulders are re-emitted as native vanilla M2s; cloaks carry their cape BLP. Everything
/// ships in the unified <c>patch-4.MPQ</c>, rebuilt on request. Vanilla set bonuses are an optional, operator-defined extra (never
/// imported). Every browse/preview/import endpoint exists per lane (<c>Tbc*</c> / <c>Wotlk*</c>) and
/// as an <c>expansion=</c> keyed form; all share one implementation over <see cref="ArmorImportLane"/>.
/// </summary>
public class ArmorForgeController : Controller
{
    private readonly CustomArmorBuildService _armor;
    private readonly ArmorImportSources _lanes;
    private readonly VanillaArmorSetCatalog _vanillaSets;
    private readonly DbcService _dbc;
    private readonly ItemConfigurationParser _itemConfig;
    private readonly ItemBudgetGenerator _itemize;
    private readonly PaletteSwapService _palette;
    private readonly BlpWriterService _blp;
    private readonly ProcessManagerService _processes;
    private readonly IWebHostEnvironment _env;
    private readonly MangosSuperUI.Services.UnifiedPatch.UnifiedPatchService _unified;
    private readonly ILogger<ArmorForgeController> _logger;

    // Armor equip slots the gameplay contract accepts (VanillaItemBuildConfigurationTranslator.ArmorInventoryTypes).
    private const string ArmorInventoryTypeError =
        "inventoryType must be a wearable Vanilla armor slot: 1 head, 3 shoulder, 5 chest, 6 waist, 7 legs, 8 feet, 9 wrists, 10 hands, 16 back, 20 robe, 23 held.";

    public ArmorForgeController(CustomArmorBuildService armor, ArmorImportSources lanes, VanillaArmorSetCatalog vanillaSets, DbcService dbc, ItemConfigurationParser itemConfig, ItemBudgetGenerator itemize, PaletteSwapService palette, BlpWriterService blp, ProcessManagerService processes, IWebHostEnvironment env, MangosSuperUI.Services.UnifiedPatch.UnifiedPatchService unified, ILogger<ArmorForgeController> logger)
    {
        _unified = unified;
        _armor = armor; _lanes = lanes; _vanillaSets = vanillaSets; _dbc = dbc; _itemConfig = itemConfig; _itemize = itemize; _palette = palette; _blp = blp; _processes = processes; _env = env; _logger = logger;
    }

    public IActionResult Index() => View();

    // ── Status ──────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Status()
    {
        object LaneStatus(ArmorImportLane lane)
        {
            var st = lane.Catalog.Status();
            int pieces = 0, sets = 0;
            if (st.Configured && st.ArchiveCount > 0)
            {
                try { pieces = lane.Catalog.Browse().Count; sets = lane.Catalog.Sets().Count; } catch { }
            }
            return new { expansion = lane.Key, label = lane.Label, configured = st.Configured, archiveCount = st.ArchiveCount, error = st.Error, pieces, sets };
        }
        return Json(new
        {
            fixtureOk = DonorItemTemplateFixture.Verify(),
            tbc = LaneStatus(_lanes.Tbc),
            wotlk = LaneStatus(_lanes.Wotlk),
            patchPresent = System.IO.File.Exists(_unified.ArtifactPath),
            deployedPatch = DeployedPatchJson(),
            serverItemSet = ServerItemSetJson(),
        });
    }

    /// <summary>GET /ArmorForge/VanillaStatus — reachability + row count for the vanilla clone lane.
    ///
    /// Deliberately NOT part of <see cref="Status"/>. Everything Status reports is a file or process
    /// probe, and the view enables the TBC and WotLK search boxes inside that one fetch's callback — so
    /// folding a world-DB round trip into it made two lanes that need no database sit disabled for the
    /// full MySQL connect timeout whenever the database was unreachable. This lane's card is seeded
    /// lazily anyway, so its status is fetched on the same trigger.</summary>
    [HttpGet]
    public async Task<IActionResult> VanillaStatus()
    {
        try
        {
            var pieces = await _armor.CountVanillaAsync();
            return Json(new { expansion = "vanilla", label = "Vanilla", configured = true, error = (string?)null, pieces, sets = 0 });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArmorForge: vanilla lane status failed");
            return Json(new { expansion = "vanilla", label = "Vanilla", configured = false, error = ex.Message, pieces = 0, sets = 0 });
        }
    }

    /// <summary>The unified patch-4.MPQ as the page sees it: built / deployed / stale, plus how many
    /// imports and deletes are queued for the next Rebuild patch click.</summary>
    private object DeployedPatchJson()
    {
        try
        {
            var st = _unified.DeployStatus();
            return new
            {
                configured = st.Configured, built = st.Built, deployedExists = st.Deployed, stale = st.Stale,
                pending = st.Pending, pendingReasons = st.PendingReasons, message = st.Message,
            };
        }
        catch (Exception ex) { return new { configured = false, built = false, deployedExists = false, stale = false, pending = 0, pendingReasons = Array.Empty<string>(), message = ex.Message }; }
    }

    /// <summary>Server-side set state: is our ItemSet.dbc in the core's dbc dir, and has mangosd been
    /// restarted since it was written? Until that restart the core zeroes every forged set_id.</summary>
    private object ServerItemSetJson()
    {
        try
        {
            var (configured, stale, writtenUtc, message) = _armor.ServerItemSetStatus();
            bool restartRequired = false;
            string? serverStarted = null;
            // The process probe is its own failure domain — a status pill must never take the page down.
            try
            {
                if (writtenUtc is DateTime written && _processes.GetMangosdStatus().StartTime is DateTime started)
                {
                    serverStarted = started.ToUniversalTime().ToString("u");
                    restartRequired = written > started.ToUniversalTime();
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "ArmorForge: mangosd start-time probe failed"); }
            return new
            {
                configured, stale, restartRequired, message,
                writtenUtc = writtenUtc?.ToString("u"), serverStarted,
            };
        }
        catch (Exception ex) { return new { configured = false, stale = false, restartRequired = false, message = ex.Message }; }
    }

    // ── Browse (grouped by set) ─────────────────────────────────────────

    /// <summary>Search results: matching SETS (with their armor members) first, then loose pieces
    /// that aren't in any set (or whose set name didn't match). One call, grouped.</summary>
    [HttpGet]
    public IActionResult TbcBrowse(string? search, string? family, int limit = 60) => LaneBrowse(_lanes.Tbc, search, family, limit);

    [HttpGet]
    public IActionResult WotlkBrowse(string? search, string? family, int limit = 60) => LaneBrowse(_lanes.Wotlk, search, family, limit);

    /// <summary>GET /ArmorForge/ImportBrowse?expansion=tbc|wotlk|vanilla&amp;search=… — lane-keyed form.
    /// The vanilla lane browses the live world DB (existing vanilla armor) rather than a client archive.</summary>
    [HttpGet]
    public async Task<IActionResult> ImportBrowse(string? expansion, string? search, string? family, int limit = 60)
    {
        if (string.Equals(expansion, "vanilla", StringComparison.OrdinalIgnoreCase))
        {
            try { return await VanillaBrowseAsync(search, family, limit); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ArmorForge: vanilla browse failed");
                return Json(new { sets = Array.Empty<object>(), otherSets = Array.Empty<object>(), otherSetCount = 0, loose = Array.Empty<object>(), total = 0, error = ex.Message });
            }
        }
        var browseLane = TryImportLane(expansion, out var browseError);
        return browseLane is null
            ? Json(new { sets = Array.Empty<object>(), otherSets = Array.Empty<object>(), otherSetCount = 0, loose = Array.Empty<object>(), total = 0, error = browseError })
            : LaneBrowse(browseLane, search, family, limit);
    }

    /// <summary>GET /ArmorForge/VanillaSource?entry=… — the source item's real gameplay, to pre-fill the
    /// Configure modal for a clone (vanilla starts from its own values).</summary>
    [HttpGet]
    public async Task<IActionResult> VanillaSource(uint entry)
    {
        CustomArmorBuildService.VanillaSourceConfigDto? s;
        try
        {
            s = await _armor.ReadVanillaSourceAsync(entry);
        }
        catch (Exception ex)
        {
            // { ok, error } rather than an unhandled 500 — prefillFromSource does `r.json()` on the
            // response, so a world-DB outage surfaced as a parse error in the console and a silently
            // un-prefilled Configure modal, which then wrote its blank defaults over the clone.
            _logger.LogWarning(ex, "ArmorForge: vanilla source {Entry} failed", entry);
            return Json(new { ok = false, error = "Could not read the world database: " + ex.Message });
        }
        if (s is null) return Json(new { ok = false, error = "Not a vanilla armor item." });
        return Json(new
        {
            ok = true, name = s.Name, quality = s.Quality, itemLevel = s.ItemLevel, requiredLevel = s.RequiredLevel,
            buyPrice = s.BuyPrice, sellPrice = s.SellPrice, armor = s.Armor,
            holyRes = s.HolyRes, fireRes = s.FireRes, natureRes = s.NatureRes, frostRes = s.FrostRes,
            shadowRes = s.ShadowRes, arcaneRes = s.ArcaneRes, maxDurability = s.MaxDurability, bonding = s.Bonding,
            inventoryType = s.InventoryType, allowableClass = s.AllowableClass,
            stats = s.Stats.Select(x => new { type = x.Type, value = x.Value }),
            spells = s.Spells.Select(x => new { spellId = x.SpellId, trigger = x.Trigger, charges = x.Charges, ppmRate = x.PpmRate, cooldownMs = x.CooldownMs, category = x.Category, categoryCooldownMs = x.CategoryCooldownMs }),
        });
    }

    /// <summary>One vanilla set card: the stock set plus the members that survived the armor filter.</summary>
    private sealed record VanillaSetCard(
        VanillaSetInfo Set,
        IReadOnlyList<CustomArmorBuildService.VanillaArmorPieceDto> Members,
        int MaxItemLevel,
        int MaxQuality,
        bool Featured);

    /// <summary>The vanilla lane's browse, grouped exactly like <see cref="LaneBrowse"/>: the tier sets
    /// first, the rest of the stock sets behind the "other sets" toggle, then loose pieces.
    ///
    /// The two halves come from different places, which is the whole reason this is not LaneBrowse.
    /// Set IDENTITY (name, membership, bonuses) is in the mounted client's ItemSet.dbc — measured on a
    /// real 1.12 mount: 172 sets, every one of them listing its members, so the DBC alone is sufficient
    /// and nothing has to be grouped by <c>item_template.set_id</c>. Everything the cards SHOW about a
    /// piece (name, quality, item level, display, slot) is live world-DB data, because that is what a
    /// clone actually copies. A set whose members include weapons or rings loses them to the armor
    /// filter, and the card reports the count it dropped rather than pretending to be complete.</summary>
    private async Task<IActionResult> VanillaBrowseAsync(string? search, string? family, int limit)
    {
        string q = (search ?? "").Trim();
        bool Match(string? text) => q.Length == 0 || (text ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
        bool hasFamily = !string.IsNullOrWhiteSpace(family);

        // One round trip for every set member on the mount, then group in memory. Measured worst case is
        // 172 sets x at most 17 members, so this is one IN-list of ~1,000 entries, not a per-set query.
        var stockSets = _vanillaSets.Sets();
        var allMembers = stockSets.SelectMany(x => x.MemberEntries).Distinct().ToArray();
        var stockSetIds = stockSets.Select(x => x.SetId).ToArray();
        var memberPieces = stockSets.Count > 0
            ? await _armor.ReadVanillaPiecesAsync(allMembers, stockSetIds)
            : (IReadOnlyList<CustomArmorBuildService.VanillaArmorPieceDto>)Array.Empty<CustomArmorBuildService.VanillaArmorPieceDto>();
        var pieceByEntry = memberPieces.ToDictionary(x => x.Entry);
        // Membership by the column the SERVER counts, for the union below.
        var byColumn = memberPieces.Where(x => x.SetId > 0)
            .GroupBy(x => x.SetId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Entry).ToHashSet());

        var cards = new List<VanillaSetCard>();
        foreach (var set in stockSets)
        {
            // DBC membership (what the client tooltip shows) unioned with item_template.set_id (what the
            // core counts). They agree on stock data; on an edited world DB the card must not promise a
            // set the server will count differently.
            var entries = new List<uint>(set.MemberEntries);
            if (byColumn.TryGetValue(set.SetId, out var columnEntries))
                foreach (var e in columnEntries) if (!entries.Contains(e)) entries.Add(e);

            var members = entries.Where(pieceByEntry.ContainsKey).Select(e => pieceByEntry[e])
                .OrderBy(m => SlotOrder(m.InventoryType)).ToList();
            if (members.Count == 0) continue;                       // a weapons-only set: not ours to clone
            if (hasFamily && !members.Any(m => m.Family.Equals(family, StringComparison.OrdinalIgnoreCase))) continue;
            if (!(Match(set.Name) || members.Any(m => Match(m.Name)) || (q.Length > 0 && set.SetId.ToString() == q))) continue;

            int maxIlvl = members.Max(m => m.ItemLevel), maxQuality = members.Max(m => m.Quality);
            // Vanilla's own predicate, not the import lanes' — see VanillaArmorSetCatalog for the
            // measurements behind both numbers, and for why quality is not part of it here.
            cards.Add(new VanillaSetCard(set, members, maxIlvl, maxQuality,
                members.Count >= VanillaArmorSetCatalog.FeaturedMinArmorPieces
                && maxIlvl >= VanillaArmorSetCatalog.FeaturedMinItemLevel));
        }

        var ordered = cards.OrderByDescending(c => c.MaxItemLevel)
                           .ThenBy(c => c.Set.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var featuredAll = ordered.Where(c => c.Featured).ToList();
        var otherAll = ordered.Where(c => !c.Featured).OrderBy(c => c.Set.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // Loose pieces: the ordinary browse minus anything a card above already shows.
        var loosePieces = await _armor.BrowseVanillaAsync(search, family, limit);
        var shown = ordered.SelectMany(c => c.Members).Select(m => m.Entry).ToHashSet();
        var loose = loosePieces.Where(x => !shown.Contains(x.Entry)).Select(VanillaPieceDto).ToList();

        bool slotSearch = q.Length > 0 && !hasFamily && ArmorTypeCatalog.FamilyForSlotWord(q) != null;
        return Json(new
        {
            expansion = "vanilla", label = "Vanilla",
            sets = featuredAll.Take(limit * 4).Select(VanillaSetDto).ToList(),
            otherSets = otherAll.Take(limit * 8).Select(VanillaSetDto).ToList(),
            featuredSetCount = featuredAll.Count, otherSetCount = otherAll.Count,
            featuredMinItemLevel = VanillaArmorSetCatalog.FeaturedMinItemLevel,
            loose, total = loosePieces.Count, slotSearch,
            setsError = _vanillaSets.Error,
        });
    }

    private object VanillaSetDto(VanillaSetCard c) => new
    {
        setId = c.Set.SetId, name = c.Set.Name, expansion = "vanilla",
        featured = c.Featured, maxItemLevel = c.MaxItemLevel, maxQuality = c.MaxQuality,
        pieces = c.Members.Select(VanillaPieceDto).ToList(),
        // Unique to this lane: a vanilla set's bonus spells exist in a vanilla core by definition, so the
        // Set Configure modal can offer the source's own table as the starting point instead of making
        // the operator retype it. The import lanes cannot — their spell ids belong to a later client.
        // Names resolved here, from the same Spell.dbc the core reads. The modal's effect SEARCH cannot
        // do it: that endpoint only lists spells used by stock ITEMS, and a set bonus is a set spell.
        bonuses = c.Set.Bonuses.Select(b => new
        {
            spellId = b.SpellId,
            threshold = b.Threshold,
            name = _dbc.AllSpellEntries.TryGetValue((uint)b.SpellId, out var sp) && !string.IsNullOrWhiteSpace(sp.Name)
                ? sp.Name : $"Spell {b.SpellId}",
        }).ToList(),
        requiredSkill = c.Set.RequiredSkill, requiredSkillRank = c.Set.RequiredSkillRank,
        // Members the armor filter dropped — weapons, rings, trinkets. Shown on the card so a 5-of-8
        // set never looks like the whole thing.
        droppedMembers = Math.Max(0, c.Set.MemberEntries.Count - c.Members.Count),
    };

    private static object VanillaPieceDto(CustomArmorBuildService.VanillaArmorPieceDto p) => new
    {
        entry = p.Entry, name = p.Name, quality = p.Quality, itemLevel = p.ItemLevel,
        displayId = p.DisplayId, inventoryType = p.InventoryType,
        family = p.Family, familyLabel = p.FamilyLabel,
        // Was hard-coded "Painted", which made every helm, shoulder and cloak in the clone list render
        // with the paintbrush icon and the "painted" note. The slot decides.
        renderKind = p.RenderKind.ToString(), expansion = "vanilla",
    };

    private IActionResult LaneBrowse(ArmorImportLane lane, string? search, string? family, int limit)
    {
        var all = lane.Catalog.Browse();
        string s = (search ?? "").Trim();
        bool Match(string? text) => s.Length == 0 || (text ?? "").Contains(s, StringComparison.OrdinalIgnoreCase);

        IEnumerable<LegacyArmorEntry> q = all;
        if (!string.IsNullOrEmpty(family)) q = q.Where(e => e.FamilyKey == family);
        var scoped = q.ToList();
        var hits = scoped.Where(e => Match(e.Name) || Match(e.SetName) || (s.Length > 0 && e.Entry.ToString() == s)).ToList();

        // Slot-word search: "boots" / "waist" / "helmet" resolves to a family, because most boots are
        // named Sabatons/Treads/Greaves and a name substring finds almost nothing. Slot hits fill the
        // pieces list below (set members included, best ilvl first) but do NOT nominate set cards —
        // every tier set contains boots, so cards would drown the answer.
        string? slotFamily = s.Length == 0 ? null : ArmorTypeCatalog.FamilyForSlotWord(s);

        // Sets: any set with a matching member or matching name; list ALL its armor members.
        // Split into the current-expansion tier/arena sets (what the browse leads with) and
        // everything else — levelling greens, dungeon blues, crafted three-pieces and the PREVIOUS
        // expansions' tiers, which the later client's ItemSet.dbc carries too. Measured on a full
        // 2.4.3 mount that is 73 sets in front of 259; on 3.3.5a, 98 in front of 369. The "other"
        // list still travels to the browser (it is a few KB of names) — the card keeps it behind a
        // toggle so it is one click away instead of a search away.
        var setIds = hits.Where(h => h.SetId != 0).Select(h => h.SetId).Distinct().ToList();
        var byEntry = all.ToDictionary(e => e.Entry);
        var resolved = setIds.Select(id => lane.Catalog.GetSet(id)).Where(x => x != null).Cast<LegacySetInfo>().ToList();
        var dtos = resolved
            .Select(set => new
            {
                setId = set.SetId, name = set.Name, expansion = lane.Key,
                featured = set.Featured, maxItemLevel = set.MaxItemLevel, maxQuality = set.MaxQuality,
                pieces = set.MemberEntries.Where(byEntry.ContainsKey).Select(e => byEntry[e])
                    .OrderBy(e => SlotOrder(e.InventoryType)).Select(e => PieceDto(e, lane)).ToList(),
            })
            .OrderByDescending(x => x.maxItemLevel)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var featuredAll = dtos.Where(x => x.featured).ToList();
        var otherAll = dtos.Where(x => !x.featured).OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList();
        var sets = featuredAll.Take(limit * 4).ToList();
        var otherSets = otherAll.Take(limit * 8).ToList();

        var loose = slotFamily != null
            ? scoped.Where(e => e.FamilyKey == slotFamily)
                .OrderByDescending(e => e.ItemLevel).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit * 2).Select(e => PieceDto(e, lane)).ToList()
            : hits.Where(h => h.SetId == 0).OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Take(limit).Select(e => PieceDto(e, lane)).ToList();
        return Json(new
        {
            expansion = lane.Key, label = lane.Label,
            total = slotFamily != null ? scoped.Count(e => e.FamilyKey == slotFamily) : hits.Count,
            sets, otherSets,
            featuredSetCount = featuredAll.Count, otherSetCount = otherAll.Count,
            featuredMinItemLevel = lane.Catalog.FeaturedItemLevelForDisplay,
            loose, slotSearch = slotFamily != null,
        });
    }

    private static object PieceDto(LegacyArmorEntry e, ArmorImportLane lane) => new
    {
        entry = e.Entry, name = e.Name, family = e.FamilyKey, familyLabel = e.FamilyLabel,
        renderKind = e.RenderKind.ToString(), material = e.Material.ToString(), quality = e.Quality,
        itemLevel = e.ItemLevel, requiredLevel = e.RequiredLevel, setId = e.SetId, setName = e.SetName,
        slotOrder = SlotOrder(e.InventoryType), displayId = e.DisplayId, inventoryType = e.InventoryType,
        expansion = lane.Key,
    };

    private static int SlotOrder(int inv) => inv switch { 1 => 0, 3 => 1, 16 => 2, 5 => 3, 20 => 3, 4 => 4, 19 => 5, 9 => 6, 10 => 7, 6 => 8, 7 => 9, 8 => 10, _ => 20 };

    // ── Pre-import dressing preview ─────────────────────────────────────

    /// <summary>Pre-import preview: a dressing payload for a later-client piece in the SAME shape as
    /// <c>/Items/ItemDressing</c> (inventoryType, geosetGroup, slotUrls, capeTextureUrl, hidesHair,
    /// attachments), built from the source row — so the character viewer's <c>equipMultiple</c> can dress
    /// the character with it exactly as it dresses vanilla items (geosets, paint order, cape). Helm /
    /// shoulder attachments are only available after import (no vanilla M2 exists yet), so those come
    /// back with empty attachments but still drive hair-hide / geosets.</summary>
    [HttpGet]
    public Task<IActionResult> TbcDressing(uint entry, string race = "Human", string gender = "Male") => LaneDressing(_lanes.Tbc, entry, race, gender);

    [HttpGet]
    public Task<IActionResult> WotlkDressing(uint entry, string race = "Human", string gender = "Male") => LaneDressing(_lanes.Wotlk, entry, race, gender);

    /// <summary>Resolve a lane key for the endpoints that only an IMPORT lane can serve. Returns null
    /// and fills <paramref name="error"/> for "vanilla" (a clone has no client archive to read art from)
    /// and for anything unrecognised, so the caller answers 400 with a sentence instead of throwing —
    /// or, as before this existed, instead of quietly serving 2.4.3 art for a vanilla entry id.</summary>
    private ArmorImportLane? TryImportLane(string? expansion, out string error)
    {
        error = "";
        try { return _lanes.Get(expansion); }
        catch (ArgumentOutOfRangeException)
        {
            // Compose the operator-facing sentence here rather than surfacing the exception text,
            // which appends ArgumentOutOfRangeException's "(Parameter 'key') Actual value was …".
            error = string.Equals(expansion, "vanilla", StringComparison.OrdinalIgnoreCase)
                ? "The vanilla clone lane has no client archive — it reads the world database, and its pieces " +
                  "already have a real display, so they dress through /Items/ItemDressing like any other item."
                : $"'{expansion}' is not an Armor Forge import lane. The import lanes are 'tbc' and 'wotlk'.";
            return null;
        }
    }

    /// <summary>GET /ArmorForge/ImportDressing?expansion=tbc|wotlk&amp;entry=… — lane-keyed form.
    /// Import lanes only: a vanilla piece already has a real display and dresses through the ordinary
    /// <c>/Items/ItemDressing</c>, which is what the forge's own viewer calls for it.</summary>
    [HttpGet]
    public Task<IActionResult> ImportDressing(string? expansion, uint entry, string race = "Human", string gender = "Male")
    {
        var lane = TryImportLane(expansion, out var error);
        return lane is null ? Task.FromResult<IActionResult>(BadRequest(new { success = false, error })) : LaneDressing(lane, entry, race, gender);
    }

    private async Task<IActionResult> LaneDressing(ArmorImportLane lane, uint entry, string race, string gender)
    {
        var payload = await BuildDressingAsync(lane, entry, race, gender, null, null, null, "fan", "improved");
        return payload is null ? NotFound() : Json(payload);
    }

    /// <summary>Build the dressing payload (same shape as /Items/ItemDressing) for a foreign piece.
    /// When <paramref name="hue"/> is set, the body-atlas slots, cape, and helm/shoulder attachment
    /// textures are recolored at that primary hue (palette engine) so the whole piece previews recolored.</summary>
    private async Task<object?> BuildDressingAsync(ArmorImportLane lane, uint entry, string race, string gender,
        float? hue, float? sat, float? light, string theory, string tier)
    {
        var item = lane.Catalog.FindEntry(entry);
        if (item is null) return null;
        var row = lane.Catalog.GetDisplayRow(item.DisplayId);
        if (row is null) return null;

        bool female = string.Equals(gender, "Female", StringComparison.OrdinalIgnoreCase);
        // Prefer the requested gender's art, then unisex, then the other gender, then bare.
        string[] suffixes = female ? new[] { "_F", "_U", "_M", "" } : new[] { "_M", "_U", "_F", "" };
        string cacheDir = Path.Combine(_env.WebRootPath, "armor_forge_cache", lane.Key, entry.ToString());
        string urlBase = $"/armor_forge_cache/{lane.Key}/{entry}";

        // Recolor setup (only used when a hue is supplied).
        bool recolor = hue.HasValue;
        if (recolor && Array.IndexOf(PaletteSwapService.RecolorTheories, theory) < 0) theory = "none";
        var (kd, ku, mm, pop) = RetextureSupport.TierShape(tier);
        int seed = RetextureSupport.SeedFor((int)item.DisplayId, tier);
        string pickKey = PickKey(hue, sat, light);
        string outDir = Path.Combine(cacheDir, "recolor");
        if (recolor) Directory.CreateDirectory(outDir);
        var ct = HttpContext.RequestAborted;

        // Every texture this piece paints is decoded first, then recolored in a second pass around
        // ONE primary detected across all of them (RecolorAnchor). Recoloring slot by slot, each on
        // its own primary, put every slot's biggest material on the pick — the whole piece went red.
        var pending = new List<(string BaseName, string BaseUrl, Action<string> Assign)>();
        RecolorAnchor? anchor = null;
        async Task<string> RecolorOrBase(string baseName, string baseUrl)
        {
            if (!recolor) return baseUrl;
            string srcDisk = Path.Combine(cacheDir, baseName);
            string outName = Path.GetFileNameWithoutExtension(baseName) + $"_{pickKey}_{theory}_{tier}_{PaletteSwapService.RecolorVersion}.png";
            string outPng = Path.Combine(outDir, outName);
            // Forge recolor = the WHOLE piece shifts to the chosen primary: full swap budget (1.01),
            // full hue leash (180°), tint structural darks — not the retexture engine's conservative
            // tiered budget (0.20/40°) which pins dominant/dark regions to their source colour.
            var ok = await _palette.RecolorSeededAsync(srcDisk, outPng, seed, 1f, 0f, tintStructural: true, ct,
                theory, kd, ku, mm, pop, swapBudget: 1.01f, hueLeash: 180f, value: ValueSettings.Keep, baseHueOverride: hue,
                baseSatOverride: sat, baseLightOverride: light, anchor: anchor);
            return ok != null ? $"{urlBase}/recolor/{outName}?t={DateTime.UtcNow.Ticks}" : baseUrl;
        }

        // Only the slots this equip type paints (see ArmorTypeProfile.PaintedSlots — later-client rows
        // carry template textures for slots the item doesn't occupy; the game ignores them).
        var profile = ArmorTypeCatalog.Get(item.FamilyKey);
        var slotUrls = new Dictionary<int, string>();
        foreach (int slot in profile.PaintedSlots)
        {
            string partial = row.ComponentPartials[slot];
            if (string.IsNullOrEmpty(partial)) continue;
            string subdir = ArmorNaming.ComponentSubdirs[slot];
            foreach (var suffix in suffixes)
            {
                var blp = lane.Catalog.ExtractFile($@"Item\TextureComponents\{subdir}\{partial}{suffix}.blp");
                if (blp is not { Length: > 0 }) continue;
                string tag = suffix.Length == 0 ? "_U" : suffix;
                string baseName = $"slot{slot}{tag}.png";
                var url = CachePng(blp, cacheDir, baseName, $"{urlBase}/{baseName}");
                if (url != null) { int s = slot; slotUrls[s] = url; pending.Add((baseName, url, u => slotUrls[s] = u)); break; }
            }
        }

        string? capeTextureUrl = null;
        if (item.InventoryType == 16 && !string.IsNullOrEmpty(row.TextureName1))
        {
            var blp = lane.Catalog.ExtractFile($@"{ArmorNaming.CapeDir}\{row.TextureName1}.blp");
            if (blp is { Length: > 0 })
            {
                var url = CachePng(blp, cacheDir, "cape.png", $"{urlBase}/cape.png");
                if (url != null) { capeTextureUrl = url; pending.Add(("cape.png", url, u => capeTextureUrl = u)); }
            }
        }

        if (recolor && pending.Count > 0)
        {
            anchor = _palette.DetectPrimaryAcross(pending.Select(x => Path.Combine(cacheDir, x.BaseName)));
            foreach (var (baseName, baseUrl, assign) in pending)
                assign(await RecolorOrBase(baseName, baseUrl));
        }

        uint raceId = race.ToLowerInvariant() switch
        {
            "human" => 1, "orc" => 2, "dwarf" => 3, "nightelf" => 4, "scourge" or "undead" => 5,
            "tauren" => 6, "gnome" => 7, "troll" => 8, _ => 1,
        };
        bool hidesHair = item.InventoryType == 1 && _dbc.DoesHelmHideHair(female ? row.HelmetVis1 : row.HelmetVis0, raceId);

        // Pre-import helm / shoulder geometry: build the preview GLB straight from the SOURCE
        // client's M2 + its texture (recolored at the primary hue when requested).
        var attachments = new Dictionary<string, string>();
        if (item.InventoryType == 1 && !string.IsNullOrEmpty(row.ModelName1))
        {
            string raceCode = race.ToLowerInvariant() switch
            {
                "human" => "Hu", "orc" => "Or", "dwarf" => "Dw", "nightelf" => "Ni", "scourge" or "undead" => "Sc",
                "tauren" => "Ta", "gnome" => "Gn", "troll" => "Tr", _ => "Hu",
            };
            string sfx = $"_{raceCode}{(female ? 'F' : 'M')}";
            string stem = Path.GetFileNameWithoutExtension(row.ModelName1);
            var url = await BuildAttachmentGlbAsync(lane, $@"{ArmorNaming.HeadDir}\{stem}{sfx}.m2", $@"{ArmorNaming.HeadDir}\{row.TextureName1}.blp",
                cacheDir, $"helm{sfx}.glb", $"{urlBase}/helm{sfx}.glb", hue, sat, light, anchor, theory, tier, seed, ct);
            if (url != null) attachments["helm"] = url;
        }
        else if (item.InventoryType == 3 && !string.IsNullOrEmpty(row.ModelName1))
        {
            string left = Path.GetFileNameWithoutExtension(row.ModelName1);
            string right = string.IsNullOrEmpty(row.ModelName2) ? (left.StartsWith("L", StringComparison.OrdinalIgnoreCase) ? "R" + left[1..] : left) : Path.GetFileNameWithoutExtension(row.ModelName2);
            string tex2 = string.IsNullOrEmpty(row.TextureName2) ? row.TextureName1 : row.TextureName2;
            var l = await BuildAttachmentGlbAsync(lane, $@"{ArmorNaming.ShoulderDir}\{left}.m2", $@"{ArmorNaming.ShoulderDir}\{row.TextureName1}.blp", cacheDir, "lshoulder.glb", $"{urlBase}/lshoulder.glb", hue, sat, light, anchor, theory, tier, seed, ct);
            var r = await BuildAttachmentGlbAsync(lane, $@"{ArmorNaming.ShoulderDir}\{right}.m2", $@"{ArmorNaming.ShoulderDir}\{tex2}.blp", cacheDir, "rshoulder.glb", $"{urlBase}/rshoulder.glb", hue, sat, light, anchor, theory, tier, seed, ct);
            if (l != null) attachments["shoulderLeft"] = l;
            if (r != null) attachments["shoulderRight"] = r;
        }

        return new
        {
            success = true,
            displayId = item.DisplayId, itemId = entry, inventoryType = item.InventoryType,
            geosetGroup = row.GeosetGroup, bodyTextures = row.ComponentPartials, slotUrls,
            attachments, capeTextureUrl,
            modelName1 = row.ModelName1, modelName2 = row.ModelName2, textureName1 = row.TextureName1, textureName2 = row.TextureName2,
            helmetGeosetVis1 = row.HelmetVis0, helmetGeosetVis2 = row.HelmetVis1, hidesHair,
            tbc = true, expansion = lane.Key, renderKind = item.RenderKind.ToString(), name = item.Name,
        };
    }

    /// <summary>GLB for a later-client helm/shoulder M2 + skin BLP, cached under armor_forge_cache.
    /// When <paramref name="hue"/> is set the skin is recolored at that primary hue before baking, so the
    /// attachment previews recolored to match the body. Type-0 textures come by filename from the source
    /// client; the DBC skin goes into the first non-type-0 slot; doubleSided for thin flaps/horns.</summary>
    private async Task<string?> BuildAttachmentGlbAsync(ArmorImportLane lane, string m2Path, string skinBlpPath,
        string cacheDir, string fileName, string url, float? hue, float? sat, float? light, RecolorAnchor? anchor, string theory, string tier, int seed, CancellationToken ct)
    {
        try
        {
            bool recolor = hue.HasValue;
            string pickKey = PickKey(hue, sat, light);
            // Version-stamp the cache (assembly MVID) so a GlbWriter change invalidates it; fold the
            // hue/theory/tier into the name so each recolour is its own cached GLB.
            fileName = CacheVersionRegistry.MakeVersioned(fileName, CacheVersionRegistry.RigidGlbVersion);
            if (recolor) fileName = Path.GetFileNameWithoutExtension(fileName) + $"_{pickKey}_{theory}_{tier}_{PaletteSwapService.RecolorVersion}" + Path.GetExtension(fileName);
            url = url[..(url.LastIndexOf('/') + 1)] + fileName;

            Directory.CreateDirectory(cacheDir);
            string file = Path.Combine(cacheDir, fileName);
            if (System.IO.File.Exists(file)) return url;
            // Version-aware parse: TBC v260 (inline views) or WotLK v264 (+ .skin profile).
            var m2 = lane.Catalog.LoadM2(m2Path);
            if (m2 is null) { _logger.LogDebug("Armor preview: {M2} not in {Lane} or unparseable", m2Path, lane.Key); return null; }
            var textures = new Dictionary<int, byte[]>();
            for (int i = 0; i < m2.Textures.Count; i++)
            {
                var t = m2.Textures[i];
                if (t.Type != 0 || string.IsNullOrEmpty(t.Filename)) continue;
                var b = lane.Catalog.ExtractFile(t.Filename);
                if (b is { Length: > 0 }) textures[i] = b;
            }
            var skin = lane.Catalog.ExtractFile(skinBlpPath);
            if (skin is { Length: > 0 })
            {
                byte[] skinBytes = skin;
                if (recolor)
                {
                    var rb = await RecolorBlpAsync(skin, cacheDir, "att_" + Path.GetFileNameWithoutExtension(skinBlpPath), hue!.Value, sat, light, anchor, theory, tier, seed, ct);
                    if (rb != null) skinBytes = rb;
                }
                int slot = -1;
                for (int i = 0; i < m2.Textures.Count; i++) if (m2.Textures[i].Type != 0 && !textures.ContainsKey(i)) { slot = i; break; }
                if (slot < 0) slot = 0;
                textures[slot] = skinBytes;
            }
            // WotLK pre-import parity: preview the emitters the import WILL bake (donor grafts),
            // not the raw v264 summary — GlbWriter's degraded raw-summary fallback then never runs.
            List<WeaponPreviewService.PreviewEmitter>? planned = null;
            if (m2.SourceBytes is null && m2.ParticleEmitters.Count > 0)
                planned = lane.Importer.PlanPreviewEmitters(m2);
            bool ok = GlbWriter.SaveGlb(m2, textures, file, doubleSided: true, plannedEmitters: planned);
            return ok ? url : null;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Armor preview: attachment GLB failed for {M2}", m2Path); return null; }
    }

    /// <summary>Recolor a BLP at the primary hue and return the recolored BLP bytes (BLP→PNG→palette
    /// recolor→BLP). Null on failure so the caller can fall back to the original skin.</summary>
    /// <summary>Cache-key fragment for a recolour: the hue, plus saturation/lightness when a full
    /// colour was picked. Without them two different picks with the same hue (red and white, say)
    /// would share a cached PNG.</summary>
    private static string PickKey(float? hue, float? sat, float? light)
    {
        int hueKey = hue.HasValue ? (((int)Math.Round(hue.Value) % 360 + 360) % 360) : 0;
        string key = $"h{hueKey}";
        if (sat.HasValue) key += $"s{(int)Math.Round(Math.Clamp(sat.Value, 0f, 1f) * 100)}";
        if (light.HasValue) key += $"l{(int)Math.Round(Math.Clamp(light.Value, 0f, 1f) * 100)}";
        return key;
    }

    private async Task<byte[]?> RecolorBlpAsync(byte[] blp, string cacheDir, string stem, float hue, float? sat, float? light, RecolorAnchor? anchor, string theory, string tier, int seed, CancellationToken ct)
    {
        try
        {
            string pngDir = Path.Combine(cacheDir, "recolor");
            Directory.CreateDirectory(pngDir);
            string basePng = Path.Combine(pngDir, stem + ".png");
            if (!System.IO.File.Exists(basePng))
            {
                var px = BlpDecoder.GetPixels(blp, 0, out int w, out int h);
                if (w == 0 || h == 0) return null;
                using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
                bmp.NotifyPixelsChanged();
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                System.IO.File.WriteAllBytes(basePng, data.ToArray());
            }
            var (kd, ku, mm, pop) = RetextureSupport.TierShape(tier);
            string outPng = Path.Combine(pngDir, $"{stem}_{PickKey(hue, sat, light)}_{theory}_{tier}_{PaletteSwapService.RecolorVersion}.png");
            // A helm/shoulder with no painted slots anchors on its own skin, like a weapon does.
            var okp = await _palette.RecolorSeededAsync(basePng, outPng, seed, 1f, 0f, tintStructural: true, ct,
                theory, kd, ku, mm, pop, swapBudget: 1.01f, hueLeash: 180f, value: ValueSettings.Keep, baseHueOverride: hue,
                baseSatOverride: sat, baseLightOverride: light, anchor: anchor);
            if (okp == null) return null;
            using var recolored = SKBitmap.Decode(outPng);
            if (recolored == null) return null;
            // Encode the already-decoded recolored bitmap directly (DXT3, no resize / no lum-alpha, which
            // was destroying the skin's alpha and nulling on non-power-of-2 sizes). Uncompressed fallback
            // has no power-of-2 gate, so a non-standard skin size still recolors instead of staying original.
            return _blp.EncodeBitmapToBlp(recolored, useDxt1: false) ?? _blp.EncodeBitmapToBlpUncompressed(recolored);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Armor recolor BLP failed for {Stem}", stem); return null; }
    }

    private string? CachePng(byte[] blp, string cacheDir, string fileName, string url)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            string file = Path.Combine(cacheDir, fileName);
            if (!System.IO.File.Exists(file))
            {
                var px = BlpDecoder.GetPixels(blp, 0, out int w, out int h);
                using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
                bmp.NotifyPixelsChanged();
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                System.IO.File.WriteAllBytes(file, data.ToArray());
            }
            return url;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Armor preview decode failed {File}", fileName); return null; }
    }

    // ── Pre-import recolor of foreign (TBC/WotLK) previews ───────────────
    // The retexture engine resolves textures from the VANILLA client by displayId, so it can't touch a
    // not-yet-imported foreign piece. These endpoints recolor the SOURCE body-atlas PNGs the dressing
    // path already extracted, so a TBC/WotLK piece/set can be recolored live before import.

    /// <summary>Extract (once) the source body-atlas slot PNGs for a foreign entry; returns their disk
    /// paths so they can be recolored or colour-sampled. Null if the entry/row isn't found.</summary>
    private (int DisplayId, Dictionary<int, string> SlotDisks, string UrlBase, string CacheDir)? ExtractDressingSlots(
        ArmorImportLane lane, uint entry, string race, string gender)
    {
        var item = lane.Catalog.FindEntry(entry);
        if (item is null) return null;
        var row = lane.Catalog.GetDisplayRow(item.DisplayId);
        if (row is null) return null;

        bool female = string.Equals(gender, "Female", StringComparison.OrdinalIgnoreCase);
        string[] suffixes = female ? new[] { "_F", "_U", "_M", "" } : new[] { "_M", "_U", "_F", "" };
        string cacheDir = Path.Combine(_env.WebRootPath, "armor_forge_cache", lane.Key, entry.ToString());
        string urlBase = $"/armor_forge_cache/{lane.Key}/{entry}";
        var profile = ArmorTypeCatalog.Get(item.FamilyKey);
        var slotDisks = new Dictionary<int, string>();
        foreach (int slot in profile.PaintedSlots)
        {
            string partial = row.ComponentPartials[slot];
            if (string.IsNullOrEmpty(partial)) continue;
            string subdir = ArmorNaming.ComponentSubdirs[slot];
            foreach (var suffix in suffixes)
            {
                var blp = lane.Catalog.ExtractFile($@"Item\TextureComponents\{subdir}\{partial}{suffix}.blp");
                if (blp is not { Length: > 0 }) continue;
                string tag = suffix.Length == 0 ? "_U" : suffix;
                string fileName = $"slot{slot}{tag}.png";
                var url = CachePng(blp, cacheDir, fileName, $"{urlBase}/{fileName}");
                if (url != null) { slotDisks[slot] = Path.Combine(cacheDir, fileName); break; }
            }
        }
        return ((int)item.DisplayId, slotDisks, urlBase, cacheDir);
    }

    /// <summary>GET /ArmorForge/RecolorDressing?expansion=&amp;entry=&amp;hue=&amp;theory=&amp;tier= — the FULL dressing
    /// payload (body slots + cape + helm/shoulder attachments) recolored at the chosen primary hue, so the
    /// character viewer re-dresses the piece/set recolored in one layered pass (no flicker, covers models).</summary>
    [HttpGet]
    public async Task<IActionResult> RecolorDressing(string? expansion, uint entry, float hue,
        float? sat = null, float? light = null,
        string theory = "fan", string tier = "improved", string race = "Human", string gender = "Male")
    {
        var lane = TryImportLane(expansion, out var laneError);
        if (lane is null) return BadRequest(new { success = false, error = laneError });
        var payload = await BuildDressingAsync(lane, entry, race, gender, hue, sat, light, theory, tier);
        return payload is null ? Json(new { success = false, error = "not found" }) : Json(payload);
    }

    /// <summary>GET /ArmorForge/PrimaryColor?expansion=&amp;entry= — the foreign piece's majority colour,
    /// so the picker can seed itself. Returns { success, primaryHex }.</summary>
    [HttpGet]
    public IActionResult PrimaryColor(string? expansion, uint entry, string race = "Human", string gender = "Male")
    {
        var lane = TryImportLane(expansion, out var laneError);
        if (lane is null) return BadRequest(new { success = false, error = laneError });
        var ext = ExtractDressingSlots(lane, entry, race, gender);
        if (ext is null || ext.Value.SlotDisks.Count == 0) return Json(new { success = false, error = "no sampleable texture" });

        // Sample the largest painted slot (chest-like slots dominate the colourway).
        var disk = ext.Value.SlotDisks.OrderBy(kv => kv.Key).First().Value;
        var families = _palette.DetectFamilies(disk);
        if (families.Count == 0) return Json(new { success = false, error = "no colour families detected" });
        var chromatic = families.Where(f => f.Family is not ("white" or "black" or "grey")).ToList();
        var primary = (chromatic.Count > 0 ? chromatic : families).OrderByDescending(f => f.Percent).First();
        return Json(new { success = true, primaryHex = HslToHex(primary.MeanHue, Math.Max(0.5f, primary.MeanSat), 0.5f) });
    }

    // HSL (h degrees, s/l 0..1) → #rrggbb, for the colour picker.
    private static string HslToHex(float h, float s, float l)
    {
        h = ((h % 360f) + 360f) % 360f;
        float c = (1 - Math.Abs(2 * l - 1)) * s;
        float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        float mm = l - c / 2;
        float r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
        int R = (int)Math.Round((r + mm) * 255), G = (int)Math.Round((g + mm) * 255), B = (int)Math.Round((b + mm) * 255);
        return $"#{R:x2}{G:x2}{B:x2}";
    }

    // ── Itemization (tier/spec starting-point stats) ────────────────────

    /// <summary>The class → archetype catalog for the spec picker in the Configure-item modal.</summary>
    [HttpGet]
    public IActionResult SpecCatalog() => Json(new { ok = true, classes = SpecProfileCatalog.ForUi() });

    /// <summary>Generate a curated starting-point gameplay draft for one armor slot, nestled into the
    /// vanilla tier curve and shaped to the chosen class/archetype. Read-only; the client fills the
    /// Configure-item modal with the result and the operator edits before importing.</summary>
    [HttpGet]
    public IActionResult Itemize(int inventoryType, int classId = 0, string? archetype = null, int? level = null, double? tier = null)
    {
        var draft = _itemize.Generate(new ItemBudgetRequest(inventoryType, classId, archetype, level, tier));
        var c = draft.Config;
        return Json(new
        {
            ok = true,
            summary = draft.Summary,
            quality = c.Quality,
            itemLevel = c.ItemLevel,
            requiredLevel = c.RequiredLevel,
            buyPrice = c.BuyPrice,
            sellPrice = c.SellPrice,
            allowableClass = c.AllowableClass,
            stats = draft.Stats.Select(s => new { type = s.Type, label = s.Label, value = s.Value }),
            effectSuggestions = draft.EffectSuggestions,
        });
    }

    // ── Import ──────────────────────────────────────────────────────────

    [HttpPost]
    public Task<IActionResult> TbcImport(uint entry, string? name, int setId = 0, string? itemConfig = null) => LaneImport(_lanes.Tbc, entry, name, setId, itemConfig);

    [HttpPost]
    public Task<IActionResult> WotlkImport(uint entry, string? name, int setId = 0, string? itemConfig = null) => LaneImport(_lanes.Wotlk, entry, name, setId, itemConfig);

    /// <summary>POST /ArmorForge/Import (expansion=tbc|wotlk|vanilla, entry, name?, setId, itemConfig?,
    /// recolorHue?/recolorTheory/recolorTier) — lane-keyed form. <paramref name="itemConfig"/> is the optional
    /// typed gameplay contract from the Configure-item modal; the recolor* params bake the previewed recolor
    /// into the shipped textures. expansion=vanilla clones an existing vanilla item instead of importing art.</summary>
    [HttpPost]
    public Task<IActionResult> Import(string? expansion, uint entry, string? name, int setId = 0, string? itemConfig = null,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "fan", string recolorTier = "improved", string? glowColor = null,
        float glowIntensity = 1f) =>
        string.Equals(expansion, "vanilla", StringComparison.OrdinalIgnoreCase)
            // The recolor/glow arguments are forwarded, not dropped. A clone cannot bake either of
            // them, and dropping them here is what made the Appearance panel's "will bake on import"
            // a lie: the operator picked a colour, watched the preview change, cloned, and got the
            // original art with nothing said. CloneVanillaAsync refuses the job instead.
            ? VanillaClone(entry, name, itemConfig,
                recolorRequested: recolorHue.HasValue,
                glowRequested: !string.IsNullOrWhiteSpace(glowColor) || Math.Abs(glowIntensity - 1f) > 0.01f)
            : ImportOnLane(expansion, entry, name, setId, itemConfig, recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor, glowIntensity);

    // Split out so an unrecognised lane key answers 400 with a sentence. ArmorImportSources.Get now
    // throws rather than silently falling back to TBC, and an expression-bodied action had nowhere to
    // catch it — the throw would have escaped as a 500 whose HTML body the result panel renders raw.
    private Task<IActionResult> ImportOnLane(string? expansion, uint entry, string? name, int setId, string? itemConfig,
        float? recolorHue, float? recolorSat, float? recolorLight, string recolorTheory, string recolorTier, string? glowColor, float glowIntensity)
    {
        var lane = TryImportLane(expansion, out var error);
        return lane is null
            ? Task.FromResult<IActionResult>(BadRequest(error))
            : LaneImport(lane, entry, name, setId, itemConfig, recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, HexToRgb255(glowColor), glowIntensity);
    }

    // "#rrggbb" → RGB 0..255 vector for the emitter colour track; null when unset/malformed.
    private static Vector3? HexToRgb255(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return null;
        const System.Globalization.NumberStyles H = System.Globalization.NumberStyles.HexNumber;
        return int.TryParse(hex.AsSpan(0, 2), H, null, out int r)
            && int.TryParse(hex.AsSpan(2, 2), H, null, out int g)
            && int.TryParse(hex.AsSpan(4, 2), H, null, out int b)
            ? new Vector3(r, g, b) : null;
    }

    private async Task<IActionResult> VanillaClone(uint entry, string? name, string? itemConfig,
        bool recolorRequested = false, bool glowRequested = false)
    {
        try
        {
            var (gameplay, cfgErrors) = await _itemConfig.ParseAsync(
                itemConfig, VanillaItemBuildConfigurationTranslator.ArmorInventoryTypes, ArmorInventoryTypeError, HttpContext.RequestAborted);
            if (cfgErrors.Count > 0)
                return BadRequest(string.Join("\n", cfgErrors));

            var r = await _armor.CloneVanillaAsync(entry, name, gameplay, recolorRequested, glowRequested);
            return r.Ok ? Json(ResultDto(r)) : BadRequest(r.Message);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ArmorForge: vanilla clone {Entry} failed", entry); return BadRequest(ex.Message); }
    }

    private async Task<IActionResult> LaneImport(ArmorImportLane lane, uint entry, string? name, int setId, string? itemConfig,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "fan", string recolorTier = "improved", Vector3? glowColor = null,
        float glowIntensity = 1f)
    {
        try
        {
            var (gameplay, cfgErrors) = await _itemConfig.ParseAsync(
                itemConfig, VanillaItemBuildConfigurationTranslator.ArmorInventoryTypes, ArmorInventoryTypeError, HttpContext.RequestAborted);
            if (cfgErrors.Count > 0)
                return BadRequest(string.Join("\n", cfgErrors));

            var r = await _armor.ImportAsync(lane, entry, name, setId, gameplay: gameplay,
                recolorHue: recolorHue, recolorSat: recolorSat, recolorLight: recolorLight, recolorTheory: recolorTheory, recolorTier: recolorTier, glowColor: glowColor,
                glowIntensity: glowIntensity);
            return r.Ok ? Json(ResultDto(r)) : BadRequest(r.Message + (r.Diagnostics.Length > 0 ? "\n" + string.Join("\n", r.Diagnostics.Take(12)) : ""));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ArmorForge: {Lane} import {Entry} failed", lane.Key, entry); return BadRequest(ex.Message); }
    }

    /// <summary>POST /ArmorForge/ImportSet — JSON body with per-piece gameplay configs + set bonuses.
    /// Imports the whole set as a unit: every piece gets its configured value/stats/effects, and the set
    /// gets its bonus table. This is the set-level "whole nine yards" configuration.</summary>
    [HttpPost]
    public async Task<IActionResult> ImportSet([FromBody] ImportSetDto dto)
    {
        if (dto is null) return BadRequest("Missing body.");
        bool vanilla = string.Equals(dto.Expansion, "vanilla", StringComparison.OrdinalIgnoreCase);
        ArmorImportLane? lane = null;
        if (!vanilla)
        {
            lane = TryImportLane(dto.Expansion, out var laneError);
            if (lane is null) return BadRequest(laneError);
        }
        try
        {
            var perPiece = new Dictionary<uint, ValidatedVanillaItemBuildConfiguration>();
            if (dto.Pieces is { Count: > 0 })
            {
                foreach (var pc in dto.Pieces)
                {
                    if (string.IsNullOrWhiteSpace(pc.ItemConfig)) continue;
                    var (cfg, errs) = await _itemConfig.ParseAsync(
                        pc.ItemConfig, VanillaItemBuildConfigurationTranslator.ArmorInventoryTypes, ArmorInventoryTypeError, HttpContext.RequestAborted);
                    if (errs.Count > 0) return BadRequest($"Piece {pc.Entry}: " + string.Join("\n", errs));
                    if (cfg is not null) perPiece[pc.Entry] = cfg;
                }
            }
            var bonuses = (dto.Bonuses ?? new List<BonusDto>()).Select(b => new ArmorSetBonus(b.Threshold, b.SpellId)).ToList();
            var entries = dto.Entries is { Count: > 0 } ? dto.Entries : null;

            if (vanilla) return await VanillaCloneSetAsync(dto, entries, perPiece, bonuses);

            var r = await _armor.ImportSetAsync(lane!, dto.SourceSetId, entries, perPiece, bonuses,
                dto.RequiredSkill, dto.RequiredSkillRank, dto.Name,
                dto.RecolorHue, dto.RecolorSat, dto.RecolorLight, dto.RecolorTheory ?? "none", dto.RecolorTier ?? "improved", HexToRgb255(dto.GlowColor),
                dto.GlowIntensity is { } gi && gi > 0f ? gi : 1f);
            return Json(new
            {
                ok = true, expansion = lane!.Key, setId = r.SetId, name = r.Name, message = r.Message, patchDeployed = r.PatchDeployed,
                patchQueued = r.PatchQueued, patchPending = r.PatchPending,
                serverItemSetDeployed = r.ServerItemSetDeployed, serverItemSetMessage = r.ServerItemSetMessage,
                bonusCount = bonuses.Count(b => b.Threshold > 0 && b.SpellId > 0),
                pieces = r.Pieces.Select(ResultDto),
            });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ArmorForge: set import {Set} failed", dto.SourceSetId); return BadRequest(ex.Message); }
    }

    /// <summary>The vanilla arm of <see cref="ImportSet"/>: clone a stock set's cloneable armor members
    /// into one new forged set.
    ///
    /// Membership comes from the client's ItemSet.dbc rather than a lane catalog, and it is re-derived
    /// here rather than trusted from the browser — the request only NARROWS it. Recolor and glow are
    /// refused for the same reason a single clone refuses them: a clone ships no art.</summary>
    private async Task<IActionResult> VanillaCloneSetAsync(
        ImportSetDto dto,
        List<uint>? entries,
        Dictionary<uint, ValidatedVanillaItemBuildConfiguration> perPiece,
        List<ArmorSetBonus> bonuses)
    {
        if (dto.RecolorHue.HasValue || !string.IsNullOrWhiteSpace(dto.GlowColor)
            || (dto.GlowIntensity is { } g && Math.Abs(g - 1f) > 0.01f))
            return BadRequest(
                "A cloned set cannot carry a recolor or glow tint — its pieces reuse the source items' own " +
                "displays, and new art needs new displays plus a patch rebuild. Import the set from the " +
                "TBC or WotLK lane if you want the appearance baked in.");

        var source = _vanillaSets.Get((int)dto.SourceSetId);
        if (source is null) return BadRequest($"Vanilla set {dto.SourceSetId} is not in the mounted client's ItemSet.dbc.");

        // Re-resolve the members server-side; the request may only narrow the set, never extend it.
        var cloneable = await _armor.ReadVanillaPiecesAsync(source.MemberEntries.ToArray());
        var chosen = cloneable
            .Where(x => entries is null || entries.Contains(x.Entry))
            .OrderBy(x => SlotOrder(x.InventoryType))
            .Select(x => x.Entry)
            .ToList();
        if (chosen.Count == 0)
            return BadRequest($"'{source.Name}' has no cloneable armor pieces (its members are weapons, rings or trinkets).");

        string name = string.IsNullOrWhiteSpace(dto.Name) ? source.Name : dto.Name!.Trim();

        var r = await _armor.CloneVanillaSetAsync(
            source.SetId, chosen, name, perPiece, bonuses, dto.RequiredSkill, dto.RequiredSkillRank);

        return Json(new
        {
            ok = true, expansion = "vanilla", setId = r.SetId, name = r.Name, message = r.Message,
            patchDeployed = r.PatchDeployed, patchQueued = r.PatchQueued, patchPending = r.PatchPending,
            serverItemSetDeployed = r.ServerItemSetDeployed, serverItemSetMessage = r.ServerItemSetMessage,
            bonusCount = bonuses.Count(b => b.Threshold > 0 && b.SpellId > 0),
            pieces = r.Pieces.Select(ResultDto),
            // A cloned SET, unlike a single clone, does ship something: the ItemSet.dbc row that makes it
            // a set at all. The UI has to say so, because every other message in this lane says the
            // opposite.
            requiresRestart = true,
        });
    }

    public sealed class ImportSetDto
    {
        public string? Expansion { get; set; }
        public uint SourceSetId { get; set; }
        public List<uint>? Entries { get; set; }
        public string? Name { get; set; }
        public List<PieceCfgDto>? Pieces { get; set; }
        public List<BonusDto>? Bonuses { get; set; }
        public int RequiredSkill { get; set; }
        public int RequiredSkillRank { get; set; }
        public float? RecolorHue { get; set; }
        public float? RecolorSat { get; set; }
        public float? RecolorLight { get; set; }
        public string? RecolorTheory { get; set; }
        public string? RecolorTier { get; set; }
        public string? GlowColor { get; set; }
        public float? GlowIntensity { get; set; }
    }
    public sealed class PieceCfgDto { public uint Entry { get; set; } public string? ItemConfig { get; set; } }

    private static object ResultDto(CustomArmorBuildResult r) => new
    {
        ok = r.Ok, tbcEntry = r.TbcEntry, sourceEntry = r.TbcEntry, expansion = r.SourceExpansion,
        itemEntry = r.ItemEntry, displayId = r.DisplayId, name = r.Name,
        family = r.ArmorTypeKey, renderKind = r.RenderKind.ToString(), message = r.Message,
        models = r.ModelMemberCount, components = r.ComponentCount, diagnostics = r.Diagnostics,
        apply = r.Apply is null ? null : new
        {
            sql = new { ok = r.Apply.SqlApplied, msg = r.Apply.SqlMessage },
            reload = new { ok = r.Apply.Reloaded, msg = r.Apply.ReloadMessage },
            // Null when there is no patch step at all, using the same "omit it rather than report a
            // false failure" idiom as serverSets below. A vanilla clone ships no art, so the untouched
            // PatchDeployed=false / empty-message pair used to render as a red, blank "Deploy ✗" row on
            // every successful clone — the operator reading a completed job as half-broken.
            deploy = r.Apply.PatchRequired ? new { ok = r.Apply.PatchDeployed, queued = r.Apply.PatchQueued, pending = r.Apply.PatchPending, msg = r.Apply.PatchDeployMessage } : null,
            serverSets = r.Apply.ServerItemSetState == nameof(ItemSetDeployState.NotNeeded) ? null : new
            {
                ok = r.Apply.ServerItemSetState == nameof(ItemSetDeployState.Deployed),
                msg = r.Apply.ServerItemSetMessage,
            },
        },
    };

    // ── Registry ────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> ListArmor()
    {
        var rows = await _armor.LoadArmorRowsAsync(includeBlobs: false);
        var sets = await _armor.ListSetsAsync();
        return Json(new
        {
            pieces = rows.Select(r => new
            {
                displayId = r.DisplayId, itemEntry = r.ItemEntry, name = r.Name, family = r.ArmorTypeKey,
                renderKind = r.RenderKind, inventoryType = r.InventoryType, setId = r.SetId,
                models = r.Models.Count, components = r.Components.Count,
            }),
            sets = sets.Select(s => new { s.SetId, s.Name, s.MemberCount, s.BonusCount, bonuses = s.Bonuses, members = s.MemberEntries }),
        });
    }

    [HttpPost] public async Task<IActionResult> Delete(long displayId) => Json(await _armor.DeleteAsync(displayId));
    [HttpPost] public async Task<IActionResult> DeleteSet(int setId) => Json(await _armor.DeleteSetAsync(setId));

    public sealed class BulkDeleteDto { public List<long> DisplayIds { get; set; } = new(); public List<int> SetIds { get; set; } = new(); }

    /// <summary>POST /ArmorForge/DeleteMany — delete ticked sets and pieces with one reload, one repack
    /// and one queued unified rebuild. Body: <c>{ "displayIds": [..], "setIds": [..] }</c>.</summary>
    [HttpPost]
    public async Task<IActionResult> DeleteMany([FromBody] BulkDeleteDto dto)
    {
        if (dto is null || (dto.DisplayIds.Count == 0 && dto.SetIds.Count == 0))
            return BadRequest("Nothing selected.");
        return Json(await _armor.DeleteManyAsync(dto.DisplayIds, dto.SetIds));
    }
    [HttpPost] public async Task<IActionResult> RebuildPatch() => Json(new { ok = true, message = await _armor.RebuildPatchAsync("manual") });

    [HttpGet]
    public IActionResult DownloadPatch()
    {
        // Forged armor ships in the unified patch now, not an archive of its own. Redirect rather
        // than serve the stale lane-local artifact — there is exactly one file to install.
        return RedirectToAction("DownloadPatch", "UnifiedPatch");
    }

    // ── Vanilla set bonuses (optional) ──────────────────────────────────

    [HttpPost]
    public async Task<IActionResult> SaveSet([FromBody] SaveSetDto dto)
    {
        if (dto is null) return BadRequest("Missing body.");
        try
        {
            var res = await _armor.SaveSetAsync(new ArmorSetSaveRequest
            {
                SetId = dto.SetId, Name = dto.Name ?? "", MemberEntries = dto.MemberEntries ?? new List<long>(),
                Bonuses = (dto.Bonuses ?? new List<BonusDto>()).Select(b => new ArmorSetBonus(b.Threshold, b.SpellId)).ToList(),
                RequiredSkill = dto.RequiredSkill, RequiredSkillRank = dto.RequiredSkillRank,
            });
            return Json(res);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ArmorForge: SaveSet failed"); return BadRequest(ex.Message); }
    }

    public sealed class SaveSetDto
    {
        public int SetId { get; set; }
        public string? Name { get; set; }
        public List<long>? MemberEntries { get; set; }
        public List<BonusDto>? Bonuses { get; set; }
        public int RequiredSkill { get; set; }
        public int RequiredSkillRank { get; set; }
    }
    public sealed class BonusDto { public int Threshold { get; set; } public int SpellId { get; set; } }
}
