using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Scene;
using ModelContextProtocol.Server;
using static AVAModeler.Mcp.SceneCensus;
using static AVAModeler.Mcp.Census;

namespace AVAModeler.Mcp;

/// <summary>Particle emitters and hair on Layout's objects. Each change is one undo step; a failed one changes nothing.</summary>
public sealed partial class ModelerTools
{
    [McpServerTool(Name = "set_particles", Destructive = true)]
    [Description("Puts a particle emitter on an object, changes it, or (off true) takes it away: sparks, fountains, smoke, dust, snow. Particles are born at rate a second from emit_from (surface: anywhere on its faces, by area, leaving along their normal; points: its points; origin: its pivot, leaving along +Y) between start_frame and end_frame, leave at speed m/s (give or take speed_jitter of it) within spread degrees of straight out, live life seconds, fall under gravity (m/s², down the world's -Y; 9.81 is Earth's, 0 for none, negative rises like smoke), slow with drag (a second), and stop on a floor at that height (metres) if given. Each is a small diamond of size metres, grown or shrunk to size_at_death × the size by the end of its life, in color [r, g, b] 0-255, glowing by glow (0-1). A particle stays where it was born as the emitter moves on. Every frame is worked out on its own (not stepped), so renders and scrubbing agree; seed changes which particles come where. Leave out what you don't want to change. Returns the emitter's settings and how many are alive at the current frame.")]
    public Task<string> SetParticles(
        [Description("The object's name, or #index from get_scene.")] string item,
        [Description("Take the emitter away.")] bool off = false,
        [Description("surface (default), points or origin.")] string? emit_from = null,
        [Description("Particles a second (default 50).")] double? rate = null,
        [Description("The frame emission starts (default 0).")] double? start_frame = null,
        [Description("The frame it stops (default 60).")] double? end_frame = null,
        [Description("Seconds each lives (default 2).")] double? life = null,
        [Description("m/s they leave at (default 1).")] double? speed = null,
        [Description("How much speeds vary, 0 to 1 (default 0.3).")] double? speed_jitter = null,
        [Description("Degrees from straight out they may leave, 0 (a jet) to 180 (default 20).")] double? spread = null,
        [Description("m/s² down the world's -Y (default 9.81; 0 for none; negative rises).")] double? gravity = null,
        [Description("Air drag a second, 0 for none (default 0).")] double? drag = null,
        [Description("A particle's size in metres (default 0.02).")] double? size = null,
        [Description("Its size at the end of its life, × the size (default 1; 0 shrinks to nothing).")] double? size_at_death = null,
        [Description("A floor at this height (metres) they stop on.")] double? floor = null,
        [Description("No floor.")] bool no_floor = false,
        [Description("[r, g, b], 0 to 255 (default [255, 230, 160]).")] double[]? color = null,
        [Description("Luminosity, 0 to 1 (default 0.5): sparks glow, dust doesn't.")] double? glow = null,
        [Description("Hide the emitter itself, so only its particles show.")] bool? hide_emitter = null,
        [Description("A different scatter with the same settings.")] int? seed = null,
        CancellationToken cancel = default) =>
        DoScene(off ? "took away particles" : "set particles", (c, l) =>
        {
            var obj = SceneObject(l.Scene, item);
            if (off) obj.Particles = null;
            else
            {
                var s = obj.Particles ?? new ParticleSettings();
                obj.Particles = (s with
                {
                    From = emit_from == null ? s.From : Enum.TryParse<EmitFrom>(emit_from, ignoreCase: true, out var ef) ? ef : throw new ArgumentException($"emit_from is surface, points or origin, not \"{emit_from}\"."),
                    Rate = rate ?? s.Rate, StartFrame = start_frame ?? s.StartFrame, EndFrame = end_frame ?? s.EndFrame, Life = life ?? s.Life,
                    Speed = speed ?? s.Speed, SpeedJitter = speed_jitter ?? s.SpeedJitter, Spread = spread ?? s.Spread, Gravity = gravity ?? s.Gravity,
                    Drag = drag ?? s.Drag, Size = size ?? s.Size, SizeAtDeath = size_at_death ?? s.SizeAtDeath,
                    Floor = no_floor ? null : floor ?? s.Floor, Color = color == null ? s.Color : Json.Colour(color, "color"),
                    Glow = glow ?? s.Glow, HideEmitter = hide_emitter ?? s.HideEmitter, Seed = seed ?? s.Seed,
                }).Checked();
            }
            l.Scene.Notify();
            var result = ItemNow(c, l, obj, l.Frame);
            if (obj.Particles is { } p)
            {
                result["particles"] = Particles(p);
                result["alive_now"] = ParticleSystem.At(p, l.Frame, l.Scene.FramesPerSecond, obj.BaseMesh(), f => obj.WorldMatrix(f)).Count;
            }
            return result;
        }, cancel);

    [McpServerTool(Name = "set_hair", Destructive = true)]
    [Description("Grows hair or fur on an object, changes it, or (off true) takes it away: density strands a square metre over its faces (or only on_surface's), each length metres (shortened at random by up to length_jitter of it), thickness metres at the root tapering to a point, in segments joints, drooping under its weight by droop (0 straight out, 1 hanging down), leaning up to frizz degrees from the surface at random, in color [r, g, b] 0-255. Strands grow from the object's deformed surface, so they follow bones, morphs and displacements; at most 30000 an object. Leave out what you don't want to change. Returns the hair's settings and how many strands it grows.")]
    public Task<string> SetHair(
        [Description("The object's name, or #index from get_scene.")] string item,
        [Description("Take the hair away.")] bool off = false,
        [Description("Strands a square metre (default 20000).")] double? density = null,
        [Description("Metres (default 0.05).")] double? length = null,
        [Description("How much lengths vary, 0 to 1 (default 0.3).")] double? length_jitter = null,
        [Description("Metres at the root (default 0.001).")] double? thickness = null,
        [Description("Joints along each strand, 1 to 32 (default 4).")] int? segments = null,
        [Description("0 (straight out) to 1 (hanging down) (default 0.3).")] double? droop = null,
        [Description("Degrees strands lean from the surface at random, 0 to 90 (default 15).")] double? frizz = null,
        [Description("Grow only on this surface's faces, by name; \"\" for every face.")] string? on_surface = null,
        [Description("[r, g, b], 0 to 255 (default [120, 80, 50]).")] double[]? color = null,
        [Description("A different scatter with the same settings.")] int? seed = null,
        CancellationToken cancel = default) =>
        DoScene(off ? "took away hair" : "set hair", (c, l) =>
        {
            var obj = SceneObject(l.Scene, item);
            if (off) obj.Hair = null;
            else
            {
                var s = obj.Hair ?? new HairSettings();
                if (on_surface is { Length: > 0 } name && !obj.Surfaces.Any(x => x.Name == name))
                    throw new ArgumentException($"{obj.Name} has no surface \"{name}\": its surfaces are {string.Join(", ", obj.Surfaces.Select(x => x.Name))}.");
                obj.Hair = (s with
                {
                    Density = density ?? s.Density, Length = length ?? s.Length, LengthJitter = length_jitter ?? s.LengthJitter, Thickness = thickness ?? s.Thickness,
                    Segments = segments ?? s.Segments, Droop = droop ?? s.Droop, Frizz = frizz ?? s.Frizz, OnSurface = on_surface ?? s.OnSurface,
                    Color = color == null ? s.Color : Json.Colour(color, "color"), Seed = seed ?? s.Seed,
                }).Checked();
            }
            l.Scene.Notify();
            var result = ItemNow(c, l, obj, l.Frame);
            if (obj.Hair is { } h)
            {
                result["hair"] = Hair(h);
                var f = l.Scene.Evaluate(l.Frame).Objects.First(o => o.Item == obj);
                int surface = f.Surfaces.Count - 1 - (obj.Particles != null ? 1 : 0);
                result["strands"] = f.Mesh.Polygons.Count(p => p.Surface == surface) / (3 * h.Segments);
            }
            return result;
        }, cancel);
}

internal static partial class SceneCensus
{
    public static JsonObject Particles(ParticleSettings p)
    {
        var o = new JsonObject
        {
            ["emit_from"] = p.From.ToString().ToLowerInvariant(), ["rate"] = Round(p.Rate), ["start_frame"] = Round(p.StartFrame), ["end_frame"] = Round(p.EndFrame),
            ["life"] = Round(p.Life), ["speed"] = Round(p.Speed), ["speed_jitter"] = Round(p.SpeedJitter), ["spread"] = Round(p.Spread),
            ["gravity"] = Round(p.Gravity), ["drag"] = Round(p.Drag), ["size"] = Round(p.Size), ["size_at_death"] = Round(p.SizeAtDeath),
            ["color"] = new JsonArray(p.Color.R, p.Color.G, p.Color.B), ["glow"] = Round(p.Glow), ["hide_emitter"] = p.HideEmitter, ["seed"] = p.Seed,
        };
        if (p.Floor is { } f) o["floor"] = Round(f);
        return o;
    }

    public static JsonObject Hair(HairSettings h) => new()
    {
        ["density"] = Round(h.Density), ["length"] = Round(h.Length), ["length_jitter"] = Round(h.LengthJitter), ["thickness"] = Round(h.Thickness),
        ["segments"] = h.Segments, ["droop"] = Round(h.Droop), ["frizz"] = Round(h.Frizz), ["on_surface"] = h.OnSurface,
        ["color"] = new JsonArray(h.Color.R, h.Color.G, h.Color.B), ["seed"] = h.Seed,
    };
}
