using MangosSuperUI.Models;
using Dapper;

namespace MangosSuperUI.Services;

/// <summary>
/// Creates, clones, and manages custom spells in spell_template.
/// 
/// CRITICAL: Custom spells use entry IDs 60000–65000 (NOT 900000+).
/// Vanilla 1.12.1 SMSG_INITIAL_SPELLS packet uses uint16 for spell IDs.
/// IDs above 65535 get truncated on the wire → wrong spell in client.
/// Safe range: 60000–65000 (above Blizzard's ~29000 max, within uint16).
///
/// spell_template changes require a server restart.
/// Client needs Spell.dbc + SkillLineAbility.dbc patching — PatchBuilderService.
/// Character needs spell in character_spell table — TeachSpellToCharacterAsync.
/// </summary>
public class SpellCreatorService
{
    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly ILogger<SpellCreatorService> _logger;

    public const int CUSTOM_SPELL_BASE = 40000;
    public const int CUSTOM_SPELL_MAX = 49999;
    public const int TRAINER_WRAPPER_BASE = 50000;
    public const int TRAINER_WRAPPER_MAX = 65000;

    public SpellCreatorService(ConnectionFactory db, AuditService audit, ILogger<SpellCreatorService> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Expose a raw mangos DB connection for PatchController queries.</summary>
    public System.Data.IDbConnection CreateMangosConnection() => _db.Mangos();

    public async Task<int> GetNextCustomEntryAsync()
    {
        using var conn = _db.Mangos();
        var maxEntry = await conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(entry) FROM spell_template WHERE entry >= @Base AND entry <= @Max",
            new { Base = CUSTOM_SPELL_BASE, Max = CUSTOM_SPELL_MAX });

        int next = (maxEntry ?? CUSTOM_SPELL_BASE - 1) + 1;
        if (next > CUSTOM_SPELL_MAX)
            throw new InvalidOperationException(
                $"Custom spell ID range exhausted ({CUSTOM_SPELL_BASE}–{CUSTOM_SPELL_MAX}). " +
                "Vanilla protocol requires spell IDs below 65535.");
        return next;
    }

    public async Task<int> GetNextTrainerWrapperEntryAsync()
    {
        using var conn = _db.Mangos();
        var maxEntry = await conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(entry) FROM spell_template WHERE entry >= @Base AND entry <= @Max",
            new { Base = TRAINER_WRAPPER_BASE, Max = TRAINER_WRAPPER_MAX });

        int next = (maxEntry ?? TRAINER_WRAPPER_BASE - 1) + 1;
        if (next > TRAINER_WRAPPER_MAX)
            throw new InvalidOperationException(
                $"Trainer wrapper ID range exhausted ({TRAINER_WRAPPER_BASE}–{TRAINER_WRAPPER_MAX}).");
        return next;
    }

    /// <summary>Create a SPELL_EFFECT_LEARN_SPELL (36) wrapper that teaches the given spell.
    /// VMaNGOS trainers only process wrapper spells, not direct spell IDs.</summary>
    public async Task<int> CreateTrainerWrapperAsync(int realSpellEntry, string spellName, string rankSubtext, int reqLevel, int spellIconId = 185)
    {
        int wrapperId = await GetNextTrainerWrapperEntryAsync();
        using var conn = _db.Mangos();
        await conn.ExecuteAsync(
            @"INSERT INTO spell_template (entry, build, school, attributes, targets, procChance,
              equippedItemClass, equippedItemSubClassMask, effect1, effectTriggerSpell1,
              spellVisual1, spellIconId, castingTimeIndex, name, nameFlags, nameSubtext, nameSubtextFlags,
              description, descriptionFlags, auraDescription, auraDescriptionFlags,
              rangeIndex, dmgMultiplier1, dmgMultiplier2, dmgMultiplier3,
              effectBonusCoefficient1, effectBonusCoefficient2, effectBonusCoefficient3, stanceBarOrder, spellLevel, baseLevel)
              VALUES (@Entry, 5875, 0, 262400, 256, 101,
              -1, -1, 36, @RealSpell,
              107, @IconId, 1, @Name, 983070, @Subtext, 983070,
              '', 983052, '', 983052,
              6, 1, 1, 1,
              0, -1, -1, -1, 0, 0)",
            new { Entry = wrapperId, RealSpell = realSpellEntry, Name = spellName, Subtext = rankSubtext, IconId = spellIconId });

        _logger.LogInformation("SpellCreator: Created trainer wrapper #{Wrapper} → teaches #{Real} ({Name} {Rank})",
            wrapperId, realSpellEntry, spellName, rankSubtext);

        return wrapperId;
    }

    /// <summary>Remove trainer wrapper spells that teach a given custom spell.</summary>
    public async Task<int> RemoveTrainerWrappersAsync(int realSpellEntry)
    {
        using var conn = _db.Mangos();
        // Find wrappers that teach this spell
        var wrapperIds = (await conn.QueryAsync<int>(
            @"SELECT entry FROM spell_template 
              WHERE entry >= @Base AND entry <= @Max 
              AND effect1 = 36 AND effectTriggerSpell1 = @Spell",
            new { Base = TRAINER_WRAPPER_BASE, Max = TRAINER_WRAPPER_MAX, Spell = realSpellEntry })).ToList();

        int removed = 0;
        foreach (var wrapperId in wrapperIds)
        {
            // Remove wrapper from trainer tables
            await conn.ExecuteAsync("DELETE FROM npc_trainer WHERE spell = @Spell", new { Spell = wrapperId });
            await conn.ExecuteAsync("DELETE FROM npc_trainer_template WHERE spell = @Spell", new { Spell = wrapperId });
            // Delete the wrapper spell itself
            await conn.ExecuteAsync("DELETE FROM spell_template WHERE entry = @Entry", new { Entry = wrapperId });
            removed++;
        }

        if (removed > 0)
            _logger.LogInformation("SpellCreator: Removed {Count} trainer wrapper(s) for spell #{Spell}", removed, realSpellEntry);

        return removed;
    }

    /// <summary>Clone an existing spell to a new custom entry.</summary>
    public async Task<int> CloneSpellAsync(int sourceEntry, Dictionary<string, object?> overrides, string? operatorIp = null)
    {
        using var conn = _db.Mangos();

        var source = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM spell_template WHERE entry = @Entry ORDER BY build DESC LIMIT 1",
            new { Entry = sourceEntry });

        if (source == null)
        {
            _logger.LogWarning("SpellCreator: Source spell {Entry} not found", sourceEntry);
            return -1;
        }

        var sourceDict = (IDictionary<string, object>)source;
        int newEntry = await GetNextCustomEntryAsync();

        var columns = (await conn.QueryAsync<string>(
            @"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
              WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'spell_template'
              ORDER BY ORDINAL_POSITION")).ToList();

        var values = new DynamicParameters();
        var colNames = new List<string>();
        var paramNames = new List<string>();

        foreach (var col in columns)
        {
            colNames.Add($"`{col}`");
            string paramName = $"p_{col}";
            paramNames.Add($"@{paramName}");

            if (col == "entry")
                values.Add(paramName, newEntry);
            else if (overrides != null && overrides.TryGetValue(col, out var overrideVal))
                values.Add(paramName, overrideVal);
            else if (sourceDict.ContainsKey(col))
                values.Add(paramName, sourceDict[col]);
            else
                values.Add(paramName, null);
        }

        var sql = $"INSERT IGNORE INTO spell_template ({string.Join(", ", colNames)}) VALUES ({string.Join(", ", paramNames)})";
        var affected = await conn.ExecuteAsync(sql, values);

        if (affected > 0)
        {
            string spellName = overrides?.ContainsKey("name") == true
                ? overrides["name"]?.ToString() ?? $"Custom #{newEntry}"
                : sourceDict.ContainsKey("name") ? sourceDict["name"]?.ToString() ?? "" : "";

            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = operatorIp,
                Category = "content",
                Action = "spell_clone",
                TargetType = "spell_template",
                TargetName = $"{spellName} (#{newEntry}) — cloned from #{sourceEntry}",
                StateAfter = System.Text.Json.JsonSerializer.Serialize(overrides ?? new()),
                IsReversible = true,
                Success = true,
                Notes = $"Cloned spell #{sourceEntry} → #{newEntry}. Server restart required."
            });

            _logger.LogInformation("SpellCreator: Cloned spell {Source} → {New} ({Name})",
                sourceEntry, newEntry, spellName);
        }

        return affected > 0 ? newEntry : -1;
    }

    /// <summary>Delete a custom spell. Removes from spell_template AND all character_spell rows.</summary>
    public async Task<bool> DeleteCustomSpellAsync(int entry, string? operatorIp = null)
    {
        if (entry < CUSTOM_SPELL_BASE || entry > CUSTOM_SPELL_MAX)
        {
            _logger.LogWarning("SpellCreator: Refusing to delete non-custom spell {Entry}", entry);
            return false;
        }

        using var mangosConn = _db.Mangos();
        var spell = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT entry, name FROM spell_template WHERE entry = @Entry LIMIT 1",
            new { Entry = entry });

        if (spell == null) return false;
        string spellName = spell.name?.ToString() ?? $"#{entry}";

        var affected = await mangosConn.ExecuteAsync(
            "DELETE FROM spell_template WHERE entry = @Entry", new { Entry = entry });

        // Clean up skill_line_ability and spell_chain
        int slaRemoved = 0, chainRemoved = 0, trainerRemoved = 0;
        try
        {
            slaRemoved = await RemoveSkillLineAbilityAsync(entry);
            chainRemoved = await RemoveSpellChainAsync(entry);
            trainerRemoved = await RemoveFromTrainersAsync(entry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SpellCreator: Could not clean skill_line_ability/spell_chain/npc_trainer for {Entry}", entry);
        }

        int charSpellsRemoved = 0;
        try
        {
            using var charConn = _db.Characters();
            charSpellsRemoved = await charConn.ExecuteAsync(
                "DELETE FROM character_spell WHERE spell = @Spell", new { Spell = entry });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SpellCreator: Could not clean character_spell for {Entry}", entry);
        }

        if (affected > 0)
        {
            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = operatorIp,
                Category = "content",
                Action = "spell_delete",
                TargetType = "spell_template",
                TargetName = $"{spellName} (#{entry})",
                IsReversible = false,
                Success = true,
                Notes = $"Deleted spell #{entry}. Removed from {charSpellsRemoved} character(s), {slaRemoved} skill_line_ability, {chainRemoved} spell_chain, {trainerRemoved} npc_trainer. Server restart required."
            });
            _logger.LogInformation("SpellCreator: Deleted #{Entry} ({Name}), {Chars} character(s) cleaned",
                entry, spellName, charSpellsRemoved);
        }

        return affected > 0;
    }

    /// <summary>Teach a spell to a character (INSERT into character_spell). Relog required.</summary>
    public async Task<bool> TeachSpellToCharacterAsync(int spellEntry, int characterGuid, string? operatorIp = null)
    {
        try
        {
            using var charConn = _db.Characters();

            var existing = await charConn.ExecuteScalarAsync<int?>(
                "SELECT spell FROM character_spell WHERE guid = @Guid AND spell = @Spell",
                new { Guid = characterGuid, Spell = spellEntry });

            if (existing.HasValue) return true;

            var charName = await charConn.ExecuteScalarAsync<string?>(
                "SELECT name FROM characters WHERE guid = @Guid", new { Guid = characterGuid });

            var affected = await charConn.ExecuteAsync(
                "INSERT IGNORE INTO character_spell (guid, spell, active, disabled) VALUES (@Guid, @Spell, 1, 0)",
                new { Guid = characterGuid, Spell = spellEntry });

            if (affected > 0)
            {
                using var mangosConn = _db.Mangos();
                var spellName = await mangosConn.ExecuteScalarAsync<string?>(
                    "SELECT name FROM spell_template WHERE entry = @Entry LIMIT 1",
                    new { Entry = spellEntry });

                await _audit.LogAsync(new AuditEntry
                {
                    Operator = "admin",
                    OperatorIp = operatorIp,
                    Category = "content",
                    Action = "spell_teach",
                    TargetType = "character_spell",
                    TargetName = $"{charName ?? "?"} learned {spellName ?? "?"} (#{spellEntry})",
                    IsReversible = true,
                    Success = true,
                    Notes = $"Taught spell #{spellEntry} to {charName} (guid {characterGuid}). Relog required."
                });
            }
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpellCreator: Failed to teach spell {Spell} to guid {Guid}", spellEntry, characterGuid);
            return false;
        }
    }

    /// <summary>Remove a spell from a character.</summary>
    public async Task<bool> UnlearnSpellFromCharacterAsync(int spellEntry, int characterGuid, string? operatorIp = null)
    {
        try
        {
            using var charConn = _db.Characters();
            var charName = await charConn.ExecuteScalarAsync<string?>(
                "SELECT name FROM characters WHERE guid = @Guid", new { Guid = characterGuid });

            var affected = await charConn.ExecuteAsync(
                "DELETE FROM character_spell WHERE guid = @Guid AND spell = @Spell",
                new { Guid = characterGuid, Spell = spellEntry });

            if (affected > 0)
            {
                await _audit.LogAsync(new AuditEntry
                {
                    Operator = "admin",
                    OperatorIp = operatorIp,
                    Category = "content",
                    Action = "spell_unlearn",
                    TargetType = "character_spell",
                    TargetName = $"{charName ?? "?"} unlearned spell #{spellEntry}",
                    IsReversible = true,
                    Success = true,
                    Notes = $"Removed spell #{spellEntry} from {charName} (guid {characterGuid}). Relog required."
                });
            }
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpellCreator: Failed to unlearn spell {Spell} from guid {Guid}", spellEntry, characterGuid);
            return false;
        }
    }

    // ===================== SKILL LINE ABILITY (Spellbook Tab Placement) =====================

    /// <summary>
    /// Class → Skill Tab mapping. skill_id determines which spellbook tab shows the spell.
    /// CRITICAL: school (damage type) ≠ skill_id (tab). Warlock Affliction/Destruction both use
    /// school=5 (Shadow) but different skill_ids (355/593).
    /// </summary>
    private static readonly Dictionary<string, (int skillId, int classMask, int spellFamilyName)> SkillTabMap = new()
    {
        // Warrior
        ["warrior_arms"] = (26, 1, 4),
        // Paladin
        ["paladin_holy"] = (594, 2, 10),
        // Hunter
        ["hunter_survival"] = (51, 4, 9),
        // Rogue
        ["rogue_combat"] = (38, 8, 8),
        // Priest
        ["priest_holy"] = (56, 16, 6),
        ["priest_shadow"] = (78, 16, 6),
        // Shaman
        ["shaman_restoration"] = (374, 64, 11),
        ["shaman_elemental"] = (375, 64, 11),
        // Mage
        ["mage_frost"] = (6, 128, 3),
        ["mage_fire"] = (8, 128, 3),
        ["mage_arcane"] = (237, 128, 3),
        // Warlock
        ["warlock_affliction"] = (355, 256, 5),
        ["warlock_destruction"] = (593, 256, 5),
        // Druid
        ["druid_restoration"] = (573, 1024, 7),
        ["druid_balance"] = (574, 1024, 7),
    };

    /// <summary>Get the full skill tab mapping for the frontend.</summary>
    public static Dictionary<string, (int skillId, int classMask, int spellFamilyName)> GetSkillTabMap() => SkillTabMap;

    /// <summary>Insert a skill_line_ability row so the spell appears in the correct spellbook tab.</summary>
    /// <param name="learnOnGetSkill">2 = auto-learned at character creation (R1 starting spells), 0 = trainer-learned (R2+).</param>
    public async Task<bool> InsertSkillLineAbilityAsync(int spellEntry, string skillTabKey, int supersededBySpell = 0, int learnOnGetSkill = 0)
    {
        if (!SkillTabMap.TryGetValue(skillTabKey, out var tab))
        {
            _logger.LogWarning("SpellCreator: Unknown skill tab key '{Key}' for spell {Entry}", skillTabKey, spellEntry);
            return false;
        }

        using var conn = _db.Mangos();

        // Get next available ID
        var maxId = await conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(id) FROM skill_line_ability");
        int nextId = (maxId ?? 0) + 1;

        var affected = await conn.ExecuteAsync(
            @"INSERT IGNORE INTO skill_line_ability
              (id, build, skill_id, spell_id, race_mask, class_mask, req_skill_value,
               superseded_by_spell, learn_on_get_skill, max_value, min_value, req_train_points)
              VALUES (@Id, 5875, @SkillId, @SpellId, 0, @ClassMask, 1,
                      @SupersededBy, @LearnOnGetSkill, 0, 0, 0)",
            new
            {
                Id = nextId,
                SkillId = tab.skillId,
                SpellId = spellEntry,
                ClassMask = tab.classMask,
                SupersededBy = supersededBySpell,
                LearnOnGetSkill = learnOnGetSkill
            });

        if (affected > 0)
            _logger.LogInformation("SpellCreator: Inserted skill_line_ability for spell {Entry} → skill_id {SkillId} ({Key})",
                spellEntry, tab.skillId, skillTabKey);

        return affected > 0;
    }

    /// <summary>Remove skill_line_ability row(s) for a custom spell.</summary>
    public async Task<int> RemoveSkillLineAbilityAsync(int spellEntry)
    {
        using var conn = _db.Mangos();
        return await conn.ExecuteAsync(
            "DELETE FROM skill_line_ability WHERE spell_id = @SpellId AND build = 5875",
            new { SpellId = spellEntry });
    }

    // ===================== SPELL CHAIN (Rank System) =====================

    /// <summary>Insert a spell_chain row to link this spell into a rank chain.</summary>
    public async Task<bool> InsertSpellChainAsync(int spellEntry, int prevSpell, int firstSpell, int rank, int reqSpell = 0)
    {
        using var conn = _db.Mangos();
        var affected = await conn.ExecuteAsync(
            @"INSERT IGNORE INTO spell_chain (spell_id, prev_spell, first_spell, rank, req_spell, build_min, build_max)
              VALUES (@SpellId, @PrevSpell, @FirstSpell, @Rank, @ReqSpell, 0, 5875)",
            new { SpellId = spellEntry, PrevSpell = prevSpell, FirstSpell = firstSpell, Rank = rank, ReqSpell = reqSpell });

        if (affected > 0)
            _logger.LogInformation("SpellCreator: Inserted spell_chain for {Entry}: rank {Rank}, first_spell {First}, prev {Prev}",
                spellEntry, rank, firstSpell, prevSpell);

        return affected > 0;
    }

    /// <summary>Remove spell_chain row(s) for a custom spell.</summary>
    public async Task<int> RemoveSpellChainAsync(int spellEntry)
    {
        using var conn = _db.Mangos();
        return await conn.ExecuteAsync(
            "DELETE FROM spell_chain WHERE spell_id = @SpellId", new { SpellId = spellEntry });
    }

    /// <summary>Update the superseded_by_spell field when a new rank is added.</summary>
    public async Task UpdateSupersededByAsync(int spellEntry, int newNextRankEntry)
    {
        using var conn = _db.Mangos();
        await conn.ExecuteAsync(
            "UPDATE skill_line_ability SET superseded_by_spell = @Next WHERE spell_id = @SpellId AND build = 5875",
            new { Next = newNextRankEntry, SpellId = spellEntry });
    }

    // ===================== RANK CHAIN QUERIES (Session 33) =====================

    /// <summary>
    /// Get all rank entries in a spell chain where the given entry is first_spell.
    /// Returns empty list if no chain exists. Includes rank 1 itself.
    /// </summary>
    public async Task<List<(int rank, int spellId)>> GetRankChainEntriesAsync(int firstSpellEntry)
    {
        using var conn = _db.Mangos();
        var rows = await conn.QueryAsync<(int rank, int spell_id)>(
            @"SELECT rank, spell_id FROM spell_chain
              WHERE first_spell = @First ORDER BY rank",
            new { First = firstSpellEntry });
        return rows.Select(r => (r.rank, r.spell_id)).ToList();
    }

    /// <summary>
    /// Get full rank chain data for DBC patching. Returns all ranks 2+ for a given rank 1 entry
    /// with their source spell entries (what they were cloned from) and skill_line_ability data.
    /// Used by RebuildUnifiedPatchFromConfigsAsync to populate AdditionalRanks on SpellPatchRequest.
    /// </summary>
    public async Task<List<RankChainPatchInfo>> GetRankChainForPatchingAsync(int rank1Entry)
    {
        using var conn = _db.Mangos();

        // Get the rank chain
        var chainRows = await conn.QueryAsync<dynamic>(
            @"SELECT sc.rank, sc.spell_id, st.name, st.nameSubtext, st.school, st.description,
                     st.effectBasePoints1, st.effectDieSides1, st.effectBasePoints2,
                     st.manaCost, st.spellLevel, st.baseLevel, st.castingTimeIndex,
                     st.effectRealPointsPerLevel1
              FROM spell_chain sc
              JOIN spell_template st ON st.entry = sc.spell_id
                AND st.build = (SELECT MAX(build) FROM spell_template WHERE entry = sc.spell_id)
              WHERE sc.first_spell = @First AND sc.rank > 1
              ORDER BY sc.rank",
            new { First = rank1Entry });

        if (!chainRows.Any())
            return new List<RankChainPatchInfo>();

        // Get the source first_spell for the rank 1 entry (from custom_spell_meta or spell_template)
        // We need to figure out the original source spell so we can map each custom rank
        // to its corresponding vanilla source rank for DBC cloning.
        // The source spell entry is stored in custom_spell_meta.source_entry.
        var sourceEntry = await conn.ExecuteScalarAsync<int?>(
            "SELECT source_entry FROM custom_spell_meta WHERE entry = @E",
            new { E = rank1Entry });

        if (!sourceEntry.HasValue || sourceEntry.Value <= 0)
            return new List<RankChainPatchInfo>();

        // Get the source spell's rank chain to find each rank's vanilla entry
        var sourceFirst = await conn.ExecuteScalarAsync<int?>(
            "SELECT first_spell FROM spell_chain WHERE spell_id = @E",
            new { E = sourceEntry.Value });
        if (!sourceFirst.HasValue) sourceFirst = sourceEntry.Value;

        var sourceRanks = await conn.QueryAsync<(int rank, int spell_id)>(
            @"SELECT rank, spell_id FROM spell_chain
              WHERE first_spell = @First ORDER BY rank",
            new { First = sourceFirst.Value });
        var sourceRankMap = sourceRanks.ToDictionary(r => r.rank, r => r.spell_id);

        // Get skill_line_ability data for the rank 1 entry to replicate for all ranks
        var slaData = await conn.QueryFirstOrDefaultAsync<dynamic>(
            @"SELECT skill_id, class_mask FROM skill_line_ability
              WHERE spell_id = @SpellId AND build = 5875 LIMIT 1",
            new { SpellId = rank1Entry });

        // Build the superseded_by chain from our custom entries
        var customRankList = (await conn.QueryAsync<(int rank, int spell_id)>(
            @"SELECT rank, spell_id FROM spell_chain
              WHERE first_spell = @First ORDER BY rank",
            new { First = rank1Entry })).ToList();
        var supersededByMap = new Dictionary<int, int>(); // entry → next rank entry
        for (int i = 0; i < customRankList.Count - 1; i++)
            supersededByMap[customRankList[i].spell_id] = customRankList[i + 1].spell_id;

        var results = new List<RankChainPatchInfo>();
        foreach (var row in chainRows)
        {
            int rank = (int)row.rank;
            int spellId = (int)row.spell_id;

            // Find the corresponding vanilla source rank entry
            int sourceRankEntry = sourceRankMap.TryGetValue(rank, out int srcId) ? srcId : 0;
            if (sourceRankEntry <= 0)
            {
                _logger.LogWarning("SpellCreator: No source rank {Rank} found for chain first_spell={First}", rank, rank1Entry);
                continue;
            }

            var info = new RankChainPatchInfo
            {
                Entry = spellId,
                SourceRankEntry = sourceRankEntry,
                Rank = rank,
                SpellName = row.name?.ToString() ?? "",
                Description = row.description?.ToString(),
                School = (int)(row.school ?? 0),
                SupersededBySpell = supersededByMap.TryGetValue(spellId, out int nextEntry) ? nextEntry : 0,
                EffectBasePoints1 = Convert.ToInt32(row.effectBasePoints1 ?? 0),
                EffectDieSides1 = Convert.ToInt32(row.effectDieSides1 ?? 0),
                EffectBasePoints2 = Convert.ToInt32(row.effectBasePoints2 ?? 0),
                ManaCost = Convert.ToInt32(row.manaCost ?? 0),
                SpellLevel = Convert.ToInt32(row.spellLevel ?? 0),
                BaseLevel = Convert.ToInt32(row.baseLevel ?? 0),
                CastingTimeIndex = Convert.ToInt32(row.castingTimeIndex ?? 0),
                EffectRealPointsPerLevel1 = Convert.ToSingle(row.effectRealPointsPerLevel1 ?? 0f),
            };

            if (slaData != null)
            {
                info.SkillId = (int)slaData.skill_id;
                info.ClassMask = (int)slaData.class_mask;
            }

            results.Add(info);
        }

        return results;
    }

    /// <summary>
    /// Delete an entire rank chain. Finds all entries in spell_chain where first_spell = entry,
    /// then deletes them all from spell_template, spell_chain, skill_line_ability, trainers, and character_spell.
    /// Returns the list of all deleted entries (excluding rank 1 which is handled separately).
    /// </summary>
    public async Task<List<int>> DeleteRankChainAsync(int rank1Entry, string? operatorIp = null)
    {
        var chainEntries = await GetRankChainEntriesAsync(rank1Entry);
        var deletedEntries = new List<int>();

        // Delete all ranks EXCEPT rank 1 (rank 1 is deleted by the normal DeleteCustomSpellAsync flow)
        foreach (var (rank, spellId) in chainEntries)
        {
            if (spellId == rank1Entry) continue; // Skip rank 1
            if (spellId < CUSTOM_SPELL_BASE || spellId > CUSTOM_SPELL_MAX) continue; // Safety check

            try
            {
                using var mangosConn = _db.Mangos();
                await mangosConn.ExecuteAsync(
                    "DELETE FROM spell_template WHERE entry = @Entry", new { Entry = spellId });
                await RemoveSkillLineAbilityAsync(spellId);
                await RemoveSpellChainAsync(spellId);
                await RemoveFromTrainersAsync(spellId);

                try
                {
                    using var charConn = _db.Characters();
                    await charConn.ExecuteAsync(
                        "DELETE FROM character_spell WHERE spell = @Spell", new { Spell = spellId });
                }
                catch { /* character DB may not be accessible */ }

                deletedEntries.Add(spellId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SpellCreator: Failed to delete rank chain entry {Entry}", spellId);
            }
        }

        if (deletedEntries.Count > 0)
        {
            _logger.LogInformation("SpellCreator: Deleted {Count} rank chain entries for first_spell={First}: {Entries}",
                deletedEntries.Count, rank1Entry, string.Join(", ", deletedEntries));

            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = operatorIp,
                Category = "content",
                Action = "spell_chain_delete",
                TargetType = "spell_template",
                TargetName = $"Rank chain for #{rank1Entry}",
                IsReversible = false,
                Success = true,
                Notes = $"Deleted {deletedEntries.Count} rank entries: {string.Join(", ", deletedEntries.Select(e => $"#{e}"))}. Server restart required."
            });
        }

        return deletedEntries;
    }

    // ===================== NPC TRAINER =====================

    /// <summary>
    /// Primary trainer template IDs per class. One INSERT into npc_trainer_template
    /// with the right template ID → every trainer NPC of that class gets the spell.
    /// </summary>
    private static readonly Dictionary<int, int> ClassTrainerTemplateMap = new()
    {
        [1] = 23,  // Warrior
        [2] = 29,  // Paladin
        [3] = 20,  // Hunter (primary, 28 NPCs)
        [4] = 26,  // Rogue
        [5] = 8,   // Priest
        [7] = 10,  // Shaman
        [8] = 1,   // Mage
        [9] = 14,  // Warlock
        [11] = 17,  // Druid
    };

    /// <summary>Get the trainer template map for the frontend.</summary>
    public static Dictionary<int, int> GetClassTrainerTemplateMap() => ClassTrainerTemplateMap;

    /// <summary>Register a spell at a trainer NPC (direct npc_trainer entry).</summary>
    public async Task<bool> InsertNpcTrainerAsync(int trainerEntry, int spellEntry, int cost, int reqLevel, int reqSkill = 0, int reqSkillValue = 0)
    {
        using var conn = _db.Mangos();
        var affected = await conn.ExecuteAsync(
            @"INSERT IGNORE INTO npc_trainer (entry, spell, spellcost, reqskill, reqskillvalue, reqlevel, build_min, build_max)
              VALUES (@Trainer, @Spell, @Cost, @ReqSkill, @ReqSkillVal, @ReqLevel, 0, 5875)",
            new { Trainer = trainerEntry, Spell = spellEntry, Cost = cost, ReqSkill = reqSkill, ReqSkillVal = reqSkillValue, ReqLevel = reqLevel });

        if (affected > 0)
            _logger.LogInformation("SpellCreator: Registered spell {Spell} at trainer {Trainer} (cost={Cost}, reqLevel={Level})",
                spellEntry, trainerEntry, cost, reqLevel);

        return affected > 0;
    }

    /// <summary>Register a spell in a trainer template (all NPCs using that template get it).</summary>
    public async Task<bool> InsertNpcTrainerTemplateAsync(int templateId, int spellEntry, int cost, int reqLevel)
    {
        using var conn = _db.Mangos();
        var affected = await conn.ExecuteAsync(
            @"INSERT IGNORE INTO npc_trainer_template (entry, spell, spellcost, reqskill, reqskillvalue, reqlevel, build_min, build_max)
              VALUES (@Template, @Spell, @Cost, 0, 0, @ReqLevel, 0, 5875)",
            new { Template = templateId, Spell = spellEntry, Cost = cost, ReqLevel = reqLevel });

        if (affected > 0)
            _logger.LogInformation("SpellCreator: Registered spell {Spell} in trainer template {Template} (cost={Cost}, reqLevel={Level})",
                spellEntry, templateId, cost, reqLevel);

        return affected > 0;
    }

    /// <summary>Register a spell at ALL trainers that teach the source spell (copies trainer entries).
    /// Vanilla trainers use SPELL_EFFECT_LEARN_SPELL (36) wrapper spells — e.g., trainer spell 1173
    /// has effectTriggerSpell1=143 (Fireball R2). We resolve through this indirection layer,
    /// create a new wrapper for our custom spell, then insert the wrapper into matching trainers.</summary>
    public async Task<int> CopyTrainerEntriesFromSourceAsync(int sourceSpellEntry, int newSpellEntry, int cost, int reqLevel,
        string? spellName = null, string? rankSubtext = null)
    {
        using var conn = _db.Mangos();
        int total = 0;

        // Step 1: Find wrapper spells that teach the source spell via SPELL_EFFECT_LEARN_SPELL (36)
        var wrapperIds = (await conn.QueryAsync<int>(
            @"SELECT entry FROM spell_template 
              WHERE effect1 = 36 AND effectTriggerSpell1 = @Spell",
            new { Spell = sourceSpellEntry })).ToList();

        // Build the full set of spell IDs to search for: the source spell itself + all wrappers
        var searchSpells = new List<int> { sourceSpellEntry };
        searchSpells.AddRange(wrapperIds);

        if (wrapperIds.Count > 0)
            _logger.LogInformation("SpellCreator: Source spell {Src} has {Count} trainer wrapper(s): {Wrappers}",
                sourceSpellEntry, wrapperIds.Count, string.Join(", ", wrapperIds));

        // Collect all trainer locations (template IDs and direct NPC IDs) where source spell appears
        // Also read spellcost so we copy the correct training price from the source.
        var directTrainers = (await conn.QueryAsync<(int entry, int spellcost)>(
            "SELECT DISTINCT entry, spellcost FROM npc_trainer WHERE spell IN @Spells",
            new { Spells = searchSpells })).ToList();

        var templateTrainers = (await conn.QueryAsync<(int entry, int spellcost)>(
            "SELECT DISTINCT entry, spellcost FROM npc_trainer_template WHERE spell IN @Spells",
            new { Spells = searchSpells })).ToList();

        if (directTrainers.Count == 0 && templateTrainers.Count == 0)
        {
            _logger.LogInformation("SpellCreator: No trainer entries found for source spell {Src} (checked {Count} wrapper(s))",
                sourceSpellEntry, wrapperIds.Count);
            return 0;
        }

        // If caller passed cost=0, use the source's cost (auto-scale)
        if (cost <= 0)
        {
            cost = templateTrainers.Select(t => t.spellcost).FirstOrDefault(c => c > 0);
            if (cost <= 0)
                cost = directTrainers.Select(t => t.spellcost).FirstOrDefault(c => c > 0);
        }

        // Step 2: Look up spell name/subtext/icon if not provided
        int iconId = 185; // fallback to Fireball icon
        if (string.IsNullOrEmpty(spellName) || string.IsNullOrEmpty(rankSubtext))
        {
            var spellInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT name, nameSubtext, spellIconId FROM spell_template WHERE entry = @Entry",
                new { Entry = newSpellEntry });
            if (spellInfo != null)
            {
                spellName ??= spellInfo.name?.ToString() ?? "Custom Spell";
                rankSubtext ??= spellInfo.nameSubtext?.ToString() ?? "";
                iconId = Convert.ToInt32(spellInfo.spellIconId ?? 185);
            }
        }
        else
        {
            // Still need the icon even if name/subtext were provided
            var iconRow = await conn.ExecuteScalarAsync<int?>(
                "SELECT spellIconId FROM spell_template WHERE entry = @Entry AND build = 5875",
                new { Entry = newSpellEntry });
            iconId = iconRow ?? 185;
        }

        // Step 3: Create a SPELL_EFFECT_LEARN_SPELL wrapper for our custom spell
        int wrapperId = await CreateTrainerWrapperAsync(newSpellEntry, spellName ?? "Custom Spell", rankSubtext ?? "", reqLevel, iconId);

        // Step 4: Insert the wrapper into all matching trainer locations
        foreach (var trainer in directTrainers)
        {
            var affected = await conn.ExecuteAsync(
                @"INSERT IGNORE INTO npc_trainer (entry, spell, spellcost, reqskill, reqskillvalue, reqlevel, build_min, build_max)
                  VALUES (@Trainer, @Spell, @Cost, 0, 0, @ReqLevel, 0, 5875)",
                new { Trainer = trainer.entry, Spell = wrapperId, Cost = cost, ReqLevel = reqLevel });
            total += affected;
        }

        foreach (var template in templateTrainers)
        {
            var affected = await conn.ExecuteAsync(
                @"INSERT IGNORE INTO npc_trainer_template (entry, spell, spellcost, reqskill, reqskillvalue, reqlevel, build_min, build_max)
                  VALUES (@Template, @Spell, @Cost, 0, 0, @ReqLevel, 0, 5875)",
                new { Template = template.entry, Spell = wrapperId, Cost = cost, ReqLevel = reqLevel });
            total += affected;
        }

        _logger.LogInformation("SpellCreator: Created wrapper #{Wrapper} for #{New}, inserted into {Count} trainer location(s) (source: #{Src}, {WrapperCount} source wrapper(s))",
            wrapperId, newSpellEntry, total, sourceSpellEntry, wrapperIds.Count);

        return total;
    }

    /// <summary>Remove a spell from all trainers (both direct and template), including trainer wrappers.</summary>
    public async Task<int> RemoveFromTrainersAsync(int spellEntry)
    {
        using var conn = _db.Mangos();
        // Remove direct entries referencing the spell itself (legacy/shouldn't exist but just in case)
        int direct = await conn.ExecuteAsync(
            "DELETE FROM npc_trainer WHERE spell = @Spell", new { Spell = spellEntry });
        int template = await conn.ExecuteAsync(
            "DELETE FROM npc_trainer_template WHERE spell = @Spell", new { Spell = spellEntry });

        // Remove trainer wrappers (50000+ range) that teach this spell
        int wrappers = await RemoveTrainerWrappersAsync(spellEntry);

        return direct + template + wrappers;
    }

    /// <summary>List all custom spells (60000–65000).</summary>
    public async Task<List<dynamic>> ListCustomSpellsAsync()
    {
        using var conn = _db.Mangos();
        return (await conn.QueryAsync<dynamic>(
            @"SELECT entry, name, nameSubtext, school, spellLevel, manaCost, spellVisual1, spellIconId
              FROM spell_template WHERE entry >= @Base AND entry <= @Max ORDER BY entry",
            new { Base = CUSTOM_SPELL_BASE, Max = CUSTOM_SPELL_MAX })).ToList();
    }

    // ===================== RANK CHAIN GENERATION =====================

    /// <summary>
    /// Fetch all ranks of a source spell for proportional scaling.
    /// Returns list of (rank, spell_id, and all scalable fields).
    /// </summary>
    public async Task<List<Dictionary<string, object>>> GetSourceRankDataAsync(int sourceEntry)
    {
        using var conn = _db.Mangos();

        var firstSpell = await conn.ExecuteScalarAsync<int?>(
            "SELECT first_spell FROM spell_chain WHERE spell_id = @E", new { E = sourceEntry });
        if (!firstSpell.HasValue) firstSpell = sourceEntry;

        var ranks = (await conn.QueryAsync(
            @"SELECT sc.rank, sc.spell_id,
                     st.name, st.nameSubtext, st.spellLevel, st.baseLevel, st.maxLevel,
                     st.manaCost, st.effectBasePoints1, st.effectDieSides1,
                     st.effectBasePoints2, st.effectDieSides2,
                     st.effectRealPointsPerLevel1, st.effectBonusCoefficient1,
                     st.castingTimeIndex, st.rangeIndex, st.speed, st.recoveryTime
              FROM spell_chain sc
              JOIN spell_template st ON st.entry = sc.spell_id
                AND st.build = (SELECT MAX(build) FROM spell_template WHERE entry = sc.spell_id)
              WHERE sc.first_spell = @First
              ORDER BY sc.rank",
            new { First = firstSpell.Value })).ToList();

        return ranks.Select(r => (IDictionary<string, object>)r)
                     .Select(r => new Dictionary<string, object>(r))
                     .ToList();
    }

    /// <summary>
    /// Generate all ranks of a custom spell, mirroring the source spell's rank progression.
    /// Rank 1 is the already-created spell (existingRank1Entry). Ranks 2+ are new clones.
    /// Each rank clones from the corresponding source rank with proportional scaling applied.
    /// Returns list of (rank, newEntry) pairs.
    /// </summary>
    public async Task<List<(int rank, int entry)>> GenerateRankChainAsync(
        int existingRank1Entry,
        int sourceFirstSpell,
        string spellName,
        string? description,
        int school,
        string? skillTabKey,
        Dictionary<string, object?> rank1Overrides,
        Dictionary<int, Dictionary<string, object?>>? perRankOverrides,
        string? operatorIp = null,
        bool copySourceTrainers = false)
    {
        var sourceRanks = await GetSourceRankDataAsync(sourceFirstSpell);
        if (sourceRanks.Count <= 1)
            return new List<(int, int)> { (1, existingRank1Entry) };

        var result = new List<(int rank, int entry)> { (1, existingRank1Entry) };

        // Get rank 1 source data as baseline for ratio calculations
        var src1 = sourceRanks[0];
        int src1Bp = Convert.ToInt32(src1["effectBasePoints1"]);
        int src1Ds = Convert.ToInt32(src1["effectDieSides1"]);
        int src1Mana = Convert.ToInt32(src1["manaCost"]);

        // Get rank 1 custom values (what the user set)
        int r1Bp = rank1Overrides.ContainsKey("effectBasePoints1")
            ? Convert.ToInt32(rank1Overrides["effectBasePoints1"])
            : src1Bp;
        int r1Ds = rank1Overrides.ContainsKey("effectDieSides1")
            ? Convert.ToInt32(rank1Overrides["effectDieSides1"])
            : src1Ds;
        int r1Mana = rank1Overrides.ContainsKey("manaCost")
            ? Convert.ToInt32(rank1Overrides["manaCost"])
            : src1Mana;

        // Calculate scaling ratios from user's rank 1 vs source rank 1
        double dmgRatio = (src1Bp + src1Ds) > 0
            ? (double)(r1Bp + r1Ds) / (src1Bp + src1Ds)
            : 1.0;
        double manaRatio = src1Mana > 0 ? (double)r1Mana / src1Mana : 1.0;

        int prevEntry = existingRank1Entry;

        for (int i = 1; i < sourceRanks.Count; i++)
        {
            var srcRank = sourceRanks[i];
            int rank = Convert.ToInt32(srcRank["rank"]);
            int srcSpellId = Convert.ToInt32(srcRank["spell_id"]);

            // Scale values proportionally
            int scaledBp = (int)Math.Round(Convert.ToInt32(srcRank["effectBasePoints1"]) * dmgRatio);
            int scaledDs = (int)Math.Round(Convert.ToInt32(srcRank["effectDieSides1"]) * dmgRatio);
            int scaledMana = (int)Math.Round(Convert.ToInt32(srcRank["manaCost"]) * manaRatio);
            int scaledBp2 = (int)Math.Round(Convert.ToInt32(srcRank["effectBasePoints2"]) * dmgRatio);

            var overrides = new Dictionary<string, object?>
            {
                ["name"] = spellName,
                ["nameSubtext"] = $"Rank {rank}",
                // Use the SOURCE rank's description — it has template variables ($s1, $o2, $d)
                // that the client auto-fills from the spell's effect values per rank.
                // A user-typed description like "Deals 50 damage" would be wrong for ranks 2+.
                ["school"] = school,
                ["effectBasePoints1"] = scaledBp,
                ["effectDieSides1"] = scaledDs,
                ["effectBasePoints2"] = scaledBp2,
                ["manaCost"] = scaledMana,
                ["spellLevel"] = Convert.ToInt32(srcRank["spellLevel"]),
                ["baseLevel"] = Convert.ToInt32(srcRank["baseLevel"]),
                ["maxLevel"] = Convert.ToInt32(srcRank["maxLevel"]),
                ["castingTimeIndex"] = Convert.ToInt32(srcRank["castingTimeIndex"]),
                ["effectRealPointsPerLevel1"] = Convert.ToSingle(srcRank["effectRealPointsPerLevel1"]),
                ["effectBonusCoefficient1"] = Convert.ToSingle(srcRank["effectBonusCoefficient1"]),
            };

            // Apply per-rank user overrides if provided
            if (perRankOverrides != null && perRankOverrides.TryGetValue(rank, out var userOvr))
            {
                foreach (var kvp in userOvr)
                {
                    if (kvp.Value != null)
                        overrides[kvp.Key] = kvp.Value;
                }
            }

            // Clone from the corresponding source rank
            int newEntry = await CloneSpellAsync(srcSpellId, overrides, operatorIp);
            if (newEntry < 0)
            {
                _logger.LogWarning("SpellCreator: Failed to clone rank {Rank} from source {Src}", rank, srcSpellId);
                continue;
            }

            // spell_chain
            await InsertSpellChainAsync(newEntry, prevEntry, existingRank1Entry, rank);

            // skill_line_ability (with superseded_by = 0 for now, fixed below)
            if (!string.IsNullOrEmpty(skillTabKey))
                await InsertSkillLineAbilityAsync(newEntry, skillTabKey);

            // Session 33: Copy trainers from the corresponding source rank
            // e.g., if source Fireball R2 is at starter trainers (template 5),
            // custom R2 also goes to template 5.
            if (copySourceTrainers)
            {
                int copied = await CopyTrainerEntriesFromSourceAsync(
                    srcSpellId, newEntry, 0, Convert.ToInt32(srcRank["spellLevel"]));
                if (copied > 0)
                    _logger.LogInformation("SpellCreator: Copied {Count} trainer entries from source R{Rank} (#{Src}) to #{New}",
                        copied, rank, srcSpellId, newEntry);
            }

            // Update previous rank's superseded_by_spell
            await UpdateSupersededByAsync(prevEntry, newEntry);

            result.Add((rank, newEntry));
            prevEntry = newEntry;
        }

        _logger.LogInformation("SpellCreator: Generated {Count} ranks for {Name} (entries: {Entries})",
            result.Count, spellName, string.Join(", ", result.Select(r => $"R{r.rank}=#{r.entry}")));

        return result;
    }

    /// <summary>List all characters for the Teach dropdown.</summary>
    public async Task<List<dynamic>> ListCharactersAsync()
    {
        using var conn = _db.Characters();
        return (await conn.QueryAsync<dynamic>(
            "SELECT guid, name, level, class AS charClass, race FROM characters ORDER BY name")).ToList();
    }

    /// <summary>Which characters know a given spell?</summary>
    public async Task<List<dynamic>> GetCharactersWithSpellAsync(int spellEntry)
    {
        using var conn = _db.Characters();
        return (await conn.QueryAsync<dynamic>(
            @"SELECT c.guid, c.name, c.level, c.class AS charClass
              FROM character_spell cs JOIN characters c ON c.guid = cs.guid
              WHERE cs.spell = @Spell ORDER BY c.name",
            new { Spell = spellEntry })).ToList();
    }
}

/// <summary>
/// Session 33: Data returned by GetRankChainForPatchingAsync for each rank 2+ entry.
/// Used to populate SpellPatchRequest.AdditionalRanks for DBC patching.
/// </summary>
public class RankChainPatchInfo
{
    public int Entry { get; set; }
    public int SourceRankEntry { get; set; }
    public int Rank { get; set; }
    public string SpellName { get; set; } = "";
    public string? Description { get; set; }
    public int School { get; set; }
    public int SkillId { get; set; }
    public int ClassMask { get; set; }
    public int SupersededBySpell { get; set; }

    // Session 33: Effect/gameplay fields from spell_template for DBC patching
    public int EffectBasePoints1 { get; set; }
    public int EffectDieSides1 { get; set; }
    public int EffectBasePoints2 { get; set; }
    public int ManaCost { get; set; }
    public int SpellLevel { get; set; }
    public int BaseLevel { get; set; }
    public int CastingTimeIndex { get; set; }
    public float EffectRealPointsPerLevel1 { get; set; }
}