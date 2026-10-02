using MangosSuperUI.Services.WorldPacks;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

/// <summary>
/// World Content Packs API for MSUIClient's Creator Mode World Builder
/// (MSUIClient shared_docs/WORLD_BUILDER.md). The client never writes the server: every edit
/// lands here, is stored as an op and audited (category <c>worldpack</c>), and reaches the game
/// only through <see cref="Publish"/>.
/// </summary>
public class WorldPacksController : Controller
{
    private readonly WorldPackStore _store;
    private readonly WorldPackBuildService _build;
    private readonly WorldPackVerifier _verifier;

    public WorldPacksController(WorldPackStore store, WorldPackBuildService build, WorldPackVerifier verifier)
    {
        _store = store;
        _build = build;
        _verifier = verifier;
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();
    private static string Op(string? op) => string.IsNullOrWhiteSpace(op) ? "msuiclient" : op.Trim()[..Math.Min(op.Trim().Length, 64)];

    private async Task<IActionResult> Guard(Func<Task<object>> body)
    {
        try { return Json(await body()); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ── reads ──────────────────────────────────────────────────────────────

    [HttpGet]
    public Task<IActionResult> Packs() => Guard(async () => new
    {
        success = true,
        packs = await _store.ListPacksAsync(),
        lastBuild = _build.LastBuildInfo(),
    });

    /// <summary>Everything the client needs to preview a map: placements of every pack (so a
    /// disabled pack can still be edited) with their published flag, the enabled-pack sculpt sum
    /// and the sculpt sum of the last publish (what a current patch-7.MPQ already contains).</summary>
    [HttpGet]
    public Task<IActionResult> State(int mapId, int includePackId = 0) => Guard(async () => new
    {
        success = true,
        mapId,
        surfaceSculptSupported = true,
        packs = await _store.ListPacksAsync(),
        placements = await _store.PlacementsAsync(null, mapId, includeDeleted: true),
        sculpt = await _store.SculptAsync(null, mapId, enabledOnly: true, includePackId),
        sourceSculpt = await _store.SculptAsync(null, mapId, enabledOnly: true, includePackId, surface: false),
        surfaceSculpt = await _store.SculptAsync(null, mapId, enabledOnly: true, includePackId, surface: true),
        publishedSculpt = await _store.PublishedSculptAsync(mapId),
        lastBuild = _build.LastBuildInfo(),
    });

    [HttpGet]
    public Task<IActionResult> Ops(int packId, int limit = 50) => Guard(async () => new
    {
        success = true,
        ops = await _store.OpsAsync(packId, Math.Clamp(limit, 1, 500)),
    });

    [HttpGet]
    public IActionResult Status() => Json(new { success = true, build = _build.Snapshot() });

    /// <summary>The published client archive. Drop it in the client's Data folder as patch-7.MPQ.</summary>
    [HttpGet]
    public IActionResult Patch()
    {
        if (!System.IO.File.Exists(_build.PatchPath))
            return NotFound(new { success = false, error = "no World Pack has been published yet" });
        return PhysicalFile(_build.PatchPath, "application/octet-stream", WorldPackBuildService.PatchFileName);
    }

    /// <summary>Server collision/navmesh files a publish owns (client X-Ray / vmap collision sync).
    /// <c>present=false</c> = the publish removed that stock file.</summary>
    /// <summary>Run the World Pack Verifier now over the published world + enabled packs
    /// (shared_docs/WORLD_BUILDER.md §7). Every publish also runs it after the restart.</summary>
    [HttpGet]
    public Task<IActionResult> Verify() => Guard(async () => new { success = true, report = await _verifier.RunAsync(null) });

    /// <summary>Pre-flight: content checks + DB column validation on the unpublished docs (seconds).</summary>
    [HttpGet]
    public Task<IActionResult> Preflight() => Guard(async () => new { success = true, report = await _verifier.PreflightAsync() });

    [HttpGet]
    public IActionResult VerifyReport() => Json(new { success = true, report = _verifier.LastReport() });

    [HttpGet]
    public IActionResult Installed() => Json(new { success = true, files = _build.InstalledFiles() });

    [HttpGet]
    public IActionResult ServerFile(string path)
    {
        string? full = _build.ServerFilePath(path);
        if (full == null) return NotFound(new { success = false, error = "not a published maps/vmaps/mmaps file" });
        return PhysicalFile(full, "application/octet-stream", Path.GetFileName(full));
    }

    // ── writes ─────────────────────────────────────────────────────────────

    public sealed class CreatePackRequest { public string Key { get; set; } = ""; public string Name { get; set; } = ""; public string? Description { get; set; } public string? Operator { get; set; } }
    public sealed class EnableRequest { public int PackId { get; set; } public bool Enabled { get; set; } public string? Operator { get; set; } }
    public sealed class SculptBody { public bool Surface { get; set; } public int PackId { get; set; } public int MapId { get; set; } public string? Label { get; set; } public List<SculptTile> Tiles { get; set; } = new(); public string? Operator { get; set; } }
    public sealed class PlaceBody : PlacementInput { public int PackId { get; set; } public int PlacementId { get; set; } public string? Operator { get; set; } }
    public sealed class IdBody { public int PackId { get; set; } public int PlacementId { get; set; } public string? Operator { get; set; } }
    public sealed class PublishBody { public bool RestartServer { get; set; } = true; public string? Operator { get; set; } }

    [HttpPost]
    public Task<IActionResult> CreatePack([FromBody] CreatePackRequest r) => Guard(async () => new
    {
        success = true,
        pack = await _store.CreatePackAsync(r.Key, r.Name, r.Description, Op(r.Operator), Ip),
    });

    [HttpPost]
    public Task<IActionResult> SetEnabled([FromBody] EnableRequest r) => Guard(async () =>
    {
        await _store.SetEnabledAsync(r.PackId, r.Enabled, Op(r.Operator), Ip);
        return new { success = true };
    });

    [HttpPost]
    [RequestSizeLimit(64_000_000)]
    public Task<IActionResult> Sculpt([FromBody] SculptBody r) => Guard(async () => new
    {
        success = true,
        result = await _store.SculptAsync(r.PackId, new SculptRequest { MapId = r.MapId, Surface = r.Surface, Label = r.Label, Tiles = r.Tiles }, Op(r.Operator), Ip),
    });

    [HttpPost]
    public Task<IActionResult> Place([FromBody] PlaceBody r) => Guard(async () => new
    {
        success = true,
        result = await _store.PlaceAsync(r.PackId, r, Op(r.Operator), Ip),
    });

    [HttpPost]
    public Task<IActionResult> Move([FromBody] PlaceBody r) => Guard(async () => new
    {
        success = true,
        result = await _store.MoveAsync(r.PlacementId, r, Op(r.Operator), Ip),
    });

    [HttpPost]
    public Task<IActionResult> Delete([FromBody] IdBody r) => Guard(async () => new
    {
        success = true,
        result = await _store.DeleteAsync(r.PlacementId, Op(r.Operator), Ip),
    });

    [HttpPost]
    public Task<IActionResult> Undo([FromBody] IdBody r) => Guard(async () => new
    {
        success = true,
        result = await _store.UndoAsync(r.PackId, Op(r.Operator), Ip),
    });

    public sealed class RelocateBody
    {
        public int PackId { get; set; }
        public int FromMap { get; set; }
        public int ToMap { get; set; }
        public int DCol { get; set; }
        public int DRow { get; set; }
        public string? Operator { get; set; }
    }

    /// <summary>
    /// Move a pack's region to another map and/or place by whole ADT tiles (docs, placements, sculpt) as ONE
    /// undoable op - e.g. a zone built as its own map becomes seamless continent land (map 0/1). Takes effect
    /// on the next publish; the verifier then checks every moved spawn, portal and tile again.
    /// </summary>
    [HttpPost]
    public Task<IActionResult> Relocate([FromBody] RelocateBody r) => Guard(async () =>
    {
        if (r.FromMap == r.ToMap && r.DCol == 0 && r.DRow == 0) throw new ArgumentException("nothing to move");
        if (r.DCol is < -63 or > 63 || r.DRow is < -63 or > 63) throw new ArgumentException("dCol/dRow must be whole tiles within the 64x64 grid");
        var spec = new RelocateSpec(r.FromMap, r.ToMap, r.DCol, r.DRow, ToStockMap: r.ToMap < WorldPackContent.MapIdBase);
        return new { success = true, result = await _store.RelocateAsync(r.PackId, spec, Op(r.Operator), Ip) };
    });

    // ── content docs: world-DB rows, client DBC rows, new maps, map tiles ─────────

    [HttpGet]
    public Task<IActionResult> Docs(int? packId, string? kind) => Guard(async () => new
    {
        success = true,
        docs = (await _store.DocsAsync(packId, kind, enabledOnly: false))
            .Select(d => new { d.PackId, d.Kind, d.DocKey, body = System.Text.Json.Nodes.JsonNode.Parse(d.Body) }),
    });

    public sealed class ContentItem
    {
        public string Kind { get; set; } = "";
        public string? Key { get; set; }
        public System.Text.Json.Nodes.JsonObject? Body { get; set; }   // null = delete
    }
    public sealed class ContentBody
    {
        public int PackId { get; set; }
        public string? Label { get; set; }
        public List<ContentItem> Items { get; set; } = new();
        public string? Operator { get; set; }
    }

    /// <summary>
    /// One undoable op over several content docs. Keys are derived server-side from the body where
    /// the kind allows it (DB rows, maps, tiles) so a client cannot file a row under the wrong key,
    /// and every row/DBC id must sit in the pack-reserved ranges (WorldPackContent).
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(64_000_000)]
    public Task<IActionResult> Content([FromBody] ContentBody r) => Guard(async () =>
    {
        var items = new List<(string kind, string key, string? body)>();
        foreach (var it in r.Items)
        {
            string kind = it.Kind.Trim();
            string key;
            if (kind.StartsWith("dbrow:", StringComparison.Ordinal))
            {
                string table = kind[6..];
                if (it.Body is null)
                {
                    if (string.IsNullOrEmpty(it.Key)) throw new ArgumentException("deleting a row needs its key");
                    if (!WorldPackContent.Tables.ContainsKey(table)) throw new ArgumentException($"table '{table}' is not pack-writable");
                    key = it.Key;
                }
                else key = WorldPackContent.RowKey(table, it.Body);
                kind = "dbrow:" + WorldPackContent.Tables.Keys.First(t => t.Equals(table, StringComparison.OrdinalIgnoreCase));
            }
            else if (kind.StartsWith("dbc:", StringComparison.Ordinal))
            {
                string name = kind[4..];
                key = it.Key ?? throw new ArgumentException("dbc rows need a key (the row id)");
                if (!uint.TryParse(key, out uint id)) throw new ArgumentException("dbc key must be the numeric row id");
                if (it.Body is not null) WorldPackContent.ValidateDbc(name, id, it.Body);
                else if (!WorldPackContent.Dbcs.ContainsKey(name)) throw new ArgumentException($"DBC '{name}' is not pack-writable");
            }
            else if (kind == "map")
            {
                key = it.Body is not null ? ((int?)it.Body["mapId"] ?? 0).ToString() : it.Key ?? "";
                if (!int.TryParse(key, out int mapId) || mapId < WorldPackContent.MapIdBase)
                    throw new ArgumentException($"pack maps need mapId >= {WorldPackContent.MapIdBase}");
                if (it.Body is not null)
                {
                    string dir = (string?)it.Body["directory"] ?? "";
                    if (dir.Length is 0 or > 40 || !dir.All(char.IsLetterOrDigit))
                        throw new ArgumentException("map directory must be 1-40 letters/digits");
                }
            }
            else if (kind == "tile")
            {
                if (it.Body is not null)
                {
                    int map = (int?)it.Body["map"] ?? -1, col = (int?)it.Body["col"] ?? -1, row = (int?)it.Body["row"] ?? -1;
                    // A pack map (800+) or a stock continent (0 Eastern Kingdoms, 1 Kalimdor): a stamp there
                    // REPLACES that stock tile while the pack is enabled (the baseline restores it after).
                    if (map is not (0 or 1) && map < WorldPackContent.MapIdBase || col is < 0 or > 63 || row is < 0 or > 63)
                        throw new ArgumentException("tile needs map 0/1 or >= 800 and col/row 0..63");
                    key = $"{map}:{col}:{row}";
                }
                else key = it.Key ?? throw new ArgumentException("deleting a tile needs its key");
            }
            else if (kind == "worldmap")
            {
                key = it.Body is not null ? WorldPackWorldMap.Parse(it.Body).Key
                    : it.Key ?? throw new ArgumentException("deleting a worldmap needs its map:area key");
                if (it.Body is not null)
                    _ = WorldPackCoast.Read(new[] { new DocRow { Kind = "worldmap", DocKey = key, Body = it.Body.ToJsonString() } });
            }
            else if (kind == "npc-replacement")
            {
                key = it.Body is not null ? WorldPackNpcReplacements.Parse(it.Body).SpawnGuid.ToString()
                    : it.Key ?? throw new ArgumentException("deleting an NPC replacement needs its spawn GUID");
                if (!uint.TryParse(key, out uint spawn) || spawn == 0 || spawn >= WorldPackContent.SpawnGuidBase)
                    throw new ArgumentException("NPC replacement key must be a stock spawn GUID");
            }
            else if (kind == "path")
            {
                // A graded path (pass, land bridge, road): absolute heights along a polyline, applied by the build
                // after seams are stitched - exact, idempotent, crack-free across every tile it crosses.
                if (it.Body is not null)
                {
                    var pts = it.Body["points"] as System.Text.Json.Nodes.JsonArray;
                    float width = (float?)it.Body["width"] ?? 0f;
                    if (pts is null || pts.Count < 2 || pts.Any(p => p is not System.Text.Json.Nodes.JsonArray { Count: 3 }) || width <= 0f)
                        throw new ArgumentException("a path needs points [[x,y,z],...] (2+) and a width");
                }
                key = it.Key ?? throw new ArgumentException("a path needs a key (its name)");
            }
            else throw new ArgumentException($"unknown content kind '{kind}'");
            items.Add((kind, key, it.Body?.ToJsonString()));
        }
        var result = await _store.SetDocsAsync(r.PackId, items, r.Label ?? $"{items.Count} content change(s)", Op(r.Operator), Ip);
        return new { success = true, result, keys = items.Select(i => new { i.kind, i.key }) };
    });

    [HttpPost]
    public Task<IActionResult> Publish([FromBody] PublishBody r) => Guard(async () =>
    {
        var (ok, buildId, error) = await _build.StartAsync(Op(r.Operator), Ip, r.RestartServer);
        return new { success = ok, buildId, error };
    });
}
