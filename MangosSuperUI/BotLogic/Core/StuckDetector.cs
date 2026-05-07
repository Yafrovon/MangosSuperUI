// ============================================================
// StuckDetector.cs — Session 25
//
// Detects bots stuck in behavioral loops.
//
// Lives on BotIdentity (one per bot). Fed by DecisionEngine on
// every strategic eval. Tracks a rolling window of activity
// transitions and detects two failure modes:
//
//   1. LOOP: same 2-3 activity sequence repeats 3+ times within
//      5 minutes. Example: Questing → NoQuests → Grinding →
//      NoHostiles → Questing → NoQuests → Grinding → ...
//
//   2. THRASH: 6+ strategic evals within 90 seconds. Normal
//      cadence is one eval every 3-10 minutes. Rapid-fire evals
//      mean every domain is rejecting the bot immediately.
//
// When stuck is detected:
//   - Returns a StuckReport with the pattern description
//   - Caller (DecisionEngine or BotBrainService) logs [BOT-STUCK],
//     emits SignalR, and can force a cooldown/wander fallback
//
// FILE PLACEMENT: BotLogic/Core/StuckDetector.cs
// REGISTRATION: Created in BotBrainService.InitializeBotAsync(),
//               stored on BotIdentity.StuckDetector
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace MangosSuperUI.BotLogic.Core
{
    public class StuckReport
    {
        public string Type { get; set; }        // "LOOP" or "THRASH"
        public string Pattern { get; set; }     // e.g. "Questing→Grinding→Questing→Grinding"
        public int Occurrences { get; set; }    // how many times the pattern repeated
        public TimeSpan Window { get; set; }    // time span of the detections
    }

    public class StuckDetector
    {
        // --- Config ---
        private const int MaxEntries = 20;                  // ring buffer size
        private const int LoopMinRepeats = 3;               // pattern must repeat 3x
        private const int ThrashMinEvals = 6;               // 6+ evals in window = thrash
        private static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ThrashWindow = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan CooldownAfterReport = TimeSpan.FromMinutes(2);

        // --- State ---
        private readonly List<(string Activity, DateTime Timestamp)> _history = new();
        private DateTime _lastReportTime = DateTime.MinValue;

        /// <summary>
        /// Call on every strategic eval (activity transition or stay).
        /// Pass the activity name the bot is entering/staying in.
        /// </summary>
        public void RecordTransition(string activityName)
        {
            _history.Add((activityName, DateTime.UtcNow));

            // Trim old entries
            while (_history.Count > MaxEntries)
                _history.RemoveAt(0);
        }

        /// <summary>
        /// Check for stuck patterns. Returns null if not stuck.
        /// Respects a 2-minute cooldown between reports to avoid spam.
        /// </summary>
        public StuckReport Check()
        {
            if (DateTime.UtcNow - _lastReportTime < CooldownAfterReport)
                return null;

            var thrash = CheckThrash();
            if (thrash != null)
            {
                _lastReportTime = DateTime.UtcNow;
                return thrash;
            }

            var loop = CheckLoop();
            if (loop != null)
            {
                _lastReportTime = DateTime.UtcNow;
                return loop;
            }

            return null;
        }

        /// <summary>
        /// Reset after a level-up or nuke — bot's situation has changed.
        /// </summary>
        public void Reset()
        {
            _history.Clear();
            _lastReportTime = DateTime.MinValue;
        }

        // ── Thrash detection ──
        // 6+ strategic evals in 90 seconds = bot is spinning
        private StuckReport CheckThrash()
        {
            var cutoff = DateTime.UtcNow - ThrashWindow;
            var recent = _history.Where(h => h.Timestamp >= cutoff).ToList();

            if (recent.Count < ThrashMinEvals)
                return null;

            var activities = string.Join("→", recent.Select(r => r.Activity));
            return new StuckReport
            {
                Type = "THRASH",
                Pattern = activities,
                Occurrences = recent.Count,
                Window = ThrashWindow
            };
        }

        // ── Loop detection ──
        // Look for repeating sequences of length 2 or 3 in recent history.
        // Example: [Q, G, Q, G, Q, G] → pattern [Q, G] repeats 3x
        private StuckReport CheckLoop()
        {
            var cutoff = DateTime.UtcNow - LoopWindow;
            var recent = _history
                .Where(h => h.Timestamp >= cutoff)
                .Select(h => h.Activity)
                .ToList();

            if (recent.Count < 4) // need at least 4 entries for a 2-element pattern × 2 repeats
                return null;

            // Try pattern lengths 2 and 3
            for (int patLen = 2; patLen <= 3; patLen++)
            {
                if (recent.Count < patLen * LoopMinRepeats)
                    continue;

                // Extract the candidate pattern from the tail
                var tail = recent.Skip(recent.Count - patLen).ToList();
                int repeats = 0;

                // Walk backward through the history counting matches
                int pos = recent.Count - patLen;
                while (pos >= 0)
                {
                    bool match = true;
                    for (int j = 0; j < patLen && pos + j < recent.Count; j++)
                    {
                        if (recent[pos + j] != tail[j])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        repeats++;
                        pos -= patLen;
                    }
                    else
                    {
                        break; // pattern broken — stop counting
                    }
                }

                if (repeats >= LoopMinRepeats)
                {
                    var patternStr = string.Join("→", tail);
                    return new StuckReport
                    {
                        Type = "LOOP",
                        Pattern = patternStr,
                        Occurrences = repeats,
                        Window = LoopWindow
                    };
                }
            }

            return null;
        }
    }
}
