using System.Reflection;
using System.Runtime.Loader;
using AVAModeler.Core;
using Formats = AVAModeler.Formats;

namespace AVAModeler.Tools;

/// <summary>
/// A loader and/or saver for an object file format, from a plugin (or built in).
/// </summary>
public interface IObjectFormat
{
    string Name { get; }
    /// <summary>The file extensions it handles, with the dot (".dxf").</summary>
    IReadOnlyList<string> Extensions { get; }
    bool CanLoad { get; }
    bool CanSave { get; }
    Document Load(string path);
    void Save(Document document, string path);
}

/// <summary>
/// A plugin: an assembly in the plugins folder with a public class implementing this. Its tools are found by
/// themselves (every public, non-abstract <see cref="Tool"/> in the assembly); it can also add file formats.
/// </summary>
public interface IModelerPlugin
{
    string Name { get; }
    IEnumerable<IObjectFormat> Formats => [];
}

/// <summary>Every tool the Modeler knows, built in and from plugins, by name.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, Type> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IObjectFormat> _formats = [];
    private readonly List<string> _plugins = [];

    public static ToolRegistry Default { get; } = Create();

    public static ToolRegistry Create()
    {
        var r = new ToolRegistry();
        r.AddAssembly(typeof(Tool).Assembly);
        return r;
    }

    public IReadOnlyCollection<string> Names => _tools.Keys;
    public IReadOnlyList<IObjectFormat> Formats => _formats;
    public IReadOnlyList<string> Plugins => _plugins;

    /// <summary>Adds every public, non-abstract tool class in an assembly (keyed by its Name).</summary>
    public int AddAssembly(Assembly assembly)
    {
        int n = 0;
        foreach (var t in assembly.GetExportedTypes().Where(t => typeof(Tool).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null))
        {
            var tool = (Tool)Activator.CreateInstance(t)!;
            _tools[tool.Name] = t;
            n++;
        }
        return n;
    }

    public void AddFormat(IObjectFormat format) => _formats.Add(format);

    public Tool Create(string name) =>
        _tools.TryGetValue(name, out var t) ? (Tool)Activator.CreateInstance(t)! : throw new ArgumentException($"There's no tool called \"{name}\".", nameof(name));

    public bool Has(string name) => _tools.ContainsKey(name);

    /// <summary>Every tool, made once, grouped as the tabs show them.</summary>
    public IEnumerable<IGrouping<string, Tool>> ByTab() =>
        _tools.Values.Select(t => (Tool)Activator.CreateInstance(t)!).OrderBy(t => t.Name).GroupBy(t => t.Tab);

    /// <summary>
    /// Loads a plugin assembly (in its own load context, sharing these libraries) and adds its tools and formats.
    /// Returns the plugin's name, or the file's if it has no <see cref="IModelerPlugin"/>.
    /// </summary>
    public string LoadPlugin(string path)
    {
        var context = new PluginContext(Path.GetFullPath(path));
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
        int tools = AddAssembly(assembly);
        string name = Path.GetFileNameWithoutExtension(path);
        foreach (var t in assembly.GetExportedTypes().Where(t => typeof(IModelerPlugin).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var plugin = (IModelerPlugin)Activator.CreateInstance(t)!;
            name = plugin.Name;
            foreach (var f in plugin.Formats) AddFormat(f);
        }
        _plugins.Add($"{name} ({tools} tools)");
        return name;
    }

    /// <summary>Loads every .dll in a folder as a plugin; failures are reported, not thrown.</summary>
    public List<string> LoadPlugins(string folder)
    {
        var problems = new List<string>();
        if (!Directory.Exists(folder)) return problems;
        foreach (var dll in Directory.GetFiles(folder, "*.dll"))
        {
            try { LoadPlugin(dll); }
            catch (Exception e) { problems.Add($"{Path.GetFileName(dll)}: {e.Message}"); }
        }
        return problems;
    }

    /// <summary>A load context that resolves a plugin's own dependencies from its folder and shares ours.</summary>
    private sealed class PluginContext(string path) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            // Our own libraries (Core, Tools, …) are shared so the plugin's tools are our tools.
            var shared = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            if (shared != null) return shared;
            string? file = _resolver.ResolveAssemblyToPath(name);
            return file != null ? LoadFromAssemblyPath(file) : null;
        }
    }
}

/// <summary>
/// The command API: everything a script or plugin needs to drive the Modeler, the counterpart of classic Modeler's
/// ARexx commands. Tools run by name with parameters by name; geometry can be read and built directly; selection,
/// layers, undo and files are all here.
/// </summary>
public sealed class Commands(Document document, ToolRegistry? registry = null)
{
    static Commands() => SkiaImages.Register();

    public Document Document { get; private set; } = document;
    public ToolRegistry Registry { get; } = registry ?? ToolRegistry.Default;
    public ToolContext Context { get; private set; } = new(document);

    /// <summary>How files are opened and saved: the built-in formats unless something else is set.</summary>
    public Func<string, Document>? Loader { get; set; } = Formats.ObjectFiles.Load;
    public Action<Document, string>? Saver { get; set; } = Formats.ObjectFiles.Save;

    /// <summary>Raised when New or Load replaces the document (the app shows the new one).</summary>
    public event Action<Document>? DocumentReplaced;

    /// <summary>Lines a script printed.</summary>
    public List<string> Output { get; } = [];

    /// <summary>The folder a running macro's file is in: relative image paths it gives are looked for from there first.</summary>
    public string? BaseDirectory { get; set; }

    /// <summary>
    /// Runs a tool with parameters by name (unnamed ones keep their defaults), as one undo step. Each value is checked
    /// (<see cref="ToolParameter.Assign"/>): a bad name or value throws an <see cref="ArgumentException"/> before
    /// anything changes. If the tool itself fails, its work is undone before the exception goes on.
    /// </summary>
    public Tool Run(string toolName, IReadOnlyDictionary<string, object?>? args = null)
    {
        var tool = Registry.Create(toolName);
        if (args != null)
            foreach (var (k, v) in args)
            {
                var p = tool.Parameter(k) ?? throw new ArgumentException($"{tool.Name} has no parameter \"{k}\". It has: {string.Join(", ", tool.Parameters.Select(x => x.Name))}.");
                p.Assign(v);
            }
        try
        {
            tool.Run(Context);
        }
        catch
        {
            tool.Cancel();
            throw;
        }
        return tool;
    }

    /// <summary>
    /// Runs several commands as one undo step (see <see cref="Core.Document.Group{T}"/>): if one fails, everything they
    /// did is put back.
    /// </summary>
    public T Group<T>(string label, Func<T> work) => Document.Group(label, work);

    public int AddPoint(Vec3 p)
    {
        int i = -1;
        Document.Edit("Add point", () => i = Document.Current.Mesh.AddPoint(p), [Document.CurrentLayer]);
        return i;
    }

    /// <summary>Adds points to the current layer in one undo step; returns their indices.</summary>
    public IReadOnlyList<int> AddPoints(IEnumerable<Vec3> points)
    {
        var list = points.ToList();
        if (list.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z))) throw new ArgumentException("Points need finite coordinates.");
        var added = new List<int>();
        if (list.Count == 0) return added;
        Document.Edit("Add points", () => { foreach (var p in list) added.Add(Document.Current.Mesh.AddPoint(p)); }, [Document.CurrentLayer]);
        return added;
    }

    public int AddPolygon(IEnumerable<int> points, string? surface = null) => AddPolygons([points.ToList()], surface)[0];

    /// <summary>
    /// Adds polygons through existing points of the current layer, in one undo step, all with one surface (made if
    /// new); returns their indices. Every polygon is checked before any is added.
    /// </summary>
    public IReadOnlyList<int> AddPolygons(IEnumerable<IReadOnlyList<int>> polygons, string? surface = null)
    {
        var list = polygons.Select(p => p.ToList()).ToList();
        var mesh = Document.Current.Mesh;
        for (int k = 0; k < list.Count; k++)
        {
            var pts = list[k];
            if (pts.Count == 0) throw new ArgumentException($"Polygon {k} has no points: a polygon needs one or more.");
            foreach (int i in pts)
                if (i < 0 || i >= mesh.Points.Count)
                    throw new ArgumentException(mesh.Points.Count == 0
                        ? $"Polygon {k} uses point {i}, but the current layer has no points."
                        : $"Polygon {k} uses point {i}, but the current layer's points are numbered 0 to {mesh.Points.Count - 1}.");
            if (pts.Distinct().Count() != pts.Count) throw new ArgumentException($"Polygon {k} uses the same point twice.");
        }
        var added = new List<int>();
        if (list.Count == 0) return added;
        Document.Edit(list.Count == 1 ? "Add polygon" : "Add polygons", () =>
        {
            int s = surface == null ? 0 : Document.SurfaceIndex(surface);
            foreach (var pts in list) added.Add(mesh.AddPolygon(pts, s));
        }, [Document.CurrentLayer]);
        return added;
    }

    public int PointCount => Document.Current.Mesh.Points.Count;
    public int PolygonCount => Document.Current.Mesh.Polygons.Count;
    public Vec3 Point(int i) => Document.Current.Mesh.Points[i];
    public IReadOnlyList<int> PolygonPoints(int i) => Document.Current.Mesh.Polygons[i].Points;

    public void SelectMode(SelectionMode mode)
    {
        Document.Selection.Mode = mode;
        Document.Notify();
    }

    /// <summary>Adds points of the current layer to the selection (in Points mode); an index that doesn't exist throws, selecting nothing.</summary>
    public void SelectPoints(IEnumerable<int> points)
    {
        var list = points.ToList();
        Check(list, Document.Current.Mesh.Points.Count, "point");
        Document.Selection.Mode = SelectionMode.Points;
        foreach (int p in list) Document.Selection.SelectPoint(Document.CurrentLayer, p);
        Document.Notify();
    }

    /// <summary>Adds polygons of the current layer to the selection (in Polygons mode); an index that doesn't exist throws, selecting nothing.</summary>
    public void SelectPolygons(IEnumerable<int> polygons)
    {
        var list = polygons.ToList();
        Check(list, Document.Current.Mesh.Polygons.Count, "polygon");
        Document.Selection.Mode = SelectionMode.Polygons;
        Document.Selection.PolygonsIn(Document.CurrentLayer).UnionWith(list);
        Document.Notify();
    }

    private static void Check(List<int> indices, int count, string what)
    {
        foreach (int i in indices)
            if (i < 0 || i >= count)
                throw new ArgumentException(count == 0
                    ? $"There's no {what} {i}: the current layer has no {what}s."
                    : $"There's no {what} {i}: the current layer's {what}s are numbered 0 to {count - 1}.");
    }

    /// <summary>Selects, in the current mode, the points inside a box, or the polygons wholly inside it.</summary>
    public void SelectInside(Box3 box)
    {
        var sel = Document.Selection;
        foreach (int l in Document.ForegroundLayers)
        {
            var mesh = Document.Layers[l].Mesh;
            if (sel.Mode == SelectionMode.Polygons)
                sel.PolygonsIn(l).UnionWith(Enumerable.Range(0, mesh.Polygons.Count).Where(i => mesh.Polygons[i].Points.All(p => box.Contains(mesh.Points[p], 1e-9))));
            else if (sel.Mode == SelectionMode.Volume)
                sel.Volume = box;
            else
                for (int i = 0; i < mesh.Points.Count; i++)
                    if (box.Contains(mesh.Points[i], 1e-9)) sel.SelectPoint(l, i);
        }
        Document.Notify();
    }

    public void SelectAll() => SelectionOps.All(Document);
    public void SelectNone() => SelectionOps.None(Document);
    public void Invert() => SelectionOps.Invert(Document);

    public void SetLayers(IEnumerable<int> foreground, IEnumerable<int>? background = null) => Document.SetLayers(foreground, background);

    public bool Undo() => Document.UndoLast();
    public bool Redo() => Document.RedoLast();

    /// <summary>
    /// Asked before New or Load replaces the document: a reason not to (unsaved work an agent mustn't throw away), or
    /// null to go ahead. A reason makes New and Load throw an <see cref="InvalidOperationException"/> with it.
    /// </summary>
    public Func<string?>? ReplaceGuard { get; set; }

    private void CheckReplace()
    {
        if (ReplaceGuard?.Invoke() is { } reason) throw new InvalidOperationException(reason);
    }

    public void New()
    {
        CheckReplace();
        Document = new Document();
        Context = new ToolContext(Document);
        DocumentReplaced?.Invoke(Document);
    }

    public void Load(string path)
    {
        CheckReplace();
        Document = (Loader ?? throw new InvalidOperationException("No file loader is set up."))(path);
        Context = new ToolContext(Document);
        DocumentReplaced?.Invoke(Document);
    }

    public void Save(string path) => (Saver ?? throw new InvalidOperationException("No file saver is set up."))(Document, path);

    // ───────────────────────────── UV maps and textures ─────────────────────────────

    /// <summary>Makes UVs for the selection (or everything) in a map, by a projection along an axis, fitted to it.</summary>
    public void MakeUVs(string map, UVProjection projection = UVProjection.Planar, Axis axis = Axis.Z) =>
        Run("Make UVs", new Dictionary<string, object?> { ["MapName"] = map, ["Projection"] = projection, ["Axis"] = axis });

    /// <summary>Chooses the UV map to work on (the UV view shows it); not an undoable edit.</summary>
    public void SetUVMap(string map)
    {
        Document.CurrentUVMap = map;
        Document.Notify();
    }

    public IReadOnlyList<string> UVMapNames => Document.UVMapNames();

    /// <summary>A point's UVs in the current layer's chosen UV map, or null.</summary>
    public Vec2? GetUV(int point)
    {
        var map = Document.CurrentUVMap ?? Document.ActiveUVMap;
        return map != null && Document.Current.Mesh.FindUVMap(map) is { } m && m.Points.TryGetValue(point, out var uv) ? uv : null;
    }

    /// <summary>Sets a point's UVs in the chosen UV map (made if new; "UV" if none is chosen and there are none), in the current layer.</summary>
    public void SetUV(int point, Vec2 uv)
    {
        var mesh = Document.Current.Mesh;
        if (point < 0 || point >= mesh.Points.Count) throw new ArgumentException($"There's no point {point + 1}.");
        string map = Document.CurrentUVMap ?? Document.ActiveUVMap ?? "UV";
        Document.Edit("Set UV", () => mesh.SetPointUV(map, point, uv), [Document.CurrentLayer]);
        Document.CurrentUVMap ??= map;
    }

    /// <summary>
    /// Sets a surface's image texture for a channel: the channel's first texture layer, or a new one. The surface is
    /// made if it's new. A relative image path is looked for from <see cref="BaseDirectory"/> first; a rooted one inside
    /// the object's folder is stored relative to it. Returns the layer
    /// (still part of the document; change it within <paramref name="setup"/> so the change is one undo step).
    /// </summary>
    public TextureLayer SetTexture(string surface, string image, TextureChannel channel = TextureChannel.Color, Action<TextureLayer>? setup = null)
    {
        TextureLayer? layer = null;
        Document.Edit("Texture", () =>
        {
            var s = Document.Surfaces[Document.SurfaceIndex(surface)];
            layer = s.TexturesOf(channel).FirstOrDefault(t => t.IsImage);
            if (layer == null)
            {
                layer = new TextureLayer { Channel = channel, UVMap = Document.ActiveUVMap ?? "UV" };
                s.Textures.Add(layer);
            }
            string path = image;
            if (!Path.IsPathRooted(path) && BaseDirectory != null && Path.GetFullPath(Path.Combine(BaseDirectory, path)) is var full && File.Exists(full)) path = full;
            layer.Image = TextureImages.RelativePath(path, Document.FilePath);
            setup?.Invoke(layer);
        }, []);
        return layer!;
    }

    /// <summary>Removes a surface's texture layers for a channel (or all of them); returns how many went.</summary>
    public int ClearTextures(string surface, TextureChannel? channel = null)
    {
        int i = Document.Surfaces.FindIndex(s => s.Name == surface);
        if (i < 0) throw new ArgumentException($"There's no surface called \"{surface}\".");
        int gone = 0;
        Document.Edit("Remove texture", () => gone = Document.Surfaces[i].Textures.RemoveAll(t => channel == null || t.Channel == channel), []);
        return gone;
    }

    public void Print(string line) => Output.Add(line);
}
