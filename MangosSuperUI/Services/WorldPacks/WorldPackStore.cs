using System.Text.Json;
using Dapper;
using MangosSuperUI.Models;
using MySqlConnector;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// World Content Packs — the audited, undoable edit store behind MSUIClient's World Builder
/// (MSUIClient shared_docs/WORLD_BUILDER.md §2-§3). Every write is an op row in <c>wp_op</c>
/// plus an <c>audit_log</c> row (category <c>worldpack</c>); the live tables are the fold of
/// the ops, and Undo appends the inverse op instead of deleting history.
/// </summary>
public sealed class WorldPackStore
{
    public const string AuditCategory = "worldpack";
    public const uint UniqueIdBase = 7_000_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly ILogger<WorldPackStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _schemaReady;

    public WorldPackStore(ConnectionFactory db, AuditService audit, ILogger<WorldPackStore> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════ schema

    public async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        using var conn = _db.Admin();
        await conn.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS wp_pack (
  id INT AUTO_INCREMENT PRIMARY KEY,
  pack_key VARCHAR(64) NOT NULL UNIQUE,
  name VARCHAR(128) NOT NULL,
  description TEXT NULL,
  enabled TINYINT NOT NULL DEFAULT 0,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS wp_op (
  id BIGINT AUTO_INCREMENT PRIMARY KEY,
  pack_id INT NOT NULL,
  kind VARCHAR(24) NOT NULL,
  label VARCHAR(160) NOT NULL DEFAULT '',
  payload LONGTEXT NOT NULL,
  operator VARCHAR(64) NOT NULL DEFAULT '',
  operator_ip VARCHAR(64) NULL,
  created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  undone_by BIGINT NULL,
  undoes BIGINT NULL,
  audit_id BIGINT NULL,
  INDEX idx_pack (pack_id, id));
CREATE TABLE IF NOT EXISTS wp_sculpt (
  pack_id INT NOT NULL,
  map_id INT NOT NULL,
  tile_col INT NOT NULL,
  tile_row INT NOT NULL,
  vertex_index INT NOT NULL,
  delta FLOAT NOT NULL,
  PRIMARY KEY (pack_id, map_id, tile_col, tile_row, vertex_index));
CREATE TABLE IF NOT EXISTS wp_surface_sculpt (
  pack_id INT NOT NULL,
  map_id INT NOT NULL,
  tile_col INT NOT NULL,
  tile_row INT NOT NULL,
  vertex_index INT NOT NULL,
  delta FLOAT NOT NULL,
  PRIMARY KEY (pack_id, map_id, tile_col, tile_row, vertex_index));
CREATE TABLE IF NOT EXISTS wp_placement (
  id INT AUTO_INCREMENT PRIMARY KEY,
  pack_id INT NOT NULL,
  map_id INT NOT NULL,
  kind VARCHAR(8) NOT NULL,
  model_path VARCHAR(260) NOT NULL,
  pos_x FLOAT NOT NULL, pos_y FLOAT NOT NULL, pos_z FLOAT NOT NULL,
  rot_x FLOAT NOT NULL DEFAULT 0, rot_y FLOAT NOT NULL DEFAULT 0, rot_z FLOAT NOT NULL DEFAULT 0,
  scale FLOAT NOT NULL DEFAULT 1,
  doodad_set INT NOT NULL DEFAULT 0,
  deleted TINYINT NOT NULL DEFAULT 0,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  INDEX idx_pack_map (pack_id, map_id));
CREATE TABLE IF NOT EXISTS wp_build (
  id INT AUTO_INCREMENT PRIMARY KEY,
  status VARCHAR(16) NOT NULL,
  operator VARCHAR(64) NOT NULL DEFAULT '',
  started_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  finished_at DATETIME NULL,
  packs VARCHAR(512) NOT NULL DEFAULT '',
  mpq_sha1 VARCHAR(40) NULL,
  mpq_size BIGINT NULL,
  log MEDIUMTEXT NULL);
CREATE TABLE IF NOT EXISTS wp_published_sculpt (
  map_id INT NOT NULL, tile_col INT NOT NULL, tile_row INT NOT NULL, vertex_index INT NOT NULL,
  delta FLOAT NOT NULL,
  PRIMARY KEY (map_id, tile_col, tile_row, vertex_index));
CREATE TABLE IF NOT EXISTS wp_published_placement (
  placement_id INT NOT NULL PRIMARY KEY,
  build_id INT NOT NULL);
CREATE TABLE IF NOT EXISTS wp_doc (
  pack_id INT NOT NULL,
  kind VARCHAR(64) NOT NULL,
  doc_key VARCHAR(190) NOT NULL,
  body LONGTEXT NOT NULL,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (pack_id, kind, doc_key));
CREATE TABLE IF NOT EXISTS wp_installed_row (
  tbl VARCHAR(64) NOT NULL,
  row_key VARCHAR(190) NOT NULL,
  pack_id INT NOT NULL,
  PRIMARY KEY (tbl, row_key));
CREATE TABLE IF NOT EXISTS wp_npc_baseline (
  spawn_guid INT UNSIGNED PRIMARY KEY,
  original_entry INT UNSIGNED NOT NULL,
  installed_entry INT UNSIGNED NULL,
  pending_entry INT UNSIGNED NULL,
  pack_id INT NOT NULL);");
        _schemaReady = true;
    }

    // ═══════════════════════════════════════════════════════════════ reads

    public async Task<List<PackRow>> ListPacksAsync()
    {
        await EnsureSchemaAsync();
        using var conn = _db.Admin();
        return (await conn.QueryAsync<PackRow>(@"
SELECT p.id AS Id, p.pack_key AS PackKey, p.name AS Name, p.description AS Description, p.enabled AS Enabled,
       (SELECT COUNT(*) FROM wp_placement x WHERE x.pack_id = p.id AND x.deleted = 0) AS Placements,
       ((SELECT COUNT(*) FROM wp_sculpt s WHERE s.pack_id = p.id) +
        (SELECT COUNT(*) FROM wp_surface_sculpt s WHERE s.pack_id = p.id)) AS SculptVertices,
       (SELECT COUNT(*) FROM wp_op o WHERE o.pack_id = p.id AND o.kind <> 'undo' AND o.undone_by IS NULL) AS UndoableOps
FROM wp_pack p ORDER BY p.id")).ToList();
    }

    public async Task<PackRow?> GetPackAsync(int packId) =>
        (await ListPacksAsync()).FirstOrDefault(p => p.Id == packId);

    public async Task<List<PlacementRow>> PlacementsAsync(int? packId, int? mapId, bool includeDeleted = false)
    {
        await EnsureSchemaAsync();
        using var conn = _db.Admin();
        return (await conn.QueryAsync<PlacementRow>(@"
SELECT p.id AS Id, p.pack_id AS PackId, p.map_id AS MapId, p.kind AS Kind, p.model_path AS ModelPath,
       p.pos_x AS PosX, p.pos_y AS PosY, p.pos_z AS PosZ, p.rot_x AS RotX, p.rot_y AS RotY, p.rot_z AS RotZ,
       p.scale AS Scale, p.doodad_set AS DoodadSet, p.deleted AS Deleted,
       (pp.placement_id IS NOT NULL) AS Published
FROM wp_placement p LEFT JOIN wp_published_placement pp ON pp.placement_id = p.id
WHERE (@packId IS NULL OR p.pack_id = @packId) AND (@mapId IS NULL OR p.map_id = @mapId)
  AND (@includeDeleted OR p.deleted = 0)
ORDER BY p.id", new { packId, mapId, includeDeleted })).ToList();
    }

    /// <summary>Sculpt deltas of one pack (or all enabled packs summed when packId is null) on a map.</summary>
    public async Task<List<SculptTile>> SculptAsync(int? packId, int mapId, bool enabledOnly, int includePackId = 0, bool? surface = null)
    {
        await EnsureSchemaAsync();
        using var conn = _db.Admin();
        // Aggregate preview totals retain the published subtraction contract; builds request each layer.
        string table = surface is { } layer ? WorldPackSculptLayers.Table(layer)
            : "(SELECT * FROM wp_sculpt UNION ALL SELECT * FROM wp_surface_sculpt)";
        var rows = await conn.QueryAsync<(int col, int row, int idx, double delta)>($@"
SELECT s.tile_col, s.tile_row, s.vertex_index, SUM(s.delta)
FROM {table} s JOIN wp_pack p ON p.id = s.pack_id
WHERE s.map_id = @mapId AND (@packId IS NULL OR s.pack_id = @packId) AND (NOT @enabledOnly OR p.enabled = 1 OR p.id = @includePackId)
GROUP BY s.tile_col, s.tile_row, s.vertex_index", new { mapId, packId, enabledOnly, includePackId });
        return Group(rows);
    }

    public async Task<List<SculptTile>> PublishedSculptAsync(int mapId)
    {
        await EnsureSchemaAsync();
        using var conn = _db.Admin();
        var rows = await conn.QueryAsync<(int col, int row, int idx, double delta)>(
            "SELECT tile_col, tile_row, vertex_index, delta * 1.0 FROM wp_published_sculpt WHERE map_id = @mapId",
            new { mapId });
        return Group(rows);
    }

    private static List<SculptTile> Group(IEnumerable<(int col, int row, int idx, double delta)> rows) =>
        rows.GroupBy(r => (r.col, r.row))
            .Select(g => new SculptTile
            {
                Col = g.Key.col,
                Row = g.Key.row,
                Deltas = g.Where(r => Math.Abs(r.delta) > 1e-4).ToDictionary(r => r.idx, r => (float)r.delta),
            })
            .Where(t => t.Deltas.Count > 0)
            .ToList();

    public async Task<List<OpRow>> OpsAsync(int packId, int limit)
    {
        await EnsureSchemaAsync();
        using var conn = _db.Admin();
        return (await conn.QueryAsync<OpRow>(@"
SELECT id AS Id, pack_id AS PackId, kind AS Kind, label AS Label, operator AS Operator, created_at AS CreatedAt,
       undone_by AS UndoneBy, undoes AS Undoes, audit_id AS AuditId
FROM wp_op WHERE pack_id = @packId ORDER BY id DESC LIMIT @limit", new { packId, limit })).ToList();
    }

    // ═══════════════════════════════════════════════════════════════ writes

    public async Task<PackRow> CreatePackAsync(string key, string name, string? description, string op, string? ip)
    {
        await EnsureSchemaAsync();
        key = key.Trim().ToLowerInvariant();
        if (key.Length == 0 || key.Length > 64 || !key.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'))
            throw new ArgumentException("pack key must be 1-64 chars of a-z, 0-9, '-' or '_'");
        using var conn = _db.Admin();
        int id = await conn.ExecuteScalarAsync<int>(
            "INSERT INTO wp_pack (pack_key, name, description) VALUES (@key, @name, @description); SELECT LAST_INSERT_ID();",
            new { key, name = string.IsNullOrWhiteSpace(name) ? key : name.Trim(), description });
        await _audit.LogAsync(new AuditEntry
        {
            Operator = op, OperatorIp = ip, Category = AuditCategory, Action = "pack_create",
            TargetType = "wp_pack", TargetName = key, TargetId = id,
            StateAfter = JsonSerializer.Serialize(new { id, key, name, description }, Json),
        });
        return (await GetPackAsync(id))!;
    }

    public async Task SetEnabledAsync(int packId, bool enabled, string op, string? ip)
    {
        await EnsureSchemaAsync();
        var pack = await GetPackAsync(packId) ?? throw new KeyNotFoundException($"pack {packId}");
        using var conn = _db.Admin();
        await conn.ExecuteAsync("UPDATE wp_pack SET enabled = @enabled WHERE id = @packId", new { packId, enabled });
        await _audit.LogAsync(new AuditEntry
        {
            Operator = op, OperatorIp = ip, Category = AuditCategory, Action = enabled ? "pack_enable" : "pack_disable",
            TargetType = "wp_pack", TargetName = pack.PackKey, TargetId = packId,
            StateBefore = JsonSerializer.Serialize(new { enabled = pack.Enabled }, Json),
            StateAfter = JsonSerializer.Serialize(new { enabled }, Json),
            IsReversible = true,
            Notes = "Takes effect on the next World Pack publish.",
        });
    }

    public async Task<OpResult> SculptAsync(int packId, SculptRequest req, string op, string? ip)
    {
        await EnsureSchemaAsync();
        var tiles = req.Tiles
            .Select(t => new SculptTile
            {
                Col = t.Col, Row = t.Row,
                Deltas = t.Deltas.Where(kv => kv.Key is >= 0 and < 129 * 129 && float.IsFinite(kv.Value) && kv.Value != 0f)
                                 .ToDictionary(kv => kv.Key, kv => kv.Value),
            })
            .Where(t => t.Deltas.Count > 0 && t.Col is >= 0 and < 64 && t.Row is >= 0 and < 64)
            .ToList();
        if (tiles.Count == 0) throw new ArgumentException("sculpt stroke has no vertices");
        if (req.Surface) tiles = WorldPackSculptLayers.SurfaceStroke(tiles);
        var payload = new SculptPayload { MapId = req.MapId, Surface = req.Surface, Tiles = tiles };

        await _writeLock.WaitAsync();
        try
        {
            using var conn = _db.Admin();
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();
            await AddSculptAsync(conn, tx, packId, payload, +1f);
            long opId = await InsertOpAsync(conn, tx, packId, "sculpt", req.Label ?? $"sculpt {tiles.Sum(t => t.Deltas.Count)} vertices", payload, op, ip, null);
            await tx.CommitAsync();
            long auditId = await AuditOpAsync(conn, opId, packId, "sculpt", op, ip, null, payload, null);
            return new OpResult { OpId = opId, AuditId = auditId };
        }
        finally { _writeLock.Release(); }
    }

    public async Task<OpResult> PlaceAsync(int packId, PlacementInput p, string op, string? ip)
    {
        await EnsureSchemaAsync();
        Validate(p);
        await _writeLock.WaitAsync();
        try
        {
            using var conn = _db.Admin();
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();
            int id = await conn.ExecuteScalarAsync<int>(@"
INSERT INTO wp_placement (pack_id, map_id, kind, model_path, pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, scale, doodad_set)
VALUES (@packId, @MapId, @Kind, @ModelPath, @PosX, @PosY, @PosZ, @RotX, @RotY, @RotZ, @Scale, @DoodadSet);
SELECT LAST_INSERT_ID();", new { packId, p.MapId, p.Kind, p.ModelPath, p.PosX, p.PosY, p.PosZ, p.RotX, p.RotY, p.RotZ, p.Scale, p.DoodadSet }, tx);
            var after = await ReadPlacementAsync(conn, tx, id);
            long opId = await InsertOpAsync(conn, tx, packId, "place", $"place {Path.GetFileName(p.ModelPath)}",
                new PlacementPayload { After = after }, op, ip, null);
            await tx.CommitAsync();
            long auditId = await AuditOpAsync(conn, opId, packId, "place", op, ip, null, after, id);
            return new OpResult { OpId = opId, AuditId = auditId, Placement = after };
        }
        finally { _writeLock.Release(); }
    }

    public async Task<OpResult> MoveAsync(int placementId, PlacementInput p, string op, string? ip)
    {
        await EnsureSchemaAsync();
        Validate(p);
        await _writeLock.WaitAsync();
        try
        {
            using var conn = _db.Admin();
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();
            var before = await ReadPlacementAsync(conn, tx, placementId) ?? throw new KeyNotFoundException($"placement {placementId}");
            if (before.Deleted) throw new InvalidOperationException("placement is deleted");
            await WritePlacementAsync(conn, tx, placementId, p.PosX, p.PosY, p.PosZ, p.RotX, p.RotY, p.RotZ, p.Scale, p.DoodadSet, false);
            var after = await ReadPlacementAsync(conn, tx, placementId);
            long opId = await InsertOpAsync(conn, tx, before.PackId, "move", $"move {Path.GetFileName(before.ModelPath)}",
                new PlacementPayload { Before = before, After = after }, op, ip, null);
            await tx.CommitAsync();
            long auditId = await AuditOpAsync(conn, opId, before.PackId, "move", op, ip, before, after, placementId);
            return new OpResult { OpId = opId, AuditId = auditId, Placement = after };
        }
        finally { _writeLock.Release(); }
    }

    public async Task<OpResult> DeleteAsync(int placementId, string op, string? ip)
    {
        await EnsureSchemaAsync();
        await _writeLock.WaitAsync();
        try
        {
            using var conn = _db.Admin();
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();
            var before = await ReadPlacementAsync(conn, tx, placementId) ?? throw new KeyNotFoundException($"placement {placementId}");
            if (before.Deleted) throw new InvalidOperationException("placement is already deleted");
            await conn.ExecuteAsync("UPDATE wp_placement SET deleted = 1 WHERE id = @placementId", new { placementId }, tx);
            var after = await ReadPlacementAsync(conn, tx, placementId);
            long opId = await InsertOpAsync(conn, tx, before.PackId, "delete", $"delete {Path.GetFileName(before.ModelPath)}",
                new PlacementPayload { Before = before, After = after }, op, ip, null);
            await tx.CommitAsync();
            long auditId = await AuditOpAsync(conn, opId, before.PackId, "delete", op, ip, before, after, placementId);
            return new OpResult { OpId = opId, AuditId = auditId, Placement = after };
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Move a pack's region (WorldPackRelocation): every doc on <c>spec.FromMap</c>, every live placement and the
    /// pack's sculpt, shifted by whole ADT tiles to <c>spec.ToMap</c> - ONE audited, undoable op. Refuses when a
    /// moved tile doc would land on a key another doc of the pack already owns.
    /// </summary>
    public async Task<OpResult> RelocateAsync(int packId, RelocateSpec spec, string op, string? ip)
    {
        await EnsureSchemaAsync();
        if (await GetPackAsync(packId) is null) throw new KeyNotFoundException($"pack {packId}");
        await _writeLock.WaitAsync();
        try
        {
            using var conn = _db.Admin();
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();
            var payload = new RelocatePayload { Spec = spec };

            var docs = (await conn.QueryAsync<DocRow>(@"
SELECT pack_id AS PackId, kind AS Kind, doc_key AS DocKey, body AS Body FROM wp_doc WHERE pack_id = @packId FOR UPDATE",
                new { packId }, tx)).ToList();
            var otherDocs = (await conn.QueryAsync<DocRow>(@"
SELECT pack_id AS PackId, kind AS Kind, doc_key AS DocKey, body AS Body FROM wp_doc WHERE pack_id <> @packId FOR UPDATE",
                new { packId }, tx)).ToList();
            WorldPackRelocation.ValidateDestination(packId, docs, otherDocs, spec);
            var bodies = docs.ToDictionary(d => (d.Kind, d.DocKey), d => (string?)d.Body);
            var changes = docs.SelectMany(d => WorldPackRelocation.Transform(d.Kind, d.DocKey,
                    System.Text.Json.Nodes.JsonNode.Parse(d.Body) as System.Text.Json.Nodes.JsonObject, spec)
                .Select(c => (Source: d.DocKey, Change: c))).ToList();
            var vacated = changes.Where(x => x.Change.Body is null).Select(x => (x.Change.Kind, x.Change.Key)).ToHashSet();
            // A MOVED doc (new key) must not land on a key another doc still owns.
            foreach (var (source, c) in changes)
                if (c.Body is not null && c.Key != source && bodies.ContainsKey((c.Kind, c.Key)) && !vacated.Contains((c.Kind, c.Key)))
                    throw new InvalidOperationException($"{c.Kind} {c.Key} already exists - the region would land on content the pack already has");
            // Deletes first (a tile may move onto a key another moved tile vacates), then writes.
            foreach (var c in changes.Select(x => x.Change).OrderBy(c => c.Body is null ? 0 : 1))
            {
                string? before = bodies.GetValueOrDefault((c.Kind, c.Key));
                await WriteDocAsync(conn, tx, packId, c.Kind, c.Key, c.Body);
                payload.Docs.Items.Add(new DocPayload { Kind = c.Kind, Key = c.Key, Before = before, After = c.Body });
                bodies[(c.Kind, c.Key)] = c.Body;
            }

            var ids = (await conn.QueryAsync<int>(
                "SELECT id FROM wp_placement WHERE pack_id = @packId AND map_id = @from AND deleted = 0 FOR UPDATE",
                new { packId, from = spec.FromMap }, tx)).ToList();
            foreach (int id in ids)
            {
                var before = await ReadPlacementAsync(conn, tx, id);
                await conn.ExecuteAsync("UPDATE wp_placement SET map_id = @to, pos_x = pos_x + @dx, pos_y = pos_y + @dy WHERE id = @id",
                    new { id, to = spec.ToMap, dx = spec.Dx, dy = spec.Dy }, tx);
                payload.Placements.Add(new PlacementPayload { Before = before, After = await ReadPlacementAsync(conn, tx, id) });
            }

            foreach (bool surface in new[] { false, true })
            {
                string table = WorldPackSculptLayers.Table(surface);
                var rows = await conn.QueryAsync<(int col, int row, int idx, double delta)>($@"
SELECT tile_col, tile_row, vertex_index, delta FROM {table} WHERE pack_id = @packId AND map_id = @from",
                    new { packId, from = spec.FromMap }, tx);
                var from = new SculptPayload { MapId = spec.FromMap, Surface = surface, Tiles = Group(rows) };
                var to = WorldPackSculptLayers.Relocate(from, spec.ToMap, spec.DCol, spec.DRow);
                await AddSculptAsync(conn, tx, packId, from, -1f);
                await AddSculptAsync(conn, tx, packId, to, +1f);
                if (surface) { payload.SurfaceFrom = from; payload.SurfaceTo = to; }
                else { payload.SculptFrom = from; payload.SculptTo = to; }
            }

            string label = $"move region map {spec.FromMap} -> {spec.ToMap} by ({spec.DCol:+0;-0}, {spec.DRow:+0;-0}) tiles: " +
                           $"{payload.Docs.Items.Count} doc change(s), {ids.Count} placement(s), {payload.SculptFrom!.Tiles.Count + payload.SurfaceFrom!.Tiles.Count} sculpted tile layer(s)";
            long opId = await InsertOpAsync(conn, tx, packId, "relocate", label, payload, op, ip, null);
            await tx.CommitAsync();
            long auditId = await AuditOpAsync(conn, opId, packId, "relocate", op, ip, new { spec, from = spec.FromMap }, new { spec, to = spec.ToMap }, null);
            return new OpResult { OpId = opId, AuditId = auditId };
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Revert the newest not-yet-undone op of a pack by appending its inverse.</summary>
    public async Task<OpResult> UndoAsync(int packId, string op, string? ip)
    {
        await EnsureSchemaAsync();
        await _writeLock.WaitAsync();
        try
        {
            using var conn = _db.Admin();
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();
            var target = await conn.QueryFirstOrDefaultAsync<(long id, string kind, string label, string payload, long? auditId)>(@"
SELECT id, kind, label, payload, audit_id FROM wp_op
WHERE pack_id = @packId AND kind <> 'undo' AND undone_by IS NULL ORDER BY id DESC LIMIT 1 FOR UPDATE", new { packId }, tx);
            if (target.id == 0) throw new InvalidOperationException("nothing to undo in this pack");

            object? restored = null;
            PlacementRow? placement = null;
            switch (target.kind)
            {
                case "sculpt":
                {
                    var s = JsonSerializer.Deserialize<SculptPayload>(target.payload, Json)!;
                    await AddSculptAsync(conn, tx, packId, s, -1f);
                    restored = s;
                    break;
                }
                case "place":
                {
                    var p = JsonSerializer.Deserialize<PlacementPayload>(target.payload, Json)!;
                    await conn.ExecuteAsync("UPDATE wp_placement SET deleted = 1 WHERE id = @Id", new { p.After!.Id }, tx);
                    placement = await ReadPlacementAsync(conn, tx, p.After.Id);
                    break;
                }
                case "move":
                case "delete":
                {
                    var p = JsonSerializer.Deserialize<PlacementPayload>(target.payload, Json)!;
                    var b = p.Before!;
                    await WritePlacementAsync(conn, tx, b.Id, b.PosX, b.PosY, b.PosZ, b.RotX, b.RotY, b.RotZ, b.Scale, b.DoodadSet, b.Deleted);
                    placement = await ReadPlacementAsync(conn, tx, b.Id);
                    break;
                }
                case "docs":
                {
                    var d = JsonSerializer.Deserialize<DocBatchPayload>(target.payload, Json)!;
                    for (int i = d.Items.Count - 1; i >= 0; i--)
                        await WriteDocAsync(conn, tx, packId, d.Items[i].Kind, d.Items[i].Key, d.Items[i].Before);
                    break;
                }
                case "relocate":
                {
                    var rp = JsonSerializer.Deserialize<RelocatePayload>(target.payload, Json)!;
                    for (int i = rp.Docs.Items.Count - 1; i >= 0; i--)
                        await WriteDocAsync(conn, tx, packId, rp.Docs.Items[i].Kind, rp.Docs.Items[i].Key, rp.Docs.Items[i].Before);
                    foreach (var pl in rp.Placements)
                        if (pl.Before is { } b)
                            await conn.ExecuteAsync("UPDATE wp_placement SET map_id = @MapId, pos_x = @PosX, pos_y = @PosY WHERE id = @Id", b, tx);
                    if (rp.SculptTo is not null) await AddSculptAsync(conn, tx, packId, rp.SculptTo, -1f);
                    if (rp.SculptFrom is not null) await AddSculptAsync(conn, tx, packId, rp.SculptFrom, +1f);
                    if (rp.SurfaceTo is not null) await AddSculptAsync(conn, tx, packId, rp.SurfaceTo, -1f);
                    if (rp.SurfaceFrom is not null) await AddSculptAsync(conn, tx, packId, rp.SurfaceFrom, +1f);
                    break;
                }
                default:
                    throw new InvalidOperationException($"op kind '{target.kind}' cannot be undone");
            }

            long undoId = await InsertOpAsync(conn, tx, packId, "undo", $"undo: {target.label}",
                new { undoes = target.id, target.kind }, op, ip, target.id);
            await conn.ExecuteAsync("UPDATE wp_op SET undone_by = @undoId WHERE id = @id", new { undoId, target.id }, tx);
            await tx.CommitAsync();

            long auditId = await _audit.LogAsync(new AuditEntry
            {
                Operator = op, OperatorIp = ip, Category = AuditCategory, Action = "undo",
                TargetType = "wp_op", TargetName = target.label, TargetId = (int)target.id,
                StateBefore = target.payload,
                StateAfter = placement != null ? JsonSerializer.Serialize(placement, Json) : null,
                ReversesId = target.auditId,
                Notes = $"World Pack {packId}: undid op #{target.id} ({target.kind})",
            });
            await conn.ExecuteAsync("UPDATE wp_op SET audit_id = @auditId WHERE id = @undoId", new { auditId, undoId });
            return new OpResult { OpId = undoId, AuditId = auditId, UndoneOpId = target.id, UndoneKind = target.kind, Placement = placement, Sculpt = restored as SculptPayload };
        }
        finally { _writeLock.Release(); }
    }

    // ═══════════════════════════════════════════════════════════════ docs (rows, DBC rows, maps, tiles)

    public async Task<List<DocRow>> DocsAsync(int? packId, string? kindPrefix, bool enabledOnly)
    {
        await EnsureSchemaAsync();
        using var conn = _db.Admin();
        return (await conn.QueryAsync<DocRow>(@"
SELECT d.pack_id AS PackId, d.kind AS Kind, d.doc_key AS DocKey, d.body AS Body
FROM wp_doc d JOIN wp_pack p ON p.id = d.pack_id
WHERE (@packId IS NULL OR d.pack_id = @packId) AND (@prefix IS NULL OR d.kind LIKE CONCAT(@prefix, '%'))
  AND (NOT @enabledOnly OR p.enabled = 1)
ORDER BY d.pack_id, d.kind, d.doc_key", new { packId, prefix = kindPrefix, enabledOnly })).ToList();
    }

    /// <summary>Create/replace (body != null) or delete (body == null) docs as ONE audited, undoable op
    /// (an NPC = template + equipment + vendor rows + spawns undoes as a unit).</summary>
    public async Task<OpResult> SetDocsAsync(int packId, IReadOnlyList<(string kind, string key, string? body)> items,
        string label, string op, string? ip)
    {
        await EnsureSchemaAsync();
        if (items.Count == 0) throw new ArgumentException("nothing to change");
        if (await GetPackAsync(packId) is null) throw new KeyNotFoundException($"pack {packId}");
        foreach (var (kind, key, _) in items)
            if (kind.Length > 64 || key.Length > 190) throw new ArgumentException("doc kind/key too long");
        await _writeLock.WaitAsync();
        try
        {
            using var conn = _db.Admin();
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();
            var payload = new DocBatchPayload();
            foreach (var (kind, key, body) in items)
            {
                string? before = await conn.QueryFirstOrDefaultAsync<string?>(
                    "SELECT body FROM wp_doc WHERE pack_id = @packId AND kind = @kind AND doc_key = @key FOR UPDATE",
                    new { packId, kind, key }, tx);
                if (body is null && before is null) throw new InvalidOperationException($"{kind} {key} does not exist");
                await WriteDocAsync(conn, tx, packId, kind, key, body);
                payload.Items.Add(new DocPayload { Kind = kind, Key = key, Before = before, After = body });
            }
            long opId = await InsertOpAsync(conn, tx, packId, "docs", label, payload, op, ip, null);
            await tx.CommitAsync();
            long auditId = await _audit.LogAsync(new AuditEntry
            {
                Operator = op, OperatorIp = ip, Category = AuditCategory, Action = "content",
                TargetType = "wp_doc", TargetName = label.Length > 100 ? label[..100] : label, TargetId = (int)opId,
                StateBefore = JsonSerializer.Serialize(payload.Items.Select(i => new { i.Kind, i.Key, i.Before }), Json),
                StateAfter = JsonSerializer.Serialize(payload.Items.Select(i => new { i.Kind, i.Key, i.After }), Json),
                IsReversible = true,
                Notes = $"World Pack {packId}: {payload.Items.Count} content doc(s). Takes effect on the next publish.",
            });
            await conn.ExecuteAsync("UPDATE wp_op SET audit_id = @auditId WHERE id = @opId", new { auditId, opId });
            return new OpResult { OpId = opId, AuditId = auditId };
        }
        finally { _writeLock.Release(); }
    }

    private static Task WriteDocAsync(MySqlConnection conn, MySqlTransaction tx, int packId, string kind, string key, string? body) =>
        body is null
            ? conn.ExecuteAsync("DELETE FROM wp_doc WHERE pack_id = @packId AND kind = @kind AND doc_key = @key", new { packId, kind, key }, tx)
            : conn.ExecuteAsync(@"INSERT INTO wp_doc (pack_id, kind, doc_key, body) VALUES (@packId, @kind, @key, @body)
ON DUPLICATE KEY UPDATE body = VALUES(body)", new { packId, kind, key, body }, tx);

    /// <summary>Rows installed into the world DB by the last publish (for removal on the next).</summary>
    public async Task<List<(string tbl, string key)>> InstalledRowsAsync()
    {
        await EnsureSchemaAsync();
        using var conn = _db.Admin();
        return (await conn.QueryAsync<(string, string)>("SELECT tbl, row_key FROM wp_installed_row")).ToList();
    }

    public async Task SetInstalledRowsAsync(IEnumerable<(string tbl, string key, int packId)> rows)
    {
        using var conn = _db.Admin();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("DELETE FROM wp_installed_row", transaction: tx);
        await conn.ExecuteAsync("INSERT INTO wp_installed_row (tbl, row_key, pack_id) VALUES (@tbl, @key, @packId)",
            rows.Select(r => new { r.tbl, r.key, r.packId }), tx);
        await tx.CommitAsync();
    }

    // ═══════════════════════════════════════════════════════════════ publish bookkeeping

    public async Task RecordPublishedAsync(int buildId, IReadOnlyDictionary<(int map, int col, int row), Dictionary<int, float>> sculpt,
        IEnumerable<int> placementIds)
    {
        using var conn = _db.Admin();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("DELETE FROM wp_published_sculpt; DELETE FROM wp_published_placement;", transaction: tx);
        foreach (var ((map, col, row), deltas) in sculpt)
            foreach (var batch in deltas.Chunk(500))
                await conn.ExecuteAsync(
                    "INSERT INTO wp_published_sculpt (map_id, tile_col, tile_row, vertex_index, delta) VALUES (@map, @col, @row, @Key, @Value)",
                    batch.Select(kv => new { map, col, row, kv.Key, kv.Value }), tx);
        await conn.ExecuteAsync("INSERT INTO wp_published_placement (placement_id, build_id) VALUES (@id, @buildId)",
            placementIds.Select(id => new { id, buildId }), tx);
        await tx.CommitAsync();
    }

    // ═══════════════════════════════════════════════════════════════ helpers

    private static void Validate(PlacementInput p)
    {
        if (p.Kind is not ("wmo" or "m2")) throw new ArgumentException("kind must be 'wmo' or 'm2'");
        var ext = Path.GetExtension(p.ModelPath).ToLowerInvariant();
        if (p.Kind == "wmo" && ext != ".wmo") throw new ArgumentException("a wmo placement needs a .wmo root path");
        if (p.Kind == "m2" && ext is not (".m2" or ".mdx")) throw new ArgumentException("an m2 placement needs a .m2/.mdx path");
        if (!float.IsFinite(p.PosX) || !float.IsFinite(p.PosY) || !float.IsFinite(p.PosZ) ||
            MathF.Abs(p.PosX) > WorldCoords.Corner || MathF.Abs(p.PosY) > WorldCoords.Corner)
            throw new ArgumentException("position out of the map");
        if (p.Scale is <= 0.01f or > 64f) throw new ArgumentException("scale out of range");
    }

    private static async Task AddSculptAsync(MySqlConnection conn, MySqlTransaction tx, int packId, SculptPayload s, float sign)
    {
        string table = WorldPackSculptLayers.Table(s.Surface);
        foreach (var t in s.Tiles)
            foreach (var batch in t.Deltas.Chunk(500))
                await conn.ExecuteAsync($@"
INSERT INTO {table} (pack_id, map_id, tile_col, tile_row, vertex_index, delta)
VALUES (@packId, @map, @col, @row, @Key, @delta)
ON DUPLICATE KEY UPDATE delta = delta + VALUES(delta)",
                    batch.Select(kv => new { packId, map = s.MapId, col = t.Col, row = t.Row, kv.Key, delta = kv.Value * sign }), tx);
        await conn.ExecuteAsync($"DELETE FROM {table} WHERE pack_id = @packId AND ABS(delta) < 0.0001", new { packId }, tx);
    }

    private static async Task<long> InsertOpAsync(MySqlConnection conn, MySqlTransaction tx, int packId, string kind, string label,
        object payload, string op, string? ip, long? undoes) =>
        await conn.ExecuteScalarAsync<long>(@"
INSERT INTO wp_op (pack_id, kind, label, payload, operator, operator_ip, undoes)
VALUES (@packId, @kind, @label, @payload, @op, @ip, @undoes); SELECT LAST_INSERT_ID();",
            new { packId, kind, label = label.Length > 160 ? label[..160] : label, payload = JsonSerializer.Serialize(payload, Json), op, ip, undoes }, tx);

    private async Task<long> AuditOpAsync(MySqlConnection conn, long opId, int packId, string action, string op, string? ip,
        object? before, object? after, int? targetId)
    {
        long auditId = await _audit.LogAsync(new AuditEntry
        {
            Operator = op, OperatorIp = ip, Category = AuditCategory, Action = action,
            TargetType = action == "sculpt" ? "wp_sculpt" : "wp_placement",
            TargetName = $"pack {packId} op #{opId}", TargetId = targetId,
            StateBefore = before != null ? JsonSerializer.Serialize(before, Json) : null,
            StateAfter = after != null ? JsonSerializer.Serialize(after, Json) : null,
            IsReversible = true,
            Notes = "World Builder op; revert with /WorldPacks/Undo. Takes effect on the next publish.",
        });
        await conn.ExecuteAsync("UPDATE wp_op SET audit_id = @auditId WHERE id = @opId", new { auditId, opId });
        return auditId;
    }

    private static Task<PlacementRow?> ReadPlacementAsync(MySqlConnection conn, MySqlTransaction? tx, int id) =>
        conn.QueryFirstOrDefaultAsync<PlacementRow?>(@"
SELECT id AS Id, pack_id AS PackId, map_id AS MapId, kind AS Kind, model_path AS ModelPath,
       pos_x AS PosX, pos_y AS PosY, pos_z AS PosZ, rot_x AS RotX, rot_y AS RotY, rot_z AS RotZ,
       scale AS Scale, doodad_set AS DoodadSet, deleted AS Deleted
FROM wp_placement WHERE id = @id", new { id }, tx);

    private static Task WritePlacementAsync(MySqlConnection conn, MySqlTransaction tx, int id,
        float x, float y, float z, float rx, float ry, float rz, float scale, int doodadSet, bool deleted) =>
        conn.ExecuteAsync(@"
UPDATE wp_placement SET pos_x = @x, pos_y = @y, pos_z = @z, rot_x = @rx, rot_y = @ry, rot_z = @rz,
       scale = @scale, doodad_set = @doodadSet, deleted = @deleted WHERE id = @id",
            new { id, x, y, z, rx, ry, rz, scale, doodadSet, deleted }, tx);
}

// ═══════════════════════════════════════════════════════════════ DTOs

public sealed class PackRow
{
    public int Id { get; set; }
    public string PackKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public long Placements { get; set; }
    public long SculptVertices { get; set; }
    public long UndoableOps { get; set; }
}

public sealed class PlacementRow
{
    public int Id { get; set; }
    public int PackId { get; set; }
    public int MapId { get; set; }
    public string Kind { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public float RotX { get; set; }
    public float RotY { get; set; }
    public float RotZ { get; set; }
    public float Scale { get; set; } = 1f;
    public int DoodadSet { get; set; }
    public bool Deleted { get; set; }
    public bool Published { get; set; }
    public uint UniqueId => WorldPackStore.UniqueIdBase + (uint)Id;
}

public class PlacementInput
{
    public int MapId { get; set; }
    public string Kind { get; set; } = "wmo";
    public string ModelPath { get; set; } = "";
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public float RotX { get; set; }
    public float RotY { get; set; }
    public float RotZ { get; set; }
    public float Scale { get; set; } = 1f;
    public int DoodadSet { get; set; }
}

public sealed class SculptTile
{
    public int Col { get; set; }
    public int Row { get; set; }
    public Dictionary<int, float> Deltas { get; set; } = new();
}

public sealed class SculptRequest
{
    public bool Surface { get; set; }
    public int MapId { get; set; }
    public string? Label { get; set; }
    public List<SculptTile> Tiles { get; set; } = new();
}

public sealed class SculptPayload
{
    public bool Surface { get; set; }
    public int MapId { get; set; }
    public List<SculptTile> Tiles { get; set; } = new();
}

public sealed class RelocatePayload
{
    public RelocateSpec? Spec { get; set; }
    public DocBatchPayload Docs { get; set; } = new();
    public List<PlacementPayload> Placements { get; set; } = new();
    public SculptPayload? SculptFrom { get; set; }
    public SculptPayload? SculptTo { get; set; }
    public SculptPayload? SurfaceFrom { get; set; }
    public SculptPayload? SurfaceTo { get; set; }
}

public sealed class PlacementPayload
{
    public PlacementRow? Before { get; set; }
    public PlacementRow? After { get; set; }
}

public sealed class DocRow
{
    public int PackId { get; set; }
    public string Kind { get; set; } = "";
    public string DocKey { get; set; } = "";
    public string Body { get; set; } = "";
}

public sealed class DocBatchPayload
{
    public List<DocPayload> Items { get; set; } = new();
}

public sealed class DocPayload
{
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
}

public sealed class OpRow
{
    public long Id { get; set; }
    public int PackId { get; set; }
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string Operator { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public long? UndoneBy { get; set; }
    public long? Undoes { get; set; }
    public long? AuditId { get; set; }
}

public sealed class OpResult
{
    public long OpId { get; set; }
    public long AuditId { get; set; }
    public long? UndoneOpId { get; set; }
    public string? UndoneKind { get; set; }
    public PlacementRow? Placement { get; set; }
    public SculptPayload? Sculpt { get; set; }
}
