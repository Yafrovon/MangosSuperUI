using System.Text.Json.Nodes;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

public sealed class WorldPackNpcReplacementTests
{
    private static JsonObject Body(uint spawn = 10, uint original = 100, uint replacement = 7000001) =>
        new() { ["spawnGuid"] = spawn, ["originalEntry"] = original, ["replacementEntry"] = replacement };
    private static WorldPackNpcReplacements.Replacement Desired => WorldPackNpcReplacements.Parse(Body(), 3);

    [Fact] public void ReplacesOneSpawnAndRestoresOriginal()
    {
        var apply = WorldPackNpcReplacements.Plan(Desired, null, 10, 100);
        Assert.Equal(7000001u, apply.TargetEntry);
        Assert.False(apply.Restore);
        var baseline = new WorldPackNpcReplacements.Baseline { SpawnGuid = 10, OriginalEntry = 100, InstalledEntry = 7000001, PackId = 3 };
        var restore = WorldPackNpcReplacements.Plan(null, baseline, 10, 7000001);
        Assert.True(restore.Restore);
        Assert.Equal(100u, restore.TargetEntry);
    }

    [Theory] [InlineData(100u)] [InlineData(7000001u)] [InlineData(7000002u)]
    public void InterruptedInstallCanRetryOrDisable(uint observed)
    {
        var b = new WorldPackNpcReplacements.Baseline { SpawnGuid = 10, OriginalEntry = 100, InstalledEntry = 7000001, PendingEntry = 7000002, PackId = 3 };
        Assert.Equal(100u, WorldPackNpcReplacements.Plan(null, b, 10, observed).TargetEntry);
        Assert.Equal(7000001u, WorldPackNpcReplacements.Plan(Desired, b, 10, observed).TargetEntry);
    }

    [Fact] public void OutsideEditOrMissingSpawnIsNeverOverwritten()
    {
        Assert.Throws<InvalidOperationException>(() => WorldPackNpcReplacements.Plan(Desired, null, 10, 101));
        Assert.Throws<InvalidOperationException>(() => WorldPackNpcReplacements.Plan(Desired, null, 10, null));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RetryingPreviouslyPendingEntrySurvivesAnotherCrashOnEitherSideOfCas(bool disable)
    {
        // Attempt one wrote B into the world, then crashed before recording it as installed.
        var baseline = new WorldPackNpcReplacements.Baseline
        {
            SpawnGuid = 10, OriginalEntry = 100, InstalledEntry = 7000001, PendingEntry = 7000002, PackId = 3,
        };
        var desired = disable ? null : WorldPackNpcReplacements.Parse(Body(replacement: 7000003), 3);
        var plan = WorldPackNpcReplacements.Plan(desired, baseline, 10, 7000002);
        var durable = WorldPackNpcReplacements.BeforeWrite(plan);
        Assert.Equal(100u, durable.OriginalEntry);
        Assert.Equal(7000002u, durable.InstalledEntry);
        Assert.Equal(disable ? 100u : 7000003u, durable.PendingEntry);

        // Crash two BEFORE CAS: world still B, and the overwritten pending field must not lose it.
        var beforeCasRetry = WorldPackNpcReplacements.Plan(desired, durable, 10, 7000002);
        Assert.Equal(plan.TargetEntry, beforeCasRetry.TargetEntry);
        var durableRetry = WorldPackNpcReplacements.BeforeWrite(beforeCasRetry);

        // Or crash AFTER CAS: the new target is accepted too; disable can always restore original.
        var afterCasRetry = WorldPackNpcReplacements.Plan(desired, durableRetry, 10, plan.TargetEntry);
        Assert.Equal(plan.TargetEntry, afterCasRetry.TargetEntry);
        Assert.Equal(100u, WorldPackNpcReplacements.Plan(null, durableRetry, 10, plan.TargetEntry).TargetEntry);
        Assert.Throws<InvalidOperationException>(() => WorldPackNpcReplacements.Plan(desired, durableRetry, 10, 123));
    }

    [Fact] public void DisabledRetryRetainsObservedPendingEntryUntilRestorationCompletes()
    {
        var interrupted = new WorldPackNpcReplacements.Baseline
        {
            SpawnGuid = 10, OriginalEntry = 100, InstalledEntry = 7000001, PendingEntry = 7000002, PackId = 3,
        };
        var reapply = WorldPackNpcReplacements.Plan(Desired, interrupted, 10, 7000002);
        var beforeReapply = WorldPackNpcReplacements.BeforeWrite(reapply); // Crash before world B -> A.
        var disable = WorldPackNpcReplacements.Plan(null, beforeReapply, 10, 7000002);
        var beforeRestore = WorldPackNpcReplacements.BeforeWrite(disable); // Crash again before world B -> original.
        Assert.Equal(100u, WorldPackNpcReplacements.Plan(null, beforeRestore, 10, 7000002).TargetEntry);
        Assert.Equal(100u, WorldPackNpcReplacements.Plan(null, beforeRestore, 10, 100).TargetEntry);
    }

    [Fact] public void ChangingRecordedOriginalIsRejected()
    {
        var b = new WorldPackNpcReplacements.Baseline { SpawnGuid = 10, OriginalEntry = 101 };
        Assert.Throws<InvalidOperationException>(() => WorldPackNpcReplacements.Plan(Desired, b, 10, 101));
    }

    [Fact] public void CompletedRestoreAllowsANewValidatedStockGeneration()
    {
        var restored = new WorldPackNpcReplacements.Baseline { SpawnGuid = 10, OriginalEntry = 100, PackId = 3 };
        var fresh = WorldPackNpcReplacements.Parse(Body(original: 101, replacement: 7000002), 4);
        var change = WorldPackNpcReplacements.Plan(fresh, restored, 10, 101);
        var durable = WorldPackNpcReplacements.BeforeWrite(change);
        Assert.Equal(101u, durable.OriginalEntry);
        Assert.Equal(101u, durable.InstalledEntry);
        Assert.Equal(7000002u, durable.PendingEntry);
        Assert.Equal(4, durable.PackId);
        Assert.Equal(101u, WorldPackNpcReplacements.Plan(null, durable, 10, 7000002).TargetEntry);
        Assert.Throws<InvalidOperationException>(() => WorldPackNpcReplacements.Plan(fresh, restored, 10, 102));
        restored.InstalledEntry = 7000001;
        Assert.Throws<InvalidOperationException>(() => WorldPackNpcReplacements.Plan(fresh, restored, 10, 7000001));
        restored.InstalledEntry = null;
        restored.PendingEntry = 7000001;
        Assert.Throws<InvalidOperationException>(() => WorldPackNpcReplacements.Plan(fresh, restored, 10, 100));
    }

    [Fact] public void OnlyAStockSpawnCanPointToAReservedTemplate()
    {
        Assert.Throws<ArgumentException>(() => WorldPackNpcReplacements.Parse(Body(spawn: 1500000)));
        Assert.Throws<ArgumentException>(() => WorldPackNpcReplacements.Parse(Body(replacement: 100)));
        Assert.Throws<ArgumentException>(() => WorldPackNpcReplacements.Parse(Body(original: 7000001)));
    }

    [Fact] public void TemplateMustBelongToTheReplacingPackAndConflictsAreRejected()
    {
        var replacement = new DocRow { PackId = 3, Kind = "npc-replacement", DocKey = "10", Body = Body().ToJsonString() };
        var template = new DocRow { PackId = 3, Kind = "dbrow:creature_template", DocKey = "7000001|0", Body = "{\"entry\":7000001}" };
        Assert.Single(WorldPackNpcReplacements.ReadDesired(new[] { replacement, template }));
        template.PackId = 4;
        Assert.Throws<ArgumentException>(() => WorldPackNpcReplacements.ReadDesired(new[] { replacement, template }));
        template.PackId = 3;
        Assert.Throws<ArgumentException>(() => WorldPackNpcReplacements.ReadDesired(new[] { replacement, replacement, template }));
    }

    [Theory] [InlineData("creature_questrelation")] [InlineData("creature_involvedrelation")]
    public void ReservedNpcMayRetainStockQuestsButCannotEditStockRelations(string table)
    {
        Assert.Equal("7000001|10", WorldPackContent.RowKey(table, new() { ["id"] = 7000001, ["quest"] = 10 }));
        Assert.Equal("100|7000001", WorldPackContent.RowKey(table, new() { ["id"] = 100, ["quest"] = 7000001 }));
        Assert.Throws<ArgumentException>(() => WorldPackContent.RowKey(table, new() { ["id"] = 100, ["quest"] = 10 }));
        Assert.Equal("7000001|10", WorldPackContent.RowKey(table, new() { ["ID"] = 7000001, ["QUEST"] = 10 }));
    }

    [Theory] [InlineData("null")] [InlineData("0")] [InlineData("-1")] [InlineData("\"invalid\"")] [InlineData("4294967296")]
    public void ReservedNpcRelationsStillRequireValidPositiveQuests(string quest)
    {
        foreach (string table in new[] { "creature_questrelation", "creature_involvedrelation" })
            Assert.Throws<ArgumentException>(() => WorldPackContent.RowKey(table,
                new() { ["id"] = 7000001, ["quest"] = JsonNode.Parse(quest) }));
    }

    [Fact] public void ContentAuditCountsReplacementAsSpawnedAndReachableQuestGiver()
    {
        static DocRow Doc(string kind, string key, JsonObject body) => new() { PackId = 3, Kind = kind, DocKey = key, Body = body.ToJsonString() };
        var docs = new List<DocRow>
        {
            Doc("npc-replacement", "10", Body()),
            Doc("dbrow:creature_template", "7000001|0", new() { ["entry"] = 7000001, ["name"] = "Replacement giver", ["npc_flags"] = 2 }),
            Doc("dbrow:creature_template", "7000002|0", new() { ["entry"] = 7000002, ["name"] = "Unplaced control" }),
            Doc("dbrow:quest_template", "7000010", new() { ["entry"] = 7000010, ["Title"] = "Replacement quest", ["Details"] = "Meet the giver.", ["Objectives"] = "Talk to the giver." }),
            Doc("dbrow:creature_questrelation", "7000001|7000010", new() { ["id"] = 7000001, ["quest"] = 7000010 }),
            Doc("dbrow:creature_involvedrelation", "7000001|7000010", new() { ["id"] = 7000001, ["quest"] = 7000010 }),
        };
        var findings = new WorldPackAudit(new()
        {
            Stock = _ => null, Built = _ => null, MapDirs = new(), Docs = docs, Placements = new(),
        }).RunContent();
        Assert.DoesNotContain(findings, f => f.Check == "C2" && f.Subject.Contains("Replacement giver"));
        Assert.Contains(findings, f => f.Check == "C2" && f.Subject.Contains("Unplaced control"));
        Assert.DoesNotContain(findings, f => f.Check == "C8" && f.Message.Contains("not spawned anywhere"));
        Assert.DoesNotContain(findings, f => f.Check == "C" && f.Severity == "error");
    }
}
