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
    /// Whether this domain has real logic implemented and should participate in
    /// strategic rolls. When false, DecisionEngine zeros out all weights for this
    /// domain's activities — the bot will never switch into a non-operational domain.
    ///
    /// Domains default to false (stub) and must explicitly override to true when
    /// their logic is built out. This prevents bots from wasting time switching to
    /// Socializing, Vendoring, TravelingToTrainer, etc. when those domains are stubs
    /// that emit no commands and leave the bot standing around doing nothing.
    ///
    /// As each domain is built, set IsOperational = true and the decision engine
    /// starts including it in rolls automatically — no other wiring needed.
    /// </summary>
    bool IsOperational => false;

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
    public int? QuestId { get; set; }
    public string QuestStatus { get; set; } = "";
    public int NewLevel { get; set; }
    public string Sender { get; set; } = "";
    public string Message { get; set; } = "";
    public string ChatType { get; set; } = "";
    public string Data { get; set; } = "";
    public string ChannelName { get; set; } = "";
    // --- Flight path fields (present on FLIGHT_FAILED) ---
    public string Reason { get; set; } = "";
    public uint Have { get; set; }
    public uint Need { get; set; }
    public uint Cost { get; set; }
}