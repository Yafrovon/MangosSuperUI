using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// A vanilla (v18) ADT held as editable parts and written back canonically:
/// MVER, MHDR, MCIN, MTEX, MMDX, MMID, MWMO, MWID, MDDF, MODF, 256 × MCNK.
///
/// Replaces the byte-splicing AdtPatcherService for the World Builder. Everything not edited
/// (textures, alpha maps, shadows, liquids, holes, area ids, sound emitters) is carried through
/// byte-for-byte; a parse → write of an untouched vanilla ADT reproduces the input exactly
/// (WorldPackClinicalTests pins that).
///
/// Edits supported:
///  * <see cref="ApplySculpt"/> — add per-V9-vertex height deltas; inner (V8) vertices take the
///    mean of their four corners' deltas; MCNR normals of every touched chunk are recomputed.
///  * <see cref="AddWmo"/> / <see cref="AddDoodad"/> — append MODF/MDDF entries (sharing name
///    tables) and reference them from the MCRF of every overlapped MCNK.
/// </summary>
public sealed class AdtDocument
{
    private const int McnkHeaderSize = 128;

    public int Col { get; private set; }
    public int Row { get; private set; }

    private byte[] _mhdr = new byte[64];
    private byte[] _mtex = Array.Empty<byte>();
    private readonly List<string> _doodadNames = new();
    private readonly List<string> _wmoNames = new();
    private readonly List<byte[]> _mddf = new();   // 36 bytes each
    private readonly List<byte[]> _modf = new();   // 64 bytes each
    private readonly byte[][] _mcnk = new byte[256][];
    private readonly bool[] _normalsDirty = new bool[256];
    /// <summary>Outer vertices (129×129) whose height a sculpt moved: only normals whose stencil
    /// touches one are rewritten, so the rest keep Blizzard's baked normals byte-for-byte.</summary>
    private readonly bool[] _heightMoved = new bool[129 * 129];
    private bool _recomputeAll;

    public int DoodadCount => _mddf.Count;
    public IReadOnlyList<string> WmoNames => _wmoNames;

    /// <summary>Every MODF entry as (model path, placement-space position, uniqueId).</summary>
    public IEnumerable<(string path, Vector3 pos, uint uid)> WmoPlacements() => _modf.Select(e =>
    {
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0));
        var p = new Vector3(F32(e, 8), F32(e, 12), F32(e, 16));
        return (n < _wmoNames.Count ? _wmoNames[n] : "?", p, BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4)));
    });
    public int WmoCount => _modf.Count;

    /// <summary>Every MODF entry with its rotation (degrees), for the placement audit.</summary>
    public IEnumerable<(string path, uint uid, Vector3 pos, Vector3 rot)> WmoPlacementsFull() => _modf.Select(e =>
    {
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0));
        return (n < _wmoNames.Count ? _wmoNames[n] : "?", BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4)),
            new Vector3(F32(e, 8), F32(e, 12), F32(e, 16)), new Vector3(F32(e, 20), F32(e, 24), F32(e, 28)));
    });

    /// <summary>Every MDDF entry (model path, uid, placement-space position, rotation, scale).</summary>
    public IEnumerable<(string path, uint uid, Vector3 pos, Vector3 rot, float scale)> DoodadPlacements() => _mddf.Select(e =>
    {
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0));
        return (n < _doodadNames.Count ? _doodadNames[n] : "?", BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4)),
            new Vector3(F32(e, 8), F32(e, 12), F32(e, 16)), new Vector3(F32(e, 20), F32(e, 24), F32(e, 28)),
            BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(32)) / 1024f);
    });

    /// <summary>Texture paths of one MCNK's layers (MCLY → MTEX), bottom layer first.</summary>
    public IReadOnlyList<string> ChunkTextures(int mcinIndex) => ChunkLayers(mcinIndex).Select(l => l.texture).ToList();

    /// <summary>
    /// One MCNK's texture layers with their 64×64 alpha (0..1; the base layer is 1 − the others).
    /// Vanilla MCAL is uncompressed 4-bit, 2048 bytes a layer, row-major: pixel row r runs along
    /// the chunk's world-X axis (north → south), column c along world-Y (west → east).
    /// </summary>
    public List<(string texture, float[] alpha)> ChunkLayers(int mcinIndex)
    {
        var names = System.Text.Encoding.ASCII.GetString(_mtex).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        byte[] c = _mcnk[mcinIndex];
        int n = I32(c, 8 + 0x0C), ofsLayer = I32(c, 8 + 0x1C), ofsAlpha = I32(c, 8 + 0x24);
        var layers = new List<(string, float[])>();
        var rest = new float[64 * 64];   // pixel count only
        for (int i = 0; i < n && ofsLayer > 0 && ofsLayer + 8 + i * 16 + 16 <= c.Length; i++)
        {
            int e = ofsLayer + 8 + i * 16;
            int tex = I32(c, e);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(e + 4));
            int at = ofsAlpha + 8 + I32(c, e + 8);
            var a = new float[64 * 64];
            if (i > 0 && (flags & 0x100) != 0 && ofsAlpha > 0 && at + 2048 <= c.Length)
                for (int p = 0; p < 2048; p++)
                {
                    byte b = c[at + p];
                    a[p * 2] = (b & 0x0F) / 15f;
                    a[p * 2 + 1] = (b >> 4) / 15f;
                }
            layers.Add((tex >= 0 && tex < names.Length ? names[tex] : "?", a));
        }
        // Layers blend upward (each alpha over everything beneath), so a layer's visible weight is its
        // alpha times what the layers above leave through; the base layer gets the remainder.
        for (int p = 0; p < rest.Length; p++)
        {
            float through = 1f;
            for (int i = layers.Count - 1; i >= 1; i--)
            {
                float a = layers[i].Item2[p];
                layers[i].Item2[p] = a * through;
                through *= 1f - a;
            }
            if (layers.Count > 0) layers[0].Item2[p] = through;
        }
        return layers;
    }

    /// <summary>MCIN index of the chunk with index (ix, iy), or −1.</summary>
    public int ChunkIndex(int ix, int iy)
    {
        for (int i = 0; i < 256; i++)
            if (I32(_mcnk[i], 8 + 0x04) == ix && I32(_mcnk[i], 8 + 0x08) == iy) return i;
        return -1;
    }

    private AdtDocument(int col, int row)
    {
        Col = col;
        Row = row;
    }

    // ═══════════════════════════════════════════════════════════════ parse

    public static AdtDocument Parse(byte[] data, int col, int row)
    {
        var doc = new AdtDocument(col, row);
        Expect(data, 0, "MVER");
        int mhdrAt = 8 + I32(data, 4);
        Expect(data, mhdrAt, "MHDR");
        int mhdrSize = I32(data, mhdrAt + 4);
        int mhdrData = mhdrAt + 8;
        doc._mhdr = data.AsSpan(mhdrData, mhdrSize).ToArray();

        byte[] Chunk(int headerFieldOffset, string magic)
        {
            int rel = I32(data, mhdrData + headerFieldOffset);
            int at = mhdrData + rel;
            Expect(data, at, magic);
            return data.AsSpan(at + 8, I32(data, at + 4)).ToArray();
        }

        byte[] mcin = Chunk(4, "MCIN");
        doc._mtex = Chunk(8, "MTEX");
        doc._doodadNames.AddRange(ReadNames(Chunk(12, "MMDX"), Chunk(16, "MMID")));
        doc._wmoNames.AddRange(ReadNames(Chunk(20, "MWMO"), Chunk(24, "MWID")));

        byte[] mddf = Chunk(28, "MDDF");
        for (int i = 0; i + 36 <= mddf.Length; i += 36) doc._mddf.Add(mddf.AsSpan(i, 36).ToArray());
        byte[] modf = Chunk(32, "MODF");
        for (int i = 0; i + 64 <= modf.Length; i += 64) doc._modf.Add(modf.AsSpan(i, 64).ToArray());

        for (int i = 0; i < 256; i++)
        {
            int off = I32(mcin, i * 16);
            int size = I32(mcin, i * 16 + 4);
            Expect(data, off, "MCNK");
            // MCIN's size is authoritative (it includes the 8-byte header); fall back to the IFF size.
            if (size <= 8 || off + size > data.Length) size = I32(data, off + 4) + 8;
            doc._mcnk[i] = data.AsSpan(off, size).ToArray();
        }

        return doc;
    }

    private static List<string> ReadNames(byte[] blob, byte[] offsets)
    {
        var names = new List<string>();
        for (int i = 0; i + 4 <= offsets.Length; i += 4)
        {
            int start = I32(offsets, i);
            int end = start;
            while (end < blob.Length && blob[end] != 0) end++;
            names.Add(Encoding.ASCII.GetString(blob, start, end - start));
        }
        return names;
    }

    // ═══════════════════════════════════════════════════════════════ write

    public byte[] Write()
    {
        RecomputeDirtyNormals();
        using var ms = new MemoryStream(256 * 1024);
        var w = new BinaryWriter(ms);

        WriteChunk(w, "MVER", BitConverter.GetBytes(18));

        long mhdrAt = ms.Position;
        WriteChunk(w, "MHDR", _mhdr);            // offsets patched below
        long mhdrData = mhdrAt + 8;

        long mcinAt = ms.Position;
        WriteChunk(w, "MCIN", new byte[256 * 16]); // patched below

        long mtexAt = ms.Position; WriteChunk(w, "MTEX", _mtex);
        var (mmdx, mmid) = BuildNames(_doodadNames);
        long mmdxAt = ms.Position; WriteChunk(w, "MMDX", mmdx);
        long mmidAt = ms.Position; WriteChunk(w, "MMID", mmid);
        var (mwmo, mwid) = BuildNames(_wmoNames);
        long mwmoAt = ms.Position; WriteChunk(w, "MWMO", mwmo);
        long mwidAt = ms.Position; WriteChunk(w, "MWID", mwid);
        long mddfAt = ms.Position; WriteChunk(w, "MDDF", _mddf.SelectMany(b => b).ToArray());
        long modfAt = ms.Position; WriteChunk(w, "MODF", _modf.SelectMany(b => b).ToArray());

        var mcin = new byte[256 * 16];
        for (int i = 0; i < 256; i++)
        {
            byte[] chunk = _mcnk[i];
            WI32(mcin, i * 16, (int)ms.Position);
            WI32(mcin, i * 16 + 4, chunk.Length);
            w.Write(chunk);
        }

        byte[] file = ms.ToArray();
        Buffer.BlockCopy(mcin, 0, file, (int)mcinAt + 8, mcin.Length);
        void Rel(int field, long at) => WI32(file, (int)mhdrData + field, (int)(at - mhdrData));
        Rel(4, mcinAt); Rel(8, mtexAt); Rel(12, mmdxAt); Rel(16, mmidAt);
        Rel(20, mwmoAt); Rel(24, mwidAt); Rel(28, mddfAt); Rel(32, modfAt);
        return file;
    }

    private static (byte[] blob, byte[] offsets) BuildNames(List<string> names)
    {
        var blob = new MemoryStream();
        var offsets = new byte[names.Count * 4];
        for (int i = 0; i < names.Count; i++)
        {
            WI32(offsets, i * 4, (int)blob.Length);
            var bytes = Encoding.ASCII.GetBytes(names[i]);
            blob.Write(bytes);
            blob.WriteByte(0);
        }
        return (blob.ToArray(), offsets);
    }

    private static void WriteChunk(BinaryWriter w, string magic, byte[] data)
    {
        // IFF magics are stored reversed ("MVER" → "REVM").
        w.Write((byte)magic[3]); w.Write((byte)magic[2]); w.Write((byte)magic[1]); w.Write((byte)magic[0]);
        w.Write(data.Length);
        w.Write(data);
    }

    // ═══════════════════════════════════════════════════════════════ sculpt

    /// <summary>
    /// Add height deltas to the outer V9 grid (index = gridRow * 129 + gridCol). Returns the
    /// number of MCVT floats changed.
    /// </summary>
    public int ApplySculpt(IReadOnlyDictionary<int, float> deltas, IReadOnlyDictionary<int, float>? innerDeltas = null)
    {
        if (deltas.Count == 0 && (innerDeltas is null || innerDeltas.Count == 0)) return 0;
        var d = new float[129 * 129];
        foreach (var (idx, delta) in deltas)
            if ((uint)idx < (uint)d.Length) d[idx] = delta;

        int changed = 0;
        for (int i = 0; i < 256; i++)
        {
            byte[] c = _mcnk[i];
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            int mcvt = I32(c, 8 + 0x14);
            if (mcvt <= 0 || mcvt + 8 + 145 * 4 > c.Length) continue;
            bool touched = false;
            for (int r = 0; r <= 8; r++)
                for (int col = 0; col <= 8; col++)
                {
                    float delta = d[(iy * 8 + r) * 129 + ix * 8 + col];
                    if (delta == 0f) continue;
                    AddF32(c, mcvt + 8 + (r * 17 + col) * 4, delta);
                    _heightMoved[(iy * 8 + r) * 129 + ix * 8 + col] = true;
                    touched = true; changed++;
                }
            for (int r = 0; r < 8; r++)
                for (int col = 0; col < 8; col++)
                {
                    int g0 = (iy * 8 + r) * 129 + ix * 8 + col;
                    float delta = innerDeltas is null
                        ? (d[g0] + d[g0 + 1] + d[g0 + 129] + d[g0 + 130]) * 0.25f
                        : innerDeltas.GetValueOrDefault((iy * 8 + r) * 128 + ix * 8 + col);
                    if (delta == 0f) continue;
                    AddF32(c, mcvt + 8 + (r * 17 + 9 + col) * 4, delta);
                    if (innerDeltas is not null)
                        _heightMoved[g0] = _heightMoved[g0 + 1] = _heightMoved[g0 + 129] = _heightMoved[g0 + 130] = true;
                    touched = true; changed++;
                }
            if (touched) _normalsDirty[i] = true;
        }

        // A chunk's edge normals depend on its neighbours' heights: widen by one chunk.
        var dirty = (bool[])_normalsDirty.Clone();
        for (int i = 0; i < 256; i++)
        {
            if (!dirty[i]) continue;
            int x = i % 16, y = i / 16;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx is >= 0 and < 16 && ny is >= 0 and < 16) _normalsDirty[ny * 16 + nx] = true;
                }
        }
        return changed;
    }

    /// <summary>Recompute MCNR for every chunk a sculpt touched (idempotent; Write-time safe).</summary>
    public void RecomputeDirtyNormals()
    {
        if (!_normalsDirty.Any(x => x)) return;
        var outer = new float[129, 129];
        var inner = new float[128, 128];
        var chunkAt = new int[256];
        for (int i = 0; i < 256; i++)
        {
            byte[] c = _mcnk[i];
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            chunkAt[iy * 16 + ix] = i;
            int mcvt = I32(c, 8 + 0x14);
            float baseZ = F32(c, 8 + 0x70);
            for (int r = 0; r <= 8; r++)
                for (int col = 0; col <= 8; col++)
                    outer[iy * 8 + r, ix * 8 + col] = baseZ + F32(c, mcvt + 8 + (r * 17 + col) * 4);
            for (int r = 0; r < 8; r++)
                for (int col = 0; col < 8; col++)
                    inner[iy * 8 + r, ix * 8 + col] = baseZ + F32(c, mcvt + 8 + (r * 17 + 9 + col) * 4);
        }

        for (int i = 0; i < 256; i++)
        {
            if (!_normalsDirty[i]) continue;
            byte[] c = _mcnk[i];
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            int mcnr = I32(c, 8 + 0x18);
            if (mcnr <= 0 || mcnr + 8 + 145 * 3 > c.Length) continue;
            for (int r = 0; r <= 8; r++)
                for (int col = 0; col <= 8; col++)
                {
                    int gr = iy * 8 + r, gc = ix * 8 + col;
                    if (!NormalStale(gr, gc, inner: false)) continue;
                    int r0 = Math.Max(gr - 1, 0), r1 = Math.Min(gr + 1, 128);
                    int c0 = Math.Max(gc - 1, 0), c1 = Math.Min(gc + 1, 128);
                    float dRow = (outer[r1, gc] - outer[r0, gc]) / ((r1 - r0) * WorldCoords.Unit);
                    float dCol = (outer[gr, c1] - outer[gr, c0]) / ((c1 - c0) * WorldCoords.Unit);
                    WriteNormal(c, mcnr + 8 + (r * 17 + col) * 3, dRow, dCol);
                }
            for (int r = 0; r < 8; r++)
                for (int col = 0; col < 8; col++)
                {
                    int gr = iy * 8 + r, gc = ix * 8 + col;
                    if (!NormalStale(gr, gc, inner: true)) continue;
                    float dRow = ((outer[gr + 1, gc] + outer[gr + 1, gc + 1]) - (outer[gr, gc] + outer[gr, gc + 1])) * 0.5f / WorldCoords.Unit;
                    float dCol = ((outer[gr, gc + 1] + outer[gr + 1, gc + 1]) - (outer[gr, gc] + outer[gr + 1, gc])) * 0.5f / WorldCoords.Unit;
                    WriteNormal(c, mcnr + 8 + (r * 17 + 9 + col) * 3, dRow, dCol);
                }
            _normalsDirty[i] = false;
        }
        _recomputeAll = false;
    }

    /// <summary>
    /// Restore the baseline surface wherever sculpting returned its outer grid to stock. Inner
    /// vertices otherwise retain the stamped source's small height residuals, and recomputed MCNR
    /// differs from Blizzard's baked normals even when the final surrounding terrain is unchanged.
    /// Changed terrain keeps its sculpted heights and freshly computed normals.
    /// </summary>
    public void RestoreUnchangedSurfaceFrom(AdtDocument baseline)
    {
        var current = OuterHeights(); var original = baseline.OuterHeights();
        bool Same(int r, int col) => Math.Abs(current[r * 129 + col] - original[r * 129 + col]) <= 0.001f;
        bool SameStencil(int r, int col)
        {
            for (int rr = Math.Max(0, r - 1); rr <= Math.Min(128, r + 2); rr++)
                for (int cc = Math.Max(0, col - 1); cc <= Math.Min(128, col + 2); cc++)
                    if (!Same(rr, cc)) return false;
            return true;
        }
        for (int i = 0; i < 256; i++)
        {
            byte[] c = _mcnk[i];
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            int si = baseline.ChunkIndex(ix, iy); if (si < 0) continue;
            byte[] sc = baseline._mcnk[si];
            int mcvt = I32(c, 8 + 0x14), smcvt = I32(sc, 8 + 0x14);
            float baseZ = F32(c, 8 + 0x70), stockZ = F32(sc, 8 + 0x70);
            for (int r = 0; r < 8; r++) for (int col = 0; col < 8; col++)
            {
                int gr = iy * 8 + r, gc = ix * 8 + col, vertex = r * 17 + 9 + col;
                if (Same(gr, gc) && Same(gr + 1, gc) && Same(gr, gc + 1) && Same(gr + 1, gc + 1))
                {
                    if (baseZ == stockZ) Buffer.BlockCopy(sc, smcvt + 8 + vertex * 4, c, mcvt + 8 + vertex * 4, 4);
                    else BinaryPrimitives.WriteSingleLittleEndian(c.AsSpan(mcvt + 8 + vertex * 4),
                        stockZ + F32(sc, smcvt + 8 + vertex * 4) - baseZ);
                }
            }
        }
        // Finalize dirty normals before restoring baked values so Write() cannot overwrite them.
        RecomputeDirtyNormals();
        for (int i = 0; i < 256; i++)
        {
            byte[] c = _mcnk[i];
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            int si = baseline.ChunkIndex(ix, iy); if (si < 0) continue;
            byte[] sc = baseline._mcnk[si];
            int mcnr = I32(c, 8 + 0x18), smcnr = I32(sc, 8 + 0x18);
            for (int r = 0; r <= 8; r++) for (int col = 0; col <= 8; col++)
            {
                if (!SameStencil(iy * 8 + r, ix * 8 + col)) continue;
                int vertex = r * 17 + col;
                Buffer.BlockCopy(sc, smcnr + 8 + vertex * 3, c, mcnr + 8 + vertex * 3, 3);
                if (r < 8 && col < 8)
                {
                    vertex = r * 17 + 9 + col;
                    Buffer.BlockCopy(sc, smcnr + 8 + vertex * 3, c, mcnr + 8 + vertex * 3, 3);
                }
            }
        }
    }

    /// <summary>
    /// World normal from the height gradient. Grid rows run toward −X and columns toward −Y, so
    /// n ∝ (dh/dRow, dh/dCol, 1) = (worldX, worldY, up). MCNR stores exactly that order × 127 —
    /// measured on stock Northshire (WorldPackAdtTests.RecomputedNormals_MatchStockNormals): byte 2
    /// averages 0.85 (up), bytes 0/1 track the row/column slopes.
    /// </summary>
    private static void WriteNormal(byte[] c, int at, float dRow, float dCol)
    {
        var n = Vector3.Normalize(new Vector3(dRow, dCol, 1f));
        c[at + 0] = unchecked((byte)(sbyte)Math.Clamp(MathF.Round(n.X * 127f), -127, 127));
        c[at + 1] = unchecked((byte)(sbyte)Math.Clamp(MathF.Round(n.Y * 127f), -127, 127));
        c[at + 2] = unchecked((byte)(sbyte)Math.Clamp(MathF.Round(n.Z * 127f), -127, 127));
    }

    /// <summary>
    /// Liquid surface of one MCNK (vanilla MCLQ), or null when the chunk has none. The MCNK flags carry
    /// the liquid kind (0x04 river, 0x08 ocean, 0x10 magma, 0x20 slime); the MCLQ data starts with the
    /// (min, max) height pair, and the max is the surface of the flat water a builder cares about.
    /// </summary>
    public float? LiquidLevel(int mcinIndex)
    {
        byte[] c = _mcnk[mcinIndex];
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(8));
        int ofs = I32(c, 8 + 0x60);
        if ((flags & 0x3C) == 0 || ofs <= 0 || ofs + 16 > c.Length) return null;
        // The MCLQ sub-header is present in vanilla files (its size field is often 0).
        int data = c[ofs] == 'Q' && c[ofs + 1] == 'L' && c[ofs + 2] == 'C' && c[ofs + 3] == 'M' ? ofs + 8 : ofs;
        float max = F32(c, data + 4);
        return float.IsFinite(max) && MathF.Abs(max) < 5000f ? max : null;
    }

    /// <summary>Vanilla low-resolution terrain holes of one MCNK (header 0x3C, 16 bits = 4x4 sub-squares).
    /// A hole is only correct where a WMO fills it (a cave mouth, a castle courtyard).</summary>
    public ushort HolesOf(int mcinIndex) => BinaryPrimitives.ReadUInt16LittleEndian(_mcnk[mcinIndex].AsSpan(8 + 0x3C));

    /// <summary>Close the holes of one MCNK (a dropped WMO no longer fills them).</summary>
    public void ClearHoles(int mcinIndex) => BinaryPrimitives.WriteUInt16LittleEndian(_mcnk[mcinIndex].AsSpan(8 + 0x3C), 0);

    /// <summary>
    /// Close the ONE hole square (a quarter chunk, 4.17 yd) containing world (x, y): terrain is drawn and
    /// collides there again. For stock holes the building does not fill - a mine or cave mouth whose WMO
    /// leaves a corner of the square open lets a walking player step off into the void. Returns false
    /// when that square is not a hole (or the point is off this tile). Bit (hy * 4 + hx) as G12 reads it.
    /// </summary>
    public bool HealHoleAt(float x, float y)
    {
        const float quarter = WorldCoords.Chunk / 4f;
        for (int i = 0; i < 256; i++)
        {
            var (_, _, ox, oy, _) = ChunkInfo(i);
            if (x > ox || x <= ox - WorldCoords.Chunk || y > oy || y <= oy - WorldCoords.Chunk) continue;
            int hy = Math.Clamp((int)((ox - x) / quarter), 0, 3), hx = Math.Clamp((int)((oy - y) / quarter), 0, 3);
            ushort mask = HolesOf(i), bit = (ushort)(1 << (hy * 4 + hx));
            if ((mask & bit) == 0) return false;
            BinaryPrimitives.WriteUInt16LittleEndian(_mcnk[i].AsSpan(8 + 0x3C), (ushort)(mask & ~bit));
            return true;
        }
        return false;
    }

    /// <summary>Test hook: the MCNR bytes of one chunk (by MCIN index).</summary>
    public byte[] NormalsOf(int mcinIndex)
    {
        byte[] c = _mcnk[mcinIndex];
        int mcnr = I32(c, 8 + 0x18);
        return c.AsSpan(mcnr + 8, 145 * 3).ToArray();
    }

    /// <summary>Test hook: force a normal recompute of one chunk without changing heights.</summary>
    public void MarkNormalsDirty(int mcinIndex) { _normalsDirty[mcinIndex] = true; _recomputeAll = true; }

    /// <summary>Does the normal at outer (or inner, <paramref name="inner"/>) vertex (gr, gc) depend on a moved height?</summary>
    private bool NormalStale(int gr, int gc, bool inner)
    {
        if (_recomputeAll) return true;
        int r0 = Math.Max(gr - 1, 0), r1 = Math.Min(gr + (inner ? 2 : 1), 128);
        int c0 = Math.Max(gc - 1, 0), c1 = Math.Min(gc + (inner ? 2 : 1), 128);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (_heightMoved[r * 129 + c]) return true;
        return false;
    }

    /// <summary>
    /// Every absolute outer-vertex height of the tile at once: index gridRow * 129 + gridCol, gridRow 0 =
    /// the north edge (largest world X), gridCol 0 = the west edge (largest world Y) - the sculpt grid.
    /// </summary>
    public float[] OuterHeights()
    {
        var h = new float[129 * 129];
        foreach (var c in _mcnk)
        {
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08), mcvt = I32(c, 8 + 0x14);
            if (mcvt <= 0 || mcvt + 8 + 145 * 4 > c.Length) continue;
            float baseZ = F32(c, 8 + 0x70);
            for (int r = 0; r <= 8; r++)
                for (int col = 0; col <= 8; col++)
                    h[(iy * 8 + r) * 129 + ix * 8 + col] = baseZ + F32(c, mcvt + 8 + (r * 17 + col) * 4);
        }
        return h;
    }

    /// <summary>
    /// A stamp REPLACES a stock tile on a continent, and its source had no sea where this one stands: where the
    /// new terrain dips below the stock tile's water (a stitched coast, a land bridge's flank), the chunk takes
    /// the stock chunk's liquid (its whole MCLQ block + liquid flags) - otherwise the seabed shows dry through a
    /// hole in the ocean. Chunks that already carry liquid (a source lake) keep their own. Returns chunks filled.
    /// </summary>
    public int CarryLiquidFrom(AdtDocument stock, bool replaceExisting = false)
    {
        int filled = 0;
        for (int i = 0; i < 256; i++)
        {
            byte[] c = _mcnk[i];
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(8));
            if (!replaceExisting && (flags & 0x3C) != 0) continue;
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            int si = stock.ChunkIndex(ix, iy);
            if (si < 0 || stock.LiquidLevel(si) is not float level) continue;
            if (!replaceExisting && ChunkMinHeight(c) >= level - 0.05f) continue;
            byte[] sc = stock._mcnk[si];
            int sOfs = I32(sc, 8 + 0x60), sSize = I32(sc, 8 + 0x64);
            if (sOfs <= 0 || sSize < 16 || sOfs + sSize > sc.Length) continue;
            int ofs = I32(c, 8 + 0x60), size = I32(c, 8 + 0x64);
            if (ofs <= 0 || size < 8 || ofs + size > c.Length) continue;
            // Replace this chunk's (empty) MCLQ block with the stock one; everything after it (MCSE) moves.
            var grown = new byte[c.Length - size + sSize];
            Buffer.BlockCopy(c, 0, grown, 0, ofs);
            Buffer.BlockCopy(sc, sOfs, grown, ofs, sSize);
            Buffer.BlockCopy(c, ofs + size, grown, ofs + sSize, c.Length - ofs - size);
            int shift = sSize - size;
            WI32(grown, 4, grown.Length - 8);                             // MCNK chunk size
            WI32(grown, 8 + 0x64, sSize);                                 // sizeLiquid
            int snd = I32(grown, 8 + 0x58);
            if (snd > ofs) WI32(grown, 8 + 0x58, snd + shift);            // ofsSndEmitters follows the block
            uint sflags = BinaryPrimitives.ReadUInt32LittleEndian(sc.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(8), (flags & ~0x3Cu) | (sflags & 0x3C));
            _mcnk[i] = grown;
            filled++;
        }
        return filled;
    }

    public readonly record struct LiquidCell(int Row, int Col, byte Flags, float H00, float H01, float H10, float H11);

    /// <summary>Active vanilla MCLQ cells, including their four water heights. Uses the client's
    /// visible-cell rule; stale MCNK liquid bits alone cannot hide a retained liquid payload.</summary>
    public IEnumerable<LiquidCell> LiquidCells()
    {
        foreach (byte[] c in _mcnk)
        {
            int ofs = I32(c, 8 + 0x60), size = I32(c, 8 + 0x64);
            if (ofs <= 0 || size < 808 || ofs + size > c.Length) continue;
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            for (int layer = 0; layer < (size - 8) / 800; layer++)
            {
                int data = ofs + 8 + layer * 800;
                for (int r = 0; r < 8; r++) for (int col = 0; col < 8; col++)
                {
                    byte flags = c[data + 656 + r * 8 + col];
                    if ((flags & 0x0F) == 0x0F) continue;
                    int vertex = data + 12 + (r * 9 + col) * 8;
                    yield return new(iy * 8 + r, ix * 8 + col, flags, F32(c, vertex), F32(c, vertex + 8),
                        F32(c, vertex + 72), F32(c, vertex + 80));
                }
            }
        }
    }

    /// <summary>Remove selected liquid cells while preserving other cells, including a pond sharing
    /// the same MCNK. Entirely empty chunks get an empty MCLQ payload, so renderers and extractors agree.</summary>
    public int RemoveLiquidCells(Func<int, int, bool> remove)
    {
        int removed = 0;
        for (int i = 0; i < 256; i++)
        {
            byte[] c = _mcnk[i];
            int ofs = I32(c, 8 + 0x60), size = I32(c, 8 + 0x64);
            if (ofs <= 0 || size < 808 || ofs + size > c.Length) continue;
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08), kept = 0, changed = 0;
            for (int layer = 0; layer < (size - 8) / 800; layer++)
            {
                int data = ofs + 8 + layer * 800;
                for (int r = 0; r < 8; r++) for (int col = 0; col < 8; col++)
                {
                    int at = data + 656 + r * 8 + col;
                    if ((c[at] & 0x0F) == 0x0F) continue;
                    if (remove(iy * 8 + r, ix * 8 + col)) { c[at] |= 0x0F; changed++; }
                    else kept++;
                }
            }
            removed += changed;
            if (changed == 0 || kept != 0) continue;
            int shift = 8 - size;
            var shrunk = new byte[c.Length + shift];
            Buffer.BlockCopy(c, 0, shrunk, 0, ofs + 8);
            Buffer.BlockCopy(c, ofs + size, shrunk, ofs + 8, c.Length - ofs - size);
            WI32(shrunk, ofs + 4, 0);
            WI32(shrunk, 4, shrunk.Length - 8);
            WI32(shrunk, 8 + 0x64, 8);
            int snd = I32(shrunk, 8 + 0x58);
            if (snd > ofs) WI32(shrunk, 8 + 0x58, snd + shift);
            WU32(shrunk, 8, BinaryPrimitives.ReadUInt32LittleEndian(shrunk.AsSpan(8)) & ~0x3Cu);
            _mcnk[i] = shrunk;
        }
        return removed;
    }

    private static float ChunkMinHeight(byte[] c)
    {
        int mcvt = I32(c, 8 + 0x14);
        if (mcvt <= 0 || mcvt + 8 + 145 * 4 > c.Length) return float.MaxValue;
        float baseZ = F32(c, 8 + 0x70), min = float.MaxValue;
        for (int k = 0; k < 145; k++) min = MathF.Min(min, F32(c, mcvt + 8 + k * 4));
        return baseZ + min;
    }

    /// <summary>Absolute height of an outer vertex (grid 0..128) — for tests and diagnostics.</summary>
    public float OuterHeight(int gridRow, int gridCol)
    {
        int iy = Math.Min(gridRow / 8, 15), ix = Math.Min(gridCol / 8, 15);
        int r = gridRow - iy * 8, col = gridCol - ix * 8;
        foreach (var c in _mcnk)
        {
            if (I32(c, 8 + 0x04) != ix || I32(c, 8 + 0x08) != iy) continue;
            return F32(c, 8 + 0x70) + F32(c, I32(c, 8 + 0x14) + 8 + (r * 17 + col) * 4);
        }
        return 0f;
    }

    /// <summary>Absolute height of a cell's center vertex (grid 0..127), for surface audits.</summary>
    public float InnerHeight(int gridRow, int gridCol)
    {
        int iy = gridRow / 8, ix = gridCol / 8;
        int i = ChunkIndex(ix, iy);
        if (i < 0) return 0f;
        byte[] c = _mcnk[i];
        int vertex = (gridRow % 8) * 17 + 9 + gridCol % 8;
        return F32(c, 8 + 0x70) + F32(c, I32(c, 8 + 0x14) + 8 + vertex * 4);
    }

    // ═══════════════════════════════════════════════════════════════ stamping (new maps)

    /// <summary>
    /// Move this tile to another grid cell (a World Pack map "stamps" stock tiles into new maps).
    /// MCNK origins follow the tile; sound emitters are dropped (their positions are absolute and
    /// they belong to the source zone). With <paramref name="keepObjects"/> the MDDF/MODF entries
    /// shift with the tile and get fresh uniqueIds from <paramref name="remapUid"/> (the same source
    /// object stamped with the same offset must map to the same id, so a building spanning two
    /// stamped tiles stays ONE spawn); otherwise every placement and MCRF reference is removed.
    /// </summary>
    public void Relocate(int newCol, int newRow, bool keepObjects, Func<uint, uint> remapUid)
        => Relocate(newCol, newRow, keepObjects, keepObjects, remapUid);

    /// <summary>As above with doodads (trees, rocks, props) and WMOs (buildings) chosen separately —
    /// stamping a forest usually wants the trees without the source zone's villages.</summary>
    public void Relocate(int newCol, int newRow, bool keepDoodads, bool keepWmos, Func<uint, uint> remapUid)
    {
        float dx = (newCol - Col) * WorldCoords.Tile;   // placement X follows the column
        float dz = (newRow - Row) * WorldCoords.Tile;   // placement Z follows the row
        Col = newCol;
        Row = newRow;
        for (int i = 0; i < 256; i++)
        {
            byte[] c = _mcnk[i];
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            BinaryPrimitives.WriteSingleLittleEndian(c.AsSpan(8 + 0x68), (32 - newRow) * WorldCoords.Tile - iy * WorldCoords.Chunk);
            BinaryPrimitives.WriteSingleLittleEndian(c.AsSpan(8 + 0x6C), (32 - newCol) * WorldCoords.Tile - ix * WorldCoords.Chunk);
            WI32(c, 8 + 0x5C, 0);   // nSndEmitters
            if (!keepDoodads || !keepWmos) ClearRefs(i, doodads: !keepDoodads, wmos: !keepWmos);
        }
        if (!keepDoodads) { _mddf.Clear(); _doodadNames.Clear(); }
        if (!keepWmos) { _modf.Clear(); _wmoNames.Clear(); }
        foreach (var e in _mddf)
        {
            WU32(e, 4, remapUid(BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4))));
            Shift(e, 8, dx, dz);
        }
        foreach (var e in _modf)
        {
            WU32(e, 4, remapUid(BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4))));
            Shift(e, 8, dx, dz);
            Shift(e, 32, dx, dz);
            Shift(e, 44, dx, dz);
        }
    }

    private static void Shift(byte[] e, int at, float dx, float dz)
    {
        BinaryPrimitives.WriteSingleLittleEndian(e.AsSpan(at), BinaryPrimitives.ReadSingleLittleEndian(e.AsSpan(at)) + dx);
        BinaryPrimitives.WriteSingleLittleEndian(e.AsSpan(at + 8), BinaryPrimitives.ReadSingleLittleEndian(e.AsSpan(at + 8)) + dz);
    }

    /// <summary>
    /// Drop the MODF entries whose model path matches <paramref name="drop"/> (e.g. keep a stamped
    /// village but not the famous castle beside it). MWMO/MWID are rebuilt and every MCNK's map-object
    /// references are remapped. Returns how many placements were removed.
    /// </summary>
    public int DropWmos(Func<string, bool> drop) => DropWmos((path, _) => drop(path));

    public int DropWmos(Func<string, Vector3, bool> drop)
    {
        // Nothing to drop → touch nothing. (The renumbering below rewrites every MODF name index; an
        // early return after it once left the old name table behind and swapped buildings' models.)
        if (!_modf.Any(e =>
            {
                int id = (int)BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0));
                return drop(id < _wmoNames.Count ? _wmoNames[id] : "", new Vector3(F32(e, 8), F32(e, 12), F32(e, 16)));
            }))
            return 0;
        var keepEntry = new bool[_modf.Count];
        var oldToNew = new int[_modf.Count];
        var names = new List<string>();
        var newModf = new List<byte[]>();
        for (int i = 0; i < _modf.Count; i++)
        {
            int nameId = (int)BinaryPrimitives.ReadUInt32LittleEndian(_modf[i].AsSpan(0));
            string path = nameId < _wmoNames.Count ? _wmoNames[nameId] : "";
            if (drop(path, new Vector3(F32(_modf[i], 8), F32(_modf[i], 12), F32(_modf[i], 16)))) { oldToNew[i] = -1; continue; }
            keepEntry[i] = true;
            oldToNew[i] = newModf.Count;
            int newName = names.FindIndex(n => n.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (newName < 0) { names.Add(path); newName = names.Count - 1; }
            WU32(_modf[i], 0, (uint)newName);
            newModf.Add(_modf[i]);
        }
        int removed = _modf.Count - newModf.Count;
        if (removed == 0) return 0;
        for (int c = 0; c < 256; c++)
        {
            byte[] m = _mcnk[c];
            int ofsRefs = I32(m, 8 + 0x20), nDoodad = I32(m, 8 + 0x10), nObj = I32(m, 8 + 0x38);
            if (ofsRefs <= 0 || nObj == 0) continue;
            int objStart = ofsRefs + 8 + nDoodad * 4;
            var kept = new List<int>();
            for (int k = 0; k < nObj; k++)
            {
                int old = I32(m, objStart + k * 4);
                if (old >= 0 && old < oldToNew.Length && oldToNew[old] >= 0) kept.Add(oldToNew[old]);
            }
            int bytes = (nObj - kept.Count) * 4;
            var d = new byte[m.Length - bytes];
            Buffer.BlockCopy(m, 0, d, 0, objStart);
            for (int k = 0; k < kept.Count; k++) WI32(d, objStart + k * 4, kept[k]);
            Buffer.BlockCopy(m, objStart + nObj * 4, d, objStart + kept.Count * 4, m.Length - objStart - nObj * 4);
            if (bytes > 0)
            {
                WI32(d, 4, I32(d, 4) - bytes);
                WI32(d, ofsRefs + 4, (nDoodad + kept.Count) * 4);
                WI32(d, 8 + 0x38, kept.Count);
                foreach (int field in new[] { 0x14, 0x18, 0x1C, 0x24, 0x2C, 0x58, 0x60, 0x74 })
                {
                    int v = I32(d, 8 + field);
                    if (v > ofsRefs) WI32(d, 8 + field, v - bytes);
                }
            }
            _mcnk[c] = d;
        }
        _modf.Clear(); _modf.AddRange(newModf);
        _wmoNames.Clear(); _wmoNames.AddRange(names);
        return removed;
    }

    /// <summary>
    /// Drop the MDDF entries (props) that <paramref name="drop"/> selects by model path and
    /// placement-space position — e.g. the fences and lamp posts that stood around a dropped WMO.
    /// MMDX/MMID are rebuilt and every MCNK's doodad references remapped. Returns how many were removed.
    /// </summary>
    public int DropDoodads(Func<string, Vector3, bool> drop)
    {
        string PathOf(byte[] e) { int id = (int)BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0)); return id < _doodadNames.Count ? _doodadNames[id] : ""; }
        Vector3 PosOf(byte[] e) => new(F32(e, 8), F32(e, 12), F32(e, 16));
        if (!_mddf.Any(e => drop(PathOf(e), PosOf(e)))) return 0;
        var oldToNew = new int[_mddf.Count];
        var names = new List<string>();
        var kept = new List<byte[]>();
        for (int i = 0; i < _mddf.Count; i++)
        {
            string path = PathOf(_mddf[i]);
            if (drop(path, PosOf(_mddf[i]))) { oldToNew[i] = -1; continue; }
            oldToNew[i] = kept.Count;
            int n = names.FindIndex(x => x.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (n < 0) { names.Add(path); n = names.Count - 1; }
            var e = (byte[])_mddf[i].Clone();
            WU32(e, 0, (uint)n);
            kept.Add(e);
        }
        int removed = _mddf.Count - kept.Count;
        for (int c = 0; c < 256; c++)
        {
            byte[] m = _mcnk[c];
            int ofsRefs = I32(m, 8 + 0x20), nDoodad = I32(m, 8 + 0x10), nObj = I32(m, 8 + 0x38);
            if (ofsRefs <= 0 || nDoodad == 0) continue;
            int start = ofsRefs + 8;
            var refs = new List<int>();
            for (int k = 0; k < nDoodad; k++)
            {
                int old = I32(m, start + k * 4);
                if (old >= 0 && old < oldToNew.Length && oldToNew[old] >= 0) refs.Add(oldToNew[old]);
            }
            int bytes = (nDoodad - refs.Count) * 4;
            var d = new byte[m.Length - bytes];
            Buffer.BlockCopy(m, 0, d, 0, start);
            for (int k = 0; k < refs.Count; k++) WI32(d, start + k * 4, refs[k]);
            Buffer.BlockCopy(m, start + nDoodad * 4, d, start + refs.Count * 4, m.Length - start - nDoodad * 4);
            if (bytes > 0)
            {
                WI32(d, 4, I32(d, 4) - bytes);
                WI32(d, ofsRefs + 4, (refs.Count + nObj) * 4);
                WI32(d, 8 + 0x10, refs.Count);
                foreach (int field in new[] { 0x14, 0x18, 0x1C, 0x24, 0x2C, 0x58, 0x60, 0x74 })
                {
                    int v = I32(d, 8 + field);
                    if (v > ofsRefs) WI32(d, 8 + field, v - bytes);
                }
            }
            _mcnk[c] = d;
        }
        _mddf.Clear(); _mddf.AddRange(kept);
        _doodadNames.Clear(); _doodadNames.AddRange(names);
        return removed;
    }

    /// <summary>Test hook: MCNK (index x, index y) and its stored origin (0x68, 0x6C) and area id.</summary>
    public (int ix, int iy, float ox, float oy, uint area) ChunkInfo(int mcinIndex)
    {
        byte[] c = _mcnk[mcinIndex];
        return (I32(c, 8 + 0x04), I32(c, 8 + 0x08), F32(c, 8 + 0x68), F32(c, 8 + 0x6C),
            BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(8 + 0x34)));
    }

    /// <summary>Stamp every MCNK with one AreaTable id (the new zone).</summary>
    public void SetAreaId(uint areaId)
    {
        foreach (var c in _mcnk) WU32(c, 8 + 0x34, areaId);
    }

    /// <summary>Re-tag every chunk of one area with another (land a pack raised out of the sea still said
    /// "The Great Sea" on the minimap). Returns how many chunks changed.</summary>
    public int ReplaceArea(uint from, uint to)
    {
        int n = 0;
        foreach (var c in _mcnk)
            if (BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(8 + 0x34)) == from) { WU32(c, 8 + 0x34, to); n++; }
        return n;
    }

    /// <summary>Set the area of the chunks whose centre lies within <paramref name="radius"/> of a
    /// world point (subzone painting).</summary>
    public int PaintArea(uint areaId, float worldX, float worldY, float radius)
    {
        int n = 0;
        foreach (var c in _mcnk)
        {
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            float cx = (32 - Row) * WorldCoords.Tile - (iy + 0.5f) * WorldCoords.Chunk;
            float cy = (32 - Col) * WorldCoords.Tile - (ix + 0.5f) * WorldCoords.Chunk;
            if ((cx - worldX) * (cx - worldX) + (cy - worldY) * (cy - worldY) > radius * radius) continue;
            WU32(c, 8 + 0x34, areaId);
            n++;
        }
        return n;
    }

    /// <summary>Remove the doodad and/or map-object references of one MCNK (MCRF holds doodad refs
    /// first, then map-object refs).</summary>
    private void ClearRefs(int mcinIndex, bool doodads, bool wmos)
    {
        byte[] c = _mcnk[mcinIndex];
        int ofsRefs = I32(c, 8 + 0x20);
        int nDoodad = I32(c, 8 + 0x10), nObj = I32(c, 8 + 0x38);
        if (ofsRefs <= 0) return;
        int dropFrom, dropCount;
        if (doodads && wmos) { dropFrom = 0; dropCount = nDoodad + nObj; }
        else if (doodads) { dropFrom = 0; dropCount = nDoodad; }
        else { dropFrom = nDoodad; dropCount = nObj; }
        if (dropCount == 0) return;
        int from = ofsRefs + 8 + dropFrom * 4, bytes = dropCount * 4;
        var d = new byte[c.Length - bytes];
        Buffer.BlockCopy(c, 0, d, 0, from);
        Buffer.BlockCopy(c, from + bytes, d, from, c.Length - from - bytes);
        WI32(d, 4, I32(d, 4) - bytes);
        WI32(d, ofsRefs + 4, (nDoodad + nObj - dropCount) * 4);
        if (doodads) WI32(d, 8 + 0x10, 0);
        if (wmos) WI32(d, 8 + 0x38, 0);
        foreach (int field in new[] { 0x14, 0x18, 0x1C, 0x24, 0x2C, 0x58, 0x60, 0x74 })
        {
            int v = I32(d, 8 + field);
            if (v > ofsRefs) WI32(d, 8 + field, v - bytes);
        }
        _mcnk[mcinIndex] = d;
    }

    /// <summary>WDT for a terrain map whose ADTs exist at <paramref name="tiles"/>.</summary>
    public static byte[] BuildWdt(IEnumerable<(int col, int row)> tiles)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        WriteChunk(w, "MVER", BitConverter.GetBytes(18));
        WriteChunk(w, "MPHD", new byte[32]);
        var main = new byte[64 * 64 * 8];
        foreach (var (col, row) in tiles)
            if (col is >= 0 and < 64 && row is >= 0 and < 64)
                WI32(main, (row * 64 + col) * 8, 1);
        WriteChunk(w, "MAIN", main);
        WriteChunk(w, "MWMO", Array.Empty<byte>());
        return ms.ToArray();
    }

    // ═══════════════════════════════════════════════════════════════ placements

    /// <summary>
    /// Append a MODF entry. <paramref name="placementPos"/> and <paramref name="rotDeg"/> are MODF
    /// fields verbatim; <paramref name="extentMin"/>/<paramref name="extentMax"/> are the placement-
    /// space AABB (used for MCRF overlap and written as the MODF extents).
    /// </summary>
    public void AddWmo(string path, uint uniqueId, Vector3 placementPos, Vector3 rotDeg,
        Vector3 extentMin, Vector3 extentMax, ushort doodadSet)
    {
        int nameIndex = NameIndex(_wmoNames, path);
        var e = new byte[64];
        WU32(e, 0, (uint)nameIndex);
        WU32(e, 4, uniqueId);
        WVec(e, 8, placementPos);
        WVec(e, 20, rotDeg);
        WVec(e, 32, extentMin);
        WVec(e, 44, extentMax);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(56), 0);         // flags
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(58), doodadSet);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(60), 0);         // nameSet
        int modfIndex = _modf.Count;
        _modf.Add(e);
        foreach (int i in OverlappedChunks(extentMin, extentMax))
            AddRef(i, doodad: false, modfIndex);
    }

    /// <summary>Append an MDDF entry; <paramref name="radius"/> (already scaled) drives MCRF overlap.</summary>
    public void AddDoodad(string path, uint uniqueId, Vector3 placementPos, Vector3 rotDeg, float scale, float radius)
    {
        int nameIndex = NameIndex(_doodadNames, path);
        var e = new byte[36];
        WU32(e, 0, (uint)nameIndex);
        WU32(e, 4, uniqueId);
        WVec(e, 8, placementPos);
        WVec(e, 20, rotDeg);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(32), (ushort)Math.Clamp(MathF.Round(scale * 1024f), 1, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(34), 0);
        int mddfIndex = _mddf.Count;
        _mddf.Add(e);
        var r = new Vector3(radius, radius, radius);
        var hits = OverlappedChunks(placementPos - r, placementPos + r);
        if (hits.Count == 0) hits.Add(ChunkIndexAt(placementPos));
        foreach (int i in hits)
            AddRef(i, doodad: true, mddfIndex);
    }

    private static int NameIndex(List<string> names, string path)
    {
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], path, StringComparison.OrdinalIgnoreCase)) return i;
        names.Add(path);
        return names.Count - 1;
    }

    /// <summary>MCNKs of THIS tile whose XZ placement-space square intersects the box.</summary>
    private List<int> OverlappedChunks(Vector3 min, Vector3 max)
    {
        var hits = new List<int>();
        float tileX0 = Col * WorldCoords.Tile;   // placement X grows with the file column
        float tileZ0 = Row * WorldCoords.Tile;   // placement Z grows with the file row
        for (int i = 0; i < 256; i++)
        {
            byte[] c = _mcnk[i];
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            float x0 = tileX0 + ix * WorldCoords.Chunk, z0 = tileZ0 + iy * WorldCoords.Chunk;
            if (max.X >= x0 && min.X <= x0 + WorldCoords.Chunk && max.Z >= z0 && min.Z <= z0 + WorldCoords.Chunk)
                hits.Add(i);
        }
        return hits;
    }

    private int ChunkIndexAt(Vector3 p)
    {
        int ix = Math.Clamp((int)((p.X - Col * WorldCoords.Tile) / WorldCoords.Chunk), 0, 15);
        int iy = Math.Clamp((int)((p.Z - Row * WorldCoords.Tile) / WorldCoords.Chunk), 0, 15);
        for (int i = 0; i < 256; i++)
            if (I32(_mcnk[i], 8 + 0x04) == ix && I32(_mcnk[i], 8 + 0x08) == iy) return i;
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════ texture paint

    /// <summary>
    /// Paint a ground texture into every chunk where <paramref name="amount"/>(worldX, worldY) &gt; 0, with the shared
    /// <see cref="TerrainPaint"/> rule (MSUIClient previews with the same code). <paramref name="chunkNear"/>(centreX,
    /// centreY) skips chunks that cannot be reached. Rewrites MCLY + MCAL (4-bit, 2048 bytes a layer, flag 0x100),
    /// nLayers and sizeAlpha, appends the texture to MTEX once, and shifts the MCNK offsets after each splice. Chunks the
    /// paint does not reach stay byte-identical. Returns how many chunks changed.
    /// </summary>
    public int PaintTexture(string texture, Func<float, float, bool> chunkNear, Func<float, float, float> amount)
    {
        var names = System.Text.Encoding.ASCII.GetString(_mtex).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        int texIndex = names.FindIndex(n => string.Equals(n, texture, StringComparison.OrdinalIgnoreCase));
        bool appended = texIndex < 0;
        if (appended) texIndex = names.Count;
        int changed = 0;
        for (int i = 0; i < 256; i++)
        {
            byte[] c = _mcnk[i];
            int ix = I32(c, 8 + 0x04), iy = I32(c, 8 + 0x08);
            var centre = TerrainPaint.PixelWorld(Col, Row, ix, iy, 31, 31);
            if (!chunkNear(centre.X, centre.Y)) continue;
            int n = I32(c, 8 + 0x0C), ofsLayer = I32(c, 8 + 0x1C), ofsAlpha = I32(c, 8 + 0x24);
            if (ofsLayer <= 0 || ofsAlpha <= 0 || ofsLayer + 8 + n * 16 > c.Length) continue;

            var textures = new List<int>();
            var alphas = new List<byte[]?>();
            var old = new Dictionary<int, (uint flags, int effect)>();
            for (int k = 0; k < n; k++)
            {
                int e = ofsLayer + 8 + k * 16;
                int tex = I32(c, e);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(e + 4));
                textures.Add(tex);
                old.TryAdd(tex, (flags, I32(c, e + 12)));
                byte[]? alpha = null;
                int at = ofsAlpha + 8 + I32(c, e + 8);
                if (k > 0)
                {
                    alpha = new byte[TerrainPaint.Pixels];
                    if ((flags & 0x100) != 0 && at + 2048 <= c.Length)
                        for (int p = 0; p < 2048; p++)
                        {
                            byte b = c[at + p];
                            alpha[p * 2] = (byte)((b & 0x0F) * 17);
                            alpha[p * 2 + 1] = (byte)((b >> 4) * 17);
                        }
                }
                alphas.Add(alpha);
            }
            if (!TerrainPaint.PaintChunk(textures, alphas, texIndex, (r, col) =>
                {
                    var w = TerrainPaint.PixelWorld(Col, Row, ix, iy, r, col);
                    return amount(w.X, w.Y);
                })) continue;

            var mcly = new byte[textures.Count * 16];
            var mcal = new byte[(textures.Count - 1) * 2048];
            for (int k = 0; k < textures.Count; k++)
            {
                var (flags, effect) = old.TryGetValue(textures[k], out var o) ? o : (0u, 0);
                flags &= ~0x300u;
                if (k > 0) flags |= 0x100u;
                WI32(mcly, k * 16, textures[k]);
                WU32(mcly, k * 16 + 4, flags);
                WI32(mcly, k * 16 + 8, k == 0 ? 0 : (k - 1) * 2048);
                WI32(mcly, k * 16 + 12, effect);
                if (k == 0) continue;
                var a = alphas[k]!;
                for (int p = 0; p < 2048; p++)
                    mcal[(k - 1) * 2048 + p] = (byte)((a[p * 2] / 17) | ((a[p * 2 + 1] / 17) << 4));
            }
            c = ReplaceSubchunk(c, 0x1C, mcly);
            c = ReplaceSubchunk(c, 0x24, mcal);
            WI32(c, 8 + 0x0C, textures.Count);
            WI32(c, 8 + 0x28, mcal.Length + 8);               // sizeAlpha includes the MCAL header (stock files agree)
            _mcnk[i] = c;
            changed++;
        }
        if (appended && changed > 0)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(texture + "\0");
            int keep = _mtex.Length;
            while (keep > 0 && _mtex[keep - 1] == 0) keep--;   // exactly one terminator between names
            var grown = new byte[(keep > 0 ? keep + 1 : 0) + bytes.Length];
            Buffer.BlockCopy(_mtex, 0, grown, 0, keep);
            Buffer.BlockCopy(bytes, 0, grown, keep > 0 ? keep + 1 : 0, bytes.Length);
            _mtex = grown;
        }
        return changed;
    }

    /// <summary>Replace the payload of the sub-chunk whose offset is in MCNK header field <paramref name="field"/>; fix its
    /// IFF size, the MCNK IFF size and every header offset that lies after it.</summary>
    private static byte[] ReplaceSubchunk(byte[] c, int field, byte[] payload)
    {
        int ofs = I32(c, 8 + field);
        int oldSize = I32(c, ofs + 4);
        int delta = payload.Length - oldSize;
        var n = new byte[c.Length + delta];
        Buffer.BlockCopy(c, 0, n, 0, ofs + 8);
        Buffer.BlockCopy(payload, 0, n, ofs + 8, payload.Length);
        Buffer.BlockCopy(c, ofs + 8 + oldSize, n, ofs + 8 + payload.Length, c.Length - (ofs + 8 + oldSize));
        WI32(n, ofs + 4, payload.Length);
        WI32(n, 4, I32(n, 4) + delta);
        if (delta != 0)
            foreach (int f in new[] { 0x14, 0x18, 0x1C, 0x20, 0x24, 0x2C, 0x58, 0x60, 0x74 })
            {
                int v = I32(n, 8 + f);
                if (v > ofs) WI32(n, 8 + f, v + delta);
            }
        return n;
    }

    /// <summary>Insert one MCRF reference (doodads before map objects) and fix every offset after it.</summary>
    private void AddRef(int mcinIndex, bool doodad, int index)
    {
        byte[] c = _mcnk[mcinIndex];
        int ofsRefs = I32(c, 8 + 0x20);
        int nDoodad = I32(c, 8 + 0x10);
        int nObj = I32(c, 8 + 0x38);
        if (ofsRefs <= 0 || ofsRefs + 8 > c.Length) return;

        int insertAt = ofsRefs + 8 + (doodad ? nDoodad : nDoodad + nObj) * 4;
        var n = new byte[c.Length + 4];
        Buffer.BlockCopy(c, 0, n, 0, insertAt);
        WI32(n, insertAt, index);
        Buffer.BlockCopy(c, insertAt, n, insertAt + 4, c.Length - insertAt);

        WI32(n, 4, I32(n, 4) + 4);                                  // MCNK IFF size
        WI32(n, ofsRefs + 4, (nDoodad + nObj + 1) * 4);             // MCRF IFF size
        if (doodad) WI32(n, 8 + 0x10, nDoodad + 1); else WI32(n, 8 + 0x38, nObj + 1);
        foreach (int field in new[] { 0x14, 0x18, 0x1C, 0x24, 0x2C, 0x58, 0x60, 0x74 })
        {
            int v = I32(n, 8 + field);
            if (v > ofsRefs) WI32(n, 8 + field, v + 4);
        }
        _mcnk[mcinIndex] = n;
    }

    // ═══════════════════════════════════════════════════════════════ helpers

    private static void Expect(byte[] d, int at, string magic)
    {
        if (at < 0 || at + 8 > d.Length ||
            d[at] != magic[3] || d[at + 1] != magic[2] || d[at + 2] != magic[1] || d[at + 3] != magic[0])
            throw new InvalidDataException($"ADT: expected {magic} at 0x{at:X}");
    }

    private static int I32(byte[] d, int at) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
    private static float F32(byte[] d, int at) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at));
    private static void WI32(byte[] d, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at), v);
    private static void WU32(byte[] d, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), v);
    private static void AddF32(byte[] d, int at, float v) =>
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(at), F32(d, at) + v);
    private static void WVec(byte[] d, int at, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(at), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(at + 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(at + 8), v.Z);
    }
}
