namespace MangosSuperUI.BotLogic.Core;

// ════════════════════════════════════════════════════════════════════
// BotGroup — Group data model for the AiBot grouping system
//
// Session 31: Sticky duo/trio groups with server-level mode control.
//
// Three server-level modes (set via dashboard, stored in bot_settings):
//   Off            — all bots solo, GroupManager is a no-op
//   Sticky         — groups form via dashboard/script, persist until disbanded
//   Opportunistic  — (future) dynamic formation based on proximity + class synergy
//
// Two group-level modes (per group):
//   BotCoordinated — C# picks quests, leader drives movement
//   PlayerLed      — (future) human player invited bot, bot follows player
//
// WoW Group (C++ side) is always the mechanical layer regardless of mode.
// C# GroupManager just tracks WHO is coordinating and WHO picks quests.
// ════════════════════════════════════════════════════════════════════

/// <summary>
/// Server-wide grouping behavior mode. Set via dashboard toggle.
/// </summary>
public enum GroupingMode
{
    /// <summary>All bots solo. GroupManager methods are no-ops.</summary>
    Off = 0,

    /// <summary>Groups form explicitly (dashboard/script). Persist until disbanded.</summary>
    Sticky = 1,

    /// <summary>(Future) Bots form/dissolve groups dynamically based on proximity, quest overlap, class synergy.</summary>
    Opportunistic = 2
}

/// <summary>
/// Who is driving the group's decision-making.
/// </summary>
public enum GroupLeaderType
{
    /// <summary>C# DecisionEngine picks quests. Leader bot drives, followers sync.</summary>
    BotCoordinated = 0,

    /// <summary>(Future) A real player invited bots. Bots follow the player's lead.</summary>
    PlayerLed = 1
}

/// <summary>
/// A persistent small group (2-3 members) that coordinates questing, movement, and combat.
/// Leader picks quests; followers adopt the leader's batch and follow movement.
/// WoW Group (C++ side) handles kill credit, loot, and party frames.
/// </summary>
public class BotGroup
{
    public int GroupId { get; set; }
    public int LeaderGuid { get; set; }
    public List<int> MemberGuids { get; set; } = new();
    public GroupLeaderType LeaderType { get; set; } = GroupLeaderType.BotCoordinated;
    public DateTime FormedAt { get; set; } = DateTime.UtcNow;

    /// <summary>(Future) If PlayerLed, the human player's GUID. Null for BotCoordinated.</summary>
    public int? PlayerLeaderGuid { get; set; }

    // ── Helpers ──

    public bool IsLeader(int guid) => LeaderGuid == guid;
    public bool IsMember(int guid) => MemberGuids.Contains(guid);
    public int Size => MemberGuids.Count;

    /// <summary>All member GUIDs except the given one (useful for "get my groupmates").</summary>
    public List<int> GetOtherMembers(int guid) => MemberGuids.Where(g => g != guid).ToList();

    /// <summary>All member GUIDs except the leader.</summary>
    public List<int> GetFollowers() => MemberGuids.Where(g => g != LeaderGuid).ToList();
}
