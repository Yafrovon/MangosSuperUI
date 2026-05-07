using MangosSuperUI.BotLogic.Core;
using MangosSuperUI.BotLogic.Data;
using Microsoft.Extensions.Logging;

namespace MangosSuperUI.BotLogic.Domains;

/// <summary>
/// Economy domain — owns vendoring orchestration, repair, loot processing, and inventory awareness.
///
/// Vendoring sub-phases: TravelingToVendor → Selling → WaitingForSellAck →
///   [Repairing → WaitingForRepairAck] → VendorComplete → Done
///
/// Repair (Session 32): After selling, if the vendor has UNIT_NPC_FLAG_REPAIR (4096),
/// the bot sends REPAIR_AT_NPC to restore durability. Repair is always attempted when
/// available — it's cheap at low levels and the bot is already at the vendor. The vendor
/// selection in ZoneDataLoader prefers repair-capable vendors within 1.5x the distance
/// of the nearest vendor, so bots naturally gravitate toward repair vendors.
///
/// Source of truth: C++ owns real inventory. C# reads freeSlots/totalSlots/copper from
/// enriched STATE messages (every 5s). ShadowInventory and CopperBalance are deprecated.
/// QuestItemProgress still fed by LOOT events for quest objective tracking.
/// </summary>
public class EconomyDomain : IBotDomain
{
    private readonly ZoneDataLoader _zoneData;
    private readonly ILogger _logger;

    public EconomyDomain(ZoneDataLoader zoneData, ILogger<EconomyDomain> logger)
    {
        _zoneData = zoneData;
        _logger = logger;
    }

    public ActivityType[] OwnedActivities => new[]
    {
        ActivityType.Vendoring,
        ActivityType.AuctionHouse,
        ActivityType.TravelingToVendor
    };

    public bool IsOperational => true;

    // ======================== Domain Transitions ========================

    public Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state)
    {
        var weights = new Dictionary<ActivityType, float>();

        // --- Currently vendoring: sub-phase lock or exit offers ---
        if (bot.CurrentActivity.Type == ActivityType.Vendoring)
        {
            var sub = bot.CurrentActivity.SubPhase ?? "";

            // Mid-vendor (traveling, selling, waiting for ack): LOCK IN.
            // Return ONLY Vendoring weight so personality/boredom modifiers in
            // DecisionEngine can't dilute it. The bot MUST finish selling.
            if (sub == "TravelingToVendor" || sub == "Selling" || sub == "WaitingForSellAck"
                || sub == "Repairing" || sub == "WaitingForRepairAck")
            {
                weights[ActivityType.Vendoring] = 100.0f;
                return weights;
            }

            // VendorComplete or Done — offer alternatives to leave
            weights[ActivityType.Vendoring] = 0.3f;
            weights[ActivityType.Questing] = 3.0f;
            weights[ActivityType.Grinding] = 1.0f;
            if (bot.HasUnlearnedSpells)
                weights[ActivityType.TravelingToTrainer] = 2.0f;
            return weights;
        }

        // --- Not currently vendoring — evaluate whether we SHOULD be ---

        // PRECONDITION: Do we actually have items to sell?
        // UsedSlots = TotalSlots - FreeSlots. If the bot has ≤2 items in bags,
        // there's almost certainly nothing sellable (equipped gear doesn't count,
        // and 1-2 items are likely quest items or consumables).
        uint usedSlots = state.TotalSlots - state.FreeSlots;
        if (usedSlots <= 2)
        {
            // Nothing to sell — don't even consider vendoring
            weights[ActivityType.Vendoring] = 0f;
            return weights;
        }

        if (state.FreeSlots == 0)
        {
            // Critical — bags completely full, can't loot or accept quest rewards.
            // Session 26: gate with VendorCooldownUntil to prevent thrashing when
            // bags are full but nothing is sellable (nothing_to_sell=1 from C++).
            if (bot.VendorCooldownUntil.HasValue && DateTime.UtcNow < bot.VendorCooldownUntil.Value)
            {
                weights[ActivityType.Vendoring] = 0.1f;
            }
            else
            {
                weights[ActivityType.Vendoring] = 9.0f;
            }
        }
        else if (state.FreeSlots <= 3)
        {
            // Urgent — almost full
            weights[ActivityType.Vendoring] = 7.0f;
        }
        else if (state.FreeSlots <= 6)
        {
            // Opportunistic — if near a vendor, go sell
            var vendor = _zoneData.GetNearestVendor(state.ZoneId, state.MapId, state.X, state.Y, bot.Level);
            if (vendor != null)
            {
                float dist = Distance2D(state.X, state.Y, vendor.X, vendor.Y);
                weights[ActivityType.Vendoring] = dist < 80f ? 5.0f : 2.0f;
            }
            else
            {
                weights[ActivityType.Vendoring] = 1.5f;
            }
        }
        else
        {
            // Bags have plenty of room — very low weight
            weights[ActivityType.Vendoring] = 0.1f;
        }

        return weights;
    }

    // ======================== OnEnter ========================

    public List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        // --- Pre-flight check: do we actually have items to sell? ---
        uint usedSlots = state.TotalSlots - state.FreeSlots;
        if (usedSlots <= 2)
        {
            _logger.LogInformation(
                "[BOT-ECON] {Name} entered Vendoring but bags nearly empty ({Used}/{Total} used). Skipping.",
                bot.Name, usedSlots, state.TotalSlots);
            bot.CurrentActivity.SubPhase = "Done";
            bot.NextStrategicEval = DateTime.UtcNow;
            return commands;
        }

        var vendor = _zoneData.GetNearestVendor(state.ZoneId, state.MapId, state.X, state.Y, bot.Level);
        if (vendor == null)
        {
            _logger.LogWarning("[BOT-ECON] {Name} needs to vendor but no vendor found in zone {Zone} map {Map}",
                bot.Name, state.ZoneId, state.MapId);
            bot.CurrentActivity.SubPhase = "Done";
            bot.NextStrategicEval = DateTime.UtcNow;
            return commands;
        }

        // Store vendor info on bot identity for the Selling sub-phase
        bot.VendorNpcEntry = vendor.NpcEntry;
        bot.VendorX = vendor.X;
        bot.VendorY = vendor.Y;
        bot.VendorZ = vendor.Z;
        bot.VendorMapId = vendor.MapId;
        bot.VendorTravelStarted = DateTime.UtcNow;

        // Session 32: store whether this vendor can repair
        bot.CurrentActivity.PhaseData["vendor_can_repair"] = vendor.CanRepair;

        float dist = Distance2D(state.X, state.Y, vendor.X, vendor.Y);

        _logger.LogInformation(
            "[BOT-ECON] {Name} traveling to vendor \"{VendorName}\" (entry={Entry}) at ({X:F0},{Y:F0},{Z:F0}), {Dist:F0}yd away. FreeSlots={Free}/{Total}",
            bot.Name, vendor.NpcName, vendor.NpcEntry, vendor.X, vendor.Y, vendor.Z,
            dist, state.FreeSlots, state.TotalSlots);

        bot.CurrentActivity.SubPhase = "TravelingToVendor";
        bot.CurrentActivity.ContextTag = $"vendor:{vendor.NpcName}";

        // MOVE_TO with jitter
        var (jx, jy) = WeightedRoller.Jitter(vendor.X, vendor.Y);
        commands.Add(new BridgeCommand("MOVE_TO", new
        {
            x = jx,
            y = jy,
            z = vendor.Z,
            mapId = vendor.MapId
        }));

        return commands;
    }

    // ======================== OnTick — Sub-Phase State Machine ========================

    public List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();
        var subPhase = bot.CurrentActivity.SubPhase ?? "";

        switch (subPhase)
        {
            case "TravelingToVendor":
                commands.AddRange(ProcessTravelingToVendor(bot, state));
                break;

            case "Selling":
                commands.AddRange(ProcessSelling(bot, state));
                break;

            case "WaitingForSellAck":
                ProcessWaitingForSellAck(bot);
                break;

            case "VendorComplete":
                ProcessVendorComplete(bot, state);
                break;

            case "Repairing":
                commands.AddRange(ProcessRepairing(bot, state));
                break;

            case "WaitingForRepairAck":
                ProcessWaitingForRepairAck(bot);
                break;
            case "Done":
                // Vendoring finished — waiting for strategic eval to switch activity
                break;

            default:
                if (!string.IsNullOrEmpty(subPhase))
                {
                    _logger.LogWarning("[BOT-ECON] {Name} unknown sub-phase '{Phase}', resetting",
                        bot.Name, subPhase);
                }
                bot.CurrentActivity.SubPhase = "Done";
                bot.NextStrategicEval = DateTime.UtcNow;
                break;
        }

        return commands;
    }

    private List<BridgeCommand> ProcessTravelingToVendor(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        if (!bot.VendorNpcEntry.HasValue)
        {
            bot.CurrentActivity.SubPhase = "Done";
            bot.NextStrategicEval = DateTime.UtcNow;
            return commands;
        }

        float dist = Distance2D(state.X, state.Y, bot.VendorX, bot.VendorY);

        // Arrived? (15yd matches C++ GetCreatureListWithEntryInGrid search radius)
        if (dist < 15.0f)
        {
            _logger.LogInformation("[BOT-ECON] {Name} arrived at vendor (entry={Entry}, {Dist:F0}yd)",
                bot.Name, bot.VendorNpcEntry, dist);
            bot.CurrentActivity.SubPhase = "Selling";
            // Fall through — send SELL_ITEMS immediately (sub-phase chain)
            commands.AddRange(ProcessSelling(bot, state));
            return commands;
        }

        // Stuck detection — if traveling > 120s with no arrival, re-send MOVE_TO
        if (bot.VendorTravelStarted.HasValue &&
            (DateTime.UtcNow - bot.VendorTravelStarted.Value).TotalSeconds > 120)
        {
            _logger.LogWarning("[BOT-ECON] {Name} stuck traveling to vendor ({Dist:F0}yd away), re-sending MOVE_TO",
                bot.Name, dist);
            bot.VendorTravelStarted = DateTime.UtcNow;
            var (jx, jy) = WeightedRoller.Jitter(bot.VendorX, bot.VendorY);
            commands.Add(new BridgeCommand("MOVE_TO", new
            {
                x = jx,
                y = jy,
                z = bot.VendorZ,
                mapId = bot.VendorMapId
            }));
        }

        return commands;
    }

    private List<BridgeCommand> ProcessSelling(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        if (!bot.VendorNpcEntry.HasValue)
        {
            _logger.LogWarning("[BOT-ECON] {Name} in Selling sub-phase but no vendor entry stored", bot.Name);
            bot.CurrentActivity.SubPhase = "Done";
            bot.NextStrategicEval = DateTime.UtcNow;
            return commands;
        }

        // Personality-driven keep_quality: cautious bots keep more, greedy bots sell more
        // keep_quality 2 = sell grey+white, keep green+ (default)
        // keep_quality 3 = sell grey+white+green, keep blue+ (aggressive seller)
        int keepQuality = 2;
        if (bot.Personality.Greed > 0.75f)
            keepQuality = 3;

        _logger.LogInformation("[BOT-ECON] {Name} sending SELL_ITEMS to vendor entry={Entry}, keep_quality={Q}",
            bot.Name, bot.VendorNpcEntry.Value, keepQuality);

        commands.Add(new BridgeCommand("SELL_ITEMS", new
        {
            npc_entry = bot.VendorNpcEntry.Value,
            keep_quality = keepQuality
        }));

        bot.CurrentActivity.SubPhase = "WaitingForSellAck";
        bot.VendorTravelStarted = DateTime.UtcNow; // reuse for sell timeout tracking

        return commands;
    }

    private void ProcessWaitingForSellAck(BotIdentity bot)
    {
        // Timeout: if waiting > 30s for SELL_ACK, force complete
        if (bot.VendorTravelStarted.HasValue &&
            (DateTime.UtcNow - bot.VendorTravelStarted.Value).TotalSeconds > 30)
        {
            _logger.LogWarning("[BOT-ECON] {Name} timed out waiting for SELL_ACK, forcing complete", bot.Name);
            bot.CurrentActivity.SubPhase = "VendorComplete";
        }
    }

    private void ProcessVendorComplete(BotIdentity bot, BotStateSnapshot state)
    {
        bot.VendorNpcEntry = null;
        bot.VendorTravelStarted = null;

        _logger.LogInformation("[BOT-ECON] {Name} vendoring complete. FreeSlots={Free}/{Total}, Copper={Copper}",
            bot.Name, state.FreeSlots, state.TotalSlots, state.Copper);

        // Force immediate strategic re-eval → PendingAction return or weighted roll
        bot.NextStrategicEval = DateTime.UtcNow;
        bot.CurrentActivity.SubPhase = "Done";
    }

    // ======================== Repair Sub-Phases (Session 32) ========================

    /// <summary>
    /// Send REPAIR_AT_NPC to the vendor we're already standing at.
    /// Mirrors the ProcessSelling pattern: send command, transition to waiting.
    /// </summary>
    private List<BridgeCommand> ProcessRepairing(BotIdentity bot, BotStateSnapshot state)
    {
        var commands = new List<BridgeCommand>();

        if (!bot.VendorNpcEntry.HasValue)
        {
            _logger.LogWarning("[BOT-ECON] {Name} in Repairing sub-phase but no vendor entry stored", bot.Name);
            bot.CurrentActivity.SubPhase = "VendorComplete";
            return commands;
        }

        _logger.LogInformation("[BOT-ECON] {Name} sending REPAIR_AT_NPC to vendor entry={Entry}",
            bot.Name, bot.VendorNpcEntry.Value);

        commands.Add(new BridgeCommand("REPAIR_AT_NPC", new
        {
            npc_entry = bot.VendorNpcEntry.Value
        }));

        bot.CurrentActivity.SubPhase = "WaitingForRepairAck";
        bot.VendorTravelStarted = DateTime.UtcNow; // reuse for repair timeout tracking

        return commands;
    }

    /// <summary>
    /// Timeout safety for REPAIR_ACK — if waiting > 15s, skip repair and continue.
    /// </summary>
    private void ProcessWaitingForRepairAck(BotIdentity bot)
    {
        if (bot.VendorTravelStarted.HasValue &&
            (DateTime.UtcNow - bot.VendorTravelStarted.Value).TotalSeconds > 15)
        {
            _logger.LogWarning("[BOT-ECON] {Name} timed out waiting for REPAIR_ACK, skipping repair", bot.Name);
            bot.CurrentActivity.SubPhase = "VendorComplete";
        }
    }

    /// <summary>
    /// Check if the bot should repair at this vendor.
    /// Returns true if the vendor can repair (stored in PhaseData during OnEnter).
    /// Always repairs when at a repair vendor — repair is cheap at low levels and
    /// the bot is already there. No point skipping it.
    /// </summary>
    private static bool ShouldRepair(BotIdentity bot)
    {
        if (bot.CurrentActivity.PhaseData.TryGetValue("vendor_can_repair", out var canRepairObj)
            && canRepairObj is bool canRepair)
        {
            return canRepair;
        }
        return false;
    }

    // ======================== OnEvent ========================

    public List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt)
    {
        var commands = new List<BridgeCommand>();

        switch (evt.EventType)
        {
            case "SELL_ACK":
                {
                    var parts = ParsePipeDelimited(evt.Data);
                    int.TryParse(parts.GetValueOrDefault("sold", "0"), out int sold);
                    int.TryParse(parts.GetValueOrDefault("copper_earned", "0"), out int copperEarned);
                    int.TryParse(parts.GetValueOrDefault("free_slots", "0"), out int freeSlots);
                    int.TryParse(parts.GetValueOrDefault("copper_total", "0"), out int copperTotal);

                    _logger.LogInformation(
                        "[BOT-ECON] {Name} SELL_ACK: sold {Sold} items for {Copper}c. Now {Free} free slots, {Total}c total.",
                        bot.Name, sold, copperEarned, freeSlots, copperTotal);

                    // If nothing was sold, skip straight to Done — no point lingering.
                    // Session 26: also set VendorCooldownUntil to prevent the FreeSlots==0
                    // critical trigger from immediately re-entering vendoring.
                    if (sold == 0)
                    {
                        _logger.LogInformation(
                            "[BOT-ECON] {Name} sold 0 items — nothing sellable, fast-exiting vendoring. Cooldown 10min.",
                            bot.Name);

                        // Session 32: still repair even if nothing was sold — bot might have
                        // damaged gear from deaths but nothing to vendor.
                        if (ShouldRepair(bot))
                        {
                            bot.CurrentActivity.SubPhase = "Repairing";
                        }
                        else
                        {
                            bot.CurrentActivity.SubPhase = "Done";
                            bot.NextStrategicEval = DateTime.UtcNow;
                        }
                        bot.VendorCooldownUntil = DateTime.UtcNow.AddMinutes(10);
                    }
                    else
                    {
                        // Session 32: repair after selling if vendor supports it
                        if (ShouldRepair(bot))
                        {
                            bot.CurrentActivity.SubPhase = "Repairing";
                        }
                        else
                        {
                            bot.CurrentActivity.SubPhase = "VendorComplete";
                        }
                    }
                    break;
                }

            case "SELL_FAIL":
                {
                    var parts = ParsePipeDelimited(evt.Data);
                    var reason = parts.GetValueOrDefault("reason", "unknown");

                    _logger.LogWarning("[BOT-ECON] {Name} SELL_FAIL: {Reason}. Aborting vendor attempt.", bot.Name, reason);
                    bot.CurrentActivity.SubPhase = "Done";
                    bot.NextStrategicEval = DateTime.UtcNow;
                    break;
                }

            case "LOOT":
                if (!string.IsNullOrEmpty(evt.Data))
                    ProcessLootEvent(bot, evt.Data);
                break;

            case "EQUIP":
                _logger.LogInformation("[BOT-ECON] {Name} equipped gear: {Data}", bot.Name, evt.Data);
                break;

            case "BAG_EQUIP":
                _logger.LogInformation("[BOT-ECON] {Name} equipped bags: {Data}", bot.Name, evt.Data);
                break;

            case "REPAIR_ACK":
                {
                    var parts = ParsePipeDelimited(evt.Data);
                    int.TryParse(parts.GetValueOrDefault("cost", "0"), out int repairCost);
                    int.TryParse(parts.GetValueOrDefault("copper_total", "0"), out int copperAfter);

                    _logger.LogInformation(
                        "[BOT-ECON] {Name} REPAIR_ACK: cost {Cost}c, {Total}c remaining.",
                        bot.Name, repairCost, copperAfter);

                    bot.CurrentActivity.SubPhase = "VendorComplete";
                    break;
                }

            case "REPAIR_FAIL":
                {
                    var parts = ParsePipeDelimited(evt.Data);
                    var reason = parts.GetValueOrDefault("reason", "unknown");

                    _logger.LogWarning("[BOT-ECON] {Name} REPAIR_FAIL: {Reason}. Skipping repair.",
                        bot.Name, reason);

                    // Not fatal — just skip repair and continue to VendorComplete
                    bot.CurrentActivity.SubPhase = "VendorComplete";
                    break;
                }
        }

        return commands;
    }

    // ======================== Loot Processing (Always-On) ========================

    /// <summary>
    /// Called by BotBrainService on every KILL event, independent of current activity domain.
    /// DEPRECATED — shadow loot is superseded by real C++ loot. Kept as fallback.
    /// </summary>
    public void ProcessKillLoot(BotIdentity bot, int creatureEntry, int creatureLevel)
    {
        int baseDrop = creatureLevel * WeightedRoller.RangeInt(3, 6);
        float variance = WeightedRoller.Range(0.7f, 1.3f);
        int copperDrop = (int)(baseDrop * variance);
        bot.CopperBalance += copperDrop;

        if (WeightedRoller.Check(0.4f))
        {
            bot.ShadowInventory.Add(new ShadowInventoryItem
            {
                ItemId = 0,
                Count = 1,
                Quality = 0,
                SellPrice = creatureLevel * WeightedRoller.RangeInt(1, 3),
                Source = "loot",
                SourceCreatureEntry = creatureEntry
            });
        }
    }

    /// <summary>
    /// Called by BotBrainService on LOOT events from C++ DoAutoLoot.
    /// Updates QuestItemProgress from real loot data. Shadow inventory kept for backward compat.
    /// </summary>
    public void ProcessLootEvent(BotIdentity bot, string lootData)
    {
        foreach (var segment in lootData.Split('|'))
        {
            if (segment.StartsWith("gold="))
            {
                if (int.TryParse(segment[5..], out int gold))
                    bot.CopperBalance += gold;
            }
            else if (segment.StartsWith("items="))
            {
                foreach (var (itemId, count) in ParseLootItems(lootData))
                {
                    // Update QuestItemProgress for quest objective tracking.
                    // Always add/increment — the old ContainsKey guard meant keys
                    // were never seeded and item progress was never tracked.
                    if (bot.QuestItemProgress.ContainsKey(itemId))
                        bot.QuestItemProgress[itemId] += count;
                    else
                        bot.QuestItemProgress[itemId] = count;

                    // Shadow inventory (deprecated)
                    var existing = bot.ShadowInventory.FirstOrDefault(i => i.ItemId == itemId && i.Source == "loot");
                    if (existing != null)
                        existing.Count += count;
                    else
                    {
                        bot.ShadowInventory.Add(new ShadowInventoryItem
                        {
                            ItemId = itemId,
                            Count = count,
                            Quality = 0,
                            SellPrice = 0,
                            Source = "loot"
                        });
                    }
                }
            }
        }
    }

    /// <summary>
    /// Parse "gold=47|items=2589:1,3299:2" → list of (itemId, count).
    /// </summary>
    public List<(int itemId, int count)> ParseLootItems(string lootData)
    {
        var result = new List<(int, int)>();
        foreach (var segment in lootData.Split('|'))
        {
            if (!segment.StartsWith("items=")) continue;
            var itemsStr = segment[6..];
            foreach (var entry in itemsStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(':');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int itemId) &&
                    int.TryParse(parts[1], out int count))
                {
                    result.Add((itemId, count));
                }
            }
        }
        return result;
    }

    // ======================== Helpers ========================

    private static Dictionary<string, string> ParsePipeDelimited(string? data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(data)) return result;
        foreach (var segment in data.Split('|'))
        {
            var eq = segment.IndexOf('=');
            if (eq > 0)
                result[segment[..eq].Trim()] = segment[(eq + 1)..].Trim();
        }
        return result;
    }

    private static float Distance2D(float x1, float y1, float x2, float y2)
    {
        float dx = x1 - x2, dy = y1 - y2;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float Lerp(float min, float max, float t) => min + (max - min) * t;
}