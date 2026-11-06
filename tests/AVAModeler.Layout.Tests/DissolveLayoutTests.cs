using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>Object dissolve, the image list and compositing in Layout: the views, the channel, and undo steps.</summary>
public class DissolveLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avamodeler-dissolve-" + Guid.NewGuid().ToString("N")[..8]);

    public DissolveLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    private static Document Box()
    {
        var doc = new Document();
        var m = doc.Current.Mesh;
        int a = m.AddPoint(new Vec3(-0.5, -0.5, 0)), b = m.AddPoint(new Vec3(0.5, -0.5, 0));
        int c = m.AddPoint(new Vec3(0.5, 0.5, 0)), d = m.AddPoint(new Vec3(-0.5, 0.5, 0));
        m.Polygons.Add(new Polygon([a, d, c, b], 0));
        m.Touch();
        return doc;
    }

    private string Png(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Core.Png.Encode(1, 1, [10, 20, 30, 255]));
        return path;
    }

    [Fact]
    public void The_views_get_each_objects_dissolve_at_the_frame()
    {
        var s = new LayoutSession();
        var obj = s.Scene.AddObject("Box", Box());
        obj.Dissolve.SetKey(0, new EnvelopeKey(1, 0));
        obj.Dissolve.AddKey(new EnvelopeKey(11, 1, Linear: true));
        Assert.Equal(0.5, SceneDrawing.Build(s.Scene.Evaluate(6), s.Scene.Items, true).Layers.Single().Dissolve, 9);
        Assert.Equal(0, SceneDrawing.Build(s.Scene.Evaluate(1), s.Scene.Items, true).Layers.Single().Dissolve, 9);
        Assert.Equal(1, SceneDrawing.Build(s.Scene.Evaluate(40), s.Scene.Items, true).Layers.Single().Dissolve, 9);
        // A Modeler document's layers aren't dissolved.
        Assert.All(ViewScene.FromDocument(Box(), DisplayStyle.Shaded).Layers, l => Assert.Equal(0, l.Dissolve));
    }

    [Fact]
    public void Dissolve_is_an_objects_channel_and_setting_it_is_one_undo_step()
    {
        var s = new LayoutSession();
        var obj = s.Scene.AddObject("Box", Box());
        Assert.Same(obj.Dissolve, SceneChannels.Find(obj, SceneChannels.Dissolve));
        Assert.Null(SceneChannels.Find(s.Scene.Lights.First(), SceneChannels.Dissolve));
        s.AutoKey = false;
        s.SetDissolve(obj, 0.25);
        Assert.Equal(0.25, obj.DissolveAt(30));
        Assert.Equal("Set Box's Dissolve", s.History.UndoLabel);
        s.AutoKey = true;
        s.SetFrame(10);
        s.SetDissolve(obj, 1);
        Assert.Equal(1, obj.DissolveAt(10));
        Assert.Contains(SceneChannels.Keyed(obj, s.Scene), c => c.Name == "dissolve");
        s.Undo();
        Assert.Equal(0.25, obj.DissolveAt(10));
        Assert.Throws<ArgumentException>(() => s.SetDissolve(obj, 1.5));
    }

    [Fact]
    public void The_images_and_compositing_change_as_one_undo_step_and_a_bad_change_changes_nothing()
    {
        var s = new LayoutSession();
        string bg = Png("bg.png"), fg = Png("fg.png");
        s.SetImages([new SceneImage { Path = bg }, new SceneImage { Path = fg }], new CompositingSettings { Background = bg, Foreground = fg, Key = ForegroundKey.Alpha });
        Assert.Equal(2, s.Scene.Images.Count);
        Assert.Equal(bg, s.Scene.Compositing.Background);
        Assert.Equal(1, s.History.Count);

        // Taking out an image compositing uses, or compositing with one not in the list, is refused.
        Assert.Throws<ArgumentException>(() => s.SetImages([new SceneImage { Path = fg }]));
        Assert.Throws<ArgumentException>(() => s.SetCompositing(new CompositingSettings { Background = Png("other.png") }));
        Assert.Throws<ArgumentException>(() => s.SetImages([new SceneImage { Path = Path.Combine(_dir, "flat.png"), Sequence = true }]));
        Assert.Equal(1, s.History.Count);

        s.SetCompositing(new CompositingSettings());
        Assert.False(s.Scene.Compositing.On);
        s.Undo();
        Assert.Equal(fg, s.Scene.Compositing.Foreground);
        s.Undo();
        Assert.Empty(s.Scene.Images);
        Assert.False(s.Scene.Compositing.On);
    }

    [Fact]
    public void Missing_compositing_images_are_among_the_image_problems()
    {
        var s = new LayoutSession();
        string gone = Path.Combine(_dir, "gone.png");
        s.SetImages([new SceneImage { Path = gone }], new CompositingSettings { Background = gone });
        Assert.Contains(s.ImageProblems(), p => p.Contains("gone.png"));
    }
}
