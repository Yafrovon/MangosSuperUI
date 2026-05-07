using System.Text;
using System.Text.Json;

namespace MangosSuperUI.Services;

/// <summary>
/// AI-powered spell icon generator using two local services:
///   1. Ollama — crafts the image generation prompt
///   2. ComfyUI (FLUX Q5 GGUF) — generates the actual 64×64 icon PNG
///
/// Session 23: ComfyUI generation now routes through ComfyUIDispatcher,
/// which manages a pool of ComfyUI nodes and picks the first free one.
///
/// Endpoints configurable via appsettings.json or server-config.json:
///   SpellCreator:ComfyUI:Nodes[]    (pool of ComfyUI instances)
///   SpellCreator:Ollama:BaseUrl     (Ollama API endpoint)
///   SpellCreator:Ollama:Model       (Ollama model tag)
///   SpellCreator:ComfyUI:ClipModel2 (T5 CLIP model filename)
///
/// Pipeline:
///   Spell name + school + description
///     → Ollama generates optimized FLUX prompt
///     → ComfyUIDispatcher routes to free node → FLUX workflow → PNG
///     → PNG saved to wwwroot/images/icons/custom/
///
/// Fallback: if no ComfyUI node is reachable, picks the best existing icon
/// from the extracted set (2,684 PNGs) using keyword matching.
/// </summary>
public class SpellIconService
{
    private readonly HttpClient _http;
    private readonly ILogger<SpellIconService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly ComfyUIDispatcher _comfyDispatcher;

    // Ollama — prompt generation (configurable via appsettings)
    private readonly string _ollamaUrl;
    private readonly string _ollamaModel;
    private readonly string _clipModel2;

    private static readonly TimeSpan OllamaTimeout = TimeSpan.FromSeconds(15);

    // Cached icon index
    private Dictionary<string, string> _iconIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public SpellIconService(
        IWebHostEnvironment env,
        ComfyUIDispatcher comfyDispatcher,
        IConfiguration config,
        ILogger<SpellIconService> logger)
    {
        _env = env;
        _comfyDispatcher = comfyDispatcher;
        _logger = logger;
        // Set a generous default timeout ONCE — per-request timeouts use CancellationTokenSource
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

        var ollamaBase = (config["SpellCreator:Ollama:BaseUrl"] ?? "").TrimEnd('/');
        _ollamaUrl = string.IsNullOrEmpty(ollamaBase) ? "" : $"{ollamaBase}/api/generate";
        _ollamaModel = config["SpellCreator:Ollama:Model"] ?? "";
        _clipModel2 = config["SpellCreator:ComfyUI:ClipModel2"] ?? "";
    }

    /// <summary>Index existing icons on first use.</summary>
    public void EnsureInitialized()
    {
        if (_initialized) return;
        var iconDir = Path.Combine(_env.WebRootPath, "images", "icons");
        if (Directory.Exists(iconDir))
        {
            foreach (var file in Directory.GetFiles(iconDir, "*.png", SearchOption.TopDirectoryOnly))
                _iconIndex[Path.GetFileNameWithoutExtension(file)] = file;
            _logger.LogInformation("SpellIconService: Indexed {Count} icons", _iconIndex.Count);
        }
        _initialized = true;
    }

    /// <summary>
    /// Generate (or select) an icon for a spell.
    /// Tries ComfyUI first; falls back to keyword-matching existing icons.
    /// </summary>
    public async Task<IconResult> GenerateIconAsync(string spellName, int school, string? description = null)
    {
        EnsureInitialized();

        // ── Try ComfyUI generation ──
        try
        {
            if (await IsComfyRunning())
            {
                string imagePrompt = await CraftImagePromptAsync(spellName, school, description);
                _logger.LogInformation("SpellIconService: Ollama prompt → \"{Prompt}\"", imagePrompt);

                string? pngPath = await GenerateWithComfyAsync(imagePrompt, spellName);
                if (pngPath != null)
                {
                    return new IconResult
                    {
                        Success = true,
                        IconPath = pngPath,
                        IconName = Path.GetFileNameWithoutExtension(pngPath),
                        Source = "comfyui-flux",
                        Prompt = imagePrompt
                    };
                }
            }
            else
            {
                _logger.LogInformation("SpellIconService: ComfyUI not reachable, falling back to icon picker");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SpellIconService: ComfyUI generation failed, falling back");
        }

        // ── Fallback: pick from existing icons ──
        string picked = PickExistingIcon(spellName, school);
        string? pickedPath = _iconIndex.TryGetValue(picked, out var p) ? p : null;

        return new IconResult
        {
            Success = pickedPath != null,
            IconPath = pickedPath,
            IconName = picked,
            Source = "existing",
            Prompt = null
        };
    }

    /// <summary>Search existing icons by keyword.</summary>
    public List<string> SearchIcons(string? query, int maxResults = 30)
    {
        EnsureInitialized();
        IEnumerable<string> icons = _iconIndex.Keys;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var terms = query.ToLower().Split(' ', '_', '-');
            icons = icons.Where(name => terms.All(t => name.ToLower().Contains(t)));
        }
        return icons.OrderBy(n => n).Take(maxResults).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // OLLAMA — Prompt Crafting
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> CraftImagePromptAsync(string spellName, int school, string? description)
    {
        string schoolName = school switch
        {
            0 => "Physical/Warrior",
            1 => "Holy/Light",
            2 => "Fire",
            3 => "Nature/Druid",
            4 => "Frost/Ice",
            5 => "Shadow/Void",
            6 => "Arcane",
            _ => "Magical"
        };

        string systemPrompt = """
            You generate image prompts for a text-to-image AI model (FLUX).
            The output must be a World of Warcraft spell icon: 64x64 pixels, square,
            hand-painted style matching vanilla WoW (2004-2006 era), no border,
            dark vignette corners, centered symbolic element.
            Respond with ONLY the prompt text. No explanation. One paragraph max.
            /no_think
            """;

        string userPrompt = $"Generate an image prompt for a WoW spell icon: \"{spellName}\", school: {schoolName}.";
        if (!string.IsNullOrEmpty(description))
            userPrompt += $" Spell description: {description}";

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = _ollamaModel,
                prompt = userPrompt,
                system = systemPrompt,
                stream = false,
                options = new { temperature = 0.7, num_predict = 100 }
            });

            using var cts = new CancellationTokenSource(OllamaTimeout);
            var resp = await _http.PostAsync(_ollamaUrl,
                new StringContent(body, Encoding.UTF8, "application/json"), cts.Token);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            string? raw = doc.RootElement.GetProperty("response").GetString()?.Trim();

            if (!string.IsNullOrWhiteSpace(raw))
            {
                // Strip <think> blocks
                var thinkStart = raw.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (thinkStart >= 0)
                {
                    var thinkEnd = raw.IndexOf("</think>", thinkStart, StringComparison.OrdinalIgnoreCase);
                    if (thinkEnd >= 0) raw = raw[(thinkEnd + 8)..].Trim();
                }
                return raw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SpellIconService: Ollama prompt crafting failed, using default prompt");
        }

        // Default prompt if Ollama fails
        return $"World of Warcraft spell icon, {schoolName.ToLower()} magic, hand-painted style, " +
               $"64x64 pixel art, gold ornate border, dark vignette, centered {spellName.ToLower()} symbol, " +
               "vanilla WoW 2004 aesthetic, game UI icon";
    }

    // ═══════════════════════════════════════════════════════════════
    // COMFYUI — Session 23: Routed through ComfyUIDispatcher
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Session 23: Check if any node in the dispatcher pool is reachable.</summary>
    private async Task<bool> IsComfyRunning()
    {
        return await _comfyDispatcher.IsAnyNodeOnlineAsync();
    }

    /// <summary>
    /// Session 23: Route through ComfyUIDispatcher. The dispatcher acquires a free
    /// node, queues the workflow, polls for result, downloads the image, and releases.
    /// </summary>
    private async Task<string?> GenerateWithComfyAsync(string prompt, string spellName)
    {
        string safeName = new string(spellName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrEmpty(safeName)) safeName = "custom_icon";

        var workflow = BuildFluxWorkflow(prompt, safeName);

        string outputDir = Path.Combine(_env.WebRootPath, "images", "textures", "comfyui_output");
        string? downloadedPath = await _comfyDispatcher.GenerateAsync(
            workflow, $"icon_{safeName}", outputDir);

        if (downloadedPath == null)
        {
            _logger.LogWarning("SpellIconService: ComfyUI generation failed for {Name}", safeName);
            return null;
        }

        // Move to custom icons directory
        var customDir = Path.Combine(_env.WebRootPath, "images", "icons", "custom");
        Directory.CreateDirectory(customDir);
        string outPath = Path.Combine(customDir, $"{safeName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
        File.Copy(downloadedPath, outPath, overwrite: true);

        _logger.LogInformation("SpellIconService: Generated icon saved to {Path}", outPath);
        return outPath;
    }

    /// <summary>
    /// Build a ComfyUI API workflow JSON for FLUX Q5 GGUF icon generation.
    /// </summary>
    private Dictionary<string, object> BuildFluxWorkflow(string prompt, string outputPrefix)
    {
        return new Dictionary<string, object>
        {
            ["1"] = new Dictionary<string, object>
            {
                ["class_type"] = "UnetLoaderGGUF",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["unet_name"] = "flux1-dev-Q5_K_S.gguf"
                }
            },
            ["2"] = new Dictionary<string, object>
            {
                ["class_type"] = "DualCLIPLoader",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["clip_name1"] = "clip_l.safetensors",
                    ["clip_name2"] = _clipModel2,
                    ["type"] = "flux"
                }
            },
            ["3"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["text"] = prompt,
                    ["clip"] = new object[] { "2", 0 }
                }
            },
            ["5"] = new Dictionary<string, object>
            {
                ["class_type"] = "EmptySD3LatentImage",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["width"] = 512,
                    ["height"] = 512,
                    ["batch_size"] = 1
                }
            },
            ["6"] = new Dictionary<string, object>
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["model"] = new object[] { "1", 0 },
                    ["positive"] = new object[] { "3", 0 },
                    ["negative"] = new object[] { "3", 0 },
                    ["latent_image"] = new object[] { "5", 0 },
                    ["seed"] = Random.Shared.Next(),
                    ["steps"] = 20,
                    ["cfg"] = 1.0,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "simple",
                    ["denoise"] = 1.0
                }
            },
            ["7"] = new Dictionary<string, object>
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["samples"] = new object[] { "6", 0 },
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
                    ["images"] = new object[] { "7", 0 },
                    ["filename_prefix"] = $"spell_icon_{outputPrefix}"
                }
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // FALLBACK — Existing Icon Picker
    // ═══════════════════════════════════════════════════════════════

    private string PickExistingIcon(string spellName, int school)
    {
        var nameWords = spellName.ToLower().Split(' ', '_', '-').Where(w => w.Length >= 3).ToList();

        var scored = _iconIndex.Keys
            .Select(icon => (name: icon, score: nameWords.Count(w => icon.ToLower().Contains(w))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ToList();

        if (scored.Count > 0) return scored[0].name;

        return school switch
        {
            0 => "Ability_Warrior_Sunder",
            1 => "Spell_Holy_HolyBolt",
            2 => "Spell_Fire_FlameBolt",
            3 => "Spell_Nature_Lightning",
            4 => "Spell_Frost_FrostBolt02",
            5 => "Spell_Shadow_ShadowBolt",
            6 => "Spell_Arcane_StarFire",
            _ => "INV_Misc_QuestionMark"
        };
    }
}

public class IconResult
{
    public bool Success { get; set; }
    public string? IconPath { get; set; }
    public string IconName { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Prompt { get; set; }
}