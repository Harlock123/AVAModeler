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

    /// <summary>Runs a tool with parameters by name (unnamed ones keep their defaults).</summary>
    public Tool Run(string toolName, IReadOnlyDictionary<string, object?>? args = null)
    {
        var tool = Registry.Create(toolName);
        if (args != null)
            foreach (var (k, v) in args)
            {
                var p = tool.Parameter(k) ?? throw new ArgumentException($"{tool.Name} has no parameter \"{k}\". It has: {string.Join(", ", tool.Parameters.Select(x => x.Name))}.");
                p.Value = v;
            }
        tool.Run(Context);
        return tool;
    }

    public int AddPoint(Vec3 p)
    {
        int i = -1;
        Document.Edit("Add point", () => i = Document.Current.Mesh.AddPoint(p), [Document.CurrentLayer]);
        return i;
    }

    public int AddPolygon(IEnumerable<int> points, string? surface = null)
    {
        var pts = points.ToList();
        var mesh = Document.Current.Mesh;
        if (pts.Count == 0 || pts.Any(i => i < 0 || i >= mesh.Points.Count)) throw new ArgumentException("A polygon needs points that exist.");
        int index = -1;
        int s = surface == null ? 0 : Document.SurfaceIndex(surface);
        Document.Edit("Add polygon", () => index = mesh.AddPolygon(pts, s), [Document.CurrentLayer]);
        return index;
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

    public void SelectPoints(IEnumerable<int> points)
    {
        Document.Selection.Mode = SelectionMode.Points;
        foreach (int p in points) Document.Selection.SelectPoint(Document.CurrentLayer, p);
        Document.Notify();
    }

    public void SelectPolygons(IEnumerable<int> polygons)
    {
        Document.Selection.Mode = SelectionMode.Polygons;
        Document.Selection.PolygonsIn(Document.CurrentLayer).UnionWith(polygons);
        Document.Notify();
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

    public void New()
    {
        Document = new Document();
        Context = new ToolContext(Document);
        DocumentReplaced?.Invoke(Document);
    }

    public void Load(string path)
    {
        Document = (Loader ?? throw new InvalidOperationException("No file loader is set up."))(path);
        Context = new ToolContext(Document);
        DocumentReplaced?.Invoke(Document);
    }

    public void Save(string path) => (Saver ?? throw new InvalidOperationException("No file saver is set up."))(Document, path);

    public void Print(string line) => Output.Add(line);
}
