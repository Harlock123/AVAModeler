using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>Layout's session without its controls: keying, the modified flag, files, and what the views draw.</summary>
public class LayoutSessionTests
{
    private static Document Box()
    {
        var doc = new Document();
        var m = doc.Current.Mesh;
        var p = new[] { new Vec3(-1, -1, -1), new Vec3(1, -1, -1), new Vec3(1, 1, -1), new Vec3(-1, 1, -1) };
        int a = m.AddPoint(p[0]), b = m.AddPoint(p[1]), c = m.AddPoint(p[2]), d = m.AddPoint(p[3]);
        m.Polygons.Add(new Polygon([a, d, c, b], 0));
        m.Touch();
        return doc;
    }

    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), "avamodeler-layout-" + Guid.NewGuid().ToString("N")[..8] + "-" + name);

    [Fact]
    public void The_first_key_replaces_a_channels_constant()
    {
        var scene = Scene.Scene.CreateDefault();
        var cam = scene.Cameras.Single();
        var x = cam[Channel.PositionX];
        Assert.True(Keying.IsConstant(x, scene));
        Keying.Set(x, scene, 1, 2, key: true, "x");
        Assert.Equal([new EnvelopeKey(1, 2)], x.Keys);
        Assert.True(Keying.IsConstant(x, scene) == false);
        Keying.Set(x, scene, 60, 8, key: true, "x");
        Assert.Equal(2, x.Keys.Count);
        Assert.Equal(5, x.Evaluate(30.5), 9);

        // A key at the same frame keeps its shape; set_key's explicit key doesn't.
        x.SetKey(1, x.Keys[1] with { Tension = 0.5 });
        Keying.Set(x, scene, 60, 9, key: true, "x");
        Assert.Equal(new EnvelopeKey(60, 9, 0.5), x.Keys[1]);
        Keying.Key(x, scene, new EnvelopeKey(60, 9), keepShape: false);
        Assert.Equal(new EnvelopeKey(60, 9), x.Keys[1]);
    }

    [Fact]
    public void Without_a_key_a_value_changes_the_constant_or_an_existing_key()
    {
        var scene = Scene.Scene.CreateDefault();
        var y = scene.Cameras.Single()[Channel.PositionY];
        Keying.Set(y, scene, 30, 4, key: false, "y");
        Assert.Single(y.Keys);
        Assert.Equal(4, y.Evaluate(1));
        Keying.Set(y, scene, 1, 0, key: true, "y");
        Keying.Set(y, scene, 60, 10, key: true, "y");
        Keying.Set(y, scene, 60, 12, key: false, "y");
        Assert.Equal(12, y.Evaluate(60));
        var e = Assert.Throws<InvalidOperationException>(() => Keying.Set(y, scene, 30, 1, key: false, "Camera's position.y"));
        Assert.Contains("keys at 1 and 60", e.Message);
        Assert.Contains("key true", e.Message);
        Assert.Equal("", Keying.Frames(new Envelope()));
    }

    [Fact]
    public void Layouts_own_keys_follow_the_same_rule_as_agents()
    {
        var s = new LayoutSession();
        var cam = s.Scene.Cameras.Single();
        var x = cam[Channel.PositionX];

        // Auto-key: the first key replaces the frame-0 constant, so the motion starts at the first frame keyed.
        s.SetFrame(10);
        s.SetChannel(cam, Channel.PositionX, 2);
        s.SetFrame(40);
        s.SetChannel(cam, Channel.PositionX, 8);
        Assert.Equal([10.0, 40.0], x.Keys.Select(k => k.Frame));
        Assert.Equal(2, cam.PositionAt(1).X, 9);

        // Auto-key off: a channel with one key or none changes throughout; an animated one changes its key here,
        // and gets one where it has none, so the change isn't lost.
        s.AutoKey = false;
        s.SetChannel(cam, Channel.PositionY, 3);
        Assert.Single(cam[Channel.PositionY].Keys);
        Assert.Equal(3, cam.PositionAt(50).Y);
        s.SetChannel(cam, Channel.PositionX, 9);
        Assert.Equal([10.0, 40.0], x.Keys.Select(k => k.Frame));
        Assert.Equal(9, x.Evaluate(40));
        s.SetFrame(25);
        s.SetChannel(cam, Channel.PositionX, 5);
        Assert.Equal([10.0, 25.0, 40.0], x.Keys.Select(k => k.Frame));

        // Create Key replaces the constants of channels never keyed too.
        var light = s.Scene.Lights.Single();
        s.Select(light);
        s.SetFrame(5);
        s.CreateKey();
        Assert.All(light.Channels, c => Assert.Equal([5.0], c.Keys.Select(k => k.Frame)));

        // Delete Key keeps a channel's only key, which holds its value.
        var y = light.PositionAt(5).Y;
        s.DeleteKey();
        Assert.All(light.Channels, c => Assert.Single(c.Keys));
        Assert.Equal(y, light.PositionAt(5).Y);
        Assert.Contains("only key", s.Status);
        s.Select(cam);
        s.SetFrame(25);
        s.DeleteKey();
        Assert.Equal([10.0, 40.0], x.Keys.Select(k => k.Frame));
    }

    [Fact]
    public void The_scene_is_modified_until_saved_and_saves_say_what_they_drop()
    {
        var s = new LayoutSession();
        Assert.False(s.Modified);
        var doc = Box();
        s.ShowModelerObject(doc);
        Assert.True(s.Modified);
        // The whole object goes in once; a single layer of it is another item.
        Assert.Same(s.Scene.Objects.Single(), s.ShowModelerObject(doc));
        s.Scene.AddObject("Layer 0", doc, 0);
        Assert.Same(s.Scene.Objects.First(), s.ShowModelerObject(doc));

        string lws = Temp("scene.lws"), json = Temp("scene.avascene");
        try
        {
            var warnings = s.SaveScene(lws);
            Assert.Contains(warnings, w => w.Contains("no file"));
            Assert.False(s.Modified);
            s.Scene.AddNull("N");
            Assert.True(s.Modified);
            Assert.Empty(s.SaveScene(json));

            // Loading goes through the loader given, and replaces the scene only once it's read.
            var t = new LayoutSession();
            var asked = new List<string>();
            t.LoadScene(lws, p => { asked.Add(p); return null; });
            Assert.NotEmpty(asked);
            Assert.Contains(t.Warnings, w => w.Contains("placeholder"));
            Assert.False(t.Modified);
            t.LoadScene(json);
            Assert.Equal(s.Scene.Items.Select(i => i.Name), t.Scene.Items.Select(i => i.Name));
            Assert.Equal(4, t.Scene.Objects.First().BaseMesh().Points.Count);
            File.WriteAllText(json, "not a scene");
            Assert.ThrowsAny<Exception>(() => t.LoadScene(json));
            Assert.Equal(s.Scene.Items.Count, t.Scene.Items.Count);
        }
        finally
        {
            File.Delete(lws);
            File.Delete(json);
        }
    }

    [Fact]
    public void Revalidate_lets_go_of_an_item_no_longer_in_the_scene()
    {
        var s = new LayoutSession();
        var n = s.Scene.AddNull("N");
        var snap = s.Scene.Snapshot();
        s.Select(n);
        s.Scene.Remove(n);
        s.Revalidate();
        Assert.NotSame(n, s.Selected);
        snap.Restore();
        Assert.Same(s.Scene, n.Scene);
        int told = 0;
        s.ChangedElsewhere += () => told++;
        s.NotifyChangedElsewhere();
        Assert.Equal(1, told);
    }

    [Fact]
    public void Looking_through_a_camera_puts_what_it_faces_in_the_middle()
    {
        var scene = Scene.Scene.CreateDefault();
        var cam = scene.Cameras.Single();
        cam.SetPosition(0, new Vec3(4, 3, -6));
        var target = new Vec3(0, 0.5, 0);
        cam.SetRotation(0, cam.RotationToward(target, 1));
        var view = new ViewCamera(ViewKind.Perspective);
        view.Resize(640, 480);
        SceneDrawing.LookThrough(view, cam, 1);
        var middle = view.Project(target);
        Assert.True(middle.Visible);
        Assert.Equal(320, middle.X, 6);
        Assert.Equal(240, middle.Y, 6);
        // The camera's own right (+X) is to the right in the picture, and its up (+Y) is up.
        var toWorld = cam.CameraToWorld(1);
        var right = view.Project(target + toWorld.TransformVector(Vec3.UnitX));
        var up = view.Project(target + toWorld.TransformVector(Vec3.UnitY));
        Assert.True(right.X > 330);
        Assert.True(up.Y < 230);
        Assert.Equal(cam.VerticalFov(1), view.FieldOfView, 9);
    }

    [Fact]
    public void A_view_of_the_scene_has_the_objects_and_every_items_drawing()
    {
        var s = new LayoutSession();
        var obj = s.ShowModelerObject(Box());
        obj.Visible = true;
        var hidden = s.Scene.AddObject("Hidden", Box());
        hidden.Visible = false;
        s.Scene.AddLight("Spot", LightType.Spot);
        s.Select(obj);
        var frame = s.Scene.Evaluate(1);
        var view = s.BuildScene(frame, shaded: true);
        Assert.Single(view.Layers);
        Assert.Equal(DisplayStyle.ShadedWireframe, view.Style);
        // The chosen object's yellow box (12 lines), the camera (13), the two lights (6 and 3).
        Assert.Equal(12 + 13 + 6 + 6, view.Overlay.Count);
        Assert.Equal(12, view.Overlay.Count(l => l.Color == Palette.Selected));
        var box = SceneDrawing.Bounds(frame);
        Assert.True(box.Contains(new Vec3(0, 0, -5)));
        Assert.Equal(0xFFFFF0A0u, SceneDrawing.Color(s.Scene.Lights.First()));
    }
}
