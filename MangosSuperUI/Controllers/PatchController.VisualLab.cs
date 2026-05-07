using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MangosSuperUI.Services;
using SkiaSharp;
using System.Runtime.InteropServices;
using War3Net.Drawing.Blp;

namespace MangosSuperUI.Controllers;

/// <summary>
/// SpellCreator Visual Lab — Phase 1 API endpoints.
/// Extends PatchController (partial class) with:
///   GET /Patch/VisualLab              → page
///   GET /Patch/VisualPreview?entry=133 → emitter + texture JSON for Three.js viewer
///   GET /Patch/VisualTexture?file=clouds8x8.blp → BLP decoded as PNG image
///
/// Reuses existing PatchController infrastructure:
///   - FindM2PathForPhase() for DBC chain resolution
///   - ReadM2FromClient() for M2 file loading
///   - _vanillaBlps for BLP header specs
///   - _dbc for SpellVisual/Kit/EffectName DBC access
///
/// Session 37: Visual Lab — 3D spell effect viewer/editor.
/// </summary>
public partial class PatchController
{
    // ═══════════════════════════════════════════════════════════════
    // PAGE
    // ═══════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult VisualLab() => View("VisualLab");

    // ═══════════════════════════════════════════════════════════════
    // VISUAL PREVIEW — Full emitter + texture JSON for Three.js
    // ═══════════════════════════════════════════════════════════════


    private static readonly Dictionary<string, byte[]> _terrainBlpCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _terrainBlpCacheLock = new();
    private static readonly Dictionary<string, AdtTerrainReader.AdtResult> _adtCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _adtCacheLock = new();

    /// <summary>
    /// GET /Patch/VisualPreview?entry=133
    /// Returns the complete visual data for a spell: per-phase emitter snapshots,
    /// texture table entries with vanilla BLP specs, and M2 paths.
    /// This is the primary data source for the Three.js particle viewer.
    ///
    /// Resolution: spellEntry → spell_template/Spell.dbc → spellVisual1
    ///   → SpellVisual.dbc → per-phase kit IDs → SpellVisualKit.dbc
    ///   → SpellVisualEffectName.dbc → M2 path → parse emitters + textures.
    /// </summary>
    [HttpGet]
    public IActionResult VisualPreview(int entry)
    {
        if (entry <= 0)
            return Json(new { success = false, error = "Spell entry required." });

        try
        {
            // ── Resolve spellVisual1 ──
            uint visualId = 0;
            using (var conn = _db.Mangos())
            {
                visualId = Dapper.SqlMapper.ExecuteScalar<uint>(conn,
                    "SELECT spellVisual1 FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                    new { E = entry });
            }
            if (visualId == 0 && _dbc.SpellEntries.TryGetValue((uint)entry, out var dbcSpell))
                visualId = dbcSpell.SpellVisual1;

            if (visualId == 0)
                return Json(new { success = false, error = $"No visual ID found for spell #{entry}." });

            // ── Read DBC chain ──
            var visualDbc = DbcWriterService.ReadDbc(Path.Combine(_dbc.DbcPath, "SpellVisual.dbc"));
            var visualRow = visualDbc.GetRow(visualId);
            if (visualRow == null)
                return Json(new { success = false, error = $"SpellVisual #{visualId} not found in DBC." });

            var kitDbc = DbcWriterService.ReadDbc(Path.Combine(_dbc.DbcPath, "SpellVisualKit.dbc"));
            var efnDbc = DbcWriterService.ReadDbc(Path.Combine(_dbc.DbcPath, "SpellVisualEffectName.dbc"));

            // ── Build per-phase data ──
            var phases = new Dictionary<string, object>();

            // Standard kit-based phases
            var phaseDefs = new (string key, int kitField)[]
            {
                ("precast", 1), ("cast", 2), ("impact", 3),
                ("state", 4), ("stateDone", 5), ("channel", 6)
            };

            foreach (var (phaseKey, kitField) in phaseDefs)
            {
                uint kitId = visualRow[kitField];
                if (kitId <= 1) continue; // 0=none, 1=dummy sentinel

                var kitRow = kitDbc.GetRow(kitId);
                if (kitRow == null) continue;

                // Collect M2 effects from this kit
                var phaseM2s = CollectKitEffects(kitRow, efnDbc, phaseKey);
                if (phaseM2s.Count > 0)
                    phases[phaseKey] = phaseM2s;
            }

            // Missile (field 7 = HasMissile = EffectName ID, not a kit)
            uint missileEffectId = visualRow[7];
            if (missileEffectId > 0)
            {
                var missileRow = efnDbc.GetRow(missileEffectId);
                if (missileRow != null)
                {
                    string m2Path = SpellVisualCloner.NormalizeM2Extension(efnDbc.ReadString(missileRow[2]));
                    if (!string.IsNullOrEmpty(m2Path))
                    {
                        var m2Result = ParseM2ForVisualLab(m2Path);
                        if (m2Result != null)
                            phases["missile"] = new[] { m2Result };
                    }
                }
            }

            // ── Spell metadata (timing for sequence playback) ──
            string spellName = "";
            float missileSpeed = 0;
            uint castingTimeIndex = 0;
            uint rangeIndex = 0;

            // Try spell_template first
            using (var connMeta = _db.Mangos())
            {
                var meta = Dapper.SqlMapper.QueryFirstOrDefault<dynamic>(connMeta,
                    @"SELECT name, speed, castingTimeIndex, rangeIndex
                      FROM spell_template WHERE entry = @E ORDER BY build DESC LIMIT 1",
                    new { E = entry });
                if (meta != null)
                {
                    spellName = meta.name ?? "";
                    missileSpeed = (float)(meta.speed ?? 0f);
                    castingTimeIndex = (uint)(meta.castingTimeIndex ?? 0);
                    rangeIndex = (uint)(meta.rangeIndex ?? 0);
                }
            }

            // DBC fallback for name
            if (string.IsNullOrEmpty(spellName) && _dbc.SpellEntries.TryGetValue((uint)entry, out var spellEntry))
                spellName = spellEntry.Name;

            // Resolve cast time from CastingTimes.dbc index
            // Common values: 0/1=instant, 5=1500ms, 14=2500ms, 15=3000ms, 22=3500ms
            float castTimeMs = ResolveCastTime(castingTimeIndex);

            return Json(new
            {
                success = true,
                spellEntry = entry,
                spellName,
                visualId,
                phases,
                timing = new
                {
                    castTimeMs,
                    missileSpeed,
                    rangeIndex
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VisualLab: VisualPreview failed for #{Entry}", entry);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Collect M2 effect data from a SpellVisualKit row.
    /// Kit effect fields are at indices 3–10.
    /// </summary>
    private List<object> CollectKitEffects(uint[] kitRow, DbcWriterService efnDbc, string phaseKey)
    {
        var results = new List<object>();
        int[] effectFields = { 3, 4, 5, 6, 7, 8, 9, 10 };

        foreach (int ef in effectFields)
        {
            if (ef >= kitRow.Length) break;
            uint effectId = kitRow[ef];
            if (effectId == 0 || effectId == 0xFFFFFFFF) continue;

            var effectRow = efnDbc.GetRow(effectId);
            if (effectRow == null) continue;

            string m2Path = SpellVisualCloner.NormalizeM2Extension(efnDbc.ReadString(effectRow[2]));
            if (string.IsNullOrEmpty(m2Path)) continue;

            var m2Result = ParseM2ForVisualLab(m2Path);
            if (m2Result != null)
                results.Add(m2Result);
        }

        return results;
    }

    /// <summary>
    /// Parse an M2 file and return emitter + texture data for the Visual Lab viewer.
    /// </summary>
    private object? ParseM2ForVisualLab(string m2Path)
    {
        byte[]? m2Data = ReadM2FromClient(m2Path);
        if (m2Data == null) return null;

        var emitters = M2EmitterParser.ReadEmitters(m2Data);
        var textures = M2TextureParser.ParseTextures(m2Data);

        if (emitters.Count == 0 && textures.Count == 0) return null;

        return new
        {
            m2Path,
            emitters = emitters.Select(e => new
            {
                index = e.Index,
                blendMode = (int)e.BlendMode,
                emitterType = (int)e.EmitterType,
                textureId = (int)e.TextureId,

                // Inline color triplet (ARGB uint32 → hex string)
                colorStart = e.ColorStart.ToString("X8"),
                colorMid = e.ColorMid.ToString("X8"),
                colorEnd = e.ColorEnd.ToString("X8"),

                // Inline scale triplet
                scaleStart = e.ScaleStart,
                scaleMid = e.ScaleMid,
                scaleEnd = e.ScaleEnd,

                // M2Track animated properties
                emissionSpeed = e.TrackValues.GetValueOrDefault("emissionSpeed"),
                speedVariation = e.TrackValues.GetValueOrDefault("speedVariation"),
                verticalRange = e.TrackValues.GetValueOrDefault("verticalRange"),
                horizontalRange = e.TrackValues.GetValueOrDefault("horizontalRange"),
                gravity = e.TrackValues.GetValueOrDefault("gravity"),
                lifespan = e.TrackValues.GetValueOrDefault("lifespan"),
                emissionRate = e.TrackValues.GetValueOrDefault("emissionRate"),
                emissionAreaLength = e.TrackValues.GetValueOrDefault("emissionAreaLength"),
                emissionAreaWidth = e.TrackValues.GetValueOrDefault("emissionAreaWidth"),

                // Keyframe counts (1=static, >1=animated)
                keyframeCounts = e.TrackKeyframeCounts
                    .Where(kv => kv.Value > 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            }),
            textures = textures.Select(t =>
            {
                // Get vanilla BLP specs for this texture
                var blpInfo = _vanillaBlps.GetBlpInfo(t.Filename);

                return new
                {
                    index = t.Index,
                    filename = t.Filename,
                    normalized = VanillaBlpService.NormalizeToFilename(t.Filename),
                    referencedByEmitters = t.ReferencedByEmitters,
                    blpInfo = blpInfo != null ? new
                    {
                        width = blpInfo.Width,
                        height = blpInfo.Height,
                        format = blpInfo.Format,
                        alphaDepth = (int)blpInfo.AlphaDepth,
                        isAdditive = blpInfo.IsNoAlpha
                    } : null
                };
            })
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // CAST TIME RESOLUTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve castingTimeIndex → milliseconds from SpellCastTimes.dbc.
    /// Falls back to a hardcoded lookup for the most common values if the DBC isn't available.
    /// </summary>
    private float ResolveCastTime(uint castingTimeIndex)
    {
        try
        {
            var castTimesPath = Path.Combine(_dbc.DbcPath, "SpellCastTimes.dbc");
            if (System.IO.File.Exists(castTimesPath))
            {
                var dbc = DbcWriterService.ReadDbc(castTimesPath);
                var row = dbc.GetRow(castingTimeIndex);
                if (row != null && row.Length > 1)
                    return row[1]; // field[1] = castTime in milliseconds
            }
        }
        catch { /* fallback */ }

        // Hardcoded fallback for common vanilla cast time indices
        return castingTimeIndex switch
        {
            0 => 0,
            1 => 0,
            2 => 250,
            4 => 1000,
            5 => 1500,
            14 => 2500,
            15 => 3000,
            16 => 3500,
            22 => 3500,
            _ => 2500
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // TEXTURE DECODE — BLP → PNG on the fly
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// GET /Patch/VisualTexture?file=clouds8x8.blp
    /// Reads a vanilla BLP from RawBlpPath, decodes it to PNG via War3Net.Drawing.Blp
    /// (BGRA pixel output) + SkiaSharp (PNG encode), and returns it as image/png.
    ///
    /// Uses in-memory caching since vanilla BLPs never change.
    /// </summary>
    private static readonly Dictionary<string, byte[]> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _textureCacheLock = new();

    [HttpGet]
    public IActionResult VisualTexture(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return BadRequest("File parameter required.");

        string normalized = VanillaBlpService.NormalizeToFilename(file);

        // Check cache
        lock (_textureCacheLock)
        {
            if (_textureCache.TryGetValue(normalized, out byte[]? cached))
                return File(cached, "image/png");
        }

        // Get BLP info and raw bytes
        var blpInfo = _vanillaBlps.GetBlpInfo(normalized);
        if (blpInfo == null)
            return NotFound($"BLP not found: {normalized}");

        string rawBlpPath = _vanillaBlps.GetType()
            .GetProperty("RawBlpPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .GetValue(_vanillaBlps)?.ToString() ?? "";

        string fullPath = Path.Combine(rawBlpPath, normalized);
        if (!System.IO.File.Exists(fullPath))
            return NotFound($"BLP file not found on disk: {normalized}");

        try
        {
            byte[] blpData = System.IO.File.ReadAllBytes(fullPath);
            byte[] pngBytes = DecodeBlpToPng(blpData);

            // Cache it
            lock (_textureCacheLock)
            {
                _textureCache[normalized] = pngBytes;
            }

            return File(pngBytes, "image/png");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VisualLab: Failed to decode BLP: {File}", normalized);
            return StatusCode(500, $"BLP decode failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Decode a BLP file to PNG bytes using War3Net.Drawing.Blp (BGRA output)
    /// and SkiaSharp for PNG encoding. Handles all vanilla BLP formats:
    /// DXT1, DXT3, DXT5, and palettized.
    /// </summary>
    private static byte[] DecodeBlpToPng(byte[] blpData)
    {
        using var ms = new MemoryStream(blpData);
        var blpFile = new BlpFile(ms);
        var pixels = blpFile.GetPixels(0, out int w, out int h);

        // War3Net returns BGRA byte array → use Bgra8888 color type
        using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var pixelSpan = bitmap.GetPixelSpan();
        pixels.AsSpan().CopyTo(MemoryMarshal.AsBytes(pixelSpan));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ═══════════════════════════════════════════════════════════════
    // TEXTURE LIST — All available vanilla BLP files
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// GET /Patch/VisualTextureList?m2Path=Spells\Fireball_Missile_Low.m2
    /// Returns texture info for a specific M2, or if no path given,
    /// lists all BLP files in the RawBlpPath directory.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTextureList(string? m2Path)
    {
        if (!string.IsNullOrEmpty(m2Path))
        {
            byte[]? m2Data = ReadM2FromClient(m2Path);
            if (m2Data == null)
                return Json(new { success = false, error = $"M2 not found: {m2Path}" });

            var textures = M2TextureParser.ParseTextures(m2Data);
            return Json(new
            {
                success = true,
                textures = textures.Select(t =>
                {
                    var info = _vanillaBlps.GetBlpInfo(t.Filename);
                    return new
                    {
                        index = t.Index,
                        filename = t.Filename,
                        normalized = VanillaBlpService.NormalizeToFilename(t.Filename),
                        width = info?.Width ?? 0,
                        height = info?.Height ?? 0,
                        format = info?.Format ?? "unknown"
                    };
                })
            });
        }

        return Json(new { success = false, error = "m2Path parameter required." });
    }

    // ═══════════════════════════════════════════════════════════════
    // TERRAIN BACKDROP — ADT heightmap for Visual Lab
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Terrain presets — well-known VMaNGOS .map files with descriptive names.
    /// Filename: MMMXXYY.map (mapId, gridX, gridY).
    /// Map 0 = Eastern Kingdoms, Map 1 = Kalimdor.
    /// Grid (32,48) ≈ Northshire/Elwynn area.
    /// Terrain presets — tile coordinates for well-known zones.
    ///
    /// Convention (empirically verified by terrain_forensics_v3.py):
    ///   gridX = gy = row index (floor(posZ/SIZE) for doodads on this tile)
    ///   gridY = gx = col index (floor(posX/SIZE) for doodads on this tile)
    ///   .map filename: VmangosMapParser builds {mapId:D3}{gridX:D2}{gridY:D2}.map
    ///   ADT filename:  Map_{gridY}_{gridX}.adt  (col first, row second)
    ///   Doodad transform: localCol = (posX/SIZE - gridY) * 128
    ///                     localRow = (posZ/SIZE - gridX) * 128
    ///
    /// Values verified against V9 height matching (error < 1.0 for correct tile).
    /// </summary>
    private static readonly (string key, string label, int mapId, int gridX, int gridY)[] _terrainPresets =
    {
        ("northshire",  "Northshire Valley",    0, 48, 33),  // .map=0004833, ADT=Azeroth_33_48
        ("elwynn",      "Elwynn Forest",        0, 49, 33),  // .map=0004933, ADT=Azeroth_33_49
        ("barrens",     "The Barrens",          1, 35, 33),  // .map=0013533, ADT=Kalimdor_33_35  ✓ was correct
        ("durotar",     "Durotar",              1, 33, 35),  // .map=0013335, ADT=Kalimdor_35_33
        ("westfall",    "Westfall",             0, 50, 29),  // .map=0005029, ADT=Azeroth_29_50
        ("darkshore",   "Darkshore",            1, 23, 33),  // .map=0012333, ADT=Kalimdor_33_23
        ("stonetalon",  "Stonetalon Mountains", 1, 32, 29),  // .map=0013229, ADT=Kalimdor_29_32
        ("redridge",    "Redridge Mountains",   0, 50, 34),  // .map=0005034, ADT=Azeroth_34_50
    };

    /// <summary>
    /// GET /Patch/VisualTerrainPresets
    /// Returns available terrain presets for the backdrop selector.
    /// Checks which .map files exist on disk.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainPresets()
    {
        string mapsDir = GetMapsDirectory();
        var available = _terrainPresets
            .Where(p =>
            {
                string filename = VmangosMapParser.BuildFilename(p.mapId, p.gridX, p.gridY);
                string fullPath = Path.Combine(mapsDir, filename);
                return System.IO.File.Exists(fullPath);
            })
            .Select(p => new { key = p.key, label = p.label })
            .ToList();

        return Json(new { success = true, presets = available, mapsDir });
    }

    /// <summary>
    /// GET /Patch/VisualTerrain?preset=northshire
    /// Returns heightmap vertex positions + triangle indices for building a Three.js mesh.
    /// Reads VMaNGOS extracted .map files.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrain(string? preset, int cx = 8, int cy = 8, int radius = 3)
    {
        string mapsDir = GetMapsDirectory();
        if (string.IsNullOrEmpty(mapsDir))
            return Json(new { success = false, error = "Maps directory not found. Expected at /home/wowvmangos/wowclient/maps/" });

        if (string.IsNullOrEmpty(preset))
            return Json(new { success = false, error = "Specify a preset parameter." });

        var p = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (p.key == null)
            return Json(new { success = false, error = $"Unknown terrain preset: {preset}" });

        string filename = VmangosMapParser.BuildFilename(p.mapId, p.gridX, p.gridY);
        string fullPath = Path.Combine(mapsDir, filename);

        if (!System.IO.File.Exists(fullPath))
            return Json(new { success = false, error = $"Map file not found: {filename}" });

        try
        {
            var result = VmangosMapParser.Parse(fullPath, cx, cy, radius);
            if (result == null)
                return Json(new { success = false, error = $"Failed to parse map file: {filename}" });

            return Json(new
            {
                success = true,
                preset,
                label = p.label,
                positions = result.Positions,
                indices = result.Indices,
                vertsWidth = result.VertsWidth,
                vertsHeight = result.VertsHeight,
                chunksWidth = result.ChunksWidth,
                chunksHeight = result.ChunksHeight,
                heightScale = result.HeightScale
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VisualLab: Terrain parse failed for {File}", filename);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Resolve the VMaNGOS extracted maps directory.
    /// Reads Vmangos:MapsDataPath from config, then infers from DbcPath,
    /// then falls back to common locations.
    ///
    /// NOTE: Requires IConfiguration added to PatchController constructor.
    /// Add to the main PatchController.cs:
    ///   Field:  private readonly IConfiguration _config;
    ///   Param:  IConfiguration config
    ///   Assign: _config = config;
    ///
    /// If you prefer not to modify the constructor, replace _config usage below
    /// with the hardcoded candidates only.
    /// </summary>
    private string GetMapsDirectory()
    {
        string[] candidates = {
            // Config: Vmangos:MapsDataPath (set in Settings page / appsettings.json)
            _config?.GetValue<string>("Vmangos:MapsDataPath") ?? "",
            // Sibling of DbcPath: .../5875/dbc → ../../maps
            string.IsNullOrEmpty(_dbc.DbcPath) ? "" : Path.GetFullPath(Path.Combine(_dbc.DbcPath, "..", "..", "maps")),
            // Common locations
            "/home/wowvmangos/wowclient/maps",
            "/home/wowvmangos/vmangos/run/data/maps",
        };

        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c))
                return c;
        }
        return "";
    }



    // ── Helper: get client data directory ──

    private string GetClientDataDirectory()
    {
        string[] candidates = {
        _config?.GetValue<string>("Vmangos:ClientDataPath") ?? "",
        "/home/wowvmangos/wowclient/Data",
    };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c))
                return c;
        }
        return "";
    }

    // ── Helper: get or parse ADT — no cache, always fresh parse ──

    private AdtTerrainReader.AdtResult? GetOrParseAdt(string clientDataPath, string mapName, int gridX, int gridY)
    {
        return AdtTerrainReader.ReadFromMpq(clientDataPath, mapName, gridX, gridY);
    }

    // ═══════════════════════════════════════════════════════════════
    // TERRAIN TEXTURES — Ground texture splat map + texture URLs
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// GET /Patch/VisualTerrainTextures?preset=northshire&cx=8&cy=8&radius=3
    /// Reads ADT from client MPQ, bakes a fully composited RGB terrain texture
    /// server-side by sampling BLP tilesets and blending with alpha maps.
    ///
    /// Response:
    /// {
    ///   success: true,
    ///   compositeBase64: "data:image/png;base64,...",
    ///   compositeWidth, compositeHeight,
    ///   chunksWidth, chunksHeight
    /// }
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainTextures(string? preset, int cx = 8, int cy = 8, int radius = 3)
    {
        if (string.IsNullOrEmpty(preset))
            return Json(new { success = false, error = "Specify a preset parameter." });

        var p = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (p.key == null)
            return Json(new { success = false, error = $"Unknown terrain preset: {preset}" });

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { success = false, error = "Client data path not configured. Set Vmangos:ClientDataPath in Settings." });

        // Map ID → map name for ADT path
        string mapName = p.mapId switch
        {
            0 => "Azeroth",
            1 => "Kalimdor",
            _ => $"Map{p.mapId}"
        };

        try
        {
            var adt = GetOrParseAdt(clientDataPath, mapName, p.gridX, p.gridY);
            if (adt == null)
                return Json(new { success = false, error = $"ADT file not found in client MPQs for {mapName} ({p.gridX},{p.gridY}). Check that Vmangos:ClientDataPath points to a valid WoW 1.12.1 Data directory." });

            if (adt.Textures.Count == 0)
                return Json(new { success = false, error = "ADT parsed but no textures found (MTEX chunk empty)." });

            // Build composite texture (baked RGB — no splat map needed)
            var compositeResult = AdtTerrainReader.BuildCompositeTexture(adt, clientDataPath, cx, cy, radius, pixelsPerChunk: 128);
            if (compositeResult == null)
                return Json(new { success = false, error = "Failed to build composite texture — no chunk data." });

            string compositeBase64 = "data:image/png;base64," + Convert.ToBase64String(compositeResult.PngBytes);

            return Json(new
            {
                success = true,
                compositeBase64,
                compositeWidth = compositeResult.Width,
                compositeHeight = compositeResult.Height,
                chunksWidth = compositeResult.ChunksWidth,
                chunksHeight = compositeResult.ChunksHeight,
                totalAdtTextures = adt.Textures.Count,
                mode = "composite"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VisualLab: VisualTerrainTextures failed for {Preset}", preset);
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// GET /Patch/VisualTerrainCompositePreview?preset=northshire&swap=false&trans=false
    /// Returns the composite texture as a raw PNG image for visual inspection.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainCompositePreview(string? preset, int cx = 8, int cy = 8, int radius = 3,
        int ppc = 64, bool swap = false, bool trans = false)
    {
        preset ??= "northshire";
        var p = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (p.key == null) return BadRequest("bad preset");

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath)) return BadRequest("no client data path");

        string mapName = p.mapId switch { 0 => "Azeroth", 1 => "Kalimdor", _ => $"Map{p.mapId}" };
        var adt = GetOrParseAdt(clientDataPath, mapName, p.gridX, p.gridY);
        if (adt == null) return NotFound("ADT not found");

        var result = AdtTerrainReader.BuildCompositeTexture(adt, clientDataPath, cx, cy, radius,
            pixelsPerChunk: ppc, swapAxes: swap, transposeAlpha: trans);
        if (result == null) return NotFound("Composite build failed");

        return File(result.PngBytes, "image/png");
    }

    /// <summary>
    /// GET /Patch/VisualTerrainCompositeCompare?preset=northshire
    /// HTML page showing composite variants (swap/transpose/CSS rotations)
    /// alongside the minimap tile for visual matching.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainCompositeCompare(string? preset, int cx = 8, int cy = 8, int radius = 3)
    {
        preset ??= "northshire";
        var p = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (p.key == null) return BadRequest("bad preset");

        string mapName = p.mapId switch { 0 => "Azeroth", 1 => "Kalimdor", _ => $"Map{p.mapId}" };
        string minimapUrl = $"/minimap/{mapName}/map{p.gridY:D2}_{p.gridX:D2}.png";

        var html = new System.Text.StringBuilder();
        html.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'><style>");
        html.AppendLine("body { background: #111; color: #eee; font-family: monospace; margin: 16px; }");
        html.AppendLine(".row { display: flex; gap: 12px; flex-wrap: wrap; margin-bottom: 20px; }");
        html.AppendLine(".panel { text-align: center; }");
        html.AppendLine(".panel img { width: 256px; height: 256px; image-rendering: pixelated; border: 1px solid #444; }");
        html.AppendLine(".panel .title { font-size: 12px; margin-bottom: 4px; color: #ff0; }");
        html.AppendLine(".panel .sub { font-size: 10px; color: #888; }");
        html.AppendLine(".ref img { border: 2px solid #0f0; }");
        html.AppendLine("</style></head><body>");
        html.AppendLine($"<h2>Composite Compare &mdash; {preset}</h2>");
        html.AppendLine($"<p>Match the composite against the minimap tile. Look for the road path.</p>");

        // Reference: minimap tile
        html.AppendLine("<h3>Reference (minimap tile — known correct)</h3>");
        html.AppendLine("<div class='row'>");
        html.AppendLine($"<div class='panel ref'><div class='title'>Minimap</div><img src='{minimapUrl}' /><div class='sub'>Full 16x16 tile</div></div>");
        html.AppendLine("</div>");

        // 4 server-side variants: (swap=false,trans=false), (swap=true,trans=false), (swap=false,trans=true), (swap=true,trans=true)
        var variants = new[]
        {
            (false, false, "Default (swap=F, trans=F)"),
            (true, false, "Swap axes (swap=T, trans=F)"),
            (false, true, "Transpose alpha (swap=F, trans=T)"),
            (true, true, "Both (swap=T, trans=T)")
        };

        html.AppendLine("<h3>Server-side variants (4 data variants × 8 CSS rotations = 32)</h3>");

        foreach (var (swap, trans, label) in variants)
        {
            string baseUrl = $"/Patch/VisualTerrainCompositePreview?preset={preset}&cx={cx}&cy={cy}&radius={radius}&swap={swap}&trans={trans}";
            html.AppendLine($"<h4>{label}</h4>");
            html.AppendLine("<div class='row'>");

            var cssTransforms = new[]
            {
                ("Original", "none"),
                ("Rot 90 CW", "rotate(90deg)"),
                ("Rot 180", "rotate(180deg)"),
                ("Rot 90 CCW", "rotate(270deg)"),
                ("Flip H", "scaleX(-1)"),
                ("Flip V", "scaleY(-1)"),
                ("Flip H+90", "scaleX(-1) rotate(90deg)"),
                ("Flip V+90", "scaleY(-1) rotate(90deg)")
            };

            foreach (var (cssLabel, transform) in cssTransforms)
            {
                html.AppendLine($"<div class='panel'><div class='title'>{cssLabel}</div>");
                html.AppendLine($"<img src='{baseUrl}' style='transform:{transform};' loading='lazy' />");
                html.AppendLine($"<div class='sub'>swap={swap} trans={trans}</div></div>");
            }
            html.AppendLine("</div>");
        }

        html.AppendLine("</body></html>");
        return Content(html.ToString(), "text/html");
    }

    // ═══════════════════════════════════════════════════════════════
    // TERRAIN DOODADS — Billboard placement data
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// GET /Patch/VisualTerrainDoodads?preset=northshire&cx=8&cy=8&radius=3
    /// Returns doodad positions classified by type (tree, rock, vegetation, etc.)
    /// for rendering as billboards in the Visual Lab viewer.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainDoodads(string? preset, int cx = 8, int cy = 8, int radius = 3)
    {
        if (string.IsNullOrEmpty(preset))
            return Json(new { success = false, error = "Specify a preset parameter." });

        var p = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (p.key == null)
            return Json(new { success = false, error = $"Unknown terrain preset: {preset}" });

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { success = false, error = "Client data path not configured." });

        string mapName = p.mapId switch { 0 => "Azeroth", 1 => "Kalimdor", _ => $"Map{p.mapId}" };

        try
        {
            var adt = GetOrParseAdt(clientDataPath, mapName, p.gridX, p.gridY);
            if (adt == null)
                return Json(new { success = false, error = "ADT not found." });

            // We need the height transform params from VmangosMapParser to match doodad positions
            // to the existing terrain mesh. Parse the .map file for these values.
            string mapsDir = GetMapsDirectory();
            string mapFilename = VmangosMapParser.BuildFilename(p.mapId, p.gridX, p.gridY);
            string mapPath = Path.Combine(mapsDir, mapFilename);
            float heightScale = 0.15f;
            float midHeight = 0;

            if (System.IO.File.Exists(mapPath))
            {
                var terrainResult = VmangosMapParser.Parse(mapPath, cx, cy, radius);
                if (terrainResult != null)
                {
                    heightScale = terrainResult.HeightScale;
                    midHeight = (terrainResult.MinHeight + terrainResult.MaxHeight) * 0.5f;
                }
            }

            var doodads = AdtTerrainReader.GetDoodadsForRegion(
                adt, cx, cy, radius, heightScale, midHeight, p.gridX, p.gridY);

            // Also get WMOs — transform to Three.js mesh coords like doodads
            var rawWmos = AdtTerrainReader.GetWmosForRegion(adt, heightScale, midHeight);

            // WMO transform: same coord system as doodads
            // posX→col (gridY axis), posZ→row (gridX axis)
            int minCX = Math.Max(0, cx - radius);
            int maxCX = Math.Min(15, cx + radius);
            int minCY = Math.Max(0, cy - radius);
            int maxCY = Math.Min(15, cy + radius);
            int v9StartX = minCX * 8;
            int v9StartY = minCY * 8;
            int v9EndX = maxCX * 8 + 8;
            int v9EndY = maxCY * 8 + 8;
            int vertsW = v9EndX - v9StartX + 1;
            int vertsH = v9EndY - v9StartY + 1;
            float totalWidth = (vertsW - 1) * AdtTerrainReader.CELL_SIZE;
            float totalDepth = (vertsH - 1) * AdtTerrainReader.CELL_SIZE;
            float offsetX = -totalWidth * 0.5f;
            float offsetZ = -totalDepth * 0.5f;

            var wmoTransformed = rawWmos.Select(w => {
                // Transform position to mesh coords
                float col = (w.PosX / AdtTerrainReader.GRID_SIZE - p.gridY) * 128;
                float row = (w.PosZ / AdtTerrainReader.GRID_SIZE - p.gridX) * 128;
                float threeX = offsetX + (col - v9StartX) * AdtTerrainReader.CELL_SIZE;
                float threeY = (w.PosY - midHeight) * heightScale;
                float threeZ = offsetZ + (row - v9StartY) * AdtTerrainReader.CELL_SIZE;

                // Transform bounding box sizes to mesh scale
                float bbSizeX = Math.Abs(w.BbMaxX - w.BbMinX) * AdtTerrainReader.CELL_SIZE / AdtTerrainReader.GRID_SIZE * 128;
                float bbSizeY = Math.Abs(w.BbMaxY - w.BbMinY) * heightScale;
                float bbSizeZ = Math.Abs(w.BbMaxZ - w.BbMinZ) * AdtTerrainReader.CELL_SIZE / AdtTerrainReader.GRID_SIZE * 128;

                return new
                {
                    model = w.ModelPath,
                    x = threeX,
                    y = threeY,
                    z = threeZ,
                    sizeX = bbSizeX,
                    sizeY = bbSizeY,
                    sizeZ = bbSizeZ
                };
            }).ToList();

            // Summary by type for debugging
            var typeCounts = doodads.GroupBy(d => d.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            return Json(new
            {
                success = true,
                doodads = doodads.Select(d => new
                {
                    model = d.ModelPath,
                    type = d.Type,
                    x = d.X,
                    y = d.Y,
                    z = d.Z,
                    rotY = d.RotY,
                    scale = d.Scale
                }),
                wmos = wmoTransformed,
                typeCounts,
                totalDoodads = doodads.Count,
                totalWmos = wmoTransformed.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VisualLab: VisualTerrainDoodads failed for {Preset}", preset);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // DOODAD 3D MODELS — M2 geometry + texture for Three.js rendering
    // ═══════════════════════════════════════════════════════════════

    /// Caches parsed M2 geometry JSON by model path to avoid re-parsing from MPQ.
    private static readonly Dictionary<string, object?> _doodadModelCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _doodadModelCacheLock = new();

    /// <summary>
    /// GET /Patch/VisualTerrainDoodadModel?path=World\TreeTypes\ElwynnTree01.m2
    /// Parses an M2 model from client MPQ and returns geometry (vertices, indices, UVs)
    /// plus the primary texture as base64 PNG — ready for Three.js mesh construction.
    /// No GLB/glTF dependency needed.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainDoodadModel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("path parameter required");

        string cacheKey = path.Replace('/', '\\').ToLowerInvariant();

        // Check cache
        lock (_doodadModelCacheLock)
        {
            if (_doodadModelCache.TryGetValue(cacheKey, out var cached))
            {
                if (cached == null) return NotFound("Model not found (cached miss)");
                return Json(cached);
            }
        }

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return BadRequest("Client data path not configured");

        try
        {
            // ADT MMDX stores paths with .mdx extension, but MPQ files use .m2
            string mpqPath = path;
            if (mpqPath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
                mpqPath = mpqPath[..^4] + ".m2";
            else if (mpqPath.EndsWith(".MDX", StringComparison.OrdinalIgnoreCase))
                mpqPath = mpqPath[..^4] + ".m2";

            // Read M2 from MPQ
            byte[]? m2Data = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, mpqPath);
            if (m2Data == null)
            {
                // Try original path as fallback
                m2Data = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, path);
            }
            if (m2Data == null)
            {
                lock (_doodadModelCacheLock) { _doodadModelCache[cacheKey] = null; }
                return NotFound("M2 not found in MPQs");
            }

            // Parse geometry
            var model = M2Reader.Parse(m2Data);
            if (model == null || !model.IsValid)
            {
                lock (_doodadModelCacheLock) { _doodadModelCache[cacheKey] = null; }
                return NotFound("M2 parse failed or no geometry");
            }

            // Build flat arrays for JSON — positions (xyz), normals (xyz), uvs (uv)
            var positions = new float[model.Vertices.Count * 3];
            var normals = new float[model.Vertices.Count * 3];
            var uvs = new float[model.Vertices.Count * 2];
            for (int i = 0; i < model.Vertices.Count; i++)
            {
                var v = model.Vertices[i];
                positions[i * 3 + 0] = v.PosX;
                positions[i * 3 + 1] = v.PosY;
                positions[i * 3 + 2] = v.PosZ;
                normals[i * 3 + 0] = v.NormX;
                normals[i * 3 + 1] = v.NormY;
                normals[i * 3 + 2] = v.NormZ;
                uvs[i * 2 + 0] = v.TexU;
                uvs[i * 2 + 1] = v.TexV;
            }

            var allIndices = model.Indices.Select(idx => (int)idx).ToArray();

            // Load ALL textures — type-0 has embedded filenames, type-1+ need resolution
            var textureMap = new Dictionary<int, string>(); // texIdx → base64 PNG

            // First: get the M2's directory path for fallback BLP scanning
            string m2Dir = Path.GetDirectoryName(mpqPath)?.Replace('/', '\\') ?? "";

            for (int ti = 0; ti < model.Textures.Count; ti++)
            {
                var tex = model.Textures[ti];

                // Type-0: embedded filename — straightforward
                if (tex.Type == 0 && !string.IsNullOrEmpty(tex.Filename))
                {
                    byte[]? pngBytes = AdtTerrainReader.ReadBlpAsPng(clientDataPath, tex.Filename);
                    if (pngBytes != null)
                    {
                        textureMap[ti] = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
                        continue;
                    }
                    // Try with .blp extension if not present
                    if (!tex.Filename.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                    {
                        pngBytes = AdtTerrainReader.ReadBlpAsPng(clientDataPath, tex.Filename + ".blp");
                        if (pngBytes != null)
                        {
                            textureMap[ti] = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
                            continue;
                        }
                    }
                }

                // Type-1+ (or type-0 with missing filename): try to find BLP by M2 name convention
                // WoW convention: M2 "SomeModel.m2" often has "SomeModel.blp" or "SomeModelSkin.blp" 
                // in the same directory or a Textures subdirectory
                if (!textureMap.ContainsKey(ti) && !string.IsNullOrEmpty(m2Dir))
                {
                    string m2BaseName = Path.GetFileNameWithoutExtension(mpqPath);

                    // Try common naming patterns for type-1 skin textures
                    var candidates = new[]
                    {
                        $"{m2Dir}\\{m2BaseName}.blp",
                        $"{m2Dir}\\{m2BaseName}Skin.blp",
                        $"{m2Dir}\\{m2BaseName}_01.blp",
                        $"{m2Dir}\\{m2BaseName}01.blp",
                        $"{m2Dir}\\Textures\\{m2BaseName}.blp",
                    };

                    foreach (var candidate in candidates)
                    {
                        if (textureMap.ContainsKey(ti)) break;
                        byte[]? pngBytes = AdtTerrainReader.ReadBlpAsPng(clientDataPath, candidate);
                        if (pngBytes != null)
                        {
                            textureMap[ti] = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
                        }
                    }
                }
            }

            // Build submesh→texture mapping from batches
            // Note: some M2s (especially doodads) have a degenerate TextureLookup (all zeros).
            // In that case, batch.TextureIndex should be used directly as the texture index.
            var submeshTexMap = new Dictionary<int, int>();
            bool lookupIsDegenerate = model.TextureLookup.Count == 0 ||
                                      model.TextureLookup.All(x => x == model.TextureLookup[0]);

            foreach (var batch in model.Batches)
            {
                int subIdx = batch.SubmeshIndex;
                if (submeshTexMap.ContainsKey(subIdx)) continue;

                int texIdx;
                if (lookupIsDegenerate)
                {
                    // Degenerate lookup — use TextureIndex directly as texture index
                    texIdx = batch.TextureIndex;
                }
                else
                {
                    // Normal lookup chain: batch.TextureIndex → TextureLookup[idx] → texture
                    texIdx = batch.TextureIndex < model.TextureLookup.Count
                        ? model.TextureLookup[batch.TextureIndex]
                        : batch.TextureIndex;
                }

                submeshTexMap[subIdx] = texIdx;
            }

            // Build per-submesh index groups with texture assignments
            var submeshes = new List<object>();
            if (model.Submeshes.Count > 0)
            {
                for (int si = 0; si < model.Submeshes.Count; si++)
                {
                    var sub = model.Submeshes[si];
                    int texIdx = submeshTexMap.ContainsKey(si) ? submeshTexMap[si] : -1;

                    // Try exact texture match first, then fallback to first available
                    string? texBase64 = null;
                    int resolvedTexIdx = texIdx;
                    if (texIdx >= 0 && textureMap.ContainsKey(texIdx))
                    {
                        texBase64 = textureMap[texIdx];
                    }
                    else if (textureMap.Count > 0)
                    {
                        // Fallback: use the texture closest to this submesh index
                        resolvedTexIdx = textureMap.Keys.OrderBy(k => Math.Abs(k - si)).First();
                        texBase64 = textureMap[resolvedTexIdx];
                    }

                    submeshes.Add(new
                    {
                        indexStart = (int)sub.IndexStart,
                        indexCount = (int)sub.IndexCount,
                        textureBase64 = texBase64,
                        texIdx,
                        resolvedTexIdx,
                        batchMapped = submeshTexMap.ContainsKey(si)
                    });
                }
            }
            else
            {
                // No submesh info — single group with all indices
                submeshes.Add(new
                {
                    indexStart = 0,
                    indexCount = allIndices.Length,
                    textureBase64 = textureMap.Count > 0 ? textureMap.Values.First() : (string?)null,
                    texIdx = 0,
                    resolvedTexIdx = 0,
                    batchMapped = false
                });
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
                texturesResolved = textureMap.Count,
                textureDebug = model.Textures.Select((t, i) => new
                {
                    index = i,
                    type = t.Type,
                    filename = t.Filename,
                    resolved = textureMap.ContainsKey(i)
                }),
                batchDebug = model.Batches.Select((b, i) => new
                {
                    batchIdx = i,
                    submeshIdx = (int)b.SubmeshIndex,
                    texLookupIdx = (int)b.TextureIndex,
                    resolvedTexIdx = b.TextureIndex < model.TextureLookup.Count ? (int)model.TextureLookup[b.TextureIndex] : -1
                }),
                textureLookup = model.TextureLookup.Select(x => (int)x).ToArray()
            };

            lock (_doodadModelCacheLock) { _doodadModelCache[cacheKey] = result; }
            return Json(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VisualLab: DoodadModel failed for {Path}", path);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // TERRAIN BLP — Decode ground textures from MPQ
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// GET /Patch/VisualTerrainBlp?file=Tileset\Elwynn\ElwynnGrass01.blp
    /// Reads a ground tileset BLP from the client's MPQ archives,
    /// decodes it to PNG, and returns it as image/png.
    ///
    /// Unlike VisualTexture (which reads from extracted RawBlpPath),
    /// this endpoint reads directly from MPQ archives — no extraction needed.
    /// Cached in-memory since terrain textures never change.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainBlp(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return BadRequest("File parameter required.");

        // Normalize to lowercase for cache key
        string cacheKey = file.Replace('/', '\\').ToLowerInvariant();

        // Check cache
        lock (_terrainBlpCacheLock)
        {
            if (_terrainBlpCache.TryGetValue(cacheKey, out byte[]? cached))
                return File(cached, "image/png");
        }

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return NotFound("Client data path not configured.");

        try
        {
            byte[]? pngBytes = AdtTerrainReader.ReadBlpAsPng(clientDataPath, file);
            if (pngBytes == null)
            {
                // Try with normalized slashes
                string altPath = file.Replace('/', '\\');
                pngBytes = AdtTerrainReader.ReadBlpAsPng(clientDataPath, altPath);
            }

            if (pngBytes == null)
                return NotFound($"BLP not found in MPQs: {file}");

            // Cache it
            lock (_terrainBlpCacheLock)
            {
                _terrainBlpCache[cacheKey] = pngBytes;
            }

            return File(pngBytes, "image/png");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VisualLab: VisualTerrainBlp failed for {File}", file);
            return StatusCode(500, $"BLP decode failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // TERRAIN DOODAD DIAGNOSTICS — coordinate transform debugging
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// GET /Patch/VisualTerrainDoodadDebug?preset=northshire
    /// Dumps raw MDDF coordinates, computed local positions, and mesh bounds
    /// to help diagnose doodad placement issues. Remove when fixed.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainDoodadDebug(string? preset, int cx = 8, int cy = 8, int radius = 3)
    {
        preset ??= "northshire";
        var p = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (p.key == null) return Json(new { error = "bad preset" });

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { error = "no client data path" });

        string mapName = p.mapId switch { 0 => "Azeroth", 1 => "Kalimdor", _ => $"Map{p.mapId}" };

        var adt = GetOrParseAdt(clientDataPath, mapName, p.gridX, p.gridY);
        if (adt == null)
            return Json(new { error = "ADT not found" });

        // Get height params from .map file
        string mapsDir = GetMapsDirectory();
        string mapFilename = VmangosMapParser.BuildFilename(p.mapId, p.gridX, p.gridY);
        string mapPath = Path.Combine(mapsDir, mapFilename);
        float heightScale = 0.15f;
        float midHeight = 0;

        if (System.IO.File.Exists(mapPath))
        {
            var terrainResult = VmangosMapParser.Parse(mapPath, cx, cy, radius);
            if (terrainResult != null)
            {
                heightScale = terrainResult.HeightScale;
                midHeight = (terrainResult.MinHeight + terrainResult.MaxHeight) * 0.5f;
            }
        }

        var diag = AdtTerrainReader.GetDoodadDiagnostics(
            adt, cx, cy, radius, heightScale, midHeight, p.gridX, p.gridY);

        // Also parse terrain to get V9 height range for comparison
        float terrainMinH = 0, terrainMaxH = 0;
        if (System.IO.File.Exists(mapPath))
        {
            var terrainResult = VmangosMapParser.Parse(mapPath, cx, cy, radius);
            if (terrainResult != null)
            {
                terrainMinH = terrainResult.MinHeight;
                terrainMaxH = terrainResult.MaxHeight;
            }
        }

        return Json(new
        {
            preset,
            mapName,
            gridX = p.gridX,
            gridY = p.gridY,
            adtFile = $"{mapName}_{p.gridY}_{p.gridX}.adt (Map_gx_gy = Map_gridY_gridX)",
            mapFile = mapFilename,
            terrainV9Heights = new { min = terrainMinH, max = terrainMaxH, mid = midHeight },
            diagnostics = diag
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // CHUNK ORDER DIAGNOSTIC — determine MCNK axis mapping
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// GET /Patch/VisualTerrainChunkOrder?preset=barrens
    /// Dumps MCNK IndexX/IndexY for sequential chunks to determine
    /// how chunks[cy*16+cx] maps to the V9 grid axes.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainChunkOrder(string? preset)
    {
        preset ??= "barrens";
        var p = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (p.key == null) return Json(new { error = "bad preset" });

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { error = "no client data path" });

        string mapName = p.mapId switch { 0 => "Azeroth", 1 => "Kalimdor", _ => $"Map{p.mapId}" };

        var adt = GetOrParseAdt(clientDataPath, mapName, p.gridX, p.gridY);
        if (adt == null)
            return Json(new { error = "ADT not found", mapName, gridX = p.gridX, gridY = p.gridY });

        if (adt.Chunks == null || adt.Chunks.Length == 0)
            return Json(new { error = "No chunks parsed" });

        // Dump first 20 chunks showing sequential index → IndexX, IndexY, texture layers
        var chunkDump = new List<object>();
        for (int i = 0; i < Math.Min(20, adt.Chunks.Length); i++)
        {
            var c = adt.Chunks[i];
            if (c == null) { chunkDump.Add(new { seq = i, isNull = true }); continue; }
            chunkDump.Add(new
            {
                seq = i,
                indexX = c.IndexX,
                indexY = c.IndexY,
                layerCount = c.Layers?.Length ?? 0,
                layerTexIndices = c.Layers?.Select(l => l.TextureIndex).ToArray(),
            });
        }

        // Also dump chunks at key positions to verify pattern
        var keyPositions = new[] { 0, 1, 2, 15, 16, 17, 32, 255 };
        var keyChunks = new List<object>();
        foreach (int i in keyPositions)
        {
            if (i >= adt.Chunks.Length) continue;
            var c = adt.Chunks[i];
            if (c == null) continue;
            keyChunks.Add(new { seq = i, indexX = c.IndexX, indexY = c.IndexY });
        }

        // Determine the pattern
        var c0 = adt.Chunks[0];
        var c1 = adt.Chunks.Length > 1 ? adt.Chunks[1] : null;
        var c16 = adt.Chunks.Length > 16 ? adt.Chunks[16] : null;
        string pattern = "unknown";
        if (c0 != null && c1 != null)
        {
            if (c1.IndexY == c0.IndexY + 1 && c1.IndexX == c0.IndexX)
                pattern = "IndexY increments first (fast axis). chunks[n]: IndexX=n/16, IndexY=n%16";
            else if (c1.IndexX == c0.IndexX + 1 && c1.IndexY == c0.IndexY)
                pattern = "IndexX increments first (fast axis). chunks[n]: IndexY=n/16, IndexX=n%16";
        }

        // Current BuildSplatMap uses chunks[cy*16+cx]
        // cx→pixOffX (horizontal), cy→pixOffY (vertical)
        // For the splat to align with V9 mesh:
        //   cx must select the V9 column axis (vx → Three.js X)
        //   cy must select the V9 row axis (vy → Three.js Z)
        // So we need to know: does IndexX=column or IndexX=row?

        return Json(new
        {
            preset,
            gridX = p.gridX,
            gridY = p.gridY,
            totalChunks = adt.Chunks.Length,
            textures = adt.Textures,
            pattern,
            keyChunks,
            firstChunks = chunkDump,
            note = "BuildSplatMap uses chunks[cy*16+cx]. cx=horizontal, cy=vertical. If IndexY is fast axis (n%16), then chunks[cy*16+cx] has IndexX=cy, IndexY=cx."
        });
    }

    /// <summary>
    /// GET /Patch/VisualTerrainAlphaDump?preset=northshire&chunkX=8&chunkY=8
    /// Returns HTML page showing raw alpha maps for a single chunk as greyscale images.
    /// Also shows the alpha data read with stride 32 vs stride 64 for comparison.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainAlphaDump(string? preset, int chunkX = 8, int chunkY = 8)
    {
        preset ??= "northshire";
        var p = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (p.key == null) return BadRequest("bad preset");

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath)) return BadRequest("no client data path");

        string mapName = p.mapId switch { 0 => "Azeroth", 1 => "Kalimdor", _ => $"Map{p.mapId}" };
        var adt = GetOrParseAdt(clientDataPath, mapName, p.gridX, p.gridY);
        if (adt == null) return NotFound("ADT not found");

        int chunkIdx = chunkY * 16 + chunkX;
        if (adt.Chunks == null || chunkIdx >= adt.Chunks.Length)
            return NotFound("Chunk not found");

        var chunk = adt.Chunks[chunkIdx];
        if (chunk?.Layers == null)
            return NotFound("No layers");

        var html = new System.Text.StringBuilder();
        html.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'><style>");
        html.AppendLine("body { background: #111; color: #eee; font-family: monospace; margin: 16px; }");
        html.AppendLine(".row { display: flex; gap: 16px; flex-wrap: wrap; margin-bottom: 16px; }");
        html.AppendLine(".panel { text-align: center; }");
        html.AppendLine(".panel img { width: 256px; height: 256px; image-rendering: pixelated; border: 1px solid #444; }");
        html.AppendLine(".panel .title { font-size: 12px; color: #ff0; margin-bottom: 4px; }");
        html.AppendLine(".panel .info { font-size: 10px; color: #888; }");
        html.AppendLine("</style></head><body>");
        html.AppendLine($"<h2>Alpha Dump — chunk ({chunkX},{chunkY}) idx {chunkIdx}</h2>");
        html.AppendLine($"<p>IndexX={chunk.IndexX}, IndexY={chunk.IndexY}, Layers={chunk.Layers.Length}</p>");

        for (int li = 0; li < chunk.Layers.Length; li++)
        {
            var layer = chunk.Layers[li];
            string texName = layer.TextureIndex < adt.Textures.Count
                ? System.IO.Path.GetFileName(adt.Textures[layer.TextureIndex]) : "???";

            html.AppendLine($"<h3>Layer {li}: tex{layer.TextureIndex} ({texName}) — flags=0x{layer.Flags:X}, alphaSize={layer.AlphaMap?.Length ?? 0}</h3>");

            if (layer.AlphaMap == null || layer.AlphaMap.Length == 0)
            {
                html.AppendLine("<p>No alpha map (base layer)</p>");
                continue;
            }

            html.AppendLine("<div class='row'>");

            // Render alpha as greyscale 64×64 (stride 64)
            {
                var px = new byte[64 * 64 * 4];
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        int idx = y * 64 + x;
                        byte val = idx < layer.AlphaMap.Length ? layer.AlphaMap[idx] : (byte)0;
                        int pi = (y * 64 + x) * 4;
                        px[pi] = val; px[pi + 1] = val; px[pi + 2] = val; px[pi + 3] = 255;
                    }
                }
                using var bmp = new SkiaSharp.SKBitmap(64, 64, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
                layer.AlphaMap.Length.ToString(); // just to use it
                var span = bmp.GetPixelSpan();
                px.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span));
                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                using var enc = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                string b64 = "data:image/png;base64," + Convert.ToBase64String(enc.ToArray());
                html.AppendLine($"<div class='panel'><div class='title'>Stride 64 (64×64)</div><img src='{b64}' />");
                // Show first 10 row checksums
                var rowSums = new List<int>();
                for (int r = 0; r < Math.Min(8, 64); r++)
                {
                    int sum = 0;
                    for (int c = 0; c < 64; c++) { int ii = r * 64 + c; if (ii < layer.AlphaMap.Length) sum += layer.AlphaMap[ii]; }
                    rowSums.Add(sum);
                }
                html.AppendLine($"<div class='info'>Row sums[0-7]: {string.Join(", ", rowSums)}</div></div>");
            }

            // Render alpha as greyscale with stride 32 → 32×128 → show as 64×64 (upscaled)
            if (layer.AlphaMap.Length >= 4096)
            {
                var px = new byte[64 * 64 * 4];
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        // Read with stride 32: row y, col x/2
                        int srcRow = y;
                        int srcCol = x / 2;
                        int idx = srcRow * 32 + srcCol;
                        byte val = idx < layer.AlphaMap.Length ? layer.AlphaMap[idx] : (byte)0;
                        int pi = (y * 64 + x) * 4;
                        px[pi] = val; px[pi + 1] = val; px[pi + 2] = val; px[pi + 3] = 255;
                    }
                }
                using var bmp = new SkiaSharp.SKBitmap(64, 64, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
                var span = bmp.GetPixelSpan();
                px.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span));
                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                using var enc = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                string b64 = "data:image/png;base64," + Convert.ToBase64String(enc.ToArray());
                html.AppendLine($"<div class='panel'><div class='title'>Stride 32 (upscaled 32→64)</div><img src='{b64}' /></div>");
            }

            // Render transposed: stride 64 but swap px/py
            {
                var px = new byte[64 * 64 * 4];
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        int idx = x * 64 + y; // transposed
                        byte val = idx < layer.AlphaMap.Length ? layer.AlphaMap[idx] : (byte)0;
                        int pi = (y * 64 + x) * 4;
                        px[pi] = val; px[pi + 1] = val; px[pi + 2] = val; px[pi + 3] = 255;
                    }
                }
                using var bmp = new SkiaSharp.SKBitmap(64, 64, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
                var span = bmp.GetPixelSpan();
                px.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span));
                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                using var enc = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                string b64 = "data:image/png;base64," + Convert.ToBase64String(enc.ToArray());
                html.AppendLine($"<div class='panel'><div class='title'>Transposed (stride 64, swap XY)</div><img src='{b64}' /></div>");
            }

            // Raw bytes dump — first 128 bytes as hex
            html.AppendLine($"<div class='panel'><div class='title'>Raw hex (first 128 bytes)</div>");
            html.AppendLine("<pre style='font-size:9px; text-align:left; max-width:500px; overflow:auto;'>");
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 32; c++)
                {
                    int idx = r * 32 + c;
                    if (idx < layer.AlphaMap.Length)
                        html.Append($"{layer.AlphaMap[idx]:X2} ");
                }
                html.AppendLine();
            }
            html.AppendLine("</pre></div>");

            html.AppendLine("</div>");
        }

        html.AppendLine("</body></html>");
        return Content(html.ToString(), "text/html");
    }

    /// <summary>
    /// GET /Patch/VisualTerrainChunkMap?preset=northshire
    /// Renders each chunk as a unique solid color based on its array index.
    /// No textures, no alpha — pure placement diagnostic.
    /// Each chunk also has its index number rendered as text.
    /// </summary>
    [HttpGet]
    public IActionResult VisualTerrainChunkMap(string? preset, int cx = 8, int cy = 8, int radius = 3)
    {
        preset ??= "northshire";
        var p = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (p.key == null) return BadRequest("bad preset");

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath)) return BadRequest("no client data path");

        string mapName = p.mapId switch { 0 => "Azeroth", 1 => "Kalimdor", _ => $"Map{p.mapId}" };
        var adt = GetOrParseAdt(clientDataPath, mapName, p.gridX, p.gridY);
        if (adt == null) return NotFound("ADT not found");

        int minCX = Math.Max(0, cx - radius);
        int maxCX = Math.Min(15, cx + radius);
        int minCY = Math.Max(0, cy - radius);
        int maxCY = Math.Min(15, cy + radius);

        int chunksW = maxCX - minCX + 1;
        int chunksH = maxCY - minCY + 1;
        int ppc = 64;
        int pixW = chunksW * ppc;
        int pixH = chunksH * ppc;
        var pixels = new byte[pixW * pixH * 4];

        var info = new System.Text.StringBuilder();
        info.AppendLine($"Grid: cx={cx}, cy={cy}, r={radius} → chunks ({minCX}-{maxCX}) x ({minCY}-{maxCY})");

        for (int ccy = minCY; ccy <= maxCY; ccy++)
        {
            for (int ccx = minCX; ccx <= maxCX; ccx++)
            {
                int chunkIdx = ccy * 16 + ccx;

                // Unique color from chunk index
                int hash = (int)(chunkIdx * 2654435761u); // knuth hash
                byte cr = (byte)((hash >> 16) & 0xFF | 0x40);
                byte cg = (byte)((hash >> 8) & 0xFF | 0x40);
                byte cb = (byte)((hash) & 0xFF | 0x40);

                int pixOffX = (ccx - minCX) * ppc;
                int pixOffY = (ccy - minCY) * ppc;

                string label = $"{chunkIdx}";
                var chunk = (adt.Chunks != null && chunkIdx < adt.Chunks.Length) ? adt.Chunks[chunkIdx] : null;
                if (chunk != null)
                    label += $" ({chunk.IndexX},{chunk.IndexY}) L{chunk.Layers?.Length ?? 0}";

                info.AppendLine($"  pos({ccx},{ccy}) → chunks[{chunkIdx}] → color({cr},{cg},{cb}) idx=({chunk?.IndexX},{chunk?.IndexY})");

                for (int py = 0; py < ppc; py++)
                {
                    for (int px = 0; px < ppc; px++)
                    {
                        int pi = ((pixOffY + py) * pixW + (pixOffX + px)) * 4;
                        // Border
                        if (px == 0 || py == 0 || px == ppc - 1 || py == ppc - 1)
                        {
                            pixels[pi] = 0; pixels[pi + 1] = 0; pixels[pi + 2] = 0; pixels[pi + 3] = 255;
                        }
                        else
                        {
                            pixels[pi] = cr; pixels[pi + 1] = cg; pixels[pi + 2] = cb; pixels[pi + 3] = 255;
                        }
                    }
                }

                // Render chunk index as simple pixel text (top-left of chunk)
                // Simple 3x5 digit font
                int textX = pixOffX + 4;
                int textY = pixOffY + 4;
                foreach (char ch in label)
                {
                    if (textX + 4 >= pixOffX + ppc) break;
                    var glyph = GetSimpleGlyph(ch);
                    if (glyph != null)
                    {
                        for (int gy = 0; gy < 5 && textY + gy < pixOffY + ppc; gy++)
                        {
                            for (int gx = 0; gx < 3 && textX + gx < pixOffX + ppc; gx++)
                            {
                                if ((glyph[gy] & (4 >> gx)) != 0)
                                {
                                    int pi = ((textY + gy) * pixW + (textX + gx)) * 4;
                                    pixels[pi] = 255; pixels[pi + 1] = 255; pixels[pi + 2] = 255; pixels[pi + 3] = 255;
                                }
                            }
                        }
                    }
                    textX += 4;
                }
            }
        }

        byte[] pngBytes;
        using (var bitmap = new SkiaSharp.SKBitmap(pixW, pixH, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul))
        {
            var span = bitmap.GetPixelSpan();
            pixels.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span));
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            pngBytes = encoded.ToArray();
        }

        // Return as HTML with image + text info
        var html = $@"<!DOCTYPE html><html><head><style>body{{background:#111;color:#eee;font-family:monospace;}}</style></head><body>
<h2>Chunk Map — {preset}</h2>
<p>Each chunk = unique color based on array index. Black borders = chunk edges.</p>
<img src='data:image/png;base64,{Convert.ToBase64String(pngBytes)}' style='image-rendering:pixelated; width:700px;' />
<pre>{info}</pre>
</body></html>";
        return Content(html, "text/html");
    }

    private static int[]? GetSimpleGlyph(char c)
    {
        return c switch
        {
            '0' => new[] { 7, 5, 5, 5, 7 },
            '1' => new[] { 2, 6, 2, 2, 7 },
            '2' => new[] { 7, 1, 7, 4, 7 },
            '3' => new[] { 7, 1, 7, 1, 7 },
            '4' => new[] { 5, 5, 7, 1, 1 },
            '5' => new[] { 7, 4, 7, 1, 7 },
            '6' => new[] { 7, 4, 7, 5, 7 },
            '7' => new[] { 7, 1, 2, 4, 4 },
            '8' => new[] { 7, 5, 7, 5, 7 },
            '9' => new[] { 7, 5, 7, 1, 7 },
            '(' => new[] { 1, 2, 2, 2, 1 },
            ')' => new[] { 4, 2, 2, 2, 4 },
            ',' => new[] { 0, 0, 0, 2, 4 },
            'L' => new[] { 4, 4, 4, 4, 7 },
            ' ' => new[] { 0, 0, 0, 0, 0 },
            _ => null
        };
    }

    [HttpGet]
    public IActionResult VisualTerrainDebug(string? preset)
    {
        preset ??= "northshire";
        var p = _terrainPresets.FirstOrDefault(x => x.key == preset);
        if (p.key == null) return Json(new { error = "bad preset" });

        string clientDataPath = GetClientDataDirectory();
        if (string.IsNullOrEmpty(clientDataPath))
            return Json(new { error = "no client data path" });

        string mapName = p.mapId == 0 ? "Azeroth" : "Kalimdor";
        string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{p.gridX}_{p.gridY}.adt";

        byte[]? adtBytes = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtPath);
        if (adtBytes == null)
            return Json(new { clientDataPath, adtPath, error = "ADT not found in any MPQ" });

        // Scan first 15 chunks
        var chunkScan = new List<object>();
        int pos = 0, count = 0;
        while (pos + 8 <= adtBytes.Length && count < 15)
        {
            string magic = System.Text.Encoding.ASCII.GetString(adtBytes, pos, 4);
            uint size = BitConverter.ToUInt32(adtBytes, pos + 4);
            chunkScan.Add(new { offset = pos, magic, size });
            if (size > (uint)adtBytes.Length) break;
            pos += 8 + (int)size;
            count++;
        }

        string hexDump = BitConverter.ToString(adtBytes, 0, Math.Min(64, adtBytes.Length));

        return Json(new { clientDataPath, adtPath, adtSize = adtBytes.Length, hexDump, chunkScan });
    }

    // Add this endpoint to PatchController.VisualLab.cs temporarily

    [HttpGet]
    public IActionResult VisualTerrainDebug3()
    {
        var report = new Dictionary<string, object>();

        string clientDataPath = GetClientDataDirectory();
        report["clientDataPath"] = clientDataPath;

        if (string.IsNullOrEmpty(clientDataPath))
        {
            report["error"] = "no client data path";
            return Json(report);
        }

        string mapName = "Azeroth";
        int gridX = 32, gridY = 48;
        string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{gridX}_{gridY}.adt";
        report["adtPath"] = adtPath;

        byte[]? data = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, adtPath);
        if (data == null)
        {
            report["error"] = "ADT not found in MPQs";
            return Json(report);
        }

        report["adtSize"] = data.Length;
        report["first80hex"] = BitConverter.ToString(data, 0, Math.Min(80, data.Length));

        // ═══════════════════════════════════════════════════════════
        // 1. FULL TOP-LEVEL CHUNK SCAN
        // ═══════════════════════════════════════════════════════════
        var topChunks = new List<object>();
        int pos = 0;
        int mcnkCount = 0;
        int firstMcnkOffset = -1;
        while (pos + 8 <= data.Length)
        {
            string magic = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            uint size = BitConverter.ToUInt32(data, pos + 4);
            if (size > (uint)data.Length) { topChunks.Add(new { offset = pos, magic, size, error = "bogus size" }); break; }

            if (magic == "KNCM")
            {
                mcnkCount++;
                if (firstMcnkOffset < 0) firstMcnkOffset = pos;
                // Only log first 3 MCNKs in top-level scan
                if (mcnkCount <= 3)
                    topChunks.Add(new { offset = pos, magic, size, note = $"MCNK #{mcnkCount}" });
            }
            else
            {
                topChunks.Add(new { offset = pos, magic, size });
            }

            pos += 8 + (int)size;
        }
        topChunks.Add(new { offset = -1, magic = "SUMMARY", size = (uint)0, note = $"Total MCNKs: {mcnkCount}" });
        report["topLevelChunks"] = topChunks;

        // ═══════════════════════════════════════════════════════════
        // 2. MTEX CONTENT (what textures does this ADT reference?)
        // ═══════════════════════════════════════════════════════════
        // Find MTEX (reversed = XETM)
        pos = 0;
        while (pos + 8 <= data.Length)
        {
            string magic = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            uint size = BitConverter.ToUInt32(data, pos + 4);
            if (magic == "XETM")
            {
                int mtexData = pos + 8;
                // Parse null-separated strings
                var texNames = new List<string>();
                int strStart = mtexData;
                int mtexEnd = mtexData + (int)size;
                for (int i = mtexData; i < mtexEnd && i < data.Length; i++)
                {
                    if (data[i] == 0)
                    {
                        if (i > strStart)
                            texNames.Add(System.Text.Encoding.ASCII.GetString(data, strStart, i - strStart));
                        strStart = i + 1;
                    }
                }
                report["mtexCount"] = texNames.Count;
                report["mtexTextures"] = texNames;
                report["mtexRawFirst100"] = BitConverter.ToString(data, mtexData, Math.Min(100, (int)size));
                break;
            }
            pos += 8 + (int)size;
        }

        // ═══════════════════════════════════════════════════════════
        // 3. FIRST MCNK DEEP DIVE
        // ═══════════════════════════════════════════════════════════
        if (firstMcnkOffset >= 0)
        {
            var mcnk = new Dictionary<string, object>();
            int mcnkPos = firstMcnkOffset;
            uint mcnkSize = BitConverter.ToUInt32(data, mcnkPos + 4);
            int mcnkData = mcnkPos + 8;
            int mcnkEnd = mcnkData + (int)mcnkSize;
            mcnk["fileOffset"] = mcnkPos;
            mcnk["dataOffset"] = mcnkData;
            mcnk["size"] = mcnkSize;

            // MCNK header (128 bytes) — dump key fields
            mcnk["hdr_flags"] = BitConverter.ToUInt32(data, mcnkData + 0x00);
            mcnk["hdr_indexX"] = BitConverter.ToUInt32(data, mcnkData + 0x04);
            mcnk["hdr_indexY"] = BitConverter.ToUInt32(data, mcnkData + 0x08);
            mcnk["hdr_nLayers"] = BitConverter.ToUInt32(data, mcnkData + 0x0C);
            mcnk["hdr_nDoodadRefs"] = BitConverter.ToUInt32(data, mcnkData + 0x10);
            mcnk["hdr_ofsHeight"] = BitConverter.ToUInt32(data, mcnkData + 0x14);
            mcnk["hdr_ofsNormal"] = BitConverter.ToUInt32(data, mcnkData + 0x18);
            mcnk["hdr_ofsLayer"] = BitConverter.ToUInt32(data, mcnkData + 0x1C);
            mcnk["hdr_ofsRefs"] = BitConverter.ToUInt32(data, mcnkData + 0x20);
            mcnk["hdr_ofsAlpha"] = BitConverter.ToUInt32(data, mcnkData + 0x24);
            mcnk["hdr_sizeAlpha"] = BitConverter.ToUInt32(data, mcnkData + 0x28);
            mcnk["hdr_ofsShadow"] = BitConverter.ToUInt32(data, mcnkData + 0x2C);
            mcnk["hdr_sizeShadow"] = BitConverter.ToUInt32(data, mcnkData + 0x30);

            // Raw header hex (first 128 bytes of MCNK data)
            mcnk["headerHex"] = BitConverter.ToString(data, mcnkData, Math.Min(128, (int)mcnkSize));

            // What's at each offset?
            uint ofsHeight = BitConverter.ToUInt32(data, mcnkData + 0x14);
            uint ofsLayer = BitConverter.ToUInt32(data, mcnkData + 0x1C);
            uint ofsAlpha = BitConverter.ToUInt32(data, mcnkData + 0x24);

            // Check: are offsets relative to mcnkData or to mcnkPos (the chunk header)?
            // Try both and report what magic we find
            var offsetTests = new Dictionary<string, object>();

            // Test ofsLayer relative to mcnkData (what I currently do)
            if (ofsLayer > 0 && ofsLayer < mcnkSize)
            {
                int testA = mcnkData + (int)ofsLayer;
                if (testA + 8 <= data.Length)
                {
                    string magicA = System.Text.Encoding.ASCII.GetString(data, testA, 4);
                    uint sizeA = BitConverter.ToUInt32(data, testA + 4);
                    offsetTests["ofsLayer_relToData"] = new { absPos = testA, magic = magicA, size = sizeA };
                }
            }
            // Test ofsLayer relative to mcnkPos (the IFF chunk start including magic+size)
            if (ofsLayer > 0)
            {
                int testB = mcnkPos + (int)ofsLayer;
                if (testB + 8 <= data.Length)
                {
                    string magicB = System.Text.Encoding.ASCII.GetString(data, testB, 4);
                    uint sizeB = BitConverter.ToUInt32(data, testB + 4);
                    offsetTests["ofsLayer_relToChunkStart"] = new { absPos = testB, magic = magicB, size = sizeB };
                }
            }
            // Test ofsHeight both ways
            if (ofsHeight > 0 && ofsHeight < mcnkSize)
            {
                int testA = mcnkData + (int)ofsHeight;
                if (testA + 8 <= data.Length)
                {
                    string magicA = System.Text.Encoding.ASCII.GetString(data, testA, 4);
                    offsetTests["ofsHeight_relToData"] = new { absPos = testA, magic = magicA };
                }
                int testB = mcnkPos + (int)ofsHeight;
                if (testB + 8 <= data.Length)
                {
                    string magicB = System.Text.Encoding.ASCII.GetString(data, testB, 4);
                    offsetTests["ofsHeight_relToChunkStart"] = new { absPos = testB, magic = magicB };
                }
            }
            // Test ofsAlpha both ways
            if (ofsAlpha > 0 && ofsAlpha < mcnkSize)
            {
                int testA = mcnkData + (int)ofsAlpha;
                if (testA + 8 <= data.Length)
                {
                    string magicA = System.Text.Encoding.ASCII.GetString(data, testA, 4);
                    offsetTests["ofsAlpha_relToData"] = new { absPos = testA, magic = magicA };
                }
                int testB = mcnkPos + (int)ofsAlpha;
                if (testB + 8 <= data.Length)
                {
                    string magicB = System.Text.Encoding.ASCII.GetString(data, testB, 4);
                    offsetTests["ofsAlpha_relToChunkStart"] = new { absPos = testB, magic = magicB };
                }
            }
            mcnk["offsetTests"] = offsetTests;

            // Brute-force sub-chunk scan within MCNK (skip 128-byte header)
            var subChunks = new List<object>();
            int scanPos = mcnkData + 128;
            while (scanPos + 8 <= mcnkEnd && subChunks.Count < 20)
            {
                string subMagic = System.Text.Encoding.ASCII.GetString(data, scanPos, 4);
                uint subSize = BitConverter.ToUInt32(data, scanPos + 4);
                int relOfs = scanPos - mcnkData;
                subChunks.Add(new { relOffset = relOfs, absOffset = scanPos, magic = subMagic, size = subSize });
                if (subSize > mcnkSize || scanPos + 8 + subSize > mcnkEnd) break;
                scanPos += 8 + (int)subSize;
            }
            mcnk["subChunksScan"] = subChunks;

            report["firstMcnk"] = mcnk;
        }

        // ═══════════════════════════════════════════════════════════
        // 4. TEST AdtTerrainReader.Parse() results
        // ═══════════════════════════════════════════════════════════
        var adt = AdtTerrainReader.ReadFromMpq(clientDataPath, mapName, gridX, gridY);
        if (adt != null)
        {
            var parseResult = new Dictionary<string, object>();
            parseResult["textureCount"] = adt.Textures.Count;
            parseResult["textures"] = adt.Textures;
            parseResult["doodadCount"] = adt.Doodads?.Count ?? 0;
            parseResult["wmoCount"] = adt.Wmos?.Count ?? 0;
            parseResult["chunkCount"] = adt.Chunks?.Length ?? 0;

            // Check first 5 chunks for layer data
            if (adt.Chunks != null)
            {
                var chunkSamples = new List<object>();
                for (int i = 0; i < Math.Min(5, adt.Chunks.Length); i++)
                {
                    var c = adt.Chunks[i];
                    if (c == null) { chunkSamples.Add(new { index = i, isNull = true }); continue; }
                    chunkSamples.Add(new
                    {
                        index = i,
                        indexX = c.IndexX,
                        indexY = c.IndexY,
                        layerCount = c.Layers?.Length ?? 0,
                        layers = c.Layers?.Select(l => new
                        {
                            textureIndex = l.TextureIndex,
                            flags = l.Flags,
                            offsetInMcal = l.OffsetInMcal,
                            hasAlpha = l.AlphaMap != null,
                            alphaSize = l.AlphaMap?.Length ?? 0
                        })
                    });
                }
                parseResult["chunkSamples"] = chunkSamples;
            }

            report["parseResult"] = parseResult;
        }
        else
        {
            report["parseResult"] = "Parse returned null";
        }

        // ═══════════════════════════════════════════════════════════
        // 5. TEST BLP READING (try first MTEX texture)
        // ═══════════════════════════════════════════════════════════
        if (adt?.Textures?.Count > 0)
        {
            var blpTest = new Dictionary<string, object>();
            string firstTex = adt.Textures[0];
            blpTest["filename"] = firstTex;

            byte[]? blpBytes = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, firstTex);
            blpTest["foundInMpq"] = blpBytes != null;
            blpTest["blpSize"] = blpBytes?.Length ?? 0;

            if (blpBytes != null && blpBytes.Length > 20)
            {
                blpTest["first20hex"] = BitConverter.ToString(blpBytes, 0, 20);
                blpTest["magic"] = System.Text.Encoding.ASCII.GetString(blpBytes, 0, 4);
            }

            // Try decoding to PNG
            try
            {
                byte[]? png = AdtTerrainReader.ReadBlpAsPng(clientDataPath, firstTex);
                blpTest["pngDecodeSuccess"] = png != null;
                blpTest["pngSize"] = png?.Length ?? 0;
            }
            catch (Exception ex)
            {
                blpTest["pngDecodeError"] = ex.Message;
            }

            report["blpTest"] = blpTest;
        }

        // ═══════════════════════════════════════════════════════════
        // 6. MMDX/MDDF SAMPLE (first 3 doodads)
        // ═══════════════════════════════════════════════════════════
        if (adt?.Doodads?.Count > 0)
        {
            report["doodadSamples"] = adt.Doodads.Take(3).Select(d => new
            {
                model = d.ModelPath,
                posX = d.PosX,
                posY = d.PosY,
                posZ = d.PosZ,
                scale = d.Scale
            });
        }

        return Json(report);
    }

}