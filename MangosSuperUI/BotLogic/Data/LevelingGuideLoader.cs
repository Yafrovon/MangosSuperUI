using System.Text.Json;

namespace MangosSuperUI.BotLogic.Data;

/// <summary>
/// Reads JSON leveling guides from BotLogic/Tables/ and provides next-task lookups.
/// Guides are structured as ordered arrays of tasks per faction/level-range.
///
/// This is a scaffold — full guide JSON content will be added as guides are authored.
/// The system works without guides (bots default to grinding behavior when no guide task is found).
/// </summary>
public class LevelingGuideLoader
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LevelingGuideLoader> _logger;

    // Faction → ordered list of guide steps
    private readonly Dictionary<string, List<GuideStep>> _guides = new();

    public LevelingGuideLoader(IWebHostEnvironment env, ILogger<LevelingGuideLoader> logger)
    {
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Load all leveling guides from BotLogic/Tables/guides/ directory.
    /// Each JSON file is a faction guide (horde.json, alliance.json).
    /// </summary>
    public void Load()
    {
        var guidesDir = Path.Combine(_env.ContentRootPath, "BotLogic", "Tables", "guides");
        if (!Directory.Exists(guidesDir))
        {
            _logger.LogInformation("LevelingGuideLoader: no guides directory found at {Path} — bots will use fallback behavior", guidesDir);
            return;
        }

        foreach (var file in Directory.GetFiles(guidesDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var guide = JsonSerializer.Deserialize<LevelingGuide>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (guide != null && !string.IsNullOrEmpty(guide.Faction))
                {
                    _guides[guide.Faction.ToLower()] = guide.Steps;
                    _logger.LogInformation("LevelingGuideLoader: loaded {Count} steps for {Faction} from {File}",
                        guide.Steps.Count, guide.Faction, Path.GetFileName(file));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LevelingGuideLoader: failed to parse {File}", file);
            }
        }
    }

    /// <summary>
    /// Get the next guide step for a bot given their faction, level, and completed quests.
    /// Returns null if no guide is loaded or all guide steps are complete.
    /// </summary>
    public GuideStep? GetNextStep(string faction, int level, HashSet<int> completedQuestIds)
    {
        if (!_guides.TryGetValue(faction.ToLower(), out var steps))
            return null;

        // Find first step that:
        // 1. Is at or below the bot's level range
        // 2. Hasn't been completed yet (if it's a quest step)
        return steps.FirstOrDefault(s =>
            s.MinLevel <= level &&
            s.MaxLevel >= level &&
            (s.QuestId == 0 || !completedQuestIds.Contains(s.QuestId)));
    }

    /// <summary>
    /// How many total steps are in the faction's guide?
    /// </summary>
    public int GetGuideStepCount(string faction)
    {
        return _guides.TryGetValue(faction.ToLower(), out var steps) ? steps.Count : 0;
    }

    /// <summary>
    /// Is a guide loaded for this faction?
    /// </summary>
    public bool HasGuide(string faction)
    {
        return _guides.ContainsKey(faction.ToLower());
    }
}

// ==================== Guide Models ====================

public class LevelingGuide
{
    public string Faction { get; set; } = "";
    public string Description { get; set; } = "";
    public List<GuideStep> Steps { get; set; } = new();
}

public class GuideStep
{
    /// <summary>Unique step ID for tracking completion.</summary>
    public int StepId { get; set; }

    /// <summary>Min bot level for this step to be relevant.</summary>
    public int MinLevel { get; set; }

    /// <summary>Max bot level for this step (skip if overlevel).</summary>
    public int MaxLevel { get; set; }

    /// <summary>"quest", "grind", "travel", "train"</summary>
    public string Type { get; set; } = "quest";

    /// <summary>Quest ID (0 if not a quest step).</summary>
    public int QuestId { get; set; }

    /// <summary>Zone where this step takes place.</summary>
    public int ZoneId { get; set; }

    /// <summary>Target position for the step.</summary>
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int MapId { get; set; }

    /// <summary>Human-readable description for dashboard.</summary>
    public string Description { get; set; } = "";

    /// <summary>For grind steps: minimum kills or target creature entry.</summary>
    public int GrindCreatureEntry { get; set; }
    public int GrindKillCount { get; set; }
}
