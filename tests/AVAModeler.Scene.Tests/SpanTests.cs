using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Key span types: TCB, linear, stepped and Bezier spans, their slopes, behaviours, and how .avascene and .lws keep them.</summary>
public class SpanTests
{
    private static Envelope Make(params EnvelopeKey[] keys)
    {
        var e = new Envelope();
        foreach (var k in keys) e.AddKey(k);
        return e;
    }

    /// <summary>The curve's slope at a frame, from either side.</summary>
    private static double Slope(Envelope e, double frame, bool after) =>
        after ? (e.Evaluate(frame + 1e-6) - e.Evaluate(frame)) / 1e-6 : (e.Evaluate(frame) - e.Evaluate(frame - 1e-6)) / 1e-6;

    [Fact]
    public void Old_style_keys_have_the_default_span_and_evaluate_as_before()
    {
        var k = new EnvelopeKey(3, 4, 0.5, 0, 0);
        Assert.Equal(SpanKind.Tcb, k.Kind);
        Assert.True(k.HasDefaultSpan);
        Assert.Equal(SpanKind.Linear, new EnvelopeKey(3, 4, Linear: true).Kind);
        Assert.Equal(new EnvelopeKey(3, 4, Linear: true), new EnvelopeKey(3, 4) with { Linear = true });
        // WithSpan keeps the linear flag in step with the span.
        Assert.True(k.WithSpan(SpanKind.Linear).Linear);
        Assert.False(new EnvelopeKey(3, 4, Linear: true).WithSpan(SpanKind.Stepped).Linear);
        Assert.Equal(SpanKind.Stepped, new EnvelopeKey(3, 4, Linear: true).WithSpan(SpanKind.Stepped).Kind);

        // A TCB and linear envelope gives exactly the numbers the Kochanek–Bartels formula gave before spans existed.
        var e = Make(new(0, 0), new(10, 10), new(20, 0));
        Assert.Equal(6.25, e.Evaluate(5), 12);
        Assert.Equal(6.25, e.Evaluate(15), 12);
        var tcb = Make(new(0, 1, 0.3, -0.2, 0.5), new(7, -4, -0.5, 0.4, 0.1), new(12, 9, 1, 0, 0), new(30, 2, 0, 0, 0, true), new(31, 5));
        tcb.PreBehavior = EndBehavior.Linear;
        tcb.PostBehavior = EndBehavior.Linear;
        foreach (var f in new[] { -5.0, 0, 2.5, 7, 9.1, 12, 20, 30, 30.5, 31, 40 })
            Assert.Equal(OldEvaluate(tcb.Keys, f), tcb.Evaluate(f));
    }

    /// <summary>The envelope's code as it was before span types, for keys with only tension, continuity, bias and the linear flag.</summary>
    private static double OldEvaluate(IReadOnlyList<EnvelopeKey> keys, double frame)
    {
        double Outgoing(int i)
        {
            var k0 = keys[i];
            var k1 = keys[i + 1];
            double d = k1.Value - k0.Value, a = 1, b = 1;
            if (!k0.Linear)
            {
                a = (1 - k0.Tension) * (1 + k0.Continuity) * (1 + k0.Bias);
                b = (1 - k0.Tension) * (1 - k0.Continuity) * (1 - k0.Bias);
            }
            if (i == 0) return b * d;
            var prev = keys[i - 1];
            double t = (k1.Frame - k0.Frame) / (k1.Frame - prev.Frame);
            return t * (a * (k0.Value - prev.Value) + b * d);
        }
        double Incoming(int i)
        {
            var k0 = keys[i];
            var k1 = keys[i + 1];
            double d = k1.Value - k0.Value, a = 1, b = 1;
            if (!k1.Linear)
            {
                a = (1 - k1.Tension) * (1 - k1.Continuity) * (1 + k1.Bias);
                b = (1 - k1.Tension) * (1 + k1.Continuity) * (1 - k1.Bias);
            }
            if (i + 2 >= keys.Count) return a * d;
            var next = keys[i + 2];
            double t = (k1.Frame - k0.Frame) / (next.Frame - k0.Frame);
            return t * (b * (next.Value - k1.Value) + a * d);
        }
        var first = keys[0];
        var last = keys[^1];
        if (frame < first.Frame)
        {
            double span = keys[1].Frame - keys[0].Frame;
            return first.Value + (keys[1].Linear ? (keys[1].Value - keys[0].Value) / span : Outgoing(0) / span) * (frame - first.Frame);
        }
        if (frame > last.Frame)
        {
            int j = keys.Count - 2;
            double span = keys[j + 1].Frame - keys[j].Frame;
            return last.Value + (keys[j + 1].Linear ? (keys[j + 1].Value - keys[j].Value) / span : Incoming(j) / span) * (frame - last.Frame);
        }
        int s = 0;
        while (s < keys.Count - 2 && keys[s + 1].Frame <= frame) s++;
        var a0 = keys[s];
        var a1 = keys[s + 1];
        if (frame <= a0.Frame) return a0.Value;
        if (frame >= a1.Frame) return a1.Value;
        double u = (frame - a0.Frame) / (a1.Frame - a0.Frame);
        if (a1.Linear) return a0.Value + u * (a1.Value - a0.Value);
        double u2 = u * u, u3 = u2 * u;
        double h1 = 2 * u3 - 3 * u2 + 1, h2 = -2 * u3 + 3 * u2, h3 = u3 - 2 * u2 + u, h4 = u3 - u2;
        return h1 * a0.Value + h2 * a1.Value + h3 * Outgoing(s) + h4 * Incoming(s);
    }

    [Fact]
    public void A_stepped_span_holds_the_previous_value_then_jumps()
    {
        var e = Make(new(0, 1), new(10, 5, Span: SpanKind.Stepped), new(20, 2));
        Assert.Equal(1, e.Evaluate(0));
        Assert.Equal(1, e.Evaluate(5));
        Assert.Equal(1, e.Evaluate(9.999));
        Assert.Equal(5, e.Evaluate(10));
        // The TCB span after a stepped key leaves it flat.
        AssertNear(0, Slope(e, 10, after: true), 1e-4);
        Assert.Equal((0.0, 0.0), (e.Slopes(1).In, e.Slopes(0).Out));
    }

    [Fact]
    public void A_bezier_span_uses_the_previous_keys_out_slope_and_its_own_in_slope()
    {
        var e = Make(new(0, 0, OutSlope: 2), new(10, 10, Span: SpanKind.Bezier, InSlope: -1, OutSlope: 0.5), new(20, 10));
        // Through its keys, leaving and arriving at the slopes given.
        Assert.Equal(0, e.Evaluate(0));
        Assert.Equal(10, e.Evaluate(10));
        AssertNear(2, Slope(e, 0, after: true), 1e-4);
        AssertNear(-1, Slope(e, 10, after: false), 1e-4);
        // The cubic Hermite by hand at the middle: (v0 + v1) / 2 + (out - in) * len / 8.
        AssertNear(5 + (2 - -1) * 10 / 8.0, e.Evaluate(5));
        // A Bezier with handles a third of the way along is the same curve.
        double u = 0.3, p0 = 0, p1 = 0 + 2 * 10 / 3.0, p2 = 10 - -1 * 10 / 3.0, p3 = 10;
        double bez = Math.Pow(1 - u, 3) * p0 + 3 * Math.Pow(1 - u, 2) * u * p1 + 3 * (1 - u) * u * u * p2 + u * u * u * p3;
        AssertNear(bez, e.Evaluate(3));
        // The TCB span after a Bezier key leaves it at the key's out-slope.
        AssertNear(0.5, Slope(e, 10, after: true), 1e-4);
        Assert.Equal((-1.0, 0.5), e.Slopes(1));
    }

    [Fact]
    public void Linear_spans_set_by_kind_or_by_the_flag_are_the_same()
    {
        var byFlag = Make(new(0, 0), new(10, 10, Linear: true), new(20, 0));
        var byKind = Make(new(0, 0), new EnvelopeKey(10, 10).WithSpan(SpanKind.Linear), new(20, 0));
        var bySpanOnly = Make(new(0, 0), new(10, 10, Span: SpanKind.Linear), new(20, 0));
        foreach (var f in new[] { 2.0, 5, 9, 12, 17 })
        {
            Assert.Equal(byFlag.Evaluate(f), byKind.Evaluate(f));
            Assert.Equal(byFlag.Evaluate(f), bySpanOnly.Evaluate(f));
        }
        AssertNear(5, byKind.Evaluate(5));
    }

    [Fact]
    public void Curves_are_continuous_at_every_key_except_into_a_step()
    {
        var e = Make(new(0, 0), new(5, 3, 0.4), new(10, 8, Linear: true), new(15, 2, Span: SpanKind.Bezier, InSlope: 1, OutSlope: -2),
            new(20, 6), new(25, 1, Span: SpanKind.Stepped), new(30, 4, Span: SpanKind.Bezier, InSlope: 0, OutSlope: 0));
        foreach (var k in e.Keys)
        {
            AssertNear(k.Value, e.Evaluate(k.Frame));
            if (k.Kind != SpanKind.Stepped) AssertNear(k.Value, e.Evaluate(k.Frame - 1e-7), 1e-4);
            AssertNear(k.Value, e.Evaluate(k.Frame + 1e-7), 1e-4);
        }
        // Into the step, the value jumps.
        AssertNear(6, e.Evaluate(25 - 1e-7), 1e-4);
        // A Bezier key with equal slopes either side is smooth through it.
        var smooth = Make(new(0, 0, OutSlope: 1), new(10, 10, Span: SpanKind.Bezier, InSlope: 0.7, OutSlope: 0.7), new(20, 12, Span: SpanKind.Bezier, InSlope: 0));
        AssertNear(Slope(smooth, 10, after: false), Slope(smooth, 10, after: true), 1e-4);
    }

    [Theory]
    [InlineData(EndBehavior.Constant)]
    [InlineData(EndBehavior.Reset)]
    [InlineData(EndBehavior.Repeat)]
    [InlineData(EndBehavior.Oscillate)]
    [InlineData(EndBehavior.OffsetRepeat)]
    [InlineData(EndBehavior.Linear)]
    public void Behaviours_work_with_stepped_and_bezier_spans(EndBehavior behavior)
    {
        var e = Make(new(0, 1, OutSlope: 0.5), new(10, 4, Span: SpanKind.Bezier, InSlope: 2), new(20, 6, Span: SpanKind.Stepped));
        e.PreBehavior = e.PostBehavior = behavior;
        double inside = e.Evaluate(5), stepped = e.Evaluate(15);
        Assert.Equal(4, stepped);
        switch (behavior)
        {
            case EndBehavior.Constant:
                Assert.Equal(1, e.Evaluate(-5));
                Assert.Equal(6, e.Evaluate(25));
                break;
            case EndBehavior.Reset:
                Assert.Equal(0, e.Evaluate(-5));
                Assert.Equal(0, e.Evaluate(25));
                break;
            case EndBehavior.Repeat:
                AssertNear(inside, e.Evaluate(25));
                AssertNear(stepped, e.Evaluate(35));
                AssertNear(stepped, e.Evaluate(-5));
                break;
            case EndBehavior.Oscillate:
                AssertNear(stepped, e.Evaluate(25));
                AssertNear(inside, e.Evaluate(35));
                break;
            case EndBehavior.OffsetRepeat:
                AssertNear(inside + 5, e.Evaluate(25));
                AssertNear(stepped - 5, e.Evaluate(-5));
                break;
            case EndBehavior.Linear:
                // Before: the Bezier span's start slope (the first key's out-slope); after: the stepped span is flat.
                AssertNear(1 - 0.5 * 4, e.Evaluate(-4));
                Assert.Equal(6, e.Evaluate(30));
                break;
        }
        // Linear after a Bezier end carries on at the last key's in-slope.
        var b = Make(new(0, 0), new(10, 5, Span: SpanKind.Bezier, InSlope: 3));
        b.PostBehavior = EndBehavior.Linear;
        AssertNear(5 + 3 * 2, b.Evaluate(12));
    }

    [Fact]
    public void Slopes_report_the_curve_and_scaling_time_divides_them()
    {
        var e = Make(new(0, 0), new(10, 10), new(20, 0));
        var (i1, o1) = e.Slopes(1);
        AssertNear(Slope(e, 10, after: false), i1, 1e-4);
        AssertNear(Slope(e, 10, after: true), o1, 1e-4);
        var lin = Make(new(0, 0), new(10, 10, Linear: true));
        Assert.Equal((1.0, 0.0), lin.Slopes(1));
        Assert.Equal((0.0, 1.0), lin.Slopes(0));

        var bez = Make(new(0, 0, OutSlope: 2), new(10, 10, Span: SpanKind.Bezier, InSlope: -1));
        double mid = bez.Evaluate(5);
        bez.ScaleTime(2);
        Assert.Equal((-0.5, 0.0), bez.Slopes(1));
        AssertNear(mid, bez.Evaluate(10));
    }

    [Fact]
    public void Avascene_files_keep_spans_and_slopes_and_leave_them_out_by_default()
    {
        var scene = new Scene();
        var n = scene.AddNull("Ball");
        var x = n[Channel.PositionX];
        x.AddKey(new EnvelopeKey(1, 0, OutSlope: 0.25));
        x.AddKey(new EnvelopeKey(10, 3, 0.5, Span: SpanKind.Bezier, InSlope: -0.5, OutSlope: 1.5));
        x.AddKey(new EnvelopeKey(20, 1).WithSpan(SpanKind.Stepped));
        x.AddKey(new EnvelopeKey(30, 2, Linear: true));
        n[Channel.PositionY].AddKey(new EnvelopeKey(1, 0));
        n[Channel.PositionY].AddKey(new EnvelopeKey(9, 2));
        string json = SceneJson.Write(scene);
        Assert.Contains("\"span\": \"Bezier\"", json);
        Assert.Contains("\"span\": \"Stepped\"", json);
        Assert.Contains("\"inSlope\": -0.5", json);
        Assert.Contains("\"outSlope\": 0.25", json);
        var (read, warnings) = SceneJson.Read(json);
        Assert.Empty(warnings);
        var rx = read.Find("Ball")![Channel.PositionX];
        Assert.Equal(x.Keys, rx.Keys);
        // A scene without stepped or Bezier keys or slopes writes no span fields, as files did before.
        var plain = new Scene();
        var p = plain.AddNull("Plain");
        p[Channel.PositionX].AddKey(new EnvelopeKey(1, 0));
        p[Channel.PositionX].AddKey(new EnvelopeKey(5, 1, Linear: true));
        string pj = SceneJson.Write(plain);
        Assert.DoesNotContain("span", pj);
        Assert.DoesNotContain("Slope", pj);
        // An old file (no span fields) reads as TCB and linear keys.
        var (old, _) = SceneJson.Read(pj);
        Assert.Equal(p[Channel.PositionX].Keys, old.Find("Plain")![Channel.PositionX].Keys);
    }

    [Fact]
    public void Lws_files_write_stepped_as_4_and_bezier_as_hermite_with_slopes_per_second()
    {
        var scene = new Scene { FramesPerSecond = 24 };
        var n = scene.AddNull("Ball");
        var x = n[Channel.PositionX];
        x.AddKey(new EnvelopeKey(0, 0, OutSlope: 0.25));
        x.AddKey(new EnvelopeKey(12, 3, 0.5, Span: SpanKind.Bezier, InSlope: -0.5, OutSlope: 1.5));
        x.AddKey(new EnvelopeKey(24, 1).WithSpan(SpanKind.Stepped));
        x.AddKey(new EnvelopeKey(36, 2, Linear: true));
        var warnings = new List<string>();
        string text = LwsWriter.Write(scene, warnings);
        Assert.Empty(warnings);
        Assert.Contains("Key 3 0.5 1 0.5 0 0 -12 36 0", text);
        Assert.Contains("Key 1 1 4 0 0 0 0 0 0", text);
        Assert.Contains("Key 0 0 0 0 0 0 0 6 0", text);
        Assert.Contains("Key 2 1.5 3 0 0 0 0 0 0", text);
        var (read, readWarnings) = LwsReader.Read(text, _ => null);
        Assert.Empty(readWarnings);
        var rx = read.Find("Ball")![Channel.PositionX];
        Assert.Equal(x.Keys.Select(k => (k.Frame, k.Value, k.Kind)), rx.Keys.Select(k => (k.Frame, k.Value, k.Kind)));
        for (int i = 0; i < x.Keys.Count; i++)
        {
            AssertNear(x.Keys[i].InSlope, rx.Keys[i].InSlope, 1e-12);
            AssertNear(x.Keys[i].OutSlope, rx.Keys[i].OutSlope, 1e-12);
        }
        foreach (var f in new[] { 3.0, 12, 18, 30 }) AssertNear(x.Evaluate(f), rx.Evaluate(f), 1e-9);
    }

    [Fact]
    public void Lws_hermite_and_bezier_keys_read_with_their_tangents_and_2d_bezier_warns()
    {
        const string text = """
            LWSC
            3

            FirstFrame 0
            LastFrame 48
            FramesPerSecond 24

            AddNullObject 10000000 Ball
            ObjectMotion
            NumChannels 9
            Channel 0
            { Envelope
              4
              Key 0 0 0 0 0 0 0 24 0
              Key 2 0.5 2 0 0 0 -12 0 0
              Key 1 1 1 0 0 0 0 0 0
              Key 3 2 5 0 0 0 0 0 0
              Behaviors 1 1
            }
            """;
        var (scene, warnings) = LwsReader.Read(text, _ => null);
        Assert.Contains(warnings, w => w.Contains("2D Bézier"));
        var x = scene.Find("Ball")![Channel.PositionX];
        Assert.Equal([SpanKind.Tcb, SpanKind.Bezier, SpanKind.Bezier, SpanKind.Tcb], x.Keys.Select(k => k.Kind));
        AssertNear(1, x.Keys[0].OutSlope);
        AssertNear(-0.5, x.Keys[1].InSlope);
        AssertNear(1, Slope(x, 0, after: true), 1e-4);
    }
}
