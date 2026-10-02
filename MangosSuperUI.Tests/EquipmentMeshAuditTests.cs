using System.Numerics;
using MangosSuperUI.Services.WeaponForge;
using Xunit;

namespace MangosSuperUI.Tests;

public class EquipmentMeshAuditTests
{
    private static RigidWeaponMesh Mesh(Vector3[] positions, uint[] indices, Vector2[]? uv = null) => new()
    {
        Positions = positions, Indices = indices,
        Normals = Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
        Uv0 = uv ?? positions.Select(p => new Vector2(p.X, p.Y)).ToArray(),
        Material = new WeaponMaterial()
    };

    [Fact]
    public void SharedEdgeAcrossUvSeam_IsWeldedWithoutChangingSource()
    {
        var mesh = Mesh([new(0,0,0), new(1,0,0), new(0,1,0), new(1,0,0), new(1,1,0), new(0,1,0)],
            [0,1,2,3,4,5]);
        var report = EquipmentMeshAudit.Analyze(mesh);
        Assert.Equal(4, report.UniquePositions);
        Assert.Equal(4, report.BoundaryEdges);
        Assert.Equal(1, report.ConnectedSurfaceComponents);
        Assert.Equal(0, report.InconsistentWindingEdges);
        Assert.Equal(6, mesh.VertexCount);
        Assert.False(report.RuntimeVerified);
    }

    [Fact]
    public void ReversedAdjacentFace_ReportsActualWindingAndNormalDefects()
    {
        var report = EquipmentMeshAudit.Analyze(Mesh([new(0,0,0),new(1,0,0),new(0,1,0),new(1,1,0)],
            [0,1,2,1,2,3]));
        Assert.Equal(1, report.InconsistentWindingEdges);
        Assert.Equal(1, report.NormalsOpposeWinding);
    }

    [Fact]
    public void InvalidInput_IsReportedWithoutThrowingOrSerializingNan()
    {
        var report = EquipmentMeshAudit.Analyze(Mesh([new(float.NaN,0,0)], [0,1,2]));
        Assert.Equal(2, report.Errors.Count);
        Assert.Empty(report.BoundsMin);
        Assert.False(report.RuntimeVerified);
    }

    [Fact]
    public void CollapsedUvAndDuplicateFaces_AreDistinctFromZeroAreaGeometry()
    {
        var report = EquipmentMeshAudit.Analyze(Mesh([new(0,0,0),new(1,0,0),new(0,1,0)],
            [0,1,2,0,1,2], [Vector2.Zero,Vector2.Zero,Vector2.Zero]));
        Assert.Equal(0, report.DegenerateTriangles);
        Assert.Equal(2, report.DegenerateUvTriangles);
        Assert.Equal(1, report.DuplicateSurfaceTriangles);
        Assert.Equal(3, report.InconsistentWindingEdges);
    }

    [Fact]
    public void FingerprintChangesForUvOrTopologyChange()
    {
        Vector3[] p = [new(0,0,0),new(1,0,0),new(0,1,0)];
        var first = EquipmentMeshAudit.Analyze(Mesh(p,[0,1,2])).MeshSha256;
        Assert.NotEqual(first, EquipmentMeshAudit.Analyze(Mesh(p,[0,2,1])).MeshSha256);
        Assert.NotEqual(first, EquipmentMeshAudit.Analyze(Mesh(p,[0,1,2], [Vector2.Zero,Vector2.One,Vector2.UnitY])).MeshSha256);
    }
}
