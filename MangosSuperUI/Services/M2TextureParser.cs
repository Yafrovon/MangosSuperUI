using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Parses and patches the texture table in vanilla v256 M2 files.
///
/// ═══════════════════════════════════════════════════════════════════════
/// VERIFIED Session 14 — Empirically confirmed across 4 Fireball M2 files
/// ═══════════════════════════════════════════════════════════════════════
///
/// M2 Header texture fields (v256):
///   0x05C: uint32  texture count
///   0x060: uint32  texture table base offset (actual table starts at base + 8)
///
/// Texture entry (16 bytes each):
///   +0x00: uint32  filename string length (including null terminator)
///   +0x04: uint32  filename offset (absolute file offset to null-terminated ASCII)
///   +0x08: uint32  pad (usually 0; last entry may overlap with string data)
///   +0x0C: uint32  pad (usually 0; same caveat)
///
/// Emitter→texture link:
///   Emitter struct +0x016: uint16 textureId (index into texture table)
///
/// Emitter inline properties (also verified Session 14):
///   +0x028: uint8  blendingType  (0=opaque, 1=mod, 2=decal/alpha, 4=additive)
///   +0x029: uint8  emitterType   (0=point, 1=sphere, 2=plane, 3=spline)
///   +0x02A: uint16 particleColorIndex
/// ═══════════════════════════════════════════════════════════════════════
/// </summary>
public static class M2TextureParser
{
    private const int HEADER_TEXTURE_COUNT = 0x05C;
    private const int HEADER_TEXTURE_OFFSET = 0x060;
    private const int TEXTURE_ENTRY_SIZE = 16;
    private const int TABLE_PADDING = 8; // 8 zero bytes between header pointer target and first entry

    private const int HEADER_PRIMARY_EMITTERS = 0x13C;
    private const int PRIMARY_EMITTER_SIZE = 0x1F8;
    private const int EMITTER_TEXTURE_ID = 0x016;

    private const int MIN_HEADER_SIZE = 0x148;

    // ═══════════════════════════════════════════════════════════════════════
    // TEXTURE PARSING
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse all texture entries from an M2 file.
    /// Returns texture info including filenames, byte lengths, and emitter references.
    /// </summary>
    public static List<M2TextureEntry> ParseTextures(byte[] m2Data)
    {
        var result = new List<M2TextureEntry>();
        if (m2Data == null || m2Data.Length < MIN_HEADER_SIZE) return result;
        if (Encoding.ASCII.GetString(m2Data, 0, 4) != "MD20") return result;

        // Read texture count and table offset from header
        if (HEADER_TEXTURE_COUNT + 4 > m2Data.Length) return result;
        if (HEADER_TEXTURE_OFFSET + 4 > m2Data.Length) return result;

        uint texCount = BitConverter.ToUInt32(m2Data, HEADER_TEXTURE_COUNT);
        uint tableBase = BitConverter.ToUInt32(m2Data, HEADER_TEXTURE_OFFSET);

        if (texCount == 0 || texCount > 256) return result;
        if (tableBase == 0 || tableBase >= m2Data.Length) return result;

        // Actual table starts after 8 bytes of padding
        uint tableStart = tableBase + TABLE_PADDING;

        for (int i = 0; i < texCount; i++)
        {
            uint entryOfs = (uint)(tableStart + i * TEXTURE_ENTRY_SIZE);
            if (entryOfs + 8 > m2Data.Length) break; // need at least nameLen + nameOfs

            uint nameLen = BitConverter.ToUInt32(m2Data, (int)entryOfs);
            uint nameOfs = BitConverter.ToUInt32(m2Data, (int)entryOfs + 4);

            string filename = "";
            int actualByteLen = 0;

            if (nameLen > 0 && nameOfs > 0 && nameOfs < m2Data.Length)
            {
                // Read null-terminated string
                int start = (int)nameOfs;
                int end = start;
                while (end < m2Data.Length && m2Data[end] != 0) end++;
                filename = Encoding.ASCII.GetString(m2Data, start, end - start);
                actualByteLen = end - start + 1; // including null terminator
            }

            result.Add(new M2TextureEntry
            {
                Index = i,
                DeclaredLength = (int)nameLen,
                FilenameOffset = (int)nameOfs,
                Filename = filename,
                ActualByteLength = actualByteLen,
                EntryOffset = (int)entryOfs
            });
        }

        // Map emitters → textures
        MapEmitterTextures(m2Data, result);

        return result;
    }

    /// <summary>Map primary emitter textureId fields to texture entries.</summary>
    private static void MapEmitterTextures(byte[] m2Data, List<M2TextureEntry> textures)
    {
        if (HEADER_PRIMARY_EMITTERS + 8 > m2Data.Length) return;

        uint emitCount = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS);
        uint emitOffset = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS + 4);

        if (emitCount == 0 || emitCount > 256) return;
        if (emitOffset == 0 || emitOffset >= m2Data.Length) return;

        for (uint e = 0; e < emitCount; e++)
        {
            int emitterBase = (int)(emitOffset + e * PRIMARY_EMITTER_SIZE);
            if (emitterBase + PRIMARY_EMITTER_SIZE > m2Data.Length) break;

            ushort texId = BitConverter.ToUInt16(m2Data, emitterBase + EMITTER_TEXTURE_ID);
            if (texId < textures.Count)
                textures[texId].ReferencedByEmitters.Add((int)e);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // BLEND MODE EXTRACTION (Session 18)
    // ═══════════════════════════════════════════════════════════════════════

    private const int EMITTER_BLEND_MODE = 0x028;
    private const int EMITTER_TEXTURE_ID_ALT = 0x02A; // verified via hex forensics Session 18

    /// <summary>
    /// Extract the dominant blend mode for each texture index.
    ///
    /// Multiple emitters may reference the same texture with different blend modes.
    /// We return the HIGHEST blend mode seen for each texture index, because:
    ///   - If ANY emitter uses blendMode=4 (additive), the texture needs RGB vignette
    ///   - blendMode=2 (alpha blend) needs alpha vignette
    ///   - Higher modes take precedence for vignette strategy
    ///
    /// Returns a dictionary mapping texture index → blend mode byte.
    /// Textures not referenced by any emitter default to 4 (additive, safest assumption).
    /// </summary>
    public static Dictionary<int, byte> GetTextureBlendModes(byte[] m2Data)
    {
        var result = new Dictionary<int, byte>();
        if (m2Data == null || m2Data.Length < MIN_HEADER_SIZE) return result;
        if (Encoding.ASCII.GetString(m2Data, 0, 4) != "MD20") return result;

        uint texCount = BitConverter.ToUInt32(m2Data, HEADER_TEXTURE_COUNT);
        if (texCount == 0 || texCount > 256) return result;

        // Default all textures to additive (safest — RGB vignette won't break alpha-blend,
        // but missing RGB vignette on additive WILL show squares)
        for (int i = 0; i < (int)texCount; i++)
            result[i] = 4;

        if (HEADER_PRIMARY_EMITTERS + 8 > m2Data.Length) return result;

        uint emitCount = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS);
        uint emitOffset = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS + 4);

        if (emitCount == 0 || emitCount > 256) return result;
        if (emitOffset == 0 || emitOffset >= m2Data.Length) return result;

        // Detect actual emitter stride by scanning for emitter start signatures
        // (first dword = -1 or small bone index, blendMode at +0x028 is 0-4)
        var emitterStarts = new List<int>();
        for (int offset = (int)emitOffset; offset < m2Data.Length - 0x30; offset++)
        {
            int firstDword = BitConverter.ToInt32(m2Data, offset);
            if (firstDword != -1 && (firstDword < 0 || firstDword > 100)) continue;

            byte blend = m2Data[offset + EMITTER_BLEND_MODE];
            if (blend > 4) continue;

            ushort texId = BitConverter.ToUInt16(m2Data, offset + EMITTER_TEXTURE_ID_ALT);
            if (texId >= texCount) continue;

            emitterStarts.Add(offset);
        }

        // Filter to consistent-stride entries starting from the first candidate
        if (emitterStarts.Count >= 2)
        {
            int stride = emitterStarts[1] - emitterStarts[0];
            var filtered = new List<int> { emitterStarts[0] };
            for (int i = 1; i < emitterStarts.Count; i++)
            {
                if (emitterStarts[i] - filtered.Last() == stride)
                    filtered.Add(emitterStarts[i]);
            }
            if (filtered.Count >= (int)emitCount)
                emitterStarts = filtered.Take((int)emitCount).ToList();
        }

        // Now extract blend modes per texture
        foreach (int ofs in emitterStarts)
        {
            byte blend = m2Data[ofs + EMITTER_BLEND_MODE];
            ushort texId = BitConverter.ToUInt16(m2Data, ofs + EMITTER_TEXTURE_ID_ALT);

            if (texId < texCount)
            {
                // Keep the highest blend mode for each texture
                if (!result.ContainsKey(texId) || blend > result[texId])
                    result[texId] = blend;
            }
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TEXTURE FILENAME PATCHING
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rewrite texture filenames in an M2 file using fixed-length null-padding.
    ///
    /// Each replacement path is written at the original filename's file offset,
    /// padded with null bytes to exactly match the original string's byte length.
    /// This means ZERO structural changes — no offsets shift, file size stays identical.
    ///
    /// Also updates the nameLen field in the texture entry to match the new string.
    ///
    /// If a replacement path (including null terminator) would exceed the original
    /// byte length, that replacement is SKIPPED to prevent data corruption.
    ///
    /// Returns the number of textures successfully patched.
    /// </summary>
    public static int PatchTextureFilenames(byte[] m2Data, Dictionary<int, string> replacements)
    {
        var textures = ParseTextures(m2Data);
        if (textures.Count == 0) return 0;

        int patched = 0;
        foreach (var kv in replacements)
        {
            int texIndex = kv.Key;
            string newPath = kv.Value;

            if (texIndex < 0 || texIndex >= textures.Count) continue;

            var tex = textures[texIndex];
            if (tex.FilenameOffset <= 0 || tex.ActualByteLength <= 0) continue;

            byte[] newBytes = Encoding.ASCII.GetBytes(newPath);
            int newLenWithNull = newBytes.Length + 1;

            // Safety: new path must fit within original byte length
            if (newLenWithNull > tex.ActualByteLength)
                continue;

            // Write new filename at original offset
            int start = tex.FilenameOffset;
            Array.Copy(newBytes, 0, m2Data, start, newBytes.Length);

            // Null-pad the remainder
            for (int i = newBytes.Length; i < tex.ActualByteLength; i++)
                m2Data[start + i] = 0;

            // Update the nameLen field in the texture entry
            byte[] lenBytes = BitConverter.GetBytes((uint)newLenWithNull);
            Array.Copy(lenBytes, 0, m2Data, tex.EntryOffset, 4);

            patched++;
        }

        return patched;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EMITTER INLINE PROPERTY PATCHING
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Patch blendMode (uint8 at +0x028) on all primary emitters.
    /// 0=opaque, 1=mod, 2=alpha, 4=additive (glow/bloom).
    /// Additive (4) makes particles glow — hugely impactful visual change.
    /// Returns number of emitters patched.
    /// </summary>
    public static int PatchBlendMode(byte[] m2Data, byte blendMode)
    {
        return PatchEmitterByte(m2Data, 0x028, blendMode);
    }

    /// <summary>
    /// Patch emitterType (uint8 at +0x029) on all primary emitters.
    /// 0=point, 1=sphere, 2=plane, 3=spline.
    /// Returns number of emitters patched.
    /// </summary>
    public static int PatchEmitterType(byte[] m2Data, byte emitterType)
    {
        return PatchEmitterByte(m2Data, 0x029, emitterType);
    }

    /// <summary>Patch a single uint8 field at a given offset on all primary emitters.</summary>
    private static int PatchEmitterByte(byte[] m2Data, int relativeOffset, byte value)
    {
        if (m2Data == null || m2Data.Length < MIN_HEADER_SIZE) return 0;
        if (Encoding.ASCII.GetString(m2Data, 0, 4) != "MD20") return 0;

        uint emitCount = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS);
        uint emitOffset = BitConverter.ToUInt32(m2Data, HEADER_PRIMARY_EMITTERS + 4);

        if (emitCount == 0 || emitCount > 256) return 0;
        if (emitOffset == 0 || emitOffset >= m2Data.Length) return 0;

        int patched = 0;
        for (uint e = 0; e < emitCount; e++)
        {
            int emitterBase = (int)(emitOffset + e * PRIMARY_EMITTER_SIZE);
            if (emitterBase + PRIMARY_EMITTER_SIZE > m2Data.Length) break;

            // Validate emitter (same check as M2ParticlePatcher)
            int particleId = BitConverter.ToInt32(m2Data, emitterBase);
            if (particleId != -1 && (particleId < 0 || particleId > 100)) continue;

            int fieldOfs = emitterBase + relativeOffset;
            if (fieldOfs < m2Data.Length)
            {
                m2Data[fieldOfs] = value;
                patched++;
            }
        }
        return patched;
    }
}

/// <summary>Information about one texture entry in an M2 file.</summary>
public class M2TextureEntry
{
    /// <summary>Index in the M2's texture array (0-based).</summary>
    public int Index { get; set; }

    /// <summary>Declared filename length from the texture entry (uint32 at entry+0x00).</summary>
    public int DeclaredLength { get; set; }

    /// <summary>Absolute file offset to the filename string (uint32 at entry+0x04).</summary>
    public int FilenameOffset { get; set; }

    /// <summary>The filename string (e.g. "SPELLS\\CLOUDS8X8.BLP").</summary>
    public string Filename { get; set; } = "";

    /// <summary>Actual byte length of the string including null terminator (measured from the file).</summary>
    public int ActualByteLength { get; set; }

    /// <summary>Absolute file offset of this entry in the texture table.</summary>
    public int EntryOffset { get; set; }

    /// <summary>Which primary emitter indices reference this texture (via textureId).</summary>
    public List<int> ReferencedByEmitters { get; set; } = new();
}