using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MangosSuperUI.Services.WeaponForge;
using MangosSuperUI.Services.WeaponForge.RawM2;
using Xunit;

namespace MangosSuperUI.Tests;

public class WeaponGeometryRevisionTests
{
    private static readonly byte[] Source = [103,108,84,70,2,0,0,0,12,0,0,0];
    private static RigidWeaponMesh Mesh(uint[]? indices = null) => new() {
        Positions = [Vector3.Zero, new(2,0,0), new(.25f,.2f,0)],
        Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        Uv0 = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], Indices = indices ?? [0,1,2], Material = new()
    };

    [Fact]
    public void RevisionPreservesIdentityTextureMetadataAndScaffoldBytes()
    {
        var row = Row(); var original = row.M2.ToArray(); var texture = row.Blp.ToArray();
        WeaponGeometryRevision.ValidateTarget(row, row.DisplayId, row.ItemEntry, row.ModelSha256);
        var plan = WeaponGeometryRevision.Compile(row, Mesh(), Source);
        Assert.Equal(row.ItemEntry, plan.ItemEntry); Assert.Equal(row.DisplayId, plan.DisplayId);
        Assert.Equal(row.ModelId, plan.ModelId); Assert.Equal(row.BuildId, plan.BuildId);
        Assert.Equal(row.TextureSha256, plan.TextureSha256); Assert.Equal(texture, row.Blp);
        Assert.Equal(original, row.M2); Assert.NotEqual(row.ModelSha256, plan.ModelSha256);
        Assert.False(M2BinaryValidator.Validate(plan.M2, 3, 4).HasErrors);
        Assert.Equal("{\"spellVisualId\":2799}", row.DbcFieldsJson);
        for (int i=0;i<original.Length;i++)
            if (i is not (>=0x44 and <0x54) and not (>=0xb4 and <0xd0)) Assert.Equal(original[i],plan.M2[i]);
        var parsed=RawM2Document.Parse(plan.M2,out _)!;
        Assert.Equal(Mesh().Positions.Select(CoordinateContract.MeshToWoW),parsed.ReadVertexPositions());
    }

    [Fact]
    public void StaleExpectedModelHashCannotRevise()
    {
        var row=Row();
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.ValidateTarget(row,row.DisplayId,row.ItemEntry,new string('f',64)));
    }

    [Theory]
    [InlineData("donor_patch")][InlineData("tbc_import")][InlineData("vanilla_recolor")]
    public void WrongSourceKindCannotRevise(string kind)
    {
        var row=Row(); row.SourceKind=kind;
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.ValidateTarget(row,row.DisplayId,row.ItemEntry,row.ModelSha256));
    }

    [Fact]
    public void WrongItemPairSharedModelAndCorruptTextureFailClosed()
    {
        var row=Row();
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.ValidateTarget(row,row.DisplayId,row.ItemEntry+1,row.ModelSha256));
        row.DisplayCount=2;
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.ValidateTarget(row,row.DisplayId,row.ItemEntry,row.ModelSha256));
        row.DisplayCount=1; row.Blp[^1]^=1;
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.ValidateTarget(row,row.DisplayId,row.ItemEntry,row.ModelSha256));
    }

    [Theory]
    [InlineData(0,0,2)][InlineData(0,1,9)]
    public void InvalidGeometryNeverCompiles(int a,int b,int c) => Assert.Throws<InvalidOperationException>(
        ()=>WeaponGeometryRevision.Compile(Row(),Mesh([(uint)a,(uint)b,(uint)c]),Source));

    [Fact]
    public void ApplyRequiresExactCompiledPreviewHash()
    {
        var plan=WeaponGeometryRevision.Compile(Row(),Mesh(),Source);
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.ValidateApplyHash(plan,null));
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.ValidateApplyHash(plan,new string('f',64)));
        WeaponGeometryRevision.ValidateApplyHash(plan,plan.ModelSha256);
    }

    [Fact]
    public void TextureOrMetadataChangeInvalidatesReviewedState()
    {
        var row=Row();var current=Row();var plan=WeaponGeometryRevision.Compile(row,Mesh(),Source);
        string token=CustomWeaponBuildService.GeometryRevisionToken(row,plan);
        current.DbcFieldsJson="{\"spellVisualId\":225}";
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.RequireUnchanged(row,current));
        Assert.NotEqual(token,CustomWeaponBuildService.GeometryRevisionToken(current,plan));
        current=Row(); current.Blp[^1]^=1; current.TextureSha256=WeaponGeometryRevision.Hash(current.Blp);
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.RequireUnchanged(row,current));
        Assert.NotEqual(token,CustomWeaponBuildService.GeometryRevisionToken(current,plan));
    }

    [Fact]
    public void EffectsAndOversizeSourceRejected()
    {
        var row=Row();row.EffectTextureCount=1;
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.ValidateTarget(row,row.DisplayId,row.ItemEntry,row.ModelSha256));
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.Compile(Row(),Mesh(),new byte[WeaponGeometryRevision.MaxSourceBytes+1]));
    }

    [Theory]
    [InlineData("animation")][InlineData("skin")]
    public void ImportedAnimatedOrSkinnedGlbIsRejected(string kind)
    {
        var importer=new GlbWeaponImporter(NullLogger<GlbWeaponImporter>.Instance);
        var result=importer.Import(Glb(kind),new GlbImportOptions { Reorient=false });
        Assert.False(result.Ok); Assert.True(result.Diagnostics.HasErrors); Assert.Null(result.Mesh);
    }

    [Fact]
    public void FullRigidGlbImportCompilesAtAuthoredCoordinates()
    {
        byte[] glb=Glb(null);
        var imported=new GlbWeaponImporter(NullLogger<GlbWeaponImporter>.Instance).Import(glb,new GlbImportOptions { Reorient=false });
        Assert.True(imported.Ok,string.Join("; ",imported.Diagnostics.Items));
        var plan=WeaponGeometryRevision.Compile(Row(),imported.Mesh!,glb);
        Assert.Equal(Mesh().Positions,imported.Mesh!.Positions);
        Assert.Equal(WeaponGeometryRevision.Hash(glb),plan.SourceSha256);
    }

    private static byte[] Glb(string? kind)
    {
        var root=JsonNode.Parse("""
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0,1]}],"nodes":[{"mesh":0},{}],
        "meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2},"indices":3,"mode":4}]}],
        "buffers":[{"byteLength":120}],
        "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":36},
        {"buffer":0,"byteOffset":72,"byteLength":24},{"buffer":0,"byteOffset":96,"byteLength":6},
        {"buffer":0,"byteOffset":104,"byteLength":4},{"buffer":0,"byteOffset":108,"byteLength":12}],
        "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[2,0.2,0]},
        {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":2,"componentType":5126,"count":3,"type":"VEC2"},
        {"bufferView":3,"componentType":5123,"count":3,"type":"SCALAR"},
        {"bufferView":4,"componentType":5126,"count":1,"type":"SCALAR","min":[0],"max":[0]},
        {"bufferView":5,"componentType":5126,"count":1,"type":"VEC3"}]}
        """)!;
        if(kind=="skin") { root["nodes"]![0]!["skin"]=0;root["skins"]=JsonNode.Parse("[{\"joints\":[1]}]"); }
        if(kind=="animation") root["animations"]=JsonNode.Parse("[{\"samplers\":[{\"input\":4,\"output\":5}],\"channels\":[{\"sampler\":0,\"target\":{\"node\":0,\"path\":\"translation\"}}]}]");
        var json=Encoding.UTF8.GetBytes(root.ToJsonString());int padded=(json.Length+3)&~3;
        var result=new byte[12+8+padded+8+120];"glTF"u8.CopyTo(result);U32(result,4,2);U32(result,8,(uint)result.Length);
        U32(result,12,(uint)padded);U32(result,16,0x4e4f534a);result.AsSpan(20,padded).Fill(32);json.CopyTo(result,20);
        int h=20+padded;U32(result,h,120);U32(result,h+4,0x004e4942);int start=h+8;var mesh=Mesh();
        for(int i=0;i<3;i++) {
            float[] values=[mesh.Positions[i].X,mesh.Positions[i].Y,mesh.Positions[i].Z];
            for(int j=0;j<3;j++)BitConverter.GetBytes(values[j]).CopyTo(result,start+i*12+j*4);
            BitConverter.GetBytes(1f).CopyTo(result,start+36+i*12+8);
            BitConverter.GetBytes(mesh.Uv0[i].X).CopyTo(result,start+72+i*8);
            BitConverter.GetBytes(mesh.Uv0[i].Y).CopyTo(result,start+72+i*8+4);
            U16(result,start+96+i*2,(ushort)i);
        }
        return result;
    }

    internal static WeaponRevisionRow Row()
    {
        var m2=new byte[4096];"MD20"u8.CopyTo(m2); U32(m2,4,256);
        U32(m2,8,8);U32(m2,12,0x150);"fixture\0"u8.CopyTo(m2.AsSpan(0x150));
        Pair(0x34,1,0x180);Pair(0x44,3,0x250);Pair(0x5c,1,0x300);U32(m2,0x300,2);
        Pair(0x84,1,0x310);Pair(0x8c,1,0x320);Pair(0x94,1,0x324);Pair(0x9c,1,0x328);
        Pair(0xa4,1,0x32c);U16(m2,0x32c,ushort.MaxValue);
        Pair(0xac,1,0x330);U16(m2,0x330,ushort.MaxValue);
        for(int i=0;i<3;i++) {int v=0x250+i*48;m2[v+12]=255;BitConverter.GetBytes(1f).CopyTo(m2,v+28);}
        U32(m2,0x4c,4);U32(m2,0x50,0x400);
        for(int i=0;i<4;i++) {
            int h=0x400+i*44,p=0x600+i*128;
            U32(m2,h,3);U32(m2,h+4,(uint)p);U16(m2,p+2,1);U16(m2,p+4,2);
            U32(m2,h+8,3);U32(m2,h+12,(uint)(p+8));U16(m2,p+10,1);U16(m2,p+12,2);
            U32(m2,h+16,3);U32(m2,h+20,(uint)(p+16));
            U32(m2,h+24,1);U32(m2,h+28,(uint)(p+32));U16(m2,p+38,3);U16(m2,p+42,3);U16(m2,p+44,1);
            U32(m2,h+32,1);U32(m2,h+36,(uint)(p+64));U16(m2,p+72,ushort.MaxValue);U16(m2,p+78,1);
        }
        var blp=new byte[1180];"BLP2"u8.CopyTo(blp);U32(blp,4,1);blp[8]=2;
        U32(blp,12,4);U32(blp,16,4);U32(blp,20,1172);U32(blp,84,8);
        return new() {ItemEntry=1102495,DisplayId=76338,ModelId=76338,BuildId="wpn-existing",SourceKind="glb_import",
            M2=m2,Blp=blp,ModelSha256=WeaponGeometryRevision.Hash(m2),TextureSha256=WeaponGeometryRevision.Hash(blp),
            ManifestCount=1,DisplayCount=1,DbcFieldsJson="{\"spellVisualId\":2799}",GameplayJson="{\"name\":\"Existing\",\"weaponType\":\"staff\"}"};
        void Pair(int offset,uint count,uint pointer){U32(m2,offset,count);U32(m2,offset+4,pointer);}
    }
    private static void U32(byte[] b,int o,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o,4),v);
    private static void U16(byte[] b,int o,ushort v)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o,2),v);
}
