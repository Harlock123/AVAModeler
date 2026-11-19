using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Geometry.Tests;

public class QuadRemeshTests
{
    private static double MeanEdge(Mesh m)
    {
        var lengths = m.EdgeMap().Keys.Select(e => Vec3.Distance(m.Points[e.A], m.Points[e.B])).ToList();
        return lengths.Average();
    }

    [Fact]
    public void A_ball_becomes_a_closed_skin_of_even_quads_at_the_length_asked()
    {
        var m = TestShapes.Sphere(Vec3.Zero, 1, 24, 12);
        var r = QuadRemesh.Apply(m, null, new QuadRemeshOptions { EdgeLength = 0.15 });
        Assert.All(m.Polygons, p => Assert.Equal(4, p.Count));
        TestShapes.AssertSolid(m);
        double v = MeshCheck.Mesh(m).Volume!.Value;
        Assert.InRange(v, 4.0 / 3 * Math.PI * 0.95, 4.0 / 3 * Math.PI * 1.01);
        Assert.InRange(MeanEdge(m), 0.15 * 0.7, 0.15 * 1.3);
        Assert.Equal(m.Polygons.Count, r.Quads);
        Assert.True(r.PairedShare > 70, $"paired {r.PairedShare}%");
    }

    [Fact]
    public void A_box_keeps_its_sharp_edges_and_its_volume()
    {
        var m = TestShapes.Box(2);
        QuadRemesh.Apply(m, null, new QuadRemeshOptions { EdgeLength = 0.25 });
        Assert.All(m.Polygons, p => Assert.Equal(4, p.Count));
        TestShapes.AssertSolid(m);
        Assert.Equal(8, MeshCheck.Mesh(m).Volume!.Value, 2);
        var b = m.Bounds();
        TestShapes.AssertClose(new Vec3(1, 1, 1), b.Max, 1e-9);
        // Every point lies on the box's surface.
        Assert.All(m.Points, p => Assert.True(Math.Abs(Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) - 1) < 1e-6, $"{p} is off the box"));
    }

    /// <summary>
    /// The share of a tilted cylinder's side edges that run up it or round it (within 20°), measured in its own
    /// axes: what following the shape means here.
    /// </summary>
    private static double Following(Mesh m, Mat4 untilt)
    {
        int straight = 0, total = 0;
        foreach (var e in m.EdgeMap().Keys)
        {
            var a = untilt.TransformPoint(m.Points[e.A]);
            var b = untilt.TransformPoint(m.Points[e.B]);
            if (a.Y < 0.1 || a.Y > 2.9 || b.Y < 0.1 || b.Y > 2.9) continue;
            var d = (b - a).Normalized();
            total++;
            if (Math.Abs(d.Y) > 0.94 || Math.Abs(d.Y) < 0.34) straight++;
        }
        return (double)straight / total;
    }

    [Fact]
    public void A_tilted_cylinders_quads_run_along_and_round_it_where_a_voxel_grid_cant()
    {
        var tilt = Mat4.RotationZ(35 * Math.PI / 180);
        var untilt = Mat4.RotationZ(-35 * Math.PI / 180);
        Mesh Tilted()
        {
            var c = TestShapes.Cylinder(sides: 32, segments: 6, radius: 1, height: 3);
            c.Transform(tilt);
            return c;
        }
        var m = Tilted();
        var r = QuadRemesh.Apply(m, null, new QuadRemeshOptions { EdgeLength = 0.2 });
        TestShapes.AssertSolid(m);
        Assert.All(m.Polygons, p => Assert.Equal(4, p.Count));
        double ours = Following(m, untilt);
        var voxel = Tilted();
        Remesh.Apply(voxel, Enumerable.Range(0, voxel.Polygons.Count).ToList(), 0.2, 2);
        double grid = Following(voxel, untilt);
        Assert.True(ours > 0.62, $"{ours:P0} of the side edges follow the cylinder");
        Assert.True(ours > grid + 0.1, $"quad remesh {ours:P0}, voxel remesh {grid:P0}");
        Assert.True(r.PairedShare > 75, $"paired {r.PairedShare}%");
    }

    [Fact]
    public void Remeshing_part_of_an_object_keeps_it_stitched_to_the_rest()
    {
        var m = TestShapes.Box(2);
        int top = Enumerable.Range(0, m.Polygons.Count).First(i => m.Normal(i).Y > 0.9);
        int before = m.Polygons.Count;
        QuadRemesh.Apply(m, [top], new QuadRemeshOptions { EdgeLength = 0.4 });
        Assert.True(m.Polygons.Count > before);
        var check = MeshCheck.Mesh(m);
        Assert.True(check.Watertight, "the other five faces take the top's new border points");
        Assert.Equal(8, check.Volume!.Value, 6);
    }

    [Fact]
    public void An_open_grid_keeps_its_edge_and_lies_flat()
    {
        var m = TestShapes.Grid(4, 2);
        QuadRemesh.Apply(m, null, new QuadRemeshOptions { EdgeLength = 0.1 });
        Assert.All(m.Polygons, p => Assert.Equal(4, p.Count));
        Assert.All(m.Points, p => Assert.Equal(0, p.Y, 9));
        var b = m.Bounds();
        TestShapes.AssertClose(new Vec3(1, 0, 1), b.Max, 1e-9);
        Assert.All(m.Polygons, p => Assert.True(m.Normal(p).Y > 0.99));
        Assert.Throws<ArgumentException>(() => QuadRemesh.Apply(TestShapes.Box(), null, new QuadRemeshOptions { EdgeLength = 0 }));
    }
}
