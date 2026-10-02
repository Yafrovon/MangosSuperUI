using System.Text.Json;
using MangosSuperUI.Services.WeaponForge;
using Xunit;

namespace MangosSuperUI.Tests;

public class WeaponNameRecoveryTests
{
    private static WeaponNameJournal Fixture()
    {
        var values=DonorItemTemplateFixture.Columns.Zip(DonorItemTemplateFixture.DonorValues).ToDictionary(x=>x.First,x=>(object)x.Second);
        values["entry"]="1102488";values["display_id"]="76331";values["name"]="Original";
        var world=WeaponNameRevision.Snapshot(values);
        var sql=WeaponItemTemplateSql.Build(1102488,"Original",76331,"build",world);
        var registry=new WeaponRenameRow {ItemEntry=1102488,DisplayId=76331,BuildId="build",SourceKind="glb_import",ManifestCount=1,
            GameplayJson="{\"name\":\"Original\",\"weaponType\":\"sword1h\"}",SqlText=sql.Text,SqlSha256=sql.Sha256};
        return WeaponNameJournal.Create(registry,world,WeaponNameRevision.Compile(registry,world,"Original","Hero's Oath","MyISAM"),"world");
    }

    [Fact]
    public async Task InterruptedWorldWriteIsCompensatedAndVerifiedBeforeJournalCompletes()
    {
        var journal=Fixture();var registry=journal.BeforeRegistry;var world=journal.AfterWorld;int writes=0,persisted=0;
        var result=await journal.ResolveAsync(()=>Task.FromResult((registry,world)),()=>{writes++;world=journal.BeforeWorld;return Task.FromResult(true);},j=>{
            Assert.Equal("Original",world["name"]);Assert.Equal("Compensated",j.Phase);persisted++;
        });
        Assert.Equal(("Compensated",true),result);Assert.Equal(1,writes);Assert.Equal(1,persisted);
    }

    [Theory]
    [InlineData(true,"Committed")][InlineData(false,"Compensated")]
    public async Task UncertainCommitOrUntouchedStateNeverRewritesWorld(bool committed,string expected)
    {
        var j=Fixture();int writes=0;
        var result=await j.ResolveAsync(()=>Task.FromResult((committed?j.AfterRegistry:j.BeforeRegistry,committed?j.AfterWorld:j.BeforeWorld)),
            ()=>{writes++;return Task.FromResult(true);},_=>{});
        Assert.Equal(expected,result.State);Assert.False(result.Restored);Assert.Equal(0,writes);
    }

    [Theory]
    [InlineData("world")][InlineData("registry")]
    public async Task LaterEditsRefuseCompensationAndKeepPreparedJournal(string change)
    {
        var j=Fixture();var world=new SortedDictionary<string,string>(j.AfterWorld,StringComparer.Ordinal);
        var registry=JsonSerializer.Deserialize<WeaponRenameRow>(JsonSerializer.Serialize(j.BeforeRegistry))!;
        if(change=="world")world["quality"]="6";else registry.GameplayJson+=" ";
        int writes=0;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>j.ResolveAsync(()=>Task.FromResult((registry,world)),
            ()=>{writes++;return Task.FromResult(true);},_=>throw new Exception("Must not finalize")));
        Assert.Equal(0,writes);Assert.Equal("Prepared",j.Phase);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CompensationMustMatchAndActuallyRestoreBeforeFinalization(bool matched)
    {
        var j=Fixture();int persist=0;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>j.ResolveAsync(()=>Task.FromResult((j.BeforeRegistry,j.AfterWorld)),
            ()=>Task.FromResult(matched),_=>persist++));
        Assert.Equal(0,persist);Assert.Equal("Prepared",j.Phase);
    }

    [Fact]
    public void GuardedWorldStatementIncludesEveryColumnAndOnlyAssignsName()
    {
        var j=Fixture();string hostile="x'); DROP TABLE item_template;--";
        var command=WeaponNameJournal.CompareAndSwap("`world`.`item_template`",j.BeforeWorld,hostile);
        Assert.StartsWith("UPDATE `world`.`item_template` SET name=@replacementName WHERE ",command.Sql);
        foreach(string column in DonorItemTemplateFixture.Columns)Assert.Contains("`"+column+"`=",command.Sql);
        Assert.Equal(131,command.Parameters.ParameterNames.Count());Assert.DoesNotContain(hostile,command.Sql);
        Assert.Equal(hostile,command.Parameters.Get<string>("replacementName"));
        Assert.Contains("BINARY `description`=BINARY",command.Sql);
    }

    [Fact]
    public void JournalRoundtripRetainsExactBeforeAndAfterAndCanResumeCompensation()
    {
        var journal=Fixture();string folder=Path.Combine(Path.GetTempPath(),"msui-name-journal-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"journal.json");
        try {
            WeaponNameJournal.DurableWrite(path,journal);
            var restored=JsonSerializer.Deserialize<WeaponNameJournal>(File.ReadAllBytes(path))!;
            Assert.Equal("RestoreWorldName",restored.Classify(journal.BeforeRegistry,journal.AfterWorld));
            Assert.Equal("Committed",restored.Classify(journal.AfterRegistry,journal.AfterWorld));
            Assert.Equal("Prepared",restored.Phase);Assert.Equal(journal.Token,restored.Token);
            Assert.False(File.Exists(path+".tmp"));
        } finally {File.Delete(path);File.Delete(path+".tmp");Directory.Delete(folder);}
    }

    [Fact]
    public void RecoveryTopologyDoesNotPermitNontransactionalRegistryOrAnotherServer()
    {
        WeaponNameJournal.RequireSupported("same","same","InnoDB","MyISAM");
        Assert.Throws<InvalidOperationException>(()=>WeaponNameJournal.RequireSupported("one","two","InnoDB","MyISAM"));
        Assert.Throws<InvalidOperationException>(()=>WeaponNameJournal.RequireSupported("same","same","MyISAM","MyISAM"));
    }

    [Theory]
    [InlineData("server")][InlineData("admin-endpoint")][InlineData("world-endpoint")][InlineData("admin-schema")][InlineData("world-schema")]
    public void RecoveryCannotFollowRepointedConnectionsIntoAClone(string changed)
    {
        var j=Fixture();j.DatabaseBinding=WeaponNameJournal.BindDatabase("server","admin","world","forge","game");
        j.RequireDatabase(j.DatabaseBinding);
        string other=WeaponNameJournal.BindDatabase(changed=="server"?"clone":"server",changed=="admin-endpoint"?"clone":"admin",
            changed=="world-endpoint"?"clone":"world",changed=="admin-schema"?"clone":"forge",changed=="world-schema"?"clone":"game");
        Assert.Throws<InvalidOperationException>(()=>j.RequireDatabase(other));
    }

    [Fact]
    public void FractionalFloatSnapshotRetainsExactStoredValueForDoubleComparison()
    {
        var j=Fixture();var row=j.BeforeWorld.ToDictionary(p=>p.Key,p=>(object)p.Value);row["dmg_min1"]=.15f;row["dmg_max1"]=.35d;
        var snapshot=WeaponNameRevision.Snapshot(row);
        Assert.Equal("0.15000000596046448",snapshot["dmg_min1"]);Assert.Equal("0.35",snapshot["dmg_max1"]);
        var query=WeaponNameJournal.MatchQuery("`world`.`item_template`",snapshot);
        Assert.StartsWith("SELECT COUNT(*) FROM `world`.`item_template` WHERE ",query.Sql);Assert.DoesNotContain("UPDATE",query.Sql);
        Assert.Equal(131,query.Parameters.ParameterNames.Count());
    }
}
