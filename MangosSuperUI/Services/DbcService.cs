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

    /// <summary>Re-read all DBC files (e.g., after path change in Settings).</summary>
    public void Reload()
    {
        _iconToDisplayIds = null; // Invalidate reverse cache
        Load();
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
            SpellIcons = LoadSpellIcon(Path.Combine(DbcPath, "SpellIcon.dbc"));
            SpellDurations = LoadSpellDuration(Path.Combine(DbcPath, "SpellDuration.dbc"));
            SpellCastTimes = LoadSpellCastTimes(Path.Combine(DbcPath, "SpellCastTimes.dbc"));
            SpellRanges = LoadSpellRange(Path.Combine(DbcPath, "SpellRange.dbc"));
            SpellEntries = LoadSpellEntries(Path.Combine(DbcPath, "Spell.dbc"));

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