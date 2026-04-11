using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;

namespace MangosSuperUI.Controllers;

public class BotsController : Controller
{
    private readonly BotBridgeService _bridge;

    public BotsController(BotBridgeService bridge)
    {
        _bridge = bridge;
    }

    public IActionResult Index()
    {
        return View();
    }

    // ==================== REST API ====================
    // These supplement the SignalR hub for non-realtime queries.

    /// <summary>
    /// GET /Bots/Api/States — snapshot of all bot states as JSON.
    /// </summary>
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

    /// <summary>
    /// GET /Bots/Api/State/{guid} — single bot state.
    /// </summary>
    [HttpGet]
    public IActionResult State(int id)
    {
        var state = _bridge.GetBotState(id);
        if (state == null)
            return NotFound(new { error = $"Bot {id} not found" });

        return Json(state);
    }

    /// <summary>
    /// POST /Bots/Api/MoveTo — send MOVE_TO command.
    /// Body: { guid, mapId, x, y, z }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> MoveTo([FromBody] MoveToRequest req)
    {
        await _bridge.SendMoveToAsync(req.Guid, req.MapId, req.X, req.Y, req.Z);
        return Json(new { success = true });
    }

    /// <summary>
    /// POST /Bots/Api/SayText — make bot say text.
    /// Body: { guid, text, chatType }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SayText([FromBody] SayTextRequest req)
    {
        await _bridge.SendSayTextAsync(req.Guid, req.Text, req.ChatType);
        return Json(new { success = true });
    }

    // --- Phase 2.5 REST endpoints ---

    /// <summary>
    /// POST /Bots/AcceptQuest — bot accepts a quest.
    /// Body: { guid, questId }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AcceptQuest([FromBody] QuestRequest req)
    {
        await _bridge.SendAcceptQuestAsync(req.Guid, req.QuestId);
        return Json(new { success = true, command = "ACCEPT_QUEST", req.Guid, req.QuestId });
    }

    /// <summary>
    /// POST /Bots/CompleteQuest — bot completes/turns in a quest.
    /// Body: { guid, questId }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CompleteQuest([FromBody] QuestRequest req)
    {
        await _bridge.SendCompleteQuestAsync(req.Guid, req.QuestId);
        return Json(new { success = true, command = "COMPLETE_QUEST", req.Guid, req.QuestId });
    }

    /// <summary>
    /// POST /Bots/AbandonQuest — bot abandons a quest.
    /// Body: { guid, questId }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AbandonQuest([FromBody] QuestRequest req)
    {
        await _bridge.SendAbandonQuestAsync(req.Guid, req.QuestId);
        return Json(new { success = true, command = "ABANDON_QUEST", req.Guid, req.QuestId });
    }

    /// <summary>
    /// POST /Bots/LearnSpell — bot learns a spell.
    /// Body: { guid, spellId }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> LearnSpell([FromBody] LearnSpellRequest req)
    {
        await _bridge.SendLearnSpellAsync(req.Guid, req.SpellId);
        return Json(new { success = true, command = "LEARN_SPELL", req.Guid, req.SpellId });
    }

    /// <summary>
    /// POST /Bots/AttackTarget — bot attacks a creature.
    /// Body: { guid, targetGuid }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AttackTarget([FromBody] TargetRequest req)
    {
        await _bridge.SendAttackTargetAsync(req.Guid, req.TargetGuid);
        return Json(new { success = true, command = "ATTACK_TARGET", req.Guid, req.TargetGuid });
    }

    /// <summary>
    /// POST /Bots/InteractNpc — bot interacts with NPC.
    /// Body: { guid, npcGuid }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> InteractNpc([FromBody] TargetRequest req)
    {
        await _bridge.SendInteractNpcAsync(req.Guid, req.TargetGuid);
        return Json(new { success = true, command = "INTERACT_NPC", req.Guid, req.TargetGuid });
    }
}

// ==================== Request DTOs ====================

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

// --- Phase 2.5 request DTOs ---

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