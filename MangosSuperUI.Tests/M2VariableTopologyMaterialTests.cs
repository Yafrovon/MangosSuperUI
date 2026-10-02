using System.Buffers.Binary;
using System.Numerics;
using MangosSuperUI.Services.WeaponForge;
using MangosSuperUI.Services.WeaponForge.RawM2;
using MangosSuperUI.Services.ArmorForge;
using Xunit;

namespace MangosSuperUI.Tests;

public class M2VariableTopologyMaterialTests
{
    [Theory]
    [InlineData(4,0)] // Native helm scaffold inherited two-sided rendering.
    [InlineData(0,1)] // Native shoulder scaffold inherited alpha-key rendering.
    public void AuthoredArmorDefaultGlbMaterialSurvivesItsNativeScaffold(int flags,int blend)
    {
        var report=new AuthoredArmorReport();
        var mesh=AuthoredArmorAssetValidator.ReadMesh(AuthoredArmorPackageTests.Glb(),"attachment.glb",report);
        Assert.True(report.Valid);Assert.False(mesh.Material.TwoSided);Assert.Equal(WeaponBlendMode.Opaque,mesh.Material.BlendMode);
        byte[] result=M2VariableTopologyBuilder.Build(Donor((ushort)flags,(ushort)blend),
            mesh.Positions.Select(CoordinateContract.MeshToWoW).ToArray(),mesh.Normals.Select(CoordinateContract.MeshNormalToWoW).ToArray(),
            mesh.Uv0,mesh,4,mesh.Material);
        var parsed=RawM2Document.Parse(result,out _)!;
        foreach(var view in parsed.Views){
            int index=U16(result,checked((int)view.Batches.Offset)+10);
            int record=checked((int)parsed.FindArray("renderFlags")!.Offset+index*4);
            Assert.Equal(0,U16(result,record)&4);Assert.Equal(0,U16(result,record+2));
        }
        Assert.False(M2BinaryValidator.Validate(result,mesh.VertexCount,4).HasErrors);
    }

    [Theory]
    [InlineData(0x35,1,false,WeaponBlendMode.Opaque)]
    [InlineData(0x31,1,false,WeaponBlendMode.Opaque)]
    [InlineData(0x35,0,false,WeaponBlendMode.Opaque)]
    [InlineData(0x31,0,true,WeaponBlendMode.Opaque)]
    [InlineData(0x35,0,false,WeaponBlendMode.AlphaKey)]
    [InlineData(0x31,0,true,WeaponBlendMode.AlphaKey)]
    public void ExplicitMaterialOverridesBothDefaultAndNondefaultDonorState(int flags,int blend,bool twoSided,WeaponBlendMode mode)
    {
        byte[] donor=Donor((ushort)flags,(ushort)blend),original=donor.ToArray();
        byte[] output=Compile(donor,new(){TwoSided=twoSided,BlendMode=mode});
        Assert.Equal(original,donor);
        Assert.False(M2BinaryValidator.Validate(output,3,4).HasErrors);
        var parsed=RawM2Document.Parse(output,out _)!;
        foreach(var view in parsed.Views) {
            int batch=checked((int)view.Batches.Offset),index=U16(output,batch+10);
            Assert.Equal(1,index); // Must follow each copied batch, not patch the first flag blindly.
            int record=checked((int)parsed.FindArray("renderFlags")!.Offset+index*4);
            Assert.Equal((ushort)((flags&~4)|(twoSided?4:0)),U16(output,record));
            Assert.Equal((ushort)mode,U16(output,record+2));
        }
        Assert.Equal(original.AsSpan(0x310,4).ToArray(),output.AsSpan(0x310,4).ToArray());
        Assert.Equal(original.AsSpan(0x318,4).ToArray(),output.AsSpan(0x318,4).ToArray());
    }

    [Fact]
    public void OmittedMaterialPreservesDonorFlagsForGeometryOnlyRevision()
    {
        byte[] donor=Donor(0x35,1),output=Compile(donor,null);
        Assert.Equal(donor.AsSpan(0x310,12).ToArray(),output.AsSpan(0x310,12).ToArray());
    }

    [Fact]
    public void ExplicitDefaultCannotSilentlyAcceptUnresolvableDonorMaterial()
    {
        byte[] donor=Donor(0x35,1);
        for(int i=0;i<4;i++)Put16(donor,0x600+i*128+64+10,99);
        Assert.Throws<InvalidOperationException>(()=>Compile(donor,new()));
    }

    static byte[] Compile(byte[] donor,WeaponMaterial? material)
    {
        var mesh=new RigidWeaponMesh{Positions=[Vector3.Zero,Vector3.UnitX,Vector3.UnitY],Normals=[Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ],
            Uv0=[Vector2.Zero,Vector2.UnitX,Vector2.UnitY],Indices=[0,1,2],Material=new()};
        return M2VariableTopologyBuilder.Build(donor,mesh.Positions,mesh.Normals,mesh.Uv0,mesh,4,material);
    }

    static byte[] Donor(ushort flags,ushort blend)
    {
        var m2=new byte[4096];"MD20"u8.CopyTo(m2);Put32(m2,4,256);
        Put32(m2,8,8);Put32(m2,12,0x150);"fixture\0"u8.CopyTo(m2.AsSpan(0x150));
        Pair(0x34,1,0x180);Pair(0x44,3,0x250);Pair(0x5c,1,0x300);Put32(m2,0x300,2);
        Pair(0x84,3,0x310);Pair(0x8c,1,0x320);Pair(0x94,1,0x324);Pair(0x9c,1,0x328);
        Put16(m2,0x310,0x12);Put16(m2,0x312,2);Put16(m2,0x314,flags);Put16(m2,0x316,blend);Put16(m2,0x318,0x24);Put16(m2,0x31a,1);
        Pair(0xa4,1,0x32c);Put16(m2,0x32c,ushort.MaxValue);Pair(0xac,1,0x330);Put16(m2,0x330,ushort.MaxValue);
        for(int i=0;i<3;i++){int v=0x250+i*48;m2[v+12]=255;BitConverter.GetBytes(1f).CopyTo(m2,v+28);}
        Put32(m2,0x4c,4);Put32(m2,0x50,0x400);
        for(int i=0;i<4;i++){
            int h=0x400+i*44,p=0x600+i*128;
            Put32(m2,h,3);Put32(m2,h+4,(uint)p);Put16(m2,p+2,1);Put16(m2,p+4,2);
            Put32(m2,h+8,3);Put32(m2,h+12,(uint)(p+8));Put16(m2,p+10,1);Put16(m2,p+12,2);
            Put32(m2,h+16,3);Put32(m2,h+20,(uint)(p+16));
            Put32(m2,h+24,1);Put32(m2,h+28,(uint)(p+32));Put16(m2,p+38,3);Put16(m2,p+42,3);Put16(m2,p+44,1);
            Put32(m2,h+32,1);Put32(m2,h+36,(uint)(p+64));Put16(m2,p+72,ushort.MaxValue);Put16(m2,p+74,1);Put16(m2,p+78,1);
        }
        return m2;
        void Pair(int offset,uint count,uint pointer){Put32(m2,offset,count);Put32(m2,offset+4,pointer);}
    }
    static ushort U16(byte[] b,int o)=>BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o,2));
    static void Put32(byte[] b,int o,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o,4),v);
    static void Put16(byte[] b,int o,ushort v)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o,2),v);
}
