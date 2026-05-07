namespace MangosSuperUI.BotLogic.Core;

/// <summary>
/// The single object that represents everything about one bot.
/// Domains never store per-bot state — they read/write BotIdentity.
/// </summary>
public class BotIdentity
{
    // --- Immutable (set at spawn/load) ---
    public int Guid { get; set; }
    public string Name { get; set; } = "";
    public int Race { get; set; }
    public int ClassId { get; set; }
    public string Faction { get; set; } = "";
    public BotPersonality Personality { get; set; } = new();

    // --- Mutable (updated by events/state messages) ---
    public int Level { get; set; } = 1;
    public long XP { get; set; }
    public long XPToNextLevel { get; set; }
    public long CopperBalance { get; set; }

    public float? CorpseX { get; set; }
    public float? CorpseY { get; set; }
    public float? CorpseZ { get; set; }
    public int? CorpseMapId { get; set; }


    // --- Activity tracking ---
    public ActivityState CurrentActivity { get; set; } = new();
    public ActivityState? PreviousActivity { get; set; }
    public DateTime NextDecisionTick { get; set; } = DateTime.UtcNow;

    // --- Strategic tick (DecisionEngine split-cadence) ---
    /// <summary>
    /// When the next strategic re-evaluation should fire. Tactical ticks (sub-phase
    /// driving) continue on the normal 10-30s cadence. Strategic evals (should I
    /// switch activities?) fire on a 3-10 minute cadence, personality-modulated.
    /// </summary>
    public DateTime NextStrategicEval { get; set; } = DateTime.UtcNow;

    // --- Quest tracking ---
    public int? ActiveQuestId { get; set; }
    public string? ActiveQuestPhase { get; set; }
    public float CurrentQuestProgress { get; set; }
    public HashSet<int> CompletedQuestIds { get; set; } = new();

    /// <summary>
    /// Quests hydrated from character_queststatus on reconnect that are still
    /// in the bot's quest log (accepted but not yet rewarded). QuestingDomain.OnEnter
    /// consumes this to rebuild the ActiveQuestEntry batch, then clears it.
    /// Carries DB progress columns so we can verify whether objectives are truly done.
    /// </summary>
    public List<HydratedQuest>? HydratedActiveQuests { get; set; }
    // --- Quest objective progress (per-slot tracking for active quest) ---
    public Dictionary<int, int> QuestObjectiveProgress { get; set; } = new(); // slot → current count
    public Dictionary<int, int> QuestItemProgress { get; set; } = new();      // itemId → current count

    // --- Quest deferral (quests the bot tried and failed/died doing) ---
    /// <summary>
    /// Quests the bot has shelved because it died, got stuck, or PATH_UNSAFE'd.
    /// Key = questId, Value = deferral info with optional level gate.
    /// Time-gated deferrals (death, stuck) expire after 10-30 min.
    /// Level-gated deferrals (PATH_UNSAFE) expire when bot reaches the safe level.
    /// </summary>
    public Dictionary<int, QuestDeferral> DeferredQuestIds { get; set; } = new();

    /// <summary>
    /// Session 33: Cumulative deferral count per quest (survives across deferral expiries).
    /// When a quest gets deferred 3+ times AND it's not part of a chain AND has no
    /// item rewards, the bot will abandon it to free up the quest log slot.
    /// Key = questId, Value = total times deferred this session.
    /// Cleared on level-up (fresh start, new capabilities).
    /// </summary>
    public Dictionary<int, int> QuestDeferralCounts { get; set; } = new();

    // --- Path blacklist (destinations rejected by C++ IsPathSafe) ---
    /// <summary>
    /// Destinations that C++ PATH_UNSAFE rejected because the mmap path crossed
    /// through high-level creature spawns. Key = (destX, destY) rounded to int
    /// for coarse matching. Value = dangerLevel the path hit.
    /// Expires ONLY when bot reaches dangerLevel - 3 (can handle those creatures).
    /// No time expiry — a level 1 bot should never retry a path through level 6 wolves
    /// every 30 minutes. It should wait until it's strong enough.
    /// </summary>
    public Dictionary<(int X, int Y), int> PathBlacklist { get; set; } = new();

    /// <summary>
    /// Count of PATH_UNSAFE events received since last quest pick or activity change.
    /// Used to detect "everything is blacklisted" and fall back to grinding.
    /// </summary>
    public int PathUnsafeCountSinceLastPick { get; set; }

    // --- Death tracking (for reactive quest shelving) ---
    /// <summary>Number of deaths since the bot last changed quests or activities.</summary>
    public int DeathsSinceQuestStart { get; set; }
    /// <summary>Where the bot last died (for "death zone" detection).</summary>
    public (float X, float Y, int Map) LastDeathLocation { get; set; }
    /// <summary>UTC time of last death.</summary>
    public DateTime LastDeathTime { get; set; }

    // --- Spell/training tracking ---
    public HashSet<int> KnownSpellIds { get; set; } = new();
    public bool HasUnlearnedSpells { get; set; }
    public int TicksSinceLastTrained { get; set; }

    // --- Shadow inventory (in-memory, flushed to DB periodically) ---
    public List<ShadowInventoryItem> ShadowInventory { get; set; } = new();

    // --- PendingAction (cross-domain return stack) ---
    /// <summary>
    /// Saved domain state for cross-domain interruptions (e.g., bags-full during quest turn-in).
    /// DecisionEngine checks this before strategic rolls and forces return to the saved domain.
    /// Set by QuestingDomain on QUEST_INTERACT_FAIL with bags-full, cleared by DecisionEngine on return.
    /// </summary>
    public PendingAction? PendingAction { get; set; }

    // --- EconomyDomain transient vendoring state (not persisted) ---
    public int? VendorNpcEntry { get; set; }
    public float VendorX { get; set; }
    public float VendorY { get; set; }
    public float VendorZ { get; set; }
    public int VendorMapId { get; set; }
    public DateTime? VendorTravelStarted { get; set; }

    // --- Relationships (future) ---
    public HashSet<int> MetPlayerGuids { get; set; } = new();

    // --- Group membership (Session 31 → Session 35 "Band of Brothers" rework) ---
    // Stamped by GroupManager.EnrichBotIdentity.
    //
    // Every grouped bot is a fully autonomous quester. The "pace-setter" is the one
    // who decides WHICH quests the group works on. All members accept, grind, and
    // turn in independently. The group synchronizes at two gates:
    //   1. DoingObjectives → TravelingToTurnIn: wait until ALL members finished objectives
    //   2. BatchComplete → PickingQuests: wait until ALL members turned in current batch
    // Members can still vendor/train/eat independently — the group waits for them.

    /// <summary>Group ID this bot belongs to, or null if solo.</summary>
    public int? GroupId { get; set; }
    /// <summary>Pace-setter's GUID. Equals this bot's Guid if pace-setter. Null if solo.</summary>
    public int? GroupLeaderGuid { get; set; }
    /// <summary>True if in a group AND is the pace-setter (picks quests for the group).</summary>
    public bool IsGroupLeader => GroupId.HasValue && GroupLeaderGuid == Guid;
    /// <summary>True if in a group AND is NOT the pace-setter.</summary>
    public bool IsGroupFollower => GroupId.HasValue && GroupLeaderGuid.HasValue && GroupLeaderGuid != Guid;
    /// <summary>True if in any group (leader or member).</summary>
    public bool IsGrouped => GroupId.HasValue;

    /// <summary>
    /// Lowest level among all group members (including pace-setter).
    /// Pace-setter uses this for quest selection so everyone can accept.
    /// Stamped by BotBrainService during group coordination injection. Null if solo.
    /// </summary>
    public int? GroupMinMemberLevel { get; set; }

    /// <summary>
    /// Session 35: True if ALL group members have completed their current quest
    /// objectives (kill/collect). Every member checks this — when false, the bot
    /// stays in DoingObjectives grinding for XP while groupmates catch up.
    /// Stamped by BotBrainService every tick for ALL grouped bots.
    /// </summary>
    public bool GroupAllObjectivesDone { get; set; } = true;

    /// <summary>
    /// Session 35: True if ALL group members have turned in all completed quests
    /// in their current batch. Every member checks this — when false, the bot
    /// waits in BatchComplete/PickingQuests. This is the core "same pace" gate.
    /// Stamped by BotBrainService every tick for ALL grouped bots.
    /// </summary>
    public bool GroupAllMembersTurnedIn { get; set; } = true;

    /// <summary>
    /// Session 35: True if ALL group members are in the Questing activity.
    /// When false, at least one member is vendoring/training/eating — the group
    /// keeps doing objectives or waits at PickingQuests rather than advancing.
    /// Stamped by BotBrainService every tick for ALL grouped bots.
    /// </summary>
    public bool GroupAllMembersQuesting { get; set; } = true;

    // --- Computed helpers ---
    public float XPPercent => XPToNextLevel > 0 ? XP / (float)XPToNextLevel : 0f;
    public bool IsNearLevelUp => XPPercent > 0.85f;
    public string RaceClassName => $"{(WowRace)Race} {(WowClass)ClassId}";

    public DateTime? VendorCooldownUntil { get; set; }

    public StuckDetector StuckDetector { get; set; }
    /// <summary>
    /// Clear expired deferrals. Called during quest selection.
    /// Time-gated deferrals expire by clock. Level-gated deferrals expire
    /// when bot reaches the required level (dangerLevel - SAFETY_MARGIN).
    /// </summary>
    public void PruneExpiredDeferrals()
    {
        var now = DateTime.UtcNow;
        var expired = DeferredQuestIds
            .Where(kv => kv.Value.IsExpired(now, Level))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in expired)
            DeferredQuestIds.Remove(id);
    }

    /// <summary>
    /// Defer a quest for a duration (time-gated). Used for death/stuck deferrals.
    /// Session 33: Also increments the cumulative deferral count for frustration tracking.
    /// </summary>
    public void DeferQuest(int questId, TimeSpan duration)
    {
        DeferredQuestIds[questId] = QuestDeferral.TimeBased(DateTime.UtcNow + duration);
        QuestDeferralCounts.TryGetValue(questId, out int count);
        QuestDeferralCounts[questId] = count + 1;
    }

    /// <summary>
    /// Defer a quest until bot reaches a safe level (level-gated). Used for PATH_UNSAFE.
    /// Bot won't retry until it's within SAFETY_MARGIN levels of the danger.
    /// </summary>
    public void DeferQuestUntilLevel(int questId, int dangerLevel, int safetyMargin = 3)
    {
        int requiredLevel = Math.Max(1, dangerLevel - safetyMargin);
        DeferredQuestIds[questId] = QuestDeferral.LevelBased(requiredLevel);
    }

    /// <summary>
    /// Prune deferrals on level-up. Only clears deferrals the bot has outleveled.
    /// Time-based deferrals are left alone (they expire by clock).
    /// Level-based deferrals clear when bot reaches the required level.
    /// Session 33: Also clears deferral counts — fresh level = fresh start.
    /// </summary>
    public void ClearAllDeferrals()
    {
        PruneExpiredDeferrals();
        QuestDeferralCounts.Clear();
    }

    /// <summary>
    /// Blacklist a destination that C++ rejected via PATH_UNSAFE.
    /// Rounds coords to int for coarse matching (~1yd granularity).
    /// Persists until bot can handle the danger level (dangerLevel - 3).
    /// </summary>
    public void AddPathBlacklist(float destX, float destY, int dangerLevel)
    {
        var key = ((int)MathF.Round(destX), (int)MathF.Round(destY));
        // Keep the higher danger level if already blacklisted
        if (PathBlacklist.TryGetValue(key, out int existing) && existing >= dangerLevel)
            return;
        PathBlacklist[key] = dangerLevel;
        PathUnsafeCountSinceLastPick++;
    }

    /// <summary>
    /// Check if a coordinate is blacklisted. Uses ±20yd tolerance to catch
    /// jittered MOVE_TO variants of the same destination.
    /// Only clears when bot has leveled past dangerLevel - 3.
    /// </summary>
    public bool IsPathBlacklisted(float x, float y)
    {
        int ix = (int)MathF.Round(x);
        int iy = (int)MathF.Round(y);

        foreach (var kvp in PathBlacklist)
        {
            // Bot has leveled past the danger — can handle it now
            if (Level >= kvp.Value - 3) continue;

            int dx = Math.Abs(kvp.Key.X - ix);
            int dy = Math.Abs(kvp.Key.Y - iy);
            if (dx <= 20 && dy <= 20) return true;
        }

        return false;
    }

    /// <summary>
    /// Remove level-obsolete blacklist entries. Called during quest selection.
    /// Entry clears when botLevel >= dangerLevel - 3.
    /// </summary>
    public void PrunePathBlacklist()
    {
        var expired = PathBlacklist
            .Where(kvp => Level >= kvp.Value - 3)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expired)
            PathBlacklist.Remove(key);
    }

    /// <summary>
    /// Prune path blacklist on level-up. Does NOT clear everything — only removes
    /// entries the bot has now outleveled. A level 1→2 bot shouldn't clear the
    /// level 6 wolf blacklist. That clears at level 3 (dangerLevel 6 - 3).
    /// </summary>
    public void ClearPathBlacklist()
    {
        PrunePathBlacklist();
        PathUnsafeCountSinceLastPick = 0;
    }

    /// <summary>
    /// Record a death for quest-shelving logic.
    /// </summary>
    public void RecordDeath(float x, float y, int map)
    {
        DeathsSinceQuestStart++;
        LastDeathLocation = (x, y, map);
        LastDeathTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Reset death counter (called when bot picks a new quest or changes activity).
    /// </summary>
    public void ResetDeathCounter()
    {
        DeathsSinceQuestStart = 0;
    }

    /// <summary>
    /// Derive faction from race ID.
    /// </summary>
    public static string FactionForRace(int race) => race switch
    {
        2 or 5 or 6 or 8 => "Horde",
        1 or 3 or 4 or 7 => "Alliance",
        _ => "Unknown"
    };
}

public class ShadowInventoryItem
{
    public int ItemId { get; set; }
    public int Count { get; set; }
    public int Quality { get; set; }
    public int SellPrice { get; set; }
    public string Source { get; set; } = "loot";
    public int SourceCreatureEntry { get; set; }
    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Saved domain state for cross-domain interruptions.
/// When a bot can't turn in a quest because bags are full, QuestingDomain saves
/// the return target here. DecisionEngine forces EconomyDomain vendoring, then
/// restores the saved domain/sub-phase on completion.
/// </summary>
public class PendingAction
{
    public ActivityType ReturnTo { get; set; }
    public string SubPhase { get; set; } = "";
    public int? QuestId { get; set; }
    public Dictionary<string, string> PhaseData { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A quest deferral that can be time-gated, level-gated, or both.
/// Time-gated: expires after a clock duration (death/stuck deferrals).
/// Level-gated: expires when bot reaches a required level (PATH_UNSAFE deferrals).
/// </summary>
public class QuestDeferral
{
    /// <summary>UTC time expiry for time-based deferrals. Null = no time limit.</summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>Bot level required for level-based deferrals. Null = no level gate.</summary>
    public int? RequiredLevel { get; set; }

    public bool IsExpired(DateTime now, int botLevel)
    {
        // Time-gated: expired if past the clock
        if (ExpiresAt.HasValue && now >= ExpiresAt.Value) return true;
        // Level-gated: expired if bot reached the required level
        if (RequiredLevel.HasValue && botLevel >= RequiredLevel.Value) return true;
        // Neither condition met — still deferred
        return false;
    }

    public static QuestDeferral TimeBased(DateTime expiresAt) => new() { ExpiresAt = expiresAt };
    public static QuestDeferral LevelBased(int requiredLevel) => new() { RequiredLevel = requiredLevel };
}

public enum WowRace : int
{
    Human = 1, Orc = 2, Dwarf = 3, NightElf = 4, Undead = 5,
    Tauren = 6, Gnome = 7, Troll = 8
}

public enum WowClass : int
{
    Warrior = 1, Paladin = 2, Hunter = 3, Rogue = 4, Priest = 5,
    Shaman = 7, Mage = 8, Warlock = 9, Druid = 11
}

/// <summary>
/// Snapshot of a quest's DB state from character_queststatus, used to rebuild
/// ActiveQuestEntry on reconnect. Carries mob_count/item_count progress columns
/// so QuestingDomain can verify objectives are actually done before marking
/// ServerComplete (the nuke script wipes inventory but doesn't reset status).
/// </summary>
public class HydratedQuest
{
    public int QuestId { get; set; }
    public int Status { get; set; }      // 1=incomplete, 3=complete
    public int[] MobCounts { get; set; } = new int[4];
    public int[] ItemCounts { get; set; } = new int[4];
}