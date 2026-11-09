using AVAModeler.Core;

namespace AVAModeler.Slicing.Tests;

public class SlicerTests
{
    private static readonly PrinterProfile Printer = new();

    private static SlicedModel Slice(Document doc, SliceSettings? settings = null) =>
        Slicer.Slice(Placement.Place(doc, Printer), settings ?? new SliceSettings(), Printer);

    [Fact]
    public void A_20_mm_cube_is_a_square_in_every_layer()
    {
        var sliced = Slice(Shapes.Cube(0.02));
        // 0.24 mm, then 0.2 mm layers until the top is reached: 1 + ceil(19.76 / 0.2) = 100.
        Assert.Equal(100, sliced.Layers.Count);
        Assert.Equal(0.24, sliced.Layers[0].Top, 9);
        Assert.Equal(0.12, sliced.Layers[0].CutAt, 9);
        Assert.Equal(0.44, sliced.Layers[1].Top, 9);
        // The last layer is thinner, ending at the top of the cube.
        Assert.Equal(20, sliced.Layers[^1].Top, 9);
        Assert.Equal(0.16, sliced.Layers[^1].Height, 9);
        Assert.Equal(0, sliced.OpenChains);
        foreach (var layer in sliced.Layers)
        {
            var island = Assert.Single(layer.Islands);
            Assert.Empty(island.Holes);
            Assert.Equal(400, island.Area, 6);
            // Four corners left once the cut points along each side are cleaned away, anticlockwise.
            Assert.Equal(4, island.Outline.Points.Count);
            Assert.False(island.Outline.IsHole);
            Assert.All(island.Outline.Points, p => Assert.True(p.X >= 100 - 1e-9 && p.X <= 120 + 1e-9 && p.Y >= 100 - 1e-9 && p.Y <= 120 + 1e-9));
        }
    }

    [Fact]
    public void A_tube_is_an_outline_with_a_hole()
    {
        var sliced = Slice(Shapes.Tube(0.02, 0.01, 0.01));
        Assert.Equal(0, sliced.OpenChains);
        foreach (var layer in sliced.Layers)
        {
            var island = Assert.Single(layer.Islands);
            var hole = Assert.Single(island.Holes);
            Assert.True(hole.IsHole);
            Assert.Equal(-100, hole.Area, 6);
            Assert.Equal(300, island.Area, 6);
            Assert.True(Geometry2D.Contains(island.Outline.Points, hole.Points[0]));
        }
    }

    [Fact]
    public void Separate_solids_are_separate_islands_and_a_solid_inside_a_hole_is_its_own()
    {
        var doc = Shapes.Tube(0.04, 0.02, 0.01);
        Shapes.Box(doc.Layers[0].Mesh, new Vec3(-0.005, 0, -0.005), new Vec3(0.005, 0.01, 0.005));
        Shapes.Box(doc.Layers[0].Mesh, new Vec3(0.05, 0, 0), new Vec3(0.06, 0.005, 0.01));
        doc.Layers[0].Mesh.Touch();
        var sliced = Slice(doc);
        var low = sliced.Layers[0];
        Assert.Equal(3, low.Islands.Count);
        Assert.Equal(1600 - 400, low.Islands[0].Area, 6);
        Assert.Single(low.Islands[0].Holes);
        Assert.All(low.Islands.Skip(1), i => Assert.Empty(i.Holes));
        Assert.Equal([100.0, 100.0], low.Islands.Skip(1).Select(i => Math.Round(i.Area, 6)));
        // Above 5 mm the small block has ended.
        Assert.Equal(2, sliced.Layers.First(l => l.CutAt > 5).Islands.Count);
    }

    [Fact]
    public void Shells_that_overlap_are_sliced_as_one_solid()
    {
        var doc = Shapes.Cube(0.02);
        Shapes.Box(doc.Layers[0].Mesh, new Vec3(0, 0, 0), new Vec3(0.02, 0.01, 0.02));
        doc.Layers[0].Mesh.Touch();
        var low = Slice(doc).Layers[0];
        // A 20 mm square and another of 20 mm overlapping a quarter of it: 400 + 400 - 100.
        Assert.Equal(700, Assert.Single(low.Islands).Area, 6);
    }

    [Fact]
    public void A_sloped_side_shrinks_layer_by_layer()
    {
        // A square pyramid 20 mm across and 10 mm tall: each layer's square is (1 - z/10) of the base.
        var doc = Shapes.Doc(m =>
        {
            m.Points.AddRange([new(-0.01, 0, -0.01), new(0.01, 0, -0.01), new(0.01, 0, 0.01), new(-0.01, 0, 0.01), new(0, 0.01, 0)]);
            int[][] faces = [[0, 1, 2, 3], [0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]];
            foreach (var f in faces)
            {
                var poly = new Polygon(f);
                m.Polygons.Add(poly);
                m.Touch();
                if (Vec3.Dot(m.Normal(poly), m.Centroid(poly) - new Vec3(0, 0.003, 0)) < 0) poly.Points.Reverse();
            }
        });
        var sliced = Slice(doc);
        foreach (var layer in sliced.Layers)
        {
            double side = 20 * (1 - layer.CutAt / 10);
            Assert.Equal(side * side, Assert.Single(layer.Islands).Area, 6);
        }
    }

    [Fact]
    public void A_hole_in_the_mesh_leaves_open_chains_out()
    {
        var doc = Shapes.Cube(0.02);
        var mesh = doc.Layers[0].Mesh;
        // Take away the front (the first face, at -Z): the cut no longer closes.
        mesh.Polygons.RemoveAt(0);
        mesh.Touch();
        var sliced = Slice(doc);
        Assert.True(sliced.OpenChains > 0);
        Assert.All(sliced.Layers, l => Assert.Empty(l.Islands));
    }

    [Fact]
    public void Layer_heights_are_checked_against_the_nozzle()
    {
        var e = Assert.Throws<ArgumentException>(() => new SliceSettings { LayerHeight = 0.4 }.Checked(Printer));
        Assert.Contains("0.32 mm", e.Message);
        new SliceSettings { LayerHeight = 0.4 }.Checked(Printer with { NozzleDiameter = 0.6 });
        Assert.Empty(Slicer.Slice(Placement.Place(new Document(), Printer), new SliceSettings(), Printer).Layers);
    }

    [Fact]
    public void Clean_drops_repeated_and_straight_run_points()
    {
        var ring = new List<Vec2> { new(0, 0), new(1, 0), new(1, 0), new(2, 0), new(2, 2), new(1, 2.0000001), new(0, 2), new(0, 0) };
        var clean = Geometry2D.Clean(ring);
        Assert.Equal([new Vec2(0, 0), new Vec2(2, 0), new Vec2(2, 2), new Vec2(0, 2)], clean);
    }
}
