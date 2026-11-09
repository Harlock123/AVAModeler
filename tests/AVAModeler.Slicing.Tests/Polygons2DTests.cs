using AVAModeler.Core;

namespace AVAModeler.Slicing.Tests;

public class Polygons2DTests
{
    private static List<Vec2> Square(double x0, double y0, double size, bool clockwise = false)
    {
        var s = new List<Vec2> { new(x0, y0), new(x0 + size, y0), new(x0 + size, y0 + size), new(x0, y0 + size) };
        if (clockwise) s.Reverse();
        return s;
    }

    [Fact]
    public void Offsets_shrink_and_grow_a_square()
    {
        var region = Polygons2D.Paths([Square(0, 0, 20)]);
        Assert.Equal(400, Polygons2D.Area(region), 6);
        Assert.Equal(19 * 19, Polygons2D.Area(Polygons2D.Offset(region, -0.5)), 6);
        // Sharp corners stay square going out; round ones lose the corners' square bits (4 - π) r².
        Assert.Equal(22 * 22, Polygons2D.Area(Polygons2D.Offset(region, 1)), 6);
        Assert.Equal(400 + 4 * 20 + Math.PI, Polygons2D.Area(Polygons2D.Offset(region, 1, Polygons2D.Corners.Round)), 1);
        // Shrunk by more than half its width, it's gone.
        Assert.Empty(Polygons2D.Offset(region, -10.5));
    }

    [Fact]
    public void A_hole_grows_as_its_region_shrinks()
    {
        var region = Polygons2D.Paths([Square(0, 0, 20), Square(5, 5, 10, clockwise: true)]);
        Assert.Equal(300, Polygons2D.Area(region), 6);
        var inset = Polygons2D.Offset(region, -1);
        Assert.Equal(18 * 18 - 12 * 12, Polygons2D.Area(inset), 6);
        var island = Assert.Single(Polygons2D.Islands(inset));
        Assert.Equal(-144, Assert.Single(island.Holes).Area, 6);
    }

    [Fact]
    public void Union_merges_overlaps_and_difference_and_intersection_cut()
    {
        var a = Polygons2D.Paths([Square(0, 0, 10)]);
        var b = Polygons2D.Paths([Square(5, 5, 10)]);
        Assert.Equal(175, Polygons2D.Area(Polygons2D.Union(a, b)), 6);
        Assert.Equal(75, Polygons2D.Area(Polygons2D.Difference(a, b)), 6);
        Assert.Equal(25, Polygons2D.Area(Polygons2D.Intersect(a, b)), 6);
        // Overlapping squares in one set of paths come out as one island.
        Assert.Single(Polygons2D.Islands(Polygons2D.Paths([Square(0, 0, 10), Square(5, 5, 10)])));
    }

    [Fact]
    public void Lines_are_clipped_to_a_region_with_a_hole()
    {
        var region = Polygons2D.Paths([Square(0, 0, 20), Square(5, 5, 10, clockwise: true)]);
        var pieces = Polygons2D.ClipLines([[new Vec2(-5, 10), new Vec2(25, 10)], [new Vec2(-5, 2), new Vec2(25, 2)]], region);
        // The line through the hole is in two pieces; the one below it, one.
        Assert.Equal(3, pieces.Count);
        Assert.Equal(20 + 5 + 5, pieces.Sum(p => Geometry2D.Perimeter(p, closed: false)), 6);
        Assert.All(pieces.SelectMany(p => p), q => Assert.True(q.X >= -1e-9 && q.X <= 20 + 1e-9));
    }

    [Fact]
    public void Islands_put_a_solid_in_a_hole_on_its_own_and_drop_specks()
    {
        var region = Polygons2D.Paths([Square(0, 0, 30), Square(5, 5, 20, clockwise: true), Square(10, 10, 10), Square(50, 50, 0.0001)]);
        var islands = Polygons2D.Islands(region, minArea: 1e-6);
        Assert.Equal(2, islands.Count);
        Assert.Equal(900 - 400, islands[0].Area, 6);
        Assert.Equal(100, islands[1].Area, 6);
        Assert.All(islands, i => Assert.False(i.Outline.IsHole));
    }
}
