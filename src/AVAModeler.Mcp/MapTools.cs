using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>The weight-map tools: set and read weights, and an automatic rig from skelegons.</summary>
public sealed partial class ModelerTools
{
    [McpServerTool(Name = "set_weights", Destructive = true)]
    [Description("Gives points of the current layer a weight from 0 to 1 in a weight map (made if new), as one undo step: what a bone with that weight_map follows in Layout (a point moves with it by its weight; 0 takes a point out of the map). Give one weight for every point, or one each. With set_symmetry on, the mirror partners get the same weight in the map of the opposite name (\"Left Arm\" and \"Right Arm\") where there is one, else in the same map. Makes it the current weight map, shown in render_view's shaded pictures (blue 0, green 0.5, red 1). For brush-like painting run_tool \"Paint Weights\"; for smoothing, normalising and mirroring, \"Smooth Weights\", \"Normalize Weights\" and \"Mirror Weights\".")]
    public Task<string> SetWeights(
        [Description("The weight map's name, e.g. \"Upper Arm.L\" (the bone's name, so it's found by name).")] string map,
        [Description("The points, by index (0-based, as get_points gives them).")] int[] points,
        [Description("One weight for all of them, or one per point, each 0 to 1.")] double[] weights,
        CancellationToken cancel = default) =>
        Do($"set weights in {map}", c =>
        {
            var targets = points.ToList();
            var values = weights.ToList();
            if (c.Document.Symmetry is { } axis)
            {
                var mesh = c.Document.Current.Mesh;
                var pairs = Symmetry.Pairs(mesh, axis);
                string? opposite = MapNames.Opposite(map.Trim());
                var twin = new List<(int, double)>();
                for (int i = 0; i < points.Length; i++)
                    if (points[i] >= 0 && points[i] < pairs.Length && pairs[points[i]] is int j and >= 0 && j != points[i])
                        twin.Add((j, weights.Length == 1 ? weights[0] : i < weights.Length ? weights[i] : 0));
                if (twin.Count > 0)
                {
                    if (opposite != null)
                    {
                        c.Document.Group($"Set Weights in {map}", () =>
                        {
                            c.SetWeights(map, targets, values);
                            c.SetWeights(opposite, [.. twin.Select(t => t.Item1)], [.. twin.Select(t => t.Item2)]);
                        });
                        c.Document.CurrentWeightMap = c.Document.ShownPointMap = map.Trim();
                        return WeightsReport(c, map);
                    }
                    if (values.Count == 1) values = [.. Enumerable.Repeat(values[0], targets.Count)];
                    targets.AddRange(twin.Select(t => t.Item1));
                    values.AddRange(twin.Select(t => t.Item2));
                }
            }
            c.SetWeights(map, targets, values);
            return WeightsReport(c, map);
        }, cancel);

    [McpServerTool(Name = "get_weights", ReadOnly = true, Idempotent = true)]
    [Description("Reads a weight map of the current layer: every point in it with its weight (points not listed have 0), and the totals. With no map, lists the layer's weight and colour maps.")]
    public Task<string> GetWeights(
        [Description("The weight map's name; leave out to list the maps.")] string? map = null,
        CancellationToken cancel = default) =>
        Do("read weights", c =>
        {
            var mesh = c.Document.Current.Mesh;
            if (string.IsNullOrWhiteSpace(map)) return new JsonObject { ["maps"] = MapsJson(mesh) };
            var m = mesh.FindPointMap(map.Trim(), PointMapKind.Weight)
                ?? throw new ArgumentException(mesh.PointMaps.Count == 0 ? "The current layer has no weight maps: make one with set_weights or auto_weights." : $"There's no weight map \"{map}\". There are: {string.Join(", ", mesh.PointMaps.Select(x => x.Name))}.");
            var result = WeightsReport(c, m.Name);
            result["weights"] = new JsonArray([.. m.Values.OrderBy(kv => kv.Key).Select(kv => (JsonNode)new JsonArray(kv.Key, Math.Round(kv.Value[0], 4)))]);
            return result;
        }, cancel);

    [McpServerTool(Name = "auto_weights", Destructive = true)]
    [Description("An automatic rig: makes a weight map for every skelegon in the foreground (2-point polygons on a surface named Skelegon…, such as \"Skelegon_UpperArm\"), named as the bone Layout's skelegons_to_bones makes from it (\"UpperArm\"), so each bone follows its own map. Each point is shared among the skelegons by 1 / distance^falloff, normalised to add to 1, shares under threshold dropped. One undo step. Fix spots afterwards with set_weights or run_tool \"Paint Weights\", then check with render_view (the current map shows in colour).")]
    public Task<string> AutoWeights(
        [Description("How fast a skelegon's pull fades with distance, 1 to 16 (default 4): higher keeps each bone's area tighter.")] double falloff = 4,
        [Description("Shares below this are dropped and the rest normalised, 0 to 0.5 (default 0.01).")] double threshold = 0.01,
        CancellationToken cancel = default) =>
        Do("made weights from skelegons", c =>
        {
            c.Run("Weights from Skelegons", new Dictionary<string, object?> { ["FalloffPower"] = falloff, ["Threshold"] = threshold });
            var result = new JsonObject();
            var layers = new JsonArray();
            foreach (int l in c.Document.ForegroundLayers)
            {
                var mesh = c.Document.Layers[l].Mesh;
                if (mesh.PointMaps.Count == 0) continue;
                layers.Add(new JsonObject { ["layer"] = l + 1, ["maps"] = MapsJson(mesh) });
            }
            result["layers"] = layers;
            result["next"] = "In Layout: skelegons_to_bones gives each bone the map of its name; set_item weight_map changes it.";
            return result;
        }, cancel);

    private static JsonObject WeightsReport(Commands c, string map)
    {
        var mesh = c.Document.Current.Mesh;
        var m = mesh.FindPointMap(map.Trim(), PointMapKind.Weight);
        return new JsonObject
        {
            ["map"] = map.Trim(),
            ["points_weighted"] = m?.Values.Count ?? 0,
            ["of_points"] = mesh.Points.Count,
            ["maps"] = MapsJson(mesh),
        };
    }

    /// <summary>A mesh's point maps: name, kind and how many points are in each.</summary>
    internal static JsonArray MapsJson(Mesh mesh) =>
        new([.. mesh.PointMaps.Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["kind"] = m.Kind.ToString().ToLowerInvariant(), ["points"] = m.Values.Count })]);
}

public sealed partial class ModelerTools
{
    /// <summary>The brushes sculpt takes, by the name agents give, and the tool each is.</summary>
    internal static readonly Dictionary<string, string> BrushTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["draw"] = "Draw Brush", ["inflate"] = "Inflate Brush", ["smooth"] = "Smooth Brush", ["pinch"] = "Pinch Brush",
        ["flatten"] = "Flatten Brush", ["crease"] = "Crease Brush", ["grab"] = "Grab Brush",
        ["paint_weights"] = "Paint Weights", ["paint_colors"] = "Paint Colors",
    };

    [McpServerTool(Name = "sculpt", Destructive = true)]
    [Description("Runs a brush along a stroke over the current layer's surface, as dragging it in the app does, as one undo step: draw (raises along the surface's normal; invert digs), inflate (swells along each point's normal), smooth, pinch (draws points toward the stroke; sharpens), flatten, crease (a groove; invert a ridge), grab (moves what's under the stroke's first point by the stroke's last point minus its first), paint_weights or paint_colors. Each stroke point is moved onto the nearest place on the surface, then the stroke is spaced into dabs a quarter of the radius apart. A draw dab at strength 1 raises the centre by a tenth of the radius, so several passes (or a longer stroke) build up. With set_symmetry on, the mirror of the stroke works too. Brushes move the mesh's points: give the area enough points first (Subdivide, or Remesh). Check with render_view.")]
    public Task<string> Sculpt(
        [Description("draw, inflate, smooth, pinch, flatten, crease, grab, paint_weights or paint_colors.")] string brush,
        [Description("The stroke: points [x, y, z] in metres on (or near) the surface, in order. One point is one dab.")] double[][] stroke,
        [Description("How far a dab reaches, metres (default 0.25).")] double radius = 0.25,
        [Description("How much a dab does at its centre, 0 to 1 (default 0.5).")] double strength = 0.5,
        [Description("smooth (default), linear, sharp, dome or constant.")] string falloff = "smooth",
        [Description("Sculpt brushes: dig in instead of building up (or spread instead of pinching).")] bool invert = false,
        [Description("paint_weights: the weight map (made if new); paint_colors: the colour map.")] string? map = null,
        [Description("paint_weights: add (default), subtract, set or smooth.")] string? mode = null,
        [Description("paint_weights with mode set: the weight, 0 to 1.")] double? value = null,
        [Description("paint_colors: the colour [r, g, b], 0 to 255.")] double[]? color = null,
        [Description("How many times to run the stroke (default 1).")] int passes = 1,
        CancellationToken cancel = default) =>
        Do($"sculpted with {brush}", c =>
        {
            if (!BrushTools.TryGetValue(brush.Trim(), out var toolName))
                throw new ArgumentException($"There's no brush \"{brush}\": use {string.Join(", ", BrushTools.Keys)}.");
            if (stroke.Length == 0) throw new ArgumentException("A stroke needs at least one point.");
            if (!(radius > 0) || !double.IsFinite(radius)) throw new ArgumentException("radius must be more than 0 metres.");
            if (passes < 1 || passes > 50) throw new ArgumentException("passes must be from 1 to 50.");
            var points = stroke.Select((p, i) => Json.Vector(p, $"stroke point {i}")).ToList();
            int layer = c.Document.CurrentLayer;
            var mesh = c.Document.Layers[layer].Mesh;
            if (mesh.Polygons.All(p => p.Count < 3)) throw new ArgumentException("The current layer has no faces to sculpt: make or open an object first.");
            var bvh = Brushes.BvhOf(mesh);
            var onSurface = points.Select(p => bvh.Nearest(p)?.Point ?? p).ToList();
            var before = Census.Of(c.Document);
            var args = new Dictionary<string, object?>
            {
                ["Radius"] = radius, ["Strength"] = strength, ["Falloff"] = Json.Choice<BrushFalloff>(falloff, "falloff").ToString(),
            };
            var probe = c.Registry.Create(toolName);
            if (probe.Parameter("Invert") != null) args["Invert"] = invert;
            if (map != null) args["Map"] = map;
            if (mode != null) args["Mode"] = Json.Choice<WeightPaintMode>(mode, "mode").ToString();
            if (value is { } v) args["Value"] = v;
            if (color != null) args["Color"] = new Vec3(Json.Colour(color, "color").R, Json.Colour(color, "color").G, Json.Colour(color, "color").B);
            c.Document.Group($"Sculpt ({brush})", () =>
            {
                for (int pass = 0; pass < passes; pass++)
                {
                    var tool = (BrushTool)c.Registry.Create(toolName);
                    foreach (var (k, val) in args) tool.Parameter(k)!.Assign(val);
                    if (tool is GrabBrushTool grab)
                    {
                        grab.Center = onSurface[0];
                        grab.Offset = points[^1] - points[0];
                        tool.Run(c.Context);
                        continue;
                    }
                    tool.Begin(c.Context);
                    tool.SetStroke(layer, onSurface);
                    tool.Update();
                    tool.Commit();
                }
            });
            var result = Census.Changes(c.Document, before, toolName);
            result["dabs_per_pass"] = toolName == "Grab Brush" ? 1 : Brushes.Space(onSurface, Math.Max(radius * 0.25, 1e-6)).Count;
            if (c.Document.ShownPointMap is { } shown) result["shown_map"] = shown;
            return result;
        }, cancel);
}
