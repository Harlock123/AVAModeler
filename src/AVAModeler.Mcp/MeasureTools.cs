using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Geometry;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>measure: distances, angles, areas and sizes, for building to dimensions.</summary>
public sealed partial class ModelerTools
{
    [McpServerTool(Name = "measure", ReadOnly = true, Idempotent = true)]
    [Description("Measures, in metres: between two places (from and to), or between points of the current layer by index (two points: their distance and its x, y, z parts; three: also the angle at the middle one, degrees), or else the current selection (the chosen points in the order picked, the chosen polygons' area and, for one, its perimeter, and the size of what's active: everything, with nothing selected). Changes nothing. Use it to build to given dimensions, then check_mesh for volume.")]
    public Task<string> Measure(
        [Description("One place [x, y, z] in metres (with to).")] double[]? from = null,
        [Description("The other place [x, y, z].")] double[]? to = null,
        [Description("Points of the current layer by index (0-based): two for a distance, three for an angle at the middle one.")] int[]? points = null,
        CancellationToken cancel = default) =>
        Do("measured", c =>
        {
            var result = new JsonObject();
            if (from != null || to != null)
            {
                if (from == null || to == null) throw new ArgumentException("Give both from and to.");
                var a = Json.Vector(from, "from");
                var b = Json.Vector(to, "to");
                Distance(result, a, b);
                return result;
            }
            if (points != null)
            {
                var mesh = c.Document.Current.Mesh;
                if (points.Length is < 2 or > 3) throw new ArgumentException("Give two points for a distance or three for an angle.");
                foreach (int p in points) if (p < 0 || p >= mesh.Points.Count) throw new ArgumentException($"There's no point {p}: the current layer's points are numbered 0 to {mesh.Points.Count - 1}.");
                var pts = points.Select(p => mesh.Points[p]).ToArray();
                Distance(result, pts[0], pts[^1]);
                if (pts.Length == 3)
                {
                    var u = pts[0] - pts[1];
                    var v = pts[2] - pts[1];
                    result["distance"] = Census.Round((pts[1] - pts[0]).Length);
                    result["second_distance"] = Census.Round((pts[2] - pts[1]).Length);
                    if (u.LengthSquared > 0 && v.LengthSquared > 0)
                        result["angle"] = Math.Round(Math.Acos(Math.Clamp(Vec3.Dot(u, v) / (u.Length * v.Length), -1, 1)) * 180 / Math.PI, 4);
                    result.Remove("delta");
                }
                return result;
            }
            var m = SelectionMeasure.Of(c.Document);
            if (m.Distance is { } d) result["distance"] = Census.Round(d);
            if (m.Delta is { } dv) result["delta"] = Census.Vec(dv);
            if (m.Angle is { } an) result["angle"] = Math.Round(an, 4);
            result["points"] = m.Points;
            if (m.Polygons > 0)
            {
                result["polygons"] = m.Polygons;
                result["area"] = Census.Round(m.Area);
                if (m.Perimeter is { } per) result["perimeter"] = Census.Round(per);
            }
            if (!m.Bounds.IsEmpty) result["size"] = Census.Vec(m.Bounds.Size);
            result["text"] = new JsonArray([.. m.Lines().Select(l => (JsonNode)l)]);
            return result;
        }, cancel);

    private static void Distance(JsonObject result, Vec3 a, Vec3 b)
    {
        result["distance"] = Census.Round((b - a).Length);
        result["delta"] = Census.Vec(b - a);
    }
}
