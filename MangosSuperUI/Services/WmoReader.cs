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

    // Group file chunks
    private static readonly uint MAGIC_MOGP = ChunkId("MOGP");
    private static readonly uint MAGIC_MOPY = ChunkId("MOPY");
    private static readonly uint MAGIC_MOVI = ChunkId("MOVI");
    private static readonly uint MAGIC_MOVT = ChunkId("MOVT");
    private static readonly uint MAGIC_MONR = ChunkId("MONR");
    private static readonly uint MAGIC_MOTV = ChunkId("MOTV");
    private static readonly uint MAGIC_MOBA = ChunkId("MOBA");

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

                    subPos = subEnd;
                }

                break; // Only one MOGP per file
            }

            pos = (int)(chunkData + chunkSize);
        }

        return group.Vertices.Count > 0 && group.Indices.Count >= 3 ? group : null;
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
    public List<(byte flags, byte materialId)> TriMaterials { get; set; } = new();
    public List<ushort> Indices { get; set; } = new();
    public List<(float x, float y, float z)> Vertices { get; set; } = new();
    public List<(float x, float y, float z)> Normals { get; set; } = new();
    public List<(float u, float v)> UVs { get; set; } = new();
    public List<WmoBatch> Batches { get; set; } = new();
    public bool IsExterior => (GroupFlags & 0x08) != 0;
    public bool IsInterior => (GroupFlags & 0x2000) != 0;
}

public class WmoBatch
{
    public uint IndexStart { get; set; }
    public ushort IndexCount { get; set; }
    public ushort VertexStart { get; set; }
    public ushort VertexEnd { get; set; }
    public byte MaterialId { get; set; }
}