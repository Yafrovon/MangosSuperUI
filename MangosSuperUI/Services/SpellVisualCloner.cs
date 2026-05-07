namespace MangosSuperUI.Services;

/// <summary>
/// Clones an entire SpellVisual DBC chain with new IDs.
/// 
/// The visual chain for a spell is:
///   Spell.dbc[spellVisual field] → SpellVisual.dbc row
///     → SpellVisualKit.dbc rows (precastKit, castKit, impactKit, stateKit, channelKit)
///       → SpellVisualEffectName.dbc rows (headEffect, chestEffect, baseEffect, leftHandEffect, etc.)
///         → M2 file paths (the actual particle/effect models)
///
/// SpellVisual.dbc layout (16 fields, 64 bytes per record):
///   EMPIRICALLY VERIFIED (April 2026, rows 67=Fireball, 64=ShadowBolt):
///   [0]  ID
///   [1]  PrecastKit          → SpellVisualKit ID
///   [2]  CastKit             → SpellVisualKit ID
///   [3]  ImpactKit           → SpellVisualKit ID
///   [4]  StateKit            → SpellVisualKit ID (0 for bolt spells)
///   [5]  StateDoneKit        → SpellVisualKit ID (0 for bolt spells)
///   [6]  ChannelKit          → SpellVisualKit ID
///   [7]  HasMissile          → SpellVisualEffectName ID (NOT a boolean!)
///                               Fireball=365 "Fireball Missile Low", ShadowBolt=151
///   [8]  MissileModel        (always 0 for these spells — missile comes from HasMissile)
///   [9]  MissilePathType     (1 = standard arc)
///   [10] MissileDestX        (Fireball=3011, ShadowBolt=3015)
///   [11] MissileDestY        (0)
///   [12] MissileDestZ        (0)
///   [13] MissileSound        (0)
///   [14] AnimEventSoundID    (0)
///   [15] Flags               (0)
///
/// SpellVisualKit.dbc layout (35 fields, 140 bytes per record):
///   [0]  ID
///   [1]  StartAnimID
///   [2]  AnimID              (53 = directed cast for both Fire and Shadow)
///   [3]  HeadEffect          → SpellVisualEffectName ID (0xFFFFFFFF = none)
///   [4]  ChestEffect         → SpellVisualEffectName ID
///   [5]  BaseEffect          → SpellVisualEffectName ID
///   [6]  LeftHandEffect      → SpellVisualEffectName ID
///   [7]  RightHandEffect     → SpellVisualEffectName ID
///   [8]  BreathEffect        → SpellVisualEffectName ID
///   [9]  LeftWeaponEffect    → SpellVisualEffectName ID
///   [10] RightWeaponEffect   → SpellVisualEffectName ID
///   [11] SoundID             → SoundEntries ID (0xFFFFFFFF = none)
///   [12] ShakeID
///   [13] CharacterProcedure  SoundEntries ID (Fireball cast=1484, impact=1507)
///   [14-34] Additional fields
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
        public string EffectRole { get; set; } = "";      // "cast_leftHand", "missile", "impact_chest", etc.
    }

    // Kit field indices that point to SpellVisualEffectName IDs
    // 0xFFFFFFFF means "none" (not 0)
    private static readonly int[] KitEffectFields = { 3, 4, 5, 6, 7, 8, 9, 10 };
    private static readonly string[] KitEffectNames = {
        "head", "chest", "base", "leftHand", "rightHand", "breath", "leftWeapon", "rightWeapon"
    };

    // SpellVisual field indices that point to SpellVisualKit IDs
    private static readonly int[] VisualKitFields = { 1, 2, 3, 4, 5, 6 };
    private static readonly string[] VisualKitNames = {
        "precast", "cast", "impact", "state", "stateDone", "channel"
    };

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
        string spellName)
    {
        var result = new CloneResult { NewVisualId = newVisualId };
        uint nextKitId = baseKitId;
        uint nextEffectId = baseEffectId;

        // ── Step 1: Clone the SpellVisual row ──
        var visualRow = spellVisualDbc.CloneRow(sourceVisualId, newVisualId);

        // ── Step 2: For each kit reference in the visual, clone the kit ──
        for (int i = 0; i < VisualKitFields.Length; i++)
        {
            int fieldIdx = VisualKitFields[i];
            uint oldKitId = visualRow[fieldIdx];
            if (oldKitId == 0) continue;

            uint newKitId = nextKitId++;
            result.KitIdMap[oldKitId] = newKitId;

            var kitRow = spellVisualKitDbc.CloneRow(oldKitId, newKitId);
            spellVisualDbc.PatchRow(newVisualId, fieldIdx, newKitId);

            // ── Step 3: For each effect reference in the kit, clone the effect ──
            for (int j = 0; j < KitEffectFields.Length; j++)
            {
                int effectFieldIdx = KitEffectFields[j];
                uint oldEffectId = kitRow[effectFieldIdx];
                if (oldEffectId == 0 || oldEffectId == 0xFFFFFFFF) continue;

                if (!result.EffectNameIdMap.TryGetValue(oldEffectId, out uint newEffectId))
                {
                    newEffectId = nextEffectId++;
                    result.EffectNameIdMap[oldEffectId] = newEffectId;

                    var effectRow = spellVisualEffectNameDbc.CloneRow(oldEffectId, newEffectId);
                    string originalName = spellVisualEffectNameDbc.ReadString(effectRow[1]);

                    // Read the ACTUAL original file path from field [2] (not derived from name!)
                    // e.g. "Particles\FireShield_Cast_Base.mdl" or "Spells\Fire_Cast_Hand.mdx"
                    string originalFilePath = spellVisualEffectNameDbc.ReadString(effectRow[2]);
                    // For MPQ lookup, normalize extension to .m2 (client files are .m2)
                    string originalM2Path = NormalizeM2Extension(originalFilePath);

                    // Build custom name using the naming convention
                    string role = $"{VisualKitNames[i]}_{KitEffectNames[j]}";
                    string customName = BuildCustomEffectName(spellName, role);
                    string customM2Path = EffectNameToM2Path(customName);

                    // Update field [1] — display name
                    uint newNameOffset = spellVisualEffectNameDbc.AddString(customName);
                    spellVisualEffectNameDbc.PatchRow(newEffectId, 1, newNameOffset);

                    // ═══ SESSION 9 FIX: Patch field [2] — FilePath (the ACTUAL M2 path) ═══
                    // Session 8 root cause: field [2] is a stringref to the M2 file path.
                    // The client loads M2s from this field, NOT from field [1].
                    // Without this patch, custom M2s in the MPQ are never loaded.
                    uint newPathOffset = spellVisualEffectNameDbc.AddString(customM2Path);
                    spellVisualEffectNameDbc.PatchRow(newEffectId, 2, newPathOffset);

                    result.EffectFiles.Add(new EffectFileMapping
                    {
                        NewEffectId = newEffectId,
                        OriginalName = originalName,
                        OriginalM2Path = originalM2Path,
                        CustomName = customName,
                        CustomM2Path = customM2Path,
                        EffectRole = role
                    });
                }

                spellVisualKitDbc.PatchRow(newKitId, effectFieldIdx, newEffectId);
            }
        }

        // ── Step 4: Handle missile effect ──
        // CRITICAL FIX: Missile is field 7 (HasMissile), NOT field 8 (MissileModel).
        // Field 7 contains the SpellVisualEffectName ID for the missile M2.
        // Field 8 is always 0 for these spells.
        uint oldMissileEffectId = visualRow[7]; // HasMissile = EffectName ID
        if (oldMissileEffectId != 0)
        {
            if (!result.EffectNameIdMap.TryGetValue(oldMissileEffectId, out uint newMissileEffectId))
            {
                newMissileEffectId = nextEffectId++;
                result.EffectNameIdMap[oldMissileEffectId] = newMissileEffectId;

                var missileRow = spellVisualEffectNameDbc.CloneRow(oldMissileEffectId, newMissileEffectId);
                string originalName = spellVisualEffectNameDbc.ReadString(missileRow[1]);
                string originalFilePath = spellVisualEffectNameDbc.ReadString(missileRow[2]);
                string originalM2Path = NormalizeM2Extension(originalFilePath);

                string customName = BuildCustomEffectName(spellName, "missile");
                string customM2Path = EffectNameToM2Path(customName);

                // Update field [1] — display name
                uint newNameOffset = spellVisualEffectNameDbc.AddString(customName);
                spellVisualEffectNameDbc.PatchRow(newMissileEffectId, 1, newNameOffset);

                // ═══ SESSION 9 FIX: Patch field [2] — FilePath ═══
                uint newPathOffset = spellVisualEffectNameDbc.AddString(customM2Path);
                spellVisualEffectNameDbc.PatchRow(newMissileEffectId, 2, newPathOffset);

                result.EffectFiles.Add(new EffectFileMapping
                {
                    NewEffectId = newMissileEffectId,
                    OriginalName = originalName,
                    OriginalM2Path = originalM2Path,
                    CustomName = customName,
                    CustomM2Path = customM2Path,
                    EffectRole = "missile"
                });
            }

            result.MissileEffectId = newMissileEffectId;
            spellVisualDbc.PatchRow(newVisualId, 7, newMissileEffectId); // Field 7, not 8
        }

        return result;
    }
}