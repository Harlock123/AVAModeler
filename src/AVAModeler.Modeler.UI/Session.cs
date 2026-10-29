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
    /// <summary>Whether the numeric panel is folded to a strip.</summary>
    public bool NumericCollapsed { get; set; }
    /// <summary>Whether the AI Chat carries on by itself after its tool-call limit, instead of waiting for "continue".</summary>
    public bool ChatAutoContinue { get; set; } = true;
    /// <summary>
    /// Whether AI agents may drive the open document through MCP (<c>AVAModeler --mcp --attach</c>), over a local
    /// pipe only this user can open. Off unless turned on.
    /// </summary>
    public bool AllowAgents { get; set; }
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
        ["Maximize View"] = "D0", ["Numeric Panel"] = "N", ["UV View"] = "U", ["MetaNURBS"] = "Tab", ["Swap Layers"] = "OemPipe", ["Zoom In"] = "OemPeriod", ["Zoom Out"] = "OemComma",
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

    /// <summary>Whether the armed tool has been dragged since it was armed or last began a step.</summary>
    public bool ToolUsed { get; set; }

    /// <summary>The armed tool's work in the UV view: the flat copy of the UV map it works on, and the model as it was.</summary>
    private sealed class UVEdit(UVSpace space, UndoStack.Snapshot before, ToolContext context)
    {
        public UVSpace Space { get; } = space;
        public UndoStack.Snapshot Before { get; } = before;
        public ToolContext Context { get; } = context;
        /// <summary>Whether the tool did more than move points, so its work can't be UVs.</summary>
        public bool Broken;
    }

    private UVEdit? _uvEdit;

    /// <summary>The context the armed tool works in: the UV view's flat copy while it's at work there, else the model's.</summary>
    public ToolContext ActiveContext => _uvEdit?.Context ?? Context;

    /// <summary>Whether the armed tool is at work in the UV view.</summary>
    public bool InUVEdit => _uvEdit != null;

    /// <summary>Whether a tool used in the UV view made or removed geometry, which UVs can't be (the UI then cancels it).</summary>
    public bool UVEditBroken => _uvEdit?.Broken ?? false;

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
        CancelTool();
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
                ToolUsed = false;
                tool.Begin(Context);
                SetStatus($"{tool.Name}: {tool.Help}");
                return true;
        }
    }

    /// <summary>
    /// Gets the armed tool ready for a drag in a view: <paramref name="uv"/> for the UV view, where it works on the
    /// current UV map laid out flat (see <see cref="UVSpace"/>) and what it does is written back as UVs. A drag in the
    /// other kind of view than the step under way, or one that starts a new step, first finishes that step (kept if the
    /// tool was used, else dropped). <paramref name="uvSnap"/> is the UV view's grid step for snapping. Returns false
    /// when the tool can't work there (no UV map to work on).
    /// </summary>
    public bool ReadyTool(bool uv, bool newStep, double uvSnap = 0)
    {
        var tool = ActiveTool;
        if (tool == null) return false;
        if (tool.Active && (newStep || uv != (_uvEdit != null)))
        {
            if (ToolUsed) KeepStep();
            else DropStep();
            ToolUsed = false;
        }
        if (tool.Active) return true;
        if (!uv)
        {
            tool.Begin(Context);
            return true;
        }
        var map = Document.ActiveUVMap;
        if (map == null)
        {
            SetStatus("There's no UV map to work on: make one with Make UVs (Polygon tab).");
            return false;
        }
        var space = new UVSpace(Document, map);
        var edit = new UVEdit(space, Document.Snapshot($"{tool.Name} UVs"), new ToolContext(space.Flat) { CenterMode = Context.CenterMode, Snap = uvSnap });
        space.Flat.Changed += () =>
        {
            if (_uvEdit != edit) return;
            if (space.WriteBack()) Document.Notify();
            else edit.Broken = true;
        };
        _uvEdit = edit;
        tool.Begin(edit.Context);
        return true;
    }

    /// <summary>Keeps the tool's step: one undo step in the model (for work in the UV view, the UVs it changed).</summary>
    private void KeepStep()
    {
        var tool = ActiveTool!;
        if (tool.Active) tool.Commit();
        if (_uvEdit is { } edit)
        {
            _uvEdit = null;
            Document.Commit(edit.Before);
        }
    }

    /// <summary>Throws the tool's step away, putting the model back as it was.</summary>
    private void DropStep()
    {
        var tool = ActiveTool!;
        if (tool.Active) tool.Cancel();
        if (_uvEdit is { } edit)
        {
            _uvEdit = null;
            Document.Revert(edit.Before);
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
        if (_uvEdit is { Broken: true }) DropStep();
        else KeepStep();
        ActiveTool = null;
        Changed?.Invoke();
    }

    /// <summary>Throws away what the active tool did and disarms it.</summary>
    public void CancelTool()
    {
        if (ActiveTool == null) return;
        DropStep();
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
        var scene = ViewScene.FromDocument(Document, perspective ? Preferences.PerspectiveStyle : DisplayStyle.Wireframe, Preferences.BackfaceTint, Smoothed);
        var sel = Document.Selection;
        if (sel.Mode == SelectionMode.Volume && !sel.Volume.IsEmpty) scene.Overlay.AddRange(OverlayLine.Box(Clamp(sel.Volume), Palette.Volume));
        return scene;
    }

    /// <summary>
    /// What the UV view draws: the current UV map of each foreground layer laid out flat (u across, v up) with the
    /// selection, the 0–1 square, and behind it the image of the first texture laid on by this map (null if there's
    /// no UV map at all).
    /// </summary>
    public ViewScene BuildUVScene(out UVSpace? space)
    {
        var scene = new ViewScene { Style = DisplayStyle.Wireframe, ShowPoints = ShowPoints, Version = Document.Version, Surfaces = Document.Surfaces };
        var square = new[] { Vec3.Zero, Vec3.UnitX, new Vec3(1, 1, 0), Vec3.UnitY };
        for (int i = 0; i < 4; i++) scene.Overlay.Add(new OverlayLine(square[i], square[(i + 1) % 4], Palette.UVSquare));
        space = null;
        if (Document.ActiveUVMap is not { } map) return scene;
        space = new UVSpace(Document, map);
        var sel = space.Flat.Selection;
        foreach (int i in Document.ForegroundLayers)
        {
            var mesh = space.Flat.Layers[i].Mesh;
            if (mesh.IsEmpty) continue;
            scene.Layers.Add(new ViewLayer
            {
                Mesh = mesh,
                SelectedPoints = sel.Mode == SelectionMode.Points && sel.Points.TryGetValue(i, out var p) ? p : null,
                SelectedPolygons = sel.Mode == SelectionMode.Polygons && sel.Polygons.TryGetValue(i, out var q) ? q : null,
                SelectedEdges = sel.Mode == SelectionMode.Edges && sel.Edges.TryGetValue(i, out var e) ? e : null,
            });
        }
        if (UVImage(map) is { } image) scene.Backdrop = TextureImages.Load(image, Document.ImageBase);
        return scene;
    }

    /// <summary>
    /// The image the UV view shows behind a map: that of the first colour texture laid on by it, preferring surfaces the
    /// foreground uses; or null.
    /// </summary>
    public string? UVImage(string map)
    {
        var used = Document.ForegroundLayers.SelectMany(l => Document.Layers[l].Mesh.Polygons).Select(p => p.Surface).ToHashSet();
        var layers = Document.Surfaces.Select((s, i) => (s, i)).OrderBy(x => used.Contains(x.i) ? 0 : 1)
            .SelectMany(x => x.s.Textures)
            .Where(t => t.IsImage && t.Projection == TextureProjection.UV && t.UVMap == map && t.Image.Length > 0);
        return layers.OrderBy(t => t.Channel == TextureChannel.Color ? 0 : 1).FirstOrDefault()?.Image;
    }

    /// <summary>Notes for the status bar about texture images that can't be found or read (a checker stands in for each).</summary>
    public List<string> ImageProblems() => TextureImages.Problems(Document.Surfaces, Document.ImageBase);

    /// <summary>A volume's unbounded sides drawn at the scene's extent, so it shows.</summary>
    private Box3 Clamp(Box3 v)
    {
        var b = Document.Bounds().Expanded(1);
        if (b.IsEmpty) b = new Box3(-Vec3.One * 10, Vec3.One * 10);
        return new Box3(Vec3.Max(v.Min, b.Min), Vec3.Min(v.Max, b.Max));
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
