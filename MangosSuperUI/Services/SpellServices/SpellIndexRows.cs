namespace MangosSuperUI.Services;

/// <summary>
/// New spell index rows (MSUIClient SPELL_CREATOR_PLAN.md decision D5). A spell's cast time,
/// duration, range and radii are ROW IDS into SpellCastTimes / SpellDuration / SpellRange /
/// SpellRadius. The workshop picks the nearest existing row; when its author asks for a value no
/// row has, the design carries a LOCAL row (id 900,000+, the client's
/// <c>SpellRuleTables.LocalRowFloor</c>) and this gives it a real one: an existing row with the same
/// value when there is one (a row added by an earlier build included), else a new row at 1000+.
///
/// Both sides read the same files: the server's dbc dir (the Core merges new rows live on
/// <c>.reload spell_index</c>, ahead of <c>.reload spell_template</c>) and patch-3, which carries
/// these four files whenever they hold a custom row (PatchBuilderService).
/// </summary>
public static class SpellIndexRows
{
    /// <summary>Rows this builds start here (stock maxima: cast times 191, durations 553,
    /// ranges 136, radii 31).</summary>
    public const uint CustomFloor = 1000;

    /// <summary>The client's local ids start here; one surviving into a column is an error.</summary>
    public const uint LocalFloor = 900_000;

    public sealed record Request(string Table, uint LocalId, double Value);

    public static readonly string[] Tables = { "SpellCastTimes", "SpellDuration", "SpellRange", "SpellRadius" };

    public static string FileOf(string table) => table + ".dbc";

    /// <summary>The table a spell_template column points into, or null.</summary>
    public static string? TableOfColumn(string column) =>
        column.Equals("castingTimeIndex", StringComparison.OrdinalIgnoreCase) ? "SpellCastTimes"
        : column.Equals("durationIndex", StringComparison.OrdinalIgnoreCase) ? "SpellDuration"
        : column.Equals("rangeIndex", StringComparison.OrdinalIgnoreCase) ? "SpellRange"
        : column.StartsWith("effectRadiusIndex", StringComparison.OrdinalIgnoreCase) ? "SpellRadius"
        : null;

    /// <summary>Parse the rules block's <c>newIndexRows</c>.</summary>
    public static List<Request> Parse(System.Text.Json.Nodes.JsonNode? node)
    {
        var result = new List<Request>();
        if (node is not System.Text.Json.Nodes.JsonArray rows) return result;
        foreach (var row in rows)
        {
            string table = row?["table"]?.GetValue<string>() ?? "";
            if (Array.IndexOf(Tables, table) < 0 || row?["localId"] is not { } id || row["value"] is not { } value) continue;
            result.Add(new Request(table, (uint)id.GetValue<long>(), value.GetValue<double>()));
        }
        return result;
    }

    /// <summary>
    /// Give every request a real row in <paramref name="tables"/> (loaded DBCs keyed by table
    /// name): an equal row is reused, else one is appended. Returns local id -> real id;
    /// <paramref name="changed"/> names the tables that grew.
    /// </summary>
    public static Dictionary<uint, uint> Resolve(IReadOnlyDictionary<string, DbcWriterService> tables,
        IEnumerable<Request> requests, out HashSet<string> changed)
    {
        changed = new HashSet<string>(StringComparer.Ordinal);
        var map = new Dictionary<uint, uint>();
        foreach (Request request in requests)
        {
            if (!tables.TryGetValue(request.Table, out DbcWriterService? dbc))
                throw new InvalidOperationException($"{FileOf(request.Table)} is not loaded");
            uint[]? same = dbc.GetAllRows().Where(row => Same(request.Table, row, request.Value))
                .OrderBy(row => row[0]).FirstOrDefault();
            if (same is not null) { map[request.LocalId] = same[0]; continue; }

            uint id = Math.Max(CustomFloor, dbc.GetMaxId() + 1);
            dbc.AddRow(RowFor(request.Table, id, request.Value, dbc.FieldCount));
            changed.Add(request.Table);
            map[request.LocalId] = id;
        }
        return map;
    }

    /// <summary>A whole row for one value: cast time and duration in ms (duration -1 = until
    /// cancelled), range and radius in yards. The per-level and maximum fields follow the stock
    /// convention for a fixed value (0 and the value itself); a range's names are left empty.</summary>
    public static uint[] RowFor(string table, uint id, double value, int fieldCount)
    {
        var row = new uint[fieldCount];
        row[0] = id;
        switch (table)
        {
            case "SpellCastTimes":
            case "SpellDuration":
                uint ms = unchecked((uint)(int)Math.Round(value));
                row[1] = ms;
                row[3] = ms;
                break;
            case "SpellRadius":
                row[1] = DbcWriterService.FloatToUint((float)value);
                row[3] = DbcWriterService.FloatToUint((float)value);
                break;
            case "SpellRange":
                row[2] = DbcWriterService.FloatToUint((float)value);   // [1] min 0, [3] flags 0
                break;
        }
        return row;
    }

    private static bool Same(string table, uint[] row, double value)
    {
        switch (table)
        {
            case "SpellCastTimes":
            case "SpellDuration":
                int ms = (int)Math.Round(value);
                return row.Length >= 4 && unchecked((int)row[1]) == ms && row[2] == 0 && unchecked((int)row[3]) == ms;
            case "SpellRadius":
                return row.Length >= 4 && Math.Abs(DbcWriterService.UintToFloat(row[1]) - value) < 0.001 &&
                       row[2] == 0 && Math.Abs(DbcWriterService.UintToFloat(row[3]) - value) < 0.001;
            case "SpellRange":
                return row.Length >= 4 && DbcWriterService.UintToFloat(row[1]) == 0f &&
                       Math.Abs(DbcWriterService.UintToFloat(row[2]) - value) < 0.001 && row[3] == 0;
            default:
                return false;
        }
    }

    /// <summary>Point the rules' index columns at the real rows. Returns an error when a column
    /// still names a local row nothing resolved.</summary>
    public static string? RewriteColumns(Dictionary<string, object?> rules, IReadOnlyDictionary<uint, uint> map)
    {
        foreach (string column in rules.Keys.ToList())
        {
            if (TableOfColumn(column) is null || rules[column] is not long value || value < LocalFloor) continue;
            if (!map.TryGetValue((uint)value, out uint real))
                return $"rules column {column} names new row {value}, but the design carries no such row";
            rules[column] = (long)real;
        }
        return null;
    }

    /// <summary>Resolve against the SERVER's dbc dir and write back the tables that grew.</summary>
    public static Dictionary<uint, uint> ApplyToServer(string dbcDir, IReadOnlyCollection<Request> requests, out HashSet<string> changed)
    {
        var tables = new Dictionary<string, DbcWriterService>(StringComparer.Ordinal);
        foreach (string table in requests.Select(r => r.Table).Distinct())
            tables[table] = DbcWriterService.ReadDbc(Path.Combine(dbcDir, FileOf(table)));
        Dictionary<uint, uint> map = Resolve(tables, requests, out changed);
        foreach (string table in changed)
        {
            string path = Path.Combine(dbcDir, FileOf(table)), temp = path + ".tmp";
            File.WriteAllBytes(temp, tables[table].Write());
            File.Move(temp, path, overwrite: true);
        }
        return map;
    }

    /// <summary>The four files as patch-3 must carry them: only those holding a custom row.</summary>
    public static IEnumerable<(string MpqPath, byte[] Bytes)> PatchFiles(string dbcDir)
    {
        foreach (string table in Tables)
        {
            string path = Path.Combine(dbcDir, FileOf(table));
            if (!File.Exists(path)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            if (DbcWriterService.ReadDbc(bytes, path).GetMaxId() >= CustomFloor)
                yield return ($"DBFilesClient\\{FileOf(table)}", bytes);
        }
    }
}
