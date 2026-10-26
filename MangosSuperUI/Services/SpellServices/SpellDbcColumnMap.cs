namespace MangosSuperUI.Services;

/// <summary>
/// The one definition of "the client's Spell.dbc row and the server's spell_template row
/// agree" (MSUIClient shared_docs/SPELL_CREATOR_PLAN.md §2 rule 4, step P0.5).
///
/// vmangos runs spells from the world table spell_template; the 1.12 client reads
/// Spell.dbc for targeting (the ground cursor is the Targets mask), cast bars, ranges,
/// tooltips, auras and visuals. A custom spell whose two rows disagree on any field both
/// read is a spell whose client lies about what the server does. Before this map the
/// patch builder mirrored a hand-picked dozen fields, and three of them were wrong:
///
///   • field 1 holds the school NUMBER (0..6); the builder wrote a school MASK into it,
///     so a Fire spell (2) arrived in the client as 4 - Frost;
///   • fields 28/29 are baseLevel/spellLevel; the builder had them swapped;
///   • effectDieSides is stored IDENTICALLY on both sides (21,693 of 21,693 stock spells),
///     never "+1 in the DBC" - the builder's +1 made every custom tooltip's maximum one
///     higher than what the server could roll.
///
/// HOW THE TABLE WAS MADE: not from memory. Every column of every stock spell_template row
/// (21,693 spells present in both, build ≤ 5875) was compared with every field of the
/// client's own Spell.dbc; each entry below is the field that agrees on ALL of them
/// (integers as u32, floats within 1e-4). The columns vmangos added for itself
/// (effectBonusCoefficient*, minTargetLevel, customFlags, script_name) have no DBC field
/// and are absent on purpose. Re-derive with the MSUIClient plan's colmap script if the
/// schema changes; /SpellCompleter/ColumnMapCheck re-proves it against the live data.
///
/// Not in the table, because the patch builder owns them: the id (0), the visual (115 -
/// the cloned chain), the icon (117), and every string and locale-flags field (120..155).
/// </summary>
public static class SpellDbcColumnMap
{
    public enum Kind { Int, Float, BigLow, BigHigh }

    public readonly record struct Column(string Name, int Field, Kind Kind);

    public static readonly IReadOnlyList<Column> Columns = Build();

    /// <summary>The 1.12.1 (5875) Spell.dbc record width.</summary>
    public const int FieldCount = 173;

    private static List<Column> Build()
    {
        var c = new List<Column>();
        void I(string name, int field) => c.Add(new Column(name, field, Kind.Int));
        void F(string name, int field) => c.Add(new Column(name, field, Kind.Float));

        I("school", 1); I("category", 2); I("castUI", 3); I("dispel", 4); I("mechanic", 5);
        I("attributes", 6); I("attributesEx", 7); I("attributesEx2", 8); I("attributesEx3", 9);
        I("attributesEx4", 10); I("stances", 11); I("stancesNot", 12); I("targets", 13);
        I("targetCreatureType", 14); I("requiresSpellFocus", 15); I("casterAuraState", 16);
        I("targetAuraState", 17); I("castingTimeIndex", 18); I("recoveryTime", 19);
        I("categoryRecoveryTime", 20); I("interruptFlags", 21); I("auraInterruptFlags", 22);
        I("channelInterruptFlags", 23); I("procFlags", 24); I("procChance", 25); I("procCharges", 26);
        I("maxLevel", 27); I("baseLevel", 28); I("spellLevel", 29); I("durationIndex", 30);
        I("powerType", 31); I("manaCost", 32); I("manCostPerLevel", 33); I("manaPerSecond", 34);
        I("manaPerSecondPerLevel", 35); I("rangeIndex", 36); F("speed", 37); I("modelNextSpell", 38);
        I("stackAmount", 39); I("totem1", 40); I("totem2", 41);
        for (int i = 1; i <= 8; i++) I($"reagent{i}", 41 + i);
        for (int i = 1; i <= 8; i++) I($"reagentCount{i}", 49 + i);
        I("equippedItemClass", 58); I("equippedItemSubClassMask", 59); I("equippedItemInventoryTypeMask", 60);
        for (int i = 1; i <= 3; i++)
        {
            I($"effect{i}", 60 + i);
            I($"effectDieSides{i}", 63 + i);
            I($"effectBaseDice{i}", 66 + i);
            F($"effectDicePerLevel{i}", 69 + i);
            F($"effectRealPointsPerLevel{i}", 72 + i);
            I($"effectBasePoints{i}", 75 + i);
            I($"effectMechanic{i}", 78 + i);
            I($"effectImplicitTargetA{i}", 81 + i);
            I($"effectImplicitTargetB{i}", 84 + i);
            I($"effectRadiusIndex{i}", 87 + i);
            I($"effectApplyAuraName{i}", 90 + i);
            I($"effectAmplitude{i}", 93 + i);
            F($"effectMultipleValue{i}", 96 + i);
            I($"effectChainTarget{i}", 99 + i);
            I($"effectItemType{i}", 102 + i);
            I($"effectMiscValue{i}", 105 + i);
            I($"effectTriggerSpell{i}", 108 + i);
            F($"effectPointsPerComboPoint{i}", 111 + i);
        }
        I("spellVisual2", 116); I("activeIconId", 118); I("spellPriority", 119);
        I("manaCostPercentage", 156); I("startRecoveryCategory", 157); I("startRecoveryTime", 158);
        I("maxTargetLevel", 159); I("spellFamilyName", 160);
        c.Add(new Column("spellFamilyFlags", 161, Kind.BigLow));
        c.Add(new Column("spellFamilyFlags", 162, Kind.BigHigh));
        I("maxAffectedTargets", 163); I("dmgClass", 164); I("preventionType", 165); I("stanceBarOrder", 166);
        for (int i = 1; i <= 3; i++) F($"dmgMultiplier{i}", 166 + i);
        I("minFactionId", 170); I("minReputation", 171); I("requiredAuraVision", 172);
        return c;
    }

    /// <summary>The u32 a column's value occupies in the DBC record.</summary>
    public static uint Encode(Column column, object? value)
    {
        if (value is null || value is DBNull) return 0;
        switch (column.Kind)
        {
            case Kind.Float:
                return BitConverter.SingleToUInt32Bits(Convert.ToSingle(value));
            case Kind.BigLow:
                return unchecked((uint)(Convert.ToUInt64(value) & 0xFFFFFFFF));
            case Kind.BigHigh:
                return unchecked((uint)(Convert.ToUInt64(value) >> 32));
            default:
                // Signed columns (basePoints can be negative) keep their two's-complement bits.
                return unchecked((uint)Convert.ToInt64(value));
        }
    }

    /// <summary>Write every mapped column of a spell_template row (a Dapper row or any
    /// column-name dictionary) into the DBC row <paramref name="entry"/>. Columns missing
    /// from the dictionary are left as they are. Returns how many fields were written.</summary>
    public static int Apply(DbcWriterService spellDbc, uint entry, IDictionary<string, object?> templateRow)
    {
        var row = new Dictionary<string, object?>(templateRow, StringComparer.OrdinalIgnoreCase);
        int written = 0;
        foreach (Column column in Columns)
        {
            if (!row.TryGetValue(column.Name, out object? value)) continue;
            spellDbc.PatchRow(entry, column.Field, Encode(column, value));
            written++;
        }
        return written;
    }

    /// <summary>Compare one stock spell's two rows; returns the names of the columns that
    /// disagree (empty = agreement). Used by the live check endpoint.</summary>
    public static List<string> Diff(uint[] dbcRow, IDictionary<string, object?> templateRow)
    {
        var row = new Dictionary<string, object?>(templateRow, StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        foreach (Column column in Columns)
        {
            if (!row.TryGetValue(column.Name, out object? value) || column.Field >= dbcRow.Length) continue;
            uint expected = Encode(column, value);
            uint actual = dbcRow[column.Field];
            bool same = column.Kind == Kind.Float
                ? MathF.Abs(BitConverter.UInt32BitsToSingle(expected) - BitConverter.UInt32BitsToSingle(actual))
                  <= 1e-4f * MathF.Max(1f, MathF.Abs(BitConverter.UInt32BitsToSingle(expected)))
                : expected == actual;
            if (!same) bad.Add(column.Name);
        }
        return bad;
    }

    /// <summary>Points for a min..max roll, the vmangos way (SpellCaster.cpp: value =
    /// basePoints + irand(baseDice, dieSides) when dieSides > 1, else basePoints + baseDice).
    /// ASSUMES effectBaseDice = 1 - write it with the points: the from-zero template (18282)
    /// carries 0, and a live cast of 99 + 1 side rolled 99, not 100 (2026-10-25).</summary>
    public static (int basePoints, int dieSides) PointsFor(int min, int max)
    {
        if (max < min) (min, max) = (max, min);
        return (min - 1, max > min ? max - min + 1 : 1);
    }
}
