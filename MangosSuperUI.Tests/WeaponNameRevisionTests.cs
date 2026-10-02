using System.Text.Json;
using System.Text.Json.Nodes;
using MangosSuperUI.Services.WeaponForge;
using Xunit;

namespace MangosSuperUI.Tests;

public class WeaponNameRevisionTests
{
    private static (WeaponRenameRow Registry,SortedDictionary<string,string> World) Fixture()
    {
        var row=DonorItemTemplateFixture.Columns.Zip(DonorItemTemplateFixture.DonorValues).ToDictionary(x=>x.First,x=>(object)x.Second);
        row["entry"]="1102488";row["display_id"]="76331";row["name"]="Old Name";
        row["quality"]="4";row["dmg_min1"]="31.25";row["description"]="Owner's preserved description";
        var world=WeaponNameRevision.Snapshot(row);
        var sql=WeaponItemTemplateSql.Build(1102488,"Old Name",76331,"original-build",world);
        return(new(){ItemEntry=1102488,DisplayId=76331,BuildId="original-build",SourceKind="glb_import",ManifestCount=1,
            GameplayJson="{\"name\":\"Old Name\",\"weaponType\":\"sword1h\",\"custom\":{\"damage\":31.25}}",SqlText=sql.Text,SqlSha256=sql.Sha256},world);
    }
    [Fact]
    public void NewNamePreservesEveryLiveGameplayColumnAndNestedRegistryValue()
    {
        var (registry,world)=Fixture();string before=JsonSerializer.Serialize(world);
        string name="The Orchard’s Last Winter'); --";
        var plan=WeaponNameRevision.Compile(registry,world,"Old Name",name,"same-server-InnoDB");
        Assert.Equal(before,JsonSerializer.Serialize(world));Assert.Equal(130,world.Count);
        var after=new SortedDictionary<string,string>(world,StringComparer.Ordinal){["name"]=name};
        Assert.Equal(WeaponItemTemplateSql.Build(registry.ItemEntry,name,registry.DisplayId,registry.BuildId,after),plan.Sql);
        Assert.Equal(WeaponNameRevision.Hash(JsonSerializer.Serialize(after)),plan.AfterWorldSha256);
        var gameplay=JsonNode.Parse(plan.GameplayJson)!;Assert.Equal(31.25,gameplay["custom"]!["damage"]!.GetValue<double>());
        Assert.Equal(name,gameplay["name"]!.GetValue<string>());Assert.DoesNotContain(name,plan.Sql.Text);
        WeaponNameRevision.ValidateApply(plan,plan.BeforeWorldSha256,plan.Token);
    }
    [Theory]
    [InlineData("world-name")][InlineData("registry-name")][InlineData("display")][InlineData("class")]
    [InlineData("subclass")][InlineData("sql-hash")][InlineData("shared")]
    public void UnmatchedIdentityOrStateRefusesNamePlan(string change)
    {
        var(r,w)=Fixture();
        if(change=="world-name")w["name"]="Changed";
        if(change=="registry-name")r.GameplayJson=r.GameplayJson.Replace("Old Name","Changed");
        if(change=="display")w["display_id"]="76332";
        if(change=="class")w["class"]="4";
        if(change=="subclass")w["subclass"]="10";
        if(change=="sql-hash")r.SqlText+="\n";
        if(change=="shared")r.ManifestCount=2;
        Assert.Throws<InvalidOperationException>(()=>WeaponNameRevision.Compile(r,w,"Old Name","New Name","same"));
    }
    [Theory]
    [InlineData("stats")][InlineData("name")][InlineData("registry")][InlineData("server")]
    public void AnyReviewedFullRowOrManifestChangeInvalidatesApply(string change)
    {
        var(r,w)=Fixture();var first=WeaponNameRevision.Compile(r,w,"Old Name","New Name","same");
        if(change=="stats")w["quality"]="5";
        if(change=="registry")r.GameplayJson=r.GameplayJson.Replace("31.25","40.5");
        var next=WeaponNameRevision.Compile(r,w,"Old Name",change=="name"?"Different":"New Name",change=="server"?"other":"same");
        Assert.Throws<InvalidOperationException>(()=>WeaponNameRevision.ValidateApply(next,first.BeforeWorldSha256,first.Token));
    }
    [Theory]
    [InlineData("one","two","InnoDB","InnoDB")]
    [InlineData("one","one","InnoDB","MyISAM")]
    [InlineData("one","one","MyISAM","InnoDB")]
    [InlineData("one","one","unavailable","InnoDB")]
    public void NonAtomicDatabaseTopologyIsBlocked(string admin,string world,string ae,string we)=>
        Assert.Throws<InvalidOperationException>(()=>WeaponNameRevision.RequireAtomic(admin,world,ae,we));
    [Fact]
    public void UnknownMissingNullColumnsCannotProducePartialPublishSql()
    {
        var(_,w)=Fixture();var row=w.ToDictionary(p=>p.Key,p=>(object)p.Value);row.Remove("quality");
        Assert.Throws<InvalidOperationException>(()=>WeaponNameRevision.Snapshot(row));
        row["quality"]=null!;Assert.Throws<InvalidOperationException>(()=>WeaponNameRevision.Snapshot(row));
        row["quality"]=4;row["surprise_stat"]=3;Assert.Throws<InvalidOperationException>(()=>WeaponNameRevision.Snapshot(row));
    }
    [Fact]
    public async Task RegistryFailureRollsBackEarlierWorldRename()
    {
        using var db=new WeaponArtRevisionTests.TransactionDatabase {Art=["Old Name","old-gameplay","old-sql"]};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>CustomWeaponBuildService.RunArtTransactionAsync(db,tx=>{
            db.Art[0]="New Name";db.Art[1]="new-gameplay";throw new InvalidOperationException("publish SQL update failure");
        }));
        Assert.Equal(new[]{"Old Name","old-gameplay","old-sql"},db.Art);Assert.True(db.RolledBack);Assert.False(db.Committed);
    }
}
