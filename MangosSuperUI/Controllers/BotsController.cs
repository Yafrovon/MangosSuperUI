using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using MangosSuperUI.Models;
using Dapper;

namespace MangosSuperUI.Controllers;

public class BotsController : Controller
{
    private readonly BotBridgeService _bridge;
    private readonly BotBrainService _brain;
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;

    public BotsController(BotBridgeService bridge, BotBrainService brain, ConnectionFactory db, DbcService dbc)
    {
        _bridge = bridge;
        _brain = brain;
        _db = db;
        _dbc = dbc;
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

    [HttpPost]
    public async Task<IActionResult> TakeFlight([FromBody] TakeFlightRequest req)
    {
        await _bridge.SendTakeFlightAsync(req.Guid, req.SourceNode, req.DestNode);
        return Json(new { success = true, command = "TAKE_FLIGHT", req.Guid });
    }

    // ==================== BotBrain API ====================

    [HttpPost]
    public IActionResult ToggleBrain(bool enabled)
    {
        _brain.BrainEnabled = enabled;
        return Json(new { success = true, enabled = _brain.BrainEnabled });
    }

    [HttpGet("Bots/BrainState/{guid}")]
    public IActionResult BrainState(int guid)
    {
        var summary = _brain.GetBotBrainSummary(guid);
        if (summary == null)
            return Json(new { guid, error = "No brain data for this bot" });
        return Json(summary);
    }

    [HttpGet]
    public IActionResult BrainStatus()
    {
        return Json(new
        {
            enabled = _brain.BrainEnabled,
            activeBots = _brain.ActiveBotCount,
            groupingMode = (int)_brain.GroupManager.Mode,
            groupingModeName = _brain.GroupManager.Mode.ToString(),
            groups = _brain.GroupManager.GetAllGroups().Select(g => new
            {
                groupId = g.GroupId,
                leaderGuid = g.LeaderGuid,
                memberGuids = g.MemberGuids,
                size = g.Size,
                formedAt = g.FormedAt
            }),
            bots = _brain.AllBots.Values.Select(b => new
            {
                guid = b.Guid,
                name = b.Name,
                level = b.Level,
                activity = b.CurrentActivity.Type.ToString(),
                quirks = b.Personality.Quirks.Select(q => q.Name),
                groupId = b.GroupId,
                isGroupLeader = b.IsGroupLeader
            })
        });
    }

    // ==================== Grouping API (Session 31) ====================

    [HttpPost]
    public async Task<IActionResult> SetGroupingMode([FromBody] GroupingModeRequest req)
    {
        if (!Enum.IsDefined(typeof(MangosSuperUI.BotLogic.Core.GroupingMode), req.Mode))
            return Json(new { success = false, error = "Invalid mode. Use 0=Off, 1=Sticky, 2=Opportunistic." });

        var mode = (MangosSuperUI.BotLogic.Core.GroupingMode)req.Mode;
        await _brain.SetGroupingModeAsync(mode);
        return Json(new { success = true, mode = req.Mode, modeName = mode.ToString() });
    }

    [HttpPost]
    public async Task<IActionResult> AutoFormGroups()
    {
        var formed = await _brain.AutoFormGroupsAsync();
        return Json(new
        {
            success = true,
            groupsFormed = formed.Count,
            groups = formed.Select(g => new
            {
                groupId = g.GroupId,
                leaderGuid = g.LeaderGuid,
                memberGuids = g.MemberGuids
            })
        });
    }

    [HttpPost]
    public async Task<IActionResult> FormGroup([FromBody] FormGroupRequest req)
    {
        if (req.LeaderGuid <= 0 || req.FollowerGuids == null || req.FollowerGuids.Length == 0)
            return Json(new { success = false, error = "Need leaderGuid + at least 1 followerGuid" });

        var group = await _brain.FormGroupAsync(req.LeaderGuid, req.FollowerGuids);
        if (group == null)
            return Json(new { success = false, error = "Formation failed — check mode is not Off and bots are not already grouped" });

        return Json(new
        {
            success = true,
            groupId = group.GroupId,
            leaderGuid = group.LeaderGuid,
            memberGuids = group.MemberGuids
        });
    }

    [HttpPost]
    public async Task<IActionResult> DisbandGroup([FromBody] DisbandGroupRequest req)
    {
        await _brain.DisbandGroupAsync(req.GroupId);
        return Json(new { success = true, groupId = req.GroupId });
    }

    // ==================== Bot Quest Progress ====================

    /// <summary>
    /// GET /Bots/QuestStatus?guid=8
    /// Returns all quest statuses for a bot from character_queststatus,
    /// joined with quest_template for titles and chain data.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> QuestStatus(int guid)
    {
        try
        {
            using var charConn = _db.Characters();
            using var mangosConn = _db.Mangos();

            var rows = await charConn.QueryAsync<dynamic>(@"
                SELECT quest, status, rewarded,
                       mob_count1, mob_count2, mob_count3, mob_count4,
                       item_count1, item_count2, item_count3, item_count4
                FROM character_queststatus
                WHERE guid = @Guid",
                new { Guid = guid });

            var questIds = rows.Select(r => (int)r.quest).Distinct().ToList();
            if (questIds.Count == 0)
                return Json(new { guid, quests = Array.Empty<object>() });

            var templates = await mangosConn.QueryAsync<dynamic>(@"
                SELECT entry, Title, QuestLevel, MinLevel, ZoneOrSort,
                       PrevQuestId, NextQuestId, NextQuestInChain, ExclusiveGroup,
                       ReqCreatureOrGOId1, ReqCreatureOrGOId2,
                       ReqCreatureOrGOCount1, ReqCreatureOrGOCount2,
                       ReqItemId1, ReqItemId2, ReqItemId3, ReqItemId4,
                       ReqItemCount1, ReqItemCount2, ReqItemCount3, ReqItemCount4
                FROM quest_template
                WHERE entry IN @Ids AND patch = (SELECT MAX(patch) FROM quest_template qt2 WHERE qt2.entry = quest_template.entry)",
                new { Ids = questIds });

            var tplMap = templates.ToDictionary(t => (int)t.entry);

            // Also get giver/turnin NPC names
            var giverRows = await mangosConn.QueryAsync<dynamic>(@"
                SELECT cqr.quest, ct.name AS giver_name, ct.entry AS giver_entry
                FROM creature_questrelation cqr
                JOIN creature_template ct ON ct.entry = cqr.id AND ct.patch = 0
                WHERE cqr.quest IN @Ids",
                new { Ids = questIds });
            var giverMap = giverRows
                .GroupBy(r => (int)r.quest)
                .ToDictionary(g => g.Key, g => g.First());

            var turnInRows = await mangosConn.QueryAsync<dynamic>(@"
                SELECT cir.quest, ct.name AS turnin_name, ct.entry AS turnin_entry
                FROM creature_involvedrelation cir
                JOIN creature_template ct ON ct.entry = cir.id AND ct.patch = 0
                WHERE cir.quest IN @Ids",
                new { Ids = questIds });
            var turnInMap = turnInRows
                .GroupBy(r => (int)r.quest)
                .ToDictionary(g => g.Key, g => g.First());

            var result = rows.Select(r =>
            {
                int qid = (int)r.quest;
                tplMap.TryGetValue(qid, out var tpl);
                giverMap.TryGetValue(qid, out var giver);
                turnInMap.TryGetValue(qid, out var turnIn);

                return new
                {
                    questId = qid,
                    status = (int)r.status,
                    rewarded = (int)r.rewarded,
                    title = (string?)(tpl?.Title) ?? $"Quest #{qid}",
                    questLevel = (int?)(tpl?.QuestLevel) ?? 0,
                    minLevel = (int?)(tpl?.MinLevel) ?? 0,
                    zone = (int?)(tpl?.ZoneOrSort) ?? 0,
                    prevQuestId = (int?)(tpl?.PrevQuestId) ?? 0,
                    exclusiveGroup = (int?)(tpl?.ExclusiveGroup) ?? 0,
                    mobCounts = new[] { (int)r.mob_count1, (int)r.mob_count2, (int)r.mob_count3, (int)r.mob_count4 },
                    mobRequired = tpl != null ? new[] {
                        (int)(tpl.ReqCreatureOrGOCount1 ?? 0), (int)(tpl.ReqCreatureOrGOCount2 ?? 0), 0, 0
                    } : new[] { 0, 0, 0, 0 },
                    itemCounts = new[] { (int)r.item_count1, (int)r.item_count2, (int)r.item_count3, (int)r.item_count4 },
                    itemRequired = tpl != null ? new[] {
                        (int)(tpl.ReqItemCount1 ?? 0), (int)(tpl.ReqItemCount2 ?? 0),
                        (int)(tpl.ReqItemCount3 ?? 0), (int)(tpl.ReqItemCount4 ?? 0)
                    } : new[] { 0, 0, 0, 0 },
                    giverName = (string?)(giver?.giver_name),
                    turnInName = (string?)(turnIn?.turnin_name)
                };
            })
            .OrderByDescending(q => q.rewarded)
            .ThenByDescending(q => q.status)
            .ThenBy(q => q.questLevel)
            .ToList();

            return Json(new { guid, quests = result });
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ==================== Real Inventory ====================

    /// <summary>
    /// GET /Bots/Inventory?guid=25
    /// Queries real character_inventory + item_template for a bot's actual bag contents,
    /// equipped gear, and gold. No shadow economy — all real server data.
    ///
    /// VMaNGOS character_inventory layout:
    ///   bag=0, slot 0-18  → equipped gear (head, neck, shoulders, ... mainhand, offhand, ranged, tabard)
    ///   bag=0, slot 19-22 → equipped bag slots
    ///   bag=0, slot 23-38 → backpack (16 slots)
    ///   bag=N (item guid of equipped bag) → items inside that bag
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Inventory(int guid)
    {
        try
        {
            using var charConn = _db.Characters();
            using var mangosConn = _db.Mangos();

            // 1. Gold from characters table
            var gold = await charConn.ExecuteScalarAsync<long?>(
                "SELECT money FROM characters WHERE guid = @Guid", new { Guid = guid }) ?? 0;

            // 2. All items from character_inventory + item_instance for stack count
            var invRows = (await charConn.QueryAsync<dynamic>(@"
                SELECT ci.bag, ci.slot, ci.item_guid AS itemGuid, ci.item_id AS itemEntry,
                       COALESCE(ii.count, 1) AS stackCount
                FROM character_inventory ci
                LEFT JOIN item_instance ii ON ii.guid = ci.item_guid
                WHERE ci.guid = @Guid
                ORDER BY ci.bag, ci.slot",
                new { Guid = guid })).ToList();

            if (invRows.Count == 0)
                return Json(new { gold, equipped = Array.Empty<object>(), bags = Array.Empty<object>(), backpack = Array.Empty<object>() });

            // 3. Collect all unique item entries to batch-query item_template
            var entries = invRows.Select(r => (int)r.itemEntry).Distinct().ToList();

            var itemDetails = new Dictionary<int, dynamic>();
            if (entries.Count > 0)
            {
                var items = await mangosConn.QueryAsync<dynamic>(@"
                    SELECT entry, name, quality, class, subclass, inventory_type AS inventoryType,
                           item_level AS itemLevel, required_level AS requiredLevel,
                           sell_price AS sellPrice, display_id AS displayId,
                           armor, dmg_min1 AS dmgMin1, dmg_max1 AS dmgMax1, delay,
                           container_slots AS containerSlots, max_count AS maxStack
                    FROM item_template
                    WHERE entry IN @Entries AND patch = 0",
                    new { Entries = entries });

                foreach (var item in items)
                    itemDetails[(int)item.entry] = item;
            }

            // 4. Classify rows
            var equipped = new List<object>();   // slots 0-18
            var bagSlots = new List<object>();   // slots 19-22 (the bags themselves)
            var backpack = new List<object>();    // slots 23-38
            var extraBags = new Dictionary<uint, List<object>>(); // bag itemGuid → items

            foreach (var row in invRows)
            {
                uint bag = (uint)row.bag;
                int slot = (int)(byte)row.slot;
                int entry = (int)row.itemEntry;
                uint itemGuid = (uint)row.itemGuid;
                int stackCount = (int)(row.stackCount ?? 1);

                itemDetails.TryGetValue(entry, out var detail);

                var itemObj = new
                {
                    slot,
                    entry,
                    itemGuid,
                    name = (string?)(detail?.name) ?? $"Item #{entry}",
                    quality = (int?)(detail?.quality) ?? 0,
                    itemClass = (int?)(detail?.@class) ?? 0,
                    subclass = (int?)(detail?.subclass) ?? 0,
                    inventoryType = (int?)(detail?.inventoryType) ?? 0,
                    itemLevel = (int?)(detail?.itemLevel) ?? 0,
                    sellPrice = (int?)(detail?.sellPrice) ?? 0,
                    armor = (int?)(detail?.armor) ?? 0,
                    containerSlots = (int?)(detail?.containerSlots) ?? 0,
                    displayId = (uint?)(detail?.displayId) ?? 0,
                    stackCount,
                    maxStack = (int?)(detail?.maxStack) ?? 1
                };

                if (bag == 0)
                {
                    if (slot <= 18)
                        equipped.Add(itemObj);
                    else if (slot <= 22)
                        bagSlots.Add(itemObj);
                    else
                        backpack.Add(itemObj);
                }
                else
                {
                    if (!extraBags.ContainsKey(bag))
                        extraBags[bag] = new List<object>();
                    extraBags[bag].Add(itemObj);
                }
            }

            // 5. Build bag summary (bag slot → contents)
            var bagSummary = bagSlots.Select(b =>
            {
                var bd = (dynamic)b;
                uint bguid = (uint)bd.itemGuid;
                var contents = extraBags.ContainsKey(bguid) ? extraBags[bguid] : new List<object>();
                return new
                {
                    bag = b,
                    contents,
                    capacity = (int)bd.containerSlots,
                    used = contents.Count
                };
            }).ToList();

            // Total sell value of all bag items (not equipped) — price × stack count
            var totalSellValue = backpack.Cast<dynamic>().Sum(i => (int)i.sellPrice * (int)i.stackCount);
            foreach (var bg in extraBags.Values)
                totalSellValue += bg.Cast<dynamic>().Sum(i => (int)i.sellPrice * (int)i.stackCount);

            var backpackUsed = backpack.Count;
            var extraUsed = extraBags.Values.Sum(b => b.Count);
            var extraCapacity = bagSlots.Cast<dynamic>().Sum(b => (int)b.containerSlots);

            // Build icon map: displayId → icon path (same pattern as ItemsController)
            var iconMap = new Dictionary<uint, string>();
            foreach (var detail in itemDetails.Values)
            {
                uint did = (uint)(detail.displayId ?? 0);
                if (did > 0 && !iconMap.ContainsKey(did))
                    iconMap[did] = _dbc.GetItemIconPath(did);
            }

            return Json(new
            {
                gold,
                equipped,
                backpack,
                bags = bagSummary,
                icons = iconMap,
                totalItems = backpackUsed + extraUsed,
                totalSlots = 16 + extraCapacity,
                freeSlots = (16 + extraCapacity) - (backpackUsed + extraUsed),
                totalSellValue
            });
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }
}

// ==================== Request DTOs ====================

public class TakeFlightRequest
{
    public int Guid { get; set; }
    public int SourceNode { get; set; }
    public int DestNode { get; set; }
}

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

// Session 31 — Grouping DTOs

public class GroupingModeRequest
{
    public int Mode { get; set; }
}

public class FormGroupRequest
{
    public int LeaderGuid { get; set; }
    public int[] FollowerGuids { get; set; } = Array.Empty<int>();
}

public class DisbandGroupRequest
{
    public int GroupId { get; set; }
}