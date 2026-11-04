using System.Globalization;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Mcp;

/// <summary>check_mesh's answer: a <see cref="MeshReport"/> as JSON, layers numbered from 1, sizes in metres.</summary>
internal static class CheckReport
{
    public static JsonObject Json(MeshReport r, int examples)
    {
        var t = r.Types;
        var o = new JsonObject
        {
            ["layers_checked"] = new JsonArray(r.Layers.Select(l => (JsonNode)(l.Layer + 1)).ToArray()),
            ["verdict"] = r.Verdict,
            ["printable"] = r.Printable,
            ["watertight"] = r.Watertight,
            ["issues"] = Strings(r.Issues()),
            ["notes"] = Strings(r.Notes()),
            ["points"] = r.Points,
            ["polygons"] = Types(r.Polygons, t),
            ["bounds"] = r.Bounds.IsEmpty ? null : Census.BoxJson(r.Bounds),
            ["area_m2"] = Sig(r.Area),
            ["volume_m3"] = r.Volume is { } v ? Sig(v) : null,
            ["center_of_mass"] = r.CenterOfMass is { } c ? Vec(c) : null,
            ["shells"] = r.Shells,
            ["holes"] = r.Holes,
            ["boundary_edges"] = r.BoundaryEdges,
            ["non_manifold_edges"] = r.NonManifoldEdges,
            ["inconsistent_edges"] = r.InconsistentEdges,
            ["flipped_polygons"] = r.FlippedPolygons,
            ["inward_shells"] = r.InwardShells,
            ["cavities"] = r.Cavities,
            ["degenerate_polygons"] = r.DegeneratePolygons,
            ["non_planar_polygons"] = r.NonPlanarPolygons,
            ["duplicate_points"] = r.DuplicatePoints,
            ["unused_points"] = r.UnusedPoints,
            ["points_and_lines"] = r.PointsAndLines,
            ["how_to_fix"] = Strings(Fixes(r)),
            ["per_layer"] = new JsonArray(r.Layers.Select(l => (JsonNode)Layer(l, examples)).ToArray()),
        };
        if (r.Volume == null && r.Faces > 0)
            o["volume_note"] = "No volume: the mesh must be closed (no holes, no edge shared by 3 or more polygons) with consistent winding first.";
        return o;
    }

    private static JsonObject Layer(LayerCheck l, int examples)
    {
        var o = new JsonObject
        {
            ["layer"] = l.Layer + 1,
            ["points"] = l.Points,
            ["polygons"] = Types(l.Polygons, l.Types),
            ["bounds"] = l.Bounds.IsEmpty ? null : Census.BoxJson(l.Bounds),
            ["area_m2"] = Sig(l.Area),
            ["volume_m3"] = l.Volume is { } v ? Sig(v) : null,
            ["center_of_mass"] = l.CenterOfMass is { } c ? Vec(c) : null,
            ["watertight"] = l.Watertight,
            ["holes"] = l.Holes,
            ["shells"] = new JsonArray(l.Shells.Select(s => (JsonNode)new JsonObject
            {
                ["faces"] = s.Faces.Count,
                ["closed"] = s.Closed,
                ["consistent"] = s.Consistent,
                ["volume_m3"] = s.Volume is { } sv ? Sig(sv) : null,
                ["facing"] = s.Volume is null ? null : s.Cavity ? "inward (a cavity inside another shell)" : s.Inward ? "inward (inside out)" : "outward",
                ["bounds"] = Census.BoxJson(s.Bounds),
            }).Take(Math.Max(examples, 1)).ToArray()),
        };
        // Which elements, a few of each kind, to select or look at (indices from 0 in this layer).
        var ex = new JsonObject();
        void Edges(string name, List<Edge> list)
        {
            if (list.Count > 0) ex[name] = new JsonArray(list.Take(examples).Select(e => (JsonNode)new JsonArray(e.A, e.B)).ToArray());
        }
        void Indices(string name, List<int> list)
        {
            if (list.Count > 0) ex[name] = new JsonArray(list.Take(examples).Select(i => (JsonNode)i).ToArray());
        }
        Edges("boundary_edges", l.BoundaryEdges);
        Edges("non_manifold_edges", l.NonManifoldEdges);
        Edges("inconsistent_edges", l.InconsistentEdges);
        Indices("flipped_polygons", l.FlippedPolygons);
        Indices("degenerate_polygons", l.DegeneratePolygons);
        Indices("non_planar_polygons", l.NonPlanarPolygons);
        Indices("duplicate_points", l.DuplicatePoints);
        Indices("unused_points", l.UnusedPoints);
        Indices("points_and_lines", l.PointsAndLines);
        if (examples > 0) o["examples"] = ex;
        return o;
    }

    /// <summary>What to run for each kind of trouble found, with the tools that already do it.</summary>
    private static List<string> Fixes(MeshReport r)
    {
        var list = new List<string>();
        void Add(bool when, string text)
        {
            if (when) list.Add(text);
        }
        Add(r.BoundaryEdges > 0, "Holes: run_tool \"Select Problems\" {\"Problem\": \"BoundaryEdges\"} and render_view to see their rims. Rims that lie on top of each other are duplicate points: Merge Points welds them. Otherwise close a hole with add_polygons (or Make Polygon) through its rim's points, clockwise seen from outside.");
        Add(r.NonManifoldEdges > 0, "Non-manifold edges: select them with \"Select Problems\" {\"Problem\": \"NonManifoldEdges\"}. Usually a face left inside a solid (Remove it) or two solids sharing an edge (Boolean Union them).");
        Add(r.InconsistentEdges > 0 || r.InwardShells > 0, "Winding: run_tool \"Align\" turns faces to agree with their neighbours and closed shells to face outward (\"Select Problems\" {\"Problem\": \"FlippedPolygons\"} shows which it would turn).");
        Add(r.DegeneratePolygons > 0, "Degenerate polygons: run_tool \"Remove Degenerate\".");
        Add(r.DuplicatePoints > 0, "Duplicate points: run_tool \"Merge Points\" (Distance 0 merges only points in the same place).");
        Add(r.UnusedPoints > 0, "Unused points: run_tool \"Remove Degenerate\" (with Remove unused points on, the default) deletes them.");
        Add(r.NonPlanarPolygons > 0, "Non-planar polygons: select them with \"Select Problems\" {\"Problem\": \"NonPlanarPolygons\"}, then run_tool \"Triple\" splits them into flat triangles.");
        return list;
    }

    private static JsonObject Types(int total, (int Points, int Lines, int Triangles, int Quads, int NGons) t) => new()
    {
        ["total"] = total, ["triangles"] = t.Triangles, ["quads"] = t.Quads, ["ngons"] = t.NGons, ["lines"] = t.Lines, ["dots"] = t.Points,
    };

    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode)s).ToArray());

    /// <summary>A number to six significant figures (small parts' volumes are tiny in m³).</summary>
    private static double Sig(double v) => double.Parse(MeshReport.Num(v), CultureInfo.InvariantCulture) + 0.0;

    private static JsonArray Vec(Vec3 v) => new(Sig(v.X), Sig(v.Y), Sig(v.Z));
}
