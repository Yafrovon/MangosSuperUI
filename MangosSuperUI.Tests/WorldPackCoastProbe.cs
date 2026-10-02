using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MangosSuperUI.Services.WorldPacks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MangosSuperUI.Tests;

/// <summary>Opt-in offline rehearsal of the real stamp/sculpt/place/stitch/coast build; no web or database writes.</summary>
public class WorldPackCoastProbe(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive=true,WriteIndented=true };
    private const BindingFlags Private = BindingFlags.Instance|BindingFlags.NonPublic;

    [Fact]
    public void PlannedContinentBuild()
    {
        string? prefix=Environment.GetEnvironmentVariable("MSUI_COAST_PLAN");
        if(string.IsNullOrWhiteSpace(prefix))return;
        string docsPath=prefix+"-expected-docs.json",statePath=prefix+"-expected-state.json";
        string root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(prefix)!,"..",".."));
        string data=Path.Combine(root,"GameData","Data");
        Assert.True(File.Exists(Path.Combine(data,"terrain.MPQ")),data);
        string outDir=prefix+"-probe";
        Directory.CreateDirectory(outDir);
        var rawDocs=JsonNode.Parse(File.ReadAllText(docsPath))!["docs"]!.AsArray();
        var docs=rawDocs.OfType<JsonObject>().Select(d=>new DocRow { PackId=(int)d["packId"]!,Kind=(string)d["kind"]!,
            DocKey=(string)d["docKey"]!,Body=d["body"]!.ToJsonString() }).ToList();
        var state=JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        var placements=state["placements"]!.Deserialize<List<PlacementRow>>(JsonOptions)!.Where(p=>p.MapId==0&&!p.Deleted).ToList();
        var sculpt=((state["sourceSculpt"]??state["sculpt"])!.Deserialize<List<SculptTile>>(JsonOptions)??[])
            .ToDictionary(t=>(map:0,col:t.Col,row:t.Row),t=>t.Deltas);
        var surfaceSculpt=(state["surfaceSculpt"]?.Deserialize<List<SculptTile>>(JsonOptions)??[])
            .ToDictionary(t=>(map:0,col:t.Col,row:t.Row),t=>t.Deltas);
        bool MapZero(DocRow d)
        {
            var b=JsonNode.Parse(d.Body)!;
            if(d.Kind=="map")return false;
            return b["map"] is null || (int)b["map"]! == 0;
        }
        var geometryDocs=docs.Where(MapZero).ToList();
        using var stock=new VanillaArchiveSet(data);
        var mapDirs=WorldPackBuildService.MapDirectories(stock);
        var missingTiles=geometryDocs.Where(d=>d.Kind=="tile").Select(d=>(d,b:JsonNode.Parse(d.Body)!)).SelectMany(t=>new[] {
            WorldCoords.AdtPath((string)t.b["sourceMap"]!,(int)t.b["sourceCol"]!,(int)t.b["sourceRow"]!),
            WorldCoords.AdtPath(mapDirs[(int)t.b["map"]!],(int)t.b["col"]!,(int)t.b["row"]!) })
            .Distinct().Where(path=>stock.ReadFile(path)is null).ToArray();
        if(missingTiles.Length>0)
        {
            File.WriteAllText(Path.Combine(outDir,"missing-tiles.json"),JsonSerializer.Serialize(missingTiles,JsonOptions));
            Assert.Fail("Plan requires unavailable stock ADTs: "+string.Join(", ",missingTiles));
        }
        var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        var service=(WorldPackBuildService)RuntimeHelpers.GetUninitializedObject(typeof(WorldPackBuildService));
        typeof(WorldPackBuildService).GetField("_logger",Private)!.SetValue(service,NullLogger<WorldPackBuildService>.Instance);
        typeof(WorldPackBuildService).GetField("_sync",Private)!.SetValue(service,new object());
        var buildState=new WorldPackBuildService.BuildState { BuildId=-1 };
        void InvokeBuild(string method,params object[] args)=>typeof(WorldPackBuildService).GetMethod(method,Private)!.Invoke(service,args);
        InvokeBuild("BuildNewMaps",buildState,stock,geometryDocs,mapDirs,files);
        var built=(Dictionary<(int map,int col,int row),AdtDocument>)typeof(WorldPackBuildService).GetField("_baseAdts",Private)!.GetValue(service)!;
        var wmos=new Dictionary<(int map,int col,int row),List<(PlacementRow p,Vector3 pos,Vector3 min,Vector3 max)>>();
        var doodads=new Dictionary<(int map,int col,int row),List<(PlacementRow p,Vector3 pos,float radius)>>();
        foreach(var p in placements)
        {
            var pos=WorldCoords.WorldToPlacement(new(p.PosX,p.PosY,p.PosZ));var rot=new Vector3(p.RotX,p.RotY,p.RotZ);
            if(p.Kind=="wmo")
            {
                var box=ModelBounds.Wmo(stock.ReadFile(p.ModelPath))??throw new InvalidOperationException(p.ModelPath);
                var (min,max)=ModelBounds.WmoExtents(box.Item1,box.Item2,pos,rot);
                int c0=(int)MathF.Floor(min.X/WorldCoords.Tile),c1=(int)MathF.Floor(max.X/WorldCoords.Tile);
                int r0=(int)MathF.Floor(min.Z/WorldCoords.Tile),r1=(int)MathF.Floor(max.Z/WorldCoords.Tile);
                for(int c=Math.Max(c0,0);c<=Math.Min(c1,63);c++)for(int r=Math.Max(r0,0);r<=Math.Min(r1,63);r++)
                {
                    if(!wmos.TryGetValue((0,c,r),out var list))wmos[(0,c,r)]=list=[];
                    list.Add((p,pos,min,max));
                }
            }
            else
            {
                var box=ModelBounds.M2(stock.ReadFile(Path.ChangeExtension(p.ModelPath,".m2")))??throw new InvalidOperationException(p.ModelPath);
                float radius=MathF.Max(box.Item1.Length(),box.Item2.Length())*p.Scale;
                var key=(0,(int)(pos.X/WorldCoords.Tile),(int)(pos.Z/WorldCoords.Tile));
                if(!doodads.TryGetValue(key,out var list))doodads[key]=list=[];
                list.Add((p,pos,radius));
            }
        }
        var paths=geometryDocs.Where(d=>d.Kind=="path").Select(d=>GradedPath.Parse(d.DocKey,d.Body)).ToList();
        var touched=built.Keys.Concat(sculpt.Keys).Concat(surfaceSculpt.Keys).Concat(wmos.Keys).Concat(doodads.Keys)
            .Concat(paths.SelectMany(p=>p.Tiles().Select(t=>(p.Map,t.col,t.row)))).Distinct().ToList();
        foreach(var key in touched)
        {
            if(!built.TryGetValue(key,out var adt))
            {
                byte[]? bytes=stock.ReadFile(WorldCoords.AdtPath(mapDirs[key.Item1],key.Item2,key.Item3));
                if(bytes is null)continue;
                built[key]=adt=AdtDocument.Parse(bytes,key.Item2,key.Item3);
            }
            if(sculpt.TryGetValue(key,out var changes))adt.ApplySculpt(changes);
            foreach(var (p,pos,min,max) in wmos.GetValueOrDefault(key)??[])
                adt.AddWmo(p.ModelPath,p.UniqueId,pos,new(p.RotX,p.RotY,p.RotZ),min,max,(ushort)p.DoodadSet);
            var inside=(wmos.GetValueOrDefault(key)??[]).SelectMany(w=>
            {
                var matrix=WorldPackGeometry.WmoMatrix(w.pos,new(w.p.RotX,w.p.RotY,w.p.RotZ));
                return WorldPackGeometry.WmoGroups(stock.ReadFile(w.p.ModelPath)).Select(g=>Obb.FromLocal(g.min,g.max,matrix));
            }).ToList();
            if(inside.Count>0)adt.DropDoodads((_,pos)=>inside.Any(b=>b.Depth(pos)>.3f));
            foreach(var (p,pos,radius) in doodads.GetValueOrDefault(key)??[])
                adt.AddDoodad(Path.ChangeExtension(p.ModelPath,".mdx"),p.UniqueId,pos,new(p.RotX,p.RotY,p.RotZ),p.Scale,radius);
        }
        InvokeBuild("StitchSeamsAndCarryWater",buildState,stock,mapDirs,built,sculpt,paths);
        var beforeCoast=built.ToDictionary(kv=>kv.Key,kv=>kv.Value.WmoPlacementsFull().Select(w=>(w.path,w.pos,w.rot)).ToList());
        WorldPackCoast.Apply(geometryDocs,stock,mapDirs,built,placements,line=>buildState.Log.Add(line));
        WorldPackSculptLayers.ApplySurface(built,surfaceSculpt);
        foreach(var (key,adt) in built)
        {
            string path=WorldCoords.AdtPath(mapDirs[key.map],key.col,key.row);
            files[path]=adt.Write();
            string dest=Path.Combine(outDir,"files",path.Replace('\\',Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.WriteAllBytes(dest,files[path]);
        }
        files[WorldPackBuildService.ManifestMpqPath]=JsonSerializer.SerializeToUtf8Bytes(new {
            adts=built.Keys.Select(k=>$"{k.map}:{k.col}_{k.row}").ToArray() });
        byte[]? ReadBuilt(string path)=>files.GetValueOrDefault(path)??stock.ReadFile(path);
        var input=new WorldPackAudit.AuditInput { Stock=stock.ReadFile,Built=ReadBuilt,MapDirs=mapDirs,Docs=geometryDocs,Placements=placements };
        var audit=new WorldPackAudit(input);
        typeof(WorldPackAudit).GetMethod("LoadRows",Private)!.Invoke(audit,null);
        var findings=new List<AuditFinding>();
        foreach(string method in new[]{"StampFidelity","PlacedBuildings","DroppedLeftovers","Spawns","TerrainIntegrity","Water","Holes","Paths","PatrolGround"})
            try{typeof(WorldPackAudit).GetMethod(method,Private)!.Invoke(audit,null);}
            catch(Exception e){findings.Add(new("probe","error",method,e.GetBaseException().Message));}
        findings.AddRange((List<AuditFinding>)typeof(WorldPackAudit).GetField("_out",Private)!.GetValue(audit)!);
        findings.AddRange(WorldPackCoast.Verify(input));
        var coast=Assert.Single(WorldPackCoast.Read(geometryDocs));
        object At(string kind,string id,int map,float x,float y,float z)
        {
            float? ground=audit.Ground(map,x,y);
            int col=WorldCoords.TileCol(y),row=WorldCoords.TileRow(x);
            AdtDocument? adt=built.GetValueOrDefault((map,col,row));
            int ix=Math.Clamp((int)(((32-col)*WorldCoords.Tile-y)/WorldCoords.Chunk),0,15);
            int iy=Math.Clamp((int)(((32-row)*WorldCoords.Tile-x)/WorldCoords.Chunk),0,15);
            float? water=adt is null?null:adt.LiquidLevel(adt.ChunkIndex(ix,iy));
            string? indoors=(string?)typeof(WorldPackAudit).GetMethod("IndoorsOf",Private)!.Invoke(audit,[map,new Vector3(x,y,z)]);
            return new {kind,id,map,x,y,z,ground,delta=z-ground,water,depth=water-ground,
                indoors,inside=coast.Region.Contains(new(x,y)),clearance=coast.CoastDistance(new(x,y))};
        }
        var spawns=geometryDocs.Where(d=>d.Kind=="dbrow:creature").Select(d=>(d,b:JsonNode.Parse(d.Body)!))
            .Where(t=>(int?)t.b["map"]==0).Select(t=>At("creature",t.d.DocKey,0,(float)t.b["position_x"]!,(float)t.b["position_y"]!,(float)t.b["position_z"]!)).ToList();
        var objects=placements.Select(p=>At("placement",p.Id.ToString(),p.MapId,p.PosX,p.PosY,p.PosZ)).ToList();
        var waterfront=new List<object>();
        foreach(var p in placements.Where(p=>p.Kind=="m2"&&new[]{"dock","pier","boat","ship","raft"}.Any(n=>p.ModelPath.Contains(n,StringComparison.OrdinalIgnoreCase))))
        {
            byte[] model=stock.ReadFile(Path.ChangeExtension(p.ModelPath,".m2"))!;
            var bounds=ModelBounds.M2(model)!.Value;
            float deck=ModelBounds.M2WalkableTop(model)??0;
            var surface=ModelBounds.M2WalkableSurface(model);
            Vector3 min=bounds.min,max=bounds.max;
            if(surface is not null)
            {
                min=surface.Vertices.Aggregate(Vector3.Min);max=surface.Vertices.Aggregate(Vector3.Max);
            }
            var matrix=WorldPackGeometry.WmoMatrix(WorldCoords.WorldToPlacement(new(p.PosX,p.PosY,p.PosZ)),new(p.RotX,p.RotY,p.RotZ));
            var samples=new List<object>();
            for(int i=0;i<=4;i++)for(int j=0;j<=4;j++)
            {
                float localX=min.X+(max.X-min.X)*i/4,localY=min.Y+(max.Y-min.Y)*j/4;
                float localZ=deck;
                if(surface is not null)
                {
                    bool hit=false;
                    for(int t=0;t+2<surface.Vertices.Length;t+=3)
                    {
                        var a=surface.Vertices[t];var b=surface.Vertices[t+1];var c=surface.Vertices[t+2];
                        float den=(b.Y-c.Y)*(a.X-c.X)+(c.X-b.X)*(a.Y-c.Y);
                        if(Math.Abs(den)<1e-6f)continue;
                        float u=((b.Y-c.Y)*(localX-c.X)+(c.X-b.X)*(localY-c.Y))/den;
                        float v=((c.Y-a.Y)*(localX-c.X)+(a.X-c.X)*(localY-c.Y))/den;
                        if(u<-.0001f||v<-.0001f||u+v>1.0001f)continue;
                        localZ=u*a.Z+v*b.Z+(1-u-v)*c.Z;hit=true;break;
                    }
                    if(!hit)continue;
                }
                var at=WorldCoords.PlacementToWorld(Vector3.Transform(new Vector3(localX,localY,localZ)*p.Scale,matrix));
                float? ground=audit.Ground(p.MapId,at.X,at.Y);
                float? water=audit.Water(p.MapId,at.X,at.Y);
                samples.Add(new {i,j,x=at.X,y=at.Y,deck=at.Z,ground,water,waterDepth=water-ground,deckClearance=at.Z-ground});
            }
            waterfront.Add(new {id=p.Id,model=p.ModelPath,localDeck=deck,localSlope=surface?.SlopeDegrees,
                localMin=surface?.MinimumHeight,localMax=surface?.MaximumHeight,scale=p.Scale,samples});
        }
        var wmoReport=built.SelectMany(kv=>kv.Value.WmoPlacementsFull().Select(w=>new {map=kv.Key.map,col=kv.Key.col,row=kv.Key.row,
            path=w.path,x=WorldCoords.PlacementToWorld(w.pos).X,y=WorldCoords.PlacementToWorld(w.pos).Y,z=WorldCoords.PlacementToWorld(w.pos).Z,
            ground=audit.Ground(kv.Key.map,WorldCoords.PlacementToWorld(w.pos).X,WorldCoords.PlacementToWorld(w.pos).Y),
            clearance=coast.CoastDistance(new(WorldCoords.PlacementToWorld(w.pos).X,WorldCoords.PlacementToWorld(w.pos).Y))})).ToList();
        var removed=beforeCoast.SelectMany(kv=>kv.Value.Where(w=>!built[kv.Key].WmoPlacementsFull().Any(a=>a.path==w.path&&a.pos==w.pos&&a.rot==w.rot))
            .Select(w=>new {col=kv.Key.col,row=kv.Key.row,path=w.path,x=WorldCoords.PlacementToWorld(w.pos).X,y=WorldCoords.PlacementToWorld(w.pos).Y})).ToList();
        var baseline=coast.Tiles.OrderBy(t=>t.col).ThenBy(t=>t.row).Select(tile=>
        {
            var original=AdtDocument.Parse(stock.ReadFile(WorldCoords.AdtPath(mapDirs[coast.Region.Map],tile.col,tile.row))!,tile.col,tile.row);
            var h=original.OuterHeights();
            var edges=new[] {Enumerable.Range(0,129).Select(k=>original.OuterHeight(0,k)).ToArray(),
                Enumerable.Range(0,129).Select(k=>original.OuterHeight(128,k)).ToArray(),
                Enumerable.Range(0,129).Select(k=>original.OuterHeight(k,0)).ToArray(),
                Enumerable.Range(0,129).Select(k=>original.OuterHeight(k,128)).ToArray()};
            return new {col=tile.col,row=tile.row,min=h.Min(),max=h.Max(),dryVertices=h.Count(v=>v>coast.SeaLevel+.1f),
                liquidChunks=Enumerable.Range(0,256).Count(i=>original.LiquidLevel(i)is not null),
                edges=edges.Select((e,i)=>new {edge=new[]{"north","south","west","east"}[i],min=e.Min(),max=e.Max()}).ToArray()};
        }).ToList();
        var report=new {tileCount=built.Count,spawns,placements=objects,waterfront,wmos=wmoReport,removedWmos=removed,baseline,findings,log=buildState.Log};
        File.WriteAllText(Path.Combine(outDir,"report.json"),JsonSerializer.Serialize(report,JsonOptions));
        output.WriteLine($"Offline probe wrote {built.Count} ADTs, {spawns.Count} spawn samples and {findings.Count} findings to {outDir}");
        foreach(var group in findings.GroupBy(f=>(f.Check,f.Severity)))output.WriteLine($"{group.Key}: {group.Count()}");
    }
}
