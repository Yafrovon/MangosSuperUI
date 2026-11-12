using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;

namespace MangosSuperUI.Controllers;

// ══════════════════════════════════════════════════════════════════════════
//  RatesController — Gameplay Tuning > Rates (Experience / Reputation / Honor).
//
//  Writes mangosd.conf through MangosdConfigDocument, then `.reload config`
//  over RA, then reads `.sui rates` back so the page shows what the running
//  core actually uses. Per-faction reputation lives in the world table
//  `reputation_reward_rate` + `.reload reputation_reward_rate`.
//  Catalog and core-hook notes: Models/RatesModels.cs and RATES.md.
// ══════════════════════════════════════════════════════════════════════════

public class RatesController : Controller
{
    private readonly RatesService _rates;
    private readonly RaService _ra;
    private readonly AuditService _audit;
    private readonly WorldMaintenanceGate _maintenance;
    private readonly ILogger<RatesController> _logger;

    public RatesController(RatesService rates, RaService ra, AuditService audit,
        WorldMaintenanceGate maintenance, ILogger<RatesController> logger)
    {
        _rates = rates;
        _ra = ra;
        _audit = audit;
        _maintenance = maintenance;
        _logger = logger;
    }

    public IActionResult Index() => RedirectToAction(nameof(Experience));
    public IActionResult Experience() => View();
    public IActionResult Reputation() => View();
    public IActionResult Honor() => View();

    [HttpGet]
    public async Task<IActionResult> State(string page)
    {
        if (!Enum.TryParse<RatesPage>(page, true, out var p))
            return BadRequest(new { error = $"Unknown rates page '{page}'." });
        return Json(await BuildStateAsync(p, HttpContext.RequestAborted));
    }

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] RatesSaveRequest request)
    {
        if (request == null || !Enum.TryParse<RatesPage>(request.Page, true, out var page))
            return BadRequest(new { error = "Unknown rates page." });

        await using var lease = await _maintenance.TryAcquireOperationAsync();
        if (lease == null)
            return Conflict(new { error = "A world restore/maintenance is running; try again when it finishes." });

        var beforeConf = await _rates.ReadConfAsync(page);
        List<RatesService.ConfChange> changes;
        try
        {
            changes = await _rates.WriteConfAsync(page, request.Values ?? new(StringComparer.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (changes.Count > 0)
        {
            await _audit.LogAsync(new AuditEntry
            {
                OperatorIp = ip,
                Category = "config",
                Action = "rates_update",
                TargetType = "config",
                TargetName = "mangosd.conf",
                StateBefore = JsonSerializer.Serialize(changes.ToDictionary(c => c.Key, c => beforeConf.GetValueOrDefault(c.Key))),
                StateAfter = JsonSerializer.Serialize(changes.ToDictionary(c => c.Key, c => c.To)),
                IsReversible = true,
                Success = true,
                Notes = $"Rates > {page}: {changes.Count} key(s)"
            });
        }

        string? reloadResponse = null;
        bool reloaded = false;
        if (request.Reload && changes.Count > 0)
        {
            (reloadResponse, reloaded) = await _audit.ExecuteAndLogAsync(
                _ra, ".reload config", operatorIp: ip, notes: $"Rates > {page} — apply live");
        }

        var state = await BuildStateAsync(page, HttpContext.RequestAborted);
        return Json(new
        {
            success = true,
            changes = changes.Select(c => new { key = c.Key, from = c.From, to = c.To }),
            reloaded,
            reloadResponse,
            state
        });
    }

    // ==================== Experience: leveling preview data ====================

    [HttpGet]
    public async Task<IActionResult> XpTable()
    {
        try
        {
            var rows = await _rates.ReadXpTableAsync();
            return Json(new { success = true, levels = rows.Select(r => new { level = r.Level, xp = r.Xp }) });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rates: player_xp_for_level read failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ==================== Reputation: per-faction table ====================

    [HttpGet]
    public async Task<IActionResult> Factions()
    {
        try
        {
            var rows = await _rates.ReadFactionRatesAsync();
            return Json(new { success = true, factions = rows });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rates: reputation_reward_rate read failed");
            return Json(new { success = false, error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> SaveFactions([FromBody] FactionRatesSaveRequest request)
    {
        if (request?.Rows == null || request.Rows.Count == 0)
            return BadRequest(new { error = "No faction rows submitted." });

        await using var lease = await _maintenance.TryAcquireOperationAsync();
        if (lease == null)
            return Conflict(new { error = "A world restore/maintenance is running; try again when it finishes." });

        List<object> before, after;
        try
        {
            (before, after) = await _rates.WriteFactionRatesAsync(request.Rows);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        string? reloadResponse = null;
        bool reloaded = false;
        if (after.Count > 0)
        {
            await _audit.LogAsync(new AuditEntry
            {
                OperatorIp = ip,
                Category = "world_db",
                Action = "reputation_reward_rate_update",
                TargetType = "table",
                TargetName = "reputation_reward_rate",
                StateBefore = JsonSerializer.Serialize(before),
                StateAfter = JsonSerializer.Serialize(after),
                IsReversible = true,
                Success = true,
                Notes = $"Rates > Reputation: {after.Count} faction row(s)"
            });
            if (request.Reload)
            {
                (reloadResponse, reloaded) = await _audit.ExecuteAndLogAsync(
                    _ra, ".reload reputation_reward_rate", operatorIp: ip, notes: "Rates > Reputation — apply live");
            }
        }

        return Json(new { success = true, changed = after.Count, reloaded, reloadResponse });
    }

    // ==================== helpers ====================

    private async Task<object> BuildStateAsync(RatesPage page, CancellationToken ct)
    {
        Dictionary<string, string?>? conf = null;
        string? confError = null;
        try
        {
            conf = await _rates.ReadConfAsync(page, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            confError = ex.Message;
        }

        var live = await _rates.ReadLiveAsync(ct);
        return new
        {
            page = page.ToString(),
            confPath = _rates.ConfPath,
            confError,
            live = new { live.Reachable, live.Hooks, live.Rts, live.Error },
            fields = RatesCatalog.For(page).Select(f => new
            {
                f.Key,
                f.Label,
                f.Group,
                f.Help,
                f.Default,
                f.Min,
                f.Max,
                f.Step,
                f.Hook,
                f.Master,
                f.Kind,
                f.Unit,
                conf = conf?.GetValueOrDefault(f.Key),
                live = live.Values.GetValueOrDefault(f.Key)
            })
        };
    }
}
