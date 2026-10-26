using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;

namespace MangosSuperUI.Controllers;

/// <summary>
/// Spell Completer — the DATA phase of the two-app custom-spell workflow.
///
/// The DESIGN phase lives in MSUIClient's creator mode: spells are tuned there
/// and exported into a session file (spell-session.json) where each entry
/// carries the complete design — tuning metadata plus the patched M2 bytes and
/// recolored BLPs, base64-embedded. This page uploads that session, and for
/// each spell the user supplies what the design phase cannot know: the real
/// name, class/skill tab, damage, mana, levels, ranks. Completing a spell:
///
///   1. clones the source into spell_template (40000+ range) with the overrides,
///   2. wires skill_line_ability + spell_chain (and optional rank chain),
///   3. persists the design bytes via CompleterStore (per-path patched M2s and
///      verbatim files) under the spell's texture-cache directory,
///   4. saves the config row in custom_spell_meta so EVERY unified patch
///      rebuild reproduces the spell,
///
/// after which the client chains POST /Patch/RebuildClientPatch to produce
/// patch-3.MPQ with the full visual+data package.
/// </summary>
public class SpellCompleterController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly SpellCreatorService _spellCreator;
    private readonly SpellConfigService _spellConfig;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<SpellCompleterController> _logger;

    private readonly RaService _ra;

    public SpellCompleterController(
        ConnectionFactory db,
        SpellCreatorService spellCreator,
        SpellConfigService spellConfig,
        DbcService dbc,
        AuditService audit,
        IWebHostEnvironment env,
        IConfiguration config,
        RaService ra,
        ILogger<SpellCompleterController> logger)
    {
        _db = db;
        _spellCreator = spellCreator;
        _spellConfig = spellConfig;
        _dbc = dbc;
        _audit = audit;
        _env = env;
        _config = config;
        _ra = ra;
        _logger = logger;
    }

    public IActionResult Index() => View();

    // ═══════════════════════════════════════════════════════════════════
    // SOURCE PREFILL — everything the inherited spell already knows
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The source spell's full gameplay profile, so the form starts
    /// from the inherited values instead of blank: damage, mana, levels, cast
    /// time, range, duration, cooldown, all three effect slots (type, aura,
    /// points, tick, misc), icon, description, rank count.</summary>
    [HttpGet]
    public async Task<IActionResult> SourceInfo(int entry)
    {
        if (entry <= 0) return Json(new { success = false, error = "bad entry" });
        try
        {
            using var conn = _db.Mangos();
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                new { E = entry });
            if (row is null)
                return Json(new { success = false, error = $"spell_template has no #{entry}" });
            var d = (IDictionary<string, object>)row;

            long V(string col) => d.TryGetValue(col, out var v) && v != null ? Convert.ToInt64(v) : 0;
            string S(string col) => d.TryGetValue(col, out var v) ? v?.ToString() ?? "" : "";

            // Rank count of the source chain (how many ranks "Generate all ranks" makes)
            int rankCount = 1;
            try
            {
                var first = await conn.ExecuteScalarAsync<int?>(
                    "SELECT first_spell FROM spell_chain WHERE spell_id = @E LIMIT 1", new { E = entry });
                if (first is > 0)
                    rankCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM spell_chain WHERE first_spell = @F", new { F = first.Value });
            }
            catch { /* chain table empty for unranked spells */ }

            // Icon id: SQL first, client DBC as fallback (spell_template mirrors it)
            long iconId = V("spellIconId");
            if (iconId == 0 && _dbc.SpellEntries.TryGetValue((uint)entry, out var dbcRow))
                iconId = dbcRow.SpellIconId;

            var effects = new List<object>();
            for (int i = 1; i <= 3; i++)
                effects.Add(new
                {
                    slot = i,
                    effect = V($"effect{i}"),
                    aura = V($"effectApplyAuraName{i}"),
                    basePoints = V($"effectBasePoints{i}"),
                    dieSides = V($"effectDieSides{i}"),
                    amplitude = V($"effectAmplitude{i}"),
                    miscValue = V($"effectMiscValue{i}"),
                });

            return Json(new
            {
                success = true,
                entry,
                name = S("name"),
                nameSubtext = S("nameSubtext"),
                description = S("description"),
                school = V("school"),
                spellIconId = iconId,
                manaCost = V("manaCost"),
                spellLevel = V("spellLevel"),
                baseLevel = V("baseLevel"),
                maxLevel = V("maxLevel"),
                castingTimeIndex = V("castingTimeIndex"),
                rangeIndex = V("rangeIndex"),
                durationIndex = V("durationIndex"),
                speed = d.TryGetValue("speed", out var sp) && sp != null ? Convert.ToSingle(sp) : 0f,
                cooldown = V("recoveryTime"),
                rankCount,
                effects,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completer: SourceInfo({Entry}) failed", entry);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // TRAINER DIAGNOSTIC — the whole chain a rank needs to reach a trainer
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Why is (or isn't) this custom spell at trainers? For each rank:
    /// its learn-spell wrapper (50000+, effect 36) and every npc_trainer /
    /// npc_trainer_template row pointing at that wrapper. If rows exist here
    /// but nothing shows in game, the world server hasn't been restarted —
    /// these tables load once at mangosd startup.</summary>
    [HttpGet]
    public async Task<IActionResult> TrainerStatus(int entry)
    {
        try
        {
            using var conn = _db.Mangos();
            var ranks = (await conn.QueryAsync<(int rank, int spell_id)>(
                @"SELECT rank, spell_id FROM spell_chain WHERE first_spell = @E ORDER BY rank",
                new { E = entry })).ToList();
            if (ranks.Count == 0) ranks.Add((1, entry));

            var report = new List<object>();
            var allWrappers = new List<int>();
            foreach (var (rank, spellId) in ranks)
            {
                var wrappers = (await conn.QueryAsync<int>(
                    @"SELECT entry FROM spell_template WHERE effect1 = 36 AND effectTriggerSpell1 = @S",
                    new { S = spellId })).ToList();
                allWrappers.AddRange(wrappers);
                var search = new List<int>(wrappers) { spellId };
                var direct = (await conn.QueryAsync<(int entry, int spell, int reqlevel)>(
                    @"SELECT entry, spell, reqlevel FROM npc_trainer WHERE spell IN @S",
                    new { S = search })).ToList();
                var template = (await conn.QueryAsync<(int entry, int spell, int reqlevel)>(
                    @"SELECT entry, spell, reqlevel FROM npc_trainer_template WHERE spell IN @S",
                    new { S = search })).ToList();
                report.Add(new
                {
                    rank,
                    spellId,
                    wrappers,
                    npcTrainerRows = direct.Select(t => new { trainerNpc = t.entry, t.spell, t.reqlevel }),
                    trainerTemplateRows = template.Select(t => new { templateId = t.entry, t.spell, t.reqlevel }),
                    atTrainers = direct.Count + template.Count > 0,
                });
            }
            // Ground truth, not inference: open the BUILT patch-3.MPQ and check
            // whether each wrapper actually has a Spell.dbc row in it. The client
            // silently hides trainer entries whose wrapper spell is missing from
            // its Spell.dbc, so this is the decisive client-side check.
            string? patchBuiltAtUtc = null;
            var wrappersInPatch = new Dictionary<uint, bool>();
            string patchFile = Path.Combine(
                _config["Vmangos:PatchOutputPath"] ?? Path.Combine(_env.WebRootPath, "patches"),
                "patch-3.MPQ");
            if (!System.IO.File.Exists(patchFile))
                patchFile = Path.Combine(_env.WebRootPath, "patches", "patch-3.MPQ");
            if (System.IO.File.Exists(patchFile))
            {
                patchBuiltAtUtc = System.IO.File.GetLastWriteTimeUtc(patchFile).ToString("o");
                try
                {
                    using var archive = Services.Mpq.MpqArchive.Open(patchFile);
                    byte[]? spellDbcBytes = archive?.ReadFile(@"DBFilesClient\Spell.dbc");
                    if (spellDbcBytes is not null)
                    {
                        var spellDbc = DbcWriterService.ReadDbc(spellDbcBytes, "patch-3 Spell.dbc");
                        foreach (int w in allWrappers)
                            wrappersInPatch[(uint)w] = spellDbc.GetRow((uint)w) != null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Completer: could not inspect {Patch}", patchFile);
                }
            }

            return Json(new
            {
                success = true,
                entry,
                ranks = report,
                patchBuiltAtUtc,
                patchFileChecked = patchFile,
                // wrapper spell id -> is its Spell.dbc row inside the built patch?
                wrappersInBuiltPatch = wrappersInPatch.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                note = "atTrainers=true + wrappersInBuiltPatch=true for every rank means BOTH sides are " +
                       "correct here — remaining causes are deployment order: the patch actually installed " +
                       "in WoW/Data is an older copy, or mangosd was last restarted before these " +
                       "wrapper/trainer rows were created. wrappersInBuiltPatch=false means the rebuild " +
                       "predates the wrappers — rebuild again and reinstall. No wrapper/rows at all = " +
                       "complete the spell again with 'Copy source trainers' checked.",
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>Interrogate the RUNNING world server: does its in-memory data
    /// include this spell (i.e. was mangosd restarted after creation), and do
    /// its log files complain about the trainer rows? Complements TrainerStatus
    /// (SQL + patch file), closing the last unverifiable link in the chain.</summary>
    [HttpGet]
    public async Task<IActionResult> ServerStatus(string name, int? wrapperFrom = null, int? wrapperTo = null)
    {
        string? lookupResult = null;
        string? lookupError = null;
        try
        {
            // "lookup spell X" answers from mangosd's LIVE spell store, not SQL.
            lookupResult = await _ra.SendCommandAsync($"lookup spell {name}");
        }
        catch (Exception ex)
        {
            lookupError = ex.Message;
        }

        // Grep the server logs for trainer-load complaints and wrapper mentions.
        var logHits = new List<string>();
        string? logsDir = _config["Vmangos:LogsDir"];
        if (!string.IsNullOrEmpty(logsDir) && Directory.Exists(logsDir))
        {
            int from = wrapperFrom ?? 50000, to = wrapperTo ?? 65000;
            foreach (string file in Directory.GetFiles(logsDir, "*.log")
                         .OrderByDescending(System.IO.File.GetLastWriteTimeUtc).Take(3))
            {
                try
                {
                    foreach (string line in System.IO.File.ReadLines(file))
                    {
                        bool trainerComplaint = line.Contains("npc_trainer", StringComparison.OrdinalIgnoreCase);
                        bool wrapperMention = false;
                        if (!trainerComplaint)
                            for (int w = from; w <= to && !wrapperMention; w += 1)
                            {
                                // cheap contains for each id would be slow over a range;
                                // only scan for ids we can cheaply detect
                                if (to - from > 50) break;
                                wrapperMention = line.Contains(w.ToString());
                            }
                        if (trainerComplaint || wrapperMention)
                        {
                            logHits.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                            if (logHits.Count >= 80) break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logHits.Add($"{Path.GetFileName(file)}: <unreadable: {ex.Message}>");
                }
                if (logHits.Count >= 80) break;
            }
        }
        else
        {
            logHits.Add($"<logs dir not found: '{logsDir}'>");
        }

        return Json(new
        {
            success = true,
            raConnected = _ra.IsConnected,
            lookup = lookupResult,
            lookupError,
            note = "If 'lookup' does NOT list the custom spell, the running mangosd predates it — " +
                   "restart the world server. If it lists it but trainers still lack it, read logHits " +
                   "for npc_trainer load complaints.",
            logHits,
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // REFERENCE LISTS — labeled dropdowns instead of raw DBC indexes
    // ═══════════════════════════════════════════════════════════════════

    private static object? _refsCache;

    /// <summary>Duration / cast-time / range indexes with human labels, read
    /// straight from the server-side DBC directory. Cached per process.</summary>
    [HttpGet]
    public IActionResult Refs()
    {
        if (_refsCache is not null) return Json(_refsCache);
        string dbcPath = _config["Vmangos:DbcPath"] ?? "/home/wowvmangos/vmangos/run/data/5875/dbc";

        List<object> ReadRef(string file, Func<uint[], string> label)
        {
            var result = new List<object>();
            try
            {
                string path = Path.Combine(dbcPath, file);
                if (!System.IO.File.Exists(path)) return result;
                foreach (var row in DbcWriterService.ReadDbc(path).GetAllRows())
                    result.Add(new { id = row[0], label = label(row) });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Completer: failed reading {File}", file);
            }
            return result;
        }

        string Ms(long ms) => ms < 0 ? "until cancelled"
            : ms == 0 ? "instant"
            : ms % 60000 == 0 && ms >= 60000 ? $"{ms / 60000} min"
            : $"{ms / 1000.0:0.#} s";

        _refsCache = new
        {
            // SpellDuration.dbc: [1] = base duration ms (int; -1 = infinite)
            durations = ReadRef("SpellDuration.dbc", r => Ms(unchecked((int)r[1]))),
            // SpellCastTimes.dbc: [1] = base cast ms
            castTimes = ReadRef("SpellCastTimes.dbc", r => Ms(unchecked((int)r[1]))),
            // SpellRange.dbc: [1]/[2] = min/max yards (floats)
            ranges = ReadRef("SpellRange.dbc", r =>
            {
                float min = DbcWriterService.UintToFloat(r[1]);
                float max = DbcWriterService.UintToFloat(r[2]);
                return min > 0 ? $"{min:0}–{max:0} yd" : max > 0 ? $"{max:0} yd" : "self";
            }),
        };
        return Json(_refsCache);
    }

    // ═══════════════════════════════════════════════════════════════════
    // PUSH INBOX — spells sent straight from MSUIClient creator mode
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ingest a design pushed by MSUIClient's creator mode, so the spell shows up
    /// on this page without anyone carrying spell-session.json across by hand.
    ///
    /// Accepts EITHER shape the creator can produce: one session entry (has
    /// tempName), or a whole session document (format "msui-spell-session" with a
    /// spells array) — the latter so the very same file the drop-zone takes can
    /// also be curl'd up. Pushing a temp name that already exists replaces it,
    /// matching the creator's own rule for its session file.
    ///
    /// The payload carries base64 M2s, BLPs and audio, so it dwarfs a normal form
    /// post; the size limit is raised here rather than globally, keeping every
    /// other endpoint on Kestrel's 30 MB default.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(268_435_456)]   // 256 MB — a fully-tuned multi-model spell with audio
    public IActionResult Push([FromBody] JsonObject? body)
    {
        if (body is null)
            return Json(new { success = false, error = "Empty push body." });

        // One entry, or the whole session document.
        var entries = new List<JsonObject>();
        if (body["spells"] is JsonArray spells)
        {
            foreach (JsonNode? node in spells)
                if (node is JsonObject entry) entries.Add(entry);
            if (entries.Count == 0)
                return Json(new { success = false, error = "Session document carries no spells." });
        }
        else if (body["tempName"] is not null)
        {
            entries.Add(body);
        }
        else
        {
            return Json(new
            {
                success = false,
                error = "Not an MSUIClient design push — expected a session entry (tempName) " +
                        "or a session document (spells[]).",
            });
        }

        var saved = new List<SpellSessionInbox.ItemSummary>();
        var rejected = new List<string>();
        foreach (JsonObject entry in entries)
        {
            string tempName = entry["tempName"]?.GetValue<string>() ?? "";
            if (SpellSessionInbox.SafeId(tempName).Length == 0)
            {
                rejected.Add($"'{tempName}' has no letters or digits to name it by");
                continue;
            }
            // A design with nothing in it would create an inbox card that cannot be
            // completed into anything — reject at the door rather than downstream.
            int modelCount = (entry["models"] as JsonArray)?.Count ?? 0;
            int audioCount = (entry["audio"] as JsonArray)?.Count ?? 0;
            int ruleCount = (entry["rules"]?["columns"] as JsonObject)?.Count ?? 0;
            if (modelCount == 0 && audioCount == 0 && ruleCount == 0)
            {
                rejected.Add($"'{tempName}' carries no models, no audio and no rules");
                continue;
            }
            try
            {
                if (SpellSessionInbox.Save(_env.ContentRootPath, entry) is { } summary)
                    saved.Add(summary);
                else
                    rejected.Add($"'{tempName}' could not be stored");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Completer: push of '{Temp}' failed to store", tempName);
                rejected.Add($"'{tempName}': {ex.Message}");
            }
        }

        if (saved.Count == 0)
            return Json(new { success = false, error = string.Join("; ", rejected) });

        _logger.LogInformation("Completer: push accepted {Count} spell(s) — {Names}",
            saved.Count, string.Join(", ", saved.Select(s => s.TempName)));

        return Json(new
        {
            success = true,
            accepted = saved.Count,
            spells = saved,
            rejected,
        });
    }

    /// <summary>Everything waiting in the inbox. Each item carries the FULL design
    /// metadata the page renders from (tuning dials, texture edits, phases) but
    /// none of the embedded bytes — listing a dozen spells must not mean shipping
    /// a dozen patched models to the browser.</summary>
    [HttpGet]
    public IActionResult Pending()
    {
        try
        {
            var items = SpellSessionInbox.List(_env.ContentRootPath);
            var spells = new List<JsonObject>();
            foreach (var item in items)
                if (SpellSessionInbox.LoadMeta(_env.ContentRootPath, item.Id) is { } meta)
                    spells.Add(meta);
            return Json(new { success = true, items, spells });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completer: listing the push inbox failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>Drop a pushed design. Only removes the inbox copy — a spell already
    /// completed from it keeps its SQL rows and its stored design bytes.</summary>
    [HttpPost]
    public IActionResult DiscardPending(string id)
    {
        try
        {
            bool removed = SpellSessionInbox.Delete(_env.ContentRootPath, id ?? "");
            return Json(removed
                ? new { success = true, error = (string?)null }
                : new { success = false, error = (string?)$"No pushed design '{id}' in the inbox." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completer: discarding pushed design '{Id}' failed", id);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Fill a completion request's design payload from the inbox when it names a
    /// pushed item. The browser then never re-uploads the bytes it was shown — it
    /// posts an id and the form fields, and the megabytes stay on this machine.
    ///
    /// Returns an error string, or null on success. An inline payload (the file
    /// drop-zone path) is left exactly as posted.
    /// </summary>
    private string? ResolvePendingDesign(CompleteSpellRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.PendingId)) return null;

        JsonObject? entry = SpellSessionInbox.LoadFull(_env.ContentRootPath, req.PendingId);
        if (entry is null)
            return $"Pushed design '{req.PendingId}' is no longer in the inbox — re-push it from the creator.";

        req.TempName ??= entry["tempName"]?.GetValue<string>();
        req.ExportedAtUtc ??= entry["exportedAtUtc"]?.GetValue<string>();
        // The source spell is the design's own property, never the form's — a
        // mistyped field must not clone the wrong spell.
        // A design built from nothing says so with 0 - and that, too, must win over the form.
        int sourceId = JsonInt(entry["sourceSpellId"]);
        if (entry["sourceSpellId"] is not null) req.SourceSpellEntry = Math.Max(0, sourceId);

        req.Models = (entry["models"] as JsonArray)?.Select(node => new CompleterModelDto
        {
            Path = node?["path"]?.GetValue<string>() ?? "",
            Phases = node?["phases"]?.GetValue<string>(),
            M2Base64 = node?["m2Base64"]?.GetValue<string>(),
        }).ToList() ?? new List<CompleterModelDto>();

        req.TintedBlps = (entry["tintedBlps"] as JsonArray)?.Select(node => new CompleterBlpDto
        {
            Path = node?["path"]?.GetValue<string>() ?? "",
            BlpBase64 = node?["blpBase64"]?.GetValue<string>(),
        }).ToList() ?? new List<CompleterBlpDto>();

        req.Composition = SpellComposition.Parse(entry["composition"]);

        // The workshop's RULES (MSUIClient SPELL_CREATOR_PLAN.md P1.7): spell_template columns,
        // numbers only, exactly as the author set them. Unknown names are dropped later by
        // CloneSpellAsync, which only writes columns the table really has.
        req.Rules = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (entry["rules"]?["columns"] is JsonObject ruleColumns)
            foreach (var (column, value) in ruleColumns)
                if (value is JsonValue jv && System.Text.RegularExpressions.Regex.IsMatch(column, "^[A-Za-z][A-Za-z0-9_]{0,63}$"))
                {
                    if (jv.TryGetValue(out long whole)) req.Rules[column] = whole;
                    else if (jv.TryGetValue(out double real)) req.Rules[column] = real;
                }

        // D5 (SPELL_CREATOR_PLAN.md): the design's NEW index rows get real ones - an equal row when
        // the server's table has it, else a new row at 1000+ written to the server's dbc dir (the
        // Core merges it live on .reload spell_index; patch-3 carries the file) - and the rules'
        // index columns are pointed at them.
        List<SpellIndexRows.Request> newRows = SpellIndexRows.Parse(entry["rules"]?["newIndexRows"]);
        try
        {
            string dbcDir = _config["Vmangos:DbcPath"] ?? "/home/wowvmangos/vmangos/run/data/5875/dbc";
            Dictionary<uint, uint> rowMap = newRows.Count == 0 ? new()
                : SpellIndexRows.ApplyToServer(dbcDir, newRows, out HashSet<string> grown);
            if (newRows.Count > 0)
                _logger.LogInformation("Completer: index rows {Map}", string.Join(", ",
                    newRows.Select(r => $"{r.Table} {r.Value} -> {rowMap[r.LocalId]}")));
            if (SpellIndexRows.RewriteColumns(req.Rules, rowMap) is { } rowError) return rowError;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completer: new index rows failed");
            return $"new index rows could not be written: {ex.Message}";
        }

        if (entry["actor"] is JsonObject actor)
            req.Actor = new CompleterActorDto
            {
                Effect = (int)JsonNum(actor["effect"], 0f),
                Name = actor["name"]?.GetValue<string>() ?? "Actor",
                DisplayId = (uint)JsonNum(actor["displayId"], 4589f),
                Scale = JsonNum(actor["scale"], 1f),
                Spell = (uint)JsonNum(actor["spell"], 0f),
            };

        req.Audio = (entry["audio"] as JsonArray)?.Select(node => new CompleterAudioDto
        {
            Cue = node?["cue"]?.GetValue<string>() ?? "",
            MpqPath = node?["mpqPath"]?.GetValue<string>() ?? "",
            SourceFile = node?["sourceFile"]?.GetValue<string>(),
            SourceSoundId = (uint)JsonNum(node?["sourceSoundId"], 0f),
            Volume = JsonNum(node?["volume"], 1f),
            Looping = node?["looping"]?.GetValue<bool>() ?? false,
            NoDuplicates = node?["noDuplicates"]?.GetValue<bool>() ?? false,
            SoundType = (uint)JsonNum(node?["soundType"], 1f),
            ExtraFlags = (uint)JsonNum(node?["extraFlags"], 0f),
            Eax = (uint)JsonNum(node?["eax"], 0f),
            MinDistance = JsonNum(node?["minDistance"], 0f),
            CutoffDistance = JsonNum(node?["cutoffDistance"], 0f),
            FileBase64 = node?["fileBase64"]?.GetValue<string>(),
        }).ToList() ?? new List<CompleterAudioDto>();

        return null;
    }

    /// <summary>
    /// Re-prove SpellDbcColumnMap against the LIVE data (MSUIClient SPELL_CREATOR_PLAN.md
    /// P0.5): every stock spell's spell_template row is mapped through the table and compared
    /// with the stock Spell.dbc row the patch pipeline builds on. A correct table agrees on
    /// every column of nearly every spell; a column that disagrees broadly means the table
    /// (or the schema) is wrong and custom spells would lie to the client about it.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ColumnMapCheck([FromServices] PatchBuilderService builder)
    {
        try
        {
            var spellDbc = builder.ReadStockServerDbc("Spell.dbc");
            using var conn = _spellCreator.CreateMangosConnection();
            var rows = await conn.QueryAsync(
                @"SELECT t.* FROM spell_template t
                  JOIN (SELECT entry, MAX(build) b FROM spell_template WHERE build <= 5875 AND entry < 40000 GROUP BY entry) m
                    ON m.entry = t.entry AND m.b = t.build");
            int checkedCount = 0, agreeing = 0;
            var perColumn = new Dictionary<string, (int n, List<uint> sample)>();
            foreach (var row in rows)
            {
                var map = ((IDictionary<string, object>)row)
                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);
                uint entry = Convert.ToUInt32(map["entry"]);
                if (spellDbc.GetRow(entry) is not { } dbcRow) continue;
                checkedCount++;
                List<string> bad = SpellDbcColumnMap.Diff(dbcRow, map);
                if (bad.Count == 0) { agreeing++; continue; }
                foreach (string column in bad)
                {
                    if (!perColumn.TryGetValue(column, out var tally)) tally = (0, new List<uint>());
                    if (tally.sample.Count < 8) tally.sample.Add(entry);
                    perColumn[column] = (tally.n + 1, tally.sample);
                }
            }
            return Json(new
            {
                success = true,
                mappedColumns = SpellDbcColumnMap.Columns.Count,
                spellsChecked = checkedCount,
                spellsAgreeingOnEveryColumn = agreeing,
                disagreements = perColumn.OrderByDescending(kv => kv.Value.n)
                    .Select(kv => new { column = kv.Key, spells = kv.Value.n, sample = kv.Value.sample }),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completer: column-map check failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>The creator writes volume as a float and the flags as integers, and
    /// GetValue&lt;float&gt; throws on an integer token (and vice versa) — every
    /// numeric read out of a pushed design goes through this tolerant pair.</summary>
    private static float JsonNum(JsonNode? node, float fallback)
    {
        if (node is null) return fallback;
        try { return node.GetValue<float>(); }
        catch (Exception) { }
        try { return node.GetValue<int>(); }
        catch (Exception) { return fallback; }
    }

    private static int JsonInt(JsonNode? node) => (int)JsonNum(node, 0f);

    /// <summary>Spell Creator actors live at 7,900,000 + their spell's entry: inside the World Pack
    /// template range (7,000,000+, WorldPackContent.TemplateBase) but above anything a pack allocates,
    /// and stable per spell, so a Test live that replaces the spell replaces its actor too.</summary>
    public const uint ActorEntryBase = 7_900_000;

    /// <summary>The actor's creature_template row, cloned from Blizzard's Searing Totem (2523, patch 0) so
    /// every column the core reads is a real totem's, then made this design's: name, display, scale,
    /// the spell it casts, creature type 11 (totem). The spell's totem effect then names it.</summary>
    private async Task WriteActorAsync(int spellEntry, CompleterActorDto actor)
    {
        uint actorEntry = ActorEntryBase + (uint)spellEntry;
        using var conn = _db.Mangos();
        conn.Open();
        await conn.ExecuteAsync("DROP TEMPORARY TABLE IF EXISTS sui_actor_tmp");
        await conn.ExecuteAsync("CREATE TEMPORARY TABLE sui_actor_tmp AS SELECT * FROM creature_template WHERE entry = 2523 AND patch = 0");
        await conn.ExecuteAsync(
            @"UPDATE sui_actor_tmp SET entry = @Entry, name = @Name, subname = 'Spell Creator actor',
                     display_id1 = @Display, display_id2 = 0, display_id3 = 0, display_id4 = 0,
                     display_scale1 = @Scale, display_probability1 = 100, display_total_probability = 100,
                     spell_id1 = @Spell, type = 11",
            new { Entry = actorEntry, Name = actor.Name.Length > 60 ? actor.Name[..60] : actor.Name,
                  Display = actor.DisplayId, Scale = actor.Scale, Spell = actor.Spell });
        await conn.ExecuteAsync("DELETE FROM creature_template WHERE entry = @Entry", new { Entry = actorEntry });
        await conn.ExecuteAsync("INSERT INTO creature_template SELECT * FROM sui_actor_tmp");
        await conn.ExecuteAsync("DROP TEMPORARY TABLE sui_actor_tmp");
        await conn.ExecuteAsync(
            $@"UPDATE spell_template SET effectMiscValue{actor.Effect} = @Actor
               WHERE entry = @Spell AND build = (SELECT MAX(b) FROM (SELECT build AS b FROM spell_template WHERE entry = @Spell) t)",
            new { Actor = actorEntry, Spell = spellEntry });
        _logger.LogInformation("Completer: actor #{Actor} '{Name}' (display {Display}, casts {Cast}) for spell #{Spell} effect {Effect}",
            actorEntry, actor.Name, actor.DisplayId, actor.Spell, spellEntry, actor.Effect);
    }

    /// <summary>Complete one spell from an uploaded MSUIClient session: create the
    /// SQL rows, persist the design bytes, save the rebuild config.</summary>
    [HttpPost]
    public async Task<IActionResult> Complete([FromBody] CompleteSpellRequest req)
    {
        // A pushed design supplies its own bytes and its own source spell, so this
        // runs BEFORE validation — the request that names one arrives with no
        // models, no BLPs and no sourceSpellEntry at all.
        if (ResolvePendingDesign(req) is { } pendingError)
            return Json(new { success = false, error = pendingError });

        if (string.IsNullOrWhiteSpace(req.SpellName))
            return Json(new { success = false, error = "Spell name is required." });
        if (CompleterStore.SafeName(req.SpellName).Length == 0)
            return Json(new { success = false, error = "Spell name needs letters or digits." });
        // A spell designed from nothing (MSUIClient "New spell") has no source to clone: it
        // starts from the documented neutral template and every field the design and the form
        // carry is written over it (SpellCreatorService.FromZeroTemplateSpell).
        bool fromZero = req.SourceSpellEntry <= 0;
        if (fromZero) req.SourceSpellEntry = SpellCreatorService.FromZeroTemplateSpell;

        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        // Every step below that can fail WITHOUT aborting the completion appends here.
        // A spell that lost its skill_line_ability, spell_chain or trainer rows still
        // returns success but is invisible in game, so these have to reach the audit
        // trail rather than dying in the log file.
        var warnings = new List<string>();

        // One batch for the whole completion. SpellCreatorService writes a spell_clone row
        // per rank without knowing anything about batching — the ambient scope stamps them
        // all with the same batch_id, so the Change Graph shows one Completer run instead
        // of N unrelated spells appearing out of nowhere.
        using var batch = AuditBatch.Begin(
            $"Spell Completer — {req.SpellName}" +
            (string.IsNullOrWhiteSpace(req.TempName) ? "" : $" ({req.TempName})"));

        try
        {
            // ── 1: spell_template clone with the user's data-phase values ──
            // (mirrors PatchController.Generate's override construction)
            var overrides = new Dictionary<string, object?>
            {
                ["name"] = req.SpellName,
                ["nameSubtext"] = req.NameSubtext ?? "Rank 1",
            };
            // A negative school means "keep the source's" (the creator's Test live sends it
            // when the design itself says nothing about school).
            if (req.School >= 0) overrides["school"] = req.School;
            if (!string.IsNullOrWhiteSpace(req.Description))
                overrides["description"] = req.Description;
            if (req.DamageMin.HasValue && req.DamageMax.HasValue)
            {
                // vmangos rolls basePoints + irand(1, dieSides) (SpellCaster.cpp), so a min..max
                // range is dieSides = max-min+1 - stored IDENTICALLY in Spell.dbc (SpellDbcColumnMap).
                var (bp1, ds1) = SpellDbcColumnMap.PointsFor(req.DamageMin.Value, req.DamageMax.Value);
                overrides["effectBasePoints1"] = bp1;
                overrides["effectDieSides1"] = ds1;
                overrides["effectBaseDice1"] = 1;   // the roll's floor; 0 on some templates (18282)
            }
            if (req.ManaCost.HasValue) overrides["manaCost"] = req.ManaCost.Value;
            if (req.SpellLevel.HasValue)
            {
                overrides["spellLevel"] = req.SpellLevel.Value;
                overrides["baseLevel"] = req.SpellLevel.Value;
            }
            if (req.MaxLevel.HasValue) overrides["maxLevel"] = req.MaxLevel.Value;
            if (req.CastingTimeIndex.HasValue) overrides["castingTimeIndex"] = req.CastingTimeIndex.Value;
            if (req.RangeIndex.HasValue) overrides["rangeIndex"] = req.RangeIndex.Value;
            if (req.DurationIndex.HasValue) overrides["durationIndex"] = req.DurationIndex.Value;
            if (req.Cooldown.HasValue) overrides["recoveryTime"] = req.Cooldown.Value;
            if (req.MissileSpeed is { } missileSpeed && missileSpeed > 0f) overrides["speed"] = missileSpeed;

            // Effect-slot overrides: the mechanics editor (School Damage / DoT /
            // Slow / Heal ...). Only CHANGED slots arrive; each carries its full
            // value set. Points: base = min-1, dieSides = max-min+1, the same number on
            // both sides (SpellDbcColumnMap.PointsFor - the old "max-min, +1 in the DBC"
            // convention made the server roll one short of the tooltip's maximum).
            // These are also remembered for the rank chain below — the generator
            // clones ranks 2+ from the SOURCE ranks, which still have the source
            // mechanics, so the structure must ride the per-rank overrides too.
            var effectStructure = new Dictionary<string, object?>();
            foreach (var ef in req.Effects ?? new List<CompleterEffectDto>())
            {
                if (ef.Slot is < 1 or > 3) continue;
                effectStructure[$"effect{ef.Slot}"] = ef.Effect;
                effectStructure[$"effectApplyAuraName{ef.Slot}"] = ef.Effect == 6 ? ef.Aura : 0;
                if (ef.PointsMin.HasValue && ef.PointsMax.HasValue)
                {
                    var (bp, ds) = SpellDbcColumnMap.PointsFor(ef.PointsMin.Value, ef.PointsMax.Value);
                    effectStructure[$"effectBasePoints{ef.Slot}"] = bp;
                    effectStructure[$"effectDieSides{ef.Slot}"] = ds;
                    effectStructure[$"effectBaseDice{ef.Slot}"] = 1;
                }
                if (ef.Amplitude.HasValue) effectStructure[$"effectAmplitude{ef.Slot}"] = ef.Amplitude.Value;
                if (ef.MiscValue.HasValue) effectStructure[$"effectMiscValue{ef.Slot}"] = ef.MiscValue.Value;
            }
            foreach (var (col, value) in effectStructure) overrides[col] = value;
            // The design's rules win over every form field (decision D1: authoring lives in the
            // workshop); identity columns stay the Completer's.
            foreach (var (col, value) in req.Rules ?? new Dictionary<string, object?>())
                if (!col.Equals("entry", StringComparison.OrdinalIgnoreCase) && !col.Equals("build", StringComparison.OrdinalIgnoreCase))
                    overrides[col] = value;
            if (!string.IsNullOrEmpty(req.SkillTabKey))
            {
                var tabMap = SpellCreatorService.GetSkillTabMap();
                if (tabMap.TryGetValue(req.SkillTabKey, out var tabInfo))
                    overrides["spellFamilyName"] = tabInfo.spellFamilyName;
            }

            // Replace in place (Test live): only an entry this Completer made from the SAME design.
            int? replaceEntry = null;
            if (req.ReplaceEntry is > 0)
            {
                var existingConfig = await _spellConfig.GetConfigAsync(req.ReplaceEntry.Value);
                if (existingConfig is null)
                    return Json(new { success = false, error = $"#{req.ReplaceEntry} is not a Completer spell - nothing to replace." });
                replaceEntry = req.ReplaceEntry;
            }
            int newEntry = await _spellCreator.CloneSpellAsync(req.SourceSpellEntry, overrides, ip, replaceEntry);
            if (newEntry < 0)
            {
                await LogCompletionAsync(req, ip, 0, null, 0, 0, null, warnings,
                    success: false, error: "spell_template clone produced no row");
                return Json(new { success = false, error = "Failed to create spell in database." });
            }

            // ── 1b: the actor (P4.2) - its own creature row, then the spell's totem effect names it ──
            if (req.Actor is { Effect: >= 1 and <= 3 } actorDto)
            {
                try { await WriteActorAsync(newEntry, actorDto); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Completer: actor for #{Entry} failed", newEntry);
                    warnings.Add($"the actor could not be written ({ex.Message}) - the spell summons nothing");
                }
            }

            // ── 2: spellbook tab + rank chain root ──
            if (!string.IsNullOrEmpty(req.SkillTabKey))
            {
                try
                {
                    // Auto-learn (2) only for genuine level-1 base spells; a mid-level clone must be
                    // trainer-learned (0) or the class auto-knows R1 and it vanishes from trainers.
                    int r1LearnFlag = await _spellCreator.ResolveRank1LearnFlagAsync(newEntry, req.SpellLevel);
                    await _spellCreator.InsertSkillLineAbilityAsync(newEntry, req.SkillTabKey, learnOnGetSkill: r1LearnFlag);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Completer: skill_line_ability insert failed for #{Entry}", newEntry);
                    warnings.Add($"skill_line_ability insert failed for tab '{req.SkillTabKey}' — the spell will not appear in the spellbook or at trainers ({ex.Message})");
                }
            }
            else
            {
                // No tab picked: prefer the tab MATCHING THE CHOSEN SCHOOL within
                // the source spell's class (School=Fire on a mage spell → Mage—Fire),
                // falling back to the source spell's own tab. School (damage type)
                // and skill line (trainer/spellbook category) are independent
                // fields in WoW's data — but when the user picks a school, the
                // matching category is almost always what they mean.
                string? schoolTabKey = null;
                try
                {
                    using var probeConn = _spellCreator.CreateMangosConnection();
                    var probeSla = await probeConn.QueryFirstOrDefaultAsync<dynamic>(
                        @"SELECT class_mask FROM skill_line_ability
                          WHERE spell_id = @E AND build = 5875 LIMIT 1",
                        new { E = req.SourceSpellEntry });
                    int sourceClassMask = probeSla != null ? (int)(probeSla.class_mask ?? 0) : 0;
                    string[] schoolNames = { "physical", "holy", "fire", "nature", "frost", "shadow", "arcane" };
                    if (sourceClassMask > 0 && req.School >= 0 && req.School < schoolNames.Length)
                    {
                        string suffix = "_" + schoolNames[req.School];
                        schoolTabKey = SpellCreatorService.GetSkillTabMap()
                            .FirstOrDefault(t => t.Value.classMask == sourceClassMask &&
                                                 t.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).Key;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Completer: school-tab match probe failed for #{Entry}", newEntry);
                    warnings.Add($"school-tab match probe failed — fell back to inheriting the source spell's tab ({ex.Message})");
                }

                if (schoolTabKey is not null)
                {
                    try
                    {
                        // Auto-learn (2) only for genuine level-1 base spells; a mid-level clone must be
                        // trainer-learned (0) or the class auto-knows R1 and it vanishes from trainers.
                        int r1LearnFlag = await _spellCreator.ResolveRank1LearnFlagAsync(newEntry, req.SpellLevel);
                        await _spellCreator.InsertSkillLineAbilityAsync(newEntry, schoolTabKey, learnOnGetSkill: r1LearnFlag);
                        _logger.LogInformation("Completer: #{Entry} auto-tabbed to '{Tab}' (matches chosen school)",
                            newEntry, schoolTabKey);
                        req.SkillTabKey = schoolTabKey;   // rank chain + family name follow the same tab
                        var tabMap2 = SpellCreatorService.GetSkillTabMap();
                        if (tabMap2.TryGetValue(schoolTabKey, out var tabInfo2))
                        {
                            overrides["spellFamilyName"] = tabInfo2.spellFamilyName;
                            using var famConn = _spellCreator.CreateMangosConnection();
                            await famConn.ExecuteAsync(
                                "UPDATE spell_template SET spellFamilyName = @F WHERE entry = @E AND build = 5875",
                                new { F = tabInfo2.spellFamilyName, E = newEntry });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Completer: school-tab insert failed for #{Entry}", newEntry);
                        warnings.Add($"school-tab insert failed for '{schoolTabKey}' — fell through to inheriting the source spell's tab ({ex.Message})");
                        schoolTabKey = null;   // fall through to source inherit
                    }
                }

                if (schoolTabKey is null)
                {
                //
                // The insert MUST allocate `id`: skill_line_ability's primary key
                // has no default and no auto-increment, so an id-less INSERT
                // IGNORE silently no-ops. The rebuild then reads skill 0/class 0
                // and writes a dead SkillLineAbility.dbc row — which the client
                // cannot categorize, so the spell VANISHES from trainer lists
                // even though the server offers it. (This is exactly how
                // FireFunnel went missing at the mage trainers.)
                try
                {
                    using var slaConn = _spellCreator.CreateMangosConnection();
                    var sourceSla = await slaConn.QueryFirstOrDefaultAsync<dynamic>(
                        @"SELECT skill_id, class_mask FROM skill_line_ability
                          WHERE spell_id = @E AND build = 5875 LIMIT 1",
                        new { E = req.SourceSpellEntry });
                    if (sourceSla != null && (int)(sourceSla.skill_id ?? 0) > 0)
                    {
                        int nextId = await slaConn.ExecuteScalarAsync<int>(
                            "SELECT COALESCE(MAX(id), 0) + 1 FROM skill_line_ability");
                        int affected = await slaConn.ExecuteAsync(
                            @"INSERT IGNORE INTO skill_line_ability
                              (id, build, skill_id, spell_id, race_mask, class_mask, req_skill_value,
                               superseded_by_spell, learn_on_get_skill, max_value, min_value, req_train_points)
                              VALUES (@Id, 5875, @SkillId, @SpellId, 0, @ClassMask, 1, 0, 2, 0, 0, 0)",
                            new
                            {
                                Id = nextId,
                                SkillId = (int)sourceSla.skill_id,
                                SpellId = newEntry,
                                ClassMask = (int)(sourceSla.class_mask ?? 0),
                            });
                        if (affected == 0)
                        {
                            _logger.LogWarning("Completer: SLA inherit insert ignored for #{Entry} (id {Id})",
                                newEntry, nextId);
                            warnings.Add($"skill_line_ability inherit insert was ignored (id {nextId} collided) — the spell may not be categorizable by the client");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Completer: source #{Src} has no skill_line_ability row to " +
                            "inherit — #{Entry} will rely on the rebuild's source fallback", req.SourceSpellEntry, newEntry);
                        warnings.Add($"source #{req.SourceSpellEntry} has no skill_line_ability row to inherit — relying on the rebuild's source fallback");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Completer: SLA auto-copy failed for #{Entry}", newEntry);
                    warnings.Add($"skill_line_ability auto-copy failed — the spell will not appear in the spellbook or at trainers ({ex.Message})");
                }
                }
            }

            try
            {
                await _spellCreator.InsertSpellChainAsync(newEntry, prevSpell: 0, firstSpell: newEntry, rank: 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Completer: spell_chain insert failed for #{Entry}", newEntry);
                warnings.Add($"spell_chain insert failed — rank progression will not work ({ex.Message})");
            }

            // ── 2b: register Rank 1 at trainers ──
            // (The rank generator only registers ranks 2+; without this step the
            // first rank never appears at any trainer.) Mirrors Generate's flow:
            // copy the source spell's trainer locations via a learn-spell wrapper;
            // if the source is a starting spell with no trainer entries, fall
            // back to registering at all class trainers for the chosen tab.
            if (req.CopySourceTrainers)
            {
                try
                {
                    int trainersCopied = await _spellCreator.CopyTrainerEntriesFromSourceAsync(
                        req.SourceSpellEntry, newEntry, 0, req.SpellLevel ?? 1);
                    if (trainersCopied > 0)
                    {
                        _logger.LogInformation("Completer: Copied {Count} trainer entries from source #{Src} to R1 #{New}",
                            trainersCopied, req.SourceSpellEntry, newEntry);
                    }
                    else if (!string.IsNullOrEmpty(req.SkillTabKey))
                    {
                        var tabMap = SpellCreatorService.GetSkillTabMap();
                        if (tabMap.TryGetValue(req.SkillTabKey, out var tabInfo))
                        {
                            // classMask = 1 << (classId - 1) → recover the classId
                            int classId = 0;
                            int mask = tabInfo.classMask;
                            while (mask > 1) { mask >>= 1; classId++; }
                            classId++;

                            var templateMap = SpellCreatorService.GetClassTrainerTemplateMap();
                            if (templateMap.TryGetValue(classId, out int templateId))
                            {
                                int iconId = 185;
                                try
                                {
                                    using var iconConn = _spellCreator.CreateMangosConnection();
                                    var icon = await Dapper.SqlMapper.ExecuteScalarAsync<int?>(iconConn,
                                        "SELECT spellIconId FROM spell_template WHERE entry = @E AND build = 5875",
                                        new { E = newEntry });
                                    iconId = icon ?? 185;
                                }
                                catch { /* fallback icon */ }

                                int wrapperId = await _spellCreator.CreateTrainerWrapperAsync(
                                    newEntry, req.SpellName, req.NameSubtext ?? "Rank 1",
                                    req.SpellLevel ?? 1, iconId);
                                await _spellCreator.InsertNpcTrainerTemplateAsync(
                                    templateId, wrapperId, 100, req.SpellLevel ?? 1);
                                _logger.LogInformation(
                                    "Completer: Registered R1 #{New} at class trainer template {Tmpl} via wrapper #{Wrap}",
                                    newEntry, templateId, wrapperId);
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Completer: source #{Src} has no trainer entries and no " +
                            "skill tab was chosen — R1 #{New} is not at any trainer", req.SourceSpellEntry, newEntry);
                        warnings.Add($"source #{req.SourceSpellEntry} has no trainer entries and no skill tab was chosen — rank 1 is not available at any trainer");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Completer: trainer registration failed for R1 #{Entry}", newEntry);
                    warnings.Add($"trainer registration failed for rank 1 — players cannot learn the spell ({ex.Message})");
                }
            }

            // ── 3: optional full rank chain, scaled per-rank like the Creator page ──
            List<(int rank, int entry)>? ranks = null;
            if (req.GenerateAllRanks)
            {
                try
                {
                    Dictionary<int, Dictionary<string, object?>>? perRank = null;

                    // Carry the mechanics changes to EVERY rank: ranks 2+ are
                    // cloned from the source ranks (which keep source mechanics),
                    // so without this a DoT'd rank 1 gets vanilla ranks 2+.
                    // Injected for a generous rank range; only existing ranks match.
                    if (effectStructure.Count > 0 || req.DurationIndex.HasValue)
                    {
                        perRank = new Dictionary<int, Dictionary<string, object?>>();
                        for (int rank = 2; rank <= 25; rank++)
                        {
                            var d = new Dictionary<string, object?>(effectStructure);
                            if (req.DurationIndex.HasValue) d["durationIndex"] = req.DurationIndex.Value;
                            perRank[rank] = d;
                        }
                    }

                    if (req.RankOverrides is { Count: > 0 })
                    {
                        perRank ??= new Dictionary<int, Dictionary<string, object?>>();
                        foreach (var (rank, ro) in req.RankOverrides)
                        {
                            // Merge ON TOP of the injected structure dict (never
                            // replace it — that would strip the mechanics).
                            var d = perRank.TryGetValue(rank, out var existing)
                                ? existing : new Dictionary<string, object?>();
                            if (ro.DamageMin.HasValue && ro.DamageMax.HasValue)
                            {
                                var (rbp, rds) = SpellDbcColumnMap.PointsFor(ro.DamageMin.Value, ro.DamageMax.Value);
                                d["effectBasePoints1"] = rbp;
                                d["effectDieSides1"] = rds;
                                d["effectBaseDice1"] = 1;
                            }
                            if (ro.ManaCost.HasValue) d["manaCost"] = ro.ManaCost.Value;
                            if (ro.SpellLevel.HasValue)
                            {
                                d["spellLevel"] = ro.SpellLevel.Value;
                                d["baseLevel"] = ro.SpellLevel.Value;
                            }
                            if (d.Count > 0) perRank[rank] = d;
                        }
                    }

                    ranks = await _spellCreator.GenerateRankChainAsync(
                        existingRank1Entry: newEntry,
                        sourceFirstSpell: req.SourceSpellEntry,
                        spellName: req.SpellName,
                        description: req.Description,
                        school: req.School,
                        skillTabKey: req.SkillTabKey,
                        rank1Overrides: overrides,
                        perRankOverrides: perRank,
                        operatorIp: ip,
                        copySourceTrainers: req.CopySourceTrainers);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Completer: rank chain generation failed for #{Entry}", newEntry);
                    warnings.Add($"rank chain generation failed — only rank 1 exists ({ex.Message})");
                }
            }

            // ── 4: persist the design bytes for every future patch rebuild ──
            var pathM2s = new List<(string originalPath, byte[] bytes)>();
            var extraFiles = new List<(string mpqPath, byte[] bytes)>();
            foreach (var model in req.Models ?? new List<CompleterModelDto>())
            {
                if (string.IsNullOrEmpty(model.M2Base64) || string.IsNullOrEmpty(model.Path)) continue;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(model.M2Base64); }
                catch
                {
                    // The spell rows already exist at this point — bailing here leaves a
                    // spell in the database with no design bytes behind it, so the audit
                    // entry has to record the orphan rather than the request just 400-ing.
                    await LogCompletionAsync(req, ip, newEntry, ranks, 0, 0, null, warnings,
                        success: false, error: $"Bad m2Base64 for {model.Path} — spell rows were already created and are now orphaned");
                    return Json(new { success = false, error = $"Bad m2Base64 for {model.Path}" });
                }

                // Geometry models (per-particle M2s) are referenced from INSIDE the
                // host model's bytes — their path cannot be re-pointed at a clone,
                // so they ship verbatim at their original path (global override).
                if (model.Phases?.StartsWith("geometry", StringComparison.OrdinalIgnoreCase) == true)
                    extraFiles.Add((SpellVisualCloner.NormalizeM2Extension(model.Path), bytes));
                else
                    pathM2s.Add((model.Path, bytes));
            }
            foreach (var blp in req.TintedBlps ?? new List<CompleterBlpDto>())
            {
                if (string.IsNullOrEmpty(blp.BlpBase64) || string.IsNullOrEmpty(blp.Path)) continue;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(blp.BlpBase64); }
                catch
                {
                    await LogCompletionAsync(req, ip, newEntry, ranks, pathM2s.Count, 0, null, warnings,
                        success: false, error: $"Bad blpBase64 for {blp.Path} — spell rows were already created and are now orphaned");
                    return Json(new { success = false, error = $"Bad blpBase64 for {blp.Path}" });
                }
                extraFiles.Add((blp.Path, bytes));
            }

            // Custom phase audio. The bytes ride into the MPQ like any other
            // extra file, but they also need a SoundEntries.dbc row and the
            // phase's cloned visual kit pointed at it, so they are stored as
            // their own kind rather than folded into extraFiles.
            var audioTracks = new List<(CompleterStore.ManifestAudio meta, byte[] bytes)>();
            foreach (var track in req.Audio ?? new List<CompleterAudioDto>())
            {
                if (string.IsNullOrEmpty(track.FileBase64) || string.IsNullOrEmpty(track.Cue)) continue;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(track.FileBase64); }
                catch
                {
                    await LogCompletionAsync(req, ip, newEntry, ranks, pathM2s.Count, extraFiles.Count,
                        null, warnings,
                        success: false, error: $"Bad fileBase64 for the {track.Cue} sound — spell rows were already created and are now orphaned");
                    return Json(new { success = false, error = $"Bad fileBase64 for the {track.Cue} sound" });
                }

                // The creator names the MPQ path from the SOURCE spell's sound, so two
                // custom spells cloned from the same original would fight over one file.
                // Re-home every track under this spell's own directory instead.
                string safe = CompleterStore.SafeName(req.SpellName);
                string ext = Path.GetExtension(track.MpqPath is { Length: > 0 } mp ? mp : track.SourceFile ?? "");
                if (ext.Length == 0) ext = ".wav";
                string mpqPath = $@"Sound\Spells\Custom\{safe}_{track.Cue}{ext}";

                audioTracks.Add((new CompleterStore.ManifestAudio
                {
                    Cue = track.Cue,
                    MpqPath = mpqPath,
                    SourceSoundId = track.SourceSoundId,
                    Volume = track.Volume,
                    Looping = track.Looping,
                    NoDuplicates = track.NoDuplicates,
                    SoundType = track.SoundType,
                    ExtraFlags = track.ExtraFlags,
                    Eax = track.Eax,
                    MinDistance = track.MinDistance,
                    CutoffDistance = track.CutoffDistance,
                }, bytes));
            }

            // The area kit (SpellVisual field 13) is cloned per spell alongside the stage
            // kits (SpellVisualCloner), so an area sound lands on the spell's own copy.

            CompleterStore.Save(new CompleterStore.StoreKey(_env.ContentRootPath, _env.WebRootPath, newEntry, req.SpellName),
                new CompleterStore.Manifest
                {
                    TempName = req.TempName ?? "",
                    SourceSpellEntry = req.SourceSpellEntry,
                    ExportedAtUtc = req.ExportedAtUtc ?? "",
                    Composition = req.Composition,
                },
                pathM2s, extraFiles, audioTracks);

            // ── 5: the config row that makes rebuilds reproduce this spell ──
            // Icon: "custom" reuses one of the user's generated PNGs (the same
            // embed pipeline as the Creator page — IconSource "comfyui-flux");
            // "source" keeps the inherited spell's vanilla icon (resolved at
            // rebuild); anything else falls back to the school icon.
            string iconSource = "school";
            string? iconPath = null;
            if (req.IconSource == "custom" && !string.IsNullOrEmpty(req.IconPath))
            {
                string customIconDir = Path.GetFullPath(
                    Path.Combine(_env.WebRootPath, "images", "icons", "custom"));
                string resolved = Path.GetFullPath(req.IconPath);
                if (resolved.StartsWith(customIconDir, StringComparison.OrdinalIgnoreCase) &&
                    System.IO.File.Exists(resolved))
                {
                    iconSource = "comfyui-flux";
                    iconPath = resolved;
                }
                else
                {
                    _logger.LogWarning("Completer: rejected icon path outside custom dir: {Path}", req.IconPath);
                    warnings.Add($"custom icon '{req.IconPath}' was rejected (outside the custom icon directory) — fell back to the school icon");
                }
            }
            else if (req.IconSource == "source")
            {
                iconSource = "source";
            }

            await _spellConfig.SaveConfigAsync(new SpellVisualConfig
            {
                Entry = newEntry,
                SourceEntry = req.SourceSpellEntry,
                SpellName = req.SpellName,
                NameSubtext = req.NameSubtext ?? "Rank 1",
                Description = req.Description,
                IconSource = iconSource,
                IconPath = iconPath,
            });

            // The design came out of the inbox, so record where it ended up. Purely
            // bookkeeping — the item stays until the user discards it, so a botched
            // completion can simply be redone from the same push.
            if (!string.IsNullOrWhiteSpace(req.PendingId))
                SpellSessionInbox.MarkCompleted(_env.ContentRootPath, req.PendingId, newEntry);

            _logger.LogInformation(
                "Completer: '{Temp}' -> #{Entry} {Name} ({M2s} per-path M2(s), {Extra} extra file(s), " +
                "{Audio} sound(s), {Ranks} rank(s))",
                req.TempName, newEntry, req.SpellName, pathM2s.Count, extraFiles.Count,
                audioTracks.Count, ranks?.Count ?? 1);

            await LogCompletionAsync(req, ip, newEntry, ranks, pathM2s.Count, extraFiles.Count,
                iconSource, warnings, success: true, audioCount: audioTracks.Count);

            return Json(new
            {
                success = true,
                spellEntry = newEntry,
                ranksGenerated = ranks?.Count ?? 0,
                m2Count = pathM2s.Count,
                extraFileCount = extraFiles.Count,
                audioCount = audioTracks.Count,
                warnings,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completer: failed to complete spell '{Name}'", req.SpellName);
            await LogCompletionAsync(req, ip, 0, null, 0, 0, null, warnings,
                success: false, error: ex.Message);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// One audit row per completion attempt — the batch header that ties together the
    /// individual spell_clone rows SpellCreatorService writes for rank 1 and every
    /// generated rank. Those rows say "a spell appeared"; this one says which Completer
    /// run produced them, from which session, and what it took to get there.
    ///
    /// Warnings ride on the entry itself. A completion that lost its skill_line_ability
    /// or trainer rows still returns success, but the spell is invisible in game — the
    /// audit trail is where that has to be visible instead of only in the log file.
    /// </summary>
    private async Task LogCompletionAsync(
        CompleteSpellRequest req,
        string ip,
        int entry,
        List<(int rank, int entry)>? ranks,
        int m2Count,
        int extraFileCount,
        string? iconSource,
        List<string> warnings,
        bool success,
        string? error = null,
        int audioCount = 0)
    {
        var summary = new
        {
            entry,
            sourceEntry = req.SourceSpellEntry,
            name = req.SpellName,
            subtext = req.NameSubtext ?? "Rank 1",
            school = req.School,
            skillTab = req.SkillTabKey,
            sessionName = req.TempName,
            generateAllRanks = req.GenerateAllRanks,
            copySourceTrainers = req.CopySourceTrainers,
            // The rank entries are exactly what a revert has to remove alongside the root.
            ranks = ranks?.Select(r => new { rank = r.rank, entry = r.entry }).ToList(),
            m2Count,
            extraFileCount,
            audioCount,
            iconSource,
            warnings,
            error,
        };

        var tail = warnings.Count > 0
            ? $". {warnings.Count} warning(s): {string.Join("; ", warnings)}"
            : "";

        var notes = success
            ? $"Completed '{req.SpellName}' as #{entry} from source #{req.SourceSpellEntry} — " +
              $"{ranks?.Count ?? 1} rank(s), {m2Count} per-path M2(s), {extraFileCount} extra file(s), " +
              $"{audioCount} custom sound(s). " +
              "Patch rebuild required." + tail
            : $"Completion of '{req.SpellName}' from source #{req.SourceSpellEntry} failed: {error}" + tail;

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = ip,
            Category = "content",
            Action = "spell_completer_run",
            TargetType = "spell_completer",
            TargetId = entry > 0 ? entry : (int?)null,
            TargetName = entry > 0 ? $"{req.SpellName} (#{entry})" : req.SpellName,
            StateAfter = System.Text.Json.JsonSerializer.Serialize(summary),
            // Undoing means deleting the root spell and its rank chain — the Change Graph
            // reads the rank entries back out of the summary above to remove the whole
            // chain rather than orphaning ranks 2+ behind a deleted rank 1.
            IsReversible = success && entry > 0,
            RevertKind = success && entry > 0 ? Services.RevertKind.DeleteCustom : Services.RevertKind.None,
            Success = success,
            Notes = notes,
        });
    }
}

// ═══════════════════════════════════════════════════════════════════════
// REQUEST DTOs — the JS posts the finalization form plus the relevant slice
// of the uploaded session (models with bytes, tinted BLPs).
// ═══════════════════════════════════════════════════════════════════════

/// <summary>MSUIClient P4.2: what a workshop actor is. <see cref="Effect"/> = the spell's effect
/// slot (1-3) whose SUMMON_TOTEM misc value must name the actor; <see cref="Spell"/> = the spell
/// the actor casts (its TotemAI picks the nearest enemy in that spell's range).</summary>
public class CompleterActorDto
{
    public int Effect { get; set; }
    public string Name { get; set; } = "Actor";
    public uint DisplayId { get; set; } = 4589;
    public float Scale { get; set; } = 1f;
    public uint Spell { get; set; }
}

public class CompleteSpellRequest
{
    /// <summary>Names a design already pushed to the inbox by MSUIClient. When
    /// set, the models, tinted BLPs, audio and source spell are loaded server-side
    /// from the stored push and anything the browser sent for them is ignored —
    /// the page posts a form, not megabytes of base64 it was never shown.</summary>
    public string? PendingId { get; set; }

    public string? TempName { get; set; }
    /// <summary>Re-complete onto this existing custom entry instead of a new one (the creator's
    /// Test live loop). Must be a spell the Completer made.</summary>
    public int? ReplaceEntry { get; set; }
    /// <summary>The design's rules block: spell_template column → value (pushed designs only).</summary>
    public Dictionary<string, object?>? Rules { get; set; }
    /// <summary>A workshop ACTOR (MSUIClient SPELL_CREATOR_PLAN.md P4.2): the totem the spell's
    /// SUMMON_TOTEM effect plants, made for this spell.</summary>
    public CompleterActorDto? Actor { get; set; }
    public int SourceSpellEntry { get; set; }
    public string? ExportedAtUtc { get; set; }

    public string SpellName { get; set; } = "";
    public string? NameSubtext { get; set; }
    public string? Description { get; set; }
    public int School { get; set; }
    public string? SkillTabKey { get; set; }
    public int? DamageMin { get; set; }
    public int? DamageMax { get; set; }
    public int? ManaCost { get; set; }
    public int? SpellLevel { get; set; }
    public int? MaxLevel { get; set; }
    public int? CastingTimeIndex { get; set; }
    public int? RangeIndex { get; set; }
    public int? DurationIndex { get; set; }
    public int? Cooldown { get; set; }
    /// <summary>Projectile flight speed in yards per second (spell_template.speed / Spell.dbc
    /// field 37). Null = inherit the source spell's.</summary>
    public float? MissileSpeed { get; set; }
    public bool CopySourceTrainers { get; set; }
    public bool GenerateAllRanks { get; set; }
    public Dictionary<int, CompleterRankOverride>? RankOverrides { get; set; }

    /// <summary>"source" (inherit the source spell's icon), "custom" (IconPath
    /// names a PNG under wwwroot/images/icons/custom), or "school".</summary>
    public string? IconSource { get; set; }
    public string? IconPath { get; set; }

    /// <summary>Changed effect slots from the mechanics editor. Each carries its
    /// complete value set (type, aura, points, tick, misc).</summary>
    public List<CompleterEffectDto>? Effects { get; set; }

    public List<CompleterModelDto>? Models { get; set; }
    public List<CompleterBlpDto>? TintedBlps { get; set; }

    /// <summary>Custom per-phase sounds from the creator's Audio section. Only
    /// ever populated from a pushed design — the file drop-zone path has never
    /// carried audio and still does not.</summary>
    public List<CompleterAudioDto>? Audio { get; set; }

    /// <summary>The creator's kit-level composition (session "composition" block). Only
    /// ever populated from a pushed design.</summary>
    public SpellComposition? Composition { get; set; }
}

public class CompleterEffectDto
{
    /// <summary>1-based effect slot (spell_template effect1..3).</summary>
    public int Slot { get; set; }
    /// <summary>SPELL_EFFECT_* id (0 none, 2 school damage, 6 apply aura, 10 heal, 30 energize, 31 weapon % dmg).</summary>
    public int Effect { get; set; }
    /// <summary>SPELL_AURA_* id when Effect is 6 (3 periodic damage, 8 periodic heal, 33 decrease speed, ...).</summary>
    public int Aura { get; set; }
    public int? PointsMin { get; set; }
    public int? PointsMax { get; set; }
    /// <summary>Tick interval in ms for periodic auras.</summary>
    public int? Amplitude { get; set; }
    public int? MiscValue { get; set; }
}

public class CompleterRankOverride
{
    public int? DamageMin { get; set; }
    public int? DamageMax { get; set; }
    public int? ManaCost { get; set; }
    public int? SpellLevel { get; set; }
}

public class CompleterModelDto
{
    public string Path { get; set; } = "";
    public string? Phases { get; set; }
    public string? M2Base64 { get; set; }
}

public class CompleterBlpDto
{
    public string Path { get; set; } = "";
    public string? BlpBase64 { get; set; }
}

/// <summary>One replaced phase sound, mirroring the creator session's audio
/// entry. Every field past the bytes is a SoundEntries.dbc column.</summary>
public class CompleterAudioDto
{
    /// <summary>precast, cast, missile, impact, state, channel or area.</summary>
    public string Cue { get; set; } = "";
    /// <summary>Path the creator played it from; only its extension survives —
    /// the completed spell re-homes the file under its own name.</summary>
    public string MpqPath { get; set; } = "";
    public string? SourceFile { get; set; }
    /// <summary>SoundEntries id the source spell used for this cue.</summary>
    public uint SourceSoundId { get; set; }
    public float Volume { get; set; } = 1f;
    public bool Looping { get; set; }
    public bool NoDuplicates { get; set; }
    public uint SoundType { get; set; } = 1;
    public uint ExtraFlags { get; set; }
    public uint Eax { get; set; }
    public float MinDistance { get; set; }
    public float CutoffDistance { get; set; }
    public string? FileBase64 { get; set; }
}
