using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVAModeler.Layout.UI;

/// <summary>
/// What Layout's view looks through: the scene's camera, a free perspective, one of the three flat views, or a light
/// (the chosen light, else the first: classic Layout's Light view, for aiming spots).
/// </summary>
public enum LayoutViewMode { Camera, Perspective, Top, Front, Side, Light }

/// <summary>
/// What dragging the chosen item does, as classic Layout's mouse modes: <see cref="MoveOrRotate"/> (the default) moves it
/// with the left button and turns it with the right; the others are one action each (the right button working the
/// third axis: depth for Move, bank for Rotate, Z for Stretch).
/// </summary>
public enum ItemDrag { MoveOrRotate, Move, Rotate, Size, Stretch }

/// <summary>The axes a drag may change (classic Layout's X, Y and Z buttons): X, Y, Z or heading, pitch, bank.</summary>
[Flags]
public enum DragAxes { None = 0, X = 1, Y = 2, Z = 4, All = X | Y | Z }

/// <summary>
/// Layout's view: the scene at the current frame through the chosen camera, with the camera's safe areas. Click an
/// item to choose it; drag it to move it (right-drag turns it), keyed at the frame. The wheel zooms and the middle
/// button pans; dragging empty space in the perspective view turns the view.
/// </summary>
public sealed class SceneView : Control
{
    private readonly LayoutSession _session;
    private readonly SoftwareRenderer _renderer = new();
    private WriteableBitmap? _bitmap;
    private readonly Dictionary<LayoutViewMode, ViewCamera> _cameras = new()
    {
        [LayoutViewMode.Perspective] = new ViewCamera(ViewKind.Perspective) { Distance = 8 },
        [LayoutViewMode.Top] = new ViewCamera(ViewKind.Top) { PixelsPerUnit = 50 },
        [LayoutViewMode.Front] = new ViewCamera(ViewKind.Front) { PixelsPerUnit = 50 },
        [LayoutViewMode.Side] = new ViewCamera(ViewKind.Side) { PixelsPerUnit = 50 },
        [LayoutViewMode.Camera] = new ViewCamera(ViewKind.Perspective),
        [LayoutViewMode.Light] = new ViewCamera(ViewKind.Perspective),
    };
    private LayoutViewMode _mode = LayoutViewMode.Camera;
    public bool Shaded { get; set; } = true;
    private Point _press, _last;
    private bool _dragItem, _rotateItem, _pan, _orbit, _moved, _comb, _uncomb;
    private Point? _hover;
    private Vec3 _startPosition, _startRotation, _startScale;

    /// <summary>What dragging the chosen item does (<see cref="ItemDrag.MoveOrRotate"/> unless a host changes it).</summary>
    public ItemDrag DragMode { get; set; } = ItemDrag.MoveOrRotate;

    /// <summary>The axes a drag may change (all unless a host restricts them).</summary>
    public DragAxes Axes { get; set; } = DragAxes.All;

    /// <summary>
    /// Which items a click may choose (classic Layout's Objects, Bones, Lights or Cameras edit mode); null, any. Locked
    /// items (<see cref="SceneItem.Locked"/>) are never chosen or dragged with the mouse.
    /// </summary>
    public Func<SceneItem, bool>? CanPick { get; set; }

    public SceneView(LayoutSession session)
    {
        _session = session;
        ClipToBounds = true;
        Focusable = true;
        session.Changed += InvalidateVisual;
    }

    public LayoutViewMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            ModeChanged?.Invoke();
            InvalidateVisual();
        }
    }

    public event Action? ModeChanged;

    /// <summary>The view's camera this frame (the scene camera's, in Camera mode).</summary>
    public ViewCamera Camera
    {
        get
        {
            var c = _cameras[_mode];
            c.Resize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
            // Look from the scene camera: its position, heading, pitch and bank, and its field of view.
            if (_mode == LayoutViewMode.Camera && _session.Scene.CurrentCamera is { } cam) SceneDrawing.LookThrough(c, cam, _session.Frame);
            if (_mode == LayoutViewMode.Light && LightShown is { } light) SceneDrawing.LookThrough(c, light, _session.Frame);
            return c;
        }
    }

    /// <summary>The light the Light view looks through: the chosen one, else the scene's first.</summary>
    public LightItem? LightShown => _session.Selected as LightItem ?? _session.Scene.Lights.FirstOrDefault();

    public void Fit()
    {
        var box = SceneDrawing.Bounds(_session.Scene.Evaluate(_session.Frame));
        foreach (var (mode, c) in _cameras)
        {
            if (mode is LayoutViewMode.Camera or LayoutViewMode.Light) continue;
            c.Resize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
            c.Fit(box);
        }
        InvalidateVisual();
    }

    /// <summary>The last picture drawn (for screenshots and tests).</summary>
    public SoftwareRenderer Renderer => _renderer;

    private bool _drawsScene = true;

    /// <summary>
    /// Whether the view draws the scene itself, in software (the default). A host that draws it with OpenGL underneath
    /// (a <c>GlSurface</c> stacked below, drawing <see cref="CurrentView"/>) sets it false: the view then draws no
    /// picture and leaves its background transparent, but still draws the camera's frame guides and the label on top,
    /// and handles the mouse as ever. <see cref="Redrawn"/> says when the picture underneath should be drawn again.
    /// </summary>
    public bool DrawsScene
    {
        get => _drawsScene;
        set
        {
            if (_drawsScene == value) return;
            _drawsScene = value;
            InvalidateVisual();
        }
    }

    /// <summary>How many times the view has drawn the scene itself (in software).</summary>
    public int ScenesDrawn { get; private set; }

    /// <summary>
    /// Raised each time the view is drawn (its scene, frame, mode or camera may have changed), after it: a host drawing
    /// the scene underneath asks for a frame of its own then.
    /// </summary>
    public event Action? Redrawn;

    /// <summary>
    /// What the view would draw now: the scene at the current frame (<see cref="LayoutSession.BuildScene"/>, with the
    /// chosen item boxed, every item's drawing and the grid) and the camera this frame (<see cref="Camera"/>, sized to the view).
    /// </summary>
    public (ViewScene Scene, ViewCamera Camera) CurrentView()
    {
        var cam = Camera;
        return (_session.BuildScene(_session.Scene.Evaluate(_session.Frame), Shaded), cam);
    }

    public override void Render(DrawingContext context)
    {
        int w = Math.Max(1, (int)Bounds.Width), h = Math.Max(1, (int)Bounds.Height);
        if (_drawsScene)
        {
            var (scene, cam) = CurrentView();
            _renderer.Render(scene, cam);
            if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using (var fb = _bitmap.Lock())
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy((int[])(object)_renderer.Pixels, y * w, fb.Address + y * fb.RowBytes, w);
            context.DrawImage(_bitmap, new Rect(0, 0, w, h));
            ScenesDrawn++;
        }
        else
        {
            // Nothing drawn, but the whole view still takes the mouse (a transparent fill is hit; no fill isn't).
            context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        }
        if (_mode == LayoutViewMode.Camera && _session.Scene.CurrentCamera is { } c) DrawFrameGuides(context, c, w, h);
        var label = _mode switch
        {
            LayoutViewMode.Camera => $"Camera: {_session.Scene.CurrentCamera?.Name ?? "(none)"}",
            LayoutViewMode.Light => $"Light: {LightShown?.Name ?? "(none)"}",
            _ => _mode.ToString(),
        };
        if (_session.HairBrush.On && _hover is { } at)
        {
            // The brush, its size on screen.
            double r = _session.HairBrush.Radius;
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 220, 120)), 1.5), at, r, r);
            label += " · combing hair (Shift or right-drag uncombs)";
        }
        context.DrawText(new FormattedText($"{label} · frame {_session.Frame:0}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 12, Brushes.White), new Point(6, 4));
        Redrawn?.Invoke();
    }

    /// <summary>The camera's picture shape, and its action- and title-safe areas, as classic Layout showed them.</summary>
    private static void DrawFrameGuides(DrawingContext dc, CameraItem cam, int w, int h)
    {
        double aspect = cam.FrameAspect, fw = w, fh = w / aspect;
        if (fh > h) { fh = h; fw = h * aspect; }
        var frame = new Rect((w - fw) / 2, (h - fh) / 2, fw, fh);
        var dim = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
        dc.FillRectangle(dim, new Rect(0, 0, w, frame.Top));
        dc.FillRectangle(dim, new Rect(0, frame.Bottom, w, h - frame.Bottom));
        dc.FillRectangle(dim, new Rect(0, frame.Top, frame.Left, fh));
        dc.FillRectangle(dim, new Rect(frame.Right, frame.Top, w - frame.Right, fh));
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1, new DashStyle([3, 3], 0));
        foreach (double f in new[] { cam.ActionSafe, cam.TitleSafe })
            dc.DrawRectangle(null, pen, new Rect(frame.Center.X - fw * f / 2, frame.Center.Y - fh * f / 2, fw * f, fh * f));
    }

    // ───────────────────────────── the mouse ─────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        _press = _last = p;
        _moved = false;
        _pan = props.IsMiddleButtonPressed || props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && _mode != LayoutViewMode.Perspective;
        _orbit = !_pan && _mode == LayoutViewMode.Perspective && (e.KeyModifiers.HasFlag(KeyModifiers.Alt) || Pick(p) == null && _session.Selected == null);
        if (!_pan && !_orbit && _session.HairBrush.On && _session.Selected is ObjectItem { Hair: not null, Locked: false } combed
            && (props.IsLeftButtonPressed || props.IsRightButtonPressed))
        {
            // The hair brush: the stroke, press to release, is one undo step. Shift or the right button uncombs.
            _comb = true;
            _uncomb = props.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _session.BeginEdit($"Comb {combed.Name}'s Hair");
        }
        else if (!_pan && !_orbit && _session.Selected is { Locked: false } item && (props.IsLeftButtonPressed || props.IsRightButtonPressed))
        {
            _dragItem = props.IsLeftButtonPressed;
            _rotateItem = props.IsRightButtonPressed;
            _startPosition = item.PositionAt(_session.Frame);
            _startRotation = item.RotationAt(_session.Frame);
            _startScale = item.ScaleAt(_session.Frame);
            // The whole drag, press to release, is one undo step (none if it doesn't move).
            _session.BeginEdit($"{LayoutSession.VectorVerb(FirstChannel)} {item.Name}");
        }
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_session.HairBrush.On)
        {
            _hover = p;
            InvalidateVisual();
        }
        if (_comb)
        {
            CombStroke(p);
            return;
        }
        if (!(_pan || _orbit || _dragItem || _rotateItem)) return;
        if (Math.Abs(p.X - _press.X) + Math.Abs(p.Y - _press.Y) > 3) _moved = true;
        if (!_moved) return;
        var cam = Camera;
        if (_pan && _mode is not (LayoutViewMode.Camera or LayoutViewMode.Light)) cam.Pan(p.X - _last.X, p.Y - _last.Y);
        else if (_orbit) cam.Orbit((p.X - _last.X) * 0.4, (p.Y - _last.Y) * 0.4);
        else if (_session.Selected is { } item && (_dragItem || _rotateItem)) DragItem(item, cam, p.X - _press.X, p.Y - _press.Y);
        _last = p;
        InvalidateVisual();
    }

    /// <summary>One stroke of the hair brush, from the last place the mouse was to <paramref name="p"/>.</summary>
    private void CombStroke(Point p)
    {
        if (Math.Abs(p.X - _press.X) + Math.Abs(p.Y - _press.Y) > 3) _moved = true;
        if (_session.Selected is not ObjectItem { Hair: not null } o) return;
        var next = _session.HairBrush.Stroke(o, _session.Frame, Camera, (_last.X, _last.Y), (p.X, p.Y), _uncomb);
        if (next != null)
        {
            o.Hair = next;
            _session.Scene.Notify();
        }
        _last = p;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    /// <summary>The first channel a drag changes: 0 position, 3 rotation, 6 scale.</summary>
    private int FirstChannel => DragMode switch
    {
        ItemDrag.Move => 0,
        ItemDrag.Rotate => 3,
        ItemDrag.Size or ItemDrag.Stretch => 6,
        _ => _dragItem ? 0 : 3,
    };

    private static Vec3 Masked(Vec3 start, Vec3 v, DragAxes axes) =>
        new(axes.HasFlag(DragAxes.X) ? v.X : start.X, axes.HasFlag(DragAxes.Y) ? v.Y : start.Y, axes.HasFlag(DragAxes.Z) ? v.Z : start.Z);

    /// <summary>A drag of the chosen item by (dx, dy) pixels since the press, by <see cref="DragMode"/> and <see cref="Axes"/>.</summary>
    private void DragItem(SceneItem item, ViewCamera cam, double dx, double dy)
    {
        bool left = _dragItem;
        switch (DragMode)
        {
            case ItemDrag.MoveOrRotate when left:
            case ItemDrag.Move when left:
            {
                // Moved in the view's plane, so far as the view shows it (in the parent's space, as LightWave did for unparented items).
                var at = item.WorldPosition(_session.Frame);
                var delta = cam.DragDelta(dx, dy, at);
                _session.SetVector(item, 0, Masked(_startPosition, _startPosition + delta, Axes));
                break;
            }
            case ItemDrag.Move:
            {
                // The right button moves along the view's depth: up the screen is away from the eye.
                var at = item.WorldPosition(_session.Frame);
                double perPixel = cam.DragDelta(1, 0, at).Length;
                var delta = cam.Basis().Forward * (-dy * perPixel);
                _session.SetVector(item, 0, Masked(_startPosition, _startPosition + delta, Axes));
                break;
            }
            case ItemDrag.MoveOrRotate:
                _session.SetVector(item, 3, Masked(_startRotation, _startRotation + new Vec3(dx * 0.5, dy * 0.5, 0), Axes));
                break;
            case ItemDrag.Rotate:
                _session.SetVector(item, 3, Masked(_startRotation, _startRotation + (left ? new Vec3(dx * 0.5, dy * 0.5, 0) : new Vec3(0, 0, dx * 0.5)), Axes));
                break;
            case ItemDrag.Size:
            {
                double f = Math.Exp(dx * 0.01);
                _session.SetVector(item, 6, Masked(_startScale, _startScale * f, Axes));
                break;
            }
            case ItemDrag.Stretch:
            {
                var f = left ? new Vec3(Math.Exp(dx * 0.01), Math.Exp(-dy * 0.01), 1) : new Vec3(1, 1, Math.Exp(dx * 0.01));
                _session.SetVector(item, 6, Masked(_startScale, new Vec3(_startScale.X * f.X, _startScale.Y * f.Y, _startScale.Z * f.Z), Axes));
                break;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        bool combing = _comb;
        EndDrag();
        if (!_moved && !_pan && !combing) _session.Select(Pick(p));
        _pan = _orbit = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
        _pan = _orbit = false;
    }

    private void EndDrag()
    {
        if (_dragItem || _rotateItem || _comb) _session.EndEdit();
        _dragItem = _rotateItem = _comb = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_mode is LayoutViewMode.Camera or LayoutViewMode.Light) return;
        var p = e.GetPosition(this);
        Camera.Zoom(Math.Pow(1.15, e.Delta.Y), p.X, p.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Whether a click may choose an item: not locked, and allowed by <see cref="CanPick"/>.</summary>
    public bool Pickable(SceneItem item) => !item.Locked && (CanPick?.Invoke(item) ?? true);

    /// <summary>The item under a point: the nearest light, camera, null or bone within a few pixels, else the front-most object.</summary>
    public SceneItem? Pick(Point p)
    {
        var cam = Camera;
        var frame = _session.Scene.Evaluate(_session.Frame);
        SceneItem? best = null;
        double bestD = 12;
        foreach (var (item, m) in frame.World)
        {
            if (item is ObjectItem || !Pickable(item)) continue;
            var s = cam.Project(m.TransformPoint(Vec3.Zero));
            if (!s.Visible) continue;
            double d = Math.Sqrt((s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y));
            if (d < bestD) { bestD = d; best = item; }
        }
        if (best != null) return best;
        var ray = cam.RayAt(p.X, p.Y);
        double bestT = double.PositiveInfinity;
        foreach (var o in frame.Objects)
        {
            if (!Pickable(o.Item)) continue;
            var mesh = o.WorldMesh;
            foreach (var t in Tessellation.Of(mesh))
                if (ray.IntersectTriangle(mesh.Points[t.A], mesh.Points[t.B], mesh.Points[t.C]) is { } hit && hit.T < bestT)
                {
                    bestT = hit.T;
                    best = o.Item;
                }
        }
        return best;
    }
}
