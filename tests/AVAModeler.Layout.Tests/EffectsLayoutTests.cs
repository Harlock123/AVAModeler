using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>The render effects in Layout: undo, focusing the camera on an item, what the views draw, and Render Animation.</summary>
public class EffectsLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avamodeler-effects-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A square a metre across round a point, facing the camera (-Z).</summary>
    private static Document Box(Vec3 centre)
    {
        var doc = new Document();
        var m = doc.Current.Mesh;
        int a = m.AddPoint(centre + new Vec3(-0.5, -0.5, 0)), b = m.AddPoint(centre + new Vec3(0.5, -0.5, 0));
        int c = m.AddPoint(centre + new Vec3(0.5, 0.5, 0)), d = m.AddPoint(centre + new Vec3(-0.5, 0.5, 0));
        m.Polygons.Add(new Polygon([a, d, c, b], 0));
        m.Touch();
        return doc;
    }

    [Fact]
    public void Changing_the_effects_is_an_undo_step_that_puts_back_every_setting()
    {
        var s = new LayoutSession();
        var cam = s.Scene.CurrentCamera!;
        var light = s.Scene.Lights.First();
        s.Edit("Effects", () =>
        {
            s.Scene.Occlusion = new AmbientOcclusion { On = true };
            s.Scene.Environment = new EnvironmentSettings { Image = "builtin:sky", Lighting = true };
            cam.DepthOfField = new DepthOfField { On = true, FocalDistance = 2 };
            cam.MotionBlur = new MotionBlur { On = true };
            (light.Type, light.AreaWidth, light.Radius, light.ShadowSamples) = (LightType.Area, 3, 0.5, 40);
            s.Scene.Notify();
        });
        Assert.Equal("Effects", s.History.UndoLabel);
        s.Undo();
        Assert.False(s.Scene.Occlusion.On || s.Scene.Environment.On || cam.DepthOfField.On || cam.MotionBlur.On);
        Assert.Equal((LightType.Distant, 1.0, 0.0, 16), (light.Type, light.AreaWidth, light.Radius, light.ShadowSamples));
        s.Redo();
        Assert.True(s.Scene.Occlusion.On && s.Scene.Environment.Lighting && cam.MotionBlur.On);
        Assert.Equal(2, cam.DepthOfField.FocalDistance);
        Assert.Equal((LightType.Area, 3.0, 40), (light.Type, light.AreaWidth, light.ShadowSamples));
    }

    [Fact]
    public void Focus_on_sets_the_focal_distance_to_an_items_depth_as_one_step()
    {
        var s = new LayoutSession();
        var cam = s.Scene.CurrentCamera!; // at z = -5, looking along +Z
        var box = s.Scene.AddObject("Box", Box(new Vec3(1, 0.5, 2)));
        double d = s.FocusOn(cam, box);
        Assert.Equal(7, d, 9);
        Assert.True(cam.DepthOfField.On);
        Assert.Equal(7, cam.DepthOfField.FocalDistance, 9);
        Assert.Equal("Focus Camera on Box", s.History.UndoLabel);
        // A null focuses on its pivot; one behind the camera can't be focused on.
        var n = s.Add(new NullItem("Behind"));
        n.SetPosition(0, new Vec3(0, 0, -8));
        Assert.Contains("behind", Assert.Throws<ArgumentException>(() => s.FocusOn(cam, n)).Message);
        Assert.Throws<ArgumentException>(() => s.FocusOn(cam, cam));
        s.Undo();
        s.Undo();
        Assert.False(cam.DepthOfField.On);
    }

    [Fact]
    public void Views_draw_an_area_lights_rectangle_and_a_soft_lights_size()
    {
        var area = new LightItem("Area", LightType.Area) { AreaWidth = 2, AreaHeight = 0.5 };
        var lines = SceneDrawing.Glyph(area).ToList();
        // The rectangle's corners are where its size puts them.
        Assert.Contains(lines, l => l.Item1 == new Vec3(-1, -0.25, 0) && l.Item2 == new Vec3(1, -0.25, 0));
        Assert.Contains(lines, l => l.Item1 == new Vec3(1, 0.25, 0) && l.Item2 == new Vec3(-1, 0.25, 0));
        var point = new LightItem("Point", LightType.Point);
        int plain = SceneDrawing.Glyph(point).Count();
        point.Radius = 0.4;
        var soft = SceneDrawing.Glyph(point).ToList();
        Assert.True(soft.Count > plain);
        Assert.Contains(soft, l => Math.Abs(l.Item1.Length - 0.4) < 1e-9 && Math.Abs(l.Item2.Length - 0.4) < 1e-9);
    }

    [Fact]
    public void A_missing_environment_image_is_reported_with_the_image_problems()
    {
        var s = new LayoutSession();
        s.Scene.Environment = new EnvironmentSettings { Image = Path.Combine(_dir, "nowhere.hdr") };
        Assert.Contains(s.ImageProblems(), p => p.Contains("nowhere.hdr") && p.Contains("backdrop"));
        s.Scene.Environment = new EnvironmentSettings { Image = "builtin:studio" };
        Assert.Empty(s.ImageProblems());
    }

    [Fact]
    public async Task Render_animation_renders_with_motion_blur()
    {
        var s = new LayoutSession();
        var box = s.Scene.AddObject("Box", Box(Vec3.Zero));
        box[Channel.PositionX].AddKey(1, -2);
        box[Channel.PositionX].AddKey(3, 2);
        s.Scene.FirstFrame = 1;
        s.Scene.LastFrame = 3;
        var settings = new AnimationSettings { First = 1, Last = 3, Width = 40, Height = 30, Samples = 1, Folder = _dir };
        var sharp = await new AnimationRender(s.Scene, settings with { Name = "sharp" }).RunAsync(cancel: TestContext.Current.CancellationToken);
        s.Scene.CurrentCamera!.MotionBlur = new MotionBlur { On = true, Shutter = 1, Samples = 8 };
        var blurred = await new AnimationRender(s.Scene, settings with { Name = "blur" }).RunAsync(cancel: TestContext.Current.CancellationToken);
        Assert.Equal(3, blurred.Files.Count);
        static int PartlyCovered(string png)
        {
            var img = TextureImages.Decode(File.ReadAllBytes(png))!;
            return Enumerable.Range(0, img.Width * img.Height).Count(i => img.Rgba[i * 4] is > 20 and < 200);
        }
        // The middle frame moves fastest: blurred, more of it is a smear between the box and the backdrop.
        Assert.True(PartlyCovered(blurred.Files[1]) > PartlyCovered(sharp.Files[1]) + 10);
    }

    [Fact]
    public void Gltf_export_gives_an_area_light_as_a_wide_spot_and_says_so()
    {
        var scene = Scene.Scene.CreateDefault();
        scene.AddLight("Panel", LightType.Area);
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "scene.gltf");
        var warnings = SceneGltf.Save(scene, path);
        Assert.Contains(warnings, w => w.Contains("Panel is an area light"));
        string json = File.ReadAllText(path);
        Assert.Contains("\"outerConeAngle\": 1.570796327", json.Replace("\":1.", "\": 1."));
    }
}
