using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Geometry.Tests.TestShapes;

namespace AVAModeler.Geometry.Tests;

public class PolygonOpsTests
{
    [Fact]
    public void MergeAFlatGridIntoOneFace()
    {
        var grid = Grid(3, 3);
        Assert.Equal(8, PolygonOps.Merge(grid));
        var face = Assert.Single(grid.Polygons);
        Assert.Equal(12, face.Count);
        Assert.Equal(9, grid.Area(face), 1e-12);
        Assert.True(grid.Normal(face).Y > 0.999);
    }

    [Fact]
    public void MergeRespectsSurfacesAnglesAndSelection()
    {
        var grid = Grid(2, 2);
        grid.Polygons[3].Surface = 1;
        Assert.Equal(2, PolygonOps.Merge(grid));
        Assert.Equal(2, grid.Polygons.Count);

        // A box's faces meet at right angles, so nothing merges; a selection of two coplanar faces does.
        var box = Box(2);
        Assert.Equal(0, PolygonOps.Merge(box));
        var g = Grid(2, 2);
        Assert.Equal(1, PolygonOps.Merge(g, [0, 1]));
        Assert.Equal(3, g.Polygons.Count);
        Assert.Equal(4, MeshMeasure.Area(g), 1e-12);
    }

    [Fact]
    public void MergeAroundAHoleLeavesMoreThanOnePiece()
    {
        var grid = Grid(3, 3);
        int middle = Enumerable.Range(0, 9).Single(i => Vec3.Near(grid.Centroid(grid.Polygons[i]), Vec3.Zero));
        grid.RemovePolygons([middle]);
        PolygonOps.Merge(grid);
        Assert.InRange(grid.Polygons.Count, 2, 4);
        Assert.Equal(8, MeshMeasure.Area(grid), 1e-12);
        Assert.All(grid.Polygons, p => Assert.Equal(p.Count, p.Points.Distinct().Count()));
    }

    [Fact]
    public void SplitAFace()
    {
        var hex = Disc(Vec3.Zero, 1, 6, surface: 3);
        double area = MeshMeasure.Area(hex);
        int added = PolygonOps.Split(hex, 0, 0, 3);
        Assert.Equal(1, added);
        Assert.Equal(4, hex.Polygons[0].Count);
        Assert.Equal(4, hex.Polygons[1].Count);
        Assert.Equal(3, hex.Polygons[1].Surface);
        Assert.Equal(area, MeshMeasure.Area(hex), 1e-12);
        AssertClose(hex.Normal(0), hex.Normal(1));
        // Neighbouring points, points not in the face, and triangles can't be split.
        Assert.Equal(-1, PolygonOps.Split(hex, 0, 0, 1));
        Assert.Equal(-1, PolygonOps.Split(hex, 0, 0, 4));
        var tri = new Mesh();
        tri.AddFace(0, Vec3.Zero, Vec3.UnitX, Vec3.UnitY);
        Assert.Equal(-1, PolygonOps.Split(tri, 0, 0, 2));
    }

    [Fact]
    public void SplitAtPointsSplitsEveryFaceHoldingTwo()
    {
        var grid = Grid(2, 2);
        // Opposite corners of the first quad; the centre point is shared by all four quads, but only quad 0 holds both.
        int n = PolygonOps.SplitAtPoints(grid, [grid.Polygons[0].Points[0], grid.Polygons[0].Points[2]]);
        Assert.Equal(1, n);
        Assert.Equal(5, grid.Polygons.Count);
        Assert.Equal(4, MeshMeasure.Area(grid), 1e-12);
        // Two neighbouring corners of a quad lie on an edge, so nothing splits.
        Assert.Equal(0, PolygonOps.SplitAtPoints(grid, [grid.Polygons[1].Points[0], grid.Polygons[1].Points[1]]));
    }

    [Fact]
    public void UnifyRemovesDuplicatesWhateverTheirWinding()
    {
        var box = Box(2);
        box.AddPolygon(box.Polygons[0].Points.AsEnumerable().Reverse());
        box.AddPolygon(box.Polygons[1].Points.Skip(1).Append(box.Polygons[1].Points[0]));
        box.AddPolygon([0, 1]);
        box.AddPolygon([1, 0]);
        Assert.Equal(3, PolygonOps.Unify(box));
        Assert.Equal(7, box.Polygons.Count);
        Assert.Equal(0, PolygonOps.Unify(box));
    }

    [Fact]
    public void AlignTurnsAClosedPieceOutward()
    {
        var box = Box(2);
        box.Flip(0);
        box.Flip(3);
        Assert.Equal(2, PolygonOps.Align(box));
        Assert.True(MeshMeasure.IsWatertight(box));
        Assert.Equal(8, MeshMeasure.Volume(box), 1e-12);

        var inside = Box(2);
        PolygonOps.Flip(inside);
        Assert.Equal(6, PolygonOps.Align(inside));
        Assert.True(FacesPointAwayFrom(inside, Vec3.Zero));

        var sphere = Sphere(Vec3.Zero, 1, 12, 6);
        var rng = new Random(7);
        foreach (int i in Enumerable.Range(0, sphere.Polygons.Count).Where(_ => rng.Next(3) == 0).ToList()) sphere.Flip(i);
        PolygonOps.Align(sphere);
        Assert.True(MeshMeasure.IsWatertight(sphere));
        Assert.True(FacesPointAwayFrom(sphere, Vec3.Zero));
    }

    [Fact]
    public void AlignFollowsTheMajorityOnAnOpenPiece()
    {
        var grid = Grid(3, 3);
        grid.Flip(4);
        Assert.Equal(1, PolygonOps.Align(grid));
        Assert.All(grid.Polygons, p => Assert.True(grid.Normal(p).Y > 0.999));

        // Two separate pieces are aligned separately.
        var two = Grid(2, 2);
        two.Append(Box(new Vec3(5, 5, 5), new Vec3(6, 6, 6)));
        PolygonOps.Flip(two, [4, 5, 6, 7, 8, 9]);
        PolygonOps.Align(two);
        Assert.Equal(1, MeshMeasure.Volume(two, Enumerable.Range(4, 6)), 1e-12);
        Assert.All(two.Polygons.Take(4), p => Assert.True(two.Normal(p).Y > 0.999));
    }

    [Fact]
    public void FlipReversesFacesOnly()
    {
        var m = Box(2);
        m.Points.Add(new Vec3(5, 5, 5));
        m.AddPolygon([0, 8]);
        PolygonOps.Flip(m);
        Assert.Equal(-8, MeshMeasure.Volume(m), 1e-12);
        Assert.Equal([0, 8], m.Polygons[^1].Points);
        PolygonOps.Flip(m, [0]);
        Assert.True(Vec3.Dot(m.Normal(0), Vec3.UnitX) < 0);
    }

    [Fact]
    public void EmptyMeshes()
    {
        var m = new Mesh();
        Assert.Equal(0, PolygonOps.Merge(m));
        Assert.Equal(0, PolygonOps.Unify(m));
        Assert.Equal(0, PolygonOps.Align(m));
        Assert.Equal(0, PolygonOps.SplitAtPoints(m, [0, 1]));
        PolygonOps.Flip(m);
        Assert.True(m.IsEmpty);
    }
}
