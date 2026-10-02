using System.Data;
using System.Text.Json;
using Dapper;
using MangosSuperUI.Models;

namespace MangosSuperUI.Services.WeaponForge;

public sealed partial class CustomWeaponBuildService
{
    private const string RenameRowSql=@"SELECT ma.item_entry AS ItemEntry,ma.display_id AS DisplayId,ma.build_id AS BuildId,
        ma.gameplay_json AS GameplayJson,ma.sql_text AS SqlText,ma.sql_sha256 AS SqlSha256,mo.source_kind AS SourceKind,
        (SELECT COUNT(*) FROM custom_weapon_item_manifest x WHERE x.item_entry=ma.item_entry) AS ManifestCount
        FROM custom_weapon_item_manifest ma JOIN custom_weapon_display d ON d.display_id=ma.display_id
        JOIN custom_weapon_model mo ON mo.model_id=d.model_id WHERE ma.item_entry=@itemEntry AND ma.display_id=@displayId";

    public async Task<object> ReviseNameAsync(long itemEntry,long displayId,string expectedOldName,string newName,bool apply=false,
        string? expectedWorldRowSha256=null,string? expectedRevisionToken=null)
    {
        await using var admin=_db.Admin();await using var world=_db.Mangos();
        await admin.OpenAsync();await world.OpenAsync();
        const string identitySql="SELECT CONCAT(@@hostname,':',@@port,':',@@server_id)";
        string adminIdentity=await admin.ExecuteScalarAsync<string>(identitySql)??"",worldIdentity=await world.ExecuteScalarAsync<string>(identitySql)??"";
        // A single Admin connection performs both writes through qualified schemas. No distributed transaction.
        string adminSchema=admin.Database,worldSchema=world.Database;
        string worldTable=WeaponNameRevision.Schema(worldSchema)+".`item_template`";
        string manifestTable=WeaponNameRevision.Schema(adminSchema)+".`custom_weapon_item_manifest`";
        var engines=await admin.QueryAsync<(string Schema,string Name,string Engine)>(@"SELECT TABLE_SCHEMA,TABLE_NAME,ENGINE FROM information_schema.TABLES
            WHERE (TABLE_SCHEMA=@adminSchema AND TABLE_NAME='custom_weapon_item_manifest') OR (TABLE_SCHEMA=@worldSchema AND TABLE_NAME='item_template')",new{adminSchema,worldSchema});
        string adminEngine=engines.SingleOrDefault(x=>x.Schema==adminSchema&&x.Name=="custom_weapon_item_manifest").Engine??"unavailable";
        string worldEngine=engines.SingleOrDefault(x=>x.Schema==worldSchema&&x.Name=="item_template").Engine??"unavailable";
        bool sameEndpoint=string.Equals(admin.DataSource,world.DataSource,StringComparison.OrdinalIgnoreCase);
        bool recoverable=string.Equals(worldEngine,"MyISAM",StringComparison.OrdinalIgnoreCase);
        string configuredIdentity=adminIdentity+(sameEndpoint?"":":different-configured-endpoint");
        if(recoverable)WeaponNameJournal.RequireSupported(configuredIdentity,worldIdentity,adminEngine,worldEngine);
        else WeaponNameRevision.RequireAtomic(configuredIdentity,worldIdentity,adminEngine,worldEngine);
        await using var nameLease=await AcquireNameLeaseAsync(admin,itemEntry);
        RequireNoPendingName(itemEntry);
        string databaseBinding=WeaponNameJournal.BindDatabase(adminIdentity,admin.DataSource,world.DataSource,adminSchema,worldSchema);
        string atomicBinding=JsonSerializer.Serialize(new{databaseBinding,adminEngine,worldEngine,recoverable});
        async Task<(WeaponRenameRow Registry,SortedDictionary<string,string> World)> Read(IDbTransaction? tx=null) {
            var r=await admin.QuerySingleOrDefaultAsync<WeaponRenameRow>(RenameRowSql+(tx is null?"":" FOR UPDATE"),new{itemEntry,displayId},tx)
                ??throw new InvalidOperationException("Registered item/display pair unavailable.");
            var rows=(await admin.QueryAsync($"SELECT * FROM {worldTable} WHERE entry=@itemEntry"+(tx is null?"":" FOR UPDATE"),new{itemEntry},tx)).ToList();
            if(rows.Count!=1)throw new InvalidOperationException("Name revision requires exactly one world patch row for this custom item.");
            return (r,WeaponNameRevision.Snapshot((IDictionary<string,object>)rows[0]));
        }
        var before=await Read();var plan=WeaponNameRevision.Compile(before.Registry,before.World,expectedOldName,newName,atomicBinding);
        var exactMatch=WeaponNameJournal.MatchQuery(worldTable,before.World);
        if(await admin.ExecuteScalarAsync<int>(exactMatch.Sql,exactMatch.Parameters)!=1)
            throw new InvalidOperationException("The database did not match its exact130-column comparison. No name write permitted.");
        if(apply)WeaponNameRevision.ValidateApply(plan,expectedWorldRowSha256,expectedRevisionToken);
        string directory=Path.Combine(_env.ContentRootPath,"App_Data","weapon-name-revisions",plan.Token);Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"before-world.json"),JsonSerializer.Serialize(before.World));
        File.WriteAllText(Path.Combine(directory,"before-manifest.json"),JsonSerializer.Serialize(before.Registry));
        File.WriteAllText(Path.Combine(directory,"item_template.sql"),plan.Sql.Text);
        if(apply&&recoverable)await ApplyLegacyNameAsync(admin,worldTable,manifestTable,worldSchema,databaseBinding,before.Registry,before.World,plan,Read);
        if(apply&&!recoverable)await RunArtTransactionAsync(admin,async tx=>{
            var locked=await Read(tx);var lockedPlan=WeaponNameRevision.Compile(locked.Registry,locked.World,expectedOldName,newName,atomicBinding);
            WeaponNameRevision.ValidateApply(lockedPlan,expectedWorldRowSha256,expectedRevisionToken);
            // The row reads hold table metadata locks. Recheck engines after those reads so a
            // preview/apply engine change cannot turn the world write into an unrollbackable one.
            var currentEngines=(await admin.QueryAsync<(string Schema,string Name,string Engine)>(@"SELECT TABLE_SCHEMA,TABLE_NAME,ENGINE FROM information_schema.TABLES
                WHERE (TABLE_SCHEMA=@adminSchema AND TABLE_NAME='custom_weapon_item_manifest') OR (TABLE_SCHEMA=@worldSchema AND TABLE_NAME='item_template')",new{adminSchema,worldSchema},tx)).ToList();
            string currentAdmin=currentEngines.SingleOrDefault(x=>x.Schema==adminSchema&&x.Name=="custom_weapon_item_manifest").Engine??"unavailable";
            string currentWorld=currentEngines.SingleOrDefault(x=>x.Schema==worldSchema&&x.Name=="item_template").Engine??"unavailable";
            WeaponNameRevision.RequireAtomic(adminIdentity,worldIdentity,currentAdmin,currentWorld);
            if(currentAdmin!=adminEngine||currentWorld!=worldEngine)throw new InvalidOperationException("Table engine changed since the reviewed preview.");
            int changed=await admin.ExecuteAsync($"UPDATE {worldTable} SET name=@newName WHERE entry=@itemEntry AND patch=@patch AND display_id=@displayId AND BINARY name=BINARY @expectedOldName",
                new{newName,itemEntry,patch=plan.Patch,displayId,expectedOldName},tx);
            if(changed!=1)throw new InvalidOperationException("World row changed during name revision.");
            changed=await admin.ExecuteAsync($"UPDATE {manifestTable} SET gameplay_json=@gameplay,sql_text=@sql,sql_sha256=@sha WHERE build_id=@build AND item_entry=@itemEntry AND display_id=@displayId AND sql_sha256=@beforeSha AND BINARY gameplay_json=BINARY @beforeGameplay",
                new{gameplay=plan.GameplayJson,sql=plan.Sql.Text,sha=plan.Sql.Sha256,build=before.Registry.BuildId,itemEntry,displayId,beforeSha=before.Registry.SqlSha256,beforeGameplay=before.Registry.GameplayJson},tx);
            if(changed!=1)throw new InvalidOperationException("Forge manifest changed during name revision.");
            var after=await Read(tx);
            if(WeaponNameRevision.Hash(JsonSerializer.Serialize(after.World))!=plan.AfterWorldSha256||after.Registry.GameplayJson!=plan.GameplayJson||after.Registry.SqlSha256!=plan.Sql.Sha256||after.Registry.SqlText!=plan.Sql.Text)
                throw new InvalidOperationException("Post-write exact name-only/manifest verification failed; rolling back.");
        });
        long auditId=0;
        if(apply)try{auditId=await _audit.LogAsync(new AuditEntry{Category="weaponforge",Action="rename_weapon",TargetType="item",TargetId=checked((int)itemEntry),TargetName=newName,
            StateBefore=JsonSerializer.Serialize(new{expectedOldName,plan.BeforeWorldSha256,before.Registry.GameplayJson,before.Registry.SqlSha256}),
            StateAfter=JsonSerializer.Serialize(new{newName,plan.AfterWorldSha256,plan.Sql.Sha256,plan.Token}),Success=true,IsReversible=false,RevertKind=RevertKind.None,
            Notes=(recoverable?"Exact name-only MyISAM compare-and-swap with durable journal, transactional Forge manifest and guarded compensation; not atomic. ":"Exact name-only world update and Forge manifest/publishable SQL synchronization in one cross-schema InnoDB transaction. ")+"Live item-template reload and client requery remain required."});}
        catch(Exception ex){_logger.LogError(ex,"Name revision committed but audit failed for item {ItemEntry}",itemEntry);}
        var result=new{ok=true,dryRun=!apply,changed=apply,itemEntry,displayId,oldName=expectedOldName,newName,
            beforeWorldRowSha256=plan.BeforeWorldSha256,afterWorldRowSha256=plan.AfterWorldSha256,revisionToken=plan.Token,
            beforeSqlSha256=before.Registry.SqlSha256,sqlSha256=plan.Sql.Sha256,sql=plan.Sql.Text,
            columnsPreserved=before.World.Keys.Where(k=>k!="name").ToArray(),sameServer=true,adminEngine,worldEngine,
            atomic=!recoverable,recoverableJournal=recoverable,fullRowPredicateVerified=true,
            transactionScope=recoverable?"MyISAM full-row name compare-and-swap + InnoDB manifest transaction; durable journal and exact compensation, not atomic":"one connection, world name + Forge gameplay name + publishable full-row SQL",auditId,auditRecorded=auditId>0,
            auditWarning=apply&&auditId==0?"Rename committed but audit logging failed; retain receipt and do not blindly retry.":null,
            runtimeReloadRequired=apply,runtimeVerified=false,patchChanged=false};
        try{File.WriteAllText(Path.Combine(directory,apply?"receipt.json":"preview.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions(JsonSerializerDefaults.Web)));}
        catch(Exception ex)when(apply){_logger.LogError(ex,"Name revision committed but receipt write failed for {ItemEntry}",itemEntry);}
        return result;
    }
}
