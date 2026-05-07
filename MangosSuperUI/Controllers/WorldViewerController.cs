using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MangosSuperUI.Services;
using SkiaSharp;
using Dapper;
using MangosSuperUI.Models;

namespace MangosSuperUI.Controllers;

/// <summary>
/// 3D World Viewer — standalone terrain renderer with doodad models and WMO buildings.
/// Self-contained controller with all terrain endpoints.
///
/// Session 43: Multi-tile support — loads 3×3 ADT grid (center + 8 neighbors).
///   Each tile returns full 16×16 chunks. Heights stitched with a global baseline.
///   Texture endpoint fetches one tile at a time (client calls 9× in parallel).
///   Doodads aggregated from all tiles with tile offsets.
///   pixelsPerChunk dropped to 64 for 9× tile load (1024×1024 per tile).
///
/// Endpoints:
///   GET /WorldViewer/              → page
///   GET /WorldViewer/Presets       → available terrain presets
///   GET /WorldViewer/Heightmap     → V9 meshes for 3×3 tile grid (all tiles in one response)
///   GET /WorldViewer/Textures      → composited RGB ground texture for ONE tile (called per-tile)
///   GET /WorldViewer/Doodads       → doodad placements + WMO bounding boxes for 3×3 grid
///   GET /WorldViewer/DoodadModel   → M2 geometry + textures for a single model
/// </summary>
public class WorldViewerController : Controller
{
    private readonly ILogger<WorldViewerController> _logger;
    private readonly IConfiguration _config;
    private readonly ConnectionFactory? _db;

    public WorldViewerController(IConfiguration config, ILogger<WorldViewerController> logger, ConnectionFactory? db = null)
    {
        _config = config;
        _logger = logger;
        _db = db;
    }

    public IActionResult Index() => View();

    // ═══════════════════════════════════════════════════════════════
    // TERRAIN PRESETS
    // ═══════════════════════════════════════════════════════════════

    // gridX = gy = (int)(32 - worldX/533.33) → first field in .map filename
    // gridY = gx = (int)(32 - worldY/533.33) → second field in .map filename
    // File: {mapId:D3}{gridX:D2}{gridY:D2}.map
    private static readonly (string key, string label, int mapId, int gridX, int gridY)[] _terrainPresets =
    {
        ("northshire",  "Northshire Valley",    0, 48, 32),
        ("elwynn",      "Elwynn Forest",        0, 49, 31),
        ("stormwind",   "Stormwind City",       0, 48, 30),
        ("westfall",    "Westfall",             0, 51, 29),
        ("duskwood",    "Duskwood",             0, 51, 32),
        ("redridge",    "Redridge Mountains",   0, 49, 36),
        ("undercity",   "Undercity",            0, 28, 31),
        ("barrens",     "The Barrens",          1, 32, 36),
        ("crossroads",  "The Crossroads",       1, 32, 36),
        ("durotar",     "Durotar",              1, 28, 40),
        ("darkshore",   "Darkshore",            1, 19, 30),
        ("stonetalon",  "Stonetalon Mountains", 1, 29, 32),
    };

    [HttpGet]
    public IActionResult Presets()
    {
        string mapsDir = GetMapsDirectory();
        var available = _terrainPresets
            .Where(p => System.IO.File.Exists(Path.Combine(mapsDir, VmangosMapParser.BuildFilename(p.mapId, p.gridX, p.gridY))))
            .Select(p => new { key = p.key, name = p.label })
            .ToList();
        return Json(new { success = true, presets = available });
    }

    // ═══════════════════════════════════════════════════════════════
    // HEIGHTMAP — multi-tile V9 meshes
    // ═══════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult Heightmap(string? preset, int tileRadius = 1)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Json(new { success = false, error });

        string mapsDir = GetMapsDirectory();
        tileRadius = Math.Clamp(tileRadius, 0, 1);

        // Full tile: cx=8, cy=8, radius=8 → all 16×16 chunks (0-15)
        // radius=7 from center 8 only reaches chunk 1, missing chunk 0!
        const int cx = 8, cy = 8, radius = 8;

        // Pass 1: parse all tiles, find global height range
        var parsed = new Dictionary<(int dx, int dy), VmangosMapParser.TerrainResult>();
        float globalMin = float.MaxValue, globalMax = float.MinValue;

        for (int dy = -tileRadius; dy <= tileRadius; dy++)
        {
            for (int dx = -tileRadius; dx <= tileRadius; dx++)
            {
                int gx = p.gridX + dy;  // gridX=row, shifts with dy (ThreeJS Z)
                int gy = p.gridY + dx;  // gridY=col, shifts with dx (ThreeJS X)
                string path = Path.Combine(mapsDir, VmangosMapParser.BuildFilename(p.mapId, gx, gy));
                if (!System.IO.File.Exists(path)) continue;

                var result = VmangosMapParser.Parse(path, cx, cy, radius);
                if (result == null) continue;

                parsed[(dx, dy)] = result;
                if (result.MinHeight < globalMin) globalMin = result.MinHeight;
                if (result.MaxHeight > globalMax) globalMax = result.MaxHeight;
            }
        }

        if (parsed.Count == 0)
            return Json(new { success = false, error = "No map tiles found." });

        // Global height transform — same for all tiles so they stitch
        float globalMidHeight = (globalMin + globalMax) * 0.5f;
        float globalHeightRange = globalMax - globalMin;
        float globalHeightScale = globalHeightRange > 0 ? Math.Min(3.5f, 350.0f / globalHeightRange) : 3.5f;

        // Full tile = 128 cells wide → 128 * CELL_SIZE in mesh units
        float tileWidthMesh = 128 * VmangosMapParser.CELL_SIZE;

        // Pass 2: rebuild positions with global height + tile offsets
        var tiles = new List<object>();
        foreach (var kv in parsed)
        {
            var (dx, dy) = kv.Key;
            var tr = kv.Value;
            int vertsW = tr.VertsWidth;
            int vertsH = tr.VertsHeight;
            float[] positions = new float[vertsW * vertsH * 3];

            float tileOffsetX = dx * tileWidthMesh;
            float tileOffsetZ = dy * tileWidthMesh;

            for (int i = 0; i < vertsW * vertsH; i++)
            {
                float origX = tr.Positions[i * 3 + 0];
                float origZ = tr.Positions[i * 3 + 2];
                float origY = tr.Positions[i * 3 + 1];

                // Undo the per-tile height transform, apply global
                float rawHeight = tr.HeightScale > 0
                    ? (origY / tr.HeightScale) + ((tr.MinHeight + tr.MaxHeight) * 0.5f)
                    : (tr.MinHeight + tr.MaxHeight) * 0.5f;

                positions[i * 3 + 0] = origX + tileOffsetX;
                positions[i * 3 + 1] = (rawHeight - globalMidHeight) * globalHeightScale;
                positions[i * 3 + 2] = origZ + tileOffsetZ;
            }

            tiles.Add(new
            {
                dx,
                dy,
                gridX = p.gridX + dy,
                gridY = p.gridY + dx,
                positions,
                indices = tr.Indices,
                vertsWidth = vertsW,
                vertsHeight = vertsH,
                chunksWidth = tr.ChunksWidth,
                chunksHeight = tr.ChunksHeight,
            });
        }

        return Json(new
        {
            success = true,
            preset,
            label = p.label,
            tileCount = tiles.Count,
            heightScale = globalHeightScale,
            midHeight = globalMidHeight,
            tileWidthMesh,
            tiles
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // TEXTURES — Composited RGB ground texture (per tile)
    // ═══════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult Textures(string? preset, int tileGridX = -1, int tileGridY = -1, int pixelsPerChunk = 128)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Json(new { success = false, error });

        int gx = tileGridX >= 0 ? tileGridX : p.gridX;
        int gy = tileGridY >= 0 ? tileGridY : p.gridY;
        pixelsPerChunk = Math.Clamp(pixelsPerChunk, 32, 512);

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { success = false, error = "Client data path not configured." });

        try
        {
            var adt = AdtTerrainReader.ReadFromMpq(clientDataPath, MapIdToName(p.mapId), gx, gy);
            if (adt == null) return Json(new { success = false, error = $"ADT not found for grid ({gx},{gy})." });
            if (adt.Textures.Count == 0) return Json(new { success = false, error = "No textures in ADT." });

            var composite = AdtTerrainReader.BuildCompositeTexture(adt, clientDataPath, 8, 8, 8, pixelsPerChunk: pixelsPerChunk);
            if (composite == null) return Json(new { success = false, error = "Composite build failed." });

            return Json(new
            {
                success = true,
                gridX = gx,
                gridY = gy,
                compositeBase64 = Convert.ToBase64String(composite.PngBytes),
                compositeWidth = composite.Width,
                compositeHeight = composite.Height,
                chunksWidth = composite.ChunksWidth,
                chunksHeight = composite.ChunksHeight,
                mode = "composite"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WorldViewer: Textures failed for grid ({GX},{GY})", gx, gy);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // DOODADS + WMOs — multi-tile
    // ═══════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult Doodads(string? preset, int tileRadius = 1)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Json(new { success = false, error });

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { success = false, error = "Client data path not configured." });

        string mapsDir = GetMapsDirectory();
        tileRadius = Math.Clamp(tileRadius, 0, 1);
        const int cx = 8, cy = 8, radius = 8;
        float tileWidthMesh = 128 * VmangosMapParser.CELL_SIZE;

        // Global height range (same calc as Heightmap)
        float globalMin = float.MaxValue, globalMax = float.MinValue;
        for (int dy = -tileRadius; dy <= tileRadius; dy++)
        {
            for (int dx = -tileRadius; dx <= tileRadius; dx++)
            {
                int gx = p.gridX + dy;
                int gy = p.gridY + dx;
                string mp = Path.Combine(mapsDir, VmangosMapParser.BuildFilename(p.mapId, gx, gy));
                if (!System.IO.File.Exists(mp)) continue;
                var tr = VmangosMapParser.Parse(mp, cx, cy, radius);
                if (tr == null) continue;
                if (tr.MinHeight < globalMin) globalMin = tr.MinHeight;
                if (tr.MaxHeight > globalMax) globalMax = tr.MaxHeight;
            }
        }
        float globalMidHeight = (globalMin + globalMax) * 0.5f;
        float globalHeightRange = globalMax - globalMin;
        float globalHeightScale = globalHeightRange > 0 ? Math.Min(3.5f, 350.0f / globalHeightRange) : 3.5f;

        var allDoodads = new List<object>();
        var allWmos = new List<object>();

        for (int dy = -tileRadius; dy <= tileRadius; dy++)
        {
            for (int dx = -tileRadius; dx <= tileRadius; dx++)
            {
                int gx = p.gridX + dy;
                int gy = p.gridY + dx;

                try
                {
                    var adt = AdtTerrainReader.ReadFromMpq(clientDataPath, MapIdToName(p.mapId), gx, gy);
                    if (adt == null) continue;

                    var doodads = AdtTerrainReader.GetDoodadsForRegion(adt, cx, cy, radius, globalHeightScale, globalMidHeight, gx, gy);

                    float tileOffX = dx * tileWidthMesh;
                    float tileOffZ = dy * tileWidthMesh;

                    foreach (var d in doodads)
                    {
                        allDoodads.Add(new
                        {
                            model = d.ModelPath,
                            type = d.Type,
                            x = d.X + tileOffX,
                            y = d.Y,
                            z = d.Z + tileOffZ,
                            rotY = d.RotY,
                            scale = d.Scale
                        });
                    }

                    // WMOs
                    var rawWmos = AdtTerrainReader.GetWmosForRegion(adt, globalHeightScale, globalMidHeight);
                    int minCX = Math.Max(0, cx - radius), maxCX = Math.Min(15, cx + radius);
                    int minCY = Math.Max(0, cy - radius), maxCY = Math.Min(15, cy + radius);
                    int v9StartX = minCX * 8, v9StartY = minCY * 8;
                    int vertsW = (maxCX * 8 + 8) - v9StartX + 1;
                    int vertsH = (maxCY * 8 + 8) - v9StartY + 1;
                    float offsetX = -((vertsW - 1) * AdtTerrainReader.CELL_SIZE) * 0.5f;
                    float offsetZ = -((vertsH - 1) * AdtTerrainReader.CELL_SIZE) * 0.5f;

                    foreach (var w in rawWmos)
                    {
                        float col = (w.PosX / AdtTerrainReader.GRID_SIZE - gy) * 128;
                        float row = (w.PosZ / AdtTerrainReader.GRID_SIZE - gx) * 128;
                        allWmos.Add(new
                        {
                            model = w.ModelPath,
                            x = offsetX + (col - v9StartX) * AdtTerrainReader.CELL_SIZE + tileOffX,
                            y = (w.PosY - globalMidHeight) * globalHeightScale,
                            z = offsetZ + (row - v9StartY) * AdtTerrainReader.CELL_SIZE + tileOffZ,
                            rotX = w.RotX,
                            rotY = w.RotY,
                            rotZ = w.RotZ,
                            sizeX = Math.Abs(w.BbMaxX - w.BbMinX) * AdtTerrainReader.CELL_SIZE / AdtTerrainReader.GRID_SIZE * 128,
                            sizeY = Math.Abs(w.BbMaxY - w.BbMinY) * globalHeightScale,
                            sizeZ = Math.Abs(w.BbMaxZ - w.BbMinZ) * AdtTerrainReader.CELL_SIZE / AdtTerrainReader.GRID_SIZE * 128
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WorldViewer: Doodads failed for tile ({GX},{GY})", gx, gy);
                }
            }
        }

        return Json(new
        {
            success = true,
            doodads = allDoodads,
            wmos = allWmos,
            totalDoodads = allDoodads.Count,
            totalWmos = allWmos.Count
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // DOODAD MODEL — M2 geometry + textures (unchanged from Session 42)
    // ═══════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, object?> _doodadModelCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _doodadModelCacheLock = new();

    [HttpGet]
    public IActionResult DoodadModel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BadRequest("path required");

        string cacheKey = path.Replace('/', '\\').ToLowerInvariant();
        lock (_doodadModelCacheLock)
        {
            if (_doodadModelCache.TryGetValue(cacheKey, out var cached))
                return cached == null ? NotFound("Model not found") : Json(cached);
        }

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath)) return BadRequest("Client data path not configured");

        try
        {
            string mpqPath = path;
            if (mpqPath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
                mpqPath = mpqPath[..^4] + ".m2";

            byte[]? m2Data = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, mpqPath)
                          ?? AdtTerrainReader.ReadFileFromMpqs(clientDataPath, path);
            if (m2Data == null)
            {
                lock (_doodadModelCacheLock) { _doodadModelCache[cacheKey] = null; }
                return NotFound("M2 not found in MPQs");
            }

            var model = M2Reader.Parse(m2Data);
            if (model == null || !model.IsValid)
            {
                lock (_doodadModelCacheLock) { _doodadModelCache[cacheKey] = null; }
                return NotFound("M2 parse failed");
            }

            var positions = new float[model.Vertices.Count * 3];
            var normals = new float[model.Vertices.Count * 3];
            var uvs = new float[model.Vertices.Count * 2];
            for (int i = 0; i < model.Vertices.Count; i++)
            {
                var v = model.Vertices[i];
                positions[i * 3] = v.PosX; positions[i * 3 + 1] = v.PosY; positions[i * 3 + 2] = v.PosZ;
                normals[i * 3] = v.NormX; normals[i * 3 + 1] = v.NormY; normals[i * 3 + 2] = v.NormZ;
                uvs[i * 2] = v.TexU; uvs[i * 2 + 1] = v.TexV;
            }
            var allIndices = model.Indices.Select(idx => (int)idx).ToArray();

            var textureMap = new Dictionary<int, string>();
            string m2Dir = Path.GetDirectoryName(mpqPath)?.Replace('/', '\\') ?? "";
            for (int ti = 0; ti < model.Textures.Count; ti++)
            {
                var tex = model.Textures[ti];
                if (tex.Type == 0 && !string.IsNullOrEmpty(tex.Filename))
                {
                    byte[]? png = AdtTerrainReader.ReadBlpAsPng(clientDataPath, tex.Filename);
                    if (png != null) { textureMap[ti] = "data:image/png;base64," + Convert.ToBase64String(png); continue; }
                    if (!tex.Filename.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                    {
                        png = AdtTerrainReader.ReadBlpAsPng(clientDataPath, tex.Filename + ".blp");
                        if (png != null) { textureMap[ti] = "data:image/png;base64," + Convert.ToBase64String(png); continue; }
                    }
                }
                if (!textureMap.ContainsKey(ti) && !string.IsNullOrEmpty(m2Dir))
                {
                    string baseName = Path.GetFileNameWithoutExtension(mpqPath);
                    foreach (var c in new[] { $"{m2Dir}\\{baseName}.blp", $"{m2Dir}\\{baseName}Skin.blp", $"{m2Dir}\\{baseName}_01.blp", $"{m2Dir}\\{baseName}01.blp" })
                    {
                        if (textureMap.ContainsKey(ti)) break;
                        byte[]? png = AdtTerrainReader.ReadBlpAsPng(clientDataPath, c);
                        if (png != null) textureMap[ti] = "data:image/png;base64," + Convert.ToBase64String(png);
                    }
                }
            }

            var subTexMap = new Dictionary<int, int>();
            bool degenerate = model.TextureLookup.Count == 0 || model.TextureLookup.All(x => x == model.TextureLookup[0]);
            foreach (var b in model.Batches)
            {
                if (subTexMap.ContainsKey(b.SubmeshIndex)) continue;
                subTexMap[b.SubmeshIndex] = degenerate ? b.TextureIndex
                    : (b.TextureIndex < model.TextureLookup.Count ? model.TextureLookup[b.TextureIndex] : b.TextureIndex);
            }

            var submeshes = new List<object>();
            for (int si = 0; si < Math.Max(1, model.Submeshes.Count); si++)
            {
                int idxStart = model.Submeshes.Count > 0 ? (int)model.Submeshes[si].IndexStart : 0;
                int idxCount = model.Submeshes.Count > 0 ? (int)model.Submeshes[si].IndexCount : allIndices.Length;
                int texIdx = subTexMap.ContainsKey(si) ? subTexMap[si] : -1;
                string? texB64 = texIdx >= 0 && textureMap.ContainsKey(texIdx) ? textureMap[texIdx]
                    : textureMap.Count > 0 ? textureMap[textureMap.Keys.OrderBy(k => Math.Abs(k - si)).First()] : null;
                submeshes.Add(new { indexStart = idxStart, indexCount = idxCount, textureBase64 = texB64 });
            }

            var result = new
            {
                success = true,
                name = model.Name,
                vertexCount = model.Vertices.Count,
                indexCount = allIndices.Length,
                positions,
                normals,
                uvs,
                indices = allIndices,
                submeshes,
                submeshCount = model.Submeshes.Count,
                textureCount = model.Textures.Count,
                texturesResolved = textureMap.Count
            };

            lock (_doodadModelCacheLock) { _doodadModelCache[cacheKey] = result; }
            return Json(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WorldViewer: DoodadModel failed for {Path}", path);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WMO MODEL — building geometry + textures
    // ═══════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, object?> _wmoModelCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _wmoModelCacheLock = new();

    [HttpGet]
    public IActionResult WmoModel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BadRequest("path required");

        string cacheKey = path.Replace('/', '\\').ToLowerInvariant();
        lock (_wmoModelCacheLock)
        {
            if (_wmoModelCache.TryGetValue(cacheKey, out var cached))
                return cached == null ? NotFound("WMO not found") : Json(cached);
        }

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath)) return BadRequest("Client data path not configured");

        try
        {
            // Read root WMO — try multiple path formats
            string mpqPath = path.Replace('/', '\\');
            _logger.LogInformation("WmoModel: Looking for root WMO: {Path}", mpqPath);

            byte[]? rootData = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, mpqPath);
            if (rootData == null)
            {
                // Try with forward slashes
                string altPath = path.Replace('\\', '/');
                _logger.LogInformation("WmoModel: Backslash path failed, trying: {Path}", altPath);
                rootData = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, altPath);
            }
            if (rootData == null)
            {
                _logger.LogWarning("WmoModel: Root WMO not found in any MPQ: {Path}", path);
                // Don't cache nulls — avoids poisoning if MPQs weren't loaded yet
                return NotFound("WMO root not found in MPQs");
            }
            _logger.LogInformation("WmoModel: Root WMO loaded, {Size} bytes", rootData.Length);

            var root = WmoReader.ParseRoot(rootData);
            if (root == null)
            {
                // Don't cache parse failures — might be a transient issue
                _logger.LogWarning("WmoModel: ParseRoot returned null for {Path} ({Size} bytes)", path, rootData.Length);
                return NotFound("WMO root parse failed");
            }
            _logger.LogInformation("WmoModel: Parsed root — {Groups} groups, {Mats} materials, {Texs} textures",
                root.NGroups, root.Materials.Count, root.NTextures);

            // Load textures from materials
            var textureCache = new Dictionary<int, string>(); // materialId → base64 PNG
            for (int mi = 0; mi < root.Materials.Count; mi++)
            {
                var mat = root.Materials[mi];
                string texPath = mat.Texture0Name;
                if (string.IsNullOrEmpty(texPath)) continue;

                byte[]? png = AdtTerrainReader.ReadBlpAsPng(clientDataPath, texPath);
                if (png != null)
                {
                    textureCache[mi] = "data:image/png;base64," + Convert.ToBase64String(png);
                }
            }

            // Read group files and merge geometry
            // Build basePath in both slash directions — War3Net is slash-sensitive
            string basePath = path.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)
                ? path[..^4] : path;
            string basePathBackslash = basePath.Replace('/', '\\');
            string basePathForward = basePath.Replace('\\', '/');

            var allPositions = new List<float>();
            var allNormals = new List<float>();
            var allUvs = new List<float>();
            var allIndices = new List<int>();
            var submeshes = new List<object>();
            int globalVertexOffset = 0;

            for (int gi = 0; gi < (int)root.NGroups; gi++)
            {
                // Try backslash first (works for most MPQs), then forward slash
                string groupPathBS = $"{basePathBackslash}_{gi:D3}.wmo";
                string groupPathFS = $"{basePathForward}_{gi:D3}.wmo";

                byte[]? groupData = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, groupPathBS)
                                 ?? AdtTerrainReader.ReadFileFromMpqs(clientDataPath, groupPathFS);
                if (groupData == null)
                {
                    _logger.LogWarning("WmoModel: Group file not found: {BS} / {FS}", groupPathBS, groupPathFS);
                    continue;
                }

                var group = WmoReader.ParseGroup(groupData);
                if (group == null)
                {
                    _logger.LogWarning("WmoModel: Group parse failed: {Path} ({Size} bytes)", groupPathBS, groupData.Length);
                    continue;
                }
                _logger.LogInformation("WmoModel: Group {Idx} — {Verts} verts, {Idxs} indices, {Batches} batches",
                    gi, group.Vertices.Count, group.Indices.Count, group.Batches.Count);

                // Add vertices — WMO is Z-up. Convert to Three.js Y-up + 90° CCW rotation.
                // Z-up→Y-up: (x, z, -y), then 90° CCW in XZ: (y, z, x)
                foreach (var (x, y, z) in group.Vertices)
                {
                    allPositions.Add(y);     // Three X
                    allPositions.Add(z);     // Three Y (up)
                    allPositions.Add(x);     // Three Z
                }

                // Add normals (same transform)
                if (group.Normals.Count == group.Vertices.Count)
                {
                    foreach (var (nx, ny, nz) in group.Normals)
                    {
                        allNormals.Add(ny);
                        allNormals.Add(nz);
                        allNormals.Add(nx);
                    }
                }
                else
                {
                    // Fill with up normals if missing
                    for (int i = 0; i < group.Vertices.Count; i++)
                    {
                        allNormals.Add(0); allNormals.Add(1); allNormals.Add(0);
                    }
                }

                // Add UVs
                if (group.UVs.Count == group.Vertices.Count)
                {
                    foreach (var (u, v) in group.UVs)
                    {
                        allUvs.Add(u);
                        allUvs.Add(v);
                    }
                }
                else
                {
                    for (int i = 0; i < group.Vertices.Count; i++)
                    {
                        allUvs.Add(0); allUvs.Add(0);
                    }
                }

                // Build submeshes from batches (or from per-triangle materials)
                if (group.Batches.Count > 0)
                {
                    foreach (var batch in group.Batches)
                    {
                        int idxStart = allIndices.Count;
                        int end = (int)Math.Min(batch.IndexStart + batch.IndexCount, group.Indices.Count);
                        for (int i = (int)batch.IndexStart; i < end; i++)
                        {
                            allIndices.Add(group.Indices[i] + globalVertexOffset);
                        }

                        int matId = batch.MaterialId;
                        string? texB64 = textureCache.ContainsKey(matId) ? textureCache[matId] : null;
                        bool noCull = matId < root.Materials.Count && root.Materials[matId].IsNoCull;
                        bool transparent = matId < root.Materials.Count && root.Materials[matId].IsTransparent;

                        submeshes.Add(new
                        {
                            indexStart = idxStart,
                            indexCount = allIndices.Count - idxStart,
                            textureBase64 = texB64,
                            materialId = matId,
                            doubleSided = noCull,
                            transparent,
                            groupIndex = gi
                        });
                    }
                }
                else
                {
                    // No batches — dump all indices as one submesh
                    int idxStart = allIndices.Count;
                    // Filter out collision-only triangles (materialId == 0xFF)
                    for (int ti = 0; ti < group.Indices.Count / 3; ti++)
                    {
                        byte matId = ti < group.TriMaterials.Count ? group.TriMaterials[ti].materialId : (byte)0;
                        if (matId == 0xFF) continue; // collision only, skip

                        int i0 = group.Indices[ti * 3 + 0] + globalVertexOffset;
                        int i1 = group.Indices[ti * 3 + 1] + globalVertexOffset;
                        int i2 = group.Indices[ti * 3 + 2] + globalVertexOffset;
                        allIndices.Add(i0);
                        allIndices.Add(i1);
                        allIndices.Add(i2);
                    }

                    // Use first material's texture as fallback
                    string? texB64 = textureCache.Count > 0 ? textureCache.Values.First() : null;
                    submeshes.Add(new
                    {
                        indexStart = idxStart,
                        indexCount = allIndices.Count - idxStart,
                        textureBase64 = texB64,
                        materialId = 0,
                        doubleSided = false,
                        transparent = false,
                        groupIndex = gi
                    });
                }

                globalVertexOffset += group.Vertices.Count;
            }

            if (allPositions.Count == 0)
            {
                // Don't cache empty results
                return NotFound("WMO has no geometry");
            }

            var result = new
            {
                success = true,
                name = Path.GetFileNameWithoutExtension(path),
                vertexCount = allPositions.Count / 3,
                indexCount = allIndices.Count,
                groupCount = (int)root.NGroups,
                positions = allPositions.ToArray(),
                normals = allNormals.ToArray(),
                uvs = allUvs.ToArray(),
                indices = allIndices.ToArray(),
                submeshes,
                submeshCount = submeshes.Count,
                texturesResolved = textureCache.Count
            };

            lock (_wmoModelCacheLock) { _wmoModelCache[cacheKey] = result; }
            return Json(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WorldViewer: WmoModel failed for {Path}", path);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WMO CATALOG — enumerate all WMO root files from MPQ listfiles
    // ═══════════════════════════════════════════════════════════════

    private static List<object>? _wmoCatalogCache = null;
    private static readonly object _wmoCatalogLock = new();

    [HttpGet]
    public IActionResult WmoCatalog()
    {
        lock (_wmoCatalogLock)
        {
            if (_wmoCatalogCache != null)
                return Json(new { success = true, entries = _wmoCatalogCache });
        }

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return BadRequest("Client data path not configured");

        try
        {
            // Read (listfile) from all MPQs and collect WMO root paths
            var allWmoPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mpqFiles = Directory.GetFiles(clientDataPath, "*.MPQ", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(clientDataPath, "*.mpq", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var mpqPath in mpqFiles)
            {
                try
                {
                    using var stream = System.IO.File.OpenRead(mpqPath);
                    using var archive = new War3Net.IO.Mpq.MpqArchive(stream);

                    if (archive.TryOpenFile("(listfile)", out var listStream))
                    {
                        using (listStream)
                        using (var reader = new StreamReader(listStream))
                        {
                            string? line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                line = line.Trim();
                                if (string.IsNullOrEmpty(line)) continue;
                                if (!line.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)) continue;

                                // Skip group files (e.g. building_000.wmo, building_001.wmo)
                                // Group files have _NNN.wmo suffix where NNN is 3 digits
                                string filename = Path.GetFileNameWithoutExtension(line);
                                if (filename.Length >= 4 &&
                                    filename[^4] == '_' &&
                                    char.IsDigit(filename[^3]) &&
                                    char.IsDigit(filename[^2]) &&
                                    char.IsDigit(filename[^1]))
                                    continue;

                                allWmoPaths.Add(line.Replace('/', '\\'));
                            }
                        }
                    }
                }
                catch { /* skip unreadable MPQs */ }
            }

            // Build catalog entries with directory-based categorization
            // Typical path: World\wmo\Azeroth\Buildings\Stormwind\Stormwind.wmo
            var entries = allWmoPaths
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    var parts = p.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    // Find the segment after "wmo" as category
                    int wmoIdx = -1;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i].Equals("wmo", StringComparison.OrdinalIgnoreCase))
                        { wmoIdx = i; break; }
                    }

                    string category = wmoIdx >= 0 && wmoIdx + 1 < parts.Length
                        ? parts[wmoIdx + 1] : "Other";
                    string subcategory = wmoIdx >= 0 && wmoIdx + 2 < parts.Length
                        ? parts[wmoIdx + 2] : "";
                    string name = Path.GetFileNameWithoutExtension(p);

                    return (object)new
                    {
                        path = p,
                        name,
                        category,
                        subcategory,
                        fullDir = string.Join("\\", parts.Take(parts.Length - 1))
                    };
                })
                .ToList();

            lock (_wmoCatalogLock) { _wmoCatalogCache = entries; }

            _logger.LogInformation("WmoCatalog: Found {Count} WMO root files", entries.Count);
            return Json(new { success = true, entries, totalCount = entries.Count });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WorldViewer: WmoCatalog failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WMO PREVIEW — lightweight metadata without full geometry load
    // ═══════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult WmoPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BadRequest("path required");

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath)) return BadRequest("Client data path not configured");

        try
        {
            string mpqPath = path.Replace('/', '\\');
            byte[]? rootData = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, mpqPath)
                            ?? AdtTerrainReader.ReadFileFromMpqs(clientDataPath, path.Replace('\\', '/'));

            if (rootData == null)
                return NotFound("WMO root not found in MPQs");

            var root = WmoReader.ParseRoot(rootData);
            if (root == null)
                return NotFound("WMO root parse failed");

            // Compute physical dimensions from bounding box (WoW units ≈ yards)
            float sizeX = Math.Abs(root.BbMaxX - root.BbMinX);
            float sizeY = Math.Abs(root.BbMaxY - root.BbMinY);
            float sizeZ = Math.Abs(root.BbMaxZ - root.BbMinZ);

            // Get first texture as thumbnail hint
            string? firstTexture = null;
            foreach (var mat in root.Materials)
            {
                if (!string.IsNullOrEmpty(mat.Texture0Name))
                {
                    byte[]? png = AdtTerrainReader.ReadBlpAsPng(clientDataPath, mat.Texture0Name);
                    if (png != null)
                    {
                        firstTexture = "data:image/png;base64," + Convert.ToBase64String(png);
                        break;
                    }
                }
            }

            return Json(new
            {
                success = true,
                name = Path.GetFileNameWithoutExtension(path),
                path,
                groups = (int)root.NGroups,
                materials = root.Materials.Count,
                textures = (int)root.NTextures,
                sizeX = Math.Round(sizeX, 1),
                sizeY = Math.Round(sizeY, 1),
                sizeZ = Math.Round(sizeZ, 1),
                bbMin = new { x = root.BbMinX, y = root.BbMinY, z = root.BbMinZ },
                bbMax = new { x = root.BbMaxX, y = root.BbMaxY, z = root.BbMaxZ },
                thumbnail = firstTexture
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WorldViewer: WmoPreview failed for {Path}", path);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // TERRAIN HEIGHT — server-side height lookup for placement snapping
    // ═══════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult TerrainHeight(string? preset, float worldX = 0, float worldZ = 0)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Json(new { success = false, error });

        string mapsDir = GetMapsDirectory();

        // Convert world coords to grid tile
        // gridX = floor(32 - worldX/533.33), gridY = floor(32 - worldZ/533.33)
        // But worldX/worldZ here are in mesh space, we need to convert back to WoW world space
        // For now, we work with the current preset's tile and interpolate within it
        int gx = p.gridX;
        int gy = p.gridY;

        string mapPath = Path.Combine(mapsDir, VmangosMapParser.BuildFilename(p.mapId, gx, gy));
        if (!System.IO.File.Exists(mapPath))
            return Json(new { success = false, error = "Map file not found" });

        try
        {
            byte[] data = System.IO.File.ReadAllBytes(mapPath);
            var terrain = VmangosMapParser.Parse(data, 8, 8, 8);
            if (terrain == null || terrain.Positions.Length == 0)
                return Json(new { success = false, error = "Could not parse terrain" });

            // Find the height at the given position by nearest-vertex lookup
            // positions[] is flat xyz: [x0,y0,z0, x1,y1,z1, ...]
            float closestY = 0;
            float closestDist = float.MaxValue;

            for (int i = 0; i < terrain.Positions.Length; i += 3)
            {
                float vx = terrain.Positions[i];
                float vy = terrain.Positions[i + 1]; // height
                float vz = terrain.Positions[i + 2];
                float dx = vx - worldX;
                float dz = vz - worldZ;
                float dist = dx * dx + dz * dz;
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestY = vy;
                }
            }

            return Json(new { success = true, height = closestY, worldX, worldZ });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WorldViewer: TerrainHeight failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WMO PLACEMENT PERSISTENCE — save/load/delete custom placements
    // ═══════════════════════════════════════════════════════════════

    private const string CREATE_PLACEMENTS_TABLE = @"
        CREATE TABLE IF NOT EXISTS custom_wmo_placements (
            id          INT AUTO_INCREMENT PRIMARY KEY,
            preset      VARCHAR(64) NOT NULL,
            map_id      INT NOT NULL DEFAULT 0,
            wmo_path    VARCHAR(512) NOT NULL,
            wmo_name    VARCHAR(128) NOT NULL DEFAULT '',
            mesh_x      FLOAT NOT NULL,
            mesh_y      FLOAT NOT NULL,
            mesh_z      FLOAT NOT NULL,
            rot_y       FLOAT NOT NULL DEFAULT 0,
            scale_val   FLOAT NOT NULL DEFAULT 1.5,
            go_entry    INT DEFAULT NULL,
            go_guid     INT DEFAULT NULL,
            committed   TINYINT NOT NULL DEFAULT 0,
            created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            INDEX idx_preset (preset)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

    private const string ALTER_PLACEMENTS_ADD_COLS = @"
        ALTER TABLE custom_wmo_placements
            ADD COLUMN IF NOT EXISTS go_entry INT DEFAULT NULL,
            ADD COLUMN IF NOT EXISTS go_guid INT DEFAULT NULL,
            ADD COLUMN IF NOT EXISTS committed TINYINT NOT NULL DEFAULT 0;";

    [HttpPost]
    public IActionResult SavePlacement([FromBody] WmoPlacementDto dto)
    {
        if (_db == null) return Json(new { success = false, error = "Database not configured" });
        if (string.IsNullOrWhiteSpace(dto?.Preset) || string.IsNullOrWhiteSpace(dto?.WmoPath))
            return Json(new { success = false, error = "preset and wmoPath required" });

        try
        {
            using var conn = _db.Admin();
            conn.Open();

            // Ensure table exists (and has new columns if upgraded)
            conn.Execute(CREATE_PLACEMENTS_TABLE);
            try { conn.Execute(ALTER_PLACEMENTS_ADD_COLS); } catch { /* columns already exist */ }

            var id = conn.ExecuteScalar<int>(@"
                INSERT INTO custom_wmo_placements (preset, map_id, wmo_path, wmo_name, mesh_x, mesh_y, mesh_z, rot_y, scale_val)
                VALUES (@Preset, @MapId, @WmoPath, @WmoName, @MeshX, @MeshY, @MeshZ, @RotY, @Scale);
                SELECT LAST_INSERT_ID();",
                new { dto.Preset, MapId = dto.MapId, dto.WmoPath, WmoName = dto.WmoName ?? "", dto.MeshX, dto.MeshY, dto.MeshZ, dto.RotY, Scale = dto.Scale });

            _logger.LogInformation("SavePlacement: Saved WMO {Name} at ({X},{Y},{Z}) for preset {Preset}, id={Id}",
                dto.WmoName, dto.MeshX, dto.MeshY, dto.MeshZ, dto.Preset, id);

            return Json(new { success = true, id });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SavePlacement failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    [HttpGet]
    public IActionResult LoadPlacements(string? preset)
    {
        if (_db == null) return Json(new { success = false, error = "Database not configured" });
        if (string.IsNullOrWhiteSpace(preset)) return Json(new { success = false, error = "preset required" });

        try
        {
            using var conn = _db.Admin();
            conn.Open();

            // Ensure table exists (first load might precede any save)
            conn.Execute(CREATE_PLACEMENTS_TABLE);

            var rows = conn.Query(@"
                SELECT id, preset, map_id AS mapId, wmo_path AS wmoPath, wmo_name AS wmoName,
                       mesh_x AS meshX, mesh_y AS meshY, mesh_z AS meshZ,
                       rot_y AS rotY, scale_val AS scale, committed
                FROM custom_wmo_placements
                WHERE preset = @Preset
                ORDER BY id", new { Preset = preset }).ToList();

            return Json(new { success = true, placements = rows, count = rows.Count });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LoadPlacements failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    [HttpPost]
    public IActionResult DeletePlacement([FromBody] DeletePlacementDto dto)
    {
        if (_db == null) return Json(new { success = false, error = "Database not configured" });
        if (dto?.Id == null || dto.Id <= 0) return Json(new { success = false, error = "id required" });

        try
        {
            using var adminConn = _db.Admin();
            adminConn.Open();

            // Read the placement before deleting (need preset + committed flag)
            var placement = adminConn.QueryFirstOrDefault(@"
                SELECT id, preset, map_id AS mapId, committed, go_entry, go_guid
                FROM custom_wmo_placements WHERE id = @Id",
                new { dto.Id });

            if (placement == null)
                return Json(new { success = false, error = "Placement not found" });

            string preset = (string)placement.preset;
            bool wasCommitted = ((int?)placement.committed ?? 0) == 1;

            // Legacy gameobject cleanup (from S47 type-14 approach)
            int? goEntry = (int?)placement.go_entry;
            int? goGuid = (int?)placement.go_guid;
            if (goGuid != null || goEntry != null)
            {
                using var mangosConn = _db.Mangos();
                mangosConn.Open();
                if (goGuid != null)
                    mangosConn.Execute("DELETE FROM gameobject WHERE guid = @Guid", new { Guid = goGuid });
                if (goEntry != null)
                {
                    var otherCount = mangosConn.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM gameobject WHERE id = @Entry", new { Entry = goEntry });
                    if (otherCount == 0)
                        mangosConn.Execute("DELETE FROM gameobject_template WHERE entry = @Entry", new { Entry = goEntry });
                }
            }

            // Delete from admin DB
            adminConn.Execute("DELETE FROM custom_wmo_placements WHERE id = @Id", new { dto.Id });

            // Always check if patch-Z.MPQ needs cleanup — not just when wasCommitted
            bool patchCleaned = false;
            int remainingCommitted = adminConn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM custom_wmo_placements WHERE preset = @Preset AND committed = 1",
                new { Preset = preset });

            if (remainingCommitted > 0)
            {
                // Rebuild with remaining committed placements
                patchCleaned = RebuildPatchMpqForPreset(adminConn, preset);
            }
            else
            {
                // No committed placements left — delete patch-Z.MPQ if it exists
                string clientDataPath = GetClientDataDirectory();
                if (!string.IsNullOrEmpty(clientDataPath))
                {
                    string patchPath = Path.Combine(clientDataPath, "patch-Z.MPQ");
                    if (System.IO.File.Exists(patchPath))
                    {
                        System.IO.File.Delete(patchPath);
                        patchCleaned = true;
                        _logger.LogInformation(
                            "DeletePlacement: Deleted orphaned patch-Z.MPQ (no committed placements remain for preset '{Preset}')",
                            preset);
                    }
                }
            }

            // Always invalidate cache after any placement change
            InvalidatePlacementCache();

            _logger.LogInformation(
                "DeletePlacement: Removed placement {Id}, wasCommitted={Committed}, patchCleaned={Cleaned}",
                dto.Id, wasCommitted, patchCleaned);

            return Json(new { success = true, deleted = true, patchCleaned });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeletePlacement failed");
            return Json(new { success = false, error = ex.Message });
        }
    }


    public class WmoPlacementDto
    {
        public string Preset { get; set; } = "";
        public int MapId { get; set; }
        public string WmoPath { get; set; } = "";
        public string? WmoName { get; set; }
        public float MeshX { get; set; }
        public float MeshY { get; set; }
        public float MeshZ { get; set; }
        public float RotY { get; set; }
        public float Scale { get; set; } = 1.5f;
    }

    public class DeletePlacementDto
    {
        public int Id { get; set; }
    }

    public class CommitPlacementDto
    {
        public int PlacementDbId { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // ═══════════════════════════════════════════════════════════════
    // COMMIT TO GAME WORLD — ADT MODF Patching
    // ═══════════════════════════════════════════════════════════════
    //
    // Pipeline (Session 49 — fixed MCRF patching):
    //   1. Read saved placement from custom_wmo_placements
    //   2. Convert mesh coords → MODF coords (ADT space)
    //   3. Read original ADT from MPQ via War3Net
    //   4. Batch-patch ADT: MWMO + MWID + MODF + MCRF for all committed placements
    //   5. Package patched ADT into patch-Z.MPQ
    //   6. Mark placement as committed
    //
    // The client loads the patched ADT and renders the WMO through its normal
    // world-building pipeline — exactly how Blizzard's original buildings work.
    // No gameobject_template or gameobject rows needed.
    //
    // Coordinate transform (mesh → MODF):
    //   modfPosX = (meshX / (128 * CELL_SIZE) + 0.5 + gridY) * GRID_SIZE
    //   modfPosY = meshY / heightScale + midHeight
    //   modfPosZ = (meshZ / (128 * CELL_SIZE) + 0.5 + gridX) * GRID_SIZE

    [HttpPost]
    public IActionResult CommitToWorld([FromBody] CommitPlacementDto dto)
    {
        if (_db == null) return Json(new { success = false, error = "Database not configured" });

        try
        {
            using var adminConn = _db.Admin();
            adminConn.Open();

            var placement = adminConn.QueryFirstOrDefault(@"
                SELECT id, preset, map_id AS mapId, wmo_path AS wmoPath, wmo_name AS wmoName,
                       mesh_x AS meshX, mesh_y AS meshY, mesh_z AS meshZ,
                       rot_y AS rotY, scale_val AS scaleVal
                FROM custom_wmo_placements WHERE id = @Id",
                new { Id = dto.PlacementDbId });

            if (placement == null)
                return Json(new { success = false, error = "Placement not found" });

            string preset = (string)placement.preset;

            if (!TryResolvePreset(preset, out var p, out var error))
                return Json(new { success = false, error = "Invalid preset: " + error });

            // Mark as committed first (so RebuildPatch picks it up)
            adminConn.Execute(@"
                UPDATE custom_wmo_placements SET map_id = @MapId, committed = 1 WHERE id = @Id",
                new { MapId = p.mapId, Id = dto.PlacementDbId });

            // Rebuild patch-Z.MPQ with ALL committed placements (batch)
            bool built = RebuildPatchMpqForPreset(adminConn, preset);
            if (!built)
            {
                adminConn.Execute("UPDATE custom_wmo_placements SET committed = 0 WHERE id = @Id",
                    new { Id = dto.PlacementDbId });
                return Json(new { success = false, error = "Failed to build patch MPQ" });
            }

            InvalidatePlacementCache();

            string wmoName = (string)(placement.wmoName ?? Path.GetFileNameWithoutExtension((string)placement.wmoPath));
            _logger.LogInformation("CommitToWorld: WMO '{Name}' committed via batch ADT MODF patch", wmoName);

            return Json(new
            {
                success = true,
                method = "adt_modf_patch_batch",
                patchMpqBuilt = true,
                message = "WMO placed in ADT via MODF patch. Download patch-Z.MPQ and restart client."
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CommitToWorld failed for placement {Id}", dto.PlacementDbId);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Rebuild patch-Z.MPQ with all committed placements for the given preset.
    /// Uses batch AddWmoPlacements (parse once, patch once).
    /// </summary>
    private bool RebuildPatchMpqForPreset(System.Data.IDbConnection adminConn, string preset)
    {
        try
        {
            string clientDataPath = GetClientDataDirectory();
            if (string.IsNullOrEmpty(clientDataPath)) return false;

            string patchPath = Path.Combine(clientDataPath, "patch-Z.MPQ");
            string patchBakPath = patchPath + ".bak";

            // Always delete stale patch-Z.MPQ and .bak BEFORE reading the original ADT.
            // ReadFileFromMpqs with skipPatchZ skips it in the load order, but removing
            // the file ensures no other code path accidentally reads it.
            if (System.IO.File.Exists(patchPath))
            {
                System.IO.File.Delete(patchPath);
                _logger.LogInformation("RebuildPatchMpq: Deleted existing patch-Z.MPQ before rebuild");
            }
            if (System.IO.File.Exists(patchBakPath))
            {
                System.IO.File.Delete(patchBakPath);
            }

            var remaining = adminConn.Query(@"
                SELECT id, preset, map_id AS mapId, wmo_path AS wmoPath,
                       mesh_x AS meshX, mesh_y AS meshY, mesh_z AS meshZ,
                       rot_y AS rotY, scale_val AS scaleVal
                FROM custom_wmo_placements
                WHERE preset = @Preset AND committed = 1 ORDER BY id",
                new { Preset = preset }).ToList();

            if (remaining.Count == 0)
            {
                // Already deleted above
                _logger.LogInformation("RebuildPatchMpq: No committed placements remain");
                InvalidatePlacementCache();
                return true;
            }

            if (!TryResolvePreset(preset, out var p, out var error))
            {
                _logger.LogWarning("RebuildPatchMpq: Invalid preset '{Preset}': {Error}", preset, error);
                return false;
            }

            string mapName = AdtPatcherService.GetMapName(p.mapId);
            string adtMpqPath = AdtPatcherService.GetAdtMpqPath(mapName, p.gridX, p.gridY);

            // Read ORIGINAL ADT (skip our patch)
            byte[] originalAdt = ReadOriginalAdtSkippingPatch(clientDataPath, adtMpqPath, patchPath);
            if (originalAdt == null)
            {
                _logger.LogWarning("RebuildPatchMpq: Could not read original ADT");
                return false;
            }

            // Load height data for coordinate transform
            string mapsDir = GetMapsDirectory();
            string mapFilePath = Path.Combine(mapsDir, VmangosMapParser.BuildFilename(p.mapId, p.gridX, p.gridY));
            float globalMidHeight = 0, globalHeightScale = 1;
            if (System.IO.File.Exists(mapFilePath))
            {
                var terrain = VmangosMapParser.Parse(mapFilePath, 8, 8, 8);
                if (terrain != null)
                {
                    globalMidHeight = (terrain.MinHeight + terrain.MaxHeight) / 2f;
                    float range = terrain.MaxHeight - terrain.MinHeight;
                    globalHeightScale = range > 0.01f ? Math.Min(3.5f, 350f / range) : 1f;
                }
            }

            float cellSize = AdtTerrainReader.CELL_SIZE;
            float gridSize = AdtTerrainReader.GRID_SIZE;

            // Build batch placement list
            var placementList = new List<(AdtPatcherService.WmoPlacement placement, uint uniqueId)>();

            foreach (var row in remaining)
            {
                float meshX = (float)row.meshX, meshY = (float)row.meshY, meshZ = (float)row.meshZ;
                float rotY = (float)row.rotY;
                string wmoPath = ((string)row.wmoPath).Replace('/', '\\');

                float modfPosX = (meshX / (128f * cellSize) + 0.5f + p.gridY) * gridSize;
                float modfPosY = meshY / globalHeightScale + globalMidHeight;
                float modfPosZ = (meshZ / (128f * cellSize) + 0.5f + p.gridX) * gridSize;

                // Read WMO bounding box for MODF entry
                float bbExtent = 50f; // default
                var modfPlacement = new AdtPatcherService.WmoPlacement
                {
                    WmoPath = wmoPath,
                    PosX = modfPosX,
                    PosY = modfPosY,
                    PosZ = modfPosZ,
                    RotX = 0,
                    RotY = rotY,
                    RotZ = 0,
                    BbMinX = modfPosX - bbExtent,
                    BbMinY = modfPosY - bbExtent,
                    BbMinZ = modfPosZ - bbExtent,
                    BbMaxX = modfPosX + bbExtent,
                    BbMaxY = modfPosY + bbExtent,
                    BbMaxZ = modfPosZ + bbExtent,
                };

                // Try to read actual WMO bbox from MOHD
                try
                {
                    byte[] wmoBytes = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, wmoPath);
                    if (wmoBytes != null)
                    {
                        var root = WmoReader.ParseRoot(wmoBytes);
                        if (root != null)
                        {
                            modfPlacement.BbMinX = modfPosX + root.BbMinX;
                            modfPlacement.BbMinY = modfPosY + root.BbMinY;
                            modfPlacement.BbMinZ = modfPosZ + root.BbMinZ;
                            modfPlacement.BbMaxX = modfPosX + root.BbMaxX;
                            modfPlacement.BbMaxY = modfPosY + root.BbMaxY;
                            modfPlacement.BbMaxZ = modfPosZ + root.BbMaxZ;
                        }
                    }
                }
                catch { /* use default extent */ }

                uint uniqueId = (uint)(500000 + (int)row.id);
                placementList.Add((modfPlacement, uniqueId));
            }

            // Single-pass batch patch
            byte[] patchedAdt = AdtPatcherService.AddWmoPlacements(originalAdt, placementList);

            _logger.LogInformation(
                "RebuildPatchMpq: Batch patched ADT ({OldSize} → {NewSize} bytes) with {Count} placements",
                originalAdt.Length, patchedAdt.Length, placementList.Count);

            var mpqBuilder = new MpqBuilderService();
            mpqBuilder.AddFile(adtMpqPath, patchedAdt);
            bool built = mpqBuilder.Build(patchPath);

            _logger.LogInformation("RebuildPatchMpq: Built patch-Z.MPQ with {Count} placements", remaining.Count);
            InvalidatePlacementCache();
            return built;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RebuildPatchMpq failed");
            return false;
        }
    }

    /// <summary>
    /// Read the original ADT from base MPQs, skipping our patch-Z.MPQ.
    /// </summary>
    /// <summary>
    /// Read the original ADT from base MPQs, skipping patch-Z.MPQ.
    /// Uses skipPatchZ parameter instead of fragile file rename.
    /// </summary>
    private byte[] ReadOriginalAdtSkippingPatch(string clientDataPath, string adtMpqPath, string patchPath)
    {
        return AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtMpqPath, skipPatchZ: true);
    }

    // DOWNLOAD PATCH MPQ — serve patch-Z.MPQ to client
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Diagnostic: read the patched ADT from patch-Z.MPQ, parse all 256 MCNKs,
    /// and dump MCRF details for any MCNK that has nMapObjRefs > 0.
    /// Also dumps MODF entries for cross-reference.
    /// </summary>
    [HttpGet]
    public IActionResult DiagnoseMcrf()
    {
        try
        {
            string clientDataPath = GetClientDataDirectory();
            if (string.IsNullOrEmpty(clientDataPath))
                return Json(new { error = "No client data path" });

            // Read the patched ADT (WITH patch-Z.MPQ)
            using var adminConn = _db?.Admin();
            adminConn?.Open();
            var lastPlacement = adminConn?.QueryFirstOrDefault(
                "SELECT preset FROM custom_wmo_placements WHERE committed = 1 ORDER BY id DESC LIMIT 1");
            if (lastPlacement == null)
                return Json(new { error = "No committed placements" });

            string preset = (string)lastPlacement.preset;
            if (!TryResolvePreset(preset, out var p, out var error))
                return Json(new { error });

            string mapName = AdtPatcherService.GetMapName(p.mapId);
            string adtMpqPath = AdtPatcherService.GetAdtMpqPath(mapName, p.gridX, p.gridY);

            // Read patched ADT (from patch-Z.MPQ)
            byte[] patchedAdt = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtMpqPath);
            if (patchedAdt == null)
                return Json(new { error = "Could not read patched ADT" });

            // Also read original for comparison
            byte[] originalAdt = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtMpqPath, skipPatchZ: true);

            var parsed = AdtPatcherService.Parse(patchedAdt);
            var diag = new Dictionary<string, object>();
            diag["adtSize"] = patchedAdt.Length;
            diag["originalSize"] = originalAdt?.Length ?? 0;
            diag["modfSize"] = parsed.ModfSize;
            diag["modfEntries"] = parsed.ModfSize / 64;

            // Dump MODF entries
            var modfEntries = new List<object>();
            int modfDataStart = parsed.ModfOffset + 8;
            for (int i = 0; i < parsed.ModfSize / 64; i++)
            {
                int off = modfDataStart + i * 64;
                modfEntries.Add(new
                {
                    index = i,
                    nameId = BitConverter.ToUInt32(patchedAdt, off),
                    uniqueId = BitConverter.ToUInt32(patchedAdt, off + 4),
                    posX = BitConverter.ToSingle(patchedAdt, off + 8),
                    posY = BitConverter.ToSingle(patchedAdt, off + 12),
                    posZ = BitConverter.ToSingle(patchedAdt, off + 16),
                });
            }
            diag["modfEntries_detail"] = modfEntries;

            // Dump MWMO paths
            var mwmoPaths = new List<string>();
            int mwmoDataStart = parsed.MwmoOffset + 8;
            int mwmoEnd = mwmoDataStart + parsed.MwmoSize;
            int spos = mwmoDataStart;
            while (spos < mwmoEnd)
            {
                int send = spos;
                while (send < mwmoEnd && patchedAdt[send] != 0) send++;
                if (send > spos)
                    mwmoPaths.Add($"offset={spos - mwmoDataStart}: {System.Text.Encoding.UTF8.GetString(patchedAdt, spos, send - spos)}");
                spos = send + 1;
            }
            diag["mwmoPaths"] = mwmoPaths;

            // Dump MCNKs with MCRF data
            var mcnkDiags = new List<object>();
            for (int i = 0; i < 256; i++)
            {
                var mcnk = parsed.Mcnks[i];
                int mcnkDataStart = mcnk.AbsoluteOffset + 8;
                int nDoodadRefs = BitConverter.ToInt32(patchedAdt, mcnkDataStart + 0x10);
                int nMapObjRefs = BitConverter.ToInt32(patchedAdt, mcnkDataStart + 0x38);
                int ofsMCRF = BitConverter.ToInt32(patchedAdt, mcnkDataStart + 0x20);

                if (nDoodadRefs > 0 || nMapObjRefs > 0)
                {
                    // Walk IFF chain to find MCRF
                    int subPos = mcnk.AbsoluteOffset + 8 + 128;
                    int mcnkEnd = mcnk.AbsoluteOffset + mcnk.TotalSize;
                    int mcrfFound = -1;
                    int mcrfSize = -1;
                    var mcrfValues = new List<int>();

                    while (subPos + 8 <= mcnkEnd)
                    {
                        uint magic = BitConverter.ToUInt32(patchedAdt, subPos);
                        int subSize = BitConverter.ToInt32(patchedAdt, subPos + 4);
                        if (subSize < 0 || subPos + 8 + subSize > mcnkEnd) break;

                        if (magic == BitConverter.ToUInt32(new byte[] { (byte)'F', (byte)'R', (byte)'C', (byte)'M' }, 0))
                        {
                            mcrfFound = subPos - mcnk.AbsoluteOffset;
                            mcrfSize = subSize;
                            for (int v = 0; v < subSize / 4; v++)
                                mcrfValues.Add(BitConverter.ToInt32(patchedAdt, subPos + 8 + v * 4));
                            break;
                        }
                        subPos += 8 + subSize;
                    }

                    mcnkDiags.Add(new
                    {
                        mcnkIndex = i,
                        indexX = mcnk.IndexX,
                        indexY = mcnk.IndexY,
                        totalSize = mcnk.TotalSize,
                        nDoodadRefs,
                        nMapObjRefs,
                        ofsMCRF,
                        mcrfFoundAtRelOff = mcrfFound,
                        mcrfIffSize = mcrfSize,
                        mcrfValues,
                        ofsMcrfMatchesScan = mcrfFound == ofsMCRF,
                    });
                }
            }
            diag["mcnksWithRefs"] = mcnkDiags;
            diag["mcnksWithRefsCount"] = mcnkDiags.Count;

            return Json(diag);
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message, stack = ex.StackTrace });
        }
    }

    [HttpGet]
    public IActionResult DownloadPatchMpq()
    {
        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return NotFound("Client data path not configured");

        string patchPath = Path.Combine(clientDataPath, "patch-Z.MPQ");
        if (!System.IO.File.Exists(patchPath))
            return NotFound("patch-Z.MPQ not found — commit a placement first");

        var bytes = System.IO.File.ReadAllBytes(patchPath);
        return File(bytes, "application/octet-stream", "patch-Z.MPQ");
    }

    // ═══════════════════════════════════════════════════════════════
    // SINGLE TILE HEIGHTMAP — for progressive loading
    // ═══════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult SingleTileHeightmap(string? preset, int tileGridX = -1, int tileGridY = -1,
        float globalMidHeight = 0, float globalHeightScale = 2.0f)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Json(new { success = false, error });

        int gx = tileGridX >= 0 ? tileGridX : p.gridX;
        int gy = tileGridY >= 0 ? tileGridY : p.gridY;

        string mapsDir = GetMapsDirectory();
        string path = Path.Combine(mapsDir, VmangosMapParser.BuildFilename(p.mapId, gx, gy));
        if (!System.IO.File.Exists(path))
            return Json(new { success = false, error = $"Map file not found for ({gx},{gy})" });

        const int cx = 8, cy = 8, radius = 8;
        var tr = VmangosMapParser.Parse(path, cx, cy, radius);
        if (tr == null) return Json(new { success = false, error = "Parse failed" });

        int vertsW = tr.VertsWidth;
        int vertsH = tr.VertsHeight;
        float[] positions = new float[vertsW * vertsH * 3];

        for (int i = 0; i < vertsW * vertsH; i++)
        {
            // Positions are centered at origin by the parser — leave them that way.
            // Client will offset via mesh.position.
            float origY = tr.Positions[i * 3 + 1];
            float rawHeight = tr.HeightScale > 0
                ? (origY / tr.HeightScale) + ((tr.MinHeight + tr.MaxHeight) * 0.5f)
                : (tr.MinHeight + tr.MaxHeight) * 0.5f;

            positions[i * 3 + 0] = tr.Positions[i * 3 + 0]; // X centered
            positions[i * 3 + 1] = (rawHeight - globalMidHeight) * globalHeightScale;
            positions[i * 3 + 2] = tr.Positions[i * 3 + 2]; // Z centered
        }

        return Json(new
        {
            success = true,
            gridX = gx,
            gridY = gy,
            positions,
            indices = tr.Indices,
            vertsWidth = vertsW,
            vertsHeight = vertsH,
            minHeight = tr.MinHeight,
            maxHeight = tr.MaxHeight
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // SINGLE TILE DOODADS — for progressive loading
    // ═══════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult SingleTileDoodads(string? preset, int tileGridX = -1, int tileGridY = -1,
        float globalMidHeight = 0, float globalHeightScale = 2.0f)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Json(new { success = false, error });

        int gx = tileGridX >= 0 ? tileGridX : p.gridX;
        int gy = tileGridY >= 0 ? tileGridY : p.gridY;

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { success = false, error = "Client data path not configured." });

        try
        {
            var adt = AdtTerrainReader.ReadFromMpq(clientDataPath, MapIdToName(p.mapId), gx, gy);
            if (adt == null) return Json(new { success = false, error = $"ADT not found for ({gx},{gy})" });

            const int cx = 8, cy = 8, radius = 8;
            var doodads = AdtTerrainReader.GetDoodadsForRegion(adt, cx, cy, radius, globalHeightScale, globalMidHeight, gx, gy);

            // WMOs
            var rawWmos = AdtTerrainReader.GetWmosForRegion(adt, globalHeightScale, globalMidHeight);
            int v9StartX = 0;
            int vertsW = 129;
            float offsetX = -((vertsW - 1) * AdtTerrainReader.CELL_SIZE) * 0.5f;
            float offsetZ = offsetX; // symmetric

            var wmos = rawWmos.Select(w =>
            {
                float col = (w.PosX / AdtTerrainReader.GRID_SIZE - gy) * 128;
                float row = (w.PosZ / AdtTerrainReader.GRID_SIZE - gx) * 128;
                return new
                {
                    model = w.ModelPath,
                    x = offsetX + (col - v9StartX) * AdtTerrainReader.CELL_SIZE,
                    y = (w.PosY - globalMidHeight) * globalHeightScale,
                    z = offsetZ + (row - v9StartX) * AdtTerrainReader.CELL_SIZE,
                    rotX = w.RotX,
                    rotY = w.RotY,
                    rotZ = w.RotZ,
                    sizeX = Math.Abs(w.BbMaxX - w.BbMinX) * AdtTerrainReader.CELL_SIZE / AdtTerrainReader.GRID_SIZE * 128,
                    sizeY = Math.Abs(w.BbMaxY - w.BbMinY) * globalHeightScale,
                    sizeZ = Math.Abs(w.BbMaxZ - w.BbMinZ) * AdtTerrainReader.CELL_SIZE / AdtTerrainReader.GRID_SIZE * 128
                };
            }).ToList();

            return Json(new
            {
                success = true,
                gridX = gx,
                gridY = gy,
                doodads = doodads.Select(d => new { model = d.ModelPath, type = d.Type, x = d.X, y = d.Y, z = d.Z, rotY = d.RotY, scale = d.Scale }),
                wmos,
                totalDoodads = doodads.Count,
                totalWmos = wmos.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WorldViewer: SingleTileDoodads failed for ({GX},{GY})", gx, gy);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WATER — liquid planes from .map files
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns water plane data for a single tile.
    /// The liquid data from the .map file is transformed to mesh coordinates
    /// using the same height scale and offset as terrain.
    /// </summary>
    [HttpGet]
    public IActionResult Water(string? preset, int tileGridX = -1, int tileGridY = -1,
        float globalMidHeight = 0, float globalHeightScale = 2.0f)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Json(new { success = false, error });

        int gx = tileGridX >= 0 ? tileGridX : p.gridX;
        int gy = tileGridY >= 0 ? tileGridY : p.gridY;

        string mapsDir = GetMapsDirectory();
        string path = Path.Combine(mapsDir, VmangosMapParser.BuildFilename(p.mapId, gx, gy));
        if (!System.IO.File.Exists(path))
            return Json(new { success = false, error = $"Map file not found for ({gx},{gy})" });

        var liquid = VmangosMapParser.ParseLiquid(path);
        if (liquid == null)
            return Json(new { success = true, hasWater = false });

        // Diagnostic logging
        int flagCount0 = 0, flag0F = 0, flagOther = 0;
        if (liquid.CellFlags != null)
        {
            foreach (var f in liquid.CellFlags)
            {
                if (f == 0) flagCount0++;
                else if (f == 0x0F) flag0F++;
                else flagOther++;
            }
        }
        _logger.LogInformation(
            "Water: tile ({GX},{GY}) — offset=({OX},{OY}) size=({W}x{H}) level={Level:F1} flags=0x{Flags:X4} " +
            "cellFlags: 0x00={F0}, 0x0F={FF}, other={FO}, total={Total}, hasHeights={HH}",
            gx, gy, liquid.OffsetX, liquid.OffsetY, liquid.Width, liquid.Height,
            liquid.LiquidLevel, liquid.LiquidFlags,
            flagCount0, flag0F, flagOther,
            liquid.CellFlags?.Length ?? 0,
            liquid.Heights != null);

        // Convert liquid region to mesh coordinates
        // The tile mesh is centered at origin: total width = 128 * CELL_SIZE
        float totalWidth = 128 * VmangosMapParser.CELL_SIZE;
        float halfWidth = totalWidth * 0.5f;

        // Liquid covers chunks (offsetX..offsetX+width-1, offsetY..offsetY+height-1)
        // Each chunk = 8 cells, each cell = CELL_SIZE
        float x1 = -halfWidth + liquid.OffsetX * 8 * VmangosMapParser.CELL_SIZE;
        float z1 = -halfWidth + liquid.OffsetY * 8 * VmangosMapParser.CELL_SIZE;
        float x2 = x1 + liquid.Width * 8 * VmangosMapParser.CELL_SIZE;
        float z2 = z1 + liquid.Height * 8 * VmangosMapParser.CELL_SIZE;

        // Water height in mesh Y coordinates
        float waterY = (liquid.LiquidLevel - globalMidHeight) * globalHeightScale;

        // If variable heights, transform them too
        float[]? meshHeights = null;
        if (liquid.Heights != null)
        {
            meshHeights = new float[liquid.Heights.Length];
            for (int i = 0; i < liquid.Heights.Length; i++)
            {
                meshHeights[i] = (liquid.Heights[i] - globalMidHeight) * globalHeightScale;
            }
        }

        return Json(new
        {
            success = true,
            hasWater = true,
            gridX = gx,
            gridY = gy,
            x1,
            z1,
            x2,
            z2,
            waterY,
            width = (int)liquid.Width,
            height = (int)liquid.Height,
            heights = meshHeights,
            cellFlags = liquid.CellFlags?.Select(b => (int)b).ToArray(),
            liquidType = (int)liquid.LiquidType
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // NEARBY OBJECTS — spatial streaming for progressive load/unload
    // ═══════════════════════════════════════════════════════════════

    // Cache parsed placement data per tile — avoids re-parsing MPQs every 600ms.
    // Key: "mapId_gx_gy_heightScale_midHeight" → TilePlacementData
    private static readonly Dictionary<string, TilePlacementData?> _placementCache = new(StringComparer.Ordinal);
    private static readonly object _placementCacheLock = new();

    /// <summary>
    /// Clears the static NearbyObjects placement cache so that
    /// subsequent requests re-read ADT data from MPQ.
    /// Call after CommitToWorld, DeletePlacement, or any patch-Z.MPQ change.
    /// </summary>
    private static void InvalidatePlacementCache()
    {
        lock (_placementCacheLock)
        {
            _placementCache.Clear();
        }
    }

    private class PlacementEntry
    {
        public string Id { get; set; } = "";
        public string Model { get; set; } = "";
        public string Type { get; set; } = "";
        public float LocalX { get; set; } // tile-local X (before tile offset)
        public float Y { get; set; }      // already height-scaled
        public float LocalZ { get; set; } // tile-local Z
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float Scale { get; set; }
        public string Kind { get; set; } = "d"; // "d" or "w"
    }

    private class TilePlacementData
    {
        public List<PlacementEntry> Entries { get; set; } = new();
    }

    private TilePlacementData? GetOrBuildPlacements(string clientDataPath, int mapId, int gx, int gy,
        float globalHeightScale, float globalMidHeight)
    {
        // Round scale/mid to avoid cache misses from float precision
        string cacheKey = $"{mapId}_{gx}_{gy}_{globalHeightScale:F4}_{globalMidHeight:F1}";
        lock (_placementCacheLock)
        {
            if (_placementCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var adt = AdtTerrainReader.ReadFromMpq(clientDataPath, MapIdToName(mapId), gx, gy);
        if (adt == null)
        {
            lock (_placementCacheLock) { _placementCache[cacheKey] = null; }
            return null;
        }

        const int cx = 8, cy = 8, radius = 8;
        int vertsW = 129;
        float offsetXZ = -((vertsW - 1) * AdtTerrainReader.CELL_SIZE) * 0.5f;

        var entries = new List<PlacementEntry>();

        // Doodads
        var doodads = AdtTerrainReader.GetDoodadsForRegion(adt, cx, cy, radius, globalHeightScale, globalMidHeight, gx, gy);
        for (int di = 0; di < doodads.Count; di++)
        {
            var d = doodads[di];
            entries.Add(new PlacementEntry
            {
                Id = $"d_{gx}_{gy}_{di}",
                Model = d.ModelPath,
                Type = d.Type,
                LocalX = d.X,
                Y = d.Y,
                LocalZ = d.Z,
                RotY = d.RotY,
                Scale = d.Scale,
                Kind = "d"
            });
        }

        // WMOs
        var rawWmos = AdtTerrainReader.GetWmosForRegion(adt, globalHeightScale, globalMidHeight);
        for (int wi = 0; wi < rawWmos.Count; wi++)
        {
            var w = rawWmos[wi];
            float col = (w.PosX / AdtTerrainReader.GRID_SIZE - gy) * 128;
            float row = (w.PosZ / AdtTerrainReader.GRID_SIZE - gx) * 128;
            entries.Add(new PlacementEntry
            {
                Id = $"w_{gx}_{gy}_{wi}",
                Model = w.ModelPath,
                LocalX = offsetXZ + col * AdtTerrainReader.CELL_SIZE,
                Y = (w.PosY - globalMidHeight) * globalHeightScale,
                LocalZ = offsetXZ + row * AdtTerrainReader.CELL_SIZE,
                RotX = w.RotX,
                RotY = w.RotY,
                RotZ = w.RotZ,
                Kind = "w"
            });
        }

        var data = new TilePlacementData { Entries = entries };
        lock (_placementCacheLock) { _placementCache[cacheKey] = data; }
        return data;
    }

    /// <summary>
    /// Returns doodads and WMOs within a spherical radius of the camera position.
    /// Server returns all placements in range; client skips duplicates it already has.
    /// 
    /// Placement IDs are deterministic: "d_{gx}_{gy}_{idx}" for doodads, "w_{gx}_{gy}_{idx}" for WMOs.
    /// Positions are returned in world space (tile offset already applied).
    /// </summary>
    [HttpGet]
    public IActionResult NearbyObjects(string? preset, float camX = 0, float camZ = 0,
        float loadRadius = 300,
        float globalMidHeight = 0, float globalHeightScale = 2.0f)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Json(new { success = false, error });

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { success = false, error = "Client data path not configured." });

        float tileWidthMesh = 128 * VmangosMapParser.CELL_SIZE;

        int maxTileReach = (int)Math.Ceiling(loadRadius / tileWidthMesh) + 1;
        var addDoodads = new List<object>();
        var addWmos = new List<object>();
        float loadR2 = loadRadius * loadRadius;

        for (int tdy = -maxTileReach; tdy <= maxTileReach; tdy++)
        {
            for (int tdx = -maxTileReach; tdx <= maxTileReach; tdx++)
            {
                int gx = p.gridX + tdy;
                int gy = p.gridY + tdx;
                if (gx < 0 || gx > 63 || gy < 0 || gy > 63) continue;

                float tileOffX = tdx * tileWidthMesh;
                float tileOffZ = tdy * tileWidthMesh;

                // Quick reject: tile center too far
                float distToTile = (float)Math.Sqrt(
                    (camX - tileOffX) * (camX - tileOffX) + (camZ - tileOffZ) * (camZ - tileOffZ));
                float halfDiag = tileWidthMesh * 0.707f;
                if (distToTile > loadRadius + halfDiag) continue;

                var tileData = GetOrBuildPlacements(clientDataPath, p.mapId, gx, gy, globalHeightScale, globalMidHeight);
                if (tileData == null) continue;

                for (int ei = 0; ei < tileData.Entries.Count; ei++)
                {
                    var e = tileData.Entries[ei];

                    float wx = e.LocalX + tileOffX;
                    float wz = e.LocalZ + tileOffZ;
                    float dx = wx - camX;
                    float dz = wz - camZ;
                    if (dx * dx + dz * dz > loadR2) continue;

                    if (e.Kind == "d")
                    {
                        addDoodads.Add(new
                        {
                            id = e.Id,
                            model = e.Model,
                            type = e.Type,
                            x = wx,
                            y = e.Y,
                            z = wz,
                            rotY = e.RotY,
                            scale = e.Scale
                        });
                    }
                    else
                    {
                        addWmos.Add(new
                        {
                            id = e.Id,
                            model = e.Model,
                            x = wx,
                            y = e.Y,
                            z = wz,
                            rotX = e.RotX,
                            rotY = e.RotY,
                            rotZ = e.RotZ
                        });
                    }
                }
            }
        }

        return Json(new
        {
            success = true,
            add = new { doodads = addDoodads, wmos = addWmos },
            addCount = addDoodads.Count + addWmos.Count,
        });
    }


    // ═══════════════════════════════════════════════════════════════
    // TEMPORARY DIAGNOSTIC — Add to WorldViewerController
    // Hit /WorldViewer/ValidatePatchedAdt to inspect the patched ADT
    // ═══════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult ValidatePatchedAdt()
    {
        try
        {
            string clientDataPath = GetClientDataDirectory();
            string patchPath = Path.Combine(clientDataPath, "patch-Z.MPQ");

            if (!System.IO.File.Exists(patchPath))
                return Json(new { error = "patch-Z.MPQ not found" });

            // Read the patched ADT from the MPQ
            byte[] patchedAdt = null;
            string adtPathInMpq = null;

            using var stream = System.IO.File.OpenRead(patchPath);
            using var archive = new War3Net.IO.Mpq.MpqArchive(stream);

            // List all files in the MPQ
            var files = new List<string>();
            if (archive.FileExists("(listfile)"))
            {
                using var lf = archive.OpenFile("(listfile)");
                using var sr = new StreamReader(lf);
                string content = sr.ReadToEnd();
                files = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            }

            // Find the ADT
            foreach (var f in files)
            {
                if (f.EndsWith(".adt", StringComparison.OrdinalIgnoreCase))
                {
                    adtPathInMpq = f;
                    using var entry = archive.OpenFile(f);
                    using var ms = new MemoryStream();
                    entry.CopyTo(ms);
                    patchedAdt = ms.ToArray();
                    break;
                }
            }

            if (patchedAdt == null)
                return Json(new { error = "No ADT found in patch-Z.MPQ", files });

            // Validate the ADT structure
            var diag = new Dictionary<string, object>();
            diag["mpqFiles"] = files;
            diag["adtPath"] = adtPathInMpq;
            diag["adtSize"] = patchedAdt.Length;

            // Check MVER
            string mverMagic = System.Text.Encoding.ASCII.GetString(patchedAdt, 0, 4);
            int mverSize = BitConverter.ToInt32(patchedAdt, 4);
            diag["mverMagic"] = mverMagic;
            diag["mverSize"] = mverSize;
            diag["mverVersion"] = BitConverter.ToInt32(patchedAdt, 8);

            // Check MHDR
            int mhdrOff = 8 + mverSize;
            string mhdrMagic = System.Text.Encoding.ASCII.GetString(patchedAdt, mhdrOff, 4);
            int mhdrSize = BitConverter.ToInt32(patchedAdt, mhdrOff + 4);
            int mhdrData = mhdrOff + 8;
            diag["mhdrMagic"] = mhdrMagic;
            diag["mhdrOffset"] = mhdrOff;
            diag["mhdrSize"] = mhdrSize;

            // Read MHDR offsets (relative to mhdrData)
            var mhdrOffsets = new Dictionary<string, object>();
            string[] mhdrNames = { "flags", "MCIN", "MTEX", "MMDX", "MMID", "MWMO", "MWID", "MDDF", "MODF" };
            for (int i = 0; i < mhdrNames.Length; i++)
            {
                int val = BitConverter.ToInt32(patchedAdt, mhdrData + i * 4);
                int abs = (i == 0) ? val : mhdrData + val; // flags is not an offset
                mhdrOffsets[mhdrNames[i]] = new { relative = val, absolute = abs };

                // Validate: check magic at absolute offset (skip flags)
                if (i > 0 && abs > 0 && abs + 4 <= patchedAdt.Length)
                {
                    string chunkMagic = System.Text.Encoding.ASCII.GetString(patchedAdt, abs, 4);
                    mhdrOffsets[mhdrNames[i] + "_magic"] = chunkMagic;
                }
            }
            diag["mhdrOffsets"] = mhdrOffsets;

            // Find MCIN and validate first few entries
            int mcinRelOff = BitConverter.ToInt32(patchedAdt, mhdrData + 4);
            int mcinAbs = mhdrData + mcinRelOff;
            string mcinMagic = System.Text.Encoding.ASCII.GetString(patchedAdt, mcinAbs, 4);
            int mcinSize2 = BitConverter.ToInt32(patchedAdt, mcinAbs + 4);
            int mcinDataStart = mcinAbs + 8;

            diag["mcinMagic"] = mcinMagic;
            diag["mcinAbs"] = mcinAbs;
            diag["mcinDataSize"] = mcinSize2;

            // Check first 5 MCNK entries from MCIN
            var mcnkChecks = new List<object>();
            for (int i = 0; i < 5; i++)
            {
                int entryBase = mcinDataStart + i * 16;
                int mcnkOff = BitConverter.ToInt32(patchedAdt, entryBase);
                int mcnkSz = BitConverter.ToInt32(patchedAdt, entryBase + 4);

                string magic = "OUT_OF_BOUNDS";
                int iffSize = -1;
                if (mcnkOff >= 0 && mcnkOff + 8 <= patchedAdt.Length)
                {
                    magic = System.Text.Encoding.ASCII.GetString(patchedAdt, mcnkOff, 4);
                    iffSize = BitConverter.ToInt32(patchedAdt, mcnkOff + 4);
                }

                mcnkChecks.Add(new
                {
                    index = i,
                    mcinOffset = mcnkOff,
                    mcinSize = mcnkSz,
                    magicAtOffset = magic,
                    iffSizeAtOffset = iffSize,
                    inBounds = mcnkOff >= 0 && mcnkOff + mcnkSz + 8 <= patchedAdt.Length
                });
            }
            diag["mcnkChecks"] = mcnkChecks;

            // Check MODF
            int modfRelOff = BitConverter.ToInt32(patchedAdt, mhdrData + 32);
            int modfAbs = mhdrData + modfRelOff;
            if (modfAbs + 8 <= patchedAdt.Length)
            {
                string modfMagic = System.Text.Encoding.ASCII.GetString(patchedAdt, modfAbs, 4);
                int modfSize3 = BitConverter.ToInt32(patchedAdt, modfAbs + 4);
                int modfEntryCount = modfSize3 / 64;
                diag["modfMagic"] = modfMagic;
                diag["modfAbs"] = modfAbs;
                diag["modfDataSize"] = modfSize3;
                diag["modfEntryCount"] = modfEntryCount;

                // Check our new entry (last one)
                if (modfEntryCount > 0)
                {
                    int lastEntryOff = modfAbs + 8 + (modfEntryCount - 1) * 64;
                    int nameId = BitConverter.ToInt32(patchedAdt, lastEntryOff);
                    float px = BitConverter.ToSingle(patchedAdt, lastEntryOff + 0x08);
                    float py = BitConverter.ToSingle(patchedAdt, lastEntryOff + 0x0C);
                    float pz = BitConverter.ToSingle(patchedAdt, lastEntryOff + 0x10);
                    diag["lastModfEntry"] = new { nameId, posX = px, posY = py, posZ = pz };
                }
            }

            // Check MWMO for our WMO path
            int mwmoRelOff = BitConverter.ToInt32(patchedAdt, mhdrData + 20);
            int mwmoAbs = mhdrData + mwmoRelOff;
            if (mwmoAbs + 8 <= patchedAdt.Length)
            {
                int mwmoSize3 = BitConverter.ToInt32(patchedAdt, mwmoAbs + 4);
                int mwmoDataStart = mwmoAbs + 8;
                // Read all null-terminated strings
                var paths = new List<string>();
                int pos = mwmoDataStart;
                int end = mwmoDataStart + mwmoSize3;
                while (pos < end)
                {
                    int strEnd = pos;
                    while (strEnd < end && patchedAdt[strEnd] != 0) strEnd++;
                    if (strEnd > pos)
                        paths.Add(System.Text.Encoding.UTF8.GetString(patchedAdt, pos, strEnd - pos));
                    pos = strEnd + 1;
                }
                diag["mwmoPaths"] = paths;
                diag["mwmoDataSize"] = mwmoSize3;
            }

            // Check for large zero blocks
            int zeroRunStart = -1;
            int maxZeroRun = 0;
            int maxZeroRunStart = -1;
            for (int i = 0; i < patchedAdt.Length; i++)
            {
                if (patchedAdt[i] == 0)
                {
                    if (zeroRunStart < 0) zeroRunStart = i;
                }
                else
                {
                    if (zeroRunStart >= 0)
                    {
                        int runLen = i - zeroRunStart;
                        if (runLen > maxZeroRun)
                        {
                            maxZeroRun = runLen;
                            maxZeroRunStart = zeroRunStart;
                        }
                    }
                    zeroRunStart = -1;
                }
            }
            diag["maxZeroRun"] = maxZeroRun;
            diag["maxZeroRunStart"] = maxZeroRunStart;
            diag["maxZeroRunStartHex"] = maxZeroRunStart >= 0 ? $"0x{maxZeroRunStart:X}" : "N/A";

            return Json(diag);
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message, stack = ex.StackTrace });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // TEMPORARY DIAGNOSTIC — Add to WorldViewerController
    // Hit /WorldViewer/DiagnoseAdtPatch to compare original vs patched
    // ═══════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult DiagnoseAdtPatch()
    {
        try
        {
            string clientDataPath = GetClientDataDirectory();

            // Read original ADT from MPQ (same one we'd patch)
            // Use the preset from the last placement, or hardcode for testing
            using var adminConn = _db.Admin();
            adminConn.Open();
            var lastPlacement = adminConn.QueryFirstOrDefault(
                "SELECT preset, map_id, wmo_path FROM custom_wmo_placements ORDER BY id DESC LIMIT 1");

            if (lastPlacement == null)
                return Json(new { error = "No placements found" });

            string preset = (string)lastPlacement.preset;
            if (!TryResolvePreset(preset, out var p, out var error))
                return Json(new { error = "Invalid preset: " + error });

            string mapName = AdtPatcherService.GetMapName(p.mapId);
            string adtMpqPath = AdtPatcherService.GetAdtMpqPath(mapName, p.gridX, p.gridY);
            byte[] original = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtMpqPath);

            if (original == null)
                return Json(new { error = "Could not read original ADT" });

            var diag = new Dictionary<string, object>();
            diag["originalSize"] = original.Length;
            diag["adtPath"] = adtMpqPath;

            // Parse the original
            var parsed = AdtPatcherService.Parse(original);
            diag["parseOk"] = true;
            diag["firstMcnk"] = parsed.FirstMcnkOffset;

            // Check what's in the original at key positions
            diag["orig_mverMagic"] = System.Text.Encoding.ASCII.GetString(original, 0, 4);
            diag["orig_mhdrMagic"] = System.Text.Encoding.ASCII.GetString(original, parsed.MhdrDataOffset - 8, 4);

            // Log original MHDR offsets
            var origMhdr = new Dictionary<string, int>();
            string[] names = { "flags", "MCIN", "MTEX", "MMDX", "MMID", "MWMO", "MWID", "MDDF", "MODF",
                               "MFBO", "MH2O", "MTFX" };
            for (int i = 0; i < 12 && i * 4 < 64; i++)
            {
                origMhdr[names[i]] = BitConverter.ToInt32(original, parsed.MhdrDataOffset + i * 4);
            }
            diag["origMhdrOffsets"] = origMhdr;

            // Log original MCIN first 3 entries
            int mcinData = parsed.McinOffset + 8;
            var origMcin = new List<object>();
            for (int i = 0; i < 3; i++)
            {
                int off = BitConverter.ToInt32(original, mcinData + i * 16);
                int sz = BitConverter.ToInt32(original, mcinData + i * 16 + 4);
                origMcin.Add(new { offset = off, size = sz });
            }
            diag["origMcinFirst3"] = origMcin;

            // Now read the patched ADT from patch-Z.MPQ
            string patchPath = Path.Combine(clientDataPath, "patch-Z.MPQ");
            if (!System.IO.File.Exists(patchPath))
                return Json(new { error = "patch-Z.MPQ not found", diag });

            byte[] patched = null;
            using (var stream = System.IO.File.OpenRead(patchPath))
            using (var archive = new War3Net.IO.Mpq.MpqArchive(stream))
            {
                // Try both slash directions
                string fwd = adtMpqPath.Replace('\\', '/');
                string bck = adtMpqPath;
                string tryPath = archive.FileExists(bck) ? bck : (archive.FileExists(fwd) ? fwd : null);
                if (tryPath != null)
                {
                    using var entry = archive.OpenFile(tryPath);
                    using var ms = new MemoryStream();
                    entry.CopyTo(ms);
                    patched = ms.ToArray();
                }
            }

            if (patched == null)
                return Json(new { error = "Could not read patched ADT from patch-Z.MPQ", diag });

            diag["patchedSize"] = patched.Length;
            diag["sizeDiff"] = patched.Length - original.Length;

            // Compare patched MHDR offsets
            int pMhdrData = parsed.MhdrDataOffset; // MHDR position shouldn't have moved (it's before all splices)
            // Actually — we need to find MHDR in the patched file
            int pMverSize = BitConverter.ToInt32(patched, 4);
            int pMhdrChunk = 8 + pMverSize;
            int pMhdrDataPos = pMhdrChunk + 8;

            var patchMhdr = new Dictionary<string, object>();
            for (int i = 0; i < 12 && i * 4 < 64; i++)
            {
                int relOff = BitConverter.ToInt32(patched, pMhdrDataPos + i * 4);
                int absOff = (i == 0) ? relOff : pMhdrDataPos + relOff;
                string magic = "N/A";
                if (i > 0 && absOff >= 0 && absOff + 4 <= patched.Length)
                    magic = System.Text.Encoding.ASCII.GetString(patched, absOff, 4);
                patchMhdr[names[i]] = new { relative = relOff, absolute = absOff, magic };
            }
            diag["patchedMhdrOffsets"] = patchMhdr;

            // Compare patched MCIN first 3
            int pMcinRel = BitConverter.ToInt32(patched, pMhdrDataPos + 4);
            int pMcinAbs = pMhdrDataPos + pMcinRel;
            int pMcinData = pMcinAbs + 8;

            var patchMcin = new List<object>();
            for (int i = 0; i < 3; i++)
            {
                int off = BitConverter.ToInt32(patched, pMcinData + i * 16);
                int sz = BitConverter.ToInt32(patched, pMcinData + i * 16 + 4);
                string magic = "OOB";
                int iffSz = -1;
                if (off >= 0 && off + 8 <= patched.Length)
                {
                    magic = System.Text.Encoding.ASCII.GetString(patched, off, 4);
                    iffSz = BitConverter.ToInt32(patched, off + 4);
                }
                patchMcin.Add(new { offset = off, mcinSize = sz, magic, iffSize = iffSz });
            }
            diag["patchedMcinFirst3"] = patchMcin;

            // Find first byte difference
            int minLen = Math.Min(original.Length, patched.Length);
            int firstDiff = -1;
            int diffCount = 0;
            for (int i = 0; i < minLen; i++)
            {
                if (original[i] != patched[i])
                {
                    if (firstDiff < 0) firstDiff = i;
                    diffCount++;
                }
            }
            diag["firstDiffOffset"] = firstDiff;
            diag["firstDiffHex"] = firstDiff >= 0 ? $"0x{firstDiff:X}" : "identical";
            diag["totalDiffBytes"] = diffCount + Math.Abs(patched.Length - original.Length);

            // If there's a diff, show what changed
            if (firstDiff >= 0)
            {
                // Show 16 bytes around first diff from both files
                int start = Math.Max(0, firstDiff - 4);
                int len = Math.Min(24, minLen - start);
                diag["origAtDiff"] = BitConverter.ToString(original, start, len);
                diag["patchAtDiff"] = BitConverter.ToString(patched, start, len);

                // What chunk is the first diff in?
                string diffChunk = "unknown";
                if (firstDiff < parsed.McinOffset) diffChunk = "before MCIN (MVER/MHDR)";
                else if (firstDiff < parsed.MtexOffset) diffChunk = "MCIN";
                else if (firstDiff < parsed.MmdxOffset) diffChunk = "MTEX";
                else if (firstDiff < parsed.MmidOffset) diffChunk = "MMDX";
                else if (firstDiff < parsed.MwmoOffset) diffChunk = "MWMO region";
                else if (firstDiff < parsed.MwidOffset) diffChunk = "MWMO/MWID region";
                else if (firstDiff < parsed.MddfOffset) diffChunk = "MWID/MDDF region";
                else if (firstDiff < parsed.ModfOffset) diffChunk = "MDDF/MODF region";
                else if (firstDiff < parsed.FirstMcnkOffset) diffChunk = "MODF or gap before MCNK";
                else diffChunk = "MCNK region";
                diag["firstDiffChunk"] = diffChunk;
            }

            // MODF check — verify our new entry
            int pModfRel = BitConverter.ToInt32(patched, pMhdrDataPos + 32);
            int pModfAbs = pMhdrDataPos + pModfRel;
            if (pModfAbs + 8 <= patched.Length)
            {
                int modfSize = BitConverter.ToInt32(patched, pModfAbs + 4);
                int origModfSize = parsed.ModfSize;
                diag["origModfSize"] = origModfSize;
                diag["patchedModfSize"] = modfSize;
                diag["modfGrew"] = modfSize - origModfSize;
                diag["modfEntries"] = modfSize / 64;
                diag["origModfEntries"] = origModfSize / 64;
            }

            return Json(diag);
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message, stack = ex.StackTrace });
        }
    }


    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════

    private bool TryResolvePreset(string? preset, out (string key, string label, int mapId, int gridX, int gridY) p, out string error)
    {
        p = default; error = "";
        if (string.IsNullOrEmpty(preset)) { error = "Specify a preset."; return false; }

        // Support dynamic preset: "@{mapId}_{gridX}_{gridY}" e.g. "@0_48_32"
        if (preset.StartsWith("@"))
        {
            var parts = preset.Substring(1).Split('_');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out int mapId) &&
                int.TryParse(parts[1], out int gridX) &&
                int.TryParse(parts[2], out int gridY) &&
                mapId >= 0 && gridX >= 0 && gridX <= 63 && gridY >= 0 && gridY <= 63)
            {
                p = (preset, $"{MapIdToName(mapId)} ({gridX},{gridY})", mapId, gridX, gridY);
                return true;
            }
            error = $"Invalid dynamic preset format: {preset}";
            return false;
        }

        var found = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (found.key == null) { error = $"Unknown preset: {preset}"; return false; }
        p = found; return true;
    }

    private static string MapIdToName(int id) => id switch { 0 => "Azeroth", 1 => "Kalimdor", _ => $"Map{id}" };

    private string GetMapsDirectory()
    {
        foreach (var c in new[] { _config?.GetValue<string>("Vmangos:MapsDataPath") ?? "", "/home/wowvmangos/wowclient/maps", "/home/wowvmangos/vmangos/run/data/maps" })
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c;
        return "";
    }

    private string GetClientDataDirectory()
    {
        foreach (var c in new[] { _config?.GetValue<string>("Vmangos:ClientDataPath") ?? "", "/home/wowvmangos/wowclient/Data" })
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c;
        return "";
    }
}