using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using ModelContextProtocol.Protocol;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>The Layout tools driven directly against a headless host: the scene, items, keys, files, pictures, and their errors.</summary>
public class LayoutHandlerTests
{
    private static double[] V(JsonNode? n) => n!.AsArray().Select(x => (double)x!).ToArray();

    private static void Near(double[] expected, double[] actual, double tolerance = 1e-5)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    /// <summary>A bench with a box in the Modeler (2 m each way, on the origin) and in the scene.</summary>
    private static async Task<Bench> WithBox()
    {
        var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 2.0, 2, 2 } }), Cancel);
        await b.Tools.AddObject(cancel: Cancel);
        return b;
    }

    // ───────────────────────────── reading ─────────────────────────────

    [Fact]
    public async Task Get_scene_describes_the_settings_and_every_item()
    {
        using var b = await WithBox();
        var s = await Parse(b.Tools.GetScene(cancel: Cancel));
        Assert.Equal(1, (int)s["frames"]!["first"]!);
        Assert.Equal(60, (int)s["frames"]!["last"]!);
        Assert.Equal(30, (double)s["frames"]!["fps"]!);
        Assert.Equal("Camera", (string?)s["camera"]);
        Assert.True((bool)s["auto_key"]!);
        var items = s["items"]!.AsArray();
        Assert.Equal(["camera", "light", "object"], items.Select(i => (string)i!["kind"]!));
        var cam = items[0]!;
        Near([0, 0, -5], V(cam["position"]));
        Assert.Equal(3.2, (double)cam["zoom"]!);
        Assert.Equal(34.708, (double)cam["fov"]!["vertical"]!, 3);
        Near([0, 0, 1], V(cam["looks_toward"]));
        var light = items[1]!;
        Assert.Equal("distant", (string?)light["type"]);
        var f = Rotations.Forward(new Vec3(45, 35, 0));
        Near([f.X, f.Y, f.Z], V(light["shines_toward"]));
        var obj = items[2]!;
        Assert.Equal("the Modeler's object (live)", (string?)obj["source"]);
        Assert.Equal(8, (int)obj["points"]!);
        Near([2, 2, 2], V(obj["world_bounds"]!["size"]));
        Assert.Empty(obj["keyed"]!.AsArray());
        Assert.Equal(1, (int)s["undo_scene"]!["depth"]!);
        Assert.Equal("Agent: added an object", (string?)s["undo_scene"]!["next_undo"]);
        Assert.Contains("Frame 70 is outside the scene's frames, 1 to 60", await Error(b.Tools.GetScene(70, Cancel)));
    }

    [Fact]
    public async Task Unknown_items_and_channels_are_clear_errors()
    {
        using var b = await WithBox();
        var e = await Error(b.Tools.SetTransform("Camra", position: [0, 0, 0], cancel: Cancel));
        Assert.Contains("no item called \"Camra\"", e);
        Assert.Contains("\"Camera\" (camera)", e);
        Assert.Contains("no channel \"wobble\"", await Error(b.Tools.SetKey("Camera", "wobble", 1, 0, cancel: Cancel)));
        Assert.Contains("zoom", await Error(b.Tools.SetKey("Camera", "intensity", 1, 0, cancel: Cancel)));
        Assert.Contains("There's no item #9", await Error(b.Tools.GetKeys("#9", cancel: Cancel)));
        // Two of a name: they're told apart by #index.
        await b.Tools.AddNull("Twin", cancel: Cancel);
        b.Scene.AddNull("Twin");
        var twins = await Error(b.Tools.GetKeys("Twin", cancel: Cancel));
        Assert.Contains("2 items called \"Twin\"", twins);
        Assert.Contains("#3", twins);
        Assert.Equal("Twin", (string?)(await Parse(b.Tools.GetKeys("#4", cancel: Cancel)))["item"]);
        // New names never clash.
        var n = await Parse(b.Tools.AddNull("Twin", cancel: Cancel));
        Assert.Equal("Twin (2)", (string?)n["name"]);
        // Channels by their other names, and any case.
        Assert.Equal("heading", (string?)(await Parse(b.Tools.SetKey("Camera", "Rotation.H", 1, 5, cancel: Cancel)))["channel"]);
        Assert.Equal("position.y", (string?)(await Parse(b.Tools.SetKey("Camera", "POSITION_Y", 1, 5, cancel: Cancel)))["channel"]);
    }

    // ───────────────────────────── adding ─────────────────────────────

    [Fact]
    public async Task Add_object_takes_the_modelers_object_once_a_file_or_one_layer()
    {
        using var b = await WithBox();
        var again = await Parse(b.Tools.AddObject(cancel: Cancel));
        Assert.Contains("already in the scene", (string?)again["note"]);
        Assert.Single(b.Scene.Objects);
        // Live: modeling shows in the scene at once.
        await b.Tools.RunTool("Move", Bench.Args(new { Offset = new[] { 1.0, 0, 0 } }), Cancel);
        var s = await Parse(b.Tools.GetScene(cancel: Cancel));
        Near([2, 1, 1], V(s["items"]![2]!["world_bounds"]!["max"]), 1e-9);
        Near([0, -1, -1], V(s["items"]![2]!["world_bounds"]!["min"]), 1e-9);

        // From a file in the working folder, placed and parented.
        await b.Tools.Save("box.lwo", cancel: Cancel);
        await b.Tools.AddNull("Rig", position: [0, 1, 0], cancel: Cancel);
        var file = await Parse(b.Tools.AddObject("box.lwo", position: [5, 0, 0], rotation: [90, 0, 0], scale: [2, 2, 2], parent: "Rig", cancel: Cancel));
        Assert.Equal("box", (string?)file["name"]);
        Assert.Equal("file", (string?)file["source"]);
        Assert.Equal("Rig", (string?)file["parent"]);
        Near([5, 1, 0], V(file["world_position"]));
        Assert.Equal(4, V(file["world_bounds"]!["size"])[0], 9);

        // One layer of the Modeler's object.
        await b.Tools.SetLayers([2], cancel: Cancel);
        await b.Tools.RunTool("Ball", null, Cancel);
        var layer = await Parse(b.Tools.AddObject(layer: 2, name: "Ball", cancel: Cancel));
        Assert.Equal(2, (int)layer["layer"]!);
        Assert.Equal(1, ((ObjectItem)b.Scene.Find("Ball")!).Layer);
        Assert.True((int)layer["points"]! > 8);

        Assert.Contains("outside the working folder", await Error(b.Tools.AddObject("../box.lwo", cancel: Cancel)));
        Assert.Contains("no file", await Error(b.Tools.AddObject("nothere.lwo", cancel: Cancel)));
        Assert.Contains("no layer 12", await Error(b.Tools.AddObject(layer: 12, cancel: Cancel)));
        Assert.Contains("Layer 5 is empty", await Error(b.Tools.AddObject(layer: 5, cancel: Cancel)));
        Assert.Equal(3, b.Scene.Objects.Count());
    }

    [Fact]
    public async Task Lights_cameras_nulls_and_bones_are_added_with_their_settings()
    {
        using var b = await WithBox();
        var spot = await Parse(b.Tools.AddLight("spot", "Key", [3, 4, -3], look_at: [0, 0, 0], color: [255, 204, 153], intensity: 1.5, cone_angle: 25, edge_angle: 8, range: 20, cancel: Cancel));
        Assert.Equal("spot", (string?)spot["type"]);
        var d = new Vec3(-3, -4, 3).Normalized();
        Near([d.X, d.Y, d.Z], V(spot["shines_toward"]));
        Assert.Equal(25, (double)spot["cone_angle"]!);
        var light = (LightItem)b.Scene.Find("Key")!;
        Assert.Equal(20, light.Range);
        Assert.Equal(1.5, light.Intensity.Evaluate(30));
        Assert.Contains("type must be one of distant, point, spot", await Error(b.Tools.AddLight("sun", cancel: Cancel)));
        // Colours are 0 to 255 as surfaces' are; Layout keeps them as 0 to 1.
        Assert.Equal([255, 204, 153], spot["color"]!.AsArray().Select(v => (int)v!));
        Near([1, 0.8, 0.6], [light.Color.X, light.Color.Y, light.Color.Z]);
        Assert.Contains("not 0 to 1", await Error(b.Tools.AddLight(color: [1, 0.8, 0.6], cancel: Cancel)));
        Assert.Contains("0 to 255", await Error(b.Tools.AddLight(color: [256, 0, 0], cancel: Cancel)));
        Assert.Contains("cone_angle must be from 0.1 to 90", await Error(b.Tools.AddLight("spot", cone_angle: 120, cancel: Cancel)));
        Assert.Contains("rotation or look_at, not both", await Error(b.Tools.AddLight(rotation: [0, 0, 0], look_at: [1, 1, 1], cancel: Cancel)));

        var cam = await Parse(b.Tools.AddCamera("Close", [0, 0, -3], fov: 60, width: 320, height: 240, cancel: Cancel));
        Assert.True((bool)cam["current"]!);
        Assert.Equal(60, (double)cam["fov"]!["vertical"]!, 6);
        Assert.Equal("Close", b.Scene.CurrentCamera!.Name);
        Assert.Contains("zoom or fov, not both", await Error(b.Tools.AddCamera(zoom: 2, fov: 40, cancel: Cancel)));
        var side = await Parse(b.Tools.AddCamera("Side", make_current: false, cancel: Cancel));
        Assert.False((bool)side["current"]!);

        var bone = await Parse(b.Tools.AddBone("Modeler object", "Spine", [0, -1, 0], [0, -90, 0], 1, cancel: Cancel));
        Assert.Equal("Modeler object", (string?)bone["object"]);
        var tip = await Parse(b.Tools.AddBone("Modeler object", "Neck", [0, 0, 1], rest_length: 0.5, parent_bone: "Spine", cancel: Cancel));
        Assert.Equal("Spine", (string?)tip["parent"]);
        Assert.Contains("isn't an object", await Error(b.Tools.AddBone("Close", cancel: Cancel)));
        Assert.Contains("isn't a bone of", await Error(b.Tools.AddBone("Modeler object", parent_bone: "Key", cancel: Cancel)));
        Assert.Contains("rest_length must be more than 0", await Error(b.Tools.AddBone("Modeler object", rest_length: 0, cancel: Cancel)));
        // Bending the spine moves the box's top.
        double before = b.Scene.Evaluate(1).Objects[0].WorldMesh.Bounds().Max.X;
        await b.Tools.SetTransform("Spine", rotation: [0, -90, 30], key: false, cancel: Cancel);
        Assert.NotEqual(before, b.Scene.Evaluate(1).Objects[0].WorldMesh.Bounds().Max.X);

        // Removing the object takes its bones; children of a removed null move up.
        await b.Tools.AddNull("Rig", cancel: Cancel);
        await b.Tools.SetItem("Close", parent: "Rig", cancel: Cancel);
        var gone = await Parse(b.Tools.RemoveItem("Modeler object", Cancel));
        Assert.Equal(["Modeler object", "Spine", "Neck"], gone["removed"]!.AsArray().Select(x => (string)x!));
        await b.Tools.RemoveItem("Rig", Cancel);
        Assert.Null(b.Scene.Find("Close")!.Parent);
    }

    // ───────────────────────────── arranging ─────────────────────────────

    [Fact]
    public async Task Set_item_changes_settings_and_says_which_belong_to_which_kind()
    {
        using var b = await WithBox();
        var r = await Parse(b.Tools.SetItem("Light", name: "Sun", type: "point", color: [255, 255, 230], intensity: 0.5, range: 30, shadows: false, cancel: Cancel));
        Assert.Equal("Sun", (string?)r["name"]);
        Assert.Equal("point", (string?)r["type"]);
        Assert.Equal(30, (double)r["range"]!);
        Assert.False((bool)r["shadows"]!);
        await b.Tools.SetItem("Camera", zoom: 2, width: 800, height: 600, cancel: Cancel);
        var cam = b.Scene.CurrentCamera!;
        Assert.Equal(2, cam.Zoom.Evaluate(1));
        Assert.Equal(800, cam.Width);
        await b.Tools.SetItem("Modeler object", pivot: [0, -1, 0], visible: false, cast_shadows: false, cancel: Cancel);
        var obj = b.Scene.Objects.Single();
        Assert.False(obj.Visible);
        Assert.False(obj.CastShadows);
        Assert.Equal(new Vec3(0, -1, 0), obj.Pivot);

        Assert.Contains("cone_angle is a setting of lights, and Camera is a camera", await Error(b.Tools.SetItem("Camera", cone_angle: 10, cancel: Cancel)));
        Assert.Contains("zoom is a setting of cameras", await Error(b.Tools.SetItem("Sun", zoom: 3, cancel: Cancel)));
        Assert.Contains("make another camera current", await Error(b.Tools.SetItem("Camera", make_current: false, cancel: Cancel)));
        // Parenting: and "" for none.
        await b.Tools.SetItem("Camera", parent: "Modeler object", cancel: Cancel);
        Assert.Same(obj, cam.Parent);
        Assert.Contains("cycle", await Error(b.Tools.SetItem("Modeler object", parent: "Camera", cancel: Cancel)));
        await b.Tools.SetItem("Camera", parent: "", cancel: Cancel);
        Assert.Null(cam.Parent);
        // Animated intensity is keyed with set_key, not set here.
        await b.Tools.SetKey("Sun", "intensity", 1, 0, cancel: Cancel);
        await b.Tools.SetKey("Sun", "intensity", 30, 2, cancel: Cancel);
        Assert.Contains("animated", await Error(b.Tools.SetItem("Sun", intensity: 1, cancel: Cancel)));
    }

    [Fact]
    public async Task A_call_that_fails_part_way_leaves_the_scene_as_it_was()
    {
        using var b = await WithBox();
        // A check that fails before anything changes.
        Assert.Contains("cone_angle", await Error(b.Tools.SetItem("Camera", name: "Renamed", pivot: [1, 1, 1], cone_angle: 10, cancel: Cancel)));
        await b.Tools.AddLight("spot", "Spot", cancel: Cancel);
        var before = SceneJson.Write(b.Scene, embedMeshes: true);
        // The name, pivot, visibility and colour are set before the bad cone angle is reached: all are put back.
        Assert.Contains("cone_angle must be from 0.1 to 90", await Error(b.Tools.SetItem("Spot", name: "Renamed", pivot: [1, 1, 1], visible: false, color: [0, 0, 255], cone_angle: 500, cancel: Cancel)));
        Assert.Contains("cycle", await Error(b.Tools.SetItem("Modeler object", name: "Renamed", parent: "Modeler object", cancel: Cancel)));
        Assert.Equal(before, SceneJson.Write(b.Scene, embedMeshes: true));
        Assert.Equal("Spot", b.Scene.Lights.Last().Name);
        // A key set before an animated channel turns the rest down.
        await b.Tools.SetKey("Camera", "position.z", 1, -5, cancel: Cancel);
        await b.Tools.SetKey("Camera", "position.z", 60, -9, cancel: Cancel);
        before = SceneJson.Write(b.Scene, embedMeshes: true);
        Assert.Contains("has no key at frame 20", await Error(b.Tools.SetTransform("Camera", 20, position: [1, 2, 3], key: false, cancel: Cancel)));
        Assert.Equal(before, SceneJson.Write(b.Scene, embedMeshes: true));
        Assert.Equal(-5, b.Scene.CurrentCamera!.PositionAt(1).Z);
        Assert.Equal(0, b.Scene.CurrentCamera.PositionAt(20).X);
        // The failures aren't undo steps, and undo_scene carries on as before them.
        // (add_object, add_light and two set_keys.)
        Assert.Equal(4, b.Layout.History.Count);
        Assert.Equal(4, (int)(await Parse(b.Tools.GetScene(cancel: Cancel)))["undo_scene"]!["depth"]!);
        Assert.Equal("Agent: set a key", b.Layout.History.UndoLabel);
        await b.Tools.UndoScene(Cancel);
        Assert.True(b.Scene.CurrentCamera.Channels[2].Keys.Count == 1);
    }

    // ───────────────────────────── animating ─────────────────────────────

    [Fact]
    public async Task Keys_interpolate_and_get_scene_reads_them_between()
    {
        using var b = await WithBox();
        await b.Tools.SetTransform("Modeler object", 1, position: [0, 0, 0], key: true, cancel: Cancel);
        await b.Tools.SetTransform("Modeler object", 30, position: [3, 0, 0], key: true, cancel: Cancel);
        await b.Tools.SetTransform("Modeler object", 60, position: [3, 3, 0], rotation: [90, 0, 0], key: true, cancel: Cancel);
        var keys = await Parse(b.Tools.GetKeys("Modeler object", "position.x", Cancel));
        var k = keys["channels"]![0]!["keys"]!.AsArray();
        Assert.Equal([1.0, 30, 60], k.Select(x => (double)x!["frame"]!));
        Assert.Equal([1.0, 30, 60], keys["frames_with_keys"]!.AsArray().Select(x => (double)x!));
        // Heading was keyed only at 60: its constant was replaced, so it holds 90 throughout.
        var all = await Parse(b.Tools.GetKeys("Modeler object", cancel: Cancel));
        var heading = all["channels"]!.AsArray().Single(c => (string?)c!["channel"] == "heading")!;
        Assert.Single(heading["keys"]!.AsArray());
        Assert.Equal(90, (double)(await Parse(b.Tools.GetScene(10, Cancel)))["items"]![2]!["rotation"]![0]!);

        // Between keys: Layout's own TCB spline, through get_scene.
        var expected = new Envelope();
        expected.AddKey(new EnvelopeKey(1, 0));
        expected.AddKey(new EnvelopeKey(30, 3));
        expected.AddKey(new EnvelopeKey(60, 3));
        var at15 = await Parse(b.Tools.GetScene(15, Cancel));
        double x15 = V(at15["items"]![2]!["position"])[0];
        Assert.Equal(expected.Evaluate(15), x15, 5);
        Assert.NotEqual(3 * 14 / 29.0, x15, 3);
        Assert.Equal(new[] { "position.x", "position.y", "position.z", "heading", "pitch", "bank" }, at15["items"]![2]!["keyed"]!.AsArray().Select(x => (string)x!));

        // Linear and tension through set_key.
        await b.Tools.SetKey("Modeler object", "position.x", 30, 3, linear: true, cancel: Cancel);
        Assert.Equal(3 * 14 / 29.0, V((await Parse(b.Tools.GetScene(15, Cancel)))["items"]![2]!["position"])[0], 5);
        var tcb = await Parse(b.Tools.SetKey("Modeler object", "position.y", 60, 3, tension: 1, cancel: Cancel));
        Assert.Equal(1, (double)tcb["keys"]![2]!["tension"]!);
        Assert.Contains("tension must be from -1 to 1", await Error(b.Tools.SetKey("Modeler object", "position.y", 60, 3, tension: 2, cancel: Cancel)));
        Assert.Contains("outside the scene's frames", await Error(b.Tools.SetKey("Modeler object", "position.y", 0, 3, cancel: Cancel)));

        // End behaviours: repeat after the last key.
        await b.Tools.SetScene(last_frame: 120, cancel: Cancel);
        var eb = await Parse(b.Tools.SetEndBehavior("Modeler object", "position.x", post: "offset_repeat", cancel: Cancel));
        Assert.Equal("offset_repeat", (string?)eb["post"]);
        Assert.Equal(b.Scene.Objects.Single().PositionAt(30).X + 3, b.Scene.Objects.Single().PositionAt(89).X, 9);
        Assert.Contains("must be one of constant, reset", await Error(b.Tools.SetEndBehavior("Modeler object", post: "loop", cancel: Cancel)));

        // Deleting keys: one channel's, or all at a frame; a channel's last key stays.
        var del = await Parse(b.Tools.DeleteKey("Modeler object", 30, cancel: Cancel));
        Assert.Contains("position.x", del["removed"]!.AsArray().Select(x => (string)x!));
        var lone = await Parse(b.Tools.DeleteKey("Modeler object", 60, "heading", Cancel));
        Assert.Contains("only key", (string?)lone["kept"]);
        Assert.Contains("no key at frame 45", await Error(b.Tools.DeleteKey("Modeler object", 45, cancel: Cancel)));
    }

    [Fact]
    public async Task Without_auto_key_values_change_the_constant()
    {
        using var b = await WithBox();
        b.Layout.AutoKey = false;
        var r = await Parse(b.Tools.SetTransform("Camera", 30, position: [0, 1, -6], cancel: Cancel));
        Assert.Equal("values (no new keys)", (string?)r["set"]);
        Assert.Empty(r["keyed"]!.AsArray());
        Assert.Equal(1, b.Scene.CurrentCamera!.PositionAt(60).Y);
        b.Layout.AutoKey = true;
        r = await Parse(b.Tools.SetTransform("Camera", 30, position: [0, 2, -6], cancel: Cancel));
        Assert.Equal("keys at frame 30", (string?)r["set"]);
        Assert.Contains("give position, rotation or scale", (await Error(b.Tools.SetTransform("Camera", cancel: Cancel))).ToLowerInvariant());
        Assert.Contains("three numbers", await Error(b.Tools.SetTransform("Camera", position: [1, 2], cancel: Cancel)));
    }

    [Fact]
    public async Task Look_at_turns_the_z_axis_toward_a_point_or_item()
    {
        using var b = await WithBox();
        await b.Tools.SetTransform("Modeler object", position: [1, 0.5, 2], key: false, cancel: Cancel);
        await b.Tools.AddNull("Rig", position: [0, 1, 0], rotation: [40, 10, 0], cancel: Cancel);
        await b.Tools.SetItem("Camera", parent: "Rig", cancel: Cancel);
        await b.Tools.SetTransform("Camera", 1, position: [3, 2, -6], rotation: [0, 0, 12], key: true, cancel: Cancel);
        var r = await Parse(b.Tools.LookAt("Camera", "Modeler object", frame: 1, key: true, cancel: Cancel));
        Near([1, 0.5, 2], V(r["facing"]));
        var cam = b.Scene.CurrentCamera!;
        var eye = cam.CameraToWorld(1).TransformPoint(Vec3.Zero);
        var look = cam.CameraToWorld(1).TransformVector(Vec3.UnitZ);
        var want = (new Vec3(1, 0.5, 2) - eye).Normalized();
        Assert.True(Vec3.Near(want, look, 1e-9), $"{look} should be {want}");
        Assert.Equal(12, cam.RotationAt(1).Z, 9);
        Near(V(r["looks_toward"]), [want.X, want.Y, want.Z]);
        // A point, keyed at another frame.
        await b.Tools.LookAt("Light", point: [0, 0, 0], frame: 20, cancel: Cancel);
        var light = b.Scene.Lights.First();
        Assert.True(Vec3.Near((-light.WorldPosition(20)).Normalized(), light.Direction(20), 1e-9));
        Assert.Contains("one of them", await Error(b.Tools.LookAt("Light", cancel: Cancel)));
        // Straight ahead is a pitch of 0, not -0.
        await b.Tools.AddCamera("Straight", [0, 0, -5], cancel: Cancel);
        Assert.Contains("\"rotation\":[0,0,0]", await b.Tools.LookAt("Straight", point: [0, 0, 3], frame: 1, cancel: Cancel));
        Assert.Contains("can't face itself", await Error(b.Tools.LookAt("Light", "Light", cancel: Cancel)));
        Assert.Contains("can't be turned to face it", await Error(b.Tools.LookAt("Light", point: [-2, 2, -2], cancel: Cancel)));
    }

    // ───────────────────────────── the scene's settings ─────────────────────────────

    [Fact]
    public async Task Set_scene_checks_everything_before_changing_anything()
    {
        using var b = await WithBox();
        await b.Tools.AddCamera("Second", make_current: false, cancel: Cancel);
        var r = await Parse(b.Tools.SetScene(first_frame: 0, last_frame: 90, fps: 24, ambient_color: [230, 230, 255], ambient_intensity: 0.1,
            gradient: true, gradient_top: [51, 77, 153], gradient_bottom: [26, 26, 26], fog: "linear", fog_min: 5, fog_max: 40, fog_color: [128, 128, 128],
            camera: "Second", frame: 45, cancel: Cancel));
        Assert.Equal(90, (int)r["frames"]!["last"]!);
        Assert.Equal(45, (double)r["frames"]!["current"]!);
        Assert.Equal("Second", (string?)r["camera"]);
        Assert.True((bool)r["background"]!["gradient"]!);
        Assert.Equal("linear", (string?)r["fog"]!["type"]);
        Assert.Equal([51, 77, 153], r["background"]!["top"]!.AsArray().Select(v => (int)v!));
        Assert.Null(r["items"]);
        Assert.Equal(45, b.Layout.Frame);
        var s = b.Scene;
        string before = SceneJson.Write(s);
        Assert.Contains("can't be before first_frame", await Error(b.Tools.SetScene(first_frame: 10, last_frame: 5, cancel: Cancel)));
        Assert.Contains("fps must be more than 0", await Error(b.Tools.SetScene(fps: 0, cancel: Cancel)));
        Assert.Contains("fog_max more than fog_min", await Error(b.Tools.SetScene(fog_min: 50, cancel: Cancel)));
        Assert.Contains("isn't a camera", await Error(b.Tools.SetScene(ambient_intensity: 3, camera: "Light", cancel: Cancel)));
        Assert.Contains("frame must be within", await Error(b.Tools.SetScene(ambient_intensity: 3, frame: 500, cancel: Cancel)));
        Assert.Contains("must be one of off, linear", await Error(b.Tools.SetScene(fog: "thick", cancel: Cancel)));
        Assert.Equal(before, SceneJson.Write(s));
    }

    [Fact]
    public async Task Undo_and_redo_scene_step_through_the_shared_history()
    {
        using var b = await WithBox();
        await b.Tools.AddLight("point", "Bulb", [0, 3, 0], cancel: Cancel);
        await b.Tools.SetTransform("Bulb", 10, position: [1, 3, 0], key: true, cancel: Cancel);
        var set = await Parse(b.Tools.SetItem("Bulb", intensity: 2, cancel: Cancel));
        // Each call says where the history stands.
        Assert.Equal(4, (int)set["undo_scene"]!["depth"]!);
        Assert.Equal("Agent: changed an item", (string?)set["undo_scene"]!["next_undo"]);
        // Moving the frame or reading isn't a step.
        await b.Tools.SetScene(frame: 20, cancel: Cancel);
        await b.Tools.GetScene(cancel: Cancel);
        Assert.Equal(4, b.Layout.History.Count);
        var u = await Parse(b.Tools.UndoScene(Cancel));
        Assert.Equal("Agent: changed an item", (string?)u["undone"]);
        Assert.Equal(1, ((LightItem)b.Scene.Find("Bulb")!).Intensity.Evaluate(1));
        Assert.Equal(20, b.Layout.Frame);
        Assert.Equal("Agent: changed an item", (string?)u["undo_scene"]!["next_redo"]);
        u = await Parse(b.Tools.UndoScene(Cancel));
        Assert.Equal("Agent: moved an item", (string?)u["undone"]);
        Assert.Equal(0, b.Scene.Find("Bulb")!.PositionAt(10).X);
        var bulb = b.Scene.Find("Bulb")!;
        await b.Tools.UndoScene(Cancel);
        Assert.Null(b.Scene.Find("Bulb"));
        Assert.Null(bulb.Scene);

        // Redo brings them back, the very item included.
        var r = await Parse(b.Tools.RedoScene(Cancel));
        Assert.Equal("Agent: added a light", (string?)r["redone"]);
        Assert.Same(bulb, b.Scene.Find("Bulb"));
        await b.Tools.RedoScene(Cancel);
        await b.Tools.RedoScene(Cancel);
        Assert.Equal(1, bulb.PositionAt(10).X);
        Assert.Equal(2, ((LightItem)bulb).Intensity.Evaluate(1));
        r = await Parse(b.Tools.RedoScene(Cancel));
        Assert.Null((string?)r["redone"]);
        Assert.Contains("nothing to redo", (string?)r["note"]);

        // The person's steps are on the same history: undo_scene takes them back too.
        b.Layout.Add(new NullItem("The person's"));
        Assert.Equal("Add Null", b.Layout.History.UndoLabel);
        u = await Parse(b.Tools.UndoScene(Cancel));
        Assert.Equal("Add Null", (string?)u["undone"]);
        Assert.Null(b.Scene.Find("The person's"));

        // A new change clears what could be redone.
        await b.Tools.AddNull("Mine", cancel: Cancel);
        Assert.False(b.Layout.History.CanRedo);
        while (b.Layout.History.CanUndo) await b.Tools.UndoScene(Cancel);
        Assert.Empty(b.Scene.Objects);
        u = await Parse(b.Tools.UndoScene(Cancel));
        Assert.Null((string?)u["undone"]);
        Assert.Contains("nothing to undo", (string?)u["note"]);

        // A new scene clears the history.
        await b.Tools.AddNull("Again", cancel: Cancel);
        await b.Tools.NewScene(discard_changes: true, cancel: Cancel);
        Assert.Equal(0, b.Layout.History.Count);
        Assert.False(b.Layout.History.CanRedo);
    }

    [Fact]
    public async Task A_failed_call_leaves_no_step_and_undo_returns_to_the_saved_scene()
    {
        using var b = await WithBox();
        await b.Tools.SaveScene("box.avascene", cancel: Cancel);
        Assert.False(b.Layout.Modified);
        int depth = b.Layout.History.Count;
        await Error(b.Tools.SetItem("Camera", name: "Renamed", cone_angle: 10, cancel: Cancel));
        await Error(b.Tools.SetTransform("Nobody", 1, position: [1, 2, 3], cancel: Cancel));
        Assert.Equal(depth, b.Layout.History.Count);
        // A failure that was put back leaves the scene as saved.
        Assert.False(b.Layout.Modified);
        await b.Tools.AddNull("N", cancel: Cancel);
        Assert.True(b.Layout.Modified);
        await b.Tools.UndoScene(Cancel);
        Assert.False(b.Layout.Modified);
        await b.Tools.RedoScene(Cancel);
        Assert.True(b.Layout.Modified);
    }

    // ───────────────────────────── files ─────────────────────────────

    [Fact]
    public async Task Scenes_save_and_open_inside_the_working_folder()
    {
        using var b = await WithBox();
        await b.Tools.AddLight("spot", "Key", [2, 3, -2], look_at: [0, 0, 0], color: [255, 230, 178], cancel: Cancel);
        await b.Tools.SetTransform("Camera", 1, position: [0, 1, -6], key: true, cancel: Cancel);
        await b.Tools.SetTransform("Camera", 60, position: [6, 1, 0], key: true, cancel: Cancel);
        var saved = await Parse(b.Tools.SaveScene("scenes/box.avascene", cancel: Cancel));
        Assert.Equal(b.Folder.File("scenes/box.avascene"), (string?)saved["saved"]);
        Assert.Empty(saved["warnings"]!.AsArray());
        Assert.False(b.Layout.Modified);
        // Saving again over its own file is fine; over another file it needs overwrite.
        await b.Tools.SaveScene("scenes/box.avascene", cancel: Cancel);
        File.WriteAllText(b.Folder.File("other.avascene"), "not a scene");
        Assert.Contains("already exists", await Error(b.Tools.SaveScene("other.avascene", cancel: Cancel)));
        Assert.Contains("outside the working folder", await Error(b.Tools.SaveScene("../out.avascene", cancel: Cancel)));
        Assert.Contains(".avascene", await Error(b.Tools.SaveScene("box.txt", cancel: Cancel)));
        // LightWave: no geometry, so the Modeler's unsaved object is a warning.
        var lws = await Parse(b.Tools.SaveScene("box.lws", cancel: Cancel));
        Assert.Contains(lws["warnings"]!.AsArray(), w => ((string)w!).Contains("no file"));

        double x30 = b.Scene.CurrentCamera!.PositionAt(30).X;
        await b.Tools.NewScene(cancel: Cancel);
        Assert.Equal(2, b.Scene.Items.Count);
        await b.Tools.AddNull("Unsaved", cancel: Cancel);
        Assert.Contains("haven't been saved", await Error(b.Tools.OpenScene("scenes/box.avascene", cancel: Cancel)));
        Assert.Contains("haven't been saved", await Error(b.Tools.NewScene(cancel: Cancel)));
        var opened = await Parse(b.Tools.OpenScene("scenes/box.avascene", discard_changes: true, cancel: Cancel));
        Assert.Equal(["Camera", "Light", "Modeler object", "Key"], opened["items"]!.AsArray().Select(i => (string)i!["name"]!));
        Assert.Equal("embedded", (string?)opened["items"]![2]!["source"]);
        Assert.Equal(x30, b.Scene.CurrentCamera!.PositionAt(30).X, 12);
        Assert.Equal(8, (int)opened["items"]![2]!["points"]!);
        Assert.Contains("no scene nothere.avascene", await Error(b.Tools.OpenScene("nothere.avascene", discard_changes: true, cancel: Cancel)));
        Assert.Contains("Not a readable avascene", await Error(b.Tools.OpenScene("other.avascene", discard_changes: true, cancel: Cancel)));
        Assert.Equal(4, b.Scene.Items.Count);

        // An .lws whose object is outside the working folder opens with a placeholder and says why.
        File.WriteAllText(b.Folder.File("far.lws"), "LWSC\n3\n\nFirstFrame 1\nLastFrame 60\nLoadObject /elsewhere/far.lwo\n");
        var far = await Parse(b.Tools.OpenScene("far.lws", discard_changes: true, cancel: Cancel));
        Assert.Contains(far["warnings"]!.AsArray(), w => ((string)w!).Contains("placeholder"));
        Assert.StartsWith("placeholder", (string?)far["items"]![0]!["source"]);
    }

    // ───────────────────────────── seeing ─────────────────────────────

    [Fact]
    public async Task Render_scene_ray_traces_a_frame_through_the_camera()
    {
        using var b = await WithBox();
        await b.Tools.SetScene(background_color: [0, 0, 51], cancel: Cancel);
        var r = await b.Tools.RenderScene(1, 64, cancel: Cancel);
        var png = Pngs.Of(r);
        var image = TextureImages.Decode(png)!;
        Assert.Equal((64, 48), (image.Width, image.Height));
        // The box is in the middle; the corners are the backdrop.
        var corner = (image.Rgba[0], image.Rgba[1], image.Rgba[2]);
        int mid = (24 * 64 + 32) * 4;
        Assert.Equal((0, 0, 51), ((int)corner.Item1, (int)corner.Item2, (int)corner.Item3));
        Assert.NotEqual(corner, (image.Rgba[mid], image.Rgba[mid + 1], image.Rgba[mid + 2]));
        Assert.Contains("through Camera, 64×48", Text(r));
        Assert.Contains("samples must be from 1 to 16", await Error(b.Tools.RenderScene(samples: 99, cancel: Cancel)));
        Assert.Contains("from 16 to 1920", await Error(b.Tools.RenderScene(width: 5000, cancel: Cancel)));
        Assert.Contains("isn't a camera", await Error(b.Tools.RenderScene(camera: "Light", cancel: Cancel)));
        Assert.Contains("outside the scene's frames", await Error(b.Tools.RenderScene(0, cancel: Cancel)));
        await b.Tools.RemoveItem("Camera", Cancel);
        Assert.Contains("no camera", await Error(b.Tools.RenderScene(cancel: Cancel)));
    }

    [Fact]
    public async Task Render_scene_view_draws_layouts_view_and_says_where_items_are()
    {
        using var b = await WithBox();
        var cam = await b.Tools.RenderSceneView(width: 160, cancel: Cancel);
        var (w, h, colours) = Pngs.Inspect(Pngs.Of(cam));
        Assert.Equal((160, 120), (w, h));
        Assert.True(colours > 10);
        Assert.Contains("object Modeler object at (80, 60)", Text(cam));
        var top = await b.Tools.RenderSceneView("top", width: 200, height: 200, shaded: false, cancel: Cancel);
        Assert.Contains("camera Camera at", Text(top));
        Assert.Contains("light Light at", Text(top));
        Assert.Contains("wireframe", Text(top));
        foreach (var v in new[] { "perspective", "front", "side" })
            Assert.True(Pngs.Inspect(Pngs.Of(await b.Tools.RenderSceneView(v, width: 64, height: 64, cancel: Cancel))).Colours > 3);
        Assert.Contains("view must be one of camera, perspective, top, front, side", await Error(b.Tools.RenderSceneView("uv", cancel: Cancel)));
    }

    [Fact]
    public async Task Render_animation_writes_numbered_frames_and_a_contact_sheet()
    {
        using var b = await WithBox();
        await b.Tools.SetTransform("Modeler object", 1, rotation: [0, 0, 0], key: true, cancel: Cancel);
        await b.Tools.SetTransform("Modeler object", 9, rotation: [90, 0, 0], key: true, cancel: Cancel);
        var r = await b.Tools.RenderAnimation("out", "spin", 1, 9, 4, width: 32, cancel: Cancel);
        var files = JsonNode.Parse(Text(r))!["files"]!.AsArray().Select(x => (string)x!).ToList();
        Assert.Equal(["spin0001.png", "spin0005.png", "spin0009.png"], files.Select(Path.GetFileName));
        Assert.All(files, f => Assert.StartsWith(b.Folder.File("out"), f));
        Assert.All(files, f => Assert.True(File.Exists(f)));
        Assert.NotEqual(File.ReadAllBytes(files[0]), File.ReadAllBytes(files[1]));
        var sheet = TextureImages.Decode(Pngs.Of(r))!;
        Assert.Equal(3 * 32 + 4 * 2, sheet.Width);
        // Again: its own files may be replaced; others only with overwrite.
        await b.Tools.RenderAnimation("out", "spin", 1, 9, 4, width: 32, cancel: Cancel);
        File.WriteAllBytes(b.Folder.File("mine0001.png"), [1]);
        Assert.Contains("already exists", await Error(b.Tools.RenderAnimation(".", "mine", 1, 1, width: 32, cancel: Cancel)));
        await b.Tools.SetScene(last_frame: 500, cancel: Cancel);
        Assert.Contains("at most 120", await Error(b.Tools.RenderAnimation(first: 1, last: 500, width: 32, cancel: Cancel)));
        Assert.Contains("million rays", await Error(b.Tools.RenderAnimation(first: 1, last: 100, width: 960, samples: 4, cancel: Cancel)));
        Assert.Contains("outside the working folder", await Error(b.Tools.RenderAnimation("../frames", first: 1, last: 1, width: 32, cancel: Cancel)));
        Assert.Contains("plain file name", await Error(b.Tools.RenderAnimation(name: "a/b", cancel: Cancel)));
    }
}
