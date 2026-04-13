using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;

namespace MangosSuperUI.Controllers;

public class BotsController : Controller
{
    private readonly BotBridgeService _bridge;
    private readonly BotBrainService _brain;

    public BotsController(BotBridgeService bridge, BotBrainService brain)
    {
        _bridge = bridge;
        _brain = brain;
    }

    public IActionResult Index()
    {
        return View();
    }

    // ==================== REST API ====================

    [HttpGet]
    public IActionResult States()
    {
        var bots = _bridge.GetAllBotStates();
        return Json(new
        {
            connected = _bridge.ConnectedCount,
            totalTracked = _bridge.TotalTracked,
            bots
        });
    }

    [HttpGet]
    public IActionResult State(int id)
    {
        var state = _bridge.GetBotState(id);
        if (state == null)
            return NotFound(new { error = $"Bot {id} not found" });
        return Json(state);
    }

    [HttpPost]
    public async Task<IActionResult> MoveTo([FromBody] MoveToRequest req)
    {
        await _bridge.SendMoveToAsync(req.Guid, req.MapId, req.X, req.Y, req.Z);
        return Json(new { success = true });
    }

    [HttpPost]
    public async Task<IActionResult> SayText([FromBody] SayTextRequest req)
    {
        await _bridge.SendSayTextAsync(req.Guid, req.Text, req.ChatType);
        return Json(new { success = true });
    }

    // --- Phase 2.5 REST endpoints ---

    [HttpPost]
    public async Task<IActionResult> AcceptQuest([FromBody] QuestRequest req)
    {
        await _bridge.SendAcceptQuestAsync(req.Guid, req.QuestId);
        return Json(new { success = true, command = "ACCEPT_QUEST", req.Guid, req.QuestId });
    }

    [HttpPost]
    public async Task<IActionResult> CompleteQuest([FromBody] QuestRequest req)
    {
        await _bridge.SendCompleteQuestAsync(req.Guid, req.QuestId);
        return Json(new { success = true, command = "COMPLETE_QUEST", req.Guid, req.QuestId });
    }

    [HttpPost]
    public async Task<IActionResult> AbandonQuest([FromBody] QuestRequest req)
    {
        await _bridge.SendAbandonQuestAsync(req.Guid, req.QuestId);
        return Json(new { success = true, command = "ABANDON_QUEST", req.Guid, req.QuestId });
    }

    [HttpPost]
    public async Task<IActionResult> LearnSpell([FromBody] LearnSpellRequest req)
    {
        await _bridge.SendLearnSpellAsync(req.Guid, req.SpellId);
        return Json(new { success = true, command = "LEARN_SPELL", req.Guid, req.SpellId });
    }

    [HttpPost]
    public async Task<IActionResult> AttackTarget([FromBody] TargetRequest req)
    {
        await _bridge.SendAttackTargetAsync(req.Guid, req.TargetGuid);
        return Json(new { success = true, command = "ATTACK_TARGET", req.Guid, req.TargetGuid });
    }

    [HttpPost]
    public async Task<IActionResult> InteractNpc([FromBody] TargetRequest req)
    {
        await _bridge.SendInteractNpcAsync(req.Guid, req.TargetGuid);
        return Json(new { success = true, command = "INTERACT_NPC", req.Guid, req.TargetGuid });
    }

    [HttpPost]
    public async Task<IActionResult> SetTaskGrind([FromBody] SetTaskGrindRequest req)
    {
        await _bridge.SendSetTaskGrindAsync(req.Guid, req.X, req.Y, req.Z, req.Radius, req.CreatureEntry, req.KillCount);
        return Json(new { success = true, command = "SET_TASK_GRIND", req.Guid });
    }

    // ==================== BotBrain API ====================

    /// <summary>
    /// POST /Bots/ToggleBrain — enable/disable the behavioral engine.
    /// </summary>
    [HttpPost]
    public IActionResult ToggleBrain(bool enabled)
    {
        _brain.BrainEnabled = enabled;
        return Json(new { success = true, enabled = _brain.BrainEnabled });
    }

    /// <summary>
    /// GET /Bots/BrainState/{guid} — get brain summary for a specific bot.
    /// </summary>
    [HttpGet("Bots/BrainState/{guid}")]
    public IActionResult BrainState(int guid)
    {
        var summary = _brain.GetBotBrainSummary(guid);
        if (summary == null)
            return Json(new { guid, error = "No brain data for this bot" });
        return Json(summary);
    }

    /// <summary>
    /// GET /Bots/BrainStatus — overall brain engine status.
    /// </summary>
    [HttpGet]
    public IActionResult BrainStatus()
    {
        return Json(new
        {
            enabled = _brain.BrainEnabled,
            activeBots = _brain.ActiveBotCount,
            bots = _brain.AllBots.Values.Select(b => new
            {
                guid = b.Guid,
                name = b.Name,
                level = b.Level,
                activity = b.CurrentActivity.Type.ToString(),
                copper = b.CopperBalance,
                quirks = b.Personality.Quirks.Select(q => q.Name)
            })
        });
    }
}

// ==================== Request DTOs (unchanged) ====================

public class MoveToRequest
{
    public int Guid { get; set; }
    public int MapId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public class SayTextRequest
{
    public int Guid { get; set; }
    public string Text { get; set; } = "";
    public int ChatType { get; set; }
}

public class QuestRequest
{
    public int Guid { get; set; }
    public int QuestId { get; set; }
}

public class LearnSpellRequest
{
    public int Guid { get; set; }
    public int SpellId { get; set; }
}

public class TargetRequest
{
    public int Guid { get; set; }
    public int TargetGuid { get; set; }
}

public class SetTaskGrindRequest
{
    public int Guid { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; } = 60f;
    public int CreatureEntry { get; set; }
    public int KillCount { get; set; }
}