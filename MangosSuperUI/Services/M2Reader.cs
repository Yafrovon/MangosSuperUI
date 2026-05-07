using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Parsed M2 model data — just the geometry and texture references we need for GLB export.
/// </summary>
public class M2Model
{
    public uint Version { get; set; }
    public string Name { get; set; } = "";
    public List<M2Vertex> Vertices { get; set; } = new();
    public List<ushort> Indices { get; set; } = new();          // triangle indices (remapped to global)
    public List<M2Submesh> Submeshes { get; set; } = new();
    public List<M2Batch> Batches { get; set; } = new();
    public List<M2TextureRef> Textures { get; set; } = new();
    public List<ushort> TextureLookup { get; set; } = new();
    public bool IsValid => Vertices.Count > 0 && Indices.Count >= 3;
}

public struct M2Vertex
{
    public float PosX, PosY, PosZ;
    public float NormX, NormY, NormZ;
    public float TexU, TexV;
}

public class M2Submesh
{
    public ushort Id { get; set; }
    public ushort VertexStart { get; set; }
    public ushort VertexCount { get; set; }
    public ushort IndexStart { get; set; }
    public ushort IndexCount { get; set; }
}

/// <summary>
/// M2Batch (texture unit) maps a submesh to a texture.
/// In vanilla, each batch is 24 bytes in the inlined view.
/// </summary>
public class M2Batch
{
    public byte Flags { get; set; }
    public byte PriorityPlane { get; set; }
    public ushort ShaderId { get; set; }
    public ushort SubmeshIndex { get; set; }    // index into Submeshes
    public ushort GeosetIndex { get; set; }
    public short ColorIndex { get; set; }
    public ushort MaterialIndex { get; set; }   // index into render flags
    public ushort MaterialLayer { get; set; }
    public ushort TextureCount { get; set; }
    public ushort TextureIndex { get; set; }    // index into TextureLookup
    public ushort TextureTransformIndex { get; set; }
    public ushort TextureWeightIndex { get; set; }
}

public class M2TextureRef
{
    public uint Type { get; set; }     // 0=filename, 1=body/skin, etc.
    public uint Flags { get; set; }
    public string Filename { get; set; } = "";
}

/// <summary>
/// Reads WoW 1.12.1 (vanilla, build 5875) M2 model files.
/// 
/// The vanilla M2 header (version 256, "MD20") is laid out as a flat struct
/// of uint32 pairs (nCount, ofsOffset). The key insight for vanilla vs WotLK:
/// in vanilla, skin/view profiles are INLINED in the M2 file (not separate .skin files),
/// so there's both nViews AND ofsViews in the header.
///
/// The complete vanilla header layout (from wowdev wiki M2#Header):
/// All offsets in bytes from file start.
/// 
/// 0x000  char[4]   magic = "MD20"
/// 0x004  uint32    version (256 for vanilla 1.12.1)
/// 0x008  uint32    nName
/// 0x00C  uint32    ofsName
/// 0x010  uint32    globalFlags
/// 0x014  uint32    nGlobalLoops
/// 0x018  uint32    ofsGlobalLoops
/// 0x01C  uint32    nSequences
/// 0x020  uint32    ofsSequences
/// 0x024  uint32    nSequenceIdxHashById
/// 0x028  uint32    ofsSequenceIdxHashById
/// 0x02C  uint32    nPlayableAnimLookup   (was nSequenceLookups)
/// 0x030  uint32    ofsPlayableAnimLookup
/// 0x034  uint32    nBones
/// 0x038  uint32    ofsBones
/// 0x03C  uint32    nKeyBoneLookup
/// 0x040  uint32    ofsKeyBoneLookup
/// 0x044  uint32    nVertices
/// 0x048  uint32    ofsVertices
/// 0x04C  uint32    nViews (nSkinProfiles)
/// --- for version &lt; 264 (vanilla/TBC), ofsViews is present: ---
/// 0x050  uint32    ofsViews
/// 0x054  uint32    nColors
/// 0x058  uint32    ofsColors
/// 0x05C  uint32    nTextures
/// 0x060  uint32    ofsTextures
/// 0x064  uint32    nTransparency
/// 0x068  uint32    ofsTransparency
/// 0x06C  uint32    nTextureAnims
/// 0x070  uint32    ofsTextureAnims
/// 0x074  uint32    nTexReplace
/// 0x078  uint32    ofsTexReplace
/// 0x07C  uint32    nRenderFlags (nMaterials)
/// 0x080  uint32    ofsRenderFlags
/// 0x084  uint32    nBoneLookup
/// 0x088  uint32    ofsBoneLookup
/// 0x08C  uint32    nTextureLookup
/// 0x090  uint32    ofsTextureLookup
/// ...continues with more fields...
/// 
/// Reference: https://wowdev.wiki/M2
/// </summary>
public class M2Reader
{
    /// <summary>
    /// Parse an M2 binary from raw bytes extracted from MPQ.
    /// Returns null if the file is not a valid M2 or cannot be parsed.
    /// </summary>
    public static M2Model? Parse(byte[] data)
    {
        if (data == null || data.Length < 0x94) // need at least up to ofsTextureLookup
            return null;

        try
        {
            var model = new M2Model();

            // ── Magic check ──
            var magic = Encoding.ASCII.GetString(data, 0, 4);
            if (magic != "MD20")
                return null;

            model.Version = ReadUInt32(data, 0x04);

            // ── Name ──
            uint nName = ReadUInt32(data, 0x08);
            uint ofsName = ReadUInt32(data, 0x0C);
            if (nName > 0 && ofsName > 0 && ofsName + nName <= data.Length)
            {
                model.Name = Encoding.ASCII.GetString(data, (int)ofsName, (int)nName).TrimEnd('\0');
            }

            // ── Vertices (always at 0x044/0x048 for vanilla) ──
            uint nVertices = ReadUInt32(data, 0x044);
            uint ofsVertices = ReadUInt32(data, 0x048);

            // ── Views ──
            uint nViews = ReadUInt32(data, 0x04C);

            // For vanilla (version < 264), ofsViews is at 0x050
            bool viewsInlined = model.Version < 264;
            uint ofsViews = 0;
            if (viewsInlined)
            {
                ofsViews = ReadUInt32(data, 0x050);
            }

            // ── Textures ──
            uint nTextures, ofsTextures;
            uint nTextureLookup, ofsTextureLookup;

            if (viewsInlined)
            {
                // Vanilla layout (ofsViews present, shifts everything after by +4)
                nTextures = ReadUInt32(data, 0x05C);
                ofsTextures = ReadUInt32(data, 0x060);
                nTextureLookup = ReadUInt32(data, 0x08C);
                ofsTextureLookup = ReadUInt32(data, 0x090);
            }
            else
            {
                // WotLK+ (no ofsViews field)
                nTextures = ReadUInt32(data, 0x058);
                ofsTextures = ReadUInt32(data, 0x05C);
                nTextureLookup = ReadUInt32(data, 0x088);
                ofsTextureLookup = ReadUInt32(data, 0x08C);
            }

            // ── Validate offsets before parsing ──
            if (nVertices == 0 || ofsVertices == 0 || ofsVertices >= data.Length)
                return null;
            if (nViews == 0)
                return null;
            if (viewsInlined && (ofsViews == 0 || ofsViews >= data.Length))
                return null;

            // ── Parse vertices ──
            if (!ParseVertices(data, nVertices, ofsVertices, model))
                return null;

            // ── Parse view 0 (inlined skin profile for vanilla) ──
            if (viewsInlined)
            {
                if (!ParseInlinedView(data, ofsViews, model))
                    return null;
            }
            else
            {
                return null; // External .skin files — not supported for 1.12.1
            }

            // ── Parse textures ──
            ParseTextures(data, nTextures, ofsTextures, model);

            // ── Parse texture lookup ──
            ParseTextureLookup(data, nTextureLookup, ofsTextureLookup, model);

            return model.IsValid ? model : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ParseVertices(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return false;

        // Each vertex is 48 bytes:
        //   float[3] pos         (12 bytes)  @ +0
        //   uint8[4] boneWeights (4 bytes)   @ +12
        //   uint8[4] boneIndices (4 bytes)   @ +16
        //   float[3] normal      (12 bytes)  @ +20
        //   float[2] texCoords0  (8 bytes)   @ +32
        //   float[2] texCoords1  (8 bytes)   @ +40
        const int VERTEX_SIZE = 48;

        if (offset + count * VERTEX_SIZE > data.Length)
            return false;

        model.Vertices.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * VERTEX_SIZE);

            float px = ReadFloat(data, off + 0);
            float py = ReadFloat(data, off + 4);
            float pz = ReadFloat(data, off + 8);

            float nx = ReadFloat(data, off + 20);
            float ny = ReadFloat(data, off + 24);
            float nz = ReadFloat(data, off + 28);

            float u = ReadFloat(data, off + 32);
            float v = ReadFloat(data, off + 36);

            // WoW Z-up to glTF Y-up: (x, y, z) → (x, z, -y)
            model.Vertices.Add(new M2Vertex
            {
                PosX = px,
                PosY = pz,
                PosZ = -py,
                NormX = nx,
                NormY = nz,
                NormZ = -ny,
                TexU = u,
                TexV = v
            });
        }

        return model.Vertices.Count > 0;
    }

    /// <summary>
    /// Parse an inlined view/skin profile (pre-WotLK format, no SKIN magic).
    /// 
    /// Layout of each M2SkinProfile (inlined):
    ///   M2Array&lt;uint16&gt; vertices      — local vertex list (indices into global vertex array)
    ///   M2Array&lt;uint16&gt; indices        — triangle indices (into local vertex list)
    ///   M2Array&lt;ubyte4&gt; bones          — bone combos (skip)
    ///   M2Array&lt;M2SkinSection&gt; submeshes
    ///   M2Array&lt;M2Batch&gt; batches
    ///   uint32 boneCountMax
    ///
    /// Each M2Array = 8 bytes (uint32 count, uint32 offset).
    /// Offsets are absolute (from file start) for inlined views.
    /// Total: 5 × 8 + 4 = 44 bytes per view.
    /// </summary>
    private static bool ParseInlinedView(byte[] data, uint viewOffset, M2Model model)
    {
        if (viewOffset + 44 > data.Length) return false;

        int off = (int)viewOffset;

        uint nLocalVerts = ReadUInt32(data, off + 0);
        uint ofsLocalVerts = ReadUInt32(data, off + 4);
        uint nTriIndices = ReadUInt32(data, off + 8);
        uint ofsTriIndices = ReadUInt32(data, off + 12);
        // off+16: nBones, off+20: ofsBones (skip)
        uint nSubmeshes = ReadUInt32(data, off + 24);
        uint ofsSubmeshes = ReadUInt32(data, off + 28);

        // ── Validate ──
        if (nLocalVerts == 0 || ofsLocalVerts == 0 || ofsLocalVerts + nLocalVerts * 2 > data.Length)
            return false;
        if (nTriIndices == 0 || ofsTriIndices == 0 || ofsTriIndices + nTriIndices * 2 > data.Length)
            return false;

        // ── Read local vertex index map ──
        var localVertexMap = new ushort[nLocalVerts];
        for (uint i = 0; i < nLocalVerts; i++)
        {
            localVertexMap[i] = ReadUInt16(data, (int)(ofsLocalVerts + i * 2));
        }

        // ── Read triangle indices and remap through local vertex map ──
        model.Indices.Capacity = (int)nTriIndices;
        for (uint i = 0; i < nTriIndices; i++)
        {
            ushort localIdx = ReadUInt16(data, (int)(ofsTriIndices + i * 2));
            if (localIdx < nLocalVerts)
            {
                model.Indices.Add(localVertexMap[localIdx]);
            }
            else
            {
                model.Indices.Add(0); // fallback for invalid index
            }
        }

        // ── Read submeshes ──
        // M2SkinSection in vanilla is 32 bytes
        if (nSubmeshes > 0 && ofsSubmeshes > 0 && ofsSubmeshes + nSubmeshes * 32 <= data.Length)
        {
            for (uint i = 0; i < nSubmeshes; i++)
            {
                int sOff = (int)(ofsSubmeshes + i * 32);
                model.Submeshes.Add(new M2Submesh
                {
                    Id = ReadUInt16(data, sOff + 0),
                    VertexStart = ReadUInt16(data, sOff + 4),
                    VertexCount = ReadUInt16(data, sOff + 6),
                    IndexStart = ReadUInt16(data, sOff + 8),
                    IndexCount = ReadUInt16(data, sOff + 10),
                });
            }
        }

        // ── Read batches (texture units) ──
        // M2Batch in vanilla is 24 bytes:
        //   uint8 flags, uint8 priorityPlane, uint16 shaderId,
        //   uint16 submeshIndex, uint16 geosetIndex,
        //   int16 colorIndex, uint16 materialIndex, uint16 materialLayer,
        //   uint16 textureCount, uint16 textureIndex,
        //   uint16 textureTransformIndex, uint16 textureWeightIndex
        uint nBatches = ReadUInt32(data, off + 32);
        uint ofsBatches = ReadUInt32(data, off + 36);

        if (nBatches > 0 && ofsBatches > 0 && ofsBatches + nBatches * 24 <= data.Length)
        {
            for (uint i = 0; i < nBatches; i++)
            {
                int bOff = (int)(ofsBatches + i * 24);
                model.Batches.Add(new M2Batch
                {
                    Flags = data[bOff + 0],
                    PriorityPlane = data[bOff + 1],
                    ShaderId = ReadUInt16(data, bOff + 2),
                    SubmeshIndex = ReadUInt16(data, bOff + 4),
                    GeosetIndex = ReadUInt16(data, bOff + 6),
                    ColorIndex = (short)ReadUInt16(data, bOff + 8),
                    MaterialIndex = ReadUInt16(data, bOff + 10),
                    MaterialLayer = ReadUInt16(data, bOff + 12),
                    TextureCount = ReadUInt16(data, bOff + 14),
                    TextureIndex = ReadUInt16(data, bOff + 16),
                    TextureTransformIndex = ReadUInt16(data, bOff + 18),
                    TextureWeightIndex = ReadUInt16(data, bOff + 20),
                });
            }
        }

        return model.Indices.Count >= 3;
    }

    private static void ParseTextures(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;

        // Each M2Texture is 16 bytes: uint32 type, uint32 flags, uint32 nFilename, uint32 ofsFilename
        const int TEX_SIZE = 16;
        if (offset + count * TEX_SIZE > data.Length) return;

        for (uint i = 0; i < count; i++)
        {
            int tOff = (int)(offset + i * TEX_SIZE);
            uint type = ReadUInt32(data, tOff);
            uint flags = ReadUInt32(data, tOff + 4);
            uint nFilename = ReadUInt32(data, tOff + 8);
            uint ofsFilename = ReadUInt32(data, tOff + 12);

            string filename = "";
            if (nFilename > 1 && ofsFilename > 0 && ofsFilename + nFilename <= data.Length)
            {
                filename = Encoding.ASCII.GetString(data, (int)ofsFilename, (int)nFilename).TrimEnd('\0');
            }

            model.Textures.Add(new M2TextureRef
            {
                Type = type,
                Flags = flags,
                Filename = filename
            });
        }
    }

    private static void ParseTextureLookup(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * 2 > data.Length) return;

        for (uint i = 0; i < count; i++)
        {
            model.TextureLookup.Add(ReadUInt16(data, (int)(offset + i * 2)));
        }
    }

    // ── Binary helpers ──

    private static uint ReadUInt32(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return 0;
        return BitConverter.ToUInt32(data, offset);
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        if (offset + 2 > data.Length) return 0;
        return BitConverter.ToUInt16(data, offset);
    }

    private static float ReadFloat(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return 0f;
        return BitConverter.ToSingle(data, offset);
    }
}