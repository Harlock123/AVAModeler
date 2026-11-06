using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>Motion modifiers and motion files in Layout's session: each change one undo step, bad ones none; glTF export sees modifiers.</summary>
public class MotionModifierLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avamodeler-motion-" + Guid.NewGuid().ToString("N")[..8]);

    public MotionModifierLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static (LayoutSession Session, NullItem Leader, NullItem Follower) Pair()
    {
        var s = new LayoutSession();
        var leader = s.Scene.AddNull("Leader");
        leader[Channel.PositionX].Clear();
        leader[Channel.PositionX].AddKey(new EnvelopeKey(0, 0, Linear: true));
        leader[Channel.PositionX].AddKey(new EnvelopeKey(10, 10, Linear: true));
        var follower = s.Scene.AddNull("Follower");
        s.Replace(s.Scene);
        return (s, leader, follower);
    }

    [Fact]
    public void Setting_modifiers_is_one_undo_step_and_a_bad_one_changes_nothing()
    {
        var (s, leader, f) = Pair();
        s.SetModifiers(f, [new LagModifier(leader) { Delay = 2 }, new JitterModifier()]);
        Assert.Equal("Set Follower's Motion Modifiers", s.History.UndoLabel);
        Assert.Equal(2, f.Modifiers.Count);
        Assert.True(s.Modified);
        int depth = s.History.Count;
        Assert.Contains("itself", Assert.Throws<ArgumentException>(() => s.SetModifiers(f, [new LagModifier(f)])).Message);
        Assert.Contains("delay", Assert.Throws<ArgumentException>(() => s.SetModifiers(f, [new LagModifier(leader) { Delay = -1 }])).Message);
        Assert.Equal(depth, s.History.Count);
        Assert.Equal(2, f.Modifiers.Count);
        s.SetModifiers(f, []);
        Assert.Equal("Remove Follower's Motion Modifiers", s.History.UndoLabel);
        Assert.Empty(f.Modifiers);
        s.Undo();
        Assert.Equal(2, f.Modifiers.Count);
        s.Undo();
        Assert.Empty(f.Modifiers);
        s.Redo();
        Assert.Equal(2, f.Modifiers.Count);
    }

    [Fact]
    public void Removing_the_leader_drops_the_lag_and_undo_brings_both_back()
    {
        var (s, leader, f) = Pair();
        s.SetModifiers(f, [new LagModifier(leader)]);
        s.Remove(leader);
        Assert.Empty(f.Modifiers);
        s.Undo();
        Assert.Same(leader, Assert.Single(f.Modifiers).Reference);
    }

    [Fact]
    public void Loading_a_motion_is_one_undo_step_and_a_bad_file_changes_nothing()
    {
        var (s, leader, f) = Pair();
        string path = Path.Combine(_dir, "leader.mot");
        Assert.Empty(s.SaveMotion(leader, path, version: 1));
        Assert.StartsWith("LWMO\n1\n", File.ReadAllText(path));
        var m = s.LoadMotion(f, path);
        Assert.Equal(1, m.Version);
        Assert.Equal("Load Motion onto Follower", s.History.UndoLabel);
        Assert.Equal(leader.PositionAt(7), f.PositionAt(7));
        int depth = s.History.Count;
        string bad = Path.Combine(_dir, "bad.mot");
        File.WriteAllText(bad, "LWSC\n3\n");
        Assert.Throws<InvalidDataException>(() => s.LoadMotion(f, bad));
        Assert.Equal(depth, s.History.Count);
        s.Undo();
        Assert.Equal(0, f.PositionAt(7).X);
        // Saving notes what a motion file can't hold.
        s.SetModifiers(f, [new OscillateModifier()]);
        Assert.Contains(s.SaveMotion(f, Path.Combine(_dir, "f.mot")), w => w.Contains("oscillate"));
    }

    [Fact]
    public void Gltf_export_samples_the_modified_motion()
    {
        var (s, _, f) = Pair();
        s.Scene.FirstFrame = 0;
        s.Scene.LastFrame = 20;
        s.SetModifiers(f, [new OscillateModifier { Channel = Channel.PositionY, Amplitude = 2, Period = 20, Phase = 90, Start = 0 }]);
        var (glb, _, _) = SceneGltf.Write(s.Scene, GltfPacking.Binary);
        int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength))!.AsObject();
        int node = json["nodes"]!.AsArray().Select((n, i) => (n, i)).Single(x => (string?)x.n!["name"] == "Follower").i;
        var animation = json["animations"]![0]!;
        var channel = animation["channels"]!.AsArray().Single(c => (int)c!["target"]!["node"]! == node && (string?)c["target"]!["path"] == "translation")!;
        var sampler = animation["samplers"]![(int)channel["sampler"]!]!;
        var accessor = json["accessors"]![(int)sampler["output"]!]!;
        var view = json["bufferViews"]![(int)accessor["bufferView"]!]!;
        int bin = 20 + jsonLength + 8 + (int)view["byteOffset"]!;
        float Y(int frame) => BinaryPrimitives.ReadSingleLittleEndian(glb.AsSpan(bin + (frame * 3 + 1) * 4));
        // Phase 90: the top of the swing at frames 0 and 20, the bottom at 10.
        Assert.Equal(2, Y(0), 5);
        Assert.Equal(-2, Y(10), 5);
        Assert.Equal(2, Y(20), 5);
    }
}
