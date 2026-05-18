using Dapper;
using MangosSuperUI.Models;
using SkiaSharp;
using System.Text;
using System.Text.Json;

namespace MangosSuperUI.Services;

/// <summary>
/// AI-powered item retexturing pipeline with persistent storage.
///
/// All retextures are saved to the `custom_item_retexture` table in vmangos_admin.
/// patch-M.MPQ is rebuilt from ALL stored retextures every time, ensuring
/// the patch always contains every custom item texture (same pattern as
/// SpellCreator's unified patch rebuild).
///
/// Pipeline:
///   1. Ollama crafts a Flux prompt from user's style direction
///   2. ComfyUI (Flux) generates replacement texture (txt2img or img2img)
///   3. Result resized to vanilla dimensions, encoded to BLP
///   4. M2 cloned with patched texture path
///   5. Everything saved to DB (BLP + M2 as BLOBs)
///   6. RebuildPatchM() reads ALL retextures from DB, builds unified patch-M.MPQ
///      with all custom BLPs, M2s, and a single patched ItemDisplayInfo.dbc
///   7. patch-M.MPQ deployed to WoW client Data folder
/// </summary>
public class ItemRetextureService
{
    private readonly ComfyUIDispatcher _comfy;
    private readonly MpqReaderService _mpq;
    private readonly BlpWriterService _blpWriter;
    private readonly ItemTextureService _itemTextures;
    private readonly DbcService _dbc;
    private readonly ConnectionFactory _db;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<ItemRetextureService> _logger;
    private readonly HttpClient _http;

    private readonly string _dbcPath;
    private readonly string _ollamaUrl;
    private readonly string _ollamaModel;
    private readonly string _clipModel2;

    private static readonly TimeSpan OllamaTimeout = TimeSpan.FromSeconds(20);

    private const uint CUSTOM_DISPLAY_BASE = 60000;

    public ItemRetextureService(
        ComfyUIDispatcher comfy,
        MpqReaderService mpq,
        BlpWriterService blpWriter,
        ItemTextureService itemTextures,
        DbcService dbc,
        ConnectionFactory db,
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<ItemRetextureService> logger)
    {
        _comfy = comfy;
        _mpq = mpq;
        _blpWriter = blpWriter;
        _itemTextures = itemTextures;
        _dbc = dbc;
        _db = db;
        _env = env;
        _config = config;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

        _dbcPath = config["Vmangos:DbcPath"]
            ?? "/home/wowvmangos/vmangos/run/data/5875/dbc";

        var ollamaBase = (config["SpellCreator:Ollama:BaseUrl"] ?? "").TrimEnd('/');
        _ollamaUrl = string.IsNullOrEmpty(ollamaBase) ? "" : $"{ollamaBase}/api/generate";
        _ollamaModel = config["SpellCreator:Ollama:Model"] ?? "";
        _clipModel2 = config["SpellCreator:ComfyUI:ClipModel2"] ?? "";
    }

    // ═══════════════════════════════════════════════════════════════════
    // DB SCHEMA INIT
    // ═══════════════════════════════════════════════════════════════════

    public async Task EnsureTableAsync()
    {
        using var conn = _db.Admin();
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS custom_item_retexture (
                id INT AUTO_INCREMENT PRIMARY KEY,
                display_id INT UNSIGNED NOT NULL,
                new_display_id INT UNSIGNED NOT NULL,
                item_name VARCHAR(255) NOT NULL DEFAULT '',
                texture_filename VARCHAR(255) NOT NULL DEFAULT '',
                texture_mpq_path VARCHAR(512) NOT NULL DEFAULT '',
                custom_blp_mpq_path VARCHAR(512) NOT NULL DEFAULT '',
                custom_m2_mpq_path VARCHAR(512) NOT NULL DEFAULT '',
                custom_blp LONGBLOB,
                custom_m2 LONGBLOB,
                prompt TEXT,
                style_direction VARCHAR(512) NOT NULL DEFAULT '',
                created_at DATETIME DEFAULT NOW()
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
    }

    // ═══════════════════════════════════════════════════════════════════
    // PUBLIC API — GENERATE RETEXTURE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<RetextureResult> RetextureAsync(RetextureRequest request, CancellationToken ct = default)
    {
        await EnsureTableAsync();
        var result = new RetextureResult { DisplayId = request.DisplayId };

        try
        {
            _logger.LogInformation(
                "Retexture: Starting for displayId {Id}, texture '{Tex}', style: {Style}, mode: {Mode}",
                request.DisplayId, request.OriginalBlpFilename, request.StyleDirection,
                request.ModifyExisting ? "img2img" : "txt2img");

            // ── Step 1: Read original texture metadata ──
            var texInfo = _itemTextures.GetTexturesForDisplay(request.DisplayId);
            if (texInfo == null)
            {
                result.Error = "Could not extract textures for this item";
                return result;
            }

            var targetTex = texInfo.Textures.FirstOrDefault(t =>
                t.MpqPath.Equals(request.OriginalMpqPath, StringComparison.OrdinalIgnoreCase)
                || t.Filename.Equals(request.OriginalBlpFilename, StringComparison.OrdinalIgnoreCase));

            if (targetTex == null)
            {
                targetTex = texInfo.Textures.FirstOrDefault();
                if (targetTex == null)
                {
                    result.Error = "No textures found for this model";
                    return result;
                }
            }

            int targetW = targetTex.Width > 0 ? targetTex.Width : 64;
            int targetH = targetTex.Height > 0 ? targetTex.Height : 64;
            bool useDxt1 = targetTex.Format == "DXT1";
            result.OriginalWidth = targetW;
            result.OriginalHeight = targetH;
            result.OriginalFormat = targetTex.Format;

            // ── Step 2: Craft prompt via Ollama ──
            string prompt;
            if (!string.IsNullOrEmpty(request.CustomPrompt))
            {
                prompt = request.CustomPrompt;
            }
            else
            {
                prompt = await CraftTexturePromptAsync(
                    request.ItemName, request.StyleDirection, targetTex.Filename,
                    targetW, targetH, ct);
            }
            result.Prompt = prompt;
            _logger.LogInformation("Retexture: Prompt → \"{Prompt}\"", prompt);

            // ── Step 3: Generate with Flux via ComfyUI ──
            if (!await _comfy.IsAnyNodeOnlineAsync(ct))
            {
                result.Error = "No ComfyUI nodes available";
                return result;
            }

            int genW = 512;
            int genH = targetW == targetH ? 512 : (int)(512.0 * targetH / targetW);
            genH = Math.Max(64, (genH / 64) * 64);

            Dictionary<string, object> workflow;

            if (request.ModifyExisting && targetTex.HasPreview)
            {
                string previewPath = Path.Combine(_env.WebRootPath,
                    targetTex.PreviewPngPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(previewPath))
                {
                    workflow = BuildTextureWorkflow(prompt, request.DisplayId, genW, genH);
                }
                else
                {
                    string uploadPngPath = Path.Combine(
                        Path.GetTempPath(),
                        $"retex_src_{request.DisplayId}_{Path.GetFileName(previewPath)}");

                    using (var srcBitmap = SKBitmap.Decode(previewPath))
                    {
                        if (srcBitmap != null)
                        {
                            using var resized = srcBitmap.Resize(
                                new SKImageInfo(genW, genH, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                            if (resized != null)
                            {
                                using var outStream = File.Create(uploadPngPath);
                                resized.Encode(outStream, SKEncodedImageFormat.Png, 100);
                            }
                        }
                    }

                    string? uploadedName = await _comfy.UploadImageFileAsync(uploadPngPath, ct);
                    try { File.Delete(uploadPngPath); } catch { }

                    if (uploadedName == null)
                    {
                        workflow = BuildTextureWorkflow(prompt, request.DisplayId, genW, genH);
                    }
                    else
                    {
                        float denoise = Math.Clamp(request.DenoiseStrength, 0.1f, 1.0f);
                        workflow = BuildImg2ImgWorkflow(prompt, uploadedName, request.DisplayId,
                            genW, genH, denoise);
                        _logger.LogInformation("Retexture: img2img mode, denoise={Denoise}", denoise);
                    }
                }
            }
            else
            {
                workflow = BuildTextureWorkflow(prompt, request.DisplayId, genW, genH);
            }

            var outputDir = Path.Combine(_env.WebRootPath, "item_textures_cache", "retexture_output");
            string? generatedPng = await _comfy.GenerateAsync(
                workflow, $"retex_{request.DisplayId}", outputDir, ct);

            if (generatedPng == null)
            {
                result.Error = "Flux generation failed or timed out";
                return result;
            }

            // ── Step 4: Resize to vanilla dimensions ──
            var resizedDir = Path.Combine(_env.WebRootPath, "item_textures_cache", "retexture_resized");
            Directory.CreateDirectory(resizedDir);
            string resizedPng = Path.Combine(resizedDir,
                $"{request.DisplayId}_{Path.GetFileNameWithoutExtension(targetTex.Filename)}.png");

            using (var resizedBmp = _blpWriter.ResizePngToBitmap(generatedPng, targetW, targetH))
            {
                if (resizedBmp == null)
                {
                    result.Error = "Failed to resize generated texture";
                    return result;
                }
                using var outStream = File.Create(resizedPng);
                resizedBmp.Encode(outStream, SKEncodedImageFormat.Png, 100);
            }

            result.GeneratedPngPath = $"/item_textures_cache/retexture_resized/{Path.GetFileName(resizedPng)}";

            // ── Step 5: Encode to BLP ──
            using var blpBitmap = SKBitmap.Decode(resizedPng);
            if (blpBitmap == null)
            {
                result.Error = "Failed to decode resized PNG for BLP encoding";
                return result;
            }

            var blpBytes = _blpWriter.EncodeBitmapToBlp(blpBitmap, useDxt1);
            if (blpBytes == null)
            {
                result.Error = "BLP encoding failed";
                return result;
            }

            // ── Step 6: Build MPQ paths ──
            string customBlpName = $"Custom_{request.DisplayId}_{Path.GetFileNameWithoutExtension(targetTex.Filename)}.blp";
            string customBlpMpqPath = customBlpName;

            // ── Step 7: Clone M2 with patched texture path ──
            byte[]? m2Bytes = null;
            string? m2MpqPath = null;

            var modelInfo = texInfo.ModelName;
            if (!string.IsNullOrEmpty(modelInfo))
            {
                m2Bytes = FindAndExtractItemM2(modelInfo);
                if (m2Bytes != null)
                {
                    string m2BaseName = Path.GetFileNameWithoutExtension(modelInfo);
                    string m2Dir = GuessM2Directory(modelInfo);
                    m2MpqPath = $"{m2Dir}Custom_{request.DisplayId}_{m2BaseName}.m2";

                    // BLP in same directory as M2
                    customBlpMpqPath = $"{m2Dir}{customBlpName}";

                    // Patch texture reference in M2
                    var texEntries = M2TextureParser.ParseTextures(m2Bytes);
                    var matchingTex = texEntries.FirstOrDefault(t =>
                        !string.IsNullOrEmpty(t.Filename) &&
                        Path.GetFileName(t.Filename).Equals(targetTex.Filename,
                            StringComparison.OrdinalIgnoreCase));

                    if (matchingTex != null)
                    {
                        var replacements = new Dictionary<int, string>
                        {
                            [matchingTex.Index] = customBlpMpqPath
                        };
                        M2TextureParser.PatchTextureFilenames(m2Bytes, replacements);
                    }
                }
            }

            // ── Step 8: Allocate new displayId and save to DB ──
            uint newDisplayId = await AllocateDisplayIdAsync();

            using (var conn = _db.Admin())
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO custom_item_retexture
                        (display_id, new_display_id, item_name, texture_filename, texture_mpq_path,
                         custom_blp_mpq_path, custom_m2_mpq_path, custom_blp, custom_m2,
                         prompt, style_direction)
                    VALUES
                        (@DisplayId, @NewDisplayId, @ItemName, @TexFilename, @TexMpqPath,
                         @BlpMpqPath, @M2MpqPath, @BlpBytes, @M2Bytes,
                         @Prompt, @Style)",
                    new
                    {
                        DisplayId = request.DisplayId,
                        NewDisplayId = newDisplayId,
                        ItemName = request.ItemName,
                        TexFilename = targetTex.Filename,
                        TexMpqPath = targetTex.MpqPath,
                        BlpMpqPath = customBlpMpqPath,
                        M2MpqPath = m2MpqPath ?? "",
                        BlpBytes = blpBytes,
                        M2Bytes = m2Bytes,
                        Prompt = prompt,
                        Style = request.StyleDirection
                    });
            }

            _logger.LogInformation(
                "Retexture: Saved to DB — displayId {Old}→{New}, BLP: {Blp}, M2: {M2}",
                request.DisplayId, newDisplayId, customBlpMpqPath, m2MpqPath ?? "(none)");

            // Register in DbcService so SuperUI knows about this displayId immediately
            // Pass null for both model and texture — clones everything from source.
            // SuperUI serves custom textures from the DB via TryLoadCustomRetexture,
            // and finds the vanilla M2 via the original model name for GLB generation.
            _dbc.RegisterCustomDisplayEntry(newDisplayId, request.DisplayId, null, null);

            // ── Step 9: Rebuild patch-M.MPQ with ALL retextures ──
            var rebuildResult = await RebuildPatchMAsync();

            result.PatchMpqPath = rebuildResult.PatchWebPath;
            result.CustomBlpMpqPath = customBlpMpqPath;
            result.CustomM2MpqPath = m2MpqPath;
            result.NewDisplayId = newDisplayId;
            result.BlpSizeBytes = blpBytes.Length;
            result.Success = rebuildResult.Success;
            if (!rebuildResult.Success)
                result.Error = rebuildResult.Error;

            _itemTextures.InvalidateCache(request.DisplayId);

            _logger.LogInformation(
                "Retexture: Complete! displayId {Old}→{New}, total retextures in patch: {Count}",
                request.DisplayId, newDisplayId, rebuildResult.TotalEntries);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retexture: Failed for displayId {Id}", request.DisplayId);
            result.Error = ex.Message;
            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // REBUILD PATCH-M.MPQ — ALL RETEXTURES
    // ═══════════════════════════════════════════════════════════════════

    public async Task<PatchMRebuildResult> RebuildPatchMAsync()
    {
        var result = new PatchMRebuildResult();

        try
        {
            await EnsureTableAsync();

            List<dynamic> retextures;
            using (var conn = _db.Admin())
            {
                retextures = (await conn.QueryAsync(
                    @"SELECT id, display_id, new_display_id, item_name,
                             texture_filename, custom_blp_mpq_path, custom_m2_mpq_path,
                             custom_blp, custom_m2
                      FROM custom_item_retexture
                      ORDER BY id")).ToList();
            }

            if (retextures.Count == 0)
            {
                _logger.LogInformation("Retexture: No retextures in DB, skipping patch-M rebuild");
                result.Success = true;
                return result;
            }

            _logger.LogInformation("Retexture: Rebuilding patch-M.MPQ with {Count} retexture(s)",
                retextures.Count);

            // Load clean ItemDisplayInfo.dbc
            string dbcFile = Path.Combine(_dbcPath, "ItemDisplayInfo.dbc");
            if (!File.Exists(dbcFile))
            {
                result.Error = "ItemDisplayInfo.dbc not found";
                return result;
            }

            var displayDbc = DbcWriterService.ReadDbc(dbcFile);
            var mpqBuilder = new MpqBuilderService(null);

            foreach (var row in retextures)
            {
                uint origDisplayId = (uint)row.display_id;
                uint newDisplayId = (uint)row.new_display_id;
                string blpMpqPath = (string)row.custom_blp_mpq_path;
                string m2MpqPath = (string)row.custom_m2_mpq_path;
                byte[]? blpBytes = row.custom_blp as byte[];
                byte[]? m2Bytes = row.custom_m2 as byte[];

                if (blpBytes != null && !string.IsNullOrEmpty(blpMpqPath))
                    mpqBuilder.AddFile(blpMpqPath, blpBytes);

                if (m2Bytes != null && !string.IsNullOrEmpty(m2MpqPath))
                    mpqBuilder.AddFile(m2MpqPath, m2Bytes);

                // Clone ItemDisplayInfo.dbc entry
                var sourceRow = displayDbc.GetRow(origDisplayId);
                if (sourceRow != null && displayDbc.GetRow(newDisplayId) == null)
                {
                    displayDbc.CloneRow(origDisplayId, newDisplayId);

                    if (!string.IsNullOrEmpty(m2MpqPath))
                    {
                        string customM2Filename = Path.GetFileName(m2MpqPath);
                        uint nameOfs = displayDbc.AddString(customM2Filename);
                        displayDbc.PatchRow(newDisplayId, 1, nameOfs);
                    }

                    if (!string.IsNullOrEmpty(blpMpqPath))
                    {
                        string customTexName = Path.GetFileNameWithoutExtension(
                            Path.GetFileName(blpMpqPath));
                        uint texOfs = displayDbc.AddString(customTexName);
                        displayDbc.PatchRow(newDisplayId, 3, texOfs);
                    }
                }

                result.TotalEntries++;
            }

            mpqBuilder.AddFile(@"DBFilesClient\ItemDisplayInfo.dbc", displayDbc.Write());

            var patchDir = Path.Combine(_env.WebRootPath, "patches", "retexture");
            Directory.CreateDirectory(patchDir);
            string patchPath = Path.Combine(patchDir, "patch-M.MPQ");

            bool built = mpqBuilder.Build(patchPath);
            if (!built)
            {
                result.Error = "Failed to build patch-M.MPQ";
                return result;
            }

            // Deploy to WoW client Data folder
            string? clientDataPath = _config["Vmangos:ClientDataPath"]
                ?? _config["SpellCreator:ClientDataPath"];
            if (!string.IsNullOrEmpty(clientDataPath) && Directory.Exists(clientDataPath))
            {
                string deployedPath = Path.Combine(clientDataPath, "patch-M.MPQ");
                File.Copy(patchPath, deployedPath, overwrite: true);
                _logger.LogInformation("Retexture: Deployed patch-M.MPQ to {Path} ({Count} retextures)",
                    deployedPath, result.TotalEntries);
            }

            result.PatchWebPath = "/patches/retexture/patch-M.MPQ";
            result.Success = true;

            _logger.LogInformation(
                "Retexture: patch-M.MPQ rebuilt — {Count} retextures, {Files} files, {Size}KB",
                result.TotalEntries, mpqBuilder.FileCount,
                new FileInfo(patchPath).Length / 1024);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retexture: patch-M rebuild failed");
            result.Error = ex.Message;
            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // LIST / DELETE / STARTUP
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Call at startup to register all existing retextures in DbcService's
    /// in-memory caches so custom displayIds work immediately.
    /// </summary>
    public async Task LoadExistingRetexturesAsync()
    {
        await EnsureTableAsync();
        using var conn = _db.Admin();
        var rows = await conn.QueryAsync(
            @"SELECT display_id, new_display_id, custom_m2_mpq_path, custom_blp_mpq_path
              FROM custom_item_retexture");

        int count = 0;
        foreach (var row in rows)
        {
            uint origId = (uint)row.display_id;
            uint newId = (uint)row.new_display_id;

            // Register with original model/texture names (null = clone from source)
            _dbc.RegisterCustomDisplayEntry(newId, origId, null, null);
            count++;
        }

        if (count > 0)
            _logger.LogInformation("Retexture: Loaded {Count} existing retextures into DBC cache", count);
    }

    public async Task<List<RetextureEntry>> ListRetexturesAsync()
    {
        await EnsureTableAsync();
        using var conn = _db.Admin();
        return (await conn.QueryAsync<RetextureEntry>(
            @"SELECT id AS Id, display_id AS DisplayId, new_display_id AS NewDisplayId,
                     item_name AS ItemName, texture_filename AS TextureFilename,
                     custom_blp_mpq_path AS CustomBlpMpqPath,
                     custom_m2_mpq_path AS CustomM2MpqPath,
                     style_direction AS StyleDirection,
                     created_at AS CreatedAt
              FROM custom_item_retexture ORDER BY id DESC")).ToList();
    }

    public async Task<bool> DeleteRetextureAsync(int id)
    {
        using var conn = _db.Admin();
        int affected = await conn.ExecuteAsync(
            "DELETE FROM custom_item_retexture WHERE id = @Id", new { Id = id });

        if (affected > 0)
        {
            await RebuildPatchMAsync();
            return true;
        }
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    // DISPLAY ID ALLOCATION
    // ═══════════════════════════════════════════════════════════════════

    private async Task<uint> AllocateDisplayIdAsync()
    {
        using var conn = _db.Admin();
        var maxId = await conn.ExecuteScalarAsync<uint?>(
            "SELECT MAX(new_display_id) FROM custom_item_retexture");

        uint next = Math.Max((maxId ?? 0) + 1, CUSTOM_DISPLAY_BASE);

        string dbcFile = Path.Combine(_dbcPath, "ItemDisplayInfo.dbc");
        if (File.Exists(dbcFile))
        {
            var dbc = DbcWriterService.ReadDbc(dbcFile);
            uint dbcMax = dbc.GetMaxId();
            if (next <= dbcMax)
                next = dbcMax + 1;
        }

        return next;
    }

    // ═══════════════════════════════════════════════════════════════════
    // OLLAMA — Texture Prompt Crafting
    // ═══════════════════════════════════════════════════════════════════

    private async Task<string> CraftTexturePromptAsync(
        string itemName, string styleDirection, string textureFilename,
        int width, int height, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_ollamaUrl) || string.IsNullOrEmpty(_ollamaModel))
            return BuildDefaultTexturePrompt(itemName, styleDirection, textureFilename);

        string systemPrompt = $"""
            You translate a user's creative direction into an image prompt for FLUX (a text-to-image AI).

            CRITICAL CONTEXT: The output image will be used as a TEXTURE MAP — a flat 2D image that gets
            UV-wrapped onto a 3D model in World of Warcraft (vanilla, 2004 era). It is NOT a picture of an object.

            RULES FOR THE PROMPT YOU GENERATE:
            - MUST describe a FLAT, TOP-DOWN surface/material — like a photo of a tabletop, fabric swatch, or metal sheet
            - MUST include: "flat 2D texture, top-down view, no perspective, no 3D rendering, no shadows"
            - MUST describe colors, materials, and surface patterns
            - MUST include "hand-painted style, World of Warcraft vanilla aesthetic"
            - NEVER describe a 3D object, weapon, character, scene, or environment
            - NEVER include words like "hammer", "sword", "shield", "blade", "hilt" as physical objects
            - Instead translate those into MATERIAL descriptions: "hilt texture" → "wrapped leather with brass studs"
            - The texture is {width}x{height} pixels

            Respond with ONLY the prompt text. No explanation. One paragraph.
            /no_think
            """;

        string userPrompt = $"Item: \"{itemName}\". Texture file: \"{textureFilename}\".";
        if (!string.IsNullOrEmpty(styleDirection))
            userPrompt += $" Creative direction: \"{styleDirection}\"";
        else
            userPrompt += " Generate a texture that fits this item's theme.";

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = _ollamaModel,
                prompt = userPrompt,
                system = systemPrompt,
                stream = false,
                options = new { temperature = 0.7, num_predict = 150 }
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(OllamaTimeout);

            var resp = await _http.PostAsync(_ollamaUrl,
                new StringContent(body, Encoding.UTF8, "application/json"), cts.Token);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            string? raw = doc.RootElement.GetProperty("response").GetString()?.Trim();

            if (!string.IsNullOrWhiteSpace(raw))
            {
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
            _logger.LogWarning(ex, "Retexture: Ollama prompt crafting failed, using default");
        }

        return BuildDefaultTexturePrompt(itemName, styleDirection, textureFilename);
    }

    private static string BuildDefaultTexturePrompt(string itemName, string style, string filename)
    {
        string material = "metal";
        string fLower = filename.ToLowerInvariant();
        if (fLower.Contains("wood") || fLower.Contains("staff") || fLower.Contains("bow"))
            material = "weathered wood grain with carved runes";
        else if (fLower.Contains("leather") || fLower.Contains("quiver"))
            material = "tooled leather with stitching";
        else if (fLower.Contains("cloth") || fLower.Contains("robe"))
            material = "woven fabric with embroidered pattern";
        else if (fLower.Contains("hammer") || fLower.Contains("mace"))
            material = "forged dark iron with metallic sheen";
        else if (fLower.Contains("sword") || fLower.Contains("blade"))
            material = "polished steel with etched patterns";

        string styleStr = string.IsNullOrEmpty(style) ? $"fantasy medieval {material}" : $"{style}, {material}";

        return $"Flat 2D texture map, top-down view, {styleStr}, " +
               "hand-painted style, World of Warcraft vanilla 2004 aesthetic, " +
               "no perspective, no 3D rendering, no shadows, no objects, " +
               "seamless tileable surface material, fill entire frame with material detail";
    }

    // ═══════════════════════════════════════════════════════════════════
    // COMFYUI WORKFLOWS
    // ═══════════════════════════════════════════════════════════════════

    private Dictionary<string, object> BuildTextureWorkflow(string prompt, uint displayId, int width, int height)
    {
        return new Dictionary<string, object>
        {
            ["1"] = new Dictionary<string, object> { ["class_type"] = "UnetLoaderGGUF", ["inputs"] = new Dictionary<string, object> { ["unet_name"] = "flux1-dev-Q5_K_S.gguf" } },
            ["2"] = new Dictionary<string, object> { ["class_type"] = "DualCLIPLoader", ["inputs"] = new Dictionary<string, object> { ["clip_name1"] = "clip_l.safetensors", ["clip_name2"] = _clipModel2, ["type"] = "flux" } },
            ["3"] = new Dictionary<string, object> { ["class_type"] = "CLIPTextEncode", ["inputs"] = new Dictionary<string, object> { ["text"] = prompt, ["clip"] = new object[] { "2", 0 } } },
            ["5"] = new Dictionary<string, object> { ["class_type"] = "EmptySD3LatentImage", ["inputs"] = new Dictionary<string, object> { ["width"] = width, ["height"] = height, ["batch_size"] = 1 } },
            ["6"] = new Dictionary<string, object> { ["class_type"] = "KSampler", ["inputs"] = new Dictionary<string, object> { ["model"] = new object[] { "1", 0 }, ["positive"] = new object[] { "3", 0 }, ["negative"] = new object[] { "3", 0 }, ["latent_image"] = new object[] { "5", 0 }, ["seed"] = Random.Shared.Next(), ["steps"] = 25, ["cfg"] = 1.0, ["sampler_name"] = "euler", ["scheduler"] = "simple", ["denoise"] = 1.0 } },
            ["7"] = new Dictionary<string, object> { ["class_type"] = "VAEDecode", ["inputs"] = new Dictionary<string, object> { ["samples"] = new object[] { "6", 0 }, ["vae"] = new object[] { "8", 0 } } },
            ["8"] = new Dictionary<string, object> { ["class_type"] = "VAELoader", ["inputs"] = new Dictionary<string, object> { ["vae_name"] = "ae.safetensors" } },
            ["9"] = new Dictionary<string, object> { ["class_type"] = "SaveImage", ["inputs"] = new Dictionary<string, object> { ["images"] = new object[] { "7", 0 }, ["filename_prefix"] = $"retexture_{displayId}" } }
        };
    }

    private Dictionary<string, object> BuildImg2ImgWorkflow(string prompt, string uploadedImageName, uint displayId, int width, int height, float denoise)
    {
        return new Dictionary<string, object>
        {
            ["1"] = new Dictionary<string, object> { ["class_type"] = "UnetLoaderGGUF", ["inputs"] = new Dictionary<string, object> { ["unet_name"] = "flux1-dev-Q5_K_S.gguf" } },
            ["2"] = new Dictionary<string, object> { ["class_type"] = "DualCLIPLoader", ["inputs"] = new Dictionary<string, object> { ["clip_name1"] = "clip_l.safetensors", ["clip_name2"] = _clipModel2, ["type"] = "flux" } },
            ["3"] = new Dictionary<string, object> { ["class_type"] = "CLIPTextEncode", ["inputs"] = new Dictionary<string, object> { ["text"] = prompt, ["clip"] = new object[] { "2", 0 } } },
            ["10"] = new Dictionary<string, object> { ["class_type"] = "LoadImage", ["inputs"] = new Dictionary<string, object> { ["image"] = uploadedImageName } },
            ["11"] = new Dictionary<string, object> { ["class_type"] = "ImageScale", ["inputs"] = new Dictionary<string, object> { ["image"] = new object[] { "10", 0 }, ["width"] = width, ["height"] = height, ["upscale_method"] = "lanczos", ["crop"] = "center" } },
            ["12"] = new Dictionary<string, object> { ["class_type"] = "VAEEncode", ["inputs"] = new Dictionary<string, object> { ["pixels"] = new object[] { "11", 0 }, ["vae"] = new object[] { "8", 0 } } },
            ["6"] = new Dictionary<string, object> { ["class_type"] = "KSampler", ["inputs"] = new Dictionary<string, object> { ["model"] = new object[] { "1", 0 }, ["positive"] = new object[] { "3", 0 }, ["negative"] = new object[] { "3", 0 }, ["latent_image"] = new object[] { "12", 0 }, ["seed"] = Random.Shared.Next(), ["steps"] = 25, ["cfg"] = 1.0, ["sampler_name"] = "euler", ["scheduler"] = "simple", ["denoise"] = denoise } },
            ["7"] = new Dictionary<string, object> { ["class_type"] = "VAEDecode", ["inputs"] = new Dictionary<string, object> { ["samples"] = new object[] { "6", 0 }, ["vae"] = new object[] { "8", 0 } } },
            ["8"] = new Dictionary<string, object> { ["class_type"] = "VAELoader", ["inputs"] = new Dictionary<string, object> { ["vae_name"] = "ae.safetensors" } },
            ["9"] = new Dictionary<string, object> { ["class_type"] = "SaveImage", ["inputs"] = new Dictionary<string, object> { ["images"] = new object[] { "7", 0 }, ["filename_prefix"] = $"retexture_i2i_{displayId}" } }
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // M2 HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static readonly string[] ItemModelPrefixes = new[]
    {
        @"Item\ObjectComponents\Weapon\", @"Item\ObjectComponents\Shield\",
        @"Item\ObjectComponents\Head\", @"Item\ObjectComponents\Shoulder\",
        @"Item\ObjectComponents\Quiver\", @"Item\ObjectComponents\Ammo\",
        @"Creature\", @"World\",
    };

    private byte[]? FindAndExtractItemM2(string modelName)
    {
        if (modelName.Contains('\\') || modelName.Contains('/'))
            return _mpq.ExtractModelFile(modelName);

        var baseName = Path.GetFileNameWithoutExtension(modelName);
        foreach (var prefix in ItemModelPrefixes)
        {
            var data = _mpq.ExtractModelFile(prefix + baseName + ".m2");
            if (data != null) return data;
            data = _mpq.ExtractModelFile(prefix + baseName + ".mdx");
            if (data != null) return data;
            data = _mpq.ExtractModelFile(prefix + baseName.ToLowerInvariant() + ".m2");
            if (data != null) return data;
        }
        return null;
    }

    private static string GuessM2Directory(string modelName)
    {
        if (modelName.Contains('\\') || modelName.Contains('/'))
        {
            string dir = Path.GetDirectoryName(modelName)?.Replace('/', '\\') ?? "";
            return string.IsNullOrEmpty(dir) ? "" : dir + "\\";
        }

        string lower = modelName.ToLowerInvariant();
        if (lower.Contains("sword") || lower.Contains("axe") || lower.Contains("mace") ||
            lower.Contains("dagger") || lower.Contains("hammer") || lower.Contains("staff") ||
            lower.Contains("bow") || lower.Contains("gun") || lower.Contains("polearm") ||
            lower.Contains("weapon"))
            return @"Item\ObjectComponents\Weapon\";
        if (lower.Contains("shield") || lower.Contains("buckler"))
            return @"Item\ObjectComponents\Shield\";
        if (lower.Contains("helm") || lower.Contains("head") || lower.Contains("crown"))
            return @"Item\ObjectComponents\Head\";
        if (lower.Contains("shoulder") || lower.Contains("pauldron"))
            return @"Item\ObjectComponents\Shoulder\";

        return @"Item\ObjectComponents\Weapon\";
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// DTOs
// ═══════════════════════════════════════════════════════════════════════════

public class RetextureRequest
{
    public uint DisplayId { get; set; }
    public string ItemName { get; set; } = "";
    public string OriginalBlpFilename { get; set; } = "";
    public string OriginalMpqPath { get; set; } = "";
    public string StyleDirection { get; set; } = "";
    public string? CustomPrompt { get; set; }
    public bool ModifyExisting { get; set; }
    public float DenoiseStrength { get; set; } = 0.5f;
}

public class RetextureResult
{
    public uint DisplayId { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Prompt { get; set; }
    public string? GeneratedPngPath { get; set; }
    public string? CustomBlpMpqPath { get; set; }
    public string? CustomM2MpqPath { get; set; }
    public string? PatchMpqPath { get; set; }
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public string? OriginalFormat { get; set; }
    public int BlpSizeBytes { get; set; }
    public uint NewDisplayId { get; set; }
}

public class PatchMRebuildResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? PatchWebPath { get; set; }
    public int TotalEntries { get; set; }
}

public class RetextureEntry
{
    public int Id { get; set; }
    public uint DisplayId { get; set; }
    public uint NewDisplayId { get; set; }
    public string ItemName { get; set; } = "";
    public string TextureFilename { get; set; } = "";
    public string CustomBlpMpqPath { get; set; } = "";
    public string CustomM2MpqPath { get; set; } = "";
    public string StyleDirection { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}