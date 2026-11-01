// RetextureEngineController.cs
//
// Backend for the Retexture Engine section. A NEW, parallel controller — it does
// NOT touch ItemsController. Retexture LOGIC stays in C# (PaletteSwapService);
// this controller exposes the seeded theory + tier + VALUE knobs, the item
// browse, and — as of this revision — the lootifier QUEUE, which the section
// used to borrow from /Items/.
//
// WHY THE QUEUE MOVED HERE
// ------------------------
// The /Items/ queue endpoints know nothing about `source`, so the section could
// BUILD a queue for one lootifier but never RUN, RESET or UNDO one — and every
// re-run minted a fresh display id while orphaning the previous one inside
// patch-4.MPQ, which is why re-running looked like it ADDED and never undid.
// The versions here are all source-scoped and come with a real revert.
// ItemsController's copies stay where they are, untouched, for the old Items UI.
//
// Endpoints:
//   GET  /RetextureEngine             -> the section view (Index)
//   GET  /RetextureEngine/Preview     -> one cell, full knobs -> PNG url  [live tuning]
//   GET  /RetextureEngine/Sheet       -> theory x tier contact sheet, value-aware
//   GET  /RetextureEngine/PreviewOnModel -> recolored atlas slots or baked GLB
//   POST /RetextureEngine/RetextureSelection -> ad-hoc selection, one tier or all
//   GET  /RetextureEngine/Browse | /GeneratedEntries | /BaseVariants
//        | /ItemSources | /SourceTexture
//
//   -- lootifier queue; every verb takes an optional `source` --
//   -- ("quest" | "crafting" | "loot"), omitted = all sources --
//   GET  /RetextureEngine/Sources      -> per-lootifier counts for the panel
//   POST /RetextureEngine/BuildQueue   -> { sources:[], requeue? }
//   GET  /RetextureEngine/QueueStatus  -> ?source=
//   POST /RetextureEngine/ProcessQueue -> { max, source?, theory, value, v* }
//   POST /RetextureEngine/ResetQueue   -> { source?, mode: failed|all|clear }
//   POST /RetextureEngine/RevertQueue  -> { source?, requeue? }   <- the undo
//   POST /RetextureEngine/PurgeOrphans -> { apply? }
//   POST /RetextureEngine/RebuildPatch
//   GET  /RetextureEngine/PatchStatus
//   GET  /RetextureEngine/DownloadPatch  -> the archive itself
//
// Value knobs (query or body): value=keep|invert, vSigma, vDetail, vKnee, vFloor,
// vBlend, vAlpha, vScale. Omitted knobs fall back to the spec defaults.

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;

namespace MangosSuperUI.Controllers;

public class RetextureEngineController : Controller
{
    private readonly PaletteSwapService _palette;
    private readonly RetextureSupport _support;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<RetextureEngineController> _logger;

    public RetextureEngineController(
        PaletteSwapService palette, RetextureSupport support,
        IWebHostEnvironment env, ILogger<RetextureEngineController> logger)
    {
        _palette = palette;
        _support = support;
        _env = env;
        _logger = logger;
    }

    // The section page. View lives at Views/RetextureEngine/Index.cshtml (next).
    public IActionResult Index() => View();

    private const string CacheDir = "retexture_engine";

    // Build ValueSettings from query knobs. mode=keep|invert; rest default to spec.
    private static ValueSettings ParseValue(
        string? mode, float? sigma, float? detail, float? knee, float? floor,
        float? blend, int? alpha, bool? scale) =>
        string.Equals(mode, "invert", StringComparison.OrdinalIgnoreCase)
            ? ValueSettings.Invert(
                sigma ?? 2.5f, detail ?? 1.0f, knee ?? 0.40f, floor ?? 0.25f,
                blend ?? 1.0f, alpha ?? 16, scale ?? true)
            : ValueSettings.Keep;

    /// <summary>
    /// GET /RetextureEngine/Preview?displayId=&theory=&tier=&ladder=&value=&vSigma=...
    /// One recolored cell with the full knob set — the live-tuning workhorse the
    /// JS calls on each knob change. Same pixels the queue would commit at these
    /// settings. Returns { success, url } (PNG under wwwroot, cache-busted).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Preview(
        uint displayId, string theory = "fan", string tier = "improved", bool ladder = false,
        string? value = null, float? vSigma = null, float? vDetail = null,
        float? vKnee = null, float? vFloor = null, float? vBlend = null,
        int? vAlpha = null, bool? vScale = null, float? hue = null)
    {
        var ct = HttpContext.RequestAborted;
        var (srcPng, err) = await _support.ResolvePrimarySourceAsync(displayId, ct);
        if (srcPng == null) return Json(new { success = false, error = err });

        if (Array.IndexOf(PaletteSwapService.RecolorTheories, theory) < 0) theory = "fan";
        var (kd, ku, m, pop) = RetextureSupport.TierShape(tier);
        var (budget, leash) = RetextureSupport.TierPolicy(tier);
        int seed = ladder ? RetextureSupport.SeedFor((int)displayId, "")
                          : RetextureSupport.SeedFor((int)displayId, tier);
        var vset = ParseValue(value, vSigma, vDetail, vKnee, vFloor, vBlend, vAlpha, vScale);

        var outDir = Path.Combine(_env.WebRootPath, "item_textures_cache", CacheDir);
        Directory.CreateDirectory(outDir);
        string vTag = vset.IsInvert ? "inv" : "keep";
        // A user-picked primary hue must be part of the cache key, or two hues share one file.
        string hueTag = hue.HasValue ? $"_h{((int)Math.Round(hue.Value) % 360 + 360) % 360}" : "";
        string file = $"prev_{displayId}_{theory}_{tier}_{(ladder ? "L" : "R")}_{vTag}{hueTag}.png";
        string outPng = Path.Combine(outDir, file);

        var ok = await _palette.RecolorSeededAsync(
            srcPng, outPng, seed, 1.0f, 0.0f, false, ct,
            theory, kd, ku, m, pop, budget, leash, vset, hue);
        if (ok == null) return Json(new { success = false, error = "recolor failed" });

        return Json(new
        {
            success = true,
            url = $"/item_textures_cache/{CacheDir}/{file}?t={DateTime.UtcNow.Ticks}",
            theory,
            tier,
            ladder,
            value = vTag,
            hue,
        });
    }

    /// <summary>
    /// GET /RetextureEngine/Primary?displayId= — the item's detected colour families (dominant first)
    /// so a colour picker can seed itself with the majority colour. Returns { success, primaryHue,
    /// primaryHex, families:[{family, hue, hex, percent, sat, light}] }.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Primary(uint displayId)
    {
        var ct = HttpContext.RequestAborted;
        var (srcPng, err) = await _support.ResolvePrimarySourceAsync(displayId, ct);
        if (srcPng == null) return Json(new { success = false, error = err });

        var families = _palette.DetectFamilies(srcPng);
        if (families.Count == 0) return Json(new { success = false, error = "no colour families detected" });

        // Prefer the dominant CHROMATIC family for the primary; fall back to the overall dominant.
        var chromatic = families.Where(f => f.Family is not ("white" or "black" or "grey")).ToList();
        var primary = (chromatic.Count > 0 ? chromatic : families).OrderByDescending(f => f.Percent).First();

        return Json(new
        {
            success = true,
            primaryHue = primary.MeanHue,
            primaryHex = HueToHex(primary.MeanHue, Math.Max(0.5f, primary.MeanSat), 0.5f),
            families = families.OrderByDescending(f => f.Percent).Select(f => new
            {
                family = f.Family,
                hue = f.MeanHue,
                hex = HueToHex(f.MeanHue, Math.Max(0.35f, f.MeanSat), Math.Clamp(f.MeanLightness, 0.25f, 0.7f)),
                percent = f.Percent,
                sat = f.MeanSat,
                light = f.MeanLightness,
            }),
        });
    }

    // HSL (h in degrees, s/l in 0..1) → #rrggbb, for the picker swatches.
    private static string HueToHex(float h, float s, float l)
    {
        h = ((h % 360f) + 360f) % 360f;
        float c = (1 - Math.Abs(2 * l - 1)) * s;
        float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        float m = l - c / 2;
        float r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        int R = (int)Math.Round((r + m) * 255), G = (int)Math.Round((g + m) * 255), B = (int)Math.Round((b + m) * 255);
        return $"#{R:x2}{G:x2}{B:x2}";
    }

    /// <summary>
    /// GET /RetextureEngine/Sheet?displayId=&cell=&ladder=&value=&vSigma=...
    /// The theory x tier contact sheet, value-aware. Same layout as the proven
    /// /Items/TheorySheet, but every cell honours the value knobs so you can
    /// survey theories under keep vs invert in one PNG.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Sheet(
        uint displayId, int cell = 128, bool ladder = false,
        string? value = null, float? vSigma = null, float? vDetail = null,
        float? vKnee = null, float? vFloor = null, float? vBlend = null,
        int? vAlpha = null, bool? vScale = null)
    {
        var ct = HttpContext.RequestAborted;
        var (srcPng, err) = await _support.ResolvePrimarySourceAsync(displayId, ct);
        if (srcPng == null) return Json(new { success = false, error = err });

        var vset = ParseValue(value, vSigma, vDetail, vKnee, vFloor, vBlend, vAlpha, vScale);
        string[] tiers = { "improved", "power", "glory", "gods" };
        var theories = PaletteSwapService.RecolorTheories;

        var outDir = Path.Combine(_env.WebRootPath, "item_textures_cache", CacheDir);
        Directory.CreateDirectory(outDir);

        int cols = tiers.Length + 1;               // +1 for the original
        int rows = theories.Length;
        int label = 84, pad = 6, header = 44;
        int W = label + cols * (cell + pad) + pad;
        int H = header + rows * (cell + pad) + pad;

        using var sheet = new SkiaSharp.SKBitmap(W, H);
        using var canvas = new SkiaSharp.SKCanvas(sheet);
        canvas.Clear(new SkiaSharp.SKColor(24, 24, 28));
        using var text = new SkiaSharp.SKPaint
        { Color = SkiaSharp.SKColors.White, IsAntialias = true };
        using var font = new SkiaSharp.SKFont { Size = 13 };

        var fams = _palette.DetectFamilies(srcPng);
        var chromaticFams = fams.Where(f => f.Family != "white" && f.Family != "black").ToList();
        string famLine = $"families: {string.Join(", ", fams.Select(f => $"{f.Family} {f.Percent:F0}%"))}";
        if (chromaticFams.Count <= 1)
            famLine += "   [SINGLE CHROMATIC FAMILY - theories degenerate; judge on a multi-family item]";
        string vLine = vset.IsInvert
            ? $"   value: INVERT (sigma {(vSigma ?? 2.5f):0.0}, detail {(vDetail ?? 1.0f):0.0})"
            : "   value: keep";
        canvas.DrawText(famLine + vLine, label + pad + (cell + pad), 14, SkiaSharp.SKTextAlign.Left, font, text);

        canvas.DrawText("original", label + pad, header - 8, SkiaSharp.SKTextAlign.Left, font, text);
        for (int t = 0; t < tiers.Length; t++)
            canvas.DrawText(tiers[t], label + pad + (t + 1) * (cell + pad), header - 8, SkiaSharp.SKTextAlign.Left, font, text);

        using (var orig = SkiaSharp.SKBitmap.Decode(srcPng))
        {
            for (int r = 0; r < rows; r++)
            {
                string theory = theories[r];
                canvas.DrawText(theory, pad, header + r * (cell + pad) + cell / 2, SkiaSharp.SKTextAlign.Left, font, text);

                var oRect = SkiaSharp.SKRect.Create(label + pad, header + r * (cell + pad), cell, cell);
                if (orig != null) canvas.DrawBitmap(orig, oRect);

                for (int t = 0; t < tiers.Length; t++)
                {
                    var (kd, ku, m, pop) = RetextureSupport.TierShape(tiers[t]);
                    var (budget, leash) = RetextureSupport.TierPolicy(tiers[t]);
                    int seed = ladder ? RetextureSupport.SeedFor((int)displayId, "")
                                      : RetextureSupport.SeedFor((int)displayId, tiers[t]);
                    string vTag = vset.IsInvert ? "inv" : "keep";
                    string cellPng = Path.Combine(outDir,
                        $"cell_{displayId}_{theory}_{tiers[t]}_{(ladder ? "L" : "R")}_{vTag}.png");

                    var ok = await _palette.RecolorSeededAsync(
                        srcPng, cellPng, seed, 1.0f, 0.0f, false, ct,
                        theory, kd, ku, m, pop, budget, leash, vset);
                    if (ok == null) continue;

                    using var bmp = SkiaSharp.SKBitmap.Decode(cellPng);
                    if (bmp == null) continue;
                    var rect = SkiaSharp.SKRect.Create(
                        label + pad + (t + 1) * (cell + pad),
                        header + r * (cell + pad), cell, cell);
                    canvas.DrawBitmap(bmp, rect);
                }
            }
        }

        string vSuffix = vset.IsInvert ? "_inv" : "";
        string sheetFile = $"sheet_{displayId}{(ladder ? "_ladder" : "")}{vSuffix}.png";
        string sheetPath = Path.Combine(outDir, sheetFile);
        using (var fs = System.IO.File.Create(sheetPath))
            sheet.Encode(fs, SkiaSharp.SKEncodedImageFormat.Png, 95);

        return Json(new
        {
            success = true,
            url = $"/item_textures_cache/{CacheDir}/{sheetFile}?t={DateTime.UtcNow.Ticks}",
            chromaticFamilies = chromaticFams.Count,
            theories,
            value = vset.IsInvert ? "invert" : "keep",
            note = "rows = theories, columns = original + tiers; same seeds the queue would use",
        });
    }

    /// <summary>
    /// GET /RetextureEngine/PreviewOnModel?displayId=&theory=&tier=&ladder=&value=...
    /// Produces the RETEXTURED assets for the 3D viewer: recolored atlas slots
    /// (painted armor) OR a recolored + baked GLB (weapon/model). The JS feeds
    /// these to equip.equipBodyAtlasRetextureDirect / equipWeaponGlbDirect.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> PreviewOnModel(
        uint displayId, string theory = "fan", string tier = "improved", bool ladder = false,
        string? value = null, float? vSigma = null, float? vDetail = null,
        float? vKnee = null, float? vFloor = null, float? vBlend = null,
        int? vAlpha = null, bool? vScale = null, float? hue = null)
    {
        var ct = HttpContext.RequestAborted;
        if (Array.IndexOf(PaletteSwapService.RecolorTheories, theory) < 0) theory = "fan";
        var shape = RetextureSupport.TierShape(tier);
        var policy = RetextureSupport.TierPolicy(tier);
        int seed = ladder ? RetextureSupport.SeedFor((int)displayId, "")
                          : RetextureSupport.SeedFor((int)displayId, tier);
        var vset = ParseValue(value, vSigma, vDetail, vKnee, vFloor, vBlend, vAlpha, vScale);
        string vTag = vset.IsInvert ? "invert" : "keep";

        // Painted armor: recolor every atlas slot with one seed (coherent piece).
        var slots = await _support.RecolorAtlasSlotsAsync(
            displayId, seed, theory, shape, policy, vset, "retexture_engine_model", ct, hue);
        if (slots != null)
            return Json(new { success = true, kind = "atlas", slotUrls = slots, value = vTag });

        // Weapon / model item: recolor the DBC texture and bake its preview GLB assets.
        var assets = await _support.RecolorModelGlbAsync(
            displayId, seed, theory, shape, policy, vset, "retexture_engine_model", ct, hue);
        if (assets != null)
            return Json(new
            {
                success = true,
                kind = "weapon",
                glbUrl = assets.GlbUrl,
                attachments = assets.Attachments,
                value = vTag
            });

        return Json(new { success = false, error = "no atlas slots and no recolorable model texture for this display" });
    }

    // ══════════════════════════════════════════════════════════════════════
    //  LOOTIFIER QUEUE
    //
    //  Every verb takes an optional `source` ("quest" | "crafting" | "loot");
    //  omitted or unrecognised means all sources. That single parameter is what
    //  makes "re-retexture only the ARPG items" expressible.
    //
    //  The three that did not exist before:
    //    ResetQueue  mode=all   re-arm done rows            -> re-retexture
    //    RevertQueue             base_display_id restored   -> the actual undo
    //    PurgeOrphans            drop unreferenced displays -> shrink the patch
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>GET /RetextureEngine/Sources — per-lootifier counts for the batch panel.</summary>
    [HttpGet]
    public async Task<IActionResult> Sources()
    {
        try { return Json(await _support.LootifierSourcesAsync()); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetextureEngine/Sources failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>POST /RetextureEngine/BuildQueue — one job per (base x tier) for the
    /// selected sources. Body: { sources:["loot"], requeue?: bool }.</summary>
    [HttpPost]
    public async Task<IActionResult> BuildQueue([FromBody] JsonElement body)
    {
        var sources = new List<string>();
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("sources", out var sEl) && sEl.ValueKind == JsonValueKind.Array)
            foreach (var s in sEl.EnumerateArray())
            {
                var v = s.GetString();
                if (!string.IsNullOrWhiteSpace(v)) sources.Add(v!);
            }

        bool requeue = body.ValueKind == JsonValueKind.Object
                       && body.TryGetProperty("requeue", out var rq) && rq.ValueKind == JsonValueKind.True;

        try { return Json(await _support.BuildQueueAsync(sources, requeue)); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetextureEngine/BuildQueue failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>GET /RetextureEngine/QueueStatus?source= — counts + recent failures.</summary>
    [HttpGet]
    public async Task<IActionResult> QueueStatus(string? source = null)
    {
        try { return Json(await _support.QueueStatusAsync(source)); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetextureEngine/QueueStatus failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>POST /RetextureEngine/ProcessQueue — drain the lootifier retexture
    /// queue under the CHOSEN theory + value, optionally scoped to ONE lootifier.
    /// Body: { max, source?, theory, value, vSigma... }.</summary>
    [HttpPost]
    public async Task<IActionResult> ProcessQueue([FromBody] JsonElement body)
    {
        int max = body.ValueKind == JsonValueKind.Object
                  && body.TryGetProperty("max", out var m) && m.TryGetInt32(out var mv) ? Math.Clamp(mv, 1, 25) : 3;
        var res = await _support.ProcessQueueAsync(
            TheoryFromBody(body), ValueFromBody(body), max, SourceFromBody(body), HttpContext.RequestAborted);
        return Json(res);
    }

    /// <summary>POST /RetextureEngine/ResetQueue — Body: { source?, mode }.
    ///
    ///   failed  requeue this source's failures (the old Reset behaviour).
    ///   all     re-arm every done/failed/reverted row — RE-RETEXTURE. Keeps
    ///           new_display_id, so the drain recycles the display it minted last
    ///           time instead of orphaning it.
    ///   clear   delete the rows. Applied retextures STAY applied; clearing the
    ///           queue is NOT an undo. RevertQueue is the undo.</summary>
    [HttpPost]
    public async Task<IActionResult> ResetQueue([FromBody] JsonElement body)
    {
        string mode = body.ValueKind == JsonValueKind.Object
                      && body.TryGetProperty("mode", out var md) && md.ValueKind == JsonValueKind.String
            ? (md.GetString() ?? "failed") : "failed";

        try { return Json(await _support.ResetQueueAsync(SourceFromBody(body), mode)); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetextureEngine/ResetQueue failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>POST /RetextureEngine/RevertQueue — put this source's variants back on
    /// their ORIGINAL display (base_display_id), delete the displays minted for them,
    /// rebuild patch-4.MPQ. Body: { source?, requeue?: bool } — requeue leaves the rows
    /// pending so you can run again from clean.</summary>
    [HttpPost]
    public async Task<IActionResult> RevertQueue([FromBody] JsonElement body)
    {
        bool requeue = body.ValueKind == JsonValueKind.Object
                       && body.TryGetProperty("requeue", out var rq) && rq.ValueKind == JsonValueKind.True;

        try { return Json(await _support.RevertQueueAsync(SourceFromBody(body), requeue)); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetextureEngine/RevertQueue failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>POST /RetextureEngine/PurgeOrphans — minted displays nothing points at
    /// any more (re-run debris, rolled-back lootifier items). Body: { apply?: bool },
    /// default false = dry run.</summary>
    [HttpPost]
    public async Task<IActionResult> PurgeOrphans([FromBody] JsonElement body)
    {
        bool apply = body.ValueKind == JsonValueKind.Object
                     && body.TryGetProperty("apply", out var a) && a.ValueKind == JsonValueKind.True;

        try { return Json(await _support.PurgeOrphansAsync(apply)); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetextureEngine/PurgeOrphans failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// GET|HEAD /RetextureEngine/DownloadPatch[?file=patch-4.MPQ]
    /// Hand over the archive this section builds. wwwroot is ephemeral, so a GET
    /// for the current patch rebuilds it from the DB when the file is missing —
    /// a HEAD probe does not. Deploying to the REAL client is still a manual copy
    /// into C:\WoW Vanilla\Data\; the WSL folder the rebuild auto-copies to is
    /// not what the game reads.
    /// </summary>
    [HttpGet]
    [HttpHead]
    public IActionResult DownloadPatch(string? file = null)
    {
        // Retextures ship in the unified patch now, alongside forged weapons and armor, so this
        // lane no longer has an archive of its own to serve. One file to install, one download.
        return RedirectToAction("DownloadPatch", "UnifiedPatch");
    }

    /// <summary>GET /RetextureEngine/PatchStatus — can a patch be produced, and
    /// what is on disk right now (size / build time for the download button).</summary>
    [HttpGet]
    public async Task<IActionResult> PatchStatus()
    {
        try { return Json(await _support.PatchStatusAsync()); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetextureEngine/PatchStatus failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>POST /RetextureEngine/RebuildPatch — force a patch-4.MPQ rebuild from
    /// the retexture tables. The drain does this automatically when the queue empties;
    /// this is the escape hatch after an interrupted run.</summary>
    [HttpPost]
    public async Task<IActionResult> RebuildPatch()
    {
        try { return Json(await _support.RebuildPatchAsync()); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetextureEngine/RebuildPatch failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>POST /RetextureEngine/RetextureSelection — retexture an ad-hoc
    /// selection (single or many) at the chosen theory + value, one tier or all,
    /// asSet=true for a shared colourway. Body: { items:[entry...], tiers:[...], theory, value, asSet }.</summary>
    [HttpPost]
    public async Task<IActionResult> RetextureSelection([FromBody] JsonElement body)
    {
        var items = new List<int>();
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("items", out var itEl) && itEl.ValueKind == JsonValueKind.Array)
            foreach (var e in itEl.EnumerateArray())
                if (e.TryGetInt32(out var v)) items.Add(v);

        var tiers = new List<string>();
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("tiers", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
            foreach (var t in tEl.EnumerateArray())
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s)) tiers.Add(s!);
            }

        bool asSet = body.ValueKind == JsonValueKind.Object
                     && body.TryGetProperty("asSet", out var a) && a.ValueKind == JsonValueKind.True;

        var res = await _support.RetextureSelectionAsync(
            items, tiers, TheoryFromBody(body), ValueFromBody(body), asSet, HttpContext.RequestAborted);
        return Json(res);
    }

    private static string TheoryFromBody(JsonElement body)
    {
        string theory = body.ValueKind == JsonValueKind.Object
                        && body.TryGetProperty("theory", out var th) && th.ValueKind == JsonValueKind.String
            ? (th.GetString() ?? "") : "";
        return Array.IndexOf(PaletteSwapService.RecolorTheories, theory) >= 0 ? theory : "fan";
    }

    /// <summary>"quest" | "crafting" | "loot"; anything else (or absent) = all sources.</summary>
    private static string? SourceFromBody(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
            ? RetextureSupport.NormalizeSource(s.GetString()) : null;

    private static ValueSettings ValueFromBody(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return ValueSettings.Keep;
        string mode = body.TryGetProperty("value", out var vm) && vm.ValueKind == JsonValueKind.String ? (vm.GetString() ?? "") : "";
        if (!string.Equals(mode, "invert", StringComparison.OrdinalIgnoreCase)) return ValueSettings.Keep;
        float F(string k, float d) => body.TryGetProperty(k, out var e) && e.TryGetSingle(out var f) ? f : d;
        int I(string k, int d) => body.TryGetProperty(k, out var e) && e.TryGetInt32(out var i) ? i : d;
        bool B(string k, bool d) => body.TryGetProperty(k, out var e)
            ? (e.ValueKind == JsonValueKind.True ? true : e.ValueKind == JsonValueKind.False ? false : d) : d;
        return ValueSettings.Invert(F("vSigma", 2.5f), F("vDetail", 1.0f), F("vKnee", 0.40f), F("vFloor", 0.25f),
            F("vBlend", 1.0f), I("vAlpha", 16), B("vScale", true));
    }

    /// <summary>
    /// GET /RetextureEngine/Browse — the item list, filtered ENTIRELY server-side.
    ///
    /// Replaces the old "call /Items/Search then throw rows away in JS" pattern,
    /// which paginated before filtering and so produced empty pages that the
    /// pager still counted. Here totalCount/totalPages describe exactly the rows
    /// that render, and `page` comes back clamped to the real range.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Browse(
        string? q, int? classFilter, int? subclassFilter, int? qualityFilter,
        int? inventoryTypeFilter, int? minLevel, int? maxLevel,
        bool retexOnly = true, int page = 1, int pageSize = 40)
    {
        try
        {
            return Json(await _support.BrowseAsync(
                q, classFilter, subclassFilter, qualityFilter,
                inventoryTypeFilter, minLevel, maxLevel, retexOnly, page, pageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetextureEngine/Browse failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>GET /RetextureEngine/GeneratedEntries — ids the UI hides so browse lists bases.</summary>
    [HttpGet]
    public async Task<IActionResult> GeneratedEntries()
        => Json(new { success = true, entries = await _support.GeneratedEntriesAsync() });

    /// <summary>GET /RetextureEngine/BaseVariants?entry= — a base's tier lineup for the strip.</summary>
    [HttpGet]
    public async Task<IActionResult> BaseVariants(int entry)
        => Json(await _support.BaseVariantsAsync(entry));

    /// <summary>GET /RetextureEngine/ItemSources?entry= — creature drops / vendors / quest rewards.</summary>
    [HttpGet]
    public async Task<IActionResult> ItemSources(int entry)
        => Json(await _support.ItemSourcesAsync(entry));

    /// <summary>GET /RetextureEngine/SourceTexture?displayId= — the display's current (committed) texture, no recolor.</summary>
    [HttpGet]
    public async Task<IActionResult> SourceTexture(uint displayId)
        => Json(await _support.SourceTextureAsync(displayId));
}
