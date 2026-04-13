namespace MangosSuperUI.BotLogic.Core;

// ======================== DecisionResult ========================

public class DecisionResult
{
    public ActivityType NewActivity { get; set; }
    public string Reason { get; set; } = "";
    public Dictionary<ActivityType, float> WeightBreakdown { get; set; } = new();
    public float RollValue { get; set; }
    public List<BridgeCommand> Commands { get; set; } = new();
    public bool ActivityChanged { get; set; }
}

// ======================== BridgeCommand ========================

public class BridgeCommand
{
    public string Type { get; set; }
    public Dictionary<string, object> Payload { get; set; } = new();

    public BridgeCommand(string type, object? payloadObj = null)
    {
        Type = type;
        if (payloadObj != null)
        {
            foreach (var prop in payloadObj.GetType().GetProperties())
                Payload[prop.Name] = prop.GetValue(payloadObj)!;
        }
    }
}

// ======================== IBotDomain ========================

public interface IBotDomain
{
    /// <summary>
    /// The activity types this domain manages.
    /// A domain can own multiple activities (e.g., EconomyDomain owns Vendoring + AuctionHouse).
    /// </summary>
    ActivityType[] OwnedActivities { get; }

    /// <summary>
    /// Called by DecisionEngine when the bot is in one of this domain's activities.
    /// Returns weighted alternatives. Must always include current activity as "stay" option.
    /// </summary>
    Dictionary<ActivityType, float> EvaluateTransitions(BotIdentity bot, BotStateSnapshot state);

    /// <summary>
    /// Called when bot transitions INTO one of this domain's activities.
    /// Returns initial commands (e.g., MOVE_TO the quest giver).
    /// </summary>
    List<BridgeCommand> OnEnter(BotIdentity bot, BotStateSnapshot state);

    /// <summary>
    /// Called every tick while bot is in this domain. Drives sub-task progression.
    /// Returns commands for the current phase.
    /// </summary>
    List<BridgeCommand> OnTick(BotIdentity bot, BotStateSnapshot state);

    /// <summary>
    /// Called when a bridge event arrives while in this domain.
    /// Returns reactive commands.
    /// </summary>
    List<BridgeCommand> OnEvent(BotIdentity bot, BotStateSnapshot state, BotEvent evt);
}

// ======================== BotEvent ========================

public class BotEvent
{
    public string EventType { get; set; } = "";
    public int CreatureEntry { get; set; }
    public long CreatureGuid { get; set; }
    public int QuestId { get; set; }
    public string QuestStatus { get; set; } = "";
    public int NewLevel { get; set; }
    public string Sender { get; set; } = "";
    public string Message { get; set; } = "";
    public string ChatType { get; set; } = "";
    public string Data { get; set; } = "";
    public string ChannelName { get; set; } = "";
}
