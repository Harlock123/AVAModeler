using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>
/// The tools that deform objects in the scene, driven directly: copy_layer, morphs, displacements, skelegons → bones and
/// bones' rest settings; their errors, and undo.
/// </summary>
public class DeformHandlerTests
{
    /// <summary>A bench with a 2 m box cut into 8 along X in layer 1, and layer 1 in the scene as "Box".</summary>
    private static async Task<Bench> WithBox()
    {
        var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new Dictionary<string, object> { ["Size"] = new[] { 2.0, 2, 2 }, ["Segments X"] = 8 }), Cancel);
        await b.Tools.AddObject(layer: 1, name: "Box", cancel: Cancel);
        return b;
    }

    private static ObjectItem Box(Bench b) => (ObjectItem)b.Scene.Find("Box")!;

    // ───────────────────────────── copy_layer ─────────────────────────────

    [Fact]
    public async Task Copy_layer_copies_into_an_empty_layer_as_one_undo_step()
    {
        using var b = await WithBox();
        var base1 = b.Doc.Layers[0].Mesh;
        int depth = b.Doc.Undo.Count;
        var r = await Parse(b.Tools.CopyLayer(1, 2, cancel: Cancel));
        Assert.Equal(base1.Points.Count, (int)r["points"]!);
        var copy = b.Doc.Layers[1].Mesh;
        Assert.NotSame(base1, copy);
        Assert.Equal(base1.Points, copy.Points);
        Assert.Equal(depth + 1, b.Doc.Undo.Count);
        // The foreground is left alone.
        Assert.Equal([0], b.Doc.ForegroundLayers);

        Assert.Contains("Layer 2 isn't empty", await Error(b.Tools.CopyLayer(1, 2, cancel: Cancel)));
        Assert.Contains("same layer", await Error(b.Tools.CopyLayer(1, 1, cancel: Cancel)));
        Assert.Contains("Layer 3 is empty", await Error(b.Tools.CopyLayer(3, 4, cancel: Cancel)));
        Assert.Contains("no layer 11", await Error(b.Tools.CopyLayer(1, 11, cancel: Cancel)));
        await b.Tools.CopyLayer(1, 2, replace: true, cancel: Cancel);
        Assert.Equal(base1.Points.Count, b.Doc.Layers[1].Mesh.Points.Count);

        await b.Tools.Undo(Cancel);
        await b.Tools.Undo(Cancel);
        Assert.True(b.Doc.Layers[1].Mesh.IsEmpty);
    }

    // ───────────────────────────── morphs ─────────────────────────────

    /// <summary>Copies layer 1 to layer 2 and moves the copy's points with x > 0.6 up by 0.5.</summary>
    private static async Task MakeTarget(Bench b)
    {
        await b.Tools.CopyLayer(1, 2, cancel: Cancel);
        await b.Tools.SetLayers([2], cancel: Cancel);
        await b.Tools.SetSelectionMode("points", Cancel);
        await b.Tools.SelectBox([0.6, -2, -2], [2, 2, 2], cancel: Cancel);
        await b.Tools.RunTool("Move", Bench.Args(new { Offset = new[] { 0.0, 0.5, 0 } }), Cancel);
        await b.Tools.SelectNone(Cancel);
        await b.Tools.SetLayers([1], cancel: Cancel);
    }

    [Fact]
    public async Task Add_morph_snapshots_a_layer_and_its_weight_is_a_channel()
    {
        using var b = await WithBox();
        await MakeTarget(b);
        var r = await Parse(b.Tools.AddMorph("Box", "Lift", 2, 0, Cancel));
        Assert.Equal("morph.Lift", (string?)r["added"]);
        var m = Assert.Single(r["morphs"]!.AsArray())!;
        Assert.Equal("Lift", (string?)m["name"]);
        Assert.Equal(0, (double)m["weight"]!);
        Assert.Equal(b.Doc.Layers[0].Mesh.Points.Count, (int)m["target_points"]!);
        Assert.Equal("Agent: added a morph", b.Layout.History.UndoLabel);

        // Key it like any channel; get_scene and get_keys show it.
        await b.Tools.SetKey("Box", "morph.Lift", 1, 0, cancel: Cancel);
        await b.Tools.SetKey("Box", "MORPH.lift", 11, 1, cancel: Cancel);
        var box = Box(b);
        Assert.Equal(0.5, box.DeformedMesh(11).Bounds().Max.Y - 1, 9);
        Assert.InRange(box.DeformedMesh(6).Bounds().Max.Y - 1, 0.1, 0.4);
        var keys = await Parse(b.Tools.GetKeys("Box", "morph.Lift", Cancel));
        Assert.Equal(2, keys["channels"]![0]!["keys"]!.AsArray().Count);
        Assert.Equal([1.0, 11], (await Parse(b.Tools.GetKeys("Box", cancel: Cancel)))["frames_with_keys"]!.AsArray().Select(x => (double)x!));
        var s = await Parse(b.Tools.GetScene(11, Cancel));
        var item = s["items"]!.AsArray().Single(i => (string?)i!["name"] == "Box")!;
        Assert.Contains("morph.Lift", item["keyed"]!.AsArray().Select(x => (string?)x));
        Assert.Equal(1, (double)item["morphs"]![0]!["weight"]!);
        Assert.True((bool)item["morphs"]![0]!["keyed"]!);

        // The target was copied: later modeling in layer 2 doesn't change it.
        await b.Tools.SetLayers([2], cancel: Cancel);
        await b.Tools.RunTool("Move", Bench.Args(new { Offset = new[] { 5.0, 0, 0 } }), Cancel);
        Assert.Equal(1, box.DeformedMesh(11).Bounds().Max.X, 9);

        // Undo takes the keys and then the morph away; redo brings back the very morph.
        var morph = Assert.Single(box.Morphs);
        await b.Tools.UndoScene(Cancel);
        await b.Tools.UndoScene(Cancel);
        await b.Tools.UndoScene(Cancel);
        Assert.Empty(box.Morphs);
        await b.Tools.RedoScene(Cancel);
        Assert.Same(morph, Assert.Single(box.Morphs));
    }

    [Fact]
    public async Task Add_morph_refuses_targets_that_dont_fit()
    {
        using var b = await WithBox();
        await b.Tools.SetLayers([3], cancel: Cancel);
        await b.Tools.RunTool("Ball", null, Cancel);
        int depth = b.Layout.History.Count;
        var e = await Error(b.Tools.AddMorph("Box", "Ball", 3, cancel: Cancel));
        Assert.Contains("Morph target \"Ball\" of Box has", e);
        Assert.Contains("same points as the object, in the same order", e);
        Assert.Contains("copy_layer", e);
        Assert.Empty(Box(b).Morphs);
        Assert.Equal(depth, b.Layout.History.Count);
        Assert.Contains("Box's own layer", await Error(b.Tools.AddMorph("Box", "Self", 1, cancel: Cancel)));
        Assert.Contains("Layer 5 is empty", await Error(b.Tools.AddMorph("Box", "None", 5, cancel: Cancel)));
        Assert.Contains("Camera is a camera, not an object", await Error(b.Tools.AddMorph("Camera", "X", 3, cancel: Cancel)));
        Assert.Contains("needs a name", await Error(b.Tools.AddMorph("Box", " ", 3, cancel: Cancel)));

        // The Modeler's object with every layer merged takes the target's layer in too: the error says what to do.
        await MakeTarget(b);
        await b.Tools.AddObject(name: "Whole", cancel: Cancel);
        e = await Error(b.Tools.AddMorph("Whole", "Lift", 2, cancel: Cancel));
        Assert.Contains("every non-empty layer merged, layer 2 included", e);
        Assert.Contains("add_object with layer", e);

        await b.Tools.AddMorph("Box", "Lift", 2, cancel: Cancel);
        Assert.Contains("already has a morph called \"lift\"", await Error(b.Tools.AddMorph("Box", "lift", 2, cancel: Cancel)));
    }

    [Fact]
    public async Task Set_morph_renames_replaces_the_target_and_sets_the_weight_and_remove_morph_takes_it_off()
    {
        using var b = await WithBox();
        await MakeTarget(b);
        await b.Tools.AddMorph("Box", "Lift", 2, cancel: Cancel);
        var box = Box(b);
        var morph = box.Morphs[0];
        var first = morph.Target;

        var r = await Parse(b.Tools.SetMorph("Box", "lift", name: "Raise", weight: 0.5, cancel: Cancel));
        Assert.Equal("morph.Raise", (string?)r["morphs"]![0]!["channel"]);
        Assert.Equal(0.25, box.DeformedMesh(1).Bounds().Max.Y - 1, 9);

        // A new target replaces the mesh (it isn't edited in place), so undo brings the old one back.
        await b.Tools.SetLayers([2], cancel: Cancel);
        await b.Tools.RunTool("Move", Bench.Args(new { Offset = new[] { 0.0, 1, 0 } }), Cancel);
        await b.Tools.SetMorph("Box", "Raise", layer: 2, cancel: Cancel);
        Assert.NotSame(first, morph.Target);
        Assert.Equal(0.75, box.DeformedMesh(1).Bounds().Max.Y - 1, 9);
        await b.Tools.UndoScene(Cancel);
        Assert.Same(first, morph.Target);

        Assert.Contains("Give name, layer or weight", await Error(b.Tools.SetMorph("Box", "Raise", cancel: Cancel)));
        Assert.Contains("has no morph \"Smile\". Its morphs are \"Raise\"", await Error(b.Tools.SetMorph("Box", "Smile", weight: 1, cancel: Cancel)));
        await b.Tools.SetKey("Box", "morph.Raise", 1, 0, cancel: Cancel);
        await b.Tools.SetKey("Box", "morph.Raise", 10, 1, cancel: Cancel);
        Assert.Contains("morph.Raise is keyed", await Error(b.Tools.SetMorph("Box", "Raise", weight: 1, cancel: Cancel)));
        Assert.Contains("has no channel \"morph.Raise\"", await Error(b.Tools.SetKey("Camera", "morph.Raise", 1, 0, cancel: Cancel)));

        var removed = await Parse(b.Tools.RemoveMorph("Box", "Raise", Cancel));
        Assert.Equal("morph.Raise", (string?)removed["removed"]);
        Assert.Empty(box.Morphs);
        Assert.Contains("has no morphs", await Error(b.Tools.RemoveMorph("Box", "Raise", Cancel)));
        await b.Tools.UndoScene(Cancel);
        Assert.Same(morph, Assert.Single(box.Morphs));
        Assert.Equal(2, morph.Weight.Keys.Count);
    }

    // ───────────────────────────── displacements ─────────────────────────────

    [Fact]
    public async Task Displacements_move_points_by_the_wave_and_their_amplitude_is_a_channel()
    {
        using var b = await WithBox();
        var r = await Parse(b.Tools.AddDisplacement("Box", cancel: Cancel));
        Assert.Equal("displacement.1.amplitude", (string?)r["added"]);
        var d = r["displacements"]![0]!;
        Assert.Equal("wave", (string?)d["shape"]);
        Assert.Equal("y", (string?)d["direction"]);
        Assert.Equal("x", (string?)d["along"]);
        Assert.Equal(0.1, (double)d["amplitude"]!);
        Assert.Null(d["center"]);

        // The point at x = 0.25 is a quarter wavelength along: it rises by the whole amplitude.
        var box = Box(b);
        var moved = box.DeformedMesh(1).Points;
        var start = box.BaseMesh().Points;
        for (int i = 0; i < start.Count; i++)
            Assert.Equal(start[i].Y + 0.1 * Math.Sin(2 * Math.PI * start[i].X), moved[i].Y, 9);

        // A ripple round a centre, moving along Z.
        r = await Parse(b.Tools.AddDisplacement("Box", "ripple", "z", center: [0, 1, 0], wavelength: 2, speed: 0.1, phase: 90, amplitude: 0.2, cancel: Cancel));
        d = r["displacements"]![1]!;
        Assert.Equal("ripple", (string?)d["shape"]);
        Assert.Equal([0.0, 1, 0], d["center"]!.AsArray().Select(x => (double)x!));
        Assert.Null(d["along"]);
        var ripple = box.Displacements[1];
        Assert.Equal(3 + 0.2 * Math.Sin(2 * Math.PI * (1 - 0.1 * 5) / 2 + Math.PI / 2), ripple.Displace(new Vec3(1, 1, 3), 5).Z, 9);

        // Changed; keyed on its channel; the constant can't be set once keyed.
        await b.Tools.SetDisplacementTool("Box", 1, direction: "z", wavelength: 4, cancel: Cancel);
        Assert.Equal((Axis.Z, Axis.X, 4.0), (box.Displacements[0].Direction, box.Displacements[0].Along, box.Displacements[0].Wavelength));
        await b.Tools.SetKey("Box", "displacement.1.amplitude", 1, 0, cancel: Cancel);
        await b.Tools.SetKey("Box", "displacement.1.amplitude", 30, 0.3, cancel: Cancel);
        Assert.Equal(0.3, box.Displacements[0].Amplitude.Evaluate(30));
        Assert.Contains("displacement.1.amplitude is keyed", await Error(b.Tools.SetDisplacementTool("Box", 1, amplitude: 1, cancel: Cancel)));
        var keys = await Parse(b.Tools.GetKeys("Box", "Displacement.1.Amplitude", Cancel));
        Assert.Equal("displacement.1.amplitude", (string?)keys["channels"]![0]!["channel"]);

        // Errors change nothing.
        string before = SceneJson.Write(b.Scene);
        Assert.Contains("along is for a wave", await Error(b.Tools.AddDisplacement("Box", "ripple", along: "x", cancel: Cancel)));
        Assert.Contains("center is for a ripple", await Error(b.Tools.AddDisplacement("Box", center: [0, 0, 0], cancel: Cancel)));
        Assert.Contains("along can't be direction (y)", await Error(b.Tools.AddDisplacement("Box", along: "y", cancel: Cancel)));
        Assert.Contains("wavelength must be more than 0", await Error(b.Tools.AddDisplacement("Box", wavelength: 0, cancel: Cancel)));
        Assert.Contains("shape must be one of wave, ripple", await Error(b.Tools.AddDisplacement("Box", "spiral", cancel: Cancel)));
        Assert.Contains("no displacement 3: its displacements are numbered 1 to 2", await Error(b.Tools.SetDisplacementTool("Box", 3, speed: 1, cancel: Cancel)));
        Assert.Contains("Give a setting to change", await Error(b.Tools.SetDisplacementTool("Box", 1, cancel: Cancel)));
        Assert.Contains("\"displacement.<number>.amplitude\"", await Error(b.Tools.SetKey("Box", "displacement.one", 1, 0, cancel: Cancel)));
        Assert.Equal(before, SceneJson.Write(b.Scene));

        // Removing the first renumbers the second; undo puts it back, keys and all.
        var first = box.Displacements[0];
        await b.Tools.RemoveDisplacement("Box", 1, Cancel);
        Assert.Same(ripple, Assert.Single(box.Displacements));
        Assert.Equal("ripple", (string?)(await Parse(b.Tools.GetScene(cancel: Cancel)))["items"]!.AsArray().Single(i => (string?)i!["name"] == "Box")!["displacements"]![0]!["shape"]);
        await b.Tools.UndoScene(Cancel);
        Assert.Equal(new[] { first, ripple }, box.Displacements);
        Assert.Equal(0.3, first.Amplitude.Evaluate(30));
        await b.Tools.UndoScene(Cancel);
        await b.Tools.UndoScene(Cancel);
        await b.Tools.UndoScene(Cancel);
        Assert.Equal((Axis.Y, 1.0), (first.Direction, first.Wavelength));
    }

    // ───────────────────────────── skelegons and bones ─────────────────────────────

    [Fact]
    public async Task Skelegons_become_a_chain_of_bones_resting_where_they_were_drawn()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 0.4, 2, 0.4 }, Center = new[] { 0.0, 1, 0 } }), Cancel);
        await b.Tools.AddPoints([[0, 0, 0], [0, 1, 0], [0, 2, 0]], Cancel);
        await b.Tools.AddPolygons([[8, 9]], "Skelegon_Arm", Cancel);
        await b.Tools.AddPolygons([[9, 10]], "Skelegon_Hand", Cancel);
        await b.Tools.AddObject(name: "Limb", cancel: Cancel);
        var r = await Parse(b.Tools.SkelegonsToBones("Limb", Cancel));
        var bones = r["bones"]!.AsArray();
        Assert.Equal(["Arm", "Hand"], bones.Select(x => (string?)x!["name"]));
        Assert.Null(bones[0]!["parent"]);
        Assert.Equal("Arm", (string?)bones[1]!["parent"]);
        Assert.Equal([0.0, 1, 0], bones[1]!["base"]!.AsArray().Select(x => (double)x!));
        Assert.Equal([0.0, 2, 0], bones[1]!["tip"]!.AsArray().Select(x => (double)x!));
        Assert.Equal(1, (double)bones[0]!["rest_length"]!);
        Assert.Equal("Agent: made bones from skelegons", b.Layout.History.UndoLabel);
        // At rest the object is unchanged; bending the hand moves only the top.
        var limb = (ObjectItem)b.Scene.Find("Limb")!;
        SamePoints(limb.BaseMesh().Points, limb.DeformedMesh(1).Points);
        await b.Tools.SetTransform("Hand", 1, rotation: [0, 90, 0], key: false, cancel: Cancel);
        var bent = limb.DeformedMesh(1).Points;
        Assert.Equal(Math.Sqrt(2), (bent[10] - new Vec3(0, 2, 0)).Length, 6);
        Assert.True((bent[9] - new Vec3(0, 1, 0)).Length < 1e-9);
        Assert.True(bent[8].Length < 1e-9);

        Assert.Contains("Camera is a camera, not an object", await Error(b.Tools.SkelegonsToBones("Camera", Cancel)));
        await b.Tools.SetLayers([2], cancel: Cancel);
        await b.Tools.RunTool("Ball", null, Cancel);
        await b.Tools.AddObject(layer: 2, name: "Plain", cancel: Cancel);
        Assert.Contains("Plain has no skelegons", await Error(b.Tools.SkelegonsToBones("Plain", Cancel)));

        // Undo Plain, the hand's bend, then the bones.
        for (int i = 0; i < 3; i++) await b.Tools.UndoScene(Cancel);
        Assert.Empty(limb.Bones);
        Assert.Null(b.Scene.Find("Arm"));
    }

    [Fact]
    public async Task Set_item_changes_a_bones_rest_pose_rests_it_here_and_resets_its_pose()
    {
        using var b = await WithBox();
        await b.Tools.AddBone("Box", "Spine", [0, -1, 0], [0, -90, 0], 2, cancel: Cancel);
        var bone = (Bone)b.Scene.Find("Spine")!;
        await b.Tools.SetTransform("Spine", 10, rotation: [0, -60, 0], key: true, cancel: Cancel);

        var r = await Parse(b.Tools.SetItem("Spine", rest_position: [0, -0.5, 0], rest_rotation: [10, -80, 0], cancel: Cancel));
        Assert.Equal([0.0, -0.5, 0], r["rest_position"]!.AsArray().Select(x => (double)x!));
        Assert.Equal(new Vec3(10, -80, 0), bone.RestRotation);
        // The pose (its channels) stays where it was.
        Assert.Equal(-60, bone.RotationAt(10).Y, 9);

        // Rest Here: the pose at frame 10 becomes the rest pose.
        await b.Tools.SetItem("Spine", rest_at_frame: 10, cancel: Cancel);
        Assert.Equal(-60, bone.RestRotation.Y, 9);
        Assert.Equal(-1, bone.RestPosition.Y, 9);
        var box = Box(b);
        SamePoints(box.BaseMesh().Points, box.DeformedMesh(10).Points);

        // Reset pose: the channels go back to rest, unanimated.
        await b.Tools.SetItem("Spine", reset_pose: true, cancel: Cancel);
        Assert.Empty(Assert.IsType<JsonArray>((await Parse(b.Tools.GetScene(cancel: Cancel)))["items"]!.AsArray().Single(i => (string?)i!["name"] == "Spine")!["keyed"]));
        Assert.Equal(-60, bone.RotationAt(1).Y, 9);

        Assert.Contains("rest_position is a setting of bones", await Error(b.Tools.SetItem("Camera", rest_position: [0, 0, 0], cancel: Cancel)));
        Assert.Contains("reset_pose is a setting of bones", await Error(b.Tools.SetItem("Box", reset_pose: true, cancel: Cancel)));
        Assert.Contains("not both", await Error(b.Tools.SetItem("Spine", rest_position: [0, 0, 0], rest_at_frame: 1, cancel: Cancel)));
        Assert.Contains("outside the scene's frames", await Error(b.Tools.SetItem("Spine", rest_at_frame: 500, cancel: Cancel)));
        Assert.Contains("rest_rotation must be three numbers", await Error(b.Tools.SetItem("Spine", rest_rotation: [1, 2], cancel: Cancel)));

        // Each was one step: undo goes back through them.
        await b.Tools.UndoScene(Cancel);
        Assert.Equal(-60, bone.RotationAt(10).Y, 9);
        await b.Tools.UndoScene(Cancel);
        Assert.Equal(new Vec3(10, -80, 0), bone.RestRotation);
        await b.Tools.UndoScene(Cancel);
        Assert.Equal(new Vec3(0, -90, 0), bone.RestRotation);
        Assert.Equal(new Vec3(0, -1, 0), bone.RestPosition);
    }

    private static void SamePoints(IReadOnlyList<Vec3> expected, IReadOnlyList<Vec3> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Assert.True((expected[i] - actual[i]).Length < 1e-9, $"Point {i}: expected {expected[i]}, got {actual[i]}.");
    }

    // ───────────────────────────── movies ─────────────────────────────

    [Fact]
    public async Task Render_animation_makes_a_movie_of_its_frames_with_ffmpeg()
    {
        if (MovieEncoder.Find() == null) Assert.Skip("ffmpeg isn't installed.");
        using var b = await WithBox();
        await b.Tools.SetTransform("Box", 1, rotation: [0, 0, 0], key: true, cancel: Cancel);
        await b.Tools.SetTransform("Box", 13, rotation: [90, 0, 0], key: true, cancel: Cancel);
        // Frames 3, 6, 9 and 12, numbered by scene frame, joined in order.
        var r = await b.Tools.RenderAnimation("out", "spin", 3, 12, 3, width: 32, movie: "both", cancel: Cancel);
        var info = JsonNode.Parse(Text(r))!;
        Assert.Equal([b.Folder.File("out/spin.mp4"), b.Folder.File("out/spin.gif")], info["movies"]!.AsArray().Select(x => (string?)x));
        Assert.Null(info["movie_problem"]);
        Assert.Equal(10, (double)info["movie_fps"]!);
        Assert.True(new FileInfo(b.Folder.File("out/spin.mp4")).Length > 100);
        Assert.Equal("GIF89a"u8.ToArray(), File.ReadAllBytes(b.Folder.File("out/spin.gif"))[..6]);
        Assert.Empty(Directory.GetFiles(b.Folder.Path, "frame0001.png", SearchOption.AllDirectories));
        // Again, replacing its own files; someone else's movie only with overwrite.
        await b.Tools.RenderAnimation("out", "spin", 3, 12, 3, width: 32, movie: "mp4", cancel: Cancel);
        File.WriteAllBytes(b.Folder.File("theirs.gif"), [1]);
        Assert.Contains("theirs.gif already exists", await Error(b.Tools.RenderAnimation(".", "theirs", 1, 1, width: 32, movie: "gif", cancel: Cancel)));
        await b.Tools.RenderAnimation(".", "theirs", 1, 1, width: 32, movie: "gif", overwrite: true, cancel: Cancel);
        Assert.True(new FileInfo(b.Folder.File("theirs.gif")).Length > 10);
    }

    [Fact]
    public async Task Without_ffmpeg_render_animation_saves_the_frames_and_says_so()
    {
        using var b = await WithBox();
        b.Tools.Movies = new MovieEncoder(null);
        var r = await b.Tools.RenderAnimation("out", "f", 1, 2, width: 32, movie: "mp4", cancel: Cancel);
        var info = JsonNode.Parse(Text(r))!;
        Assert.Empty(info["movies"]!.AsArray());
        Assert.Equal(MovieEncoder.NotInstalled, (string?)info["movie_problem"]);
        Assert.True(File.Exists(b.Folder.File("out/f0002.png")));
        Assert.False(File.Exists(b.Folder.File("out/f.mp4")));
        // Without movie, nothing is said about one.
        Assert.Null(JsonNode.Parse(Text(await b.Tools.RenderAnimation("out", "f", 1, 2, width: 32, cancel: Cancel)))!["movie_problem"]);
        Assert.Contains("movie must be mp4, gif or both", await Error(b.Tools.RenderAnimation(movie: "avi", cancel: Cancel)));
    }
}
