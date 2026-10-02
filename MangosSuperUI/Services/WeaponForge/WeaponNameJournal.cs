using System.Text.Json;
using Dapper;

namespace MangosSuperUI.Services.WeaponForge;

internal sealed class WeaponNameJournal
{
    public string Token { get; set; } = "";
    public string WorldSchema { get; set; } = "";
    public string DatabaseBinding { get; set; } = "";
    public string Phase { get; set; } = "Prepared";
    public WeaponRenameRow BeforeRegistry { get; set; } = new();
    public WeaponRenameRow AfterRegistry { get; set; } = new();
    public SortedDictionary<string,string> BeforeWorld { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string,string> AfterWorld { get; set; } = new(StringComparer.Ordinal);
    public static string Digest<T>(T value) => WeaponNameRevision.Hash(JsonSerializer.Serialize(value));

    internal static WeaponNameJournal Create(WeaponRenameRow registry, SortedDictionary<string,string> world,
        WeaponNameRevisionPlan plan, string schema, string databaseBinding="test-fixture")
    {
        var after = JsonSerializer.Deserialize<WeaponRenameRow>(JsonSerializer.Serialize(registry))!;
        after.GameplayJson = plan.GameplayJson; after.SqlText = plan.Sql.Text; after.SqlSha256 = plan.Sql.Sha256;
        return new() { Token=plan.Token, WorldSchema=schema, DatabaseBinding=databaseBinding, BeforeRegistry=registry, AfterRegistry=after,
            BeforeWorld=new(world,StringComparer.Ordinal), AfterWorld=new(world,StringComparer.Ordinal) { ["name"]=plan.NewName } };
    }

    internal static string BindDatabase(string server,string adminEndpoint,string worldEndpoint,string adminSchema,string worldSchema)
        => JsonSerializer.Serialize(new{server,adminEndpoint,worldEndpoint,adminSchema,worldSchema});
    internal void RequireDatabase(string currentBinding)
    {
        if(string.IsNullOrWhiteSpace(DatabaseBinding)||DatabaseBinding!=currentBinding)
            throw new InvalidOperationException("Recovery database identity, configured endpoints or schemas changed from the original journal.");
    }

    internal string Classify(WeaponRenameRow registry, SortedDictionary<string,string> world)
    {
        string r=Digest(registry), w=Digest(world);
        if(r==Digest(AfterRegistry) && w==Digest(AfterWorld)) return "Committed";
        if(r==Digest(BeforeRegistry) && w==Digest(BeforeWorld)) return "Compensated";
        if(r==Digest(BeforeRegistry) && w==Digest(AfterWorld)) return "RestoreWorldName";
        return "Conflict";
    }

    internal static (string Sql, DynamicParameters Parameters) CompareAndSwap(string qualifiedTable,
        SortedDictionary<string,string> expected, string name)
    {
        // Snapshot validation binds the fixed column whitelist before identifiers enter SQL.
        WeaponNameRevision.Snapshot(expected.ToDictionary(p=>p.Key,p=>(object)p.Value));
        var args=new DynamicParameters();args.Add("replacementName",name);var conditions=new List<string>();int index=0;
        foreach(var (column,value) in expected) {
            string parameter="c"+index++;args.Add(parameter,value);
            conditions.Add(column is "name" or "description" ? $"BINARY `{column}`=BINARY @{parameter}" : $"`{column}`=@{parameter}");
        }
        return ($"UPDATE {qualifiedTable} SET name=@replacementName WHERE "+string.Join(" AND ",conditions),args);
    }

    internal static (string Sql,DynamicParameters Parameters) MatchQuery(string qualifiedTable,SortedDictionary<string,string> expected)
    {
        var guarded=CompareAndSwap(qualifiedTable,expected,expected["name"]);
        return ($"SELECT COUNT(*) FROM {qualifiedTable}"+guarded.Sql[guarded.Sql.IndexOf(" WHERE ",StringComparison.Ordinal)..],guarded.Parameters);
    }

    internal static void RequireSupported(string adminServer,string worldServer,string adminEngine,string worldEngine)
    {
        if(adminServer!=worldServer || !new[]{"InnoDB","XtraDB"}.Contains(adminEngine,StringComparer.OrdinalIgnoreCase)
            || !string.Equals(worldEngine,"MyISAM",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recoverable name revision requires the same server, a transactional Forge manifest and the existing MyISAM item table.");
    }

    internal async Task<(string State,bool Restored)> ResolveAsync(
        Func<Task<(WeaponRenameRow Registry,SortedDictionary<string,string> World)>> read,
        Func<Task<bool>> restoreWorld,Action<WeaponNameJournal> persist)
    {
        var current=await read();string state=Classify(current.Registry,current.World);bool restored=false;
        if(state=="RestoreWorldName") {
            if(!await restoreWorld())throw new InvalidOperationException($"Name recovery comparison changed; retained journal {Token}, no unrelated row overwritten.");
            restored=true;current=await read();state=Classify(current.Registry,current.World);
        }
        if(state is not ("Committed" or "Compensated"))
            throw new InvalidOperationException($"Name recovery conflicts with a later world or Forge edit; retained journal {Token}, no overwrite performed.");
        Phase=state;persist(this);return(state,restored);
    }

    internal static void DurableWrite(string path, WeaponNameJournal journal)
    {
        string pending=path+".tmp";byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(journal);
        using(var stream=new FileStream(pending,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) {
            stream.Write(bytes);stream.Flush(flushToDisk:true);
        }
        File.Move(pending,path,overwrite:true);
    }
}
