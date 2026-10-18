using System.Text.Json;
using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Tools;

namespace AVAModeler.Modeler.UI;

/// <summary>The settings that last between runs, kept as JSON in the user's settings folder.</summary>
public sealed class Preferences
{
    /// <summary>"Software" or "OpenGL".</summary>
    public string Renderer { get; set; } = "Software";
    public int UndoLevels { get; set; } = 100;
    /// <summary>Minutes between auto-saves of a modified object (0: never).</summary>
    public int AutosaveMinutes { get; set; } = 3;
    public bool SnapToGrid { get; set; }
    public bool BackfaceTint { get; set; } = true;
    public DisplayStyle PerspectiveStyle { get; set; } = DisplayStyle.ShadedWireframe;
    public ActionCenter ActionCenter { get; set; } = ActionCenter.Selection;
    public int SubdivisionLevel { get; set; } = 2;
    public List<string> RecentFiles { get; set; } = [];
    /// <summary>Key overrides: command name → key gesture text (e.g. "Move": "T").</summary>
    public Dictionary<string, string> Keys { get; set; } = [];

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVAModeler");
    public static string DefaultPath => Path.Combine(Folder, "preferences.json");
    public static string PluginsFolder => Path.Combine(Folder, "plugins");
    public static string AutosaveFolder => Path.Combine(Folder, "autosave");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Preferences Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (File.Exists(path)) return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path), Json) ?? new Preferences();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new Preferences();
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
    }
}

/// <summary>
/// The keyboard: a classic-style default layout of single keys (t Move, y Rotate, h Size…), every one remappable
/// through <see cref="Preferences.Keys"/>. Keys are written as the key's name, with "Shift+", "Ctrl+" or "Alt+" in
/// front as needed ("Shift+X", "Ctrl+Z", "Tab").
/// </summary>
public sealed class Keymap
{
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        // Tools.
        ["Move"] = "T", ["Rotate"] = "Y", ["Size"] = "H", ["Stretch"] = "Shift+H", ["Shear"] = "Shift+V", ["Twist"] = "Alt+T",
        ["Taper"] = "Shift+Y", ["Bend"] = "Shift+B", ["Magnet"] = "Shift+M", ["Drag"] = "Ctrl+T", ["Box"] = "Shift+X",
        ["Ball"] = "Shift+O", ["Disc"] = "Shift+C", ["Cone"] = "Shift+N", ["Points"] = "Shift+P", ["Text"] = "Shift+W",
        ["Make Polygon"] = "P", ["Flip"] = "F", ["Merge Points"] = "M", ["Remove"] = "Delete", ["Surface"] = "Q",
        ["Triple"] = "Shift+T", ["Subdivide"] = "Shift+D", ["Extrude"] = "E", ["Lathe"] = "L", ["Mirror"] = "V",
        ["Bevel"] = "B", ["Smooth Shift"] = "Shift+F", ["Metaform"] = "Shift+Tab", ["Merge Polygons"] = "Shift+Z", ["Split"] = "Ctrl+L",
        ["Align"] = "Ctrl+F", ["Unify"] = "Ctrl+U", ["Clone"] = "Ctrl+C", ["Array"] = "Ctrl+Y", ["Path Extrude"] = "Ctrl+P", ["Rail Extrude"] = "Ctrl+E", ["Boolean"] = "Shift+R", ["Drill"] = "Ctrl+R",
        // Commands.
        ["Undo"] = "Ctrl+Z", ["Redo"] = "Ctrl+Shift+Z", ["Cut"] = "X", ["Copy"] = "C", ["Paste"] = "Ctrl+V",
        ["Cycle Selection Mode"] = "Space", ["Commit"] = "Enter", ["Cancel"] = "Escape", ["Deselect"] = "OemQuestion",
        ["Invert Selection"] = "OemQuotes", ["Select Connected"] = "OemCloseBrackets", ["Fit All"] = "A", ["Fit Selected"] = "Shift+A",
        ["Maximize View"] = "D0", ["MetaNURBS"] = "Tab", ["Swap Layers"] = "OemPipe", ["Zoom In"] = "OemPeriod", ["Zoom Out"] = "OemComma",
        ["New"] = "Ctrl+N", ["Open"] = "Ctrl+O", ["Save"] = "Ctrl+S", ["Quit"] = "Ctrl+Q",
    };

    private readonly Dictionary<string, string> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public Keymap(IReadOnlyDictionary<string, string>? overrides = null)
    {
        var merged = new Dictionary<string, string>(Defaults);
        if (overrides != null)
            foreach (var (command, key) in overrides) merged[command] = key;
        // Later entries win on a clash, so an override always takes its key.
        foreach (var (command, key) in merged.Where(kv => overrides == null || !overrides.ContainsKey(kv.Key)))
            _byKey[Normalise(key)] = command;
        if (overrides != null)
            foreach (var (command, key) in overrides) _byKey[Normalise(key)] = command;
        Bindings = merged;
    }

    /// <summary>Every command and its key.</summary>
    public IReadOnlyDictionary<string, string> Bindings { get; }

    public string? CommandFor(string gesture) => _byKey.TryGetValue(Normalise(gesture), out var c) ? c : null;

    /// <summary>A gesture in a standard order: Ctrl, Shift, Alt, then the key.</summary>
    public static string Normalise(string gesture)
    {
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string key = parts.Length > 0 ? parts[^1] : "";
        var mods = parts.Take(parts.Length - 1).Select(m => m.ToLowerInvariant()).ToHashSet();
        var s = "";
        if (mods.Contains("ctrl") || mods.Contains("control") || mods.Contains("cmd") || mods.Contains("meta")) s += "Ctrl+";
        if (mods.Contains("shift")) s += "Shift+";
        if (mods.Contains("alt")) s += "Alt+";
        return s + key.ToUpperInvariant();
    }
}

/// <summary>
/// The Modeler's state apart from its controls: the document, the tools and the active one, the selection mode,
/// display settings and MetaNURBS, so the logic can be driven (and tested) without a window.
/// </summary>
public sealed class ModelerSession
{
    public Document Document { get; private set; }
    public ToolContext Context { get; private set; }
    public ToolRegistry Registry { get; }
    public Commands Commands { get; private set; }
    public Preferences Preferences { get; }
    public Keymap Keymap { get; private set; }

    /// <summary>The armed tool (interactive tools wait for a drag in a view), or none.</summary>
    public Tool? ActiveTool { get; private set; }

    /// <summary>Whether the foreground is shown as its smoothed (MetaNURBS) surface.</summary>
    public bool MetaNurbs { get; private set; }
    public bool ShowPoints => Document.Selection.Mode == SelectionMode.Points;

    /// <summary>How the app smooths a cage for MetaNURBS (set by the app to the subdivision code).</summary>
    public static Func<Mesh, int, Mesh>? Subdivide { get; set; }

    /// <summary>Messages for the status bar (tool help, errors).</summary>
    public string Status { get; private set; } = "";

    public event Action? Changed;
    public event Action? DocumentReplaced;

    private readonly Dictionary<int, (int Version, Mesh Smooth)> _smoothCache = [];

    public ModelerSession(Preferences? prefs = null, ToolRegistry? registry = null)
    {
        Preferences = prefs ?? new Preferences();
        Registry = registry ?? ToolRegistry.Default;
        Keymap = new Keymap(Preferences.Keys);
        Document = new Document();
        Context = new ToolContext(Document);
        Commands = new Commands(Document, Registry);
        Attach();
    }

    private void Attach()
    {
        Document.Undo.Limit = Preferences.UndoLevels;
        Document.Changed += () => Changed?.Invoke();
        Context = new ToolContext(Document) { CenterMode = Preferences.ActionCenter };
        Commands = new Commands(Document, Registry);
        Commands.DocumentReplaced += doc => Replace(doc);
        _smoothCache.Clear();
    }

    /// <summary>Puts a new or loaded document in place.</summary>
    public void Replace(Document doc)
    {
        ActiveTool?.Cancel();
        ActiveTool = null;
        Document = doc;
        Attach();
        DocumentReplaced?.Invoke();
        Changed?.Invoke();
    }

    public void SetStatus(string text)
    {
        Status = text;
        Changed?.Invoke();
    }

    public void ReloadKeys() => Keymap = new Keymap(Preferences.Keys);

    /// <summary>
    /// Picks a tool: an interactive one is armed (and stays armed for drags until committed); an immediate one runs
    /// now; a requester returns false so the UI can ask for its numbers first.
    /// </summary>
    public bool Activate(string toolName)
    {
        var tool = Registry.Create(toolName);
        return Activate(tool);
    }

    public bool Activate(Tool tool)
    {
        CommitTool();
        switch (tool.Kind)
        {
            case ToolKind.Immediate:
                Run(tool);
                return true;
            case ToolKind.Requester:
                return false;
            default:
                ActiveTool = tool;
                tool.Begin(Context);
                SetStatus($"{tool.Name}: {tool.Help}");
                return true;
        }
    }

    /// <summary>Runs a tool in one go, reporting any failure in the status bar rather than throwing.</summary>
    public bool Run(Tool tool)
    {
        try
        {
            tool.Run(Context);
            SetStatus($"{tool.Name} done.");
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            tool.Cancel();
            SetStatus($"{tool.Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>Keeps what the active tool did (one undo step) and disarms it.</summary>
    public void CommitTool()
    {
        if (ActiveTool == null) return;
        ActiveTool.Commit();
        ActiveTool = null;
        Changed?.Invoke();
    }

    /// <summary>Throws away what the active tool did and disarms it.</summary>
    public void CancelTool()
    {
        if (ActiveTool == null) return;
        ActiveTool.Cancel();
        ActiveTool = null;
        Changed?.Invoke();
    }

    /// <summary>Re-applies the active tool after its numbers changed in the numeric panel.</summary>
    public void ToolParametersChanged()
    {
        if (ActiveTool is { Active: true } t) t.Update();
    }

    public void CycleSelectionMode()
    {
        var s = Document.Selection;
        s.Mode = s.Mode switch
        {
            SelectionMode.Points => SelectionMode.Polygons,
            SelectionMode.Polygons => SelectionMode.Volume,
            SelectionMode.Volume => SelectionMode.Edges,
            _ => SelectionMode.Points,
        };
        Document.Notify();
    }

    public void SetSelectionMode(SelectionMode mode)
    {
        Document.Selection.Mode = mode;
        Document.Notify();
    }

    public void ToggleMetaNurbs()
    {
        MetaNurbs = !MetaNurbs;
        SetStatus(MetaNurbs ? "MetaNURBS on: the cage's smooth surface is shown (Metaform bakes it)." : "MetaNURBS off.");
    }

    /// <summary>A layer's smoothed surface for MetaNURBS, cached until the layer changes.</summary>
    public Mesh? Smoothed(int layer)
    {
        if (!MetaNurbs || Subdivide == null) return null;
        var mesh = Document.Layers[layer].Mesh;
        if (mesh.Polygons.Count == 0) return null;
        if (_smoothCache.TryGetValue(layer, out var c) && c.Version == mesh.Version) return c.Smooth;
        var smooth = Subdivide(mesh, Math.Clamp(Preferences.SubdivisionLevel, 1, 4));
        _smoothCache[layer] = (mesh.Version, smooth);
        return smooth;
    }

    /// <summary>What the views draw: the foreground and background layers, the selection, and the display settings.</summary>
    public ViewScene BuildScene(bool perspective)
    {
        var scene = new ViewScene
        {
            Surfaces = Document.Surfaces,
            Style = perspective ? Preferences.PerspectiveStyle : DisplayStyle.Wireframe,
            ShowPoints = ShowPoints,
            BackfaceTint = Preferences.BackfaceTint,
            Version = Document.Version,
        };
        var sel = Document.Selection;
        for (int i = 0; i < Document.Layers.Count; i++)
        {
            var layer = Document.Layers[i];
            if (layer.State == LayerState.Off || layer.Mesh.IsEmpty) continue;
            bool fg = layer.State == LayerState.Foreground;
            scene.Layers.Add(new ViewLayer
            {
                Mesh = layer.Mesh,
                Background = !fg,
                SelectedPoints = fg && sel.Mode == SelectionMode.Points && sel.Points.TryGetValue(i, out var p) ? p : null,
                SelectedPolygons = fg && sel.Mode == SelectionMode.Polygons && sel.Polygons.TryGetValue(i, out var q) ? q : null,
                SelectedEdges = fg && sel.Mode == SelectionMode.Edges && sel.Edges.TryGetValue(i, out var e) ? e : null,
                Subdivided = fg ? Smoothed(i) : null,
            });
        }
        if (sel.Mode == SelectionMode.Volume && !sel.Volume.IsEmpty) scene.Overlay.AddRange(BoxLines(Clamp(sel.Volume), Palette.Volume));
        return scene;
    }

    /// <summary>A volume's unbounded sides drawn at the scene's extent, so it shows.</summary>
    private Box3 Clamp(Box3 v)
    {
        var b = Document.Bounds().Expanded(1);
        if (b.IsEmpty) b = new Box3(-Vec3.One * 10, Vec3.One * 10);
        return new Box3(Vec3.Max(v.Min, b.Min), Vec3.Min(v.Max, b.Max));
    }

    public static IEnumerable<OverlayLine> BoxLines(Box3 b, uint color)
    {
        var c = new Vec3[8];
        for (int i = 0; i < 8; i++) c[i] = new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
        int[][] edges = [[0, 1], [2, 3], [4, 5], [6, 7], [0, 2], [1, 3], [4, 6], [5, 7], [0, 4], [1, 5], [2, 6], [3, 7]];
        return edges.Select(e => new OverlayLine(c[e[0]], c[e[1]], color));
    }

    /// <summary>Counts for the status bar: points and polygons in the foreground, the selection, and polygon types.</summary>
    public string Counts()
    {
        int points = 0, polys = 0, selPoints = 0, selPolys = 0;
        var types = (0, 0, 0, 0, 0);
        var sel = Document.Selection;
        foreach (int l in Document.ForegroundLayers)
        {
            var m = Document.Layers[l].Mesh;
            points += m.Points.Count;
            polys += m.Polygons.Count;
            selPoints += sel.Points.TryGetValue(l, out var p) ? p.Count : 0;
            selPolys += sel.Polygons.TryGetValue(l, out var q) ? q.Count : 0;
            var t = m.PolygonTypes();
            types = (types.Item1 + t.Points, types.Item2 + t.Lines, types.Item3 + t.Triangles, types.Item4 + t.Quads, types.Item5 + t.NGons);
        }
        string selected = sel.Mode switch
        {
            SelectionMode.Points => $"{selPoints} of {points} points",
            SelectionMode.Polygons => $"{selPolys} of {polys} polygons",
            SelectionMode.Edges => $"{sel.Edges.Values.Sum(e => e.Count)} edges",
            _ => sel.Volume.IsEmpty ? "no volume" : "volume set",
        };
        return $"{points} points · {polys} polygons ({types.Item3} tri, {types.Item4} quad, {types.Item5} n-gon, {types.Item2} line, {types.Item1} point) · {selected}";
    }
}
