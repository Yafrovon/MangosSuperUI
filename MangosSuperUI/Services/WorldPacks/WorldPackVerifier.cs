using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using MangosSuperUI.Models;
using MangosSuperUI.Services.Mpq;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// The World Pack Verifier's server half (MSUIClient shared_docs/WORLD_BUILDER.md §7): runs
/// <see cref="WorldPackAudit"/> over the PUBLISHED world (out/patch-7.MPQ over the stock
/// archives), the enabled packs' docs and placements and the live world DB, then scans mangosd's
/// Server.log / DBErrors.log since its last start for every line naming a pack id. Every publish
/// runs it after the restart; <c>GET /WorldPacks/Verify</c> runs it on demand. The report is kept
/// at worldpacks/out/verify.json for the client's World Builder "Verify" section.
/// </summary>
public sealed class WorldPackVerifier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly WorldPackStore _store;
    private readonly ConnectionFactory _db;
    private readonly ILogger<WorldPackVerifier> _logger;
    private readonly string _root;
    private readonly string _logDir;
    private readonly string _serverData;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorldPackVerifier(WorldPackStore store, ConnectionFactory db, IConfiguration config, ILogger<WorldPackVerifier> logger)
    {
        _store = store;
        _db = db;
        _logger = logger;
        _root = config["WorldPacks:Root"] ?? "/home/wowvmangos/worldpacks";
        _logDir = config["WorldPacks:ServerLogDir"] ?? "/home/wowvmangos/vmangos/run/bin";
        _serverData = config["Vmangos:ServerDataPath"] ?? "/home/wowvmangos/vmangos/run/data";   // as WorldPackBuildService
    }

    public string ReportPath => Path.Combine(_root, "out", "verify.json");

    public sealed class Report
    {
        public int? BuildId { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public List<string> Packs { get; set; } = new();
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public List<AuditFinding> Findings { get; set; } = new();
    }

    public Report? LastReport()
    {
        try { return File.Exists(ReportPath) ? JsonSerializer.Deserialize<Report>(File.ReadAllText(ReportPath), Json) : null; }
        catch { return null; }
    }

    public async Task<Report> RunAsync(int? buildId, DateTime? serverStartedAfterUtc = null)
    {
        await _gate.WaitAsync();
        try
        {
            var packs = (await _store.ListPacksAsync()).Where(p => p.Enabled).ToList();
            var docs = await _store.DocsAsync(null, null, enabledOnly: true);
            var placements = new List<PlacementRow>();
            foreach (var p in packs) placements.AddRange(await _store.PlacementsAsync(p.Id, null));

            var report = new Report { BuildId = buildId, Packs = packs.Select(p => p.PackKey).ToList() };
            string patch = Path.Combine(_root, "out", WorldPackBuildService.PatchFileName);
            using var stock = new VanillaArchiveSet(Path.Combine(_root, "work", "Data"));
            using var built = File.Exists(patch) ? MpqArchive.Open(patch) : null;
            var mapDirs = WorldPackBuildService.MapDirectories(stock);
            foreach (var d in docs.Where(d => d.Kind == "map"))
            {
                var m = JsonNode.Parse(d.Body)!;
                mapDirs[(int)m["mapId"]!] = (string)m["directory"]!;
            }

            using var conn = _db.Mangos();
            await conn.OpenAsync();
            var facts = new DbFacts(conn);
            var audit = new WorldPackAudit(new WorldPackAudit.AuditInput
            {
                Stock = stock.ReadFile,
                Built = path => built?.ReadFile(path) ?? stock.ReadFile(path),
                MapDirs = mapDirs,
                Docs = docs,
                Placements = placements.Where(p => !p.Deleted).ToList(),
                Facts = facts,
                Mmaps = Path.Combine(_serverData, "mmaps"),   // G15: the navmesh mangosd paths on, as installed
            });
            report.Findings = await Task.Run(audit.Run);
            report.Findings.AddRange(ScanServerLogs(docs, serverStartedAfterUtc));
            report.Errors = report.Findings.Count(f => f.Severity == "error");
            report.Warnings = report.Findings.Count(f => f.Severity == "warn");
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
            await File.WriteAllTextAsync(ReportPath, JsonSerializer.Serialize(report, Json));
            return report;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Pre-flight over the enabled packs' docs as they are NOW (unpublished edits included): every
    /// content check (C1-C10) plus the world-DB column-type validation the install would run (C0).
    /// Seconds instead of a build; publish runs it first and refuses to build on a C0 error.
    /// </summary>
    public async Task<Report> PreflightAsync()
    {
        var packs = (await _store.ListPacksAsync()).Where(p => p.Enabled).ToList();
        var docs = await _store.DocsAsync(null, null, enabledOnly: true);
        var report = new Report { Packs = packs.Select(p => p.PackKey).ToList() };
        using var conn = _db.Mangos();
        await conn.OpenAsync();
        using (var admin = _db.Admin())
        {
            await admin.OpenAsync();
            try { _ = await WorldPackNpcReplacements.PrepareAsync(conn, admin, docs); }
            catch (Exception ex) { report.Findings.Add(new AuditFinding("C0", "error", "NPC replacements", ex.Message)); }
        }
        var audit = new WorldPackAudit(new WorldPackAudit.AuditInput
        {
            Stock = _ => null, Built = _ => null, MapDirs = new(), Docs = docs, Placements = new(),
            Facts = new DbFacts(conn),
            Mmaps = Path.Combine(_serverData, "mmaps"),   // G15 on the installed navmesh
        });
        report.Findings.AddRange(await Task.Run(audit.RunContent));
        try
        {
            await WorldPackBuildService.ValidateRowsAsync(conn, docs.Where(d => d.Kind.StartsWith("dbrow:") && !string.IsNullOrEmpty(d.Body))
                .Select(d => (d.Kind[6..], JsonNode.Parse(d.Body)!.AsObject())));
            report.Findings.Add(new("C0", "info", "world DB", $"{docs.Count(d => d.Kind.StartsWith("dbrow:"))} row(s) fit their live column types"));
        }
        catch (InvalidOperationException ex) { report.Findings.Add(new("C0", "error", "world DB", ex.Message)); }
        report.Findings.AddRange(ValidateDbcDocs(docs));
        report.Errors = report.Findings.Count(f => f.Severity == "error");
        report.Warnings = report.Findings.Count(f => f.Severity == "warn");
        return report;
    }

    /// <summary>The build's own DBC rules, checked before a build: every field index exists in the real
    /// DBC (field 0 is the id, taken from the doc key) and a cloneFrom row exists. Build #14 failed on this.</summary>
    private List<AuditFinding> ValidateDbcDocs(List<DocRow> docs)
    {
        var found = new List<AuditFinding>();
        var dbcDocs = docs.Where(d => d.Kind.StartsWith("dbc:") && !string.IsNullOrEmpty(d.Body)).ToList();
        if (dbcDocs.Count == 0) return found;
        using var stock = new VanillaArchiveSet(Path.Combine(_root, "work", "Data"));
        foreach (var group in dbcDocs.GroupBy(d => d.Kind[4..], StringComparer.OrdinalIgnoreCase))
        {
            string path = $@"DBFilesClient\{group.Key}.dbc";
            var bytes = stock.ReadFile(path);
            if (bytes == null) { found.Add(new("C0", "error", $"{group.Key}.dbc", "no such DBC in the stock archives")); continue; }
            var dbc = DbcWriterService.ReadDbc(bytes, path);
            var ids = dbc.GetAllRows().Select(r => r[0]).ToHashSet();
            foreach (var d in group)
            {
                var body = JsonNode.Parse(d.Body)!.AsObject();
                if (body["cloneFrom"] is JsonNode from && !ids.Contains((uint)from))
                    found.Add(new("C0", "error", $"{group.Key}.dbc row {d.DocKey}", $"cloneFrom {from} is not a row of the stock DBC"));
                foreach (var (index, _) in body["fields"]?.AsObject() ?? new JsonObject())
                    if (!int.TryParse(index, out int f) || f <= 0 || f >= dbc.FieldCount)
                        found.Add(new("C0", "error", $"{group.Key}.dbc row {d.DocKey}", $"field {index} does not exist (fields 1..{dbc.FieldCount - 1}; field 0 is the id from the doc key) - the build would fail"));
            }
        }
        return found;
    }

    /// <summary>Pack ids as whole numbers in mangosd's own complaints (since its last start).</summary>
    private List<AuditFinding> ScanServerLogs(List<DocRow> docs, DateTime? after)
    {
        var ids = new HashSet<string>();
        foreach (var d in docs)
        {
            // Only the id the pack OWNS (the reserved-range column), never the stock ids a row points at.
            if (d.Kind.StartsWith("dbrow:") && WorldPackContent.Tables.TryGetValue(d.Kind[6..], out var rule))
            {
                int at = Array.FindIndex(rule.Key, k => k.Equals(rule.RangeColumn, StringComparison.OrdinalIgnoreCase));
                var parts = d.DocKey.Split('|');
                if (at >= 0 && at < parts.Length) ids.Add(parts[at]);
            }
            if (d.Kind.StartsWith("dbc:") || d.Kind == "map") ids.Add(d.DocKey);
        }
        var found = new List<AuditFinding>();
        if (ids.Count == 0) return found;
        var number = new Regex(@"\d+", RegexOptions.Compiled);
        foreach (var file in new[] { "Server.log", "DBErrors.log" })
        {
            string path = Path.Combine(_logDir, file);
            if (!File.Exists(path)) { found.Add(new("L1", "warn", file, $"not found at {path}: server complaints unchecked")); continue; }
            var lines = ReadShared(path);
            // Only the current run: from the last "starting" banner (Server.log) or the whole file (DBErrors.log is rewritten per start).
            int from = 0;
            if (file == "Server.log")
                for (int i = lines.Count - 1; i >= 0; i--)
                    if (lines[i].Contains("Using configuration file", StringComparison.OrdinalIgnoreCase) || lines[i].Contains("<Ctrl-C> to stop", StringComparison.OrdinalIgnoreCase)) { from = i; break; }
            // Engine noise: a complaint whose SHAPE (numbers blanked, timestamp dropped) also appears for
            // content that is not the pack's is how mangosd always talks (e.g. every raid's
            // "[DungeonReset] ... ScheduleReset ... for unknown instance" at startup) - reported as info.
            string Shape(string l) => number.Replace(l.Length > 20 ? l[20..] : l, "#");
            bool Ours(string l) => number.Matches(l).Any(m => ids.Contains(m.Value) &&
                (m.Value.Length > 3 || l.Contains("map", StringComparison.OrdinalIgnoreCase)));
            var stockShapes = new HashSet<string>();
            for (int i = from; i < lines.Count; i++)
                if (Fatal.IsMatch(lines[i]) && !Ours(lines[i])) stockShapes.Add(Shape(lines[i]));
            int hits = 0;
            for (int i = from; i < lines.Count && hits < 60; i++)
            {
                string line = lines[i];
                // Small ids (map 800/801) are common numbers: only count them on a line about a map.
                if (!number.Matches(line).Any(m => ids.Contains(m.Value) &&
                        (m.Value.Length > 3 || line.Contains("map", StringComparison.OrdinalIgnoreCase)))) continue;
                // Our own diagnostics, and SuperUI bots logging in where they logged out (a test group left inside
                // a pack dungeon: "[AIBOT] Factory creating AiBotAI: ... map=801") - not complaints about content.
                if (line.Contains("[SUI]") || line.Contains("TELEMETRY") || line.Contains("[AIBOT]")) continue;
                hits++;
                // mangosd drops what it complains about: "ignored"/"nonexistent"/"skipped" lines mean the
                // content is NOT in the game (e.g. LoadMapTemplate's ghost-entrance line erases the map).
                bool dropped = Fatal.IsMatch(line);
                bool noise = stockShapes.Contains(Shape(line));
                found.Add(new("L1", noise ? "info" : dropped ? "error" : "warn", file,
                    (noise ? "(engine noise - stock content logs the same) " : "") + (line.Length > 400 ? line[..400] : line)));
            }
            if (hits == 0) found.Add(new("L1", "info", file, $"no line since the server start names a pack id ({ids.Count} ids watched)"));
        }
        return found;
    }

    private static readonly Regex Fatal = new(@"shutting down|Can't continue|ERROR|nonexistent|non existing|not listed|not exist|invalid|\bbad\b|ignored|skipped|not found|wrong",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<string> ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // Big logs: only the tail can belong to the current run.
        if (fs.Length > 32 * 1024 * 1024) fs.Seek(-32 * 1024 * 1024, SeekOrigin.End);
        using var sr = new StreamReader(fs);
        var list = new List<string>();
        string? l;
        while ((l = sr.ReadLine()) != null) list.Add(l);
        return list;
    }

    /// <summary>Existence lookups against the live world DB, cached per table.</summary>
    private sealed class DbFacts : IStockFacts
    {
        private readonly MySqlConnector.MySqlConnection _conn;
        private readonly Dictionary<string, HashSet<long>> _sets = new();
        private Dictionary<long, int>? _spawns;
        private ILookup<long, long>? _options;

        private static readonly Dictionary<string, (string table, string col)> Keys = new()
        {
            ["item_template"] = ("item_template", "entry"),
            ["spell_template"] = ("spell_template", "entry"),
            ["creature_template"] = ("creature_template", "entry"),
            ["quest_template"] = ("quest_template", "entry"),
            ["broadcast_text"] = ("broadcast_text", "entry"),
            ["npc_text"] = ("npc_text", "ID"),
            ["gossip_menu"] = ("gossip_menu", "entry"),
            ["creature_equip_template"] = ("creature_equip_template", "entry"),
            ["creature_loot_template"] = ("creature_loot_template", "entry"),
            ["creature_display_info_addon"] = ("creature_display_info_addon", "display_id"),
            ["gameobject_template"] = ("gameobject_template", "entry"),
        };

        public DbFacts(MySqlConnector.MySqlConnection conn) => _conn = conn;

        public bool Has(string table, long id)
        {
            if (!Keys.TryGetValue(table, out var k)) return true;
            if (!_sets.TryGetValue(table, out var set))
                _sets[table] = set = _conn.Query<long>($"SELECT DISTINCT `{k.col}` FROM `{k.table}`").ToHashSet();
            return set.Contains(id);
        }

        public int StockSpawns(long entry)
        {
            _spawns ??= _conn.Query<(long id, int n)>("SELECT id, COUNT(*) FROM creature GROUP BY id").ToDictionary(x => x.id, x => x.n);
            return _spawns.GetValueOrDefault(entry);
        }

        public bool HasGossipOption(long menuId, uint npcFlag)
        {
            _options ??= _conn.Query<(long menu, long flag)>("SELECT menu_id, npc_option_npcflag FROM gossip_menu_option").ToLookup(x => x.menu, x => x.flag);
            return _options[menuId].Any(f => (f & npcFlag) != 0);
        }
    }
}
