using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Geometry.Tests;

/// <summary>Bake Maps' sums: normals, occlusion, curvature and colour through a UV map, and the bleed.</summary>
public class BakingTests
{
    /// <summary>A square in the XY plane facing -Z (toward the Front view), 2 m across, UVs from its corners (U along +X, V up +Y).</summary>
    private static Mesh Square(double z = 0)
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(-1, -1, z), new Vec3(-1, 1, z), new Vec3(1, 1, z), new Vec3(1, -1, z));
        var poly = m.Polygons[0];
        for (int i = 0; i < 4; i++) m.SetCornerUV("UV", poly, poly.Points[i], new Vec2((m.Points[i].X + 1) / 2, (m.Points[i].Y + 1) / 2));
        return m;
    }

    private static (byte R, byte G, byte B) At(BakedMap map, int x, int y) => (map.Rgba[(y * map.Width + x) * 4], map.Rgba[(y * map.Width + x) * 4 + 1], map.Rgba[(y * map.Width + x) * 4 + 2]);

    private static readonly List<Surface> Plain = [new Surface("Default")];

    [Fact]
    public void A_flat_face_bakes_the_straight_up_normal_and_a_tilted_detail_tilts_it()
    {
        var low = Square();
        Assert.True(low.Normal(0).Z < -0.99);
        var flat = Baking.Bake(low, [0], Plain, new BakeSettings { Size = 32 });
        Assert.Equal(32 * 32, flat.Covered);
        Assert.Equal(((byte)128, (byte)128, (byte)255), At(flat, 16, 16));

        // The detail leans toward +X (its normal turned that way): the map's red, along U = +X, goes up.
        var detail = Square(-0.01);
        detail.Transform(Mat4.Rotation(Vec3.UnitY, Mat4.Deg(20)));
        var leaning = Baking.Bake(low, [0], Plain, new BakeSettings { Size = 32, Distance = 0.5 }, detail);
        var (r, g, b) = At(leaning, 16, 16);
        double nx = r / 255.0 * 2 - 1;
        Assert.Equal(Vec3.Dot(detail.Normal(0), Vec3.UnitX), nx, 1);
        Assert.InRange(g, 120, 136);
        Assert.True(b > 200);
    }

    [Fact]
    public void Occlusion_darkens_the_floor_by_a_box_and_curvature_lights_a_ball()
    {
        // A floor facing up, 4 m across, with a 1 m box standing in its middle.
        var m = new Mesh();
        m.AddFace(0, new Vec3(-2, 0, -2), new Vec3(-2, 0, 2), new Vec3(2, 0, 2), new Vec3(2, 0, -2));
        Assert.True(m.Normal(0).Y > 0.99);
        var floor = m.Polygons[0];
        for (int i = 0; i < 4; i++) m.SetCornerUV("UV", floor, floor.Points[i], new Vec2((m.Points[i].X + 2) / 4, (m.Points[i].Z + 2) / 4));
        m.Append(TestShapes.Box(new Vec3(-0.5, 0, -0.5), new Vec3(0.5, 1, 0.5)));
        var ao = Baking.Bake(m, [0], Plain, new BakeSettings { Kind = BakeKind.AmbientOcclusion, Size = 64, Samples = 64, Distance = 2 });
        // Beside the box (x = 0.6 m: pixel 41) against out at the edge (x = 1.9: pixel 62), level with its middle.
        Assert.True(At(ao, 41, 32).R + 40 < At(ao, 62, 32).R, $"{At(ao, 41, 32)} vs {At(ao, 62, 32)}");

        var ball = TestShapes.Sphere(Vec3.Zero, 1, 24, 12);
        for (int pi = 0; pi < ball.Polygons.Count; pi++)
        {
            var p = ball.Polygons[pi];
            var c = ball.Centroid(p);
            foreach (int q in p.Points) ball.SetCornerUV("UV", p, q, new Vec2((ball.Points[q].X + 1) / 2, (ball.Points[q].Y + 1) / 2));
        }
        var front = Enumerable.Range(0, ball.Polygons.Count).Where(i => ball.Normal(i).Z < -0.5).ToList();
        var curv = Baking.Bake(ball, front, Plain, new BakeSettings { Kind = BakeKind.Curvature, Size = 32, Padding = 0 });
        Assert.True(At(curv, 16, 16).R > 140);
    }

    [Fact]
    public void Colour_bakes_the_surface_and_the_edges_bleed()
    {
        var m = new Mesh();
        // A square on the left half of the picture only.
        m.AddFace(0, new Vec3(-1, -1, 0), new Vec3(-1, 1, 0), new Vec3(1, 1, 0), new Vec3(1, -1, 0));
        var poly = m.Polygons[0];
        for (int i = 0; i < 4; i++) m.SetCornerUV("UV", poly, poly.Points[i], new Vec2((m.Points[i].X + 1) / 4, (m.Points[i].Y + 1) / 2));
        var red = new List<Surface> { new("Red") { Color = new Rgb(255, 0, 0) } };
        var map = Baking.Bake(m, [0], red, new BakeSettings { Kind = BakeKind.Color, Size = 32, Padding = 3 });
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(map, 8, 16));
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(map, 17, 16)); // bled
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(map, 25, 16)); // beyond the bleed
        Assert.Throws<InvalidOperationException>(() => Baking.Bake(m, [0], red, new BakeSettings { UVMap = "Other" }));
        Assert.Throws<ArgumentException>(() => Baking.Bake(m, [0], red, new BakeSettings { Size = 8 }));
    }
}
