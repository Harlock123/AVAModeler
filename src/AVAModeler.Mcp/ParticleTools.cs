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
    [Description("Puts a particle emitter on an object, changes it, or (off true) takes it away: sparks, fountains, smoke, dust, snow. Particles are born at rate a second from emit_from (surface: anywhere on its faces, by area, leaving along their normal; points: its points; origin: its pivot, leaving along +Y) between start_frame and end_frame, leave at speed m/s (give or take speed_jitter of it) within spread degrees of straight out, live life seconds, fall under gravity (m/s², down the world's -Y; 9.81 is Earth's, 0 for none, negative rises like smoke), slow with drag (a second), and stop on a floor at that height (metres) if given. Each is a small diamond of size metres, grown or shrunk to size_at_death × the size by the end of its life, in color [r, g, b] 0-255, glowing by glow (0-1). A particle stays where it was born as the emitter moves on. look: gem (the diamonds, default), blob (one smooth surface melting close particles together: liquid, lava), puff (soft, seen-through, lit by the lights: smoke, steam; rendered only, the views show small diamonds), sprite (flat glowing spots that add light: sparks, fireflies; rendered only) or object (a copy of instance_of on each, its largest side the particle's size, turned at random and by spin °/s, or with align_to_motion its +Z along its flight: debris, leaves, a flock). opacity (0-1, default 0.6) is how much a puff hides or how bright a sprite is. collide true makes them meet the scene's other visible objects (wherever those are as they move) as well as the floor; on_hit says what happens there: stick (default: stop, riding along with a moving object), bounce (keeping bounce of the speed into the surface, 0-1, default 0.5, and losing friction of the speed along it, 0-1, default 0.2) or die. Without collisions (and with stick on the floor) every frame is worked out on its own in closed form; with them each particle is stepped through its life on half-frame steps, slower but the same every time. Seed changes which particles come where. Leave out what you don't want to change. Returns the emitter's settings and how many are alive at the current frame.")]
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
        [Description("gem (default), blob, puff, sprite or object.")] string? look = null,
        [Description("For look object: the object copied onto each particle, by name.")] string? instance_of = null,
        [Description("For look object: how fast each copy turns, °/s (default 0).")] double? spin = null,
        [Description("For look object: point each copy's +Z along its flight.")] bool? align_to_motion = null,
        [Description("For puffs and sprites, 0 to 1 (default 0.6).")] double? opacity = null,
        [Description("Meet the scene's other visible objects as well as the floor.")] bool? collide = null,
        [Description("stick (default), bounce or die: what a particle does on meeting the floor or an object.")] string? on_hit = null,
        [Description("For bounce: the share of speed into the surface kept, 0 to 1 (default 0.5).")] double? bounce = null,
        [Description("For bounce: the share of speed along the surface lost, 0 to 1 (default 0.2).")] double? friction = null,
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
                    Look = look == null ? s.Look : Enum.TryParse<ParticleLook>(look, ignoreCase: true, out var lk) && Enum.IsDefined(lk) && !int.TryParse(look, out _) ? lk : throw new ArgumentException($"look is gem, blob, puff, sprite or object, not \"{look}\"."),
                    InstanceOf = instance_of ?? s.InstanceOf, Spin = spin ?? s.Spin, AlignToMotion = align_to_motion ?? s.AlignToMotion, Opacity = opacity ?? s.Opacity,
                    Collide = collide ?? s.Collide, Bounce = bounce ?? s.Bounce, Friction = friction ?? s.Friction,
                    OnHit = on_hit == null ? s.OnHit : Enum.TryParse<ParticleHit>(on_hit, ignoreCase: true, out var oh) && Enum.IsDefined(oh) && !int.TryParse(on_hit, out _) ? oh : throw new ArgumentException($"on_hit is stick, bounce or die, not \"{on_hit}\"."),
                }).Checked();
                if (obj.Particles.Look == ParticleLook.Object && !l.Scene.Objects.Any(x => x.Name == obj.Particles.InstanceOf && x != obj))
                    throw new ArgumentException($"There's no other object called \"{obj.Particles.InstanceOf}\" to copy onto the particles: get_scene lists the objects.");
            }
            l.Scene.Notify();
            var result = ItemNow(c, l, obj, l.Frame);
            if (obj.Particles is { } p)
            {
                result["particles"] = Particles(p);
                result["alive_now"] = p.Collide || (p.Floor != null && p.OnHit != ParticleHit.Stick)
                    ? null // stepped particles: counting them means working them all out; a render shows them
                    : (JsonNode)ParticleSystem.At(p, l.Frame, l.Scene.FramesPerSecond, obj.BaseMesh(), f => obj.WorldMatrix(f)).Count;
            }
            return result;
        }, cancel);

    [McpServerTool(Name = "set_hair", Destructive = true)]
    [Description("Grows hair or fur on an object, changes it, or (off true) takes it away: density strands a square metre over its faces (or only on_surface's), each length metres (shortened at random by up to length_jitter of it), thickness metres at the root tapering to a point, in segments joints, drooping under its weight by droop (0 straight out, 1 hanging down), leaning up to frizz degrees from the surface at random, in color [r, g, b] 0-255. Strands grow from the object's deformed surface, so they follow bones, morphs and displacements (and a soft body); at most 30000 an object. comb (0-1) lays them toward comb_direction [x, y, z] in the object's own coordinates (default [0, 0, -1], the back). dynamics true makes them swing as the object moves, starts, stops and turns, lagging like a weight on a spring and settling: stiffness is how fast they swing back (Hz, 0.1 floppy to 20 stiff, default 2), damping how soon a swing dies (0 swings on, 1 no overshoot, default 0.3). Only the object's motion (and its parents') swings them, not its bones or morphs; worked out from the scene's first frame, the same every time. Leave out what you don't want to change. Returns the hair's settings and how many strands it grows.")]
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
        [Description("Swing as the object moves.")] bool? dynamics = null,
        [Description("For dynamics: Hz, 0.1 to 20 (default 2).")] double? stiffness = null,
        [Description("For dynamics: 0 to 2 (default 0.3).")] double? damping = null,
        [Description("How far strands are combed, 0 to 1 (default 0).")] double? comb = null,
        [Description("[x, y, z], the way they're combed in the object's coordinates (default [0, 0, -1]).")] double[]? comb_direction = null,
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
                    Dynamics = dynamics ?? s.Dynamics, Stiffness = stiffness ?? s.Stiffness, Damping = damping ?? s.Damping, Comb = comb ?? s.Comb,
                    CombDirection = comb_direction == null ? s.CombDirection
                        : comb_direction.Length == 3 ? new Vec3(comb_direction[0], comb_direction[1], comb_direction[2]) : throw new ArgumentException("comb_direction is three numbers, [x, y, z]."),
                }).Checked();
            }
            l.Scene.Notify();
            var result = ItemNow(c, l, obj, l.Frame);
            if (obj.Hair is { } h)
            {
                result["hair"] = Hair(h);
                var f = l.Scene.Evaluate(l.Frame).Objects.First(o => o.Item == obj);
                int surface = f.Surfaces.ToList().FindIndex(x => x.Name == $"{obj.Name} hair");
                result["strands"] = f.Mesh.Polygons.Count(p => p.Surface == surface) / (3 * h.Segments);
            }
            return result;
        }, cancel);
}

public sealed partial class ModelerTools
{
    [McpServerTool(Name = "set_soft_body", Destructive = true)]
    [Description("Makes an object cloth or a soft body, changes it, or (off true) makes it solid again. Its points then move under gravity (m/s² down the world's -Y, default 9.81) from start_frame (default the scene's first frame), held together by its edges (stiffness 0-1, default 0.9) and against folding (bend 0-1, default 0.1), drawn back to its animated shape by goal (0 for cloth, default; up to 1 for a jiggly soft body that follows its animation), slowed by damping (a second, default 0.5), held fast where the faces of the surface pin are (by name: a flag's edge on its pole, a cape's shoulders; they move only as the object is animated), kept thickness metres (default 0.005) above a floor at that height if given and, with collide true, out of the scene's other visible objects as they move. substeps (1-64, default 8) and iterations (1-64, default 8) trade speed for steadiness. Worked out by stepping from the start frame and kept frame by frame, so playing forward is quick and any frame comes out the same; any change to the scene starts it again. Use a mesh with enough points to bend (a subdivided plane for cloth); at most 20000 points. Hair grows on the moved surface. Leave out what you don't want to change. Returns the settings.")]
    public Task<string> SetSoftBody(
        [Description("The object's name, or #index from get_scene.")] string item,
        [Description("Make it solid again.")] bool off = false,
        [Description("0 to 1 (default 0.9).")] double? stiffness = null,
        [Description("0 to 1 (default 0.1).")] double? bend = null,
        [Description("0 (cloth) to 1 (default 0).")] double? goal = null,
        [Description("A second, 0 to 100 (default 0.5).")] double? damping = null,
        [Description("m/s² down -Y (default 9.81).")] double? gravity = null,
        [Description("A floor at this height (metres).")] double? floor = null,
        [Description("No floor.")] bool no_floor = false,
        [Description("Keep out of the scene's other visible objects.")] bool? collide = null,
        [Description("Metres kept off the floor and objects (default 0.005).")] double? thickness = null,
        [Description("The surface whose faces' points are held fast, by name; \"\" for none.")] string? pin = null,
        [Description("The frame it starts moving from (default the scene's first).")] double? start_frame = null,
        [Description("Steps a frame, 1 to 64 (default 8).")] int? substeps = null,
        [Description("Edge passes a step, 1 to 64 (default 8).")] int? iterations = null,
        CancellationToken cancel = default) =>
        DoScene(off ? "made solid" : "set soft body", (c, l) =>
        {
            var obj = SceneObject(l.Scene, item);
            if (off) obj.SoftBody = null;
            else
            {
                var s = obj.SoftBody ?? new SoftBodySettings();
                if (pin is { Length: > 0 } name && !obj.Surfaces.Any(x => x.Name == name))
                    throw new ArgumentException($"{obj.Name} has no surface \"{name}\": its surfaces are {string.Join(", ", obj.Surfaces.Select(x => x.Name))}.");
                obj.SoftBody = (s with
                {
                    Stiffness = stiffness ?? s.Stiffness, Bend = bend ?? s.Bend, Goal = goal ?? s.Goal, Damping = damping ?? s.Damping, Gravity = gravity ?? s.Gravity,
                    Floor = no_floor ? null : floor ?? s.Floor, Collide = collide ?? s.Collide, Thickness = thickness ?? s.Thickness, Pin = pin ?? s.Pin,
                    StartFrame = start_frame ?? s.StartFrame, Substeps = substeps ?? s.Substeps, Iterations = iterations ?? s.Iterations,
                }).Checked();
                if (obj.BaseMesh().Points.Count > SoftBodySettings.MaxPoints)
                    throw new ArgumentException($"{obj.Name} has {obj.BaseMesh().Points.Count} points; a soft body can have at most {SoftBodySettings.MaxPoints}.");
            }
            l.Scene.Notify();
            var result = ItemNow(c, l, obj, l.Frame);
            if (obj.SoftBody is { } b) result["soft_body"] = SoftBody(b);
            return result;
        }, cancel);
}

internal static partial class SceneCensus
{
    public static JsonObject SoftBody(SoftBodySettings b)
    {
        var o = new JsonObject
        {
            ["stiffness"] = Round(b.Stiffness), ["bend"] = Round(b.Bend), ["goal"] = Round(b.Goal), ["damping"] = Round(b.Damping),
            ["gravity"] = Round(b.Gravity), ["collide"] = b.Collide, ["thickness"] = Round(b.Thickness), ["pin"] = b.Pin,
            ["substeps"] = b.Substeps, ["iterations"] = b.Iterations,
        };
        if (b.Floor is { } f) o["floor"] = Round(f);
        if (b.StartFrame is { } s) o["start_frame"] = Round(s);
        return o;
    }

    public static JsonObject Particles(ParticleSettings p)
    {
        var o = new JsonObject
        {
            ["emit_from"] = p.From.ToString().ToLowerInvariant(), ["rate"] = Round(p.Rate), ["start_frame"] = Round(p.StartFrame), ["end_frame"] = Round(p.EndFrame),
            ["life"] = Round(p.Life), ["speed"] = Round(p.Speed), ["speed_jitter"] = Round(p.SpeedJitter), ["spread"] = Round(p.Spread),
            ["gravity"] = Round(p.Gravity), ["drag"] = Round(p.Drag), ["size"] = Round(p.Size), ["size_at_death"] = Round(p.SizeAtDeath),
            ["color"] = new JsonArray(p.Color.R, p.Color.G, p.Color.B), ["glow"] = Round(p.Glow), ["hide_emitter"] = p.HideEmitter, ["seed"] = p.Seed,
            ["look"] = p.Look.ToString().ToLowerInvariant(), ["collide"] = p.Collide, ["on_hit"] = p.OnHit.ToString().ToLowerInvariant(),
        };
        if (p.Look == ParticleLook.Object)
        {
            o["instance_of"] = p.InstanceOf;
            o["spin"] = Round(p.Spin);
            o["align_to_motion"] = p.AlignToMotion;
        }
        if (p.Look is ParticleLook.Puff or ParticleLook.Sprite) o["opacity"] = Round(p.Opacity);
        if (p.OnHit == ParticleHit.Bounce)
        {
            o["bounce"] = Round(p.Bounce);
            o["friction"] = Round(p.Friction);
        }
        if (p.Floor is { } f) o["floor"] = Round(f);
        return o;
    }

    public static JsonObject Hair(HairSettings h)
    {
        var o = new JsonObject
        {
            ["density"] = Round(h.Density), ["length"] = Round(h.Length), ["length_jitter"] = Round(h.LengthJitter), ["thickness"] = Round(h.Thickness),
            ["segments"] = h.Segments, ["droop"] = Round(h.Droop), ["frizz"] = Round(h.Frizz), ["on_surface"] = h.OnSurface,
            ["color"] = new JsonArray(h.Color.R, h.Color.G, h.Color.B), ["seed"] = h.Seed, ["dynamics"] = h.Dynamics,
        };
        if (h.Dynamics)
        {
            o["stiffness"] = Round(h.Stiffness);
            o["damping"] = Round(h.Damping);
        }
        if (h.Comb > 0)
        {
            o["comb"] = Round(h.Comb);
            o["comb_direction"] = new JsonArray(Round(h.CombDirection.X), Round(h.CombDirection.Y), Round(h.CombDirection.Z));
        }
        return o;
    }
}
