using System.Diagnostics;
using System.Text;

namespace MangosSuperUI.Services;

/// <summary>
/// Regenerates server collision/LoS/pathfinding data after WMO placement commits.
///
/// Pipeline (surgical — skips VMapExtractor entirely):
///   1. REBUILD dir_bin from vanilla baseline + all active DB placements
///   2. Run VMapAssembler (Buildings/ → vmaps/) — rebuilds vmtree + vmtiles
///   3. Copy updated vmaps to server data directory
///   4. Run MoveMapGenerator for affected tile(s) — rebuilds mmtiles
///   5. Copy updated mmtiles to server data directory
///
/// KEY DESIGN: dir_bin is always rebuilt from scratch (vanilla baseline + DB state).
///   - No append, no duplicates, no orphans.
///   - Deleting a placement = DB delete → next regen excludes it automatically.
///   - The vanilla baseline (dir_bin.vanilla) is auto-created on first regen.
///     If the current dir_bin already has custom records (ID ≥ 900000), they are
///     stripped during baseline creation — safe to run on a dirty dir_bin.
///   - Concurrent regen calls are serialized via semaphore.
///
/// WHY THIS WORKS WITHOUT VMapExtractor:
///   VMapExtractor reads ADTs from MPQ, extracts WMO geometry into Buildings/*.wmo,
///   and writes placement records to Buildings/dir_bin. But:
///   - The WMO geometry files (e.g., Farm.wmo) already exist in Buildings/ from the
///     original extraction. Custom placements reuse existing vanilla WMO models.
///   - We only need to tell VMapAssembler WHERE the WMO is placed (dir_bin record).
///   - So we rebuild dir_bin with our placements and run VMapAssembler + MoveMapGenerator.
///
/// dir_bin BINARY FORMAT (per record):
///   [uint32] mapID
///   [uint32] tileX
///   [uint32] tileY
///   [uint32] flags          (MOD_HAS_BOUND=4 for WMOs)
///   [uint16] adtId          (0 for custom placements)
///   [uint32] ID             (unique — we use 900000+ range)
///   [float3] iPos           (fixCoords applied: Z,X,Y swap from WoW coords)
///   [float3] iRot           (rotation in degrees)
///   [float]  iScale         (1.0 for WMOs)
///   [float3] iBound.low     (world-space AABB, only if MOD_HAS_BOUND)
///   [float3] iBound.high
///   [uint32] nameLen
///   [char*]  name           (plain filename, e.g. "Farm.wmo")
///
/// COORDINATE TRANSFORM (fixCoords):
///   VMapExtractor applies fixCoords(v) = (v.z, v.x, v.y) to positions and bounds
///   before writing to dir_bin. Our MODF coordinates are already in WoW ADT space
///   (same as what the extractor reads from MODF), so we apply the same transform.
///
/// LAZY LOADING:
///   VMaNGOS lazy-loads vmaps (StaticMapTree::LoadMapTile) and mmaps
///   (MMapManager::loadMap) per tile on demand. If no player is on the tile,
///   replacing the files on disk is sufficient. If players are present,
///   a server restart or teleport-away-and-back forces reload.
/// </summary>
public class ServerDataService
{
    private readonly IConfiguration _config;
    private readonly ILogger<ServerDataService> _logger;

    // Custom placement IDs start at 900000 to avoid collision with vanilla IDs
    // (GenerateUniqueObjectId in extractor is sequential from 1, vanilla has ~200k objects)
    private const uint CUSTOM_OBJECT_ID_FLOOR = 900000;

    // ModelFlags from VMaNGOS ModelInstance.h
    private const uint MOD_M2 = 1;
    private const uint MOD_WORLDSPAWN = 1 << 1;
    private const uint MOD_HAS_BOUND = 1 << 2;

    // Serialize all regen operations — dir_bin rebuild + extractor runs must not overlap
    private static readonly SemaphoreSlim _regenLock = new(1, 1);

    public ServerDataService(IConfiguration config, ILogger<ServerDataService> logger)
    {
        _config = config;
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════
    // PATH HELPERS
    // ════════════════════════════════════════════════════════════════

    private string GetClientDir()
    {
        // Parent of Data/ — where Buildings/ and extractors run from
        string dataPath = _config.GetValue<string>("Vmangos:ClientDataPath")
            ?? "/home/wowvmangos/wowclient/Data";
        return Path.GetDirectoryName(dataPath) ?? "/home/wowvmangos/wowclient";
    }

    private string GetBuildingsDir() => Path.Combine(GetClientDir(), "Buildings");
    private string GetDirBinPath() => Path.Combine(GetBuildingsDir(), "dir_bin");
    private string GetVanillaDirBinPath() => Path.Combine(GetBuildingsDir(), "dir_bin.vanilla");

    private string GetExtractorsDir()
    {
        return _config.GetValue<string>("Vmangos:ExtractorsPath")
            ?? "/home/wowvmangos/vmangos/run/bin/Extractors";
    }

    private string GetServerDataDir()
    {
        return _config.GetValue<string>("Vmangos:ServerDataPath")
            ?? "/home/wowvmangos/vmangos/run/data";
    }

    private string GetServerVmapsDir() => Path.Combine(GetServerDataDir(), "vmaps");
    private string GetServerMmapsDir() => Path.Combine(GetServerDataDir(), "mmaps");

    // ════════════════════════════════════════════════════════════════
    // MAIN PIPELINE
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Full server data regeneration for one or more tiles.
    /// Rebuilds dir_bin from vanilla baseline + all active DB placements,
    /// runs VMapAssembler, then MoveMapGenerator for each affected tile.
    ///
    /// <paramref name="activePlacements"/> is the full list of committed placements from DB.
    /// <paramref name="affectedTiles"/> is the set of (mapId, tileX, tileY) to regenerate mmaps for.
    /// </summary>
    public async Task<ServerDataResult> RegenerateServerData(
        List<DirBinPlacement> activePlacements,
        List<(int mapId, int tileX, int tileY)> affectedTiles,
        Action<string>? onProgress = null)
    {
        var result = new ServerDataResult();
        var sw = Stopwatch.StartNew();

        void Log(string msg)
        {
            _logger.LogInformation("ServerData: {Msg}", msg);
            onProgress?.Invoke(msg);
            result.Log.Add(msg);
        }

        // Serialize — only one regen at a time
        if (!await _regenLock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            result.Error = "Another regeneration is already in progress. Please wait.";
            return result;
        }

        try
        {
            // ── Validate paths ──
            string buildingsDir = GetBuildingsDir();
            string dirBinPath = GetDirBinPath();
            string vanillaPath = GetVanillaDirBinPath();
            string extractorsDir = GetExtractorsDir();
            string clientDir = GetClientDir();

            if (!Directory.Exists(buildingsDir))
            {
                result.Error = $"Buildings directory not found: {buildingsDir}";
                return result;
            }
            if (!File.Exists(dirBinPath))
            {
                result.Error = $"dir_bin not found: {dirBinPath}";
                return result;
            }
            if (!Directory.Exists(extractorsDir))
            {
                result.Error = $"Extractors directory not found: {extractorsDir}. " +
                    "Set Vmangos:ExtractorsPath in Settings → World Viewer & Server Data.";
                return result;
            }

            // ── Verify WMO geometry for all placements ──
            foreach (var p in activePlacements)
            {
                string plainName = FindBuildingsFile(p.WmoPath);
                string wmoGeomPath = Path.Combine(buildingsDir, plainName);
                if (!File.Exists(wmoGeomPath))
                {
                    result.Error = $"WMO geometry not found in Buildings/: {plainName} " +
                                   $"(from path: {p.WmoPath}). " +
                                   "This WMO may need a full VMapExtractor run first.";
                    return result;
                }
                // Cache the resolved plain name on the placement
                p.ResolvedPlainName = plainName;
            }

            if (activePlacements.Count > 0)
                Log($"Verified {activePlacements.Count} WMO geometry file(s) in Buildings/");
            else
                Log("No active placements — rebuilding dir_bin with vanilla data only (cleanup)");

            // ── Step 1: Ensure vanilla baseline exists ──
            EnsureVanillaBaseline(dirBinPath, vanillaPath);
            Log($"Vanilla baseline: {new FileInfo(vanillaPath).Length:N0} bytes");

            // ── Step 2: Rebuild dir_bin = vanilla + active placements ──
            RebuildDirBin(vanillaPath, dirBinPath, activePlacements);
            Log($"Rebuilt dir_bin: vanilla + {activePlacements.Count} custom placement(s)");

            // ── Step 3: Run VMapAssembler ──
            Log("Running VMapAssembler (Buildings → vmaps)...");
            string vmapAssemblerPath = Path.Combine(extractorsDir, "VMapAssembler");
            string vmapOutputDir = Path.Combine(clientDir, "vmaps");

            var vmapResult = await RunProcess(
                vmapAssemblerPath,
                $"\"{buildingsDir}\" \"{vmapOutputDir}\"",
                clientDir,
                TimeSpan.FromMinutes(10),
                Log);

            if (!vmapResult.Success)
            {
                result.Error = $"VMapAssembler failed (exit code {vmapResult.ExitCode}): {vmapResult.LastError}";
                return result;
            }
            Log($"VMapAssembler completed in {vmapResult.ElapsedSeconds:F1}s");

            // ── Step 4: Copy affected vmaps to server data ──
            string serverVmapsDir = GetServerVmapsDir();
            int totalVmapsCopied = 0;
            var processedMaps = new HashSet<int>();
            foreach (var (mapId, tileX, tileY) in affectedTiles)
            {
                int copied = CopyAffectedVmaps(vmapOutputDir, serverVmapsDir, mapId, tileX, tileY, processedMaps);
                totalVmapsCopied += copied;
            }
            Log($"Copied {totalVmapsCopied} vmap file(s) to server data");

            // ── Step 5: Run MoveMapGenerator for each affected tile ──
            // CRITICAL (Session 54): MoveMapGenerator's --tile mode calls buildNavMesh(),
            // which overwrites the {mapId}.mmap params file with a freshly computed navmesh
            // origin based on discovered tiles on disk. If the tile count differs from the
            // original full extraction (even by 1), the origin shifts and the mmtile's
            // dtMeshHeader::x won't match what mangosd expects — the server silently rejects
            // the tile, breaking all NPC pathfinding on it. Fix: backup the .mmap file
            // before MoveMapGenerator runs, restore it after.
            int totalMmapsCopied = 0;
            string mmapGenPath = Path.Combine(extractorsDir, "MoveMapGenerator");
            string mmapOutputDir = Path.Combine(clientDir, "mmaps");
            string serverMmapsDir = GetServerMmapsDir();
            int threadCount = Environment.ProcessorCount;

            foreach (var (mapId, tileX, tileY) in affectedTiles)
            {
                Log($"Running MoveMapGenerator for map {mapId} tile ({tileX},{tileY})...");
                string mmapArgs = $"{mapId} --tile {tileX},{tileY} --threads {threadCount} --silent";

                // ── Protect .mmap params file from being overwritten ──
                string mmapParamsFile = Path.Combine(mmapOutputDir, $"{mapId:D3}.mmap");
                string mmapParamsBackup = mmapParamsFile + ".pre_singletile_bak";
                bool mmapParamsProtected = false;

                if (File.Exists(mmapParamsFile))
                {
                    File.Copy(mmapParamsFile, mmapParamsBackup, overwrite: true);
                    mmapParamsProtected = true;
                    Log($"Protected {mapId:D3}.mmap params ({new FileInfo(mmapParamsFile).Length} bytes)");
                }

                var mmapResult = await RunProcess(
                    mmapGenPath,
                    mmapArgs,
                    clientDir,
                    TimeSpan.FromMinutes(15),
                    Log);

                // ── Restore .mmap params file — always, even if MoveMapGenerator failed ──
                if (mmapParamsProtected && File.Exists(mmapParamsBackup))
                {
                    File.Copy(mmapParamsBackup, mmapParamsFile, overwrite: true);
                    Log($"Restored {mapId:D3}.mmap params");
                }

                if (!mmapResult.Success)
                {
                    Log($"WARNING: MoveMapGenerator failed for tile ({tileX},{tileY}): {mmapResult.LastError}");
                    continue; // continue with other tiles
                }
                Log($"MoveMapGenerator tile ({tileX},{tileY}) completed in {mmapResult.ElapsedSeconds:F1}s");

                int copied = CopyAffectedMmaps(mmapOutputDir, serverMmapsDir, mapId, tileX, tileY);
                totalMmapsCopied += copied;
            }
            Log($"Copied {totalMmapsCopied} mmap file(s) to server data");

            // ── Done ──
            sw.Stop();
            result.Success = true;
            result.VmapsRegenerated = true;
            result.MmapsRegenerated = totalMmapsCopied > 0;
            result.VmapsCopied = totalVmapsCopied;
            result.MmapsCopied = totalMmapsCopied;
            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            result.PlacementsIncluded = activePlacements.Count;
            Log($"Server data regeneration complete in {result.ElapsedSeconds:F1}s total");

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Error = ex.Message;
            Log($"ERROR: {ex.Message}");
            return result;
        }
        finally
        {
            _regenLock.Release();
        }
    }

    // ════════════════════════════════════════════════════════════════
    // VANILLA BASELINE
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensures dir_bin.vanilla exists. Created automatically on first regen.
    /// If the current dir_bin has custom records (ID ≥ 900000), they are stripped
    /// during baseline creation — safe to run on a previously-dirty dir_bin.
    /// </summary>
    private void EnsureVanillaBaseline(string dirBinPath, string vanillaPath)
    {
        if (File.Exists(vanillaPath))
            return;

        _logger.LogInformation("ServerData: Creating vanilla baseline (first-time setup)...");

        // Read current dir_bin and keep only vanilla records (ID < 900000)
        var vanillaRecords = new List<byte[]>();
        int customSkipped = 0;

        using (var fs = new FileStream(dirBinPath, FileMode.Open, FileAccess.Read))
        using (var br = new BinaryReader(fs))
        {
            while (fs.Position < fs.Length)
            {
                long recordStart = fs.Position;
                try
                {
                    uint mapId = br.ReadUInt32();
                    uint tileX = br.ReadUInt32();
                    uint tileY = br.ReadUInt32();
                    uint flags = br.ReadUInt32();
                    ushort adtId = br.ReadUInt16();
                    uint id = br.ReadUInt32();

                    // Skip position (3f), rotation (3f), scale (1f) = 7 floats
                    br.ReadBytes(7 * sizeof(float));

                    // Skip bounds if present
                    if ((flags & MOD_HAS_BOUND) != 0)
                        br.ReadBytes(6 * sizeof(float));

                    uint nameLen = br.ReadUInt32();
                    br.ReadBytes((int)nameLen);

                    long recordEnd = fs.Position;
                    int recordSize = (int)(recordEnd - recordStart);

                    if (id >= CUSTOM_OBJECT_ID_FLOOR)
                    {
                        customSkipped++;
                        continue;
                    }

                    // Keep vanilla record
                    fs.Position = recordStart;
                    vanillaRecords.Add(br.ReadBytes(recordSize));
                }
                catch (EndOfStreamException) { break; }
            }
        }

        // Write vanilla-only baseline
        using (var fs = new FileStream(vanillaPath, FileMode.Create, FileAccess.Write))
        {
            foreach (var record in vanillaRecords)
                fs.Write(record, 0, record.Length);
        }

        _logger.LogInformation(
            "ServerData: Vanilla baseline created — {VanillaCount} records, {SkippedCount} custom records stripped",
            vanillaRecords.Count, customSkipped);
    }

    // ════════════════════════════════════════════════════════════════
    // DIR_BIN REBUILD (replaces old AppendDirBinRecord)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rebuild dir_bin = vanilla baseline bytes + serialized custom placement records.
    /// This is idempotent: calling it twice with the same placements produces the same file.
    /// No duplicates, no orphans — the DB is the source of truth.
    /// </summary>
    private void RebuildDirBin(string vanillaPath, string dirBinPath, List<DirBinPlacement> placements)
    {
        // Backup current dir_bin
        string backupPath = dirBinPath + ".pre_rebuild_bak";
        File.Copy(dirBinPath, backupPath, overwrite: true);

        try
        {
            // Start with vanilla baseline
            byte[] vanillaBytes = File.ReadAllBytes(vanillaPath);

            using var fs = new FileStream(dirBinPath, FileMode.Create, FileAccess.Write);

            // Write vanilla records
            fs.Write(vanillaBytes, 0, vanillaBytes.Length);

            // Append each active custom placement
            using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);
            foreach (var p in placements)
            {
                WriteDirBinRecord(bw, p);
            }

            _logger.LogInformation(
                "ServerData: dir_bin rebuilt — {VanillaSize:N0} vanilla bytes + {CustomCount} custom records = {TotalSize:N0} bytes",
                vanillaBytes.Length, placements.Count, fs.Length);
        }
        catch
        {
            // Restore on failure
            if (File.Exists(backupPath))
                File.Copy(backupPath, dirBinPath, overwrite: true);
            throw;
        }
    }

    /// <summary>
    /// Write a single dir_bin record. Format matches VMapExtractor's WMOInstance write.
    /// </summary>
    private void WriteDirBinRecord(BinaryWriter bw, DirBinPlacement p)
    {
        string plainName = p.ResolvedPlainName ?? FindBuildingsFile(p.WmoPath);

        // ── Header: mapID, tileX, tileY ──
        bw.Write((uint)p.MapId);
        bw.Write((uint)p.TileX);
        bw.Write((uint)p.TileY);

        // ── ModelSpawn fields ──
        uint flags = MOD_HAS_BOUND; // WMOs always have bounds
        bw.Write(flags);

        ushort adtId = 0;
        bw.Write(adtId);

        bw.Write(p.UniqueId);

        // Position — apply fixCoords: (z, x, y)
        bw.Write(p.ModfPosZ);  // vmap X = wow Z
        bw.Write(p.ModfPosX);  // vmap Y = wow X
        bw.Write(p.ModfPosY);  // vmap Z = wow Y

        // Rotation — same fixCoords swap
        bw.Write(p.RotZ);
        bw.Write(p.RotX);
        bw.Write(p.RotY);

        // Scale (always 1.0 for WMOs)
        bw.Write(1.0f);

        // Bounding box — fixCoords applied
        bw.Write(p.BbMinZ);  // vmap X = wow Z
        bw.Write(p.BbMinX);  // vmap Y = wow X
        bw.Write(p.BbMinY);  // vmap Z = wow Y

        bw.Write(p.BbMaxZ);
        bw.Write(p.BbMaxX);
        bw.Write(p.BbMaxY);

        // Name
        byte[] nameBytes = Encoding.ASCII.GetBytes(plainName);
        bw.Write((uint)nameBytes.Length);
        bw.Write(nameBytes);

        _logger.LogDebug(
            "DirBin: Record — map={MapId} tile=({TileX},{TileY}) id={Id} name={Name}",
            p.MapId, p.TileX, p.TileY, p.UniqueId, plainName);
    }

    // ════════════════════════════════════════════════════════════════
    // PLAIN NAME EXTRACTION
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Matches VMapExtractor's GetPlainName + FixNameCase + FixNameSpaces behavior.
    /// "World\wmo\Azeroth\Buildings\human_farm\farm.wmo" → "Farm.wmo"
    /// "World\wmo\Azeroth\Buildings\GnomeHut\GnomeHut.wmo" → "Gnomehut.wmo"
    /// </summary>
    public static string GetPlainName(string fullPath)
    {
        string name = fullPath;
        int lastSep = name.LastIndexOfAny(new[] { '\\', '/' });
        if (lastSep >= 0)
            name = name.Substring(lastSep + 1);

        if (name.Length > 0)
            name = char.ToUpper(name[0]) + name.Substring(1).ToLower();

        name = name.Replace(' ', '_');
        return name;
    }

    /// <summary>
    /// Find the actual filename in Buildings/ via case-insensitive match.
    /// </summary>
    public string FindBuildingsFile(string wmoPath)
    {
        string plainName = GetPlainName(wmoPath);
        string buildingsDir = GetBuildingsDir();

        if (File.Exists(Path.Combine(buildingsDir, plainName)))
            return plainName;

        try
        {
            string match = Directory.GetFiles(buildingsDir, "*.wmo")
                .Select(Path.GetFileName)
                .FirstOrDefault(f => string.Equals(f, plainName, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                _logger.LogInformation("FindBuildingsFile: case-insensitive match '{Match}' for '{Plain}'",
                    match, plainName);
                return match;
            }
        }
        catch { /* directory listing failed, fall through */ }

        return plainName;
    }

    // ════════════════════════════════════════════════════════════════
    // FILE COPY HELPERS
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Copy the vmtree and affected vmtile(s) from extraction output to server data.
    /// processedMaps tracks which map vmtrees have already been copied (only need once per map).
    /// On first copy, snapshots the server's original files as .vanilla backups.
    /// </summary>
    private int CopyAffectedVmaps(string sourceDir, string destDir, int mapId, int tileX, int tileY,
        HashSet<int> processedMaps)
    {
        Directory.CreateDirectory(destDir);
        int copied = 0;

        // Copy vmtree once per map (contains the global BIH for all spawns on that map)
        if (processedMaps.Add(mapId))
        {
            string vmtreeFile = $"{mapId:D3}.vmtree";
            string vmtreeSrc = Path.Combine(sourceDir, vmtreeFile);
            if (File.Exists(vmtreeSrc))
            {
                string destPath = Path.Combine(destDir, vmtreeFile);
                BackupVanillaFile(destPath);
                File.Copy(vmtreeSrc, destPath, overwrite: true);
                copied++;
            }
        }

        // Copy the specific vmtile
        string vmtileFile = $"{mapId:D3}_{tileX:D2}_{tileY:D2}.vmtile";
        string vmtileSrc = Path.Combine(sourceDir, vmtileFile);
        if (File.Exists(vmtileSrc))
        {
            string destPath = Path.Combine(destDir, vmtileFile);
            BackupVanillaFile(destPath);
            File.Copy(vmtileSrc, destPath, overwrite: true);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// Copy the affected mmtile from extraction output to server data.
    /// On first copy, snapshots the server's original file as .vanilla backup.
    /// Also backs up the server-side .mmap params file (Session 54: this file
    /// defines the navmesh origin that all mmtiles must match).
    /// </summary>
    private int CopyAffectedMmaps(string sourceDir, string destDir, int mapId, int tileX, int tileY)
    {
        Directory.CreateDirectory(destDir);
        int copied = 0;

        // Backup server-side .mmap params file if not already backed up
        // (this is the contract between MoveMapGenerator and mangosd — must never change)
        string serverMmapParams = Path.Combine(destDir, $"{mapId:D3}.mmap");
        BackupVanillaFile(serverMmapParams);

        // MoveMapGenerator --tile X,Y writes file as {mapId:D3}{Y:D2}{X:D2}.mmtile
        string mmtileFile = $"{mapId:D3}{tileY:D2}{tileX:D2}.mmtile";
        string mmtileSrc = Path.Combine(sourceDir, mmtileFile);
        if (File.Exists(mmtileSrc))
        {
            string destPath = Path.Combine(destDir, mmtileFile);
            BackupVanillaFile(destPath);
            File.Copy(mmtileSrc, destPath, overwrite: true);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// If the file exists and no .vanilla backup exists yet, create the backup.
    /// This preserves the original extraction output so it can be restored if
    /// the regeneration pipeline produces bad data.
    /// </summary>
    private void BackupVanillaFile(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        string vanillaPath = filePath + ".vanilla";
        if (File.Exists(vanillaPath))
            return; // already backed up

        try
        {
            File.Copy(filePath, vanillaPath);
            _logger.LogInformation("ServerData: Backed up vanilla {File} ({Size:N0} bytes)",
                Path.GetFileName(filePath), new FileInfo(filePath).Length);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError("ServerData: Cannot create backup {VanillaPath} — permission denied. " +
                "Ensure the application user has write access to {Dir}. Error: {Msg}",
                vanillaPath, Path.GetDirectoryName(vanillaPath), ex.Message);
            throw new InvalidOperationException(
                $"Cannot create vanilla backup at {vanillaPath} — permission denied. " +
                $"Ensure the application user has write access to {Path.GetDirectoryName(vanillaPath)}/", ex);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // RESTORE VANILLA
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Restore all .vanilla backup files in vmaps/ and mmaps/ server data directories.
    /// Also restores dir_bin.vanilla → dir_bin in Buildings/.
    /// </summary>
    public RestoreResult RestoreVanillaServerData(Action<string>? onProgress = null)
    {
        var result = new RestoreResult();

        void Log(string msg)
        {
            _logger.LogInformation("RestoreVanilla: {Msg}", msg);
            onProgress?.Invoke(msg);
        }

        // Restore dir_bin from vanilla baseline
        string dirBinPath = GetDirBinPath();
        string vanillaBaseline = GetVanillaDirBinPath();
        if (File.Exists(vanillaBaseline))
        {
            File.Copy(vanillaBaseline, dirBinPath, overwrite: true);
            result.FilesRestored++;
            Log($"Restored dir_bin from vanilla baseline ({new FileInfo(dirBinPath).Length:N0} bytes)");
        }

        // Scan vmaps and mmaps for .vanilla backup files
        string[] dirs = { GetServerVmapsDir(), GetServerMmapsDir() };
        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            string dirName = Path.GetFileName(dir);
            var vanillaFiles = Directory.GetFiles(dir, "*.vanilla");
            foreach (string vanillaPath in vanillaFiles)
            {
                string originalPath = vanillaPath.Substring(0, vanillaPath.Length - ".vanilla".Length);
                string fileName = Path.GetFileName(originalPath);

                try
                {
                    File.Copy(vanillaPath, originalPath, overwrite: true);
                    result.FilesRestored++;
                    Log($"Restored {dirName}/{fileName}");
                }
                catch (Exception ex)
                {
                    Log($"WARNING: Could not restore {dirName}/{fileName}: {ex.Message}");
                }
            }

            if (vanillaFiles.Length > 0)
                Log($"Restored {vanillaFiles.Length} file(s) in {dirName}/");
        }

        // Also check client-side vmaps/mmaps (extraction output)
        string clientDir = GetClientDir();
        string[] clientDirs = {
            Path.Combine(clientDir, "vmaps"),
            Path.Combine(clientDir, "mmaps")
        };
        foreach (string dir in clientDirs)
        {
            if (!Directory.Exists(dir)) continue;

            string dirName = Path.GetFileName(dir);
            var vanillaFiles = Directory.GetFiles(dir, "*.vanilla");
            foreach (string vanillaPath in vanillaFiles)
            {
                string originalPath = vanillaPath.Substring(0, vanillaPath.Length - ".vanilla".Length);
                try
                {
                    File.Copy(vanillaPath, originalPath, overwrite: true);
                    result.FilesRestored++;
                }
                catch { /* best effort for client-side */ }
            }

            if (vanillaFiles.Length > 0)
                Log($"Restored {vanillaFiles.Length} client-side file(s) in {dirName}/");
        }

        return result;
    }

    // ════════════════════════════════════════════════════════════════
    // BACKUP STATUS
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scans all configured directories for .vanilla backup files.
    /// Returns a summary of what can be restored.
    /// </summary>
    public BackupStatusResult GetBackupStatus()
    {
        var result = new BackupStatusResult();

        // dir_bin.vanilla
        string vanillaBaseline = GetVanillaDirBinPath();
        result.DirBinBackup = File.Exists(vanillaBaseline);
        if (result.DirBinBackup)
            result.DirBinBackupSize = new FileInfo(vanillaBaseline).Length;

        // Server-side vmaps/ and mmaps/
        result.VmapFiles = CountVanillaFiles(GetServerVmapsDir());
        result.MmapFiles = CountVanillaFiles(GetServerMmapsDir());

        // Client-side vmaps/ and mmaps/ (extraction output)
        string clientDir = GetClientDir();
        result.ClientVmapFiles = CountVanillaFiles(Path.Combine(clientDir, "vmaps"));
        result.ClientMmapFiles = CountVanillaFiles(Path.Combine(clientDir, "mmaps"));

        result.TotalBackups = (result.DirBinBackup ? 1 : 0)
            + result.VmapFiles + result.MmapFiles
            + result.ClientVmapFiles + result.ClientMmapFiles;

        // Include the paths so the UI can show where backups live
        result.ServerVmapsDir = GetServerVmapsDir();
        result.ServerMmapsDir = GetServerMmapsDir();
        result.ClientVmapsDir = Path.Combine(clientDir, "vmaps");
        result.ClientMmapsDir = Path.Combine(clientDir, "mmaps");
        result.BuildingsDir = GetBuildingsDir();

        return result;
    }

    private static int CountVanillaFiles(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try { return Directory.GetFiles(dir, "*.vanilla").Length; }
        catch { return 0; }
    }

    // ════════════════════════════════════════════════════════════════
    // PROCESS RUNNER
    // ════════════════════════════════════════════════════════════════

    private async Task<ProcessResult> RunProcess(
        string exePath, string args, string workingDir,
        TimeSpan timeout, Action<string> onProgress)
    {
        var result = new ProcessResult();
        var sw = Stopwatch.StartNew();

        if (!File.Exists(exePath))
        {
            result.LastError = $"Executable not found: {exePath}";
            return result;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
                if (e.Data.Length > 0 && !e.Data.StartsWith("MMAP:"))
                    onProgress?.Invoke($"  > {e.Data}");
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
                result.LastError = e.Data;
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.StandardInput.WriteLineAsync("");
            proc.StandardInput.Close();
        }
        catch { /* stdin may not be needed for all processes */ }

        bool exited = await Task.Run(() => proc.WaitForExit((int)timeout.TotalMilliseconds));
        sw.Stop();

        if (!exited)
        {
            try { proc.Kill(); } catch { }
            result.LastError = $"Process timed out after {timeout.TotalMinutes:F0} minutes";
            result.ExitCode = -1;
            return result;
        }

        result.ExitCode = proc.ExitCode;
        result.Success = proc.ExitCode == 0;
        result.Output = outputBuilder.ToString();
        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;

        if (!result.Success && string.IsNullOrEmpty(result.LastError))
            result.LastError = $"Process exited with code {proc.ExitCode}";

        return result;
    }

    // ════════════════════════════════════════════════════════════════
    // DIAGNOSTIC
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Read and validate all records in dir_bin. Returns summary info.
    /// </summary>
    public DirBinDiagnostic DiagnoseDirBin()
    {
        var diag = new DirBinDiagnostic();
        string dirBinPath = GetDirBinPath();
        string vanillaPath = GetVanillaDirBinPath();

        if (!File.Exists(dirBinPath))
        {
            diag.Error = "dir_bin not found";
            return diag;
        }

        diag.FileSizeBytes = new FileInfo(dirBinPath).Length;
        diag.VanillaBaselineExists = File.Exists(vanillaPath);
        if (diag.VanillaBaselineExists)
            diag.VanillaBaselineSizeBytes = new FileInfo(vanillaPath).Length;

        using var fs = new FileStream(dirBinPath, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        int recordCount = 0;
        int customCount = 0;
        var mapIds = new HashSet<uint>();
        var tileKeys = new HashSet<string>();

        try
        {
            while (fs.Position < fs.Length)
            {
                uint mapId = br.ReadUInt32();
                uint tileX = br.ReadUInt32();
                uint tileY = br.ReadUInt32();

                uint flags = br.ReadUInt32();
                ushort adtId = br.ReadUInt16();
                uint id = br.ReadUInt32();

                br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // pos
                br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // rot
                br.ReadSingle(); // scale

                if ((flags & MOD_HAS_BOUND) != 0)
                {
                    br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // bbLow
                    br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // bbHigh
                }

                uint nameLen = br.ReadUInt32();
                if (nameLen > 500)
                {
                    diag.Error = $"Invalid name length {nameLen} at record {recordCount}";
                    break;
                }
                string name = Encoding.ASCII.GetString(br.ReadBytes((int)nameLen));

                recordCount++;
                mapIds.Add(mapId);
                tileKeys.Add($"{mapId}_{tileX}_{tileY}");

                if (id >= CUSTOM_OBJECT_ID_FLOOR)
                {
                    customCount++;
                    diag.CustomRecords.Add(new DirBinRecord
                    {
                        MapId = mapId,
                        TileX = tileX,
                        TileY = tileY,
                        Id = id,
                        Name = name,
                        Flags = flags
                    });
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Normal — file doesn't have a sentinel
        }

        diag.TotalRecords = recordCount;
        diag.CustomRecords_Count = customCount;
        diag.UniqueMapIds = mapIds.Count;
        diag.UniqueTiles = tileKeys.Count;
        return diag;
    }
}

// ════════════════════════════════════════════════════════════════
// DTOs
// ════════════════════════════════════════════════════════════════

/// <summary>
/// A placement record ready to be written to dir_bin.
/// Built by the controller from DB data — all coordinates already computed.
/// </summary>
public class DirBinPlacement
{
    public int PlacementDbId { get; set; }
    public int MapId { get; set; }
    public int TileX { get; set; }
    public int TileY { get; set; }
    public uint UniqueId { get; set; }  // 900000 + DB id

    // MODF coordinates (WoW ADT space — before fixCoords)
    public float ModfPosX { get; set; }
    public float ModfPosY { get; set; }
    public float ModfPosZ { get; set; }
    public float RotX { get; set; }
    public float RotY { get; set; }
    public float RotZ { get; set; }

    // MODF bounding box (WoW ADT space)
    public float BbMinX { get; set; }
    public float BbMinY { get; set; }
    public float BbMinZ { get; set; }
    public float BbMaxX { get; set; }
    public float BbMaxY { get; set; }
    public float BbMaxZ { get; set; }

    // WMO path as stored in MWMO
    public string WmoPath { get; set; } = "";

    // Resolved by FindBuildingsFile during validation — set before WriteDirBinRecord
    public string? ResolvedPlainName { get; set; }
}

public class ServerDataResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public bool VmapsRegenerated { get; set; }
    public bool MmapsRegenerated { get; set; }
    public int VmapsCopied { get; set; }
    public int MmapsCopied { get; set; }
    public int PlacementsIncluded { get; set; }
    public double ElapsedSeconds { get; set; }
    public List<string> Log { get; set; } = new();
}

public class RestoreResult
{
    public int FilesRestored { get; set; }
}

public class BackupStatusResult
{
    public bool DirBinBackup { get; set; }
    public long DirBinBackupSize { get; set; }
    public int VmapFiles { get; set; }
    public int MmapFiles { get; set; }
    public int ClientVmapFiles { get; set; }
    public int ClientMmapFiles { get; set; }
    public int TotalBackups { get; set; }
    public string? ServerVmapsDir { get; set; }
    public string? ServerMmapsDir { get; set; }
    public string? ClientVmapsDir { get; set; }
    public string? ClientMmapsDir { get; set; }
    public string? BuildingsDir { get; set; }
}

public class ProcessResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; } = -1;
    public string Output { get; set; } = "";
    public string? LastError { get; set; }
    public double ElapsedSeconds { get; set; }
}

public class DirBinDiagnostic
{
    public string? Error { get; set; }
    public long FileSizeBytes { get; set; }
    public bool VanillaBaselineExists { get; set; }
    public long VanillaBaselineSizeBytes { get; set; }
    public int TotalRecords { get; set; }
    public int CustomRecords_Count { get; set; }
    public int UniqueMapIds { get; set; }
    public int UniqueTiles { get; set; }
    public List<DirBinRecord> CustomRecords { get; set; } = new();
}

public class DirBinRecord
{
    public uint MapId { get; set; }
    public uint TileX { get; set; }
    public uint TileY { get; set; }
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public uint Flags { get; set; }
}