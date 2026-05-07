using Dapper;
using MangosSuperUI.BotLogic.Core;
using MangosSuperUI.BotLogic.Tracking;
using MangosSuperUI.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MangosSuperUI.BotLogic.Core;

// ════════════════════════════════════════════════════════════════════
// GroupManager — Formation, persistence, and lifecycle for bot groups
//
// Session 31: Lives as a singleton on BotBrainService.
//
// All public methods are gated by GroupingMode — when Off, they're no-ops.
// DB table: vmangos_admin.bot_groups (created by BotBrainDbInit).
//
// Design for future extensibility:
//   - OnPlayerInvite / OnPlayerGroupLeft stubs for real player integration
//   - GroupLeaderType.PlayerLed path for human-led groups
//   - Opportunistic formation logic placeholder
// ════════════════════════════════════════════════════════════════════

public class GroupManager
{
    private readonly ConnectionFactory _db;
    private readonly ILogger _logger;

    // GroupId → BotGroup
    private readonly ConcurrentDictionary<int, BotGroup> _groups = new();

    // BotGuid → GroupId (reverse lookup for fast "what group am I in?")
    private readonly ConcurrentDictionary<int, int> _botToGroup = new();

    // Server-wide grouping mode (set from dashboard, defaults to Off)
    private GroupingMode _mode = GroupingMode.Off;

    // Auto-increment for in-memory group IDs (DB uses AUTO_INCREMENT,
    // but we need IDs for groups formed before first DB flush)
    private int _nextGroupId = 1;

    public GroupManager(ConnectionFactory db, ILogger logger)
    {
        _db = db;
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════
    // Mode control (called from dashboard / BotBrainService)
    // ════════════════════════════════════════════════════════════════

    public GroupingMode Mode
    {
        get => _mode;
        set
        {
            var old = _mode;
            _mode = value;
            _logger.LogInformation("[BOT-GROUP] Grouping mode changed: {Old} → {New}", old, value);

            // If switching to Off, disband everything
            if (value == GroupingMode.Off && old != GroupingMode.Off)
            {
                DisbandAll();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Queries (always available regardless of mode)
    // ════════════════════════════════════════════════════════════════

    /// <summary>Get the group a bot belongs to, or null if solo.</summary>
    public BotGroup? GetGroup(int botGuid)
    {
        if (_botToGroup.TryGetValue(botGuid, out int groupId))
            if (_groups.TryGetValue(groupId, out var group))
                return group;
        return null;
    }

    /// <summary>Is this bot in any group?</summary>
    public bool IsGrouped(int botGuid) => _botToGroup.ContainsKey(botGuid);

    /// <summary>Is this bot the leader of its group?</summary>
    public bool IsLeader(int botGuid)
    {
        var group = GetGroup(botGuid);
        return group?.IsLeader(botGuid) ?? false;
    }

    /// <summary>Is this bot a follower (grouped but not leader)?</summary>
    public bool IsFollower(int botGuid)
    {
        var group = GetGroup(botGuid);
        return group != null && !group.IsLeader(botGuid);
    }

    /// <summary>Get the leader's GUID for a grouped bot, or null if solo.</summary>
    public int? GetLeaderGuid(int botGuid)
    {
        var group = GetGroup(botGuid);
        return group?.LeaderGuid;
    }

    /// <summary>Get all current groups (for dashboard display).</summary>
    public IReadOnlyCollection<BotGroup> GetAllGroups() => _groups.Values.ToList();

    /// <summary>Get all ungrouped bot GUIDs from a given set.</summary>
    public List<int> GetUngroupedBots(IEnumerable<int> allBotGuids)
        => allBotGuids.Where(g => !_botToGroup.ContainsKey(g)).ToList();

    // ════════════════════════════════════════════════════════════════
    // Formation (gated by mode)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Form a new bot-coordinated group. Leader = first GUID.
    /// Returns the BotGroup if formed, null if mode is Off or invalid.
    /// Does NOT send FORM_GROUP to C++ — caller must do that after
    /// (BotBrainService has the bridge reference).
    /// </summary>
    public BotGroup? FormGroup(int leaderGuid, params int[] followerGuids)
    {
        if (_mode == GroupingMode.Off)
        {
            _logger.LogDebug("[BOT-GROUP] FormGroup rejected — mode is Off");
            return null;
        }

        // Validate: no one is already grouped
        var allMembers = new List<int> { leaderGuid };
        allMembers.AddRange(followerGuids);

        foreach (var guid in allMembers)
        {
            if (_botToGroup.ContainsKey(guid))
            {
                _logger.LogWarning("[BOT-GROUP] FormGroup rejected — bot {Guid} is already in group {GroupId}",
                    guid, _botToGroup[guid]);
                return null;
            }
        }

        if (allMembers.Count < 2 || allMembers.Count > 5)
        {
            _logger.LogWarning("[BOT-GROUP] FormGroup rejected — invalid size {Size} (need 2-5)", allMembers.Count);
            return null;
        }

        var group = new BotGroup
        {
            GroupId = _nextGroupId++,
            LeaderGuid = leaderGuid,
            MemberGuids = allMembers,
            LeaderType = GroupLeaderType.BotCoordinated,
            FormedAt = DateTime.UtcNow
        };

        _groups[group.GroupId] = group;
        foreach (var guid in allMembers)
            _botToGroup[guid] = group.GroupId;

        _logger.LogInformation("[BOT-GROUP] Formed group {GroupId}: leader={Leader}, members=[{Members}]",
            group.GroupId, leaderGuid, string.Join(",", allMembers));

        return group;
    }

    /// <summary>
    /// Disband a group by groupId. Removes all tracking.
    /// Does NOT send DISBAND_GROUP to C++ — caller must do that.
    /// </summary>
    public bool DisbandGroup(int groupId)
    {
        if (!_groups.TryRemove(groupId, out var group))
            return false;

        foreach (var guid in group.MemberGuids)
            _botToGroup.TryRemove(guid, out _);

        _logger.LogInformation("[BOT-GROUP] Disbanded group {GroupId} (was: [{Members}])",
            groupId, string.Join(",", group.MemberGuids));

        return true;
    }

    /// <summary>Remove a single bot from its group. If group drops to 1, disband.</summary>
    public bool RemoveFromGroup(int botGuid)
    {
        var group = GetGroup(botGuid);
        if (group == null) return false;

        group.MemberGuids.Remove(botGuid);
        _botToGroup.TryRemove(botGuid, out _);

        _logger.LogInformation("[BOT-GROUP] Removed bot {Guid} from group {GroupId}", botGuid, group.GroupId);

        // If only 1 member left, disband
        if (group.MemberGuids.Count <= 1)
        {
            DisbandGroup(group.GroupId);
        }
        // If the leader left, promote lowest GUID
        else if (group.LeaderGuid == botGuid)
        {
            group.LeaderGuid = group.MemberGuids.Min();
            _logger.LogInformation("[BOT-GROUP] New leader for group {GroupId}: {NewLeader}",
                group.GroupId, group.LeaderGuid);
        }

        return true;
    }

    /// <summary>Disband all groups. Called when mode switches to Off.</summary>
    public void DisbandAll()
    {
        var groupIds = _groups.Keys.ToList();
        foreach (var gid in groupIds)
            DisbandGroup(gid);

        _logger.LogInformation("[BOT-GROUP] Disbanded all groups ({Count})", groupIds.Count);
    }

    // ════════════════════════════════════════════════════════════════
    // Player integration stubs (future — Ollama chat / right-click invite)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// (Future) Called when a real player invites a bot into their party.
    /// Removes bot from any bot-bot group and marks as PlayerLed.
    /// </summary>
    public void OnPlayerInvite(int botGuid, int playerGuid)
    {
        // Remove from existing bot group if any
        RemoveFromGroup(botGuid);

        // TODO: Create a PlayerLed group entry, set bot as follower
        // The bot should switch to a "follow player" mode in DecisionEngine
        _logger.LogInformation("[BOT-GROUP] (stub) Player {Player} invited bot {Bot} — not yet implemented",
            playerGuid, botGuid);
    }

    /// <summary>
    /// (Future) Called when a bot leaves a player-led group (player kicks, disbands, or logs out).
    /// Bot returns to solo or re-joins a bot group if Sticky/Opportunistic mode is on.
    /// </summary>
    public void OnPlayerGroupLeft(int botGuid)
    {
        _logger.LogInformation("[BOT-GROUP] (stub) Bot {Bot} left player group — not yet implemented", botGuid);
    }

    // ════════════════════════════════════════════════════════════════
    // Auto-formation (called explicitly, not automatically)
    // ════════════════════════════════════════════════════════════════

    private const int MAX_LEVEL_GAP = 2;        // bots must be within 2 levels
    private const float MAX_PAIR_DISTANCE = 200f; // must be within 200yd to pair

    /// <summary>
    /// Class-aware auto-formation for ungrouped bots. Builds trios where possible,
    /// falls back to duos. Filters by level proximity and zone/distance.
    ///
    /// Composition priority (best → worst):
    ///   Trio:  warrior + priest + mage       (tank + healer + DPS — best possible)
    ///   Trio:  warrior + priest + paladin    (tank + healer + off-tank)
    ///   Trio:  warrior + paladin + mage      (tank + off-healer + DPS)
    ///   Duo:   warrior + priest              (tank + healer)
    ///   Duo:   warrior + paladin             (tank + off-healer)
    ///   Duo:   warrior + mage               (tank + DPS)
    ///   Duo:   paladin + priest              (off-tank + healer, no warrior)
    ///   Duo:   paladin + mage               (off-tank + DPS, no warrior)
    ///
    /// Leader selection: warrior > paladin > priest > mage (tankiest leads).
    /// Within same class, lowest level leads (so quests are available to all).
    /// </summary>
    public List<BotGroup> AutoFormGroups(
        IReadOnlyDictionary<int, BotIdentity> allBots,
        Func<int, BotPosition?>? getPosition = null)
    {
        if (_mode == GroupingMode.Off)
        {
            _logger.LogDebug("[BOT-GROUP] AutoFormGroups skipped — mode is Off");
            return new List<BotGroup>();
        }

        var formed = new List<BotGroup>();
        var claimed = new HashSet<int>(); // GUIDs already assigned this round

        var ungrouped = allBots.Values
            .Where(b => !IsGrouped(b.Guid))
            .ToList();

        // Helper: check if two bots are compatible (level + distance)
        bool AreCompatible(BotIdentity a, BotIdentity b)
        {
            if (Math.Abs(a.Level - b.Level) > MAX_LEVEL_GAP) return false;
            if (getPosition != null)
            {
                var posA = getPosition(a.Guid);
                var posB = getPosition(b.Guid);
                if (posA != null && posB != null)
                {
                    if (posA.MapId != posB.MapId) return false;
                    float dx = posA.X - posB.X;
                    float dy = posA.Y - posB.Y;
                    if (dx * dx + dy * dy > MAX_PAIR_DISTANCE * MAX_PAIR_DISTANCE) return false;
                }
            }
            return true;
        }

        // Helper: check if a third bot is compatible with both existing members
        bool IsCompatibleWithBoth(BotIdentity candidate, BotIdentity a, BotIdentity b)
            => AreCompatible(candidate, a) && AreCompatible(candidate, b);

        // Helper: pick leader — warrior > paladin > priest > mage, then lowest level
        int PickLeader(params BotIdentity[] members)
        {
            int ClassPriority(int classId) => classId switch
            {
                1 => 0, // Warrior — always leads
                2 => 1, // Paladin
                5 => 2, // Priest
                _ => 3  // Mage, others
            };
            return members
                .OrderBy(m => ClassPriority(m.ClassId))
                .ThenBy(m => m.Level)
                .ThenBy(m => m.Guid)
                .First().Guid;
        }

        // Helper: try to form a group, returns true if successful
        bool TryForm(params BotIdentity[] members)
        {
            if (members.Any(m => claimed.Contains(m.Guid))) return false;
            int leader = PickLeader(members);
            var followers = members.Where(m => m.Guid != leader).Select(m => m.Guid).ToArray();
            var group = FormGroup(leader, followers);
            if (group == null) return false;
            formed.Add(group);
            foreach (var m in members) claimed.Add(m.Guid);
            return true;
        }

        // Buckets (only unclaimed, re-filtered each pass)
        List<BotIdentity> GetAvailable(int classId) =>
            ungrouped.Where(b => b.ClassId == classId && !claimed.Contains(b.Guid))
                     .OrderBy(b => b.Level).ThenBy(b => b.Guid).ToList();

        // ── Pass 1: Trios (warrior + priest + DPS/off-tank) ──

        // warrior + priest + mage (best trio)
        foreach (var w in GetAvailable((int)WowClass.Warrior))
        {
            var priest = GetAvailable((int)WowClass.Priest)
                .FirstOrDefault(p => AreCompatible(w, p));
            if (priest == null) continue;

            var mage = GetAvailable((int)WowClass.Mage)
                .FirstOrDefault(m => IsCompatibleWithBoth(m, w, priest));
            if (mage != null)
                TryForm(w, priest, mage);
        }

        // warrior + priest + paladin
        foreach (var w in GetAvailable((int)WowClass.Warrior))
        {
            var priest = GetAvailable((int)WowClass.Priest)
                .FirstOrDefault(p => AreCompatible(w, p));
            if (priest == null) continue;

            var paladin = GetAvailable((int)WowClass.Paladin)
                .FirstOrDefault(p => IsCompatibleWithBoth(p, w, priest));
            if (paladin != null)
                TryForm(w, priest, paladin);
        }

        // warrior + paladin + mage
        foreach (var w in GetAvailable((int)WowClass.Warrior))
        {
            var paladin = GetAvailable((int)WowClass.Paladin)
                .FirstOrDefault(p => AreCompatible(w, p));
            if (paladin == null) continue;

            var mage = GetAvailable((int)WowClass.Mage)
                .FirstOrDefault(m => IsCompatibleWithBoth(m, w, paladin));
            if (mage != null)
                TryForm(w, paladin, mage);
        }

        // ── Pass 2: Duos (remaining ungrouped) ──

        // warrior + priest
        foreach (var w in GetAvailable((int)WowClass.Warrior))
        {
            var priest = GetAvailable((int)WowClass.Priest)
                .FirstOrDefault(p => AreCompatible(w, p));
            if (priest != null) TryForm(w, priest);
        }

        // warrior + paladin
        foreach (var w in GetAvailable((int)WowClass.Warrior))
        {
            var paladin = GetAvailable((int)WowClass.Paladin)
                .FirstOrDefault(p => AreCompatible(w, p));
            if (paladin != null) TryForm(w, paladin);
        }

        // warrior + mage
        foreach (var w in GetAvailable((int)WowClass.Warrior))
        {
            var mage = GetAvailable((int)WowClass.Mage)
                .FirstOrDefault(m => AreCompatible(w, m));
            if (mage != null) TryForm(w, mage);
        }

        // paladin + priest (no warriors left)
        foreach (var pal in GetAvailable((int)WowClass.Paladin))
        {
            var priest = GetAvailable((int)WowClass.Priest)
                .FirstOrDefault(p => AreCompatible(pal, p));
            if (priest != null) TryForm(pal, priest);
        }

        // paladin + mage
        foreach (var pal in GetAvailable((int)WowClass.Paladin))
        {
            var mage = GetAvailable((int)WowClass.Mage)
                .FirstOrDefault(m => AreCompatible(pal, m));
            if (mage != null) TryForm(pal, mage);
        }

        // ── Pass 3 (Session 33): Any-class duos — better grouped than solo ──
        // After all class-synergy combos are exhausted, pair up whoever's left.
        // Rogue + Rogue? Fine. Priest + Priest? Still shared kill credit.
        {
            var stillUngrouped = ungrouped.Where(b => !claimed.Contains(b.Guid)).ToList();
            while (stillUngrouped.Count >= 2)
            {
                var first = stillUngrouped[0];
                var partner = stillUngrouped.Skip(1)
                    .FirstOrDefault(b => AreCompatible(first, b));

                if (partner != null)
                {
                    TryForm(first, partner);
                    stillUngrouped.Remove(first);
                    stillUngrouped.Remove(partner);
                }
                else
                {
                    // Can't pair this bot with anyone (level/distance gap) — skip
                    stillUngrouped.Remove(first);
                }
            }
        }

        // ── Pass 4 (Session 33): No bot left behind ──
        // If exactly 1 bot remains ungrouped, stuff them into the smallest
        // existing group (making it 3 or 4). Better than solo.
        {
            var loner = ungrouped.Where(b => !claimed.Contains(b.Guid)).ToList();
            if (loner.Count == 1)
            {
                var stray = loner[0];
                // Find smallest compatible group to absorb the stray
                var bestGroup = formed
                    .Where(g => g.Size < 5) // WoW party max
                    .Where(g =>
                    {
                        // Check level compatibility with all members
                        return g.MemberGuids.All(mg =>
                        {
                            if (!allBots.TryGetValue(mg, out var member)) return true;
                            return Math.Abs(member.Level - stray.Level) <= MAX_LEVEL_GAP;
                        });
                    })
                    .OrderBy(g => g.Size) // smallest first — prefer making a trio over a quad
                    .FirstOrDefault();

                if (bestGroup != null)
                {
                    bestGroup.MemberGuids.Add(stray.Guid);
                    _botToGroup[stray.Guid] = bestGroup.GroupId;
                    claimed.Add(stray.Guid);

                    _logger.LogInformation(
                        "[BOT-GROUP] No-bot-left-behind: added {Name}({Guid}) to group {GroupId} (now {Size} members)",
                        stray.Name, stray.Guid, bestGroup.GroupId, bestGroup.Size);
                }
            }
        }

        var remaining = ungrouped.Count(b => !claimed.Contains(b.Guid));
        _logger.LogInformation(
            "[BOT-GROUP] AutoFormGroups: formed {Count} groups ({Trios} trios, {Duos} duos, {Larger} 4+), {Remaining} bots ungrouped",
            formed.Count,
            formed.Count(g => g.Size == 3),
            formed.Count(g => g.Size == 2),
            formed.Count(g => g.Size >= 4),
            remaining);

        return formed;
    }

    // ════════════════════════════════════════════════════════════════
    // BotIdentity enrichment (called after group changes)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stamp GroupId + GroupLeaderGuid onto BotIdentity for a bot.
    /// Called after forming/disbanding groups so domains can read group state
    /// off BotIdentity without needing a GroupManager reference.
    /// </summary>
    public void EnrichBotIdentity(BotIdentity bot)
    {
        var group = GetGroup(bot.Guid);
        if (group != null)
        {
            bot.GroupId = group.GroupId;
            bot.GroupLeaderGuid = group.IsLeader(bot.Guid) ? bot.Guid : group.LeaderGuid;
        }
        else
        {
            bot.GroupId = null;
            bot.GroupLeaderGuid = null;
        }
    }

    /// <summary>Enrich all bots in the roster.</summary>
    public void EnrichAllBots(IEnumerable<BotIdentity> bots)
    {
        foreach (var bot in bots)
            EnrichBotIdentity(bot);
    }

    // ════════════════════════════════════════════════════════════════
    // DB persistence
    // ════════════════════════════════════════════════════════════════

    /// <summary>Load groups from DB on startup. Assigns GroupId/GroupLeaderGuid on BotIdentity.</summary>
    public async Task LoadGroupsFromDbAsync()
    {
        try
        {
            using var conn = _db.Admin();
            var rows = await conn.QueryAsync<BotGroupRow>(
                "SELECT group_id, leader_guid, member_guids, leader_type, formed_at FROM bot_groups");

            int loaded = 0;
            foreach (var row in rows)
            {
                var memberGuids = row.member_guids
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.Parse(s.Trim()))
                    .ToList();

                if (memberGuids.Count < 2) continue;

                var group = new BotGroup
                {
                    GroupId = row.group_id,
                    LeaderGuid = row.leader_guid,
                    MemberGuids = memberGuids,
                    LeaderType = (GroupLeaderType)row.leader_type,
                    FormedAt = row.formed_at
                };

                _groups[group.GroupId] = group;
                foreach (var guid in memberGuids)
                    _botToGroup[guid] = group.GroupId;

                if (group.GroupId >= _nextGroupId)
                    _nextGroupId = group.GroupId + 1;

                loaded++;
            }

            _logger.LogInformation("[BOT-GROUP] Loaded {Count} groups from DB", loaded);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BOT-GROUP] Failed to load groups from DB");
        }
    }

    /// <summary>Persist current groups to DB (full replace).</summary>
    public async Task SaveGroupsToDbAsync()
    {
        try
        {
            using var conn = _db.Admin();

            // Truncate and re-insert (simple for small group counts)
            await conn.ExecuteAsync("DELETE FROM bot_groups");

            foreach (var group in _groups.Values)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO bot_groups (group_id, leader_guid, member_guids, leader_type, formed_at)
                    VALUES (@GroupId, @LeaderGuid, @MemberGuids, @LeaderType, @FormedAt)",
                    new
                    {
                        group.GroupId,
                        group.LeaderGuid,
                        MemberGuids = string.Join(",", group.MemberGuids),
                        LeaderType = (int)group.LeaderType,
                        group.FormedAt
                    });
            }

            _logger.LogDebug("[BOT-GROUP] Saved {Count} groups to DB", _groups.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BOT-GROUP] Failed to save groups to DB");
        }
    }

    // ── DB row model ──
    private class BotGroupRow
    {
        public int group_id { get; set; }
        public int leader_guid { get; set; }
        public string member_guids { get; set; } = "";
        public int leader_type { get; set; }
        public DateTime formed_at { get; set; }
    }
}