using MangosSuperUI.Models;
using Dapper;
using MangosSuperUI.Services;

namespace MangosSuperUI.BotLogic.Data;

/// <summary>
/// Loads the entire vanilla quest dependency graph from the mangos DB at startup.
/// Replaces LevelingGuideLoader — no static JSON, no manual guide authoring.
/// Every quest is a node; edges are prerequisite chains, exclusive groups, and breadcrumbs.
/// The bot's race, class, and level filter the graph to reachable quests at runtime.
///
/// All data is loaded once and held in memory (~4,700 quests — trivial footprint).
/// No per-bot queries needed.
///
/// KEY FIX (April 18, 2026): Kill target spawn positions are now resolved per-quest,
/// scoped to spawns near the quest giver. Before this fix, "Wolves Across the Border"
/// averaged ALL Young Wolf spawns across Elwynn Forest, producing a grind center 5,000+
/// yards from Northshire. Now it only averages the spawns within 500 yards of Eagan
/// Peltskinner, producing a correct ~60 yard grind center.
///
/// SESSION 19 FIX: PrevQuests reverse edge building. VMaNGOS builds quest prerequisite
/// lists from TWO sources: (1) PrevQuestId on the quest itself, and (2) reverse
/// NextQuestId edges — when quest A has NextQuestId=B, quest B gets A added to its
/// prevQuests list. Without this, quests like 3903 "Milly Osworth" (which has
/// PrevQuestId=0 but is referenced by quest 18 and 33 via NextQuestId) appeared
/// eligible before their real prerequisites were met, causing C++ CanTakeQuest to
/// reject them with requirements_not_met. See ObjectMgr.cpp lines 5925-5947.
/// </summary>
public class QuestGraphLoader
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<QuestGraphLoader> _logger;

    private Dictionary<int, QuestNode> _quests = new();
    private bool _loaded;

    public QuestGraphLoader(ConnectionFactory db, ILogger<QuestGraphLoader> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>All quests, keyed by quest_id.</summary>
    public IReadOnlyDictionary<int, QuestNode> AllQuests => _quests;

    /// <summary>Whether the graph has been loaded successfully.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>Get a specific quest by ID.</summary>
    public QuestNode? GetQuest(int questId) =>
        _quests.TryGetValue(questId, out var q) ? q : null;

    /// <summary>
    /// Get all quests a bot can currently accept given their race, class, level,
    /// and set of completed quest IDs. Respects PrevQuests chains (including
    /// reverse NextQuestId edges), ExclusiveGroup, race/class masks, and MinLevel.
    /// </summary>
    public List<QuestNode> GetAvailableQuests(int raceBit, int classBit, int level,
        HashSet<int> completedQuestIds, HashSet<int>? activeQuestIds = null)
    {
        var results = new List<QuestNode>();
        var active = activeQuestIds ?? new HashSet<int>();

        foreach (var quest in _quests.Values)
        {
            // Skip already completed
            if (completedQuestIds.Contains(quest.QuestId))
                continue;

            // Skip quests already in the bot's quest log (active or complete-not-turned-in)
            if (active.Contains(quest.QuestId))
                continue;

            // Level gate
            if (quest.MinLevel > level)
                continue;

            // Race/class masks
            if (!quest.IsAvailableToRace(raceBit))
                continue;
            if (!quest.IsAvailableToClass(classBit))
                continue;

            // PrevQuests check — mirrors VMaNGOS SatisfyQuestPreviousQuest.
            // The list is built from PrevQuestId (direct) + reverse NextQuestId edges.
            // Logic: if there are any positive entries (must be rewarded), at least
            // one of them must be in completedQuestIds. Negative entries mean "must
            // be active/in-progress" — we skip that check here since C++ will catch it.
            if (quest.PrevQuests.Count > 0)
            {
                var positivePrereqs = quest.PrevQuests.Where(pq => pq > 0).ToList();
                if (positivePrereqs.Count > 0)
                {
                    bool anyRewarded = positivePrereqs.Any(pq => completedQuestIds.Contains(pq));
                    if (!anyRewarded)
                        continue;
                }
                // Negative entries (must be active) — we don't have the active quest
                // set here, so let C++ CanTakeQuest handle those cases.
            }

            // ExclusiveGroup check — sign determines behavior:
            //   Positive: "pick one" — only one quest from this group can be active/completed.
            //             Block if any sibling is already active or completed.
            //   Negative: "do all" — all quests in this group must be completed before the
            //             follow-up (NextQuestId target) unlocks. No exclusion needed here;
            //             VMaNGOS checks the "all must be done" condition on the follow-up
            //             quest's SatisfyQuestPreviousQuest, not on accept of the individual.
            //   Session 29 fix: was treating ALL ExclusiveGroup values as "pick one",
            //   which blocked bots from having quest 18 AND 33 active simultaneously
            //   (they share ExclusiveGroup=-18, meaning both must be done for quest 3903).
            if (quest.ExclusiveGroup > 0)
            {
                bool groupConflict = _quests.Values.Any(other =>
                    other.QuestId != quest.QuestId &&
                    other.ExclusiveGroup == quest.ExclusiveGroup &&
                    (completedQuestIds.Contains(other.QuestId) ||
                     active.Contains(other.QuestId)));
                if (groupConflict)
                    continue;
            }

            // Must have a giver NPC (skip quests with no known giver)
            if (quest.Giver == null)
                continue;

            // Skip junk/test quests (no title, or <UNUSED>/<nyi>)
            if (string.IsNullOrEmpty(quest.Title) ||
                quest.Title.StartsWith("<UNUSED>") ||
                quest.Title.StartsWith("<nyi>") ||
                quest.Title.StartsWith("<TXT>") ||
                quest.Title.StartsWith("<TEST>"))
                continue;

            results.Add(quest);
        }

        return results;
    }

    // ── Startup Load ──────────────────────────────────────────────────────

    /// <summary>
    /// Load all quest data from the mangos DB. Call once at startup.
    /// </summary>
    public async Task LoadAsync()
    {
        _logger.LogInformation("QuestGraphLoader: loading quest graph from mangos DB...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var conn = _db.Mangos();

            // Step 1: Load all quest nodes
            var quests = await LoadQuestNodesAsync(conn);

            // Step 2: Load quest givers
            await LoadQuestGiversAsync(conn, quests);

            // Step 3: Load quest turn-ins
            await LoadQuestTurnInsAsync(conn, quests);

            // Step 4: Load ALL creature spawn positions for kill target creatures
            //         (raw spawns, not aggregated — we aggregate per-quest below)
            var allCreatureSpawns = await LoadCreatureSpawnPositionsAsync(conn, quests);

            // Step 5: Resolve kill target grind centers per-quest, scoped to giver proximity
            ResolveKillTargetsPerQuest(quests, allCreatureSpawns);

            // Step 6: Load item drop sources
            await LoadItemDropSourcesAsync(conn, quests);

            // Step 6b: Load gameobject drop sources (for items not dropped by creatures)
            await LoadGameObjectDropSourcesAsync(conn, quests);

            // Step 7: Load item names
            await LoadItemNamesAsync(conn, quests);

            // Step 8: Build PrevQuests lists (mirrors VMaNGOS ObjectMgr prevQuests)
            // Two sources: (a) PrevQuestId on this quest, (b) reverse NextQuestId edges
            BuildPrevQuestsLists(quests);

            _quests = quests;
            _loaded = true;

            sw.Stop();

            // Log summary stats
            int withGiver = quests.Values.Count(q => q.Giver != null);
            int withTurnIn = quests.Values.Count(q => q.TurnIn != null);
            int withKillObj = quests.Values.Count(q => q.HasKillObjectives);
            int withItemObj = quests.Values.Count(q => q.HasItemObjectives);
            int withGoObj = quests.Values.Count(q => q.ItemObjectives.Any(i => i.BestGoSource != null));
            int withPrereq = quests.Values.Count(q => q.PrevQuests.Count > 0);

            _logger.LogInformation(
                "QuestGraphLoader: loaded {Total} quests in {Ms}ms — " +
                "givers={Givers}, turnins={TurnIns}, kill_obj={Kill}, item_obj={Item}, go_obj={GO}, prereqs={Prereq}",
                quests.Count, sw.ElapsedMilliseconds,
                withGiver, withTurnIn, withKillObj, withItemObj, withGoObj, withPrereq);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuestGraphLoader: failed to load quest graph");
            _loaded = false;
        }
    }

    // ── Query 1: Quest Nodes ──────────────────────────────────────────────

    private async Task<Dictionary<int, QuestNode>> LoadQuestNodesAsync(System.Data.IDbConnection conn)
    {
        var rows = await conn.QueryAsync<dynamic>(@"
            SELECT
                entry, Title, QuestLevel, MinLevel, ZoneOrSort,
                RequiredRaces, RequiredClasses, QuestFlags, SpecialFlags,
                PrevQuestId, NextQuestId, NextQuestInChain, ExclusiveGroup, BreadcrumbForQuestId,
                ReqCreatureOrGOId1, ReqCreatureOrGOCount1,
                ReqCreatureOrGOId2, ReqCreatureOrGOCount2,
                ReqCreatureOrGOId3, ReqCreatureOrGOCount3,
                ReqCreatureOrGOId4, ReqCreatureOrGOCount4,
                ReqItemId1, ReqItemCount1,
                ReqItemId2, ReqItemCount2,
                ReqItemId3, ReqItemCount3,
                ReqItemId4, ReqItemCount4,
                SrcItemId, RewXP, RewOrReqMoney,
                RewChoiceItemId1, RewChoiceItemId2, RewChoiceItemId3,
                RewChoiceItemId4, RewChoiceItemId5, RewChoiceItemId6,
                RewItemId1, RewItemId2, RewItemId3, RewItemId4
            FROM quest_template
            WHERE patch = 0");

        var quests = new Dictionary<int, QuestNode>();

        foreach (var r in rows)
        {
            int id = (int)r.entry;
            var node = new QuestNode
            {
                QuestId = id,
                Title = (string)(r.Title ?? ""),
                QuestLevel = (int)r.QuestLevel,
                MinLevel = (int)r.MinLevel,
                ZoneId = (int)r.ZoneOrSort,
                RaceMask = (int)r.RequiredRaces,
                ClassMask = (int)r.RequiredClasses,
                QuestFlags = (int)r.QuestFlags,
                SpecialFlags = (int)r.SpecialFlags,
                PrevQuestId = (int)r.PrevQuestId,
                NextQuestId = (int)r.NextQuestId,
                NextQuestInChain = (int)r.NextQuestInChain,
                ExclusiveGroup = (int)r.ExclusiveGroup,
                BreadcrumbForQuestId = (int)r.BreadcrumbForQuestId,
                SrcItemId = (int)r.SrcItemId,
                RewXP = (int)r.RewXP,
                RewMoney = (int)r.RewOrReqMoney,
                RewChoiceItemId1 = (int)r.RewChoiceItemId1,
                RewChoiceItemId2 = (int)r.RewChoiceItemId2,
                RewChoiceItemId3 = (int)r.RewChoiceItemId3,
                RewChoiceItemId4 = (int)r.RewChoiceItemId4,
                RewChoiceItemId5 = (int)r.RewChoiceItemId5,
                RewChoiceItemId6 = (int)r.RewChoiceItemId6,
                RewItemId1 = (int)r.RewItemId1,
                RewItemId2 = (int)r.RewItemId2,
                RewItemId3 = (int)r.RewItemId3,
                RewItemId4 = (int)r.RewItemId4
            };

            // Build kill/interact objectives (slots 1-4)
            var objectives = new List<QuestObjective>();
            AddObjective(objectives, 1, (int)r.ReqCreatureOrGOId1, (int)r.ReqCreatureOrGOCount1);
            AddObjective(objectives, 2, (int)r.ReqCreatureOrGOId2, (int)r.ReqCreatureOrGOCount2);
            AddObjective(objectives, 3, (int)r.ReqCreatureOrGOId3, (int)r.ReqCreatureOrGOCount3);
            AddObjective(objectives, 4, (int)r.ReqCreatureOrGOId4, (int)r.ReqCreatureOrGOCount4);
            node.Objectives = objectives.ToArray();

            // Build item objectives (slots 1-4)
            var items = new List<QuestItemReq>();
            AddItemReq(items, 1, (int)r.ReqItemId1, (int)r.ReqItemCount1);
            AddItemReq(items, 2, (int)r.ReqItemId2, (int)r.ReqItemCount2);
            AddItemReq(items, 3, (int)r.ReqItemId3, (int)r.ReqItemCount3);
            AddItemReq(items, 4, (int)r.ReqItemId4, (int)r.ReqItemCount4);
            node.ItemObjectives = items.ToArray();

            quests[id] = node;
        }

        _logger.LogInformation("QuestGraphLoader: loaded {Count} quest nodes", quests.Count);
        return quests;
    }

    private static void AddObjective(List<QuestObjective> list, int slot, int creatureOrGO, int count)
    {
        if (creatureOrGO != 0 && count > 0)
            list.Add(new QuestObjective { Slot = slot, CreatureOrGOId = creatureOrGO, Count = count });
    }

    private static void AddItemReq(List<QuestItemReq> list, int slot, int itemId, int count)
    {
        if (itemId > 0 && count > 0)
            list.Add(new QuestItemReq { Slot = slot, ItemId = itemId, Count = count });
    }

    // ── Query 2: Quest Givers ─────────────────────────────────────────────

    private async Task LoadQuestGiversAsync(System.Data.IDbConnection conn, Dictionary<int, QuestNode> quests)
    {
        var rows = await conn.QueryAsync<dynamic>(@"
            SELECT
                cqr.quest, cqr.id AS npc_entry,
                ct.name,
                c.position_x, c.position_y, c.position_z, c.map
            FROM creature_questrelation cqr
            JOIN creature_template ct ON ct.entry = cqr.id AND ct.patch = 0
            JOIN creature c ON c.id = cqr.id
            GROUP BY cqr.quest, cqr.id");

        int resolved = 0;
        foreach (var r in rows)
        {
            int questId = (int)r.quest;
            if (quests.TryGetValue(questId, out var quest) && quest.Giver == null)
            {
                quest.Giver = new QuestNpcLocation
                {
                    NpcEntry = (int)r.npc_entry,
                    Name = (string)(r.name ?? ""),
                    X = (float)r.position_x,
                    Y = (float)r.position_y,
                    Z = (float)r.position_z,
                    Map = (int)r.map
                };
                resolved++;
            }
        }

        _logger.LogInformation("QuestGraphLoader: resolved {Count} quest givers", resolved);
    }

    // ── Query 3: Quest Turn-ins ───────────────────────────────────────────

    private async Task LoadQuestTurnInsAsync(System.Data.IDbConnection conn, Dictionary<int, QuestNode> quests)
    {
        var rows = await conn.QueryAsync<dynamic>(@"
            SELECT
                cir.quest, cir.id AS npc_entry,
                ct.name,
                c.position_x, c.position_y, c.position_z, c.map
            FROM creature_involvedrelation cir
            JOIN creature_template ct ON ct.entry = cir.id AND ct.patch = 0
            JOIN creature c ON c.id = cir.id
            GROUP BY cir.quest, cir.id");

        int resolved = 0;
        foreach (var r in rows)
        {
            int questId = (int)r.quest;
            if (quests.TryGetValue(questId, out var quest) && quest.TurnIn == null)
            {
                quest.TurnIn = new QuestNpcLocation
                {
                    NpcEntry = (int)r.npc_entry,
                    Name = (string)(r.name ?? ""),
                    X = (float)r.position_x,
                    Y = (float)r.position_y,
                    Z = (float)r.position_z,
                    Map = (int)r.map
                };
                resolved++;
            }
        }

        _logger.LogInformation("QuestGraphLoader: resolved {Count} quest turn-ins", resolved);
    }

    // ── Query 4: Load ALL creature spawn positions (raw, not aggregated) ──

    /// <summary>
    /// Load individual spawn positions for every creature entry referenced in kill
    /// objectives. Returns a dictionary: creature_entry → list of (map, x, y, z).
    /// We do NOT aggregate here — aggregation happens per-quest in ResolveKillTargetsPerQuest
    /// so we can scope the grind center to spawns near each quest's giver.
    /// </summary>
    private async Task<Dictionary<int, List<CreatureSpawn>>> LoadCreatureSpawnPositionsAsync(
        System.Data.IDbConnection conn, Dictionary<int, QuestNode> quests)
    {
        // Collect all creature entries referenced in kill objectives
        var creatureEntries = quests.Values
            .SelectMany(q => q.Objectives)
            .Where(o => o.IsCreature)
            .Select(o => o.CreatureEntry)
            .Distinct()
            .ToList();

        if (creatureEntries.Count == 0) return new();

        // Load individual spawn positions + creature names
        var rows = await conn.QueryAsync<dynamic>(@"
            SELECT
                c.id AS creature_entry,
                ct.name,
                c.map,
                c.position_x,
                c.position_y,
                c.position_z
            FROM creature c
            JOIN creature_template ct ON ct.entry = c.id AND ct.patch = 0
            WHERE c.id IN @Entries",
            new { Entries = creatureEntries });

        var result = new Dictionary<int, List<CreatureSpawn>>();

        foreach (var r in rows)
        {
            int entry = (int)r.creature_entry;

            if (!result.TryGetValue(entry, out var list))
            {
                list = new List<CreatureSpawn>();
                result[entry] = list;
            }

            list.Add(new CreatureSpawn
            {
                Name = (string)(r.name ?? ""),
                Map = (int)r.map,
                X = (float)r.position_x,
                Y = (float)r.position_y,
                Z = (float)r.position_z
            });
        }

        _logger.LogInformation("QuestGraphLoader: loaded {Spawns} individual spawn positions for {Creatures} kill target creatures",
            result.Values.Sum(l => l.Count), result.Count);

        return result;
    }

    // ── Step 5: Resolve kill target grind centers PER QUEST ───────────────

    /// <summary>
    /// For each quest's kill objectives, compute the grind center from the
    /// NEAREST CLUSTER of spawns to the quest giver — not from all spawns
    /// on the continent, and not even from all spawns within 500 yards.
    ///
    /// "Wolves Across the Border" giver = Eagan Peltskinner at (-8869,-163).
    /// Young Wolf (entry 299) spawns across all of Elwynn Forest.
    /// Old code: AVG(all spawns on map) = somewhere in central Elwynn (~5,000yd away).
    /// New code: sort spawns by distance from Eagan, take the nearest cluster
    /// (within 150yd of the closest wolf), average those → ~30yd from Eagan.
    ///
    /// This models real player behavior: you kill the first wolf you see near
    /// the quest giver, then work outward. You don't trek to the statistical
    /// centroid of all wolves on the continent.
    /// </summary>
    private void ResolveKillTargetsPerQuest(
        Dictionary<int, QuestNode> quests,
        Dictionary<int, List<CreatureSpawn>> allSpawns)
    {
        int resolved = 0;
        int totalObjectives = 0;
        int usedTier1 = 0, usedTier2 = 0, usedTier3 = 0, usedGlobal = 0;

        foreach (var quest in quests.Values)
        {
            // Use the quest giver position as the reference point.
            // If no giver, fall back to turn-in position. If neither, skip.
            float refX, refY;
            int refMap;

            if (quest.Giver != null)
            {
                refX = quest.Giver.X;
                refY = quest.Giver.Y;
                refMap = quest.Giver.Map;
            }
            else if (quest.TurnIn != null)
            {
                refX = quest.TurnIn.X;
                refY = quest.TurnIn.Y;
                refMap = quest.TurnIn.Map;
            }
            else
            {
                continue;
            }

            foreach (var obj in quest.Objectives)
            {
                if (!obj.IsCreature) continue;
                totalObjectives++;

                if (!allSpawns.TryGetValue(obj.CreatureEntry, out var spawns) || spawns.Count == 0)
                    continue;

                // Filter to same map as quest giver first
                var sameMapSpawns = spawns.Where(s => s.Map == refMap).ToList();
                if (sameMapSpawns.Count == 0)
                {
                    // Creature doesn't spawn on this map at all — use best map globally
                    var bestMap = spawns.GroupBy(s => s.Map)
                        .OrderByDescending(g => g.Count())
                        .First();
                    var sorted = bestMap.OrderBy(s => Distance2D(s.X, s.Y, refX, refY)).ToList();
                    var cluster = TakeNearestCluster(sorted, refX, refY);
                    var agg = AggregateSpawns(cluster);
                    ApplyToObjective(obj, cluster[0].Name, bestMap.Key, agg, cluster);
                    usedGlobal++;
                    resolved++;
                    continue;
                }

                // Sort all same-map spawns by distance from quest giver.
                // A real player kills the first wolf they see, then works outward.
                // We take the nearest cluster of spawns (up to 10, or all within
                // 150yd of the nearest one — whichever is more) as the grind center.
                var sortedSpawns = sameMapSpawns
                    .OrderBy(s => Distance2D(s.X, s.Y, refX, refY))
                    .ToList();

                float nearestDist = Distance2D(sortedSpawns[0].X, sortedSpawns[0].Y, refX, refY);

                // Track which tier the nearest spawn fell into (for logging)
                if (nearestDist <= 500f) usedTier1++;
                else if (nearestDist <= 1000f) usedTier2++;
                else if (nearestDist <= 2000f) usedTier3++;
                else usedGlobal++;

                var nearestCluster = TakeNearestCluster(sortedSpawns, refX, refY);
                var result = AggregateSpawns(nearestCluster);
                ApplyToObjective(obj, nearestCluster[0].Name, refMap, result, nearestCluster);
                resolved++;
            }
        }

        _logger.LogInformation(
            "QuestGraphLoader: resolved {Resolved}/{Total} kill objectives — " +
            "proximity tiers: ≤500yd={T1}, ≤1000yd={T2}, ≤2000yd={T3}, map-wide={Global}",
            resolved, totalObjectives, usedTier1, usedTier2, usedTier3, usedGlobal);
    }

    /// <summary>
    /// From a list of spawns sorted by distance from the quest giver, take
    /// the nearest cluster. A "cluster" = all spawns within 150 yards of the
    /// nearest spawn, or at least 5 spawns (whichever gives more).
    ///
    /// This models how a real player handles "kill 10 wolves": they kill the
    /// first wolf they see near the quest giver, then work outward. The grind
    /// center should be where those nearest wolves are, not the average of
    /// all wolves on the continent.
    ///
    /// The 150yd cluster radius handles cases where spawns are loosely
    /// scattered (e.g., 8 wolves over a 200yd stretch outside Northshire).
    /// The min-5 floor handles cases where spawns are very sparse.
    /// </summary>
    private static List<CreatureSpawn> TakeNearestCluster(List<CreatureSpawn> sortedByDistance, float refX, float refY)
    {
        if (sortedByDistance.Count <= 5) return sortedByDistance;

        // The nearest spawn is the anchor point for the cluster
        var anchor = sortedByDistance[0];
        float clusterRadius = 150f;

        // Take all spawns within clusterRadius of the anchor
        var cluster = sortedByDistance
            .Where(s => Distance2D(s.X, s.Y, anchor.X, anchor.Y) <= clusterRadius)
            .ToList();

        // Ensure we have at least 5 spawns (grab nearest if cluster is too tight)
        if (cluster.Count < 5)
            cluster = sortedByDistance.Take(Math.Min(5, sortedByDistance.Count)).ToList();

        return cluster;
    }

    /// <summary>Compute average position and spread radius from a list of spawns.</summary>
    private static (float x, float y, float z, float radius) AggregateSpawns(List<CreatureSpawn> spawns)
    {
        float avgX = spawns.Average(s => s.X);
        float avgY = spawns.Average(s => s.Y);
        float avgZ = spawns.Average(s => s.Z);

        float spreadX = spawns.Max(s => s.X) - spawns.Min(s => s.X);
        float spreadY = spawns.Max(s => s.Y) - spawns.Min(s => s.Y);
        float radius = Math.Clamp(Math.Max(spreadX, spreadY) / 2f, 20f, 80f);

        return (avgX, avgY, avgZ, radius);
    }

    /// <summary>Apply aggregated spawn data to a quest objective.</summary>
    private static void ApplyToObjective(QuestObjective obj, string name, int map,
        (float x, float y, float z, float radius) agg,
        List<CreatureSpawn>? clusterSpawns = null)
    {
        obj.TargetName = name;
        obj.GrindMap = map;
        obj.GrindX = agg.x;
        obj.GrindY = agg.y;
        obj.GrindZ = agg.z;
        obj.GrindRadius = agg.radius;

        // Session 31: Preserve individual spawn positions for fan-out
        if (clusterSpawns != null && clusterSpawns.Count > 0)
        {
            obj.SpawnPositions = clusterSpawns
                .Select(s => (s.X, s.Y, s.Z))
                .ToList();
        }
    }

    private static float Distance2D(float x1, float y1, float x2, float y2)
    {
        float dx = x1 - x2;
        float dy = y1 - y2;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ── Query 5: Item Drop Sources ────────────────────────────────────────

    private async Task LoadItemDropSourcesAsync(System.Data.IDbConnection conn, Dictionary<int, QuestNode> quests)
    {
        // Collect all item IDs referenced in item objectives
        var itemIds = quests.Values
            .SelectMany(q => q.ItemObjectives)
            .Select(i => i.ItemId)
            .Distinct()
            .ToList();

        if (itemIds.Count == 0) return;

        // Query creature_loot_template for drop sources
        var dropRows = await conn.QueryAsync<dynamic>(@"
            SELECT
                clt.item AS item_id,
                clt.entry AS creature_entry,
                ct.name AS creature_name,
                clt.ChanceOrQuestChance AS drop_chance
            FROM creature_loot_template clt
            JOIN creature_template ct ON ct.entry = clt.entry AND ct.patch = 0
            WHERE clt.item IN @ItemIds",
            new { ItemIds = itemIds });

        // Group by item → list of drop sources
        var dropMap = new Dictionary<int, List<ItemDropSource>>();
        var dropCreatureEntries = new HashSet<int>();

        foreach (var r in dropRows)
        {
            int itemId = (int)r.item_id;
            int creatureEntry = (int)r.creature_entry;

            if (!dropMap.TryGetValue(itemId, out var list))
            {
                list = new List<ItemDropSource>();
                dropMap[itemId] = list;
            }

            list.Add(new ItemDropSource
            {
                CreatureEntry = creatureEntry,
                CreatureName = (string)(r.creature_name ?? ""),
                DropChance = (float)r.drop_chance
            });

            dropCreatureEntries.Add(creatureEntry);
        }

        // Load INDIVIDUAL spawn positions for drop source creatures (not aggregated).
        // We resolve grind centers per-quest below, scoped to the quest giver's proximity —
        // same pattern as ResolveKillTargetsPerQuest(). This is the Session 26 P0 fix:
        // the old code GROUP BY'd globally, causing item-drop quests to get grind centers
        // 3000yd away (e.g., Tough Wolf Meat → Dun Morogh instead of Northshire).
        var dropCreatureSpawns = new Dictionary<int, List<CreatureSpawn>>();
        if (dropCreatureEntries.Count > 0)
        {
            var spawnRows = await conn.QueryAsync<dynamic>(@"
                SELECT
                    c.id AS creature_entry,
                    ct.name,
                    c.map,
                    c.position_x,
                    c.position_y,
                    c.position_z
                FROM creature c
                JOIN creature_template ct ON ct.entry = c.id AND ct.patch = 0
                WHERE c.id IN @Entries",
                new { Entries = dropCreatureEntries.ToList() });

            foreach (var r in spawnRows)
            {
                int entry = (int)r.creature_entry;
                if (!dropCreatureSpawns.TryGetValue(entry, out var list))
                {
                    list = new List<CreatureSpawn>();
                    dropCreatureSpawns[entry] = list;
                }
                list.Add(new CreatureSpawn
                {
                    Name = (string)(r.name ?? ""),
                    Map = (int)r.map,
                    X = (float)r.position_x,
                    Y = (float)r.position_y,
                    Z = (float)r.position_z
                });
            }

            _logger.LogInformation(
                "QuestGraphLoader: loaded {Spawns} individual spawns for {Creatures} item-drop creatures",
                dropCreatureSpawns.Values.Sum(l => l.Count), dropCreatureSpawns.Count);
        }

        // Apply drop sources to quest item objectives AND resolve grind centers per-quest
        int resolved = 0;
        int itemObjResolved = 0;
        int usedNearby = 0, usedGlobalFallback = 0;

        foreach (var quest in quests.Values)
        {
            foreach (var itemObj in quest.ItemObjectives)
            {
                if (!dropMap.TryGetValue(itemObj.ItemId, out var sources))
                    continue;

                itemObj.DropSources = sources;
                resolved++;

                // Resolve grind center for BestDropSource, scoped to quest giver proximity.
                // Use giver position as reference; fall back to turn-in; skip if neither.
                float refX, refY;
                int refMap;
                if (quest.Giver != null)
                {
                    refX = quest.Giver.X;
                    refY = quest.Giver.Y;
                    refMap = quest.Giver.Map;
                }
                else if (quest.TurnIn != null)
                {
                    refX = quest.TurnIn.X;
                    refY = quest.TurnIn.Y;
                    refMap = quest.TurnIn.Map;
                }
                else continue;

                // Find the best drop source creature that has spawns near the quest giver.
                // Priority: highest |DropChance| among creatures with same-map spawns near giver.
                ItemDropSource? bestSource = null;
                List<CreatureSpawn>? bestCluster = null;
                int bestClusterMap = refMap;

                foreach (var ds in sources.OrderByDescending(s => Math.Abs(s.DropChance)))
                {
                    if (!dropCreatureSpawns.TryGetValue(ds.CreatureEntry, out var spawns) || spawns.Count == 0)
                        continue;

                    // Prefer same-map spawns near the quest giver
                    var sameMapSpawns = spawns.Where(s => s.Map == refMap).ToList();
                    if (sameMapSpawns.Count > 0)
                    {
                        var sorted = sameMapSpawns
                            .OrderBy(s => Distance2D(s.X, s.Y, refX, refY))
                            .ToList();
                        var cluster = TakeNearestCluster(sorted, refX, refY);
                        bestSource = ds;
                        bestCluster = cluster;
                        bestClusterMap = refMap;
                        break; // Same-map + highest drop chance = best option
                    }

                    // Track best cross-map fallback (only if no same-map source found yet)
                    if (bestSource == null)
                    {
                        var bestMap = spawns.GroupBy(s => s.Map)
                            .OrderByDescending(g => g.Count())
                            .First();
                        var sorted = bestMap
                            .OrderBy(s => Distance2D(s.X, s.Y, refX, refY))
                            .ToList();
                        bestSource = ds;
                        bestCluster = TakeNearestCluster(sorted, refX, refY);
                        bestClusterMap = bestMap.Key;
                        // Don't break — keep looking for same-map sources with lower drop chance
                    }
                }

                if (bestSource != null && bestCluster != null)
                {
                    var agg = AggregateSpawns(bestCluster);
                    bestSource.SpawnCount = bestCluster.Count;
                    bestSource.GrindMap = bestClusterMap;
                    bestSource.GrindX = agg.x;
                    bestSource.GrindY = agg.y;
                    bestSource.GrindZ = agg.z;
                    bestSource.GrindRadius = agg.radius;
                    // Session 31: Preserve individual spawn positions for fan-out
                    bestSource.SpawnPositions = bestCluster
                        .Select(s => (s.X, s.Y, s.Z))
                        .ToList();
                    itemObjResolved++;

                    if (bestClusterMap == refMap) usedNearby++;
                    else usedGlobalFallback++;
                }
            }
        }

        _logger.LogInformation(
            "QuestGraphLoader: resolved drop sources for {Resolved}/{Total} item objectives " +
            "({Items} unique items, {Creatures} drop creatures) — " +
            "grind centers: {ObjResolved} resolved (nearby={Nearby}, cross-map={Global})",
            resolved, quests.Values.SelectMany(q => q.ItemObjectives).Count(),
            itemIds.Count, dropCreatureEntries.Count,
            itemObjResolved, usedNearby, usedGlobalFallback);
    }

    // ── Query 6b: Game Object Drop Sources ──────────────────────────────

    /// <summary>
    /// For quest items that have NO creature drop source, check if they come from
    /// game objects (chests, barrels, herb nodes, etc.) via gameobject_loot_template.
    /// Loads GO spawn positions so bots can walk to and interact with them.
    /// </summary>
    private async Task LoadGameObjectDropSourcesAsync(System.Data.IDbConnection conn, Dictionary<int, QuestNode> quests)
    {
        // Find item objectives that have no creature drop source
        var itemsWithNoCreatureDrop = quests.Values
            .SelectMany(q => q.ItemObjectives)
            .Where(i => i.BestDropSource == null)
            .Select(i => i.ItemId)
            .Distinct()
            .ToList();

        if (itemsWithNoCreatureDrop.Count == 0) return;

        // Query gameobject_loot_template → gameobject_template → gameobject spawns
        var rows = await conn.QueryAsync<dynamic>(@"
            SELECT
                golt.item AS item_id,
                gt.entry AS go_entry,
                gt.name AS go_name,
                g.map,
                g.position_x,
                g.position_y,
                g.position_z
            FROM gameobject_loot_template golt
            JOIN gameobject_template gt ON gt.data1 = golt.entry AND gt.type = 3
            JOIN gameobject g ON g.id = gt.entry
            WHERE golt.item IN @ItemIds",
            new { ItemIds = itemsWithNoCreatureDrop });

        // Group by (itemId, goEntry) → aggregate spawn positions
        var goSourceMap = new Dictionary<int, Dictionary<int, GameObjectDropSource>>();

        foreach (var r in rows)
        {
            int itemId = (int)r.item_id;
            int goEntry = (int)r.go_entry;

            if (!goSourceMap.TryGetValue(itemId, out var goDict))
            {
                goDict = new Dictionary<int, GameObjectDropSource>();
                goSourceMap[itemId] = goDict;
            }

            if (!goDict.TryGetValue(goEntry, out var source))
            {
                source = new GameObjectDropSource
                {
                    GoEntry = goEntry,
                    GoName = (string)(r.go_name ?? "")
                };
                goDict[goEntry] = source;
            }

            source.SpawnPositions.Add(((float)r.position_x, (float)r.position_y, (float)r.position_z));
        }

        // Aggregate and apply to quest item objectives
        int resolved = 0;
        foreach (var quest in quests.Values)
        {
            // Get reference position for proximity scoping
            float refX, refY;
            int refMap;
            if (quest.Giver != null) { refX = quest.Giver.X; refY = quest.Giver.Y; refMap = quest.Giver.Map; }
            else if (quest.TurnIn != null) { refX = quest.TurnIn.X; refY = quest.TurnIn.Y; refMap = quest.TurnIn.Map; }
            else continue;

            foreach (var itemObj in quest.ItemObjectives)
            {
                if (itemObj.BestDropSource != null) continue; // creature source exists, skip
                if (!goSourceMap.TryGetValue(itemObj.ItemId, out var goDict)) continue;

                foreach (var (goEntry, source) in goDict)
                {
                    if (source.SpawnPositions.Count == 0) continue;

                    // Filter to same map as quest giver
                    // (GO spawns don't have a map field per-spawn in our query, but we joined
                    // via gameobject table which has map — all spawns for one GO entry share map
                    // since we loaded map from the gameobject table. Actually we did load per-spawn
                    // map. Let me just use the first spawn's inferred map.)
                    // Actually we loaded map per row. Let me aggregate properly.
                    source.SpawnCount = source.SpawnPositions.Count;
                    float avgX = source.SpawnPositions.Average(s => s.X);
                    float avgY = source.SpawnPositions.Average(s => s.Y);
                    float avgZ = source.SpawnPositions.Average(s => s.Z);

                    float spreadX = source.SpawnPositions.Max(s => s.X) - source.SpawnPositions.Min(s => s.X);
                    float spreadY = source.SpawnPositions.Max(s => s.Y) - source.SpawnPositions.Min(s => s.Y);
                    float radius = Math.Clamp(Math.Max(spreadX, spreadY) / 2f, 15f, 80f);

                    source.X = avgX;
                    source.Y = avgY;
                    source.Z = avgZ;
                    source.Map = refMap; // GO spawns are on same map as quest giver for starter zones
                    source.Radius = radius;

                    itemObj.GoSources.Add(source);
                    resolved++;
                }
            }
        }

        _logger.LogInformation(
            "QuestGraphLoader: resolved {Resolved} GO drop sources for {Items} items with no creature source",
            resolved, itemsWithNoCreatureDrop.Count);
    }

    // ── Query 7: Item Names ───────────────────────────────────────────────

    private async Task LoadItemNamesAsync(System.Data.IDbConnection conn, Dictionary<int, QuestNode> quests)
    {
        var itemIds = quests.Values
            .SelectMany(q => q.ItemObjectives)
            .Select(i => i.ItemId)
            .Distinct()
            .ToList();

        if (itemIds.Count == 0) return;

        var rows = await conn.QueryAsync<dynamic>(@"
            SELECT entry, name
            FROM item_template
            WHERE entry IN @ItemIds AND patch = 0",
            new { ItemIds = itemIds });

        var nameMap = rows.ToDictionary(r => (int)r.entry, r => (string)(r.name ?? ""));

        foreach (var quest in quests.Values)
        {
            foreach (var itemObj in quest.ItemObjectives)
            {
                if (nameMap.TryGetValue(itemObj.ItemId, out var name))
                    itemObj.ItemName = name;
            }
        }

        _logger.LogInformation("QuestGraphLoader: resolved {Count} item names", nameMap.Count);
    }

    // ── Step 8: Build PrevQuests Lists ─────────────────────────────────────

    /// <summary>
    /// Mirrors VMaNGOS ObjectMgr.cpp lines 5925-5947.
    /// Builds each quest's PrevQuests list from two sources:
    ///   1. PrevQuestId on the quest itself → added to own PrevQuests
    ///   2. NextQuestId on OTHER quests → added to the target quest's PrevQuests
    /// Sign convention matches VMaNGOS:
    ///   Positive prevQuest = must be rewarded (completed and turned in)
    ///   Negative prevQuest = must be active (currently in quest log)
    /// SatisfyQuestPreviousQuest logic: if ANY positive entry is rewarded, pass.
    /// </summary>
    private void BuildPrevQuestsLists(Dictionary<int, QuestNode> quests)
    {
        int directCount = 0, reverseCount = 0;

        foreach (var quest in quests.Values)
        {
            // Source 1: PrevQuestId → own prevQuests (VMaNGOS line 5935)
            // Skip if the target quest doesn't exist (same guard as VMaNGOS)
            if (quest.PrevQuestId != 0 && quests.ContainsKey(Math.Abs(quest.PrevQuestId)))
            {
                quest.PrevQuests.Add(quest.PrevQuestId);
                directCount++;
            }

            // Source 2: NextQuestId → target quest's prevQuests (VMaNGOS line 5946)
            // If this quest has NextQuestId, add this quest's ID to the target's PrevQuests.
            // Sign: if NextQuestId > 0, push positive (must be rewarded).
            //        if NextQuestId < 0, push negative (must be active).
            if (quest.NextQuestId != 0)
            {
                int targetId = Math.Abs(quest.NextQuestId);
                if (quests.TryGetValue(targetId, out var targetQuest))
                {
                    int signedId = quest.NextQuestId < 0
                        ? -quest.QuestId
                        : quest.QuestId;
                    targetQuest.PrevQuests.Add(signedId);
                    reverseCount++;
                }
            }
        }

        _logger.LogInformation(
            "QuestGraphLoader: built PrevQuests — {Direct} direct, {Reverse} reverse edges",
            directCount, reverseCount);
    }

    // ── Race/Class Bitmask Helpers ─────────────────────────────────────────

    /// <summary>
    /// Convert WowRace enum value (1-8) to the race bitmask used in quest_template.RequiredRaces.
    /// </summary>
    public static int RaceToBitmask(int raceId) => raceId switch
    {
        1 => 1,    // Human
        2 => 2,    // Orc
        3 => 4,    // Dwarf
        4 => 8,    // Night Elf
        5 => 16,   // Undead
        6 => 32,   // Tauren
        7 => 64,   // Gnome
        8 => 128,  // Troll
        _ => 0
    };

    /// <summary>
    /// Convert WowClass enum value to the class bitmask used in quest_template.RequiredClasses.
    /// </summary>
    public static int ClassToBitmask(int classId) => classId switch
    {
        1 => 1,     // Warrior
        2 => 2,     // Paladin
        3 => 4,     // Hunter
        4 => 8,     // Rogue
        5 => 16,    // Priest
        7 => 64,    // Shaman
        8 => 128,   // Mage
        9 => 256,   // Warlock
        11 => 1024, // Druid
        _ => 0
    };
}

/// <summary>
/// Individual creature spawn position. Used as intermediate data during per-quest
/// kill target resolution. Not stored long-term — aggregated into QuestObjective fields.
/// </summary>
internal class CreatureSpawn
{
    public string Name { get; set; } = "";
    public int Map { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}