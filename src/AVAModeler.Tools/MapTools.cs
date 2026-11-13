using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Tools;

/// <summary>
/// A brush: dragged over the model, it lays dabs where the mouse's ray meets the foreground's surface, a share of its
/// radius apart, each working on the points within its radius by its falloff. The whole stroke is one undo step. Run
/// from a script it lays one dab at Center. With symmetry on, each dab has a mirrored twin.
/// </summary>
public abstract class BrushTool : Tool
{
    [Param("Radius", Distance = true, Min = 0, Order = 90, Description = "How far a dab reaches, in metres.")] public double Radius { get; set; } = 0.25;
    [Param("Strength", Min = 0, Max = 1, Step = 0.05, Order = 91, Description = "How much a dab does at its centre, 0 to 1.")] public double Strength { get; set; } = 0.5;
    [Param("Falloff", Order = 92, Description = "How a dab fades toward its radius: Smooth, Linear, Sharp, Dome or Constant.")] public BrushFalloff Falloff { get; set; } = BrushFalloff.Smooth;
    [Param("Center", Distance = true, Order = 93, Description = "Run from a script: where its one dab goes (on or near the surface).")] public Vec3 Center { get; set; }
    [Param("Use pressure", Order = 94, Description = "A pen's pressure scales each dab's strength.")] public bool UsePressure { get; set; } = true;

    /// <summary>The stroke so far: each dab's centre (on the original surface), layer and pressure. Null when run from a script.</summary>
    private List<(int Layer, Vec3 At, double Pressure)>? _stroke;
    private Vec3? _last;

    /// <summary>The dabs this run lays: the stroke's, or one at Center in each foreground layer.</summary>
    protected IReadOnlyList<(int Layer, Vec3 At, double Pressure)> Dabs =>
        _stroke ?? [.. Doc.ForegroundLayers.Select(l => (l, Center, 1.0))];

    /// <summary>A stroke given as surface points (agents' strokes): spaced into dabs as a mouse's would be.</summary>
    public void SetStroke(int layer, IReadOnlyList<Vec3> path, IReadOnlyList<double>? pressure = null)
    {
        _stroke = [.. Brushes.Space(path, Spacing).Select((p, i) => (layer, p, pressure is { Count: > 0 } pr ? pr[Math.Min(i * pr.Count / Math.Max(1, path.Count), pr.Count - 1)] : 1.0))];
    }

    /// <summary>How far apart a stroke's dabs are.</summary>
    protected virtual double Spacing => Math.Max(Radius * 0.25, 1e-6);

    protected override void OnBegin()
    {
        _stroke = null;
        _last = null;
        _cache = null;
    }

    /// <summary>Whether dabs land on the surface as it is now (sculpting) rather than as it was when the stroke began (painting).</summary>
    protected virtual bool HitsCurrentSurface => false;

    protected override void OnDrag(DragInfo d)
    {
        _stroke ??= [];
        if (d.Ray is not { } ray) return;
        var layers = Doc.ForegroundLayers.ToList();
        var hit = Brushes.Raycast([.. layers.Select(l => HitsCurrentSurface ? Doc.Layers[l].Mesh : Original(l))], ray);
        if (hit is not { } h) { _last = null; return; }
        int layer = layers[h.Mesh];
        var at = h.Hit.Point;
        double pressure = UsePressure ? Math.Clamp(d.Pressure, 0, 1) : 1;
        if (_last is not { } last) { _stroke.Add((layer, at, pressure)); _last = at; return; }
        foreach (var p in Brushes.Space([last, at], Spacing).Skip(1)) { _stroke.Add((layer, p, pressure)); _last = p; }
    }

    // The stroke as far as the last update went, so each update lays only the new dabs.
    private Dictionary<int, Mesh>? _cache;
    private int _cached;
    private string? _cacheKey;

    private string Key() => string.Join("|", Parameters.Select(p => $"{p.Name}={p.Value}")) + $"|{Doc.Symmetry}|{Doc.Selection.Any}";

    protected override void Apply()
    {
        var dabs = Dabs;
        int start = 0;
        string key = Key();
        if (_stroke != null && _cache != null && _cacheKey == key && _cached <= dabs.Count && _cache.Keys.All(l => l < Doc.Layers.Count))
        {
            foreach (var (l, m) in _cache) Doc.Layers[l].Mesh.TakeFrom(m.Clone());
            start = _cached;
        }
        for (int i = start; i < dabs.Count; i++)
        {
            var (layer, at, pressure) = dabs[i];
            if (layer >= Doc.Layers.Count) continue;
            var mesh = Doc.Layers[layer].Mesh;
            var allowed = Doc.Selection.Any ? Doc.Selection.ActivePoints(layer, mesh).ToHashSet() : null;
            DabAt(layer, mesh, allowed, at, pressure, mirrored: false);
            if (Doc.Symmetry is { } axis && !Symmetry.OnPlane(at, axis))
                DabAt(layer, mesh, allowed, Symmetry.Mirror(at, axis), pressure, mirrored: true);
        }
        foreach (int l in Doc.ForegroundLayers) Doc.Layers[l].Mesh.Touch();
        if (_stroke != null && Cacheable)
        {
            _cache = Doc.ForegroundLayers.ToDictionary(l => l, l => Doc.Layers[l].Mesh.Clone());
            _cached = dabs.Count;
            _cacheKey = key;
        }
    }

    /// <summary>Whether a stroke's result so far can be kept and built on (not for a brush whose dabs all change together, as Grab's do).</summary>
    protected virtual bool Cacheable => true;

    /// <summary>One dab: change the points under <paramref name="at"/>. <paramref name="mirrored"/> for symmetry's twin.</summary>
    protected abstract void DabAt(int layer, Mesh mesh, IReadOnlySet<int>? allowed, Vec3 at, double pressure, bool mirrored);
}

/// <summary>The map a map tool works on: the named one, else the document's current one, made if new.</summary>
internal static class MapChoice
{
    public static PointMap Weight(Document doc, Mesh mesh, string? name)
    {
        string n = string.IsNullOrWhiteSpace(name) ? doc.CurrentWeightMap ?? "Weight" : name.Trim();
        doc.CurrentWeightMap = n;
        return mesh.AddPointMap(n, PointMapKind.Weight);
    }

    public static PointMap Color(Document doc, Mesh mesh, string? name)
    {
        string n = string.IsNullOrWhiteSpace(name) ? doc.CurrentColorMap ?? "Color" : name.Trim();
        doc.CurrentColorMap = n;
        return mesh.FindPointMap(n, PointMapKind.Rgb) ?? mesh.AddPointMap(n, PointMapKind.Rgb);
    }

    /// <summary>The weight maps named in a comma-separated list, or every weight map for "".</summary>
    public static List<PointMap> Weights(Mesh mesh, string? names)
    {
        var all = mesh.PointMaps.Where(m => m.Kind == PointMapKind.Weight).ToList();
        if (string.IsNullOrWhiteSpace(names)) return all;
        var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var w in wanted)
            if (!all.Any(m => m.Name == w)) throw new ArgumentException($"There's no weight map \"{w}\".{(all.Count > 0 ? $" There are: {string.Join(", ", all.Select(m => m.Name))}." : "")}");
        return all.Where(m => wanted.Contains(m.Name)).ToList();
    }
}

/// <summary>Paint Weights: a brush that paints a weight map.</summary>
public sealed class PaintWeightsTool : BrushTool
{
    public override string Name => "Paint Weights";
    public override string Tab => "Maps";
    public override string Help => "Drag over the model to paint the weight map (shown in colour: blue 0, red 1).";
    public override string Summary => "A brush that paints a weight map (Map, made if new; the current one if empty): Add, Subtract, Set (toward Value) or Smooth (toward the neighbours' weights), by Strength at its centre fading over Radius. Run from a script it lays one dab at Center. With symmetry on the mirrored dab paints the map of the opposite name (\"Left Arm\" and \"Right Arm\") where there is one. Shows the map in the shaded views.";

    [Param("Map", Description = "The weight map to paint (made if new); empty for the current one.")] public string Map { get; set; } = "";
    [Param("Mode", Description = "Add, Subtract, Set (toward Value) or Smooth.")] public WeightPaintMode Mode { get; set; } = WeightPaintMode.Add;
    [Param("Value", Min = 0, Max = 1, Step = 0.05, Description = "With Set: the weight it paints toward.")] public double Value { get; set; } = 1;

    protected override void DabAt(int layer, Mesh mesh, IReadOnlySet<int>? allowed, Vec3 at, double pressure, bool mirrored)
    {
        var map = MapChoice.Weight(Doc, mesh, Map);
        if (mirrored && MapNames.Opposite(map.Name) is { } o && mesh.FindPointMap(o, PointMapKind.Weight) is { } twin) map = twin;
        Doc.ShownPointMap = Doc.CurrentWeightMap;
        WeightMaps.Dab(mesh, map, Brushes.Under(mesh, allowed, at, Radius, Falloff), Mode, Strength * pressure, Value);
    }
}

/// <summary>Set Weight: gives the selected points a weight in a map.</summary>
public sealed class SetWeightTool : Tool
{
    public override string Name => "Set Weight";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Gives the selected points (every point, with none selected) Value in the weight map Map (made if new; the current one if empty). A weight of 0 takes a point out of the map.";

    [Param("Map", Description = "The weight map (made if new); empty for the current one.")] public string Map { get; set; } = "";
    [Param("Value", Min = 0, Max = 1, Step = 0.05)] public double Value { get; set; } = 1;

    protected override void Apply()
    {
        foreach (var (_, mesh, points) in ActivePoints())
        {
            var map = MapChoice.Weight(Doc, mesh, Map);
            foreach (int p in points) map.SetWeight(p, Value);
            mesh.Touch();
        }
        Doc.ShownPointMap = Doc.CurrentWeightMap;
    }
}

/// <summary>Smooth Weights: evens out a weight map over the selection.</summary>
public sealed class SmoothWeightsTool : Tool
{
    public override string Name => "Smooth Weights";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Eases each selected point's weight in Map (the current weight map if empty) toward its neighbours' average, Iterations times at Strength, softening hard edges between bones' areas.";

    [Param("Map", Description = "The weight map; empty for the current one.")] public string Map { get; set; } = "";
    [Param("Strength", Min = 0, Max = 1, Step = 0.05)] public double Strength { get; set; } = 0.5;
    [Param("Iterations", Min = 1, Max = 100, Step = 1)] public int Iterations { get; set; } = 3;

    protected override void Apply()
    {
        foreach (var (_, mesh, points) in ActivePoints())
        {
            WeightMaps.Smooth(mesh, MapChoice.Weight(Doc, mesh, Map), points, Strength, Iterations);
            mesh.Touch();
        }
    }
}

/// <summary>Normalize Weights: makes the weight maps add to 1 at every selected point.</summary>
public sealed class NormalizeWeightsTool : Tool
{
    public override string Name => "Normalize Weights";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Immediate;
    public override string Summary => "Scales the weights at each selected point (every point, with none selected) so the weight maps named in Maps (comma-separated; every weight map if empty) add to 1 there, as bones expect. Points in none of them are left alone.";

    [Param("Maps", Description = "The weight maps, comma-separated; empty for all of them.")] public string Maps { get; set; } = "";

    protected override void Apply()
    {
        foreach (var (_, mesh, points) in ActivePoints())
        {
            WeightMaps.Normalize(MapChoice.Weights(mesh, Maps), points);
            mesh.Touch();
        }
    }
}

/// <summary>Mirror Weights: copies one side's weights onto the other.</summary>
public sealed class MirrorWeightsTool : Tool
{
    public override string Name => "Mirror Weights";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Copies the weights of the selected points (every point, with none selected) on the positive side of Axis (or the negative, with From positive off) to their mirror partners: into the same map, or into the map of the opposite name where there is one (\"Left Arm\" into \"Right Arm\", \"Arm.L\" into \"Arm.R\"). Maps names the weight maps (comma-separated; all if empty).";

    [Param("Maps", Description = "The weight maps, comma-separated; empty for all of them.")] public string Maps { get; set; } = "";
    [Param("Axis")] public Axis Axis { get; set; } = Axis.X;
    [Param("From positive", Description = "Copy from the positive side to the negative (off: the other way).")] public bool FromPositive { get; set; } = true;

    protected override void Apply()
    {
        foreach (var (_, mesh, points) in ActivePoints())
        {
            WeightMaps.Mirror(mesh, MapChoice.Weights(mesh, Maps), Axis, FromPositive, points);
            mesh.Touch();
        }
    }
}

/// <summary>Weights from Skelegons: an automatic rig's weight maps.</summary>
public sealed class WeightsFromSkelegonsTool : Tool
{
    public override string Name => "Weights from Skelegons";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Makes a weight map for every skelegon in the foreground (2-point polygons on a surface named Skelegon…), named as the bone Layout makes from it, so each bone follows its own map. Each point (every point, or the selected ones) is shared among the skelegons by 1 / distance^Falloff, normalised to add to 1, with shares under Threshold dropped. Paint fixes afterwards with Paint Weights.";

    [Param("Falloff", Min = 1, Max = 16, Step = 0.5, Description = "How fast a skelegon's pull fades with distance: higher keeps each bone's area tighter.")] public double FalloffPower { get; set; } = 4;
    [Param("Threshold", Min = 0, Max = 0.5, Step = 0.01, Description = "Shares below this are dropped and the rest made to add to 1 again.")] public double Threshold { get; set; } = 0.01;

    protected override void Apply()
    {
        int made = 0;
        foreach (var (_, mesh, points) in ActivePoints())
        {
            if (SkelegonNames.Of(mesh, Doc.Surfaces).Count == 0) continue;
            var names = WeightMaps.FromSkelegons(mesh, Doc.Surfaces, FalloffPower, Threshold, Doc.Selection.Any ? points : null);
            made += names.Count;
            if (names.Count > 0) { Doc.CurrentWeightMap = names[0]; Doc.ShownPointMap = names[0]; }
        }
        if (made == 0) throw new InvalidOperationException("Weights from Skelegons needs skelegons in a foreground layer: 2-point polygons on a surface named Skelegon… (Points, then Make Polygon with two points, on a Skelegon surface).");
    }
}

/// <summary>
/// A sculpt brush (the Sculpt tab): drag over the model and the surface under the brush moves, dab by dab, on the
/// mesh's points (with MetaNURBS on, the cage's). One undo step a stroke. From a script, one dab at Center.
/// </summary>
public abstract class SculptTool : BrushTool
{
    public override string Tab => "Sculpt";
    public abstract SculptBrush Brush { get; }
    protected override bool HitsCurrentSurface => true;

    [Param("Invert", Description = "Dig in instead of building up (Draw, Inflate, Crease), or push apart instead of pinching.")] public bool Invert { get; set; }

    protected override void DabAt(int layer, Mesh mesh, IReadOnlySet<int>? allowed, Vec3 at, double pressure, bool mirrored) =>
        Sculpting.Dab(mesh, Brushes.Under(mesh, allowed, at, Radius, Falloff), Brush, at, Radius, Strength * pressure, Invert);
}

public sealed class DrawBrushTool : SculptTool
{
    public override string Name => "Draw Brush";
    public override SculptBrush Brush => SculptBrush.Draw;
    public override string Help => "Drag over the surface to build it up (Invert digs in).";
    public override string Summary => "A sculpt brush that raises the surface under it along its average normal (Invert lowers it): at Strength 1, a tenth of Radius at the centre each dab, fading by Falloff. Run from a script it lays one dab at Center; sculpt (MCP) lays a stroke.";
}

public sealed class InflateBrushTool : SculptTool
{
    public override string Name => "Inflate Brush";
    public override SculptBrush Brush => SculptBrush.Inflate;
    public override string Help => "Drag to swell the surface out along each point's normal.";
    public override string Summary => "A sculpt brush that pushes each point under it out along its own normal (Invert shrinks), swelling a shape evenly round.";
}

public sealed class SmoothBrushTool : SculptTool
{
    public override string Name => "Smooth Brush";
    public override SculptBrush Brush => SculptBrush.Smooth;
    public override string Help => "Drag to smooth the surface under the brush.";
    public override string Summary => "A sculpt brush that eases each point under it toward its neighbours' average, by Strength at the centre.";
}

public sealed class PinchBrushTool : SculptTool
{
    public override string Name => "Pinch Brush";
    public override SculptBrush Brush => SculptBrush.Pinch;
    public override string Help => "Drag along an edge to pinch the surface toward the brush (sharpens creases and ridges).";
    public override string Summary => "A sculpt brush that draws points toward its centre across the surface (Invert spreads them), tightening edges and ridges.";
}

public sealed class FlattenBrushTool : SculptTool
{
    public override string Name => "Flatten Brush";
    public override SculptBrush Brush => SculptBrush.Flatten;
    public override string Help => "Drag to flatten the surface onto its plane under the brush.";
    public override string Summary => "A sculpt brush that moves the points under it onto the plane of the surface there (their weighted average place and normal), by Strength.";
}

public sealed class CreaseBrushTool : SculptTool
{
    public override string Name => "Crease Brush";
    public override SculptBrush Brush => SculptBrush.Crease;
    public override string Help => "Drag to cut a sharp groove (Invert raises a ridge).";
    public override string Summary => "A sculpt brush that pinches points toward its centre and pushes them in: a sharp groove along the stroke (Invert: a ridge).";
}

/// <summary>Grab: moves the points under the brush with the drag, as one piece.</summary>
public sealed class GrabBrushTool : SculptTool
{
    public override string Name => "Grab Brush";
    public override SculptBrush Brush => SculptBrush.Grab;
    public override string Help => "Press on the surface and drag: what's under the brush comes along, fading toward its edge.";
    public override string Summary => "A sculpt brush that moves the points within Radius of Center by Offset, fully at the centre and fading by Falloff: pulling out a nose, an ear or a horn. From the mouse, Center is where the press lands and Offset the drag.";

    [Param("Offset", Distance = true, Description = "How far, and which way, the points under the brush move.")] public Vec3 Offset { get; set; }

    private bool _grabbed;
    protected override bool Cacheable => false;

    protected override void OnBegin()
    {
        base.OnBegin();
        _grabbed = false;
    }

    protected override void OnDrag(DragInfo d)
    {
        if (d.First || !_grabbed)
        {
            if (d.Ray is not { } ray) return;
            var layers = Doc.ForegroundLayers.ToList();
            if (Brushes.Raycast([.. layers.Select(Original)], ray) is not { } hit) return;
            Center = hit.Hit.Point;
            _grabbed = true;
        }
        Offset = d.Delta;
    }

    protected override void Apply()
    {
        foreach (int layer in Doc.ForegroundLayers)
        {
            var mesh = Doc.Layers[layer].Mesh;
            var allowed = Doc.Selection.Any ? Doc.Selection.ActivePoints(layer, mesh).ToHashSet() : null;
            var under = Brushes.Under(mesh, allowed, Center, Radius, Falloff);
            var mirrored = Doc.Symmetry is { } axis && !Symmetry.OnPlane(Center, axis)
                ? Brushes.Under(mesh, allowed, Symmetry.Mirror(Center, axis), Radius, Falloff) : null;
            Sculpting.Dab(mesh, under, SculptBrush.Grab, Center, Radius, Strength, Invert, Offset);
            if (mirrored != null) Sculpting.Dab(mesh, mirrored, SculptBrush.Grab, Center, Radius, Strength, Invert, Symmetry.Mirror(Offset, Doc.Symmetry!.Value));
            mesh.Touch();
        }
    }

    protected override void DabAt(int layer, Mesh mesh, IReadOnlySet<int>? allowed, Vec3 at, double pressure, bool mirrored) { }
}

/// <summary>Paint Colors: a brush that paints a colour map.</summary>
public sealed class PaintColorsTool : BrushTool
{
    public override string Name => "Paint Colors";
    public override string Tab => "Maps";
    public override string Help => "Drag over the model to paint the colour map.";
    public override string Summary => "A brush that paints Color ([r, g, b], 0 to 255) into a colour map (Map, made if new; the current one if empty), mixing toward it by Strength at the centre, fading over Radius. A surface shows the map in renders when its vertex colour map names it (set_surface vertex_color_map); the shaded views show it with Show Map. Run from a script it lays one dab at Center.";

    [Param("Map", Description = "The colour map to paint (made if new); empty for the current one.")] public string Map { get; set; } = "";
    [Param("Color", Step = 1, Min = 0, Max = 255, Description = "The colour, [r, g, b] from 0 to 255.")] public Vec3 Color { get; set; } = new(255, 64, 32);

    protected override void DabAt(int layer, Mesh mesh, IReadOnlySet<int>? allowed, Vec3 at, double pressure, bool mirrored)
    {
        var map = MapChoice.Color(Doc, mesh, Map);
        Doc.ShownPointMap = map.Name;
        var target = Color / 255;
        foreach (var (p, w) in Brushes.Under(mesh, allowed, at, Radius, Falloff))
        {
            var was = map.Color(p) ?? Vec3.One;
            map.SetColor(p, Vec3.Lerp(was, target, Math.Clamp(Strength * pressure * w, 0, 1)));
        }
    }
}

/// <summary>Set Color: fills the selection's points with a colour.</summary>
public sealed class SetColorTool : Tool
{
    public override string Name => "Set Color";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Gives the selected points (every point, with none selected) Color ([r, g, b], 0 to 255) in the colour map Map (made if new; the current one if empty).";

    [Param("Map", Description = "The colour map (made if new); empty for the current one.")] public string Map { get; set; } = "";
    [Param("Color", Step = 1, Min = 0, Max = 255, Description = "The colour, [r, g, b] from 0 to 255.")] public Vec3 Color { get; set; } = new(255, 255, 255);

    protected override void Apply()
    {
        foreach (var (_, mesh, points) in ActivePoints())
        {
            var map = MapChoice.Color(Doc, mesh, Map);
            foreach (int p in points) map.SetColor(p, Color / 255);
            mesh.Touch();
        }
        Doc.ShownPointMap = Doc.CurrentColorMap;
    }
}

/// <summary>Bake Maps: normal, ambient occlusion, curvature or colour into a picture through a UV map.</summary>
public sealed class BakeMapsTool : Tool
{
    public override string Name => "Bake Maps";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Bakes into a PNG (File) through a UV map (the current one if empty), for the selected faces (all, with none selected) in the foreground: Normal (tangent-space normals of the detailed mesh in the background layers, found along the low mesh's normals within Distance; with no background, the foreground's own smoothing for a flat surface), AmbientOcclusion (Samples rays a pixel, Distance far), Curvature (edges light, creases dark) or Color (the surface's colour, procedurals and UV images). Padding bleeds the edges. With Hook up, the picture is laid on the faces' surface: a normal map on its Normal channel, occlusion and curvature multiplied into Color, colour as its colour image.";

    [Param("Kind", Description = "Normal, AmbientOcclusion, Curvature or Color.")] public BakeKind Bake { get; set; } = BakeKind.Normal;
    [Param("File", Description = "The PNG to write (relative to the object's folder, or the working folder for agents); empty: the object's name and the kind.")] public string File { get; set; } = "";
    [Param("Size", Min = 16, Max = 8192, Step = 256, Description = "Width and height in pixels.")] public int Size { get; set; } = 1024;
    [Param("Padding", Min = 0, Max = 64, Step = 1, Description = "Pixels the baked areas bleed outward.")] public int Padding { get; set; } = 4;
    [Param("Samples", Min = 1, Max = 1024, Step = 8, Description = "Ambient occlusion: rays a pixel.")] public int Samples { get; set; } = 32;
    [Param("Distance", Distance = true, Min = 0, Description = "Normal: how far either side the detailed surface is looked for; occlusion: how far things hide the sky. 0: a twentieth of the object's size.")] public double Distance { get; set; }
    [Param("UV map", Description = "The UV map to bake through (empty: the current one).")] public string UVMap { get; set; } = "";
    [Param("Hook up", Description = "Lay the picture on the faces' surface (Normal channel, or multiplied into Color, or as the colour image).")] public bool HookUp { get; set; } = true;

    /// <summary>Where the last run wrote its picture, and how many pixels it covered.</summary>
    public string? Written { get; private set; }
    public int Covered { get; private set; }

    protected override void Apply()
    {
        string map = string.IsNullOrWhiteSpace(UVMap) ? Doc.ActiveUVMap ?? "" : UVMap.Trim();
        if (map.Length == 0) throw new InvalidOperationException("Bake Maps needs a UV map: make one with Make UVs (atlas gives every face its own place).");
        var detail = Bake == BakeKind.Normal || Bake == BakeKind.AmbientOcclusion ? Doc.BackgroundMesh() : null;
        if (detail is { Polygons.Count: 0 }) detail = null;
        var settings = new BakeSettings { Kind = Bake, Size = Size, Padding = Padding, Samples = Samples, Distance = Distance, UVMap = map };
        string? objectFolder = Doc.FilePath is { } fp ? Path.GetDirectoryName(Path.GetFullPath(fp)) : null;
        string name = string.IsNullOrWhiteSpace(File)
            ? $"{(Doc.FilePath is { } f ? Path.GetFileNameWithoutExtension(f) : "object")}_{Bake.ToString().ToLowerInvariant()}.png"
            : File.Trim();
        if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name += ".png";
        string path = Path.IsPathRooted(name) ? name : Path.Combine(Context.BaseDirectory ?? objectFolder ?? Environment.CurrentDirectory, name);
        BakedMap? baked = null;
        var surfaces = new HashSet<int>();
        var whole = new Mesh();
        var faces = new List<int>();
        foreach (var (_, mesh, polys) in ActivePolygons())
        {
            int offset = whole.Polygons.Count;
            whole.Append(mesh);
            faces.AddRange(polys.Select(p => p + offset));
            foreach (int p in polys) surfaces.Add(mesh.Polygons[p].Surface);
        }
        baked = Baking.Bake(whole, faces, Doc.Surfaces, settings, detail, Doc.ImageBase);
        var png = Png.Encode(baked.Width, baked.Height, baked.Rgba);
        if (Context.FileSaver is { } save) save(path, png);
        else Commands.WriteWhole(path, png);
        Written = path;
        Covered = baked.Covered;
        TextureImages.Put(path, new TextureImage(baked.Width, baked.Height, baked.Rgba));
        if (!HookUp) return;
        string image = TextureImages.RelativePath(path, Doc.FilePath);
        foreach (int si in surfaces)
        {
            if (si < 0 || si >= Doc.Surfaces.Count) continue;
            var s = Doc.Surfaces[si];
            var channel = Bake == BakeKind.Normal ? TextureChannel.Normal : TextureChannel.Color;
            s.Textures.RemoveAll(t => t.IsImage && t.Image == image);
            var layer = new TextureLayer
            {
                Channel = channel, Image = image, Projection = TextureProjection.UV, UVMap = map,
                Blend = Bake is BakeKind.AmbientOcclusion or BakeKind.Curvature ? TextureBlend.Multiply : TextureBlend.Normal,
            };
            s.Textures.Add(layer);
            s.Raw = null;
        }
    }
}
