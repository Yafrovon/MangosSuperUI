using System.Text;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>One verifier result. Severity: error = broken for players, warn = probably wrong or
/// ugly, info = a measured fact (so a clean report still shows WHAT was checked).</summary>
public sealed record AuditFinding(string Check, string Severity, string Subject, string Message,
    int? Map = null, float? X = null, float? Y = null, float? Z = null);

/// <summary>Facts about the stock world the pack builds on (the live world DB). Null = offline:
/// existence checks against stock data are skipped and say so.</summary>
public interface IStockFacts
{
    bool Has(string table, long id);
    int StockSpawns(long creatureEntry);
    bool HasGossipOption(long menuId, uint npcFlag);
}

/// <summary>
/// The static half of the World Pack Verifier (MSUIClient shared_docs/WORLD_BUILDER.md §7).
/// Pure: reads the PUBLISHED world (patch-7 over stock) through <see cref="AuditInput.Built"/>,
/// the stock archives, the enabled packs' docs and placements, and optional stock facts.
///
/// Geometry  G1 stamped tiles equal their stock source + offset (every WMO and doodad)
///           G2 placed buildings clipping other buildings     G3 props inside placed buildings
///           G4 placed buildings floating                     G5 roads under / beside placed buildings
///           G6 leftovers of dropped WMOs                     G7 spawns vs ground   G8 portal ends vs ground
///           G9 every patched tile: no height cracks between chunks/tiles, no normal drift outside the sculpt
///           G10 water: boats on the surface, dock decks above it, no spawn standing in deep water
///           G11 every pack-map tile resolves to a minimap image (md5translate.trs -> an existing BLP)
///           G12 every terrain hole on a pack-map or patched tile has building geometry over it (else: the void)
///           G13 graded paths walkable   G14 patrol routes: every waypoint on the ground, every leg walkable
///           (grade, water, into a building - the leg from the last point back to the first included)
/// Content   C1..C16 templates, spawns, npc flags vs services, gossip options, vendors, trainers,
///           equipment, displays, loot, quests (obtainable objectives), EventAI, texts, maps,
///           areas + graveyards, area triggers, lights.
/// </summary>
public sealed class WorldPackAudit
{
    public sealed class AuditInput
    {
        public required Func<string, byte[]?> Stock { get; init; }
        public required Func<string, byte[]?> Built { get; init; }
        public required Dictionary<int, string> MapDirs { get; init; }
        public required List<DocRow> Docs { get; init; }
        public required List<PlacementRow> Placements { get; init; }
        public IStockFacts? Facts { get; init; }
        /// <summary>The server's installed mmaps directory (G15 reachability); null = not checked (pre-flight).</summary>
        public string? Mmaps { get; init; }
    }

    private readonly AuditInput _in;
    private readonly List<AuditFinding> _out = new();
    private readonly Dictionary<(int, int, int), AdtDocument?> _adts = new();
    private readonly Dictionary<string, List<(uint flags, Vector3 min, Vector3 max)>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int, int, int, int), List<(string texture, float[] alpha)>> _layers = new();

    public WorldPackAudit(AuditInput input) => _in = input;

    public List<AuditFinding> Run()
    {
        LoadRows();
        Guard("G1", StampFidelity);
        Guard("G17", () => _out.AddRange(WorldPackCoast.Verify(_in)));
        Guard("G2-G5", PlacedBuildings);
        Guard("G6", DroppedLeftovers);
        Guard("G7", Spawns);
        Guard("G8", Portals);
        Guard("G9", TerrainIntegrity);
        Guard("G10", Water);
        Guard("G11", Minimaps);
        Guard("G12", Holes);
        Guard("G13", Paths);
        Guard("G14", PatrolGround);
        Guard("G15", Reachability);
        Guard("G16", () => _out.AddRange(WorldPackWorldMap.Verify(_in.Docs, _in.Stock, _in.Built)));
        Guard("C", Content);
        return _out;
    }

    /// <summary>Pre-flight: the content checks only (no published world needed) — seconds, before a
    /// 5-minute build. Terrain/geometry checks need the published patch and run after publish.</summary>
    public List<AuditFinding> RunContent()
    {
        LoadRows();
        Guard("G16", () => { _ = WorldPackWorldMap.AreaDocuments(_in.Docs); });
        Guard("C", Content);
        // Reachability needs only the docs and the INSTALLED navmesh (spawns do not change it): a spawn moved onto an
        // island is caught in seconds, before a build. Terrain/placement edits reshape the navmesh only when published.
        Guard("G15", Reachability);
        return _out;
    }

    private void Guard(string check, Action a)
    {
        try { a(); }
        catch (Exception ex) { Add(check, "error", "verifier", $"check crashed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void Add(string check, string sev, string subject, string msg, int? map = null, Vector3? world = null) =>
        _out.Add(new AuditFinding(check, sev, subject, msg, map, world?.X, world?.Y, world?.Z));

    // ═══════════════════════════════════════════════════════════════ world access

    private AdtDocument? Adt(int map, int col, int row)
    {
        if (_adts.TryGetValue((map, col, row), out var d)) return d;
        AdtDocument? doc = null;
        if (_in.MapDirs.TryGetValue(map, out var dir) && col is >= 0 and < 64 && row is >= 0 and < 64)
        {
            var bytes = _in.Built(WorldCoords.AdtPath(dir, col, row));
            if (bytes != null) doc = AdtDocument.Parse(bytes, col, row);
        }
        return _adts[(map, col, row)] = doc;
    }

    /// <summary>Terrain height at a world point (bilinear over the outer grid), or null off the map.</summary>
    public float? Ground(int map, float x, float y)
    {
        int col = WorldCoords.TileCol(y), row = WorldCoords.TileRow(x);
        var adt = Adt(map, col, row);
        if (adt == null) return null;
        float gr = ((32 - row) * WorldCoords.Tile - x) / WorldCoords.Unit;
        float gc = ((32 - col) * WorldCoords.Tile - y) / WorldCoords.Unit;
        int r0 = Math.Clamp((int)gr, 0, 127), c0 = Math.Clamp((int)gc, 0, 127);
        float fr = Math.Clamp(gr - r0, 0, 1), fc = Math.Clamp(gc - c0, 0, 1);
        float h00 = adt.OuterHeight(r0, c0), h01 = adt.OuterHeight(r0, c0 + 1);
        float h10 = adt.OuterHeight(r0 + 1, c0), h11 = adt.OuterHeight(r0 + 1, c0 + 1);
        return (h00 * (1 - fc) + h01 * fc) * (1 - fr) + (h10 * (1 - fc) + h11 * fc) * fr;
    }

    /// <summary>Visible weight of road textures at a world point (0..1), or null off the map.</summary>
    public float? Road(int map, float x, float y)
    {
        int col = WorldCoords.TileCol(y), row = WorldCoords.TileRow(x);
        var adt = Adt(map, col, row);
        if (adt == null) return null;
        float gr = ((32 - row) * WorldCoords.Tile - x) / WorldCoords.Unit;
        float gc = ((32 - col) * WorldCoords.Tile - y) / WorldCoords.Unit;
        int iy = Math.Clamp((int)(gr / 8), 0, 15), ix = Math.Clamp((int)(gc / 8), 0, 15);
        if (!_layers.TryGetValue((map, col, row, iy * 16 + ix), out var layers))
        {
            int idx = adt.ChunkIndex(ix, iy);
            _layers[(map, col, row, iy * 16 + ix)] = layers = idx < 0 ? new() : adt.ChunkLayers(idx);
        }
        int pr = Math.Clamp((int)((gr - iy * 8) * 8), 0, 63), pc = Math.Clamp((int)((gc - ix * 8) * 8), 0, 63);
        float w = 0;
        foreach (var (tex, alpha) in layers)
            if (WorldPackGeometry.IsRoadTexture(tex)) w += alpha[pr * 64 + pc];
        return w;
    }

    private List<(uint flags, Vector3 min, Vector3 max)> Groups(string wmo)
    {
        if (!_groups.TryGetValue(wmo, out var g))
            _groups[wmo] = g = WorldPackGeometry.WmoGroups(_in.Built(wmo) ?? _in.Stock(wmo));
        return g;
    }

    private sealed record WmoInst(string Path, uint Uid, Vector3 Pos, Vector3 Rot, List<Obb> Boxes, Obb Whole);

    private WmoInst Inst(string path, uint uid, Vector3 pos, Vector3 rot)
    {
        var m = WorldPackGeometry.WmoMatrix(pos, rot);
        var boxes = Groups(path).Select(g => Obb.FromLocal(g.min, g.max, m)).ToList();
        var b = ModelBounds.Wmo(_in.Built(path) ?? _in.Stock(path));
        var whole = b is { } bb ? Obb.FromLocal(bb.min, bb.max, m) : boxes.FirstOrDefault();
        return new WmoInst(path, uid, pos, rot, boxes, whole);
    }

    /// <summary>Every WMO instance (deduped by uid) and ADT doodad referenced by the tiles around a
    /// placement-space point.</summary>
    private (List<WmoInst> wmos, List<(string path, uint uid, Vector3 pos, float scale)> doodads) Around(int map, Vector3 placement, int tiles = 1)
    {
        int c = (int)(placement.X / WorldCoords.Tile), r = (int)(placement.Z / WorldCoords.Tile);
        var wmos = new Dictionary<uint, WmoInst>();
        var doodads = new Dictionary<uint, (string, uint, Vector3, float)>();
        for (int dc = -tiles; dc <= tiles; dc++)
            for (int dr = -tiles; dr <= tiles; dr++)
            {
                var adt = Adt(map, c + dc, r + dr);
                if (adt == null) continue;
                foreach (var (path, uid, pos, rot) in adt.WmoPlacementsFull())
                    if (!wmos.ContainsKey(uid)) wmos[uid] = Inst(path, uid, pos, rot);
                foreach (var (path, uid, pos, _, scale) in adt.DoodadPlacements())
                    doodads.TryAdd(uid, (path, uid, pos, scale));
            }
        return (wmos.Values.ToList(), doodads.Values.ToList());
    }

    private static Vector3 W(Vector3 placement) => WorldCoords.PlacementToWorld(placement);

    // ═══════════════════════════════════════════════════════════════ G1 stamp fidelity

    private IEnumerable<(DocRow doc, JsonObject body)> Docs(string kind) =>
        _in.Docs.Where(d => d.Kind == kind && !string.IsNullOrEmpty(d.Body)).Select(d => (d, JsonNode.Parse(d.Body)!.AsObject()));

    private void StampFidelity()
    {
        var placedUids = _in.Placements.Select(p => p.UniqueId).ToHashSet();
        int tiles = 0, wmos = 0, doodads = 0, wrong = 0;
        foreach (var (_, t) in Docs("tile"))
        {
            int map = (int)t["map"]!, col = (int)t["col"]!, row = (int)t["row"]!;
            string srcDir = (string)t["sourceMap"]!;
            int sc = (int)t["sourceCol"]!, sr = (int)t["sourceRow"]!;
            bool keepDoodads = (bool?)t["keepDoodads"] ?? (bool?)t["keepObjects"] ?? true;
            bool keepWmos = (bool?)t["keepWmos"] ?? (bool?)t["keepObjects"] ?? false;
            var drops = (t["dropWmos"] as JsonArray)?.Select(n => n!.ToString()).ToList() ?? new();
            var dropProps = (t["dropDoodads"] as JsonArray)?.Select(n => n!.ToString()).ToList() ?? new();
            string subject = $"tile {map}:{col},{row} ← {srcDir} {sc},{sr}";
            var srcBytes = _in.Stock(WorldCoords.AdtPath(srcDir, sc, sr));
            var built = Adt(map, col, row);
            if (srcBytes == null || built == null) { Add("G1", "error", subject, srcBytes == null ? "stock source ADT missing" : "published ADT missing"); continue; }
            var src = AdtDocument.Parse(srcBytes, sc, sr);
            var off = new Vector3((col - sc) * WorldCoords.Tile, 0, (row - sr) * WorldCoords.Tile);
            static string Key(string path, Vector3 pos, Vector3 rot) =>
                $"{path.ToLowerInvariant()}|{pos.X:F1}|{pos.Y:F1}|{pos.Z:F1}|{rot.X:F1}|{rot.Y:F1}|{rot.Z:F1}";

            var coasts = WorldPackCoast.Read(_in.Docs).Where(c => c.Region.Map == map && c.Tiles.Contains((col, row))).ToList();
            var want = new List<string>();
            if (keepWmos)
                foreach (var (path, _, pos, rot) in src.WmoPlacementsFull())
                    if (!drops.Any(dp => path.EndsWith(dp, StringComparison.OrdinalIgnoreCase)) && !coasts.Any(c => c.ClearsBuilding(pos + off, sc == col && sr == row && srcDir == _in.MapDirs[map]))) want.Add(Key(path, pos + off, rot));
            var have = built.WmoPlacementsFull().Where(w => !placedUids.Contains(w.uid)).Select(w => Key(w.path, w.pos, w.rot)).ToList();
            Diff("WMO", want, have);
            wmos += have.Count;

            want.Clear();
            // Props inside a dropped WMO's group boxes are dropped with it (the build's rule, exactly).
            var gone = src.WmoPlacementsFull().Where(w => drops.Any(dp => w.path.EndsWith(dp, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(w => Inst(w.path, w.uid, w.pos, w.rot).Boxes).ToList();
            // ...and so are props standing inside a building a pack placed there (the build clears them).
            var placedBoxes = _in.Placements.Where(pl => !pl.Deleted && pl.Kind == "wmo" && pl.MapId == map)
                .SelectMany(pl => Inst(pl.ModelPath, pl.UniqueId, WorldCoords.WorldToPlacement(new Vector3(pl.PosX, pl.PosY, pl.PosZ)),
                    new Vector3(pl.RotX, pl.RotY, pl.RotZ)).Boxes).ToList();
            // ...and so are props in a graded path's lane (the build clears it: a road has no tree in it).
            var lanes = Docs("path").Select(p => GradedPath.Parse(p.doc.DocKey, p.body.ToJsonString()))
                .Where(p => p.Clear && p.Map == map).ToList();
            if (keepDoodads)
                foreach (var (path, _, pos, rot, _) in src.DoodadPlacements())
                    if (!gone.Any(b => b.Depth(pos) > 0f) && !placedBoxes.Any(b => b.Depth(pos + off) > 0.3f) &&
                        !dropProps.Any(dp => path.EndsWith(dp, StringComparison.OrdinalIgnoreCase)) &&
                        !lanes.Any(l => l.InLane(pos + off)) && !coasts.Any(c => c.ClearsProp(pos + off, sc == col && sr == row && srcDir == _in.MapDirs[map])))
                        want.Add(Key(path, pos + off, rot));
            have = built.DoodadPlacements().Where(d => !placedUids.Contains(d.uid)).Select(d => Key(d.path, d.pos, d.rot)).ToList();
            Diff("doodad", want, have);
            doodads += have.Count;
            tiles++;

            void Diff(string what, List<string> w, List<string> h)
            {
                var missing = w.GroupBy(k => k).Select(g => (g.Key, n: g.Count() - h.Count(x => x == g.Key))).Where(x => x.n > 0).ToList();
                var extra = h.GroupBy(k => k).Select(g => (g.Key, n: g.Count() - w.Count(x => x == g.Key))).Where(x => x.n > 0).ToList();
                wrong += missing.Sum(x => x.n) + extra.Sum(x => x.n);
                foreach (var (k, n) in missing.Take(5)) Add("G1", "error", subject, $"stock {what} missing after stamping: {k.Split('|')[0]} (x{n})", map);
                foreach (var (k, n) in extra.Take(5)) Add("G1", "error", subject, $"{what} that is not in the stock source: {k.Split('|')[0]} (x{n})", map);
            }
        }
        if (tiles > 0 && wrong == 0)
            Add("G1", "info", "stamped tiles", $"{tiles} tile(s): every kept WMO ({wmos}) and doodad ({doodads}) equals its stock source + the stamp offset — relative layout is Blizzard's");
        else if (tiles > 0)
            Add("G1", "error", "stamped tiles", $"{tiles} tile(s), {wmos} WMOs, {doodads} doodads: {wrong} placement(s) differ from stock + offset (model, position or rotation)");
    }

    // ═══════════════════════════════════════════════════════════════ G2–G5 placed buildings

    private void PlacedBuildings()
    {
        foreach (var p in _in.Placements.Where(p => !p.Deleted))
        {
            var pos = WorldCoords.WorldToPlacement(new Vector3(p.PosX, p.PosY, p.PosZ));
            var world = new Vector3(p.PosX, p.PosY, p.PosZ);
            string subject = $"placement #{p.Id} {Path.GetFileName(p.ModelPath)}";
            var (wmos, doodads) = Around(p.MapId, pos);
            if (p.Kind != "wmo")
            {
                foreach (var w in wmos)
                    if (w.Boxes.Any(b => b.Depth(pos) > 0.3f))
                        Add("G3", "warn", subject, $"model origin is inside {Path.GetFileName(w.Path)}", p.MapId, world);
                continue;
            }
            var me = Inst(p.ModelPath, p.UniqueId, pos, new Vector3(p.RotX, p.RotY, p.RotZ));
            if (me.Boxes.Count == 0) { Add("G2", "error", subject, "WMO has no group bounds (model missing?)", p.MapId, world); continue; }

            // G2 — interpenetration with every other building nearby.
            foreach (var other in wmos.Where(w => w.Uid != p.UniqueId))
            {
                float depth = 0;
                foreach (var a in me.Boxes) foreach (var b in other.Boxes) depth = MathF.Max(depth, Obb.Penetration(a, b));
                if (depth > 0.5f)
                    Add("G2", "warn", subject, $"clips into {Path.GetFileName(other.Path)} by {depth:F1} yd (group bounds)", p.MapId, W(other.Pos));
            }

            // G3 — ADT props (trees, fences, rocks) standing inside the building.
            var inside = doodads.Where(d => me.Boxes.Any(b => b.Depth(d.pos) > 0.3f)).GroupBy(d => Path.GetFileNameWithoutExtension(d.path)).ToList();
            foreach (var g in inside)
                Add("G3", "warn", subject, $"{g.Count()} × {g.Key} inside the building", p.MapId, W(g.First().pos));

            // G4 — contact with the ground: bottom face vs terrain.
            var bottom = me.Whole.SamplePoints(8).Where((_, i) => i % 2 == 0).Select(W).ToList();
            var gaps = bottom.Select(b => (b, h: Ground(p.MapId, b.X, b.Y))).Where(x => x.h != null).Select(x => x.b.Z - x.h!.Value).ToList();
            if (gaps.Count > 0)
            {
                float floating = gaps.Count(g => g > 0.75f) / (float)gaps.Count, maxGap = gaps.Max(), maxBury = -gaps.Min();
                string msg = $"footprint: {floating:P0} of samples > 0.75 yd above terrain (max gap {maxGap:F1} yd, deepest foundation {maxBury:F1} yd)";
                Add("G4", floating > 0.15f ? "warn" : "info", subject, msg, p.MapId, world);
            }
            // G4b - buried walls: terrain INSIDE the footprint (1 yd in from the walls) rising above the
            // floor (the placement origin) climbs the walls on the uphill side. Fix: script 'pad <id>'.
            var inner = me.Whole with { Half = me.Whole.Half - new Vector3(1f, 1f, 0f) };   // WMO local axes: z is up
            float worstRise = 0; Vector3 worstAt = world;
            foreach (var q in inner.SamplePoints(10).Select(W))
                if (Ground(p.MapId, q.X, q.Y) is { } gh && gh - p.PosZ > worstRise) { worstRise = gh - p.PosZ; worstAt = q; }
            if (worstRise > 1.0f)
                Add("G4", "warn", subject, $"terrain rises {worstRise:F1} yd above the floor inside the footprint (walls buried) - pad it", p.MapId, worstAt);

            // G5 — roads: covered by the footprint, and the building's axes against the nearest road's run.
            var foot = me.Whole.SamplePoints(10).Where((_, i) => i % 2 == 0).Select(W).ToList();
            var roadUnder = foot.Select(f => Road(p.MapId, f.X, f.Y) ?? 0f).ToList();
            float covered = roadUnder.Count(r => r > 0.5f) / (float)Math.Max(1, roadUnder.Count);
            var roadPts = new List<Vector2>();
            for (float dx = -40; dx <= 40; dx += 1.5f)
                for (float dy = -40; dy <= 40; dy += 1.5f)
                    if ((Road(p.MapId, world.X + dx, world.Y + dy) ?? 0) > 0.5f) roadPts.Add(new Vector2(world.X + dx, world.Y + dy));
            string roadMsg = $"road under footprint {covered:P0}";
            string sev = covered > 0.15f ? "warn" : "info";
            if (roadPts.Count >= 12)
            {
                var near = roadPts.OrderBy(r => Vector2.Distance(r, new Vector2(world.X, world.Y))).First();
                var seg = roadPts.Where(r => Vector2.Distance(r, near) < 14f).ToList();
                float dist = Vector2.Distance(near, new Vector2(world.X, world.Y));
                float roadAngle = PrincipalAngle(seg);
                var ax = W(me.Whole.Center + me.Whole.AxisX) - W(me.Whole.Center);
                float bAngle = MathF.Atan2(ax.Y, ax.X);
                float mis = MathF.Abs(((roadAngle - bAngle) * 180f / MathF.PI % 90f + 90f) % 90f);
                if (mis > 45f) mis = 90f - mis;
                roadMsg += $"; nearest road {dist:F0} yd from origin, building {mis:F0}° off square to it";
                if (dist < 25f && mis > 15f) sev = "warn";
            }
            else roadMsg += "; no road within 40 yd";
            Add("G5", sev, subject, roadMsg, p.MapId, world);
        }
    }

    private static float PrincipalAngle(List<Vector2> pts)
    {
        var mean = pts.Aggregate(Vector2.Zero, (a, b) => a + b) / pts.Count;
        float sxx = 0, sxy = 0, syy = 0;
        foreach (var q in pts) { var d = q - mean; sxx += d.X * d.X; sxy += d.X * d.Y; syy += d.Y * d.Y; }
        return 0.5f * MathF.Atan2(2 * sxy, sxx - syy);
    }

    // ═══════════════════════════════════════════════════════════════ G6 dropped WMOs

    private void DroppedLeftovers()
    {
        var seen = new HashSet<string>();
        foreach (var (_, t) in Docs("tile"))
        {
            var drops = (t["dropWmos"] as JsonArray)?.Select(n => n!.ToString()).ToList();
            if (drops is not { Count: > 0 }) continue;
            int map = (int)t["map"]!, col = (int)t["col"]!, row = (int)t["row"]!, sc = (int)t["sourceCol"]!, sr = (int)t["sourceRow"]!;
            var srcBytes = _in.Stock(WorldCoords.AdtPath((string)t["sourceMap"]!, sc, sr));
            if (srcBytes == null) continue;
            var off = new Vector3((col - sc) * WorldCoords.Tile, 0, (row - sr) * WorldCoords.Tile);
            foreach (var (path, uid, pos, rot) in AdtDocument.Parse(srcBytes, sc, sr).WmoPlacementsFull())
            {
                if (!drops.Any(d => path.EndsWith(d, StringComparison.OrdinalIgnoreCase)) || !seen.Add($"{map}:{uid}")) continue;
                var gone = Inst(path, uid, pos + off, rot);
                var (_, doodads) = Around(map, gone.Pos, 2);
                var left = doodads.Where(d => gone.Boxes.Any(b => b.Depth(d.pos) > 0f)).GroupBy(d => Path.GetFileNameWithoutExtension(d.path)).ToList();
                string subject = $"dropped {Path.GetFileName(path)}";
                Add("G6", left.Count > 0 ? "warn" : "info", subject,
                    left.Count == 0 ? "no ADT props left in its footprint"
                        : $"{left.Sum(g => g.Count())} prop(s) left standing where it was: " + string.Join(", ", left.Take(8).Select(g => $"{g.Count()}×{g.Key}")),
                    map, W(gone.Pos));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════ G7 spawns, G8 portals

    private string? IndoorsOf(int map, Vector3 world)
    {
        var pl = WorldCoords.WorldToPlacement(world);
        var (wmos, _) = Around(map, pl, 0);
        foreach (var w in wmos)
            if (w.Boxes.Any(b => b.Depth(pl + new Vector3(0, 1, 0)) > 0)) return Path.GetFileName(w.Path);
        return null;
    }

    /// <summary>The building whose footprint the point stands over (above its boxes: its roof).</summary>
    private string? RoofUnder(int map, Vector3 world)
    {
        var pl = WorldCoords.WorldToPlacement(world);
        var (wmos, _) = Around(map, pl, 0);
        foreach (var w in wmos)
            foreach (var b in w.Boxes)
            {
                var below = pl - new Vector3(0, b.Half.Length() * 2f, 0);   // placement Y is up
                for (float t = 0; t <= 1f; t += 0.05f)
                    if (b.Depth(Vector3.Lerp(pl, below, t)) > 0) return Path.GetFileName(w.Path);
            }
        return null;
    }

    private void Spawns()
    {
        int ok = 0, indoor = 0;
        foreach (var table in new[] { "creature", "gameobject" })
            foreach (var (_, r) in Docs("dbrow:" + table))
            {
                int map = I(r, "map");
                var at = new Vector3(F(r, "position_x"), F(r, "position_y"), F(r, "position_z"));
                long entry = L(r, "id");
                string subject = $"{table} {L(r, "guid")} ({Name(table, entry)})";
                if (!_in.MapDirs.ContainsKey(map)) { Add("G7", "error", subject, $"map {map} does not exist", map, at); continue; }
                float? h = Ground(map, at.X, at.Y);
                if (h == null) { Add("G7", "error", subject, "no terrain tile under the spawn", map, at); continue; }
                string? inWmo = IndoorsOf(map, at);
                if (inWmo != null) { indoor++; if (at.Z < h - 0.5f && table == "creature") Add("G7", "info", subject, $"below terrain inside {inWmo} (basement/cellar? client check)", map, at); continue; }
                float slope = 0;
                for (int k = 0; k < 8; k++)
                {
                    float a = k * MathF.PI / 4f;
                    if (Ground(map, at.X + MathF.Cos(a) * 1.5f, at.Y + MathF.Sin(a) * 1.5f) is { } n) slope = MathF.Max(slope, MathF.Abs(n - h.Value) / 1.5f);
                }
                float slopeDeg = MathF.Atan(slope) * 180f / MathF.PI;
                if (slopeDeg > 50f && MathF.Abs(at.Z - h.Value) < 1.5f)
                    Add("G7", "error", subject, $"stands on a {slopeDeg:F0} degree slope (walkable ~50): players slide off, it cannot chase", map, at);
                if (at.Z < h - 0.5f) Add("G7", "error", subject, $"{h - at.Z:F1} yd under the ground", map, at);
                else if (at.Z > h + 2.5f)
                {
                    string? under = RoofUnder(map, at);
                    Add("G7", under != null ? "error" : "warn", subject, under != null
                        ? $"standing on top of {under} ({at.Z - h:F1} yd above the terrain) - a roof is not a spawn point"
                        : $"floating {at.Z - h:F1} yd above the terrain (no building here)", map, at);
                }
                else ok++;
            }
        Add("G7", "info", "spawns", $"{ok} outdoor spawn(s) on the terrain; {indoor} inside buildings (collision checked by the client pass)");
    }

    private void Portals()
    {
        // The client learns a pack portal's name + destination only from patch-7's teleport table
        // (WorldPackBuildService.TeleportsMpqPath); without it the portal is a bare trigger volume.
        var table = _in.Built(WorldPackBuildService.TeleportsMpqPath);
        var shipped = table == null ? new HashSet<string>()
            : Encoding.UTF8.GetString(table).Split('\n').Skip(1).Select(l => l.Split('\t')[0].Trim()).ToHashSet();
        foreach (var (_, r) in Docs("dbrow:areatrigger_teleport"))
        {
            int map = I(r, "target_map");
            var at = new Vector3(F(r, "target_position_x"), F(r, "target_position_y"), F(r, "target_position_z"));
            string subject = $"portal {L(r, "id")} {S(r, "name")}";
            if (!shipped.Contains(L(r, "id").ToString()))
                Add("G8", "error", subject, "missing from patch-7's portal table (WorldPacks\\areatrigger_teleport.tsv): the client shows a nameless volume with no destination - republish");
            if (!_in.MapDirs.ContainsKey(map)) { Add("G8", "error", subject, $"target map {map} does not exist", map, at); continue; }
            float? h = Ground(map, at.X, at.Y);
            if (h == null) { Add("G8", "error", subject, "target is off the map's terrain", map, at); continue; }
            string? inWmo = IndoorsOf(map, at);
            if (inWmo == null && at.Z < h - 0.5f) Add("G8", "error", subject, $"arrival {h - at.Z:F1} yd under the ground", map, at);
            else if (inWmo == null && at.Z > h + 4f) Add("G8", "warn", subject, $"arrival {at.Z - h:F1} yd above the ground (players fall)", map, at);
            else Add("G8", "info", subject, inWmo != null ? $"arrives inside {inWmo} (client pass checks the floor)" : $"arrival on the ground (terrain {h:F1})", map, at);
        }
    }

    // ═══════════════════════════════════════════════════════════════ G13 graded paths

    /// <summary>
    /// Every graded path (doc kind "path") on the PUBLISHED ground, sampled every 2 yd along its polyline: a
    /// grade a player cannot walk (over 45 degrees) is an error, a stretch more than 1.5 yd under water a warning.
    /// A pass or a land bridge that is not walkable is not a pass.
    /// </summary>
    private void Paths()
    {
        foreach (var (doc, b) in Docs("path"))
        {
            string key = doc.DocKey;
            var path = GradedPath.Parse(key, b.ToJsonString());
            int samples = 0; float worst = 0f; Vector3? worstAt = null; Vector3? wet = null;
            (string wmo, Vector3 at)? building = null;
            float? prev = null; Vector2 prevP = default;
            for (int i = 0; i + 1 < path.Points.Count; i++)
            {
                var a = new Vector2(path.Points[i].X, path.Points[i].Y);
                var ab = new Vector2(path.Points[i + 1].X, path.Points[i + 1].Y) - a;
                int n = Math.Max(1, (int)(ab.Length() / 2f));
                for (int k = (i == 0 ? 0 : 1); k <= n; k++)
                {
                    var p = a + ab * ((float)k / n);
                    if (Ground(path.Map, p.X, p.Y) is not float h) continue;
                    samples++;
                    if (prev is float ph && Vector2.Distance(p, prevP) > 0.5f)
                    {
                        float deg = MathF.Atan(MathF.Abs(h - ph) / Vector2.Distance(p, prevP)) * 180f / MathF.PI;
                        if (deg > worst) { worst = deg; worstAt = new Vector3(p.X, p.Y, h); }
                    }
                    if (wet == null && Water(path.Map, p.X, p.Y) is float lvl && lvl - h > 1.5f) wet = new Vector3(p.X, p.Y, h);
                    // 2026-09-27 live: greymane-pass ENDED inside a farmhouse footprint - the walk stopped at its wall.
                    if (building == null && IndoorsOf(path.Map, new Vector3(p.X, p.Y, h)) is { } wmo) building = (wmo, new Vector3(p.X, p.Y, h));
                    prev = h; prevP = p;
                }
            }
            if (samples == 0) { Add("G13", "error", $"path {key}", "no published ground under the path (is it on a patched tile?)", path.Map, path.Points[0]); continue; }
            if (worst > 45f) Add("G13", "error", $"path {key}", $"grade {worst:F0} degrees at ({worstAt!.Value.X:F0}, {worstAt.Value.Y:F0}) - players cannot walk it (45 max)", path.Map, worstAt.Value);
            else Add("G13", "info", $"path {key}", $"{samples} samples, steepest grade {worst:F0} degrees", path.Map, path.Points[0]);
            if (wet is { } w) Add("G13", "warn", $"path {key}", $"runs under water at ({w.X:F0}, {w.Y:F0}) - raise it or it is a swim", path.Map, w);
            if (building is { } bd)
                Add("G13", "warn", $"path {key}", $"runs into {bd.wmo} at ({bd.at.X:F0}, {bd.at.Y:F0}) - a wall stops the walk unless it is a passage (arch, gate): move the point or prove it with tier 3 paths", path.Map, bd.at);
        }
    }

    // ═══════════════════════════════════════════════════════════════ G14 patrol routes

    /// <summary>
    /// Every patrol (a pack creature with movement_type 2 and its creature_movement points) on the PUBLISHED
    /// ground: each waypoint within reach of the terrain (a point in the air or under the ground is walked to by
    /// the navmesh's nearest poly - the mob cuts corners or stalls), and each leg - including the closing leg from
    /// the last point back to the first, vanilla waypoints loop - sampled every 2 yd for grade, deep water and
    /// buildings in the way. The server paths each leg on the navmesh; tier 3 (track) proves the walk.
    /// </summary>
    private void PatrolGround()
    {
        var points = Rows("creature_movement").GroupBy(r => L(r, "id")).ToDictionary(g => g.Key, g => g.OrderBy(r => L(r, "point")).ToList());
        foreach (var (_, c) in Docs("dbrow:creature"))
        {
            if (I(c, "movement_type") != 2 || !points.TryGetValue(L(c, "guid"), out var pts) || pts.Count < 2) continue;
            int map = I(c, "map");
            string subject = $"patrol {L(c, "guid")} ({Name("creature", L(c, "id"))})";
            var route = pts.Select(p => new Vector3(F(p, "position_x"), F(p, "position_y"), F(p, "position_z"))).ToList();
            foreach (var p in route)
            {
                if (Ground(map, p.X, p.Y) is not float h) { Add("G14", "error", subject, $"waypoint ({p.X:F0}, {p.Y:F0}) has no terrain under it", map, p); continue; }
                if (IndoorsOf(map, p) == null && (p.Z < h - 1f || p.Z > h + 3f))
                    Add("G14", "warn", subject, $"waypoint ({p.X:F0}, {p.Y:F0}) is {p.Z - h:F1} yd off the ground (terrain {h:F1}) - snap it", map, p);
            }
            float worst = 0f; Vector3? worstAt = null, wet = null; (string wmo, Vector3 at)? building = null;
            for (int i = 0; i < route.Count; i++)
            {
                var a = new Vector2(route[i].X, route[i].Y);
                var b = new Vector2(route[(i + 1) % route.Count].X, route[(i + 1) % route.Count].Y);
                int n = Math.Max(1, (int)(Vector2.Distance(a, b) / 2f));
                float? prev = null; Vector2 prevP = a;
                for (int k = 0; k <= n; k++)
                {
                    var p = a + (b - a) * ((float)k / n);
                    if (Ground(map, p.X, p.Y) is not float h) continue;
                    if (prev is float ph && Vector2.Distance(p, prevP) > 0.5f)
                    {
                        float deg = MathF.Atan(MathF.Abs(h - ph) / Vector2.Distance(p, prevP)) * 180f / MathF.PI;
                        if (deg > worst) { worst = deg; worstAt = new Vector3(p.X, p.Y, h); }
                    }
                    if (wet == null && Water(map, p.X, p.Y) is float lvl && lvl - h > 1.5f) wet = new Vector3(p.X, p.Y, h);
                    if (building == null && k > 0 && k < n && IndoorsOf(map, new Vector3(p.X, p.Y, h)) is { } wmo) building = (wmo, new Vector3(p.X, p.Y, h));
                    prev = h; prevP = p;
                }
            }
            if (worst > 50f) Add("G14", "error", subject, $"a leg climbs {worst:F0} degrees at ({worstAt!.Value.X:F0}, {worstAt.Value.Y:F0}) - it cannot walk it", map, worstAt.Value);
            if (wet is { } w) Add("G14", "warn", subject, $"a leg runs under water at ({w.X:F0}, {w.Y:F0})", map, w);
            if (building is { } bd)
            {
                // The server walks each leg on the navmesh: legs whose ends share a component are walkable around or
                // through the building (a city is ONE WMO - every leg in it is "inside" its group boxes).
                bool connected = Nav(map) is { } nav && route.Select(p => nav.At(p.X, p.Y, p.Z)?.Component).Distinct().Count() == 1
                                 && nav.At(route[0].X, route[0].Y, route[0].Z) != null;
                if (connected)
                    Add("G14", "info", subject, $"legs run through {bd.wmo} at ({bd.at.X:F0}, {bd.at.Y:F0}); every waypoint is on one navmesh component, the server paths them", map, bd.at);
                else
                    Add("G14", "warn", subject, $"a leg crosses {bd.wmo} at ({bd.at.X:F0}, {bd.at.Y:F0}) - fine through a gate or arch, a wall stops it: prove it with tier 3 (track)", map, bd.at);
            }
            if (worst <= 50f && wet == null && building == null)
                Add("G14", "info", subject, $"{route.Count} waypoints on the ground, steepest leg {worst:F0} degrees", map, route[0]);
        }
    }

    // ═══════════════════════════════════════════════════════════════ G15 reachability (server navmesh)

    private readonly Dictionary<int, WorldPackNavMesh?> _nav = new();

    /// <summary>The installed navmesh of a map: every tile of a pack instance, or the tiles under the pack's spawns
    /// (+ neighbours) on a continent. Null when mmaps are not available (pre-flight) or the map has none.</summary>
    private WorldPackNavMesh? Nav(int map)
    {
        if (_in.Mmaps is not { } dir) return null;
        if (_nav.TryGetValue(map, out var cached)) return cached;
        IEnumerable<(int, int)>? cells = IsPackInstance(map) ? null
            : Docs("dbrow:creature").Where(d => I(d.body, "map") == map)
                .Select(d => WorldPackNavMesh.CellOf(F(d.body, "position_x"), F(d.body, "position_y"))).Distinct().ToList();
        return _nav[map] = WorldPackNavMesh.Load(dir, map, cells);
    }

    private bool IsPackInstance(int map) => Docs("map").Any(d => (int?)d.body["mapId"] == map && ((int?)d.body["instanceType"] ?? 0) != 0);

    /// <summary>A creature players fight (the navmesh governs how it chases): no service flags, and loot, a rank, EventAI
    /// or a quest kill objective. Players themselves walk on collision, not the navmesh - a farmhand on a porch is fine.</summary>
    private bool Combatant(long entry)
    {
        var t = Rows("creature_template").FirstOrDefault(r => L(r, "entry") == entry);
        if (t is null) return true;
        if (L(t, "npc_flags") != 0) return false;
        return L(t, "loot_id") != 0 || I(t, "rank") > 0 || !string.IsNullOrEmpty(t["ai_name"]?.ToString()) ||
               Rows("quest_template").Any(q => Enumerable.Range(1, 4).Any(i => L(q, $"ReqCreatureOrGOId{i}") == entry));
    }

    /// <summary>
    /// Can it be walked to? The server moves creatures and party bots on the navmesh (vmangos mmaps), and two points on
    /// different navmesh components have no path. On a pack INSTANCE map every pack creature and every patrol waypoint
    /// must be on the component of the entrance arrival (areatrigger_teleport target): map 801's Baron Ashbury and Lord
    /// Walden stood in a walled ward that was its own island - ".mmap path" INCOMPLETE, the boss evaded "target
    /// unreachable", and every trial had teleported the group in (2026-09-27). On a continent a pack creature on a tiny
    /// island (a roof, a rock top) can neither chase nor be reached: warning. Patrol waypoints must share their leader's
    /// component (a waypoint on another island is where the patrol stops).
    /// </summary>
    private void Reachability()
    {
        if (_in.Mmaps is null) return;
        var points = Rows("creature_movement").GroupBy(r => L(r, "id")).ToDictionary(g => g.Key, g => g.OrderBy(r => L(r, "point")).ToList());
        foreach (var byMap in Docs("dbrow:creature").GroupBy(d => I(d.body, "map")))
        {
            int map = byMap.Key;
            var nav = Nav(map);
            if (nav is null) { Add("G15", "warn", "navmesh", $"map {map}: no installed navmesh (mmaps) - reachability not checked", map); continue; }
            bool instance = IsPackInstance(map);
            var entrances = Rows("areatrigger_teleport").Where(r => I(r, "target_map") == map)
                .Select(r => new Vector3(F(r, "target_position_x"), F(r, "target_position_y"), F(r, "target_position_z"))).ToList();
            var home = new HashSet<int>();
            foreach (var e in entrances)
            {
                if (nav.At(e.X, e.Y, e.Z, 4f) is { } c) home.Add(c.Component);
                else if (instance) Add("G15", "error", "entrance", $"the entrance arrival ({e.X:F0}, {e.Y:F0}) has no navmesh under it - nothing can path from it", map, e);
            }
            int ok = 0, waypoints = 0;
            foreach (var (_, c) in byMap)
            {
                if (!instance && !Combatant(L(c, "id"))) continue;
                var at = new Vector3(F(c, "position_x"), F(c, "position_y"), F(c, "position_z"));
                string subject = $"spawn {L(c, "guid")} ({Name("creature", L(c, "id"))})";
                var here = nav.At(at.X, at.Y, at.Z);
                if (here is null) { Add("G15", instance ? "error" : "warn", subject, $"no walkable navmesh under ({at.X:F0}, {at.Y:F0}) - it cannot move, and nothing can path to it", map, at); continue; }
                if (instance && home.Count > 0 && !home.Contains(here.Value.Component))
                {
                    Add("G15", "error", subject, $"UNREACHABLE: stands on a navmesh island of {nav.Area(here.Value.Component):F0} sq yd, not the entrance's " +
                        $"({string.Join("/", home.Select(h => nav.Area(h).ToString("F0")))} sq yd) - the group cannot walk to it and it evades 'target unreachable'; move it (movespawn) or open the way", map, at);
                    continue;
                }
                if (!instance && nav.Area(here.Value.Component) < 1500f)
                {
                    Add("G15", "warn", subject, $"stands on a navmesh island of {nav.Area(here.Value.Component):F0} sq yd (a roof, a rock top?) - it cannot chase and cannot be reached on foot", map, at);
                    continue;
                }
                ok++;
                if (I(c, "movement_type") == 2 && points.TryGetValue(L(c, "guid"), out var pts))
                    foreach (var pt in pts)
                    {
                        waypoints++;
                        var wp = new Vector3(F(pt, "position_x"), F(pt, "position_y"), F(pt, "position_z"));
                        if (nav.At(wp.X, wp.Y, wp.Z)?.Component != here.Value.Component)
                            Add("G15", "error", $"patrol {L(c, "guid")} ({Name("creature", L(c, "id"))})",
                                $"waypoint {L(pt, "point")} ({wp.X:F0}, {wp.Y:F0}) is not on the leader's navmesh component - the patrol stops there", map, wp);
                    }
            }
            Add("G15", "info", "navmesh", instance
                ? $"map {map}: {ok} pack creature(s) and {waypoints} patrol waypoint(s) on the entrance's navmesh component ({string.Join("/", home.Select(h => nav.Area(h).ToString("F0")))} sq yd, {nav.Tiles} tile(s))"
                : $"map {map}: {ok} pack combatant(s) and {waypoints} patrol waypoint(s) on walkable navmesh ({nav.Tiles} tile(s) read)", map);
        }
    }

    // ═══════════════════════════════════════════════════════════════ G12 terrain holes

    /// <summary>
    /// A terrain hole is only correct where a WMO fills it (cellar, cave or mine mouth, crypt): its
    /// floor catches the player. A hole with no building geometry over it drops players into the void -
    /// e.g. after a drop, a stamp without its WMOs, or a client without the building's collision.
    /// Vanilla MCNK holes: 16 bits, bit (y * 4 + x) = one quarter-chunk square.
    /// </summary>
    private void Holes()
    {
        var keys = Docs("tile").Select(x => ((int)x.body["map"]!, (int)x.body["col"]!, (int)x.body["row"]!)).ToList();
        var manifest = _in.Built(WorldPackBuildService.ManifestMpqPath);
        if (manifest != null)
            foreach (var k in (JsonNode.Parse(manifest)?["adts"] as JsonArray)?.Select(n => n!.ToString()) ?? Enumerable.Empty<string>())
            {
                var p = k.Split(':', '_');
                keys.Add((int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])));
            }
        int holes = 0, open = 0;
        foreach (var (map, col, row) in keys.Distinct())
        {
            var adt = Adt(map, col, row);
            if (adt == null) continue;
            for (int i = 0; i < 256; i++)
            {
                ushort mask = adt.HolesOf(i);
                if (mask == 0) continue;
                var (_, _, ox, oy, _) = adt.ChunkInfo(i);
                bool reported = false;   // one "covered hole" info per chunk: tier 3 walks each of these
                for (int b = 0; b < 16; b++)
                {
                    if ((mask & (1 << b)) == 0) continue;
                    holes++;
                    int hx = b % 4, hy = b / 4;
                    float x = ox - (hy + 0.5f) * WorldCoords.Chunk / 4f, y = oy - (hx + 0.5f) * WorldCoords.Chunk / 4f;
                    float h = Ground(map, x, y) ?? 0f;
                    var (wmos, _) = Around(map, WorldCoords.WorldToPlacement(new Vector3(x, y, h)), 1);
                    // Covered = some building group box spans the hole's column from a little under the
                    // terrain surface to a little above it (its floor or ramp is what the player lands on).
                    bool covered = wmos.Any(w => w.Boxes.Any(bx =>
                        Enumerable.Range(-8, 11).Any(dz => bx.Depth(WorldCoords.WorldToPlacement(new Vector3(x, y, h + dz))) > 0)));
                    if (covered && !reported)
                    {
                        reported = true;
                        Add("G12", "info", "hole", $"covered terrain hole (a building's floor must catch players here)", map, new Vector3(x, y, h));
                    }
                    if (!covered)
                    {
                        open++;
                        Add("G12", "error", $"tile {map}:{col},{row}", $"terrain hole with no building over it: players fall into the void at ({x:F0}, {y:F0})", map, new Vector3(x, y, h));
                    }
                }
            }
        }
        Add("G12", open == 0 ? "info" : "error", "terrain holes", $"{holes} hole square(s) on pack/patched tiles, {open} without building geometry over them");
        // Healed squares (tile doc healHoles): the published tile must no longer have them.
        foreach (var (_, t) in Docs("tile"))
            foreach (var h in (t["healHoles"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                int map = (int)t["map"]!, col = (int)t["col"]!, row = (int)t["row"]!;
                float x = (float)h["x"]!, y = (float)h["y"]!;
                var adt = Adt(map, col, row);
                if (adt == null) continue;
                bool still = false;
                for (int i = 0; i < 256 && !still; i++)
                {
                    var (_, _, ox, oy, _) = adt.ChunkInfo(i);
                    if (x > ox || x <= ox - WorldCoords.Chunk || y > oy || y <= oy - WorldCoords.Chunk) continue;
                    int hy = Math.Clamp((int)((ox - x) / (WorldCoords.Chunk / 4f)), 0, 3), hx = Math.Clamp((int)((oy - y) / (WorldCoords.Chunk / 4f)), 0, 3);
                    still = (adt.HolesOf(i) & (1 << (hy * 4 + hx))) != 0;
                }
                Add("G12", still ? "error" : "info", $"healed hole ({x:F0}, {y:F0})",
                    still ? "the tile doc heals this square but the published tile still has the hole (republish)" : "hole square closed by the pack (terrain drawn and solid)",
                    map, new Vector3(x, y, Ground(map, x, y) ?? 0f));
            }
    }

    // ═══════════════════════════════════════════════════════════════ G11 minimap

    private void Minimaps()
    {
        var tiles = Docs("tile").Select(x => x.body).ToList();
        if (tiles.Count == 0) return;
        var trs = _in.Built(WorldPackBuildService.MinimapTrsPath);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (trs != null)
            foreach (var line in System.Text.Encoding.UTF8.GetString(trs).Split('\n'))
            {
                var parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) map[parts[0]] = parts[1];
            }
        int ok = 0, rendered = 0;
        foreach (var t in tiles)
        {
            int m = (int)t["map"]!, col = (int)t["col"]!, row = (int)t["row"]!;
            if (!_in.MapDirs.TryGetValue(m, out var dir)) continue;
            string key = $@"{dir}\map{col}_{row}.blp";
            string source = $@"{(string?)t["sourceMap"]}\map{(int?)t["sourceCol"]}_{(int?)t["sourceRow"]}.blp";
            if (!map.TryGetValue(key, out var hash))
                Add("G11", "warn", $"tile {m}:{col},{row}", $"no minimap image ({key} not in md5translate.trs): the minimap is empty there", m, TileCentre(col, row));
            else if ((_in.Built(@"textures\Minimap\" + hash) ?? _in.Stock(@"textures\Minimap\" + hash)) == null)
                Add("G11", "warn", $"tile {m}:{col},{row}", $"minimap entry points at a missing image ({hash})", m, TileCentre(col, row));
            // Re-rendered by the build where the published ground differs from its source (WorldPackMinimap).
            else if (hash.StartsWith("wp_", StringComparison.OrdinalIgnoreCase)) { ok++; rendered++; }
            // A stamp over a stock continent tile must show its SOURCE image, not the replaced tile's (open sea).
            else if (map.TryGetValue(source, out var want) && !string.Equals(want, hash, StringComparison.OrdinalIgnoreCase))
                Add("G11", "warn", $"tile {m}:{col},{row}", $"minimap still shows the replaced tile ({hash}), not its source {source}", m, TileCentre(col, row));
            else ok++;
        }
        Add("G11", "info", "minimap", $"{ok} of {tiles.Count} pack-map tile(s) have a minimap image ({rendered} re-rendered where the published ground differs from their source)");
    }

    // ═══════════════════════════════════════════════════════════════ G10 water

    /// <summary>Liquid surface at a world point (its MCNK's MCLQ), or null where there is no liquid.</summary>
    public float? Water(int map, float x, float y)
    {
        int col = WorldCoords.TileCol(y), row = WorldCoords.TileRow(x);
        var adt = Adt(map, col, row);
        if (adt == null) return null;
        float gr = ((32 - row) * WorldCoords.Tile - x) / WorldCoords.Unit;
        float gc = ((32 - col) * WorldCoords.Tile - y) / WorldCoords.Unit;
        int idx = adt.ChunkIndex(Math.Clamp((int)(gc / 8), 0, 15), Math.Clamp((int)(gr / 8), 0, 15));
        return idx < 0 ? null : adt.LiquidLevel(idx);
    }

    private void Water()
    {
        foreach (var p in _in.Placements.Where(p => !p.Deleted))
        {
            string name = Path.GetFileNameWithoutExtension(p.ModelPath).ToLowerInvariant();
            bool dock = name.Contains("dock") || name.Contains("pier") || name.Contains("jetty") || name.Contains("boardwalk");
            float? level = Water(p.MapId, p.PosX, p.PosY);
            if (dock && level == null)   // a dock is judged by the water beside it, not under its origin
                for (float r = 3; r <= 15 && level == null; r += 3)
                    for (int k = 0; k < 12 && level == null; k++)
                        level = Water(p.MapId, p.PosX + MathF.Cos(k * MathF.Tau / 12) * r, p.PosY + MathF.Sin(k * MathF.Tau / 12) * r);
            if (level == null)
            {
                if (dock) Add("G10", "warn", $"placement #{p.Id} {Path.GetFileName(p.ModelPath)}", "a dock with no water within 15 yd", p.MapId, new Vector3(p.PosX, p.PosY, p.PosZ));
                continue;
            }
            float? ground = Ground(p.MapId, p.PosX, p.PosY);
            if (!dock && ground is { } g && g > level) continue;                // on dry land beside the water
            string subject = $"placement #{p.Id} {Path.GetFileName(p.ModelPath)}";
            var at = new Vector3(p.PosX, p.PosY, p.PosZ);
            float lv = level.Value;
            if (name.Contains("boat") || name.Contains("ship") || name.Contains("raft"))
            {
                if (p.PosZ < lv - 0.6f) Add("G10", "warn", subject, $"sunk {lv - p.PosZ:F1} yd below the water surface ({lv:F1})", p.MapId, at);
                else if (p.PosZ > lv + 1.0f) Add("G10", "warn", subject, $"hovering {p.PosZ - lv:F1} yd above the water surface ({lv:F1})", p.MapId, at);
                else Add("G10", "info", subject, $"floats on the water (surface {level:F1})", p.MapId, at);
            }
            else if (dock)
            {
                var b = p.Kind == "wmo" ? ModelBounds.Wmo(_in.Built(p.ModelPath) ?? _in.Stock(p.ModelPath))
                                        : ModelBounds.M2(_in.Stock(Path.ChangeExtension(p.ModelPath, ".m2")));
                if (b is not { } bb) continue;
                // A ramp spans elevations deliberately; it needs entry/exit walking proof, not a
                // flat-deck height limit applied to one arbitrary triangle's centroid.
                var surface = p.Kind == "wmo" ? null : ModelBounds.M2WalkableSurface(_in.Stock(Path.ChangeExtension(p.ModelPath, ".m2")));
                float? measuredDeck = null;
                if (surface is not null)
                {
                    var matrix = WorldPackGeometry.WmoMatrix(WorldCoords.WorldToPlacement(at), new(p.RotX, p.RotY, p.RotZ));
                    var normal = Vector3.Normalize(Vector3.TransformNormal(surface.Normal, matrix));
                    float slope = MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * 180f / MathF.PI;
                    var heights = surface.Vertices.Select(v => WorldCoords.PlacementToWorld(Vector3.Transform(v * p.Scale, matrix)).Z).ToArray();
                    if (slope > 1 && heights.Max() - heights.Min() > .25f)
                    {
                        Add("G10", "info", subject,
                            $"sloped walkable surface spans {heights.Min() - lv:F1}–{heights.Max() - lv:F1} yd above water at {slope:F1} degrees; entry/exit walking proof required",
                            p.MapId, at);
                        continue;
                    }
                    measuredDeck = WorldCoords.PlacementToWorld(Vector3.Transform(surface.Center * p.Scale, matrix)).Z;
                }
                // Flat decks retain the water-height thresholds. The bounding box top is often posts.
                float deckLocal = p.Kind == "wmo" ? bb.max.Z
                    : ModelBounds.M2WalkableTop(_in.Stock(Path.ChangeExtension(p.ModelPath, ".m2"))) ?? bb.max.Z;
                float deck = measuredDeck ?? p.PosZ + deckLocal * p.Scale;
                if (deck < lv + 0.2f) Add("G10", "warn", subject, $"deck {deck:F1} is at/under the water surface ({lv:F1})", p.MapId, at);
                else if (deck > lv + 4f) Add("G10", "warn", subject, $"deck {deck:F1} is {deck - lv:F1} yd above the water - players cannot step off into it", p.MapId, at);
                else Add("G10", "info", subject, $"deck {deck - lv:F1} yd above the water", p.MapId, at);
            }
        }
        foreach (var (_, r) in Docs("dbrow:creature"))
        {
            int map = I(r, "map");
            var at = new Vector3(F(r, "position_x"), F(r, "position_y"), F(r, "position_z"));
            if (!_in.MapDirs.ContainsKey(map) || Water(map, at.X, at.Y) is not { } level) continue;
            if (level - at.Z > 1.5f && IndoorsOf(map, at) == null)
                Add("G10", "warn", $"creature {L(r, "guid")} ({Name("creature", L(r, "id"))})", $"stands {level - at.Z:F1} yd under the water surface", map, at);
        }
    }

    // ═══════════════════════════════════════════════════════════════ G9 terrain integrity

    /// <summary>Every ADT the build patched (build manifest): shared edge heights must agree between
    /// chunks and across tile borders (a mismatch is a visible crack and a hole in the navmesh), and on
    /// stock tiles a normal may differ from Blizzard's only where a height it depends on moved.</summary>
    private void TerrainIntegrity()
    {
        var manifest = _in.Built(WorldPackBuildService.ManifestMpqPath);
        if (manifest == null) { Add("G9", "warn", "terrain", "no build manifest in the published patch"); return; }
        var adts = (JsonNode.Parse(manifest)?["adts"] as JsonArray)?.Select(n => n!.ToString()).ToList() ?? new();
        int tiles = 0, cracks = 0, drift = 0;
        foreach (var key in adts)
        {
            var parts = key.Split(':', '_');
            int map = int.Parse(parts[0]), col = int.Parse(parts[1]), row = int.Parse(parts[2]);
            var adt = Adt(map, col, row);
            if (adt == null) continue;
            tiles++;
            string subject = $"tile {map}:{col},{row}";
            // Tile borders: my column 128 is the east neighbour's column 0, my row 128 the south neighbour's row 0.
            foreach (var (dc, dr) in new[] { (1, 0), (0, 1), (-1, 0), (0, -1) })
            {
                var n = Adt(map, col + dc, row + dr);
                if (n == null) continue;
                float worst = 0;
                for (int k = 0; k <= 128; k++)
                {
                    float a = dc != 0 ? adt.OuterHeight(k, dc > 0 ? 128 : 0) : adt.OuterHeight(dr > 0 ? 128 : 0, k);
                    float b = dc != 0 ? n.OuterHeight(k, dc > 0 ? 0 : 128) : n.OuterHeight(dr > 0 ? 0 : 128, k);
                    worst = MathF.Max(worst, MathF.Abs(a - b));
                }
                if (worst > 0.05f) { cracks++; Add("G9", "error", subject, $"crack along the border with tile {col + dc},{row + dr}: heights differ by up to {worst:F2} yd", map, TileCentre(col, row)); }
            }
            // Stock tiles: normal drift outside moved heights.
            if (!_in.MapDirs.TryGetValue(map, out var dir)) continue;
            var stockBytes = _in.Stock(WorldCoords.AdtPath(dir, col, row));
            if (stockBytes == null) continue;
            var stock = AdtDocument.Parse(stockBytes, col, row);
            var moved = new bool[129 * 129];
            for (int r = 0; r <= 128; r++)
                for (int c = 0; c <= 128; c++)
                    moved[r * 129 + c] = MathF.Abs(stock.OuterHeight(r, c) - adt.OuterHeight(r, c)) > 0.001f;
            int stray = 0, maxDiff = 0;
            for (int i = 0; i < 256; i++)
            {
                var (ix, iy, _, _, _) = stock.ChunkInfo(i);
                int j = adt.ChunkIndex(ix, iy);
                if (j < 0) continue;
                byte[] a = stock.NormalsOf(i), b = adt.NormalsOf(j);
                for (int v = 0; v < 145; v++)
                {
                    int d = Math.Max(Math.Abs((sbyte)a[v * 3] - (sbyte)b[v * 3]), Math.Max(Math.Abs((sbyte)a[v * 3 + 1] - (sbyte)b[v * 3 + 1]), Math.Abs((sbyte)a[v * 3 + 2] - (sbyte)b[v * 3 + 2])));
                    if (d == 0) continue;
                    bool inner = v % 17 >= 9;
                    int gr = iy * 8 + v / 17, gc = ix * 8 + (inner ? v % 17 - 9 : v % 17);
                    bool near = false;
                    for (int r = Math.Max(gr - 1, 0); r <= Math.Min(gr + 2, 128) && !near; r++)
                        for (int c = Math.Max(gc - 1, 0); c <= Math.Min(gc + 2, 128) && !near; c++)
                            near = moved[r * 129 + c];
                    if (!near) { stray++; maxDiff = Math.Max(maxDiff, d); }
                }
            }
            if (stray > 0) { drift++; Add("G9", "warn", subject, $"{stray} normal(s) differ from stock where no height moved (max {maxDiff}/127): shading patches", map, TileCentre(col, row)); }
        }
        Add("G9", cracks + drift == 0 ? "info" : "warn", "terrain", $"{tiles} patched tile(s): {cracks} border crack(s), {drift} tile(s) with normal drift outside the sculpt");
    }

    private static Vector3 TileCentre(int col, int row) =>
        new((32 - row - 0.5f) * WorldCoords.Tile, (32 - col - 0.5f) * WorldCoords.Tile, 0);

    // ═══════════════════════════════════════════════════════════════ content

    private readonly Dictionary<string, List<JsonObject>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, JsonObject>> _dbc = new(StringComparer.OrdinalIgnoreCase);

    private void LoadRows()
    {
        foreach (var d in _in.Docs)
        {
            if (string.IsNullOrEmpty(d.Body)) continue;
            if (d.Kind.StartsWith("dbrow:"))
            {
                if (!_rows.TryGetValue(d.Kind[6..], out var l)) _rows[d.Kind[6..]] = l = new();
                l.Add(JsonNode.Parse(d.Body)!.AsObject());
            }
            else if (d.Kind.StartsWith("dbc:"))
            {
                if (!_dbc.TryGetValue(d.Kind[4..], out var m)) _dbc[d.Kind[4..]] = m = new();
                m[d.DocKey] = JsonNode.Parse(d.Body)!.AsObject();
            }
        }
    }

    private List<JsonObject> Rows(string t) => _rows.GetValueOrDefault(t) ?? new();
    private static long L(JsonObject r, string c) => r[c] is JsonValue v && long.TryParse(WorldPackContent.Scalar(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;
    private static int I(JsonObject r, string c) => (int)L(r, c);
    private static float F(JsonObject r, string c) => r[c] is JsonValue v && float.TryParse(WorldPackContent.Scalar(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    private static string S(JsonObject r, string c) => r[c] is JsonValue v ? WorldPackContent.Scalar(v) : "";

    private bool Exists(string table, long id, string keyCol = "entry") =>
        Rows(table).Any(r => L(r, keyCol) == id) || (_in.Facts?.Has(table, id) ?? true);
    private string Name(string table, long entry) =>
        Rows(table + "_template").FirstOrDefault(r => L(r, "entry") == entry) is { } t ? S(t, "name") : $"entry {entry}";

    private const uint Gossip = 0x1, QuestGiver = 0x2, Vendor = 0x4, Trainer = 0x10, SpiritHealer = 0x20,
        Innkeeper = 0x80, Banker = 0x100, Repair = 0x4000;
    private static readonly (uint flag, string what)[] Services =
    {
        // A quest giver's quests are listed through its menu's QUESTGIVER option too.
        (QuestGiver, "quest-giver"), (Vendor, "vendor"), (Trainer, "trainer"), (Innkeeper, "innkeeper"), (Banker, "banker"),
        (SpiritHealer, "spirit healer"), (Repair, "repair"),
    };

    private void Content()
    {
        if (_in.Facts == null) Add("C", "info", "stock facts", "offline run: references to stock rows (items, spells, displays) were not checked");
        var templates = Rows("creature_template").ToDictionary(r => L(r, "entry"));
        var spawnsByEntry = Rows("creature").GroupBy(r => L(r, "id")).ToDictionary(g => g.Key, g => g.Count());
        // A replacement retargets one existing stock spawn; it has no reserved creature row.
        // Count validated replacement documents for both unused-template and quest-reachability checks.
        foreach (var replacement in WorldPackNpcReplacements.ReadDesired(_in.Docs))
            spawnsByEntry[replacement.ReplacementEntry] = spawnsByEntry.GetValueOrDefault(replacement.ReplacementEntry) + 1;
        int Spawned(long e) => spawnsByEntry.GetValueOrDefault(e) + (templates.ContainsKey(e) ? 0 : _in.Facts?.StockSpawns(e) ?? 1);
        var summoned = Rows("creature_ai_scripts").Where(r => I(r, "command") == 10).Select(r => L(r, "datalong")).ToHashSet();
        var starters = Rows("creature_questrelation").ToLookup(r => L(r, "quest"), r => L(r, "id"));
        var enders = Rows("creature_involvedrelation").ToLookup(r => L(r, "quest"), r => L(r, "id"));
        var goStarters = Rows("gameobject_questrelation").ToLookup(r => L(r, "quest"), r => L(r, "id"));
        var goEnders = Rows("gameobject_involvedrelation").ToLookup(r => L(r, "quest"), r => L(r, "id"));
        var exploreTriggers = Rows("areatrigger_involvedrelation").ToLookup(r => L(r, "quest"), r => L(r, "id"));
        var giverOf = Rows("creature_questrelation").Concat(Rows("creature_involvedrelation")).Select(r => L(r, "id")).ToHashSet();
        var vendorOf = Rows("npc_vendor").ToLookup(r => L(r, "entry"));
        var trainerOf = Rows("npc_trainer").ToLookup(r => L(r, "entry"));
        var menuOptions = Rows("gossip_menu_option").ToLookup(r => L(r, "menu_id"));
        var menus = Rows("gossip_menu").ToLookup(r => L(r, "entry"));
        var loot = Rows("creature_loot_template").ToLookup(r => L(r, "entry"));
        int services = 0;

        // C0 — spawn guids inside the pack range: above it the server's runtime guid counter overflows
        // its 24 bits on the first runtime spawn and mangosd shuts down.
        foreach (var t in new[] { "creature", "gameobject" })
            foreach (var r in Rows(t))
                if (L(r, "guid") >= WorldPackContent.SpawnGuidCeiling || L(r, "guid") < WorldPackContent.SpawnGuidBase)
                    Add("C0", "error", $"{t} {L(r, "guid")}", $"guid outside the pack spawn range [{WorldPackContent.SpawnGuidBase}, {WorldPackContent.SpawnGuidCeiling}): mangosd shuts down on the first runtime spawn (24-bit guid overflow)", I(r, "map"));

        // C1 — spawns reference templates.
        foreach (var r in Rows("creature"))
            if (!templates.ContainsKey(L(r, "id")) && !(_in.Facts?.Has("creature_template", L(r, "id")) ?? true))
                Add("C1", "error", $"creature {L(r, "guid")}", $"spawns entry {L(r, "id")}, which has no creature_template", I(r, "map"));

        foreach (var (entry, t) in templates)
        {
            string who = $"{S(t, "name")} ({entry})";
            uint flags = (uint)L(t, "npc_flags");
            // C2 — every pack creature exists in the world somewhere.
            if (!spawnsByEntry.ContainsKey(entry) && !summoned.Contains(entry))
                Add("C2", "warn", who, "template is never spawned or summoned");
            // C3 — services the flags promise must be reachable.
            if ((flags & Vendor) != 0 && !vendorOf[entry].Any()) Add("C3", "error", who, "vendor flag but no npc_vendor items");
            if ((flags & Vendor) == 0 && vendorOf[entry].Any()) Add("C3", "error", who, "sells items but has no vendor flag (players cannot open the shop)");
            if ((flags & Trainer) != 0 && !trainerOf[entry].Any()) Add("C3", "error", who, "trainer flag but no npc_trainer spells");
            if ((flags & Trainer) == 0 && trainerOf[entry].Any()) Add("C3", "error", who, "has trainer spells but no trainer flag");
            if ((flags & QuestGiver) != 0 && !giverOf.Contains(entry)) Add("C3", "warn", who, "quest-giver flag but starts/ends no quest");
            if ((flags & QuestGiver) == 0 && giverOf.Contains(entry)) Add("C3", "error", who, "starts/ends quests but has no quest-giver flag");
            long menu = L(t, "gossip_menu_id");
            if (menu != 0)
            {
                if (!menus[menu].Any() && !(_in.Facts?.Has("gossip_menu", menu) ?? true))
                    Add("C4", "error", who, $"gossip_menu_id {menu} has no gossip_menu row");
                // C4 — with its own menu, ONLY that menu's options show: each service needs its option row.
                foreach (var (flag, what) in Services)
                {
                    if ((flags & flag) == 0) continue;
                    services++;
                    bool has = menuOptions[menu].Any(o => (L(o, "npc_option_npcflag") & flag) != 0) || (_in.Facts?.HasGossipOption(menu, flag) ?? false);
                    if (!has) Add("C4", "error", who, $"{what} flag, but gossip menu {menu} has no {what} option — players cannot reach the {what}");
                }
            }
            // C5 — equipment, display, loot.
            long equip = L(t, "equipment_id");
            if (equip != 0)
            {
                var eq = Rows("creature_equip_template").FirstOrDefault(r => L(r, "entry") == equip);
                if (eq == null && !(_in.Facts?.Has("creature_equip_template", equip) ?? true)) Add("C5", "error", who, $"equipment_id {equip} has no creature_equip_template row");
                else if (eq != null)
                    foreach (var c in new[] { "item1", "item2", "item3" })
                        if (L(eq, c) != 0 && !Exists("item_template", L(eq, c))) Add("C5", "error", who, $"equipment {c} = {L(eq, c)} is not an item");
            }
            for (int i = 1; i <= 4; i++)
            {
                long disp = L(t, "display_id" + i);
                if (disp != 0 && !Exists("creature_display_info_addon", disp, "display_id")) Add("C5", "error", who, $"display_id{i} {disp} does not exist");
            }
            long lootId = L(t, "loot_id");
            if (lootId != 0 && !loot[lootId].Any() && !(_in.Facts?.Has("creature_loot_template", lootId) ?? true))
                Add("C5", "warn", who, $"loot_id {lootId} has no loot rows (drops nothing)");
            if (S(t, "ai_name") != "EventAI" && Rows("creature_ai_events").Any(e => L(e, "creature_id") == entry))
                Add("C10", "error", who, "has EventAI events but ai_name is not EventAI (they never run)");
        }
        Add("C4", "info", "services", $"{services} service flag(s) on NPCs with their own gossip menu checked for reachable options");

        // C6 — shop and trainer lists point at real things.
        foreach (var r in Rows("npc_vendor"))
            if (!Exists("item_template", L(r, "item"))) Add("C6", "error", $"vendor {L(r, "entry")}", $"sells item {L(r, "item")}, which does not exist");
        foreach (var r in Rows("npc_trainer"))
            if (!Exists("spell_template", L(r, "spell"))) Add("C6", "error", $"trainer {L(r, "entry")}", $"teaches spell {L(r, "spell")}, which does not exist");
        foreach (var r in Rows("creature_loot_template"))
            if (L(r, "mincountOrRef") >= 0 && !Exists("item_template", L(r, "item"))) Add("C6", "error", $"loot {L(r, "entry")}", $"drops item {L(r, "item")}, which does not exist");

        // C7 — texts behind gossip.
        foreach (var r in Rows("gossip_menu"))
        {
            long text = L(r, "text_id");
            var nt = Rows("npc_text").FirstOrDefault(x => L(x, "ID") == text);
            if (nt == null && !(_in.Facts?.Has("npc_text", text) ?? true)) Add("C7", "error", $"gossip menu {L(r, "entry")}", $"text {text} has no npc_text row (empty window)");
            else if (nt != null && !Exists("broadcast_text", L(nt, "BroadcastTextID0"))) Add("C7", "error", $"npc_text {text}", $"broadcast text {L(nt, "BroadcastTextID0")} missing");
        }

        // C8 — quests: givers, objectives obtainable, rewards real, chain intact.
        var lootByTemplate = templates.Where(kv => L(kv.Value, "loot_id") != 0)
            .SelectMany(kv => loot[L(kv.Value, "loot_id")].Select(l => (creature: kv.Key, item: L(l, "item"))))
            .ToLookup(x => x.item, x => x.creature);
        var sold = Rows("npc_vendor").Select(r => L(r, "item")).ToHashSet();
        foreach (var q in Rows("quest_template"))
        {
            long id = L(q, "entry");
            string who = $"quest {id} \"{S(q, "Title")}\"";
            if (!starters[id].Any() && !goStarters[id].Any()) Add("C8", "error", who, "nothing starts it (no creature or gameobject giver)");
            if (!enders[id].Any() && !goEnders[id].Any()) Add("C8", "error", who, "nothing ends it (cannot be turned in)");
            // Exploration: the flag and the trigger must come together, and the client must know the trigger.
            bool exploreFlag = (L(q, "SpecialFlags") & 2) != 0;
            if (exploreFlag && !exploreTriggers[id].Any()) Add("C8", "error", who, "exploration flag set but no areatrigger_involvedrelation (can never complete)");
            foreach (var trig in exploreTriggers[id])
            {
                if (!exploreFlag) Add("C8", "error", who, $"trigger {trig} completes it but SpecialFlags lacks the exploration bit (2)");
                if (!(_dbc.GetValueOrDefault("AreaTrigger")?.ContainsKey(trig.ToString()) ?? false))
                    Add("C8", "error", who, $"exploration trigger {trig} has no AreaTrigger.dbc row: the client never reports entering it");
                if (!Rows("areatrigger_template").Any(t => L(t, "id") == trig))
                    Add("C8", "error", who, $"exploration trigger {trig} has no areatrigger_template row: the server does not know it");
            }
            foreach (var g in starters[id].Concat(enders[id]).Distinct())
                if (Spawned(g) == 0) Add("C8", "error", who, $"giver/ender {Name("creature", g)} is not spawned anywhere");
            if (L(q, "MinLevel") > L(q, "QuestLevel") && L(q, "QuestLevel") > 0) Add("C8", "warn", who, "MinLevel above QuestLevel");
            if (string.IsNullOrWhiteSpace(S(q, "Details")) || string.IsNullOrWhiteSpace(S(q, "Objectives"))) Add("C8", "warn", who, "empty Details/Objectives text");
            for (int i = 1; i <= 4; i++)
            {
                long target = L(q, "ReqCreatureOrGOId" + i);
                if (target > 0 && Spawned(target) == 0 && !summoned.Contains(target))
                    Add("C8", "error", who, $"kill objective {Name("creature", target)} is not spawned anywhere");
                if (target < 0 && !Rows("gameobject").Any(g => L(g, "id") == -target) && !(_in.Facts?.Has("gameobject_template", -target) ?? true))
                    Add("C8", "error", who, $"use-object objective {-target} is neither a pack gameobject nor a stock one");
                long item = L(q, "ReqItemId" + i);
                if (item == 0) continue;
                if (!Exists("item_template", item)) { Add("C8", "error", who, $"objective item {item} does not exist"); continue; }
                bool obtainable = lootByTemplate[item].Any(c => Spawned(c) > 0) || sold.Contains(item) || L(q, "SrcItemId") == item;
                if (!obtainable)
                    Add("C8", item < WorldPackContent.TemplateBase ? "warn" : "error", who,
                        $"objective item {item} is not dropped by any spawned pack creature nor sold by a pack vendor" +
                        (item < WorldPackContent.TemplateBase ? " (stock item: obtainable elsewhere?)" : ""));
                else if (lootByTemplate[item].Any())
                {
                    var chance = loot[L(templates[lootByTemplate[item].First()], "loot_id")].First(l => L(l, "item") == item);
                    if (F(chance, "ChanceOrQuestChance") >= 0) Add("C8", "warn", who, $"objective item {item} drops with a normal chance (not quest-only: drops for everyone, forever)");
                }
            }
            foreach (var c in Enumerable.Range(1, 4).Select(i => "RewItemId" + i).Concat(Enumerable.Range(1, 6).Select(i => "RewChoiceItemId" + i)))
                if (L(q, c) != 0 && !Exists("item_template", L(q, c))) Add("C8", "error", who, $"{c} {L(q, c)} does not exist");
            foreach (var c in new[] { "PrevQuestId", "NextQuestInChain" })
                if (L(q, c) != 0 && !Exists("quest_template", Math.Abs(L(q, c)))) Add("C8", "error", who, $"{c} {L(q, c)} does not exist");
        }

        // C9 — EventAI actions.
        var scripts = Rows("creature_ai_scripts").ToLookup(r => L(r, "id"));
        foreach (var e in Rows("creature_ai_events"))
        {
            string who = $"EventAI {L(e, "id")} ({Name("creature", L(e, "creature_id"))})";
            if (I(e, "event_type") == 2 && (L(e, "event_flags") & 1) != 0) Add("C9", "warn", who, "HP-window event marked repeatable (server rejects the flag)");
            for (int a = 1; a <= 3; a++)
            {
                long s = L(e, $"action{a}_script");
                if (s == 0) continue;
                if (!scripts[s].Any()) { Add("C9", "error", who, $"action{a}_script {s} has no creature_ai_scripts rows"); continue; }
                foreach (var sc in scripts[s])
                {
                    if (I(sc, "command") == 15 && !Exists("spell_template", L(sc, "datalong"))) Add("C9", "error", who, $"casts spell {L(sc, "datalong")}, which does not exist");
                    if (I(sc, "command") == 0 && L(sc, "dataint") != 0 && !Exists("broadcast_text", L(sc, "dataint"))) Add("C9", "error", who, $"says text {L(sc, "dataint")}, which does not exist");
                }
            }
        }

        // C10 — maps, instances, areas, graveyards, triggers, lights.
        var teleports = Rows("areatrigger_teleport");
        foreach (var (_, m) in Docs("map"))
        {
            int id = (int)m["mapId"]!;
            string who = $"map {id} {(string?)m["name"]}";
            var tpl = Rows("map_template").FirstOrDefault(r => L(r, "entry") == id);
            if (tpl == null) { Add("C10", "error", who, "no map_template row (server will not load it)"); continue; }
            int type = I(tpl, "map_type");
            int ghost = I(tpl, "ghost_entrance_map");
            // ObjectMgr::LoadMapTemplate ERASES an instance whose ghost entrance is not on continent 0/1.
            if (type != 0 && ghost != -1 && ghost != 0 && ghost != 1) Add("C10", "error", who, $"ghost entrance on map {ghost}: the server deletes this map at startup (only continents 0/1 are allowed; use -1)");
            if (!teleports.Any(r => I(r, "target_map") == id)) Add("C10", "error", who, "no portal leads into it");
            var outOf = _dbc.GetValueOrDefault("AreaTrigger")?.Where(kv => DbcField(kv.Value, 1) == id).Select(kv => long.Parse(kv.Key)).ToHashSet() ?? new();
            if (!teleports.Any(r => outOf.Contains(L(r, "id")) && I(r, "target_map") != id)) Add("C10", "error", who, "no portal leads out of it");
            if (!(_dbc.GetValueOrDefault("Light")?.Values.Any(l => DbcField(l, 1) == id) ?? false)) Add("C10", "warn", who, "no Light.dbc row: the map renders with the global default light");
        }
        var graveyardZones = Rows("game_graveyard_zone").ToLookup(r => L(r, "ghost_zone"), r => L(r, "id"));
        foreach (var (key, a) in _dbc.GetValueOrDefault("AreaTable") ?? new())
        {
            long id = long.Parse(key);
            string who = $"area {id}";
            var areaRow = Rows("area_template").FirstOrDefault(r => L(r, "entry") == id);
            if (areaRow == null) Add("C10", "warn", who, "AreaTable row without area_template (server shows no name / wrong zone)");
            // Faction (FactionGroupMask, field 20: 0 contested, 2 Alliance, 4 Horde) is CHOSEN per area, and the
            // client's and the server's agree. Gilneas cloned Silverpine (130) and inherited its Horde 4: the minimap
            // painted Duskhaven red for Alliance players while area_template.team said 0 (2026-09-27, live).
            bool factionSet = a["fields"] is JsonObject af && af.ContainsKey("20");
            if (!factionSet && a["cloneFrom"] is JsonValue cloneSource)
                Add("C10", "warn", who, $"faction (AreaTable field 20) is inherited from clone source {WorldPackContent.Scalar(cloneSource)} - " +
                    "set it (0 contested, 2 Alliance, 4 Horde): the minimap paints the zone by it");
            else if (areaRow != null && DbcField(a, 20) != L(areaRow, "team"))
                Add("C10", "error", who, $"client faction {DbcField(a, 20)} (AreaTable field 20) differs from the server's area_template.team " +
                    $"{L(areaRow, "team")}: the minimap and the server's territory rules disagree");
            long parent = DbcField(a, 2);
            if (parent == 0)
            {
                if (!graveyardZones[id].Any()) Add("C10", "error", who, "zone has no graveyard (dead players have nowhere to release)");
                foreach (var gy in graveyardZones[id])
                    if (!(_dbc.GetValueOrDefault("WorldSafeLocs")?.ContainsKey(gy.ToString()) ?? false) && gy >= WorldPackContent.AreaIdBase)
                        Add("C10", "error", who, $"graveyard {gy} has no WorldSafeLocs row");
            }
        }
        foreach (var r in teleports)
        {
            long id = L(r, "id");
            if (!(_dbc.GetValueOrDefault("AreaTrigger")?.ContainsKey(id.ToString()) ?? false))
                Add("C10", "error", $"portal {id}", "no AreaTrigger.dbc row: the client never reports stepping into it");
            if (!Rows("areatrigger_template").Any(t => L(t, "id") == id))
                Add("C10", "error", $"portal {id}", "no areatrigger_template row: the server does not know the trigger");
        }

        // C11 — linked packs (creature_groups) and patrols (movement_type 2 + creature_movement).
        var spawnRows = Rows("creature").GroupBy(r => L(r, "guid")).ToDictionary(x => x.Key, x => x.First());
        var groupRows = Rows("creature_groups");
        foreach (var g in groupRows)
        {
            long leader = L(g, "leader_guid"), member = L(g, "member_guid");
            string who = $"group {leader}: member {member}";
            if (!spawnRows.TryGetValue(leader, out var lead)) { Add("C11", "error", who, $"leader {leader} is not a pack spawn"); continue; }
            if (!spawnRows.TryGetValue(member, out var mem)) { Add("C11", "error", who, $"member {member} is not a pack spawn"); continue; }
            if (I(lead, "map") != I(mem, "map")) Add("C11", "error", who, "leader and member are on different maps");
            if ((L(g, "flags") & ~0xFFL) != 0) Add("C11", "error", who, $"flags 0x{L(g, "flags"):X} outside CreatureGroups OPTION_* (0xFF)");
            float apart = Vector2.Distance(new(F(lead, "position_x"), F(lead, "position_y")), new(F(mem, "position_x"), F(mem, "position_y")));
            if (member != leader && apart > 40f)
                Add("C11", "warn", who, $"{apart:F0} yd from its leader - a linked member that far pulls another room with it");
            if ((L(g, "flags") & 1) != 0 && member != leader && I(mem, "movement_type") != 0)
                Add("C11", "warn", who, "follows its leader in formation but also has its own movement (wander/waypoints) - it fights the formation");
        }
        foreach (var leader in groupRows.Select(g => L(g, "leader_guid")).Distinct())
            if (!groupRows.Any(g => L(g, "leader_guid") == leader && L(g, "member_guid") == leader))
                Add("C11", "warn", $"group {leader}", "the leader has no row of its own (stock groups list the leader as a member; aggro/respawn options follow the rows)");
        var waypoints = Rows("creature_movement").GroupBy(r => L(r, "id")).ToDictionary(x => x.Key, x => x.OrderBy(r => L(r, "point")).ToList());
        foreach (var (guid, pts) in waypoints)
        {
            string who = $"patrol {guid}";
            if (!spawnRows.TryGetValue(guid, out var c)) { Add("C11", "error", who, "waypoints for a guid that is not a pack spawn"); continue; }
            who += $" ({Name("creature", L(c, "id"))})";
            if (I(c, "movement_type") != 2) { Add("C11", "warn", who, $"has {pts.Count} waypoint(s) but movement_type {I(c, "movement_type")} - they are ignored (2 = waypoints)"); continue; }
            if (pts.Count < 2) { Add("C11", "error", who, $"{pts.Count} waypoint - a patrol needs at least 2"); continue; }
            var spawn = new Vector2(F(c, "position_x"), F(c, "position_y"));
            float first = Vector2.Distance(spawn, new(F(pts[0], "position_x"), F(pts[0], "position_y")));
            if (first > 15f) Add("C11", "warn", who, $"spawns {first:F0} yd from its first waypoint - it walks there first (and back after every evade)");
            for (int i = 0; i < pts.Count; i++)
            {
                var a = new Vector2(F(pts[i], "position_x"), F(pts[i], "position_y"));
                var b = new Vector2(F(pts[(i + 1) % pts.Count], "position_x"), F(pts[(i + 1) % pts.Count], "position_y"));
                if (Vector2.Distance(a, b) > 80f)
                    Add("C11", "warn", who, $"leg {i + 1}->{(i + 1) % pts.Count + 1} is {Vector2.Distance(a, b):F0} yd - long legs cut corners through walls; add a point");
            }
        }
        foreach (var c in spawnRows.Values.Where(c => I(c, "movement_type") == 2 && !waypoints.ContainsKey(L(c, "guid"))))
            Add("C11", "error", $"patrol {L(c, "guid")} ({Name("creature", L(c, "id"))})", "movement_type 2 with no waypoints - it stands still");
    }

    private static long DbcField(JsonObject body, int index) =>
        body["fields"] is JsonObject f && f[index.ToString()] is JsonValue v && long.TryParse(WorldPackContent.Scalar(v), out var x) ? x : 0;
}
