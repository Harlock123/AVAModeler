using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>The render effects' settings in snapshots (undo), copies (rendering elsewhere), .avascene and .lws files.</summary>
public class EffectsFilesTests
{
    /// <summary>A scene with every effect's settings away from their defaults.</summary>
    private static Scene Set(string? environment = null)
    {
        var scene = Scene.CreateDefault();
        scene.Occlusion = new AmbientOcclusion { On = true, Samples = 24, Distance = 0.7, Strength = 0.8 };
        scene.Environment = new EnvironmentSettings { Image = environment ?? "builtin:sky", Intensity = 1.5, Heading = 40, Lighting = true, Samples = 12 };
        var area = scene.AddLight("Area", LightType.Area);
        area.AreaWidth = 2.5;
        area.AreaHeight = 0.5;
        area.ShadowSamples = 32;
        var point = scene.AddLight("Bulb", LightType.Point);
        point.Radius = 0.25;
        var cam = scene.CurrentCamera!;
        cam.DepthOfField = new DepthOfField { On = true, FocalDistance = 3.5, FStop = 1.4, Samples = 20 };
        cam.MotionBlur = new MotionBlur { On = true, Shutter = 0.75, Samples = 10 };
        return scene;
    }

    private static void SameEffects(Scene expected, Scene actual)
    {
        Assert.Equal(expected.Occlusion, actual.Occlusion);
        Assert.Equal(expected.Environment, actual.Environment);
        var (ea, aa) = ((LightItem)expected.Find("Area")!, (LightItem)actual.Find("Area")!);
        Assert.Equal((LightType.Area, 2.5, 0.5, 32), (aa.Type, aa.AreaWidth, aa.AreaHeight, aa.ShadowSamples));
        Assert.Equal(((LightItem)expected.Find("Bulb")!).Radius, ((LightItem)actual.Find("Bulb")!).Radius);
        Assert.Equal(expected.CurrentCamera!.DepthOfField, actual.CurrentCamera!.DepthOfField);
        Assert.Equal(expected.CurrentCamera.MotionBlur, actual.CurrentCamera.MotionBlur);
        Assert.Equal(ea.AreaWidth, aa.AreaWidth);
    }

    [Fact]
    public void A_snapshot_puts_back_every_effect_setting()
    {
        var scene = Set();
        var snap = scene.Snapshot();
        var reference = Set();
        scene.Occlusion = new AmbientOcclusion();
        scene.Environment = new EnvironmentSettings();
        var area = (LightItem)scene.Find("Area")!;
        (area.Type, area.AreaWidth, area.AreaHeight, area.ShadowSamples) = (LightType.Spot, 1, 1, 16);
        ((LightItem)scene.Find("Bulb")!).Radius = 0;
        scene.CurrentCamera!.DepthOfField = new DepthOfField();
        scene.CurrentCamera.MotionBlur = new MotionBlur();
        snap.Restore();
        SameEffects(reference, scene);
    }

    [Fact]
    public void A_copy_takes_every_effect_setting()
    {
        var scene = Set();
        var copy = scene.Copy();
        SameEffects(scene, copy);
        // The settings are records that never change, so sharing them is safe; changing the original's leaves the copy's.
        scene.Occlusion = scene.Occlusion with { On = false };
        Assert.True(copy.Occlusion.On);
    }

    [Fact]
    public void An_avascene_keeps_every_effect_setting()
    {
        var scene = Set();
        var back = SceneJson.Read(SceneJson.Write(scene)).Scene;
        SameEffects(scene, back);
        // Off, nothing is written, so older scenes and these read the same.
        var plain = Scene.CreateDefault();
        string text = SceneJson.Write(plain);
        Assert.DoesNotContain("occlusion", text);
        Assert.DoesNotContain("depthOfField", text);
        var again = SceneJson.Read(text).Scene;
        Assert.False(again.Occlusion.On || again.Environment.On || again.CurrentCamera!.DepthOfField.On || again.CurrentCamera.MotionBlur.On);
    }

    [Fact]
    public void An_avascene_stores_the_environment_file_beside_it_relatively()
    {
        using var dir = new TempDir();
        string env = Path.Combine(dir.Path, "maps", "studio.hdr");
        Directory.CreateDirectory(Path.GetDirectoryName(env)!);
        File.WriteAllBytes(env, Hdr.Write(new HdrImage(2, 1, [1, 1, 1, 0, 0, 0])));
        var scene = Set(env);
        string path = Path.Combine(dir.Path, "scene.avascene");
        SceneJson.WriteFile(scene, path);
        Assert.Contains("\"maps/studio.hdr\"", File.ReadAllText(path));
        var back = SceneJson.ReadFile(path).Scene;
        Assert.Equal(env, Path.GetFullPath(back.Environment.Image));
        Assert.Empty(back.Evaluate(0).Warnings);
    }

    [Fact]
    public void Lws_keeps_lightwaves_own_camera_settings_and_area_lights()
    {
        var scene = Set();
        string text = LwsWriter.Write(scene);
        Assert.Contains("LightType 4", text);
        Assert.Contains("DepthOfField 1", text);
        Assert.Contains("LensFStop 1.4", text);
        Assert.Contains("BlurLength 0.75", text);
        var back = LwsReader.Read(text, _ => null).Scene;
        var cam = back.CurrentCamera!;
        Assert.True(cam.DepthOfField.On);
        Assert.Equal(3.5, cam.DepthOfField.FocalDistance);
        Assert.Equal(1.4, cam.DepthOfField.FStop);
        Assert.True(cam.MotionBlur.On);
        Assert.Equal(0.75, cam.MotionBlur.Shutter);
        Assert.Equal(LightType.Area, ((LightItem)back.Find("Area")!).Type);
        // A scene without them writes none of those lines.
        string plain = LwsWriter.Write(Scene.CreateDefault());
        Assert.DoesNotContain("DepthOfField", plain);
        Assert.DoesNotContain("MotionBlur", plain);
    }

    [Fact]
    public void Lws_from_lightwave_5_reads_depth_of_field_and_motion_blur()
    {
        const string lw5 = "LWSC\n1\n\nFirstFrame 1\nLastFrame 30\n\nAddCamera\nCameraName Camera\nShowCamera 1 2\nCameraMotion (unnamed)\n  9\n  1\n  0 0 -5 0 0 0 1 1 1\n  0 0 0 0 0\nEndBehavior 1\nZoomFactor 3.2\nMotionBlur 1\nBlurLength 0.5\nDepthofField 1\nFocalDistance 2.5\nLensFStop 2\n";
        var cam = LwsReader.Read(lw5, _ => null).Scene.CurrentCamera!;
        Assert.Equal(new MotionBlur { On = true, Shutter = 0.5 }, cam.MotionBlur);
        Assert.Equal(new DepthOfField { On = true, FocalDistance = 2.5, FStop = 2 }, cam.DepthOfField);
    }
}
