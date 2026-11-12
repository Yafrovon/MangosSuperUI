using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Parses vanilla 1.12.1 WDBC files at startup and provides lookup dictionaries
/// for resolving item display IDs and spell icon IDs to icon filenames.
/// 
/// WDBC format: 20-byte header (magic, recordCount, fieldCount, recordSize, stringBlockSize)
///              followed by fixed-size records, then a string block.
///              String fields store a uint32 offset into the string block.
/// </summary>
public partial class DbcService
{
    private readonly ILogger<DbcService> _logger;
    private readonly IConfiguration _configuration;

    // ── Lookup dictionaries (populated at startup) ─────────────────────────

    /// <summary>displayId → icon filename (lowercase, no extension, no path).
    /// Example: 29604 → "inv_sword_39"</summary>
    public IReadOnlyDictionary<uint, string> ItemDisplayIcons { get; private set; }
        = new Dictionary<uint, string>();

    /// <summary>spellIconId → icon filename (lowercase, no extension, no path).
    /// Example: 1 → "spell_fire_fireball"</summary>
    public IReadOnlyDictionary<uint, string> SpellIcons { get; private set; }
        = new Dictionary<uint, string>();

    /// <summary>durationIndex → (duration_ms, duration_per_level, max_duration)</summary>
    public IReadOnlyDictionary<uint, SpellDurationEntry> SpellDurations { get; private set; }
        = new Dictionary<uint, SpellDurationEntry>();

    /// <summary>castTimeIndex → (base_ms, per_level_ms, minimum_ms)</summary>
    public IReadOnlyDictionary<uint, SpellCastTimeEntry> SpellCastTimes { get; private set; }
        = new Dictionary<uint, SpellCastTimeEntry>();

    /// <summary>rangeIndex → (range_min, range_max, display_name)</summary>
    public IReadOnlyDictionary<uint, SpellRangeEntry> SpellRanges { get; private set; }
        = new Dictionary<uint, SpellRangeEntry>();

    /// <summary>spellId → visible SpellDbcEntry (lightweight, for ordinary source-spell search).
    /// Hidden client helper spells are deliberately omitted from this view.</summary>
    public IReadOnlyDictionary<uint, SpellDbcEntry> SpellEntries { get; private set; }
        = new Dictionary<uint, SpellDbcEntry>();

    /// <summary>Every spell row installed in the active Vanilla Spell.dbc, including hidden or
    /// unnamed passive/helper rows used by item equip effects. Validation must use this complete
    /// view: an item spell can be valid even when it has no spellbook-facing label.</summary>
    public IReadOnlyDictionary<uint, SpellDbcEntry> AllSpellEntries { get; private set; }
        = new Dictionary<uint, SpellDbcEntry>();

    /// <summary>IDs installed in the active Vanilla SkillLine.dbc and Faction.dbc.
    /// Item Forge uses these sets to reject impossible numeric equip requirements.</summary>
    public IReadOnlySet<uint> SkillLineIds { get; private set; } = new HashSet<uint>();
    public IReadOnlySet<uint> FactionIds { get; private set; } = new HashSet<uint>();

    /// <summary>Reputation-bearing Faction.dbc rows with their enUS name and parent
    /// (header) faction. Used by Gameplay Tuning > Rates > Reputation.</summary>
    public IReadOnlyList<ReputationFactionDbc> ReputationFactions { get; private set; } = Array.Empty<ReputationFactionDbc>();

    /// <summary>displayId → model info (model names + texture names from DBC fields [1-4]).
    /// Used by ItemTextureService to resolve displayId → M2 file path.</summary>
    public IReadOnlyDictionary<uint, ItemModelDbc> ItemModelInfos { get; private set; }
        = new Dictionary<uint, ItemModelDbc>();

    /// <summary>gameobject displayId → client model path from GameObjectDisplayInfo.dbc
    /// (e.g. "World\GenericTribal\PassiveDoodads\Banner\TribalBanner01.mdx" or a .wmo).
    /// Used by GameObjectModelService to generate GLB previews on demand.</summary>
    public IReadOnlyDictionary<uint, string> GameObjectDisplayModels { get; private set; }
        = new Dictionary<uint, string>();

    /// <summary>All rows of CharSections.dbc (vanilla 1.12 character-creation
    /// texture table). Queried via GetDefaultFaceSection — and future helpers
    /// for skin tones / hair styles. Used by CharacterSkinCompositor to layer
    /// face textures onto the body atlas.</summary>
    public IReadOnlyList<CharSectionDbc> CharacterSections { get; private set; }
        = Array.Empty<CharSectionDbc>();

    /// <summary>(race, sex, hairstyle) to the matching category-0 M2 geoset.</summary>
    public IReadOnlyDictionary<(uint Race, uint Sex, uint Style), int> CharacterHairGeosets { get; private set; }
        = new Dictionary<(uint Race, uint Sex, uint Style), int>();

    /// <summary>
    /// HelmetGeosetVisData.dbc — vanilla 1.12, 6 fields × 24 bytes, ~16 rows.
    /// Maps helmetGeosetVisID → race-indexed bitmasks for hiding hair, facial
    /// hair, and ears when a helm is equipped. Used by DoesHelmHideHair().
    /// </summary>
    private IReadOnlyDictionary<uint, HelmetGeosetVisEntry> HelmetGeosetVisData { get; set; }
        = new Dictionary<uint, HelmetGeosetVisEntry>();

    /// <summary>All rows of WorldMapArea.dbc — the client's zone bounding
    /// boxes. Used by LootifierController for zone-scoped batch scans:
    /// creature spawns are filtered by position against these boxes, since
    /// the VMaNGOS creature table has no zone column.</summary>
    public IReadOnlyList<WorldMapZoneDbc> WorldMapZones { get; private set; }
        = Array.Empty<WorldMapZoneDbc>();

    // ── Status / diagnostics ──────────────────────────────────────────────

    public bool IsLoaded { get; private set; }
    public string? LoadError { get; private set; }
    public string DbcPath { get; private set; } = string.Empty;
    public Dictionary<string, int> LoadedCounts { get; private set; } = new();

    // ── Constructor ───────────────────────────────────────────────────────

    public DbcService(ILogger<DbcService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        Load();
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Resolve an item's displayId to an icon web path, or fallback.</summary>
    public string GetItemIconPath(uint displayId)
    {
        if (ItemDisplayIcons.TryGetValue(displayId, out var name))
            return $"/Icon/Get?name={name}";
        return "/Icon/Get?name=inv_misc_questionmark";
    }

    /// <summary>Resolve a spell's SpellIconID to an icon web path, or fallback.</summary>
    public string GetSpellIconPath(uint spellIconId)
    {
        if (SpellIcons.TryGetValue(spellIconId, out var name))
            return $"/Icon/Get?name={name}";
        return "/Icon/Get?name=inv_misc_questionmark";
    }

    /// <summary>Resolve a displayId to its model info (model names + texture names).</summary>
    public ItemModelDbc? GetItemModelInfo(uint displayId)
    {
        if (ItemModelInfos.TryGetValue(displayId, out var info))
            return info;
        return null;
    }

    /// <summary>Resolve a gameobject displayId to its client model path, or null.</summary>
    public string? GetGameObjectModelPath(uint displayId)
    {
        if (GameObjectDisplayModels.TryGetValue(displayId, out var path))
            return path;
        return null;
    }

    /// <summary>
    /// Find the default Face row for a given (race, gender) — BaseSection=Face (1),
    /// VariationIndex=0, ColorIndex=0. This is the vanilla character-creation
    /// "default appearance" preset. Returns null if no row matches.
    ///
    /// Used by CharacterSkinCompositor to resolve face_lower (TextureName1)
    /// and face_upper (TextureName2) for the default skin PNG.
    /// </summary>
    public CharSectionDbc? GetDefaultFaceSection(uint race, uint sex)
    {
        foreach (var row in CharacterSections)
        {
            if (row.Race == race && row.Sex == sex
                && row.BaseSection == 1     // Face
                && row.VariationIndex == 0
                && row.ColorIndex == 0)
                return row;
        }
        return null;
    }

    /// <summary>
    /// Find the default Hair row for a given (race, gender) — BaseSection=Hair (3),
    /// VariationIndex=0 (hair style 0), ColorIndex=0 (hair color 0). Returns
    /// null if no row matches.
    ///
    /// Used by CharacterModelService to bind the hair color BLP
    /// (TextureName1) to the M2 texture slot Type=6 (TEX_COMPONENT_CHAR_HAIR).
    /// Without this, hair geosets fall through to the body atlas in
    /// SkinnedGlbWriter's material assignment, producing the
    /// "face-painted-on-hair" artifact visible on Dwarf/Tauren etc.
    ///
    /// CharSections Hair row textures:
    ///   TextureName1 — hair color BLP (the one we want for the hair slot)
    ///   TextureName2 — sideburns / lower-hair overlay (painted onto
    ///                  FACE_LOWER in vanilla — future session)
    ///   TextureName3 — eyebrows / upper-hair overlay (painted onto
    ///                  FACE_UPPER in vanilla — future session)
    /// </summary>
    public CharSectionDbc? GetDefaultHairSection(uint race, uint sex)
    {
        foreach (var row in CharacterSections)
        {
            if (row.Race == race && row.Sex == sex
                && row.BaseSection == 3     // Hair
                && row.VariationIndex == 0
                && row.ColorIndex == 0)
                return row;
        }
        return null;
    }

    /// <summary>
    /// Resolve a visible default hairstyle whose texture and geometry describe
    /// the same character-creation choice. Style 9 preserves the established
    /// SuperUI look; races without it fall back through their available styles.
    /// </summary>
    public CharacterHairAppearance? GetPreferredHairAppearance(uint race, uint sex)
    {
        var styles = CharacterHairGeosets.Keys
            .Where(k => k.Race == race && k.Sex == sex)
            .Select(k => k.Style)
            .Distinct()
            .OrderByDescending(style => style == 9)
            .ThenByDescending(style => style);

        foreach (uint style in styles)
        {
            var section = CharacterSections.FirstOrDefault(row =>
                row.Race == race && row.Sex == sex &&
                row.BaseSection == 3 && row.VariationIndex == style && row.ColorIndex == 0);

            if (section == null) continue;
            if (!CharacterHairGeosets.TryGetValue((race, sex, style), out int geosetId)) continue;
            return new CharacterHairAppearance(style, geosetId, section);
        }

        return null;
    }

    /// <summary>
    /// Check whether a helm's HelmetGeosetVisData row hides hair for a
    /// given race. Uses the proper DBC decode: field[1] (hairFlags) is a
    /// bitmask over ChrRaces — if bit (1 &lt;&lt; raceId) is set, that race's
    /// hair should be suppressed.
    ///
    /// Replaces the v1!=v2 heuristic from Session L, which failed on
    /// helms where both m_helmetGeosetVisID fields point to the same row
    /// (e.g. Dreadnaught Helmet: v1=v2=368, hairFlags=0xFFFFFFFF).
    ///
    /// Vanilla race IDs: 1=Human, 2=Orc, 3=Dwarf, 4=NightElf,
    /// 5=Undead, 6=Tauren, 7=Gnome, 8=Troll.
    /// </summary>
    public bool DoesHelmHideHair(uint helmetGeosetVisId, uint raceId)
    {
        if (helmetGeosetVisId == 0)
            return false;  // no vis row assigned → show hair
        if (!HelmetGeosetVisData.TryGetValue(helmetGeosetVisId, out var entry))
            return false;  // unknown row → safe default: show hair
        return (entry.HairFlags >> (int)raceId & 1) == 1;
    }

    /// <summary>
    /// Register a custom ItemDisplayInfo entry at runtime.
    /// Clones the icon and model info from an existing displayId so the new
    /// displayId is fully functional in SuperUI (icon lookup, texture extraction,
    /// GLB generation) without requiring a restart or DBC reload.
    /// Called by ItemRetextureService after creating a retexture.
    /// </summary>
    /// <param name="itemVisual">The custom display's OWN ItemVisual (enchant glow). Null inherits the
    /// donor's, which is right for a retexture cloning a real display but WRONG for a Forge writing
    /// its own row: a forged weapon's chosen glow lives in custom_weapon_display.item_visual, and
    /// inheriting the donor's 0 meant every web-side glow lookup
    /// (ItemTextureService.ResolveVisualEffects, WeaponForgeController.PreviewForged) resolved
    /// nothing — the Items page rendered forged weapons with no enchant effect while the Forge's own
    /// preview, which reads the build state rather than this cache, showed it correctly.</param>
    public void RegisterCustomDisplayEntry(uint newDisplayId, uint sourceDisplayId,
        string? customModelName = null, string? customTextureName = null, string? customModelName2 = null,
        uint? itemVisual = null)
    {
        // Clone icon from source
        if (ItemDisplayIcons is Dictionary<uint, string> iconDict)
        {
            if (iconDict.TryGetValue(sourceDisplayId, out var iconName))
                iconDict[newDisplayId] = iconName;
        }

        // Clone model info from source, with optional overrides
        if (ItemModelInfos is Dictionary<uint, ItemModelDbc> modelDict)
        {
            if (modelDict.TryGetValue(sourceDisplayId, out var sourceModel))
            {
                var custom = new ItemModelDbc
                {
                    ModelName1 = customModelName ?? sourceModel.ModelName1,
                    // Weapon Forge passes "" to clear a donor's second model, or its own SUI_W name
                    // when the family mirrors ModelName2 (thrown weapons); null inherits.
                    ModelName2 = customModelName2 ?? sourceModel.ModelName2,
                    TextureName1 = customTextureName ?? sourceModel.TextureName1,
                    // TextureName2 must follow the model pair, never the donor.
                    //
                    // A caller that passes customModelName2 is stating the model pair explicitly —
                    // it is a Forge writing its own row, not a retexture cloning a real display. In
                    // that case the donor's TextureName2 is a stock skin for a completely different
                    // item, and it is not cosmetic: ItemTextureService.EnsureShoulderGlb builds the
                    // RIGHT spaulder from ModelName2 + TextureName2, so the forged right-hand mesh
                    // gets skinned with the donor's BLP while the left gets the forged one. Sampled
                    // through the forged material's alpha key that punches the pad into disconnected
                    // islands — "fine in game, exploded in the previewer", because the patch DBC has
                    // it right (ArmorDisplayInfoRow.BuildAndAdd writes TextureName2 == TextureName1
                    // whenever ModelName2 is set) and only this in-memory row was wrong.
                    //
                    // Mirroring that patch rule exactly is what keeps the previewer and the client
                    // showing the same thing. A null customModelName2 still inherits, which is right
                    // for ItemRetextureService: there the donor row IS the item being retextured.
                    TextureName2 = customModelName2 is null
                        ? sourceModel.TextureName2
                        : customModelName2.Length == 0
                            ? ""
                            : customTextureName ?? sourceModel.TextureName1,
                    // Session C: clone (don't share reference) the array
                    // fields so a future RegisterCustomDisplayEntry call
                    // can't accidentally mutate the source row.
                    BodyTextures = sourceModel.BodyTextures != null
                        ? (string[])sourceModel.BodyTextures.Clone()
                        : new string[8],
                    GeosetGroup = sourceModel.GeosetGroup != null
                        ? (int[])sourceModel.GeosetGroup.Clone()
                        : new int[3],
                    // Session L: scalars — no clone needed, just copy.
                    HelmetGeosetVis1 = sourceModel.HelmetGeosetVis1,
                    HelmetGeosetVis2 = sourceModel.HelmetGeosetVis2,
                    // Session N: inherit the source's item visual so a
                    // retextured enchanted weapon keeps its mounted effect.
                    ItemVisualId = itemVisual ?? sourceModel.ItemVisualId,
                };
                modelDict[newDisplayId] = custom;
            }
        }

        // Invalidate reverse icon cache
        _iconToDisplayIds = null;

        _logger.LogInformation(
            "DbcService: Registered custom displayId {New} (cloned from {Source}, model={Model})",
            newDisplayId, sourceDisplayId, customModelName ?? "(same)");
    }

    /// <summary>
    /// Register a custom ItemDisplayInfo entry from a row the caller states in full.
    ///
    /// This is the form a Forge should use. The clone-based overload exists for
    /// <c>ItemRetextureService</c>, where the donor row genuinely IS the item being retextured, so
    /// inheriting its unspecified fields is correct. A Forge has no such donor: it picks the first
    /// stock row that merely LOOKS like the right shape (any <c>LShoulder*</c>, any <c>Helm_*</c>)
    /// and everything it does not override — TextureName2, geoset groups, helmet visibility masks,
    /// body-atlas stems, item visual — silently comes from an unrelated item. The previewer then
    /// renders something the client never will, because the patch DBC was written correctly.
    ///
    /// Pass exactly what was written into the patch row so the two agree by construction.
    /// </summary>
    /// <summary>
    /// Register ONLY an inventory icon for a custom display id, with no model row.
    ///
    /// For pieces that genuinely have no model: PAINTED armour (chest, gloves, legs, waist, wrists,
    /// feet, shirt, tabard) paints the shared character body atlas and is served from
    /// custom_armor_component by BodyAtlasTextureService. Those displays still need an icon, and
    /// registering a model row for them would advertise an .mdx that does not exist.
    /// </summary>
    public void RegisterCustomDisplayIcon(uint displayId, string iconName)
    {
        if (string.IsNullOrEmpty(iconName)) return;
        if (ItemDisplayIcons is Dictionary<uint, string> iconDict)
        {
            // Lowercase to match the base loader's contract (the dictionary is documented as
            // "icon filename (lowercase, no extension, no path)"). IconSearch does an ordinal
            // Contains against a lowercased query, so a mixed-case custom entry is unfindable.
            iconDict[displayId] = iconName.ToLowerInvariant();
            _iconToDisplayIds = null;   // the reverse map is memoised; a new icon invalidates it
        }
    }

    public void RegisterCustomDisplayEntry(uint newDisplayId, ItemModelDbc row, string? iconName = null)
    {
        if (ItemDisplayIcons is Dictionary<uint, string> iconDict && !string.IsNullOrEmpty(iconName))
            iconDict[newDisplayId] = iconName.ToLowerInvariant();   // see RegisterCustomDisplayIcon

        if (ItemModelInfos is Dictionary<uint, ItemModelDbc> modelDict)
        {
            // Defensive copies of the array fields: the caller keeps its own instances, and a later
            // registration must not be able to mutate a row already in the cache.
            row.BodyTextures = row.BodyTextures is { Length: 8 } bt ? (string[])bt.Clone() : new string[8];
            row.GeosetGroup = row.GeosetGroup is { Length: 3 } gg ? (int[])gg.Clone() : new int[3];
            row.ModelName1 ??= "";
            row.ModelName2 ??= "";
            row.TextureName1 ??= "";
            row.TextureName2 ??= "";
            for (int i = 0; i < row.BodyTextures.Length; i++) row.BodyTextures[i] ??= "";
            modelDict[newDisplayId] = row;
        }

        _iconToDisplayIds = null;

        _logger.LogInformation(
            "DbcService: Registered custom displayId {New} from an explicit row (model1={M1}, model2={M2}, tex1={T1}, tex2={T2})",
            newDisplayId, row.ModelName1, row.ModelName2, row.TextureName1, row.TextureName2);
    }

    /// <summary>Re-read all DBC files (e.g., after path change in Settings).</summary>
    public void Reload()
    {
        _iconToDisplayIds = null; // Invalidate reverse cache
        Load();
    }

    // ── Session N diagnostic ──────────────────────────────────────────────

    /// <summary>
    /// Raw-byte dump of a single ItemDisplayInfo.dbc row plus a histogram of
    /// non-zero values at each field index across the full table.
    ///
    /// Built to settle the "field 22 = m_itemVisual?" question empirically
    /// when Thunderfury's displayId 30606 came back with itemVisualId=0
    /// even though it visibly carries lightning in-game. Two things to look
    /// at in the response:
    ///
    ///   1. row.fields[22] — the value we currently call itemVisualId. If
    ///      it's 0 for 30606 but non-zero for other "visually flashy"
    ///      displayIds (e.g. Sulfuras displayId 34471), the offset is right
    ///      and Thunderfury just doesn't bind its visual through this DBC.
    ///   2. histogram[i] — count of non-zero values at field i across all
    ///      rows. The "m_itemVisual" column should have a handful of hundreds
    ///      of hits — not zero, not tens of thousands. If field [22] shows
    ///      0 hits but field [21] or [23] shows ~hundreds, we have the
    ///      wrong offset.
    /// </summary>
    public object DumpItemDisplayInfoRow(uint displayId)
    {
        var path = Path.Combine(DbcPath, "ItemDisplayInfo.dbc");
        if (!File.Exists(path))
            return new { error = $"File not found: {path}" };

        var (records, stringBlock, recordSize) = ReadDbcFile(path);
        int recordCount = records.Length / recordSize;
        int fieldCount = recordSize / 4;

        // Build the histogram in a single pass, capture the target row.
        var histogram = new int[fieldCount];
        object? targetRow = null;
        uint? minId = null, maxId = null;

        for (int i = 0; i < recordCount; i++)
        {
            int offset = i * recordSize;
            uint id = BitConverter.ToUInt32(records, offset);
            if (minId == null || id < minId) minId = id;
            if (maxId == null || id > maxId) maxId = id;

            for (int f = 0; f < fieldCount; f++)
            {
                uint v = BitConverter.ToUInt32(records, offset + f * 4);
                if (v != 0) histogram[f]++;
            }

            if (id == displayId)
            {
                var fields = new uint[fieldCount];
                for (int f = 0; f < fieldCount; f++)
                    fields[f] = BitConverter.ToUInt32(records, offset + f * 4);
                // Decode each stringref to its string value so we can tell
                // stringrefs from real integer fields at a glance.
                var strings = new string[fieldCount];
                for (int f = 0; f < fieldCount; f++)
                    strings[f] = ReadString(stringBlock, fields[f]);
                targetRow = new
                {
                    found = true,
                    id,
                    rowOffset = offset,
                    fields,
                    strings,
                };
            }
        }

        return new
        {
            file = path,
            recordCount,
            fieldCount,
            recordSize,
            stringBlockSize = stringBlock.Length,
            minId,
            maxId,
            histogram,  // histogram[i] = how many rows have a non-zero value at field i
            row = targetRow ?? (object)new { found = false, displayId },
        };
    }

    // ── Reverse icon lookup (for icon picker) ──────────────────────────────

    private Dictionary<string, List<uint>>? _iconToDisplayIds;

    /// <summary>
    /// Returns a reverse lookup: icon filename → list of displayIds that use it.
    /// Lazy-built from ItemDisplayIcons on first call, cached until Reload().
    /// </summary>
    public Dictionary<string, List<uint>> GetIconToDisplayIds()
    {
        if (_iconToDisplayIds != null)
            return _iconToDisplayIds;

        var map = new Dictionary<string, List<uint>>();
        foreach (var kv in ItemDisplayIcons)
        {
            if (!map.TryGetValue(kv.Value, out var list))
            {
                list = new List<uint>();
                map[kv.Value] = list;
            }
            list.Add(kv.Key);
        }

        _iconToDisplayIds = map;
        return _iconToDisplayIds;
    }

    // ── Core load logic ───────────────────────────────────────────────────

    private void Load()
    {
        IsLoaded = false;
        LoadError = null;
        LoadedCounts.Clear();
        AllSpellEntries = new Dictionary<uint, SpellDbcEntry>();
        SpellEntries = new Dictionary<uint, SpellDbcEntry>();
        SkillLineIds = new HashSet<uint>();
        FactionIds = new HashSet<uint>();
        ReputationFactions = Array.Empty<ReputationFactionDbc>();

        // Read path from config — check server-config.json override first, then appsettings.json
        DbcPath = _configuration["Vmangos:DbcPath"]
                   ?? "/home/wowvmangos/vmangos/run/data/5875/dbc";

        if (!Directory.Exists(DbcPath))
        {
            LoadError = $"DBC directory not found: {DbcPath}";
            _logger.LogWarning("DbcService: {Error}", LoadError);
            return;
        }

        _logger.LogInformation("DbcService: Loading DBC files from {Path}", DbcPath);

        try
        {
            ItemDisplayIcons = LoadItemDisplayInfo(Path.Combine(DbcPath, "ItemDisplayInfo.dbc"));
            ItemModelInfos = LoadItemModelInfo(Path.Combine(DbcPath, "ItemDisplayInfo.dbc"));
            GameObjectDisplayModels = LoadGameObjectDisplayInfo(Path.Combine(DbcPath, "GameObjectDisplayInfo.dbc"));
            SpellIcons = LoadSpellIcon(Path.Combine(DbcPath, "SpellIcon.dbc"));
            SpellDurations = LoadSpellDuration(Path.Combine(DbcPath, "SpellDuration.dbc"));
            SpellCastTimes = LoadSpellCastTimes(Path.Combine(DbcPath, "SpellCastTimes.dbc"));
            SpellRanges = LoadSpellRange(Path.Combine(DbcPath, "SpellRange.dbc"));
            AllSpellEntries = LoadSpellEntries(Path.Combine(DbcPath, "Spell.dbc"));
            SpellEntries = AllSpellEntries
                .Where(kv => !kv.Value.Hidden && !string.IsNullOrWhiteSpace(kv.Value.Name))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            SkillLineIds = LoadEntryIds(Path.Combine(DbcPath, "SkillLine.dbc"), "SkillLine");
            FactionIds = LoadFactionRequirementIds(Path.Combine(DbcPath, "Faction.dbc"));
            ReputationFactions = LoadReputationFactions(Path.Combine(DbcPath, "Faction.dbc"));
            CharacterSections = LoadCharSections(Path.Combine(DbcPath, "CharSections.dbc"));
            CharacterHairGeosets = LoadCharHairGeosets(Path.Combine(DbcPath, "CharHairGeosets.dbc"));
            HelmetGeosetVisData = LoadHelmetGeosetVisData(Path.Combine(DbcPath, "HelmetGeosetVisData.dbc"));
            WorldMapZones = LoadWorldMapArea(Path.Combine(DbcPath, "WorldMapArea.dbc"));

            IsLoaded = true;
            _logger.LogInformation("DbcService: Loaded successfully — {Counts}",
                string.Join(", ", LoadedCounts.Select(kv => $"{kv.Key}: {kv.Value}")));
        }
        catch (Exception ex)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
            AllSpellEntries = new Dictionary<uint, SpellDbcEntry>();
            SpellEntries = new Dictionary<uint, SpellDbcEntry>();
            SkillLineIds = new HashSet<uint>();
            FactionIds = new HashSet<uint>();
            _logger.LogError(ex, "DbcService: Failed to load DBC files");
        }
    }

    // ── Individual DBC parsers ────────────────────────────────────────────

    /// <summary>
    /// ItemDisplayInfo.dbc — 23 fields, 92 bytes per record.
    /// Field layout (all uint32), corrected against the raw installed-DBC evidence in
    /// WEAPON_GEN.md §2.2 (field 9 is nonzero in only 11 armor/cape/tabard rows with values 1/2 =
    /// flags; field 10 is nonzero in 1,008 rows carrying ranged SpellVisual values = SpellVisualID):
    ///   [0] m_ID
    ///   [1-2] m_modelName[2]         (stringref) — logical name, includes .mdx
    ///   [3-4] m_modelTexture[2]      (stringref) — bare texture stems
    ///   [5] m_inventoryIcon           (stringref) ← THIS IS WHAT WE WANT
    ///   [6-8] m_geosetGroup[3]
    ///   [9] m_flags                    (sparse; 1/2 on a few armor rows)
    ///   [10] m_spellVisualID           (bows 5, firearms 224, thrown 98, wands 225/226)
    ///   [11] m_groupSoundIndex
    ///   [12-13] m_helmetGeosetVisID[2]
    ///   [14-21] m_texture[8]         (stringref) — body-atlas armor components
    ///   [22] m_itemVisual
    /// </summary>
    private Dictionary<uint, string> LoadItemDisplayInfo(string filePath)
    {
        var dict = new Dictionary<uint, string>();
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: File not found: {File}", filePath);
            LoadedCounts["ItemDisplayInfo"] = 0;
            return dict;
        }

        var (records, stringBlock, recordSize) = ReadDbcFile(filePath);

        for (int i = 0; i < records.Length / recordSize; i++)
        {
            int offset = i * recordSize;
            uint id = BitConverter.ToUInt32(records, offset);                   // field 0
            uint iconOffset = BitConverter.ToUInt32(records, offset + 5 * 4);   // field 5

            string iconName = ReadString(stringBlock, iconOffset);
            if (!string.IsNullOrEmpty(iconName))
            {
                // DBC stores: "INV_Sword_39" — normalize to lowercase for filename match
                dict[id] = iconName.ToLowerInvariant();
            }
        }

        LoadedCounts["ItemDisplayInfo"] = dict.Count;
        _logger.LogInformation("DbcService: Parsed {Count} ItemDisplayInfo entries", dict.Count);
        return dict;
    }

    /// <summary>
    /// SpellIcon.dbc — 2 fields, 8 bytes per record.
    /// Field layout:
    ///   [0] m_ID           (uint32)
    ///   [1] m_textureFilename (stringref) — e.g. "Interface\Icons\Spell_Fire_Fireball"
    /// </summary>
    private Dictionary<uint, string> LoadSpellIcon(string filePath)
    {
        var dict = new Dictionary<uint, string>();
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: File not found: {File}", filePath);
            LoadedCounts["SpellIcon"] = 0;
            return dict;
        }

        var (records, stringBlock, recordSize) = ReadDbcFile(filePath);

        for (int i = 0; i < records.Length / recordSize; i++)
        {
            int offset = i * recordSize;
            uint id = BitConverter.ToUInt32(records, offset);
            uint nameOffset = BitConverter.ToUInt32(records, offset + 4);

            string texturePath = ReadString(stringBlock, nameOffset);
            if (!string.IsNullOrEmpty(texturePath))
            {
                // DBC stores: "Interface\Icons\Spell_Fire_Fireball"
                // We want just: "spell_fire_fireball"
                string iconName = texturePath
                    .Replace("Interface\\Icons\\", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Interface/Icons/", "", StringComparison.OrdinalIgnoreCase)
                    .ToLowerInvariant();

                dict[id] = iconName;
            }
        }

        LoadedCounts["SpellIcon"] = dict.Count;
        _logger.LogInformation("DbcService: Parsed {Count} SpellIcon entries", dict.Count);
        return dict;
    }

    /// <summary>
    /// GameObjectDisplayInfo.dbc — vanilla 1.12: 12 fields, 48 bytes per record.
    ///   [0] m_ID        (uint32)  — the gameobject displayId
    ///   [1] m_modelName (stringref) — client model path, .mdx/.mdl (M2) or .wmo
    ///   [2..11] m_Sound[10]
    /// The path is kept verbatim (backslashes, extension included) — the MPQ
    /// reader and GameObjectModelService handle extension/slash variations.
    /// </summary>
    private Dictionary<uint, string> LoadGameObjectDisplayInfo(string filePath)
    {
        var dict = new Dictionary<uint, string>();
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: File not found: {File}", filePath);
            LoadedCounts["GameObjectDisplayInfo"] = 0;
            return dict;
        }

        var (records, stringBlock, recordSize) = ReadDbcFile(filePath);

        for (int i = 0; i < records.Length / recordSize; i++)
        {
            int offset = i * recordSize;
            uint id = BitConverter.ToUInt32(records, offset);
            uint nameOffset = BitConverter.ToUInt32(records, offset + 4);

            string modelPath = ReadString(stringBlock, nameOffset);
            if (!string.IsNullOrEmpty(modelPath))
                dict[id] = modelPath;
        }

        LoadedCounts["GameObjectDisplayInfo"] = dict.Count;
        _logger.LogInformation("DbcService: Parsed {Count} GameObjectDisplayInfo entries", dict.Count);
        return dict;
    }

    /// <summary>
    /// SpellDuration.dbc — 4 fields, 16 bytes per record. No strings.
    ///   [0] m_ID, [1] m_duration, [2] m_durationPerLevel, [3] m_maxDuration
    /// </summary>
    private Dictionary<uint, SpellDurationEntry> LoadSpellDuration(string filePath)
    {
        var dict = new Dictionary<uint, SpellDurationEntry>();
        if (!File.Exists(filePath))
        {
            LoadedCounts["SpellDuration"] = 0;
            return dict;
        }

        var (records, _, recordSize) = ReadDbcFile(filePath);

        for (int i = 0; i < records.Length / recordSize; i++)
        {
            int offset = i * recordSize;
            uint id = BitConverter.ToUInt32(records, offset);
            int duration = BitConverter.ToInt32(records, offset + 4);
            int perLevel = BitConverter.ToInt32(records, offset + 8);
            int maxDuration = BitConverter.ToInt32(records, offset + 12);

            dict[id] = new SpellDurationEntry(duration, perLevel, maxDuration);
        }

        LoadedCounts["SpellDuration"] = dict.Count;
        return dict;
    }

    /// <summary>
    /// SpellCastTimes.dbc — 4 fields, 16 bytes per record. No strings.
    ///   [0] m_ID, [1] m_base, [2] m_perLevel, [3] m_minimum
    /// </summary>
    private Dictionary<uint, SpellCastTimeEntry> LoadSpellCastTimes(string filePath)
    {
        var dict = new Dictionary<uint, SpellCastTimeEntry>();
        if (!File.Exists(filePath))
        {
            LoadedCounts["SpellCastTimes"] = 0;
            return dict;
        }

        var (records, _, recordSize) = ReadDbcFile(filePath);

        for (int i = 0; i < records.Length / recordSize; i++)
        {
            int offset = i * recordSize;
            uint id = BitConverter.ToUInt32(records, offset);
            int baseMs = BitConverter.ToInt32(records, offset + 4);
            int perLevel = BitConverter.ToInt32(records, offset + 8);
            int minimum = BitConverter.ToInt32(records, offset + 12);

            dict[id] = new SpellCastTimeEntry(baseMs, perLevel, minimum);
        }

        LoadedCounts["SpellCastTimes"] = dict.Count;
        return dict;
    }

    /// <summary>
    /// SpellRange.dbc — 22 fields, 88 bytes per record.
    /// Vanilla 1.12.1 layout (with localized strings):
    ///   [0] m_ID
    ///   [1] m_rangeMin
    ///   [2] m_rangeMax
    ///   [3] m_flags
    ///   [4-12] m_displayName_lang (9 fields: 8 locale stringrefs + 1 bitmask)
    ///   [13-21] m_displayNameShort_lang (9 fields: 8 locale stringrefs + 1 bitmask)
    /// We read rangeMin/rangeMax as floats and displayName from field[4] (enUS).
    /// </summary>
    private Dictionary<uint, SpellRangeEntry> LoadSpellRange(string filePath)
    {
        var dict = new Dictionary<uint, SpellRangeEntry>();
        if (!File.Exists(filePath))
        {
            LoadedCounts["SpellRange"] = 0;
            return dict;
        }

        var (records, stringBlock, recordSize) = ReadDbcFile(filePath);

        for (int i = 0; i < records.Length / recordSize; i++)
        {
            int offset = i * recordSize;
            uint id = BitConverter.ToUInt32(records, offset);
            float rangeMin = BitConverter.ToSingle(records, offset + 4);
            float rangeMax = BitConverter.ToSingle(records, offset + 8);
            uint flags = BitConverter.ToUInt32(records, offset + 12);
            uint nameOffset = BitConverter.ToUInt32(records, offset + 16); // field[4], enUS

            string name = ReadString(stringBlock, nameOffset);

            dict[id] = new SpellRangeEntry(rangeMin, rangeMax, flags, name);
        }

        LoadedCounts["SpellRange"] = dict.Count;
        return dict;
    }

    /// <summary>
    /// Spell.dbc — 173 fields, 692 bytes per record (vanilla 1.12.1 build 5875).
    /// Only parses the fields needed for source-spell search and item-effect validation:
    ///   [0]   ID
    ///   [6]   Attributes (PASSIVE bit 0x40, HIDDEN bit 0x80)
    ///   [28]  SpellLevel
    ///   [115] SpellVisualID[0]
    ///   [117] SpellIconID
    ///   [120] Name_lang enUS (stringref)
    ///   [129] Subtext_lang enUS (stringref)
    /// </summary>
    private Dictionary<uint, SpellDbcEntry> LoadSpellEntries(string filePath)
    {
        var dict = new Dictionary<uint, SpellDbcEntry>();
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: File not found: {File}", filePath);
            LoadedCounts["Spell"] = 0;
            return dict;
        }

        var (records, stringBlock, recordSize) = ReadDbcFile(filePath, expectedFieldCount: 173, expectedRecordSize: 692);

        for (int i = 0; i < records.Length / recordSize; i++)
        {
            int o = i * recordSize;
            uint id = BitConverter.ToUInt32(records, o);
            uint attributes = BitConverter.ToUInt32(records, o + 6 * 4);

            bool hidden = (attributes & 0x80) != 0;
            bool passive = (attributes & 0x40) != 0;

            uint spellLevel = BitConverter.ToUInt32(records, o + 28 * 4);
            uint spellVisual1 = BitConverter.ToUInt32(records, o + 115 * 4);
            uint spellIconId = BitConverter.ToUInt32(records, o + 117 * 4);
            uint nameOffset = BitConverter.ToUInt32(records, o + 120 * 4);
            uint subtextOffset = BitConverter.ToUInt32(records, o + 129 * 4);
            uint descOffset = BitConverter.ToUInt32(records, o + 138 * 4);

            string name = ReadString(stringBlock, nameOffset);
            string subtext = ReadString(stringBlock, subtextOffset);
            string description = ReadString(stringBlock, descOffset);

            dict[id] = new SpellDbcEntry(id, name, subtext, 0, spellVisual1, spellIconId,
                spellLevel, description, hidden, passive);
        }

        LoadedCounts["Spell"] = dict.Count;
        _logger.LogInformation("DbcService: Parsed all {Count} Spell.dbc rows (including hidden/unnamed)", dict.Count);
        return dict;
    }

    // ── WDBC file reader ──────────────────────────────────────────────────

    /// <summary>
    /// ItemDisplayInfo.dbc — parse model + texture names + geoset/body-atlas
    /// fields for Session C dressing.
    ///
    /// Vanilla 1.12.1 layout (23 fields, 92 bytes per record). Empirically
    /// verified by dumping all 23 fields across robes, plate chests, cloth,
    /// boots, gloves, and a trade good (Red Dye), plus a histogram of small
    /// int values at every field index over all 29,604 records. The layout
    /// matches mangos-zero r558 and TrinityCore's 3.3.5a doc minus the
    /// post-vanilla second inventory icon stringref:
    ///
    ///   [0]      m_ID                              uint32
    ///   [1-2]    m_modelName[0..1]                 stringref each
    ///   [3-4]    m_modelTexture[0..1]              stringref each
    ///   [5]      m_inventoryIcon                   stringref (single in vanilla)
    ///   [6-8]    m_geosetGroup[0..2]               uint32 each
    ///   [9]      m_flags                           uint32 (sparse; 1/2 on 11 armor rows)
    ///   [10]     m_spellVisualID                   uint32 (bows 5, firearms 224,
    ///                                                       thrown 98, wands 225/226 —
    ///                                                       ranged values confirm SpellVisual)
    ///   [11]     m_groupSoundIndex                 uint32 (~69%, values 7-16
    ///                                                       are armor sound groups)
    ///   [12-13]  m_helmetGeosetVis[0..1]           uint32 each (~4%, helms only)
    ///   [14-21]  m_texture[0..7]                   stringref each
    ///   [22]     m_itemVisual                      uint32 (~1.2%) — Session N:
    ///                                              indexes ItemVisuals.dbc for
    ///                                              lightning/glow/ribbon effects.
    ///
    /// m_texture slot layout (suffix → body part):
    ///   slot 0 (_AU) ArmUpper       shoulders/biceps
    ///   slot 1 (_AL) ArmLower       forearms
    ///   slot 2 (_HA) Hand           hand/wrist
    ///   slot 3 (_TU) TorsoUpper     chest
    ///   slot 4 (_TL) TorsoLower     belly/waist
    ///   slot 5 (_LU) LegUpper       thigh / robe upper
    ///   slot 6 (_LL) LegLower       shin / robe lower (boots paint here)
    ///   slot 7 (_FO) Foot           foot (boots paint here)
    ///
    /// NOTE: an earlier draft of this parser used a -2 shift (texture base
    /// = field 12) which happened to produce visually correct output for
    /// chests because the compositor's SLOT_TO_REGION started at slot 2.
    /// The shift was wrong-for-the-right-reason; corrected to the real
    /// layout so slot indices match TrinityCore's doc and we get access
    /// to LegLower and Foot for boot textures.
    /// </summary>
    private Dictionary<uint, ItemModelDbc> LoadItemModelInfo(string filePath)
    {
        var dict = new Dictionary<uint, ItemModelDbc>();
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: File not found for model info: {File}", filePath);
            LoadedCounts["ItemModelInfo"] = 0;
            return dict;
        }

        var (records, stringBlock, recordSize) = ReadDbcFile(filePath);
        int recordCount = records.Length / recordSize;

        // Sanity check the schema — if recordSize changes (different DBC
        // version) we want to know loudly rather than silently parse garbage.
        if (recordSize != 92)
        {
            _logger.LogWarning(
                "DbcService: ItemDisplayInfo.dbc recordSize={Size} (expected 92 for vanilla 1.12). " +
                "Field offsets in LoadItemModelInfo may be incorrect.", recordSize);
        }

        for (int i = 0; i < recordCount; i++)
        {
            int offset = i * recordSize;

            uint id = BitConverter.ToUInt32(records, offset + 0 * 4);
            uint modelName1Ofs = BitConverter.ToUInt32(records, offset + 1 * 4);
            uint modelName2Ofs = BitConverter.ToUInt32(records, offset + 2 * 4);
            uint texName1Ofs = BitConverter.ToUInt32(records, offset + 3 * 4);
            uint texName2Ofs = BitConverter.ToUInt32(records, offset + 4 * 4);

            // Skip field 5 (m_inventoryIcon) — already parsed by LoadItemDisplayInfo.

            // Fields 6-8: m_geosetGroup[0..2]. Variants are small ints 0-5.
            int geosetGroup0 = BitConverter.ToInt32(records, offset + 6 * 4);
            int geosetGroup1 = BitConverter.ToInt32(records, offset + 7 * 4);
            int geosetGroup2 = BitConverter.ToInt32(records, offset + 8 * 4);

            // Skip fields 9-11: m_flags, m_spellVisualID, m_groupSoundIndex.

            // Fields 12-13: m_helmetGeosetVis[0..1]. Drives hair / facial-hair
            // hiding when a helm is equipped. Encoding is reverse-engineered
            // empirically (see ItemModelDbc.HelmetGeosetVis1/2 docstring).
            // Always populated even for non-helm rows because the DBC has
            // them everywhere — the consumer decides whether they're
            // meaningful based on inventory_type.
            uint helmetGeosetVis1 = BitConverter.ToUInt32(records, offset + 12 * 4);
            uint helmetGeosetVis2 = BitConverter.ToUInt32(records, offset + 13 * 4);

            // Fields 14-21: m_texture[0..7] body-atlas texture partial names.
            var bodyTextures = new string[8];
            for (int t = 0; t < 8; t++)
            {
                uint texOfs = BitConverter.ToUInt32(records, offset + (14 + t) * 4);
                bodyTextures[t] = ReadString(stringBlock, texOfs);
            }

            // Session N: field 22 — m_itemVisual. Indexes ItemVisuals.dbc.
            // Non-zero on ~1.2% of rows (Sulfuras, enchanted weapons, glowing
            // staves, etc). Thunderfury 30606 is zero; its effects are native M2.
            // Empirically verified by the
            // field-21 histogram pass — the original parser already checked
            // this offset for the schema doc above, so no re-probing needed.
            uint itemVisualId = BitConverter.ToUInt32(records, offset + 22 * 4);

            string modelName1 = ReadString(stringBlock, modelName1Ofs);
            string modelName2 = ReadString(stringBlock, modelName2Ofs);
            string texName1 = ReadString(stringBlock, texName1Ofs);
            string texName2 = ReadString(stringBlock, texName2Ofs);

            // Store the entry if it has ANY identifying data — model name,
            // model texture, body atlas texture, OR a non-zero geoset group.
            // Body-atlas-only items (e.g. plate gauntlets with no separate
            // model) have empty modelName but populated bodyTextures +
            // geosetGroup; without this expanded filter they were silently
            // dropped from the dictionary.
            bool hasModelOrTex =
                !string.IsNullOrEmpty(modelName1) || !string.IsNullOrEmpty(modelName2) ||
                !string.IsNullOrEmpty(texName1) || !string.IsNullOrEmpty(texName2);
            bool hasBodyTex = bodyTextures.Any(s => !string.IsNullOrEmpty(s));
            bool hasGeosetGroup = geosetGroup0 != 0 || geosetGroup1 != 0 || geosetGroup2 != 0;

            if (hasModelOrTex || hasBodyTex || hasGeosetGroup)
            {
                dict[id] = new ItemModelDbc
                {
                    ModelName1 = modelName1,
                    ModelName2 = modelName2,
                    TextureName1 = texName1,
                    TextureName2 = texName2,
                    BodyTextures = bodyTextures,
                    GeosetGroup = new[] { geosetGroup0, geosetGroup1, geosetGroup2 },
                    HelmetGeosetVis1 = helmetGeosetVis1,
                    HelmetGeosetVis2 = helmetGeosetVis2,
                    // Session N: itemVisualId for lightning/glow effect lookup.
                    ItemVisualId = itemVisualId,
                };
            }
        }

        LoadedCounts["ItemModelInfo"] = dict.Count;
        _logger.LogInformation(
            "DbcService: Parsed {Count} ItemModelInfo entries (with model/texture/geoset refs)",
            dict.Count);
        return dict;
    }

    /// <summary>
    /// HelmetGeosetVisData.dbc — vanilla 1.12, 6 fields × 24 bytes, ~16 rows.
    /// Field layout (all uint32, no string fields):
    ///   [0] m_ID
    ///   [1] m_hairFlags        — bitmask: bit (1 &lt;&lt; raceId) → hide hair for that race
    ///   [2] m_facialHairFlags0 — bitmask: hide facial hair (beard)
    ///   [3] m_facialHairFlags1 — bitmask: hide facial hair (moustache/sideburns)
    ///   [4] m_facialHairFlags2 — bitmask: hide facial hair (group 3)
    ///   [5] m_earsFlags        — bitmask: hide ears for that race
    ///
    /// Referenced by ItemDisplayInfo.dbc fields [12-13] (m_helmetGeosetVisID).
    /// Each helm item has two vis IDs; the client picks one based on gender.
    /// We check v1 (male) via DoesHelmHideHair for the current race.
    ///
    /// Verified empirically May 19 2026:
    ///   Row 245 hairFlags=0x00000000 → circlets/open helms (no hiding)
    ///   Row 247 hairFlags=0x00000000 → Helm of Might / Defias Mask (open)
    ///   Row 248 hairFlags=0xFFFFFFBF → Helm of Wrath (closed, all races except Tauren)
    ///   Row 368 hairFlags=0xFFFFFFFF → Dreadnaught (closed, all races incl Tauren)
    /// </summary>
    private Dictionary<uint, HelmetGeosetVisEntry> LoadHelmetGeosetVisData(string filePath)
    {
        var dict = new Dictionary<uint, HelmetGeosetVisEntry>();
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: HelmetGeosetVisData.dbc not found at {Path}", filePath);
            LoadedCounts["HelmetGeosetVisData"] = 0;
            return dict;
        }

        var (records, _, recordSize) = ReadDbcFile(filePath);
        int recordCount = records.Length / recordSize;

        if (recordSize != 24)
        {
            _logger.LogWarning(
                "DbcService: HelmetGeosetVisData.dbc recordSize={Size} (expected 24 for vanilla 1.12). " +
                "Field offsets may be incorrect.", recordSize);
        }

        for (int i = 0; i < recordCount; i++)
        {
            int offset = i * recordSize;
            uint id = BitConverter.ToUInt32(records, offset + 0);
            uint hairFlags = BitConverter.ToUInt32(records, offset + 4);
            uint facial0 = BitConverter.ToUInt32(records, offset + 8);
            uint facial1 = BitConverter.ToUInt32(records, offset + 12);
            uint facial2 = BitConverter.ToUInt32(records, offset + 16);
            uint earsFlags = BitConverter.ToUInt32(records, offset + 20);

            dict[id] = new HelmetGeosetVisEntry(id, hairFlags, facial0, facial1, facial2, earsFlags);
        }

        LoadedCounts["HelmetGeosetVisData"] = dict.Count;
        _logger.LogInformation(
            "DbcService: Parsed {Count} HelmetGeosetVisData entries", dict.Count);
        return dict;
    }

    /// <summary>
    /// CharSections.dbc — vanilla 1.12, 10 fields × 40 bytes per record.
    /// Field layout verified against /home/wowvmangos/vmangos/run/data/5875/dbc/CharSections.dbc
    /// (3671 records, 198,838-byte string block)
    ///
    /// All rows are kept — no filtering at parse time. Callers query via
    /// GetDefaultFaceSection (and future helpers for skin tones / hair styles).
    /// </summary>
    private Dictionary<(uint Race, uint Sex, uint Style), int> LoadCharHairGeosets(string filePath)
    {
        var dict = new Dictionary<(uint Race, uint Sex, uint Style), int>();
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: CharHairGeosets.dbc not found at {Path}", filePath);
            LoadedCounts["CharHairGeosets"] = 0;
            return dict;
        }

        var (records, _, recordSize) = ReadDbcFile(filePath);
        int recordCount = records.Length / recordSize;
        if (recordSize < 20)
        {
            _logger.LogWarning("DbcService: CharHairGeosets.dbc recordSize={Size} (expected at least 20)", recordSize);
            return dict;
        }

        for (int i = 0; i < recordCount; i++)
        {
            int offset = i * recordSize;
            uint race = BitConverter.ToUInt32(records, offset + 4);
            uint sex = BitConverter.ToUInt32(records, offset + 8);
            uint style = BitConverter.ToUInt32(records, offset + 12);
            uint geoset = BitConverter.ToUInt32(records, offset + 16);
            bool showsScalp = recordSize >= 24 && BitConverter.ToUInt32(records, offset + 20) != 0;
            dict[(race, sex, style)] = showsScalp ? 1 : (int)geoset;
        }

        LoadedCounts["CharHairGeosets"] = dict.Count;
        _logger.LogInformation("DbcService: Parsed {Count} CharHairGeosets mappings", dict.Count);
        return dict;
    }

    private List<CharSectionDbc> LoadCharSections(string filePath)
    {
        var list = new List<CharSectionDbc>();
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: CharSections.dbc not found at {Path}", filePath);
            LoadedCounts["CharSections"] = 0;
            return list;
        }

        var (records, stringBlock, recordSize) = ReadDbcFile(filePath);
        int recordCount = records.Length / recordSize;

        if (recordSize != 40)
        {
            _logger.LogWarning(
                "DbcService: CharSections.dbc recordSize={Size} (expected 40 for vanilla 1.12). " +
                "Field offsets in LoadCharSections may be incorrect.", recordSize);
        }

        for (int i = 0; i < recordCount; i++)
        {
            int offset = i * recordSize;
            uint id = BitConverter.ToUInt32(records, offset + 0);
            uint race = BitConverter.ToUInt32(records, offset + 4);
            uint sex = BitConverter.ToUInt32(records, offset + 8);
            uint baseSec = BitConverter.ToUInt32(records, offset + 12);
            uint varIdx = BitConverter.ToUInt32(records, offset + 16);
            uint colorIdx = BitConverter.ToUInt32(records, offset + 20);
            uint tex1Ofs = BitConverter.ToUInt32(records, offset + 24);
            uint tex2Ofs = BitConverter.ToUInt32(records, offset + 28);
            uint tex3Ofs = BitConverter.ToUInt32(records, offset + 32);
            uint flags = BitConverter.ToUInt32(records, offset + 36);

            list.Add(new CharSectionDbc(
                id, race, sex, baseSec, varIdx, colorIdx,
                ReadString(stringBlock, tex1Ofs),
                ReadString(stringBlock, tex2Ofs),
                ReadString(stringBlock, tex3Ofs),
                flags));
        }

        LoadedCounts["CharSections"] = list.Count;
        _logger.LogInformation("DbcService: Parsed {Count} CharSections entries", list.Count);

        // Session R: log the default Face row for each (race, gender) so we
        // can verify the schema parsed correctly without a separate dump
        // endpoint. Expect ~16 lines, strings looking like
        // "Character\Human\Male\HumanMaleFaceLower00_00". If strings are
        // empty or garbled, the field offsets are wrong — investigate
        // before trusting CharacterSkinCompositor output.
        var defaultFaces = list
            .Where(r => r.BaseSection == 1 && r.VariationIndex == 0 && r.ColorIndex == 0)
            .OrderBy(r => r.Race).ThenBy(r => r.Sex)
            .Take(16);
        foreach (var f in defaultFaces)
        {
            _logger.LogInformation(
                "  CharSections default face: race={Race} sex={Sex} faceLower='{T1}' faceUpper='{T2}'",
                f.Race, f.Sex, f.TextureName1, f.TextureName2);
        }

        // Session S: same diagnostic for Hair rows (BaseSection=3). Used by
        // CharacterModelService to bind a per-race-gender hair texture to
        // the M2's CHAR_HAIR slot, so hair geosets stop sampling the body
        // atlas (the "face-painted-on-dwarf-hair" bug).
        var defaultHair = list
            .Where(r => r.BaseSection == 3 && r.VariationIndex == 0 && r.ColorIndex == 0)
            .OrderBy(r => r.Race).ThenBy(r => r.Sex)
            .Take(16);
        foreach (var h in defaultHair)
        {
            _logger.LogInformation(
                "  CharSections default hair: race={Race} sex={Sex} hairColor='{T1}' lowerOverlay='{T2}' upperOverlay='{T3}'",
                h.Race, h.Sex, h.TextureName1, h.TextureName2, h.TextureName3);
        }

        return list;
    }

    /// <summary>
    /// WorldMapArea.dbc — vanilla 1.12: 8 fields, 32 bytes per record, ~50 rows.
    /// Field layout:
    ///   [0] m_ID
    ///   [1] m_mapID      — 0=Eastern Kingdoms, 1=Kalimdor (vanilla outdoor world)
    ///   [2] m_areaID     — AreaTable id; 0 for continent overview rows
    ///   [3] m_areaName   (stringref) — internal name, e.g. "DunMorogh",
    ///                      "Ogrimmar" (the typo is in the DBC itself)
    ///   [4] m_locLeft    (float) — west  border, Y max
    ///   [5] m_locRight   (float) — east  border, Y min
    ///   [6] m_locTop     (float) — north border, X max
    ///   [7] m_locBottom  (float) — south border, X min
    /// Note the rotated WoW axes: left/right are Y borders, top/bottom are X.
    /// Missing file is non-fatal — zone filtering just reports unavailable.
    /// </summary>
    private IReadOnlyList<WorldMapZoneDbc> LoadWorldMapArea(string filePath)
    {
        var list = new List<WorldMapZoneDbc>();

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: WorldMapArea.dbc not found at {Path} — zone filtering disabled", filePath);
            LoadedCounts["WorldMapArea"] = 0;
            return list;
        }

        var (records, stringBlock, recordSize) = ReadDbcFile(filePath);
        int recordCount = records.Length / recordSize;

        for (int i = 0; i < recordCount; i++)
        {
            int o = i * recordSize;
            uint id = BitConverter.ToUInt32(records, o);
            uint mapId = BitConverter.ToUInt32(records, o + 4);
            uint areaId = BitConverter.ToUInt32(records, o + 8);
            uint nameOfs = BitConverter.ToUInt32(records, o + 12);
            float locLeft = BitConverter.ToSingle(records, o + 16);
            float locRight = BitConverter.ToSingle(records, o + 20);
            float locTop = BitConverter.ToSingle(records, o + 24);
            float locBottom = BitConverter.ToSingle(records, o + 28);

            string internalName = ReadString(stringBlock, nameOfs);
            if (internalName.Length == 0) continue;

            list.Add(new WorldMapZoneDbc(
                id, mapId, areaId, internalName, PrettyZoneName(internalName),
                locLeft, locRight, locTop, locBottom));
        }

        LoadedCounts["WorldMapArea"] = list.Count;
        _logger.LogInformation("DbcService: Parsed {Count} WorldMapArea entries", list.Count);
        return list;
    }

    private static readonly Dictionary<string, string> ZoneNameOverrides = new()
    {
        ["Ogrimmar"] = "Orgrimmar",       // misspelled in the DBC itself
        ["UnGoro"] = "Un'Goro Crater",
    };

    /// <summary>"DunMorogh" → "Dun Morogh"; known-awkward names overridden.</summary>
    private static string PrettyZoneName(string internalName)
    {
        if (ZoneNameOverrides.TryGetValue(internalName, out var ov))
            return ov;
        return System.Text.RegularExpressions.Regex.Replace(internalName, "(?<=[a-z])(?=[A-Z])", " ");
    }

    /// <summary>Load the primary ID (field 0) from a Vanilla DBC without guessing any
    /// table-specific payload layout. Every client DBC row begins with this uint32 key.</summary>
    private IReadOnlySet<uint> LoadFactionRequirementIds(string filePath)
    {
        const string label = "Faction";
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: Faction.dbc not found at {Path}", filePath);
            LoadedCounts[label] = 0;
            return new HashSet<uint>();
        }

        var (records, _, recordSize) = ReadDbcFile(filePath);
        if (recordSize < 2 * sizeof(uint))
            throw new InvalidDataException($"Faction.dbc record size {recordSize} cannot contain ID and reputationListID.");

        var ids = new HashSet<uint>();
        for (int offset = 0; offset < records.Length; offset += recordSize)
        {
            uint id = BitConverter.ToUInt32(records, offset);
            int reputationListId = BitConverter.ToInt32(records, offset + sizeof(uint));
            if (id != 0 && reputationListId >= 0) ids.Add(id);
        }

        LoadedCounts[label] = ids.Count;
        _logger.LogInformation("DbcService: Parsed {Count} reputation-bearing Faction.dbc IDs", ids.Count);
        return ids;
    }

    /// <summary>
    /// Faction.dbc (vanilla 1.12.1, 37 fields / 148 bytes): [0] ID, [1] ReputationIndex
    /// (-1 = no reputation), [18] ParentFactionID, [19] Name_lang enUS. Rows whose name
    /// starts with a space are the hidden profession-spec "factions" and are skipped.
    /// </summary>
    private IReadOnlyList<ReputationFactionDbc> LoadReputationFactions(string filePath)
    {
        if (!File.Exists(filePath))
            return Array.Empty<ReputationFactionDbc>();

        var (records, stringBlock, recordSize) = ReadDbcFile(filePath);
        if (recordSize != 37 * sizeof(uint))
        {
            _logger.LogWarning("DbcService: Faction.dbc record size {Size} is not the vanilla 148; faction names skipped", recordSize);
            return Array.Empty<ReputationFactionDbc>();
        }

        var list = new List<ReputationFactionDbc>();
        for (int offset = 0; offset < records.Length; offset += recordSize)
        {
            uint id = BitConverter.ToUInt32(records, offset);
            int reputationIndex = BitConverter.ToInt32(records, offset + 4);
            if (id == 0 || reputationIndex < 0) continue;
            uint parentId = BitConverter.ToUInt32(records, offset + 18 * 4);
            string name = ReadString(stringBlock, BitConverter.ToUInt32(records, offset + 19 * 4));
            if (name.Length == 0 || char.IsWhiteSpace(name[0])) continue;
            list.Add(new ReputationFactionDbc(id, reputationIndex, parentId, name));
        }
        return list;
    }

    private IReadOnlySet<uint> LoadEntryIds(string filePath, string label)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("DbcService: {Label}.dbc not found at {Path}", label, filePath);
            LoadedCounts[label] = 0;
            return new HashSet<uint>();
        }

        var (records, _, recordSize) = ReadDbcFile(filePath);
        if (recordSize < sizeof(uint))
            throw new InvalidDataException($"{label}.dbc record size {recordSize} cannot contain an ID.");

        var ids = new HashSet<uint>();
        for (int offset = 0; offset < records.Length; offset += recordSize)
        {
            uint id = BitConverter.ToUInt32(records, offset);
            if (id != 0) ids.Add(id);
        }

        LoadedCounts[label] = ids.Count;
        _logger.LogInformation("DbcService: Parsed {Count} {Label}.dbc IDs", ids.Count, label);
        return ids;
    }

    // ── WDBC file reader ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a WDBC file and returns the raw record bytes, string block, and record size.
    /// Header: 4 bytes magic ("WDBC"), 4 bytes recordCount, 4 bytes fieldCount,
    ///         4 bytes recordSize, 4 bytes stringBlockSize.
    /// </summary>
    private (byte[] records, byte[] stringBlock, int recordSize) ReadDbcFile(
        string filePath, int? expectedFieldCount = null, int? expectedRecordSize = null)
    {
        using var fs = File.OpenRead(filePath);
        using var br = new BinaryReader(fs);

        // Read header
        uint magic = br.ReadUInt32();
        if (magic != 0x43424457) // "WDBC" in little-endian
            throw new InvalidDataException($"Invalid DBC magic in {filePath}: 0x{magic:X8}");

        uint recordCount = br.ReadUInt32();
        uint fieldCount = br.ReadUInt32();
        uint recordSize = br.ReadUInt32();
        uint stringBlockSize = br.ReadUInt32();

        if (recordSize == 0)
            throw new InvalidDataException($"DBC record size is zero in {filePath}.");
        if (expectedFieldCount is int requiredFields && fieldCount != (uint)requiredFields)
            throw new InvalidDataException(
                $"Unexpected DBC field count in {filePath}: {fieldCount} (expected {requiredFields}).");
        if (expectedRecordSize is int requiredSize && recordSize != (uint)requiredSize)
            throw new InvalidDataException(
                $"Unexpected DBC record size in {filePath}: {recordSize} (expected {requiredSize}).");

        long recordByteCount;
        try { recordByteCount = checked((long)recordCount * recordSize); }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"DBC record section is too large in {filePath}.", ex);
        }
        if (recordByteCount > int.MaxValue || stringBlockSize > int.MaxValue)
            throw new InvalidDataException($"DBC sections exceed supported in-memory size in {filePath}.");

        // Read all records as a flat byte array
        byte[] records = br.ReadBytes((int)recordByteCount);
        if (records.Length != (int)recordByteCount)
            throw new EndOfStreamException($"DBC record section is truncated in {filePath}.");

        // Read string block
        byte[] stringBlock = br.ReadBytes((int)stringBlockSize);
        if (stringBlock.Length != (int)stringBlockSize)
            throw new EndOfStreamException($"DBC string block is truncated in {filePath}.");

        return (records, stringBlock, (int)recordSize);
    }

    /// <summary>
    /// Reads a null-terminated string from the string block at the given byte offset.
    /// </summary>
    private static string ReadString(byte[] stringBlock, uint offset)
    {
        if (offset == 0 || offset >= stringBlock.Length)
            return string.Empty;

        int end = (int)offset;
        while (end < stringBlock.Length && stringBlock[end] != 0)
            end++;

        return Encoding.UTF8.GetString(stringBlock, (int)offset, end - (int)offset);
    }
}

// ── DBC entry records ─────────────────────────────────────────────────────

public record SpellDurationEntry(int DurationMs, int DurationPerLevel, int MaxDurationMs)
{
    /// <summary>Human-friendly label for the UI dropdown.</summary>
    public string DisplayLabel => DurationMs switch
    {
        -1 => "Infinite",
        0 => "Instant",
        _ when DurationMs >= 3600000 => $"{DurationMs / 3600000}h",
        _ when DurationMs >= 60000 => $"{DurationMs / 60000}m",
        _ when DurationMs >= 1000 => $"{DurationMs / 1000}s",
        _ => $"{DurationMs}ms"
    };
}

public record SpellCastTimeEntry(int BaseMs, int PerLevelMs, int MinimumMs)
{
    public string DisplayLabel => BaseMs switch
    {
        0 => "Instant",
        _ when BaseMs >= 1000 => $"{BaseMs / 1000.0:0.#} sec",
        _ => $"{BaseMs}ms"
    };
}

public record SpellRangeEntry(float RangeMin, float RangeMax, uint Flags, string DisplayName)
{
    public string DisplayLabel => !string.IsNullOrEmpty(DisplayName)
        ? $"{DisplayName} ({RangeMax:0} yd)"
        : $"{RangeMax:0} yd";
}

/// <summary>
/// Lightweight spell entry parsed from Spell.dbc — used for source spell search
/// when spell_template (SQL) doesn't have a row for a given vanilla spell.
/// Only the fields needed by PatchController.SearchSource are parsed.
/// </summary>
public record SpellDbcEntry(
    uint Entry,
    string Name,
    string NameSubtext,
    uint School,
    uint SpellVisual1,
    uint SpellIconId,
    uint SpellLevel,
    string Description = "",
    bool Hidden = false,
    bool Passive = false
);

/// <summary>
/// One row of vanilla CharSections.dbc (10 fields, 40 bytes per record,
/// confirmed empirically against /home/wowvmangos/vmangos/run/data/5875/dbc/CharSections.dbc).
///
/// Field layout:
///   [0] ID
///   [1] Race          — CharRaces.dbc id (1=Human, 2=Orc, 3=Dwarf,
///                       4=NightElf, 5=Scourge, 6=Tauren, 7=Gnome, 8=Troll)
///   [2] Sex           — 0=Male, 1=Female
///   [3] BaseSection   — CharacterSectionType enum: 0=Skin, 1=Face,
///                       2=FacialHair, 3=Hair, 4=Underwear
///   [4] VariationIndex — Face-shape / hair-style choice. Default = 0.
///   [5] ColorIndex    — Skin tone / hair color choice. Default = 0.
///   [6-8] TextureName[0..2] — three stringrefs. For BaseSection=Face:
///                              [0] = face_lower BLP partial path
///                              [1] = face_upper BLP partial path
///                              [2] = empty in vanilla
///   [9] Flags
/// </summary>
public record CharSectionDbc(
    uint Id,
    uint Race,
    uint Sex,
    uint BaseSection,
    uint VariationIndex,
    uint ColorIndex,
    string TextureName1,
    string TextureName2,
    string TextureName3,
    uint Flags);

public record CharacterHairAppearance(
    uint Style,
    int GeosetId,
    CharSectionDbc Section);

/// <summary>
/// One row of HelmetGeosetVisData.dbc (6 fields, 24 bytes per record).
/// Each field (except ID) is a bitmask over ChrRaces: bit (1 &lt;&lt; raceId)
/// indicates the geoset should be hidden for that race.
///
/// Vanilla race IDs: 1=Human, 2=Orc, 3=Dwarf, 4=NightElf, 5=Undead,
/// 6=Tauren, 7=Gnome, 8=Troll.
/// </summary>
public record HelmetGeosetVisEntry(
    uint Id,
    uint HairFlags,
    uint FacialHairFlags0,
    uint FacialHairFlags1,
    uint FacialHairFlags2,
    uint EarsFlags);

/// <summary>
/// One row of vanilla WorldMapArea.dbc (8 fields, 32 bytes per record).
/// LocLeft/LocRight are the Y borders (west/east), LocTop/LocBottom the
/// X borders (north/south) — WoW's map axes are rotated relative to
/// world coordinates. AreaId 0 marks continent overview rows.
/// </summary>
public record ReputationFactionDbc(uint Id, int ReputationIndex, uint ParentId, string Name);

public record WorldMapZoneDbc(
    uint Id,
    uint MapId,
    uint AreaId,
    string InternalName,
    string DisplayName,
    float LocLeft,
    float LocRight,
    float LocTop,
    float LocBottom);
