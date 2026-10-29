using System.Globalization;
using System.Text.Json.Nodes;
using AVAModeler.Core;

namespace AVAModeler.Mcp;

/// <summary>What the tools report back: the document, its layers and surfaces, the selection, and what a step changed.</summary>
internal static class Census
{
    /// <summary>How many points and polygons each layer has.</summary>
    public static (int Points, int Polygons)[] Of(Document doc) => doc.Layers.Select(l => (l.Mesh.Points.Count, l.Mesh.Polygons.Count)).ToArray();

    /// <summary>
    /// What a step changed: the counts before and after (and per layer that changed), the bounds of the visible layers
    /// now, the selection, the history.
    /// </summary>
    public static JsonObject Changes(Document doc, (int Points, int Polygons)[] before, string done)
    {
        var after = Of(doc);
        var layers = new JsonArray();
        for (int i = 0; i < after.Length; i++)
        {
            var b = i < before.Length ? before[i] : (0, 0);
            if (b != after[i]) layers.Add(new JsonObject { ["layer"] = i + 1, ["points"] = Pair(b.Item1, after[i].Points), ["polygons"] = Pair(b.Item2, after[i].Polygons) });
        }
        return new JsonObject
        {
            ["done"] = done,
            ["points"] = Pair(before.Sum(x => x.Points), after.Sum(x => x.Points)),
            ["polygons"] = Pair(before.Sum(x => x.Polygons), after.Sum(x => x.Polygons)),
            ["changed_layers"] = layers,
            ["visible_bounds"] = doc.Bounds() is { IsEmpty: false } bounds ? BoxJson(bounds) : null,
            ["selection"] = Selection(doc),
            ["undo"] = UndoState(doc),
        };
    }

    private static JsonObject Pair(int before, int after) => new() { ["before"] = before, ["after"] = after };

    /// <summary>A run of indices: the first, the last and how many (they're always consecutive when added).</summary>
    public static JsonObject Range(IReadOnlyList<int> indices) => new()
    {
        ["first"] = indices.Count > 0 ? indices[0] : null,
        ["last"] = indices.Count > 0 ? indices[^1] : null,
        ["count"] = indices.Count,
    };

    /// <summary>The selection: its mode, how much is selected, and (for the current layer) which.</summary>
    public static JsonObject Selection(Document doc)
    {
        var sel = doc.Selection;
        int cur = doc.CurrentLayer;
        var fg = doc.ForegroundLayers.ToList();
        var o = new JsonObject { ["mode"] = sel.Mode.ToString().ToLowerInvariant() };
        switch (sel.Mode)
        {
            case SelectionMode.Points:
                o["selected"] = fg.Sum(l => sel.Points.TryGetValue(l, out var s) ? s.Count : 0);
                o["current_layer"] = Indices(sel.OrderedPoints(cur));
                break;
            case SelectionMode.Polygons:
                o["selected"] = fg.Sum(l => sel.Polygons.TryGetValue(l, out var s) ? s.Count : 0);
                o["current_layer"] = Indices(sel.PolygonsIn(cur).Order().ToList());
                break;
            case SelectionMode.Edges:
                o["selected"] = fg.Sum(l => sel.Edges.TryGetValue(l, out var s) ? s.Count : 0);
                o["current_layer"] = new JsonArray(sel.EdgesIn(cur).Take(100).Select(e => (JsonNode)new JsonArray(e.A, e.B)).ToArray());
                break;
            default:
                o["selected"] = sel.Volume.IsEmpty ? 0 : 1;
                o["volume"] = sel.Volume.IsEmpty ? null : BoxJson(sel.Volume);
                break;
        }
        if (!sel.Any) o["note"] = "Nothing is selected: tools act on everything in the foreground layers.";
        return o;
    }

    private static JsonArray Indices(List<int> list)
    {
        var a = new JsonArray(list.Take(200).Select(i => (JsonNode)i).ToArray());
        if (list.Count > 200) a.Add($"… and {list.Count - 200} more");
        return a;
    }

    public static JsonObject UndoState(Document doc) => new()
    {
        ["depth"] = doc.Undo.Count,
        ["next_undo"] = doc.Undo.UndoLabel,
        ["next_redo"] = doc.Undo.RedoLabel,
    };

    /// <summary>The whole document, or (brief) its layers, bounds, selection and history only.</summary>
    public static JsonObject Document(Document doc, bool brief)
    {
        var layers = new JsonArray();
        var quiet = new List<int>();
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer.State == LayerState.Off && layer.Mesh.IsEmpty) { quiet.Add(i + 1); continue; }
            var o = new JsonObject
            {
                ["layer"] = i + 1,
                ["state"] = layer.State.ToString().ToLowerInvariant(),
                ["points"] = layer.Mesh.Points.Count,
                ["polygons"] = layer.Mesh.Polygons.Count,
            };
            if (layer.Name.Length > 0) o["name"] = layer.Name;
            if (!layer.Mesh.IsEmpty) o["bounds"] = BoxJson(layer.Mesh.Bounds());
            layers.Add(o);
        }
        var result = new JsonObject
        {
            ["file"] = doc.FilePath,
            ["modified"] = doc.Modified,
            ["current_layer"] = doc.CurrentLayer + 1,
            ["layers"] = layers,
        };
        if (quiet.Count > 0) result["empty_layers_off"] = Ranges(quiet);
        var visible = doc.Bounds();
        result["visible_bounds"] = visible.IsEmpty ? null : BoxJson(visible);
        result["surfaces"] = brief
            ? new JsonArray(doc.Surfaces.Select(s => (JsonNode)s.Name).ToArray())
            : new JsonArray(doc.Surfaces.Select(s => (JsonNode)Surface(doc, s)).ToArray());
        result["uv_maps"] = new JsonArray(doc.UVMapNames().Select(n => (JsonNode)n).ToArray());
        result["current_uv_map"] = doc.ActiveUVMap;
        result["selection"] = Selection(doc);
        result["undo"] = UndoState(doc);
        return result;
    }

    /// <summary>A surface's settings, its textures, and how many polygons use it.</summary>
    public static JsonObject Surface(Document doc, Surface s)
    {
        int index = doc.Surfaces.IndexOf(s);
        int used = doc.Layers.Sum(l => l.Mesh.Polygons.Count(p => p.Surface == index));
        return new JsonObject
        {
            ["name"] = s.Name,
            ["color"] = new JsonArray(s.Color.R, s.Color.G, s.Color.B),
            ["diffuse"] = Round(s.Diffuse),
            ["specular"] = Round(s.Specular),
            ["glossiness"] = Round(s.Glossiness),
            ["luminosity"] = Round(s.Luminosity),
            ["reflection"] = Round(s.Reflection),
            ["transparency"] = Round(s.Transparency),
            ["smoothing"] = s.Smoothing,
            ["smoothing_angle"] = Round(s.SmoothingAngle),
            ["double_sided"] = s.DoubleSided,
            ["polygons"] = used,
            ["textures"] = new JsonArray(s.Textures.Select(t => (JsonNode)Texture(t)).ToArray()),
        };
    }

    public static JsonObject Texture(TextureLayer t)
    {
        var o = new JsonObject
        {
            ["channel"] = t.Channel.ToString().ToLowerInvariant(),
            ["image"] = t.Image,
            ["projection"] = t.Projection.ToString().ToLowerInvariant(),
            ["axis"] = t.Axis.ToString().ToLowerInvariant(),
            ["center"] = Vec(t.Center),
            ["size"] = Vec(t.Size),
            ["opacity"] = Round(t.Opacity),
        };
        if (t.Projection == TextureProjection.UV) o["uv_map"] = t.UVMap;
        if (t.Procedural != null) o["procedural"] = t.Procedural;
        return o;
    }

    /// <summary>A layer by its number from 1, as the app shows it (the current one if none is given); returns its index from 0.</summary>
    public static (int Layer, Mesh Mesh) Layer(Document doc, int? layer)
    {
        if (layer is int n && (n < 1 || n > doc.Layers.Count)) throw new ArgumentException($"There's no layer {n}: layers are numbered 1 to {doc.Layers.Count}.");
        int l = layer is int k ? k - 1 : doc.CurrentLayer;
        return (l, doc.Layers[l].Mesh);
    }

    /// <summary>The points or polygons of a layer the selection makes active; none when nothing is selected.</summary>
    public static IEnumerable<int> Active(Document doc, int layer, bool points)
    {
        if (!doc.Selection.Any || doc.Layers[layer].State != LayerState.Foreground) return [];
        var mesh = doc.Layers[layer].Mesh;
        return (points ? doc.Selection.ActivePoints(layer, mesh) : doc.Selection.ActivePolygons(layer, mesh)).Order();
    }

    public static void CheckPage(int offset, int limit)
    {
        if (offset < 0) throw new ArgumentException("offset can't be negative.");
        if (limit < 1 || limit > 5000) throw new ArgumentException("limit must be from 1 to 5000.");
    }

    public static void CheckIndices(IEnumerable<int> indices, int count, string what)
    {
        foreach (int i in indices)
            if (i < 0 || i >= count)
                throw new ArgumentException(count == 0
                    ? $"There's no {what} {i}: the current layer has no {what}s."
                    : $"There's no {what} {i}: the current layer's {what}s are numbered 0 to {count - 1}.");
    }

    public static double Round(double v, int digits = 6) => Math.Round(v, digits) + 0.0; // + 0.0 turns -0 into 0

    public static JsonArray Vec(Vec3 v) => new(Round(v.X), Round(v.Y), Round(v.Z));

    public static JsonObject BoxJson(Box3 b) => new() { ["min"] = Vec(b.Min), ["max"] = Vec(b.Max), ["size"] = Vec(b.Size) };

    public static string Box(Box3 b) => FormattableString.Invariant($"x {b.Min.X:0.###} to {b.Max.X:0.###}, y {b.Min.Y:0.###} to {b.Max.Y:0.###}, z {b.Min.Z:0.###} to {b.Max.Z:0.###} m");

    /// <summary>Layer numbers as runs ("2-9").</summary>
    private static string Ranges(List<int> list)
    {
        var parts = new List<string>();
        for (int i = 0; i < list.Count;)
        {
            int j = i;
            while (j + 1 < list.Count && list[j + 1] == list[j] + 1) j++;
            parts.Add(i == j ? list[i].ToString(CultureInfo.InvariantCulture) : $"{list[i]}-{list[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
