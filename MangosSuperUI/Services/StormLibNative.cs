using System.Runtime.InteropServices;

namespace MangosSuperUI.Services;

/// <summary>
/// P/Invoke layer for Ladislav Zezula's StormLib (https://github.com/ladislav-zezula/StormLib).
///
/// === Why this exists ===
/// War3Net.IO.Mpq (the previous backing library) cannot read entries from
/// vanilla WoW 1.12 patch.MPQ — it returns a non-null MpqStream whose
/// .Read() throws NotSupportedException on the very first byte. The cause
/// is War3Net's lack of support for certain compression-flag combinations
/// (likely PKWARE Implode + ADPCM / Huffman variants) that Warcraft 3 MPQs
/// don't use but vanilla WoW 1.12 patch.MPQ does. See SESSION_I_HANDOFF.md
/// for the full diagnostic trail.
///
/// StormLib is the canonical reference implementation of the MPQ format,
/// used internally by every reliable WoW asset tool (wow.export, WMV, MPQ
/// Editor, Ladik's MPQ Editor). It handles every compression combination
/// vanilla 1.12 uses.
///
/// === Native library location ===
/// The .so / .dll lives at runtimes/{rid}/native/libstorm.so (or .dll).
/// .NET 8 auto-discovers DllImports from this convention — no LD_LIBRARY_PATH
/// or environment fiddling required. The build copies the right one to the
/// output directory based on the runtime identifier.
///
/// === Why this is a separate file from MpqReaderService ===
/// Native interop boundaries are noisy and accident-prone. Keeping the raw
/// P/Invokes here means MpqReaderService stays readable as ordinary C#.
/// Anything weird (calling conventions, marshalling, error mapping)
/// stays quarantined in this file.
///
/// === API subset ===
/// StormLib exposes ~60 functions. We use 7:
///   - SFileOpenArchive       — open .mpq, get archive handle
///   - SFileCloseArchive      — release archive handle
///   - SFileHasFile           — hash-table existence check (probe, no allocation)
///   - SFileOpenFileEx        — open a file inside an archive, get file handle
///   - SFileGetFileSize       — uncompressed size (for buffer allocation)
///   - SFileReadFile          — read N bytes into a buffer
///   - SFileCloseFile         — release file handle
///
/// (We don't need SFileFindFirstFile / SFileFindNextFile — listfile
/// iteration goes through reading the special "(listfile)" pseudo-entry,
/// same as we already do.)
/// </summary>
internal static class StormLibNative
{
    // The .NET runtime resolves "storm" → "libstorm.so" on Linux,
    // "storm.dll" on Windows, "libstorm.dylib" on macOS.
    // It searches runtimes/{rid}/native/ first, then the output directory,
    // then the OS library search path.
    private const string LIB = "storm";

    // ── Open / Close Archive ───────────────────────────────────────────

    /// <summary>
    /// Open an MPQ archive from disk.
    /// </summary>
    /// <param name="szMpqName">Filesystem path to the .MPQ file.</param>
    /// <param name="dwPriority">Search priority. Ignored by StormLib; pass 0.</param>
    /// <param name="dwFlags">Open flags — see SFILE_OPEN_* constants. Pass 0 for default.</param>
    /// <param name="phMpq">Out: handle to the opened archive on success.</param>
    /// <returns>true on success, false on failure (Marshal.GetLastWin32Error for code).</returns>
    [DllImport(LIB, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SFileOpenArchive(
        [MarshalAs(UnmanagedType.LPStr)] string szMpqName,
        uint dwPriority,
        uint dwFlags,
        out IntPtr phMpq);

    [DllImport(LIB, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SFileCloseArchive(IntPtr hMpq);

    // ── File Existence / Open / Close ──────────────────────────────────

    /// <summary>
    /// Check whether a file exists in the archive (hash-table lookup, no I/O on the data).
    /// Cheap — use this for probe-only operations to avoid the allocation of
    /// SFileOpenFileEx + SFileCloseFile.
    /// </summary>
    [DllImport(LIB, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SFileHasFile(
        IntPtr hMpq,
        [MarshalAs(UnmanagedType.LPStr)] string szFileName);

    /// <summary>
    /// Open a file inside an archive for reading.
    /// </summary>
    /// <param name="dwSearchScope">SFILE_OPEN_FROM_MPQ (0) is what we always want.</param>
    [DllImport(LIB, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SFileOpenFileEx(
        IntPtr hMpq,
        [MarshalAs(UnmanagedType.LPStr)] string szFileName,
        uint dwSearchScope,
        out IntPtr phFile);

    [DllImport(LIB, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SFileCloseFile(IntPtr hFile);

    // ── Read ───────────────────────────────────────────────────────────

    /// <summary>
    /// Get the uncompressed size of an open file. Two return paths:
    ///   - Files smaller than 4 GB: function returns the size directly,
    ///     pFileSizeHigh receives 0.
    ///   - Files larger than 4 GB: function returns the low 32 bits,
    ///     pFileSizeHigh receives the high 32 bits.
    /// Body-atlas BLPs are all well under 4 GB so we pass IntPtr.Zero for the high.
    /// </summary>
    [DllImport(LIB, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern uint SFileGetFileSize(IntPtr hFile, IntPtr pFileSizeHigh);

    /// <summary>
    /// Read bytes from an open file handle.
    /// </summary>
    /// <param name="lpBuffer">Pointer to a buffer at least dwToRead bytes long.</param>
    /// <param name="dwToRead">Number of bytes to read.</param>
    /// <param name="pdwRead">Out: actual bytes read.</param>
    /// <param name="lpOverlapped">Async I/O — pass IntPtr.Zero for synchronous.</param>
    [DllImport(LIB, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SFileReadFile(
        IntPtr hFile,
        IntPtr lpBuffer,
        uint dwToRead,
        out uint pdwRead,
        IntPtr lpOverlapped);

    // ── Open flags ─────────────────────────────────────────────────────

    /// <summary>Pass to SFileOpenArchive's dwFlags: read-only mode. Don't
    /// memory-map (better with our many-archive workload), don't try
    /// to interpret the file as a save game, etc.</summary>
    public const uint MPQ_OPEN_READ_ONLY = 0x00000100;
    public const uint MPQ_OPEN_NO_LISTFILE = 0x00010000;
    public const uint MPQ_OPEN_NO_ATTRIBUTES = 0x00020000;

    /// <summary>Pass to SFileOpenFileEx's dwSearchScope: open from a specific archive.</summary>
    public const uint SFILE_OPEN_FROM_MPQ = 0x00000000;

    // ── Helper: load a whole file into a managed byte[] ────────────────

    /// <summary>
    /// Open a file in the archive, read its contents into a managed array,
    /// close the file handle. Returns null if the file doesn't exist or any
    /// I/O step fails.
    ///
    /// This is the only "logic" in this file — it's worth keeping here so
    /// every caller doesn't reinvent the open/size/read/close dance with
    /// its attendant pinning and cleanup.
    /// </summary>
    public static byte[]? ReadEntireFile(IntPtr hArchive, string mpqPath)
    {
        if (!SFileOpenFileEx(hArchive, mpqPath, SFILE_OPEN_FROM_MPQ, out var hFile))
            return null;

        try
        {
            uint size = SFileGetFileSize(hFile, IntPtr.Zero);
            // 0xFFFFFFFF is StormLib's "error" sentinel for SFileGetFileSize.
            if (size == 0xFFFFFFFF) return null;
            if (size == 0) return Array.Empty<byte>();

            var buffer = new byte[size];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                if (!SFileReadFile(hFile, handle.AddrOfPinnedObject(), size, out var actuallyRead, IntPtr.Zero))
                    return null;
                if (actuallyRead != size)
                {
                    // Partial read — shouldn't happen for non-streaming archives,
                    // but if it does, hand back what we got rather than null so
                    // the caller can decide.
                    Array.Resize(ref buffer, (int)actuallyRead);
                }
                return buffer;
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            SFileCloseFile(hFile);
        }
    }
}
