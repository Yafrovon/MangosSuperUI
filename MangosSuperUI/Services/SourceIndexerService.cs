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