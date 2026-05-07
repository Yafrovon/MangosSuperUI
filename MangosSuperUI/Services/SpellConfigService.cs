using Dapper;
using System.Text.Json;
using MangosSuperUI.Models;
using MangosSuperUI.Controllers;

namespace MangosSuperUI.Services;

/// <summary>
/// Persists per-spell visual configuration (color preset, per-phase particle params)
/// in a `custom_spell_meta` table in the mangos database.
///
/// This enables the unified patch system: when rebuilding patch-3.MPQ, the builder
/// can reproduce every custom spell's visual modifications from stored config.
///
/// Table: custom_spell_meta (auto-created on first use)
///   entry       INT PRIMARY KEY  — matches spell_template.entry (60000-65000)
///   source_entry INT             — the vanilla spell this was cloned from
///   spell_name   VARCHAR(255)    — display name
///   color_preset VARCHAR(32)     — "shadow", "frost", etc.
///   phase_params TEXT            — JSON blob of per-phase knobs (PerPhaseParams)
///   icon_source  VARCHAR(32)     — "comfyui-flux", "existing", etc.
///   icon_path    VARCHAR(512)    — path to the generated PNG (for rebuild)
///   created_at   DATETIME        — when the spell was first created
///   updated_at   DATETIME        — last config change
/// </summary>
public class SpellConfigService
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<SpellConfigService> _logger;
    private bool _tableChecked = false;

    public SpellConfigService(ConnectionFactory db, ILogger<SpellConfigService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Ensure the custom_spell_meta table exists.</summary>
    private async Task EnsureTableAsync()
    {
        if (_tableChecked) return;

        using var conn = _db.Mangos();
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS custom_spell_meta (
                entry         INT NOT NULL PRIMARY KEY,
                source_entry  INT NOT NULL DEFAULT 0,
                spell_name    VARCHAR(255) NOT NULL DEFAULT '',
                name_subtext  VARCHAR(255) DEFAULT NULL,
                description   TEXT DEFAULT NULL,
                tooltip       TEXT DEFAULT NULL,
                color_preset  VARCHAR(32) DEFAULT NULL,
                phase_params  TEXT DEFAULT NULL,
                icon_source   VARCHAR(32) DEFAULT NULL,
                icon_path     VARCHAR(512) DEFAULT NULL,
                created_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ");

        // Add columns if upgrading from earlier schema
        try
        {
            await conn.ExecuteAsync("ALTER TABLE custom_spell_meta ADD COLUMN IF NOT EXISTS name_subtext VARCHAR(255) DEFAULT NULL AFTER spell_name");
            await conn.ExecuteAsync("ALTER TABLE custom_spell_meta ADD COLUMN IF NOT EXISTS description TEXT DEFAULT NULL AFTER name_subtext");
            await conn.ExecuteAsync("ALTER TABLE custom_spell_meta ADD COLUMN IF NOT EXISTS tooltip TEXT DEFAULT NULL AFTER description");
        }
        catch { /* columns already exist */ }

        _tableChecked = true;
        _logger.LogInformation("SpellConfig: custom_spell_meta table ensured");
    }

    /// <summary>Save or update a spell's visual config.</summary>
    public async Task SaveConfigAsync(SpellVisualConfig config)
    {
        await EnsureTableAsync();
        using var conn = _db.Mangos();

        string? phaseJson = config.PhaseParams != null
            ? JsonSerializer.Serialize(config.PhaseParams, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            : null;

        await conn.ExecuteAsync(@"
            INSERT INTO custom_spell_meta (entry, source_entry, spell_name, name_subtext, description, tooltip, color_preset, phase_params, icon_source, icon_path)
            VALUES (@Entry, @SourceEntry, @SpellName, @NameSubtext, @Description, @Tooltip, @ColorPreset, @PhaseParams, @IconSource, @IconPath)
            ON DUPLICATE KEY UPDATE
                source_entry = @SourceEntry,
                spell_name = @SpellName,
                name_subtext = @NameSubtext,
                description = @Description,
                tooltip = @Tooltip,
                color_preset = @ColorPreset,
                phase_params = @PhaseParams,
                icon_source = @IconSource,
                icon_path = @IconPath",
            new
            {
                config.Entry,
                config.SourceEntry,
                config.SpellName,
                config.NameSubtext,
                config.Description,
                config.Tooltip,
                config.ColorPreset,
                PhaseParams = phaseJson,
                config.IconSource,
                config.IconPath
            });

        _logger.LogInformation("SpellConfig: Saved config for #{Entry} ({Name})", config.Entry, config.SpellName);
    }

    /// <summary>Get a spell's visual config. Returns null if not found.</summary>
    public async Task<SpellVisualConfig?> GetConfigAsync(int entry)
    {
        await EnsureTableAsync();
        using var conn = _db.Mangos();

        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM custom_spell_meta WHERE entry = @Entry", new { Entry = entry });

        if (row == null) return null;

        return MapRowToConfig(row);
    }

    /// <summary>Get ALL custom spell configs (for unified patch rebuild).</summary>
    public async Task<List<SpellVisualConfig>> GetAllConfigsAsync()
    {
        await EnsureTableAsync();
        using var conn = _db.Mangos();

        var rows = await conn.QueryAsync<dynamic>(
            "SELECT * FROM custom_spell_meta ORDER BY entry");

        return rows.Select(MapRowToConfig).ToList();
    }

    /// <summary>Delete a spell's visual config.</summary>
    public async Task DeleteConfigAsync(int entry)
    {
        await EnsureTableAsync();
        using var conn = _db.Mangos();
        await conn.ExecuteAsync("DELETE FROM custom_spell_meta WHERE entry = @Entry", new { Entry = entry });
        _logger.LogInformation("SpellConfig: Deleted config for #{Entry}", entry);
    }

    private SpellVisualConfig MapRowToConfig(dynamic row)
    {
        var config = new SpellVisualConfig
        {
            Entry = (int)row.entry,
            SourceEntry = (int)row.source_entry,
            SpellName = row.spell_name?.ToString() ?? "",
            NameSubtext = row.name_subtext?.ToString(),
            Description = row.description?.ToString(),
            Tooltip = row.tooltip?.ToString(),
            ColorPreset = row.color_preset?.ToString(),
            IconSource = row.icon_source?.ToString(),
            IconPath = row.icon_path?.ToString()
        };

        string? phaseJson = row.phase_params?.ToString();
        if (!string.IsNullOrEmpty(phaseJson))
        {
            try
            {
                config.PhaseParams = JsonSerializer.Deserialize<PerPhaseParams>(phaseJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SpellConfig: Failed to deserialize phase_params for #{Entry}", config.Entry);
            }
        }

        return config;
    }
}

/// <summary>Visual configuration for a custom spell, persisted in custom_spell_meta.</summary>
public class SpellVisualConfig
{
    public int Entry { get; set; }
    public int SourceEntry { get; set; }
    public string SpellName { get; set; } = "";
    public string? NameSubtext { get; set; }
    public string? Description { get; set; }
    public string? Tooltip { get; set; }
    public string? ColorPreset { get; set; }
    public PerPhaseParams? PhaseParams { get; set; }
    public string? IconSource { get; set; }
    public string? IconPath { get; set; }
}