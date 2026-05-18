// MangosSuperUI/Models/FkResearch.cs
//
// DTOs for the FK research endpoint suite. Used by SourceMapController to
// assemble code bundles for foreign-key relationship discovery via LLM audit.
//
// Drop this into the existing MangosSuperUI/Models/ folder. It only adds new
// types and does not modify any existing model.

namespace MangosSuperUI.Models;

// ──────────── Request DTOs ────────────

/// <summary>
/// Request body for the generic "find functions containing string literals" search.
/// Used by callers that want raw symbol-list hits without bundle assembly.
/// </summary>
public class FindStringReferencesRequest
{
    /// <summary>One or more string tokens to look for inside string literals embedded in symbol bodies.</summary>
    public List<string> Needles { get; set; } = new();

    /// <summary>If true, only symbols whose body contains EVERY needle qualify. If false, ANY needle is enough.</summary>
    public bool RequireAll { get; set; } = false;

    /// <summary>If true, match needles only inside `"..."` string literals (default). If false, match anywhere in the body text.</summary>
    public bool LiteralsOnly { get; set; } = true;

    /// <summary>Optional cap on the number of returned symbols (after ranking). 0 = no cap.</summary>
    public int MaxResults { get; set; } = 0;
}

/// <summary>
/// Request body for the high-level FK research bundle. Given a (db, table, column),
/// the service returns a prompt-ready bundle of all code that touches that column.
/// </summary>
public class FkResearchBundleRequest
{
    /// <summary>Database name (mangos, characters, realmd, logs). Informational; not used to filter symbols.</summary>
    public string Db { get; set; } = "";

    /// <summary>Table name as it appears in SQL string literals (e.g. "creature_template").</summary>
    public string Table { get; set; } = "";

    /// <summary>Column name as it appears in SQL string literals (e.g. "loot_id").</summary>
    public string Column { get; set; } = "";

    /// <summary>
    /// Optional: list of all OTHER candidate target table names. The bundle will flag
    /// which of these names also appear as string literals in each selected symbol,
    /// giving the LLM cross-table reference signal.
    /// </summary>
    public List<string> CandidateTargetTables { get; set; } = new();

    /// <summary>How many call-graph hops to include around each primary hit. Default 1.</summary>
    public int NeighborDepth { get; set; } = 1;

    /// <summary>Max symbols to include in the bundle (counts primary + neighbors). Default 40.</summary>
    public int MaxSymbols { get; set; } = 40;

    /// <summary>If true, include containing class definitions (members list) for selected symbols.</summary>
    public bool IncludeContainingTypes { get; set; } = true;

    /// <summary>
    /// Approximate token budget for the entire bundle. Symbols are added in priority
    /// order (primary → column_only → callers/callees of primary → table_only) and
    /// dropped if including them would exceed this cap. Default 48000.
    /// 0 = no budget enforcement (legacy behavior).
    /// </summary>
    public int MaxTokensEstimate { get; set; } = 48000;

    /// <summary>
    /// If a candidate target table list is provided, symbols that mention more than
    /// this many candidate tables are flagged as "saturated" (likely registration
    /// tables or command dispatchers, not real loaders) and demoted to a separate
    /// role. They are NOT included by default; set IncludeSaturated=true to override.
    /// Default 5.
    /// </summary>
    public int SaturationThreshold { get; set; } = 5;

    /// <summary>If true, saturated symbols are kept (but tagged). Default false — they're dropped.</summary>
    public bool IncludeSaturated { get; set; } = false;

    /// <summary>
    /// If true, include "table_only" hits (symbols that mention the table but not the
    /// column) as fallback context once higher-priority slots and budget allow.
    /// Default false — table_only is mostly noise when primary hits exist.
    /// </summary>
    public bool IncludeTableOnly { get; set; } = false;

    /// <summary>
    /// Soft cap on per-symbol body length, in lines. Functions exceeding this are
    /// flagged with a "huge" marker so the consumer can filter them. Default 800.
    /// 0 = no flagging.
    /// </summary>
    public int HugeFunctionLineThreshold { get; set; } = 800;
}

// ──────────── Response DTOs ────────────

/// <summary>
/// Result of a raw FindStringReferences query. Lightweight — just symbol identity and match metadata.
/// </summary>
public class FindStringReferencesResult
{
    public List<string> Needles { get; set; } = new();
    public bool RequireAll { get; set; }
    public int TotalMatches { get; set; }
    public List<StringReferenceMatch> Matches { get; set; } = new();
}

public class StringReferenceMatch
{
    public string SymbolId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? MemberOf { get; set; }
    public string? File { get; set; }
    public int LineStart { get; set; }
    public int LineEnd { get; set; }

    /// <summary>How many of the requested needles were found in this symbol's literals.</summary>
    public int NeedlesMatched { get; set; }

    /// <summary>The actual needle strings that matched (for quick inspection).</summary>
    public List<string> MatchingNeedles { get; set; } = new();

    /// <summary>Number of times any needle appeared (sum of occurrences). Used as a relevance signal.</summary>
    public int OccurrenceCount { get; set; }
}

/// <summary>
/// Full FK research bundle: a prompt-ready snapshot of all code touching a (db, table, column).
/// The pipeline drops this into a Source 2 / Source 9 / Source 10 LLM prompt as-is.
/// </summary>
public class FkResearchBundleResult
{
    // Echo of the request, for traceability
    public string Db { get; set; } = "";
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";

    // Stats
    public int TotalSymbols { get; set; }
    public int PrimaryHitCount { get; set; }
    public int NeighborCount { get; set; }
    public int ConsumerHitCount { get; set; }
    public int EstimatedTokens { get; set; }
    public int TotalLines { get; set; }
    public DateTime GeneratedAt { get; set; }

    /// <summary>The struct member name(s) the SQL column was resolved to flow
    /// into, via patterns like `pInfo->loot_id = fields[N]`. Used to find
    /// consumers in FK Layer 2. Empty if no assignment pattern matched.</summary>
    public List<string> ResolvedStructMembers { get; set; } = new();

    // Diagnostics — what we searched
    public List<string> NeedlesSearched { get; set; } = new();
    public string SearchDiagnostics { get; set; } = "";

    /// <summary>Symbols that were considered but dropped from the bundle (with reason).
    /// Lets callers audit what got left on the floor.</summary>
    public List<DroppedSymbol> Dropped { get; set; } = new();

    // The actual content
    public List<FkBundleSymbol> Symbols { get; set; } = new();
    public List<FkBundleType> Types { get; set; } = new();

    // Cross-reference table: for each candidate target table, which symbols mention it?
    // Keyed by table name → list of symbol ids that contain that table name as a string literal.
    public Dictionary<string, List<string>> CrossTableReferences { get; set; } = new();

    /// <summary>
    /// File-scope cross-reference (FK Layer 2.5). For each candidate target table,
    /// which FILES contain that table name as a literal (whether at file scope OR
    /// in any function body), AND which of our selected symbols live in those files.
    /// Captures the static-initializer pattern: `LootStore LootTemplates_Creature(
    /// "creature_loot_template", ...)` lives outside any function, but it's
    /// dispositive evidence that the table backs the consumers in that file.
    /// Keyed: table name → list of (file → symbol ids in that file).
    /// </summary>
    public Dictionary<string, List<CrossFileReference>> CrossTableReferencesByFile { get; set; } = new();

    // Pre-formatted prompt-ready text (UTF-8). Saves the pipeline from re-stitching.
    public string FormattedText { get; set; } = "";
}

public class FkBundleSymbol
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? MemberOf { get; set; }
    public string Signature { get; set; } = "";
    public string? File { get; set; }
    public int LineStart { get; set; }
    public int LineEnd { get; set; }

    /// <summary>"primary" = direct string-literal hit on table+column;
    /// "column_only" = column without table;
    /// "consumer" = references the struct member the column maps to (FK Layer 2);
    /// "table_only" = table without column (context only);
    /// "caller" / "callee" = call-graph neighbor of a primary;
    /// "saturated" = mentions many candidate tables, likely a registration noise function.</summary>
    public string Role { get; set; } = "primary";

    /// <summary>The full function body text.</summary>
    public string Body { get; set; } = "";

    /// <summary>Other table names from the candidate list that appear in this symbol's literals.</summary>
    public List<string> AlsoReferences { get; set; } = new();

    /// <summary>Symbols that call into this one (truncated for prompt brevity).</summary>
    public List<string> CalledBy { get; set; } = new();

    /// <summary>Symbols this one calls out to (truncated).</summary>
    public List<string> CallsOut { get; set; } = new();

    /// <summary>True if this symbol's body exceeds the huge-function threshold (likely
    /// a registration / dispatch / static-init table, not focused logic).</summary>
    public bool IsHuge { get; set; } = false;

    /// <summary>Number of candidate target tables this symbol's literals mention.
    /// High values indicate a registration or command-table function — not a real loader.</summary>
    public int CandidateTablesMentioned { get; set; } = 0;
}

public class FkBundleType
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DeclaredIn { get; set; } = "";
    public List<string> Inherits { get; set; } = new();
    public List<string> Members { get; set; } = new();
    public List<string> Methods { get; set; } = new();
}

/// <summary>Lightweight record of a symbol that was considered for the bundle but dropped.</summary>
public class DroppedSymbol
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    /// <summary>Why it was dropped: "budget", "saturated", "max_symbols".</summary>
    public string Reason { get; set; } = "";
    public int EstimatedTokens { get; set; }
    public int CandidateTablesMentioned { get; set; }
}

/// <summary>
/// File-scope cross-reference. A candidate target table appears in a file, and
/// one or more of our selected bundle symbols live in that file. Strong signal
/// the FK target is real, especially for the static-initializer pattern.
/// </summary>
public class CrossFileReference
{
    public string FilePath { get; set; } = "";
    public List<string> SymbolsInFile { get; set; } = new();
}