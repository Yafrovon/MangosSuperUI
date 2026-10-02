using MangosSuperUI.Services;
using MangosSuperUI.Services.WeaponForge;
using Xunit;

namespace MangosSuperUI.Tests;

public sealed class EquipmentReferenceServiceTests
{
    [Fact]
    public void BoundsReportReaderAndOriginalWowAxesWithoutAssumingOriginIsMinimum()
    {
        var model = Triangle();
        var result = EquipmentReferenceService.GeometryStatistics(model);
        Assert.Equal(new float[] { -2, 1, -4 }, result.ReaderYUpBounds!.Min);
        Assert.Equal(new float[] { 3, 5, 2 }, result.ReaderYUpBounds.Max);
        Assert.Equal(new float[] { -2, -2, 1 }, result.WowZUpBounds!.Min);
        Assert.Equal(new float[] { 3, 4, 5 }, result.WowZUpBounds.Max);
        Assert.Equal(1, result.TriangleCount);
    }

    [Fact]
    public void GeometryFingerprintDoesNotCountRecolorsAsDifferentMeshesButDetectsShapeChanges()
    {
        var original = Triangle();
        var recolored = Triangle();
        recolored.Textures.Add(new M2TextureRef { Type = 0, Filename = "different.blp" });
        Assert.Equal(EquipmentReferenceService.GeometryStatistics(original).GeometrySha256,
            EquipmentReferenceService.GeometryStatistics(recolored).GeometrySha256);
        var changed = recolored.Vertices[0]; changed.PosX += .01f; recolored.Vertices[0] = changed;
        Assert.NotEqual(EquipmentReferenceService.GeometryStatistics(original).GeometrySha256,
            EquipmentReferenceService.GeometryStatistics(recolored).GeometrySha256);
    }

    [Fact]
    public void InvalidPositionsAreReportedAndCannotPolluteJsonBounds()
    {
        var model = Triangle();
        model.Vertices.Add(new M2Vertex { PosX = float.NaN, PosY = float.PositiveInfinity });
        var result = EquipmentReferenceService.GeometryStatistics(model);
        Assert.Equal(1, result.NonFinitePositions);
        Assert.Equal(4, result.VertexCount);
        Assert.All(result.ReaderYUpBounds!.Min, x => Assert.True(float.IsFinite(x)));
    }

    [Fact]
    public void QuantilesUseLinearInterpolationAndEmptyGeometryHasNoInventedZeroBudget()
    {
        var q = EquipmentReferenceService.Quantiles(new double[] { 100, 400, 200, 300 })!;
        Assert.Equal(250, q.P50);
        Assert.Equal(370, q.P90);
        Assert.Equal(175, q.P25);
        Assert.Null(EquipmentReferenceService.Quantiles(Array.Empty<double>()));
    }

    [Fact]
    public void TextureLuminanceExcludesHiddenRgbAndWeightsPartialAlphaInLinearSpace()
    {
        // Fully opaque red; fully transparent green must not brighten the result; half-alpha blue.
        byte[] pixels = { 0, 0, 255, 255, 0, 255, 0, 0, 255, 0, 0, 128 };
        var result = EquipmentReferenceService.TextureStatistics(pixels, 3, 1);
        Assert.Equal(2, result.VisiblePixels);
        Assert.Equal(1, result.TranslucentPixels);
        Assert.Equal((.2126 + .0722 * (128.0 / 255)) / (1 + 128.0 / 255), result.MeanLuminance!.Value, 10);
        Assert.Equal(1, result.LuminanceHistogram.Sum(), 10);
        Assert.Equal(2.0 / 3, result.VisibleCoverage, 10);
        Assert.Throws<ArgumentException>(() => EquipmentReferenceService.TextureStatistics(pixels, 4, 1));
    }

    [Fact]
    public void FullyTransparentTextureHasNoMeasuredLuminance()
    {
        var result = EquipmentReferenceService.TextureStatistics(new byte[] { 255, 255, 255, 0 }, 1, 1);
        Assert.Null(result.MeanLuminance);
        Assert.Equal(0, result.LuminanceHistogram.Sum());
    }

    [Theory]
    [InlineData("wotlk")]
    [InlineData("classic")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedSourcesNeverSilentlyFallBackToTbc(string? source)
        => Assert.Throws<ArgumentException>(() => EquipmentReferenceService.ValidateSource(source));

    private static M2Model Triangle() => new()
    {
        Vertices = new()
        {
            new M2Vertex { PosX = -2, PosY = 1, PosZ = -4 },
            new M2Vertex { PosX = 3, PosY = 1, PosZ = 2 },
            new M2Vertex { PosX = 0, PosY = 5, PosZ = -1 }
        },
        Indices = new() { 0, 1, 2 }
    };
}
