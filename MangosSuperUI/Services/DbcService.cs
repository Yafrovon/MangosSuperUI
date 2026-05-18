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
public class DbcService
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

    /// <summary>spellId → SpellDbcEntry (lightweight, for source spell search fallback).
    /// Parsed from Spell.dbc — covers ALL ~22k vanilla spells, unlike spell_template which
    /// only has server-side overrides.</summary>
    public IReadOnlyDictionary<uint, SpellDbcEntry> SpellEntries { get; private set; }
        = new Dictionary<uint, SpellDbcEntry>();

    /// <summary>displayId → model info (model names + texture names from DBC fields [1-4]).
    /// Used by ItemTextureService to resolve displayId → M2 file path.</summary>
    public IReadOnlyDictionary<uint, ItemModelDbc> ItemModelInfos { get; private set; }
        = new Dictionary<uint, ItemModelDbc>();

    /// <summary>All rows of CharSections.dbc (vanilla 1.12 character-creation
    /// texture table). Queried via GetDefaultFaceSection — and future helpers
    /// for skin tones / hair styles. Used by CharacterSkinCompositor to layer
    /// face textures onto the body atlas.</summary>
    public IReadOnlyList<CharSectionDbc> CharacterSections { get; private set; }
        = Array.Empty<CharSectionDbc>();

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
            return $"/icons/{name}.png";
        return "/icons/inv_misc_questionmark.png";
    }

    /// <summary>Resolve a spell's SpellIconID to an icon web path, or fallback.</summary>
    public string GetSpellIconPath(uint spellIconId)
    {
        if (SpellIcons.TryGetValue(spellIconId, out var name))
            return $"/icons/{name}.png";
        return "/icons/inv_misc_questionmark.png";
    }

    /// <summary>Resolve a displayId to its model info (model names + texture names).</summary>
    public ItemModelDbc? GetItemModelInfo(uint displayId)
    {
        if (ItemModelInfos.TryGetValue(displayId, out var info))
            return info;
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
    /// Register a custom ItemDisplayInfo entry at runtime.
    /// Clones the icon and model info from an existing displayId so the new
    /// displayId is fully functional in SuperUI (icon lookup, texture extraction,
    /// GLB generation) without requiring a restart or DBC reload.
    /// Called by ItemRetextureService after creating a retexture.
    /// </summary>
    public void RegisterCustomDisplayEntry(uint newDisplayId, uint sourceDisplayId,
        string? customModelName = null, string? customTextureName = null)
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
                    ModelName2 = sourceModel.ModelName2,
                    TextureName1 = customTextureName ?? sourceModel.TextureName1,
                    TextureName2 = sourceModel.TextureName2,
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
                    // retextured Thunderfury still produces lightning.
                    ItemVisualId = sourceModel.ItemVisualId,
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
            SpellIcons = LoadSpellIcon(Path.Combine(DbcPath, "SpellIcon.dbc"));
            SpellDurations = LoadSpellDuration(Path.Combine(DbcPath, "SpellDuration.dbc"));
            SpellCastTimes = LoadSpellCastTimes(Path.Combine(DbcPath, "SpellCastTimes.dbc"));
            SpellRanges = LoadSpellRange(Path.Combine(DbcPath, "SpellRange.dbc"));
            SpellEntries = LoadSpellEntries(Path.Combine(DbcPath, "Spell.dbc"));
            CharacterSections = LoadCharSections(Path.Combine(DbcPath, "CharSections.dbc"));

            IsLoaded = true;
            _logger.LogInformation("DbcService: Loaded successfully — {Counts}",
                string.Join(", ", LoadedCounts.Select(kv => $"{kv.Key}: {kv.Value}")));
        }
        catch (Exception ex)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex, "DbcService: Failed to load DBC files");
        }
    }

    // ── Individual DBC parsers ────────────────────────────────────────────

    /// <summary>
    /// ItemDisplayInfo.dbc — 23 fields, 92 bytes per record.
    /// Field layout (all uint32):
    ///   [0] m_ID
    ///   [1-2] m_modelName[2]         (stringref)
    ///   [3-4] m_modelTexture[2]      (stringref)
    ///   [5] m_inventoryIcon           (stringref) ← THIS IS WHAT WE WANT
    ///   [6] m_groundModel             (stringref)
    ///   [7-9] m_geosetGroup[3]
    ///   [10] m_spellVisualID
    ///   [11] m_groupSoundIndex
    ///   [12-13] m_helmetGeosetVisID[2]
    ///   [14-21] m_texture[8]         (stringref)
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
    /// Only parses the fields needed for source-spell search:
    ///   [0]   ID
    ///   [6]   Attributes (check HIDDEN bit 0x80)
    ///   [25]  SpellLevel
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

        var (records, stringBlock, recordSize) = ReadDbcFile(filePath);

        for (int i = 0; i < records.Length / recordSize; i++)
        {
            int o = i * recordSize;
            uint id = BitConverter.ToUInt32(records, o);
            uint attributes = BitConverter.ToUInt32(records, o + 6 * 4);

            // Skip hidden spells (attribute bit 0x80)
            if ((attributes & 0x80) != 0) continue;

            uint spellLevel = BitConverter.ToUInt32(records, o + 25 * 4);
            uint spellVisual1 = BitConverter.ToUInt32(records, o + 115 * 4);
            uint spellIconId = BitConverter.ToUInt32(records, o + 117 * 4);
            uint nameOffset = BitConverter.ToUInt32(records, o + 120 * 4);
            uint subtextOffset = BitConverter.ToUInt32(records, o + 129 * 4);
            uint descOffset = BitConverter.ToUInt32(records, o + 138 * 4);

            string name = ReadString(stringBlock, nameOffset);
            if (string.IsNullOrEmpty(name)) continue;

            string subtext = ReadString(stringBlock, subtextOffset);
            string description = ReadString(stringBlock, descOffset);

            dict[id] = new SpellDbcEntry(id, name, subtext, 0, spellVisual1, spellIconId, spellLevel, description);
        }

        LoadedCounts["Spell"] = dict.Count;
        _logger.LogInformation("DbcService: Parsed {Count} Spell.dbc entries (non-hidden)", dict.Count);
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
    ///   [9]      m_spellVisualID                   uint32 (sparse, ~0.04%)
    ///   [10]     m_groundModel?                    uint32 (sparse, ~2%)
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

            // Skip fields 9-11: m_spellVisualID, m_groundModel(?), m_groupSoundIndex.

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
            // Non-zero on ~1.2% of rows (Thunderfury, Sulfuras, enchanted
            // weapons, glowing staves, etc). Empirically verified by the
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
    /// CharSections.dbc — vanilla 1.12, 10 fields × 40 bytes per record.
    /// Field layout verified against /home/wowvmangos/vmangos/run/data/5875/dbc/CharSections.dbc
    /// (3671 records, 198,838-byte string block)
    ///
    /// All rows are kept — no filtering at parse time. Callers query via
    /// GetDefaultFaceSection (and future helpers for skin tones / hair styles).
    /// </summary>
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

    // ── WDBC file reader (original, unchanged) ──────────────────────────────────────────────────

    /// <summary>
    /// Reads a WDBC file and returns the raw record bytes, string block, and record size.
    /// Header: 4 bytes magic ("WDBC"), 4 bytes recordCount, 4 bytes fieldCount,
    ///         4 bytes recordSize, 4 bytes stringBlockSize.
    /// </summary>
    private (byte[] records, byte[] stringBlock, int recordSize) ReadDbcFile(string filePath)
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

        // Read all records as a flat byte array
        byte[] records = br.ReadBytes((int)(recordCount * recordSize));

        // Read string block
        byte[] stringBlock = br.ReadBytes((int)stringBlockSize);

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
    string Description = ""
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