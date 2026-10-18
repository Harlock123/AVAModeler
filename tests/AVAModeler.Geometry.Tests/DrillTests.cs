using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Geometry.Tests.TestShapes;

namespace AVAModeler.Geometry.Tests;

public class DrillTests
{
    private const int Sides = 32;
    private static readonly double Circle = RegularPolygonArea(1, Sides);
    // A circle outline of radius 1, well inside a 4×4 grid, drilled along Y.
    private static Mesh Cutter => Disc(new Vec3(0.1, 5, 0.2), 1, Sides);

    [Fact]
    public void CoreKeepsTheInside()
    {
        var r = Drill.Apply(Grid(4, 4, surface: 1), Cutter, Axis.Y, DrillOperation.Core);
        Assert.Equal(Circle, MeshMeasure.Area(r), 1e-9);
        Assert.All(r.Polygons, p => Assert.True(r.Normal(p).Y > 0.999));
        Assert.All(r.Polygons, p => Assert.Equal(1, p.Surface));
        Assert.All(r.Points, p => Assert.Equal(0, p.Y));
    }

    [Fact]
    public void TunnelCutsAHole()
    {
        var grid = Grid(4, 4);
        var r = Drill.Apply(grid, Cutter, Axis.Y, DrillOperation.Tunnel);
        Assert.Equal(16 - Circle, MeshMeasure.Area(r), 1e-9);
        // Untouched quads come through as they were, sharing their original points.
        Assert.True(r.Polygons.Count(p => p.Count == 4) >= 4);
        // The cut keeps the surface stitched: only the grid's rim and the hole's rim are open.
        var open = MeshMeasure.OpenEdges(r);
        double rim = open.Sum(e => Vec3.Distance(r.Points[e.A], r.Points[e.B]));
        Assert.Equal(16 + Sides * 2 * Math.Sin(Math.PI / Sides), rim, 1e-9);
    }

    [Fact]
    public void StencilAndSliceKeepEverything()
    {
        var grid = Grid(4, 4, surface: 1);
        var st = Drill.Apply(grid, Cutter, Axis.Y, DrillOperation.Stencil, stencilSurface: 9);
        Assert.Equal(16, MeshMeasure.Area(st), 1e-9);
        Assert.Equal(Circle, MeshMeasure.Area(st, Enumerable.Range(0, st.Polygons.Count).Where(i => st.Polygons[i].Surface == 9)), 1e-9);

        var sl = Drill.Apply(grid, Cutter, Axis.Y, DrillOperation.Slice);
        Assert.Equal(16, MeshMeasure.Area(sl), 1e-9);
        Assert.True(sl.Polygons.Count > grid.Polygons.Count);
        Assert.All(sl.Polygons, p => Assert.Equal(1, p.Surface));
        // Sliced but still one sheet: the only open edges are the grid's rim.
        double rim = MeshMeasure.OpenEdges(sl).Sum(e => Vec3.Distance(sl.Points[e.A], sl.Points[e.B]));
        Assert.Equal(16, rim, 1e-9);
    }

    [Fact]
    public void BoxDrilledThroughWithACircle()
    {
        var box = Box(new Vec3(-2, -1, -2), new Vec3(2, 1, 2));
        double total = MeshMeasure.Area(box);

        var tunnel = Drill.Apply(box, Cutter, Axis.Y, DrillOperation.Tunnel);
        Assert.Equal(total - 2 * Circle, MeshMeasure.Area(tunnel), 1e-9);

        var core = Drill.Apply(box, Cutter, Axis.Y, DrillOperation.Core);
        Assert.Equal(2 * Circle, MeshMeasure.Area(core), 1e-9);
        // The two discs keep the box's directions: one faces up, one down.
        Assert.Single(core.Polygons, p => core.Normal(p).Y > 0.999);
        Assert.Single(core.Polygons, p => core.Normal(p).Y < -0.999);

        var slice = Drill.Apply(box, Cutter, Axis.Y, DrillOperation.Slice);
        Assert.True(MeshMeasure.IsWatertight(slice));
        Assert.Equal(32, MeshMeasure.Volume(slice), 1e-9);
    }

    [Fact]
    public void ACutterAcrossTheSideWallsCutsThemToo()
    {
        var box = Box(2);
        // A square outline seen along X crossing the box's top edge: it cuts the ±X walls and the top.
        var cutter = new Mesh();
        cutter.AddFace(0, new Vec3(0, 0.5, -0.5), new Vec3(0, 1.5, -0.5), new Vec3(0, 1.5, 0.5), new Vec3(0, 0.5, 0.5));
        var slice = Drill.Apply(box, cutter, Axis.X, DrillOperation.Slice);
        Assert.True(MeshMeasure.IsWatertight(slice));
        Assert.Equal(8, MeshMeasure.Volume(slice), 1e-9);
        var core = Drill.Apply(box, cutter, Axis.X, DrillOperation.Core);
        // Inside the prism: 0.5 × 1 on each X wall, and the top strip 2 × 1.
        Assert.Equal(0.5 + 0.5 + 2, MeshMeasure.Area(core), 1e-9);
    }

    [Fact]
    public void ClosedOperandOutlinesAddUpRatherThanCancel()
    {
        // A sphere seen from above: its upper and lower faces overlap with opposite windings.
        var sphere = Sphere(new Vec3(0, 3, 0), 1, 16, 8);
        var r = Drill.Apply(Grid(4, 4), sphere, Axis.Y, DrillOperation.Core);
        Assert.Equal(RegularPolygonArea(1, 16), MeshMeasure.Area(r), 1e-9);
    }

    [Fact]
    public void ConcaveOutlinesAndFaces()
    {
        // An L-shaped face (area 3) cut by the same L turned half round about (1, 1): they overlap in 2 squares.
        Vec3[] L(double s) => [new(0, 0, 0), new(0, 0, 2 * s), new(s, 0, 2 * s), new(s, 0, s), new(2 * s, 0, s), new(2 * s, 0, 0)];
        var face = new Mesh();
        face.AddFace(0, L(1));
        var cutter = new Mesh();
        cutter.AddFace(0, L(1).Select(p => new Vec3(2 - p.X, 4, 2 - p.Z)).ToArray());
        var core = Drill.Apply(face, cutter, Axis.Y, DrillOperation.Core);
        Assert.Equal(2, MeshMeasure.Area(core), 1e-9);
        var tunnel = Drill.Apply(face, cutter, Axis.Y, DrillOperation.Tunnel);
        Assert.Equal(1, MeshMeasure.Area(tunnel), 1e-9);
    }

    [Fact]
    public void DegenerateInputsPassThrough()
    {
        var m = Grid(1, 2);
        int a = m.AddPoint(new Vec3(0, 3, 0));
        int b = m.AddPoint(new Vec3(1, 3, 0));
        m.AddPolygon([a]);
        m.AddPolygon([a, b]);
        // A zero-area face (three points in a row).
        int c = m.AddPoint(new Vec3(2, 0, 0)), d = m.AddPoint(new Vec3(3, 0, 0)), e = m.AddPoint(new Vec3(4, 0, 0));
        m.AddPolygon([c, d, e]);
        var r = Drill.Apply(m, Cutter, Axis.Y, DrillOperation.Slice);
        Assert.Single(r.Polygons, p => p.Count == 1);
        Assert.Single(r.Polygons, p => p.Count == 2);
        Assert.Equal(4, MeshMeasure.Area(r), 1e-9);

        Assert.Empty(Drill.Apply(new Mesh(), Cutter, Axis.Y, DrillOperation.Core).Polygons);
        // With nothing to drill with, faces come through; the zero-area one is dropped as a degenerate.
        var same = Drill.Apply(m, new Mesh(), Axis.Z, DrillOperation.Tunnel);
        Assert.Single(same.Polygons, p => p.Count >= 3);
    }
}
