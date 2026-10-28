using AVAModeler.Core;

namespace AVAModeler.Tools;

/// <summary>The ways Make UVs lays out a selection.</summary>
public enum UVProjection
{
    /// <summary>Flat along an axis, as a planar image map lies.</summary>
    Planar,
    /// <summary>Round an axis, as a cylindrical image map wraps.</summary>
    Cylindrical,
    /// <summary>Round an axis and pole to pole, as a spherical image map wraps.</summary>
    Spherical,
    /// <summary>Each face flat along the axis it faces most, all sharing the one square.</summary>
    Cubic,
    /// <summary>Like Cubic, but the six sides each get a cell of their own in a 3 × 2 layout, kept in proportion.</summary>
    Atlas,
}

/// <summary>
/// Make UVs: gives the selected polygons (or all) coordinates in a UV map, made if new, by a projection. With "Fit"
/// on, the projection is sized to the selection (as a texture's Automatic Sizing does), filling the 0–1 square;
/// otherwise its centre and size are used as given, so the UVs match a planar, cylindrical or spherical image map with
/// the same settings. Faces whose corners land on both sides of a wrapping projection's seam keep the ones on the far
/// side as their own (a seam), as do neighbours outside the selection whose UVs this changes.
/// </summary>
public sealed class MakeUVsTool : Tool
{
    public override string Name => "Make UVs";
    public override string Tab => "Polygon";
    public override string Summary => "Gives the selected polygons (or all) coordinates in a UV map (made if new) by a projection along Axis: planar, cylindrical, spherical, cubic or atlas. With Fit on, the layout fills the 0-1 square; otherwise Center and Size place the projection as an image map with the same settings would. Makes that map the current one.";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Help => "Lays the selected polygons (or all) out in a UV map by a projection. The UV view shows the result.";

    [Param("UV map", Description = "The map's name (made if new).")] public string MapName { get; set; } = "UV";
    [Param("Projection")] public UVProjection Projection { get; set; } = UVProjection.Planar;
    [Param("Axis", Description = "The axis it projects along (planar), or wraps round (cylindrical, spherical).")] public Axis Axis { get; set; } = Axis.Z;
    /// <summary>Size the projection to the selection (filling the 0–1 square) rather than by Center and Size.</summary>
    [Param("Fit to the selection")] public bool Fit { get; set; } = true;
    [Param("Center", Distance = true, Description = "Used when Fit is off.")] public Vec3 Center { get; set; }
    [Param("Size", Distance = true, Description = "Used when Fit is off.")] public Vec3 Size { get; set; } = Vec3.One;
    /// <summary>How many times a cylindrical or spherical layout goes round.</summary>
    [Param("Width wrap", Min = 0.01, Step = 0.5, Description = "How many times a cylindrical or spherical layout goes round.")] public double WidthWrap { get; set; } = 1;

    protected override void Apply()
    {
        if (string.IsNullOrWhiteSpace(MapName)) throw new ArgumentException("Make UVs needs a UV map name.");
        foreach (var (_, mesh, polys) in ActivePolygons())
        {
            var chosen = polys.Where(i => mesh.Polygons[i].Count > 0).ToList();
            if (chosen.Count == 0) continue;
            var uvs = Layout(mesh, chosen);
            UVEdits.Replace(mesh, MapName, chosen, uvs);
        }
        Doc.CurrentUVMap = MapName;
    }

    /// <summary>Each chosen polygon's new corner UVs, in its point order.</summary>
    private List<Vec2[]> Layout(Mesh mesh, List<int> polygons)
    {
        var bounds = mesh.Bounds(mesh.PointsOf(polygons));
        var (center, size) = Fit ? TextureMapping.AutoSize(bounds) : (Center, Size);
        var proj = Projection switch
        {
            UVProjection.Cylindrical => TextureProjection.Cylindrical,
            UVProjection.Spherical => TextureProjection.Spherical,
            UVProjection.Cubic or UVProjection.Atlas => TextureProjection.Cubic,
            _ => TextureProjection.Planar,
        };
        var result = new List<Vec2[]>(polygons.Count);
        foreach (int pi in polygons)
        {
            var poly = mesh.Polygons[pi];
            var uv = new Vec2[poly.Count];
            TextureMapping.ProjectFace(proj, Axis, center, size, WidthWrap, 1, poly.Points.Select(p => mesh.Points[p]).ToArray(), mesh.Normal(poly), uv);
            result.Add(uv);
        }
        if (Projection == UVProjection.Atlas) Atlas(mesh, polygons, result, size);
        else if (Fit && Projection is UVProjection.Cylindrical or UVProjection.Spherical) FitHeight(result);
        return result;
    }

    /// <summary>A wrapping layout fitted to the selection: its height (v) stretched to fill 0–1.</summary>
    private static void FitHeight(List<Vec2[]> uvs)
    {
        double lo = uvs.SelectMany(u => u).Select(u => u.Y).DefaultIfEmpty(0).Min(), hi = uvs.SelectMany(u => u).Select(u => u.Y).DefaultIfEmpty(1).Max();
        if (hi - lo < 1e-12) return;
        foreach (var face in uvs)
            for (int i = 0; i < face.Length; i++) face[i] = face[i] with { Y = (face[i].Y - lo) / (hi - lo) };
    }

    /// <summary>
    /// Moves each face of a cubic layout into the cell for the way it faces (+X, −X, +Y along the bottom row; −Y, +Z,
    /// −Z along the top), all at one scale so the pieces keep their proportions. Faces seen from the back are mirrored
    /// so no piece comes out back to front.
    /// </summary>
    private static void Atlas(Mesh mesh, List<int> polygons, List<Vec2[]> uvs, Vec3 size)
    {
        const double pad = 0.02;
        var cellOf = new int[polygons.Count];
        for (int k = 0; k < polygons.Count; k++)
        {
            var n = mesh.Normal(mesh.Polygons[polygons[k]]);
            var axis = TextureMapping.Dominant(n);
            bool negative = n[axis] < 0;
            cellOf[k] = (int)axis * 2 + (negative ? 1 : 0);
            // The planar views see faces facing −Z (Front), +Y (Top) and +X (Side); the others would come out back to front.
            bool flip = axis == Axis.Z ? !negative : negative;
            if (flip)
                for (int i = 0; i < uvs[k].Length; i++) uvs[k][i] = uvs[k][i] with { X = 1 - uvs[k][i].X };
        }
        var extent = new double[6];
        var min = Enumerable.Repeat(new Vec2(double.MaxValue, double.MaxValue), 6).ToArray();
        var world = new List<Vec2[]>(uvs.Count);
        for (int k = 0; k < uvs.Count; k++)
        {
            var axis = (Axis)(cellOf[k] / 2);
            var (right, up) = TextureMapping.ViewAxes(axis);
            // Back into lengths, so every side shares one scale.
            var w = uvs[k].Select(u => new Vec2(u.X * size[right], u.Y * size[up])).ToArray();
            world.Add(w);
            foreach (var p in w) min[cellOf[k]] = new Vec2(Math.Min(min[cellOf[k]].X, p.X), Math.Min(min[cellOf[k]].Y, p.Y));
        }
        for (int k = 0; k < world.Count; k++)
            foreach (var p in world[k])
                extent[cellOf[k]] = Math.Max(extent[cellOf[k]], Math.Max(p.X - min[cellOf[k]].X, p.Y - min[cellOf[k]].Y));
        double span = Math.Max(extent.Max(), 1e-12);
        double cell = 1.0 / 3 - 2 * pad;
        for (int k = 0; k < world.Count; k++)
        {
            int c = cellOf[k];
            var origin = new Vec2(c % 3 / 3.0 + pad, c / 3 * 0.5 + pad);
            for (int i = 0; i < world[k].Length; i++)
                uvs[k][i] = origin + (world[k][i] - min[c]) * (cell / span);
        }
    }
}

/// <summary>Changes to a mesh's UVs shared by the tools and commands that make them.</summary>
public static class UVEdits
{
    /// <summary>
    /// Replaces some polygons' UVs in a map (made if new) with new ones (each polygon's in its point order). Neighbours
    /// outside the set that shared points with it keep the UVs they had, as their own corners' where they now differ.
    /// </summary>
    public static void Replace(Mesh mesh, string map, IReadOnlyList<int> polygons, IReadOnlyList<Vec2[]> uvs)
    {
        mesh.AddUVMap(map);
        var chosen = polygons.ToHashSet();
        var points = mesh.PointsOf(polygons);
        // What the neighbours had, before the shared points change.
        var kept = new List<(Polygon Poly, int Point, Vec2 UV)>();
        foreach (int p in points)
            foreach (int q in mesh.PolygonsOf(p))
                if (!chosen.Contains(q) && mesh.TryGetUV(map, mesh.Polygons[q], p, out var uv)) kept.Add((mesh.Polygons[q], p, uv));
        var m = mesh.FindUVMap(map)!;
        foreach (int p in points) m.Points.Remove(p);
        foreach (int pi in polygons) mesh.Polygons[pi].CornerUVs?.Remove(map);
        for (int k = 0; k < polygons.Count; k++)
        {
            var poly = mesh.Polygons[polygons[k]];
            for (int i = 0; i < poly.Count && i < uvs[k].Length; i++) mesh.SetCornerUV(map, poly, poly.Points[i], uvs[k][i]);
        }
        foreach (var (poly, p, uv) in kept) mesh.SetCornerUV(map, poly, p, uv);
        foreach (var poly in mesh.Polygons) Tidy(poly);
        mesh.Touch();
    }

    private static void Tidy(Polygon poly)
    {
        if (poly.CornerUVs == null) return;
        foreach (var name in poly.CornerUVs.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) poly.CornerUVs.Remove(name);
        if (poly.CornerUVs.Count == 0) poly.CornerUVs = null;
    }
}

/// <summary>
/// UV space as a document of its own, so the ordinary tools can work on UVs: each foreground layer's UV map laid out
/// flat (u along X, v along Y, at z = 0), as the UV view shows it. Each mapped point becomes a flat point; a corner
/// with UVs of its own (a seam) gets a flat point of its own, shared by the corners with the same point and UVs. Faces
/// with an unmapped corner are left out. Selections carry across both ways (a point's seam copies go with it), and
/// what a tool does to the flat points is written back as UVs, so long as it only moved them.
/// </summary>
public sealed class UVSpace
{
    private sealed class LayerMap
    {
        /// <summary>Each flat point's model point.</summary>
        public readonly List<int> Owner = [];
        /// <summary>Each model point's main flat point (its own UVs), if it has one.</summary>
        public readonly Dictionary<int, int> Main = [];
        /// <summary>The flat points standing for model points' seam corners.</summary>
        public readonly Dictionary<int, List<int>> Extras = [];
        /// <summary>The model corners (polygon, point) each seam flat point stands for.</summary>
        public readonly Dictionary<int, List<(Polygon Poly, int Point)>> Corners = [];
        /// <summary>Each flat polygon's model polygon index.</summary>
        public readonly List<int> Polygon = [];
        public readonly Dictionary<int, int> FlatPolygon = [];
        /// <summary>The flat polygons' points as built, to tell whether a tool did more than move points.</summary>
        public readonly List<int[]> Shape = [];
        public int PointCount;
    }

    private readonly Dictionary<int, LayerMap> _layers = [];

    /// <summary>The model document.</summary>
    public Document Model { get; }
    /// <summary>The UV map laid out.</summary>
    public string Map { get; }
    /// <summary>The flat document: the same layer numbers, the model's foreground layers' UVs in its foreground.</summary>
    public Document Flat { get; }

    public UVSpace(Document model, string map)
    {
        Model = model;
        Map = map;
        Flat = new Document { Surfaces = model.Surfaces };
        while (Flat.Layers.Count < model.Layers.Count) Flat.Layers.Add(new Layer());
        for (int l = 0; l < model.Layers.Count; l++)
        {
            Flat.Layers[l].State = model.Layers[l].State == LayerState.Foreground ? LayerState.Foreground : LayerState.Off;
            if (model.Layers[l].State == LayerState.Foreground) _layers[l] = Build(model.Layers[l].Mesh, Flat.Layers[l].Mesh);
        }
        Flat.Selection.Mode = model.Selection.Mode == SelectionMode.Volume ? SelectionMode.Points : model.Selection.Mode;
        SelectionToFlat();
    }

    private LayerMap Build(Mesh model, Mesh flat)
    {
        var lm = new LayerMap();
        int Add(int owner, Vec2 uv)
        {
            lm.Owner.Add(owner);
            flat.Points.Add(new Vec3(uv.X, uv.Y, 0));
            return flat.Points.Count - 1;
        }
        var uvs = model.FindUVMap(Map);
        if (uvs != null)
            foreach (var (i, uv) in uvs.Points.OrderBy(kv => kv.Key))
                if (i >= 0 && i < model.Points.Count) lm.Main[i] = Add(i, uv);
        var seams = new Dictionary<(int, Vec2), int>();
        for (int pi = 0; pi < model.Polygons.Count; pi++)
        {
            var poly = model.Polygons[pi];
            if (!model.IsMapped(Map, poly)) continue;
            var own = poly.CornerUVs?.GetValueOrDefault(Map);
            var pts = new int[poly.Count];
            for (int c = 0; c < poly.Count; c++)
            {
                int p = poly.Points[c];
                if (own != null && own.TryGetValue(p, out var uv))
                {
                    if (!seams.TryGetValue((p, uv), out int k))
                    {
                        seams[(p, uv)] = k = Add(p, uv);
                        if (!lm.Extras.TryGetValue(p, out var list)) lm.Extras[p] = list = [];
                        list.Add(k);
                        lm.Corners[k] = [];
                    }
                    if (!lm.Corners[k].Contains((poly, p))) lm.Corners[k].Add((poly, p));
                    pts[c] = k;
                }
                else pts[c] = lm.Main[p];
            }
            lm.FlatPolygon[pi] = flat.Polygons.Count;
            lm.Polygon.Add(pi);
            lm.Shape.Add(pts);
            flat.Polygons.Add(new Polygon(pts, poly.Surface));
        }
        lm.PointCount = flat.Points.Count;
        flat.Touch();
        return lm;
    }

    /// <summary>A flat point's model point (in a foreground layer), or -1.</summary>
    public int ModelPoint(int layer, int flatPoint) =>
        _layers.TryGetValue(layer, out var lm) && flatPoint >= 0 && flatPoint < lm.Owner.Count ? lm.Owner[flatPoint] : -1;

    /// <summary>A flat polygon's model polygon, or -1.</summary>
    public int ModelPolygon(int layer, int flatPolygon) =>
        _layers.TryGetValue(layer, out var lm) && flatPolygon >= 0 && flatPolygon < lm.Polygon.Count ? lm.Polygon[flatPolygon] : -1;

    /// <summary>The model's selection, as it falls in the flat document.</summary>
    public void SelectionToFlat()
    {
        var from = Model.Selection;
        var to = Flat.Selection;
        to.Clear(allModes: true);
        foreach (var (l, lm) in _layers)
        {
            foreach (int p in from.OrderedPoints(l))
            {
                if (lm.Main.TryGetValue(p, out int k)) to.SelectPoint(l, k);
                foreach (int e in lm.Extras.GetValueOrDefault(p) ?? []) to.SelectPoint(l, e);
            }
            foreach (int pi in from.PolygonsIn(l))
                if (lm.FlatPolygon.TryGetValue(pi, out int f)) to.PolygonsIn(l).Add(f);
            var flatMesh = Flat.Layers[l].Mesh;
            var edges = flatMesh.EdgeMap();
            foreach (var e in from.EdgesIn(l))
                foreach (var fe in edges.Keys.Where(fe => lm.Owner[fe.A] == e.A && lm.Owner[fe.B] == e.B || lm.Owner[fe.A] == e.B && lm.Owner[fe.B] == e.A))
                    to.EdgesIn(l).Add(fe);
        }
    }

    /// <summary>The flat document's selection put back on the model (replacing its selection in the flat layers).</summary>
    public void SelectionToModel()
    {
        var from = Flat.Selection;
        var to = Model.Selection;
        if (Model.Selection.Mode != SelectionMode.Volume) to.Mode = from.Mode;
        foreach (var (l, lm) in _layers)
        {
            to.PointsIn(l).Clear();
            to.PointOrder.Remove(l);
            to.PolygonsIn(l).Clear();
            to.EdgesIn(l).Clear();
            foreach (int k in from.OrderedPoints(l))
                if (k >= 0 && k < lm.Owner.Count) to.SelectPoint(l, lm.Owner[k]);
            foreach (int f in from.PolygonsIn(l))
                if (f >= 0 && f < lm.Polygon.Count) to.PolygonsIn(l).Add(lm.Polygon[f]);
            foreach (var e in from.EdgesIn(l))
                if (e.A < lm.Owner.Count && e.B < lm.Owner.Count && lm.Owner[e.A] != lm.Owner[e.B]) to.EdgesIn(l).Add(Edge.Of(lm.Owner[e.A], lm.Owner[e.B]));
        }
    }

    /// <summary>Whether the flat layers still have the shape they were built with (a tool only moved points).</summary>
    public bool SameShape()
    {
        foreach (var (l, lm) in _layers)
        {
            var mesh = Flat.Layers[l].Mesh;
            if (mesh.Points.Count != lm.PointCount || mesh.Polygons.Count != lm.Shape.Count) return false;
            for (int i = 0; i < lm.Shape.Count; i++)
                if (!mesh.Polygons[i].Points.SequenceEqual(lm.Shape[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Writes the flat points' positions back to the model as UVs (only while the flat layers keep their shape); returns
    /// whether it did.
    /// </summary>
    public bool WriteBack()
    {
        if (!SameShape()) return false;
        foreach (var (l, lm) in _layers)
        {
            var flat = Flat.Layers[l].Mesh;
            var model = Model.Layers[l].Mesh;
            var map = model.AddUVMap(Map);
            for (int k = 0; k < flat.Points.Count; k++)
            {
                var uv = new Vec2(flat.Points[k].X, flat.Points[k].Y);
                if (lm.Corners.TryGetValue(k, out var corners))
                    foreach (var (poly, p) in corners)
                    {
                        poly.CornerUVs ??= [];
                        if (!poly.CornerUVs.TryGetValue(Map, out var own)) poly.CornerUVs[Map] = own = [];
                        own[p] = uv;
                    }
                else map.Points[lm.Owner[k]] = uv;
            }
            model.Touch();
        }
        return true;
    }
}
