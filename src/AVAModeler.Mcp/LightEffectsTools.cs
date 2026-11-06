using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Layout;
using AVAModeler.Scene;
using ModelContextProtocol.Server;
using static AVAModeler.Mcp.Census;
using static AVAModeler.Mcp.SceneCensus;

namespace AVAModeler.Mcp;

/// <summary>
/// A light's own effects: lens flares, volumetric beams and shadow maps. Each call is one step on Layout's undo
/// history (through <see cref="DoScene{T}"/>), every setting is checked before anything changes, and the result is the
/// light as get_scene describes it.
/// </summary>
public sealed partial class ModelerTools
{
    [McpServerTool(Name = "set_lens_flare", Destructive = true)]
    [Description("Gives a light a lens flare, as LightWave 5's: drawn over the rendered picture where the light is seen (added to the colour; the alpha channel is left as the objects made it), in the light's colour: a central glow with a white-hot core, an optional halo ring and an optional star of streaks (their angles look random but are the same every render). It fades behind objects (seen through a transparent surface it's dimmed by its transparency), as the light leaves the frame, and optionally with distance. A distant light flares where its direction meets the picture (like the sun); a light behind the camera has none. Sizes are fractions of the picture's height. Settings you leave out stay as they are; all are off or at their defaults until set. intensity sets the flare's brightness throughout (1 = 100%), or keys it at frame if you give one (it's the channel flare.intensity: set_key, get_keys and the dope sheet work on it while the flare is on). Cheap: a few milliseconds a frame, plus one ray toward the light for fade_behind.")]
    public Task<string> SetLensFlare(
        [Description("The light's name, or #index from get_scene.")] string light,
        [Description("Lens flare on or off (default off).")] bool? on = null,
        [Description("Its intensity, 0 or more: 1 is 100% (the default), 2 twice as bright. Throughout, or a key at frame.")] double? intensity = null,
        [Description("Key the intensity at this frame instead of setting it throughout (within the scene's frames).")] double? frame = null,
        [Description("The central glow's radius as a fraction of the picture's height, 0.001 to 2 (default 0.1).")] double? size = null,
        [Description("Draw the central glow (default true).")] bool? glow = null,
        [Description("Draw a halo ring round the glow (default false).")] bool? rings = null,
        [Description("The ring's radius as a fraction of the picture's height, 0.001 to 2 (default 0.15).")] double? ring_size = null,
        [Description("How many streaks the star has, 0 (none, the default) to 64; 4 to 8 look like a star filter.")] int? streaks = null,
        [Description("The longest streak's length as a fraction of the picture's height, 0.001 to 4 (default 0.3); each streak is half to all of it.")] double? streak_length = null,
        [Description("Fade it behind objects (default true): opaque ones hide it, transparent ones dim it.")] bool? fade_behind = null,
        [Description("Fade it out as the light leaves the frame (default true), over the glow's radius beyond the edge.")] bool? fade_off_screen = null,
        [Description("Fade it with the light's distance from the camera beyond nominal_distance (default false): at twice that distance, half as bright.")] bool? fade_with_distance = null,
        [Description("With fade_with_distance, the distance in metres at full intensity, more than 0 (default 5).")] double? nominal_distance = null,
        [Description("How dissolved it is, 0 (not at all, the default) to 1 (gone).")] double? dissolve = null,
        CancellationToken cancel = default) =>
        DoScene("set a lens flare", (c, l) =>
        {
            var it = LightNamed(l.Scene, light);
            var f = it.Flare;
            if (on is { } o) f = f with { On = o };
            if (size is { } sz) f = f with { Size = sz };
            if (glow is { } g) f = f with { Glow = g };
            if (rings is { } r) f = f with { Ring = r };
            if (ring_size is { } rs) f = f with { RingSize = rs };
            if (streaks is { } n) f = f with { Streaks = n };
            if (streak_length is { } sl) f = f with { StreakLength = sl };
            if (fade_behind is { } fb) f = f with { FadeBehind = fb };
            if (fade_off_screen is { } fo) f = f with { FadeOffScreen = fo };
            if (fade_with_distance is { } fd) f = f with { FadeWithDistance = fd };
            if (nominal_distance is { } nd) f = f with { NominalDistance = nd };
            if (dissolve is { } d) f = f with { Dissolve = d };
            f.Checked();
            if (frame != null && intensity == null) throw new ArgumentException("frame keys the intensity: give intensity too.");
            if (intensity is { } i)
            {
                if (!double.IsFinite(i) || i < 0) throw new ArgumentException("intensity must be 0 or more (1 is 100%).");
                if (frame is { } at) l.SetFlareIntensity(it, SceneCensus.Frame(l, at), i, key: true);
                else
                {
                    if (Keying.IsAnimated(it.FlareIntensity))
                        throw new ArgumentException($"{it.Name}'s flare intensity is animated (keys at {Keying.Frames(it.FlareIntensity)}): give frame to key it there, or change its keys with set_key (channel \"flare.intensity\").");
                    l.SetFlareIntensity(it, l.Frame, i, key: false);
                }
            }
            l.SetLensFlare(it, f);
            return ItemNow(c, l, it, l.Frame);
        }, cancel);

    [McpServerTool(Name = "set_volumetric_light", Destructive = true)]
    [Description("Makes a light's beam visible in the air (volumetric lighting, as LightWave 5.5's): a spot light's cone, a point light's glow round it, or a distant light's shafts. Rays from the camera march through the beam between the camera and what they hit (only that part, so empty space costs nothing), adding the light scattered toward the camera at each step: the light's colour × its intensity × the spot's cone and soft edge × its falloff × this intensity × 0.1 per metre (at intensity 1, ten metres of unfaded beam add the light's full brightness). With shadows, things in the beam cast shadows into it (shafts of light); fog dims the beam with distance like everything else. Only the camera's rays see beams, not reflections or what's seen through glass, and the alpha channel is left as the objects made it. Settings you leave out stay as they are. Cost: samples steps a pixel per volumetric light, each with a shadow ray when shadows are on (a shadow map lookup instead if set_shadow_map gave the light one, much faster): start with 16 to 32 samples and small pictures; 64 to 128 for smooth final beams. Area lights have no beam.")]
    public Task<string> SetVolumetricLight(
        [Description("The light's name, or #index from get_scene.")] string light,
        [Description("Volumetric light on or off (default off).")] bool? on = null,
        [Description("The beam's brightness, 0 or more: 1 (100%, the default) adds a tenth of the light per metre of beam crossed.")] double? intensity = null,
        [Description("Steps along each camera ray through the beam, 1 to 256 (default 32): more is smoother and slower (each pixel jitters its steps, so few still give smooth, slightly grainy beams).")] int? samples = null,
        [Description("How far the beam reaches in metres from the light (from the camera for a distant light), 0 or more; 0 (the default) takes the light's range, or without one reaches across the scene. A beam without a range fades linearly to nothing at its end.")] double? distance = null,
        [Description("Things in the beam cast shadows into it (default true): a shadow ray (or shadow map lookup) per step. Off is faster.")] bool? shadows = null,
        CancellationToken cancel = default) =>
        DoScene("set a volumetric light", (c, l) =>
        {
            var it = LightNamed(l.Scene, light);
            var v = it.Volumetric;
            if (on is { } o) v = v with { On = o };
            if (intensity is { } i) v = v with { Intensity = i };
            if (samples is { } n) v = v with { Samples = n };
            if (distance is { } d) v = v with { Distance = d };
            if (shadows is { } sh) v = v with { Shadows = sh };
            if (v.On && it.Type == LightType.Area) throw new ArgumentException($"{it.Name} is an area light, which has no volumetric beam: use a spot, point or distant light.");
            l.SetVolumetric(it, v);
            return ItemNow(c, l, it, l.Frame);
        }, cancel);

    [McpServerTool(Name = "set_shadow_map", Destructive = true)]
    [Description("Chooses how a light casts shadows. raytrace (the default): a shadow ray from each point lit toward the light, exact and sharp (soft from area lights and lights with a radius, at shadow_samples rays each: slow). shadow_map: the light's view of the scene is drawn once a frame into a depth map size × size pixels, and shadows are looked up in it, with an edge softened over fuzziness map pixels; much faster than soft ray-traced shadows, but approximate: transparent surfaces cast full shadows, a point or spot light with a radius is lit as a point (fuzziness gives the softness instead), and too small a map makes blocky or blurred edges. A spot light uses one map over its cone (wider than 60°: a cube of six), a point light a cube of six 90° maps (each at most 2048 pixels), a distant light one map fitted round the whole scene (so a big scene needs a bigger map). Area lights keep ray-traced shadows. off: no shadows. Settings you leave out stay as they are.")]
    public Task<string> SetShadowMap(
        [Description("The light's name, or #index from get_scene.")] string light,
        [Description("raytrace, shadow_map or off.")] string? type = null,
        [Description("The map's size in pixels a side, 64 to 4096 (default 512); 1024 to 2048 for crisp shadows from a distant light over a big scene.")] int? size = null,
        [Description("The soft edge's width in map pixels, 0 (hard) to 32 (default 1): the edge softens over about twice this.")] double? fuzziness = null,
        CancellationToken cancel = default) =>
        DoScene("set a light's shadows", (c, l) =>
        {
            var it = LightNamed(l.Scene, light);
            bool shadows = it.Shadows;
            var map = it.ShadowMap;
            if (type != null)
            {
                switch (type.Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_"))
                {
                    case "raytrace" or "ray_trace" or "ray_traced" or "raytraced":
                        (shadows, map) = (true, map with { On = false });
                        break;
                    case "shadow_map" or "shadowmap" or "map":
                        if (it.Type == LightType.Area) throw new ArgumentException($"{it.Name} is an area light, which keeps ray-traced shadows: use raytrace, or a spot, point or distant light for a shadow map.");
                        (shadows, map) = (true, map with { On = true });
                        break;
                    case "off" or "none":
                        shadows = false;
                        break;
                    default:
                        throw new ArgumentException($"type must be raytrace, shadow_map or off, not \"{type}\".");
                }
            }
            if (size is { } sz) map = map with { Size = sz };
            if (fuzziness is { } fz) map = map with { Fuzziness = fz };
            l.SetShadows(it, shadows, map);
            return ItemNow(c, l, it, l.Frame);
        }, cancel);

    /// <summary>A light by name or #index.</summary>
    private static LightItem LightNamed(AVAModeler.Scene.Scene scene, string name)
    {
        var it = Item(scene, name, "light");
        return it as LightItem ?? throw new ArgumentException($"{it.Name} is {(it is ObjectItem ? "an" : "a")} {it.Kind.ToLowerInvariant()}, not a light.");
    }
}

/// <summary>What get_scene says about a light's own effects.</summary>
internal static class LightEffectsCensus
{
    public static void Describe(LightItem light, JsonObject o, double frame)
    {
        o["shadow_type"] = !light.Shadows ? "off" : light.UsesShadowMap ? "shadow_map" : "raytrace";
        if (light.UsesShadowMap) o["shadow_map"] = new JsonObject { ["size"] = light.ShadowMap.Size, ["fuzziness"] = Round(light.ShadowMap.Fuzziness) };
        var f = light.Flare;
        o["lens_flare"] = f.On
            ? new JsonObject
            {
                ["on"] = true, ["intensity"] = Round(light.FlareIntensity.Evaluate(frame)), ["size"] = Round(f.Size), ["glow"] = f.Glow,
                ["rings"] = f.Ring, ["ring_size"] = Round(f.RingSize), ["streaks"] = f.Streaks, ["streak_length"] = Round(f.StreakLength),
                ["fade_behind"] = f.FadeBehind, ["fade_off_screen"] = f.FadeOffScreen, ["fade_with_distance"] = f.FadeWithDistance,
                ["nominal_distance"] = Round(f.NominalDistance), ["dissolve"] = Round(f.Dissolve),
            }
            : new JsonObject { ["on"] = false };
        var v = light.Volumetric;
        o["volumetric"] = v.On
            ? new JsonObject
            {
                ["on"] = true, ["intensity"] = Round(v.Intensity), ["samples"] = v.Samples, ["distance"] = Round(v.Distance), ["shadows"] = v.Shadows,
            }
            : new JsonObject { ["on"] = false };
    }
}
