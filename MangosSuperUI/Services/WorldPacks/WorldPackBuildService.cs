using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MangosSuperUI.Models;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// Publishes the ENABLED World Content Packs (MSUIClient shared_docs/WORLD_BUILDER.md §2):
///
///  1. patch every touched stock ADT (sculpt + MODF/MDDF + MCRF) → <c>out/patch-7.MPQ</c>;
///  2. run the real extractors in <c>work/</c> over stock archives + patch-7
///     (MapExtractor -m, VMapExtractor -m, VMapAssembler, MoveMapGenerator per changed tile + neighbours);
///  3. install every output file that differs from the stock state into the server DataDir, keeping
///     the stock file in <c>baseline/</c> the first time it is replaced; files owned by the previous
///     publish that are no longer produced are restored from the baseline;
///  4. restart mangosd (scoped) so resident grids reload.
///
/// The toolchain was verified deterministic on 2026-09-26: re-extracting stock maps 0/1 reproduces
/// all 1705 .map and 929 vmap files and the Northshire mmtile byte-for-byte, so "differs from stock"
/// is exactly "changed by a pack".
/// </summary>
public sealed class WorldPackBuildService
{
    public const string PatchFileName = "patch-7.MPQ";
    public const string ManifestMpqPath = "WorldPacks\\build.json";
    /// <summary>The packs' areatrigger_teleport rows in the client's reference TSV format (MSUIClient
    /// AreaTriggerTeleportTable merges it over data/reference): pack portals get their names and
    /// destinations (loading-screen art, destination prewarm). A stock client ignores the file.</summary>
    public const string TeleportsMpqPath = "WorldPacks\\areatrigger_teleport.tsv";

    private void BuildTeleportTable(BuildState s, List<DocRow> docs, Dictionary<string, byte[]> files)
    {
        string[] cols = { "id", "patch", "name", "message", "required_level", "required_condition", "target_map",
                          "target_position_x", "target_position_y", "target_position_z", "target_orientation" };
        var lines = new List<string> { string.Join('\t', cols) };
        foreach (var d in docs.Where(d => d.Kind == "dbrow:areatrigger_teleport" && !string.IsNullOrEmpty(d.Body)))
        {
            var b = JsonNode.Parse(d.Body)!.AsObject();
            string Cell(string c) => (b[c] switch
            {
                JsonObject { } o when o["f"] is JsonNode f => f.ToString(),
                JsonNode n => n.ToString(),
                _ => "0",
            }).Replace('\t', ' ').Replace('\n', ' ');
            lines.Add(string.Join('\t', cols.Select(Cell)));
        }
        if (lines.Count == 1) return;
        files[TeleportsMpqPath] = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        Log(s, $"teleport table: {lines.Count - 1} pack portal(s) for the client");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly WorldPackStore _store;
    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly ProcessManagerService _process;
    private readonly WorldPackVerifier _verifier;
    private readonly ILogger<WorldPackBuildService> _logger;

    private readonly object _sync = new();
    private BuildState? _current;

    public string Root { get; }
    private readonly string? _serverLogPath;
    public string ServerData { get; }
    private string Work => Path.Combine(Root, "work");
    private string Tools => Path.Combine(Root, "tools");
    private string Baseline => Path.Combine(Root, "baseline");
    private string OutDir => Path.Combine(Root, "out");
    private string InstalledManifestPath => Path.Combine(Root, "installed.json");
    public string PatchPath => Path.Combine(OutDir, PatchFileName);

    public WorldPackBuildService(WorldPackStore store, ConnectionFactory db, AuditService audit,
        ProcessManagerService process, WorldPackVerifier verifier, IConfiguration config, ILogger<WorldPackBuildService> logger)
    {
        _store = store;
        _verifier = verifier;
        _db = db;
        _audit = audit;
        _process = process;
        _logger = logger;
        Root = config["WorldPacks:Root"] ?? "/home/wowvmangos/worldpacks";
        ServerData = config["Vmangos:ServerDataPath"] ?? "/home/wowvmangos/vmangos/run/data";
        _serverLogPath = Path.Combine(config["WorldPacks:ServerLogDir"] ?? "/home/wowvmangos/vmangos/run/bin", "Server.log");
    }

    // ═══════════════════════════════════════════════════════════════ status

    public sealed class BuildState
    {
        public int BuildId { get; init; }
        public string Status { get; set; } = "running";   // running | succeeded | failed
        public string Phase { get; set; } = "starting";
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; set; }
        public string? Error { get; set; }
        public string? MpqSha1 { get; set; }
        public long MpqSize { get; set; }
        public int FilesInstalled { get; set; }
        public int FilesRestored { get; set; }
        public int RowsChanged { get; set; }
        public bool ServerRestarted { get; set; }
        public int? VerifyErrors { get; set; }
        public int? VerifyWarnings { get; set; }
        public List<string> Log { get; } = new();
    }

    public object Snapshot()
    {
        lock (_sync)
        {
            if (_current == null) return new { running = false, lastBuild = LastBuildInfo() };
            return new
            {
                running = _current.Status == "running",
                _current.BuildId, _current.Status, _current.Phase, _current.StartedUtc, _current.FinishedUtc,
                _current.Error, _current.MpqSha1, _current.MpqSize, _current.FilesInstalled, _current.FilesRestored,
                _current.ServerRestarted, _current.VerifyErrors, _current.VerifyWarnings,
                log = _current.Log.TakeLast(60).ToList(),
                lastBuild = LastBuildInfo(),
            };
        }
    }

    /// <summary>What a client needs to decide whether its patch-7.MPQ is current.</summary>
    public object? LastBuildInfo()
    {
        if (!File.Exists(PatchPath)) return null;
        var sidecar = PatchPath + ".json";
        try
        {
            if (File.Exists(sidecar))
                return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(sidecar));
        }
        catch { /* fall through */ }
        return new { size = new FileInfo(PatchPath).Length };
    }

    // ═══════════════════════════════════════════════════════════════ start

    public async Task<(bool ok, int buildId, string? error)> StartAsync(string op, string? ip, bool restartServer)
    {
        await _store.EnsureSchemaAsync();
        lock (_sync)
        {
            if (_current is { Status: "running" })
                return (false, _current.BuildId, $"build #{_current.BuildId} is already running");
        }

        var packs = (await _store.ListPacksAsync()).Where(p => p.Enabled).ToList();
        int buildId;
        using (var conn = _db.Admin())
            buildId = await conn.ExecuteScalarAsync<int>(
                "INSERT INTO wp_build (status, operator, packs) VALUES ('running', @op, @packs); SELECT LAST_INSERT_ID();",
                new { op, packs = string.Join(",", packs.Select(p => p.PackKey)) });

        var state = new BuildState { BuildId = buildId };
        lock (_sync) _current = state;
        _ = Task.Run(() => RunAsync(state, packs, op, ip, restartServer));
        return (true, buildId, null);
    }

    private void Log(BuildState s, string line)
    {
        var stamped = $"{DateTime.UtcNow:HH:mm:ss} {line}";
        lock (_sync) s.Log.Add(stamped);
        _logger.LogInformation("[worldpack #{Build}] {Line}", s.BuildId, line);
    }

    // ═══════════════════════════════════════════════════════════════ pipeline

    private async Task RunAsync(BuildState s, List<PackRow> packs, string op, string? ip, bool restartServer)
    {
        try
        {
            Log(s, $"publishing {packs.Count} enabled pack(s): {string.Join(", ", packs.Select(p => p.PackKey))}");
            Directory.CreateDirectory(Path.Combine(Work, "Data"));
            Directory.CreateDirectory(Baseline);
            Directory.CreateDirectory(OutDir);

            // ── 0. pre-flight (content + column types, seconds) ─────────────────
            s.Phase = "preflight";
            var pre = await _verifier.PreflightAsync();
            Log(s, $"preflight: {pre.Errors} error(s), {pre.Warnings} warning(s) in pack content");
            foreach (var f in pre.Findings.Where(f => f.Severity == "error").Take(10))
                Log(s, $"preflight ERROR {f.Check} {f.Subject}: {f.Message}");
            if (pre.Findings.Any(f => f.Check == "C0" && f.Severity == "error"))
                throw new InvalidOperationException("pre-flight: world-DB rows would be rejected (see C0) — nothing was built or installed");

            // ── 1. client patch ────────────────────────────────────────────────
            s.Phase = "patch";
            var (files, touched, publishedSculpt, publishedPlacements) = await BuildPatchFilesAsync(s, packs);
            var manifest = new
            {
                buildId = s.BuildId,
                createdUtc = DateTime.UtcNow,
                packs = packs.Select(p => new { p.Id, p.PackKey, p.Name }).ToList(),
                adts = touched.Select(t => $"{t.map}:{t.col}_{t.row}").ToList(),
                // Tiles whose ground a pack STAMPED (not just sculpted): the client paints their minimap images
                // over the continent's painted world map, so new land shows up there.
                stamps = _stamps.Keys.OrderBy(k => k).Select(k => $"{k.map}:{k.col}_{k.row}").ToList(),
            };
            var manifestJson = JsonSerializer.Serialize(manifest, Json);
            files[ManifestMpqPath] = Encoding.UTF8.GetBytes(manifestJson);

            var builder = new MpqBuilderService(null, null);
            foreach (var (path, data) in files) builder.AddFile(path, data);
            if (!builder.Build(PatchPath)) throw new InvalidOperationException("MPQ build/verify failed");
            byte[] mpq = await File.ReadAllBytesAsync(PatchPath);
            s.MpqSha1 = Convert.ToHexString(SHA1.HashData(mpq)).ToLowerInvariant();
            s.MpqSize = mpq.Length;
            Log(s, $"{PatchFileName}: {files.Count} file(s), {mpq.Length / 1024} KiB, sha1 {s.MpqSha1}");

            string workPatch = Path.Combine(Work, "Data", PatchFileName);
            if (File.Exists(workPatch) || IsSymlink(workPatch)) File.Delete(workPatch);
            File.CreateSymbolicLink(workPatch, PatchPath);

            // ── 2. server data ─────────────────────────────────────────────────
            var previous = LoadInstalled();
            var maps = touched.Select(t => t.map).Distinct().OrderBy(m => m).ToList();
            var produced = new Dictionary<string, string>();   // rel path under DataDir → work file
            var removed = new HashSet<string>();               // rel paths the build wants absent

            var keep = new HashSet<string>();   // installed files a previous publish produced and this one reuses
            var fingerprints = MapFingerprints();
            if (maps.Count > 0)
            {
                s.Phase = "extract";
                await ExtractAsync(s, maps);
                s.Phase = "compare";
                var changedTiles = CollectChanged(s, maps, previous, produced, removed);
                // Geometry changed exactly where a pack patched an ADT (sculpt, MODF, MDDF) or where a
                // previous publish did (restored tiles need their stock navmesh back - the baseline has it).
                foreach (var t in touched) changedTiles.Add(t);
                // A map whose patched ADTs are byte-identical to the last successful publish has the
                // same navmesh: keep the installed tiles instead of regenerating them.
                var lastPrints = LoadNavHashes();
                foreach (int m in maps)
                {
                    if (!lastPrints.TryGetValue(m, out var last) || !fingerprints.TryGetValue(m, out var now) || last != now) continue;
                    changedTiles.RemoveWhere(t => t.map == m);
                    foreach (var rel in previous.Keys.Where(k => k.StartsWith($"mmaps/{m:D3}")))
                        keep.Add(rel);
                    Log(s, $"navmesh: map {m} unchanged since the last publish — keeping its installed tiles");
                }
                s.Phase = "navmesh";
                await BuildNavMeshAsync(s, maps.Where(m => changedTiles.Any(t => t.map == m)).ToList(), changedTiles, previous, produced, keep);
            }
            foreach (var (rel, local) in _serverDbcs)
                if (!Same(File.ReadAllBytes(local), StockBytes(rel, previous))) produced[rel] = local;

            // ── 3. install ─────────────────────────────────────────────────────
            s.Phase = "install";
            Install(s, previous, produced, removed, keep);
            s.Phase = "database";
            s.RowsChanged = await ApplyDbRowsAsync(s);

            // ── 4. restart ─────────────────────────────────────────────────────
            if (restartServer && (s.FilesInstalled > 0 || s.FilesRestored > 0 || s.RowsChanged > 0))
            {
                s.Phase = "restart";
                Log(s, "restarting mangosd so resident grids reload");
                var r = await _process.RestartMangosdAsync();
                Log(s, $"mangosd restart: {r.Trim()}");
                s.ServerRestarted = true;
            }
            else if (!restartServer)
                Log(s, "server restart skipped by request — new collision/navmesh loads with the next restart");

            await _store.RecordPublishedAsync(s.BuildId, publishedSculpt, publishedPlacements);
            await File.WriteAllTextAsync(NavHashPath, JsonSerializer.Serialize(fingerprints, Json));
            await File.WriteAllTextAsync(NavTileHashPath, JsonSerializer.Serialize(TileFingerprints(), Json));
            await File.WriteAllTextAsync(PatchPath + ".json", JsonSerializer.Serialize(new
            {
                buildId = s.BuildId, sha1 = s.MpqSha1, size = s.MpqSize, createdUtc = DateTime.UtcNow,
                packs = packs.Select(p => p.PackKey).ToList(),
            }, Json));

            // ── 5. verify ──────────────────────────────────────────────────────
            s.Phase = "verify";
            if (s.ServerRestarted) await WaitForWorldAsync(s, TimeSpan.FromMinutes(3));
            var report = await _verifier.RunAsync(s.BuildId);
            s.VerifyErrors = report.Errors;
            s.VerifyWarnings = report.Warnings;
            Log(s, $"verify: {report.Errors} error(s), {report.Warnings} warning(s), {report.Findings.Count} finding(s) — GET /WorldPacks/VerifyReport");
            foreach (var f in report.Findings.Where(f => f.Severity == "error").Take(20))
                Log(s, $"verify ERROR {f.Check} {f.Subject}: {f.Message}");

            s.Status = "succeeded";
            s.Phase = "done";
            Log(s, $"done: {s.FilesInstalled} installed, {s.FilesRestored} restored");
        }
        catch (Exception ex)
        {
            s.Status = "failed";
            s.Error = ex.Message;
            Log(s, "FAILED: " + ex.Message);
            _logger.LogError(ex, "World pack build #{Build} failed", s.BuildId);
        }
        finally
        {
            s.FinishedUtc = DateTime.UtcNow;
            string log;
            lock (_sync) log = string.Join("\n", s.Log);
            try
            {
                using var conn = _db.Admin();
                await conn.ExecuteAsync(@"
UPDATE wp_build SET status = @Status, finished_at = NOW(), mpq_sha1 = @MpqSha1, mpq_size = @MpqSize, log = @log
WHERE id = @BuildId", new { s.Status, s.MpqSha1, s.MpqSize, log, s.BuildId });
                await _audit.LogAsync(new AuditEntry
                {
                    Operator = op, OperatorIp = ip, Category = WorldPackStore.AuditCategory, Action = "publish",
                    TargetType = "wp_build", TargetName = $"build #{s.BuildId}", TargetId = s.BuildId,
                    StateAfter = JsonSerializer.Serialize(new
                    {
                        s.Status, s.MpqSha1, s.MpqSize, s.FilesInstalled, s.FilesRestored, s.ServerRestarted,
                        packs = packs.Select(p => p.PackKey),
                    }, Json),
                    Success = s.Status == "succeeded",
                    Notes = s.Error,
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "World pack build bookkeeping failed"); }
        }
    }

    /// <summary>Wait until the restarted mangosd writes "World initialized." (its logs then describe THIS content).</summary>
    private async Task WaitForWorldAsync(BuildState s, TimeSpan timeout)
    {
        string log = _serverLogPath!;
        var until = DateTime.UtcNow + timeout;
        var started = s.StartedUtc;
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(log) > started)
                {
                    using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    string text = await sr.ReadToEndAsync();
                    if (text.Contains("World initialized.")) { Log(s, "mangosd is up (World initialized)"); return; }
                }
            }
            catch (IOException) { }
            await Task.Delay(3000);
        }
        Log(s, "mangosd did not report 'World initialized.' in time — verifying anyway");
    }

    // ═══════════════════════════════════════════════════════════════ 1. patch

    private async Task<(Dictionary<string, byte[]> files, List<(int map, int col, int row)> touched,
        Dictionary<(int map, int col, int row), Dictionary<int, float>> sculpt, List<int> placements)>
        BuildPatchFilesAsync(BuildState s, List<PackRow> packs)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        _mapAdtBytes = new();
        _serverDbcs = new();
        var sculpt = new Dictionary<(int map, int col, int row), Dictionary<int, float>>();
        var surfaceSculpt = new Dictionary<(int map, int col, int row), Dictionary<int, float>>();
        var wmos = new Dictionary<(int map, int col, int row), List<(PlacementRow p, Vector3 pos, Vector3 min, Vector3 max)>>();
        var m2s = new Dictionary<(int map, int col, int row), List<(PlacementRow p, Vector3 pos, float radius)>>();
        var includedPlacements = new List<int>();
        if (packs.Count == 0) return (files, new(), sculpt, includedPlacements);

        using var stock = new VanillaArchiveSet(Path.Combine(Work, "Data"));
        var mapDirs = MapDirectories(stock);
        var docs = await _store.DocsAsync(null, null, enabledOnly: true);
        BuildNewMaps(s, stock, docs, mapDirs, files);
        var mapDocs = docs.Concat(WorldPackWorldMap.AreaDocuments(docs)).ToList();
        BuildDbcs(s, stock, mapDocs.Concat(ZoneWorldMapAreas(s, mapDocs)).ToList(), files);
        WorldPackWorldMap.BuildAssets(stock.ReadFile, docs, files, message => Log(s, message));
        BuildTeleportTable(s, docs, files);

        var placements = new List<PlacementRow>();
        foreach (var pack in packs)
            placements.AddRange(await _store.PlacementsAsync(pack.Id, null));
        var sculptMaps = new HashSet<int>();
        using (var conn = _db.Admin())
            foreach (var m in await conn.QueryAsync<int>(
                "SELECT DISTINCT s.map_id FROM (SELECT pack_id,map_id FROM wp_sculpt UNION SELECT pack_id,map_id FROM wp_surface_sculpt) s JOIN wp_pack p ON p.id = s.pack_id WHERE p.enabled = 1"))
                sculptMaps.Add(m);

        foreach (int map in sculptMaps)
        {
            foreach (var t in await _store.SculptAsync(null, map, enabledOnly: true, surface: false))
                sculpt[(map, t.Col, t.Row)] = t.Deltas;
            foreach (var t in await _store.SculptAsync(null, map, enabledOnly: true, surface: true))
                surfaceSculpt[(map, t.Col, t.Row)] = t.Deltas;
        }

        var wmoBounds = new Dictionary<string, (Vector3, Vector3)?>(StringComparer.OrdinalIgnoreCase);
        var m2Bounds = new Dictionary<string, (Vector3, Vector3)?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in placements)
        {
            if (!mapDirs.ContainsKey(p.MapId)) throw new InvalidOperationException($"placement #{p.Id}: unknown map {p.MapId}");
            var pos = WorldCoords.WorldToPlacement(new Vector3(p.PosX, p.PosY, p.PosZ));
            var rot = new Vector3(p.RotX, p.RotY, p.RotZ);
            if (p.Kind == "wmo")
            {
                if (!wmoBounds.TryGetValue(p.ModelPath, out var b))
                    wmoBounds[p.ModelPath] = b = ModelBounds.Wmo(stock.ReadFile(p.ModelPath));
                if (b == null) throw new InvalidOperationException($"placement #{p.Id}: WMO not found in stock archives: {p.ModelPath}");
                var (min, max) = ModelBounds.WmoExtents(b.Value.Item1, b.Value.Item2, pos, rot);
                int c0 = (int)MathF.Floor(min.X / WorldCoords.Tile), c1 = (int)MathF.Floor(max.X / WorldCoords.Tile);
                int r0 = (int)MathF.Floor(min.Z / WorldCoords.Tile), r1 = (int)MathF.Floor(max.Z / WorldCoords.Tile);
                for (int c = Math.Max(c0, 0); c <= Math.Min(c1, 63); c++)
                    for (int r = Math.Max(r0, 0); r <= Math.Min(r1, 63); r++)
                        Add(wmos, (p.MapId, c, r), (p, pos, min, max));
            }
            else
            {
                string m2Path = Path.ChangeExtension(p.ModelPath, ".m2");
                if (!m2Bounds.TryGetValue(m2Path, out var b))
                    m2Bounds[m2Path] = b = ModelBounds.M2(stock.ReadFile(m2Path));
                if (b == null) throw new InvalidOperationException($"placement #{p.Id}: M2 not found in stock archives: {m2Path}");
                float radius = MathF.Max(b.Value.Item1.Length(), b.Value.Item2.Length()) * p.Scale;
                Add(m2s, (p.MapId, (int)(pos.X / WorldCoords.Tile), (int)(pos.Z / WorldCoords.Tile)), (p, pos, radius));
            }
            includedPlacements.Add(p.Id);
        }

        var paths = docs.Where(d => d.Kind == "path").Select(d => GradedPath.Parse(d.DocKey, d.Body)).ToList();
        var touched = sculpt.Keys.Concat(surfaceSculpt.Keys).Concat(wmos.Keys).Concat(m2s.Keys).Concat(_baseAdts.Keys)
            .Concat(paths.SelectMany(p => p.Tiles().Select(t => (p.Map, t.col, t.row))))
            .Where(k => mapDirs.ContainsKey(k.Item1)).Distinct().OrderBy(k => k).ToList();
        var present = new List<(int map, int col, int row)>();
        var built = new Dictionary<(int map, int col, int row), AdtDocument>();
        foreach (var key in touched)
        {
            var (map, col, row) = key;
            string adtPath = WorldCoords.AdtPath(mapDirs[map], col, row);
            AdtDocument doc;
            if (_baseAdts.TryGetValue(key, out var generated)) doc = generated;
            else
            {
                byte[]? bytes = stock.ReadFile(adtPath);
                if (bytes == null)
                {
                    Log(s, $"skip {adtPath}: no ADT there (placement extends past the terrain)");
                    continue;
                }
                doc = AdtDocument.Parse(bytes, col, row);
            }
            int moved = sculpt.TryGetValue(key, out var d) ? doc.ApplySculpt(d) : 0;
            foreach (var (p, pos, min, max) in wmos.GetValueOrDefault(key) ?? new())
                doc.AddWmo(p.ModelPath, p.UniqueId, pos, new Vector3(p.RotX, p.RotY, p.RotZ), min, max, (ushort)p.DoodadSet);
            // A placed building clears the ADT props (stock or stamped trees, straw, rocks) standing inside
            // its group boxes - verifier G3. Pack-placed M2s are deliberate and stay (G3 still reports them).
            var inside = (wmos.GetValueOrDefault(key) ?? new()).SelectMany(w =>
            {
                var m = WorldPackGeometry.WmoMatrix(w.pos, new Vector3(w.p.RotX, w.p.RotY, w.p.RotZ));
                return WorldPackGeometry.WmoGroups(stock.ReadFile(w.p.ModelPath)).Select(g => Obb.FromLocal(g.min, g.max, m));
            }).ToList();
            // (Pack M2 placements are appended below, after this, so they are never swept.)
            int cleared = inside.Count == 0 ? 0 : doc.DropDoodads((_, dpos) => inside.Any(b => b.Depth(dpos) > 0.3f));
            if (cleared > 0) Log(s, $"{adtPath}: cleared {cleared} ADT prop(s) standing inside placed buildings");
            foreach (var (p, pos, radius) in m2s.GetValueOrDefault(key) ?? new())
                doc.AddDoodad(Path.ChangeExtension(p.ModelPath, ".mdx"), p.UniqueId, pos, new Vector3(p.RotX, p.RotY, p.RotZ), p.Scale, radius);
            built[key] = doc;
            present.Add(key);
            Log(s, $"{adtPath}: {moved} height(s), +{wmos.GetValueOrDefault(key)?.Count ?? 0} WMO, +{m2s.GetValueOrDefault(key)?.Count ?? 0} M2");
        }
        StitchSeamsAndCarryWater(s, stock, mapDirs, built, sculpt, paths);
        WorldPackCoast.Apply(docs, stock, mapDirs, built, placements, message => Log(s, message));
        // Human edits are deltas to the finished surface, after all absolute shaping and stitching.
        // Shared border samples are mirrored at save time; Write rebuilds changed normals and minimaps see final heights.
        WorldPackSculptLayers.ApplySurface(built, surfaceSculpt);
        foreach (var key in present)
        {
            var (map, col, row) = key;
            string adtPath = WorldCoords.AdtPath(mapDirs[map], col, row);
            files[adtPath] = built[key].Write();
            if (!_mapAdtBytes.TryGetValue(map, out var list)) _mapAdtBytes[map] = list = new();
            list.Add((col, row, files[adtPath]));
        }
        BuildMinimaps(s, stock, docs, mapDirs, files, built, present);
        return (files, present, WorldPackSculptLayers.Totals(sculpt, surfaceSculpt), includedPlacements);
    }

    /// <summary>
    /// Seams between a stamp and ground it was not cut from. Every edge of a stitched stamp that faces a tile it
    /// was NOT cut next to (a stock tile, a sculpted one, a stamp from another source) is a seam: a world-space
    /// segment carrying that neighbour's FINAL edge heights. Every stamped vertex then blends toward every seam
    /// within its band (smoothstep: the seam's height at the edge, untouched at the band's end), in one global
    /// order - so a vertex shared by two stamps sits at the same world point and gets the same result, the block
    /// has no crack inside it (G9), and a seam that ends (the map's edge) fades out instead of stepping.
    /// The stamp's own sculpt is already in (seams are exact; 2026-09-27: sculpt after stitch reopened them).
    /// Then, on a stock continent, chunks the new ground takes below the stock tile's water get that water.
    /// </summary>
    private void StitchSeamsAndCarryWater(BuildState s, VanillaArchiveSet stock, Dictionary<int, string> mapDirs,
        Dictionary<(int map, int col, int row), AdtDocument> built,
        Dictionary<(int map, int col, int row), Dictionary<int, float>> sculpt, List<GradedPath> paths)
    {
        var stockCache = new Dictionary<(int, int, int), AdtDocument?>();
        AdtDocument? Stock((int map, int col, int row) k)
        {
            if (stockCache.TryGetValue(k, out var d)) return d;
            var bytes = stock.ReadFile(WorldCoords.AdtPath(mapDirs[k.map], k.col, k.row));
            return stockCache[k] = bytes == null ? null : AdtDocument.Parse(bytes, k.col, k.row);
        }
        const float T = WorldCoords.Tile, unit = WorldCoords.Tile / 128f;
        var finalHeights = built.ToDictionary(kv => kv.Key, kv => kv.Value.OuterHeights());

        var seams = new List<(int Map, Vector2 A, Vector2 B, float[] Targets, float Band)>();
        foreach (var (key, stamp) in _stamps.OrderBy(k => k.Key))
        {
            if (stamp.Stitch <= 0 || !built.ContainsKey(key)) continue;
            var (map, col, row) = key;
            bool stockMap = !_newMaps.Contains(map);
            float x0 = (32 - row) * T, y0 = (32 - col) * T;    // north-west corner (x north, y west)
            // (neighbour offset, seam from A to B = the edge vertices k = 0..128, the neighbour's facing vertex k)
            var edges = new (int dc, int dr, Vector2 a, Vector2 b, Func<int, int> theirs)[]
            {
                (0, -1, new(x0, y0), new(x0, y0 - T), k => 128 * 129 + k),          // north ↔ their south row
                (0, +1, new(x0 - T, y0), new(x0 - T, y0 - T), k => k),              // south ↔ their north row
                (-1, 0, new(x0, y0), new(x0 - T, y0), k => k * 129 + 128),          // west  ↔ their east column
                (+1, 0, new(x0, y0 - T), new(x0 - T, y0 - T), k => k * 129),        // east  ↔ their west column
            };
            foreach (var (dc, dr, a, b, theirs) in edges)
            {
                var nk = (map, col + dc, row + dr);
                if (_stamps.TryGetValue(nk, out var ns) && string.Equals(ns.SourceMap, stamp.SourceMap, StringComparison.OrdinalIgnoreCase) &&
                    ns.SourceCol - stamp.SourceCol == dc && ns.SourceRow - stamp.SourceRow == dr)
                    continue;                                                       // cut from the adjacent source tile: contiguous
                float[]? nh = finalHeights.TryGetValue(nk, out var fh) ? fh : stockMap ? Stock(nk)?.OuterHeights() : null;
                if (nh == null) continue;                                           // nothing there (the map's edge)
                var targets = new float[129];
                for (int k = 0; k <= 128; k++) targets[k] = nh[theirs(k)];
                seams.Add((map, a, b, targets, stamp.Stitch));
            }
        }

        foreach (var (key, stamp) in _stamps.OrderBy(k => k.Key))
        {
            // Stitch 0 = keep this stamp's ground exactly (an in-place edit: a healed hole, an area re-tag). Its
            // stitched neighbours meet IT; blending it toward their seams opened a 7.5 yd crack against the
            // untouched stock tile on its other side (build #25, tile 29,34 vs 28,34).
            if (stamp.Stitch <= 0 || !built.TryGetValue(key, out var doc)) continue;
            var (map, col, row) = key;
            float x0 = (32 - row) * T, y0 = (32 - col) * T;
            var near = seams.Where(m => m.Map == map &&
                MathF.Max(MathF.Min(m.A.X, m.B.X) - m.Band, x0 - T) <= MathF.Min(MathF.Max(m.A.X, m.B.X) + m.Band, x0) &&
                MathF.Max(MathF.Min(m.A.Y, m.B.Y) - m.Band, y0 - T) <= MathF.Min(MathF.Max(m.A.Y, m.B.Y) + m.Band, y0)).ToList();
            if (near.Count > 0)
            {
                var h = finalHeights[key];
                var deltas = new Dictionary<int, float>();
                for (int gr = 0; gr <= 128; gr++)
                    for (int gc = 0; gc <= 128; gc++)
                    {
                        var p = new Vector2(x0 - gr * unit, y0 - gc * unit);
                        float start = h[gr * 129 + gc];
                        // All seams in range TOGETHER: a vertex ON a seam takes exactly that seam's height (a later
                        // seam must not pull it off - build #19's corner cracks); elsewhere the nearest seams dominate
                        // (inverse-square weights) and the strongest band weight says how far to move.
                        double onSeam = 0, sumW = 0, sumT = 0; int onCount = 0; float maxW = 0f;
                        foreach (var m in near)
                        {
                            var ab = m.B - m.A;
                            float t = Math.Clamp(Vector2.Dot(p - m.A, ab) / ab.LengthSquared(), 0f, 1f);
                            float dist = Vector2.Distance(p, m.A + ab * t);
                            if (dist >= m.Band) continue;
                            float k = t * 128f;
                            int k0 = Math.Min((int)k, 127);
                            float target = m.Targets[k0] + (m.Targets[k0 + 1] - m.Targets[k0]) * (k - k0);
                            if (dist < 0.01f) { onSeam += target; onCount++; continue; }
                            float u = dist / m.Band, w = 1f - u * u * (3f - 2f * u);   // 1 at the seam, 0 at the band's end
                            double iw = w / ((double)dist * dist);
                            sumW += iw; sumT += iw * target; maxW = MathF.Max(maxW, w);
                        }
                        float cur = onCount > 0 ? (float)(onSeam / onCount)
                                  : sumW > 0 ? start + ((float)(sumT / sumW) - start) * maxW : start;
                        if (cur != start) deltas[gr * 129 + gc] = cur - start;
                    }
                if (deltas.Count > 0)
                {
                    doc.ApplySculpt(deltas);
                    Log(s, $"tile {map}:{col},{row}: stitched {deltas.Count} vertices toward {near.Count} seam(s)");
                }
            }
        }
        // Graded paths last (after the seams): absolute heights, the same for every tile they cross.
        foreach (var path in paths)
            foreach (var (col, row) in path.Tiles())
                if (built.TryGetValue((path.Map, col, row), out var doc))
                {
                    int moved = doc.ApplySculpt(path.Deltas(col, row, doc.OuterHeights()));
                    int cleared = path.Clear ? doc.DropDoodads((_, pos) => path.InLane(pos)) : 0;
                    if (moved > 0 || cleared > 0) Log(s, $"path {path.Name}: tile {path.Map}:{col},{row} graded ({moved} height(s), {cleared} prop(s) cleared from the lane)");
                }
        // Water after everything that moves ground: a stamp's chunks that now dip under the stock sea take it.
        foreach (var (key, _) in _stamps.OrderBy(k => k.Key))
            if (built.TryGetValue(key, out var doc) && !_newMaps.Contains(key.map) && Stock(key) is { } original)
            {
                int filled = doc.CarryLiquidFrom(original);
                if (filled > 0) Log(s, $"tile {key.map}:{key.col},{key.row}: {filled} chunk(s) under the stock sea level took its water");
            }
    }

    private static void Add<TK, TV>(Dictionary<TK, List<TV>> d, TK k, TV v) where TK : notnull
    {
        if (!d.TryGetValue(k, out var list)) d[k] = list = new List<TV>();
        list.Add(v);
    }

    /// <summary>Map id → directory name, from the stock Map.dbc (WDBC: id = field 0, directory = field 1).</summary>
    public static Dictionary<int, string> MapDirectories(VanillaArchiveSet stock)
    {
        var dbc = stock.ReadFile("DBFilesClient\\Map.dbc") ?? throw new InvalidOperationException("Map.dbc missing");
        int records = BitConverter.ToInt32(dbc, 4), recordSize = BitConverter.ToInt32(dbc, 12);
        int strings = 20 + records * recordSize;
        var result = new Dictionary<int, string>();
        for (int i = 0; i < records; i++)
        {
            int at = 20 + i * recordSize;
            int id = BitConverter.ToInt32(dbc, at);
            int s = strings + BitConverter.ToInt32(dbc, at + 4);
            int e = s;
            while (e < dbc.Length && dbc[e] != 0) e++;
            result[id] = Encoding.ASCII.GetString(dbc, s, e - s);
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════ 1b. new maps + DBC rows

    /// <summary>Generated base ADTs of pack maps for the current build (sculpt/placements apply on top).</summary>
    private Dictionary<(int map, int col, int row), AdtDocument> _baseAdts = new();
    /// <summary>Every stamped tile of this build: its source (contiguity = no seam) and its stitch band (yd).</summary>
    private Dictionary<(int map, int col, int row), (string SourceMap, int SourceCol, int SourceRow, float Stitch)> _stamps = new();
    /// <summary>Map ids that exist only because a pack defines them (whole-map navmesh builds).</summary>
    private HashSet<int> _newMaps = new();

    private void BuildNewMaps(BuildState s, VanillaArchiveSet stock, List<DocRow> docs, Dictionary<int, string> mapDirs,
        Dictionary<string, byte[]> files)
    {
        _baseAdts = new();
        _stamps = new();
        _newMaps = new();
        foreach (var d in docs.Where(d => d.Kind == "map"))
        {
            var m = JsonNode.Parse(d.Body)!.AsObject();
            int id = (int)m["mapId"]!;
            string dir = (string)m["directory"]!;
            if (mapDirs.ContainsKey(id)) throw new InvalidOperationException($"map {id} already exists in the stock Map.dbc");
            mapDirs[id] = dir;
            _newMaps.Add(id);
        }
        foreach (var group in docs.Where(d => d.Kind == "tile").GroupBy(d => (int)JsonNode.Parse(d.Body)!["map"]!))
        {
            int map = group.Key;
            // A stamp on a stock continent (0/1) REPLACES that stock tile while the pack is enabled.
            bool stockMap = !_newMaps.Contains(map);
            if (stockMap && map is not (0 or 1)) throw new InvalidOperationException($"tile docs for map {map}, which no enabled pack defines");
            string dir = mapDirs[map];
            foreach (var d in group)
            {
                var t = JsonNode.Parse(d.Body)!.AsObject();
                int col = (int)t["col"]!, row = (int)t["row"]!;
                string srcDir = (string)t["sourceMap"]!;
                int sc = (int)t["sourceCol"]!, sr = (int)t["sourceRow"]!;
                bool keepDoodads = (bool?)t["keepDoodads"] ?? (bool?)t["keepObjects"] ?? true;
                bool keepWmos = (bool?)t["keepWmos"] ?? (bool?)t["keepObjects"] ?? false;
                uint area = (uint?)t["areaId"] ?? 0;
                var bytes = stock.ReadFile(WorldCoords.AdtPath(srcDir, sc, sr))
                            ?? throw new InvalidOperationException($"tile {map}:{col},{row}: no stock ADT {srcDir}_{sc}_{sr}");
                var doc = AdtDocument.Parse(bytes, sc, sr);
                int dCol = col - sc, dRow = row - sr;
                // An IDENTITY stamp (a stock tile edited in place - drop a prop, heal a hole) keeps the stock ids:
                // a wall piece that spans into the untouched neighbour tile must stay ONE object, not draw twice.
                bool identity = dCol == 0 && dRow == 0 && string.Equals(srcDir, dir, StringComparison.OrdinalIgnoreCase);
                // Same source object + same stamp offset → same id, so a building spanning two stamped
                // tiles stays one spawn. High bit keeps these clear of stock and placement ids.
                doc.Relocate(col, row, keepDoodads, keepWmos, identity ? uid => uid
                    : uid => 0x8000_0000u | (uint)((uid * 2654435761u + (uint)(dCol * 131 + dRow) * 40503u + (uint)map * 7919u) & 0x7FFF_FFFF));
                if (t["dropWmos"] is JsonArray drops && drops.Count > 0)
                {
                    var names = drops.Select(n => n!.ToString()).ToList();
                    bool Dropped(string p) => names.Any(n => p.EndsWith(n, StringComparison.OrdinalIgnoreCase));
                    // The props that stood inside a dropped building (its fences, lamps, the trees in its
                    // courtyard) go with it — the verifier's G6 reports any that survive.
                    var gone = doc.WmoPlacementsFull().Where(w => Dropped(w.path)).SelectMany(w =>
                    {
                        var m = WorldPackGeometry.WmoMatrix(w.pos, w.rot);
                        return WorldPackGeometry.WmoGroups(stock.ReadFile(w.path)).Select(g => Obb.FromLocal(g.min, g.max, m));
                    }).ToList();
                    int wmosGone = doc.DropWmos(Dropped);
                    int propsGone = doc.DropDoodads((_, pos) => gone.Any(b => b.Depth(pos) > 0f));
                    if (wmosGone > 0) Log(s, $"tile {map}:{col},{row}: dropped {wmosGone} WMO(s) and {propsGone} prop(s) inside them");
                }
                // Props dropped by name (the Greymane Wall's closed portcullis: a pack raises a copy instead).
                if (t["dropDoodads"] is JsonArray dropM2 && dropM2.Count > 0)
                {
                    var names = dropM2.Select(n => n!.ToString()).ToList();
                    int gone = doc.DropDoodads((path, _) => names.Any(n => path.EndsWith(n, StringComparison.OrdinalIgnoreCase)));
                    Log(s, $"tile {map}:{col},{row}: dropped {gone} prop(s) by name");
                }
                if (area != 0) doc.SetAreaId(area);
                if (t["areaPaint"] is JsonArray paints)
                    foreach (var p in paints.OfType<JsonObject>())
                        doc.PaintArea((uint)p["areaId"]!, (float)p["x"]!, (float)p["y"]!, (float)p["radius"]!);
                // "from>to[,from>to]": re-tag one area's chunks (a sea the pack turned into land joins the zone).
                if (t["areaReplace"]?.ToString() is { Length: > 0 } replace)
                    foreach (var pair in replace.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (pair.Split('>') is [var f, var to] && uint.TryParse(f, out uint fromArea) && uint.TryParse(to, out uint toArea))
                            Log(s, $"tile {map}:{col},{row}: {doc.ReplaceArea(fromArea, toArea)} chunk(s) re-tagged area {fromArea} -> {toArea}");
                // Stock hole squares the building does not fill (verifier tier 2 walk-off): close them.
                if (t["healHoles"] is JsonArray heals && heals.Count > 0)
                {
                    int healed = heals.OfType<JsonObject>().Count(h => doc.HealHoleAt((float)h["x"]!, (float)h["y"]!));
                    Log(s, $"tile {map}:{col},{row}: healed {healed} of {heals.Count} terrain hole square(s)");
                }
                _baseAdts[(map, col, row)] = doc;
                _stamps[(map, col, row)] = (srcDir, sc, sr, (float?)t["stitch"] ?? 0f);
            }
            if (stockMap)
            {
                // The stock WDT already lists these tiles; only tiles that stock has no ADT for would need it.
                var missing = group.Select(d => JsonNode.Parse(d.Body)!).Where(t =>
                    stock.ReadFile(WorldCoords.AdtPath(dir, (int)t["col"]!, (int)t["row"]!)) == null).ToList();
                if (missing.Count > 0) throw new InvalidOperationException($"{missing.Count} stamp(s) on map {map} where the stock WDT has no tile - extend the continent's WDT first");
                Log(s, $"map {map} ({dir}): {group.Count()} stock tile(s) replaced by stamps");
                continue;
            }
            files[WorldCoords.WdtPath(dir)] = AdtDocument.BuildWdt(group.Select(d =>
            {
                var t = JsonNode.Parse(d.Body)!;
                return ((int)t["col"]!, (int)t["row"]!);
            }));
            Log(s, $"new map {map} ({dir}): {group.Count()} stamped tile(s) + WDT");
        }
    }

    public const string MinimapTrsPath = @"textures\Minimap\md5translate.trs";

    /// <summary>
    /// Minimaps (verifier G11). The client resolves "Dir\\mapCOL_ROW.blp" through md5translate.trs to a BLP in
    /// textures\\Minimap. Every tile this build touched (stamps, sculpted stock tiles, tiles with placed buildings)
    /// gets the image of the ground it was built from - a stamp its SOURCE tile's, anything else its own stock image -
    /// and where the PUBLISHED ground differs from that ground (height, water, a dropped or placed building) those
    /// pixels are re-rendered and feathered in (<see cref="WorldPackMinimap"/>), written as textures\\Minimap\\wp_*.blp.
    /// A tile whose ground is unchanged keeps pointing at the Blizzard image. Pack maps get their own "dir:" section.
    /// Before 2026-09-27 this ran BEFORE sculpt, placements and the seam stitch and only mapped source images: Gilneas'
    /// pass (land raised out of the sea, Azeroth 29,34) showed open sea.
    /// </summary>
    private void BuildMinimaps(BuildState s, VanillaArchiveSet stock, List<DocRow> docs, Dictionary<int, string> mapDirs,
        Dictionary<string, byte[]> files, Dictionary<(int map, int col, int row), AdtDocument> built,
        List<(int map, int col, int row)> present)
    {
        if (present.Count == 0) return;
        byte[]? trs = stock.ReadFile(MinimapTrsPath);
        if (trs == null) { Log(s, "minimap: stock md5translate.trs missing - no minimaps"); return; }
        string text = Encoding.UTF8.GetString(trs);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) hashes[parts[0]] = parts[1];
        }
        var stamps = docs.Where(d => d.Kind == "tile").Select(d => JsonNode.Parse(d.Body)!.AsObject())
            .ToDictionary(t => ((int)t["map"]!, (int)t["col"]!, (int)t["row"]!));

        var texColour = new Dictionary<string, System.Numerics.Vector3?>(StringComparer.OrdinalIgnoreCase);
        System.Numerics.Vector3? TextureColour(string path)
        {
            if (texColour.TryGetValue(path, out var c)) return c;
            try
            {
                byte[]? blp = stock.ReadFile(path);
                if (blp != null)
                {
                    byte[] px = BlpDecoder.GetPixels(blp, 0, out int w, out int h);
                    double r = 0, g = 0, b = 0; int n = 0;
                    for (int i = 0; i + 3 < px.Length; i += 4 * 7) { b += px[i]; g += px[i + 1]; r += px[i + 2]; n++; }
                    c = n > 0 ? new System.Numerics.Vector3((float)(r / n), (float)(g / n), (float)(b / n)) : null;
                }
            }
            catch { c = null; }
            return texColour[path] = c;
        }
        byte[]? Image(string key)
        {
            if (!hashes.TryGetValue(key, out var hash)) return null;
            try
            {
                byte[]? blp = stock.ReadFile(@"textures\Minimap\" + hash);
                if (blp == null) return null;
                byte[] px = BlpDecoder.GetPixels(blp, 0, out int w, out int h);
                return w == WorldPackMinimap.Size && h == WorldPackMinimap.Size ? px : null;
            }
            catch { return null; }
        }

        // Pass 1: each touched tile against the ground its image shows (a stamp's SOURCE tile, else the stock tile).
        var jobs = new List<(int map, int col, int row, string srcKey, byte[]? image, WorldPackMinimap.Tile tile)>();
        var wmoSizes = new Dictionary<string, (System.Numerics.Vector3, System.Numerics.Vector3)?>(StringComparer.OrdinalIgnoreCase);
        var waterPool = new List<(float, System.Numerics.Vector3)>();
        foreach (var key in present)
        {
            var (map, col, row) = key;
            string srcDir = mapDirs[map]; int sc = col, sr = row;
            if (stamps.TryGetValue(key, out var st)) { srcDir = (string)st["sourceMap"]!; sc = (int)st["sourceCol"]!; sr = (int)st["sourceRow"]!; }
            string srcKey = $@"{srcDir}\map{sc}_{sr}.blp";
            byte[]? srcAdt = stock.ReadFile(WorldCoords.AdtPath(srcDir, sc, sr));
            byte[]? image = Image(srcKey);
            var tile = WorldPackMinimap.Prepare(built[key], col, row, srcAdt == null ? null : AdtDocument.Parse(srcAdt, sc, sr), sc, sr,
                image, stock.ReadFile, TextureColour, wmoSizes, waterPool);
            jobs.Add((map, col, row, srcKey, image, tile));
        }
        // The sea is one colour: its model also learns from the untouched stock tiles around the touched ones.
        foreach (var (map, col, row) in present.Where(k => !_newMaps.Contains(k.map))
                     .SelectMany(k => new[] { (k.map, k.col - 1, k.row), (k.map, k.col + 1, k.row), (k.map, k.col, k.row - 1), (k.map, k.col, k.row + 1) })
                     .Distinct().Where(k => !present.Contains(k)))
        {
            byte[]? adt = stock.ReadFile(WorldCoords.AdtPath(mapDirs[map], col, row));
            byte[]? img = Image($@"{mapDirs[map]}\map{col}_{row}.blp");
            if (adt == null || img == null) continue;
            var ground = WorldPackMinimap.Sample(AdtDocument.Parse(adt, col, row), TextureColour);
            WorldPackMinimap.Fit(img, ground, new bool[WorldPackMinimap.Size * WorldPackMinimap.Size], WorldPackMinimap.Calibration.Default, waterPool);
        }
        // A tile without enough ground of its own takes the average ground fit; every tile takes the shared water model.
        var mean = WorldPackMinimap.Mean(jobs.Select(j => j.tile.Fit), waterPool);

        // Pass 2: the image each tile's minimap line points at.
        var lineFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);     // "Dir\mapC_R.blp" -> file
        var writer = new BlpWriterService();
        int rendered = 0, kept = 0, repaintedPixels = 0;
        foreach (var j in jobs)
        {
            string dir = mapDirs[j.map], key = $@"{dir}\map{j.col}_{j.row}.blp";
            int n = j.tile.ChangedPixels;
            if (n == 0)
            {
                if (hashes.TryGetValue(j.srcKey, out var h) && !string.Equals(j.srcKey, key, StringComparison.OrdinalIgnoreCase)) lineFor[key] = h;
                kept++;
                continue;
            }
            byte[] bgra = WorldPackMinimap.Render(j.tile, j.image, mean);
            using var bmp = new SkiaSharp.SKBitmap(WorldPackMinimap.Size, WorldPackMinimap.Size, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmp.GetPixels(), bgra.Length);
            bmp.NotifyPixelsChanged();
            byte[]? blp = writer.EncodeBitmapToBlp(bmp, useDxt1: true);
            if (blp == null) { Log(s, $"minimap: {key} could not be encoded - it keeps its source image"); continue; }
            string file = $"wp_{dir}_{j.col}_{j.row}.blp";
            files[@"textures\Minimap\" + file] = blp;
            lineFor[key] = file;
            rendered++; repaintedPixels += n;
        }

        // md5translate.trs: replace the lines of stock-continent tiles, add missing ones, and a section per pack map.
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        text = string.Join("\n", text.Split('\n').Select(line =>
        {
            var parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !lineFor.TryGetValue(parts[0], out var file)) return line;
            matched.Add(parts[0]);
            return $"{parts[0]}\t{file}\r";
        }));
        var sb = new StringBuilder(text.TrimEnd('\r', '\n')).Append("\r\n");
        foreach (var group in lineFor.Keys.Where(k => !matched.Contains(k)).GroupBy(k => k[..k.IndexOf('\\')]))
        {
            sb.Append("dir: ").Append(group.Key).Append("\r\n");
            foreach (var k in group.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) sb.Append(k).Append('\t').Append(lineFor[k]).Append("\r\n");
        }
        files[MinimapTrsPath] = Encoding.UTF8.GetBytes(sb.ToString());
        Log(s, $"minimap: {rendered} tile(s) re-rendered where the published ground differs ({repaintedPixels} px), " +
               $"{kept} unchanged; colours fitted to {mean.GroundSamples} Blizzard image(s)");
    }

    /// <summary>Patched ADT bytes per map of this build — the navmesh input fingerprint.</summary>
    private Dictionary<int, List<(int col, int row, byte[] bytes)>> _mapAdtBytes = new();

    private string NavHashPath => Path.Combine(Root, "navhash.json");
    private string NavTileHashPath => Path.Combine(Root, "navtiles.json");

    /// <summary>"map:col:row" → SHA-1 of that patched ADT: which tiles of a pack map need new navmesh.</summary>
    private Dictionary<string, string> TileFingerprints() => _mapAdtBytes
        .SelectMany(kv => kv.Value.Select(t => (key: $"{kv.Key}:{t.col}:{t.row}", t.bytes)))
        .ToDictionary(x => x.key, x => Convert.ToHexString(SHA1.HashData(x.bytes)));

    private Dictionary<string, string> LoadNavTileHashes()
    {
        try
        {
            if (File.Exists(NavTileHashPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(NavTileHashPath)) ?? new();
        }
        catch { }
        return new();
    }

    private Dictionary<int, string> MapFingerprints() => _mapAdtBytes.ToDictionary(kv => kv.Key, kv =>
    {
        using var sha = SHA1.Create();
        foreach (var (col, row, bytes) in kv.Value.OrderBy(t => t.col).ThenBy(t => t.row))
        {
            sha.TransformBlock(BitConverter.GetBytes(col * 64 + row), 0, 4, null, 0);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    });

    /// <summary>Server-side DBC files of this build (DataDir-relative → work file).</summary>
    private Dictionary<string, string> _serverDbcs = new();

    /// <summary>
    /// A pack ZONE (AreaTable row with no parent) that has stamped tiles and no WorldMapArea doc of its own gets a
    /// generated WorldMapArea row: bounds fitted around its tiles at the world map's 1002:668 aspect (west = left,
    /// north = top). The client has no painted art for it and draws the zone's minimap mosaic in those bounds.
    /// </summary>
    private IEnumerable<DocRow> ZoneWorldMapAreas(BuildState s, List<DocRow> docs)
    {
        var existing = docs.Where(d => d.Kind == "dbc:WorldMapArea").Select(d => JsonNode.Parse(d.Body)!["fields"]?["2"])
            .Where(n => n is not null).Select(n => (int)(double)n!).ToHashSet();
        var tiles = docs.Where(d => d.Kind == "tile").Select(d => JsonNode.Parse(d.Body)!.AsObject()).ToList();
        foreach (var d in docs.Where(d => d.Kind == "dbc:AreaTable"))
        {
            var f = JsonNode.Parse(d.Body)!["fields"] as JsonObject;
            if (f is null || (int?)f["2"] is not 0 and not null || !int.TryParse(d.DocKey, out int zone) || existing.Contains(zone)) continue;
            int map = (int?)f["1"] ?? -1;
            var own = tiles.Where(t => (int?)t["map"] == map && (int?)t["areaId"] == zone).ToList();
            if (own.Count == 0) continue;
            const float tile = WorldCoords.Tile, aspect = 1002f / 668f, margin = 1.06f;
            float west = (32 - own.Min(t => (int)t["col"]!)) * tile, east = (31 - own.Max(t => (int)t["col"]!)) * tile;
            float north = (32 - own.Min(t => (int)t["row"]!)) * tile, south = (31 - own.Max(t => (int)t["row"]!)) * tile;
            float width = (west - east) * margin, height = (north - south) * margin;
            if (width / height < aspect) width = height * aspect; else height = width / aspect;
            float cy = (west + east) / 2f, cx = (north + south) / 2f;
            string name = new string(((string?)f["11"] ?? $"Zone{zone}").Where(char.IsLetterOrDigit).ToArray());
            Log(s, $"WorldMapArea: zone {zone} ({name}) on map {map} gets a generated row over {own.Count} tile(s)");
            yield return new DocRow
            {
                Kind = "dbc:WorldMapArea", DocKey = zone.ToString(),
                Body = new JsonObject
                {
                    ["fields"] = new JsonObject
                    {
                        ["1"] = map, ["2"] = zone, ["3"] = name,
                        ["4"] = new JsonObject { ["f"] = cy + width / 2f }, ["5"] = new JsonObject { ["f"] = cy - width / 2f },
                        ["6"] = new JsonObject { ["f"] = cx + height / 2f }, ["7"] = new JsonObject { ["f"] = cx - height / 2f },
                    },
                }.ToJsonString(),
            };
        }
    }

    private void BuildDbcs(BuildState s, VanillaArchiveSet stock, List<DocRow> docs, Dictionary<string, byte[]> files)
    {
        _serverDbcs = new();
        foreach (var group in docs.Where(d => d.Kind.StartsWith("dbc:")).GroupBy(d => d.Kind[4..], StringComparer.OrdinalIgnoreCase))
        {
            string path = $"DBFilesClient\\{group.Key}.dbc";
            var dbc = DbcWriterService.ReadDbc(stock.ReadFile(path) ?? throw new InvalidOperationException($"{path} missing from stock archives"), path);
            foreach (var d in group.OrderBy(d => uint.Parse(d.DocKey)))
            {
                uint id = uint.Parse(d.DocKey);
                var body = JsonNode.Parse(d.Body)!.AsObject();
                dbc.RemoveRowsWhere(x => x == id);
                if (body["cloneFrom"] is JsonNode from) dbc.CloneRow((uint)from, id);
                else
                {
                    var fresh = new uint[dbc.FieldCount];
                    fresh[0] = id;
                    dbc.AddRow(fresh);
                }
                foreach (var (index, value) in body["fields"]!.AsObject())
                {
                    int field = int.Parse(index);
                    if (field <= 0 || field >= dbc.FieldCount) throw new InvalidOperationException($"{group.Key}.dbc has no field {field}");
                    // string → string block; { "f": x } → float bits; number → integer.
                    if (value is JsonValue v && v.TryGetValue<string>(out var str)) dbc.PatchRow(id, field, dbc.AddString(str));
                    else if (value is JsonObject fo && fo["f"] is JsonNode fv) dbc.PatchRowFloat(id, field, (float)(double)fv);
                    else dbc.PatchRow(id, field, unchecked((uint)(long)value!));
                }
            }
            dbc.SortById();
            files[path] = dbc.Write();
            // DBCs mangosd itself reads (WorldSafeLocs = graveyards) also go to the server DataDir.
            if (WorldPackContent.ServerDbcs.Contains(group.Key))
            {
                string local = Path.Combine(Work, "serverdbc", group.Key + ".dbc");
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                File.WriteAllBytes(local, files[path]);
                _serverDbcs[$"5875/dbc/{group.Key}.dbc"] = local;
            }
            Log(s, $"{group.Key}.dbc: +{group.Count()} row(s)");
        }
    }

    /// <summary>
    /// Make the world DB hold exactly the rows of the enabled packs: rows a previous publish added
    /// and no enabled pack still owns are deleted; every owned row is (re)written. Packs only own
    /// rows in reserved id ranges (WorldPackContent), so stock data is never touched.
    /// </summary>
    private async Task<int> ApplyDbRowsAsync(BuildState s)
    {
        var allDocs = await _store.DocsAsync(null, null, enabledOnly: true);
        var docs = allDocs.Where(d => d.Kind.StartsWith("dbrow:", StringComparison.Ordinal)).ToList();
        var desired = new Dictionary<(string tbl, string key), (int pack, JsonObject row)>();
        foreach (var d in docs) desired[(d.Kind[6..], d.DocKey)] = (d.PackId, JsonNode.Parse(d.Body)!.AsObject());
        var previous = await _store.InstalledRowsAsync();

        int changed = 0;
        using var conn = _db.Mangos();
        await conn.OpenAsync();

        // Most VMaNGOS world tables are MyISAM: a transaction cannot roll a half-applied set back.
        // So (1) validate EVERY row against the live column types before the first write, and
        // (2) record the union of old + new rows as installed first, so a crash mid-way can never
        // leave rows the next publish does not know to clean up.
        await ValidateRowsAsync(conn, desired.Select(kv => (kv.Key.tbl, kv.Value.row)));
        using var admin = _db.Admin();
        await admin.OpenAsync();
        var replacements = await WorldPackNpcReplacements.PrepareAsync(conn, admin, allDocs);
        // Restore stock spawn links before deleting the reserved templates they used.
        changed += await WorldPackNpcReplacements.ApplyAsync(conn, admin, replacements.Where(r => r.Restore));
        await _store.SetInstalledRowsAsync(desired.Select(kv => (kv.Key.tbl, kv.Key.key, kv.Value.pack))
            .Concat(previous.Where(p => !desired.ContainsKey(p)).Select(p => (p.tbl, p.key, 0))));

        using var tx = await conn.BeginTransactionAsync();
        foreach (var (tbl, key) in previous)
        {
            if (desired.ContainsKey((tbl, key)) || !WorldPackContent.Tables.TryGetValue(tbl, out var rule)) continue;
            var p = new DynamicParameters();
            var parts = key.Split('|');
            string where = string.Join(" AND ", rule.Key.Select((c, i) => { p.Add("k" + i, parts[i]); return $"`{c}` = @k{i}"; }));
            changed += await conn.ExecuteAsync($"DELETE FROM `{tbl}` WHERE {where}", p, tx);
        }
        foreach (var ((tbl, key), (_, row)) in desired)
        {
            var rule = WorldPackContent.Tables[tbl];
            var p = new DynamicParameters();
            var parts = key.Split('|');
            string where = string.Join(" AND ", rule.Key.Select((c, i) => { p.Add("k" + i, parts[i]); return $"`{c}` = @k{i}"; }));
            await conn.ExecuteAsync($"DELETE FROM `{tbl}` WHERE {where}", p, tx);
            var cols = row.Select(kv => kv.Key).ToList();
            var ip = new DynamicParameters();
            for (int i = 0; i < cols.Count; i++) ip.Add("v" + i, JsonScalar(row[cols[i]]));
            await conn.ExecuteAsync(
                $"INSERT INTO `{tbl}` ({string.Join(", ", cols.Select(c => $"`{c}`"))}) VALUES ({string.Join(", ", cols.Select((_, i) => "@v" + i))})",
                ip, tx);
            changed++;
        }
        await tx.CommitAsync();
        // Reserved templates and their services exist before a stock spawn points to them.
        changed += await WorldPackNpcReplacements.ApplyAsync(conn, admin, replacements.Where(r => !r.Restore));
        await _store.SetInstalledRowsAsync(desired.Select(kv => (kv.Key.tbl, kv.Key.key, kv.Value.pack)));
        Log(s, $"world DB: {desired.Count} pack row(s) written, {previous.Count(r => !desired.ContainsKey(r))} removed");
        return changed;
    }

    /// <summary>Every column must exist and every number must fit its integer type (MySQL would
    /// otherwise reject the row half-way through a non-transactional apply).</summary>
    internal static async Task ValidateRowsAsync(MySqlConnector.MySqlConnection conn, IEnumerable<(string tbl, JsonObject row)> rows)
    {
        var list = rows.ToList();
        var tables = list.Select(r => r.tbl).Distinct().ToList();
        if (tables.Count == 0) return;
        var cols = (await conn.QueryAsync<(string t, string c, string type)>(
                "SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN @tables",
                new { tables }))
            .ToDictionary(x => (x.t.ToLowerInvariant(), x.c.ToLowerInvariant()), x => x.type.ToLowerInvariant());
        var errors = new List<string>();
        foreach (var (tbl, row) in list)
            foreach (var (col, value) in row)
            {
                if (!cols.TryGetValue((tbl.ToLowerInvariant(), col.ToLowerInvariant()), out var type)) { errors.Add($"{tbl}.{col}: no such column"); continue; }
                if (value is not JsonValue v || v.TryGetValue<string>(out _)) continue;
                if (!double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) continue;
                if (!FitsColumn(type, d)) errors.Add($"{tbl}.{col} = {d} does not fit {type}");
            }
        if (errors.Count > 0)
            throw new InvalidOperationException("world DB rows rejected before writing: " + string.Join("; ", errors.Distinct().Take(12)));
    }

    /// <summary>Does a number fit a MySQL column type (information_schema COLUMN_TYPE, lower case)? Integer widths, and
    /// UNSIGNED on every numeric type: creature_groups.angle is FLOAT UNSIGNED and a negative angle (an Atan2 result)
    /// was refused half-way through build #28's non-transactional apply - the integer-only check had passed it.</summary>
    internal static bool FitsColumn(string type, double d)
    {
        bool unsigned = type.Contains("unsigned");
        (double lo, double hi)? range = type.StartsWith("tinyint") ? (unsigned ? (0, 255) : (-128, 127))
            : type.StartsWith("smallint") ? (unsigned ? (0, 65535) : (-32768, 32767))
            : type.StartsWith("mediumint") ? (unsigned ? (0, 16777215) : (-8388608, 8388607))
            : type.StartsWith("int") ? (unsigned ? (0, 4294967295) : (-2147483648, 2147483647))
            : type.StartsWith("bigint") ? (unsigned ? (0, 18446744073709551615d) : (-9223372036854775808d, 9223372036854775807d))
            : unsigned && (type.StartsWith("float") || type.StartsWith("double") || type.StartsWith("decimal") || type.StartsWith("real"))
                ? (0, double.MaxValue)
            : null;
        return range is not { } rg || (d >= rg.lo && d <= rg.hi);
    }

    private static object? JsonScalar(JsonNode? n)
    {
        if (n is not JsonValue v) return n?.ToJsonString();
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        string raw = v.ToJsonString();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E')) return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        return long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ═══════════════════════════════════════════════════════════════ 2. extract

    private async Task ExtractAsync(BuildState s, List<int> maps)
    {
        string list = string.Join(",", maps);
        foreach (var m in maps)
        {
            Directory.CreateDirectory(Path.Combine(Work, "maps"));
            foreach (var f in Directory.EnumerateFiles(Path.Combine(Work, "maps"), $"{m:D3}*.map")) File.Delete(f);
            var vm = Path.Combine(Work, "vmaps");
            if (Directory.Exists(vm))
                foreach (var f in Directory.EnumerateFiles(vm, $"{m:D3}*")) File.Delete(f);
        }
        await RunToolAsync(s, "MapExtractor", $"-i . -o . -e 1 -m {list}", TimeSpan.FromMinutes(10));

        var buildings = Path.Combine(Work, "Buildings");
        foreach (var name in new[] { "dir", "dir_bin" })
            if (File.Exists(Path.Combine(buildings, name))) File.Delete(Path.Combine(buildings, name));
        await RunToolAsync(s, "VMapExtractor", $"-l -d ./Data/ -m {list}", TimeSpan.FromMinutes(20));
        Directory.CreateDirectory(Path.Combine(Work, "vmaps"));
        await RunToolAsync(s, "VMapAssembler", "Buildings vmaps", TimeSpan.FromMinutes(20));
    }

    private async Task RunToolAsync(BuildState s, string tool, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(Path.Combine(Tools, tool), args)
        {
            WorkingDirectory = Work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"{tool} did not start");
        proc.StandardInput.Close();   // VMapExtractor waits on stdin when it refuses a polluted dir
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException($"{tool} timed out after {timeout}");
        }
        string output = (await stdout) + (await stderr);
        string tail = string.Join(" | ", output.Split('\n', '\r').Where(l => l.Trim().Length > 0).TakeLast(2));
        Log(s, $"{tool} {args} → exit {proc.ExitCode} in {sw.Elapsed.TotalSeconds:F0}s: {tail}");
        if (proc.ExitCode != 0) throw new InvalidOperationException($"{tool} failed (exit {proc.ExitCode}): {tail}");
    }

    // ═══════════════════════════════════════════════════════════════ 2b. compare

    /// <summary>Stock bytes of a DataDir-relative file: the baseline copy when an earlier publish
    /// replaced it, "absent" when an earlier publish created it, else whatever is installed.</summary>
    private byte[]? StockBytes(string rel, Dictionary<string, bool> previous)
    {
        if (previous.TryGetValue(rel, out bool stockPresent))
            return stockPresent ? File.ReadAllBytes(Path.Combine(Baseline, rel)) : null;
        var live = Path.Combine(ServerData, rel);
        return File.Exists(live) ? File.ReadAllBytes(live) : null;
    }

    private bool StockExists(string rel, Dictionary<string, bool> previous) =>
        previous.TryGetValue(rel, out bool present) ? present : File.Exists(Path.Combine(ServerData, rel));

    private HashSet<(int map, int col, int row)> CollectChanged(BuildState s, List<int> maps,
        Dictionary<string, bool> previous, Dictionary<string, string> produced, HashSet<string> removed)
    {
        var changed = new HashSet<(int map, int col, int row)>();
        int n = 0;
        foreach (int m in maps)
        {
            // .map: MMMRRCC (row, col)
            foreach (var f in Directory.EnumerateFiles(Path.Combine(Work, "maps"), $"{m:D3}*.map"))
            {
                string name = Path.GetFileName(f), rel = "maps/" + name;
                if (Same(File.ReadAllBytes(f), StockBytes(rel, previous))) continue;
                produced[rel] = f; n++;
                changed.Add((m, int.Parse(name.Substring(5, 2)), int.Parse(name.Substring(3, 2))));
            }

            // vmaps of this map: MMM.vmtree and MMM_CC_RR.vmtile
            var workVm = Path.Combine(Work, "vmaps");
            var workNames = Directory.EnumerateFiles(workVm, $"{m:D3}*").Select(Path.GetFileName).ToHashSet()!;
            foreach (var name in workNames)
            {
                string rel = "vmaps/" + name;
                if (Same(File.ReadAllBytes(Path.Combine(workVm, name!)), StockBytes(rel, previous))) continue;
                produced[rel] = Path.Combine(workVm, name!); n++;
                // vmtile bytes also change when the map's spawn tree is renumbered (a new spawn shifts
                // every later node index), so a differing vmtile is installed but is NOT evidence of
                // changed geometry - navmesh work keys off the patched ADTs instead (see BuildNavMeshAsync).
            }
            // A stock vmtile the new build no longer produces (its last spawn was removed).
            foreach (var f in Directory.EnumerateFiles(Path.Combine(ServerData, "vmaps"), $"{m:D3}_*.vmtile")
                         .Select(Path.GetFileName)
                         .Concat(previous.Keys.Where(k => k.StartsWith($"vmaps/{m:D3}_")).Select(k => k[6..])))
            {
                if (workNames.Contains(f) || !StockExists("vmaps/" + f, previous)) continue;
                removed.Add("vmaps/" + f);
            }
        }

        // Models referenced by the new vmaps: install the ones the server does not have.
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Work, "vmaps")))
        {
            string name = Path.GetFileName(f);
            if (char.IsDigit(name[0]) && name.Length > 3 && char.IsDigit(name[1]) && char.IsDigit(name[2])) continue;
            if (name == "temp_gameobject_models") continue;
            string rel = "vmaps/" + name;
            if (StockExists(rel, previous)) continue;
            produced[rel] = f; n++;
        }

        Log(s, $"{n} server file(s) differ from stock; {changed.Count} tile(s) changed: " +
               string.Join(" ", changed.OrderBy(t => t).Select(t => $"{t.map}:{t.col},{t.row}")));
        return changed;
    }

    private static bool TryVmtileTile(string name, out int col, out int row)
    {
        col = row = 0;
        var parts = Path.GetFileNameWithoutExtension(name).Split('_');
        return name.EndsWith(".vmtile") && parts.Length == 3 && int.TryParse(parts[1], out col) && int.TryParse(parts[2], out row);
    }

    private static bool Same(byte[] a, byte[]? b) => b != null && a.AsSpan().SequenceEqual(b);

    // ═══════════════════════════════════════════════════════════════ 2c. navmesh

    private async Task BuildNavMeshAsync(BuildState s, List<int> maps, HashSet<(int map, int col, int row)> changed,
        Dictionary<string, bool> previous, Dictionary<string, string> produced, HashSet<string> keep)
    {
        var mm = Path.Combine(Work, "mmaps");
        Directory.CreateDirectory(mm);

        // Changed tiles and their 8 neighbours (navmesh connectivity crosses tile borders).
        var tiles = new SortedSet<(int map, int col, int row)>();
        foreach (var (m, c, r) in changed)
        {
            if (_newMaps.Contains(m)) continue;   // whole-map build below
            for (int dc = -1; dc <= 1; dc++)
                for (int dr = -1; dr <= 1; dr++)
                    if (File.Exists(Path.Combine(Work, "maps", $"{m:D3}{r + dr:D2}{c + dc:D2}.map")))
                        tiles.Add((m, c + dc, r + dr));
        }

        foreach (int m in maps)
        {
            var stockParams = StockBytes($"mmaps/{m:D3}.mmap", previous);
            if (stockParams != null) await File.WriteAllBytesAsync(Path.Combine(mm, $"{m:D3}.mmap"), stockParams);
            foreach (var f in Directory.EnumerateFiles(mm, $"{m:D3}*.mmtile")) File.Delete(f);
        }

        // A pack map whose installed navmesh covers the same tile set: only tiles whose patched ADT
        // changed (+ neighbours, connectivity crosses borders) are regenerated; the rest stay installed.
        var lastTiles = LoadNavTileHashes();
        var nowTiles = TileFingerprints();
        var incremental = new HashSet<int>();
        foreach (int m in maps.Where(_newMaps.Contains))
        {
            string installedParams = Path.Combine(ServerData, "mmaps", $"{m:D3}.mmap");
            var lastSet = lastTiles.Keys.Where(k => k.StartsWith($"{m}:")).ToHashSet();
            var nowSet = nowTiles.Keys.Where(k => k.StartsWith($"{m}:")).ToHashSet();
            if (!File.Exists(installedParams) || lastSet.Count == 0 || !lastSet.SetEquals(nowSet) ||
                !previous.ContainsKey($"mmaps/{m:D3}.mmap"))
                continue;
            File.Copy(installedParams, Path.Combine(mm, $"{m:D3}.mmap"), overwrite: true);
            int before = tiles.Count;
            foreach (var key in nowSet.Where(k => lastTiles[k] != nowTiles[k]))
            {
                var p = key.Split(':');
                int c = int.Parse(p[1]), r = int.Parse(p[2]);
                for (int dc = -1; dc <= 1; dc++)
                    for (int dr = -1; dr <= 1; dr++)
                        if (nowSet.Contains($"{m}:{c + dc}:{r + dr}")) tiles.Add((m, c + dc, r + dr));
            }
            // Everything of this map that is NOT regenerated stays exactly as installed.
            foreach (var rel in previous.Keys.Where(k => k.StartsWith($"mmaps/{m:D3}")))
            {
                string name = Path.GetFileName(rel);
                bool redone = tiles.Any(t => t.map == m && name == $"{m:D3}{t.row:D2}{t.col:D2}.mmtile");
                if (!redone) keep.Add(rel);
            }
            incremental.Add(m);
            Log(s, $"navmesh: pack map {m} incremental — {tiles.Count - before} of {nowSet.Count} tile(s) regenerated (changed + neighbours)");
        }

        // A map that exists only in a pack and has no usable installed navmesh: build the whole map
        // (its own params file included) and install every tile.
        foreach (int m in maps.Where(m => _newMaps.Contains(m) && !incremental.Contains(m)))
        {
            string par = Path.Combine(mm, $"{m:D3}.mmap");
            if (File.Exists(par)) File.Delete(par);
            await RunToolAsync(s, "MoveMapGenerator", $"{m} --threads 8 --silent", TimeSpan.FromMinutes(60));
            foreach (var f in Directory.EnumerateFiles(mm, $"{m:D3}*"))
                produced["mmaps/" + Path.GetFileName(f)] = f;
            Log(s, $"navmesh: new map {m}: {Directory.EnumerateFiles(mm, $"{m:D3}*.mmtile").Count()} tile(s)");
        }
        if (tiles.Count == 0) { Log(s, "navmesh: no stock-map tiles changed"); return; }

        Log(s, $"navmesh: {tiles.Count} tile(s) (changed + neighbours), up to 8 in parallel, ~3-4 min each");
        using var gate = new SemaphoreSlim(8);
        var jobs = tiles.Select(async t =>
        {
            await gate.WaitAsync();
            try
            {
                await RunToolAsync(s, "MoveMapGenerator", $"{t.map} --tile {t.col},{t.row} --threads 1 --silent", TimeSpan.FromMinutes(30));
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(jobs);

        foreach (int m in maps.Where(m => !_newMaps.Contains(m) && tiles.Any(t => t.map == m)))
        {
            // (pack maps: the params file is the installed one we copied in, never compared to stock)
            var stockParams = StockBytes($"mmaps/{m:D3}.mmap", previous);
            var built = await File.ReadAllBytesAsync(Path.Combine(mm, $"{m:D3}.mmap"));
            if (stockParams != null && !Same(built, stockParams))
                throw new InvalidOperationException($"navmesh params of map {m} changed ({m:D3}.mmap) — refusing to install mismatched tiles");
        }

        foreach (var (m, c, r) in tiles)
        {
            string name = $"{m:D3}{r:D2}{c:D2}.mmtile", rel = "mmaps/" + name, f = Path.Combine(mm, name);
            if (!File.Exists(f)) { Log(s, $"navmesh: {name} was not produced"); continue; }
            if (!Same(await File.ReadAllBytesAsync(f), StockBytes(rel, previous))) produced[rel] = f;
        }
        Log(s, $"navmesh: {produced.Keys.Count(k => k.StartsWith("mmaps/"))} tile(s) differ from stock");
    }

    // ═══════════════════════════════════════════════════════════════ 3. install

    private Dictionary<string, bool> LoadInstalled()
    {
        try
        {
            if (File.Exists(InstalledManifestPath))
                return JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(InstalledManifestPath)) ?? new();
        }
        catch { }
        return new();
    }

    /// <summary>Installed maps/vmaps/mmaps files with their SHA-1, so a client collision sync (MSUIClient
    /// WorldPackCollisionSync) downloads only the files that changed. Hashes are cached per
    /// (path, size, write time) - a publish rewrites whole map sets, most of them byte-identical.</summary>
    public IEnumerable<object> InstalledFiles() => LoadInstalled().Keys
        .Where(k => k.StartsWith("maps/") || k.StartsWith("vmaps/") || k.StartsWith("mmaps/"))
        .OrderBy(k => k)
        .Select(k =>
        {
            var f = new FileInfo(Path.Combine(ServerData, k));
            return (object)new { path = k, present = f.Exists, size = f.Exists ? f.Length : 0, sha1 = f.Exists ? Sha1Cached(f) : null };
        });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, DateTime Written, string Sha1)> _installedSha = new();

    private static string? Sha1Cached(FileInfo f)
    {
        if (_installedSha.TryGetValue(f.FullName, out var c) && c.Size == f.Length && c.Written == f.LastWriteTimeUtc) return c.Sha1;
        try
        {
            using var stream = f.OpenRead();
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream)).ToLowerInvariant();
            _installedSha[f.FullName] = (f.Length, f.LastWriteTimeUtc, sha);
            return sha;
        }
        catch { return null; }
    }

    /// <summary>Absolute path of an installed maps/vmaps/mmaps file, or null (no traversal, no other trees).</summary>
    public string? ServerFilePath(string rel)
    {
        if (string.IsNullOrEmpty(rel) || rel.Contains("..") || rel.Contains('\\')) return null;
        if (!(rel.StartsWith("maps/") || rel.StartsWith("vmaps/") || rel.StartsWith("mmaps/"))) return null;
        if (!LoadInstalled().ContainsKey(rel)) return null;
        string full = Path.Combine(ServerData, rel);
        return File.Exists(full) ? full : null;
    }

    private Dictionary<int, string> LoadNavHashes()
    {
        try
        {
            if (File.Exists(NavHashPath))
                return JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(NavHashPath)) ?? new();
        }
        catch { }
        return new();
    }

    private void Install(BuildState s, Dictionary<string, bool> previous, Dictionary<string, string> produced,
        HashSet<string> removed, HashSet<string> keep)
    {
        var next = new Dictionary<string, bool>();
        foreach (var rel in keep)
            if (!produced.ContainsKey(rel) && previous.TryGetValue(rel, out bool kept)) next[rel] = kept;

        // Files the previous publish owned that this one does not: back to stock.
        foreach (var (rel, stockPresent) in previous)
        {
            if (produced.ContainsKey(rel) || removed.Contains(rel) || keep.Contains(rel)) continue;
            var live = Path.Combine(ServerData, rel);
            if (stockPresent) CopyAtomic(Path.Combine(Baseline, rel), live);
            else if (File.Exists(live)) File.Delete(live);
            s.FilesRestored++;
        }

        foreach (var rel in produced.Keys.Concat(removed))
        {
            bool stockPresent = previous.TryGetValue(rel, out bool p) ? p : File.Exists(Path.Combine(ServerData, rel));
            if (!previous.ContainsKey(rel) && stockPresent)
                CopyAtomic(Path.Combine(ServerData, rel), Path.Combine(Baseline, rel));
            next[rel] = stockPresent;
        }
        foreach (var (rel, src) in produced) { CopyAtomic(src, Path.Combine(ServerData, rel)); s.FilesInstalled++; }
        foreach (var rel in removed)
        {
            var live = Path.Combine(ServerData, rel);
            if (File.Exists(live)) File.Delete(live);
            s.FilesInstalled++;
        }

        File.WriteAllText(InstalledManifestPath, JsonSerializer.Serialize(next, Json));
        Log(s, $"install: {s.FilesInstalled} file(s) written/removed, {s.FilesRestored} restored to stock; " +
               $"{next.Count} file(s) now owned by World Packs");
    }

    private static void CopyAtomic(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        string tmp = dst + ".wp-tmp";
        File.Copy(src, tmp, true);
        File.Move(tmp, dst, true);
    }

    private static bool IsSymlink(string path)
    {
        try { return new FileInfo(path).LinkTarget != null; } catch { return false; }
    }
}
