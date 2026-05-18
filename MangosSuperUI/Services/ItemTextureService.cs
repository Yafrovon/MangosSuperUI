using Dapper;
using MangosSuperUI.Models;
using System.Collections.Concurrent;
using System.Text;
using War3Net.Drawing.Blp;

namespace MangosSuperUI.Services;

/// <summary>
/// Extracts, decodes, and serves item model textures on demand.
///
/// Pipeline:
///   1. DbcService gives us displayId → model filenames (from ItemDisplayInfo.dbc)
///   2. MpqReaderService extracts the M2 binary from MPQ
///   3. M2Reader/M2TextureParser parses texture references from the M2
///   4. MpqReaderService extracts the BLP texture files
///   5. War3Net.Drawing.Blp decodes BLP → raw BGRA pixels
///   6. SkiaSharp encodes to PNG for web preview
///   7. Results cached in memory + on disk to avoid re-extraction
///
/// This replaces the old "check if GLB exists on disk" approach with
/// live extraction that works for ALL ~6000+ items, not just pre-extracted ones.
/// </summary>
public class ItemTextureService
{
    private readonly MpqReaderService _mpq;
    private readonly DbcService _dbc;
    private readonly BlpWriterService _blpWriter;
    private readonly VanillaBlpService _vanillaBlp;
    private readonly ConnectionFactory _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ItemTextureService> _logger;

    // Cache: displayId → extracted texture info
    private readonly ConcurrentDictionary<uint, ItemTextureInfo?> _cache = new();

    // Disk cache directory for decoded PNGs
    private string CacheDir => Path.Combine(_env.WebRootPath, "item_textures_cache");

    public ItemTextureService(
        MpqReaderService mpq,
        DbcService dbc,
        BlpWriterService blpWriter,
        VanillaBlpService vanillaBlp,
        ConnectionFactory db,
        IWebHostEnvironment env,
        ILogger<ItemTextureService> logger)
    {
        _mpq = mpq;
        _dbc = dbc;
        _blpWriter = blpWriter;
        _vanillaBlp = vanillaBlp;
        _db = db;
        _env = env;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get texture info for an item by displayId.
    /// Returns model metadata + decoded texture PNGs (cached on disk).
    /// </summary>
    public ItemTextureInfo? GetTexturesForDisplay(uint displayId)
    {
        if (_cache.TryGetValue(displayId, out var cached))
            return cached;

        var result = ExtractTextures(displayId);
        _cache[displayId] = result;
        return result;
    }

    /// <summary>
    /// Ensure a GLB file exists for the given displayId.
    /// Extracts M2 + BLPs from MPQ, converts to GLB via GlbWriter, caches on disk.
    /// Returns the web path to the GLB, or null if conversion fails.
    ///
    /// === Filename versioning ===
    /// The cache filename embeds the current RigidGlbVersion (e.g.
    /// "31506.v2.glb"). When GlbWriter changes, bumping the constant
    /// makes prior versions invisible to File.Exists, forcing
    /// regeneration. Stale files get swept at service startup.
    /// </summary>
    public string? EnsureGlb(uint displayId)
    {
        if (displayId == 0) return null;

        // Check if GLB already exists on disk (pre-extracted or previously generated)
        var glbDir = Path.Combine(_env.WebRootPath, "item_models");
        var naturalFilename = $"{displayId}.glb";
        var versionedFilename = CacheVersionRegistry.MakeVersioned(
            naturalFilename, CacheVersionRegistry.RigidGlbVersion);
        var glbPath = Path.Combine(glbDir, versionedFilename);
        var webPath = $"/item_models/{versionedFilename}";

        if (File.Exists(glbPath))
            return webPath;

        // Generate on demand
        try
        {
            var modelInfo = _dbc.GetItemModelInfo(displayId);
            if (modelInfo == null) return null;

            string? modelName = !string.IsNullOrEmpty(modelInfo.Value.ModelName1)
                ? modelInfo.Value.ModelName1
                : modelInfo.Value.ModelName2;
            if (string.IsNullOrEmpty(modelName)) return null;

            var m2Data = FindAndExtractItemM2(modelName);
            if (m2Data == null) return null;

            var m2Model = M2Reader.Parse(m2Data);
            if (m2Model == null || !m2Model.IsValid) return null;

            // Extract all textures referenced by the M2
            var textures = new Dictionary<int, byte[]>();

            // Textures embedded in M2 (filename refs)
            for (int i = 0; i < m2Model.Textures.Count; i++)
            {
                var texRef = m2Model.Textures[i];
                if (string.IsNullOrEmpty(texRef.Filename)) continue;

                var blpData = _mpq.ExtractFile(texRef.Filename);
                if (blpData == null)
                    blpData = _mpq.ExtractFile(texRef.Filename.ToLowerInvariant());
                if (blpData != null)
                    textures[i] = blpData;
            }

            // Also try DBC texture names for type-1 (skin) textures
            if (!string.IsNullOrEmpty(modelInfo.Value.TextureName1))
            {
                var blpData = FindItemBlp(modelInfo.Value.TextureName1, modelName);
                if (blpData != null)
                {
                    // Find first texture slot that's type 1 (body/skin) or empty
                    int slot = FindSkinTextureSlot(m2Model, textures);
                    if (slot >= 0)
                        textures[slot] = blpData;
                }
            }
            if (!string.IsNullOrEmpty(modelInfo.Value.TextureName2))
            {
                var blpData = FindItemBlp(modelInfo.Value.TextureName2, modelName);
                if (blpData != null)
                {
                    int slot = FindSkinTextureSlot2(m2Model, textures);
                    if (slot >= 0)
                        textures[slot] = blpData;
                }
            }

            if (textures.Count == 0)
            {
                _logger.LogDebug("ItemTexture: No textures for GLB, displayId {Id}", displayId);
                // Still try — model will render with fallback grey material
            }

            Directory.CreateDirectory(glbDir);
            bool ok = GlbWriter.SaveGlb(m2Model, textures, glbPath);

            if (ok)
            {
                _logger.LogInformation("ItemTexture: Generated GLB for displayId {Id} ({Model}, {Size}KB)",
                    displayId, modelName, new FileInfo(glbPath).Length / 1024);
                return webPath;
            }

            _logger.LogWarning("ItemTexture: GlbWriter failed for displayId {Id}", displayId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ItemTexture: GLB generation failed for displayId {Id}", displayId);
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ATTACHMENT GLBs (helm / shoulders) — Session L
    // ═══════════════════════════════════════════════════════════════════
    //
    // Helms and shoulders are NOT body-atlas items. They're standalone
    // rigid-body M2 models that mount under named bones via the M2
    // attachment system:
    //
    //   attachment ID 11 → Helm        (parented to Bone_54 = Head)
    //   attachment ID  5 → ShoulderL   (parented to Bone_56 on HumanMale)
    //   attachment ID  6 → ShoulderR   (parented to Bone_55 on HumanMale)
    //
    // Both render through the existing rigid GlbWriter pipeline, same as
    // weapons (Session D). The data difference vs weapons:
    //
    //   Helm:   ItemDisplayInfo.ModelName1 = helm M2 (e.g.
    //           "Helm_Plate_RaidPaladin_A_01.mdx"), ModelName2 = empty.
    //           TextureName1 = the BLP partial (e.g.
    //           "Helm_Plate_RaidPaladin_A_01Gold"), TextureName2 = empty.
    //
    //   Shoulder: ModelName1 = LEFT spaulder M2 (e.g.
    //           "LShoulder_Plate_RaidPaladin_A_01.mdx"),
    //           ModelName2 = RIGHT spaulder M2 (e.g.
    //           "RShoulder_Plate_RaidPaladin_A_01.mdx").
    //           Both textures usually identical, but we honor TextureName1
    //           for left and TextureName2 for right in case they differ.
    //
    // Why not reuse EnsureGlb?
    //   EnsureGlb has fallback logic ModelName1 ?? ModelName2 — fine for
    //   weapons (only ever one model) but for shoulders that fallback
    //   means we'd silently get the LEFT spaulder when asked for the
    //   right one. We need explicit per-slot entry points.
    //
    // Cache file layout:
    //   /item_models/{displayId}_helm.glb
    //   /item_models/{displayId}_lshoulder.glb
    //   /item_models/{displayId}_rshoulder.glb
    //
    // Separate suffixes from the body GLB ({displayId}.glb) so the
    // weapon-model cache and attachment-model cache live side-by-side
    // without collision.

    /// <summary>Which spaulder slot to extract for a shoulder displayId.</summary>
    public enum ShoulderSide { Left, Right }

    // Race code mapping for helm filename suffix resolution. Vanilla
    // 1.12 helm M2s live at:
    //
    //   Item\ObjectComponents\Head\<BaseName>_<RR><G>.m2
    //
    // where <RR> is the 2-char race code below and <G> is M or F. The
    // DBC ItemDisplayInfo.ModelName1 stores only "<BaseName>.mdx" (no
    // suffix) — the client appends the right suffix at runtime based on
    // the character it's rendering. We replicate that here.
    //
    // Discovered via MpqProbe on Helm_Plate_RaidPaladin_A_01 (Session L):
    //   Hu Human    Dw Dwarf    Gn Gnome   Ni NightElf
    //   Or Orc      Sc Scourge  Ta Tauren  Tr Troll
    //
    // Race naming matches what CharacterModelService.NormalizeRace
    // accepts ("Scourge" not "Undead" — vanilla MPQ folder convention).
    private static readonly Dictionary<string, string> HelmRaceCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Human"] = "Hu",
            ["Dwarf"] = "Dw",
            ["Gnome"] = "Gn",
            ["NightElf"] = "Ni",
            ["Orc"] = "Or",
            ["Scourge"] = "Sc",
            ["Undead"] = "Sc",   // alias, same MPQ folder convention
            ["Tauren"] = "Ta",
            ["Troll"] = "Tr",
        };

    /// <summary>
    /// Ensure a helm GLB exists for the given displayId + character race
    /// + gender. Returns the web URL, or null on any failure.
    ///
    /// Helms are race+gender-specific — the DBC stores only the base
    /// name "<...>_RaidPaladin_A_01.mdx" and the client appends
    /// "_HuM"/"_HuF"/"_DwM"/etc at runtime. The cached GLB is keyed by
    /// (displayId, race, gender) so the same helm worn by a human male
    /// and a dwarf female generate distinct files. See HelmRaceCodes
    /// for the race-code mapping.
    ///
    /// race / gender accept the same casings CharacterModelService does
    /// ("Human"/"Male", case-insensitive). Unknown races return null.
    /// </summary>
    public string? EnsureHelmGlb(uint displayId, string race, string gender)
    {
        if (displayId == 0) return null;
        if (string.IsNullOrEmpty(race) || string.IsNullOrEmpty(gender)) return null;
        if (!HelmRaceCodes.TryGetValue(race, out var raceCode)) return null;
        char genderCode =
            gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 'F' :
            gender.Equals("Male", StringComparison.OrdinalIgnoreCase) ? 'M' :
            '\0';
        if (genderCode == '\0') return null;

        var suffix = $"_{raceCode}{genderCode}";   // e.g. "_HuM"

        var glbDir = Path.Combine(_env.WebRootPath, "item_models");
        // Cache key includes the race-gender suffix AND the RigidGlb writer
        // version so (a) a human-male helm and a dwarf-female helm don't
        // collide, and (b) bumping the writer version invalidates all
        // prior helm GLBs without manual cleanup.
        var naturalFilename = $"{displayId}_helm{suffix}.glb";
        var versionedFilename = CacheVersionRegistry.MakeVersioned(
            naturalFilename, CacheVersionRegistry.RigidGlbVersion);
        var glbPath = Path.Combine(glbDir, versionedFilename);
        var webPath = $"/item_models/{versionedFilename}";

        if (File.Exists(glbPath))
            return webPath;

        var modelInfo = _dbc.GetItemModelInfo(displayId);
        if (modelInfo == null) return null;

        // Append the race-gender suffix to the DBC base name.
        // ModelName1 looks like "Helm_Plate_RaidPaladin_A_01.mdx" — strip
        // extension, add suffix, let BuildAttachmentGlb's
        // FindAndExtractItemM2 try .m2 / .mdx / cases.
        var baseName = modelInfo.Value.ModelName1 ?? "";
        if (string.IsNullOrEmpty(baseName)) return null;
        var withoutExt = Path.GetFileNameWithoutExtension(baseName);
        var resolvedModelName = withoutExt + suffix + ".m2";

        Directory.CreateDirectory(glbDir);
        bool ok = BuildAttachmentGlb(
            displayId,
            resolvedModelName,
            modelInfo.Value.TextureName1,
            glbPath,
            $"helm{suffix}");
        return ok ? webPath : null;
    }

    /// <summary>
    /// Ensure a shoulder (left or right) GLB exists for the given displayId.
    /// Returns the web URL, or null on failure.
    ///
    /// Left  → ModelName1 + TextureName1.
    /// Right → ModelName2 + TextureName2 (fall back to TextureName1 if
    ///         TextureName2 is empty, which is common — both spaulders
    ///         usually share the same texture).
    /// </summary>
    public string? EnsureShoulderGlb(uint displayId, ShoulderSide side)
    {
        if (displayId == 0) return null;

        var sideSuffix = side == ShoulderSide.Left ? "lshoulder" : "rshoulder";
        var glbDir = Path.Combine(_env.WebRootPath, "item_models");
        var naturalFilename = $"{displayId}_{sideSuffix}.glb";
        var versionedFilename = CacheVersionRegistry.MakeVersioned(
            naturalFilename, CacheVersionRegistry.RigidGlbVersion);
        var glbPath = Path.Combine(glbDir, versionedFilename);
        var webPath = $"/item_models/{versionedFilename}";

        if (File.Exists(glbPath))
            return webPath;

        var modelInfo = _dbc.GetItemModelInfo(displayId);
        if (modelInfo == null) return null;

        string? modelName;
        string? textureName;
        if (side == ShoulderSide.Left)
        {
            modelName = modelInfo.Value.ModelName1;
            textureName = modelInfo.Value.TextureName1;
        }
        else
        {
            modelName = modelInfo.Value.ModelName2;
            // If TextureName2 is empty, fall back to TextureName1 — both
            // spaulders share a texture in every observed vanilla case.
            textureName = !string.IsNullOrEmpty(modelInfo.Value.TextureName2)
                ? modelInfo.Value.TextureName2
                : modelInfo.Value.TextureName1;
        }

        Directory.CreateDirectory(glbDir);
        bool ok = BuildAttachmentGlb(
            displayId, modelName, textureName, glbPath, sideSuffix);
        return ok ? webPath : null;
    }

    /// <summary>
    /// Shared helm/shoulder GLB builder. Extracts the M2, applies any
    /// embedded textures, swaps in the DBC-supplied skin texture, writes
    /// the GLB via the rigid writer.
    ///
    /// Returns false on any step failing — caller maps that to a null URL.
    /// All errors are logged with context so the loop in EnsureHelmGlb /
    /// EnsureShoulderGlb stays terse.
    /// </summary>
    private bool BuildAttachmentGlb(
        uint displayId,
        string? modelName,
        string? textureName,
        string glbPath,
        string kindLabel)
    {
        if (string.IsNullOrEmpty(modelName))
        {
            _logger.LogDebug(
                "ItemTexture/Attachment: displayId {Id} {Kind} — empty modelName",
                displayId, kindLabel);
            return false;
        }

        try
        {
            // Same path search as weapons — Item\ObjectComponents\{Head,Shoulder,...}
            // is in ItemModelPrefixes, so a bare "Helm_..." or "LShoulder_..."
            // filename resolves correctly.
            var m2Data = FindAndExtractItemM2(modelName);
            if (m2Data == null)
            {
                _logger.LogWarning(
                    "ItemTexture/Attachment: M2 not found in MPQ for displayId {Id} {Kind} — modelName='{Name}'",
                    displayId, kindLabel, modelName);
                return false;
            }

            var m2Model = M2Reader.Parse(m2Data);
            if (m2Model == null || !m2Model.IsValid)
            {
                _logger.LogWarning(
                    "ItemTexture/Attachment: M2 parse failed for displayId {Id} {Kind} — modelName='{Name}'",
                    displayId, kindLabel, modelName);
                return false;
            }

            // ── Texture collection ──
            // Mirrors the EnsureGlb pattern: first apply any embedded-by-
            // filename textures from the M2's own texture array (these are
            // type-0, rare on character armor pieces but possible), then
            // overlay the DBC-supplied skin texture into the first type-1
            // slot (the "client supplies this" slot).
            var textures = new Dictionary<int, byte[]>();

            for (int i = 0; i < m2Model.Textures.Count; i++)
            {
                var texRef = m2Model.Textures[i];
                if (string.IsNullOrEmpty(texRef.Filename)) continue;

                var blpData = _mpq.ExtractFile(texRef.Filename)
                            ?? _mpq.ExtractFile(texRef.Filename.ToLowerInvariant());
                if (blpData != null) textures[i] = blpData;
            }

            if (!string.IsNullOrEmpty(textureName))
            {
                var blpData = FindItemBlp(textureName, modelName);
                if (blpData != null)
                {
                    int slot = FindSkinTextureSlot(m2Model, textures);
                    if (slot >= 0) textures[slot] = blpData;
                }
                else
                {
                    _logger.LogWarning(
                        "ItemTexture/Attachment: skin BLP not found for displayId {Id} {Kind} — textureName='{Name}' (model='{Model}')",
                        displayId, kindLabel, textureName, modelName);
                    // Continue anyway — GlbWriter will fall back to a grey
                    // material. The user will see geometry without skin,
                    // which is still a useful diagnostic outcome.
                }
            }

            // Attachment GLBs go in with doubleSided=true. Vanilla helm
            // and shoulder M2s frequently include single-sided thin
            // features (spaulder hanging flap, helm wings/horns) whose
            // authored winding faces the wrong way after our coordinate
            // flip — backface culling then makes them disappear. See the
            // doubleSided docstring on GlbWriter.SaveGlb.
            bool ok = GlbWriter.SaveGlb(m2Model, textures, glbPath, doubleSided: true);
            if (ok)
            {
                _logger.LogInformation(
                    "ItemTexture/Attachment: Generated displayId {Id} {Kind} GLB ({Model}, {Size}KB)",
                    displayId, kindLabel, modelName,
                    new FileInfo(glbPath).Length / 1024);
                return true;
            }

            _logger.LogWarning(
                "ItemTexture/Attachment: GlbWriter failed for displayId {Id} {Kind} (model='{Model}')",
                displayId, kindLabel, modelName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ItemTexture/Attachment: Exception generating displayId {Id} {Kind}",
                displayId, kindLabel);
            return false;
        }
    }

    /// <summary>
    /// Find a texture slot in the M2 that the DBC's TextureName should fill.
    ///
    /// Vanilla M2 texture types:
    ///   0  — filename-based (the M2 has a baked-in path)
    ///   1  — character skin   (race + gender → CharacterTextures path)
    ///   2  — item object skin (filled from ItemDisplayInfo.TextureName1, e.g.
    ///                          weapons, armor parts) ← SESSION N
    ///   11 — monster skin 1, etc.
    ///
    /// Previously we only matched Type==1. Vanilla weapons use Type==2 for
    /// their primary skin slot (verified empirically on Thunderfury's
    /// Sword_2H_Ashbringer02.mdx: slot 4 is Type=2 with empty filename,
    /// expected to be filled with Sword_2H_Ashbringer_A_01Blue.blp from the
    /// DBC). Without accepting Type==2, the slot was found by the
    /// "first-empty" fallback which sometimes worked by accident but
    /// regularly missed for items where the M2 has multiple empty slots in
    /// non-Type-2 positions.
    /// </summary>
    private static int FindSkinTextureSlot(M2Model m2, Dictionary<int, byte[]> existing)
    {
        // Prefer the proper Type 2 (item object skin) slot, then Type 1
        // (character skin) for backward compatibility, then any empty slot.
        for (int i = 0; i < m2.Textures.Count; i++)
        {
            if (m2.Textures[i].Type == 2 && !existing.ContainsKey(i))
                return i;
        }
        for (int i = 0; i < m2.Textures.Count; i++)
        {
            if (m2.Textures[i].Type == 1 && !existing.ContainsKey(i))
                return i;
        }
        // If no typed slot matched, use first empty
        for (int i = 0; i < m2.Textures.Count; i++)
        {
            if (!existing.ContainsKey(i))
                return i;
        }
        return m2.Textures.Count; // append
    }

    private static int FindSkinTextureSlot2(M2Model m2, Dictionary<int, byte[]> existing)
    {
        // Second skin texture — look for another Type 2 slot, then Type 1.
        // Some items use two object-skin slots (cloth + metal, weapons with
        // separate hilt/blade textures referenced via TextureName2).
        for (int i = 0; i < m2.Textures.Count; i++)
        {
            if (m2.Textures[i].Type == 2 && !existing.ContainsKey(i))
                return i;
        }
        int found = 0;
        for (int i = 0; i < m2.Textures.Count; i++)
        {
            if (m2.Textures[i].Type == 1 && !existing.ContainsKey(i))
            {
                found++;
                if (found == 2) return i;
            }
        }
        // Fallback: next unused after first
        for (int i = 0; i < m2.Textures.Count; i++)
        {
            if (!existing.ContainsKey(i))
                return i;
        }
        return m2.Textures.Count + 1;
    }

    /// <summary>Try to find a BLP for a DBC texture name in common item paths.</summary>
    private byte[]? FindItemBlp(string textureName, string modelName)
    {
        if (string.IsNullOrEmpty(textureName)) return null;

        string[] tryPaths = {
            $"Item\\ObjectComponents\\Weapon\\{textureName}.blp",
            $"Item\\ObjectComponents\\Shield\\{textureName}.blp",
            $"Item\\ObjectComponents\\Head\\{textureName}.blp",
            $"Item\\ObjectComponents\\Shoulder\\{textureName}.blp",
            $"Item\\ObjectComponents\\Quiver\\{textureName}.blp",
        };

        foreach (var path in tryPaths)
        {
            var data = _mpq.ExtractFile(path);
            if (data != null) return data;
        }

        return null;
    }

    /// <summary>
    /// Get the raw BLP bytes for a texture from MPQ, for retexture pipeline.
    /// </summary>
    public byte[]? GetRawBlp(string mpqPath)
    {
        return _mpq.ExtractFile(mpqPath);
    }

    /// <summary>
    /// Invalidate cache for a displayId (after retexture).
    /// </summary>
    public void InvalidateCache(uint displayId)
    {
        _cache.TryRemove(displayId, out _);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CUSTOM RETEXTURE — serve from DB instead of MPQ
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Check if this displayId is a custom retexture. If so, decode the custom BLP
    /// from the DB and return it as an ItemTextureInfo. Also includes the vanilla
    /// textures from the original displayId for reference.
    /// </summary>
    private ItemTextureInfo? TryLoadCustomRetexture(uint displayId)
    {
        try
        {
            using var conn = _db.Admin();
            var row = conn.QueryFirstOrDefault(
                @"SELECT display_id, new_display_id, item_name, texture_filename,
                         custom_blp_mpq_path, custom_m2_mpq_path, custom_blp
                  FROM custom_item_retexture
                  WHERE new_display_id = @Did
                  LIMIT 1",
                new { Did = displayId });

            if (row == null) return null;

            uint origDisplayId = (uint)row.display_id;
            string texFilename = (string)(row.texture_filename ?? "");
            string blpMpqPath = (string)(row.custom_blp_mpq_path ?? "");
            byte[]? customBlp = row.custom_blp as byte[];

            _logger.LogInformation(
                "ItemTexture: Loading custom retexture for displayId {New} (from {Orig})",
                displayId, origDisplayId);

            // Get the vanilla textures from the original displayId
            var vanillaInfo = ExtractVanillaTextures(origDisplayId);

            var textures = new List<ItemTextureEntry>();

            // Add the custom BLP as the primary texture
            if (customBlp != null && customBlp.Length > 0)
            {
                string pngCachePath = GetCachePngPath(displayId, 0, blpMpqPath);
                string webPath = GetWebPngPath(displayId, 0, blpMpqPath);

                int width = 0, height = 0;
                string format = "Custom";

                if (customBlp.Length >= 20 && customBlp[0] == 'B' && customBlp[1] == 'L')
                {
                    width = (int)BitConverter.ToUInt32(customBlp, 12);
                    height = (int)BitConverter.ToUInt32(customBlp, 16);
                    byte alphaType = customBlp[10];
                    format = customBlp[8] == 2 ? alphaType switch
                    {
                        0 => "DXT1",
                        1 => "DXT3",
                        7 => "DXT5",
                        _ => "DXT"
                    } : "Other";
                }

                // Decode custom BLP to PNG for preview
                if (!File.Exists(pngCachePath))
                {
                    try { DecodeBlpToPng(customBlp, pngCachePath); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ItemTexture: Failed to decode custom BLP for displayId {Id}", displayId);
                    }
                }

                textures.Add(new ItemTextureEntry
                {
                    Index = 0,
                    Filename = $"★ {Path.GetFileName(blpMpqPath)}",
                    MpqPath = blpMpqPath,
                    Width = width,
                    Height = height,
                    Format = format,
                    AlphaDepth = customBlp.Length >= 10 ? customBlp[9] : (byte)0,
                    BlpFileSize = customBlp.Length,
                    PreviewPngPath = webPath,
                    HasPreview = File.Exists(pngCachePath)
                });
            }

            // Add vanilla textures as reference (with original indices offset)
            if (vanillaInfo != null)
            {
                foreach (var vt in vanillaInfo.Textures)
                {
                    // Skip if it's the same texture we replaced
                    if (vt.Filename.Equals(texFilename, StringComparison.OrdinalIgnoreCase))
                        continue;

                    vt.Index = textures.Count;
                    textures.Add(vt);
                }
            }

            var modelName = vanillaInfo?.ModelName ?? "(custom)";

            return new ItemTextureInfo
            {
                DisplayId = displayId,
                ModelName = modelName,
                M2Size = vanillaInfo?.M2Size ?? 0,
                VertexCount = vanillaInfo?.VertexCount ?? 0,
                TriangleCount = vanillaInfo?.TriangleCount ?? 0,
                Textures = textures
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ItemTexture: Custom retexture check failed for displayId {Id}", displayId);
            return null;
        }
    }

    /// <summary>Extract vanilla textures without caching (used by custom retexture fallback).</summary>
    private ItemTextureInfo? ExtractVanillaTextures(uint displayId)
    {
        // Temporarily bypass cache to get vanilla textures
        if (_cache.TryGetValue(displayId, out var cached) && cached != null)
            return cached;

        // Save/restore to avoid polluting cache
        var origCache = _cache.TryGetValue(displayId, out var existing) ? existing : null;
        var result = ExtractTexturesFromMpq(displayId);
        if (origCache != null)
            _cache[displayId] = origCache;
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // EXTRACTION PIPELINE
    // ═══════════════════════════════════════════════════════════════════

    private ItemTextureInfo? ExtractTextures(uint displayId)
    {
        // Check if this is a custom retextured displayId — serve from DB
        var customResult = TryLoadCustomRetexture(displayId);
        if (customResult != null)
            return customResult;

        return ExtractTexturesFromMpq(displayId);
    }

    private ItemTextureInfo? ExtractTexturesFromMpq(uint displayId)
    {
        // Step 1: Get model paths from DBC
        var modelInfo = _dbc.GetItemModelInfo(displayId);
        if (modelInfo == null)
        {
            _logger.LogDebug("ItemTexture: No model info in DBC for displayId {Id}", displayId);
            return null;
        }

        // Try both model slots (main hand / off hand)
        string? modelName = !string.IsNullOrEmpty(modelInfo.Value.ModelName1)
            ? modelInfo.Value.ModelName1
            : modelInfo.Value.ModelName2;

        if (string.IsNullOrEmpty(modelName))
        {
            _logger.LogDebug("ItemTexture: DisplayId {Id} has no model name in DBC", displayId);
            return null;
        }

        // Step 2: Resolve model path and extract M2
        // ItemDisplayInfo stores bare model names like "Sword_1H_Short_02.mdx"
        // The actual path is under Item\ObjectComponents\<type>\
        var m2Data = FindAndExtractItemM2(modelName);
        if (m2Data == null)
        {
            _logger.LogDebug("ItemTexture: Could not extract M2 for {Model} (displayId {Id})",
                modelName, displayId);
            return null;
        }

        // Step 3: Parse M2 for texture references
        var m2Model = M2Reader.Parse(m2Data);
        var texEntries = M2TextureParser.ParseTextures(m2Data);

        // Collect textures from both parsers
        var textures = new List<ItemTextureEntry>();

        // M2TextureParser gives us the filename paths (better for patching)
        foreach (var tex in texEntries)
        {
            if (string.IsNullOrEmpty(tex.Filename)) continue;

            var entry = ExtractAndDecodeTexture(displayId, tex.Index, tex.Filename, modelInfo.Value);
            if (entry != null)
                textures.Add(entry);
        }

        // If M2TextureParser found nothing, try M2Reader's texture refs
        if (textures.Count == 0 && m2Model != null)
        {
            for (int i = 0; i < m2Model.Textures.Count; i++)
            {
                var texRef = m2Model.Textures[i];
                if (string.IsNullOrEmpty(texRef.Filename)) continue;

                var entry = ExtractAndDecodeTexture(displayId, i, texRef.Filename, modelInfo.Value);
                if (entry != null)
                    textures.Add(entry);
            }
        }

        // Also try the DBC texture names (m_modelTexture fields)
        // These are sometimes separate from what's embedded in the M2
        if (!string.IsNullOrEmpty(modelInfo.Value.TextureName1))
        {
            var dbcTex = TryExtractDbcTexture(displayId, modelInfo.Value.TextureName1,
                modelName, textures.Count);
            if (dbcTex != null && !textures.Any(t =>
                t.Filename.Equals(dbcTex.Filename, StringComparison.OrdinalIgnoreCase)))
                textures.Add(dbcTex);
        }
        if (!string.IsNullOrEmpty(modelInfo.Value.TextureName2))
        {
            var dbcTex = TryExtractDbcTexture(displayId, modelInfo.Value.TextureName2,
                modelName, textures.Count);
            if (dbcTex != null && !textures.Any(t =>
                t.Filename.Equals(dbcTex.Filename, StringComparison.OrdinalIgnoreCase)))
                textures.Add(dbcTex);
        }

        if (textures.Count == 0)
        {
            _logger.LogDebug("ItemTexture: No textures extracted for displayId {Id} ({Model})",
                displayId, modelName);
            return null;
        }

        var info = new ItemTextureInfo
        {
            DisplayId = displayId,
            ModelName = modelName,
            M2Size = m2Data.Length,
            VertexCount = m2Model?.Vertices.Count ?? 0,
            TriangleCount = m2Model != null ? m2Model.Indices.Count / 3 : 0,
            Textures = textures
        };

        _logger.LogInformation(
            "ItemTexture: displayId {Id} → {Model} ({Verts}v/{Tris}t), {TexCount} textures",
            displayId, modelName, info.VertexCount, info.TriangleCount, textures.Count);

        return info;
    }

    // ═══════════════════════════════════════════════════════════════════
    // M2 FILE RESOLUTION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Item M2 files live under Item\ObjectComponents\{Type}\ in the MPQ.
    /// The DBC only stores the bare filename (e.g. "Sword_1H_Short_02.mdx"),
    /// so we need to search the known subdirectories.
    /// </summary>
    private static readonly string[] ItemModelPrefixes = new[]
    {
        @"Item\ObjectComponents\Weapon\",
        @"Item\ObjectComponents\Shield\",
        @"Item\ObjectComponents\Head\",
        @"Item\ObjectComponents\Shoulder\",
        @"Item\ObjectComponents\Quiver\",
        @"Item\ObjectComponents\Ammo\",
        // Some items use creature or other paths
        @"Creature\",
        @"World\",
    };

    private byte[]? FindAndExtractItemM2(string modelName)
    {
        // If the model name already has a full path, try it directly
        if (modelName.Contains('\\') || modelName.Contains('/'))
        {
            return _mpq.ExtractModelFile(modelName);
        }

        // Strip extension for searching
        var baseName = Path.GetFileNameWithoutExtension(modelName);

        // Try each known prefix
        foreach (var prefix in ItemModelPrefixes)
        {
            var data = _mpq.ExtractModelFile(prefix + baseName + ".m2");
            if (data != null) return data;

            data = _mpq.ExtractModelFile(prefix + baseName + ".mdx");
            if (data != null) return data;

            // Try lowercase
            data = _mpq.ExtractModelFile(prefix + baseName.ToLowerInvariant() + ".m2");
            if (data != null) return data;
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // BLP EXTRACTION + DECODING
    // ═══════════════════════════════════════════════════════════════════

    private ItemTextureEntry? ExtractAndDecodeTexture(uint displayId, int texIndex,
        string blpPath, ItemModelDbc modelInfo)
    {
        // Extract the BLP from MPQ
        var blpData = _mpq.ExtractFile(blpPath);
        if (blpData == null)
        {
            // Try variations — sometimes paths have wrong casing
            blpData = _mpq.ExtractFile(blpPath.ToLowerInvariant());
            if (blpData == null)
            {
                _logger.LogDebug("ItemTexture: BLP not found in MPQ: {Path}", blpPath);
                return null;
            }
        }

        // Decode BLP → PNG and save to disk cache
        string pngCachePath = GetCachePngPath(displayId, texIndex, blpPath);
        string webPath = GetWebPngPath(displayId, texIndex, blpPath);

        int width = 0, height = 0;
        string format = "Unknown";
        byte alphaDepth = 0;

        try
        {
            // Read BLP header for metadata
            if (blpData.Length >= 20 && blpData[0] == 'B' && blpData[1] == 'L' &&
                blpData[2] == 'P' && blpData[3] == '2')
            {
                byte compression = blpData[8];
                alphaDepth = blpData[9];
                byte alphaType = blpData[10];
                width = (int)BitConverter.ToUInt32(blpData, 12);
                height = (int)BitConverter.ToUInt32(blpData, 16);

                format = compression switch
                {
                    2 => alphaType switch
                    {
                        0 => "DXT1",
                        1 => "DXT3",
                        7 => "DXT5",
                        _ => $"DXT({alphaType})"
                    },
                    1 => "Palettized",
                    _ => $"Unknown({compression})"
                };
            }

            // Decode to PNG if not already cached
            if (!File.Exists(pngCachePath))
            {
                DecodeBlpToPng(blpData, pngCachePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ItemTexture: Failed to decode BLP {Path}", blpPath);
            return null;
        }

        return new ItemTextureEntry
        {
            Index = texIndex,
            Filename = Path.GetFileName(blpPath),
            MpqPath = blpPath,
            Width = width,
            Height = height,
            Format = format,
            AlphaDepth = alphaDepth,
            BlpFileSize = blpData.Length,
            PreviewPngPath = webPath,
            HasPreview = File.Exists(pngCachePath)
        };
    }

    /// <summary>
    /// Try to extract a texture referenced by DBC m_modelTexture fields.
    /// These are bare texture names that need path resolution.
    /// </summary>
    private ItemTextureEntry? TryExtractDbcTexture(uint displayId, string textureName,
        string modelName, int texIndex)
    {
        if (string.IsNullOrEmpty(textureName)) return null;

        // The DBC texture name is usually just the filename without path or extension
        // Try common paths where item textures live
        var modelDir = Path.GetDirectoryName(modelName)?.Replace('/', '\\') ?? "";

        string[] tryPaths;
        if (!string.IsNullOrEmpty(modelDir))
        {
            tryPaths = new[]
            {
                $"{modelDir}\\{textureName}.blp",
                $"Item\\ObjectComponents\\Weapon\\{textureName}.blp",
                $"Item\\ObjectComponents\\Shield\\{textureName}.blp",
                $"Item\\ObjectComponents\\Head\\{textureName}.blp",
                $"Item\\ObjectComponents\\Shoulder\\{textureName}.blp",
            };
        }
        else
        {
            tryPaths = new[]
            {
                $"Item\\ObjectComponents\\Weapon\\{textureName}.blp",
                $"Item\\ObjectComponents\\Shield\\{textureName}.blp",
                $"Item\\ObjectComponents\\Head\\{textureName}.blp",
                $"Item\\ObjectComponents\\Shoulder\\{textureName}.blp",
            };
        }

        foreach (var path in tryPaths)
        {
            var blpData = _mpq.ExtractFile(path);
            if (blpData != null)
            {
                var entry = new ItemTextureEntry { MpqPath = path };

                // Read BLP header
                if (blpData.Length >= 20 && blpData[0] == 'B' && blpData[1] == 'L')
                {
                    entry.Width = (int)BitConverter.ToUInt32(blpData, 12);
                    entry.Height = (int)BitConverter.ToUInt32(blpData, 16);
                    byte alphaType = blpData[10];
                    entry.Format = blpData[8] == 2 ? alphaType switch
                    {
                        0 => "DXT1",
                        1 => "DXT3",
                        7 => "DXT5",
                        _ => "DXT"
                    } : "Other";
                    entry.AlphaDepth = blpData[9];
                    entry.BlpFileSize = blpData.Length;
                }

                entry.Index = texIndex;
                entry.Filename = $"{textureName}.blp";

                string pngCachePath = GetCachePngPath(displayId, texIndex, path);
                entry.PreviewPngPath = GetWebPngPath(displayId, texIndex, path);

                try
                {
                    if (!File.Exists(pngCachePath))
                        DecodeBlpToPng(blpData, pngCachePath);
                    entry.HasPreview = File.Exists(pngCachePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ItemTexture: Failed to decode DBC texture {Name}", textureName);
                }

                return entry;
            }
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // BLP → PNG DECODING (using War3Net.Drawing.Blp)
    // ═══════════════════════════════════════════════════════════════════

    private void DecodeBlpToPng(byte[] blpData, string outputPngPath)
    {
        var dir = Path.GetDirectoryName(outputPngPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var ms = new MemoryStream(blpData);
        var blpFile = new BlpFile(ms);
        var pixels = blpFile.GetPixels(0, out int w, out int h);

        // War3Net returns BGRA pixels — convert to SkiaSharp SKBitmap
        using var bitmap = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Bgra8888,
            SkiaSharp.SKAlphaType.Unpremul);

        // Pin and copy pixel data
        var bitmapPixels = bitmap.GetPixels();
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmapPixels, pixels.Length);
        bitmap.NotifyPixelsChanged();

        // Encode to PNG
        using var outStream = File.Create(outputPngPath);
        bitmap.Encode(outStream, SkiaSharp.SKEncodedImageFormat.Png, 100);

        _logger.LogDebug("ItemTexture: Decoded BLP → {Path} ({W}×{H})", outputPngPath, w, h);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CACHE PATHS
    // ═══════════════════════════════════════════════════════════════════

    private string GetCachePngPath(uint displayId, int texIndex, string blpPath)
    {
        var safeName = Path.GetFileNameWithoutExtension(blpPath)
            .ToLowerInvariant()
            .Replace('\\', '_').Replace('/', '_');
        return Path.Combine(CacheDir, $"{displayId}", $"tex{texIndex}_{safeName}.png");
    }

    private string GetWebPngPath(uint displayId, int texIndex, string blpPath)
    {
        var safeName = Path.GetFileNameWithoutExtension(blpPath)
            .ToLowerInvariant()
            .Replace('\\', '_').Replace('/', '_');
        return $"/item_textures_cache/{displayId}/tex{texIndex}_{safeName}.png";
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// DTOs
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>All texture data for an item's 3D model.</summary>
public class ItemTextureInfo
{
    public uint DisplayId { get; set; }
    public string ModelName { get; set; } = "";
    public int M2Size { get; set; }
    public int VertexCount { get; set; }
    public int TriangleCount { get; set; }
    public List<ItemTextureEntry> Textures { get; set; } = new();
}

/// <summary>A single texture from an item's M2 model.</summary>
public class ItemTextureEntry
{
    public int Index { get; set; }
    public string Filename { get; set; } = "";
    public string MpqPath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = "";
    public byte AlphaDepth { get; set; }
    public int BlpFileSize { get; set; }
    public string PreviewPngPath { get; set; } = "";
    public bool HasPreview { get; set; }
}

/// <summary>Model info from ItemDisplayInfo.dbc.</summary>
public struct ItemModelDbc
{
    public string ModelName1;
    public string ModelName2;
    public string TextureName1;
    public string TextureName2;

    // ── Session C: body atlas dressing ──
    // The 8 m_texture[] stringref fields (slots 0-7). Slots 0 and 1 are
    // always empty in vanilla; slots 2-7 are the body atlas paint texture
    // partial names (e.g. "Robe_C_01Blue_Chest_TU"). Maps to character
    // body atlas regions via SLOT_TO_REGION in region-rects.js.
    public string[] BodyTextures;

    // The 3 m_geosetGroup[] fields. Vanilla 1.12.1 has 3 (not 5 like later
    // expansions). Drives geoset variant selection per SLOT_RULES in
    // geoset-rules.js. Index meanings depend on the item's inventory_type.
    public int[] GeosetGroup;

    // ── Session L: helm hair/facial-hair hiding ──
    // ItemDisplayInfo fields [12] and [13]: m_helmetGeosetVis[0..1].
    // Vanilla docs are thin on the exact encoding. wowdev.wiki suggests
    // bitmasks against geoset groups but the bits-vs-direct-id question
    // hasn't been confirmed against 1.12 specifically. We parse and
    // surface the raw values; the dressing rule is being reverse-
    // engineered empirically.
    //
    //   HelmetGeosetVis1 — covers hair (cat 0 hair variants) per most refs
    //   HelmetGeosetVis2 — covers facial hair (beard, sideburns, moustache)
    //                      on bearded races (Dwarf male, NightElf male, etc).
    //                      Probably maps to cat 1 (facial) on those models.
    public uint HelmetGeosetVis1;
    public uint HelmetGeosetVis2;

    // ── Session N: item visual effects ──
    // ItemDisplayInfo field [22]: m_itemVisual. Indexes ItemVisuals.dbc,
    // which itself references up to 5 rows in ItemVisualEffects.dbc, each
    // pointing at a separate "effect M2" file (the model carrying the
    // particles, ribbons, and animated tracks). The weapon M2 itself does
    // NOT carry the lightning/glow geometry — vanilla 1.12 splits geometry
    // from visuals here. ~1.2% of ItemDisplayInfo rows have a non-zero
    // value; for most items this stays 0 and the client renders no effect.
    //
    // Confirmed via Session M discovery: Thunderfury (displayId 30606)
    // expects a non-zero ItemVisualId resolving to the lightning effects.
    public uint ItemVisualId;
}