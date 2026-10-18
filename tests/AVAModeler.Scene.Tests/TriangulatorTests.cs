using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

public class TriangulatorTests
{
    private static double TriangleArea(Vec3 a, Vec3 b, Vec3 c) => Vec3.Cross(b - a, c - a).Length / 2;

    /// <summary>Triangulates a face and checks the triangles cover its area and all face its way.</summary>
    private static List<(int A, int B, int C)> Check(Mesh mesh, Polygon poly, int expectedCount)
    {
        var tris = Triangulator.Triangulate(mesh, poly);
        Assert.Equal(expectedCount, tris.Count);
        var n = mesh.Normal(poly);
        double area = 0;
        foreach (var (a, b, c) in tris)
        {
            var tri = new Polygon([a, b, c]);
            area += TriangleArea(mesh.Points[a], mesh.Points[b], mesh.Points[c]);
            Assert.True(Vec3.Dot(mesh.Normal(tri), n) > 0.99, "A triangle faces the wrong way.");
        }
        AssertNear(mesh.Area(poly), area, 1e-9);
        return tris;
    }

    [Fact]
    public void TrianglesAndQuads()
    {
        var m = Square(Vec3.Zero, 2, Vec3.UnitY);
        Check(m, m.Polygons[0], 2);
        var t = new Mesh();
        t.AddFace(0, Vec3.Zero, Vec3.UnitX, Vec3.UnitY);
        Check(t, t.Polygons[0], 1);
    }

    [Fact]
    public void ConcaveShapesBothWindings()
    {
        // An L shape, then the same wound the other way.
        Vec3[] l = [new(0, 0, 0), new(2, 0, 0), new(2, 1, 0), new(1, 1, 0), new(1, 2, 0), new(0, 2, 0)];
        var m = new Mesh();
        m.AddFace(0, l);
        m.AddFace(0, [.. l.Reverse()]);
        Check(m, m.Polygons[0], 4);
        Check(m, m.Polygons[1], 4);
    }

    [Fact]
    public void StarShapeInAnyPlane()
    {
        var pts = new List<Vec3>();
        for (int i = 0; i < 10; i++)
        {
            double a = Math.PI * 2 * i / 10, r = i % 2 == 0 ? 2 : 0.7;
            pts.Add(new Vec3(Math.Cos(a) * r, Math.Sin(a) * r, 0));
        }
        var m = new Mesh();
        m.AddFace(0, [.. pts]);
        m.Transform(Rotations.Matrix(new Vec3(30, 50, 70)) * Mat4.Translation(new Vec3(1, 2, 3)));
        Check(m, m.Polygons[0], 8);
    }

    [Fact]
    public void CollinearPointsAndSlightlyBentFacesStillWork()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(2, 0, 0), new Vec3(2, 2, 0.01), new Vec3(0, 2, 0));
        var tris = Triangulator.Triangulate(m, m.Polygons[0]);
        Assert.Equal(3, tris.Count);
        Assert.All(tris, t => Assert.True(TriangleArea(m.Points[t.A], m.Points[t.B], m.Points[t.C]) > 1e-6));
    }

    [Fact]
    public void LinesAndPointsGiveNothing()
    {
        Assert.Empty(Triangulator.Triangulate([Vec3.Zero, Vec3.One], Vec3.UnitZ));
        Assert.Empty(Triangulator.Triangulate([Vec3.Zero], Vec3.UnitZ));
    }

    [Fact]
    public void DegenerateOutlinesStillFinish()
    {
        Vec3[] bowtie = [new(0, 0, 0), new(2, 2, 0), new(2, 0, 0), new(0, 2, 0)];
        Assert.Equal(2, Triangulator.Triangulate(bowtie, Vec3.UnitZ).Count);
        Vec3[] flat = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 0, 0)];
        Assert.Equal(2, Triangulator.Triangulate(flat, Vec3.Zero).Count);
    }
}
