using System.Text.Json.Nodes;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_lens_flare, set_volumetric_light and set_shadow_map: what they set, what get_scene shows, undo, and refusals that change nothing.</summary>
public class LightEffectsHandlerTests
{
    private static async Task<(Bench Bench, LightItem Spot)> WithSpot()
    {
        var b = new Bench();
        await b.Tools.AddLight("spot", "Spot", position: [0, 4, 0], look_at: [0, 0, 0], cancel: Cancel);
        return (b, (LightItem)b.Scene.Find("Spot")!);
    }

    [Fact]
    public async Task Set_lens_flare_turns_it_on_as_one_undo_step_and_get_scene_shows_it()
    {
        var (b, spot) = await WithSpot();
        using var _ = b;
        int steps = b.Layout.History.Count;
        var item = await Parse(b.Tools.SetLensFlare("Spot", on: true, intensity: 0.8, size: 0.2, rings: true, streaks: 6, streak_length: 0.4, fade_with_distance: true, nominal_distance: 12, cancel: Cancel));
        var flare = item["lens_flare"]!;
        Assert.True((bool)flare["on"]!);
        Assert.Equal(0.8, (double)flare["intensity"]!);
        Assert.Equal(6, (int)flare["streaks"]!);
        Assert.True((bool)flare["rings"]!);
        Assert.Equal(new LensFlare { On = true, Size = 0.2, Ring = true, Streaks = 6, StreakLength = 0.4, FadeWithDistance = true, NominalDistance = 12 }, spot.Flare);
        Assert.Equal("Agent: set a lens flare", (string?)item["undo_scene"]!["next_undo"]);
        Assert.Equal(steps + 1, b.Layout.History.Count);

        await b.Tools.UndoScene(Cancel);
        Assert.False(spot.Flare.On);
        Assert.Equal(1, spot.FlareIntensity.Evaluate(0));
        var scene = await Parse(b.Tools.GetScene(cancel: Cancel));
        var listed = scene["items"]!.AsArray().First(i => (string?)i!["name"] == "Spot")!;
        Assert.False((bool)listed["lens_flare"]!["on"]!);
        Assert.Equal("raytrace", (string?)listed["shadow_type"]);
        Assert.False((bool)listed["volumetric"]!["on"]!);
    }

    [Fact]
    public async Task Flare_intensity_keys_at_a_frame_and_is_a_channel_for_set_key_and_get_keys()
    {
        var (b, spot) = await WithSpot();
        using var _ = b;
        await b.Tools.SetLensFlare("Spot", on: true, intensity: 0, frame: 1, cancel: Cancel);
        await b.Tools.SetLensFlare("Spot", intensity: 2, frame: 30, cancel: Cancel);
        Assert.Equal([0.0, 2.0], spot.FlareIntensity.Keys.Select(k => k.Value));
        await b.Tools.SetKey("Spot", "flare.intensity", 15, 1.5, cancel: Cancel);
        var keys = await Parse(b.Tools.GetKeys("Spot", "flare.intensity", Cancel));
        Assert.Equal(3, keys["channels"]!.AsArray().Single()!["keys"]!.AsArray().Count);
        // Animated, it's changed by keys, not throughout.
        Assert.Contains("frame", await Error(b.Tools.SetLensFlare("Spot", intensity: 1, cancel: Cancel)));
    }

    [Fact]
    public async Task Set_volumetric_light_and_set_shadow_map_set_their_records()
    {
        var (b, spot) = await WithSpot();
        using var _ = b;
        var item = await Parse(b.Tools.SetVolumetricLight("Spot", on: true, intensity: 1.5, samples: 24, distance: 6, shadows: false, cancel: Cancel));
        Assert.Equal(new VolumetricLight { On = true, Intensity = 1.5, Samples = 24, Distance = 6, Shadows = false }, spot.Volumetric);
        Assert.Equal(24, (int)item["volumetric"]!["samples"]!);

        item = await Parse(b.Tools.SetShadowMap("Spot", type: "shadow_map", size: 1024, fuzziness: 3, cancel: Cancel));
        Assert.True(spot.UsesShadowMap);
        Assert.Equal("shadow_map", (string?)item["shadow_type"]);
        Assert.Equal(1024, (int)item["shadow_map"]!["size"]!);
        Assert.Equal(3, (double)item["shadow_map"]!["fuzziness"]!);

        item = await Parse(b.Tools.SetShadowMap("Spot", type: "off", cancel: Cancel));
        Assert.False(spot.Shadows);
        Assert.Equal("off", (string?)item["shadow_type"]);
        await b.Tools.SetShadowMap("Spot", type: "raytrace", cancel: Cancel);
        Assert.True(spot.Shadows);
        Assert.False(spot.UsesShadowMap);
        // The map's settings are kept for when it's chosen again.
        Assert.Equal(1024, spot.ShadowMap.Size);

        // A render with all three on works.
        await b.Tools.SetLensFlare("Spot", on: true, cancel: Cancel);
        await b.Tools.SetShadowMap("Spot", type: "shadow_map", cancel: Cancel);
        await b.Tools.SetVolumetricLight("Spot", samples: 4, cancel: Cancel);
        var picture = RayTracer.Render(b.Scene, 1, new RenderSettings { Width = 32, Height = 24 }, null, Cancel);
        Assert.Equal(32 * 24 * 4, picture.Rgba.Length);
    }

    [Fact]
    public async Task Bad_light_effect_settings_are_refused_and_change_nothing()
    {
        var (b, spot) = await WithSpot();
        using var _ = b;
        await b.Tools.AddLight("area", "Panel", position: [0, 3, 0], cancel: Cancel);
        int steps = b.Layout.History.Count;
        Assert.Contains("not a light", await Error(b.Tools.SetLensFlare("Camera", on: true, cancel: Cancel)));
        Assert.Contains("Nothing", await Error(b.Tools.SetLensFlare("Nothing", on: true, cancel: Cancel)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("streaks", await Error(b.Tools.SetLensFlare("Spot", on: true, streaks: 100, cancel: Cancel)));
        Assert.Contains("size", await Error(b.Tools.SetLensFlare("Spot", on: true, size: 0, cancel: Cancel)));
        Assert.Contains("give intensity", await Error(b.Tools.SetLensFlare("Spot", on: true, frame: 5, cancel: Cancel)));
        Assert.Contains("outside the scene's frames", await Error(b.Tools.SetLensFlare("Spot", on: true, intensity: 1, frame: 9999, cancel: Cancel)));
        Assert.Contains("0 or more", await Error(b.Tools.SetLensFlare("Spot", on: true, intensity: -1, cancel: Cancel)));
        Assert.Contains("1 to 256", await Error(b.Tools.SetVolumetricLight("Spot", on: true, samples: 0, cancel: Cancel)));
        Assert.Contains("distance", await Error(b.Tools.SetVolumetricLight("Spot", on: true, distance: -2, cancel: Cancel)));
        Assert.Contains("area light", await Error(b.Tools.SetVolumetricLight("Panel", on: true, cancel: Cancel)));
        Assert.Contains("area light", await Error(b.Tools.SetShadowMap("Panel", type: "shadow_map", cancel: Cancel)));
        Assert.Contains("raytrace, shadow_map or off", await Error(b.Tools.SetShadowMap("Spot", type: "soft", cancel: Cancel)));
        Assert.Contains("64 to 4096", await Error(b.Tools.SetShadowMap("Spot", type: "shadow_map", size: 10000, cancel: Cancel)));
        Assert.Contains("fuzziness", await Error(b.Tools.SetShadowMap("Spot", fuzziness: 99, cancel: Cancel)));

        Assert.Equal(steps, b.Layout.History.Count);
        Assert.Equal(new LensFlare(), spot.Flare);
        Assert.Equal(new VolumetricLight(), spot.Volumetric);
        Assert.Equal(new ShadowMapSettings(), spot.ShadowMap);
        Assert.True(spot.Shadows);
        Assert.True(spot.FlareIntensity.IsConstant);
    }

    [Fact]
    public async Task The_light_effect_tools_are_scene_tools_with_descriptions_of_their_units_and_defaults()
    {
        foreach (var name in new[] { "set_lens_flare", "set_volumetric_light", "set_shadow_map" }) Assert.Contains(name, ToolGroups.Scene);
        using var b = new Bench();
        await using var server = await InProcess.Start(b.Host);
        var tools = (await server.Client.ListToolsAsync(cancellationToken: Cancel)).ToDictionary(t => t.Name);
        Assert.Contains("fractions of the picture's height", tools["set_lens_flare"].Description);
        Assert.Contains("per metre", tools["set_volumetric_light"].Description);
        Assert.Contains("(default 32)", tools["set_volumetric_light"].JsonSchema.GetRawText());
        Assert.Contains("(default 512)", tools["set_shadow_map"].JsonSchema.GetRawText());
        Assert.Contains("fuzziness", tools["set_shadow_map"].Description);
        var item = await server.Client.Json("set_shadow_map", new { light = "Light", type = "shadow_map", size = 256 });
        Assert.Equal("shadow_map", (string?)item["shadow_type"]);
    }
}
