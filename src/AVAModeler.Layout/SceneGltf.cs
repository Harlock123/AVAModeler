using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>
/// Exports a Layout scene as glTF 2.0 (.glb, or .gltf with its data inside it or in a .bin beside it): its objects
/// (each object's undeformed mesh and surfaces), nulls, cameras and lights (KHR_lights_punctual) as nodes in their
/// parent hierarchy, and their keyed motion as one animation sampled at every frame of the scene's range. Everything is
/// converted to glTF's right-handed, +Y up space (see <see cref="GltfBuilder"/>). Bones aren't nodes: an item parented
/// to a bone hangs from the nearest item above it that is, moving as it did.
/// </summary>
public static class SceneGltf
{
    /// <summary>The extensions a scene can be exported as.</summary>
    public static readonly string[] Extensions = [".glb", ".gltf"];

    /// <summary>
    /// The asset's bytes (and the .bin's, for <see cref="GltfPacking.Separate"/>), with notes on what glTF couldn't
    /// take: morphs, displacements and bones (objects are in their rest shape), an animated zoom or light intensity
    /// (the first frame's is used), hidden items (exported, as glTF has no hiding), placeholders (an empty node).
    /// </summary>
    public static (byte[] Main, byte[]? Bin, List<string> Warnings) Write(Scene.Scene scene, GltfPacking packing, string binName = "scene.bin")
    {
        var builder = new GltfBuilder();
        var warnings = new List<string>();
        var exported = scene.Items.Where(i => i is not Bone).ToList();
        var nodeOf = new Dictionary<SceneItem, int>();
        double first = scene.FirstFrame;
        var frames = Frames(scene);

        // Nodes first (so parents can be found), then the hierarchy, then each node's transform and motion.
        var meshOf = new Dictionary<Document, Dictionary<int, int>>();
        foreach (var item in exported)
        {
            var node = new JsonObject { ["name"] = item.Name };
            switch (item)
            {
                case ObjectItem o:
                    if (o.Document is not { } doc)
                    {
                        warnings.Add($"{o.Name} is a placeholder (its object couldn't be loaded), so it's an empty node.");
                        break;
                    }
                    if (o.Bones.Count > 0 || o.Morphs.Count > 0 || o.Displacements.Count > 0)
                        warnings.Add($"{o.Name}'s bones, morphs and displacements aren't exported: it's in its rest shape.");
                    // Objects sharing a document and layer share a mesh.
                    if (!meshOf.TryGetValue(doc, out var byLayer)) meshOf[doc] = byLayer = [];
                    int key = o.Layer ?? -1;
                    if (!byLayer.TryGetValue(key, out int mesh))
                        byLayer[key] = mesh = builder.AddMesh(o.Name, o.BaseMesh(), doc.Surfaces, SceneDrawing.ImageBase(o));
                    if (mesh >= 0) node["mesh"] = mesh;
                    break;
                case CameraItem c:
                    if (!c.Zoom.IsConstant) warnings.Add($"{c.Name}'s zoom is animated; glTF keeps the first frame's.");
                    node["camera"] = builder.AddCamera(new JsonObject
                    {
                        ["name"] = c.Name,
                        ["type"] = "perspective",
                        ["perspective"] = new JsonObject
                        {
                            ["yfov"] = Math.Round(Mat4.Deg(c.VerticalFov(first)), 9),
                            ["aspectRatio"] = Math.Round(c.FrameAspect, 9),
                            ["znear"] = 0.01,
                        },
                    });
                    break;
                case LightItem l:
                    if (!l.Intensity.IsConstant) warnings.Add($"{l.Name}'s intensity is animated; glTF keeps the first frame's.");
                    if (l.Type == LightType.Area) warnings.Add($"{l.Name} is an area light, which glTF hasn't got: it's exported as a spot light shining across its front.");
                    node["extensions"] = new JsonObject { [GltfBuilder.LightsExtension] = new JsonObject { ["light"] = builder.AddLight(Light(l, first)) } };
                    break;
            }
            if (!item.Visible) warnings.Add($"{item.Name} is hidden in Layout but exported: glTF has no hidden nodes.");
            nodeOf[item] = builder.AddNode(node);
        }

        SceneItem? ExportedParent(SceneItem item)
        {
            foreach (var a in item.Ancestors())
                if (nodeOf.ContainsKey(a)) return a;
            return null;
        }

        var roots = new List<int>();
        var channels = new JsonArray();
        var samplers = new JsonArray();
        int times = -1;
        foreach (var item in exported)
        {
            var parent = ExportedParent(item);
            if (parent == null) roots.Add(nodeOf[item]);
            else builder.AddChild(nodeOf[parent], nodeOf[item]);

            var samples = frames.Select(f => GltfBuilder.Decompose(Local(item, parent, f))).ToList();
            // Keep each quaternion on the same side as the one before, so interpolating between them goes the short way.
            for (int i = 1; i < samples.Count; i++)
                if (Dot(samples[i].R, samples[i - 1].R) < 0) samples[i] = (samples[i].T, [.. samples[i].R.Select(v => -v)], samples[i].S);
            var node = Node(builder, nodeOf[item]);
            var (t0, r0, s0) = samples[0];
            if (!Same(t0, [0, 0, 0])) node["translation"] = Numbers(t0);
            if (!Same(r0, [0, 0, 0, 1])) node["rotation"] = Numbers(r0);
            if (!Same(s0, [1, 1, 1])) node["scale"] = Numbers(s0);
            foreach (var (path, pick, size) in new (string, Func<(double[] T, double[] R, double[] S), double[]>, int)[]
                     { ("translation", s => s.T, 3), ("rotation", s => s.R, 4), ("scale", s => s.S, 3) })
            {
                if (samples.All(s => Same(pick(s), pick(samples[0])))) continue;
                if (times < 0) times = builder.AddFloats([.. frames.Select(f => (float)((f - first) / scene.FramesPerSecond))], 1, withBounds: true);
                int output = builder.AddFloats([.. samples.SelectMany(s => pick(s).Select(v => (float)v))], size, withBounds: false);
                samplers.Add(new JsonObject { ["input"] = times, ["output"] = output, ["interpolation"] = "LINEAR" });
                channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = nodeOf[item], ["path"] = path } });
            }
        }
        if (channels.Count > 0) builder.AddAnimation(new JsonObject { ["name"] = "Scene", ["channels"] = channels, ["samplers"] = samplers });
        if (scene.Bones.Any()) warnings.Add("Bones aren't exported as glTF skins.");
        var (main, bin) = builder.Build(roots, packing, binName);
        warnings.AddRange(builder.Warnings);
        return (main, bin, warnings);
    }

    /// <summary>
    /// Writes the scene to a .glb or .gltf file (and its .bin beside it when <paramref name="separateBin"/>), each in
    /// full to a temporary file first, so a failure leaves the old one. Returns the warnings.
    /// </summary>
    public static List<string> Save(Scene.Scene scene, string path, bool separateBin = false)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(ext)) throw new NotSupportedException($"A scene is exported to glTF as .glb or .gltf, not {(ext == "" ? "a file without an extension" : ext)}.");
        var packing = ext == ".glb" ? GltfPacking.Binary : separateBin ? GltfPacking.Separate : GltfPacking.Embedded;
        string binPath = Path.ChangeExtension(path, ".bin");
        var (main, bin, warnings) = Write(scene, packing, Path.GetFileName(binPath));
        if (bin != null) Replace(binPath, bin);
        Replace(path, main);
        return warnings;
    }

    /// <summary>The files exporting to a path writes: the file, and a .gltf's .bin when it goes beside it.</summary>
    public static string[] Targets(string path, bool separateBin) =>
        separateBin && path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) ? [path, Path.ChangeExtension(path, ".bin")] : [path];

    private static void Replace(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>The frames sampled: the scene's range by its step (at least the first frame).</summary>
    public static List<double> Frames(Scene.Scene scene)
    {
        int step = Math.Max(1, scene.FrameStep);
        var frames = new List<double>();
        for (int f = scene.FirstFrame; f <= scene.LastFrame; f += step) frames.Add(f);
        if (frames.Count == 0) frames.Add(scene.FirstFrame);
        return frames;
    }

    /// <summary>An item's transform relative to the exported item it hangs from (or the world), at a frame.</summary>
    private static Mat4 Local(SceneItem item, SceneItem? parent, double frame) =>
        parent == null ? item.WorldMatrix(frame) : parent.WorldMatrix(frame).Inverted() * item.WorldMatrix(frame);

    private static JsonObject Light(LightItem l, double frame)
    {
        var light = new JsonObject
        {
            ["name"] = l.Name,
            // glTF's lights are points: an area light goes as a spot shining across its front half.
            ["type"] = l.Type switch { LightType.Point => "point", LightType.Spot or LightType.Area => "spot", _ => "directional" },
            ["color"] = Numbers([GltfBuilder.ToLinear(Math.Clamp(l.Color.X, 0, 1)), GltfBuilder.ToLinear(Math.Clamp(l.Color.Y, 0, 1)), GltfBuilder.ToLinear(Math.Clamp(l.Color.Z, 0, 1))]),
            ["intensity"] = Math.Round(Math.Max(0, l.Intensity.Evaluate(frame)), 9),
        };
        if (l.Type != LightType.Distant && l.Range > 0) light["range"] = l.Range;
        if (l.Type == LightType.Spot)
        {
            double outer = Math.Clamp(l.ConeAngle, 0.1, 90);
            double inner = Math.Clamp(outer - Math.Max(l.EdgeAngle, 0), 0, outer - 0.05);
            light["spot"] = new JsonObject { ["innerConeAngle"] = Math.Round(Mat4.Deg(inner), 9), ["outerConeAngle"] = Math.Round(Mat4.Deg(outer), 9) };
        }
        if (l.Type == LightType.Area)
            light["spot"] = new JsonObject { ["innerConeAngle"] = 0.0, ["outerConeAngle"] = Math.Round(Math.PI / 2, 9) };
        return light;
    }

    private static JsonObject Node(GltfBuilder builder, int index) => builder.Node(index);

    private static JsonArray Numbers(double[] v) => new([.. v.Select(x => (JsonNode)JsonValue.Create(Math.Round(x, 9))!)]);

    private static double Dot(double[] a, double[] b) => a.Zip(b, (x, y) => x * y).Sum();

    private static bool Same(double[] a, double[] b) => a.Zip(b, (x, y) => Math.Abs(x - y) <= 1e-7).All(x => x);
}
