using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using MangosSuperUI.Models;
using Microsoft.Extensions.Options;

namespace MangosSuperUI.Services;

/// <summary>
/// Reads and writes the macro rate keys in mangosd.conf, reads back what the running
/// core actually uses (<c>.sui rates</c> over RA), and owns the per-faction
/// <c>reputation_reward_rate</c> table. Applying (reload, audit) is the controller's job.
/// </summary>
public sealed class RatesService
{
    private const string DefaultConfPath = "/home/wowvmangos/vmangos/run/etc/mangosd.conf";
    private static readonly Regex LiveLine = new(@"^\s*(Rate\.[A-Za-z.]+|RpDecay)=(.*)$", RegexOptions.Multiline);
    private static readonly Regex LiveHeader = new(@"SUI rates: hooks=(\d) rts=(\d)");

    private readonly IOptionsMonitor<VmangosSettings> _vmangos;
    private readonly RaService _ra;
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly ILogger<RatesService> _logger;

    public RatesService(IOptionsMonitor<VmangosSettings> vmangos, RaService ra, ConnectionFactory db,
        DbcService dbc, ILogger<RatesService> logger)
    {
        _vmangos = vmangos;
        _ra = ra;
        _db = db;
        _dbc = dbc;
        _logger = logger;
    }

    public string ConfPath
    {
        get
        {
            var path = _vmangos.CurrentValue.MangosdConfPath;
            return string.IsNullOrWhiteSpace(path) ? DefaultConfPath : path;
        }
    }

    // ==================== mangosd.conf ====================

    /// <summary>Current conf text for each key on the page; null when the key is absent (core default applies).</summary>
    public async Task<Dictionary<string, string?>> ReadConfAsync(RatesPage page, CancellationToken ct = default)
    {
        var doc = await MangosdConfigDocument.LoadAsync(ConfPath, ct);
        return RatesCatalog.For(page).ToDictionary(f => f.Key, f => Unquote(doc.Get(f.Key)), StringComparer.OrdinalIgnoreCase);
    }

    public sealed record ConfChange(string Key, string? From, string To);

    /// <summary>
    /// Validates every submitted key against the page's catalog and writes the changed ones
    /// atomically. Keys missing from the conf are appended. Returns only real changes.
    /// </summary>
    public async Task<List<ConfChange>> WriteConfAsync(RatesPage page, IReadOnlyDictionary<string, string> values,
        CancellationToken ct = default)
    {
        var path = ConfPath;
        if (!File.Exists(path))
            throw new InvalidOperationException($"mangosd.conf not found: {path}");

        var doc = await MangosdConfigDocument.LoadAsync(path, ct);
        var changes = new List<ConfChange>();
        foreach (var (key, raw) in values)
        {
            var field = RatesCatalog.Find(key);
            if (field == null || field.Page != page)
                throw new InvalidOperationException($"'{key}' is not a {page} rate.");

            var next = RatesCatalog.NormalizeValue(field, raw);
            var current = doc.Get(field.Key);
            if (SameValue(field, current, next)) continue;

            doc.Set(field.Key, next);
            changes.Add(new ConfChange(field.Key, Unquote(current), Unquote(next)!));
        }

        if (changes.Count > 0)
            await doc.SaveAtomicAsync(path, ct);
        return changes;
    }

    private static bool SameValue(RateField field, string? current, string next)
    {
        if (current == null) return false;
        if (field.Kind == "curve")
            return string.Equals(Unquote(current), Unquote(next), StringComparison.Ordinal);
        return double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
               && double.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
               && Math.Abs(a - b) < 1e-9;
    }

    private static string? Unquote(string? value)
    {
        if (value == null) return null;
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    // ==================== live core values ====================

    public sealed class LiveRates
    {
        public bool Reachable { get; init; }
        public bool Hooks { get; init; }
        public bool Rts { get; init; }
        public string? Error { get; init; }
        public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asks the running core for its in-memory rates. A core without the SuperUI hooks has
    /// no <c>.sui rates</c>; that is reported as Hooks = false, not as an error.
    /// </summary>
    public async Task<LiveRates> ReadLiveAsync(CancellationToken ct = default)
    {
        string response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            response = await _ra.SendCommandAsync(".sui rates", timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rates: .sui rates failed");
            return new LiveRates { Reachable = false, Error = ex.Message };
        }

        var header = LiveHeader.Match(response);
        if (!header.Success)
            return new LiveRates { Reachable = true, Hooks = false };

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in LiveLine.Matches(response))
            values[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return new LiveRates
        {
            Reachable = true,
            Hooks = header.Groups[1].Value == "1",
            Rts = header.Groups[2].Value == "1",
            Values = values
        };
    }

    // ==================== XP per level (for the leveling preview) ====================

    public async Task<List<(int Level, long Xp)>> ReadXpTableAsync()
    {
        using var mangos = _db.Mangos();
        var rows = await mangos.QueryAsync<(int lvl, long xp)>(
            "SELECT `lvl`, `xp_for_next_level` FROM `player_xp_for_level` ORDER BY `lvl`");
        return rows.Select(r => (r.lvl, r.xp)).ToList();
    }

    // ==================== per-faction reputation_reward_rate ====================

    public sealed record FactionRate(uint Id, string Name, uint ParentId, string ParentName,
        double Quest, double Creature, double Spell, bool HasRow);

    public async Task<List<FactionRate>> ReadFactionRatesAsync()
    {
        using var mangos = _db.Mangos();
        var rows = (await mangos.QueryAsync<(uint faction, float quest, float creature, float spell)>(
                "SELECT `faction`, `quest_rate`, `creature_rate`, `spell_rate` FROM `reputation_reward_rate`"))
            .ToDictionary(r => r.faction);

        var factions = _dbc.ReputationFactions;
        var names = factions.ToDictionary(f => f.Id, f => f.Name);
        var list = factions.Select(f =>
        {
            var has = rows.TryGetValue(f.Id, out var r);
            return new FactionRate(f.Id, f.Name, f.ParentId,
                f.ParentId != 0 && names.TryGetValue(f.ParentId, out var parent) ? parent : "",
                has ? r.quest : 1, has ? r.creature : 1, has ? r.spell : 1, has);
        }).ToList();

        // Rows for factions the DBC list doesn't know still show, so nothing in the table is hidden.
        foreach (var (id, r) in rows.Where(kv => !names.ContainsKey(kv.Key)))
            list.Add(new FactionRate(id, $"Faction {id}", 0, "", r.quest, r.creature, r.spell, true));
        return list;
    }

    /// <summary>
    /// Upserts submitted factions; a row whose three rates are all 1 is deleted (no row =
    /// stock behaviour). Returns the before/after rows of what changed, for the audit log.
    /// </summary>
    public async Task<(List<object> Before, List<object> After)> WriteFactionRatesAsync(IEnumerable<FactionRateRow> submitted)
    {
        var known = _dbc.ReputationFactions.Select(f => f.Id).ToHashSet();
        using var mangos = _db.Mangos();
        var existing = (await mangos.QueryAsync<(uint faction, float quest, float creature, float spell)>(
                "SELECT `faction`, `quest_rate`, `creature_rate`, `spell_rate` FROM `reputation_reward_rate`"))
            .ToDictionary(r => r.faction);

        var before = new List<object>();
        var after = new List<object>();
        foreach (var row in submitted)
        {
            if (known.Count > 0 && !known.Contains(row.Faction) && !existing.ContainsKey(row.Faction))
                throw new InvalidOperationException($"Faction {row.Faction} is not a reputation faction in Faction.dbc.");
            foreach (var v in new[] { row.Quest, row.Creature, row.Spell })
                if (!double.IsFinite(v) || v < 0 || v > 1000)
                    throw new InvalidOperationException($"Faction {row.Faction}: rates must be between 0 and 1000.");

            var had = existing.TryGetValue(row.Faction, out var old);
            bool stock = Same(row.Quest, 1) && Same(row.Creature, 1) && Same(row.Spell, 1);
            if (had && Same(row.Quest, old.quest) && Same(row.Creature, old.creature) && Same(row.Spell, old.spell))
                continue;
            if (!had && stock)
                continue;

            if (had) before.Add(new { faction = row.Faction, quest = old.quest, creature = old.creature, spell = old.spell });
            if (stock)
            {
                await mangos.ExecuteAsync("DELETE FROM `reputation_reward_rate` WHERE `faction` = @f", new { f = row.Faction });
                after.Add(new { faction = row.Faction, deleted = true });
            }
            else
            {
                await mangos.ExecuteAsync(
                    "REPLACE INTO `reputation_reward_rate` (`faction`, `quest_rate`, `creature_rate`, `spell_rate`) " +
                    "VALUES (@f, @q, @c, @s)",
                    new { f = row.Faction, q = row.Quest, c = row.Creature, s = row.Spell });
                after.Add(new { faction = row.Faction, quest = row.Quest, creature = row.Creature, spell = row.Spell });
            }
        }
        return (before, after);
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-6;
}
