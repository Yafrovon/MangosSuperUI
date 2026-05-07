using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.BotLogic.Data;
using Dapper;
using MangosSuperUI.Services;
using MangosSuperUI.Models;

namespace MangosSuperUI.Controllers;

/// <summary>
/// Quest graph data — zone chains, quest metadata, NPC locations.
/// General-purpose (not bot-specific). Bot progress queries live in BotsController.
/// </summary>
public class QuestDataController : Controller
{
    private readonly QuestGraphLoader _questGraph;
    private readonly ConnectionFactory _db;

    public QuestDataController(QuestGraphLoader questGraph, ConnectionFactory db)
    {
        _questGraph = questGraph;
        _db = db;
    }

    /// <summary>
    /// GET /QuestData/ZoneChain?zoneId=9
    /// Returns all quests for a zone with full chain edge data.
    /// Used by the quest chain visualizer.
    /// </summary>
    [HttpGet]
    public IActionResult ZoneChain(int zoneId)
    {
        if (!_questGraph.IsLoaded)
            return Json(new { error = "Quest graph not loaded" });

        var quests = _questGraph.AllQuests.Values
            .Where(q => q.ZoneId == zoneId && q.Giver != null)
            .Select(q => new
            {
                q.QuestId,
                q.Title,
                q.QuestLevel,
                q.MinLevel,
                q.PrevQuestId,
                q.NextQuestId,
                q.NextQuestInChain,
                q.ExclusiveGroup,
                q.RaceMask,
                q.ClassMask,
                prevQuests = q.PrevQuests,
                hasKillObjectives = q.HasKillObjectives,
                hasItemObjectives = q.HasItemObjectives,
                objectives = q.Objectives.Select(o => new
                {
                    o.Slot,
                    o.CreatureEntry,
                    o.Count,
                    o.TargetName,
                    o.GrindX,
                    o.GrindY
                }),
                itemObjectives = q.ItemObjectives.Select(i => new
                {
                    i.ItemId,
                    i.Count,
                    i.ItemName,
                    bestDrop = i.BestDropSource != null ? new
                    {
                        i.BestDropSource.CreatureEntry,
                        i.BestDropSource.CreatureName,
                        i.BestDropSource.GrindX,
                        i.BestDropSource.GrindY
                    } : null
                }),
                giver = q.Giver != null ? new { q.Giver.NpcEntry, q.Giver.Name, q.Giver.X, q.Giver.Y, q.Giver.Map } : null,
                turnIn = q.TurnIn != null ? new { q.TurnIn.NpcEntry, q.TurnIn.Name, q.TurnIn.X, q.TurnIn.Y, q.TurnIn.Map } : null
            })
            .OrderBy(q => q.QuestLevel)
            .ThenBy(q => q.QuestId)
            .ToList();

        return Json(new { zoneId, count = quests.Count, quests });
    }

    /// <summary>
    /// GET /QuestData/Zones
    /// Returns all zones that have quests with givers, for the zone selector dropdown.
    /// </summary>
    [HttpGet]
    public IActionResult Zones()
    {
        if (!_questGraph.IsLoaded)
            return Json(new { error = "Quest graph not loaded" });

        var zones = _questGraph.AllQuests.Values
            .Where(q => q.ZoneId > 0 && q.Giver != null)
            .GroupBy(q => q.ZoneId)
            .Select(g => new
            {
                zoneId = g.Key,
                questCount = g.Count(),
                minLevel = g.Min(q => q.MinLevel),
                maxLevel = g.Max(q => q.QuestLevel)
            })
            .OrderBy(z => z.minLevel)
            .ThenBy(z => z.zoneId)
            .ToList();

        return Json(zones);
    }
}
