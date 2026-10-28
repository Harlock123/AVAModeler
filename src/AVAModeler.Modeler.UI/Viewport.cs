using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Tools;
using SelectionMode = AVAModeler.Core.SelectionMode;
using GL = Silk.NET.OpenGL.GL;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace AVAModeler.Modeler.UI;

/// <summary>Draws a view with the software renderer into a bitmap.</summary>
public sealed class SoftwareSurface : Control
{
    private readonly SoftwareRenderer _renderer = new();
    private WriteableBitmap? _bitmap;
    public required Func<ViewScene> Scene { get; init; }
    public required ViewCamera Camera { get; init; }

    public SoftwareRenderer Renderer => _renderer;

    public override void Render(DrawingContext context)
    {
        int w = Math.Max(1, (int)Bounds.Width), h = Math.Max(1, (int)Bounds.Height);
        Camera.Resize(w, h);
        _renderer.Render(Scene(), Camera);
        if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var fb = _bitmap.Lock())
        {
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy((int[])(object)_renderer.Pixels, y * w, fb.Address + y * fb.RowBytes, w);
        }
        context.DrawImage(_bitmap, new Rect(0, 0, w, h));
    }
}

/// <summary>Draws a view with OpenGL. If the context can't be had, <see cref="Failed"/> is raised and the view falls back to software.</summary>
public sealed class GlSurface : OpenGlControlBase
{
    private GlRenderer? _renderer;
    public required Func<ViewScene> Scene { get; init; }
    public required ViewCamera Camera { get; init; }
    public event Action<string>? Failed;
    public string? Description { get; private set; }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            var api = GL.GetApi(gl.GetProcAddress);
            bool es = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES;
            _renderer = new GlRenderer(api, es);
            Description = $"{_renderer.Name} ({gl.Renderer})";
        }
        catch (Exception e)
        {
            Failed?.Invoke(e.Message);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer?.Dispose();
        _renderer = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_renderer == null) return;
        try
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            Camera.Resize(Math.Max(1, (int)(Bounds.Width * scale)), Math.Max(1, (int)(Bounds.Height * scale)));
            // The camera works in device pixels here; the overlay and input work in layout units, so scale them.
            Camera.PixelsPerUnit *= scale;
            try { _renderer.Render(Scene(), Camera); }
            finally { Camera.PixelsPerUnit /= scale; Camera.Resize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height)); }
        }
        catch (Exception e)
        {
            _renderer = null;
            Failed?.Invoke(e.Message);
        }
    }
}

/// <summary>
/// One of the four views: its picture (software or OpenGL), the view's name and grid size, a rubber band while
/// selecting, and the mouse: click to pick, drag a box (or right-drag a lasso) to select, drag with a tool armed to
/// use it; the wheel zooms, the middle button (or Alt) pans, and dragging in the perspective view turns it. Any view
/// can be switched to the UV view (<see cref="ShowUV"/>): the current UV map laid out flat over its texture, where
/// points and polygons are picked as in the other views and the tools that move points move UVs.
/// </summary>
public sealed class ViewportControl : Panel
{
    private readonly ViewCamera _modelCamera;
    private readonly ViewCamera _uvCamera = new(ViewKind.UV) { Center = new Vec3(0.5, 0.5, 0), PixelsPerUnit = 200 };
    private bool _uv;

    /// <summary>The camera the view looks through now: its own, or the UV view's.</summary>
    public ViewCamera Camera => _uv ? _uvCamera : _modelCamera;

    /// <summary>Which of the four views this is (whatever it's showing now).</summary>
    public ViewKind Kind => _modelCamera.Kind;

    /// <summary>The view's own camera, on the model, even while it shows the UV map.</summary>
    public ViewCamera ModelCamera => _modelCamera;

    /// <summary>Whether the view shows the UV map.</summary>
    public bool IsUV => _uv;

    public ModelerSession Session { get; }
    private Control _surface;
    private readonly Overlay _overlay;
    private bool _useGl;

    /// <summary>Asks to fill (or stop filling) the quad view.</summary>
    public event Action<ViewportControl>? MaximizeRequested;

    private enum Gesture { None, Pan, Orbit, Box, Lasso, Volume, Tool }
    private Gesture _gesture;
    private Point _press, _last;
    private bool _moved;
    private readonly List<Point> _lasso = [];
    private KeyModifiers _mods;

    public ViewportControl(ViewKind kind, ModelerSession session, bool openGl)
    {
        _modelCamera = new ViewCamera(kind) { PixelsPerUnit = 80 };
        Session = session;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Focusable = true;
        _overlay = new Overlay(this) { IsHitTestVisible = false };
        _surface = MakeSurface(openGl);
        Children.Add(_surface);
        Children.Add(_overlay);
        session.Changed += Redraw;
    }

    public string RendererName => _surface is GlSurface g ? g.Description ?? "OpenGL" : "Software";

    /// <summary>The last frame drawn by the software renderer (for screenshots and tests).</summary>
    public SoftwareRenderer? Software => (_surface as SoftwareSurface)?.Renderer;

    private Control MakeSurface(bool openGl)
    {
        _useGl = openGl;
        if (openGl)
        {
            var gl = new GlSurface { Scene = Scene, Camera = Camera };
            gl.Failed += message =>
            {
                Session.SetStatus($"OpenGL isn't available here ({message}); drawing in software.");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => UseRenderer(false));
            };
            return gl;
        }
        return new SoftwareSurface { Scene = Scene, Camera = Camera };
    }

    /// <summary>Switches between the software and OpenGL renderers.</summary>
    public void UseRenderer(bool openGl)
    {
        if (openGl == _useGl) return;
        Children.Remove(_surface);
        _surface = MakeSurface(openGl);
        Children.Insert(0, _surface);
        Redraw();
    }

    /// <summary>Shows the UV map in this view (or goes back to the view it was).</summary>
    public void ShowUV(bool on)
    {
        if (on == _uv) return;
        _uv = on;
        // The picture's camera changes with it.
        Children.Remove(_surface);
        _surface = MakeSurface(_useGl);
        Children.Insert(0, _surface);
        if (on) FitAll();
        Redraw();
    }

    private ViewScene Scene()
    {
        if (_uv) return UVScene();
        var scene = Session.BuildScene(Camera.IsPerspective);
        if (Session.Context.CenterMode == ActionCenter.Mouse || Session.ActiveTool != null && !Session.InUVEdit)
        {
            // A small cross at the action centre while a tool is armed.
            var c = Session.ActiveTool != null ? Session.Context.Center() : Session.Context.MousePoint;
            double s = 6 / Math.Max(Camera.IsPerspective ? Camera.Focal / Camera.Distance : Camera.PixelsPerUnit, 1e-9);
            scene.Overlay.Add(new OverlayLine(c - Vec3.UnitX * s, c + Vec3.UnitX * s, Palette.ActionCenter));
            scene.Overlay.Add(new OverlayLine(c - Vec3.UnitY * s, c + Vec3.UnitY * s, Palette.ActionCenter));
            scene.Overlay.Add(new OverlayLine(c - Vec3.UnitZ * s, c + Vec3.UnitZ * s, Palette.ActionCenter));
        }
        return scene;
    }

    /// <summary>The UV view's picture, with the action centre while a tool works on the UVs.</summary>
    private ViewScene UVScene()
    {
        var scene = Session.BuildUVScene(out _);
        if (Session.InUVEdit)
        {
            var c = Session.ActiveContext.Center();
            double s = 6 / Math.Max(Camera.PixelsPerUnit, 1e-9);
            scene.Overlay.Add(new OverlayLine(c - Vec3.UnitX * s, c + Vec3.UnitX * s, Palette.ActionCenter));
            scene.Overlay.Add(new OverlayLine(c - Vec3.UnitY * s, c + Vec3.UnitY * s, Palette.ActionCenter));
        }
        return scene;
    }

    public void Redraw()
    {
        _surface.InvalidateVisual();
        if (_surface is GlSurface g) g.RequestNextFrameRendering();
        _overlay.InvalidateVisual();
    }

    public void FitAll()
    {
        Camera.Resize((int)Bounds.Width, (int)Bounds.Height);
        Camera.Fit(_uv ? UVBounds(selected: false) : Session.Document.Bounds());
        Redraw();
    }

    public void FitSelected()
    {
        var box = Box3.Empty;
        var doc = Session.Document;
        if (_uv) box = UVBounds(selected: true);
        else
            foreach (int l in doc.ForegroundLayers)
            {
                var mesh = doc.Layers[l].Mesh;
                foreach (int i in doc.Selection.ActivePoints(l, mesh)) box = box.Include(mesh.Points[i]);
            }
        Camera.Resize((int)Bounds.Width, (int)Bounds.Height);
        Camera.Fit(box.IsEmpty ? _uv ? UVBounds(selected: false) : doc.Bounds() : box);
        Redraw();
    }

    /// <summary>The UV view's extent: the 0–1 square and every UV (or only the selection's, if there is one).</summary>
    private Box3 UVBounds(bool selected)
    {
        var box = selected ? Box3.Empty : new Box3(Vec3.Zero, new Vec3(1, 1, 0));
        if (Session.Document.ActiveUVMap == null) return box.IsEmpty ? new Box3(Vec3.Zero, new Vec3(1, 1, 0)) : box;
        var space = new UVSpace(Session.Document, Session.Document.ActiveUVMap);
        foreach (int l in space.Flat.ForegroundLayers)
        {
            var mesh = space.Flat.Layers[l].Mesh;
            var points = selected ? space.Flat.Selection.ActivePoints(l, mesh) : Enumerable.Range(0, mesh.Points.Count).ToArray();
            if (selected && !space.Flat.Selection.Any) continue;
            foreach (int i in points) box = box.Include(mesh.Points[i]);
        }
        return box;
    }

    // ───────────────────────────── the mouse ─────────────────────────────

    private static List<(int, Mesh)> ForegroundMeshes(Document doc) =>
        doc.ForegroundLayers.Select(l => (l, doc.Layers[l].Mesh)).ToList();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        _press = _last = p;
        _moved = false;
        _mods = e.KeyModifiers;
        Camera.Resize((int)Bounds.Width, (int)Bounds.Height);
        if (e.ClickCount == 2 && props.IsLeftButtonPressed && Session.ActiveTool == null)
        {
            MaximizeRequested?.Invoke(this);
            return;
        }
        if (props.IsMiddleButtonPressed || props.IsLeftButtonPressed && _mods.HasFlag(KeyModifiers.Alt))
            _gesture = Camera.IsPerspective && _mods.HasFlag(KeyModifiers.Alt) && props.IsLeftButtonPressed ? Gesture.Orbit : Gesture.Pan;
        else if (Session.ActiveTool != null && (props.IsLeftButtonPressed || props.IsRightButtonPressed))
        {
            _gesture = Gesture.Tool;
            BeginToolDrag(p, props.IsRightButtonPressed);
        }
        else if (props.IsRightButtonPressed) _gesture = Camera.IsPerspective ? Gesture.Orbit : Gesture.Lasso;
        else if (props.IsLeftButtonPressed)
            _gesture = Session.Document.Selection.Mode == SelectionMode.Volume && !Camera.IsPerspective && !_uv ? Gesture.Volume
                : Camera.IsPerspective ? Gesture.Orbit : Gesture.Box;
        _lasso.Clear();
        _lasso.Add(p);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_gesture == Gesture.None) return;
        double dx = p.X - _last.X, dy = p.Y - _last.Y;
        if (Math.Abs(p.X - _press.X) + Math.Abs(p.Y - _press.Y) > 3) _moved = true;
        switch (_gesture)
        {
            case Gesture.Pan:
                Camera.Pan(dx, dy);
                Redraw();
                break;
            case Gesture.Orbit:
                if (_moved) { Camera.Orbit(dx * 0.4, dy * 0.4); Redraw(); }
                break;
            case Gesture.Tool:
                ToolDrag(p, first: false);
                break;
            case Gesture.Lasso:
                _lasso.Add(p);
                _overlay.InvalidateVisual();
                break;
            default:
                _overlay.InvalidateVisual();
                break;
        }
        _last = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        var g = _gesture;
        _gesture = Gesture.None;
        e.Pointer.Capture(null);
        bool add = _mods.HasFlag(KeyModifiers.Shift), remove = _mods.HasFlag(KeyModifiers.Control);
        switch (g)
        {
            case Gesture.Orbit when !_moved:
            case Gesture.Box when !_moved:
            case Gesture.Lasso when !_moved:
                Click(p, add, remove);
                break;
            case Gesture.Box:
                SelectInside(Picking.Box(_press.X, _press.Y, p.X, p.Y), add, remove);
                break;
            case Gesture.Lasso:
                SelectInside(_lasso.Select(q => (q.X, q.Y)).ToList(), add, remove);
                break;
            case Gesture.Volume when _moved:
            {
                var sel = Session.Document.Selection;
                sel.Volume = Picking.VolumeFromDrag(Camera, _press.X, _press.Y, p.X, p.Y, sel.Volume);
                Session.Document.Notify();
                break;
            }
            case Gesture.Volume:
                Session.Document.Selection.Volume = Box3.Empty;
                Session.Document.Notify();
                break;
            case Gesture.Tool:
                Session.Document.Notify();
                break;
        }
        _lasso.Clear();
        Redraw();
    }

    /// <summary>
    /// Picks in this view: in the model, or for the UV view in its flat copy of the UV map, the selection going back
    /// to the model.
    /// </summary>
    private void Pick(Action<Document> pick)
    {
        if (!_uv)
        {
            pick(Session.Document);
            return;
        }
        if (Session.Document.ActiveUVMap is not { } map) return;
        var space = new UVSpace(Session.Document, map);
        pick(space.Flat);
        space.SelectionToModel();
        Session.Document.Notify();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        Camera.Resize((int)Bounds.Width, (int)Bounds.Height);
        Camera.Zoom(Math.Pow(1.15, e.Delta.Y), p.X, p.Y);
        Redraw();
        e.Handled = true;
    }

    private Vec3 _toolStart;
    private bool _alternate;

    private void BeginToolDrag(Point p, bool alternate)
    {
        var tool = Session.ActiveTool!;
        // A new drag with a tool that has already been used is a new step (one undo each), except for the
        // tools whose drags all shape one thing (a box's width in one view, its depth in another). A drag in the UV
        // view works on UVs, so it starts a step of its own too.
        if (!Session.ReadyTool(_uv, ToolUsed && !RefinesAcrossDrags(tool), UVSnap))
        {
            _gesture = Gesture.None;
            return;
        }
        Session.Document.Notify();
        var ctx = Session.ActiveContext;
        var through = ctx.CenterMode == ActionCenter.Mouse ? Camera.Center : ctx.Center();
        _toolStart = Camera.Unproject(p.X, p.Y, through);
        ctx.MousePoint = _toolStart;
        _alternate = alternate;
        ToolDrag(p, first: true);
    }

    /// <summary>Whether the active tool has been dragged since it was (re)armed (in any view).</summary>
    private bool ToolUsed
    {
        get => Session.ToolUsed;
        set => Session.ToolUsed = value;
    }

    /// <summary>The UV view's snapping step: its grid, when snapping is on.</summary>
    private double UVSnap => Session.Preferences.SnapToGrid ? _uvCamera.GridStep() : 0;

    private static bool RefinesAcrossDrags(Tool t) => t is BoxTool or BallTool or DiscTool or PointsTool or TextTool or LatheTool or MirrorTool;

    private void ToolDrag(Point p, bool first)
    {
        var tool = Session.ActiveTool;
        if (tool == null || !Session.ReadyTool(_uv, false, UVSnap)) return;
        var (r, u, f) = Camera.Basis();
        var current = Camera.Unproject(p.X, p.Y, _toolStart);
        var drag = new DragInfo
        {
            Start = _toolStart, Current = current, DxPixels = p.X - _press.X, DyPixels = p.Y - _press.Y,
            Right = r, Up = u, Forward = f, First = first, Alternate = _alternate, Constrain = _mods.HasFlag(KeyModifiers.Shift),
            UnitsPerPixel = Camera.DragDelta(1, 0, _toolStart).Length,
        };
        tool.Drag(drag);
        ToolUsed = true;
        if (Session.UVEditBroken)
        {
            Session.CancelTool();
            Session.SetStatus($"{tool.Name} can't work in the UV view: only tools that move points (Move, Rotate, Size and the like) can move UVs.");
            _gesture = Gesture.None;
        }
    }

    /// <summary>Resets the "used" flag when a tool is armed.</summary>
    public void ToolArmed() => ToolUsed = false;

    private void Click(Point p, bool add, bool remove) => Pick(doc => Click(doc, p, add, remove));

    private void Click(Document doc, Point p, bool add, bool remove)
    {
        var sel = doc.Selection;
        var layers = ForegroundMeshes(doc);
        bool keep = add || remove;
        switch (sel.Mode)
        {
            case SelectionMode.Points:
            {
                var hit = Picking.NearestPoint(Camera, layers, p.X, p.Y);
                if (!keep) sel.Clear();
                if (hit is { } h)
                {
                    if (remove) sel.DeselectPoint(h.Layer, h.Index);
                    else sel.SelectPoint(h.Layer, h.Index);
                }
                break;
            }
            case SelectionMode.Polygons:
            {
                var hit = Picking.PolygonAt(Camera, layers, p.X, p.Y);
                if (!keep) sel.Clear();
                if (hit is { } h)
                {
                    var set = sel.PolygonsIn(h.Layer);
                    if (remove) set.Remove(h.Index); else set.Add(h.Index);
                }
                break;
            }
            case SelectionMode.Edges:
            {
                var hit = Picking.NearestEdge(Camera, layers, p.X, p.Y);
                if (!keep) sel.Clear();
                if (hit is { } h)
                {
                    var set = sel.EdgesIn(h.Layer);
                    if (remove) set.Remove(h.Edge); else set.Add(h.Edge);
                }
                break;
            }
        }
        doc.Notify();
    }

    private void SelectInside(IReadOnlyList<(double X, double Y)> shape, bool add, bool remove) => Pick(doc => SelectInside(doc, shape, add, remove));

    private void SelectInside(Document doc, IReadOnlyList<(double X, double Y)> shape, bool add, bool remove)
    {
        var sel = doc.Selection;
        if (!add && !remove) sel.Clear();
        foreach (var (l, mesh) in ForegroundMeshes(doc))
            switch (sel.Mode)
            {
                case SelectionMode.Points:
                    foreach (int i in Picking.PointsInside(Camera, mesh, shape))
                        if (remove) sel.DeselectPoint(l, i); else sel.SelectPoint(l, i);
                    break;
                case SelectionMode.Polygons:
                    if (remove) sel.PolygonsIn(l).ExceptWith(Picking.PolygonsInside(Camera, mesh, shape));
                    else sel.PolygonsIn(l).UnionWith(Picking.PolygonsInside(Camera, mesh, shape));
                    break;
                case SelectionMode.Edges:
                    if (remove) sel.EdgesIn(l).ExceptWith(Picking.EdgesInside(Camera, mesh, shape));
                    else sel.EdgesIn(l).UnionWith(Picking.EdgesInside(Camera, mesh, shape));
                    break;
            }
        doc.Notify();
    }

    /// <summary>What's drawn over the picture: the view's name, the grid size, and a selection band or lasso.</summary>
    private sealed class Overlay(ViewportControl view) : Control
    {
        private static readonly Typeface Face = new(FontFamily.Default);
        private static readonly IBrush Label = new SolidColorBrush(Color.FromRgb(235, 235, 235));
        private static readonly IBrush Shadow = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
        private static readonly Pen Band = new(new SolidColorBrush(Color.FromRgb(255, 227, 74)), 1, new DashStyle([4, 3], 0));

        public override void Render(DrawingContext dc)
        {
            var cam = view.Camera;
            string title = view._uv ? $"UV: {view.Session.Document.ActiveUVMap ?? "(no UV map: Polygon › Make UVs)"}" : cam.Title;
            Text(dc, title, new Point(6, 4), 12);
            // UV space has no units: its grid is a fraction of the image.
            string grid = view._uv ? cam.GridStep().ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : ViewCamera.FormatLength(cam.GridStep());
            if (!cam.IsPerspective) Text(dc, "Grid: " + grid, new Point(6, Bounds.Height - 18), 11);
            if (view._gesture is Gesture.Box or Gesture.Volume && view._moved)
                dc.DrawRectangle(null, Band, new Rect(view._press, view._last).Normalize());
            if (view._gesture == Gesture.Lasso && view._lasso.Count > 1)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(view._lasso[0], false);
                    foreach (var q in view._lasso.Skip(1)) c.LineTo(q);
                    c.EndFigure(true);
                }
                dc.DrawGeometry(null, Band, g);
            }
        }

        private static void Text(DrawingContext dc, string s, Point at, double size)
        {
            dc.DrawText(new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Shadow), at + new Point(1, 1));
            dc.DrawText(new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Label), at);
        }
    }
}

/// <summary>The quad view: Top, Perspective, Front and Side, as classic Modeler laid them out; any can be maximized.</summary>
public sealed class QuadView : Grid
{
    public IReadOnlyList<ViewportControl> Views { get; }
    public ViewportControl? Maximized { get; private set; }

    public QuadView(ModelerSession session, bool openGl)
    {
        ColumnDefinitions = new ColumnDefinitions("*,2,*");
        RowDefinitions = new RowDefinitions("*,2,*");
        Background = new SolidColorBrush(Color.FromRgb(40, 40, 40));
        var top = new ViewportControl(ViewKind.Top, session, openGl);
        var persp = new ViewportControl(ViewKind.Perspective, session, openGl);
        var front = new ViewportControl(ViewKind.Front, session, openGl);
        var side = new ViewportControl(ViewKind.Side, session, openGl);
        Place(top, 0, 0);
        Place(persp, 0, 2);
        Place(front, 2, 0);
        Place(side, 2, 2);
        Views = [top, front, side, persp];
        foreach (var v in Views) v.MaximizeRequested += ToggleMaximize;
    }

    private void Place(Control c, int row, int col)
    {
        SetRow(c, row);
        SetColumn(c, col);
        Children.Add(c);
    }

    public void ToggleMaximize(ViewportControl view)
    {
        Maximized = Maximized == view ? null : view;
        foreach (var v in Views)
        {
            v.IsVisible = Maximized == null || v == Maximized;
            if (Maximized != null && v == Maximized)
            {
                SetRow(v, 0); SetColumn(v, 0); SetRowSpan(v, 3); SetColumnSpan(v, 3);
            }
        }
        if (Maximized == null)
        {
            var (top, front, side, persp) = (Views[0], Views[1], Views[2], Views[3]);
            foreach (var (v, r, c) in new[] { (top, 0, 0), (persp, 0, 2), (front, 2, 0), (side, 2, 2) })
            {
                SetRow(v, r); SetColumn(v, c); SetRowSpan(v, 1); SetColumnSpan(v, 1);
            }
        }
    }

    /// <summary>The view under the mouse last, or the maximized one, for keyboard zooms.</summary>
    public ViewportControl Focused => Maximized ?? Views.FirstOrDefault(v => v.IsPointerOver) ?? Views[0];

    public void FitAll() { foreach (var v in Views) v.FitAll(); }
    public void FitSelected() { foreach (var v in Views) v.FitSelected(); }

    /// <summary>The view showing the UV map, if one is.</summary>
    public ViewportControl? UVView => Views.FirstOrDefault(v => v.IsUV);

    /// <summary>
    /// Switches a view to the UV map or back: the one given, else the one under the mouse, else whichever already
    /// shows it, else the Side view. Only one view shows it at a time.
    /// </summary>
    public void ToggleUV(ViewportControl? view = null)
    {
        view ??= Maximized ?? Views.FirstOrDefault(v => v.IsPointerOver) ?? UVView ?? Views.First(v => v.Kind == ViewKind.Side);
        bool on = !view.IsUV;
        foreach (var v in Views) if (v != view) v.ShowUV(false);
        view.ShowUV(on);
    }
    public void UseRenderer(bool openGl) { foreach (var v in Views) v.UseRenderer(openGl); }
    public void Redraw() { foreach (var v in Views) v.Redraw(); }
    public void ToolArmed() { foreach (var v in Views) v.ToolArmed(); }
}
