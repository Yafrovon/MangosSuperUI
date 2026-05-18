using MangosSuperUI.Models;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MangosSuperUI.Services;

/// <summary>
/// Singleton service that indexes the VMaNGOS C++ source tree into a four-layer
/// in-memory index (Files, Symbols, Types, Enums) with reverse lookups.
/// Supports on-demand reindex and trace export for LLM-assisted code tracing.
/// </summary>
public class SourceIndexerService
{
    private readonly ILogger<SourceIndexerService> _log;
    private SourceIndex? _index;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IndexProgress _progress = new();

    // ──────────── String literal index (for FK research) ────────────
    // Parallel to _index, rebuilt during ReindexAsync. Maps lowercased word tokens
    // extracted from string literals → set of symbol ids whose body contains that
    // token inside a quoted "..." literal. Used for fast O(1) lookup of every
    // function that mentions a given SQL table/column name in a query string.
    //
    // We ALSO store, per symbol, the set of tokens that appeared in its literals.
    // That lets us cheaply answer "which other table names from a list does this
    // symbol mention" without re-scanning the body.
    private Dictionary<string, HashSet<string>> _literalTokenToSymbols = new();
    private Dictionary<string, HashSet<string>> _symbolToLiteralTokens = new();

    // ──────────── Struct member reference index (FK Layer 2) ────────────
    // For a column like `creature_template.loot_id`, layer 1 finds the LOADER
    // function. But the FK target evidence usually lives elsewhere — in CONSUMER
    // functions that read `someInfo->loot_id` and pass it to a loot-system call.
    // This index maps "loot_id" → every symbol whose body contains a member-access
    // expression with that member name (foo->loot_id or foo.loot_id).
    //
    // The qualified form (struct::member) is also tracked when we can resolve LHS
    // type; the unqualified form is the recall fallback.
    private Dictionary<string, HashSet<string>> _memberToReferencingSymbols = new();
    private Dictionary<string, HashSet<string>> _symbolToMembersReferenced = new();

    // ──────────── File-scope string literal index (FK Layer 2.5) ────────────
    // Some FK target evidence lives in FILE-SCOPE static initializers, not inside
    // any function. Example in LootMgr.cpp:
    //
    //     LootStore LootTemplates_Creature("creature_loot_template", "creature entry", true);
    //
    // This is the canonical declaration that "creature_loot_template" is the table
    // backing LootTemplates_Creature. None of its call sites mention the table name;
    // that name lives only in this one global. We index every string literal token
    // per file, regardless of whether it's inside a function body. The bundle's
    // cross-table annotation then checks: "for each selected symbol's file, what
    // file-scope literals does that file contain?" If a consumer function lives in
    // LootMgr.cpp and LootMgr.cpp contains "creature_loot_template" at file scope,
    // we surface that as a cross-reference. This catches the static-init pattern.
    private Dictionary<string, HashSet<string>> _fileToLiteralTokens = new();
    private Dictionary<string, HashSet<string>> _literalTokenToFiles = new();

    // Member-name stopword set. Standard library / language-builtin member names
    // create huge numbers of false positives ("vec.size", "iter->second", etc.)
    // and have nothing to do with SQL columns. Anything in this set is skipped
    // during member-access indexing.
    private static readonly HashSet<string> MemberNameStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        // STL containers
        "size", "empty", "begin", "end", "cbegin", "cend", "rbegin", "rend",
        "front", "back", "data", "at", "find", "count", "insert", "erase", "clear",
        "push_back", "pop_back", "push_front", "pop_front", "emplace", "emplace_back",
        "reserve", "resize", "capacity", "swap", "assign", "max_size",
        // STL pairs/iters
        "first", "second", "key", "value",
        // strings
        "c_str", "length", "substr", "append", "compare", "replace",
        // smart pointers / utility
        "get", "reset", "release", "lock", "unlock", "use_count", "weak", "strong",
        // I/O / streams
        "str", "rdbuf", "fail", "good", "eof", "bad", "tellg", "tellp", "seekg", "seekp",
        // misc very-common names that are almost never SQL columns
        "ptr", "self", "this", "obj", "tmp", "ret", "result", "value_type", "iterator",
        "const_iterator", "reference", "pointer", "type", "raw", "ref",
    };

    // ──────────── Regex patterns (compiled once) ────────────

    private static readonly Regex RxInclude = new(
        @"^\s*#include\s+[<""](.+?)[>""]",
        RegexOptions.Compiled);

    // Forward declaration: class Player; or struct Foo;
    private static readonly Regex RxForwardDecl = new(
        @"^(?:class|struct)\s+\w+\s*;",
        RegexOptions.Compiled);

    // class Player : public Unit  (brace may be on NEXT line — Allman style)
    // struct WorldPacket
    // class ChatCommand
    // Also handles: class CliHandler : public ChatHandler
    //               class MANGOS_DLL_SPEC Player : public Unit
    //               template<class T> class Foo : public Bar
    private static readonly Regex RxClassDecl = new(
        @"^(?:template\s*<[^>]*>\s*)?(class|struct)\s+(?:MANGOS_DLL_SPEC\s+|MANGOS_DLL_DECL\s+)?(\w+)\s*(?:final\s*)?(?::(?:\s*(?:public|protected|private)\s+)?(\w+)(?:\s*,\s*(?:public|protected|private)\s+\w+)*)?\s*(?:\{)?\s*$",
        RegexOptions.Compiled);

    // enum PlayerFlags { ... }
    // enum class OpcodeType : uint8 { ... }
    // enum Foo   (brace on next line — Allman style)
    private static readonly Regex RxEnumDecl = new(
        @"^enum\s+(?:class\s+)?(\w+)(?:\s*:\s*\w+)?\s*\{?\s*$",
        RegexOptions.Compiled);

    // Function definition:  void Player::Update(uint32 diff) {
    // Also catches: static void Foo(int x) {
    // The key: ClassName::MethodName( or just FunctionName( followed eventually by {
    private static readonly Regex RxFuncDef = new(
        @"^((?:virtual\s+|static\s+|inline\s+|const\s+|explicit\s+)*(?:[\w:*&<>]+(?:\s+[\w:*&<>]+)*?))\s+((?:\w+::)*\w+)\s*\(([^)]*)\)\s*(const)?\s*(?:override\s*)?(?:=\s*\w+\s*)?(\{)?\s*$",
        RegexOptions.Compiled);

    // Function declaration in .h:  void Update(uint32 diff);
    // virtual void Update(uint32 diff) = 0;
    private static readonly Regex RxFuncDecl = new(
        @"^\s*((?:virtual\s+|static\s+|inline\s+|explicit\s+)*(?:[\w:*&<>]+(?:\s+[\w:*&<>]+)*?))\s+(\w+)\s*\(([^)]*)\)\s*(const)?\s*(?:override)?\s*(?:=\s*0)?\s*;",
        RegexOptions.Compiled);

    // Member variable: Type m_name;  or Type* m_name;
    private static readonly Regex RxMember = new(
        @"^\s+([\w:*&<>]+(?:\s*[*&])?)\s+(m_\w+)\s*(?:=\s*[^;]+)?;",
        RegexOptions.Compiled);

    // #define PLAYER_FLAGS_AFK 0x02
    private static readonly Regex RxDefine = new(
        @"^#define\s+(\w+)\s+(.+?)(?:\s*//.*)?$",
        RegexOptions.Compiled);

    // Opcode handler: OpcodeHandler(MSG_MOVE_START, ..., &WorldSession::HandleMove)
    private static readonly Regex RxOpcodeHandler = new(
        @"&(\w+::\w+)",
        RegexOptions.Compiled);

    // Function call inside body:  SomeName( or Class::Method(
    private static readonly Regex RxFuncCall = new(
        @"\b(\w+(?:::\w+)?)\s*\(",
        RegexOptions.Compiled);

    // Complexity keywords
    private static readonly Regex RxComplexity = new(
        @"\b(if|else|for|while|switch|case|catch|do)\b",
        RegexOptions.Compiled);

    // C++ double-quoted string literal. Matches "..." with escaped quotes handled.
    // We deliberately do NOT match raw string literals (R"(...)") or single-char
    // literals — VMaNGOS SQL is always in plain double-quoted strings.
    private static readonly Regex RxStringLiteral = new(
        "\"((?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.Compiled);

    // Token splitter inside a string literal. SQL identifiers (table/column names)
    // are [A-Za-z_][A-Za-z0-9_]*. We split on anything else so that "DELETE FROM
    // `creature_template` WHERE guid = '%u'" yields { delete, from, creature_template,
    // where, guid, u }. Backticks around table names are stripped because they're
    // not part of the token.
    private static readonly Regex RxLiteralToken = new(
        @"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.Compiled);

    // Struct member access: foo->bar  or  foo.bar  — captures the member name.
    // The LHS is captured for optional type resolution but we mostly key on the
    // RHS member name for the inverted index. Pattern restricted to word chars
    // so it ignores arithmetic/calls/etc.
    //
    // Captures:
    //   1: LHS identifier (e.g. "pInfo", "m_creature")
    //   2: accessor ("->" or ".")
    //   3: member name (e.g. "loot_id")
    private static readonly Regex RxMemberAccess = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s*(->|\.)\s*([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    // Loader assignment pattern: `something->member = fields[N].GetXxx(...)`
    // This is the smoking-gun pattern for "column N of a SQL query gets stored
    // into this struct field". Used by the FK research bundle to resolve which
    // struct member a column flows into.
    //
    // Captures:
    //   1: LHS identifier (e.g. "pInfo")
    //   2: accessor
    //   3: member name (e.g. "loot_id")
    //   4: fields[N] index
    private static readonly Regex RxFieldsAssignment = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s*(->|\.)\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*fields\s*\[\s*(\d+)\s*\]",
        RegexOptions.Compiled);

    // ──────────── Constructor ────────────

    public SourceIndexerService(ILogger<SourceIndexerService> log)
    {
        _log = log;
    }

    // ──────────── Public API ────────────

    public bool IsIndexing => _progress.Phase == "scanning" || _progress.Phase == "parsing"
                           || _progress.Phase == "bodies" || _progress.Phase == "reverse";
    public IndexProgress CurrentProgress => _progress;
    public SourceIndex? GetIndex() => _index;

    public SymbolEntry? GetSymbol(string qualifiedName) =>
        _index?.Symbols.TryGetValue(qualifiedName, out var s) == true ? s : null;

    public TypeEntry? GetType(string name) =>
        _index?.Types.TryGetValue(name, out var t) == true ? t : null;

    public EnumEntry? GetEnum(string name) =>
        _index?.Enums.TryGetValue(name, out var e) == true ? e : null;

    public FileEntry? GetFile(string path) =>
        _index?.Files.TryGetValue(path, out var f) == true ? f : null;

    public List<object> Search(string query, string kind = "all", int max = 50)
    {
        var idx = _index;
        if (idx == null || string.IsNullOrWhiteSpace(query)) return new();

        var q = query.Trim().ToLowerInvariant();
        var results = new List<object>();

        // When searching all kinds, allocate per-kind limits so symbols don't starve others
        int perKind = kind == "all" ? Math.Max(max / 4, 15) : max;

        if (kind is "all" or "symbol")
        {
            int count = 0;
            foreach (var (id, sym) in idx.Symbols)
            {
                if (count >= perKind) break;
                if (id.ToLowerInvariant().Contains(q) || sym.Signature.ToLowerInvariant().Contains(q))
                {
                    results.Add(new { id, sym.Name, type = "symbol", sym.Kind, sym.MemberOf, file = sym.DefinedInFile, sym.LineCount, callCount = sym.CallsOut.Count, callerCount = sym.CalledBy.Count });
                    count++;
                }
            }
        }

        if (kind is "all" or "type")
        {
            int count = 0;
            foreach (var (name, t) in idx.Types)
            {
                if (count >= perKind) break;
                if (name.ToLowerInvariant().Contains(q))
                {
                    results.Add(new { id = name, t.Name, type = "type", t.Kind, t.DeclaredIn, methodCount = t.Methods.Count, memberCount = t.Members.Count });
                    count++;
                }
            }
        }

        if (kind is "all" or "enum")
        {
            int count = 0;
            foreach (var (name, e) in idx.Enums)
            {
                if (count >= perKind) break;
                if (name.ToLowerInvariant().Contains(q) || e.Values.Any(v => v.Name.ToLowerInvariant().Contains(q)))
                {
                    results.Add(new { id = name, e.Name, type = "enum", e.Kind, e.DeclaredIn, valueCount = e.Values.Count });
                    count++;
                }
            }
        }

        if (kind is "all" or "file")
        {
            int count = 0;
            foreach (var (path, f) in idx.Files)
            {
                if (count >= perKind) break;
                if (path.ToLowerInvariant().Contains(q) || f.FileName.ToLowerInvariant().Contains(q))
                {
                    results.Add(new { id = path, Name = f.FileName, type = "file", f.Extension, f.LineCount, symbolCount = f.DefinedSymbols.Count + f.DeclaredSymbols.Count });
                    count++;
                }
            }
        }

        return results;
    }

    // ──────────── Smart Search (resolves C++ call expressions) ────────────

    /// <summary>
    /// Parses C++ call expressions like me->IsDead(), pVictim->GetHealth(),
    /// Unit::IsDead, etc. and resolves them to matching indexed symbols.
    /// Walks the inheritance hierarchy to rank probable matches.
    /// </summary>
    public SmartSearchResult SmartSearch(string query)
    {
        var idx = _index;
        var result = new SmartSearchResult { RawQuery = query };

        if (idx == null || string.IsNullOrWhiteSpace(query))
            return result;

        // ── Parse the C++ expression ──
        var raw = query.Trim().TrimEnd('(', ')', ';', ' ');

        // Check for -> (pointer member access)
        var arrowIdx = raw.LastIndexOf("->");
        if (arrowIdx >= 0)
        {
            result.Variable = raw.Substring(0, arrowIdx).Trim();
            result.MethodName = raw.Substring(arrowIdx + 2).Trim();
            result.ExpressionType = "pointer_member";
        }
        // Check for . (dot member access) — but not file paths
        else if (raw.Contains('.') && !raw.Contains('/') && !raw.Contains('\\'))
        {
            var dotIdx = raw.LastIndexOf('.');
            result.Variable = raw.Substring(0, dotIdx).Trim();
            result.MethodName = raw.Substring(dotIdx + 1).Trim();
            result.ExpressionType = "dot_member";
        }
        // Check for :: (scope resolution)
        else if (raw.Contains("::"))
        {
            var sepIdx = raw.LastIndexOf("::");
            result.ExplicitClass = raw.Substring(0, sepIdx).Trim();
            result.MethodName = raw.Substring(sepIdx + 2).Trim();
            result.ExpressionType = "scope_resolution";
        }
        else
        {
            result.MethodName = raw;
            result.ExpressionType = "bare_name";
        }

        // Clean method name: strip parens, template args
        if (result.MethodName != null)
        {
            var parenPos = result.MethodName.IndexOf('(');
            if (parenPos >= 0) result.MethodName = result.MethodName.Substring(0, parenPos);
            var templatePos = result.MethodName.IndexOf('<');
            if (templatePos >= 0) result.MethodName = result.MethodName.Substring(0, templatePos);
            result.MethodName = result.MethodName.Trim();
        }

        if (string.IsNullOrEmpty(result.MethodName))
            return result;

        // ── Well-known VMaNGOS variable → type hints ──
        var knownVariables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["me"] = new[] { "Creature", "Unit", "Object", "WorldObject" },
            ["m_creature"] = new[] { "Creature", "Unit" },
            ["m_bot"] = new[] { "Player", "Unit" },
            ["m_caster"] = new[] { "Unit", "WorldObject" },
            ["m_target"] = new[] { "Unit", "WorldObject" },
            ["pVictim"] = new[] { "Unit", "Player", "Creature" },
            ["pTarget"] = new[] { "Unit", "Player", "Creature" },
            ["pPlayer"] = new[] { "Player", "Unit" },
            ["pCreature"] = new[] { "Creature", "Unit" },
            ["pCaster"] = new[] { "Unit", "SpellCaster" },
            ["m_session"] = new[] { "WorldSession" },
            ["pSession"] = new[] { "WorldSession" },
            ["_player"] = new[] { "Player", "Unit" },
            ["m_owner"] = new[] { "Unit", "Player", "Object" },
            ["pOwner"] = new[] { "Unit", "Player" },
            ["m_master"] = new[] { "Player", "Unit" },
        };

        // Resolve variable to candidate types
        string[]? hintedTypes = null;
        if (result.Variable != null && knownVariables.TryGetValue(result.Variable, out var known))
        {
            hintedTypes = known;
            result.ResolvedTypes = known.ToList();
        }
        else if (result.ExplicitClass != null)
        {
            // Extract just the last segment for lookup: "WorldPackets::Taxi::Foo" → "Foo"
            var lastSep = result.ExplicitClass.LastIndexOf("::");
            var shortClass = lastSep >= 0 ? result.ExplicitClass.Substring(lastSep + 2) : result.ExplicitClass;
            hintedTypes = new[] { shortClass, result.ExplicitClass };
            result.ResolvedTypes = hintedTypes.ToList();
        }

        // ── Find all symbols whose Name matches the method name ──
        var matches = new List<SmartSearchMatch>();

        foreach (var (id, sym) in idx.Symbols)
        {
            if (!sym.Name.Equals(result.MethodName, StringComparison.OrdinalIgnoreCase))
                continue;

            var match = new SmartSearchMatch
            {
                Id = sym.Id,
                Name = sym.Name,
                MemberOf = sym.MemberOf,
                File = sym.DefinedInFile,
                LineStart = sym.BodyLineStart,
                LineCount = sym.LineCount,
                Signature = sym.Signature,
                Kind = sym.Kind,
                CallCount = sym.CallsOut.Count,
                CallerCount = sym.CalledBy.Count,
                IsVirtual = sym.IsVirtual,
                Confidence = "possible"
            };

            // ── Score confidence ──
            if (hintedTypes != null && sym.MemberOf != null)
            {
                // Direct class match
                var memberOfShort = sym.MemberOf;
                var lastSep = memberOfShort.LastIndexOf("::");
                if (lastSep >= 0) memberOfShort = memberOfShort.Substring(lastSep + 2);

                if (hintedTypes.Any(h => h.Equals(memberOfShort, StringComparison.OrdinalIgnoreCase)
                                      || h.Equals(sym.MemberOf, StringComparison.OrdinalIgnoreCase)))
                {
                    match.Confidence = "high";
                }
                else
                {
                    // Walk inheritance: does any hinted type inherit from this symbol's class?
                    // e.g., me is Creature, Creature inherits Unit, method is Unit::IsDead → high
                    foreach (var hintType in hintedTypes)
                    {
                        if (IsInInheritanceChain(idx, hintType, memberOfShort))
                        {
                            match.Confidence = "high";
                            match.InheritancePath = BuildInheritancePath(idx, hintType, memberOfShort);
                            break;
                        }
                    }
                }
            }

            matches.Add(match);
        }

        // Sort: high confidence first, then by caller count
        matches.Sort((a, b) =>
        {
            var confOrder = ConfidenceOrder(a.Confidence).CompareTo(ConfidenceOrder(b.Confidence));
            if (confOrder != 0) return confOrder;
            return b.CallerCount.CompareTo(a.CallerCount);
        });

        result.Matches = matches;
        result.TotalMatches = matches.Count;

        return result;
    }

    private static int ConfidenceOrder(string conf) => conf switch
    {
        "high" => 0,
        "medium" => 1,
        "possible" => 2,
        _ => 3
    };

    /// <summary>
    /// Check if childType inherits (directly or transitively) from ancestorType.
    /// </summary>
    private bool IsInInheritanceChain(SourceIndex idx, string childType, string ancestorType, int maxDepth = 10)
    {
        if (childType.Equals(ancestorType, StringComparison.OrdinalIgnoreCase)) return true;
        if (maxDepth <= 0) return false;

        if (!idx.Types.TryGetValue(childType, out var typeEntry)) return false;

        foreach (var baseName in typeEntry.Inherits)
        {
            if (baseName.Equals(ancestorType, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsInInheritanceChain(idx, baseName, ancestorType, maxDepth - 1)) return true;
        }

        return false;
    }

    /// <summary>
    /// Build a readable inheritance path from child to ancestor, e.g. "Creature → Unit → Object"
    /// </summary>
    private string? BuildInheritancePath(SourceIndex idx, string childType, string ancestorType, int maxDepth = 10)
    {
        var path = new List<string> { childType };
        if (BuildPathRecursive(idx, childType, ancestorType, path, maxDepth))
            return string.Join(" → ", path);
        return null;
    }

    private bool BuildPathRecursive(SourceIndex idx, string current, string target, List<string> path, int maxDepth)
    {
        if (current.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
        if (maxDepth <= 0) return false;
        if (!idx.Types.TryGetValue(current, out var typeEntry)) return false;

        foreach (var baseName in typeEntry.Inherits)
        {
            path.Add(baseName);
            if (BuildPathRecursive(idx, baseName, target, path, maxDepth - 1)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    // ──────────── Reindex ────────────

    public async Task<IndexResult> ReindexAsync(string sourcePath)
    {
        if (!await _lock.WaitAsync(0))
            return new IndexResult { Success = false, Error = "Indexing already in progress" };

        var sw = Stopwatch.StartNew();
        try
        {
            _progress = new IndexProgress { Phase = "scanning" };

            if (!Directory.Exists(sourcePath))
                return new IndexResult { Success = false, Error = $"Source path not found: {sourcePath}" };

            // Reset string-literal indexes; they are rebuilt during Pass 2 below.
            _literalTokenToSymbols = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            _symbolToLiteralTokens = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            // Reset member-access indexes (FK Layer 2).
            _memberToReferencingSymbols = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            _symbolToMembersReferenced = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            // Reset file-scope literal indexes (FK Layer 2.5).
            _fileToLiteralTokens = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            _literalTokenToFiles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            var index = new SourceIndex
            {
                SourcePath = sourcePath,
                IndexedAt = DateTime.UtcNow
            };

            // Discover all .h and .cpp files
            var allFiles = Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".h", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            _progress.TotalFiles = allFiles.Count;

            // ── Pass 1: Parse each file ──
            _progress.Phase = "parsing";

            // We need to read all file lines for body extraction later
            var fileLines = new Dictionary<string, string[]>();

            foreach (var fullPath in allFiles)
            {
                var relativePath = Path.GetRelativePath(sourcePath, fullPath).Replace('\\', '/');
                _progress.CurrentFile = relativePath;
                _progress.FilesProcessed++;

                try
                {
                    var lines = await File.ReadAllLinesAsync(fullPath);
                    fileLines[relativePath] = lines;
                    ParseFile(index, relativePath, lines);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to parse {File}", relativePath);
                }
            }

            // ── Pass 2: Extract function bodies and analyze calls ──
            _progress.Phase = "bodies";
            _progress.FilesProcessed = 0;

            foreach (var (relPath, lines) in fileLines)
            {
                _progress.CurrentFile = relPath;
                _progress.FilesProcessed++;
                ExtractBodiesAndCalls(index, relPath, lines);
            }

            // ── Pass 3: Build reverse indices ──
            _progress.Phase = "reverse";
            BuildReverseIndices(index);

            // ── Finalize ──
            index.TotalFiles = index.Files.Count;
            index.TotalLines = index.Files.Values.Sum(f => f.LineCount);
            index.TotalSymbols = index.Symbols.Count;
            index.TotalTypes = index.Types.Count;
            index.TotalEnums = index.Enums.Count;

            _index = index;
            _progress = new IndexProgress { Phase = "complete" };

            sw.Stop();
            var result = new IndexResult
            {
                Success = true,
                Files = index.TotalFiles,
                Symbols = index.TotalSymbols,
                Types = index.TotalTypes,
                Enums = index.TotalEnums,
                Lines = index.TotalLines,
                ElapsedMs = sw.ElapsedMilliseconds
            };

            _log.LogInformation("Source index built: {Files} files, {Symbols} symbols, {Types} types, {Enums} enums, {Lines} lines in {Ms}ms",
                result.Files, result.Symbols, result.Types, result.Enums, result.Lines, result.ElapsedMs);

            return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reindex failed");
            _progress = new IndexProgress { Phase = "error", Error = ex.Message };
            return new IndexResult { Success = false, Error = ex.Message };
        }
        finally
        {
            _lock.Release();
        }
    }

    // ──────────── Pass 1: Parse file structure ────────────

    private void ParseFile(SourceIndex index, string relativePath, string[] lines)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        var fileName = Path.GetFileName(relativePath);
        var dir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";

        var fileEntry = new FileEntry
        {
            Path = relativePath,
            FileName = fileName,
            Extension = ext,
            Directory = dir,
            LineCount = lines.Length
        };

        string? currentClass = null;
        int braceDepth = 0;
        int classBraceDepth = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Skip comments and preprocessor (except includes and defines)
            if (trimmed.StartsWith("//") && !trimmed.StartsWith("//"))
            {
                // Allow // comments but skip for parsing — they're just comments
            }

            // Track brace depth for class scope
            braceDepth += CountChar(line, '{') - CountChar(line, '}');
            if (classBraceDepth >= 0 && braceDepth <= classBraceDepth)
            {
                currentClass = null;
                classBraceDepth = -1;
            }

            // #include
            var mInc = RxInclude.Match(line);
            if (mInc.Success)
            {
                fileEntry.Includes.Add(mInc.Groups[1].Value);
                continue;
            }

            // enum
            var mEnum = RxEnumDecl.Match(trimmed);
            if (mEnum.Success && !trimmed.Contains("//"))
            {
                var enumName = mEnum.Groups[1].Value;
                if (enumName.Length > 1 && !IsCommonKeyword(enumName))
                {
                    var enumEntry = ParseEnum(enumName, trimmed.Contains("class") ? "enum_class" : "enum", relativePath, lines, i);
                    if (enumEntry != null && !index.Enums.ContainsKey(enumName))
                    {
                        index.Enums[enumName] = enumEntry;
                        fileEntry.DeclaredEnums.Add(enumName);
                    }
                }
                continue;
            }

            // class/struct declaration — skip forward declarations first
            if (RxForwardDecl.IsMatch(trimmed))
                continue; // e.g. "class Player;" or "struct Foo;"

            var mClass = RxClassDecl.Match(trimmed);
            if (mClass.Success && !trimmed.Contains(";"))
            {
                var kind = mClass.Groups[1].Value;
                var name = mClass.Groups[2].Value;
                var baseName = mClass.Groups[3].Success ? mClass.Groups[3].Value : null;

                if (name.Length > 1 && !IsCommonKeyword(name))
                {
                    // Verify this is a definition (has opening brace), not just a forward decl
                    // Brace may be on same line or within next 3 non-blank lines (Allman style)
                    bool hasBrace = trimmed.Contains("{");
                    if (!hasBrace)
                    {
                        for (int look = i + 1; look < lines.Length && look <= i + 3; look++)
                        {
                            var lookTrimmed = lines[look].TrimStart();
                            if (string.IsNullOrWhiteSpace(lookTrimmed)) continue;
                            if (lookTrimmed.StartsWith("{"))
                            {
                                hasBrace = true;
                                // Count this brace in our depth tracking
                                braceDepth += CountChar(lines[look], '{') - CountChar(lines[look], '}');
                            }
                            break; // stop at first non-blank line
                        }
                    }

                    if (!hasBrace) continue; // no brace found — skip (forward decl or something weird)

                    currentClass = name;
                    classBraceDepth = braceDepth - 1; // will exit when braces return to this level

                    if (!index.Types.ContainsKey(name))
                    {
                        var typeEntry = new TypeEntry
                        {
                            Name = name,
                            Kind = kind,
                            DeclaredIn = relativePath,
                            DeclaredAtLine = i + 1
                        };
                        if (baseName != null) typeEntry.Inherits.Add(baseName);
                        index.Types[name] = typeEntry;
                    }
                    else if (baseName != null && !index.Types[name].Inherits.Contains(baseName))
                    {
                        index.Types[name].Inherits.Add(baseName);
                    }

                    fileEntry.DeclaredTypes.Add(name);
                }
                continue;
            }

            // In a header file: function declarations and member variables
            if (ext == ".h" && currentClass != null)
            {
                // Member variable
                var mMem = RxMember.Match(line);
                if (mMem.Success)
                {
                    var memName = mMem.Groups[2].Value;
                    if (index.Types.TryGetValue(currentClass, out var t) && !t.Members.Contains(memName))
                        t.Members.Add(memName);
                    continue;
                }

                // Function declaration
                var mDecl = RxFuncDecl.Match(trimmed);
                if (mDecl.Success)
                {
                    var returnType = mDecl.Groups[1].Value.Trim();
                    var funcName = mDecl.Groups[2].Value;
                    var parms = mDecl.Groups[3].Value.Trim();
                    var isConst = mDecl.Groups[4].Success;

                    var qualifiedName = currentClass + "::" + funcName;
                    var sig = returnType + " " + qualifiedName + "(" + parms + ")" + (isConst ? " const" : "");

                    // Check if a longer-qualified version already exists from a .cpp file
                    // e.g., .cpp already registered "WorldPackets::Taxi::TaxiNodeStatusQuery::ReadFromWorldPacket"
                    // and we're about to register "TaxiNodeStatusQuery::ReadFromWorldPacket" — skip the duplicate
                    var longerKeyExists = index.Symbols.Keys.Any(k => k != qualifiedName && k.EndsWith("::" + qualifiedName));
                    if (longerKeyExists)
                    {
                        // Find the existing longer key and update its declaration location
                        var longerKey = index.Symbols.Keys.First(k => k != qualifiedName && k.EndsWith("::" + qualifiedName));
                        var existing = index.Symbols[longerKey];
                        if (existing.DeclaredIn == null)
                            existing.DeclaredIn = relativePath + ":" + (i + 1);

                        fileEntry.DeclaredSymbols.Add(longerKey);

                        if (index.Types.TryGetValue(currentClass, out var t3))
                        {
                            if (!t3.Methods.Contains(funcName)) t3.Methods.Add(funcName);
                            if (!t3.QualifiedMethods.Contains(longerKey)) t3.QualifiedMethods.Add(longerKey);
                        }
                    }
                    else if (!index.Symbols.ContainsKey(qualifiedName))
                    {
                        index.Symbols[qualifiedName] = new SymbolEntry
                        {
                            Id = qualifiedName,
                            Name = funcName,
                            Kind = DetectMethodKind(returnType, trimmed),
                            ReturnType = CleanReturnType(returnType),
                            Signature = sig,
                            Params = ParseParams(parms),
                            DeclaredIn = relativePath + ":" + (i + 1),
                            MemberOf = currentClass,
                            IsVirtual = returnType.Contains("virtual"),
                            IsStatic = returnType.Contains("static"),
                            IsConst = isConst
                        };

                        fileEntry.DeclaredSymbols.Add(qualifiedName);

                        if (index.Types.TryGetValue(currentClass, out var t2))
                        {
                            if (!t2.Methods.Contains(funcName)) t2.Methods.Add(funcName);
                            if (!t2.QualifiedMethods.Contains(qualifiedName)) t2.QualifiedMethods.Add(qualifiedName);
                        }
                    }
                    else
                    {
                        // Update declaration location if we only had the definition before
                        var existing = index.Symbols[qualifiedName];
                        if (existing.DeclaredIn == null)
                            existing.DeclaredIn = relativePath + ":" + (i + 1);

                        fileEntry.DeclaredSymbols.Add(qualifiedName);

                        if (index.Types.TryGetValue(currentClass, out var t2))
                        {
                            if (!t2.Methods.Contains(funcName)) t2.Methods.Add(funcName);
                            if (!t2.QualifiedMethods.Contains(qualifiedName)) t2.QualifiedMethods.Add(qualifiedName);
                        }
                    }
                }
            }

            // Function definition (in .cpp or .h inline)
            if (ext == ".cpp" || (ext == ".h" && trimmed.Contains("{")))
            {
                var mDef = RxFuncDef.Match(trimmed);
                if (mDef.Success && mDef.Groups[2].Value.Contains("::"))
                {
                    var returnType = mDef.Groups[1].Value.Trim();
                    var qualifiedName = mDef.Groups[2].Value;
                    var parms = mDef.Groups[3].Value.Trim();
                    var isConst = mDef.Groups[4].Success;

                    // Use LAST :: split for funcName, everything before for className
                    // e.g. "WorldPackets::Taxi::TaxiNodeStatusQuery::ReadFromWorldPacket"
                    //   → className = "WorldPackets::Taxi::TaxiNodeStatusQuery"
                    //   → funcName  = "ReadFromWorldPacket"
                    var lastSep = qualifiedName.LastIndexOf("::");
                    var className = qualifiedName.Substring(0, lastSep);
                    var funcName = qualifiedName.Substring(lastSep + 2);

                    var sig = returnType + " " + qualifiedName + "(" + parms + ")" + (isConst ? " const" : "");

                    // Check if a shorter-qualified version already exists from the .h declaration pass
                    // e.g., .h registered "TaxiNodeStatusQuery::ReadFromWorldPacket" but .cpp uses
                    //        "WorldPackets::Taxi::TaxiNodeStatusQuery::ReadFromWorldPacket"
                    // We want to merge into the existing entry and use the FULL qualified name as the canonical key
                    string? shortKey = null;
                    if (qualifiedName.Contains("::"))
                    {
                        // Try progressively shorter prefixes to find an existing declaration
                        var remaining = qualifiedName;
                        while (remaining.Contains("::"))
                        {
                            var firstSep = remaining.IndexOf("::");
                            remaining = remaining.Substring(firstSep + 2);
                            if (remaining.Contains("::") && index.Symbols.ContainsKey(remaining))
                            {
                                shortKey = remaining;
                                break;
                            }
                        }
                    }

                    if (shortKey != null && !index.Symbols.ContainsKey(qualifiedName))
                    {
                        // Promote the short-key entry to the full qualified name
                        var existing = index.Symbols[shortKey];
                        index.Symbols.Remove(shortKey);
                        existing.Id = qualifiedName;
                        existing.Signature = sig;
                        existing.MemberOf = className;
                        index.Symbols[qualifiedName] = existing;

                        // Update any file entry references from short key to full key
                        foreach (var fe2 in index.Files.Values)
                        {
                            if (fe2.DeclaredSymbols.Remove(shortKey))
                                fe2.DeclaredSymbols.Add(qualifiedName);
                            if (fe2.DefinedSymbols.Remove(shortKey))
                                fe2.DefinedSymbols.Add(qualifiedName);
                        }

                        // Update type method references
                        foreach (var te in index.Types.Values)
                        {
                            if (te.QualifiedMethods.Remove(shortKey))
                                te.QualifiedMethods.Add(qualifiedName);
                        }
                    }
                    else if (!index.Symbols.ContainsKey(qualifiedName))
                    {
                        index.Symbols[qualifiedName] = new SymbolEntry
                        {
                            Id = qualifiedName,
                            Name = funcName,
                            Kind = DetectMethodKind(returnType, trimmed),
                            ReturnType = CleanReturnType(returnType),
                            Signature = sig,
                            Params = ParseParams(parms),
                            MemberOf = className,
                            IsVirtual = returnType.Contains("virtual"),
                            IsStatic = returnType.Contains("static"),
                            IsConst = isConst
                        };
                    }

                    var sym = index.Symbols[qualifiedName];
                    sym.DefinedIn = relativePath + ":" + (i + 1);
                    sym.DefinedInFile = relativePath;
                    sym.BodyLineStart = i + 1;

                    fileEntry.DefinedSymbols.Add(qualifiedName);

                    // Register with class type — try full className, then just the last segment
                    var registered = false;
                    if (index.Types.TryGetValue(className, out var t))
                    {
                        if (!t.Methods.Contains(funcName)) t.Methods.Add(funcName);
                        if (!t.QualifiedMethods.Contains(qualifiedName)) t.QualifiedMethods.Add(qualifiedName);
                        registered = true;
                    }
                    if (!registered)
                    {
                        // className might be "WorldPackets::Taxi::TaxiNodeStatusQuery"
                        // but the type is registered as just "TaxiNodeStatusQuery"
                        var lastClassSep = className.LastIndexOf("::");
                        var shortClassName = lastClassSep >= 0 ? className.Substring(lastClassSep + 2) : className;
                        if (index.Types.TryGetValue(shortClassName, out var t2))
                        {
                            if (!t2.Methods.Contains(funcName)) t2.Methods.Add(funcName);
                            if (!t2.QualifiedMethods.Contains(qualifiedName)) t2.QualifiedMethods.Add(qualifiedName);
                        }
                    }
                }
                // Free function (no ::)
                else if (mDef.Success && !mDef.Groups[2].Value.Contains("::") && ext == ".cpp")
                {
                    var returnType = mDef.Groups[1].Value.Trim();
                    var funcName = mDef.Groups[2].Value;
                    var parms = mDef.Groups[3].Value.Trim();

                    if (!IsCommonKeyword(funcName) && funcName.Length > 1 && !index.Symbols.ContainsKey(funcName))
                    {
                        var sig = returnType + " " + funcName + "(" + parms + ")";
                        index.Symbols[funcName] = new SymbolEntry
                        {
                            Id = funcName,
                            Name = funcName,
                            Kind = returnType.Contains("static") ? "static_function" : "function",
                            ReturnType = CleanReturnType(returnType),
                            Signature = sig,
                            Params = ParseParams(parms),
                            DefinedIn = relativePath + ":" + (i + 1),
                            DefinedInFile = relativePath,
                            BodyLineStart = i + 1,
                            IsStatic = returnType.Contains("static")
                        };

                        fileEntry.DefinedSymbols.Add(funcName);
                    }
                }
                // Inline method definition in .h inside a class body (no :: qualifier)
                // e.g. bool IsDead() const { return m_deathState != ALIVE; }
                else if (mDef.Success && !mDef.Groups[2].Value.Contains("::") && ext == ".h" && currentClass != null)
                {
                    var returnType = mDef.Groups[1].Value.Trim();
                    var funcName = mDef.Groups[2].Value;
                    var parms = mDef.Groups[3].Value.Trim();
                    var isConst = mDef.Groups[4].Success;

                    if (!IsCommonKeyword(funcName) && funcName.Length > 1)
                    {
                        var qualifiedName = currentClass + "::" + funcName;
                        var sig = returnType + " " + qualifiedName + "(" + parms + ")" + (isConst ? " const" : "");

                        if (!index.Symbols.ContainsKey(qualifiedName))
                        {
                            index.Symbols[qualifiedName] = new SymbolEntry
                            {
                                Id = qualifiedName,
                                Name = funcName,
                                Kind = DetectMethodKind(returnType, trimmed),
                                ReturnType = CleanReturnType(returnType),
                                Signature = sig,
                                Params = ParseParams(parms),
                                DeclaredIn = relativePath + ":" + (i + 1),
                                DefinedIn = relativePath + ":" + (i + 1),
                                DefinedInFile = relativePath,
                                BodyLineStart = i + 1,
                                MemberOf = currentClass,
                                IsVirtual = returnType.Contains("virtual"),
                                IsStatic = returnType.Contains("static"),
                                IsConst = isConst
                            };
                        }
                        else
                        {
                            // Update existing declaration-only entry with definition location
                            var existing = index.Symbols[qualifiedName];
                            if (existing.DefinedInFile == null)
                            {
                                existing.DefinedIn = relativePath + ":" + (i + 1);
                                existing.DefinedInFile = relativePath;
                                existing.BodyLineStart = i + 1;
                            }
                        }

                        fileEntry.DefinedSymbols.Add(qualifiedName);

                        if (index.Types.TryGetValue(currentClass, out var t2))
                        {
                            if (!t2.Methods.Contains(funcName)) t2.Methods.Add(funcName);
                            if (!t2.QualifiedMethods.Contains(qualifiedName)) t2.QualifiedMethods.Add(qualifiedName);
                        }
                    }
                }
            }
        }

        // Build convenience lookups
        if (!index.FileToSymbols.ContainsKey(relativePath))
            index.FileToSymbols[relativePath] = new List<string>();
        index.FileToSymbols[relativePath].AddRange(fileEntry.DefinedSymbols);
        index.FileToSymbols[relativePath].AddRange(fileEntry.DeclaredSymbols);

        index.Files[relativePath] = fileEntry;

        // ── File-scope string literal index (FK Layer 2.5) ──
        // Scan the whole file for "..." literals and tokenize them. We deliberately
        // do NOT try to distinguish between "inside a function" and "at file scope"
        // here — we just record everything per-file. This is a superset of the
        // per-symbol literal index, and that's the point: it includes file-scope
        // static initializers like
        //     LootStore LootTemplates_Creature("creature_loot_template", ...);
        // which never make it into any symbol's body extraction. The bundle uses
        // this index as a fallback signal: if a candidate target table appears as
        // a file-scope literal in the same file as a selected consumer, that's
        // strong cross-reference evidence.
        var fileTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            foreach (Match litMatch in RxStringLiteral.Matches(line))
            {
                var literal = litMatch.Groups[1].Value;
                if (literal.Length < 4) continue;
                foreach (Match tokMatch in RxLiteralToken.Matches(literal))
                {
                    var tok = tokMatch.Value.ToLowerInvariant();
                    if (tok.Length < 3) continue;
                    fileTokens.Add(tok);
                }
            }
        }
        if (fileTokens.Count > 0)
        {
            _fileToLiteralTokens[relativePath] = fileTokens;
            foreach (var tok in fileTokens)
            {
                if (!_literalTokenToFiles.TryGetValue(tok, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _literalTokenToFiles[tok] = set;
                }
                set.Add(relativePath);
            }
        }
    }

    // ──────────── Pass 2: Extract bodies, analyze calls ────────────

    private void ExtractBodiesAndCalls(SourceIndex index, string relativePath, string[] lines)
    {
        // Find all symbols defined in this file that have a BodyLineStart
        var symbolsInFile = index.Symbols.Values
            .Where(s => s.DefinedInFile == relativePath && s.BodyLineStart > 0)
            .ToList();

        foreach (var sym in symbolsInFile)
        {
            int startIdx = sym.BodyLineStart - 1; // 0-based
            if (startIdx < 0 || startIdx >= lines.Length) continue;

            // Find the opening brace
            int braceStart = startIdx;
            while (braceStart < lines.Length && !lines[braceStart].Contains("{"))
                braceStart++;

            if (braceStart >= lines.Length) continue;

            // Brace-count to find end
            int depth = 0;
            int endIdx = braceStart;
            for (int i = braceStart; i < lines.Length; i++)
            {
                depth += CountChar(lines[i], '{') - CountChar(lines[i], '}');
                if (depth <= 0)
                {
                    endIdx = i;
                    break;
                }
            }

            sym.BodyLineEnd = endIdx + 1;
            sym.LineCount = sym.BodyLineEnd - sym.BodyLineStart + 1;

            // Extract body text
            var bodyLines = new StringBuilder();
            for (int i = startIdx; i <= endIdx && i < lines.Length; i++)
                bodyLines.AppendLine(lines[i]);

            var bodyText = bodyLines.ToString();

            // Complexity
            sym.Complexity = RxComplexity.Matches(bodyText).Count;

            // ── String literal index build ──
            // Extract every "..." string literal in the body, tokenize each one into
            // identifier-shaped words, and register (token → symbol) in both directions.
            // This powers FindStringReferences and FkResearchBundle later.
            var literalTokensForThisSymbol = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match litMatch in RxStringLiteral.Matches(bodyText))
            {
                var literal = litMatch.Groups[1].Value;
                // Cheap filter: ignore tiny literals and printf format strings that are
                // mostly format codes. SQL literals are always >= ~10 chars.
                if (literal.Length < 4) continue;
                foreach (Match tokMatch in RxLiteralToken.Matches(literal))
                {
                    var tok = tokMatch.Value.ToLowerInvariant();
                    // Skip obvious noise: very short tokens, pure hex artifacts, and
                    // C-style printf width specifiers handled by RxLiteralToken giving
                    // us things like "u", "d", "s". Two chars or less is below FK
                    // identifier length (everything we care about is >= 3 chars).
                    if (tok.Length < 3) continue;
                    literalTokensForThisSymbol.Add(tok);
                }
            }
            // Register
            _symbolToLiteralTokens[sym.Id] = literalTokensForThisSymbol;
            foreach (var tok in literalTokensForThisSymbol)
            {
                if (!_literalTokenToSymbols.TryGetValue(tok, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _literalTokenToSymbols[tok] = set;
                }
                set.Add(sym.Id);
            }

            // ── Member access index build (FK Layer 2) ──
            // Scan body for foo->member and foo.member patterns. Register the
            // member name as referenced by this symbol. This is what lets the FK
            // research bundle answer: "where does the value of CreatureInfo::loot_id
            // actually get used?" — the answer drives FK target inference because
            // the consumer functions are the ones that ultimately query the target
            // table (e.g. creature_loot_template).
            //
            // We filter out: stopword member names (size, first, c_str, etc.),
            // names under 3 chars, and access patterns inside string literals (a
            // crude check — we skip lines that look like they're inside a literal,
            // which we approximate by checking for unbalanced quotes before the
            // match position. In practice, member access inside strings is so
            // rare in C++ this is fine to ignore for correctness purposes).
            var membersForThisSymbol = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match memMatch in RxMemberAccess.Matches(bodyText))
            {
                var memberName = memMatch.Groups[3].Value;
                if (memberName.Length < 3) continue;
                if (MemberNameStopwords.Contains(memberName)) continue;
                // Skip if the "member" is actually a numeric literal or all-uppercase
                // constant — those are most likely enum values or macros, not struct
                // fields. Real SQL-derived struct fields are typically lower_snake_case.
                if (memberName.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c))) continue;
                membersForThisSymbol.Add(memberName);
            }
            // Register
            if (membersForThisSymbol.Count > 0)
            {
                _symbolToMembersReferenced[sym.Id] = membersForThisSymbol;
                foreach (var memberName in membersForThisSymbol)
                {
                    if (!_memberToReferencingSymbols.TryGetValue(memberName, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        _memberToReferencingSymbols[memberName] = set;
                    }
                    set.Add(sym.Id);
                }
            }

            // Find calls to other known symbols
            var calls = new HashSet<string>();
            var typeRefs = new HashSet<string>();

            var callMatches = RxFuncCall.Matches(bodyText);
            foreach (Match m in callMatches)
            {
                var callName = m.Groups[1].Value;
                if (callName == sym.Name || callName == sym.Id) continue; // skip self

                // Direct match to known symbol
                if (index.Symbols.ContainsKey(callName))
                {
                    // Check if this is actually the same function under a shorter qualified name
                    // e.g., sym.Id = "WorldPackets::Taxi::X::Foo" and callName = "X::Foo"
                    if (sym.Id.EndsWith("::" + callName)) continue; // self-call via partial qualification
                    calls.Add(callName);
                    continue;
                }

                // Try qualified: if we're in a class, try ClassName::method
                if (sym.MemberOf != null)
                {
                    var qualified = sym.MemberOf + "::" + callName;
                    if (index.Symbols.ContainsKey(qualified))
                    {
                        if (qualified != sym.Id && !sym.Id.EndsWith("::" + qualified))
                            calls.Add(qualified);
                        continue;
                    }
                }

                // Try parent classes for unqualified calls
                if (!callName.Contains("::") && callName.Length > 2)
                {
                    // Check parent/base classes
                    if (sym.MemberOf != null && index.Types.TryGetValue(sym.MemberOf, out var myType))
                    {
                        foreach (var baseName in myType.Inherits)
                        {
                            var baseQual = baseName + "::" + callName;
                            if (index.Symbols.ContainsKey(baseQual))
                            {
                                if (baseQual != sym.Id)
                                    calls.Add(baseQual);
                                break;
                            }
                        }
                    }
                }
            }

            // Type references in body — extract all word tokens once, then check against type names
            // This is O(tokens + types) instead of O(types × bodyLength) with per-type regex
            var wordTokens = new HashSet<string>();
            foreach (Match wm in Regex.Matches(bodyText, @"\b(\w+)\b"))
                wordTokens.Add(wm.Groups[1].Value);

            foreach (var typeName in index.Types.Keys)
            {
                if (typeName.Length > 2 && wordTokens.Contains(typeName))
                    typeRefs.Add(typeName);
            }

            sym.CallsOut = calls.ToList();
            sym.UsesTypes = typeRefs.ToList();

            // Register with class-to-symbols lookup
            if (sym.MemberOf != null)
            {
                if (!index.ClassToSymbols.ContainsKey(sym.MemberOf))
                    index.ClassToSymbols[sym.MemberOf] = new List<string>();
                if (!index.ClassToSymbols[sym.MemberOf].Contains(sym.Id))
                    index.ClassToSymbols[sym.MemberOf].Add(sym.Id);
            }
        }
    }

    // ──────────── Pass 3: Reverse indices ────────────

    private void BuildReverseIndices(SourceIndex index)
    {
        // CalledBy: invert CallsOut
        foreach (var sym in index.Symbols.Values)
        {
            foreach (var callTarget in sym.CallsOut)
            {
                if (index.Symbols.TryGetValue(callTarget, out var target))
                {
                    if (!target.CalledBy.Contains(sym.Id))
                        target.CalledBy.Add(sym.Id);
                }
            }
        }

        // InheritedBy: invert Inherits
        foreach (var type in index.Types.Values)
        {
            foreach (var baseName in type.Inherits)
            {
                if (index.Types.TryGetValue(baseName, out var baseType))
                {
                    if (!baseType.InheritedBy.Contains(type.Name))
                        baseType.InheritedBy.Add(type.Name);
                }
            }
        }

        // TypeToUsers: invert UsesTypes
        foreach (var sym in index.Symbols.Values)
        {
            foreach (var typeName in sym.UsesTypes)
            {
                if (!index.TypeToUsers.ContainsKey(typeName))
                    index.TypeToUsers[typeName] = new List<string>();
                if (!index.TypeToUsers[typeName].Contains(sym.Id))
                    index.TypeToUsers[typeName].Add(sym.Id);
            }
        }

        // EnumsUsed: scan symbol bodies for enum value names
        var enumValueMap = new Dictionary<string, string>(); // value name → enum name
        foreach (var e in index.Enums.Values)
            foreach (var v in e.Values)
                enumValueMap[v.Name] = e.Name;

        foreach (var sym in index.Symbols.Values)
        {
            // We'll check UsesTypes for now - detailed enum scanning would need body text
            // For efficiency, we check if enum value names appear in the symbol's calls or type refs
            // This is a simplified approach; full body scanning happens if needed
        }
    }

    // ──────────── Trace Export ────────────

    public TraceExport? ExportTrace(string rootSymbol, int depth = 2, bool includeTypes = true, bool includeHeaders = true)
    {
        var idx = _index;
        if (idx == null) return null;
        if (!idx.Symbols.TryGetValue(rootSymbol, out var rootSym)) return null;

        var export = new TraceExport
        {
            RootSymbol = rootSymbol,
            Depth = depth,
            GeneratedAt = DateTime.UtcNow
        };

        // Collect all symbols in the trace
        var visited = new HashSet<string>();
        var symbolsInTrace = new List<string>();
        CollectTraceSymbols(idx, rootSymbol, depth, visited, symbolsInTrace, new HashSet<string>());

        // Build call tree
        var expanded = new HashSet<string>();
        export.CallTree = BuildTraceNode(idx, rootSymbol, depth, new HashSet<string>(), expanded, 0);

        // Collect function bodies
        var filesInScope = new HashSet<string>();
        var typesInScope = new HashSet<string>();

        foreach (var symId in symbolsInTrace)
        {
            if (!idx.Symbols.TryGetValue(symId, out var sym)) continue;

            // Read body from source file
            string body = "";
            if (sym.DefinedInFile != null && sym.BodyLineStart > 0 && sym.BodyLineEnd > 0)
            {
                body = ReadBodyFromSource(idx.SourcePath, sym.DefinedInFile, sym.BodyLineStart, sym.BodyLineEnd);
                filesInScope.Add(sym.DefinedInFile);
            }
            if (sym.DeclaredIn != null)
            {
                var declFile = sym.DeclaredIn.Split(':')[0];
                filesInScope.Add(declFile);
            }

            export.Functions.Add(new TraceFunction
            {
                QualifiedName = sym.Id,
                File = sym.DefinedInFile ?? "",
                LineStart = sym.BodyLineStart,
                LineEnd = sym.BodyLineEnd,
                Signature = sym.Signature,
                Body = body
            });

            if (includeTypes)
            {
                foreach (var t in sym.UsesTypes)
                    typesInScope.Add(t);
                if (sym.MemberOf != null)
                    typesInScope.Add(sym.MemberOf);
            }
        }

        // Build file refs
        if (includeHeaders)
        {
            // Also include headers that the in-scope files include
            var expandedFiles = new HashSet<string>(filesInScope);
            foreach (var f in filesInScope.ToList())
            {
                if (idx.Files.TryGetValue(f, out var fe))
                {
                    foreach (var inc in fe.Includes)
                    {
                        // Try to find the included file in the index
                        var match = idx.Files.Keys.FirstOrDefault(k => k.EndsWith("/" + inc) || k == inc);
                        if (match != null) expandedFiles.Add(match);
                    }
                }
            }

            foreach (var f in expandedFiles)
            {
                if (idx.Files.TryGetValue(f, out var fe))
                    export.FilesInScope.Add(new TraceFileRef { Path = f, Includes = fe.Includes });
            }
        }

        // Build type refs
        if (includeTypes)
        {
            // Also add parent classes
            foreach (var tn in typesInScope.ToList())
            {
                if (idx.Types.TryGetValue(tn, out var te))
                    foreach (var baseName in te.Inherits)
                        typesInScope.Add(baseName);
            }

            foreach (var tn in typesInScope)
            {
                if (idx.Types.TryGetValue(tn, out var te))
                {
                    export.TypesInScope.Add(new TraceTypeRef
                    {
                        Name = te.Name,
                        Kind = te.Kind,
                        DeclaredIn = te.DeclaredIn,
                        Inherits = te.Inherits,
                        Members = te.Members
                    });
                }
            }

            // Enums used by types in scope
            foreach (var tn in typesInScope)
            {
                if (idx.Types.TryGetValue(tn, out var te))
                {
                    foreach (var en in te.EnumsUsed)
                    {
                        if (idx.Enums.TryGetValue(en, out var ee))
                        {
                            export.EnumsInScope.Add(new TraceEnumRef
                            {
                                Name = ee.Name,
                                DeclaredIn = ee.DeclaredIn,
                                Values = ee.Values
                            });
                        }
                    }
                }
            }
        }

        // Stats
        export.TotalFunctions = export.Functions.Count;
        export.TotalLines = export.Functions.Sum(f => f.Body.Split('\n').Length);
        export.EstimatedTokens = export.Functions.Sum(f => f.Body.Length) / 4;

        // Format text
        export.FormattedText = FormatTraceText(export);

        return export;
    }

    private void CollectTraceSymbols(SourceIndex idx, string symbolId, int remainingDepth,
        HashSet<string> visited, List<string> result, HashSet<string> ancestors)
    {
        if (visited.Contains(symbolId) || remainingDepth < 0) return;
        if (ancestors.Contains(symbolId)) return; // cycle

        visited.Add(symbolId);
        result.Add(symbolId);

        if (!idx.Symbols.TryGetValue(symbolId, out var sym)) return;
        if (remainingDepth <= 0) return;

        var newAncestors = new HashSet<string>(ancestors) { symbolId };
        foreach (var callId in sym.CallsOut)
            CollectTraceSymbols(idx, callId, remainingDepth - 1, visited, result, newAncestors);
    }

    private TraceNode BuildTraceNode(SourceIndex idx, string symbolId, int remainingDepth,
        HashSet<string> ancestors, HashSet<string> expanded, int currentDepth)
    {
        if (ancestors.Contains(symbolId))
            return new TraceNode { Symbol = symbolId, Depth = currentDepth, IsCycle = true, IsLeaf = true };

        if (!idx.Symbols.TryGetValue(symbolId, out var sym))
            return new TraceNode { Symbol = symbolId, Depth = currentDepth, IsLeaf = true };

        if (expanded.Contains(symbolId))
            return new TraceNode { Symbol = symbolId, Depth = currentDepth, IsRef = true, IsLeaf = true };

        expanded.Add(symbolId);

        if (remainingDepth <= 0 || sym.CallsOut.Count == 0)
            return new TraceNode { Symbol = symbolId, Depth = currentDepth, IsLeaf = sym.CallsOut.Count == 0 };

        var newAncestors = new HashSet<string>(ancestors) { symbolId };
        var children = new List<TraceNode>();

        foreach (var callId in sym.CallsOut)
            children.Add(BuildTraceNode(idx, callId, remainingDepth - 1, newAncestors, expanded, currentDepth + 1));

        return new TraceNode
        {
            Symbol = symbolId,
            Depth = currentDepth,
            Children = children
        };
    }

    private string ReadBodyFromSource(string sourcePath, string relativeFile, int lineStart, int lineEnd)
    {
        try
        {
            var fullPath = Path.Combine(sourcePath, relativeFile);
            if (!File.Exists(fullPath)) return "";

            var lines = File.ReadAllLines(fullPath);
            var sb = new StringBuilder();
            for (int i = lineStart - 1; i < lineEnd && i < lines.Length; i++)
                sb.AppendLine(lines[i]);
            return sb.ToString().TrimEnd();
        }
        catch { return ""; }
    }

    private string FormatTraceText(TraceExport export)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== TRACE: {export.RootSymbol} (depth {export.Depth}) ===");
        sb.AppendLine($"=== Generated: {export.GeneratedAt:yyyy-MM-ddTHH:mm:ss} ===");
        sb.AppendLine();

        // Files
        if (export.FilesInScope.Count > 0)
        {
            sb.AppendLine("--- FILES IN SCOPE ---");
            foreach (var f in export.FilesInScope)
                sb.AppendLine($"{f.Path} (includes: {string.Join(", ", f.Includes.Take(10))}{(f.Includes.Count > 10 ? ", ..." : "")})");
            sb.AppendLine();
        }

        // Types
        if (export.TypesInScope.Count > 0)
        {
            sb.AppendLine("--- TYPES IN SCOPE ---");
            foreach (var t in export.TypesInScope)
            {
                var inheritance = t.Inherits.Count > 0 ? " : " + string.Join(", ", t.Inherits) : "";
                sb.AppendLine($"[{t.Kind}] {t.Name}{inheritance} (declared in {t.DeclaredIn})");
                if (t.Members.Count > 0)
                    sb.AppendLine($"  Members: {string.Join(", ", t.Members.Take(15))}{(t.Members.Count > 15 ? ", ..." : "")}");
            }
            sb.AppendLine();
        }

        // Enums
        if (export.EnumsInScope.Count > 0)
        {
            sb.AppendLine("--- ENUMS IN SCOPE ---");
            foreach (var e in export.EnumsInScope)
            {
                sb.AppendLine($"[{e.Name}] (declared in {e.DeclaredIn})");
                foreach (var v in e.Values.Take(20))
                    sb.AppendLine($"  {v.Name} = {v.Value ?? "?"}");
                if (e.Values.Count > 20)
                    sb.AppendLine($"  ... and {e.Values.Count - 20} more");
            }
            sb.AppendLine();
        }

        // Function bodies
        sb.AppendLine("--- FUNCTION BODIES ---");
        foreach (var f in export.Functions)
        {
            sb.AppendLine();
            sb.AppendLine($"// {f.File}:{f.LineStart}-{f.LineEnd}");
            sb.AppendLine(f.Body);
        }
        sb.AppendLine();

        // Call tree
        if (export.CallTree != null)
        {
            sb.AppendLine("--- CALL CHAIN ---");
            FormatTraceTree(sb, export.CallTree, "", true);
        }

        return sb.ToString();
    }

    private void FormatTraceTree(StringBuilder sb, TraceNode node, string prefix, bool isLast)
    {
        var connector = prefix.Length == 0 ? "" : (isLast ? "└── " : "├── ");
        var suffix = node.IsCycle ? " [CYCLE]" : node.IsRef ? " [ref]" : node.IsLeaf ? " (leaf)" : "";
        sb.AppendLine($"{prefix}{connector}{node.Symbol}{suffix}");

        if (node.Children.Count > 0)
        {
            var childPrefix = prefix + (prefix.Length == 0 ? "" : (isLast ? "    " : "│   "));
            for (int i = 0; i < node.Children.Count; i++)
                FormatTraceTree(sb, node.Children[i], childPrefix, i == node.Children.Count - 1);
        }
    }

    // ──────────── Topic Explorer ────────────

    /// <summary>
    /// Analyzes all symbols/types/enums/files matching a query and produces a
    /// structured intelligence report: entry points, core logic, helpers,
    /// related types/enums, file map, and inter-function call flows.
    /// </summary>
    public TopicReport? ExploreTopic(string query)
    {
        var idx = _index;
        if (idx == null || string.IsNullOrWhiteSpace(query)) return null;

        var terms = query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Collect all matching symbols
        var matchedSymbols = new List<SymbolEntry>();
        foreach (var sym in idx.Symbols.Values)
        {
            var haystack = (sym.Id + " " + sym.Signature).ToLowerInvariant();
            if (terms.All(t => haystack.Contains(t)))
                matchedSymbols.Add(sym);
        }

        // Collect matching types
        var matchedTypes = new List<TypeEntry>();
        foreach (var t in idx.Types.Values)
        {
            if (terms.All(term => t.Name.ToLowerInvariant().Contains(term)))
                matchedTypes.Add(t);
        }

        // Collect matching enums (name or values)
        var matchedEnums = new List<EnumEntry>();
        foreach (var e in idx.Enums.Values)
        {
            var haystack = e.Name.ToLowerInvariant() + " " + string.Join(" ", e.Values.Select(v => v.Name.ToLowerInvariant()));
            if (terms.All(t => haystack.Contains(t)))
                matchedEnums.Add(e);
        }

        // Collect matching files
        var matchedFiles = new List<FileEntry>();
        foreach (var f in idx.Files.Values)
        {
            if (terms.All(t => f.Path.ToLowerInvariant().Contains(t)))
                matchedFiles.Add(f);
        }

        if (matchedSymbols.Count == 0 && matchedTypes.Count == 0 && matchedEnums.Count == 0 && matchedFiles.Count == 0)
            return null;

        // ── Classify symbols ──

        // Entry points: 0 callers among matched set OR high caller count from outside
        // These are opcode handlers, public API entry functions
        var entryPoints = new List<TopicSymbol>();
        var coreLogic = new List<TopicSymbol>();
        var helpers = new List<TopicSymbol>();
        var allOther = new List<TopicSymbol>();

        // Build set of matched IDs for internal reference detection
        var matchedIds = new HashSet<string>(matchedSymbols.Select(s => s.Id));

        foreach (var sym in matchedSymbols)
        {
            // Count callers from OUTSIDE the matched set (external callers)
            var externalCallers = sym.CalledBy.Count(c => !matchedIds.Contains(c));
            var internalCallers = sym.CalledBy.Count(c => matchedIds.Contains(c));
            var externalCalls = sym.CallsOut.Count(c => !matchedIds.Contains(c));
            var internalCalls = sym.CallsOut.Count(c => matchedIds.Contains(c));

            var ts = new TopicSymbol
            {
                Id = sym.Id,
                Name = sym.Name,
                Signature = sym.Signature,
                File = sym.DefinedInFile ?? "",
                LineStart = sym.BodyLineStart,
                LineEnd = sym.BodyLineEnd,
                LineCount = sym.LineCount,
                MemberOf = sym.MemberOf,
                Kind = sym.Kind,
                TotalCallers = sym.CalledBy.Count,
                TotalCalls = sym.CallsOut.Count,
                ExternalCallers = externalCallers,
                InternalCallers = internalCallers,
                ExternalCalls = externalCalls,
                InternalCalls = internalCalls,
                Complexity = sym.Complexity,
                IsVirtual = sym.IsVirtual
            };

            // Classification logic:
            // Entry point: no callers at all (opcode handler) or only called from outside the topic
            if (sym.CalledBy.Count == 0 || (internalCallers == 0 && externalCallers > 0))
                entryPoints.Add(ts);
            // Core logic: called by other topic functions AND calls other topic functions
            else if (internalCallers > 0 && internalCalls > 0)
                coreLogic.Add(ts);
            // Helper: called by topic functions but doesn't call other topic functions
            else if (internalCallers > 0 && internalCalls == 0)
                helpers.Add(ts);
            else
                allOther.Add(ts);
        }

        // Sort each category by relevance
        entryPoints = entryPoints.OrderByDescending(s => s.TotalCalls).ToList();
        coreLogic = coreLogic.OrderByDescending(s => s.InternalCallers + s.InternalCalls).ToList();
        helpers = helpers.OrderByDescending(s => s.TotalCallers).ToList();
        allOther = allOther.OrderByDescending(s => s.TotalCallers + s.TotalCalls).ToList();

        // ── Build call flow among matched symbols ──
        var callFlows = new List<TopicCallFlow>();
        foreach (var sym in matchedSymbols.Where(s => s.CallsOut.Any(c => matchedIds.Contains(c))))
        {
            var targets = sym.CallsOut.Where(c => matchedIds.Contains(c)).ToList();
            if (targets.Count > 0)
            {
                callFlows.Add(new TopicCallFlow
                {
                    From = sym.Id,
                    To = targets
                });
            }
        }

        // ── Build file summary ──
        // Group matched symbols by file
        var fileMap = new Dictionary<string, TopicFile>();
        foreach (var sym in matchedSymbols)
        {
            // Prefer DefinedInFile (the .cpp), fall back to DeclaredIn header file
            var file = sym.DefinedInFile;
            if (file == null && sym.DeclaredIn != null)
                file = sym.DeclaredIn.Split(':')[0]; // DeclaredIn is "path:line" format
            file ??= "(unknown)";
            if (!fileMap.ContainsKey(file))
            {
                var fe = idx.Files.TryGetValue(file, out var f) ? f : null;
                fileMap[file] = new TopicFile
                {
                    Path = file,
                    TotalFunctions = fe?.DefinedSymbols.Count ?? 0,
                    MatchedFunctions = new List<string>()
                };
            }
            fileMap[file].MatchedFunctions.Add(sym.Id);
        }

        // ── Build type summary ──
        var typeSummaries = matchedTypes.Select(t => new TopicType
        {
            Name = t.Name,
            Kind = t.Kind,
            DeclaredIn = t.DeclaredIn,
            Inherits = t.Inherits,
            InheritedBy = t.InheritedBy,
            MethodCount = t.Methods.Count,
            MemberCount = t.Members.Count,
            TopMethods = t.QualifiedMethods.Take(10).ToList()
        }).ToList();

        // Also find types that are heavily used by the matched symbols but weren't in the name match
        var relatedTypes = new Dictionary<string, int>();
        foreach (var sym in matchedSymbols)
        {
            foreach (var typeName in sym.UsesTypes)
            {
                if (!matchedTypes.Any(t => t.Name == typeName))
                {
                    relatedTypes[typeName] = relatedTypes.GetValueOrDefault(typeName) + 1;
                }
            }
        }
        var topRelatedTypes = relatedTypes
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv =>
            {
                var t = idx.Types.TryGetValue(kv.Key, out var te) ? te : null;
                return new TopicType
                {
                    Name = kv.Key,
                    Kind = t?.Kind ?? "?",
                    DeclaredIn = t?.DeclaredIn ?? "",
                    MethodCount = t?.Methods.Count ?? 0,
                    MemberCount = t?.Members.Count ?? 0,
                    UsedByCount = kv.Value
                };
            })
            .ToList();

        // ── Build enum summary ──
        var enumSummaries = matchedEnums.Select(e => new TopicEnum
        {
            Name = e.Name,
            Kind = e.Kind,
            DeclaredIn = e.DeclaredIn,
            ValueCount = e.Values.Count,
            SampleValues = e.Values.Take(8).Select(v => v.Name).ToList()
        }).ToList();

        // ── Formatted text for LLM consumption ──
        var report = new TopicReport
        {
            Query = query,
            GeneratedAt = DateTime.UtcNow,
            TotalSymbols = matchedSymbols.Count,
            TotalTypes = matchedTypes.Count,
            TotalEnums = matchedEnums.Count,
            TotalFiles = fileMap.Count,
            EntryPoints = entryPoints,
            CoreLogic = coreLogic,
            Helpers = helpers,
            Other = allOther,
            Types = typeSummaries,
            RelatedTypes = topRelatedTypes,
            Enums = enumSummaries,
            Files = fileMap.Values.OrderByDescending(f => f.MatchedFunctions.Count).ToList(),
            CallFlows = callFlows
        };

        report.FormattedText = FormatTopicReport(report);

        return report;
    }

    private string FormatTopicReport(TopicReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== TOPIC: \"{r.Query}\" ===");
        sb.AppendLine($"=== {r.TotalSymbols} functions, {r.TotalTypes} types, {r.TotalEnums} enums, {r.TotalFiles} files ===");
        sb.AppendLine();

        if (r.EntryPoints.Count > 0)
        {
            sb.AppendLine("--- ENTRY POINTS (external triggers / opcode handlers) ---");
            foreach (var s in r.EntryPoints)
                sb.AppendLine($"  {s.Id,-55} {s.TotalCallers,3} callers → calls {s.TotalCalls,3}  [{s.File}:{s.LineStart}]");
            sb.AppendLine();
        }

        if (r.CoreLogic.Count > 0)
        {
            sb.AppendLine("--- CORE LOGIC (called by + calls other topic functions) ---");
            foreach (var s in r.CoreLogic)
                sb.AppendLine($"  {s.Id,-55} {s.InternalCallers,3} in-callers, {s.InternalCalls,3} in-calls, {s.LineCount,4} lines  [{s.File}:{s.LineStart}]");
            sb.AppendLine();
        }

        if (r.Helpers.Count > 0)
        {
            sb.AppendLine("--- HELPERS (called by topic, doesn't call back) ---");
            foreach (var s in r.Helpers)
                sb.AppendLine($"  {s.Id,-55} {s.TotalCallers,3} callers, {s.LineCount,4} lines  [{s.File}:{s.LineStart}]");
            sb.AppendLine();
        }

        if (r.Other.Count > 0)
        {
            sb.AppendLine("--- OTHER MATCHED ---");
            foreach (var s in r.Other)
                sb.AppendLine($"  {s.Id,-55} {s.TotalCallers,3} callers, {s.TotalCalls,3} calls  [{s.File}:{s.LineStart}]");
            sb.AppendLine();
        }

        if (r.Types.Count > 0)
        {
            sb.AppendLine("--- TYPES (directly matched) ---");
            foreach (var t in r.Types)
            {
                var inh = t.Inherits?.Count > 0 ? " : " + string.Join(", ", t.Inherits) : "";
                sb.AppendLine($"  [{t.Kind}] {t.Name}{inh}  ({t.MethodCount} methods, {t.MemberCount} members)  [{t.DeclaredIn}]");
            }
            sb.AppendLine();
        }

        if (r.RelatedTypes.Count > 0)
        {
            sb.AppendLine("--- RELATED TYPES (used by matched functions) ---");
            foreach (var t in r.RelatedTypes)
                sb.AppendLine($"  {t.Name,-30} used by {t.UsedByCount} matched functions  [{t.DeclaredIn}]");
            sb.AppendLine();
        }

        if (r.Enums.Count > 0)
        {
            sb.AppendLine("--- ENUMS ---");
            foreach (var e in r.Enums)
                sb.AppendLine($"  {e.Name,-30} {e.ValueCount} values: {string.Join(", ", e.SampleValues)}{(e.ValueCount > 8 ? ", ..." : "")}");
            sb.AppendLine();
        }

        if (r.Files.Count > 0)
        {
            sb.AppendLine("--- FILES ---");
            foreach (var f in r.Files)
                sb.AppendLine($"  {f.Path,-55} {f.MatchedFunctions.Count} matched / {f.TotalFunctions} total functions");
            sb.AppendLine();
        }

        if (r.CallFlows.Count > 0)
        {
            sb.AppendLine("--- INTERNAL CALL FLOW (within topic) ---");
            foreach (var flow in r.CallFlows)
            {
                var shortFrom = flow.From.Contains("::") ? flow.From : flow.From;
                sb.AppendLine($"  {shortFrom} → {string.Join(", ", flow.To)}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ──────────── Parsing helpers ────────────

    private EnumEntry? ParseEnum(string name, string kind, string file, string[] lines, int startLine)
    {
        var entry = new EnumEntry
        {
            Name = name,
            Kind = kind,
            DeclaredIn = file,
            LineStart = startLine + 1
        };

        // Find opening brace if not on same line
        int i = startLine;
        while (i < lines.Length && !lines[i].Contains("{")) i++;
        if (i >= lines.Length) return null;

        // Scan values until closing brace
        int depth = 0;
        for (int j = i; j < lines.Length && j < startLine + 500; j++) // safety cap
        {
            depth += CountChar(lines[j], '{') - CountChar(lines[j], '}');

            var trimmed = lines[j].Trim();
            // Match enum value: NAME = VALUE, or NAME,
            var match = Regex.Match(trimmed, @"^(\w+)\s*(?:=\s*([^,/]+))?\s*,?\s*(?://.*)?$");
            if (match.Success && match.Groups[1].Value != "{" && match.Groups[1].Value != "}")
            {
                var valName = match.Groups[1].Value;
                if (!IsCommonKeyword(valName) && valName.Length > 1)
                {
                    entry.Values.Add(new EnumValue
                    {
                        Name = valName,
                        Value = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null
                    });
                }
            }

            if (depth <= 0 && j > i)
            {
                entry.LineEnd = j + 1;
                break;
            }
        }

        if (entry.LineEnd == 0) entry.LineEnd = entry.LineStart;
        return entry.Values.Count > 0 ? entry : null;
    }

    private static List<string> ParseParams(string paramsStr)
    {
        if (string.IsNullOrWhiteSpace(paramsStr)) return new();
        return paramsStr.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && p != "void")
            .ToList();
    }

    private static string DetectMethodKind(string returnType, string line)
    {
        if (returnType.Contains("virtual") || line.Contains("override")) return "virtual_method";
        if (returnType.Contains("static")) return "static_method";
        return "method";
    }

    private static string CleanReturnType(string raw)
    {
        return raw.Replace("virtual ", "").Replace("static ", "").Replace("inline ", "")
                  .Replace("explicit ", "").Trim();
    }

    private static int CountChar(string s, char c)
    {
        int count = 0;
        foreach (var ch in s)
            if (ch == c) count++;
        return count;
    }

    private static bool IsCommonKeyword(string name)
    {
        return name switch
        {
            "if" or "else" or "for" or "while" or "do" or "switch" or "case" or "default" or "return"
            or "break" or "continue" or "goto" or "try" or "catch" or "throw" or "new" or "delete"
            or "this" or "true" or "false" or "void" or "int" or "char" or "float" or "double"
            or "bool" or "long" or "short" or "unsigned" or "signed" or "const" or "static"
            or "inline" or "virtual" or "override" or "explicit" or "nullptr" or "NULL"
            or "sizeof" or "typedef" or "using" or "namespace" or "template" or "typename"
            or "public" or "private" or "protected" or "struct" or "class" or "enum" or "union"
            or "auto" or "register" or "volatile" or "extern" or "mutable" or "friend"
            or "operator" or "ASSERT" or "LOG" or "sLog" or "printf" or "sprintf" or "snprintf"
            => true,
            _ => false
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    //
    //                    FK RESEARCH — string-literal lookups
    //
    // The methods below power the foreign-key relationship mining pipeline.
    // They use the inverted index built during ExtractBodiesAndCalls to find
    // every C++ function whose body contains specific SQL identifiers inside
    // string literals — i.e. every loader, every query, every reference.
    //
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Find every indexed symbol whose body contains the given needles as tokens
    /// inside C++ string literals. O(needles + result_size) — does not re-scan
    /// any source code; uses the inverted index built during reindex.
    /// </summary>
    /// <param name="needles">Token strings to look for (case-insensitive).</param>
    /// <param name="requireAll">If true, only return symbols matching ALL needles.</param>
    /// <param name="maxResults">Cap on result count (0 = no cap).</param>
    public FindStringReferencesResult FindStringReferences(
        IEnumerable<string> needles, bool requireAll = false, int maxResults = 0)
    {
        var result = new FindStringReferencesResult
        {
            Needles = needles?.ToList() ?? new List<string>(),
            RequireAll = requireAll
        };

        var idx = _index;
        if (idx == null || result.Needles.Count == 0) return result;

        // Normalize needles to the same casing used by the index.
        var normalizedNeedles = result.Needles
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        if (normalizedNeedles.Count == 0) return result;

        // For each needle, get the set of symbols whose literals contain it.
        // Empty set if the needle was never indexed.
        var perNeedleSets = new List<(string needle, HashSet<string> symbols)>();
        foreach (var n in normalizedNeedles)
        {
            if (_literalTokenToSymbols.TryGetValue(n, out var set))
                perNeedleSets.Add((n, set));
            else
                perNeedleSets.Add((n, new HashSet<string>(StringComparer.Ordinal)));
        }

        // Compute candidate set
        HashSet<string> candidates;
        if (requireAll)
        {
            // Intersection of all per-needle sets. Start with the smallest set
            // to minimize the intersection cost.
            var ordered = perNeedleSets.OrderBy(p => p.symbols.Count).ToList();
            if (ordered[0].symbols.Count == 0) return result;
            candidates = new HashSet<string>(ordered[0].symbols, StringComparer.Ordinal);
            for (int i = 1; i < ordered.Count; i++)
                candidates.IntersectWith(ordered[i].symbols);
        }
        else
        {
            // Union of all per-needle sets.
            candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, set) in perNeedleSets)
                candidates.UnionWith(set);
        }

        // Build response matches with per-symbol metadata
        var matches = new List<StringReferenceMatch>(candidates.Count);
        foreach (var symId in candidates)
        {
            if (!idx.Symbols.TryGetValue(symId, out var sym)) continue;
            if (!_symbolToLiteralTokens.TryGetValue(symId, out var symTokens)) continue;

            var matchingNeedles = new List<string>();
            int needlesMatched = 0;
            foreach (var n in normalizedNeedles)
            {
                if (symTokens.Contains(n))
                {
                    matchingNeedles.Add(n);
                    needlesMatched++;
                }
            }

            matches.Add(new StringReferenceMatch
            {
                SymbolId = sym.Id,
                Name = sym.Name,
                MemberOf = sym.MemberOf,
                File = sym.DefinedInFile,
                LineStart = sym.BodyLineStart,
                LineEnd = sym.BodyLineEnd,
                NeedlesMatched = needlesMatched,
                MatchingNeedles = matchingNeedles,
                // OccurrenceCount is approximated as needlesMatched — we don't track
                // multiplicity in the inverted index. If exact counts are ever needed,
                // re-scan the body for that symbol only.
                OccurrenceCount = needlesMatched
            });
        }

        // Rank: more needles matched first, then larger functions (more context).
        matches.Sort((a, b) =>
        {
            var byNeedles = b.NeedlesMatched.CompareTo(a.NeedlesMatched);
            if (byNeedles != 0) return byNeedles;
            return (b.LineEnd - b.LineStart).CompareTo(a.LineEnd - a.LineStart);
        });

        if (maxResults > 0 && matches.Count > maxResults)
            matches = matches.Take(maxResults).ToList();

        result.Matches = matches;
        result.TotalMatches = matches.Count;
        return result;
    }

    /// <summary>
    /// Find every indexed symbol whose body references the given struct member
    /// names via `foo->member` or `foo.member` access patterns. This is the FK
    /// Layer 2 lookup: it finds CONSUMERS of a struct field, not just the loader.
    ///
    /// Member names are matched case-insensitively (the index is OrdinalIgnoreCase).
    /// Pass multiple names to find any-of (union); use requireAll=true for AND.
    /// </summary>
    public FindStringReferencesResult FindMemberReferences(
        IEnumerable<string> memberNames, bool requireAll = false, int maxResults = 0)
    {
        // Re-use the same response shape as FindStringReferences — same essential
        // semantics (which symbols mention these tokens) but the index queried is
        // different (member references vs string literal tokens).
        var result = new FindStringReferencesResult
        {
            Needles = memberNames?.ToList() ?? new List<string>(),
            RequireAll = requireAll
        };

        var idx = _index;
        if (idx == null || result.Needles.Count == 0) return result;

        var normalized = result.Needles
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0) return result;

        var perNeedleSets = new List<(string needle, HashSet<string> symbols)>();
        foreach (var n in normalized)
        {
            if (_memberToReferencingSymbols.TryGetValue(n, out var set))
                perNeedleSets.Add((n, set));
            else
                perNeedleSets.Add((n, new HashSet<string>(StringComparer.Ordinal)));
        }

        HashSet<string> candidates;
        if (requireAll)
        {
            var ordered = perNeedleSets.OrderBy(p => p.symbols.Count).ToList();
            if (ordered[0].symbols.Count == 0) return result;
            candidates = new HashSet<string>(ordered[0].symbols, StringComparer.Ordinal);
            for (int i = 1; i < ordered.Count; i++)
                candidates.IntersectWith(ordered[i].symbols);
        }
        else
        {
            candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, set) in perNeedleSets)
                candidates.UnionWith(set);
        }

        var matches = new List<StringReferenceMatch>(candidates.Count);
        foreach (var symId in candidates)
        {
            if (!idx.Symbols.TryGetValue(symId, out var sym)) continue;
            if (!_symbolToMembersReferenced.TryGetValue(symId, out var symMembers)) continue;

            var matching = new List<string>();
            int matchedCount = 0;
            foreach (var n in normalized)
            {
                if (symMembers.Contains(n))
                {
                    matching.Add(n);
                    matchedCount++;
                }
            }

            matches.Add(new StringReferenceMatch
            {
                SymbolId = sym.Id,
                Name = sym.Name,
                MemberOf = sym.MemberOf,
                File = sym.DefinedInFile,
                LineStart = sym.BodyLineStart,
                LineEnd = sym.BodyLineEnd,
                NeedlesMatched = matchedCount,
                MatchingNeedles = matching,
                OccurrenceCount = matchedCount
            });
        }

        matches.Sort((a, b) =>
        {
            var byNeedles = b.NeedlesMatched.CompareTo(a.NeedlesMatched);
            if (byNeedles != 0) return byNeedles;
            return (b.LineEnd - b.LineStart).CompareTo(a.LineEnd - a.LineStart);
        });

        if (maxResults > 0 && matches.Count > maxResults)
            matches = matches.Take(maxResults).ToList();

        result.Matches = matches;
        result.TotalMatches = matches.Count;
        return result;
    }

    /// <summary>
    /// Given a loader symbol and a SQL column name, attempt to resolve which C++
    /// struct member that column flows into. Looks for patterns like:
    ///
    ///     pInfo->loot_id = fields[50].GetUInt32();
    ///     creatureInfo.loot_id = fields[7].GetUInt32();
    ///
    /// in the loader's body or in any function the loader calls. Returns the
    /// best-match member name (most-frequent), or null if no assignment pattern
    /// found. The column name is matched case-insensitively and we tolerate both
    /// exact column-name matches and the common case where the C++ member name
    /// happens to equal the column name (the dominant pattern in VMaNGOS).
    /// </summary>
    public string? ResolveColumnToStructMember(SymbolEntry loader, string columnName)
    {
        var idx = _index;
        if (idx == null) return null;
        if (string.IsNullOrWhiteSpace(columnName)) return null;
        var colLow = columnName.Trim().ToLowerInvariant();

        // Collect bodies to scan: the loader itself + every function it calls
        // directly. The fields[N] = ... pattern typically lives in either the
        // loader (when it parses results inline) or in a helper like LoadCreatureInfo.
        var scanQueue = new List<SymbolEntry> { loader };
        foreach (var calleeId in loader.CallsOut)
        {
            if (idx.Symbols.TryGetValue(calleeId, out var cs))
                scanQueue.Add(cs);
        }

        // Tally: member name → occurrence count
        var memberHits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var sym in scanQueue)
        {
            if (sym.DefinedInFile == null || sym.BodyLineStart <= 0 || sym.BodyLineEnd <= 0)
                continue;
            var body = ReadBodyFromSource(idx.SourcePath, sym.DefinedInFile, sym.BodyLineStart, sym.BodyLineEnd);
            if (string.IsNullOrEmpty(body)) continue;

            foreach (Match m in RxFieldsAssignment.Matches(body))
            {
                var memberName = m.Groups[3].Value;
                if (string.IsNullOrEmpty(memberName)) continue;
                if (!memberHits.TryGetValue(memberName, out var n)) n = 0;
                memberHits[memberName] = n + 1;
            }
        }

        if (memberHits.Count == 0) return null;

        // STRICT: only return an exact case-insensitive match for the column name.
        // The previous behavior was to fall back to the most-frequent member when
        // no exact match was found, but that caused noise — for a 17-line loader
        // that just dispatches to LoadCreatureInfo, the "most frequent" was just
        // whatever member name happened to appear first in fields[N] = ... lines
        // (typically `name`), which is unrelated to the actual column the caller
        // asked about. Returning null is safer; the bundle's column-name fallback
        // will handle the case where the C++ field name happens to equal the
        // SQL column name (the dominant VMaNGOS pattern).
        var exact = memberHits.Keys.FirstOrDefault(k =>
            k.Equals(colLow, StringComparison.OrdinalIgnoreCase));
        return exact; // null if no exact match
    }

    /// <summary>
    /// Assemble a complete FK research bundle for a (db, table, column) triple.
    /// This is THE high-level endpoint for the foreign-key mining pipeline: it
    /// returns prompt-ready code that the LLM can analyze for FK relationships.
    /// </summary>
    public FkResearchBundleResult? BuildFkResearchBundle(FkResearchBundleRequest req)
    {
        var idx = _index;
        if (idx == null) return null;
        if (string.IsNullOrWhiteSpace(req.Table) || string.IsNullOrWhiteSpace(req.Column))
            return null;

        var result = new FkResearchBundleResult
        {
            Db = req.Db,
            Table = req.Table,
            Column = req.Column,
            GeneratedAt = DateTime.UtcNow
        };

        var tableLow = req.Table.Trim().ToLowerInvariant();
        var colLow = req.Column.Trim().ToLowerInvariant();

        // ── 1. Primary hits: symbols mentioning BOTH the table AND the column ──
        var primaryRes = FindStringReferences(new[] { tableLow, colLow }, requireAll: true, maxResults: 0);

        // ── 2. Column-only hits: symbols mentioning the column but NOT the table ──
        //     (loaders for other tables that JOIN against this column, etc.)
        var columnAnyRes = FindStringReferences(new[] { colLow }, requireAll: true, maxResults: 0);
        var primaryIds = new HashSet<string>(primaryRes.Matches.Select(m => m.SymbolId), StringComparer.Ordinal);
        var columnOnlyHits = columnAnyRes.Matches.Where(m => !primaryIds.Contains(m.SymbolId)).ToList();

        // ── 3. Table-only hits: symbols mentioning the table but NOT the column ──
        //     (other queries against the same table — sometimes useful sibling context,
        //     but usually noise; default IncludeTableOnly=false.)
        var tableAnyRes = FindStringReferences(new[] { tableLow }, requireAll: true, maxResults: 0);
        var tableOnlyHits = tableAnyRes.Matches
            .Where(m => !primaryIds.Contains(m.SymbolId))
            .ToList();

        result.NeedlesSearched.Add(tableLow);
        result.NeedlesSearched.Add(colLow);

        // ── Pre-compute candidate-table mention counts per symbol ──
        // Used both for saturation filtering AND for the per-symbol AlsoReferences list.
        var candidateLow = (req.CandidateTargetTables ?? new List<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t != tableLow) // don't bother flagging the source table itself
            .Distinct()
            .ToList();

        int CountCandidateMentions(string symId)
        {
            if (candidateLow.Count == 0) return 0;
            if (!_symbolToLiteralTokens.TryGetValue(symId, out var symTokens)) return 0;
            int n = 0;
            foreach (var c in candidateLow)
                if (symTokens.Contains(c)) n++;
            return n;
        }

        List<string> AlsoRefsFor(string symId)
        {
            var refs = new List<string>();
            if (candidateLow.Count == 0) return refs;
            if (!_symbolToLiteralTokens.TryGetValue(symId, out var symTokens)) return refs;
            foreach (var c in candidateLow)
                if (symTokens.Contains(c)) refs.Add(c);
            return refs;
        }

        // ── Helper: estimate tokens for a symbol (body length / 4 is rule-of-thumb) ──
        int EstimateTokens(SymbolEntry s)
        {
            int lines = (s.BodyLineEnd > 0 && s.BodyLineStart > 0)
                ? (s.BodyLineEnd - s.BodyLineStart + 1) : 0;
            // Rough char-per-line estimate for C++ code: ~60. Tokens ≈ chars / 4.
            return (lines * 60) / 4;
        }

        bool IsSaturated(string symId)
        {
            if (candidateLow.Count == 0 || req.SaturationThreshold <= 0) return false;
            return CountCandidateMentions(symId) >= req.SaturationThreshold;
        }

        // ── Build the prioritized candidate queue ──
        // Order: primary → column_only → callers/callees of primary → table_only (if enabled).
        // Saturated symbols get pushed to the back (or dropped entirely).
        var queue = new List<(SymbolEntry sym, string role, int priority)>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        void EnqueueIfNew(SymbolEntry s, string role, int priority)
        {
            if (seenIds.Contains(s.Id)) return;
            seenIds.Add(s.Id);
            queue.Add((s, role, priority));
        }

        // priority 0: primary
        foreach (var m in primaryRes.Matches)
            if (idx.Symbols.TryGetValue(m.SymbolId, out var s))
                EnqueueIfNew(s, "primary", 0);
        result.PrimaryHitCount = queue.Count(q => q.role == "primary");

        // priority 1: column_only
        foreach (var m in columnOnlyHits)
            if (idx.Symbols.TryGetValue(m.SymbolId, out var s))
                EnqueueIfNew(s, "column_only", 1);

        // ── priority 2: CONSUMERS via struct member flow (FK Layer 2) ──
        //
        // The loader stores the column into a struct field. The FK target evidence
        // typically lives in the CONSUMERS of that field — functions that read
        // `someInfo->member` and pass it to the actual loot/spell/item subsystem.
        //
        // Strategy: for each primary hit, look at the loader and its callees for
        // the pattern `foo->X = fields[N]` to resolve which struct member the
        // column maps to. Then look up every symbol that references that member.
        // Add those as "consumer" role. They're often where the FK target table
        // is mentioned in a SQL literal — which the cross-table annotation will
        // surface as a strong signal.
        //
        // We also try the raw column name as a fallback member name, in case the
        // loader's body wasn't readable or the assignment lives somewhere we
        // didn't reach. In VMaNGOS the C++ member name almost always equals the
        // SQL column name (loot_id → loot_id) — that's the dominant pattern.
        //
        // SPECIAL CASE for VMaNGOS's pervasive `NameN` → `Name[N-1]` convention.
        // SQL columns like ReqItemId1, spell_id1, display_id1, ReqItemCount1 map
        // to C++ array fields ReqItemId[0], spell_id[0], display_id[0], etc.
        // The struct member is the de-numbered base name (ReqItemId, spell_id).
        // We detect the trailing digit and add the de-numbered form as an
        // additional needle. This is what unblocks consumer expansion for the
        // very common multi-value FK columns.
        var resolvedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var psym in queue.Where(q => q.role == "primary").Select(q => q.sym).ToList())
        {
            var resolved = ResolveColumnToStructMember(psym, colLow);
            if (!string.IsNullOrEmpty(resolved))
                resolvedMembers.Add(resolved);
        }
        // Always also try the column name verbatim — the dominant pattern.
        if (colLow.Length >= 3 && !MemberNameStopwords.Contains(colLow))
            resolvedMembers.Add(colLow);

        // NameN → Name array fallback. If the column ends in one or more digits,
        // also add the de-numbered base. This is gated to >=4 chars to avoid
        // single-letter+digit noise.
        var trimmedBase = StripTrailingDigits(colLow);
        if (trimmedBase != null && trimmedBase.Length >= 3
            && trimmedBase != colLow
            && !MemberNameStopwords.Contains(trimmedBase))
        {
            resolvedMembers.Add(trimmedBase);
            // Also try with the trailing index resolved via the loader scan — in
            // case it actually appears with the array suffix as a struct accessor
            // (rare, but cheap to check).
        }

        result.ResolvedStructMembers = resolvedMembers.ToList();

        if (resolvedMembers.Count > 0)
        {
            // Find every symbol referencing any of these members.
            var consumerRes = FindMemberReferences(resolvedMembers, requireAll: false, maxResults: 0);
            foreach (var m in consumerRes.Matches)
            {
                if (idx.Symbols.TryGetValue(m.SymbolId, out var cs))
                    EnqueueIfNew(cs, "consumer", 2);
            }
            result.ConsumerHitCount = consumerRes.Matches.Count;
        }

        // priority 3: neighbors of primary (callees and callers)
        if (req.NeighborDepth > 0)
        {
            var primarySyms = queue.Where(q => q.role == "primary").Select(q => q.sym).ToList();
            foreach (var psym in primarySyms)
            {
                foreach (var calleeId in psym.CallsOut)
                    if (idx.Symbols.TryGetValue(calleeId, out var cs))
                        EnqueueIfNew(cs, "callee", 3);
                foreach (var callerId in psym.CalledBy)
                    if (idx.Symbols.TryGetValue(callerId, out var cs))
                        EnqueueIfNew(cs, "caller", 3);
            }
        }

        // priority 4: table_only (only if explicitly enabled)
        if (req.IncludeTableOnly)
        {
            foreach (var m in tableOnlyHits)
                if (idx.Symbols.TryGetValue(m.SymbolId, out var s))
                    EnqueueIfNew(s, "table_only", 4);
        }

        // Within each priority, sort by line count ascending — small functions first.
        // This means when budget runs out, we drop the biggest functions, keeping breadth.
        queue.Sort((a, b) =>
        {
            int byPri = a.priority.CompareTo(b.priority);
            if (byPri != 0) return byPri;
            int aLines = (a.sym.BodyLineEnd > 0) ? (a.sym.BodyLineEnd - a.sym.BodyLineStart) : 0;
            int bLines = (b.sym.BodyLineEnd > 0) ? (b.sym.BodyLineEnd - b.sym.BodyLineStart) : 0;
            return aLines.CompareTo(bLines);
        });

        // ── Selection pass: respect MaxSymbols, MaxTokensEstimate, saturation ──
        var selectedSymbols = new List<(SymbolEntry sym, string role)>();
        int runningTokenEstimate = 0;

        foreach (var (sym, role, _) in queue)
        {
            // Saturation check — applies regardless of role
            if (IsSaturated(sym.Id) && !req.IncludeSaturated)
            {
                result.Dropped.Add(new DroppedSymbol
                {
                    Id = sym.Id,
                    Role = role,
                    Reason = "saturated",
                    EstimatedTokens = EstimateTokens(sym),
                    CandidateTablesMentioned = CountCandidateMentions(sym.Id)
                });
                continue;
            }

            // Max symbols cap
            if (selectedSymbols.Count >= req.MaxSymbols)
            {
                result.Dropped.Add(new DroppedSymbol
                {
                    Id = sym.Id,
                    Role = role,
                    Reason = "max_symbols",
                    EstimatedTokens = EstimateTokens(sym),
                    CandidateTablesMentioned = CountCandidateMentions(sym.Id)
                });
                continue;
            }

            // Token budget — but never drop primary hits to enforce budget. Primaries
            // are the whole point of the bundle; if they bust the budget, the caller
            // needs to know (and may raise MaxTokensEstimate accordingly).
            int symTokens = EstimateTokens(sym);
            bool budgetExceeded = req.MaxTokensEstimate > 0
                                  && runningTokenEstimate + symTokens > req.MaxTokensEstimate;
            if (budgetExceeded && role != "primary")
            {
                result.Dropped.Add(new DroppedSymbol
                {
                    Id = sym.Id,
                    Role = role,
                    Reason = "budget",
                    EstimatedTokens = symTokens,
                    CandidateTablesMentioned = CountCandidateMentions(sym.Id)
                });
                continue;
            }

            selectedSymbols.Add((sym, role));
            runningTokenEstimate += symTokens;
        }

        result.NeighborCount = selectedSymbols.Count(t => t.role == "caller" || t.role == "callee");

        // ── Cross-table reference annotation (post-selection) ──
        //     For each candidate target table, list which SELECTED symbols mention it.
        //     This is the signal that helps the LLM resolve "what table does this
        //     column point at". We compute it AFTER selection so dropped symbols don't
        //     show up in the cross-references either.
        foreach (var cand in candidateLow)
        {
            if (!_literalTokenToSymbols.TryGetValue(cand, out var symsWithCand)) continue;
            foreach (var (sym, _) in selectedSymbols)
            {
                if (symsWithCand.Contains(sym.Id))
                {
                    if (!result.CrossTableReferences.TryGetValue(cand, out var list))
                    {
                        list = new List<string>();
                        result.CrossTableReferences[cand] = list;
                    }
                    list.Add(sym.Id);
                }
            }
        }

        // ── File-scope cross-reference annotation (FK Layer 2.5) ──
        //     For each candidate target table, find every FILE that contains the
        //     table name as a string literal (anywhere — inside or outside function
        //     bodies). Then, for each of those files, list which of our selected
        //     bundle symbols live in that file. This catches the static-initializer
        //     pattern where the table name lives in a file-scope global and never
        //     appears in any of the consumer functions' bodies.
        //
        //     Note: this is a superset of CrossTableReferences. A symbol might
        //     appear here but not in CrossTableReferences (its file mentions the
        //     table, but the symbol itself doesn't). That's the entire point.
        var selectedByFile = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (sym, _) in selectedSymbols)
        {
            if (string.IsNullOrEmpty(sym.DefinedInFile)) continue;
            if (!selectedByFile.TryGetValue(sym.DefinedInFile, out var list))
            {
                list = new List<string>();
                selectedByFile[sym.DefinedInFile] = list;
            }
            list.Add(sym.Id);
        }
        foreach (var cand in candidateLow)
        {
            if (!_literalTokenToFiles.TryGetValue(cand, out var filesWithCand)) continue;
            foreach (var file in filesWithCand)
            {
                if (!selectedByFile.TryGetValue(file, out var symsInFile)) continue;
                if (!result.CrossTableReferencesByFile.TryGetValue(cand, out var refs))
                {
                    refs = new List<CrossFileReference>();
                    result.CrossTableReferencesByFile[cand] = refs;
                }
                refs.Add(new CrossFileReference
                {
                    FilePath = file,
                    SymbolsInFile = symsInFile
                });
            }
        }

        // ── Materialize bundle symbol entries (with bodies) ──
        var typesSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (sym, role) in selectedSymbols)
        {
            string body = "";
            if (sym.DefinedInFile != null && sym.BodyLineStart > 0 && sym.BodyLineEnd > 0)
                body = ReadBodyFromSource(idx.SourcePath, sym.DefinedInFile, sym.BodyLineStart, sym.BodyLineEnd);

            // Truncate caller/callee lists for prompt compactness; the full graph
            // is reachable via the Trace endpoint if the LLM ever needs it.
            const int neighborListCap = 8;
            var callers = sym.CalledBy.Take(neighborListCap).ToList();
            var callees = sym.CallsOut.Take(neighborListCap).ToList();

            int lineCount = (sym.BodyLineEnd > 0 && sym.BodyLineStart > 0)
                ? (sym.BodyLineEnd - sym.BodyLineStart + 1) : 0;
            bool isHuge = req.HugeFunctionLineThreshold > 0
                          && lineCount >= req.HugeFunctionLineThreshold;

            result.Symbols.Add(new FkBundleSymbol
            {
                Id = sym.Id,
                Name = sym.Name,
                MemberOf = sym.MemberOf,
                Signature = sym.Signature,
                File = sym.DefinedInFile,
                LineStart = sym.BodyLineStart,
                LineEnd = sym.BodyLineEnd,
                Role = role,
                Body = body,
                AlsoReferences = AlsoRefsFor(sym.Id),
                CalledBy = callers,
                CallsOut = callees,
                IsHuge = isHuge,
                CandidateTablesMentioned = CountCandidateMentions(sym.Id)
            });

            if (req.IncludeContainingTypes && sym.MemberOf != null)
                typesSeen.Add(sym.MemberOf);
        }

        // ── 7. Type definitions for the classes that owned the selected symbols ──
        if (req.IncludeContainingTypes)
        {
            foreach (var tn in typesSeen)
            {
                if (idx.Types.TryGetValue(tn, out var t))
                {
                    result.Types.Add(new FkBundleType
                    {
                        Name = t.Name,
                        Kind = t.Kind,
                        DeclaredIn = t.DeclaredIn,
                        Inherits = t.Inherits.ToList(),
                        Members = t.Members.ToList(),
                        Methods = t.Methods.ToList()
                    });
                }
            }
        }

        // ── Stats and pre-formatted prompt text ──
        result.TotalSymbols = result.Symbols.Count;
        result.TotalLines = result.Symbols.Sum(s => string.IsNullOrEmpty(s.Body) ? 0 : s.Body.Split('\n').Length);
        result.EstimatedTokens = result.Symbols.Sum(s => s.Body.Length) / 4;

        int droppedBudget = result.Dropped.Count(d => d.Reason == "budget");
        int droppedSaturated = result.Dropped.Count(d => d.Reason == "saturated");
        int droppedMaxSyms = result.Dropped.Count(d => d.Reason == "max_symbols");

        result.SearchDiagnostics = string.Format(
            "primary={0}, column_only={1}, table_only_found={2}, table_only_included={3}, " +
            "consumers_found={4}, consumers_included={5}, resolved_members=[{6}], " +
            "neighbors_added={7}, total_selected={8}, " +
            "cross_table_symbol={9}, cross_table_file={10}, " +
            "dropped[budget={11} saturated={12} max_syms={13}]",
            result.PrimaryHitCount,
            columnOnlyHits.Count,
            tableOnlyHits.Count,
            result.Symbols.Count(s => s.Role == "table_only"),
            result.ConsumerHitCount,
            result.Symbols.Count(s => s.Role == "consumer"),
            string.Join(",", result.ResolvedStructMembers),
            result.NeighborCount,
            result.TotalSymbols,
            result.CrossTableReferences.Count,
            result.CrossTableReferencesByFile.Count,
            droppedBudget,
            droppedSaturated,
            droppedMaxSyms);

        result.FormattedText = FormatFkBundleText(result);

        return result;
    }

    /// <summary>
    /// Format an FK research bundle as a single UTF-8 text blob suitable for
    /// dropping directly into an LLM prompt. The pipeline does not have to
    /// re-stitch anything.
    /// </summary>
    private string FormatFkBundleText(FkResearchBundleResult b)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== FK RESEARCH BUNDLE ===");
        sb.AppendLine($"Database: {b.Db}");
        sb.AppendLine($"Table:    {b.Table}");
        sb.AppendLine($"Column:   {b.Column}");
        if (b.ResolvedStructMembers.Count > 0)
            sb.AppendLine($"Resolved struct member(s) the column flows into: {string.Join(", ", b.ResolvedStructMembers)}");
        sb.AppendLine($"Generated: {b.GeneratedAt:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"Diagnostics: {b.SearchDiagnostics}");
        sb.AppendLine($"Symbols: {b.TotalSymbols}   Lines: {b.TotalLines}   ~Tokens: {b.EstimatedTokens}");
        sb.AppendLine();

        // Cross-table reference summary
        if (b.CrossTableReferences.Count > 0)
        {
            sb.AppendLine("--- CROSS-TABLE REFERENCES (symbol-level) ---");
            sb.AppendLine("(Candidate target tables that appear in selected symbols' SQL literals.");
            sb.AppendLine(" Strong signal that an FK relationship may exist between this column and one of these tables.)");
            foreach (var (tableName, symIds) in b.CrossTableReferences.OrderByDescending(kv => kv.Value.Count))
            {
                sb.AppendLine($"  {tableName}  ({symIds.Count} symbols): {string.Join(", ", symIds.Take(6))}{(symIds.Count > 6 ? ", ..." : "")}");
            }
            sb.AppendLine();
        }

        // File-scope cross-reference summary — catches static-init patterns
        if (b.CrossTableReferencesByFile.Count > 0)
        {
            sb.AppendLine("--- CROSS-TABLE REFERENCES (file-scope) ---");
            sb.AppendLine("(Candidate target tables that appear as string literals in the SAME FILE as");
            sb.AppendLine(" selected bundle symbols — even if not inside those symbols' bodies. This catches");
            sb.AppendLine(" file-scope static initializers like `LootStore X(\"creature_loot_template\", ...);`");
            sb.AppendLine(" that declare table→object bindings outside any function. STRONG FK signal.)");
            foreach (var (tableName, refs) in b.CrossTableReferencesByFile.OrderByDescending(kv => kv.Value.Sum(r => r.SymbolsInFile.Count)))
            {
                int totalSyms = refs.Sum(r => r.SymbolsInFile.Count);
                sb.AppendLine($"  {tableName}  ({refs.Count} file(s), {totalSyms} symbol(s)):");
                foreach (var r in refs)
                    sb.AppendLine($"    {r.FilePath}: {string.Join(", ", r.SymbolsInFile.Take(5))}{(r.SymbolsInFile.Count > 5 ? ", ..." : "")}");
            }
            sb.AppendLine();
        }

        // Containing types
        if (b.Types.Count > 0)
        {
            sb.AppendLine("--- CONTAINING TYPES ---");
            foreach (var t in b.Types)
            {
                var inh = t.Inherits.Count > 0 ? " : " + string.Join(", ", t.Inherits) : "";
                sb.AppendLine($"[{t.Kind}] {t.Name}{inh}   (declared in {t.DeclaredIn})");
                if (t.Members.Count > 0)
                    sb.AppendLine($"  members: {string.Join(", ", t.Members.Take(20))}{(t.Members.Count > 20 ? ", ..." : "")}");
            }
            sb.AppendLine();
        }

        // Function bodies, grouped by role
        var byRole = b.Symbols
            .GroupBy(s => s.Role)
            .OrderBy(g => RoleOrder(g.Key));

        foreach (var grp in byRole)
        {
            sb.AppendLine($"--- {grp.Key.ToUpperInvariant()} ({grp.Count()}) ---");
            foreach (var s in grp)
            {
                sb.AppendLine();
                var flags = new List<string>();
                if (s.AlsoReferences.Count > 0)
                    flags.Add("also-references: " + string.Join(", ", s.AlsoReferences));
                if (s.IsHuge)
                    flags.Add($"HUGE-FUNCTION ({s.LineEnd - s.LineStart + 1} lines — may be a registration/dispatch table, not a focused loader)");
                if (s.CandidateTablesMentioned > 0)
                    flags.Add($"mentions-{s.CandidateTablesMentioned}-candidate-tables");
                var flagStr = flags.Count > 0 ? "   [" + string.Join("; ", flags) + "]" : "";
                sb.AppendLine($"// {s.Id}   {s.File}:{s.LineStart}-{s.LineEnd}{flagStr}");
                sb.AppendLine(s.Body);
            }
            sb.AppendLine();
        }

        // Dropped-symbol summary at the end — helps the caller understand what was
        // left out and why, in case the bundle feels incomplete.
        if (b.Dropped.Count > 0)
        {
            sb.AppendLine($"--- DROPPED FROM BUNDLE ({b.Dropped.Count}) ---");
            sb.AppendLine("(Symbols considered but excluded from this bundle.)");
            foreach (var grp in b.Dropped.GroupBy(d => d.Reason))
            {
                sb.AppendLine($"  reason={grp.Key} ({grp.Count()}):");
                foreach (var d in grp.Take(10))
                    sb.AppendLine($"    {d.Id}   role={d.Role}   ~tokens={d.EstimatedTokens}   candidate_tables={d.CandidateTablesMentioned}");
                if (grp.Count() > 10)
                    sb.AppendLine($"    ... and {grp.Count() - 10} more");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static int RoleOrder(string role) => role switch
    {
        "primary" => 0,
        "column_only" => 1,
        "consumer" => 2,
        "caller" => 3,
        "callee" => 4,
        "table_only" => 5,
        "saturated" => 6,
        _ => 7
    };

    /// <summary>
    /// Strip a run of trailing digits from a column name and return the de-numbered
    /// base. `loot_id` → `loot_id` (no change, returns same string).
    /// `ReqItemId1` → `ReqItemId`. `spell_id4` → `spell_id`. `display_id12` → `display_id`.
    /// Returns null if the result would be empty.
    /// </summary>
    private static string? StripTrailingDigits(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        int end = s.Length;
        while (end > 0 && char.IsDigit(s[end - 1])) end--;
        if (end == 0) return null;
        // Also strip a single trailing underscore if one was left dangling
        // (e.g. "foo_1" → "foo_" → "foo"). Common VMaNGOS pattern.
        if (end > 0 && s[end - 1] == '_') end--;
        if (end == 0) return null;
        return s.Substring(0, end);
    }
}

// ──────────── Smart Search DTOs ────────────

public class SmartSearchResult
{
    public string RawQuery { get; set; } = "";
    public string? ExpressionType { get; set; }
    public string? Variable { get; set; }
    public string? ExplicitClass { get; set; }
    public string? MethodName { get; set; }
    public List<string>? ResolvedTypes { get; set; }
    public int TotalMatches { get; set; }
    public List<SmartSearchMatch> Matches { get; set; } = new();
}

public class SmartSearchMatch
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? MemberOf { get; set; }
    public string? File { get; set; }
    public int LineStart { get; set; }
    public int LineCount { get; set; }
    public string Signature { get; set; } = "";
    public string? Kind { get; set; }
    public int CallCount { get; set; }
    public int CallerCount { get; set; }
    public bool IsVirtual { get; set; }
    public string Confidence { get; set; } = "possible";
    public string? InheritancePath { get; set; }
}