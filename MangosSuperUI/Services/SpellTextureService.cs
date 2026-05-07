using System.Text;
using System.Text.Json;
using MangosSuperUI.Controllers;
using SkiaSharp;

namespace MangosSuperUI.Services;

/// <summary>
/// AI-powered particle texture generator for spell visual effects.
///
/// ═══════════════════════════════════════════════════════════════════════
/// Session 21: RUNTIME TRACE INSTRUMENTATION
///
/// The diagnostic JSON from Session 20 was fabricating — it logged what
/// the code INTENDED to do (recipe roles) but BLP Lab + in-game confirmed
/// every texture came out as Shape/CenteredShape. The diagnostic was
/// writing "recipe" into roleResolution after the fact, not capturing
/// what actually executed.
///
/// Fix: GenerateSingleTextureAsync now builds a TextureSlotTrace object
/// at each decision point, capturing the ACTUAL runtime values:
///   - What RecipeRole was on the slot when it arrived
///   - What hasRecipe evaluated to
///   - What ResolveRecipeRole returned
///   - What density was set to
///   - The exact prompt that was built
///   - The vignette mode chosen
///   - The replacement path
/// PatchController collects these and writes {SpellName}_trace.json.
/// ═══════════════════════════════════════════════════════════════════════
///
/// Session 23: COMFYUI POOL DISPATCHER
///
/// Session 26: GRADIENT CLAMPING + ALPHA PIPELINE FIX
///
/// Session 27: RESIZE-FIRST PIPELINE
///
/// The entire post-processing pipeline was reordered. Previously:
///   gradient clamp at 512×512 → lum-alpha at 512×512 → vignette at 512×512
///   → resize inside ConvertPngToBlpInternal → (lum-alpha AGAIN) → encode
///
/// Now:
///   resize to vanilla dimensions FIRST (512→32, 512→128, etc.)
///   → gradient clamp at final resolution
///   → lum-alpha at final resolution (once, not twice)
///   → vignette at final resolution (BOTH RGB+alpha for DXT3 additive)
///   → encode directly from pixel array (no second resize)
///
/// Session 28: BRIGHTNESS FLOOR MASK — Content-Aware Alpha Carving
///
/// Replaces the geometric radial vignette for particle textures.
/// The vignette assumed content was centered and circular — a geometric
/// shape that didn't match the AI-generated content. The brightness
/// floor reads actual pixel brightness and kills everything below a
/// percentage of peak. The particle's visible shape follows the content
/// (lightning arcs, glows, fire wisps) — irregular, organic, blobby —
/// exactly like a hand-painted vanilla texture.
///
/// Pipeline order changed:
///   resize → gradient clamp → BRIGHTNESS FLOOR → lum-alpha → encode
///
/// Brightness floor runs BEFORE lum-alpha so dead pixels (0,0,0,0)
/// stay dead through the lum-alpha pass.
///
/// ComfyUI generation now routes through ComfyUIDispatcher, which manages
/// a pool of ComfyUI nodes. The sequential foreach
/// over texture slots is replaced with Task.WhenAll — up to N textures
/// generate in parallel (one per node). With 2 nodes, a 22-texture spell
/// completes in ~11 min instead of ~22 min.
///
/// Endpoints are configurable via appsettings.json or server-config.json:
///   SpellCreator:ComfyUI:Nodes[]    (pool of ComfyUI instances)
///   SpellCreator:Ollama:BaseUrl     (Ollama API endpoint)
///   SpellCreator:Ollama:Model       (Ollama model tag)
///   SpellCreator:ComfyUI:ClipModel2 (T5 CLIP model filename)
/// ═══════════════════════════════════════════════════════════════════════
/// </summary>
public class SpellTextureService
{
    private readonly HttpClient _http;
    private readonly ILogger<SpellTextureService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly BlpWriterService _blpWriter;
    private readonly ComfyUIDispatcher _comfyDispatcher;

    // Ollama — prompt refinement (configurable via appsettings)
    private readonly string _ollamaUrl;
    private readonly string _ollamaModel;
    private readonly string _clipModel2;

    public SpellTextureService(
        IWebHostEnvironment env,
        BlpWriterService blpWriter,
        ComfyUIDispatcher comfyDispatcher,
        IConfiguration config,
        ILogger<SpellTextureService> logger)
    {
        _env = env;
        _blpWriter = blpWriter;
        _comfyDispatcher = comfyDispatcher;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };

        var ollamaBase = (config["SpellCreator:Ollama:BaseUrl"] ?? "").TrimEnd('/');
        _ollamaUrl = string.IsNullOrEmpty(ollamaBase) ? "" : $"{ollamaBase}/api/generate";
        _ollamaModel = config["SpellCreator:Ollama:Model"] ?? "";
        _clipModel2 = config["SpellCreator:ComfyUI:ClipModel2"] ?? "";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TEXTURE ROLE + DENSITY CLASSIFICATION
    // ═══════════════════════════════════════════════════════════════════════

    public enum TextureRole
    {
        Glow,
        Shape,
        Ring,
        Ribbon,
        Atlas,
        Bloom
    }

    public enum TextureDensity
    {
        FullCoverage,
        CenteredShape
    }

    /// <summary>Classify a vanilla texture filename into a role. FALLBACK — use recipe when available.</summary>
    public static TextureRole ClassifyTexture(string filename)
    {
        string upper = filename.ToUpperInvariant();
        if (upper.Contains("8X8") || upper.Contains("16X16") || upper.Contains("4X4")) return TextureRole.Atlas;
        if (upper.Contains("TOONSMOKE") || upper.Contains("CLOUDS")) return TextureRole.Atlas;
        if (upper.Contains("RIBBON")) return TextureRole.Ribbon;
        if (upper.Contains("SHOCKWAVE") || upper.Contains("RING") || upper.Contains("WAVE")) return TextureRole.Ring;
        if (upper.Contains("LENSFLARE") || upper.Contains("LENS") || upper.Contains("FLARE")) return TextureRole.Bloom;
        if (upper.Contains("GLOW") || upper.Contains("GENERICGLOW")) return TextureRole.Glow;
        return TextureRole.Shape;
    }

    /// <summary>Classify density from vanilla filename. FALLBACK — use recipe when available.</summary>
    public static TextureDensity ClassifyDensity(string filename)
    {
        string upper = filename.ToUpperInvariant();
        if (upper.Contains("MOLTENROCK")) return TextureDensity.FullCoverage;
        if (upper.Contains("LAVALUMP")) return TextureDensity.FullCoverage;
        if (upper.Contains("FLAMELICK")) return TextureDensity.FullCoverage;
        if (upper.Contains("FIREPLUME")) return TextureDensity.FullCoverage;
        if (upper.Contains("FIRENOVA")) return TextureDensity.FullCoverage;
        if (upper.Contains("FIREBLAST")) return TextureDensity.FullCoverage;
        if (upper.Contains("FIRESPIT")) return TextureDensity.FullCoverage;
        if (upper.Contains("FROSTCLOUD")) return TextureDensity.FullCoverage;
        if (upper.Contains("SHADOWCLOUD")) return TextureDensity.FullCoverage;
        if (upper.Contains("HOLYCLOUD")) return TextureDensity.FullCoverage;
        if (upper.Contains("ARCANECLOUD")) return TextureDensity.FullCoverage;
        if (upper.Contains("SMOKE") && !upper.Contains("TOONSMOKE")) return TextureDensity.FullCoverage;
        if (upper.Contains("LAVA") && !upper.Contains("LAVALUMP")) return TextureDensity.FullCoverage;
        return TextureDensity.CenteredShape;
    }

    /// <summary>
    /// Session 19: Resolve role from recipe string. "Body" maps to Shape role
    /// (with FullCoverage density handled separately).
    /// </summary>
    private static TextureRole ResolveRecipeRole(string recipeRole)
    {
        return recipeRole.ToLowerInvariant() switch
        {
            "atlas" => TextureRole.Atlas,
            "body" => TextureRole.Shape,
            "glow" => TextureRole.Glow,
            "ring" => TextureRole.Ring,
            "ribbon" => TextureRole.Ribbon,
            "bloom" => TextureRole.Bloom,
            "shape" => TextureRole.Shape,
            _ => TextureRole.Shape
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // THEME DEFINITIONS — unchanged from Session 17
    // ═══════════════════════════════════════════════════════════════════════

    public static readonly Dictionary<string, TextureTheme> Themes = new()
    {
        ["lightning"] = new TextureTheme
        {
            Name = "Lightning / Electrical",
            Color = "electric blue-white",
            RolePrompts = new()
            {
                [TextureRole.Glow] = "electric blue-white energy glow, bright crackling center, electrical discharge aura, plasma light",
                [TextureRole.Shape] = "single lightning bolt spark, jagged electrical arc fragment, bright blue-white plasma, sharp angular shape",
                [TextureRole.Ring] = "circular electrical shockwave ring, blue-white lightning spreading outward in a ring pattern, crackling energy",
                [TextureRole.Ribbon] = "horizontal electrical energy trail, blue-white lightning streak with noisy jagged irregular edges, crackling current arc, not smooth",
                [TextureRole.Atlas] = "electrical discharge dissipation, blue-white sparks scattering and fading, crackling plasma fragments",
                [TextureRole.Bloom] = "electric blue-white energy bloom, bright plasma core with electrical fringing, lens flare with sparks",
            },
            BodyPrompt = "dense chaotic electrical discharge pattern, blue-white crackling plasma noise, like a microscopic view of ball lightning, chaotic arcs and filaments overlapping, every pixel has visible electrical energy"
        },
        ["void"] = new TextureTheme
        {
            Name = "Void / Shadow",
            Color = "deep purple-black",
            RolePrompts = new()
            {
                [TextureRole.Glow] = "deep purple void energy glow, dark matter aura with faint purple highlights",
                [TextureRole.Shape] = "single void fragment, dark purple-black crystalline shard, shadow energy",
                [TextureRole.Ring] = "circular void rift ring, deep purple shadow energy expanding in a ring",
                [TextureRole.Ribbon] = "horizontal shadow energy trail, dark purple void streak with wispy edges",
                [TextureRole.Atlas] = "void energy dissipation, purple-black shadow fragments fading",
                [TextureRole.Bloom] = "dark purple void bloom, shadowy core with purple fringing",
            },
            BodyPrompt = "dense swirling void energy pattern, deep purple-black shadow noise, like looking into dark matter, tendrils and wisps overlapping"
        },
        ["holy"] = new TextureTheme
        {
            Name = "Holy / Divine",
            Color = "golden-white",
            RolePrompts = new()
            {
                [TextureRole.Glow] = "golden divine light glow, warm holy radiance, sacred energy aura",
                [TextureRole.Shape] = "single holy light fragment, golden crystalline spark, divine energy",
                [TextureRole.Ring] = "circular holy shockwave ring, golden light expanding outward",
                [TextureRole.Ribbon] = "horizontal holy energy trail, golden light streak with soft edges",
                [TextureRole.Atlas] = "divine light dissipation, golden sparks fading gracefully",
                [TextureRole.Bloom] = "golden holy bloom, divine light core with warm radiance",
            },
            BodyPrompt = "dense divine light pattern, golden-white holy energy, warm radiance filling every pixel, overlapping rays and sacred geometry"
        },
        ["frost"] = new TextureTheme
        {
            Name = "Frost / Ice",
            Color = "icy blue-white",
            RolePrompts = new()
            {
                [TextureRole.Glow] = "icy blue frost glow, crystalline cold energy aura, frozen light",
                [TextureRole.Shape] = "single ice crystal, sharp faceted frost shard, icy blue transparent",
                [TextureRole.Ring] = "circular frost shockwave ring, ice crystals expanding outward in a ring",
                [TextureRole.Ribbon] = "horizontal frost trail, icy blue streak with crystalline edges",
                [TextureRole.Atlas] = "ice crystal shattering sequence, blue-white frost fragments scattering",
                [TextureRole.Bloom] = "icy blue frost bloom, frozen core with crystalline fringing",
            },
            BodyPrompt = "dense ice crystal texture, icy blue-white frost patterns, like frozen surface under microscope, crystalline structures overlapping"
        },
        ["nature"] = new TextureTheme
        {
            Name = "Nature / Growth",
            Color = "vibrant green",
            RolePrompts = new()
            {
                [TextureRole.Glow] = "vibrant green nature glow, living energy aura, growth light",
                [TextureRole.Shape] = "single leaf or vine fragment, vibrant green plant energy",
                [TextureRole.Ring] = "circular nature shockwave ring, green vines expanding outward",
                [TextureRole.Ribbon] = "horizontal vine trail, green nature energy with leaf edges",
                [TextureRole.Atlas] = "nature energy dissipation, green leaves and petals scattering",
                [TextureRole.Bloom] = "vibrant green nature bloom, growth energy with leaf fringing",
            },
            BodyPrompt = "dense swirling nature energy, vibrant green organic patterns, living vines and leaves overlapping, every pixel filled with growth"
        },
        ["arcane"] = new TextureTheme
        {
            Name = "Arcane / Magical",
            Color = "violet-purple",
            RolePrompts = new()
            {
                [TextureRole.Glow] = "arcane purple energy glow, mystical violet aura, magical radiance",
                [TextureRole.Shape] = "single arcane rune fragment, violet magical symbol, mystical energy",
                [TextureRole.Ring] = "circular arcane shockwave ring, purple magical energy expanding",
                [TextureRole.Ribbon] = "horizontal arcane trail, violet magical streak with runic edges",
                [TextureRole.Atlas] = "arcane energy dissipation, purple rune fragments fading",
                [TextureRole.Bloom] = "arcane purple bloom, mystical core with violet fringing",
            },
            BodyPrompt = "dense arcane energy pattern, violet-purple magical noise, swirling runes and mystical symbols overlapping"
        },
        ["crystal_ice"] = new TextureTheme
        {
            Name = "Crystal Ice",
            Color = "pale crystalline blue",
            RolePrompts = new()
            {
                [TextureRole.Glow] = "pale crystalline ice glow, faceted diamond-like cold light",
                [TextureRole.Shape] = "single crystal ice shard, pale blue transparent faceted gem",
                [TextureRole.Ring] = "circular crystal ice ring, faceted shards expanding outward",
                [TextureRole.Ribbon] = "horizontal crystal ice trail, pale blue faceted streak",
                [TextureRole.Atlas] = "crystal ice shattering, pale blue faceted fragments scattering",
                [TextureRole.Bloom] = "crystalline ice bloom, diamond-like pale blue core",
            },
            BodyPrompt = "dense crystal ice texture, pale blue faceted patterns, like inside a geode, crystalline structures overlapping"
        },
    };

    // ═══════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════════════

    public async Task<TextureGenerationResult> GenerateTexturesAsync(TextureGenerationRequest request)
    {
        var result = new TextureGenerationResult { SpellName = request.SpellName };
        string safeSpellName = SanitizeName(request.SpellName);

        string texDir = Path.Combine(_env.WebRootPath, "images", "textures", "custom", safeSpellName);
        Directory.CreateDirectory(texDir);

        TextureTheme? theme = null;
        if (!string.IsNullOrEmpty(request.ThemeKey) && Themes.TryGetValue(request.ThemeKey, out theme))
        {
            _logger.LogInformation("TextureGen: Using theme '{Theme}' for {Spell}",
                theme.Name, request.SpellName);
        }

        // ═══════════════════════════════════════════════════════════════
        // Session 23: Parallel dispatch across ComfyUI node pool.
        // Each GenerateSingleTextureAsync internally calls the dispatcher,
        // which blocks (async) until a node is free. With N nodes in the
        // pool, up to N textures generate concurrently. The dispatcher
        // handles queueing — we just fire them all at once.
        // ═══════════════════════════════════════════════════════════════
        var slotTasks = request.TextureSlots.Select(slot =>
            GenerateSingleTextureWithCatchAsync(slot, theme, safeSpellName, texDir, request.SpellName)
        ).ToList();

        var slotResults = await Task.WhenAll(slotTasks);

        foreach (var (generated, trace, error) in slotResults)
        {
            if (trace != null) result.Traces.Add(trace);

            if (generated != null)
            {
                result.Textures.Add(generated);
            }
            else
            {
                result.Errors.Add(error ?? "Texture generation failed");
            }
        }

        result.Success = result.Textures.Count > 0;
        result.OutputDirectory = texDir;

        _logger.LogInformation("TextureGen: Generated {Count}/{Total} textures for {Spell} ({Errors} errors)",
            result.Textures.Count, request.TextureSlots.Count, request.SpellName, result.Errors.Count);

        return result;
    }

    /// <summary>
    /// Session 23: Exception-safe wrapper for parallel dispatch.
    /// Returns a tuple so Task.WhenAll never faults the aggregate.
    /// </summary>
    private async Task<(GeneratedTexture? texture, TextureSlotTrace? trace, string? error)>
        GenerateSingleTextureWithCatchAsync(
            TextureSlotRequest slot,
            TextureTheme? theme,
            string safeSpellName,
            string texDir,
            string spellName)
    {
        try
        {
            var (generated, trace) = await GenerateSingleTextureAsync(
                slot, theme, safeSpellName, texDir, spellName);

            if (generated != null)
                return (generated, trace, null);

            return (null, trace,
                $"Texture [{slot.Index}] ({slot.OriginalFilename}): generation failed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TextureGen: Failed to generate texture [{Index}] for {Spell}",
                slot.Index, spellName);
            return (null, null, $"Texture [{slot.Index}]: {ex.Message}");
        }
    }

    /// <summary>
    /// Session 19: Convert a recipe phase into TextureSlotRequests.
    /// Called by PatchController when generating textures for a recipe-defined spell.
    /// </summary>
    public static List<TextureSlotRequest> RecipePhaseToSlots(RecipePhase phase, string phaseName)
    {
        var slots = new List<TextureSlotRequest>();
        foreach (var recipeSlot in phase.Slots)
        {
            slots.Add(new TextureSlotRequest
            {
                Index = recipeSlot.Index,
                OriginalFilename = recipeSlot.VanillaFilename,
                OriginalFilenameLength = recipeSlot.VanillaFilename.Length + 1,
                OriginalWidth = recipeSlot.VanillaWidth ?? 0,
                OriginalHeight = recipeSlot.VanillaHeight ?? 0,
                VanillaFormat = recipeSlot.VanillaFormat,
                Phase = phaseName,
                RecipeRole = recipeSlot.Role,
                RecipeJob = recipeSlot.Job,
                RecipeDensity = recipeSlot.Density,
                BlendMode = recipeSlot.BlendMode,
                RecipeVignette = recipeSlot.Vignette,
                RecipeVignetteInner = recipeSlot.VignetteInner,
                RecipeVignetteOuter = recipeSlot.VignetteOuter,
                RecipeGridSize = recipeSlot.GridSize,
            });
        }
        return slots;
    }

    /// <summary>
    /// Generate a single texture for one M2 texture slot.
    /// Session 21: Returns a trace object alongside the generated texture.
    /// </summary>
    private async Task<(GeneratedTexture? texture, TextureSlotTrace? trace)> GenerateSingleTextureAsync(
        TextureSlotRequest slot,
        TextureTheme? theme,
        string safeSpellName,
        string texDir,
        string spellName)
    {
        // ═══════════════════════════════════════════════════════════════
        // Session 21: Build trace at each decision point
        // ═══════════════════════════════════════════════════════════════
        var trace = new TextureSlotTrace
        {
            SlotIndex = slot.Index,
            Phase = slot.Phase,
            OriginalFilename = slot.OriginalFilename,
            OriginalFilenameLength = slot.OriginalFilenameLength,
            Timestamp = DateTime.UtcNow.ToString("o"),

            // ── TRACE POINT 1: What arrived on the slot ──
            Input_RecipeRole = slot.RecipeRole,
            Input_RecipeJob = slot.RecipeJob,
            Input_RecipeDensity = slot.RecipeDensity,
            Input_RecipeVignette = slot.RecipeVignette,
            Input_RecipeVignetteInner = slot.RecipeVignetteInner,
            Input_RecipeVignetteOuter = slot.RecipeVignetteOuter,
            Input_RecipeGridSize = slot.RecipeGridSize,
            Input_BlendMode = slot.BlendMode,
            Input_RoleOverride = slot.RoleOverride?.ToString(),
            Input_CustomPrompt = slot.CustomPrompt,

            // ── Session 22: Vanilla BLP reference ──
            Input_VanillaFormat = slot.VanillaFormat,
            Input_VanillaWidth = slot.OriginalWidth,
            Input_VanillaHeight = slot.OriginalHeight,
            Input_VanillaAlphaDepth = slot.VanillaAlphaDepth,
        };

        // ── TRACE POINT 2: hasRecipe evaluation ──
        bool hasRecipe = !string.IsNullOrEmpty(slot.RecipeRole);
        trace.Decision_HasRecipe = hasRecipe;
        trace.Decision_RecipeRoleWasNull = slot.RecipeRole == null;
        trace.Decision_RecipeRoleWasEmpty = slot.RecipeRole == "";
        trace.Decision_RecipeRoleRawValue = slot.RecipeRole ?? "(null)";

        // ═══════════════════════════════════════════════════════════════
        // Role + Density resolution
        // ═══════════════════════════════════════════════════════════════
        TextureRole role;
        TextureDensity density;

        if (hasRecipe)
        {
            role = ResolveRecipeRole(slot.RecipeRole!);
            density = (slot.RecipeDensity ?? "").ToLowerInvariant() == "fullcoverage"
                ? TextureDensity.FullCoverage
                : TextureDensity.CenteredShape;

            // ── TRACE POINT 3a: Recipe path taken ──
            trace.Decision_Path = "RECIPE";
            trace.Decision_ResolvedRole = role.ToString();
            trace.Decision_ResolvedDensity = density.ToString();
            trace.Decision_ResolveRecipeRoleInput = slot.RecipeRole!;
            trace.Decision_ResolveRecipeRoleOutput = role.ToString();
        }
        else
        {
            role = slot.RoleOverride ?? ClassifyTexture(slot.OriginalFilename);
            density = ClassifyDensity(slot.OriginalFilename);

            // ── TRACE POINT 3b: Heuristic path taken ──
            trace.Decision_Path = "HEURISTIC";
            trace.Decision_ResolvedRole = role.ToString();
            trace.Decision_ResolvedDensity = density.ToString();
            trace.Decision_HeuristicInput = slot.OriginalFilename;
            trace.Decision_HadRoleOverride = slot.RoleOverride != null;
            if (slot.RoleOverride != null)
                trace.Decision_RoleOverrideValue = slot.RoleOverride.Value.ToString();
        }

        // ── TRACE POINT 4: Prompt building ──
        string prompt;
        if (!string.IsNullOrEmpty(slot.CustomPrompt))
        {
            prompt = slot.CustomPrompt;
            trace.Prompt_Source = "CUSTOM_PROMPT";
        }
        else if (theme != null)
        {
            prompt = BuildFullPrompt(theme, role, density, slot);
            trace.Prompt_Source = "THEME";
            trace.Prompt_ThemeName = theme.Name;
            trace.Prompt_RoleUsedForLookup = role.ToString();
            trace.Prompt_DensityUsedForLookup = density.ToString();
            // Check if BodyPrompt path was taken
            trace.Prompt_UsedBodyPrompt = (role == TextureRole.Shape
                && density == TextureDensity.FullCoverage
                && !string.IsNullOrEmpty(theme.BodyPrompt));
            // Check if role had a matching RolePrompt entry
            trace.Prompt_HadRolePromptEntry = theme.RolePrompts.ContainsKey(role);
        }
        else
        {
            prompt = BuildDefaultPrompt(role, density, spellName, slot);
            trace.Prompt_Source = "DEFAULT";
        }
        trace.Prompt_Final = prompt;

        if (slot.UseOllamaRefinement)
        {
            prompt = await RefinePromptAsync(prompt, role, spellName) ?? prompt;
            trace.Prompt_OllamaRefined = true;
            trace.Prompt_AfterOllama = prompt;
        }

        var (width, height) = GetGenerationSize(role, slot);

        // ── TRACE POINT 5: Output filename ──
        string outputFilename;
        if (hasRecipe && !string.IsNullOrEmpty(slot.RecipeJob))
            outputFilename = $"tex_{safeSpellName}_{slot.Phase}_{slot.Index}_{slot.RecipeRole}_{slot.RecipeJob}";
        else
            outputFilename = $"tex_{safeSpellName}_{slot.Index}_{role.ToString().ToLower()}";
        trace.Output_Filename = outputFilename;

        string recipeInfo = hasRecipe
            ? $" recipe={slot.RecipeRole}/{slot.RecipeJob}"
            : " (heuristic)";
        _logger.LogInformation(
            "TextureGen: [{Index}] role={Role} density={Density} blend={Blend}{Recipe} size={W}x{H}",
            slot.Index, role, density, slot.BlendMode, recipeInfo, width, height);

        // ── TRACE POINT 6: ComfyUI call (Session 23: routed through dispatcher) ──
        string? pngPath = await GenerateWithComfyAsync(prompt, outputFilename, width, height);
        trace.ComfyUI_Success = pngPath != null;
        trace.ComfyUI_OutputPath = pngPath;

        if (pngPath == null)
        {
            _logger.LogWarning("TextureGen: ComfyUI generation failed for [{Index}]", slot.Index);
            trace.Output_Error = "ComfyUI generation failed";
            return (null, trace);
        }

        string savedPath = Path.Combine(texDir, $"{outputFilename}.png");
        if (pngPath != savedPath)
            File.Copy(pngPath, savedPath, overwrite: true);

        // ═══════════════════════════════════════════════════════════════════
        // Session 28: POST-PROCESSING PIPELINE
        //
        // Pipeline order:
        //   1. Resize to vanilla dimensions (512→32, 512→128, etc.)
        //   2. Gradient clamping at final resolution
        //   3. BRIGHTNESS FLOOR MASK (Session 28 — replaces vignette)
        //   4. Luminance-to-alpha at final resolution (DXT3 only, once)
        //   5. Encode directly from pixel array
        //
        // The brightness floor kills pixels below a percentage of peak
        // brightness. The particle's visible shape follows the content,
        // not a geometric circle. Dead pixels (0,0,0,0) contribute
        // nothing under additive blending, making quad edges invisible.
        // ═══════════════════════════════════════════════════════════════════

        // ── Determine output format and dimensions ──
        int blpWidth = slot.OriginalWidth > 0 ? slot.OriginalWidth : Math.Max(width, height);
        int blpHeight = slot.OriginalHeight > 0 ? slot.OriginalHeight : Math.Max(width, height);
        bool useDxt1 = slot.VanillaAlphaDepth == 0 && slot.OriginalWidth > 0;

        bool isAdditive = slot.BlendMode == 4 || slot.BlendMode == 0 || slot.BlendMode == 1;
        bool vanillaHasNoAlpha = slot.VanillaAlphaDepth == 0 && slot.OriginalWidth > 0;
        bool skipAlphaPipeline = vanillaHasNoAlpha
            || (isAdditive && density == TextureDensity.FullCoverage && role == TextureRole.Shape);

        // ── Step 1: RESIZE to vanilla dimensions ──
        using var resizedBitmap = _blpWriter.ResizePngToBitmap(savedPath, blpWidth, blpHeight);
        if (resizedBitmap == null)
        {
            _logger.LogWarning("TextureGen: Resize to {W}x{H} failed for [{Index}]", blpWidth, blpHeight, slot.Index);
            trace.Output_Error = "Resize failed";
            return (null, trace);
        }

        _logger.LogInformation("TextureGen: [{Index}] resized {SrcW}x{SrcH} → {DstW}x{DstH}",
            slot.Index, width, height, blpWidth, blpHeight);

        // ── Step 2: GRADIENT CLAMPING at final resolution ──
        try
        {
            bool clamped = ApplyGradientClamping(resizedBitmap, maxGradient: 35, passes: 20);
            trace.PostProcess_GradientClamped = clamped;
        }
        catch (Exception gcEx)
        {
            _logger.LogDebug(gcEx, "TextureGen: Gradient clamping failed for [{Index}] (non-fatal)", slot.Index);
        }

        // ═══════════════════════════════════════════════════════════════
        // Step 3: BRIGHTNESS FLOOR MASK (Session 28)
        //
        // Replaces the radial vignette. Instead of a geometric circle,
        // this kills pixels based on actual brightness relative to peak.
        // Dark corners/edges → dead (0,0,0,0). Bright content → preserved.
        // The particle's visible shape follows the content, not a circle.
        //
        // Must run BEFORE lum-alpha: we want to kill dark pixels while
        // RGB still represents the original brightness. Lum-alpha would
        // convert brightness to alpha, making the floor check inaccurate.
        //
        // Skipped for:
        //   - FullCoverage body textures (content fills entire frame by design)
        //   - Ribbon textures (horizontal strip, no masking needed)
        //
        // Atlas textures use per-cell variant so each animation frame
        // gets independent peak brightness reference.
        // ═══════════════════════════════════════════════════════════════

        // Determine masking strategy
        string maskMode;
        if (density == TextureDensity.FullCoverage && role == TextureRole.Shape)
            maskMode = "content_gentle";  // was "none" — still apply floor but softer
        else if (role == TextureRole.Ribbon)
            maskMode = "none";       // ribbon: horizontal strip, no masking
        else if (role == TextureRole.Atlas)
            maskMode = "atlas";      // atlas: per-cell brightness floor
        else
            maskMode = "content";    // everything else: brightness floor

        // Allow recipe to override (backward compat + future tuning)
        if (slot.RecipeVignette != null)
        {
            string rv = slot.RecipeVignette.ToLowerInvariant();
            if (rv == "none")
                maskMode = "none";
            // "rgb", "alpha", "both", "content" all map to content-aware masking now
        }

        // Brightness floor parameters — tunable per role
        float floorPercent = role switch
        {
            TextureRole.Glow => 0.08f,    // glows have soft halos — be gentler
            TextureRole.Bloom => 0.08f,    // same for blooms
            TextureRole.Ring => 0.10f,     // rings have thin bright arcs
            TextureRole.Atlas => 0.12f,    // atlas frames are small, standard threshold
            TextureRole.Shape => 0.15f,    // shapes can be more aggressive
            _ => 0.12f
        };
        float kneeWidth = 0.08f;  // smooth transition band

        int gridSize = slot.RecipeGridSize ?? 4;
        if (role == TextureRole.Atlas && slot.RecipeGridSize == null)
        {
            string upperOrig = slot.OriginalFilename.ToUpperInvariant();
            if (upperOrig.Contains("8X8")) gridSize = 8;
            else if (upperOrig.Contains("16X16")) gridSize = 16;
        }

        // ── TRACE POINT 7: Masking decision ──
        trace.Vignette_Mode = maskMode;
        trace.Vignette_Inner = floorPercent;   // repurposed: floor threshold
        trace.Vignette_Outer = kneeWidth;      // repurposed: knee width
        trace.Vignette_RecipeProvided = slot.RecipeVignette != null;
        trace.Vignette_IsAdditive = isAdditive;

        switch (maskMode)
        {
            case "none":
                _logger.LogDebug("TextureGen: [{Index}] mask=none (FullCoverage body or ribbon)", slot.Index);
                break;

            case "atlas":
                _blpWriter.ApplyAtlasBrightnessFloorMask(resizedBitmap, gridSize, floorPercent, kneeWidth);
                _logger.LogInformation("TextureGen: [{Index}] atlas brightness floor ({Grid}x{Grid}, floor={Floor})",
                    slot.Index, gridSize, floorPercent);
                break;

            case "content":
                _blpWriter.ApplyBrightnessFloorMask(resizedBitmap, floorPercent, kneeWidth);
                _logger.LogInformation("TextureGen: [{Index}] brightness floor (floor={Floor}, knee={Knee})",
                    slot.Index, floorPercent, kneeWidth);
                break;
            case "content_gentle":
                _blpWriter.ApplyBrightnessFloorMask(resizedBitmap, 0.05f, 0.05f);
                break;
        }

        // ── Step 4: LUMINANCE → ALPHA at final resolution ──
        // Applied exactly ONCE, on the resized+masked pixel array.
        // Skipped for DXT1 (no alpha block) and FullCoverage additive body.
        // Runs AFTER brightness floor so dead pixels (0,0,0,0) stay dead.
        if (!skipAlphaPipeline)
        {
            _blpWriter.ApplyLuminanceAlphaPixels(resizedBitmap);
        }
        trace.PostProcess_SkippedLuminanceAlpha = skipAlphaPipeline;

        // ── Step 5: ENCODE directly from pixel array ──
        byte[]? blpBytes = _blpWriter.EncodeBitmapToBlp(resizedBitmap, useDxt1);

        string fmtLabel = useDxt1 ? "DXT1" : (skipAlphaPipeline ? "DXT3 no-lum" : "DXT3 +lum");
        trace.PostProcess_BlpEncoder = $"EncodeBitmapToBlp ({fmtLabel}, {blpWidth}x{blpHeight})";
        trace.Output_GenerationSize = $"{width}x{height} (FLUX) → {blpWidth}x{blpHeight} (resize-first)";

        if (blpBytes == null)
        {
            _logger.LogWarning("TextureGen: BLP conversion failed for [{Index}]", slot.Index);
            trace.Output_Error = "BLP conversion failed";
            return (null, trace);
        }

        // ═══════════════════════════════════════════════════════════════
        // Session 20: Replacement path — MUST fit in M2 filename slot
        // ═══════════════════════════════════════════════════════════════
        string replacementPath = BuildReplacementPath(safeSpellName, slot.Index, slot.OriginalFilenameLength, slot.Phase);

        // ── TRACE POINT 8: Final output ──
        trace.Output_ReplacementPath = replacementPath;
        trace.Output_BlpSize = blpBytes.Length;
        trace.Output_Role = role.ToString();
        trace.Output_Density = density.ToString();

        var generated = new GeneratedTexture
        {
            TextureIndex = slot.Index,
            Role = role,
            Prompt = prompt,
            PngPath = savedPath,
            BlpBytes = blpBytes,
            ReplacementMpqPath = replacementPath,
            OriginalFilename = slot.OriginalFilename,
            Width = width,
            Height = height,
        };

        return (generated, trace);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PROMPT BUILDING — Density-Aware
    // ═══════════════════════════════════════════════════════════════════════

    private static string BuildFullPrompt(TextureTheme theme, TextureRole role, TextureDensity density, TextureSlotRequest slot)
    {
        if (role == TextureRole.Shape && density == TextureDensity.FullCoverage
            && !string.IsNullOrEmpty(theme.BodyPrompt))
        {
            return $"Seamless game particle texture on pure black background. " +
                   $"Shows {theme.BodyPrompt}. " +
                   "The content must fill the ENTIRE image with NO empty black regions. " +
                   "This is a body/mass texture where many particles overlap — " +
                   "dense, noisy, organic pattern, not a centered shape. " +
                   "Think zoomed-in surface texture, not an isolated object.";
        }

        string themeRolePrompt = "";
        if (theme.RolePrompts.TryGetValue(role, out var rp))
            themeRolePrompt = rp;
        else
            themeRolePrompt = theme.Color + " energy effect";

        string baseInstruction = role switch
        {
            TextureRole.Atlas =>
                $"Sprite sheet grid of animation frames on pure black background. " +
                $"Each frame shows a stage of {themeRolePrompt}. " +
                $"4x4 grid, 16 evenly spaced square frames, consistent style. " +
                "Game particle texture atlas, clean separation between frames.",

            TextureRole.Ribbon =>
                $"Horizontal game particle trail texture on pure black background. " +
                $"Shows {themeRolePrompt}. " +
                "Wide horizontal orientation, energy fading from bright left to transparent right. " +
                "Game ribbon particle texture, seamless horizontal tiling possible.",

            TextureRole.Ring =>
                $"Circular ring effect on pure black background. " +
                $"Shows {themeRolePrompt}. " +
                "Perfect circle, bright ring with dark empty center. " +
                "Game shockwave particle texture, centered.",

            TextureRole.Glow =>
                $"Radial glow particle sprite on pure black background. " +
                $"Shows {themeRolePrompt}. " +
                "Bright center smoothly fading to transparent edges, circular shape. " +
                "Game glow particle texture, centered, soft edges.",

            TextureRole.Bloom =>
                $"Bright lens flare bloom on pure black background. " +
                $"Shows {themeRolePrompt}. " +
                "Intense bright center with light rays and chromatic fringing. " +
                "Game bloom particle texture, centered.",

            _ =>
                $"Single isolated particle sprite on pure black background. " +
                $"Shows {themeRolePrompt}. " +
                "One distinct shape, no duplicates, centered in frame. " +
                "Game particle texture, sharp details, clean edges against black."
        };

        return baseInstruction;
    }

    private static string BuildDefaultPrompt(TextureRole role, TextureDensity density, string spellName, TextureSlotRequest slot)
    {
        if (role == TextureRole.Shape && density == TextureDensity.FullCoverage)
        {
            return $"Dense seamless energy texture for a spell called \"{spellName}\". " +
                   "Fills the entire image with chaotic energy patterns, no empty space, " +
                   "suitable as a World of Warcraft spell particle body texture.";
        }

        string roleDesc = role switch
        {
            TextureRole.Glow => "radial energy glow",
            TextureRole.Shape => "particle sprite",
            TextureRole.Ring => "circular shockwave ring",
            TextureRole.Ribbon => "horizontal energy trail",
            TextureRole.Atlas => "4x4 animation sprite sheet",
            TextureRole.Bloom => "bright lens bloom",
            _ => "particle sprite"
        };

        return $"Game particle texture for a spell called \"{spellName}\". " +
               $"A {roleDesc} on pure black background, centered, " +
               "suitable as a World of Warcraft spell particle texture.";
    }

    /// <summary>Optionally refine a prompt through Ollama for better results.</summary>
    private async Task<string?> RefinePromptAsync(string basePrompt, TextureRole role, string spellName)
    {
        try
        {
            string systemPrompt =
                "You refine image generation prompts for particle textures used in a World of Warcraft spell system. " +
                "The textures must be on pure BLACK background (black = transparent in additive blending). " +
                "Keep prompts concise (under 150 words). Output ONLY the refined prompt, nothing else.";

            var payload = new
            {
                model = _ollamaModel,
                prompt = $"Refine this game texture prompt for a {role} texture for spell '{spellName}':\n\n{basePrompt}",
                system = systemPrompt,
                stream = false,
                options = new { temperature = 0.7, num_predict = 200 }
            };

            var resp = await _http.PostAsync(_ollamaUrl,
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

            if (!resp.IsSuccessStatusCode) return null;

            var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            return json.TryGetProperty("response", out var r) ? r.GetString()?.Trim() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TextureGen: Ollama refinement failed (non-fatal)");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMFYUI GENERATION — Session 23: Routed through ComfyUIDispatcher
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Session 23: Route through ComfyUIDispatcher instead of hitting a single node.
    /// The dispatcher acquires a free node from the pool, queues the workflow,
    /// polls for output, downloads the image, and releases the node.
    /// </summary>
    private async Task<string?> GenerateWithComfyAsync(string prompt, string outputPrefix, int width, int height)
    {
        try
        {
            var workflow = BuildFluxWorkflow(prompt, outputPrefix, width, height);
            string outputDir = Path.Combine(_env.WebRootPath, "images", "textures", "comfyui_output");
            return await _comfyDispatcher.GenerateAsync(
                workflow, $"tex_{outputPrefix}", outputDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TextureGen: ComfyUI dispatcher error for {Prefix}", outputPrefix);
            return null;
        }
    }

    private Dictionary<string, object> BuildFluxWorkflow(string prompt, string outputPrefix, int width, int height)
    {
        return new Dictionary<string, object>
        {
            ["3"] = new Dictionary<string, object>
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["model"] = new object[] { "4", 0 },
                    ["positive"] = new object[] { "6", 0 },
                    ["negative"] = new object[] { "7", 0 },
                    ["latent_image"] = new object[] { "5", 0 },
                    ["seed"] = Random.Shared.Next(),
                    ["steps"] = 20,
                    ["cfg"] = 1.0,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "simple",
                    ["denoise"] = 1.0
                }
            },
            ["4"] = new Dictionary<string, object>
            {
                ["class_type"] = "UnetLoaderGGUF",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["unet_name"] = "flux1-dev-Q5_K_S.gguf",
                }
            },
            ["5"] = new Dictionary<string, object>
            {
                ["class_type"] = "EmptySD3LatentImage",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["width"] = width,
                    ["height"] = height,
                    ["batch_size"] = 1
                }
            },
            ["6"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["clip"] = new object[] { "10", 0 },
                    ["text"] = prompt
                }
            },
            ["7"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["clip"] = new object[] { "10", 0 },
                    ["text"] = ""
                }
            },
            ["10"] = new Dictionary<string, object>
            {
                ["class_type"] = "DualCLIPLoader",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["clip_name1"] = _clipModel2,
                    ["clip_name2"] = "clip_l.safetensors",
                    ["type"] = "flux"
                }
            },
            ["11"] = new Dictionary<string, object>
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["samples"] = new object[] { "3", 0 },
                    ["vae"] = new object[] { "8", 0 }
                }
            },
            ["8"] = new Dictionary<string, object>
            {
                ["class_type"] = "VAELoader",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["vae_name"] = "ae.safetensors"
                }
            },
            ["9"] = new Dictionary<string, object>
            {
                ["class_type"] = "SaveImage",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["images"] = new object[] { "11", 0 },
                    ["filename_prefix"] = $"spell_tex_{outputPrefix}"
                }
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SESSION 26: GRADIENT CLAMPING
    //
    // Iteratively smooth pixel-to-pixel transitions that exceed a threshold.
    // Uses a separable approach: for each pixel, if the max RGB difference
    // to any 4-neighbor exceeds maxGradient, blend toward the local average.
    //
    // Session 27: Now runs at FINAL resolution (32×32, 128×128) instead of
    // 512×512. The 4×4 DXT blocks are exactly these pixels — clamping
    // directly smooths the transitions the encoder will see.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Apply gradient clamping to a bitmap in-place.
    /// Returns true if any pixels were modified.
    /// </summary>
    private bool ApplyGradientClamping(SKBitmap bitmap, int maxGradient = 35, int passes = 20)
    {
        int w = bitmap.Width;
        int h = bitmap.Height;
        if (w < 4 || h < 4) return false;

        var pixels = bitmap.Pixels;
        // Work in float arrays for precision
        var r = new float[w * h];
        var g = new float[w * h];
        var b = new float[w * h];
        var a = new byte[w * h]; // preserve alpha untouched

        for (int i = 0; i < pixels.Length; i++)
        {
            r[i] = pixels[i].Red;
            g[i] = pixels[i].Green;
            b[i] = pixels[i].Blue;
            a[i] = pixels[i].Alpha;
        }

        bool anyChanged = false;

        for (int pass = 0; pass < passes; pass++)
        {
            int changedThisPass = 0;

            var newR = new float[w * h];
            var newG = new float[w * h];
            var newB = new float[w * h];
            Array.Copy(r, newR, r.Length);
            Array.Copy(g, newG, g.Length);
            Array.Copy(b, newB, b.Length);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    float pr = r[idx], pg = g[idx], pb = b[idx];

                    // Compute max RGB distance to any 4-neighbor
                    float maxDiff = 0;
                    float avgR = pr, avgG = pg, avgB = pb;
                    int neighborCount = 1;

                    // Left
                    if (x > 0)
                    {
                        int ni = idx - 1;
                        float d = MaxChannelDiff(pr, pg, pb, r[ni], g[ni], b[ni]);
                        if (d > maxDiff) maxDiff = d;
                        avgR += r[ni]; avgG += g[ni]; avgB += b[ni];
                        neighborCount++;
                    }
                    // Right
                    if (x < w - 1)
                    {
                        int ni = idx + 1;
                        float d = MaxChannelDiff(pr, pg, pb, r[ni], g[ni], b[ni]);
                        if (d > maxDiff) maxDiff = d;
                        avgR += r[ni]; avgG += g[ni]; avgB += b[ni];
                        neighborCount++;
                    }
                    // Up
                    if (y > 0)
                    {
                        int ni = idx - w;
                        float d = MaxChannelDiff(pr, pg, pb, r[ni], g[ni], b[ni]);
                        if (d > maxDiff) maxDiff = d;
                        avgR += r[ni]; avgG += g[ni]; avgB += b[ni];
                        neighborCount++;
                    }
                    // Down
                    if (y < h - 1)
                    {
                        int ni = idx + w;
                        float d = MaxChannelDiff(pr, pg, pb, r[ni], g[ni], b[ni]);
                        if (d > maxDiff) maxDiff = d;
                        avgR += r[ni]; avgG += g[ni]; avgB += b[ni];
                        neighborCount++;
                    }

                    if (maxDiff > maxGradient)
                    {
                        // Blend toward local average — strength proportional to overshoot
                        float blend = Math.Min((maxDiff - maxGradient) / maxGradient, 0.5f);
                        avgR /= neighborCount;
                        avgG /= neighborCount;
                        avgB /= neighborCount;
                        newR[idx] = pr * (1 - blend) + avgR * blend;
                        newG[idx] = pg * (1 - blend) + avgG * blend;
                        newB[idx] = pb * (1 - blend) + avgB * blend;
                        changedThisPass++;
                    }
                }
            }

            r = newR;
            g = newG;
            b = newB;

            if (changedThisPass > 0) anyChanged = true;
            if (changedThisPass == 0) break; // converged
        }

        if (anyChanged)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new SKColor(
                    (byte)Math.Clamp((int)(r[i] + 0.5f), 0, 255),
                    (byte)Math.Clamp((int)(g[i] + 0.5f), 0, 255),
                    (byte)Math.Clamp((int)(b[i] + 0.5f), 0, 255),
                    a[i]);
            }
            bitmap.Pixels = pixels;
        }

        return anyChanged;
    }

    /// <summary>Max single-channel difference between two RGB pixels.</summary>
    private static float MaxChannelDiff(float r1, float g1, float b1, float r2, float g2, float b2)
    {
        float dr = MathF.Abs(r1 - r2);
        float dg = MathF.Abs(g1 - g2);
        float db = MathF.Abs(b1 - b2);
        return MathF.Max(dr, MathF.Max(dg, db));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static (int width, int height) GetGenerationSize(TextureRole role, TextureSlotRequest slot)
    {
        return role switch
        {
            TextureRole.Atlas => (512, 512),
            TextureRole.Ribbon => (512, 256),
            TextureRole.Ring => (512, 512),
            TextureRole.Glow => (256, 256),
            TextureRole.Bloom => (512, 512),
            TextureRole.Shape => (512, 512),
            _ => (512, 512)
        };
    }

    /// <summary>
    /// Session 25: Phase-aware replacement path builder.
    /// Guarantees no filename collisions across phases by encoding
    /// spell abbreviation + phase code + slot index in the filename.
    ///
    /// Format: SPELLS\CS_{spellAbbrev}_{phaseCode}_{slotIndex}.BLP
    /// Minimum unique part: 6 chars (fits tightest 21-byte vanilla filenames)
    /// </summary>
    public static string BuildReplacementPath(string safeSpellName, int textureIndex, int maxByteLength, string phase)
    {
        string prefix = "SPELLS\\CS_";
        string suffix = ".BLP";
        int overhead = prefix.Length + suffix.Length + 1; // +1 for null terminator
        int available = maxByteLength - overhead;

        // Phase code (1 char)
        char phaseCode = GetPhaseCode(phase);

        // Spell abbreviation — use as many chars as budget allows, minimum 2
        // Budget: available - 4 (for "_X_N" where X=phase, N=slot digit)
        int abbrevBudget = available - 4; // underscore + phase + underscore + digit
        if (abbrevBudget < 2) abbrevBudget = 2; // absolute minimum

        string spellAbbrev = MakeSpellAbbreviation(safeSpellName, abbrevBudget);

        // Build the unique part: "{abbrev}_{phase}_{slot}"
        string uniquePart = $"{spellAbbrev}_{phaseCode}_{textureIndex}";

        // Safety check: if somehow too long, truncate abbreviation
        if (uniquePart.Length > available)
        {
            int excess = uniquePart.Length - available;
            int newAbbrevLen = Math.Max(1, spellAbbrev.Length - excess);
            spellAbbrev = spellAbbrev[..newAbbrevLen];
            uniquePart = $"{spellAbbrev}_{phaseCode}_{textureIndex}";
        }

        return $"{prefix}{uniquePart}{suffix}";
    }

    /// <summary>
    /// Session 25 OVERLOAD: backward-compatible signature (no phase).
    /// Uses phase code 'X' (unknown) — still prevents collisions with
    /// phase-aware callers but should only be used as a fallback.
    /// </summary>
    public static string BuildReplacementPath(string safeSpellName, int textureIndex, int maxByteLength)
    {
        return BuildReplacementPath(safeSpellName, textureIndex, maxByteLength, "unknown");
    }

    /// <summary>
    /// Map phase name to a single-character code.
    /// Codes are chosen to be mnemonic and avoid ambiguity.
    /// </summary>
    private static char GetPhaseCode(string phase)
    {
        // Normalize: strip anything after backslash (e.g. "precast_Spells\\Fire_..." → "precast")
        // and take the base phase name before any underscore-qualified suffixes
        string normalized = phase.ToLowerInvariant();

        // Handle the doubled-phase format from trace (e.g. "cast_Spells\\Fire_Cast_Hand")
        if (normalized.Contains('\\'))
            normalized = normalized[..normalized.IndexOf('\\')].TrimEnd('_');

        return normalized switch
        {
            "precast" => 'P',
            "cast" => 'C',
            "impact" => 'I',
            "missile" => 'M',
            "area" => 'A',
            "state" => 'S',
            "target" => 'T',
            "channel" => 'H',
            _ => 'X'  // unknown/fallback
        };
    }

    /// <summary>
    /// Generate a short abbreviation from a spell name.
    /// For CamelCase names, takes the first letter of each word (up to maxLen).
    /// For single words, takes the first maxLen characters.
    /// </summary>
    private static string MakeSpellAbbreviation(string name, int maxLen)
    {
        if (string.IsNullOrEmpty(name))
            return "XX";

        // Extract CamelCase word starts
        var starts = new List<char>();
        for (int i = 0; i < name.Length; i++)
        {
            if (i == 0 && char.IsLetterOrDigit(name[i]))
                starts.Add(char.ToUpperInvariant(name[i]));
            else if (char.IsUpper(name[i]) && i > 0 && !char.IsUpper(name[i - 1]))
                starts.Add(name[i]);
            else if (i > 0 && name[i - 1] == '_' && char.IsLetterOrDigit(name[i]))
                starts.Add(char.ToUpperInvariant(name[i]));
        }

        string abbrev;
        if (starts.Count >= 2)
        {
            // CamelCase: use word initials
            abbrev = new string(starts.Take(maxLen).ToArray());
        }
        else
        {
            // Single word: use first N chars
            var clean = new string(name.Where(char.IsLetterOrDigit).ToArray());
            abbrev = clean.Length <= maxLen ? clean : clean[..maxLen];
            abbrev = abbrev.ToUpperInvariant();
        }

        // Pad to at least 2 chars
        while (abbrev.Length < 2)
            abbrev += "X";

        return abbrev.Length <= maxLen ? abbrev : abbrev[..maxLen];
    }


    private static string SanitizeName(string name)
    {
        return new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SESSION 29: TEXTURE REPROCESSING — Skip ComfyUI, Re-run Pipeline Only
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Session 29: Reprocess an existing PNG through the full post-processing pipeline
    /// without calling ComfyUI. Returns BLP bytes ready for MPQ packaging.
    /// Pipeline: resize → gradient clamp → brightness floor → debug PNG save → lum-alpha → encode
    /// </summary>
    public byte[]? ReprocessTexture(
        string pngPath, int blpWidth, int blpHeight, bool useDxt1,
        string role, string? recipeDensity = null,
        float? floorOverride = null, float? kneeOverride = null,
        int gridSize = 4, string? debugPngDir = null, string? debugFilename = null)
    {
        if (!File.Exists(pngPath))
        {
            _logger.LogWarning("ReprocessTexture: PNG not found: {Path}", pngPath);
            return null;
        }

        TextureRole roleEnum = ResolveRecipeRole(role);
        TextureDensity density = (recipeDensity ?? "").ToLowerInvariant() == "fullcoverage"
            ? TextureDensity.FullCoverage : TextureDensity.CenteredShape;

        using var resizedBitmap = _blpWriter.ResizePngToBitmap(pngPath, blpWidth, blpHeight);
        if (resizedBitmap == null) return null;

        // Gradient clamping
        try { ApplyGradientClamping(resizedBitmap, maxGradient: 35, passes: 20); }
        catch { /* non-fatal */ }

        // Brightness floor mask
        string maskMode;
        if (density == TextureDensity.FullCoverage && roleEnum == TextureRole.Shape)
            maskMode = "content_gentle";
        else if (roleEnum == TextureRole.Ribbon)
            maskMode = "none";
        else if (roleEnum == TextureRole.Atlas)
            maskMode = "atlas";
        else
            maskMode = "content";

        float floorPercent = floorOverride ?? roleEnum switch
        {
            TextureRole.Glow => 0.08f,
            TextureRole.Bloom => 0.08f,
            TextureRole.Ring => 0.10f,
            TextureRole.Atlas => 0.12f,
            TextureRole.Shape => 0.15f,
            _ => 0.12f
        };
        float kneeWidth = kneeOverride ?? 0.08f;

        switch (maskMode)
        {
            case "atlas":
                _blpWriter.ApplyAtlasBrightnessFloorMask(resizedBitmap, gridSize, floorPercent, kneeWidth);
                break;
            case "content":
                _blpWriter.ApplyBrightnessFloorMask(resizedBitmap, floorPercent, kneeWidth);
                break;
            case "content_gentle":
                _blpWriter.ApplyBrightnessFloorMask(resizedBitmap, 0.05f, 0.05f);
                break;
        }

        // Debug PNG save (Session 29 — Priority 2)
        if (!string.IsNullOrEmpty(debugPngDir) && !string.IsNullOrEmpty(debugFilename))
        {
            try
            {
                Directory.CreateDirectory(debugPngDir);
                using var debugStream = File.OpenWrite(Path.Combine(debugPngDir, $"{debugFilename}_processed.png"));
                debugStream.SetLength(0);
                resizedBitmap.Encode(debugStream, SkiaSharp.SKEncodedImageFormat.Png, 100);
            }
            catch { /* non-fatal */ }
        }

        // Luminance → alpha (DXT3 only)
        bool skipAlpha = useDxt1
            || (density == TextureDensity.FullCoverage && roleEnum == TextureRole.Shape);
        if (!skipAlpha)
            _blpWriter.ApplyLuminanceAlphaPixels(resizedBitmap);

        return _blpWriter.EncodeBitmapToBlp(resizedBitmap, useDxt1);
    }

    /// <summary>
    /// Session 29: Batch reprocess all textures in a manifest.
    /// </summary>
    public Dictionary<int, byte[]> ReprocessManifest(
        List<TextureCacheEntry> manifest, string texDir, VanillaBlpService vanillaBlps,
        float? globalFloorOverride = null, float? globalKneeOverride = null)
    {
        var results = new Dictionary<int, byte[]>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Get all PNGs for fuzzy matching
        var allPngs = Directory.Exists(texDir)
            ? Directory.GetFiles(texDir, "*.png").Select(Path.GetFileName).ToList()
            : new List<string?>();

        string safeName = Path.GetFileName(texDir); // e.g. "ThunderBall"

        for (int i = 0; i < manifest.Count; i++)
        {
            var entry = manifest[i];
            if (!seen.Add(entry.BlpFilename)) continue;

            // Try exact match first
            string exactPng = Path.ChangeExtension(entry.BlpFilename, ".png");
            string pngPath = Path.Combine(texDir, exactPng);

            // Fuzzy match if exact fails
            if (!File.Exists(pngPath))
            {
                string prefix = $"tex_{safeName}_{entry.Phase}_{entry.TextureIndex}_";
                string prefixNoPhase = $"tex_{safeName}_{entry.TextureIndex}_";
                var match = allPngs.FirstOrDefault(p =>
                    p != null && (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                               || p.StartsWith(prefixNoPhase, StringComparison.OrdinalIgnoreCase))
                    && p.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    pngPath = Path.Combine(texDir, match);
            }

            if (!File.Exists(pngPath)) continue;

            var vanillaInfo = vanillaBlps.GetBlpInfo(entry.OriginalFilename);
            int blpW = vanillaInfo?.Width ?? 64, blpH = vanillaInfo?.Height ?? 64;
            bool useDxt1 = vanillaInfo != null && vanillaInfo.AlphaDepth == 0;

            string? recipeDensity = ClassifyDensity(entry.OriginalFilename) == TextureDensity.FullCoverage
                ? "FullCoverage" : null;

            int gs = 4;
            string upper = entry.OriginalFilename.ToUpperInvariant();
            if (upper.Contains("8X8")) gs = 8;
            else if (upper.Contains("16X16")) gs = 16;

            byte[]? blpBytes = ReprocessTexture(pngPath, blpW, blpH, useDxt1,
                entry.Role, recipeDensity, globalFloorOverride, globalKneeOverride,
                gs, debugPngDir: texDir,
                debugFilename: Path.GetFileNameWithoutExtension(entry.BlpFilename));

            if (blpBytes != null)
            {
                results[i] = blpBytes;
                _logger.LogInformation("ReprocessManifest: {Blp} → {Bytes}b ({W}×{H} {Fmt})",
                    entry.BlpFilename, blpBytes.Length, blpW, blpH, useDxt1 ? "DXT1" : "DXT3");
            }
        }
        return results;
    }

    /// <summary>
    /// Session 29: Reprocess a single texture from a SpellTuningPreset TextureTuning entry.
    /// </summary>
    public byte[]? ReprocessFromTuning(
        TextureTuning tuning, string spellName, string comfyOutputDir,
        VanillaBlpService vanillaBlps, string? vanillaFilename,
        string? debugPngDir = null)
    {
        string safeName = new string(spellName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

        // Resolve PNG path
        string pngPath;
        if (tuning.SourcePng.StartsWith("comfyui_output/", StringComparison.OrdinalIgnoreCase))
        {
            pngPath = Path.Combine(Path.GetDirectoryName(comfyOutputDir) ?? comfyOutputDir,
                tuning.SourcePng);
        }
        else
        {
            string texDir = Path.Combine(Path.GetDirectoryName(comfyOutputDir) ?? "",
                "..", "custom", safeName);
            pngPath = Path.Combine(texDir, tuning.SourcePng);
        }

        if (!File.Exists(pngPath))
        {
            _logger.LogWarning("ReprocessFromTuning: PNG not found: {Path}", pngPath);
            return null;
        }

        int blpW = 64, blpH = 64;
        bool useDxt1 = false;
        if (!string.IsNullOrEmpty(vanillaFilename))
        {
            var info = vanillaBlps.GetBlpInfo(vanillaFilename);
            if (info != null) { blpW = info.Width; blpH = info.Height; useDxt1 = info.AlphaDepth == 0; }
        }

        return ReprocessTexture(pngPath, blpW, blpH, useDxt1,
            tuning.Role, tuning.Density, tuning.FloorPercent, tuning.KneeWidth,
            debugPngDir: debugPngDir,
            debugFilename: $"tuning_slot{tuning.SlotIndex}");
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Session 21: RUNTIME TRACE DTO
// Captures every decision point during texture generation.
// Written to {SpellName}_trace.json by PatchController.
// ═══════════════════════════════════════════════════════════════════════════

public class TextureSlotTrace
{
    public int SlotIndex { get; set; }
    public string Phase { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public int OriginalFilenameLength { get; set; }
    public string Timestamp { get; set; } = "";

    // ── TRACE 1: What arrived on the TextureSlotRequest ──
    public string? Input_RecipeRole { get; set; }
    public string? Input_RecipeJob { get; set; }
    public string? Input_RecipeDensity { get; set; }
    public string? Input_RecipeVignette { get; set; }
    public float? Input_RecipeVignetteInner { get; set; }
    public float? Input_RecipeVignetteOuter { get; set; }
    public int? Input_RecipeGridSize { get; set; }
    public byte Input_BlendMode { get; set; }
    public string? Input_RoleOverride { get; set; }
    public string? Input_CustomPrompt { get; set; }

    // ── Session 22: Vanilla BLP reference ──
    public string? Input_VanillaFormat { get; set; }
    public int Input_VanillaWidth { get; set; }
    public int Input_VanillaHeight { get; set; }
    public byte Input_VanillaAlphaDepth { get; set; }

    // ── TRACE 2: hasRecipe evaluation ──
    public bool Decision_HasRecipe { get; set; }
    public bool Decision_RecipeRoleWasNull { get; set; }
    public bool Decision_RecipeRoleWasEmpty { get; set; }
    public string Decision_RecipeRoleRawValue { get; set; } = "";

    // ── TRACE 3: Role/Density resolution ──
    public string Decision_Path { get; set; } = "";  // "RECIPE" or "HEURISTIC"
    public string Decision_ResolvedRole { get; set; } = "";
    public string Decision_ResolvedDensity { get; set; } = "";
    // Recipe path extras
    public string? Decision_ResolveRecipeRoleInput { get; set; }
    public string? Decision_ResolveRecipeRoleOutput { get; set; }
    // Heuristic path extras
    public string? Decision_HeuristicInput { get; set; }
    public bool? Decision_HadRoleOverride { get; set; }
    public string? Decision_RoleOverrideValue { get; set; }

    // ── TRACE 4: Prompt building ──
    public string Prompt_Source { get; set; } = "";  // "CUSTOM_PROMPT", "THEME", "DEFAULT"
    public string? Prompt_ThemeName { get; set; }
    public string? Prompt_RoleUsedForLookup { get; set; }
    public string? Prompt_DensityUsedForLookup { get; set; }
    public bool? Prompt_UsedBodyPrompt { get; set; }
    public bool? Prompt_HadRolePromptEntry { get; set; }
    public string Prompt_Final { get; set; } = "";
    public bool Prompt_OllamaRefined { get; set; }
    public string? Prompt_AfterOllama { get; set; }

    // ── TRACE 5: ComfyUI ──
    public bool ComfyUI_Success { get; set; }
    public string? ComfyUI_OutputPath { get; set; }

    // ── TRACE 6: Masking decision (Session 28: was vignette, now brightness floor) ──
    public string? Vignette_Mode { get; set; }       // "none", "content", "atlas"
    public float Vignette_Inner { get; set; }         // Session 28: repurposed as floorPercent
    public float Vignette_Outer { get; set; }         // Session 28: repurposed as kneeWidth
    public bool Vignette_RecipeProvided { get; set; }
    public bool Vignette_IsAdditive { get; set; }

    // ── TRACE 6b: Post-processing decisions (Session 21 fix) ──
    public bool PostProcess_SkippedLuminanceAlpha { get; set; }
    public bool PostProcess_GradientClamped { get; set; }
    public string? PostProcess_BlpEncoder { get; set; }

    // ── TRACE 7: Final output ──
    public string Output_GenerationSize { get; set; } = "";
    public string Output_Filename { get; set; } = "";
    public string? Output_ReplacementPath { get; set; }
    public int Output_BlpSize { get; set; }
    public string Output_Role { get; set; } = "";
    public string Output_Density { get; set; } = "";
    public string? Output_Error { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════
// DTOs — Session 19 updates + Session 21 trace list
// ═══════════════════════════════════════════════════════════════════════════

public class TextureTheme
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public Dictionary<SpellTextureService.TextureRole, string> RolePrompts { get; set; } = new();
    public string? BodyPrompt { get; set; }
}

public class TextureGenerationRequest
{
    public string SpellName { get; set; } = "";
    public string? ThemeKey { get; set; }
    public List<TextureSlotRequest> TextureSlots { get; set; } = new();
}

public class TextureSlotRequest
{
    // === Existing fields ===
    public int Index { get; set; }
    public string OriginalFilename { get; set; } = "";
    public int OriginalFilenameLength { get; set; }
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }

    // === Session 22: Vanilla BLP reference specs ===

    /// <summary>Vanilla BLP format: "DXT1", "DXT3", etc. Read from rawblps.</summary>
    public string? VanillaFormat { get; set; }

    /// <summary>Vanilla BLP alpha depth: 0=no alpha, 8=full alpha.</summary>
    public byte VanillaAlphaDepth { get; set; }

    /// <summary>Vanilla BLP alpha type: 0=DXT1, 1=DXT3.</summary>
    public byte VanillaAlphaType { get; set; }

    public SpellTextureService.TextureRole? RoleOverride { get; set; }
    public string? CustomPrompt { get; set; }
    public bool UseOllamaRefinement { get; set; } = false;
    public byte BlendMode { get; set; } = 4;

    // === Session 19: Recipe fields ===

    /// <summary>Phase name: "missile", "cast_leftHand", "impact_base", etc.</summary>
    public string Phase { get; set; } = "";

    /// <summary>Recipe-defined role: "Atlas", "Body", "Glow", "Ring", "Ribbon", "Bloom", "Shape".</summary>
    public string? RecipeRole { get; set; }

    /// <summary>Recipe-defined job: "Trail", "Core", "Mass", "Streak", etc.</summary>
    public string? RecipeJob { get; set; }

    /// <summary>Recipe-defined density: "FullCoverage" or "CenteredShape".</summary>
    public string? RecipeDensity { get; set; }

    /// <summary>Recipe-defined vignette mode: "rgb", "alpha", or "none".</summary>
    public string? RecipeVignette { get; set; }

    /// <summary>Recipe-defined vignette inner radius. Null = use per-role default.</summary>
    public float? RecipeVignetteInner { get; set; }

    /// <summary>Recipe-defined vignette outer radius. Null = use 0.90 default.</summary>
    public float? RecipeVignetteOuter { get; set; }

    /// <summary>Recipe-defined grid size for atlas textures. Null = auto-detect from filename.</summary>
    public int? RecipeGridSize { get; set; }
}

public class TextureGenerationResult
{
    public bool Success { get; set; }
    public string SpellName { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public List<GeneratedTexture> Textures { get; set; } = new();
    public List<TextureSlotTrace> Traces { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class GeneratedTexture
{
    public int TextureIndex { get; set; }
    public SpellTextureService.TextureRole Role { get; set; }
    public string Prompt { get; set; } = "";
    public string PngPath { get; set; } = "";
    public byte[] BlpBytes { get; set; } = Array.Empty<byte>();
    public string ReplacementMpqPath { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}