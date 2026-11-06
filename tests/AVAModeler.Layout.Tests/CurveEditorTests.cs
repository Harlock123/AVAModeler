using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>
/// The graph editor's logic: every channel as a curve, showing and choosing curves, choosing keys across them, dragging,
/// nudging, setting numbers, spans and behaviours, copy and paste (onto another channel too), delete and scale, each an
/// undo step; and keying, retiming and the dope sheet keeping spans and slopes.
/// </summary>
public class CurveEditorTests
{
    /// <summary>
    /// A session with a null keyed on position.x at 1, 10 and 20 (10 with tension 0.5), position.y at 1 and 20 and heading
    /// at 10 and 30, and a light whose intensity is keyed, over frames 1 to 60; the null is chosen.
    /// </summary>
    private static (LayoutSession Session, CurveEditor Editor, NullItem Ball) Keyed()
    {
        var s = new LayoutSession();
        var n = s.Scene.AddNull("Ball");
        s.Replace(s.Scene);
        var x = n[Channel.PositionX];
        Keying.Key(x, s.Scene, new EnvelopeKey(1, 0), keepShape: false);
        x.AddKey(new EnvelopeKey(10, 5, Tension: 0.5));
        x.AddKey(new EnvelopeKey(20, 2));
        Keying.Key(n[Channel.PositionY], s.Scene, new EnvelopeKey(1, 1), keepShape: false);
        n[Channel.PositionY].AddKey(new EnvelopeKey(20, 3));
        Keying.Key(n[Channel.Heading], s.Scene, new EnvelopeKey(10, 90), keepShape: false);
        n[Channel.Heading].AddKey(new EnvelopeKey(30, 180, Linear: true));
        var light = s.Scene.Lights.First();
        Keying.Key(light.Intensity, s.Scene, new EnvelopeKey(1, 1), keepShape: false);
        light.Intensity.AddKey(new EnvelopeKey(30, 0.2));
        s.Replace(s.Scene);
        s.Select(n);
        return (s, new CurveEditor(s), n);
    }

    private static double[] Frames(Envelope e) => [.. e.Keys.Select(k => k.Frame)];

    [Fact]
    public void Every_channel_is_a_curve_and_curves_are_shown_alone_together_or_all_keyed()
    {
        var (s, ed, n) = Keyed();
        Assert.Equal(SceneChannels.Of(n).Select(c => c.Name), ed.Channels().Select(c => c.Name));
        Assert.Equal(CurveEditor.DefaultShown, ed.Shown().Select(c => c.Name));
        ed.ClickChannel("heading", add: false);
        Assert.Equal(["heading"], ed.Shown().Select(c => c.Name));
        ed.ClickChannel("position.y", add: true);
        Assert.Equal(["position.y", "heading"], ed.Shown().Select(c => c.Name));
        ed.ClickChannel("position.y", add: true);
        Assert.Equal(["heading"], ed.Shown().Select(c => c.Name));
        Assert.Equal(3, ed.ShowKeyed());
        Assert.Equal(["position.x", "position.y", "heading"], ed.Shown().Select(c => c.Name));
        ed.ShowAll();
        Assert.Equal(9, ed.Shown().Count);

        // A light's intensity, an object's morphs and a followed path's position are curves too.
        var light = s.Scene.Lights.First();
        s.Select(light);
        Assert.Contains("intensity", ed.Channels().Select(c => c.Name));
        ed.ShowKeyed();
        Assert.Equal(["intensity"], ed.Shown().Select(c => c.Name));
        var other = s.Scene.AddNull("Other");
        s.Select(other);
        // Curves the item hasn't leave its position shown.
        Assert.Equal(CurveEditor.DefaultShown, ed.Shown().Select(c => c.Name));
        s.Edit("Follow", () => n.Path = new FollowPath { Points = [other, s.Scene.Cameras.First()] });
        s.Select(n);
        Assert.Contains(SceneChannels.PathPosition, ed.Channels().Select(c => c.Name));
    }

    [Fact]
    public void Keys_are_chosen_across_curves_by_click_shift_click_and_box()
    {
        var (_, ed, n) = Keyed();
        ed.Click(ed.Key("position.x", 10), toggle: false);
        ed.Click(ed.Key("position.y", 20), toggle: true);
        Assert.Equal(2, ed.Selected.Count);
        Assert.Equal(["position.x", "position.y"], ed.ChosenKeys().Select(k => k.Channel));
        ed.Click(ed.Key("position.x", 10), toggle: true);
        Assert.Equal([new KeyId(n, "position.y", 20)], ed.Selected);
        // A box of frames 0 to 15 and values 0.5 to 6 takes x's key at 10 and y's at 1 (x's at 1 is at 0).
        ed.Box(0, 15, 6, 0.5, add: false);
        Assert.Equal([("position.x", 10.0), ("position.y", 1.0)], ed.ChosenKeys().Select(k => (k.Channel, k.Key.Frame)));
        ed.SelectAll();
        Assert.Equal(5, ed.Selected.Count);
        // Hiding a curve, or choosing another item, lets go of its keys.
        ed.ShowOnly("position.y");
        Assert.All(ed.Selected, k => Assert.Equal("position.y", k.Channel));
        ed.Session.Select(ed.Session.Scene.Cameras.First());
        ed.Prune();
        Assert.Empty(ed.Selected);
    }

    [Fact]
    public void Setting_a_number_on_several_keys_is_one_undo_step()
    {
        var (s, ed, n) = Keyed();
        ed.Choose([ed.Key("position.x", 10), ed.Key("position.y", 20)]);
        int depth = s.History.Count;
        Assert.Equal(2, ed.Set(KeyField.Tension, 2));
        Assert.Equal(depth + 1, s.History.Count);
        Assert.Equal(1, n[Channel.PositionX].Keys[1].Tension);
        Assert.Equal(1, n[Channel.PositionY].Keys[1].Tension);
        Assert.Equal(1, ed.Common(KeyField.Tension));
        Assert.Null(ed.Common(KeyField.Value));
        Assert.Equal(0, ed.Set(KeyField.Tension, 1));
        ed.Set(KeyField.Continuity, -0.5);
        ed.Set(KeyField.Value, 4);
        Assert.Equal(4, n[Channel.PositionY].Keys[1].Value);
        Assert.Equal("Set Key Value", s.History.UndoLabel);
        s.Undo();
        s.Undo();
        s.Undo();
        Assert.Equal(0.5, n[Channel.PositionX].Keys[1].Tension);
        Assert.Equal(0, n[Channel.PositionY].Keys[1].Tension);
        Assert.Throws<ArgumentException>(() => ed.Set(KeyField.Bias, double.NaN));

        // Frames: one key per curve can go to a frame; two on one curve can't.
        ed.Choose([ed.Key("position.x", 10), ed.Key("position.y", 20)]);
        ed.Set(KeyField.Frame, 12);
        Assert.Equal([1.0, 12, 20], Frames(n[Channel.PositionX]));
        Assert.Equal([1.0, 12], Frames(n[Channel.PositionY]));
        Assert.Equal([12.0, 12], ed.Selected.Select(k => k.Frame));
        ed.Choose([ed.Key("position.x", 12), ed.Key("position.x", 20)]);
        Assert.Throws<ArgumentException>(() => ed.Set(KeyField.Frame, 5));
    }

    [Fact]
    public void Spans_and_slopes_are_set_on_the_chosen_keys_and_bezier_keeps_the_curve()
    {
        var (s, ed, n) = Keyed();
        var x = n[Channel.PositionX];
        var before = Enumerable.Range(0, 41).Select(f => x.Evaluate(f * 0.5)).ToArray();
        ed.Choose([ed.Key("position.x", 10), ed.Key("position.x", 20)]);
        Assert.Equal(SpanKind.Tcb, ed.CommonSpan);
        Assert.Equal(2, ed.SetSpan(SpanKind.Bezier));
        Assert.Equal("Set Key Span", s.History.UndoLabel);
        Assert.Equal([SpanKind.Tcb, SpanKind.Bezier, SpanKind.Bezier], x.Keys.Select(k => k.Kind));
        // The slopes filled in keep the curve as it was.
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], x.Evaluate(i * 0.5), 9);
        // Then the slopes change it.
        ed.Choose([ed.Key("position.x", 10)]);
        ed.Set(KeyField.InSlope, 0);
        ed.Set(KeyField.OutSlope, 0);
        Assert.Equal((0.0, 0.0), x.Slopes(1));
        ed.SetSpan(SpanKind.Stepped);
        Assert.Equal(0, x.Evaluate(9));
        s.Undo();
        s.Undo();
        s.Undo();
        s.Undo();
        Assert.Equal([SpanKind.Tcb, SpanKind.Tcb, SpanKind.Tcb], x.Keys.Select(k => k.Kind));
        Assert.True(x.Keys.All(k => k.InSlope == 0 && k.OutSlope == 0));
    }

    [Fact]
    public void Behaviours_go_on_the_curves_with_chosen_keys_or_every_shown_curve()
    {
        var (s, ed, n) = Keyed();
        Assert.Equal(3, ed.SetBehavior(EndBehavior.Repeat, null));
        Assert.Equal(EndBehavior.Repeat, n[Channel.PositionZ].PreBehavior);
        Assert.Equal("Pre Behavior", s.History.UndoLabel);
        ed.Click(ed.Key("position.y", 20), toggle: false);
        Assert.Equal(1, ed.SetBehavior(null, EndBehavior.Oscillate));
        Assert.Equal(EndBehavior.Oscillate, n[Channel.PositionY].PostBehavior);
        Assert.Equal(EndBehavior.Constant, n[Channel.PositionX].PostBehavior);
        Assert.Equal(0, ed.SetBehavior(null, EndBehavior.Oscillate));
    }

    [Fact]
    public void Dragging_and_nudging_keys_are_one_undo_step_each()
    {
        var (s, ed, n) = Keyed();
        var x = n[Channel.PositionX];
        var y = n[Channel.PositionY];
        ed.Choose([ed.Key("position.x", 10), ed.Key("position.y", 20)]);
        int depth = s.History.Count;
        Assert.True(ed.BeginDrag());
        ed.DragBy(0, 0.5);
        ed.DragBy(0, 1.5);
        ed.DragBy(3.4, 1.5);
        ed.EndDrag();
        Assert.Equal(depth + 1, s.History.Count);
        Assert.Equal("Drag Keys", s.History.UndoLabel);
        Assert.Equal([1.0, 13, 20], Frames(x));
        Assert.Equal(6.5, x.Keys[1].Value);
        Assert.Equal(0.5, x.Keys[1].Tension);
        Assert.Equal([1.0, 23], Frames(y));
        Assert.Equal(4.5, y.Keys[1].Value);
        Assert.Equal([13.0, 23], ed.Selected.Select(k => k.Frame).Order());
        s.Undo();
        Assert.Equal([1.0, 10, 20], Frames(x));
        Assert.Equal(5, x.Keys[1].Value);

        // Arrow keys' nudges.
        ed.Choose([ed.Key("position.x", 10)]);
        Assert.Equal(1, ed.Nudge(1, 0));
        Assert.Equal("Nudge Keys", s.History.UndoLabel);
        Assert.Equal([1.0, 11, 20], Frames(x));
        ed.Nudge(0, -0.25);
        Assert.Equal(4.75, x.Keys[1].Value);
        Assert.Equal(depth + 2, s.History.Count);
    }

    [Fact]
    public void Copy_and_paste_onto_the_same_channels_or_one_curve_onto_another()
    {
        var (s, ed, n) = Keyed();
        var x = n[Channel.PositionX];
        x.SetKey(1, x.Keys[1].WithSpan(SpanKind.Bezier) with { InSlope = 0.3, OutSlope = -0.2 });
        ed.Choose([ed.Key("position.x", 10), ed.Key("position.x", 20)]);
        Assert.Equal(2, ed.Copy());
        Assert.True(ed.CopiedOneCurve);
        // Several curves shown: back onto position.x.
        Assert.Equal(2, ed.Paste(40));
        Assert.Equal([1.0, 10, 20, 40, 50], Frames(x));
        Assert.Equal(x.Keys[1] with { Frame = 40 }, x.Keys[3]);
        Assert.Equal("Paste Keys", s.History.UndoLabel);
        s.Undo();
        // One curve shown: onto it ("copy a curve to another channel").
        ed.ShowOnly("scale.y");
        Assert.Equal(2, ed.Paste(5));
        var sy = n[Channel.ScaleY];
        Assert.Equal([5.0, 15], Frames(sy));
        Assert.Equal(SpanKind.Bezier, sy.Keys[0].Kind);
        Assert.Equal(0.3, sy.Keys[0].InSlope);
        Assert.Equal([new KeyId(n, "scale.y", 5), new KeyId(n, "scale.y", 15)], ed.Selected.OrderBy(k => k.Frame));
        s.Undo();
        // Onto another item's channel of the same name.
        ed.ShowAll();
        var other = s.Scene.AddNull("Other");
        s.Select(other);
        Assert.Equal(2, ed.Paste(1));
        Assert.Equal([1.0, 11], Frames(other[Channel.PositionX]));
    }

    [Fact]
    public void Delete_keeps_a_curves_last_key_and_scale_spaces_keys_and_divides_slopes()
    {
        var (s, ed, n) = Keyed();
        var y = n[Channel.PositionY];
        ed.Choose([ed.Key("position.y", 1), ed.Key("position.y", 20)]);
        Assert.Equal(1, ed.Delete());
        Assert.Single(y.Keys);
        s.Undo();

        var x = n[Channel.PositionX];
        x.SetKey(2, x.Keys[2].WithSpan(SpanKind.Bezier) with { InSlope = 0.4, OutSlope = 0.2 });
        ed.Choose([ed.Key("position.x", 10), ed.Key("position.x", 20)]);
        Assert.Equal(1, ed.Scale(2));
        Assert.Equal("Scale Keys", s.History.UndoLabel);
        Assert.Equal([1.0, 10, 30], Frames(x));
        Assert.Equal((0.2, 0.1), (x.Keys[2].InSlope, x.Keys[2].OutSlope));
        Assert.Equal([10.0, 30], ed.Selected.Select(k => k.Frame).Order());
    }

    [Fact]
    public void Keying_shifting_and_the_dope_sheet_keep_spans_and_slopes()
    {
        var (s, ed, n) = Keyed();
        var x = n[Channel.PositionX];
        var bez = x.Keys[1].WithSpan(SpanKind.Bezier) with { InSlope = 0.3, OutSlope = -0.2 };
        x.SetKey(1, bez);
        // Keying a new value keeps the shape.
        Keying.Set(x, s.Scene, 10, 7, key: true, "x");
        Assert.Equal(bez with { Value = 7 }, x.Keys[1]);
        Keying.Set(x, s.Scene, 10, 6, key: false, "x");
        Assert.Equal(bez with { Value = 6 }, x.Keys[1]);
        // Shifting keeps slopes; scaling divides them.
        KeyTiming.Shift(s, [new KeyId(n, "position.x", 10)], 2);
        Assert.Equal(bez with { Value = 6, Frame = 12 }, x.Keys[1]);
        KeyTiming.Scale(s, [new KeyId(n, "position.x", 12)], 0.5, 2);
        Assert.Equal((0.6, -0.4), (x.Keys[1].InSlope, x.Keys[1].OutSlope));
        Assert.Equal(7, x.Keys[1].Frame);
        // The dope sheet's copy and paste keep them.
        var sheet = new DopeSheet(s);
        sheet.Click(new KeyId(n, "position.x", 7), toggle: false);
        sheet.Copy();
        sheet.Paste(40);
        Assert.Equal(x.Keys[1] with { Frame = 40 }, x.Keys[^1]);
    }

    [Fact]
    public void Set_span_to_bezier_fills_the_curves_slopes_and_keeps_given_ones()
    {
        var e = new Envelope();
        e.AddKey(new EnvelopeKey(0, 0));
        e.AddKey(new EnvelopeKey(10, 10, 0.2, 0.3));
        e.AddKey(new EnvelopeKey(20, 0));
        e.AddKey(new EnvelopeKey(30, 5));
        var before = Enumerable.Range(0, 61).Select(f => e.Evaluate(f * 0.5)).ToArray();
        var slopes = e.Slopes(2);
        Keying.SetSpan(e, 2, SpanKind.Bezier);
        Assert.Equal(slopes, (e.Keys[2].InSlope, e.Keys[2].OutSlope));
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], e.Evaluate(i * 0.5), 9);
        Keying.SetSpan(e, 2, SpanKind.Bezier, inSlope: 0);
        Assert.Equal(0, e.Keys[2].InSlope);
        Assert.Equal(slopes.Out, e.Keys[2].OutSlope);
        Keying.SetSpan(e, 2, SpanKind.Linear);
        Assert.True(e.Keys[2].Linear);
        Assert.Throws<ArgumentException>(() => Keying.SetSpan(e, 2, SpanKind.Bezier, outSlope: double.PositiveInfinity));
    }
}
