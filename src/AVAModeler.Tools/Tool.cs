using System.Reflection;
using AVAModeler.Core;

namespace AVAModeler.Tools;

/// <summary>
/// Marks a tool property as a parameter: it shows in the numeric panel, scripts can set it by name, and dragging in a
/// view changes it. Numbers can have limits; a distance shows in metres.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ParamAttribute(string label) : Attribute
{
    public string Label { get; } = label;
    public double Min { get; init; } = double.NegativeInfinity;
    public double Max { get; init; } = double.PositiveInfinity;
    public double Step { get; init; } = 0.1;
    /// <summary>A length, shown in metres and snapped to the grid when snapping is on.</summary>
    public bool Distance { get; init; }
    /// <summary>An angle in degrees.</summary>
    public bool Angle { get; init; }
    public int Order { get; init; }
}

/// <summary>One of a tool's parameters, read through reflection from its <see cref="ParamAttribute"/>.</summary>
public sealed class ToolParameter(Tool tool, PropertyInfo property, ParamAttribute attribute)
{
    public string Name => property.Name;
    public string Label => attribute.Label;
    public ParamAttribute Info => attribute;
    public Type Type => property.PropertyType;

    public object? Value
    {
        get => property.GetValue(tool);
        set => property.SetValue(tool, Convert(value, property.PropertyType));
    }

    private static object? Convert(object? value, Type type)
    {
        if (value == null) return null;
        if (type.IsInstanceOfType(value)) return value;
        if (type.IsEnum) return value is string s ? Enum.Parse(type, s, ignoreCase: true) : Enum.ToObject(type, System.Convert.ToInt32(value));
        if (type == typeof(Vec3) && value is IEnumerable<double> list)
        {
            var a = list.ToArray();
            return new Vec3(a.ElementAtOrDefault(0), a.ElementAtOrDefault(1), a.ElementAtOrDefault(2));
        }
        return System.Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Where Rotate, Size and their kind turn and scale about.</summary>
public enum ActionCenter { Mouse, Selection, Origin }

/// <summary>A drag in a view, in the terms a tool needs.</summary>
public sealed class DragInfo
{
    /// <summary>Where the drag began and where the mouse is now, on the plane through the action centre facing the view.</summary>
    public Vec3 Start, Current;
    /// <summary>The drag in screen pixels from where it began.</summary>
    public double DxPixels, DyPixels;
    /// <summary>The view's right, up and into-the-screen directions.</summary>
    public Vec3 Right, Up, Forward;
    /// <summary>How many world units a pixel is at the action centre.</summary>
    public double UnitsPerPixel = 0.01;
    /// <summary>The right mouse button (classic tools use it for a second axis or a constrained variant).</summary>
    public bool Alternate;
    /// <summary>Constrain to the main direction of the drag (Shift).</summary>
    public bool Constrain;
    /// <summary>The first drag of a new click (a tool like Points adds something on each one).</summary>
    public bool First;
    public Vec3 Delta => Current - Start;
    /// <summary>The axis the view looks down (X, Y or Z), for tools that work about the view's own axis.</summary>
    public Axis ViewAxis => Math.Abs(Forward.X) > 0.5 ? Axis.X : Math.Abs(Forward.Y) > 0.5 ? Axis.Y : Axis.Z;
}

/// <summary>What a tool works with: the document, the action centre and snapping.</summary>
public sealed class ToolContext(Document document)
{
    public Document Document { get; } = document;
    public ActionCenter CenterMode = ActionCenter.Selection;
    /// <summary>The mouse's position at the start of a drag, for <see cref="ActionCenter.Mouse"/>.</summary>
    public Vec3 MousePoint;
    /// <summary>Snap new positions to this grid step (0: no snapping).</summary>
    public double Snap;

    /// <summary>The point tools act about: the mouse, the centre of the active points' bounds, or the origin.</summary>
    public Vec3 Center()
    {
        switch (CenterMode)
        {
            case ActionCenter.Origin: return Vec3.Zero;
            case ActionCenter.Mouse: return MousePoint;
        }
        var box = Box3.Empty;
        foreach (int l in Document.ForegroundLayers)
        {
            var mesh = Document.Layers[l].Mesh;
            foreach (int i in Document.Selection.ActivePoints(l, mesh)) box = box.Include(mesh.Points[i]);
        }
        return box.Center;
    }

    public double SnapValue(double v) => Snap > 0 ? Math.Round(v / Snap) * Snap : v;
    public Vec3 SnapPoint(Vec3 p) => Snap > 0 ? new Vec3(SnapValue(p.X), SnapValue(p.Y), SnapValue(p.Z)) : p;
}

/// <summary>How a tool runs from a button or a key.</summary>
public enum ToolKind
{
    /// <summary>Armed, then dragged in a view (with the numeric panel live).</summary>
    Interactive,
    /// <summary>Runs at once on the selection.</summary>
    Immediate,
    /// <summary>Asks for its parameters first (a numeric requester), then runs.</summary>
    Requester,
}

/// <summary>
/// A Modeler tool. Its parameters are typed properties marked <see cref="ParamAttribute"/>; the numeric panel, the
/// command API and dragging all set them. The lifecycle is <see cref="Begin"/>, any number of <see cref="Update"/>s
/// (each recomputes the result from the untouched originals, so the preview is always exact), then
/// <see cref="Commit"/> (one undo step) or <see cref="Cancel"/>.
/// </summary>
public abstract class Tool
{
    public abstract string Name { get; }
    public abstract string Tab { get; }
    public virtual ToolKind Kind => ToolKind.Interactive;
    public virtual string Help => "";

    protected ToolContext Context { get; private set; } = null!;
    protected Document Doc => Context.Document;
    private UndoStack.Snapshot? _before;
    private Dictionary<int, Mesh> _originals = [];
    private Selection? _originalSelection;

    public bool Active => _before != null;

    private List<ToolParameter>? _parameters;

    public IReadOnlyList<ToolParameter> Parameters => _parameters ??= GetType()
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => (p, a: p.GetCustomAttribute<ParamAttribute>()))
        .Where(x => x.a != null)
        .OrderBy(x => x.a!.Order)
        .ThenBy(x => x.p.MetadataToken)
        .Select(x => new ToolParameter(this, x.p, x.a!))
        .ToList();

    public ToolParameter? Parameter(string name) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Label, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The layers the tool changes: by default, the foreground.</summary>
    protected virtual IEnumerable<int> Layers => Doc.ForegroundLayers;

    /// <summary>Starts: remembers the layers as they are, for the previews and for undo.</summary>
    public void Begin(ToolContext context)
    {
        if (Active) Cancel();
        Context = context;
        var layers = Layers.ToList();
        _before = context.Document.Snapshot(Name, layers);
        _originals = layers.ToDictionary(i => i, i => context.Document.Layers[i].Mesh.Clone());
        _originalSelection = context.Document.Selection.Clone();
        OnBegin();
    }

    /// <summary>Called once at the start, with the originals in place (e.g. to measure the selection).</summary>
    protected virtual void OnBegin() { }

    /// <summary>A layer's mesh as it was when the tool began.</summary>
    protected Mesh Original(int layer) => _originals.TryGetValue(layer, out var m) ? m : Doc.Layers[layer].Mesh;

    /// <summary>The selection as it was when the tool began.</summary>
    protected Selection OriginalSelection => _originalSelection ?? Doc.Selection;

    /// <summary>Recomputes the result from the originals with the current parameters.</summary>
    public void Update()
    {
        if (!Active) return;
        // Put the originals back into the same mesh objects, so anything holding a layer's mesh still sees it.
        foreach (var (i, m) in _originals)
        {
            var copy = m.Clone();
            var mesh = Doc.Layers[i].Mesh;
            mesh.Points = copy.Points;
            mesh.Polygons = copy.Polygons;
            mesh.Touch();
        }
        Doc.Selection = _originalSelection!.Clone();
        Apply();
        Doc.Notify();
    }

    /// <summary>Changes the parameters from a drag, then updates.</summary>
    public void Drag(DragInfo drag)
    {
        if (!Active) return;
        OnDrag(drag);
        Update();
    }

    /// <summary>Sets parameters from a drag (interactive tools override this).</summary>
    protected virtual void OnDrag(DragInfo drag) { }

    /// <summary>Does the work on the current (restored) layers with the current parameters.</summary>
    protected abstract void Apply();

    public void Commit()
    {
        if (_before == null) return;
        var before = _before;
        _before = null;
        _originals = [];
        Doc.Commit(before);
    }

    public void Cancel()
    {
        if (_before == null) return;
        var before = _before;
        _before = null;
        _originals = [];
        Context.Document.Revert(before);
    }

    /// <summary>Runs the tool in one go with its current parameters: begin, apply, commit.</summary>
    public void Run(ToolContext context)
    {
        Begin(context);
        Update();
        Commit();
    }

    /// <summary>Puts the parameters back to their defaults.</summary>
    public virtual void Reset()
    {
        var fresh = (Tool)Activator.CreateInstance(GetType())!;
        foreach (var p in Parameters) p.Value = fresh.Parameter(p.Name)!.Value;
    }

    // ───────────────────────────── helpers for tools ─────────────────────────────

    /// <summary>The foreground layers with the points the selection makes active in each.</summary>
    protected IEnumerable<(int Layer, Mesh Mesh, IReadOnlyCollection<int> Points)> ActivePoints()
    {
        foreach (int l in Doc.ForegroundLayers)
        {
            var mesh = Doc.Layers[l].Mesh;
            yield return (l, mesh, Doc.Selection.ActivePoints(l, mesh));
        }
    }

    /// <summary>The foreground layers with the polygons the selection makes active in each.</summary>
    protected IEnumerable<(int Layer, Mesh Mesh, IReadOnlyCollection<int> Polygons)> ActivePolygons()
    {
        foreach (int l in Doc.ForegroundLayers)
        {
            var mesh = Doc.Layers[l].Mesh;
            yield return (l, mesh, Doc.Selection.ActivePolygons(l, mesh));
        }
    }

    /// <summary>Moves every active point through a function of its position.</summary>
    protected void MapPoints(Func<Vec3, Vec3> f)
    {
        foreach (var (_, mesh, points) in ActivePoints())
        {
            foreach (int i in points) mesh.Points[i] = f(mesh.Points[i]);
            mesh.Touch();
        }
    }

    /// <summary>The bounds of the active points across the foreground.</summary>
    protected Box3 ActiveBounds()
    {
        var box = Box3.Empty;
        foreach (var (_, mesh, points) in ActivePoints())
            foreach (int i in points) box = box.Include(mesh.Points[i]);
        return box;
    }

    /// <summary>The surface new geometry gets: the named one (created if new), or the first.</summary>
    protected int SurfaceFor(string? name) => string.IsNullOrWhiteSpace(name) ? 0 : Doc.SurfaceIndex(name);

    public override string ToString() => Name;
}
