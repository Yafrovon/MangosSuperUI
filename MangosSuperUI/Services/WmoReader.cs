using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Reads WoW 1.12.1 (vanilla) WMO (World Map Object) files.
///
/// WMO structure:
///   Root file (e.g. "building.wmo") — textures, materials, group count
///   Group files (e.g. "building_000.wmo", "building_001.wmo") — actual geometry
///
/// Root chunks we parse:
///   MOHD — header (64 bytes): nTextures, nGroups, etc.
///   MOTX — texture filename blob (BLP paths)
///   MOMT — materials (64 bytes each): texture offsets into MOTX, flags
///   MODS — doodad sets (32 bytes each): set name + first instance + count
///   MODN — null-separated M2 filenames for embedded doodads
///   MODD — doodad placements (40 bytes each): name offset, flags, pos,
///          quaternion (XYZW), scale, BGRA tint
///
/// Group file chunks we parse:
///   MOGP — group header (68 bytes) then subchunks
///   MOPY — per-triangle material info (2 bytes: flags + materialID)
///   MOVI — triangle indices (uint16)
///   MOVT — vertices (float x3, in X,Z,-Y order → we convert to Y-up)
///   MONR — normals (float x3, same coord transform)
///   MOTV — UV coords (float x2)
///   MOBA — render batches (24 bytes each)
///
/// FourCC note: WoW stores chunk IDs reversed on disk (little-endian).
///   "MOHD" is stored as bytes D,H,O,M. We use the same ChunkId() reverse
///   helper as AdtTerrainReader to handle this correctly.
///
/// Reference: https://wowdev.wiki/WMO
/// </summary>
public class WmoReader
{
    // ── FourCC helpers — WoW stores chunk IDs reversed on disk (little-endian) ──
    // Same approach as AdtTerrainReader.ChunkId()
    private static uint ChunkId(string fourcc)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(fourcc);
        Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    // Root file chunks
    private static readonly uint MAGIC_MVER = ChunkId("MVER");
    private static readonly uint MAGIC_MOHD = ChunkId("MOHD");
    private static readonly uint MAGIC_MOTX = ChunkId("MOTX");
    private static readonly uint MAGIC_MOMT = ChunkId("MOMT");

    // Root file chunks — doodads (M2 props embedded inside the WMO)
    // MODS = doodad sets (named groups, e.g. "Set_$DefaultGlobal" + variants)
    // MODN = null-separated M2 filename blob
    // MODD = doodad instances (40 bytes each: name offset + flags, pos, quaternion, scale, color)
    // Note: MODI (file data ID table) is a Legion+ chunk; vanilla 1.12.1 uses MODN byte offsets.
    private static readonly uint MAGIC_MODS = ChunkId("MODS");
    private static readonly uint MAGIC_MODN = ChunkId("MODN");
    private static readonly uint MAGIC_MODD = ChunkId("MODD");

    // Group file chunks
    private static readonly uint MAGIC_MOGP = ChunkId("MOGP");
    private static readonly uint MAGIC_MOPY = ChunkId("MOPY");
    private static readonly uint MAGIC_MOVI = ChunkId("MOVI");
    private static readonly uint MAGIC_MOVT = ChunkId("MOVT");
    private static readonly uint MAGIC_MONR = ChunkId("MONR");
    private static readonly uint MAGIC_MOTV = ChunkId("MOTV");
    private static readonly uint MAGIC_MOBA = ChunkId("MOBA");
    // MLIQ = liquid inside the WMO group (Stormwind canals, Undercity slime, etc.).
    // Present only when MOGP.GroupFlags bit 0x1000 (SMOGroup::LIQUIDSURFACE) is set.
    private static readonly uint MAGIC_MLIQ = ChunkId("MLIQ");

    // MLIQ sub-structure sizes per wowdev WMO#MLIQ_chunk + Noggit wmo_liquid.cpp:
    //   WMOLiquidHeader = 30 bytes (0x1E):
    //     C2iVector liquidVerts  (uint32 x, y)      — xverts, yverts
    //     C2iVector liquidTiles  (uint32 x, y)      — xtiles, ytiles (=verts - 1)
    //     C3Vector  liquidCorner (float x, y, z)    — base coord in WMO local space
    //     uint16    liquidMtlId                     — material id (MOMT index)
    //   SMOLVert  = 8 bytes (union 4-byte meta + float height)   × xverts*yverts
    //   SMOLTile  = 1 byte  (bits: 0-5 legacy/type, bit 0x08 dont_render, etc.) × xtiles*ytiles
    private const int WMO_LIQUID_HEADER_SIZE = 30;
    private const int WMO_LIQUID_VERT_SIZE = 8;
    private const int WMO_LIQUID_TILE_SIZE = 1;
    // Liquid tile size in WMO local units. Per wowdev: same as ADT cell (1/8 of an MCNK chunk).
    // ADT CELL_SIZE = 33.3333 / 8 = 4.16667. WMO uses identical tile size for MLIQ.
    private const float WMO_LIQUID_UNIT = 33.3333f / 8.0f;

    /// <summary>
    /// Parse a WMO root file from raw bytes.
    /// Returns header info + material/texture data needed to resolve group textures.
    /// </summary>
    public static WmoRootData? ParseRoot(byte[] data)
    {
        if (data == null || data.Length < 20) return null;

        var root = new WmoRootData();
        int pos = 0;

        while (pos + 8 <= data.Length)
        {
            uint chunkId = BitConverter.ToUInt32(data, pos);
            uint chunkSize = BitConverter.ToUInt32(data, pos + 4);
            int chunkData = pos + 8;
            int chunkEnd = (int)(chunkData + chunkSize);

            if (chunkEnd > data.Length) break;

            if (chunkId == MAGIC_MOHD)
            {
                if (chunkSize >= 64)
                {
                    root.NTextures = BitConverter.ToUInt32(data, chunkData + 0);
                    root.NGroups = BitConverter.ToUInt32(data, chunkData + 4);
                    root.NPortals = BitConverter.ToUInt32(data, chunkData + 8);
                    root.NLights = BitConverter.ToUInt32(data, chunkData + 12);
                    // Bounding box at +0x24 (6 floats)
                    if (chunkSize >= 0x3C)
                    {
                        root.BbMinX = BitConverter.ToSingle(data, chunkData + 0x24);
                        root.BbMinY = BitConverter.ToSingle(data, chunkData + 0x28);
                        root.BbMinZ = BitConverter.ToSingle(data, chunkData + 0x2C);
                        root.BbMaxX = BitConverter.ToSingle(data, chunkData + 0x30);
                        root.BbMaxY = BitConverter.ToSingle(data, chunkData + 0x34);
                        root.BbMaxZ = BitConverter.ToSingle(data, chunkData + 0x38);
                    }
                }
            }
            else if (chunkId == MAGIC_MOTX)
            {
                // Blob of zero-terminated texture filenames
                root.TextureBlob = new byte[chunkSize];
                Array.Copy(data, chunkData, root.TextureBlob, 0, (int)chunkSize);
            }
            else if (chunkId == MAGIC_MOMT)
            {
                // Materials, 64 bytes each
                int matCount = (int)(chunkSize / 64);
                for (int i = 0; i < matCount; i++)
                {
                    int mOfs = chunkData + i * 64;
                    root.Materials.Add(new WmoMaterial
                    {
                        Flags = BitConverter.ToUInt32(data, mOfs + 0),
                        Shader = BitConverter.ToUInt32(data, mOfs + 4),
                        BlendMode = BitConverter.ToUInt32(data, mOfs + 8),
                        Texture0Offset = BitConverter.ToUInt32(data, mOfs + 0x0C),
                        Texture1Offset = BitConverter.ToUInt32(data, mOfs + 0x18),
                        Texture2Offset = BitConverter.ToUInt32(data, mOfs + 0x24),
                    });
                }
            }
            else if (chunkId == MAGIC_MODS)
            {
                // Doodad sets, 32 bytes each:
                //   char[20] name (null-padded; e.g. "Set_$DefaultGlobal", "Set_Stormwind_City")
                //   uint32   firstInstanceIndex (index into MODD array)
                //   uint32   doodadCount
                //   uint32   padding (unused)
                int setCount = (int)(chunkSize / 32);
                for (int i = 0; i < setCount; i++)
                {
                    int sOfs = chunkData + i * 32;
                    // Read name as null-terminated within the 20-byte slot
                    int nameLen = 0;
                    while (nameLen < 20 && data[sOfs + nameLen] != 0) nameLen++;
                    string setName = Encoding.ASCII.GetString(data, sOfs, nameLen);
                    root.DoodadSets.Add(new WmoDoodadSet
                    {
                        Name = setName,
                        FirstInstanceIndex = BitConverter.ToUInt32(data, sOfs + 20),
                        DoodadCount = BitConverter.ToUInt32(data, sOfs + 24)
                    });
                }
            }
            else if (chunkId == MAGIC_MODN)
            {
                // Blob of zero-terminated M2 filenames. MODD nameOffset values
                // are byte offsets into this blob.
                root.DoodadNameBlob = new byte[chunkSize];
                Array.Copy(data, chunkData, root.DoodadNameBlob, 0, (int)chunkSize);
            }
            else if (chunkId == MAGIC_MODD)
            {
                // Doodad instances, 40 bytes each. WoW vanilla layout:
                //   bytes 0..3   : packed = (nameOffset:24) | (flags:8)
                //   bytes 4..15  : position    (3 × float32, Z-up)
                //   bytes 16..31 : quaternion  (4 × float32, X,Y,Z,W in Z-up space)
                //   bytes 32..35 : scale       (float32)
                //   bytes 36..39 : color       (BGRA, 4 × uint8)
                //
                // We store raw values here. Z-up→Y-up conversion happens at
                // composition time (server-side), same as for ADT MDDF.
                int doodadCount = (int)(chunkSize / 40);
                for (int i = 0; i < doodadCount; i++)
                {
                    int dOfs = chunkData + i * 40;
                    uint packed = BitConverter.ToUInt32(data, dOfs + 0);
                    uint nameOffset = packed & 0x00FFFFFFu;          // low 24 bits
                    byte flags = (byte)((packed >> 24) & 0xFFu);     // high 8 bits

                    root.Doodads.Add(new WmoDoodadDef
                    {
                        NameOffset = nameOffset,
                        Flags = flags,
                        PosX = BitConverter.ToSingle(data, dOfs + 4),
                        PosY = BitConverter.ToSingle(data, dOfs + 8),
                        PosZ = BitConverter.ToSingle(data, dOfs + 12),
                        QuatX = BitConverter.ToSingle(data, dOfs + 16),
                        QuatY = BitConverter.ToSingle(data, dOfs + 20),
                        QuatZ = BitConverter.ToSingle(data, dOfs + 24),
                        QuatW = BitConverter.ToSingle(data, dOfs + 28),
                        Scale = BitConverter.ToSingle(data, dOfs + 32),
                        ColorB = data[dOfs + 36],
                        ColorG = data[dOfs + 37],
                        ColorR = data[dOfs + 38],
                        ColorA = data[dOfs + 39],
                    });
                }
            }

            pos = chunkEnd;
        }

        // Resolve texture filenames from blob
        if (root.TextureBlob != null)
        {
            foreach (var mat in root.Materials)
            {
                mat.Texture0Name = ReadStringFromBlob(root.TextureBlob, (int)mat.Texture0Offset);
                mat.Texture1Name = ReadStringFromBlob(root.TextureBlob, (int)mat.Texture1Offset);
                mat.Texture2Name = ReadStringFromBlob(root.TextureBlob, (int)mat.Texture2Offset);
            }
        }

        // Resolve doodad M2 filenames from MODN blob
        if (root.DoodadNameBlob != null)
        {
            foreach (var d in root.Doodads)
            {
                d.ModelPath = ReadStringFromBlob(root.DoodadNameBlob, (int)d.NameOffset);
            }
        }

        return root.NGroups > 0 ? root : null;
    }

    /// <summary>
    /// Parse a WMO group file from raw bytes.
    /// Returns geometry: vertices, indices, normals, UVs, per-triangle material IDs, batches.
    /// </summary>
    public static WmoGroupData? ParseGroup(byte[] data)
    {
        if (data == null || data.Length < 20) return null;

        var group = new WmoGroupData();
        int pos = 0;

        // Group file structure:
        //   MVER chunk (version)
        //   MOGP chunk (contains everything else as subchunks)
        // The MOGP chunk size covers the whole rest of the file.
        // MOGP has a 68-byte header, then subchunks start at offset 0x44 from MOGP data start.

        // Find MOGP
        while (pos + 8 <= data.Length)
        {
            uint magic = BitConverter.ToUInt32(data, pos);
            uint chunkSize = BitConverter.ToUInt32(data, pos + 4);
            int chunkData = pos + 8;

            if (magic == MAGIC_MOGP)
            {
                // MOGP header is 68 bytes
                if (chunkData + 68 > data.Length) return null;

                group.GroupFlags = BitConverter.ToUInt32(data, chunkData + 0);
                // BB at +4 and +16 (two C3Vectors = 24 bytes)
                group.BbMinX = BitConverter.ToSingle(data, chunkData + 4);
                group.BbMinY = BitConverter.ToSingle(data, chunkData + 8);
                group.BbMinZ = BitConverter.ToSingle(data, chunkData + 12);
                group.BbMaxX = BitConverter.ToSingle(data, chunkData + 16);
                group.BbMaxY = BitConverter.ToSingle(data, chunkData + 20);
                group.BbMaxZ = BitConverter.ToSingle(data, chunkData + 24);
                // MOGP +0x34 = groupLiquid (uint32 — LiquidType DBC id, used by MLIQ).
                // Some vanilla WMOs leave this 0 even when MLIQ is present; the actual
                // per-tile liquid_type bits in SMOLTile take priority for rendering.
                group.GroupLiquid = BitConverter.ToUInt32(data, chunkData + 0x34);

                // Subchunks start at MOGP data + 68 (0x44)
                int subPos = chunkData + 68;
                int mogpEnd = (int)(chunkData + chunkSize);
                if (mogpEnd > data.Length) mogpEnd = data.Length;

                while (subPos + 8 <= mogpEnd)
                {
                    uint subMagic = BitConverter.ToUInt32(data, subPos);
                    uint subSize = BitConverter.ToUInt32(data, subPos + 4);
                    int subData = subPos + 8;
                    int subEnd = (int)(subData + subSize);
                    if (subEnd > mogpEnd) break;

                    if (subMagic == MAGIC_MOPY)
                    {
                        // Per-triangle material info, 2 bytes each (flags + materialID)
                        int triCount = (int)(subSize / 2);
                        for (int i = 0; i < triCount; i++)
                        {
                            byte flags = data[subData + i * 2];
                            byte matId = data[subData + i * 2 + 1];
                            group.TriMaterials.Add((flags, matId));
                        }
                    }
                    else if (subMagic == MAGIC_MOVI)
                    {
                        // Triangle indices, uint16
                        int idxCount = (int)(subSize / 2);
                        for (int i = 0; i < idxCount; i++)
                        {
                            group.Indices.Add(BitConverter.ToUInt16(data, subData + i * 2));
                        }
                    }
                    else if (subMagic == MAGIC_MOVT)
                    {
                        // Vertices, 3 floats each
                        // WMO coords stored as-is; Z-up → Y-up transform done in controller
                        int vertCount = (int)(subSize / 12);
                        for (int i = 0; i < vertCount; i++)
                        {
                            int vOfs = subData + i * 12;
                            float x = BitConverter.ToSingle(data, vOfs + 0);
                            float y = BitConverter.ToSingle(data, vOfs + 4);
                            float z = BitConverter.ToSingle(data, vOfs + 8);
                            group.Vertices.Add((x, y, z));
                        }
                    }
                    else if (subMagic == MAGIC_MONR)
                    {
                        // Normals, 3 floats each
                        int normCount = (int)(subSize / 12);
                        for (int i = 0; i < normCount; i++)
                        {
                            int nOfs = subData + i * 12;
                            float nx = BitConverter.ToSingle(data, nOfs + 0);
                            float ny = BitConverter.ToSingle(data, nOfs + 4);
                            float nz = BitConverter.ToSingle(data, nOfs + 8);
                            group.Normals.Add((nx, ny, nz));
                        }
                    }
                    else if (subMagic == MAGIC_MOTV)
                    {
                        // UV coords, 2 floats each (only first set)
                        if (group.UVs.Count == 0) // only take first MOTV
                        {
                            int uvCount = (int)(subSize / 8);
                            for (int i = 0; i < uvCount; i++)
                            {
                                int uOfs = subData + i * 8;
                                float u = BitConverter.ToSingle(data, uOfs + 0);
                                float v = BitConverter.ToSingle(data, uOfs + 4);
                                group.UVs.Add((u, v));
                            }
                        }
                    }
                    else if (subMagic == MAGIC_MOBA)
                    {
                        // Render batches, 24 bytes each
                        // Layout: 6×uint16 bounding box (12 bytes) + uint32 startIndex + uint16 nIndices
                        //         + uint16 startVertex + uint16 endVertex + byte flags + byte materialId
                        int batchCount = (int)(subSize / 24);
                        for (int i = 0; i < batchCount; i++)
                        {
                            int bOfs = subData + i * 24;
                            group.Batches.Add(new WmoBatch
                            {
                                // Bytes 0-11: bounding box (6 × uint16 = 12 bytes)
                                IndexStart = BitConverter.ToUInt32(data, bOfs + 12),   // 0x0C
                                IndexCount = BitConverter.ToUInt16(data, bOfs + 16),   // 0x10
                                VertexStart = BitConverter.ToUInt16(data, bOfs + 18),  // 0x12
                                VertexEnd = BitConverter.ToUInt16(data, bOfs + 20),    // 0x14
                                // Byte 22 (0x16): flags
                                MaterialId = data[bOfs + 23],                          // 0x17
                            });
                        }
                    }
                    else if (subMagic == MAGIC_MLIQ)
                    {
                        // MLIQ — water/lava surface inside the WMO group.
                        // Reference: wowdev WMO#MLIQ_chunk + Noggit wmo_liquid.cpp::initGeometry.
                        //
                        // Layout:
                        //   +0x00  WMOLiquidHeader  (30 bytes)
                        //   +0x1E  SMOLVert[xverts * yverts]  (8 bytes each)
                        //   +...   SMOLTile[xtiles * ytiles]  (1 byte each)
                        //
                        // Vertex grid is (xtiles+1) × (ytiles+1). Tile (i, j) uses vertices
                        // (i, j), (i+1, j), (i+1, j+1), (i, j+1).
                        //
                        // Tile byte: bit 0x08 = dont_render (skip this tile).
                        //            bits 0..5 are the liquid type / material code.
                        if (subSize >= WMO_LIQUID_HEADER_SIZE)
                        {
                            int xverts = (int)BitConverter.ToUInt32(data, subData + 0x00);
                            int yverts = (int)BitConverter.ToUInt32(data, subData + 0x04);
                            int xtiles = (int)BitConverter.ToUInt32(data, subData + 0x08);
                            int ytiles = (int)BitConverter.ToUInt32(data, subData + 0x0C);
                            float cornerX = BitConverter.ToSingle(data, subData + 0x10);
                            float cornerY = BitConverter.ToSingle(data, subData + 0x14);
                            float cornerZ = BitConverter.ToSingle(data, subData + 0x18);
                            ushort mtlId = BitConverter.ToUInt16(data, subData + 0x1C);

                            int vertCount = xverts * yverts;
                            int tileCount = xtiles * ytiles;
                            int needed = WMO_LIQUID_HEADER_SIZE
                                       + vertCount * WMO_LIQUID_VERT_SIZE
                                       + tileCount * WMO_LIQUID_TILE_SIZE;

                            if (xverts > 0 && yverts > 0 && xtiles > 0 && ytiles > 0
                                && (uint)needed <= subSize
                                && subData + needed <= mogpEnd)
                            {
                                var liq = new WmoLiquid
                                {
                                    XVerts = xverts,
                                    YVerts = yverts,
                                    XTiles = xtiles,
                                    YTiles = ytiles,
                                    CornerX = cornerX,
                                    CornerY = cornerY,
                                    CornerZ = cornerZ,
                                    MaterialId = mtlId,
                                    VertexHeights = new float[vertCount],
                                    TileFlags = new byte[tileCount],
                                };

                                // Vertices: 4 bytes union (water-vert flow / magma s,t) + float height.
                                int vBase = subData + WMO_LIQUID_HEADER_SIZE;
                                for (int i = 0; i < vertCount; i++)
                                {
                                    int vp = vBase + i * WMO_LIQUID_VERT_SIZE;
                                    // bytes [vp..vp+3] = water/magma metadata — skip.
                                    liq.VertexHeights[i] = BitConverter.ToSingle(data, vp + 4);
                                }

                                // Tiles: 1 byte each. Caller checks (b & 0x08) for dont_render.
                                int tBase = vBase + vertCount * WMO_LIQUID_VERT_SIZE;
                                Array.Copy(data, tBase, liq.TileFlags, 0, tileCount);

                                group.Liquid = liq;
                            }
                        }
                    }

                    subPos = subEnd;
                }

                break; // Only one MOGP per file
            }

            pos = (int)(chunkData + chunkSize);
        }

        return (group.Vertices.Count > 0 && group.Indices.Count >= 3) || group.Liquid != null
            ? group
            : null;
    }

    private static string ReadStringFromBlob(byte[] blob, int offset)
    {
        if (offset < 0 || offset >= blob.Length) return "";
        int end = offset;
        while (end < blob.Length && blob[end] != 0) end++;
        if (end == offset) return "";
        return Encoding.ASCII.GetString(blob, offset, end - offset);
    }
}

// ── DTOs ──

public class WmoRootData
{
    public uint NTextures { get; set; }
    public uint NGroups { get; set; }
    public uint NPortals { get; set; }
    public uint NLights { get; set; }
    public float BbMinX { get; set; }
    public float BbMinY { get; set; }
    public float BbMinZ { get; set; }
    public float BbMaxX { get; set; }
    public float BbMaxY { get; set; }
    public float BbMaxZ { get; set; }
    public byte[]? TextureBlob { get; set; }
    public List<WmoMaterial> Materials { get; set; } = new();

    // Embedded doodads (MODS + MODN + MODD)
    public List<WmoDoodadSet> DoodadSets { get; set; } = new();
    public byte[]? DoodadNameBlob { get; set; }
    public List<WmoDoodadDef> Doodads { get; set; } = new();
}

/// <summary>
/// Named doodad set from MODS (32 bytes). A WMO can ship multiple sets
/// (e.g. "Set_$DefaultGlobal" + "Set_Stormwind_Day" + destroyed variants).
/// Each MODF placement chooses one set via its doodadSet field.
/// FirstInstanceIndex/DoodadCount slice the MODD array for this set.
/// </summary>
public class WmoDoodadSet
{
    public string Name { get; set; } = "";
    public uint FirstInstanceIndex { get; set; }
    public uint DoodadCount { get; set; }
}

/// <summary>
/// Single doodad instance from MODD (40 bytes), in the WMO's local
/// coordinate system (Z-up). Position, rotation (quaternion XYZW), and
/// scale must be composed with the parent WMO's world transform at
/// placement time. Color is a BGRA tint (255,255,255,255 = no tint).
/// </summary>
public class WmoDoodadDef
{
    public uint NameOffset { get; set; }   // byte offset into MODN
    public byte Flags { get; set; }        // high byte of the packed name+flags field
    public string ModelPath { get; set; } = ""; // resolved from MODN blob

    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }

    public float QuatX { get; set; }
    public float QuatY { get; set; }
    public float QuatZ { get; set; }
    public float QuatW { get; set; }

    public float Scale { get; set; } = 1.0f;

    public byte ColorB { get; set; } = 255;
    public byte ColorG { get; set; } = 255;
    public byte ColorR { get; set; } = 255;
    public byte ColorA { get; set; } = 255;
}

public class WmoMaterial
{
    public uint Flags { get; set; }
    public uint Shader { get; set; }
    public uint BlendMode { get; set; }
    public uint Texture0Offset { get; set; }
    public uint Texture1Offset { get; set; }
    public uint Texture2Offset { get; set; }
    public string Texture0Name { get; set; } = "";
    public string Texture1Name { get; set; } = "";
    public string Texture2Name { get; set; } = "";
    public bool IsNoCull => (Flags & 0x04) != 0; // two-sided
    public bool IsTransparent => BlendMode != 0;
}

public class WmoGroupData
{
    public uint GroupFlags { get; set; }
    public float BbMinX { get; set; }
    public float BbMinY { get; set; }
    public float BbMinZ { get; set; }
    public float BbMaxX { get; set; }
    public float BbMaxY { get; set; }
    public float BbMaxZ { get; set; }
    /// <summary>
    /// MOGP +0x34 groupLiquid — LiquidType DBC id. May be 0 even when MLIQ is present
    /// (vanilla WMOs frequently leave this unset and rely on per-tile bits in SMOLTile).
    /// </summary>
    public uint GroupLiquid { get; set; }
    public List<(byte flags, byte materialId)> TriMaterials { get; set; } = new();
    public List<ushort> Indices { get; set; } = new();
    public List<(float x, float y, float z)> Vertices { get; set; } = new();
    public List<(float x, float y, float z)> Normals { get; set; } = new();
    public List<(float u, float v)> UVs { get; set; } = new();
    public List<WmoBatch> Batches { get; set; } = new();
    /// <summary>
    /// Water/lava surface inside this group (null = no MLIQ). Set when GroupFlags &amp; 0x1000
    /// (SMOGroup::LIQUIDSURFACE). Coordinates are in WMO local space — caller must
    /// transform by the WMO instance's MODF position+rotation.
    /// </summary>
    public WmoLiquid? Liquid { get; set; }
    public bool IsExterior => (GroupFlags & 0x08) != 0;
    public bool IsInterior => (GroupFlags & 0x2000) != 0;
    /// <summary>True when MOGP.GroupFlags has SMOGroup::LIQUIDSURFACE (0x1000) set.</summary>
    public bool HasLiquid => (GroupFlags & 0x1000) != 0;
}

/// <summary>
/// One MLIQ liquid surface inside a WMO group. Layout matches wowdev WMO#MLIQ_chunk:
/// a (XVerts × YVerts) vertex grid of heights plus a (XTiles × YTiles) tile mask
/// (XTiles = XVerts - 1, etc.). Tile flag bit 0x08 means dont_render.
///
/// Local space convention (Noggit wmo_liquid.cpp): tile (i, j) covers
///   (CornerX + i*UNIT, height, CornerY - j*UNIT) — note Z grows NEGATIVE in j.
/// The caller composes this with the WMO instance's MODF position+rotation to get world coords.
/// </summary>
public class WmoLiquid
{
    public int XVerts { get; set; }
    public int YVerts { get; set; }
    public int XTiles { get; set; }
    public int YTiles { get; set; }
    public float CornerX { get; set; }
    public float CornerY { get; set; }
    public float CornerZ { get; set; }
    public ushort MaterialId { get; set; }
    /// <summary>Vertex heights, row-major over (yverts × xverts): index = j*xverts + i.</summary>
    public float[] VertexHeights { get; set; } = Array.Empty<float>();
    /// <summary>Tile flag bytes, row-major over (ytiles × xtiles): index = j*xtiles + i.
    /// Bit 0x08 = dont_render. Bits 0..2 carry legacy liquid type.</summary>
    public byte[] TileFlags { get; set; } = Array.Empty<byte>();
}

public class WmoBatch
{
    public uint IndexStart { get; set; }
    public ushort IndexCount { get; set; }
    public ushort VertexStart { get; set; }
    public ushort VertexEnd { get; set; }
    public byte MaterialId { get; set; }
}