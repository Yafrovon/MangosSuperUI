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
    private readonly MpqReaderService _mpq;
    private readonly ProcessResourceSampler _resources;
    private readonly ProcessCoreSampler _cores;
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
        MpqReaderService mpq,
        ProcessResourceSampler resources,
        ProcessCoreSampler cores,
        ILogger<HomeController> logger)
    {
        _resources = resources;
        _cores = cores;
        _processManager = processManager;
        _raService = raService;
        _db = db;
        _audit = audit;
        _dbInit = dbInit;
        _env = env;
        _config = config;
        _vmangosSettings = vmangosSettings;
        _raSettings = raSettings;
        _mpq = mpq;
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

        // CPU is a rate, so the first poll after a page load reports null and the
        // second onward reports a real figure. Sampled here, on the same 10 s poll
        // the rest of the dashboard uses, rather than on a background timer — the
        // delta window then matches what the user is actually looking at.
        var mangosdResources = _resources.Sample("mangosd", mangosd.Pid);
        var realmdResources = _resources.Sample("realmd", realmd.Pid);
        var superuiResources = _resources.SampleCurrentProcess("superui");

        // Per-core attribution. An aggregate hides the shape that matters: the
        // world loop lives on one or two cores, so "0.44 of 32" reads as idle
        // even when a map thread is close to saturating its core.
        var mangosdCores = _cores.Sample("mangosd", mangosd.Pid);
        var superuiCores = _cores.Sample("superui", Environment.ProcessId);
        var hostCores = _cores.SampleHost();

        // Parse .server info from RA for live data
        string? serverInfoRaw = null;
        int playersOnline = 0;
        int maxOnline = 0;
        string? uptime = null;
        string? coreRevision = null;

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
            resources = new
            {
                mangosd = mangosdResources,
                realmd = realmdResources,
                superui = superuiResources
            },
            cores = new
            {
                mangosd = mangosdCores,
                superui = superuiCores,
                host = hostCores
            },
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
    /// Reports the descriptor limit for a world/auth unit: what the drop-in asks
    /// for, what systemd is configured to give, and — the only one that counts —
    /// what the running process actually has.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> UnitLimits(string unit = "mangosd")
    {
        var privileged = HttpContext.RequestServices.GetRequiredService<PrivilegedOpsService>();
        int? pid = unit.Contains("realmd", StringComparison.Ordinal)
            ? _processManager.GetRealmdStatus().Pid
            : _processManager.GetMangosdStatus().Pid;

        UnitLimitsReport report = await privileged.GetLimitsAsync(unit, pid, HttpContext.RequestAborted);

        // Remediation depends on whether this install has ever run setup. An
        // upgrade drops the new binaries but does not re-run the script, so the
        // helper is absent on every pre-existing install — and those are the
        // ones still capped at 1024.
        string? remediation = null;
        if (report.LimitTooLow)
        {
            remediation = privileged.HelperInstalled
                ? "Click Raise limit. Takes effect the next time the world server restarts."
                : "Re-run the setup script to enable in-app fixes: "
                  + "sudo bash /opt/mangossuperui/Scripts/setup-mangossuperui.sh";
        }

        return Json(new
        {
            ok = report.Available,
            helperInstalled = privileged.HelperInstalled,
            canFixInApp = privileged.HelperInstalled,
            recommended = PrivilegedOpsService.RecommendedNoFile,
            restartRequired = report.RestartRequired,
            limitTooLow = report.LimitTooLow,
            approximateBotCeiling = report.ApproximateBotCeiling,
            remediation,
            report
        });
    }

    /// <summary>
    /// Writes the systemd drop-in that raises a unit's descriptor limit.
    ///
    /// Deliberately does not restart the unit: one bridge socket per bot means
    /// this governs how large the fleet can grow, and bouncing a live world
    /// server is the operator's call, through the existing restart control.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SetUnitNoFile(string unit = "mangosd", long value = 0)
    {
        var privileged = HttpContext.RequestServices.GetRequiredService<PrivilegedOpsService>();
        if (value <= 0)
            value = PrivilegedOpsService.RecommendedNoFile;

        PrivilegedOpResult result = await privileged.SetNoFileAsync(unit, value, HttpContext.RequestAborted);
        if (result.Ok)
        {
            // A privileged write to /etc belongs in the audit trail, not just the log.
            await _audit.LogAsync(new AuditEntry
            {
                Category = "system",
                Action = "set-nofile",
                TargetType = "systemd-unit",
                TargetName = unit,
                StateAfter = $"LimitNOFILE={value}",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString()
            });
            _logger.LogInformation("LimitNOFILE for {Unit} set to {Value}", unit, value);
        }

        return Json(new
        {
            ok = result.Ok,
            error = result.Error,
            output = result.Output,
            note = result.Ok ? $"Applies the next time {unit} starts." : null
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
                : (OperatingSystem.IsWindows()
                    ? "Start it from the Processes panel (launches mangosd.exe from the bin directory)."
                    : "Start it: sudo systemctl start mangosd")
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
                : (OperatingSystem.IsWindows()
                    ? "Start it from the Processes panel (launches realmd.exe from the bin directory)."
                    : "Start it: sudo systemctl start realmd")
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
                        : (OperatingSystem.IsWindows()
                            ? "mangosd is not running. Start it first from the Processes panel."
                            : "mangosd is not running. Start it first: sudo systemctl start mangosd");
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

                if (connName == "Admin")
                {
                    try
                    {
                        await conn.ExecuteAsync(@"
                            SELECT bot_guid, bot_name, queue_id, status, payload_json,
                                   spec_tab, profile_id, profile_name, active_role, active_role_name,
                                   rotation_mode, rotation_profile, rotation_name, rotation_fingerprint,
                                   reset_talents, expected_revision, observed_session_at, request_id,
                                   claim_owner, claim_expires_at, attempt_count, queued_by, queued_from,
                                   created_at, updated_at, next_attempt_at, dispatched_at, completed_at,
                                   last_code, last_message
                              FROM bot_combat_loadout_queue
                             LIMIT 0");
                        var queueIndexCount = await conn.ExecuteScalarAsync<int>(@"
                            SELECT COUNT(DISTINCT INDEX_NAME)
                              FROM information_schema.STATISTICS
                             WHERE TABLE_SCHEMA = DATABASE()
                               AND TABLE_NAME = 'bot_combat_loadout_queue'
                               AND INDEX_NAME IN
                                   ('PRIMARY', 'uq_bot_combat_loadout_queue_id',
                                    'idx_bot_combat_loadout_queue_due')");
                        if (queueIndexCount != 3)
                            throw new InvalidOperationException(
                                "bot_combat_loadout_queue is missing one or more required indexes.");
                    }
                    catch (Exception ex)
                    {
                        checks.Add(new DiagnosticCheck
                        {
                            Category = "database",
                            Name = "Database: vmangos_admin queue schema",
                            Status = "error",
                            Detail = ex.Message,
                            Fix = "Run /opt/mangossuperui/Scripts/setup-mangossuperui.sh from the current release."
                        });
                        continue;
                    }
                }

                checks.Add(new DiagnosticCheck
                {
                    Category = "database",
                    Name = $"Database: {label}",
                    Status = "ok",
                    Detail = connName == "Admin"
                        ? $"Connected ({tableCount} tables); combat-loadout queue schema ready"
                        : $"Connected ({tableCount} tables)"
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

        // ── 6. Client game data (MPQ loaders) ──
        // Icons and 3D models are decoded ON DEMAND from the WoW 1.12.1 client's
        // MPQ archives — there is no extraction step and no static asset sync.
        // The only requirement is that Vmangos:ClientDataPath points at a valid
        // client Data/ directory.
        var clientDataPath = _config["Vmangos:ClientDataPath"]
            ?? _config["SpellCreator:ClientDataPath"];

        if (string.IsNullOrWhiteSpace(clientDataPath) || !Directory.Exists(clientDataPath))
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "assets",
                Name = "Client Data Path (MPQ)",
                Status = "error",
                Detail = string.IsNullOrWhiteSpace(clientDataPath)
                    ? "Vmangos:ClientDataPath is not configured. Icons and 3D models are served on demand from the client MPQs — without this path, no icons or models will display."
                    : $"Configured client data path does not exist: {clientDataPath}. Icons and 3D models are served on demand from the client MPQs.",
                Fix = "In Settings, set 'Client Data Path' to your WoW 1.12.1 client's Data directory (the folder containing *.MPQ files), then restart the app."
            });
        }
        else
        {
            var mpqCount = Directory.GetFiles(clientDataPath, "*.MPQ").Length
                + Directory.GetFiles(clientDataPath, "*.mpq").Length;
            checks.Add(new DiagnosticCheck
            {
                Category = "assets",
                Name = "Client Data Path (MPQ)",
                Status = mpqCount > 0 ? "ok" : "error",
                Detail = mpqCount > 0
                    ? $"{mpqCount} MPQ archives at {clientDataPath}"
                    : $"Directory exists but contains no *.MPQ files: {clientDataPath}",
                Fix = mpqCount > 0 ? null
                    : "Point 'Client Data Path' in Settings at the client's Data folder itself (e.g. .../wowclient/Data), not the client root."
            });

            // Live probe: run the fallback questionmark icon through the WHOLE
            // pipeline /Icon/Get uses — MPQ extract, then BlpDecoder.ToPngBytes
            // (managed decode + SkiaSharp PNG encode). Probing only the extract
            // half reports "ok" while every icon 404s, because the native
            // SkiaSharp library can be missing on a box whose MPQs are perfect.
            // The two halves are reported separately so the detail names the
            // stage that actually broke.
            const string probeIcon = "Interface\\Icons\\INV_Misc_QuestionMark.blp";

            byte[]? probe = null;
            string? extractError = null;
            try { probe = _mpq.ExtractFile(probeIcon); }
            catch (Exception ex)
            {
                extractError = $"{ex.GetType().Name}: {ex.Message}";
                _logger.LogWarning(ex, "Diagnostics: MPQ icon probe threw");
            }

            if (probe is not { Length: > 0 })
            {
                checks.Add(new DiagnosticCheck
                {
                    Category = "assets",
                    Name = "Icon Loader (on-demand)",
                    Status = "error",
                    Detail = extractError is null
                        ? $"Could not read {probeIcon} from the mounted MPQ archives ({_mpq.ArchiveCount} mounted). Icons and models will not display."
                        : $"Reading {probeIcon} from the mounted MPQ archives threw — {extractError}",
                    Fix = "Verify the client Data directory is a complete WoW 1.12.1 install (dbc/model/texture/interface MPQs present) and restart the app."
                });
            }
            else
            {
                // Extract succeeded — now the half that used to go untested.
                byte[]? png = null;
                string? decodeError = null;
                try { png = BlpDecoder.ToPngBytes(probe); }
                catch (Exception ex)
                {
                    // The interesting message is on the INNERMOST exception: a
                    // missing native lib surfaces as TypeInitializationException
                    // wrapping DllNotFoundException, and only the inner text
                    // names the .so that failed to load (e.g. libfontconfig.so.1).
                    var root = ex;
                    while (root.InnerException is not null) root = root.InnerException;
                    decodeError = $"{root.GetType().Name}: {root.Message.Split('\n')[0].Trim()}";
                    _logger.LogWarning(ex, "Diagnostics: BLP->PNG probe threw");
                }

                var decoded = png is { Length: > 0 };
                checks.Add(new DiagnosticCheck
                {
                    Category = "assets",
                    Name = "Icon Loader (on-demand)",
                    Status = decoded ? "ok" : "error",
                    Detail = decoded
                        ? $"Icons decode from client MPQs on demand ({_mpq.ArchiveCount} archives mounted, {png!.Length:N0}-byte PNG probe)."
                        : decodeError is null
                            ? "The icon BLP read from the MPQs but produced an empty PNG. Icons and models will not display."
                            : $"The icon BLP read fine from the MPQs, but BLP→PNG conversion failed — {decodeError}. Icons and models will not display.",
                    Fix = decoded ? null
                        : decodeError?.Contains("libSkiaSharp", StringComparison.OrdinalIgnoreCase) == true
                          || decodeError?.Contains("fontconfig", StringComparison.OrdinalIgnoreCase) == true
                            ? "SkiaSharp's native library could not load. On Debian/Ubuntu install its dependency with 'sudo apt install -y libfontconfig1', then RESTART the app — .NET caches the failed type initializer for the life of the process, so the fix does not take effect until a restart."
                            : "Image conversion failed after a successful MPQ read — check the app log for the full exception under 'Diagnostics: BLP->PNG probe threw'."
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
                Notes = OperatingSystem.IsWindows()
                    ? $"process {request.Action} {request.Service}"
                    : $"systemctl {request.Action} {request.Service}"
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
    private static void ParseServerInfo(string raw, out int playersOnline, out int maxOnline, out string? uptime, out string? coreRevision)
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
