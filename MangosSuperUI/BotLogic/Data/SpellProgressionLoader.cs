using Dapper;
using MangosSuperUI.Models;
using MangosSuperUI.Services;

namespace MangosSuperUI.BotLogic.Data;

/// <summary>
/// Reads class/level → available spells from the mangos npc_trainer table.
/// Also provides nearest trainer lookups from creature + creature_template.
/// Data is cached at first query per class — trainer spell lists don't change at runtime.
/// </summary>
public class SpellProgressionLoader
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<SpellProgressionLoader> _logger;

    // Cache: classId → list of (spellId, reqLevel)
    private readonly Dictionary<int, List<TrainerSpell>> _spellsByClass = new();

    // Cache: classId → list of trainer spawn locations
    private readonly Dictionary<int, List<TrainerLocation>> _trainersByClass = new();

    public SpellProgressionLoader(ConnectionFactory db, ILogger<SpellProgressionLoader> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Get all spells a bot of this class could learn at or below the given level
    /// that they don't already know.
    /// </summary>
    public async Task<List<int>> GetUnlearnedSpellsAsync(int classId, int level, HashSet<int> knownSpells)
    {
        var allSpells = await GetClassSpellsAsync(classId);

        return allSpells
            .Where(s => s.ReqLevel <= level && !knownSpells.Contains(s.SpellId))
            .Select(s => s.SpellId)
            .ToList();
    }

    /// <summary>
    /// Get the nearest class trainer to the given position.
    /// Returns null if no trainer data is cached for this class.
    /// </summary>
    public async Task<TrainerLocation?> GetNearestTrainerAsync(int classId, int mapId, float x, float y, float z)
    {
        var trainers = await GetClassTrainersAsync(classId);

        return trainers
            .Where(t => t.MapId == mapId)
            .OrderBy(t => DistSq(t.X, t.Y, x, y))
            .FirstOrDefault();
    }

    // ==================== Cached Lookups ====================

    private async Task<List<TrainerSpell>> GetClassSpellsAsync(int classId)
    {
        if (_spellsByClass.TryGetValue(classId, out var cached))
            return cached;

        try
        {
            using var conn = _db.Mangos();

            // npc_trainer stores trainer_id → spell mappings
            // We need to find trainers for this class, then get their spell lists
            // VMaNGOS trainer entries are keyed by creature_template.trainer_id
            var spells = (await conn.QueryAsync<TrainerSpell>(@"
                SELECT DISTINCT nt.spell AS SpellId, nt.reqlevel AS ReqLevel, nt.spellcost AS Cost
                FROM npc_trainer nt
                INNER JOIN creature_template ct ON ct.trainer_id = nt.entry
                WHERE ct.trainer_class = @ClassId
                  AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)
                  AND nt.reqlevel > 0
                ORDER BY nt.reqlevel, nt.spell",
                new { ClassId = classId })).ToList();

            _spellsByClass[classId] = spells;
            _logger.LogInformation("SpellProgressionLoader: cached {Count} spells for class {ClassId}", spells.Count, classId);
            return spells;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SpellProgressionLoader: failed to load spells for class {ClassId}", classId);
            return new List<TrainerSpell>();
        }
    }

    private async Task<List<TrainerLocation>> GetClassTrainersAsync(int classId)
    {
        if (_trainersByClass.TryGetValue(classId, out var cached))
            return cached;

        try
        {
            using var conn = _db.Mangos();

            var trainers = (await conn.QueryAsync<TrainerLocation>(@"
                SELECT c.map AS MapId, c.position_x AS X, c.position_y AS Y, c.position_z AS Z,
                       ct.entry AS NpcEntry, ct.name AS NpcName
                FROM creature c
                INNER JOIN creature_template ct ON c.id = ct.entry
                WHERE ct.trainer_class = @ClassId
                  AND ct.npc_flags & 16 = 16
                  AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)
                GROUP BY c.map, c.position_x, c.position_y, c.position_z, ct.entry, ct.name",
                new { ClassId = classId })).ToList();

            _trainersByClass[classId] = trainers;
            _logger.LogInformation("SpellProgressionLoader: cached {Count} trainer locations for class {ClassId}",
                trainers.Count, classId);
            return trainers;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SpellProgressionLoader: failed to load trainers for class {ClassId}", classId);
            return new List<TrainerLocation>();
        }
    }

    private static float DistSq(float x1, float y1, float x2, float y2)
    {
        float dx = x1 - x2, dy = y1 - y2;
        return dx * dx + dy * dy;
    }
}

public class TrainerSpell
{
    public int SpellId { get; set; }
    public int ReqLevel { get; set; }
    public int Cost { get; set; }
}

public class TrainerLocation
{
    public int MapId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int NpcEntry { get; set; }
    public string NpcName { get; set; } = "";
}
