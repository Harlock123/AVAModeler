using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>The render effects through the scene tools: set_scene, add_light, set_item, get_scene, render_scene and render_animation.</summary>
public class EffectsHandlerTests
{
    private static async Task<Bench> WithBox()
    {
        var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 2.0, 2, 2 } }), Cancel);
        await b.Tools.AddObject(cancel: Cancel);
        return b;
    }

    [Fact]
    public async Task Set_scene_turns_on_ambient_occlusion_and_the_environment_as_one_undo_step()
    {
        using var b = await WithBox();
        File.WriteAllBytes(b.Folder.File("sky.hdr"), Hdr.Write(new HdrImage(4, 2, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])));
        var s = await Parse(b.Tools.SetScene(ao: true, ao_samples: 32, ao_distance: 0.5, environment: "sky.hdr", environment_lighting: true, environment_heading: 30, cancel: Cancel));
        Assert.Equal(new AmbientOcclusion { On = true, Samples = 32, Distance = 0.5 }, b.Scene.Occlusion);
        Assert.Equal(b.Folder.File("sky.hdr"), b.Scene.Environment.Image);
        Assert.True(b.Scene.Environment.Lighting);
        Assert.True((bool)s["ambient_occlusion"]!["on"]!);
        Assert.Equal(0.5, (double)s["ambient_occlusion"]!["distance"]!);
        Assert.Equal(30, (double)s["environment"]!["heading"]!);
        Assert.Equal("Agent: changed the scene's settings", (string?)s["undo_scene"]!["next_undo"]);
        await b.Tools.UndoScene(Cancel);
        Assert.False(b.Scene.Occlusion.On);
        Assert.False(b.Scene.Environment.On);

        // Built-in environments need no file; "" takes it away.
        await b.Tools.SetScene(environment: "builtin:Studio", cancel: Cancel);
        Assert.Equal("builtin:studio", b.Scene.Environment.Image);
        await b.Tools.SetScene(environment: "", cancel: Cancel);
        Assert.False(b.Scene.Environment.On);
        Assert.Null((await Parse(b.Tools.GetScene(cancel: Cancel)))["environment"]);
    }

    [Fact]
    public async Task Bad_effect_settings_are_refused_and_change_nothing()
    {
        using var b = await WithBox();
        Assert.Contains("Can't find the environment image", await Error(b.Tools.SetScene(environment: "missing.hdr", cancel: Cancel)));
        Assert.Contains("builtin:sky", await Error(b.Tools.SetScene(environment: "builtin:moon", cancel: Cancel)));
        Assert.Contains("1 to 256", await Error(b.Tools.SetScene(ao: true, ao_samples: 0, cancel: Cancel)));
        Assert.Contains("metres", await Error(b.Tools.SetScene(ao_distance: -1, cancel: Cancel)));
        Assert.Contains("needs an environment", await Error(b.Tools.SetScene(environment_lighting: true, cancel: Cancel)));
        Assert.Contains("for area lights", await Error(b.Tools.SetItem("Light", area_size: [1, 1], cancel: Cancel)));
        Assert.Contains("for point and spot", await Error(b.Tools.SetItem("Light", radius: 0.5, cancel: Cancel)));
        Assert.Contains("f-stop", await Error(b.Tools.SetItem("Camera", f_stop: 0, cancel: Cancel)));
        Assert.Contains("not both", await Error(b.Tools.SetItem("Camera", f_stop: 2, aperture: 0.1, cancel: Cancel)));
        Assert.Contains("shutter", await Error(b.Tools.SetItem("Camera", motion_blur: true, shutter: 2, cancel: Cancel)));
        Assert.Contains("setting of cameras", await Error(b.Tools.SetItem("Light", depth_of_field: true, cancel: Cancel)));
        Assert.Contains("itself", await Error(b.Tools.SetItem("Camera", focus_on: "Camera", cancel: Cancel)));
        await b.Tools.AddNull("Behind", position: [0, 0, -9], cancel: Cancel);
        Assert.Contains("behind Camera", await Error(b.Tools.SetItem("Camera", focus_on: "Behind", cancel: Cancel)));
        await b.Tools.UndoScene(Cancel);
        Assert.False(b.Scene.Occlusion.On);
        Assert.False(b.Scene.CurrentCamera!.DepthOfField.On);
        Assert.False(b.Scene.CurrentCamera.MotionBlur.On);
        Assert.Equal(1, b.Layout.History.Count);
    }

    [Fact]
    public async Task Area_lights_and_soft_point_lights_through_add_light_and_set_item()
    {
        using var b = await WithBox();
        var area = await Parse(b.Tools.AddLight("area", "Panel", position: [0, 4, 0], look_at: [0, 0, 0], area_size: [2, 0.5], shadow_samples: 32, cancel: Cancel));
        Assert.Equal("area", (string?)area["type"]);
        Assert.Equal([2, 0.5], area["area_size"]!.AsArray().Select(x => (double)x!));
        Assert.Equal(32, (int)area["shadow_samples"]!);
        var light = Assert.IsType<LightItem>(b.Scene.Find("Panel"));
        Assert.Equal((LightType.Area, 2, 0.5, 32), (light.Type, light.AreaWidth, light.AreaHeight, light.ShadowSamples));

        var bulb = await Parse(b.Tools.AddLight("point", "Bulb", position: [2, 2, -2], radius: 0.3, cancel: Cancel));
        Assert.Equal(0.3, (double)bulb["radius"]!);
        await b.Tools.SetItem("Bulb", radius: 0, cancel: Cancel);
        Assert.False(((LightItem)b.Scene.Find("Bulb")!).IsSoft);
    }

    [Fact]
    public async Task Set_item_gives_the_camera_depth_of_field_and_motion_blur()
    {
        using var b = await WithBox();
        // The camera is 5 m back from the box's middle.
        var cam = await Parse(b.Tools.SetItem("Camera", focus_on: "Modeler object", f_stop: 0.5, dof_samples: 9, motion_blur: true, shutter: 0.25, blur_samples: 12, cancel: Cancel));
        Assert.True((bool)cam["depth_of_field"]!["on"]!);
        Assert.Equal(5, (double)cam["depth_of_field"]!["focal_distance"]!, 6);
        Assert.Equal(0.5, (double)cam["depth_of_field"]!["f_stop"]!);
        Assert.Equal(3.2 * 0.0075 / 0.5 / 2, (double)cam["depth_of_field"]!["aperture"]!, 6);
        Assert.Equal(0.25, (double)cam["motion_blur"]!["shutter"]!);
        var camera = b.Scene.CurrentCamera!;
        Assert.Equal(new DepthOfField { On = true, FocalDistance = 5, FStop = 0.5, Samples = 9 }, camera.DepthOfField with { FocalDistance = Math.Round(camera.DepthOfField.FocalDistance, 9) });
        Assert.Equal(new MotionBlur { On = true, Shutter = 0.25, Samples = 12 }, camera.MotionBlur);
        // The aperture in metres sets the f-stop at the current zoom.
        await b.Tools.SetItem("Camera", aperture: 0.012, cancel: Cancel);
        Assert.Equal(1, camera.DepthOfField.FStop, 9);
        // All of that one step each; undo takes back the last.
        await b.Tools.UndoScene(Cancel);
        Assert.Equal(0.5, camera.DepthOfField.FStop);
        await b.Tools.SetItem("Camera", depth_of_field: false, motion_blur: false, cancel: Cancel);
        Assert.False((bool)(await Parse(b.Tools.GetScene(cancel: Cancel)))["items"]![0]!["depth_of_field"]!["on"]!);
    }

    [Fact]
    public async Task Render_scene_and_render_animation_honour_the_effects()
    {
        using var b = await WithBox();
        await b.Tools.SetScene(ao: true, ao_samples: 4, environment: "builtin:sky", environment_lighting: true, environment_samples: 4, cancel: Cancel);
        await b.Tools.SetItem("Camera", depth_of_field: true, f_stop: 0.5, dof_samples: 4, cancel: Cancel);
        var r = await b.Tools.RenderScene(1, 32, samples: 1, cancel: Cancel);
        Assert.Contains("4 rays a pixel (raised by depth of field or motion blur)", Text(r));
        // The sky shows where the box isn't.
        var image = TextureImages.Decode(Pngs.Of(r))!;
        Assert.True(image.Rgba[2] > image.Rgba[0], "The corner should be the blue sky.");

        // Motion blur on an animated box: render_animation counts the blur's samples against its limit.
        await b.Tools.SetKey("Modeler object", "position.x", 1, -1, cancel: Cancel);
        await b.Tools.SetKey("Modeler object", "position.x", 3, 1, cancel: Cancel);
        await b.Tools.SetItem("Camera", depth_of_field: false, motion_blur: true, blur_samples: 4, cancel: Cancel);
        var anim = await b.Tools.RenderAnimation("blur", "f", 1, 3, width: 32, cancel: Cancel);
        Assert.True(File.Exists(b.Folder.File("blur/f0002.png")));
        Assert.NotNull(anim);
        await b.Tools.SetItem("Camera", blur_samples: 256, cancel: Cancel);
        var e = await Error(b.Tools.RenderAnimation("blur2", "f", 1, 60, width: 320, cancel: Cancel));
        Assert.Contains("256 rays a pixel from the camera's depth of field or motion blur samples", e);
    }

    [Fact]
    public async Task Agents_reach_the_effects_through_a_client()
    {
        using var b = await WithBox();
        await using var s = await InProcess.Start(b.Host);
        var scene = await s.Client.Json("set_scene", new { ao = true, environment = "builtin:studio" });
        Assert.True((bool)scene["ambient_occlusion"]!["on"]!);
        var light = await s.Client.Json("add_light", new { type = "area", area_size = new[] { 1.5, 1.5 }, position = new[] { 0, 3, 0 }, look_at = new[] { 0, 0, 0 } });
        Assert.Equal("area", (string?)light["type"]);
        var cam = await s.Client.Json("set_item", new { item = "Camera", focus_on = "Modeler object", f_stop = 0.3, motion_blur = true });
        Assert.True((bool)cam["motion_blur"]!["on"]!);
        // The tools' schemas tell agents about them.
        var tools = await s.Client.ListToolsAsync(cancellationToken: Cancel);
        string schema = tools.Single(t => t.Name == "set_scene").JsonSchema.GetRawText();
        Assert.Contains("environment_lighting", schema);
        Assert.Contains("builtin:studio", schema);
        Assert.Contains("focus_on", tools.Single(t => t.Name == "set_item").JsonSchema.GetRawText());
        Assert.Contains("area_size", tools.Single(t => t.Name == "add_light").JsonSchema.GetRawText());
    }
}
