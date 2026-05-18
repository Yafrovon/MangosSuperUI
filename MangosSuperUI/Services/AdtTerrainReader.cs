using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using War3Net.IO.Mpq;

namespace MangosSuperUI.Services;

/// <summary>
/// Reads WoW 1.12.1 ADT terrain files from client MPQ archives and extracts:
///   - Ground texture filenames (MTEX)
///   - Per-chunk texture layer definitions (MCLY)
///   - Alpha blend maps (MCAL) → composite RGBA splat map
///   - Doodad placements (MMDX + MDDF)
///   - WMO placements (MWMO + MODF)
///
/// ADT format is IFF-style chunked: each section = char[4] magic + uint32 size + data[size].
/// A single ADT tile covers one 533.33-yard grid cell (16×16 terrain chunks).
/// Each chunk (MCNK) is ~33.33 yards and contains up to 4 texture layers blended by alpha maps.
///
/// This service is general-purpose — usable anywhere in MangosSuperUI, not just Visual Lab.
/// All data comes from the user's own WoW client installation at Vmangos:ClientDataPath.
///
/// Session 39: Terrain Renderer — ADT parsing for textured terrain backdrop.
/// </summary>
public static class AdtTerrainReader
{
    // ═══════════════════════════════════════════════════════════════════
    // CONSTANTS
    // ═══════════════════════════════════════════════════════════════════

    // IFF chunk magic bytes (little-endian uint32 when read with BitConverter)
    private static readonly uint MAGIC_MVER = ChunkId("MVER");
    private static readonly uint MAGIC_MHDR = ChunkId("MHDR");
    private static readonly uint MAGIC_MCIN = ChunkId("MCIN");
    private static readonly uint MAGIC_MTEX = ChunkId("MTEX");
    private static readonly uint MAGIC_MMDX = ChunkId("MMDX");
    private static readonly uint MAGIC_MMID = ChunkId("MMID");
    private static readonly uint MAGIC_MWMO = ChunkId("MWMO");
    private static readonly uint MAGIC_MWID = ChunkId("MWID");
    private static readonly uint MAGIC_MDDF = ChunkId("MDDF");
    private static readonly uint MAGIC_MODF = ChunkId("MODF");
    private static readonly uint MAGIC_MCNK = ChunkId("MCNK");
    private static readonly uint MAGIC_MCLY = ChunkId("MCLY");
    private static readonly uint MAGIC_MCAL = ChunkId("MCAL");
    private static readonly uint MAGIC_MCSH = ChunkId("MCSH");
    private static readonly uint MAGIC_MCVT = ChunkId("MCVT");
    private static readonly uint MAGIC_MCLQ = ChunkId("MCLQ");

    // ADT grid constants — same as VmangosMapParser
    public const int CHUNKS_PER_SIDE = 16;
    public const float CHUNK_SIZE = 33.3333f;
    public const float GRID_SIZE = 533.3333f;
    public const float CELL_SIZE = CHUNK_SIZE / 8.0f; // ~4.167 yards per cell

    // Alpha map dimensions
    public const int ALPHA_SIZE_FULL = 64;
    public const int ALPHA_SIZE_HALF = 32;

    // MCLY flags
    private const uint MCLY_FLAG_COMPRESSED_ALPHA = 0x200;
    private const uint MCLY_FLAG_BIG_ALPHA = 0x100; // 4096 bytes uncompressed (64×64) instead of 2048 (32×64)

    // MCNK header size (128 bytes before sub-chunks)
    private const int MCNK_HEADER_SIZE = 128;

    // MCLQ liquid block size
    //   float min_height + float max_height
    //   + 9*9 vertices × (4 bytes water/magma meta + 4 bytes height) = 648 bytes
    //   + 8*8 tile flag bytes = 64 bytes
    //   + uint32 n_flowvs + 2 × 38-byte flowv = 80 bytes
    //   = 800 bytes total per liquid layer
    private const int MCLQ_LAYER_SIZE = 4 + 4 + 9 * 9 * 8 + 8 * 8 + 4 + 2 * 38;

    // ═══════════════════════════════════════════════════════════════════
    // PUBLIC API — High-level extraction from MPQ
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Read an ADT file from the client's terrain.MPQ and parse all terrain data.
    /// Returns null if the file or MPQ can't be found.
    /// </summary>
    /// <param name="clientDataPath">Path to the WoW client Data/ directory (Vmangos:ClientDataPath)</param>
    /// <param name="mapName">Map name, e.g. "Azeroth" (EK) or "Kalimdor"</param>
    /// <param name="gridX">Grid X coordinate (0-63)</param>
    /// <param name="gridY">Grid Y coordinate (0-63)</param>
    public static AdtResult? ReadFromMpq(string clientDataPath, string mapName, int gridX, int gridY)
    {
        if (string.IsNullOrEmpty(clientDataPath) || !Directory.Exists(clientDataPath))
            return null;

        // Preset convention: gridX = gy (row), gridY = gx (col)
        // VmangosMapParser: {mapId}{gridX}{gridY} = {mapId}{gy}{gx} — matches HeightMapService
        // ADT naming: Map_{col}_{row}.adt = Map_{gx}_{gy}.adt = Map_{gridY}_{gridX}.adt
        string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{gridY}_{gridX}.adt";

        byte[]? adtBytes = ReadFileFromMpqs(clientDataPath, adtPath);
        if (adtBytes == null)
            return null;

        return Parse(adtBytes, gridX, gridY);
    }

    /// <summary>
    /// Read a specific file from the client's MPQ archives.
    /// Searches all MPQs in priority order: patch archives first, then base archives.
    /// Case-insensitive path matching via War3Net's MpqArchive.
    /// </summary>
    public static byte[]? ReadFileFromMpqs(string clientDataPath, string internalPath, bool skipPatchZ = false)
    {
        // MPQ load order: patches first (reverse alphabetical), then base archives
        // For terrain data, it's typically in terrain.MPQ but patches could override
        var mpqFiles = GetMpqLoadOrder(clientDataPath);

        foreach (var mpqPath in mpqFiles)
        {
            // Skip patch-Z.MPQ when reading "original" data for patching
            if (skipPatchZ)
            {
                var fname = Path.GetFileName(mpqPath);
                if (fname.Equals("patch-Z.MPQ", StringComparison.OrdinalIgnoreCase) ||
                    fname.Equals("patch-z.mpq", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            try
            {
                using var stream = File.OpenRead(mpqPath);
                using var archive = new MpqArchive(stream);

                if (archive.TryOpenFile(internalPath, out var fileStream))
                {
                    using (fileStream)
                    {
                        using var ms = new MemoryStream();
                        fileStream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                // MPQ open/read failed — try next
            }
        }

        return null;
    }

    /// <summary>
    /// Get MPQ files in client load order.
    /// Patches load first (patch-3 > patch-2 > patch > base archives).
    /// </summary>
    private static List<string> GetMpqLoadOrder(string clientDataPath)
    {
        var result = new List<string>();
        if (!Directory.Exists(clientDataPath)) return result;

        var allMpqs = Directory.GetFiles(clientDataPath, "*.MPQ", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(clientDataPath, "*.mpq", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Patches first (sorted reverse so patch-3 > patch-2 > patch)
        var patches = allMpqs
            .Where(f => Path.GetFileName(f).StartsWith("patch", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Then base archives — terrain.MPQ is the primary one for ADT data
        var bases = allMpqs
            .Where(f => !Path.GetFileName(f).StartsWith("patch", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f =>
            {
                // Prioritize terrain.MPQ for ADT reads, model.MPQ for doodads
                var name = Path.GetFileName(f).ToLowerInvariant();
                if (name == "terrain.mpq") return 0;
                if (name == "model.mpq") return 1;
                return 10;
            })
            .ToList();

        result.AddRange(patches);
        result.AddRange(bases);
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // PUBLIC API — Parse raw ADT bytes
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse raw ADT file bytes into structured terrain data.
    /// This is the main entry point for pre-loaded ADT data.
    /// </summary>
    public static AdtResult? Parse(byte[] data, int gridX = 0, int gridY = 0)
    {
        if (data == null || data.Length < 8)
            return null;

        var result = new AdtResult { GridX = gridX, GridY = gridY };
        var chunks = ParseTopLevelChunks(data);

        // MTEX — texture filename table (null-separated strings)
        if (chunks.TryGetValue(MAGIC_MTEX, out var mtex))
            result.Textures = ParseMtex(data, mtex.offset, mtex.size);

        // ── M2 doodad path resolution ─────────────────────────────────────
        // MMDX:  null-separated path strings, one per unique M2.
        // MMID:  uint32[] of byte offsets into MMDX (one per registered M2).
        // MDDF.nameId is an INDEX into MMID (not a byte offset into MMDX).
        //   path = MMDX_at_byte(MMID[MDDF.nameId])
        // Dense zones (Westfall, Duskwood, etc.) revealed that the old
        // "offset lookup with sequential-index fallback" approach silently
        // mis-resolves entries whenever a real MMDX byte offset happens to
        // collide with one of the fallback index values. We now read MMID
        // properly. If MMID is missing (shouldn't happen on vanilla 1.12.1),
        // the offset-lookup fallback is still used.
        uint[]? mmidOffsets = null;
        if (chunks.TryGetValue(MAGIC_MMID, out var mmid))
            mmidOffsets = ParseUint32Array(data, mmid.offset, mmid.size);

        Dictionary<uint, string>? mmdxByOffset = null;
        if (chunks.TryGetValue(MAGIC_MMDX, out var mmdx))
            mmdxByOffset = BuildOffsetStringMap(data, mmdx.offset, mmdx.size);

        // ── WMO path resolution (same shape: MWMO + MWID + MODF) ──────────
        uint[]? mwidOffsets = null;
        if (chunks.TryGetValue(MAGIC_MWID, out var mwid))
            mwidOffsets = ParseUint32Array(data, mwid.offset, mwid.size);

        Dictionary<uint, string>? mwmoByOffset = null;
        if (chunks.TryGetValue(MAGIC_MWMO, out var mwmo))
            mwmoByOffset = BuildOffsetStringMap(data, mwmo.offset, mwmo.size);

        // MDDF — doodad placements
        if (chunks.TryGetValue(MAGIC_MDDF, out var mddf) && mmdxByOffset != null)
            result.Doodads = ParseMddf(data, mddf.offset, mddf.size, mmidOffsets, mmdxByOffset);

        // MODF — WMO placements
        if (chunks.TryGetValue(MAGIC_MODF, out var modf) && mwmoByOffset != null)
            result.Wmos = ParseModf(data, modf.offset, modf.size, mwidOffsets, mwmoByOffset);

        // MCNK — terrain chunks (256 per ADT, 16×16 grid)
        // Each MCNK has its own sub-chunks including MCLY (layers) and MCAL (alpha)
        result.Chunks = ParseAllMcnk(data, chunks);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // PUBLIC API — Splat map generation
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build a composite RGBA splat map PNG from parsed ADT data.
    /// Each color channel encodes one texture layer's alpha:
    ///   R = layer 0 alpha (always 255 — base layer)
    ///   G = layer 1 alpha
    ///   B = layer 2 alpha
    ///   A = layer 3 alpha
    ///
    /// The splat map covers a rectangular region of chunks.
    /// Output size: (chunksWide * 64) × (chunksHigh * 64) pixels.
    /// </summary>
    /// <param name="adt">Parsed ADT result</param>
    /// <param name="centerChunkX">Center chunk X (0-15 within this ADT)</param>
    /// <param name="centerChunkY">Center chunk Y (0-15 within this ADT)</param>
    /// <param name="radius">Chunk radius around center</param>
    /// <returns>PNG bytes of the RGBA splat map, or null if no texture data</returns>
    public static SplatMapResult? BuildSplatMap(AdtResult adt, int centerChunkX = 8, int centerChunkY = 8, int radius = 3)
    {
        if (adt.Chunks == null || adt.Chunks.Length == 0)
            return null;

        int minCX = Math.Max(0, centerChunkX - radius);
        int maxCX = Math.Min(CHUNKS_PER_SIDE - 1, centerChunkX + radius);
        int minCY = Math.Max(0, centerChunkY - radius);
        int maxCY = Math.Min(CHUNKS_PER_SIDE - 1, centerChunkY + radius);

        int chunksW = maxCX - minCX + 1;
        int chunksH = maxCY - minCY + 1;
        int pixW = chunksW * ALPHA_SIZE_FULL;
        int pixH = chunksH * ALPHA_SIZE_FULL;

        // RGBA pixel buffer — 4 bytes per pixel
        var pixels = new byte[pixW * pixH * 4];

        // Collect unique texture indices used across the selected chunks, sorted by frequency
        // Most common texture becomes layer 0 (base), minimizing visible seams
        var texFrequency = new Dictionary<int, int>();
        for (int cy = minCY; cy <= maxCY; cy++)
        {
            for (int cx = minCX; cx <= maxCX; cx++)
            {
                int chunkIdx = cy * CHUNKS_PER_SIDE + cx;
                if (chunkIdx >= adt.Chunks.Length) continue;
                var chunk = adt.Chunks[chunkIdx];
                if (chunk?.Layers == null) continue;

                foreach (var layer in chunk.Layers)
                {
                    texFrequency.TryGetValue(layer.TextureIndex, out int count);
                    texFrequency[layer.TextureIndex] = count + 1;
                }
            }
        }

        // Sort by frequency descending — most common texture is the base layer
        var globalTexIndices = texFrequency.OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .Take(4)
            .ToList();

        // Build splat map: iterate each chunk in the selected region
        for (int cy = minCY; cy <= maxCY; cy++)
        {
            for (int cx = minCX; cx <= maxCX; cx++)
            {
                int chunkIdx = cy * CHUNKS_PER_SIDE + cx;
                if (chunkIdx >= adt.Chunks.Length) continue;
                var chunk = adt.Chunks[chunkIdx];
                if (chunk?.Layers == null) continue;

                int pixOffX = (cx - minCX) * ALPHA_SIZE_FULL;
                int pixOffY = (cy - minCY) * ALPHA_SIZE_FULL;

                // For each pixel in this chunk's 64×64 region
                for (int py = 0; py < ALPHA_SIZE_FULL; py++)
                {
                    for (int px = 0; px < ALPHA_SIZE_FULL; px++)
                    {
                        int pixIdx = ((pixOffY + py) * pixW + (pixOffX + px)) * 4;

                        // Initialize: all channels 0 except A=255 (PNG must be opaque)
                        pixels[pixIdx + 0] = 0; // R = layer 0 weight
                        pixels[pixIdx + 1] = 0; // G = layer 1 weight
                        pixels[pixIdx + 2] = 0; // B = layer 2 weight
                        pixels[pixIdx + 3] = 255; // A = PNG opacity (always opaque)

                        // The shader blends as:
                        //   color = tex0; color = mix(color, tex1, G); color = mix(color, tex2, B);
                        // This means R isn't actually used by the shader — tex0 is always the starting
                        // color and G/B control how much tex1/tex2 replace it.
                        //
                        // For each chunk, layer 0 is the base. If the chunk's base texture is the
                        // same as global texture 0, no splat is needed (R=255, G=0, B=0 = all base).
                        // If the chunk's base is actually global texture 1, we need G=255 for those pixels.

                        // First pass: determine the effective alpha for each global texture at this pixel
                        // Start with: the chunk's layer 0 covers everything not covered by layers 1+
                        // Then layers 1+ overlay with their alpha maps

                        // Find which global layer this chunk's base texture maps to
                        int chunkBaseGlobal = chunk.Layers.Length > 0
                            ? globalTexIndices.IndexOf(chunk.Layers[0].TextureIndex)
                            : 0;

                        // For pixels where the base layer dominates, we need to express that
                        // in terms of the global layer assignments
                        byte remainingAlpha = 255; // how much of the base is still visible

                        // Process overlay layers (1+) — they have alpha maps
                        for (int li = 1; li < chunk.Layers.Length; li++)
                        {
                            var layer = chunk.Layers[li];
                            int globalLayer = globalTexIndices.IndexOf(layer.TextureIndex);
                            if (globalLayer < 0 || globalLayer > 2) continue; // only 3 layers in shader (0,1,2)

                            byte alpha = GetAlphaValue(chunk, li, px, py);

                            // Store alpha in the appropriate channel
                            // Global layer 0 = base (no channel), 1 = G, 2 = B
                            if (globalLayer == 1)
                                pixels[pixIdx + 1] = alpha;
                            else if (globalLayer == 2)
                                pixels[pixIdx + 2] = alpha;
                            else if (globalLayer == 0)
                            {
                                // This overlay texture IS the global base — don't need to blend it
                                // (it would be underneath anyway)
                            }

                            remainingAlpha = (byte)Math.Max(0, remainingAlpha - alpha);
                        }

                        // If this chunk's base texture is NOT global texture 0,
                        // the remaining alpha (not covered by overlays) should show
                        // the chunk's base texture instead of global texture 0.
                        if (chunkBaseGlobal > 0 && chunkBaseGlobal <= 2)
                        {
                            // Put the remaining base coverage into the correct channel
                            if (chunkBaseGlobal == 1)
                                pixels[pixIdx + 1] = (byte)Math.Max(pixels[pixIdx + 1], remainingAlpha);
                            else if (chunkBaseGlobal == 2)
                                pixels[pixIdx + 2] = (byte)Math.Max(pixels[pixIdx + 2], remainingAlpha);
                        }
                    }
                }
            }
        }

        // Encode as PNG using SkiaSharp
        byte[] pngBytes;
        using (var bitmap = new SkiaSharp.SKBitmap(pixW, pixH, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul))
        {
            var span = bitmap.GetPixelSpan();
            pixels.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span));
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            pngBytes = encoded.ToArray();
        }

        // Build texture list in layer order
        var layerTextures = new List<string>();
        for (int i = 0; i < globalTexIndices.Count; i++)
        {
            int texIdx = globalTexIndices[i];
            if (texIdx < adt.Textures.Count)
                layerTextures.Add(adt.Textures[texIdx]);
            else
                layerTextures.Add("");
        }

        return new SplatMapResult
        {
            PngBytes = pngBytes,
            Width = pixW,
            Height = pixH,
            ChunksWidth = chunksW,
            ChunksHeight = chunksH,
            MinChunkX = minCX,
            MinChunkY = minCY,
            LayerTextures = layerTextures
        };
    }

    /// <summary>
    /// Get alpha value for a specific layer at a specific pixel within a chunk.
    /// Layer 0 has no alpha map (implicit full coverage).
    /// ParseMcal always stores alpha as 64×64 (upscaling 32→64 if needed),
    /// so this is a simple stride-64 read.
    /// </summary>
    private static byte GetAlphaValue(McnkChunk chunk, int layerIndex, int px, int py)
    {
        if (layerIndex <= 0 || layerIndex >= chunk.Layers.Length)
            return 0;

        var layer = chunk.Layers[layerIndex];
        var alpha = layer.AlphaMap;
        if (alpha == null || alpha.Length == 0)
            return 0;

        // Always 64×64 stride-64 — ParseMcal guarantees this
        int idx = py * ALPHA_SIZE_FULL + px;
        return idx < alpha.Length ? alpha[idx] : (byte)0;
    }

    // ═══════════════════════════════════════════════════════════════════
    // PUBLIC API — Doodad filtering for a region
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Filter doodads to those within a chunk region, classify by model path,
    /// and transform ADT coordinates to Three.js coordinates matching
    /// VmangosMapParser's output.
    ///
    /// ADT coordinate system (wowdev.wiki):
    ///   MDDF position is in the global ADT coordinate system:
    ///     posX, posZ range from 0 to 64*533.333 = 34133.33
    ///     posY = height (WoW world Z)
    ///
    ///   ADT file naming: Map_{tileX}_{tileY}.adt
    ///   where tileX = floor(posX / 533.333) and tileY = floor(posZ / 533.333)
    ///   Our preset gridX = tileX, gridY = tileY.
    ///
    ///   Within-tile local position (0..128 cell indices):
    ///     localCol (vx) = (posX / SIZE - gridX) * 128   → Three.js X
    ///     localRow (vy) = (posZ / SIZE - gridY) * 128   → Three.js Z
    ///
    ///   BUT: if the above produces all doodads outside the mesh, the axes
    ///   may be swapped. We try the primary mapping first and if the first
    ///   5 samples are all out-of-bounds, we swap axes automatically.
    ///
    /// VmangosMapParser builds the mesh from V9[vy * 129 + vx]:
    ///   vx = column → Three.js X = offsetX + (vx - v9StartX) * CELL_SIZE
    ///   vy = row    → Three.js Z = offsetZ + (vy - v9StartY) * CELL_SIZE
    ///   height      → Three.js Y = (h - midHeight) * heightScale
    /// </summary>
    public static List<DoodadInstance> GetDoodadsForRegion(
        AdtResult adt, int centerChunkX, int centerChunkY, int radius,
        float heightScale, float midHeight, int gridX, int gridY)
    {
        if (adt.Doodads == null || adt.Doodads.Count == 0)
            return new List<DoodadInstance>();

        int minCX = Math.Max(0, centerChunkX - radius);
        int maxCX = Math.Min(CHUNKS_PER_SIDE - 1, centerChunkX + radius);
        int minCY = Math.Max(0, centerChunkY - radius);
        int maxCY = Math.Min(CHUNKS_PER_SIDE - 1, centerChunkY + radius);

        // V9 vertex range for the selected chunk region (same as VmangosMapParser.BuildMeshResult)
        int v9StartX = minCX * 8;
        int v9EndX = maxCX * 8 + 8;
        int v9StartY = minCY * 8;
        int v9EndY = maxCY * 8 + 8;

        int vertsW = v9EndX - v9StartX + 1;
        int vertsH = v9EndY - v9StartY + 1;

        // Same centering offsets as VmangosMapParser
        float totalWidth = (vertsW - 1) * CELL_SIZE;
        float totalDepth = (vertsH - 1) * CELL_SIZE;
        float offsetX = -totalWidth * 0.5f;
        float offsetZ = -totalDepth * 0.5f;

        // Coordinate mapping:
        // Preset convention: gridX = gy (row), gridY = gx (col)
        // MDDF coordinates in ADT-space (0..34133):
        //   posX / SIZE → ADT column = gx = gridY
        //   posZ / SIZE → ADT row    = gy = gridX
        //   posY        → height (WoW world Z)
        //
        // VmangosMapParser V9 grid: v9[vy * 129 + vx]
        //   vx = column → Three.js X,  vy = row → Three.js Z
        //
        // Within-tile cell indices:
        //   localCol (vx) = (posX / SIZE - gridY) * 128   (posX→gx=gridY)
        //   localRow (vy) = (posZ / SIZE - gridX) * 128   (posZ→gy=gridX)

        var result = new List<DoodadInstance>();

        foreach (var d in adt.Doodads)
        {
            float localCol = (d.PosX / GRID_SIZE - gridY) * 128.0f; // posX→gx=gridY→vx
            float localRow = (d.PosZ / GRID_SIZE - gridX) * 128.0f; // posZ→gy=gridX→vy

            // Loose culling — allow doodads slightly outside the selected chunk window
            if (localCol < v9StartX - 16 || localCol > v9EndX + 16) continue;
            if (localRow < v9StartY - 16 || localRow > v9EndY + 16) continue;

            // Transform to Three.js coordinates (same formula as VmangosMapParser.BuildMeshResult)
            float meshCol = localCol - v9StartX;
            float meshRow = localRow - v9StartY;

            float threeX = offsetX + meshCol * CELL_SIZE;
            float threeY = (d.PosY - midHeight) * heightScale;
            float threeZ = offsetZ + meshRow * CELL_SIZE;

            string doodadType = ClassifyDoodad(d.ModelPath);

            result.Add(new DoodadInstance
            {
                ModelPath = d.ModelPath,
                Type = doodadType,
                X = threeX,
                Y = threeY,
                Z = threeZ,
                RotY = d.RotY,
                Scale = d.Scale
            });
        }

        return result;
    }

    /// <summary>
    /// Diagnostic: analyze doodad coordinate distribution for debugging.
    /// Returns raw coordinate stats and computed mesh positions for the first N doodads.
    ///
    /// Convention:
    ///   Preset (gridX, gridY) = (gy=row, gx=col)
    ///   .map file = {mapId}{gridX}{gridY} = {mapId}{gy}{gx} (HeightMapService convention)
    ///   ADT file = Map_{gx}_{gy}.adt = Map_{gridY}_{gridX}.adt
    ///   MDDF posX / SIZE → gx = gridY (column)
    ///   MDDF posZ / SIZE → gy = gridX (row)
    /// </summary>
    public static object GetDoodadDiagnostics(
        AdtResult adt, int centerChunkX, int centerChunkY, int radius,
        float heightScale, float midHeight, int gridX, int gridY)
    {
        if (adt.Doodads == null || adt.Doodads.Count == 0)
            return new { total = 0, error = "No doodads parsed" };

        int minCX = Math.Max(0, centerChunkX - radius);
        int maxCX = Math.Min(CHUNKS_PER_SIDE - 1, centerChunkX + radius);
        int minCY = Math.Max(0, centerChunkY - radius);
        int maxCY = Math.Min(CHUNKS_PER_SIDE - 1, centerChunkY + radius);

        int v9StartX = minCX * 8;
        int v9EndX = maxCX * 8 + 8;
        int v9StartY = minCY * 8;
        int v9EndY = maxCY * 8 + 8;
        int vertsW = v9EndX - v9StartX + 1;
        int vertsH = v9EndY - v9StartY + 1;
        float totalWidth = (vertsW - 1) * CELL_SIZE;
        float totalDepth = (vertsH - 1) * CELL_SIZE;
        float offsetX = -totalWidth * 0.5f;
        float offsetZ = -totalDepth * 0.5f;

        // Compute stats for ALL doodads
        float minPosX = float.MaxValue, maxPosX = float.MinValue;
        float minPosY = float.MaxValue, maxPosY = float.MinValue;
        float minPosZ = float.MaxValue, maxPosZ = float.MinValue;
        foreach (var d in adt.Doodads)
        {
            if (d.PosX < minPosX) minPosX = d.PosX;
            if (d.PosX > maxPosX) maxPosX = d.PosX;
            if (d.PosY < minPosY) minPosY = d.PosY;
            if (d.PosY > maxPosY) maxPosY = d.PosY;
            if (d.PosZ < minPosZ) minPosZ = d.PosZ;
            if (d.PosZ > maxPosZ) maxPosZ = d.PosZ;
        }

        // Direct mapping: posX→gx=gridY (col), posZ→gy=gridX (row)
        int inBounds = 0;
        foreach (var d in adt.Doodads)
        {
            float col = (d.PosX / GRID_SIZE - gridY) * 128;
            float row = (d.PosZ / GRID_SIZE - gridX) * 128;
            if (col >= -2 && col <= 130 && row >= -2 && row <= 130) inBounds++;
        }

        // Count how many pass the chunk window culling
        int inChunkWindow = 0;
        foreach (var d in adt.Doodads)
        {
            float col = (d.PosX / GRID_SIZE - gridY) * 128;
            float row = (d.PosZ / GRID_SIZE - gridX) * 128;
            if (col >= v9StartX - 16 && col <= v9EndX + 16 &&
                row >= v9StartY - 16 && row <= v9EndY + 16) inChunkWindow++;
        }

        // Sample first 5 doodads
        var samples = adt.Doodads.Take(5).Select(d =>
        {
            float col = (d.PosX / GRID_SIZE - gridY) * 128;
            float row = (d.PosZ / GRID_SIZE - gridX) * 128;
            float meshCol = col - v9StartX;
            float meshRow = row - v9StartY;
            float threeX = offsetX + meshCol * CELL_SIZE;
            float threeY = (d.PosY - midHeight) * heightScale;
            float threeZ = offsetZ + meshRow * CELL_SIZE;
            bool inWindow = col >= v9StartX - 16 && col <= v9EndX + 16 &&
                            row >= v9StartY - 16 && row <= v9EndY + 16;

            return new
            {
                model = d.ModelPath,
                raw = new { posX = d.PosX, posY = d.PosY, posZ = d.PosZ },
                tileIndices = new { posX_div_SIZE = d.PosX / GRID_SIZE, posZ_div_SIZE = d.PosZ / GRID_SIZE },
                local = new { localCol = col, localRow = row, inTile = col >= 0 && col <= 128 && row >= 0 && row <= 128 },
                threeJs = new { x = threeX, y = threeY, z = threeZ, inWindow },
                meshRange = new { v9StartX, v9EndX, v9StartY, v9EndY },
            };
        }).ToList();

        return new
        {
            total = adt.Doodads.Count,
            gridX,
            gridY,
            note = "gridX=gy(row), gridY=gx(col). ADT=Map_{gridY}_{gridX}.adt. .map={mapId}{gridX}{gridY}",
            expectedPosXRange_gx = new { min = gridY * GRID_SIZE, max = (gridY + 1) * GRID_SIZE },
            expectedPosZRange_gy = new { min = gridX * GRID_SIZE, max = (gridX + 1) * GRID_SIZE },
            actualRanges = new
            {
                posX = new { min = minPosX, max = maxPosX },
                posY = new { min = minPosY, max = maxPosY },
                posZ = new { min = minPosZ, max = maxPosZ }
            },
            inBounds,
            inChunkWindow,
            heightTransform = new { midHeight, heightScale },
            meshBounds = new { offsetX, offsetZ, totalWidth, totalDepth },
            samples
        };
    }

    /// <summary>
    /// Classify a doodad model path into a rendering category.
    /// Used to pick billboard color/shape in the viewer.
    /// </summary>
    public static string ClassifyDoodad(string modelPath)
    {
        string lower = modelPath.ToLowerInvariant();

        // Trees and large vegetation
        if (lower.Contains("tree") || lower.Contains("spruce") || lower.Contains("birch") ||
            lower.Contains("oak") || lower.Contains("pine") || lower.Contains("maple") ||
            lower.Contains("willow") || lower.Contains("palm") || lower.Contains("cedar") ||
            lower.Contains("redwood") || lower.Contains("canopy") || lower.Contains("trunk"))
            return "tree";

        // Bushes and small vegetation
        if (lower.Contains("bush") || lower.Contains("fern") || lower.Contains("flower") ||
            lower.Contains("grass") || lower.Contains("weed") || lower.Contains("plant") ||
            lower.Contains("shrub") || lower.Contains("vine") || lower.Contains("moss") ||
            lower.Contains("mushroom") || lower.Contains("herb") || lower.Contains("cattail") ||
            lower.Contains("lily") || lower.Contains("clover") || lower.Contains("thistle"))
            return "vegetation";

        // Rocks and geological
        if (lower.Contains("rock") || lower.Contains("stone") || lower.Contains("boulder") ||
            lower.Contains("cliff") || lower.Contains("crag") || lower.Contains("pebble") ||
            lower.Contains("stalactite") || lower.Contains("stalagmite") || lower.Contains("crystal"))
            return "rock";

        // Fences and barriers
        if (lower.Contains("fence") || lower.Contains("wall") || lower.Contains("gate") ||
            lower.Contains("post") || lower.Contains("railing") || lower.Contains("barrier"))
            return "fence";

        // Logs, stumps, and wood debris
        if (lower.Contains("log") || lower.Contains("stump") || lower.Contains("deadtree") ||
            lower.Contains("fallentree") || lower.Contains("woodpile") || lower.Contains("lumber"))
            return "wood";

        // Detail objects (small environmental props)
        if (lower.Contains("detail") || lower.Contains("groundclutter"))
            return "detail";

        return "generic";
    }

    // ═══════════════════════════════════════════════════════════════════
    // PUBLIC API — WMO filtering for a region
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get WMO bounding boxes for rendering as building silhouettes.
    /// </summary>
    public static List<WmoInstance> GetWmosForRegion(
        AdtResult adt, float heightScale, float midHeight)
    {
        if (adt.Wmos == null || adt.Wmos.Count == 0)
            return new List<WmoInstance>();

        // WMOs already have bounding boxes from MODF — just pass them through
        // with coordinate transform applied by the client (JS side)
        // For now, return raw world coordinates and let JS do the transform
        return adt.Wmos;
    }

    // ═══════════════════════════════════════════════════════════════════
    // BLP TEXTURE READING FROM MPQ
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Read a BLP texture file from the client's MPQ archives and decode to PNG bytes.
    /// Used for ground tileset textures referenced by MTEX.
    /// </summary>
    /// <param name="clientDataPath">Client Data/ directory</param>
    /// <param name="blpPath">Internal MPQ path, e.g. "Tileset\\Elwynn\\ElwynnGrass01.blp"</param>
    /// <returns>PNG bytes, or null if not found</returns>
    public static byte[]? ReadBlpAsPng(string clientDataPath, string blpPath)
    {
        byte[]? blpData = ReadFileFromMpqs(clientDataPath, blpPath);
        if (blpData == null) return null;

        try
        {
            using var ms = new MemoryStream(blpData);
            var blpFile = new War3Net.Drawing.Blp.BlpFile(ms);
            var pixels = blpFile.GetPixels(0, out int w, out int h);

            // War3Net returns BGRA — encode with SkiaSharp
            using var bitmap = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
            var span = bitmap.GetPixelSpan();
            pixels.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span));
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            return encoded.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read a BLP texture and return raw BGRA pixel data + dimensions.
    /// Used by BuildCompositeTexture for per-pixel sampling.
    /// </summary>
    public static (byte[] bgra, int width, int height)? ReadBlpPixels(string clientDataPath, string blpPath)
    {
        byte[]? blpData = ReadFileFromMpqs(clientDataPath, blpPath);
        if (blpData == null) return null;

        try
        {
            using var ms = new MemoryStream(blpData);
            var blpFile = new War3Net.Drawing.Blp.BlpFile(ms);
            var pixels = blpFile.GetPixels(0, out int w, out int h);
            return (pixels, w, h);
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SERVER-SIDE COMPOSITE TEXTURE — baked RGB terrain texture
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build a fully composited RGB terrain texture by sampling and blending
    /// each chunk's BLP tileset textures with their alpha maps.
    ///
    /// Unlike BuildSplatMap (which outputs alpha channels for shader blending),
    /// this bakes the final pixel color server-side. No texture slot limits,
    /// no global remapping — each chunk uses its own layers independently.
    ///
    /// Output: RGBA PNG at (chunksW * pixelsPerChunk) × (chunksH * pixelsPerChunk).
    /// pixelsPerChunk controls quality: 64 = alpha-res match, 128+ = sharper tiling.
    /// </summary>
    public static SplatMapResult? BuildCompositeTexture(
        AdtResult adt, string clientDataPath,
        int centerChunkX = 8, int centerChunkY = 8, int radius = 3,
        int pixelsPerChunk = 64, bool swapAxes = false, bool transposeAlpha = false)
    {
        if (adt.Chunks == null || adt.Chunks.Length == 0)
            return null;

        int minCX = Math.Max(0, centerChunkX - radius);
        int maxCX = Math.Min(CHUNKS_PER_SIDE - 1, centerChunkX + radius);
        int minCY = Math.Max(0, centerChunkY - radius);
        int maxCY = Math.Min(CHUNKS_PER_SIDE - 1, centerChunkY + radius);

        int chunksW = maxCX - minCX + 1;
        int chunksH = maxCY - minCY + 1;
        int pixW = chunksW * pixelsPerChunk;
        int pixH = chunksH * pixelsPerChunk;

        var pixels = new byte[pixW * pixH * 4];

        // Cache decoded BLP pixel data: textureIndex → (bgra, w, h)
        var blpCache = new Dictionary<int, (byte[] bgra, int w, int h)?>();

        (byte[] bgra, int w, int h)? GetBlpPixels(int textureIndex)
        {
            if (blpCache.TryGetValue(textureIndex, out var cached))
                return cached;

            (byte[] bgra, int w, int h)? result = null;
            if (textureIndex < adt.Textures.Count)
            {
                string blpPath = adt.Textures[textureIndex];
                result = ReadBlpPixels(clientDataPath, blpPath);
            }
            blpCache[textureIndex] = result;
            return result;
        }

        // Sample a BLP texture at a UV coordinate (tiled/wrapped)
        // Returns (R, G, B). BLP data is BGRA.
        (byte r, byte g, byte b) SampleTexture((byte[] bgra, int w, int h) tex, float u, float v)
        {
            // Wrap UV to [0,1)
            u = u - (float)Math.Floor(u);
            v = v - (float)Math.Floor(v);

            int tx = (int)(u * tex.w) % tex.w;
            int ty = (int)(v * tex.h) % tex.h;
            if (tx < 0) tx += tex.w;
            if (ty < 0) ty += tex.h;

            int idx = (ty * tex.w + tx) * 4;
            if (idx + 2 >= tex.bgra.Length) return (128, 128, 128);

            return (tex.bgra[idx + 2], tex.bgra[idx + 1], tex.bgra[idx + 0]); // BGRA → RGB
        }

        // Texture tiling: each chunk is ~33.33 yards, tileset textures tile ~8 times across a chunk
        // So the UV for texture sampling = pixel position within chunk * (tilesPerChunk / pixelsPerChunk)
        float tilesPerChunk = 8.0f;

        // Iterate chunks
        for (int cy = minCY; cy <= maxCY; cy++)
        {
            for (int cx = minCX; cx <= maxCX; cx++)
            {
                // swapAxes: try IndexX→vertical, IndexY→horizontal instead of the default
                int chunkIdx = swapAxes
                    ? cx * CHUNKS_PER_SIDE + cy   // transposed lookup
                    : cy * CHUNKS_PER_SIDE + cx;  // normal lookup
                if (chunkIdx < 0 || chunkIdx >= adt.Chunks.Length) continue;
                var chunk = adt.Chunks[chunkIdx];
                if (chunk?.Layers == null || chunk.Layers.Length == 0) continue;

                int pixOffX = (cx - minCX) * pixelsPerChunk;
                int pixOffY = (cy - minCY) * pixelsPerChunk;

                // Get BLP pixel data for each layer in this chunk
                var layerTextures = new (byte[] bgra, int w, int h)?[chunk.Layers.Length];
                for (int li = 0; li < chunk.Layers.Length; li++)
                {
                    layerTextures[li] = GetBlpPixels(chunk.Layers[li].TextureIndex);
                }

                // Render each pixel — sample BLP textures and blend with alpha
                for (int py = 0; py < pixelsPerChunk; py++)
                {
                    for (int px = 0; px < pixelsPerChunk; px++)
                    {
                        // Texture UV: tile the BLP texture across each chunk
                        float texU = (float)px / pixelsPerChunk * tilesPerChunk;
                        float texV = (float)py / pixelsPerChunk * tilesPerChunk;

                        // Alpha map coordinates
                        int alphaPx = px * ALPHA_SIZE_FULL / pixelsPerChunk;
                        int alphaPy = py * ALPHA_SIZE_FULL / pixelsPerChunk;

                        // Start with base layer (layer 0) — full coverage, no alpha
                        byte finalR = 128, finalG = 128, finalB = 128;
                        if (layerTextures[0] != null)
                        {
                            var c = SampleTexture(layerTextures[0]!.Value, texU, texV);
                            finalR = c.r; finalG = c.g; finalB = c.b;
                        }

                        // Blend overlay layers (1+) using alpha maps
                        for (int li = 1; li < chunk.Layers.Length; li++)
                        {
                            if (layerTextures[li] == null) continue;
                            byte alpha = GetAlphaValue(chunk, li, alphaPx, alphaPy);
                            if (alpha == 0) continue;

                            var overlay = SampleTexture(layerTextures[li]!.Value, texU, texV);
                            float a = alpha / 255.0f;
                            finalR = (byte)(finalR + (overlay.r - finalR) * a);
                            finalG = (byte)(finalG + (overlay.g - finalG) * a);
                            finalB = (byte)(finalB + (overlay.b - finalB) * a);
                        }

                        int pixIdx = ((pixOffY + py) * pixW + (pixOffX + px)) * 4;
                        pixels[pixIdx + 0] = finalR;
                        pixels[pixIdx + 1] = finalG;
                        pixels[pixIdx + 2] = finalB;
                        pixels[pixIdx + 3] = 255;
                    }
                }
            }
        }

        // Encode as PNG
        byte[] pngBytes;
        using (var bitmap = new SkiaSharp.SKBitmap(pixW, pixH, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul))
        {
            var span = bitmap.GetPixelSpan();
            pixels.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span));
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            pngBytes = encoded.ToArray();
        }

        return new SplatMapResult
        {
            PngBytes = pngBytes,
            Width = pixW,
            Height = pixH,
            ChunksWidth = chunksW,
            ChunksHeight = chunksH,
            MinChunkX = minCX,
            MinChunkY = minCY,
            LayerTextures = new List<string>() // not used for composite
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // INTERNAL — IFF chunk parsing
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Convert a 4-char chunk ID to the uint32 value that BitConverter.ToUInt32 reads
    /// from the ADT file. ADT stores chunk magic in big-endian ASCII order: the logical
    /// "MVER" is stored as bytes R,E,V,M in the file. On little-endian systems,
    /// BitConverter.ToUInt32 reads those bytes as 0x4D564552. So we reverse our input
    /// string to match: "MVER" → bytes R,E,V,M → ToUInt32 → match.
    /// </summary>
    private static uint ChunkId(string fourcc)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(fourcc);
        Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    /// <summary>
    /// Convert a uint32 chunk magic back to a human-readable 4-char string (for diagnostics).
    /// </summary>
    private static string ChunkIdToString(uint magic)
    {
        byte[] bytes = BitConverter.GetBytes(magic);
        Array.Reverse(bytes);
        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// Scan the ADT file for all top-level IFF chunks.
    /// Returns a dict of magic → (dataOffset, dataSize).
    /// For repeated chunks (MCNK), stores only the first occurrence;
    /// MCNK chunks are parsed separately via MCIN.
    /// </summary>
    private static Dictionary<uint, (int offset, int size)> ParseTopLevelChunks(byte[] data)
    {
        var result = new Dictionary<uint, (int offset, int size)>();
        int pos = 0;

        while (pos + 8 <= data.Length)
        {
            uint magic = BitConverter.ToUInt32(data, pos);
            uint size = BitConverter.ToUInt32(data, pos + 4);
            int dataStart = pos + 8;

            if (dataStart + size > data.Length)
                break; // Truncated chunk

            // For MCNK, we don't store in the top-level dict (there are 256 of them)
            // Instead, we'll find them via sequential scan
            if (magic != MAGIC_MCNK)
            {
                if (!result.ContainsKey(magic))
                    result[magic] = (dataStart, (int)size);
            }

            pos = dataStart + (int)size;
        }

        return result;
    }

    /// <summary>
    /// Find a sub-chunk within a parent chunk's data region.
    /// </summary>
    private static (int offset, int size)? FindSubChunk(byte[] data, int searchStart, int searchEnd, uint targetMagic)
    {
        int pos = searchStart;
        while (pos + 8 <= searchEnd)
        {
            uint magic = BitConverter.ToUInt32(data, pos);
            uint size = BitConverter.ToUInt32(data, pos + 4);
            int dataStart = pos + 8;

            if (magic == targetMagic)
                return (dataStart, (int)size);

            pos = dataStart + (int)size;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // INTERNAL — MTEX (texture filenames)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse MTEX chunk: null-separated texture filenames.
    /// Returns the full internal paths (e.g. "Tileset\\Elwynn\\ElwynnGrass01.blp").
    /// </summary>
    private static List<string> ParseMtex(byte[] data, int offset, int size)
    {
        return ParseNullSeparatedStrings(data, offset, size);
    }

    /// <summary>
    /// Parse a null-separated string table. Used by MTEX (texture filenames).
    /// MMDX / MWMO use BuildOffsetStringMap instead, because MMID / MWID
    /// reference strings by their byte offset within the chunk data.
    /// </summary>
    private static List<string> ParseNullSeparatedStrings(byte[] data, int offset, int size)
    {
        var result = new List<string>();
        int end = Math.Min(offset + size, data.Length);
        int strStart = offset;

        for (int i = offset; i < end; i++)
        {
            if (data[i] == 0)
            {
                if (i > strStart)
                {
                    string s = Encoding.ASCII.GetString(data, strStart, i - strStart);
                    result.Add(s);
                }
                strStart = i + 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Build a map from { byte offset within chunk data → string } for a
    /// null-separated string blob (MMDX / MWMO).
    ///
    /// The byte offset is the value MMID / MWID entries point at — so a MODF
    /// or MDDF entry resolves by:
    ///   path = map[ MWID[nameId] ]   (for WMOs)
    ///   path = map[ MMID[nameId] ]   (for M2 doodads)
    ///
    /// Unlike the original implementation, this map does NOT also include
    /// fallback sequential-index keys. The old fallback masked the real bug
    /// (treating MODF.nameId as a byte offset directly) and produced silent
    /// wrong-string resolution whenever a real string-start offset happened
    /// to equal one of the fallback index values.
    /// </summary>
    private static Dictionary<uint, string> BuildOffsetStringMap(byte[] data, int chunkDataOffset, int chunkDataSize)
    {
        var map = new Dictionary<uint, string>();
        int end = Math.Min(chunkDataOffset + chunkDataSize, data.Length);
        int strStart = chunkDataOffset;

        for (int i = chunkDataOffset; i < end; i++)
        {
            if (data[i] == 0)
            {
                if (i > strStart)
                {
                    uint offsetInChunk = (uint)(strStart - chunkDataOffset);
                    map[offsetInChunk] = Encoding.ASCII.GetString(data, strStart, i - strStart);
                }
                strStart = i + 1;
            }
        }
        return map;
    }

    /// <summary>
    /// Read a packed uint32[] from an IFF chunk's data region. Used for MMID
    /// and MWID — each entry is a byte offset into the corresponding string
    /// blob (MMDX / MWMO).
    /// </summary>
    private static uint[] ParseUint32Array(byte[] data, int offset, int size)
    {
        int count = size / 4;
        int end = Math.Min(offset + count * 4, data.Length);
        int actual = Math.Max(0, (end - offset) / 4);
        var arr = new uint[actual];
        for (int i = 0; i < actual; i++)
            arr[i] = BitConverter.ToUInt32(data, offset + i * 4);
        return arr;
    }

    // ═══════════════════════════════════════════════════════════════════
    // INTERNAL — MDDF (doodad placements)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse MDDF chunk: 36-byte doodad placement entries.
    ///
    /// nameId is an index into MMID. MMID[nameId] is the byte offset of the
    /// path string inside MMDX. The two-step indirection (rather than nameId
    /// being a direct byte offset) is what the file format actually specifies
    /// — see wowdev.wiki ADT chunk reference.
    ///
    /// mmidOffsets may be null on legacy/non-vanilla ADTs that omit MMID;
    /// in that case we treat nameId as a direct byte offset into MMDX as a
    /// last-resort fallback.
    /// </summary>
    private static List<DoodadPlacement> ParseMddf(byte[] data, int offset, int size, uint[]? mmidOffsets, Dictionary<uint, string> mmdxByOffset)
    {
        var result = new List<DoodadPlacement>();
        int entrySize = 36;
        int count = size / entrySize;

        for (int i = 0; i < count; i++)
        {
            int pos = offset + i * entrySize;
            if (pos + entrySize > data.Length) break;

            uint nameId = BitConverter.ToUInt32(data, pos);
            // uint uniqueId = BitConverter.ToUInt32(data, pos + 4);
            float posX = BitConverter.ToSingle(data, pos + 8);
            float posY = BitConverter.ToSingle(data, pos + 12);
            float posZ = BitConverter.ToSingle(data, pos + 16);
            float rotX = BitConverter.ToSingle(data, pos + 20);
            float rotY = BitConverter.ToSingle(data, pos + 24);
            float rotZ = BitConverter.ToSingle(data, pos + 28);
            ushort scale = BitConverter.ToUInt16(data, pos + 32);
            // ushort flags = BitConverter.ToUInt16(data, pos + 34);

            uint mmdxOffset;
            if (mmidOffsets != null && nameId < mmidOffsets.Length)
                mmdxOffset = mmidOffsets[nameId];
            else
                mmdxOffset = nameId; // legacy fallback

            string modelPath = mmdxByOffset.TryGetValue(mmdxOffset, out var name)
                ? name
                : $"Unknown_{nameId}";

            result.Add(new DoodadPlacement
            {
                ModelPath = modelPath,
                PosX = posX,
                PosY = posY,
                PosZ = posZ,
                RotX = rotX,
                RotY = rotY,
                RotZ = rotZ,
                Scale = scale / 1024.0f
            });
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // INTERNAL — MODF (WMO placements)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse MODF chunk: 64-byte WMO placement entries.
    ///
    /// nameId is an index into MWID; MWID[nameId] is the byte offset of the
    /// path string inside MWMO. This was discovered the hard way in Session 50
    /// for the write path (committing custom WMO placements) — same rule
    /// applies on the read path, which previously assumed nameId was a direct
    /// byte offset and silently misresolved entries in any zone where MODF
    /// nameIds collided with real MWMO byte offsets (Westfall, Duskwood,
    /// most of Eastern Kingdoms outside Elwynn).
    ///
    /// mwidOffsets may be null on legacy/non-vanilla ADTs that omit MWID;
    /// in that case we treat nameId as a direct byte offset into MWMO as a
    /// last-resort fallback.
    /// </summary>
    private static List<WmoInstance> ParseModf(byte[] data, int offset, int size, uint[]? mwidOffsets, Dictionary<uint, string> mwmoByOffset)
    {
        var result = new List<WmoInstance>();
        int entrySize = 64;
        int count = size / entrySize;

        for (int i = 0; i < count; i++)
        {
            int pos = offset + i * entrySize;
            if (pos + entrySize > data.Length) break;

            uint nameId = BitConverter.ToUInt32(data, pos);
            // uint uniqueId = BitConverter.ToUInt32(data, pos + 4);
            float posX = BitConverter.ToSingle(data, pos + 8);
            float posY = BitConverter.ToSingle(data, pos + 12);
            float posZ = BitConverter.ToSingle(data, pos + 16);
            float rotX = BitConverter.ToSingle(data, pos + 20);
            float rotY = BitConverter.ToSingle(data, pos + 24);
            float rotZ = BitConverter.ToSingle(data, pos + 28);
            float bbMinX = BitConverter.ToSingle(data, pos + 32);
            float bbMinY = BitConverter.ToSingle(data, pos + 36);
            float bbMinZ = BitConverter.ToSingle(data, pos + 40);
            float bbMaxX = BitConverter.ToSingle(data, pos + 44);
            float bbMaxY = BitConverter.ToSingle(data, pos + 48);
            float bbMaxZ = BitConverter.ToSingle(data, pos + 52);
            // ushort flags = BitConverter.ToUInt16(data, pos + 56);
            ushort doodadSet = BitConverter.ToUInt16(data, pos + 58);

            uint mwmoOffset;
            if (mwidOffsets != null && nameId < mwidOffsets.Length)
                mwmoOffset = mwidOffsets[nameId];
            else
                mwmoOffset = nameId; // legacy fallback

            string modelPath = mwmoByOffset.TryGetValue(mwmoOffset, out var name)
                ? name
                : $"Unknown_{nameId}";

            result.Add(new WmoInstance
            {
                ModelPath = modelPath,
                PosX = posX,
                PosY = posY,
                PosZ = posZ,
                RotX = rotX,
                RotY = rotY,
                RotZ = rotZ,
                BbMinX = bbMinX,
                BbMinY = bbMinY,
                BbMinZ = bbMinZ,
                BbMaxX = bbMaxX,
                BbMaxY = bbMaxY,
                BbMaxZ = bbMaxZ,
                DoodadSet = doodadSet
            });
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // INTERNAL — MCNK (terrain chunks with layers + alpha)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse all 256 MCNK terrain chunks from the ADT.
    /// Each MCNK has sub-chunks: MCLY (layers), MCAL (alpha), MCVT (heights), etc.
    /// </summary>
    private static McnkChunk[] ParseAllMcnk(byte[] data, Dictionary<uint, (int offset, int size)> topChunks)
    {
        var chunks = new McnkChunk[CHUNKS_PER_SIDE * CHUNKS_PER_SIDE];

        // Find all MCNK chunks by scanning the file
        int pos = 0;
        int chunkIdx = 0;

        while (pos + 8 <= data.Length && chunkIdx < 256)
        {
            uint magic = BitConverter.ToUInt32(data, pos);
            uint size = BitConverter.ToUInt32(data, pos + 4);
            int dataStart = pos + 8;

            if (dataStart + size > data.Length)
                break;

            if (magic == MAGIC_MCNK)
            {
                chunks[chunkIdx] = ParseMcnk(data, dataStart, (int)size);
                chunkIdx++;
            }

            pos = dataStart + (int)size;
        }

        return chunks;
    }

    /// <summary>
    /// Parse a single MCNK chunk.
    /// mcnkDataStart = position right after the 8-byte IFF header (magic+size).
    /// The 128-byte MCNK header is at mcnkDataStart, sub-chunks follow after it.
    /// IMPORTANT: The offset fields in the MCNK header are relative to the
    /// chunk start (mcnkDataStart - 8, i.e. the position of the KNCM magic),
    /// NOT relative to mcnkDataStart.
    /// </summary>
    private static McnkChunk ParseMcnk(byte[] data, int mcnkDataStart, int mcnkSize)
    {
        var chunk = new McnkChunk();
        int mcnkEnd = mcnkDataStart + mcnkSize;

        // The base address for all header offsets — the position of the IFF magic bytes
        int mcnkBase = mcnkDataStart - 8;

        // MCNK header (128 bytes) — contains flags, layer count, offsets to sub-chunks
        if (mcnkDataStart + MCNK_HEADER_SIZE > data.Length)
            return chunk;

        chunk.IndexX = (int)BitConverter.ToUInt32(data, mcnkDataStart + 0x04);
        chunk.IndexY = (int)BitConverter.ToUInt32(data, mcnkDataStart + 0x08);
        int nLayers = (int)BitConverter.ToUInt32(data, mcnkDataStart + 0x0C);

        // Sub-chunk offsets are relative to mcnkBase (chunk start including IFF header)
        uint ofsLayer = BitConverter.ToUInt32(data, mcnkDataStart + 0x1C);
        uint ofsAlpha = BitConverter.ToUInt32(data, mcnkDataStart + 0x24);
        uint sizeAlpha = BitConverter.ToUInt32(data, mcnkDataStart + 0x28);
        // MCLQ — per-chunk liquid (water/lava/slime). Vanilla 1.x stores liquid here.
        uint ofsLiquid = BitConverter.ToUInt32(data, mcnkDataStart + 0x60);
        uint sizeLiquid = BitConverter.ToUInt32(data, mcnkDataStart + 0x64);

        // Parse MCLY (texture layers)
        if (ofsLayer > 0 && mcnkBase + ofsLayer + 8 <= data.Length)
        {
            int mclyPos = mcnkBase + (int)ofsLayer;
            if (BitConverter.ToUInt32(data, mclyPos) == MAGIC_MCLY)
            {
                uint mclySize = BitConverter.ToUInt32(data, mclyPos + 4);
                int mclyData = mclyPos + 8;
                chunk.Layers = ParseMcly(data, mclyData, (int)mclySize, nLayers);
            }
        }

        // Parse MCAL (alpha maps)
        if (ofsAlpha > 0 && sizeAlpha > 0 && chunk.Layers != null && mcnkBase + ofsAlpha + 8 <= data.Length)
        {
            int mcalPos = mcnkBase + (int)ofsAlpha;
            if (BitConverter.ToUInt32(data, mcalPos) == MAGIC_MCAL)
            {
                uint mcalSize = BitConverter.ToUInt32(data, mcalPos + 4);
                int mcalData = mcalPos + 8;
                ParseMcal(data, mcalData, (int)mcalSize, chunk.Layers);
            }
            else
            {
                // Some ADTs store MCAL data directly without IFF header
                ParseMcal(data, mcalPos, (int)sizeAlpha, chunk.Layers);
            }
        }

        // Parse MCLQ (liquid layers).
        //   if (sizeLiquid > 8) → MCLQ block exists.
        //   At mcnkBase + ofsLiquid: 4-byte 'MCLQ' magic + 4-byte size + N×800-byte mclq layers
        //   N = (sizeLiquid - 8) / sizeof(mclq) = (sizeLiquid - 8) / 800
        if (sizeLiquid > 8 && ofsLiquid > 0 && mcnkBase + ofsLiquid + 8 <= data.Length)
        {
            int mclqPos = mcnkBase + (int)ofsLiquid;
            if (BitConverter.ToUInt32(data, mclqPos) == MAGIC_MCLQ)
            {
                int layerCount = ((int)sizeLiquid - 8) / MCLQ_LAYER_SIZE;
                if (layerCount > 0)
                {
                    int payloadStart = mclqPos + 8;
                    chunk.Liquid = ParseMclqLayers(data, payloadStart, layerCount);
                }
            }
        }

        if (chunk.Layers == null)
            chunk.Layers = Array.Empty<MclyLayer>();

        return chunk;
    }

    /// <summary>
    /// Parse MCLY entries (16 bytes each): texture layer definitions.
    /// </summary>
    private static MclyLayer[] ParseMcly(byte[] data, int offset, int size, int expectedCount)
    {
        int entrySize = 16;
        int count = Math.Min(size / entrySize, expectedCount);
        count = Math.Min(count, 4); // max 4 layers per chunk

        var layers = new MclyLayer[count];
        for (int i = 0; i < count; i++)
        {
            int pos = offset + i * entrySize;
            if (pos + entrySize > data.Length) break;

            layers[i] = new MclyLayer
            {
                TextureIndex = (int)BitConverter.ToUInt32(data, pos),
                Flags = BitConverter.ToUInt32(data, pos + 4),
                OffsetInMcal = BitConverter.ToUInt32(data, pos + 8),
                EffectId = BitConverter.ToInt32(data, pos + 12)
            };
        }

        return layers;
    }

    /// <summary>
    /// Parse MCLQ liquid layers from inside an MCNK chunk (vanilla 1.x format).
    ///
    /// Per-layer 800-byte struct `mclq`:
    ///   +0   float min_height                       (4)
    ///   +4   float max_height                       (4)
    ///   +8   mclq_vertex vertices[9*9]              (648 = 81 × 8 bytes/vertex)
    ///           each vertex: 4 bytes (water_vert | magma_vert union) + float height
    ///   +656 mclq_tile tiles[8*8]                   (64 bytes, 1 each)
    ///           bitpacked: bits 0-2 liquid_type, bit 3 dont_render,
    ///                       bit 4 flag_0x10, bit 5 flag_0x20, bit 6 fishable, bit 7 fatigue
    ///   +720 uint32 n_flowvs                        (4)
    ///   +724 mclq_flowvs flowvs[2]                  (76 = 2 × 38, always present)
    ///   =800
    ///
    /// We pull min/max height, the 81 vertex heights, the 64 tile-render flags,
    /// and the dominant liquid_type code. Flow data is ignored.
    /// </summary>
    private static List<MclqLayer> ParseMclqLayers(byte[] data, int payloadStart, int layerCount)
    {
        var result = new List<MclqLayer>(layerCount);
        int pos = payloadStart;
        for (int li = 0; li < layerCount; li++)
        {
            if (pos + MCLQ_LAYER_SIZE > data.Length) break;

            float minH = BitConverter.ToSingle(data, pos + 0);
            float maxH = BitConverter.ToSingle(data, pos + 4);

            // 9×9 vertices, each 8 bytes: 4 bytes water/magma metadata + 4 bytes float height.
            var heights = new float[81];
            int vbase = pos + 8;
            for (int i = 0; i < 81; i++)
            {
                int vp = vbase + i * 8;
                // bytes [vp .. vp+3] = water/magma meta (depth, flow_0, flow_1, filler) — skip.
                heights[i] = BitConverter.ToSingle(data, vp + 4);
            }

            // 8×8 tile flag bytes start after the 81-vertex block.
            int tbase = vbase + 81 * 8;
            var tileRender = new bool[64];
            byte dominantType = 0;
            for (int i = 0; i < 64; i++)
            {
                byte b = data[tbase + i];
                bool dontRender = (b & 0x08) != 0;
                tileRender[i] = !dontRender;
                if (!dontRender)
                {
                    // bits 0..2 are the liquid_type code.
                    // case 1 → ocean, 3 → slime, 4 → river/water, 6 → magma.
                    byte t = (byte)(b & 0x07);
                    if (t != 0) dominantType = t;
                }
            }

            result.Add(new MclqLayer
            {
                LiquidType = dominantType,
                MinHeight = minH,
                MaxHeight = maxH,
                VertexHeights = heights,
                TileRender = tileRender,
            });

            pos += MCLQ_LAYER_SIZE;
        }
        return result;
    }

    /// <summary>
    /// Parse MCAL alpha maps and attach to corresponding layers.
    /// Layer 0 has no alpha (it's the base). Layers 1-3 have alpha maps.
    ///
    /// IMPORTANT: The bigAlpha flag (0x100) cannot be trusted blindly.
    /// Some vanilla 1.12.1 ADTs set bigAlpha on all layers but the actual
    /// data is 2048 bytes (32×64) per layer, packed contiguously.
    /// We determine actual size by looking at the gap between consecutive
    /// layer offsets within MCAL. If the gap is ~2048, the data is 32-wide
    /// regardless of what the flag says.
    /// </summary>
    private static void ParseMcal(byte[] data, int mcalStart, int mcalSize, MclyLayer[] layers)
    {
        for (int i = 1; i < layers.Length; i++) // skip layer 0
        {
            var layer = layers[i];
            bool compressed = (layer.Flags & MCLY_FLAG_COMPRESSED_ALPHA) != 0;
            bool bigAlpha = (layer.Flags & MCLY_FLAG_BIG_ALPHA) != 0;

            int alphaOffset = mcalStart + (int)layer.OffsetInMcal;

            byte[]? rawAlpha = null;

            if (compressed)
            {
                // Compressed alpha: decompress to 4096 (64×64)
                rawAlpha = DecompressAlpha(data, alphaOffset, mcalStart + mcalSize, ALPHA_SIZE_FULL * ALPHA_SIZE_FULL);
            }
            else
            {
                // Determine actual alpha size from layer offset gaps.
                // This is more reliable than the bigAlpha flag alone.
                int actualSize;
                if (i + 1 < layers.Length && !((layers[i + 1].Flags & MCLY_FLAG_COMPRESSED_ALPHA) != 0))
                {
                    // Gap to next layer's alpha offset tells us real size
                    actualSize = (int)(layers[i + 1].OffsetInMcal - layer.OffsetInMcal);
                }
                else
                {
                    // Last alpha layer (or next is compressed): use remaining MCAL size
                    actualSize = mcalSize - (int)layer.OffsetInMcal;
                }

                // Clamp to valid sizes: either 4096 (64×64) or 2048 (32×64)
                int readSize;
                if (actualSize >= ALPHA_SIZE_FULL * ALPHA_SIZE_FULL)
                    readSize = ALPHA_SIZE_FULL * ALPHA_SIZE_FULL; // 4096
                else
                    readSize = ALPHA_SIZE_HALF * ALPHA_SIZE_FULL; // 2048

                if (alphaOffset + readSize <= data.Length)
                {
                    rawAlpha = new byte[readSize];
                    Array.Copy(data, alphaOffset, rawAlpha, 0, readSize);
                }
            }

            if (rawAlpha == null) continue;

            byte[] fullAlpha;

            // 2048-byte format: 64×64 grid of 4-bit alpha values, two per byte.
            // Each byte stores y+0 in low nibble, y+1 in high nibble. Convert to
            // 8-bit by replicating the 4 bits (v | v<<4), so 0xA → 0xAA.
            //
            // The previous implementation treated this as 32×64 8-bit stretched
            // horizontally — that doubled effective alpha resolution loss and
            // misaligned values, producing artifacts especially at chunk seams.
            if (rawAlpha.Length == ALPHA_SIZE_HALF * ALPHA_SIZE_FULL)
            {
                fullAlpha = new byte[ALPHA_SIZE_FULL * ALPHA_SIZE_FULL];
                int inIdx = 0;
                for (int x = 0; x < ALPHA_SIZE_FULL; x++)
                {
                    for (int y = 0; y < ALPHA_SIZE_FULL; y += 2)
                    {
                        byte packed = rawAlpha[inIdx++];
                        byte lower = (byte)(packed & 0x0F);
                        byte upper = (byte)((packed >> 4) & 0x0F);
                        fullAlpha[x * ALPHA_SIZE_FULL + y + 0] = (byte)(lower | (lower << 4));
                        fullAlpha[x * ALPHA_SIZE_FULL + y + 1] = (byte)(upper | (upper << 4));
                    }
                }

                // Old-format alpha maps have garbage in the last row and last column —
                // it's padding the encoder never wrote real data to.
                // Without this fix, chunk boundaries show as a hard line.
                for (int e = 0; e < ALPHA_SIZE_FULL; e++)
                {
                    fullAlpha[e * ALPHA_SIZE_FULL + 63] = fullAlpha[e * ALPHA_SIZE_FULL + 62];
                    fullAlpha[63 * ALPHA_SIZE_FULL + e] = fullAlpha[62 * ALPHA_SIZE_FULL + e];
                }
                fullAlpha[63 * ALPHA_SIZE_FULL + 63] = fullAlpha[62 * ALPHA_SIZE_FULL + 62];
            }
            else
            {
                // bigAlpha (4096 bytes): already 64×64 at 8 bits per texel, no
                // transform needed. The garbage-edge fix does NOT apply to this
                // format — bigAlpha encodes real data in every texel.
                fullAlpha = rawAlpha;
            }

            layer.AlphaMap = fullAlpha;
        }
    }

    /// <summary>
    /// Decompress RLE-encoded alpha map data.
    /// Format: each control byte:
    ///   bit 7 set → fill mode: next byte repeated (controlByte & 0x7F) times
    ///   bit 7 clear → copy mode: copy next (controlByte & 0x7F) bytes literally
    /// Output size depends on bigAlpha flag: 4096 (64×64) or 2048 (32×64).
    /// </summary>
    private static byte[] DecompressAlpha(byte[] data, int offset, int maxOffset, int targetSize = 4096)
    {
        var result = new byte[targetSize];
        int outPos = 0;
        int inPos = offset;

        while (outPos < result.Length && inPos < maxOffset)
        {
            byte control = data[inPos++];
            int count = control & 0x7F;

            if ((control & 0x80) != 0)
            {
                // Fill mode
                if (inPos >= maxOffset) break;
                byte fillValue = data[inPos++];
                for (int j = 0; j < count && outPos < result.Length; j++)
                    result[outPos++] = fillValue;
            }
            else
            {
                // Copy mode
                for (int j = 0; j < count && outPos < result.Length && inPos < maxOffset; j++)
                    result[outPos++] = data[inPos++];
            }
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // DTOs
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Complete parsed ADT tile data.</summary>
    public class AdtResult
    {
        public int GridX { get; set; }
        public int GridY { get; set; }

        /// <summary>Texture filenames from MTEX (full MPQ paths).</summary>
        public List<string> Textures { get; set; } = new();

        /// <summary>Doodad placements from MDDF.</summary>
        public List<DoodadPlacement>? Doodads { get; set; }

        /// <summary>WMO placements from MODF.</summary>
        public List<WmoInstance>? Wmos { get; set; }

        /// <summary>256 terrain chunks (16×16), indexed as [y * 16 + x].</summary>
        public McnkChunk[]? Chunks { get; set; }
    }

    /// <summary>A single 33-yard terrain chunk with texture layers and alpha.</summary>
    public class McnkChunk
    {
        public int IndexX { get; set; }
        public int IndexY { get; set; }
        public MclyLayer[] Layers { get; set; } = Array.Empty<MclyLayer>();

        /// <summary>
        /// MCLQ liquid layers for this chunk (null = no water, normally 1 layer when present).
        /// Each layer has a 9×9 vertex grid and 8×8 tile mask.
        /// </summary>
        public List<MclqLayer>? Liquid { get; set; }
    }

    /// <summary>One MCLQ liquid layer inside an MCNK chunk (vanilla 1.x format).</summary>
    public class MclqLayer
    {
        /// <summary>
        /// Liquid type code from the per-tile bits 0..2. Common values:
        ///   1 = ocean, 3 = slime, 4 = river, 6 = magma
        /// </summary>
        public byte LiquidType { get; set; }

        public float MinHeight { get; set; }
        public float MaxHeight { get; set; }

        /// <summary>9×9 vertex heights in WoW world Z, row-major (z*9+x).</summary>
        public float[] VertexHeights { get; set; } = Array.Empty<float>();

        /// <summary>
        /// 8×8 tile-render mask, row-major (z*8+x). True = render this tile (water visible),
        /// false = dont_render bit was set (hole in the water surface).
        /// </summary>
        public bool[] TileRender { get; set; } = Array.Empty<bool>();
    }

    /// <summary>Texture layer definition from MCLY (16 bytes).</summary>
    public class MclyLayer
    {
        /// <summary>Index into the ADT's MTEX texture filename table.</summary>
        public int TextureIndex { get; set; }

        /// <summary>Layer flags (0x100=big alpha, 0x200=compressed alpha).</summary>
        public uint Flags { get; set; }

        /// <summary>Byte offset of this layer's alpha data within MCAL.</summary>
        public uint OffsetInMcal { get; set; }

        /// <summary>Ground effect ID (-1 = none).</summary>
        public int EffectId { get; set; }

        /// <summary>Decoded alpha map (64×64 or 32×64 bytes). Null for layer 0.</summary>
        public byte[]? AlphaMap { get; set; }
    }

    /// <summary>Raw doodad placement from MDDF (36 bytes).</summary>
    public class DoodadPlacement
    {
        public string ModelPath { get; set; } = "";
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float Scale { get; set; } = 1.0f;
    }

    /// <summary>Classified doodad ready for Three.js rendering.</summary>
    public class DoodadInstance
    {
        public string ModelPath { get; set; } = "";
        public string Type { get; set; } = "generic";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float RotY { get; set; }
        public float Scale { get; set; } = 1.0f;
    }

    /// <summary>WMO building placement from MODF (64 bytes).</summary>
    public class WmoInstance
    {
        public string ModelPath { get; set; } = "";
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float BbMinX { get; set; }
        public float BbMinY { get; set; }
        public float BbMinZ { get; set; }
        public float BbMaxX { get; set; }
        public float BbMaxY { get; set; }
        public float BbMaxZ { get; set; }

        /// <summary>
        /// Which doodad set inside the WMO root to activate for this placement.
        /// Set 0 is conventionally the always-active "Set_$DefaultGlobal".
        /// Some WMOs ship multiple sets (e.g. day/night, intact/destroyed).
        /// </summary>
        public ushort DoodadSet { get; set; }
    }

    /// <summary>Composite splat map result with texture assignments.</summary>
    public class SplatMapResult
    {
        /// <summary>RGBA PNG bytes of the splat map.</summary>
        public byte[] PngBytes { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
        public int ChunksWidth { get; set; }
        public int ChunksHeight { get; set; }
        public int MinChunkX { get; set; }
        public int MinChunkY { get; set; }

        /// <summary>Texture filenames in layer order (index 0-3 = R,G,B,A channels).</summary>
        public List<string> LayerTextures { get; set; } = new();
    }
}