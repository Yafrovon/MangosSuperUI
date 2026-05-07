using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Patches vanilla WoW 1.12.1 ADT files to inject new WMO placements.
///
/// The vanilla ADT (v18) chunked format:
///   MVER (version=18)
///   MHDR (offsets to all top-level chunks, relative to MHDR data start)
///   MCIN (256 entries: absolute offset + size for each MCNK)
///   MTEX (texture filenames)
///   MMDX (doodad model paths)
///   MMID (offsets into MMDX)
///   MWMO (WMO model paths, null-terminated, concatenated)
///   MWID (uint32 offsets into MWMO for each WMO)
///   MDDF (doodad placement records, 36 bytes each)
///   MODF (WMO placement records, 64 bytes each)
///   [optional: MFBO, MH2O, MTFX]
///   256 × MCNK (each containing MCVT, MCNR, MCLY, MCRF, MCAL, MCSH, MCLQ, MCSE, etc.)
///
/// To add WMO(s):
///   1. Append path(s) to MWMO, add offset(s) to MWID
///   2. Append 64-byte MODF entry per placement
///   3. Update MCRF in overlapping MCNK chunks (so the client actually draws them)
///   4. Rebuild MHDR offsets and MCIN absolute offsets (since chunk sizes shifted)
///
/// The patched ADT goes into patch-Z.MPQ at the correct path so the client loads it
/// instead of the original.
///
/// CRITICAL: The client uses MCRF to decide what to render.
/// "If a doodad entry from MDDF or MODF gets never referenced in a chunk's MCRF,
///  it won't be drawn at all." — wowdev.wiki
///
/// MCRF PATCHING STRATEGY (Session 49 rewrite):
///   - Only patch MCNKs whose world-space AABB overlaps the WMO's MODF bounding box
///   - Scan for the actual MCRF sub-chunk magic within the MCNK bytes (don't trust ofsMCRF alone)
///   - Use the MCRF IFF size field to determine data extent (not header counts)
///   - Insert the new MODF index at the END of MCRF data (after all existing refs)
///   - Adjust all MCNK sub-chunk offsets that point past the insertion
/// </summary>
public class AdtPatcherService
{
    private const float TILE_SIZE = 533.33333f;     // one ADT tile in world units
    private const float CHUNK_SIZE = TILE_SIZE / 16; // one MCNK chunk = 33.3333 yards

    /// <summary>
    /// Represents a parsed ADT file with all chunks located by offset.
    /// </summary>
    public class AdtFile
    {
        public byte[] Raw;

        public int MhdrDataOffset;

        public int McinOffset, McinSize;
        public int MtexOffset, MtexSize;
        public int MmdxOffset, MmdxSize;
        public int MmidOffset, MmidSize;
        public int MwmoOffset, MwmoSize;
        public int MwidOffset, MwidSize;
        public int MddfOffset, MddfSize;
        public int ModfOffset, ModfSize;

        public McnkInfo[] Mcnks = new McnkInfo[256];
        public int FirstMcnkOffset;
        public int HeaderRegionEnd;
    }

    public class McnkInfo
    {
        public int AbsoluteOffset;  // where the MCNK chunk header starts in file
        public int TotalSize;       // full size including 8-byte IFF header
        public int IndexX, IndexY;  // chunk grid position (0-15 each)

        public int McnkHeaderSize;     // 128 bytes typically
        public int McrfSubOffset;      // ofsMCRF from MCNK header (relative to MCNK chunk start including IFF header)
        public int McrfDoodadCount;    // nDoodadRefs from MCNK header
        public int McrfMapObjCount;    // nMapObjRefs from MCNK header
    }

    /// <summary>
    /// WMO placement parameters in MODF coordinate space (ADT world coords).
    /// </summary>
    public class WmoPlacement
    {
        public string WmoPath;         // e.g. "World\\wmo\\Azeroth\\Buildings\\human_farm\\farm.wmo"
        public float PosX, PosY, PosZ; // MODF position (ADT coordinate space)
        public float RotX, RotY, RotZ; // degrees
        public float BbMinX, BbMinY, BbMinZ;
        public float BbMaxX, BbMaxY, BbMaxZ;
        public ushort Flags = 0;
        public ushort DoodadSet = 0;
        public ushort NameSet = 0;
    }

    // ════════════════════════════════════════════════════════════════
    // PARSE
    // ════════════════════════════════════════════════════════════════

    public static AdtFile Parse(byte[] data)
    {
        var adt = new AdtFile { Raw = data };

        if (data.Length < 20)
            throw new InvalidDataException("ADT too small");

        ValidateChunkMagic(data, 0, "MVER");
        int mverDataSize = ReadInt32(data, 4);

        int mhdrOffset = 8 + mverDataSize;
        ValidateChunkMagic(data, mhdrOffset, "MHDR");
        adt.MhdrDataOffset = mhdrOffset + 8;

        int mhdrBase = adt.MhdrDataOffset;

        int offsMCIN = ReadInt32(data, mhdrBase + 4);
        int offsMTEX = ReadInt32(data, mhdrBase + 8);
        int offsMMDX = ReadInt32(data, mhdrBase + 12);
        int offsMMID = ReadInt32(data, mhdrBase + 16);
        int offsMWMO = ReadInt32(data, mhdrBase + 20);
        int offsMWID = ReadInt32(data, mhdrBase + 24);
        int offsMDDF = ReadInt32(data, mhdrBase + 28);
        int offsMODF = ReadInt32(data, mhdrBase + 32);

        void LocateChunk(int relOffset, out int absOffset, out int dataSize)
        {
            absOffset = mhdrBase + relOffset;
            dataSize = ReadInt32(data, absOffset + 4);
        }

        LocateChunk(offsMCIN, out adt.McinOffset, out adt.McinSize);
        LocateChunk(offsMTEX, out adt.MtexOffset, out adt.MtexSize);
        LocateChunk(offsMMDX, out adt.MmdxOffset, out adt.MmdxSize);
        LocateChunk(offsMMID, out adt.MmidOffset, out adt.MmidSize);
        LocateChunk(offsMWMO, out adt.MwmoOffset, out adt.MwmoSize);
        LocateChunk(offsMWID, out adt.MwidOffset, out adt.MwidSize);
        LocateChunk(offsMDDF, out adt.MddfOffset, out adt.MddfSize);
        LocateChunk(offsMODF, out adt.ModfOffset, out adt.ModfSize);

        // Parse MCIN to find all 256 MCNKs
        int mcinData = adt.McinOffset + 8;
        int firstMcnk = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            int entryBase = mcinData + i * 16;
            int mcnkAbsOff = ReadInt32(data, entryBase);

            // Read IFF size from MCNK chunk header
            int iffDataSize = ReadInt32(data, mcnkAbsOff + 4);
            int totalSize = iffDataSize + 8;

            int mcnkDataStart = mcnkAbsOff + 8;

            var info = new McnkInfo
            {
                AbsoluteOffset = mcnkAbsOff,
                TotalSize = totalSize,
                McnkHeaderSize = 128,
                IndexX = ReadInt32(data, mcnkDataStart + 4),  // 0x04
                IndexY = ReadInt32(data, mcnkDataStart + 8),  // 0x08
                McrfDoodadCount = ReadInt32(data, mcnkDataStart + 0x10),
                McrfMapObjCount = ReadInt32(data, mcnkDataStart + 0x38),
                McrfSubOffset = ReadInt32(data, mcnkDataStart + 0x20), // relative to MCNK chunk start (includes IFF header)
            };
            adt.Mcnks[i] = info;

            if (mcnkAbsOff < firstMcnk)
                firstMcnk = mcnkAbsOff;
        }

        adt.FirstMcnkOffset = firstMcnk;
        adt.HeaderRegionEnd = firstMcnk;

        return adt;
    }

    // ════════════════════════════════════════════════════════════════
    // SINGLE PLACEMENT — wrapper for backward compat
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Patch an ADT to add a single WMO placement. Delegates to batch method.
    /// </summary>
    public static byte[] AddWmoPlacement(byte[] originalAdt, WmoPlacement placement, uint uniqueId = 0)
    {
        return AddWmoPlacements(originalAdt, new[] { (placement, uniqueId) });
    }

    // ════════════════════════════════════════════════════════════════
    // BATCH PLACEMENT — parse once, patch once
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Patch an ADT to add multiple WMO placements in a single pass.
    /// Parse once, splice MWMO/MWID/MODF for all placements, patch MCRF
    /// only for overlapping MCNKs, rebuild offsets once.
    /// </summary>
    public static byte[] AddWmoPlacements(byte[] originalAdt, IReadOnlyList<(WmoPlacement placement, uint uniqueId)> placements)
    {
        if (placements.Count == 0) return originalAdt;

        var adt = Parse(originalAdt);

        // ─── Determine which grid tile this ADT belongs to ───
        // We need the tile's gridX/gridY to compute MCNK world-space bounds.
        // The ADT's MODF positions are in ADT coordinate space:
        //   worldX = 32*533.33 - modfZ, worldY = 32*533.33 - modfX
        // But for MCNK overlap, we work in MODF space directly.
        // Each MCNK covers CHUNK_SIZE (33.33) in each axis of MODF space.
        // The MCNK at grid [chunkX, chunkY] within this tile covers:
        //   MODF X range: [tileOriginX + chunkY * CHUNK_SIZE, tileOriginX + (chunkY+1) * CHUNK_SIZE]
        //   MODF Z range: [tileOriginZ + chunkX * CHUNK_SIZE, tileOriginZ + (chunkX+1) * CHUNK_SIZE]
        //
        // But we don't know the tile origin from the ADT alone. Instead, we test overlap
        // using the *relative* position of the WMO within the tile. The MODF bounding box
        // is in absolute coordinates, and we know the tile origin = (gridY * TILE_SIZE, 0, gridX * TILE_SIZE)
        // ... actually we DO have the MODF positions, so we can just compute which MCNKs overlap.
        //
        // Simpler approach: compute the MODF position's chunk indices within the tile,
        // then expand by the bounding box extent to get a range of MCNK indices.

        // ─── Step 1: Build per-placement data (MWMO/MWID/MODF bytes) ───
        int mwmoDataStart = adt.MwmoOffset + 8;
        int mwidDataStart = adt.MwidOffset + 8;
        int existingModfCount = adt.ModfSize / 64;

        // Accumulate all appends
        var mwmoAppendStream = new MemoryStream();
        var mwidAppendStream = new MemoryStream();
        var modfAppendStream = new MemoryStream();

        // Track which MODF indices are new and what MCNKs they overlap
        // Key: MCNK index (0-255), Value: list of new MODF indices to add to MCRF
        var mcnkModfRefs = new Dictionary<int, List<int>>();

        // Running MWMO size (original + appended so far) for byte offset calculation
        int runningMwmoSize = adt.MwmoSize;
        int runningMwidCount = adt.MwidSize / 4;

        for (int pi = 0; pi < placements.Count; pi++)
        {
            var (placement, uniqueId) = placements[pi];
            string normalizedPath = placement.WmoPath.Replace('/', '\\');
            byte[] pathBytes = Encoding.UTF8.GetBytes(normalizedPath);

            // Check if path already exists in MWMO (original data only — appended paths checked below)
            int wmoByteOffsetInMwmo;
            bool pathIsNew = true;

            int existingPathOffset = FindStringInChunk(originalAdt, mwmoDataStart, adt.MwmoSize, normalizedPath);
            if (existingPathOffset >= 0)
            {
                wmoByteOffsetInMwmo = existingPathOffset;
                pathIsNew = false;
            }
            else
            {
                // Also check if a previous placement in this batch already added the same path
                int appendOffset = FindStringInBytes(mwmoAppendStream.ToArray(), normalizedPath);
                if (appendOffset >= 0)
                {
                    wmoByteOffsetInMwmo = adt.MwmoSize + appendOffset;
                    pathIsNew = false;
                }
                else
                {
                    // New path — append to MWMO
                    wmoByteOffsetInMwmo = runningMwmoSize;
                    mwmoAppendStream.Write(pathBytes, 0, pathBytes.Length);
                    mwmoAppendStream.WriteByte(0); // null terminator
                    runningMwmoSize += pathBytes.Length + 1;

                    // MWID: append one uint32 entry
                    var mwidEntry = BitConverter.GetBytes(wmoByteOffsetInMwmo);
                    mwidAppendStream.Write(mwidEntry, 0, 4);
                    runningMwidCount++;
                }
            }

            // MODF: always append 64-byte entry
            int newModfIndex = existingModfCount + pi;
            byte[] modfRecord = new byte[64];

            WriteUint32(modfRecord, 0x00, (uint)wmoByteOffsetInMwmo);
            WriteUint32(modfRecord, 0x04, uniqueId);
            WriteFloat(modfRecord, 0x08, placement.PosX);
            WriteFloat(modfRecord, 0x0C, placement.PosY);
            WriteFloat(modfRecord, 0x10, placement.PosZ);
            WriteFloat(modfRecord, 0x14, placement.RotX);
            WriteFloat(modfRecord, 0x18, placement.RotY);
            WriteFloat(modfRecord, 0x1C, placement.RotZ);
            WriteFloat(modfRecord, 0x20, placement.BbMinX);
            WriteFloat(modfRecord, 0x24, placement.BbMinY);
            WriteFloat(modfRecord, 0x28, placement.BbMinZ);
            WriteFloat(modfRecord, 0x2C, placement.BbMaxX);
            WriteFloat(modfRecord, 0x30, placement.BbMaxY);
            WriteFloat(modfRecord, 0x34, placement.BbMaxZ);
            WriteUint16(modfRecord, 0x38, placement.Flags);
            WriteUint16(modfRecord, 0x3A, placement.DoodadSet);
            WriteUint16(modfRecord, 0x3C, placement.NameSet);
            WriteUint16(modfRecord, 0x3E, 0); // padding

            modfAppendStream.Write(modfRecord, 0, 64);

            // ─── Determine which MCNKs overlap this WMO's bounding box ───
            var overlapping = FindOverlappingMcnks(adt, placement);
            foreach (int mcnkIdx in overlapping)
            {
                if (!mcnkModfRefs.ContainsKey(mcnkIdx))
                    mcnkModfRefs[mcnkIdx] = new List<int>();
                mcnkModfRefs[mcnkIdx].Add(newModfIndex);
            }
        }

        byte[] mwmoAppend = mwmoAppendStream.ToArray();
        byte[] mwidAppend = mwidAppendStream.ToArray();
        byte[] modfAppend = modfAppendStream.ToArray();

        // ─── Step 2: Splice header region (MWMO/MWID/MODF) ───
        var splices = new List<(int chunkOffset, int chunkHeaderSize, int oldDataSize, byte[] appendData, string name)>
        {
            (adt.MwmoOffset, 8, adt.MwmoSize, mwmoAppend, "MWMO"),
            (adt.MwidOffset, 8, adt.MwidSize, mwidAppend, "MWID"),
            (adt.ModfOffset, 8, adt.ModfSize, modfAppend, "MODF"),
        };
        splices.Sort((a, b) => a.chunkOffset.CompareTo(b.chunkOffset));

        using var output = new MemoryStream();
        int cursor = 0;
        var chunkNewOffsets = new Dictionary<string, int>();

        foreach (var splice in splices)
        {
            int chunkStart = splice.chunkOffset;
            int chunkEnd = chunkStart + 8 + splice.oldDataSize;

            int bytesToCopy = chunkStart - cursor;
            if (bytesToCopy > 0)
                output.Write(originalAdt, cursor, bytesToCopy);

            chunkNewOffsets[splice.name] = (int)output.Position;

            // Write original chunk verbatim
            output.Write(originalAdt, chunkStart, 8 + splice.oldDataSize);

            // Append new data
            if (splice.appendData.Length > 0)
                output.Write(splice.appendData, 0, splice.appendData.Length);

            // Fix the chunk's size field
            int newDataSize = splice.oldDataSize + splice.appendData.Length;
            long savedPos = output.Position;
            output.Position = chunkNewOffsets[splice.name] + 4;
            output.Write(BitConverter.GetBytes(newDataSize), 0, 4);
            output.Position = savedPos;

            cursor = chunkEnd;
        }

        // Copy remaining header bytes up to first MCNK
        int remainingHeader = adt.FirstMcnkOffset - cursor;
        if (remainingHeader > 0)
            output.Write(originalAdt, cursor, remainingHeader);

        // ─── Step 3: Write MCNKs — only patch those that need MCRF updates ───
        // DIAGNOSTIC: Set to true to skip MCRF patching and test if header splice alone works
        bool SKIP_MCRF_PATCH = false; // IFF-chain walk approach — safe to enable

        int[] newMcnkOffsets = new int[256];
        int[] newMcnkSizes = new int[256];

        for (int i = 0; i < 256; i++)
        {
            var mcnk = adt.Mcnks[i];
            newMcnkOffsets[i] = (int)output.Position;

            if (!SKIP_MCRF_PATCH && mcnkModfRefs.TryGetValue(i, out var modfIndices) && modfIndices.Count > 0)
            {
                // This MCNK overlaps one or more new WMOs — patch its MCRF
                byte[] patchedMcnk = PatchMcnkMcrf(originalAdt, mcnk, modfIndices);
                output.Write(patchedMcnk, 0, patchedMcnk.Length);
                newMcnkSizes[i] = patchedMcnk.Length;
            }
            else
            {
                // No overlap — copy verbatim
                output.Write(originalAdt, mcnk.AbsoluteOffset, mcnk.TotalSize);
                newMcnkSizes[i] = mcnk.TotalSize;
            }
        }

        // Copy anything after the last MCNK
        int afterMcnks = 0;
        for (int i = 0; i < 256; i++)
        {
            int end = adt.Mcnks[i].AbsoluteOffset + adt.Mcnks[i].TotalSize;
            if (end > afterMcnks) afterMcnks = end;
        }
        if (afterMcnks < originalAdt.Length)
            output.Write(originalAdt, afterMcnks, originalAdt.Length - afterMcnks);

        // ─── Step 4: Fix offsets ───
        byte[] result = output.ToArray();

        int mverSize = ReadInt32(result, 4);
        int mhdrChunkPos = 8 + mverSize;
        int mhdrDataPos = mhdrChunkPos + 8;

        // Build shift map
        var shiftPoints = splices
            .Select(s => (origEnd: s.chunkOffset + 8 + s.oldDataSize, growth: s.appendData.Length))
            .OrderBy(s => s.origEnd)
            .ToList();

        int ComputeShift(int origOffset)
        {
            int shift = 0;
            foreach (var sp in shiftPoints)
            {
                if (origOffset >= sp.origEnd)
                    shift += sp.growth;
                else
                    break;
            }
            return shift;
        }

        // Fix MHDR offset fields
        int origMhdrData = adt.MhdrDataOffset;
        int[] mhdrFields = { 4, 8, 12, 16, 20, 24, 28, 32 };
        foreach (int field in mhdrFields)
        {
            int origRelOffset = ReadInt32(originalAdt, origMhdrData + field);
            int origAbsOffset = origMhdrData + origRelOffset;
            int newAbsOffset = origAbsOffset + ComputeShift(origAbsOffset);
            int newRelOffset = newAbsOffset - mhdrDataPos;
            WriteInt32(result, mhdrDataPos + field, newRelOffset);
        }

        // Fix optional MHDR fields (MFBO, MH2O, MTFX)
        int[] mhdrOptionalFields = { 36, 40, 44 };
        foreach (int field in mhdrOptionalFields)
        {
            if (field + 4 > 64) break;
            int origRelOffset = ReadInt32(originalAdt, origMhdrData + field);
            if (origRelOffset != 0)
            {
                int origAbsOffset = origMhdrData + origRelOffset;
                int newAbsOffset = origAbsOffset + ComputeShift(origAbsOffset);
                int newRelOffset = newAbsOffset - mhdrDataPos;
                WriteInt32(result, mhdrDataPos + field, newRelOffset);
            }
        }

        // Fix MCIN entries
        int mcinOrigAbs = adt.McinOffset;
        int mcinNewAbs = mcinOrigAbs + ComputeShift(mcinOrigAbs);
        int mcinDataStart = mcinNewAbs + 8;

        for (int i = 0; i < 256; i++)
        {
            int entryBase = mcinDataStart + i * 16;
            WriteInt32(result, entryBase, newMcnkOffsets[i]);
            WriteInt32(result, entryBase + 4, newMcnkSizes[i]);  // MCIN size = total including IFF header
            WriteInt32(result, entryBase + 8, 0);  // flags
            WriteInt32(result, entryBase + 12, 0);  // asyncId
        }

        return result;
    }

    // ════════════════════════════════════════════════════════════════
    // MCNK OVERLAP DETECTION
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determine which MCNK indices (0-255) overlap a WMO's MODF bounding box.
    ///
    /// MODF coordinates: origin at map corner (0,0), X and Z increase across tile.
    /// Each ADT tile = TILE_SIZE (533.33) in MODF space.
    /// Each MCNK = CHUNK_SIZE (33.33) in MODF space.
    ///
    /// We need to know which tile this ADT is for. Since we have the MODF positions
    /// in absolute MODF space, and we know the tile covers a TILE_SIZE × TILE_SIZE
    /// region, we derive the tile origin from the first MCNK's actual position data.
    /// However, MCNK headers store IndexX/IndexY (0-15) which are chunk-local indices.
    ///
    /// Strategy: Since the MODF bounding box is in absolute coordinates and we know
    /// each chunk covers CHUNK_SIZE in X and Z, we can compute which chunks the
    /// bounding box intersects by dividing the MODF min/max by CHUNK_SIZE and
    /// comparing to each MCNK's absolute position.
    ///
    /// Actually, simpler: the MODF position is already in the same coordinate space
    /// as the ADT. The tile at grid (gridX, gridY) covers:
    ///   MODF X: [gridY * TILE_SIZE, (gridY+1) * TILE_SIZE]
    ///   MODF Z: [gridX * TILE_SIZE, (gridX+1) * TILE_SIZE]
    /// Each MCNK (chunkRow, chunkCol) within the tile covers:
    ///   MODF X: [gridY*TILE_SIZE + chunkCol*CHUNK_SIZE, gridY*TILE_SIZE + (chunkCol+1)*CHUNK_SIZE]
    ///   MODF Z: [gridX*TILE_SIZE + chunkRow*CHUNK_SIZE, gridX*TILE_SIZE + (chunkRow+1)*CHUNK_SIZE]
    ///
    /// We don't know gridX/gridY from the ADT alone, but we can derive the tile origin
    /// from the MODF position (it must fall within the tile).
    ///
    /// Even simpler: we can convert the MODF bounding box to chunk-relative indices
    /// and just test those. Since we don't know the tile origin, we'll use the
    /// placement's center position to estimate it, then compute chunk overlap.
    ///
    /// SAFEST approach: for each MCNK, compute its world-space AABB from the known
    /// tile origin (derived from the placement position), then test intersection with
    /// the MODF bounding box.
    /// </summary>
    private static List<int> FindOverlappingMcnks(AdtFile adt, WmoPlacement placement)
    {
        var result = new List<int>();

        // Derive tile origin from the placement position
        // Tile gridY = floor(modfPosX / TILE_SIZE), gridX = floor(modfPosZ / TILE_SIZE)
        int tileGridY = (int)Math.Floor(placement.PosX / TILE_SIZE);
        int tileGridX = (int)Math.Floor(placement.PosZ / TILE_SIZE);
        float tileOriginX = tileGridY * TILE_SIZE;  // MODF X origin of this tile
        float tileOriginZ = tileGridX * TILE_SIZE;  // MODF Z origin of this tile

        // Bounding box in MODF space
        float bbMinX = placement.BbMinX;
        float bbMaxX = placement.BbMaxX;
        float bbMinZ = placement.BbMinZ;
        float bbMaxZ = placement.BbMaxZ;
        // Y (height) — we don't filter by Y since MCNKs span all heights

        for (int i = 0; i < 256; i++)
        {
            var mcnk = adt.Mcnks[i];
            int chunkRow = mcnk.IndexX; // 0-15, maps to Z axis in MODF
            int chunkCol = mcnk.IndexY; // 0-15, maps to X axis in MODF

            float chunkMinX = tileOriginX + chunkCol * CHUNK_SIZE;
            float chunkMaxX = chunkMinX + CHUNK_SIZE;
            float chunkMinZ = tileOriginZ + chunkRow * CHUNK_SIZE;
            float chunkMaxZ = chunkMinZ + CHUNK_SIZE;

            // AABB overlap test in XZ plane
            if (bbMaxX >= chunkMinX && bbMinX <= chunkMaxX &&
                bbMaxZ >= chunkMinZ && bbMinZ <= chunkMaxZ)
            {
                result.Add(i);
            }
        }

        // Safety: if overlap test found nothing (possible if bbox is wrong or coordinate mismatch),
        // fall back to ALL chunks so the WMO at least renders somewhere
        if (result.Count == 0)
        {
            for (int i = 0; i < 256; i++)
                result.Add(i);
        }

        return result;
    }

    // ════════════════════════════════════════════════════════════════
    // MCNK PATCHING — add MODF indices to MCRF
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rebuild a single MCNK chunk with additional MODF indices appended to MCRF.
    ///
    /// Strategy:
    ///   1. Scan the MCNK bytes for the actual MCRF sub-chunk magic (belt-and-suspenders vs ofsMCRF)
    ///   2. Use the MCRF IFF size field to find the exact end of MCRF data
    ///   3. Insert N*4 bytes at the end of MCRF data
    ///   4. Fix: MCNK IFF size, nMapObjRefs, MCRF IFF size, all sub-chunk offsets past insertion
    ///
    /// Offset reference frames (CRITICAL):
    ///   - MCNK header offsets (ofsMCRF, ofsMCAL, etc.) are relative to the MCNK
    ///     CHUNK START — the position of the "MCNK" magic bytes (AbsoluteOffset).
    ///   - "Within newMcnk[]" offsets are relative to index 0 of the local byte array,
    ///     which corresponds to AbsoluteOffset in the original file.
    ///   - So: position_in_newMcnk = MCNK_header_offset_value (they share the same base)
    /// </summary>
    private static byte[] PatchMcnkMcrf(byte[] adt, McnkInfo mcnk, List<int> newModfIndices)
    {
        int insertCount = newModfIndices.Count;
        int insertBytes = insertCount * 4;

        // ── MCRF is HEADERLESS — no IFF magic+size ──
        // Unlike other MCNK sub-chunks, MCRF has no "FRCM" magic and no size field.
        // ofsMCRF points DIRECTLY to the raw uint32 data.
        // Data layout: [nDoodadRefs × uint32 MDDF indices] [nMapObjRefs × uint32 MODF indices]
        // Total data size = (nDoodadRefs + nMapObjRefs) * 4
        //
        // We append our new MODF indices at the end of the existing MCRF data
        // (after all doodad refs + all existing WMO refs).

        int mcnkStart = mcnk.AbsoluteOffset;

        // ofsMCRF is relative to MCNK chunk start (including IFF header)
        // It points directly to the first uint32 of MCRF data
        int mcrfDataRelOff = mcnk.McrfSubOffset;
        int existingDataSize = (mcnk.McrfDoodadCount + mcnk.McrfMapObjCount) * 4;
        int insertRelOff = mcrfDataRelOff + existingDataSize; // append after existing data

        // Bounds check
        if (insertRelOff < 0 || insertRelOff > mcnk.TotalSize)
        {
            byte[] unchanged = new byte[mcnk.TotalSize];
            Array.Copy(adt, mcnkStart, unchanged, 0, mcnk.TotalSize);
            return unchanged;
        }

        // Build new MCNK: original bytes + extra bytes for new MCRF entries
        byte[] newMcnk = new byte[mcnk.TotalSize + insertBytes];

        // Copy up to insertion point
        Array.Copy(adt, mcnkStart, newMcnk, 0, insertRelOff);

        // Write new MODF indices
        for (int n = 0; n < insertCount; n++)
            WriteInt32(newMcnk, insertRelOff + n * 4, newModfIndices[n]);

        // Copy remainder (everything after the old MCRF data end)
        int insertAbsOff = mcnkStart + insertRelOff;
        int remaining = mcnk.TotalSize - insertRelOff;
        Array.Copy(adt, insertAbsOff, newMcnk, insertRelOff + insertBytes, remaining);

        // Fix MCNK IFF size (at offset 4)
        WriteInt32(newMcnk, 4, (mcnk.TotalSize - 8) + insertBytes);

        // Fix nMapObjRefs in MCNK header (data offset 0x38)
        int oldObjCount = ReadInt32(newMcnk, 8 + 0x38);
        WriteInt32(newMcnk, 8 + 0x38, oldObjCount + insertCount);

        // Adjust sub-chunk offsets in MCNK header that point PAST the insertion
        // ofsMCRF (0x20) does NOT need adjustment — MCRF data start didn't move
        // But everything after the MCRF data region needs +insertBytes
        int[] subChunkOffsetFields = { 0x14, 0x18, 0x1C, 0x24, 0x2C, 0x58, 0x60, 0x74 };
        foreach (int field in subChunkOffsetFields)
        {
            int headerOff = 8 + field;
            int subChunkOff = ReadInt32(newMcnk, headerOff);
            if (subChunkOff > 0 && subChunkOff >= insertRelOff)
                WriteInt32(newMcnk, headerOff, subChunkOff + insertBytes);
        }

        return newMcnk;
    }

    // ════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════

    public static string GetAdtMpqPath(string mapName, int gridX, int gridY)
    {
        return $"World\\Maps\\{mapName}\\{mapName}_{gridY}_{gridX}.adt";
    }

    public static string GetMapName(int mapId)
    {
        return mapId switch
        {
            0 => "Azeroth",
            1 => "Kalimdor",
            _ => $"Map{mapId}"
        };
    }

    private static void ValidateChunkMagic(byte[] data, int offset, string expected)
    {
        string onDisk = Encoding.ASCII.GetString(data, offset, 4);
        string reversed = new string(expected.Reverse().ToArray());
        if (onDisk != reversed)
            throw new InvalidDataException($"Expected chunk '{expected}' (on-disk '{reversed}') at offset {offset}, got '{onDisk}'");
    }

    /// <summary>Find a null-terminated string in a byte array. Returns offset or -1.</summary>
    private static int FindStringInBytes(byte[] data, string needle)
    {
        if (data.Length == 0) return -1;
        byte[] needleBytes = Encoding.UTF8.GetBytes(needle);
        int end = data.Length - needleBytes.Length;

        for (int i = 0; i <= end; i++)
        {
            if (i > 0 && data[i - 1] != 0) continue; // must be at string start
            bool match = true;
            for (int j = 0; j < needleBytes.Length; j++)
            {
                if (data[i + j] != needleBytes[j]) { match = false; break; }
            }
            if (match && (i + needleBytes.Length >= data.Length || data[i + needleBytes.Length] == 0))
                return i;
        }
        return -1;
    }

    private static int FindStringInChunk(byte[] data, int chunkDataStart, int chunkDataSize, string needle)
    {
        byte[] needleBytes = Encoding.UTF8.GetBytes(needle);
        int end = chunkDataStart + chunkDataSize - needleBytes.Length;

        for (int i = chunkDataStart; i <= end; i++)
        {
            if (i > chunkDataStart && data[i - 1] != 0) continue;
            bool match = true;
            for (int j = 0; j < needleBytes.Length; j++)
            {
                if (data[i + j] != needleBytes[j]) { match = false; break; }
            }
            if (match && (i + needleBytes.Length >= chunkDataStart + chunkDataSize
                         || data[i + needleBytes.Length] == 0))
            {
                return i - chunkDataStart;
            }
        }
        return -1;
    }

    private static int ReadInt32(byte[] data, int offset) => BitConverter.ToInt32(data, offset);
    private static uint ReadUint32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static float ReadFloat(byte[] data, int offset) => BitConverter.ToSingle(data, offset);

    private static void WriteInt32(byte[] data, int offset, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Copy(bytes, 0, data, offset, 4);
    }

    private static void WriteUint32(byte[] data, int offset, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Copy(bytes, 0, data, offset, 4);
    }

    private static void WriteFloat(byte[] data, int offset, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Copy(bytes, 0, data, offset, 4);
    }

    private static void WriteUint16(byte[] data, int offset, ushort value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Copy(bytes, 0, data, offset, 2);
    }

    private static byte[] ExtractChunkData(byte[] adt, int chunkOffset)
    {
        int size = ReadInt32(adt, chunkOffset + 4);
        byte[] data = new byte[size];
        Array.Copy(adt, chunkOffset + 8, data, 0, size);
        return data;
    }

    private static void WriteChunk(BinaryWriter bw, string magic, byte[] sourceData, int dataOffset, int dataSize)
    {
        byte[] magicBytes = Encoding.ASCII.GetBytes(magic);
        Array.Reverse(magicBytes);
        bw.Write(magicBytes);
        bw.Write(dataSize);
        bw.Write(sourceData, dataOffset, dataSize);
    }

    private static void WriteChunkFromBytes(BinaryWriter bw, string magic, byte[] data)
    {
        byte[] magicBytes = Encoding.ASCII.GetBytes(magic);
        Array.Reverse(magicBytes);
        bw.Write(magicBytes);
        bw.Write(data.Length);
        bw.Write(data);
    }

    private static void CopyChunk(BinaryWriter bw, byte[] adt, int chunkOffset)
    {
        int size = ReadInt32(adt, chunkOffset + 4);
        bw.Write(adt, chunkOffset, 8 + size);
    }
}