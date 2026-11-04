using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>Exporting a Layout scene as glTF: the node hierarchy, cameras, lights and sampled motion.</summary>
public class SceneGltfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avamodeler-scenegltf-" + Guid.NewGuid().ToString("N")[..8]);

    public SceneGltfTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static Document Triangle()
    {
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        return doc;
    }

    /// <summary>A scene: a null carrying a keyed object, a turned null, the default camera and light, a spot light.</summary>
    private static Scene.Scene Sample()
    {
        var scene = Scene.Scene.CreateDefault();
        scene.FirstFrame = 1;
        scene.LastFrame = 31;
        scene.FramesPerSecond = 30;
        var rig = scene.AddNull("Rig");
        rig.SetPosition(0, new Vec3(1, 0, 0));
        var ship = scene.AddObject("Ship", Triangle());
        ship.Parent = rig;
        ship[Channel.PositionZ].Clear();
        ship[Channel.PositionZ].AddKey(1, 0);
        ship[Channel.PositionZ].AddKey(31, 4);
        var turned = scene.AddNull("Turned");
        turned.SetRotation(0, new Vec3(90, 0, 0));
        var spot = scene.AddLight("Spot", LightType.Spot);
        spot.ConeAngle = 20;
        spot.EdgeAngle = 5;
        spot.Range = 12;
        spot.Color = new Vec3(1, 0.5, 0.25);
        return scene;
    }

    private static JsonObject Json(byte[] glb)
    {
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb));
        Assert.Equal((uint)glb.Length, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)));
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        Assert.Equal(0, length % 4);
        return JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, length))!.AsObject();
    }

    private static float[] Floats(byte[] glb, JsonObject json, int accessor)
    {
        int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        int bin = 20 + jsonLength + 8;
        var a = json["accessors"]![accessor]!;
        var view = json["bufferViews"]![(int)a["bufferView"]!]!;
        int components = (string)a["type"]! switch { "SCALAR" => 1, "VEC3" => 3, _ => 4 };
        var result = new float[(int)a["count"]! * components];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(glb.AsSpan(bin + (int)view["byteOffset"]! + i * 4));
        return result;
    }

    private static JsonNode NodeNamed(JsonObject json, string name) => json["nodes"]!.AsArray().Single(n => (string?)n!["name"] == name)!;

    private static int IndexOf(JsonObject json, string name) => json["nodes"]!.AsArray().Select((n, i) => (n, i)).Single(x => (string?)x.n!["name"] == name).i;

    [Fact]
    public void Exports_the_hierarchy_cameras_and_lights()
    {
        var (glb, _, warnings) = SceneGltf.Write(Sample(), GltfPacking.Binary);
        var json = Json(glb);
        Assert.Contains(GltfBuilder.LightsExtension, json["extensionsUsed"]!.AsArray().Select(v => (string?)v));
        Assert.Empty(warnings);

        // The ship hangs from the rig; the rest are at the top of the scene.
        var roots = json["scenes"]![0]!["nodes"]!.AsArray().Select(v => (int)v!).ToList();
        Assert.DoesNotContain(IndexOf(json, "Ship"), roots);
        Assert.Contains(IndexOf(json, "Rig"), roots);
        Assert.Equal([IndexOf(json, "Ship")], NodeNamed(json, "Rig")["children"]!.AsArray().Select(v => (int)v!));
        Assert.Equal([1, 0, 0], NodeNamed(json, "Rig")["translation"]!.AsArray().Select(v => (double)v!));
        Assert.NotNull(NodeNamed(json, "Ship")["mesh"]);

        // The camera, five back from the origin in LightWave (-Z), is five along +Z in glTF, looking down -Z at it:
        // no rotation needed, as both look along their own forward axis.
        var camera = NodeNamed(json, "Camera");
        Assert.Equal([0, 0, 5], camera["translation"]!.AsArray().Select(v => (double)v!));
        Assert.Null(camera["rotation"]);
        var perspective = json["cameras"]![(int)camera["camera"]!]!["perspective"]!;
        Assert.Equal(Mat4.Deg(CameraItem.ZoomToVerticalFov(CameraItem.DefaultZoom)), (double)perspective["yfov"]!, 6);
        Assert.Equal(640 / 480.0, (double)perspective["aspectRatio"]!, 6);

        var lights = json["extensions"]![GltfBuilder.LightsExtension]!["lights"]!.AsArray();
        var spot = lights[(int)NodeNamed(json, "Spot")["extensions"]![GltfBuilder.LightsExtension]!["light"]!]!;
        Assert.Equal("spot", (string?)spot["type"]);
        Assert.Equal(Mat4.Deg(20), (double)spot["spot"]!["outerConeAngle"]!, 6);
        Assert.Equal(Mat4.Deg(15), (double)spot["spot"]!["innerConeAngle"]!, 6);
        Assert.Equal(12, (double)spot["range"]!);
        Assert.Equal(1, (double)spot["color"]![0]!, 6);
        Assert.Equal(GltfBuilder.ToLinear(0.5), (double)spot["color"]![1]!, 6);
        Assert.Equal("directional", (string?)lights[(int)NodeNamed(json, "Light")["extensions"]![GltfBuilder.LightsExtension]!["light"]!]!["type"]);
    }

    [Fact]
    public void A_heading_turns_the_same_way_in_glTF()
    {
        var json = Json(SceneGltf.Write(Sample(), GltfPacking.Binary).Main);
        // Heading 90 turns LightWave's +Z (forward) to +X. glTF's forward is -Z; the rotation must take it to +X too.
        var q = NodeNamed(json, "Turned")["rotation"]!.AsArray().Select(v => (double)v!).ToArray();
        var m = Rotate(q, new Vec3(0, 0, -1));
        Assert.True(Vec3.Near(new Vec3(1, 0, 0), m, 1e-6), m.ToString());
    }

    private static Vec3 Rotate(double[] q, Vec3 v)
    {
        var u = new Vec3(q[0], q[1], q[2]);
        double w = q[3];
        return u * (2 * Vec3.Dot(u, v)) + v * (w * w - Vec3.Dot(u, u)) + Vec3.Cross(u, v) * (2 * w);
    }

    [Fact]
    public void Keyed_motion_is_sampled_every_frame()
    {
        var scene = Sample();
        var (glb, _, _) = SceneGltf.Write(scene, GltfPacking.Binary);
        var json = Json(glb);
        var animation = json["animations"]!.AsArray().Single()!;
        var channels = animation["channels"]!.AsArray();
        // Only the ship moves, and only its position.
        var channel = Assert.Single(channels)!;
        Assert.Equal(IndexOf(json, "Ship"), (int)channel["target"]!["node"]!);
        Assert.Equal("translation", (string?)channel["target"]!["path"]);
        var sampler = animation["samplers"]![(int)channel["sampler"]!]!;
        int input = (int)sampler["input"]!;
        // The times: 31 frames from 0 s, and the min and max the spec requires on an animation's input.
        var times = Floats(glb, json, input);
        Assert.Equal(31, times.Length);
        Assert.Equal(0, times[0]);
        Assert.Equal(1f, times[^1], 5);
        Assert.Equal(0, (double)json["accessors"]![input]!["min"]![0]!);
        Assert.Equal(1, (double)json["accessors"]![input]!["max"]![0]!, 5);
        var t = Floats(glb, json, (int)sampler["output"]!);
        Assert.Equal(31 * 3, t.Length);
        // Relative to the rig, the ship goes from z 0 to 4 in LightWave: 0 to -4 in glTF.
        for (int f = 0; f < 31; f++)
        {
            var lw = scene.Find("Ship")!.PositionAt(f + 1);
            Assert.Equal(lw.X, t[f * 3], 5);
            Assert.Equal(-lw.Z, t[f * 3 + 2], 5);
        }
        Assert.Equal(-4, t[^1], 5);
    }

    [Fact]
    public void Saves_a_gltf_with_its_bin_beside_it_and_notes_what_it_leaves_out()
    {
        var scene = Sample();
        var ship = (ObjectItem)scene.Find("Ship")!;
        ship.AddMorph(new MorphTarget("Squash", ship.BaseMesh()));
        scene.AddObject("Lost");
        string path = Path.Combine(_dir, "scene.gltf");
        var warnings = SceneGltf.Save(scene, path, separateBin: true);
        Assert.True(File.Exists(Path.Combine(_dir, "scene.bin")));
        Assert.Contains(warnings, w => w.Contains("Ship") && w.Contains("rest shape"));
        Assert.Contains(warnings, w => w.Contains("Lost") && w.Contains("placeholder"));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("scene.bin", (string?)json["buffers"]![0]!["uri"]);
        Assert.Throws<NotSupportedException>(() => SceneGltf.Save(scene, Path.Combine(_dir, "scene.lws")));
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".tmp"));

        // Its meshes read back as an object, one layer for the ship (placed by the rig and its first frame).
        var doc = ObjectFiles.Load(Path.Combine(_dir, "scene.gltf"));
        var layer = doc.Layers.Single(l => !l.Mesh.IsEmpty);
        Assert.Equal("Ship", layer.Name);
        Assert.Contains(layer.Mesh.Points, p => Vec3.Near(p, new Vec3(2, 0, 0), 1e-6));
    }
}
