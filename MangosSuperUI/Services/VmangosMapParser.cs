using System;
using System.Collections.Generic;
using System.IO;

namespace MangosSuperUI.Services;

/// <summary>
/// Reads VMaNGOS extracted .map files and produces heightmap geometry
/// for the Visual Lab 3D terrain backdrop.
///
/// VMaNGOS .map file format (from GridMapDefines.h / GridMap.cpp):
///
///   GridMapFileHeader (40 bytes):
///     uint32 mapMagic         "MAPS"
///     uint32 versionMagic     "z1.4" (version string)
///     uint32 areaMapOffset
///     uint32 areaMapSize
///     uint32 heightMapOffset
///     uint32 heightMapSize
///     uint32 liquidMapOffset
///     uint32 liquidMapSize
///     uint32 holesOffset
///     uint32 holesSize
///
///   At heightMapOffset → GridMapHeightHeader (16 bytes):
///     uint32 fourcc           "MHGT"
///     uint32 flags            (0x01=NO_HEIGHT, 0x02=AS_INT16, 0x04=AS_INT8)
///     float  gridHeight       (base height)
///     float  gridMaxHeight    (max height, used for int16/int8 scaling)
///
///   Then immediately:
///     float/uint16/uint8[129*129]  V9 — outer vertex grid (continuous across all 16×16 chunks)
///     float/uint16/uint8[128*128]  V8 — inner vertex grid (center of each quad cell)
///
/// Filename encoding: MMMXXYY.map
///   MMM = map ID (000=Eastern Kingdoms, 001=Kalimdor)
///   XX  = grid X (00-63)
///   YY  = grid Y (00-63)
///
/// Session 37: Visual Lab Tier 3 terrain backdrop.
/// </summary>
public static class VmangosMapParser
{
    // V9 = 129×129 continuous outer vertices (16 chunks × 8 cells + 1 edge = 129)
    // V8 = 128×128 continuous inner vertices (center of each cell)
    public const int V9_SIDE = 129;
    public const int V8_SIDE = 128;
    public const int V9_COUNT = V9_SIDE * V9_SIDE; // 16641
    public const int V8_COUNT = V8_SIDE * V8_SIDE; // 16384

    public const int CHUNKS_PER_SIDE = 16;
    public const float CHUNK_SIZE = 33.3333f;   // 533.333 / 16
    public const float GRID_SIZE = 533.3333f;
    public const float CELL_SIZE = CHUNK_SIZE / 8.0f; // ~4.167 yards per vertex spacing

    // File header: 40 bytes
    private const int HEADER_SIZE = 40;
    // Height sub-header: 16 bytes (fourcc + flags + gridHeight + gridMaxHeight)
    private const int HEIGHT_HEADER_SIZE = 16;

    /// <summary>
    /// Parse a VMaNGOS .map file and return terrain geometry for the Visual Lab.
    /// </summary>
    public static TerrainResult? Parse(string mapFilePath, int centerChunkX = 8, int centerChunkY = 8, int radius = 3)
    {
        if (!File.Exists(mapFilePath))
            return null;
        return Parse(File.ReadAllBytes(mapFilePath), centerChunkX, centerChunkY, radius);
    }

    /// <summary>
    /// Parse .map bytes and return terrain geometry.
    /// </summary>
    public static TerrainResult? Parse(byte[] data, int centerChunkX = 8, int centerChunkY = 8, int radius = 3)
    {
        if (data == null || data.Length < HEADER_SIZE)
            return null;

        // Validate magic: "MAPS"
        if (data[0] != 'M' || data[1] != 'A' || data[2] != 'P' || data[3] != 'S')
            return null;

        // Read file header (10 uint32s = 40 bytes)
        // [0]=magic [1]=version [2]=areaOff [3]=areaSz [4]=heightOff [5]=heightSz
        // [6]=liquidOff [7]=liquidSz [8]=holesOff [9]=holesSz
        uint heightMapOffset = BitConverter.ToUInt32(data, 16);
        uint heightMapSize = BitConverter.ToUInt32(data, 20);

        if (heightMapOffset == 0 || heightMapOffset + HEIGHT_HEADER_SIZE > data.Length)
            return null;

        // Read height sub-header at heightMapOffset
        int hPos = (int)heightMapOffset;

        // Validate fourcc: "MHGT"
        if (data[hPos] != 'M' || data[hPos + 1] != 'H' || data[hPos + 2] != 'G' || data[hPos + 3] != 'T')
            return null;

        uint flags = BitConverter.ToUInt32(data, hPos + 4);
        float gridHeight = BitConverter.ToSingle(data, hPos + 8);
        float gridMaxHeight = BitConverter.ToSingle(data, hPos + 12);
        hPos += HEIGHT_HEADER_SIZE;

        bool noHeight = (flags & 0x0001) != 0;
        bool asInt16 = (flags & 0x0002) != 0;
        bool asInt8 = (flags & 0x0004) != 0;

        if (noHeight)
            return BuildFlatResult(centerChunkX, centerChunkY, radius, gridHeight);

        // Read V9 (129×129) and V8 (128×128) height arrays
        float[] v9 = new float[V9_COUNT];
        float[] v8 = new float[V8_COUNT];

        if (asInt16)
        {
            float multiplier = (gridMaxHeight - gridHeight) / 65535.0f;
            int needed = (V9_COUNT + V8_COUNT) * 2;
            if (hPos + needed > data.Length)
                return null;

            for (int i = 0; i < V9_COUNT; i++)
            {
                ushort val = BitConverter.ToUInt16(data, hPos);
                hPos += 2;
                v9[i] = gridHeight + val * multiplier;
            }
            for (int i = 0; i < V8_COUNT; i++)
            {
                ushort val = BitConverter.ToUInt16(data, hPos);
                hPos += 2;
                v8[i] = gridHeight + val * multiplier;
            }
        }
        else if (asInt8)
        {
            float multiplier = (gridMaxHeight - gridHeight) / 255.0f;
            int needed = V9_COUNT + V8_COUNT;
            if (hPos + needed > data.Length)
                return null;

            for (int i = 0; i < V9_COUNT; i++)
            {
                v9[i] = gridHeight + data[hPos] * multiplier;
                hPos++;
            }
            for (int i = 0; i < V8_COUNT; i++)
            {
                v8[i] = gridHeight + data[hPos] * multiplier;
                hPos++;
            }
        }
        else
        {
            // Float32
            int needed = (V9_COUNT + V8_COUNT) * 4;
            if (hPos + needed > data.Length)
                return null;

            for (int i = 0; i < V9_COUNT; i++)
            {
                v9[i] = BitConverter.ToSingle(data, hPos);
                hPos += 4;
            }
            for (int i = 0; i < V8_COUNT; i++)
            {
                v8[i] = BitConverter.ToSingle(data, hPos);
                hPos += 4;
            }
        }

        return BuildMeshResult(v9, v8, centerChunkX, centerChunkY, radius);
    }

    /// <summary>
    /// Build a Three.js-ready mesh from V9 outer vertices for the requested chunk region.
    /// V9 is a continuous 129×129 grid: v9[y * 129 + x] where x,y ∈ [0,128].
    /// Each chunk spans 8 cells, so chunk (cx,cy) covers V9 rows [cy*8..(cy+1)*8]
    /// and cols [cx*8..(cx+1)*8].
    /// </summary>
    private static TerrainResult BuildMeshResult(float[] v9, float[] v8, int cx, int cy, int radius)
    {
        int minCX = Math.Max(0, cx - radius);
        int maxCX = Math.Min(15, cx + radius);
        int minCY = Math.Max(0, cy - radius);
        int maxCY = Math.Min(15, cy + radius);

        // V9 vertex range for selected chunks
        int v9StartX = minCX * 8;
        int v9EndX = maxCX * 8 + 8; // inclusive last vertex
        int v9StartY = minCY * 8;
        int v9EndY = maxCY * 8 + 8;

        int vertsW = v9EndX - v9StartX + 1;
        int vertsH = v9EndY - v9StartY + 1;

        float[] positions = new float[vertsW * vertsH * 3];

        // Find height range for the selected region
        float minHeight = float.MaxValue, maxHeight = float.MinValue;
        for (int vy = v9StartY; vy <= v9EndY; vy++)
        {
            for (int vx = v9StartX; vx <= v9EndX; vx++)
            {
                float h = v9[vy * V9_SIDE + vx];
                if (h < minHeight) minHeight = h;
                if (h > maxHeight) maxHeight = h;
            }
        }

        float midHeight = (minHeight + maxHeight) * 0.5f;

        // Center the mesh at Three.js origin
        float totalWidth = (vertsW - 1) * CELL_SIZE;
        float totalDepth = (vertsH - 1) * CELL_SIZE;
        float offsetX = -totalWidth * 0.5f;
        float offsetZ = -totalDepth * 0.5f;

        // Height scale: map real-world height range to comfortable visual range
        float heightRange = maxHeight - minHeight;
        float heightScale = heightRange > 0 ? Math.Min(0.15f, 20.0f / heightRange) : 0.15f;

        for (int vy = v9StartY; vy <= v9EndY; vy++)
        {
            for (int vx = v9StartX; vx <= v9EndX; vx++)
            {
                int localX = vx - v9StartX;
                int localY = vy - v9StartY;
                int vi = (localY * vertsW + localX) * 3;

                float h = v9[vy * V9_SIDE + vx];

                positions[vi + 0] = offsetX + localX * CELL_SIZE;      // X
                positions[vi + 1] = (h - midHeight) * heightScale;     // Y (height)
                positions[vi + 2] = offsetZ + localY * CELL_SIZE;      // Z
            }
        }

        // Build triangle indices
        var indices = new List<int>();
        for (int y = 0; y < vertsH - 1; y++)
        {
            for (int x = 0; x < vertsW - 1; x++)
            {
                int tl = y * vertsW + x;
                int tr = tl + 1;
                int bl = (y + 1) * vertsW + x;
                int br = bl + 1;

                indices.Add(tl); indices.Add(bl); indices.Add(tr);
                indices.Add(tr); indices.Add(bl); indices.Add(br);
            }
        }

        return new TerrainResult
        {
            Positions = positions,
            Indices = indices.ToArray(),
            VertsWidth = vertsW,
            VertsHeight = vertsH,
            ChunksWidth = maxCX - minCX + 1,
            ChunksHeight = maxCY - minCY + 1,
            HeightScale = heightScale,
            MinHeight = minHeight,
            MaxHeight = maxHeight
        };
    }

    /// <summary>
    /// Build a flat terrain result for grids with MAP_HEIGHT_NO_HEIGHT flag.
    /// </summary>
    private static TerrainResult BuildFlatResult(int cx, int cy, int radius, float height)
    {
        int minCX = Math.Max(0, cx - radius);
        int maxCX = Math.Min(15, cx + radius);
        int minCY = Math.Max(0, cy - radius);
        int maxCY = Math.Min(15, cy + radius);

        int vertsW = (maxCX - minCX + 1) * 8 + 1;
        int vertsH = (maxCY - minCY + 1) * 8 + 1;

        float totalWidth = (vertsW - 1) * CELL_SIZE;
        float totalDepth = (vertsH - 1) * CELL_SIZE;
        float offsetX = -totalWidth * 0.5f;
        float offsetZ = -totalDepth * 0.5f;

        float[] positions = new float[vertsW * vertsH * 3];
        for (int y = 0; y < vertsH; y++)
        {
            for (int x = 0; x < vertsW; x++)
            {
                int vi = (y * vertsW + x) * 3;
                positions[vi + 0] = offsetX + x * CELL_SIZE;
                positions[vi + 1] = 0;
                positions[vi + 2] = offsetZ + y * CELL_SIZE;
            }
        }

        var indices = new List<int>();
        for (int y = 0; y < vertsH - 1; y++)
        {
            for (int x = 0; x < vertsW - 1; x++)
            {
                int tl = y * vertsW + x;
                int tr = tl + 1;
                int bl = (y + 1) * vertsW + x;
                int br = bl + 1;
                indices.Add(tl); indices.Add(bl); indices.Add(tr);
                indices.Add(tr); indices.Add(bl); indices.Add(br);
            }
        }

        return new TerrainResult
        {
            Positions = positions,
            Indices = indices.ToArray(),
            VertsWidth = vertsW,
            VertsHeight = vertsH,
            ChunksWidth = maxCX - minCX + 1,
            ChunksHeight = maxCY - minCY + 1,
            HeightScale = 0,
            MinHeight = height,
            MaxHeight = height
        };
    }

    // ── Filename helpers ──

    /// <summary>
    /// Build a .map filename from map ID and grid coordinates.
    /// Format: MMMXXYY.map (e.g. "0003248.map" = map 0, grid 32,48)
    /// </summary>
    public static string BuildFilename(int mapId, int gridX, int gridY)
    {
        return $"{mapId:D3}{gridX:D2}{gridY:D2}.map";
    }

    /// <summary>
    /// Parse a .map filename into its components.
    /// </summary>
    public static (int mapId, int gridX, int gridY)? ParseFilename(string filename)
    {
        string name = Path.GetFileNameWithoutExtension(filename);
        if (name.Length != 7 ||
            !int.TryParse(name[..3], out int mapId) ||
            !int.TryParse(name[3..5], out int gridX) ||
            !int.TryParse(name[5..7], out int gridY))
            return null;
        return (mapId, gridX, gridY);
    }

    // ── DTOs ──

    public class TerrainResult
    {
        public float[] Positions { get; set; } = Array.Empty<float>();
        public int[] Indices { get; set; } = Array.Empty<int>();
        public int VertsWidth { get; set; }
        public int VertsHeight { get; set; }
        public int ChunksWidth { get; set; }
        public int ChunksHeight { get; set; }
        public float HeightScale { get; set; }
        public float MinHeight { get; set; }
        public float MaxHeight { get; set; }
    }

    public class LiquidResult
    {
        // CELL coordinates (0..127, not chunk 0..15). The extractor uses
        // ADT_GRID_SIZE=128 cells per side for the liquid mask.
        public byte OffsetX { get; set; }
        public byte OffsetY { get; set; }
        // Vertex-grid dimensions (= cell-grid-size + 1). Heights[] has Width*Height entries.
        public byte Width { get; set; }
        public byte Height { get; set; }
        // Base/min liquid height — NOT the water surface! This is minHeight from
        // the writer, with empty cells pre-filled to CONF_use_minHeight (-500.0).
        // Useful only as a fallback when Heights[] has no data for a given cell.
        public float LiquidLevel { get; set; }
        public byte HeaderFlags { get; set; }        // 0x01 = NO_TYPE, 0x02 = NO_HEIGHT
        public byte GlobalLiquidFlags { get; set; }  // type byte when NO_TYPE
        public ushort LiquidType { get; set; }       // entry id when NO_TYPE

        // Per-vertex heights in a Width × Height grid spanning the cell-bounding-box
        // of liquid cells. Most entries are sentinel (-500.0); real water heights
        // live inside the chunk regions that have CellFlags != 0.
        public float[]? Heights { get; set; }

        // 16×16 per-chunk liquid type byte. THIS is the authoritative "does this
        // chunk have liquid" mask. Indexed [chunkRow*16 + chunkCol] over the
        // FULL tile chunk grid (not the cell-bounding-box).
        //   byte == 0    → no liquid in this chunk
        //   byte nonzero → liquid present (0x08=water, 0x02=ocean, 0x01=magma,
        //                  0x04=slime, 0x10=deep, 0x20=wmo-water)
        // null when NO_TYPE is set (then every chunk in the cell-bbox region
        // uses GlobalLiquidFlags).
        public byte[]? CellFlags { get; set; }

        // 16×16 per-chunk liquid entry id (LiquidType DBC). null when NO_TYPE.
        public ushort[]? LiquidEntry { get; set; }
    }

    /// <summary>
    /// Parse liquid data from a VMaNGOS .map file.
    ///
    /// Byte layout (verified against vmangos/src/game/Maps/GridMap.cpp
    /// loadGridMapLiquidData() and GridMapDefines.h GridMapLiquidHeader):
    ///
    ///   At liquidMapOffset:
    ///     uint32 fourcc        "MLIQ"          (+0)
    ///     uint8  flags                          (+4)   header flags
    ///     uint8  liquidFlags                    (+5)   default liquid type byte (used when NO_TYPE)
    ///     uint16 liquidType                     (+6)   default liquid entry id   (used when NO_TYPE)
    ///     uint8  offsetX                        (+8)   start chunk X in 0..15
    ///     uint8  offsetY                        (+9)   start chunk Y
    ///     uint8  width                          (+10)  chunk-region width  in 1..16
    ///     uint8  height                         (+11)  chunk-region height
    ///     float  liquidLevel                    (+12)  base/global height
    ///                                                  (= 16 bytes header total)
    ///
    ///   if !(flags &amp; MAP_LIQUID_NO_TYPE = 0x01):
    ///     uint16[16*16] liquidEntry             (512 bytes) per-chunk liquid id
    ///     uint8 [16*16] liquidFlags             (256 bytes) per-chunk liquid type byte
    ///   (else: every chunk in the region implicitly uses header.liquidFlags / header.liquidType)
    ///
    ///   if !(flags &amp; MAP_LIQUID_NO_HEIGHT = 0x02):
    ///     float[width*height] heights           per-cell height (NOT per-vertex)
    ///   (else: every cell uses liquidLevel)
    ///
    /// VMaNGOS per-chunk semantics (from GridMap::getLiquidStatus):
    ///   uint8 cellByte = m_liquidFlags[chunkRow*16 + chunkCol]
    ///                  (or m_liquidGlobalFlags when NO_TYPE)
    ///   if (cellByte == 0) → no liquid in this chunk
    ///   nonzero → liquid present (bits 0x01=magma, 0x02=ocean, 0x04=slime,
    ///             0x08=water, 0x10=deep, 0x20=wmo-water)
    /// </summary>
    public static LiquidResult? ParseLiquid(string mapFilePath)
    {
        if (!File.Exists(mapFilePath)) return null;
        return ParseLiquid(File.ReadAllBytes(mapFilePath));
    }

    public static LiquidResult? ParseLiquid(byte[] data)
    {
        if (data == null || data.Length < HEADER_SIZE) return null;

        // Validate file magic
        if (data[0] != 'M' || data[1] != 'A' || data[2] != 'P' || data[3] != 'S')
            return null;

        uint liquidMapOffset = BitConverter.ToUInt32(data, 24);
        uint liquidMapSize = BitConverter.ToUInt32(data, 28);

        // Need at least the 16-byte MLIQ header
        if (liquidMapOffset == 0 || liquidMapSize == 0 || liquidMapOffset + 16 > data.Length)
            return null;

        int pos = (int)liquidMapOffset;

        // Validate MLIQ fourcc
        if (data[pos] != 'M' || data[pos + 1] != 'L' || data[pos + 2] != 'I' || data[pos + 3] != 'Q')
            return null;

        // Header (matches GridMapLiquidHeader byte-for-byte)
        byte headerFlags = data[pos + 4];
        byte globalLiqFlg = data[pos + 5];
        ushort liquidType = BitConverter.ToUInt16(data, pos + 6);
        byte offsetX = data[pos + 8];
        byte offsetY = data[pos + 9];
        byte width = data[pos + 10];
        byte height = data[pos + 11];
        float liquidLevel = BitConverter.ToSingle(data, pos + 12);
        pos += 16;

        if (width == 0 || height == 0) return null;

        const byte MAP_LIQUID_NO_TYPE = 0x01;
        const byte MAP_LIQUID_NO_HEIGHT = 0x02;

        // Per-chunk arrays come first (BEFORE heights), only when NO_TYPE is clear.
        // The fixed sizes are 16*16 — NOT width*height. Indexed [chunkRow*16+chunkCol].
        ushort[]? liquidEntry = null;
        byte[]? cellFlags = null;
        if ((headerFlags & MAP_LIQUID_NO_TYPE) == 0)
        {
            const int chunkGridCount = 16 * 16; // 256 chunks
            int entryBytes = chunkGridCount * 2;
            int flagBytes = chunkGridCount * 1;
            if (pos + entryBytes + flagBytes > data.Length)
                return null; // truncated file
            liquidEntry = new ushort[chunkGridCount];
            for (int i = 0; i < chunkGridCount; i++)
            {
                liquidEntry[i] = BitConverter.ToUInt16(data, pos);
                pos += 2;
            }
            cellFlags = new byte[chunkGridCount];
            Array.Copy(data, pos, cellFlags, 0, chunkGridCount);
            pos += chunkGridCount;
        }

        // Per-cell heights (one float per cell, not per vertex)
        float[]? heights = null;
        if ((headerFlags & MAP_LIQUID_NO_HEIGHT) == 0)
        {
            int cellCount = width * height;
            int needed = cellCount * 4;
            if (pos + needed > data.Length)
                return null; // truncated
            heights = new float[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                heights[i] = BitConverter.ToSingle(data, pos);
                pos += 4;
            }
        }

        return new LiquidResult
        {
            OffsetX = offsetX,
            OffsetY = offsetY,
            Width = width,
            Height = height,
            LiquidLevel = liquidLevel,
            HeaderFlags = headerFlags,
            GlobalLiquidFlags = globalLiqFlg,
            LiquidType = liquidType,
            Heights = heights,
            CellFlags = cellFlags,
            LiquidEntry = liquidEntry,
        };
    }
}