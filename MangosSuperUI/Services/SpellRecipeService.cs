using System.Text.Json;
using System.Text.Json.Serialization;

namespace MangosSuperUI.Services;

/// <summary>
/// Spell visual recipes — hand-curated definitions of what each texture slot
/// in each spell phase (precast/cast/missile/impact) does compositionally.
///
/// ═══════════════════════════════════════════════════════════════════════
/// Session 19: Replaces the filename-heuristic classification system.
///
/// PROBLEM: ClassifyTexture("CS_Thun_3.BLP") → Shape/CenteredShape
///   because the custom filename has no vanilla keywords.
///   The original MOLTENROCK.BLP → Shape/FullCoverage was lost at rename.
///
/// FIX: Recipe JSON defines each slot explicitly:
///   - Role (Atlas, Body, Glow, Ribbon, Ring, Bloom, Shape)
///   - Job (what it does: Trail, Core, Mass, Streak, etc.)
///   - Density (FullCoverage vs CenteredShape)
///   - Blend mode (4=additive, 2=alpha)
///   - Vignette strategy (rgb, alpha, none)
///   - Grid size (for atlases)
///
/// Each slot gets a self-documenting filename:
///   CS_{SpellName}_{Phase}_{Index}_{Role}_{Job}.BLP
///   e.g. CS_ThunderBall_Missile_3_Body_Mass.BLP
///
/// Recipes are stored as JSON files in /opt/mangossuperui/recipes/
/// and loaded at startup. Claude pre-computes them by analyzing vanilla
/// M2 files — this works airgapped since the recipes ship as static JSON.
/// ═══════════════════════════════════════════════════════════════════════
/// </summary>
public class SpellRecipeService
{
    private readonly ILogger<SpellRecipeService> _logger;
    private readonly IConfiguration _config;
    private readonly Dictionary<string, SpellRecipe> _recipes = new(StringComparer.OrdinalIgnoreCase);

    private string RecipePath => _config["SpellCreator:RecipePath"]
        ?? "/opt/mangossuperui/recipes";

    public SpellRecipeService(IConfiguration config, ILogger<SpellRecipeService> logger)
    {
        _config = config;
        _logger = logger;
        LoadRecipes();
    }

    private void LoadRecipes()
    {
        string dir = RecipePath;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _logger.LogInformation("SpellRecipe: Created recipe directory: {Path}", dir);
            return;
        }

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var recipe = JsonSerializer.Deserialize<SpellRecipe>(json, _jsonOptions);
                if (recipe != null && !string.IsNullOrEmpty(recipe.RecipeId))
                {
                    _recipes[recipe.RecipeId] = recipe;
                    _logger.LogInformation("SpellRecipe: Loaded '{Id}' — {Phases} phases from {File}",
                        recipe.RecipeId, recipe.Phases.Count, Path.GetFileName(file));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SpellRecipe: Failed to load {File}", file);
            }
        }

        _logger.LogInformation("SpellRecipe: {Count} recipes loaded", _recipes.Count);
    }

    public SpellRecipe? GetRecipe(string recipeId)
        => _recipes.TryGetValue(recipeId, out var r) ? r : null;

    /// <summary>
    /// Look up a recipe by the vanilla source spell entry ID.
    /// Used by PatchController which knows the source entry number, not the recipe ID.
    /// Returns the first recipe whose SourceSpellEntry matches.
    /// </summary>
    public SpellRecipe? GetRecipeBySourceEntry(uint sourceSpellEntry)
        => _recipes.Values.FirstOrDefault(r => r.SourceSpellEntry == sourceSpellEntry);

    public IReadOnlyDictionary<string, SpellRecipe> GetAllRecipes() => _recipes;

    /// <summary>Save or update a recipe to disk.</summary>
    public void SaveRecipe(SpellRecipe recipe)
    {
        string dir = RecipePath;
        Directory.CreateDirectory(dir);

        string fileName = $"{recipe.RecipeId}.json";
        string filePath = Path.Combine(dir, fileName);
        string json = JsonSerializer.Serialize(recipe, _jsonOptions);
        File.WriteAllText(filePath, json);

        _recipes[recipe.RecipeId] = recipe;
        _logger.LogInformation("SpellRecipe: Saved '{Id}' to {Path}", recipe.RecipeId, filePath);
    }

    /// <summary>
    /// Build the replacement BLP filename for a texture slot from its recipe definition.
    /// Format: CS_{SpellName}_{Phase}_{Index}_{Role}_{Job}.BLP
    /// </summary>
    public static string BuildSlotFilename(string spellName, string phase, RecipeTextureSlot slot)
    {
        string safe = SanitizeName(spellName);
        // e.g. CS_ThunderBall_Missile_3_Body_Mass.BLP
        return $"CS_{safe}_{phase}_{slot.Index}_{slot.Role}_{slot.Job}.BLP";
    }

    /// <summary>
    /// Build the full MPQ path for a texture slot.
    /// </summary>
    public static string BuildSlotMpqPath(string spellName, string phase, RecipeTextureSlot slot)
    {
        return $"SPELLS\\{BuildSlotFilename(spellName, phase, slot)}";
    }

    /// <summary>
    /// Parse role/job/density from a recipe-style filename.
    /// Returns null if the filename doesn't match the recipe naming convention.
    /// </summary>
    public static RecipeTextureSlot? ParseSlotFromFilename(string filename)
    {
        // Expected: CS_{Spell}_{Phase}_{Index}_{Role}_{Job}.BLP
        string name = Path.GetFileNameWithoutExtension(filename);
        var parts = name.Split('_');

        // Need at least: CS, Spell, Phase, Index, Role, Job (6+ parts)
        // But spell name could have underscores, so find from the end:
        // Last part = Job, second-to-last = Role, before that = Index
        if (parts.Length < 6 || parts[0] != "CS") return null;

        string job = parts[^1];
        string role = parts[^2];
        if (!int.TryParse(parts[^3], out int index)) return null;

        return new RecipeTextureSlot
        {
            Index = index,
            Role = role,
            Job = job,
            // Derive density/blend from role
            Density = (role == "Body") ? "FullCoverage" : "CenteredShape",
            BlendMode = 4, // default, can be overridden
            Vignette = (role == "Body") ? "none" : "rgb"
        };
    }

    private static string SanitizeName(string name)
        => new string(name.Where(c => char.IsLetterOrDigit(c)).ToArray());

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

// ═══════════════════════════════════════════════════════════════════════
// RECIPE DATA MODEL
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Complete recipe for a spell's visual effects.
/// Defines every texture slot in every phase with explicit roles.
/// </summary>
public class SpellRecipe
{
    /// <summary>Unique recipe ID, e.g. "fireball_bolt" or "frostbolt_bolt".</summary>
    public string RecipeId { get; set; } = "";

    /// <summary>Human-readable name, e.g. "Fireball (Bolt Spell)".</summary>
    public string Name { get; set; } = "";

    /// <summary>What vanilla spell this recipe is based on.</summary>
    public string SourceSpell { get; set; } = "";
    public uint SourceSpellEntry { get; set; }

    /// <summary>General notes about this spell's visual composition.</summary>
    public string? Notes { get; set; }

    /// <summary>Per-phase definitions. Keys: "precast", "cast", "missile", "impact", "state", "channel".</summary>
    public Dictionary<string, RecipePhase> Phases { get; set; } = new();
}

/// <summary>
/// One phase of a spell's visual (e.g. the missile phase).
/// Contains the M2 model info and all texture slot definitions.
/// </summary>
public class RecipePhase
{
    /// <summary>The vanilla M2 filename for this phase, e.g. "Fireball_Missile_Low.m2".</summary>
    public string SourceM2 { get; set; } = "";

    /// <summary>The SpellVisualEffectName role this M2 occupies, e.g. "missile", "cast_leftHand".</summary>
    public string EffectRole { get; set; } = "";

    /// <summary>Notes about this phase's composition.</summary>
    public string? Notes { get; set; }

    /// <summary>Number of particle emitters in this M2.</summary>
    public int EmitterCount { get; set; }

    /// <summary>All texture slots in this M2, with explicit role definitions.</summary>
    public List<RecipeTextureSlot> Slots { get; set; } = new();
}

/// <summary>
/// One texture slot in an M2 file. Fully defines what this texture does,
/// how it should be generated, and how it should be processed.
/// </summary>
public class RecipeTextureSlot
{
    /// <summary>Index in the M2 texture table (0-based).</summary>
    public int Index { get; set; }

    /// <summary>The vanilla BLP filename this slot originally referenced.</summary>
    public string VanillaFilename { get; set; } = "";

    /// <summary>
    /// What kind of texture this is. Determines prompt strategy and post-processing.
    /// Values: "Atlas", "Body", "Glow", "Ring", "Ribbon", "Bloom", "Shape"
    /// </summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// What compositional job this texture performs in the spell effect.
    /// Human-readable, becomes part of the filename.
    /// Examples: "Trail", "Core", "Mass", "Streak", "Sparkle", "Ring", "Halo"
    /// </summary>
    public string Job { get; set; } = "";

    /// <summary>
    /// How much of the frame content fills.
    /// "FullCoverage" = dense, fills every pixel (MOLTENROCK, LAVALUMP)
    /// "CenteredShape" = distinct shape on black background
    /// </summary>
    public string Density { get; set; } = "CenteredShape";

    /// <summary>
    /// Dominant blend mode from emitters referencing this texture.
    /// 4 = additive (most common), 2 = alpha blend, 0 = opaque.
    /// </summary>
    public byte BlendMode { get; set; } = 4;

    /// <summary>
    /// Vignette strategy for this slot.
    /// "rgb" = fade RGB to black (for additive blend)
    /// "alpha" = fade alpha only (for alpha blend)
    /// "none" = skip vignette (for FullCoverage body textures)
    /// </summary>
    public string Vignette { get; set; } = "rgb";

    /// <summary>Vignette inner radius (where fade starts). Default 0.35.</summary>
    public float VignetteInner { get; set; } = 0.35f;

    /// <summary>Vignette outer radius (where fully faded). Default 0.95.</summary>
    public float VignetteOuter { get; set; } = 0.95f;

    /// <summary>Grid size for atlas textures (4 = 4x4, 8 = 8x8). Ignored for non-atlas.</summary>
    public int GridSize { get; set; } = 4;

    /// <summary>
    /// Which emitter indices reference this texture.
    /// Informational — helps understand the spell composition.
    /// </summary>
    public List<int>? EmitterRefs { get; set; }

    /// <summary>
    /// Brief description of what this texture does in the spell effect.
    /// e.g. "The solid molten ball that forms the fireball sphere"
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Session 22: Vanilla BLP width. Optional — if not set in recipe JSON,
    /// VanillaBlpService reads it from rawblps at generation time.
    /// </summary>
    public int? VanillaWidth { get; set; }

    /// <summary>Session 22: Vanilla BLP height.</summary>
    public int? VanillaHeight { get; set; }

    /// <summary>Session 22: Vanilla BLP format ("DXT1", "DXT3").</summary>
    public string? VanillaFormat { get; set; }
}