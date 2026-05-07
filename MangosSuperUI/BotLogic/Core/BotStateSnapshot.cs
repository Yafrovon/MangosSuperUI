namespace MangosSuperUI.BotLogic.Core;

/// <summary>
/// Lightweight snapshot populated from the most recent bridge STATE message.
/// Passed into every domain method so they don't need to query the bridge themselves.
/// </summary>
public class BotStateSnapshot
{
    // From STATE message
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    public int Level { get; set; }
    public int MapId { get; set; }
    public int ZoneId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public bool InCombat { get; set; }
    public bool IsDead { get; set; }
    public long TargetGuid { get; set; }

    // Enriched STATE fields (Session 6 C++ � freeSlots/totalSlots/copper)
    public uint FreeSlots { get; set; } = 16;
    public uint TotalSlots { get; set; } = 16;
    public uint Copper { get; set; } = 0;

    // Computed
    public float HealthPercent => MaxHealth > 0 ? Health / (float)MaxHealth : 1f;
    public float ManaPercent => MaxMana > 0 ? Mana / (float)MaxMana : 1f;
    public float BagFullness => TotalSlots > 0 ? (TotalSlots - FreeSlots) / (float)TotalSlots : 0f;

    // Enriched by BotStateTracker
    public long XP { get; set; }
    public long XPToNextLevel { get; set; }
    public bool IsNearTown { get; set; }
    public int NearbyPlayerCount { get; set; }
    public int NearbyBotCount { get; set; }

    // Server-side quest status (from C++ GetQuestStatus � authoritative)
    public uint ServerQuestId { get; set; } = 0;
    public uint ServerQuestStatus { get; set; } = 0;

    // --- Group state (Session 31 — enriched by BotBrainService from GroupManager + bridge) ---
    public int? GroupId { get; set; }
    public int? GroupLeaderGuid { get; set; }
    public float? LeaderX { get; set; }
    public float? LeaderY { get; set; }
    public float? LeaderZ { get; set; }
    public bool IsGrouped => GroupId.HasValue;

    /// <summary>
    /// Build a snapshot from the existing BotState (BotBridgeService model).
    /// </summary>
    public static BotStateSnapshot FromBridgeState(Services.BotState bs)
    {
        return new BotStateSnapshot
        {
            Health = bs.Health,
            MaxHealth = bs.MaxHealth,
            Mana = bs.Mana,
            MaxMana = bs.MaxMana,
            Level = bs.Level,
            MapId = bs.MapId,
            ZoneId = bs.ZoneId,
            X = bs.X,
            Y = bs.Y,
            Z = bs.Z,
            InCombat = bs.InCombat,
            IsDead = bs.IsDead,
            TargetGuid = bs.TargetGuid,
            FreeSlots = bs.FreeSlots,
            TotalSlots = bs.TotalSlots,
            Copper = bs.Copper,
            ServerQuestId = bs.QuestId,
            ServerQuestStatus = bs.QuestStatus
        };
    }
}