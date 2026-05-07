using System.Text.Json;

namespace MangosSuperUI.Services;

/// <summary>
/// Provides accurate vanilla BLP specifications by reading actual extracted BLP files.
///
/// ═══════════════════════════════════════════════════════════════════════
/// Session 22: VANILLA BLP REFERENCE LOOKUP
///
/// PROBLEM: The texture pipeline was guessing BLP output parameters —
///   defaulting to 512×512 DXT3 alphaDepth=8 for everything. Vanilla
///   body textures like MoltenRock.blp are actually 64×64 DXT1 alphaDepth=0
///   with 100% fill. The mismatch caused dark missiles (wrong size/fill)
///   and square artifacts (wrong format/alpha handling).
///
/// FIX: Read the ACTUAL vanilla BLP files from the configured RawBlpPath
///   directory, which contains every BLP referenced by any M2 in all MPQs,
///   extracted as lowercase filenames.
///
/// FLOW:
///   1. Recipe/M2 parser gives us vanilla filename: "SPELLS\\MOLTENROCK.BLP"
///   2. Normalize to lowercase filename: "moltenrock.blp"
///   3. Read header from rawblps → 64×64, DXT1, alphaDepth=0
///   4. Pipeline generates thunder-themed content at FLUX resolution
///   5. Post-process resizes to 64×64, encodes as DXT1 alphaDepth=0
///   6. Result: new look, same envelope the particle system expects
///
/// CONFIG (in appsettings.json or server-config.json):
///   SpellCreator:RawBlpPath — directory with extracted vanilla BLPs
///   SpellCreator:DataPath   — directory with m2_texture_graph.json
/// ═══════════════════════════════════════════════════════════════════════
/// </summary>
public class VanillaBlpService
{
    private readonly ILogger<VanillaBlpService> _logger;
    private readonly IConfiguration _config;

    // In-memory caches — loaded lazily, small enough to hold
    private Dictionary<string, List<string>>? _m2ToBlps;
    private Dictionary<string, List<string>>? _blpToM2s;
    private readonly Dictionary<string, VanillaBlpInfo> _headerCache = new(StringComparer.OrdinalIgnoreCase);

    private string RawBlpPath => _config["SpellCreator:RawBlpPath"] ?? "";

    private string DataPath => _config["SpellCreator:DataPath"] ?? "";

    public VanillaBlpService(IConfiguration config, ILogger<VanillaBlpService> logger)
    {
        _config = config;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get vanilla BLP specs for a texture referenced in an M2 file.
    /// Accepts any path format: "SPELLS\\MOLTENROCK.BLP", "Spells/MoltenRock.blp",
    /// or just "moltenrock.blp". Returns null if the BLP isn't found or RawBlpPath
    /// is not configured.
    /// </summary>
    public VanillaBlpInfo? GetBlpInfo(string vanillaPath)
    {
        if (string.IsNullOrEmpty(RawBlpPath))
        {
            _logger.LogDebug("VanillaBlp: RawBlpPath not configured — set SpellCreator:RawBlpPath in Settings");
            return null;
        }

        string filename = NormalizeToFilename(vanillaPath);

        // Check cache first
        if (_headerCache.TryGetValue(filename, out var cached))
            return cached;

        // Read from disk
        string fullPath = Path.Combine(RawBlpPath, filename);
        if (!File.Exists(fullPath))
        {
            _logger.LogDebug("VanillaBlp: Not found: {File} (looked in {Dir})", filename, RawBlpPath);
            return null;
        }

        var info = ReadBlpHeader(fullPath, filename);
        if (info != null)
            _headerCache[filename] = info;

        return info;
    }

    /// <summary>
    /// Get all vanilla BLP specs for every texture in an M2 file.
    /// Returns a dict keyed by texture index in the M2.
    /// </summary>
    public Dictionary<int, VanillaBlpInfo> GetBlpInfosForM2(string m2Path, List<(int Index, string Filename)> parsedTextures)
    {
        var result = new Dictionary<int, VanillaBlpInfo>();

        foreach (var (index, filename) in parsedTextures)
        {
            var info = GetBlpInfo(filename);
            if (info != null)
                result[index] = info;
        }

        return result;
    }

    /// <summary>
    /// Look up which BLP filenames an M2 uses, from the texture graph.
    /// Returns lowercase filenames like ["moltenrock.blp", "lavalump2.blp"].
    /// </summary>
    public List<string>? GetTexturesForM2(string m2Path)
    {
        EnsureGraphLoaded();
        if (_m2ToBlps == null) return null;

        // Try exact key first, then case-insensitive
        if (_m2ToBlps.TryGetValue(m2Path, out var blps))
            return blps;

        // Normalize: the graph uses backslash paths like "Spells\\Fireball_Missile_Low.m2"
        string normalized = m2Path.Replace('/', '\\');
        foreach (var (key, value) in _m2ToBlps)
        {
            if (string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Look up which M2s use a given BLP filename.
    /// </summary>
    public List<string>? GetM2sForBlp(string blpFilename)
    {
        EnsureGraphLoaded();
        if (_blpToM2s == null) return null;

        string filename = NormalizeToFilename(blpFilename);
        return _blpToM2s.TryGetValue(filename, out var m2s) ? m2s : null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // BLP HEADER READER
    // ═══════════════════════════════════════════════════════════════════════

    private VanillaBlpInfo? ReadBlpHeader(string fullPath, string filename)
    {
        try
        {
            // Only need the header — first 148 bytes (up through mip sizes)
            byte[] header;
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read))
            {
                int fileSize = (int)fs.Length;
                int readSize = Math.Min(fileSize, 148);
                header = new byte[readSize];
                fs.Read(header, 0, readSize);

                // Build the info
                if (readSize < 20 || header[0] != 'B' || header[1] != 'L' || header[2] != 'P' || header[3] != '2')
                {
                    _logger.LogWarning("VanillaBlp: Not a valid BLP2 file: {File}", filename);
                    return null;
                }

                byte compression = header[8];
                byte alphaDepth = header[9];
                byte alphaType = header[10];
                byte hasMips = header[11];
                int width = (int)BitConverter.ToUInt32(header, 12);
                int height = (int)BitConverter.ToUInt32(header, 16);

                string format = compression switch
                {
                    2 => alphaType switch
                    {
                        0 => "DXT1",
                        1 => "DXT3",
                        7 => "DXT5",
                        _ => $"DXT({alphaType})"
                    },
                    1 => "Palettized",
                    _ => $"Unknown({compression})"
                };

                // Count mip levels
                int mipCount = 0;
                if (readSize >= 84)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        uint mipOfs = BitConverter.ToUInt32(header, 20 + i * 4);
                        if (mipOfs > 0) mipCount++;
                        else break;
                    }
                }

                var info = new VanillaBlpInfo
                {
                    Filename = filename,
                    Width = width,
                    Height = height,
                    Compression = compression,
                    AlphaDepth = alphaDepth,
                    AlphaType = alphaType,
                    Format = format,
                    HasMips = hasMips != 0,
                    MipCount = mipCount,
                    FileSize = fileSize
                };

                _logger.LogDebug(
                    "VanillaBlp: {File} → {W}×{H} {Fmt} alphaDepth={AD}",
                    filename, width, height, format, alphaDepth);

                return info;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VanillaBlp: Failed to read header: {File}", filename);
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRAPH LOADING (lazy, from disk)
    // ═══════════════════════════════════════════════════════════════════════

    private void EnsureGraphLoaded()
    {
        if (_m2ToBlps != null) return;

        if (string.IsNullOrEmpty(DataPath))
        {
            _logger.LogDebug("VanillaBlp: DataPath not configured — set SpellCreator:DataPath in Settings");
            _m2ToBlps = new();
            _blpToM2s = new();
            return;
        }

        string graphPath = Path.Combine(DataPath, "m2_texture_graph.json");
        string reversePath = Path.Combine(DataPath, "blp_to_m2_reverse.json");

        if (File.Exists(graphPath))
        {
            try
            {
                var json = File.ReadAllText(graphPath);
                _m2ToBlps = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json)
                    ?? new();
                _logger.LogInformation("VanillaBlp: Loaded texture graph — {Count} M2s", _m2ToBlps.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VanillaBlp: Failed to load texture graph");
                _m2ToBlps = new();
            }
        }
        else
        {
            _logger.LogWarning("VanillaBlp: Texture graph not found at {Path}", graphPath);
            _m2ToBlps = new();
        }

        if (File.Exists(reversePath))
        {
            try
            {
                var json = File.ReadAllText(reversePath);
                _blpToM2s = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json)
                    ?? new();
                _logger.LogInformation("VanillaBlp: Loaded reverse graph — {Count} BLPs", _blpToM2s.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VanillaBlp: Failed to load reverse graph");
                _blpToM2s = new();
            }
        }
        else
        {
            _blpToM2s = new();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Normalize any vanilla texture path to a lowercase filename.
    /// "SPELLS\\MOLTENROCK.BLP" → "moltenrock.blp"
    /// "Creature/GolemHarvest/RED_GLOW3.BLP" → "red_glow3.blp"
    /// "moltenrock.blp" → "moltenrock.blp"
    /// </summary>
    public static string NormalizeToFilename(string vanillaPath)
    {
        // Strip directory — could be backslash or forward slash
        string filename = Path.GetFileName(vanillaPath.Replace('\\', '/'));
        return filename.ToLowerInvariant();
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// VANILLA BLP INFO DTO
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Vanilla BLP file characteristics read from actual extracted BLP headers.
/// These are the ground-truth specs that custom replacements must match.
/// </summary>
public class VanillaBlpInfo
{
    /// <summary>Lowercase filename, e.g. "moltenrock.blp"</summary>
    public string Filename { get; set; } = "";

    /// <summary>Pixel width (power of 2). e.g. 64</summary>
    public int Width { get; set; }

    /// <summary>Pixel height (power of 2). e.g. 64</summary>
    public int Height { get; set; }

    /// <summary>Raw compression byte (2=DXT, 1=Palettized)</summary>
    public byte Compression { get; set; }

    /// <summary>Alpha depth: 0=no alpha, 1=1-bit, 8=full alpha</summary>
    public byte AlphaDepth { get; set; }

    /// <summary>Alpha type: 0=DXT1, 1=DXT3, 7=DXT5</summary>
    public byte AlphaType { get; set; }

    /// <summary>Human-readable format string: "DXT1", "DXT3", "DXT5", etc.</summary>
    public string Format { get; set; } = "";

    /// <summary>Whether mipmaps are present</summary>
    public bool HasMips { get; set; }

    /// <summary>Number of mipmap levels</summary>
    public int MipCount { get; set; }

    /// <summary>Total file size in bytes</summary>
    public int FileSize { get; set; }

    /// <summary>Max dimension (for square resize target)</summary>
    public int MaxDimension => Math.Max(Width, Height);

    /// <summary>Whether this is a no-alpha additive texture (DXT1, alphaDepth=0)</summary>
    public bool IsNoAlpha => AlphaDepth == 0;

    /// <summary>Whether this BLP uses DXT1 compression</summary>
    public bool IsDxt1 => Format == "DXT1";

    /// <summary>Whether this BLP uses DXT3 compression</summary>
    public bool IsDxt3 => Format == "DXT3";
}