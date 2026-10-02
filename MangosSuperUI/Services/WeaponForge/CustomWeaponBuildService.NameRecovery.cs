using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;

namespace MangosSuperUI.Services.WeaponForge;

public sealed partial class CustomWeaponBuildService
{
    private string NameJournalDirectory => Path.Combine(_env.ContentRootPath,"App_Data","weapon-name-revisions");
    private string PendingNamePath(long itemEntry) => Path.Combine(NameJournalDirectory,$"pending-{itemEntry}.json");
    private void PersistNameJournal(WeaponNameJournal journal)
    {
        Directory.CreateDirectory(NameJournalDirectory);
        WeaponNameJournal.DurableWrite(Path.Combine(NameJournalDirectory,journal.Token+".journal.json"),journal);
        WeaponNameJournal.DurableWrite(PendingNamePath(journal.BeforeRegistry.ItemEntry),journal);
    }
    private void RequireNoPendingName(long itemEntry)
    {
        string path=PendingNamePath(itemEntry);if(!File.Exists(path))return;
        var journal=JsonSerializer.Deserialize<WeaponNameJournal>(File.ReadAllBytes(path))
            ??throw new InvalidOperationException("Unreadable name recovery journal; no rename performed.");
        if(journal.Phase=="Prepared")throw new InvalidOperationException(
            $"Name revision {journal.Token} requires explicit RecoverName inspection before another preview or apply. No writes performed.");
    }
    private sealed class NameLease(DbConnection connection,string key):IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { if(connection.State==ConnectionState.Open)await connection.ExecuteAsync("SELECT RELEASE_LOCK(@key)",new{key}); }
            catch(DbException) { /* A lost physical session releases its server advisory locks. */ }
        }
    }
    private static async Task<NameLease> AcquireNameLeaseAsync(DbConnection connection,long entry)
    {
        string key="msui.weapon-name."+entry;
        int acquired=await connection.ExecuteScalarAsync<int>("SELECT GET_LOCK(@key,0)",new{key});
        if(acquired!=1)throw new InvalidOperationException("This item's name revision is already running.");
        return new NameLease(connection,key);
    }

    private static async Task RequireLegacyNameEnginesAsync(DbConnection connection,string worldSchema,IDbTransaction tx)
    {
        string adminSchema=connection.Database;
        var tables=(await connection.QueryAsync<(string Schema,string Name,string Engine)>(@"SELECT TABLE_SCHEMA,TABLE_NAME,ENGINE FROM information_schema.TABLES
            WHERE (TABLE_SCHEMA=@adminSchema AND TABLE_NAME='custom_weapon_item_manifest') OR (TABLE_SCHEMA=@worldSchema AND TABLE_NAME='item_template')",new{adminSchema,worldSchema},tx)).ToList();
        string manifest=tables.SingleOrDefault(t=>t.Schema==adminSchema&&t.Name=="custom_weapon_item_manifest").Engine??"unavailable";
        string world=tables.SingleOrDefault(t=>t.Schema==worldSchema&&t.Name=="item_template").Engine??"unavailable";
        WeaponNameJournal.RequireSupported("locked-server","locked-server",manifest,world);
    }

    private async Task ApplyLegacyNameAsync(DbConnection connection,string worldTable,string manifestTable,string worldSchema,string databaseBinding,
        WeaponRenameRow before,SortedDictionary<string,string> world,WeaponNameRevisionPlan plan,
        Func<IDbTransaction?,Task<(WeaponRenameRow Registry,SortedDictionary<string,string> World)>> read)
    {
        var journal=WeaponNameJournal.Create(before,world,plan,worldSchema,databaseBinding);
        PersistNameJournal(journal); // Durable recovery inputs precede the nontransactional statement.
        Exception? failure=null;
        try {
            await RunArtTransactionAsync(connection,async tx=>{
                var locked=await read(tx);
                await RequireLegacyNameEnginesAsync(connection,worldSchema,tx);
                if(journal.Classify(locked.Registry,locked.World)!="Compensated")
                    throw new InvalidOperationException("World or Forge state changed before the guarded name write.");
                var cas=WeaponNameJournal.CompareAndSwap(worldTable,journal.BeforeWorld,plan.NewName);
                if(await connection.ExecuteAsync(cas.Sql,cas.Parameters,tx)!=1)
                    throw new InvalidOperationException("Full-row world comparison changed; no name write matched.");
                int changed=await connection.ExecuteAsync($"UPDATE {manifestTable} SET gameplay_json=@gameplay,sql_text=@sql,sql_sha256=@sha WHERE build_id=@build AND item_entry=@entry AND display_id=@display AND sql_sha256=@oldSha AND BINARY gameplay_json=BINARY @oldGameplay",
                    new{gameplay=plan.GameplayJson,sql=plan.Sql.Text,sha=plan.Sql.Sha256,build=before.BuildId,entry=before.ItemEntry,display=before.DisplayId,oldSha=before.SqlSha256,oldGameplay=before.GameplayJson},tx);
                if(changed!=1)throw new InvalidOperationException("Forge manifest changed during the name revision.");
                var after=await read(tx);
                if(journal.Classify(after.Registry,after.World)!="Committed")throw new InvalidOperationException("Name-only postcondition failed.");
            });
        } catch(Exception ex) { failure=ex; }
        // Read committed state after transaction disposal: an uncertain commit must never be
        // compensated if both rows already contain the exact intended after state.
        (string State,bool Restored) resolution=default;
        await RunArtTransactionAsync(connection,async tx=>{
            async Task<(WeaponRenameRow Registry,SortedDictionary<string,string> World)> ReadLocked(){
                var state=await read(tx);await RequireLegacyNameEnginesAsync(connection,worldSchema,tx);return state;
            }
            resolution=await journal.ResolveAsync(ReadLocked,async ()=>{
                var undo=WeaponNameJournal.CompareAndSwap(worldTable,journal.AfterWorld,journal.BeforeWorld["name"]);
                return await connection.ExecuteAsync(undo.Sql,undo.Parameters,tx)==1;
            },PersistNameJournal);
        });
        if(resolution.State!="Committed")throw new InvalidOperationException("Name revision failed; exact previous world name and Forge manifest were verified restored.",failure);
        if(failure is not null)_logger.LogWarning(failure,"Name revision {Token} verified committed after an uncertain transaction response",journal.Token);
    }

    public async Task<object> RecoverNameAsync(long itemEntry,long displayId,string expectedRevisionToken)
    {
        WeaponGeometryRevision.RequireHash(expectedRevisionToken,nameof(expectedRevisionToken));
        string path=PendingNamePath(itemEntry);
        if(!File.Exists(path))throw new InvalidOperationException("No pending name journal exists for this item.");
        await using var admin=_db.Admin();await using var world=_db.Mangos();await admin.OpenAsync();await world.OpenAsync();
        await using var nameLease=await AcquireNameLeaseAsync(admin,itemEntry);
        var journal=JsonSerializer.Deserialize<WeaponNameJournal>(File.ReadAllBytes(path))??throw new InvalidOperationException("Invalid journal.");
        if(journal.Token!=expectedRevisionToken||journal.BeforeRegistry.ItemEntry!=itemEntry||journal.BeforeRegistry.DisplayId!=displayId||journal.WorldSchema!=world.Database)
            throw new InvalidOperationException("Recovery token, item/display or world schema does not match.");
        const string identity="SELECT CONCAT(@@hostname,':',@@port,':',@@server_id)";
        string a=await admin.ExecuteScalarAsync<string>(identity)??"",w=await world.ExecuteScalarAsync<string>(identity)??"";
        if(a!=w||!string.Equals(admin.DataSource,world.DataSource,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Recovery database endpoint changed.");
        journal.RequireDatabase(WeaponNameJournal.BindDatabase(a,admin.DataSource,world.DataSource,admin.Database,world.Database));
        string table=WeaponNameRevision.Schema(world.Database)+".`item_template`";
        async Task<(WeaponRenameRow Registry,SortedDictionary<string,string> World)> Read(IDbTransaction tx) {
            var r=await admin.QuerySingleAsync<WeaponRenameRow>(RenameRowSql+" FOR UPDATE",new{itemEntry,displayId},tx);
            var rows=(await admin.QueryAsync($"SELECT * FROM {table} WHERE entry=@itemEntry FOR UPDATE",new{itemEntry},tx)).ToList();
            if(rows.Count!=1)throw new InvalidOperationException("Recovery requires the exact single patch row.");
            await RequireLegacyNameEnginesAsync(admin,world.Database,tx);
            return(r,WeaponNameRevision.Snapshot((IDictionary<string,object>)rows[0]));
        }
        (string State,bool Restored) resolution=default;
        await RunArtTransactionAsync(admin,async tx=>{
            resolution=await journal.ResolveAsync(()=>Read(tx),async ()=>{
                var undo=WeaponNameJournal.CompareAndSwap(table,journal.AfterWorld,journal.BeforeWorld["name"]);
                return await admin.ExecuteAsync(undo.Sql,undo.Parameters,tx)==1;
            },PersistNameJournal);
        });
        return new{ok=true,itemEntry,displayId,revisionToken=journal.Token,state=resolution.State,restoredWorldName=resolution.Restored,atomic=false,statsChanged=false,runtimeVerified=false};
    }
}
