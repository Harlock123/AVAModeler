using System.Globalization;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using SceneModel = AVAModeler.Scene.Scene;
using static AVAModeler.Mcp.Census;

namespace AVAModeler.Mcp;

/// <summary>
/// What the Layout tools report and how they read an agent's names for things: items (by name, or "#index"),
/// channels ("position.x", "heading", "intensity"…), frames, colours.
/// </summary>
internal static class SceneCensus
{
    /// <summary>The channel names agents use, in the order of <see cref="Channel"/>.</summary>
    public static readonly string[] ChannelNames = ["position.x", "position.y", "position.z", "heading", "pitch", "bank", "scale.x", "scale.y", "scale.z"];

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ───────────────────────────── reading names ─────────────────────────────

    /// <summary>An item by its name (exact, else ignoring case) or by "#index" (its place in get_scene's list).</summary>
    public static SceneItem Item(SceneModel scene, string? name, string what = "item")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException($"Give the {what}'s name, as get_scene lists it.");
        name = name.Trim();
        var items = scene.Items;
        if (name.StartsWith('#') && int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            if (index < 0 || index >= items.Count) throw new ArgumentException($"There's no item #{index}: the scene has {items.Count} items, #0 to #{items.Count - 1}.");
            return items[index];
        }
        var found = items.Where(i => i.Name == name).ToList();
        if (found.Count == 0) found = items.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 1) return found[0];
        if (found.Count > 1)
            throw new ArgumentException($"There are {found.Count} items called \"{name}\" ({string.Join(", ", found.Select(i => $"#{IndexOf(scene, i)} {i.Kind.ToLowerInvariant()}"))}): give one as \"#{IndexOf(scene, found[0])}\", or rename one with set_item.");
        throw new ArgumentException($"There's no item called \"{name}\". The scene has: {(items.Count == 0 ? "nothing" : string.Join(", ", items.Select(i => $"\"{i.Name}\" ({i.Kind.ToLowerInvariant()})")))}.");
    }

    public static int IndexOf(SceneModel scene, SceneItem item) => scene.Items.ToList().IndexOf(item);

    /// <summary>A name not yet used in the scene: the one asked for, or it with " (2)", " (3)"… after it.</summary>
    public static string FreeName(SceneModel scene, string? wanted, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(wanted) ? fallback : wanted.Trim();
        if (scene.Items.All(i => i.Name != name)) return name;
        for (int n = 2; ; n++)
            if (scene.Items.All(i => i.Name != $"{name} ({n})")) return $"{name} ({n})";
    }

    /// <summary>
    /// The channels an item has: its nine motion channels, a light's intensity, a camera's zoom, and an object's morph
    /// weights ("morph.Smile") and displacement amplitudes ("displacement.1.amplitude", numbered from 1).
    /// </summary>
    public static IEnumerable<(string Name, Envelope Envelope)> Channels(SceneItem item)
    {
        for (int i = 0; i < 9; i++) yield return (ChannelNames[i], item.Channels[i]);
        if (item is LightItem l) yield return ("intensity", l.Intensity);
        if (item is CameraItem c) yield return ("zoom", c.Zoom);
        if (item is ObjectItem o)
        {
            foreach (var m in o.Morphs) yield return (MorphChannel(m), m.Weight);
            for (int i = 0; i < o.Displacements.Count; i++) yield return (DisplacementChannel(i), o.Displacements[i].Amplitude);
        }
    }

    /// <summary>A morph's weight channel: "morph." and its name.</summary>
    public static string MorphChannel(MorphTarget m) => MorphPrefix + m.Name;

    /// <summary>A displacement's amplitude channel, numbered from 1: "displacement.1.amplitude".</summary>
    public static string DisplacementChannel(int index) => FormattableString.Invariant($"{DisplacementPrefix}{index + 1}.amplitude");

    public const string MorphPrefix = "morph.", DisplacementPrefix = "displacement.";

    /// <summary>The frames an item has keys at, on any of its channels (morph weights and displacements' amplitudes too).</summary>
    public static IReadOnlyList<double> KeyFrames(SceneItem item)
    {
        var frames = new List<double>();
        foreach (var (_, e) in Channels(item))
            foreach (var k in e.Keys)
                if (!frames.Any(f => Math.Abs(f - k.Frame) < 1e-9)) frames.Add(k.Frame);
        frames.Sort();
        return frames;
    }

    /// <summary>An object's morph by name (exact, else ignoring case).</summary>
    public static MorphTarget Morph(ObjectItem obj, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the morph's name, as get_scene lists it.");
        name = name.Trim();
        var found = obj.Morphs.FirstOrDefault(m => m.Name == name) ?? obj.Morphs.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        return found ?? throw new ArgumentException(obj.Morphs.Count == 0
            ? $"{obj.Name} has no morphs. Make one with add_morph."
            : $"{obj.Name} has no morph \"{name}\". Its morphs are {string.Join(", ", obj.Morphs.Select(m => $"\"{m.Name}\""))}.");
    }

    /// <summary>An object's displacement by its number, from 1 as get_scene lists them.</summary>
    public static WaveDisplacement Displacement(ObjectItem obj, int number)
    {
        if (obj.Displacements.Count == 0) throw new ArgumentException($"{obj.Name} has no displacements. Add one with add_displacement.");
        if (number < 1 || number > obj.Displacements.Count)
            throw new ArgumentException($"{obj.Name} has no displacement {number}: its displacements are numbered 1 to {obj.Displacements.Count}.");
        return obj.Displacements[number - 1];
    }

    /// <summary>An item that must be an object (morphs, displacements and skelegons belong to objects).</summary>
    public static ObjectItem SceneObject(SceneModel scene, string? name, string what = "object")
    {
        var it = Item(scene, name, what);
        return it as ObjectItem ?? throw new ArgumentException($"{it.Name} is {(it.Kind == "Object" ? "an" : "a")} {it.Kind.ToLowerInvariant()}, not an object.");
    }

    /// <summary>One of an item's channels by name: position.x, heading (or rotation.h), scale.z, intensity, zoom, morph.Smile, displacement.1.amplitude…</summary>
    public static (string Name, Envelope Envelope) Channel(SceneItem item, string? channel)
    {
        if (item is ObjectItem obj && channel != null)
        {
            string c = channel.Trim();
            if (c.StartsWith(MorphPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var m = Morph(obj, c[MorphPrefix.Length..]);
                return (MorphChannel(m), m.Weight);
            }
            if (c.StartsWith(DisplacementPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = c[DisplacementPrefix.Length..];
                int dot = rest.IndexOf('.');
                string number = dot < 0 ? rest : rest[..dot];
                if (dot >= 0 && !string.Equals(rest[(dot + 1)..], "amplitude", StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                    throw new ArgumentException($"A displacement's channel is \"displacement.<number>.amplitude\" (numbered from 1), e.g. \"displacement.1.amplitude\", not \"{channel}\".");
                return (DisplacementChannel(n - 1), Displacement(obj, n).Amplitude);
            }
        }
        string key = new((channel ?? "").ToLowerInvariant().Where(char.IsLetter).ToArray());
        key = key switch
        {
            "rotationh" or "h" => "heading",
            "rotationp" or "p" => "pitch",
            "rotationb" or "b" => "bank",
            "x" or "posx" => "positionx",
            "y" or "posy" => "positiony",
            "z" or "posz" => "positionz",
            _ => key,
        };
        foreach (var (name, env) in Channels(item))
            if (new string(name.Where(char.IsLetter).ToArray()) == key) return (name, env);
        throw new ArgumentException($"{item.Name} has no channel \"{channel}\". Its channels are {string.Join(", ", Channels(item).Select(c => c.Name))}.");
    }

    /// <summary>A frame inside the scene's range (default: the current frame).</summary>
    public static double Frame(LayoutSession l, double? frame)
    {
        var s = l.Scene;
        double f = frame ?? l.Frame;
        if (!double.IsFinite(f) || f < s.FirstFrame || f > s.LastFrame)
            throw new ArgumentException($"Frame {f} is outside the scene's frames, {s.FirstFrame} to {s.LastFrame}. Use a frame in that range, or widen it with set_scene (first_frame, last_frame).");
        return f;
    }

    /// <summary>A colour [r, g, b], whole numbers from 0 to 255 like every tool's (Layout keeps it as 0 to 1).</summary>
    public static Vec3 Colour(double[]? c, string what) => Json.SceneColour(c, what);

    /// <summary>A vector [x, y, z].</summary>
    public static Vec3 Vector(double[]? a, string what)
    {
        if (a is not { Length: 3 } || a.Any(x => !double.IsFinite(x))) throw new ArgumentException($"{what} must be three numbers [x, y, z].");
        return new Vec3(a[0], a[1], a[2]);
    }

    // ───────────────────────────── reporting ─────────────────────────────

    /// <summary>The whole scene at a frame, as get_scene gives it.</summary>
    public static JsonObject Scene(LayoutSession l, Document modelerDocument, double frame)
    {
        var s = l.Scene;
        var sf = s.Evaluate(frame);
        var warnings = l.Warnings.Concat(sf.Warnings).Distinct().ToList();
        return new JsonObject
        {
            ["frames"] = new JsonObject
            {
                ["first"] = s.FirstFrame, ["last"] = s.LastFrame, ["fps"] = Round(s.FramesPerSecond), ["step"] = s.FrameStep,
                ["current"] = Round(l.Frame), ["described_at"] = Round(frame),
            },
            ["auto_key"] = l.AutoKey,
            ["camera"] = s.CurrentCamera?.Name,
            ["ambient"] = new JsonObject { ["color"] = Json.ColourJson(s.AmbientColor), ["intensity"] = Round(s.AmbientIntensity) },
            ["background"] = s.GradientBackground
                ? new JsonObject { ["gradient"] = true, ["top"] = Json.ColourJson(s.GradientTop), ["bottom"] = Json.ColourJson(s.GradientBottom) }
                : new JsonObject { ["gradient"] = false, ["color"] = Json.ColourJson(s.BackgroundColor) },
            ["fog"] = s.Fog == FogType.Off ? new JsonObject { ["type"] = "off" }
                : new JsonObject { ["type"] = "linear", ["min_distance"] = Round(s.FogMinDistance), ["max_distance"] = Round(s.FogMaxDistance), ["color"] = Json.ColourJson(s.FogColor) },
            ["file"] = l.ScenePath,
            ["modified"] = l.Modified,
            ["items"] = new JsonArray(s.Items.Select(i => (JsonNode)Item(l, i, sf, modelerDocument)).ToArray()),
            ["warnings"] = Strings(warnings),
            ["undo_scene"] = UndoState(l),
        };
    }

    /// <summary>What undo_scene and redo_scene would do now: the scene's shared history (the person's steps and agents').</summary>
    public static JsonObject UndoState(LayoutSession l) => new()
    {
        ["depth"] = l.History.Count,
        ["next_undo"] = l.History.UndoLabel,
        ["next_redo"] = l.History.RedoLabel,
    };

    /// <summary>One item at a worked-out frame.</summary>
    public static JsonObject Item(LayoutSession l, SceneItem item, SceneFrame sf, Document modelerDocument)
    {
        var s = l.Scene;
        double frame = sf.Frame;
        var world = sf.WorldOf(item);
        var o = new JsonObject
        {
            ["index"] = IndexOf(s, item),
            ["name"] = item.Name,
            ["kind"] = item.Kind.ToLowerInvariant(),
            ["parent"] = item.Parent?.Name,
            ["visible"] = item.Visible,
            ["position"] = Vec(item.PositionAt(frame)),
            ["rotation"] = Vec(item.RotationAt(frame)),
            ["scale"] = Vec(item.ScaleAt(frame)),
        };
        if (item.Pivot != Vec3.Zero) o["pivot"] = Vec(item.Pivot);
        if (item.Parent != null || item is Bone || item.Pivot != Vec3.Zero) o["world_position"] = Vec(world.TransformPoint(item.Pivot));
        o["keyed"] = Strings(Channels(item).Where(c => !Keying.IsConstant(c.Envelope, s)).Select(c => c.Name));
        switch (item)
        {
            case LightItem light:
                o["type"] = light.Type.ToString().ToLowerInvariant();
                o["color"] = Json.ColourJson(light.Color);
                o["intensity"] = Round(light.Intensity.Evaluate(frame));
                if (light.Type != LightType.Point) o["shines_toward"] = Vec(light.Direction(frame));
                if (light.Type == LightType.Spot) { o["cone_angle"] = Round(light.ConeAngle); o["edge_angle"] = Round(light.EdgeAngle); }
                if (light.Type != LightType.Distant) o["range"] = Round(light.Range);
                o["shadows"] = light.Shadows;
                break;
            case CameraItem cam:
                o["current"] = s.CurrentCamera == cam;
                o["zoom"] = Round(cam.Zoom.Evaluate(frame));
                o["fov"] = new JsonObject { ["horizontal"] = Round(cam.HorizontalFov(frame), 3), ["vertical"] = Round(cam.VerticalFov(frame), 3) };
                o["width"] = cam.Width;
                o["height"] = cam.Height;
                o["looks_toward"] = Vec(cam.CameraToWorld(frame).TransformVector(Vec3.UnitZ));
                break;
            case ObjectItem obj:
                o["source"] = obj.IsPlaceholder ? "placeholder (its file couldn't be loaded)"
                    : ReferenceEquals(obj.Document, modelerDocument) ? "the Modeler's object (live)"
                    : obj.SourcePath != null ? "file" : "embedded";
                if (obj.SourcePath != null) o["file"] = obj.SourcePath;
                if (obj.Layer is int layer) o["layer"] = layer + 1;
                var of = sf.Objects.First(x => x.Item == obj);
                o["points"] = of.Mesh.Points.Count;
                o["polygons"] = of.Mesh.Polygons.Count;
                if (!of.Mesh.IsEmpty) o["world_bounds"] = BoxJson(of.WorldMesh.Bounds());
                if (obj.Bones.Count > 0) o["bones"] = Strings(obj.Bones.Select(b => b.Name));
                if (obj.Morphs.Count > 0) o["morphs"] = Morphs(obj, frame);
                if (obj.Displacements.Count > 0) o["displacements"] = Displacements(obj, frame);
                if (!obj.CastShadows) o["cast_shadows"] = false;
                if (!obj.ReceiveShadows) o["receive_shadows"] = false;
                break;
            case Bone bone:
                o["object"] = bone.Owner.Name;
                o["rest_position"] = Vec(bone.RestPosition);
                o["rest_rotation"] = Vec(bone.RestRotation);
                o["rest_length"] = Round(bone.RestLength);
                o["strength"] = Round(bone.Strength);
                o["active"] = bone.Active;
                break;
        }
        return o;
    }

    /// <summary>An object's morphs at a frame: each one's name, weight channel, weight, and the target's point count.</summary>
    public static JsonArray Morphs(ObjectItem obj, double frame)
    {
        var problems = obj.MorphProblems();
        return new JsonArray(obj.Morphs.Select(m =>
        {
            var o = new JsonObject
            {
                ["name"] = m.Name,
                ["channel"] = MorphChannel(m),
                ["weight"] = Round(m.Weight.Evaluate(frame)),
                ["target_points"] = m.Target.Points.Count,
            };
            if (m.Weight.Keys.Count > 1) o["keyed"] = true;
            if (problems.FirstOrDefault(p => p.StartsWith($"Morph target \"{m.Name}\" ", StringComparison.Ordinal)) is { } problem) o["problem"] = problem;
            return (JsonNode)o;
        }).ToArray());
    }

    /// <summary>An object's displacements at a frame, numbered from 1, with every setting and the amplitude then.</summary>
    public static JsonArray Displacements(ObjectItem obj, double frame) =>
        new(obj.Displacements.Select((d, i) => (JsonNode)Displacement(d, i, frame)).ToArray());

    public static JsonObject Displacement(WaveDisplacement d, int index, double frame)
    {
        var o = new JsonObject
        {
            ["number"] = index + 1,
            ["channel"] = DisplacementChannel(index),
            ["shape"] = d.Shape.ToString().ToLowerInvariant(),
            ["direction"] = d.Direction.ToString().ToLowerInvariant(),
        };
        if (d.Shape == WaveShape.Wave) o["along"] = d.Along.ToString().ToLowerInvariant();
        else o["center"] = Vec(d.Center);
        o["wavelength"] = Round(d.Wavelength);
        o["speed"] = Round(d.Speed);
        o["phase"] = Round(d.Phase);
        o["amplitude"] = Round(d.Amplitude.Evaluate(frame));
        if (d.Amplitude.Keys.Count > 1) o["keyed"] = true;
        return o;
    }

    /// <summary>Where an item is in the world at a frame: an object's middle (its placed bounds' centre), else its pivot.</summary>
    public static Vec3 WorldPoint(SceneFrame sf, SceneItem item)
    {
        if (item is ObjectItem obj && sf.Objects.FirstOrDefault(x => x.Item == obj) is { Mesh.IsEmpty: false } of) return of.WorldMesh.Bounds().Center;
        return sf.WorldOf(item).TransformPoint(item.Pivot);
    }

    /// <summary>A channel's keys and behaviours.</summary>
    public static JsonObject Keys(string name, Envelope e, SceneModel scene)
    {
        var o = new JsonObject { ["channel"] = name };
        if (Keying.IsConstant(e, scene))
        {
            o["constant"] = Round(e.Evaluate(scene.FirstFrame));
            return o;
        }
        o["keys"] = new JsonArray(e.Keys.Select(k =>
        {
            var key = new JsonObject { ["frame"] = Round(k.Frame), ["value"] = Round(k.Value) };
            if (k.Tension != 0) key["tension"] = Round(k.Tension);
            if (k.Continuity != 0) key["continuity"] = Round(k.Continuity);
            if (k.Bias != 0) key["bias"] = Round(k.Bias);
            if (k.Linear) key["linear"] = true;
            return (JsonNode)key;
        }).ToArray());
        o["pre"] = Behavior(e.PreBehavior);
        o["post"] = Behavior(e.PostBehavior);
        return o;
    }

    public static string Behavior(EndBehavior b) => b == EndBehavior.OffsetRepeat ? "offset_repeat" : b.ToString().ToLowerInvariant();

    public static JsonArray Strings(IEnumerable<string> s) => new(s.Select(x => (JsonNode)x).ToArray());

    public static string Describe(SceneItem item) => $"{item.Kind.ToLowerInvariant()} \"{item.Name}\"";

    public static string Frames(double frame) => $"frame {F(frame)}";
}
