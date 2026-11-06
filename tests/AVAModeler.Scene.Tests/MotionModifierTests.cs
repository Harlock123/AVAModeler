using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Motion modifiers: lag, jitter, cycle and oscillate through <see cref="MotionSolver"/>, loops, references, copies, snapshots and files.</summary>
public class MotionModifierTests
{
    /// <summary>A leader null moving along +X from 0 at frame 0 to 10 at frame 10 (linear), and a follower null.</summary>
    private static (Scene Scene, NullItem Leader, NullItem Follower) Pair()
    {
        var scene = new Scene();
        var leader = scene.AddNull("Leader");
        leader[Channel.PositionX].Clear();
        leader[Channel.PositionX].AddKey(new EnvelopeKey(0, 0, Linear: true));
        leader[Channel.PositionX].AddKey(new EnvelopeKey(10, 10, Linear: true));
        leader[Channel.PositionX].PostBehavior = EndBehavior.Linear;
        var follower = scene.AddNull("Follower");
        return (scene, leader, follower);
    }

    [Fact]
    public void Without_modifiers_motion_is_the_keys()
    {
        var (_, _, f) = Pair();
        f.SetScale(0, new Vec3(1, 2, 3));
        f.SetPosition(0, new Vec3(1, 2, 3));
        Assert.Empty(f.Modifiers);
        Assert.Equal(f.ScaleAt(4), f.MotionScaleAt(4));
        Assert.Equal(f.PositionAt(4), f.MotionPositionAt(4));
    }

    [Fact]
    public void A_lagging_item_is_where_its_leader_was_plus_its_own_offset()
    {
        var (scene, leader, f) = Pair();
        f.SetPosition(0, new Vec3(0, 1, 0));
        int version = scene.Version;
        f.Modifiers = [new LagModifier(leader) { Delay = 3 }];
        Assert.True(scene.Version > version);
        AssertNear(new Vec3(3, 1, 0), f.WorldPosition(6));
        AssertNear(new Vec3(4.5, 1, 0), f.WorldPosition(7.5));
        // Its keys are as they were.
        Assert.Equal(new Vec3(0, 1, 0), f.PositionAt(6));

        // Under a parent, the leader's place is seen from the parent: the world position comes out the same.
        var parent = scene.AddNull("Parent");
        parent.SetPosition(0, new Vec3(0, 0, 5));
        parent.SetRotation(0, new Vec3(90, 0, 0));
        f.Parent = parent;
        f.SetPosition(0, Vec3.Zero);
        AssertNear(new Vec3(3, 0, 0), f.WorldPosition(6));

        // Half strength takes half the leader's place.
        f.Parent = null;
        f.Modifiers = [new LagModifier(leader) { Delay = 0, Strength = 0.5 }];
        AssertNear(new Vec3(4, 0, 0), f.WorldPosition(8));
    }

    [Fact]
    public void A_lag_can_take_the_leaders_rotation()
    {
        var (_, leader, f) = Pair();
        leader[Channel.Heading].Clear();
        leader[Channel.Heading].AddKey(new EnvelopeKey(0, 0, Linear: true));
        leader[Channel.Heading].AddKey(new EnvelopeKey(10, 90, Linear: true));
        f.Modifiers = [new LagModifier(leader) { Delay = 2, Rotation = true }];
        AssertNear(new Vec3(45, 0, 0), f.MotionRotationAt(7), 1e-6);
        Assert.Equal(Vec3.Zero, f.RotationAt(7));
        f.Modifiers = [new LagModifier(leader) { Delay = 2 }];
        Assert.Equal(Vec3.Zero, f.MotionRotationAt(7));
    }

    [Fact]
    public void Jitter_is_smooth_bounded_and_the_same_every_time()
    {
        var (scene, _, f) = Pair();
        var jitter = new JitterModifier { Position = new Vec3(0.2, 0, 0.1), Rotation = new Vec3(5, 0, 0), Frequency = 1, Seed = 7 };
        f.Modifiers = [jitter];
        var xs = Enumerable.Range(0, 120).Select(i => f.MotionPositionAt(i)).ToList();
        Assert.All(xs, p => Assert.InRange(p.X, -0.2 * 1.1, 0.2 * 1.1));
        Assert.All(xs, p => Assert.Equal(0, p.Y));
        Assert.True(xs.Max(p => p.X) - xs.Min(p => p.X) > 0.05, "It should wobble.");
        // Smooth: one frame at 1 Hz moves it only a little.
        for (int i = 1; i < xs.Count; i++) Assert.True(Math.Abs(xs[i].X - xs[i - 1].X) < 0.05);
        // The same in another scene, with no Random anywhere.
        var (_, _, g) = Pair();
        g.Modifiers = [jitter];
        for (int i = 0; i < 120; i += 7) Assert.Equal(f.MotionPositionAt(i), g.MotionPositionAt(i));
        // Another seed, another wobble; rotation wobbles too.
        g.Modifiers = [jitter with { Seed = 8 }];
        Assert.Contains(Enumerable.Range(0, 60), i => f.MotionPositionAt(i) != g.MotionPositionAt(i));
        Assert.Contains(Enumerable.Range(0, 60), i => f.MotionRotationAt(i).X != 0);
        Assert.NotNull(scene);
    }

    [Fact]
    public void A_cycle_plays_the_items_keys_by_its_drivers_channel()
    {
        var scene = new Scene();
        var wheel = scene.AddNull("Wheel");
        wheel[Channel.Heading].Clear();
        wheel[Channel.Heading].AddKey(new EnvelopeKey(0, 0, Linear: true));
        wheel[Channel.Heading].AddKey(new EnvelopeKey(20, 720, Linear: true));
        var crank = scene.AddNull("Crank");
        crank[Channel.PositionY].Clear();
        crank[Channel.PositionY].AddKey(new EnvelopeKey(100, 0, Linear: true));
        crank[Channel.PositionY].AddKey(new EnvelopeKey(110, 1, Linear: true));
        crank.Modifiers = [new CycleModifier(wheel) { DriverChannel = Channel.Heading, Low = 0, High = 360 }];
        // Heading 90 (frame 2.5) is a quarter of the way through its keys, 180 half, 540 half again.
        AssertNear(0.25, crank.MotionPositionAt(2.5).Y);
        AssertNear(0.5, crank.MotionPositionAt(5).Y);
        AssertNear(0.5, crank.MotionPositionAt(15).Y);
        // Its keys are untouched.
        Assert.Equal(0, crank.PositionAt(5).Y);
        // With fewer than two key frames there's nothing to play.
        var still = scene.AddNull("Still");
        still.Modifiers = [new CycleModifier(wheel)];
        Assert.Equal(still.PositionAt(5), still.MotionPositionAt(5));
    }

    [Fact]
    public void Oscillate_adds_a_damped_swing_from_its_start()
    {
        var (_, _, f) = Pair();
        f.Modifiers = [new OscillateModifier { Channel = Channel.ScaleY, Amplitude = 0.5, Period = 20, Damping = 0.1, Start = 10 }];
        Assert.Equal(new Vec3(1, 1, 1), f.MotionScaleAt(5));
        AssertNear(1 + 0.5 * Math.Exp(-0.5), f.MotionScaleAt(15).Y);
        // The scale reaches the item's transform.
        AssertNear(new Vec3(0, 1 + 0.5 * Math.Exp(-0.5), 0), f.LocalMatrix(15).TransformVector(Vec3.UnitY));
        Assert.Equal(new Vec3(1, 1, 1), f.ScaleAt(15));
        f.Modifiers = [new OscillateModifier { Channel = Channel.Bank, Amplitude = 10, Period = 40, Phase = 90, Start = 0 }];
        AssertNear(10, f.MotionRotationAt(0).Z);
        AssertNear(-10, f.MotionRotationAt(20).Z);
    }

    [Fact]
    public void Modifiers_apply_in_order_and_settings_are_checked()
    {
        var (_, leader, f) = Pair();
        f.Modifiers = [new LagModifier(leader) { Delay = 0 }, new OscillateModifier { Channel = Channel.PositionX, Amplitude = 1, Period = 4, Phase = 90 }];
        // The lag puts it at x = 4 at frame 4; the swing, a whole period on and starting at its top, adds 1.
        AssertNear(5, f.MotionPositionAt(4).X);
        Assert.Throws<ArgumentException>(() => new LagModifier(leader) { Delay = -1 }.Checked());
        Assert.Throws<ArgumentException>(() => new LagModifier(leader) { Strength = 2 }.Checked());
        Assert.Throws<ArgumentException>(() => new JitterModifier { Frequency = 0 }.Checked());
        Assert.Throws<ArgumentException>(() => new CycleModifier(leader) { Low = 1, High = 1 }.Checked());
        Assert.Throws<ArgumentException>(() => new OscillateModifier { Period = 0 }.Checked());
        Assert.Throws<ArgumentException>(() => new OscillateModifier { Damping = -1 }.Checked());
    }

    [Fact]
    public void Problem_refuses_what_would_chase_itself()
    {
        var (scene, leader, f) = Pair();
        Assert.Contains("itself", MotionModifier.Problem(f, new LagModifier(f)));
        Assert.Contains("itself", MotionModifier.Problem(f, new CycleModifier(f)));
        var child = scene.AddNull("Child");
        child.Parent = f;
        Assert.Contains("moves with", MotionModifier.Problem(f, new LagModifier(child)));
        // A child can drive a cycle: its channel is its own.
        Assert.Null(MotionModifier.Problem(f, new CycleModifier(child)));
        leader.Modifiers = [new LagModifier(f)];
        Assert.Contains("loop", MotionModifier.Problem(f, new LagModifier(leader)));
        Assert.Null(MotionModifier.Problem(f, new JitterModifier()));
        var other = new Scene().AddNull("Elsewhere");
        Assert.Contains("isn't in", MotionModifier.Problem(f, new LagModifier(other)));
    }

    [Fact]
    public void A_loop_of_lags_is_cut_where_it_closes_and_comes_out_the_same_whichever_way_it_is_asked()
    {
        Vec3 A(bool aFirst)
        {
            var (_, leader, f) = Pair();
            // Set straight on the items, past Problem's check: the solver must still stop.
            leader.Modifiers = [new LagModifier(f) { Delay = 1 }];
            f.Modifiers = [new LagModifier(leader) { Delay = 2 }];
            if (!aFirst) _ = f.WorldPosition(8);
            return leader.WorldPosition(8);
        }
        var first = A(true);
        Assert.True(double.IsFinite(first.X));
        Assert.Equal(first, A(false));
    }

    [Fact]
    public void A_modifier_on_a_changed_leader_sees_the_change()
    {
        var (_, leader, f) = Pair();
        f.Modifiers = [new LagModifier(leader) { Delay = 1 }];
        AssertNear(new Vec3(4, 0, 0), f.WorldPosition(5));
        leader[Channel.PositionY].SetConstant(2);
        AssertNear(new Vec3(4, 2, 0), f.WorldPosition(5));
    }

    [Fact]
    public void Removing_a_leader_drops_its_lag_and_undo_brings_it_back()
    {
        var (scene, leader, f) = Pair();
        var jitter = new JitterModifier();
        f.Modifiers = [new LagModifier(leader), jitter];
        var before = scene.Snapshot();
        scene.Remove(leader);
        Assert.Equal([jitter], f.Modifiers);
        before.Restore();
        Assert.Equal(2, f.Modifiers.Count);
        Assert.Same(leader, f.Modifiers[0].Reference);
        // A snapshot keeps the list it was taken with.
        var snap = scene.Snapshot();
        f.Modifiers = [];
        snap.Restore();
        Assert.Equal(2, f.Modifiers.Count);
    }

    [Fact]
    public void Copies_point_their_modifiers_at_the_copies()
    {
        var (scene, leader, f) = Pair();
        f.Modifiers = [new LagModifier(leader) { Delay = 2 }, new OscillateModifier()];
        var copy = scene.Copy();
        var cf = copy.Find("Follower")!;
        Assert.Same(copy.Find("Leader"), cf.Modifiers[0].Reference);
        AssertNear(f.WorldPosition(6), cf.WorldPosition(6));

        // Cloned on its own in the same scene, the copy follows the same leader.
        var map = scene.CopyItems([f]);
        Assert.Same(leader, map[f].Modifiers[0].Reference);
        // Merged into another scene without its leader, the lag is dropped and the rest kept.
        var other = new Scene();
        var merged = other.CopyItems([f]);
        Assert.IsType<OscillateModifier>(Assert.Single(merged[f].Modifiers));
        // With its leader, it follows the leader's copy.
        var both = new Scene().CopyItems([leader, f]);
        Assert.Same(both[leader], both[f].Modifiers[0].Reference);
    }

    [Fact]
    public void Avascene_files_keep_every_kind_and_leave_them_out_when_there_are_none()
    {
        var (scene, leader, f) = Pair();
        Assert.DoesNotContain("modifiers", SceneJson.Write(scene));
        var mods = new MotionModifier[]
        {
            new LagModifier(leader) { Delay = 2.5, Strength = 0.75, Rotation = true },
            new JitterModifier { Position = new Vec3(0.1, 0.2, 0.3), Rotation = new Vec3(1, 2, 3), Frequency = 4, Seed = 9 },
            new CycleModifier(leader) { DriverChannel = Channel.PositionZ, Low = -1, High = 3 },
            new OscillateModifier { Channel = Channel.Pitch, Amplitude = 12, Period = 17, Phase = 30, Damping = 0.02, Start = 5 },
        };
        f.Modifiers = mods;
        var back = SceneJson.Read(SceneJson.Write(scene));
        Assert.Empty(back.Warnings);
        var bf = back.Scene.Find("Follower")!;
        var bl = back.Scene.Find("Leader")!;
        Assert.Equal(4, bf.Modifiers.Count);
        Assert.Equal(mods[0] with { }, ((LagModifier)bf.Modifiers[0]) with { Leader = leader });
        Assert.Equal(mods[1], bf.Modifiers[1]);
        Assert.Equal(mods[2], ((CycleModifier)bf.Modifiers[2]) with { Driver = leader });
        Assert.Equal(mods[3], bf.Modifiers[3]);
        Assert.Same(bl, bf.Modifiers[0].Reference);
        AssertNear(f.WorldPosition(7), bf.WorldPosition(7));
    }

    [Fact]
    public void Avascene_files_with_a_missing_leader_or_bad_settings_leave_the_modifier_out_with_a_warning()
    {
        var (scene, leader, f) = Pair();
        f.Modifiers = [new LagModifier(leader), new OscillateModifier()];
        var json = SceneJson.Write(scene).Replace("\"leader\": 0", "\"leader\": 9").Replace("\"period\": 30", "\"period\": -1");
        var back = SceneJson.Read(json);
        Assert.Empty(back.Scene.Find("Follower")!.Modifiers);
        Assert.Equal(2, back.Warnings.Count);
        Assert.Contains(back.Warnings, w => w.Contains("lag") && w.Contains("isn't in the scene"));
        Assert.Contains(back.Warnings, w => w.Contains("oscillate") && w.Contains("period"));
    }

    [Fact]
    public void Lws_files_have_no_place_for_modifiers_and_say_so()
    {
        var (scene, leader, f) = Pair();
        f.Modifiers = [new LagModifier(leader), new JitterModifier()];
        var warnings = new List<string>();
        LwsWriter.Write(scene, warnings);
        Assert.Contains(warnings, w => w.Contains("Follower") && w.Contains("lag, jitter") && w.Contains("no place"));
    }

    [Fact]
    public void A_lagging_object_is_drawn_and_rendered_where_its_leader_was()
    {
        var scene = new Scene { BackgroundColor = new Vec3(0, 0, 1), AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -6));
        cam.Width = 40;
        cam.Height = 30;
        scene.AddLight("Light");
        var leader = scene.AddNull("Leader");
        leader[Channel.PositionX].Clear();
        leader[Channel.PositionX].AddKey(new EnvelopeKey(0, -2, Linear: true));
        leader[Channel.PositionX].AddKey(new EnvelopeKey(10, 2, Linear: true));
        var ball = scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 0.6)));
        ball.Modifiers = [new LagModifier(leader) { Delay = 10 }];
        // At frame 10 the leader is at x = 2 but the ball is where it was at frame 0: x = -2.
        var frame = scene.Evaluate(10);
        var bounds = frame.Objects.Single().WorldMesh.Bounds();
        AssertNear(new Vec3(-2, 0, 0), (bounds.Min + bounds.Max) / 2, 1e-6);
        var image = RayTracer.Render(scene, 10, null, null, TestContext.Current.CancellationToken);
        static bool Blue((byte R, byte G, byte B, byte A) p) => p.R == 0 && p.G == 0 && p.B == 255;
        // Left of centre is the ball; right of centre, where the leader is, is backdrop.
        Assert.Contains(Enumerable.Range(0, 20), x => !Blue(image.Pixel(x, 15)));
        Assert.All(Enumerable.Range(20, 20), x => Assert.True(Blue(image.Pixel(x, 15)), $"pixel {x}"));
    }
}
