using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangosSuperUI.Services.WeaponForge;

internal static class WeaponNameRevision
{
    internal static string Hash(string value)=>WeaponGeometryRevision.Hash(Encoding.UTF8.GetBytes(value));
    internal static string Schema(string value)
    {
        if(string.IsNullOrEmpty(value)||value.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))
            throw new InvalidOperationException("Unsupported database identifier; no rename performed.");
        return "`"+value+"`";
    }
    internal static void RequireAtomic(string adminServer,string worldServer,string adminEngine,string worldEngine)
    {
        if(adminServer!=worldServer)throw new InvalidOperationException("Rename blocked: Admin and world connections do not identify the same database server.");
        if(!new[]{adminEngine,worldEngine}.All(e=>string.Equals(e,"InnoDB",StringComparison.OrdinalIgnoreCase)||string.Equals(e,"XtraDB",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Rename blocked: both tables must be transactional InnoDB/XtraDB (manifest={adminEngine}, item_template={worldEngine}). No writes performed.");
    }
    internal static SortedDictionary<string,string> Snapshot(IDictionary<string,object> row)
    {
        var result=new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach(var pair in row) {
            if(pair.Value is null or DBNull)throw new InvalidOperationException("Nullable item_template fields are outside the exact rename SQL contract: "+pair.Key);
            // Widen Single before formatting: MySQL compares a FLOAT column with
            // a string parameter as double. "0.15" loses the stored float's bits.
            result[pair.Key]=pair.Value switch {
                float value=>((double)value).ToString("R",CultureInfo.InvariantCulture),
                double value=>value.ToString("R",CultureInfo.InvariantCulture),
                _=>Convert.ToString(pair.Value,CultureInfo.InvariantCulture)??""
            };
        }
        if(!result.Keys.Order().SequenceEqual(DonorItemTemplateFixture.Columns.Order()))
            throw new InvalidOperationException("item_template columns differ from the verified 130-column export contract; no partial export or rename is permitted.");
        foreach(var pair in result.Where(p=>p.Key is not ("name" or "description")))
            if(!double.TryParse(pair.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!double.IsFinite(value))
                throw new InvalidOperationException("Non-finite/non-numeric item_template value: "+pair.Key);
        return result;
    }
    internal static WeaponNameRevisionPlan Compile(WeaponRenameRow registry,SortedDictionary<string,string> world,
        string expectedOldName,string newName,string atomicBinding)
    {
        if(string.IsNullOrWhiteSpace(newName)||newName.Length>255||newName.Any(char.IsControl))throw new InvalidOperationException("New name must contain1..255 characters and no control characters.");
        if(newName==expectedOldName)throw new InvalidOperationException("New name must differ from current name.");
        if(registry.ItemEntry<WeaponIdReservationService.ItemEntryFloor||registry.DisplayId<WeaponIdReservationService.ItemDisplayFloor||registry.ManifestCount!=1||registry.SourceKind!="glb_import")
            throw new InvalidOperationException("Rename requires one registered GLB weapon and existing item/display identity.");
        var gameplay=JsonNode.Parse(registry.GameplayJson)?.AsObject()??throw new InvalidOperationException("Invalid gameplay JSON");
        if(world["entry"]!=registry.ItemEntry.ToString(CultureInfo.InvariantCulture)||world["display_id"]!=registry.DisplayId.ToString(CultureInfo.InvariantCulture)||
            world["class"]!="2"||world["name"]!=expectedOldName||gameplay["name"]?.GetValue<string>()!=expectedOldName)
            throw new InvalidOperationException("World and Forge identity/name do not match the exact inspected old name.");
        if(Hash(registry.SqlText)!=registry.SqlSha256)throw new InvalidOperationException("Stored publishable SQL does not match its recorded hash.");
        var profile=WeaponTypeCatalog.All.SingleOrDefault(p=>p.Key==gameplay["weaponType"]?.GetValue<string>())
            ??throw new InvalidOperationException("Registered weapon family is unavailable.");
        if(world["subclass"]!=profile.Subclass.ToString(CultureInfo.InvariantCulture))throw new InvalidOperationException("World weapon subclass differs from registered family.");
        string before=JsonSerializer.Serialize(world),beforeHash=Hash(before);
        var after=new SortedDictionary<string,string>(world,StringComparer.Ordinal){["name"]=newName};
        // Supplying every verified live column prevents regenerating gameplay from donor defaults.
        GeneratedSql sql=WeaponItemTemplateSql.Build(registry.ItemEntry,newName,registry.DisplayId,registry.BuildId,after);
        gameplay["name"]=newName;string newGameplay=gameplay.ToJsonString();
        string token=Hash(JsonSerializer.Serialize(new{registry.ItemEntry,registry.DisplayId,registry.BuildId,registry.GameplayJson,
            registry.SqlSha256,beforeHash,expectedOldName,newName,newSqlSha256=sql.Sha256,atomicBinding}));
        return new(registry.ItemEntry,registry.DisplayId,expectedOldName,newName,beforeHash,Hash(JsonSerializer.Serialize(after)),newGameplay,sql,token,world["patch"]);
    }
    internal static void ValidateApply(WeaponNameRevisionPlan plan,string? expectedRowHash,string? expectedToken)
    {
        WeaponGeometryRevision.RequireHash(expectedRowHash,"expectedWorldRowSha256");WeaponGeometryRevision.RequireHash(expectedToken,"expectedRevisionToken");
        if(plan.BeforeWorldSha256!=expectedRowHash||plan.Token!=expectedToken)throw new InvalidOperationException("Name, full world row, publishable SQL, registry metadata or transaction binding changed since preview. No rename applied.");
    }
}

internal sealed class WeaponRenameRow
{
    public long ItemEntry {get;set;}
    public long DisplayId {get;set;}
    public string BuildId {get;set;}="";
    public string GameplayJson {get;set;}="";
    public string SqlText {get;set;}="";
    public string SqlSha256 {get;set;}="";
    public string SourceKind {get;set;}="";
    public int ManifestCount {get;set;}
}
internal sealed record WeaponNameRevisionPlan(long ItemEntry,long DisplayId,string OldName,string NewName,string BeforeWorldSha256,
    string AfterWorldSha256,string GameplayJson,GeneratedSql Sql,string Token,string Patch);
