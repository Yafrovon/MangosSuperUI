using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using MangosSuperUI.Models;
using Microsoft.Extensions.Options;
using Dapper;
using System.Net.Sockets;

namespace MangosSuperUI.Controllers;

public class HomeController : Controller
{
    private readonly ProcessManagerService _processManager;
    private readonly RaService _raService;
    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly DbInitializationService _dbInit;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly IOptionsMonitor<VmangosSettings> _vmangosSettings;
    private readonly IOptionsMonitor<RemoteAccessSettings> _raSettings;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        ProcessManagerService processManager,
        RaService raService,
        ConnectionFactory db,
        AuditService audit,
        DbInitializationService dbInit,
        IWebHostEnvironment env,
        IConfiguration config,
        IOptionsMonitor<VmangosSettings> vmangosSettings,
        IOptionsMonitor<RemoteAccessSettings> raSettings,
        ILogger<HomeController> logger)
    {
        _processManager = processManager;
        _raService = raService;
        _db = db;
        _audit = audit;
        _dbInit = dbInit;
        _env = env;
        _config = config;
        _vmangosSettings = vmangosSettings;
        _raSettings = raSettings;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Status()
    {
        var mangosd = _processManager.GetMangosdStatus();
        var realmd = _processManager.GetRealmdStatus();

        // Parse .server info from RA for live data
        string serverInfoRaw = null;
        int playersOnline = 0;
        int maxOnline = 0;
        string uptime = null;
        string coreRevision = null;

        try
        {
            if (_raService.IsConnected || mangosd.IsRunning)
            {
                serverInfoRaw = await _raService.SendCommandAsync(".server info");
                ParseServerInfo(serverInfoRaw, out playersOnline, out maxOnline, out uptime, out coreRevision);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get .server info from RA");
        }

        // DB stats
        int totalAccounts = 0;
        int totalCharacters = 0;
        int gmAccounts = 0;
        int bannedAccounts = 0;

        try
        {
            using var realmdConn = _db.Realmd();
            totalAccounts = await realmdConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM account");
            gmAccounts = await realmdConn.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT id) FROM account_access WHERE gmlevel > 0");
            bannedAccounts = await realmdConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM account_banned WHERE active = 1");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query realmd stats");
        }

        try
        {
            using var charConn = _db.Characters();
            totalCharacters = await charConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM characters");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query characters stats");
        }

        return Json(new
        {
            mangosd,
            realmd,
            raConnected = _raService.IsConnected,
            playersOnline,
            maxOnline,
            uptime,
            coreRevision,
            serverInfoRaw,
            totalAccounts,
            totalCharacters,
            gmAccounts,
            bannedAccounts
        });
    }

    /// <summary>
    /// Returns per-database connectivity and vmangos_admin init status.
    /// Polled once on dashboard load (not every 10s — this is heavier than Status).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DbHealth()
    {
        var report = await _dbInit.CheckHealthAsync();
        return Json(report);
    }

    /// <summary>
    /// Comprehensive self-diagnosis endpoint. Probes every subsystem and returns
    /// specific error reasons and fix suggestions — not just red/green.
    /// Called on demand via a "Diagnose" button on the dashboard.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Diagnose()
    {
        var checks = new List<DiagnosticCheck>();
        var ra = _raSettings.CurrentValue;
        var vm = _vmangosSettings.CurrentValue;

        // ── 1. First-run detection ──
        var configPath = Path.Combine(_env.ContentRootPath, "server-config.json");
        var hasOverride = System.IO.File.Exists(configPath);
        checks.Add(new DiagnosticCheck
        {
            Category = "config",
            Name = "Configuration Override",
            Status = hasOverride ? "ok" : "warning",
            Detail = hasOverride
                ? $"server-config.json found"
                : "No server-config.json — using appsettings.json defaults. Run the setup script or configure via Settings.",
            Fix = hasOverride ? null : "Go to Settings and save, or run: sudo bash setup-mangossuperui.sh"
        });

        // Check for placeholder RA credentials
        var raIsDefault = string.IsNullOrEmpty(ra.Username)
            || ra.Username == "ADMIN"
            || ra.Password == "CHANGE_ME";
        if (raIsDefault)
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "config",
                Name = "RA Credentials",
                Status = "error",
                Detail = "RA username/password appear to be defaults or empty.",
                Fix = "Create an RA account in the mangosd console (.account create NAME PASS) then update Settings."
            });
        }

        // ── 2. Process detection ──
        var procDiag = _processManager.GetDiagnostics();

        checks.Add(new DiagnosticCheck
        {
            Category = "process",
            Name = "World Server (mangosd)",
            Status = procDiag.MangosdRunning ? (procDiag.MangosdNameMismatch ? "warning" : "ok") : "error",
            Detail = procDiag.MangosdRunning
                ? $"Running as '{procDiag.ResolvedMangosd}' (PID {procDiag.MangosdPid})"
                : "Not running",
            Fix = procDiag.MangosdRunning
                ? procDiag.MangosdHint
                : "Start it: sudo systemctl start mangosd"
        });

        checks.Add(new DiagnosticCheck
        {
            Category = "process",
            Name = "Auth Server (realmd)",
            Status = procDiag.RealmdRunning ? (procDiag.RealmdNameMismatch ? "warning" : "ok") : "error",
            Detail = procDiag.RealmdRunning
                ? $"Running as '{procDiag.ResolvedRealmd}' (PID {procDiag.RealmdPid})"
                : "Not running",
            Fix = procDiag.RealmdRunning
                ? procDiag.RealmdHint
                : "Start it: sudo systemctl start realmd"
        });

        // ── 3. RA connectivity ──
        string raStatus = "ok";
        string raDetail;
        string? raFix = null;

        if (raIsDefault)
        {
            raStatus = "error";
            raDetail = "Credentials not configured";
            raFix = "Set RA username/password in Settings.";
        }
        else
        {
            try
            {
                // Test TCP connectivity first
                using var tcpTest = new TcpClient();
                var connectTask = tcpTest.ConnectAsync(ra.Host, ra.Port);
                if (await Task.WhenAny(connectTask, Task.Delay(3000)) == connectTask && tcpTest.Connected)
                {
                    // Port is open — check if RA is actually responding
                    if (_raService.IsConnected)
                    {
                        raDetail = $"Connected to {ra.Host}:{ra.Port} as '{ra.Username}'";
                    }
                    else
                    {
                        // Port open but not authenticated — likely auth failure
                        raStatus = "warning";
                        raDetail = $"Port {ra.Port} is open but RA not authenticated. Possible causes: wrong password, "
                            + "Ra.MinLevel not set in mangosd.conf, or account created via SQL instead of console.";
                        raFix = "Verify: 1) Ra.MinLevel = 3 exists in mangosd.conf (not just Ra.MinAccountLevel). "
                            + "2) Account was created via '.account create' in the mangosd console (not raw SQL). "
                            + "3) Username/password match exactly.";
                    }
                }
                else
                {
                    raStatus = "error";
                    raDetail = $"Cannot connect to {ra.Host}:{ra.Port} — connection refused or timed out.";
                    raFix = procDiag.MangosdRunning
                        ? "mangosd is running but RA port is closed. Check Ra.Enable = 1 in mangosd.conf and restart mangosd."
                        : "mangosd is not running. Start it first: sudo systemctl start mangosd";
                }
            }
            catch (Exception ex)
            {
                raStatus = "error";
                raDetail = $"RA connection test failed: {ex.Message}";
                raFix = "Check that mangosd is running and RA is enabled in mangosd.conf.";
            }
        }

        checks.Add(new DiagnosticCheck
        {
            Category = "ra",
            Name = "Remote Access (RA)",
            Status = raStatus,
            Detail = raDetail,
            Fix = raFix
        });

        // ── 4. Database connections ──
        var dbNames = new[] {
            ("Mangos", "mangos"),
            ("Characters", "characters"),
            ("Realmd", "realmd"),
            ("Logs", "logs"),
            ("Admin", "vmangos_admin")
        };

        foreach (var (connName, label) in dbNames)
        {
            var connStr = _config.GetConnectionString(connName);
            if (string.IsNullOrEmpty(connStr))
            {
                checks.Add(new DiagnosticCheck
                {
                    Category = "database",
                    Name = $"Database: {label}",
                    Status = "error",
                    Detail = "Connection string not configured.",
                    Fix = "Configure in Settings or run the setup script."
                });
                continue;
            }

            try
            {
                using var conn = new MySqlConnector.MySqlConnection(connStr);
                await conn.OpenAsync();
                var tableCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()");
                checks.Add(new DiagnosticCheck
                {
                    Category = "database",
                    Name = $"Database: {label}",
                    Status = "ok",
                    Detail = $"Connected ({tableCount} tables)"
                });
            }
            catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.AccessDenied)
            {
                checks.Add(new DiagnosticCheck
                {
                    Category = "database",
                    Name = $"Database: {label}",
                    Status = "error",
                    Detail = $"Access denied for the configured user.",
                    Fix = label == "vmangos_admin"
                        ? "Run: sudo mysql -e \"CREATE DATABASE IF NOT EXISTS vmangos_admin; GRANT ALL ON vmangos_admin.* TO 'mangos'@'localhost'; FLUSH PRIVILEGES;\""
                        : "Check the username and password in the connection string."
                });
            }
            catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.UnknownDatabase)
            {
                checks.Add(new DiagnosticCheck
                {
                    Category = "database",
                    Name = $"Database: {label}",
                    Status = "error",
                    Detail = $"Database '{label}' does not exist.",
                    Fix = label == "vmangos_admin"
                        ? "It will be auto-created on next restart if the user has CREATE privileges, or run: sudo mysql -e \"CREATE DATABASE vmangos_admin;\""
                        : "VMaNGOS databases may not be initialized. Check the VMaNGOS installation."
                });
            }
            catch (Exception ex)
            {
                checks.Add(new DiagnosticCheck
                {
                    Category = "database",
                    Name = $"Database: {label}",
                    Status = "error",
                    Detail = $"Connection failed: {ex.Message}",
                    Fix = "Check that MariaDB/MySQL is running (sudo systemctl status mariadb) and the connection string is correct."
                });
            }
        }

        // ── 5. Paths ──
        CheckPath(checks, "DBC Directory", vm.DbcPath, required: true,
            filePattern: "*.dbc", expectedMin: 50,
            detail: "Spell/Item browsers need DBC files for icon resolution.",
            fix: "Set Vmangos:DbcPath in Settings to your VMaNGOS data/5875/dbc directory.");

        CheckPath(checks, "Maps Directory", vm.MapsDataPath, required: false,
            filePattern: "*.map", expectedMin: 100,
            detail: "World Map terrain Z-resolution. Without this, objects spawn at Z=0.",
            fix: "Set Vmangos:MapsDataPath in Settings to your VMaNGOS data/maps directory.");

        CheckPath(checks, "Bin Directory", vm.BinDirectory, required: true,
            detail: "VMaNGOS binary directory.",
            fix: "Set Vmangos:BinDirectory in Settings.");

        CheckPath(checks, "mangosd.conf", vm.MangosdConfPath, required: true, isFile: true,
            detail: "Config Editor page reads this file.",
            fix: "Set Vmangos:MangosdConfPath in Settings. Find it: find / -name mangosd.conf 2>/dev/null");

        // Check for mangosd.conf RA settings if the file exists
        if (!string.IsNullOrEmpty(vm.MangosdConfPath) && System.IO.File.Exists(vm.MangosdConfPath))
        {
            try
            {
                var confText = System.IO.File.ReadAllText(vm.MangosdConfPath);
                var hasRaMinLevel = confText.Contains("Ra.MinLevel", StringComparison.OrdinalIgnoreCase)
                    && !confText.Contains("#Ra.MinLevel", StringComparison.OrdinalIgnoreCase);

                if (!hasRaMinLevel)
                {
                    checks.Add(new DiagnosticCheck
                    {
                        Category = "config",
                        Name = "mangosd.conf: Ra.MinLevel",
                        Status = "error",
                        Detail = "Ra.MinLevel is missing from mangosd.conf. RA authentication will always fail without it.",
                        Fix = "Add 'Ra.MinLevel = 3' to mangosd.conf (after Ra.Restricted) and restart mangosd. "
                            + "The config documents Ra.MinAccountLevel but the VMaNGOS code reads Ra.MinLevel — this is a known gap."
                    });
                }

                var hasRaEnabled = System.Text.RegularExpressions.Regex.IsMatch(confText, @"^Ra\.Enable\s*=\s*1",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
                if (!hasRaEnabled)
                {
                    checks.Add(new DiagnosticCheck
                    {
                        Category = "config",
                        Name = "mangosd.conf: Ra.Enable",
                        Status = "error",
                        Detail = "Ra.Enable is not set to 1. RA connections will be refused.",
                        Fix = "Set Ra.Enable = 1 in mangosd.conf and restart mangosd."
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read mangosd.conf for RA diagnostics");
            }
        }

        // ── 6. Static assets ──
        var wwwroot = Path.Combine(_env.ContentRootPath, "wwwroot");

        CheckAssetDir(checks, "Icons", Path.Combine(wwwroot, "icons"), "*.png", 2600,
            "Item/spell icon images for browsers.");
        CheckAssetDir(checks, "Game Object Models", Path.Combine(wwwroot, "models"), "*.glb", 900,
            "3D model previews on the Game Objects page.");
        CheckAssetDir(checks, "Item Models", Path.Combine(wwwroot, "item_models"), "*.glb", 100,
            "3D model previews on the Items page.");

        // Check icon case
        var iconsDir = Path.Combine(wwwroot, "icons");
        if (Directory.Exists(iconsDir))
        {
            var upperCount = Directory.GetFiles(iconsDir, "*.png")
                .Count(f => Path.GetFileName(f).Any(char.IsUpper));
            if (upperCount > 0)
            {
                checks.Add(new DiagnosticCheck
                {
                    Category = "assets",
                    Name = "Icon Filename Case",
                    Status = "warning",
                    Detail = $"{upperCount} icon files have uppercase characters. They won't load on Linux (case-sensitive filesystem).",
                    Fix = $"Run: cd {iconsDir} && for f in *; do mv \"$f\" \"$(echo \"$f\" | tr '[:upper:]' '[:lower:]')\" 2>/dev/null; done"
                });
            }
        }

        // ── Summary ──
        var errorCount = checks.Count(c => c.Status == "error");
        var warnCount = checks.Count(c => c.Status == "warning");
        var isFirstRun = !hasOverride && raIsDefault;

        return Json(new
        {
            checks,
            summary = new
            {
                total = checks.Count,
                ok = checks.Count(c => c.Status == "ok"),
                warnings = warnCount,
                errors = errorCount,
                isFirstRun,
                overallStatus = errorCount > 0 ? "error" : warnCount > 0 ? "warning" : "ok"
            }
        });
    }

    private static void CheckPath(List<DiagnosticCheck> checks, string name, string? path,
        bool required, string? filePattern = null, int expectedMin = 0,
        string? detail = null, string? fix = null, bool isFile = false)
    {
        if (string.IsNullOrEmpty(path))
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "paths",
                Name = name,
                Status = required ? "error" : "info",
                Detail = $"Not configured. {detail ?? ""}",
                Fix = fix
            });
            return;
        }

        var exists = isFile ? System.IO.File.Exists(path) : Directory.Exists(path);
        if (!exists)
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "paths",
                Name = name,
                Status = required ? "error" : "warning",
                Detail = $"Path does not exist: {path}",
                Fix = fix
            });
            return;
        }

        if (filePattern != null && !isFile)
        {
            var count = Directory.GetFiles(path, filePattern).Length;
            if (count < expectedMin)
            {
                checks.Add(new DiagnosticCheck
                {
                    Category = "paths",
                    Name = name,
                    Status = "warning",
                    Detail = $"{count} {filePattern} files found (expected {expectedMin}+). Path: {path}",
                    Fix = fix
                });
                return;
            }

            checks.Add(new DiagnosticCheck
            {
                Category = "paths",
                Name = name,
                Status = "ok",
                Detail = $"{count} {filePattern} files. Path: {path}"
            });
        }
        else
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "paths",
                Name = name,
                Status = "ok",
                Detail = $"Found: {path}"
            });
        }
    }

    private static void CheckAssetDir(List<DiagnosticCheck> checks, string name, string path,
        string pattern, int expectedMin, string purpose)
    {
        if (!Directory.Exists(path))
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "assets",
                Name = name,
                Status = "info",
                Detail = $"Directory not present. {purpose} Run the MangosSuperUI Extractor to populate.",
                Fix = $"mkdir -p {path}"
            });
            return;
        }

        var count = Directory.GetFiles(path, pattern).Length;
        checks.Add(new DiagnosticCheck
        {
            Category = "assets",
            Name = name,
            Status = count >= expectedMin ? "ok" : (count > 0 ? "warning" : "info"),
            Detail = count > 0
                ? $"{count} files ({(count >= expectedMin ? "good" : $"expected {expectedMin}+")})"
                : $"Directory exists but empty. {purpose}"
        });
    }

    [HttpPost]
    public async Task<IActionResult> SendCommand([FromBody] CommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Command))
            return BadRequest(new { error = "Command cannot be empty" });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _raService, request.Command, operatorIp: ip);

        return Json(new { success, response = success ? response : null, error = success ? null : response });
    }

    [HttpPost]
    public async Task<IActionResult> ProcessAction([FromBody] ProcessActionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Service) || string.IsNullOrWhiteSpace(request?.Action))
            return BadRequest(new { error = "Service and action are required" });

        try
        {
            var result = (request.Service.ToLower(), request.Action.ToLower()) switch
            {
                ("mangosd", "start") => await _processManager.StartMangosdAsync(),
                ("mangosd", "stop") => await _processManager.StopMangosdAsync(),
                ("mangosd", "restart") => await _processManager.RestartMangosdAsync(),
                ("realmd", "start") => await _processManager.StartRealmdAsync(),
                ("realmd", "stop") => await _processManager.StopRealmdAsync(),
                ("realmd", "restart") => await _processManager.RestartRealmdAsync(),
                _ => throw new ArgumentException($"Unknown service/action: {request.Service}/{request.Action}")
            };

            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "system",
                Action = $"process_{request.Action}",
                TargetType = "service",
                TargetName = request.Service,
                Success = true,
                Notes = $"systemctl {request.Action} {request.Service}"
            });

            return Json(new { success = true, message = $"{request.Service} {request.Action} completed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessAction failed: {Service}/{Action}", request.Service, request.Action);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "system",
                Action = $"process_{request.Action}",
                TargetType = "service",
                TargetName = request.Service,
                Success = false,
                Notes = ex.Message
            });
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Parses VMaNGOS .server info response.
    /// </summary>
    private static void ParseServerInfo(string raw, out int playersOnline, out int maxOnline, out string uptime, out string coreRevision)
    {
        playersOnline = 0;
        maxOnline = 0;
        uptime = null;
        coreRevision = null;

        if (string.IsNullOrEmpty(raw))
            return;

        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Core revision:"))
            {
                coreRevision = trimmed["Core revision:".Length..].Trim();
            }
            else if (trimmed.StartsWith("Players online:"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(trimmed,
                    @"Players online:\s*(\d+).*Max online:\s*(\d+)");
                if (match.Success)
                {
                    int.TryParse(match.Groups[1].Value, out playersOnline);
                    int.TryParse(match.Groups[2].Value, out maxOnline);
                }
            }
            else if (trimmed.StartsWith("Server uptime:"))
            {
                uptime = trimmed["Server uptime:".Length..].Trim().TrimEnd('.');
            }
        }
    }
}

public class DiagnosticCheck
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "ok"; // ok, warning, error, info
    public string Detail { get; set; } = "";
    public string? Fix { get; set; }
}

public class CommandRequest
{
    public string Command { get; set; } = "";
}

public class ProcessActionRequest
{
    public string Service { get; set; } = "";
    public string Action { get; set; } = "";
}