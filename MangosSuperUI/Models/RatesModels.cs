using System.Globalization;

namespace MangosSuperUI.Models;

// ══════════════════════════════════════════════════════════════════════════
//  Gameplay Tuning > Rates — the macro XP / reputation / honor multipliers.
//
//  Every value here is a mangosd.conf key, so it travels with World State
//  snapshots (each parked world restores its own conf) and goes live through
//  `.reload config`: the core reads all of these at award time, never cached.
//
//  Keys marked Hook = true only exist in cores built with the SuperUI rate
//  hooks (SuperUiContent/SuiWorld/Shared/SuiRates.cpp + the World/Player/
//  HonorMgr seams). An older core ignores unknown conf keys, so saving them is
//  harmless; the page reads `.sui rates` (hooks=1) to tell the owner whether
//  they are actually in effect. See RATES.md at the repo root.
// ══════════════════════════════════════════════════════════════════════════

public enum RatesPage { Experience, Reputation, Honor }

public sealed class RateField
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public RatesPage Page { get; init; }
    public string Group { get; init; } = "";
    public string Help { get; init; } = "";
    public double Default { get; init; } = 1;
    public double Min { get; init; }
    public double Max { get; init; } = 1000;
    public double Step { get; init; } = 0.05;

    /// <summary>Needs the SuperUI core hooks; stock VMaNGOS ignores the key.</summary>
    public bool Hook { get; init; }

    /// <summary>Follows the page's master multiplier (false for bounds, penalties, decay).</summary>
    public bool Master { get; init; } = true;

    /// <summary>"number" or "curve" (Rate.XP.LevelCurve band string).</summary>
    public string Kind { get; init; } = "number";

    /// <summary>Suffix shown after the value: "×" for multipliers, "" for plain values like RpDecay.</summary>
    public string Unit { get; init; } = "×";
}

public static class RatesCatalog
{
    public const string LevelCurveKey = "Rate.XP.LevelCurve";

    public static readonly IReadOnlyList<RateField> Fields = new[]
    {
        // ── Experience ────────────────────────────────────────────────────
        F("Rate.XP.Kill", "Creature kills", RatesPage.Experience, "XP sources",
            "Every creature kill. Elite kills also get the elite factor below on top of this."),
        F("Rate.XP.Kill.Elite", "Elite kill factor", RatesPage.Experience, "XP sources",
            "Extra factor for elite kills only, multiplied with the kill rate. Leave at 1 to treat elites like normal mobs.",
            master: false),
        F("Rate.XP.Quest", "Quest rewards", RatesPage.Experience, "XP sources",
            "Quest turn-in XP. The gold a quest pays at max level instead of XP is not affected."),
        F("Rate.XP.Explore", "Exploration", RatesPage.Experience, "XP sources",
            "XP for discovering new areas."),
        F("Rate.Rest.InGame", "Rest gain while logged in", RatesPage.Experience, "Rested XP",
            "How fast rested XP builds while standing in an inn or city."),
        F("Rate.Rest.Offline.InTavernOrCity", "Rest gain offline (inn/city)", RatesPage.Experience, "Rested XP",
            "How fast rested XP builds while logged out in an inn or city."),
        F("Rate.Rest.Offline.InWilderness", "Rest gain offline (wilderness)", RatesPage.Experience, "Rested XP",
            "How fast rested XP builds while logged out anywhere else."),
        F("Rate.XP.Personal.Min", "Lowest .xprate a player may pick", RatesPage.Experience, "Player .xprate",
            "Players can set their own XP rate with .xprate. This is the floor (0 lets them stop leveling).",
            master: false, max: 100),
        F("Rate.XP.Personal.Max", "Highest .xprate a player may pick", RatesPage.Experience, "Player .xprate",
            "The ceiling for .xprate. Keep at 1 to let players only slow down, never speed up.",
            master: false, max: 100),
        new RateField
        {
            Key = LevelCurveKey, Label = "XP level curve", Page = RatesPage.Experience, Group = "Level curve",
            Help = "Extra multiplier by the player's level, applied to every XP source after the rates above.",
            Hook = true, Master = false, Kind = "curve", Default = 0
        },

        // ── Reputation ────────────────────────────────────────────────────
        F("Rate.Reputation.Gain", "All reputation", RatesPage.Reputation, "Global",
            "Multiplies every reputation change, gains and losses, from every source."),
        F("Rate.Reputation.Kill", "From creature kills", RatesPage.Reputation, "By source",
            "Reputation from killing creatures, on top of the global rate.", hook: true, master: false),
        F("Rate.Reputation.Quest", "From quests", RatesPage.Reputation, "By source",
            "Reputation from quest rewards, on top of the global rate.", hook: true, master: false),
        F("Rate.Reputation.Spell", "From spells & turn-ins", RatesPage.Reputation, "By source",
            "Reputation granted by spells: item turn-ins, insignia, tabards and similar.", hook: true, master: false),
        F("Rate.Reputation.LowLevel.Kill", "Gray creature kills", RatesPage.Reputation, "Gray content penalty",
            "Applied instead of full value when the creature is gray to the player. Stock VMaNGOS ships 0.2.",
            master: false, def: 0.2),
        F("Rate.Reputation.LowLevel.Quest", "Gray quests", RatesPage.Reputation, "Gray content penalty",
            "Applied when the quest is gray to the player, on top of the vanilla level falloff.",
            master: false),

        // ── Honor ─────────────────────────────────────────────────────────
        F("Rate.Honor.Kill", "Honorable kills", RatesPage.Honor, "Honor earned",
            "Honor from killing enemy players and racial leaders.", hook: true),
        F("Rate.Honor.Bonus", "Battleground bonus honor", RatesPage.Honor, "Honor earned",
            "Bonus honor for battleground objectives and wins. The scoreboard shows the scaled value.", hook: true),
        F("Rate.Honor.Quest", "Honor from spells & quests", RatesPage.Honor, "Honor earned",
            "Honor granted by spells, e.g. battleground mark turn-ins.", hook: true),
        F("Rate.Honor.Dishonor", "Dishonorable kill penalty", RatesPage.Honor, "Honor earned",
            "How hard killing civilians hurts. The penalty is taken from rank points straight away.",
            hook: true, master: false),
        F("Rate.Honor.RankPoints", "Rank point earning", RatesPage.Honor, "Rank progression",
            "Weekly rank points earned from your standing. This is the setting that makes ranking faster.",
            hook: true),
        F("RpDecay", "Weekly rank point decay", RatesPage.Honor, "Rank progression",
            "Share of rank points lost each week (0.2 = 20%). Stock vanilla value is 0.2; 0 means no decay.",
            master: false, def: 0.2, max: 1, step: 0.01, unit: ""),
    };

    public static IEnumerable<RateField> For(RatesPage page) => Fields.Where(f => f.Page == page);

    public static RateField? Find(string key) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string FormatNumber(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Validate one submitted value and return the exact text written to mangosd.conf.</summary>
    public static string NormalizeValue(RateField field, string? raw)
    {
        raw = (raw ?? "").Trim();
        if (field.Kind == "curve")
            return "\"" + LevelCurve.Normalize(raw) + "\"";

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
            throw new InvalidOperationException($"{field.Label}: '{raw}' is not a number.");
        if (value < field.Min || value > field.Max)
            throw new InvalidOperationException($"{field.Label} must be between {field.Min} and {field.Max}.");
        return FormatNumber(value);
    }

    private static RateField F(string key, string label, RatesPage page, string group, string help,
        bool hook = false, bool master = true, double def = 1, double max = 1000, double step = 0.05,
        string unit = "×") => new()
    {
        Key = key, Label = label, Page = page, Group = group, Help = help,
        Hook = hook, Master = master, Default = def, Max = max, Step = step, Unit = unit
    };
}

/// <summary>
/// Rate.XP.LevelCurve grammar, mirrored from SuiRates::LoadLevelCurve: bands
/// "LO-HI:RATE" or "LVL:RATE" separated by spaces/commas/semicolons, levels 1-255,
/// rate 0-1000, later bands override earlier ones. The web side rejects what the
/// core would only log and skip, so a saved curve always means what it says.
/// </summary>
public static class LevelCurve
{
    public sealed record Band(int From, int To, double Rate);

    public static List<Band> Parse(string? spec)
    {
        var bands = new List<Band>();
        var text = (spec ?? "").Replace('"', ' ').Replace(',', ' ').Replace(';', ' ');
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1)
                throw new InvalidOperationException($"Level curve band '{token}' must look like 1-19:3 or 60:1.5.");
            var range = token[..colon];
            var dash = range.IndexOf('-');
            if (!int.TryParse(dash < 0 ? range : range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                || !int.TryParse(dash < 0 ? range : range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var to))
                throw new InvalidOperationException($"Level curve band '{token}' has an invalid level range.");
            if (from < 1 || to < from || to > 255)
                throw new InvalidOperationException($"Level curve band '{token}': levels must be 1-255, low to high.");
            if (!double.TryParse(token[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
                || !double.IsFinite(rate) || rate < 0 || rate > 1000)
                throw new InvalidOperationException($"Level curve band '{token}': rate must be between 0 and 1000.");
            bands.Add(new Band(from, to, rate));
        }
        return bands;
    }

    public static string Normalize(string? spec) =>
        string.Join(' ', Parse(spec).Select(b =>
            (b.From == b.To ? b.From.ToString(CultureInfo.InvariantCulture) : $"{b.From}-{b.To}")
            + ":" + RatesCatalog.FormatNumber(b.Rate)));
}

public sealed class RatesSaveRequest
{
    public string Page { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Reload { get; set; } = true;
}

public sealed class FactionRateRow
{
    public uint Faction { get; set; }
    public double Quest { get; set; } = 1;
    public double Creature { get; set; } = 1;
    public double Spell { get; set; } = 1;
}

public sealed class FactionRatesSaveRequest
{
    public List<FactionRateRow> Rows { get; set; } = new();
    public bool Reload { get; set; } = true;
}
