namespace MangosSuperUI.Services;

/// <summary>
/// Orchestrates the full spell-creation patch pipeline:
///   1. Read original DBC files from server data directory
///   2. Clone visual chain (SpellVisual → Kit → EffectName) with new IDs
///   3. Add new spell entry to Spell.dbc
///   4. Add SkillLineAbility.dbc entry (REQUIRED for spellbook visibility)
///   5. Add SpellIcon.dbc entry + convert PNG → BLP if custom icon provided
///   6. Patch M2 particle emitters (colors, rate, scale)
///   7. Package everything (DBCs + M2s + BLP) into patch-custom-XXXXX.MPQ
///   8. Write patched DBCs to server DBC dir
///
/// CRITICAL 1.12.1 REQUIREMENTS:
///   - Spell ID MUST be &lt; 65535 (SMSG_INITIAL_SPELLS = uint16)
///   - SkillLineAbility.dbc MUST have a row for the spell → spellbook tab
///   - SPELL_ATTR_HIDDEN_CLIENTSIDE (0x80) MUST be cleared from Attributes field
///   - WDB cache must be cleared on client between tests
///   - Patch MPQ must be named patch-?.MPQ (single char, e.g. patch-Z.MPQ)
///   - Patched Spell.dbc MUST be written to server DBC dir AND mangosd restarted
///
/// ═══════════════════════════════════════════════════════════════
/// SPELL.DBC FIELD INDICES — EMPIRICALLY VERIFIED 2026-04-27
/// via fireball_forensic.py against entry 133 (Fireball Rank 1)
/// Locale flags pattern 0x003F007E found at [128, 137, 146, 155]
/// ═══════════════════════════════════════════════════════════════
///   [0]       ID
///   [6]       Attributes (where HIDDEN bit 0x80 lives) — NOT field 10!
///   [115]     SpellVisualID[0]    — NOT field 131!
///   [116]     SpellVisualID[1]
///   [117]     SpellIconID         — NOT field 133!
///   [118]     ActiveIconID
///   [119]     SpellPriority
///   [120-127] Name_lang[0..7]     (enUS=120) — NOT 136!
///   [128]     Name_lang_flags     (0x003F007E)
///   [129-136] Subtext_lang[0..7]  (enUS=129) — NOT 145!
///   [137]     Subtext_lang_flags
///   [138-145] Description_lang[0..7] (enUS=138) — NOT 154!
///   [146]     Description_lang_flags
///   [147-154] Tooltip_lang[0..7]  (enUS=147) — was unmapped!
///   [155]     Tooltip_lang_flags
/// ═══════════════════════════════════════════════════════════════
///
/// SPELLICON.DBC LAYOUT (build 5875):
///   2 fields per record, 8 bytes per record:
///     [0] ID         — referenced by Spell.dbc field [117]
///     [1] TextureRef — stringref → "Interface\Icons\<name>" (no extension)
///
/// ═══════════════════════════════════════════════════════════════
/// ID RANGE STRATEGY (Session 9):
///   SpellVisual/Kit/EffectName IDs at 60000+ are SILENTLY IGNORED
///   by the 1.12.1 client. Custom visual chain IDs start at 10000
///   (safely above vanilla maximums: Visual=7891, Kit=6757, Effect=3067).
///   Spell.dbc, SpellIcon.dbc, SkillLineAbility.dbc work fine at 60000+.
/// ═══════════════════════════════════════════════════════════════
/// </summary>
public class PatchBuilderService
{
    private readonly IConfiguration _config;
    private readonly BlpWriterService _blpWriter;
    private readonly ILogger<PatchBuilderService> _logger;

    // ── Spell.dbc field indices (VERIFIED from forensic dump) ──
    private const int FIELD_ATTRIBUTES = 6;           // Fireball = 0x00010000
    private const int FIELD_SPELL_VISUAL_ID = 115;    // Fireball = 67
    private const int FIELD_SPELL_ICON_ID = 117;      // Fireball = 185
    private const int FIELD_NAME_ENUS = 120;           // Fireball → "Fireball"
    private const int FIELD_NAME_FLAGS = 128;          // 0x003F007E
    private const int FIELD_SUBTEXT_ENUS = 129;        // Fireball → "Rank 1"
    private const int FIELD_SUBTEXT_FLAGS = 137;       // 0x003F007E
    private const int FIELD_DESC_ENUS = 138;           // Fireball → "Hurls a fiery ball..."
    private const int FIELD_DESC_FLAGS = 146;          // 0x003F007E
    private const int FIELD_TOOLTIP_ENUS = 147;        // Fireball → "$s2 Fire damage..."
    private const int FIELD_TOOLTIP_FLAGS = 155;       // 0x003F007E
    private const int FIELD_SCHOOL_MASK = 1;           // category / school area
    private const uint LOCALE_FLAGS = 0x003F007E;      // enUS locale flags pattern
    private const uint SPELL_ATTR_HIDDEN = 0x80;       // SPELL_ATTR_HIDDEN_CLIENTSIDE

    // ── Spell.dbc effect/gameplay field indices (Session 33 — EMPIRICALLY VERIFIED) ──
    // Verified via forensic dump of Fireball R1-R12 (entries 133,143,145,3140,8400,10148,25306)
    // against known spell_template values from Session 32 DB trace.
    //
    // CRITICAL DBC vs DB difference for EffectDieSides:
    //   spell_template.effectDieSides1 = damageMax - damageMin (e.g., 8 for Fireball R1)
    //   Spell.dbc EffectDieSides[0]    = effectDieSides1 + 1   (e.g., 9 for Fireball R1)
    //   DBC formula: damage = BasePoints + rand(1, DieSides) → min=BP+1, max=BP+DieSides
    //   So DBC DieSides = damageMax - BasePoints = (damageMax) - (damageMin - 1) = ds1 + 1
    //
    // [27] MaxLevel: spell_template stores 0 ("no cap"), but DBC stores actual computed
    //   value (spellLevel + 4 for Fireball). When cloning from source rank, the DBC value
    //   is already correct — we only patch it if the custom spell explicitly overrides maxLevel.
    private const int FIELD_CASTING_TIME_INDEX = 18;   // CastingTimeIndex → CastingTimes.dbc     [VERIFIED: R1=16, R2=5, R4=14, R5=22]
    private const int FIELD_MAX_LEVEL = 27;              // MaxLevel (DBC stores actual, not 0)     [VERIFIED: R1=5, R5=28, R8=46]
    private const int FIELD_SPELL_LEVEL = 28;            // SpellLevel                              [VERIFIED: R1=1, R2=6, R3=12, R4=18]
    private const int FIELD_BASE_LEVEL = 29;             // BaseLevel                               [VERIFIED: matches SpellLevel for Fireball]
    private const int FIELD_DURATION_INDEX = 30;         // DurationIndex → SpellDuration.dbc       [VERIFIED: R1=35, R2+=31-32]
    private const int FIELD_MANA_COST = 32;              // ManaCost                                [VERIFIED: R1=30, R2=45, R3=65, R4=95]
    private const int FIELD_RANGE_INDEX = 36;            // RangeIndex → SpellRange.dbc             [VERIFIED: 35 for all Fireball]
    private const int FIELD_SPEED = 37;                  // Speed (missile, FLOAT yards/sec)        [VERIFIED: 24.0 for all Fireball]
    private const int FIELD_EFFECT_DIE_SIDES_0 = 64;     // EffectDieSides[0] = effectDieSides1 + 1 [VERIFIED: R1=9, R5=49, R8=97]
    private const int FIELD_EFFECT_DIE_SIDES_1 = 65;     // EffectDieSides[1] (DoT die sides)       [VERIFIED: 1 for all Fireball]
    private const int FIELD_EFFECT_REAL_PPL_0 = 73;      // EffectRealPointsPerLevel[0] (FLOAT)     [VERIFIED: R1=0.6, R2=0.8, R3=1.0]
    private const int FIELD_EFFECT_BASE_POINTS_0 = 76;   // EffectBasePoints[0] = effectBasePoints1 [VERIFIED: R1=13, R2=30, R3=52]
    private const int FIELD_EFFECT_BASE_POINTS_1 = 77;   // EffectBasePoints[1] = effectBasePoints2 [VERIFIED: R3=1, R4=2, R5=4, R8=9]

    // ── Trainer wrapper DBC field indices (Session 34) ──
    // Trainer wrappers use SPELL_EFFECT_LEARN_SPELL (36) to teach the real spell.
    // Client needs these in Spell.dbc to render trainer UI.
    private const int FIELD_EFFECT_0 = 61;                 // Effect[0] — 36 = SPELL_EFFECT_LEARN_SPELL
    private const int FIELD_EFFECT_TRIGGER_SPELL_0 = 109;  // EffectTriggerSpell[0] — the spell being taught
    private const int TRAINER_WRAPPER_CLONE_SOURCE = 1173; // Vanilla "Fireball Rank 2" learn-spell wrapper

    // ── ID Floor Constants ──
    // Spell.dbc / SpellIcon.dbc work at any ID (no client cap)
    private const uint CUSTOM_SPELL_FLOOR = 40000;  // covers custom spells (40000-49999) + wrappers (50000+)
    private const uint CUSTOM_ICON_FLOOR = 60000;   // custom SpellIcon.dbc entries

    // SpellVisual / SpellVisualKit / SpellVisualEffectName — client ignores 60000+
    // Vanilla maximums: Visual=7891, Kit=6757, EffectName=3067
    // All three use 10000 as a single unified floor (well above all vanilla maximums)
    private const uint CUSTOM_VISUAL_FLOOR = 10000;

    // ── Per-DBC scrub floor map ──
    // When reading server DBCs, scrub custom entries above these floors
    // NOTE: SkillLineAbility.dbc is NOT in this map — it uses a custom scrub
    // (by spellId in field [2], not by row ID in field [0])
    private static readonly Dictionary<string, uint> DbcScrubFloors = new()
    {
        { "SpellVisual.dbc",            CUSTOM_VISUAL_FLOOR },
        { "SpellVisualKit.dbc",         CUSTOM_VISUAL_FLOOR },
        { "SpellVisualEffectName.dbc",  CUSTOM_VISUAL_FLOOR },
        { "Spell.dbc",                  CUSTOM_SPELL_FLOOR },
        { "SpellIcon.dbc",              CUSTOM_ICON_FLOOR },
    };

    private string DbcPath => _config["Vmangos:DbcPath"]
        ?? "/home/wowvmangos/vmangos/run/data/5875/dbc";

    private string ClientDataPath => _config["Vmangos:ClientDataPath"]
        ?? "/home/wowvmangos/wowclient/Data";

    private string PatchOutputPath => _config["Vmangos:PatchOutputPath"]
        ?? "/opt/mangossuperui/wwwroot/patches";

    /// <summary>
    /// Path to pre-extracted M2 files from the client's patch.MPQ (fallback).
    /// 
    /// War3Net has a known bug reading from large (~2GB) MPQs: MpqStream gets
    /// prematurely disposed (ObjectDisposedException), causing reads to silently
    /// fall through to model.MPQ which has older M2 versions with stale texture
    /// paths (green "missing texture" blocks in-game).
    /// 
    /// The pipeline tries MPQ reading first. If that fails, it falls back to
    /// pre-extracted static files in this directory. If neither source has the
    /// file, an ERROR is logged (not silently skipped).
    /// 
    /// Pre-extract spell M2 files from patch.MPQ (via Ladik's MPQ Editor) into
    /// this directory, preserving the MPQ path structure:
    ///   {ClientM2Path}/Spells/Fire_Cast_Hand.m2
    ///   {ClientM2Path}/Spells/Fire_Precast_Hand.m2
    ///   {ClientM2Path}/Spells/Fireball_Missile_Low.m2
    ///   {ClientM2Path}/Spells/MoltenBlast_Impact_Chest.m2
    ///   etc.
    /// </summary>
    private string ClientM2Path => _config["Vmangos:ClientM2Path"]
        ?? "/home/wowvmangos/wowclient/m2";

    public PatchBuilderService(
        IConfiguration config,
        BlpWriterService blpWriter,
        ILogger<PatchBuilderService> logger)
    {
        _config = config;
        _blpWriter = blpWriter;
        _logger = logger;
    }

    /// <summary>
    /// Rebuild the unified patch-3.MPQ containing ALL custom spells.
    /// 
    /// Called after every Generate or Delete operation. Loads clean DBCs,
    /// iterates all custom spells with saved configs, clones each visual
    /// chain + M2s, and packages everything into one MPQ.
    ///
    /// This replaces the per-spell patch approach. Players only need ONE
    /// patch file (patch-3.MPQ) regardless of how many custom spells exist.
    /// </summary>
    public async Task<UnifiedPatchResult> RebuildUnifiedPatchAsync(List<SpellPatchRequest> spells)
    {
        var result = new UnifiedPatchResult();

        try
        {
            _logger.LogInformation("PatchBuilder: Rebuilding unified patch for {Count} custom spell(s)", spells.Count);

            // ── Step 1: Load CLEAN DBCs (scrub all custom entries) ──
            var spellDbc = ReadCleanDbc("DBFilesClient\\Spell.dbc");
            var visualDbc = ReadCleanDbc("DBFilesClient\\SpellVisual.dbc");
            var kitDbc = ReadCleanDbc("DBFilesClient\\SpellVisualKit.dbc");
            var effectNameDbc = ReadCleanDbc("DBFilesClient\\SpellVisualEffectName.dbc");
            var skillLineAbilityDbc = ReadCleanSlaDbc();
            var spellIconDbc = ReadCleanDbc("DBFilesClient\\SpellIcon.dbc");

            _logger.LogInformation(
                "PatchBuilder: Loaded clean DBCs — Spell:{S} Visual:{V} Kit:{K} Effect:{E} SLA:{SLA} Icon:{I}",
                spellDbc.RecordCount, visualDbc.RecordCount, kitDbc.RecordCount,
                effectNameDbc.RecordCount, skillLineAbilityDbc.RecordCount, spellIconDbc.RecordCount);

            var mpqBuilder = new MpqBuilderService(_logger as ILogger<MpqBuilderService>);
            int totalM2s = 0;

            // ── Step 2: Process each custom spell ──
            foreach (var request in spells)
            {
                try
                {
                    if (request.SpellEntry > 65000)
                    {
                        _logger.LogWarning("PatchBuilder: Skipping spell {E} — exceeds uint16 range", request.SpellEntry);
                        result.Errors.Add($"Spell #{request.SpellEntry}: exceeds uint16 range");
                        continue;
                    }

                    // ── Allocate visual chain IDs ──
                    uint newVisualId = Math.Max(visualDbc.GetMaxId() + 1, CUSTOM_VISUAL_FLOOR);
                    uint baseKitId = Math.Max(kitDbc.GetMaxId() + 1, CUSTOM_VISUAL_FLOOR);
                    uint baseEffectId = Math.Max(effectNameDbc.GetMaxId() + 1, CUSTOM_VISUAL_FLOOR);

                    // ── Clone visual chain ──
                    var cloneResult = SpellVisualCloner.Clone(
                        visualDbc, kitDbc, effectNameDbc,
                        request.SourceVisualId,
                        newVisualId, baseKitId, baseEffectId,
                        SanitizeName(request.SpellName));

                    _logger.LogInformation("PatchBuilder: [{Name}] Cloned visual V:{V} Kits:{K} Effects:{E}",
                        request.SpellName, cloneResult.NewVisualId, cloneResult.KitIdMap.Count, cloneResult.EffectNameIdMap.Count);

                    // ── Add spell to Spell.dbc ──
                    var sourceSpellRow = spellDbc.GetRow(request.SourceSpellEntry);
                    if (sourceSpellRow == null)
                    {
                        _logger.LogWarning("PatchBuilder: [{Name}] Source spell {E} not found in Spell.dbc — skipping",
                            request.SpellName, request.SourceSpellEntry);
                        result.Errors.Add($"Spell #{request.SpellEntry} ({request.SpellName}): source {request.SourceSpellEntry} not in Spell.dbc");
                        continue;
                    }

                    var newSpellRow = spellDbc.CloneRow(request.SourceSpellEntry, request.SpellEntry);
                    spellDbc.PatchRow(request.SpellEntry, FIELD_SPELL_VISUAL_ID, cloneResult.NewVisualId);

                    // ── Icon ──
                    uint finalIconId;
                    byte[]? customBlpBytes = null;
                    string? customBlpMpqPath = null;

                    if (!string.IsNullOrEmpty(request.IconPngPath) && File.Exists(request.IconPngPath))
                    {
                        customBlpBytes = _blpWriter.ConvertPngToBlp(request.IconPngPath, 64);
                        if (customBlpBytes != null)
                        {
                            string sanitized = SanitizeName(request.SpellName);
                            if (string.IsNullOrEmpty(sanitized)) sanitized = $"Spell_{request.SpellEntry}";
                            finalIconId = PatchSpellIconDbc(spellIconDbc, sanitized);
                            customBlpMpqPath = $@"Interface\Icons\CustomSpell_{sanitized}.blp";
                        }
                        else
                        {
                            finalIconId = request.SpellIconId ?? 1;
                        }
                    }
                    else
                    {
                        finalIconId = request.SpellIconId ?? 1;
                    }

                    spellDbc.PatchRow(request.SpellEntry, FIELD_SPELL_ICON_ID, finalIconId);

                    // ── School, attributes, name, subtext, description, tooltip ──
                    if (request.SchoolMask.HasValue)
                        spellDbc.PatchRow(request.SpellEntry, FIELD_SCHOOL_MASK, request.SchoolMask.Value);

                    uint attrs = newSpellRow[FIELD_ATTRIBUTES];
                    attrs &= ~SPELL_ATTR_HIDDEN;
                    spellDbc.PatchRow(request.SpellEntry, FIELD_ATTRIBUTES, attrs);

                    uint nameOfs = spellDbc.AddString(request.SpellName);
                    spellDbc.PatchRow(request.SpellEntry, FIELD_NAME_ENUS, nameOfs);
                    for (int i = 1; i < 8; i++) spellDbc.PatchRow(request.SpellEntry, FIELD_NAME_ENUS + i, 0);
                    spellDbc.PatchRow(request.SpellEntry, FIELD_NAME_FLAGS, LOCALE_FLAGS);

                    if (!string.IsNullOrEmpty(request.NameSubtext))
                    {
                        uint subtextOfs = spellDbc.AddString(request.NameSubtext);
                        spellDbc.PatchRow(request.SpellEntry, FIELD_SUBTEXT_ENUS, subtextOfs);
                        for (int i = 1; i < 8; i++) spellDbc.PatchRow(request.SpellEntry, FIELD_SUBTEXT_ENUS + i, 0);
                        spellDbc.PatchRow(request.SpellEntry, FIELD_SUBTEXT_FLAGS, LOCALE_FLAGS);
                    }
                    else
                    {
                        for (int i = 0; i < 8; i++) spellDbc.PatchRow(request.SpellEntry, FIELD_SUBTEXT_ENUS + i, 0);
                        spellDbc.PatchRow(request.SpellEntry, FIELD_SUBTEXT_FLAGS, LOCALE_FLAGS);
                    }

                    if (!string.IsNullOrEmpty(request.Description))
                    {
                        uint descOfs = spellDbc.AddString(request.Description);
                        spellDbc.PatchRow(request.SpellEntry, FIELD_DESC_ENUS, descOfs);
                        for (int i = 1; i < 8; i++) spellDbc.PatchRow(request.SpellEntry, FIELD_DESC_ENUS + i, 0);
                        spellDbc.PatchRow(request.SpellEntry, FIELD_DESC_FLAGS, LOCALE_FLAGS);
                    }

                    if (!string.IsNullOrEmpty(request.Tooltip))
                    {
                        uint tooltipOfs = spellDbc.AddString(request.Tooltip);
                        spellDbc.PatchRow(request.SpellEntry, FIELD_TOOLTIP_ENUS, tooltipOfs);
                        for (int i = 1; i < 8; i++) spellDbc.PatchRow(request.SpellEntry, FIELD_TOOLTIP_ENUS + i, 0);
                        spellDbc.PatchRow(request.SpellEntry, FIELD_TOOLTIP_FLAGS, LOCALE_FLAGS);
                    }

                    // ── Session 45: R1 gameplay field overrides (mana, damage, level, cast time, etc.) ──
                    // These come from spell_template overrides set by the user in the wizard.
                    // Without this block, R1's DBC row is a pure clone of the source spell,
                    // so the server/client uses the source's mana cost, damage, etc.
                    if (request.ManaCost.HasValue)
                        spellDbc.PatchRow(request.SpellEntry, FIELD_MANA_COST, (uint)request.ManaCost.Value);
                    if (request.EffectBasePoints0.HasValue)
                        spellDbc.PatchRow(request.SpellEntry, FIELD_EFFECT_BASE_POINTS_0, (uint)request.EffectBasePoints0.Value);
                    if (request.EffectDieSides0.HasValue)
                        spellDbc.PatchRow(request.SpellEntry, FIELD_EFFECT_DIE_SIDES_0, (uint)(request.EffectDieSides0.Value + 1)); // DB→DBC: +1
                    if (request.EffectBasePoints1.HasValue)
                        spellDbc.PatchRow(request.SpellEntry, FIELD_EFFECT_BASE_POINTS_1, (uint)request.EffectBasePoints1.Value);
                    if (request.SpellLevel.HasValue)
                        spellDbc.PatchRow(request.SpellEntry, FIELD_SPELL_LEVEL, (uint)request.SpellLevel.Value);
                    if (request.BaseLevel.HasValue)
                        spellDbc.PatchRow(request.SpellEntry, FIELD_BASE_LEVEL, (uint)request.BaseLevel.Value);
                    if (request.MaxLevel.HasValue)
                        spellDbc.PatchRow(request.SpellEntry, FIELD_MAX_LEVEL, (uint)request.MaxLevel.Value);
                    if (request.CastingTimeIndex.HasValue)
                        spellDbc.PatchRow(request.SpellEntry, FIELD_CASTING_TIME_INDEX, (uint)request.CastingTimeIndex.Value);
                    if (request.RangeIndex.HasValue)
                        spellDbc.PatchRow(request.SpellEntry, FIELD_RANGE_INDEX, (uint)request.RangeIndex.Value);
                    if (request.EffectRealPointsPerLevel0.HasValue)
                        spellDbc.PatchRowFloat(request.SpellEntry, FIELD_EFFECT_REAL_PPL_0, request.EffectRealPointsPerLevel0.Value);

                    // ── SkillLineAbility ──
                    bool slaExists = false;
                    foreach (var row in skillLineAbilityDbc.GetAllRows())
                    {
                        if (row.Length > 2 && row[2] == request.SpellEntry) { slaExists = true; break; }
                    }
                    if (!slaExists)
                    {
                        uint newSlaId = skillLineAbilityDbc.GetMaxId() + 1;
                        int slaFieldCount = skillLineAbilityDbc.RecordSize / 4;
                        var slaRow = new uint[slaFieldCount];
                        slaRow[0] = newSlaId;
                        slaRow[1] = (uint)request.SkillId;
                        slaRow[2] = request.SpellEntry;
                        slaRow[3] = 0;
                        slaRow[4] = (uint)request.ClassMask;
                        if (slaFieldCount > 5) slaRow[5] = 1;
                        skillLineAbilityDbc.AddRow(slaRow);
                    }

                    // ── M2 files ──
                    foreach (var effectFile in cloneResult.EffectFiles)
                    {
                        byte[]? originalM2 = ReadFileFromClientMpq(effectFile.OriginalM2Path);
                        if (originalM2 == null)
                        {
                            string basePath = effectFile.OriginalM2Path;
                            if (basePath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
                            {
                                string stem = basePath.Substring(0, basePath.Length - 3);
                                originalM2 = ReadFileFromClientMpq(stem + ".mdx")
                                          ?? ReadFileFromClientMpq(stem + ".mdl");
                            }
                        }

                        if (originalM2 == null)
                        {
                            // Revert DBC FilePath to vanilla
                            uint sourceEffectId = cloneResult.EffectNameIdMap
                                .FirstOrDefault(kv => kv.Value == effectFile.NewEffectId).Key;
                            var sourceRow = effectNameDbc.GetRow(sourceEffectId);
                            if (sourceRow != null)
                                effectNameDbc.PatchRow(effectFile.NewEffectId, 2, sourceRow[2]);
                            continue;
                        }

                        // Resolve per-role particle params
                        string phase = effectFile.EffectRole.Split('_')[0];
                        M2ParticlePatcher.ParticlePatchParams? effectParams = request.ParticleParams;
                        if (request.PerRoleParams != null)
                        {
                            if (request.PerRoleParams.TryGetValue(phase, out var phaseParams))
                                effectParams = phaseParams;
                            else if (request.PerRoleParams.TryGetValue(effectFile.EffectRole, out var roleParams))
                                effectParams = roleParams;
                        }

                        // ── Apply particle parameter patches (Tier A — existing) ──
                        byte[] finalM2;
                        if (effectParams != null)
                        {
                            byte[]? patchedM2 = M2ParticlePatcher.PatchParticles(originalM2, effectParams);
                            finalM2 = patchedM2 ?? (byte[])originalM2.Clone();
                            if (patchedM2 != null) totalM2s++;
                        }
                        else
                        {
                            finalM2 = (byte[])originalM2.Clone();
                        }

                        // ── Apply texture replacements (Tier B — Session 14) ──
                        if (request.PerPhaseTextures != null &&
                            request.PerPhaseTextures.TryGetValue(phase, out var phaseTextures))
                        {
                            var replacementMap = new Dictionary<int, string>();
                            foreach (var texRepl in phaseTextures)
                            {
                                replacementMap[texRepl.TextureIndex] = texRepl.ReplacementMpqPath;
                                if (texRepl.BlpBytes.Length > 0)
                                    mpqBuilder.AddFile(texRepl.ReplacementMpqPath, texRepl.BlpBytes);
                            }

                            int texPatched = M2TextureParser.PatchTextureFilenames(finalM2, replacementMap);
                            if (texPatched > 0)
                            {
                                _logger.LogInformation(
                                    "PatchBuilder: [{Name}] Patched {Count} texture(s) in {Role} M2",
                                    request.SpellName, texPatched, effectFile.EffectRole);
                            }
                        }

                        // ── Apply blendMode override (Tier C — Session 14) ──
                        if (request.PerPhaseBlendMode != null &&
                            request.PerPhaseBlendMode.TryGetValue(phase, out var blendMode))
                        {
                            int blendPatched = M2TextureParser.PatchBlendMode(finalM2, blendMode);
                            if (blendPatched > 0)
                            {
                                _logger.LogInformation(
                                    "PatchBuilder: [{Name}] Set blendMode={Mode} on {Count} emitter(s) in {Role}",
                                    request.SpellName, blendMode, blendPatched, effectFile.EffectRole);
                            }
                        }

                        // ── Apply emitterType override (Tier C — Session 14) ──
                        if (request.PerPhaseEmitterType != null &&
                            request.PerPhaseEmitterType.TryGetValue(phase, out var emitterType))
                        {
                            int typePatched = M2TextureParser.PatchEmitterType(finalM2, emitterType);
                            if (typePatched > 0)
                            {
                                _logger.LogInformation(
                                    "PatchBuilder: [{Name}] Set emitterType={Type} on {Count} emitter(s) in {Role}",
                                    request.SpellName, emitterType, typePatched, effectFile.EffectRole);
                            }
                        }

                        // ── Apply pre-patched M2 override (Session 30 — experiment lab / tuning) ──
                        if (request.PerPhasePatchedM2s != null &&
                            request.PerPhasePatchedM2s.TryGetValue(phase, out var patchedM2Bytes))
                        {
                            finalM2 = patchedM2Bytes;
                            _logger.LogInformation(
                                "PatchBuilder: [{Name}] Using pre-patched M2 for {Phase} ({Bytes} bytes)",
                                request.SpellName, phase, patchedM2Bytes.Length);
                        }

                        mpqBuilder.AddFile(effectFile.CustomM2Path, finalM2);
                    }

                    // Add custom BLP
                    if (customBlpBytes != null && customBlpMpqPath != null)
                        mpqBuilder.AddFile(customBlpMpqPath, customBlpBytes);

                    result.SpellsIncluded.Add((int)request.SpellEntry);
                    result.VisualIdMap[(int)request.SpellEntry] = cloneResult.NewVisualId;
                    result.IconIdMap[(int)request.SpellEntry] = finalIconId;

                    _logger.LogInformation("PatchBuilder: [{Name}] Added to unified patch (visual={V}, icon={I})",
                        request.SpellName, cloneResult.NewVisualId, finalIconId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PatchBuilder: Failed to process spell #{E} ({Name}) for unified patch",
                        request.SpellEntry, request.SpellName);
                    result.Errors.Add($"Spell #{request.SpellEntry} ({request.SpellName}): {ex.Message}");
                }
            }

            // ── Step 2b: Patch Spell.dbc + SkillLineAbility.dbc for rank chain entries (Session 33) ──
            // Ranks 2+ don't have their own visual config (they share rank 1's visuals),
            // so they're not processed in the main loop above. But the client needs Spell.dbc
            // rows for each rank (tooltips, spellbook) and SkillLineAbility.dbc rows (tab placement).
            //
            // Each rank clones its Spell.dbc row from the corresponding vanilla source rank
            // (e.g., custom R5 clones from vanilla Fireball R5 = entry 8400), preserving all
            // per-rank DBC fields (cast time, range, effect structure, duration, etc.).
            // We then patch in: custom entry ID, spell name, "Rank N" subtext, visual ID
            // (from rank 1), icon ID (from rank 1), school, and clear the hidden attribute.
            int ranksPatched = 0;
            foreach (var request in spells)
            {
                if (request.AdditionalRanks == null || request.AdditionalRanks.Count == 0)
                    continue;

                // Get rank 1's visual ID and icon ID from what we just processed
                uint r1VisualId = result.VisualIdMap.TryGetValue((int)request.SpellEntry, out var vid) ? vid : 0;
                uint r1IconId = result.IconIdMap.TryGetValue((int)request.SpellEntry, out var iid) ? iid : (request.SpellIconId ?? 1);

                foreach (var rank in request.AdditionalRanks)
                {
                    try
                    {
                        if (rank.Entry > 65000)
                        {
                            _logger.LogWarning("PatchBuilder: Skipping rank {R} entry {E} — exceeds uint16 range",
                                rank.Rank, rank.Entry);
                            continue;
                        }

                        // Clone Spell.dbc row from the corresponding source rank
                        var sourceRankRow = spellDbc.GetRow(rank.SourceRankEntry);
                        if (sourceRankRow == null)
                        {
                            _logger.LogWarning("PatchBuilder: [{Name} R{R}] Source rank {Src} not found in Spell.dbc — skipping",
                                rank.SpellName, rank.Rank, rank.SourceRankEntry);
                            result.Errors.Add($"Rank {rank.Rank} (#{rank.Entry}): source rank {rank.SourceRankEntry} not in Spell.dbc");
                            continue;
                        }

                        // Check if already exists (idempotent rebuild)
                        if (spellDbc.GetRow(rank.Entry) != null)
                        {
                            _logger.LogInformation("PatchBuilder: [{Name} R{R}] Entry {E} already in Spell.dbc, skipping",
                                rank.SpellName, rank.Rank, rank.Entry);
                            continue;
                        }

                        var newRow = spellDbc.CloneRow(rank.SourceRankEntry, rank.Entry);

                        // Visual — same as rank 1 (all ranks share the same visual chain)
                        if (r1VisualId > 0)
                            spellDbc.PatchRow(rank.Entry, FIELD_SPELL_VISUAL_ID, r1VisualId);

                        // Icon — same as rank 1
                        spellDbc.PatchRow(rank.Entry, FIELD_SPELL_ICON_ID, r1IconId);

                        // School
                        if (rank.SchoolMask.HasValue)
                            spellDbc.PatchRow(rank.Entry, FIELD_SCHOOL_MASK, rank.SchoolMask.Value);

                        // Clear hidden attribute
                        uint attrs = newRow[FIELD_ATTRIBUTES];
                        attrs &= ~SPELL_ATTR_HIDDEN;
                        spellDbc.PatchRow(rank.Entry, FIELD_ATTRIBUTES, attrs);

                        // Name — same for all ranks
                        uint nameOfs = spellDbc.AddString(rank.SpellName);
                        spellDbc.PatchRow(rank.Entry, FIELD_NAME_ENUS, nameOfs);
                        for (int i = 1; i < 8; i++) spellDbc.PatchRow(rank.Entry, FIELD_NAME_ENUS + i, 0);
                        spellDbc.PatchRow(rank.Entry, FIELD_NAME_FLAGS, LOCALE_FLAGS);

                        // Subtext — "Rank N"
                        string rankSubtext = $"Rank {rank.Rank}";
                        uint subtextOfs = spellDbc.AddString(rankSubtext);
                        spellDbc.PatchRow(rank.Entry, FIELD_SUBTEXT_ENUS, subtextOfs);
                        for (int i = 1; i < 8; i++) spellDbc.PatchRow(rank.Entry, FIELD_SUBTEXT_ENUS + i, 0);
                        spellDbc.PatchRow(rank.Entry, FIELD_SUBTEXT_FLAGS, LOCALE_FLAGS);

                        // Description — prefer R1's custom description from custom_spell_meta
                        // (request.Description) over the rank's spell_template description,
                        // which is the vanilla source text copied by CloneSpellAsync.
                        // Only fall back to rank.Description if R1 has no custom description.
                        string? rankDesc = !string.IsNullOrEmpty(request.Description)
                            ? request.Description
                            : rank.Description;

                        if (!string.IsNullOrEmpty(rankDesc))
                        {
                            uint descOfs = spellDbc.AddString(rankDesc);
                            spellDbc.PatchRow(rank.Entry, FIELD_DESC_ENUS, descOfs);
                            for (int i = 1; i < 8; i++) spellDbc.PatchRow(rank.Entry, FIELD_DESC_ENUS + i, 0);
                            spellDbc.PatchRow(rank.Entry, FIELD_DESC_FLAGS, LOCALE_FLAGS);
                        }

                        // Tooltip — same logic: prefer R1's custom tooltip
                        string? rankTooltip = !string.IsNullOrEmpty(request.Tooltip)
                            ? request.Tooltip
                            : null; // no rank-level tooltip field exists, just use R1's or leave clone
                        if (!string.IsNullOrEmpty(rankTooltip))
                        {
                            uint tooltipOfs = spellDbc.AddString(rankTooltip);
                            spellDbc.PatchRow(rank.Entry, FIELD_TOOLTIP_ENUS, tooltipOfs);
                            for (int i = 1; i < 8; i++) spellDbc.PatchRow(rank.Entry, FIELD_TOOLTIP_ENUS + i, 0);
                            spellDbc.PatchRow(rank.Entry, FIELD_TOOLTIP_FLAGS, LOCALE_FLAGS);
                        }

                        // ── Effect/gameplay fields — patch DBC with custom-scaled values ──
                        // These ensure tooltips ($s1, $o2) show the custom spell's actual
                        // damage/mana/level values, not the vanilla source rank's values.
                        //
                        // CRITICAL: DBC EffectDieSides = spell_template effectDieSides + 1
                        // because DBC formula is: damage = BasePoints + rand(1, DieSides)
                        // while spell_template stores effectDieSides = damageMax - damageMin.
                        if (rank.EffectBasePoints0.HasValue)
                            spellDbc.PatchRow(rank.Entry, FIELD_EFFECT_BASE_POINTS_0,
                                (uint)rank.EffectBasePoints0.Value); // signed → uint reinterpret

                        if (rank.EffectDieSides0.HasValue)
                            spellDbc.PatchRow(rank.Entry, FIELD_EFFECT_DIE_SIDES_0,
                                (uint)(rank.EffectDieSides0.Value + 1)); // DB→DBC: +1

                        if (rank.EffectBasePoints1.HasValue)
                            spellDbc.PatchRow(rank.Entry, FIELD_EFFECT_BASE_POINTS_1,
                                (uint)rank.EffectBasePoints1.Value);

                        if (rank.ManaCost.HasValue)
                            spellDbc.PatchRow(rank.Entry, FIELD_MANA_COST, (uint)rank.ManaCost.Value);

                        if (rank.SpellLevel.HasValue)
                            spellDbc.PatchRow(rank.Entry, FIELD_SPELL_LEVEL, (uint)rank.SpellLevel.Value);

                        if (rank.BaseLevel.HasValue)
                            spellDbc.PatchRow(rank.Entry, FIELD_BASE_LEVEL, (uint)rank.BaseLevel.Value);

                        if (rank.CastingTimeIndex.HasValue)
                            spellDbc.PatchRow(rank.Entry, FIELD_CASTING_TIME_INDEX, (uint)rank.CastingTimeIndex.Value);

                        if (rank.EffectRealPointsPerLevel0.HasValue)
                            spellDbc.PatchRowFloat(rank.Entry, FIELD_EFFECT_REAL_PPL_0,
                                rank.EffectRealPointsPerLevel0.Value);

                        // ── SkillLineAbility.dbc — tab placement for this rank ──
                        if (rank.SkillLineAbilityData.HasValue)
                        {
                            var sla = rank.SkillLineAbilityData.Value;

                            // Check for existing SLA row
                            bool slaExists = false;
                            foreach (var row in skillLineAbilityDbc.GetAllRows())
                            {
                                if (row.Length > 2 && row[2] == rank.Entry) { slaExists = true; break; }
                            }

                            if (!slaExists)
                            {
                                uint newSlaId = skillLineAbilityDbc.GetMaxId() + 1;
                                int slaFieldCount = skillLineAbilityDbc.RecordSize / 4;
                                var slaRow = new uint[slaFieldCount];
                                slaRow[0] = newSlaId;                       // ID
                                slaRow[1] = (uint)sla.skillId;              // SkillLine = spellbook tab
                                slaRow[2] = rank.Entry;                     // SpellId
                                slaRow[3] = 0;                              // RaceMask = all
                                slaRow[4] = (uint)sla.classMask;            // ClassMask
                                if (slaFieldCount > 5) slaRow[5] = 1;      // AcquireMethod = learned
                                if (slaFieldCount > 8)
                                    slaRow[8] = (uint)sla.supersededBySpell; // SupersededBySpell
                                skillLineAbilityDbc.AddRow(slaRow);
                            }
                        }
                        else
                        {
                            // No skill tab specified — add a generic SLA row so spell shows in spellbook
                            bool slaExists = false;
                            foreach (var row in skillLineAbilityDbc.GetAllRows())
                            {
                                if (row.Length > 2 && row[2] == rank.Entry) { slaExists = true; break; }
                            }
                            if (!slaExists)
                            {
                                uint newSlaId = skillLineAbilityDbc.GetMaxId() + 1;
                                int slaFieldCount = skillLineAbilityDbc.RecordSize / 4;
                                var slaRow = new uint[slaFieldCount];
                                slaRow[0] = newSlaId;
                                slaRow[1] = 0; // General tab
                                slaRow[2] = rank.Entry;
                                if (slaFieldCount > 5) slaRow[5] = 1;
                                skillLineAbilityDbc.AddRow(slaRow);
                            }
                        }

                        ranksPatched++;
                        result.SpellsIncluded.Add((int)rank.Entry);

                        _logger.LogInformation(
                            "PatchBuilder: [{Name} R{R}] Patched rank entry {E} (src={Src}, vis={V}, icon={I})",
                            rank.SpellName, rank.Rank, rank.Entry, rank.SourceRankEntry, r1VisualId, r1IconId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "PatchBuilder: Failed to patch rank {R} entry {E} for {Name}",
                            rank.Rank, rank.Entry, rank.SpellName);
                        result.Errors.Add($"Rank {rank.Rank} (#{rank.Entry}): {ex.Message}");
                    }
                }
            }

            if (ranksPatched > 0)
                _logger.LogInformation("PatchBuilder: Patched {Count} additional rank entries into Spell.dbc + SkillLineAbility.dbc",
                    ranksPatched);

            // ── Step 2c: Add trainer wrapper spells to Spell.dbc (Session 34) ──
            // VMaNGOS trainers only process SPELL_EFFECT_LEARN_SPELL (36) wrapper spells.
            // Our wrappers exist in spell_template (server loads them), but the client also
            // needs Spell.dbc entries to render them in the trainer UI.
            // Clone from vanilla wrapper 1173 (Fireball R2 learn-spell) and patch in
            // custom entry, name, subtext, trigger spell ID, and icon.
            int wrappersPatched = 0;

            // Build a map: taught spell entry → R1 entry (for icon lookup from IconIdMap)
            var taughtToR1 = new Dictionary<uint, uint>();
            foreach (var request in spells)
            {
                // R1 teaches itself
                taughtToR1[request.SpellEntry] = request.SpellEntry;
                // R2+ ranks all map back to R1
                if (request.AdditionalRanks != null)
                {
                    foreach (var rank in request.AdditionalRanks)
                        taughtToR1[rank.Entry] = request.SpellEntry;
                }
            }

            foreach (var request in spells)
            {
                if (request.TrainerWrappers == null || request.TrainerWrappers.Count == 0)
                    continue;

                foreach (var wrapper in request.TrainerWrappers)
                {
                    try
                    {
                        if (wrapper.WrapperEntry > 65000)
                        {
                            _logger.LogWarning("PatchBuilder: Skipping trainer wrapper {E} — exceeds uint16 range",
                                wrapper.WrapperEntry);
                            continue;
                        }

                        // Skip if already in DBC (idempotent rebuild)
                        if (spellDbc.GetRow(wrapper.WrapperEntry) != null)
                            continue;

                        // Clone from vanilla learn-spell wrapper (1173 = Fireball R2 trainer spell)
                        var sourceRow = spellDbc.GetRow(TRAINER_WRAPPER_CLONE_SOURCE);
                        if (sourceRow == null)
                        {
                            _logger.LogWarning("PatchBuilder: Trainer wrapper clone source {S} not found in Spell.dbc",
                                TRAINER_WRAPPER_CLONE_SOURCE);
                            continue;
                        }

                        spellDbc.CloneRow(TRAINER_WRAPPER_CLONE_SOURCE, wrapper.WrapperEntry);

                        // Patch the trigger spell to point to our custom spell
                        spellDbc.PatchRow(wrapper.WrapperEntry, FIELD_EFFECT_0, 36); // SPELL_EFFECT_LEARN_SPELL
                        spellDbc.PatchRow(wrapper.WrapperEntry, FIELD_EFFECT_TRIGGER_SPELL_0, wrapper.TeachesSpellEntry);

                        // Patch name + subtext for trainer UI display
                        uint nameOfs = spellDbc.AddString(wrapper.SpellName);
                        spellDbc.PatchRow(wrapper.WrapperEntry, FIELD_NAME_ENUS, nameOfs);
                        for (int i = 1; i < 8; i++) spellDbc.PatchRow(wrapper.WrapperEntry, FIELD_NAME_ENUS + i, 0);
                        spellDbc.PatchRow(wrapper.WrapperEntry, FIELD_NAME_FLAGS, LOCALE_FLAGS);

                        uint subtextOfs = spellDbc.AddString(wrapper.RankSubtext);
                        spellDbc.PatchRow(wrapper.WrapperEntry, FIELD_SUBTEXT_ENUS, subtextOfs);
                        for (int i = 1; i < 8; i++) spellDbc.PatchRow(wrapper.WrapperEntry, FIELD_SUBTEXT_ENUS + i, 0);
                        spellDbc.PatchRow(wrapper.WrapperEntry, FIELD_SUBTEXT_FLAGS, LOCALE_FLAGS);

                        // Patch icon — resolve R1's icon from IconIdMap (populated in Step 2a)
                        // wrapper.IconId from DB is stale at initial create time (icon gen hasn't run yet)
                        uint wrapperIconId = 0;
                        if (taughtToR1.TryGetValue(wrapper.TeachesSpellEntry, out uint r1ForWrapper))
                            result.IconIdMap.TryGetValue((int)r1ForWrapper, out wrapperIconId);
                        if (wrapperIconId > 0)
                            spellDbc.PatchRow(wrapper.WrapperEntry, FIELD_SPELL_ICON_ID, wrapperIconId);

                        wrappersPatched++;
                        result.SpellsIncluded.Add((int)wrapper.WrapperEntry);

                        _logger.LogInformation("PatchBuilder: Added trainer wrapper #{W} → teaches #{T} ({Name} {Rank}), icon={Icon} to Spell.dbc",
                            wrapper.WrapperEntry, wrapper.TeachesSpellEntry, wrapper.SpellName, wrapper.RankSubtext, wrapperIconId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "PatchBuilder: Failed to add trainer wrapper {E}", wrapper.WrapperEntry);
                        result.Errors.Add($"Trainer wrapper #{wrapper.WrapperEntry}: {ex.Message}");
                    }
                }
            }

            if (wrappersPatched > 0)
                _logger.LogInformation("PatchBuilder: Added {Count} trainer wrapper entries to Spell.dbc", wrappersPatched);

            // ── Step 3: Serialize all DBCs ──
            byte[] spellDbcBytes = spellDbc.Write();
            byte[] visualDbcBytes = visualDbc.Write();
            byte[] kitDbcBytes = kitDbc.Write();
            byte[] efnDbcBytes = effectNameDbc.Write();
            byte[] slaDbcBytes = skillLineAbilityDbc.Write();
            byte[] iconDbcBytes = spellIconDbc.Write();

            mpqBuilder.AddFile("DBFilesClient\\Spell.dbc", spellDbcBytes);
            mpqBuilder.AddFile("DBFilesClient\\SpellVisual.dbc", visualDbcBytes);
            mpqBuilder.AddFile("DBFilesClient\\SpellVisualKit.dbc", kitDbcBytes);
            mpqBuilder.AddFile("DBFilesClient\\SpellVisualEffectName.dbc", efnDbcBytes);
            mpqBuilder.AddFile("DBFilesClient\\SkillLineAbility.dbc", slaDbcBytes);
            mpqBuilder.AddFile("DBFilesClient\\SpellIcon.dbc", iconDbcBytes);

            // ── Step 4: Write server DBCs ──
            try
            {
                File.WriteAllBytes(Path.Combine(DbcPath, "Spell.dbc"), spellDbcBytes);
                File.WriteAllBytes(Path.Combine(DbcPath, "SpellVisual.dbc"), visualDbcBytes);
                File.WriteAllBytes(Path.Combine(DbcPath, "SpellVisualKit.dbc"), kitDbcBytes);
                File.WriteAllBytes(Path.Combine(DbcPath, "SpellVisualEffectName.dbc"), efnDbcBytes);
                File.WriteAllBytes(Path.Combine(DbcPath, "SkillLineAbility.dbc"), slaDbcBytes);
                File.WriteAllBytes(Path.Combine(DbcPath, "SpellIcon.dbc"), iconDbcBytes);
                _logger.LogInformation("PatchBuilder: Updated ALL server DBCs");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PatchBuilder: Could not write server-side DBCs");
            }

            // ── Step 5: Build the unified MPQ ──
            Directory.CreateDirectory(PatchOutputPath);
            string patchFileName = "patch-3.MPQ";
            string fullPath = Path.Combine(PatchOutputPath, patchFileName);

            // Delete old unified patch and any per-spell patches
            if (File.Exists(fullPath))
                File.Delete(fullPath);
            foreach (var old in Directory.GetFiles(PatchOutputPath, "patch-custom-*.MPQ"))
                File.Delete(old);

            if (mpqBuilder.FileCount > 6) // More than just the 6 DBCs = has actual content
            {
                if (!mpqBuilder.Build(fullPath))
                {
                    _logger.LogError("PatchBuilder: Unified MPQ build failed");
                    result.Success = false;
                    result.Errors.Add("MPQ build failed");
                    return result;
                }

                result.PatchFilePath = fullPath;
                result.PatchFileName = patchFileName;
                result.TotalFiles = mpqBuilder.FileCount;
            }
            else if (spells.Count == 0)
            {
                // No custom spells — no patch needed. Clean state.
                _logger.LogInformation("PatchBuilder: No custom spells — no patch generated (clean state)");
                result.PatchFileName = "(none)";
            }

            result.Success = true;
            result.M2FilesPatched = totalM2s;

            _logger.LogInformation(
                "PatchBuilder: Unified patch complete — {Spells} spells, {Files} files, {M2s} M2s, {Errors} errors",
                result.SpellsIncluded.Count, result.TotalFiles, totalM2s, result.Errors.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PatchBuilder: Unified patch rebuild failed");
            result.Success = false;
            result.Errors.Add($"Fatal: {ex.Message}");
            return result;
        }
    }

    public async Task<PatchResult?> BuildSpellPatchAsync(SpellPatchRequest request)
    {
        try
        {
            _logger.LogInformation("PatchBuilder: Starting patch for {Name} (#{Entry}, srcVis={SrcVis})",
                request.SpellName, request.SpellEntry, request.SourceVisualId);

            if (request.SpellEntry > 65000)
            {
                _logger.LogError("PatchBuilder: Entry {E} exceeds uint16 safe range (60000–65000)", request.SpellEntry);
                return null;
            }

            // ── Step 1: Load CLEAN DBCs from server directory ──
            // Read from server DBC dir (correct build 5875, 173-field Spell.dbc) and
            // scrub any custom entries left over from previous generations.
            // Each DBC uses its own scrub floor — visual chain DBCs scrub at 10000,
            // spell/icon/SLA DBCs scrub at 60000.
            var spellDbc = ReadCleanDbc("DBFilesClient\\Spell.dbc");
            var visualDbc = ReadCleanDbc("DBFilesClient\\SpellVisual.dbc");
            var kitDbc = ReadCleanDbc("DBFilesClient\\SpellVisualKit.dbc");
            var effectNameDbc = ReadCleanDbc("DBFilesClient\\SpellVisualEffectName.dbc");
            var skillLineAbilityDbc = ReadCleanSlaDbc();
            var spellIconDbc = ReadCleanDbc("DBFilesClient\\SpellIcon.dbc");

            _logger.LogInformation(
                "PatchBuilder: Loaded clean DBCs — Spell:{S} Visual:{V} Kit:{K} Effect:{E} SLA:{SLA} Icon:{I}",
                spellDbc.RecordCount, visualDbc.RecordCount, kitDbc.RecordCount,
                effectNameDbc.RecordCount, skillLineAbilityDbc.RecordCount, spellIconDbc.RecordCount);

            // ── Step 2: New IDs — visual chain starts at 10000, spell stuff at 60000 ──
            uint newVisualId = Math.Max(visualDbc.GetMaxId() + 1, CUSTOM_VISUAL_FLOOR);
            uint baseKitId = Math.Max(kitDbc.GetMaxId() + 1, CUSTOM_VISUAL_FLOOR);
            uint baseEffectId = Math.Max(effectNameDbc.GetMaxId() + 1, CUSTOM_VISUAL_FLOOR);

            // ── Step 3: Clone visual chain ──
            var cloneResult = SpellVisualCloner.Clone(
                visualDbc, kitDbc, effectNameDbc,
                request.SourceVisualId,
                newVisualId, baseKitId, baseEffectId,
                SanitizeName(request.SpellName));

            _logger.LogInformation("PatchBuilder: Cloned visual — V:{V} Kits:{K} Effects:{E} Files:{F}",
                cloneResult.NewVisualId, cloneResult.KitIdMap.Count,
                cloneResult.EffectNameIdMap.Count, cloneResult.EffectFiles.Count);

            // ── Step 4: Add spell to Spell.dbc ──
            var sourceSpellRow = spellDbc.GetRow(request.SourceSpellEntry);
            if (sourceSpellRow == null)
            {
                _logger.LogError("PatchBuilder: Source spell {E} not found in Spell.dbc", request.SourceSpellEntry);
                return null;
            }

            var newSpellRow = spellDbc.CloneRow(request.SourceSpellEntry, request.SpellEntry);

            // SpellVisualID[0] = field 115 (VERIFIED)
            spellDbc.PatchRow(request.SpellEntry, FIELD_SPELL_VISUAL_ID, cloneResult.NewVisualId);

            // ── Step 4b: SpellIconID (field 117) ──
            // If a custom PNG was provided AND BLP conversion succeeds, we add a new
            // row to SpellIcon.dbc pointing at our custom texture path and use that
            // new row's ID. If no PNG or conversion fails, fall back to the vanilla
            // icon ID supplied by the controller (school-appropriate default).
            uint finalIconId;
            byte[]? customBlpBytes = null;
            string? customBlpMpqPath = null;

            if (!string.IsNullOrEmpty(request.IconPngPath) && File.Exists(request.IconPngPath))
            {
                customBlpBytes = _blpWriter.ConvertPngToBlp(request.IconPngPath, 64);
                if (customBlpBytes != null)
                {
                    string sanitized = SanitizeName(request.SpellName);
                    if (string.IsNullOrEmpty(sanitized)) sanitized = $"Spell_{request.SpellEntry}";

                    finalIconId = PatchSpellIconDbc(spellIconDbc, sanitized);
                    customBlpMpqPath = $@"Interface\Icons\CustomSpell_{sanitized}.blp";

                    _logger.LogInformation(
                        "PatchBuilder: Custom icon → SpellIconID={Id}, BLP path '{Path}' ({Size} bytes)",
                        finalIconId, customBlpMpqPath, customBlpBytes.Length);
                }
                else
                {
                    _logger.LogWarning(
                        "PatchBuilder: BLP conversion failed for {Path} — falling back to vanilla icon",
                        request.IconPngPath);
                    finalIconId = request.SpellIconId ?? 1;
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(request.IconPngPath))
                {
                    _logger.LogWarning(
                        "PatchBuilder: IconPngPath '{Path}' not found on disk — using vanilla fallback",
                        request.IconPngPath);
                }
                finalIconId = request.SpellIconId ?? 1;
            }

            spellDbc.PatchRow(request.SpellEntry, FIELD_SPELL_ICON_ID, finalIconId);

            // SchoolMask — field 1 in DBC maps to spell_template.school
            if (request.SchoolMask.HasValue)
                spellDbc.PatchRow(request.SpellEntry, FIELD_SCHOOL_MASK, request.SchoolMask.Value);

            // Clear SPELL_ATTR_HIDDEN_CLIENTSIDE (bit 0x80) from Attributes = field 6 (VERIFIED)
            uint attrs = newSpellRow[FIELD_ATTRIBUTES];
            attrs &= ~SPELL_ATTR_HIDDEN;
            spellDbc.PatchRow(request.SpellEntry, FIELD_ATTRIBUTES, attrs);

            // Name: fields 120–128 (enUS at 120, locale flags at 128) (VERIFIED)
            uint nameOfs = spellDbc.AddString(request.SpellName);
            spellDbc.PatchRow(request.SpellEntry, FIELD_NAME_ENUS, nameOfs);
            for (int i = 1; i < 8; i++) spellDbc.PatchRow(request.SpellEntry, FIELD_NAME_ENUS + i, 0);
            spellDbc.PatchRow(request.SpellEntry, FIELD_NAME_FLAGS, LOCALE_FLAGS);

            // NameSubtext: fields 129–137 (VERIFIED)
            if (!string.IsNullOrEmpty(request.NameSubtext))
            {
                uint subtextOfs = spellDbc.AddString(request.NameSubtext);
                spellDbc.PatchRow(request.SpellEntry, FIELD_SUBTEXT_ENUS, subtextOfs);
                for (int i = 1; i < 8; i++) spellDbc.PatchRow(request.SpellEntry, FIELD_SUBTEXT_ENUS + i, 0);
                spellDbc.PatchRow(request.SpellEntry, FIELD_SUBTEXT_FLAGS, LOCALE_FLAGS);
            }
            else
            {
                // Clear subtext (don't inherit source spell's rank)
                for (int i = 0; i < 8; i++) spellDbc.PatchRow(request.SpellEntry, FIELD_SUBTEXT_ENUS + i, 0);
                spellDbc.PatchRow(request.SpellEntry, FIELD_SUBTEXT_FLAGS, LOCALE_FLAGS);
            }

            // Description: fields 138–146 (VERIFIED)
            if (!string.IsNullOrEmpty(request.Description))
            {
                uint descOfs = spellDbc.AddString(request.Description);
                spellDbc.PatchRow(request.SpellEntry, FIELD_DESC_ENUS, descOfs);
                for (int i = 1; i < 8; i++) spellDbc.PatchRow(request.SpellEntry, FIELD_DESC_ENUS + i, 0);
                spellDbc.PatchRow(request.SpellEntry, FIELD_DESC_FLAGS, LOCALE_FLAGS);
            }

            // Tooltip: fields 147–155 (VERIFIED — was previously unmapped!)
            if (!string.IsNullOrEmpty(request.Tooltip))
            {
                uint tooltipOfs = spellDbc.AddString(request.Tooltip);
                spellDbc.PatchRow(request.SpellEntry, FIELD_TOOLTIP_ENUS, tooltipOfs);
                for (int i = 1; i < 8; i++) spellDbc.PatchRow(request.SpellEntry, FIELD_TOOLTIP_ENUS + i, 0);
                spellDbc.PatchRow(request.SpellEntry, FIELD_TOOLTIP_FLAGS, LOCALE_FLAGS);
            }

            // ── Step 5: Add SkillLineAbility.dbc entry ──
            // SkillLineAbility.dbc layout (vanilla, 15 fields per forensic dump):
            //   [0] ID
            //   [1] SkillLine — which skill tab (0 = General tab)
            //   [2] SpellId — the spell entry
            //   [3] RaceMask — 0 = all races
            //   [4] ClassMask — 0 = all classes
            //   [5] AcquireMethod — 1 = learned
            //   [6..14] other fields
            //
            // DEDUP: Check if a row already exists for this spell before adding
            uint slaMaxId = skillLineAbilityDbc.GetMaxId();
            bool slaExists = false;
            foreach (var row in skillLineAbilityDbc.GetAllRows())
            {
                if (row.Length > 2 && row[2] == request.SpellEntry)
                {
                    slaExists = true;
                    _logger.LogInformation("PatchBuilder: SkillLineAbility row already exists for spell {Spell}, skipping",
                        request.SpellEntry);
                    break;
                }
            }

            if (!slaExists)
            {
                uint newSlaId = slaMaxId + 1;
                int slaFieldCount = skillLineAbilityDbc.RecordSize / 4;
                var slaRow = new uint[slaFieldCount];
                slaRow[0] = newSlaId;           // ID
                slaRow[1] = (uint)request.SkillId;   // SkillLine = spellbook tab (e.g. 8 = Fire)
                slaRow[2] = request.SpellEntry; // SpellId
                slaRow[3] = 0;                  // RaceMask = all
                slaRow[4] = (uint)request.ClassMask; // ClassMask (e.g. 128 = Mage)
                if (slaFieldCount > 5) slaRow[5] = 1;  // AcquireMethod = learned
                skillLineAbilityDbc.AddRow(slaRow);

                _logger.LogInformation("PatchBuilder: Added SkillLineAbility #{Id} for spell {Spell}",
                    newSlaId, request.SpellEntry);
            }

            // ── Step 6: Patch M2 particle effects ──
            var mpqBuilder = new MpqBuilderService(_logger as ILogger<MpqBuilderService>);
            int patchedM2Count = 0;

            foreach (var effectFile in cloneResult.EffectFiles)
            {
                // Try to read the original M2 from client MPQs.
                // The OriginalM2Path comes from the DBC FilePath field [2], normalized to .m2.
                // If not found, also try .mdx and .mdl (some vanilla effects use these).
                byte[]? originalM2 = ReadFileFromClientMpq(effectFile.OriginalM2Path);
                if (originalM2 == null)
                {
                    // Try alternate extensions — vanilla MPQs sometimes store as .mdx or .mdl
                    string basePath = effectFile.OriginalM2Path;
                    if (basePath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
                    {
                        string stem = basePath.Substring(0, basePath.Length - 3);
                        originalM2 = ReadFileFromClientMpq(stem + ".mdx")
                                  ?? ReadFileFromClientMpq(stem + ".mdl");
                    }
                }

                if (originalM2 == null)
                {
                    _logger.LogWarning("PatchBuilder: Could not read M2 (tried .m2/.mdx/.mdl): {Path} — " +
                        "reverting DBC FilePath to original so client uses vanilla effect",
                        effectFile.OriginalM2Path);

                    // Revert the DBC FilePath (field 2) to the original vanilla path
                    // so the client loads the vanilla effect instead of showing green.
                    // The original path is still in the vanilla MPQs (e.g. .mdl files).
                    var effectRow = effectNameDbc.GetRow(effectFile.NewEffectId);
                    if (effectRow != null)
                    {
                        // Read the original effect's field[2] value from the cloned row.
                        // CloneRow copied the original's field[2] (vanilla path offset),
                        // then we overwrote it with the custom path. We need to find the
                        // original vanilla DBC entry to get its path back.
                        // Simplest: just look up what the source effect had.
                        // The source ID is in EffectNameIdMap (reversed).
                        uint sourceEffectId = cloneResult.EffectNameIdMap
                            .FirstOrDefault(kv => kv.Value == effectFile.NewEffectId).Key;
                        var sourceRow = effectNameDbc.GetRow(sourceEffectId);
                        if (sourceRow != null)
                        {
                            // Restore field[2] to the original vanilla FilePath stringref
                            effectNameDbc.PatchRow(effectFile.NewEffectId, 2, sourceRow[2]);
                            _logger.LogInformation("PatchBuilder: Reverted effect {Id} FilePath to vanilla offset {Ofs}",
                                effectFile.NewEffectId, sourceRow[2]);
                        }
                    }
                    continue;
                }

                // Apply particle patches if requested, otherwise use original bytes
                // Per-role params (Visual Designer) take priority over global ParticleParams
                string phase = effectFile.EffectRole.Split('_')[0];
                M2ParticlePatcher.ParticlePatchParams? effectParams = request.ParticleParams;

                if (request.PerRoleParams != null)
                {
                    if (request.PerRoleParams.TryGetValue(phase, out var phaseParams))
                    {
                        effectParams = phaseParams;
                        _logger.LogInformation("PatchBuilder: Using per-phase params for {Role} → phase '{Phase}'",
                            effectFile.EffectRole, phase);
                    }
                    else if (request.PerRoleParams.TryGetValue(effectFile.EffectRole, out var roleParams))
                    {
                        effectParams = roleParams;
                    }
                }

                // ── Apply particle parameter patches (Tier A — existing) ──
                byte[] finalM2;
                if (effectParams != null)
                {
                    byte[]? patchedM2 = M2ParticlePatcher.PatchParticles(originalM2, effectParams);
                    finalM2 = patchedM2 ?? (byte[])originalM2.Clone();
                    if (patchedM2 != null) patchedM2Count++;
                }
                else
                {
                    finalM2 = (byte[])originalM2.Clone();
                }

                // ── Apply texture replacements (Tier B — Session 14) ──
                if (request.PerPhaseTextures != null &&
                    request.PerPhaseTextures.TryGetValue(phase, out var phaseTextures))
                {
                    var replacementMap = new Dictionary<int, string>();
                    foreach (var texRepl in phaseTextures)
                    {
                        replacementMap[texRepl.TextureIndex] = texRepl.ReplacementMpqPath;
                        if (texRepl.BlpBytes.Length > 0)
                            mpqBuilder.AddFile(texRepl.ReplacementMpqPath, texRepl.BlpBytes);
                    }

                    int texPatched = M2TextureParser.PatchTextureFilenames(finalM2, replacementMap);
                    if (texPatched > 0)
                    {
                        _logger.LogInformation(
                            "PatchBuilder: Patched {Count} texture(s) in {Role} M2",
                            texPatched, effectFile.EffectRole);
                    }
                }

                // ── Apply blendMode override (Tier C — Session 14) ──
                if (request.PerPhaseBlendMode != null &&
                    request.PerPhaseBlendMode.TryGetValue(phase, out var blendMode))
                {
                    int blendPatched = M2TextureParser.PatchBlendMode(finalM2, blendMode);
                    if (blendPatched > 0)
                    {
                        _logger.LogInformation(
                            "PatchBuilder: Set blendMode={Mode} on {Count} emitter(s) in {Role}",
                            blendMode, blendPatched, effectFile.EffectRole);
                    }
                }

                // ── Apply emitterType override (Tier C — Session 14) ──
                if (request.PerPhaseEmitterType != null &&
                    request.PerPhaseEmitterType.TryGetValue(phase, out var emitterType))
                {
                    int typePatched = M2TextureParser.PatchEmitterType(finalM2, emitterType);
                    if (typePatched > 0)
                    {
                        _logger.LogInformation(
                            "PatchBuilder: Set emitterType={Type} on {Count} emitter(s) in {Role}",
                            emitterType, typePatched, effectFile.EffectRole);
                    }
                }

                mpqBuilder.AddFile(effectFile.CustomM2Path, finalM2);
                _logger.LogInformation("PatchBuilder: Added M2 {Role} → {Path} ({Size} bytes)",
                    effectFile.EffectRole, effectFile.CustomM2Path, finalM2.Length);
            }

            // ── Step 7: Serialize all modified DBCs ──
            byte[] spellDbcBytes = spellDbc.Write();
            byte[] visualDbcBytes = visualDbc.Write();
            byte[] kitDbcBytes = kitDbc.Write();
            byte[] efnDbcBytes = effectNameDbc.Write();
            byte[] slaDbcBytes = skillLineAbilityDbc.Write();
            byte[] iconDbcBytes = spellIconDbc.Write();

            mpqBuilder.AddFile("DBFilesClient\\Spell.dbc", spellDbcBytes);
            mpqBuilder.AddFile("DBFilesClient\\SpellVisual.dbc", visualDbcBytes);
            mpqBuilder.AddFile("DBFilesClient\\SpellVisualKit.dbc", kitDbcBytes);
            mpqBuilder.AddFile("DBFilesClient\\SpellVisualEffectName.dbc", efnDbcBytes);
            mpqBuilder.AddFile("DBFilesClient\\SkillLineAbility.dbc", slaDbcBytes);
            mpqBuilder.AddFile("DBFilesClient\\SpellIcon.dbc", iconDbcBytes);

            // Add custom BLP icon if we generated one
            if (customBlpBytes != null && customBlpMpqPath != null)
            {
                mpqBuilder.AddFile(customBlpMpqPath, customBlpBytes);
                _logger.LogInformation("PatchBuilder: Packaged custom BLP {Path} into MPQ", customBlpMpqPath);
            }

            // ── Step 8: Write ALL patched DBCs to server dir ──
            // SpellIcon.dbc is also written here so the server has a consistent view
            // of what icons exist (not strictly required by mangosd, but keeps the
            // server DBC dir in sync with what's in the patch MPQ for diagnostics).
            try
            {
                File.WriteAllBytes(Path.Combine(DbcPath, "Spell.dbc"), spellDbcBytes);
                File.WriteAllBytes(Path.Combine(DbcPath, "SpellVisual.dbc"), visualDbcBytes);
                File.WriteAllBytes(Path.Combine(DbcPath, "SpellVisualKit.dbc"), kitDbcBytes);
                File.WriteAllBytes(Path.Combine(DbcPath, "SpellVisualEffectName.dbc"), efnDbcBytes);
                File.WriteAllBytes(Path.Combine(DbcPath, "SkillLineAbility.dbc"), slaDbcBytes);
                File.WriteAllBytes(Path.Combine(DbcPath, "SpellIcon.dbc"), iconDbcBytes);
                _logger.LogInformation("PatchBuilder: Updated ALL server DBCs at {Path} (incl. SpellIcon)", DbcPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PatchBuilder: Could not write server-side DBCs");
            }

            // ── Step 9: Build the MPQ ──
            Directory.CreateDirectory(PatchOutputPath);
            string patchFileName = $"patch-custom-{request.SpellEntry}.MPQ";
            string fullPath = Path.Combine(PatchOutputPath, patchFileName);

            if (!mpqBuilder.Build(fullPath))
            {
                _logger.LogError("PatchBuilder: MPQ build failed");
                return null;
            }

            var result = new PatchResult
            {
                Success = true,
                PatchFilePath = fullPath,
                PatchFileName = patchFileName,
                SpellEntry = (int)request.SpellEntry,
                NewVisualId = cloneResult.NewVisualId,
                NewIconId = finalIconId,
                CustomBlpPackaged = customBlpBytes != null,
                DbcFilesPatched = 6,
                M2FilesPatched = patchedM2Count,
                TotalFiles = mpqBuilder.FileCount,
                FilePaths = mpqBuilder.GetQueuedPaths().ToList()
            };

            _logger.LogInformation(
                "PatchBuilder: SUCCESS — {File} ({Total} files, {M2} M2s, customBlp={Blp})",
                patchFileName, result.TotalFiles, patchedM2Count, result.CustomBlpPackaged);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PatchBuilder: Failed for spell {E}", request.SpellEntry);
            return null;
        }
    }

    /// <summary>
    /// Add a row to SpellIcon.dbc pointing to our custom icon's texture path.
    /// SpellIcon.dbc has 2 fields per record: [0]=ID, [1]=stringref → texture path.
    /// The texture path uses "Interface\Icons\CustomSpell_<name>" — note no .blp
    /// extension; the client appends it automatically when loading the texture.
    /// Returns the new SpellIcon.dbc ID for use in Spell.dbc field [117].
    /// </summary>
    private uint PatchSpellIconDbc(DbcWriterService spellIconDbc, string sanitizedSpellName)
    {
        string textureBase = $@"Interface\Icons\CustomSpell_{sanitizedSpellName}";
        uint newIconId = Math.Max(spellIconDbc.GetMaxId() + 1, CUSTOM_SPELL_FLOOR);

        int fieldCount = spellIconDbc.RecordSize / 4;
        var row = new uint[fieldCount];
        row[0] = newIconId;
        row[1] = spellIconDbc.AddString(textureBase);
        spellIconDbc.AddRow(row);

        _logger.LogInformation("PatchBuilder: Added SpellIcon.dbc row #{Id} → '{Path}'",
            newIconId, textureBase);

        return newIconId;
    }

    /// <summary>
    /// Read a DBC from the server directory and scrub any previously-added custom entries.
    /// 
    /// The server DBC dir has the correct build 5875 DBCs (173-field Spell.dbc etc.)
    /// but may be contaminated with custom entries from previous patch generations.
    /// We read the file, remove any rows with ID >= the per-DBC floor, and return
    /// a clean DBC ready for fresh custom content.
    /// 
    /// Per-DBC floors (Session 9):
    ///   SpellVisual/Kit/EffectName → 10000 (client ignores 60000+)
    ///   Spell/SpellIcon/SkillLineAbility → 60000 (work fine at 60000+)
    /// </summary>
    private DbcWriterService ReadCleanDbc(string dbcMpqPath)
    {
        string fileName = Path.GetFileName(dbcMpqPath.Replace('\\', '/'));
        string serverPath = Path.Combine(DbcPath, fileName);
        var dbc = DbcWriterService.ReadDbc(serverPath);

        // Look up the scrub floor for this DBC (default to CUSTOM_SPELL_FLOOR if not in map)
        uint floor = DbcScrubFloors.GetValueOrDefault(fileName, CUSTOM_SPELL_FLOOR);

        // Scrub any custom entries from previous generations
        int removed = dbc.RemoveRowsWhere(id => id >= floor);
        if (removed > 0)
            _logger.LogInformation("PatchBuilder: Scrubbed {Count} custom entries (>={Floor}) from {File}",
                removed, floor, fileName);

        return dbc;
    }

    /// <summary>
    /// Read SkillLineAbility.dbc with custom scrub logic.
    /// SLA rows for custom spells have LOW IDs (15031+) but reference custom spellIds (40000+)
    /// in field [2]. Standard ID-based scrub won't catch them. This method scrubs by spellId.
    /// </summary>
    private DbcWriterService ReadCleanSlaDbc()
    {
        string serverPath = Path.Combine(DbcPath, "SkillLineAbility.dbc");
        var dbc = DbcWriterService.ReadDbc(serverPath);

        // Collect IDs of rows where spellId (field [2]) is in our custom range
        var idsToRemove = new List<uint>();
        foreach (var row in dbc.GetAllRows())
        {
            if (row.Length > 2 && row[2] >= CUSTOM_SPELL_FLOOR)
                idsToRemove.Add(row[0]);
        }

        if (idsToRemove.Count > 0)
        {
            var idSet = new HashSet<uint>(idsToRemove);
            int removed = dbc.RemoveRowsWhere(id => idSet.Contains(id));
            _logger.LogInformation("PatchBuilder: Scrubbed {Count} custom SLA entries (spellId>={Floor})",
                removed, CUSTOM_SPELL_FLOOR);
        }

        return dbc;
    }

    /// <summary>
    /// Read an M2 file from client data. Attempts MPQ reading first, then falls back
    /// to pre-extracted static files on disk.
    /// 
    /// KNOWN ISSUE: War3Net throws ObjectDisposedException ("Cannot access a closed
    /// Stream") when reading from large MPQs (~2GB patch.MPQ). The MpqStream is
    /// prematurely disposed during CopyTo. This causes the read to silently fall
    /// through to model.MPQ, which has older M2 versions with stale texture paths
    /// (e.g. WORLD\SKILLACTIVATED\CONTAINERS\FLARE.BLP instead of correct
    /// ITEM\OBJECTCOMPONENTS\WEAPON\FLARE.BLP) → green "missing texture" blocks.
    /// 
    /// WORKAROUND: Pre-extract spell M2 files from patch.MPQ (via Ladik's MPQ Editor)
    /// into ClientM2Path, preserving the MPQ directory structure:
    ///   {ClientM2Path}/Spells/Fire_Cast_Hand.m2
    ///   {ClientM2Path}/Spells/Fire_Precast_Hand.m2
    ///   {ClientM2Path}/Spells/Fireball_Missile_Low.m2
    ///   {ClientM2Path}/Spells/MoltenBlast_Impact_Chest.m2
    ///   etc.
    /// </summary>
    private byte[]? ReadFileFromClientMpq(string mpqPath)
    {
        string normalizedPath = mpqPath.Replace("/", "\\");

        // ── Attempt 1: Pre-extracted static file (PREFERRED SOURCE) ──
        // These are extracted from patch.MPQ via Ladik's and are the correct
        // versions with valid texture paths. Always try this first.
        if (Directory.Exists(ClientM2Path))
        {
            string fsPath = Path.Combine(ClientM2Path, normalizedPath.Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(fsPath))
            {
                var data = File.ReadAllBytes(fsPath);
                _logger.LogInformation("ReadM2: '{Path}' from static files ({Size} bytes)",
                    normalizedPath, data.Length);
                return data;
            }

            // Case-insensitive fallback — Linux is case-sensitive but DBC paths
            // use mixed case (Spells\ vs spells\ vs SPELLS\). Walk the directory
            // tree doing case-insensitive matching so one extraction covers all.
            string? resolved = ResolveCaseInsensitive(ClientM2Path, normalizedPath.Replace('\\', Path.DirectorySeparatorChar));
            if (resolved != null && File.Exists(resolved))
            {
                var data = File.ReadAllBytes(resolved);
                _logger.LogInformation("ReadM2: '{Path}' from static files (case-insensitive → {Resolved}, {Size} bytes)",
                    normalizedPath, Path.GetRelativePath(ClientM2Path, resolved), data.Length);
                return data;
            }
        }

        // ── Attempt 2: Read from MPQ (ONLY patch.MPQ and higher) ──
        // NEVER read from model.MPQ — it has older M2 versions with stale
        // texture paths (e.g. WORLD\SKILLACTIVATED\CONTAINERS\FLARE.BLP)
        // that cause green "missing texture" blocks in-game.
        // Only try patch-*.MPQ files (patch.MPQ, patch-2.MPQ, etc.)
        if (Directory.Exists(ClientDataPath))
        {
            try
            {
                var mpqFiles = Directory.GetFiles(ClientDataPath, "*.MPQ")
                    .Concat(Directory.GetFiles(ClientDataPath, "*.mpq"))
                    .Where(f =>
                    {
                        string name = Path.GetFileName(f).ToLowerInvariant();
                        // Only patch MPQs — never model.MPQ, texture.MPQ, etc.
                        return name.StartsWith("patch");
                    })
                    .OrderByDescending(f => Path.GetFileName(f))
                    .ToList();

                foreach (var mpqFile in mpqFiles)
                {
                    string mpqName = Path.GetFileName(mpqFile);
                    FileStream? fileStream = null;
                    War3Net.IO.Mpq.MpqArchive? archive = null;
                    try
                    {
                        fileStream = File.OpenRead(mpqFile);
                        archive = new War3Net.IO.Mpq.MpqArchive(fileStream);

                        if (archive.FileExists(normalizedPath))
                        {
                            // Read ALL bytes before disposing anything
                            var entryStream = archive.OpenFile(normalizedPath);
                            var ms = new MemoryStream();
                            entryStream.CopyTo(ms);
                            var data = ms.ToArray();
                            ms.Dispose();
                            entryStream.Dispose();
                            archive.Dispose();
                            fileStream.Dispose();

                            _logger.LogInformation("ReadM2: Found '{Path}' in {Mpq} ({Size} bytes)",
                                normalizedPath, mpqName, data.Length);
                            return data;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ReadM2: FAILED to read from {Mpq} ({Size} bytes) — trying next",
                            mpqName, new FileInfo(mpqFile).Length);
                    }
                    finally
                    {
                        archive?.Dispose();
                        fileStream?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReadM2: MPQ enumeration failed for {Path}", normalizedPath);
            }
        }

        // ── Both failed ──
        _logger.LogError("ReadM2: '{Path}' NOT FOUND in static files at '{M2Dir}' or any patch MPQ. " +
            "Extract this file from patch.MPQ using Ladik's MPQ Editor into the fallback directory, " +
            "preserving the MPQ path structure (e.g. <M2Dir>/Spells/filename.m2).",
            normalizedPath, ClientM2Path);
        return null;
    }

    private static string SanitizeName(string name)
    {
        return new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

    /// <summary>
    /// Resolve a relative path under a base directory using case-insensitive matching.
    /// 
    /// MPQ file paths are case-insensitive (Windows heritage) but Linux filesystems
    /// are case-sensitive. SpellVisualEffectName.dbc uses mixed case: "Spells\",
    /// "spells\", "SPELLS\", "Particles\", etc. Rather than maintaining multiple
    /// directories or symlinks, we walk the directory tree and match each path
    /// segment case-insensitively.
    /// 
    /// Returns the resolved absolute path, or null if not found.
    /// </summary>
    private static string? ResolveCaseInsensitive(string basePath, string relativePath)
    {
        string[] segments = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string current = basePath;

        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            bool isLast = (i == segments.Length - 1);

            // Try exact match first (fast path)
            string exact = Path.Combine(current, segment);
            if (isLast ? File.Exists(exact) : Directory.Exists(exact))
            {
                current = exact;
                continue;
            }

            // Case-insensitive search
            string? match = null;
            try
            {
                if (isLast)
                {
                    // Looking for a file
                    match = Directory.GetFiles(current)
                        .FirstOrDefault(f => string.Equals(
                            Path.GetFileName(f), segment, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // Looking for a directory
                    match = Directory.GetDirectories(current)
                        .FirstOrDefault(d => string.Equals(
                            Path.GetFileName(d), segment, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { /* directory not accessible */ }

            if (match == null)
                return null;

            current = match;
        }

        return current;
    }
}

// ── DTOs ──

public class SpellPatchRequest
{
    public uint SpellEntry { get; set; }
    public uint SourceSpellEntry { get; set; }

    /// <summary>
    /// Fallback SpellIcon.dbc ID to use when no custom icon is provided OR
    /// when BLP conversion fails. The controller fills this with a school-
    /// appropriate vanilla icon ID via GetSchoolIconId().
    /// </summary>
    public uint? SpellIconId { get; set; }

    /// <summary>
    /// On-disk path to a PNG to convert into a custom BLP icon. When set and
    /// the file exists, the patch pipeline:
    ///   1. Converts PNG → BLP2 DXT3 64×64 via BlpWriterService
    ///   2. Adds a new SpellIcon.dbc row pointing at "Interface\Icons\CustomSpell_<name>"
    ///   3. Packages the BLP into the MPQ at that path
    ///   4. Uses the new SpellIcon.dbc ID in Spell.dbc field [117] instead of SpellIconId
    /// If null/empty/missing, the pipeline falls back to SpellIconId.
    /// </summary>
    public string? IconPngPath { get; set; }

    public uint SourceVisualId { get; set; }
    public string SpellName { get; set; } = "";
    public string? NameSubtext { get; set; }
    public string? Description { get; set; }
    public string? Tooltip { get; set; }
    public uint? SchoolMask { get; set; }

    /// <summary>
    /// SkillLine ID for SkillLineAbility.dbc (e.g. 8 = Fire for Mage).
    /// When set, the DBC entry uses this instead of 0 (General tab).
    /// </summary>
    public int SkillId { get; set; }

    /// <summary>
    /// Class mask for SkillLineAbility.dbc (e.g. 128 = Mage).
    /// When set, the DBC entry restricts to this class.
    /// </summary>
    public int ClassMask { get; set; }

    // ── Session 45: Gameplay field overrides for R1 DBC patching ──
    // These mirror the RankPatchData fields. When non-null, the patch builder
    // writes them into the R1 Spell.dbc row so the DBC matches spell_template.
    // Without these, R1 clones the source DBC row and keeps the source's
    // mana cost, damage, spell level, etc. — ignoring user overrides.
    public int? ManaCost { get; set; }
    public int? EffectBasePoints0 { get; set; }
    public int? EffectDieSides0 { get; set; }
    public int? EffectBasePoints1 { get; set; }
    public int? SpellLevel { get; set; }
    public int? BaseLevel { get; set; }
    public int? MaxLevel { get; set; }
    public int? CastingTimeIndex { get; set; }
    public int? RangeIndex { get; set; }
    public float? EffectRealPointsPerLevel0 { get; set; }
    public float? EffectBonusCoefficient0 { get; set; }
    public float? MissileSpeed { get; set; }
    public int? Cooldown { get; set; }

    public M2ParticlePatcher.ParticlePatchParams? ParticleParams { get; set; }

    /// <summary>
    /// Per-effect-role particle params from the Visual Designer.
    /// Key is the phase name ("precast", "cast", "missile", "impact").
    /// Value is the ParticlePatchParams for M2s in that phase.
    /// When null, all M2s use the global ParticleParams.
    /// 
    /// The effect role (e.g. "precast_leftHand", "impact_chest") is matched
    /// by splitting on '_' and checking the first segment against these keys.
    /// </summary>
    public Dictionary<string, M2ParticlePatcher.ParticlePatchParams>? PerRoleParams { get; set; }

    /// <summary>
    /// Per-phase texture replacement data. Key is the phase name ("precast", "cast", "missile", "impact").
    /// Value is a list of texture replacements for M2s in that phase.
    /// Each TextureReplacement has the BLP bytes, the MPQ path, and the M2 texture index.
    /// When null, no texture replacement is performed (vanilla textures kept).
    /// </summary>
    public Dictionary<string, List<TextureReplacement>>? PerPhaseTextures { get; set; }

    /// <summary>
    /// Per-phase blend mode override. Key is phase name, value is blend mode byte.
    /// 0=opaque, 1=mod, 2=alpha, 4=additive. Null entry or missing key = don't change.
    /// Additive (4) makes particles glow — most fire/frost spells use this.
    /// Switching to alpha (2) gives a solid/smoky look.
    /// </summary>
    public Dictionary<string, byte>? PerPhaseBlendMode { get; set; }

    /// <summary>
    /// Per-phase emitter type override. Key is phase name, value is emitter type byte.
    /// 0=point, 1=sphere, 2=plane, 3=spline. Null entry or missing key = don't change.
    /// </summary>
    public Dictionary<string, byte>? PerPhaseEmitterType { get; set; }

    /// <summary>
    /// Session 30: Per-phase pre-patched M2 byte arrays from experiment lab or tuning system.
    /// Key is phase name ("precast", "cast", "missile", "impact").
    /// Value is the complete patched M2 bytes (emitter changes already applied).
    /// When present, REPLACES the M2 after all standard Tier A/B/C patching.
    /// Written to disk by ApplySpellTuning and RunExperiment as m2_patched_{phase}_{filename}.
    /// </summary>
    public Dictionary<string, byte[]>? PerPhasePatchedM2s { get; set; }

    /// <summary>
    /// Session 33: Additional rank entries that need Spell.dbc + SkillLineAbility.dbc patching.
    /// These are ranks 2+ from GenerateRankChainAsync — they exist in spell_template (server)
    /// but not in the client DBCs. Each rank clones its Spell.dbc row from the corresponding
    /// source rank (preserving per-rank cast time, range, effect structure) and patches in
    /// the custom spell's name, subtext ("Rank N"), visual ID, icon ID, and school.
    /// 
    /// Null/empty when the spell has no rank chain (single rank or ranks not generated).
    /// Rank 1 is handled by the main SpellPatchRequest — only ranks 2+ go here.
    /// </summary>
    public List<RankPatchData>? AdditionalRanks { get; set; }

    /// <summary>
    /// Session 34: Trainer wrapper spells (50000+ range) that need Spell.dbc entries.
    /// VMaNGOS trainers only process SPELL_EFFECT_LEARN_SPELL (36) wrapper spells.
    /// These exist in spell_template (server) but the client also needs DBC entries
    /// to render them in the trainer UI. Each wrapper is cloned from vanilla wrapper
    /// 1173 and patched with the custom spell name, subtext, and trigger spell ID.
    /// </summary>
    public List<TrainerWrapperData>? TrainerWrappers { get; set; }
}

/// <summary>
/// Session 33: Data needed to patch a single rank (2+) into Spell.dbc and SkillLineAbility.dbc.
/// The rank already exists in spell_template (server-side). This data drives client-side patching
/// so the client knows about the rank (tooltips, spellbook tab, rank subtext).
/// </summary>
public class RankPatchData
{
    /// <summary>Custom spell entry for this rank (40000–65000 range).</summary>
    public uint Entry { get; set; }

    /// <summary>The vanilla source spell entry this rank was cloned from (e.g., Fireball R5 = 8400).</summary>
    public uint SourceRankEntry { get; set; }

    /// <summary>Rank number (2, 3, 4, ...).</summary>
    public int Rank { get; set; }

    /// <summary>Spell name (same for all ranks).</summary>
    public string SpellName { get; set; } = "";

    /// <summary>Description (usually inherited from source — contains $s1/$o2/$d template vars).</summary>
    public string? Description { get; set; }

    /// <summary>School override (same as rank 1).</summary>
    public uint? SchoolMask { get; set; }

    /// <summary>
    /// SkillLineAbility data for spellbook tab placement.
    /// [0]=skillId, [1]=classMask, [2]=supersededBySpell (next rank entry, 0 if last rank).
    /// Null = don't add SLA row (General tab).
    /// </summary>
    public (int skillId, int classMask, int supersededBySpell)? SkillLineAbilityData { get; set; }

    // ── Session 33: Effect/gameplay fields for Spell.dbc tooltip accuracy ──
    // These values come from spell_template (the custom-scaled values from GenerateRankChainAsync)
    // and are written into the DBC so tooltips show correct damage/mana/level values.
    // Null = don't override (keep value from source rank clone).

    /// <summary>effectBasePoints1 from spell_template. DBC field [77] (signed int).</summary>
    public int? EffectBasePoints0 { get; set; }
    /// <summary>effectDieSides1 from spell_template. DBC field [65].</summary>
    public int? EffectDieSides0 { get; set; }
    /// <summary>effectBasePoints2 from spell_template. DBC field [78] (signed int).</summary>
    public int? EffectBasePoints1 { get; set; }
    /// <summary>manaCost from spell_template. DBC field [33].</summary>
    public int? ManaCost { get; set; }
    /// <summary>spellLevel from spell_template. DBC field [30].</summary>
    public int? SpellLevel { get; set; }
    /// <summary>baseLevel from spell_template. DBC field [29].</summary>
    public int? BaseLevel { get; set; }
    /// <summary>maxLevel from spell_template. DBC field [28].</summary>
    public int? MaxLevel { get; set; }
    /// <summary>castingTimeIndex from spell_template. DBC field [19].</summary>
    public int? CastingTimeIndex { get; set; }
    /// <summary>effectRealPointsPerLevel1 from spell_template. DBC field [73] (float).</summary>
    public float? EffectRealPointsPerLevel0 { get; set; }
}

public class PatchResult
{
    public bool Success { get; set; }
    public string PatchFilePath { get; set; } = "";
    public string PatchFileName { get; set; } = "";
    public int SpellEntry { get; set; }
    public uint NewVisualId { get; set; }

    /// <summary>
    /// The SpellIcon.dbc ID written to Spell.dbc field [117].
    /// Either a freshly-allocated custom ID (if BLP was packaged) or the
    /// fallback vanilla ID supplied by the controller.
    /// </summary>
    public uint NewIconId { get; set; }

    /// <summary>True when a custom BLP was successfully packaged into the MPQ.</summary>
    public bool CustomBlpPackaged { get; set; }

    public int DbcFilesPatched { get; set; }
    public int M2FilesPatched { get; set; }
    public int TotalFiles { get; set; }
    public List<string> FilePaths { get; set; } = new();
}

/// <summary>Result of rebuilding the unified patch-3.MPQ with all custom spells.</summary>
public class UnifiedPatchResult
{
    public bool Success { get; set; }
    public string PatchFilePath { get; set; } = "";
    public string PatchFileName { get; set; } = "";
    public int M2FilesPatched { get; set; }
    public int TotalFiles { get; set; }
    public List<int> SpellsIncluded { get; set; } = new();
    public Dictionary<int, uint> VisualIdMap { get; set; } = new();  // entry → newVisualId
    public Dictionary<int, uint> IconIdMap { get; set; } = new();    // entry → newIconId
    public List<string> Errors { get; set; } = new();
}

/// <summary>A single texture replacement for an M2 file in a specific spell phase.</summary>
public class TextureReplacement
{
    /// <summary>Index in the M2's texture table (0-based).</summary>
    public int TextureIndex { get; set; }

    /// <summary>The replacement MPQ path (e.g. "SPELLS\CS_Lightning_0.BLP").
    /// This gets written into the M2's texture filename slot (null-padded to fit).
    /// Must be shorter than the original filename to avoid data corruption.</summary>
    public string ReplacementMpqPath { get; set; } = "";

    /// <summary>BLP2 DXT3 bytes for the replacement texture. Added to MPQ at ReplacementMpqPath.</summary>
    public byte[] BlpBytes { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Session 34: Data needed to add a trainer wrapper spell to Spell.dbc.
/// The wrapper exists in spell_template (server-side) with effect1=36 (LEARN_SPELL)
/// and effectTriggerSpell1 pointing to the real custom spell. The client needs a
/// Spell.dbc entry to render it in the trainer UI.
/// </summary>
public class TrainerWrapperData
{
    /// <summary>Wrapper spell entry (50000+ range).</summary>
    public uint WrapperEntry { get; set; }

    /// <summary>The real custom spell this wrapper teaches.</summary>
    public uint TeachesSpellEntry { get; set; }

    /// <summary>Spell name for trainer UI display.</summary>
    public string SpellName { get; set; } = "";

    /// <summary>Rank subtext for trainer UI display (e.g., "Rank 2").</summary>
    public string RankSubtext { get; set; } = "";

    /// <summary>SpellIcon.dbc ID for trainer UI icon. Should be R1's custom icon.</summary>
    public uint IconId { get; set; }
}