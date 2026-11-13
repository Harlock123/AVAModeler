using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Rigid bodies: falling, bouncing, resting, stacking, toppling, thrown; kept in files, copies and snapshots.</summary>
public class RigidBodyTests
{
    /// <summary>A box centred on its base's middle (its origin on the bottom face), w × h × d.</summary>
    private static Mesh Box(double w, double h, double d)
    {
        var m = new Mesh();
        double x = w / 2, z = d / 2;
        for (int i = 0; i < 8; i++) m.Points.Add(new Vec3((i & 1) == 0 ? -x : x, (i & 2) == 0 ? 0 : h, (i & 4) == 0 ? -z : z));
        int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        foreach (var f in faces) m.Polygons.Add(new Polygon([.. f], 0));
        m.Touch();
        return m;
    }

    private static Scene Stage(int last = 120)
    {
        var s = new Scene { FirstFrame = 1, LastFrame = last };
        s.AddCamera("Camera");
        return s;
    }

    private static ObjectItem Ground(Scene s)
    {
        var g = s.AddObject("Ground", Doc(Box(20, 0.2, 20)));
        g.SetPosition(1, new Vec3(0, -0.2, 0));
        g.RigidBody = new RigidBodySettings { Kind = RigidBodyKind.Static, Shape = CollisionShape.Box, Friction = 0.8 };
        return g;
    }

    [Fact]
    public void A_ball_falls_bounces_and_comes_to_rest_on_the_floor()
    {
        var s = Stage(150);
        var ball = s.AddObject("Ball", Doc(Sphere(Vec3.Zero, 0.25, 16, 8)));
        ball[Channel.PositionY].SetConstant(2);
        ball.RigidBody = new RigidBodySettings { Shape = CollisionShape.Sphere, Bounce = 0.6, Floor = 0 };
        double lowest = double.MaxValue, highestAfter = 0;
        bool landed = false;
        for (double f = 1; f <= 150; f += 0.5)
        {
            double y = ball.WorldPosition(f).Y;
            lowest = Math.Min(lowest, y);
            if (y < 0.3) landed = true;
            else if (landed) highestAfter = Math.Max(highestAfter, y);
        }
        Assert.True(lowest > 0.25 - 0.03, $"it sank to {lowest}");
        Assert.True(highestAfter > 0.5, $"it didn't bounce ({highestAfter})");
        AssertNear(0.25, ball.WorldPosition(150).Y, 0.02);
        // Before it starts, its keys place it; the keys themselves are untouched.
        Assert.Equal(2, ball.PositionAt(150).Y);
        Assert.Equal(2, ball.WorldPosition(1).Y, 9);
    }

    [Theory]
    [InlineData(CollisionShape.Box)]
    [InlineData(CollisionShape.Hull)]
    public void A_tilted_box_settles_flat_on_a_static_ground(CollisionShape shape)
    {
        var s = Stage(150);
        Ground(s);
        var box = s.AddObject("Box", Doc(Box(1, 1, 1)));
        box.SetPosition(1, new Vec3(0, 1.5, 0));
        box.SetRotation(1, new Vec3(20, 35, 15));
        box.RigidBody = new RigidBodySettings { Shape = shape, Bounce = 0.1 };
        var m = box.WorldMatrix(150);
        var up = m.TransformVector(Vec3.UnitY).Normalized();
        double flat = Math.Max(Math.Abs(up.X), Math.Max(Math.Abs(up.Y), Math.Abs(up.Z)));
        Assert.True(flat > 0.98, $"it isn't lying on a face ({up})");
        // Its middle is half its height above the ground's top.
        AssertNear(0.5, m.TransformPoint(new Vec3(0, 0.5, 0)).Y, 0.04);
    }

    [Fact]
    public void A_stack_of_boxes_stays_stacked()
    {
        var s = Stage(120);
        Ground(s);
        var boxes = new List<ObjectItem>();
        for (int i = 0; i < 3; i++)
        {
            var b = s.AddObject($"Box {i}", Doc(Box(1, 0.5, 1)));
            b.SetPosition(1, new Vec3(0, 0.5 * i + 0.01 * i, 0));
            b.RigidBody = new RigidBodySettings { Shape = CollisionShape.Box, Bounce = 0 };
            boxes.Add(b);
        }
        for (int i = 0; i < 3; i++)
        {
            var p = boxes[i].WorldPosition(120);
            Assert.True(Math.Abs(p.X) < 0.05 && Math.Abs(p.Z) < 0.05, $"box {i} slid to {p}");
            AssertNear(0.5 * i, p.Y, 0.05);
        }
    }

    [Fact]
    public void A_pushed_domino_knocks_the_rest_over()
    {
        var s = Stage(120);
        Ground(s);
        var dominoes = new List<ObjectItem>();
        for (int i = 0; i < 5; i++)
        {
            var d = s.AddObject($"Domino {i}", Doc(Box(0.25, 0.5, 0.06)));
            d.SetPosition(1, new Vec3(0, 0, i * 0.3));
            d.RigidBody = new RigidBodySettings { Shape = CollisionShape.Box, Bounce = 0, Friction = 0.6, Spin = i == 0 ? new Vec3(150, 0, 0) : Vec3.Zero };
            dominoes.Add(d);
        }
        foreach (var d in dominoes)
        {
            var up = d.WorldMatrix(120).TransformVector(Vec3.UnitY).Normalized();
            Assert.True(up.Y < 0.75, $"{d.Name} is still standing ({up})");
        }
        // They fell forward, toward +Z.
        Assert.True(dominoes[^1].WorldMatrix(120).TransformVector(Vec3.UnitY).Z > 0.3);
    }

    [Fact]
    public void A_thrown_ball_carries_its_speed_and_its_keyed_motion()
    {
        var s = Stage(60);
        var ball = s.AddObject("Ball", Doc(Sphere(Vec3.Zero, 0.2, 12, 6)));
        // Keyed moving +X at 3 m/s (0.1 m a frame at 30 fps) until frame 10, then let go.
        ball[Channel.PositionY].SetConstant(3);
        var x = ball[Channel.PositionX];
        x.SetConstant(0);
        x.SetKey(0, new EnvelopeKey(1, 0).WithSpan(SpanKind.Linear));
        x.AddKey(new EnvelopeKey(10, 0.9).WithSpan(SpanKind.Linear));
        ball.RigidBody = new RigidBodySettings { Shape = CollisionShape.Sphere, StartFrame = 10, Velocity = new Vec3(0, 0, 2), Damping = 0 };
        AssertNear(0.45, ball.WorldPosition(5.5).X, 1e-6);
        var at = ball.WorldPosition(40);
        AssertNear(0.9 + 3 * 1.0, at.X, 0.15);
        AssertNear(2.0, at.Z, 0.1);
        AssertNear(3 - 0.5 * 9.81 * 1.0, at.Y, 0.15);
    }

    [Fact]
    public void Rigid_bodies_are_kept_in_files_copies_and_snapshots()
    {
        var s = Stage();
        var ball = s.AddObject("Ball", Doc(Sphere(Vec3.Zero, 0.2, 12, 6)));
        var settings = new RigidBodySettings { Shape = CollisionShape.Sphere, Mass = 2, Velocity = new Vec3(1, 2, 3), Floor = 0 };
        ball.RigidBody = settings;
        var back = SceneJson.Read(SceneJson.Write(s)).Scene;
        Assert.Equal(settings, back.Objects.Single().RigidBody);
        Assert.Equal(settings, s.Copy().Objects.Single().RigidBody);
        var lws = LwsReader.Read(LwsWriter.Write(s), _ => ball.Document).Scene;
        Assert.Equal(settings, lws.Objects.Single().RigidBody);
        var snap = s.Snapshot();
        ball.RigidBody = null;
        snap.Restore();
        Assert.Equal(settings, ball.RigidBody);
        Assert.Throws<ArgumentException>(() => (settings with { Bounce = 2 }).Checked());
    }
}
