namespace MangosSuperUI.Services;

// ── The creator's kit-level composition (MSUIClient session "composition" block) ─────────
//
// Per stage: the caster animation and the nine attachment slots (0 Head, 1 Chest, 2 Base,
// 3 LeftHand, 4 RightHand, 5 Breath, 6..8 Special) each naming an effect M2 path and a
// scale; plus the missile. A stage present here is authoritative: its slot list REPLACES
// the source kit's effects (absent slots are cleared). A model from another spell is just
// a path - the patch builder reads it from the client archives and writes it at the cloned
// custom path like any effect file. See shared_docs/SPELL_CREATOR_IDE.md §2.8 (MSUIClient).

public sealed class SpellComposition
{
    public Dictionary<string, StageComposition> Stages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public MissileComposition? Missile { get; set; }
    /// <summary>The lasting AREA visual (SpellVisual 11/12/13): MSUIClient SPELL_CREATOR_PLAN.md P2.5.</summary>
    public AreaComposition? Area { get; set; }
    /// <summary>Raw SpellVisual fields a design states outright - only the small ones nothing else
    /// models: 8 (missile path type), 9 (missile arrival attachment), 14, 15 (flags). MSUIClient P5.1.</summary>
    public Dictionary<int, uint> VisualFields { get; set; } = new();
    public static readonly int[] RawVisualFields = { 8, 9, 14, 15 };

    /// <summary>Parse the session's block: {"cast": {animationId, slots:[...]}, ..., "missile": {...}}.</summary>
    public static SpellComposition? Parse(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is not System.Text.Json.Nodes.JsonObject obj) return null;
        var result = new SpellComposition();
        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, "visualFields", StringComparison.OrdinalIgnoreCase) && value is System.Text.Json.Nodes.JsonObject raw)
            {
                foreach (var (field, fieldValue) in raw)
                    if (int.TryParse(field, out int f) && Array.IndexOf(RawVisualFields, f) >= 0)
                        result.VisualFields[f] = ReadRaw(fieldValue);
                continue;
            }
            if (value is not System.Text.Json.Nodes.JsonObject stage) continue;
            if (string.Equals(key, "area", StringComparison.OrdinalIgnoreCase))
            {
                result.Area = new AreaComposition
                {
                    ModelPath = stage["modelPath"]?.GetValue<string>() ?? "",
                    Scale = ReadFloat(stage["scale"], 1f),
                    ShardSelector = (int)ReadFloat(stage["shardSelector"], -1f),
                    ShardRate = ReadFloat(stage["shardRate"], 0f),
                    Sound = stage["sound"] is { } areaSound ? (int?)ReadFloat(areaSound, 0f) : null,
                };
                continue;
            }
            if (string.Equals(key, "missile", StringComparison.OrdinalIgnoreCase))
            {
                result.Missile = new MissileComposition
                {
                    ModelPath = stage["modelPath"]?.GetValue<string>() ?? "",
                    Scale = ReadFloat(stage["scale"], 1f),
                    Sound = stage["sound"] is { } missileSound ? (int?)ReadFloat(missileSound, 0f) : null,
                    Attachment = stage["attachment"] is { } attach ? (int?)ReadFloat(attach, 0f) : null,
                };
                continue;
            }
            var composed = new StageComposition
            {
                AnimationId = stage["animationId"] is { } anim ? (int?)ReadFloat(anim, 0f) : null,
                CameraShake = stage["cameraShake"] is { } shake ? (int?)ReadFloat(shake, 0f) : null,
                Sound = stage["sound"] is { } sound ? (int?)ReadFloat(sound, 0f) : null,
                WorldEffect = stage["worldEffect"]?.GetValue<string>(),
                KitType = stage["kitType"] is { } kitType ? (int?)ReadFloat(kitType, 0f) : null,
            };
            if (stage["charProcs"] is System.Text.Json.Nodes.JsonArray procs)
            {
                composed.CharProcs = new List<CharProcComposition>();
                foreach (var procNode in procs)
                    if (procNode is System.Text.Json.Nodes.JsonObject proc)
                        composed.CharProcs.Add(new CharProcComposition
                        {
                            Type = (int)ReadFloat(proc["type"], -1f),
                            Parameters = (proc["params"] as System.Text.Json.Nodes.JsonArray)?.Select(p => ReadFloat(p, 0f)).Take(4).ToArray() ?? new float[4],
                        });
            }
            if (stage["slots"] is System.Text.Json.Nodes.JsonArray slots)
                foreach (var slotNode in slots)
                {
                    if (slotNode is not System.Text.Json.Nodes.JsonObject slot) continue;
                    composed.Slots.Add(new SlotComposition
                    {
                        Slot = (int)ReadFloat(slot["slot"], 0f),
                        ModelPath = slot["modelPath"]?.GetValue<string>() ?? "",
                        Scale = ReadFloat(slot["scale"], 1f),
                    });
                }
            result.Stages[key] = composed;
        }
        return result.Stages.Count > 0 || result.Missile is not null || result.Area is not null || result.VisualFields.Count > 0 ? result : null;
    }

    /// <summary>A raw DBC uint: read as an INTEGER (a float loses 0xFFFFFFFF, the "none" many fields
    /// hold); -1 means 0xFFFFFFFF. MSUIClient P5.1 round trip.</summary>
    private static uint ReadRaw(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return 0;
        try { return unchecked((uint)node.GetValue<long>()); } catch (Exception) { }
        try { return (uint)node.GetValue<double>(); } catch (Exception) { return 0; }
    }

    private static float ReadFloat(System.Text.Json.Nodes.JsonNode? node, float fallback)
    {
        if (node is null) return fallback;
        try { return node.GetValue<float>(); } catch (Exception) { }
        try { return node.GetValue<int>(); } catch (Exception) { return fallback; }
    }
}

public sealed class StageComposition
{
    /// <summary>AnimationData id the caster plays; null = none (kit field 2 = 0).</summary>
    public int? AnimationId { get; set; }
    /// <summary>SpellEffectCameraShakes id (kit field 14): 0 = none; null = keep the source kit's.
    /// MSUIClient SPELL_CREATOR_PLAN.md P4.4.</summary>
    public int? CameraShake { get; set; }
    /// <summary>A SoundEntries id for the kit (field 13); null = keep the kit's. MSUIClient P3.9.</summary>
    public int? Sound { get; set; }
    /// <summary>The kit's world effect (field 12) model path; "" = none; null = keep. MSUIClient P4.3.</summary>
    public string? WorldEffect { get; set; }
    /// <summary>The kit's CharProc lanes (fields 15-34): null = keep the source's; a list (even empty)
    /// replaces all four lanes. MSUIClient P5.1 / P4.4.</summary>
    public List<CharProcComposition>? CharProcs { get; set; }
    /// <summary>The kit type (field 1); null = the phase's rule (KitTypeFor). MSUIClient P5.1.</summary>
    public int? KitType { get; set; }
    public List<SlotComposition> Slots { get; set; } = new();
}

public sealed class CharProcComposition
{
    /// <summary>The proc type (0 chain beam, 1 tint, 8 weapon trail, 9 area shards, 11 animation rate,
    /// 14 alpha, ...); -1 = an empty lane.</summary>
    public int Type { get; set; } = -1;
    public float[] Parameters { get; set; } = new float[4];
}

public sealed class SlotComposition
{
    public int Slot { get; set; }
    public string ModelPath { get; set; } = "";
    public float Scale { get; set; } = 1f;
}

public sealed class AreaComposition
{
    /// <summary>The looping centre model while the DynamicObject lives; empty = none.</summary>
    public string ModelPath { get; set; } = "";
    public float Scale { get; set; } = 1f;
    /// <summary>The client's type-9 shard model (0..6, its own literal table); -1 = no shards.</summary>
    public int ShardSelector { get; set; } = -1;
    /// <summary>Shards per second over the area's radius.</summary>
    public float ShardRate { get; set; }
    /// <summary>A SoundEntries id for the area kit (field 13); null = keep. MSUIClient P3.9.</summary>
    public int? Sound { get; set; }
}

public sealed class MissileComposition
{
    /// <summary>Empty = the spell has no missile.</summary>
    public string ModelPath { get; set; } = "";
    public float Scale { get; set; } = 1f;
    /// <summary>A SoundEntries id for the flight loop (SpellVisual field 10); null = keep. MSUIClient P3.9.</summary>
    public int? Sound { get; set; }
    /// <summary>Where on the TARGET the missile arrives (SpellVisual field 9, an ordinal into the client's
    /// missile attachment table); null = keep. MSUIClient P5.1.</summary>
    public int? Attachment { get; set; }
}

/// <summary>
/// Clones an entire SpellVisual DBC chain with new IDs.
/// 
/// The visual chain for a spell is:
///   Spell.dbc[spellVisual field] → SpellVisual.dbc row
///     → SpellVisualKit.dbc rows (precastKit, castKit, impactKit, stateKit, channelKit)
///       → SpellVisualEffectName.dbc rows (headEffect, chestEffect, baseEffect, leftHandEffect, etc.)
///         → M2 file paths (the actual particle/effect models)
///
/// FIELD MAP SOURCE OF TRUTH
///   These indices are the client's, taken from MSUIClient's
///   Formats/SpellVisualCatalog.cs — byte-verified against build 5875 and
///   cross-checked by tools/spellvis/spellvis.py, which is the documented oracle
///   for this layout. A loader that actually RENDERS these rows is the only field
///   map worth trusting; the numbers below were re-confirmed against our own
///   dbc/patch MPQs before being written down.
///
/// SpellVisual.dbc layout (16 fields, 64 bytes per record, all u32):
///   [0]  ID
///   [1]  PrecastKit          → SpellVisualKit ID
///   [2]  CastKit             → SpellVisualKit ID
///   [3]  ImpactKit           → SpellVisualKit ID
///   [4]  StateKit            → SpellVisualKit ID (0 for bolt spells)
///   [5]  ChannelKit          → SpellVisualKit ID (set on 127 of 2165 rows)
///   [6]  HasMissile          → a GATE, not a foreign key. Only ever 0 or 1 on
///                               the shipped table (228 rows carry 1). The client
///                               never reads it — the real missile gate is
///                               Spell.dbc Speed > 0. DO NOT clone this as a kit:
///                               1 is also the id of the dummy SpellVisualKit row,
///                               so treating it as a kit clones that sentinel (and
///                               the dead zzOLD__FireShield_Cast_Base effect it
///                               points at) and overwrites the gate with a kit id.
///   [7]  MissileEffect       → SpellVisualEffectName ID for the projectile model
///                               Fireball=365 "Fireball Missile Low", ShadowBolt=151
///   [8]  (unmapped — 0 on every row we have looked at)
///   [9]  MissileAttachOrdinal  ORDINAL into the client's MissileAttachTable,
///                               NOT an attachment id (Fireball=1)
///   [10] MissileSound        → SoundEntries ID, the in-flight loop (Fireball=3011)
///   [11] AreaGate
///   [12] AreaEffect          → SpellVisualEffectName ID (DynamicObject centre model)
///   [13] AreaKit             → SpellVisualKit ID (its type-9 CharProcs rate the
///                               area shards). Cloned per spell with the stage kits
///                               (VisualKitFields), so editing it never touches the
///                               other spells sharing the source's kit.
///   [14] StrikeSound         → SoundEntries ID
///   [15] (unmapped)
///
/// SpellVisualKit.dbc layout (35 fields, 140 bytes per record):
///   [0]  ID
///   [1]  StartAnimID
///   [2]  AnimID              → AnimationData.dbc (53 = directed cast for Fire and Shadow)
///   [3]  HeadEffect          → SpellVisualEffectName ID (0 AND 0xFFFFFFFF = none)
///   [4]  ChestEffect         → SpellVisualEffectName ID
///   [5]  BaseEffect          → SpellVisualEffectName ID
///   [6]  LeftHandEffect      → SpellVisualEffectName ID
///   [7]  RightHandEffect     → SpellVisualEffectName ID
///   [8]  BreathEffect        → SpellVisualEffectName ID
///   [9]  Special1Effect      → SpellVisualEffectName ID
///   [10] Special2Effect      → SpellVisualEffectName ID
///   [11] Special3Effect      → SpellVisualEffectName ID — the NINTH slot. The
///                               client reads nine slots at [3..11]; it is empty on
///                               all 1772 shipped kits, but it is an EFFECT slot,
///                               so nothing else may be written there.
///   [12] (unmapped)
///   [13] SoundID             → SoundEntries ID (Fireball cast=1484, impact=1507)
///   [15-18] CharProc types, [19-34] their parameters (four lanes, transposed)
///
/// THE NONE-SENTINEL
///   "No value" is written as EITHER 0 OR 0xFFFFFFFF, inconsistently, on the same
///   table (of 15948 kit effect slots: 8 zeros, 14087 0xFFFFFFFF). Every foreign
///   key read here folds BOTH, matching SpellVisualCatalog.Fk().
///
/// SpellVisualEffectName.dbc layout — CORRECTED Session 8:
///   [0]  ID
///   [1]  Name        (stringref → display/debug label, e.g. "Fire Cast Hand")
///   [2]  FilePath    (stringref → ACTUAL M2 path, e.g. "Spells\Fire_Cast_Hand.mdx")
///   [3]  AreaEffectSize (uint32, usually 0 or 4)
///   [4]  Scale       (float, 0.0 or 1.0)
///
///   ⚠️ Field [2] is a STRINGREF (FilePath), NOT a float! Session 8 root cause.
///   The client uses field [2] to locate the M2 file. Field [1] is display only.
///   Vanilla DBC uses .mdx extension in FilePath; actual MPQ files are .m2.
///   We write .m2 in the DBC since that matches MPQ contents. If the client
///   can't find it, try switching to .mdx (the client may map internally).
/// </summary>
public class SpellVisualCloner
{
    /// <summary>Result of cloning a visual chain.</summary>
    public class CloneResult
    {
        public uint NewVisualId { get; set; }
        public Dictionary<uint, uint> KitIdMap { get; set; } = new();         // old kit ID → new kit ID
        public Dictionary<uint, uint> EffectNameIdMap { get; set; } = new();  // old effectName ID → new effectName ID
        public List<EffectFileMapping> EffectFiles { get; set; } = new();     // new effect IDs with their M2 paths
        public uint MissileEffectId { get; set; }                              // new missile effect ID (if any)
    }

    /// <summary>Maps an effect name ID to its M2 file path (original and custom).</summary>
    public class EffectFileMapping
    {
        public uint NewEffectId { get; set; }
        public string OriginalName { get; set; } = "";    // DBC effect name (e.g. "Fire Cast Hand")
        public string OriginalM2Path { get; set; } = "";  // Derived M2 path (e.g. "Spells\\Fire_Cast_Hand.m2")
        public string CustomName { get; set; } = "";      // New DBC effect name (e.g. "Voidstrike Cast Hand")
        public string CustomM2Path { get; set; } = "";    // New M2 path (e.g. "Spells\\Voidstrike_Cast_Hand.m2")
        public string EffectRole { get; set; } = "";      // "<stage>_<slot>": "cast_leftHand", "channel_base",
                                                          // "impact_chest", or the bare "missile".
                                                          // Stage is one of precast/cast/impact/state/channel;
                                                          // callers split on '_' to key per-phase params.
    }

    /// <summary>
    /// The NINE kit field indices that point to SpellVisualEffectName IDs.
    /// The client reads [3..11]; slot names follow its KitAttachmentIds order
    /// (Head, Chest, Base, LeftHand, RightHand, Breath, Special1..3).
    /// Either 0 or 0xFFFFFFFF means "none" — see <see cref="IsNone"/>.
    /// </summary>
    private static readonly int[] KitEffectFields = { 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly string[] KitEffectNames = {
        "head", "chest", "base", "leftHand", "rightHand", "breath",
        "special1", "special2", "special3"
    };

    /// <summary>
    /// SpellVisual field indices that point to SpellVisualKit IDs — the FIVE
    /// stage kits, [1..5]. Field [6] is the never-read missile gate and is NOT a
    /// kit reference; there is no "stateDone" stage on this table.
    /// </summary>
    private static readonly int[] VisualKitFields = { 1, 2, 3, 4, 5, 13 };
    private static readonly string[] VisualKitNames = {
        "precast", "cast", "impact", "state", "channel", "area"
    };

    /// <summary>SpellVisualKit field 2: the caster's AnimationData id (SpellVisualCatalog).</summary>
    private const int KIT_FIELD_ANIMATION = 2;
    /// <summary>SpellVisualKit field 1: the kit type, by phase (KitTypeFor).</summary>
    private const int KIT_FIELD_TYPE = 1;
    private static uint KitTypeFor(string stage) => stage switch
    {
        "impact" => 1u,
        "state" or "channel" => 2u,
        _ => 0u,
    };
    /// <summary>SpellVisualKit field 14: SpellEffectCameraShakes id (0 = none).</summary>
    private const int KIT_FIELD_CAMERA_SHAKE = 14;
    /// <summary>SpellVisualKit field 13: SoundEntries id.</summary>
    private const int KIT_FIELD_SOUND = 13;
    /// <summary>SpellVisualKit field 12: the world effect (SpellVisualEffectName id).</summary>
    private const int KIT_FIELD_WORLD = 12;
    /// <summary>SpellVisualKit CharProc lanes: type of lane i at 15+i; parameter p of lane i at
    /// 19 + 4p + i (four transposed arrays, MSUIClient SpellVisualCatalog). An EMPTY lane is
    /// -1 - type 0 is CHAIN_CHANNEL, so a zero row declares four chain beams.</summary>
    private const int KIT_FIELD_PROC_TYPE = 15;
    private const int KIT_FIELD_PROC_PARAMS = 19;

    /// <summary>A kit the cloner MADE (from zero, or from another stage's row) carries none of the
    /// source's extras: no start animation, no world effect, no sound, no camera shake, and four
    /// EMPTY CharProc lanes (-1) - never another stage's sound or chain beam.</summary>
    private static void BlankKitExtras(DbcWriterService kits, uint kitId, uint[] row)
    {
        foreach (int field in new[] { 1, 12, 13, 14 })
            if (field < row.Length) { kits.PatchRow(kitId, field, 0); row[field] = 0; }
        for (int lane = 0; lane < 4 && KIT_FIELD_PROC_TYPE + lane < row.Length; lane++)
        {
            kits.PatchRow(kitId, KIT_FIELD_PROC_TYPE + lane, 0xFFFFFFFF);
            row[KIT_FIELD_PROC_TYPE + lane] = 0xFFFFFFFF;
        }
        for (int f = KIT_FIELD_PROC_PARAMS; f < KIT_FIELD_PROC_PARAMS + 16 && f < row.Length; f++)
        {
            kits.PatchRow(kitId, f, 0);
            row[f] = 0;
        }
    }

    /// <summary>SpellVisualEffectName field 4: effect scale (5-field 1.12 table: id, name, path, areaEffectSize, scale).</summary>
    private const int EFFECT_FIELD_SCALE = 4;

    /// <summary>
    /// Fold BOTH none-sentinels. The shipped tables write "no value" as either 0
    /// or 0xFFFFFFFF, inconsistently, on the same column. Mirrors
    /// SpellVisualCatalog.Fk() in the client.
    /// </summary>
    private static bool IsNone(uint id) => id == 0 || id == 0xFFFFFFFF;

    /// <summary>
    /// Derive the M2 file path from a SpellVisualEffectName display name.
    /// Convention: spaces → underscores, prepend "Spells\\", append ".m2"
    /// This path is used BOTH for the MPQ file path AND the DBC FilePath field [2].
    /// </summary>
    public static string EffectNameToM2Path(string effectName)
    {
        return $"Spells\\{effectName.Replace(' ', '_')}.m2";
    }

    /// <summary>
    /// Normalize a DBC FilePath to the actual MPQ file extension.
    /// Vanilla DBC uses .mdx/.mdl extensions but actual MPQ files are .m2.
    /// e.g. "Spells\Fire_Cast_Hand.mdx" → "Spells\Fire_Cast_Hand.m2"
    ///      "Particles\FireShield_Cast_Base.mdl" → "Particles\FireShield_Cast_Base.m2"
    /// </summary>
    public static string NormalizeM2Extension(string dbcFilePath)
    {
        if (string.IsNullOrEmpty(dbcFilePath))
            return dbcFilePath;

        // Replace .mdx or .mdl with .m2 for MPQ lookup
        if (dbcFilePath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
            dbcFilePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            return dbcFilePath.Substring(0, dbcFilePath.Length - 4) + ".m2";
        }
        return dbcFilePath;
    }

    /// <summary>
    /// Build a custom effect name from a spell name and a role descriptor.
    /// e.g. ("Voidstrike", "cast_leftHand") → "Voidstrike Cast LeftHand"
    /// The M2 path is then derived: "Spells\\Voidstrike_Cast_LeftHand.m2"
    /// </summary>
    public static string BuildCustomEffectName(string spellName, string role)
    {
        // Convert role like "cast_leftHand" to "Cast LeftHand"
        string rolePart = string.Join(" ", role.Split('_')
            .Select(p => p.Length > 0 ? char.ToUpper(p[0]) + p.Substring(1) : p));
        return $"{spellName} {rolePart}";
    }

    /// <summary>
    /// Clone an entire SpellVisual chain, assigning new IDs and creating new
    /// SpellVisualEffectName entries with custom names and FilePaths.
    /// </summary>
    public static CloneResult Clone(
        DbcWriterService spellVisualDbc,
        DbcWriterService spellVisualKitDbc,
        DbcWriterService spellVisualEffectNameDbc,
        uint sourceVisualId,
        uint newVisualId,
        uint baseKitId,
        uint baseEffectId,
        string spellName,
        SpellComposition? composition = null)
    {
        var result = new CloneResult { NewVisualId = newVisualId };
        uint nextKitId = baseKitId;
        uint nextEffectId = baseEffectId;

        // ── Step 1: Clone the SpellVisual row ──
        // A spell built from nothing (MSUIClient "New spell", or a source with no visual)
        // has no row to clone: start from an all-zero visual and let the composition fill
        // every stage (SPELL_CREATOR_PLAN.md P0.4). Every kit and effect below then takes
        // the existing "never authored by the source" branches.
        uint[] visualRow;
        if (IsNone(sourceVisualId) || spellVisualDbc.GetRow(sourceVisualId) is null)
        {
            visualRow = new uint[Math.Max(16, spellVisualDbc.FieldCount)];
            visualRow[0] = newVisualId;
            spellVisualDbc.AddRow(visualRow);
        }
        else
        {
            visualRow = spellVisualDbc.CloneRow(sourceVisualId, newVisualId);
        }

        // A template effect row and kit row for slots/stages the source never authored but
        // the composition fills: the first real one on this visual, else a zero row.
        uint templateEffectId = 0;
        uint templateKitId = 0;
        foreach (int fieldIdx in VisualKitFields)
        {
            uint kitId = fieldIdx < visualRow.Length ? visualRow[fieldIdx] : 0;
            if (IsNone(kitId)) continue;
            if (templateKitId == 0) templateKitId = kitId;
            uint[]? kit = spellVisualKitDbc.GetRow(kitId);
            if (kit is null) continue;
            foreach (int effectFieldIdx in KitEffectFields)
                if (templateEffectId == 0 && effectFieldIdx < kit.Length && !IsNone(kit[effectFieldIdx]))
                    templateEffectId = kit[effectFieldIdx];
        }
        if (templateEffectId == 0 && !IsNone(visualRow[7])) templateEffectId = visualRow[7];

        // One effect row per (source effect, composed path): the source's own effects keep
        // the historical one-clone-per-id sharing; a composed model gets its own row.
        uint EnsureEffect(uint oldEffectId, string? composedPath, float scale, string role)
        {
            bool composed = composedPath is { Length: > 0 };
            if (!composed && result.EffectNameIdMap.TryGetValue(oldEffectId, out uint existing))
                return existing;

            uint newEffectId = nextEffectId++;
            uint[] effectRow;
            string originalName;
            string originalFilePath;
            if (!IsNone(oldEffectId))
            {
                effectRow = spellVisualEffectNameDbc.CloneRow(oldEffectId, newEffectId);
                originalName = spellVisualEffectNameDbc.ReadString(effectRow[1]);
                originalFilePath = spellVisualEffectNameDbc.ReadString(effectRow[2]);
            }
            else if (templateEffectId != 0)
            {
                effectRow = spellVisualEffectNameDbc.CloneRow(templateEffectId, newEffectId);
                originalName = "Composed";
                originalFilePath = "";
            }
            else
            {
                effectRow = new uint[Math.Max(5, spellVisualEffectNameDbc.FieldCount)];
                effectRow[0] = newEffectId;
                effectRow[EFFECT_FIELD_SCALE] = DbcWriterService.FloatToUint(1f);
                spellVisualEffectNameDbc.AddRow(effectRow);
                originalName = "Composed";
                originalFilePath = "";
            }
            if (!composed) result.EffectNameIdMap[oldEffectId] = newEffectId;

            // Read the ACTUAL original file path from field [2] (not derived from name!) - or,
            // for a composed slot, the path the creator chose (any effect M2 in the archives).
            string originalM2Path = NormalizeM2Extension(composed ? composedPath! : originalFilePath);

            string customName = BuildCustomEffectName(spellName, role);
            string customM2Path = EffectNameToM2Path(customName);

            // Field [1] display name; field [2] the FilePath the client actually loads from.
            spellVisualEffectNameDbc.PatchRow(newEffectId, 1, spellVisualEffectNameDbc.AddString(customName));
            spellVisualEffectNameDbc.PatchRow(newEffectId, 2, spellVisualEffectNameDbc.AddString(customM2Path));
            if (composed || Math.Abs(scale - 1f) > 1e-4f)
                spellVisualEffectNameDbc.PatchRowFloat(newEffectId, EFFECT_FIELD_SCALE, scale);

            result.EffectFiles.Add(new EffectFileMapping
            {
                NewEffectId = newEffectId,
                OriginalName = originalName,
                OriginalM2Path = originalM2Path,
                CustomName = customName,
                CustomM2Path = customM2Path,
                EffectRole = role
            });
            return newEffectId;
        }

        // ── Step 2: For each kit reference in the visual, clone the kit ──
        // A composed stage is authoritative: its slot list replaces the kit's effects, its
        // animation replaces field 2, and a stage the source never had is created.
        for (int i = 0; i < VisualKitFields.Length; i++)
        {
            int fieldIdx = VisualKitFields[i];
            uint oldKitId = fieldIdx < visualRow.Length ? visualRow[fieldIdx] : 0;
            StageComposition? stage = null;
            composition?.Stages.TryGetValue(VisualKitNames[i], out stage);
            if (IsNone(oldKitId) && stage is null) continue;

            uint newKitId = nextKitId++;
            uint[] kitRow;
            if (!IsNone(oldKitId))
            {
                result.KitIdMap[oldKitId] = newKitId;
                kitRow = spellVisualKitDbc.CloneRow(oldKitId, newKitId);
            }
            else if (templateKitId != 0)
            {
                kitRow = spellVisualKitDbc.CloneRow(templateKitId, newKitId);
                foreach (int effectFieldIdx in KitEffectFields)
                {
                    spellVisualKitDbc.PatchRow(newKitId, effectFieldIdx, 0);
                    kitRow[effectFieldIdx] = 0;
                }
                spellVisualKitDbc.PatchRow(newKitId, KIT_FIELD_ANIMATION, 0);
                BlankKitExtras(spellVisualKitDbc, newKitId, kitRow);
            }
            else
            {
                kitRow = new uint[spellVisualKitDbc.FieldCount];
                kitRow[0] = newKitId;
                spellVisualKitDbc.AddRow(kitRow);
                BlankKitExtras(spellVisualKitDbc, newKitId, kitRow);
            }
            spellVisualDbc.PatchRow(newVisualId, fieldIdx, newKitId);
            // A kit made here (no source kit) gets its phase's KIT TYPE (field 1): measured over every
            // stock kit (MSUIClient P5.1 round trip) - precast/cast 0, impact 1, state/channel 2.
            if (stage?.KitType is int statedType && statedType >= 0)
                spellVisualKitDbc.PatchRow(newKitId, KIT_FIELD_TYPE, (uint)statedType);
            else if (IsNone(oldKitId))
                spellVisualKitDbc.PatchRow(newKitId, KIT_FIELD_TYPE, KitTypeFor(VisualKitNames[i]));

            if (stage is not null)
                spellVisualKitDbc.PatchRow(newKitId, KIT_FIELD_ANIMATION, (uint)Math.Max(0, stage.AnimationId ?? 0));
            if (stage?.CharProcs is { } charProcs)
                for (int lane = 0; lane < 4; lane++)
                {
                    CharProcComposition? proc = lane < charProcs.Count ? charProcs[lane] : null;
                    int type = proc?.Type ?? -1;
                    spellVisualKitDbc.PatchRow(newKitId, KIT_FIELD_PROC_TYPE + lane, type < 0 ? 0xFFFFFFFF : (uint)type);
                    for (int p = 0; p < 4; p++)
                        spellVisualKitDbc.PatchRowFloat(newKitId, KIT_FIELD_PROC_PARAMS + p * 4 + lane,
                            type < 0 || proc is null || p >= proc.Parameters.Length ? 0f : proc.Parameters[p]);
                }
            if (stage?.CameraShake is int shake)
                spellVisualKitDbc.PatchRow(newKitId, KIT_FIELD_CAMERA_SHAKE, (uint)Math.Max(0, shake));
            if (stage?.Sound is int sound && sound > 0)
                spellVisualKitDbc.PatchRow(newKitId, KIT_FIELD_SOUND, (uint)sound);
            if (stage?.WorldEffect is { } worldPath)
            {
                uint oldWorld = kitRow.Length > KIT_FIELD_WORLD ? kitRow[KIT_FIELD_WORLD] : 0;
                spellVisualKitDbc.PatchRow(newKitId, KIT_FIELD_WORLD, worldPath.Length == 0 ? 0u
                    : EnsureEffect(IsNone(oldWorld) ? 0 : oldWorld, worldPath, 1f, "world"));
            }

            // ── Step 3: For each effect slot in the kit, clone / compose the effect ──
            for (int j = 0; j < KitEffectFields.Length; j++)
            {
                int effectFieldIdx = KitEffectFields[j];
                uint oldEffectId = effectFieldIdx < kitRow.Length ? kitRow[effectFieldIdx] : 0;
                string? composedPath = null;
                float scale = 1f;
                if (stage is not null)
                {
                    SlotComposition? slot = stage.Slots.FirstOrDefault(sl => sl.Slot == j);
                    if (slot is null || slot.ModelPath.Length == 0)
                    {
                        spellVisualKitDbc.PatchRow(newKitId, effectFieldIdx, 0);   // cleared by the design
                        continue;
                    }
                    composedPath = slot.ModelPath;
                    scale = slot.Scale;
                }
                else if (IsNone(oldEffectId)) continue;

                string role = $"{VisualKitNames[i]}_{KitEffectNames[j]}";
                uint newEffectId = EnsureEffect(oldEffectId, composedPath, scale, role);
                spellVisualKitDbc.PatchRow(newKitId, effectFieldIdx, newEffectId);
            }
        }

        // ── Step 3b: the AREA (fields 11 gate, 12 centre model, 13 kit with type-9 shards) ──
        if (composition?.Area is { } area)
        {
            if (area.ModelPath.Length > 0)
            {
                uint areaEffect = EnsureEffect(IsNone(visualRow[12]) ? 0 : visualRow[12], area.ModelPath, area.Scale, "area_centre");
                spellVisualDbc.PatchRow(newVisualId, 11, 1);
                spellVisualDbc.PatchRow(newVisualId, 12, areaEffect);
            }
            else
            {
                spellVisualDbc.PatchRow(newVisualId, 11, 0);
                spellVisualDbc.PatchRow(newVisualId, 12, 0);
            }
            uint areaKit = spellVisualDbc.GetRow(newVisualId)![13];
            if (IsNone(areaKit) && (area.ShardSelector >= 0 || area.Sound is > 0))
            {
                areaKit = nextKitId++;
                var row = new uint[spellVisualKitDbc.FieldCount];
                row[0] = areaKit;
                spellVisualKitDbc.AddRow(row);
                BlankKitExtras(spellVisualKitDbc, areaKit, row);
                spellVisualDbc.PatchRow(newVisualId, 13, areaKit);
            }
            if (!IsNone(areaKit))
            {
                // Lane 0 carries the shards; any other type-9 lane is cleared so there is one rate.
                for (int lane = 0; lane < 4; lane++)
                    if ((int)spellVisualKitDbc.GetRow(areaKit)![KIT_FIELD_PROC_TYPE + lane] == 9)
                        spellVisualKitDbc.PatchRow(areaKit, KIT_FIELD_PROC_TYPE + lane, 0xFFFFFFFF);
                if (area.ShardSelector >= 0)
                {
                    spellVisualKitDbc.PatchRow(areaKit, KIT_FIELD_PROC_TYPE, 9);
                    spellVisualKitDbc.PatchRowFloat(areaKit, KIT_FIELD_PROC_PARAMS + 0 * 4, area.ShardSelector);
                    spellVisualKitDbc.PatchRowFloat(areaKit, KIT_FIELD_PROC_PARAMS + 1 * 4, MathF.Max(area.ShardRate, 0.1f));
                    spellVisualKitDbc.PatchRowFloat(areaKit, KIT_FIELD_PROC_PARAMS + 2 * 4, 0f);
                    spellVisualKitDbc.PatchRowFloat(areaKit, KIT_FIELD_PROC_PARAMS + 3 * 4, 0f);
                }
                if (area.Sound is int areaSound && areaSound > 0)
                    spellVisualKitDbc.PatchRow(areaKit, KIT_FIELD_SOUND, (uint)areaSound);
            }
        }

        // ── Step 4: Handle missile effect ──
        // Field 7 is the missile's SpellVisualEffectName ID (Fireball=365).
        // Field 6 is only the gate and field 8 is unmapped/zero — neither is cloned.
        uint oldMissileEffectId = visualRow[7];
        MissileComposition? missile = composition?.Missile;
        if (missile is not null && missile.ModelPath.Length == 0)
        {
            spellVisualDbc.PatchRow(newVisualId, 7, 0);   // the design removed the projectile
        }
        else if (!IsNone(oldMissileEffectId) || missile is not null)
        {
            uint newMissileEffectId = EnsureEffect(oldMissileEffectId, missile?.ModelPath, missile?.Scale ?? 1f, "missile");
            result.MissileEffectId = newMissileEffectId;
            spellVisualDbc.PatchRow(newVisualId, 7, newMissileEffectId);
            // The gate every shipped missile row carries (228 of 228). A visual built from zero
            // has it at 0; set it so the row looks like every real missile visual does.
            spellVisualDbc.PatchRow(newVisualId, 6, 1);
        }
        if (missile?.Sound is int flightSound && flightSound > 0)
            spellVisualDbc.PatchRow(newVisualId, 10, (uint)flightSound);   // the in-flight loop
        if (missile?.Attachment is int attachment && attachment >= 0)
            spellVisualDbc.PatchRow(newVisualId, 9, (uint)attachment);     // where it arrives on the target
        if (composition is not null)
            foreach (var (field, raw) in composition.VisualFields)
                if (field < spellVisualDbc.FieldCount) spellVisualDbc.PatchRow(newVisualId, field, raw);

        return result;
    }
}