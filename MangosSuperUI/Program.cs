using MangosSuperUI.BotLogic.Brain;
using MangosSuperUI.BotLogic.Chat.Capacity;
using MangosSuperUI.BotLogic.Chat.Coordinator;
using MangosSuperUI.BotLogic.Chat.Core;
using MangosSuperUI.BotLogic.Chat.Engine;
using MangosSuperUI.BotLogic.Chat.Health;
using MangosSuperUI.BotLogic.Chat.Memory;
using MangosSuperUI.BotLogic.Chat.Voice;
using MangosSuperUI.BotLogic.Core;
using MangosSuperUI.BotLogic.Data;
using MangosSuperUI.BotLogic.Planners;
using MangosSuperUI.BotLogic.Tracking;
using MangosSuperUI.Hubs;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.AspNetCore.StaticFiles;
using System.Diagnostics.Metrics;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();  // ← sends watchdog heartbeats + handles SIGTERM gracefully

// ---------- Brain log buffer (in-memory ring, fed to the Bots "Live" tab on demand) ----------
var botLogBuffer = new BotLogBuffer();
builder.Services.AddSingleton(botLogBuffer);
builder.Logging.AddProvider(new BotLogBufferProvider(botLogBuffer));
builder.Logging.AddFilter<BotLogBufferProvider>("MangosSuperUI", LogLevel.Debug);

// ---------- Additional Config Source ----------
builder.Configuration.AddJsonFile("server-config.json", optional: true, reloadOnChange: true);

// ---------- Configuration ----------
builder.Services.Configure<VmangosSettings>(builder.Configuration.GetSection("Vmangos"));
builder.Services.Configure<RemoteAccessSettings>(builder.Configuration.GetSection("RemoteAccess"));
builder.Services.Configure<BotChatSettings>(builder.Configuration.GetSection("BotChat"));
builder.Services.Configure<BotSpawnSettings>(builder.Configuration.GetSection("BotSpawn"));

// ---------- Data ----------
builder.Services.AddSingleton<ConnectionFactory>();

// ---------- Services ----------
builder.Services.AddSingleton<DbInitializationService>();
builder.Services.AddSingleton<RaService>();
builder.Services.AddSingleton<ProcessManagerService>();
// Every privileged operation funnels through one root-owned helper script, so
// the trust boundary is a single reviewable file rather than scattered sudo calls.
builder.Services.AddSingleton<PrivilegedOpsService>();
// Singleton, not scoped: CPU% is a delta between polls, so the previous
// CPU-time reading has to outlive the request that took it.
builder.Services.AddSingleton<ProcessResourceSampler>();
// Also a singleton, and for the same reason: per-core attribution is a delta
// over per-thread CPU counters, so the previous reading must survive the request.
builder.Services.AddSingleton<ProcessCoreSampler>();
builder.Services.AddSingleton<StateCaptureService>();
builder.Services.AddHttpContextAccessor();            // lets AuditService stamp the caller's ip on rows written deep in a service
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<WorldArtifactService>();
builder.Services.AddSingleton<RtsWorldCreationService>();
builder.Services.AddSingleton<WorldMaintenanceGate>();
builder.Services.AddSingleton<WorldStateService>();   // world suspend/resume — registry lives on disk, not in a swappable DB
builder.Services.AddSingleton<ChangeGraphService>();  // audit_log as a drillable graph, with entry/batch undo
builder.Services.AddSingleton<DivergenceService>();   // live drift vs og_* baselines — the state view behind the graph
builder.Services.AddSingleton<DbcService>();
builder.Services.AddSingleton<BotTalentVisibilityService>();
builder.Services.AddSingleton<HeightMapService>();
builder.Services.AddSingleton<BotBridgeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BotBridgeService>());
// Written by BotBrainService, read by RuntimeScaleDiagnosticsService — a shared
// singleton so the diagnostics service never has to depend on the brain.
builder.Services.AddSingleton<BrainLoopMetrics>();
builder.Services.AddSingleton<RuntimeScaleDiagnosticsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RuntimeScaleDiagnosticsService>());
builder.Services.AddSingleton<BotSpawnService>();   // Add Bots batches: background RA loop + SpawnProgress over the bridge hub
builder.Services.AddSingleton<OllamaChatService>();
builder.Services.AddSingleton<SourceIndexerService>();
builder.Services.AddHostedService<SourceIndexWarmupService>();   // build the Source Map index at startup (was lost on every restart)
builder.Services.AddSingleton<CircuitTraceSourceService>();
builder.Services.AddSingleton<ZoneSafetyMap>();
builder.Services.AddSingleton<BotFallRecorder>();   // always-on void/fall black box (flush-only-on-fall)
builder.Services.AddSingleton<SpellCreatorService>();
builder.Services.AddSingleton<BlpWriterService>();
builder.Services.AddSingleton<PatchBuilderService>();
builder.Services.AddSingleton<SpellIconService>();
builder.Services.AddSingleton<SpellConfigService>();
builder.Services.AddSingleton<SpellTextureService>();
builder.Services.AddSingleton<SpellRecipeService>();
builder.Services.AddSingleton<ComfyUIDispatcher>();
builder.Services.AddSingleton<ComfyUIUpscaler>();
builder.Services.AddSingleton<VanillaBlpService>();
builder.Services.AddSingleton<SpellDnaService>();
builder.Services.AddSingleton<MpqReaderService>();
builder.Services.AddSingleton<GameObjectModelService>();
builder.Services.AddSingleton<MinimapTileService>();
builder.Services.AddSingleton<CharacterModelService>();
builder.Services.AddSingleton<BodyAtlasTextureService>();
builder.Services.AddSingleton<CacheVersionRegistry>();
builder.Services.AddSingleton<CharacterSkinCompositor>();
builder.Services.AddSingleton<PaletteSwapService>();
builder.Services.AddScoped<VariationRecipeService>();
builder.Services.AddScoped<TextureSegmentationService>();
builder.Services.AddSingleton<VramManager>();
builder.Services.AddSingleton<WikiDocStore>();
builder.Services.AddSingleton<WikiIndexer>();
builder.Services.AddSingleton<WikiSearchStore>();
builder.Services.AddScoped<RetextureSupport>();


builder.Services.AddScoped<ItemTextureService>();
builder.Services.AddScoped<ItemRetextureService>();

// ---------- Weapon Forge (WEAPON_GEN.md) — the IMPORT page: pre-textured GLB → M2/patch ----------
// Pure, offline artifact compiler + atomic id reservation. No live DB/RA/client-path side effects.
// The creation tooling (sketch workbench, texture zones, local AI texturing) was removed from this
// page 2026-08-19 and is archived under Desktop\ItemForgeMSUIFiles.
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.WeaponIdReservationService>();
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.WeaponPatchBuilder>();
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.WeaponPreviewService>();
// Per-family stock donor resolution (scaffold M2, DBC row, grip envelope) — cached for the app lifetime.
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.WeaponDonorResolver>();
// Read-only later-client archive mounts for the import sections: TBC (WeaponForge:TbcDataPath)
// and WotLK (WeaponForge:WotlkDataPath). Same managed reader; WotLK models are v264 + .skin.
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.TbcMpqSource>();
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.WotlkMpqSource>();
// Shipped item-name catalogs (wwwroot/data/tbc-item-catalog.json, wotlk-item-catalog.json) — names
// never live in MPQs. LegacyImportSources pairs each mount with its catalog, keyed "tbc"/"wotlk".
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.TbcItemCatalog>();
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.WotlkItemCatalog>();
// Third lane: stock 1.12 art through the SAME import pipeline. It exists because a vanilla clone
// reuses the source display and therefore can never be recolored — colours live in the BLP, glow
// and particle colours in the M2, and a clone ships neither. This lane packages its own, which is
// what makes "recolor skin" and "tint glow / flame effects" available on a stock weapon.
// It mounts the LIVE client and excludes the patches this app writes, so our own output can never
// be re-imported as source art; its item list is the live item_template, not a shipped json.
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.VanillaMpqSource>();
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.VanillaItemCatalog>();
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.LegacyImportSources>();
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.VanillaItemSpellCatalog>();
// Shared typed-gameplay intake (itemConfig JSON → validated item_template overrides) for both forges.
builder.Services.AddScoped<MangosSuperUI.Services.WeaponForge.ItemConfigurationParser>();
// Curated tier/spec itemization generator (deterministic, DB-free) — starting-point stats for imports.
builder.Services.AddSingleton<MangosSuperUI.Services.Itemization.ItemBudgetGenerator>();
// Phase-3 donor-scaffold writer: emits real custom geometry on the donor scaffold (fixed topology).
// Swap back to NullWeaponMeshWriter only to isolate the compiler from the writer during debugging.
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.IWeaponMeshWriter,
    MangosSuperUI.Services.WeaponForge.DonorScaffoldWriter>();
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.WeaponAssetCompiler>();
// The one packaging path for every weapon source: compile → persist compiled bytes → rebuild the
// single unified patch MPQ (ALL custom weapons) → straight .mpq + .sql outputs, no ZIP.
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.CustomWeaponBuildService>();
// Pre-textured GLB import; high-poly sources are decimated by the static UvPreservingDecimator.
builder.Services.AddSingleton<MangosSuperUI.Services.WeaponForge.GlbWeaponImporter>();

// Armor Forge — patch-6 (painted body-atlas pieces, custom-skinned stock helm/shoulder models,
// cloaks) + tier sets (ItemSet.dbc). Reuses the weapon id allocator + reservation tables.
builder.Services.AddSingleton<MangosSuperUI.Services.ArmorForge.ArmorPatchBuilder>();
builder.Services.AddSingleton<MangosSuperUI.Services.ArmorForge.CustomArmorBuildService>();

// Unified patch — the ONE archive carrying ItemDisplayInfo.dbc for every lane that writes it
// (retextures, weapons, armor), replacing the patch-4 -> patch-5 -> patch-6 chain so a change in any
// lane means one rebuild and one download instead of a cascade.
// SCOPED, not singleton, for the same reason as CustomDisplayRegistrar below: it resolves
// ItemRetextureService, which is scoped, and a singleton capturing a scoped dependency fails DI
// validation at startup. Singletons that want to trigger a rebuild must open a scope
// (IServiceScopeFactory.CreateScope) rather than injecting this directly.
builder.Services.AddSingleton<MangosSuperUI.Services.UnifiedPatch.UnifiedPatchBuilder>();
builder.Services.AddScoped<MangosSuperUI.Services.UnifiedPatch.UnifiedPatchService>();
// SCOPED, not singleton: it depends on ItemRetextureService, which is scoped. A singleton
// capturing a scoped dependency fails DI validation at startup.
builder.Services.AddScoped<CustomDisplayRegistrar>();
builder.Services.AddSingleton<MangosSuperUI.Services.ArmorForge.TbcArmorCatalog>();
builder.Services.AddSingleton<MangosSuperUI.Services.ArmorForge.TbcArmorImporter>();
// WotLK lane: same catalog/importer over the WotLK mount + shipped WotLK catalog; ArmorImportSources
// pairs the two lanes by key ("tbc"/"wotlk") for the controller and build service.
builder.Services.AddSingleton<MangosSuperUI.Services.ArmorForge.WotlkArmorCatalog>();
builder.Services.AddSingleton<MangosSuperUI.Services.ArmorForge.WotlkArmorImporter>();
builder.Services.AddSingleton<MangosSuperUI.Services.ArmorForge.ArmorImportSources>();
// Vanilla clone lane: the stock sets come from the mounted 1.12 client's own ItemSet.dbc, not from a
// later-client archive, so this sits outside ArmorImportSources (which pairs the two IMPORT lanes).
builder.Services.AddSingleton<MangosSuperUI.Services.ArmorForge.VanillaArmorSetCatalog>();

// NPC dev window (spawn / pathing / aggro) commit + audit path.
builder.Services.AddScoped<NpcDevApplyService>();
builder.Services.AddScoped<NpcDevBaselineService>();

// ---------- BotLogic: Behavioral Engine ----------

// Tracking (in-memory, singleton)
builder.Services.AddSingleton<BotStateTracker>();

// Data loaders
builder.Services.AddSingleton<QuirkLoader>();
builder.Services.AddSingleton<SpellProgressionLoader>();
builder.Services.AddSingleton<ZoneDataLoader>();
builder.Services.AddSingleton<CreatureSpawnLoader>();   // Scatter Build 2: per-entry spawn footprint sampler (QuestPlanner)
builder.Services.AddSingleton<QuestGraphLoader>();
builder.Services.AddSingleton<BotBrainDbInit>();

// Brain spine (Strategy B rebuild): executor + supervisor + driver
builder.Services.AddSingleton<BotExecutor>();
builder.Services.AddSingleton<BotSupervisor>();

// Brain planners (Phase 2 — Grinding): goal selector + per-goal planners (IBotPlanner).
// BotBrain self-assembles the Goal→planner map from the registered IBotPlanner set;
// adding a goal in P3+ is one more AddSingleton<IBotPlanner, …>() here.
builder.Services.AddSingleton<GoalSelector>();
builder.Services.AddSingleton<IBotPlanner, GrindPlanner>();
builder.Services.AddSingleton<IBotPlanner, QuestPlanner>();   // P3: Goal.Questing
builder.Services.AddSingleton<IBotPlanner, MaintenancePlanner>();
builder.Services.AddSingleton<IBotPlanner, TrainingPlanner>();   // Goal.Training — class-trainer trip
builder.Services.AddSingleton<IBotPlanner, HubErrandPlanner>();  // Goal.Vendoring — "do your rounds" hub errand (player-party, 2026-07-08 §3)

// [ROTATION] Custom combat rotations — profile loading, assignment persistence, LOAD_ROTATION
// push (2026-07-16). Self-wires into BotBridgeService for the HELLO re-push; the activation
// line after Build() below is what actually constructs it before the first bot connects.
builder.Services.AddSingleton<RotationService>();
builder.Services.AddSingleton<BotCombatLoadoutService>();
builder.Services.AddSingleton<BotCombatLoadoutQueueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BotCombatLoadoutQueueService>());

// Read-only spellbook projection for the Bot Monitor. It rides on RotationService so a
// learned-spell view can also say which instructions of the assigned custom slate the
// bot cannot actually cast; nothing here mutates character or core state.
builder.Services.AddSingleton<BotSpellbookVisibilityService>();

// [RAID-PLAN] Raid plan documents (the Encounter Lab's exports) — storage, assignment
// persistence, LOAD_RAID_PLAN push (PLAN_19 M-B). Same self-wire + eager-construction
// pattern as RotationService.
builder.Services.AddSingleton<RaidPlanService>();

builder.Services.AddSingleton<BotBrain>();
builder.Services.AddSingleton<BotDiagnosticsService>();

// Brain orchestrator (BackgroundService)
builder.Services.AddSingleton<BotBrainService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BotBrainService>());

// ---------- BotLogic: Chat social layer (CHAT_ARCHITECTURE) ----------
// C0: coordinator stub — drains CHAT_RECV stimuli off the bridge and logs [CHAT-COORD].
// Fully separate from the brain spine (D9): its own BackgroundService, never on a brain tick.
// C1: settings snapshot service (§14.1 — 5s TTL, zone→global resolution, hot-apply)
builder.Services.AddSingleton<ChatSettingsService>();
// C2: reactive whisper MVP — persona, engine, broker (temp, C5 replaces), typing timeline, Tier 0
builder.Services.AddSingleton<PersonaService>();
builder.Services.AddSingleton<PromptAssembler>();
builder.Services.AddSingleton<StylePostPass>();
builder.Services.AddSingleton<ConversationTracker>();
builder.Services.AddSingleton<TypingScheduler>();
builder.Services.AddSingleton<IInferenceBroker, FixedEndpointBroker>();
builder.Services.AddSingleton<IChatEngine, ChatEngine>();
// C3: Tier-1 verbatim memory + relationship bumps (buffered, flushed by the coordinator)
builder.Services.AddSingleton<ChatMemoryStore>();
// C4: arbitration — urge scoring + the anti-storm guards (chain depth, token buckets)
builder.Services.AddSingleton<UrgeScorer>();
builder.Services.AddSingleton<ChainGuard>();
builder.Services.AddSingleton<BudgetBuckets>();
// C6: voice library builder (Batch-class admin action from the Capacity tab)
builder.Services.AddSingleton<VoiceLibraryBuilder>();
builder.Services.AddSingleton<ChatCoordinator>();
builder.Services.AddSingleton<IChatCoordinator>(sp => sp.GetRequiredService<ChatCoordinator>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatCoordinator>());
builder.Services.AddSingleton<ChatHealthService>();

// ---------- MVC + SignalR ----------
builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // Custom display registration (retexture -> weapon -> armor). These caches are in-memory only,
    // so this has to run at every boot; the SAME sequence also has to run after DbcService.Reload(),
    // which is why it lives in one shared type. A DB that is down at boot must NOT take the panel
    // down — the registrar swallows and logs per lane.
    var bootLog = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        scope.ServiceProvider.GetRequiredService<CustomDisplayRegistrar>()
            .RegisterAllAsync("startup").GetAwaiter().GetResult();
    }
    catch (Exception ex) { bootLog.LogError(ex, "Startup: custom display registration failed"); }

    var registry = scope.ServiceProvider.GetRequiredService<CacheVersionRegistry>();
    registry.SweepAllOnStartup();
}

// ---------- Database Bootstrap ----------
// Ensures vmangos_admin DB + tables exist before any request can hit AuditService.
// Never throws — logs errors and sets AdminDbReady = false for dashboard to display.
var dbInit = app.Services.GetRequiredService<DbInitializationService>();
await dbInit.InitializeAsync();


await app.Services.GetRequiredService<QuestGraphLoader>().LoadAsync();
await app.Services.GetRequiredService<ZoneSafetyMap>().LoadAsync();
// Vendor/innkeeper NPC cache — backs MaintenancePlanner.GetNearestVendor. Was registered
// as a singleton but its LoadAsync was never invoked at boot, so _vendorsByMap stayed empty
// and every vendor lookup returned null ("no vendors loaded on this map"). Load it here, same
// as the other startup loaders.
await app.Services.GetRequiredService<ZoneDataLoader>().LoadAsync();

// Creature spawn footprints — backs QuestPlanner's Scatter (Build 2). Like the loaders above,
// registered as a singleton but its LoadAsync must be invoked here or _spawnsByEntry stays empty
// and every objective dispatch falls back to the canonical GrindX/GrindY (no scatter, no crash —
// just today's dogpile). Confirm the "CreatureSpawnLoader: cached N spawn points across M entries"
// line at boot.
await app.Services.GetRequiredService<CreatureSpawnLoader>().LoadAsync();

// Class-trainer location cache — backs TrainingPlanner.GetNearestTrainer. Like ZoneDataLoader
// above, registered as a singleton but its LoadAsync must be invoked here or _trainersByClass
// stays empty and every training trip gives up ("no-loader" / "no-trainer-in-range"). Confirm the
// "[SpellProgression] Loaded N trainer spawns across M classes" line at boot.
await app.Services.GetRequiredService<SpellProgressionLoader>().LoadAsync();

// [ROTATION] Eagerly construct RotationService so its SetRotationService(this) wire-in lands
// BEFORE the bridge accepts the first HELLO — a lazily-resolved singleton would otherwise not
// exist until the first API call, and every bot login before that would miss its re-push.
app.Services.GetRequiredService<RotationService>();

// [RAID-PLAN] Same eager construction, same reason: the HELLO re-push seam must be
// wired before the first bot connects (PLAN_19 M-B).
app.Services.GetRequiredService<RaidPlanService>();

// ---------- Pipeline ----------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

// Static files with custom MIME types (GLB for 3D model-viewer)
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".glb"] = "model/gltf-binary";
// Serve /client/ -> /client/index.html for the MSUI Client SPA. Without this,
// UseStaticFiles only serves exact file paths and /client/ returns 404 even
// though index.html is sitting right there.
app.UseDefaultFiles(new DefaultFilesOptions
{
    DefaultFileNames = new List<string> { "index.html" }
});

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider,
    // Script and stylesheet files are NOT version-stamped when they are pulled in as ES-module
    // imports (embed.js → viewer.js → …), so without an explicit policy the browser applies its
    // heuristic freshness and keeps serving yesterday's module for a while after a publish — the
    // Armor Forge viewer kept the old resize logic through several reloads. no-cache still lets the
    // ETag answer 304 on every navigation; it just forces that revalidation.
    OnPrepareResponse = ctx =>
    {
        string ext = Path.GetExtension(ctx.File.Name);
        if (ext.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".css", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});

// ---------- MSUI Client: WebSocket ↔ TCP bridge (design doc DD-4) ----------
// MUST come before UseRouting so the upgrade is handled ahead of MVC route
// matching. UseWebSockets is required even though SignalR is registered —
// SignalR brings its own transport handling and does not enable raw WS.
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<ConsoleHub>("/hubs/console");
app.MapHub<LogStreamHub>("/hubs/logs");
app.MapHub<BotBridgeHub>("/hubs/botbridge");

app.Run();
