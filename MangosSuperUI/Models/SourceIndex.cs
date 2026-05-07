namespace MangosSuperUI.Models;

/// <summary>
/// Complete in-memory index of the VMaNGOS C++ source tree.
/// Built by SourceIndexerService, queried by SourceMapController.
/// </summary>
public class SourceIndex
{
    public DateTime IndexedAt { get; set; }
    public string SourcePath { get; set; } = "";
    public int TotalFiles { get; set; }
    public int TotalLines { get; set; }
    public int TotalSymbols { get; set; }
    public int TotalTypes { get; set; }
    public int TotalEnums { get; set; }

    // Primary indices
    public Dictionary<string, FileEntry> Files { get; set; } = new();       // key: relative path
    public Dictionary<string, SymbolEntry> Symbols { get; set; } = new();   // key: qualified name
    public Dictionary<string, TypeEntry> Types { get; set; } = new();       // key: type name
    public Dictionary<string, EnumEntry> Enums { get; set; } = new();       // key: enum name

    // Convenience lookups (built after main parse)
    public Dictionary<string, List<string>> FileToSymbols { get; set; } = new();   // file path → symbol IDs
    public Dictionary<string, List<string>> ClassToSymbols { get; set; } = new();  // class name → symbol IDs
    public Dictionary<string, List<string>> TypeToUsers { get; set; } = new();     // type name → functions that use it
}

// ──────────────────────── Layer 1: File Index ────────────────────────

/// <summary>
/// One entry per .h or .cpp file. Answers: "What does this file contain and depend on?"
/// </summary>
public class FileEntry
{
    public string Path { get; set; } = "";              // "src/game/Objects/Player.cpp" (relative)
    public string FileName { get; set; } = "";          // "Player.cpp"
    public string Extension { get; set; } = "";         // ".cpp" or ".h"
    public string Directory { get; set; } = "";         // "src/game/Objects"
    public int LineCount { get; set; }
    public List<string> Includes { get; set; } = new(); // ["Player.h", "World.h", ...]
    public List<string> DeclaredSymbols { get; set; } = new();  // symbols declared (for .h)
    public List<string> DefinedSymbols { get; set; } = new();   // symbols defined (for .cpp)
    public List<string> DeclaredTypes { get; set; } = new();    // types declared in this file
    public List<string> DeclaredEnums { get; set; } = new();    // enums declared in this file
}

// ──────────────────────── Layer 2: Symbol Index ────────────────────────

/// <summary>
/// One entry per function or method. The core lookup layer.
/// Answers: "Where is this, what does it do, what does it touch?"
/// </summary>
public class SymbolEntry
{
    public string Id { get; set; } = "";                // "Player::Update" (qualified name, used as dict key)
    public string Name { get; set; } = "";              // "Update" (short name)
    public string Kind { get; set; } = "";              // "method", "function", "virtual_method", "static_method"
    public string ReturnType { get; set; } = "";        // "void"
    public string Signature { get; set; } = "";         // "void Player::Update(uint32 diff)"
    public List<string> Params { get; set; } = new();   // ["uint32 diff"]

    // Location
    public string? DeclaredIn { get; set; }             // "src/game/Objects/Player.h:145" (null if not found)
    public string? DefinedIn { get; set; }              // "src/game/Objects/Player.cpp:142"
    public string? DefinedInFile { get; set; }          // "src/game/Objects/Player.cpp" (just the file)
    public int BodyLineStart { get; set; }
    public int BodyLineEnd { get; set; }

    // Relationships
    public string? MemberOf { get; set; }               // "Player" (null for free functions)
    public List<string> CallsOut { get; set; } = new(); // ["Unit::Update", "GetSession", ...]
    public List<string> CalledBy { get; set; } = new(); // reverse index, built in 2nd pass
    public List<string> UsesTypes { get; set; } = new();// ["WorldPacket", "ObjectGuid", ...]

    // Metadata
    public bool IsVirtual { get; set; }
    public bool IsStatic { get; set; }
    public bool IsConst { get; set; }
    public int Complexity { get; set; }                 // count of if/for/while/switch/case/catch in body
    public int LineCount { get; set; }                  // BodyLineEnd - BodyLineStart + 1
}

// ──────────────────────── Layer 3: Type Index ────────────────────────

/// <summary>
/// One entry per class or struct.
/// Answers: "What is this type, what does it inherit, what are its members?"
/// </summary>
public class TypeEntry
{
    public string Name { get; set; } = "";              // "Player"
    public string Kind { get; set; } = "";              // "class" or "struct"
    public string DeclaredIn { get; set; } = "";        // "src/game/Objects/Player.h"
    public int? DeclaredAtLine { get; set; }
    public List<string> Inherits { get; set; } = new();     // ["Unit"]
    public List<string> InheritedBy { get; set; } = new();  // reverse, built in 2nd pass
    public List<string> Members { get; set; } = new();      // ["m_session", "m_inventory", ...]
    public List<string> Methods { get; set; } = new();      // ["Update", "SendPacket", ...] (short names)
    public List<string> QualifiedMethods { get; set; } = new(); // ["Player::Update", "Player::SendPacket"]
    public List<string> EnumsUsed { get; set; } = new();
    public List<string> NestedTypes { get; set; } = new();
}

// ──────────────────────── Layer 4: Enum / Define Index ────────────────────────

/// <summary>
/// One entry per enum or define group.
/// Answers: "What values does this have and who uses it?"
/// </summary>
public class EnumEntry
{
    public string Name { get; set; } = "";              // "PlayerFlags"
    public string Kind { get; set; } = "";              // "enum", "enum_class", "define_group"
    public string DeclaredIn { get; set; } = "";        // "src/game/Objects/Player.h"
    public int LineStart { get; set; }
    public int LineEnd { get; set; }
    public List<EnumValue> Values { get; set; } = new();
    public List<string> UsedByFunctions { get; set; } = new(); // reverse, built in pass
}

public class EnumValue
{
    public string Name { get; set; } = "";
    public string? Value { get; set; }                  // raw string: hex, decimal, expression, or null
}

// ──────────────────────── Trace Export ────────────────────────

/// <summary>
/// The killer feature: a self-contained context bundle for LLM-assisted code tracing.
/// </summary>
public class TraceExport
{
    public string RootSymbol { get; set; } = "";
    public int Depth { get; set; }
    public DateTime GeneratedAt { get; set; }

    public List<TraceFileRef> FilesInScope { get; set; } = new();
    public List<TraceTypeRef> TypesInScope { get; set; } = new();
    public List<TraceEnumRef> EnumsInScope { get; set; } = new();
    public List<TraceFunction> Functions { get; set; } = new();
    public TraceNode? CallTree { get; set; }

    // Pre-formatted text for direct paste
    public string FormattedText { get; set; } = "";

    // Stats
    public int TotalFunctions { get; set; }
    public int TotalLines { get; set; }
    public int EstimatedTokens { get; set; }    // rough: total chars / 4
}

/// <summary>Lightweight file reference for trace scope.</summary>
public class TraceFileRef
{
    public string Path { get; set; } = "";
    public List<string> Includes { get; set; } = new();
}

/// <summary>Lightweight type reference for trace scope.</summary>
public class TraceTypeRef
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DeclaredIn { get; set; } = "";
    public List<string> Inherits { get; set; } = new();
    public List<string> Members { get; set; } = new();
}

/// <summary>Lightweight enum reference for trace scope.</summary>
public class TraceEnumRef
{
    public string Name { get; set; } = "";
    public string DeclaredIn { get; set; } = "";
    public List<EnumValue> Values { get; set; } = new();
}

/// <summary>A function included in the trace with its full source body.</summary>
public class TraceFunction
{
    public string QualifiedName { get; set; } = "";
    public string File { get; set; } = "";
    public int LineStart { get; set; }
    public int LineEnd { get; set; }
    public string Signature { get; set; } = "";
    public string Body { get; set; } = "";     // full source text
}

/// <summary>Recursive call tree node for trace visualization.</summary>
public class TraceNode
{
    public string Symbol { get; set; } = "";
    public int Depth { get; set; }
    public bool IsLeaf { get; set; }
    public bool IsCycle { get; set; }
    public bool IsRef { get; set; }            // already expanded elsewhere in tree
    public List<TraceNode> Children { get; set; } = new();
}

// ──────────────────────── Progress / Status ────────────────────────

public class IndexProgress
{
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public string CurrentFile { get; set; } = "";
    public string Phase { get; set; } = "idle";   // "scanning", "parsing", "bodies", "reverse", "complete", "error"
    public string? Error { get; set; }
    public double PercentComplete => TotalFiles > 0 ? Math.Round((double)FilesProcessed / TotalFiles * 100, 1) : 0;
}

public class IndexResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int Files { get; set; }
    public int Symbols { get; set; }
    public int Types { get; set; }
    public int Enums { get; set; }
    public int Lines { get; set; }
    public double ElapsedMs { get; set; }
}

// ──────────────────────── Topic Explorer ────────────────────────

/// <summary>
/// Intelligence report for a topic query — classifies all matching symbols
/// into entry points, core logic, helpers, with related types/enums/files
/// and internal call flow mapping.
/// </summary>
public class TopicReport
{
    public string Query { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public int TotalSymbols { get; set; }
    public int TotalTypes { get; set; }
    public int TotalEnums { get; set; }
    public int TotalFiles { get; set; }

    public List<TopicSymbol> EntryPoints { get; set; } = new();
    public List<TopicSymbol> CoreLogic { get; set; } = new();
    public List<TopicSymbol> Helpers { get; set; } = new();
    public List<TopicSymbol> Other { get; set; } = new();

    public List<TopicType> Types { get; set; } = new();
    public List<TopicType> RelatedTypes { get; set; } = new();
    public List<TopicEnum> Enums { get; set; } = new();
    public List<TopicFile> Files { get; set; } = new();
    public List<TopicCallFlow> CallFlows { get; set; } = new();

    public string FormattedText { get; set; } = "";
}

public class TopicSymbol
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Signature { get; set; } = "";
    public string File { get; set; } = "";
    public int LineStart { get; set; }
    public int LineEnd { get; set; }
    public int LineCount { get; set; }
    public string? MemberOf { get; set; }
    public string Kind { get; set; } = "";
    public int TotalCallers { get; set; }
    public int TotalCalls { get; set; }
    public int ExternalCallers { get; set; }
    public int InternalCallers { get; set; }
    public int ExternalCalls { get; set; }
    public int InternalCalls { get; set; }
    public int Complexity { get; set; }
    public bool IsVirtual { get; set; }
}

public class TopicType
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DeclaredIn { get; set; } = "";
    public List<string> Inherits { get; set; } = new();
    public List<string> InheritedBy { get; set; } = new();
    public int MethodCount { get; set; }
    public int MemberCount { get; set; }
    public int UsedByCount { get; set; }
    public List<string> TopMethods { get; set; } = new();
}

public class TopicEnum
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DeclaredIn { get; set; } = "";
    public int ValueCount { get; set; }
    public List<string> SampleValues { get; set; } = new();
}

public class TopicFile
{
    public string Path { get; set; } = "";
    public int TotalFunctions { get; set; }
    public List<string> MatchedFunctions { get; set; } = new();
}

public class TopicCallFlow
{
    public string From { get; set; } = "";
    public List<string> To { get; set; } = new();
}