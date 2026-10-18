using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Geometry.Tests.TestShapes;

namespace AVAModeler.Geometry.Tests;

public class SubdivisionTests
{
    [Fact]
    public void CubeCountsAfterOneAndTwoLevels()
    {
        var one = Subdivision.CatmullClark(Box(2));
        Assert.Equal(26, one.Points.Count);
        Assert.Equal(24, one.Polygons.Count);
        Assert.All(one.Polygons, p => Assert.Equal(4, p.Count));
        var two = Subdivision.CatmullClark(Box(2), 2);
        Assert.Equal(98, two.Points.Count);
        Assert.Equal(96, two.Polygons.Count);
        Assert.True(MeshMeasure.IsWatertight(two));
    }

    [Fact]
    public void CubeShrinksTowardASphereAndStaysSymmetric()
    {
        var cube = Box(2);
        var m = Subdivision.CatmullClark(cube, 3);
        Assert.True(MeshMeasure.IsWatertight(m));
        Assert.True(FacesPointAwayFrom(m, Vec3.Zero));
        double v = MeshMeasure.Volume(m);
        Assert.InRange(v, 4.0 / 3 * Math.PI * 0.5 * 0.5 * 0.5, 8);
        // Every point lies inside the cube, and the corners have been pulled in.
        Assert.All(m.Points, p => Assert.True(Math.Abs(p.X) <= 1 && Math.Abs(p.Y) <= 1 && Math.Abs(p.Z) <= 1));
        Assert.True(m.Points[7].Length < Math.Sqrt(3) * 0.7);
        // The point set is unchanged by mirroring in each axis and by swapping axes.
        var set = m.Points.Select(Key).ToHashSet();
        Assert.All(m.Points, p => Assert.Contains(Key(p with { X = -p.X }), set));
        Assert.All(m.Points, p => Assert.Contains(Key(new Vec3(p.Y, p.Z, p.X)), set));
        // Known first-level positions: a corner goes to (5/9)(1,1,1), a face centre stays on the face.
        var one = Subdivision.CatmullClark(cube);
        AssertClose(new Vec3(5, 5, 5) / 9, one.Points[7]);
        Assert.Contains(one.Points, p => Vec3.Near(p, new Vec3(0, 1, 0)));
    }

    private static (long, long, long) Key(Vec3 p) => ((long)Math.Round(p.X * 1e7), (long)Math.Round(p.Y * 1e7), (long)Math.Round(p.Z * 1e7));

    [Fact]
    public void OpenGridUsesBoundaryRules()
    {
        var grid = Grid(2, 2);
        var m = Subdivision.CatmullClark(grid);
        Assert.Equal(25, m.Points.Count);
        Assert.Equal(16, m.Polygons.Count);
        Assert.All(m.Points, p => Assert.Equal(0, p.Y, 1e-12));
        Assert.All(m.Polygons, p => Assert.True(m.Normal(p).Y > 0.99));
        // The middle of an edge of the grid stays on that edge (the boundary curve rule).
        int mid = grid.Points.FindIndex(p => Vec3.Near(p, new Vec3(0, 0, -1)));
        AssertClose(new Vec3(0, 0, -1), m.Points[mid]);
        // A corner moves in along the curve: 3/4 of itself plus 1/8 of each neighbour.
        AssertClose(new Vec3(-0.875, 0, -0.875), m.Points[0]);
        // The open rim is still a single loop of edges: 16 edges, each used once.
        Assert.Equal(16, MeshMeasure.OpenEdges(m).Count);
    }

    [Fact]
    public void TrianglesNGonsLinesAndNonManifoldEdgesAreHandled()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        // A pentagon sharing an edge with the triangle, and a third face on the same edge (non-manifold).
        m.Points.AddRange([new Vec3(1, -1, 0), new Vec3(0.5, -1.5, 0), new Vec3(0, -1, 0)]);
        m.AddPolygon([0, 2, 3, 4, 5]);
        m.Points.Add(new Vec3(0.5, 0, 1));
        m.AddPolygon([2, 0, 6]);
        m.AddPolygon([1, 6]);
        m.AddPolygon([4]);
        var r = Subdivision.CatmullClark(m);
        Assert.Equal(3 + 5 + 3, r.Polygons.Count(p => p.Count == 4));
        Assert.Single(r.Polygons, p => p.Count == 2);
        Assert.Single(r.Polygons, p => p.Count == 1);
        // The non-manifold edge 0–2 is a crease, so its new point is its midpoint.
        Assert.Contains(r.Points, p => Vec3.Near(p, new Vec3(0.5, 0, 0)));
        Assert.All(r.Points, p => Assert.False(double.IsNaN(p.X)));
    }

    [Fact]
    public void MetaformOnASelectionLeavesTheRestStitched()
    {
        var box = Box(2);
        var top = Enumerable.Range(0, 6).Single(i => box.Normal(i).Y > 0.5);
        var before = box.Points.ToList();
        Subdivision.Metaform(box, [top]);
        // The top's four corners are shared with the sides, so they stay; the top becomes four quads.
        for (int i = 0; i < 8; i++) Assert.Equal(before[i], box.Points[i]);
        Assert.Equal(9, box.Polygons.Count);
        Assert.True(MeshMeasure.IsWatertight(box));
        Assert.Equal(8, MeshMeasure.Volume(box), 1e-12);
    }

    [Fact]
    public void MetaformOnEverythingMatchesCatmullClark()
    {
        var a = Box(2);
        Subdivision.Metaform(a);
        var b = Subdivision.CatmullClark(Box(2));
        Assert.Equal(b.Points, a.Points);
    }

    [Fact]
    public void FacetedSubdivideSplitsWithoutMoving()
    {
        var box = Box(2);
        Subdivision.Faceted(box);
        Assert.Equal(26, box.Points.Count);
        Assert.Equal(24, box.Polygons.Count);
        Assert.True(MeshMeasure.IsWatertight(box));
        Assert.Equal(8, MeshMeasure.Volume(box), 1e-12);

        var tri = new Mesh();
        tri.AddFace(3, new Vec3(0, 0, 0), new Vec3(0, 2, 0), new Vec3(2, 0, 0));
        double area = MeshMeasure.Area(tri);
        var n = tri.Normal(0);
        Subdivision.Faceted(tri);
        Assert.Equal(4, tri.Polygons.Count);
        Assert.Equal(6, tri.Points.Count);
        Assert.All(tri.Polygons, p => { Assert.Equal(3, p.Count); Assert.Equal(3, p.Surface); AssertClose(n, tri.Normal(p)); });
        Assert.Equal(area, MeshMeasure.Area(tri), 1e-12);

        var hex = Disc(Vec3.Zero, 1, 6);
        Subdivision.Faceted(hex);
        Assert.Equal(6, hex.Polygons.Count);
        Assert.Equal(6 + 6 + 1, hex.Points.Count);
    }

    [Fact]
    public void FacetedOnASelectionStitchesIntoNeighbours()
    {
        var grid = Grid(2, 2);
        Subdivision.Faceted(grid, [0]);
        Assert.Equal(3 + 4, grid.Polygons.Count);
        // The two neighbours of quad 0 have taken its edge midpoints.
        Assert.Equal(2, grid.Polygons.Take(4).Count(p => p.Count == 5));
        // Only the rim is open: its 8 edges, two of them now halved.
        Assert.Equal(10, MeshMeasure.OpenEdges(grid).Count);
        Assert.Equal(4, MeshMeasure.Area(grid), 1e-12);
    }

    [Fact]
    public void SmoothPullsAPointIn()
    {
        var grid = Grid(2, 2);
        int centre = grid.Points.FindIndex(p => Vec3.Near(p, Vec3.Zero));
        grid.Points[centre] = new Vec3(0, 1, 0);
        Subdivision.Smooth(grid, [centre], 0.5, 1);
        Assert.Equal(0.5, grid.Points[centre].Y, 1e-12);
        Subdivision.Smooth(grid, [centre], 0.5, 10);
        Assert.True(grid.Points[centre].Y < 0.001);
        // Smoothing a flat grid all over leaves it flat.
        var flat = Grid(3, 3);
        Subdivision.Smooth(flat, null, 0.5, 5);
        Assert.All(flat.Points, p => Assert.Equal(0, p.Y));
    }

    [Fact]
    public void TaubinShrinksLessThanLaplacian()
    {
        var a = Sphere(Vec3.Zero, 1, 16, 8);
        var b = a.Clone();
        double v = MeshMeasure.Volume(a);
        Subdivision.Smooth(a, null, 0.5, 5);
        Subdivision.Smooth(b, null, 0.5, 5, preserveVolume: true);
        double lap = v - MeshMeasure.Volume(a), taubin = Math.Abs(v - MeshMeasure.Volume(b));
        Assert.True(lap > 0.3, $"laplacian lost {lap}");
        Assert.True(taubin < lap / 4, $"taubin lost {taubin}, laplacian {lap}");
    }

    [Fact]
    public void EmptyAndDegenerateMeshes()
    {
        Assert.True(Subdivision.CatmullClark(new Mesh(), 2).IsEmpty);
        var lines = new Mesh();
        lines.Points.AddRange([Vec3.Zero, Vec3.UnitX]);
        lines.AddPolygon([0, 1]);
        var r = Subdivision.CatmullClark(lines);
        Assert.Equal(2, r.Points.Count);
        Subdivision.Faceted(lines);
        Assert.Single(lines.Polygons);
        Subdivision.Smooth(new Mesh(), null);
        // A face with all its points in a row still subdivides without NaNs.
        var flat = new Mesh();
        flat.AddFace(0, Vec3.Zero, Vec3.UnitX, Vec3.UnitX * 2);
        var f = Subdivision.CatmullClark(flat);
        Assert.All(f.Points, p => Assert.False(double.IsNaN(p.X)));
    }
}
