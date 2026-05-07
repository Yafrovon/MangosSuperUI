using Dapper;
using MangosSuperUI.Models;
using MangosSuperUI.Services;

namespace MangosSuperUI.BotLogic.Data;

/// <summary>
/// Loads class trainer locations from mangos DB at startup.
/// Used by TrainingDomain to find nearest trainer for MOVE_TO.
/// C++ handles actual spell learning via TRAIN_AT_NPC bridge command —
/// we only need to know WHERE the trainers are and WHICH class they serve.
/// </summary>
public class SpellProgressionLoader
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<SpellProgressionLoader> _logger;

    private Dictionary<int, List<TrainerLocation>> _trainersByClass = new();

    public bool IsLoaded { get; private set; }

    public SpellProgressionLoader(ConnectionFactory db, ILogger<SpellProgressionLoader> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LoadAsync()
    {
        try
        {
            using var conn = _db.Mangos();

            var rows = await conn.QueryAsync<dynamic>(@"
                SELECT ct.entry AS NpcEntry, ct.name AS NpcName,
                       ct.trainer_class AS TrainerClass,
                       c.map AS Map, c.position_x AS X, c.position_y AS Y, c.position_z AS Z
                FROM creature_template ct
                INNER JOIN creature c ON c.id = ct.entry
                WHERE ct.trainer_type = 0
                  AND ct.trainer_class > 0
                  AND (ct.npc_flags & 16) != 0
                  AND ct.patch = 0
                ORDER BY ct.trainer_class, ct.entry");

            _trainersByClass = new Dictionary<int, List<TrainerLocation>>();
            int total = 0;

            foreach (var row in rows)
            {
                int classId = Convert.ToInt32(row.TrainerClass);
                if (!_trainersByClass.ContainsKey(classId))
                    _trainersByClass[classId] = new List<TrainerLocation>();

                _trainersByClass[classId].Add(new TrainerLocation
                {
                    NpcEntry = Convert.ToInt32(row.NpcEntry),
                    NpcName = (string)(row.NpcName ?? "Unknown"),
                    Map = Convert.ToInt32(row.Map),
                    X = Convert.ToSingle(row.X),
                    Y = Convert.ToSingle(row.Y),
                    Z = Convert.ToSingle(row.Z)
                });
                total++;
            }

            IsLoaded = true;
            _logger.LogInformation(
                "[SpellProgression] Loaded {Count} trainer spawns across {Classes} classes",
                total, _trainersByClass.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SpellProgression] Failed to load trainer data");
            IsLoaded = false;
        }
    }

    public TrainerLocation? GetNearestTrainer(int classId, int mapId, float botX, float botY, float maxDistance = 2000f)
    {
        if (!_trainersByClass.TryGetValue(classId, out var trainers))
            return null;

        TrainerLocation? nearest = null;
        float nearestDist = float.MaxValue;

        foreach (var t in trainers)
        {
            if (t.Map != mapId) continue;
            float dx = t.X - botX, dy = t.Y - botY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist < nearestDist && dist <= maxDistance)
            {
                nearestDist = dist;
                nearest = t;
            }
        }

        return nearest;
    }
}

public class TrainerLocation
{
    public int NpcEntry { get; set; }
    public string NpcName { get; set; } = "";
    public int Map { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}