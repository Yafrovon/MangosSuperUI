using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MangosSuperUI.Services.ArmorForge;
using MangosSuperUI.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace MangosSuperUI.Tests;

public sealed class AuthoredArmorPackageTests
{
    [Fact]
    public void ShoulderFitsAcceptSelectiveSidesAndKeepBothDefaultsRequired()
    {
        var package = Package();
        var piece = package.Manifest.Pieces.Single(p => p.Key == "shoulder");
        var assets = package.Assets.ToDictionary(x => x.Key, x => x.Value);
        assets.Add("models/shoulder_R_NiF.glb", Glb());
        piece.ShoulderFits.Add("NiF", new() { ["R"] = "models/shoulder_R_NiF.glb" });
        package = new() { Manifest = package.Manifest, Assets = assets, Sha256 = package.Sha256 };
        var report = new AuthoredArmorAssetValidator().Validate(package);
        Assert.True(report.Valid, string.Join(";", report.Issues));
        Assert.Equal(19, report.Meshes.Count);
        piece.Models.Remove("L");
        Assert.False(new AuthoredArmorAssetValidator().Validate(package).Valid);
    }

    [Theory]
    [InlineData("NeF", "R", "models/fit.glb")]
    [InlineData("nif", "R", "models/fit.glb")]
    [InlineData("NiF", "left", "models/fit.glb")]
    [InlineData("NiF", "R", "../fit.glb")]
    [InlineData("NiF", "R", "models/shoulder_L.glb")]
    [InlineData("NiF", "R", "models/missing.glb")]
    public void InvalidShoulderFitDeclarationsAreRejected(string body, string side, string path)
    {
        var package = Package();
        package.Manifest.Pieces.Single(p => p.Key == "shoulder").ShoulderFits.Add(body, new() { [side] = path });
        Assert.False(new AuthoredArmorAssetValidator().Validate(package).Valid);
    }

    [Theory]
    [InlineData("helm")]
    [InlineData("chest")]
    public void ShoulderFitsCannotCreateBodyOrHelmExceptions(string key)
    {
        var package = Package();
        package.Manifest.Pieces.Single(p => p.Key == key).ShoulderFits.Add("NiF", new() { ["R"] = "models/shoulder_R.glb" });
        Assert.False(new AuthoredArmorAssetValidator().Validate(package).Valid);
    }

    [Fact]
    public void RetryPreservesMatchingRowsAndOnlyInsertsMissingRows()
    {
        Assert.Equal(AuthoredArmorResumeDisposition.Missing, AuthoredArmorResumePolicy.Decide(60001, 5001, Array.Empty<(long, int)>()));
        Assert.Equal(AuthoredArmorResumeDisposition.AlreadyApplied, AuthoredArmorResumePolicy.Decide(60001, 5001, new[] { (60001L, 5001) }));
    }

    [Fact]
    public void RetryRefusesOtherDisplayOtherSetAndMultiplePatchRows()
    {
        Assert.Equal(AuthoredArmorResumeDisposition.Conflict, AuthoredArmorResumePolicy.Decide(60001, 5001, new[] { (60002L, 5001) }));
        Assert.Equal(AuthoredArmorResumeDisposition.Conflict, AuthoredArmorResumePolicy.Decide(60001, 5001, new[] { (60001L, 5002) }));
        Assert.Equal(AuthoredArmorResumeDisposition.Conflict, AuthoredArmorResumePolicy.Decide(60001, 5001, new[] { (60001L, 5001), (60001L, 5001) }));
    }

    [Fact]
    public void CompleteOriginalPackage_ReportsEveryAttachmentAndNeverClaimsRuntimeVerification()
    {
        var report = new AuthoredArmorAssetValidator().Validate(Package());
        Assert.True(report.Valid, string.Join("\n", report.Issues));
        Assert.Equal(18, report.Meshes.Count);
        Assert.Equal("unverified", report.RuntimeStatus);
        Assert.False(report.Compiled);
    }

    [Fact]
    public void MissingHelmVariant_IsRejectedRatherThanSubstitutingHumanMesh()
    {
        var package = Package(); package.Manifest.Pieces[0].Models.Remove("TaF");
        var report = new AuthoredArmorAssetValidator().Validate(package);
        Assert.False(report.Valid);
        Assert.Contains(report.Issues, i => i.Asset == "helm" && i.Message.Contains("TaF"));
    }

    [Fact]
    public void BodySlotNeedsCoverageForBothGenders()
    {
        var package = Package(); package.Manifest.Pieces.Single(p => p.Key == "gloves").Components[0].Gender = "_F";
        var report = new AuthoredArmorAssetValidator().Validate(package);
        Assert.Contains(report.Issues, i => i.Asset == "gloves" && i.Severity == "error" && i.Message.Contains("both"));
    }

    [Fact]
    public void SamePngReferencedAsIconAndSkin_StillChecksBothDimensionContracts()
    {
        var package = Package(); package.Manifest.Pieces[0].SkinPng = "icon.png";
        var report = new AuthoredArmorAssetValidator().Validate(package);
        Assert.Contains(report.Issues, i => i.Asset == "icon.png" && i.Severity == "error" && i.Message.Contains("128×128"));
    }

    [Theory]
    [InlineData(256, 128, true)]
    [InlineData(128, 256, true)]
    [InlineData(64, 128, false)]
    [InlineData(256, 192, false)]
    public void SharedModelSkinAllowsRectangularNativeAtlasWithinBudget(int width, int height, bool expectedValid)
    {
        var original = Package();
        var assets = original.Assets.ToDictionary(p => p.Key, p => p.Value);
        assets["skin.png"] = Png(width, height);
        var package = new AuthoredArmorPackage { Manifest = original.Manifest, Assets = assets, Sha256 = original.Sha256 };
        var report = new AuthoredArmorAssetValidator().Validate(package);
        Assert.Equal(expectedValid, report.Valid);
        if (expectedValid)
            Assert.Contains(report.Textures, t => t.Asset == "skin.png" && t.Width == width && t.Height == height);
        else
            Assert.Contains(report.Issues, i => i.Asset == "skin.png" && i.Severity == "error" && i.Message.Contains("Model skin"));
    }

    [Fact]
    public void PaintCannotOverwriteAnUnrelatedBodyRegion()
    {
        var package = Package(); package.Manifest.Pieces.Single(p => p.Key == "boots").Components.Add(new() { Slot = 3, Png = "body64.png" });
        Assert.Contains(new AuthoredArmorAssetValidator().Validate(package).Issues, i => i.Asset == "boots" && i.Message.Contains("out-of-slot"));
    }

    [Fact]
    public void ClothShoesMayOmitShinPaintToPreserveRobeHem()
    {
        var package = Package();
        package.Manifest.Material = "cloth";
        package.Manifest.Pieces.Single(p => p.Key == "boots").Components.RemoveAll(c => c.Slot == 6);
        var report = new AuthoredArmorAssetValidator().Validate(package);
        Assert.True(report.Valid, string.Join("\n", report.Issues));
    }

    [Theory]
    [InlineData("Male", false)]
    [InlineData("Female", false)]
    [InlineData("Male", true)]
    [InlineData("Female", true)]
    public void FootOnlyShoeDressingPreservesMissingShinAndSelectsActualGender(string gender, bool gendered)
    {
        var package = Package();
        package.Manifest.Material = "cloth";
        var shoes = package.Manifest.Pieces.Single(p => p.Key == "boots");
        shoes.Components.RemoveAll(c => c.Slot == 6);
        if (gendered)
        {
            shoes.Components[0].Gender = "_M";
            shoes.Components.Add(new() { Slot = 7, Gender = "_F", Png = "body32.png" });
        }
        Assert.True(new AuthoredArmorAssetValidator().Validate(package).Valid);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var file = archive.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(file, package.Manifest, AuthoredArmorPackage.JsonOptions);
            foreach (var (name, bytes) in package.Assets) { using var file = archive.CreateEntry(name).Open(); file.Write(bytes); }
        }
        byte[] zip = stream.ToArray(); string id = AuthoredArmorPackage.Hash(zip);
        string root = Path.Combine(Path.GetTempPath(), "msui-shoe-dressing-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(root, "App_Data", "authored-armor", id);
        Directory.CreateDirectory(stage);
        try
        {
            File.WriteAllBytes(Path.Combine(stage, "package.zip"), zip);
            var controller = new AuthoredArmorController(null!, null!, null!, null!,
                new DressingEnvironment { ContentRootPath = root }, NullLogger<AuthoredArmorController>.Instance);
            var result = Assert.IsType<JsonResult>(controller.Dressing(id, "boots", "Human", gender));
            var payload = JsonSerializer.SerializeToElement(result.Value);
            var slots = payload.GetProperty("slotUrls");
            Assert.Single(slots.EnumerateObject());
            string suffix = gendered ? gender == "Female" ? "_F" : "_M" : "_U";
            Assert.EndsWith("boots_slot7" + suffix + ".png", slots.GetProperty("7").GetString());
            Assert.Equal("unverified", payload.GetProperty("runtimeStatus").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class DressingEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void PaintedItemCannotHaveNoVisibleComponents()
    {
        var package = Package();
        package.Manifest.Pieces.Single(p => p.Key == "boots").Components.Clear();
        Assert.Contains(new AuthoredArmorAssetValidator().Validate(package).Issues,
            i => i.Asset == "boots" && i.Severity == "error" && i.Message.Contains("at least one"));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("C:/outside.png")]
    [InlineData("textures\\outside.png")]
    [InlineData("/outside.png")]
    public void ZipRejectsPathsWithoutExtractingAnything(string path)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        { using var file = archive.CreateEntry(path).Open(); file.WriteByte(1); }
        Assert.Throws<InvalidDataException>(() => AuthoredArmorPackage.Read(stream.ToArray()));
    }

    [Fact]
    public void ZipRejectsCaseInsensitiveDuplicates()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (string name in new[] { "texture.png", "TEXTURE.PNG" }) { using var file = archive.CreateEntry(name).Open(); file.WriteByte(1); }
        Assert.Throws<InvalidDataException>(() => AuthoredArmorPackage.Read(stream.ToArray()));
    }

    [Fact]
    public void ZipRejectsExpandedMemberBeforeAllocatingItsPixels()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        { using var file = archive.CreateEntry("huge.png").Open(); file.Write(new byte[AuthoredArmorPackage.MaxMemberBytes + 1]); }
        Assert.Throws<InvalidDataException>(() => AuthoredArmorPackage.Read(stream.ToArray()));
    }

    [Fact]
    public void GlbKeepsAuthoredOriginAndScale()
    {
        var positions = new[] { new Vector3(2, 3, 4), new Vector3(2.1f, 3, 4), new Vector3(2, 3.1f, 4) };
        var report = new AuthoredArmorReport(); var mesh = AuthoredArmorAssetValidator.ReadMesh(Glb(positions), "helm.glb", report);
        Assert.True(report.Valid); Assert.Equal(positions, mesh.Positions);
        Assert.Equal(3, report.Meshes.Single().BoundaryEdges);
    }

    [Fact]
    public void CollapsedUvIsAnErrorWithoutDiscardingTriangles()
    {
        var report = new AuthoredArmorReport();
        var mesh = AuthoredArmorAssetValidator.ReadMesh(Glb(uvs: new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero }), "bad.glb", report);
        Assert.Equal(1, mesh.TriangleCount); Assert.False(report.Valid);
        Assert.Equal(1, report.Meshes.Single().DegenerateUvFaces);
    }

    [Fact]
    public void ReversedNormalsAreRejected()
    {
        var report = new AuthoredArmorReport(); AuthoredArmorAssetValidator.ReadMesh(Glb(normal: -Vector3.UnitZ), "bad.glb", report);
        Assert.False(report.Valid); Assert.Equal(1, report.Meshes.Single().OpposedNormals);
    }

    [Fact]
    public void FullZipRoundtripPreservesHashAndAllReferencedFiles()
    {
        var package = Package(); using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var file = archive.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(file, package.Manifest, AuthoredArmorPackage.JsonOptions);
            foreach (var (name, bytes) in package.Assets) { using var file = archive.CreateEntry(name).Open(); file.Write(bytes); }
        }
        var zip = stream.ToArray(); var read = AuthoredArmorPackage.Read(zip);
        Assert.Equal(AuthoredArmorPackage.Hash(zip), read.Sha256);
        Assert.True(new AuthoredArmorAssetValidator().Validate(read).Valid);
    }

    private static AuthoredArmorPackage Package()
    {
        var manifest = new AuthoredArmorManifest { SchemaVersion = 1, Name = "Test armor", Material = "plate", Author = "Test artist", DesignNotes = "Original test geometry and paint." };
        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        { ["icon.png"] = Png(64, 64), ["skin.png"] = Png(128, 128), ["body64.png"] = Png(128, 64), ["body32.png"] = Png(128, 32) };
        foreach (string key in new[] { "helm", "shoulder", "chest", "legs", "gloves", "boots", "bracers", "belt" })
        {
            var piece = new AuthoredArmorPiece { Key = key, Name = "Test " + key, IconPng = "icon.png" };
            var profile = ArmorTypeCatalog.Get(key);
            if (profile.RenderKind == ArmorRenderKind.Modelled)
            {
                piece.SkinPng = "skin.png";
                foreach (var suffix in key == "helm" ? ArmorNaming.HelmVariantSuffixes : new[] { "L", "R" })
                { string path = $"models/{key}_{suffix}.glb"; piece.Models.Add(suffix, path); assets.Add(path, Glb()); }
            }
            foreach (int slot in profile.PaintedSlots) piece.Components.Add(new() { Slot = slot, Gender = "_U", Png = slot is 2 or 4 or 7 ? "body32.png" : "body64.png" });
            manifest.Pieces.Add(piece);
        }
        return new() { Manifest = manifest, Assets = assets, Sha256 = new string('a', 64) };
    }
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height); bitmap.Erase(new SKColor(105, 75, 45, 255));
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100); return png.ToArray();
    }
    internal static byte[] Glb(Vector3[]? positions = null, Vector2[]? uvs = null, Vector3? normal = null)
    {
        positions ??= new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        uvs ??= new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY };
        using var binary = new MemoryStream(); using (var w = new BinaryWriter(binary, Encoding.UTF8, true))
        {
            foreach (var p in positions) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
            for (int i = 0; i < 3; i++) { var n = normal ?? Vector3.UnitZ; w.Write(n.X); w.Write(n.Y); w.Write(n.Z); }
            foreach (var uv in uvs) { w.Write(uv.X); w.Write(uv.Y); }
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
        }
        var min = positions.Aggregate(Vector3.Min); var max = positions.Aggregate(Vector3.Max);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            asset = new { version = "2.0" }, scene = 0, scenes = new[] { new { nodes = new[] { 0 } } }, nodes = new[] { new { mesh = 0 } },
            meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0, NORMAL = 1, TEXCOORD_0 = 2 }, indices = 3, mode = 4 } } } },
            buffers = new[] { new { byteLength = 102 } },
            bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 36 }, new { buffer = 0, byteOffset = 36, byteLength = 36 }, new { buffer = 0, byteOffset = 72, byteLength = 24 }, new { buffer = 0, byteOffset = 96, byteLength = 6 } },
            accessors = new object[] { new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3", min = new[] { min.X, min.Y, min.Z }, max = new[] { max.X, max.Y, max.Z } },
                new { bufferView = 1, componentType = 5126, count = 3, type = "VEC3" }, new { bufferView = 2, componentType = 5126, count = 3, type = "VEC2" }, new { bufferView = 3, componentType = 5123, count = 3, type = "SCALAR" } },
        });
        int paddedJson = (json.Length + 3) & ~3; using var result = new MemoryStream(); using var writer = new BinaryWriter(result);
        writer.Write(0x46546C67u); writer.Write(2u); writer.Write((uint)(12 + 8 + paddedJson + 8 + 104));
        writer.Write(paddedJson); writer.Write(0x4E4F534Au); writer.Write(json); for (int i = json.Length; i < paddedJson; i++) writer.Write((byte)32);
        writer.Write(104); writer.Write(0x004E4942u); writer.Write(binary.ToArray()); writer.Write((ushort)0); return result.ToArray();
    }
}
