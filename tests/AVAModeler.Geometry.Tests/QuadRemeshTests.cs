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
    /// The share of a tilted cylinder's side edges that run up it or round it (within <paramref name="degrees"/>),
    /// measured in its own axes: what following the shape means here.
    /// </summary>
    private static double Following(Mesh m, Mat4 untilt, double degrees = 20)
    {
        double up = Math.Cos(degrees * Math.PI / 180), round = Math.Sin(degrees * Math.PI / 180);
        int straight = 0, total = 0;
        foreach (var e in m.EdgeMap().Keys)
        {
            var a = untilt.TransformPoint(m.Points[e.A]);
            var b = untilt.TransformPoint(m.Points[e.B]);
            if (a.Y < 0.1 || a.Y > 2.9 || b.Y < 0.1 || b.Y > 2.9) continue;
            var d = (b - a).Normalized();
            total++;
            if (Math.Abs(d.Y) > up || Math.Abs(d.Y) < round) straight++;
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
        Assert.True(ours > 0.93, $"{ours:P0} of the side edges follow the cylinder");
        double strict = Following(m, untilt, 10);
        Assert.True(strict > 0.85, $"{strict:P0} of the side edges follow the cylinder within 10°");
        Assert.True(ours > grid + 0.3, $"quad remesh {ours:P0}, voxel remesh {grid:P0}");
        Assert.True(r.PairedShare > 85, $"paired {r.PairedShare}%");
        Assert.InRange(MeanEdge(m), 0.2 * 0.85, 0.2 * 1.15);
        // Few points where other than four quads meet (a cylinder's side needs none).
        var valence = new int[m.Points.Count];
        foreach (var e in m.EdgeMap().Keys) { valence[e.A]++; valence[e.B]++; }
        int irregular = Enumerable.Range(0, m.Points.Count).Count(v => valence[v] != 4 && untilt.TransformPoint(m.Points[v]).Y is > 0.1 and < 2.9);
        Assert.True(irregular < 40, $"{irregular} irregular points on the side");
    }

    [Fact]
    public void A_torus_with_no_creases_gets_quads_along_its_tube_and_round_its_ring()
    {
        const double R = 1, r = 0.35;
        var m = new Mesh();
        const int n = 48, k = 16;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < k; j++)
            {
                double u = 2 * Math.PI * i / n, v = 2 * Math.PI * j / k;
                m.Points.Add(new Vec3((R + r * Math.Cos(v)) * Math.Cos(u), r * Math.Sin(v), (R + r * Math.Cos(v)) * Math.Sin(u)));
            }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < k; j++)
                m.Polygons.Add(new Polygon([i * k + j, (i + 1) % n * k + j, (i + 1) % n * k + (j + 1) % k, i * k + (j + 1) % k]));
        m.Touch();
        if (MeshCheck.Mesh(m).Volume < 0) foreach (var p in m.Polygons) p.Points.Reverse();
        m.Touch();
        double before = MeshCheck.Mesh(m).Volume!.Value;
        QuadRemesh.Apply(m, null, new QuadRemeshOptions { EdgeLength = 0.12 });
        TestShapes.AssertSolid(m);
        Assert.All(m.Polygons, p => Assert.Equal(4, p.Count));
        Assert.InRange(MeshCheck.Mesh(m).Volume!.Value, before * 0.95, before * 1.01);
        // Edges run round the ring or round the tube (within 20°).
        int along = 0, total = 0;
        foreach (var e in m.EdgeMap().Keys)
        {
            var a = m.Points[e.A];
            var b = m.Points[e.B];
            var mid = (a + b) / 2;
            double c = Math.Abs(Vec3.Dot((b - a).Normalized(), new Vec3(-mid.Z, 0, mid.X).Normalized()));
            total++;
            if (c > Math.Cos(20 * Math.PI / 180) || c < Math.Sin(20 * Math.PI / 180)) along++;
        }
        Assert.True(along > 0.85 * total, $"{(double)along / total:P0} of the edges follow the torus");
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
