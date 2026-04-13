using System.Text.Json;
using MangosSuperUI.BotLogic.Core;

namespace MangosSuperUI.BotLogic.Data;

/// <summary>
/// Loads quirk definitions from quirk_tables.json at startup.
/// Provides the available quirk pool for PersonalityRoller.
/// </summary>
public class QuirkLoader
{
    private readonly ILogger<QuirkLoader> _logger;
    private readonly IWebHostEnvironment _env;
    private List<BotQuirk> _quirks = new();

    public QuirkLoader(ILogger<QuirkLoader> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public IReadOnlyList<BotQuirk> AllQuirks => _quirks;

    public void Load()
    {
        try
        {
            // Try BotLogic/Tables first, then wwwroot fallback
            var path = Path.Combine(_env.ContentRootPath, "BotLogic", "Tables", "quirk_tables.json");
            if (!File.Exists(path))
            {
                _logger.LogWarning("QuirkLoader: quirk_tables.json not found at {Path}", path);
                LoadDefaults();
                return;
            }

            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            var quirksArray = doc.RootElement.GetProperty("quirks");

            _quirks = new List<BotQuirk>();
            foreach (var qEl in quirksArray.EnumerateArray())
            {
                var quirk = new BotQuirk
                {
                    Id = qEl.GetProperty("id").GetString() ?? "",
                    Name = qEl.GetProperty("name").GetString() ?? "",
                    Description = qEl.GetProperty("description").GetString() ?? "",
                    Modifiers = new Dictionary<string, JsonElement>()
                };

                if (qEl.TryGetProperty("modifiers", out var mods))
                {
                    foreach (var prop in mods.EnumerateObject())
                    {
                        quirk.Modifiers[prop.Name] = prop.Value.Clone();
                    }
                }

                _quirks.Add(quirk);
            }

            _logger.LogInformation("QuirkLoader: loaded {Count} quirks", _quirks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuirkLoader: failed to load quirk_tables.json");
            LoadDefaults();
        }
    }

    /// <summary>
    /// Get a quirk by ID.
    /// </summary>
    public BotQuirk? GetQuirk(string id)
    {
        return _quirks.FirstOrDefault(q => q.Id == id);
    }

    /// <summary>
    /// Resolve comma-separated quirk IDs (from DB persistence) back to quirk objects.
    /// </summary>
    public List<BotQuirk> ResolveQuirkIds(string? quirkIdsCsv)
    {
        if (string.IsNullOrWhiteSpace(quirkIdsCsv)) return new List<BotQuirk>();

        return quirkIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => GetQuirk(id))
            .Where(q => q != null)
            .Select(q => q!)
            .ToList();
    }

    private void LoadDefaults()
    {
        _quirks = new List<BotQuirk>
        {
            new() { Id = "ah_addict", Name = "AH Addict", Description = "Visits AH twice as often" },
            new() { Id = "chatty_kathy", Name = "Chatty Kathy", Description = "Chats with nearby players" },
            new() { Id = "completionist", Name = "Completionist", Description = "Never abandons quests" },
            new() { Id = "grinder", Name = "Born Grinder", Description = "Prefers grinding to questing" },
            new() { Id = "afker", Name = "AFK Andy", Description = "Occasionally goes AFK" },
            new() { Id = "speed_demon", Name = "Speed Demon", Description = "Minimal loitering" },
            new() { Id = "explorer", Name = "Wanderlust", Description = "Random zone wandering" },
            new() { Id = "leeroy", Name = "Leeroy Jenkins", Description = "Engages everything" },
        };
        _logger.LogWarning("QuirkLoader: using {Count} default quirks", _quirks.Count);
    }
}
