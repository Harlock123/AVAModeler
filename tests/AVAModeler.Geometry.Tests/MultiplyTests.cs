using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Geometry.Tests.TestShapes;

namespace AVAModeler.Geometry.Tests;

public class MultiplyTests
{
    private static int Facing(Mesh m, Vec3 dir) => Enumerable.Range(0, m.Polygons.Count).Single(i => Vec3.Dot(m.Normal(i), dir) > 0.999);

    // ---- Extrude ----

    [Fact]
    public void ExtrudeATopFaceUp()
    {
        var box = Box(2);
        int top = Facing(box, Vec3.UnitY);
        Multiply.Extrude(box, [top], new Vec3(0, 1, 0));
        Assert.Equal(10, box.Polygons.Count);
        Assert.Equal(12, box.Points.Count);
        Assert.True(MeshMeasure.IsWatertight(box));
        Assert.Equal(12, MeshMeasure.Volume(box), 1e-12);
        Assert.True(box.Normal(top).Y > 0.999);
        Assert.All(box.Polygons[top].Points, p => Assert.Equal(2, box.Points[p].Y));
        Assert.True(FacesPointAwayFrom(box, new Vec3(0, 0.5, 0)));
    }

    [Fact]
    public void ExtrudeInSegmentsAndInward()
    {
        var box = Box(2);
        int top = Facing(box, Vec3.UnitY);
        Multiply.Extrude(box, [top], new Vec3(0, 1.5, 0), 3);
        Assert.Equal(6 + 12, box.Polygons.Count);
        Assert.Equal(8 + 12, box.Points.Count);
        Assert.True(MeshMeasure.IsWatertight(box));
        Assert.Equal(14, MeshMeasure.Volume(box), 1e-12);

        var dent = Box(2);
        Multiply.Extrude(dent, [Facing(dent, Vec3.UnitY)], new Vec3(0, -0.5, 0));
        Assert.True(MeshMeasure.IsWatertight(dent));
        Assert.Equal(6, MeshMeasure.Volume(dent), 1e-12);
    }

    [Fact]
    public void ExtrudeARegionMakesWallsOnlyOnItsBorder()
    {
        var grid = Grid(2, 2);
        Multiply.Extrude(grid, [0, 1, 2, 3], new Vec3(0, 1, 0));
        // 8 border edges make 8 walls; the centre point is used by nothing else, so it moves rather than copies.
        Assert.Equal(4 + 8, grid.Polygons.Count);
        Assert.Equal(9 + 8, grid.Points.Count);
        Assert.Equal(1, grid.Points[4].Y);
        Assert.All(grid.Polygons.Skip(4), w => Assert.True(Math.Abs(grid.Normal(w).Y) < 1e-12));
        // The walls face out from the middle.
        Assert.All(grid.Polygons.Skip(4), w => Assert.True(Vec3.Dot(grid.Normal(w), grid.Centroid(w) with { Y = 0 }) > 0));
    }

    [Fact]
    public void ExtrudeAFlatOutlineWithABaseCap()
    {
        foreach (double dir in new[] { 1.0, -1.0 })
        {
            var disc = Disc(Vec3.Zero, 1, 12);
            Multiply.Extrude(disc, [0], new Vec3(0, 2 * dir, 0), capBase: true);
            Assert.True(MeshMeasure.IsWatertight(disc));
            Assert.Equal(2 * RegularPolygonArea(1, 12), MeshMeasure.Volume(disc), 1e-12);
            Assert.True(FacesPointAwayFrom(disc, new Vec3(0, dir, 0)));
        }
    }

    [Fact]
    public void ExtrudeLinesIntoFacesAndPointsIntoLines()
    {
        var m = new Mesh();
        m.Points.AddRange([new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(5, 5, 5)]);
        m.AddPolygon([0, 1], 2);
        m.AddPolygon([1, 2], 2);
        m.AddPolygon([3], 3);
        Multiply.Extrude(m, [0, 1, 2], new Vec3(0, 0, 1), 2);
        Assert.Equal(4, m.Polygons.Count(p => p.Count == 4));
        Assert.Equal(2, m.Polygons.Count(p => p.Count == 2));
        Assert.Equal(2, MeshMeasure.Area(m), 1e-12);
        Assert.Equal(4 * 3, m.Points.Count);
        Assert.All(m.Polygons.Where(p => p.Count == 4), p => Assert.Equal(2, p.Surface));
        Assert.All(m.Polygons.Where(p => p.Count == 2), p => Assert.Equal(3, p.Surface));
        // The strips share the middle line's points: one connected sheet.
        Assert.Equal(8, MeshMeasure.OpenEdges(m).Count);
    }

    // ---- Lathe ----

    private static Mesh Profile(int points)
    {
        // A half circle from the bottom pole to the top pole, in the XY plane, as a chain of lines.
        var m = new Mesh();
        for (int i = 0; i < points; i++)
        {
            double a = -Math.PI / 2 + Math.PI * i / (points - 1);
            m.Points.Add(new Vec3(Math.Cos(a), Math.Sin(a), 0));
        }
        m.Points[0] = new Vec3(0, -1, 0);
        m.Points[^1] = new Vec3(0, 1, 0);
        for (int i = 0; i + 1 < points; i++) m.AddPolygon([i, i + 1]);
        return m;
    }

    [Fact]
    public void LatheAHalfCircleIntoASphere()
    {
        var m = Profile(9);
        Multiply.Lathe(m, Enumerable.Range(0, 8), Axis.Y, Vec3.Zero, 0, 360, 24);
        Assert.Equal(2 + 7 * 24, m.Points.Count);
        Assert.Equal(24 * 8, m.Polygons.Count);
        Assert.Equal(2 * 24, m.Polygons.Count(p => p.Count == 3));
        Assert.True(MeshMeasure.IsWatertight(m));
        Assert.True(FacesPointAwayFrom(m, Vec3.Zero));
        // Eight profile segments make a coarse globe, a little smaller than the sphere.
        Assert.Equal(4 * Math.PI / 3, MeshMeasure.Volume(m), 0.25);
    }

    [Fact]
    public void LatheTheOtherWayStillFacesOut()
    {
        var m = Profile(5);
        for (int i = 0; i < 4; i++) m.Polygons[i].Points.Reverse();
        Multiply.Lathe(m, Enumerable.Range(0, 4), Axis.Y, Vec3.Zero, 0, -360, 12);
        Assert.True(MeshMeasure.IsWatertight(m));
        Assert.True(MeshMeasure.Volume(m) > 0);
    }

    [Fact]
    public void LatheAnOpenProfileFacesAwayFromTheAxis()
    {
        // A straight line parallel to the axis lathes into an open tube.
        var m = new Mesh();
        m.Points.AddRange([new Vec3(1, 0, 0), new Vec3(1, 2, 0)]);
        m.AddPolygon([0, 1]);
        Multiply.Lathe(m, [0], Axis.Y, Vec3.Zero, 0, 360, 16);
        Assert.Equal(16, m.Polygons.Count);
        Assert.Equal(32, m.Points.Count);
        Assert.All(m.Polygons, p => Assert.True(Vec3.Dot(m.Normal(p), m.Centroid(p) with { Y = 0 }) > 0));
    }

    [Fact]
    public void LatheAFaceIntoATorusAndAPartTurnIntoACappedSolid()
    {
        // A unit square from x = 1 to 2 in the XY plane; a ring of N wedges holds N·sin(2π/N)·A·x̄.
        static Mesh Square()
        {
            var m = new Mesh();
            m.AddFace(4, new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(2, 1, 0), new Vec3(2, 0, 0));
            return m;
        }
        var torus = Square();
        Multiply.Lathe(torus, [0], Axis.Y, Vec3.Zero, 0, 360, 24);
        Assert.True(MeshMeasure.IsWatertight(torus));
        Assert.Equal(24 * 4, torus.Polygons.Count);
        Assert.Equal(24 * 4, torus.Points.Count);
        Assert.Equal(24 * Math.Sin(2 * Math.PI / 24) * 1.5, MeshMeasure.Volume(torus), 1e-9);
        Assert.All(torus.Polygons, p => Assert.Equal(4, p.Surface));

        var wedge = Square();
        Multiply.Lathe(wedge, [0], Axis.Y, Vec3.Zero, 30, 120, 6);
        Assert.True(MeshMeasure.IsWatertight(wedge));
        Assert.Equal(4 * 6 + 2, wedge.Polygons.Count);
        Assert.Equal(6 * Math.Sin(Mat4.Deg(15)) * 1.5, MeshMeasure.Volume(wedge), 1e-9);
    }

    [Fact]
    public void LatheASpiral()
    {
        var m = new Mesh();
        m.Points.Add(new Vec3(1, 0, 0));
        m.AddPolygon([0]);
        Multiply.Lathe(m, [0], Axis.Y, Vec3.Zero, 0, 720, 16, offsetPerRevolution: 2);
        Assert.Equal(16, m.Polygons.Count);
        Assert.All(m.Polygons, p => Assert.Equal(2, p.Count));
        Assert.Equal(17, m.Points.Count);
        Assert.Contains(m.Points, p => Vec3.Near(p, new Vec3(1, 4, 0), 1e-9));
        Assert.Contains(m.Points, p => Vec3.Near(p, new Vec3(-1, 1, 0), 1e-9));
    }

    // ---- Path and rail extrude ----

    private static Mesh UnitSquareFacingUp()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(-0.5, 0, -0.5), new Vec3(-0.5, 0, 0.5), new Vec3(0.5, 0, 0.5), new Vec3(0.5, 0, -0.5));
        Assert.True(m.Normal(0).Y > 0.999);
        return m;
    }

    [Fact]
    public void PathExtrudeAlongAStraightPath()
    {
        var m = UnitSquareFacingUp();
        Multiply.PathExtrude(m, [0], [Vec3.Zero, new Vec3(0, 1, 0), new Vec3(0, 3, 0)], capBase: true);
        Assert.True(MeshMeasure.IsWatertight(m));
        Assert.Equal(3, MeshMeasure.Volume(m), 1e-12);
        Assert.Equal(1 + 4 * 2 + 1, m.Polygons.Count);
    }

    [Fact]
    public void PathExtrudeTurnsTheProfileToFollowTheCurve()
    {
        var m = UnitSquareFacingUp();
        // A quarter circle from the origin heading up (+Y), ending at (2, 2) heading along +X.
        var path = Enumerable.Range(0, 17).Select(i =>
        {
            double a = Math.PI / 2 * i / 16;
            return new Vec3(2 - 2 * Math.Cos(a), 2 * Math.Sin(a), 0);
        }).ToList();
        Multiply.PathExtrude(m, [0], path);
        AssertClose(new Vec3(2, 2, 0), m.Centroid(m.Polygons[0]), 1e-9);
        // A polyline's end directions are its end chords, each half a step (2.8125°) short of the arc's tangent, so
        // the profile turns 90° less one step.
        AssertClose(new Vec3(Math.Sin(Mat4.Deg(84.375)), Math.Cos(Mat4.Deg(84.375)), 0), m.Normal(0), 1e-9);
        // The profile keeps its size along the way.
        Assert.Equal(1, m.Area(m.Polygons[0]), 1e-9);
        Assert.Equal(1 + 4 * 16, m.Polygons.Count);

        var moved = UnitSquareFacingUp();
        Multiply.PathExtrude(moved, [0], path, orient: false);
        AssertClose(Vec3.UnitY, moved.Normal(0), 1e-9);
    }

    [Fact]
    public void RailExtrudeResamplesTheRailEvenly()
    {
        var rail = new List<Vec3> { Vec3.Zero, new(0, 0.5, 0), new(0, 3, 0), new(0, 4, 0) };
        var even = Multiply.Resample(rail, 4);
        Assert.Equal(5, even.Count);
        for (int i = 0; i < 5; i++) AssertClose(new Vec3(0, i, 0), even[i], 1e-12);

        var m = new Mesh();
        m.Points.AddRange([Vec3.Zero, Vec3.UnitX]);
        m.AddPolygon([0, 1]);
        Multiply.RailExtrude(m, [0], rail, 4);
        Assert.Equal(4, m.Polygons.Count);
        Assert.Equal(4, MeshMeasure.Area(m), 1e-12);
        Assert.Equal(10, m.Points.Count);
        Assert.All(Enumerable.Range(0, 5), i => Assert.Contains(m.Points, p => Vec3.Near(p, new Vec3(1, i, 0))));
    }

    // ---- Bevel and smooth shift ----

    [Fact]
    public void BevelATopFace()
    {
        var box = Box(2);
        int top = Facing(box, Vec3.UnitY);
        Multiply.Bevel(box, [top], 0.25, 0.5);
        Assert.Equal(10, box.Polygons.Count);
        Assert.True(MeshMeasure.IsWatertight(box));
        Assert.Equal(2.25, box.Area(box.Polygons[top]), 1e-12);
        Assert.Equal(8 + 0.5 / 3 * (4 + 2.25 + 3), MeshMeasure.Volume(box), 1e-12);
        Assert.True(box.Normal(top).Y > 0.999);
    }

    [Fact]
    public void BevelAConcaveFaceInsetsEvenly()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 0, 2), new Vec3(1, 0, 2), new Vec3(1, 0, 1), new Vec3(2, 0, 1), new Vec3(2, 0, 0));
        Multiply.Bevel(m, [0], 0.1, 0);
        // Offsetting an L of area 3 and perimeter 8 inward by d leaves 3 - 8d + 4d² (five convex corners, one reflex).
        Assert.Equal(3 - 0.8 + 0.04, m.Area(m.Polygons[0]), 1e-12);
        Assert.Equal(3, MeshMeasure.Area(m), 1e-12);
        Assert.Equal(7, m.Polygons.Count);
    }

    [Fact]
    public void SmoothShiftOneFace()
    {
        var box = Box(2);
        int top = Facing(box, Vec3.UnitY);
        Multiply.SmoothShift(box, [top], 1);
        Assert.True(MeshMeasure.IsWatertight(box));
        Assert.Equal(12, MeshMeasure.Volume(box), 1e-12);
        Assert.Equal(10, box.Polygons.Count);
    }

    [Fact]
    public void SmoothShiftAWholeCubeWithSharpEdges()
    {
        var box = Box(2);
        Multiply.SmoothShift(box, Enumerable.Range(0, 6), 0.5, 89);
        // The faces part at every edge: 12 bevel strips and 8 corner triangles, like a cube grown by an octahedron.
        Assert.True(MeshMeasure.IsWatertight(box));
        Assert.Equal(6 + 12 + 8, box.Polygons.Count);
        Assert.Equal(24, box.Points.Count);
        Assert.Equal(8 + 6 * 4 * 0.5 + 12 * 2 * 0.125 + 8 * 0.125 / 6, MeshMeasure.Volume(box), 1e-12);
        Assert.True(FacesPointAwayFrom(box, Vec3.Zero));
    }

    [Fact]
    public void SmoothShiftAWholeCubeSmoothlyJustGrowsIt()
    {
        var box = Box(2);
        Multiply.SmoothShift(box, Enumerable.Range(0, 6), 0.5, 180);
        Assert.Equal(6, box.Polygons.Count);
        Assert.Equal(8, box.Points.Count);
        double side = 2 + 2 * 0.5 / Math.Sqrt(3);
        Assert.Equal(side * side * side, MeshMeasure.Volume(box), 1e-12);
    }

    [Fact]
    public void SmoothShiftTwoFacesOfACornerPartsThemAlongTheSharpEdge()
    {
        var box = Box(2);
        int top = Facing(box, Vec3.UnitY), side = Facing(box, Vec3.UnitX);
        Multiply.SmoothShift(box, [top, side], 0.5, 45);
        Assert.True(MeshMeasure.IsWatertight(box));
        // Each face grows a slab of 2 × 2 × 0.5, joined by a wedge of half 0.5 × 0.5 × 2.
        Assert.Equal(8 + 2 + 2 + 0.25, MeshMeasure.Volume(box), 1e-12);
    }

    // ---- Mirror, clone and arrays ----

    [Fact]
    public void MirrorAHalfSphereClosesTheSeam()
    {
        var sphere = Sphere(Vec3.Zero, 1, 24, 12);
        var half = sphere.Clone();
        half.RemovePolygons(Enumerable.Range(0, half.Polygons.Count).Where(i => half.Centroid(half.Polygons[i]).X < 0));
        half.RemoveUnusedPoints();
        var added = Multiply.Mirror(half, Enumerable.Range(0, half.Polygons.Count), Axis.X, 0, 1e-9);
        Assert.Equal(sphere.Polygons.Count, half.Polygons.Count);
        Assert.Equal(sphere.Points.Count, half.Points.Count);
        Assert.True(MeshMeasure.IsWatertight(half));
        Assert.Equal(MeshMeasure.Volume(sphere), MeshMeasure.Volume(half), 1e-9);
        Assert.All(added, i => Assert.True(half.Centroid(half.Polygons[i]).X < 0));
    }

    [Fact]
    public void MirrorAboutAnOffCentrePlaneSkipsFacesInThePlane()
    {
        var box = Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1));
        int inPlane = Facing(box, Vec3.UnitX);
        var added = Multiply.Mirror(box, Enumerable.Range(0, 6), Axis.X, 1);
        // The +X face lies in the plane and isn't copied; the copy shares its four points.
        Assert.Equal(5, added.Count);
        Assert.Equal(12, box.Points.Count);
        Assert.Contains(box.Points, p => p == new Vec3(2, 1, 1));
        var copy = new Mesh { Points = box.Points, Polygons = added.Select(i => box.Polygons[i]).ToList() };
        copy.Polygons.Add(box.Polygons[inPlane].Clone());
        copy.Polygons[^1].Points.Reverse();
        Assert.Equal(1, MeshMeasure.Volume(copy), 1e-12);
    }

    [Fact]
    public void CloneStepsAccumulate()
    {
        var box = Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1), surface: 5);
        var added = Multiply.Clone(box, Enumerable.Range(0, 6), 3, new Vec3(2, 0, 0), Vec3.Zero, Vec3.One, Vec3.Zero);
        Assert.Equal(18, added.Count);
        Assert.Equal(32, box.Points.Count);
        Assert.Equal(4, MeshMeasure.Volume(box), 1e-12);
        Assert.Contains(box.Points, p => p == new Vec3(7, 1, 1));
        Assert.All(box.Polygons, p => Assert.Equal(5, p.Surface));

        var turned = Box(new Vec3(1, 0, 0), new Vec3(2, 1, 1));
        Multiply.Clone(turned, Enumerable.Range(0, 6), 1, Vec3.Zero, new Vec3(90, 0, 0), new Vec3(2, 2, 2), Vec3.Zero);
        Assert.Equal(1 + 8, MeshMeasure.Volume(turned), 1e-9);

        var mirrored = Box(new Vec3(1, 0, 0), new Vec3(2, 1, 1));
        Multiply.Clone(mirrored, Enumerable.Range(0, 6), 1, Vec3.Zero, Vec3.Zero, new Vec3(-1, 1, 1), Vec3.Zero);
        Assert.Equal(2, MeshMeasure.Volume(mirrored), 1e-12);
    }

    [Fact]
    public void RectangularAndRadialArrays()
    {
        var box = Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1));
        var added = Multiply.Array(box, Enumerable.Range(0, 6), 2, 3, 1, new Vec3(2, 2, 2));
        Assert.Equal(5 * 6, added.Count);
        Assert.Equal(6, MeshMeasure.Volume(box), 1e-12);
        Assert.Contains(box.Points, p => p == new Vec3(3, 5, 1));

        var ring = Box(new Vec3(2, 0, 0), new Vec3(3, 1, 1));
        Multiply.RadialArray(ring, Enumerable.Range(0, 6), 4, Axis.Y, Vec3.Zero);
        Assert.Equal(24, ring.Polygons.Count);
        Assert.Equal(4, MeshMeasure.Volume(ring), 1e-12);
        Assert.Contains(ring.Points, p => Vec3.Near(p, new Vec3(-3, 0, 0), 1e-12));
    }

    [Fact]
    public void EmptySelectionsAndDegenerateInputsDoNothingHarmful()
    {
        var m = new Mesh();
        Multiply.Extrude(m, [], Vec3.UnitY);
        Multiply.Lathe(m, [], Axis.Y, Vec3.Zero, 0, 360, 8);
        Multiply.Bevel(m, [], 0.1, 0.1);
        Multiply.SmoothShift(m, [], 1);
        Assert.Empty(Multiply.Mirror(m, [], Axis.X));
        Assert.Empty(Multiply.Clone(m, [], 3, Vec3.One, Vec3.Zero, Vec3.One, Vec3.Zero));
        Assert.True(m.IsEmpty);

        // A zero-area face bevels and smooth-shifts without NaNs; a point lathed on the axis vanishes.
        var flat = new Mesh();
        flat.AddFace(0, Vec3.Zero, Vec3.UnitX, Vec3.UnitX * 2);
        Multiply.Bevel(flat, [0], 0.1, 0.1);
        Multiply.SmoothShift(flat, [0], 0.1);
        Assert.All(flat.Points, p => Assert.False(double.IsNaN(p.X)));
        var onAxis = new Mesh();
        onAxis.Points.Add(new Vec3(0, 1, 0));
        onAxis.AddPolygon([0]);
        Multiply.Lathe(onAxis, [0], Axis.Y, Vec3.Zero, 0, 360, 8);
        Assert.Empty(onAxis.Polygons);
    }
}
