using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MangosSuperUI.Services;

public class ProcessManagerService
{
    private readonly IOptionsMonitor<VmangosSettings> _settingsMonitor;
    private readonly ILogger<ProcessManagerService> _logger;

    // Cache the auto-detected process names so we don't re-scan /proc every poll
    private string? _resolvedMangosdName;
    private string? _resolvedRealmdName;
    private DateTime _lastResolveScan = DateTime.MinValue;
    private static readonly TimeSpan ResolveCacheDuration = TimeSpan.FromSeconds(30);

    public ProcessManagerService(IOptionsMonitor<VmangosSettings> settings, ILogger<ProcessManagerService> logger)
    {
        _settingsMonitor = settings;
        _logger = logger;
    }

    private VmangosSettings Settings => _settingsMonitor.CurrentValue;

    public ProcessStatus GetMangosdStatus() => GetProcessStatus("mangosd", Settings.MangosdProcess);
    public ProcessStatus GetRealmdStatus() => GetProcessStatus("realmd", Settings.RealmdProcess);

    public async Task<string> StartMangosdAsync() => await RunSystemctlAsync("start", "mangosd");
    public async Task<string> StopMangosdAsync() => await RunSystemctlAsync("stop", "mangosd");
    public async Task<string> RestartMangosdAsync() => await RunSystemctlAsync("restart", "mangosd");

    public async Task<string> StartRealmdAsync() => await RunSystemctlAsync("start", "realmd");
    public async Task<string> StopRealmdAsync() => await RunSystemctlAsync("stop", "realmd");
    public async Task<string> RestartRealmdAsync() => await RunSystemctlAsync("restart", "realmd");

    /// <summary>
    /// Returns diagnostics about process detection — what name was configured,
    /// what was actually found, and how it was resolved.
    /// </summary>
    public ProcessDiagnostics GetDiagnostics()
    {
        var diag = new ProcessDiagnostics
        {
            ConfiguredMangosd = Settings.MangosdProcess,
            ConfiguredRealmd = Settings.RealmdProcess
        };

        // Force a fresh scan for diagnostics
        _lastResolveScan = DateTime.MinValue;

        var mangosdStatus = GetProcessStatus("mangosd", Settings.MangosdProcess);
        var realmdStatus = GetProcessStatus("realmd", Settings.RealmdProcess);

        diag.ResolvedMangosd = _resolvedMangosdName;
        diag.ResolvedRealmd = _resolvedRealmdName;
        diag.MangosdRunning = mangosdStatus.IsRunning;
        diag.RealmdRunning = realmdStatus.IsRunning;
        diag.MangosdPid = mangosdStatus.Pid;
        diag.RealmdPid = realmdStatus.Pid;

        // Check if configured name matches resolved name
        if (mangosdStatus.IsRunning && _resolvedMangosdName != null
            && !string.Equals(Settings.MangosdProcess, _resolvedMangosdName, StringComparison.Ordinal))
        {
            diag.MangosdNameMismatch = true;
            diag.MangosdHint = $"Configured as '{Settings.MangosdProcess}' but /proc reports '{_resolvedMangosdName}'. "
                + "Update the process name in Settings or it may show offline on some systems.";
        }

        if (realmdStatus.IsRunning && _resolvedRealmdName != null
            && !string.Equals(Settings.RealmdProcess, _resolvedRealmdName, StringComparison.Ordinal))
        {
            diag.RealmdNameMismatch = true;
            diag.RealmdHint = $"Configured as '{Settings.RealmdProcess}' but /proc reports '{_resolvedRealmdName}'. "
                + "Update the process name in Settings or it may show offline on some systems.";
        }

        return diag;
    }

    private async Task<string> RunSystemctlAsync(string action, string unit)
    {
        _logger.LogInformation("Running systemctl {Action} {Unit}", action, unit);

        var psi = new ProcessStartInfo
        {
            FileName = "sudo",
            Arguments = $"systemctl {action} {unit}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"Failed to start systemctl {action} {unit}");

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            _logger.LogError("systemctl {Action} {Unit} failed (exit {Code}): {Error}", action, unit, proc.ExitCode, stderr);
            throw new InvalidOperationException($"systemctl {action} {unit} failed: {stderr.Trim()}");
        }

        // Invalidate cached process names after start/restart so next poll re-scans
        if (action is "start" or "restart")
        {
            _lastResolveScan = DateTime.MinValue;
        }

        _logger.LogInformation("systemctl {Action} {Unit} succeeded", action, unit);
        return stdout.Trim();
    }

    /// <summary>
    /// Gets process status using a multi-strategy approach:
    /// 1. Try the configured process name via Process.GetProcessesByName
    /// 2. If not found, scan /proc for any process whose comm or cmdline contains the keyword
    /// 3. Cache the resolved name so subsequent polls are fast
    /// </summary>
    private ProcessStatus GetProcessStatus(string keyword, string configuredName)
    {
        // Strategy 1: Try configured name directly (fast path)
        try
        {
            var processes = Process.GetProcessesByName(configuredName);
            if (processes.Length > 0)
            {
                var proc = processes[0];
                UpdateResolvedName(keyword, configuredName);
                return new ProcessStatus
                {
                    IsRunning = true,
                    Pid = proc.Id,
                    ProcessName = configuredName,
                    StartTime = TryGetStartTime(proc),
                    Uptime = TryGetUptime(proc)
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetProcessesByName({Name}) failed, falling back to /proc scan", configuredName);
        }

        // Strategy 2: Try the cached resolved name (if different from configured)
        var cached = keyword == "mangosd" ? _resolvedMangosdName : _resolvedRealmdName;
        if (cached != null && cached != configuredName)
        {
            try
            {
                var processes = Process.GetProcessesByName(cached);
                if (processes.Length > 0)
                {
                    var proc = processes[0];
                    return new ProcessStatus
                    {
                        IsRunning = true,
                        Pid = proc.Id,
                        ProcessName = cached,
                        StartTime = TryGetStartTime(proc),
                        Uptime = TryGetUptime(proc)
                    };
                }
            }
            catch { }
        }

        // Strategy 3: Scan /proc (expensive — throttled to once per ResolveCacheDuration)
        if (DateTime.UtcNow - _lastResolveScan > ResolveCacheDuration)
        {
            var found = ScanProcForProcess(keyword);
            if (found != null)
            {
                UpdateResolvedName(keyword, found.Value.commName);
                _lastResolveScan = DateTime.UtcNow;

                return new ProcessStatus
                {
                    IsRunning = true,
                    Pid = found.Value.pid,
                    ProcessName = found.Value.commName,
                    StartTime = TryGetStartTimeByPid(found.Value.pid),
                    Uptime = TryGetUptimeByPid(found.Value.pid)
                };
            }
            _lastResolveScan = DateTime.UtcNow;
        }

        return new ProcessStatus { IsRunning = false };
    }

    /// <summary>
    /// Scans /proc/*/comm and /proc/*/cmdline for a process matching the keyword.
    /// This catches cases where the binary is "mangosd" but /proc/comm reports "mangosd-main",
    /// or the binary was renamed, or it runs under screen/tmux.
    /// </summary>
    private (int pid, string commName)? ScanProcForProcess(string keyword)
    {
        try
        {
            var procDirs = Directory.GetDirectories("/proc")
                .Where(d => int.TryParse(Path.GetFileName(d), out _))
                .ToArray();

            foreach (var dir in procDirs)
            {
                var pid = int.Parse(Path.GetFileName(dir));

                // Check /proc/PID/comm first (the "short" name, max 15 chars)
                var commPath = Path.Combine(dir, "comm");
                if (File.Exists(commPath))
                {
                    try
                    {
                        var comm = File.ReadAllText(commPath).Trim();
                        if (comm.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation(
                                "Process auto-detect: found {Keyword} via /proc/{Pid}/comm = '{Comm}'",
                                keyword, pid, comm);
                            return (pid, comm);
                        }
                    }
                    catch { }
                }

                // Check /proc/PID/cmdline (full command line, null-separated)
                var cmdlinePath = Path.Combine(dir, "cmdline");
                if (File.Exists(cmdlinePath))
                {
                    try
                    {
                        var cmdline = File.ReadAllText(cmdlinePath).Replace('\0', ' ').Trim();
                        // Only match on the executable name, not arguments
                        var exe = cmdline.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                        var exeName = Path.GetFileName(exe);
                        if (exeName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            // Read the actual comm name for this PID
                            var actualComm = File.Exists(commPath)
                                ? File.ReadAllText(commPath).Trim()
                                : exeName;

                            _logger.LogInformation(
                                "Process auto-detect: found {Keyword} via /proc/{Pid}/cmdline, comm='{Comm}'",
                                keyword, pid, actualComm);
                            return (pid, actualComm);
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan /proc for {Keyword}", keyword);
        }

        return null;
    }

    private void UpdateResolvedName(string keyword, string name)
    {
        if (keyword == "mangosd")
            _resolvedMangosdName = name;
        else
            _resolvedRealmdName = name;
    }

    private static DateTime? TryGetStartTime(Process proc)
    {
        try { return proc.StartTime; } catch { return null; }
    }

    private static TimeSpan? TryGetUptime(Process proc)
    {
        try { return DateTime.Now - proc.StartTime; } catch { return null; }
    }

    private static DateTime? TryGetStartTimeByPid(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            return proc.StartTime;
        }
        catch { return null; }
    }

    private static TimeSpan? TryGetUptimeByPid(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            return DateTime.Now - proc.StartTime;
        }
        catch { return null; }
    }
}

public class ProcessStatus
{
    public bool IsRunning { get; set; }
    public int? Pid { get; set; }
    public string? ProcessName { get; set; }
    public DateTime? StartTime { get; set; }
    public TimeSpan? Uptime { get; set; }
}

public class ProcessDiagnostics
{
    public string? ConfiguredMangosd { get; set; }
    public string? ConfiguredRealmd { get; set; }
    public string? ResolvedMangosd { get; set; }
    public string? ResolvedRealmd { get; set; }
    public bool MangosdRunning { get; set; }
    public bool RealmdRunning { get; set; }
    public int? MangosdPid { get; set; }
    public int? RealmdPid { get; set; }
    public bool MangosdNameMismatch { get; set; }
    public bool RealmdNameMismatch { get; set; }
    public string? MangosdHint { get; set; }
    public string? RealmdHint { get; set; }
}