using Dapper;
using MangosSuperUI.Models;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MangosSuperUI.Services.ArmorForge;

namespace MangosSuperUI.Services;

/// <summary>
/// Temporary GLB assets produced for an uncommitted model retexture preview.
/// Ordinary model items use <see cref="GlbUrl"/>. Shoulder items also expose
/// the two authored side models through <see cref="Attachments"/>; GlbUrl keeps
/// the existing preview-response gate compatible.
/// </summary>
public sealed class PreviewGlbAssets
{
    public string? GlbUrl { get; init; }
    public Dictionary<string, string> Attachments { get; init; } = new();
}

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
    public List<string> ShoulderFitErrors { get; } = new();
    public List<ShoulderFitSelection> ShoulderFitSelections { get; } = new();
    private readonly MpqReaderService _mpq;
    private readonly DbcService _dbc;
    private readonly BlpWriterService _blpWriter;
    private readonly VanillaBlpService _vanillaBlp;
    private readonly ConnectionFactory _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ItemTextureService> _logger;

    // Cache: displayId → extracted texture info
    private readonly ConcurrentDictionary<uint, ItemTextureInfo?> _cache = new();

    // Process-wide because ItemTextureService itself is scoped per request.
    // Serializes source capture, fingerprinting, and promotion of the canonical
    // GLB + sidecar across concurrent HTTP requests for the same display.
    private static readonly ConcurrentDictionary<uint, object> GlbBuildLocks = new();

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
    /// === Code + source versioning ===
    /// The canonical filename embeds RigidGlbVersion (e.g. "31506.v2.glb")
    /// for writer changes. A sibling .source file stores a SHA-256 over the
    /// resolved model identity, raw M2, and every bound texture slot. A missing
    /// or mismatched stamp regenerates the GLB even when the display ID did not
    /// change. The returned URL carries that fingerprint as a query token so a
    /// browser cannot reuse an older same-display response.
    /// </summary>
    public string? EnsureGlb(uint displayId)
    {
        if (displayId == 0) return null;

        // Lock before resolving any DB/DBC/MPQ input, not merely before the
        // final write. Otherwise an older request can capture generation A,
        // wait while a newer request publishes B, then overwrite B with A.
        object buildLock = GlbBuildLocks.GetOrAdd(displayId, static _ => new object());
        lock (buildLock)
            return EnsureGlbLocked(displayId);
    }

    /// <summary>Source capture, fingerprinting, validation, and promotion for
    /// one display. The caller holds that display's process-wide lock for the
    /// entire operation, so generations can only commit in capture order.</summary>
    private string? EnsureGlbLocked(uint displayId)
    {
        var glbDir = Path.Combine(_env.WebRootPath, "item_models");
        var naturalFilename = $"{displayId}.glb";
        var versionedFilename = CacheVersionRegistry.MakeVersioned(
            naturalFilename, CacheVersionRegistry.RigidGlbVersion);
        var glbPath = Path.Combine(glbDir, versionedFilename);
        var sourceFilename = CacheVersionRegistry.MakeVersioned(
            $"{displayId}.source", CacheVersionRegistry.RigidGlbVersion);
        var sourcePath = Path.Combine(glbDir, sourceFilename);

        // Generate on demand
        try
        {
            // MpqReaderService is a singleton and may have initialized before
            // the first patch-5 existed. Force an idempotent catalog refresh at
            // the display-asset boundary before resolving any source bytes.
            _mpq.RefreshLivePatches();

            // ── Check if this is a custom retexture (displayId 60000+) ──
            // If so, use the ORIGINAL displayId's M2/textures but swap in
            // the custom BLP from the DB. This makes the 3D viewer show
            // the retextured model instead of vanilla.
            var retexInfo = GetRetextureInfo(displayId);
            uint resolvedDisplayId = retexInfo?.OrigDisplayId ?? displayId;

            var modelInfo = _dbc.GetItemModelInfo(resolvedDisplayId);
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

            // ── Inject custom BLP from DB (replaces the vanilla texture
            //    that was retextured) ──
            if (retexInfo != null && retexInfo.CustomBlp != null)
            {
                // Find which texture slot the retexture replaced by matching
                // the original texture filename against the M2's texture refs
                // and the DBC texture names.
                int injectedSlot = -1;
                string retexFilename = retexInfo.OrigTexFilename;

                // First: check M2 embedded texture filenames
                for (int i = 0; i < m2Model.Textures.Count; i++)
                {
                    var texRef = m2Model.Textures[i];
                    if (!string.IsNullOrEmpty(texRef.Filename) &&
                        Path.GetFileName(texRef.Filename)
                            .Equals(retexFilename, StringComparison.OrdinalIgnoreCase))
                    {
                        injectedSlot = i;
                        break;
                    }
                }

                // Second: check DBC texture names (type-1 skin textures
                // that aren't stored by filename in the M2)
                if (injectedSlot < 0)
                {
                    string retexBase = Path.GetFileNameWithoutExtension(retexFilename);
                    if (!string.IsNullOrEmpty(modelInfo.Value.TextureName1) &&
                        modelInfo.Value.TextureName1.Equals(retexBase, StringComparison.OrdinalIgnoreCase))
                    {
                        injectedSlot = FindSkinTextureSlot(m2Model,
                            new Dictionary<int, byte[]>()); // find the canonical slot
                    }
                    else if (!string.IsNullOrEmpty(modelInfo.Value.TextureName2) &&
                             modelInfo.Value.TextureName2.Equals(retexBase, StringComparison.OrdinalIgnoreCase))
                    {
                        injectedSlot = FindSkinTextureSlot2(m2Model,
                            new Dictionary<int, byte[]>());
                    }
                }

                // Third: fallback — replace the first skin/object-skin slot
                if (injectedSlot < 0)
                    injectedSlot = FindSkinTextureSlot(m2Model, new Dictionary<int, byte[]>());

                if (injectedSlot >= 0)
                {
                    textures[injectedSlot] = retexInfo.CustomBlp;
                    _logger.LogInformation(
                        "ItemTexture/GLB: Injected custom BLP into slot {Slot} for retexture displayId {Id} (from {Orig})",
                        injectedSlot, displayId, retexInfo.OrigDisplayId);
                }
            }

            if (textures.Count == 0)
            {
                _logger.LogDebug("ItemTexture: No textures for GLB, displayId {Id}", displayId);
                // Still try — model will render with fallback grey material
            }

            string sourceFingerprint = ComputeGlbSourceFingerprint(
                displayId, resolvedDisplayId, modelInfo.Value, modelName,
                retexInfo, m2Data, textures);
            string webPath = $"/item_models/{versionedFilename}?source={sourceFingerprint}";

            if (GlbCacheMatchesSource(glbPath, sourcePath, sourceFingerprint))
                return webPath;

            Directory.CreateDirectory(glbDir);
            if (!WriteGlbCacheAtomically(
                    m2Model, textures, glbPath, sourcePath, sourceFingerprint,
                    ResolveVisualEffects(displayId, m2Model)))
            {
                _logger.LogWarning("ItemTexture: GlbWriter failed for displayId {Id}", displayId);
                return null;
            }

            _logger.LogInformation("ItemTexture: Generated GLB for displayId {Id} ({Model}, {Size}KB)",
                displayId, modelName, new FileInfo(glbPath).Length / 1024);
            return webPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ItemTexture: GLB generation failed for displayId {Id}", displayId);
            return null;
        }
    }

    private static string ComputeGlbSourceFingerprint(
        uint displayId,
        uint resolvedDisplayId,
        ItemModelDbc modelInfo,
        string selectedModelName,
        RetextureGlbInfo? retexture,
        byte[] m2Data,
        IReadOnlyDictionary<int, byte[]> textures)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Domain/version marker makes future changes to the fingerprint framing
        // an intentional cache invalidation rather than an accidental collision.
        AppendFingerprintString(hash, "ItemTextureService.EnsureGlb/source-v2");
        AppendFingerprintUInt32(hash, modelInfo.ItemVisualId);
        AppendFingerprintUInt32(hash, displayId);
        AppendFingerprintUInt32(hash, resolvedDisplayId);
        AppendFingerprintString(hash, selectedModelName);
        AppendFingerprintString(hash, modelInfo.ModelName1);
        AppendFingerprintString(hash, modelInfo.ModelName2);
        AppendFingerprintString(hash, modelInfo.TextureName1);
        AppendFingerprintString(hash, modelInfo.TextureName2);
        AppendFingerprintUInt32(hash, retexture?.OrigDisplayId ?? 0);
        AppendFingerprintString(hash, retexture?.OrigTexFilename);
        AppendFingerprintBytes(hash, m2Data);

        AppendFingerprintUInt32(hash, checked((uint)textures.Count));
        foreach (var texture in textures.OrderBy(pair => pair.Key))
        {
            AppendFingerprintInt32(hash, texture.Key);
            AppendFingerprintBytes(hash, texture.Value);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendFingerprintString(IncrementalHash hash, string? value)
    {
        AppendFingerprintBytes(hash, Encoding.UTF8.GetBytes(value ?? string.Empty));
    }

    private static void AppendFingerprintBytes(IncrementalHash hash, byte[] value)
    {
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, value.LongLength);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static void AppendFingerprintUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendFingerprintInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    /// <summary>
    /// The item's <c>ItemVisual</c>, resolved to loaded effect models mounted on its attachments.
    ///
    /// This channel carries enchant glows and many permanent weapon effects, and none of those
    /// separate effect models is in the item's own bytes, so
    /// without this an item decodes perfectly and still renders dead. Best-effort by design: an item
    /// with no visual, or a visual whose models are missing, simply gets no extra emitters.
    /// </summary>
    private IReadOnlyList<M2Fx.ItemVisualEffects.Effect>? ResolveVisualEffects(uint displayId, M2Model host)
    {
        try
        {
            var info = _dbc.GetItemModelInfo(displayId);
            uint visualId = info?.ItemVisualId ?? 0;
            if (visualId == 0) return null;

            var effects = M2Fx.ItemVisualEffects.Resolve(visualId, host,
                path => _mpq.ExtractFile(path) ?? _mpq.ExtractFile(path.ToLowerInvariant()));
            if (effects.Count == 0)
            {
                _logger.LogDebug("ItemTexture: displayId {Id} itemVisual {Visual} resolved to no usable effect model",
                    displayId, visualId);
                return null;
            }

            _logger.LogInformation("ItemTexture: displayId {Id} itemVisual {Visual} → {Count} effect model(s): {Models}",
                displayId, visualId, effects.Count, string.Join(", ", effects.Select(e => e.ModelPath)));
            return effects;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ItemTexture: item-visual resolution failed for displayId {Id}", displayId);
            return null;
        }
    }

    private static bool GlbCacheMatchesSource(
        string glbPath, string sourcePath, string expectedFingerprint)
    {
        if (!File.Exists(glbPath) || !File.Exists(sourcePath)) return false;

        try
        {
            string actual = File.ReadAllText(sourcePath).Trim();
            return string.Equals(actual, expectedFingerprint, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool WriteGlbCacheAtomically(
        M2Model m2Model,
        Dictionary<int, byte[]> textures,
        string glbPath,
        string sourcePath,
        string sourceFingerprint,
        IReadOnlyList<M2Fx.ItemVisualEffects.Effect>? visualEffects = null)
    {
        string nonce = Guid.NewGuid().ToString("N");
        string tempGlbPath = $"{glbPath}.{nonce}.tmp";
        string tempSourcePath = $"{sourcePath}.{nonce}.tmp";

        try
        {
            if (!GlbWriter.SaveGlb(m2Model, textures, tempGlbPath, doubleSided: false, visualEffects))
                return false;

            File.WriteAllText(tempSourcePath, sourceFingerprint, new UTF8Encoding(false));

            // Invalidate the old stamp before replacing the GLB, then promote
            // the new stamp last. Every interruption point therefore leaves a
            // missing stamp, never a new GLB blessed by the old fingerprint.
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            File.Move(tempGlbPath, glbPath, overwrite: true);
            File.Move(tempSourcePath, sourcePath, overwrite: true);
            return true;
        }
        finally
        {
            try { if (File.Exists(tempGlbPath)) File.Delete(tempGlbPath); } catch { }
            try { if (File.Exists(tempSourcePath)) File.Delete(tempSourcePath); } catch { }
        }
    }

    /// <summary>
    /// Build a TEMPORARY preview GLB for a staged retexture — used by the
    /// segmented-variation modal so a card can be shown in 3D (and equipped on
    /// the character) BEFORE anything is committed to the DB or patch-4.MPQ.
    ///
    /// Mirrors EnsureGlb's extraction (vanilla M2 + its textures) but injects a
    /// caller-supplied RECOLORED PNG (encoded to BLP at the vanilla slot's
    /// dimensions/format) into the resolved object-skin slot, then writes the
    /// GLB to a throwaway preview directory. NOTHING is persisted: no DB row, no
    /// patch rebuild, no versioned cache entry. The caller is responsible for
    /// deleting the returned file when the modal closes (or letting the periodic
    /// sweep collect it).
    ///
    /// Returns the web path to the temp GLB, or null on failure.
    /// </summary>
    /// <param name="displayId">The ORIGINAL vanilla displayId being retextured.</param>
    /// <param name="recoloredPngPath">Disk path to the already-rendered recolor PNG.</param>
    /// <summary>
    /// The geometry-sampled skin slots for a display's item model — the texture
    /// indices the render batches actually sample (batch → TextureLookup → Textures),
    /// minus overlay passes (spec/glow/env/reflect/smoke/skill), each with its raw M2
    /// filename. EMPTY filename = a Type-2 slot the DBC's TextureName1 fills; a NON-empty
    /// filename = a baked Type-0 skin. Callers recolor what the model actually renders
    /// instead of trusting a possibly-dead TextureName1 override.
    /// </summary>
    public List<(int Index, string Filename)> GetSampledSkinSlots(uint displayId)
    {
        var result = new List<(int, string)>();
        try
        {
            var modelInfo = _dbc.GetItemModelInfo(displayId);
            if (modelInfo == null) return result;
            string? modelName = !string.IsNullOrEmpty(modelInfo.Value.ModelName1)
                ? modelInfo.Value.ModelName1
                : modelInfo.Value.ModelName2;
            if (string.IsNullOrEmpty(modelName)) return result;
            var m2Data = FindAndExtractItemM2(modelName);
            if (m2Data == null) return result;
            var m2 = M2Reader.Parse(m2Data);
            if (m2 == null || !m2.IsValid) return result;
            foreach (int ti in GlbWriter.SampledTextureIndices(m2))
            {
                if (ti < 0 || ti >= m2.Textures.Count) continue;
                string up = (m2.Textures[ti].Filename ?? "").ToUpperInvariant();
                if (up.Contains("SPEC") || up.Contains("GLOW") || up.Contains("SMOKE") ||
                    up.Contains("ENV") || up.Contains("REFLECT") || up.Contains("SKILLACTIVATED"))
                    continue;
                result.Add((ti, m2.Textures[ti].Filename ?? ""));
            }
        }
        catch { /* best-effort diagnostics helper */ }
        return result;
    }

    /// <summary>
    /// Build every temporary GLB needed to preview a staged model retexture.
    ///
    /// Shoulder displays are a special case: ItemDisplayInfo carries two
    /// independently-authored M2s (ModelName1 = left, ModelName2 = right).
    /// Returning both avoids cloning/mirroring one spaulder in the browser,
    /// which is not geometrically equivalent and can point the clone inward.
    /// Other model items keep the historical single-GLB contract.
    /// </summary>
    public PreviewGlbAssets? BuildPreviewGlbs(uint displayId, string recoloredPngPath)
    {
        if (displayId == 0 || string.IsNullOrEmpty(recoloredPngPath) || !File.Exists(recoloredPngPath))
            return null;

        var modelInfo = _dbc.GetItemModelInfo(displayId);
        if (modelInfo == null) return null;

        var info = modelInfo.Value;
        bool isShoulder = IsShoulderModelName(info.ModelName1) ||
                          IsShoulderModelName(info.ModelName2);

        if (isShoulder)
        {
            var leftUrl = BuildPreviewGlbCore(
                displayId, recoloredPngPath,
                info.ModelName1, info.TextureName1,
                "lshoulder", doubleSided: true);

            string rightTexture = !string.IsNullOrEmpty(info.TextureName2)
                ? info.TextureName2
                : info.TextureName1;
            var rightUrl = BuildPreviewGlbCore(
                displayId, recoloredPngPath,
                info.ModelName2, rightTexture,
                "rshoulder", doubleSided: true);

            // A partial pair would leave the other side's previously-mounted
            // model in place, producing a mixed old/new preview. Fail the pair
            // atomically and remove whichever temporary half did build.
            if (leftUrl == null || rightUrl == null)
            {
                DeletePreviewGlb(leftUrl);
                DeletePreviewGlb(rightUrl);
                _logger.LogWarning(
                    "ItemTexture: incomplete shoulder preview pair for displayId {Id} (left={Left}, right={Right})",
                    displayId, leftUrl != null, rightUrl != null);
                return null;
            }

            return new PreviewGlbAssets
            {
                GlbUrl = leftUrl,
                Attachments = new Dictionary<string, string>
                {
                    ["shoulderLeft"] = leftUrl,
                    ["shoulderRight"] = rightUrl
                }
            };
        }

        string? modelName = !string.IsNullOrEmpty(info.ModelName1)
            ? info.ModelName1
            : info.ModelName2;
        string? textureName = !string.IsNullOrEmpty(info.ModelName1)
            ? info.TextureName1
            : (!string.IsNullOrEmpty(info.TextureName2) ? info.TextureName2 : info.TextureName1);

        var glbUrl = BuildPreviewGlbCore(
            displayId, recoloredPngPath, modelName, textureName,
            "model", doubleSided: false);
        return glbUrl == null ? null : new PreviewGlbAssets { GlbUrl = glbUrl };
    }

    private static bool IsShoulderModelName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        string stem = Path.GetFileNameWithoutExtension(modelName.Replace('\\', '/'));
        return stem.Contains("shoulder", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Side-aware implementation shared by ordinary model previews and each
    /// authored half of a shoulder pair.
    /// </summary>
    private string? BuildPreviewGlbCore(
        uint displayId,
        string recoloredPngPath,
        string? modelName,
        string? textureName,
        string assetLabel,
        bool doubleSided)
    {
        if (string.IsNullOrEmpty(modelName)) return null;

        try
        {

            var m2Data = FindAndExtractItemM2(modelName);
            if (m2Data == null) return null;

            var m2Model = M2Reader.Parse(m2Data);
            if (m2Model == null || !m2Model.IsValid) return null;

            // Extract all vanilla textures the M2 references (same as EnsureGlb).
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
            }

            // The recolor must land on the slot the GEOMETRY actually samples for the
            // base skin (batch → TextureLookup → Textures), skipping overlay passes —
            // NOT the "first Type-2" guess, which misses baked-skin weapons like Gressil
            // (renders ITEM\OBJECTCOMPONENTS\WEAPON\1HSWD_02, a Type-0 slot).
            // A weapon can render the SAME skin at MORE THAN ONE sampled slot
            // (Corrupted Ashbringer's two visible pieces). Inject the recolor into
            // EVERY sampled slot that shares the primary skin's filename so all pieces
            // change, not just the first — again skipping overlay passes.
            // The skin can be rendered at MULTIPLE sampled slots — as a baked Type-0
            // texture AND as a Type-2 slot the DBC fills with the SAME skin (Corrupted
            // Ashbringer: baked SWORD_2H_ASHBRINGERCORRUPT at one slot + a Type-2 slot
            // the selected DBC texture fills with the same skin for its other piece).
            // Inject into
            // every such slot. A Type-2 slot has an EMPTY M2 filename, so it can't be
            // matched by filename — match it by the side's DBC texture == the primary skin.
            string dbcTextureStem = Path.GetFileNameWithoutExtension(
                (textureName ?? "").Replace('\\', '/'));
            var skinSlots = new List<int>();
            bool havePrimary = false;
            string primaryStem = "";
            foreach (int ti in GlbWriter.SampledTextureIndices(m2Model))
            {
                if (ti < 0 || ti >= m2Model.Textures.Count) continue;
                string fn = m2Model.Textures[ti].Filename ?? "";
                string up = fn.ToUpperInvariant();
                if (up.Contains("SPEC") || up.Contains("GLOW") || up.Contains("SMOKE") ||
                    up.Contains("ENV") || up.Contains("REFLECT") || up.Contains("SKILLACTIVATED"))
                    continue;
                string stem = Path.GetFileNameWithoutExtension(fn.Replace('\\', '/'));
                if (!havePrimary) { havePrimary = true; primaryStem = stem; }
                bool sameBaked = stem.Equals(primaryStem, StringComparison.OrdinalIgnoreCase);
                bool dbcSameSkin = stem.Length == 0 &&
                    (primaryStem.Length == 0 || dbcTextureStem.Equals(primaryStem, StringComparison.OrdinalIgnoreCase));
                if (sameBaked || dbcSameSkin)
                    skinSlots.Add(ti);
            }
            if (skinSlots.Count == 0)
            {
                int fallback = FindSkinTextureSlot(m2Model, new Dictionary<int, byte[]>());
                if (fallback >= 0) skinSlots.Add(fallback);
            }

            // TEMP DIAGNOSTIC: raw M2 texture layout + what we chose to inject into.
            for (int di = 0; di < m2Model.Textures.Count; di++)
                _logger.LogInformation("ItemTexture/DBG {Id} tex[{I}] Type={T} File='{F}'",
                    displayId, di, m2Model.Textures[di].Type, m2Model.Textures[di].Filename);
            _logger.LogInformation(
                "ItemTexture/DBG {Id} {Asset} Lookup=[{L}] Batches=[{B}] Sampled=[{S}] primaryStem='{P}' dbcTextureStem='{T}' skinSlots=[{K}]",
                displayId, assetLabel, string.Join(",", m2Model.TextureLookup),
                string.Join(" ", m2Model.Batches.Select(b => $"{b.SubmeshIndex}:{b.TextureIndex}")),
                string.Join(",", GlbWriter.SampledTextureIndices(m2Model)),
                primaryStem, dbcTextureStem, string.Join(",", skinSlots));

            // Native dims/format of the slot we inject into: the sampled slot's own
            // texture; for a Type-2 slot (empty filename) use the selected DBC texture;
            // else the first texture.
            int targetW = 0, targetH = 0;
            bool useDxt1 = false;
            var texMeta = GetTexturesForDisplay(displayId);
            ItemTextureEntry? dimTex = null;
            if (primaryStem.Length > 0)
                dimTex = texMeta?.Textures.FirstOrDefault(t =>
                    Path.GetFileNameWithoutExtension((t.Filename ?? "").Replace('\\', '/'))
                        .Equals(primaryStem, StringComparison.OrdinalIgnoreCase));
            if (dimTex == null && !string.IsNullOrEmpty(textureName))
            {
                string dbcTexture = Path.GetFileNameWithoutExtension(textureName.Replace('\\', '/'));
                dimTex = texMeta?.Textures.FirstOrDefault(t =>
                    Path.GetFileNameWithoutExtension((t.Filename ?? "").Replace('\\', '/'))
                        .Equals(dbcTexture, StringComparison.OrdinalIgnoreCase));
            }
            dimTex ??= texMeta?.Textures.FirstOrDefault();
            if (dimTex != null)
            {
                targetW = dimTex.Width > 0 ? dimTex.Width : 0;
                targetH = dimTex.Height > 0 ? dimTex.Height : 0;
                useDxt1 = dimTex.Format == "DXT1";
            }
            if (targetW == 0 || targetH == 0) { targetW = 256; targetH = 256; }

            // Honor a super-res'd recolor: when the PNG is larger than the vanilla
            // slot, encode the BLP at an integer multiple of vanilla (stays power-
            // of-two, so the M2's normalized UVs sample it cleanly) rather than
            // throwing the extra resolution away. Native-size PNGs are unchanged.
            var (encW, encH) = ScaledBlpDims(recoloredPngPath, targetW, targetH);

            // Encode the recolor PNG → BLP, inject it.
            using (var resized = _blpWriter.ResizePngToBitmap(recoloredPngPath, encW, encH))
            {
                if (resized == null) return null;
                var blpBytes = _blpWriter.EncodeBitmapToBlp(resized, useDxt1);
                if (blpBytes == null) return null;
                foreach (int s in skinSlots) textures[s] = blpBytes;
            }

            // Write to a throwaway preview path (NOT the versioned cache).
            var previewDir = Path.Combine(_env.WebRootPath, "item_models", "_preview");
            Directory.CreateDirectory(previewDir);
            string fileName = $"preview_{displayId}_{assetLabel}_{Guid.NewGuid():N}.glb";
            string glbPath = Path.Combine(previewDir, fileName);

            // Mount the item's own ItemVisual (enchant glow) effect models, exactly as the committed
            // cache path does. Without this a staged recolor previewed WITHOUT its glow and then
            // gained one on save — the preview has to be what gets persisted. Resolved AFTER the
            // recolor BLP injection because effect models carry their own sheets and are unaffected
            // by the injected skin. Degrades to null (today's behaviour) when the item has no visual.
            var visualEffects = ResolveVisualEffects(displayId, m2Model);

            bool ok = GlbWriter.SaveGlb(m2Model, textures, glbPath, doubleSided, visualEffects);
            if (!ok)
            {
                _logger.LogWarning(
                    "ItemTexture: preview GLB write failed for displayId {Id} {Asset}",
                    displayId, assetLabel);
                return null;
            }

            _logger.LogInformation(
                "ItemTexture: built preview GLB {File} for displayId {Id} {Asset} (recolor → slot(s) {Slots})",
                fileName, displayId, assetLabel, string.Join(",", skinSlots));
            return $"/item_models/_preview/{fileName}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ItemTexture: BuildPreviewGlb failed for displayId {Id} {Asset} model {Model}",
                displayId, assetLabel, modelName);
            return null;
        }
    }

    /// <summary>
    /// Choose BLP encode dimensions for a (possibly super-res'd) recolor PNG.
    /// When the PNG is larger than the vanilla slot, round to the nearest INTEGER
    /// multiple of vanilla — vanilla item textures are power-of-two, so an integer
    /// multiple stays power-of-two and the M2's normalized UVs sample it cleanly —
    /// keeping the higher resolution. Otherwise return vanilla dims unchanged.
    /// Capped at 1024 per side so a stray large PNG can't balloon the texture.
    /// Falls back to vanilla dims if the PNG can't be read.
    /// </summary>
    private static (int W, int H) ScaledBlpDims(string pngPath, int vanW, int vanH)
    {
        if (vanW <= 0 || vanH <= 0) return (vanW, vanH);
        int pngW;
        try
        {
            using var probe = SkiaSharp.SKBitmap.Decode(pngPath);
            if (probe == null) return (vanW, vanH);
            pngW = probe.Width;
        }
        catch { return (vanW, vanH); }

        int mult = Math.Max(1, (int)Math.Round((double)pngW / vanW));
        while (mult > 1 && (vanW * mult > 1024 || vanH * mult > 1024)) mult--;
        return (vanW * mult, vanH * mult);
    }

    /// <summary>
    /// Delete a temp preview GLB previously produced by BuildPreviewGlbs. Safe to
    /// call with a web path ("/item_models/_preview/xxx.glb") or null. Best-effort.
    /// Also opportunistically sweeps preview GLBs older than 1 hour so abandoned
    /// previews don't accumulate.
    /// </summary>
    public void DeletePreviewGlb(string? webPath)
    {
        try
        {
            var previewDir = Path.Combine(_env.WebRootPath, "item_models", "_preview");

            if (!string.IsNullOrEmpty(webPath))
            {
                var name = Path.GetFileName(webPath.Replace('\\', '/'));
                var full = Path.Combine(previewDir, name);
                if (File.Exists(full)) File.Delete(full);
            }

            // Opportunistic sweep of stale previews (older than 1 hour).
            if (Directory.Exists(previewDir))
            {
                var cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (var f in Directory.EnumerateFiles(previewDir, "preview_*.glb"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                    catch { /* best-effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation("ItemTexture: DeletePreviewGlb best-effort cleanup note ({Err})", ex.Message);
        }
    }

    /// <summary>
    /// Lightweight retexture lookup for EnsureGlb — just the fields needed
    /// to inject the custom BLP into the GLB texture dict.
    /// Returns null for non-retextured displayIds.
    /// </summary>
    private RetextureGlbInfo? GetRetextureInfo(uint displayId)
    {
        try
        {
            using var conn = _db.Admin();
            var row = conn.QueryFirstOrDefault(
                @"SELECT display_id, texture_filename, custom_blp
                  FROM custom_item_retexture
                  WHERE new_display_id = @Did
                  LIMIT 1",
                new { Did = displayId });

            if (row == null) return null;

            return new RetextureGlbInfo
            {
                OrigDisplayId = (uint)row.display_id,
                OrigTexFilename = (string)(row.texture_filename ?? ""),
                CustomBlp = row.custom_blp as byte[]
            };
        }
        catch { return null; }
    }

    private class RetextureGlbInfo
    {
        public uint OrigDisplayId { get; set; }
        public string OrigTexFilename { get; set; } = "";
        public byte[]? CustomBlp { get; set; }
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
    //   attachment ID  5 → ShoulderRight (parented to Bone_56 on HumanMale)
    //   attachment ID  6 → ShoulderLeft  (parented to Bone_55 on HumanMale)
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
        return BuildAttachmentGlb(
            displayId,
            resolvedModelName,
            modelInfo.Value.TextureName1,
            glbPath,
            versionedFilename,
            $"helm{suffix}");
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
    public static string ShoulderBodyCode(string? race, string? gender) =>
        HelmRaceCodes.TryGetValue(race ?? "", out var raceCode)
        && (string.Equals(gender, "Female", StringComparison.OrdinalIgnoreCase) || string.Equals(gender, "Male", StringComparison.OrdinalIgnoreCase))
            ? raceCode + (string.Equals(gender, "Female", StringComparison.OrdinalIgnoreCase) ? "F" : "M") : "invalid";

    public string? EnsureShoulderGlb(uint displayId, ShoulderSide side, string? race = "Human", string? gender = "Male")
    {
        if (displayId == 0) return null;

        var sideSuffix = side == ShoulderSide.Left ? "lshoulder" : "rshoulder";
        var glbDir = Path.Combine(_env.WebRootPath, "item_models");
        string bodyCode = ShoulderBodyCode(race, gender);
        var naturalFilename = $"{displayId}_{sideSuffix}_{bodyCode}.glb";
        var versionedFilename = CacheVersionRegistry.MakeVersioned(
            naturalFilename, CacheVersionRegistry.RigidGlbVersion);
        var glbPath = Path.Combine(glbDir, versionedFilename);

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
        return BuildAttachmentGlb(
            displayId, modelName, textureName, glbPath, versionedFilename, sideSuffix,
            bodyCode, side == ShoulderSide.Left ? "L" : "R");
    }

    /// <summary>
    /// Shared helm/shoulder GLB builder. Extracts the M2, applies any
    /// embedded textures, swaps in the DBC-supplied skin texture, writes
    /// the GLB via the rigid writer. Returns the web URL, or null on any
    /// step failing. All errors are logged with context so the callers in
    /// EnsureHelmGlb / EnsureShoulderGlb stay terse.
    ///
    /// === Cache discipline (same as EnsureGlb, which weapons already had) ===
    /// The filename carries RigidGlbVersion, which only moves when the assembly
    /// is rebuilt. That is enough for a writer change and NOT enough for a
    /// content change: a forged SUI_A_#### model keeps its name across a
    /// re-import, so a bare File.Exists check kept serving the GLB built from
    /// the previous patch's bytes for the life of the process — which reads as
    /// "the fix did nothing" while iterating in the Armor Forge.
    ///
    /// So: refresh the live patch mounts, resolve the real bytes, fingerprint
    /// them, and rebuild whenever the fingerprint moves. The URL carries the
    /// fingerprint so a browser cannot reuse an older same-display response
    /// either.
    /// </summary>
    private string? BuildAttachmentGlb(
        uint displayId,
        string? modelName,
        string? textureName,
        string glbPath,
        string versionedFilename,
        string kindLabel,
        string? bodyCode = null,
        string? shoulderSide = null)
    {
        if (string.IsNullOrEmpty(modelName))
        {
            _logger.LogDebug(
                "ItemTexture/Attachment: displayId {Id} {Kind} — empty modelName",
                displayId, kindLabel);
            return null;
        }

        bool declaredShoulder = false;
        byte[]? declaredShoulderSkin = null;
        try
        {
            // The MPQ singleton may have mounted before patch-5/patch-6 existed.
            _mpq.RefreshLivePatches();
            // Same path search as weapons — Item\ObjectComponents\{Head,Shoulder,...}
            // is in ItemModelPrefixes, so a bare "Helm_..." or "LShoulder_..."
            // filename resolves correctly.
            var m2Data = FindAndExtractItemM2(modelName);
            if (m2Data == null)
            {
                _logger.LogWarning(
                    "ItemTexture/Attachment: M2 not found in MPQ for displayId {Id} {Kind} — modelName='{Name}'",
                    displayId, kindLabel, modelName);
                return null;
            }

            var m2Model = M2Reader.Parse(m2Data);
            if (m2Model == null || !m2Model.IsValid)
            {
                _logger.LogWarning(
                    "ItemTexture/Attachment: M2 parse failed for displayId {Id} {Kind} — modelName='{Name}'",
                    displayId, kindLabel, modelName);
                return null;
            }

            if (shoulderSide is not null)
            {
                declaredShoulder = m2Model.Name.Contains("|MSUI_SHOULDER_FITS_", StringComparison.Ordinal);
                string defaultPath = @"Item\ObjectComponents\Shoulder\" + Path.GetFileNameWithoutExtension(modelName) + ".m2";
                string skinPath = @"Item\ObjectComponents\Shoulder\" + textureName + ".blp";
                var selected = ShoulderFitResolver.Resolve(defaultPath, m2Data, m2Model.Name, bodyCode!, skinPath,
                    shoulderSide, member => _mpq.ExtractFile(member));
                if (selected.ManifestPath is not null)
                {
                    declaredShoulderSkin = _mpq.ExtractFile(selected.SkinPath!);
                    if (declaredShoulderSkin is null || ShoulderFitResolver.Hash(declaredShoulderSkin) != selected.SkinSha256)
                        throw new InvalidDataException("Declared shoulder shared skin changed during preview resolution.");
                    ShoulderFitSelections.Add(selected);
                    modelName = selected.Path;
                    m2Data = selected.Bytes;
                    m2Model = M2Reader.Parse(m2Data);
                    if (m2Model is null || !m2Model.IsValid) throw new InvalidDataException("Declared shoulder-fit M2 cannot be parsed.");
                    kindLabel += $"|{bodyCode}|{selected.ManifestSha256}";
                }
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
                var blpData = declaredShoulderSkin ?? FindItemBlp(textureName, modelName);
                if (blpData != null)
                {
                    int slot = FindSkinTextureSlot(m2Model, textures);
                    if (slot >= 0) textures[slot] = blpData;
                    else if (declaredShoulder) throw new InvalidDataException("Declared shoulder has no shared-skin material slot.");
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

            var visualEffects = ResolveVisualEffects(displayId, m2Model);
            string fingerprint = ComputeAttachmentSourceFingerprint(
                displayId, kindLabel, modelName, textureName, m2Data, textures,
                _dbc.GetItemModelInfo(displayId)?.ItemVisualId ?? 0);
            string webPath = $"/item_models/{versionedFilename}?source={fingerprint}";
            string sourcePath = Path.ChangeExtension(glbPath, ".source");

            if (GlbCacheMatchesSource(glbPath, sourcePath, fingerprint))
                return webPath;

            // Attachment GLBs go in with doubleSided=true. Vanilla helm
            // and shoulder M2s frequently include single-sided thin
            // features (spaulder hanging flap, helm wings/horns) whose
            // authored winding faces the wrong way after our coordinate
            // flip — backface culling then makes them disappear. See the
            // doubleSided docstring on GlbWriter.SaveGlb.
            if (WriteAttachmentGlbAtomically(m2Model, textures, glbPath, sourcePath, fingerprint,
                    visualEffects))
            {
                _logger.LogInformation(
                    "ItemTexture/Attachment: Generated displayId {Id} {Kind} GLB ({Model}, {Size}KB)",
                    displayId, kindLabel, modelName,
                    new FileInfo(glbPath).Length / 1024);
                return webPath;
            }

            _logger.LogWarning(
                "ItemTexture/Attachment: GlbWriter failed for displayId {Id} {Kind} (model='{Model}')",
                displayId, kindLabel, modelName);
            return null;
        }
        catch (Exception ex)
        {
            if (declaredShoulder) ShoulderFitErrors.Add($"display {displayId} {shoulderSide}/{bodyCode}: {ex.Message}");
            _logger.LogWarning(ex,
                "ItemTexture/Attachment: Exception generating displayId {Id} {Kind}",
                displayId, kindLabel);
            return null;
        }
    }

    /// <summary>Fingerprint over everything that decides what an attachment GLB looks like: the
    /// resolved names, the raw M2, and every bound texture blob. Same framing as
    /// <see cref="ComputeGlbSourceFingerprint"/>, with its own domain marker so the two cannot
    /// collide.</summary>
    private static string ComputeAttachmentSourceFingerprint(
        uint displayId,
        string kindLabel,
        string modelName,
        string? textureName,
        byte[] m2Data,
        IReadOnlyDictionary<int, byte[]> textures,
        uint itemVisualId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendFingerprintString(hash, "ItemTextureService.BuildAttachmentGlb/source-v2");
        AppendFingerprintUInt32(hash, displayId);
        AppendFingerprintUInt32(hash, itemVisualId);
        AppendFingerprintString(hash, kindLabel);
        AppendFingerprintString(hash, modelName);
        AppendFingerprintString(hash, textureName);
        AppendFingerprintBytes(hash, m2Data);

        AppendFingerprintUInt32(hash, checked((uint)textures.Count));
        foreach (var texture in textures.OrderBy(pair => pair.Key))
        {
            AppendFingerprintInt32(hash, texture.Key);
            AppendFingerprintBytes(hash, texture.Value);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Write the GLB and its stamp so every interruption point leaves a MISSING stamp
    /// rather than a new GLB blessed by the old fingerprint. Mirrors
    /// <see cref="WriteGlbCacheAtomically"/>, which cannot be reused directly because attachments
    /// need doubleSided=true.</summary>
    private static bool WriteAttachmentGlbAtomically(
        M2Model m2Model,
        Dictionary<int, byte[]> textures,
        string glbPath,
        string sourcePath,
        string sourceFingerprint,
        IReadOnlyList<M2Fx.ItemVisualEffects.Effect>? visualEffects = null)
    {
        string nonce = Guid.NewGuid().ToString("N");
        string tempGlbPath = $"{glbPath}.{nonce}.tmp";
        string tempSourcePath = $"{sourcePath}.{nonce}.tmp";

        try
        {
            if (!GlbWriter.SaveGlb(m2Model, textures, tempGlbPath, doubleSided: true, visualEffects))
                return false;

            File.WriteAllText(tempSourcePath, sourceFingerprint, new UTF8Encoding(false));

            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            File.Move(tempGlbPath, glbPath, overwrite: true);
            File.Move(tempSourcePath, sourcePath, overwrite: true);
            return true;
        }
        finally
        {
            try { if (File.Exists(tempGlbPath)) File.Delete(tempGlbPath); } catch { }
            try { if (File.Exists(tempSourcePath)) File.Delete(tempSourcePath); } catch { }
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
    /// CLOAKS / CAPES — the third kind of item texture.
    ///
    /// A cape has NO M2 model of its own (it's a geoset on the character), so
    /// GetTexturesForDisplay can't serve it: that method hard-returns null when
    /// ModelName is empty. And it paints nothing into the body atlas, so
    /// BodyAtlasTextureService can't either — that only walks m_texture[0..7]
    /// under Item\TextureComponents\, which cloaks never populate.
    ///
    /// Instead a cape is textured straight from ItemDisplayInfo's ModelTexture
    /// (TextureName1), resolved under:
    ///
    ///     Item\ObjectComponents\Cape\{TextureName1}.blp
    ///
    /// Note FindItemBlp deliberately does NOT probe Cape\ — it's only reachable
    /// from the M2 path, which capes never enter. Hence this dedicated resolver.
    ///
    /// Returns an ItemTextureEntry (same shape the model path yields) so callers
    /// and the retexture commit can treat it identically. Null if the display has
    /// no cape BLP — i.e. it isn't a cloak.
    /// </summary>
    public ItemTextureEntry? GetCapeTexture(uint displayId)
        => GetObjectComponentTexture(displayId, "Cape");

    /// <summary>
    /// OBJECT-COMPONENT TEXTURE — the DBC-direct resolver, generalized.
    ///
    /// GetCapeTexture was this same routine with "Cape" hardcoded. Two other item
    /// kinds need it:
    ///
    ///   HELMS. ExtractTexturesFromMpq resolves textures by parsing the M2, and
    ///   bails if the M2 can't be extracted. But helm M2s are RACE+GENDER suffixed
    ///   (Helm_X_HuM.m2, Helm_X_OrF.m2 — see EnsureHelmGlb), while ItemDisplayInfo
    ///   stores only the bare stem. So FindAndExtractItemM2 misses, and every helm
    ///   reported "No textures found" — 150 of them in the July batch. A helm's
    ///   TEXTURE never needed the M2: TextureName1 + Item\ObjectComponents\Head\
    ///   resolves it directly, exactly like a cape.
    ///
    ///   SHOULDERS. Same fallback, for the same reason, when the M2 path misses.
    ///
    /// Returns an ItemTextureEntry with the same shape the M2 path yields, so the
    /// retexture commit treats all three kinds identically. Null when the display
    /// has no BLP under that subdir — i.e. it isn't that kind of item.
    /// </summary>
    public ItemTextureEntry? GetObjectComponentTexture(uint displayId, string subdir)
    {
        // Honor custom retextures the same way GetTexturesForDisplay does.
        var retexInfo = GetRetextureInfo(displayId);
        uint resolvedDisplayId = retexInfo?.OrigDisplayId ?? displayId;

        var modelInfo = _dbc.GetItemModelInfo(resolvedDisplayId);
        if (modelInfo == null) return null;

        // Both texture columns and the client's own candidate list (MSUIClient CharacterRenderer
        // .CapeTextureCandidates): ObjectComponents then TextureComponents, bare stem then the
        // _U / _M / _F gender-suffixed sheets. One fixed path missed every cloak whose row names
        // the second column or a suffixed sheet, and those drew as bare cloth.
        string? texName = null; byte[]? blpData = null; string mpqPath = "";
        foreach (string name in new[] { modelInfo.Value.TextureName1, modelInfo.Value.TextureName2 })
        {
            if (string.IsNullOrEmpty(name)) continue;
            string stem = name.Replace('/', '\\').TrimStart('\\');
            if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            var candidates = stem.Contains('\\')
                ? new[] { stem + ".blp" }
                : new[]
                {
                    $"Item\\ObjectComponents\\{subdir}\\{stem}.blp",
                    $"Item\\TextureComponents\\{subdir}\\{stem}.blp",
                    $"Item\\ObjectComponents\\{subdir}\\{stem}_U.blp",
                    $"Item\\ObjectComponents\\{subdir}\\{stem}_M.blp",
                    $"Item\\ObjectComponents\\{subdir}\\{stem}_F.blp",
                    $"Item\\TextureComponents\\{subdir}\\{stem}_U.blp",
                    $"Item\\TextureComponents\\{subdir}\\{stem}_M.blp",
                    $"Item\\TextureComponents\\{subdir}\\{stem}_F.blp",
                };
            foreach (string candidate in candidates)
            {
                blpData = _mpq.ExtractFile(candidate) ?? _mpq.ExtractFile(candidate.ToLowerInvariant());
                if (blpData != null) { texName = name; mpqPath = candidate; break; }
            }
            if (blpData != null) break;
        }
        if (blpData == null || texName is null)
        {
            _logger.LogDebug(
                "ObjectComponent: displayId {Id} has no BLP under {Subdir} for '{T1}' / '{T2}'",
                displayId, subdir, modelInfo.Value.TextureName1, modelInfo.Value.TextureName2);
            return null;
        }

        string sub = subdir.ToLowerInvariant();
        var cacheDir = Path.Combine(_env.WebRootPath, "item_textures_cache", sub);
        Directory.CreateDirectory(cacheDir);

        string safeName = texName.Replace('\\', '_').Replace('/', '_');
        string pngCachePath = Path.Combine(cacheDir, $"{sub}_{resolvedDisplayId}_{safeName}.png");
        string webPath = $"/item_textures_cache/{sub}/{sub}_{resolvedDisplayId}_{safeName}.png";

        if (!File.Exists(pngCachePath))
        {
            try
            {
                DecodeBlpToPng(blpData, pngCachePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ObjectComponent: BLP decode failed for displayId {Id} ({Tex}) under {Subdir}",
                    displayId, texName, subdir);
                return null;
            }
        }

        return new ItemTextureEntry
        {
            Index = 0,
            Filename = $"{texName}.blp",
            MpqPath = mpqPath,
            BlpFileSize = blpData.Length,
            PreviewPngPath = webPath,
            HasPreview = true
        };
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
    /// Clears both the in-memory texture cache and the on-disk GLB file
    /// so the next request regenerates with the updated texture.
    /// </summary>
    public void InvalidateCache(uint displayId)
    {
        _cache.TryRemove(displayId, out _);

        // Also delete the cached GLB and its source stamp so the next request
        // regenerates with the new model/texture inputs. Share EnsureGlb's
        // display-scoped lock so invalidation cannot race an atomic promotion.
        try
        {
            object buildLock = GlbBuildLocks.GetOrAdd(displayId, static _ => new object());
            lock (buildLock)
            {
                var glbDir = Path.Combine(_env.WebRootPath, "item_models");
                if (Directory.Exists(glbDir))
                {
                    // Match versioned/unversioned GLBs, source sidecars, and
                    // any same-display temp artifact left by an interrupted run.
                    foreach (var file in Directory.GetFiles(glbDir, $"{displayId}.*"))
                    {
                        string extension = Path.GetExtension(file);
                        if (!extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) &&
                            !extension.Equals(".source", StringComparison.OrdinalIgnoreCase) &&
                            !extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                            continue;

                        File.Delete(file);
                        _logger.LogInformation(
                            "ItemTexture: Deleted cached GLB artifact {File} for invalidation",
                            Path.GetFileName(file));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ItemTexture: Failed to delete cached GLB for displayId {Id}", displayId);
        }
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

        var pixels = BlpDecoder.GetPixels(blpData, 0, out int w, out int h);

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
    // Thunderfury display 30606 is a useful counterexample: this stays zero;
    // its lightning comes from native geometry/tracks in the weapon M2.
    public uint ItemVisualId;
}
