using AVAModeler.Core;

namespace AVAModeler.Scene.Tests;

/// <summary>LightWave motion files (.mot), versions 1 and 3: reading, writing, round trips, malformed files and applying to an item.</summary>
public class MotionFileTests
{
    /// <summary>A motion as LightWave 5's Save Motion writes it: nine channels keyed together at three frames, a repeat after the last.</summary>
    private const string LightWave5 = """
        LWMO
        1
        9
        3
        0 0 0 0 0 0 1 1 1
        0 0 0 0 0
        1 2 3 90 0 0 1 1 1
        15 0 0.5 0 0
        2 0 0 180 -10 5 2 2 2
        30 1 0 0 0
        EndBehavior 2
        """;

    private static void SameKeys(Envelope expected, Envelope actual)
    {
        Assert.Equal(expected.Keys.Count, actual.Keys.Count);
        for (int i = 0; i < expected.Keys.Count; i++) Assert.Equal(expected.Keys[i], actual.Keys[i]);
        Assert.Equal(expected.PreBehavior, actual.PreBehavior);
        Assert.Equal(expected.PostBehavior, actual.PostBehavior);
    }

    /// <summary>An item with channels keyed at different frames, with shapes, linear spans and behaviours.</summary>
    private static NullItem Busy()
    {
        var n = new Scene().AddNull("Busy");
        n[Channel.PositionX].Clear();
        n[Channel.PositionX].AddKey(new EnvelopeKey(0, 0));
        n[Channel.PositionX].AddKey(new EnvelopeKey(12, 3.25, Tension: 0.5, Continuity: -0.25, Bias: 0.75));
        n[Channel.PositionX].AddKey(new EnvelopeKey(24, -1, Linear: true));
        n[Channel.PositionX].PreBehavior = EndBehavior.Oscillate;
        n[Channel.PositionX].PostBehavior = EndBehavior.OffsetRepeat;
        n[Channel.Heading].Clear();
        n[Channel.Heading].AddKey(new EnvelopeKey(5, 10));
        n[Channel.Heading].AddKey(new EnvelopeKey(12, 100));
        n[Channel.Heading].AddKey(new EnvelopeKey(17, 370));
        n[Channel.ScaleY].SetConstant(2.5);
        return n;
    }

    [Fact]
    public void Reads_a_LightWave_5_motion()
    {
        var m = MotionFile.Read(LightWave5);
        Assert.Equal(1, m.Version);
        Assert.Empty(m.Warnings);
        Assert.Equal(9, m.Channels.Count);
        var x = m.Channels[(int)Channel.PositionX];
        Assert.Equal([new EnvelopeKey(0, 0), new EnvelopeKey(15, 1, Tension: 0.5), new EnvelopeKey(30, 2, Linear: true)], x.Keys);
        Assert.Equal(EndBehavior.Repeat, x.PostBehavior);
        Assert.Equal(180, m.Channels[(int)Channel.Heading].Evaluate(30));
        Assert.Equal(-10, m.Channels[(int)Channel.Pitch].Evaluate(30));
        Assert.Equal(2, m.Channels[(int)Channel.ScaleZ].Evaluate(30));
        // Repeat: frame 45 is frame 15 again.
        Assert.Equal(1, x.Evaluate(45), 9);
    }

    [Fact]
    public void Version_3_round_trips_exactly_and_reads_times_in_seconds()
    {
        var item = Busy();
        var text = MotionFile.Write(item, 24, version: 3);
        Assert.StartsWith("LWMO\n3\n", text);
        Assert.Contains("NumChannels 9", text);
        // Frame 12 at 24 fps is half a second.
        Assert.Contains("Key 3.25 0.5 0 0.5 -0.25 0.75", text);
        var back = MotionFile.Read(text, 24);
        Assert.Equal(3, back.Version);
        Assert.Empty(back.Warnings);
        for (int c = 0; c < 9; c++) SameKeys(item.Channels[c], back.Channels[c]);
        Assert.Equal(3, MotionFile.DefaultVersion);
        Assert.Equal(text, MotionFile.Write(item, 24));
    }

    [Fact]
    public void Version_1_round_trips_a_motion_keyed_together()
    {
        var m = MotionFile.Read(LightWave5);
        var text = MotionFile.Write(m.Channels, 30, version: 1);
        var back = MotionFile.Read(text);
        for (int c = 0; c < 9; c++) SameKeys(m.Channels[c], back.Channels[c]);
        Assert.EndsWith("EndBehavior 2\n", text);
        // And it is the same text LightWave 5 wrote, numbers aside.
        Assert.Equal(LightWave5.Replace("\r", "").Split('\n').Length, text.TrimEnd('\n').Split('\n').Length);
    }

    [Fact]
    public void Version_1_keys_every_channel_at_every_key_frame_and_says_what_it_changed()
    {
        var item = Busy();
        var warnings = new List<string>();
        var back = MotionFile.Read(MotionFile.Write(item, 30, version: 1, warnings));
        // Every channel keyed at the union of frames: 0, 5, 12, 17, 24.
        Assert.All(back.Channels, e => Assert.Equal([0.0, 5, 12, 17, 24], e.Keys.Select(k => k.Frame)));
        // Values at the key frames are each channel's own.
        foreach (var f in new[] { 0.0, 5, 12, 17, 24 })
            for (int c = 0; c < 9; c++)
                Assert.Equal(item.Channels[c].Evaluate(f), back.Channels[c].Evaluate(f), 9);
        Assert.Contains(warnings, w => w.Contains("same frames"));
        Assert.Contains(warnings, w => w.Contains("before the first key"));
        Assert.Contains(warnings, w => w.Contains("after the last key"));
        Assert.Contains(warnings, w => w.Contains("tension"));
    }

    [Fact]
    public void An_unanimated_item_writes_one_key()
    {
        var n = new Scene().AddNull("Still");
        n.SetPosition(0, new Vec3(1, 2, 3));
        var back = MotionFile.Read(MotionFile.Write(n, 30, version: 1));
        Assert.All(back.Channels, e => Assert.Single(e.Keys));
        Assert.Equal(new Vec3(1, 2, 3), new Vec3(back.Channels[0].Evaluate(9), back.Channels[1].Evaluate(9), back.Channels[2].Evaluate(9)));
        Assert.Equal(1, back.Channels[8].Evaluate(9));
    }

    [Theory]
    [InlineData("")]
    [InlineData("LWSC\n3\n")]
    [InlineData("LWMO\n")]
    [InlineData("LWMO\nthree\n")]
    [InlineData("LWMO\n2\n9\n1\n0 0 0 0 0 0 1 1 1\n0 0 0 0 0\n")]
    [InlineData("LWMO\n1\n")]
    [InlineData("LWMO\n1\n9\n2\n0 0 0 0 0 0 1 1 1\n0 0 0 0 0\n1 1 1 0 0 0 1 1 1\n")]
    [InlineData("LWMO\n1\n12\n1\n0 0 0 0 0 0 1 1 1 0 0 0\n0 0 0 0 0\n")]
    [InlineData("LWMO\n1\n9\n0\n")]
    [InlineData("LWMO\n3\nChannel 0\n{ Envelope\n 1\n Key 0 0 0 0 0 0 0 0 0\n}\n")]
    [InlineData("LWMO\n3\nNumChannels 9\nChannel 0\nKey 0 0 0 0 0 0 0 0 0\n")]
    [InlineData("LWMO\n3\nNumChannels 9\n")]
    public void Malformed_files_are_refused(string text) =>
        Assert.Throws<InvalidDataException>(() => MotionFile.Read(text));

    [Fact]
    public void Channels_a_file_leaves_out_stay_at_rest_with_a_warning()
    {
        var m = MotionFile.Read("LWMO\n3\nNumChannels 1\nChannel 1\n{ Envelope\n  1\n  Key 4 0 0 0 0 0 0 0 0\n  Behaviors 1 1\n}\nChannel 12\n{ Envelope\n  0\n}\n");
        Assert.Equal(4, m.Channels[1].Evaluate(0));
        Assert.Equal(0, m.Channels[0].Evaluate(0));
        Assert.Equal(1, m.Channels[7].Evaluate(0));
        Assert.Contains(m.Warnings, w => w.Contains("PositionX") && w.Contains("at rest"));
        Assert.Contains(m.Warnings, w => w.Contains("channel 12"));
    }

    [Fact]
    public void A_motion_saved_from_one_item_applies_to_another()
    {
        var source = Busy();
        var scene = new Scene();
        var target = scene.AddNull("Target");
        target.SetPosition(0, new Vec3(9, 9, 9));
        int version = scene.Version;
        int changes = 0;
        scene.Changed += () => changes++;
        var m = MotionFile.Read(MotionFile.Write(source, 30), 30);
        MotionFile.Apply(target, m.Channels);
        for (int c = 0; c < 9; c++) SameKeys(source.Channels[c], target.Channels[c]);
        Assert.True(scene.Version > version);
        Assert.Equal(1, changes);
        // The file's envelopes aren't shared with the item.
        m.Channels[0].SetConstant(100);
        Assert.NotEqual(100, target.PositionAt(0).X);
        Assert.Throws<ArgumentException>(() => MotionFile.Apply(target, m.Channels.Take(3).ToList()));
    }

    [Fact]
    public void Writing_says_paths_and_modifiers_are_left_out_and_refuses_other_versions()
    {
        var scene = new Scene();
        var a = scene.AddNull("A");
        var b = scene.AddNull("B");
        var n = scene.AddNull("N");
        n.Path = new FollowPath { Points = [a, b] };
        n.Modifiers = [new JitterModifier()];
        var warnings = new List<string>();
        MotionFile.Write(n, 30, warnings: warnings);
        Assert.Contains(warnings, w => w.Contains("path"));
        Assert.Contains(warnings, w => w.Contains("jitter"));
        Assert.Throws<ArgumentException>(() => MotionFile.Write(n, 30, version: 2));
    }
}
