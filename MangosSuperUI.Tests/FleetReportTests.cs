using MangosSuperUI.BotLogic.Brain;
using MangosSuperUI.BotLogic.Core;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>
/// The fleet report split into a cheap periodic summary and an on-demand detail
/// table, and the detail table stopped sorting the whole fleet to print 60 rows.
/// These tests pin the behaviour that split has to preserve.
/// </summary>
public sealed class FleetReportTests
{
    private static BotContext Bot(int guid, string name, bool stalled = false, bool feedStale = false)
        => new()
        {
            Guid = guid,
            Name = name,
            Level = 10,
            Stalled = stalled,
            SensoryFeedStale = feedStale
        };

    [Fact]
    public void RenderSummary_KeepsRollupsAndOmitsPerBotRows()
    {
        var bots = new[] { Bot(7, "Alpha"), Bot(8, "Bravo", stalled: true) };

        string summary = FleetReport.RenderSummary(bots);

        Assert.Contains("FLEET  2 bots", summary);
        Assert.Contains("stalled: 1/2", summary);
        // The whole point of the summary is that it does not carry per-bot rows:
        // at thousands of bots those were built every 30 s for an Info log.
        Assert.DoesNotContain("[7]", summary);
        Assert.DoesNotContain("[8]", summary);
    }

    [Fact]
    public void RenderDetailed_CapsRowsAndReportsTheHiddenRemainder()
    {
        BotContext[] bots = Enumerable.Range(1, 100)
            .Select(i => Bot(i, $"Bot{i:D3}"))
            .ToArray();

        string report = FleetReport.RenderDetailed(bots, maxRows: 10);

        Assert.Equal(10, CountRows(report));
        Assert.Contains("… 90 more bots", report);
    }

    [Fact]
    public void RenderDetailed_ShowsStaleThenStalledBotsFirst()
    {
        var bots = new[]
        {
            Bot(1, "Aaa"),
            Bot(2, "Bbb"),
            Bot(3, "Ccc", stalled: true),
            Bot(4, "Ddd", feedStale: true),
            Bot(5, "Eee")
        };

        string report = FleetReport.RenderDetailed(bots, maxRows: 2);

        Assert.Equal(2, CountRows(report));
        Assert.Contains("[4]", report);   // feed-stale outranks everything
        Assert.Contains("[3]", report);   // then stalled
        Assert.DoesNotContain("[1]", report);
    }

    [Fact]
    public void RenderDetailed_KeepsBothBotsWhenNamesCollide()
    {
        // The bounded selector is a set, so its comparer must be a TOTAL order.
        // Without the guid tie-break, one of these two would be silently dropped.
        var bots = new[] { Bot(11, "Same"), Bot(12, "Same"), Bot(13, "Zed") };

        string report = FleetReport.RenderDetailed(bots, maxRows: 2);

        Assert.Equal(2, CountRows(report));
        Assert.Contains("[11]", report);
        Assert.Contains("[12]", report);
    }

    [Fact]
    public void RenderDetailed_MatchesFullSortOrderForTheRowsItKeeps()
    {
        BotContext[] bots = Enumerable.Range(1, 50)
            .Select(i => Bot(i, $"Bot{i:D2}", stalled: i % 7 == 0, feedStale: i % 11 == 0))
            .ToArray();

        string report = FleetReport.RenderDetailed(bots, maxRows: 12);

        int[] expected = bots
            .OrderByDescending(b => b.SensoryFeedStale)
            .ThenByDescending(b => b.Stalled)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Guid)
            .Take(12)
            .Select(b => b.Guid)
            .ToArray();

        Assert.Equal(expected, RowGuids(report));
    }

    private static int CountRows(string report) => RowGuids(report).Length;

    private static int[] RowGuids(string report) => report
        .Split('\n')
        .Where(line => line.StartsWith("  [", StringComparison.Ordinal))
        .Select(line => int.Parse(line[3..line.IndexOf(']')]))
        .ToArray();
}
