namespace MangosSuperUI.BotLogic.Core;

public enum ActivityType
{
    Idle,
    Questing,
    Grinding,
    TravelingToTrainer,
    Training,
    TravelingToVendor,
    Vendoring,
    AuctionHouse,
    Exploring,
    Socializing,
    Eating,
    CorpseRunning,
    FlightPath,
    TravelingToQuest,
    Loitering
}

public class ActivityState
{
    public ActivityType Type { get; set; } = ActivityType.Idle;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public string? ContextTag { get; set; }
    public int TicksInState { get; set; }
    public bool IsInterruptible { get; set; } = true;

    /// <summary>
    /// How many minutes the bot has been in this activity.
    /// Used for boredom escalation calculations across all domains.
    /// </summary>
    public double MinutesInState => (DateTime.UtcNow - StartedAt).TotalMinutes;

    /// <summary>
    /// Sub-state tracking for complex activities.
    /// QuestingDomain uses this for QuestPhase (PickingQuest, TravelingToGiver, etc.)
    /// EconomyDomain uses this for VendorPhase (Walking, Selling, Browsing, etc.)
    /// </summary>
    public string? SubPhase { get; set; }

    /// <summary>
    /// Arbitrary data bag for the current domain to store phase-specific state.
    /// Avoids domains needing to maintain their own per-bot dictionaries.
    /// Examples: current quest ID, target creature entry, vendor NPC entry.
    /// </summary>
    public Dictionary<string, object> PhaseData { get; set; } = new();
}
