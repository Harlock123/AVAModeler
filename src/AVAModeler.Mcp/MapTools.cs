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
