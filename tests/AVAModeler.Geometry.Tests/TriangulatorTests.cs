using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Geometry.Tests.TestShapes;

namespace AVAModeler.Geometry.Tests;

public class TriangulatorTests
{
    private static double TriangleArea(IReadOnlyList<Vec3> pts, int[] t) =>
        t.Chunk(3).Sum(x => Vec3.Cross(pts[x[1]] - pts[x[0]], pts[x[2]] - pts[x[0]]).Length / 2);

    private static Vec3 TriangleNormal(IReadOnlyList<Vec3> pts, int a, int b, int c) => Vec3.Cross(pts[b] - pts[a], pts[c] - pts[a]).Normalized();

    [Fact]
    public void TrianglesAndConvexQuadsTakeTheQuickPath()
    {
        Assert.Equal([0, 1, 2], Triangulator.Triangulate([Vec3.Zero, Vec3.UnitX, Vec3.UnitY]));
        // A kite: the shorter diagonal (1–3) is used.
        Vec3[] kite = [new(0, 0, 0), new(1, 0.2, 0), new(4, 0, 0), new(1, -0.2, 0)];
        var t = Triangulator.Triangulate(kite);
        Assert.Equal([0, 1, 3, 1, 2, 3], t);
    }

    [Fact]
    public void ConcaveNGonKeepsAreaAndWinding()
    {
        // A star of 5 points, concave at every other point, in a tilted plane, wound clockwise and anticlockwise.
        var star = new List<Vec3>();
        var tilt = Mat4.Rotation(new Vec3(1, 2, 3), 0.7);
        for (int i = 0; i < 10; i++)
        {
            double a = Math.PI * i / 5, r = i % 2 == 0 ? 2 : 0.8;
            star.Add(tilt.TransformPoint(new Vec3(Math.Cos(a) * r, Math.Sin(a) * r, 0)));
        }
        foreach (var outline in new[] { star, star.AsEnumerable().Reverse().ToList() })
        {
            var t = Triangulator.Triangulate(outline);
            Assert.Equal(8 * 3, t.Length);
            var n = outline.Select((p, i) => Vec3.Cross(p, outline[(i + 1) % outline.Count])).Aggregate(Vec3.Zero, (a, b) => a + b).Normalized();
            double area = 0;
            for (int i = 0; i < outline.Count; i++) area += Vec3.Dot(n, Vec3.Cross(outline[i], outline[(i + 1) % outline.Count])) / 2;
            Assert.Equal(area, TriangleArea(outline, t), 1e-12);
            foreach (var x in t.Chunk(3)) Assert.True(Vec3.Dot(TriangleNormal(outline, x[0], x[1], x[2]), n) > 0.999);
        }
    }

    [Fact]
    public void NonPlanarFacesStillCover()
    {
        Vec3[] saddle = [new(0, 0, 0), new(1, 0, 0.3), new(2, 0, 0), new(2, 1, 0.3), new(2, 2, 0), new(0, 2, 0.3)];
        var t = Triangulator.Triangulate(saddle);
        Assert.Equal(4 * 3, t.Length);
        Assert.Equal(6, t.Distinct().Count());
    }

    [Fact]
    public void DegenerateOutlinesGiveNothing()
    {
        Assert.Empty(Triangulator.Triangulate([]));
        Assert.Empty(Triangulator.Triangulate([Vec3.Zero, Vec3.UnitX]));
        Assert.Empty(Triangulator.Triangulate([Vec3.Zero, Vec3.UnitX, Vec3.UnitX * 2, Vec3.UnitX * 3]));
        // A repeated point is harmless.
        var t = Triangulator.Triangulate([Vec3.Zero, Vec3.UnitX, Vec3.UnitX, new Vec3(1, 1, 0), Vec3.UnitY]);
        Assert.Equal(1, TriangleArea([Vec3.Zero, Vec3.UnitX, Vec3.UnitX, new Vec3(1, 1, 0), Vec3.UnitY], t), 1e-12);
    }

    [Fact]
    public void MeshTrianglesFaceTheSameWayAsTheirFaces()
    {
        var m = Sphere(Vec3.Zero, 1, 12, 6);
        m.AddPolygon([0, 1]);
        m.AddPolygon([0]);
        var tris = Triangulator.TriangulateMesh(m);
        Assert.Equal(12 * 2 + 12 * 4 * 2, tris.Count);
        foreach (var t in tris)
            Assert.True(Vec3.Dot(TriangleNormal(m.Points, t.A, t.B, t.C), m.Normal(t.Polygon)) > 0.99);
    }

    [Fact]
    public void TripleSplitsLargerFacesOnly()
    {
        var box = Box(2, surface: 4);
        box.AddFace(0, Vec3.Zero, Vec3.UnitX, Vec3.UnitY);
        box.AddPolygon([0, 1]);
        Assert.Equal(6, Triangulator.Triple(box));
        Assert.Equal(6 * 2 + 2, box.Polygons.Count);
        Assert.Equal(1, box.Polygons.Count(p => p.Count == 2));
        Assert.True(box.Polygons.Take(6).All(p => p.Count == 3 && p.Surface == 4));
        Assert.Equal(8, MeshMeasure.Volume(box, Enumerable.Range(0, box.Polygons.Count).Where(i => box.Polygons[i].Surface == 4)), 1e-12);
        Assert.Equal(0, Triangulator.Triple(box));

        var hex = Disc(Vec3.Zero, 1, 6);
        Assert.Equal(1, Triangulator.Triple(hex, [0]));
        Assert.Equal(4, hex.Polygons.Count);
    }
}

public class MeasureAndBvhTests
{
    [Fact]
    public void VolumeAreaAndWatertightness()
    {
        var box = Box(new Vec3(0, 0, 0), new Vec3(1, 2, 3));
        Assert.Equal(6, MeshMeasure.Volume(box), 1e-12);
        Assert.Equal(22, MeshMeasure.Area(box), 1e-12);
        Assert.True(MeshMeasure.IsWatertight(box));
        Assert.Empty(MeshMeasure.OpenEdges(box));
        box.Flip(0);
        Assert.False(MeshMeasure.IsWatertight(box));
        box.RemovePolygons([0]);
        Assert.Equal(4, MeshMeasure.OpenEdges(box).Count);
        Assert.False(MeshMeasure.IsWatertight(new Mesh()));
    }

    [Fact]
    public void RaycastFindsTheNearestFace()
    {
        var m = Box(2);
        m.Append(Box(new Vec3(-1, -1, 4), new Vec3(1, 1, 6)));
        var bvh = new TriangleBvh(m);
        Assert.Equal(24, bvh.TriangleCount);
        var hit = bvh.Raycast(new Ray(new Vec3(0.2, 0.3, -10), Vec3.UnitZ));
        Assert.NotNull(hit);
        Assert.Equal(9, hit.Value.Distance, 1e-12);
        AssertClose(new Vec3(0.2, 0.3, -1), hit.Value.Point);
        Assert.True(m.Normal(hit.Value.Polygon).Z < -0.999);
        // From between the boxes, looking forward, the second box's front.
        var mid = bvh.Raycast(new Ray(new Vec3(0, 0, 2), Vec3.UnitZ));
        Assert.Equal(2, mid!.Value.Distance, 1e-12);
        Assert.Null(bvh.Raycast(new Ray(new Vec3(5, 5, -10), Vec3.UnitZ)));
        Assert.Null(bvh.Raycast(new Ray(new Vec3(0, 0, -10), Vec3.UnitZ), maxDistance: 5));
    }

    [Fact]
    public void RaycastMatchesBruteForceOnASphere()
    {
        var m = Sphere(new Vec3(1, 2, 3), 2, 32, 16);
        var bvh = new TriangleBvh(m);
        var tris = Triangulator.TriangulateMesh(m);
        var rng = new Random(1);
        for (int i = 0; i < 50; i++)
        {
            var origin = new Vec3(rng.NextDouble() * 10 - 5, rng.NextDouble() * 10 - 5, -10);
            var ray = new Ray(origin, new Vec3(1, 2, 3) + new Vec3(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()) - origin);
            double best = double.PositiveInfinity;
            foreach (var t in tris)
                if (ray.IntersectTriangle(m.Points[t.A], m.Points[t.B], m.Points[t.C], 0) is { } h) best = Math.Min(best, h.T);
            var hit = bvh.Raycast(ray);
            Assert.NotNull(hit);
            Assert.Equal(best, hit.Value.Distance, 1e-12);
        }
    }

    [Fact]
    public void NearestPointOnTheMesh()
    {
        var bvh = new TriangleBvh(Box(2));
        var n = bvh.Nearest(new Vec3(0.3, 5, -0.2));
        Assert.NotNull(n);
        AssertClose(new Vec3(0.3, 1, -0.2), n.Value.Point);
        Assert.Equal(4, n.Value.Distance, 1e-12);
        var corner = bvh.Nearest(new Vec3(3, 3, 3))!.Value;
        AssertClose(Vec3.One, corner.Point);
        var inside = bvh.Nearest(new Vec3(0.9, 0, 0))!.Value;
        Assert.Equal(0.1, inside.Distance, 1e-12);

        var empty = new TriangleBvh(new Mesh());
        Assert.Null(empty.Nearest(Vec3.Zero));
        Assert.Null(empty.Raycast(new Ray(Vec3.Zero, Vec3.UnitX)));
    }
}
