using System.Collections.Concurrent;
using System.Text.Json;
using MangosSuperUI.BotLogic.Core;
using MangosSuperUI.Services;
using Dapper;
using MangosSuperUI.Models;

namespace MangosSuperUI.BotLogic.Tracking;

/// <summary>
/// Accumulates activity log entries in-memory and flushes to bot_activity_log
/// in the admin DB (SQL Server) every 30 seconds as a batch insert.
/// At 200 bots × 1 decision every 20 seconds = 10 rows/sec → ~300 rows per flush.
/// </summary>
public class BotActivityLog
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<BotActivityLog> _logger;
    private readonly ConcurrentQueue<ActivityLogEntry> _queue = new();
    private DateTime _lastFlush = DateTime.UtcNow;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    public BotActivityLog(ConnectionFactory db, ILogger<BotActivityLog> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Queue an activity log entry for batched insert.
    /// </summary>
    public void LogDecision(int botGuid, DecisionResult result)
    {
        _queue.Enqueue(new ActivityLogEntry
        {
            BotGuid = botGuid,
            ActivityType = result.NewActivity.ToString(),
            StartedAt = DateTime.UtcNow,
            ContextTag = null,
            DecisionReason = result.Reason,
            WeightSnapshot = JsonSerializer.Serialize(
                result.WeightBreakdown.ToDictionary(kv => kv.Key.ToString(), kv => Math.Round(kv.Value, 3))),
            RollValue = result.RollValue
        });
    }

    /// <summary>
    /// Log an activity end (when transitioning away).
    /// </summary>
    public void LogActivityEnd(int botGuid, ActivityState activity)
    {
        _queue.Enqueue(new ActivityLogEntry
        {
            BotGuid = botGuid,
            ActivityType = activity.Type.ToString(),
            StartedAt = activity.StartedAt,
            EndedAt = DateTime.UtcNow,
            ContextTag = activity.ContextTag,
            DecisionReason = $"Ended after {activity.MinutesInState:F1} min ({activity.TicksInState} ticks)"
        });
    }

    /// <summary>
    /// Called periodically by BotBrainService. Flushes if interval elapsed.
    /// </summary>
    public async Task FlushIfDueAsync()
    {
        if (DateTime.UtcNow - _lastFlush < FlushInterval)
            return;

        await FlushAsync();
    }

    /// <summary>
    /// Force flush all queued entries.
    /// </summary>
    public async Task FlushAsync()
    {
        _lastFlush = DateTime.UtcNow;

        var batch = new List<ActivityLogEntry>();
        while (_queue.TryDequeue(out var entry))
            batch.Add(entry);

        if (batch.Count == 0) return;

        try
        {
            using var conn = _db.Admin();

            // Batch insert using Dapper
            await conn.ExecuteAsync(@"
                INSERT INTO bot_activity_log
                    (bot_guid, activity_type, started_at, ended_at, context_tag, decision_reason, weight_snapshot, roll_value)
                VALUES
                    (@BotGuid, @ActivityType, @StartedAt, @EndedAt, @ContextTag, @DecisionReason, @WeightSnapshot, @RollValue)",
                batch);

            _logger.LogDebug("BotActivityLog: flushed {Count} entries", batch.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotActivityLog: flush failed for {Count} entries — re-queuing", batch.Count);
            // Re-queue failed entries (best-effort)
            foreach (var entry in batch)
                _queue.Enqueue(entry);
        }
    }

    /// <summary>
    /// How many entries are queued but not yet flushed.
    /// </summary>
    public int PendingCount => _queue.Count;
}

internal class ActivityLogEntry
{
    public int BotGuid { get; set; }
    public string ActivityType { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? ContextTag { get; set; }
    public string? DecisionReason { get; set; }
    public string? WeightSnapshot { get; set; }
    public float? RollValue { get; set; }
}
