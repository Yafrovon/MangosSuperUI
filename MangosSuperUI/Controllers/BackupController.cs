using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MangosSuperUI.Models;
using MangosSuperUI.Services;

namespace MangosSuperUI.Controllers;

public class BackupController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly ProcessManagerService _proc;
    private readonly VmangosSettings _settings;
    private readonly IConfiguration _config;
    private readonly ILogger<BackupController> _logger;

    // Configurable via appsettings — defaults if missing
    private string BackupRoot => _settings.BackupDirectory ?? "/home/wowvmangos/backups";
    private string SourceRoot => _settings.VmangosSourcePath ?? "/home/wowvmangos/vmangos/src";
    private string SqlRoot => _settings.VmangosSqlPath ?? "/home/wowvmangos/vmangos/sql";

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BackupController(
        ConnectionFactory db,
        AuditService audit,
        ProcessManagerService proc,
        IOptions<VmangosSettings> settings,
        IConfiguration config,
        ILogger<BackupController> logger)
    {
        _db = db;
        _audit = audit;
        _proc = proc;
        _settings = settings.Value;
        _config = config;
        _logger = logger;
    }

    // ===================== PAGE =====================

    public IActionResult Index() => View();

    // ===================== LIST =====================

    /// <summary>
    /// GET /Backup/List — Returns all backups with manifests, sorted newest first.
    /// </summary>
    [HttpGet]
    public IActionResult List()
    {
        EnsureBackupDir();

        var backups = new List<object>();
        var root = new DirectoryInfo(BackupRoot);
        if (!root.Exists) return Json(new { backups });

        foreach (var dir in root.GetDirectories().OrderByDescending(d => d.Name))
        {
            var manifestPath = Path.Combine(dir.FullName, "manifest.json");
            if (!System.IO.File.Exists(manifestPath)) continue;

            try
            {
                var json = System.IO.File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<JsonElement>(json);
                backups.Add(new
                {
                    folder = dir.Name,
                    manifest,
                    totalSize = GetDirectorySize(dir)
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read manifest in {Dir}", dir.Name);
            }
        }

        return Json(new { backups });
    }

    // ===================== STATS (live snapshot) =====================

    /// <summary>
    /// GET /Backup/Stats — Current DB stats for display before backup.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Stats()
    {
        var stats = await GatherStats();
        return Json(stats);
    }

    // ===================== CREATE BACKUP =====================

    /// <summary>
    /// POST /Backup/Create — Create a backup of selected groups.
    /// Body: { groups: ["world","players","core"], label: "Before lootifier batch" }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] BackupRequest request)
    {
        if (request.Groups == null || request.Groups.Length == 0)
            return Json(new { success = false, error = "No backup groups selected" });

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupDir = Path.Combine(BackupRoot, timestamp);

        try
        {
            EnsureBackupDir();
            Directory.CreateDirectory(backupDir);

            var results = new Dictionary<string, GroupResult>();
            var sizes = new Dictionary<string, string>();

            foreach (var group in request.Groups)
            {
                switch (group)
                {
                    case "world":
                        results["world"] = await BackupWorld(backupDir);
                        break;
                    case "players":
                        results["players"] = await BackupPlayers(backupDir);
                        break;
                    case "core":
                        results["core"] = await BackupCore(backupDir);
                        break;
                }
            }

            // Check for failures
            var failed = results.Where(r => !r.Value.Success).ToList();
            if (failed.Count == results.Count)
            {
                // All failed — clean up
                try { Directory.Delete(backupDir, true); } catch { }
                return Json(new { success = false, error = "All backup groups failed: " + string.Join("; ", failed.Select(f => f.Key + ": " + f.Value.Error)) });
            }

            // Gather stats and write manifest
            var stats = await GatherStats();
            foreach (var group in request.Groups)
            {
                var files = Directory.GetFiles(backupDir, $"{group}*");
                long totalBytes = files.Sum(f => new FileInfo(f).Length);
                sizes[group] = FormatBytes(totalBytes);
            }

            var manifest = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                label = request.Label ?? "",
                groups = request.Groups.Where(g => results.ContainsKey(g) && results[g].Success).ToArray(),
                failedGroups = failed.Select(f => f.Key).ToArray(),
                stats,
                sizes
            };

            await System.IO.File.WriteAllTextAsync(
                Path.Combine(backupDir, "manifest.json"),
                JsonSerializer.Serialize(manifest, _jsonOpts));

            // Audit
            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "system",
                Action = "backup_create",
                TargetType = "backup",
                TargetName = timestamp,
                StateAfter = JsonSerializer.Serialize(manifest, _jsonOpts),
                IsReversible = false,
                Success = true,
                Notes = $"Backup created: {string.Join(", ", manifest.groups)}" +
                        (string.IsNullOrEmpty(request.Label) ? "" : $" — {request.Label}")
            });

            return Json(new { success = true, folder = timestamp, manifest });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup creation failed");
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true); } catch { }
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ===================== RESTORE =====================

    /// <summary>
    /// POST /Backup/Restore — Restore a specific group from a backup.
    /// Body: { folder: "2026-04-13_14-30-00", group: "world" }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Restore([FromBody] RestoreRequest request)
    {
        if (string.IsNullOrEmpty(request.Folder) || string.IsNullOrEmpty(request.Group))
            return Json(new { success = false, error = "Missing folder or group" });

        // Sanitize folder name
        var safeName = Path.GetFileName(request.Folder);
        var backupDir = Path.Combine(BackupRoot, safeName);
        if (!Directory.Exists(backupDir))
            return Json(new { success = false, error = "Backup not found" });

        try
        {
            // Take a pre-restore safety backup
            var safetyTimestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_pre-restore";
            var safetyDir = Path.Combine(BackupRoot, safetyTimestamp);
            Directory.CreateDirectory(safetyDir);

            switch (request.Group)
            {
                case "world":
                    await BackupWorld(safetyDir); // safety snapshot
                    await RestoreWorld(backupDir);
                    break;
                case "players":
                    await BackupPlayers(safetyDir); // safety snapshot
                    await RestorePlayers(backupDir);
                    break;
                case "core":
                    await BackupCore(safetyDir); // safety snapshot
                    await RestoreCore(backupDir);
                    break;
                default:
                    return Json(new { success = false, error = "Unknown group: " + request.Group });
            }

            // Write a minimal manifest for the safety backup
            var safetyManifest = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                label = $"Auto-snapshot before restoring {request.Group} from {safeName}",
                groups = new[] { request.Group },
                failedGroups = Array.Empty<string>(),
                stats = await GatherStats(),
                sizes = new Dictionary<string, string>()
            };
            await System.IO.File.WriteAllTextAsync(
                Path.Combine(safetyDir, "manifest.json"),
                JsonSerializer.Serialize(safetyManifest, _jsonOpts));

            // Audit
            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "system",
                Action = "backup_restore",
                TargetType = "backup",
                TargetName = safeName,
                IsReversible = false,
                Success = true,
                Notes = $"Restored {request.Group} from {safeName}. Safety snapshot: {safetyTimestamp}"
            });

            return Json(new { success = true, safetyBackup = safetyTimestamp });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore failed for {Group} from {Folder}", request.Group, request.Folder);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ===================== DELETE =====================

    /// <summary>
    /// POST /Backup/Delete — Delete a backup folder.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Delete([FromBody] DeleteRequest request)
    {
        if (string.IsNullOrEmpty(request.Folder))
            return Json(new { success = false, error = "Missing folder" });

        var safeName = Path.GetFileName(request.Folder);
        var backupDir = Path.Combine(BackupRoot, safeName);
        if (!Directory.Exists(backupDir))
            return Json(new { success = false, error = "Backup not found" });

        try
        {
            Directory.Delete(backupDir, true);

            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "system",
                Action = "backup_delete",
                TargetType = "backup",
                TargetName = safeName,
                IsReversible = false,
                Success = true,
                Notes = $"Deleted backup {safeName}"
            });

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ===================== UPDATE LABEL =====================

    /// <summary>
    /// POST /Backup/UpdateLabel — Update the label/notes on a backup.
    /// </summary>
    [HttpPost]
    public IActionResult UpdateLabel([FromBody] UpdateLabelRequest request)
    {
        if (string.IsNullOrEmpty(request.Folder))
            return Json(new { success = false, error = "Missing folder" });

        var safeName = Path.GetFileName(request.Folder);
        var manifestPath = Path.Combine(BackupRoot, safeName, "manifest.json");
        if (!System.IO.File.Exists(manifestPath))
            return Json(new { success = false, error = "Manifest not found" });

        try
        {
            var json = System.IO.File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

            // Replace the label value
            var updated = new Dictionary<string, object>();
            foreach (var kv in dict)
            {
                if (kv.Key == "label")
                    updated["label"] = request.Label ?? "";
                else
                    updated[kv.Key] = kv.Value;
            }

            System.IO.File.WriteAllText(manifestPath, JsonSerializer.Serialize(updated, _jsonOpts));
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ===================== BACKUP IMPLEMENTATIONS =====================

    private async Task<GroupResult> BackupWorld(string dir)
    {
        try
        {
            var (host, port, user, pass) = ParseConnectionString("Mangos");
            await RunMysqlDump(host, port, user, pass, "mangos", Path.Combine(dir, "world_mangos.sql.gz"));
            await RunMysqlDump(host, port, user, pass, "vmangos_admin", Path.Combine(dir, "world_vmangos_admin.sql.gz"));
            return new GroupResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "World backup failed");
            return new GroupResult { Success = false, Error = ex.Message };
        }
    }

    private async Task<GroupResult> BackupPlayers(string dir)
    {
        try
        {
            var (host, port, user, pass) = ParseConnectionString("Characters");
            await RunMysqlDump(host, port, user, pass, "characters", Path.Combine(dir, "players_characters.sql.gz"));
            await RunMysqlDump(host, port, user, pass, "realmd", Path.Combine(dir, "players_realmd.sql.gz"));
            return new GroupResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Players backup failed");
            return new GroupResult { Success = false, Error = ex.Message };
        }
    }

    private async Task<GroupResult> BackupCore(string dir)
    {
        try
        {
            var confPath = _settings.MangosdConfPath ?? "/home/wowvmangos/vmangos/run/etc/mangosd.conf";
            var outputPath = Path.Combine(dir, "core_source.tar.gz");

            // Build the tar command — include src/, sql/, and mangosd.conf
            var args = new List<string> { "czf", outputPath };

            if (Directory.Exists(SourceRoot))
                args.AddRange(new[] { "-C", Path.GetDirectoryName(SourceRoot)!, Path.GetFileName(SourceRoot) });

            if (Directory.Exists(SqlRoot))
                args.AddRange(new[] { "-C", Path.GetDirectoryName(SqlRoot)!, Path.GetFileName(SqlRoot) });

            if (System.IO.File.Exists(confPath))
                args.AddRange(new[] { "-C", Path.GetDirectoryName(confPath)!, Path.GetFileName(confPath) });

            await RunProcess("tar", string.Join(" ", args.Select(a => $"\"{a}\"")));
            return new GroupResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Core backup failed");
            return new GroupResult { Success = false, Error = ex.Message };
        }
    }

    // ===================== RESTORE IMPLEMENTATIONS =====================

    private async Task RestoreWorld(string dir)
    {
        var (host, port, user, pass) = ParseConnectionString("Mangos");

        var mangosPath = Path.Combine(dir, "world_mangos.sql.gz");
        var adminPath = Path.Combine(dir, "world_vmangos_admin.sql.gz");

        if (!System.IO.File.Exists(mangosPath))
            throw new FileNotFoundException("world_mangos.sql.gz not found in backup");

        // Stop mangosd for clean restore
        try { await _proc.StopMangosdAsync(); } catch { /* may not be running */ }
        await Task.Delay(2000);

        try
        {
            await RunMysqlRestore(host, port, user, pass, "mangos", mangosPath);
            if (System.IO.File.Exists(adminPath))
                await RunMysqlRestore(host, port, user, pass, "vmangos_admin", adminPath);
        }
        finally
        {
            try { await _proc.StartMangosdAsync(); } catch (Exception ex) { _logger.LogError(ex, "Failed to restart mangosd after restore"); }
        }
    }

    private async Task RestorePlayers(string dir)
    {
        var (host, port, user, pass) = ParseConnectionString("Characters");

        var charsPath = Path.Combine(dir, "players_characters.sql.gz");
        var realmPath = Path.Combine(dir, "players_realmd.sql.gz");

        if (!System.IO.File.Exists(charsPath))
            throw new FileNotFoundException("players_characters.sql.gz not found in backup");

        try { await _proc.StopMangosdAsync(); } catch { }
        await Task.Delay(2000);

        try
        {
            await RunMysqlRestore(host, port, user, pass, "characters", charsPath);
            if (System.IO.File.Exists(realmPath))
                await RunMysqlRestore(host, port, user, pass, "realmd", realmPath);
        }
        finally
        {
            try { await _proc.StartMangosdAsync(); } catch (Exception ex) { _logger.LogError(ex, "Failed to restart mangosd after restore"); }
        }
    }

    private async Task RestoreCore(string dir)
    {
        var archivePath = Path.Combine(dir, "core_source.tar.gz");
        if (!System.IO.File.Exists(archivePath))
            throw new FileNotFoundException("core_source.tar.gz not found in backup");

        // Extract src/ back to parent of SourceRoot
        var srcParent = Path.GetDirectoryName(SourceRoot) ?? "/home/wowvmangos/vmangos";
        await RunProcess("tar", $"xzf \"{archivePath}\" -C \"{srcParent}\"");
    }

    // ===================== SHELL HELPERS =====================

    private async Task RunMysqlDump(string host, string port, string user, string pass, string database, string outputPath)
    {
        // mysqldump | gzip > file
        var cmd = $"mysqldump -h{host} -P{port} -u{user} -p{pass} --single-transaction --routines --triggers {database} | gzip > \"{outputPath}\"";
        await RunBash(cmd);
    }

    private async Task RunMysqlRestore(string host, string port, string user, string pass, string database, string inputPath)
    {
        // Drop and recreate, then import
        var dropCreate = $"mysql -h{host} -P{port} -u{user} -p{pass} -e \"DROP DATABASE IF EXISTS \\`{database}\\`; CREATE DATABASE \\`{database}\\`;\"";
        await RunBash(dropCreate);

        var restore = $"gunzip < \"{inputPath}\" | mysql -h{host} -P{port} -u{user} -p{pass} {database}";
        await RunBash(restore);
    }

    private async Task RunBash(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start bash");

        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        // mysqldump writes warnings to stderr even on success — only fail on non-zero exit
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Command failed (exit {proc.ExitCode}): {stderr.Trim()}");
    }

    private async Task RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");

        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} failed (exit {proc.ExitCode}): {stderr.Trim()}");
    }

    // ===================== STATS =====================

    private async Task<Dictionary<string, object>> GatherStats()
    {
        var stats = new Dictionary<string, object>();
        try
        {
            using var mangos = _db.Mangos();
            stats["customItems"] = await mangos.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM item_template WHERE entry >= 900000 AND entry < 950000");
            stats["lootifierItems"] = await mangos.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM item_template WHERE entry >= 950000");
            stats["totalItems"] = await mangos.ExecuteScalarAsync<int>(
                "SELECT COUNT(DISTINCT entry) FROM item_template");

            // Modified spells/GOs (only if baseline exists)
            using var admin = _db.Admin();
            var hasBaseline = await admin.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM og_baseline_meta") > 0;
            stats["baselineInitialized"] = hasBaseline;

            stats["auditLogRows"] = await admin.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_log");

            using var chars = _db.Characters();
            stats["totalCharacters"] = await chars.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM characters");

            using var realmd = _db.Realmd();
            stats["totalAccounts"] = await realmd.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM account");

            // Core source info
            stats["sourceExists"] = Directory.Exists(SourceRoot);
            if (Directory.Exists(SourceRoot))
            {
                var srcInfo = new DirectoryInfo(SourceRoot);
                stats["sourceFiles"] = srcInfo.GetFiles("*", SearchOption.AllDirectories).Length;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to gather some backup stats");
            stats["error"] = ex.Message;
        }
        return stats;
    }

    // ===================== HELPERS =====================

    private (string host, string port, string user, string pass) ParseConnectionString(string name)
    {
        var cs = _config.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Connection string '{name}' not found");

        var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLower(), p => p[1].Trim());

        return (
            parts.GetValueOrDefault("server", "127.0.0.1"),
            parts.GetValueOrDefault("port", "3306"),
            parts.GetValueOrDefault("user", "mangos"),
            parts.GetValueOrDefault("password", "mangos")
        );
    }

    private void EnsureBackupDir()
    {
        if (!Directory.Exists(BackupRoot))
            Directory.CreateDirectory(BackupRoot);
    }

    private static long GetDirectorySize(DirectoryInfo dir)
    {
        return dir.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        if (bytes < 1024 * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("F1") + " GB";
    }

    // ===================== REQUEST DTOs =====================

    public class BackupRequest
    {
        public string[]? Groups { get; set; }
        public string? Label { get; set; }
    }

    public class RestoreRequest
    {
        public string? Folder { get; set; }
        public string? Group { get; set; }
    }

    public class DeleteRequest
    {
        public string? Folder { get; set; }
    }

    public class UpdateLabelRequest
    {
        public string? Folder { get; set; }
        public string? Label { get; set; }
    }

    private class GroupResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
    }
}
