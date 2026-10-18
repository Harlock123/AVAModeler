using AVAModeler.Core;

namespace AVAModeler.Rendering;

/// <summary>
/// Draws a view. Implementations: <see cref="SoftwareRenderer"/> into a pixel buffer (works everywhere, and in
/// tests), and the OpenGL renderer into the current GL context. Both draw the same <see cref="ViewScene"/> through
/// the same <see cref="ViewCamera"/>.
/// </summary>
public interface IRenderer : IDisposable
{
    string Name { get; }
    void Render(ViewScene scene, ViewCamera camera);
}

/// <summary>
/// The software renderer: a z-buffered triangle rasterizer for the shaded view and anti-aliased lines for the
/// wireframe, into a 32-bit BGRA pixel buffer (0xAARRGGBB words) the UI shows as a bitmap.
/// </summary>
public sealed class SoftwareRenderer : IRenderer
{
    public string Name => "Software";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] Pixels { get; private set; } = [];
    private float[] _depth = [];
    private ViewCamera _cam = new(ViewKind.Front);

    public void Dispose() { }

    private void Size(int w, int h)
    {
        if (w == Width && h == Height && Pixels.Length == w * h) return;
        Width = w;
        Height = h;
        Pixels = new uint[w * h];
        _depth = new float[w * h];
    }

    public void Render(ViewScene scene, ViewCamera camera)
    {
        _cam = camera;
        Size(camera.Width, camera.Height);
        Array.Fill(Pixels, camera.IsPerspective ? Palette.PerspectiveBackground : Palette.Background);
        Array.Fill(_depth, float.PositiveInfinity);
        if (scene.ShowGrid) DrawGrid();

        bool shaded = camera.IsPerspective && scene.Style != DisplayStyle.Wireframe;
        // The background layers first, dimmed.
        foreach (var layer in scene.Layers.Where(l => l.Background))
            DrawWire(layer.Mesh, Palette.BackgroundWire, depthTest: shaded);
        foreach (var layer in scene.Layers.Where(l => !l.Background))
        {
            var mesh = layer.Mesh;
            if (shaded) Shade(layer.Subdivided ?? mesh, scene, layer.Subdivided == null ? layer.SelectedPolygons : null);
            bool wire = !shaded || scene.Style == DisplayStyle.ShadedWireframe || layer.Subdivided != null;
            if (wire) DrawWire(mesh, layer.Subdivided != null ? Palette.Cage : Palette.Wire, depthTest: shaded && layer.Subdivided == null);
            if (layer.Subdivided != null && !shaded) DrawWire(layer.Subdivided, Palette.Wire, false);
            DrawSelection(layer);
            DrawSinglePoints(mesh);
            if (scene.ShowPoints) DrawPoints(mesh, layer.SelectedPoints, all: true);
            else if (layer.SelectedPoints is { Count: > 0 }) DrawPoints(mesh, layer.SelectedPoints, all: false);
        }
        foreach (var line in scene.Overlay) Line3(line.A, line.B, line.Color, false, 1.5);
    }

    // ───────────────────────────── the grid ─────────────────────────────

    private void DrawGrid()
    {
        double step = _cam.GridStep();
        if (_cam.IsPerspective)
        {
            // A floor grid of 20 × 20 steps round the target, on y = 0.
            double s = _cam.GridStep(30);
            double cx = Math.Round(_cam.Center.X / s) * s, cz = Math.Round(_cam.Center.Z / s) * s;
            for (int i = -10; i <= 10; i++)
            {
                uint c = i == 0 ? Palette.Axis : Palette.Grid;
                Line3(new Vec3(cx + i * s, 0, cz - 10 * s), new Vec3(cx + i * s, 0, cz + 10 * s), Math.Abs(cx + i * s) < s / 2 ? Palette.Axis : c, false, 1);
                Line3(new Vec3(cx - 10 * s, 0, cz + i * s), new Vec3(cx + 10 * s, 0, cz + i * s), Math.Abs(cz + i * s) < s / 2 ? Palette.Axis : c, false, 1);
            }
            return;
        }
        var (r, u, _) = _cam.Basis();
        // World coordinates at the screen's edges along the view's right and up axes.
        double cr = Vec3.Dot(_cam.Center, r), cu = Vec3.Dot(_cam.Center, u);
        double halfW = Width / 2.0 / _cam.PixelsPerUnit, halfH = Height / 2.0 / _cam.PixelsPerUnit;
        for (double v = Math.Floor((cr - halfW) / step) * step; v <= cr + halfW; v += step)
        {
            double x = Width / 2.0 + (v - cr) * _cam.PixelsPerUnit;
            long n = (long)Math.Round(v / step);
            uint c = Math.Abs(v) < step / 2 ? Palette.Axis : n % 10 == 0 ? Palette.GridMajor : Palette.Grid;
            VLine((int)Math.Round(x), c);
        }
        for (double v = Math.Floor((cu - halfH) / step) * step; v <= cu + halfH; v += step)
        {
            double y = Height / 2.0 - (v - cu) * _cam.PixelsPerUnit;
            long n = (long)Math.Round(v / step);
            uint c = Math.Abs(v) < step / 2 ? Palette.Axis : n % 10 == 0 ? Palette.GridMajor : Palette.Grid;
            HLine((int)Math.Round(y), c);
        }
    }

    private void VLine(int x, uint c)
    {
        if (x < 0 || x >= Width) return;
        for (int y = 0; y < Height; y++) Pixels[y * Width + x] = c;
    }

    private void HLine(int y, uint c)
    {
        if (y < 0 || y >= Height) return;
        Array.Fill(Pixels, c, y * Width, Width);
    }

    // ───────────────────────────── shaded faces ─────────────────────────────

    private void Shade(Mesh mesh, ViewScene scene, IReadOnlySet<int>? selected)
    {
        var tris = Tessellation.Of(mesh);
        var proj = new ScreenPoint[mesh.Points.Count];
        for (int i = 0; i < proj.Length; i++) proj[i] = _cam.Project(mesh.Points[i]);
        var light = Shading.Light(_cam);
        var normals = new Vec3[mesh.Polygons.Count];
        for (int i = 0; i < normals.Length; i++) normals[i] = mesh.Normal(i);
        var corners = Shading.CornerNormals(mesh, scene.Surfaces);
        foreach (var t in tris)
        {
            ScreenPoint a = proj[t.A], b = proj[t.B], c = proj[t.C];
            if (!a.Visible || !b.Visible || !c.Visible) continue;
            var poly = mesh.Polygons[t.Polygon];
            var surface = Shading.SurfaceOf(scene, poly);
            bool sel = selected != null && selected.Contains(t.Polygon);
            if (surface is { Smoothing: true })
            {
                Vec3 Col(int point) => Shading.FaceColor(normals[t.Polygon], mesh.Points[point], surface, _cam, light, scene.BackfaceTint, sel, Shading.Corner(corners, mesh, t.Polygon, point));
                FillTriangle(a, b, c, Col(t.A), Col(t.B), Col(t.C));
            }
            else
            {
                var col = Shading.FaceColor(normals[t.Polygon], mesh.Points[t.A], surface, _cam, light, scene.BackfaceTint, sel);
                FillTriangle(a, b, c, col, col, col);
            }
        }
    }

    private static uint Pack(Vec3 c) =>
        0xFF000000u | (uint)Math.Clamp(c.X * 255, 0, 255) << 16 | (uint)Math.Clamp(c.Y * 255, 0, 255) << 8 | (uint)Math.Clamp(c.Z * 255, 0, 255);

    /// <summary>A z-buffered triangle, its colour blended across it from the corners' (Gouraud shading).</summary>
    private void FillTriangle(ScreenPoint a, ScreenPoint b, ScreenPoint c, Vec3 ca, Vec3 cb, Vec3 cc)
    {
        double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(area) < 1e-9) return;
        int minX = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        int maxX = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int minY = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        int maxY = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY) return;
        double inv = 1 / area;
        bool flat = ca == cb && cb == cc;
        uint flatColor = Pack(ca);
        // Depth interpolated as 1/z, which is linear across the screen in perspective.
        double za = 1 / Math.Max(a.Depth, 1e-9), zb = 1 / Math.Max(b.Depth, 1e-9), zc = 1 / Math.Max(c.Depth, 1e-9);
        bool persp = _cam.IsPerspective;
        for (int y = minY; y <= maxY; y++)
        {
            double py = y + 0.5;
            int row = y * Width;
            for (int x = minX; x <= maxX; x++)
            {
                double px = x + 0.5;
                double w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) * inv;
                double w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) * inv;
                double w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                double depth = persp ? 1 / (w0 * za + w1 * zb + w2 * zc) : w0 * a.Depth + w1 * b.Depth + w2 * c.Depth;
                float d = (float)depth;
                if (d >= _depth[row + x]) continue;
                _depth[row + x] = d;
                Pixels[row + x] = flat ? flatColor : Pack(ca * w0 + cb * w1 + cc * w2);
            }
        }
    }

    // ───────────────────────────── lines and points ─────────────────────────────

    private void DrawWire(Mesh mesh, uint color, bool depthTest)
    {
        var proj = new ScreenPoint[mesh.Points.Count];
        for (int i = 0; i < proj.Length; i++) proj[i] = _cam.Project(mesh.Points[i]);
        foreach (var e in mesh.EdgeMap().Keys)
            Segment(mesh.Points[e.A], mesh.Points[e.B], proj[e.A], proj[e.B], color, depthTest, 1);
    }

    private void DrawSelection(ViewLayer layer)
    {
        var mesh = layer.Mesh;
        if (layer.SelectedPolygons is { Count: > 0 } polys)
        {
            foreach (int pi in polys)
            {
                if (pi < 0 || pi >= mesh.Polygons.Count) continue;
                var p = mesh.Polygons[pi];
                if (p.Count == 1) { Dot(_cam.Project(mesh.Points[p.Points[0]]), Palette.Selected, 3); continue; }
                foreach (var (a, b) in p.Edges()) Line3(mesh.Points[a], mesh.Points[b], Palette.Selected, false, 1.6);
            }
        }
        if (layer.SelectedEdges is { Count: > 0 } edges)
            foreach (var e in edges)
                if (e.A < mesh.Points.Count && e.B < mesh.Points.Count)
                    Line3(mesh.Points[e.A], mesh.Points[e.B], Palette.Selected, false, 2.2);
    }

    /// <summary>One-point polygons are drawn as dots, always.</summary>
    private void DrawSinglePoints(Mesh mesh)
    {
        foreach (var p in mesh.Polygons)
            if (p.Count == 1 && p.Points[0] < mesh.Points.Count) Dot(_cam.Project(mesh.Points[p.Points[0]]), Palette.Wire, 2);
    }

    private void DrawPoints(Mesh mesh, IReadOnlySet<int>? selected, bool all)
    {
        if (all)
            for (int i = 0; i < mesh.Points.Count; i++)
                if (selected == null || !selected.Contains(i)) Dot(_cam.Project(mesh.Points[i]), Palette.Point, 1.5);
        if (selected == null) return;
        foreach (int i in selected)
            if (i >= 0 && i < mesh.Points.Count) Dot(_cam.Project(mesh.Points[i]), Palette.Selected, 3);
    }

    private void Dot(ScreenPoint p, uint color, double radius)
    {
        if (!p.Visible) return;
        int r = (int)Math.Ceiling(radius);
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            int x = (int)Math.Floor(p.X) + dx, y = (int)Math.Floor(p.Y) + dy;
            if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
            double d = Math.Sqrt((x + 0.5 - p.X) * (x + 0.5 - p.X) + (y + 0.5 - p.Y) * (y + 0.5 - p.Y));
            double cover = Math.Clamp(radius + 0.5 - d, 0, 1);
            if (cover > 0) Blend(y * Width + x, color, cover);
        }
    }

    /// <summary>A world-space line, clipped to the near plane in perspective.</summary>
    private void Line3(Vec3 a, Vec3 b, uint color, bool depthTest, double width)
    {
        Segment(a, b, _cam.Project(a), _cam.Project(b), color, depthTest, width);
    }

    private void Segment(Vec3 wa, Vec3 wb, ScreenPoint a, ScreenPoint b, uint color, bool depthTest, double width)
    {
        if (!a.Visible || !b.Visible)
        {
            if (!_cam.IsPerspective || !a.Visible && !b.Visible) return;
            // Clip the hidden end to just in front of the near plane.
            var f = _cam.Basis().Forward;
            double da = Vec3.Dot(wa - _cam.Eye, f), db = Vec3.Dot(wb - _cam.Eye, f), near = _cam.Near * 1.01;
            var cut = Vec3.Lerp(wa, wb, (near - da) / (db - da));
            if (!a.Visible) a = _cam.Project(cut); else b = _cam.Project(cut);
            if (!a.Visible || !b.Visible) return;
        }
        WuLine(a, b, color, depthTest, width);
    }

    /// <summary>An anti-aliased line of any width, by how much of each pixel the line's band covers.</summary>
    private void WuLine(ScreenPoint a, ScreenPoint b, uint color, bool depthTest, double width)
    {
        double x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;
        // Skip what's wholly off screen; clamp absurd coordinates so the loop stays bounded.
        if (Math.Max(x0, x1) < -2 || Math.Min(x0, x1) > Width + 2 || Math.Max(y0, y1) < -2 || Math.Min(y0, y1) > Height + 2) return;
        if (!ClipToScreen(ref x0, ref y0, ref x1, ref y1, out double t0, out double t1)) return;
        double d0 = a.Depth + (b.Depth - a.Depth) * t0, d1 = a.Depth + (b.Depth - a.Depth) * t1;
        bool steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
        if (steep) { (x0, y0) = (y0, x0); (x1, y1) = (y1, x1); }
        if (x0 > x1) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); (d0, d1) = (d1, d0); }
        double dx = x1 - x0, gradient = dx < 1e-9 ? 0 : (y1 - y0) / dx;
        // Each column covers the rows the line's band [centre - width/2, centre + width/2] overlaps, by how much.
        double half = width / 2 * Math.Sqrt(1 + gradient * gradient);
        int xs = (int)Math.Round(x0), xe = (int)Math.Round(x1);
        for (int x = xs; x <= xe; x++)
        {
            double t = dx < 1e-9 ? 0 : Math.Clamp((x + 0.5 - x0) / dx, 0, 1);
            double c = y0 + gradient * (x + 0.5 - x0);
            float depth = (float)(d0 + (d1 - d0) * t);
            double lo = c - half, hi = c + half;
            for (int y = (int)Math.Floor(lo); y <= (int)Math.Floor(hi); y++)
                Plot(steep, x, y, color, Math.Min(y + 1, hi) - Math.Max(y, lo), depthTest, depth);
        }
    }

    /// <summary>Liang–Barsky clip of a segment to the screen, giving the kept part's fractions along it.</summary>
    private bool ClipToScreen(ref double x0, ref double y0, ref double x1, ref double y1, out double t0, out double t1)
    {
        t0 = 0;
        t1 = 1;
        double dx = x1 - x0, dy = y1 - y0;
        double[] p = [-dx, dx, -dy, dy];
        double[] q = [x0 + 1, Width + 1 - x0, y0 + 1, Height + 1 - y0];
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-12) { if (q[i] < 0) return false; continue; }
            double r = q[i] / p[i];
            if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
        }
        double ox = x0, oy = y0;
        x0 = ox + dx * t0;
        y0 = oy + dy * t0;
        x1 = ox + dx * t1;
        y1 = oy + dy * t1;
        return true;
    }

    private void Plot(bool steep, int x, int y, uint color, double cover, bool depthTest, float depth)
    {
        if (steep) (x, y) = (y, x);
        if (x < 0 || y < 0 || x >= Width || y >= Height || cover <= 0.004) return;
        int i = y * Width + x;
        // Lines sit a hair in front of the faces they edge.
        if (depthTest && depth > _depth[i] * 1.002f + 1e-4f) return;
        Blend(i, color, cover);
    }

    private void Blend(int i, uint color, double a)
    {
        uint dst = Pixels[i];
        a = Math.Clamp(a, 0, 1);
        uint r = (uint)(((color >> 16) & 0xFF) * a + ((dst >> 16) & 0xFF) * (1 - a));
        uint g = (uint)(((color >> 8) & 0xFF) * a + ((dst >> 8) & 0xFF) * (1 - a));
        uint b = (uint)((color & 0xFF) * a + (dst & 0xFF) * (1 - a));
        Pixels[i] = 0xFF000000u | r << 16 | g << 8 | b;
    }
}
