using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using MangosSuperUI.Models;
using Dapper;

namespace MangosSuperUI.Controllers;

public class AccountsController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly RaService _raService;
    private readonly AuditService _audit;
    private readonly ILogger<AccountsController> _logger;

    public AccountsController(ConnectionFactory db, RaService raService, AuditService audit, ILogger<AccountsController> logger)
    {
        _db = db;
        _raService = raService;
        _audit = audit;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Paginated account list with search and filters.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? q = null,
        [FromQuery] int? gmLevel = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Realmd();

            var where = new List<string>();
            var parameters = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(q))
            {
                where.Add("(a.username LIKE @term OR a.last_ip LIKE @term)");
                parameters.Add("term", "%" + q.Trim() + "%");
            }

            if (gmLevel.HasValue)
            {
                where.Add("a.gmlevel = @gmLevel");
                parameters.Add("gmLevel", gmLevel.Value);
            }

            if (status == "banned")
            {
                where.Add("EXISTS (SELECT 1 FROM account_banned ab WHERE ab.id = a.id AND ab.active = 1)");
            }
            else if (status == "muted")
            {
                where.Add("a.mutetime > UNIX_TIMESTAMP()");
            }
            else if (status == "locked")
            {
                where.Add("a.locked = 1");
            }
            else if (status == "online")
            {
                where.Add("a.online = 1");
            }
            else if (status == "gm")
            {
                where.Add("a.gmlevel > 0");
            }

            var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

            // Total count
            var countSql = $"SELECT COUNT(*) FROM account a {whereClause}";
            var total = await conn.ExecuteScalarAsync<int>(countSql, parameters);

            // Page data
            var offset = (page - 1) * pageSize;
            parameters.Add("limit", pageSize);
            parameters.Add("offset", offset);

            var dataSql = $@"
                SELECT a.id, a.username, a.last_ip AS lastIp, a.last_login AS lastLogin,
                       a.gmlevel AS gmLevel, a.locked, a.mutetime AS muteTime, a.online,
                       a.email, a.os, a.platform,
                       (SELECT COUNT(*) FROM characters.characters c WHERE c.account = a.id) AS characterCount,
                       EXISTS (SELECT 1 FROM account_banned ab WHERE ab.id = a.id AND ab.active = 1) AS isBanned
                FROM account a
                {whereClause}
                ORDER BY a.id ASC
                LIMIT @limit OFFSET @offset";

            var accounts = await conn.QueryAsync<AccountListRow>(dataSql, parameters);

            return Json(new
            {
                total,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling((double)total / pageSize),
                accounts = accounts.Select(a => new
                {
                    a.Id,
                    a.Username,
                    a.LastIp,
                    lastLogin = a.LastLogin.Year > 2000 ? a.LastLogin.ToString("yyyy-MM-dd HH:mm") : "Never",
                    a.GmLevel,
                    gmLevelName = GetGmLevelName(a.GmLevel),
                    a.Locked,
                    isMuted = a.MuteTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    a.IsBanned,
                    a.Online,
                    a.Email,
                    a.Os,
                    a.Platform,
                    a.CharacterCount
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Account list query failed");
            return Json(new { total = 0, accounts = Array.Empty<object>() });
        }
    }

    /// <summary>
    /// Summary stats for the filter bar.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Summary()
    {
        try
        {
            using var conn = _db.Realmd();

            // Typed result — MySQL returns COUNT(*) as long and SUM(CASE...) as decimal.
            // Casting those off a dynamic via (int) throws at runtime, which was making
            // every badge come back as 0 via the catch block.
            var stats = await conn.QueryFirstAsync<SummaryRow>(@"
                SELECT 
                    COUNT(*) AS Total,
                    SUM(CASE WHEN online = 1 THEN 1 ELSE 0 END) AS Online,
                    SUM(CASE WHEN gmlevel > 0 THEN 1 ELSE 0 END) AS Gm,
                    SUM(CASE WHEN locked = 1 THEN 1 ELSE 0 END) AS Locked,
                    SUM(CASE WHEN mutetime > UNIX_TIMESTAMP() THEN 1 ELSE 0 END) AS Muted
                FROM account");

            var banned = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(DISTINCT id) FROM account_banned WHERE active = 1");

            return Json(new
            {
                total = (int)stats.Total,
                online = (int)stats.Online,
                gm = (int)stats.Gm,
                locked = (int)stats.Locked,
                muted = (int)stats.Muted,
                banned
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Account summary failed");
            return Json(new { total = 0, online = 0, gm = 0, locked = 0, muted = 0, banned = 0 });
        }
    }

    /// <summary>
    /// Full account detail including characters and ban history.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Detail([FromQuery] int id)
    {
        try
        {
            using var realmdConn = _db.Realmd();
            var account = await realmdConn.QueryFirstOrDefaultAsync<AccountDetailRow>(
                @"SELECT id, username, last_ip AS lastIp, last_login AS lastLogin,
                         gmlevel AS gmLevel, locked, mutetime AS muteTime, online,
                         email, os, platform, joindate AS joinDate
                  FROM account WHERE id = @id",
                new { id });

            if (account == null)
                return Json(new { found = false });

            // Characters
            using var charConn = _db.Characters();
            var characters = await charConn.QueryAsync<AccountCharacter>(
                @"SELECT guid, name, level, race, class AS classId, gender, online,
                         played_time_total AS playedTotal, logout_time AS logoutTime
                  FROM characters WHERE account = @id ORDER BY level DESC",
                new { id });

            // Ban history
            var bans = await realmdConn.QueryAsync<BanHistoryRow>(
                @"SELECT bandate AS banDate, unbandate AS unbanDate, bannedby AS bannedBy,
                         banreason AS banReason, active
                  FROM account_banned WHERE id = @id ORDER BY bandate DESC",
                new { id });

            // Audit history (last 20 actions on this account)
            var auditHistory = await _audit.GetTargetHistoryAsync("account", account.Username, 20);

            return Json(new
            {
                found = true,
                account = new
                {
                    account.Id,
                    account.Username,
                    account.LastIp,
                    lastLogin = account.LastLogin.Year > 2000 ? account.LastLogin.ToString("yyyy-MM-dd HH:mm") : "Never",
                    joinDate = account.JoinDate.Year > 2000 ? account.JoinDate.ToString("yyyy-MM-dd HH:mm") : "Unknown",
                    account.GmLevel,
                    gmLevelName = GetGmLevelName(account.GmLevel),
                    account.Locked,
                    isMuted = account.MuteTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    isBanned = bans.Any(b => b.Active == 1),
                    account.Online,
                    account.Email,
                    account.Os,
                    account.Platform
                },
                characters = characters.Select(c => new
                {
                    c.Guid,
                    c.Name,
                    c.Level,
                    race = GetRaceName(c.Race),
                    className = GetClassName(c.ClassId),
                    gender = c.Gender == 0 ? "Male" : "Female",
                    c.Online,
                    playedTotal = FormatPlaytime(c.PlayedTotal),
                    lastSeen = c.Online == 1 ? "Now" :
                        (c.LogoutTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(c.LogoutTime).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "—")
                }),
                bans = bans.Select(b => new
                {
                    banDate = DateTimeOffset.FromUnixTimeSeconds(b.BanDate).LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                    unbanDate = b.UnbanDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(b.UnbanDate).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "Permanent",
                    b.BannedBy,
                    b.BanReason,
                    b.Active
                }),
                auditHistory = auditHistory.Select(a => new
                {
                    a.Timestamp,
                    a.Action,
                    a.Category,
                    a.RaCommand,
                    a.Success
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Account detail failed for id {Id}", id);
            return Json(new { found = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Send an RA command (account actions route through here).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RaCommand([FromBody] RaCommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Command))
            return BadRequest(new { error = "Command cannot be empty" });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _raService, request.Command, operatorIp: ip, notes: "Accounts Page");

        return Json(new { success, response = success ? response : null, error = success ? null : response });
    }

    // ==================== Helpers ====================

    private static string FormatPlaytime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    private static string GetGmLevelName(int level) => level switch
    {
        0 => "Player",
        1 => "Moderator",
        2 => "Ticket Master",
        3 => "Game Master",
        4 => "Basic Admin",
        5 => "Developer",
        6 => "Administrator",
        7 => "Console",
        _ => $"Unknown ({level})"
    };

    private static string GetRaceName(int race) => race switch
    {
        1 => "Human",
        2 => "Orc",
        3 => "Dwarf",
        4 => "Night Elf",
        5 => "Undead",
        6 => "Tauren",
        7 => "Gnome",
        8 => "Troll",
        _ => $"Unknown ({race})"
    };

    private static string GetClassName(int classId) => classId switch
    {
        1 => "Warrior",
        2 => "Paladin",
        3 => "Hunter",
        4 => "Rogue",
        5 => "Priest",
        7 => "Shaman",
        8 => "Mage",
        9 => "Warlock",
        11 => "Druid",
        _ => $"Unknown ({classId})"
    };
}

// ==================== DTOs ====================

public class SummaryRow
{
    public long Total { get; set; }
    public decimal Online { get; set; }
    public decimal Gm { get; set; }
    public decimal Locked { get; set; }
    public decimal Muted { get; set; }
}

public class AccountListRow
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string LastIp { get; set; } = "";
    public DateTime LastLogin { get; set; }
    public int GmLevel { get; set; }
    public int Locked { get; set; }
    public long MuteTime { get; set; }
    public int Online { get; set; }
    public string? Email { get; set; }
    public string? Os { get; set; }
    public string? Platform { get; set; }
    public int CharacterCount { get; set; }
    public bool IsBanned { get; set; }
}

public class AccountDetailRow
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string LastIp { get; set; } = "";
    public DateTime LastLogin { get; set; }
    public DateTime JoinDate { get; set; }
    public int GmLevel { get; set; }
    public int Locked { get; set; }
    public long MuteTime { get; set; }
    public int Online { get; set; }
    public string? Email { get; set; }
    public string? Os { get; set; }
    public string? Platform { get; set; }
}

public class AccountCharacter
{
    public int Guid { get; set; }
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int Race { get; set; }
    public int ClassId { get; set; }
    public int Gender { get; set; }
    public int Online { get; set; }
    public long PlayedTotal { get; set; }
    public long LogoutTime { get; set; }
}

public class BanHistoryRow
{
    public long BanDate { get; set; }
    public long UnbanDate { get; set; }
    public string BannedBy { get; set; } = "";
    public string BanReason { get; set; } = "";
    public int Active { get; set; }
}