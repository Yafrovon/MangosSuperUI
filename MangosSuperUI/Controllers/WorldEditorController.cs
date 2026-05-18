using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Linq;
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
public class WorldEditorController : Controller
{
    private readonly ILogger<WorldEditorController> _logger;
    private readonly IConfiguration _config;
    private readonly ConnectionFactory? _db;

    public WorldEditorController(IConfiguration config, ILogger<WorldEditorController> logger, ConnectionFactory? db = null)
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
            _logger.LogInformation("WmoModel: {Path} — {Sets} doodad sets, {Defs} doodad defs",
                path, root.DoodadSets.Count, root.Doodads.Count);

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

            int id;
            bool patchRebuilt = false;
            if (dto.Id.HasValue && dto.Id.Value > 0)
            {
                // UPDATE existing placement (drag-move, rotate, etc.)
                conn.Execute(@"
                    UPDATE custom_wmo_placements
                    SET mesh_x = @MeshX, mesh_y = @MeshY, mesh_z = @MeshZ,
                        rot_y = @RotY, scale_val = @Scale
                    WHERE id = @Id",
                    new { dto.MeshX, dto.MeshY, dto.MeshZ, dto.RotY, Scale = dto.Scale, Id = dto.Id.Value });
                id = dto.Id.Value;

                // If this placement is committed, the patch-Z.MPQ has stale
                // MODF coords. Rebuild it so the patched ADT matches the DB.
                var committed = conn.ExecuteScalar<int>(
                    "SELECT committed FROM custom_wmo_placements WHERE id = @Id",
                    new { Id = id });
                if (committed == 1)
                {
                    patchRebuilt = RebuildPatchMpqForPreset(conn, dto.Preset);
                    InvalidatePlacementCache();
                }
            }
            else
            {
                // INSERT new placement
                id = conn.ExecuteScalar<int>(@"
                    INSERT INTO custom_wmo_placements (preset, map_id, wmo_path, wmo_name, mesh_x, mesh_y, mesh_z, rot_y, scale_val)
                    VALUES (@Preset, @MapId, @WmoPath, @WmoName, @MeshX, @MeshY, @MeshZ, @RotY, @Scale);
                    SELECT LAST_INSERT_ID();",
                    new { dto.Preset, MapId = dto.MapId, dto.WmoPath, WmoName = dto.WmoName ?? "", dto.MeshX, dto.MeshY, dto.MeshZ, dto.RotY, Scale = dto.Scale });
            }

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

            // Auto-trigger server data regen if this was a committed placement.
            // Fire-and-forget — delete returns immediately, regen runs in background.
            // The rebuilt dir_bin will exclude the deleted placement, so its collision
            // and pathfinding data get removed from the server.
            bool serverRegenQueued = false;
            if (wasCommitted)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var bgAdminConn = _db.Admin();
                        bgAdminConn.Open();

                        var (activePlacements, affectedTiles) = BuildActivePlacements(bgAdminConn);

                        // Include the tile of the DELETED placement so its collision gets removed
                        if (TryResolvePreset(preset, out var delP, out _))
                        {
                            var deletedTile = (delP.mapId, tileX: delP.gridY, tileY: delP.gridX);
                            if (!affectedTiles.Any(t => t.mapId == deletedTile.mapId
                                && t.tileX == deletedTile.tileX && t.tileY == deletedTile.tileY))
                            {
                                affectedTiles.Add(deletedTile);
                            }
                        }

                        var logger = LoggerFactory.Create(b => b.AddConsole())
                            .CreateLogger<ServerDataService>();
                        var service = new ServerDataService(_config, logger);

                        await service.RegenerateServerData(activePlacements, affectedTiles);
                        _logger.LogInformation(
                            "DeletePlacement: Background server data regen completed for preset '{Preset}'", preset);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "DeletePlacement: Background server data regen failed");
                    }
                });
                serverRegenQueued = true;
            }

            _logger.LogInformation(
                "DeletePlacement: Removed placement {Id}, wasCommitted={Committed}, patchCleaned={Cleaned}, serverRegenQueued={Regen}",
                dto.Id, wasCommitted, patchCleaned, serverRegenQueued);

            return Json(new { success = true, deleted = true, patchCleaned, serverRegenQueued });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeletePlacement failed");
            return Json(new { success = false, error = ex.Message });
        }
    }


    public class WmoPlacementDto
    {
        public int? Id { get; set; }
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
    /// Regenerate server collision/LoS/pathfinding data for a committed placement.
    /// Streams progress via SSE (Server-Sent Events).
    /// Regenerate server collision/LoS/pathfinding data.
    /// Rebuilds dir_bin from vanilla baseline + ALL committed placements in DB,
    /// then runs VMapAssembler + MoveMapGenerator for the specified tile.
    /// </summary>
    [HttpPost]
    public async Task RegenerateServerData([FromBody] RegenerateServerDataDto dto)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        async Task SendEvent(string data)
        {
            await Response.WriteAsync($"data: {data}\n\n");
            await Response.Body.FlushAsync();
        }

        try
        {
            if (_db == null)
            {
                await SendEvent("ERROR: Database not configured");
                return;
            }

            using var adminConn = _db.Admin();
            adminConn.Open();

            // Resolve the triggering placement (for tile coordinates)
            var triggerPlacement = adminConn.QueryFirstOrDefault(@"
                SELECT id, preset, map_id AS mapId
                FROM custom_wmo_placements WHERE id = @Id AND committed = 1",
                new { Id = dto.PlacementDbId });

            if (triggerPlacement == null)
            {
                await SendEvent("ERROR: Committed placement not found");
                return;
            }

            string preset = (string)triggerPlacement.preset;
            if (!TryResolvePreset(preset, out var p, out var error))
            {
                await SendEvent($"ERROR: Invalid preset: {error}");
                return;
            }

            // Tile for MoveMapGenerator — swap gridX/gridY per dir_bin convention
            int triggerTileX = p.gridY;
            int triggerTileY = p.gridX;

            // Build ALL active placements for dir_bin rebuild
            var (activePlacements, _) = BuildActivePlacements(adminConn);

            await SendEvent($"Rebuilding dir_bin with {activePlacements.Count} active placement(s)");

            // Only run MoveMapGenerator for the triggering tile
            var tilesToRegen = new List<(int mapId, int tileX, int tileY)>
            {
                (p.mapId, triggerTileX, triggerTileY)
            };

            var service = new ServerDataService(_config,
                HttpContext.RequestServices.GetRequiredService<ILogger<ServerDataService>>());

            var result = await service.RegenerateServerData(activePlacements, tilesToRegen, async msg =>
            {
                await SendEvent(msg);
            });

            if (result.Success)
            {
                await SendEvent($"DONE: Server data regenerated in {result.ElapsedSeconds:F1}s " +
                    $"({result.VmapsCopied} vmaps, {result.MmapsCopied} mmaps, " +
                    $"{result.PlacementsIncluded} placements in dir_bin)");
            }
            else
            {
                await SendEvent($"ERROR: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RegenerateServerData failed");
            await SendEvent($"ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Diagnostic: inspect dir_bin contents (total records, custom records, vanilla baseline status).
    /// </summary>
    [HttpGet]
    public IActionResult DiagnoseDirBin()
    {
        var service = new ServerDataService(_config,
            HttpContext.RequestServices.GetRequiredService<ILogger<ServerDataService>>());
        return Json(service.DiagnoseDirBin());
    }

    /// <summary>
    /// Returns the current state of vanilla .vanilla backup files across all
    /// configured directories. Used by both the Settings page status panel
    /// and the World Viewer Restore Defaults button to show what can be restored.
    /// </summary>
    [HttpGet]
    public IActionResult BackupStatus()
    {
        try
        {
            var service = new ServerDataService(_config,
                HttpContext.RequestServices.GetRequiredService<ILogger<ServerDataService>>());
            return Json(service.GetBackupStatus());
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // NOTE: CleanDirBin endpoint removed — the rebuild-from-DB approach makes manual cleaning
    // unnecessary. Use RegenerateAllServerData to rebuild everything from scratch.

    /// <summary>
    /// Full regeneration: rebuilds dir_bin from vanilla + ALL committed placements,
    /// then runs VMapAssembler + MoveMapGenerator for EVERY tile that has a placement.
    /// Use after bulk changes or as a "fix everything" button.
    /// </summary>
    [HttpPost]
    public async Task RegenerateAllServerData()
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        async Task SendEvent(string data)
        {
            await Response.WriteAsync($"data: {data}\n\n");
            await Response.Body.FlushAsync();
        }

        try
        {
            if (_db == null)
            {
                await SendEvent("ERROR: Database not configured");
                return;
            }

            using var adminConn = _db.Admin();
            adminConn.Open();

            var (activePlacements, allAffectedTiles) = BuildActivePlacements(adminConn);

            await SendEvent($"Full rebuild: {activePlacements.Count} placement(s), {allAffectedTiles.Count} tile(s)");

            var service = new ServerDataService(_config,
                HttpContext.RequestServices.GetRequiredService<ILogger<ServerDataService>>());

            var result = await service.RegenerateServerData(activePlacements, allAffectedTiles, async msg =>
            {
                await SendEvent(msg);
            });

            if (result.Success)
            {
                await SendEvent($"DONE: Full rebuild in {result.ElapsedSeconds:F1}s " +
                    $"({result.VmapsCopied} vmaps, {result.MmapsCopied} mmaps, " +
                    $"{result.PlacementsIncluded} placements)");
            }
            else
            {
                await SendEvent($"ERROR: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RegenerateAllServerData failed");
            await SendEvent($"ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Restore vanilla defaults: delete all custom placements, restore .vanilla backup files,
    /// delete patch-Z.MPQ. This is the "undo everything" button.
    /// SSE stream for progress.
    /// </summary>
    [HttpPost]
    public async Task RestoreVanillaDefaults()
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        async Task SendEvent(string data)
        {
            await Response.WriteAsync($"data: {data}\n\n");
            await Response.Body.FlushAsync();
        }

        try
        {
            if (_db == null)
            {
                await SendEvent("ERROR: Database not configured");
                return;
            }

            // ── Pre-validate: check configured paths exist ──
            var service = new ServerDataService(_config,
                HttpContext.RequestServices.GetRequiredService<ILogger<ServerDataService>>());

            var backupStatus = service.GetBackupStatus();

            // Check that at least the Buildings directory is accessible
            // (it's the anchor for the whole pipeline)
            string clientDataPath = GetClientDataDirectory();
            if (string.IsNullOrEmpty(clientDataPath))
            {
                await SendEvent("ERROR: Vmangos:ClientDataPath is not configured. " +
                    "Set it in Settings → World Viewer & Server Data → Client Data Path.");
                return;
            }

            // Report what we found
            await SendEvent($"Client data path: {clientDataPath}");
            await SendEvent($"Server vmaps dir: {backupStatus.ServerVmapsDir ?? "(not configured)"}");
            await SendEvent($"Server mmaps dir: {backupStatus.ServerMmapsDir ?? "(not configured)"}");
            await SendEvent($"Vanilla backups found: {backupStatus.TotalBackups} file(s)");

            if (backupStatus.TotalBackups == 0)
            {
                await SendEvent("WARNING: No .vanilla backup files found — " +
                    "server data files cannot be restored to vanilla state. " +
                    "Proceeding with DB cleanup and patch deletion only.");
            }

            // ── Step 1: Delete all custom placements from DB ──
            using var adminConn = _db.Admin();
            adminConn.Open();

            int deletedCount = adminConn.Execute("DELETE FROM custom_wmo_placements");
            await SendEvent($"Deleted {deletedCount} placement(s) from database");

            // ── Step 2: Delete patch-Z.MPQ ──
            if (!string.IsNullOrEmpty(clientDataPath))
            {
                string patchPath = Path.Combine(clientDataPath, "patch-Z.MPQ");
                if (System.IO.File.Exists(patchPath))
                {
                    System.IO.File.Delete(patchPath);
                    await SendEvent("Deleted patch-Z.MPQ");
                }
                string patchBak = patchPath + ".bak";
                if (System.IO.File.Exists(patchBak))
                {
                    System.IO.File.Delete(patchBak);
                }
            }

            // ── Step 3: Restore vanilla server data files ──
            var restoreResult = service.RestoreVanillaServerData(async msg =>
            {
                await SendEvent(msg);
            });
            await SendEvent($"Restored {restoreResult.FilesRestored} vanilla file(s)");

            // ── Step 4: Rebuild dir_bin from vanilla baseline (no custom placements) ──
            // Run regen with empty placement list to restore dir_bin to vanilla
            var emptyPlacements = new List<DirBinPlacement>();
            var emptyTiles = new List<(int mapId, int tileX, int tileY)>();
            var regenResult = await service.RegenerateServerData(emptyPlacements, emptyTiles, async msg =>
            {
                await SendEvent(msg);
            });

            // ── Step 5: Invalidate caches ──
            InvalidatePlacementCache();
            await SendEvent("Invalidated server caches");

            await SendEvent($"DONE: Vanilla defaults restored — {deletedCount} placements removed, " +
                $"{restoreResult.FilesRestored} files restored");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RestoreVanillaDefaults failed");
            await SendEvent($"ERROR: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // TERRAIN SCULPT — Phase 8: save/commit/regen pipeline
    //
    //   SaveSculptData          → DB only (deltas). Fast. No file writes.
    //   CommitSculptedTerrain   → Patches ADT MCVT + builds patch-Z.MPQ + writes .map
    //   RegenerateSculptServerData → Rebuilds vmaps/mmaps for sculpted tiles
    //
    // Mirrors the WMO placement pattern:
    //   save → commit globe → download MPQ → regen server data
    // ═══════════════════════════════════════════════════════════════

    private const string CREATE_SCULPT_TABLE = @"
        CREATE TABLE IF NOT EXISTS custom_terrain_sculpts (
            id           INT AUTO_INCREMENT PRIMARY KEY,
            preset       VARCHAR(64) NOT NULL,
            tile_grid_x  INT NOT NULL,
            tile_grid_y  INT NOT NULL,
            vertex_index INT NOT NULL,
            delta_y      FLOAT NOT NULL,
            committed    TINYINT NOT NULL DEFAULT 0,
            created_at   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            UNIQUE KEY uq_tile_vertex (preset, tile_grid_x, tile_grid_y, vertex_index),
            INDEX idx_preset_tile (preset, tile_grid_x, tile_grid_y)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

    public class SaveSculptDataRequest
    {
        public string Preset { get; set; } = "";
        public int TileGridX { get; set; }
        public int TileGridY { get; set; }
        /// <summary>
        /// Sparse vertex deltas: { "vertexIndex": cumulativeDeltaY, ... }
        /// Only modified vertices are included. Values are mesh-Y space deltas.
        /// </summary>
        public Dictionary<string, float> Deltas { get; set; } = new();
    }

    public class CommitSculptedTerrainRequest
    {
        public string Preset { get; set; } = "";
        public int TileGridX { get; set; }
        public int TileGridY { get; set; }
        public float GlobalMidHeight { get; set; }
        public float GlobalHeightScale { get; set; }
        /// <summary>All vertex mesh-Y values (full tile: 129×129 = 16641 floats).</summary>
        public float[] Heights { get; set; } = Array.Empty<float>();
    }

    /// <summary>
    /// Save sculpt deltas to DB only. No ADT/MPQ/.map changes.
    /// Fast — called frequently during sculpt sessions.
    /// Uses INSERT ON DUPLICATE KEY UPDATE for idempotent upsert.
    /// </summary>
    [HttpPost]
    public IActionResult SaveSculptData([FromBody] SaveSculptDataRequest request)
    {
        if (_db == null) return Json(new { success = false, error = "Database not configured" });

        try
        {
            if (request.Deltas == null || request.Deltas.Count == 0)
                return Json(new { success = false, error = "No delta data" });

            using var conn = _db.Admin();
            conn.Open();
            conn.Execute(CREATE_SCULPT_TABLE);

            // Batch upsert deltas
            int upserted = 0;
            foreach (var kv in request.Deltas)
            {
                if (!int.TryParse(kv.Key, out int vertexIndex)) continue;
                float delta = kv.Value;

                conn.Execute(@"
                    INSERT INTO custom_terrain_sculpts (preset, tile_grid_x, tile_grid_y, vertex_index, delta_y)
                    VALUES (@Preset, @GridX, @GridY, @VertIdx, @Delta)
                    ON DUPLICATE KEY UPDATE delta_y = delta_y + @Delta, updated_at = CURRENT_TIMESTAMP",
                    new
                    {
                        Preset = request.Preset,
                        GridX = request.TileGridX,
                        GridY = request.TileGridY,
                        VertIdx = vertexIndex,
                        Delta = delta
                    });
                upserted++;
            }

            _logger.LogInformation("SaveSculptData: upserted {Count} vertex deltas for tile ({GX},{GY})",
                upserted, request.TileGridX, request.TileGridY);

            return Json(new { success = true, upserted });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveSculptData failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Commit sculpted terrain: patch ADT MCVT, build patch-Z.MPQ, write .map file.
    /// Reads vanilla V9 from the .map.vanilla backup, applies sculpt deltas from DB,
    /// then patches the ADT and builds the MPQ. Does NOT depend on client mesh state.
    /// </summary>
    [HttpPost]
    public IActionResult CommitSculptedTerrain([FromBody] CommitSculptedTerrainRequest request)
    {
        try
        {
            if (!TryResolvePreset(request.Preset, out var p, out var error))
                return Json(new { success = false, error });

            if (_db == null)
                return Json(new { success = false, error = "Database not configured" });

            int gridX = request.TileGridX > 0 ? request.TileGridX : p.gridX;
            int gridY = request.TileGridY > 0 ? request.TileGridY : p.gridY;

            _logger.LogInformation(
                "CommitSculptedTerrain: preset={Preset} tile=({GX},{GY})",
                request.Preset, gridX, gridY);

            // ── Compute global height scale (same as Heightmap endpoint) ──
            // Needed to convert mesh-Y deltas to world-height deltas.
            string mapsDir = GetMapsDirectory();
            float gMin = float.MaxValue, gMax = float.MinValue;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int gx2 = p.gridX + dy, gy2 = p.gridY + dx;
                    string mp2 = Path.Combine(mapsDir, VmangosMapParser.BuildFilename(p.mapId, gx2, gy2));
                    // For height range, use vanilla backup if it exists
                    string vp2 = mp2 + ".vanilla";
                    string sp2 = System.IO.File.Exists(vp2) ? vp2 : mp2;
                    if (!System.IO.File.Exists(sp2)) continue;
                    var tr2 = VmangosMapParser.Parse(sp2, 8, 8, 8);
                    if (tr2 == null) continue;
                    if (tr2.MinHeight < gMin) gMin = tr2.MinHeight;
                    if (tr2.MaxHeight > gMax) gMax = tr2.MaxHeight;
                }
            }
            float globalMid = (gMin + gMax) * 0.5f;
            float globalRange = gMax - gMin;
            float globalScale = globalRange > 0 ? Math.Min(3.5f, 350.0f / globalRange) : 3.5f;

            // ── Load sculpt deltas from DB ──
            using var dbConn = _db.Admin();
            dbConn.Open();
            dbConn.Execute(CREATE_SCULPT_TABLE);

            var deltas = dbConn.Query(
                @"SELECT vertex_index, delta_y FROM custom_terrain_sculpts
                  WHERE preset = @P AND tile_grid_x = @GX AND tile_grid_y = @GY",
                new { P = request.Preset, GX = gridX, GY = gridY }).ToList();

            if (deltas.Count == 0)
            {
                _logger.LogWarning("CommitSculptedTerrain: no sculpt deltas in DB for this tile");
                return Json(new { success = false, error = "No sculpt data found in database for this tile" });
            }

            // Convert mesh-Y deltas to world-height deltas (= MCVT deltas).
            // meshY = (worldH - globalMid) * globalScale
            // so deltaWorldH = deltaMeshY / globalScale
            // MCVT stores worldH - baseHeight, so deltaMCVT = deltaWorldH
            var worldDeltas = new Dictionary<int, float>();
            foreach (var d in deltas)
            {
                int vi = (int)d.vertex_index;
                float deltaInMeshY = (float)d.delta_y;
                float deltaWorld = globalScale != 0 ? deltaInMeshY / globalScale : 0;
                worldDeltas[vi] = deltaWorld;
            }

            _logger.LogInformation(
                "CommitSculptedTerrain: {Count} DB deltas, globalScale={Scale:F4}, globalMid={Mid:F2}, sample delta[0]: meshY={MeshY:F4} → world={World:F4}",
                deltas.Count, globalScale, globalMid,
                deltas.Count > 0 ? (float)deltas[0].delta_y : 0f,
                deltas.Count > 0 ? (worldDeltas.Values.First()) : 0f);

            // ── Read original ADT ──
            string clientDataPath = GetClientDataDirectory();
            if (string.IsNullOrEmpty(clientDataPath))
                return Json(new { success = false, error = "Client data path not configured" });

            string mapName = MapIdToName(p.mapId);
            string adtMpqPath = $"World\\Maps\\{mapName}\\{mapName}_{gridY}_{gridX}.adt";
            byte[]? originalAdt = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtMpqPath, skipPatchZ: true);

            if (originalAdt == null)
                return Json(new { success = false, error = "Could not read original ADT" });

            // ── Patch MCVT directly with world-height deltas ──
            // Instead of replacing all MCVT data from a V9 array, we apply delta offsets
            // to the existing vanilla MCVT values. This preserves the ADT's own coordinate
            // system (MCVT stores heights relative to the MCNK baseHeight).
            byte[] patchedAdt = PatchMcvtDeltas(originalAdt, worldDeltas);

            _logger.LogInformation(
                "CommitSculptedTerrain: patched MCVT with {Count} world-height deltas",
                worldDeltas.Count);

            // ── Also regenerate the .map file for VMaNGOS server (pathfinding/LoS) ──
            // Read vanilla V9, apply deltas, write new .map
            string mapFilename = VmangosMapParser.BuildFilename(p.mapId, gridX, gridY);
            string mapFilePath = Path.Combine(mapsDir, mapFilename);
            string vanillaMapPath = mapFilePath + ".vanilla";
            string sourceMapPath = System.IO.File.Exists(vanillaMapPath) ? vanillaMapPath : mapFilePath;

            float[]? vanillaV9 = ReadRawV9FromMapFile(sourceMapPath);
            if (vanillaV9 != null && vanillaV9.Length == 129 * 129)
            {
                foreach (var kv in worldDeltas)
                {
                    if (kv.Key >= 0 && kv.Key < vanillaV9.Length)
                        vanillaV9[kv.Key] += kv.Value;
                }

                float[] v8 = RecomputeV8FromV9(vanillaV9);
                byte[] mapFileBytes = GenerateMapFile(vanillaV9, v8);

                try
                {
                    BackupVanillaFileStatic(mapFilePath, _logger);
                    System.IO.File.WriteAllBytes(mapFilePath, mapFileBytes);
                    _logger.LogInformation("CommitSculptedTerrain: wrote .map ({Bytes} bytes)", mapFileBytes.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CommitSculptedTerrain: failed to write .map file");
                }
            }

            // ── Apply MODF patches for committed WMO placements on the SAME ADT ──
            try
            {
                var committedWmos = dbConn.Query(@"
                    SELECT id, preset, map_id AS mapId, wmo_path AS wmoPath,
                           mesh_x AS meshX, mesh_y AS meshY, mesh_z AS meshZ,
                           rot_y AS rotY, scale_val AS scaleVal
                    FROM custom_wmo_placements
                    WHERE preset = @Preset AND committed = 1 ORDER BY id",
                    new { Preset = request.Preset }).ToList();

                if (committedWmos.Count > 0)
                {
                    _logger.LogInformation(
                        "CommitSculptedTerrain: applying {Count} committed WMO MODF patches",
                        committedWmos.Count);

                    float cellSz = AdtTerrainReader.CELL_SIZE;
                    float gridSz = AdtTerrainReader.GRID_SIZE;

                    var placementList = new List<(AdtPatcherService.WmoPlacement placement, uint uniqueId)>();

                    foreach (var row in committedWmos)
                    {
                        float meshX = (float)row.meshX, meshY = (float)row.meshY, meshZ = (float)row.meshZ;
                        float rotY = (float)row.rotY;
                        string wmoPath = ((string)row.wmoPath).Replace('/', '\\');

                        float modfPosX = (meshX / (128f * cellSz) + 0.5f + p.gridY) * gridSz;
                        float modfPosY = meshY / globalScale + globalMid;
                        float modfPosZ = (meshZ / (128f * cellSz) + 0.5f + p.gridX) * gridSz;

                        float bbExtent = 50f;
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

                        try
                        {
                            byte[]? wmoBytes = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, wmoPath);
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
                        catch { }

                        uint uniqueId = (uint)(500000 + (int)row.id);
                        placementList.Add((modfPlacement, uniqueId));
                    }

                    patchedAdt = AdtPatcherService.AddWmoPlacements(patchedAdt, placementList);

                    _logger.LogInformation(
                        "CommitSculptedTerrain: MODF patched {Count} WMOs into sculpted ADT ({Size} bytes)",
                        placementList.Count, patchedAdt.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CommitSculptedTerrain: MODF patching failed");
            }

            // ── Build patch-Z.MPQ ──
            try
            {
                string patchPath = Path.Combine(clientDataPath, "patch-Z.MPQ");
                if (System.IO.File.Exists(patchPath)) System.IO.File.Delete(patchPath);
                string patchBak = patchPath + ".bak";
                if (System.IO.File.Exists(patchBak)) System.IO.File.Delete(patchBak);

                var mpqBuilder = new MpqBuilderService();
                mpqBuilder.AddFile(adtMpqPath, patchedAdt);
                mpqBuilder.Build(patchPath);

                _logger.LogInformation("CommitSculptedTerrain: built patch-Z.MPQ ({Bytes} bytes)",
                    new FileInfo(patchPath).Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CommitSculptedTerrain: failed to build patch-Z.MPQ");
            }

            // ── Mark sculpt data as committed in DB ──
            try
            {
                dbConn.Execute(@"
                    UPDATE custom_terrain_sculpts SET committed = 1
                    WHERE preset = @Preset AND tile_grid_x = @GridX AND tile_grid_y = @GridY",
                    new { Preset = request.Preset, GridX = gridX, GridY = gridY });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CommitSculptedTerrain: could not mark sculpt data as committed");
            }

            InvalidatePlacementCache();

            return Json(new { success = true, vertexCount = 129 * 129, deltasApplied = worldDeltas.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CommitSculptedTerrain failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Regenerate server vmaps/mmaps for tiles with committed sculpt data.
    /// SSE stream for progress — same pattern as RegenerateServerData.
    /// </summary>
    [HttpPost]
    public async Task RegenerateSculptServerData(string? preset)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        async Task SendEvent(string data)
        {
            await Response.WriteAsync($"data: {data}\n\n");
            await Response.Body.FlushAsync();
        }

        try
        {
            if (!TryResolvePreset(preset, out var p, out var error))
            {
                await SendEvent($"ERROR: {error}");
                return;
            }

            // Build tile list from preset
            int tileX = p.gridY; // dir_bin swap
            int tileY = p.gridX;

            await SendEvent($"Regenerating server data for tile ({tileX},{tileY})");

            // Include any WMO placements in dir_bin rebuild
            var activePlacements = new List<DirBinPlacement>();
            if (_db != null)
            {
                try
                {
                    using var adminConn = _db.Admin();
                    adminConn.Open();
                    var (placements, _) = BuildActivePlacements(adminConn);
                    activePlacements = placements;
                }
                catch { /* proceed without WMO placements */ }
            }

            var tilesToRegen = new List<(int mapId, int tileX, int tileY)> { (p.mapId, tileX, tileY) };

            var service = new ServerDataService(_config,
                HttpContext.RequestServices.GetRequiredService<ILogger<ServerDataService>>());

            var result = await service.RegenerateServerData(activePlacements, tilesToRegen, async msg =>
            {
                await SendEvent(msg);
            });

            if (result.Success)
                await SendEvent($"DONE: Server data regenerated in {result.ElapsedSeconds:F1}s");
            else
                await SendEvent($"ERROR: {result.Error}");
        }
        catch (Exception ex)
        {
            await SendEvent($"ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Check whether sculpt data exists for a preset (for UI button visibility).
    /// </summary>
    [HttpGet]
    public IActionResult HasSculptData(string? preset)
    {
        if (_db == null) return Json(new { success = true, hasSavedData = false, hasCommitted = false });
        if (string.IsNullOrWhiteSpace(preset)) return Json(new { success = false, error = "preset required" });

        try
        {
            using var conn = _db.Admin();
            conn.Open();
            conn.Execute(CREATE_SCULPT_TABLE);

            int totalRows = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM custom_terrain_sculpts WHERE preset = @Preset",
                new { Preset = preset });
            int committedRows = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM custom_terrain_sculpts WHERE preset = @Preset AND committed = 1",
                new { Preset = preset });

            return Json(new { success = true, hasSavedData = totalRows > 0, hasCommitted = committedRows > 0 });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Restore vanilla terrain: delete sculpt data from DB, restore .map.vanilla,
    /// delete patch-Z.MPQ. The user must reload the preset to see restored terrain.
    /// </summary>
    [HttpPost]
    public IActionResult RestoreSculptedTerrain([FromBody] RestoreSculptPresetDto dto)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dto?.Preset))
                return Json(new { success = false, error = "preset required" });

            if (!TryResolvePreset(dto.Preset, out var p, out var error))
                return Json(new { success = false, error });

            int deletedRows = 0;

            // Delete sculpt data from DB
            if (_db != null)
            {
                using var conn = _db.Admin();
                conn.Open();
                conn.Execute(CREATE_SCULPT_TABLE);
                deletedRows = conn.Execute(
                    "DELETE FROM custom_terrain_sculpts WHERE preset = @Preset",
                    new { Preset = dto.Preset });
                _logger.LogInformation("RestoreSculptedTerrain: deleted {Count} sculpt rows", deletedRows);
            }

            // Restore .map.vanilla → .map
            string mapsDir = GetMapsDirectory();
            if (!string.IsNullOrEmpty(mapsDir))
            {
                string mapFilename = VmangosMapParser.BuildFilename(p.mapId, p.gridX, p.gridY);
                string mapFilePath = Path.Combine(mapsDir, mapFilename);
                string vanillaPath = mapFilePath + ".vanilla";

                if (System.IO.File.Exists(vanillaPath))
                {
                    System.IO.File.Copy(vanillaPath, mapFilePath, overwrite: true);
                    _logger.LogInformation("RestoreSculptedTerrain: restored {File} from .vanilla backup",
                        mapFilename);
                }
            }

            // Delete patch-Z.MPQ (if sculpt was the only thing in it)
            // Check if there are committed WMO placements first
            bool hasWmoCommits = false;
            if (_db != null)
            {
                try
                {
                    using var conn2 = _db.Admin();
                    conn2.Open();
                    hasWmoCommits = conn2.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM custom_wmo_placements WHERE preset = @Preset AND committed = 1",
                        new { Preset = dto.Preset }) > 0;
                }
                catch { /* safe to ignore */ }
            }

            string clientDataPath = GetClientDataDirectory();
            if (!string.IsNullOrEmpty(clientDataPath))
            {
                string patchPath = Path.Combine(clientDataPath, "patch-Z.MPQ");

                if (hasWmoCommits)
                {
                    // WMO placements exist — rebuild patch-Z.MPQ with MODF only (no sculpt)
                    if (_db != null)
                    {
                        using var conn3 = _db.Admin();
                        conn3.Open();
                        RebuildPatchMpqForPreset(conn3, dto.Preset);
                        _logger.LogInformation(
                            "RestoreSculptedTerrain: rebuilt patch-Z.MPQ with WMO placements only");
                    }
                }
                else
                {
                    // No WMO placements — just delete the MPQ
                    if (System.IO.File.Exists(patchPath))
                    {
                        System.IO.File.Delete(patchPath);
                        _logger.LogInformation("RestoreSculptedTerrain: deleted patch-Z.MPQ");
                    }
                }
            }

            InvalidatePlacementCache();

            return Json(new { success = true, deletedRows });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RestoreSculptedTerrain failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    public class RestoreSculptPresetDto
    {
        public string Preset { get; set; } = "";
    }

    /// <summary>
    /// Diagnostic: validate the .map file generator by round-tripping heights
    /// from an existing .map file through GenerateMapFile and comparing.
    /// </summary>
    [HttpGet]
    public IActionResult DiagnoseMapFileGenerator(string? preset)
    {
        try
        {
            if (!TryResolvePreset(preset, out var p, out var error))
                return Json(new { success = false, error });

            string mapsDir = GetMapsDirectory();
            string mapFilename = VmangosMapParser.BuildFilename(p.mapId, p.gridX, p.gridY);
            string mapFilePath = Path.Combine(mapsDir, mapFilename);

            if (!System.IO.File.Exists(mapFilePath))
                return Json(new { success = false, error = $"Map file not found: {mapFilePath}" });

            byte[] originalBytes = System.IO.File.ReadAllBytes(mapFilePath);

            // Read height header
            int heightMapOffset = (int)BitConverter.ToUInt32(originalBytes, 16);
            if (heightMapOffset == 0)
                return Json(new { success = false, error = "No height data in .map" });

            int hPos = heightMapOffset;
            uint flags = BitConverter.ToUInt32(originalBytes, hPos + 4);
            float gridHeight = BitConverter.ToSingle(originalBytes, hPos + 8);
            float gridMaxHeight = BitConverter.ToSingle(originalBytes, hPos + 12);
            hPos += 16;

            bool noHeight = (flags & 0x0001) != 0;
            bool asInt16 = (flags & 0x0002) != 0;
            bool asInt8 = (flags & 0x0004) != 0;

            if (noHeight)
                return Json(new { success = true, message = "Flat tile — no height data to compare" });

            // Read V9 and V8
            float[] v9 = new float[129 * 129];
            float[] v8 = new float[128 * 128];

            if (asInt16)
            {
                float mult = (gridMaxHeight - gridHeight) / 65535.0f;
                for (int i = 0; i < v9.Length; i++) { v9[i] = gridHeight + BitConverter.ToUInt16(originalBytes, hPos) * mult; hPos += 2; }
                for (int i = 0; i < v8.Length; i++) { v8[i] = gridHeight + BitConverter.ToUInt16(originalBytes, hPos) * mult; hPos += 2; }
            }
            else if (asInt8)
            {
                float mult = (gridMaxHeight - gridHeight) / 255.0f;
                for (int i = 0; i < v9.Length; i++) { v9[i] = gridHeight + originalBytes[hPos] * mult; hPos++; }
                for (int i = 0; i < v8.Length; i++) { v8[i] = gridHeight + originalBytes[hPos] * mult; hPos++; }
            }
            else
            {
                for (int i = 0; i < v9.Length; i++) { v9[i] = BitConverter.ToSingle(originalBytes, hPos); hPos += 4; }
                for (int i = 0; i < v8.Length; i++) { v8[i] = BitConverter.ToSingle(originalBytes, hPos); hPos += 4; }
            }

            // Generate and compare
            byte[] generatedBytes = GenerateMapFile(v9, v8);
            var reparse = VmangosMapParser.Parse(generatedBytes, 8, 8, 8);

            int genHPos = (int)BitConverter.ToUInt32(generatedBytes, 16) + 16;
            float maxV9Diff = 0, maxV8Diff = 0;
            int v9Mismatches = 0, v8Mismatches = 0;

            for (int i = 0; i < v9.Length; i++)
            {
                float genVal = BitConverter.ToSingle(generatedBytes, genHPos + i * 4);
                float diff = Math.Abs(genVal - v9[i]);
                if (diff > maxV9Diff) maxV9Diff = diff;
                if (diff > 0.01f) v9Mismatches++;
            }
            for (int i = 0; i < v8.Length; i++)
            {
                float genVal = BitConverter.ToSingle(generatedBytes, genHPos + v9.Length * 4 + i * 4);
                float diff = Math.Abs(genVal - v8[i]);
                if (diff > maxV8Diff) maxV8Diff = diff;
                if (diff > 0.01f) v8Mismatches++;
            }

            return Json(new
            {
                success = true,
                originalFile = new
                {
                    path = mapFilePath,
                    bytes = originalBytes.Length,
                    format = asInt16 ? "int16" : asInt8 ? "int8" : "float32",
                    gridHeight,
                    gridMaxHeight
                },
                generatedFile = new
                {
                    bytes = generatedBytes.Length,
                    format = "float32",
                    reparseOk = reparse != null && reparse.Positions.Length > 0
                },
                comparison = new
                {
                    v9Count = v9.Length,
                    v8Count = v8.Length,
                    maxV9Diff,
                    maxV8Diff,
                    v9Mismatches,
                    v8Mismatches,
                    heightsMatch = v9Mismatches == 0 && v8Mismatches == 0,
                    note = asInt16 || asInt8 ? "Original uses compressed format — small diffs expected from quantization" : "Both float32 — diffs should be zero"
                }
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Diagnostic: read vanilla V9 from .map.vanilla, write into vanilla ADT via PatchMcvtInAdt,
    /// then compare every MCVT float against the original ADT. If non-zero diffs exist, the
    /// V9-to-MCNK axis mapping is wrong.
    /// </summary>
    [HttpGet]
    public IActionResult DiagnoseMcvtRoundtrip(string? preset)
    {
        try
        {
            if (!TryResolvePreset(preset, out var p, out var error))
                return Json(new { success = false, error });

            string mapsDir = GetMapsDirectory();
            string mapFilename = VmangosMapParser.BuildFilename(p.mapId, p.gridX, p.gridY);
            string mapFilePath = Path.Combine(mapsDir, mapFilename);
            string vanillaMapPath = mapFilePath + ".vanilla";
            string sourceMapPath = System.IO.File.Exists(vanillaMapPath) ? vanillaMapPath : mapFilePath;

            // Read raw V9 from .map
            float[]? v9 = ReadRawV9FromMapFile(sourceMapPath);
            if (v9 == null || v9.Length != 129 * 129)
                return Json(new { success = false, error = "Could not read V9 from .map" });

            // Read original ADT
            string clientDataPath = GetClientDataDirectory();
            string mapName = MapIdToName(p.mapId);
            string adtMpqPath = $"World\\Maps\\{mapName}\\{mapName}_{p.gridY}_{p.gridX}.adt";
            byte[]? originalAdt = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtMpqPath, skipPatchZ: true);
            if (originalAdt == null)
                return Json(new { success = false, error = "Could not read original ADT" });

            // Patch MCVT with our V9
            byte[] patchedAdt = PatchMcvtInAdt(originalAdt, v9, p.gridX, p.gridY);

            // Compare every MCVT float between original and patched
            uint MCNK_MAGIC = BitConverter.ToUInt32(new byte[] { (byte)'K', (byte)'N', (byte)'C', (byte)'M' }, 0);
            uint MCVT_MAGIC = BitConverter.ToUInt32(new byte[] { (byte)'T', (byte)'V', (byte)'C', (byte)'M' }, 0);

            var chunkResults = new List<object>();
            int pos = 0;
            int chunkIdx = 0;
            int totalDiffs = 0;
            float maxDiff = 0;

            while (pos + 8 <= originalAdt.Length && chunkIdx < 256)
            {
                uint magic = BitConverter.ToUInt32(originalAdt, pos);
                uint size = BitConverter.ToUInt32(originalAdt, pos + 4);
                int dataStart = pos + 8;
                if (dataStart + size > originalAdt.Length) break;

                if (magic == MCNK_MAGIC)
                {
                    int mcnkDataStart = dataStart;
                    int indexX = (int)BitConverter.ToUInt32(originalAdt, mcnkDataStart + 0x04);
                    int indexY = (int)BitConverter.ToUInt32(originalAdt, mcnkDataStart + 0x08);
                    float baseHeight = BitConverter.ToSingle(originalAdt, mcnkDataStart + 0x6C);

                    uint ofsMcvt = BitConverter.ToUInt32(originalAdt, mcnkDataStart + 0x14);
                    int mcvtStart = pos + (int)ofsMcvt;

                    if (mcvtStart + 8 + 145 * 4 <= originalAdt.Length &&
                        BitConverter.ToUInt32(originalAdt, mcvtStart) == MCVT_MAGIC)
                    {
                        int mcvtDataStart = mcvtStart + 8;
                        int diffs = 0;
                        float chunkMaxDiff = 0;
                        float firstOrigVal = BitConverter.ToSingle(originalAdt, mcvtDataStart);
                        float firstPatchVal = BitConverter.ToSingle(patchedAdt, mcvtDataStart);

                        for (int fi = 0; fi < 145; fi++)
                        {
                            float orig = BitConverter.ToSingle(originalAdt, mcvtDataStart + fi * 4);
                            float patched2 = BitConverter.ToSingle(patchedAdt, mcvtDataStart + fi * 4);
                            float diff = Math.Abs(orig - patched2);
                            if (diff > 0.01f)
                            {
                                diffs++;
                                if (diff > chunkMaxDiff) chunkMaxDiff = diff;
                            }
                        }

                        totalDiffs += diffs;
                        if (chunkMaxDiff > maxDiff) maxDiff = chunkMaxDiff;

                        if (diffs > 0 || chunkIdx < 4) // always show first 4 + any with diffs
                        {
                            chunkResults.Add(new
                            {
                                chunk = chunkIdx,
                                indexX,
                                indexY,
                                baseHeight,
                                diffs,
                                chunkMaxDiff = Math.Round(chunkMaxDiff, 4),
                                firstOrigMcvt = Math.Round(firstOrigVal, 4),
                                firstPatchMcvt = Math.Round(firstPatchVal, 4),
                                // Show what V9 value we used for index 0,0 of this chunk
                                v9AtChunkOrigin = Math.Round(v9[(indexY * 8) * 129 + (indexX * 8)], 4),
                                v9MinusBase = Math.Round(v9[(indexY * 8) * 129 + (indexX * 8)] - baseHeight, 4),
                                // Also try swapped axes for comparison
                                v9Swapped = Math.Round(v9[(indexX * 8) * 129 + (indexY * 8)], 4),
                                v9SwappedMinusBase = Math.Round(v9[(indexX * 8) * 129 + (indexY * 8)] - baseHeight, 4),
                            });
                        }
                    }
                    chunkIdx++;
                }
                pos = dataStart + (int)size;
            }

            return Json(new
            {
                success = true,
                mapFile = System.IO.Path.GetFileName(sourceMapPath),
                v9Range = new { min = Math.Round((double)v9.Min(), 2), max = Math.Round((double)v9.Max(), 2) },
                totalChunks = chunkIdx,
                totalDiffs,
                maxDiff = Math.Round(maxDiff, 4),
                verdict = totalDiffs == 0 ? "PASS — V9 round-trips to identical MCVT" :
                          $"FAIL — {totalDiffs} MCVT floats differ (max {maxDiff:F2})",
                chunks = chunkResults
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ── Phase 8 helper: read raw absolute V9 heights directly from a .map file ──
    // Bypasses VmangosMapParser entirely — no per-tile scale transform to undo.
    // Returns float[129*129] of absolute world-Z heights, or null on failure.

    private float[]? ReadRawV9FromMapFile(string mapFilePath)
    {
        if (!System.IO.File.Exists(mapFilePath)) return null;

        try
        {
            byte[] bytes = System.IO.File.ReadAllBytes(mapFilePath);
            return ReadRawV9FromBytes(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReadRawV9FromMapFile: failed to read {Path}", mapFilePath);
            return null;
        }
    }

    private static float[]? ReadRawV9FromBytes(byte[] bytes)
    {
        if (bytes.Length < 56) return null; // minimum: 40-byte file header + 16-byte height header

        // Validate file magic "MAPS"
        if (bytes[0] != 'M' || bytes[1] != 'A' || bytes[2] != 'P' || bytes[3] != 'S') return null;

        uint heightMapOffset = BitConverter.ToUInt32(bytes, 16);
        if (heightMapOffset == 0 || heightMapOffset + 16 > bytes.Length) return null;

        int hPos = (int)heightMapOffset;

        // Validate height magic "MHGT"
        if (bytes[hPos] != 'M' || bytes[hPos + 1] != 'H' || bytes[hPos + 2] != 'G' || bytes[hPos + 3] != 'T')
            return null;

        uint flags = BitConverter.ToUInt32(bytes, hPos + 4);
        float gridHeight = BitConverter.ToSingle(bytes, hPos + 8);
        float gridMaxHeight = BitConverter.ToSingle(bytes, hPos + 12);
        hPos += 16;

        bool noHeight = (flags & 0x0001) != 0;
        bool asInt16 = (flags & 0x0002) != 0;
        bool asInt8 = (flags & 0x0004) != 0;

        if (noHeight)
        {
            // Flat tile — fill with constant height
            var flat = new float[129 * 129];
            Array.Fill(flat, gridHeight);
            return flat;
        }

        float[] v9 = new float[129 * 129];

        if (asInt16)
        {
            if (hPos + 129 * 129 * 2 > bytes.Length) return null;
            float mult = (gridMaxHeight - gridHeight) / 65535.0f;
            for (int i = 0; i < v9.Length; i++)
            {
                v9[i] = gridHeight + BitConverter.ToUInt16(bytes, hPos) * mult;
                hPos += 2;
            }
        }
        else if (asInt8)
        {
            if (hPos + 129 * 129 > bytes.Length) return null;
            float mult = (gridMaxHeight - gridHeight) / 255.0f;
            for (int i = 0; i < v9.Length; i++)
            {
                v9[i] = gridHeight + bytes[hPos] * mult;
                hPos++;
            }
        }
        else
        {
            // float32
            if (hPos + 129 * 129 * 4 > bytes.Length) return null;
            for (int i = 0; i < v9.Length; i++)
            {
                v9[i] = BitConverter.ToSingle(bytes, hPos);
                hPos += 4;
            }
        }

        return v9;
    }

    // ── Phase 8 helper: apply delta offsets to MCVT floats in an ADT ──
    // Unlike PatchMcvtInAdt (which replaces ALL MCVT data from a V9 array),
    // this method only modifies the specific vertices that have sculpt deltas.
    // Deltas are in world-height space (same units as MCVT offsets).
    //
    // MCVT stores 145 floats per MCNK chunk: 9 rows of (9 outer V9 + 8 inner V8).
    // The sculpt tool only modifies V9 vertices (the outer grid), so we need to
    // map the 129×129 V9 vertex_index to the correct MCNK chunk and MCVT position.
    //
    // V9 vertex_index layout: v9[row * 129 + col] where row/col are 0-128.
    // MCNK chunk at (indexX, indexY) covers V9 rows [indexY*8 .. indexY*8+8],
    //   cols [indexX*8 .. indexX*8+8].
    // Within MCVT, V9 vertices for row r (0-8) are at positions r*17+0 through r*17+8.
    // V8 vertices for row r (0-7) are at positions r*17+9 through r*17+16.

    private byte[] PatchMcvtDeltas(byte[] original, Dictionary<int, float> worldDeltas)
    {
        byte[] patched = new byte[original.Length];
        Array.Copy(original, patched, original.Length);

        uint MCNK_MAGIC = BitConverter.ToUInt32(new byte[] { (byte)'K', (byte)'N', (byte)'C', (byte)'M' }, 0);
        uint MCVT_MAGIC = BitConverter.ToUInt32(new byte[] { (byte)'T', (byte)'V', (byte)'C', (byte)'M' }, 0);

        // Build a lookup: for each V9 vertex_index, which MCNK chunks contain it
        // and at what MCVT float offset within that chunk?
        // A V9 vertex at the boundary between chunks appears in multiple MCNKs.
        // We need to patch all of them.

        int pos = 0;
        int chunkIdx = 0;
        int totalPatched = 0;

        while (pos + 8 <= patched.Length && chunkIdx < 256)
        {
            uint magic = BitConverter.ToUInt32(patched, pos);
            uint size = BitConverter.ToUInt32(patched, pos + 4);
            int dataStart = pos + 8;
            if (dataStart + size > patched.Length) break;

            if (magic == MCNK_MAGIC)
            {
                int mcnkBase = pos;
                int mcnkDataStart = dataStart;

                int indexX = (int)BitConverter.ToUInt32(patched, mcnkDataStart + 0x04);
                int indexY = (int)BitConverter.ToUInt32(patched, mcnkDataStart + 0x08);

                uint ofsMcvt = BitConverter.ToUInt32(patched, mcnkDataStart + 0x14);
                int mcvtStart = mcnkBase + (int)ofsMcvt;

                if (mcvtStart + 8 + 145 * 4 <= patched.Length &&
                    BitConverter.ToUInt32(patched, mcvtStart) == MCVT_MAGIC)
                {
                    int mcvtDataStart = mcvtStart + 8;
                    int v9RowStart = indexY * 8;
                    int v9ColStart = indexX * 8;

                    // Iterate the 9×9 V9 outer vertices in this chunk
                    for (int row = 0; row <= 8; row++)
                    {
                        for (int col = 0; col <= 8; col++)
                        {
                            int v9Row = v9RowStart + row;
                            int v9Col = v9ColStart + col;
                            int v9Index = v9Row * 129 + v9Col;

                            if (worldDeltas.TryGetValue(v9Index, out float delta))
                            {
                                // MCVT layout: each row has 9 outer + 8 inner = 17 floats
                                int mcvtFloatIdx = row * 17 + col;
                                int byteOffset = mcvtDataStart + mcvtFloatIdx * 4;

                                float current = BitConverter.ToSingle(patched, byteOffset);
                                BitConverter.TryWriteBytes(patched.AsSpan(byteOffset, 4), current + delta);
                                totalPatched++;
                            }
                        }
                    }

                    // Also update V8 inner vertices that are affected
                    // V8[r,c] is the center of the quad formed by V9[r,c], V9[r,c+1], V9[r+1,c], V9[r+1,c+1]
                    // If any of those 4 corners changed, recalculate V8 as the average offset
                    for (int row = 0; row < 8; row++)
                    {
                        for (int col = 0; col < 8; col++)
                        {
                            int v9_00 = (v9RowStart + row) * 129 + (v9ColStart + col);
                            int v9_10 = (v9RowStart + row) * 129 + (v9ColStart + col + 1);
                            int v9_01 = (v9RowStart + row + 1) * 129 + (v9ColStart + col);
                            int v9_11 = (v9RowStart + row + 1) * 129 + (v9ColStart + col + 1);

                            bool anyChanged = worldDeltas.ContainsKey(v9_00) ||
                                              worldDeltas.ContainsKey(v9_10) ||
                                              worldDeltas.ContainsKey(v9_01) ||
                                              worldDeltas.ContainsKey(v9_11);

                            if (anyChanged)
                            {
                                // Read the current (already-patched) V9 corner MCVT values
                                float h00 = BitConverter.ToSingle(patched, mcvtDataStart + (row * 17 + col) * 4);
                                float h10 = BitConverter.ToSingle(patched, mcvtDataStart + (row * 17 + col + 1) * 4);
                                float h01 = BitConverter.ToSingle(patched, mcvtDataStart + ((row + 1) * 17 + col) * 4);
                                float h11 = BitConverter.ToSingle(patched, mcvtDataStart + ((row + 1) * 17 + col + 1) * 4);

                                float newV8 = (h00 + h10 + h01 + h11) * 0.25f;
                                int v8McvtIdx = row * 17 + 9 + col;
                                BitConverter.TryWriteBytes(patched.AsSpan(mcvtDataStart + v8McvtIdx * 4, 4), newV8);
                            }
                        }
                    }
                }
                chunkIdx++;
            }
            pos = dataStart + (int)size;
        }

        _logger.LogInformation("PatchMcvtDeltas: patched {Total} V9 vertices across {Chunks} MCNK chunks",
            totalPatched, chunkIdx);
        return patched;
    }

    // ── Phase 8 helper: patch MCVT heights in an ADT byte array (in-place) ──

    private byte[] PatchMcvtInAdt(byte[] original, float[] v9, int gridX, int gridY)
    {
        byte[] patched = new byte[original.Length];
        Array.Copy(original, patched, original.Length);

        // WoW ADT FourCCs are stored reversed on disk: "MCNK" → bytes 'K','N','C','M'
        uint MCNK_MAGIC = BitConverter.ToUInt32(new byte[] { (byte)'K', (byte)'N', (byte)'C', (byte)'M' }, 0);
        uint MCVT_MAGIC = BitConverter.ToUInt32(new byte[] { (byte)'T', (byte)'V', (byte)'C', (byte)'M' }, 0);

        int vertsW = 129, vertsH = 129;
        int pos = 0;
        int chunkIdx = 0;

        while (pos + 8 <= patched.Length && chunkIdx < 256)
        {
            uint magic = BitConverter.ToUInt32(patched, pos);
            uint size = BitConverter.ToUInt32(patched, pos + 4);
            int dataStart = pos + 8;
            if (dataStart + size > patched.Length) break;

            if (magic == MCNK_MAGIC)
            {
                int mcnkBase = pos;
                int mcnkDataStart = dataStart;

                int indexX = (int)BitConverter.ToUInt32(patched, mcnkDataStart + 0x04);
                int indexY = (int)BitConverter.ToUInt32(patched, mcnkDataStart + 0x08);

                // ofsHeight (MCVT) at MCNK header +0x14
                uint ofsMcvt = BitConverter.ToUInt32(patched, mcnkDataStart + 0x14);
                // base height at MCNK header +0x6C (position.Y in the 3-float position at +0x68)
                float baseHeight = BitConverter.ToSingle(patched, mcnkDataStart + 0x6C);

                int mcvtStart = mcnkBase + (int)ofsMcvt;

                if (mcvtStart + 8 + 145 * 4 <= patched.Length &&
                    BitConverter.ToUInt32(patched, mcvtStart) == MCVT_MAGIC)
                {
                    int mcvtDataStart = mcvtStart + 8;

                    int v9RowStart = indexY * 8;
                    int v9ColStart = indexX * 8;

                    int mcvtFloatIdx = 0;
                    for (int row = 0; row <= 8; row++)
                    {
                        // 9 outer vertices (V9)
                        for (int col = 0; col <= 8; col++)
                        {
                            int v9Row = v9RowStart + row;
                            int v9Col = v9ColStart + col;
                            float absH = (v9Row < vertsH && v9Col < vertsW)
                                ? v9[v9Row * vertsW + v9Col] : baseHeight;
                            float mcvtVal = absH - baseHeight;
                            BitConverter.TryWriteBytes(patched.AsSpan(mcvtDataStart + mcvtFloatIdx * 4, 4), mcvtVal);
                            mcvtFloatIdx++;
                        }
                        // 8 inner vertices (V8) — average of surrounding V9
                        if (row < 8)
                        {
                            for (int col = 0; col < 8; col++)
                            {
                                int r = v9RowStart + row, c = v9ColStart + col;
                                float h00 = (r < vertsH && c < vertsW) ? v9[r * vertsW + c] : baseHeight;
                                float h10 = (r < vertsH && c + 1 < vertsW) ? v9[r * vertsW + c + 1] : baseHeight;
                                float h01 = (r + 1 < vertsH && c < vertsW) ? v9[(r + 1) * vertsW + c] : baseHeight;
                                float h11 = (r + 1 < vertsH && c + 1 < vertsW) ? v9[(r + 1) * vertsW + c + 1] : baseHeight;
                                float inner = (h00 + h10 + h01 + h11) * 0.25f;
                                BitConverter.TryWriteBytes(patched.AsSpan(mcvtDataStart + mcvtFloatIdx * 4, 4), inner - baseHeight);
                                mcvtFloatIdx++;
                            }
                        }
                    }
                }
                chunkIdx++;
            }
            pos = dataStart + (int)size;
        }

        _logger.LogInformation("PatchMcvtInAdt: patched {Count} MCNK chunks", chunkIdx);
        return patched;
    }

    // ── Phase 8 helper: recompute V8 center vertices from V9 ──

    private static float[] RecomputeV8FromV9(float[] v9)
    {
        var v8 = new float[128 * 128];
        for (int row = 0; row < 128; row++)
        {
            for (int col = 0; col < 128; col++)
            {
                float h00 = v9[row * 129 + col];
                float h10 = v9[row * 129 + col + 1];
                float h01 = v9[(row + 1) * 129 + col];
                float h11 = v9[(row + 1) * 129 + col + 1];
                v8[row * 128 + col] = (h00 + h10 + h01 + h11) * 0.25f;
            }
        }
        return v8;
    }

    // ── Phase 8 helper: generate VMaNGOS .map file from V9/V8 heights ──

    private static byte[] GenerateMapFile(float[] v9, float[] v8)
    {
        float gridHeight = float.MaxValue, gridMaxHeight = float.MinValue;
        for (int i = 0; i < v9.Length; i++) { if (v9[i] < gridHeight) gridHeight = v9[i]; if (v9[i] > gridMaxHeight) gridMaxHeight = v9[i]; }
        for (int i = 0; i < v8.Length; i++) { if (v8[i] < gridHeight) gridHeight = v8[i]; if (v8[i] > gridMaxHeight) gridMaxHeight = v8[i]; }

        int headerSize = 40, heightHeaderSize = 16;
        int heightDataSize = (129 * 129 + 128 * 128) * 4;

        using var ms = new MemoryStream(headerSize + heightHeaderSize + heightDataSize);
        using var bw = new BinaryWriter(ms);

        // File header (40 bytes)
        bw.Write((byte)'M'); bw.Write((byte)'A'); bw.Write((byte)'P'); bw.Write((byte)'S');
        bw.Write((byte)'z'); bw.Write((byte)'1'); bw.Write((byte)'.'); bw.Write((byte)'4');
        bw.Write((uint)0); bw.Write((uint)0);               // areaMapOffset, areaMapSize
        bw.Write((uint)headerSize);                          // heightMapOffset
        bw.Write((uint)(heightHeaderSize + heightDataSize)); // heightMapSize
        bw.Write((uint)0); bw.Write((uint)0);               // liquidMapOffset, liquidMapSize
        bw.Write((uint)0); bw.Write((uint)0);               // holesOffset, holesSize

        // Height header (16 bytes)
        bw.Write((byte)'M'); bw.Write((byte)'H'); bw.Write((byte)'G'); bw.Write((byte)'T');
        bw.Write((uint)0);      // flags = 0 (float32)
        bw.Write(gridHeight);
        bw.Write(gridMaxHeight);

        // V9 + V8
        for (int i = 0; i < v9.Length; i++) bw.Write(v9[i]);
        for (int i = 0; i < v8.Length; i++) bw.Write(v8[i]);

        return ms.ToArray();
    }

    /// <summary>
    /// Static vanilla backup — mirrors ServerDataService.BackupVanillaFile.
    /// If the file exists and no .vanilla backup exists yet, creates the backup.
    /// Idempotent: second call for the same file is a no-op.
    /// Used by SaveTerrainHeights to protect .map files before overwriting.
    /// </summary>
    private static void BackupVanillaFileStatic(string filePath, ILogger logger)
    {
        if (!System.IO.File.Exists(filePath))
            return;

        string vanillaPath = filePath + ".vanilla";
        if (System.IO.File.Exists(vanillaPath))
            return; // already backed up on a previous save

        try
        {
            System.IO.File.Copy(filePath, vanillaPath);
            logger.LogInformation("SaveTerrainHeights: backed up vanilla {File} ({Size:N0} bytes)",
                Path.GetFileName(filePath), new FileInfo(filePath).Length);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SaveTerrainHeights: could not create vanilla backup at {Path}", vanillaPath);
            // Non-fatal — proceed with the write. The user can always re-extract.
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // REBUILD PATCH MPQ — existing WMO commit pipeline
    // ═══════════════════════════════════════════════════════════════

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

            // Phase 8: If committed sculpt data exists for this tile, apply MCVT
            // patches BEFORE MODF patches. Pipeline: original → sculpt → MODF → MPQ.
            try
            {
                var sculptRows = adminConn.Query(@"
                    SELECT vertex_index AS vertexIndex, delta_y AS deltaY
                    FROM custom_terrain_sculpts
                    WHERE preset = @Preset AND committed = 1",
                    new { Preset = preset }).ToList();

                if (sculptRows.Count > 0)
                {
                    _logger.LogInformation(
                        "RebuildPatchMpq: applying {Count} sculpt vertex deltas before MODF patching",
                        sculptRows.Count);

                    // Compute global height scale to convert mesh-Y deltas to world-height deltas
                    string mapsDir0 = GetMapsDirectory();
                    float sMin = float.MaxValue, sMax = float.MinValue;
                    for (int sdy = -1; sdy <= 1; sdy++)
                    {
                        for (int sdx = -1; sdx <= 1; sdx++)
                        {
                            int sgx = p.gridX + sdy, sgy = p.gridY + sdx;
                            string smp = Path.Combine(mapsDir0, VmangosMapParser.BuildFilename(p.mapId, sgx, sgy));
                            string svp = smp + ".vanilla";
                            string ssp = System.IO.File.Exists(svp) ? svp : smp;
                            if (!System.IO.File.Exists(ssp)) continue;
                            var str = VmangosMapParser.Parse(ssp, 8, 8, 8);
                            if (str == null) continue;
                            if (str.MinHeight < sMin) sMin = str.MinHeight;
                            if (str.MaxHeight > sMax) sMax = str.MaxHeight;
                        }
                    }
                    float sMid = (sMin + sMax) * 0.5f;
                    float sRange = sMax - sMin;
                    float sScale = sRange > 0 ? Math.Min(3.5f, 350.0f / sRange) : 3.5f;

                    // Convert DB deltas to world-height deltas and apply via PatchMcvtDeltas
                    var sculptDeltas = new Dictionary<int, float>();
                    foreach (var sr in sculptRows)
                    {
                        int vi = (int)sr.vertexIndex;
                        float meshDelta = (float)sr.deltaY;
                        sculptDeltas[vi] = sScale != 0 ? meshDelta / sScale : 0;
                    }

                    originalAdt = PatchMcvtDeltas(originalAdt, sculptDeltas);
                    _logger.LogInformation(
                        "RebuildPatchMpq: applied {Count} sculpt MCVT deltas (globalScale={Scale:F4})",
                        sculptDeltas.Count, sScale);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RebuildPatchMpq: sculpt MCVT patching failed — proceeding with MODF only");
            }

            // Load height data for coordinate transform
            // MUST use same multi-tile loop as Heightmap endpoint (tileRadius=1 → 3×3 grid)
            string mapsDir = GetMapsDirectory();
            float globalMin = float.MaxValue, globalMax = float.MinValue;
            int commitTileRadius = 1;
            for (int dy = -commitTileRadius; dy <= commitTileRadius; dy++)
            {
                for (int dx = -commitTileRadius; dx <= commitTileRadius; dx++)
                {
                    int gx = p.gridX + dy;
                    int gy = p.gridY + dx;
                    string mp = Path.Combine(mapsDir, VmangosMapParser.BuildFilename(p.mapId, gx, gy));
                    if (!System.IO.File.Exists(mp)) continue;
                    var tr = VmangosMapParser.Parse(mp, 8, 8, 8);
                    if (tr == null) continue;
                    if (tr.MinHeight < globalMin) globalMin = tr.MinHeight;
                    if (tr.MaxHeight > globalMax) globalMax = tr.MaxHeight;
                }
            }
            float globalMidHeight = (globalMin + globalMax) * 0.5f;
            float globalHeightRange = globalMax - globalMin;
            float globalHeightScale = globalHeightRange > 0 ? Math.Min(3.5f, 350.0f / globalHeightRange) : 3.5f;

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

                // Store MODF coords for server data regeneration
                adminConn.Execute(@"
                     UPDATE custom_wmo_placements
                     SET modf_pos_x = @PosX, modf_pos_y = @PosY, modf_pos_z = @PosZ,
                         modf_bb_min_x = @BbMinX, modf_bb_min_y = @BbMinY, modf_bb_min_z = @BbMinZ,
                         modf_bb_max_x = @BbMaxX, modf_bb_max_y = @BbMaxY, modf_bb_max_z = @BbMaxZ
                     WHERE id = @Id",
                new
                {
                    PosX = modfPlacement.PosX,
                    PosY = modfPlacement.PosY,
                    PosZ = modfPlacement.PosZ,
                    BbMinX = modfPlacement.BbMinX,
                    BbMinY = modfPlacement.BbMinY,
                    BbMinZ = modfPlacement.BbMinZ,
                    BbMaxX = modfPlacement.BbMaxX,
                    BbMaxY = modfPlacement.BbMaxY,
                    BbMaxZ = modfPlacement.BbMaxZ,
                    Id = (int)row.id
                });

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
    /// One-shot MCRF diagnostic. Reads original + patched ADTs, analyzes MCRF structure,
    /// cross-references WMO refs against MODF entries. Returns plain text (not JSON).
    /// Writes same output to /tmp/mcrf_diag.txt for easy retrieval.
    /// </summary>
    [HttpGet]
    public IActionResult DiagnoseMcrf()
    {
        var sb = new System.Text.StringBuilder();
        void W(string s = "") => sb.AppendLine(s);

        try
        {
            string clientDataPath = GetClientDataDirectory();
            if (string.IsNullOrEmpty(clientDataPath))
            { W("ERROR: No client data path"); return Content(sb.ToString(), "text/plain"); }

            System.Data.IDbConnection adminConn = null;
            try { adminConn = _db?.Admin(); adminConn?.Open(); } catch { }

            var lastPlacement = adminConn != null
                ? Dapper.SqlMapper.QueryFirstOrDefault(adminConn,
                    "SELECT preset FROM custom_wmo_placements WHERE committed = 1 ORDER BY id DESC LIMIT 1")
                : null;
            if (lastPlacement == null)
            { W("ERROR: No committed placements"); adminConn?.Dispose(); return Content(sb.ToString(), "text/plain"); }

            string preset = (string)lastPlacement.preset;
            if (!TryResolvePreset(preset, out var p, out var error))
            { W($"ERROR: {error}"); adminConn?.Dispose(); return Content(sb.ToString(), "text/plain"); }

            string mapName = AdtPatcherService.GetMapName(p.mapId);
            string adtMpqPath = AdtPatcherService.GetAdtMpqPath(mapName, p.gridX, p.gridY);

            W("═══ MCRF ANALYSIS ═══");
            W($"Preset: {preset}  Map: {p.mapId}  Grid: ({p.gridX},{p.gridY})");
            W($"ADT: {adtMpqPath}");

            // Read both ADTs
            byte[] orig = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtMpqPath, skipPatchZ: true);
            if (orig == null) { W("ERROR: Could not read original ADT"); adminConn?.Dispose(); return Content(sb.ToString(), "text/plain"); }

            byte[] patched = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtMpqPath);
            bool hasPatch = patched != null && patched.Length != orig.Length;

            W($"Original: {orig.Length} bytes  Patched: {(hasPatch ? $"{patched.Length} bytes (+{patched.Length - orig.Length})" : "none")}");
            W();

            // ── Helper lambdas ──
            int RI(byte[] d, int o) => BitConverter.ToInt32(d, o);
            uint RU(byte[] d, int o) => BitConverter.ToUInt32(d, o);
            float RF(byte[] d, int o) => BitConverter.ToSingle(d, o);
            string RS(byte[] d, int start, int max)
            {
                int end = start;
                int lim = Math.Min(start + max, d.Length);
                while (end < lim && d[end] != 0) end++;
                return end > start ? System.Text.Encoding.ASCII.GetString(d, start, end - start) : "";
            }
            string ReverseM(byte[] d, int o)
            {
                byte[] mb = BitConverter.GetBytes(RU(d, o));
                Array.Reverse(mb);
                return System.Text.Encoding.ASCII.GetString(mb);
            }

            // ── Parse structure ──
            void AnalyzeAdt(byte[] adt, string label)
            {
                W($"═══ {label} ADT ({adt.Length} bytes) ═══");

                int mverSize = RI(adt, 4);
                int mhdrData = 8 + mverSize + 8;

                int mwmoAbs = mhdrData + RI(adt, mhdrData + 20);
                int mwmoSize = RI(adt, mwmoAbs + 4);
                int mwidAbs = mhdrData + RI(adt, mhdrData + 24);
                int mwidSize = RI(adt, mwidAbs + 4);
                int mddfAbs = mhdrData + RI(adt, mhdrData + 28);
                int mddfSize = RI(adt, mddfAbs + 4);
                int modfAbs = mhdrData + RI(adt, mhdrData + 32);
                int modfSize = RI(adt, modfAbs + 4);
                int mcinAbs = mhdrData + RI(adt, mhdrData + 4);

                int modfCount = modfSize / 64;
                int mddfCount = mddfSize / 36;

                // MODF entries
                W($"MODF: {modfCount} entries  MDDF: {mddfCount} entries");
                for (int i = 0; i < modfCount; i++)
                {
                    int off = modfAbs + 8 + i * 64;
                    uint nameId = RU(adt, off);
                    string path = RS(adt, mwmoAbs + 8 + (int)nameId, 512);
                    W($"  MODF[{i}] nameId={nameId} uid={RU(adt, off + 4)} pos=({RF(adt, off + 8):F1},{RF(adt, off + 12):F1},{RF(adt, off + 16):F1}) \"{path}\"");
                }

                // MWMO paths
                W($"MWMO ({mwmoSize} bytes):");
                int sp = mwmoAbs + 8; int mwEnd = sp + mwmoSize;
                while (sp < mwEnd)
                {
                    int se = sp;
                    while (se < mwEnd && adt[se] != 0) se++;
                    if (se > sp) W($"  ofs={sp - mwmoAbs - 8}: \"{System.Text.Encoding.ASCII.GetString(adt, sp, se - sp)}\"");
                    sp = se + 1;
                }

                // Parse MCNKs from MCIN
                int mcinData = mcinAbs + 8;
                int mcnksWithWmo = 0;

                // First pass: count
                for (int i = 0; i < 256; i++)
                {
                    int mcAbs = RI(adt, mcinData + i * 16);
                    int nMO = RI(adt, mcAbs + 8 + 0x38);
                    if (nMO > 0) mcnksWithWmo++;
                }
                W($"MCNKs with nMapObjRefs>0: {mcnksWithWmo}");
                W();

                // Second pass: detailed dump of MCNKs with WMO refs
                for (int i = 0; i < 256; i++)
                {
                    int mcAbs = RI(adt, mcinData + i * 16);
                    int mcIff = RI(adt, mcAbs + 4);
                    int mcData = mcAbs + 8;
                    int ixX = RI(adt, mcData + 4);
                    int ixY = RI(adt, mcData + 8);
                    int nDR = RI(adt, mcData + 0x10);
                    int nMO = RI(adt, mcData + 0x38);
                    int ofsMCRF = RI(adt, mcData + 0x20);

                    if (nMO <= 0) continue;

                    W($"── MCNK[{i}] ({ixX},{ixY}) size={mcIff + 8} nDoodad={nDR} nMapObj={nMO} ofsMCRF=0x{ofsMCRF:X} ──");

                    // What's at ofsMCRF?
                    int mcrfAbs = mcAbs + ofsMCRF;
                    int mcrfDataAbs = mcrfAbs + 8; // skip IFF header
                    if (mcrfAbs + 8 <= adt.Length)
                    {
                        string f4 = System.Text.Encoding.ASCII.GetString(adt, mcrfAbs, 4);
                        bool isMagic = (f4 == "FRCM");
                        int mcrfIffSize = RI(adt, mcrfAbs + 4);
                        int expectedIffSize = (nDR + nMO) * 4;
                        W($"  @ofsMCRF: magic={f4} iffSize={mcrfIffSize} expected={expectedIffSize} match={mcrfIffSize == expectedIffSize}");
                    }

                    // Read MCRF data (AFTER 8-byte IFF header)
                    int expectedSz = (nDR + nMO) * 4;
                    // Doodad refs
                    if (nDR > 0)
                    {
                        var dv = new List<string>();
                        for (int r = 0; r < Math.Min(nDR, 50); r++)
                        {
                            int pos = mcrfDataAbs + r * 4;
                            if (pos + 4 <= adt.Length) dv.Add(RI(adt, pos).ToString());
                        }
                        int maxD = dv.Select(int.Parse).Max();
                        W($"  DoodadRefs[0..{nDR - 1}]: [{string.Join(",", dv)}{(nDR > 50 ? "..." : "")}] max={maxD} valid={maxD < mddfCount}");
                    }

                    // WMO refs
                    int wStart = mcrfDataAbs + nDR * 4;
                    var wv = new List<int>();
                    for (int r = 0; r < nMO; r++)
                    {
                        int pos = wStart + r * 4;
                        if (pos + 4 <= adt.Length) wv.Add(RI(adt, pos));
                    }
                    W($"  WmoRefs: [{string.Join(",", wv)}]");
                    foreach (int val in wv)
                    {
                        bool valid = val >= 0 && val < modfCount;
                        if (valid)
                        {
                            int moff = modfAbs + 8 + val * 64;
                            uint nid = RU(adt, moff);
                            string p2 = RS(adt, mwmoAbs + 8 + (int)nid, 512);
                            W($"    val={val} → MODF[{val}] ✓ \"{p2}\"");
                        }
                        else
                        {
                            W($"    val={val} → INVALID (max index={modfCount - 1})");
                        }
                    }

                    // Context bytes (around MCRF data, not IFF header)
                    if (mcrfDataAbs >= 16 && mcrfDataAbs + expectedSz + 16 <= adt.Length)
                    {
                        W($"  16B before data: {BitConverter.ToString(adt, mcrfDataAbs - 16, 16)}");
                        W($"  MCRF data:       {BitConverter.ToString(adt, mcrfDataAbs, Math.Min(expectedSz, 64))}");
                        W($"  16B after data:  {BitConverter.ToString(adt, mcrfDataAbs + expectedSz, 16)}");
                    }

                    // IFF chain
                    var chain = new List<string>();
                    int sub = mcAbs + 8 + 128;
                    int mcEnd = mcAbs + 8 + mcIff;
                    while (sub + 8 <= mcEnd && chain.Count < 20)
                    {
                        string m = ReverseM(adt, sub);
                        int sz = RI(adt, sub + 4);
                        if (sz < 0 || sub + 8 + sz > mcEnd) break;
                        int rel = sub - mcAbs;
                        chain.Add($"{m}(0x{rel:X},{sz})");
                        if (m == "MCRF") W($"  *** MCRF in IFF chain at 0x{rel:X} size={sz} ***");
                        sub += 8 + sz;
                    }
                    W($"  IFF: {string.Join(" → ", chain)}");
                    W();
                }

                // Also 2 doodad-only samples
                int samp = 0;
                for (int i = 0; i < 256 && samp < 2; i++)
                {
                    int mcAbs = RI(adt, mcinData + i * 16);
                    int mcData = mcAbs + 8;
                    int nDR = RI(adt, mcData + 0x10);
                    int nMO = RI(adt, mcData + 0x38);
                    if (nDR == 0 || nMO > 0) continue;
                    samp++;

                    int ofsMCRF = RI(adt, mcData + 0x20);
                    int mcrfAbs = mcAbs + ofsMCRF;
                    string f4 = mcrfAbs + 4 <= adt.Length ? System.Text.Encoding.ASCII.GetString(adt, mcrfAbs, 4) : "?";
                    bool isMag = f4 == "FRCM";
                    int mcrfIffSz = mcrfAbs + 8 <= adt.Length ? RI(adt, mcrfAbs + 4) : -1;

                    var chain = new List<string>();
                    int sub = mcAbs + 8 + 128;
                    int mcEnd = mcAbs + 8 + RI(adt, mcAbs + 4);
                    while (sub + 8 <= mcEnd && chain.Count < 20)
                    {
                        string m = ReverseM(adt, sub);
                        int sz = RI(adt, sub + 4);
                        if (sz < 0 || sub + 8 + sz > mcEnd) break;
                        chain.Add($"{m}(0x{(sub - mcAbs):X},{sz})");
                        sub += 8 + sz;
                    }
                    W($"Sample doodad-only MCNK[{i}] nD={nDR} ofsMCRF=0x{ofsMCRF:X} magic={f4} iffSize={mcrfIffSz} expected={nDR * 4}");
                    W($"  IFF: {string.Join(" → ", chain)}");
                }
                W();
            }

            AnalyzeAdt(orig, "ORIGINAL");
            if (hasPatch) AnalyzeAdt(patched, "PATCHED");

            adminConn?.Dispose();
        }
        catch (Exception ex)
        {
            W($"EXCEPTION: {ex.Message}");
            W(ex.StackTrace);
        }

        string output = sb.ToString();

        // Write to file for easy retrieval
        try { System.IO.File.WriteAllText("/tmp/mcrf_diag.txt", output); }
        catch { /* ignore write failure */ }

        return Content(output, "text/plain");
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
    /// Reads MCLQ liquid sub-chunks directly from the ADT in the WoW client MPQs.
    /// This is the vanilla 1.x source of truth — same data Noggit and the WoW
    /// client itself consume. VMaNGOS .map files are NOT used for water (they
    /// only contain a coarse pathfinding summary).
    ///
    /// Pipeline per chunk that has MCLQ data:
    ///   - 9×9 vertex height grid (heights in WoW world Z)
    ///   - 8×8 tile-render mask (per-tile dont_render bit)
    ///   - Liquid type code (water/ocean/slime/magma)
    /// We emit one quad per rendered tile, using the four surrounding vertex
    /// heights from the 9×9 grid (matching Noggit's mesh).
    /// </summary>
    [HttpGet]
    public IActionResult Water(string? preset, int tileGridX = -1, int tileGridY = -1,
        float globalMidHeight = 0, float globalHeightScale = 2.0f)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Json(new { success = false, error });

        int gx = tileGridX >= 0 ? tileGridX : p.gridX;
        int gy = tileGridY >= 0 ? tileGridY : p.gridY;

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { success = false, error = "Client data path not configured." });

        var adt = AdtTerrainReader.ReadFromMpq(clientDataPath, MapIdToName(p.mapId), gx, gy);
        if (adt == null || adt.Chunks == null)
            return Json(new { success = true, hasWater = false });

        // Tile is 128 cells per side, centered at origin. Cell column cx →
        // mesh-X (cx - 64)*CELL_SIZE; cell row cy → mesh-Z (cy - 64)*CELL_SIZE.
        // (Matches AdtTerrainReader.GetDoodadsForRegion / VmangosMapParser mesh.)
        float cellSize = AdtTerrainReader.CELL_SIZE;
        const int CHUNK_TILES = 8;
        const int VERTS_PER_CHUNK_SIDE = 9;

        var quadPositions = new List<float>();
        var quadIndices = new List<int>();
        int vertexBase = 0;
        int chunksWithWater = 0;
        int totalLayers = 0;
        int emittedQuads = 0;
        byte firstLiquidType = 0;

        foreach (var chunk in adt.Chunks)
        {
            if (chunk == null || chunk.Liquid == null || chunk.Liquid.Count == 0) continue;
            chunksWithWater++;
            totalLayers += chunk.Liquid.Count;

            int chunkCellX = chunk.IndexX * CHUNK_TILES; // 0..120, column origin in 0..128 cell grid
            int chunkCellY = chunk.IndexY * CHUNK_TILES; // row origin

            foreach (var layer in chunk.Liquid)
            {
                if (layer.VertexHeights.Length != 81 || layer.TileRender.Length != 64) continue;
                if (firstLiquidType == 0 && layer.LiquidType != 0) firstLiquidType = layer.LiquidType;

                for (int tz = 0; tz < CHUNK_TILES; tz++)
                {
                    for (int tx = 0; tx < CHUNK_TILES; tx++)
                    {
                        if (!layer.TileRender[tz * CHUNK_TILES + tx]) continue;

                        // 4 vertex heights (NW, NE, SW, SE) from the 9×9 grid.
                        float hNW = layer.VertexHeights[tz * VERTS_PER_CHUNK_SIDE + tx];
                        float hNE = layer.VertexHeights[tz * VERTS_PER_CHUNK_SIDE + tx + 1];
                        float hSW = layer.VertexHeights[(tz + 1) * VERTS_PER_CHUNK_SIDE + tx];
                        float hSE = layer.VertexHeights[(tz + 1) * VERTS_PER_CHUNK_SIDE + tx + 1];

                        // Clamp into [min, max] like Noggit does — protects against
                        // garbage data on unused vertices.
                        float minH = layer.MinHeight, maxH = layer.MaxHeight;
                        if (minH > maxH) { float t = minH; minH = maxH; maxH = t; }
                        hNW = Math.Clamp(hNW, minH, maxH);
                        hNE = Math.Clamp(hNE, minH, maxH);
                        hSW = Math.Clamp(hSW, minH, maxH);
                        hSE = Math.Clamp(hSE, minH, maxH);

                        // Transform to mesh-Y
                        float yNW = (hNW - globalMidHeight) * globalHeightScale;
                        float yNE = (hNE - globalMidHeight) * globalHeightScale;
                        float ySW = (hSW - globalMidHeight) * globalHeightScale;
                        float ySE = (hSE - globalMidHeight) * globalHeightScale;

                        // Tile cell coords in 0..128 grid
                        int cellX = chunkCellX + tx;
                        int cellY = chunkCellY + tz;

                        float mx0 = (cellX - 64f) * cellSize;
                        float mx1 = (cellX + 1 - 64f) * cellSize;
                        float mz0 = (cellY - 64f) * cellSize;
                        float mz1 = (cellY + 1 - 64f) * cellSize;

                        quadPositions.Add(mx0); quadPositions.Add(yNW); quadPositions.Add(mz0); // NW
                        quadPositions.Add(mx1); quadPositions.Add(yNE); quadPositions.Add(mz0); // NE
                        quadPositions.Add(mx0); quadPositions.Add(ySW); quadPositions.Add(mz1); // SW
                        quadPositions.Add(mx1); quadPositions.Add(ySE); quadPositions.Add(mz1); // SE

                        quadIndices.Add(vertexBase); quadIndices.Add(vertexBase + 2); quadIndices.Add(vertexBase + 1);
                        quadIndices.Add(vertexBase + 1); quadIndices.Add(vertexBase + 2); quadIndices.Add(vertexBase + 3);
                        vertexBase += 4;
                        emittedQuads++;
                    }
                }
            }
        }

        _logger.LogInformation(
            "Water: tile ({GX},{GY}) ADT chunks={Total} withWater={WW} layers={Layers} → emittedQuads={EQ} firstLiquidType={LT}",
            gx, gy, adt.Chunks.Length, chunksWithWater, totalLayers, emittedQuads, firstLiquidType);

        // ─────────────────────────────────────────────────────────────
        // WMO MLIQ pass — water that lives INSIDE WMO group files,
        // not in ADT MCLQ. Stormwind canals, Undercity slime pools,
        // Darnassus temple pools, IronForge lava etc. all live here.
        //
        // Per-quad world-XZ membership in this tile is checked at emit
        // time. A WMO whose footprint straddles multiple ADT tiles
        // contributes its quads to whichever tiles their world-XZ
        // actually fall into — no central "owner tile" assumption.
        // ─────────────────────────────────────────────────────────────
        int wmoQuadsEmitted = 0;
        int wmoGroupsHit = 0;
        int wmoInstancesWithLiquid = 0;
        if (adt.Wmos != null)
        {
            float gridSize = AdtTerrainReader.GRID_SIZE;
            const float WMO_LIQ_UNIT = 33.3333f / 8.0f; // matches WmoReader.WMO_LIQUID_UNIT
            foreach (var wmoInst in adt.Wmos)
            {
                if (string.IsNullOrEmpty(wmoInst.ModelPath)) continue;

                var wmoRoot = GetWmoRootCached(clientDataPath, wmoInst.ModelPath);
                if (wmoRoot == null) continue;

                // Build the same MODF rotation quaternion used by the doodad pass
                // (ComposeWmoDoodadsIntoTile lines ~3787-3796): yaw = 270 + RotY,
                // YXZ composition with pitch (RotX) and roll (RotZ).
                double pitchRad = wmoInst.RotX * Math.PI / 180.0;
                double yawRad = (270.0 + wmoInst.RotY) * Math.PI / 180.0;
                double rollRad = wmoInst.RotZ * Math.PI / 180.0;
                var qWmoYaw = QuatFromAxisAngle(0, 1, 0, yawRad);
                var qWmoPitch = QuatFromAxisAngle(1, 0, 0, pitchRad);
                var qWmoRoll = QuatFromAxisAngle(0, 0, 1, rollRad);
                var qWmo = QuatMul(QuatMul(qWmoYaw, qWmoPitch), qWmoRoll);

                bool instanceHadLiquid = false;
                for (int gi = 0; gi < (int)wmoRoot.NGroups; gi++)
                {
                    var grp = GetWmoGroupCached(clientDataPath, wmoInst.ModelPath, gi);
                    if (grp == null) continue;
                    var liq = grp.Liquid;
                    if (liq == null) continue;
                    if (liq.VertexHeights.Length != liq.XVerts * liq.YVerts) continue;
                    if (liq.TileFlags.Length != liq.XTiles * liq.YTiles) continue;
                    wmoGroupsHit++;
                    instanceHadLiquid = true;

                    // Per-vertex transform from MLIQ local (Z-up file frame) to mesh space.
                    // Inputs: (i, j) tile-grid corner indices, height from vertex array.
                    //   1. WMO Z-up file space:
                    //        vx_zup = CornerX + i * UNIT
                    //        vy_zup = CornerY + j * UNIT
                    //        vz_zup = height
                    //   2. Geometry's Z-up → Y-up convention (matches WmoModel endpoint
                    //      vertex transform — line 587-594):  (x,y,z) → (y,z,x)
                    //        vx_yup_local = vy_zup
                    //        vy_yup_local = vz_zup
                    //        vz_yup_local = vx_zup
                    //   3. Apply qWmo (MODF rotation in Y-up world frame).
                    //   4. Translate by MODF position (already Y-up world).
                    //   5. World XZ → tile-local cell coord:
                    //        col = (worldX / GRID_SIZE - gy) * 128
                    //        row = (worldZ / GRID_SIZE - gx) * 128
                    //      → meshX = (col - 64) * CELL_SIZE
                    //      → meshZ = (row - 64) * CELL_SIZE
                    //      → meshY = (worldY - globalMidHeight) * globalHeightScale
                    (float mx, float my, float mz, float wx, float wz) Project(int i, int j)
                    {
                        if (j * liq.XVerts + i >= liq.VertexHeights.Length)
                            return (0, 0, 0, 0, 0);
                        float vx_zup = liq.CornerX + i * WMO_LIQ_UNIT;
                        float vy_zup = liq.CornerY + j * WMO_LIQ_UNIT;
                        float vz_zup = liq.VertexHeights[j * liq.XVerts + i];

                        // Z-up file → Y-up local (same as MOVT vertex transform)
                        double lx = vy_zup;
                        double ly = vz_zup;
                        double lz = vx_zup;

                        // Rotate by qWmo
                        var (rx, ry, rz) = QuatRotateVec(qWmo, lx, ly, lz);

                        // Translate to world (Y-up)
                        double wxd = wmoInst.PosX + rx;
                        double wyd = wmoInst.PosY + ry;
                        double wzd = wmoInst.PosZ + rz;

                        // World XZ → tile-local cell coord (matches AdtTerrainReader doodad path
                        // and the existing MCLQ pipeline above):
                        double col = (wxd / gridSize - gy) * 128.0;
                        double row = (wzd / gridSize - gx) * 128.0;
                        float mX = (float)((col - 64.0) * cellSize);
                        float mZ = (float)((row - 64.0) * cellSize);
                        float mY = (float)((wyd - globalMidHeight) * globalHeightScale);

                        return (mX, mY, mZ, (float)wxd, (float)wzd);
                    }

                    for (int j = 0; j < liq.YTiles; j++)
                    {
                        for (int i = 0; i < liq.XTiles; i++)
                        {
                            byte tflag = liq.TileFlags[j * liq.XTiles + i];
                            if ((tflag & 0x08) != 0) continue; // dont_render

                            // Project the 4 corners
                            var p00 = Project(i, j);
                            var p10 = Project(i + 1, j);
                            var p01 = Project(i, j + 1);
                            var p11 = Project(i + 1, j + 1);

                            // Per-quad tile membership: which ADT tile does the quad's
                            // world-XZ center belong to? If it's not our (gx,gy), skip.
                            float cx = 0.25f * (p00.wx + p10.wx + p01.wx + p11.wx);
                            float cz = 0.25f * (p00.wz + p10.wz + p01.wz + p11.wz);
                            int quadTileGy = (int)Math.Floor(cx / gridSize); // column → gridY
                            int quadTileGx = (int)Math.Floor(cz / gridSize); // row    → gridX
                            if (quadTileGx != gx || quadTileGy != gy) continue;

                            // Emit quad (same winding as ADT MCLQ pass above)
                            // NW = (i,j),   NE = (i+1,j)
                            // SW = (i,j+1), SE = (i+1,j+1)
                            quadPositions.Add(p00.mx); quadPositions.Add(p00.my); quadPositions.Add(p00.mz);
                            quadPositions.Add(p10.mx); quadPositions.Add(p10.my); quadPositions.Add(p10.mz);
                            quadPositions.Add(p01.mx); quadPositions.Add(p01.my); quadPositions.Add(p01.mz);
                            quadPositions.Add(p11.mx); quadPositions.Add(p11.my); quadPositions.Add(p11.mz);

                            quadIndices.Add(vertexBase); quadIndices.Add(vertexBase + 2); quadIndices.Add(vertexBase + 1);
                            quadIndices.Add(vertexBase + 1); quadIndices.Add(vertexBase + 2); quadIndices.Add(vertexBase + 3);
                            vertexBase += 4;
                            wmoQuadsEmitted++;
                            emittedQuads++;

                            // If we don't yet have a liquid type code, derive one from
                            // this tile's legacy-type bits (0..2). 1=ocean, 3=slime,
                            // 4=river, 6=magma per Noggit.
                            if (firstLiquidType == 0)
                            {
                                byte t = (byte)(tflag & 0x07);
                                if (t != 0) firstLiquidType = t;
                            }
                        }
                    }
                }
                if (instanceHadLiquid) wmoInstancesWithLiquid++;
            }
        }

        _logger.LogInformation(
            "Water: tile ({GX},{GY}) WMO instances={Instances} (withLiquid={WL}) groupsHit={GH} → wmoQuads={WQ}",
            gx, gy, adt.Wmos?.Count ?? 0, wmoInstancesWithLiquid, wmoGroupsHit, wmoQuadsEmitted);

        if (emittedQuads == 0)
            return Json(new { success = true, hasWater = false });

        return Json(new
        {
            success = true,
            hasWater = true,
            gridX = gx,
            gridY = gy,
            positions = quadPositions.ToArray(),
            indices = quadIndices.ToArray(),
            cellsEmitted = emittedQuads,
            liquidType = (int)firstLiquidType
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
        public string Kind { get; set; } = "d"; // "d", "w", or "wd"

        // For WMO-embedded doodads (Kind = "wd"): the final orientation as
        // a Y-up quaternion. Rotation is composed server-side from the
        // WMO's MODF Euler and the MODD quaternion, then basis-changed to
        // Y-up so the client applies it directly without further conversion.
        public float QuatX { get; set; }
        public float QuatY { get; set; }
        public float QuatZ { get; set; }
        public float QuatW { get; set; } = 1.0f;
    }

    private class TilePlacementData
    {
        public List<PlacementEntry> Entries { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────
    // WMO doodad cache — per-WMO-root parsed MODS/MODN/MODD.
    // Stormwind has 6,026 MODD defs in a single WMO; we don't want to
    // re-parse the root MPQ for every MODF instance that references it.
    // ─────────────────────────────────────────────────────────────────
    private static readonly Dictionary<string, WmoRootData?> _wmoDoodadCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _wmoDoodadCacheLock = new();

    // ─────────────────────────────────────────────────────────────────
    // WMO group cache — per-group parsed MLIQ + geometry data.
    // Keyed by "rootPath::groupIndex" so SW's 50 groups parse once per process.
    // We cache nulls too (group missing / no parseable content) to avoid
    // hammering the MPQ on each Water request.
    // ─────────────────────────────────────────────────────────────────
    private static readonly Dictionary<string, WmoGroupData?> _wmoGroupCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _wmoGroupCacheLock = new();

    private WmoGroupData? GetWmoGroupCached(string clientDataPath, string rootPath, int groupIndex)
    {
        string cacheKey = $"{rootPath.Replace('/', '\\').ToLowerInvariant()}::{groupIndex:D3}";
        lock (_wmoGroupCacheLock)
        {
            if (_wmoGroupCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        // Group filename: {root_without_.wmo}_{NNN}.wmo (mirrors WmoModel endpoint logic).
        string basePathBS = rootPath.Replace('/', '\\');
        if (basePathBS.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
            basePathBS = basePathBS.Substring(0, basePathBS.Length - 4);
        string basePathFS = basePathBS.Replace('\\', '/');

        string groupPathBS = $"{basePathBS}_{groupIndex:D3}.wmo";
        string groupPathFS = $"{basePathFS}_{groupIndex:D3}.wmo";

        try
        {
            byte[]? groupData = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, groupPathBS)
                             ?? AdtTerrainReader.ReadFileFromMpqs(clientDataPath, groupPathFS);
            if (groupData == null)
            {
                lock (_wmoGroupCacheLock) { _wmoGroupCache[cacheKey] = null; }
                return null;
            }
            var group = WmoReader.ParseGroup(groupData);
            lock (_wmoGroupCacheLock) { _wmoGroupCache[cacheKey] = group; }
            return group;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetWmoGroupCached: failed for {Path} #{Idx}", rootPath, groupIndex);
            lock (_wmoGroupCacheLock) { _wmoGroupCache[cacheKey] = null; }
            return null;
        }
    }

    private WmoRootData? GetWmoRootCached(string clientDataPath, string wmoPath)
    {
        string cacheKey = wmoPath.Replace('/', '\\').ToLowerInvariant();
        lock (_wmoDoodadCacheLock)
        {
            if (_wmoDoodadCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        try
        {
            string mpqPath = wmoPath.Replace('/', '\\');
            byte[]? rootData = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, mpqPath);
            if (rootData == null)
            {
                string altPath = wmoPath.Replace('\\', '/');
                rootData = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, altPath);
            }
            if (rootData == null)
            {
                lock (_wmoDoodadCacheLock) { _wmoDoodadCache[cacheKey] = null; }
                return null;
            }
            var root = WmoReader.ParseRoot(rootData);
            lock (_wmoDoodadCacheLock) { _wmoDoodadCache[cacheKey] = root; }
            return root;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetWmoRootCached: failed for {Path}", wmoPath);
            lock (_wmoDoodadCacheLock) { _wmoDoodadCache[cacheKey] = null; }
            return null;
        }
    }

    /// <summary>
    /// Compose a WMO's embedded doodads (MODD) into world-space placements.
    ///
    /// Coordinate frames (per the reference doc, doodads-and-water-rendering.md):
    ///
    ///   MODD local frame     : Z-up   (model file convention; X=right, Y=fwd, Z=up)
    ///   MODF world frame     : Y-up   (the WMO's PosX/Y/Z in MODF is already Y-up)
    ///   Three.js viewer      : Y-up   (same as MODF world)
    ///
    /// Basis change for any vector going from Z-up → Y-up:
    ///     (x, y, z)  →  (x, z, -y)
    ///
    /// Basis change for a quaternion from Z-up → Y-up (same rule applied to xyz):
    ///     (qx, qy, qz, qw)  →  (qx, qz, -qy, qw)
    ///
    /// We do everything in Y-up after the basis change. The WMO's MODF Euler
    /// rotation is interpreted as rotations around Y-up axes (yaw = around Y,
    /// the vertical axis). The final pose passed to the client is a full
    /// quaternion (qx, qy, qz, qw) so non-yaw orientations (tilted banners,
    /// angled torches) are preserved without flattening to a single yaw.
    /// </summary>
    private void ComposeWmoDoodadsIntoTile(
        AdtTerrainReader.WmoInstance wmo,
        WmoRootData root,
        int gridX, int gridY,
        float heightScale, float midHeight,
        float offsetXZ,
        List<PlacementEntry> entries,
        int wmoIndex,
        bool diag)
    {
        // ── Pick active doodad set(s) ──────────────────────────────────────
        // Set 0 ("$DefaultGlobal") is conventionally always-on; the MODF
        // doodadSet field selects an additional set on top.
        var emittedRanges = new List<(uint first, uint count)>();
        if (root.DoodadSets.Count > 0)
        {
            emittedRanges.Add((root.DoodadSets[0].FirstInstanceIndex,
                               root.DoodadSets[0].DoodadCount));
            if (wmo.DoodadSet > 0 && wmo.DoodadSet < root.DoodadSets.Count)
            {
                emittedRanges.Add((root.DoodadSets[wmo.DoodadSet].FirstInstanceIndex,
                                   root.DoodadSets[wmo.DoodadSet].DoodadCount));
            }
        }
        else
        {
            emittedRanges.Add((0u, (uint)root.Doodads.Count));
        }

        // ── Build WMO rotation as a Y-up quaternion ───────────────────────
        // MODF Euler is interpreted in the world frame (Y-up):
        //   RotX = pitch around X (east-west axis)
        //   RotY = yaw   around Y (up axis)         ← the dominant rotation
        //   RotZ = roll  around Z (north-south axis)
        // Build as separate quats then multiply in YXZ order (yaw·pitch·roll)
        // — matches the THREE.Euler 'YXZ' order used elsewhere in this codebase.
        //
        // Yaw convention for WMO MODF rotY:
        //   yawDeg = 270 + rotY
        // This was determined empirically via A/B/C/D testing of six candidate
        // conventions against Stormwind (RotY=38.5°), using the five named
        // guardian statues (Khadgar/Alleria/Turalyon/Kurdran/Danath) as
        // ground-truth landmarks that must stand on the WMO-geometry plinths.
        // Previous attempts: -rotY produced visible drift on large WMOs that
        // grew with distance from the WMO origin; small WMOs (farms etc.)
        // looked correct only because their bounding radius was small enough
        // to hide the angular error. 270+rotY places the five named statues
        // on their plinths and the bridge lamps on their bases.
        //
        // (For comparison: ADT MDDF M2 doodads use yawDeg = rotY - 90 on the
        // client. The two conventions differ because WMO RotY and MDDF RotY
        // are measured from different reference axes in the authoring tool.)
        double pitchRad = wmo.RotX * Math.PI / 180.0;
        double yawRad = (270.0 + wmo.RotY) * Math.PI / 180.0;
        double rollRad = wmo.RotZ * Math.PI / 180.0;

        // Quaternion(axis, angle) = (axis·sin(a/2), cos(a/2))
        var qWmoYaw = QuatFromAxisAngle(0, 1, 0, yawRad);
        var qWmoPitch = QuatFromAxisAngle(1, 0, 0, pitchRad);
        var qWmoRoll = QuatFromAxisAngle(0, 0, 1, rollRad);
        // YXZ composition: q = yaw * pitch * roll
        var qWmo = QuatMul(QuatMul(qWmoYaw, qWmoPitch), qWmoRoll);

        // ── DIAG (gated by caller's diag flag) ────────────────────────────
        // Logs MODF Eulers + composed qWmo, then a histogram of all distinct
        // model paths inside the emitted ranges (so we can see what the WMO
        // actually contains), then targeted per-doodad details for landmark
        // paths (STATUE/LAMP/LIGHT/POST/TORCH/LANTERN/BANNER), up to 30
        // entries per WMO. No global state; gated entirely by the `diag` arg.
        bool diagLogThisCall = diag;
        if (diagLogThisCall)
        {
            _logger.LogInformation(
                "WMODOODAD_DIAG WMO={Path} tile=({GX},{GY}) doodadSet={Set}\n" +
                "  MODF pos=({PX:F2},{PY:F2},{PZ:F2}) rot=(X={RX:F3}, Y={RY:F3}, Z={RZ:F3}) deg\n" +
                "  Composed qWmo=({QX:F6},{QY:F6},{QZ:F6},{QW:F6})\n" +
                "  DoodadSets[0]=\"{Set0Name}\" first={Set0First} count={Set0Count}\n" +
                "  Total emitted ranges={RangeCount}, total MODD count={TotalDoodads}",
                wmo.ModelPath, gridX, gridY, wmo.DoodadSet,
                wmo.PosX, wmo.PosY, wmo.PosZ,
                wmo.RotX, wmo.RotY, wmo.RotZ,
                qWmo.x, qWmo.y, qWmo.z, qWmo.w,
                root.DoodadSets.Count > 0 ? root.DoodadSets[0].Name : "(none)",
                root.DoodadSets.Count > 0 ? root.DoodadSets[0].FirstInstanceIndex : 0u,
                root.DoodadSets.Count > 0 ? root.DoodadSets[0].DoodadCount : 0u,
                emittedRanges.Count, root.Doodads.Count);

            // ── Histogram of model paths inside the emitted ranges ──
            // We want this so we can see what doodads the WMO actually
            // contains. SW has 6026 MODDs; the dump of the first 3 was
            // hitting interior fittings (vials, candelabras) — landmark
            // doodads visible from outside (statues, lampposts) live deeper.
            var pathCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (first, count) in emittedRanges)
            {
                int end = (int)Math.Min(first + count, (uint)root.Doodads.Count);
                for (int i = (int)first; i < end; i++)
                {
                    var path = root.Doodads[i].ModelPath ?? "";
                    if (path.Length == 0) continue;
                    pathCounts[path] = pathCounts.TryGetValue(path, out var c) ? c + 1 : 1;
                }
            }
            var sortedPaths = pathCounts
                .OrderByDescending(kv => kv.Value)
                .ToList();
            _logger.LogInformation(
                "WMODOODAD_DIAG   model path histogram: {Distinct} distinct paths, {Total} instances total",
                sortedPaths.Count, sortedPaths.Sum(kv => kv.Value));
            // Emit one line per path so journalctl grep is easy.
            foreach (var kv in sortedPaths)
            {
                _logger.LogInformation(
                    "WMODOODAD_DIAG   pathHist  count={Count,5}  model=\"{Model}\"",
                    kv.Value, kv.Key);
            }
        }

        // Counter for per-doodad dump (only landmark matches, up to 30 per WMO)
        int diagDoodadDumpCount = 0;
        const int DIAG_MAX_DUMP_PER_WMO = 30;
        // Case-insensitive substring patterns for "things visible from outside
        // and easy to correlate with the screenshot." Add to this set if a
        // specific landmark you can see isn't matching.
        var diagLandmarkPatterns = new[]
        {
            "STATUE", "LAMP", "LIGHT", "POST", "TORCH", "LANTERN", "BANNER", "FLAG", "PILLAR"
        };

        const float TILE_YARDS = AdtTerrainReader.GRID_SIZE;

        foreach (var (first, count) in emittedRanges)
        {
            int end = (int)Math.Min(first + count, (uint)root.Doodads.Count);
            for (int i = (int)first; i < end; i++)
            {
                var d = root.Doodads[i];
                if (string.IsNullOrEmpty(d.ModelPath)) continue;

                // ── Local position: Z-up → Y-up basis change ──────────────
                // (x, y, z)_Zup  →  (x, z, -y)_Yup
                double lpx = d.PosX;
                double lpy = d.PosZ;     // local Z (up) → world Y (up)
                double lpz = -d.PosY;    // local Y (forward) → world -Z (south)

                // ── Local quaternion: same basis change ───────────────────
                // (qx, qy, qz, qw)_Zup  →  (qx, qz, -qy, qw)_Yup
                double lqx = d.QuatX;
                double lqy = d.QuatZ;
                double lqz = -d.QuatY;
                double lqw = d.QuatW;

                // ── Rotate local Y-up position by qWmo ────────────────────
                var rotated = QuatRotateVec(qWmo, lpx, lpy, lpz);

                // ── Translate into MODF world space (Y-up) ────────────────
                float worldPosX = (float)(wmo.PosX + rotated.x);
                float worldPosY = (float)(wmo.PosY + rotated.y);
                float worldPosZ = (float)(wmo.PosZ + rotated.z);

                // ── Compose final orientation: qFinal = qWmo · qLocal_Yup ─
                var qLocal = (x: lqx, y: lqy, z: lqz, w: lqw);
                var qFinal = QuatMul(qWmo, qLocal);

                // ── Same per-tile transform MDDF uses for X/Z plane ───────
                float localCol = (worldPosX / TILE_YARDS - gridY) * 128.0f;
                float localRow = (worldPosZ / TILE_YARDS - gridX) * 128.0f;

                if (localCol < -16 || localCol > 128 + 16) continue;
                if (localRow < -16 || localRow > 128 + 16) continue;

                float threeX = offsetXZ + localCol * AdtTerrainReader.CELL_SIZE;
                float threeY = (worldPosY - midHeight) * heightScale;
                float threeZ = offsetXZ + localRow * AdtTerrainReader.CELL_SIZE;

                // ── DIAG: dump landmark MODDs per logged WMO ──────────────
                // We only dump full details for doodads whose path contains
                // one of the landmark patterns (statues/lamps/torches/etc.).
                // These are the things visible from outside that we can
                // correlate with the screenshot. Up to DIAG_MAX_DUMP_PER_WMO.
                if (diagLogThisCall && diagDoodadDumpCount < DIAG_MAX_DUMP_PER_WMO)
                {
                    bool isLandmark = false;
                    string upperPath = d.ModelPath.ToUpperInvariant();
                    foreach (var pat in diagLandmarkPatterns)
                    {
                        if (upperPath.Contains(pat)) { isLandmark = true; break; }
                    }
                    if (isLandmark)
                    {
                        diagDoodadDumpCount++;
                        _logger.LogInformation(
                            "WMODOODAD_DIAG   doodad[{Idx}] model=\"{Model}\" scale={Scale:F3}\n" +
                            "    rawLocal pos=({RPX:F3},{RPY:F3},{RPZ:F3}) quat=({RQX:F4},{RQY:F4},{RQZ:F4},{RQW:F4})\n" +
                            "    basisYup pos=({LPX:F3},{LPY:F3},{LPZ:F3}) quat=({LQX:F4},{LQY:F4},{LQZ:F4},{LQW:F4})\n" +
                            "    rotatedByQwmo=({RX:F3},{RY:F3},{RZ:F3})  worldPos=({WX:F2},{WY:F2},{WZ:F2})\n" +
                            "    qFinal=({QFX:F4},{QFY:F4},{QFZ:F4},{QFW:F4})  threeXYZ=({TX:F2},{TY:F2},{TZ:F2})",
                            i, d.ModelPath, d.Scale,
                            d.PosX, d.PosY, d.PosZ,
                            d.QuatX, d.QuatY, d.QuatZ, d.QuatW,
                            lpx, lpy, lpz, lqx, lqy, lqz, lqw,
                            rotated.x, rotated.y, rotated.z,
                            worldPosX, worldPosY, worldPosZ,
                            qFinal.x, qFinal.y, qFinal.z, qFinal.w,
                            threeX, threeY, threeZ);
                    }
                }

                entries.Add(new PlacementEntry
                {
                    Id = $"wd_{gridX}_{gridY}_{wmoIndex}_{i}",
                    Model = d.ModelPath,
                    Type = AdtTerrainReader.ClassifyDoodad(d.ModelPath),
                    LocalX = threeX,
                    Y = threeY,
                    LocalZ = threeZ,
                    Scale = d.Scale,
                    Kind = "wd",
                    QuatX = (float)qFinal.x,
                    QuatY = (float)qFinal.y,
                    QuatZ = (float)qFinal.z,
                    QuatW = (float)qFinal.w,
                });
            }
        }
    }

    // ── Quaternion helpers (Y-up convention, Hamilton product) ─────────────

    private static (double x, double y, double z, double w) QuatFromAxisAngle(
        double ax, double ay, double az, double angleRad)
    {
        double half = angleRad * 0.5;
        double s = Math.Sin(half);
        return (ax * s, ay * s, az * s, Math.Cos(half));
    }

    private static (double x, double y, double z, double w) QuatMul(
        (double x, double y, double z, double w) a,
        (double x, double y, double z, double w) b)
    {
        return (
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z
        );
    }

    /// <summary>v' = q · v · q⁻¹ for a unit quaternion q.</summary>
    private static (double x, double y, double z) QuatRotateVec(
        (double x, double y, double z, double w) q,
        double vx, double vy, double vz)
    {
        // Optimized: t = 2 * (qxyz × v);  v' = v + qw*t + qxyz × t
        double tx = 2.0 * (q.y * vz - q.z * vy);
        double ty = 2.0 * (q.z * vx - q.x * vz);
        double tz = 2.0 * (q.x * vy - q.y * vx);
        double rx = vx + q.w * tx + (q.y * tz - q.z * ty);
        double ry = vy + q.w * ty + (q.z * tx - q.x * tz);
        double rz = vz + q.w * tz + (q.x * ty - q.y * tx);
        return (rx, ry, rz);
    }

    private TilePlacementData? GetOrBuildPlacements(string clientDataPath, int mapId, int gx, int gy,
        float globalHeightScale, float globalMidHeight, bool diag = false)
    {
        // Round scale/mid to avoid cache misses from float precision
        string cacheKey = $"{mapId}_{gx}_{gy}_{globalHeightScale:F4}_{globalMidHeight:F1}";
        // When diag is set, bypass the cache entirely: don't read, don't write.
        // Every WMO touched in this build will hit ComposeWmoDoodadsIntoTile
        // with diag=true and log. Subsequent non-diag calls re-populate cache.
        if (!diag)
        {
            lock (_placementCacheLock)
            {
                if (_placementCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }
        }

        var adt = AdtTerrainReader.ReadFromMpq(clientDataPath, MapIdToName(mapId), gx, gy);
        if (adt == null)
        {
            if (!diag)
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

            // Emit this WMO's embedded doodads (MODD inside the WMO root) as
            // M2 placements. Cached per WMO path — Stormwind's 6,026 MODD defs
            // are parsed once, then reused for every tile the WMO touches.
            var wmoRoot = GetWmoRootCached(clientDataPath, w.ModelPath);
            if (wmoRoot != null && wmoRoot.Doodads.Count > 0)
            {
                ComposeWmoDoodadsIntoTile(
                    w, wmoRoot,
                    gx, gy,
                    globalHeightScale, globalMidHeight,
                    offsetXZ,
                    entries,
                    wi,
                    diag);
            }
        }

        var data = new TilePlacementData { Entries = entries };

        // Diagnostic: how many of each kind ended up on this tile?
        int adtDoodadCount = 0, wmoCount = 0, wmoDoodadCount = 0;
        foreach (var e in entries)
        {
            if (e.Kind == "w") wmoCount++;
            else if (e.Id.StartsWith("wd_")) wmoDoodadCount++;
            else adtDoodadCount++;
        }
        _logger.LogInformation(
            "GetOrBuildPlacements: tile ({GX},{GY}) → {AdtD} ADT doodads, {WmoC} WMOs, {WmoD} WMO doodads",
            gx, gy, adtDoodadCount, wmoCount, wmoDoodadCount);

        if (!diag)
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
        float globalMidHeight = 0, float globalHeightScale = 2.0f,
        bool wmoDoodadDiag = false)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Json(new { success = false, error });

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { success = false, error = "Client data path not configured." });

        if (wmoDoodadDiag)
        {
            _logger.LogInformation(
                "WMODOODAD_DIAG === START === preset={Preset} cam=({CX:F1},{CZ:F1}) radius={R:F0}",
                preset, camX, camZ, loadRadius);
        }

        float tileWidthMesh = 128 * VmangosMapParser.CELL_SIZE;

        // Anchor the tile scan on the camera, not the preset center.
        // The client passes camX/camZ in mesh-space relative to the preset
        // origin (same frame the placement positions live in). Convert to a
        // tile-grid offset so the iteration follows the camera as it moves
        // away from the preset center; otherwise WMOs/doodads silently stop
        // loading once the camera leaves the original ±maxTileReach window.
        // Mesh→grid convention matches the client's TileGrid.cameraToGrid:
        //   meshX → +gridY (column), meshZ → +gridX (row).
        int camTileDx = (int)Math.Round(camX / tileWidthMesh);
        int camTileDy = (int)Math.Round(camZ / tileWidthMesh);
        int camGridX = p.gridX + camTileDy;
        int camGridY = p.gridY + camTileDx;

        int maxTileReach = (int)Math.Ceiling(loadRadius / tileWidthMesh) + 1;
        var addDoodads = new List<object>();
        var addWmos = new List<object>();
        float loadR2 = loadRadius * loadRadius;

        for (int tdy = -maxTileReach; tdy <= maxTileReach; tdy++)
        {
            for (int tdx = -maxTileReach; tdx <= maxTileReach; tdx++)
            {
                int gx = camGridX + tdy;
                int gy = camGridY + tdx;
                if (gx < 0 || gx > 63 || gy < 0 || gy > 63) continue;

                // Tile offset is in mesh space relative to the preset origin,
                // so camX/camZ (also preset-relative) compare directly. We
                // express the candidate tile's offset via its grid delta from
                // the preset, NOT from the camera, to keep the per-entry math
                // (e.LocalX + tileOffX) consistent with how placements were
                // computed in GetOrBuildPlacements.
                float tileOffX = (gy - p.gridY) * tileWidthMesh;
                float tileOffZ = (gx - p.gridX) * tileWidthMesh;

                // Quick reject: tile center too far
                float distToTile = (float)Math.Sqrt(
                    (camX - tileOffX) * (camX - tileOffX) + (camZ - tileOffZ) * (camZ - tileOffZ));
                float halfDiag = tileWidthMesh * 0.707f;
                if (distToTile > loadRadius + halfDiag) continue;

                var tileData = GetOrBuildPlacements(clientDataPath, p.mapId, gx, gy, globalHeightScale, globalMidHeight, wmoDoodadDiag);
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
                            scale = e.Scale,
                            kind = "d"
                        });
                    }
                    else if (e.Kind == "wd")
                    {
                        // WMO-embedded doodad: full quaternion orientation,
                        // already in Y-up world space. Client applies it
                        // directly without Euler conversion or rotY offset.
                        addDoodads.Add(new
                        {
                            id = e.Id,
                            model = e.Model,
                            type = e.Type,
                            x = wx,
                            y = e.Y,
                            z = wz,
                            scale = e.Scale,
                            kind = "wd",
                            qx = e.QuatX,
                            qy = e.QuatY,
                            qz = e.QuatZ,
                            qw = e.QuatW
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

    // ═══════════════════════════════════════════════════════════════
    // DIAGNOSTIC — Height pipeline: place → save → commit → stream round-trip
    //   Usage: Place a WMO, hit this. Delete it, hit this again.
    //   Compare "saved meshY" vs "streamed Y after round-trip" vs "vanilla WMO Y at same area"
    // ═══════════════════════════════════════════════════════════════
    [HttpGet]
    public IActionResult DiagnoseHeight(string? preset)
    {
        if (!TryResolvePreset(preset, out var p, out var error))
            return Content($"ERROR: {error}", "text/plain");

        string clientDataPath = GetClientDataDirectory();
        string mapsDir = GetMapsDirectory();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== HEIGHT DIAGNOSTIC ===");
        sb.AppendLine($"Preset: {preset}  gridX={p.gridX} gridY={p.gridY} mapId={p.mapId}");
        sb.AppendLine();

        // ── Multi-tile height calc (matches Heightmap endpoint) ──
        float globalMin = float.MaxValue, globalMax = float.MinValue;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int gx = p.gridX + dy, gy = p.gridY + dx;
                string mp = Path.Combine(mapsDir, VmangosMapParser.BuildFilename(p.mapId, gx, gy));
                if (!System.IO.File.Exists(mp)) continue;
                var tr = VmangosMapParser.Parse(mp, 8, 8, 8);
                if (tr == null) continue;
                sb.AppendLine($"  Tile ({gx},{gy}): min={tr.MinHeight:F4} max={tr.MaxHeight:F4}");
                if (tr.MinHeight < globalMin) globalMin = tr.MinHeight;
                if (tr.MaxHeight > globalMax) globalMax = tr.MaxHeight;
            }
        }
        float midHeight = (globalMin + globalMax) * 0.5f;
        float range2 = globalMax - globalMin;
        float heightScale = range2 > 0 ? Math.Min(3.5f, 350.0f / range2) : 3.5f;
        sb.AppendLine();
        sb.AppendLine($"Multi-tile: globalMin={globalMin:F4}  globalMax={globalMax:F4}");
        sb.AppendLine($"  midHeight={midHeight:F4}  heightScale={heightScale:F6}");

        // ── Saved placements from DB ──
        sb.AppendLine();
        sb.AppendLine("=== SAVED PLACEMENTS (from DB) ===");
        using var adminConn = new MySqlConnector.MySqlConnection("Server=localhost;Database=vmangos_admin;User=mangos;Password=mangos");
        adminConn.Open();
        var placements = adminConn.Query(
            "SELECT id, wmo_path, mesh_x, mesh_y, mesh_z, rot_y, scale_val, committed FROM custom_wmo_placements WHERE preset=@Preset ORDER BY id",
            new { Preset = preset });

        float cellSize = AdtTerrainReader.CELL_SIZE;
        float gridSize = AdtTerrainReader.GRID_SIZE;

        foreach (var row in placements)
        {
            float meshX = (float)row.mesh_x, meshY = (float)row.mesh_y, meshZ = (float)row.mesh_z;
            sb.AppendLine($"  Placement #{row.id}: committed={row.committed}");
            sb.AppendLine($"    DB meshY = {meshY:F4}");
            sb.AppendLine($"    Reverse transform → modfPosY = {meshY:F4} / {heightScale:F6} + {midHeight:F4} = {meshY / heightScale + midHeight:F4}");
            sb.AppendLine($"    Round-trip streamedY = (modfPosY - midHeight) * heightScale = {meshY:F4}  (should equal DB meshY)");
        }

        // ── Vanilla WMOs from ADT (what streaming sees) ──
        sb.AppendLine();
        sb.AppendLine("=== VANILLA + PATCHED WMOs FROM ADT (what NearbyObjects returns) ===");
        if (!string.IsNullOrEmpty(clientDataPath))
        {
            try
            {
                var adt = AdtTerrainReader.ReadFromMpq(clientDataPath, MapIdToName(p.mapId), p.gridX, p.gridY);
                if (adt != null)
                {
                    var wmos = AdtTerrainReader.GetWmosForRegion(adt, heightScale, midHeight);
                    sb.AppendLine($"  Total WMOs in ADT: {wmos.Count}");
                    foreach (var w in wmos)
                    {
                        float streamedY = (w.PosY - midHeight) * heightScale;
                        sb.AppendLine($"  WMO: {w.ModelPath}");
                        sb.AppendLine($"    MODF PosY (raw) = {w.PosY:F4}");
                        sb.AppendLine($"    streamedY = ({w.PosY:F4} - {midHeight:F4}) * {heightScale:F6} = {streamedY:F4}");
                        sb.AppendLine($"    MODF Pos = ({w.PosX:F2}, {w.PosY:F2}, {w.PosZ:F2})");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  ADT read error: {ex.Message}");
            }

            // ── Also read WITHOUT patch-Z for comparison ──
            sb.AppendLine();
            sb.AppendLine("=== VANILLA WMOs (original ADT, no patch-Z) ===");
            try
            {
                string mapName = MapIdToName(p.mapId);
                string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{p.gridY}_{p.gridX}.adt";
                byte[]? origBytes = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtPath, skipPatchZ: true);
                if (origBytes != null)
                {
                    var origAdt = AdtTerrainReader.Parse(origBytes, p.gridX, p.gridY);
                    if (origAdt != null)
                    {
                        var origWmos = AdtTerrainReader.GetWmosForRegion(origAdt, heightScale, midHeight);
                        sb.AppendLine($"  Original WMOs: {origWmos.Count}");
                        foreach (var w in origWmos)
                        {
                            float streamedY = (w.PosY - midHeight) * heightScale;
                            sb.AppendLine($"  WMO: {w.ModelPath}");
                            sb.AppendLine($"    MODF PosY (raw) = {w.PosY:F4}");
                            sb.AppendLine($"    streamedY = {streamedY:F4}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  Original ADT read error: {ex.Message}");
            }
        }

        // ── JS-side context ──
        sb.AppendLine();
        sb.AppendLine("=== JS-SIDE NOTES ===");
        sb.AppendLine("  terrain mesh: position.y = -0.5");
        sb.AppendLine("  wmoGroup:     position.y = -0.5");
        sb.AppendLine("  ghost:        added to scene (no offset)");
        sb.AppendLine("  ghostY = raycast hit on terrain (includes terrain's -0.5)");
        sb.AppendLine("  placed mesh goes into wmoGroup (gets additional -0.5)");
        sb.AppendLine("  → placed WMO world Y = meshY + wmoGroup.y = meshY - 0.5");
        sb.AppendLine("  → streamed WMO world Y = streamedY + wmoGroup.y = streamedY - 0.5");
        sb.AppendLine("  If ghostY == streamedY, both display at same height (both get -0.5 from their parent)");
        sb.AppendLine("  But ghost is in scene (no -0.5), terrain surface is at vertexY-0.5");
        sb.AppendLine("  So ghost appears 0.5 units ABOVE where the placed WMO ends up");

        return Content(sb.ToString(), "text/plain");
    }

    private static string MapIdToName(int id) => id switch { 0 => "Azeroth", 1 => "Kalimdor", _ => $"Map{id}" };

    // ═══════════════════════════════════════════════════════════════
    // HELPER: Build active placements + affected tiles from DB
    // ═══════════════════════════════════════════════════════════════

    // Must match ServerDataService.CUSTOM_OBJECT_ID_FLOOR
    private const uint CUSTOM_OBJECT_ID_FLOOR = 900000;

    /// <summary>
    /// Queries ALL committed placements from DB that have stored MODF coordinates,
    /// builds DirBinPlacement list and the set of affected tiles for MoveMapGenerator.
    /// </summary>
    private (List<DirBinPlacement> placements, List<(int mapId, int tileX, int tileY)> tiles)
        BuildActivePlacements(System.Data.IDbConnection adminConn)
    {
        var placements = new List<DirBinPlacement>();
        var tileSet = new HashSet<(int, int, int)>();

        var rows = adminConn.Query(@"
            SELECT id, preset, map_id AS mapId, wmo_path AS wmoPath,
                   modf_pos_x AS modfPosX, modf_pos_y AS modfPosY, modf_pos_z AS modfPosZ,
                   modf_bb_min_x AS modfBbMinX, modf_bb_min_y AS modfBbMinY, modf_bb_min_z AS modfBbMinZ,
                   modf_bb_max_x AS modfBbMaxX, modf_bb_max_y AS modfBbMaxY, modf_bb_max_z AS modfBbMaxZ,
                   rot_y AS rotY
            FROM custom_wmo_placements
            WHERE committed = 1 AND modf_pos_x IS NOT NULL
            ORDER BY id").ToList();

        foreach (var row in rows)
        {
            string rowPreset = (string)row.preset;
            if (!TryResolvePreset(rowPreset, out var rp, out _))
            {
                _logger.LogWarning("BuildActivePlacements: Skipping placement {Id} — invalid preset '{Preset}'",
                    (int)row.id, rowPreset);
                continue;
            }

            // dir_bin tileX/tileY = swapped from preset gridX/gridY
            int tileX = rp.gridY;
            int tileY = rp.gridX;

            string wmoPath = ((string)row.wmoPath).Replace('/', '\\');

            placements.Add(new DirBinPlacement
            {
                PlacementDbId = (int)row.id,
                MapId = rp.mapId,
                TileX = tileX,
                TileY = tileY,
                UniqueId = CUSTOM_OBJECT_ID_FLOOR + (uint)(int)row.id,
                ModfPosX = (float)row.modfPosX,
                ModfPosY = (float)row.modfPosY,
                ModfPosZ = (float)row.modfPosZ,
                RotX = 0,
                RotY = (float)row.rotY,
                RotZ = 0,
                BbMinX = (float)row.modfBbMinX,
                BbMinY = (float)row.modfBbMinY,
                BbMinZ = (float)row.modfBbMinZ,
                BbMaxX = (float)row.modfBbMaxX,
                BbMaxY = (float)row.modfBbMaxY,
                BbMaxZ = (float)row.modfBbMaxZ,
                WmoPath = wmoPath
            });

            tileSet.Add((rp.mapId, tileX, tileY));
        }

        return (placements, tileSet.ToList());
    }

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

public class RegenerateServerDataDto
{
    public int PlacementDbId { get; set; }
}