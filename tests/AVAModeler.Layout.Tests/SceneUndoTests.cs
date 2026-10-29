using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>Layout's undo: each kind of step undone and redone exactly, drags as one step, the depth, and clearing.</summary>
public class SceneUndoTests
{
    /// <summary>A box with a skelegon (a 2-point polygon on a "Skelegon" surface) running up its middle.</summary>
    private static Document Box()
    {
        var doc = new Document();
        var m = doc.Current.Mesh;
        var p = new[] { new Vec3(-1, -1, -1), new Vec3(1, -1, -1), new Vec3(1, 1, -1), new Vec3(-1, 1, -1) };
        int a = m.AddPoint(p[0]), b = m.AddPoint(p[1]), c = m.AddPoint(p[2]), d = m.AddPoint(p[3]);
        m.Polygons.Add(new Polygon([a, d, c, b], 0));
        doc.Surfaces.Add(new Surface("Skelegon Spine"));
        int s0 = m.AddPoint(new Vec3(0, -1, 0)), s1 = m.AddPoint(new Vec3(0, 1, 0));
        m.Polygons.Add(new Polygon([s0, s1], 1));
        m.Touch();
        return doc;
    }

    /// <summary>Everything about the scene, as its file keeps it (meshes, morphs and displacements included).</summary>
    private static string State(LayoutSession s) => SceneJson.Write(s.Scene, embedMeshes: true);

    /// <summary>Makes one step, then checks undo puts the scene back exactly and redo makes it again exactly.</summary>
    private static void UndoesAndRedoes(LayoutSession s, string label, Action step)
    {
        var before = State(s);
        int depth = s.History.Count;
        step();
        var after = State(s);
        Assert.NotEqual(before, after);
        Assert.Equal(depth + 1, s.History.Count);
        Assert.Equal(label, s.History.UndoLabel);
        Assert.Equal(label, s.Undo());
        Assert.Equal(before, State(s));
        Assert.Equal(label, s.History.RedoLabel);
        Assert.Equal(label, s.Redo());
        Assert.Equal(after, State(s));
        Assert.Equal(depth + 1, s.History.Count);
        Assert.False(s.History.CanRedo);
    }

    private static LayoutSession WithObject(out ObjectItem obj)
    {
        var s = new LayoutSession();
        obj = s.ShowModelerObject(Box());
        return s;
    }

    [Fact]
    public void Each_kind_of_step_undoes_and_redoes_exactly()
    {
        var s = WithObject(out var obj);
        Assert.Equal("Add Modeler Object", s.History.UndoLabel);
        var cam = s.Scene.Cameras.Single();
        var light = s.Scene.Lights.Single();

        UndoesAndRedoes(s, "Move Camera", () => s.SetVector(cam, 0, new Vec3(1, 2, -6)));
        s.SetFrame(30);
        UndoesAndRedoes(s, "Rotate Camera", () => s.SetVector(cam, 3, new Vec3(20, 10, 0)));
        UndoesAndRedoes(s, "Size Modeler object", () => s.SetVector(obj, 6, new Vec3(2, 2, 2)));
        s.Select(light);
        UndoesAndRedoes(s, "Create Key", s.CreateKey);
        UndoesAndRedoes(s, "Move Camera", () => s.SetVector(cam, 0, new Vec3(3, 2, -6)));
        s.SetFrame(1);
        s.Select(cam);
        UndoesAndRedoes(s, "Delete Key", s.DeleteKey);
        UndoesAndRedoes(s, "Add Light", () => s.Add(new LightItem("Spot", LightType.Spot)));
        UndoesAndRedoes(s, "Add Null", () => s.Add(new NullItem("Null")));
        UndoesAndRedoes(s, "Parent Camera", () => s.Edit("Parent Camera", () => cam.Parent = s.Scene.Find("Null")));
        UndoesAndRedoes(s, "Rename Light", () => s.Edit("Rename Light", () => { light.Name = "Key"; s.Scene.Notify(); }));
        UndoesAndRedoes(s, "Light Colour", () => s.Edit("Light Colour", () => { light.Color = new Vec3(1, 0.5, 0); light.Intensity.AddKey(1, 2); s.Scene.Notify(); }));
        UndoesAndRedoes(s, "Skelegons → Bones", () => Assert.Single(s.Edit("Skelegons → Bones", () => Skelegons.CreateBones(s.Scene, obj))));
        var bone = obj.Bones.Single();
        UndoesAndRedoes(s, "Rest Length", () => s.Edit("Rest Length", () => { bone.RestLength = 3; bone.Active = false; s.Scene.Notify(); }));
        UndoesAndRedoes(s, "Scene Settings", () => s.Edit("Scene Settings", () =>
        {
            (s.Scene.FirstFrame, s.Scene.LastFrame, s.Scene.FramesPerSecond) = (0, 120, 24);
            (s.Scene.AmbientColor, s.Scene.AmbientIntensity, s.Scene.GradientBackground, s.Scene.BackgroundColor) = (new Vec3(1, 0, 0), 0.5, true, new Vec3(0, 0, 1));
            (s.Scene.Fog, s.Scene.FogMinDistance, s.Scene.FogMaxDistance, s.Scene.FogColor) = (FogType.Linear, 2, 20, Vec3.One);
            s.Scene.Notify();
        }));
        UndoesAndRedoes(s, "Add Camera", () => s.Add(new CameraItem("Second")));
        UndoesAndRedoes(s, "Use Camera", () => s.Edit("Use Camera", () => s.Scene.CurrentCamera = (CameraItem)s.Scene.Find("Second")!));
        UndoesAndRedoes(s, "Zoom", () => s.Edit("Zoom", () => { cam.Zoom.AddKey(10, 5); cam.Width = 320; }));

        // Morphs and displacements (and their settings) are steps too.
        UndoesAndRedoes(s, "Add Morph", () => s.Edit("Add Morph", () => obj.AddMorph(new MorphTarget("Squash", obj.BaseMesh()))));
        var morph = obj.Morphs.Single();
        UndoesAndRedoes(s, "Morph Weight", () => s.Edit("Morph Weight", () => { morph.Weight.AddKey(10, 0.5); morph.Name = "Squish"; s.Scene.Notify(); }));
        UndoesAndRedoes(s, "Add Ripple", () => s.Edit("Add Ripple", () => obj.AddDisplacement(new WaveDisplacement { Shape = WaveShape.Ripple })));
        var ripple = obj.Displacements.Single();
        UndoesAndRedoes(s, "Ripple", () => s.Edit("Ripple", () => { ripple.Wavelength = 3; ripple.Speed = 0.1; ripple.Amplitude.AddKey(5, 1); s.Scene.Notify(); }));
        UndoesAndRedoes(s, "Remove Morph", () => s.Edit("Remove Morph", () => obj.RemoveMorph(morph)));

        // Removing an object takes its bones; undoing brings back the very items, and redoing takes them again.
        s.Select(obj);
        UndoesAndRedoes(s, "Remove Modeler object", () => s.Remove(obj));
        Assert.Null(obj.Scene);
        Assert.NotSame(obj, s.Selected);
        s.Undo();
        Assert.Same(s.Scene, obj.Scene);
        Assert.Same(s.Scene, bone.Scene);
        Assert.Same(bone, obj.Bones.Single());
    }

    [Fact]
    public void A_drag_is_one_step_from_press_to_release()
    {
        var s = new LayoutSession();
        var cam = s.Scene.Cameras.Single();
        var start = State(s);
        s.BeginEdit("Move Camera");
        Assert.True(s.Editing);
        for (int i = 1; i <= 10; i++) s.SetVector(cam, 0, new Vec3(i * 0.1, 0, -5));
        Assert.Equal(0, s.History.Count);
        s.EndEdit();
        Assert.False(s.Editing);
        Assert.Equal(1, s.History.Count);
        Assert.Equal("Move Camera", s.History.UndoLabel);
        var moved = State(s);
        s.Undo();
        Assert.Equal(start, State(s));
        s.Redo();
        Assert.Equal(moved, State(s));

        // A press and release that moves nothing is no step.
        s.BeginEdit("Move Camera");
        s.EndEdit();
        Assert.Equal(1, s.History.Count);

        // An agent's call in the middle of a drag is a step of its own: the drag so far is one, and the rest another.
        s.BeginEdit("Move Camera");
        s.SetVector(cam, 0, new Vec3(3, 0, -5));
        s.Edit("Agent: added a null", () => s.Scene.AddNull("Agent's"), apart: true);
        s.SetVector(cam, 0, new Vec3(4, 0, -5));
        s.EndEdit();
        Assert.Equal(4, s.History.Count);
        Assert.Equal("Move Camera", s.Undo());
        Assert.Equal(3, cam.PositionAt(1).X, 9);
        Assert.Equal("Agent: added a null", s.Undo());
        Assert.Null(s.Scene.Find("Agent's"));
        Assert.Equal("Move Camera", s.Undo());
        Assert.Equal(1, cam.PositionAt(1).X, 9);

        // Undo ends a drag left under way.
        s.BeginEdit("Rotate Camera");
        s.SetVector(cam, 3, new Vec3(45, 0, 0));
        Assert.Equal("Rotate Camera", s.Undo());
        Assert.False(s.Editing);
        Assert.Equal(0, cam.RotationAt(1).X, 9);
    }

    [Fact]
    public void A_failed_edit_puts_the_scene_back_and_leaves_no_step()
    {
        var s = new LayoutSession();
        s.Add(new NullItem("N"));
        var before = State(s);
        int depth = s.History.Count;
        var cam = s.Scene.Cameras.Single();
        Assert.Throws<InvalidOperationException>(() => s.Edit("Bad", () =>
        {
            cam.Name = "Renamed";
            s.Scene.AddNull("Half done");
            throw new InvalidOperationException("Part way.");
        }));
        Assert.Equal(before, State(s));
        Assert.Equal(depth, s.History.Count);
        Assert.Equal("Add Null", s.History.UndoLabel);
        // Undo and redo can't be used inside an edit.
        Assert.Throws<InvalidOperationException>(() => s.Edit("Undo", () => { s.Undo(); }));
        Assert.Equal(depth, s.History.Count);
    }

    [Fact]
    public void The_history_keeps_at_most_its_limit()
    {
        var s = new LayoutSession();
        Assert.Equal(100, s.History.Limit);
        s.History.Limit = 5;
        var states = new List<string> { State(s) };
        for (int i = 0; i < 8; i++)
        {
            s.Add(new NullItem($"N{i}"));
            states.Add(State(s));
        }
        Assert.Equal(5, s.History.Count);
        while (s.History.CanUndo) s.Undo();
        // The three oldest steps were let go: undo stops after the fourth null went in.
        Assert.Equal(states[3], State(s));
        Assert.Null(s.Undo());
        Assert.Equal(5, s.History.RedoCount);
    }

    [Fact]
    public void A_new_or_loaded_scene_clears_the_history()
    {
        string path = Path.Combine(Path.GetTempPath(), "avamodeler-undo-" + Guid.NewGuid().ToString("N")[..8] + ".avascene");
        try
        {
            var s = WithObject(out _);
            s.Add(new NullItem("N"));
            s.SaveScene(path);
            s.Undo();
            Assert.True(s.History.CanRedo);
            s.LoadScene(path);
            Assert.False(s.History.CanUndo);
            Assert.False(s.History.CanRedo);
            Assert.Null(s.Undo());

            s.Add(new NullItem("M"));
            var doc = Box();
            s.NewScene(doc);
            Assert.False(s.History.CanUndo);
            Assert.False(s.Modified);
            // The new scene has the Modeler's object, chosen, and it isn't a step to undo.
            Assert.Same(doc, s.Scene.Objects.Single().Document);
            Assert.Same(s.Scene.Objects.Single(), s.Selected);

            s.Add(new NullItem("O"));
            s.Replace(Scene.Scene.CreateDefault());
            Assert.Equal(0, s.History.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Undoing_back_to_the_saved_scene_makes_it_unmodified()
    {
        string path = Path.Combine(Path.GetTempPath(), "avamodeler-undo-" + Guid.NewGuid().ToString("N")[..8] + ".avascene");
        try
        {
            var s = new LayoutSession();
            Assert.False(s.Modified);
            s.Add(new NullItem("A"));
            Assert.True(s.Modified);
            s.Undo();
            Assert.False(s.Modified);
            s.Redo();
            Assert.True(s.Modified);
            s.SaveScene(path);
            Assert.False(s.Modified);
            s.Add(new NullItem("B"));
            s.Add(new NullItem("C"));
            s.Undo();
            Assert.True(s.Modified);
            s.Undo();
            Assert.False(s.Modified);
            s.Undo();
            Assert.True(s.Modified);
            s.Redo();
            Assert.False(s.Modified);
            // A change that isn't a step (made straight on the scene) still counts as one.
            s.Scene.AddNull("Unrecorded");
            Assert.True(s.Modified);
            s.Redo();
            Assert.True(s.Modified);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Steps_are_told_and_named()
    {
        var s = new LayoutSession();
        int told = 0;
        s.History.Changed += () => told++;
        s.Add(new LightItem("Point", LightType.Point));
        Assert.Equal(1, told);
        Assert.True(s.History.CanUndo);
        Assert.False(s.History.CanRedo);
        Assert.Null(s.History.RedoLabel);
        s.Undo();
        Assert.Equal(2, told);
        Assert.Contains("Undid Add Light", s.Status);
        Assert.Equal("Add Light", s.History.RedoLabel);
        s.Redo();
        Assert.Contains("Redid Add Light", s.Status);
        // Changing nothing is no step.
        s.Edit("Nothing", () => { });
        Assert.Equal(1, s.History.Count);
        Assert.Equal("Add Light", s.History.UndoLabel);
    }
}
