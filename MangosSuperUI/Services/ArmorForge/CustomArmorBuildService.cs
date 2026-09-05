using System.Numerics;
using System.Text.Json;
using Dapper;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using MangosSuperUI.Services.WeaponForge;
using SkiaSharp;

namespace MangosSuperUI.Services.ArmorForge;

/// <summary>
/// The ONE packaging path for forged armor (ARMOR_FORGE.md §2) — the armor-side sibling of
/// <c>CustomWeaponBuildService</c>. Sources come from <see cref="LegacyArmorImporter"/> (a TBC piece, or
/// a whole TBC set). For each piece: reserve ids → resolve/emit the source (components, skin,
/// 16 helm variants / L+R shoulder) → persist to <c>custom_armor_*</c> → rebuild the unified
/// <c>patch-6.MPQ</c> from DB → apply live (world <c>item_template</c> INSERT via the weapon forge's
/// donor-clone SQL, <c>.reload item_template</c>, patch deploy) → audit.
///
/// Everything ships in the single unified patch-4.MPQ (see UnifiedPatchService); this lane keeps its
/// own patch-6 artifact for build output and the server-side ItemSet.dbc, but the client only ever
/// receives the unified archive. Imports, clones and deletes QUEUE a unified rebuild; the operator
/// ships the whole batch with one Rebuild patch click.
///
/// Sets: a TBC set is imported as a unit — one <c>custom_armor_set</c> row (TBC name, NO bonuses;
/// vanilla bonuses are the operator's own business, editable afterwards) and every member stamped
/// with <c>set_id</c> (item_template.set_id too). ItemSet.dbc is emitted for the client tooltip.
/// </summary>
public sealed class CustomArmorBuildService : ICustomMpqMemberSource
{
    public const string PatchFileName = "patch-6.MPQ";

    private readonly MpqReaderService _mpq;
    private readonly WeaponIdReservationService _ids;
    private readonly ArmorPatchBuilder _patch;
    private readonly ArmorImportSources _lanes;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;
    private readonly RaService _ra;
    private readonly ConnectionFactory _db;
    private readonly PaletteSwapService _palette;
    private readonly BlpWriterService _blp;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    /// <summary>For reaching UnifiedPatchService, which is SCOPED while this service is a singleton
    /// — it has to be resolved through a scope rather than injected.</summary>
    private readonly IServiceProvider _services;
    private readonly ILogger<CustomArmorBuildService> _logger;

    private const string KindSet = WeaponIdReservationService.KindArmorSet;

    public CustomArmorBuildService(MpqReaderService mpq, WeaponIdReservationService ids,
        ArmorPatchBuilder patch, ArmorImportSources lanes, DbcService dbc,
        AuditService audit, RaService ra, ConnectionFactory db, PaletteSwapService palette,
        BlpWriterService blp, IWebHostEnvironment env,
        IConfiguration config, IServiceProvider services, ILogger<CustomArmorBuildService> logger)
    {
        _mpq = mpq; _ids = ids;
        _mpq.RegisterCustomMemberSource(this); _patch = patch; _lanes = lanes; _dbc = dbc;
        _audit = audit; _ra = ra; _db = db; _palette = palette; _blp = blp; _env = env; _config = config;
        _services = services; _logger = logger;
    }

    private string? ClientDataPath
    {
        get
        {
            var p = _config["Vmangos:ClientDataPath"] ?? _config["SpellCreator:ClientDataPath"];
            return !string.IsNullOrEmpty(p) && Directory.Exists(p) ? p : null;
        }
    }

    /// <summary>
    /// The server's own dbc directory — where mangosd loads ItemSet.dbc at startup.
    ///
    /// This is REQUIRED for ANY forged set, not just for bonuses (the old comment here claimed the
    /// opposite and is why the key was never set on the live box). <c>ObjectMgr::LoadItemPrototypes</c>
    /// validates <c>item_template.set_id</c> against <c>sItemSetStore</c> unconditionally and zeroes the
    /// column in memory when the id is missing — so without this deploy a forged set loses its bonuses
    /// AND its tooltip set block AND its membership, and DBErrors.log fills with "has wrong ItemSet".
    ///
    /// The core builds this path as DataDir + SUPPORTED_CLIENT_BUILD (a compile-time 5875) + "/dbc/",
    /// which is exactly what <c>Vmangos:DbcPath</c> already points at — so that is the fallback rather
    /// than making the operator hand-add a key the Settings page cannot even write.
    /// </summary>
    private (string? Dir, string Detail) ResolveServerDbcDir()
    {
        var tried = new List<string>();
        foreach (var (key, value) in new (string Key, string? Value)[]
        {
            ("ArmorForge:ServerDbcPath", _config["ArmorForge:ServerDbcPath"]),
            ("Vmangos:DbcPath", _config["Vmangos:DbcPath"]),
            ("Vmangos:ServerDataPath", _config["Vmangos:ServerDataPath"] is { Length: > 0 } dataPath
                ? Path.Combine(dataPath, ServerClientBuild, "dbc") : null),
        })
        {
            if (string.IsNullOrWhiteSpace(value)) { tried.Add($"{key} unset"); continue; }
            if (Directory.Exists(value)) return (value, $"{key} = {value}");
            tried.Add($"{key} = {value} (directory not found)");
        }
        return (null, string.Join("; ", tried));
    }

    /// <summary>The client build whose dbc subdirectory the core reads (VMaNGOS SUPPORTED_CLIENT_BUILD).</summary>
    private const string ServerClientBuild = "5875";

    private string? ServerDbcPath => ResolveServerDbcDir().Dir;

    public string ArtifactRoot =>
        _config["ArmorForge:ArtifactRoot"] is { Length: > 0 } cfg ? cfg : Path.Combine(_env.WebRootPath, "armor_forge_builds");

    // ═══════════════════════════════════════════════════════════════════
    // IMPORT ONE PIECE (TBC or WotLK lane)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Import one TBC armor piece (the original lane). See <see cref="ImportAsync"/>.</summary>
    public Task<CustomArmorBuildResult> ImportTbcAsync(uint entry, string? nameOverride = null,
        int vanillaSetId = 0, bool rebuild = true) =>
        ImportAsync(_lanes.Tbc, entry, nameOverride, vanillaSetId, rebuild);

    /// <summary>Import one WotLK armor piece. See <see cref="ImportAsync"/>.</summary>
    public Task<CustomArmorBuildResult> ImportWotlkAsync(uint entry, string? nameOverride = null,
        int vanillaSetId = 0, bool rebuild = true) =>
        ImportAsync(_lanes.Wotlk, entry, nameOverride, vanillaSetId, rebuild);

    /// <summary>Lane-keyed form ("tbc" / "wotlk").</summary>
    public Task<CustomArmorBuildResult> ImportAsync(string expansion, uint entry, string? nameOverride = null,
        int vanillaSetId = 0, bool rebuild = true, ValidatedVanillaItemBuildConfiguration? gameplay = null,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "fan", string recolorTier = "improved", Vector3? glowColor = null, float glowIntensity = 1f) =>
        ImportAsync(_lanes.Get(expansion), entry, nameOverride, vanillaSetId, rebuild, gameplay, recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor, glowIntensity);

    /// <summary>Import one later-client armor piece. <paramref name="vanillaSetId"/> is OUR set id
    /// (0 = none); <paramref name="rebuild"/> false lets a set import batch pieces and rebuild once at
    /// the end. <paramref name="gameplay"/> is the optional validated user gameplay contract (value,
    /// stats, spell effects, resistances, requirements) layered over the armor identity row — the
    /// armor-side equivalent of the weapon forge's itemConfig. Everything past resolution is
    /// lane-agnostic: the same ids, SQL, persistence and patch.</summary>
    public async Task<CustomArmorBuildResult> ImportAsync(ArmorImportLane lane, uint entry, string? nameOverride = null,
        int vanillaSetId = 0, bool rebuild = true, ValidatedVanillaItemBuildConfiguration? gameplay = null,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "fan", string recolorTier = "improved", Vector3? glowColor = null, float glowIntensity = 1f)
    {
        var trace = new ArmorAttemptTrace();
        try
        {
            return await ImportCoreAsync(lane, entry, trace, nameOverride, vanillaSetId, rebuild, gameplay,
                recolorHue, recolorSat, recolorLight, recolorTheory, recolorTier, glowColor, glowIntensity);
        }
        catch (Exception ex)
        {
            // An import that throws past the reservation leaves ids — and possibly registry rows and
            // a world row — behind. The success row is written last, so without this the trail shows
            // nothing at all for the attempt.
            await LogImportFailureAsync(lane, entry, trace, ex.Message);
            throw;
        }
    }

    /// <summary>What an import had done by the time it failed — see the weapon forge's equivalent.
    /// Read by the failure audit row so orphaned ids are findable.</summary>
    private sealed class ArmorAttemptTrace
    {
        public string? BuildId;
        public long ItemEntry;
        public long DisplayId;
        public string? Name;
        public string Stage = "start";
    }

    /// <summary>The failure twin of the import row at the end of <see cref="ImportCoreAsync"/>. Import
    /// fails two ways — a graceful Ok=false return (resolve/persist) and a throw — and both used to be
    /// silent.</summary>
    private Task LogImportFailureAsync(ArmorImportLane lane, uint entry, ArmorAttemptTrace trace, string message) =>
        _audit.LogAsync(new AuditEntry
        {
            Category = "armorforge",
            Action = $"import_{lane.Key}",
            TargetType = "item",
            TargetName = trace.Name,
            TargetId = trace.ItemEntry is > 0 and <= int.MaxValue ? (int)trace.ItemEntry : null,
            StateAfter = JsonSerializer.Serialize(new
            {
                buildId = trace.BuildId,
                failedAtStage = trace.Stage,
                sourceEntry = entry,
                sourceExpansion = lane.Key,
                itemEntry = trace.ItemEntry,
                displayId = trace.DisplayId,
            }),
            IsReversible = false,
            RevertKind = RevertKind.None,
            Success = false,
            Notes = $"{lane.Label} import of source {entry} FAILED at stage '{trace.Stage}': {message}" +
                    (trace.ItemEntry > 0
                        ? $" Ids {trace.ItemEntry}/{trace.DisplayId} were reserved — released on the graceful paths, possibly orphaned on a throw."
                        : " No ids were reserved."),
        });

    private async Task<CustomArmorBuildResult> ImportCoreAsync(ArmorImportLane lane, uint entry, ArmorAttemptTrace trace,
        string? nameOverride = null,
        int vanillaSetId = 0, bool rebuild = true, ValidatedVanillaItemBuildConfiguration? gameplay = null,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "fan", string recolorTier = "improved", Vector3? glowColor = null, float glowIntensity = 1f)
    {
        if (!DonorItemTemplateFixture.Verify())
            throw new InvalidOperationException("Donor item_template fixture failed hash verification.");

        var diag = new ForgeDiagnostics("armor-import-" + lane.Key);
        var result = new CustomArmorBuildResult { TbcEntry = entry, SourceExpansion = lane.Key };

        // 1) Base DBC beneath patch-6 — id floor.
        byte[] baseDbc = ResolveBaseDbc();
        uint dbcMax = DbcWriterService.ReadDbc(baseDbc, ArmorNaming.ItemDisplayInfoMember).GetMaxId();

        // 2) Reserve ids (the display id doubles as the SUI_A model index).
        string buildId = "arm-" + Guid.NewGuid().ToString("N")[..12];
        long entryFloor = await _ids.ComputeItemEntryFloorAsync();
        long displayFloor = await _ids.ComputeDisplayIdFloorAsync(dbcMax);
        trace.BuildId = buildId;
        trace.Stage = "reserve";
        var entryRes = await _ids.ReserveAsync(WeaponIdReservationService.KindItemEntry, entryFloor, buildId, "item");
        var dispRes = await _ids.ReserveAsync(WeaponIdReservationService.KindItemDisplay, displayFloor, buildId, "display");
        int displayIndex = checked((int)dispRes.Id);
        trace.ItemEntry = entryRes.Id;
        trace.DisplayId = dispRes.Id;
        trace.Stage = "resolve";

        // 3) Resolve + emit the source. Any failure before persistence releases the reserved ids.
        ArmorImportSource? src;
        try
        {
            src = lane.Importer.Resolve(entry, displayIndex, diag, glowColor, glowIntensity);
        }
        catch (Exception ex)
        {
            diag.Error("import.resolve", ex.Message);
            src = null;
        }
        if (src is null || diag.HasErrors)
        {
            await _ids.ReleaseAsync(WeaponIdReservationService.KindItemDisplay, dispRes.Id);
            await _ids.ReleaseAsync(WeaponIdReservationService.KindItemEntry, entryRes.Id);
            result.Ok = false;
            result.Diagnostics = diag.Items.Select(i => i.ToString()).ToArray();
            result.Message = string.Join("; ", diag.Items.Where(i => i.Severity == ForgeSeverity.Error).Select(i => i.Message));
            await LogImportFailureAsync(lane, entry, trace, result.Message);
            return result;
        }

        // Bake the chosen recolor into the source textures before persist/package, seeded off the SOURCE
        // display id so the shipped item matches what the operator previewed.
        if (recolorHue.HasValue && (src.Components.Count > 0 || src.TextureBlp is { Length: > 0 }))
        {
            int rseed = RetextureSupport.SeedFor((int)(lane.Catalog.FindEntry(entry)?.DisplayId ?? entry), recolorTier);
            await RecolorSourceAsync(src, rseed, recolorHue.Value, recolorSat, recolorLight, recolorTheory, recolorTier, CancellationToken.None);
        }

        var profile = ArmorTypeCatalog.Get(src.FamilyKey);
        string name = !string.IsNullOrWhiteSpace(nameOverride) ? nameOverride!.Trim()
            : !string.IsNullOrWhiteSpace(gameplay?.Name) ? gameplay!.Name!.Trim()
            : src.Name;
        int armor = profile.DefaultArmor(src.Material);
        result.ItemEntry = entryRes.Id; result.DisplayId = dispRes.Id; result.Name = name;
        result.ArmorTypeKey = src.FamilyKey; result.RenderKind = src.RenderKind;

        // 4) item_template row: armor identity + the source item's own quality/level, set link.
        var overrides = profile.ItemTemplateOverrides(src.Material, armor, vanillaSetId);
        overrides["quality"] = Math.Clamp(src.Quality, 0, 6).ToString();
        overrides["item_level"] = Math.Clamp(src.ItemLevel, 1, 255).ToString();
        overrides["required_level"] = Math.Clamp(src.RequiredLevel, 0, 60).ToString();
        // Layer the user gameplay contract over the armor identity + source levels: value, stats,
        // spell effects, resistances, durability, requirements (and its own quality/item_level when
        // the tier picker set them). Identity columns the modal never exposes (class/subclass/
        // inventory_type/material) are absent from the contract, so the forge's armor defaults stand.
        if (gameplay is { Overrides.Count: > 0 })
            foreach (var (column, value) in gameplay.Overrides)
                overrides[column] = value;
        var sql = WeaponItemTemplateSql.Build(entryRes.Id, name, dispRes.Id, buildId, overrides);
        result.Sql = sql.Text;

        // 5) Persist (fail-closed — without this the piece can't be repackaged). A persistence
        //    failure releases the ids too; nothing else has been written yet.
        trace.Name = name;
        trace.Stage = "persist";
        try
        {
        await PersistAsync(new ArmorPersistRow
        {
            DisplayId = dispRes.Id, ItemEntry = entryRes.Id, BuildId = buildId, SetId = vanillaSetId,
            RenderKind = src.RenderKind.ToString(), ArmorTypeKey = src.FamilyKey, Material = (int)src.Material,
            InventoryType = profile.InventoryType, Name = name, IconStem = src.IconStem,
            ModelName = src.ModelName, ModelName2 = src.ModelName2, TextureName = src.TextureName,
            TextureMpqPath = src.TextureMpqPath, ModelTextureBlp = src.TextureBlp,
            Geoset0 = src.GeosetGroup[0], Geoset1 = src.GeosetGroup[1], Geoset2 = src.GeosetGroup[2],
            HelmetVis0 = src.HelmetVis0, HelmetVis1 = src.HelmetVis1, GroupSound = src.GroupSoundIndex,
            SqlText = sql.Text,
            GameplayJson = JsonSerializer.Serialize(new { name, src.FamilyKey, renderKind = src.RenderKind.ToString(), material = src.Material.ToString(), armor, tbcEntry = entry, tbcSet = src.SetId, sourceExpansion = lane.Key, sourceExpansionLabel = lane.Label, gameplay = gameplay?.Overrides }),
        }, src.Components, src.ModelMembers);
        }
        catch (Exception ex)
        {
            await _ids.ReleaseAsync(WeaponIdReservationService.KindItemDisplay, dispRes.Id);
            await _ids.ReleaseAsync(WeaponIdReservationService.KindItemEntry, entryRes.Id);
            result.Ok = false;
            result.Message = "persist failed: " + ex.Message;
            result.Diagnostics = diag.Items.Select(i => i.ToString()).ToArray();
            await LogImportFailureAsync(lane, entry, trace, result.Message);
            return result;
        }

        await _ids.MarkStateAsync(WeaponIdReservationService.KindItemEntry, entryRes.Id, "committed");
        await _ids.MarkStateAsync(WeaponIdReservationService.KindItemDisplay, dispRes.Id, "committed");

        // 6) In-memory DBC registration (web preview). Clone from any stock row of the same slot
        //    and override names; the M2/BLP bytes are read from the live patch-6 once deployed.
        RegisterDisplayWithDbc(dispRes.Id, profile, src);

        // 7) Apply: world SQL + reload always; patch rebuild/deploy unless batched by a set import.
        trace.Stage = "apply";
        var apply = new ServerApplyStatus();
        var sqlRes = await ApplyItemSqlAsync(sql, entryRes.Id);
        apply.SqlApplied = sqlRes.Ok; apply.SqlMessage = sqlRes.Message;
        if (sqlRes.Ok) { var r = await ReloadItemTemplateAsync(); apply.Reloaded = r.Ok; apply.ReloadMessage = r.Message; }
        if (rebuild)
        {
            var patch = await AssembleUnifiedPatchAsync();
            WriteOutputs(buildId, patch, sql.Text);
            // Client patch NOT rebuilt here — queued for the next Rebuild patch click. The server-side
            // ItemSet.dbc still goes out now: it is a different file on a different machine.
            var dep = QueueUnifiedRebuild($"imported '{name}' (entry {entryRes.Id})");
            apply.PatchDeployed = false; apply.PatchDeployMessage = dep.Message;
            apply.PatchQueued = dep.Queued; apply.PatchPending = dep.Pending;
            var setDep = DeployItemSetToServer(patch);
            apply.ServerItemSetState = setDep.State.ToString();
            apply.ServerItemSetMessage = setDep.Message;
        }
        result.Apply = apply;
        result.Ok = true;
        result.Diagnostics = diag.Items.Select(i => i.ToString()).ToArray();
        result.ModelMemberCount = src.ModelMembers.Count;
        result.ComponentCount = src.Components.Count;

        await _audit.LogAsync(new AuditEntry
        {
            Category = "armorforge", Action = $"import_{lane.Key}_" + src.RenderKind.ToString().ToLowerInvariant(),
            TargetType = "item", TargetName = name, TargetId = checked((int)entryRes.Id),
            RaCommand = apply.SqlApplied ? ".reload item_template" : null, RaResponse = apply.ReloadMessage,
            StateAfter = JsonSerializer.Serialize(new { buildId, itemEntry = entryRes.Id, displayId = dispRes.Id, src.FamilyKey, tbcEntry = entry, sourceExpansion = lane.Key, setId = vanillaSetId, models = src.ModelMembers.Count, components = src.Components.Count }),
            IsReversible = true, RevertKind = RevertKind.Registry,
            // The world-DB row is what makes the piece exist to the core — see the weapon forge's
            // matching note. Registry rows and the patch are durable either way.
            Success = apply.SqlApplied,
            Notes = $"{lane.Label} {entry} → {src.FamilyKey}. SQL: {apply.SqlMessage}. " + (rebuild ? $"Deploy: {apply.PatchDeployMessage}." : "(patch rebuilt by set import)"),
        });
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // IMPORT A WHOLE SET (TBC or WotLK lane)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Import every armor member of a TBC set (the original lane). See <see cref="ImportSetAsync"/>.</summary>
    public Task<ArmorSetImportResult> ImportTbcSetAsync(uint tbcSetId, IReadOnlyList<uint>? onlyEntries = null) =>
        ImportSetAsync(_lanes.Tbc, tbcSetId, onlyEntries);

    /// <summary>Import every armor member of a WotLK set. See <see cref="ImportSetAsync"/>.</summary>
    public Task<ArmorSetImportResult> ImportWotlkSetAsync(uint wotlkSetId, IReadOnlyList<uint>? onlyEntries = null) =>
        ImportSetAsync(_lanes.Wotlk, wotlkSetId, onlyEntries);

    /// <summary>Lane-keyed form ("tbc" / "wotlk").</summary>
    public Task<ArmorSetImportResult> ImportSetAsync(string expansion, uint sourceSetId, IReadOnlyList<uint>? onlyEntries = null) =>
        ImportSetAsync(_lanes.Get(expansion), sourceSetId, onlyEntries);

    /// <summary>Import every armor member of a later-client set as one visual unit: allocate OUR set id,
    /// create the set row, import each piece (with its per-piece gameplay config) stamped with the set id,
    /// write the operator's set bonuses, rebuild patch-6 once. <paramref name="perPieceGameplay"/> maps a
    /// SOURCE member entry to its validated gameplay contract (value/stats/effects), so a set is configured
    /// as a unit — the armor equivalent of "the whole nine yards" for every piece plus the set bonuses.</summary>
    public async Task<ArmorSetImportResult> ImportSetAsync(ArmorImportLane lane, uint tbcSetId,
        IReadOnlyList<uint>? onlyEntries = null,
        IReadOnlyDictionary<uint, ValidatedVanillaItemBuildConfiguration>? perPieceGameplay = null,
        IReadOnlyList<ArmorSetBonus>? bonuses = null, int reqSkill = 0, int reqSkillRank = 0, string? setNameOverride = null,
        float? recolorHue = null, float? recolorSat = null, float? recolorLight = null, string recolorTheory = "fan", string recolorTier = "improved", Vector3? glowColor = null, float glowIntensity = 1f)
    {
        var set = lane.Catalog.GetSet(tbcSetId)
            ?? throw new ArgumentException($"{lane.Label} set {tbcSetId} not found (is the {lane.Label} client mounted?).");
        var members = set.MemberEntries.Where(e => onlyEntries is null || onlyEntries.Contains(e)).ToList();
        if (members.Count == 0) throw new ArgumentException($"{lane.Label} set '{set.Name}' has no importable armor members.");

        string setName = string.IsNullOrWhiteSpace(setNameOverride) ? set.Name : setNameOverride.Trim();
        var validBonuses = (bonuses ?? new List<ArmorSetBonus>())
            .Where(b => b.Threshold > 0 && b.SpellId > 0).OrderBy(b => b.Threshold).Take(ArmorItemSetDbc.MaxBonuses).ToList();

        // A set import is N+1 audit rows — one per member from ImportAsync, plus the set row below.
        // Unbatched they arrived as N+1 unrelated events; the ambient scope groups them into one
        // named run in the Change Graph without any of the per-piece call sites knowing about it.
        using var batch = AuditBatch.Begin($"Armor Forge — import {lane.Label} set '{setName}'");

        string buildId = "armset-" + Guid.NewGuid().ToString("N")[..12];
        int setId = 0;
        try
        {
            long floor = await ComputeSetIdFloorAsync();
            var res = await _ids.ReserveAsync(KindSet, floor, buildId, "set");
            setId = checked((int)res.Id);
            await _ids.MarkStateAsync(KindSet, res.Id, "committed");

            await using (var conn = _db.Admin())
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    @"INSERT INTO custom_armor_set (set_id, name, bonuses_json, req_skill, req_skill_rank, created_at)
                      VALUES (@setId, @name, @bonuses, @skill, @rank, NOW())",
                    new { setId, name = setName, bonuses = JsonSerializer.Serialize(validBonuses), skill = reqSkill, rank = reqSkillRank });
            }

            var result = new ArmorSetImportResult { SetId = setId, Name = setName };
            foreach (var entry in members)
            {
                try
                {
                    ValidatedVanillaItemBuildConfiguration? pieceGameplay = null;
                    perPieceGameplay?.TryGetValue(entry, out pieceGameplay);
                    var piece = await ImportAsync(lane, entry, null, setId, rebuild: false, gameplay: pieceGameplay,
                        recolorHue: recolorHue, recolorSat: recolorSat, recolorLight: recolorLight, recolorTheory: recolorTheory, recolorTier: recolorTier, glowColor: glowColor, glowIntensity: glowIntensity);
                    result.Pieces.Add(piece);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ArmorForge: {Lane} set {Set} member {Entry} failed", lane.Key, tbcSetId, entry);
                    result.Pieces.Add(new CustomArmorBuildResult { TbcEntry = entry, SourceExpansion = lane.Key, Ok = false, Message = ex.Message });
                }
            }

            // One rebuild + deploy for the whole set.
            var patch = await AssembleUnifiedPatchAsync();
            WriteOutputs(buildId, patch, null);
            var dep = QueueUnifiedRebuild($"imported set {setId} '{result.Name}'");
            var serverDeploy = DeployItemSetToServer(patch);
            result.PatchDeployed = false;
            result.PatchQueued = dep.Queued;
            result.PatchPending = dep.Pending;
            result.ServerItemSetDeployed = serverDeploy.State == ItemSetDeployState.Deployed;
            result.ServerItemSetMessage = serverDeploy.Message;
            // The server DBC line is NOT conditional on bonuses: without it the core zeroes set_id and the
            // set loses its tooltip block and its membership, bonuses or not.
            result.Message = $"{result.Pieces.Count(p => p.Ok)}/{members.Count} pieces imported as set {setId} '{setName}'"
                + (validBonuses.Count > 0 ? $" with {validBonuses.Count} bonus(es)" : " (no bonuses)") + $". {dep.Message}"
                + $" Server sets: {serverDeploy.Message}";

            int okPieces = result.Pieces.Count(p => p.Ok);
            await _audit.LogAsync(new AuditEntry
            {
                Category = "armorforge", Action = $"import_{lane.Key}_set", TargetType = "itemset", TargetName = setName, TargetId = setId,
                StateAfter = JsonSerializer.Serialize(new { setId, tbcSetId, sourceExpansion = lane.Key, pieces = result.Pieces.Select(p => new { p.TbcEntry, p.Ok, p.ItemEntry, p.DisplayId }) }),
                IsReversible = true, RevertKind = RevertKind.Registry,
                // A set that imported zero of its members is not a success just because the set row
                // was created. Partial imports stay true — the set exists and the piece rows say which.
                Success = okPieces > 0,
                Notes = result.Message,
            });
            return result;
        }
        catch (Exception ex)
        {
            await _audit.LogAsync(new AuditEntry
            {
                Category = "armorforge", Action = $"import_{lane.Key}_set", TargetType = "itemset",
                TargetName = setName, TargetId = setId > 0 ? setId : null,
                StateAfter = JsonSerializer.Serialize(new { buildId, setId, tbcSetId, sourceExpansion = lane.Key, memberCount = members.Count }),
                IsReversible = false, RevertKind = RevertKind.None, Success = false,
                Notes = $"{lane.Label} set import FAILED: {ex.Message}" +
                        (setId > 0
                            ? $" Set {setId} was already created and any members imported before the failure are still there — delete the set to unwind."
                            : " No set id was allocated."),
            });
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VANILLA SET BONUSES (optional, operator-defined — never imported)
    // ═══════════════════════════════════════════════════════════════════

    public async Task<ArmorSetResult> SaveSetAsync(ArmorSetSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("A set needs a name.", nameof(request));
        if (request.MemberEntries is null || request.MemberEntries.Count == 0) throw new ArgumentException("A set needs at least one member piece.", nameof(request));

        int setId = request.SetId;
        string buildId = "armset-" + Guid.NewGuid().ToString("N")[..12];
        string setName = request.Name.Trim();
        try
        {
            if (setId <= 0)
            {
                long floor = await ComputeSetIdFloorAsync();
                var res = await _ids.ReserveAsync(KindSet, floor, buildId, "set");
                setId = checked((int)res.Id);
                await _ids.MarkStateAsync(KindSet, res.Id, "committed");
            }

            var bonuses = (request.Bonuses ?? new List<ArmorSetBonus>())
                .Where(b => b.Threshold > 0 && b.SpellId > 0).OrderBy(b => b.Threshold).Take(ArmorItemSetDbc.MaxBonuses).ToList();

            await using (var conn = _db.Admin())
            {
                await conn.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();
                await conn.ExecuteAsync(
                    @"INSERT INTO custom_armor_set (set_id, name, bonuses_json, req_skill, req_skill_rank, created_at)
                      VALUES (@setId, @name, @bonuses, @skill, @rank, NOW())
                      ON DUPLICATE KEY UPDATE name=@name, bonuses_json=@bonuses, req_skill=@skill, req_skill_rank=@rank",
                    new { setId, name = setName, bonuses = JsonSerializer.Serialize(bonuses), skill = request.RequiredSkill, rank = request.RequiredSkillRank }, tx);
                await conn.ExecuteAsync("UPDATE custom_armor_display SET set_id=0 WHERE set_id=@setId", new { setId }, tx);
                await conn.ExecuteAsync("UPDATE custom_armor_display SET set_id=@setId WHERE item_entry IN @entries", new { setId, entries = request.MemberEntries }, tx);
                await tx.CommitAsync();
            }

            // Everything below is after the commit — an audit row written inside that transaction
            // would survive its rollback, because AuditService writes on its own connection.
            // Union in the CLONED members before stamping. StampItemSetAsync clears set_id on every row
            // NOT in the list it is given, and the bonuses editor builds its member checkboxes from the
            // registry (custom_armor_display) — which a clone never writes to. So saving bonuses on a set
            // that mixes imported and cloned pieces would submit only the imported ones and silently drop
            // every cloned piece out of the set. A no-op for sets with no cloned members.
            var stampEntries = request.MemberEntries.ToList();
            var clonedForStamp = await ReadClonedSetMembersAsync(new[] { setId });
            if (clonedForStamp.TryGetValue(setId, out var clonedStamp))
                foreach (int e in clonedStamp) if (!stampEntries.Contains(e)) stampEntries.Add(e);
            var stamp = await StampItemSetAsync(setId, stampEntries);
            var patch = await AssembleUnifiedPatchAsync();
            WriteOutputs(buildId, patch, null);
            var deploy = QueueUnifiedRebuild($"saved set {setId} '{setName}'");
            var serverDeploy = DeployItemSetToServer(patch);
            var reload = await ReloadItemTemplateAsync();

            await _audit.LogAsync(new AuditEntry
            {
                Category = "armorforge", Action = "save_set", TargetType = "itemset", TargetName = setName, TargetId = setId,
                // This row issues a reload but never recorded it, unlike every other apply path here.
                RaCommand = ".reload item_template", RaResponse = reload.Message,
                StateAfter = JsonSerializer.Serialize(new { setId, members = request.MemberEntries, bonuses }),
                IsReversible = true, RevertKind = RevertKind.Registry,
                // The registry write is committed by here; the item_template stamp is what the core
                // actually reads for set membership, so a failed stamp is a failed save.
                Success = stamp.Ok,
                Notes = $"Set {setId} saved with {request.MemberEntries.Count} member(s). Stamp: {stamp.Message}. Client patch: {deploy.Message}. Server DBC: {serverDeploy.Message}. Reload: {reload.Message}.",
            });

            return new ArmorSetResult
            {
                SetId = setId, MemberCount = request.MemberEntries.Count, BonusCount = bonuses.Count,
                PatchDeployed = false,
                PatchQueued = deploy.Queued,
                PatchPending = deploy.Pending,
                ServerDbcDeployed = serverDeploy.State == ItemSetDeployState.Deployed,
                ItemTemplateStamped = stamp.Ok,
                Message = $"Set {setId}: {deploy.Message}; server sets: {serverDeploy.Message}",
            };
        }
        catch (Exception ex)
        {
            await _audit.LogAsync(new AuditEntry
            {
                Category = "armorforge", Action = "save_set", TargetType = "itemset",
                TargetName = setName, TargetId = setId > 0 ? setId : null,
                StateAfter = JsonSerializer.Serialize(new { buildId, setId, members = request.MemberEntries }),
                IsReversible = false, RevertKind = RevertKind.None, Success = false,
                Notes = $"Set save FAILED: {ex.Message} — membership and bonuses are written in one transaction, " +
                        "but the item_template stamp, the patch and the server DBC that follow it are not.",
            });
            throw;
        }
    }

    private async Task<(bool Ok, string Message)> StampItemSetAsync(int setId, IReadOnlyList<long> entries)
    {
        try
        {
            await using var conn = _db.Mangos();
            await conn.OpenAsync();
            // Unstamp pieces removed from the set (else the server keeps counting them), then stamp members.
            int cleared = await conn.ExecuteAsync("UPDATE item_template SET set_id=0 WHERE set_id=@setId AND entry NOT IN @entries", new { setId, entries });
            int rows = await conn.ExecuteAsync("UPDATE item_template SET set_id=@setId WHERE entry IN @entries", new { setId, entries });
            return (true, $"stamped set_id on {rows} item_template row(s)" + (cleared > 0 ? $", cleared {cleared}" : ""));
        }
        catch (Exception ex) { return (false, $"set_id stamp failed: {ex.Message}"); }
    }

    private async Task<long> ComputeSetIdFloorAsync()
    {
        long floor = ArmorItemSetDbc.CustomSetIdFloor;
        try
        {
            await using var conn = _db.Admin();
            await conn.OpenAsync();
            long max = await conn.ExecuteScalarAsync<long?>("SELECT MAX(set_id) FROM custom_armor_set") ?? 0;
            if (max + 1 > floor) floor = max + 1;
        }
        catch { /* table may not exist yet */ }
        try
        {
            byte[]? setDbc = _mpq.ExtractFile(ArmorNaming.ItemSetMember, skipArchive: n => n.StartsWith("patch-6", StringComparison.OrdinalIgnoreCase));
            if (setDbc is { Length: > 0 })
            {
                var reader = DbcWriterService.ReadDbc(setDbc, ArmorNaming.ItemSetMember);
                if (reader.RecordSize == ArmorItemSetDbc.RecordSize && reader.GetMaxId() + 1 > floor) floor = reader.GetMaxId() + 1;
            }
        }
        catch { }
        return floor;
    }

    // ═══════════════════════════════════════════════════════════════════
    // RECOLOR BAKE (persist the previewed recolor into the shipped textures)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Recolor the resolved source's textures in place (body-atlas components + helm/shoulder/cloak
    /// skin) at the chosen primary hue — same palette engine and aggressive coverage the live preview uses,
    /// seeded off the SOURCE display id for parity — so the packaged item ships recolored.</summary>
    private async Task RecolorSourceAsync(ArmorImportSource src, int seed, float hue, float? sat, float? light, string theory, string tier, CancellationToken ct)
    {
        if (Array.IndexOf(PaletteSwapService.RecolorTheories, theory) < 0) theory = "none";
        var (kd, ku, mm, pop) = RetextureSupport.TierShape(tier);
        string tmpDir = Path.Combine(Path.GetTempPath(), "armorbake", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            // One primary for the whole piece, detected across every texture it paints — the same
            // anchor the preview used — so the bake does not put each slot's own biggest material on
            // the pick and ship a flat red piece.
            var anchorPngs = new List<string>();
            foreach (var c in src.Components)
            {
                string pngPath = Path.Combine(tmpDir, $"anchor_c{c.Slot}{c.GenderSuffix}.png");
                if (await TryWriteBlpAsPngAsync(c.Blp, pngPath, ct)) anchorPngs.Add(pngPath);
            }
            if (src.TextureBlp is { Length: > 0 })
            {
                string pngPath = Path.Combine(tmpDir, "anchor_skin.png");
                if (await TryWriteBlpAsPngAsync(src.TextureBlp, pngPath, ct)) anchorPngs.Add(pngPath);
            }
            RecolorAnchor? anchor = anchorPngs.Count > 0 ? _palette.DetectPrimaryAcross(anchorPngs) : null;

            if (src.Components.Count > 0)
            {
                var recolored = new List<ArmorComponentBlob>();
                foreach (var c in src.Components)
                {
                    var rb = await RecolorBlpBytesAsync(c.Blp, tmpDir, $"c{c.Slot}{c.GenderSuffix}", seed, hue, sat, light, anchor, theory, kd, ku, mm, pop, uncompressed: true, ct);
                    recolored.Add(new ArmorComponentBlob { Slot = c.Slot, GenderSuffix = c.GenderSuffix, MpqPath = c.MpqPath, Blp = rb ?? c.Blp });
                }
                src.Components.Clear();
                src.Components.AddRange(recolored);
            }
            if (src.TextureBlp is { Length: > 0 })
            {
                var rb = await RecolorBlpBytesAsync(src.TextureBlp, tmpDir, "skin", seed, hue, sat, light, anchor, theory, kd, ku, mm, pop, uncompressed: false, ct);
                if (rb != null) src.TextureBlp = rb;
            }
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    private static async Task<bool> TryWriteBlpAsPngAsync(byte[] blp, string pngPath, CancellationToken ct)
    {
        try
        {
            var px = BlpDecoder.GetPixels(blp, 0, out int w, out int h);
            if (w == 0 || h == 0) return false;
            using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
            bmp.NotifyPixelsChanged();
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(pngPath, data.ToArray(), ct);
            return true;
        }
        catch { return false; }
    }

    private async Task<byte[]?> RecolorBlpBytesAsync(byte[] blp, string tmpDir, string stem, int seed, float hue, float? sat, float? light,
        RecolorAnchor? anchor, string theory, float kd, float ku, float mm, float pop, bool uncompressed, CancellationToken ct)
    {
        try
        {
            var px = BlpDecoder.GetPixels(blp, 0, out int w, out int h);
            if (w == 0 || h == 0) return null;
            string basePng = Path.Combine(tmpDir, stem + ".png");
            using (var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul))
            {
                System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
                bmp.NotifyPixelsChanged();
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(basePng, data.ToArray(), ct);
            }
            string outPng = Path.Combine(tmpDir, stem + "_r.png");
            var okp = await _palette.RecolorSeededAsync(basePng, outPng, seed, 1f, 0f, tintStructural: true, ct,
                theory, kd, ku, mm, pop, swapBudget: 1.01f, hueLeash: 180f, value: ValueSettings.Keep, baseHueOverride: hue,
                baseSatOverride: sat, baseLightOverride: light, anchor: anchor);
            if (okp == null) return null;
            using var recoloredBmp = SKBitmap.Decode(outPng);
            if (recoloredBmp == null) return null;
            return uncompressed
                ? _blp.EncodeBitmapToBlpUncompressed(recoloredBmp)
                : (_blp.EncodeBitmapToBlp(recoloredBmp, useDxt1: false) ?? _blp.EncodeBitmapToBlpUncompressed(recoloredBmp));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Armor bake recolor BLP failed {Stem}", stem); return null; }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VANILLA CLONE (re-itemize an existing vanilla armor item)
    // ═══════════════════════════════════════════════════════════════════
    //
    // Unlike the TBC/WotLK lanes (which re-emit FOREIGN art into patch-6), a vanilla clone reuses the
    // source item's OWN vanilla display — the model/textures already live in the mounted client, so
    // there is nothing to package. It is a pure item_template clone: copy the source row into a fresh
    // custom entry (reusing display_id), then apply the operator's gameplay edits. Usable immediately
    // via .additem; no new display, no patch rebuild. A recolor performed in the config necessarily
    // takes the new-display path instead (Armor Forge recolor bake), because a recolor is new art.

    /// <param name="SetId">The piece's own <c>item_template.set_id</c> — 0 for the loose browse, which
    /// does not select it. This is what the SERVER counts for set bonuses, so the set cards union it
    /// with the client DBC's member lists rather than trusting the DBC alone.</param>
    public sealed record VanillaArmorPieceDto(uint Entry, string Name, int Quality, int ItemLevel,
        int DisplayId, int InventoryType, string Family, string FamilyLabel, ArmorRenderKind RenderKind,
        int SetId = 0);

    public sealed record VanillaSourceConfigDto(string Name, int Quality, int ItemLevel, int RequiredLevel,
        long BuyPrice, long SellPrice, int Armor, int HolyRes, int FireRes, int NatureRes, int FrostRes,
        int ShadowRes, int ArcaneRes, int MaxDurability, int Bonding, int InventoryType, int AllowableClass,
        IReadOnlyList<(int Type, int Value)> Stats,
        IReadOnlyList<(int SpellId, int Trigger, int Charges, float PpmRate, int CooldownMs, int Category, int CategoryCooldownMs)> Spells);

    private static readonly HashSet<int> VanillaArmorSlots = new() { 1, 3, 4, 5, 6, 7, 8, 9, 10, 16, 19, 20, 23 };

    private static (string Key, string Label) FamilyForInventory(int inv) => inv switch
    {
        1 => ("helm", "Head"), 3 => ("shoulder", "Shoulder"), 4 => ("shirt", "Shirt"), 5 => ("chest", "Chest"),
        6 => ("belt", "Waist"), 7 => ("legs", "Legs"), 8 => ("boots", "Feet"), 9 => ("bracers", "Wrist"),
        10 => ("gloves", "Hands"), 16 => ("cloak", "Back"), 19 => ("tabard", "Tabard"), 20 => ("robe", "Robe"),
        23 => ("held", "Held"), _ => ("other", "Other")
    };

    /// <summary>How a slot actually renders, for the browse row's icon and note. The clone lane used to
    /// report every piece as <see cref="ArmorRenderKind.Painted"/>, which is true of the body-atlas slots
    /// and a lie about the three that carry geometry: a helm and a shoulder are attached models and a
    /// cloak is the cape geoset plus a texture. "held" is not in <see cref="ArmorTypeCatalog"/> (the
    /// import lanes cannot build one) but a clone needs no art, so it is offered and named honestly —
    /// its model hangs off the off-hand attachment, so it is Modelled, not painted.</summary>
    private static ArmorRenderKind RenderKindForInventory(int inv) => inv switch
    {
        1 or 3 => ArmorRenderKind.Modelled,
        16 => ArmorRenderKind.Cloak,
        23 => ArmorRenderKind.Modelled,
        _ => ArmorRenderKind.Painted,
    };

    /// <summary>How many stock vanilla armor pieces the clone lane can offer. Doubles as the lane's
    /// reachability probe for the status pill — it is the same predicate the browse uses, so a number
    /// here means the browse below it will return rows.</summary>
    public async Task<int> CountVanillaAsync()
    {
        await using var conn = _db.Mangos();
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM item_template
              WHERE patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = item_template.entry)
                AND class = 4 AND display_id > 0 AND inventory_type IN @slots
                AND entry < @customFloor",
            new { slots = VanillaArmorSlots.ToArray(), customFloor = WeaponIdReservationService.ItemEntryFloor });
    }

    /// <summary>Browse existing vanilla (world-DB) armor pieces to clone. Class 4 with a display; filtered
    /// by name/entry and optional slot family.</summary>
    /// <summary>Resolve specific entries to cloneable armor pieces — the set-card path, where membership
    /// comes from <see cref="VanillaArmorSetCatalog"/> (the client's ItemSet.dbc) and only the gameplay
    /// columns need looking up. Applies the same filters as the loose browse (max-patch row, class 4,
    /// a wearable slot, a real display, stock entries only), so a set's non-armor members — the weapons
    /// in <c>Dal'Rend's Arms</c>, the rings in a dungeon set — simply do not come back and the caller
    /// can report how many were dropped.</summary>
    public async Task<IReadOnlyList<VanillaArmorPieceDto>> ReadVanillaPiecesAsync(
        IReadOnlyCollection<uint> entries, IReadOnlyCollection<int>? setIds = null)
    {
        bool hasSetIds = setIds is { Count: > 0 };
        if (entries.Count == 0 && !hasSetIds) return Array.Empty<VanillaArmorPieceDto>();

        await using var conn = _db.Mangos();
        await conn.OpenAsync();
        // Two membership sources, unioned. The DBC lists a set's items for the CLIENT tooltip; the
        // SERVER counts item_template.set_id, and the WotLK lane already had to learn that the column
        // wins when they disagree (ARMOR_FORGE.md §4b-ii — Blizzard's own T9 DBC rows are wrong). Stock
        // 1.12 data agrees, but a DB with edited set_id values would otherwise show one membership on
        // the card and count a different one in game.
        var rows = await conn.QueryAsync(
            @"SELECT entry, name, quality, item_level AS ItemLevel, display_id AS DisplayId,
                     inventory_type AS InventoryType, set_id AS SetId
              FROM item_template
              WHERE (entry IN @entries OR (@hasSetIds = 1 AND set_id IN @setIds))
                AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = item_template.entry)
                AND class = 4 AND display_id > 0 AND inventory_type IN @slots
                AND entry < @customFloor",
            new
            {
                entries = entries.Count > 0 ? entries.ToArray() : new uint[] { 0 },
                setIds = hasSetIds ? setIds!.ToArray() : new[] { 0 },
                hasSetIds = hasSetIds ? 1 : 0,
                slots = VanillaArmorSlots.ToArray(),
                customFloor = WeaponIdReservationService.ItemEntryFloor,
            });

        var list = new List<VanillaArmorPieceDto>();
        foreach (var r in rows)
        {
            int inv = Convert.ToInt32(r.InventoryType);
            var fam = FamilyForInventory(inv);
            list.Add(new VanillaArmorPieceDto(
                Convert.ToUInt32(r.entry), (string)r.name, Convert.ToInt32(r.quality),
                Convert.ToInt32(r.ItemLevel), Convert.ToInt32(r.DisplayId), inv, fam.Key, fam.Label,
                RenderKindForInventory(inv), Convert.ToInt32(r.SetId ?? 0)));
        }
        return list;
    }

    public async Task<IReadOnlyList<VanillaArmorPieceDto>> BrowseVanillaAsync(string? search, string? family, int limit = 60)
    {
        limit = Math.Clamp(limit, 1, 200);
        var slots = VanillaArmorSlots.ToArray();
        if (!string.IsNullOrWhiteSpace(family))
        {
            var matched = VanillaArmorSlots.Where(s => FamilyForInventory(s).Key.Equals(family, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matched.Length > 0) slots = matched;
        }

        // Slot-word search ("boots", "waist", "helmet") narrows to the slot instead of the name —
        // same behavior as the import lanes' browse; boots are mostly named Sabatons/Treads/Greaves.
        var slotFamily = ArmorTypeCatalog.FamilyForSlotWord(search);
        if (slotFamily != null && string.IsNullOrWhiteSpace(family))
        {
            var matched = VanillaArmorSlots.Where(s => FamilyForInventory(s).Key.Equals(slotFamily, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matched.Length > 0) { slots = matched; search = null; }
        }

        string like = "%" + (search ?? "").Trim() + "%";
        uint entryExact = uint.TryParse((search ?? "").Trim(), out var ee) ? ee : 0;
        bool hasSearch = !string.IsNullOrWhiteSpace(search);

        await using var conn = _db.Mangos();
        await conn.OpenAsync();
        var rows = await conn.QueryAsync(
            // Two predicates the weapon forge's vanilla browse already carries and this one did not
            // (BrowseVanillaWeaponsAsync says as much in its own comment):
            //   • max-patch scoping. item_template is keyed (entry, patch), so an item that Blizzard
            //     revised across content patches has a row per patch and used to appear once per row —
            //     duplicates that ate the LIMIT and made the list look arbitrarily short.
            //   • excluding the custom range, or every piece this forge has already imported or cloned
            //     comes back as a clone source. Cloning one of those points the copy at a CUSTOM display
            //     that "Delete" can destroy later, leaving the clone rendering as a missing model.
            @"SELECT entry, name, quality, item_level AS ItemLevel, display_id AS DisplayId, inventory_type AS InventoryType
              FROM item_template
              WHERE patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = item_template.entry)
                AND class = 4 AND display_id > 0 AND inventory_type IN @slots
                AND entry < @customFloor
                AND (@noSearch = 1 OR name LIKE @like OR entry = @entryExact)
              ORDER BY item_level DESC, quality DESC, name
              LIMIT @limit",
            new
            {
                slots, like, entryExact, noSearch = hasSearch ? 0 : 1, limit,
                customFloor = WeaponIdReservationService.ItemEntryFloor,
            });

        var list = new List<VanillaArmorPieceDto>();
        foreach (var r in rows)
        {
            int inv = Convert.ToInt32(r.InventoryType);
            var fam = FamilyForInventory(inv);
            list.Add(new VanillaArmorPieceDto(
                Convert.ToUInt32(r.entry), (string)r.name, Convert.ToInt32(r.quality),
                Convert.ToInt32(r.ItemLevel), Convert.ToInt32(r.DisplayId), inv, fam.Key, fam.Label,
                RenderKindForInventory(inv)));
        }
        return list;
    }

    /// <summary>Read the source item's real gameplay so the Configure modal can pre-fill it (vanilla clones
    /// start from the source, which is already on the vanilla curve).</summary>
    public async Task<VanillaSourceConfigDto?> ReadVanillaSourceAsync(uint entry)
    {
        await using var conn = _db.Mangos();
        await conn.OpenAsync();
        // Highest patch row wins — the same predicate every other item_template read in this app uses.
        // Without it Dapper handed back whichever row the storage engine returned first, so the Configure
        // modal could pre-fill a pre-nerf version of the item and then write those stale numbers back as
        // the clone's authoritative overrides.
        var row = await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM item_template WHERE entry=@entry ORDER BY patch DESC LIMIT 1", new { entry });
        if (row is null) return null;
        var d = (IDictionary<string, object>)row;
        int I(string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v) : 0;
        long L(string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToInt64(v) : 0;
        float F(string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToSingle(v) : 0f;
        string S(string k) => d.TryGetValue(k, out var v) && v != null ? v.ToString() ?? "" : "";

        if (I("class") != 4) return null;

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

        return new VanillaSourceConfigDto(S("name"), I("quality"), I("item_level"), I("required_level"),
            L("buy_price"), L("sell_price"), I("armor"), I("holy_res"), I("fire_res"), I("nature_res"),
            I("frost_res"), I("shadow_res"), I("arcane_res"), I("max_durability"), I("bonding"),
            I("inventory_type"), I("allowable_class"), stats, spells);
    }

    /// <summary>Clone an existing vanilla armor item into a new custom entry (reusing its display), then
    /// apply the operator's gameplay edits. No new display/patch — usable via .additem after a reload.
    ///
    /// <paramref name="recolorRequested"/> / <paramref name="glowRequested"/> are not features here —
    /// they are refusals. The Appearance panel can preview a recolor or a glow tint on a vanilla piece,
    /// and the import lanes bake exactly that into the shipped BLPs. A clone cannot: it ships no art at
    /// all, it reuses the SOURCE's display, and repainting that display would repaint the stock item for
    /// every character wearing it. The lane used to accept those parameters at the controller and drop
    /// them on the floor, so the operator was told "will bake on import" and got an unrecolored item
    /// with nothing explaining why. Fail loudly instead.</summary>
    public async Task<CustomArmorBuildResult> CloneVanillaAsync(uint sourceEntry, string? nameOverride,
        ValidatedVanillaItemBuildConfiguration? gameplay,
        bool recolorRequested = false, bool glowRequested = false, int setId = 0, bool reload = true)
    {
        var result = new CustomArmorBuildResult { TbcEntry = sourceEntry, SourceExpansion = "vanilla" };

        // Stock set ids are 1..551 (measured on the mounted 1.12 ItemSet.dbc); forged ids start at
        // ArmorItemSetDbc.CustomSetIdFloor. Accepting a stock id here would re-create exactly the silent
        // enrolment the set_id-to-0 projection exists to prevent, from the other direction.
        if (setId != 0 && setId < ArmorItemSetDbc.CustomSetIdFloor)
        {
            result.Ok = false;
            result.Message = $"Set id {setId} is a stock vanilla set. A clone may only join a FORGED set " +
                             $"(id {ArmorItemSetDbc.CustomSetIdFloor} or above) — cloning a set creates one.";
            return result;
        }

        if (recolorRequested || glowRequested)
        {
            result.Ok = false;
            string what = recolorRequested && glowRequested ? "recolor and glow tint"
                : recolorRequested ? "recolor" : "glow tint";
            result.Message =
                $"This clone cannot carry the previewed {what}. A clone reuses the source item's own display, " +
                "and new art needs a new display plus a patch-6 rebuild — which is what the TBC and WotLK import " +
                "lanes do. Either clear the Appearance panel and clone the piece as-is, or import the piece from " +
                "an import lane, where the recolor and glow are baked into the shipped textures.";
            return result;
        }

        await using var conn = _db.Mangos();
        await conn.OpenAsync();

        var srcRow = await conn.QueryFirstOrDefaultAsync(
            @"SELECT entry, name, class AS ItemClass, subclass AS Subclass,
                     display_id AS DisplayId, inventory_type AS InventoryType
              FROM item_template WHERE entry=@e
              ORDER BY patch DESC LIMIT 1",
            new { e = sourceEntry });
        if (srcRow is null) { result.Ok = false; result.Message = $"Vanilla item {sourceEntry} not found."; return result; }
        // Cloning a piece this forge already made would point the copy at a CUSTOM display id, which a
        // later "Delete" on the original destroys — leaving the clone rendering as a missing model with
        // nothing recording why. The weapon lane refuses this; so does this one now.
        if (sourceEntry >= WeaponIdReservationService.ItemEntryFloor)
        {
            result.Ok = false;
            result.Message = $"Item {sourceEntry} is already a custom item — clone from a stock vanilla piece instead. " +
                             "(A clone reuses the source's display, and a custom display can be deleted out from under it.)";
            return result;
        }
        if (Convert.ToInt32(srcRow.ItemClass) != 4) { result.Ok = false; result.Message = $"Item {sourceEntry} is not armor (class 4)."; return result; }
        // Shields are class-4 armor but they live in the Weapon Forge (WeaponTypeCatalog's "shield"
        // family, slot 14 — deliberately absent from VanillaArmorSlots). The browse never offers
        // them, but this method is reachable by entry id, and a shield that got through landed on
        // family "other" and then failed modal validation with nothing explaining why.
        if (Convert.ToInt32(srcRow.Subclass) == 6)
        {
            result.Ok = false;
            result.Message = $"Item {sourceEntry} is a shield — clone it from the Weapon Forge's Vanilla lane, which owns the shield family.";
            return result;
        }

        string sourceName = (string)srcRow.name;
        int displayId = Convert.ToInt32(srcRow.DisplayId);
        int inv = Convert.ToInt32(srcRow.InventoryType);
        var fam = FamilyForInventory(inv);

        string buildId = "arm-clone-" + Guid.NewGuid().ToString("N")[..12];
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
            // Clone the exact source row into the new entry (schema-agnostic column list), reusing display_id.
            var cols = (await conn.QueryAsync<string>(
                @"SELECT COLUMN_NAME FROM information_schema.COLUMNS
                  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'item_template' ORDER BY ORDINAL_POSITION",
                transaction: tx)).ToList();
            if (cols.Count == 0) throw new InvalidOperationException("item_template schema could not be read.");

            string colList = string.Join(",", cols.Select(c => $"`{c}`"));
            string selList = string.Join(",", cols.Select(c =>
                c.Equals("entry", StringComparison.OrdinalIgnoreCase) ? "@newEntry"
                // patch 0 — the convention every other custom item this forge makes already follows
                // (DonorItemTemplateFixture's row is patch 0). The core loads, per entry, the highest
                // patch row NOT ABOVE the server's content-patch setting, so a row inherited at the
                // source's patch is invisible on any server configured below it. Zero always loads.
                : c.Equals("patch", StringComparison.OrdinalIgnoreCase) ? "0"
                // set_id is always cleared here and only ever re-stamped by the override UPDATE below.
                // Never the SOURCE's: riding that across would make the clone count toward the stock
                // tier set's bonuses, a silent gameplay change nobody asked for. Doing it in two steps
                // fails in the safe direction — a failure between the INSERT and the UPDATE leaves an
                // unjoined clone, not a piece silently enrolled in a set.
                : c.Equals("set_id", StringComparison.OrdinalIgnoreCase) ? "0"
                // These three name OTHER item entries (the faction mirror, the quest a book starts,
                // the gift a wrapper produces). Copied verbatim they point the clone at the source's
                // relationships, which is never what a re-itemized copy wants.
                : c.Equals("other_team_entry", StringComparison.OrdinalIgnoreCase) ? "0"
                : c.Equals("start_quest", StringComparison.OrdinalIgnoreCase) ? "0"
                : c.Equals("wrapped_gift", StringComparison.OrdinalIgnoreCase) ? "0"
                : $"`{c}`"));
            // ORDER BY patch DESC LIMIT 1 is load-bearing, not tidiness. item_template is keyed
            // (entry, patch), so a source revised across content patches has one row per patch — and
            // because the projection above pins every copy to patch 0, an unqualified SELECT would try
            // to insert them ALL as (newEntry, 0) and die on the primary key with ER_DUP_ENTRY. Take
            // the newest row, which is the one the browse and the Configure pre-fill both showed.
            int inserted = await conn.ExecuteAsync(
                $"INSERT INTO item_template ({colList}) SELECT {selList} FROM item_template WHERE entry=@src ORDER BY patch DESC LIMIT 1",
                new { newEntry, src = sourceEntry }, tx);
            // A zero-row insert used to report a perfectly successful clone of nothing.
            if (inserted != 1)
                throw new InvalidOperationException(
                    $"clone INSERT affected {inserted} rows (expected 1) copying item {sourceEntry}.");

            // Apply the name + validated gameplay overrides on top of the cloned row.
            var sets = new List<string> { "name=@nm" };
            var dp = new DynamicParameters();
            dp.Add("e", newEntry);
            dp.Add("nm", name);
            // Set membership, when this clone is part of a cloned set. It is what the SERVER counts for
            // bonuses, and — because a clone has no registry row — it is also the only record that this
            // piece belongs to the set at all (see ReadClonedSetMembersAsync).
            if (setId > 0) { sets.Add("set_id=@sid"); dp.Add("sid", setId); }
            if (gameplay?.Overrides is { Count: > 0 })
            {
                foreach (var (col, literal) in gameplay.Overrides)
                {
                    // Belt and braces: the clone keeps the SOURCE's slot. Unlike the weapon modal, the
                    // armor Configure modal has no slot control and never sends inventory_type today, so
                    // this is unreachable — it is here because honouring a slot change would move (say) a
                    // chest piece into the legs slot while it still wears the chest's display, and a
                    // body-atlas item paints into the region its DISPLAY declares, so the piece would
                    // render on the wrong part of the body. Slot changes belong to the import lanes,
                    // which build art for the family they claim.
                    if (col.Equals("inventory_type", StringComparison.OrdinalIgnoreCase)) continue;

                    if (col.Equals("description", StringComparison.OrdinalIgnoreCase))
                    {
                        sets.Add("description=@desc");
                        dp.Add("desc", literal); // raw string value, parameterized
                    }
                    else
                    {
                        // Translator emits safe numeric/CONVERT literals for every other column.
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
            catch (Exception rollbackEx) { _logger.LogWarning(rollbackEx, "ArmorForge: clone rollback failed for entry {Entry}", newEntry); }

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
                _logger.LogWarning(cleanupEx, "ArmorForge: clone cleanup delete failed for entry {Entry}", newEntry);
            }

            await _ids.ReleaseAsync(WeaponIdReservationService.KindItemEntry, entryRes.Id);
            result.Ok = false;
            result.Message = "clone failed: " + ex.Message;

            await _audit.LogAsync(new AuditEntry
            {
                // "item_custom", not "item": ChangeGraphService.TargetTables has no mapping for "item",
                // so the one row that exists to record a failed clone was the one row the Change Graph
                // could not show. It matches the success audit and the weapon lane.
                Category = "armorforge", Action = "clone_vanilla", TargetType = "item_custom", TargetName = name,
                TargetId = newEntry is > 0 and <= int.MaxValue ? (int)newEntry : null,
                StateAfter = JsonSerializer.Serialize(new { buildId, sourceEntry, itemEntry = newEntry, displayId, family = fam.Key }),
                IsReversible = false, RevertKind = RevertKind.None, Success = false,
                Notes = $"Vanilla clone {sourceEntry} → {newEntry} FAILED: {ex.Message}. The id was returned to the pool. " +
                        (cleaned
                            ? $"item_template row {newEntry} was removed."
                            : $"THE CLEANUP DELETE ALSO FAILED — a partial copy of {sourceEntry} may still be live under entry " +
                              $"{newEntry}. Check with: SELECT * FROM item_template WHERE entry = {newEntry};"),
            });
            return result;
        }

        await _ids.MarkStateAsync(WeaponIdReservationService.KindItemEntry, entryRes.Id, "committed");

        var apply = new ServerApplyStatus
        {
            SqlApplied = true,
            SqlMessage = $"cloned entry {newEntry} (reuses display {displayId})",
            PatchRequired = false, // nothing is packaged — there is no patch step to report on
        };
        // One reload per SET, not per piece: a 9-piece set clone was nine RA round-trips to the live
        // core, which is the same pile-up the set DELETE path had to be fixed for (ARMOR_FORGE.md §7b).
        // Defaults to true so the single-clone path is unchanged.
        if (reload)
        {
            var reloadRes = await ReloadItemTemplateAsync();
            apply.Reloaded = reloadRes.Ok; apply.ReloadMessage = reloadRes.Message;
        }
        else apply.ReloadMessage = "deferred to the end of the set";

        result.ItemEntry = newEntry; result.DisplayId = displayId; result.Name = name;
        result.ArmorTypeKey = fam.Key;
        result.RenderKind = RenderKindForInventory(inv); // the source's own art — a helm is still Modelled
        result.Apply = apply; result.Ok = true;
        result.Message = $"Cloned vanilla {sourceEntry} → {newEntry} (reuses display {displayId}).";

        await _audit.LogAsync(new AuditEntry
        {
            Category = "armorforge", Action = "clone_vanilla",
            // "item_custom" (not "item") is deliberate, and matches CloneVanillaWeaponAsync: it is the
            // target type ChangeGraphService.RevertByDeletingAsync keys on, and it files the row under
            // the Change Graph's items domain. A clone is the one Armor Forge output the graph can
            // genuinely undo — there is no registry row and no patch member to strand, so deleting the
            // item_template row restores the world exactly. Marking it IsReversible=false left the
            // operator with no way to take a clone back at all.
            TargetType = "item_custom", TargetName = name,
            TargetId = checked((int)newEntry),
            RaCommand = ".reload item_template", RaResponse = apply.ReloadMessage,
            StateAfter = JsonSerializer.Serialize(new
            {
                buildId, sourceEntry, sourceName, itemEntry = newEntry, displayId,
                family = fam.Key, inventoryType = inv, overrides = gameplay?.Overrides,
            }),
            IsReversible = true, RevertKind = RevertKind.DeleteCustom, Success = true,
            Notes = $"Vanilla clone {sourceEntry} → {newEntry}, reuses display {displayId}. Reload: {apply.ReloadMessage}.",
        });
        return result;
    }

    /// <summary>Clone a whole stock vanilla set: every cloneable member into a new custom entry, all of
    /// them joined to ONE newly reserved forged set id, with the operator's per-piece gameplay and set
    /// bonuses.
    ///
    /// This is the one place the vanilla lane's "nothing is packaged" contract does not hold, and it
    /// cannot: a set is not a property of its items, it is a row in <c>ItemSet.dbc</c>. The pieces still
    /// ship no art — they reuse their sources' displays — but the set DEFINITION has to reach both the
    /// client (patch-6, for the tooltip) and the server's own dbc directory (for the bonuses to fire),
    /// and the core reads DBCs only at startup. So unlike a single clone this needs a patch rebuild, a
    /// client restart and a mangosd restart, and the result message says so.
    ///
    /// Bonuses default to the SOURCE set's own — uniquely possible here, because a vanilla set's spells
    /// exist in a vanilla core by definition. The TBC/WotLK lanes must ask the operator to pick vanilla
    /// equivalents instead.</summary>
    public async Task<ArmorSetImportResult> CloneVanillaSetAsync(
        int sourceSetId,
        IReadOnlyList<uint> memberEntries,
        string setName,
        IReadOnlyDictionary<uint, ValidatedVanillaItemBuildConfiguration>? perPieceGameplay = null,
        IReadOnlyList<ArmorSetBonus>? bonuses = null,
        int reqSkill = 0, int reqSkillRank = 0)
    {
        if (memberEntries.Count == 0)
            throw new ArgumentException("A cloned set needs at least one cloneable armor member.", nameof(memberEntries));
        if (string.IsNullOrWhiteSpace(setName))
            throw new ArgumentException("A cloned set needs a name.", nameof(setName));
        setName = setName.Trim();

        // ItemSet.dbc holds 17 member slots. Refuse rather than silently dropping the tail.
        if (memberEntries.Count > ArmorItemSetDbc.MaxItems)
            throw new ArgumentException(
                $"A set can hold at most {ArmorItemSetDbc.MaxItems} items; {memberEntries.Count} were selected.", nameof(memberEntries));

        var validBonuses = (bonuses ?? new List<ArmorSetBonus>())
            .Where(b => b.Threshold > 0 && b.SpellId > 0).OrderBy(b => b.Threshold).Take(ArmorItemSetDbc.MaxBonuses).ToList();

        // Preflight the packaging path BEFORE anything is reserved or written. Unlike a single clone,
        // this method has to build and deploy a patch at the end — and that step needs a mounted client
        // to extract a base DBC from, plus a server dbc directory for the core to read the set from. It
        // used to reach those requirements only after reserving a set id, inserting the set row and
        // cloning every piece, so a missing mount left a half-built set behind and threw.
        if (_mpq.ExtractFile(ArmorNaming.ItemSetMember, skipArchive: n => n.StartsWith("patch-6", StringComparison.OrdinalIgnoreCase)) is not { Length: > 0 })
            throw new InvalidOperationException(
                "Cloning a SET needs the 1.12 client mounted: unlike a single clone it ships an ItemSet.dbc " +
                "row in patch-6, which is built by extending the client's own copy. Set Vmangos:ClientDataPath " +
                "in Settings, or clone the pieces individually instead.");

        // N+1 audit rows — one per cloned piece plus the set row — grouped into one named run, exactly
        // as ImportSetAsync does for the import lanes.
        using var batch = AuditBatch.Begin($"Armor Forge \u2014 clone vanilla set '{setName}'");

        string buildId = "armclone-set-" + Guid.NewGuid().ToString("N")[..12];
        int setId = 0;
        try
        {
            long floor = await ComputeSetIdFloorAsync();
            var res = await _ids.ReserveAsync(KindSet, floor, buildId, "set");
            setId = checked((int)res.Id);
            await _ids.MarkStateAsync(KindSet, res.Id, "committed");

            await using (var conn = _db.Admin())
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    @"INSERT INTO custom_armor_set (set_id, name, bonuses_json, req_skill, req_skill_rank, created_at)
                      VALUES (@setId, @name, @bonuses, @skill, @rank, NOW())",
                    new { setId, name = setName, bonuses = JsonSerializer.Serialize(validBonuses), skill = reqSkill, rank = reqSkillRank });
            }

            var result = new ArmorSetImportResult { SetId = setId, Name = setName };
            foreach (var entry in memberEntries)
            {
                try
                {
                    ValidatedVanillaItemBuildConfiguration? pieceGameplay = null;
                    perPieceGameplay?.TryGetValue(entry, out pieceGameplay);
                    var piece = await CloneVanillaAsync(entry, null, pieceGameplay, setId: setId, reload: false);
                    if (!piece.Ok) _logger.LogWarning("ArmorForge: vanilla set clone member {Entry} failed: {Msg}", entry, piece.Message);
                    result.Pieces.Add(piece);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ArmorForge: vanilla set {Set} member {Entry} failed", sourceSetId, entry);
                    result.Pieces.Add(new CustomArmorBuildResult { TbcEntry = entry, SourceExpansion = "vanilla", Ok = false, Message = ex.Message });
                }
            }

            // One reload and one rebuild for the whole set. The pieces contributed no art, so what the
            // rebuild actually ships is the ItemSet.dbc row — which is the entire point.
            var reload = await ReloadItemTemplateAsync();
            var patch = await AssembleUnifiedPatchAsync();
            WriteOutputs(buildId, patch, null);
            var dep = QueueUnifiedRebuild($"cloned vanilla set {setId} '{setName}'");
            var serverDeploy = DeployItemSetToServer(patch);
            result.PatchDeployed = false;
            result.PatchQueued = dep.Queued;
            result.PatchPending = dep.Pending;
            result.ServerItemSetDeployed = serverDeploy.State == ItemSetDeployState.Deployed;
            result.ServerItemSetMessage = serverDeploy.Message;

            int okPieces = result.Pieces.Count(x => x.Ok);
            result.Message = $"{okPieces}/{memberEntries.Count} pieces cloned into set {setId} '{setName}'"
                + (validBonuses.Count > 0 ? $" with {validBonuses.Count} bonus(es)" : " (no bonuses)")
                + $". Reload: {reload.Message}. {dep.Message} Server sets: {serverDeploy.Message}";

            await _audit.LogAsync(new AuditEntry
            {
                Category = "armorforge", Action = "clone_vanilla_set", TargetType = "itemset",
                TargetName = setName, TargetId = setId,
                StateAfter = JsonSerializer.Serialize(new
                {
                    setId, sourceSetId, sourceExpansion = "vanilla",
                    pieces = result.Pieces.Select(x => new { x.TbcEntry, x.Ok, x.ItemEntry, x.DisplayId }),
                }),
                IsReversible = true, RevertKind = RevertKind.Registry,
                // A set that cloned none of its members is not a success just because the set row exists.
                Success = okPieces > 0,
                Notes = result.Message,
            });
            return result;
        }
        catch (Exception ex)
        {
            await _audit.LogAsync(new AuditEntry
            {
                Category = "armorforge", Action = "clone_vanilla_set", TargetType = "itemset",
                TargetName = setName, TargetId = setId > 0 ? setId : null,
                StateAfter = JsonSerializer.Serialize(new { buildId, setId, sourceSetId, memberCount = memberEntries.Count }),
                IsReversible = false, RevertKind = RevertKind.None, Success = false,
                Notes = $"Vanilla set clone FAILED: {ex.Message}" +
                        (setId > 0
                            ? $" Set {setId} was already created and any members cloned before the failure are still there \u2014 delete the set to unwind."
                            : " No set id was allocated."),
            });
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // PERSISTENCE
    // ═══════════════════════════════════════════════════════════════════

    private async Task PersistAsync(ArmorPersistRow row, IReadOnlyList<ArmorComponentBlob> components, IReadOnlyList<MpqMember> models)
    {
        await using var conn = _db.Admin();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await conn.ExecuteAsync(
            @"INSERT INTO custom_armor_display
                (display_id, item_entry, build_id, set_id, render_kind, armor_type_key, material,
                 inventory_type, name, icon_stem, model_name, model_name2, texture_name, texture_mpq_path,
                 compiled_blp, geoset0, geoset1, geoset2, helmet_vis0, helmet_vis1, group_sound,
                 mirror_model, sql_text, gameplay_json, created_at)
              VALUES
                (@DisplayId, @ItemEntry, @BuildId, @SetId, @RenderKind, @ArmorTypeKey, @Material,
                 @InventoryType, @Name, @IconStem, @ModelName, @ModelName2, @TextureName, @TextureMpqPath,
                 @ModelTextureBlp, @Geoset0, @Geoset1, @Geoset2, @HelmetVis0, @HelmetVis1, @GroupSound,
                 0, @SqlText, @GameplayJson, NOW())
              ON DUPLICATE KEY UPDATE
                item_entry=@ItemEntry, set_id=@SetId, name=@Name, model_name=@ModelName, model_name2=@ModelName2,
                texture_name=@TextureName, texture_mpq_path=@TextureMpqPath, compiled_blp=@ModelTextureBlp",
            row, tx);

        await conn.ExecuteAsync("DELETE FROM custom_armor_component WHERE display_id=@DisplayId", new { row.DisplayId }, tx);
        foreach (var c in components)
            await conn.ExecuteAsync(
                @"INSERT INTO custom_armor_component (display_id, slot, gender_suffix, mpq_path, component_stem, compiled_blp, created_at)
                  VALUES (@DisplayId, @Slot, @Suffix, @MpqPath, @Stem, @Blp, NOW())",
                new { row.DisplayId, c.Slot, Suffix = c.GenderSuffix, c.MpqPath, Stem = ArmorNaming.ComponentStem((int)row.DisplayId, c.Slot), c.Blp }, tx);

        await conn.ExecuteAsync("DELETE FROM custom_armor_model WHERE display_id=@DisplayId", new { row.DisplayId }, tx);
        foreach (var m in models)
            await conn.ExecuteAsync(
                @"INSERT INTO custom_armor_model (display_id, mpq_path, compiled_m2, created_at) VALUES (@DisplayId, @MpqPath, @Data, NOW())",
                new { row.DisplayId, m.MpqPath, m.Data }, tx);

        await tx.CommitAsync();
    }

    /// <summary>Mirror the patch-6 ItemDisplayInfo row into the in-memory DBC the web previewer reads.
    ///
    /// This states the row in full rather than cloning a stock one. Cloning is what it used to do, and
    /// the fields it did NOT override came from whichever arbitrary row matched the shape filter
    /// first — for shoulders that is display 1057, <c>LShoulder_Leather_A_01</c>, whose
    /// TextureName2 is <c>Shoulder_Leather_A_01Brown</c>. Since
    /// <see cref="ItemTextureService.EnsureShoulderGlb"/> builds the right spaulder from
    /// ModelName2 + TextureName2, the forged right pad was previewed wearing a stock leather skin
    /// while the left wore its own — the "correct in game, wonky in the previewer" report, since
    /// <see cref="ArmorDisplayInfoRow.BuildAndAdd"/> always wrote the client's row correctly.
    ///
    /// Every field below is the same value that row gets, so the two cannot drift again.</summary>
    private void RegisterDisplayWithDbc(long displayId, ArmorTypeProfile profile, ArmorImportSource src)
    {
        try
        {
            if (src.RenderKind == ArmorRenderKind.Painted)
            {
                // A Painted piece has no model, but it is NOT exempt from registration.
                //   * Its icon lives in ItemDisplayIcons, and returning early here meant
                //     chest/gloves/legs/waist/wrists/feet never entered it and showed the red "?".
                //   * ItemsController.ItemDressing 404s outright when GetItemModelInfo returns null,
                //     and BodyAtlasTextureService reads the eight component stems out of
                //     ItemModelInfos[displayId].BodyTextures — so with no row the piece can never be
                //     dressed onto the 3D character either.
                // Register a MODEL-LESS row: empty model/texture names (there is no .mdx to
                // advertise) carrying the component stems, exactly what the patch DBC row writes.
                var painted = new string[8];
                foreach (var c in src.Components)
                    if (c.Slot >= 0 && c.Slot < 8)
                        painted[c.Slot] = ArmorNaming.ComponentStem((int)displayId, c.Slot);

                _dbc.RegisterCustomDisplayEntry((uint)displayId, new ItemModelDbc
                {
                    ModelName1 = "", ModelName2 = "", TextureName1 = "", TextureName2 = "",
                    // Geosets are load-bearing even with no model: they are what makes a robe render
                    // its skirt. Zeroing them paints the texture onto a piece with the wrong shape.
                    GeosetGroup = src.GeosetGroup is { Length: 3 } gg ? (int[])gg.Clone() : new int[3],
                    HelmetGeosetVis1 = 0, HelmetGeosetVis2 = 0,
                    BodyTextures = painted,
                    ItemVisualId = 0,
                }, iconName: string.IsNullOrEmpty(src.IconStem) ? null : src.IconStem);
                return;
            }

            string model1 = src.ModelName ?? "";
            string model2 = src.ModelName2 ?? "";
            string texture1 = src.TextureName ?? "";
            var geoset = src.GeosetGroup is { Length: 3 } g ? (int[])g.Clone() : new int[3];

            _dbc.RegisterCustomDisplayEntry((uint)displayId, new ItemModelDbc
            {
                ModelName1 = model1,
                ModelName2 = model2,
                TextureName1 = texture1,
                // Shoulders are an L/R pair sharing one skin; a single-model piece has no second
                // texture. Same rule as ArmorDisplayInfoRow.BuildAndAdd.
                TextureName2 = model2.Length == 0 ? "" : texture1,
                GeosetGroup = geoset,
                HelmetGeosetVis1 = src.HelmetVis0,
                HelmetGeosetVis2 = src.HelmetVis1,
                // Modelled/cloak pieces paint no body atlas, and the forge writes no ItemVisual —
                // inheriting either from a donor is how a forged helm picked up someone else's
                // hair-hiding or a stock item's glow.
                BodyTextures = new string[8],
                ItemVisualId = 0,
            }, iconName: string.IsNullOrEmpty(src.IconStem) ? null : src.IconStem);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ArmorForge: in-memory DBC registration skipped for display {Id}", displayId);
        }
    }

    /// <summary>
    /// Re-register every forged armor display into DbcService's in-memory caches at startup, so the
    /// web UI resolves their icon / model / texture after an app restart.
    ///
    /// <see cref="RegisterDisplayWithDbc"/> runs at FORGE time and writes to an in-memory cache that
    /// does NOT survive a restart — the same durability gap the Retexture Engine closes with
    /// LoadExistingRetexturesAsync and the Weapon Forge closes with LoadExistingWeaponsAsync. Armor
    /// had no equivalent, so every imported piece reverted to the red "?" icon and vanished from the
    /// 3D viewer the first time the app was restarted after importing it.
    ///
    /// Rebuilt from the registry rather than from an ArmorImportSource, because the import-time
    /// source object is long gone by then; custom_armor_display persists every field the entry needs.
    /// </summary>
    public async Task LoadExistingArmorAsync()
    {
        try
        {
            // Blobs are megabytes of compiled M2/BLP and nothing here reads them.
            var rows = await LoadArmorRowsAsync(includeBlobs: false);
            int registered = 0;
            foreach (var r in rows)
            {
                // Painted pieces have no display model — BodyAtlasTextureService serves them from
                // custom_armor_component instead. Same skip RegisterDisplayWithDbc applies.
                if (string.Equals(r.RenderKind, nameof(ArmorRenderKind.Painted), StringComparison.OrdinalIgnoreCase))
                {
                    // Model-less row + icon — see RegisterDisplayWithDbc for why a Painted piece
                    // still needs a row: ItemDressing 404s without one and the body atlas is keyed
                    // off BodyTextures. The stems come from the persisted component rows.
                    try
                    {
                        var painted = new string[8];
                        foreach (var c in r.Components)
                            if (c.Slot >= 0 && c.Slot < 8)
                                painted[c.Slot] = string.IsNullOrEmpty(c.ComponentStem)
                                    ? ArmorNaming.ComponentStem((int)r.DisplayId, c.Slot)
                                    : c.ComponentStem;
                        _dbc.RegisterCustomDisplayEntry((uint)r.DisplayId, new ItemModelDbc
                        {
                            ModelName1 = "", ModelName2 = "", TextureName1 = "", TextureName2 = "",
                            // See the forge-time branch: a robe without its geosets renders skirtless.
                            GeosetGroup = new[] { r.Geoset0, r.Geoset1, r.Geoset2 },
                            HelmetGeosetVis1 = 0, HelmetGeosetVis2 = 0,
                            BodyTextures = painted,
                            ItemVisualId = 0,
                        }, iconName: string.IsNullOrEmpty(r.IconStem) ? null : r.IconStem);
                        registered++;
                    }
                    catch (Exception iex)
                    { _logger.LogDebug(iex, "ArmorForge: painted re-registration skipped for display {Id}", r.DisplayId); }
                    continue;
                }
                try
                {
                    string model1 = r.ModelName ?? "";
                    string model2 = r.ModelName2 ?? "";
                    string texture1 = r.TextureName ?? "";
                    _dbc.RegisterCustomDisplayEntry((uint)r.DisplayId, new ItemModelDbc
                    {
                        ModelName1 = model1,
                        ModelName2 = model2,
                        TextureName1 = texture1,
                        // Shoulders are an L/R pair sharing one skin; a single-model piece has no
                        // second texture. Same rule as RegisterDisplayWithDbc and ArmorDisplayInfoRow.
                        TextureName2 = model2.Length == 0 ? "" : texture1,
                        GeosetGroup = new[] { r.Geoset0, r.Geoset1, r.Geoset2 },
                        HelmetGeosetVis1 = (uint)r.HelmetVis0,
                        HelmetGeosetVis2 = (uint)r.HelmetVis1,
                        BodyTextures = new string[8],
                        ItemVisualId = 0,
                    }, iconName: string.IsNullOrEmpty(r.IconStem) ? null : r.IconStem);
                    registered++;
                }
                catch (Exception inner)
                {
                    _logger.LogDebug(inner, "ArmorForge: re-registration skipped for display {Id}", r.DisplayId);
                }
            }
            if (registered > 0)
                _logger.LogInformation("ArmorForge: registered {Count} forged armor display(s) into the DBC cache", registered);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArmorForge: LoadExistingArmorAsync failed (forged armor may render as red '?' until a rebuild)");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // PATCH ASSEMBLY — lane contribution + standalone patch-6
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>This lane's rows and members for the single unified patch. Null when there is
    /// genuinely nothing to ship. Every source is a DB table, never a mounted archive, which is what
    /// lets the old per-lane patches stop existing without losing anything already forged.
    ///
    /// <paramref name="patchCeilingName"/> names the archive this contribution will be packed into,
    /// so the base ItemSet.dbc resolves from strictly BENEATH it and a builder never reads its own
    /// previous output back as input. Defaults to this lane's own patch for the standalone path.</summary>
    internal async Task<UnifiedPatch.ArmorLaneContribution?> GetPatchContributionAsync(
        ForgeDiagnostics diag, string? patchCeilingName = null)
    {
        var rows = await LoadArmorRowsAsync();
        // Sets are resolved up front, not just at packaging time, because they can be the ONLY reason to
        // build a patch. A cloned set writes no custom_armor_display rows at all, so `rows.Count == 0`
        // used to mean "nothing to ship" and returned null — which on a clone-only install removed
        // the patch from the client and restored the server's stock ItemSet.dbc, quietly unmaking the set
        // that had just been created. Bail only when there is genuinely nothing.
        var sets = await LoadSetsAsync(rows);
        if (rows.Count == 0 && sets.Count == 0) return null;

        int skippedCount = 0;
        var displays = new List<ArmorDisplayEntry>();
        var models = new List<MpqMember>();
        var textures = new List<MpqMember>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rows)
        {
            var kind = Enum.TryParse<ArmorRenderKind>(r.RenderKind, out var k) ? k : ArmorRenderKind.Painted;
            var componentStems = new Dictionary<int, string>();

            foreach (var c in r.Components)
            {
                componentStems[c.Slot] = c.ComponentStem;
                if (c.Blp is { Length: > 0 } && seen.Add(c.MpqPath)) textures.Add(new MpqMember { MpqPath = c.MpqPath, Data = c.Blp });
            }
            if (r.ModelTextureBlp is { Length: > 0 } && !string.IsNullOrEmpty(r.TextureMpqPath) && seen.Add(r.TextureMpqPath!))
                textures.Add(new MpqMember { MpqPath = r.TextureMpqPath!, Data = r.ModelTextureBlp });
            int modelBytesPacked = 0;
            foreach (var m in r.Models)
                if (m.M2 is { Length: > 0 })
                {
                    modelBytesPacked++;
                    if (seen.Add(m.MpqPath))
                        (m.MpqPath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) ? models : textures).Add(new MpqMember { MpqPath = m.MpqPath, Data = m.M2 });
                }

            // A Modelled piece (helm/shoulder) whose model bytes are all missing must not get a
            // display row: the row would name an .mdx this archive does not contain, and because
            // patch-6 owns the table the client reads, that publishes a guaranteed error model. The
            // weapon lane already splits these into a skipped bucket; do the same here rather than
            // shipping art-less rows. Modelled ONLY — Painted pieces paint the body atlas and Cloak
            // rides the built-in cape geoset, so neither has a model and neither is affected.
            if (kind == ArmorRenderKind.Modelled && modelBytesPacked == 0)
            {
                _logger.LogWarning(
                    "ArmorForge: display {DisplayId} is a {Kind} piece with no stored model bytes — omitted from " +
                    "{Patch} entirely (no member, no display row). Delete it from the Forged Armor list and re-import it.",
                    r.DisplayId, kind, patchCeilingName ?? PatchFileName);
                diag.Warn("armor.skipped",
                    $"armor display {r.DisplayId} is a {kind} piece with no stored model bytes — omitted entirely " +
                    "(no member, no display row). Delete it from the Forged Armor list and re-import it.");
                skippedCount++;
                continue;
            }

            displays.Add(new ArmorDisplayEntry
            {
                RenderKind = kind,
                Params = new ArmorDisplayInfoParams
                {
                    DisplayId = (uint)r.DisplayId,
                    ModelName = string.IsNullOrEmpty(r.ModelName) ? null : r.ModelName,
                    ModelName2 = string.IsNullOrEmpty(r.ModelName2) ? null : r.ModelName2,
                    TextureName = string.IsNullOrEmpty(r.TextureName) ? null : r.TextureName,
                    IconStem = r.IconStem ?? "",
                    GroupSoundIndex = (uint)r.GroupSound,
                    GeosetGroup0 = r.Geoset0, GeosetGroup1 = r.Geoset1, GeosetGroup2 = r.Geoset2,
                    HelmetVis0 = (uint)r.HelmetVis0, HelmetVis1 = (uint)r.HelmetVis1,
                    ComponentStems = componentStems.Count > 0 ? componentStems : null,
                },
            });
        }

        // Icon self-heal (2026-08-24): pieces imported while a deployed forge patch already carried
        // their icon skipped packaging (AttachIcon's stock check used to read the forge's own
        // patches), so those registry rows store no icon member and every rebuild shipped a patch
        // without the icon — invisible bag icons. On each assembly, any registered icon stem that is
        // neither stock-vanilla (custom patches excluded) nor already among the members is re-pulled
        // from a mounted import lane, added to this patch, and persisted to custom_armor_model so
        // the repair is durable.
        int stockCeiling = Mpq.MpqPatchOrder.Rank("patch-2.MPQ");
        foreach (var r in rows)
        {
            string stem = r.IconStem ?? "";
            if (stem.Length == 0) continue;
            string iconMember = $@"Interface\Icons\{stem}.blp";
            if (seen.Contains(iconMember)) continue;
            if (_mpq.ExtractFile(iconMember, skipArchive: n => Mpq.MpqPatchOrder.Rank(n) > stockCeiling) is not null)
                continue; // genuinely stock — the client resolves it without us
            byte[]? iconBlp = null;
            foreach (var lane in _lanes.All)
            {
                try { iconBlp = lane.Catalog.ExtractFile(iconMember); } catch { iconBlp = null; }
                if (iconBlp is { Length: > 0 }) break;
            }
            if (iconBlp is not { Length: > 0 } || WeaponAssetCompiler.ValidateBlp2Envelope(iconBlp) is not null)
            {
                _logger.LogWarning("ArmorForge: display {DisplayId} icon '{Stem}' is neither stock, stored, nor recoverable from a mounted import lane — its bag icon stays blank",
                    r.DisplayId, stem);
                continue;
            }
            seen.Add(iconMember);
            textures.Add(new MpqMember { MpqPath = iconMember, Data = iconBlp });
            try
            {
                await using var conn = _db.Admin();
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    @"INSERT INTO custom_armor_model (display_id, mpq_path, compiled_m2, created_at) VALUES (@DisplayId, @MpqPath, @Data, NOW())",
                    new { r.DisplayId, MpqPath = iconMember, Data = iconBlp });
                _logger.LogInformation("ArmorForge: repaired missing icon '{Stem}' for display {DisplayId} from a mounted import lane", stem, r.DisplayId);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "ArmorForge: icon '{Stem}' shipped in this patch but could not be persisted", stem); }
        }

        byte[]? baseItemSet = null;
        bool setsOmitted = false;
        if (sets.Count > 0)
        {
            // Beneath the archive we are packing INTO, by rank. Matching on the name "patch-6" was
            // only ever correct while this lane owned that one file; ranking keeps it correct when
            // the contribution is packed into the unified patch instead.
            int ceiling = Mpq.MpqPatchOrder.Rank(patchCeilingName ?? PatchFileName);
            baseItemSet = _mpq.ExtractFile(ArmorNaming.ItemSetMember,
                skipArchive: n => Mpq.MpqPatchOrder.Rank(n) >= ceiling);
            if (baseItemSet is null || baseItemSet.Length == 0)
            {
                _logger.LogWarning("ArmorForge: base ItemSet.dbc unreadable — {Count} set(s) omitted from this build", sets.Count);
                diag.Warn("armor.sets.omitted",
                    $"Base ItemSet.dbc could not be read, so {sets.Count} forged set(s) ship no bonuses this build.");
                sets = new List<ArmorSetDefinition>();
                setsOmitted = true;
            }
        }

        return new UnifiedPatch.ArmorLaneContribution
        {
            Displays = displays,
            // One list out; the standalone builder splits it back by extension exactly the way the
            // gather loop decided which bucket each member belonged in.
            Members = models.Concat(textures).ToArray(),
            Sets = sets,
            CleanItemSetDbc = baseItemSet,
            SetsOmitted = setsOmitted,
            SkippedCount = skippedCount,
        };
    }

    /// <summary>Deploy through the UNIFIED patch. Every lane that writes ItemDisplayInfo.dbc now
    /// ships in one archive, so this lane no longer places a file of its own: it asks the unified
    /// service to repackage all lanes, deploy that, and retire the superseded per-lane archives.
    ///
    /// Scoped resolve, not injection: UnifiedPatchService depends on the scoped ItemRetextureService
    /// and this service is a singleton, so it must open a scope. Best-effort like the deploy it
    /// replaces — a packaging failure is reported, never thrown into an import flow.</summary>
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

    /// <summary>Record a registry change WITHOUT rebuilding or deploying the unified patch. Every
    /// import used to repack all three lanes and deploy — one item at a time, failing whenever the
    /// game client was running. Now the operator forges a batch and ships it with one Rebuild patch
    /// click. Shaped like a deploy result so the apply rows can show it, with <c>Queued</c> so the
    /// UI renders "pending" rather than a failed deploy.</summary>
    private (bool Ok, bool Queued, int Pending, string Message) QueueUnifiedRebuild(string reason)
    {
        try
        {
            using var scope = _services.CreateScope();
            if (scope.ServiceProvider.GetService(typeof(UnifiedPatch.UnifiedPatchService))
                is not UnifiedPatch.UnifiedPatchService unified)
                return (false, false, 0, "unified patch service unavailable — nothing queued");
            int pending = unified.QueueChange("armor", reason);
            return (true, true, pending,
                $"rebuild queued — {pending} change(s) pending; close WoW and click Rebuild patch when you are done");
        }
        catch (Exception ex)
        {
            return (false, false, 0, $"could not queue the patch rebuild ({ex.Message}) — click Rebuild patch yourself");
        }
    }

    /// <summary>Queue, shaped exactly like <see cref="DeployUnifiedAsync"/> for the paths that take either.</summary>
    private (bool Ok, string Message) QueueAsDeployResult(string reason)
    {
        var q = QueueUnifiedRebuild(reason);
        return (q.Ok, q.Message);
    }

    /// <summary>Standalone patch-6 assembly — this lane packing its own archive, as it did before the
    /// unified patch existed. Kept so the Armor Forge can still build in isolation; the unified
    /// service calls <see cref="GetPatchContributionAsync"/> and packs one archive for all lanes.</summary>
    public async Task<ArmorPatchResult?> AssembleUnifiedPatchAsync()
    {
        var diag = new ForgeDiagnostics("armor-assemble");
        // Same ceiling as the unified build: our own previously packaged sets must never be read
        // back as "base", or a released set id collides with its own ghost in the mounted patch.
        var contribution = await GetPatchContributionAsync(diag, UnifiedPatch.UnifiedPatchService.PatchFileName);
        if (contribution is null) return null;

        byte[] baseDbc = ResolveBaseDbc();
        var baseReader = DbcWriterService.ReadDbc(baseDbc, ArmorNaming.ItemDisplayInfoMember);
        var customIds = contribution.Displays.Select(d => d.Params.DisplayId).ToHashSet();
        int replaced = baseReader.RemoveRowsWhere(id => customIds.Contains(id));
        byte[] cleanedBase = replaced > 0 ? baseReader.Write() : baseDbc;

        static bool IsModel(MpqMember m) => m.MpqPath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase);
        var input = new ArmorPatchInput
        {
            CleanItemDisplayInfoDbc = cleanedBase,
            Displays = contribution.Displays,
            Models = contribution.Members.Where(IsModel).ToArray(),
            Textures = contribution.Members.Where(m => !IsModel(m)).ToArray(),
            Sets = contribution.Sets,
            CleanItemSetDbc = contribution.CleanItemSetDbc,
            SetsOmitted = contribution.SetsOmitted,
        };
        string tempDir = Path.Combine(Path.GetTempPath(), "armorforge", Guid.NewGuid().ToString("N")[..8]);
        return _patch.Build(input, tempDir);
    }

    /// <param name="reason">Free text for the log and audit row.</param>
    /// <param name="deploy">True (the Rebuild patch button, the weapon/retexture cascade) repacks the
    /// unified patch and deploys it. False (deletes) repacks this lane's own artifact and the server
    /// ItemSet.dbc only, and QUEUES the unified rebuild for the next Rebuild patch click.</param>
    public async Task<string> RebuildPatchAsync(string reason, bool deploy = true)
    {
        try
        {
            var patch = await AssembleUnifiedPatchAsync();
            if (patch is null)
            {
                var removal = RemoveDeployedPatch();
                var restore = RestoreServerItemSetIfEmpty();
                // The unified archive still carries this lane's old rows until it is repacked.
                var unifiedEmpty = deploy
                    ? await DeployUnifiedAsync("armor registry emptied: " + reason)
                    : QueueAsDeployResult("armor registry emptied: " + reason);

                // This is the most consequential unlogged path in either forge. With no armor left in
                // the registry it deletes patch-6 out of the live client AND copies the .vanilla
                // sidecar back over the RUNNING SERVER's ItemSet.dbc — and it is reached not only from
                // the Armor Forge but from every weapon forge, weapon delete and retexture rebuild,
                // which all cascade into here. The server keeps the old DBC in memory until it is
                // restarted, so the change is invisible until it very suddenly is not.
                //
                // Logged only when a file actually moved: on an armor-less install this branch runs
                // on every one of those cascades, and a row per no-op would bury the one that did.
                if (removal.Changed || restore.Changed)
                    await LogPatchAuditAsync("patch_remove", reason,
                        ok: removal.Ok,
                        notes: "No armor left in the registry, so patch-6 was removed from the client " +
                               $"({removal.Message}) and the live server's ItemSet.dbc was RESTORED to stock from the .vanilla sidecar ({restore.Message}). " +
                               $"Triggered by: {reason}. The server keeps the previous ItemSet.dbc in memory until mangosd is restarted.",
                        extra: new { patchRemoved = removal.Changed, clientRemoval = removal.Message, serverItemSetRestore = restore.Message });

                return $"no armor in registry — client patch: {unifiedEmpty.Message}";
            }
            WriteCanonicalPatch(patch);
            var dep = deploy
                ? await DeployUnifiedAsync("armor patch rebuild: " + reason)
                : QueueAsDeployResult("armor patch rebuild: " + reason);
            var setDeploy = DeployItemSetToServer(patch);
            if (setDeploy.State == ItemSetDeployState.Failed)
                _logger.LogWarning("ArmorForge: server ItemSet.dbc NOT deployed — {Msg}", setDeploy.Message);
            _logger.LogInformation("ArmorForge: rebuilt armor patch ({Reason}) — {Msg}", reason, dep.Message);

            await LogPatchAuditAsync("patch_rebuild", reason,
                ok: dep.Ok && setDeploy.State != ItemSetDeployState.Failed,
                notes: $"armor patch repackaged; client patch: {dep.Message}. " +
                       $"Server ItemSet.dbc: {setDeploy.State} — {setDeploy.Message}. Triggered by: {reason}." +
                       (setDeploy.State == ItemSetDeployState.Deployed
                           ? " mangosd must be restarted before it reads the new ItemSet.dbc."
                           : ""),
                extra: new { patchRemoved = false, clientDeploy = dep.Message, serverItemSetState = setDeploy.State.ToString(), serverItemSetMessage = setDeploy.Message });

            return setDeploy.State == ItemSetDeployState.NotNeeded
                ? dep.Message
                : $"{dep.Message}. Server sets: {setDeploy.Message}";
        }
        catch (Exception ex)
        {
            await LogPatchAuditAsync("patch_rebuild", reason, ok: false,
                notes: $"patch-6 rebuild FAILED: {ex.Message}. Triggered by: {reason}. " +
                       "The deployed patch and the server's ItemSet.dbc are whatever the previous build left.",
                extra: new { error = ex.Message });
            throw;
        }
    }

    /// <summary>One row per patch-6 write or removal. patch-6 is a file in the running client's Data
    /// folder and its ItemSet.dbc is a file in the running server's dbc folder, so every write —
    /// whoever triggered it — belongs in the trail.</summary>
    private Task LogPatchAuditAsync(string action, string reason, bool ok, string notes, object extra) =>
        _audit.LogAsync(new AuditEntry
        {
            Category = "armorforge",
            Action = action,
            TargetType = "patch",
            TargetName = "patch-6.MPQ",
            StateAfter = JsonSerializer.Serialize(new
            {
                reason,
                patch = "patch-6.MPQ",
                clientDataPath = ClientDataPath,
                serverDbcPath = ServerDbcPath,
                detail = extra,
            }),
            IsReversible = false,
            RevertKind = RevertKind.None,
            Success = ok,
            Notes = notes,
        });

    // ═══════════════════════════════════════════════════════════════════
    // DELETE
    // ═══════════════════════════════════════════════════════════════════

    public Task<ArmorDeleteResult> DeleteAsync(long displayId) => DeleteAsync(displayId, rebuild: true);

    /// <param name="rebuild">Repackage patch-6 and reload the world table after this piece. Set false
    /// when deleting a whole set, so the (expensive) rebuild happens ONCE at the end instead of once
    /// per piece — the same contract <see cref="ImportAsync(ArmorImportLane, uint, string?, int, bool)"/>
    /// uses for set imports.</param>
    public async Task<ArmorDeleteResult> DeleteAsync(long displayId, bool rebuild)
    {
        long? entry;
        string? pieceName = null;
        IDictionary<string, object>? registrySnapshot = null;
        try
        {
            await using (var conn = _db.Admin())
            {
                await conn.OpenAsync();
                // Read the whole registry row, not just the entry id. These four DELETEs destroy the
                // only copy of the compiled M2/BLP bytes, so what the row said is the only record
                // there will ever be of what was deleted.
                var reg = await conn.QueryFirstOrDefaultAsync(
                    "SELECT * FROM custom_armor_display WHERE display_id=@displayId", new { displayId });
                if (reg is null) return new ArmorDeleteResult { Ok = false, NotFound = true, Message = $"no forged armor with display {displayId}" };
                registrySnapshot = StripBlobs((IDictionary<string, object>)reg);
                entry = registrySnapshot.TryGetValue("item_entry", out var e) && e is not null ? (long?)Convert.ToInt64(e) : null;
                pieceName = registrySnapshot.TryGetValue("name", out var n) ? n as string : null;
                if (entry is null) return new ArmorDeleteResult { Ok = false, NotFound = true, Message = $"no forged armor with display {displayId}" };

                await conn.ExecuteAsync("DELETE FROM custom_armor_component WHERE display_id=@displayId", new { displayId });
                await conn.ExecuteAsync("DELETE FROM custom_armor_model WHERE display_id=@displayId", new { displayId });
                await conn.ExecuteAsync("DELETE FROM custom_armor_display WHERE display_id=@displayId", new { displayId });
            }

            // The world row goes next, so snapshot it while it still exists.
            var itemRowSnapshot = await ReadItemRowAsync(entry.Value);

            await _ids.ReleaseAsync(WeaponIdReservationService.KindItemDisplay, displayId);
            await _ids.ReleaseAsync(WeaponIdReservationService.KindItemEntry, entry.Value);
            var del = await DeleteItemRowAsync(entry.Value);
            string reloadMessage = "(deferred to the set delete)", patchMessage = "(deferred to the set delete)";
            if (rebuild)
            {
                reloadMessage = (await ReloadItemTemplateAsync()).Message;
                patchMessage = await RebuildPatchAsync("delete", deploy: false);
            }
            await _audit.LogAsync(new AuditEntry
            {
                Category = "armorforge", Action = "delete", TargetType = "item",
                TargetName = pieceName, TargetId = checked((int)entry.Value), Success = del.Ok,
                RaCommand = rebuild ? ".reload item_template" : null,
                RaResponse = rebuild ? reloadMessage : null,
                StateBefore = JsonSerializer.Serialize(new { registry = registrySnapshot, itemTemplate = itemRowSnapshot }),
                Notes = $"Deleted forged armor display {displayId} / entry {entry}. World: {del.Message}. Reload: {reloadMessage}. Patch: {patchMessage}. " +
                        (itemRowSnapshot is null
                            ? "No item_template row was found to snapshot."
                            : "The destroyed registry and item_template rows are captured in state_before (compiled bytes excluded)."),
                IsReversible = false,
            });
            return new ArmorDeleteResult { Ok = true, Message = $"deleted display {displayId} / entry {entry}." + (rebuild ? $" {patchMessage}" : "") };
        }
        catch (Exception ex)
        {
            await _audit.LogAsync(new AuditEntry
            {
                Category = "armorforge", Action = "delete", TargetType = "item",
                TargetName = pieceName, Success = false, IsReversible = false, RevertKind = RevertKind.None,
                StateBefore = registrySnapshot is null ? null : JsonSerializer.Serialize(new { registry = registrySnapshot }),
                Notes = $"Delete of forged armor display {displayId} FAILED: {ex.Message} — registry rows go first, " +
                        "then the ids, then the world row, then the patch, so the piece may be partly removed.",
            });
            throw;
        }
    }

    /// <summary>Drop the compiled-bytes columns before a registry row goes into an audit snapshot.
    /// A single forged piece carries multi-megabyte BLP/M2 blobs; state_before is for reading, and
    /// the blobs are gone with the row either way.</summary>
    private static IDictionary<string, object> StripBlobs(IDictionary<string, object> row) =>
        row.Where(kv => kv.Value is not byte[])
           .ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>The whole item_template row, for the audit trail's state_before — schema-agnostic, and
    /// best-effort so a delete is never blocked by a snapshot that could not be taken.</summary>
    private async Task<IDictionary<string, object>?> ReadItemRowAsync(long entry)
    {
        try
        {
            await using var conn = _db.Mangos();
            await conn.OpenAsync();
            // ORDER BY patch DESC — item_template is keyed (entry, patch); see the weapon forge's twin.
            var row = await conn.QueryFirstOrDefaultAsync(
                "SELECT * FROM item_template WHERE entry = @entry ORDER BY patch DESC LIMIT 1", new { entry });
            return row is null ? null : (IDictionary<string, object>)row;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArmorForge: snapshot of item_template {Entry} failed (delete continues)", entry);
            return null;
        }
    }

    /// <summary>
    /// Delete every piece of one of OUR sets, then the set row.
    ///
    /// Order and batching both matter here, and both used to be wrong:
    ///
    ///   • <b>The set row goes LAST.</b> It used to be deleted first, before a single piece was
    ///     touched. The registry groups pieces by joining them to <c>custom_armor_set</c>, so the
    ///     instant that row vanished every remaining piece fell out of its group into "Single pieces" —
    ///     the set was visibly broken up even though the pieces were still there. Deleting it last
    ///     means an interrupted delete leaves an intact, still-grouped, still-retryable set.
    ///   • <b>One patch rebuild, not one per piece.</b> Each <see cref="DeleteAsync(long)"/> repackaged
    ///     the whole of patch-6, redeployed it, rewrote ItemSet.dbc and issued a
    ///     <c>.reload item_template</c> over RA. On an eight-piece set that is eight full rebuilds and
    ///     eight RA round-trips — slow enough to blow the request budget partway through, which is how
    ///     "it deleted the first piece and broke up the set" happened.
    ///
    /// Per-piece failures are contained and reported instead of aborting the loop, and the set row
    /// (and its id) are only released when every piece actually went.
    /// </summary>
    /// <summary>Delete a selection of sets and single pieces with ONE world reload, ONE lane repack and
    /// one queued unified rebuild at the end. Sets go first (their pieces come with them); a piece that
    /// was ticked as well as its set is simply already gone by the time its turn comes.</summary>
    public async Task<ArmorBulkDeleteResult> DeleteManyAsync(IReadOnlyList<long> displayIds, IReadOnlyList<int> setIds)
    {
        var result = new ArmorBulkDeleteResult();
        foreach (int setId in setIds.Distinct())
        {
            try
            {
                var r = await DeleteSetAsync(setId, rebuild: false);
                result.Items.Add(new ArmorBulkDeleteItem { Kind = "set", Id = setId, Ok = r.Ok, Message = r.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ArmorForge: bulk delete of set {SetId} failed", setId);
                result.Items.Add(new ArmorBulkDeleteItem { Kind = "set", Id = setId, Ok = false, Message = ex.Message });
            }
        }
        foreach (long displayId in displayIds.Distinct())
        {
            try
            {
                var r = await DeleteAsync(displayId, rebuild: false);
                // Already gone (its set was deleted above) is the goal, not a failure.
                result.Items.Add(new ArmorBulkDeleteItem { Kind = "piece", Id = displayId, Ok = r.Ok || r.NotFound, Message = r.NotFound ? "already removed with its set" : r.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ArmorForge: bulk delete of display {DisplayId} failed", displayId);
                result.Items.Add(new ArmorBulkDeleteItem { Kind = "piece", Id = displayId, Ok = false, Message = ex.Message });
            }
        }

        int ok = result.Items.Count(i => i.Ok), failed = result.Items.Count - ok;
        // Nothing removed means nothing to reload or repack — and no rebuild to queue.
        var reload = ok > 0 ? await ReloadItemTemplateAsync() : (Ok: true, Message: "nothing deleted — no reload");
        string rebuild = ok > 0
            ? await RebuildPatchAsync($"deleted {ok} armor item(s) in one batch", deploy: false)
            : "nothing deleted — patch untouched";
        result.Ok = failed == 0;
        result.Message = $"deleted {ok} of {result.Items.Count} selected" + (failed > 0 ? $"; {failed} failed — see the list" : "")
            + $". Reload: {reload.Message}. {rebuild}";
        return result;
    }

    /// <param name="rebuild">Reload the world table and repack after the set. False when deleting a
    /// batch of sets and pieces, so that happens ONCE at the end (see <see cref="DeleteManyAsync"/>).</param>
    public async Task<ArmorDeleteResult> DeleteSetAsync(int setId, bool rebuild = true)
    {
        List<long> displays;
        string? setName;
        await using (var conn = _db.Admin())
        {
            await conn.OpenAsync();
            displays = (await conn.QueryAsync<long>("SELECT display_id FROM custom_armor_display WHERE set_id=@setId", new { setId })).ToList();
            setName = await conn.ExecuteScalarAsync<string?>("SELECT name FROM custom_armor_set WHERE set_id=@setId", new { setId });
        }
        // CLONED members have no registry row — the display list above cannot see them — so their only
        // record is item_template.set_id. Without this a cloned set deleted "successfully": the loop
        // below never ran, so `complete` stayed true, the set row was dropped and its id released, while
        // every cloned item stayed live stamped with a set id that had just been handed back to the pool.
        var clonedBySet = await ReadClonedSetMembersAsync(new[] { setId });
        var clonedEntries = clonedBySet.TryGetValue(setId, out var ce) ? ce : new List<int>();

        // Each piece writes its own delete row, and the patch rebuild at the end writes another.
        // The scope makes the whole unwind read as one run instead of a dozen loose deletions.
        using var batch = AuditBatch.Begin(
            $"Armor Forge — delete {(string.IsNullOrWhiteSpace(setName) ? $"set {setId}" : $"set {setId} '{setName}'")}");

        int deleted = 0;
        var failures = new List<string>();
        try
        {
            foreach (var d in displays)
            {
                try
                {
                    var piece = await DeleteAsync(d, rebuild: false);
                    if (piece.Ok) deleted++;
                    else if (piece.NotFound) deleted++;   // already gone — the goal, not a failure
                    else failures.Add($"display {d}: {piece.Message}");
                }
                catch (Exception ex)
                {
                    // One bad piece must not strand the rest half-deleted with no set row to regroup them.
                    _logger.LogError(ex, "ArmorForge: deleting display {DisplayId} of set {SetId} failed", d, setId);
                    failures.Add($"display {d}: {ex.Message}");
                }
            }

            // Cloned pieces: nothing to unpackage, so this is just the item row and its reserved id.
            int clonedDeleted = 0;
            foreach (int entry in clonedEntries)
            {
                var del = await DeleteItemRowAsync(entry);
                if (del.Ok)
                {
                    clonedDeleted++;
                    await _ids.ReleaseAsync(WeaponIdReservationService.KindItemEntry, entry);
                }
                else failures.Add($"cloned item {entry}: {del.Message}");
            }
            deleted += clonedDeleted;

            bool complete = failures.Count == 0;
            if (complete)
            {
                await using var conn = _db.Admin();
                await conn.OpenAsync();
                await conn.ExecuteAsync("DELETE FROM custom_armor_set WHERE set_id=@setId", new { setId });
                await _ids.ReleaseAsync(KindSet, setId);
            }

            // Now, once, for the whole set (or once for the whole batch, when the caller defers it).
            var reload = rebuild ? await ReloadItemTemplateAsync() : (Ok: true, Message: "deferred to the end of the batch");
            string rebuildMsg = rebuild ? await RebuildPatchAsync($"delete set {setId}", deploy: false) : "patch repack deferred to the end of the batch";

            string label = string.IsNullOrWhiteSpace(setName) ? $"set {setId}" : $"set {setId} '{setName}'";
            int totalPieces = displays.Count + clonedEntries.Count;
            string message = complete
                ? $"deleted {label} and all {deleted} piece(s)"
                    + (clonedEntries.Count > 0 ? $" ({clonedEntries.Count} cloned)" : "") + $". {rebuildMsg}"
                : $"deleted {deleted}/{totalPieces} piece(s) of {label}; {failures.Count} failed, so the set was KEPT so you can retry: {string.Join("; ", failures.Take(4))}. {rebuildMsg}";

            await _audit.LogAsync(new AuditEntry
            {
                Category = "armorforge", Action = "delete_set", TargetType = "itemset", TargetId = setId,
                TargetName = setName, Success = complete,
                RaCommand = ".reload item_template", RaResponse = reload.Message,
                StateBefore = JsonSerializer.Serialize(new { setId, setName, memberDisplayIds = displays }),
                StateAfter = JsonSerializer.Serialize(new { setRowDeleted = complete, piecesDeleted = deleted, failures }),
                Notes = $"{message} Reload: {reload.Message}.",
                IsReversible = false,
            });
            return new ArmorDeleteResult { Ok = complete, Message = message };
        }
        catch (Exception ex)
        {
            await _audit.LogAsync(new AuditEntry
            {
                Category = "armorforge", Action = "delete_set", TargetType = "itemset", TargetId = setId,
                TargetName = setName, Success = false, IsReversible = false, RevertKind = RevertKind.None,
                StateBefore = JsonSerializer.Serialize(new { setId, setName, memberDisplayIds = displays }),
                StateAfter = JsonSerializer.Serialize(new { piecesDeleted = deleted, failures }),
                Notes = $"Set delete FAILED after {deleted}/{displays.Count} piece(s): {ex.Message} — " +
                        "the set row is kept when anything fails, so the delete can be retried.",
            });
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // DB LOADERS
    // ═══════════════════════════════════════════════════════════════════

    public sealed class ArmorRow
    {
        public long DisplayId { get; set; }
        public long ItemEntry { get; set; }
        public int SetId { get; set; }
        public string RenderKind { get; set; } = "Painted";
        public string ArmorTypeKey { get; set; } = "";
        public int Material { get; set; }
        public int InventoryType { get; set; }
        public string Name { get; set; } = "";
        public string? IconStem { get; set; }
        public string? ModelName { get; set; }
        public string? ModelName2 { get; set; }
        public string? TextureName { get; set; }
        public string? TextureMpqPath { get; set; }
        public byte[]? ModelTextureBlp { get; set; }
        public int Geoset0 { get; set; }
        public int Geoset1 { get; set; }
        public int Geoset2 { get; set; }
        public int HelmetVis0 { get; set; }
        public int HelmetVis1 { get; set; }
        public int GroupSound { get; set; }
        public List<ArmorComponentRow> Components { get; set; } = new();
        public List<ArmorModelRow> Models { get; set; } = new();
    }
    public sealed class ArmorComponentRow { public int Slot; public string GenderSuffix = "_U"; public string MpqPath = ""; public string ComponentStem = ""; public byte[]? Blp; }
    public sealed class ArmorModelRow { public string MpqPath = ""; public byte[]? M2; }

    // ── ICustomMpqMemberSource ─────────────────────────────────────────────────────────────
    // Previews resolve a forged piece's helm/shoulder M2 and BLPs through MpqReaderService; until the
    // next Rebuild patch those bytes exist only here. One row per path, cached briefly so a dressing
    // pass (a dozen texture reads) is one query per member, not one per read.
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
            data = conn.ExecuteScalar<byte[]?>("SELECT compiled_m2 FROM custom_armor_model WHERE mpq_path = @p LIMIT 1", new { p = mpqPath })
                ?? conn.ExecuteScalar<byte[]?>("SELECT compiled_blp FROM custom_armor_component WHERE mpq_path = @p LIMIT 1", new { p = mpqPath })
                ?? conn.ExecuteScalar<byte[]?>("SELECT compiled_blp FROM custom_armor_display WHERE texture_mpq_path = @p LIMIT 1", new { p = mpqPath });
        }
        catch (Exception ex) { _logger.LogDebug(ex, "ArmorForge: registry member lookup failed for {Path}", mpqPath); }
        _memberCache[mpqPath] = (DateTime.UtcNow, data);
        return data;
    }

    public async Task<List<ArmorRow>> LoadArmorRowsAsync(bool includeBlobs = true)
    {
        var list = new List<ArmorRow>();
        try
        {
            await using var conn = _db.Admin();
            await conn.OpenAsync();
            string blobCol = includeBlobs ? "compiled_blp" : "NULL AS compiled_blp";
            var rows = (await conn.QueryAsync(
                $@"SELECT display_id, item_entry, set_id, render_kind, armor_type_key, material, inventory_type, name, icon_stem,
                          model_name, model_name2, texture_name, texture_mpq_path, {blobCol}, geoset0, geoset1, geoset2,
                          helmet_vis0, helmet_vis1, group_sound
                   FROM custom_armor_display ORDER BY display_id")).ToList();
            foreach (var r in rows)
            {
                list.Add(new ArmorRow
                {
                    DisplayId = Convert.ToInt64(r.display_id), ItemEntry = Convert.ToInt64(r.item_entry),
                    SetId = Convert.ToInt32(r.set_id ?? 0), RenderKind = (string?)r.render_kind ?? "Painted",
                    ArmorTypeKey = (string?)r.armor_type_key ?? "", Material = Convert.ToInt32(r.material ?? 0),
                    InventoryType = Convert.ToInt32(r.inventory_type ?? 0), Name = (string?)r.name ?? "",
                    IconStem = (string?)r.icon_stem, ModelName = (string?)r.model_name, ModelName2 = (string?)r.model_name2,
                    TextureName = (string?)r.texture_name, TextureMpqPath = (string?)r.texture_mpq_path,
                    ModelTextureBlp = r.compiled_blp as byte[],
                    Geoset0 = Convert.ToInt32(r.geoset0 ?? 0), Geoset1 = Convert.ToInt32(r.geoset1 ?? 0), Geoset2 = Convert.ToInt32(r.geoset2 ?? 0),
                    HelmetVis0 = Convert.ToInt32(r.helmet_vis0 ?? 0), HelmetVis1 = Convert.ToInt32(r.helmet_vis1 ?? 0),
                    GroupSound = Convert.ToInt32(r.group_sound ?? 0),
                });
            }
            var byId = list.ToDictionary(a => a.DisplayId);

            string cBlob = includeBlobs ? "compiled_blp" : "NULL AS compiled_blp";
            var comps = (await conn.QueryAsync($"SELECT display_id, slot, gender_suffix, mpq_path, component_stem, {cBlob} FROM custom_armor_component ORDER BY display_id, slot")).ToList();
            foreach (var c in comps)
                if (byId.TryGetValue((long)Convert.ToInt64(c.display_id), out ArmorRow? ar))
                    ar.Components.Add(new ArmorComponentRow { Slot = Convert.ToInt32(c.slot), GenderSuffix = (string?)c.gender_suffix ?? "_U", MpqPath = (string?)c.mpq_path ?? "", ComponentStem = (string?)c.component_stem ?? "", Blp = c.compiled_blp as byte[] });

            string mBlob = includeBlobs ? "compiled_m2" : "NULL AS compiled_m2";
            var mods = (await conn.QueryAsync($"SELECT display_id, mpq_path, {mBlob} FROM custom_armor_model ORDER BY display_id, mpq_path")).ToList();
            foreach (var m in mods)
                if (byId.TryGetValue((long)Convert.ToInt64(m.display_id), out ArmorRow? ar))
                    ar.Models.Add(new ArmorModelRow { MpqPath = (string?)m.mpq_path ?? "", M2 = m.compiled_m2 as byte[] });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ArmorForge: LoadArmorRowsAsync failed"); }
        return list;
    }

    private async Task<List<ArmorSetDefinition>> LoadSetsAsync(List<ArmorRow> rows)
    {
        var sets = new List<ArmorSetDefinition>();
        try
        {
            await using var conn = _db.Admin();
            await conn.OpenAsync();
            var setRows = (await conn.QueryAsync("SELECT set_id, name, bonuses_json, req_skill, req_skill_rank FROM custom_armor_set")).ToList();

            // A set has two kinds of member and only one of them is in the registry. An IMPORTED piece
            // has a custom_armor_display row (it ships art). A CLONED piece has none — a clone reuses the
            // source's display and writes nothing but an item_template row — so its membership exists
            // ONLY as item_template.set_id. Without this union the patch builder saw a cloned set with
            // zero members, dropped it, and shipped an ItemSet.dbc with no row for it; the core then
            // zeroes the very set_id the clone just wrote (ARMOR_FORGE.md §10) and the set is inert.
            var setIdList = new List<int>();
            foreach (var x in setRows) setIdList.Add(Convert.ToInt32(x.set_id));
            var clonedMembers = await ReadClonedSetMembersAsync(setIdList);

            foreach (var s in setRows)
            {
                int setId = Convert.ToInt32(s.set_id);
                var members = rows.Where(r => r.SetId == setId).Select(r => (int)r.ItemEntry)
                    .Union(clonedMembers.TryGetValue(setId, out var cloned) ? cloned : Enumerable.Empty<int>())
                    .Distinct().ToList();
                if (members.Count == 0) continue;
                var bonuses = new List<ArmorSetBonus>();
                try { bonuses = JsonSerializer.Deserialize<List<ArmorSetBonus>>((string?)s.bonuses_json ?? "[]") ?? new(); } catch { }
                sets.Add(new ArmorSetDefinition
                {
                    SetId = setId, Name = (string?)s.name ?? $"Set {setId}", ItemEntries = members, Bonuses = bonuses,
                    RequiredSkill = Convert.ToInt32(s.req_skill ?? 0), RequiredSkillRank = Convert.ToInt32(s.req_skill_rank ?? 0),
                });
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ArmorForge: LoadSetsAsync failed"); }
        return sets;
    }

    /// <summary>Item entries that belong to a forged set purely by <c>item_template.set_id</c> — i.e.
    /// the CLONED pieces, which have no registry row. Scoped to the custom entry range so a stock item
    /// that happens to carry a colliding set_id can never be swept into a forged set.</summary>
    private async Task<Dictionary<int, List<int>>> ReadClonedSetMembersAsync(IReadOnlyCollection<int> setIds)
    {
        var bySet = new Dictionary<int, List<int>>();
        if (setIds.Count == 0) return bySet;
        try
        {
            await using var conn = _db.Mangos();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(
                @"SELECT entry, set_id AS SetId FROM item_template
                  WHERE set_id IN @setIds
                    AND entry >= @customFloor
                    AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = item_template.entry)
                  ORDER BY entry",
                new { setIds = setIds.ToArray(), customFloor = WeaponIdReservationService.ItemEntryFloor });
            foreach (var r in rows)
            {
                int setId = Convert.ToInt32(r.SetId);
                if (!bySet.TryGetValue(setId, out var list)) bySet[setId] = list = new List<int>();
                list.Add(Convert.ToInt32(r.entry));
            }
        }
        catch (Exception ex)
        {
            // The world DB is a separate failure domain from the registry. A set whose members are all
            // imported must still package if this read fails.
            _logger.LogWarning(ex, "ArmorForge: reading cloned set members failed; cloned pieces may be missing from patch-6");
        }
        return bySet;
    }

    public async Task<List<ArmorSetSummary>> ListSetsAsync()
    {
        var rows = await LoadArmorRowsAsync(includeBlobs: false);
        var sets = await LoadSetsAsync(rows);
        return sets.Select(s => new ArmorSetSummary
        {
            SetId = s.SetId, Name = s.Name, MemberCount = s.ItemEntries.Count, BonusCount = s.Bonuses.Count,
            Bonuses = s.Bonuses.ToList(), MemberEntries = s.ItemEntries.ToList(),
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════
    // APPLY / DEPLOY
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
            _logger.LogWarning(ex, "ArmorForge: item_template insert for entry {Entry} failed", entry);
            return (false, $"world DB insert failed: {ex.Message} — item_template.sql is in the build folder for manual apply");
        }
    }

    private async Task<(bool Ok, string Message)> DeleteItemRowAsync(long entry)
    {
        try
        {
            await using var conn = _db.Mangos();
            await conn.OpenAsync();
            int rows = await conn.ExecuteAsync("DELETE FROM item_template WHERE entry=@entry", new { entry });
            return (true, rows > 0 ? $"item_template row {entry} deleted" : $"no item_template row {entry} existed");
        }
        catch (Exception ex) { return (false, $"world DB delete failed: {ex.Message}"); }
    }

    private async Task<(bool Ok, string Message)> ReloadItemTemplateAsync()
    {
        try
        {
            var response = await _ra.SendCommandAsync(".reload item_template");
            var trimmed = (response ?? "").Trim();
            return (true, trimmed.Length > 0 ? trimmed : "reload issued");
        }
        catch (Exception ex) { return (false, $"RA reload failed: {ex.Message} — run .reload item_template yourself"); }
    }

    private (bool Ok, string Message) DeployPatch(ArmorPatchResult? patch)
    {
        if (patch is null) return (false, "no patch built");
        var dataPath = ClientDataPath;
        if (dataPath is null) return (false, "no client Data path configured — copy the downloaded patch yourself");
        try
        {
            string target = Path.Combine(dataPath, PatchFileName);
            File.WriteAllBytes(target, patch.MpqBytes);
            return (true, $"deployed to {target}");
        }
        catch (Exception ex) { return (false, $"deploy failed ({ex.Message}) — the client is probably running; close it and click Rebuild patch"); }
    }

    /// <summary>
    /// Write the freshly built ItemSet.dbc into the SERVER's dbc directory. The client gets its copy
    /// inside patch-6; the server needs its own, because it loads DBCs from disk at startup and uses
    /// <c>sItemSetStore</c> to validate every <c>item_template.set_id</c> (see <see cref="ResolveServerDbcDir"/>).
    ///
    /// Tri-state on purpose: the old version returned Ok for "there was nothing to deploy", so a build
    /// that shipped no ItemSet.dbc at all reported the same success as one that deployed it.
    /// </summary>
    private (ItemSetDeployState State, string Message, string? Path) DeployItemSetToServer(ArmorPatchResult? patch)
    {
        if (patch is null) return (ItemSetDeployState.NotNeeded, "no patch built", null);
        return DeployItemSetToServer(patch.ItemSetDbcBytes, patch.ItemSetOmitted);
    }

    /// <summary>Deploy the built ItemSet.dbc to the RUNNING SERVER's dbc folder, by bytes rather than
    /// by patch object, so the unified patch can reuse it — the sets it ships are the same sets, and
    /// the core still zeroes every forged set_id without this file whatever archive shipped it.
    /// Overload, not a rewrite: the client-side member and the server-side file have always been two
    /// separate deliveries, and only the second one is what mangosd reads.</summary>
    internal (ItemSetDeployState State, string Message, string? Path) DeployItemSetToServer(
        byte[]? itemSetDbcBytes, bool itemSetOmitted)
    {
        if (itemSetOmitted)
            return (ItemSetDeployState.Failed,
                "sets exist but no ItemSet.dbc was built (the base ItemSet.dbc could not be read from the mounted archives) — the server will zero every forged set_id", null);
        if (itemSetDbcBytes is not { Length: > 0 } bytes)
            return (ItemSetDeployState.NotNeeded, "no forged sets — nothing to deploy", null);

        var (dir, detail) = ResolveServerDbcDir();
        if (dir is null)
            return (ItemSetDeployState.Failed,
                $"no server dbc directory resolved, so forged sets will NOT work in game (tried: {detail})", null);

        string target = Path.Combine(dir, "ItemSet.dbc");
        try
        {
            // First-write-wins stock sidecar, so the untouched vanilla file is always recoverable
            // (same contract as ServerDataService.BackupVanillaFile uses for maps/vmaps). Only ever
            // taken from a file that still LOOKS stock — enshrining a custom copy as ".vanilla" would
            // make the restore path put custom rows back forever.
            string vanilla = target + ".vanilla";
            if (File.Exists(target) && !File.Exists(vanilla) && LooksLikeStockItemSet(target)) File.Copy(target, vanilla);

            File.WriteAllBytes(target, bytes);

            // Read back and byte-compare: a short/locked write here is silent otherwise, and the
            // symptom (sets zeroed at startup) looks identical to not having deployed at all.
            var written = File.ReadAllBytes(target);
            if (written.Length != bytes.Length || !written.AsSpan().SequenceEqual(bytes))
                return (ItemSetDeployState.Failed, $"ItemSet.dbc written to {target} did not read back identical — deploy not trusted", target);

            return (ItemSetDeployState.Deployed, $"deployed ItemSet.dbc to {target} — RESTART mangosd for it to take effect", target);
        }
        catch (Exception ex) { return (ItemSetDeployState.Failed, $"server ItemSet.dbc deploy failed ({target}): {ex.Message}", target); }
    }

    /// <summary>Does this ItemSet.dbc still look like the shipped vanilla one? Stock 1.12 tops out around
    /// set id 551; forged sets start at <see cref="ArmorItemSetDbc.CustomSetIdFloor"/>. Used to make sure
    /// the ".vanilla" sidecar is only ever taken from a genuinely stock file.</summary>
    private bool LooksLikeStockItemSet(string path)
    {
        try
        {
            var reader = DbcWriterService.ReadDbc(File.ReadAllBytes(path), ArmorNaming.ItemSetMember);
            return reader.GetMaxId() < ArmorItemSetDbc.CustomSetIdFloor;
        }
        catch (Exception ex)
        {
            // Unreadable/unknown layout: do NOT claim it as the vanilla reference.
            _logger.LogWarning(ex, "ArmorForge: could not inspect {Path} to decide if it is stock", path);
            return false;
        }
    }

    /// <summary>Put the stock ItemSet.dbc back when the registry no longer has any forged set, so the
    /// server stops carrying orphan custom rows. Best-effort: never fails a rebuild. Returns what it
    /// did, because this overwrites a file in the RUNNING server and the audit row has to say so.</summary>
    private (bool Changed, string Message) RestoreServerItemSetIfEmpty()
    {
        var (dir, _) = ResolveServerDbcDir();
        if (dir is null) return (false, "no server dbc path configured — nothing restored");
        try
        {
            string target = Path.Combine(dir, "ItemSet.dbc");
            string vanilla = target + ".vanilla";
            if (!File.Exists(vanilla)) return (false, $"no stock sidecar at {vanilla} — {target} left as-is");
            // Only write when it would actually change something. This runs on every weapon forge and
            // every retexture rebuild through the cascade, and re-copying a byte-identical file over a
            // path the server holds open buys nothing but risk — and an audit row that cried wolf.
            if (File.Exists(target) && FilesMatch(target, vanilla))
                return (false, "server ItemSet.dbc is already stock — left alone");
            File.Copy(vanilla, target, overwrite: true);
            return (true, $"restored stock {target} from {Path.GetFileName(vanilla)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArmorForge: could not restore the stock server ItemSet.dbc");
            return (true, $"restore FAILED: {ex.Message}");
        }
    }

    private static bool FilesMatch(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch { return false; }   // unreadable ⇒ treat as different and let the copy decide
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
        catch (Exception ex) { return (false, true, $"could not remove deployed patch ({ex.Message})"); }
    }

    private void WriteOutputs(string buildId, ArmorPatchResult? patch, string? sql)
    {
        try
        {
            string dir = Path.Combine(ArtifactRoot, $"armor-build-{buildId}");
            Directory.CreateDirectory(dir);
            if (patch is not null) { File.WriteAllBytes(Path.Combine(dir, PatchFileName), patch.MpqBytes); WriteCanonicalPatch(patch); }
            if (sql is not null) File.WriteAllText(Path.Combine(dir, "item_template.sql"), sql);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ArmorForge: WriteOutputs failed for {Build}", buildId); }
    }

    private void WriteCanonicalPatch(ArmorPatchResult patch)
    {
        try
        {
            Directory.CreateDirectory(ArtifactRoot);
            File.WriteAllBytes(Path.Combine(ArtifactRoot, PatchFileName), patch.MpqBytes);
            // Keep the last-built ItemSet.dbc beside the patch so the status check can compare it to
            // the server's copy without repacking the whole MPQ on every page load.
            string canonicalSet = Path.Combine(ArtifactRoot, "ItemSet.dbc");
            if (patch.ItemSetDbcBytes is { Length: > 0 } setBytes) File.WriteAllBytes(canonicalSet, setBytes);
            else if (File.Exists(canonicalSet)) File.Delete(canonicalSet);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ArmorForge: WriteCanonicalPatch failed"); }
    }

    /// <summary>Path of the last-built ItemSet.dbc, or null when the registry has no sets.</summary>
    public string? CanonicalItemSetPath
    {
        get { var p = Path.Combine(ArtifactRoot, "ItemSet.dbc"); return File.Exists(p) ? p : null; }
    }

    public string? CanonicalPatchPath
    {
        get { var p = Path.Combine(ArtifactRoot, PatchFileName); return File.Exists(p) ? p : null; }
    }

    /// <summary>
    /// Is the SERVER's ItemSet.dbc the one we last built, and has mangosd been restarted since?
    ///
    /// Unlike the client patch this has a second failure mode beyond "stale": the core reads DBCs once,
    /// at startup (<c>World::SetInitialWorldSettings</c> → <c>LoadDBCStores</c>, and no <c>.reload</c>
    /// command touches a DBC store), so a correctly deployed file still does nothing until the world
    /// server is restarted. Until then every forged <c>set_id</c> is zeroed at load.
    /// </summary>
    public (bool Configured, bool Stale, DateTime? WrittenUtc, string Message) ServerItemSetStatus()
    {
        var (dir, detail) = ResolveServerDbcDir();
        if (dir is null) return (false, false, null, $"no server dbc directory resolved — forged sets cannot work in game (tried: {detail})");

        string target = Path.Combine(dir, "ItemSet.dbc");
        string? canonical = CanonicalItemSetPath;
        if (canonical is null)
            return (true, false, File.Exists(target) ? File.GetLastWriteTimeUtc(target) : null,
                $"no forged sets — server ItemSet.dbc untouched ({detail})");
        if (!File.Exists(target))
            return (true, true, null, $"forged sets exist but {target} is missing — click Rebuild patch");
        try
        {
            var a = File.ReadAllBytes(canonical); var b = File.ReadAllBytes(target);
            bool same = a.Length == b.Length && a.AsSpan().SequenceEqual(b);
            return (true, !same, File.GetLastWriteTimeUtc(target), same
                ? $"server ItemSet.dbc matches the last build ({target})"
                : $"server ItemSet.dbc is STALE — it does not match the last build; click Rebuild patch ({target})");
        }
        catch (Exception ex) { return (true, false, null, $"could not compare the server ItemSet.dbc: {ex.Message}"); }
    }

    /// <summary>The base ItemDisplayInfo.dbc: the mounted copy from strictly BENEATH patch-6, so
    /// patch-6 re-unions patch-4 (retextures) + patch-5 (weapons) instead of shadowing them.
    /// patch-6 sits at the top of the mount, so its table is the only one the client ever reads —
    /// it MUST carry every custom row from every lane, which is why weapon rows legitimately appear
    /// here. Skipping by RANK rather than by name keeps that a one-way chain (patch-4 → patch-5 →
    /// patch-6); see the note on WeaponForge's ResolveBaseDbc for the cycle this replaced.</summary>
    private byte[] ResolveBaseDbc()
    {
        // ONLY the armor key — never inherit WeaponForge:CleanDbcPath: that is the state WITHOUT
        // patch-5, and patch-6 (top of the mount) built on it would shadow every forged weapon.
        var cfgPath = _config["ArmorForge:CleanDbcPath"];
        if (!string.IsNullOrWhiteSpace(cfgPath) && File.Exists(cfgPath)) return File.ReadAllBytes(cfgPath);
        // Beneath the UNIFIED patch, not this lane's old patch-6: the forge's own rows live in
        // patch-4 now, and a base that includes them makes every re-used id "already exist" —
        // measured as "ItemSet id 5011 already exists in the base DBC" after a set was deleted and
        // the (on-request) rebuild had not yet replaced the client's copy.
        int myRank = Mpq.MpqPatchOrder.Rank(UnifiedPatch.UnifiedPatchService.PatchFileName);
        return _mpq.ExtractFile(ArmorNaming.ItemDisplayInfoMember, skipArchive: n => Mpq.MpqPatchOrder.Rank(n) >= myRank)
            ?? throw new InvalidOperationException("Could not extract a base ItemDisplayInfo.dbc from the mounted archives.");
    }
}

// ── DTOs ────────────────────────────────────────────────────────────────

public sealed class CustomArmorBuildResult
{
    public bool Ok { get; set; }
    /// <summary>The SOURCE item entry in the later client's catalog (TBC or WotLK — see
    /// <see cref="SourceExpansion"/>); the name predates the second lane.</summary>
    public uint TbcEntry { get; set; }
    /// <summary>"tbc" / "wotlk" — which import lane produced this piece.</summary>
    public string SourceExpansion { get; set; } = "tbc";
    public string ArmorTypeKey { get; set; } = "";
    public ArmorRenderKind RenderKind { get; set; }
    public long ItemEntry { get; set; }
    public long DisplayId { get; set; }
    public string Name { get; set; } = "";
    public string Sql { get; set; } = "";
    public string Message { get; set; } = "";
    public int ModelMemberCount { get; set; }
    public int ComponentCount { get; set; }
    public string[] Diagnostics { get; set; } = Array.Empty<string>();
    public ServerApplyStatus? Apply { get; set; }
}

public sealed class ArmorSetImportResult
{
    public int SetId { get; set; }
    public string Name { get; set; } = "";
    public List<CustomArmorBuildResult> Pieces { get; } = new();
    public bool PatchDeployed { get; set; }
    /// <summary>The client patch was not rebuilt; the change waits for the next Rebuild patch click.</summary>
    public bool PatchQueued { get; set; }
    public int PatchPending { get; set; }
    /// <summary>Whether the server's own ItemSet.dbc was written. False means the core will zero this
    /// set's <c>set_id</c> at load — no tooltip set block, no n-piece bonuses.</summary>
    public bool ServerItemSetDeployed { get; set; }
    public string ServerItemSetMessage { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ServerApplyStatus
{
    public bool SqlApplied { get; set; }
    public string SqlMessage { get; set; } = "";
    public bool Reloaded { get; set; }
    public string ReloadMessage { get; set; } = "";
    public bool PatchDeployed { get; set; }
    public string PatchDeployMessage { get; set; } = "";
    /// <summary>The client patch was not rebuilt; the change waits for the next Rebuild patch click.
    /// Rendered as "pending", not as a failed deploy.</summary>
    public bool PatchQueued { get; set; }
    public int PatchPending { get; set; }
    /// <summary>False when the operation ships no art and therefore has no patch step — a vanilla
    /// clone reuses the source's own display. Without this the result panel rendered the untouched
    /// <see cref="PatchDeployed"/>=false / empty-message pair as a red, blank "Deploy ✗" row on every
    /// successful clone, which reads as a half-failed operation.</summary>
    public bool PatchRequired { get; set; } = true;
    /// <summary>"NotNeeded" / "Deployed" / "Failed" — see <see cref="ItemSetDeployState"/>.</summary>
    public string ServerItemSetState { get; set; } = nameof(ItemSetDeployState.NotNeeded);
    public string ServerItemSetMessage { get; set; } = "";
}

public sealed class ArmorSetSaveRequest
{
    public int SetId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<long> MemberEntries { get; init; }
    public List<ArmorSetBonus>? Bonuses { get; init; }
    public int RequiredSkill { get; init; }
    public int RequiredSkillRank { get; init; }
}

public sealed class ArmorSetResult
{
    public int SetId { get; set; }
    public int MemberCount { get; set; }
    public int BonusCount { get; set; }
    public bool PatchDeployed { get; set; }
    public bool PatchQueued { get; set; }
    public int PatchPending { get; set; }
    public bool ServerDbcDeployed { get; set; }
    public bool ItemTemplateStamped { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ArmorSetSummary
{
    public int SetId { get; set; }
    public string Name { get; set; } = "";
    public int MemberCount { get; set; }
    public int BonusCount { get; set; }
    public List<ArmorSetBonus> Bonuses { get; set; } = new();
    public List<int> MemberEntries { get; set; } = new();
}

public sealed class ArmorBulkDeleteResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<ArmorBulkDeleteItem> Items { get; } = new();
}

public sealed class ArmorBulkDeleteItem
{
    public string Kind { get; set; } = "";   // "set" | "piece"
    public long Id { get; set; }
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ArmorDeleteResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";

    /// <summary>The registry had no such row. Distinguished from a real failure so a set delete can
    /// treat an already-gone piece as done rather than refusing to release the set for ever.</summary>
    public bool NotFound { get; set; }
}

/// <summary>Flat row for the persistence INSERT (Dapper parameter object).</summary>
public sealed class ArmorPersistRow
{
    public long DisplayId { get; init; }
    public long ItemEntry { get; init; }
    public string BuildId { get; init; } = "";
    public int SetId { get; init; }
    public string RenderKind { get; init; } = "";
    public string ArmorTypeKey { get; init; } = "";
    public int Material { get; init; }
    public int InventoryType { get; init; }
    public string Name { get; init; } = "";
    public string IconStem { get; init; } = "";
    public string? ModelName { get; init; }
    public string? ModelName2 { get; init; }
    public string? TextureName { get; init; }
    public string? TextureMpqPath { get; init; }
    public byte[]? ModelTextureBlp { get; init; }
    public int Geoset0 { get; init; }
    public int Geoset1 { get; init; }
    public int Geoset2 { get; init; }
    public uint HelmetVis0 { get; init; }
    public uint HelmetVis1 { get; init; }
    public uint GroupSound { get; init; }
    public string SqlText { get; init; } = "";
    public string GameplayJson { get; init; } = "";
}
