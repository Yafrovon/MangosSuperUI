namespace MangosSuperUI.Services;

/// <summary>
/// Singleton service that opens the WoW 1.12.1 client MPQ archives at startup
/// and provides on-demand file extraction for any game asset.
///
/// This replaces the need for pre-extracting every M2/BLP to disk.
/// MPQ search order: reverse alphabetical (patch-Z &gt; patch-2 &gt; patch &gt; model &gt; base)
/// so patch overrides take priority.
///
/// Config: Vmangos:ClientDataPath → "/home/wowvmangos/wowclient/Data"
///
/// === Backing library ===
/// Uses StormLib (Ladislav Zezula) via the StormLibNative P/Invoke layer.
/// The native libstorm.so / storm.dll lives at runtimes/{rid}/native/ in the
/// project tree and is auto-resolved by .NET 8's DllImport lookup — no
/// LD_LIBRARY_PATH or env setup required.
///
/// === Thread safety ===
/// StormLib's SFileHasFile / SFileOpenFileEx / SFileReadFile are NOT
/// thread-safe on a shared archive handle. The native code mutates
/// per-archive state (decompression buffers, hash-table cursors) without
/// internal locking, so two concurrent calls on the same IntPtr race and
/// one will get phantom-false from SFileHasFile for files that exist.
///
/// This bit us hard during body-atlas resolution: equipping a full set
/// fires 10 items × 8 slots = ~80 concurrent ExtractFile calls into the
/// same archive list, producing intermittent partial renders (bare skin
/// where Wrath plate should be). See the Session "BodyAtlas concurrency"
/// handoff for the full diagnosis.
///
/// Fix: a single _stormLock around every native call. Per-archive locks
/// would allow more concurrency in theory, but ExtractFile returns in
/// microseconds — the simpler "no two threads in StormLib at once"
/// invariant is worth more than the contention savings. Revisit only if
/// profiling shows the lock is hot, which is very unlikely.
///
/// History: previously backed by War3Net.IO.Mpq, which works for Warcraft 3
/// MPQs but cannot read entries from vanilla WoW 1.12 patch.MPQ (returns a
/// stream whose .Read() throws NotSupportedException — see Session I handoff).
/// StormLib is the reference implementation and handles every compression
/// combination vanilla 1.12 uses.
/// </summary>
public class MpqReaderService : IDisposable
{
    private readonly ILogger<MpqReaderService> _logger;
    private readonly IConfiguration _config;

    /// <summary>Opened archives, in load order. Reverse iteration during
    /// extraction gives patch-overrides-base semantics.</summary>
    private readonly List<(string Name, IntPtr Handle)> _archives = new();

    private bool _initialized;
    private readonly object _initLock = new();

    /// <summary>
    /// Serializes every StormLib native call. See class doc for why.
    /// Held briefly (microseconds per ExtractFile), so contention is
    /// negligible even under the heaviest concurrent-equip load.
    /// </summary>
    private readonly object _stormLock = new();

    public bool IsInitialized => _initialized;
    public int ArchiveCount => _archives.Count;

    public MpqReaderService(IConfiguration config, ILogger<MpqReaderService> logger)
    {
        _config = config;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════
    // INITIALIZATION (lazy, thread-safe)
    // ═══════════════════════════════════════════════════════════════════

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            Initialize();
        }
    }

    private void Initialize()
    {
        var dataPath = _config["Vmangos:ClientDataPath"]
            ?? _config["SpellCreator:ClientDataPath"]
            ?? "/home/wowvmangos/wowclient/Data";

        if (!Directory.Exists(dataPath))
        {
            _logger.LogWarning("MpqReader: Client data path not found: {Path}", dataPath);
            _initialized = true;
            return;
        }

        _logger.LogInformation("MpqReader: Opening MPQ archives from {Path}", dataPath);

        var mpqFiles = Directory.GetFiles(dataPath, "*.MPQ", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(dataPath, "*.mpq", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Initialization happens once under _initLock, and no other code
        // can be touching StormLib yet (we set _initialized = true at the
        // end, gating every public method). So we don't need _stormLock
        // here — the init-lock provides the same guarantee.
        foreach (var mpqPath in mpqFiles)
        {
            var name = Path.GetFileName(mpqPath);

            // Skip our own custom patch files — we only want vanilla assets
            if (name.StartsWith("patch-Z", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("patch-M", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("patch-3", StringComparison.OrdinalIgnoreCase))
                continue;

            long size = -1;
            try { size = new FileInfo(mpqPath).Length; } catch { }

            // Open read-only. We never modify archives at runtime.
            // No flag for "skip listfile" — StormLib loads it lazily and
            // cheaply, unlike War3Net, so we let it.
            const uint flags = StormLibNative.MPQ_OPEN_READ_ONLY;

            if (StormLibNative.SFileOpenArchive(mpqPath, 0, flags, out var handle))
            {
                _archives.Add((name, handle));
                _logger.LogInformation("MpqReader: Opened {Name} ({Size:N0} bytes)", name, size);
            }
            else
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                _logger.LogWarning(
                    "MpqReader: SFileOpenArchive failed for {Name} ({Size:N0} bytes), errno={Err}",
                    name, size, err);
            }
        }

        _logger.LogInformation("MpqReader: {Count} archives opened", _archives.Count);
        _initialized = true;
    }

    // ═══════════════════════════════════════════════════════════════════
    // FILE EXTRACTION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extract a file by its MPQ-internal path.
    /// Searches archives in reverse order (patches override base files).
    /// Returns null if not found in any archive.
    /// </summary>
    public byte[]? ExtractFile(string mpqPath)
    {
        EnsureInitialized();

        // _stormLock serializes the entire reverse-search + read sequence.
        // We can't release between SFileHasFile and ReadEntireFile because
        // both touch the same archive's internal state — releasing in
        // between would let a second thread corrupt it before we read.
        // See class-level doc for why this is necessary.
        lock (_stormLock)
        {
            // Reverse iteration: patches first, base last. Matches WoW's own
            // resolution order — a file present in patch.MPQ wins over the same
            // path in (e.g.) texture.MPQ.
            for (int i = _archives.Count - 1; i >= 0; i--)
            {
                var (name, handle) = _archives[i];
                try
                {
                    // Fast existence check before the heavier open-read-close.
                    // Avoids allocating a file handle for the common "file not in
                    // this archive" case during reverse-order search.
                    if (!StormLibNative.SFileHasFile(handle, mpqPath)) continue;

                    var data = StormLibNative.ReadEntireFile(handle, mpqPath);
                    if (data != null) return data;

                    // SFileHasFile said yes but ReadEntireFile failed — log it,
                    // this would be a real bug worth investigating.
                    _logger.LogWarning(
                        "MpqReader: ExtractFile {Path} — SFileHasFile=true but read failed in {Archive}",
                        mpqPath, name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "MpqReader: ExtractFile {Path} threw in {Archive}: {Type}: {Err}",
                        mpqPath, name, ex.GetType().Name, ex.Message);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Try to extract a model file, attempting multiple extension variations.
    /// ItemDisplayInfo.dbc stores model names without consistent extensions
    /// (.mdx, .MDX, .m2, .M2 all appear).
    /// </summary>
    public byte[]? ExtractModelFile(string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;

        if (modelPath.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
            return null;

        string pathNoExt;
        if (modelPath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
            modelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            pathNoExt = modelPath[..^4];
        else if (modelPath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
            pathNoExt = modelPath[..^3];
        else
            pathNoExt = modelPath;

        string[] extensions = { ".m2", ".mdx", ".M2", ".MDX" };
        string[] pathBases = { pathNoExt, pathNoExt.ToLowerInvariant() };

        // ExtractFile takes _stormLock internally for each call. That
        // means a single ExtractModelFile that misses 7 times before
        // hitting will lock+unlock 8 times rather than holding the lock
        // across the whole search — preferable because it lets other
        // threads slip in between extension attempts.
        foreach (var pb in pathBases)
        {
            foreach (var ext in extensions)
            {
                var data = ExtractFile(pb + ext);
                if (data != null) return data;
            }
        }

        return ExtractFile(modelPath);
    }

    // ═══════════════════════════════════════════════════════════════════
    // HASH-TABLE PROBE (diagnostics, listfile-independent)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Diagnostic — for each candidate path, check every archive via the
    /// hash table (SFileHasFile, not (listfile)) and return all hits.
    /// Use when you need to know "does this file exist anywhere?" without
    /// depending on listfile presence.
    ///
    /// Reports sizes via a real open-and-size call rather than a cached
    /// stream metadata trick, so hits in this list confirm the file is
    /// genuinely readable by SFileReadFile.
    /// </summary>
    public List<MpqHit> FindByExactPaths(IEnumerable<string> candidatePaths)
    {
        EnsureInitialized();
        var hits = new List<MpqHit>();

        // Same _stormLock as ExtractFile — SFileHasFile, SFileOpenFileEx,
        // SFileGetFileSize, and SFileCloseFile all mutate the archive's
        // internal cursor/buffer state. Diagnostics endpoint, so the long
        // hold doesn't matter.
        lock (_stormLock)
        {
            foreach (var candidate in candidatePaths)
            {
                for (int i = _archives.Count - 1; i >= 0; i--)
                {
                    var (archName, handle) = _archives[i];
                    try
                    {
                        if (!StormLibNative.SFileHasFile(handle, candidate)) continue;

                        // Open and read the size for honest reporting. This is
                        // the diagnostic endpoint, so a per-hit allocation is fine.
                        long size = 0;
                        if (StormLibNative.SFileOpenFileEx(handle, candidate,
                            StormLibNative.SFILE_OPEN_FROM_MPQ, out var hFile))
                        {
                            size = StormLibNative.SFileGetFileSize(hFile, IntPtr.Zero);
                            StormLibNative.SFileCloseFile(hFile);
                        }

                        hits.Add(new MpqHit
                        {
                            Path = candidate,
                            Archive = archName,
                            Size = size,
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            "MpqReader: FindByExactPaths {Path} threw in {Archive}: {Type}: {Err}",
                            candidate, archName, ex.GetType().Name, ex.Message);
                    }
                }
            }
        }

        return hits;
    }

    public class MpqHit
    {
        public string Path { get; set; } = "";
        public string Archive { get; set; } = "";
        public long Size { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════
    // LISTFILE-BASED SEARCH (diagnostics)
    // ═══════════════════════════════════════════════════════════════════

    private List<string>? _allPaths;
    private readonly object _allPathsLock = new();

    /// <summary>
    /// Extract the (listfile) from each opened MPQ and flatten into one list.
    /// Cached after first call. Note: some MPQs ship without a listfile or
    /// with an incomplete one — this is best-effort.
    /// </summary>
    public List<string> GetAllPaths()
    {
        EnsureInitialized();
        if (_allPaths != null) return _allPaths;
        lock (_allPathsLock)
        {
            if (_allPaths != null) return _allPaths;

            var combined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ReadEntireFile is a StormLib call — guard with _stormLock
            // to prevent racing a concurrent ExtractFile on the same
            // handle. Only happens once (cached afterwards) but the
            // first call could otherwise corrupt an in-flight equip.
            lock (_stormLock)
            {
                foreach (var (name, handle) in _archives)
                {
                    try
                    {
                        var listfile = StormLibNative.ReadEntireFile(handle, "(listfile)");
                        if (listfile == null || listfile.Length == 0) continue;

                        var contents = System.Text.Encoding.UTF8.GetString(listfile);
                        foreach (var line in contents.Split(new[] { '\r', '\n' },
                                                           StringSplitOptions.RemoveEmptyEntries))
                        {
                            combined.Add(line.Trim());
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            "MpqReader: (listfile) read failed in {Name}: {Err}",
                            name, ex.Message);
                    }
                }
            }

            _allPaths = combined.ToList();
            _logger.LogInformation(
                "MpqReader: cached {Count} unique paths from listfiles", _allPaths.Count);
            return _allPaths;
        }
    }

    /// <summary>
    /// Find every MPQ-internal path whose filename contains the partial name
    /// (case-insensitive). Used by BodyAtlasTextureService diagnostics.
    /// </summary>
    public List<string> FindByPartialName(string partial)
    {
        if (string.IsNullOrEmpty(partial)) return new List<string>();
        var paths = GetAllPaths();
        return paths
            .Where(p => p.IndexOf(partial, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
    }

    // ═══════════════════════════════════════════════════════════════════
    // CLEANUP
    // ═══════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        // SFileCloseArchive is a StormLib call, so technically belongs
        // under _stormLock. In practice Dispose only runs at shutdown
        // when no other threads are calling in, but the lock costs
        // nothing and keeps the invariant "every StormLib call is
        // serialized" airtight.
        lock (_stormLock)
        {
            foreach (var (_, handle) in _archives)
            {
                try { StormLibNative.SFileCloseArchive(handle); } catch { }
            }
            _archives.Clear();
        }
    }
}