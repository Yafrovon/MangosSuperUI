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

    // --- Activity tracking ---
    public ActivityState CurrentActivity { get; set; } = new();
    public ActivityState? PreviousActivity { get; set; }
    public DateTime NextDecisionTick { get; set; } = DateTime.UtcNow;

    // --- Quest tracking ---
    public int? ActiveQuestId { get; set; }
    public string? ActiveQuestPhase { get; set; }
    public float CurrentQuestProgress { get; set; }
    public HashSet<int> CompletedQuestIds { get; set; } = new();

    // --- Spell/training tracking ---
    public HashSet<int> KnownSpellIds { get; set; } = new();
    public bool HasUnlearnedSpells { get; set; }
    public int TicksSinceLastTrained { get; set; }

    // --- Shadow inventory (in-memory, flushed to DB periodically) ---
    public List<ShadowInventoryItem> ShadowInventory { get; set; } = new();

    // --- Relationships (future) ---
    public HashSet<int> MetPlayerGuids { get; set; } = new();

    // --- Computed helpers ---
    public float XPPercent => XPToNextLevel > 0 ? XP / (float)XPToNextLevel : 0f;
    public bool IsNearLevelUp => XPPercent > 0.85f;
    public string RaceClassName => $"{(WowRace)Race} {(WowClass)ClassId}";

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
