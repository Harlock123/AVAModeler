using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Geometry.Tests.TestShapes;

namespace AVAModeler.Geometry.Tests;

public class BooleanTests
{
    // Two 2-unit cubes overlapping by 1 in X and 0.5 in Y and Z (general position): overlap 1 × 1.5 × 1.5.
    private static readonly Mesh A = Box(new Vec3(-1, -1, -1), new Vec3(1, 1, 1), surface: 1);
    private static readonly Mesh B = Box(new Vec3(0, -0.5, -0.5), new Vec3(2, 1.5, 1.5), surface: 2);
    private const double Overlap = 1 * 1.5 * 1.5;

    private static void AssertSolid(Mesh m, double volume, double tol = 1e-9)
    {
        Assert.True(MeshMeasure.IsWatertight(m), $"open edges: {MeshMeasure.OpenEdges(m).Count}");
        Assert.Equal(volume, MeshMeasure.Volume(m), tol);
        // Fan-volume and LibTess triangles must agree: no face is self-overlapping.
        var tris = Triangulator.TriangulateMesh(m);
        double v = tris.Sum(t => Vec3.Dot(m.Points[t.A], Vec3.Cross(m.Points[t.B], m.Points[t.C]))) / 6;
        Assert.Equal(volume, v, tol);
    }

    [Fact]
    public void UnionOfOverlappingBoxes()
    {
        var r = Booleans.Union(A, B);
        AssertSolid(r, 8 + 8 - Overlap);
        Assert.Contains(r.Polygons, p => p.Surface == 1);
        Assert.Contains(r.Polygons, p => p.Surface == 2);
    }

    [Fact]
    public void SubtractOfOverlappingBoxes()
    {
        var r = Booleans.Subtract(A, B);
        AssertSolid(r, 8 - Overlap);
    }

    [Fact]
    public void IntersectOfOverlappingBoxes()
    {
        var r = Booleans.Intersect(A, B);
        AssertSolid(r, Overlap);
        Assert.True(FacesPointAwayFrom(r, new Vec3(0.5, 0.25, 0.25)));
        // A box comes out as a box: the cut faces rejoin into six quads.
        Assert.Equal(6, r.Polygons.Count);
        Assert.Equal(8, r.Points.Count);
    }

    [Fact]
    public void CoplanarFacesAreHandled()
    {
        // Same height and depth: the top, bottom, front and back faces are shared planes.
        var a = Box(new Vec3(0, 0, 0), new Vec3(2, 1, 1));
        var b = Box(new Vec3(1, 0, 0), new Vec3(3, 1, 1));
        AssertSolid(Booleans.Union(a, b), 3);
        AssertSolid(Booleans.Subtract(a, b), 1);
        AssertSolid(Booleans.Intersect(a, b), 1);
        // Each original face's pieces rejoin; faces from different originals stay apart even when coplanar
        // (four long sides of two faces each, plus the two ends).
        Assert.Equal(10, Booleans.Union(a, b).Polygons.Count);
    }

    [Fact]
    public void SubtractMakesATunnel()
    {
        var a = Box(new Vec3(-1, -1, -1), new Vec3(1, 1, 1));
        var drill = Box(new Vec3(-0.5, -2, -0.5), new Vec3(0.5, 2, 0.5), surface: 3);
        var r = Booleans.Subtract(a, drill, new BooleanOptions { CutSurface = 7 });
        AssertSolid(r, 8 - 2);
        Assert.Equal(4, r.Polygons.Count(p => p.Surface == 7));
        Assert.DoesNotContain(r.Polygons, p => p.Surface == 3);
        // The cut walls face into the hole.
        foreach (var p in r.Polygons.Where(p => p.Surface == 7))
            Assert.True(Vec3.Dot(r.Normal(p), r.Centroid(p) with { Y = 0 }) < 0);
    }

    [Fact]
    public void SpheresStayClosedAndNearTheRightVolume()
    {
        var s1 = Sphere(Vec3.Zero, 1, 24, 12);
        var s2 = Sphere(new Vec3(0.8, 0.1, 0.05), 1, 24, 12);
        double v1 = MeshMeasure.Volume(s1);
        var u = Booleans.Union(s1, s2);
        var d = Booleans.Subtract(s1, s2);
        var i = Booleans.Intersect(s1, s2);
        foreach (var m in new[] { u, d, i })
            Assert.True(MeshMeasure.IsWatertight(m), $"open edges: {MeshMeasure.OpenEdges(m).Count}");
        double vu = MeshMeasure.Volume(u), vd = MeshMeasure.Volume(d), vi = MeshMeasure.Volume(i);
        // Inclusion–exclusion holds exactly for polyhedra: |A∪B| = |A| + |B| - |A∩B| and |A-B| = |A| - |A∩B|.
        Assert.Equal(v1 + MeshMeasure.Volume(s2) - vi, vu, 1e-9);
        Assert.Equal(v1 - vi, vd, 1e-9);
        // Two unit spheres 0.8066 apart overlap by π(4r + d)(2r - d)² / 12, scaled down as the faceted globe is.
        double dist = new Vec3(0.8, 0.1, 0.05).Length;
        double lens = Math.PI * (4 + dist) * Math.Pow(2 - dist, 2) / 12;
        Assert.Equal(lens * v1 / (4 * Math.PI / 3), vi, 0.05);
    }

    [Fact]
    public void BoxMinusSphereIsWatertight()
    {
        var box = Box(2);
        var s = Sphere(new Vec3(1, 1, 1), 0.8, 16, 8);
        var r = Booleans.Subtract(box, s);
        Assert.True(MeshMeasure.IsWatertight(r));
        Assert.True(MeshMeasure.Volume(r) < 8 && MeshMeasure.Volume(r) > 8 - 4.0 / 3 * Math.PI * 0.512 / 8 - 0.05);
    }

    [Fact]
    public void DisjointAndContainedSolids()
    {
        var a = Box(2);
        var far = Box(new Vec3(5, 5, 5), new Vec3(6, 6, 6));
        AssertSolid(Booleans.Union(a, far), 9);
        AssertSolid(Booleans.Subtract(a, far), 8);
        Assert.Empty(Booleans.Intersect(a, far).Polygons);
        var inner = Box(1);
        var hollow = Booleans.Subtract(a, inner);
        AssertSolid(hollow, 7);
        AssertSolid(Booleans.Intersect(a, inner), 1);
        AssertSolid(Booleans.Union(a, inner), 8);
    }

    [Fact]
    public void EmptySides()
    {
        var a = Box(2);
        var empty = new Mesh();
        Assert.Equal(8, MeshMeasure.Volume(Booleans.Union(empty, a)), 1e-9);
        Assert.Empty(Booleans.Subtract(empty, a).Polygons);
        Assert.Empty(Booleans.Intersect(empty, a).Polygons);
        Assert.Equal(8, MeshMeasure.Volume(Booleans.Subtract(a, empty)), 1e-9);
        Assert.Empty(Booleans.Intersect(a, empty).Polygons);
        Assert.Empty(Booleans.Union(empty, empty).Polygons);
    }

    [Fact]
    public void PointsAndLinesPassThroughAndAddCombines()
    {
        var a = Box(2);
        int p = a.AddPoint(new Vec3(9, 9, 9));
        int q = a.AddPoint(new Vec3(9, 8, 9));
        a.AddPolygon([p, q], 4);
        a.AddPolygon([p], 5);
        var b = Box(new Vec3(0, 0, 0), new Vec3(3, 3, 3));
        b.AddPolygon([0, 1]);
        var r = Booleans.Union(a, b);
        Assert.Single(r.Polygons, x => x.Count == 2 && x.Surface == 4);
        Assert.Single(r.Polygons, x => x.Count == 1 && x.Surface == 5);
        Assert.Contains(new Vec3(9, 9, 9), r.Points);

        var added = Booleans.Apply(BooleanOperation.Add, a, b);
        Assert.Equal(a.Polygons.Count + b.Polygons.Count, added.Polygons.Count);
        Assert.Equal(a.Points.Count + b.Points.Count, added.Points.Count);
    }

    [Fact]
    public void ConcaveAndNonPlanarFacesAreHandled()
    {
        // An L-shaped prism: its two caps are concave hexagons.
        var l = new Mesh();
        Vec2[] outline = [new(0, 0), new(0, 2), new(1, 2), new(1, 1), new(2, 1), new(2, 0)];
        var bottom = l.AddFace(0, outline.Select(v => new Vec3(v.X, 0, v.Y)).ToArray());
        if (l.Normal(bottom).Y > 0) l.Flip(bottom);
        int n = outline.Length;
        for (int i = 0; i < n; i++) l.Points.Add(l.Points[i] with { Y = 1 });
        var top = l.Polygons[bottom].Points.Select(i => i + n).Reverse().ToList();
        l.AddPolygon(top);
        var bp = l.Polygons[bottom].Points;
        for (int i = 0; i < n; i++)
        {
            int a = bp[i], b = bp[(i + 1) % n];
            l.AddPolygon([b, a, a + n, b + n]);
        }
        Assert.Equal(3, MeshMeasure.Volume(l), 1e-12);
        var cutter = Box(new Vec3(0.5, -1, 0.5), new Vec3(1.5, 2, 1.5));
        var r = Booleans.Subtract(l, cutter);
        AssertSolid(r, 3 - 0.75);
    }

    [Fact]
    public void RotatedBoxesInManyPositions()
    {
        var rng = new Random(42);
        for (int trial = 0; trial < 20; trial++)
        {
            var a = Box(2);
            var b = Box(new Vec3(-0.8, -0.6, -0.7), new Vec3(0.9, 0.7, 0.8));
            var turn = Mat4.Rotation(new Vec3(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()), rng.NextDouble() * Math.PI);
            b.Transform(Mat4.Translation(new Vec3(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()) * 1.5) * turn);
            double va = MeshMeasure.Volume(a), vb = MeshMeasure.Volume(b);
            var u = Booleans.Union(a, b);
            var d = Booleans.Subtract(a, b);
            var i = Booleans.Intersect(a, b);
            foreach (var m in new[] { u, d, i })
                if (m.Polygons.Count > 0) Assert.True(MeshMeasure.IsWatertight(m), $"trial {trial}: {MeshMeasure.OpenEdges(m).Count} open edges");
            double vi = MeshMeasure.Volume(i);
            Assert.Equal(va + vb - vi, MeshMeasure.Volume(u), 1e-9);
            Assert.Equal(va - vi, MeshMeasure.Volume(d), 1e-9);
            Assert.InRange(vi, 0, vb + 1e-9);
        }
    }

    [Fact]
    public void ResultsChainIntoFurtherBooleans()
    {
        // A box with three tunnels along the three axes, each cut from the last result.
        var m = Box(2);
        m = Booleans.Subtract(m, Box(new Vec3(-0.5, -2, -0.5), new Vec3(0.5, 2, 0.5)));
        m = Booleans.Subtract(m, Box(new Vec3(-2, -0.5, -0.5), new Vec3(2, 0.5, 0.5)));
        m = Booleans.Subtract(m, Box(new Vec3(-0.5, -0.5, -2), new Vec3(0.5, 0.5, 2)));
        AssertSolid(m, 8 - 3 * 2 + 2 * 1);
        // Rejoining keeps the count modest: each holed face of the cube ends up in a few pieces, not dozens.
        Assert.True(m.Polygons.Count <= 60, $"{m.Polygons.Count} polygons");
    }
}
