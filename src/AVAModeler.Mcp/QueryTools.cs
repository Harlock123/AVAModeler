using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Geometry;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// Selecting by what geometry is rather than by number (select_where), selections kept by name across steps
/// (save_selection, select_saved, list_selections), and checking work against expectations in one call (expect).
/// </summary>
public sealed partial class ModelerTools
{
    private sealed record SavedSelection(string Name, int Layer, SelectionMode Mode, int[] PointIds, int[] PolygonIds);

    private readonly Dictionary<string, SavedSelection> _selections = new(StringComparer.Ordinal);

    // ───────────────────────────── select_where ─────────────────────────────

    [McpServerTool(Name = "select_where", Idempotent = true)]
    [Description("Selects the current layer's polygons (or points) by what they are, rather than by index: every condition given must hold (leave out the ones you don't need). For polygons, places are their centres; for points, their positions. mode replaces the selection (default), adds to it, removes from it or keeps only what's in both (intersect). Switches to Polygons or Points mode, like select_polygons. Returns how many matched and were selected, their ids (the first 100) and the selection. Not an undo step. Example: the top faces of a seat: {\"surface\": \"Seat\", \"normal\": [0, 1, 0], \"within_degrees\": 20}.")]
    public Task<string> SelectWhere(
        [Description("\"polygons\" (default) or \"points\".")] string what = "polygons",
        [Description("On this surface (points: used by a polygon on it).")] string? surface = null,
        [Description("The axis for above and below: x, y or z.")] string? axis = null,
        [Description("Centre (or point) at or above this value along axis, in metres.")] double? above = null,
        [Description("Centre (or point) at or below this value along axis, in metres.")] double? below = null,
        [Description("Polygons facing this way: a direction [x, y, z] (it needn't be unit length), within within_degrees.")] double[]? normal = null,
        [Description("How far a polygon's normal may be from normal, in degrees (default 30).")] double within_degrees = 30,
        [Description("Polygons with at least this area, in square metres.")] double? min_area = null,
        [Description("Polygons with at most this area, in square metres.")] double? max_area = null,
        [Description("Polygons with at least this many points (3 a triangle, 4 a quad).")] int? min_vertices = null,
        [Description("Polygons with at most this many points.")] int? max_vertices = null,
        [Description("A weight map: points whose weight is within min_weight..max_weight (polygons: every point of it).")] string? weight_map = null,
        [Description("The least weight (default 0).")] double min_weight = 0,
        [Description("The most weight (default 1).")] double max_weight = 1,
        [Description("Near this place [x, y, z] (with within).")] double[]? near = null,
        [Description("How near to near, in metres.")] double? within = null,
        [Description("Inside a box from this corner [x, y, z] (with box_max).")] double[]? box_min = null,
        [Description("The box's other corner [x, y, z].")] double[]? box_max = null,
        [Description("Only what's in this saved selection (save_selection).")] string? saved = null,
        [Description("replace (default), add, remove or intersect.")] string mode = "replace",
        CancellationToken cancel = default) =>
        Do("selected by query", c =>
        {
            var doc = c.Document;
            bool points = (what ?? "polygons").Trim().ToLowerInvariant() switch
            {
                "polygons" or "polygon" or "faces" => false,
                "points" or "point" => true,
                _ => throw new ArgumentException($"what must be \"polygons\" or \"points\", not \"{what}\"."),
            };
            string how = (mode ?? "replace").Trim().ToLowerInvariant();
            if (how is not ("replace" or "add" or "remove" or "intersect"))
                throw new ArgumentException($"mode must be replace, add, remove or intersect, not \"{mode}\".");
            if (points && (normal != null || min_area != null || max_area != null || min_vertices != null || max_vertices != null))
                throw new ArgumentException("normal, min_area, max_area, min_vertices and max_vertices are for polygons: give what \"polygons\", or leave them out.");
            Axis? along = axis == null ? null : Symmetry.Parse(axis) ?? throw new ArgumentException($"axis must be x, y or z, not \"{axis}\".");
            if ((above != null || below != null) && along == null) throw new ArgumentException("above and below need an axis (x, y or z).");
            if ((near == null) != (within == null)) throw new ArgumentException("near and within go together: a place and a distance.");
            if ((box_min == null) != (box_max == null)) throw new ArgumentException("box_min and box_max go together.");
            Vec3? toward = normal == null ? null : Json.Vector(normal, "normal");
            if (toward is { } t && t.Length < 1e-12) throw new ArgumentException("normal needs a direction, not [0, 0, 0].");
            Vec3? centre = near == null ? null : Json.Vector(near, "near");
            Box3? box = box_min == null ? null : Box3.Of([Json.Vector(box_min, "box_min"), Json.Vector(box_max!, "box_max")]);

            int layer = doc.CurrentLayer;
            var mesh = doc.Layers[layer].Mesh;
            int surfaceIndex = -1;
            if (surface != null)
            {
                surfaceIndex = doc.Surfaces.FindIndex(s => s.Name == surface);
                if (surfaceIndex < 0) throw new ArgumentException($"There's no surface called \"{surface}\". The object has: {string.Join(", ", doc.Surfaces.Select(s => s.Name))}.");
            }
            PointMap? weights = null;
            if (weight_map != null)
                weights = mesh.FindPointMap(weight_map, PointMapKind.Weight) ?? throw new ArgumentException($"Layer {layer + 1} has no weight map called \"{weight_map}\".");
            HashSet<int>? inSaved = null;
            if (saved != null) inSaved = [.. Resolve(Saved(saved, layer), points).Found];

            double cosLimit = Math.Cos(Math.Clamp(within_degrees, 0, 180) * Math.PI / 180);
            bool Place(Vec3 p) =>
                (along is not { } ax || (above is not { } lo || p[ax] >= lo - 1e-9) && (below is not { } hi || p[ax] <= hi + 1e-9))
                && (centre is not { } ctr || (p - ctr).Length <= within!.Value + 1e-9)
                && (box is not { } b || b.Contains(p, 1e-9));
            bool Weighed(int point) => weights == null || weights.Weight(point) is var w && w >= min_weight - 1e-9 && w <= max_weight + 1e-9;

            var matched = new List<int>();
            if (points)
            {
                HashSet<int>? onSurface = surfaceIndex < 0 ? null
                    : [.. mesh.Polygons.Where(p => p.Surface == surfaceIndex).SelectMany(p => p.Points)];
                for (int i = 0; i < mesh.Points.Count; i++)
                    if ((onSurface == null || onSurface.Contains(i)) && Place(mesh.Points[i]) && Weighed(i) && (inSaved == null || inSaved.Contains(i)))
                        matched.Add(i);
            }
            else
            {
                for (int i = 0; i < mesh.Polygons.Count; i++)
                {
                    var poly = mesh.Polygons[i];
                    if (surfaceIndex >= 0 && poly.Surface != surfaceIndex) continue;
                    if (min_vertices is { } lo && poly.Count < lo || max_vertices is { } hi && poly.Count > hi) continue;
                    if (inSaved != null && !inSaved.Contains(i)) continue;
                    if (!Place(mesh.Centroid(poly))) continue;
                    if (min_area != null || max_area != null)
                    {
                        double area = mesh.Area(poly);
                        if (min_area is { } a0 && area < a0 - 1e-12 || max_area is { } a1 && area > a1 + 1e-12) continue;
                    }
                    if (toward is { } dir)
                    {
                        var n = mesh.Normal(poly);
                        if (n.Length < 1e-12 || Vec3.Dot(n.Normalized(), dir.Normalized()) < cosLimit - 1e-9) continue;
                    }
                    if (weights != null && !poly.Points.All(Weighed)) continue;
                    matched.Add(i);
                }
            }

            var current = points ? doc.Selection.PointsIn(layer).ToHashSet() : doc.Selection.PolygonsIn(layer).ToHashSet();
            var chosen = how switch
            {
                "add" => current.Union(matched),
                "remove" => current.Except(matched),
                "intersect" => current.Intersect(matched),
                _ => matched,
            };
            var final = chosen.Order().ToList();
            c.SelectMode(points ? SelectionMode.Points : SelectionMode.Polygons);
            c.SelectNone();
            if (points) c.SelectPoints(final); else c.SelectPolygons(final);

            var known = _ids.Of(layer);
            var idList = points ? known.PointIds : known.PolygonIds;
            return new JsonObject
            {
                ["matched"] = matched.Count,
                ["selected"] = final.Count,
                ["ids"] = new JsonArray([.. final.Take(100).Where(i => i < idList.Length).Select(i => (JsonNode)idList[i])]),
                ["selection"] = Census.Selection(doc),
            };
        }, cancel);

    // ───────────────────────────── saved selections ─────────────────────────────

    [McpServerTool(Name = "save_selection")]
    [Description("Keeps the current layer's selected points and polygons by name, by their ids, so select_saved can select them again after other tools have renumbered the mesh (\"wheel_left\", \"seat_top\"). The same name again replaces it; remove true forgets it instead. Kept for this session. Returns what it holds.")]
    public Task<string> SaveSelection(
        [Description("The selection's name.")] string name,
        [Description("Forget the saved selection of this name instead.")] bool remove = false,
        CancellationToken cancel = default) =>
        Do("saved a selection", c =>
        {
            var key = (name ?? "").Trim();
            if (key.Length == 0) throw new ArgumentException("Give the selection a name.");
            if (remove)
                return _selections.Remove(key)
                    ? new JsonObject { ["removed"] = key }
                    : throw new ArgumentException($"There's no saved selection called \"{key}\".");
            var doc = c.Document;
            int layer = doc.CurrentLayer;
            var known = _ids.Of(layer);
            int[] pointIds = [.. doc.Selection.PointsIn(layer).Where(i => i < known.PointIds.Length).Order().Select(i => known.PointIds[i])];
            int[] polygonIds = [.. doc.Selection.PolygonsIn(layer).Where(i => i < known.PolygonIds.Length).Order().Select(i => known.PolygonIds[i])];
            if (pointIds.Length + polygonIds.Length == 0) throw new ArgumentException("Nothing is selected in the current layer: select something first.");
            var s = new SavedSelection(key, layer, doc.Selection.Mode, pointIds, polygonIds);
            _selections[key] = s;
            return Describe(s);
        }, cancel);

    [McpServerTool(Name = "select_saved", Idempotent = true)]
    [Description("Selects a saved selection (save_selection) again, by its ids, wherever its points and polygons are now. Its layer must be the current one. Replaces the selection unless add is true. Any that are gone (deleted, or rebuilt by a tool) are counted as missing. Not an undo step.")]
    public Task<string> SelectSaved(
        [Description("The saved selection's name.")] string name,
        [Description("Add to what's selected rather than replacing it.")] bool add = false,
        CancellationToken cancel = default) =>
        Do("selected a saved selection", c =>
        {
            var doc = c.Document;
            var s = Saved(name, doc.CurrentLayer);
            bool points = s.PolygonIds.Length == 0 || s.Mode == SelectionMode.Points;
            var (found, missing) = Resolve(s, points);
            if (found.Count == 0) throw new InvalidOperationException($"None of \"{s.Name}\"'s {(points ? "points" : "polygons")} is still there: edits since removed or rebuilt them all.");
            c.SelectMode(points ? SelectionMode.Points : SelectionMode.Polygons);
            if (!add) c.SelectNone();
            if (points) c.SelectPoints(found); else c.SelectPolygons(found);
            var json = new JsonObject { ["name"] = s.Name, ["selected"] = found.Count, ["selection"] = Census.Selection(doc) };
            if (missing > 0) json["missing"] = missing;
            return json;
        }, cancel);

    [McpServerTool(Name = "list_selections", ReadOnly = true, Idempotent = true)]
    [Description("Lists the saved selections: each one's name, layer, mode, how many points and polygons it holds and how many of them are still there.")]
    public Task<string> ListSelections(CancellationToken cancel = default) =>
        Do("listed saved selections", c => new JsonObject
        {
            ["selections"] = new JsonArray([.. _selections.Values.Select(s => (JsonNode)Describe(s))]),
        }, cancel);

    private SavedSelection Saved(string name, int layer)
    {
        if (!_selections.TryGetValue((name ?? "").Trim(), out var s))
            throw new ArgumentException(_selections.Count == 0
                ? $"There's no saved selection called \"{name}\": save one with save_selection."
                : $"There's no saved selection called \"{name}\". Saved: {string.Join(", ", _selections.Keys.Select(k => $"\"{k}\""))}.");
        if (s.Layer != layer) throw new ArgumentException($"\"{s.Name}\" is in layer {s.Layer + 1}: make it the current layer first (set_layers).");
        return s;
    }

    /// <summary>A saved selection's current indices (points, or polygons), and how many of its ids are gone.</summary>
    private (List<int> Found, int Missing) Resolve(SavedSelection s, bool points)
    {
        var known = _ids.Of(s.Layer);
        var map = GeometryIds.IndexOf(points ? known.PointIds : known.PolygonIds);
        var ids = points ? s.PointIds : s.PolygonIds;
        var found = new List<int>();
        int missing = 0;
        foreach (var id in ids)
            if (map.TryGetValue(id, out int i)) found.Add(i); else missing++;
        return (found, missing);
    }

    private JsonObject Describe(SavedSelection s)
    {
        var known = _ids.Of(s.Layer);
        var points = known.PointIds.ToHashSet();
        var polygons = known.PolygonIds.ToHashSet();
        return new JsonObject
        {
            ["name"] = s.Name,
            ["layer"] = s.Layer + 1,
            ["mode"] = s.Mode.ToString().ToLowerInvariant(),
            ["points"] = s.PointIds.Length,
            ["polygons"] = s.PolygonIds.Length,
            ["points_there"] = s.PointIds.Count(points.Contains),
            ["polygons_there"] = s.PolygonIds.Count(polygons.Contains),
        };
    }

    // ───────────────────────────── expect ─────────────────────────────

    [McpServerTool(Name = "expect", ReadOnly = true, Idempotent = true)]
    [Description("Checks the object against what it should be, in one call: every condition given is a check (leave out the ones you don't need), and the answer says which passed, with what was expected and what was found. Use it as the \"am I done?\" step of a build, check, fix loop. With fail true a failed check makes the call fail, so as the last step of a batch it takes the whole batch back. Checks the foreground layers unless layers is given. Changes nothing.")]
    public Task<string> Expect(
        [Description("The layers to check, numbered from 1. Default: the foreground layers.")] int[]? layers = null,
        [Description("Closed: no holes and no edge shared by three or more polygons (true), or open (false).")] bool? watertight = null,
        [Description("Ready to print: watertight, consistently wound, facing out, no degenerate faces, as check_mesh says.")] bool? printable = null,
        [Description("At most this many polygons.")] int? max_polygons = null,
        [Description("At least this many polygons.")] int? min_polygons = null,
        [Description("At most this many points.")] int? max_points = null,
        [Description("At least this many points.")] int? min_points = null,
        [Description("Everything lies at or beyond this corner [x, y, z] (metres).")] double[]? bounds_min = null,
        [Description("Everything lies at or before this corner [x, y, z] (metres).")] double[]? bounds_max = null,
        [Description("The size is at most this [x, y, z] (metres).")] double[]? size_max = null,
        [Description("The size is at least this [x, y, z] (metres).")] double[]? size_min = null,
        [Description("Symmetric across the plane through the origin square to this axis (x, y or z): every point has a mirror partner.")] string? symmetric = null,
        [Description("The enclosed volume is at least this, in cubic metres (it must be closed).")] double? volume_min = null,
        [Description("The enclosed volume is at most this, in cubic metres.")] double? volume_max = null,
        [Description("Each of these surfaces is on at least one polygon.")] string[]? surfaces_used = null,
        [Description("The polygons with polygon_ids (in the current layer) are on this surface.")] string? surface = null,
        [Description("Polygon ids for surface.")] int[]? polygon_ids = null,
        [Description("Exactly this many polygons are selected (the current layer).")] int? selected_polygons = null,
        [Description("Exactly this many points are selected (the current layer).")] int? selected_points = null,
        [Description("Make the call fail if any check fails (so a batch it ends is taken back).")] bool fail = false,
        CancellationToken cancel = default) =>
        Do("checked expectations", c =>
        {
            var doc = c.Document;
            var which = layers is { Length: > 0 } ? [.. layers.Select(n => Census.Layer(doc, n).Layer)] : doc.ForegroundLayers.ToList();
            var report = MeshCheck.Layers(doc, which);
            var checks = new JsonArray();
            void Check(string name, bool ok, JsonNode? expected, JsonNode? actual) =>
                checks.Add(new JsonObject { ["check"] = name, ["passed"] = ok, ["expected"] = expected, ["actual"] = actual });
            JsonArray Vec(Vec3 v) => new(Census.Round(v.X), Census.Round(v.Y), Census.Round(v.Z));

            if (watertight is { } w) Check("watertight", report.Watertight == w, w, report.Watertight);
            if (printable is { } p) Check("printable", report.Printable == p, p, report.Printable);
            if (max_polygons is { } maxPolys) Check("max_polygons", report.Polygons <= maxPolys, maxPolys, report.Polygons);
            if (min_polygons is { } minPolys) Check("min_polygons", report.Polygons >= minPolys, minPolys, report.Polygons);
            if (max_points is { } maxPts) Check("max_points", report.Points <= maxPts, maxPts, report.Points);
            if (min_points is { } minPts) Check("min_points", report.Points >= minPts, minPts, report.Points);
            var bounds = report.Bounds;
            bool empty = report.Points == 0;
            if (bounds_min != null)
            {
                var lo = Json.Vector(bounds_min, "bounds_min");
                Check("bounds_min", !empty && bounds.Min.X >= lo.X - 1e-9 && bounds.Min.Y >= lo.Y - 1e-9 && bounds.Min.Z >= lo.Z - 1e-9, Vec(lo), empty ? null : Vec(bounds.Min));
            }
            if (bounds_max != null)
            {
                var hi = Json.Vector(bounds_max, "bounds_max");
                Check("bounds_max", !empty && bounds.Max.X <= hi.X + 1e-9 && bounds.Max.Y <= hi.Y + 1e-9 && bounds.Max.Z <= hi.Z + 1e-9, Vec(hi), empty ? null : Vec(bounds.Max));
            }
            var size = empty ? new Vec3(0, 0, 0) : bounds.Max - bounds.Min;
            if (size_max != null)
            {
                var s = Json.Vector(size_max, "size_max");
                Check("size_max", size.X <= s.X + 1e-9 && size.Y <= s.Y + 1e-9 && size.Z <= s.Z + 1e-9, Vec(s), Vec(size));
            }
            if (size_min != null)
            {
                var s = Json.Vector(size_min, "size_min");
                Check("size_min", size.X >= s.X - 1e-9 && size.Y >= s.Y - 1e-9 && size.Z >= s.Z - 1e-9, Vec(s), Vec(size));
            }
            if (symmetric != null)
            {
                var ax = Symmetry.Parse(symmetric) ?? throw new ArgumentException($"symmetric must be x, y or z, not \"{symmetric}\".");
                int unpaired = which.Sum(l => Symmetry.Pairs(doc.Layers[l].Mesh, ax).Count(x => x < 0));
                Check($"symmetric_{ax.ToString().ToLowerInvariant()}", unpaired == 0, "every point paired", unpaired == 0 ? "every point paired" : $"{unpaired} points without a mirror partner");
            }
            if (volume_min != null || volume_max != null)
            {
                var v = report.Volume;
                if (volume_min is { } vmin) Check("volume_min", v is { } a && a >= vmin - 1e-12, vmin, v is { } x ? Census.Round(x, 9) : "not closed");
                if (volume_max is { } vmax) Check("volume_max", v is { } b && b <= vmax + 1e-12, vmax, v is { } y ? Census.Round(y, 9) : "not closed");
            }
            if (surfaces_used != null)
            {
                var used = which.SelectMany(l => doc.Layers[l].Mesh.Polygons).Select(pl => pl.Surface).Where(i => i >= 0 && i < doc.Surfaces.Count).Select(i => doc.Surfaces[i].Name).ToHashSet();
                foreach (var name in surfaces_used) Check($"surface_used: {name}", used.Contains(name), true, used.Contains(name));
            }
            if (surface != null || polygon_ids != null)
            {
                if (surface == null || polygon_ids == null) throw new ArgumentException("surface and polygon_ids go together.");
                int layer = doc.CurrentLayer;
                var known = _ids.Of(layer);
                var map = GeometryIds.IndexOf(known.PolygonIds);
                var mesh = doc.Layers[layer].Mesh;
                var wrong = new JsonArray();
                foreach (var id in polygon_ids)
                {
                    if (!map.TryGetValue(id, out int i)) { wrong.Add(new JsonObject { ["id"] = id, ["surface"] = "gone" }); continue; }
                    var name = mesh.Polygons[i].Surface is var si && si >= 0 && si < doc.Surfaces.Count ? doc.Surfaces[si].Name : $"#{si}";
                    if (name != surface) wrong.Add(new JsonObject { ["id"] = id, ["surface"] = name });
                }
                Check($"surface: {surface}", wrong.Count == 0, $"{polygon_ids.Length} polygons on {surface}", wrong.Count == 0 ? "all of them" : wrong);
            }
            if (selected_polygons is { } sp) Check("selected_polygons", doc.Selection.PolygonsIn(doc.CurrentLayer).Count == sp, sp, doc.Selection.PolygonsIn(doc.CurrentLayer).Count);
            if (selected_points is { } spt) Check("selected_points", doc.Selection.PointsIn(doc.CurrentLayer).Count == spt, spt, doc.Selection.PointsIn(doc.CurrentLayer).Count);
            if (checks.Count == 0) throw new ArgumentException("Give at least one check (watertight, max_polygons, bounds_max, symmetric and so on).");

            var failed = checks.Where(x => !(bool)x!["passed"]!).ToList();
            if (fail && failed.Count > 0)
                throw new ExpectationFailedException($"{failed.Count} of {checks.Count} expectations failed: " +
                    string.Join("; ", failed.Select(x => $"{(string)x!["check"]!} expected {x["expected"]?.ToJsonString()}, found {x["actual"]?.ToJsonString()}")) + ".");
            return new JsonObject { ["passed"] = failed.Count == 0, ["failed"] = failed.Count, ["checks"] = checks };
        }, cancel);
}

/// <summary>expect with fail true found a check that didn't pass.</summary>
public sealed class ExpectationFailedException(string message) : InvalidOperationException(message);
