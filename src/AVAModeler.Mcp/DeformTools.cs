using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using ModelContextProtocol.Server;
using static AVAModeler.Mcp.SceneCensus;

namespace AVAModeler.Mcp;

/// <summary>
/// Deforming objects in the scene: morph targets made from Modeler layers, wave and ripple displacements, and bones
/// made from skelegons. Each is a scene edit through <see cref="DoScene{T}"/>, so one step on Layout's undo history.
/// Morph targets are copies of a layer taken when the morph is made (or its target replaced), so later modeling
/// doesn't change them, and undo puts back the very mesh that was there.
/// </summary>
public sealed partial class ModelerTools
{
    private const string MorphHow = "Make a target as LightWave makes endomorphs: copy the base layer to an empty one with copy_layer, change the copy only with tools that move points (Move, Rotate, Size, Stretch, Bend, Taper, Twist, Magnet, Drag, Shear, Smooth, Jitter; select points first to move just some), never ones that add or remove points, so point i of the target is point i of the object. Put the object in the scene by its own layer (add_object layer 1): with no layer it merges every non-empty layer, the target's too.";

    // ───────────────────────────── layers ─────────────────────────────

    [McpServerTool(Name = "copy_layer", Destructive = true)]
    [Description("Copies a layer's geometry (points, polygons, UVs; the surfaces are shared) into another layer, as one undo step, as Copy and Paste between layers do but without the clipboard. The copy's points are in the same order, so it's the way to start a morph target: copy the base layer, then move points in the copy. The other layer must be empty unless replace is true. Layers are numbered 1 to 10. It doesn't change which layers are in the foreground: set_layers to work on the copy.")]
    public Task<string> CopyLayer(
        [Description("The layer to copy, 1 to 10.")] int from,
        [Description("The layer to copy into, 1 to 10.")] int to,
        [Description("Replace what's in the other layer (default false: it must be empty).")] bool replace = false,
        CancellationToken cancel = default) =>
        Do($"copied layer {from} to layer {to}", c =>
        {
            var doc = c.Document;
            foreach (var (n, what) in new[] { (from, "from"), (to, "to") })
                if (n < 1 || n > doc.Layers.Count) throw new ArgumentException($"There's no layer {n} ({what}): the object's layers are 1 to {doc.Layers.Count}.");
            if (from == to) throw new ArgumentException("from and to are the same layer.");
            var source = doc.Layers[from - 1].Mesh;
            if (source.IsEmpty) throw new ArgumentException($"Layer {from} is empty: there's nothing to copy.");
            if (!doc.Layers[to - 1].Mesh.IsEmpty && !replace)
                throw new ArgumentException($"Layer {to} isn't empty ({doc.Layers[to - 1].Mesh.Points.Count} points). Choose an empty layer, or set replace to throw away what's there.");
            var before = Census.Of(doc);
            doc.Edit($"Copy layer {from} to {to}", () => doc.Layers[to - 1].Mesh = source.Clone(), [to - 1]);
            var result = Census.Changes(doc, before, "Copy layer");
            result["from"] = from;
            result["to"] = to;
            result["points"] = source.Points.Count;
            result["polygons"] = source.Polygons.Count;
            return result;
        }, cancel);

    // ───────────────────────────── morphs ─────────────────────────────

    [McpServerTool(Name = "add_morph", Destructive = true)]
    [Description("Gives a scene object a morph target: a copy of a Modeler layer, taken now (later modeling doesn't change it), that the object can blend toward. The target must have exactly the object's point count, in the same order: point i of the object moves toward point i of the target. " + MorphHow + " The weight is a channel, morph.<name> (e.g. morph.Smile): 0 is the base shape, 1 the target, between blends, beyond 1 exaggerates; key it with set_key to animate (set_key channel \"morph.Smile\"). Several morphs add up. Morphs apply first, then bones, then displacements, all in the object's own coordinates.")]
    public Task<string> AddMorph(
        [Description("The scene object, by name or #index.")] string @object,
        [Description("The morph's name, unique on the object, e.g. \"Smile\". Its channel is morph.<name>.")] string name,
        [Description("The Modeler layer holding the target shape, 1 to 10.")] int layer,
        [Description("Its weight throughout, until keyed: 0 the base shape, 1 the target (default 1). Key it with set_key instead to animate.")] double weight = 1,
        CancellationToken cancel = default) =>
        DoScene("added a morph", (c, l) =>
        {
            var obj = SceneObject(l.Scene, @object);
            if (obj.IsPlaceholder) throw new ArgumentException($"{obj.Name} is a placeholder (its file couldn't be loaded), so it has no shape to morph.");
            string n = MorphName(obj, name, null);
            if (!double.IsFinite(weight)) throw new ArgumentException("weight must be a number (0 is the base shape, 1 the target).");
            var target = TargetLayer(c.Document, obj, layer);
            var morph = obj.AddMorph(new MorphTarget(n, target, new Envelope(weight)));
            CheckMorph(c.Document, obj, morph, layer);
            var result = ItemNow(c, l, obj, l.Frame);
            result["added"] = MorphChannel(morph);
            return result;
        }, cancel);

    [McpServerTool(Name = "set_morph", Destructive = true)]
    [Description("Changes a morph: renames it (its channel, morph.<name>, follows), replaces its target with a fresh copy of a Modeler layer (same point count, same order; " + MorphHow + "), or sets its weight throughout (0 base shape, 1 target). When the weight is keyed, change its keys with set_key on morph.<name> instead.")]
    public Task<string> SetMorph(
        [Description("The scene object, by name or #index.")] string @object,
        [Description("The morph's name, as get_scene lists it.")] string morph,
        [Description("A new name, unique on the object.")] string? name = null,
        [Description("A Modeler layer, 1 to 10, to copy as the new target.")] int? layer = null,
        [Description("The weight throughout: 0 the base shape, 1 the target.")] double? weight = null,
        CancellationToken cancel = default) =>
        DoScene("changed a morph", (c, l) =>
        {
            var obj = SceneObject(l.Scene, @object);
            var m = Morph(obj, morph);
            if (name == null && layer == null && weight == null) throw new ArgumentException("Give name, layer or weight (or several).");
            if (name != null) m.Name = MorphName(obj, name, m);
            if (layer is int n)
            {
                // A new mesh, not the old one changed: undo keeps targets by reference.
                m.Target = TargetLayer(c.Document, obj, n);
                CheckMorph(c.Document, obj, m, n);
            }
            if (weight is { } w)
            {
                if (!double.IsFinite(w)) throw new ArgumentException("weight must be a number (0 is the base shape, 1 the target).");
                if (Keying.IsAnimated(m.Weight)) throw new ArgumentException($"{MorphChannel(m)} is keyed: change its keys with set_key.");
                m.Weight.SetConstant(w);
            }
            l.Scene.Notify();
            return ItemNow(c, l, obj, l.Frame);
        }, cancel);

    [McpServerTool(Name = "remove_morph", Destructive = true)]
    [Description("Takes a morph off an object, with its weight keys. The Modeler's layers are untouched.")]
    public Task<string> RemoveMorph(
        [Description("The scene object, by name or #index.")] string @object,
        [Description("The morph's name, as get_scene lists it.")] string morph,
        CancellationToken cancel = default) =>
        DoScene("removed a morph", (c, l) =>
        {
            var obj = SceneObject(l.Scene, @object);
            var m = Morph(obj, morph);
            obj.RemoveMorph(m);
            var result = ItemNow(c, l, obj, l.Frame);
            result["removed"] = MorphChannel(m);
            return result;
        }, cancel);

    private static string MorphName(ObjectItem obj, string? name, MorphTarget? self)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A morph needs a name, e.g. \"Smile\".");
        string n = name.Trim();
        if (obj.Morphs.Any(m => m != self && string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{obj.Name} already has a morph called \"{n}\". Choose another name, or replace its target with set_morph.");
        return n;
    }

    /// <summary>A copy of a Modeler layer to be a morph target.</summary>
    private static Mesh TargetLayer(Document doc, ObjectItem obj, int layer)
    {
        CheckLayer(doc, layer);
        if (ReferenceEquals(obj.Document, doc) && obj.Layer == layer - 1)
            throw new ArgumentException($"Layer {layer} is {obj.Name}'s own layer, its base shape. Copy it to an empty layer with copy_layer, change the copy, and make that the target.");
        return doc.Layers[layer - 1].Mesh.Clone();
    }

    /// <summary>Refuses a target whose point count isn't the object's, with the scene's own explanation.</summary>
    private static void CheckMorph(Document doc, ObjectItem obj, MorphTarget m, int layer)
    {
        string? problem = obj.MorphProblems().FirstOrDefault(p => p.StartsWith($"Morph target \"{m.Name}\" ", StringComparison.Ordinal));
        if (problem == null) return;
        string why = problem.Replace("; it is ignored.", ".", StringComparison.Ordinal);
        string hint = ReferenceEquals(obj.Document, doc) && obj.Layer == null
            ? $" {obj.Name} is the Modeler's object with every non-empty layer merged, layer {layer} included: put just the base layer in the scene (add_object with layer) and give the morph to that."
            : " " + MorphHow;
        throw new ArgumentException($"{why} A morph target needs the same points as the object, in the same order.{hint}");
    }

    // ───────────────────────────── displacements ─────────────────────────────

    private const string DisplacementHow = "Each point moves along direction (an axis: x, y or z) by amplitude × sin(360° × (s − speed × frame) / wavelength + phase), in the object's own coordinates (after morphs and bones). A wave has straight crests: s is the point's coordinate along the along axis, so the crests are lines across that axis and travel along it (a flag in the XY plane: direction z, along x). A ripple has circular crests round center: s is the point's distance from center measured across direction (a pond in the XZ plane: direction y), so they spread out from it. Positive speed moves the crests toward +along, or outward for a ripple.";

    [McpServerTool(Name = "add_displacement", Destructive = true)]
    [Description("Adds a procedural wave or ripple displacement to a scene object, as Layout's displacements do. " + DisplacementHow + " The amplitude is a channel, displacement.<number>.amplitude (numbered from 1 in the order added, e.g. displacement.1.amplitude): key it with set_key to make the waves grow or die away. Needs enough points across the object to show (a Box with segments, or a subdivided shape).")]
    public Task<string> AddDisplacement(
        [Description("The scene object, by name or #index.")] string @object,
        [Description("wave (default: straight crests) or ripple (circular crests round center).")] string shape = "wave",
        [Description("The axis points move along: x, y (default) or z.")] string direction = "y",
        [Description("Wave only: the axis the crests travel along, not direction (default x, or z when direction is x).")] string? along = null,
        [Description("Ripple only: the centre [x, y, z] in the object's coordinates, metres (default [0, 0, 0]).")] double[]? center = null,
        [Description("Metres from one crest to the next, more than 0 (default 1).")] double wavelength = 1,
        [Description("Metres the crests travel each frame (default 0: they stand still).")] double speed = 0,
        [Description("Degrees to shift the wave along (default 0).")] double phase = 0,
        [Description("The height of the crests, metres (default 0.1). Key displacement.<number>.amplitude with set_key to animate it.")] double amplitude = 0.1,
        CancellationToken cancel = default) =>
        DoScene("added a displacement", (c, l) =>
        {
            var obj = SceneObject(l.Scene, @object);
            var d = new WaveDisplacement();
            SetDisplacement(d, shape, direction, along, center, wavelength, speed, phase, adding: true);
            if (!double.IsFinite(amplitude)) throw new ArgumentException("amplitude must be a number, in metres.");
            d.Amplitude.SetConstant(amplitude);
            obj.AddDisplacement(d);
            var result = ItemNow(c, l, obj, l.Frame);
            result["added"] = DisplacementChannel(obj.Displacements.Count - 1);
            return result;
        }, cancel);

    [McpServerTool(Name = "set_displacement", Destructive = true)]
    [Description("Changes a displacement's settings, leaving out what you don't give. " + DisplacementHow + " amplitude sets it throughout; when it's keyed, change its keys with set_key on displacement.<number>.amplitude instead.")]
    public Task<string> SetDisplacementTool(
        [Description("The scene object, by name or #index.")] string @object,
        [Description("Which displacement, numbered from 1 as get_scene lists them.")] int number,
        [Description("wave or ripple.")] string? shape = null,
        [Description("The axis points move along: x, y or z.")] string? direction = null,
        [Description("Wave: the axis the crests travel along.")] string? along = null,
        [Description("Ripple: the centre [x, y, z], metres.")] double[]? center = null,
        [Description("Metres from crest to crest, more than 0.")] double? wavelength = null,
        [Description("Metres the crests travel each frame.")] double? speed = null,
        [Description("Degrees to shift the wave along.")] double? phase = null,
        [Description("The crests' height, metres, throughout.")] double? amplitude = null,
        CancellationToken cancel = default) =>
        DoScene("changed a displacement", (c, l) =>
        {
            var obj = SceneObject(l.Scene, @object);
            var d = Displacement(obj, number);
            if (shape == null && direction == null && along == null && center == null && wavelength == null && speed == null && phase == null && amplitude == null)
                throw new ArgumentException("Give a setting to change: shape, direction, along, center, wavelength, speed, phase or amplitude.");
            SetDisplacement(d, shape, direction, along, center, wavelength, speed, phase, adding: false);
            if (amplitude is { } a)
            {
                if (!double.IsFinite(a)) throw new ArgumentException("amplitude must be a number, in metres.");
                if (Keying.IsAnimated(d.Amplitude)) throw new ArgumentException($"{DisplacementChannel(number - 1)} is keyed: change its keys with set_key.");
                d.Amplitude.SetConstant(a);
            }
            l.Scene.Notify();
            return ItemNow(c, l, obj, l.Frame);
        }, cancel);

    [McpServerTool(Name = "remove_displacement", Destructive = true)]
    [Description("Takes a displacement off an object, with its amplitude keys. Those after it move up a number (displacement 3 becomes 2, and so do their channels' names).")]
    public Task<string> RemoveDisplacement(
        [Description("The scene object, by name or #index.")] string @object,
        [Description("Which displacement, numbered from 1 as get_scene lists them.")] int number,
        CancellationToken cancel = default) =>
        DoScene("removed a displacement", (c, l) =>
        {
            var obj = SceneObject(l.Scene, @object);
            obj.RemoveDisplacement(Displacement(obj, number));
            var result = ItemNow(c, l, obj, l.Frame);
            result["removed"] = number;
            return result;
        }, cancel);

    /// <summary>Checks and sets a displacement's settings (the ones given).</summary>
    private static void SetDisplacement(WaveDisplacement d, string? shape, string? direction, string? along, double[]? center, double? wavelength, double? speed, double? phase, bool adding)
    {
        var s = shape == null ? d.Shape : Json.Choice<WaveShape>(shape, "shape");
        var dir = direction == null ? d.Direction : Json.Choice<Axis>(direction, "direction");
        Axis? on = along == null ? null : Json.Choice<Axis>(along, "along");
        if (s == WaveShape.Ripple && on != null) throw new ArgumentException("along is for a wave: a ripple spreads out from its center.");
        if (s == WaveShape.Wave && center != null) throw new ArgumentException("center is for a ripple: a wave's crests are straight, and travel along its along axis.");
        if (s == WaveShape.Wave && on == null && (adding || d.Along == dir)) on = dir == Axis.X ? Axis.Z : Axis.X;
        if (s == WaveShape.Wave && (on ?? d.Along) == dir)
            throw new ArgumentException($"along can't be direction ({dir.ToString().ToLowerInvariant()}): the crests travel across the way points move. Use one of the other two axes.");
        if (wavelength is { } w && (!double.IsFinite(w) || w <= 0)) throw new ArgumentException("wavelength must be more than 0 metres.");
        if (speed is { } v && !double.IsFinite(v)) throw new ArgumentException("speed must be a number, metres a frame.");
        if (phase is { } p && !double.IsFinite(p)) throw new ArgumentException("phase must be a number of degrees.");
        var middle = center == null ? (Vec3?)null : Vector(center, "center");
        d.Shape = s;
        d.Direction = dir;
        if (on is { } a) d.Along = a;
        if (middle is { } m) d.Center = m;
        if (wavelength is { } w2) d.Wavelength = w2;
        if (speed is { } v2) d.Speed = v2;
        if (phase is { } p2) d.Phase = p2;
    }

    // ───────────────────────────── skelegons ─────────────────────────────

    [McpServerTool(Name = "skelegons_to_bones", Destructive = true)]
    [Description("Makes bones from an object's skelegons, as Layout's Skelegons → Bones does. A skelegon is a 2-point polygon (a line) on a surface whose name starts with \"Skelegon\" (e.g. \"Skelegon_Arm\"), modeled with add_points and add_polygons: its first point is the bone's base, its second the tip. A skelegon whose base is on another's tip becomes that bone's child, so a chain of lines makes a chain of bones. Each bone is named after the rest of its surface's name (\"Arm\"), or \"Bone n\", made unique; it rests where the skelegon is, and its channels start at that rest pose. Key the bones' rotation (set_transform, set_key) to bend the object. Running it again adds another set.")]
    public Task<string> SkelegonsToBones(
        [Description("The scene object, by name or #index.")] string @object,
        CancellationToken cancel = default) =>
        DoScene("made bones from skelegons", (c, l) =>
        {
            var obj = SceneObject(l.Scene, @object);
            var bones = Skelegons.CreateBones(l.Scene, obj);
            if (bones.Count == 0)
                throw new ArgumentException($"{obj.Name} has no skelegons: 2-point polygons on a surface whose name starts with \"{Skelegons.SurfacePrefix}\" (e.g. add_polygons [[a, b]] with surface \"Skelegon_Arm\", a the base and b the tip).");
            var sf = l.Scene.Evaluate(l.Frame);
            return new JsonObject
            {
                ["object"] = obj.Name,
                ["bones"] = new JsonArray(bones.Select(b =>
                {
                    var (start, end) = b.RestSegment();
                    var o = SceneCensus.Item(l, b, sf, c.Document);
                    o["base"] = Census.Vec(start);
                    o["tip"] = Census.Vec(end);
                    return (JsonNode)o;
                }).ToArray()),
            };
        }, cancel);
}
