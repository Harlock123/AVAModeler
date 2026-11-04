using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>The dope sheet's logic: rows, choosing keys, and moving, scaling, copying, pasting and deleting them as undo steps.</summary>
public class DopeSheetTests
{
    /// <summary>
    /// A session with a null keyed on position.x at 1, 10 and 20 (frame 10 with tension 0.5) and on heading at 10 and
    /// 30, over frames 1 to 60; the camera and light are never keyed.
    /// </summary>
    private static (LayoutSession Session, DopeSheet Sheet, NullItem Null) Keyed()
    {
        var s = new LayoutSession();
        var n = s.Scene.AddNull("Ball");
        s.Replace(s.Scene);
        var x = n[Channel.PositionX];
        Keying.Key(x, s.Scene, new EnvelopeKey(1, 0), keepShape: false);
        x.AddKey(new EnvelopeKey(10, 5, Tension: 0.5));
        x.AddKey(new EnvelopeKey(20, 2));
        Keying.Key(n[Channel.Heading], s.Scene, new EnvelopeKey(10, 90), keepShape: false);
        n[Channel.Heading].AddKey(new EnvelopeKey(30, 180, Linear: true));
        s.Replace(s.Scene);
        return (s, new DopeSheet(s), n);
    }

    private static double[] Frames(Envelope e) => [.. e.Keys.Select(k => k.Frame)];

    [Fact]
    public void Rows_are_the_items_and_with_channels_their_keyed_channels()
    {
        var (s, sheet, n) = Keyed();
        var rows = sheet.Rows();
        Assert.Equal(["Camera: Camera", "Light: Light", "Null: Ball"], rows.Select(r => r.Label));
        Assert.Empty(sheet.KeysOf(rows[0]));
        Assert.Equal([1.0, 10, 20, 30], sheet.KeysOf(rows[2]));
        sheet.ShowChannels = true;
        rows = sheet.Rows();
        Assert.Equal(["Camera: Camera", "Light: Light", "Null: Ball", "  position.x", "  heading"], rows.Select(r => r.Label));
        Assert.Equal([10.0, 30], sheet.KeysOf(rows[4]));
        Assert.Same(n, rows[4].Item);
        Assert.Same(s.Scene, n.Scene);
    }

    [Fact]
    public void Keys_are_chosen_by_click_shift_click_and_box()
    {
        var (_, sheet, n) = Keyed();
        sheet.ShowChannels = true;
        var row = sheet.Rows()[2];
        sheet.Click(DopeSheet.Key(row, 10), toggle: false);
        Assert.Equal([new KeyId(n, null, 10)], sheet.Selected);
        // An item's key stands for its channels' keys at that frame.
        Assert.True(sheet.IsSelected(new KeyId(n, "heading", 10)));
        sheet.Click(DopeSheet.Key(row, 20), toggle: true);
        Assert.Equal(2, sheet.Selected.Count);
        sheet.Click(DopeSheet.Key(row, 10), toggle: true);
        Assert.Equal([new KeyId(n, null, 20)], sheet.Selected);
        sheet.Click(DopeSheet.Key(row, 1), toggle: false);
        Assert.Single(sheet.Selected);
        // A box over the channel rows from frame 5 to 25.
        sheet.Box(3, 4, 5, 25, add: false);
        Assert.Equal([new KeyId(n, "position.x", 10), new KeyId(n, "position.x", 20), new KeyId(n, "heading", 10)], sheet.Selected.OrderBy(k => k.Channel == "heading").ThenBy(k => k.Frame));
        sheet.Box(2, 2, 25, 40, add: true);
        Assert.Equal(4, sheet.Selected.Count);
        sheet.SelectAll();
        Assert.Equal(4 + 2 + 3, sheet.Selected.Count);
        sheet.ClearSelection();
        Assert.Empty(sheet.Selected);
    }

    [Fact]
    public void Moving_keys_is_one_undo_step_snapped_to_frames_keeping_their_shape()
    {
        var (s, sheet, n) = Keyed();
        var row = sheet.Rows()[2];
        sheet.Click(DopeSheet.Key(row, 1), false);
        sheet.Click(DopeSheet.Key(row, 10), true);
        sheet.Click(DopeSheet.Key(row, 20), true);
        int depth = s.History.Count;
        Assert.Equal(4, sheet.Move(4.6));
        Assert.Equal(depth + 1, s.History.Count);
        Assert.Equal("Move Keys", s.History.UndoLabel);
        // Snapped to 5 frames; every channel's keys at those frames moved together.
        Assert.Equal([6.0, 15, 25], Frames(n[Channel.PositionX]));
        Assert.Equal([15.0, 30], Frames(n[Channel.Heading]));
        Assert.Equal(new EnvelopeKey(15, 5, Tension: 0.5), n[Channel.PositionX].Keys[1]);
        Assert.Equal([6.0, 15, 25], sheet.Selected.Select(k => k.Frame).Order());
        // The camera's constant (never keyed) isn't touched.
        Assert.Equal([0.0], Frames(s.Scene.CurrentCamera![Channel.PositionZ]));
        s.Undo();
        Assert.Equal([1.0, 10, 20], Frames(n[Channel.PositionX]));
        Assert.Equal([10.0, 30], Frames(n[Channel.Heading]));
        sheet.Prune();
        Assert.Empty(sheet.Selected);
    }

    [Fact]
    public void A_moved_key_replaces_one_where_it_lands()
    {
        var (s, sheet, n) = Keyed();
        sheet.ShowChannels = true;
        sheet.Click(new KeyId(n, "heading", 10), false);
        sheet.Move(20);
        Assert.Equal([30.0], Frames(n[Channel.Heading]));
        Assert.Equal(90, n[Channel.Heading].Keys[0].Value);
        Assert.Contains("1 key(s) they landed on were replaced", s.Status);
        // Moving by nothing changes nothing and leaves no step.
        int depth = s.History.Count;
        Assert.Equal(0, sheet.Move(0.2));
        Assert.Equal(depth, s.History.Count);
    }

    [Fact]
    public void Scaling_keys_stretches_their_timing_about_the_earliest()
    {
        var (s, sheet, n) = Keyed();
        sheet.ShowChannels = true;
        sheet.Box(3, 3, 1, 60, add: false);
        Assert.Equal(2, sheet.Scale(2));
        Assert.Equal([1.0, 19, 39], Frames(n[Channel.PositionX]));
        Assert.Equal("Scale Keys", s.History.UndoLabel);
        Assert.Equal([1.0, 19, 39], sheet.Selected.Select(k => k.Frame).Order());
        s.Undo();
        // Squeezed hard, two land on one frame: the later one wins.
        sheet.Box(3, 3, 1, 60, add: false);
        sheet.Scale(0.06);
        Assert.Equal([1.0, 2], Frames(n[Channel.PositionX]));
        Assert.Equal(2, n[Channel.PositionX].Keys[1].Value);
        Assert.Throws<ArgumentException>(() => sheet.Scale(0));
        Assert.Throws<ArgumentException>(() => sheet.Scale(-1));
        // A pivot of its own.
        s.Undo();
        sheet.Box(3, 3, 1, 60, add: false);
        sheet.Scale(0.5, pivot: 21);
        Assert.Equal([11.0, 16, 21], Frames(n[Channel.PositionX]));
    }

    [Fact]
    public void Copied_keys_paste_at_the_current_frame_and_undo()
    {
        var (s, sheet, n) = Keyed();
        var row = sheet.Rows()[2];
        sheet.Click(DopeSheet.Key(row, 10), false);
        sheet.Click(DopeSheet.Key(row, 20), true);
        Assert.Equal(3, sheet.Copy());
        Assert.True(sheet.CanPaste);
        s.SetFrame(41);
        Assert.Equal(3, sheet.Paste(s.Frame));
        Assert.Equal("Paste Keys", s.History.UndoLabel);
        Assert.Equal([1.0, 10, 20, 41, 51], Frames(n[Channel.PositionX]));
        Assert.Equal(new EnvelopeKey(41, 5, Tension: 0.5), n[Channel.PositionX].Keys[3]);
        Assert.Equal([10.0, 30, 41], Frames(n[Channel.Heading]));
        Assert.Equal([41.0, 41, 51], sheet.Selected.Select(k => k.Frame).Order());
        s.Undo();
        Assert.Equal([1.0, 10, 20], Frames(n[Channel.PositionX]));
        // Keys of an item that has gone aren't pasted.
        s.Remove(n);
        Assert.Equal(0, sheet.Paste(5));
    }

    [Fact]
    public void Deleting_keys_leaves_each_channels_last_key()
    {
        var (s, sheet, n) = Keyed();
        sheet.SelectAll();
        int deleted = sheet.Delete();
        // position.x keeps one of three, heading one of two.
        Assert.Equal(3, deleted);
        Assert.Single(n[Channel.PositionX].Keys);
        Assert.Single(n[Channel.Heading].Keys);
        Assert.Contains("2 kept", s.Status);
        Assert.Equal("Delete Keys", s.History.UndoLabel);
        s.Undo();
        Assert.Equal(3, n[Channel.PositionX].Keys.Count);
    }

    [Fact]
    public void Shift_and_scale_through_key_timing_take_morphs_and_lights_too()
    {
        var s = new LayoutSession();
        var light = s.Scene.Lights.First();
        Keying.Key(light.Intensity, s.Scene, new EnvelopeKey(5, 1), keepShape: false);
        light.Intensity.AddKey(15, 0.2);
        var keys = new[] { new KeyId(light, "intensity", 5), new KeyId(light, "intensity", 15) };
        var (moved, replaced) = KeyTiming.Shift(s, keys, -2);
        Assert.Equal((2, 0), (moved, replaced));
        Assert.Equal([3.0, 13], Frames(light.Intensity));
        KeyTiming.Scale(s, [new KeyId(light, null, 3), new KeyId(light, null, 13)], 3, 3);
        Assert.Equal([3.0, 33], Frames(light.Intensity));
        Assert.Throws<ArgumentException>(() => KeyTiming.Shift(s, keys, double.NaN));
    }
}
