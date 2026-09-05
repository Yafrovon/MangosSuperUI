using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using SkiaSharp;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using MangosSuperUI.Services.Itemization;
using MangosSuperUI.Services.WeaponForge;
using MangosSuperUI.Services.WeaponForge.Motion;
using MangosSuperUI.Services.WeaponForge.RawM2;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

/// <summary>
/// Weapon Forge (WEAPON_GEN.md) HTTP surface — the IMPORT page. It accepts a finished,
/// pre-textured GLB (UVs + embedded texture authored elsewhere), decimates it to a game budget
/// with the UV-preserving decimator, and packages it through the one proofed path: M2 + BLP
/// compile, world-DB insert + reload, registry entry, and a queued unified patch-4.MPQ rebuild.
///
/// The creation tooling that used to live here (sketch workbench, texture zones, local AI
/// texturing) is archived under Desktop\ItemForgeMSUIFiles.
///
/// Everything here is build/staging only in spirit: forging inserts and deploys via the audited
/// build service; nothing else touches a live server or client.
/// </summary>
public class WeaponForgeController : Controller
{
    // Golden donor fixture paths (WEAPON_GEN.md §13.3).
    private const string DonorM2Path = @"ITEM\ObjectComponents\WEAPON\Sword_1H_Short_A_01.m2";
    private const string DonorBlpPath = @"ITEM\ObjectComponents\WEAPON\Sword_1H_Short_A_01Blue.blp";

    private readonly MpqReaderService _mpq;
    private readonly WeaponPreviewService _preview;
    private readonly CustomWeaponBuildService _builder;
    private readonly GlbWeaponImporter _glbImporter;
    private readonly WeaponDonorResolver _donors;
    private readonly LegacyImportSources _sources;
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly ItemTextureService _itemTextures;
    private readonly VanillaItemSpellCatalog _itemSpells;
    private readonly ItemBudgetGenerator _itemize;
    private readonly PaletteSwapService _palette;
    private readonly BlpWriterService _blp;
    private readonly MangosSuperUI.Services.UnifiedPatch.UnifiedPatchService _unified;
    private readonly ILogger<WeaponForgeController> _logger;

    // High-poly sources are welcome — they are decimated to budget before forging.
    private const long MaxGlbBytes = 128 * 1024 * 1024;   // 128 MB
    // The variable-topology M2 writer's hard ceiling (RigidWeaponMeshValidator.VariableHardCeiling).
    /// <summary>GLB-route triangle ceiling. Raised from 1,000 (2026-08-21): the vanilla client
    /// renders multi-thousand-triangle weapons fine (the TBC route already forges 2–3k-triangle
    /// models through the same writer), and detail-heavy Sketchfab exports lose their gems/pommel
    /// when crushed to a few hundred. Stays far under the UInt16 skin-section capacity.</summary>
    private const int MaxForgeTriangles = 4000;
    // A preserved TBC mesh is bounded by the vanilla view's UInt16 index count, not the Forge's
    // authoring/decimation policy used for arbitrary GLB uploads.
    private const int MaxTbcForgeTriangles = ushort.MaxValue / 3;
    private const int MaxItemConfigurationChars = 64 * 1024;

    private static readonly JsonSerializerOptions ItemConfigurationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public WeaponForgeController(MpqReaderService mpq, WeaponPreviewService preview,
        CustomWeaponBuildService builder, GlbWeaponImporter glbImporter, WeaponDonorResolver donors,
        LegacyImportSources sources, ConnectionFactory db, DbcService dbc,
        ItemTextureService itemTextures, VanillaItemSpellCatalog itemSpells,
        ItemBudgetGenerator itemize, PaletteSwapService palette, BlpWriterService blp,
        MangosSuperUI.Services.UnifiedPatch.UnifiedPatchService unified,
        ILogger<WeaponForgeController> logger)
    {
        _mpq = mpq;
        _unified = unified;
        _preview = preview;
        _builder = builder;
        _glbImporter = glbImporter;
        _donors = donors;
        _sources = sources;
        _db = db;
        _dbc = dbc;
        _itemTextures = itemTextures;
        _itemSpells = itemSpells;
        _itemize = itemize;
        _palette = palette;
        _blp = blp;
        _logger = logger;
    }

    /// <summary>
    /// Deserialize and validate the optional typed Vanilla gameplay contract carried as the
    /// multipart <c>itemConfig</c> JSON field. Unknown JSON properties fail closed so a typo can
    /// never silently produce a donor-default item. Spell ids are checked against the complete
    /// installed Vanilla Spell.dbc when that DBC was available at startup.
    /// </summary>
    private async Task<(ValidatedVanillaItemBuildConfiguration? Configuration, IReadOnlyList<string> Errors)>
        ParseItemConfigurationAsync(string? itemConfig, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemConfig))
            return (null, Array.Empty<string>());

        if (itemConfig.Length > MaxItemConfigurationChars)
            return (null, [$"itemConfig exceeds the {MaxItemConfigurationChars:N0}-character limit."]);

        VanillaItemBuildConfiguration? request;
        try
        {
            request = JsonSerializer.Deserialize<VanillaItemBuildConfiguration>(
                itemConfig, ItemConfigurationJsonOptions);
        }
        catch (JsonException ex)
        {
            string location = ex.Path is { Length: > 0 } ? $" at {ex.Path}" : "";
            return (null, [$"itemConfig is not valid JSON{location}: {ex.Message}"]);
        }

        if (request is null)
            return (null, ["itemConfig must be a JSON object, not null."]);

        Func<uint, bool>? spellExists = _dbc.IsLoaded && _dbc.AllSpellEntries.Count > 0
            ? spellId => _dbc.AllSpellEntries.ContainsKey(spellId)
            : null;
        Func<int, bool>? requiredSkillExists = _dbc.IsLoaded && _dbc.SkillLineIds.Count > 0
            ? id => _dbc.SkillLineIds.Contains((uint)id)
            : null;
        Func<int, bool>? reputationFactionExists = _dbc.IsLoaded && _dbc.FactionIds.Count > 0
            ? id => _dbc.FactionIds.Contains((uint)id)
            : null;

        if (!VanillaItemBuildConfigurationTranslator.TryTranslate(
                request, spellExists, requiredSkillExists, reputationFactionExists,
                out var validated, out var errors))
            return (null, errors);

        if (request.Spells is { Count: > 0 })
        {
            IReadOnlyList<NativeItemSpellUsage> nativeUsage;
            try
            {
                nativeUsage = await _itemSpells.GetUsageAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WeaponForge: could not validate native item spell effects");
                return (null, ["Item spell effects cannot be validated against stock Vanilla items right now."]);
            }

            var nativeErrors = new List<string>();
            for (int i = 0; i < request.Spells.Count; i++)
            {
                var spell = request.Spells[i]!; // structural/null validation already passed above
                uint spellId = (uint)spell.SpellId!.Value;
                int trigger = spell.Trigger!.Value;
                int charges = spell.Charges ?? 0;
                float ppmRate = spell.PpmRate ?? 0;
                int cooldownMs = spell.CooldownMs ?? -1;
                int category = spell.Category ?? 0;
                int categoryCooldownMs = spell.CategoryCooldownMs ?? -1;
                bool exactStockSlot = nativeUsage.Any(x =>
                    x.SpellId == spellId &&
                    x.TriggerValue == trigger &&
                    x.Charges == charges &&
                    x.PpmRate == ppmRate &&
                    x.CooldownMs == cooldownMs &&
                    x.Category == category &&
                    x.CategoryCooldownMs == categoryCooldownMs);
                if (!exactStockSlot)
                    nativeErrors.Add($"spells[{i}] must preserve a complete stock Vanilla item-spell slot; " +
                        $"spell {spellId} is not available with that {ItemSpellTriggerLabel(trigger)}, charges, PPM, and cooldown combination.");
            }
            if (nativeErrors.Count > 0)
                return (null, nativeErrors);
        }

        return (validated, Array.Empty<string>());
    }

    private static string ItemSpellTriggerLabel(int trigger) => trigger switch
    {
        0 => "Use",
        1 => "On Equip",
        2 => "Chance on Hit",
        _ => $"trigger {trigger}"
    };

    private static IReadOnlyList<string> ValidateConfigurationForWeaponFamily(
        WeaponTypeProfile profile, ValidatedVanillaItemBuildConfiguration? configured)
    {
        if (configured is null ||
            !configured.Overrides.TryGetValue("inventory_type", out var rawInventoryType) ||
            !int.TryParse(rawInventoryType, out int inventoryType))
            return Array.Empty<string>();

        if (profile.AllowedInventoryTypes.Contains(inventoryType)) return Array.Empty<string>();
        return [$"inventoryType is incompatible with {profile.Label}; choose {profile.AllowedInventoryTypesLabel}."];
    }

    private static Dictionary<string, string>? MergeItemOverrides(
        Dictionary<string, string>? existing,
        ValidatedVanillaItemBuildConfiguration? configured)
    {
        if (configured is null || configured.Overrides.Count == 0)
            return existing;

        existing ??= new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (column, value) in configured.Overrides)
            existing[column] = value;
        return existing;
    }

    /// <summary>Uniform response for every full weapon build: ids, hashes, direct downloads for the
    /// straight patch MPQ and the item SQL (no ZIP), preview, grip markers, and diagnostics.</summary>
    private object BuildResultJson(CustomWeaponBuildResult r, object? grip = null) => new
    {
        ok = true,
        r.BuildId,
        r.ItemEntry,
        r.DisplayId,
        r.ModelIndex,
        r.Name,
        r.WeaponType,
        r.WeaponTypeLabel,
        r.InventoryType,
        r.InventoryTypeLabel,
        r.SourceKind,
        r.ModelMember,
        r.TextureMember,
        r.MpqSha256,
        r.DbcSha256,
        r.SqlSha256,
        r.AllMembersVerified,
        r.PackagedWeaponCount,
        r.SkippedWeaponCount,
        r.PreviewGlbWebPath,
        r.TriangleCount,
        r.VertexCount,
        mpqDownloadUrl = $"/WeaponForge/DownloadBuild?build={Uri.EscapeDataString(r.BuildDirName)}&file={CustomWeaponBuildService.PatchFileName}",
        sqlDownloadUrl = $"/WeaponForge/DownloadBuild?build={Uri.EscapeDataString(r.BuildDirName)}&file=item_template.sql",
        grip,
        apply = r.Apply,
        diagnostics = r.Diagnostics,
    };

    /// <summary>Grip-marker payload for the viewer, computed on the final normalized mesh. The
    /// main-hand band sits at the model origin — that is exactly where the client's hand bone
    /// mounts the weapon, so it is precise. The off-hand band (two-handers only) is an approximate
    /// zone: the character animation places the second hand, not the weapon file.</summary>
    private static object BuildGripInfo(RigidWeaponMesh mesh, WeaponTypeProfile profile, WeaponDonorInfo donor)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        foreach (var p in mesh.Positions)
        {
            minX = MathF.Min(minX, p.X);
            maxX = MathF.Max(maxX, p.X);
        }
        float len = MathF.Max(maxX - minX, 1e-6f);

        // Largest cross-section radius near an X station, for sizing the band around the shaft.
        float RadiusAt(float station)
        {
            float best = 0f;
            int hits = 0;
            float halfWindow = len * 0.06f;
            foreach (var p in mesh.Positions)
            {
                if (MathF.Abs(p.X - station) > halfWindow) continue;
                best = MathF.Max(best, MathF.Sqrt(p.Y * p.Y + p.Z * p.Z));
                hits++;
            }
            if (hits == 0)
                foreach (var p in mesh.Positions)
                    best = MathF.Max(best, MathF.Sqrt(p.Y * p.Y + p.Z * p.Z));
            return best;
        }

        object? secondHand = null;
        if (profile.SecondHandFraction is { } fraction)
        {
            // The off-hand grips the handle BEHIND the main hand (toward the pommel/butt), i.e. at
            // negative X in weapon space — never out on the guard or blade. Keep the band on the
            // geometry that actually exists behind the palm (a model whose origin sits near its
            // back end has almost no handle there; the band then hugs what little there is).
            float behind = MathF.Max(0f, -minX);
            float x2 = -MathF.Min(fraction * len, behind * 0.85f);
            secondHand = new { x = x2, radius = RadiusAt(x2) };
        }

        return new
        {
            type = profile.Key,
            label = profile.Label,
            twoHanded = profile.TwoHanded,
            palm = new { x = 0f, radius = RadiusAt(0f) },
            secondHand,
            minX,
            maxX,
            extent = donor.ExtentX,
            palmBackFraction = donor.PalmBackFraction,
            note = (profile.IsShield
                       ? "Green band = forearm strap (model origin, exact) — shields hang from the left forearm at the centre of the face."
                       : profile.IsRanged
                       ? "Green band = hand on the weapon (model origin, exact) — bows grip at the centre of the limbs, guns/crossbows at the trigger grip, thrown/wands at the handle."
                       : "Green band = main-hand palm (model origin, exact).") +
                   (secondHand is not null ? " Blue band ≈ off-hand on the handle behind the main hand (the character animation places it; approximate)." : ""),
        };
    }

    /// <summary>The unified patch-4.MPQ as the page sees it: built / deployed / stale, plus how many
    /// forges and deletes are queued for the next Rebuild patch click.</summary>
    private object DeployedPatchJson()
    {
        try
        {
            var st = _unified.DeployStatus();
            return new
            {
                configured = st.Configured, built = st.Built, deployedExists = st.Deployed, stale = st.Stale,
                pending = st.Pending, pendingReasons = st.PendingReasons, message = st.Message,
            };
        }
        catch (Exception ex) { return new { configured = false, built = false, deployedExists = false, stale = false, pending = 0, pendingReasons = Array.Empty<string>(), message = ex.Message }; }
    }

    /// <summary>GET /WeaponForge — the Item Assets import page (Game Development).</summary>
    [HttpGet]
    public IActionResult Index() => View();

    /// <summary>GET /WeaponForge/Status — foundation self-checks: fixture integrity, contract
    /// version, whether the golden donor resolves from the mounted archives, and per-family
    /// donor resolution (which stock model each weapon type will scaffold on).</summary>
    [HttpGet]
    public IActionResult Status()
    {
        var donorM2 = SafeExtract(DonorM2Path);
        var donorBlp = SafeExtract(DonorBlpPath);

        var types = WeaponTypeCatalog.All.Select(p =>
        {
            try
            {
                var d = _donors.Resolve(p);
                return new
                {
                    key = p.Key,
                    label = p.Label,
                    inventoryType = p.InventoryType,
                    inventoryTypeLabel = CustomWeaponBuildService.InventoryTypeLabel(p.InventoryType),
                    twoHanded = p.TwoHanded,
                    isRanged = p.IsRanged,
                    isShield = p.IsShield,
                    armor = p.Armor,
                    block = p.Block,
                    glbImport = p.GlbImportSupported,
                    allowedInventoryTypes = p.AllowedInventoryTypes,
                    ok = true,
                    donorModel = (string?)d.ModelName,
                    donorDisplayRow = d.DisplayRow,
                    measureModel = d.MeasureModelName,
                    measureDisplayRow = d.MeasureDisplayRow,
                    spellVisualId = d.SpellVisualId,
                    mirrorModelName2 = d.MirrorModelName2,
                    extent = d.ExtentX,
                    palmBackFraction = d.PalmBackFraction,
                    orientation = (string?)d.Orientation.ToString(),
                    error = (string?)null,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning("WeaponForge: donor resolution for {Type} failed: {Error}", p.Key, ex.Message);
                return new
                {
                    key = p.Key,
                    label = p.Label,
                    inventoryType = p.InventoryType,
                    inventoryTypeLabel = CustomWeaponBuildService.InventoryTypeLabel(p.InventoryType),
                    twoHanded = p.TwoHanded,
                    isRanged = p.IsRanged,
                    isShield = p.IsShield,
                    armor = p.Armor,
                    block = p.Block,
                    glbImport = p.GlbImportSupported,
                    allowedInventoryTypes = p.AllowedInventoryTypes,
                    ok = false,
                    donorModel = (string?)null,
                    donorDisplayRow = 0u,
                    measureModel = (string?)null,
                    measureDisplayRow = (uint?)null,
                    spellVisualId = 0u,
                    mirrorModelName2 = false,
                    extent = 0f,
                    palmBackFraction = 0f,
                    orientation = (string?)null,
                    error = (string?)ex.Message,
                };
            }
        }).ToArray();

        return Json(new
        {
            fixtureVerified = DonorItemTemplateFixture.Verify(),
            fixtureSha = DonorItemTemplateFixture.ExpectedSha256,
            coordinateContractVersion = CoordinateContract.Version,
            deployedPatch = DeployedPatchJson(),
            donorM2Found = donorM2 is not null,
            donorM2Bytes = donorM2?.Length ?? 0,
            donorBlpFound = donorBlp is not null,
            donorBlpBytes = donorBlp?.Length ?? 0,
            weaponTypes = types,
            note = "Build/staging only. No SQL/patch is applied to any live server or client.",
        });
    }

    /// <summary>GET /WeaponForge/InspectDonor — run the lossless raw M2 inspector on the golden
    /// donor and confirm the byte-exact round trip. Proves the Phase-0 inspector on real bytes.</summary>
    [HttpGet]
    public IActionResult InspectDonor()
    {
        var m2 = SafeExtract(DonorM2Path);
        if (m2 is null) return NotFound(new { error = $"Donor M2 not found in mounted archives: {DonorM2Path}" });

        var doc = RawM2Document.Parse(m2, out var err);
        if (doc is null) return Json(new { ok = false, error = err });

        var report = RawM2Inspector.Inspect(doc);
        bool roundTrips = RawM2Inspector.RoundTripsExact(m2);
        return Json(new { ok = true, roundTripsExact = roundTrips, report });
    }

    /// <summary>GET /WeaponForge/PreviewDonor — extract the donor M2 + BLP and render a preview GLB
    /// from the raw bytes (content-hash addressed, no display-id lookup).</summary>
    [HttpGet]
    public IActionResult PreviewDonor()
    {
        var m2 = SafeExtract(DonorM2Path);
        if (m2 is null) return NotFound(new { error = $"Donor M2 not found: {DonorM2Path}" });
        var blp = SafeExtract(DonorBlpPath);

        var result = _preview.RenderFromBytes(m2, blp);
        return Json(result);
    }

    private static readonly string[] DownloadableBuildFiles =
        { CustomWeaponBuildService.PatchFileName, "item_template.sql", "manifest.json", "validation-report.md", "OWNER_CHECKLIST.md" };

    /// <summary>GET /WeaponForge/DownloadBuild?build=weapon-build-xxx&amp;file=patch-4.MPQ — serves one
    /// file from a prepared build directory. Pure read: never rebuilds or deploys anything.</summary>
    [HttpGet]
    public IActionResult DownloadBuild(string build, string file)
    {
        var safeBuild = Path.GetFileName(build ?? "");
        var safeFile = Path.GetFileName(file ?? "");
        if (!safeBuild.StartsWith("weapon-build-", StringComparison.Ordinal) ||
            !DownloadableBuildFiles.Contains(safeFile, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = "Invalid build or file name." });

        var fullPath = Path.Combine(_builder.ArtifactRoot, safeBuild, safeFile);
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { error = $"Not found: {safeBuild}/{safeFile}" });

        string contentType = Path.GetExtension(safeFile).ToLowerInvariant() switch
        {
            ".mpq" => "application/octet-stream",
            ".sql" => "text/plain",
            ".json" => "application/json",
            ".md" => "text/markdown",
            _ => "application/octet-stream",
        };
        return PhysicalFile(fullPath, contentType, safeFile);
    }

    /// <summary>GET /WeaponForge/DownloadPatch — the one deployable archive. Forged weapons ship in
    /// the unified patch alongside retextures and armor, so this redirects rather than serving the
    /// stale weapon-only artifact: there is exactly one file to install.</summary>
    [HttpGet]
    public IActionResult DownloadPatch()
    {
        return RedirectToAction("DownloadPatch", "UnifiedPatch");
    }

    /// <summary>GET /WeaponForge/ListWeapons — the Forge's inventory: every weapon currently
    /// recorded in the registry (and therefore packaged into the unified patch).</summary>
    [HttpGet]
    public async Task<IActionResult> ListWeapons()
    {
        try
        {
            var weapons = await _builder.ListWeaponsAsync();
            return Json(new
            {
                ok = true,
                weapons = weapons.Select(w => new
                {
                    w.DisplayId,
                    w.ItemEntry,
                    w.Name,
                    weaponType = w.WeaponType,
                    weaponTypeLabel = w.WeaponType is null ? null : WeaponTypeCatalog.Get(w.WeaponType).Label,
                    inventoryType = w.InventoryType,
                    inventoryTypeLabel = w.InventoryTypeLabel,
                    w.SourceKind,
                    w.ModelMpqPath,
                    w.BuildId,
                    createdAt = w.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                }),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeaponForge: ListWeapons failed");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>POST /WeaponForge/DeleteWeapon?displayId= — remove one forged weapon EVERYWHERE:
    /// registry, world-DB item row (+reload), and the deployed patch (repackaged without it). The
    /// weapon's ids are released for reuse; the audit log keeps the history.</summary>
    [HttpPost]
    public async Task<IActionResult> DeleteWeapon(long displayId)
    {
        try
        {
            var result = await _builder.DeleteWeaponAsync(displayId);
            return Json(new
            {
                ok = true,
                deleted = result.Deleted,
                weaponsRemaining = result.Rebuild.WeaponCount,
                patchRemoved = result.Rebuild.PatchRemoved,
                mpqSha256 = result.Rebuild.MpqSha256,
                itemRowDeleted = result.ItemRowDeleted,
                itemRowMessage = result.ItemRowMessage,
                reloaded = result.Reloaded,
                reloadMessage = result.ReloadMessage,
                patchDeployed = result.Rebuild.PatchDeployed,
                patchDeployMessage = result.Rebuild.PatchDeployMessage,
                patchQueued = result.Rebuild.PatchQueued,
                patchPending = result.Rebuild.PatchPending,
                patchDownloadUrl = result.Rebuild.PatchRemoved ? null : "/WeaponForge/DownloadPatch",
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { ok = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeaponForge: DeleteWeapon {DisplayId} failed", displayId);
            return Json(new { ok = false, error = ex.Message });
        }
    }

    public sealed class BulkDeleteRequest { public List<long> DisplayIds { get; set; } = new(); }

    /// <summary>POST /WeaponForge/DeleteWeapons — delete a selection of forged weapons with one
    /// reload, one lane repack and one queued unified rebuild. Body: <c>{ "displayIds": [..] }</c>.</summary>
    [HttpPost]
    public async Task<IActionResult> DeleteWeapons([FromBody] BulkDeleteRequest req)
    {
        if (req?.DisplayIds is not { Count: > 0 }) return BadRequest(new { ok = false, error = "No weapons selected." });
        try
        {
            var r = await _builder.DeleteWeaponsAsync(req.DisplayIds);
            return Json(new
            {
                ok = true,
                deleted = r.Deleted.Select(w => new { w.DisplayId, w.ItemEntry, w.Name }),
                failed = r.Failed,
                weaponsRemaining = r.Rebuild.WeaponCount,
                patchRemoved = r.Rebuild.PatchRemoved,
                reloaded = r.Reloaded,
                reloadMessage = r.ReloadMessage,
                patchDeployed = r.Rebuild.PatchDeployed,
                patchDeployMessage = r.Rebuild.PatchDeployMessage,
                patchQueued = r.Rebuild.PatchQueued,
                patchPending = r.Rebuild.PatchPending,
                patchDownloadUrl = r.Rebuild.PatchRemoved ? null : "/WeaponForge/DownloadPatch",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeaponForge: bulk delete failed");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>POST /WeaponForge/RebuildPatch — repackage the unified patch-4.MPQ from current DB
    /// state (every lane), deploy it to the client Data folder, and drain the pending-rebuild queue.
    /// This is the ONE place a forge or delete reaches the client.</summary>
    [HttpPost]
    public async Task<IActionResult> RebuildPatch()
    {
        try
        {
            var summary = await _builder.RebuildPatchAsync("manual rebuild from UI");
            return Json(new
            {
                ok = true,
                weaponCount = summary.WeaponCount,
                patchRemoved = summary.PatchRemoved,
                mpqSha256 = summary.MpqSha256,
                patchDeployed = summary.PatchDeployed,
                patchDeployMessage = summary.PatchDeployMessage,
                diagnostics = summary.Diagnostics,
                patchDownloadUrl = summary.PatchRemoved ? null : "/WeaponForge/DownloadPatch",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeaponForge: RebuildPatch failed");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Shared import + decimation front half for preview and forge. The GLB must carry
    /// UV0 and (normally) an embedded texture; targetTriangles ≤ 0 skips decimation. The family
    /// donor supplies the target length and palm-back fraction the normalizer lands on.</summary>
    private (RigidWeaponMesh? Mesh, GlbImportResult Import, int OriginalTriangles, string? Decimation, string? Error)
        ImportAndDecimate(byte[] bytes, WeaponDonorInfo donor, bool reorient, int targetTriangles,
            float rollDegrees, bool flipGripEnd, bool straightenBlade, int bladeProfile,
            GlbShapeControls? shape = null)
    {
        shape ??= new GlbShapeControls();
        var import = _glbImporter.Import(bytes, new GlbImportOptions
        {
            Reorient = reorient,
            TargetExtent = donor.ExtentX,
            PalmBackFraction = donor.PalmBackFraction,
            Orientation = donor.Orientation,
            RollDegrees = rollDegrees,
            FlipGripEnd = flipGripEnd,
            StraightenBlade = straightenBlade,
            BladeProfile = Math.Clamp(bladeProfile, 0, 100) / 100f,
            SizeScale = shape.SizePercent <= 0 ? 1f : Math.Clamp(shape.SizePercent, 25, 400) / 100f,
            LengthScale = shape.LengthPercent <= 0 ? 1f : Math.Clamp(shape.LengthPercent, 25, 400) / 100f,
            WidthScale = shape.WidthPercent <= 0 ? 1f : Math.Clamp(shape.WidthPercent, 25, 400) / 100f,
            DepthScale = shape.DepthPercent <= 0 ? 1f : Math.Clamp(shape.DepthPercent, 25, 400) / 100f,
            FlipUpsideDown = shape.FlipUpsideDown,
            MirrorSide = shape.MirrorSide,
            HeadScale = shape.HeadPercent <= 0 ? 1f : Math.Clamp(shape.HeadPercent, 25, 400) / 100f,
            HaftScale = shape.HaftPercent <= 0 ? 1f : Math.Clamp(shape.HaftPercent, 25, 400) / 100f,
            GripFraction = shape.GripPercent < 0 ? null : Math.Clamp(shape.GripPercent, 0, 100) / 100f,
            OffsetUp = Math.Clamp(shape.OffsetUpCm, -200, 200) / 100f,
            OffsetSide = Math.Clamp(shape.OffsetSideCm, -200, 200) / 100f,
            PitchDegrees = Math.Clamp(shape.PitchDegrees, -90, 90),
            YawDegrees = Math.Clamp(shape.YawDegrees, -90, 90),
        });
        if (!import.Ok || import.Mesh is null)
            return (null, import, 0, null, "GLB import failed — fix the model and retry.");

        var mesh = import.Mesh;
        int original = mesh.TriangleCount;
        string? decimation = null;
        if (targetTriangles > 0 && mesh.TriangleCount > targetTriangles)
        {
            int target = Math.Clamp(targetTriangles, 50, MaxForgeTriangles);
            try
            {
                mesh = UvPreservingDecimator.Decimate(mesh, target, out decimation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WeaponForge: decimation to {Target} failed", target);
                return (null, import, original, null, "Decimation failed: " + ex.Message);
            }
        }
        return (mesh, import, original, decimation, null);
    }

    /// <summary>POST /WeaponForge/UploadGlb (multipart, field "file") — import a finished,
    /// pre-textured GLB (any triangle count), decimate it to the requested budget with the
    /// UV-preserving decimator, and preview the result WITHOUT packaging anything. What you see is
    /// exactly what ForgeGlb builds at the same target.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxGlbBytes)]
    public async Task<IActionResult> UploadGlb(IFormFile? file, string? weaponType = null, bool reorient = true,
        int targetTriangles = 500, float rollDegrees = 0f, bool flipGripEnd = false, bool straightenBlade = false,
        int bladeProfile = 0, int brightness = 0, int saturation = 0, GlbShapeControls? shape = null,
        int itemVisual = 0, int glowStartPercent = 10, int glowEndPercent = 90,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved")
    {
        var (bytes, err) = await ReadBounded(file, MaxGlbBytes);
        if (err is not null) return BadRequest(new { ok = false, error = err });
        if (bytes!.Length < 12 || !(bytes[0] == 'g' && bytes[1] == 'l' && bytes[2] == 'T' && bytes[3] == 'F'))
            return BadRequest(new { ok = false, error = "Not a binary glTF (.glb): missing 'glTF' magic." });

        var profile = WeaponTypeCatalog.Get(weaponType);
        WeaponDonorInfo donor;
        try { donor = _donors.Resolve(profile); }
        catch (Exception ex)
        { return Json(new { ok = false, error = $"No stock donor for {profile.Label}: {ex.Message}" }); }

        var (mesh, import, original, decimation, importErr) =
            ImportAndDecimate(bytes, donor, reorient, targetTriangles, rollDegrees, flipGripEnd, straightenBlade, bladeProfile, shape);
        if (mesh is null)
            return Json(new
            {
                ok = false,
                error = importErr,
                diagnostics = import.Diagnostics.Items.Select(i => i.ToString()),
            });

        // Pre-import recolor preview — same palette engine as the lanes, seeded off the source
        // GLB's sha so the forge bakes the identical result.
        byte[]? texturePng = import.TexturePng;
        bool recolorApplied = false;
        if (recolorHue.HasValue && texturePng is { Length: > 0 })
        {
            int rseed = RetextureSupport.SeedFor(GlbRecolorSeed(import.SourceSha256), recolorTier);
            var rp = await RecolorTexturePngAsync(texturePng, rseed, recolorHue.Value, recolorSat, recolorLight, recolorTheory, recolorTier, HttpContext.RequestAborted);
            if (rp is not null) { texturePng = rp; recolorApplied = true; }
            else import.Diagnostics.Warn("recolor.preview", "The embedded texture has no recolorable colour families; showing the original.");
        }

        // The chosen enchant glow (ItemVisual) previews on the same anchor stations the forge
        // writes — spread across the operator's chosen span — so the GLB route shows its glow
        // exactly like the TBC/WotLK lanes do.
        var (glowLo, glowHi) = GlowRange(glowStartPercent, glowEndPercent);
        var preview = _preview.RenderMesh(mesh, AdjustTexture(texturePng, brightness, saturation),
            visualEffects: ResolveVisualEffectsForMesh((uint)Math.Max(itemVisual, 0), mesh, glowLo, glowHi));
        return Json(new
        {
            ok = preview.Ok,
            weaponType = profile.Key,
            weaponTypeLabel = profile.Label,
            vertexCount = mesh.VertexCount,
            triangleCount = mesh.TriangleCount,
            originalTriangleCount = original,
            decimation,
            sourceSha256 = import.SourceSha256,
            hasTexture = import.TexturePng is { Length: > 0 },
            withinForgeBudget = mesh.TriangleCount <= MaxForgeTriangles,
            normalization = mesh.Normalization,
            recolorApplied,
            grip = BuildGripInfo(mesh, profile, donor),
            preview,
            diagnostics = import.Diagnostics.Items.Select(i => i.ToString()),
            note = "Preview only — nothing was packaged. Forge builds this geometry and material into the game.",
        });
    }

    /// <summary>POST /WeaponForge/ForgeGlb (multipart, field "file") — end-to-end: import the
    /// pre-textured GLB, decimate to the requested budget, then package it for real into the
    /// unified patch MPQ. The GLB's embedded texture becomes the weapon's BLP.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxGlbBytes)]
    public async Task<IActionResult> ForgeGlb(IFormFile? file, string? name = null, string? weaponType = null,
        bool reorient = true,
        int targetTriangles = 500, float rollDegrees = 0f, bool flipGripEnd = false, bool straightenBlade = false,
        int bladeProfile = 0, int brightness = 0, int saturation = 0, string? itemConfig = null,
        GlbShapeControls? shape = null, int itemVisual = 0, int glowStartPercent = 10, int glowEndPercent = 90,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved")
    {
        var (configuredItem, configurationErrors) = await ParseItemConfigurationAsync(
            itemConfig, HttpContext.RequestAborted);
        if (configurationErrors.Count > 0)
            return BadRequest(new
            {
                ok = false,
                error = "The Vanilla item configuration is invalid.",
                errors = configurationErrors,
            });

        var (bytes, err) = await ReadBounded(file, MaxGlbBytes);
        if (err is not null) return BadRequest(new { ok = false, error = err });
        if (bytes!.Length < 12 || !(bytes[0] == 'g' && bytes[1] == 'l' && bytes[2] == 'T' && bytes[3] == 'F'))
            return BadRequest(new { ok = false, error = "Not a binary glTF (.glb): missing 'glTF' magic." });

        var profile = WeaponTypeCatalog.Get(weaponType);
        var familyErrors = ValidateConfigurationForWeaponFamily(profile, configuredItem);
        if (familyErrors.Count > 0)
            return BadRequest(new { ok = false, error = "The item configuration does not match the weapon family.", errors = familyErrors });
        WeaponDonorInfo donor;
        try { donor = _donors.Resolve(profile); }
        catch (Exception ex)
        { return Json(new { ok = false, error = $"No stock donor for {profile.Label}: {ex.Message}" }); }

        var (mesh, import, _, decimation, importErr) =
            ImportAndDecimate(bytes, donor, reorient, targetTriangles, rollDegrees, flipGripEnd, straightenBlade, bladeProfile, shape);
        if (mesh is null)
            return Json(new
            {
                ok = false,
                error = importErr,
                diagnostics = import.Diagnostics.Items.Select(i => i.ToString()),
            });
        if (mesh.TriangleCount > MaxForgeTriangles)
            return Json(new
            {
                ok = false,
                error = $"{mesh.TriangleCount:N0} triangles exceeds the M2 budget ({MaxForgeTriangles:N0}). " +
                        "Lower the target-triangles slider — the decimator preserves the UVs and texture.",
            });

        try
        {
            // Bake the previewed recolor into the shipped texture — same engine and sha-derived
            // seed the preview used, so what was on screen is what ships.
            byte[]? texturePng = import.TexturePng;
            bool recolorBaked = false;
            if (recolorHue.HasValue && texturePng is { Length: > 0 })
            {
                int rseed = RetextureSupport.SeedFor(GlbRecolorSeed(import.SourceSha256), recolorTier);
                var rp = await RecolorTexturePngAsync(texturePng, rseed, recolorHue.Value, recolorSat, recolorLight, recolorTheory, recolorTier, HttpContext.RequestAborted);
                if (rp is not null) { texturePng = rp; recolorBaked = true; }
                else import.Diagnostics.Warn("recolor.bake", "The embedded texture has no recolorable colour families; forging the original.");
            }

            // A GLB carries no ItemVisual of its own, so the picker is the only source; 0 = no glow,
            // which is the default. Anchors come from the imported geometry rather than the donor's
            // (see SpreadGlowAnchors) so the glow lands on what was actually imported.
            uint glow = itemVisual > 0 ? (uint)itemVisual : 0u;
            var (glowLo, glowHi) = GlowRange(glowStartPercent, glowEndPercent);
            if (glow != 0)
                import.Diagnostics.Info("visual.chosen",
                    $"Enchant-style glow: ItemVisual {glow} ({ItemVisualSuggester.Find(glow)?.Label ?? "custom"}), " +
                    $"anchored along the imported mesh from {glowLo:P0} to {glowHi:P0} of its length.");

            var result = await _builder.BuildAsync(new CustomWeaponBuildRequest
            {
                Name = configuredItem?.Name ?? name,
                SourceKind = "glb_import",
                WeaponTypeKey = profile.Key,
                VariableTriangleHardCeiling = MaxForgeTriangles,
                ItemOverrides = MergeItemOverrides(null, configuredItem),
                Mesh = mesh,
                Topology = WeaponTopologyMode.Variable,
                TexturePng = AdjustTexture(texturePng, brightness, saturation),
                SourceBlob = bytes,
                ItemVisual = glow,
                AttachmentPointsWoW = glow != 0 ? SpreadGlowAnchors(mesh, glowLo, glowHi) : null,
                GeneratorParamsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reorient, targetTriangles, rollDegrees, flipGripEnd, straightenBlade, bladeProfile,
                    brightness, saturation, itemVisual = (int)glow, glowStartPercent, glowEndPercent,
                    shape = shape ?? new GlbShapeControls(),
                    donorDisplayRow = donor.DisplayRow,
                    donorExtent = donor.ExtentX,
                    donorPalmBackFraction = donor.PalmBackFraction,
                    // Provenance of the baked recolor (the registry blobs already carry it).
                    recolor = recolorBaked ? (object?)new { hue = recolorHue, theory = recolorTheory, tier = recolorTier } : null,
                }),
                WriterVersion = "variable-topology-v1",
            });
            if (decimation is not null)
                _logger.LogInformation("WeaponForge: ForgeGlb {Decimation}", decimation);
            return Json(BuildResultJson(result, BuildGripInfo(mesh, profile, donor)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeaponForge: ForgeGlb failed");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>GET /WeaponForge/PreviewForged?displayId= — render a forged weapon's stored M2+BLP
    /// into a preview GLB so it can be inspected in the viewer.</summary>
    [HttpGet]
    public async Task<IActionResult> PreviewForged(long displayId)
    {
        var (m2, blp, effects, sourceKind) = await LoadForgedBytesAsync(displayId);
        if (m2 is null) return NotFound(new { ok = false, error = $"No stored M2 for display id {displayId}." });

        // The forged weapon's enchant glow is an ItemVisual on its display row, not anything in the
        // stored M2 — resolve it so the preview shows what the client will.
        var host = M2Reader.Parse(m2);
        uint visualId = _dbc.GetItemModelInfo((uint)displayId)?.ItemVisualId ?? 0;
        var preview = _preview.RenderFromBytes(m2, blp, MergeStockPreviewTextures(host, effects),
            ResolveVisualEffects(visualId, host),
            preserveSourceGraph: IsSourcePreservingBuild(sourceKind));
        return Json(new { ok = preview.Ok, preview, hasTexture = blp is { Length: > 0 }, displayId, itemVisual = visualId });
    }

    internal static bool IsSourcePreservingBuild(string? sourceKind)
        => string.Equals(sourceKind, "vanilla_recolor", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, byte[]>? MergeStockPreviewTextures(
        M2Model? model, IReadOnlyDictionary<string, byte[]>? custom)
    {
        var result = custom is null
            ? new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, byte[]>(custom, StringComparer.OrdinalIgnoreCase);
        if (model is not null)
        {
            foreach (var texture in model.Textures)
            {
                string? stockPath = WeaponPreviewService.StockPreviewTexturePath(texture);
                if (stockPath is null || result.ContainsKey(stockPath))
                    continue;
                byte[]? bytes = _mpq.ExtractFile(stockPath)
                    ?? _mpq.ExtractFile(stockPath.ToLowerInvariant());
                if (bytes is { Length: > 0 }) result[stockPath] = bytes;
            }
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Load a forged weapon's compiled M2 (+ BLP + effect textures) from the registry
    /// tables (model_id == display_id).</summary>
    private async Task<(byte[]? M2, byte[]? Blp, Dictionary<string, byte[]>? Effects, string? SourceKind)>
        LoadForgedBytesAsync(long displayId)
    {
        await using var conn = _db.Admin();
        await conn.OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            @"SELECT m.compiled_m2 AS M2, d.compiled_blp AS Blp, m.source_kind AS SourceKind
              FROM custom_weapon_model m
              LEFT JOIN custom_weapon_display d ON d.model_id = m.model_id
              WHERE m.model_id = @displayId", new { displayId });
        if (row is null) return (null, null, null, null);

        Dictionary<string, byte[]>? effects = null;
        var texRows = await conn.QueryAsync(
            @"SELECT mpq_path AS MpqPath, compiled_blp AS Blp
              FROM custom_weapon_model_texture
              WHERE model_id = @displayId AND compiled_blp IS NOT NULL
              ORDER BY slot", new { displayId });
        foreach (var t in texRows)
        {
            effects ??= new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            effects[(string)t.MpqPath] = (byte[])t.Blp;
        }
        return ((byte[]?)row.M2, (byte[]?)row.Blp, effects, (string?)row.SourceKind);
    }

    /// <summary>GET /WeaponForge/InspectWeapon?displayId= — structural side-by-side dump of a forged
    /// weapon's stored M2 against the golden donor: header fields, bounds, all views, submesh/batch
    /// records, sample vertices, binary-validator output, and automated comparison checks. Built for
    /// debugging renders-invisible failures without client round-trips.</summary>
    [HttpGet]
    public async Task<IActionResult> InspectWeapon(long displayId)
    {
        byte[]? forged;
        await using (var conn = _db.Admin())
        {
            await conn.OpenAsync();
            forged = await conn.QueryFirstOrDefaultAsync<byte[]?>(
                "SELECT compiled_m2 FROM custom_weapon_model WHERE model_id = @displayId", new { displayId });
        }
        if (forged is null or { Length: 0 })
            return NotFound(new { error = $"No stored compiled M2 for display id {displayId}." });

        var donor = SafeExtract(DonorM2Path);
        if (donor is null)
            return NotFound(new { error = $"Donor M2 not found in mounted archives: {DonorM2Path}" });

        return Json(new
        {
            displayId,
            donor = DumpM2(donor, expectedViews: 4),
            forged = DumpM2(forged, expectedViews: 4),
            checks = CompareM2(donor, forged),
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // LATER-CLIENT IMPORT — TBC (WeaponForge:TbcDataPath) and WotLK
    // (WeaponForge:WotlkDataPath), both set on the Settings page.
    //
    // A TBC/WotLK weapon is NOT byte-compatible with the 1.12 client (M2
    // v260–263 / v264 + external .skin vs the required v256), but it doesn't
    // need a converter either: LegacyMpqSource.LoadM2 parses either layout
    // into the same M2Model (positions/normals/UV0/triangles, already
    // palm-at-origin in WoW space, plus the rest-pose material graph), and the
    // mesh is fed through the exact pipeline a GLB import uses — re-emitted as
    // a genuine vanilla v256 on the family donor scaffold, its BLP2 textures
    // carried byte-for-byte. Models are addressed by their stem and resolved
    // through the server-built index — raw MPQ paths are never accepted from
    // the client. Every public endpoint below exists per lane (Tbc* / Wotlk*)
    // and as an `expansion=` form; all of them share one implementation.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>GET /WeaponForge/TbcStatus — mount state of the configured TBC Data folder plus
    /// the shipped item-name catalog join.</summary>
    [HttpGet]
    public IActionResult TbcStatus() => LegacyStatus(_sources.Tbc);

    /// <summary>GET /WeaponForge/WotlkStatus — same for the WotLK (3.3.5a) Data folder.</summary>
    [HttpGet]
    public IActionResult WotlkStatus() => LegacyStatus(_sources.Wotlk);

    /// <summary>GET /WeaponForge/VanillaImportStatus — mount state of the vanilla lane. Unlike the
    /// other two this needs no client path configured: it falls back to the client the app already
    /// deploys into, with our own patches excluded so forged art is never offered as source.</summary>
    [HttpGet]
    public IActionResult VanillaImportStatus() => LegacyStatus(_sources.Vanilla);

    /// <summary>GET /WeaponForge/ImportStatus?expansion=tbc|wotlk|vanilla — lane-keyed form.</summary>
    [HttpGet]
    public IActionResult ImportStatus(string? expansion = null) => LegacyStatus(_sources.Get(expansion));

    private IActionResult LegacyStatus(LegacyImportSource src)
    {
        var (configured, path, archiveCount, error) = src.Mpq.Status();
        int weaponCount = 0, itemCount = 0;
        if (configured && error is null)
        {
            try
            {
                var index = src.Mpq.WeaponIndex();
                weaponCount = index.Count;
                var rows = index.Select(w => w.DisplayRow).ToHashSet();
                itemCount = src.Items.Items.Count(i => LegacyItemCatalog.TypeKeyFor(i.ItemClass, i.Subclass) is not null && rows.Contains(i.DisplayId));
            }
            catch (Exception ex) { error = ex.Message; }
        }
        return Json(new
        {
            expansion = src.Key,
            label = src.Label,
            configured,
            path,
            archiveCount,
            weaponCount,
            itemCount,
            catalogItems = src.Items.Items.Count,
            error,
            note = $"Set the {src.Label} client Data path on the Settings page (Weapon Forge section).",
        });
    }

    /// <summary>GET /WeaponForge/TbcWeapons?search=&amp;page=&amp;pageSize= — paged browse. When the
    /// shipped item catalog is present, rows are real ITEMS (name/quality/ilvl, joined to the
    /// mounted archives by display id, weapon type pre-mapped from the subclass); without it,
    /// the browse degrades to raw model stems.</summary>
    [HttpGet]
    public IActionResult TbcWeapons(string? search = null, int page = 1, int pageSize = 60) =>
        LegacyWeapons(_sources.Tbc, search, page, pageSize);

    /// <summary>GET /WeaponForge/WotlkWeapons — the WotLK browse (same shape as TbcWeapons).</summary>
    [HttpGet]
    public IActionResult WotlkWeapons(string? search = null, int page = 1, int pageSize = 60) =>
        LegacyWeapons(_sources.Wotlk, search, page, pageSize);

    /// <summary>GET /WeaponForge/VanillaImportWeapons — browse stock weapons and shields. The item
    /// list is the live item_template rather than a shipped catalog, so it cannot drift from the
    /// server; custom entries are excluded so our own output is never re-imported.</summary>
    [HttpGet]
    public IActionResult VanillaImportWeapons(string? search = null, int page = 1, int pageSize = 60) =>
        LegacyWeapons(_sources.Vanilla, search, page, pageSize);

    /// <summary>GET /WeaponForge/ImportWeapons?expansion=tbc|wotlk|vanilla&amp;search=… — lane-keyed form.</summary>
    [HttpGet]
    public IActionResult ImportWeapons(string? expansion = null, string? search = null, int page = 1, int pageSize = 60) =>
        LegacyWeapons(_sources.Get(expansion), search, page, pageSize);

    private IActionResult LegacyWeapons(LegacyImportSource src, string? search, int page, int pageSize)
    {
        IReadOnlyList<LegacyWeaponEntry> index;
        try { index = src.Mpq.WeaponIndex(); }
        catch (Exception ex) { return Json(new { ok = false, expansion = src.Key, error = ex.Message }); }

        pageSize = Math.Clamp(pageSize, 10, 200);
        string s = search?.Trim() ?? "";

        var byRow = index.ToDictionary(w => w.DisplayRow, w => w);

        // Item mode: shipped names joined to the user's archives. Weapons only — armor ships in the
        // catalog for the Armor Forge; shields (class 4 / subclass 6) are the one armor family that
        // is forgeable here.
        var items = src.Items.Items
            .Where(i => LegacyItemCatalog.TypeKeyFor(i.ItemClass, i.Subclass) is not null &&
                        byRow.ContainsKey(i.DisplayId))
            .ToList();
        if (items.Count > 0)
        {
            IEnumerable<LegacyItemInfo> filtered = items;
            if (s.Length > 0)
                filtered = items.Where(i =>
                    i.Name.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    byRow[i.DisplayId].ModelStem.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    i.Entry.ToString() == s);

            var list = filtered.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Entry).ToList();
            int total = list.Count;
            int pages = Math.Max(1, (total + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 1, pages);

            return Json(new
            {
                ok = true,
                expansion = src.Key,
                label = src.Label,
                mode = "items",
                total,
                page,
                pages,
                weapons = list.Skip((page - 1) * pageSize).Take(pageSize).Select(i =>
                {
                    var w = byRow[i.DisplayId];
                    string typeKey = LegacyItemCatalog.TypeKeyFor(i.ItemClass, i.Subclass)!;
                    return new
                    {
                        entry = i.Entry,
                        name = i.Name,
                        quality = i.Quality,
                        itemLevel = i.ItemLevel,
                        typeKey,
                        typeLabel = WeaponTypeCatalog.Get(typeKey).Label,
                        inventoryType = i.InventoryType,
                        inventoryTypeLabel = CustomWeaponBuildService.InventoryTypeLabel(i.InventoryType),
                        w.DisplayRow,
                        model = w.ModelStem,
                        texture = w.TextureStem,
                    };
                }),
            });
        }

        // Model-stem fallback (catalog missing, or nothing joined).
        IEnumerable<LegacyWeaponEntry> mFiltered = index;
        if (s.Length > 0)
            mFiltered = index.Where(w =>
                w.ModelStem.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                w.TextureStem.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                w.IconStem.Contains(s, StringComparison.OrdinalIgnoreCase));

        var mList = mFiltered.OrderBy(w => w.ModelStem, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.DisplayRow).ToList();
        int mTotal = mList.Count;
        int mPages = Math.Max(1, (mTotal + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, mPages);

        return Json(new
        {
            ok = true,
            expansion = src.Key,
            label = src.Label,
            mode = "models",
            total = mTotal,
            page,
            pages = mPages,
            weapons = mList.Skip((page - 1) * pageSize).Take(pageSize).Select(w => new
            {
                entry = 0u,
                name = w.ModelStem,
                quality = 1,
                itemLevel = 0,
                typeKey = (string?)null,
                typeLabel = (string?)null,
                inventoryType = (int?)null,
                inventoryTypeLabel = (string?)null,
                w.DisplayRow,
                model = w.ModelStem,
                texture = w.TextureStem,
            }),
        });
    }

    /// <summary>Resolve a client-supplied model stem (+ optional display row when one model has
    /// several texture variants) through the server-built index. Never trusts a raw path.</summary>
    private static LegacyWeaponEntry? ResolveLegacyEntry(LegacyImportSource src, string? model, uint displayRow)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var index = src.Mpq.WeaponIndex();
        if (displayRow > 0)
        {
            var byRow = index.FirstOrDefault(w => w.DisplayRow == displayRow &&
                w.ModelStem.Equals(model.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byRow is not null) return byRow;
        }
        return index.FirstOrDefault(w => w.ModelStem.Equals(model.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolve a browse selection: a catalog item entry (preferred — carries name, quality
    /// and the subclass-mapped weapon type) or a bare model stem/display row from the fallback mode.</summary>
    private static (LegacyWeaponEntry? Entry, LegacyItemInfo? Item) ResolveLegacySelection(LegacyImportSource src, uint itemEntry, string? model, uint displayRow)
    {
        LegacyItemInfo? item = itemEntry > 0 ? src.Items.FindByEntry(itemEntry) : null;
        if (item is not null)
        {
            var byRow = src.Mpq.WeaponIndex().FirstOrDefault(w => w.DisplayRow == item.DisplayId);
            if (byRow is not null) return (byRow, item);
        }
        return (ResolveLegacyEntry(src, model, displayRow), item);
    }

    /// <summary>
    /// Turn a motion plan into what the previewer needs to draw the same effect: each graft plus the
    /// decoded sheet it samples, positioned back in the preview's Y-up mesh space.
    ///
    /// The import preview renders an intermediate mesh, not a packaged model, so there are no forged
    /// bytes for the GLB writer's usual emitter path to read. Without this the browse-and-preview
    /// surface is the one place a forged effect stays invisible — which is precisely where someone is
    /// looking when they are deciding whether an import is worth forging.
    ///
    /// The plan's positions are WoW space (the planner works in the coordinate frame the M2 is
    /// written in) and already carry the placement transform, so they only need converting back.
    /// </summary>
    /// <summary>
    /// An item visual resolved to loaded effect models, mounted on the host's attachment points.
    ///
    /// The ItemVisual is a whole third channel: the item's own bytes say nothing about it, and it is
    /// where enchant glows and many permanent weapon effects live. Thunderfury is not one of them:
    /// its stock ItemVisual is zero and its lightning lives in its preserved M2. The ids the
    /// forge deals in are always VANILLA ids (the suggester only offers ones the 1.12 client has), so
    /// the effect models come out of the vanilla mount regardless of which lane the item came from.
    /// </summary>
    private IReadOnlyList<MangosSuperUI.Services.M2Fx.ItemVisualEffects.Effect>? ResolveVisualEffects(uint itemVisualId, M2Model? host)
    {
        if (itemVisualId == 0) return null;
        try
        {
            var effects = MangosSuperUI.Services.M2Fx.ItemVisualEffects.Resolve(itemVisualId, host,
                path => _mpq.ExtractFile(path) ?? _mpq.ExtractFile(path.ToLowerInvariant()));
            return effects.Count > 0 ? effects : null;
        }
        catch { return null; }
    }

    private List<WeaponPreviewService.PreviewEmitter>? BuildPreviewEmitters(
        EffectMotionPlanner.Plan plan, IReadOnlyList<Vector3> positionsWoW)
    {
        if (!plan.Any) return null;

        var result = new List<WeaponPreviewService.PreviewEmitter>();
        var pngCache = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);

        foreach (var graft in plan.Grafts)
        {
            string path = graft.TexturePath ?? "";
            if (path.Length == 0) continue;
            if (!pngCache.TryGetValue(path, out var png))
            {
                var blp = _mpq.ExtractFile(path) ?? _mpq.ExtractFile(path.ToLowerInvariant());
                png = blp is { Length: > 0 } ? BlpToPng(blp) : null;
                pngCache[path] = png;
            }
            if (png is not { Length: > 0 }) continue;

            result.Add(new WeaponPreviewService.PreviewEmitter(
                graft, CoordinateContract.WoWToMesh(graft.PositionWoW), png));
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>Plan the animated rebuild of a source model's particle effects (see
    /// <see cref="EffectMotionPlanner"/>). Donors come out of the live 1.12 mount, so a client that
    /// is missing one simply yields a smaller plan instead of failing the import.</summary>
    private EffectMotionPlanner.Plan PlanMotion(M2Model? model, IReadOnlyList<Vector3> positionsWoW, string label)
    {
        var emitters = model?.ParticleEmitters ?? new List<M2ParticleEmitterInfo>();
        if (emitters.Count == 0)
            return new EffectMotionPlanner.Plan(Array.Empty<M2EmitterTransplanter.Graft>(), Array.Empty<string>(), 0);
        return EffectMotionPlanner.Build(emitters, positionsWoW, path => _mpq.ExtractFile(path), null, label);
    }

    private sealed record PreservedVanillaWeapon(
        M2Model Model,
        byte[] M2Bytes,
        byte[] DisplayBlp,
        byte[] DisplayPng,
        IReadOnlyDictionary<string, byte[]>? SupplementalPreviewBlps,
        ForgeDiagnostics Diagnostics);

    /// <summary>
    /// Load a stock 1.12 weapon without passing it through <see cref="LegacyWeaponMeshExtractor"/>.
    /// Keeping the original M2 graph is what preserves source bones, global sequences, material
    /// tracks, particle/ribbon emitters, and the Type-3 weapon-blade sheen. Skin-only recolors leave
    /// every M2 byte intact; native-effect recolors later make only verified color-value and Type-0
    /// filename-pointer edits.
    /// </summary>
    private (PreservedVanillaWeapon? Weapon, string? Error) LoadPreservedVanillaWeapon(
        LegacyImportSource src, LegacyWeaponEntry entry)
    {
        var diag = new ForgeDiagnostics("vanilla-source");
        var (model, m2Bytes, loadError) = src.Mpq.LoadM2Detailed(entry.M2Path);
        if (model is null || m2Bytes is not { Length: > 0 })
            return (null, loadError ?? "The stock 1.12 M2 could not be parsed.");
        if (model.Version != 256)
            return (null, $"The vanilla source-preserving lane requires an M2 v256 source (got v{model.Version}).");

        var sampledTextureSlots = WeaponPreviewService.SampledTextureSlots(model);
        if (!WeaponPreviewService.SamplesDisplayTexture(model))
            return (null,
                $"{entry.ModelStem} has no sampled Type-2 object-skin slot. Changing ItemDisplayInfo.TextureName1 would not recolor any rendered batch safely.");
        if (string.IsNullOrWhiteSpace(entry.BlpPath))
            return (null, $"Display {entry.DisplayRow} has no TextureName1 BLP to recolor.");

        byte[]? displayBlp = src.Mpq.ExtractFile(entry.BlpPath)
            ?? src.Mpq.ExtractFile(entry.BlpPath.ToLowerInvariant());
        if (displayBlp is not { Length: > 0 })
            return (null, $"The stock display texture '{entry.BlpPath}' could not be read.");
        byte[]? displayPng = BlpToPng(displayBlp);
        if (displayPng is not { Length: > 0 })
            return (null, $"The stock display texture '{entry.BlpPath}' could not be decoded.");

        var hardcoded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var emitterTextureSlots = MangosSuperUI.Services.M2Fx.M2FxReader
            .EmitterTextureSlots(m2Bytes).ToHashSet();
        for (int textureSlot = 0; textureSlot < model.Textures.Count; textureSlot++)
        {
            var texture = model.Textures[textureSlot];
            string? stockPath = WeaponPreviewService.StockPreviewTexturePath(texture);
            if (stockPath is null)
            {
                if (sampledTextureSlots.Contains(textureSlot) && !WeaponPreviewService.UsesDisplayTexture(texture))
                    diag.Warn("vanilla.texture.runtime-unsupported",
                        $"Sampled replaceable Type-{texture.Type} slot {textureSlot} has no WebGL runtime binding; it will use a neutral preview material while the original M2 remains intact for the client.");
                continue;
            }
            if (hardcoded.ContainsKey(stockPath))
                continue;
            byte[]? bytes = src.Mpq.ExtractFile(stockPath)
                ?? src.Mpq.ExtractFile(stockPath.ToLowerInvariant());
            if (bytes is { Length: > 0 })
            {
                hardcoded[stockPath] = bytes;
                continue;
            }

            if (texture.Type == 0 && sampledTextureSlots.Contains(textureSlot))
                return (null,
                    $"Sampled hardcoded source texture '{stockPath}' is unavailable in the vanilla mount; refusing to show an incomplete recolor preview.");

            if (texture.Type == 0 && emitterTextureSlots.Contains(textureSlot))
                diag.Warn("vanilla.texture.emitter-missing",
                    $"Emitter sheet '{stockPath}' is unavailable in the vanilla mount; that native emitter cannot be shown in WebGL.");
            else
                diag.Warn("vanilla.texture.runtime-missing",
                    $"The stock runtime Type-{texture.Type} preview texture '{stockPath}' is unavailable; that slot will use a neutral WebGL material. The packaged M2 still asks the 1.12 client for its own runtime replacement.");
        }

        diag.Info("vanilla.source.preserved",
            $"Preserving the stock M2 byte-for-byte ({model.Vertices.Count:N0} vertices, {model.Indices.Count / 3:N0} triangles, " +
            $"{model.Bones.Count:N0} bones, {model.GlobalSequenceDurations.Count:N0} global sequence(s)); downstream edits are restricted to its Type-2 skin and explicitly verified native-effect color channels.");
        return (new PreservedVanillaWeapon(model, m2Bytes, displayBlp, displayPng,
            hardcoded.Count > 0 ? hardcoded : null, diag), null);
    }

    /// <summary>Encode the already-recolored native-size PNG. DXT3 keeps authored alpha; the
    /// palettized fallback is lossless for stock-style palettes and, unlike a resize path, never
    /// changes the texture envelope.</summary>
    private byte[]? EncodePreservedDisplayBlp(byte[] png)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(png);
            if (bitmap is null) return null;
            return _blp.EncodeBitmapToBlp(bitmap, useDxt1: false)
                ?? _blp.EncodeBitmapToBlpUncompressed(bitmap);
        }
        catch { return null; }
    }

    private sealed record PreparedNativeEffectTint(
        byte[] M2Bytes,
        IReadOnlyList<PrecompiledWeaponEffectTexture> Textures,
        int ColorTracksRecolored,
        int EmittersRecolored);

    /// <summary>
    /// Recolor only native effect channels that can remain structurally identical: private copies
    /// of hardcoded Type-0 compositing/particle/ribbon sheets plus in-place hue shifts of authored
    /// material and particle color values. Shared runtime Type-3 textures and ItemVisual effect
    /// models are intentionally outside this boundary.
    /// </summary>
    private (PreparedNativeEffectTint? Tint, string? Error) PrepareNativeEffectTint(
        PreservedVanillaWeapon source, string glowColor)
    {
        Vector3? target = HexToRgb255(glowColor);
        if (target is not Vector3 rgb)
            return (null, "Glow color must be a six-digit hex color such as #33aaff.");

        var (targetHue, targetSaturation) = HueAndSaturation(rgb);
        float lightnessScale = GlowLightnessScale(rgb);
        bool darkAura = IsDarkGlowPick(rgb);
        IReadOnlyList<NativeWeaponEffectTexture> selected;
        try
        {
            selected = NativeWeaponEffectRecolor.SelectEligibleTextures(source.Model);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            return (null,
                $"The source native-effect texture graph could not be inspected safely: {ex.Message}");
        }

        int[] eligibleTextureIndices = selected
            .SelectMany(effect => effect.TextureIndices)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        M2MaterialColorHueWriter.Result materialTint;
        try
        {
            materialTint = M2MaterialColorHueWriter.Apply(
                source.M2Bytes, targetHue, targetSaturation, eligibleTextureIndices, lightnessScale);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            return (null,
                $"The source material-color animation could not be inspected safely: {ex.Message}");
        }
        if (!materialTint.IsComplete)
        {
            string reason = materialTint.Notes.Count > 0
                ? " " + string.Join("; ", materialTint.Notes)
                : string.Empty;
            return (null,
                "The source material-color animation cannot be completely recolored without " +
                "risking a static or corrupt effect; no effect changes were made." + reason);
        }
        foreach (string note in materialTint.Notes)
            source.Diagnostics.Info("vanilla.effect.material", note);

        var textures = new List<PrecompiledWeaponEffectTexture>(selected.Count);
        foreach (NativeWeaponEffectTexture effect in selected)
        {
            if (source.SupplementalPreviewBlps is null ||
                !source.SupplementalPreviewBlps.TryGetValue(effect.SourcePath, out byte[]? sourceBlp) ||
                sourceBlp is not { Length: > 0 })
            {
                return (null,
                    $"Native effect texture '{effect.SourcePath}' is unavailable; refusing to forge an incomplete recolor.");
            }

            byte[]? tintedBlp;
            try
            {
                byte[]? sourcePng = BlpToPng(sourceBlp);
                byte[]? tintedPng = sourcePng is { Length: > 0 }
                    ? NativeWeaponEffectRecolor.TintPng(
                        sourcePng, targetHue, targetSaturation, lightnessScale: lightnessScale, darkAura: darkAura)
                    : null;
                tintedBlp = tintedPng is { Length: > 0 }
                    ? EncodePreservedDisplayBlp(tintedPng)
                    : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                return (null,
                    $"Native effect texture '{effect.SourcePath}' could not be inspected safely: {ex.Message}");
            }
            if (tintedBlp is not { Length: > 0 })
                return (null, $"Native effect texture '{effect.SourcePath}' could not be recolored and encoded.");

            textures.Add(new PrecompiledWeaponEffectTexture(
                effect.TextureIndices, effect.SourcePath, tintedBlp));
        }

        byte[] tintedM2 = materialTint.M2;
        int colorTracksRecolored = materialTint.ColorsChanged;
        M2ParticleColorHueWriter.Result particleTint;
        try
        {
            particleTint = M2ParticleColorHueWriter.Apply(
                tintedM2, targetHue, targetSaturation, eligibleTextureIndices, lightnessScale);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            return (null,
                $"The source particle color ramps could not be inspected safely: {ex.Message}");
        }
        if (!particleTint.IsComplete)
        {
            string reason = particleTint.Notes.Count > 0
                ? " " + string.Join("; ", particleTint.Notes)
                : string.Empty;
            return (null,
                "The source particle color ramps cannot be completely recolored without risking " +
                "a partial or corrupt effect; no effect changes were made." + reason);
        }
        foreach (string note in particleTint.Notes)
            source.Diagnostics.Info("vanilla.effect.particle", note);
        tintedM2 = particleTint.M2;
        int emittersRecolored = particleTint.EmittersChanged;

        // Dark pick: the tinted sheets now carry coverage alpha, so their compositing passes must
        // draw alpha-blended to show as a dark aura. Only render-flag entries used exclusively by
        // the admitted effect batches are re-flagged; a shared entry stays additive (and dims).
        if (darkAura && textures.Count > 0)
        {
            M2EffectBlendModeWriter.Result blend;
            try { blend = M2EffectBlendModeWriter.Apply(tintedM2, eligibleTextureIndices); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                return (null, $"The source render-flag table could not be inspected safely: {ex.Message}");
            }
            tintedM2 = blend.M2;
            foreach (string note in blend.Notes) source.Diagnostics.Info("vanilla.effect.blend", note);
            source.Diagnostics.Info("vanilla.effect.blend", blend.MaterialsChanged.Count > 0
                ? $"Dark glow pick: {blend.MaterialsChanged.Count} compositing material(s) re-flagged additive → alpha-blend so the effect draws as a dark aura."
                : "Dark glow pick: no compositing material could be re-flagged (shared with opaque batches); the effect is dimmed instead.");
        }

        if (textures.Count == 0 && colorTracksRecolored == 0 && emittersRecolored == 0)
        {
            return (null,
                "This model has no safely isolated native Type-0 effect/ribbon sheet, material color track, " +
                "or particle color ramp to recolor. " +
                "Shared ItemVisual and Type-3 runtime effects stay stock.");
        }

        source.Diagnostics.Info("vanilla.effect.tint",
            $"Native effect recolor prepared {textures.Count} private Type-0 sheet(s) and " +
            $"changed {colorTracksRecolored} material color track(s) plus " +
            $"{emittersRecolored} particle ramp(s); shared ItemVisual and Type-3 effects remain stock.");
        return (new PreparedNativeEffectTint(
            tintedM2, textures, colorTracksRecolored, emittersRecolored), null);
    }

    private static (ItemVisualSuggester.Suggestion? Suggestion, uint Chosen, object ParticleInfo)
        ChooseSourceVisual(M2Model model, LegacyItemVisualIndex? sourceVisuals, uint sourceDisplayRow,
            int requestedVisual, ForgeDiagnostics diag)
    {
        var emitters = model.ParticleEmitters;
        var (srcVisualId, srcVisualStems) = sourceVisuals?.ForDisplayRow(sourceDisplayRow)
            ?? (0u, (IReadOnlyList<string>)Array.Empty<string>());
        var sourceCatalogVisual = ItemVisualSuggester.Find(srcVisualId);
        ItemVisualSuggester.Suggestion? suggestion = srcVisualId == 0 ? null : new(
            srcVisualId,
            sourceCatalogVisual?.Label ?? $"ItemVisual {srcVisualId}",
            "Exact ItemVisual from the stock Vanilla ItemDisplayInfo row.",
            string.Join(", ", srcVisualStems));
        uint chosen = ChoosePreservedItemVisual(srcVisualId, requestedVisual);

        if (srcVisualId != 0)
            diag.Info("visual.source", $"The source display carries ItemVisual {srcVisualId}; auto preserves that exact Vanilla id.");
        if (requestedVisual >= 0 && chosen != srcVisualId)
            diag.Info("visual.override", $"Explicit ItemVisual override: source {srcVisualId}, chosen {chosen}.");
        if (chosen != 0)
            diag.Info("visual.chosen", $"Enchant-style glow: ItemVisual {chosen} ({ItemVisualSuggester.Find(chosen)?.Label ?? "custom"}).");

        object particles = new
        {
            count = emitters.Count,
            textures = emitters.Select(e => e.TextureName is null ? null : Path.GetFileNameWithoutExtension(e.TextureName))
                .Where(t => t is not null).Distinct().Take(6),
            colours = emitters.Where(e => e.ColorRgb is not null)
                .Select(e => $"#{(int)Math.Clamp(e.ColorRgb!.Value.X, 0, 255):X2}{(int)Math.Clamp(e.ColorRgb!.Value.Y, 0, 255):X2}{(int)Math.Clamp(e.ColorRgb!.Value.Z, 0, 255):X2}")
                .Distinct().Take(6),
            sourceVisual = srcVisualId == 0 ? null : new
            {
                id = srcVisualId,
                effects = srcVisualStems.Take(5),
                preserved = srcVisualId,
                label = sourceCatalogVisual?.Label,
                reason = "Exact stock Vanilla ItemVisual; native M2 emitters are already preserved separately.",
            },
        };
        return (suggestion, chosen, particles);
    }

    internal static uint ChoosePreservedItemVisual(uint sourceItemVisual, int requestedVisual)
        => requestedVisual < 0 ? sourceItemVisual : checked((uint)requestedVisual);

    /// <summary>Fail-closed placement predicate for a byte-preserved M2. LegacyPlacement ignores
    /// width/depth/head/haft because later-client imports do not expose those reshapes, but a crafted
    /// vanilla request must not slip one through and report that it was applied.</summary>
    internal static bool IsSourceGraphPlacementIdentity(GlbShapeControls? shape, bool flipGripEnd)
    {
        static bool DefaultPercent(int value) => value <= 0 || value == 100;
        return LegacyPlacement.IsIdentity(shape, flipGripEnd) &&
               (shape is null ||
                (DefaultPercent(shape.WidthPercent) && DefaultPercent(shape.DepthPercent) &&
                 DefaultPercent(shape.HeadPercent) && DefaultPercent(shape.HaftPercent)));
    }

    /// <summary>Pure request boundary used by the vanilla source-preserving lane and its regression
    /// tests. Exactly one payload is selected: raw precompiled M2, never a rigid mesh.</summary>
    internal static CustomWeaponBuildRequest CreatePreservedVanillaBuildRequest(
        string? name, string weaponTypeKey, Dictionary<string, string>? itemOverrides,
        byte[] sourceM2, byte[] displayBlp, string? iconStem, uint itemVisual,
        uint groupSoundIndex, uint spellVisualId, bool mirrorModelName2, string generatorParamsJson)
        => new()
        {
            Name = name,
            SourceKind = "vanilla_recolor",
            IconStem = iconStem,
            WeaponTypeKey = weaponTypeKey,
            ItemOverrides = itemOverrides,
            PrecompiledM2 = sourceM2,
            PrecompiledBlp = displayBlp,
            SourceBlob = sourceM2,
            ItemVisual = itemVisual,
            DisplayGroupSoundIndex = groupSoundIndex,
            DisplaySpellVisualId = spellVisualId,
            DisplayMirrorModelName2 = mirrorModelName2,
            GeneratorParamsJson = generatorParamsJson,
            WriterVersion = "vanilla-source-v1",
        };

    /// <summary>Effect-aware source-preserving request. <paramref name="sourceM2"/> remains the
    /// provenance blob; <paramref name="tintedM2"/> differs only in verified color values and is
    /// later repointed to build-private Type-0 members by the builder.</summary>
    internal static CustomWeaponBuildRequest CreatePreservedVanillaEffectBuildRequest(
        string? name, string weaponTypeKey, Dictionary<string, string>? itemOverrides,
        byte[] sourceM2, byte[] tintedM2, byte[] displayBlp, string? iconStem, uint itemVisual,
        uint groupSoundIndex, uint spellVisualId, bool mirrorModelName2, string generatorParamsJson,
        IReadOnlyList<PrecompiledWeaponEffectTexture> effectTextures)
        => new()
        {
            Name = name,
            SourceKind = "vanilla_recolor",
            IconStem = iconStem,
            WeaponTypeKey = weaponTypeKey,
            ItemOverrides = itemOverrides,
            PrecompiledM2 = tintedM2,
            PrecompiledBlp = displayBlp,
            PrecompiledEffectTextures = effectTextures,
            SourceBlob = sourceM2,
            ItemVisual = itemVisual,
            DisplayGroupSoundIndex = groupSoundIndex,
            DisplaySpellVisualId = spellVisualId,
            DisplayMirrorModelName2 = mirrorModelName2,
            GeneratorParamsJson = generatorParamsJson,
            WriterVersion = "vanilla-source-effects-v2",
        };

    /// <summary>Shared extract + parse + mesh-build front half for both lanes. PNGs feed the web
    /// preview; original BLP2 bytes feed the forge so the source texture/mips/compression are not
    /// altered. Model parsing is version-aware (<see cref="LegacyMpqSource.LoadM2Detailed"/>).</summary>
    private (RigidWeaponMesh? Mesh, byte[]? M2Bytes, byte[]? TexturePng, List<byte[]>? EffectPngs,
        byte[]? TextureBlp, List<byte[]>? EffectBlps, ForgeDiagnostics Diag, string? Error, M2Model? Model)
        LoadLegacyWeapon(LegacyImportSource src, LegacyWeaponEntry entry, int targetTriangles)
    {
        var diag = new ForgeDiagnostics(src.Key + "-import");

        var (m2, m2Bytes, loadError) = src.Mpq.LoadM2Detailed(entry.M2Path);
        if (m2 is null)
            return (null, m2Bytes, null, null, null, null, diag, loadError ?? $"The {src.Label} M2 could not be parsed.", null);
        diag.Info("import.source", $"{entry.ModelStem}: {src.Label} M2 v{m2.Version}, {m2.Vertices.Count} verts, {m2.Indices.Count / 3} tris.");

        // If the effects can be rebuilt as real, moving 1.12 emitters, do NOT also bake them into
        // static sprites — that would draw the effect twice, once alive and once as a decal.
        bool rebuildAsMotion = PlanMotion(m2, Array.Empty<Vector3>(), entry.ModelStem).Any;
        var extracted = LegacyWeaponMeshExtractor.Extract(m2, diag, src.Label, bakeEmitters: !rebuildAsMotion);
        if (rebuildAsMotion)
            diag.Info("motion.plan", $"{m2.ParticleEmitters.Count} source particle emitter(s) will be rebuilt as animated 1.12 emitters instead of static sprites.");
        if (extracted is null)
            return (null, m2Bytes, null, null, null, null, diag, $"The {src.Label} model has no usable triangles.", m2);
        var mesh = extracted.Mesh;

        // Resolve each texture slot: a hardcoded source path, or null = the display row's Type-2 BLP.
        (byte[]? Png, byte[]? Blp) SlotTexture(string? sourcePath, string slotName)
        {
            string? path = sourcePath ?? entry.BlpPath;
            if (path is null) { diag.Warn("import.texture", $"No texture source for {slotName}."); return (null, null); }
            var blp = src.Mpq.ExtractFile(path);
            if (blp is not { Length: > 0 }) { diag.Warn("import.texture", $"{src.Label} BLP {path} not found ({slotName})."); return (null, null); }
            var png = BlpToPng(blp);
            if (png is null) diag.Warn("import.texture", $"{src.Label} BLP {path} could not be decoded ({slotName}).");
            return (png, blp);
        }

        var baseTexture = SlotTexture(extracted.SourceTextures.Count > 0 ? extracted.SourceTextures[0].SourcePath : null, "base");
        byte[]? texturePng = baseTexture.Png;
        if (texturePng is null)
            return (null, m2Bytes, null, null, baseTexture.Blp, null, diag,
                $"The {src.Label} weapon's required base texture is unavailable; fidelity mode will not substitute the donor texture.", m2);
        List<byte[]>? effectPngs = null;
        List<byte[]>? effectBlps = null;
        if (extracted.SourceTextures.Count > 1 && mesh.Passes is not null)
        {
            effectPngs = new List<byte[]>();
            effectBlps = new List<byte[]>();
            for (int s = 1; s < extracted.SourceTextures.Count; s++)
            {
                var effect = SlotTexture(extracted.SourceTextures[s].SourcePath, $"effect slot {s}");
                if (effect.Png is null || effect.Blp is null)
                    return (null, m2Bytes, texturePng, null, baseTexture.Blp, null, diag,
                        $"The {src.Label} weapon's required texture slot {s} is unavailable; fidelity mode will not drop its render pass.", m2);
                effectPngs.Add(effect.Png);
                effectBlps.Add(effect.Blp);
            }
        }

        // Later-client imports are fidelity-first. Decimation merges submeshes and destroys the
        // source batch/pass structure that carries cutouts, overlays and glow, so the legacy triangle
        // target is intentionally ignored on this route. Arbitrary GLB imports retain their
        // separate 1,000-triangle authoring policy.
        if (targetTriangles > 0)
            diag.Info("tbc.fidelity.target-ignored",
                $"Triangle target {targetTriangles:N0} ignored in {src.Label} fidelity mode; preserved all {mesh.TriangleCount:N0} source triangles and render passes.");

        return (mesh, m2Bytes, texturePng, effectPngs, baseTexture.Blp, effectBlps, diag, null, m2);
    }

    /// <summary>Five enchant/glow anchors (attachment ids 0..4) spread evenly along a mesh's long
    /// axis, hilt to tip, inset a tenth of the length at each end so the outermost glows sit on the
    /// weapon instead of floating past it. Laterally they ride the model's centreline.
    ///
    /// A vanilla glow visual fills all five ItemVisuals slots, so this is what decides whether the
    /// glow reads as an enchant running the length of the weapon or as a lump in one place. GLB
    /// imports have no anchors of their own and would otherwise inherit the donor scaffold's, which
    /// sit on the donor's blade — a different length and shape from whatever was imported.</summary>
    /// <summary>Normalize the operator's glow placement range: percent along the weapon's length
    /// (0 = pommel/butt end, 100 = tip), clamped and ordered. Start == end is legal — all five
    /// anchor stations pile onto one point, a single glow spot instead of a hilt-to-tip run.</summary>
    private static (float Lo, float Hi) GlowRange(int startPercent, int endPercent)
    {
        float a = Math.Clamp(startPercent, 0, 100) / 100f;
        float b = Math.Clamp(endPercent, 0, 100) / 100f;
        return a <= b ? (a, b) : (b, a);
    }

    /// <summary>One of the five glow anchor stations spread evenly across the chosen fraction of
    /// the placed mesh's length, in the mesh's own Y-up space. The forge converts these same
    /// stations to WoW space (<see cref="SpreadGlowAnchors"/>); the GLB preview mounts effect
    /// models on them directly, so what the viewer shows is where the forged glow will hang.</summary>
    private static Vector3 GlowAnchorMesh(RigidWeaponMesh mesh, int slot, float loFrac, float hiFrac)
    {
        float minX = mesh.Positions.Min(v => v.X), maxX = mesh.Positions.Max(v => v.X);
        float span = MathF.Max(maxX - minX, 1e-4f);
        float lo = minX + loFrac * span, hi = minX + hiFrac * span;
        float cy = (mesh.Positions.Min(v => v.Y) + mesh.Positions.Max(v => v.Y)) * 0.5f;
        float cz = (mesh.Positions.Min(v => v.Z) + mesh.Positions.Max(v => v.Z)) * 0.5f;
        return new Vector3(lo + (hi - lo) * (Math.Clamp(slot, 0, 4) / 4f), cy, cz);
    }

    private static Dictionary<uint, Vector3> SpreadGlowAnchors(RigidWeaponMesh mesh, float loFrac, float hiFrac)
    {
        var points = new Dictionary<uint, Vector3>();
        for (uint id = 0; id <= 4; id++)
            points[id] = CoordinateContract.MeshToWoW(GlowAnchorMesh(mesh, (int)id, loFrac, hiFrac));
        return points;
    }

    /// <summary>ItemVisual effects for the GLB route's preview: a GLB has no host M2 to read
    /// attachments from, so the chosen enchant glow mounts on the same anchor stations the forge
    /// writes (<see cref="SpreadGlowAnchors"/>) across the operator's chosen span.</summary>
    private IReadOnlyList<MangosSuperUI.Services.M2Fx.ItemVisualEffects.Effect>? ResolveVisualEffectsForMesh(
        uint itemVisualId, RigidWeaponMesh mesh, float loFrac, float hiFrac)
    {
        if (itemVisualId == 0) return null;
        try
        {
            var effects = MangosSuperUI.Services.M2Fx.ItemVisualEffects.Resolve(itemVisualId, null,
                path => _mpq.ExtractFile(path) ?? _mpq.ExtractFile(path.ToLowerInvariant()),
                slot => GlowAnchorMesh(mesh, slot, loFrac, hiFrac));
            return effects.Count > 0 ? effects : null;
        }
        catch { return null; }
    }

    /// <summary>Owner hand placement + enchant-glow decision shared by preview and forge. The source
    /// geometry is exact by default (measured: later-client weapons are authored to the same palm
    /// convention as vanilla); <see cref="LegacyPlacement"/> only moves things when the controls say so.
    /// Source attachment points (enchant/glow anchors 0..4) ride along with the mesh.
    ///
    /// The glow itself comes from whichever source actually carries it, in this order when the owner
    /// leaves the picker on Auto:
    ///
    ///   1. the source weapon's OWN <c>ItemDisplayInfo.ItemVisual</c>, mapped onto 1.12 by
    ///      <see cref="ItemVisualSuggester.MapLaterClientVisual"/> — usually an exact copy, and by far
    ///      the most common case (890 of 7,515 TBC weapon rows, 1,356 of 10,062 WotLK ones);
    ///   2. failing that, the nearest vanilla glow for the model's particle emitters.
    ///
    /// <paramref name="spreadGlowAnchors"/> overrides where the glow hangs. Vanilla glow visuals fill
    /// all five slots, so the look depends entirely on where anchors 0..4 sit — and plenty of source
    /// models bunch all five in one spot (Axe_2h_OutlandRaid_D_04 puts them at X 0.773-0.982 of a
    /// -0.560..1.200 model, i.e. all on the head). Spreading them evenly along the long axis gives the
    /// vanilla hilt-to-tip enchant look instead of five blobs in one place.</summary>
    private static (RigidWeaponMesh Mesh, Dictionary<uint, Vector3> AttachmentsWoW, float SourceGripPercent,
        ItemVisualSuggester.Suggestion? Suggestion, uint ItemVisual, object ParticleInfo, List<Vector3> EmitterPositionsWoW)
        PlaceLegacyWeapon(RigidWeaponMesh mesh, M2Model? model, GlbShapeControls? shape, bool flipGripEnd, int itemVisual,
            ForgeDiagnostics diag, LegacyItemVisualIndex? sourceVisuals = null, uint sourceDisplayRow = 0,
            bool spreadGlowAnchors = false)
    {
        float sourceGrip = LegacyPlacement.GripFraction(mesh) * 100f;
        var srcAttachments = (model?.Attachments ?? new List<M2Attachment>())
            .Where(a => a.Id <= 4)
            .GroupBy(a => a.Id).Select(g => g.First())
            .OrderBy(a => a.Id).ToList();
        // Particle-emitter positions ride the SAME transform as the mesh and the glow anchors: an
        // owner who resizes or re-grips the import must not leave its flames behind in mid-air.
        var srcEmitters = model?.ParticleEmitters ?? new List<M2ParticleEmitterInfo>();
        var toPlace = srcAttachments.Select(a => a.Position).Concat(srcEmitters.Select(e => e.Position)).ToList();
        var (placed, pts) = LegacyPlacement.Apply(mesh, toPlace, shape, flipGripEnd, diag);
        var attachments = new Dictionary<uint, Vector3>();
        for (int i = 0; i < srcAttachments.Count; i++)
            attachments[srcAttachments[i].Id] = CoordinateContract.MeshToWoW(pts[i]);
        var emitterPositionsWoW = new List<Vector3>();
        for (int i = 0; i < srcEmitters.Count; i++)
            emitterPositionsWoW.Add(CoordinateContract.MeshToWoW(pts[srcAttachments.Count + i]));

        // MeshToWoW keeps X, so the long axis is directly comparable across the two spaces.
        float minX = placed.Positions.Min(p => p.X), maxX = placed.Positions.Max(p => p.X);
        float modelSpan = MathF.Max(maxX - minX, 1e-4f);

        // Missing anchors (some source models carry fewer than five) are spread along the placed
        // model's long axis so a glow never hangs on the donor's old blade.
        if (attachments.Count > 0 && attachments.Count < 5)
        {
            var known = attachments.Values.Select(v => v.X).ToList();
            float lo = MathF.Max(minX, known.Min() - 0.25f * modelSpan), hi = MathF.Min(maxX, known.Max() + 0.25f * modelSpan);
            var refPos = attachments.Values.First();
            for (uint id = 0; id <= 4; id++)
                if (!attachments.ContainsKey(id))
                    attachments[id] = new Vector3(lo + (hi - lo) * (id / 4f), refPos.Y, refPos.Z);
        }

        // How far apart the source put its anchors, as a fraction of the weapon's length — the number
        // that decides whether a glow reads as "along the weapon" or "a lump on the blade".
        float anchorSpan = attachments.Count > 1
            ? (attachments.Values.Max(v => v.X) - attachments.Values.Min(v => v.X)) / modelSpan
            : 0f;
        if (spreadGlowAnchors && attachments.Count > 0)
        {
            // Hilt to tip, inset a tenth of the length at each end so the outermost glows sit ON the
            // weapon rather than floating off its ends.
            float lo = minX + 0.10f * modelSpan, hi = maxX - 0.10f * modelSpan;
            var refPos = attachments.Values.First();
            for (uint id = 0; id <= 4; id++)
                attachments[id] = new Vector3(lo + (hi - lo) * (id / 4f), refPos.Y, refPos.Z);
            diag.Info("visual.anchors.spread",
                $"Glow anchors 0..4 spread evenly from hilt to tip (the source's own anchors spanned {anchorSpan * 100f:F0}% of the weapon's length).");
        }
        else if (attachments.Count > 1 && anchorSpan < 0.25f)
        {
            diag.Info("visual.anchors.clustered",
                $"The source's five glow anchors span only {anchorSpan * 100f:F0}% of the weapon's length, so any enchant glow bunches in one spot. " +
                "Turn on 'spread glow along weapon' for the vanilla hilt-to-tip look.");
        }

        // Which glow? The source row's own ItemVisual first, emitters as the fallback.
        var emitters = model?.ParticleEmitters ?? new List<M2ParticleEmitterInfo>();
        var emitterSuggestion = ItemVisualSuggester.Suggest(emitters);
        var (srcVisualId, srcVisualStems) = sourceVisuals?.ForDisplayRow(sourceDisplayRow) ?? (0u, (IReadOnlyList<string>)Array.Empty<string>());
        var carried = ItemVisualSuggester.MapLaterClientVisual(srcVisualId, srcVisualStems);
        var suggestion = carried is { ItemVisual: > 0 } ? carried : (emitterSuggestion ?? carried);
        uint chosen = itemVisual < 0 ? (suggestion?.ItemVisual ?? 0) : (uint)itemVisual;

        if (srcVisualId != 0)
            diag.Info("visual.source",
                $"The source display row carries ItemVisual {srcVisualId}" +
                (srcVisualStems.Count > 0 ? $" ({string.Join(" + ", srcVisualStems.Take(3))})" : "") +
                $" — {carried?.Reason ?? "no mapping"}.");
        if (chosen != 0 && ItemVisualSuggester.Find(chosen) is null) { diag.Warn("visual.unknown", $"ItemVisual {chosen} is not a known 1.12 visual; using it anyway."); }
        if (chosen != 0)
            diag.Info("visual.chosen", $"Enchant-style glow: ItemVisual {chosen} ({ItemVisualSuggester.Find(chosen)?.Label ?? "custom"})" +
                (itemVisual < 0 && suggestion is not null ? $" — {suggestion.Reason} ({suggestion.EmitterSummary})." : "."));
        else if (suggestion is not null && itemVisual == 0)
            diag.Info("visual.none", $"Source has {suggestion.EmitterSummary}; no glow chosen (suggested: {suggestion.Label}).");

        object particleInfo = new
        {
            count = emitters.Count,
            textures = emitters.Select(e => e.TextureName is null ? null : Path.GetFileNameWithoutExtension(e.TextureName)).Where(t => t is not null).Distinct().Take(6),
            colours = emitters.Where(e => e.ColorRgb is not null).Select(e => $"#{(int)Math.Clamp(e.ColorRgb!.Value.X, 0, 255):X2}{(int)Math.Clamp(e.ColorRgb!.Value.Y, 0, 255):X2}{(int)Math.Clamp(e.ColorRgb!.Value.Z, 0, 255):X2}").Distinct().Take(6),
            sourceVisual = srcVisualId == 0 ? null : new { id = srcVisualId, effects = srcVisualStems.Take(5), mapped = carried?.ItemVisual ?? 0, mappedLabel = carried?.Label, reason = carried?.Reason },
            emitterSuggested = emitterSuggestion is null ? null : new { id = emitterSuggestion.ItemVisual, label = emitterSuggestion.Label, reason = emitterSuggestion.Reason },
            anchorSpanPercent = MathF.Round(anchorSpan * 100f, 0),
            anchorsSpread = spreadGlowAnchors,
            suggested = suggestion is null ? null : new { id = suggestion.ItemVisual, label = suggestion.Label, reason = suggestion.Reason },
            chosen = new { id = chosen, label = chosen == 0 ? "none" : (ItemVisualSuggester.Find(chosen)?.Label ?? $"visual {chosen}") },
        };
        return (placed, attachments, sourceGrip, suggestion, chosen, particleInfo, emitterPositionsWoW);
    }

    /// <summary>GET /WeaponForge/ItemVisuals — the vanilla enchant-style glows a forged weapon can
    /// carry (ItemDisplayInfo field 22), for the import cards' glow picker. Labels come from the
    /// suggester's catalog; the live 1.12 ItemVisuals.dbc is consulted so only ids the client has are
    /// offered, and any stock rows the catalog doesn't name are listed by their effect model stems.</summary>
    [HttpGet]
    public IActionResult ItemVisuals()
    {
        var list = new List<object>();
        try
        {
            var iv = _mpq.ExtractFile(@"DBFilesClient\ItemVisuals.dbc");
            var ive = _mpq.ExtractFile(@"DBFilesClient\ItemVisualEffects.dbc");
            var effName = new Dictionary<uint, string>();
            if (ive is { Length: > 0 })
            {
                var eff = DbcWriterService.ReadDbc(ive, "ive");
                foreach (var r in eff.GetAllRows()) if (r.Length > 1) effName[r[0]] = Path.GetFileNameWithoutExtension(eff.ReadString(r[1]));
            }
            if (iv is { Length: > 0 })
            {
                var vis = DbcWriterService.ReadDbc(iv, "iv");
                foreach (var r in vis.GetAllRows())
                {
                    var effects = Enumerable.Range(1, vis.FieldCount - 1).Select(i => r[i]).Where(e => e != 0)
                        .Select(e => effName.TryGetValue(e, out var n) ? n : $"#{e}").Distinct().ToList();
                    var known = ItemVisualSuggester.Find(r[0]);
                    list.Add(new { id = r[0], label = known?.Label ?? string.Join(" + ", effects), effects, curated = known is not null });
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "ItemVisuals: live DBC read failed; serving the curated catalog"); }
        if (list.Count == 0)
            list.AddRange(ItemVisualSuggester.Catalog.Select(v => new { id = v.Id, label = v.Label, effects = Array.Empty<string>(), curated = true }));
        return Json(new { ok = true, visuals = list.OrderBy(v => ((dynamic)v).curated ? 0 : 1).ThenBy(v => (string)((dynamic)v).label).ToList() });
    }

    /// <summary>GET /WeaponForge/GlowCoverage?expansion=tbc|wotlk — how much of a mounted client's
    /// weapon glow the forge can actually reproduce, answered from DBC data alone so it is instant.
    ///
    /// Every browsable weapon/shield display row is checked for a permanent glow
    /// (<c>ItemDisplayInfo.ItemVisual</c>) and each one classified:
    ///
    ///   • <b>exact</b>  — 1.12 ships the same ItemVisuals row, so it copies across untouched;
    ///   • <b>mapped</b> — a later-client-only row matched to the nearest vanilla glow;
    ///   • <b>none</b>   — a cast/state animation with no permanent-glow equivalent.
    ///
    /// Particle-emitter glows are NOT counted here: they live inside the models, so counting them
    /// means parsing every M2 (measured: 1,103 of 7,515 TBC weapons carry emitters, and the sweep
    /// takes minutes). The per-weapon preview reports those individually.</summary>
    [HttpGet]
    public IActionResult GlowCoverage(string? expansion = null)
    {
        var src = _sources.Get(expansion);
        var status = src.Mpq.Status();
        if (!status.Configured || status.ArchiveCount == 0)
            return Json(new { ok = false, expansion = src.Key, error = status.Error ?? $"No {src.Label} client is mounted." });

        var visuals = src.Mpq.ItemVisuals();
        var weaponRows = src.Mpq.WeaponIndex();
        int exact = 0, mapped = 0, unmatched = 0;
        var byVisual = new Dictionary<uint, int>();
        foreach (var w in weaponRows)
        {
            var (id, stems) = visuals.ForDisplayRow(w.DisplayRow);
            if (id == 0) continue;
            byVisual[id] = byVisual.GetValueOrDefault(id) + 1;
            var m = ItemVisualSuggester.MapLaterClientVisual(id, stems);
            if (m is null || m.ItemVisual == 0) unmatched++;
            else if (m.ItemVisual == id) exact++;
            else mapped++;
        }
        int withGlow = exact + mapped + unmatched;

        var top = byVisual.OrderByDescending(kv => kv.Value).Take(20).Select(kv =>
        {
            var stems = visuals.EffectStems(kv.Key);
            var m = ItemVisualSuggester.MapLaterClientVisual(kv.Key, stems);
            return new
            {
                sourceId = kv.Key,
                weapons = kv.Value,
                effects = stems.Take(3),
                vanillaId = m?.ItemVisual ?? 0,
                vanillaLabel = m is null || m.ItemVisual == 0 ? "no 1.12 equivalent" : m.Label,
                kind = m is null || m.ItemVisual == 0 ? "none" : (m.ItemVisual == kv.Key ? "exact" : "mapped"),
                reason = m?.Reason,
            };
        }).ToList();

        return Json(new
        {
            ok = true,
            expansion = src.Key,
            expansionLabel = src.Label,
            weaponRows = weaponRows.Count,
            withGlow,
            exact,
            mapped,
            unmatched,
            distinctVisuals = byVisual.Count,
            top,
            note = $"{withGlow:N0} of {weaponRows.Count:N0} browsable {src.Label} weapon/shield models carry a permanent glow on their display row. " +
                   $"{exact:N0} use an ItemVisual 1.12 already ships (copied across unchanged), {mapped:N0} are matched to the nearest vanilla glow, " +
                   $"and {unmatched:N0} are spell-cast animations with no permanent-glow equivalent. Models that glow via particle emitters instead " +
                   "are reported per weapon when you preview them.",
        });
    }

    /// <summary>GET /WeaponForge/TbcPreviewWeapon — render one TBC weapon through the import
    /// pipeline (same mesh + texture the forge would package) without packaging anything.</summary>
    [HttpGet]
    public Task<IActionResult> TbcPreviewWeapon(uint entry = 0, string? model = null, uint displayRow = 0,
        string? weaponType = null, int targetTriangles = 0, int brightness = 0, int saturation = 0,
        GlbShapeControls? shape = null, bool flipGripEnd = false, int itemVisual = -1, bool glowSpread = false,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved",
        string? glowColor = null) =>
        LegacyPreviewWeapon(_sources.Tbc, entry, model, displayRow, weaponType, targetTriangles, brightness, saturation, shape, flipGripEnd, itemVisual, glowSpread, recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor);

    /// <summary>GET /WeaponForge/WotlkPreviewWeapon — the WotLK preview.</summary>
    [HttpGet]
    public Task<IActionResult> WotlkPreviewWeapon(uint entry = 0, string? model = null, uint displayRow = 0,
        string? weaponType = null, int targetTriangles = 0, int brightness = 0, int saturation = 0,
        GlbShapeControls? shape = null, bool flipGripEnd = false, int itemVisual = -1, bool glowSpread = false,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved",
        string? glowColor = null) =>
        LegacyPreviewWeapon(_sources.Wotlk, entry, model, displayRow, weaponType, targetTriangles, brightness, saturation, shape, flipGripEnd, itemVisual, glowSpread, recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor);

    /// <summary>GET /WeaponForge/VanillaPreviewWeapon — recolor the stock display skin while
    /// preserving the original 1.12 M2 and its complete animation/render graph.</summary>
    [HttpGet]
    public Task<IActionResult> VanillaPreviewWeapon(uint entry = 0, string? model = null, uint displayRow = 0,
        string? weaponType = null, int targetTriangles = 0, int brightness = 0, int saturation = 0,
        GlbShapeControls? shape = null, bool flipGripEnd = false, int itemVisual = -1, bool glowSpread = false,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved",
        string? glowColor = null) =>
        PreviewPreservedVanillaWeapon(entry, model, displayRow, weaponType, brightness, saturation,
            shape, flipGripEnd, itemVisual, glowSpread, recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor);

    /// <summary>GET /WeaponForge/ImportPreviewWeapon?expansion=tbc|wotlk|vanilla&amp;… — lane-keyed form.</summary>
    [HttpGet]
    public Task<IActionResult> ImportPreviewWeapon(string? expansion = null, uint entry = 0, string? model = null, uint displayRow = 0,
        string? weaponType = null, int targetTriangles = 0, int brightness = 0, int saturation = 0,
        GlbShapeControls? shape = null, bool flipGripEnd = false, int itemVisual = -1, bool glowSpread = false,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved",
        string? glowColor = null) =>
        string.Equals(expansion, VanillaMpqSource.SourceKey, StringComparison.OrdinalIgnoreCase)
            ? PreviewPreservedVanillaWeapon(entry, model, displayRow, weaponType, brightness, saturation,
                shape, flipGripEnd, itemVisual, glowSpread, recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor)
            : LegacyPreviewWeapon(_sources.Get(expansion), entry, model, displayRow, weaponType,
                targetTriangles, brightness, saturation, shape, flipGripEnd, itemVisual, glowSpread,
                recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor);

    private async Task<IActionResult> PreviewPreservedVanillaWeapon(uint entry, string? model, uint displayRow,
        string? weaponType, int brightness, int saturation, GlbShapeControls? shape, bool flipGripEnd,
        int itemVisual, bool glowSpread, float? recolorHue, float? recolorSat, float? recolorLight, string recolorTheory, string recolorTier,
        string? glowColor)
    {
        var src = _sources.Vanilla;
        var (sel, item) = ResolveLegacySelection(src, entry, model, displayRow);
        if (sel is null)
            return NotFound(new { ok = false, error = $"Unknown Vanilla weapon (entry {entry}, model '{model}')." });

        // Repositioning vertices without transforming every bone pivot/key and attachment would break
        // the very source graph this route exists to preserve. The vanilla card exposes no placement
        // controls, but fail closed for hand-authored requests instead of silently flattening the M2.
        if (!IsSourceGraphPlacementIdentity(shape, flipGripEnd) || glowSpread)
            return BadRequest(new
            {
                ok = false,
                error = "Vanilla source-preserving recolor cannot reshape, flip, or redistribute glow anchors. " +
                        "Those edits require a full animated-rig transform; use the stock placement to keep the weapon alive.",
            });

        string? typeKey = string.IsNullOrWhiteSpace(weaponType) && item is not null
            ? LegacyItemCatalog.TypeKeyFor(item.ItemClass, item.Subclass)
            : weaponType;
        var profile = WeaponTypeCatalog.Find(typeKey);
        if (profile is null)
            return Json(new { ok = false, expansion = src.Key, error = $"Unknown or unsupported weapon family '{typeKey}'." });

        var (source, loadError) = LoadPreservedVanillaWeapon(src, sel);
        if (source is null)
            return Json(new { ok = false, expansion = src.Key, error = loadError });

        byte[] texturePng = source.DisplayPng;
        bool recolorApplied = false;
        if (recolorHue.HasValue)
        {
            int seed = RetextureSupport.SeedFor((int)sel.DisplayRow, recolorTier);
            byte[]? recolored = await RecolorTexturePngAsync(texturePng, seed, recolorHue.Value, recolorSat, recolorLight,
                recolorTheory, recolorTier, HttpContext.RequestAborted);
            if (recolored is not null) { texturePng = recolored; recolorApplied = true; }
            else source.Diagnostics.Warn("recolor.preview",
                "The Type-2 skin has no recolorable colour families; showing the original texture.");
        }

        byte[]? adjustedPng = AdjustTexture(texturePng, brightness, saturation);
        bool textureChanged = recolorApplied || brightness != 0 || saturation != 0;
        byte[]? previewBlp = textureChanged && adjustedPng is { Length: > 0 }
            ? EncodePreservedDisplayBlp(adjustedPng)
            : source.DisplayBlp;
        if (previewBlp is not { Length: > 0 })
            return Json(new { ok = false, expansion = src.Key, error = "The recolored display skin could not be encoded as a vanilla BLP." });

        PreparedNativeEffectTint? nativeTint = null;
        byte[] previewM2 = source.M2Bytes;
        IReadOnlyDictionary<string, byte[]>? previewTextures = source.SupplementalPreviewBlps;
        if (!string.IsNullOrWhiteSpace(glowColor))
        {
            var (prepared, effectError) = PrepareNativeEffectTint(source, glowColor);
            if (prepared is null)
                return BadRequest(new { ok = false, expansion = src.Key, error = effectError });
            nativeTint = prepared;
            previewM2 = prepared.M2Bytes;

            var exactTextures = source.SupplementalPreviewBlps is null
                ? new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
                : source.SupplementalPreviewBlps.ToDictionary(
                    pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (PrecompiledWeaponEffectTexture effect in prepared.Textures)
                exactTextures[effect.SourcePath] = effect.Blp;
            previewTextures = exactTextures;
        }

        var visual = ChooseSourceVisual(source.Model, src.Mpq.ItemVisuals(), sel.DisplayRow,
            itemVisual, source.Diagnostics);
        var preview = _preview.RenderFromBytes(previewM2, previewBlp, previewTextures,
            ResolveVisualEffects(visual.Chosen, source.Model), preserveSourceGraph: true);

        return Json(new
        {
            ok = preview.Ok,
            expansion = src.Key,
            expansionLabel = src.Label,
            itemEntry = item?.Entry ?? 0,
            itemName = item?.Name,
            model = sel.ModelStem,
            texture = sel.TextureStem,
            sel.DisplayRow,
            weaponType = profile.Key,
            weaponTypeLabel = profile.Label,
            inventoryType = EffectiveTbcInventoryType(item, profile),
            inventoryTypeLabel = CustomWeaponBuildService.InventoryTypeLabel(EffectiveTbcInventoryType(item, profile)),
            vertexCount = source.Model.Vertices.Count,
            triangleCount = source.Model.Indices.Count / 3,
            hasTexture = true,
            withinForgeBudget = true,
            grip = (object?)null,
            sourceGripPercent = (float?)null,
            placementApplied = false,
            recolorApplied,
            nativeEffectsRecolored = nativeTint is not null,
            nativeEffectTexturesRecolored = nativeTint?.Textures.Count ?? 0,
            nativeColorTracksRecolored = nativeTint?.ColorTracksRecolored ?? 0,
            nativeEmittersRecolored = nativeTint?.EmittersRecolored ?? 0,
            ribbonsPreserved = source.Model.RibbonEmitterCount,
            sourcePreserved = true,
            bonesPreserved = source.Model.Bones.Count,
            globalSequencesPreserved = source.Model.GlobalSequenceDurations.Count,
            particles = visual.ParticleInfo,
            itemVisual = visual.Chosen,
            suggestedItemVisual = visual.Suggestion is null ? null : new
            {
                id = visual.Suggestion.ItemVisual,
                label = visual.Suggestion.Label,
                reason = visual.Suggestion.Reason,
            },
            preview,
            diagnostics = source.Diagnostics.Items.Select(i => i.ToString()),
            note = nativeTint is null
                ? "Preview only — the original vanilla M2 is intact. Only its Type-2 ItemDisplayInfo skin was replaced; bones, global sequences, billboards, material tracks, Type-3 blade sheen, and hardcoded effect textures are preserved."
                : source.Model.RibbonEmitterCount > 0
                    ? $"Source-preserved effect preview — selected native Type-0 sheets and existing color values were hue-shifted without rebuilding the M2. The {source.Model.RibbonEmitterCount} preserved ribbon emitter(s) are packaged for the game client but are not simulated by this WebGL preview; their animation records and timing remain intact."
                    : "Exact effect preview — selected native Type-0 sheets and existing color values were hue-shifted without rebuilding the M2. Bones, global sequences, billboards, animation timing, Type-3 runtime sheen, and the separate ItemVisual remain intact.",
        });
    }

    private async Task<IActionResult> LegacyPreviewWeapon(LegacyImportSource src, uint entry, string? model, uint displayRow,
        string? weaponType, int targetTriangles, int brightness, int saturation,
        GlbShapeControls? shape, bool flipGripEnd, int itemVisual, bool glowSpread,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved",
        string? glowColor = null)
    {
        var (sel, item) = ResolveLegacySelection(src, entry, model, displayRow);
        if (sel is null) return NotFound(new { ok = false, error = $"Unknown {src.Label} weapon (entry {entry}, model '{model}')." });

        var (loadedMesh, _, texturePng, effectPngs, _, _, diag, err, sourceModel) = LoadLegacyWeapon(src, sel, targetTriangles);
        if (loadedMesh is null)
            return Json(new { ok = false, expansion = src.Key, error = err, diagnostics = diag.Items.Select(i => i.ToString()) });

        // Pre-import recolor preview: shift the source skin to the chosen primary hue with the same
        // palette engine the Armor Forge uses, so the viewer shows exactly what a forge would bake.
        bool recolorApplied = false;
        if (recolorHue.HasValue && texturePng is { Length: > 0 })
        {
            int rseed = RetextureSupport.SeedFor((int)sel.DisplayRow, recolorTier);
            var rp = await RecolorTexturePngAsync(texturePng, rseed, recolorHue.Value, recolorSat, recolorLight, recolorTheory, recolorTier, HttpContext.RequestAborted);
            if (rp is not null) { texturePng = rp; recolorApplied = true; }
            else diag.Warn("recolor.preview", "The skin has no recolorable colour families; showing the original texture.");
        }
        Vector3? glowRgb = HexToRgb255(glowColor);
        if (!string.IsNullOrWhiteSpace(glowColor) && glowRgb is null)
            return BadRequest(new { ok = false, expansion = src.Key, error = "Glow color must be a six-digit hex color such as #33aaff." });

        var (mesh, _, sourceGripPercent, suggestion, chosenVisual, particleInfo, emitterPositions) =
            PlaceLegacyWeapon(loadedMesh, sourceModel, shape, flipGripEnd, itemVisual, diag,
                src.Mpq.ItemVisuals(), sel.DisplayRow, glowSpread);
        var motionPlan = PlanMotion(sourceModel, emitterPositions, sel.ModelStem);
        foreach (var note in motionPlan.Notes) diag.Info("motion.emitter", note);

        IReadOnlyList<int> effectTextureSlotsRecolored = Array.Empty<int>();
        if (glowRgb is Vector3 previewGlow)
        {
            try
            {
                var (effectHue, effectSaturation) = HueAndSaturation(previewGlow);
                LegacyWeaponEffectTint effectTint = LegacyWeaponEffectRecolor.Apply(
                    mesh, effectPngs, effectBlps: null, effectHue, effectSaturation,
                    GlowLightnessScale(previewGlow), IsDarkGlowPick(previewGlow));
                effectPngs = effectTint.Pngs;
                if (effectTint.Passes is not null)
                {
                    mesh.Passes = effectTint.Passes;
                    diag.Info("effect.tint.preview", "Dark glow pick: compositing passes re-flagged from additive to alpha-blend so the effect draws as a dark aura.");
                }
                effectTextureSlotsRecolored = effectTint.TextureSlots;
                if (effectTextureSlotsRecolored.Count > 0)
                    diag.Info("effect.tint.preview",
                        $"Hue-shifted compositing effect texture slot(s) {string.Join(", ", effectTextureSlotsRecolored)} for the preview; render-pass animation and environment mapping are unchanged.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                return BadRequest(new
                {
                    ok = false,
                    expansion = src.Key,
                    error = $"The imported effect textures could not be recolored safely: {ex.Message}",
                });
            }

            if (motionPlan.Grafts.Count > 0)
                motionPlan = motionPlan with
                {
                    Grafts = motionPlan.Grafts
                        .Select(graft => graft with { ColorRgb = previewGlow, ColorRamp = null })
                        .ToList(),
                };
        }

        string? typeKey = string.IsNullOrWhiteSpace(weaponType) && item is not null
            ? LegacyItemCatalog.TypeKeyFor(item.ItemClass, item.Subclass)
            : weaponType;
        // Same fail-closed rule as the forge path — previewing as a sword what cannot be forged as
        // one is how the silent fallback stayed invisible.
        var previewProfile = WeaponTypeCatalog.Find(typeKey);
        if (previewProfile is null)
            return Json(new
            {
                ok = false,
                expansion = src.Key,
                error = string.IsNullOrWhiteSpace(typeKey)
                    ? $"{src.Label} item {entry} is a weapon subclass the Forge has no family for (fist weapon, spear and fishing pole are not importable). Pick a family explicitly to preview it as that family."
                    : $"Unknown weapon family '{typeKey}'.",
            });
        WeaponTypeProfile profile = previewProfile;
        int effectiveInventoryType = EffectiveTbcInventoryType(item, profile);
        object? grip = null;
        try { grip = BuildGripInfo(mesh, profile, _donors.Resolve(profile)); }
        catch { /* grip markers are optional for preview */ }
        if (profile.IsRanged)
            diag.Warn("import.ranged.rigid",
                $"{profile.Label} import is rigid on the family scaffold's root bone: the {src.Label} model's limb/string/hammer animation is not carried; its projectile visual and ranged slot are.");

        var preview = _preview.RenderMesh(mesh, AdjustTexture(texturePng, brightness, saturation), effectPngs,
            emitters: BuildPreviewEmitters(motionPlan, emitterPositions),
            visualEffects: ResolveVisualEffects((uint)Math.Max(chosenVisual, 0), sourceModel));
        return Json(new
        {
            ok = preview.Ok,
            expansion = src.Key,
            expansionLabel = src.Label,
            itemEntry = item?.Entry ?? 0,
            itemName = item?.Name,
            model = sel.ModelStem,
            texture = sel.TextureStem,
            sel.DisplayRow,
            weaponType = profile.Key,
            weaponTypeLabel = profile.Label,
            inventoryType = effectiveInventoryType,
            inventoryTypeLabel = CustomWeaponBuildService.InventoryTypeLabel(effectiveInventoryType),
            vertexCount = mesh.VertexCount,
            triangleCount = mesh.TriangleCount,
            hasTexture = texturePng is { Length: > 0 },
            withinForgeBudget = mesh.TriangleCount <= MaxTbcForgeTriangles,
            grip,
            sourceGripPercent = MathF.Round(sourceGripPercent, 1),
            placementApplied = !LegacyPlacement.IsIdentity(shape, flipGripEnd),
            recolorApplied,
            effectTexturesRecolored = effectTextureSlotsRecolored.Count,
            effectTextureSlotsRecolored,
            effectEmittersRecolored = glowRgb is null ? 0 : motionPlan.Grafts.Count,
            particles = particleInfo,
            itemVisual = chosenVisual,
            suggestedItemVisual = suggestion is null ? null : new { id = suggestion.ItemVisual, label = suggestion.Label, reason = suggestion.Reason },
            preview,
            diagnostics = diag.Items.Select(i => i.ToString()),
            note = "Preview only — nothing was packaged. Geometry, sidedness and pass order match the forge; WebGL approximates WoW multi-texture combiners and shows UV animation at its rest frame, while the forged M2 retains supported global UV tracks. Enchant-style glows (ItemVisual) render in-game only.",
        });
    }

    /// <summary>POST /WeaponForge/ForgeTbc — package one TBC weapon for real: its render graph is
    /// emitted as vanilla v256 on the family donor scaffold and compatible BLP2 bytes stay intact.</summary>
    [HttpPost]
    public Task<IActionResult> ForgeTbc(uint entry = 0, string? model = null, uint displayRow = 0,
        string? name = null, string? weaponType = null, int targetTriangles = 0,
        int brightness = 0, int saturation = 0, string? itemConfig = null,
        GlbShapeControls? shape = null, bool flipGripEnd = false, int itemVisual = -1, bool glowSpread = false,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved", string? glowColor = null) =>
        LegacyForge(_sources.Tbc, entry, model, displayRow, name, weaponType, targetTriangles, brightness, saturation, itemConfig, shape, flipGripEnd, itemVisual, glowSpread, recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor);

    /// <summary>POST /WeaponForge/ForgeWotlk — package one WotLK weapon (same contract as ForgeTbc).</summary>
    [HttpPost]
    public Task<IActionResult> ForgeWotlk(uint entry = 0, string? model = null, uint displayRow = 0,
        string? name = null, string? weaponType = null, int targetTriangles = 0,
        int brightness = 0, int saturation = 0, string? itemConfig = null,
        GlbShapeControls? shape = null, bool flipGripEnd = false, int itemVisual = -1, bool glowSpread = false,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved", string? glowColor = null) =>
        LegacyForge(_sources.Wotlk, entry, model, displayRow, name, weaponType, targetTriangles, brightness, saturation, itemConfig, shape, flipGripEnd, itemVisual, glowSpread, recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor);

    /// <summary>POST /WeaponForge/ForgeVanilla — package one STOCK weapon as a new custom item with
    /// its own display: recolored skin, tinted glow, chosen enchant visual. This is the whole reason
    /// the lane exists — a vanilla CLONE reuses the source display and so can never be recolored,
    /// because the colours live in the BLP and the glow colours in the M2 and a clone ships neither.
    /// The trade is that this packages: a patch, and a client restart.</summary>
    [HttpPost]
    public Task<IActionResult> ForgeVanilla(uint entry = 0, string? model = null, uint displayRow = 0,
        string? name = null, string? weaponType = null, int targetTriangles = 0,
        int brightness = 0, int saturation = 0, string? itemConfig = null,
        GlbShapeControls? shape = null, bool flipGripEnd = false, int itemVisual = -1, bool glowSpread = false,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved", string? glowColor = null) =>
        ForgePreservedVanilla(entry, model, displayRow, name, weaponType, targetTriangles, brightness,
            saturation, itemConfig, shape, flipGripEnd, itemVisual, glowSpread, recolorHue, recolorSat, recolorLight,
            recolorTheory, recolorTier, glowColor);

    /// <summary>POST /WeaponForge/ForgeImport?expansion=tbc|wotlk|vanilla — lane-keyed form.</summary>
    [HttpPost]
    public Task<IActionResult> ForgeImport(string? expansion = null, uint entry = 0, string? model = null, uint displayRow = 0,
        string? name = null, string? weaponType = null, int targetTriangles = 0,
        int brightness = 0, int saturation = 0, string? itemConfig = null,
        GlbShapeControls? shape = null, bool flipGripEnd = false, int itemVisual = -1, bool glowSpread = false,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved", string? glowColor = null) =>
        string.Equals(expansion, VanillaMpqSource.SourceKey, StringComparison.OrdinalIgnoreCase)
            ? ForgePreservedVanilla(entry, model, displayRow, name, weaponType, targetTriangles,
                brightness, saturation, itemConfig, shape, flipGripEnd, itemVisual, glowSpread,
                recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor)
            : LegacyForge(_sources.Get(expansion), entry, model, displayRow, name, weaponType,
                targetTriangles, brightness, saturation, itemConfig, shape, flipGripEnd, itemVisual,
                glowSpread, recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor);

    private async Task<IActionResult> ForgePreservedVanilla(uint entry, string? model, uint displayRow,
        string? name, string? weaponType, int targetTriangles, int brightness, int saturation,
        string? itemConfig, GlbShapeControls? shape, bool flipGripEnd, int itemVisual, bool glowSpread,
        float? recolorHue, float? recolorSat, float? recolorLight, string recolorTheory, string recolorTier, string? glowColor)
    {
        var (configuredItem, configurationErrors) = await ParseItemConfigurationAsync(
            itemConfig, HttpContext.RequestAborted);
        if (configurationErrors.Count > 0)
            return BadRequest(new
            {
                ok = false,
                error = "The Vanilla item configuration is invalid.",
                errors = configurationErrors,
            });

        var src = _sources.Vanilla;
        var (sel, item) = ResolveLegacySelection(src, entry, model, displayRow);
        if (sel is null)
            return NotFound(new { ok = false, error = $"Unknown Vanilla weapon (entry {entry}, model '{model}')." });

        string? typeKey = string.IsNullOrWhiteSpace(weaponType) && item is not null
            ? LegacyItemCatalog.TypeKeyFor(item.ItemClass, item.Subclass)
            : weaponType;
        var profile = WeaponTypeCatalog.Find(typeKey);
        if (profile is null)
            return BadRequest(new { ok = false, error = $"Unknown or unsupported weapon family '{typeKey}'." });
        var familyErrors = ValidateConfigurationForWeaponFamily(profile, configuredItem);
        if (familyErrors.Count > 0)
            return BadRequest(new { ok = false, error = "The item configuration does not match the weapon family.", errors = familyErrors });

        if (!IsSourceGraphPlacementIdentity(shape, flipGripEnd) || glowSpread)
            return BadRequest(new
            {
                ok = false,
                error = "Vanilla source-preserving recolor cannot reshape, flip, or redistribute glow anchors. " +
                        "Those edits would require rewriting the complete animated rig.",
            });

        var (source, loadError) = LoadPreservedVanillaWeapon(src, sel);
        if (source is null)
            return Json(new { ok = false, expansion = src.Key, error = loadError });

        PreparedNativeEffectTint? nativeTint = null;
        if (!string.IsNullOrWhiteSpace(glowColor))
        {
            var (prepared, effectError) = PrepareNativeEffectTint(source, glowColor);
            if (prepared is null)
                return BadRequest(new { ok = false, expansion = src.Key, error = effectError });
            nativeTint = prepared;
        }

        if (targetTriangles > 0)
            source.Diagnostics.Info("vanilla.source.target-ignored",
                $"Triangle target {targetTriangles:N0} ignored: decimation would detach vertices from the source bone rig.");

        byte[] texturePng = source.DisplayPng;
        bool recolorBaked = false;
        if (recolorHue.HasValue)
        {
            int seed = RetextureSupport.SeedFor((int)sel.DisplayRow, recolorTier);
            byte[]? recolored = await RecolorTexturePngAsync(texturePng, seed, recolorHue.Value, recolorSat, recolorLight,
                recolorTheory, recolorTier, HttpContext.RequestAborted);
            if (recolored is not null) { texturePng = recolored; recolorBaked = true; }
            else source.Diagnostics.Warn("recolor.bake",
                "The Type-2 skin has no recolorable colour families; forging its original texture.");
        }

        byte[]? adjustedPng = AdjustTexture(texturePng, brightness, saturation);
        bool textureChanged = recolorBaked || brightness != 0 || saturation != 0;
        byte[]? packagedBlp = textureChanged && adjustedPng is { Length: > 0 }
            ? EncodePreservedDisplayBlp(adjustedPng)
            : source.DisplayBlp;
        if (packagedBlp is not { Length: > 0 })
            return Json(new { ok = false, error = "The recolored Type-2 display skin could not be encoded as a vanilla BLP." });

        var visual = ChooseSourceVisual(source.Model, src.Mpq.ItemVisuals(), sel.DisplayRow,
            itemVisual, source.Diagnostics);

        Dictionary<string, string>? itemOverrides = null;
        if (item is not null)
        {
            itemOverrides = new Dictionary<string, string>
            {
                ["sheath"] = item.Sheath.ToString(),
                ["delay"] = item.DelayMs.ToString(),
            };
            if (!profile.IsRanged && item.InventoryType is 13 or 17 or 21 or 22)
                itemOverrides["inventory_type"] = item.InventoryType.ToString();
        }
        itemOverrides = MergeItemOverrides(itemOverrides, configuredItem);

        string buildName = !string.IsNullOrWhiteSpace(configuredItem?.Name) ? configuredItem.Name!
            : !string.IsNullOrWhiteSpace(name) ? name.Trim()
            : item is not null ? item.Name
            : PrettyTbcName(sel.ModelStem);
        string generatorJson = JsonSerializer.Serialize(new
        {
            sourceExpansion = src.Key,
            sourceExpansionLabel = src.Label,
            sourceItemEntry = item?.Entry ?? 0,
            sourceItemName = item?.Name,
            sourceModel = sel.ModelStem,
            sourceTexture = sel.TextureStem,
            sourceDisplayRow = sel.DisplayRow,
            sourceM2Preserved = true,
            sourceM2Version = source.Model.Version,
            sourceBones = source.Model.Bones.Count,
            sourceGlobalSequences = source.Model.GlobalSequenceDurations.Count,
            brightness,
            saturation,
            itemVisual = visual.Chosen,
            recolor = recolorBaked ? (object?)new
            {
                hue = recolorHue,
                theory = recolorTheory,
                tier = recolorTier,
            } : null,
            nativeEffectRecolor = nativeTint is null ? null : new
            {
                color = glowColor,
                textureSheets = nativeTint.Textures.Count,
                materialColorTracks = nativeTint.ColorTracksRecolored,
                emitterRamps = nativeTint.EmittersRecolored,
                itemVisualPreserved = visual.Chosen,
                type3Preserved = true,
            },
        });

        try
        {
            CustomWeaponBuildRequest request = nativeTint is null
                ? CreatePreservedVanillaBuildRequest(buildName, profile.Key, itemOverrides,
                    source.M2Bytes, packagedBlp, sel.IconStem, visual.Chosen,
                    sel.GroupSoundIndex, sel.SpellVisualId, sel.MirrorModelName2, generatorJson)
                : CreatePreservedVanillaEffectBuildRequest(buildName, profile.Key, itemOverrides,
                    source.M2Bytes, nativeTint.M2Bytes, packagedBlp, sel.IconStem, visual.Chosen,
                    sel.GroupSoundIndex, sel.SpellVisualId, sel.MirrorModelName2, generatorJson,
                    nativeTint.Textures);
            var result = await _builder.BuildAsync(request);
            return Json(BuildResultJson(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeaponForge: source-preserved Vanilla forge {Model} failed", sel.ModelStem);
            return Json(new { ok = false, error = ex.Message, diagnostics = source.Diagnostics.Items.Select(i => i.ToString()) });
        }
    }

    private async Task<IActionResult> LegacyForge(LegacyImportSource src, uint entry, string? model, uint displayRow,
        string? name, string? weaponType, int targetTriangles, int brightness, int saturation, string? itemConfig,
        GlbShapeControls? shape, bool flipGripEnd, int itemVisual, bool glowSpread,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "primary", string recolorTier = "improved", string? glowColor = null)
    {
        var (configuredItem, configurationErrors) = await ParseItemConfigurationAsync(
            itemConfig, HttpContext.RequestAborted);
        if (configurationErrors.Count > 0)
            return BadRequest(new
            {
                ok = false,
                error = "The Vanilla item configuration is invalid.",
                errors = configurationErrors,
            });

        var (sel, item) = ResolveLegacySelection(src, entry, model, displayRow);
        if (sel is null) return NotFound(new { ok = false, error = $"Unknown {src.Label} weapon (entry {entry}, model '{model}')." });

        string? typeKey = string.IsNullOrWhiteSpace(weaponType) && item is not null
            ? LegacyItemCatalog.TypeKeyFor(item.ItemClass, item.Subclass)
            : weaponType;
        // Fail closed. Get()'s fallback would forge a 1H SWORD for any family it does not know —
        // including the subclasses the catalog deliberately declines (fist weapon, spear, fishing
        // pole) — and nothing downstream would say so.
        var profile = WeaponTypeCatalog.Find(typeKey);
        if (profile is null)
            return BadRequest(new
            {
                ok = false,
                error = string.IsNullOrWhiteSpace(typeKey)
                    ? $"{src.Label} item {entry} is a weapon subclass the Forge has no family for (fist weapon, spear and fishing pole are not importable). Pick a family explicitly to forge it as that family instead."
                    : $"Unknown weapon family '{typeKey}'.",
            });
        var familyErrors = ValidateConfigurationForWeaponFamily(profile, configuredItem);
        if (familyErrors.Count > 0)
            return BadRequest(new { ok = false, error = "The item configuration does not match the weapon family.", errors = familyErrors });
        WeaponDonorInfo donor;
        try { donor = _donors.Resolve(profile); }
        catch (Exception ex)
        { return Json(new { ok = false, error = $"No stock donor for {profile.Label}: {ex.Message}" }); }

        var (loadedMesh, m2Bytes, texturePng, effectPngs, textureBlp, effectBlps, diag, err, sourceModel) = LoadLegacyWeapon(src, sel, targetTriangles);
        if (loadedMesh is null)
            return Json(new { ok = false, expansion = src.Key, error = err, diagnostics = diag.Items.Select(i => i.ToString()) });

        // Bake the previewed recolor into the shipped skin — same palette engine and seed the
        // preview used, so what was on screen is what ships. A successful recolor is new art, so
        // the source BLP2 bytes are dropped and the builder re-encodes from the recolored PNG.
        bool recolorBaked = false;
        if (recolorHue.HasValue && texturePng is { Length: > 0 })
        {
            int rseed = RetextureSupport.SeedFor((int)sel.DisplayRow, recolorTier);
            var rp = await RecolorTexturePngAsync(texturePng, rseed, recolorHue.Value, recolorSat, recolorLight, recolorTheory, recolorTier, HttpContext.RequestAborted);
            if (rp is not null) { texturePng = rp; recolorBaked = true; }
            else diag.Warn("recolor.bake", "The skin has no recolorable colour families; forging the original texture.");
        }

        var (mesh, attachmentsWoW, sourceGripPercent, suggestion, chosenVisual, _, emitterPositions) =
            PlaceLegacyWeapon(loadedMesh, sourceModel, shape, flipGripEnd, itemVisual, diag,
                src.Mpq.ItemVisuals(), sel.DisplayRow, glowSpread);
        var motionPlan = PlanMotion(sourceModel, emitterPositions, sel.ModelStem);
        foreach (var note in motionPlan.Notes) diag.Info("motion.emitter", note);

        Vector3? glowRgb = HexToRgb255(glowColor);
        if (!string.IsNullOrWhiteSpace(glowColor) && glowRgb is null)
            return BadRequest(new { ok = false, expansion = src.Key, error = "Glow color must be a six-digit hex color such as #33aaff." });

        // Later-client material effects are independent of particle emitters. In particular, the
        // Warglaive's moving green shell is ArmorReflect3 in an environment-mapped Blend-4 pass.
        // Recolor its pixels only; the render pass (including the 0xFFFF EnvMap coordinate) remains
        // untouched. Emptying the changed source-BLP entry is intentional: the builder otherwise
        // prefers that original BLP over the recolored PNG and silently ships the stock green sheet.
        IReadOnlyList<int> effectTextureSlotsRecolored = Array.Empty<int>();
        if (glowRgb is Vector3 effectGlow)
        {
            try
            {
                var (effectHue, effectSaturation) = HueAndSaturation(effectGlow);
                LegacyWeaponEffectTint effectTint = LegacyWeaponEffectRecolor.Apply(
                    mesh, effectPngs, effectBlps, effectHue, effectSaturation,
                    GlowLightnessScale(effectGlow), IsDarkGlowPick(effectGlow));
                effectPngs = effectTint.Pngs;
                if (effectTint.Passes is not null)
                {
                    mesh.Passes = effectTint.Passes;
                    diag.Info("effect.tint.bake", "Dark glow pick: compositing passes re-flagged from additive to alpha-blend so the effect draws as a dark aura.");
                }
                effectBlps = effectTint.Blps;
                effectTextureSlotsRecolored = effectTint.TextureSlots;
                if (effectTextureSlotsRecolored.Count > 0)
                    diag.Info("effect.tint.bake",
                        $"Hue-shifted compositing effect texture slot(s) {string.Join(", ", effectTextureSlotsRecolored)}; environment mapping and pass animation remain intact.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                return BadRequest(new
                {
                    ok = false,
                    expansion = src.Key,
                    error = $"The imported effect textures could not be recolored safely: {ex.Message}",
                });
            }
        }

        // Particle effects are a separate channel: override every planned graft's authored colour
        // ramp (ColorRamp=null so the selected flat ColorRgb wins).
        IReadOnlyList<M2EmitterTransplanter.Graft> motionGrafts = motionPlan.Grafts;
        if (glowRgb is Vector3 gc && motionGrafts.Count > 0)
        {
            motionGrafts = motionGrafts.Select(g => g with { ColorRgb = gc, ColorRamp = null }).ToList();
            diag.Info("motion.tint", $"{motionGrafts.Count} rebuilt emitter(s) tinted to the chosen glow colour.");
        }
        if (mesh.TriangleCount > MaxTbcForgeTriangles)
            return Json(new
            {
                ok = false,
                error = $"{mesh.TriangleCount:N0} triangles exceeds the vanilla M2 UInt16 index budget ({MaxTbcForgeTriangles:N0}).",
            });

        // Carry the SOURCE item's own presentation fields over the family defaults: sheath is the
        // big one (Warglaives are 1H swords with the two-hander back-sheath value 1 — the crossed-
        // on-back look; the client picks back-LEFT for the main-hand slot and back-RIGHT for the
        // off-hand slot automatically), plus the authentic slot binding and swing delay. Damage
        // stays donor-level on purpose — stats are made in vanilla terms, not imported TBC/WotLK
        // power (DPS is never carried).
        Dictionary<string, string>? itemOverrides = null;
        if (item is not null)
        {
            itemOverrides = new Dictionary<string, string>
            {
                ["sheath"] = item.Sheath.ToString(),
                ["delay"] = item.DelayMs.ToString(),
            };
            // Melee families keep the source item's own slot binding; ranged families are bound to
            // exactly one vanilla slot (bows 15, guns/crossbows/wands 26, thrown 25), so the
            // family contract wins even when the source row used a different ranged slot.
            if (!profile.IsRanged && item.InventoryType is 13 or 17 or 21 or 22)
                itemOverrides["inventory_type"] = item.InventoryType.ToString();
        }
        if (profile.IsRanged)
            diag.Warn("import.ranged.rigid",
                $"{profile.Label} import is rigid on the family scaffold's root bone: the {src.Label} model's limb/string/hammer animation is not carried; its projectile visual ({donor.SpellVisualId}) and ranged slot are.");
        // Explicit modal values layer over both the family defaults (inside the builder) and the
        // source presentation values above. Omitted values preserve those existing contracts.
        itemOverrides = MergeItemOverrides(itemOverrides, configuredItem);

        // ── Authentic source icon ───────────────────────────────────────────────────────────────
        // The import used to ship the family DONOR's icon, so a forged Warglaive showed a plain
        // sword. The source display row names its own icon; use it, and carry the BLP across only
        // when the vanilla client has no file by that name (most TBC icon names already exist in
        // 1.12 — an icon shared with a vanilla item resolves for free).
        string? iconStem = string.IsNullOrWhiteSpace(sel.IconStem) ? null : sel.IconStem;
        byte[]? iconBlp = null;
        if (iconStem is not null)
        {
            string member = $@"Interface\Icons\{iconStem}.blp";
            // Only Blizzard's own archives count as "vanilla has it" — the mounted client dir also
            // holds the forge's deployed patches, and an icon a previous import packaged reads back
            // as present, so the new item would skip packaging and lose its icon on the next
            // registry rebuild (same defect measured on the armor side, 2026-08-24).
            int stockCeiling = Services.Mpq.MpqPatchOrder.Rank("patch-2.MPQ");
            bool IsCustomPatch(string n) => Services.Mpq.MpqPatchOrder.Rank(n) > stockCeiling;
            bool inVanilla = _mpq.ExtractFile(member, skipArchive: IsCustomPatch) is { Length: > 0 }
                          || _mpq.ExtractFile(member.ToLowerInvariant(), skipArchive: IsCustomPatch) is { Length: > 0 };
            if (!inVanilla)
            {
                iconBlp = src.Mpq.ExtractFile(member) ?? src.Mpq.ExtractFile(member.ToLowerInvariant());
                if (iconBlp is not { Length: > 0 })
                {
                    // Neither client has it (measured: 11 of 585, mostly consumable icons sitting on
                    // weapon rows). Fall back to the donor rather than ship a name nothing resolves.
                    diag.Warn("icon.unavailable",
                        $"Source icon '{iconStem}' is in neither the vanilla nor the {src.Label} client; using the family donor's icon instead.");
                    iconStem = null;
                    iconBlp = null;
                }
            }
        }

        try
        {
            byte[]? adjustedTexturePng = AdjustTexture(texturePng, brightness, saturation);
            // A baked recolor is new art: never ship the original BLP2 bytes underneath it.
            bool sourceGradeUnchanged = !recolorBaked && (brightness == 0 && saturation == 0 ||
                ReferenceEquals(adjustedTexturePng, texturePng));
            var result = await _builder.BuildAsync(new CustomWeaponBuildRequest
            {
                Name = !string.IsNullOrWhiteSpace(configuredItem?.Name) ? configuredItem.Name
                     : !string.IsNullOrWhiteSpace(name) ? name
                     : item is not null ? item.Name
                     : PrettyTbcName(sel.ModelStem),
                SourceKind = src.Key + "_import",   // "tbc_import" / "wotlk_import"
                // The source item's OWN inventory icon, and its bytes only when 1.12 lacks a file by
                // that name. Measured across the TBC weapon set: 472 of 585 icon names already exist
                // in vanilla, so most imports package nothing and simply reference the name.
                IconStem = iconStem,
                IconBlp = iconBlp,
                WeaponTypeKey = profile.Key,
                ItemOverrides = itemOverrides,
                Mesh = mesh,
                Topology = WeaponTopologyMode.Variable,
                VariableTriangleHardCeiling = MaxTbcForgeTriangles,
                TexturePng = adjustedTexturePng,
                TextureBlp = sourceGradeUnchanged ? textureBlp : null,
                EffectTexturesPng = effectPngs,
                EffectTexturesBlp = effectBlps,
                SourceBlob = m2Bytes,
                ItemVisual = chosenVisual,
                MotionGrafts = motionGrafts.Count > 0 ? motionGrafts : null,
                AttachmentPointsWoW = attachmentsWoW.Count > 0 ? attachmentsWoW : null,
                // The tbc* keys are the lane-neutral names the builder reads back (e.g.
                // tbcInventoryType recovers the slot on rebuild); sourceExpansion says which lane.
                GeneratorParamsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    sourceExpansion = src.Key,
                    sourceExpansionLabel = src.Label,
                    tbcItemEntry = item?.Entry ?? 0,
                    tbcItemName = item?.Name,
                    tbcModel = sel.ModelStem,
                    tbcTexture = sel.TextureStem,
                    tbcDisplayRow = sel.DisplayRow,
                    tbcSheath = item?.Sheath,
                    tbcInventoryType = item?.InventoryType,
                    tbcGlowPasses = mesh.Passes?.Count(p => p.BlendMode >= 3) ?? 0,
                    targetTriangles,
                    sourceGripPercent = MathF.Round(sourceGripPercent, 1),
                    placement = LegacyPlacement.IsIdentity(shape, flipGripEnd) ? null : new
                    {
                        gripPercent = shape?.GripPercent, offsetUpCm = shape?.OffsetUpCm, offsetSideCm = shape?.OffsetSideCm,
                        pitchDegrees = shape?.PitchDegrees, yawDegrees = shape?.YawDegrees, sizePercent = shape?.SizePercent,
                        lengthPercent = shape?.LengthPercent, flipGripEnd, flipUpsideDown = shape?.FlipUpsideDown, mirrorSide = shape?.MirrorSide,
                    },
                    itemVisual = chosenVisual,
                    itemVisualSuggested = suggestion?.ItemVisual,
                    sourceParticleEmitters = sourceModel?.ParticleEmitters.Count ?? 0,
                    attachmentsCarried = attachmentsWoW.Count,
                    // Provenance of the baked appearance edits (the registry blobs already carry them).
                    recolor = recolorBaked ? (object?)new { hue = recolorHue, theory = recolorTheory, tier = recolorTier } : null,
                    glowColor = glowRgb is null ? null : glowColor,
                    effectTextureSlotsRecolored,
                }),
                WriterVersion = "tbc-rendergraph-v3-effect-tint",
            });
            return Json(BuildResultJson(result, BuildGripInfo(mesh, profile, donor)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeaponForge: Forge{Lane} {Model} failed", src.Key, sel.ModelStem);
            return Json(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>"Sword_2H_Blood_D_02" → "Sword 2H Blood D 02" — a readable default item name.</summary>
    private static string PrettyTbcName(string stem) => stem.Replace('_', ' ');

    private static int EffectiveTbcInventoryType(LegacyItemInfo? item, WeaponTypeProfile profile) =>
        !profile.IsRanged && item?.InventoryType is 13 or 17 or 21 or 22 ? item.InventoryType : profile.InventoryType;

    /// <summary>Decode a TBC BLP2's base mip to PNG for the texture pipeline. Null on failure.</summary>
    private static byte[]? BlpToPng(byte[] blp)
    {
        try
        {
            var bgra = BlpDecoder.GetPixels(blp, 0, out int w, out int h);
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var img = SKImage.FromPixelCopy(info, bgra);
            if (img is null) return null;
            using var png = img.Encode(SKEncodedImageFormat.Png, 100);
            return png?.ToArray();
        }
        catch
        {
            return null;
        }
    }

    // ── M2 structural dump helpers (vanilla MD20 v256 fixed header offsets) ──

    private static uint HU32(byte[] b, int o) =>
        o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)) : 0;
    private static ushort HU16(byte[] b, int o) =>
        o + 2 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2)) : (ushort)0;
    private static float HF(byte[] b, int o) =>
        o + 4 <= b.Length ? BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o, 4)) : 0f;
    private static float[] HV3(byte[] b, int o) => new[] { HF(b, o), HF(b, o + 4), HF(b, o + 8) };

    private static object DumpM2(byte[] m2, int expectedViews)
    {
        var doc = RawM2Document.Parse(m2, out var err);
        var validator = M2BinaryValidator.Validate(m2, expectedVertexCount: doc?.VertexCount ?? 0, expectedViews: expectedViews);

        uint nVerts = HU32(m2, 0x44), ofsVerts = HU32(m2, 0x48);
        uint nViews = HU32(m2, 0x4C), ofsViews = HU32(m2, 0x50);

        object DumpVertex(uint index)
        {
            int o = (int)(ofsVerts + index * 48);
            if (o + 48 > m2.Length) return new { index, error = "out of bounds" };
            return new
            {
                index,
                pos = HV3(m2, o),
                weights = new[] { m2[o + 12], m2[o + 13], m2[o + 14], m2[o + 15] },
                bones = new[] { m2[o + 16], m2[o + 17], m2[o + 18], m2[o + 19] },
                normal = HV3(m2, o + 20),
                uv = new[] { HF(m2, o + 32), HF(m2, o + 36) },
            };
        }

        object? DumpView(RawM2View v)
        {
            if (!v.HeaderInBounds) return new { v.Index, error = "header out of bounds" };
            object? submesh = null;
            if (v.Submeshes.Count > 0 && v.Submeshes.InBounds)
            {
                int s = (int)v.Submeshes.Offset;
                submesh = new
                {
                    id = HU32(m2, s + 0),
                    vertexStart = HU16(m2, s + 4),
                    vertexCount = HU16(m2, s + 6),
                    indexStart = HU16(m2, s + 8),
                    indexCount = HU16(m2, s + 10),
                    boneCount = HU16(m2, s + 12),
                    boneComboIndex = HU16(m2, s + 14),
                    boneInfluences = HU16(m2, s + 16),
                    centerBoneIndex = HU16(m2, s + 18),
                    center = HV3(m2, s + 20),
                    rawHex = Convert.ToHexString(m2.AsSpan(s, Math.Min(32, m2.Length - s))),
                };
            }
            object? batch = null;
            if (v.Batches.Count > 0 && v.Batches.InBounds)
            {
                int t = (int)v.Batches.Offset;
                var u16s = new ushort[12];
                for (int k = 0; k < 12; k++) u16s[k] = HU16(m2, t + k * 2);
                batch = new { fieldsU16 = u16s, rawHex = Convert.ToHexString(m2.AsSpan(t, Math.Min(24, m2.Length - t))) };
            }

            var lookupSample = new List<ushort>();
            var triSample = new List<ushort>();
            if (v.VertexLookup.InBounds)
                for (uint k = 0; k < Math.Min(8, v.VertexLookup.Count); k++)
                    lookupSample.Add(HU16(m2, (int)(v.VertexLookup.Offset + k * 2)));
            if (v.Triangles.InBounds)
                for (uint k = 0; k < Math.Min(12, v.Triangles.Count); k++)
                    triSample.Add(HU16(m2, (int)(v.Triangles.Offset + k * 2)));

            return new
            {
                v.Index,
                headerOffset = v.HeaderOffset,
                vertexLookup = new { v.VertexLookup.Count, v.VertexLookup.Offset, v.VertexLookup.InBounds },
                triangles = new { v.Triangles.Count, v.Triangles.Offset, v.Triangles.InBounds },
                properties = new { v.Properties.Count, v.Properties.Offset, v.Properties.InBounds },
                submeshes = new { v.Submeshes.Count, v.Submeshes.Offset, v.Submeshes.InBounds },
                batches = new { v.Batches.Count, v.Batches.Offset, v.Batches.InBounds },
                lod = v.Lod,
                lookupSample,
                triSample,
                submesh0 = submesh,
                batch0 = batch,
            };
        }

        var vertexSamples = new List<object>();
        if (nVerts > 0)
        {
            vertexSamples.Add(DumpVertex(0));
            if (nVerts > 2) vertexSamples.Add(DumpVertex(nVerts / 2));
            vertexSamples.Add(DumpVertex(nVerts - 1));
        }

        return new
        {
            fileSize = m2.Length,
            parseError = err,
            name = doc?.Name,
            nameLen = HU32(m2, 0x08),
            nameOfs = HU32(m2, 0x0C),
            globalFlags = HU32(m2, 0x10),
            nVertices = nVerts,
            ofsVertices = ofsVerts,
            nViews,
            ofsViews,
            vertexBox = new { min = HV3(m2, 0x0B4), max = HV3(m2, 0x0C0), radius = HF(m2, 0x0CC) },
            boundingBox = new { min = HV3(m2, 0x0D0), max = HV3(m2, 0x0DC), radius = HF(m2, 0x0E8) },
            headerHex = Convert.ToHexString(m2.AsSpan(0, Math.Min(0x100, m2.Length))),
            views = doc?.Views.Select(DumpView).ToArray(),
            vertexSamples,
            validator = validator.Items.Select(i => i.ToString()).ToArray(),
        };
    }

    private static List<string> CompareM2(byte[] donor, byte[] forged)
    {
        var notes = new List<string>();
        var dDoc = RawM2Document.Parse(donor, out _);
        var fDoc = RawM2Document.Parse(forged, out _);
        if (dDoc is null || fDoc is null) { notes.Add("parse failure — see per-file parseError"); return notes; }

        void Check(string label, bool ok, string detail) => notes.Add($"{(ok ? "OK " : "BAD")} {label}: {detail}");

        // Vertex weights: a zero weight sum collapses vertices to the origin in the client.
        uint fOfs = HU32(forged, 0x48);
        uint fN = HU32(forged, 0x44);
        int zeroWeight = 0;
        for (uint i = 0; i < fN; i++)
        {
            int o = (int)(fOfs + i * 48);
            if (o + 16 > forged.Length) break;
            if (forged[o + 12] + forged[o + 13] + forged[o + 14] + forged[o + 15] == 0) zeroWeight++;
        }
        Check("vertex weights", zeroWeight == 0, $"{zeroWeight}/{fN} vertices have all-zero bone weights");

        // Triangle indices in range of the lookup.
        var fv0 = fDoc.Views[0];
        bool triOk = true; uint triMax = 0;
        for (uint k = 0; k < fv0.Triangles.Count && fv0.Triangles.InBounds; k++)
        {
            ushort ix = HU16(forged, (int)(fv0.Triangles.Offset + k * 2));
            triMax = Math.Max(triMax, ix);
            if (ix >= fv0.VertexLookup.Count) { triOk = false; break; }
        }
        Check("triangle indices", triOk, $"max {triMax} vs lookup count {fv0.VertexLookup.Count}");

        // Bounds sanity.
        float fRadius = HF(forged, 0x0CC);
        Check("bounds radius", fRadius > 0.01f && fRadius < 50f, $"vertexBox radius {fRadius}");

        // Structural equality of the donor-templated records.
        var dv0 = dDoc.Views[0];
        if (dv0.Batches.Count > 0 && fv0.Batches.Count > 0)
        {
            var dB = donor.AsSpan((int)dv0.Batches.Offset, 24).ToArray();
            var fB = forged.AsSpan((int)fv0.Batches.Offset, 24).ToArray();
            Check("batch template", dB.AsSpan().SequenceEqual(fB), Convert.ToHexString(dB) + " vs " + Convert.ToHexString(fB));
        }
        if (dv0.Submeshes.Count > 0 && fv0.Submeshes.Count > 0)
        {
            int ds = (int)dv0.Submeshes.Offset, fs = (int)fv0.Submeshes.Offset;
            Check("submesh bone fields",
                HU16(donor, ds + 12) == HU16(forged, fs + 12) && HU16(donor, ds + 14) == HU16(forged, fs + 14) &&
                HU16(donor, ds + 16) == HU16(forged, fs + 16) && HU16(donor, ds + 18) == HU16(forged, fs + 18),
                $"donor ({HU16(donor, ds + 12)},{HU16(donor, ds + 14)},{HU16(donor, ds + 16)},{HU16(donor, ds + 18)}) vs " +
                $"forged ({HU16(forged, fs + 12)},{HU16(forged, fs + 14)},{HU16(forged, fs + 16)},{HU16(forged, fs + 18)})");
            Check("submesh id", HU32(donor, ds) == HU32(forged, fs), $"donor {HU32(donor, ds)} vs forged {HU32(forged, fs)}");
        }
        Check("view lod dword", dv0.Lod == fv0.Lod, $"donor {dv0.Lod} vs forged {fv0.Lod}");
        Check("view count", fDoc.Views.Count == dDoc.Views.Count, $"donor {dDoc.Views.Count} vs forged {fDoc.Views.Count}");

        // Every forged view array must be in bounds.
        foreach (var v in fDoc.Views)
            Check($"view {v.Index} arrays in bounds",
                v.HeaderInBounds && v.VertexLookup.InBounds && v.Triangles.InBounds &&
                v.Properties.InBounds && v.Submeshes.InBounds && v.Batches.InBounds,
                "vertexLookup/triangles/properties/submeshes/batches");

        return notes;
    }

    /// <summary>Brightness/saturation grade on the embedded texture (−100..+100 each), applied
    /// IDENTICALLY for preview and forge so what you see is what packages. Brightness is
    /// multiplicative (+100 ≈ ×2, −100 ≈ ×½ — blacks stay black); saturation blends toward
    /// (−) or away from (+) luminance grey. Zero/zero returns the input untouched.</summary>
    // ── Pre-import recolor (Armor Forge parity) ─────────────────────────
    // The retexture engine resolves textures from the VANILLA client by displayId, so it can't touch
    // a not-yet-imported foreign weapon. These paths recolor the SOURCE skin the import pipeline
    // already decoded, so a TBC/WotLK weapon recolors live in the preview and bakes on forge.

    /// <summary>Recolor a decoded skin PNG at the chosen primary hue via the palette engine (the
    /// engine is file-path based, so this round-trips through a temp dir). Null when the skin has no
    /// detectable colour families, so callers fall back to the original texture.</summary>
    private async Task<byte[]?> RecolorTexturePngAsync(byte[] png, int seed, float hue, float? sat, float? light, string theory, string tier, CancellationToken ct)
    {
        if (Array.IndexOf(PaletteSwapService.RecolorTheories, theory) < 0) theory = "none";
        var (kd, ku, mm, pop) = RetextureSupport.TierShape(tier);
        string tmpDir = Path.Combine(Path.GetTempPath(), "weaponbake", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            string basePng = Path.Combine(tmpDir, "skin.png");
            await System.IO.File.WriteAllBytesAsync(basePng, png, ct);
            string outPng = Path.Combine(tmpDir, "skin_r.png");
            // Forge recolor = the WHOLE skin shifts to the chosen primary: full swap budget (1.01),
            // unleashed hue (180°) — matching the Armor Forge, unlike the retexture engine's tiered
            // budget which pins dominant/dark regions to their source colour.
            var ok = await _palette.RecolorSeededAsync(basePng, outPng, seed, 1f, 0f, tintStructural: true, ct,
                theory, kd, ku, mm, pop, swapBudget: 1.01f, hueLeash: 180f, value: ValueSettings.Keep, baseHueOverride: hue,
                baseSatOverride: sat, baseLightOverride: light);
            return ok is null ? null : await System.IO.File.ReadAllBytesAsync(outPng, ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "WeaponForge: recolor failed"); return null; }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    /// <summary>Stable palette seed for the GLB route, which has no display row: the source GLB's
    /// own sha (same file → same recolor in preview and forge; different files roll differently).</summary>
    private static int GlbRecolorSeed(string? sourceSha256) =>
        sourceSha256 is { Length: >= 8 } &&
        uint.TryParse(sourceSha256.AsSpan(0, 8), System.Globalization.NumberStyles.HexNumber, null, out uint h)
            ? unchecked((int)h) : 0;

    /// <summary>GET /WeaponForge/PrimaryColor?expansion=&amp;entry=&amp;model=&amp;displayRow= — the foreign
    /// weapon skin's majority colour, so the recolor picker can seed itself. { success, primaryHex }.</summary>
    [HttpGet]
    public async Task<IActionResult> PrimaryColor(string? expansion, uint entry = 0, string? model = null, uint displayRow = 0)
    {
        var src = _sources.Get(expansion);
        var (sel, _) = ResolveLegacySelection(src, entry, model, displayRow);
        if (sel is null) return Json(new { success = false, error = $"Unknown {src.Label} weapon." });
        if (sel.BlpPath is null) return Json(new { success = false, error = "No display texture to sample." });
        var blp = src.Mpq.ExtractFile(sel.BlpPath);
        var png = blp is { Length: > 0 } ? BlpToPng(blp) : null;
        if (png is null) return Json(new { success = false, error = "The skin could not be decoded." });

        string tmpDir = Path.Combine(Path.GetTempPath(), "weaponbake", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            string disk = Path.Combine(tmpDir, "skin.png");
            await System.IO.File.WriteAllBytesAsync(disk, png, HttpContext.RequestAborted);
            var families = _palette.DetectFamilies(disk);
            if (families.Count == 0) return Json(new { success = false, error = "no colour families detected" });
            var chromatic = families.Where(f => f.Family is not ("white" or "black" or "grey")).ToList();
            var primary = (chromatic.Count > 0 ? chromatic : families).OrderByDescending(f => f.Percent).First();
            return Json(new { success = true, primaryHex = HslToHex(primary.MeanHue, Math.Max(0.5f, primary.MeanSat), 0.5f) });
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    // HSL (h degrees, s/l 0..1) → #rrggbb, for the colour picker (Armor Forge parity).
    private static string HslToHex(float h, float s, float l)
    {
        h = ((h % 360f) + 360f) % 360f;
        float c = (1 - Math.Abs(2 * l - 1)) * s;
        float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        float mm = l - c / 2;
        float r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
        int R = (int)Math.Round((r + mm) * 255), G = (int)Math.Round((g + mm) * 255), B = (int)Math.Round((b + mm) * 255);
        return $"#{R:x2}{G:x2}{B:x2}";
    }

    // "#rrggbb" → RGB 0..255 vector for the emitter colour track; null when unset/malformed.
    private static Vector3? HexToRgb255(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return null;
        const System.Globalization.NumberStyles H = System.Globalization.NumberStyles.HexNumber;
        return int.TryParse(hex.AsSpan(0, 2), H, null, out int r)
            && int.TryParse(hex.AsSpan(2, 2), H, null, out int g)
            && int.TryParse(hex.AsSpan(4, 2), H, null, out int b)
            ? new Vector3(r, g, b) : null;
    }

    /// <summary>How much of the effect's own brightness a glow pick keeps. Effects are additive, so
    /// hue/saturation alone cannot express a dark pick — black desaturates to a WHITE glow. Picks at
    /// or above mid-lightness keep the effect's full brightness; darker picks dim it in proportion,
    /// and pure black switches the tinted effect off.</summary>
    private static float GlowLightnessScale(Vector3 rgb255)
    {
        float r = Math.Clamp(rgb255.X / 255f, 0f, 1f);
        float g = Math.Clamp(rgb255.Y / 255f, 0f, 1f);
        float b = Math.Clamp(rgb255.Z / 255f, 0f, 1f);
        float lightness = (MathF.Max(r, MathF.Max(g, b)) + MathF.Min(r, MathF.Min(g, b))) * 0.5f;
        return Math.Clamp(lightness / 0.5f, 0f, 1f);
    }

    /// <summary>A glow pick darker than mid-grey. Additive effects cannot draw it, so the effect is
    /// switched to an alpha-blended dark aura (see M2EffectBlendModeWriter / LegacyWeaponEffectRecolor).</summary>
    private static bool IsDarkGlowPick(Vector3 rgb255) => GlowLightnessScale(rgb255) < 0.999f;

    private static (float HueDegrees, float Saturation) HueAndSaturation(Vector3 rgb255)
    {
        float r = Math.Clamp(rgb255.X / 255f, 0f, 1f);
        float g = Math.Clamp(rgb255.Y / 255f, 0f, 1f);
        float b = Math.Clamp(rgb255.Z / 255f, 0f, 1f);
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;
        float lightness = (max + min) * 0.5f;
        if (delta < 0.0001f) return (0f, 0f);

        float saturation = lightness > 0.5f
            ? delta / (2f - max - min)
            : delta / (max + min);
        float hue = max == r
            ? ((g - b) / delta + (g < b ? 6f : 0f)) * 60f
            : max == g
                ? ((b - r) / delta + 2f) * 60f
                : ((r - g) / delta + 4f) * 60f;
        return (hue, saturation);
    }

    // ── Itemization (tier/spec starting-point stats — Armor Forge parity) ─

    /// <summary>The class → archetype catalog for the spec picker in the Configure-item modal.</summary>
    // ── Vanilla lane (clone an existing 1.12 weapon) ────────────────────
    //
    // The third lane. It reads the LIVE world DB rather than a mounted client archive, so unlike
    // TBC/WotLK it is always available and needs no Settings path. Nothing is packaged: the clone
    // keeps the source's display, so its model, texture, sheath and grip are already in the client.

    /// <summary>GET /WeaponForge/VanillaWeapons?search=&amp;family=&amp;limit= — browse stock weapons
    /// (and shields) in the world DB as clone sources.</summary>
    [HttpGet]
    public async Task<IActionResult> VanillaWeapons(string? search = null, string? family = null, int limit = 60)
    {
        try
        {
            var weapons = await _builder.BrowseVanillaWeaponsAsync(search, family, limit);
            return Json(new
            {
                ok = true,
                total = weapons.Count,
                weapons = weapons.Select(w => new
                {
                    entry = w.Entry, name = w.Name, quality = w.Quality, itemLevel = w.ItemLevel,
                    displayId = w.DisplayId, itemClass = w.ItemClass, subclass = w.Subclass,
                    inventoryType = w.InventoryType, inventoryTypeLabel = CustomWeaponBuildService.InventoryTypeLabel(w.InventoryType),
                    delayMs = w.DelayMs, damageMin = w.DamageMin, damageMax = w.DamageMax,
                    family = w.Family, familyLabel = w.FamilyLabel, expansion = "vanilla",
                }),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: vanilla weapon browse failed");
            return Json(new { ok = false, total = 0, weapons = Array.Empty<object>(), error = ex.Message });
        }
    }

    /// <summary>GET /WeaponForge/VanillaPreview?displayId= — render a STOCK weapon's own display in
    /// the forge viewer.
    ///
    /// The other two lanes preview by re-emitting foreign art (<c>{Tbc|Wotlk}PreviewWeapon</c>) and a
    /// forged weapon previews from its stored bytes (<c>PreviewForged</c>). A clone has neither: it
    /// packages nothing and keeps the source display, so the model, texture and enchant glow are
    /// already in the mounted 1.12 client. <see cref="ItemTextureService.EnsureGlb"/> is the exact
    /// pipeline the Items page 3D view uses for a stock display — same <c>GlbWriter</c>, same
    /// <c>suiFx</c> manifest, same resolved <c>ItemVisual</c> effect models — so the vanilla lane gets
    /// the same viewer treatment (blend suffixes, env-map matcap, m2fx, item rig) for free. The result
    /// is the version-stamped on-demand cache under <c>wwwroot/item_models</c>, not a throwaway file.
    /// </summary>
    [HttpGet]
    public IActionResult VanillaPreview(uint displayId)
    {
        if (displayId == 0) return BadRequest(new { ok = false, error = "displayId is required." });
        try
        {
            string? webPath = _itemTextures.EnsureGlb(displayId);
            if (string.IsNullOrEmpty(webPath))
                return Json(new
                {
                    ok = false,
                    error = $"Display {displayId} has no renderable model in the mounted 1.12 client " +
                            "(ItemDisplayInfo row missing, or the item has no ObjectComponents M2 — " +
                            "quivers, ammo pouches and a few off-hand tomes are like this). The clone " +
                            "itself still works; only the 3D preview is unavailable.",
                });

            uint visualId = _dbc.GetItemModelInfo(displayId)?.ItemVisualId ?? 0;
            return Json(new { ok = true, previewGlbWebPath = webPath, displayId, itemVisual = visualId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: vanilla preview for display {DisplayId} failed", displayId);
            return Json(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>GET /WeaponForge/VanillaWeaponSource?entry= — the source weapon's real gameplay, to
    /// pre-fill the Configure modal. NOT optional for weapons: the modal always submits a full
    /// override set and its defaults are a 2–4 damage trinket, so a clone without this pre-fill
    /// silently re-rolls the source into junk.</summary>
    [HttpGet]
    public async Task<IActionResult> VanillaWeaponSource(uint entry)
    {
        CustomWeaponBuildService.VanillaWeaponSourceDto? s;
        try
        {
            s = await _builder.ReadVanillaWeaponSourceAsync(entry);
        }
        catch (Exception ex)
        {
            // Same { ok, error } envelope VanillaWeapons uses. An unhandled throw here became a 500
            // with an HTML body, and the caller's `await r.json()` then reported the operator's world
            // DB outage as "SyntaxError: Unexpected token '<'".
            _logger.LogWarning(ex, "WeaponForge: vanilla weapon source {Entry} failed", entry);
            return Json(new { ok = false, error = "Could not read the world database: " + ex.Message });
        }
        if (s is null) return Json(new { ok = false, error = $"Item {entry} is not a stock weapon or shield." });
        return Json(new
        {
            ok = true,
            name = s.Name, quality = s.Quality, itemLevel = s.ItemLevel, requiredLevel = s.RequiredLevel,
            buyPrice = s.BuyPrice, sellPrice = s.SellPrice,
            itemClass = s.ItemClass, subclass = s.Subclass,
            inventoryType = s.InventoryType, inventoryTypeLabel = CustomWeaponBuildService.InventoryTypeLabel(s.InventoryType),
            family = s.Family, familyLabel = s.FamilyLabel,
            damageMin = s.DamageMin, damageMax = s.DamageMax, damageType = s.DamageType, delayMs = s.DelayMs,
            sheath = s.Sheath, ammoType = s.AmmoType, rangeMod = s.RangeModPercent,
            armor = s.Armor, block = s.Block, maxDurability = s.MaxDurability, bonding = s.Bonding,
            allowableClass = s.AllowableClass, allowableRace = s.AllowableRace,
            requiredSkill = s.RequiredSkill, requiredSkillRank = s.RequiredSkillRank,
            requiredSpell = s.RequiredSpell, requiredHonorRank = s.RequiredHonorRank,
            requiredReputationFaction = s.RequiredReputationFaction, requiredReputationRank = s.RequiredReputationRank,
            holyRes = s.HolyRes, fireRes = s.FireRes, natureRes = s.NatureRes,
            frostRes = s.FrostRes, shadowRes = s.ShadowRes, arcaneRes = s.ArcaneRes,
            stats = s.Stats.Select(t => new { type = t.Type, value = t.Value }),
            spells = s.Spells.Select(sp => new
            {
                spellId = sp.SpellId, trigger = sp.Trigger, charges = sp.Charges, ppmRate = sp.PpmRate,
                cooldownMs = sp.CooldownMs, category = sp.Category, categoryCooldownMs = sp.CategoryCooldownMs,
            }),
        });
    }

    /// <summary>POST /WeaponForge/CloneVanilla — clone a stock weapon into a new custom entry and
    /// re-itemize it. No patch, no display id, no client restart.</summary>
    [HttpPost]
    public async Task<IActionResult> CloneVanilla(uint entry, string? name = null, string? itemConfig = null)
    {
        var (configuredItem, configurationErrors) = await ParseItemConfigurationAsync(
            itemConfig, HttpContext.RequestAborted);
        if (configurationErrors.Count > 0)
            return BadRequest(new { ok = false, error = "The Vanilla item configuration is invalid.", errors = configurationErrors });

        var result = await _builder.CloneVanillaWeaponAsync(entry, name, configuredItem);
        if (!result.Ok) return BadRequest(new { ok = false, error = result.Message });
        return Json(new
        {
            ok = true,
            sourceEntry = result.SourceEntry,
            itemEntry = result.ItemEntry,
            displayId = result.DisplayId,
            name = result.Name,
            family = result.Family,
            inventoryType = result.InventoryType,
            inventoryTypeLabel = CustomWeaponBuildService.InventoryTypeLabel(result.InventoryType),
            reloaded = result.Reloaded,
            reloadMessage = result.ReloadMessage,
            message = result.Message,
            note = "The clone reuses the source's display, so nothing is packaged and no client restart is needed. " +
                   "It is not in the Forged Weapons list (that list is keyed on custom displays) — manage it from the Items page.",
        });
    }

    [HttpGet]
    public IActionResult SpecCatalog() => Json(new { ok = true, classes = SpecProfileCatalog.ForUi() });

    /// <summary>Generate a curated starting-point gameplay draft for one weapon slot/family, nestled
    /// into the vanilla tier curve (DPS + stat budget) and shaped to the chosen class/archetype.
    /// Read-only; the client fills the Configure-item modal and the operator edits before forging.</summary>
    [HttpGet]
    public IActionResult Itemize(int inventoryType, string? familyKey = null, int classId = 0,
        string? archetype = null, int? level = null, double? tier = null, int? delayMs = null)
    {
        var d = _itemize.GenerateWeapon(new WeaponBudgetRequest(inventoryType, familyKey, classId, archetype, level, tier, delayMs));
        return Json(new
        {
            ok = true,
            summary = d.Summary,
            quality = d.Quality,
            itemLevel = d.ItemLevel,
            requiredLevel = d.RequiredLevel,
            buyPrice = d.BuyPrice,
            sellPrice = d.SellPrice,
            allowableClass = d.AllowableClass,
            dps = d.Dps,
            damageMin = d.DamageMin,
            damageMax = d.DamageMax,
            damageType = d.DamageType,
            delayMs = d.DelayMs,
            armor = d.Armor,
            block = d.Block,
            maxDurability = d.MaxDurability,
            stats = d.Stats.Select(s => new { type = s.Type, label = s.Label, value = s.Value }),
            effectSuggestions = d.EffectSuggestions,
        });
    }

    private static byte[]? AdjustTexture(byte[]? png, int brightness, int saturation)
    {
        if (png is null || (brightness == 0 && saturation == 0)) return png;
        try
        {
            using var src = SKBitmap.Decode(png);
            if (src is null) return png;
            float bright = MathF.Pow(2f, Math.Clamp(brightness, -100, 100) / 100f);
            float sat = 1f + Math.Clamp(saturation, -100, 100) / 100f;
            const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            float sr = (1 - sat) * lr, sg = (1 - sat) * lg, sb = (1 - sat) * lb;
            var m = new float[]
            {
                (sr + sat) * bright, sg * bright,         sb * bright,         0, 0,
                sr * bright,         (sg + sat) * bright, sb * bright,         0, 0,
                sr * bright,         sg * bright,         (sb + sat) * bright, 0, 0,
                0, 0, 0, 1, 0,
            };
            using var surface = SKSurface.Create(new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(m) };
            surface.Canvas.DrawBitmap(src, 0, 0, paint);
            surface.Canvas.Flush();
            using var img = surface.Snapshot();
            using var outPng = img.Encode(SKEncodedImageFormat.Png, 95);
            return outPng.ToArray();
        }
        catch { return png; }
    }

    private static async Task<(byte[]? Bytes, string? Error)> ReadBounded(IFormFile? file, long maxBytes)
    {
        if (file is null || file.Length == 0) return (null, "No file uploaded.");
        if (file.Length > maxBytes) return (null, $"File is {file.Length:N0} bytes; limit is {maxBytes:N0}.");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        if (bytes.Length > maxBytes) return (null, "File exceeds the size limit.");
        return (bytes, null);
    }

    private byte[]? SafeExtract(string mpqPath)
    {
        try { return _mpq.ExtractFile(mpqPath); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: extract failed for {Path}", mpqPath);
            return null;
        }
    }
}
