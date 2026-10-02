using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Numerics;
using MangosSuperUI.Services;
using MangosSuperUI.Services.WeaponForge;
using MangosSuperUI.Services.WeaponForge.RawM2;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;
using MangosSuperUI.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Runtime.CompilerServices;

namespace MangosSuperUI.Tests;

public class WeaponArtRevisionTests
{
    private static readonly byte[] Source = [103,108,84,70,2,0,0,0,12,0,0,0];
    private static readonly BlpWriterService Writer = new(NullLogger<BlpWriterService>.Instance);
    private static RigidWeaponMesh Mesh() => new() {
        Positions=[Vector3.Zero,new(2,0,0),new(.25f,.2f,0)], Normals=[Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ],
        Uv0=[Vector2.Zero,Vector2.UnitX,Vector2.UnitY],Indices=[0,1,2],Material=new() };
    private static byte[] Png(int size, SKColor color)
    {
        using var bitmap=new SKBitmap(size,size,SKColorType.Rgba8888,SKAlphaType.Unpremul);
        bitmap.Erase(color);using var image=SKImage.FromBitmap(bitmap);
        using var encoded=image.Encode(SKEncodedImageFormat.Png,100);return encoded.ToArray();
    }
    private static WeaponArtRevisionPlan Compile(WeaponRevisionRow row, SKColor? skin=null, SKColor? icon=null) =>
        WeaponArtRevision.Compile(row,Mesh(),Source,Png(256,skin??SKColors.DarkBlue),Png(64,icon??SKColors.Gold),Writer);

    [Theory]
    [InlineData("model-hash")][InlineData("skin-hash")][InlineData("missing-file")]
    [InlineData("missing-skin")][InlineData("missing-icon")][InlineData("malformed-glb")]
    public async Task EndpointRefusesInvalidInputsBeforeAccessingAnyService(string failure)
    {
        // Uninitialized dependencies deliberately fail if input rejection ever reaches registry services.
        var controller=(WeaponForgeController)RuntimeHelpers.GetUninitializedObject(typeof(WeaponForgeController));
        IFormFile File(byte[] bytes)=>new FormFile(new MemoryStream(bytes),0,bytes.Length,"file","input.bin");
        var result=await controller.ReviseArt(failure=="missing-file"?null:File("bad-glb"u8.ToArray()),
            failure=="missing-skin"?null:File(Png(256,SKColors.Blue)),failure=="missing-icon"?null:File(Png(64,SKColors.Gold)),
            76338,1102495,failure=="model-hash"?"bad":new string('a',64),failure=="skin-hash"?"bad":new string('b',64));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void FullArtReplacesExplicitMaterialSkinAndIconWithoutChangingIdentityOrAnchors()
    {
        var row=WeaponGeometryRevisionTests.Row();
        BinaryPrimitives.WriteUInt16LittleEndian(row.M2.AsSpan(0x310,2),0x25); // preserve 0x21; clear two-sided
        BinaryPrimitives.WriteUInt16LittleEndian(row.M2.AsSpan(0x312,2),1); // inherited alpha-key
        row.ModelSha256=WeaponGeometryRevision.Hash(row.M2);
        Assert.Throws<InvalidOperationException>(()=>WeaponGeometryRevision.Compile(row,Mesh(),Source));
        var plan=Compile(row);
        Assert.Equal((ushort)0x21,BinaryPrimitives.ReadUInt16LittleEndian(plan.Geometry.M2.AsSpan(0x310,2)));
        Assert.Equal((ushort)0,BinaryPrimitives.ReadUInt16LittleEndian(plan.Geometry.M2.AsSpan(0x312,2)));
        Assert.Equal(row.M2.AsSpan(0x180,108).ToArray(),plan.Geometry.M2.AsSpan(0x180,108).ToArray());
        Assert.Equal(row.ItemEntry,plan.Geometry.ItemEntry);Assert.Equal(row.DisplayId,plan.Geometry.DisplayId);
        Assert.Equal(row.BuildId,plan.Geometry.BuildId);Assert.NotEqual(row.TextureSha256,plan.TextureSha256);
        Assert.Equal("Interface\\Icons\\INV_SUI_W_76338_RAID.blp",plan.IconMpqPath);
        Assert.Equal(256u,BinaryPrimitives.ReadUInt32LittleEndian(plan.Blp.AsSpan(12,4)));
        Assert.Equal(64u,BinaryPrimitives.ReadUInt32LittleEndian(plan.IconBlp.AsSpan(12,4)));
        Assert.Equal(0,plan.Blp[9]);Assert.Equal(8,plan.IconBlp[9]);Assert.Equal(1,plan.IconBlp[10]);
        Assert.Equal(Mesh().Positions.Select(CoordinateContract.MeshToWoW),RawM2Document.Parse(plan.Geometry.M2,out _)!.ReadVertexPositions());
    }

    [Fact]
    public void AuthoredIconAlphaIsEncodedAsDxt3InsteadOfFlattened()
    {
        var plan=Compile(WeaponGeometryRevisionTests.Row(),icon:new SKColor(100,150,200,128));
        int offset=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(plan.IconBlp.AsSpan(20,4)));
        Assert.Equal(8,plan.IconBlp[9]);Assert.Equal(1,plan.IconBlp[10]);
        int decodedAlpha=(plan.IconBlp[offset]&15)*17;
        Assert.InRange(Math.Abs(decodedAlpha-128),0,8);Assert.NotEqual(255,decodedAlpha);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void StaleOldModelOrSkinIsRejected(bool model)
    {
        var row=WeaponGeometryRevisionTests.Row();
        Assert.Throws<InvalidOperationException>(()=>WeaponArtRevision.ValidateTarget(row,[],row.DisplayId,row.ItemEntry,
            model?new string('f',64):row.ModelSha256,model?row.TextureSha256:new string('f',64)));
    }

    [Theory]
    [InlineData(128,64,false)][InlineData(256,128,false)][InlineData(256,64,true)]
    public void NoSilentTextureResizeOrAlphaFlatten(int skinSize,int iconSize,bool transparent)
    {
        Assert.Throws<InvalidOperationException>(()=>WeaponArtRevision.Compile(WeaponGeometryRevisionTests.Row(),Mesh(),Source,
            Png(skinSize,transparent?new SKColor(30,60,90,254):SKColors.Blue),Png(iconSize,SKColors.Gold),Writer));
    }

    [Theory]
    [InlineData("skin")][InlineData("icon")][InlineData("metadata")][InlineData("source")]
    public void ReviewedTokenBindsNewArtAndPreservedState(string change)
    {
        var row=WeaponGeometryRevisionTests.Row();var original=Compile(row);string token=WeaponArtRevision.Token(row,[],original);
        WeaponArtRevision.ValidateApply(original,token,token,original.Geometry.ModelSha256,original.TextureSha256,original.IconSha256);
        var changed=change switch {"skin"=>Compile(row,SKColors.Red),"icon"=>Compile(row,icon:SKColors.Green),_=>original};
        if(change=="metadata")row.DbcFieldsJson="{\"spellVisualId\":225}";
        if(change=="source")row.SourceSha256=new string('1',64);
        string changedToken=WeaponArtRevision.Token(row,[],changed);
        Assert.NotEqual(token,changedToken);
        Assert.Throws<InvalidOperationException>(()=>WeaponArtRevision.ValidateApply(changed,changedToken,token,
            original.Geometry.ModelSha256,original.TextureSha256,original.IconSha256));
    }

    [Fact]
    public void OnlyTheOwnedCanonicalIconMayBeReplaced()
    {
        var row=WeaponGeometryRevisionTests.Row();var plan=Compile(row);row.EffectTextureCount=1;row.IconStem=plan.IconStem;
        var icon=new WeaponArtAuxiliary {Slot=1,Path=plan.IconMpqPath,Bytes=plan.IconBlp,Sha256=plan.IconSha256};
        WeaponArtRevision.ValidateTarget(row,[icon],row.DisplayId,row.ItemEntry,row.ModelSha256,row.TextureSha256);
        string token=WeaponArtRevision.Token(row,[icon],plan);
        icon.Sha256=new string('f',64);
        Assert.NotEqual(token,WeaponArtRevision.Token(row,[icon],plan));
        Assert.Throws<InvalidOperationException>(()=>WeaponArtRevision.ValidateTarget(row,[icon],row.DisplayId,row.ItemEntry,row.ModelSha256,row.TextureSha256));
        icon.Sha256=plan.IconSha256;icon.Path="Spells\\unrelated.blp";
        Assert.Throws<InvalidOperationException>(()=>WeaponArtRevision.ValidateTarget(row,[icon],row.DisplayId,row.ItemEntry,row.ModelSha256,row.TextureSha256));
    }

    [Theory]
    [InlineData("skin")][InlineData("icon")][InlineData("stale")]
    public async Task ProductionTransactionBoundaryRollsBackAllPriorWritesWhenAnyStageFails(string failure)
    {
        using var db=new TransactionDatabase();string[] before=db.Art.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>CustomWeaponBuildService.RunArtTransactionAsync(db,tx=> {
            Assert.Same(db.Current,tx);
            if(failure=="stale")throw new InvalidOperationException("stale locked target");
            db.Art[0]="new-model";
            if(failure=="skin")throw new InvalidOperationException("display conditional update affected zero rows");
            db.Art[1]="new-skin";
            throw new InvalidOperationException("icon insert failed");
        }));
        Assert.Equal(before,db.Art);Assert.True(db.RolledBack);Assert.False(db.Committed);
    }

    [Fact]
    public async Task ProductionTransactionBoundaryCommitsAllThreeMembersTogether()
    {
        using var db=new TransactionDatabase();
        await CustomWeaponBuildService.RunArtTransactionAsync(db,tx=>{db.Art=["new-model","new-skin","new-icon"];return Task.CompletedTask;});
        Assert.Equal(new[]{"new-model","new-skin","new-icon"},db.Art);Assert.True(db.Committed);Assert.False(db.RolledBack);
    }

    // Transactional storage fixture exercises the production begin/commit/rollback boundary,
    // including failures after earlier writes. It does not substitute for a MySQL integration run.
    internal sealed class TransactionDatabase : DbConnection
    {
        public string[] Art=["old-model","old-skin","old-icon"];
        public bool Committed, RolledBack;public DbTransaction? Current;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString {get;set;}="";
        public override string Database=>"fixture";public override string DataSource=>"fixture";
        public override string ServerVersion=>"fixture";public override ConnectionState State=>ConnectionState.Open;
        public override void ChangeDatabase(string databaseName)=>throw new NotSupportedException();
        public override void Open(){}public override void Close(){}
        protected override DbCommand CreateDbCommand()=>throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)=>Current=new Transaction(this);
        private sealed class Transaction(TransactionDatabase db) : DbTransaction
        {
            private readonly string[] before=db.Art.ToArray();
            public override IsolationLevel IsolationLevel=>IsolationLevel.ReadCommitted;
            protected override DbConnection DbConnection=>db;
            public override void Commit()=>db.Committed=true;
            public override void Rollback(){db.Art=before;db.RolledBack=true;}
        }
    }
}
