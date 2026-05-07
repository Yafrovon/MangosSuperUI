using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using Dapper;

namespace MangosSuperUI.Controllers;

/// <summary>
/// Session 30: Experiment Lab + Observation + Knowledge endpoints.
/// Partial class — lives alongside PatchController.cs.
///
/// INTEGRATION:
///   1. Add "partial" keyword to PatchController.cs:
///        public partial class PatchController : Controller
///
///   2. Add SpellDnaService to PatchController constructor:
///        - Field:  private readonly SpellDnaService _dna;
///        - Param:  SpellDnaService dna
///        - Body:   _dna = dna;
///
///   3. Program.cs:
///        builder.Services.AddSingleton<SpellDnaService>();
///
///   4. Drop this file into Controllers/ next to PatchController.cs.
/// </summary>
public partial class PatchController
{
    // ===================== EXPERIMENT ENGINE =====================

    /// <summary>
    /// POST /Patch/PlanExperiment — Preview an experiment batch.
    /// </summary>
    [HttpPost]
    public IActionResult PlanExperiment([FromBody] PlanExperimentRequest req)
    {
        if (req.SourceSpellEntry <= 0)
            return Json(new { success = false, error = "Source spell entry is required." });

        try
        {
            var phaseEmitters = ReadAllPhaseEmitters(req.SourceSpellEntry);
            if (phaseEmitters.Count == 0)
                return Json(new { success = false, error = "No emitters found in any phase." });

            string sourceName = req.SourceSpellName ?? $"Spell{req.SourceSpellEntry}";

            ExperimentBatch batch;
            if (string.Equals(req.Mode, "extreme", StringComparison.OrdinalIgnoreCase))
            {
                batch = _dna.PlanExtremeExperiments(
                    phaseEmitters, req.SourceSpellEntry, sourceName, req.TargetEmitters);
            }
            else
            {
                batch = _dna.PlanExperiments(
                    phaseEmitters, req.SourceSpellEntry, sourceName,
                    req.BatchSize > 0 ? req.BatchSize : 20, req.TargetEmitters);
            }

            return Json(new
            {
                success = true,
                sourceEntry = req.SourceSpellEntry,
                sourceName,
                mode = req.Mode ?? "standard",
                phasesFound = phaseEmitters.Keys.ToList(),
                phaseEmitterCounts = phaseEmitters.ToDictionary(kv => kv.Key, kv => kv.Value.Count),
                totalPlans = batch.Plans.Count,
                totalObservations = batch.TotalObservations,
                targetedEmitter = batch.TargetedEmitterIndex,
                emitterReason = batch.EmitterReason,
                plans = batch.Plans.Select(p => new
                {
                    p.CloneIndex,
                    p.SpellName,
                    p.SpellDescription,
                    changes = p.PhaseChanges.Select(kv => new
                    {
                        phase = kv.Key,
                        kv.Value.EmitterIndex,
                        kv.Value.Parameter,
                        baseline = kv.Value.BaselineValue,
                        newVal = kv.Value.NewValue,
                        kv.Value.Mode,
                        desc = kv.Value.ParameterDescription,
                    }).ToList()
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: PlanExperiment failed for #{Entry}", req.SourceSpellEntry);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Patch/RunExperiment — Execute: clone N spells, patch M2s, rebuild patch.
    /// All clones cost 1 mana so you can spam them without drinking.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RunExperiment([FromBody] RunExperimentRequest req)
    {
        if (req.SourceSpellEntry <= 0)
            return Json(new { success = false, error = "Source spell entry is required." });

        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        try
        {
            var phaseEmitters = new Dictionary<string, List<EmitterSnapshot>>();
            var phaseM2Paths = new Dictionary<string, string>();

            foreach (var phase in new[] { "precast", "cast", "missile", "impact" })
            {
                string? m2Path = FindM2PathForPhase(req.SourceSpellEntry, phase);
                if (m2Path == null) continue;
                byte[]? m2Data = ReadM2FromClient(m2Path);
                if (m2Data == null) continue;
                var emitters = M2EmitterParser.ReadEmitters(m2Data);
                if (emitters.Count > 0)
                {
                    phaseEmitters[phase] = emitters;
                    phaseM2Paths[phase] = m2Path;
                }
            }

            if (phaseEmitters.Count == 0)
                return Json(new { success = false, error = "No emitters found in any phase." });

            string sourceName = req.SourceSpellName ?? $"Spell{req.SourceSpellEntry}";

            ExperimentBatch batch;
            if (string.Equals(req.Mode, "extreme", StringComparison.OrdinalIgnoreCase))
            {
                batch = _dna.PlanExtremeExperiments(
                    phaseEmitters, req.SourceSpellEntry, sourceName, req.TargetEmitters);
            }
            else
            {
                batch = _dna.PlanExperiments(
                    phaseEmitters, req.SourceSpellEntry, sourceName,
                    req.BatchSize > 0 ? req.BatchSize : 20, req.TargetEmitters);
            }

            if (batch.Plans.Count == 0)
                return Json(new { success = false, error = "No experiment plans generated." });

            var results = new List<object>();
            var createdEntries = new List<int>();

            foreach (var plan in batch.Plans)
            {
                try
                {
                    int newEntry = await _spellCreator.CloneSpellAsync(
                        req.SourceSpellEntry,
                        new Dictionary<string, object?>
                        {
                            ["name"] = plan.SpellName,
                            ["nameSubtext"] = "Experiment",
                            ["description"] = plan.SpellDescription,
                            ["manaCost"] = 1,
                        }, ip);

                    if (newEntry < 0)
                    {
                        results.Add(new { plan.CloneIndex, success = false, error = "Clone failed" });
                        continue;
                    }
                    createdEntries.Add(newEntry);

                    await _spellConfig.SaveConfigAsync(new SpellVisualConfig
                    {
                        Entry = newEntry,
                        SourceEntry = req.SourceSpellEntry,
                        SpellName = plan.SpellName,
                        NameSubtext = "Experiment",
                        Description = plan.SpellDescription,
                        ColorPreset = "default",
                    });

                    string safeName = new string(plan.SpellName
                        .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                    string texDir = Path.Combine(_env.WebRootPath,
                        "images", "textures", "custom", safeName);
                    Directory.CreateDirectory(texDir);

                    foreach (var (phase, change) in plan.PhaseChanges)
                    {
                        if (!phaseM2Paths.TryGetValue(phase, out string? m2Path)) continue;
                        byte[]? m2Data = ReadM2FromClient(m2Path);
                        if (m2Data == null) continue;

                        byte[] patched = (byte[])m2Data.Clone();
                        bool ok;

                        if (change.Parameter == "scaleStart")
                        {
                            var em = phaseEmitters[phase].First(e => e.Index == change.EmitterIndex);
                            float ratio = em.ScaleStart > 0 ? change.NewValue / em.ScaleStart : 1f;
                            ok = M2EmitterParser.PatchScaleValues(patched, change.EmitterIndex,
                                change.NewValue, em.ScaleMid * ratio, em.ScaleEnd * ratio);
                        }
                        else
                        {
                            ok = M2EmitterParser.PatchTrackValue(patched, change.EmitterIndex,
                                change.Parameter, change.NewValue);
                        }

                        if (ok)
                        {
                            string cachePath = Path.Combine(texDir,
                                $"m2_patched_{phase}_{Path.GetFileName(m2Path)}");
                            await System.IO.File.WriteAllBytesAsync(cachePath, patched);
                        }
                    }

                    if (req.TeachToCharacterGuid > 0)
                        await _spellCreator.TeachSpellToCharacterAsync(newEntry, req.TeachToCharacterGuid, ip);

                    results.Add(new
                    {
                        plan.CloneIndex,
                        success = true,
                        spellEntry = newEntry,
                        spellName = plan.SpellName,
                        description = plan.SpellDescription,
                        changes = plan.PhaseChanges.Select(kv => new
                        {
                            phase = kv.Key,
                            kv.Value.EmitterIndex,
                            kv.Value.Parameter,
                            baseline = kv.Value.BaselineValue,
                            newVal = kv.Value.NewValue,
                            kv.Value.Mode,
                            desc = kv.Value.ParameterDescription,
                        }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Patch: Experiment clone #{Idx} failed", plan.CloneIndex);
                    results.Add(new { plan.CloneIndex, success = false, error = ex.Message });
                }
            }

            var unifiedResult = await RebuildUnifiedPatchFromConfigsAsync();

            if (unifiedResult?.Success == true)
            {
                using var connUpdate = _db.Mangos();
                foreach (int entry in createdEntries)
                {
                    if (unifiedResult.VisualIdMap.TryGetValue(entry, out uint vid))
                    {
                        try
                        {
                            await connUpdate.ExecuteAsync(
                                @"UPDATE spell_template SET spellVisual1 = @Visual
                                  WHERE entry = @Entry
                                    AND build = (SELECT MAX(b) FROM
                                      (SELECT build AS b FROM spell_template WHERE entry = @Entry) t)",
                                new { Visual = vid, Entry = entry });
                        }
                        catch { /* non-fatal */ }
                    }
                }
            }

            return Json(new
            {
                success = true,
                sourceName,
                sourceEntry = req.SourceSpellEntry,
                clonesCreated = createdEntries.Count,
                totalPlanned = batch.Plans.Count,
                totalObservations = batch.TotalObservations,
                patchRebuilt = unifiedResult?.Success ?? false,
                patchFileName = unifiedResult?.PatchFileName,
                results,
                note = "Server restart required. Copy patch to client, clear WDB, relog."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: RunExperiment failed for #{Entry}", req.SourceSpellEntry);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Patch/CleanupExperiment — Delete all experiment clones for a source spell.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CleanupExperiment([FromBody] CleanupExperimentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SourceSpellName))
            return Json(new { success = false, error = "Source spell name is required." });

        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        int deleted = 0;

        try
        {
            using var conn = _db.Mangos();
            var experiments = (await conn.QueryAsync<dynamic>(
                @"SELECT entry, name FROM spell_template
                  WHERE (name LIKE @PatternE OR name LIKE @PatternX) AND entry >= @Base AND entry <= @Max",
                new
                {
                    PatternE = $"{req.SourceSpellName} E%",
                    PatternX = $"{req.SourceSpellName} X%",
                    Base = SpellCreatorService.CUSTOM_SPELL_BASE,
                    Max = SpellCreatorService.CUSTOM_SPELL_MAX
                })).ToList();

            foreach (var exp in experiments)
            {
                int entry = (int)exp.entry;
                await _spellCreator.DeleteCustomSpellAsync(entry, ip);
                await _spellConfig.DeleteConfigAsync(entry);

                string safeName = new string(((string)exp.name)
                    .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                string texDir = Path.Combine(_env.WebRootPath,
                    "images", "textures", "custom", safeName);
                if (Directory.Exists(texDir))
                    try { Directory.Delete(texDir, true); } catch { }
                deleted++;
            }

            var unifiedResult = await RebuildUnifiedPatchFromConfigsAsync();

            return Json(new
            {
                success = true,
                deleted,
                patchRebuilt = unifiedResult?.Success ?? false,
                note = "Server restart required."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: CleanupExperiment failed");
            return Json(new { success = false, error = ex.Message, deletedBeforeError = deleted });
        }
    }

    // ===================== OBSERVATIONS =====================

    /// <summary>
    /// POST /Patch/SaveObservations — Save observation notes for an experiment batch.
    /// </summary>
    [HttpPost]
    public IActionResult SaveObservations([FromBody] SaveObservationsRequest req)
    {
        if (req.Observations == null || req.Observations.Count == 0)
            return Json(new { success = false, error = "No observations to save." });

        try
        {
            int saved = 0;
            foreach (var obs in req.Observations)
            {
                if (obs.Phases == null || obs.Phases.Count == 0) continue;
                obs.SourceSpellEntry = req.SourceSpellEntry;
                obs.SourceSpellName = req.SourceSpellName ?? "";
                _dna.RecordObservation(obs);
                saved++;
            }

            return Json(new { success = true, saved });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: SaveObservations failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// GET /Patch/ExperimentLog — Get all recorded observations.
    /// </summary>
    [HttpGet]
    public IActionResult ExperimentLog()
    {
        var observations = _dna.GetObservations();
        return Json(new { success = true, count = observations.Count, observations });
    }

    /// <summary>
    /// GET /Patch/ExportExperiment?sourceName=Fireball — Export as structured text for Claude.
    /// </summary>
    [HttpGet]
    public IActionResult ExportExperiment(string sourceName)
    {
        var allObs = _dna.GetObservations();
        var filtered = allObs
            .Where(o => string.Equals(o.SourceSpellName, sourceName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.CloneIndex)
            .ToList();

        if (filtered.Count == 0)
            return Json(new { success = false, error = $"No observations found for '{sourceName}'." });

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Experiment Results: {sourceName} (source #{filtered[0].SourceSpellEntry})");
        sb.AppendLine($"# Observations: {filtered.Count}");
        sb.AppendLine($"# Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        foreach (var obs in filtered)
        {
            sb.AppendLine($"## E{obs.CloneIndex:D2} (#{obs.CloneSpellEntry})");
            foreach (var (phase, po) in obs.Phases)
            {
                string impact = po.ImpactRating switch
                {
                    0 => "NONE",
                    1 => "SUBTLE",
                    2 => "NOTICEABLE",
                    3 => "DRAMATIC",
                    _ => "?"
                };
                sb.AppendLine($"  {phase}/em?: {po.Parameter} {po.BaselineValue:F2}→{po.NewValue:F2} | impact={impact} | {po.Observation ?? "(no notes)"}");
            }
            if (!string.IsNullOrEmpty(obs.OverallNotes))
                sb.AppendLine($"  OVERALL: {obs.OverallNotes}");
            sb.AppendLine();
        }

        return Json(new { success = true, text = sb.ToString(), count = filtered.Count });
    }

    // ===================== KNOWLEDGE =====================

    /// <summary>GET /Patch/ExperimentSummary?spellName=Fireball — Get experiment summary for a spell.</summary>
    [HttpGet]
    public IActionResult ExperimentSummary(string spellName)
    {
        var summary = _dna.GetSummary(spellName);
        if (summary == null)
            return Json(new { success = true, exists = false });
        return Json(new
        {
            success = true,
            exists = true,
            summary
        });
    }

    /// <summary>POST /Patch/SaveExperimentSummary — Save/update experiment summary.</summary>
    [HttpPost]
    public IActionResult SaveExperimentSummary([FromBody] ExperimentSummary summary)
    {
        if (string.IsNullOrWhiteSpace(summary.SpellName))
            return Json(new { success = false, error = "Spell name is required." });
        try
        {
            _dna.SaveSummary(summary);
            return Json(new { success = true, spellName = summary.SpellName, batches = summary.Batches.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: SaveExperimentSummary failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>GET /Patch/ExperimentSummaries — List all spells with experiment summaries.</summary>
    [HttpGet]
    public IActionResult ExperimentSummaries()
    {
        return Json(new { success = true, spells = _dna.ListSummaries() });
    }

    /// <summary>GET /Patch/Archetypes — List all emitter behavior archetypes.</summary>
    [HttpGet]
    public IActionResult Archetypes()
    {
        return Json(new { success = true, archetypes = _dna.GetArchetypes().Values });
    }

    /// <summary>GET /Patch/ProfileM2 — Build an M2 profile with archetype classifications.</summary>
    [HttpGet]
    public IActionResult ProfileM2(int entry, string phase)
    {
        try
        {
            string? m2Path = FindM2PathForPhase(entry, phase);
            if (m2Path == null)
                return Json(new { success = false, error = $"No M2 found for {phase} phase." });

            byte[]? m2Data = ReadM2FromClient(m2Path);
            if (m2Data == null)
                return Json(new { success = false, error = "Could not read M2 file." });

            var profile = _dna.BuildProfile(m2Data, m2Path, m2Path);

            return Json(new
            {
                success = true,
                m2Path,
                phase,
                profile.EmitterCount,
                profile.TextureCount,
                emitters = profile.Emitters.Select(e => new
                {
                    e.Index,
                    e.TextureId,
                    e.BlendMode,
                    e.EmitterType,
                    e.EmissionRate,
                    e.Lifespan,
                    e.EmissionSpeed,
                    e.Gravity,
                    e.EmissionAreaLength,
                    e.EmissionAreaWidth,
                    e.ScaleStart,
                    e.ScaleMid,
                    e.ScaleEnd,
                    e.ArchetypeId,
                    e.ArchetypeScore,
                }),
                textures = profile.Textures.Select(t => new
                {
                    t.Index,
                    t.VanillaFilename,
                    t.ReferencedByEmitters,
                }),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: ProfileM2 failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Patch/AppendBatchSummary — Append a single batch (from Claude quantization) 
    /// to an existing spell summary. Creates the summary if it doesn't exist.
    /// Accepts: { spellName, sourceEntry, batch: { ... }, emittersRemaining?, nextBatchSuggestion? }
    /// </summary>
    [HttpPost]
    public IActionResult AppendBatchSummary([FromBody] AppendBatchRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SpellName))
            return Json(new { success = false, error = "spellName is required." });
        if (req.Batch == null)
            return Json(new { success = false, error = "batch object is required." });

        try
        {
            var summary = _dna.GetSummary(req.SpellName);
            if (summary == null)
            {
                summary = new ExperimentSummary
                {
                    SpellName = req.SpellName,
                    SourceEntry = req.SourceEntry,
                    Batches = new List<BatchSummary>(),
                    EmittersRemaining = new List<int>(),
                };
            }

            // Auto-assign batchId as max existing + 1
            int nextId = summary.Batches.Count > 0
                ? summary.Batches.Max(b => b.BatchId) + 1
                : 1;
            req.Batch.BatchId = nextId;

            // Default date to today if not provided
            if (string.IsNullOrWhiteSpace(req.Batch.Date))
                req.Batch.Date = DateTime.UtcNow.ToString("yyyy-MM-dd");

            // Auto-populate phasesFound from the spell's actual phases if empty
            if ((req.Batch.PhasesFound == null || req.Batch.PhasesFound.Count == 0) && req.SourceEntry > 0)
            {
                var phaseEmitters = ReadAllPhaseEmitters(req.SourceEntry);
                req.Batch.PhasesFound = phaseEmitters.Keys.OrderBy(k => k).ToList();
            }

            // Auto-populate parametersCovered from findings if empty
            if ((req.Batch.ParametersCovered == null || req.Batch.ParametersCovered.Count == 0)
                && req.Batch.Findings?.PerParameter != null)
            {
                req.Batch.ParametersCovered = req.Batch.Findings.PerParameter.Keys.ToList();
            }

            // Auto-build TestedCombos from findings if not provided
            // This ensures PlanExtremeExperiments knows what was covered
            if ((req.Batch.TestedCombos == null || req.Batch.TestedCombos.Count == 0)
                && req.Batch.Findings?.PerParameter != null
                && req.Batch.PhasesFound?.Count > 0)
            {
                // Read real emitter baselines so TestedCombos match correctly
                var phaseEmitters = req.SourceEntry > 0
                    ? ReadAllPhaseEmitters(req.SourceEntry)
                    : new Dictionary<string, List<EmitterSnapshot>>();

                req.Batch.TestedCombos = new List<TestedCombo>();
                foreach (var paramName in req.Batch.Findings.PerParameter.Keys)
                {
                    foreach (var phase in req.Batch.PhasesFound)
                    {
                        float baseline = 0f;
                        if (phaseEmitters.TryGetValue(phase, out var emitters))
                        {
                            var em = emitters.FirstOrDefault(e => e.Index == req.Batch.EmitterTargeted);
                            if (em != null)
                            {
                                baseline = paramName == "scaleStart"
                                    ? em.ScaleStart
                                    : em.TrackValues.GetValueOrDefault(paramName) ?? 0f;
                            }
                        }

                        req.Batch.TestedCombos.Add(new TestedCombo
                        {
                            Phase = phase,
                            EmitterIndex = req.Batch.EmitterTargeted,
                            Parameter = paramName,
                            Baseline = baseline
                        });
                    }
                }
            }

            summary.Batches.Add(req.Batch);

            // Update top-level fields if provided
            if (req.EmittersRemaining != null)
                summary.EmittersRemaining = req.EmittersRemaining;
            if (!string.IsNullOrWhiteSpace(req.NextBatchSuggestion))
                summary.NextBatchSuggestion = req.NextBatchSuggestion;

            _dna.SaveSummary(summary);

            return Json(new
            {
                success = true,
                spellName = summary.SpellName,
                batchId = nextId,
                totalBatches = summary.Batches.Count,
                emittersRemaining = summary.EmittersRemaining,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patch: AppendBatchSummary failed for {Spell}", req.SpellName);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ── Helper shared with main PatchController ──

    private Dictionary<string, List<EmitterSnapshot>> ReadAllPhaseEmitters(int sourceSpellEntry)
    {
        var result = new Dictionary<string, List<EmitterSnapshot>>();
        foreach (var phase in new[] { "precast", "cast", "missile", "impact" })
        {
            string? m2Path = FindM2PathForPhase(sourceSpellEntry, phase);
            if (m2Path == null) continue;
            byte[]? m2Data = ReadM2FromClient(m2Path);
            if (m2Data == null) continue;
            var emitters = M2EmitterParser.ReadEmitters(m2Data);
            if (emitters.Count > 0)
                result[phase] = emitters;
        }
        return result;
    }

    /// <summary>
    /// Session 30: Load pre-patched M2 files from the texture cache directory.
    /// Written by ApplySpellTuning and RunExperiment as m2_patched_{phase}_{filename}.
    /// Returns phase → M2 bytes, or null if no patched M2s exist.
    /// 
    /// NOTE: This is called by RebuildUnifiedPatchFromConfigsAsync in the main
    /// PatchController. Since this is a partial class, it has access to _env.
    /// Add this call to the SpellPatchRequest construction in the main file:
    ///     PerPhasePatchedM2s = LoadPatchedM2s(config.Entry, config.SpellName),
    /// </summary>
    internal Dictionary<string, byte[]>? LoadPatchedM2s(int entry, string spellName)
    {
        string safeName = new string(spellName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        string texDir = Path.Combine(_env.WebRootPath, "images", "textures", "custom", safeName);

        if (!Directory.Exists(texDir)) return null;

        var m2Files = Directory.GetFiles(texDir, "m2_patched_*");
        if (m2Files.Length == 0) return null;

        var result = new Dictionary<string, byte[]>();

        foreach (var filePath in m2Files)
        {
            string fileName = Path.GetFileName(filePath);
            // Parse: m2_patched_{phase}_{m2filename}
            string remainder = fileName.Substring("m2_patched_".Length);
            int firstUnderscore = remainder.IndexOf('_');
            if (firstUnderscore <= 0) continue;

            string phase = remainder.Substring(0, firstUnderscore);

            try
            {
                byte[] m2Data = System.IO.File.ReadAllBytes(filePath);
                if (m2Data.Length > 0)
                {
                    result[phase] = m2Data;
                    _logger.LogInformation("Patch: Loaded patched M2 for #{Entry} {Phase} ({Bytes} bytes)",
                        entry, phase, m2Data.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Patch: Failed to load patched M2 {File}", fileName);
            }
        }

        return result.Count > 0 ? result : null;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// REQUEST DTOs
// ═══════════════════════════════════════════════════════════════════════

public class PlanExperimentRequest
{
    public int SourceSpellEntry { get; set; }
    public string? SourceSpellName { get; set; }
    public int BatchSize { get; set; } = 20;
    public string? Mode { get; set; }
    public Dictionary<string, int>? TargetEmitters { get; set; }
}

public class RunExperimentRequest
{
    public int SourceSpellEntry { get; set; }
    public string? SourceSpellName { get; set; }
    public int BatchSize { get; set; } = 20;
    public string? Mode { get; set; }
    public int TeachToCharacterGuid { get; set; }
    public Dictionary<string, int>? TargetEmitters { get; set; }
}

public class CleanupExperimentRequest
{
    public string SourceSpellName { get; set; } = "";
}

public class SaveObservationsRequest
{
    public int SourceSpellEntry { get; set; }
    public string? SourceSpellName { get; set; }
    public List<ExperimentObservation> Observations { get; set; } = new();
}

public class AppendBatchRequest
{
    public string SpellName { get; set; } = "";
    public int SourceEntry { get; set; }
    public BatchSummary Batch { get; set; } = new();
    public List<int>? EmittersRemaining { get; set; }
    public string? NextBatchSuggestion { get; set; }
}