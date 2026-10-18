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

/// <summary>What Layout's view looks through: the scene's camera, a free perspective, or one of the three flat views.</summary>
public enum LayoutViewMode { Camera, Perspective, Top, Front, Side }

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
    };
    private LayoutViewMode _mode = LayoutViewMode.Camera;
    public bool Shaded { get; set; } = true;
    private Point _press, _last;
    private bool _dragItem, _rotateItem, _pan, _orbit, _moved;
    private Vec3 _startPosition, _startRotation;

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
            if (_mode == LayoutViewMode.Camera && _session.Scene.CurrentCamera is { } cam)
            {
                // Look from the scene camera: its position and heading/pitch (bank isn't shown), and its field of view.
                var world = cam.WorldMatrix(_session.Frame);
                var eye = world.TransformPoint(Vec3.Zero);
                var hpb = Rotations.FromMatrix(world);
                c.Heading = hpb.X;
                c.Pitch = hpb.Y;
                c.Distance = 1;
                c.FieldOfView = cam.VerticalFov(_session.Frame);
                c.Center = eye + c.Basis().Forward;
            }
            return c;
        }
    }

    public void Fit()
    {
        var frame = _session.Scene.Evaluate(_session.Frame);
        var box = Box3.Empty;
        foreach (var o in frame.Objects) box = box.Include(o.WorldMesh.Bounds());
        foreach (var (item, m) in frame.World) if (item is not ObjectItem) box = box.Include(m.TransformPoint(Vec3.Zero));
        foreach (var (mode, c) in _cameras)
        {
            if (mode == LayoutViewMode.Camera) continue;
            c.Resize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
            c.Fit(box);
        }
        InvalidateVisual();
    }

    /// <summary>The last picture drawn (for screenshots and tests).</summary>
    public SoftwareRenderer Renderer => _renderer;

    public override void Render(DrawingContext context)
    {
        int w = Math.Max(1, (int)Bounds.Width), h = Math.Max(1, (int)Bounds.Height);
        var cam = Camera;
        var frame = _session.Scene.Evaluate(_session.Frame);
        _renderer.Render(_session.BuildScene(frame, Shaded), cam);
        if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var fb = _bitmap.Lock())
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy((int[])(object)_renderer.Pixels, y * w, fb.Address + y * fb.RowBytes, w);
        context.DrawImage(_bitmap, new Rect(0, 0, w, h));
        if (_mode == LayoutViewMode.Camera && _session.Scene.CurrentCamera is { } c) DrawFrameGuides(context, c, w, h);
        var label = _mode == LayoutViewMode.Camera ? $"Camera: {_session.Scene.CurrentCamera?.Name ?? "(none)"}" : _mode.ToString();
        context.DrawText(new FormattedText($"{label} · frame {_session.Frame:0}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 12, Brushes.White), new Point(6, 4));
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
        if (!_pan && !_orbit && _session.Selected is { } item && (props.IsLeftButtonPressed || props.IsRightButtonPressed))
        {
            _dragItem = props.IsLeftButtonPressed;
            _rotateItem = props.IsRightButtonPressed;
            _startPosition = item.PositionAt(_session.Frame);
            _startRotation = item.RotationAt(_session.Frame);
        }
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (!(_pan || _orbit || _dragItem || _rotateItem)) return;
        if (Math.Abs(p.X - _press.X) + Math.Abs(p.Y - _press.Y) > 3) _moved = true;
        if (!_moved) return;
        var cam = Camera;
        if (_pan && _mode != LayoutViewMode.Camera) cam.Pan(p.X - _last.X, p.Y - _last.Y);
        else if (_orbit) cam.Orbit((p.X - _last.X) * 0.4, (p.Y - _last.Y) * 0.4);
        else if (_session.Selected is { } item && _dragItem)
        {
            // Moved in the view's plane, so far as the view shows it (in the parent's space, as LightWave did for unparented items).
            var at = item.WorldPosition(_session.Frame);
            var delta = cam.DragDelta(p.X - _press.X, p.Y - _press.Y, at);
            _session.SetVector(item, 0, _startPosition + delta);
        }
        else if (_session.Selected is { } turn && _rotateItem)
            _session.SetVector(turn, 3, _startRotation + new Vec3((p.X - _press.X) * 0.5, (p.Y - _press.Y) * 0.5, 0));
        _last = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (!_moved && !_pan) _session.Select(Pick(p));
        _pan = _orbit = _dragItem = _rotateItem = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_mode == LayoutViewMode.Camera) return;
        var p = e.GetPosition(this);
        Camera.Zoom(Math.Pow(1.15, e.Delta.Y), p.X, p.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>The item under a point: the nearest light, camera, null or bone within a few pixels, else the front-most object.</summary>
    public SceneItem? Pick(Point p)
    {
        var cam = Camera;
        var frame = _session.Scene.Evaluate(_session.Frame);
        SceneItem? best = null;
        double bestD = 12;
        foreach (var (item, m) in frame.World)
        {
            if (item is ObjectItem) continue;
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
