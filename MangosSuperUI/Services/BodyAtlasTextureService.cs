using System.Collections.Concurrent;
using War3Net.Drawing.Blp;

namespace MangosSuperUI.Services;

/// <summary>
/// Resolves the 8 body-atlas BLP textures for an item display (slots 0-7 of
/// ItemDisplayInfo.m_texture[]) into web-accessible PNG URLs.
///
/// === Pipeline ===
///   1. Caller passes a displayId.
///   2. DbcService.ItemModelInfos[displayId].BodyTextures gives the 8
///      m_texture[] partial filenames, slot-indexed 0-7.
///   3. For each non-empty slot, build the BLP MPQ path using the
///      slot-specific TextureComponents directory.
///   4. MpqReaderService extracts the BLP.
///   5. War3Net + SkiaSharp decode to PNG (mirroring ItemTextureService's
///      DecodeBlpToPng pattern).
///   6. PNG cached on disk under /body_atlas_cache/{displayId}/slot{N}_{name}.png
///      and a web URL is returned.
///
/// === Why this is separate from ItemTextureService ===
/// ItemTextureService handles the WEAPON / SHIELD case: each item has its
/// own full M2 model with embedded textures. Those models are large and
/// complete-render-target.
///
/// This service handles the ARMOR case: items have NO model of their own —
/// they paint partial textures into the SHARED character body atlas which
/// is composited per-frame on the client. The atlas regions and slot table
/// are defined in /character-viewer/region-rects.js (Session C).
///
/// === BLP path layout ===
/// Vanilla BLP paths for body textures follow this convention:
///
///   Item\TextureComponents\{SuffixDir}\{partialName}_{suffix}.blp
///
/// where the suffix and directory are slot-specific:
///
///   slot 0 → ArmUpperTexture       → "_AU"
///   slot 1 → ArmLowerTexture       → "_AL"
///   slot 2 → HandTexture           → "_HA"
///   slot 3 → TorsoUpperTexture     → "_TU"
///   slot 4 → TorsoLowerTexture     → "_TL"
///   slot 5 → LegUpperTexture       → "_LU"
///   slot 6 → LegLowerTexture       → "_LL"
///   slot 7 → FootTexture           → "_FO"
///
/// However, the DBC's `m_texture[]` stringrefs already contain the full
/// partial name INCLUDING the suffix (e.g. "Robe_C_01Blue_Chest_TU"). So
/// we only need to prepend the TextureComponents subdirectory and append
/// ".blp" — the rest is in the DBC.
///
/// Some items use male/female-specific variants by appending "_M" or "_F"
/// to the BLP filename (e.g. "Robe_..._TU_M.blp"). Both possibilities are
/// tried.
///
/// === Cache policy ===
/// Only SUCCESSFUL resolves are cached. A null result (BLP not found in
/// any MPQ, or decode failure) is left out of _cache so subsequent
/// requests re-probe. Previously we cached nulls too — that turned out
/// to be poisonous: when MpqReaderService had its StormLib concurrency
/// bug (fixed by adding _stormLock there), a single concurrent equip
/// could write phantom nulls for slots that genuinely exist on disk,
/// and those nulls then stuck for the entire service lifetime. Even
/// after the underlying race was fixed, the in-memory nulls survived
/// every deploy until service restart. Positive-only caching means
/// transient failures self-heal on the next request.
///
/// Cost: a slot that legitimately doesn't exist in the MPQ (common —
/// slots 2/7 miss frequently because gloves/boots use M2 geometry, not
/// the body atlas) gets re-probed every equip. ExtractFile is now
/// thread-safe and returns in microseconds; an 8-slot DBC-miss is well
/// under a millisecond. Not worth caching.
/// </summary>
public class BodyAtlasTextureService
{
    private readonly MpqReaderService _mpq;
    private readonly DbcService _dbc;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BodyAtlasTextureService> _logger;

    // Cache: (displayId, slot) → web URL.
    // ONLY POSITIVE RESULTS are stored. A slot that resolved to null
    // (not in MPQ, decode failed) is intentionally left out so the next
    // request re-probes it. See "Cache policy" in the class doc.
    private readonly ConcurrentDictionary<(uint, int), string> _cache = new();

    // Per-displayId locks to prevent double-decode on simultaneous requests.
    // Same pattern as CharacterModelService._locks.
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _locks = new();

    public BodyAtlasTextureService(
        MpqReaderService mpq,
        DbcService dbc,
        IWebHostEnvironment env,
        ILogger<BodyAtlasTextureService> logger)
    {
        _mpq = mpq;
        _dbc = dbc;
        _env = env;
        _logger = logger;
    }

    private string CacheDir => Path.Combine(_env.WebRootPath, "body_atlas_cache");

    // Slot → MPQ subdir under "Item\TextureComponents\". The slot
    // numbering matches m_texture[0..7] in vanilla 1.12 ItemDisplayInfo.dbc:
    //
    //   slot 0 (_AU) → ArmUpperTexture       (shoulders/biceps)
    //   slot 1 (_AL) → ArmLowerTexture       (forearms)
    //   slot 2 (_HA) → HandTexture           (hand/wrist)
    //   slot 3 (_TU) → TorsoUpperTexture     (chest)
    //   slot 4 (_TL) → TorsoLowerTexture     (belly/waist)
    //   slot 5 (_LU) → LegUpperTexture       (thigh / robe upper)
    //   slot 6 (_LL) → LegLowerTexture       (shin / robe lower)
    //   slot 7 (_FO) → FootTexture           (foot — boots use this)
    //
    // Earlier drafts used a 2-6 mapping that descended from a wrong
    // DBC parser offset; corrected once the field layout was empirically
    // derived. See LoadItemModelInfo doc in DbcService.cs.
    private static readonly Dictionary<int, string> SlotToSubdir = new()
    {
        { 0, "ArmUpperTexture" },
        { 1, "ArmLowerTexture" },
        { 2, "HandTexture" },
        { 3, "TorsoUpperTexture" },
        { 4, "TorsoLowerTexture" },
        { 5, "LegUpperTexture" },
        { 6, "LegLowerTexture" },
        { 7, "FootTexture" },
    };

    /// <summary>
    /// Result of resolving a displayId — one web URL per non-empty slot.
    /// Keys are slot indices (2-7); missing keys mean that slot was empty
    /// in the DBC. URLs are web-relative (e.g. "/body_atlas_cache/12345/slot5_foo.png").
    /// </summary>
    public class BodyAtlasResult
    {
        public uint DisplayId { get; set; }
        public Dictionary<int, string> SlotUrls { get; set; } = new();
        /// <summary>The raw partial-name strings for diagnostics
        /// (e.g. "Robe_C_01Blue_Chest_TU").</summary>
        public Dictionary<int, string> SlotPartialNames { get; set; } = new();
    }

    /// <summary>
    /// Resolve every body-atlas BLP for a display and return PNG URLs.
    /// Returns null if the displayId isn't in the DBC.
    ///
    /// Idempotent and cached — call freely.
    /// </summary>
    public async Task<BodyAtlasResult?> EnsureAtlasTexturesAsync(uint displayId)
    {
        var infoNullable = _dbc.GetItemModelInfo(displayId);
        if (infoNullable == null)
        {
            _logger.LogDebug("BodyAtlas: displayId {Id} not in DBC", displayId);
            return null;
        }
        // ItemModelDbc is a nullable struct — unwrap once. `.BodyTextures`
        // can still be null on legacy entries (e.g. RegisterCustomDisplayEntry
        // rows created before the Session C field expansion); we handle that
        // with a per-access null guard below.
        var info = infoNullable.Value;

        var result = new BodyAtlasResult { DisplayId = displayId };

        // Fast path: every non-empty slot already cached?
        //
        // Since we only cache positive results, "TryGetValue miss" means
        // either "never resolved" or "resolved but failed". We can't tell
        // them apart without re-probing — which is exactly the point. If
        // any non-empty DBC slot is missing from cache, fall through to
        // the slow path. The slow path's per-slot ExtractFile is now
        // cheap (microseconds under _stormLock), so an occasional
        // unnecessary re-probe of a known-bad slot is fine.
        bool allCached = true;
        for (int slot = 0; slot <= 7; slot++)
        {
            var partial = (info.BodyTextures != null && info.BodyTextures.Length > slot)
                ? info.BodyTextures[slot] : null;
            if (string.IsNullOrEmpty(partial)) continue;
            if (_cache.TryGetValue((displayId, slot), out var url))
            {
                result.SlotUrls[slot] = url;
                result.SlotPartialNames[slot] = partial;
            }
            else
            {
                allCached = false;
                break;
            }
        }
        if (allCached) return result;

        // Slow path: acquire per-display lock and (re)resolve.
        var sem = _locks.GetOrAdd(displayId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            for (int slot = 0; slot <= 7; slot++)
            {
                var partial = (info.BodyTextures != null && info.BodyTextures.Length > slot)
                    ? info.BodyTextures[slot] : null;
                if (string.IsNullOrEmpty(partial)) continue;

                result.SlotPartialNames[slot] = partial;
                var url = await ResolveSlotAsync(displayId, slot, partial);

                if (url != null)
                {
                    // Positive-only caching. A null here is left out of
                    // _cache so the next request re-probes. See class doc.
                    _cache[(displayId, slot)] = url;
                    result.SlotUrls[slot] = url;
                }
            }
        }
        finally
        {
            sem.Release();
        }

        return result;
    }

    /// <summary>
    /// Extract + decode a single slot's BLP. Returns the web URL on success
    /// or null on failure (BLP not found, decode error).
    ///
    /// Note: failures are NOT cached at the calling layer — see class doc.
    /// </summary>
    private async Task<string?> ResolveSlotAsync(uint displayId, int slot, string partialName)
    {
        if (!SlotToSubdir.TryGetValue(slot, out var subdir))
        {
            _logger.LogWarning("BodyAtlas: invalid slot {Slot}", slot);
            return null;
        }

        // Cache filename — keep the original case-aware partial for traceability.
        var safeName = partialName.Replace('\\', '_').Replace('/', '_');
        var cachePngPath = Path.Combine(CacheDir, displayId.ToString(),
            $"slot{slot}_{safeName}.png");
        var webUrl = $"/body_atlas_cache/{displayId}/slot{slot}_{safeName}.png";

        // Fast path: PNG already exists on disk from a previous run.
        // Disk cache survives service restarts, so this short-circuits
        // even when the in-memory _cache is cold.
        if (File.Exists(cachePngPath))
            return webUrl;

        // Try the expected MPQ paths, in order. Empirically (50-minute
        // brute-force probe over ~15k displayIds with body textures) the
        // _M-suffixed variant wins for most torso slots, with _F a close
        // second. But sleeves and legs frequently use a _U "unisex" suffix
        // that wasn't in the original probe set — Robe_A_01Indigo_Pant_LU
        // resolves to ..._LU_U.blp, for example. So try _M → _F → _U →
        // bare. Hit rates are slot-dependent and not always high — vanilla
        // simply doesn't have body-atlas BLPs for every slot a DBC row
        // references. Slots 2 (hand) and 7 (foot) commonly miss because
        // gloves/boots paint via M2 geometry, not the body atlas.
        string[] candidates = new[]
        {
            $"Item\\TextureComponents\\{subdir}\\{partialName}_M.blp",
            $"Item\\TextureComponents\\{subdir}\\{partialName}_F.blp",
            $"Item\\TextureComponents\\{subdir}\\{partialName}_U.blp",
            $"Item\\TextureComponents\\{subdir}\\{partialName}.blp",
        };

        byte[]? blpData = null;
        string? hitPath = null;
        foreach (var candidate in candidates)
        {
            blpData = _mpq.ExtractFile(candidate);
            _logger.LogInformation(
                "BodyAtlas: TRY displayId={Id} slot={Slot} path={Path} hit={Hit} bytes={Bytes}",
                displayId, slot, candidate, blpData != null, blpData?.Length ?? 0);
            if (blpData != null) { hitPath = candidate; break; }
        }

        if (blpData == null)
        {
            _logger.LogInformation(
                "BodyAtlas: displayId={Id} slot={Slot} BLP not in MPQ — partial={Partial} subdir={Subdir} (this is normal for many slots in vanilla)",
                displayId, slot, partialName, subdir);
            return null;
        }

        try
        {
            DecodeBlpToPng(blpData, cachePngPath);
            _logger.LogDebug(
                "BodyAtlas: displayId={Id} slot={Slot} → {Url} (from {Mpq})",
                displayId, slot, webUrl, hitPath);
            return webUrl;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BodyAtlas: BLP decode failed for displayId={Id} slot={Slot} ({Partial})",
                displayId, slot, partialName);
            return null;
        }
    }

    // ───────────────────────────────────────────────────────────────
    // BLP → PNG (mirrors ItemTextureService.DecodeBlpToPng to keep
    // the conversion pipeline identical across services)
    // ───────────────────────────────────────────────────────────────

    private static void DecodeBlpToPng(byte[] blpData, string outputPngPath)
    {
        var dir = Path.GetDirectoryName(outputPngPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var ms = new MemoryStream(blpData);
        var blpFile = new BlpFile(ms);
        var pixels = blpFile.GetPixels(0, out int w, out int h);

        using var bitmap = new SkiaSharp.SKBitmap(w, h,
            SkiaSharp.SKColorType.Bgra8888,
            SkiaSharp.SKAlphaType.Unpremul);

        var bitmapPixels = bitmap.GetPixels();
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmapPixels, pixels.Length);
        bitmap.NotifyPixelsChanged();

        using var outStream = File.Create(outputPngPath);
        bitmap.Encode(outStream, SkiaSharp.SKEncodedImageFormat.Png, 100);
    }
}