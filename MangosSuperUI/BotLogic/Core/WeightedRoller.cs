namespace MangosSuperUI.BotLogic.Core;

/// <summary>
/// Generic weighted random selection utility.
/// Thread-safe via ThreadStatic RNG — safe for parallel bot processing.
/// </summary>
public static class WeightedRoller
{
    [ThreadStatic] private static Random? _rng;
    private static Random Rng => _rng ??= new Random(Guid.NewGuid().GetHashCode());

    /// <summary>
    /// Normalizes weights and rolls. Returns selected key + raw roll value (0-1) for logging.
    /// Zero/negative weights are excluded from the roll.
    /// </summary>
    public static (T selected, float rollValue) Roll<T>(Dictionary<T, float> weights) where T : notnull
    {
        var valid = weights.Where(kv => kv.Value > 0).ToList();
        if (valid.Count == 0)
            throw new InvalidOperationException("No valid weights to roll from");

        if (valid.Count == 1)
            return (valid[0].Key, 0.5f);

        float total = valid.Sum(kv => kv.Value);
        float roll = (float)(Rng.NextDouble() * total);
        float cumulative = 0f;

        foreach (var kvp in valid)
        {
            cumulative += kvp.Value;
            if (roll <= cumulative)
                return (kvp.Key, roll / total);
        }

        return (valid.Last().Key, 1.0f);
    }

    /// <summary>
    /// Simple probability check. Returns true with given chance (0.0 = never, 1.0 = always).
    /// </summary>
    public static bool Check(float probability)
    {
        return Rng.NextDouble() < probability;
    }

    /// <summary>
    /// Roll a float in range [min, max].
    /// </summary>
    public static float Range(float min, float max)
    {
        return min + (float)(Rng.NextDouble() * (max - min));
    }

    /// <summary>
    /// Roll an int in range [min, max] inclusive.
    /// </summary>
    public static int RangeInt(int min, int max)
    {
        return Rng.Next(min, max + 1);
    }

    /// <summary>
    /// Normal distribution roll (Box-Muller). Clamped to [0, 1].
    /// </summary>
    public static float Normal(float mean, float stdDev)
    {
        double u1 = 1.0 - Rng.NextDouble();
        double u2 = Rng.NextDouble();
        double normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        float value = (float)(mean + normal * stdDev);
        return Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Adds a random offset (0.5–2.0 yards) in a random direction to prevent bots stacking on the same point.
    /// Use for grind centers, exploration wanders, and other non-NPC destinations.
    /// </summary>
    public static (float x, float y) Jitter(float x, float y)
    {
        float distance = Range(0.5f, 2.0f);
        float angle = Range(0f, MathF.PI * 2f);
        return (x + distance * MathF.Cos(angle), y + distance * MathF.Sin(angle));
    }

    /// <summary>
    /// Offsets target point 0.5–2.0 yards toward the bot's current position, with ±30° spread.
    /// Keeps jittered destination on ground the bot is approaching from — avoids walls behind NPCs.
    /// Falls back to random Jitter if bot is already on top of the target.
    /// </summary>
    public static (float x, float y) JitterToward(float targetX, float targetY, float fromX, float fromY)
    {
        float dx = fromX - targetX;
        float dy = fromY - targetY;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return Jitter(targetX, targetY);

        float baseAngle = MathF.Atan2(dy, dx);
        float spread = Range(-0.52f, 0.52f); // ±30° in radians
        float angle = baseAngle + spread;
        float dist = Range(0.5f, 2.0f);
        return (targetX + dist * MathF.Cos(angle), targetY + dist * MathF.Sin(angle));
    }
}