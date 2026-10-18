using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

public class EnvelopeTests
{
    private static Envelope Make(params EnvelopeKey[] keys)
    {
        var e = new Envelope();
        foreach (var k in keys) e.AddKey(k);
        return e;
    }

    [Fact]
    public void EmptyIsZeroAndOneKeyIsConstant()
    {
        Assert.Equal(0, new Envelope().Evaluate(5));
        var e = Envelope.Constant(3);
        Assert.True(e.IsConstant);
        Assert.Equal(3, e.Evaluate(-100));
        Assert.Equal(3, e.Evaluate(100));
    }

    [Fact]
    public void PassesThroughEveryKey()
    {
        var e = Make(new(0, 1, 0.3, -0.2, 0.5), new(7, -4, -0.5, 0.4, 0.1), new(12, 9, 1, 0, 0), new(30, 2, 0, 0, 0, true), new(31, 5));
        foreach (var k in e.Keys) AssertNear(k.Value, e.Evaluate(k.Frame));
    }

    [Fact]
    public void EvenKeysMatchHandWorkedValue()
    {
        // Keys (0,0) (10,10) (20,0): the tangent at the middle key is flat, the first key's is the chord.
        var e = Make(new(0, 0), new(10, 10), new(20, 0));
        AssertNear(6.25, e.Evaluate(5));
        AssertNear(6.25, e.Evaluate(15));
    }

    [Fact]
    public void UnevenKeysUseTheTimingAdjustment()
    {
        // Out tangent at (10,10) = (20/30)·((10-0) + (40-10)) = 26.67; in tangent at the last key = 30.
        // At u = 0.5: 0.5·10 + 0.5·40 + 0.125·26.67 − 0.125·30.
        var e = Make(new(0, 0), new(10, 10), new(30, 40));
        AssertNear(25 + 0.125 * (80.0 / 3) - 0.125 * 30, e.Evaluate(20));
    }

    [Fact]
    public void TensionScalesTheTangents()
    {
        // Tension 0.5 halves both tangent terms: out = (2/3)·(0.5·10 + 0.5·30).
        var e = Make(new(0, 0), new(10, 10, Tension: 0.5), new(30, 40));
        AssertNear(25 + 0.125 * (40.0 / 3) - 0.125 * 30, e.Evaluate(20));
    }

    [Fact]
    public void ContinuityAndBiasMatchTheFormula()
    {
        // At the middle key: a = (1−t)(1+c)(1+b), b = (1−t)(1−c)(1−b) for the outgoing tangent.
        double c = 0.3, bias = -0.4;
        var e = Make(new(0, 0), new(10, 10, 0, c, bias), new(20, 30));
        double a = (1 + c) * (1 + bias), b = (1 - c) * (1 - bias);
        double outgoing = 0.5 * (a * 10 + b * 20);
        double incoming = 20; // last key: a·d with zeros
        AssertNear(0.5 * 10 + 0.5 * 30 + 0.125 * outgoing - 0.125 * incoming, e.Evaluate(15));
    }

    [Fact]
    public void TensionOneIsFlatAtTheKey()
    {
        var e = Make(new(0, 0), new(10, 10, Tension: 1), new(20, 30));
        double h = 1e-4;
        AssertNear(0, (e.Evaluate(10) - e.Evaluate(10 - h)) / h, 1e-3);
        AssertNear(0, (e.Evaluate(10 + h) - e.Evaluate(10)) / h, 1e-3);
    }

    [Fact]
    public void LinearFlagMakesTheSegmentIntoTheKeyStraight()
    {
        var e = Make(new(0, 0), new(10, 10), new(20, 0, Linear: true), new(30, 50));
        AssertNear(5, e.Evaluate(15));
        AssertNear(2, e.Evaluate(18));
        // The next segment is a curve again; a linear key's own tangent ignores tension, continuity and bias.
        AssertNear(25 + 0.125 * 20 - 0.125 * 50, e.Evaluate(25));
    }

    [Fact]
    public void TwoPlainKeysInterpolateInAStraightLine()
    {
        var e = Make(new(0, 0), new(10, 10));
        for (int f = 0; f <= 10; f++) AssertNear(f, e.Evaluate(f), 1e-12);
    }

    [Theory]
    [InlineData(EndBehavior.Reset, 15, 0)]
    [InlineData(EndBehavior.Constant, 15, 10)]
    [InlineData(EndBehavior.Repeat, 15, 5)]
    [InlineData(EndBehavior.Repeat, 23, 3)]
    [InlineData(EndBehavior.Oscillate, 12, 8)]
    [InlineData(EndBehavior.Oscillate, 22, 2)]
    [InlineData(EndBehavior.OffsetRepeat, 15, 15)]
    [InlineData(EndBehavior.OffsetRepeat, 32, 32)]
    [InlineData(EndBehavior.Linear, 20, 20)]
    public void PostBehaviours(EndBehavior behaviour, double frame, double expected)
    {
        var e = Make(new(0, 0), new(10, 10));
        e.PostBehavior = behaviour;
        AssertNear(expected, e.Evaluate(frame), 1e-9);
    }

    [Theory]
    [InlineData(EndBehavior.Reset, -5, 0)]
    [InlineData(EndBehavior.Constant, -5, 0)]
    [InlineData(EndBehavior.Repeat, -5, 5)]
    [InlineData(EndBehavior.Oscillate, -3, 3)]
    [InlineData(EndBehavior.Oscillate, -13, 7)]
    [InlineData(EndBehavior.OffsetRepeat, -5, -5)]
    [InlineData(EndBehavior.Linear, -10, -10)]
    public void PreBehaviours(EndBehavior behaviour, double frame, double expected)
    {
        var e = Make(new(0, 0), new(10, 10));
        e.PreBehavior = behaviour;
        AssertNear(expected, e.Evaluate(frame), 1e-9);
    }

    [Fact]
    public void RepeatWorksWhenTheFirstKeyIsntAtZero()
    {
        var e = Make(new(5, 0), new(15, 10));
        e.PostBehavior = EndBehavior.Repeat;
        e.PreBehavior = EndBehavior.Repeat;
        AssertNear(3, e.Evaluate(18));
        AssertNear(7, e.Evaluate(2));
    }

    [Fact]
    public void KeysCanBeAddedReplacedMovedAndRemoved()
    {
        var e = new Envelope();
        int changes = 0;
        e.Changed += () => changes++;
        e.AddKey(10, 1);
        e.AddKey(0, 2);
        e.AddKey(new EnvelopeKey(5, 3, Tension: 0.5));
        Assert.Equal([0.0, 5, 10], e.Keys.Select(k => k.Frame));
        e.AddKey(5, 4);
        Assert.Equal(3, e.Keys.Count);
        Assert.Equal(0.5, e.Keys[1].Tension);
        Assert.Equal(4, e.Keys[1].Value);

        int moved = e.MoveKey(1, 20);
        Assert.Equal(2, moved);
        Assert.Equal([0.0, 10, 20], e.Keys.Select(k => k.Frame));
        e.MoveKey(2, 10); // lands on an existing key and replaces it
        Assert.Equal(2, e.Keys.Count);
        Assert.Equal(4, e.Keys[1].Value);

        Assert.True(e.RemoveKeyAt(0));
        Assert.False(e.RemoveKeyAt(99));
        e.RemoveKey(0);
        Assert.Empty(e.Keys);
        e.SetConstant(7);
        Assert.Equal(7, e.Evaluate(1000));
        Assert.Equal(changes, e.Version);
        Assert.True(changes >= 8);
    }

    [Fact]
    public void SamplesSpanTheRange()
    {
        var e = Make(new(0, 0), new(10, 10));
        var s = e.Sample(0, 10, 11);
        Assert.Equal(11, s.Length);
        Assert.Equal((10.0, 10.0), s[^1]);
        AssertNear(4, s[4].Value);
        Assert.Single(e.Sample(3, 3, 1));
        Assert.Empty(e.Sample(0, 1, 0));
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var e = Make(new(0, 0), new(10, 10));
        e.PostBehavior = EndBehavior.Repeat;
        var c = e.Clone();
        e.AddKey(20, 5);
        Assert.Equal(2, c.Keys.Count);
        Assert.Equal(EndBehavior.Repeat, c.PostBehavior);
    }
}
