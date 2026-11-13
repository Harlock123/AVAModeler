using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Expressions: channels set by formulas reading the frame, the channel's value and other items' channels.</summary>
public class ExpressionTests
{
    [Fact]
    public void A_wheel_turns_as_its_car_rolls_and_a_bob_rides_on_the_keys()
    {
        var s = new Scene { FirstFrame = 1, LastFrame = 60 };
        var car = s.AddNull("Car");
        car[Channel.PositionZ].SetConstant(0);
        car[Channel.PositionZ].SetKey(0, new EnvelopeKey(1, 0).WithSpan(SpanKind.Linear));
        car[Channel.PositionZ].AddKey(new EnvelopeKey(31, 3).WithSpan(SpanKind.Linear));
        var wheel = s.AddNull("Wheel");
        wheel.Modifiers = [new ExpressionModifier { Channel = Channel.Pitch, Formula = "[Car.position.z] / 0.3 * deg(1)" }.Checked()];
        AssertNear(1.5 / 0.3 * 180 / Math.PI, wheel.MotionRotationAt(16).Y, 1e-9);
        Assert.Equal(0, wheel.RotationAt(16).Y); // its keys are untouched

        var buoy = s.AddNull("Buoy");
        buoy[Channel.PositionY].SetConstant(2);
        buoy.Modifiers = [new ExpressionModifier { Channel = Channel.PositionY, Formula = "value + sin(time * 2 * pi) * 0.25" }];
        AssertNear(2.25, buoy.MotionPositionAt(7.5).Y, 1e-9); // a quarter second in at 30 fps
        AssertNear(2, buoy.MotionPositionAt(15).Y, 1e-9);

        var gate = s.AddNull("Gate");
        gate.Modifiers = [new ExpressionModifier { Channel = Channel.Heading, Formula = "if([Car.world.z] > 2, clamp(([Car.world.z] - 2) * 90, 0, 90), 0)" }];
        Assert.Equal(0, gate.MotionRotationAt(10).X);
        AssertNear(90, gate.MotionRotationAt(31).X, 1e-9);
        Assert.Equal(Noise.Perlin(1.5, 0.5, 0.5), Expressions.Compile("noise(1.5)").Evaluate(gate, 1, 0));
    }

    [Fact]
    public void Mistakes_are_said_where_they_are_and_loops_are_cut()
    {
        Assert.Contains("character 5", Assert.Throws<ArgumentException>(() => Expressions.Compile("1 + )")).Message);
        Assert.Contains("\"wobble\" isn't known", Assert.Throws<ArgumentException>(() => Expressions.Compile("wobble(2)")).Message);
        Assert.Contains("should be [item.channel]", Assert.Throws<ArgumentException>(() => Expressions.Compile("[Car.speed]")).Message);
        Assert.Contains("takes 3 arguments", Assert.Throws<ArgumentException>(() => Expressions.Compile("clamp(1, 2)")).Message);
        Assert.Throws<ArgumentException>(() => new ExpressionModifier { Formula = "" }.Checked());

        var s = new Scene();
        var a = s.AddNull("A");
        var b = s.AddNull("B");
        a.Modifiers = [new ExpressionModifier { Channel = Channel.PositionX, Formula = "[B.position.x] + 1" }];
        b.Modifiers = [new ExpressionModifier { Channel = Channel.PositionX, Formula = "[A.position.x] + 1" }];
        Assert.True(double.IsFinite(a.MotionPositionAt(1).X));
        Assert.Contains("no item of that name", MotionModifier.Problem(a, new ExpressionModifier { Formula = "[Ghost.heading]" }));
        Assert.Null(MotionModifier.Problem(a, new ExpressionModifier { Formula = "[B.heading] * 2" }));
    }

    [Fact]
    public void Expressions_are_kept_in_files_copies_and_snapshots()
    {
        var s = new Scene();
        var a = s.AddNull("A");
        var e = new ExpressionModifier { Channel = Channel.Bank, Formula = "frame * 2" };
        a.Modifiers = [e];
        var back = SceneJson.Read(SceneJson.Write(s)).Scene;
        Assert.Equal(e, back.Find("A")!.Modifiers.Single());
        Assert.Equal(e, s.Copy().Find("A")!.Modifiers.Single());
        var snap = s.Snapshot();
        a.Modifiers = [];
        snap.Restore();
        Assert.Equal(e, a.Modifiers.Single());
        AssertNear(20, back.Find("A")!.MotionRotationAt(10).Z, 1e-9);
    }
}
