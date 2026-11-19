using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Geometry.Tests;

public class UnwrapTests
{
    private static List<int> AllFaces(Mesh m) => Enumerable.Range(0, m.Polygons.Count).ToList();

    private static void InSquare(UnwrapResult r)
    {
        foreach (var face in r.UVs)
            foreach (var uv in face)
            {
                Assert.InRange(uv.X, -1e-9, 1 + 1e-9);
                Assert.InRange(uv.Y, -1e-9, 1 + 1e-9);
            }
    }

    /// <summary>Signed area of a face's UVs (negative: clockwise, the views' way round).</summary>
    private static double Area(Vec2[] uv)
    {
        double s = 0;
        for (int i = 0; i < uv.Length; i++) s += Vec2.Cross(uv[i], uv[(i + 1) % uv.Length]);
        return s / 2;
    }

    /// <summary>The largest share by which charts' UV area per square metre differ (0: all at one scale).</summary>
    private static double ScaleSpread(Mesh m, UnwrapResult r)
    {
        var ratios = new List<double>();
        for (int f = 0; f < m.Polygons.Count; f++)
        {
            double a = m.Area(m.Polygons[f]);
            if (a > 1e-12) ratios.Add(Math.Abs(Area(r.UVs[f])) / a);
        }
        return ratios.Max() / ratios.Min() - 1;
    }

    [Fact]
    public void A_flat_patch_is_one_chart_with_no_distortion()
    {
        var m = TestShapes.Grid(6, 2);
        var r = Unwrap.Run(m, AllFaces(m));
        Assert.Equal(1, r.Charts);
        Assert.Equal(0, r.Flipped);
        Assert.InRange(r.Stretch, 1, 1.0001);
        Assert.True(r.Coverage > 0.8, $"coverage {r.Coverage}");
        InSquare(r);
        // Every face the same size in UV, as on the model.
        Assert.True(ScaleSpread(m, r) < 1e-6);
    }

    [Fact]
    public void Faces_come_out_wound_as_the_front_view_sees_them()
    {
        // A quad facing the front (−Z), wound clockwise as seen from there: its UVs run clockwise too, as Make UVs' do.
        var m = new Mesh();
        m.AddFace(0, new Vec3(-1, -1, 0), new Vec3(-1, 1, 0), new Vec3(1, 1, 0), new Vec3(1, -1, 0));
        Assert.True(m.Normal(0).Z < 0);
        var r = Unwrap.Run(m, [0]);
        Assert.True(Area(r.UVs[0]) < 0);
        Assert.Equal(0, r.Flipped);
    }

    [Fact]
    public void A_box_cuts_at_its_edges_into_six_square_charts()
    {
        var m = TestShapes.Box(2);
        var r = Unwrap.Run(m, AllFaces(m));
        Assert.Equal(6, r.Charts);
        Assert.Equal(0, r.Flipped);
        Assert.InRange(r.Stretch, 1, 1.0001);
        InSquare(r);
        Assert.True(ScaleSpread(m, r) < 1e-6);
        // Charts don't overlap: their total area is the sum of the faces'.
        Assert.True(r.Coverage > 0.4 && r.Coverage <= 1);
    }

    [Fact]
    public void A_closed_smooth_ball_is_split_until_it_lies_flat()
    {
        var m = TestShapes.Sphere(Vec3.Zero, 1, 24, 12);
        var r = Unwrap.Run(m, AllFaces(m), options: new UnwrapOptions { SeamAngle = 0 });
        Assert.True(r.Charts >= 2);
        Assert.True(r.CutCharts >= 1);
        Assert.Equal(0, r.Flipped);
        Assert.True(r.Stretch < 1.3, $"stretch {r.Stretch}");
        InSquare(r);
    }

    [Fact]
    public void A_seam_down_a_tube_opens_it_into_one_flat_strip()
    {
        var m = TestShapes.Cylinder(sides: 16, segments: 4, radius: 1, height: 3, caps: false);
        // The edges at angle 0, from bottom to top (point i of each ring).
        var seam = Enumerable.Range(0, 4).Select(j => Edge.Of(j * 16, (j + 1) * 16)).ToList();
        var r = Unwrap.Run(m, AllFaces(m), seam, new UnwrapOptions { SeamAngle = 0 });
        Assert.Equal(1, r.Charts);
        Assert.Equal(0, r.CutCharts);
        Assert.Equal(0, r.Flipped);
        Assert.True(r.Stretch < 1.01, $"stretch {r.Stretch}");
        InSquare(r);
        // The strip is the tube's height by its girth: about 3 by 6.2, so twice as wide as high.
        var all = r.UVs.SelectMany(u => u).ToList();
        double w = all.Max(p => p.X) - all.Min(p => p.X), h = all.Max(p => p.Y) - all.Min(p => p.Y);
        double ratio = Math.Max(w, h) / Math.Min(w, h);
        Assert.InRange(ratio, 1.8, 2.3);
    }

    [Fact]
    public void Surfaces_are_cut_apart_unless_asked_not_to()
    {
        var m = TestShapes.Grid(4, 2);
        for (int f = 0; f < 8; f++) m.Polygons[f].Surface = 1;
        Assert.Equal(2, Unwrap.Run(m, AllFaces(m)).Charts);
        Assert.Equal(1, Unwrap.Run(m, AllFaces(m), options: new UnwrapOptions { SurfaceSeams = false }).Charts);
    }

    [Fact]
    public void The_same_mesh_unwraps_the_same_every_time()
    {
        var m = TestShapes.Sphere(Vec3.Zero, 1, 12, 6);
        var a = Unwrap.Run(m, AllFaces(m));
        var b = Unwrap.Run(m, AllFaces(m));
        for (int f = 0; f < a.UVs.Count; f++) Assert.Equal(a.UVs[f], b.UVs[f]);
    }
}
