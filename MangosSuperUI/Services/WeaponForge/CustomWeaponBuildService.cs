using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MangosSuperUI.Models;

namespace MangosSuperUI.Services.WeaponForge;

/// <summary>
/// The one packaging path for EVERY custom weapon source — donor clone (Phase 1), parametric
/// sword (Route A), imported GLB / reconstructed sketch (Route B). Compiles the input to M2+BLP,
/// persists the compiled bytes as the durable source of truth in the custom_weapon_* tables, and
/// then rebuilds the single unified <c>patch-5.MPQ</c> containing ALL custom weapons recorded in
/// the database — mirroring how the spell pipeline rebuilds one patch-3.MPQ from DB state. A new
/// build therefore never orphans previously forged weapons: their members and DBC rows are
/// re-packaged from the stored bytes every time.
///
/// patch-5 (not 4): the Retexture Engine owns <c>patch-4.MPQ</c>. The Forge's patch sits ABOVE it,
/// and its DBC is built on the effective state read BENEATH patch-5 (see ResolveBaseDbc) — so it
/// always unions the retexture rows in patch-4 without ever feeding its own output back as input.
/// The retexture pipeline triggers <see cref="RebuildPatchAsync"/> after each of its own rebuilds
/// so patch-5's DBC never goes stale above a newer patch-4.
///
/// Forging APPLIES, like the app's other tools (NPC dev commits SQL, the Retexture Engine deploys
/// its patch): the item row is inserted into the world DB (fail-closed INSERT), the core is told
/// <c>.reload item_template</c> over RA, and patch-5.MPQ is deployed into the client Data folder.
/// Deleting a weapon reverses all of it. Every apply step is best-effort and individually reported —
/// a down DB/RA/client never fails the build, it just leaves that step for the owner (the files are
/// always also written to the artifact root). The one step that stays manual is restarting the
/// client, which nothing can automate away.
/// </summary>
public sealed class CustomWeaponBuildService : ICustomMpqMemberSource
{
    public const string PatchFileName = "patch-5.MPQ";
    private const string LegacyPatchFileName = "patch-4.MPQ"; // pre-rename Forge output; cleaned up on write

    private const string DonorBlpPath = @"ITEM\ObjectComponents\WEAPON\Sword_1H_Short_A_01Blue.blp";
    private const uint DonorDisplayRow = 679;

    private readonly MpqReaderService _mpq;
    private readonly WeaponIdReservationService _ids;
    private readonly WeaponPatchBuilder _patch;
    private readonly WeaponAssetCompiler _compiler;
    private readonly WeaponPreviewService _preview;
    private readonly WeaponDonorResolver _donors;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;
    private readonly RaService _ra;
    private readonly ConnectionFactory _db;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<CustomWeaponBuildService> _logger;
    private readonly IServiceProvider _services;

    public CustomWeaponBuildService(MpqReaderService mpq, WeaponIdReservationService ids,
        WeaponPatchBuilder patch, WeaponAssetCompiler compiler, WeaponPreviewService preview,
        WeaponDonorResolver donors, DbcService dbc, AuditService audit, RaService ra,
        ConnectionFactory db, IWebHostEnvironment env,
        IConfiguration config, ILogger<CustomWeaponBuildService> logger,
        IServiceProvider services)
    {
        _mpq = mpq; _ids = ids; _patch = patch; _compiler = compiler; _preview = preview;
        _mpq.RegisterCustomMemberSource(this);
        _donors = donors; _dbc = dbc; _audit = audit; _ra = ra; _db = db; _env = env;
        _config = config; _logger = logger; _services = services;
    }

    /// <summary>Rebuild the Armor Forge's patch-6 after patch-5 changes. patch-6 sits ABOVE patch-5
    /// and carries its own full ItemDisplayInfo.dbc, so a fresh weapon row would be shadowed by a
    /// stale patch-6 until it repackages. Resolved lazily to avoid a construction-order coupling; a
    /// no-op when no armor exists. Best-effort — never fails the weapon flow.</summary>
    /// <summary>Deploy through the UNIFIED patch. Every lane that writes ItemDisplayInfo.dbc now
    /// ships in one archive, so this lane no longer places a file of its own: it asks the unified
    /// service to repackage all lanes, deploy that, and retire the superseded per-lane archives.
    /// This is what removed the old cascade — there is no patch above us left to re-union.
    ///
    /// Scoped resolve, not injection: UnifiedPatchService depends on the scoped ItemRetextureService
    /// and this service is a singleton, so it must open a scope. Best-effort like the deploy it
    /// replaces — a packaging failure is reported, never thrown into the forge flow.</summary>
    private async Task<(bool Ok, string Message)> DeployUnifiedAsync(string reason)
    {
        try
        {
            using var scope = _services.CreateScope();
            if (scope.ServiceProvider.GetService(typeof(UnifiedPatch.UnifiedPatchService))
                is not UnifiedPatch.UnifiedPatchService unified)
                return (false, "unified patch service unavailable — nothing deployed");
            var summary = await unified.RebuildAsync(reason);
            return (summary.Ok, summary.DeployMessage ?? summary.Message);
        }
        catch (Exception ex)
        {
            return (false, $"unified patch rebuild failed ({ex.Message}) — nothing deployed");
        }
    }

    /// <summary>Record a registry change WITHOUT rebuilding or deploying the unified patch. Forging
    /// used to repack and deploy on every single weapon; now the operator forges a batch and ships it
    /// with one Rebuild patch click. Returns a result shaped like a deploy so the apply rows can show
    /// it, with <c>Queued</c> so the UI renders "pending" rather than a failed deploy.</summary>
    private (bool Ok, bool Queued, int Pending, string Message) QueueUnifiedRebuild(string reason)
    {
        try
        {
            using var scope = _services.CreateScope();
            if (scope.ServiceProvider.GetService(typeof(UnifiedPatch.UnifiedPatchService))
                is not UnifiedPatch.UnifiedPatchService unified)
                return (false, false, 0, "unified patch service unavailable — nothing queued");
            int pending = unified.QueueChange("weapon", reason);
            return (true, true, pending,
                $"rebuild queued — {pending} change(s) pending; close WoW and click Rebuild patch when you are done forging");
        }
        catch (Exception ex)
        {
            return (false, false, 0, $"could not queue the patch rebuild ({ex.Message}) — click Rebuild patch yourself");
        }
    }

    private async Task RebuildArmorPatchAsync(string reason)
    {
        try
        {
            var armor = _services.GetService(typeof(MangosSuperUI.Services.ArmorForge.CustomArmorBuildService))
                as MangosSuperUI.Services.ArmorForge.CustomArmorBuildService;
            if (armor is null) return;
            await armor.RebuildPatchAsync(reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: armor patch-6 re-union after '{Reason}' failed — rebuild it from the Armor Forge page", reason);
        }
    }

    /// <summary>The client Data directory the app deploys patches into — same config the Retexture
    /// Engine uses for patch-4. Null when not configured/present (deploy steps report and skip).</summary>
    private string? ClientDataPath
    {
        get
        {
            var p = _config["Vmangos:ClientDataPath"] ?? _config["SpellCreator:ClientDataPath"];
            return !string.IsNullOrEmpty(p) && Directory.Exists(p) ? p : null;
        }
    }

    public string ArtifactRoot =>
        _config["WeaponForge:ArtifactRoot"] is { Length: > 0 } cfg
            ? cfg
            : Path.Combine(_env.WebRootPath, "weapon_forge_builds");

    private static bool PassUsesTextureSlot(WeaponPass pass, int textureSlot) =>
        pass.TextureBindings is { Count: > 0 } bindings
            ? bindings.Any(binding => binding.TextureSlot == textureSlot)
            : pass.TextureSlot == textureSlot;

    private static bool PassNeedsTextureAlpha(WeaponPass pass) => pass.BlendMode is 1 or 2 or 4;

    // ═══════════════════════════════════════════════════════════════════
    // BUILD (one new weapon → persist → unified patch)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// What a forge attempt had done to the world by the time it threw. A build reserves ids,
    /// writes registry rows and inserts a world-DB row before anything can still fail, so a throw
    /// leaves real state behind; without this the audit log's only record of the attempt would be
    /// its absence. Filled in as the build passes each boundary and read by the failure entry.
    /// </summary>
    private sealed class ForgeAttemptTrace
    {
        public string? BuildId;
        public long ItemEntry;
        public long DisplayId;
        public string? Name;
        public string Stage = "start";
    }

    /// <summary>Later-client fidelity imports already carry authored material timing.
    /// Adding a generic pulse changes a steady source glow into an invented bright/dim loop.</summary>
    internal static bool ShouldInventGlowPulse(string sourceKind)
        => sourceKind is not ("tbc_import" or "wotlk_import");

    public async Task<CustomWeaponBuildResult> BuildAsync(CustomWeaponBuildRequest request)
    {
        var trace = new ForgeAttemptTrace();
        try
        {
            return await BuildCoreAsync(request, trace);
        }
        catch (Exception ex)
        {
            // The forge's success row is the last statement of the happy path, so a throw used to
            // produce no row at all — reserved ids, persisted registry rows and a possibly-inserted
            // world row with nothing in the log to find them by. Same entry shape, Success=false.
            await _audit.LogAsync(new AuditEntry
            {
                Category = "weaponforge",
                Action = "forge_" + request.SourceKind,
                TargetType = "item",
                TargetName = trace.Name,
                TargetId = trace.ItemEntry is > 0 and <= int.MaxValue ? (int)trace.ItemEntry : null,
                StateAfter = JsonSerializer.Serialize(new
                {
                    buildId = trace.BuildId,
                    failedAtStage = trace.Stage,
                    itemEntry = trace.ItemEntry,
                    displayId = trace.DisplayId,
                    weaponType = request.WeaponTypeKey,
                }),
                IsReversible = false,
                RevertKind = RevertKind.None,
                Success = false,
                Notes = $"Forge FAILED at stage '{trace.Stage}': {ex.Message}" +
                        (trace.ItemEntry > 0
                            ? $" Ids {trace.ItemEntry}/{trace.DisplayId} were reserved and may be orphaned — check the Forged Weapons list and the id registry."
                            : " No ids were reserved."),
            });
            throw;
        }
    }

    private async Task<CustomWeaponBuildResult> BuildCoreAsync(CustomWeaponBuildRequest request, ForgeAttemptTrace trace)
    {
        bool hasMesh = request.Mesh is not null;
        bool hasPrecompiled = request.PrecompiledM2 is { Length: > 0 };
        if (hasMesh == hasPrecompiled)
            throw new ArgumentException("Provide exactly one of Mesh or PrecompiledM2.", nameof(request));
        if (hasMesh && request.PrecompiledEffectTextures is { Count: > 0 })
            throw new ArgumentException("PrecompiledEffectTextures can only accompany PrecompiledM2.", nameof(request));

        if (!DonorItemTemplateFixture.Verify())
            throw new InvalidOperationException("Donor item_template fixture failed hash verification.");

        // Weapon family: gameplay contract from the catalog, visual donor (scaffold, sound, icon,
        // grip envelope) resolved from the installed stock assets. Fails closed when the family has
        // no usable stock donor.
        var profile = WeaponTypeCatalog.Get(request.WeaponTypeKey);
        var donor = _donors.Resolve(profile);
        var displayFields = ResolveDisplayFields(request, donor.GroupSoundIndex,
            donor.SpellVisualId, donor.MirrorModelName2);
        uint displayGroupSound = displayFields.GroupSoundIndex;
        uint displaySpellVisual = displayFields.SpellVisualId;
        bool displayMirrorModelName2 = displayFields.MirrorModelName2;

        // 1) Base DBC (beneath patch-5) — id floor.
        byte[] baseDbc = ResolveBaseDbc();
        var baseReader = DbcWriterService.ReadDbc(baseDbc, WeaponNaming.ItemDisplayInfoMember);
        uint dbcMax = baseReader.GetMaxId();

        // 2) Reserve ids atomically. buildId is the stable reservation slot key so a retry is idempotent.
        string buildId = (request.SourceKind == "donor_patch" ? "gold-" : "wpn-") + Guid.NewGuid().ToString("N")[..12];
        long entryFloor = await _ids.ComputeItemEntryFloorAsync();
        long displayFloor = await _ids.ComputeDisplayIdFloorAsync(dbcMax);
        trace.BuildId = buildId;
        trace.Stage = "reserve";
        var entryRes = await _ids.ReserveAsync(WeaponIdReservationService.KindItemEntry, entryFloor, buildId, "item");
        var dispRes = await _ids.ReserveAsync(WeaponIdReservationService.KindItemDisplay, displayFloor, buildId, "display");
        trace.ItemEntry = entryRes.Id;
        trace.DisplayId = dispRes.Id;

        int modelIndex = checked((int)dispRes.Id); // 1 model ↔ 1 display; ties SUI_W names to the display id
        string weaponName = string.IsNullOrWhiteSpace(request.Name)
            ? $"Forged {profile.DefaultNoun} {dispRes.Id}"
            : request.Name.Trim();
        trace.Name = weaponName;
        trace.Stage = "compile";

        // 3) Compile (or accept precompiled donor bytes). BLP falls back to the donor texture so a
        //    textureless mesh still ships something valid to sample.
        var diag = new ForgeDiagnostics("build");
        byte[] m2;
        byte[] blp;
        var effectBlps = new List<(string MpqPath, byte[] Blp)>();
        if (hasMesh)
        {
            // A pre-encoded source BLP2 (the TBC fidelity route) is packaged byte-for-byte. PNG
            // authoring routes retain their existing power-of-two envelope and encoder policy.
            WeaponTexture? texture = null;
            if (request.TextureBlp is { Length: > 0 } sourceBlp)
            {
                texture = new WeaponTexture { SourceBlp = sourceBlp };
            }
            else if (request.TexturePng is { Length: > 0 })
            {
                var (tw, th) = TargetBlpSize(request.TexturePng);
                // Alpha-keyed materials and TBC pass modes that consume texture alpha need DXT3.
                // TextureBindings handles multi-texture source batches; TextureSlot remains the
                // compatibility fallback for ordinary one-texture passes.
                bool keepAlpha = request.Mesh?.Material.BlendMode == WeaponBlendMode.AlphaKey ||
                    request.Mesh?.Passes?.Any(p => PassUsesTextureSlot(p, 0) && PassNeedsTextureAlpha(p)) == true;
                texture = new WeaponTexture { SourcePng = request.TexturePng, Width = tw, Height = th, UseDxt1 = !keepAlpha };
            }

            // Effect textures (multi-pass glow): packaged as Type-0 members the emitted M2 names
            // by hardcoded path. Alpha survives (DXT3) when any pass using the slot consumes it;
            // pure additive blend mode 3 works on RGB and stays DXT1.
            List<WeaponTexture>? effectTextures = null;
            List<string>? effectPaths = null;
            int encodedEffectCount = request.EffectTexturesBlp?.Count ?? 0;
            int pngEffectCount = request.EffectTexturesPng?.Count ?? 0;
            int effectCount = Math.Max(encodedEffectCount, pngEffectCount);
            if (effectCount > 0 && request.Mesh?.Passes is { Count: > 0 } meshPasses)
            {
                effectTextures = new List<WeaponTexture>();
                effectPaths = new List<string>();
                for (int i = 0; i < effectCount; i++)
                {
                    if (i < encodedEffectCount && request.EffectTexturesBlp![i] is { Length: > 0 } effectBlp)
                    {
                        effectTextures.Add(new WeaponTexture { SourceBlp = effectBlp });
                        effectPaths.Add(WeaponNaming.EffectTextureMpqPath(modelIndex, i + 1, profile.ComponentDir));
                        continue;
                    }

                    if (i >= pngEffectCount)
                        throw new InvalidOperationException($"Effect texture slot {i + 1} has no BLP2 or PNG source.");
                    var png = request.EffectTexturesPng![i];
                    if (png is not { Length: > 0 })
                        throw new InvalidOperationException($"Effect texture slot {i + 1} has no image bytes.");
                    var (ew, eh) = TargetBlpSize(png);
                    bool alpha = meshPasses.Any(p =>
                        PassUsesTextureSlot(p, i + 1) && PassNeedsTextureAlpha(p));
                    effectTextures.Add(new WeaponTexture { SourcePng = png, Width = ew, Height = eh, UseDxt1 = !alpha });
                    effectPaths.Add(WeaponNaming.EffectTextureMpqPath(modelIndex, i + 1, profile.ComponentDir));
                }
            }

            var compiled = _compiler.Compile(request.Mesh!, texture, new WeaponCompileOptions
            {
                ModelIndex = modelIndex,
                CanonicalInternalName = !request.KeepDonorInternalName,
                DonorM2Path = donor.M2Path,
                EffectTextures = effectTextures,
                EffectTexturePaths = effectPaths,
                MeshValidation = new MeshValidationOptions
                {
                    Topology = request.Topology,
                    VariableHardCeiling = request.VariableTriangleHardCeiling,
                },
            });
            diag.AddRange(compiled.Diagnostics);
            if (compiled.M2 is null || compiled.Diagnostics.HasErrors)
                throw new InvalidOperationException("Mesh compilation failed: " + string.Join("; ",
                    compiled.Diagnostics.Items.Where(i => i.Severity == ForgeSeverity.Error).Select(i => i.Message)));
            m2 = compiled.M2;
            // Later-client imports carry their own enchant/glow attachment points (0..4 along the
            // blade); move the donor's onto them so an ItemVisual (or a real enchant) sits on the
            // imported geometry, not where the donor's blade used to be.
            if (request.AttachmentPointsWoW is { Count: > 0 } attachments)
            {
                m2 = RawM2.M2GeometryPatcher.RewriteAttachmentPositions(m2, attachments);
                diag.Info("attachments.carried", $"{attachments.Count} attachment point(s) moved onto the imported geometry.");
            }
            // Motion: a source effect that moved is rebuilt from a stock 1.12 emitter rather than
            // photographed into a static sprite. Never fatal — a failed graft leaves the model as-is.
            if (request.MotionGrafts is { Count: > 0 } grafts)
            {
                try
                {
                    var motion = RawM2.M2EmitterTransplanter.Apply(m2, grafts);
                    foreach (var note in motion.Notes) diag.Info("motion.emitter", note);
                    if (motion.Grafted > 0)
                    {
                        m2 = motion.M2;
                        diag.Info("motion.invented",
                            $"{motion.Grafted} animated particle emitter(s) rebuilt from stock 1.12 donors — an invented conversion: " +
                            "1.12 cannot host the source emitter graph, so its position, colour and size were rebuilt on Blizzard's own emission behaviour.");
                    }
                }
                catch (Exception ex) { diag.Warn("motion.failed", $"Emitter graft skipped: {ex.Message}"); }
            }
            // Generated art can opt into the Forge's generic additive "breath". Later-client
            // fidelity imports must keep their authored timing: the TBC Warglaive's glow is
            // constant while its second texture unit scrolls the lines up the blade.
            if (ShouldInventGlowPulse(request.SourceKind))
            {
                try
                {
                    var parsedForPulse = M2Reader.Parse(m2);
                    if (parsedForPulse is not null)
                    {
                        var glowColors = RawM2.M2GlowPulseWriter.AdditiveColorIndices(parsedForPulse);
                        if (glowColors.Count > 0)
                        {
                            var pulse = RawM2.M2GlowPulseWriter.Apply(m2, glowColors);
                            if (pulse.Pulsed > 0)
                            {
                                m2 = pulse.M2;
                                foreach (var note in pulse.Notes) diag.Info("motion.pulse", note);
                            }
                        }
                    }
                }
                catch (Exception pex) { diag.Warn("motion.pulse.failed", $"Glow pulse skipped: {pex.Message}"); }
            }
            else
            {
                diag.Info("motion.pulse.preserved",
                    "Preserved the later-client material timing; no synthetic additive glow pulse was added.");
            }
            blp = compiled.Blp ?? ExtractDonorBlp(donor);
            for (int i = 0; i < compiled.EffectBlps.Count && effectPaths is not null; i++)
                effectBlps.Add((effectPaths[i], compiled.EffectBlps[i]));
        }
        else
        {
            m2 = request.PrecompiledM2!;
            blp = request.PrecompiledBlp ?? ExtractDonorBlp(donor);

            // A source-preserved Vanilla clone may recolor one or more native material-effect
            // sheets (Thunderfury lightning, the Warglaive environment shell, ...). Stock Type-0
            // filenames are shared by many models, so never package a replacement at the stock
            // path. Give every changed slot a build-private member and alter only that texture
            // record's filename pointer; the original animation/render graph remains untouched.
            if (request.PrecompiledEffectTextures is { Count: > 0 } nativeEffects)
            {
                var ordered = nativeEffects
                    .OrderBy(e => e.TextureSlots is { Count: > 0 } ? e.TextureSlots.Min() : int.MaxValue)
                    .ToList();
                if (ordered.Any(e => e.TextureSlots is not { Count: > 0 }))
                    throw new InvalidOperationException("A precompiled effect texture has no M2 texture slots.");
                int[] allSlots = ordered.SelectMany(e => e.TextureSlots).ToArray();
                if (allSlots.Any(slot => slot < 0))
                    throw new InvalidOperationException("A precompiled effect texture has a negative M2 texture slot.");
                if (allSlots.GroupBy(slot => slot).Any(g => g.Count() != 1))
                    throw new InvalidOperationException("A precompiled effect texture slot was supplied more than once.");

                var replacements = new Dictionary<int, string>();
                var expectedSources = new Dictionary<int, string>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var effect = ordered[i];
                    if (effect.Blp is not { Length: > 0 })
                        throw new InvalidOperationException(
                            $"Precompiled effect texture slot(s) {string.Join(", ", effect.TextureSlots)} have no BLP bytes.");

                    string privatePath = WeaponNaming.EffectTextureMpqPath(
                        modelIndex, i + 1, profile.ComponentDir);
                    foreach (int textureSlot in effect.TextureSlots)
                    {
                        replacements.Add(textureSlot, privatePath);
                        expectedSources.Add(textureSlot, effect.SourcePath);
                    }
                    effectBlps.Add((privatePath, effect.Blp));
                    diag.Info("vanilla.effect.private",
                        $"Type-0 texture slot(s) {string.Join(", ", effect.TextureSlots)} ('{effect.SourcePath}') " +
                        $"were copied to '{privatePath}' for this weapon only.");
                }

                m2 = RawM2.M2GeometryPatcher.RewriteHardcodedTexturePaths(
                    m2, replacements, expectedSources);
            }
        }

        // 4) Persist FIRST — the stored compiled bytes are what every future unified rebuild
        //    re-packages, so a weapon that isn't durably recorded must not be handed out.
        //    The gameplay row clones donor 2131 with the family's subclass/inventory/sheath/
        //    material/delay overrides, so a forged axe IS an axe to the core. Request-level
        //    overrides (e.g. the TBC source item's own sheath/slot/delay) layer on top.
        var overrides = profile.ItemTemplateOverrides();
        if (request.ItemOverrides is not null)
            foreach (var (col, val) in request.ItemOverrides)
                overrides[col] = val;
        int effectiveInventoryType = ResolveEffectiveInventoryType(profile, overrides);
        var sql = WeaponItemTemplateSql.Build(entryRes.Id, weaponName, dispRes.Id, buildId, overrides);
        // The source item's own icon wins; the donor's is the fallback for GLB imports (which have
        // no icon of their own) and for sources whose display row names no icon.
        string iconStem = !string.IsNullOrWhiteSpace(request.IconStem) ? request.IconStem!
                        : donor.IconStem.Length > 0 ? donor.IconStem
                        : ReadDonorIconStem(baseReader);

        // An icon the vanilla client does not have must ship with the patch, or the name in the DBC
        // row resolves to nothing and the item shows the red "?". Rides custom_weapon_model_texture,
        // which LoadEffectTextureMembersAsync packages by mpq_path alone — no schema change.
        // Capture to a local: the compiler does not carry a property's null-narrowing across
        // statements, and the tuple element is non-nullable.
        if (request.IconBlp is { Length: > 0 } iconBytes && !string.IsNullOrWhiteSpace(iconStem))
        {
            string iconMember = $@"Interface\Icons\{iconStem}.blp";
            if (!effectBlps.Any(e => string.Equals(e.MpqPath, iconMember, StringComparison.OrdinalIgnoreCase)))
                effectBlps.Add((iconMember, iconBytes));
            diag.Info("icon.packaged",
                $"Packaging the source icon '{iconStem}' ({iconBytes.Length:N0} bytes) — the vanilla client has no icon by that name.");
        }
        trace.Stage = "persist";
        await PersistRecordsAsync(request, profile, donor, buildId, modelIndex, entryRes.Id, dispRes.Id,
            weaponName, effectiveInventoryType, m2, blp, effectBlps, sql,
            displayGroupSound, displaySpellVisual, displayMirrorModelName2, iconStem);
        await _ids.MarkStateAsync(WeaponIdReservationService.KindItemEntry, entryRes.Id, "committed");
        await _ids.MarkStateAsync(WeaponIdReservationService.KindItemDisplay, dispRes.Id, "committed");

        // Inject the new display into DbcService's in-memory caches so the web UI (Items page icon
        // lookup, model/texture panel, GLB preview) resolves it immediately — the same registration
        // the Retexture Engine does for its custom displays. Without this the forged weapon's display
        // id is absent from the statically-loaded server DBC and renders as the red "?" with no
        // texture. Cloned from the family's donor row, then overridden with the weapon's own SUI_W names.
        RegisterDisplayWithDbc(dispRes.Id, modelIndex, donor.DisplayRow, displayMirrorModelName2,
            itemVisual: request.ItemVisual);
        // The donor-clone above copies the DONOR's icon. Override with the one actually written into
        // the patch row, or the Items page and the client disagree about how the item looks.
        if (!string.IsNullOrWhiteSpace(iconStem)) _dbc.RegisterCustomDisplayIcon((uint)dispRes.Id, iconStem);

        // 5) Unified rebuild: every custom weapon with stored bytes, this one included.
        trace.Stage = "assemble";
        var assembly = await AssembleUnifiedPatchAsync(diag, buildId);
        if (assembly.Patch is null)
            throw new InvalidOperationException("Unified patch assembly produced no weapons — the just-persisted weapon should have been included.");

        // 6) Preview the freshly packaged bytes (effect textures bound by their hardcoded paths).
        // The chosen enchant glow (ItemVisual) is resolved against the built M2's own attachment
        // points so the post-forge preview shows it exactly like the registry View does.
        var preview = _preview.RenderFromBytes(m2, blp,
            ResolvePreviewTextureBlps(m2, effectBlps),
            ResolvePreviewVisualEffects(request.ItemVisual, m2),
            preserveSourceGraph: string.Equals(request.SourceKind, "vanilla_recolor",
                StringComparison.OrdinalIgnoreCase));

        // 7) Write the build directory: straight patch-5.MPQ + item_template.sql (+ reports). No ZIP.
        var manifest = BuildManifest(request, profile, donor, buildId, entryRes.Id, dispRes.Id, modelIndex, weaponName,
            effectiveInventoryType, displayGroupSound, displaySpellVisual, displayMirrorModelName2,
            sql, assembly.Patch, assembly.PackagedCount,
            assembly.SkippedCount, assembly.ReplacedInBase);
        string buildDir = WriteOutputs(buildId, entryRes.Id, dispRes.Id, modelIndex, assembly.Patch, sql, manifest, diag, profile.ComponentDir);

        // 8) Apply — like the app's other tools: world DB row, core reload, client patch deploy.
        //    Each step is best-effort and individually reported; failures leave that step manual.
        trace.Stage = "apply";
        var sqlApply = await ApplyItemSqlAsync(sql, entryRes.Id);
        var reload = sqlApply.Ok ? await ReloadItemTemplateAsync() : (Ok: false, Message: "skipped — SQL was not applied");
        // The client patch is NOT rebuilt here any more — the change is queued and shipped with the
        // next Rebuild patch click, so several weapons can be forged before one repack + deploy.
        var deploy = QueueUnifiedRebuild($"forged '{weaponName}' (entry {entryRes.Id})");
        var apply = new ServerApplyStatus
        {
            SqlApplied = sqlApply.Ok, SqlMessage = sqlApply.Message,
            Reloaded = reload.Ok, ReloadMessage = reload.Message,
            PatchDeployed = false, PatchDeployMessage = deploy.Message,
            PatchQueued = deploy.Queued, PatchPending = deploy.Pending,
        };

        _logger.LogInformation(
            "WeaponForge: build {Build} ({Kind}, {Type}) → entry {Entry}, display {Display}; {Patch} packages {Count} weapon(s); " +
            "sql={Sql} reload={Reload} deploy={Deploy}",
            buildId, request.SourceKind, profile.Key, entryRes.Id, dispRes.Id, PatchFileName, assembly.PackagedCount,
            sqlApply.Ok, reload.Ok, deploy.Ok);

        // Audit trail (Activity Log / Change Graph) — records the build AND what was applied live.
        await _audit.LogAsync(new AuditEntry
        {
            Category = "weaponforge",
            Action = "forge_" + request.SourceKind,
            TargetType = "item",
            TargetName = weaponName,
            TargetId = checked((int)entryRes.Id),
            RaCommand = sqlApply.Ok ? ".reload item_template" : null,
            RaResponse = sqlApply.Ok ? reload.Message : null,
            StateAfter = JsonSerializer.Serialize(new
            {
                buildId,
                itemEntry = entryRes.Id,
                displayId = dispRes.Id,
                inventoryType = effectiveInventoryType,
                inventoryTypeLabel = InventoryTypeLabel(effectiveInventoryType),
                model = WeaponNaming.ModelMpqPath(modelIndex, profile.ComponentDir),
                mpqSha256 = assembly.Patch.MpqSha256,
                weaponsInPatch = assembly.PackagedCount,
                applied = apply,
            }),
            IsReversible = true,
            RevertKind = RevertKind.Registry,
            // The world-DB insert is what makes the weapon exist to the core. Registry rows and the
            // patch are durable either way (and the Forged Weapons list can still delete it), but a
            // forge whose row never landed is a half-applied change and the log should say so
            // rather than report a flat success the operator has to read the Notes to disbelieve.
            Success = sqlApply.Ok,
            Notes = $"{PatchFileName} rebuilt with {assembly.PackagedCount} weapon(s). " +
                    $"SQL: {sqlApply.Message}. Reload: {reload.Message}. Client patch: {deploy.Message}. " +
                    "Undo via the Forged Weapons list on the Item Assets page.",
        });

        return new CustomWeaponBuildResult
        {
            BuildId = buildId,
            ItemEntry = entryRes.Id,
            DisplayId = dispRes.Id,
            ModelIndex = modelIndex,
            Name = weaponName,
            WeaponType = profile.Key,
            WeaponTypeLabel = profile.Label,
            InventoryType = effectiveInventoryType,
            InventoryTypeLabel = InventoryTypeLabel(effectiveInventoryType),
            SourceKind = request.SourceKind,
            ModelMember = WeaponNaming.ModelMpqPath(modelIndex, profile.ComponentDir),
            TextureMember = WeaponNaming.TextureMpqPath(modelIndex, 1, profile.ComponentDir),
            MpqSha256 = assembly.Patch.MpqSha256,
            DbcSha256 = assembly.Patch.DbcSha256,
            SqlSha256 = sql.Sha256,
            AllMembersVerified = assembly.Patch.AllVerified,
            PackagedWeaponCount = assembly.PackagedCount,
            SkippedWeaponCount = assembly.SkippedCount,
            PreviewGlbWebPath = preview.Ok ? preview.GlbWebPath : null,
            TriangleCount = preview.TriangleCount,
            VertexCount = preview.VertexCount,
            BuildDirectory = buildDir,
            BuildDirName = Path.GetFileName(buildDir),
            Apply = apply,
            Diagnostics = diag.Items.Select(i => i.ToString()).ToArray(),
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // REBUILD ONLY (no new weapon — repackage current DB state)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rebuild the canonical patch-5.MPQ from the weapons currently in the database, without
    /// reserving ids or adding anything. Called after a weapon is deleted, on demand from the UI,
    /// and by the Retexture Engine after its own patch-4 rebuild (so patch-5's DBC re-unions the
    /// fresh retexture rows instead of shadowing them). With zero weapons the canonical patch is
    /// removed — an empty overlay would serve no purpose and still shadow patch-4's DBC.
    /// </summary>
    /// <param name="reason">Free text for the log and audit row.</param>
    /// <param name="deploy">True (the Rebuild patch button, the retexture cascade) repacks the unified
    /// patch and deploys it. False (deletes) repacks this lane's own artifact only and QUEUES the
    /// unified rebuild for the next Rebuild patch click.</param>
    public async Task<WeaponPatchRebuildSummary> RebuildPatchAsync(string reason, bool deploy = true)
    {
        try
        {
            var diag = new ForgeDiagnostics("rebuild");
            var assembly = await AssembleUnifiedPatchAsync(diag, "rebuild-" + Guid.NewGuid().ToString("N")[..8]);

            if (assembly.Patch is null)
            {
                RemoveCanonicalPatches();
                var removal = RemoveDeployedPatch();
                _logger.LogInformation("WeaponForge: rebuild ({Reason}) — no weapons in DB; {Patch} removed ({Client})",
                    reason, PatchFileName, removal.Message);

                // This branch DELETES the patch out of the live client's Data folder — the most
                // destructive thing the Forge does that isn't called "delete", reached from a plain
                // Rebuild click, from the last weapon being removed, and from the retexture engine's
                // own rebuilds. It gets its own row rather than hiding inside whatever triggered it,
                // but only when a file actually moved: with no weapons forged this branch runs on
                // every retexture rebuild, and a row per no-op would bury the real removals.
                if (removal.Changed)
                    await LogPatchAuditAsync("patch_remove", reason, weaponCount: 0, mpqSha256: null,
                        ok: removal.Ok, message: removal.Message,
                        notes: $"No weapons left in the registry, so {PatchFileName} was removed from the artifact root and " +
                               $"from the client Data folder ({removal.Message}). Triggered by: {reason}. " +
                               "The unified patch rebuild runs next and will restore the server's stock ItemSet.dbc if no armor remains either.");

                bool emptiedQueued = false; int emptiedPending = 0; string emptiedMessage;
                if (deploy)
                    emptiedMessage = (await DeployUnifiedAsync("weapon registry emptied: " + reason)).Message;
                else
                {
                    var q = QueueUnifiedRebuild("weapon registry emptied: " + reason);
                    emptiedQueued = q.Queued; emptiedPending = q.Pending; emptiedMessage = q.Message;
                }
                var emptied = (Queued: emptiedQueued, Pending: emptiedPending, Message: emptiedMessage);
                return new WeaponPatchRebuildSummary
                {
                    WeaponCount = 0,
                    PatchRemoved = true,
                    MpqSha256 = null,
                    PatchDeployed = false,
                    PatchDeployMessage = deploy ? removal.Message : emptied.Message,
                    PatchQueued = emptied.Queued,
                    PatchPending = emptied.Pending,
                    Diagnostics = diag.Items.Select(i => i.ToString()).ToArray(),
                };
            }

            WriteCanonicalPatch(assembly.Patch.MpqBytes);
            var queued = deploy
                ? (Ok: false, Queued: false, Pending: 0, Message: "")
                : QueueUnifiedRebuild("weapon patch rebuild: " + reason);
            var deployed = deploy
                ? await DeployUnifiedAsync("weapon patch rebuild: " + reason)
                : (Ok: queued.Ok, Message: queued.Message);
            _logger.LogInformation("WeaponForge: rebuild ({Reason}) — {Patch} repackaged with {Count} weapon(s); deploy={Deploy}",
                reason, PatchFileName, assembly.PackagedCount, deploy ? deployed.Ok : "queued");

            await LogPatchAuditAsync("patch_rebuild", reason, assembly.PackagedCount, assembly.Patch.MpqSha256,
                ok: deployed.Ok, message: deployed.Message,
                notes: $"{PatchFileName} repackaged with {assembly.PackagedCount} weapon(s)" +
                       (assembly.SkippedCount > 0 ? $" ({assembly.SkippedCount} skipped — no compiled bytes)" : "") +
                       $". {(deploy ? "Deploy" : "Client patch")}: {deployed.Message}. Triggered by: {reason}.");

            return new WeaponPatchRebuildSummary
            {
                WeaponCount = assembly.PackagedCount,
                PatchRemoved = false,
                MpqSha256 = assembly.Patch.MpqSha256,
                PatchDeployed = deploy && deployed.Ok,
                PatchDeployMessage = deployed.Message,
                PatchQueued = queued.Queued,
                PatchPending = queued.Pending,
                Diagnostics = diag.Items.Select(i => i.ToString()).ToArray(),
            };
        }
        catch (Exception ex)
        {
            await LogPatchAuditAsync("patch_rebuild", reason, weaponCount: 0, mpqSha256: null,
                ok: false, message: ex.Message,
                notes: $"{PatchFileName} rebuild FAILED: {ex.Message}. Triggered by: {reason}. " +
                       "The deployed patch is whatever the previous build left there.");
            throw;
        }
    }

    /// <summary>One row per patch write/removal. The patch is not a build artifact — it is a file in
    /// the running client's Data folder, and the armor re-union it cascades into rewrites a DBC in
    /// the running server's dbc folder — so every one of them belongs in the trail, whether it came
    /// from a forge, a delete, a Rebuild click or the retexture engine.</summary>
    private Task LogPatchAuditAsync(string action, string reason, int weaponCount, string? mpqSha256,
        bool ok, string message, string notes) =>
        _audit.LogAsync(new AuditEntry
        {
            Category = "weaponforge",
            Action = action,
            TargetType = "patch",
            TargetName = PatchFileName,
            StateAfter = JsonSerializer.Serialize(new
            {
                reason,
                patch = PatchFileName,
                weaponCount,
                mpqSha256,
                clientDataPath = ClientDataPath,
                deployMessage = message,
            }),
            IsReversible = false,
            RevertKind = RevertKind.None,
            Success = ok,
            Notes = notes,
        });

    // ═══════════════════════════════════════════════════════════════════
    // LIST / DELETE (the Forge's inventory + undo)
    // ═══════════════════════════════════════════════════════════════════

    public async Task<List<ForgedWeaponInfo>> ListWeaponsAsync()
    {
        await using var conn = _db.Admin();
        await conn.OpenAsync();
        var rows = await conn.QueryAsync(
            @"SELECT d.display_id       AS DisplayId,
                     d.created_at       AS CreatedAt,
                     d.donor_display_id AS DonorDisplayRow,
                     d.item_visual      AS ItemVisual,
                     mo.source_kind     AS SourceKind,
                     mo.model_mpq_path  AS ModelMpqPath,
                     ma.item_entry      AS ItemEntry,
                     ma.build_id        AS BuildId,
                     ma.gameplay_json   AS GameplayJson,
                     mo.generator_params_json AS GeneratorParamsJson
              FROM custom_weapon_display d
              -- LEFT so a weapon whose model row is missing still LISTS. It cannot be packaged, but
              -- it must remain visible and deletable — an INNER join hid exactly the broken weapons
              -- the operator needs to delete and re-forge.
              LEFT JOIN custom_weapon_model mo ON mo.model_id = d.model_id
              LEFT JOIN custom_weapon_item_manifest ma ON ma.display_id = d.display_id
              ORDER BY d.display_id");

        var list = new List<ForgedWeaponInfo>();
        foreach (var r in rows)
        {
            long entry = r.ItemEntry is null ? 0L : (long)Convert.ToInt64(r.ItemEntry);
            string? gameplayJson = (string?)r.GameplayJson;
            string? generatorParamsJson = (string?)r.GeneratorParamsJson;
            string? weaponType = ReadGameplayJsonField(gameplayJson, "weaponType");
            int? inventoryType = ReadGameplayJsonInteger(gameplayJson, "inventoryType")
                ?? ReadGameplayJsonInteger(generatorParamsJson, "tbcInventoryType");
            if (inventoryType is <= 0) inventoryType = null;
            if (inventoryType is null && !string.IsNullOrWhiteSpace(weaponType))
            {
                var legacyProfile = WeaponTypeCatalog.All.FirstOrDefault(p =>
                    p.Key.Equals(weaponType, StringComparison.OrdinalIgnoreCase));
                inventoryType = legacyProfile?.InventoryType;
            }
            string? inventoryTypeLabel = ReadGameplayJsonField(gameplayJson, "inventoryTypeLabel");
            if (string.IsNullOrWhiteSpace(inventoryTypeLabel) && inventoryType is { } resolvedInventoryType)
                inventoryTypeLabel = InventoryTypeLabel(resolvedInventoryType);
            list.Add(new ForgedWeaponInfo
            {
                DisplayId = Convert.ToInt64(r.DisplayId),
                ItemEntry = entry,
                Name = ReadGameplayJsonField(gameplayJson, "name") ?? (entry > 0 ? $"Weapon {entry}" : "Unnamed weapon"),
                WeaponType = weaponType,
                InventoryType = inventoryType,
                InventoryTypeLabel = inventoryTypeLabel,
                SourceKind = (string?)r.SourceKind ?? "unknown",
                // Empty when the model row is gone — the row still lists so it can be deleted.
                ModelMpqPath = (string?)r.ModelMpqPath ?? "",
                DonorDisplayRow = r.DonorDisplayRow is null ? 0L : (long)Convert.ToInt64(r.DonorDisplayRow),
                ItemVisual = r.ItemVisual is null ? 0u : (uint)Convert.ToUInt32(r.ItemVisual),
                BuildId = (string?)r.BuildId,
                CreatedAt = (DateTime)r.CreatedAt,
            });
        }
        return list;
    }

    /// <summary>
    /// Register all forged weapon displays into DbcService's in-memory caches at startup, so the web
    /// UI resolves their icon/model/texture immediately after an app restart — the in-memory
    /// registration is not durable across restarts, exactly like the Retexture Engine's
    /// <c>LoadExistingRetexturesAsync</c> (which this mirrors, and which Program.cs calls alongside).
    /// </summary>
    public async Task LoadExistingWeaponsAsync()
    {
        try
        {
            var weapons = await ListWeaponsAsync();
            foreach (var w in weapons)
                RegisterDisplayWithDbc(w.DisplayId, checked((int)w.DisplayId), // modelIndex == displayId
                    w.DonorDisplayRow > 0 ? (uint)w.DonorDisplayRow : DonorDisplayRow,
                    itemVisual: w.ItemVisual);
            if (weapons.Count > 0)
                _logger.LogInformation("WeaponForge: registered {Count} forged weapon display(s) into the DBC cache", weapons.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: LoadExistingWeaponsAsync failed (forged weapons may render as red '?' until a rebuild)");
        }
    }

    /// <summary>Clone the family donor's ItemDisplayInfo row into the in-memory DBC under the forged
    /// display id, overriding the model/texture with the weapon's own SUI_W names. Best-effort: a
    /// failure here only costs the web preview, never the build.</summary>
    private void RegisterDisplayWithDbc(long displayId, int modelIndex, uint donorDisplayRow,
        bool mirrorModelName2 = false, uint itemVisual = 0)
    {
        try
        {
            string model = WeaponNaming.DbcModelName(modelIndex);
            _dbc.RegisterCustomDisplayEntry((uint)displayId, donorDisplayRow,
                model,
                WeaponNaming.DbcTextureName(modelIndex),
                customModelName2: mirrorModelName2 ? model : "",
                // The weapon's OWN glow, not the donor's — see the overload's remarks.
                itemVisual: itemVisual);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: DBC display registration for display {Display} failed", displayId);
        }
    }

    /// <summary>
    /// Remove one forged weapon EVERYWHERE the Forge put it: registry rows, its world-DB
    /// item_template row (with a core reload), and the packaged patch (repackaged and redeployed
    /// without it). Its entry/display ids are RELEASED for reuse — the audit log is the history.
    /// </summary>
    /// <param name="rebuild">Reload the world table and repack this lane's artifact after the delete.
    /// False when deleting a batch, so that happens ONCE at the end (see <see cref="DeleteWeaponsAsync"/>).</param>
    // ── ICustomMpqMemberSource ─────────────────────────────────────────────────────────────
    // Previews resolve a forged weapon's M2 and BLPs through MpqReaderService; until the next
    // Rebuild patch those bytes exist only here. Cached briefly so one dressing pass is one query
    // per member.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, byte[]? Data)> _memberCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MemberCacheTtl = TimeSpan.FromSeconds(30);

    public byte[]? TryGetMember(string mpqPath)
    {
        if (string.IsNullOrEmpty(mpqPath)) return null;
        if (_memberCache.TryGetValue(mpqPath, out var hit) && DateTime.UtcNow - hit.At < MemberCacheTtl)
            return hit.Data;
        byte[]? data = null;
        try
        {
            using var conn = _db.Admin();
            conn.Open();
            data = conn.ExecuteScalar<byte[]?>("SELECT compiled_m2 FROM custom_weapon_model WHERE model_mpq_path = @p LIMIT 1", new { p = mpqPath })
                ?? conn.ExecuteScalar<byte[]?>("SELECT compiled_blp FROM custom_weapon_display WHERE texture_mpq_path = @p LIMIT 1", new { p = mpqPath })
                ?? conn.ExecuteScalar<byte[]?>("SELECT compiled_blp FROM custom_weapon_model_texture WHERE mpq_path = @p LIMIT 1", new { p = mpqPath });
        }
        catch (Exception ex) { _logger.LogDebug(ex, "WeaponForge: registry member lookup failed for {Path}", mpqPath); }
        _memberCache[mpqPath] = (DateTime.UtcNow, data);
        return data;
    }

    public async Task<WeaponDeleteResult> DeleteWeaponAsync(long displayId, bool rebuild = true)
    {
        ForgedWeaponInfo? victim = (await ListWeaponsAsync()).FirstOrDefault(w => w.DisplayId == displayId);
        if (victim is null)
            throw new KeyNotFoundException($"No forged weapon with display id {displayId}.");

        // One delete writes several rows — the delete itself plus the patch rebuild it forces and
        // the armor re-union that cascades off that. Grouped, they read as one operation in the
        // Change Graph instead of three unrelated events a minute apart.
        using var batch = AuditBatch.Begin($"Weapon Forge — delete '{victim.Name}' (display {displayId})");
        try
        {
            return await DeleteWeaponCoreAsync(displayId, victim, rebuild);
        }
        catch (Exception ex)
        {
            await _audit.LogAsync(new AuditEntry
            {
                Category = "weaponforge",
                Action = "forge_delete",
                TargetType = "item",
                TargetName = victim.Name,
                TargetId = victim.ItemEntry is > 0 and <= int.MaxValue ? (int)victim.ItemEntry : null,
                StateBefore = JsonSerializer.Serialize(victim),
                IsReversible = false,
                RevertKind = RevertKind.None,
                Success = false,
                Notes = $"Delete FAILED: {ex.Message} — the weapon may be partly removed (registry rows go first, " +
                        "then the ids, then the world row, then the patch). Re-run the delete or check the Forged Weapons list.",
            });
            throw;
        }
    }

    private async Task<WeaponDeleteResult> DeleteWeaponCoreAsync(long displayId, ForgedWeaponInfo victim, bool rebuild)
    {
        // Snapshot the world row BEFORE it is destroyed. The ForgedWeaponInfo summary the delete row
        // used to carry names the weapon but not its stats, so a delete of the wrong item left
        // nothing to rebuild it from. RevertKind stays None — this is forensics, not an undo path.
        var itemRowSnapshot = victim.ItemEntry > 0 ? await ReadItemRowAsync(victim.ItemEntry) : null;

        await using (var conn = _db.Admin())
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM custom_weapon_item_manifest WHERE display_id = @displayId", new { displayId });
            await conn.ExecuteAsync("DELETE FROM custom_weapon_display WHERE display_id = @displayId", new { displayId });
            await conn.ExecuteAsync("DELETE FROM custom_weapon_model_texture WHERE model_id = @displayId", new { displayId });
            await conn.ExecuteAsync("DELETE FROM custom_weapon_model WHERE model_id = @displayId", new { displayId });
        }

        // Free the ids for reuse (best-effort — a hiccup here only wastes an id, never blocks the delete).
        try
        {
            if (victim.ItemEntry > 0)
                await _ids.ReleaseAsync(WeaponIdReservationService.KindItemEntry, victim.ItemEntry);
            await _ids.ReleaseAsync(WeaponIdReservationService.KindItemDisplay, displayId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: releasing ids for display {Display} failed (delete continues)", displayId);
        }

        var itemRow = victim.ItemEntry > 0
            ? await DeleteItemRowAsync(victim.ItemEntry)
            : (Ok: true, Message: "no item entry recorded");
        var reload = !rebuild ? (Ok: true, Message: "deferred to the end of the batch")
            : itemRow.Ok ? await ReloadItemTemplateAsync() : (Ok: false, Message: "skipped — world row not deleted");

        // Repack this lane's artifact so the registry and the build output agree, but leave the
        // client patch alone: the removal ships with the next Rebuild patch click like every forge.
        // A batch delete defers this too, so N deletes cost one repack rather than N.
        var patch = rebuild
            ? await RebuildPatchAsync($"deleted weapon display {displayId}", deploy: false)
            : DeferredRebuildSummary();

        await _audit.LogAsync(new AuditEntry
        {
            Category = "weaponforge",
            Action = "forge_delete",
            TargetType = "item",
            TargetName = victim.Name,
            TargetId = victim.ItemEntry is > 0 and <= int.MaxValue ? (int)victim.ItemEntry : null,
            RaCommand = itemRow.Ok ? ".reload item_template" : null,
            RaResponse = itemRow.Ok ? reload.Message : null,
            StateBefore = JsonSerializer.Serialize(new { registry = victim, itemTemplate = itemRowSnapshot }),
            IsReversible = false,
            RevertKind = RevertKind.None,
            Success = itemRow.Ok,
            Notes = $"Deleted everywhere. World row: {itemRow.Message}. Reload: {reload.Message}. " +
                    $"Patch: {(patch.PatchRemoved ? "removed (no weapons left)" : $"repackaged with {patch.WeaponCount} weapon(s)")}, " +
                    $"{patch.PatchDeployMessage}. Ids released for reuse. " +
                    (itemRowSnapshot is null
                        ? "No item_template row was found to snapshot."
                        : "The destroyed item_template row is captured in state_before."),
        });

        _logger.LogInformation("WeaponForge: deleted weapon display {Display} (entry {Entry}, '{Name}') everywhere",
            victim.DisplayId, victim.ItemEntry, victim.Name);

        return new WeaponDeleteResult
        {
            Deleted = victim,
            Rebuild = patch,
            ItemRowDeleted = itemRow.Ok,
            ItemRowMessage = itemRow.Message,
            Reloaded = reload.Ok,
            ReloadMessage = reload.Message,
        };
    }

    private static WeaponPatchRebuildSummary DeferredRebuildSummary(string message = "deferred to the end of the batch") => new()
    {
        WeaponCount = 0, PatchRemoved = false, MpqSha256 = null,
        PatchDeployed = false, PatchDeployMessage = message,
        Diagnostics = Array.Empty<string>(),
    };

    /// <summary>Delete several forged weapons with ONE world reload and ONE lane repack at the end,
    /// and one queued unified rebuild. Each weapon is still deleted (and audited) on its own, so one
    /// failure does not strand the rest; the per-weapon outcome is reported back.</summary>
    public async Task<WeaponBulkDeleteResult> DeleteWeaponsAsync(IReadOnlyList<long> displayIds)
    {
        var deleted = new List<ForgedWeaponInfo>();
        var failed = new List<(long DisplayId, string Error)>();
        var ids = displayIds.Distinct().ToList();
        using var batch = AuditBatch.Begin($"Weapon Forge — delete {ids.Count} weapon(s)");
        foreach (long id in ids)
        {
            try
            {
                var r = await DeleteWeaponAsync(id, rebuild: false);
                deleted.Add(r.Deleted);
                if (!r.ItemRowDeleted) failed.Add((id, $"registry row removed but the world row was not: {r.ItemRowMessage}"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WeaponForge: bulk delete of display {Display} failed", id);
                failed.Add((id, ex.Message));
            }
        }

        // Nothing removed means nothing to reload or repack — and no rebuild to queue.
        var reload = deleted.Count > 0 ? await ReloadItemTemplateAsync() : (Ok: true, Message: "nothing deleted — no reload");
        var rebuild = deleted.Count > 0
            ? await RebuildPatchAsync($"deleted {deleted.Count} weapon(s) in one batch", deploy: false)
            : DeferredRebuildSummary("nothing deleted — patch untouched");
        _logger.LogInformation("WeaponForge: bulk delete — {Deleted} deleted, {Failed} failed; reload={Reload}; {Patch}",
            deleted.Count, failed.Count, reload.Ok, rebuild.PatchDeployMessage);

        return new WeaponBulkDeleteResult
        {
            Deleted = deleted,
            Failed = failed.Select(f => new WeaponBulkDeleteFailure { DisplayId = f.DisplayId, Error = f.Error }).ToList(),
            Reloaded = reload.Ok, ReloadMessage = reload.Message,
            Rebuild = rebuild,
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // VANILLA LANE (clone an existing 1.12 weapon, re-itemize it)
    // ═══════════════════════════════════════════════════════════════════
    //
    // The third lane, alongside GLB and the TBC/WotLK imports, and the odd one out: it packages
    // NOTHING. The source is a weapon already in the live world DB, so its ItemDisplayInfo row, its
    // M2 and its BLP are all already in the operator's client. Cloning copies the item_template row
    // to a fresh custom entry, keeps the SOURCE display id, applies the operator's gameplay edits
    // and reloads the core. No id reservation for a display, no compile, no patch-5, no deploy, no
    // client restart — the sheath, attachment, two-hand grip and sound all ride along because the
    // display never changed.
    //
    // The clone is deliberately NOT written into custom_weapon_display. That table is keyed by
    // display_id and LoadExistingWeaponsAsync re-registers every row in it into DbcService at
    // startup and on every DBC reload, overriding the display's model/texture with this Forge's
    // SUI_W names. A clone carries a STOCK display id, so registering it would repoint that stock
    // display — breaking the source weapon and every other stock item that shares it, everywhere in
    // the web UI — and AssembleUnifiedPatchAsync would list it as permanently "skipped" for having
    // no compiled bytes. Clones therefore do not appear in the Forged Weapons list and are managed
    // from the Items page; the audit row carries RevertKind.DeleteCustom so the Change Graph can
    // undo one directly, which is more than a forged weapon offers.

    /// <summary>One row of the vanilla weapon browse.</summary>
    public sealed record VanillaWeaponDto(uint Entry, string Name, int Quality, int ItemLevel,
        int DisplayId, int ItemClass, int Subclass, int InventoryType, int DelayMs, float DamageMin, float DamageMax,
        string? Family, string FamilyLabel);

    /// <summary>The source weapon's real gameplay, for pre-filling the Configure-item modal. Unlike
    /// armor's equivalent this has to carry the weapon columns — damage, speed, subclass, sheath,
    /// block, ammo, range — because the weapon modal always sends a FULL override set and its
    /// defaults are a 2–4 damage, 20-durability trinket. Without the pre-fill, cloning Thunderfury
    /// would produce exactly that.</summary>
    public sealed record VanillaWeaponSourceDto(string Name, int Quality, int ItemLevel, int RequiredLevel,
        long BuyPrice, long SellPrice, int ItemClass, int Subclass, int InventoryType, string? Family, string FamilyLabel,
        float DamageMin, float DamageMax, int DamageType, int DelayMs, int Sheath, int AmmoType, int RangeModPercent,
        int Armor, int Block, int MaxDurability, int Bonding, int AllowableClass,
        // Every field below is one the Configure modal ALWAYS submits (collectItemConfig emits
        // allowableRace and the six required* values unconditionally, defaulting to 0). If the
        // pre-fill cannot see the source's value it cannot put it back in the form, so the clone
        // shipped with the restriction stripped: a race-locked, rank-gated or reputation-gated
        // weapon quietly became equippable by anyone. allowable_class already round-tripped; these
        // are the rest of the same contract.
        int AllowableRace, int RequiredSkill, int RequiredSkillRank, int RequiredSpell,
        int RequiredHonorRank, int RequiredReputationFaction, int RequiredReputationRank,
        int HolyRes, int FireRes, int NatureRes, int FrostRes, int ShadowRes, int ArcaneRes,
        IReadOnlyList<(int Type, int Value)> Stats,
        IReadOnlyList<(int SpellId, int Trigger, int Charges, float PpmRate, int CooldownMs, int Category, int CategoryCooldownMs)> Spells);

    /// <summary>Result of a vanilla clone. Deliberately not <see cref="CustomWeaponBuildResult"/> —
    /// there is no build id, no model, no patch and no packaged member count to report.</summary>
    public sealed class VanillaCloneResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public uint SourceEntry { get; set; }
        public long ItemEntry { get; set; }
        public long DisplayId { get; set; }
        public string Name { get; set; } = "";
        public string? Family { get; set; }
        public int InventoryType { get; set; }
        public bool Reloaded { get; set; }
        public string? ReloadMessage { get; set; }
    }

    /// <summary>Class-2 weapons plus class-4/subclass-6 shields (the Weapon Forge owns the shield
    /// family), from the LIVE world DB — no client mount needed, unlike the TBC/WotLK lanes.</summary>
    public async Task<IReadOnlyList<VanillaWeaponDto>> BrowseVanillaWeaponsAsync(string? search, string? family, int limit = 60)
    {
        limit = Math.Clamp(limit, 1, 200);
        string like = "%" + (search ?? "").Trim() + "%";
        uint entryExact = uint.TryParse((search ?? "").Trim(), out var ee) ? ee : 0;
        bool hasSearch = !string.IsNullOrWhiteSpace(search);

        await using var conn = _db.Mangos();
        await conn.OpenAsync();
        var rows = await conn.QueryAsync(
            // Two things armor's browse does not do, and should:
            //   • max-patch scoping (the same predicate /Items/Search uses) — item_template is
            //     keyed (entry, patch), so without it a weapon appears once per patch row it has.
            //   • excluding the custom range, or every weapon already cloned or forged by this app
            //     comes back as a clone source.
            @"SELECT entry, name, quality, item_level AS ItemLevel, display_id AS DisplayId,
                     class AS ItemClass, subclass AS Subclass, inventory_type AS InventoryType,
                     delay AS DelayMs, dmg_min1 AS DamageMin, dmg_max1 AS DamageMax
              FROM item_template
              WHERE patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = item_template.entry)
                AND (class = 2 OR (class = 4 AND subclass = 6))
                AND display_id > 0
                AND entry < @customFloor
                AND (@noSearch = 1 OR name LIKE @like OR entry = @entryExact)
              ORDER BY item_level DESC, quality DESC, name
              LIMIT @fetch",
            new
            {
                like, entryExact, noSearch = hasSearch ? 0 : 1,
                customFloor = WeaponIdReservationService.ItemEntryFloor,
                // Over-fetch: the family filter below cannot be pushed into SQL (it is a
                // subclass→family mapping, not a column), so trim after mapping.
                fetch = string.IsNullOrWhiteSpace(family) ? limit : limit * 4,
            });

        var list = new List<VanillaWeaponDto>();
        foreach (var r in rows)
        {
            int cls = Convert.ToInt32(r.ItemClass), sub = Convert.ToInt32(r.Subclass);
            string? famKey = LegacyItemCatalog.TypeKeyFor(cls, sub);
            if (!string.IsNullOrWhiteSpace(family) && !string.Equals(famKey, family, StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(new VanillaWeaponDto(
                Convert.ToUInt32(r.entry), (string)r.name, Convert.ToInt32(r.quality), Convert.ToInt32(r.ItemLevel),
                Convert.ToInt32(r.DisplayId), cls, sub, Convert.ToInt32(r.InventoryType),
                Convert.ToInt32(r.DelayMs), Convert.ToSingle(r.DamageMin), Convert.ToSingle(r.DamageMax),
                famKey, FamilyLabel(famKey, sub)));
            if (list.Count >= limit) break;
        }
        return list;
    }

    /// <summary>The source weapon's gameplay for the Configure modal's pre-fill. Null when the entry
    /// is missing or is not a weapon/shield.</summary>
    public async Task<VanillaWeaponSourceDto?> ReadVanillaWeaponSourceAsync(uint entry)
    {
        // propagate: a world-DB outage must reach the controller's catch as an outage, not arrive here
        // disguised as "no such row" and get reported to the operator as a fact about their item.
        var d = await ReadItemRowAsync(entry, propagate: true);
        if (d is null) return null;

        int I(string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v) : 0;
        long L(string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToInt64(v) : 0;
        float F(string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToSingle(v) : 0f;
        string S(string k) => d.TryGetValue(k, out var v) && v != null ? v.ToString() ?? "" : "";

        int cls = I("class"), sub = I("subclass");
        if (!IsForgeableWeapon(cls, sub)) return null;

        var stats = new List<(int, int)>();
        for (int i = 1; i <= 10; i++)
        {
            int t = I($"stat_type{i}"), v = I($"stat_value{i}");
            if (v != 0) stats.Add((t, v));
        }
        var spells = new List<(int, int, int, float, int, int, int)>();
        for (int i = 1; i <= 5; i++)
        {
            int sid = I($"spellid_{i}");
            if (sid != 0)
                spells.Add((sid, I($"spelltrigger_{i}"), I($"spellcharges_{i}"), F($"spellppmrate_{i}"),
                    I($"spellcooldown_{i}"), I($"spellcategory_{i}"), I($"spellcategorycooldown_{i}")));
        }

        string? famKey = LegacyItemCatalog.TypeKeyFor(cls, sub);
        return new VanillaWeaponSourceDto(
            S("name"), I("quality"), I("item_level"), I("required_level"),
            L("buy_price"), L("sell_price"), cls, sub, I("inventory_type"), famKey, FamilyLabel(famKey, sub),
            F("dmg_min1"), F("dmg_max1"), I("dmg_type1"), I("delay"), I("sheath"), I("ammo_type"), I("range_mod"),
            I("armor"), I("block"), I("max_durability"), I("bonding"), I("allowable_class"),
            I("allowable_race"), I("required_skill"), I("required_skill_rank"), I("required_spell"),
            I("required_honor_rank"), I("required_reputation_faction"), I("required_reputation_rank"),
            I("holy_res"), I("fire_res"), I("nature_res"), I("frost_res"), I("shadow_res"), I("arcane_res"),
            stats, spells);
    }

    /// <summary>
    /// Clone an existing vanilla weapon into a new custom entry, reusing its display, then apply the
    /// operator's gameplay edits. Usable via <c>.additem</c> after the reload; nothing is packaged.
    /// </summary>
    public async Task<VanillaCloneResult> CloneVanillaWeaponAsync(uint sourceEntry, string? nameOverride,
        ValidatedVanillaItemBuildConfiguration? gameplay)
    {
        var result = new VanillaCloneResult { SourceEntry = sourceEntry };

        await using var conn = _db.Mangos();
        await conn.OpenAsync();

        var srcRow = await conn.QueryFirstOrDefaultAsync(
            @"SELECT entry, name, class AS ItemClass, subclass AS Subclass,
                     display_id AS DisplayId, inventory_type AS InventoryType
              FROM item_template
              WHERE entry=@e
              ORDER BY patch DESC LIMIT 1",
            new { e = sourceEntry });
        if (srcRow is null) { result.Message = $"Vanilla item {sourceEntry} not found."; return result; }

        int cls = Convert.ToInt32(srcRow.ItemClass), sub = Convert.ToInt32(srcRow.Subclass);
        if (!IsForgeableWeapon(cls, sub))
        {
            result.Message = cls == 4
                ? $"Item {sourceEntry} is armor — clone it from the Armor Forge's Vanilla lane."
                : $"Item {sourceEntry} is not a weapon (class 2) or shield (class 4 / subclass 6).";
            return result;
        }
        if (sourceEntry >= WeaponIdReservationService.ItemEntryFloor)
        {
            result.Message = $"Item {sourceEntry} is already a custom item — clone from a stock weapon instead.";
            return result;
        }

        string sourceName = (string)srcRow.name;
        int displayId = Convert.ToInt32(srcRow.DisplayId);
        int inv = Convert.ToInt32(srcRow.InventoryType);
        string? famKey = LegacyItemCatalog.TypeKeyFor(cls, sub);

        string buildId = "wpn-clone-" + Guid.NewGuid().ToString("N")[..12];
        long entryFloor = await _ids.ComputeItemEntryFloorAsync();
        var entryRes = await _ids.ReserveAsync(WeaponIdReservationService.KindItemEntry, entryFloor, buildId, "item");
        long newEntry = entryRes.Id;

        string name = !string.IsNullOrWhiteSpace(nameOverride) ? nameOverride!.Trim()
            : !string.IsNullOrWhiteSpace(gameplay?.Name) ? gameplay!.Name!.Trim()
            : sourceName;

        // A transaction around the copy AND the operator's overrides — plus a compensating delete,
        // because on this schema the transaction alone is not enough. VMaNGOS ships item_template as
        // ENGINE=MyISAM (WEAPON_GEN.md's DDL, and the owner's own schema dump in
        // wwwroot/data/curated-relationships.json records mangos.item_template as MyISAM). MyISAM
        // ignores START TRANSACTION: the INSERT is committed the moment it runs and ROLLBACK answers
        // warning 1196 rather than undoing anything. The transaction is kept because an InnoDB fork
        // DOES honour it; the catch below is what actually cleans up here.
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            // Schema-agnostic column list so this keeps working on forks that add item_template
            // columns. Every column but the entry rides across verbatim — including subclass,
            // sheath, sound_override_subclass, ammo_type, range_mod and stackable, none of which the
            // config modal can express and all of which must match the display the clone reuses.
            var cols = (await conn.QueryAsync<string>(
                @"SELECT COLUMN_NAME FROM information_schema.COLUMNS
                  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'item_template' ORDER BY ORDINAL_POSITION",
                transaction: tx)).ToList();
            if (cols.Count == 0) throw new InvalidOperationException("item_template schema could not be read.");

            string colList = string.Join(",", cols.Select(c => $"`{c}`"));
            string selList = string.Join(",", cols.Select(c =>
                c.Equals("entry", StringComparison.OrdinalIgnoreCase) ? "@newEntry"
                // patch 0, matching DonorItemTemplateFixture (every forged weapon is patch 0). The
                // core loads, per entry, the highest patch row NOT ABOVE the server's content-patch
                // setting; inheriting the source's patch left the clone invisible on any server
                // configured below it, which is exactly the case a clone of a late-patch item hits.
                : c.Equals("patch", StringComparison.OrdinalIgnoreCase) ? "0"
                // These name OTHER item entries — the faction mirror, the quest a book starts, the
                // gift a wrapper produces. Copied verbatim they hand the clone the source's
                // relationships: an Alliance clone still pointing at the Horde original, a second
                // item handing out the same quest.
                : c.Equals("other_team_entry", StringComparison.OrdinalIgnoreCase) ? "0"
                : c.Equals("start_quest", StringComparison.OrdinalIgnoreCase) ? "0"
                : c.Equals("wrapped_gift", StringComparison.OrdinalIgnoreCase) ? "0"
                // set_id joins the item to a stock item set. Riding it across silently makes the clone
                // count toward that set's bonuses — a gameplay change nobody asked for.
                : c.Equals("set_id", StringComparison.OrdinalIgnoreCase) ? "0"
                : $"`{c}`"));
            // ORDER BY patch DESC LIMIT 1 is load-bearing, not tidiness. A source revised across
            // content patches has one row per patch, and because the projection above pins every copy
            // to patch 0, an unqualified SELECT would try to insert them ALL as (newEntry, 0) and die
            // on the (entry, patch) primary key with ER_DUP_ENTRY.
            int inserted = await conn.ExecuteAsync(
                $"INSERT INTO item_template ({colList}) SELECT {selList} FROM item_template WHERE entry=@src ORDER BY patch DESC LIMIT 1",
                new { newEntry, src = sourceEntry }, tx);
            // Never fell through before: a zero-row insert reported a perfectly successful clone of
            // nothing, and the reserved id was marked committed against a row that does not exist.
            if (inserted != 1)
                throw new InvalidOperationException(
                    $"clone INSERT affected {inserted} rows (expected 1) copying item {sourceEntry}.");

            // Name + the validated gameplay overrides on top of the cloned row.
            var sets = new List<string> { "name=@nm" };
            var dp = new DynamicParameters();
            dp.Add("e", newEntry);
            dp.Add("nm", name);
            if (gameplay?.Overrides is { Count: > 0 })
            {
                foreach (var (col, literal) in gameplay.Overrides)
                {
                    // The clone keeps the SOURCE's slot. The modal always sends inventory_type, and
                    // honouring it would let a 1H sword (subclass 7) be moved into the two-hand slot,
                    // or a fishing pole into a slot its own subclass forbids — with a stock display
                    // that cannot be re-scaffolded to match. Slot changes belong to the import lanes,
                    // which build a model for the family they claim.
                    if (col.Equals("inventory_type", StringComparison.OrdinalIgnoreCase)) continue;

                    if (col.Equals("description", StringComparison.OrdinalIgnoreCase))
                    {
                        sets.Add("description=@desc");
                        dp.Add("desc", literal); // raw string value, parameterized
                    }
                    else
                    {
                        // The translator emits safe numeric/CONVERT literals for every other column.
                        sets.Add($"`{col}`={literal}");
                    }
                }
            }
            int updated = await conn.ExecuteAsync(
                $"UPDATE item_template SET {string.Join(",", sets)} WHERE entry=@e", dp, tx);
            if (updated != 1)
                throw new InvalidOperationException(
                    $"clone UPDATE affected {updated} rows (expected 1) for new entry {newEntry}.");

            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(); }
            catch (Exception rollbackEx) { _logger.LogWarning(rollbackEx, "WeaponForge: clone rollback failed for entry {Entry}", newEntry); }

            // On MyISAM the rollback above did nothing, so remove the copy by hand before the id goes
            // back in the pool. Releasing the id without this leaves a live, unedited duplicate of the
            // source under a custom entry that nothing points at and nothing records.
            bool cleaned;
            try
            {
                await conn.ExecuteAsync("DELETE FROM item_template WHERE entry=@e", new { e = newEntry });
                cleaned = true;
            }
            catch (Exception cleanupEx)
            {
                cleaned = false;
                _logger.LogWarning(cleanupEx, "WeaponForge: clone cleanup delete failed for entry {Entry}", newEntry);
            }

            await _ids.ReleaseAsync(WeaponIdReservationService.KindItemEntry, entryRes.Id);
            result.Ok = false;
            result.Message = "clone failed: " + ex.Message;
            await _audit.LogAsync(new AuditEntry
            {
                Category = "weaponforge", Action = "clone_vanilla", TargetType = "item_custom", TargetName = name,
                TargetId = newEntry is > 0 and <= int.MaxValue ? (int)newEntry : null,
                StateAfter = JsonSerializer.Serialize(new { buildId, sourceEntry, itemEntry = newEntry, displayId, family = famKey }),
                IsReversible = false, RevertKind = RevertKind.None, Success = false,
                Notes = $"Vanilla weapon clone {sourceEntry} → {newEntry} FAILED: {ex.Message}. The id was returned to the pool. " +
                        (cleaned
                            ? $"item_template row {newEntry} was removed."
                            : $"THE CLEANUP DELETE ALSO FAILED — a partial copy of {sourceEntry} may still be live under entry " +
                              $"{newEntry}. Check with: SELECT * FROM item_template WHERE entry = {newEntry};"),
            });
            return result;
        }

        await _ids.MarkStateAsync(WeaponIdReservationService.KindItemEntry, entryRes.Id, "committed");

        var reload = await ReloadItemTemplateAsync();

        result.Ok = true;
        result.ItemEntry = newEntry;
        result.DisplayId = displayId;
        result.Name = name;
        result.Family = famKey;
        result.InventoryType = inv;
        result.Reloaded = reload.Ok;
        result.ReloadMessage = reload.Message;
        result.Message = $"Cloned vanilla {sourceEntry} → {newEntry} (reuses display {displayId}). No patch needed.";

        await _audit.LogAsync(new AuditEntry
        {
            Category = "weaponforge",
            Action = "clone_vanilla",
            // "item_custom" (not "item", which the forge rows use) is deliberate: it is the target
            // type ChangeGraphService.RevertByDeletingAsync keys on, and it puts the row in the
            // Change Graph's items domain and the drift tracker's items surface — none of which
            // "item" gets. A clone is the one forge output the graph can genuinely undo, because
            // there is no registry row and no patch left behind by deleting the item.
            TargetType = "item_custom",
            TargetName = name,
            TargetId = checked((int)newEntry),
            RaCommand = ".reload item_template",
            RaResponse = reload.Message,
            StateAfter = JsonSerializer.Serialize(new
            {
                buildId, sourceEntry, sourceName, itemEntry = newEntry, displayId,
                family = famKey, itemClass = cls, subclass = sub, inventoryType = inv,
                overrides = gameplay?.Overrides,
            }),
            IsReversible = true,
            RevertKind = RevertKind.DeleteCustom,
            Success = true,
            Notes = $"Vanilla clone {sourceEntry} ('{sourceName}') → {newEntry}, reuses display {displayId}. " +
                    $"Reload: {reload.Message}. No patch — the model and texture are already in the client. " +
                    "Undo from the Change Graph, or delete the item on the Items page.",
        });

        _logger.LogInformation("WeaponForge: cloned vanilla weapon {Source} → {Entry} '{Name}' (display {Display})",
            sourceEntry, newEntry, name, displayId);
        return result;
    }

    /// <summary>Class-2 weapons and class-4/subclass-6 shields. Everything else belongs to the
    /// Armor Forge (or to no forge at all).</summary>
    private static bool IsForgeableWeapon(int itemClass, int subclass) =>
        itemClass == 2 || (itemClass == 4 && subclass == 6);

    /// <summary>A display label for the browse. Families the import lanes decline still get a name
    /// here — a clone needs no donor scaffold, so a fist weapon or fishing pole clones fine.</summary>
    private static string FamilyLabel(string? famKey, int subclass) =>
        famKey is not null ? WeaponTypeCatalog.Get(famKey).Label
        : subclass switch
        {
            9 => "Warglaive",
            11 or 12 => "Exotic",
            13 => "Fist Weapon",
            17 => "Spear",
            20 => "Fishing Pole",
            _ => "Other",
        };

    // ═══════════════════════════════════════════════════════════════════
    // SERVER APPLY (world DB + RA reload + client patch deploy)
    // ═══════════════════════════════════════════════════════════════════

    private async Task<(bool Ok, string Message)> ApplyItemSqlAsync(GeneratedSql sql, long entry)
    {
        try
        {
            await using var conn = _db.Mangos();
            await conn.OpenAsync();
            await conn.ExecuteAsync(sql.Text);
            return (true, $"item_template row {entry} inserted");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: item_template insert for entry {Entry} failed", entry);
            return (false, $"world DB insert failed: {ex.Message} — item_template.sql is in the build folder for manual apply");
        }
    }

    /// <summary>The whole item_template row, for the audit trail's state_before. Schema-agnostic
    /// (SELECT *), so it keeps working across core forks that add columns. Best-effort: a delete is
    /// never blocked by a snapshot that could not be taken, it just loses the forensics.</summary>
    /// <summary><paramref name="propagate"/> decides what a database failure means to the caller. The
    /// delete path wants the swallow — a missing snapshot must not stop a delete. The clone pre-fill
    /// does NOT: swallowing there turned "the world DB is down" into a null row, which the caller then
    /// reported as "item N is not a stock weapon or shield" — a statement about the item, when the item
    /// was never read.</summary>
    private async Task<IDictionary<string, object>?> ReadItemRowAsync(long entry, bool propagate = false)
    {
        try
        {
            await using var conn = _db.Mangos();
            await conn.OpenAsync();
            // ORDER BY patch DESC: item_template is keyed (entry, patch), so an unordered read of a
            // multi-patch stock weapon returns an arbitrary older row — wrong for the clone pre-fill
            // and misleading in a delete snapshot.
            var row = await conn.QueryFirstOrDefaultAsync(
                "SELECT * FROM item_template WHERE entry = @entry ORDER BY patch DESC LIMIT 1", new { entry });
            return row is null ? null : (IDictionary<string, object>)row;
        }
        catch (Exception ex)
        {
            if (propagate) throw;
            _logger.LogWarning(ex, "WeaponForge: snapshot of item_template {Entry} failed (delete continues)", entry);
            return null;
        }
    }

    private async Task<(bool Ok, string Message)> DeleteItemRowAsync(long entry)
    {
        try
        {
            await using var conn = _db.Mangos();
            await conn.OpenAsync();
            int rows = await conn.ExecuteAsync("DELETE FROM item_template WHERE entry = @entry", new { entry });
            return (true, rows > 0 ? $"item_template row {entry} deleted" : $"no item_template row {entry} existed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: item_template delete for entry {Entry} failed", entry);
            return (false, $"world DB delete failed: {ex.Message} — run manually: DELETE FROM item_template WHERE entry = {entry};");
        }
    }

    private async Task<(bool Ok, string Message)> ReloadItemTemplateAsync()
    {
        try
        {
            var response = await _ra.SendCommandAsync(".reload item_template");
            var trimmed = (response ?? "").Trim();
            return (true, trimmed.Length > 0 ? trimmed : "reload issued");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: .reload item_template failed");
            return (false, $"RA reload failed: {ex.Message} — run .reload item_template yourself");
        }
    }

    private (bool Ok, string Message) DeployPatchToClient(byte[] mpqBytes)
    {
        var dataPath = ClientDataPath;
        if (dataPath is null)
            return (false, "no client Data path configured — copy the downloaded patch yourself");
        try
        {
            string target = Path.Combine(dataPath, PatchFileName);
            File.WriteAllBytes(target, mpqBytes);
            return (true, $"deployed to {target}");
        }
        catch (Exception ex)
        {
            return (false, $"deploy failed ({ex.Message}) — the client is probably running; close it and click Rebuild patch");
        }
    }

    private (bool Ok, bool Changed, string Message) RemoveDeployedPatch()
    {
        var dataPath = ClientDataPath;
        if (dataPath is null) return (true, false, "no client Data path configured");
        try
        {
            string target = Path.Combine(dataPath, PatchFileName);
            if (File.Exists(target)) { File.Delete(target); return (true, true, $"removed {target}"); }
            return (true, false, "no deployed patch to remove");
        }
        catch (Exception ex)
        {
            return (false, true, $"could not remove deployed patch ({ex.Message}) — the client is probably running; delete it after closing");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SHARED ASSEMBLY
    // ═══════════════════════════════════════════════════════════════════

    private sealed class UnifiedAssembly
    {
        public WeaponPatchResult? Patch;     // null ⇔ zero packageable weapons
        public int PackagedCount;
        public int SkippedCount;
        public int ReplacedInBase;
    }

    private async Task<UnifiedAssembly> AssembleUnifiedPatchAsync(ForgeDiagnostics diag, string tempKey)
    {
        byte[] baseDbc = ResolveBaseDbc();
        var baseReader = DbcWriterService.ReadDbc(baseDbc, WeaponNaming.ItemDisplayInfoMember);
        uint donorGroupSound = ReadGroupSound(baseReader);

        string donorIcon = ReadDonorIconStem(baseReader);

        var installed = await LoadPackagedWeaponsAsync();
        var skipped = installed.Where(w => w.M2 is null || w.Blp is null || w.ModelMpqPath is null).ToList();
        var packaged = installed.Where(w => w.M2 is not null && w.Blp is not null && w.ModelMpqPath is not null).ToList();
        foreach (var s in skipped)
        {
            // Name WHICH half is missing: a null model path means the custom_weapon_model row is
            // gone outright (the case that used to disappear silently through an INNER join), while
            // null bytes mean the row exists but was never compiled into it.
            string what = s.ModelMpqPath is null ? "has no custom_weapon_model row at all"
                        : s.M2 is null && s.Blp is null ? "has neither compiled M2 nor BLP bytes"
                        : s.M2 is null ? "has no compiled M2 bytes"
                        : "has no compiled BLP bytes";
            diag.Warn("package.skipped",
                $"display {s.DisplayId} {what} — it is NOT in this patch and will render as the error model in-game; " +
                "delete it from the Forged Weapons list and re-forge it to restore its art");
        }

        if (packaged.Count == 0)
            return new UnifiedAssembly { Patch = null, PackagedCount = 0, SkippedCount = skipped.Count, ReplacedInBase = 0 };

        // If a previously built Forge patch was mounted anyway (or a custom row leaked into a lower
        // archive), strip our ids so the DB-driven rebuild is the single authority.
        var customIds = packaged.Select(w => (uint)w.DisplayId).ToHashSet();
        int replaced = baseReader.RemoveRowsWhere(id => customIds.Contains(id));
        byte[] cleanedBase = replaced > 0 ? baseReader.Write() : baseDbc;

        // Effect textures (multi-pass glow) and packaged icons ride along as additional members,
        // keyed by the hardcoded paths the packaged M2s and display rows reference.
        var effectMembers = await LoadEffectTextureMembersAsync(
            packaged.Select(w => (long)w.ModelId).ToHashSet());

        // One member per canonical path across BOTH texture sets. Per-weapon art (SUI_W_nnnn) is
        // unique by construction, but a SHARED member is not: every weapon imported from the same
        // source ships that source's bag icon as Interface\Icons\<stem>.blp, so a second import off
        // the same art — the off-hand Warglaive after the main hand — lands on a path the first one
        // already occupies. The builder treats a duplicate path as a determinism failure and throws,
        // which turned that into a hard "Duplicate MPQ member path" build error with nothing wrong.
        // Collapse here instead: identical bytes ARE the same file, and differing bytes get a
        // diagnostic naming the collision rather than a silent overwrite. Keyed through the
        // builder's own canonicaliser so the two agree on what "same path" means. The armor
        // assembler funnels every member through one such set and so cannot hit this; weapons
        // deduped models and per-weapon textures separately and concatenated shared members raw,
        // which left the icon — the only member two weapons can share — completely unguarded.
        var textureMembers = new List<MpqMember>();
        var seenTexturePaths = new Dictionary<string, MpqMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in packaged
                     .Select(w => new MpqMember { MpqPath = w.TextureMpqPath, Data = w.Blp! })
                     .Concat(effectMembers))
        {
            string key = WeaponPatchBuilder.CanonicalMpqPath(member.MpqPath);
            if (seenTexturePaths.TryGetValue(key, out var kept))
            {
                if (!kept.Data.AsSpan().SequenceEqual(member.Data))
                    diag.Warn("package.member.conflict",
                        $"Two forged weapons ship DIFFERENT bytes for the same member '{key}' " +
                        $"({kept.Data.Length:N0} vs {member.Data.Length:N0} bytes); packing the first and " +
                        "dropping the second — one of the two will wear the other's art in-game. Re-forge the " +
                        "later weapon with its own texture or icon name if both are meant to differ.");
                continue;
            }
            seenTexturePaths[key] = member;
            textureMembers.Add(member);
        }

        var input = new WeaponPatchInput
        {
            CleanItemDisplayInfoDbc = cleanedBase,
            Displays = packaged.Select(w => new WeaponDisplayInfoParams
            {
                DisplayId = (uint)w.DisplayId,
                ModelIndex = (int)w.ModelId,
                GroupSoundIndex = w.GroupSoundIndex ?? donorGroupSound,
                // Package-time fallback heals rows persisted before the icon fix (empty stem = red "?").
                IconStem = string.IsNullOrEmpty(w.IconStem) ? donorIcon : w.IconStem,
                SpellVisualId = w.SpellVisualId ?? 0,
                MirrorModelName2 = w.MirrorModelName2,
                ItemVisual = (uint)w.ItemVisual,
            }).ToArray(),
            Models = packaged.GroupBy(w => w.ModelMpqPath!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new MpqMember { MpqPath = g.Key, Data = g.First().M2! }).ToArray(),
            Textures = textureMembers.ToArray(),
        };
        string tempDir = Path.Combine(Path.GetTempPath(), "weaponforge", tempKey);
        var patch = _patch.Build(input, tempDir);

        return new UnifiedAssembly
        {
            Patch = patch,
            PackagedCount = packaged.Count,
            SkippedCount = skipped.Count,
            ReplacedInBase = replaced,
        };
    }

    /// <summary>This lane's rows and members for the single unified patch. Same DB sources as
    /// <see cref="AssembleUnifiedPatchAsync"/>, but it stops short of building an archive: the
    /// unified builder owns the one ItemDisplayInfo.dbc now, and because DBC rows carry string
    /// OFFSETS into a per-file string block, contributing rows into ITS writer is the only way the
    /// names stay valid. No base-row cleanup is needed here either — the unified base is resolved
    /// from beneath patch-4, so no forge output can leak back in as its own input.
    ///
    /// Members are handed over UNCOLLAPSED: the unified builder dedupes across all three lanes at
    /// once, which is where a weapon and an armor piece sharing one bag icon actually meet.</summary>
    internal async Task<UnifiedPatch.WeaponLaneContribution> GetPatchContributionAsync(ForgeDiagnostics diag)
    {
        byte[] baseDbc = ResolveBaseDbc();
        var baseReader = DbcWriterService.ReadDbc(baseDbc, WeaponNaming.ItemDisplayInfoMember);
        uint donorGroupSound = ReadGroupSound(baseReader);
        string donorIcon = ReadDonorIconStem(baseReader);

        var installed = await LoadPackagedWeaponsAsync();
        var skipped = installed.Where(w => w.M2 is null || w.Blp is null || w.ModelMpqPath is null).ToList();
        var packaged = installed.Where(w => w.M2 is not null && w.Blp is not null && w.ModelMpqPath is not null).ToList();
        foreach (var s in skipped)
        {
            string what = s.ModelMpqPath is null ? "has no custom_weapon_model row at all"
                        : s.M2 is null && s.Blp is null ? "has neither compiled M2 nor BLP bytes"
                        : s.M2 is null ? "has no compiled M2 bytes"
                        : "has no compiled BLP bytes";
            diag.Warn("weapon.skipped",
                $"weapon display {s.DisplayId} {what} — it is NOT in this patch and will render as the error model in-game; " +
                "delete it from the Forged Weapons list and re-forge it to restore its art");
        }
        if (packaged.Count == 0)
            return new UnifiedPatch.WeaponLaneContribution { SkippedCount = skipped.Count };

        var effectMembers = await LoadEffectTextureMembersAsync(
            packaged.Select(w => (long)w.ModelId).ToHashSet());

        var members = new List<MpqMember>();
        members.AddRange(packaged.GroupBy(w => w.ModelMpqPath!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MpqMember { MpqPath = g.Key, Data = g.First().M2! }));
        members.AddRange(packaged.Select(w => new MpqMember { MpqPath = w.TextureMpqPath, Data = w.Blp! }));
        members.AddRange(effectMembers);

        return new UnifiedPatch.WeaponLaneContribution
        {
            Displays = packaged.Select(w => new WeaponDisplayInfoParams
            {
                DisplayId = (uint)w.DisplayId,
                ModelIndex = (int)w.ModelId,
                GroupSoundIndex = w.GroupSoundIndex ?? donorGroupSound,
                // Package-time fallback heals rows persisted before the icon fix (empty stem = red "?").
                IconStem = string.IsNullOrEmpty(w.IconStem) ? donorIcon : w.IconStem,
                SpellVisualId = w.SpellVisualId ?? 0,
                MirrorModelName2 = w.MirrorModelName2,
                ItemVisual = (uint)w.ItemVisual,
            }).ToArray(),
            Members = members,
            SkippedCount = skipped.Count,
        };
    }

    /// <summary>The chosen enchant glow (ItemVisual) resolved into effect models mounted on the
    /// built M2's own attachment points, for the post-forge preview. Degrades to null — a visual is
    /// an enhancement and must never fail a build's preview step.</summary>
    private IReadOnlyList<M2Fx.ItemVisualEffects.Effect>? ResolvePreviewVisualEffects(uint itemVisualId, byte[] m2)
    {
        if (itemVisualId == 0) return null;
        try
        {
            var host = M2Reader.Parse(m2);
            var effects = M2Fx.ItemVisualEffects.Resolve(itemVisualId, host,
                path => _mpq.ExtractFile(path) ?? _mpq.ExtractFile(path.ToLowerInvariant()));
            return effects.Count > 0 ? effects : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Custom/private render-graph effect members come from the build request. Any remaining
    /// hardcoded stock paths (for example an unchanged Thunderfury sheet), plus replaceable Type 3
    /// which the client supplies as ArmorReflect4, are resolved from the stock mount only for WebGL
    /// preview. A recolored sheet is always repointed to its private path first; a stock path is
    /// never persisted or shadowed globally.
    /// </summary>
    private IReadOnlyDictionary<string, byte[]>? ResolvePreviewTextureBlps(
        byte[] m2Bytes, IReadOnlyList<(string MpqPath, byte[] Blp)> builtTextures)
    {
        var result = builtTextures.ToDictionary(e => e.MpqPath, e => e.Blp,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            var model = M2Reader.Parse(m2Bytes);
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
        }
        catch { /* missing stock preview texture never invalidates the built artifact */ }
        return result.Count > 0 ? result : null;
    }

    /// <summary>The base ItemDisplayInfo.dbc to union onto: the effective mounted copy from strictly
    /// BENEATH patch-5 — so the Forge never reads its own installed output back as input, and rows
    /// added to lower patches (the Retexture Engine's patch-4) are always re-unioned.
    /// An explicit clean copy can be pointed at via WeaponForge:CleanDbcPath.
    ///
    /// Skipping by RANK, not by name, is load-bearing. Skipping only "patch-5" left patch-6 (armor,
    /// which ranks ABOVE us) as the first archive to answer, so patch-5's base was patch-6 — whose
    /// own base was patch-5. The two archives recycled one row set forever: patch-4's retexture rows
    /// could never re-enter the table (measured: 1,625 ids in the 67249–68876 band went missing from
    /// the client-effective DBC), and a row whose art had been deleted became immortal, ping-ponging
    /// between the two with no members behind it. Ranking turns that cycle into a strict downward
    /// chain — patch-4 → patch-5 → patch-6 — which is what the layering always intended.</summary>
    private byte[] ResolveBaseDbc()
    {
        var cfgPath = _config["WeaponForge:CleanDbcPath"];
        if (!string.IsNullOrWhiteSpace(cfgPath) && File.Exists(cfgPath))
            return File.ReadAllBytes(cfgPath);

        // Beneath the UNIFIED patch, not this lane's old patch-5: the forge's own display rows live
        // in patch-4 now. Reading them back as base makes a re-used display id "already exist" once
        // a weapon has been deleted and the on-request rebuild has not yet replaced the client copy.
        int myRank = Mpq.MpqPatchOrder.Rank(UnifiedPatch.UnifiedPatchService.PatchFileName);
        return _mpq.ExtractFile(WeaponNaming.ItemDisplayInfoMember,
                skipArchive: name => Mpq.MpqPatchOrder.Rank(name) >= myRank)
            ?? throw new InvalidOperationException("Could not extract a base ItemDisplayInfo.dbc from the mounted archives.");
    }

    /// <summary>Fallback texture for a textureless mesh: the family donor's own BLP where the donor
    /// row names one, else the golden sword's — so the model always samples something valid.</summary>
    private byte[] ExtractDonorBlp(WeaponDonorInfo donor)
    {
        if (donor.BlpPath is { Length: > 0 } path)
        {
            var blp = _mpq.ExtractFile(path);
            if (blp is { Length: > 0 }) return blp;
        }
        return _mpq.ExtractFile(DonorBlpPath)
            ?? throw new InvalidOperationException($"Donor BLP not found in mounted archives: {DonorBlpPath}");
    }

    private static uint ReadGroupSound(DbcWriterService dbc)
    {
        var row = dbc.GetRow(DonorDisplayRow);
        return row is not null && row.Length > WeaponDisplayInfoRow.F_GroupSoundIndex
            ? row[WeaponDisplayInfoRow.F_GroupSoundIndex]
            : 0u;
    }

    /// <summary>The donor's inventory icon stem (field 5 of display row 679, e.g. INV_Sword_04).
    /// An empty InventoryIcon renders as the red "?" in bags — proven in the first real-client run —
    /// so every weapon without its own icon inherits the donor's until icon generation exists.</summary>
    private static string ReadDonorIconStem(DbcWriterService dbc)
    {
        var row = dbc.GetRow(DonorDisplayRow);
        if (row is not null && row.Length > WeaponDisplayInfoRow.F_InventoryIcon)
        {
            var stem = dbc.ReadString(row[WeaponDisplayInfoRow.F_InventoryIcon]);
            if (!string.IsNullOrEmpty(stem)) return stem;
        }
        return "INV_Sword_04"; // donor 2131 Shortsword's stock icon — always present in base MPQs
    }

    private static string? ReadGameplayJsonField(string? json, string field)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            return doc.TryGetProperty(field, out var n) ? n.GetString() : null;
        }
        catch { return null; }
    }

    private static int? ReadGameplayJsonInteger(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(field, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        }
        catch { /* older/malformed optional metadata falls through to the family default */ }
        return null;
    }

    private static int ResolveEffectiveInventoryType(WeaponTypeProfile profile,
        IReadOnlyDictionary<string, string> effectiveOverrides)
    {
        if (!effectiveOverrides.TryGetValue("inventory_type", out string? value) ||
            !int.TryParse(value, out int inventoryType) || inventoryType <= 0)
            throw new InvalidOperationException(
                $"Weapon type '{profile.Key}' produced invalid inventory_type '{value ?? "<missing>"}'.");
        return inventoryType;
    }

    internal static (uint GroupSoundIndex, uint SpellVisualId, bool MirrorModelName2)
        ResolveDisplayFields(CustomWeaponBuildRequest request, uint donorGroupSoundIndex,
            uint donorSpellVisualId, bool donorMirrorModelName2)
        => (
            request.DisplayGroupSoundIndex ?? donorGroupSoundIndex,
            request.DisplaySpellVisualId ?? donorSpellVisualId,
            request.DisplayMirrorModelName2 ?? donorMirrorModelName2);

    /// <summary>Human equip-slot label for the vanilla/TBC inventory enum. Kept separate from the
    /// weapon-family label because a 1H Sword may be unrestricted, Main Hand, or Off Hand.</summary>
    public static string InventoryTypeLabel(int inventoryType) => inventoryType switch
    {
        0 => "Non-equip",
        1 => "Head",
        2 => "Neck",
        3 => "Shoulder",
        4 => "Shirt",
        5 => "Chest",
        6 => "Waist",
        7 => "Legs",
        8 => "Feet",
        9 => "Wrist",
        10 => "Hands",
        11 => "Finger",
        12 => "Trinket",
        13 => "One-Hand",
        14 => "Shield",
        15 => "Ranged",
        16 => "Back",
        17 => "Two-Hand",
        18 => "Bag",
        19 => "Tabard",
        20 => "Robe",
        21 => "Main Hand",
        22 => "Off Hand",
        23 => "Held Off-Hand",
        24 => "Ammo",
        25 => "Thrown",
        26 => "Ranged",
        27 => "Quiver",
        28 => "Relic",
        _ => $"Inventory type {inventoryType}",
    };

    // ═══════════════════════════════════════════════════════════════════
    // PERSISTENCE
    // ═══════════════════════════════════════════════════════════════════

    private async Task PersistRecordsAsync(CustomWeaponBuildRequest request, WeaponTypeProfile profile,
        WeaponDonorInfo donor, string buildId, int modelIndex,
        long entry, long display, string weaponName, int effectiveInventoryType, byte[] m2, byte[] blp,
        IReadOnlyList<(string MpqPath, byte[] Blp)> effectBlps, GeneratedSql sql, uint groupSound,
        uint spellVisualId, bool mirrorModelName2, string iconStem)
    {
        string? sourceSha = request.SourceBlob is { Length: > 0 } src ? Sha256(src) : null;
        // Everything the unified rebuild needs to re-author this display row from DB state alone:
        // the family donor's sound group, its ranged projectile SpellVisual, and whether the row
        // mirrors ModelName2 (thrown weapons) — so a later rebuild never has to re-resolve donors.
        string dbcFieldsJson = JsonSerializer.Serialize(new
        {
            groupSoundIndex = groupSound,
            spellVisualId,
            mirrorModelName2,
        });
        string gameplayJson = JsonSerializer.Serialize(new
        {
            name = weaponName,
            sourceKind = request.SourceKind,
            weaponType = profile.Key,
            weaponTypeLabel = profile.Label,
            inventoryType = effectiveInventoryType,
            inventoryTypeLabel = InventoryTypeLabel(effectiveInventoryType),
            donorModel = donor.ModelName,
        });

        await using var conn = _db.Admin();
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();

        await conn.ExecuteAsync(
            @"INSERT INTO custom_weapon_model
                (model_id, model_mpq_path, compiled_m2, m2_sha256, source_kind, source_blob, source_sha256,
                 generator_params_json, writer_version, coordinate_contract_version, validation_state)
              VALUES (@model_id, @path, @m2, @m2sha, @kind, @src, @srcsha, @params, @writer, @ccv, 'built')
              ON DUPLICATE KEY UPDATE
                compiled_m2 = VALUES(compiled_m2), m2_sha256 = VALUES(m2_sha256),
                source_blob = VALUES(source_blob), source_sha256 = VALUES(source_sha256),
                generator_params_json = VALUES(generator_params_json), validation_state = VALUES(validation_state)",
            new
            {
                model_id = display,
                path = WeaponNaming.ModelMpqPath(modelIndex, profile.ComponentDir),
                m2,
                m2sha = Sha256(m2),
                kind = request.SourceKind,
                src = request.SourceBlob,
                srcsha = sourceSha,
                @params = request.GeneratorParamsJson,
                writer = request.WriterVersion,
                ccv = CoordinateContract.Version,
            }, transaction: transaction);

        await conn.ExecuteAsync(
            @"INSERT INTO custom_weapon_display
                (display_id, model_id, texture_mpq_path, compiled_blp, blp_sha256, source_texture,
                 icon_stem, item_visual, donor_display_id, dbc_fields_json, validation_state)
              VALUES (@display, @model_id, @tex, @blp, @blpsha, @srctex, @icon, @itemVisual, @donor, @fields, 'built')
              ON DUPLICATE KEY UPDATE
                compiled_blp = VALUES(compiled_blp), blp_sha256 = VALUES(blp_sha256),
                source_texture = VALUES(source_texture), icon_stem = VALUES(icon_stem), item_visual = VALUES(item_visual),
                dbc_fields_json = VALUES(dbc_fields_json), validation_state = VALUES(validation_state)",
            new
            {
                display,
                itemVisual = (int)request.ItemVisual,
                model_id = display,
                tex = WeaponNaming.TextureMpqPath(modelIndex, 1, profile.ComponentDir),
                blp,
                blpsha = Sha256(blp),
                srctex = request.TexturePng,
                icon = iconStem,
                donor = donor.DisplayRow,
                fields = dbcFieldsJson,
            }, transaction: transaction);

        await conn.ExecuteAsync(
            @"INSERT INTO custom_weapon_item_manifest
                (build_id, item_entry, display_id, gameplay_json, sql_text, sql_sha256)
              VALUES (@build, @entry, @display, @gameplay, @sql, @sqlsha)
              ON DUPLICATE KEY UPDATE sql_sha256 = VALUES(sql_sha256), gameplay_json = VALUES(gameplay_json)",
            new { build = buildId, entry, display, gameplay = gameplayJson, sql = sql.Text, sqlsha = sql.Sha256 },
            transaction: transaction);

        // Effect textures (multi-pass glow): replace the model's set wholesale — the emitted M2's
        // hardcoded filenames and these rows must stay in lockstep.
        await conn.ExecuteAsync("DELETE FROM custom_weapon_model_texture WHERE model_id = @model_id",
            new { model_id = display }, transaction: transaction);
        for (int i = 0; i < effectBlps.Count; i++)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO custom_weapon_model_texture (model_id, slot, mpq_path, compiled_blp, blp_sha256)
                  VALUES (@model_id, @slot, @path, @blp, @sha)",
                new
                {
                    model_id = display,
                    slot = i + 1,
                    path = effectBlps[i].MpqPath,
                    blp = effectBlps[i].Blp,
                    sha = Sha256(effectBlps[i].Blp),
                }, transaction: transaction);
        }

        await transaction.CommitAsync();
    }

    private sealed class PackagedWeaponRow
    {
        public ulong DisplayId { get; set; }
        public ulong ModelId { get; set; }
        public string TextureMpqPath { get; set; } = "";
        public byte[]? Blp { get; set; }
        public string? IconStem { get; set; }
        public int ItemVisual { get; set; }
        public string? DbcFieldsJson { get; set; }
        /// <summary>Null when the LEFT join found no custom_weapon_model row for this display —
        /// the weapon is unpackageable and is reported as skipped rather than silently dropped.</summary>
        public string? ModelMpqPath { get; set; }
        public byte[]? M2 { get; set; }

        public uint? GroupSoundIndex => ReadUInt("groupSoundIndex");

        /// <summary>Ranged projectile SpellVisual persisted at build time; null on rows built
        /// before ranged families existed (packaged as 0 — the melee value).</summary>
        public uint? SpellVisualId => ReadUInt("spellVisualId");

        /// <summary>ModelName2 mirrors ModelName1 (thrown weapons); absent/false on older rows.</summary>
        public bool MirrorModelName2
        {
            get
            {
                if (string.IsNullOrEmpty(DbcFieldsJson)) return false;
                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(DbcFieldsJson);
                    return doc.TryGetProperty("mirrorModelName2", out var m) && m.ValueKind == JsonValueKind.True;
                }
                catch { return false; }
            }
        }

        private uint? ReadUInt(string property)
        {
            if (string.IsNullOrEmpty(DbcFieldsJson)) return null;
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(DbcFieldsJson);
                return doc.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetUInt32() : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Effect-texture MPQ members (multi-pass glow) for the given packaged models.</summary>
    private async Task<List<MpqMember>> LoadEffectTextureMembersAsync(HashSet<long> modelIds)
    {
        if (modelIds.Count == 0) return new List<MpqMember>();
        await using var conn = _db.Admin();
        await conn.OpenAsync();
        var rows = await conn.QueryAsync(
            @"SELECT model_id AS ModelId, mpq_path AS MpqPath, compiled_blp AS Blp
              FROM custom_weapon_model_texture
              WHERE compiled_blp IS NOT NULL
              ORDER BY model_id, slot");
        var members = new List<MpqMember>();
        foreach (var r in rows)
        {
            if (!modelIds.Contains(Convert.ToInt64(r.ModelId))) continue;
            members.Add(new MpqMember { MpqPath = (string)r.MpqPath, Data = (byte[])r.Blp });
        }
        return members;
    }

    private async Task<List<PackagedWeaponRow>> LoadPackagedWeaponsAsync()
    {
        await using var conn = _db.Admin();
        await conn.OpenAsync();
        var rows = await conn.QueryAsync<PackagedWeaponRow>(
            @"SELECT d.display_id      AS DisplayId,
                     d.model_id        AS ModelId,
                     d.texture_mpq_path AS TextureMpqPath,
                     d.compiled_blp    AS Blp,
                     d.icon_stem       AS IconStem,
                     d.item_visual     AS ItemVisual,
                     d.dbc_fields_json AS DbcFieldsJson,
                     m.model_mpq_path  AS ModelMpqPath,
                     m.compiled_m2     AS M2
              FROM custom_weapon_display d
              -- LEFT, not INNER: an INNER join made a display whose custom_weapon_model row is
              -- missing vanish from the rebuild entirely — no member, no DBC row, and (because it
              -- never reached the null-bytes check) no diagnostic either. It is now surfaced as a
              -- skipped weapon like any other missing-bytes case.
              LEFT JOIN custom_weapon_model m ON m.model_id = d.model_id
              ORDER BY d.display_id");
        return rows.ToList();
    }

    // ═══════════════════════════════════════════════════════════════════
    // OUTPUTS
    // ═══════════════════════════════════════════════════════════════════

    private object BuildManifest(CustomWeaponBuildRequest request, WeaponTypeProfile profile,
        WeaponDonorInfo donorInfo, string buildId, long entry, long display,
        int modelIndex, string weaponName, int effectiveInventoryType, uint displayGroupSound,
        uint displaySpellVisual, bool displayMirrorModelName2, GeneratedSql sql, WeaponPatchResult patch,
        int packagedCount, int skippedCount, int replacedInBase) => new
    {
        buildId,
        createdAtUtc = DateTime.UtcNow.ToString("O"),
        sourceKind = request.SourceKind,
        weaponType = new
        {
            key = profile.Key,
            label = profile.Label,
            subclass = profile.Subclass,
            familyInventoryType = profile.InventoryType,
            inventoryType = effectiveInventoryType,
            inventoryTypeLabel = InventoryTypeLabel(effectiveInventoryType),
            sheath = profile.Sheath,
            material = profile.Material,
            delayMs = profile.DelayMs,
            isRanged = profile.IsRanged,
            ammoType = profile.AmmoType,
            rangeMod = profile.RangeMod,
        },
        itemEntry = entry,
        displayId = display,
        modelIndex,
        name = weaponName,
        names = new
        {
            model = WeaponNaming.DbcModelName(modelIndex),
            modelMember = WeaponNaming.ModelMpqPath(modelIndex, profile.ComponentDir),
            texture = WeaponNaming.DbcTextureName(modelIndex),
            textureMember = WeaponNaming.TextureMpqPath(modelIndex, 1, profile.ComponentDir),
            dbcMember = WeaponNaming.ItemDisplayInfoMember,
        },
        versions = new
        {
            coordinateContract = CoordinateContract.Version,
            writer = request.WriterVersion,
            generator = request.SourceKind,
        },
        donor = new
        {
            displayRow = donorInfo.DisplayRow,
            model = donorInfo.ModelName,
            m2Path = donorInfo.M2Path,
            groupSoundIndex = donorInfo.GroupSoundIndex,
            spellVisualId = donorInfo.SpellVisualId,
            mirrorModelName2 = donorInfo.MirrorModelName2,
            measureDisplayRow = donorInfo.MeasureDisplayRow,
            measureModel = donorInfo.MeasureModelName,
            extentX = donorInfo.ExtentX,
            palmBackFraction = donorInfo.PalmBackFraction,
            orientation = donorInfo.Orientation.ToString(),
        },
        displayFields = new
        {
            groupSoundIndex = displayGroupSound,
            spellVisualId = displaySpellVisual,
            mirrorModelName2 = displayMirrorModelName2,
            sourcePreserved = request.DisplayGroupSoundIndex.HasValue ||
                              request.DisplaySpellVisualId.HasValue ||
                              request.DisplayMirrorModelName2.HasValue,
        },
        packaging = new
        {
            patchFileName = PatchFileName,
            weaponsPackaged = packagedCount,
            weaponsSkipped = skippedCount,
            baseRowsReplaced = replacedInBase,
            note = $"{PatchFileName} is the single unified weapon patch: it contains EVERY custom weapon recorded in the " +
                   "database, and its DBC also carries the Retexture Engine's patch-4 rows (it is built on the state beneath patch-5). " +
                   "Install it ALONGSIDE patch-4, never instead of it.",
        },
        sql = new { sha256 = sql.Sha256 },
        dbc = new { sha256 = patch.DbcSha256, sizeBytes = patch.DbcBytes.Length },
        mpq = new { sha256 = patch.MpqSha256, sizeBytes = patch.MpqBytes.Length },
        members = patch.Members,
    };

    private string WriteOutputs(string buildId, long entry, long display, int modelIndex,
        WeaponPatchResult patch, GeneratedSql sql, object manifest, ForgeDiagnostics diag, string componentDir)
    {
        string buildDir = Path.Combine(ArtifactRoot, $"weapon-build-{buildId}");
        Directory.CreateDirectory(buildDir);

        File.WriteAllBytes(Path.Combine(buildDir, PatchFileName), patch.MpqBytes);
        File.WriteAllText(Path.Combine(buildDir, "item_template.sql"), sql.Text, new UTF8Encoding(false));

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(buildDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, jsonOpts), new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(buildDir, "validation-report.md"),
            RenderValidationMarkdown(buildId, entry, display, modelIndex, patch, diag, componentDir), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(buildDir, "OWNER_CHECKLIST.md"),
            RenderOwnerChecklist(buildId, entry, display, patch, sql), new UTF8Encoding(false));

        WriteCanonicalPatch(patch.MpqBytes);
        return buildDir;
    }

    /// <summary>The newest unified patch is also the canonical one — a stable copy at the artifact
    /// root. Any pre-rename patch-4 artifact left by the Forge is removed so only one Forge patch
    /// file can ever be picked up from here.</summary>
    private void WriteCanonicalPatch(byte[] mpqBytes)
    {
        Directory.CreateDirectory(ArtifactRoot);
        File.WriteAllBytes(Path.Combine(ArtifactRoot, PatchFileName), mpqBytes);
        try
        {
            string legacy = Path.Combine(ArtifactRoot, LegacyPatchFileName);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch { /* best-effort cleanup */ }
    }

    private void RemoveCanonicalPatches()
    {
        foreach (var f in new[] { PatchFileName, LegacyPatchFileName })
        {
            try
            {
                string path = Path.Combine(ArtifactRoot, f);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    private static string RenderValidationMarkdown(string buildId, long entry, long display, int modelIndex,
        WeaponPatchResult patch, ForgeDiagnostics diag, string? componentDir = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Validation report — build {buildId}");
        sb.AppendLine();
        sb.AppendLine($"- Item entry: **{entry}**");
        sb.AppendLine($"- Display id: **{display}**");
        sb.AppendLine($"- Model: `{WeaponNaming.DbcModelName(modelIndex)}` → `{WeaponNaming.ModelMpqPath(modelIndex, componentDir)}`");
        sb.AppendLine($"- Texture: `{WeaponNaming.DbcTextureName(modelIndex)}` → `{WeaponNaming.TextureMpqPath(modelIndex, 1, componentDir)}`");
        sb.AppendLine($"- MPQ SHA-256: `{patch.MpqSha256}`");
        sb.AppendLine($"- DBC SHA-256: `{patch.DbcSha256}`");
        sb.AppendLine($"- All members byte-verified after repack: **{(patch.AllVerified ? "yes" : "NO")}**");
        sb.AppendLine();
        sb.AppendLine("## Packaged members (ALL custom weapons)");
        sb.AppendLine();
        sb.AppendLine("| path | size | sha256 | verified |");
        sb.AppendLine("|---|---:|---|:--:|");
        foreach (var m in patch.Members)
            sb.AppendLine($"| `{m.MpqPath}` | {m.Size} | `{m.Sha256[..12]}…` | {(m.Verified ? "✓" : "✗")} |");
        if (diag.Items.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Diagnostics");
            sb.AppendLine();
            foreach (var line in diag.Items) sb.AppendLine($"- {line}");
        }
        return sb.ToString();
    }

    private static string RenderOwnerChecklist(string buildId, long entry, long display,
        WeaponPatchResult patch, GeneratedSql sql)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# VERIFY — weapon build {buildId}");
        sb.AppendLine();
        sb.AppendLine("The Forge already applied this build: item_template row inserted (fail-closed), `.reload");
        sb.AppendLine($"item_template` issued, and `{PatchFileName}` deployed to the client Data folder — per-step");
        sb.AppendLine("results are in the build result and the Activity Log. If a step failed, this folder has the");
        sb.AppendLine("files to do it by hand (`item_template.sql`, the patch).");
        sb.AppendLine();
        sb.AppendLine($"- Item entry: **{entry}**   Display id: **{display}**");
        sb.AppendLine($"- MPQ SHA-256: `{patch.MpqSha256}`   SQL SHA-256: `{sql.Sha256}`");
        sb.AppendLine($"- `{PatchFileName}` contains EVERY forged weapon and sits ABOVE the Retexture Engine's");
        sb.AppendLine("  patch-4.MPQ — the two install side by side; never overwrite patch-4 with this file.");
        sb.AppendLine();
        sb.AppendLine("1. Fully close the Blizzard client and MSUIClient (a warm client caches DBC/model lookups).");
        sb.AppendLine("   If this entry was ever seen with different metadata, remove WDB\\itemcache.wdb (or the whole WDB cache directory) before relaunching; WoW recreates it.");
        sb.AppendLine($"2. On a GM: `.additem {entry}` and verify query/name/icon.");
        sb.AppendLine("3. Check main hand, offhand (where allowed), held and sheathed states, and dressing view.");
        sb.AppendLine("4. Assign the same display via an NPC virtual weapon and verify that path.");
        sb.AppendLine("5. Record screenshots/logs, client build, pass/fail, and any pivot/culling/texture discrepancy.");
        return sb.ToString();
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Target BLP dimensions for a source PNG: each axis snapped to the largest power of two
    /// ≤ the source and ≤ 256, preserving aspect. A 256×128 zone atlas stays 256×128; a 512×512
    /// reconstruction texture becomes 256×256. Falls back to 256×256 if the PNG can't be read.</summary>
    private static (int W, int H) TargetBlpSize(byte[] png)
    {
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(png));
            if (codec is not null)
                return (SnapPow2(codec.Info.Width), SnapPow2(codec.Info.Height));
        }
        catch { /* fall through */ }
        return (256, 256);
    }

    private static int SnapPow2(int v)
    {
        v = Math.Clamp(v, 8, 256);
        int p = 8;
        while (p * 2 <= v) p *= 2;
        return p;
    }
}

/// <summary>One custom weapon to build and package. Exactly one of <see cref="Mesh"/> (compiled
/// through the Forge writer) or <see cref="PrecompiledM2"/> (donor-clone bytes) must be set.</summary>
public sealed record PrecompiledWeaponEffectTexture(
    IReadOnlyList<int> TextureSlots,
    string SourcePath,
    byte[] Blp);

public sealed class CustomWeaponBuildRequest
{
    public string? Name { get; init; }

    /// <summary>The SOURCE item's own inventory icon stem (TBC/WotLK ItemDisplayInfo field 5), e.g.
    /// "INV_Weapon_Glaive_01". Null falls back to the family donor's icon, which is what every
    /// import used to do — a forged Warglaive shipped the donor sword's INV_Sword_04.</summary>
    public string? IconStem { get; init; }

    /// <summary>The icon's BLP bytes, supplied ONLY when the vanilla client does not already have
    /// an icon by that name (measured: 472 of the 585 icon names TBC weapons use already exist in
    /// 1.12, so this is null ~81% of the time). Packaged into the patch as
    /// <c>Interface\Icons\{IconStem}.blp</c> so the client can resolve the name.</summary>
    public byte[]? IconBlp { get; init; }

    /// <summary>'donor_patch' | 'parametric' | 'glb_import' | 'vanilla_recolor' | 'sketch3d' — recorded as provenance.</summary>
    public required string SourceKind { get; init; }

    /// <summary>Weapon family key from <see cref="WeaponTypeCatalog"/> — drives the gameplay row
    /// (subclass/inventory/sheath/material/delay), the donor scaffold the M2 writer builds on, and
    /// the display donor row (sound/icon). Unknown/empty falls back to the proven 1H sword.</summary>
    public string? WeaponTypeKey { get; init; }

    /// <summary>Item_template column overrides layered OVER the family defaults — e.g. the TBC
    /// import carries the source item's own sheath (the Warglaives are 1H swords with the
    /// two-hander back-sheath value 1 — that's the crossed-on-back look), inventory slot, and
    /// swing delay. Keys are validated against the fixture columns; fail-closed on unknowns.</summary>
    public Dictionary<string, string>? ItemOverrides { get; init; }

    public RigidWeaponMesh? Mesh { get; init; }
    public WeaponTopologyMode Topology { get; init; } = WeaponTopologyMode.Variable;

    /// <summary>Route-specific variable-topology ceiling. Arbitrary authored/imported meshes keep
    /// the Forge's conservative 1,000-triangle policy; the TBC fidelity route explicitly raises
    /// this to the vanilla skin section's UInt16 index capacity.</summary>
    public int VariableTriangleHardCeiling { get; init; } = 1000;

    /// <summary>Optional PNG master (e.g. the GLB's embedded texture). Encoded to a 256×256 DXT1 BLP;
    /// when absent the donor texture is packaged so the model still samples something valid.</summary>
    public byte[]? TexturePng { get; init; }

    /// <summary>Optional already-encoded BLP2 master. Used by TBC fidelity imports so texture
    /// dimensions, mipmaps, alpha and compressed blocks are packaged without a lossy round trip.</summary>
    public byte[]? TextureBlp { get; init; }

    /// <summary>Effect texture PNGs for the mesh's texture slots 1.. (multi-pass glow imports).
    /// Encoded and packaged as Type-0 members the emitted M2 references by hardcoded path.</summary>
    public IReadOnlyList<byte[]>? EffectTexturesPng { get; init; }

    /// <summary>Pre-encoded BLP2 sources parallel to texture slots 1..; preferred over PNG entries.</summary>
    public IReadOnlyList<byte[]>? EffectTexturesBlp { get; init; }

    public byte[]? PrecompiledM2 { get; init; }
    public byte[]? PrecompiledBlp { get; init; }

    /// <summary>Per-item replacements for selected hardcoded Type-0 texture records in a
    /// source-preserved M2. The builder assigns private MPQ paths after reserving the display id,
    /// repoints only those records, and packages these BLPs at the private paths.</summary>
    public IReadOnlyList<PrecompiledWeaponEffectTexture>? PrecompiledEffectTextures { get; init; }

    /// <summary>Source material for later edit/recompile (original GLB, sketch PNG, …).</summary>
    public byte[]? SourceBlob { get; init; }
    public string? GeneratorParamsJson { get; init; }
    public string? WriterVersion { get; init; }
    /// <summary>ItemDisplayInfo field 22: a vanilla ItemVisuals.dbc id (enchant-style glow the client
    /// renders permanently on the weapon's attachment points), 0 = none. Used by the later-client
    /// imports to approximate source particle effects the 1.12 scaffold cannot host.</summary>
    public uint ItemVisual { get; init; }

    /// <summary>Optional source ItemDisplayInfo scalars. Vanilla byte-preserving recolors carry
    /// these exactly; generated and later-client routes leave them null and retain family-donor
    /// behavior.</summary>
    public uint? DisplayGroupSoundIndex { get; init; }
    public uint? DisplaySpellVisualId { get; init; }
    public bool? DisplayMirrorModelName2 { get; init; }
    /// <summary>Attachment id → WoW-space position to write over the donor's attachment records after
    /// compilation (weapon ids 0..4 = where enchant/ItemVisual effects hang). Null keeps the donor's.</summary>
    public IReadOnlyDictionary<uint, System.Numerics.Vector3>? AttachmentPointsWoW { get; init; }

    /// <summary>Stock-1.12 particle emitters to graft onto the compiled model so a source effect that
    /// MOVED still moves — see <see cref="RawM2.M2EmitterTransplanter"/> and
    /// <see cref="Motion.EffectMotionPlanner"/>. Null/empty leaves the model as compiled.</summary>
    public IReadOnlyList<RawM2.M2EmitterTransplanter.Graft>? MotionGrafts { get; init; }

    /// <summary>Debug lever: keep the donor's internal M2 name instead of the canonical
    /// SUI_W_#### rename, to isolate the EOF name-append in the reference client.</summary>
    public bool KeepDonorInternalName { get; init; }
}

public sealed class CustomWeaponBuildResult
{
    public required string BuildId { get; init; }
    public required long ItemEntry { get; init; }
    public required long DisplayId { get; init; }
    public required int ModelIndex { get; init; }
    public required string Name { get; init; }
    public required string WeaponType { get; init; }
    public required string WeaponTypeLabel { get; init; }
    public required int InventoryType { get; init; }
    public required string InventoryTypeLabel { get; init; }
    public required string SourceKind { get; init; }
    public required string ModelMember { get; init; }
    public required string TextureMember { get; init; }
    public required string MpqSha256 { get; init; }
    public required string DbcSha256 { get; init; }
    public required string SqlSha256 { get; init; }
    public required bool AllMembersVerified { get; init; }
    public required int PackagedWeaponCount { get; init; }
    public required int SkippedWeaponCount { get; init; }
    public string? PreviewGlbWebPath { get; init; }
    public int TriangleCount { get; init; }
    public int VertexCount { get; init; }
    public required string BuildDirectory { get; init; }
    public required string BuildDirName { get; init; }
    public required ServerApplyStatus Apply { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
}

/// <summary>What the Forge applied live, step by step. A false is not an error — the message says
/// what to do manually (the files always also exist in the build folder).</summary>
public sealed class ServerApplyStatus
{
    public required bool SqlApplied { get; init; }
    public required string SqlMessage { get; init; }
    public required bool Reloaded { get; init; }
    public required string ReloadMessage { get; init; }
    public required bool PatchDeployed { get; init; }
    public required string PatchDeployMessage { get; init; }
    /// <summary>The client patch was not rebuilt; the change waits for the next Rebuild patch click.
    /// Rendered as "pending", not as a failed deploy.</summary>
    public bool PatchQueued { get; init; }
    /// <summary>Queue depth after this change, when <see cref="PatchQueued"/>.</summary>
    public int PatchPending { get; init; }
}

public sealed class WeaponPatchRebuildSummary
{
    public required int WeaponCount { get; init; }
    public required bool PatchRemoved { get; init; }
    public required string? MpqSha256 { get; init; }
    public required bool PatchDeployed { get; init; }
    public required string PatchDeployMessage { get; init; }
    public bool PatchQueued { get; init; }
    public int PatchPending { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
}

public sealed class ForgedWeaponInfo
{
    public required long DisplayId { get; init; }
    public required long ItemEntry { get; init; }
    public required string Name { get; init; }
    /// <summary>Family key recorded at forge time; null for weapons forged before types existed.</summary>
    public string? WeaponType { get; init; }
    /// <summary>Effective item_template inventory_type after request overrides. Recovered from TBC
    /// generator metadata or the family for older gameplay_json rows when possible.</summary>
    public int? InventoryType { get; init; }
    public string? InventoryTypeLabel { get; init; }
    public required string SourceKind { get; init; }
    public required string ModelMpqPath { get; init; }
    /// <summary>Stock ItemDisplayInfo row the display was cloned from (0 when unrecorded).</summary>
    public long DonorDisplayRow { get; init; }
    /// <summary>The forged weapon's own enchant glow (ItemDisplayInfo field 22). Carried so the
    /// startup re-registration can restore it into the DBC cache; inheriting the donor's left the
    /// Items page unable to resolve any forged weapon's glow.</summary>
    public uint ItemVisual { get; init; }
    public string? BuildId { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public sealed class WeaponBulkDeleteResult
{
    public required IReadOnlyList<ForgedWeaponInfo> Deleted { get; init; }
    public required IReadOnlyList<WeaponBulkDeleteFailure> Failed { get; init; }
    public required bool Reloaded { get; init; }
    public required string ReloadMessage { get; init; }
    public required WeaponPatchRebuildSummary Rebuild { get; init; }
}

public sealed class WeaponBulkDeleteFailure
{
    public required long DisplayId { get; init; }
    public required string Error { get; init; }
}

public sealed class WeaponDeleteResult
{
    public required ForgedWeaponInfo Deleted { get; init; }
    public required WeaponPatchRebuildSummary Rebuild { get; init; }
    public required bool ItemRowDeleted { get; init; }
    public required string ItemRowMessage { get; init; }
    public required bool Reloaded { get; init; }
    public required string ReloadMessage { get; init; }
}
