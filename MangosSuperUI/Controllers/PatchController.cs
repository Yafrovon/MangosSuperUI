using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;

namespace MangosSuperUI.Controllers;

public partial class PatchController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly PatchBuilderService _patchBuilder;
    private readonly SpellCreatorService _spellCreator;
    private readonly SpellIconService _iconService;
    private readonly SpellTextureService _textureService;
    private readonly SpellRecipeService _recipeService;
    private readonly SpellConfigService _spellConfig;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PatchController> _logger;
    private readonly VanillaBlpService _vanillaBlps;
    private readonly SpellDnaService _dna;
    private readonly IConfiguration _config;

    private string PatchOutputPath => Path.Combine(_env.WebRootPath, "patches");


    public PatchController(
        ConnectionFactory db,
        PatchBuilderService patchBuilder,
        SpellCreatorService spellCreator,
        SpellIconService iconService,
        SpellTextureService textureService,
        SpellRecipeService recipeService,
        SpellConfigService spellConfig,
        DbcService dbc,
        AuditService audit,
        IWebHostEnvironment env,
        ILogger<PatchController> logger,
        VanillaBlpService vanillaBlps,
        SpellDnaService dna,
        IConfiguration config)
    {
        _db = db;
        _patchBuilder = patchBuilder;
        _spellCreator = spellCreator;
        _iconService = iconService;
        _textureService = textureService;
        _recipeService = recipeService;
        _spellConfig = spellConfig;
        _dbc = dbc;
        _audit = audit;
        _env = env;
        _logger = logger;
        _vanillaBlps = vanillaBlps;
        _dna = dna;
        _config = config;
    }

    public IActionResult Index() => View();

    // ===================== SOURCE SPELL LOOKUP =====================

    [HttpGet]
    public async Task<IActionResult> SearchSource(string? q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Json(new { results = Array.Empty<object>() });

        var query = q.Trim();
        const int limit = 50;

        // ── Step 1: Search spell_template (server-side overrides) ──
        using var conn = _db.Mangos();
        string where;
        var parameters = new DynamicParameters();

        if (uint.TryParse(query, out var spellId))
        {
            where = "WHERE entry = @Id AND build = (SELECT MAX(build) FROM spell_template st2 WHERE st2.entry = spell_template.entry)";
            parameters.Add("Id", spellId);
        }
        else
        {
            where = "WHERE name LIKE @Search AND build = (SELECT MAX(build) FROM spell_template st2 WHERE st2.entry = spell_template.entry)";
            parameters.Add("Search", $"%{query}%");
        }

        var sqlResults = (await conn.QueryAsync<dynamic>(
            $@"SELECT entry, name, nameSubtext, school, spellVisual1, spellIconId, speed,
                      manaCost, castingTimeIndex, rangeIndex, spellLevel, maxLevel,
                      effectBasePoints1, effectDieSides1, effectRealPointsPerLevel1,
                      effectBonusCoefficient1, recoveryTime, durationIndex,
                      spellFamilyName, description
               FROM spell_template {where}
               ORDER BY entry LIMIT {limit}", parameters)).ToList();

        // ── Step 2: Search Spell.dbc for spells NOT in spell_template ──
        var sqlEntryIds = new HashSet<uint>(sqlResults.Select(r => (uint)r.entry));
        var dbcMatches = new List<object>();

        if (_dbc.SpellEntries.Count > 0)
        {
            IEnumerable<SpellDbcEntry> dbcSearch;

            if (uint.TryParse(query, out var dbcId))
            {
                dbcSearch = _dbc.SpellEntries.TryGetValue(dbcId, out var entry)
                    ? new[] { entry }
                    : Enumerable.Empty<SpellDbcEntry>();
            }
            else
            {
                dbcSearch = _dbc.SpellEntries.Values
                    .Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var e in dbcSearch)
            {
                if (sqlEntryIds.Contains(e.Entry)) continue; // Already in SQL results
                dbcMatches.Add(new
                {
                    entry = e.Entry,
                    name = e.Name,
                    nameSubtext = e.NameSubtext,
                    school = (int)e.School,
                    spellVisual1 = e.SpellVisual1,
                    spellIconId = e.SpellIconId,
                    speed = 0,
                    manaCost = 0,
                    castingTimeIndex = 0,
                    rangeIndex = 0,
                    spellLevel = e.SpellLevel,
                    maxLevel = 0,
                    effectBasePoints1 = 0,
                    effectDieSides1 = 0,
                    effectRealPointsPerLevel1 = 0f,
                    effectBonusCoefficient1 = 0f,
                    recoveryTime = 0,
                    durationIndex = 0,
                    spellFamilyName = 0,
                    description = e.Description ?? "",
                    dbcOnly = true
                });
            }
        }

        // ── Step 2b: Backfill empty descriptions from DBC ──
        // spell_template often has empty description; the real text is in Spell.dbc
        if (_dbc.SpellEntries.Count > 0)
        {
            for (int i = 0; i < sqlResults.Count; i++)
            {
                var r = (dynamic)sqlResults[i];
                string desc = r.description ?? "";
                if (string.IsNullOrWhiteSpace(desc) && _dbc.SpellEntries.TryGetValue((uint)r.entry, out var dbcEntry))
                {
                    if (!string.IsNullOrEmpty(dbcEntry.Description))
                    {
                        // Replace the result with description filled in from DBC
                        sqlResults[i] = new
                        {
                            entry = (uint)r.entry,
                            name = (string)r.name,
                            nameSubtext = (string?)r.nameSubtext,
                            school = (int)r.school,
                            spellVisual1 = (uint)r.spellVisual1,
                            spellIconId = (uint)r.spellIconId,
                            speed = (float)r.speed,
                            manaCost = (uint)r.manaCost,
                            castingTimeIndex = (uint)r.castingTimeIndex,
                            rangeIndex = (uint)r.rangeIndex,
                            spellLevel = (uint)r.spellLevel,
                            maxLevel = (uint)r.maxLevel,
                            effectBasePoints1 = (int)r.effectBasePoints1,
                            effectDieSides1 = (int)r.effectDieSides1,
                            effectRealPointsPerLevel1 = (float)r.effectRealPointsPerLevel1,
                            effectBonusCoefficient1 = (float)r.effectBonusCoefficient1,
                            recoveryTime = (uint)r.recoveryTime,
                            durationIndex = (uint)r.durationIndex,
                            spellFamilyName = (uint)r.spellFamilyName,
                            description = dbcEntry.Description
                        };
                    }
                }
            }
        }

        // ── Step 3: Merge, sort by entry, cap at limit ──
        var merged = sqlResults.Cast<object>()
            .Concat(dbcMatches)
            .OrderBy(r => ((dynamic)r).entry)
            .Take(limit)
            .ToList();

        return Json(new { results = merged });
    }

    // ===================== GENERATE SPELL + PATCH =====================

    /// <summary>Fetch all ranks of a source spell with their scaling data, for rank chain preview.</summary>
    [HttpGet]
    public async Task<IActionResult> SourceRanks(int entry)
    {
        if (entry <= 0) return Json(new { ranks = Array.Empty<object>() });

        using var conn = _db.Mangos();

        // Find first_spell for this entry
        var firstSpell = await conn.ExecuteScalarAsync<int?>(
            "SELECT first_spell FROM spell_chain WHERE spell_id = @E", new { E = entry });
        if (!firstSpell.HasValue) firstSpell = entry; // not in spell_chain, treat as standalone

        // Get all ranks
        var ranks = (await conn.QueryAsync<dynamic>(
            @"SELECT sc.rank, sc.spell_id, st.name, st.nameSubtext, st.spellLevel, st.baseLevel, st.maxLevel,
                     st.manaCost, st.effectBasePoints1, st.effectDieSides1,
                     st.effectBasePoints2, st.effectDieSides2,
                     st.effectRealPointsPerLevel1, st.effectBonusCoefficient1,
                     st.castingTimeIndex, st.rangeIndex, st.speed, st.recoveryTime
              FROM spell_chain sc
              JOIN spell_template st ON st.entry = sc.spell_id
                AND st.build = (SELECT MAX(build) FROM spell_template WHERE entry = sc.spell_id)
              WHERE sc.first_spell = @First
              ORDER BY sc.rank",
            new { First = firstSpell.Value })).ToList();

        if (!ranks.Any())
        {
            // No spell_chain data — return single rank from spell_template
            var single = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT 1 AS rank, entry AS spell_id, name, nameSubtext, spellLevel, baseLevel, maxLevel,
                         manaCost, effectBasePoints1, effectDieSides1,
                         effectBasePoints2, effectDieSides2,
                         effectRealPointsPerLevel1, effectBonusCoefficient1,
                         castingTimeIndex, rangeIndex, speed, recoveryTime
                  FROM spell_template WHERE entry = @E
                  ORDER BY build DESC LIMIT 1", new { E = entry });
            if (single != null) ranks.Add(single);
        }

        // Also check how many trainer entries the source has
        var trainerCount = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM (
                SELECT spell FROM npc_trainer WHERE spell = @E
                UNION ALL
                SELECT spell FROM npc_trainer_template WHERE spell = @E
              ) t", new { E = entry });

        return Json(new { ranks, sourceEntry = entry, firstSpell = firstSpell.Value, trainerCount });
    }

    [HttpPost]
    public async Task<IActionResult> Generate([FromBody] GeneratePatchRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SpellName))
            return Json(new { success = false, error = "Spell name is required." });
        if (req.SourceSpellEntry <= 0)
            return Json(new { success = false, error = "Source spell is required." });

        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        try
        {
            // ── Step 1: Create spell in spell_template ──
            var overrides = new Dictionary<string, object?>
            {
                ["name"] = req.SpellName,
                ["nameSubtext"] = req.NameSubtext ?? "Rank 1",
                ["school"] = req.School
            };

            // Description: if user left it blank or used default, DON'T override —
            // let it inherit the source spell's description which has template variables
            // ($s1, $o2, $d) that the client auto-fills with correct damage values.
            if (!string.IsNullOrWhiteSpace(req.Description))
                overrides["description"] = req.Description;

            // Session 32: Apply spell property overrides
            if (req.DamageMin.HasValue && req.DamageMax.HasValue)
            {
                overrides["effectBasePoints1"] = req.DamageMin.Value - 1; // min = base + 1
                overrides["effectDieSides1"] = req.DamageMax.Value - req.DamageMin.Value; // max = base + dieSides
            }
            if (req.ManaCost.HasValue)
                overrides["manaCost"] = req.ManaCost.Value;
            if (req.SpellLevel.HasValue)
            {
                overrides["spellLevel"] = req.SpellLevel.Value;
                overrides["baseLevel"] = req.SpellLevel.Value;
            }
            if (req.MaxLevel.HasValue)
                overrides["maxLevel"] = req.MaxLevel.Value;
            if (req.CastingTimeIndex.HasValue)
                overrides["castingTimeIndex"] = req.CastingTimeIndex.Value;
            if (req.RangeIndex.HasValue)
                overrides["rangeIndex"] = req.RangeIndex.Value;
            if (req.SpellCoefficient.HasValue)
                overrides["effectBonusCoefficient1"] = req.SpellCoefficient.Value;
            if (req.DamagePerLevel.HasValue)
                overrides["effectRealPointsPerLevel1"] = req.DamagePerLevel.Value;
            if (req.MissileSpeed.HasValue)
                overrides["speed"] = req.MissileSpeed.Value;
            if (req.Cooldown.HasValue)
                overrides["recoveryTime"] = req.Cooldown.Value;

            // Set spellFamilyName based on skill tab if provided
            if (!string.IsNullOrEmpty(req.SkillTabKey))
            {
                var tabMap = SpellCreatorService.GetSkillTabMap();
                if (tabMap.TryGetValue(req.SkillTabKey, out var tabInfo))
                {
                    overrides["spellFamilyName"] = tabInfo.spellFamilyName;
                }
            }

            int newEntry = await _spellCreator.CloneSpellAsync(req.SourceSpellEntry, overrides, ip);
            if (newEntry < 0)
                return Json(new { success = false, error = "Failed to create spell in database." });

            // ── Step 1b: Insert skill_line_ability for spellbook tab placement ──
            if (!string.IsNullOrEmpty(req.SkillTabKey))
            {
                try
                {
                    await _spellCreator.InsertSkillLineAbilityAsync(newEntry, req.SkillTabKey, learnOnGetSkill: 2);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Patch: Failed to insert skill_line_ability for #{Entry}", newEntry);
                }
            }

            // ── Step 1c: Insert spell_chain (rank 1 — self-referencing) ──
            try
            {
                await _spellCreator.InsertSpellChainAsync(
                    spellEntry: newEntry,
                    prevSpell: 0,
                    firstSpell: newEntry,
                    rank: 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Patch: Failed to insert spell_chain for #{Entry}", newEntry);
            }

            // ── Step 1c2: Copy source trainers for R1 if requested (Session 33) ──
            // Session 35: If source R1 is a starting spell with no trainer entry,
            // fall back to "Add to all class trainers" so R1 is still trainable.
            if (req.CopySourceTrainers && req.SourceSpellEntry > 0)
            {
                try
                {
                    int trainersCopied = await _spellCreator.CopyTrainerEntriesFromSourceAsync(
                        req.SourceSpellEntry, newEntry, 0, req.SpellLevel ?? 1);
                    if (trainersCopied > 0)
                    {
                        _logger.LogInformation("Patch: Copied {Count} trainer entries from source #{Src} to R1 #{New}",
                            trainersCopied, req.SourceSpellEntry, newEntry);
                    }
                    else if (!string.IsNullOrEmpty(req.SkillTabKey))
                    {
                        // Source R1 has no trainer entries (starting spell) — register at all class trainers
                        _logger.LogInformation("Patch: Source #{Src} has no trainer entries (starting spell), falling back to class trainers for R1 #{New}",
                            req.SourceSpellEntry, newEntry);

                        var tabMap = SpellCreatorService.GetSkillTabMap();
                        if (tabMap.TryGetValue(req.SkillTabKey, out var tabInfo))
                        {
                            // Convert classMask to classId: classMask = 1 << (classId - 1)
                            int classId = 0;
                            int mask = tabInfo.classMask;
                            while (mask > 1) { mask >>= 1; classId++; }
                            classId++; // 1-based

                            var templateMap = SpellCreatorService.GetClassTrainerTemplateMap();
                            if (templateMap.TryGetValue(classId, out int templateId))
                            {
                                // Look up icon from the custom spell
                                int iconId = 185;
                                try
                                {
                                    using var iconConn = _spellCreator.CreateMangosConnection();
                                    var icon = await Dapper.SqlMapper.ExecuteScalarAsync<int?>(iconConn,
                                        "SELECT spellIconId FROM spell_template WHERE entry = @E AND build = 5875",
                                        new { E = newEntry });
                                    iconId = icon ?? 185;
                                }
                                catch { /* fallback */ }

                                int wrapperId = await _spellCreator.CreateTrainerWrapperAsync(
                                    newEntry, req.SpellName ?? "Custom Spell", req.NameSubtext ?? "Rank 1",
                                    req.SpellLevel ?? 1, iconId);

                                // Use a reasonable cost for R1 (vanilla Fireball R2 costs 100 copper = 1 silver)
                                int r1Cost = 100;
                                await _spellCreator.InsertNpcTrainerTemplateAsync(templateId, wrapperId, r1Cost, req.SpellLevel ?? 1);

                                _logger.LogInformation("Patch: Registered R1 #{New} at class trainer template {Tmpl} via wrapper #{Wrap}",
                                    newEntry, templateId, wrapperId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Patch: Failed to copy trainers from source #{Src} to #{New}",
                        req.SourceSpellEntry, newEntry);
                }
            }

            // ── Step 1d: Generate all ranks if requested ──
            List<(int rank, int entry)>? generatedRanks = null;
            if (req.GenerateAllRanks)
            {
                try
                {
                    // Build per-rank overrides from the DTO
                    Dictionary<int, Dictionary<string, object?>>? perRankOvr = null;
                    if (req.RankOverrides != null && req.RankOverrides.Count > 0)
                    {
                        perRankOvr = new Dictionary<int, Dictionary<string, object?>>();
                        foreach (var kvp in req.RankOverrides)
                        {
                            var ro = kvp.Value;
                            var d = new Dictionary<string, object?>();
                            if (ro.DamageMin.HasValue && ro.DamageMax.HasValue)
                            {
                                d["effectBasePoints1"] = ro.DamageMin.Value - 1;
                                d["effectDieSides1"] = ro.DamageMax.Value - ro.DamageMin.Value;
                            }
                            if (ro.ManaCost.HasValue) d["manaCost"] = ro.ManaCost.Value;
                            if (ro.SpellLevel.HasValue) { d["spellLevel"] = ro.SpellLevel.Value; d["baseLevel"] = ro.SpellLevel.Value; }
                            if (ro.MaxLevel.HasValue) d["maxLevel"] = ro.MaxLevel.Value;
                            if (ro.CastingTimeIndex.HasValue) d["castingTimeIndex"] = ro.CastingTimeIndex.Value;
                            if (ro.SpellCoefficient.HasValue) d["effectBonusCoefficient1"] = ro.SpellCoefficient.Value;
                            if (ro.DamagePerLevel.HasValue) d["effectRealPointsPerLevel1"] = ro.DamagePerLevel.Value;
                            if (d.Count > 0) perRankOvr[kvp.Key] = d;
                        }
                    }

                    generatedRanks = await _spellCreator.GenerateRankChainAsync(
                        existingRank1Entry: newEntry,
                        sourceFirstSpell: req.SourceSpellEntry,
                        spellName: req.SpellName,
                        description: req.Description,
                        school: req.School,
                        skillTabKey: req.SkillTabKey,
                        rank1Overrides: overrides,
                        perRankOverrides: perRankOvr,
                        operatorIp: ip,
                        copySourceTrainers: req.CopySourceTrainers);

                    _logger.LogInformation("Patch: Generated {Count} ranks for {Name}", generatedRanks.Count, req.SpellName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Patch: Rank chain generation failed for #{Entry}", newEntry);
                }
            }

            // ── Step 2: Generate icon (async, non-blocking on failure) ──
            // Three icon paths:
            //   a) ExistingIconPath set → reuse a previously-generated custom icon PNG (family templating)
            //   b) GenerateIcon=true → ComfyUI/FLUX AI generation, source="comfyui-flux"
            //   c) Neither → fall back to school-based vanilla SpellIcon.dbc ID
            IconResult? iconResult = null;
            try
            {
                if (!string.IsNullOrEmpty(req.ExistingIconPath) && System.IO.File.Exists(req.ExistingIconPath))
                {
                    // Reuse an existing custom icon (family template)
                    iconResult = new IconResult
                    {
                        Success = true,
                        IconPath = req.ExistingIconPath,
                        IconName = Path.GetFileNameWithoutExtension(req.ExistingIconPath),
                        Source = "comfyui-flux", // Treat as custom so BLP pipeline runs
                        Prompt = "(reused from existing custom icon)"
                    };
                    _logger.LogInformation("Patch: Using existing custom icon template: {Path}", req.ExistingIconPath);
                }
                else if (req.GenerateIcon)
                {
                    iconResult = await _iconService.GenerateIconAsync(req.SpellName, req.School, req.Description);
                    _logger.LogInformation("Patch: Icon generation → {Source}: {Name}",
                        iconResult?.Source ?? "none", iconResult?.IconName ?? "none");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Patch: Icon generation failed (non-fatal)");
            }

            // ── Step 3: Check source visual exists (SQL first, DBC fallback) ──
            uint sourceVisual;
            using (var connCheck = _db.Mangos())
            {
                sourceVisual = await connCheck.ExecuteScalarAsync<uint>(
                    "SELECT spellVisual1 FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                    new { E = req.SourceSpellEntry });
            }

            // DBC fallback — spell_template only has server-side overrides
            if (sourceVisual == 0 && _dbc.SpellEntries.TryGetValue((uint)req.SourceSpellEntry, out var dbcSource))
            {
                sourceVisual = dbcSource.SpellVisual1;
            }

            if (sourceVisual == 0)
            {
                return Json(new
                {
                    success = true,
                    spellEntry = newEntry,
                    hasPatch = false,
                    warning = "Spell created but source has no visual — no MPQ generated.",
                    iconResult = iconResult != null ? new { iconResult.IconName, iconResult.Source, iconResult.Prompt } : null
                });
            }

            // ── Step 4: Save visual config for unified patch rebuilds ──
            string? iconPngPath = null;
            if (iconResult != null && iconResult.Source == "comfyui-flux"
                && !string.IsNullOrEmpty(iconResult.IconPath))
            {
                iconPngPath = iconResult.IconPath;
            }

            // ── Merge global texture theme + blend modes into per-phase knobs ──
            // This ensures the config JSON carries all the Visual Studio settings
            // so RebuildUnifiedPatchFromConfigsAsync can reconstruct them.
            if (req.UsePerPhaseParams && req.PhaseParams != null)
            {
                var phaseKnobsList = new (string key, PhaseKnobs? knobs)[]
                {
                    ("precast", req.PhaseParams.Precast), ("cast", req.PhaseParams.Cast),
                    ("missile", req.PhaseParams.Missile), ("impact", req.PhaseParams.Impact),
                    ("state", req.PhaseParams.State), ("stateDone", req.PhaseParams.StateDone),
                    ("channel", req.PhaseParams.Channel),
                };
                foreach (var (key, knobs) in phaseKnobsList)
                {
                    if (knobs == null) continue;
                    // Merge global blend mode into per-phase if not already set
                    if (knobs.BlendMode == null && req.PhaseBlendModes != null
                        && req.PhaseBlendModes.TryGetValue(key, out int bm))
                        knobs.BlendMode = bm;
                    // Merge global texture theme into per-phase if not already set
                    if (string.IsNullOrEmpty(knobs.TextureTheme) && !string.IsNullOrEmpty(req.TextureTheme))
                        knobs.TextureTheme = req.TextureTheme;
                }
            }

            await _spellConfig.SaveConfigAsync(new SpellVisualConfig
            {
                Entry = newEntry,
                SourceEntry = req.SourceSpellEntry,
                SpellName = req.SpellName,
                NameSubtext = req.NameSubtext,
                Description = req.Description,
                ColorPreset = req.ColorPreset == "custom" && !string.IsNullOrEmpty(req.CustomColor)
                    ? $"custom:{req.CustomColor}"
                    : req.ColorPreset,
                PhaseParams = req.UsePerPhaseParams ? req.PhaseParams : null,
                IconSource = iconResult?.Source,
                IconPath = iconPngPath
            });

            // ── Step 4b: Generate textures if a theme is selected ──
            // Textures are generated once here and cached to disk as BLP files.
            // RebuildUnifiedPatchFromConfigsAsync reads cached BLPs during rebuild.
            if (!string.IsNullOrEmpty(req.TextureTheme))
            {
                try
                {
                    await GenerateAndCacheTexturesForSpellAsync(
                        newEntry, req.SourceSpellEntry, req.SpellName, req.TextureTheme,
                        req.PhaseTextureOverrides);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Patch: Texture generation failed for #{Entry} (non-fatal)", newEntry);
                }
            }

            // ── Step 5: Rebuild unified patch with ALL custom spells ──
            var unifiedResult = await RebuildUnifiedPatchFromConfigsAsync();

            if (unifiedResult == null || !unifiedResult.Success)
            {
                return Json(new
                {
                    success = true,
                    spellEntry = newEntry,
                    hasPatch = false,
                    warning = "Spell created and config saved, but patch rebuild failed. Check logs." +
                        (unifiedResult?.Errors.Any() == true ? " Errors: " + string.Join("; ", unifiedResult.Errors) : ""),
                    iconResult = iconResult != null ? new { iconResult.IconName, iconResult.Source, iconResult.Prompt } : null
                });
            }

            // ── Step 5: Update spell_template with visual + icon IDs from unified patch ──
            using var connUpdate = _db.Mangos();
            if (unifiedResult.VisualIdMap.TryGetValue(newEntry, out uint newVisualId))
            {
                try
                {
                    await connUpdate.ExecuteAsync(
                        @"UPDATE spell_template SET spellVisual1 = @Visual
                          WHERE entry = @Entry
                            AND build = (SELECT MAX(b) FROM (SELECT build AS b FROM spell_template WHERE entry = @Entry) t)",
                        new { Visual = newVisualId, Entry = newEntry });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Patch: Failed to update spellVisual1 for #{Entry}", newEntry);
                }
            }

            if (unifiedResult.IconIdMap.TryGetValue(newEntry, out uint newIconId))
            {
                try
                {
                    // Update R1
                    await connUpdate.ExecuteAsync(
                        @"UPDATE spell_template SET spellIconId = @Icon
                          WHERE entry = @Entry
                            AND build = (SELECT MAX(b) FROM (SELECT build AS b FROM spell_template WHERE entry = @Entry) t)",
                        new { Icon = newIconId, Entry = newEntry });

                    // Update R2+ ranks with the same icon
                    if (generatedRanks != null)
                    {
                        foreach (var (rank, rankEntry) in generatedRanks)
                        {
                            if (rankEntry == newEntry) continue; // Skip R1, already done
                            await connUpdate.ExecuteAsync(
                                @"UPDATE spell_template SET spellIconId = @Icon
                                  WHERE entry = @Entry",
                                new { Icon = newIconId, Entry = rankEntry });
                        }
                    }

                    // Update all trainer wrappers (50000+ range) that teach any of our ranks
                    await connUpdate.ExecuteAsync(
                        @"UPDATE spell_template SET spellIconId = @Icon
                          WHERE entry >= @WBase AND entry <= @WMax
                          AND effect1 = 36
                          AND effectTriggerSpell1 IN (
                              SELECT spell_id FROM spell_chain WHERE first_spell = @R1
                          )",
                        new
                        {
                            Icon = newIconId,
                            WBase = SpellCreatorService.TRAINER_WRAPPER_BASE,
                            WMax = SpellCreatorService.TRAINER_WRAPPER_MAX,
                            R1 = newEntry
                        });

                    _logger.LogInformation("Patch: Updated spellIconId={Icon} on R1 #{R1}, {RankCount} ranks, and matching wrappers",
                        newIconId, newEntry, generatedRanks?.Count ?? 0);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Patch: Failed to update spellIconId for #{Entry}", newEntry);
                }
            }

            // ── Step 6: Auto-teach to character if requested ──
            bool taught = false;
            if (req.TeachToCharacterGuid > 0)
            {
                taught = await _spellCreator.TeachSpellToCharacterAsync(newEntry, req.TeachToCharacterGuid, ip);
            }

            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = ip,
                Category = "content",
                Action = "patch_generate",
                TargetType = "patch",
                TargetName = unifiedResult.PatchFileName,
                Success = true,
                Notes = $"Spell #{newEntry} '{req.SpellName}', unified patch: {unifiedResult.SpellsIncluded.Count} spells, " +
                        $"{unifiedResult.TotalFiles} files, {unifiedResult.M2FilesPatched} M2s, " +
                        $"icon={iconResult?.Source ?? "none"}, taught={taught}"
            });

            return Json(new
            {
                success = true,
                spellEntry = newEntry,
                hasPatch = true,
                patchFileName = unifiedResult.PatchFileName,
                totalSpellsInPatch = unifiedResult.SpellsIncluded.Count,
                m2Count = unifiedResult.M2FilesPatched,
                totalFiles = unifiedResult.TotalFiles,
                taught,
                iconResult = iconResult != null ? new { iconResult.IconName, iconResult.Source, iconResult.Prompt } : null,
                ranksGenerated = generatedRanks?.Select(r => new { r.rank, r.entry }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: Generate failed for {Name}", req.SpellName);
            return Json(new { success = false, error = $"Internal error: {ex.Message}" });
        }
    }

    // ===================== DELETE SPELL =====================

    /// <summary>Returns the skill tab mapping for the Quick Create class/tab dropdowns.</summary>
    [HttpGet]
    public IActionResult SkillTabMap()
    {
        var map = SpellCreatorService.GetSkillTabMap();
        var result = map.Select(kvp => new
        {
            key = kvp.Key,
            skillId = kvp.Value.skillId,
            classMask = kvp.Value.classMask,
            spellFamilyName = kvp.Value.spellFamilyName
        });
        return Json(new { tabs = result });
    }

    /// <summary>Search trainer NPCs by name or entry ID.</summary>
    [HttpGet]
    public async Task<IActionResult> SearchTrainers(string? q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Json(new { results = Array.Empty<object>() });

        using var conn = _db.Mangos();
        var query = q.Trim();
        string where;
        var parameters = new DynamicParameters();

        if (uint.TryParse(query, out var npcId))
        {
            where = "WHERE ct.entry = @Id";
            parameters.Add("Id", npcId);
        }
        else
        {
            where = "WHERE ct.name LIKE @Search";
            parameters.Add("Search", $"%{query}%");
        }

        // trainer_type: 0=class, 1=mount, 2=tradeskill, 3=pet
        // npc_flags bit 16 (0x10) = trainer
        var results = await conn.QueryAsync<dynamic>(
            $@"SELECT ct.entry, ct.name, ct.subname, ct.trainer_type, ct.trainer_class, ct.trainer_id
               FROM creature_template ct
               {where} AND (ct.npc_flags & 16) = 16
               ORDER BY ct.name LIMIT 30", parameters);

        return Json(new { results });
    }

    /// <summary>Register a custom spell at all class trainers via the primary trainer template.</summary>
    [HttpPost]
    public async Task<IActionResult> RegisterAtClassTrainers([FromBody] RegisterClassTrainerRequest req)
    {
        if (req.SpellEntry <= 0 || req.TrainerClass <= 0)
            return Json(new { success = false, error = "Spell entry and trainer class required." });

        var templateMap = SpellCreatorService.GetClassTrainerTemplateMap();
        if (!templateMap.TryGetValue(req.TrainerClass, out int templateId))
            return Json(new { success = false, error = $"No trainer template found for class {req.TrainerClass}." });

        // VMaNGOS trainers require SPELL_EFFECT_LEARN_SPELL wrappers — create one
        // Look up the custom spell's icon so the wrapper inherits it
        int iconId = 185;
        try
        {
            using var conn = _spellCreator.CreateMangosConnection();
            var icon = await Dapper.SqlMapper.ExecuteScalarAsync<int?>(conn,
                "SELECT spellIconId FROM spell_template WHERE entry = @E AND build = 5875",
                new { E = req.SpellEntry });
            iconId = icon ?? 185;
        }
        catch { /* fallback to 185 */ }

        int wrapperId = await _spellCreator.CreateTrainerWrapperAsync(
            req.SpellEntry, req.SpellName ?? "Custom Spell", req.RankSubtext ?? "", req.ReqLevel, iconId);

        var ok = await _spellCreator.InsertNpcTrainerTemplateAsync(
            templateId, wrapperId, req.Cost, req.ReqLevel);

        return Json(new { success = ok, templateId, trainerClass = req.TrainerClass, wrapperId });
    }

    /// <summary>Copy trainer entries from the source spell to a new custom spell.</summary>
    [HttpPost]
    public async Task<IActionResult> CopySourceTrainers([FromBody] CopySourceTrainersRequest req)
    {
        if (req.SpellEntry <= 0 || req.SourceSpellEntry <= 0)
            return Json(new { success = false, error = "Spell entry and source entry required." });

        var count = await _spellCreator.CopyTrainerEntriesFromSourceAsync(
            req.SourceSpellEntry, req.SpellEntry, req.Cost, req.ReqLevel);

        return Json(new { success = count > 0, copiedCount = count });
    }

    /// <summary>Register a custom spell at a specific trainer NPC.</summary>
    [HttpPost]
    public async Task<IActionResult> RegisterAtTrainer([FromBody] RegisterTrainerRequest req)
    {
        if (req.SpellEntry <= 0 || req.TrainerEntry <= 0)
            return Json(new { success = false, error = "Spell entry and trainer entry required." });

        var ok = await _spellCreator.InsertNpcTrainerAsync(
            req.TrainerEntry, req.SpellEntry, req.Cost, req.ReqLevel);

        return Json(new { success = ok });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteSpell([FromBody] DeleteSpellRequest req)
    {
        if (req.Entry <= 0)
            return Json(new { success = false, error = "Spell entry required." });

        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        // ── Session 33: Cascade delete rank chain (ranks 2+) before deleting rank 1 ──
        List<int> chainDeleted = new();
        try
        {
            chainDeleted = await _spellCreator.DeleteRankChainAsync(req.Entry, ip);
            if (chainDeleted.Count > 0)
                _logger.LogInformation("Patch: Cascade deleted {Count} rank chain entries for #{E}: {Entries}",
                    chainDeleted.Count, req.Entry, string.Join(", ", chainDeleted));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patch: Rank chain cascade delete failed for #{E} (continuing with rank 1 delete)", req.Entry);
        }

        var deleted = await _spellCreator.DeleteCustomSpellAsync(req.Entry, ip);

        if (deleted)
        {
            // Delete the visual config
            await _spellConfig.DeleteConfigAsync(req.Entry);

            // Rebuild unified patch without this spell
            var rebuildResult = await RebuildUnifiedPatchFromConfigsAsync();
            _logger.LogInformation("Patch: Deleted spell #{E} + {ChainCount} rank entries, rebuilt unified patch ({Spells} spells remaining)",
                req.Entry, chainDeleted.Count, rebuildResult?.SpellsIncluded.Count ?? 0);
        }

        return Json(new
        {
            success = deleted,
            error = deleted ? null : "Spell not found or not a custom spell.",
            chainDeletedCount = chainDeleted.Count,
            chainDeletedEntries = chainDeleted
        });
    }

    // ===================== TEACH / UNLEARN =====================

    [HttpPost]
    public async Task<IActionResult> TeachSpell([FromBody] TeachSpellRequest req)
    {
        if (req.SpellEntry <= 0 || req.CharacterGuid <= 0)
            return Json(new { success = false, error = "Spell entry and character required." });

        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        var result = await _spellCreator.TeachSpellToCharacterAsync(req.SpellEntry, req.CharacterGuid, ip);
        return Json(new { success = result });
    }

    [HttpPost]
    public async Task<IActionResult> UnlearnSpell([FromBody] TeachSpellRequest req)
    {
        if (req.SpellEntry <= 0 || req.CharacterGuid <= 0)
            return Json(new { success = false, error = "Spell entry and character required." });

        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        var result = await _spellCreator.UnlearnSpellFromCharacterAsync(req.SpellEntry, req.CharacterGuid, ip);
        return Json(new { success = result });
    }

    // ===================== CHARACTER / SPELL QUERIES =====================

    [HttpGet]
    public async Task<IActionResult> Characters()
    {
        var chars = await _spellCreator.ListCharactersAsync();
        return Json(new { characters = chars });
    }

    [HttpGet]
    public async Task<IActionResult> SpellCharacters(int entry)
    {
        var chars = await _spellCreator.GetCharactersWithSpellAsync(entry);
        return Json(new { characters = chars });
    }

    // ===================== SOURCE SPELL PHASES =====================

    /// <summary>
    /// GET /Patch/SourcePhases?entry=133 — Returns which visual phases the source spell
    /// actually uses (non-zero kit slots in SpellVisual.dbc). Used by the Visual Designer
    /// to show/hide irrelevant phase sliders.
    /// </summary>
    [HttpGet]
    public IActionResult SourcePhases(int entry)
    {
        if (entry <= 0)
            return Json(new { phases = Array.Empty<string>() });

        try
        {
            // Get spellVisual1 from spell_template first, DBC fallback
            uint visualId = 0;
            using (var conn = _db.Mangos())
            {
                visualId = conn.ExecuteScalar<uint>(
                    "SELECT spellVisual1 FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                    new { E = entry });
            }

            if (visualId == 0 && _dbc.SpellEntries.TryGetValue((uint)entry, out var dbcSpell))
                visualId = dbcSpell.SpellVisual1;

            if (visualId == 0)
                return Json(new { phases = new[] { "precast", "cast", "missile", "impact" } }); // safe default

            // Read SpellVisual.dbc and find the row
            var dbcPath = Path.Combine(_dbc.DbcPath, "SpellVisual.dbc");

            if (!System.IO.File.Exists(dbcPath))
                return Json(new { phases = new[] { "precast", "cast", "missile", "impact" } });

            var dbc = DbcWriterService.ReadDbc(dbcPath);
            var row = dbc.GetRow(visualId);

            if (row == null)
                return Json(new { phases = new[] { "precast", "cast", "missile", "impact" } });

            // SpellVisual.dbc layout (16 fields, 64 bytes):
            // [0]=ID [1]=PrecastKit [2]=CastKit [3]=ImpactKit [4]=StateKit
            // [5]=StateDoneKit [6]=ChannelKit [7]=HasMissile [8]=MissileModel
            // [9]=MissilePathType [10]=MissileDestAttach [11]=MissileSound
            // [12]=AnimEventSoundID [13]=Flags [14]=CasterImpactKit [15]=TargetImpactKit
            //
            // Kit ID 1 is a dummy/sentinel row in SpellVisualKit.dbc — it exists but
            // has no visual effects (all effect fields are 0). Treat it as "no kit."
            // Channeled spells (Blizzard etc.) typically reuse CastKit for the loop —
            // ChannelKit (field 6) is rarely populated with a separate kit.
            var activePhases = new List<string>();
            if (row[1] > 1) activePhases.Add("precast");
            if (row[2] > 1) activePhases.Add("cast");
            if (row[7] > 0) activePhases.Add("missile");   // HasMissile — EffectName ID, 0 = none
            if (row[3] > 1) activePhases.Add("impact");
            if (row[4] > 1) activePhases.Add("state");
            if (row[5] > 1) activePhases.Add("stateDone");
            if (row[6] > 1) activePhases.Add("channel");   // Rare — most channels reuse CastKit

            return Json(new { phases = activePhases, visualId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patch: SourcePhases lookup failed for #{Entry}", entry);
            return Json(new { phases = new[] { "precast", "cast", "missile", "impact" } });
        }
    }

    // ===================== CUSTOM ICONS =====================

    /// <summary>
    /// GET /Patch/CustomIcons — Lists all previously generated custom icon PNGs.
    /// Used by the icon picker to let users reuse an existing custom icon for
    /// spells in the same visual "family".
    /// </summary>
    [HttpGet]
    public IActionResult CustomIcons()
    {
        var customDir = Path.Combine(_env.WebRootPath, "images", "icons", "custom");
        if (!Directory.Exists(customDir))
            return Json(new { icons = Array.Empty<object>() });

        var icons = Directory.GetFiles(customDir, "*.png")
            .OrderByDescending(f => new FileInfo(f).CreationTimeUtc)
            .Select(f => new
            {
                name = Path.GetFileNameWithoutExtension(f),
                fileName = Path.GetFileName(f),
                path = f,
                webPath = $"/images/icons/custom/{Path.GetFileName(f)}",
                created = new FileInfo(f).CreationTimeUtc
            })
            .ToList();

        return Json(new { icons });
    }

    // ===================== ICON GENERATION =====================

    [HttpPost]
    public async Task<IActionResult> GenerateIcon([FromBody] GenerateIconRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SpellName))
            return Json(new { success = false, error = "Spell name required." });

        try
        {
            var result = await _iconService.GenerateIconAsync(req.SpellName, req.School, req.Description);

            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "content",
                Action = "icon_generate",
                TargetType = "icon",
                TargetName = result.IconName,
                Success = result.Success,
                Notes = $"Source={result.Source}, Prompt={result.Prompt}"
            });

            return Json(new
            {
                success = result.Success,
                iconName = result.IconName,
                iconPath = result.IconPath,
                source = result.Source,
                prompt = result.Prompt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: Icon generation failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    [HttpGet]
    public IActionResult SearchIcons(string? q)
    {
        var icons = _iconService.SearchIcons(q);
        return Json(new { icons });
    }

    // ===================== PATCH LIST / DOWNLOAD / DELETE =====================

    [HttpGet]
    public async Task<IActionResult> CustomSpells()
    {
        var spells = await _spellCreator.ListCustomSpellsAsync();
        var configs = await _spellConfig.GetAllConfigsAsync();
        var configMap = configs.ToDictionary(c => c.Entry, c => c.SourceEntry);

        // Build rank chain map: for each spell, find its first_spell
        using var conn = _db.Mangos();
        var chainRows = await conn.QueryAsync<(int spell_id, int first_spell, int rank)>(
            @"SELECT spell_id, first_spell, rank FROM spell_chain
              WHERE first_spell >= @Base AND first_spell <= @Max",
            new { Base = 40000, Max = 49999 });
        var chainMap = chainRows.ToDictionary(r => r.spell_id, r => (r.first_spell, r.rank));

        var result = new List<object>();
        foreach (var s in spells)
        {
            int entry = (int)s.entry;
            string name = (string)s.name;
            string nameSubtext = s.nameSubtext != null ? (string)s.nameSubtext : "";
            int school = (int)s.school;
            int spellLevel = s.spellLevel != null ? (int)s.spellLevel : 0;
            int manaCost = s.manaCost != null ? (int)s.manaCost : 0;
            int sourceEntry = configMap.TryGetValue(entry, out int src) ? src : 0;
            string safeName = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            string manifestPath = Path.Combine(_env.WebRootPath, "images", "textures", "custom",
                safeName, "manifest.json");

            int firstSpell = entry;
            int rank = 1;
            if (chainMap.TryGetValue(entry, out var chainInfo))
            {
                firstSpell = chainInfo.first_spell;
                rank = chainInfo.rank;
            }

            result.Add(new
            {
                entry,
                name,
                nameSubtext,
                school,
                sourceEntry,
                hasManifest = System.IO.File.Exists(manifestPath),
                firstSpell,
                rank,
                spellLevel,
                manaCost
            });
        }
        return Json(new { spells = result });
    }

    [HttpGet]
    public IActionResult List()
    {
        if (!Directory.Exists(PatchOutputPath))
            return Json(new { patches = Array.Empty<object>() });

        var patches = Directory.GetFiles(PatchOutputPath, "*.MPQ")
            .Concat(Directory.GetFiles(PatchOutputPath, "*.mpq"))
            .Select(f => new
            {
                fileName = Path.GetFileName(f),
                sizeBytes = new FileInfo(f).Length,
                created = new FileInfo(f).CreationTimeUtc
            })
            .OrderByDescending(f => f.created)
            .ToList();

        return Json(new { patches });
    }

    [HttpGet]
    public IActionResult Download(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return BadRequest("File name required");
        file = Path.GetFileName(file);
        var fullPath = Path.Combine(PatchOutputPath, file);
        if (!System.IO.File.Exists(fullPath)) return NotFound($"Patch '{file}' not found");
        return PhysicalFile(fullPath, "application/octet-stream", file);
    }

    [HttpPost]
    public IActionResult Delete([FromBody] DeletePatchRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.FileName))
            return Json(new { success = false, error = "File name required" });

        var file = Path.GetFileName(req.FileName);
        var fullPath = Path.Combine(PatchOutputPath, file);
        if (!System.IO.File.Exists(fullPath))
            return Json(new { success = false, error = "File not found" });

        System.IO.File.Delete(fullPath);

        _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "patch_delete",
            TargetType = "patch",
            TargetName = file,
            Success = true
        }).ConfigureAwait(false);

        return Json(new { success = true });
    }

    // ===================== TEXTURE THEMES & GENERATION (Session 14) =====================

    /// <summary>
    /// GET /Patch/TextureThemes — Returns available texture themes for the streamlined mode.
    /// Each theme has a name, color description, and the set of role-specific prompts.
    /// </summary>
    [HttpGet]
    public IActionResult TextureThemes()
    {
        var themes = SpellTextureService.Themes.Select(kv => new
        {
            key = kv.Key,
            name = kv.Value.Name,
            color = kv.Value.Color
        }).ToList();

        return Json(new { themes });
    }

    /// <summary>
    /// GET /Patch/M2Textures?entry=133 — Parses the source spell's M2 files and returns
    /// texture information for each phase. Used by the advanced texture editor to show
    /// what vanilla textures each emitter uses.
    /// </summary>
    [HttpGet]
    public IActionResult M2Textures(int entry)
    {
        if (entry <= 0)
            return Json(new { phases = Array.Empty<object>() });

        try
        {
            // Get the source spell's visual ID
            uint visualId = 0;
            using (var conn = _db.Mangos())
            {
                visualId = conn.ExecuteScalar<uint>(
                    "SELECT spellVisual1 FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                    new { E = entry });
            }
            if (visualId == 0 && _dbc.SpellEntries.TryGetValue((uint)entry, out var dbcSpell))
                visualId = dbcSpell.SpellVisual1;
            if (visualId == 0)
                return Json(new { error = "No visual ID found for this spell." });

            // Read the SpellVisual.dbc row to get effect IDs per phase
            var dbcPath = Path.Combine(_dbc.DbcPath, "SpellVisual.dbc");
            if (!System.IO.File.Exists(dbcPath))
                return Json(new { error = "SpellVisual.dbc not found." });
            var visualDbc = DbcWriterService.ReadDbc(dbcPath);
            var visualRow = visualDbc.GetRow(visualId);
            if (visualRow == null)
                return Json(new { error = $"SpellVisual #{visualId} not found in DBC." });

            // Read SpellVisualKit and EffectName DBCs
            var kitDbc = DbcWriterService.ReadDbc(Path.Combine(_dbc.DbcPath, "SpellVisualKit.dbc"));
            var efnDbc = DbcWriterService.ReadDbc(Path.Combine(_dbc.DbcPath, "SpellVisualEffectName.dbc"));

            // Map phase → kit field → effect → M2 path → texture list
            var phaseResults = new List<object>();

            var phaseDefs = new (string key, int kitField)[]
            {
                ("precast", 1), ("cast", 2), ("impact", 3),
                ("state", 4), ("stateDone", 5), ("channel", 6)
            };

            foreach (var (phaseKey, kitField) in phaseDefs)
            {
                uint kitId = visualRow[kitField];
                if (kitId <= 1) continue; // 0 = none, 1 = dummy sentinel

                var kitRow = kitDbc.GetRow(kitId);
                if (kitRow == null) continue;

                // Collect M2 paths from this kit's effects
                var phaseTextures = new List<object>();
                int[] effectFields = { 3, 4, 5, 6, 7, 8, 9, 10 };

                foreach (int ef in effectFields)
                {
                    uint effectId = kitRow[ef];
                    if (effectId == 0 || effectId == 0xFFFFFFFF) continue;

                    var effectRow = efnDbc.GetRow(effectId);
                    if (effectRow == null) continue;

                    string m2Path = SpellVisualCloner.NormalizeM2Extension(efnDbc.ReadString(effectRow[2]));
                    if (string.IsNullOrEmpty(m2Path)) continue;

                    // Read the actual M2 file and parse its textures
                    byte[]? m2Data = ReadM2FromClient(m2Path);
                    if (m2Data == null) continue;

                    var textures = M2TextureParser.ParseTextures(m2Data);
                    if (textures.Count == 0) continue;

                    phaseTextures.Add(new
                    {
                        m2Path,
                        effectName = efnDbc.ReadString(effectRow[1]),
                        textures = textures.Select(t => new
                        {
                            index = t.Index,
                            filename = t.Filename,
                            byteLength = t.ActualByteLength,
                            role = SpellTextureService.ClassifyTexture(t.Filename).ToString().ToLower(),
                            emitters = t.ReferencedByEmitters
                        })
                    });
                }

                // Also check missile (field 7 = HasMissile = EffectName ID)
                if (phaseKey == "cast") // missile is separate, handle below
                    continue;

                if (phaseTextures.Count > 0)
                    phaseResults.Add(new { phase = phaseKey, m2Files = phaseTextures });
            }

            // Handle missile separately (field 7)
            uint missileEffectId = visualRow[7];
            if (missileEffectId > 0)
            {
                var missileRow = efnDbc.GetRow(missileEffectId);
                if (missileRow != null)
                {
                    string m2Path = SpellVisualCloner.NormalizeM2Extension(efnDbc.ReadString(missileRow[2]));
                    byte[]? m2Data = !string.IsNullOrEmpty(m2Path) ? ReadM2FromClient(m2Path) : null;
                    if (m2Data != null)
                    {
                        var textures = M2TextureParser.ParseTextures(m2Data);
                        if (textures.Count > 0)
                        {
                            phaseResults.Add(new
                            {
                                phase = "missile",
                                m2Files = new[] { new
                                {
                                    m2Path,
                                    effectName = efnDbc.ReadString(missileRow[1]),
                                    textures = textures.Select(t => new
                                    {
                                        index = t.Index,
                                        filename = t.Filename,
                                        byteLength = t.ActualByteLength,
                                        role = SpellTextureService.ClassifyTexture(t.Filename).ToString().ToLower(),
                                        emitters = t.ReferencedByEmitters
                                    })
                                }}
                            });
                        }
                    }
                }
            }

            return Json(new { phases = phaseResults, visualId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patch: M2Textures lookup failed for #{Entry}", entry);
            return Json(new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Patch/GenerateTextures — Generate AI textures for a spell using ComfyUI/FLUX.
    /// Returns generated texture info (PNG paths, prompts used, roles detected).
    /// The actual BLP conversion and M2 patching happens during Generate (patch rebuild).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> GenerateTextures([FromBody] GenerateTexturesRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SpellName))
            return Json(new { success = false, error = "Spell name required." });
        if (req.TextureSlots == null || req.TextureSlots.Count == 0)
            return Json(new { success = false, error = "No texture slots specified." });

        try
        {
            var genRequest = new TextureGenerationRequest
            {
                SpellName = req.SpellName,
                ThemeKey = req.ThemeKey,
                TextureSlots = req.TextureSlots.Select(s => new TextureSlotRequest
                {
                    Index = s.Index,
                    OriginalFilename = s.OriginalFilename ?? "",
                    OriginalFilenameLength = s.OriginalFilenameLength,
                    OriginalWidth = s.OriginalWidth,
                    OriginalHeight = s.OriginalHeight,
                    RoleOverride = s.RoleOverride != null
                        ? Enum.TryParse<SpellTextureService.TextureRole>(s.RoleOverride, true, out var r) ? r : null
                        : null,
                    CustomPrompt = s.CustomPrompt,
                    UseOllamaRefinement = s.UseOllamaRefinement
                }).ToList()
            };

            var result = await _textureService.GenerateTexturesAsync(genRequest);

            return Json(new
            {
                success = result.Success,
                textures = result.Textures.Select(t => new
                {
                    textureIndex = t.TextureIndex,
                    role = t.Role.ToString().ToLower(),
                    prompt = t.Prompt,
                    pngPath = t.PngPath,
                    replacementMpqPath = t.ReplacementMpqPath,
                    originalFilename = t.OriginalFilename,
                    width = t.Width,
                    height = t.Height,
                    blpSize = t.BlpBytes.Length
                }),
                outputDirectory = result.OutputDirectory,
                errors = result.Errors
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: GenerateTextures failed for {Name}", req.SpellName);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Helper: Read M2 from client data (reuses PatchBuilderService's resolution logic).
    /// Tries static files first, then patch MPQs.
    /// </summary>
    private byte[]? ReadM2FromClient(string m2Path)
    {
        // Use the same path resolution as PatchBuilderService
        var clientM2Path = _patchBuilder.GetType()
            .GetProperty("ClientM2Path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .GetValue(_patchBuilder)?.ToString()
            ?? "/home/wowvmangos/wowclient/m2";

        string normalized = m2Path.Replace('\\', Path.DirectorySeparatorChar);
        string fullPath = Path.Combine(clientM2Path, normalized);

        if (System.IO.File.Exists(fullPath))
            return System.IO.File.ReadAllBytes(fullPath);

        // Case-insensitive search
        string? dir = Path.GetDirectoryName(fullPath);
        string? file = Path.GetFileName(fullPath);
        if (dir != null && file != null && Directory.Exists(dir))
        {
            var match = Directory.GetFiles(dir)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return System.IO.File.ReadAllBytes(match);
        }

        // Try parent directory case-insensitive
        if (Directory.Exists(clientM2Path))
        {
            string[] segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string current = clientM2Path;
            foreach (var seg in segments)
            {
                bool isLast = seg == segments[^1];
                var entries = isLast ? Directory.GetFiles(current) : Directory.GetDirectories(current);
                var found = entries.FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e), seg, StringComparison.OrdinalIgnoreCase));
                if (found == null) return null;
                current = found;
            }
            if (System.IO.File.Exists(current))
                return System.IO.File.ReadAllBytes(current);
        }

        return null;
    }

    // ===================== TEXTURE REPROCESSING (Session 29) =====================

    /// <summary>
    /// POST /Patch/ReprocessTextures — Reprocess all cached textures with new floor/knee params.
    /// No ComfyUI — milliseconds not minutes.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ReprocessTextures([FromBody] ReprocessTexturesRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SpellName))
            return Json(new { success = false, error = "Spell name is required." });

        try
        {
            string safeName = new string(req.SpellName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            string texDir = Path.Combine(_env.WebRootPath, "images", "textures", "custom", safeName);
            string manifestPath = Path.Combine(texDir, "manifest.json");

            if (!System.IO.File.Exists(manifestPath))
                return Json(new { success = false, error = $"No texture manifest found for '{req.SpellName}'." });

            var manifest = System.Text.Json.JsonSerializer.Deserialize<List<TextureCacheEntry>>(
                await System.IO.File.ReadAllTextAsync(manifestPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest == null || manifest.Count == 0)
                return Json(new { success = false, error = "Manifest is empty." });

            var reprocessed = _textureService.ReprocessManifest(
                manifest, texDir, _vanillaBlps, req.FloorOverride, req.KneeOverride);

            if (reprocessed.Count == 0)
                return Json(new { success = false, error = "No textures were successfully reprocessed." });

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int written = 0;
            for (int i = 0; i < manifest.Count; i++)
            {
                if (!seen.Add(manifest[i].BlpFilename)) continue;
                if (!reprocessed.TryGetValue(i, out byte[]? blpBytes)) continue;
                await System.IO.File.WriteAllBytesAsync(Path.Combine(texDir, manifest[i].BlpFilename), blpBytes);
                written++;
            }

            var unifiedResult = await RebuildUnifiedPatchFromConfigsAsync();

            return Json(new
            {
                success = true,
                reprocessedCount = written,
                patchRebuilt = unifiedResult?.Success ?? false,
                patchFileName = unifiedResult?.PatchFileName,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: ReprocessTextures failed for '{Name}'", req.SpellName);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Patch/ApplySpellTuning — Apply a complete spell tuning preset (JSON Drop system).
    /// Reprocesses textures from existing PNGs, patches emitter M2Track values, rebuilds patch.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ApplySpellTuning([FromBody] ApplyTuningRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SpellName) || req.Preset == null)
            return Json(new { success = false, error = "Spell name and preset are required." });
        if (req.SpellEntry <= 0)
            return Json(new { success = false, error = "Spell entry is required." });

        try
        {
            var preset = req.Preset;
            string safeName = new string(req.SpellName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            string texDir = Path.Combine(_env.WebRootPath, "images", "textures", "custom", safeName);
            string comfyDir = Path.Combine(_env.WebRootPath, "images", "textures", "comfyui_output");
            Directory.CreateDirectory(texDir);

            int texturesProcessed = 0;
            int emittersPatched = 0;
            var newManifestEntries = new List<TextureCacheEntry>();

            // Read existing manifest
            string manifestPath = Path.Combine(texDir, "manifest.json");
            var existingManifest = new List<TextureCacheEntry>();
            if (System.IO.File.Exists(manifestPath))
            {
                existingManifest = System.Text.Json.JsonSerializer.Deserialize<List<TextureCacheEntry>>(
                    await System.IO.File.ReadAllTextAsync(manifestPath),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<TextureCacheEntry>();
            }

            var phasesWithPatchedM2s = new List<string>();

            foreach (var (phaseKey, phaseTuning) in preset.Phases)
            {
                // ── Texture reprocessing ──
                if (phaseTuning.Textures != null)
                {
                    foreach (var texTuning in phaseTuning.Textures)
                    {
                        string? vanillaFilename = existingManifest
                            .FirstOrDefault(e => e.Phase == phaseKey && e.TextureIndex == texTuning.SlotIndex)
                            ?.OriginalFilename;

                        byte[]? blpBytes = _textureService.ReprocessFromTuning(
                            texTuning, req.SpellName, comfyDir, _vanillaBlps, vanillaFilename, texDir);

                        if (blpBytes != null)
                        {
                            string blpFilename = $"tex_{safeName}_{phaseKey}_{texTuning.SlotIndex}_{texTuning.Role.ToLower()}.blp";
                            await System.IO.File.WriteAllBytesAsync(Path.Combine(texDir, blpFilename), blpBytes);

                            var existingEntry = existingManifest
                                .FirstOrDefault(e => e.Phase == phaseKey && e.TextureIndex == texTuning.SlotIndex);

                            string replacementPath = existingEntry?.ReplacementMpqPath
                                ?? SpellTextureService.BuildReplacementPath(safeName, texTuning.SlotIndex,
                                    (vanillaFilename?.Length ?? 30) + 1, phaseKey);

                            newManifestEntries.Add(new TextureCacheEntry
                            {
                                Phase = phaseKey,
                                TextureIndex = texTuning.SlotIndex,
                                BlpFilename = blpFilename,
                                ReplacementMpqPath = replacementPath,
                                OriginalFilename = vanillaFilename ?? "",
                                Role = texTuning.Role.ToLower(),
                                Prompt = "(reprocessed from tuning preset)"
                            });
                            texturesProcessed++;
                        }
                    }
                }

                // ── Emitter property patching ──
                if (phaseTuning.Emitters != null && phaseTuning.Emitters.Count > 0)
                {
                    string? m2Path = FindM2PathForPhase(preset.SourceSpellEntry, phaseKey);
                    if (m2Path != null)
                    {
                        byte[]? m2Data = ReadM2FromClient(m2Path);
                        if (m2Data != null)
                        {
                            byte[] patched = (byte[])m2Data.Clone();
                            foreach (var emitterPatch in phaseTuning.Emitters)
                            {
                                int count = M2EmitterParser.ApplyEmitterPatch(patched, emitterPatch);
                                emittersPatched += count;
                                _logger.LogInformation(
                                    "Tuning: [{Phase}] emitter[{Idx}] → {Count} properties patched",
                                    phaseKey, emitterPatch.EmitterIndex, count);
                            }

                            string m2CachePath = Path.Combine(texDir, $"m2_patched_{phaseKey}_{Path.GetFileName(m2Path)}");
                            await System.IO.File.WriteAllBytesAsync(m2CachePath, patched);
                            phasesWithPatchedM2s.Add(phaseKey);
                        }
                    }
                }
            }

            // Merge manifest
            var touchedSlots = new HashSet<string>(
                newManifestEntries.Select(e => $"{e.Phase}:{e.TextureIndex}"));
            var preserved = existingManifest
                .Where(e => !touchedSlots.Contains($"{e.Phase}:{e.TextureIndex}"))
                .ToList();
            preserved.AddRange(newManifestEntries);

            await System.IO.File.WriteAllTextAsync(manifestPath,
                System.Text.Json.JsonSerializer.Serialize(preserved,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));

            // Save preset
            string presetPath = Path.Combine(texDir, $"tuning_{preset.PresetName}.json");
            await System.IO.File.WriteAllTextAsync(presetPath,
                System.Text.Json.JsonSerializer.Serialize(preset,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));

            var unifiedResult = await RebuildUnifiedPatchFromConfigsAsync();

            return Json(new
            {
                success = true,
                presetName = preset.PresetName,
                texturesProcessed,
                emittersPatched,
                patchRebuilt = unifiedResult?.Success ?? false,
                patchFileName = unifiedResult?.PatchFileName,
                manifestEntries = preserved.Count,
                phasesWithPatchedM2s
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: ApplySpellTuning failed for '{Name}'", req.SpellName);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// GET /Patch/M2Emitters?entry=133&amp;phase=missile — Read emitter properties from M2 files.
    /// </summary>
    [HttpGet]
    public IActionResult M2Emitters(int entry, string? phase = null)
    {
        if (entry <= 0) return Json(new { success = false, error = "Entry required." });

        try
        {
            var phaseEmitters = new List<object>();
            var phasesToQuery = !string.IsNullOrEmpty(phase) ? new[] { phase }
                : new[] { "precast", "cast", "missile", "impact", "state", "stateDone", "channel" };

            foreach (var phaseKey in phasesToQuery)
            {
                string? m2Path = FindM2PathForPhase(entry, phaseKey);
                if (m2Path == null) continue;

                byte[]? m2Data = ReadM2FromClient(m2Path);
                if (m2Data == null) continue;

                var emitters = M2EmitterParser.ReadEmitters(m2Data);
                if (emitters.Count == 0) continue;

                phaseEmitters.Add(new
                {
                    phase = phaseKey,
                    m2Path,
                    emitters = emitters.Select(e => new
                    {
                        index = e.Index,
                        blendMode = (int)e.BlendMode,
                        emitterType = (int)e.EmitterType,
                        textureId = (int)e.TextureId,
                        scaleStart = e.ScaleStart,
                        scaleMid = e.ScaleMid,
                        scaleEnd = e.ScaleEnd,
                        tracks = e.TrackValues.Where(kv => kv.Value.HasValue)
                            .ToDictionary(kv => kv.Key, kv => new
                            {
                                value = kv.Value!.Value,
                                keyframes = e.TrackKeyframeCounts.GetValueOrDefault(kv.Key, 0)
                            })
                    })
                });
            }

            return Json(new { success = true, phases = phaseEmitters });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patch: M2Emitters lookup failed for #{Entry}", entry);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// GET /Patch/SpellManifest?spellName=ThunderBall — Returns the texture manifest for retune UI.
    /// </summary>
    [HttpGet]
    public IActionResult SpellManifest(string spellName)
    {
        if (string.IsNullOrWhiteSpace(spellName))
            return Json(new { success = false, error = "Spell name required." });

        string safeName = new string(spellName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        string texDir = Path.Combine(_env.WebRootPath, "images", "textures", "custom", safeName);
        string manifestPath = Path.Combine(texDir, "manifest.json");

        if (!System.IO.File.Exists(manifestPath))
            return Json(new { success = false, entries = Array.Empty<object>() });

        try
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<List<TextureCacheEntry>>(
                System.IO.File.ReadAllText(manifestPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest == null) return Json(new { success = false, entries = Array.Empty<object>() });

            // Get all PNGs in the directory for fuzzy matching
            var allPngs = Directory.Exists(texDir)
                ? Directory.GetFiles(texDir, "*.png").Select(Path.GetFileName).ToList()
                : new List<string?>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = manifest.Where(e => seen.Add(e.BlpFilename)).Select(e =>
            {
                // Try exact match first: blp filename → png
                string exactPng = Path.ChangeExtension(e.BlpFilename, ".png");
                bool hasPng = System.IO.File.Exists(Path.Combine(texDir, exactPng));
                string? pngFilename = hasPng ? exactPng : null;

                // If no exact match, search for a PNG with matching phase+index pattern
                // BLP: tex_ThunderBall_precast_0_shape.blp
                // PNG: tex_ThunderBall_precast_0_Body_HandFire.png
                // Match on: tex_{spell}_{phase}_{index}_
                if (!hasPng)
                {
                    string prefix = $"tex_{safeName}_{e.Phase}_{e.TextureIndex}_";
                    // Also try without phase (heuristic path): tex_{spell}_{index}_
                    string prefixNoPhase = $"tex_{safeName}_{e.TextureIndex}_";

                    var match = allPngs.FirstOrDefault(p =>
                        p != null && (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                   || p.StartsWith(prefixNoPhase, StringComparison.OrdinalIgnoreCase))
                        && p.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        pngFilename = match;
                        hasPng = true;
                    }
                }

                // Check for debug processed PNG (try both naming patterns)
                string debugExact = Path.GetFileNameWithoutExtension(e.BlpFilename) + "_processed.png";
                bool hasDebug = System.IO.File.Exists(Path.Combine(texDir, debugExact));
                string? debugFilename = hasDebug ? debugExact : null;

                if (!hasDebug && pngFilename != null)
                {
                    string debugAlt = Path.GetFileNameWithoutExtension(pngFilename) + "_processed.png";
                    hasDebug = System.IO.File.Exists(Path.Combine(texDir, debugAlt));
                    if (hasDebug) debugFilename = debugAlt;
                }

                return new
                {
                    e.Phase,
                    e.TextureIndex,
                    e.BlpFilename,
                    e.ReplacementMpqPath,
                    e.OriginalFilename,
                    e.Role,
                    hasPng,
                    pngWebPath = hasPng ? $"/images/textures/custom/{safeName}/{pngFilename}" : (string?)null,
                    hasDebugPng = hasDebug,
                    debugPngWebPath = hasDebug ? $"/images/textures/custom/{safeName}/{debugFilename}" : (string?)null,
                };
            }).ToList();

            return Json(new { success = true, entries, spellName = safeName });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>Session 29: Find M2 file path for a spell phase by walking the DBC chain.</summary>
    private string? FindM2PathForPhase(int sourceSpellEntry, string phaseKey)
    {
        try
        {
            uint visualId = 0;
            using (var conn = _db.Mangos())
            {
                visualId = conn.ExecuteScalar<uint>(
                    "SELECT spellVisual1 FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                    new { E = sourceSpellEntry });
            }
            if (visualId == 0 && _dbc.SpellEntries.TryGetValue((uint)sourceSpellEntry, out var dbcSpell))
                visualId = dbcSpell.SpellVisual1;
            if (visualId == 0) return null;

            var dbcPath = Path.Combine(_dbc.DbcPath, "SpellVisual.dbc");
            if (!System.IO.File.Exists(dbcPath)) return null;
            var visualDbc = DbcWriterService.ReadDbc(dbcPath);
            var visualRow = visualDbc.GetRow(visualId);
            if (visualRow == null) return null;

            var efnDbc = DbcWriterService.ReadDbc(Path.Combine(_dbc.DbcPath, "SpellVisualEffectName.dbc"));

            if (phaseKey == "missile")
            {
                uint missileEffectId = visualRow[7];
                if (missileEffectId == 0) return null;
                var missileRow = efnDbc.GetRow(missileEffectId);
                if (missileRow == null) return null;
                return SpellVisualCloner.NormalizeM2Extension(efnDbc.ReadString(missileRow[2]));
            }

            int kitField = phaseKey switch
            {
                "precast" => 1,
                "cast" => 2,
                "impact" => 3,
                "state" => 4,
                "stateDone" => 5,
                "channel" => 6,
                _ => -1
            };
            if (kitField < 0) return null;

            uint kitId = visualRow[kitField];
            if (kitId <= 1) return null;

            var kitDbc = DbcWriterService.ReadDbc(Path.Combine(_dbc.DbcPath, "SpellVisualKit.dbc"));
            var kitRow = kitDbc.GetRow(kitId);
            if (kitRow == null) return null;

            foreach (int ef in new[] { 3, 4, 5, 6, 7, 8, 9, 10 })
            {
                uint effectId = kitRow[ef];
                if (effectId == 0 || effectId == 0xFFFFFFFF) continue;
                var effectRow = efnDbc.GetRow(effectId);
                if (effectRow == null) continue;
                string m2Path = SpellVisualCloner.NormalizeM2Extension(efnDbc.ReadString(effectRow[2]));
                if (!string.IsNullOrEmpty(m2Path)) return m2Path;
            }
            return null;
        }
        catch { return null; }
    }

    // ===================== HELPERS =====================

    /// <summary>
    /// Load all saved spell configs and rebuild the unified patch-3.MPQ.
    /// Called after every Generate or Delete operation.
    /// </summary>
    private async Task<UnifiedPatchResult?> RebuildUnifiedPatchFromConfigsAsync()
    {
        var configs = await _spellConfig.GetAllConfigsAsync();

        var requests = new List<SpellPatchRequest>();
        using var conn = _db.Mangos();

        foreach (var config in configs)
        {
            // Look up the source spell's visual ID
            var sourceVisual = await conn.ExecuteScalarAsync<uint?>(
                "SELECT spellVisual1 FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                new { E = config.SourceEntry });

            if (sourceVisual == null || sourceVisual == 0)
            {
                _logger.LogWarning("Patch: Skipping #{Entry} — source spell {Src} has no visual",
                    config.Entry, config.SourceEntry);
                continue;
            }

            // Build per-role params from saved config
            Dictionary<string, M2ParticlePatcher.ParticlePatchParams>? perRoleParams = null;
            M2ParticlePatcher.ParticlePatchParams? globalParams = null;

            if (config.PhaseParams != null)
            {
                perRoleParams = new Dictionary<string, M2ParticlePatcher.ParticlePatchParams>();
                var pp = config.PhaseParams;
                var phaseMap = new (string key, PhaseKnobs? knobs)[]
                {
                    ("precast",   pp.Precast),
                    ("cast",      pp.Cast),
                    ("missile",   pp.Missile),
                    ("impact",    pp.Impact),
                    ("state",     pp.State),
                    ("stateDone", pp.StateDone),
                    ("channel",   pp.Channel),
                };

                foreach (var (key, knobs) in phaseMap)
                {
                    if (knobs != null)
                    {
                        var built = BuildParticleParamsForPhase(config.ColorPreset, knobs);
                        if (built != null) perRoleParams[key] = built;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(config.ColorPreset))
            {
                globalParams = BuildParticleParams(config.ColorPreset, 1.0f);
            }

            // Look up skill_line_ability for R1 to get SkillId and ClassMask for DBC patching
            int slaSkillId = 0, slaClassMask = 0;
            try
            {
                var slaRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT skill_id, class_mask FROM skill_line_ability WHERE spell_id = @E AND build = 5875 LIMIT 1",
                    new { E = config.Entry });
                if (slaRow != null)
                {
                    slaSkillId = Convert.ToInt32(slaRow.skill_id ?? 0);
                    slaClassMask = Convert.ToInt32(slaRow.class_mask ?? 0);
                }
            }
            catch { /* fallback to 0 */ }

            // Session 45: Load R1 gameplay overrides from spell_template for DBC patching.
            // Without this, R1's DBC row clones the source and keeps source mana/damage/level.
            var r1Fields = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT manaCost, effectBasePoints1, effectDieSides1, effectBasePoints2,
                         spellLevel, baseLevel, castingTimeIndex, rangeIndex,
                         effectRealPointsPerLevel1, effectBonusCoefficient1, speed, recoveryTime
                  FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                new { E = config.Entry });

            requests.Add(new SpellPatchRequest
            {
                SpellEntry = (uint)config.Entry,
                SourceSpellEntry = (uint)config.SourceEntry,
                SourceVisualId = sourceVisual.Value,
                SpellName = config.SpellName,
                NameSubtext = config.NameSubtext,
                Description = config.Description,
                Tooltip = config.Tooltip,
                SkillId = slaSkillId,
                ClassMask = slaClassMask,
                SchoolMask = GetSchoolMask(
                    (int)(await conn.ExecuteScalarAsync<uint?>(
                        "SELECT school FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                        new { E = config.Entry }) ?? 0)),
                SpellIconId = GetSchoolIconId(
                    (int)(await conn.ExecuteScalarAsync<uint?>(
                        "SELECT school FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                        new { E = config.Entry }) ?? 2)),
                IconPngPath = config.IconPath,
                ParticleParams = globalParams,
                PerRoleParams = perRoleParams,
                // Session 14/15: blend mode + emitter type + texture cache
                PerPhaseBlendMode = BuildBlendModeMap(config.PhaseParams),
                PerPhaseEmitterType = BuildEmitterTypeMap(config.PhaseParams),
                PerPhaseTextures = LoadCachedTextures(config.Entry, config.SpellName),
                // Session 30: load pre-patched M2s from experiment lab / tuning system
                PerPhasePatchedM2s = LoadPatchedM2s(config.Entry, config.SpellName),
                // Session 45: R1 gameplay fields for DBC accuracy
                ManaCost = r1Fields != null ? (int?)Convert.ToInt32(r1Fields.manaCost ?? 0) : null,
                EffectBasePoints0 = r1Fields != null ? (int?)Convert.ToInt32(r1Fields.effectBasePoints1 ?? 0) : null,
                EffectDieSides0 = r1Fields != null ? (int?)Convert.ToInt32(r1Fields.effectDieSides1 ?? 0) : null,
                EffectBasePoints1 = r1Fields != null ? (int?)Convert.ToInt32(r1Fields.effectBasePoints2 ?? 0) : null,
                SpellLevel = r1Fields != null ? (int?)Convert.ToInt32(r1Fields.spellLevel ?? 0) : null,
                BaseLevel = r1Fields != null ? (int?)Convert.ToInt32(r1Fields.baseLevel ?? 0) : null,
                CastingTimeIndex = r1Fields != null ? (int?)Convert.ToInt32(r1Fields.castingTimeIndex ?? 0) : null,
                RangeIndex = r1Fields != null ? (int?)Convert.ToInt32(r1Fields.rangeIndex ?? 0) : null,
                EffectRealPointsPerLevel0 = r1Fields != null ? (float?)Convert.ToSingle(r1Fields.effectRealPointsPerLevel1 ?? 0f) : null,
            });
        }

        // ── Session 33: Populate AdditionalRanks for each spell that has a rank chain ──
        foreach (var request in requests)
        {
            try
            {
                var rankChain = await _spellCreator.GetRankChainForPatchingAsync((int)request.SpellEntry);
                if (rankChain.Count > 0)
                {
                    request.AdditionalRanks = rankChain.Select(r => new RankPatchData
                    {
                        Entry = (uint)r.Entry,
                        SourceRankEntry = (uint)r.SourceRankEntry,
                        Rank = r.Rank,
                        SpellName = r.SpellName,
                        Description = r.Description,
                        SchoolMask = r.School > 0 ? GetSchoolMask(r.School) : request.SchoolMask,
                        SkillLineAbilityData = r.SkillId > 0
                            ? (r.SkillId, r.ClassMask, r.SupersededBySpell)
                            : null,
                        // Session 33: Effect/gameplay fields for DBC tooltip accuracy
                        EffectBasePoints0 = r.EffectBasePoints1,
                        EffectDieSides0 = r.EffectDieSides1,
                        EffectBasePoints1 = r.EffectBasePoints2,
                        ManaCost = r.ManaCost,
                        SpellLevel = r.SpellLevel,
                        BaseLevel = r.BaseLevel,
                        CastingTimeIndex = r.CastingTimeIndex,
                        EffectRealPointsPerLevel0 = r.EffectRealPointsPerLevel1,
                    }).ToList();

                    _logger.LogInformation("Patch: Attached {Count} rank entries to spell #{E} ({Name})",
                        request.AdditionalRanks.Count, request.SpellEntry, request.SpellName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Patch: Failed to load rank chain for spell #{E}", request.SpellEntry);
            }
        }

        // ── Session 34: Attach trainer wrapper data for DBC patching ──
        // Trainer wrappers (50000+ range) need Spell.dbc entries so the client
        // can render them in the trainer UI.
        try
        {
            using var twConn = _spellCreator.CreateMangosConnection();
            var wrappers = (await Dapper.SqlMapper.QueryAsync<dynamic>(twConn,
                @"SELECT w.entry, w.effectTriggerSpell1, w.name, w.nameSubtext,
                         COALESCE(r1.spellIconId, 0) AS r1IconId
                  FROM spell_template w
                  LEFT JOIN spell_chain sc ON sc.spell_id = w.effectTriggerSpell1
                  LEFT JOIN spell_template r1 ON r1.entry = COALESCE(sc.first_spell, w.effectTriggerSpell1)
                    AND r1.build = (SELECT MAX(build) FROM spell_template WHERE entry = r1.entry)
                  WHERE w.entry >= @Base AND w.entry <= @Max 
                  AND w.effect1 = 36",
                new { Base = SpellCreatorService.TRAINER_WRAPPER_BASE, Max = SpellCreatorService.TRAINER_WRAPPER_MAX })).ToList();

            if (wrappers.Count > 0)
            {
                // Attach all wrappers to the first request (they all share the same Spell.dbc)
                var firstRequest = requests.FirstOrDefault();
                if (firstRequest != null)
                {
                    firstRequest.TrainerWrappers = wrappers.Select(w => new TrainerWrapperData
                    {
                        WrapperEntry = (uint)Convert.ToInt32(w.entry),
                        TeachesSpellEntry = (uint)Convert.ToInt32(w.effectTriggerSpell1),
                        SpellName = w.name?.ToString() ?? "",
                        RankSubtext = w.nameSubtext?.ToString() ?? "",
                        IconId = (uint)Convert.ToInt32(w.r1IconId ?? 0),
                    }).ToList();

                    _logger.LogInformation("Patch: Attached {Count} trainer wrappers for DBC patching", firstRequest.TrainerWrappers.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patch: Failed to load trainer wrappers for DBC patching");
        }

        return await _patchBuilder.RebuildUnifiedPatchAsync(requests);
    }

    private static M2ParticlePatcher.ParticlePatchParams? BuildParticleParams(string? preset, float intensity)
    {
        if (string.IsNullOrEmpty(preset) || preset == "none") return null;
        float rate = intensity > 0 ? intensity : 1.0f;

        // Handle custom color: "custom:#rrggbb" → hue-shift mode
        if (preset.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
        {
            uint? rgb = ParseHexToRgb(preset.Substring(7));
            if (rgb == null) return null;
            return new M2ParticlePatcher.ParticlePatchParams
            {
                UseHueShift = true,
                HueShiftColor = rgb.Value,
                EmissionRateMultiplier = rate,
                EmissionAreaMultiplier = rate > 1 ? rate * 0.8f : null,
                ScaleMultiplier = rate > 1 ? 1.0f + (rate - 1.0f) * 0.3f : null
            };
        }

        return preset switch
        {
            "shadow" => new M2ParticlePatcher.ParticlePatchParams
            {
                ColorValues = new uint[] { 0xFFB432FF, 0xCC7820C8, 0x003C0A96 },
                EmissionRateMultiplier = rate,
                EmissionAreaMultiplier = rate > 1 ? rate * 0.8f : null,
                ScaleMultiplier = rate > 1 ? 1.0f + (rate - 1.0f) * 0.3f : null
            },
            "frost" => new M2ParticlePatcher.ParticlePatchParams
            {
                ColorValues = new uint[] { 0xFFAADDFF, 0xCC4488CC, 0x00112266 },
                EmissionRateMultiplier = rate,
                EmissionAreaMultiplier = rate > 1 ? rate * 0.8f : null,
                ScaleMultiplier = rate > 1 ? 1.0f + (rate - 1.0f) * 0.3f : null
            },
            "holy" => new M2ParticlePatcher.ParticlePatchParams
            {
                ColorValues = new uint[] { 0xFFFFF5CC, 0xCCFFDD66, 0x00CCAA00 },
                EmissionRateMultiplier = rate,
                EmissionAreaMultiplier = rate > 1 ? rate * 0.8f : null,
                ScaleMultiplier = rate > 1 ? 1.0f + (rate - 1.0f) * 0.3f : null
            },
            "nature" => new M2ParticlePatcher.ParticlePatchParams
            {
                ColorValues = new uint[] { 0xFF44FF44, 0xCC22AA22, 0x00115511 },
                EmissionRateMultiplier = rate,
                EmissionAreaMultiplier = rate > 1 ? rate * 0.8f : null,
                ScaleMultiplier = rate > 1 ? 1.0f + (rate - 1.0f) * 0.3f : null
            },
            "arcane" => new M2ParticlePatcher.ParticlePatchParams
            {
                ColorValues = new uint[] { 0xFFFF88FF, 0xCCBB44CC, 0x00660088 },
                EmissionRateMultiplier = rate,
                EmissionAreaMultiplier = rate > 1 ? rate * 0.8f : null,
                ScaleMultiplier = rate > 1 ? 1.0f + (rate - 1.0f) * 0.3f : null
            },
            "fire" => new M2ParticlePatcher.ParticlePatchParams
            {
                EmissionRateMultiplier = rate != 1.0f ? rate : null,
                EmissionAreaMultiplier = rate > 1 ? rate * 0.8f : null,
                ScaleMultiplier = rate > 1 ? 1.0f + (rate - 1.0f) * 0.3f : null
            },
            _ => null
        };
    }

    /// <summary>
    /// Build ParticlePatchParams from per-phase knobs (Visual Designer mode).
    /// Each knob value is a multiplier: 1.0 = vanilla, &gt;1 = intensified.
    /// Color is still determined by the global preset.
    /// </summary>
    private static M2ParticlePatcher.ParticlePatchParams? BuildParticleParamsForPhase(
        string? preset, PhaseKnobs knobs)
    {
        bool anyChange = knobs.EmissionRate != 1.0f || knobs.Scale != 1.0f ||
                         knobs.Speed != 1.0f || knobs.Lifespan != 1.0f || knobs.Area != 1.0f;

        // ── Color resolution: per-phase color → global preset → none ──
        // Per-phase color uses hue-shift mode (preserves per-emitter depth).
        // Global preset uses flat-replace mode (legacy behavior).
        uint[]? colors = null;
        bool useHueShift = false;
        uint hueShiftColor = 0;

        if (!string.IsNullOrEmpty(knobs.Color))
        {
            // Per-phase color override → hue-shift mode
            uint? rgb = ParseHexToRgb(knobs.Color);
            if (rgb.HasValue)
            {
                useHueShift = true;
                hueShiftColor = rgb.Value;
            }
        }
        else if (preset != null && preset.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
        {
            // Global custom color → hue-shift mode
            uint? rgb = ParseHexToRgb(preset.Substring(7));
            if (rgb.HasValue)
            {
                useHueShift = true;
                hueShiftColor = rgb.Value;
            }
        }
        else
        {
            // Named preset → flat-replace mode (legacy)
            colors = preset switch
            {
                "shadow" => new uint[] { 0xFFB432FF, 0xCC7820C8, 0x003C0A96 },
                "frost" => new uint[] { 0xFFAADDFF, 0xCC4488CC, 0x00112266 },
                "holy" => new uint[] { 0xFFFFF5CC, 0xCCFFDD66, 0x00CCAA00 },
                "nature" => new uint[] { 0xFF44FF44, 0xCC22AA22, 0x00115511 },
                "arcane" => new uint[] { 0xFFFF88FF, 0xCCBB44CC, 0x00660088 },
                _ => null  // "fire" or unknown = keep original colors
            };
        }

        if (!anyChange && colors == null && !useHueShift) return null;

        return new M2ParticlePatcher.ParticlePatchParams
        {
            ColorValues = colors,
            UseHueShift = useHueShift,
            HueShiftColor = hueShiftColor,
            EmissionRateMultiplier = knobs.EmissionRate != 1.0f ? knobs.EmissionRate : null,
            ScaleMultiplier = knobs.Scale != 1.0f ? knobs.Scale : null,
            EmissionSpeedMultiplier = knobs.Speed != 1.0f ? knobs.Speed : null,
            LifespanMultiplier = knobs.Lifespan != 1.0f ? knobs.Lifespan : null,
            EmissionAreaMultiplier = knobs.Area != 1.0f ? knobs.Area : null,
        };
    }

    /// <summary>Build per-phase blend mode map from saved PhaseParams (Session 14).</summary>
    private static Dictionary<string, byte>? BuildBlendModeMap(PerPhaseParams? pp)
    {
        if (pp == null) return null;

        var map = new Dictionary<string, byte>();
        var phases = new (string key, PhaseKnobs? knobs)[]
        {
            ("precast", pp.Precast), ("cast", pp.Cast), ("missile", pp.Missile),
            ("impact", pp.Impact), ("state", pp.State), ("stateDone", pp.StateDone),
            ("channel", pp.Channel)
        };

        foreach (var (key, knobs) in phases)
        {
            if (knobs?.BlendMode != null)
                map[key] = (byte)knobs.BlendMode.Value;
        }

        return map.Count > 0 ? map : null;
    }

    /// <summary>Build per-phase emitter type map from saved PhaseParams (Session 15).</summary>
    private static Dictionary<string, byte>? BuildEmitterTypeMap(PerPhaseParams? pp)
    {
        if (pp == null) return null;

        var map = new Dictionary<string, byte>();
        var phases = new (string key, PhaseKnobs? knobs)[]
        {
            ("precast", pp.Precast), ("cast", pp.Cast), ("missile", pp.Missile),
            ("impact", pp.Impact), ("state", pp.State), ("stateDone", pp.StateDone),
            ("channel", pp.Channel)
        };

        foreach (var (key, knobs) in phases)
        {
            if (knobs?.EmitterType != null)
                map[key] = (byte)knobs.EmitterType.Value;
        }

        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Load cached BLP textures for a spell from disk.
    /// BLPs are cached at: wwwroot/images/textures/custom/{spellName}/tex_{name}_{idx}_{role}.blp
    /// Returns null if no cached textures exist (spell was created without a theme).
    /// </summary>
    private Dictionary<string, List<TextureReplacement>>? LoadCachedTextures(int entry, string spellName)
    {
        string safeName = new string(spellName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        string texDir = Path.Combine(_env.WebRootPath, "images", "textures", "custom", safeName);

        if (!Directory.Exists(texDir)) return null;

        var blpFiles = Directory.GetFiles(texDir, "*.blp");
        if (blpFiles.Length == 0) return null;

        // Also need a manifest to know which phase each BLP belongs to
        string manifestPath = Path.Combine(texDir, "manifest.json");
        if (!System.IO.File.Exists(manifestPath))
        {
            _logger.LogDebug("Patch: No texture manifest for #{Entry} ({Name}), skipping texture cache", entry, spellName);
            return null;
        }

        try
        {
            var manifestJson = System.IO.File.ReadAllText(manifestPath);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<List<TextureCacheEntry>>(manifestJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest == null || manifest.Count == 0) return null;

            var result = new Dictionary<string, List<TextureReplacement>>();

            foreach (var entry_ in manifest)
            {
                string blpPath = Path.Combine(texDir, entry_.BlpFilename);
                if (!System.IO.File.Exists(blpPath)) continue;

                byte[] blpBytes = System.IO.File.ReadAllBytes(blpPath);
                if (blpBytes.Length == 0) continue;

                if (!result.ContainsKey(entry_.Phase))
                    result[entry_.Phase] = new List<TextureReplacement>();

                result[entry_.Phase].Add(new TextureReplacement
                {
                    TextureIndex = entry_.TextureIndex,
                    ReplacementMpqPath = entry_.ReplacementMpqPath,
                    BlpBytes = blpBytes
                });
            }

            if (result.Count > 0)
                _logger.LogInformation("Patch: Loaded {Count} cached textures for #{Entry} ({Name})",
                    result.Values.Sum(l => l.Count), entry, spellName);

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patch: Failed to load texture cache for #{Entry}", entry);
            return null;
        }
    }

    /// <summary>
    /// Generate textures for all phases of a spell and cache BLP bytes + manifest to disk.
    /// Called during Generate (first-time creation) when a texture theme is selected.
    ///
    /// Session 20: Recipe integration — looks up recipe by source spell entry.
    /// Session 21: Runtime trace collection — writes {SpellName}_trace.json.
    /// </summary>
    private async Task GenerateAndCacheTexturesForSpellAsync(
        int spellEntry, int sourceSpellEntry, string spellName, string themeKey,
        Dictionary<string, List<TextureSlotOverride>>? customOverrides)
    {
        _logger.LogInformation("Patch: Generating textures for #{Entry} ({Name}) with theme '{Theme}'",
            spellEntry, spellName, themeKey);

        // ── Recipe lookup ──
        var recipe = _recipeService.GetRecipeBySourceEntry((uint)sourceSpellEntry);
        if (recipe != null)
            _logger.LogInformation("Patch: Found recipe '{Id}' for source spell #{Src}",
                recipe.RecipeId, sourceSpellEntry);
        else
            _logger.LogInformation("Patch: No recipe for source spell #{Src} — using heuristics",
                sourceSpellEntry);

        // Get source spell's M2 texture info (same as M2Textures endpoint)
        uint visualId = 0;
        using (var conn = _db.Mangos())
        {
            visualId = await conn.ExecuteScalarAsync<uint>(
                "SELECT spellVisual1 FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                new { E = sourceSpellEntry });
        }
        if (visualId == 0 && _dbc.SpellEntries.TryGetValue((uint)sourceSpellEntry, out var dbcSpell))
            visualId = dbcSpell.SpellVisual1;
        if (visualId == 0)
        {
            _logger.LogWarning("Patch: Cannot generate textures — no visual ID for source #{Src}", sourceSpellEntry);
            return;
        }

        // Read DBC chain to find M2 files per phase
        var dbcPath = Path.Combine(_dbc.DbcPath, "SpellVisual.dbc");
        if (!System.IO.File.Exists(dbcPath)) return;
        var visualDbc = DbcWriterService.ReadDbc(dbcPath);
        var visualRow = visualDbc.GetRow(visualId);
        if (visualRow == null) return;

        var kitDbc = DbcWriterService.ReadDbc(Path.Combine(_dbc.DbcPath, "SpellVisualKit.dbc"));
        var efnDbc = DbcWriterService.ReadDbc(Path.Combine(_dbc.DbcPath, "SpellVisualEffectName.dbc"));

        var phaseDefs = new (string key, int kitField)[]
        {
            ("precast", 1), ("cast", 2), ("impact", 3),
            ("state", 4), ("stateDone", 5), ("channel", 6)
        };

        string safeName = new string(spellName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

        // ═══════════════════════════════════════════════════════════════
        // Session 28: Collect ALL phase work items first, then fire in
        // parallel. Previous design awaited each phase sequentially —
        // fast GPU idled waiting for slow GPU to finish same phase.
        // ═══════════════════════════════════════════════════════════════
        var workItems = new List<M2TextureWorkItem>();

        // Collect work items from standard phases
        foreach (var (phaseKey, kitField) in phaseDefs)
        {
            uint kitId = visualRow[kitField];
            if (kitId <= 1) continue;

            var kitRow = kitDbc.GetRow(kitId);
            if (kitRow == null) continue;

            int[] effectFields = { 3, 4, 5, 6, 7, 8, 9, 10 };
            foreach (int ef in effectFields)
            {
                uint effectId = kitRow[ef];
                if (effectId == 0 || effectId == 0xFFFFFFFF) continue;
                var effectRow = efnDbc.GetRow(effectId);
                if (effectRow == null) continue;

                string m2Path = SpellVisualCloner.NormalizeM2Extension(efnDbc.ReadString(effectRow[2]));
                if (string.IsNullOrEmpty(m2Path)) continue;

                byte[]? m2Data = ReadM2FromClient(m2Path);
                if (m2Data == null) continue;

                RecipePhase? recipePhase = FindRecipePhaseForM2(recipe, m2Path, phaseKey);
                string effectivePhaseName = recipePhase?.EffectRole ?? phaseKey;

                workItems.Add(new M2TextureWorkItem
                {
                    M2Data = m2Data,
                    Phase = effectivePhaseName,
                    SafeName = safeName,
                    ThemeKey = themeKey,
                    CustomOverrides = customOverrides,
                    RecipePhase = recipePhase,
                    M2Path = m2Path,
                    DbcPhaseKey = phaseKey
                });
            }
        }

        // Collect missile work item
        uint missileEffectId = visualRow[7];
        if (missileEffectId > 0)
        {
            var missileRow = efnDbc.GetRow(missileEffectId);
            if (missileRow != null)
            {
                string m2Path = SpellVisualCloner.NormalizeM2Extension(efnDbc.ReadString(missileRow[2]));
                byte[]? m2Data = !string.IsNullOrEmpty(m2Path) ? ReadM2FromClient(m2Path) : null;
                if (m2Data != null)
                {
                    RecipePhase? recipePhase = FindRecipePhaseForM2(recipe, m2Path, "missile");
                    string effectivePhaseName = recipePhase?.EffectRole ?? "missile";

                    workItems.Add(new M2TextureWorkItem
                    {
                        M2Data = m2Data,
                        Phase = effectivePhaseName,
                        SafeName = safeName,
                        ThemeKey = themeKey,
                        CustomOverrides = customOverrides,
                        RecipePhase = recipePhase,
                        M2Path = m2Path,
                        DbcPhaseKey = "missile"
                    });
                }
            }
        }

        _logger.LogInformation(
            "Patch: Collected {Count} M2 work items across all phases — dispatching in parallel",
            workItems.Count);

        // ── Fire ALL work items in parallel ──
        var tasks = workItems.Select(wi =>
            GenerateTexturesForM2ReturnAsync(wi)
        ).ToList();

        var results = await Task.WhenAll(tasks);

        // ── Merge results sequentially (diagnostics, manifest, traces) ──
        var manifest = new List<TextureCacheEntry>();
        var diagnosticPhases = new Dictionary<string, object>();
        var tracePhases = new Dictionary<string, object>();

        foreach (var r in results)
        {
            if (r == null) continue;
            manifest.AddRange(r.ManifestEntries);

            // Deduplicate phase keys (same logic as before)
            string diagKey = diagnosticPhases.ContainsKey(r.PhaseKey)
                ? $"{r.PhaseKey}_{Path.GetFileNameWithoutExtension(r.M2Path)}"
                : r.PhaseKey;
            diagnosticPhases[diagKey] = r.DiagnosticData;

            string traceKey = tracePhases.ContainsKey(r.PhaseKey)
                ? $"{r.PhaseKey}_{Path.GetFileNameWithoutExtension(r.M2Path)}"
                : r.PhaseKey;
            tracePhases[traceKey] = r.TraceData;
        }

        // Write manifest
        if (manifest.Count > 0)
        {
            string texDir = Path.Combine(_env.WebRootPath, "images", "textures", "custom", safeName);
            Directory.CreateDirectory(texDir);
            string manifestPath = Path.Combine(texDir, "manifest.json");
            var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            await System.IO.File.WriteAllTextAsync(manifestPath, manifestJson);

            _logger.LogInformation("Patch: Cached {Count} texture BLPs for #{Entry} ({Name}) → {Dir}",
                manifest.Count, spellEntry, spellName, texDir);
        }

        // ── Session 20: Write diagnostic JSON (kept for backward compat) ──
        WriteDiagnosticJson(safeName, sourceSpellEntry, recipe, diagnosticPhases);

        // ── Session 21: Write runtime trace JSON ──
        WriteTraceJson(safeName, sourceSpellEntry, recipe, tracePhases);
    }

    /// <summary>
    /// Session 28: Work item for parallel M2 texture generation.
    /// Contains everything needed to generate textures for one M2 file.
    /// </summary>
    private class M2TextureWorkItem
    {
        public byte[] M2Data { get; set; } = null!;
        public string Phase { get; set; } = "";
        public string SafeName { get; set; } = "";
        public string ThemeKey { get; set; } = "";
        public Dictionary<string, List<TextureSlotOverride>>? CustomOverrides { get; set; }
        public RecipePhase? RecipePhase { get; set; }
        public string M2Path { get; set; } = "";
        public string DbcPhaseKey { get; set; } = "";
    }

    /// <summary>
    /// Session 28: Result from parallel M2 texture generation.
    /// Replaces the old pattern of mutating shared manifest/diagnostics/traces dicts.
    /// </summary>
    private class M2TextureResult
    {
        public string PhaseKey { get; set; } = "";
        public string M2Path { get; set; } = "";
        public List<TextureCacheEntry> ManifestEntries { get; set; } = new();
        public object DiagnosticData { get; set; } = null!;
        public object TraceData { get; set; } = null!;
    }

    /// <summary>
    /// Match a recipe phase to the M2 file being processed.
    /// Tries sourceM2 filename match first, then falls back to phase key.
    /// Returns null if no recipe or no matching phase found.
    /// </summary>
    private static RecipePhase? FindRecipePhaseForM2(SpellRecipe? recipe, string m2Path, string phaseKey)
    {
        if (recipe == null) return null;

        string m2Filename = Path.GetFileName(m2Path).ToLowerInvariant();

        // First pass: match by sourceM2 filename (most precise)
        foreach (var (_, phase) in recipe.Phases)
        {
            if (!string.IsNullOrEmpty(phase.SourceM2) &&
                string.Equals(Path.GetFileName(phase.SourceM2), m2Filename,
                    StringComparison.OrdinalIgnoreCase))
            {
                return phase;
            }
        }

        // Second pass: match by phase key (e.g. "missile", "cast")
        // Recipe keys might be "missile", "cast_leftHand", etc.
        // Try exact match first, then prefix match
        if (recipe.Phases.TryGetValue(phaseKey, out var exact))
            return exact;

        // Prefix match: "cast" matches "cast_leftHand"
        foreach (var (key, phase) in recipe.Phases)
        {
            if (key.StartsWith(phaseKey, StringComparison.OrdinalIgnoreCase))
                return phase;
        }

        return null;
    }

    /// <summary>
    /// Session 28: Parallel-safe texture generation for a single M2 file.
    /// Returns results instead of mutating shared collections.
    /// All generation + post-processing logic unchanged from Session 21.
    /// </summary>
    private async Task<M2TextureResult?> GenerateTexturesForM2ReturnAsync(M2TextureWorkItem wi)
    {
        var m2Data = wi.M2Data;
        var phase = wi.Phase;
        var safeName = wi.SafeName;
        var themeKey = wi.ThemeKey;
        var customOverrides = wi.CustomOverrides;
        var recipePhase = wi.RecipePhase;
        var m2Path = wi.M2Path;
        var dbcPhaseKey = wi.DbcPhaseKey;

        var parsedTextures = M2TextureParser.ParseTextures(m2Data);
        if (parsedTextures.Count == 0) return null;

        // Session 18: Extract per-texture blend modes from emitter data
        var blendModes = M2TextureParser.GetTextureBlendModes(m2Data);

        // ── Build slot list: recipe-driven or heuristic-driven ──
        List<TextureSlotRequest> slots;
        bool usingRecipe = recipePhase != null && recipePhase.Slots.Count > 0;

        // Session 21: trace the slot-building decision
        string slotBuildSource;
        int recipeSlotCount = 0;
        int parsedTextureCount = parsedTextures.Count;

        if (usingRecipe)
        {
            // Recipe path — explicit role/job/density/vignette per slot
            slots = SpellTextureService.RecipePhaseToSlots(recipePhase!, phase);
            slotBuildSource = "RECIPE";
            recipeSlotCount = recipePhase!.Slots.Count;
            _logger.LogInformation("Patch: [{Phase}] Using recipe — {Count} slots defined",
                phase, slots.Count);

            // Fill in any M2-parsed data the recipe doesn't carry
            foreach (var slot in slots)
            {
                var parsed = parsedTextures.FirstOrDefault(t => t.Index == slot.Index);
                if (parsed != null)
                {
                    // Recipe already sets OriginalFilenameLength from VanillaFilename,
                    // but use the actual M2-measured length for safety
                    slot.OriginalFilenameLength = parsed.ActualByteLength;

                    // Session 22: Read vanilla BLP specs from rawblps
                    var vanillaInfo = _vanillaBlps.GetBlpInfo(parsed.Filename);
                    if (vanillaInfo != null)
                    {
                        slot.OriginalWidth = vanillaInfo.Width;
                        slot.OriginalHeight = vanillaInfo.Height;
                        slot.VanillaFormat = vanillaInfo.Format;
                        slot.VanillaAlphaDepth = vanillaInfo.AlphaDepth;
                        slot.VanillaAlphaType = vanillaInfo.AlphaType;
                        _logger.LogInformation(
                            "Patch: [{Phase}][{Idx}] Vanilla ref: {File} → {W}×{H} {Fmt} alphaDepth={AD}",
                            phase, slot.Index, vanillaInfo.Filename,
                            vanillaInfo.Width, vanillaInfo.Height,
                            vanillaInfo.Format, vanillaInfo.AlphaDepth);
                    }
                }
            }
        }
        else
        {
            // Heuristic path — classify from M2TextureParser output
            slots = parsedTextures.Select(t =>
            {
                // Session 22: Read vanilla BLP specs from rawblps
                var vanillaInfo = _vanillaBlps.GetBlpInfo(t.Filename);

                return new TextureSlotRequest
                {
                    Index = t.Index,
                    OriginalFilename = t.Filename,
                    OriginalFilenameLength = t.ActualByteLength,
                    OriginalWidth = vanillaInfo?.Width ?? 0,
                    OriginalHeight = vanillaInfo?.Height ?? 0,
                    VanillaFormat = vanillaInfo?.Format,
                    VanillaAlphaDepth = vanillaInfo?.AlphaDepth ?? 0,
                    VanillaAlphaType = vanillaInfo?.AlphaType ?? 0,
                    RoleOverride = null,
                    CustomPrompt = null,
                    UseOllamaRefinement = false,
                    BlendMode = blendModes.TryGetValue(t.Index, out byte bm) ? bm : (byte)4,
                    Phase = phase
                };
            }).ToList();

            slotBuildSource = "HEURISTIC";
            _logger.LogInformation("Patch: [{Phase}] No recipe — {Count} slots from heuristics",
                phase, slots.Count);
        }

        // Apply custom prompts if any
        if (customOverrides != null && customOverrides.TryGetValue(phase, out var overrides))
        {
            foreach (var ov in overrides)
            {
                var slot = slots.FirstOrDefault(s => s.Index == ov.Index);
                if (slot != null)
                {
                    slot.CustomPrompt = ov.CustomPrompt;
                    if (ov.RoleOverride != null && Enum.TryParse<SpellTextureService.TextureRole>(ov.RoleOverride, true, out var r))
                        slot.RoleOverride = r;
                }
            }
        }

        // ── Session 21: Snapshot the slots BEFORE sending to SpellTextureService ──
        var slotSnapshots = slots.Select(s => new
        {
            index = s.Index,
            originalFilename = s.OriginalFilename,
            originalFilenameLength = s.OriginalFilenameLength,
            phase = s.Phase,
            recipeRole = s.RecipeRole,
            recipeJob = s.RecipeJob,
            recipeDensity = s.RecipeDensity,
            recipeVignette = s.RecipeVignette,
            blendMode = (int)s.BlendMode,
            roleOverride = s.RoleOverride?.ToString(),
            customPrompt = s.CustomPrompt,
        }).ToList();

        var genRequest = new TextureGenerationRequest
        {
            SpellName = safeName,
            ThemeKey = themeKey,
            TextureSlots = slots
        };

        var result = await _textureService.GenerateTexturesAsync(genRequest);

        // Cache each generated BLP to disk + build manifest entries
        string texDir = Path.Combine(_env.WebRootPath, "images", "textures", "custom", safeName);
        Directory.CreateDirectory(texDir);

        var manifestEntries = new List<TextureCacheEntry>();

        // ── Session 20: Collect diagnostic slot data (kept for backward compat) ──
        var diagSlots = new List<object>();

        foreach (var tex in result.Textures)
        {
            // Self-documenting filename when recipe is present
            string blpFilename;
            if (usingRecipe)
                blpFilename = $"tex_{safeName}_{phase}_{tex.TextureIndex}_{tex.Role.ToString().ToLower()}.blp";
            else
                blpFilename = $"tex_{safeName}_{tex.TextureIndex}_{tex.Role.ToString().ToLower()}.blp";

            string blpPath = Path.Combine(texDir, blpFilename);
            await System.IO.File.WriteAllBytesAsync(blpPath, tex.BlpBytes);

            manifestEntries.Add(new TextureCacheEntry
            {
                Phase = phase,
                TextureIndex = tex.TextureIndex,
                BlpFilename = blpFilename,
                ReplacementMpqPath = tex.ReplacementMpqPath,
                OriginalFilename = tex.OriginalFilename,
                Role = tex.Role.ToString().ToLower(),
                Prompt = tex.Prompt
            });

            // ── Build diagnostic entry for this slot (Session 20 — kept) ──
            var slotReq = slots.FirstOrDefault(s => s.Index == tex.TextureIndex);
            var parsedTex = parsedTextures.FirstOrDefault(t => t.Index == tex.TextureIndex);

            object? vanillaBlpInfo = null;
            if (parsedTex != null && !string.IsNullOrEmpty(parsedTex.Filename))
            {
                var vanillaRef = _vanillaBlps.GetBlpInfo(parsedTex.Filename);
                if (vanillaRef != null)
                    vanillaBlpInfo = new
                    {
                        compression = vanillaRef.Format,
                        alphaDepth = (int)vanillaRef.AlphaDepth,
                        alphaType = (int)vanillaRef.AlphaType,
                        width = vanillaRef.Width,
                        height = vanillaRef.Height,
                        mipLevels = vanillaRef.MipCount,
                        fileSize = vanillaRef.FileSize
                    };
            }

            diagSlots.Add(new
            {
                index = tex.TextureIndex,
                vanillaFilename = tex.OriginalFilename,
                vanillaBlp = vanillaBlpInfo,
                customFilename = tex.ReplacementMpqPath,
                customBlp = ParseBlpHeader(tex.BlpBytes),
                recipe = usingRecipe ? new
                {
                    role = slotReq?.RecipeRole,
                    job = slotReq?.RecipeJob,
                    density = slotReq?.RecipeDensity,
                    blendMode = (int)(slotReq?.BlendMode ?? 4),
                    vignette = slotReq?.RecipeVignette,
                    vignetteInner = slotReq?.RecipeVignetteInner,
                    vignetteOuter = slotReq?.RecipeVignetteOuter,
                    gridSize = slotReq?.RecipeGridSize
                } : null,
                pipeline = new
                {
                    roleResolution = usingRecipe ? "recipe" : "heuristic",
                    roleResult = tex.Role.ToString(),
                    densityResolution = usingRecipe ? "recipe" : "heuristic",
                    densityResult = slotReq?.RecipeDensity
                        ?? SpellTextureService.ClassifyDensity(tex.OriginalFilename).ToString(),
                    blendMode = (int)(slotReq?.BlendMode ?? 4),
                    vignetteApplied = slotReq?.RecipeVignette ?? "auto",
                    vignetteInner = slotReq?.RecipeVignetteInner,
                    vignetteOuter = slotReq?.RecipeVignetteOuter,
                    prompt = tex.Prompt,
                    comfyuiSize = $"{tex.Width}x{tex.Height}"
                }
            });
        }

        // Add errors to diagnostics too
        var diagErrors = result.Errors.Count > 0 ? result.Errors : null;

        return new M2TextureResult
        {
            PhaseKey = phase,
            M2Path = m2Path,
            ManifestEntries = manifestEntries,
            DiagnosticData = new
            {
                sourceM2 = m2Path,
                recipeMatched = usingRecipe,
                emitterCount = recipePhase?.EmitterCount ?? 0,
                texturesParsed = parsedTextures.Count,
                texturesGenerated = result.Textures.Count,
                slots = diagSlots,
                errors = diagErrors
            },
            TraceData = new
            {
                // Controller-level context: what PatchController decided BEFORE
                // handing off to SpellTextureService
                controller = new
                {
                    dbcPhaseKey,
                    effectivePhaseName = phase,
                    sourceM2 = m2Path,
                    m2Filename = Path.GetFileName(m2Path),
                    recipePhaseFound = recipePhase != null,
                    recipePhaseSourceM2 = recipePhase?.SourceM2,
                    recipePhaseEffectRole = recipePhase?.EffectRole,
                    recipePhaseSlotCount = recipePhase?.Slots.Count ?? 0,
                    slotBuildSource,
                    parsedTextureCount,
                    recipeSlotCount,
                    slotsBuilt = slots.Count,
                },
                // The slots as they were when handed to SpellTextureService
                slotSnapshots,
                // The traces from inside SpellTextureService.GenerateSingleTextureAsync
                slotTraces = result.Traces,
                // Errors
                errors = result.Errors.Count > 0 ? result.Errors : null,
            }
        };
    }

    /// <summary>
    /// Parse the first bytes of a BLP2 file to extract header info for diagnostics.
    /// Returns null if data is too short or not BLP2.
    /// </summary>
    private static object? ParseBlpHeader(byte[]? data)
    {
        if (data == null || data.Length < 20) return null;
        if (data[0] != 'B' || data[1] != 'L' || data[2] != 'P' || data[3] != '2')
            return null;

        // BLP2 header: [4] type, [8] compression, [9] alpha_depth, [10] alpha_type,
        // [11] has_mips, [12..15] width, [16..19] height
        byte compression = data[8];
        byte alphaDepth = data[9];
        byte alphaType = data[10];
        byte hasMips = data[11];
        uint width = BitConverter.ToUInt32(data, 12);
        uint height = BitConverter.ToUInt32(data, 16);

        // Count actual mip levels (non-zero offsets)
        int mipLevels = 0;
        if (data.Length >= 84)
        {
            for (int i = 0; i < 16; i++)
            {
                uint mipOfs = BitConverter.ToUInt32(data, 20 + i * 4);
                if (mipOfs > 0) mipLevels++;
                else break;
            }
        }

        string compressionStr = compression switch
        {
            1 => "Palettized",
            2 => alphaType switch
            {
                0 => "DXT1",
                1 => "DXT3",
                7 => "DXT5",
                _ => $"DXT({alphaType})"
            },
            3 => "Uncompressed",
            _ => $"Unknown({compression})"
        };

        return new
        {
            compression = compressionStr,
            alphaDepth = (int)alphaDepth,
            alphaType = (int)alphaType,
            width = (int)width,
            height = (int)height,
            mipLevels,
            fileSize = data.Length
        };
    }

    /// <summary>
    /// Try to read a vanilla BLP file from the client data directory.
    /// Resolves paths like "SPELLS\\MOLTENROCK.BLP" → /home/wowvmangos/wowclient/m2/SPELLS/MOLTENROCK.BLP
    /// Returns null if not found (non-fatal).
    /// </summary>
    private byte[]? TryReadVanillaBlp(string vanillaPath)
    {
        try
        {
            var clientM2Path = _patchBuilder.GetType()
                .GetProperty("ClientM2Path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .GetValue(_patchBuilder)?.ToString()
                ?? "/home/wowvmangos/wowclient/m2";

            // BLP textures might be alongside M2s or in a textures subdir
            string normalized = vanillaPath.Replace('\\', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(clientM2Path, normalized);

            if (System.IO.File.Exists(fullPath))
                return System.IO.File.ReadAllBytes(fullPath);

            // Case-insensitive search
            string? dir = Path.GetDirectoryName(fullPath);
            string? file = Path.GetFileName(fullPath);
            if (dir != null && file != null && Directory.Exists(dir))
            {
                var match = Directory.GetFiles(dir)
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return System.IO.File.ReadAllBytes(match);
            }

            return null;
        }
        catch
        {
            return null; // non-fatal — diagnostic only
        }
    }

    /// <summary>
    /// Session 20: Write diagnostic JSON with the full decision chain for every texture slot.
    /// Output: /opt/mangossuperui/customjson/{SpellName}_texturemap.json
    /// Paste this into a Claude session to debug texture issues without re-reading M2s.
    /// </summary>
    private void WriteDiagnosticJson(
        string safeName, int sourceSpellEntry,
        SpellRecipe? recipe, Dictionary<string, object> diagnosticPhases)
    {
        try
        {
            string customJsonDir = "/opt/mangossuperui/customjson";
            Directory.CreateDirectory(customJsonDir);

            var diagnostic = new
            {
                spellName = safeName,
                sourceSpellEntry,
                recipeId = recipe?.RecipeId,
                recipeFound = recipe != null,
                generatedAt = DateTime.UtcNow.ToString("o"),
                phases = diagnosticPhases
            };

            string json = System.Text.Json.JsonSerializer.Serialize(diagnostic,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });

            string path = Path.Combine(customJsonDir, $"{safeName}_texturemap.json");
            System.IO.File.WriteAllText(path, json);

            _logger.LogInformation("Patch: Diagnostic texturemap written to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patch: Failed to write diagnostic JSON (non-fatal)");
        }
    }

    /// <summary>
    /// Session 21: Write runtime trace JSON with actual decision-point values.
    /// Unlike the diagnostic texturemap (which logs intentions), this captures
    /// what each code path ACTUALLY evaluated at runtime.
    /// Output: /opt/mangossuperui/customjson/{SpellName}_trace.json
    /// </summary>
    private void WriteTraceJson(
        string safeName, int sourceSpellEntry,
        SpellRecipe? recipe, Dictionary<string, object> tracePhases)
    {
        try
        {
            string customJsonDir = "/opt/mangossuperui/customjson";
            Directory.CreateDirectory(customJsonDir);

            var traceDoc = new
            {
                _header = "SESSION 21 RUNTIME TRACE — shows actual code decisions, not intentions",
                spellName = safeName,
                sourceSpellEntry,
                recipeId = recipe?.RecipeId,
                recipeFound = recipe != null,
                recipePhaseKeys = recipe?.Phases.Keys.ToList(),
                generatedAt = DateTime.UtcNow.ToString("o"),
                phases = tracePhases
            };

            string json = System.Text.Json.JsonSerializer.Serialize(traceDoc,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });

            string path = Path.Combine(customJsonDir, $"{safeName}_trace.json");
            System.IO.File.WriteAllText(path, json);

            _logger.LogInformation("Patch: Runtime trace written to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patch: Failed to write trace JSON (non-fatal)");
        }
    }

    /// <summary>Parse "#rrggbb" to 0x00RRGGBB. Returns null on invalid input.</summary>
    private static uint? ParseHexToRgb(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return null;
        if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            return null;
        return rgb;
    }

    /// <summary>
    /// Derive M2 particle start/mid/end ARGB colors from a hex string like "#ff6622".
    /// Start = full alpha + base color, Mid = ~80% alpha + darkened 30%, End = 0 alpha + darkened 60%.
    /// This mirrors the pattern used by the built-in presets (bright → darker → faded out).
    /// </summary>
    private static uint[]? ColorsFromHex(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return null;

        if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            return null;

        byte r = (byte)((rgb >> 16) & 0xFF);
        byte g = (byte)((rgb >> 8) & 0xFF);
        byte b = (byte)(rgb & 0xFF);

        // Start: full alpha, base color
        uint start = 0xFF000000 | rgb;

        // Mid: ~80% alpha (0xCC), darken each channel by 30%
        byte mr = (byte)(r * 0.70f);
        byte mg = (byte)(g * 0.70f);
        byte mb = (byte)(b * 0.70f);
        uint mid = 0xCC000000 | ((uint)mr << 16) | ((uint)mg << 8) | mb;

        // End: 0 alpha (fades out), darken each channel by 60%
        byte er = (byte)(r * 0.40f);
        byte eg = (byte)(g * 0.40f);
        byte eb = (byte)(b * 0.40f);
        uint end = 0x00000000 | ((uint)er << 16) | ((uint)eg << 8) | eb;

        return new uint[] { start, mid, end };
    }

    private static uint GetSchoolMask(int school)
    {
        return school switch
        {
            0 => 0x01,
            1 => 0x02,
            2 => 0x04,
            3 => 0x08,
            4 => 0x10,
            5 => 0x20,
            6 => 0x40,
            _ => 0x01
        };
    }

    /// <summary>
    /// Map spell school to a representative SpellIcon.dbc entry ID.
    /// These are existing vanilla icon IDs — used as a FALLBACK when:
    ///   - GenerateIcon=false on the request, or
    ///   - SpellIconService falls back to "existing" source (no ComfyUI), or
    ///   - BLP conversion fails for the AI-generated PNG.
    /// 
    /// When ComfyUI/FLUX produces a PNG, PatchBuilderService creates a NEW
    /// SpellIcon.dbc row pointing at "Interface\Icons\CustomSpell_<name>" and
    /// uses that ID instead. The fallback IDs below are only consulted when
    /// the custom path doesn't fire.
    ///
    /// To find more: grep SpellIcon.dbc for icon paths, each row is [ID, StringRef→path].
    /// </summary>
    private static uint GetSchoolIconId(int school)
    {
        return (uint)(school switch
        {
            0 => 260,   // Ability_Warrior_Sunder (physical)
            1 => 52,    // Spell_Holy_HolyBolt
            2 => 185,   // Spell_Fire_FlameBolt (default fire — Fireball uses this)
            3 => 153,   // Spell_Nature_Lightning
            4 => 160,   // Spell_Frost_FrostBolt02
            5 => 136,   // Spell_Shadow_ShadowBolt
            6 => 64,    // Spell_Arcane_StarFire
            _ => 1       // INV_Misc_QuestionMark
        });
    }
}

// ── Request DTOs ──

/// <summary>
/// Per-phase particle multipliers sent from the Visual Designer UI.
/// Each knob is a multiplier (1.0 = vanilla, 2.0 = double, etc.)
/// </summary>
public class PhaseKnobs
{
    public float EmissionRate { get; set; } = 1.0f;
    public float Scale { get; set; } = 1.0f;
    public float Speed { get; set; } = 1.0f;
    public float Lifespan { get; set; } = 1.0f;
    public float Area { get; set; } = 1.0f;

    /// <summary>Per-phase color override as "#rrggbb" hex string.
    /// When set, overrides the global color preset for this phase.
    /// Uses hue-shift mode to preserve per-emitter luminance/saturation.</summary>
    public string? Color { get; set; }

    /// <summary>Per-phase blend mode override (Session 14).
    /// 0=opaque, 1=mod, 2=alpha, 4=additive. Null = don't change.</summary>
    public int? BlendMode { get; set; }

    /// <summary>Per-phase texture theme override (Session 14).
    /// When set, overrides the global texture theme for this phase.
    /// Null = use global theme or no texture replacement.</summary>
    public string? TextureTheme { get; set; }

    /// <summary>Per-phase emitter type override (Session 15).
    /// 0=point, 1=sphere, 2=plane, 3=spline. Null = don't change.</summary>
    public int? EmitterType { get; set; }
}

/// <summary>Per-phase particle params from the Visual Designer.</summary>
public class PerPhaseParams
{
    public PhaseKnobs? Precast { get; set; }
    public PhaseKnobs? Cast { get; set; }
    public PhaseKnobs? Missile { get; set; }
    public PhaseKnobs? Impact { get; set; }
    public PhaseKnobs? State { get; set; }
    public PhaseKnobs? StateDone { get; set; }
    public PhaseKnobs? Channel { get; set; }
}

public class GeneratePatchRequest
{
    public string SpellName { get; set; } = "";
    public string? NameSubtext { get; set; }
    public string? Description { get; set; }
    public int SourceSpellEntry { get; set; }
    public string? SourceSpellName { get; set; }
    public int School { get; set; }
    public string? ColorPreset { get; set; }

    /// <summary>
    /// Hex color string (e.g. "#ff6622") when ColorPreset is "custom".
    /// Server derives start/mid/end ARGB particle colors from this.
    /// </summary>
    public string? CustomColor { get; set; }
    public float Intensity { get; set; } = 1.0f;
    public bool GenerateIcon { get; set; } = false;
    public int TeachToCharacterGuid { get; set; } = 0;

    // ── Session 32: Spell Properties ──

    /// <summary>Skill tab key (e.g. "mage_fire", "priest_shadow"). Determines spellbook tab via skill_line_ability.</summary>
    public string? SkillTabKey { get; set; }

    /// <summary>Minimum direct damage. Mapped to effectBasePoints1 = damageMin - 1.</summary>
    public int? DamageMin { get; set; }

    /// <summary>Maximum direct damage. effectDieSides1 = damageMax - damageMin.</summary>
    public int? DamageMax { get; set; }

    /// <summary>Mana cost override.</summary>
    public int? ManaCost { get; set; }

    /// <summary>Required caster level.</summary>
    public int? SpellLevel { get; set; }

    /// <summary>Max level the spell scales to (for effectRealPointsPerLevel).</summary>
    public int? MaxLevel { get; set; }

    /// <summary>Cast time DBC index. Common: 1=instant, 5=1.5s, 14=2.5s, 15=3.0s, 22=3.5s.</summary>
    public int? CastingTimeIndex { get; set; }

    /// <summary>Range DBC index. Common: 1=self, 2=melee(5yd), 4=30yd, 35=35yd, 6=40yd.</summary>
    public int? RangeIndex { get; set; }

    /// <summary>Spell power coefficient (0.0 to 1.0+). How much spell power scales damage.</summary>
    public float? SpellCoefficient { get; set; }

    /// <summary>Damage scaling per caster level above spellLevel.</summary>
    public float? DamagePerLevel { get; set; }

    /// <summary>Missile travel speed in yards/sec (e.g. 24 for Fireball).</summary>
    public float? MissileSpeed { get; set; }

    /// <summary>Cooldown in milliseconds.</summary>
    public int? Cooldown { get; set; }

    /// <summary>When true, auto-generate all ranks mirroring the source spell's rank progression.</summary>
    public bool GenerateAllRanks { get; set; } = false;

    /// <summary>Session 33: When true, copy trainer entries per-rank from the corresponding source rank during rank chain generation.</summary>
    public bool CopySourceTrainers { get; set; } = false;

    /// <summary>Per-rank overrides. Key is rank number (1-based). Only non-null fields override the auto-calculated values.</summary>
    public Dictionary<int, RankOverrideDto>? RankOverrides { get; set; }

    /// <summary>When true, use PhaseParams instead of flat Intensity for particle tuning.</summary>
    public bool UsePerPhaseParams { get; set; } = false;

    /// <summary>Per-phase particle multipliers from the Visual Designer. Null when UsePerPhaseParams=false.</summary>
    public PerPhaseParams? PhaseParams { get; set; }

    /// <summary>
    /// Absolute path to an existing custom icon PNG to reuse (from /Patch/CustomIcons).
    /// When set, skips AI icon generation and uses this PNG directly for BLP conversion.
    /// Allows spells in the same visual "family" to share an icon template.
    /// </summary>
    public string? ExistingIconPath { get; set; }

    /// <summary>Texture theme key for streamlined texture generation (e.g. "lightning", "void").
    /// When set, AI textures are generated for each phase's M2 files using theme-appropriate prompts.
    /// Null = no texture replacement (keep vanilla textures).</summary>
    public string? TextureTheme { get; set; }

    /// <summary>Per-phase blend mode override. Key is phase name, value is blend mode (0-4).
    /// 0=opaque, 1=mod, 2=alpha, 4=additive. Null = don't change.</summary>
    public Dictionary<string, int>? PhaseBlendModes { get; set; }

    /// <summary>Per-phase custom texture prompts (advanced mode).
    /// Key is phase name, value is list of per-texture-slot prompt overrides.
    /// When set, overrides the theme prompts for specific texture slots.</summary>
    public Dictionary<string, List<TextureSlotOverride>>? PhaseTextureOverrides { get; set; }
}

public class DeletePatchRequest
{
    public string? FileName { get; set; }
}

public class DeleteSpellRequest
{
    public int Entry { get; set; }
}

public class RegisterTrainerRequest
{
    public int SpellEntry { get; set; }
    public int TrainerEntry { get; set; }
    public int Cost { get; set; }
    public int ReqLevel { get; set; }
}

public class RegisterClassTrainerRequest
{
    public int SpellEntry { get; set; }
    public int TrainerClass { get; set; }
    public int Cost { get; set; }
    public int ReqLevel { get; set; }
    public string? SpellName { get; set; }
    public string? RankSubtext { get; set; }
}

public class CopySourceTrainersRequest
{
    public int SpellEntry { get; set; }
    public int SourceSpellEntry { get; set; }
    public int Cost { get; set; }
    public int ReqLevel { get; set; }
}

public class RankOverrideDto
{
    public int? DamageMin { get; set; }
    public int? DamageMax { get; set; }
    public int? ManaCost { get; set; }
    public int? SpellLevel { get; set; }
    public int? MaxLevel { get; set; }
    public int? CastingTimeIndex { get; set; }
    public float? SpellCoefficient { get; set; }
    public float? DamagePerLevel { get; set; }
    public int? TrainerCost { get; set; }
}

public class TeachSpellRequest
{
    public int SpellEntry { get; set; }
    public int CharacterGuid { get; set; }
}

public class GenerateIconRequest
{
    public string SpellName { get; set; } = "";
    public int School { get; set; }
    public string? Description { get; set; }
}

// ── Session 14: Texture Generation DTOs ──

/// <summary>Per-texture-slot override for advanced mode.</summary>
public class TextureSlotOverride
{
    public int Index { get; set; }
    public string? CustomPrompt { get; set; }
    public string? RoleOverride { get; set; }
    public bool UseOllamaRefinement { get; set; } = false;
}

/// <summary>Request to generate textures (standalone endpoint for preview/testing).</summary>
public class GenerateTexturesRequest
{
    public string SpellName { get; set; } = "";
    public string? ThemeKey { get; set; }
    public int SourceSpellEntry { get; set; }
    public List<GenerateTextureSlotInput> TextureSlots { get; set; } = new();
}

public class GenerateTextureSlotInput
{
    public int Index { get; set; }
    public string? OriginalFilename { get; set; }
    public int OriginalFilenameLength { get; set; }
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public string? RoleOverride { get; set; }
    public string? CustomPrompt { get; set; }
    public bool UseOllamaRefinement { get; set; } = false;
}

// ── Session 29: Reprocessing + Tuning DTOs ──

public class ReprocessTexturesRequest
{
    public int SpellEntry { get; set; }
    public string SpellName { get; set; } = "";
    public float? FloorOverride { get; set; }
    public float? KneeOverride { get; set; }
}

public class ApplyTuningRequest
{
    public int SpellEntry { get; set; }
    public string SpellName { get; set; } = "";
    public SpellTuningPreset Preset { get; set; } = null!;
}

// ── Session 15: Texture Cache DTOs ──

/// <summary>
/// Entry in the texture cache manifest (manifest.json per spell).
/// Stored on disk so RebuildUnifiedPatchFromConfigsAsync can reload
/// cached BLP textures without re-running ComfyUI/FLUX.
/// </summary>
public class TextureCacheEntry
{
    public string Phase { get; set; } = "";
    public int TextureIndex { get; set; }
    public string BlpFilename { get; set; } = "";
    public string ReplacementMpqPath { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public string Role { get; set; } = "";
    public string Prompt { get; set; } = "";
}