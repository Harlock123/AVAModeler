using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Slicing;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace AVAModeler.Modeler.UI;

/// <summary>
/// The sliced print in 3D, as the layers stack up to the one chosen: the bed and its 10 mm grid, and each layer's
/// lines in their kinds' colours, drawn by the Modeler's software renderer as overlay lines (bottom layer first, so
/// higher ones are drawn over lower ones, as they'd be seen from above). The chosen layer is bright and the ones
/// under it a little darker; far down only their outer walls are drawn, to keep a tall print quick. Drag to turn it,
/// drag with the right or middle button (or Shift) to move it, and scroll to zoom.
/// </summary>
public sealed class PrintPreview3D : Control
{
    private readonly SoftwareRenderer _renderer = new();
    private readonly ViewCamera _camera = new(ViewKind.Perspective) { Heading = 30, Pitch = 35 };
    private WriteableBitmap? _bitmap;
    private PrintJob? _job;
    private int _layer;
    private Point? _drag;
    private bool _panning;
    private bool _framed;

    /// <summary>Layers below the chosen one by more than this show only their outer walls.</summary>
    public const int FullLayers = 40;

    public PrintJob? Job
    {
        get => _job;
        set { _job = value; _framed = false; InvalidateVisual(); }
    }

    public int Layer
    {
        get => _layer;
        set { _layer = value; InvalidateVisual(); }
    }

    public ViewCamera Camera => _camera;

    /// <summary>How many line segments the last picture drew (for tests).</summary>
    public int Segments { get; private set; }

    /// <summary>The bed's millimetres (X right, Y back, Z up) as the renderer's metres (X right, Y up, Z away).</summary>
    public static Vec3 ToScene(double x, double y, double z) => new(x / 1000, z / 1000, y / 1000);

    /// <summary>The overlay lines for the print up to layer <paramref name="upTo"/>, with the bed.</summary>
    public static List<OverlayLine> Lines(PrintJob? job, int upTo, PrinterProfile printer)
    {
        var lines = new List<OverlayLine>();
        const uint grid = 0xFF4A4A52, edge = 0xFF8C8C96;
        if (printer.RoundBed)
        {
            // A delta's round bed: its rim, and the grid inside it.
            double r = printer.BedWidth / 2;
            for (int i = 0; i < 96; i++)
            {
                double a0 = 2 * Math.PI * i / 96, a1 = 2 * Math.PI * (i + 1) / 96;
                lines.Add(new(ToScene(r + r * Math.Cos(a0), r + r * Math.Sin(a0), 0), ToScene(r + r * Math.Cos(a1), r + r * Math.Sin(a1), 0), edge));
            }
            for (double x = 10; x < printer.BedWidth; x += 10)
            {
                double half = Math.Sqrt(Math.Max(0, r * r - (x - r) * (x - r)));
                lines.Add(new(ToScene(x, r - half, 0), ToScene(x, r + half, 0), grid));
                lines.Add(new(ToScene(r - half, x, 0), ToScene(r + half, x, 0), grid));
            }
        }
        else
        {
            for (double x = 10; x < printer.BedWidth; x += 10) lines.Add(new(ToScene(x, 0, 0), ToScene(x, printer.BedDepth, 0), grid));
            for (double y = 10; y < printer.BedDepth; y += 10) lines.Add(new(ToScene(0, y, 0), ToScene(printer.BedWidth, y, 0), grid));
            Vec3[] corners = [ToScene(0, 0, 0), ToScene(printer.BedWidth, 0, 0), ToScene(printer.BedWidth, printer.BedDepth, 0), ToScene(0, printer.BedDepth, 0)];
            for (int i = 0; i < 4; i++) lines.Add(new(corners[i], corners[(i + 1) % 4], edge));
        }
        if (job == null || job.Plan.Layers.Count == 0) return lines;
        int top = Math.Clamp(upTo, 0, job.Plan.Layers.Count - 1);
        for (int i = 0; i <= top; i++)
        {
            var layer = job.Plan.Layers[i];
            bool full = top - i <= FullLayers;
            double z = layer.Layer.Top;
            foreach (var path in layer.Paths)
            {
                if (!full && path.Kind != PathKind.OuterWall) continue;
                uint colour = Colour(path.Kind, i == top ? 1.0 : 0.62);
                var pts = path.Points;
                for (int k = 1; k < pts.Count; k++) lines.Add(new(ToScene(pts[k - 1].X, pts[k - 1].Y, z), ToScene(pts[k].X, pts[k].Y, z), colour));
                if (path.Closed && pts.Count > 2) lines.Add(new(ToScene(pts[^1].X, pts[^1].Y, z), ToScene(pts[0].X, pts[0].Y, z), colour));
            }
        }
        return lines;
    }

    /// <summary>A kind's colour (as the top view has it) as 0xAARRGGBB, at a brightness.</summary>
    public static uint Colour(PathKind kind, double brightness)
    {
        var c = ((ISolidColorBrush)LayerPreview.BrushFor(kind)).Color;
        uint Part(byte v) => (uint)Math.Clamp(Math.Round(v * brightness), 0, 255);
        return 0xFF000000 | Part(c.R) << 16 | Part(c.G) << 8 | Part(c.B);
    }

    public override void Render(DrawingContext context)
    {
        int w = Math.Max(1, (int)Bounds.Width), h = Math.Max(1, (int)Bounds.Height);
        _camera.Resize(w, h);
        var printer = _job?.Setup.Printer ?? new PrinterProfile();
        if (!_framed)
        {
            var b = _job is { Model.Bounds.IsEmpty: false } j ? j.Model.Bounds : new Box3(new Vec3(0, 0, 0), new Vec3(printer.BedWidth, printer.BedDepth, 0));
            _camera.Fit(new Box3(ToScene(b.Min.X, b.Min.Y, b.Min.Z), ToScene(b.Max.X, b.Max.Y, b.Max.Z)));
            _framed = true;
        }
        var scene = new ViewScene { ShowGrid = false, Overlay = Lines(_job, _layer, printer) };
        Segments = scene.Overlay.Count;
        _renderer.Render(scene, _camera);
        if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var fb = _bitmap.Lock())
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy((int[])(object)_renderer.Pixels, y * w, fb.Address + y * fb.RowBytes, w);
        context.DrawImage(_bitmap, new Rect(0, 0, w, h));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        _drag = e.GetPosition(this);
        _panning = props.IsRightButtonPressed || props.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } from) return;
        var at = e.GetPosition(this);
        var d = at - from;
        if (_panning) _camera.Pan(d.X, d.Y);
        else _camera.Orbit(d.X * 0.4, d.Y * 0.4);
        _drag = at;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var at = e.GetPosition(this);
        _camera.Zoom(Math.Pow(1.15, e.Delta.Y), at.X, at.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Frames the print again, as when it was sliced.</summary>
    public void Refit()
    {
        _framed = false;
        InvalidateVisual();
    }
}
