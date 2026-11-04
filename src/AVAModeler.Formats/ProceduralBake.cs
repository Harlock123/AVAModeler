using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Formats;

/// <summary>
/// A surface's colour procedurals baked into an image, for formats that can only carry pictures (glTF's base colour
/// texture): the surface's colour with every colour procedural layer laid over it, worked out at the 3D point each
/// pixel stands for, and the UVs that lay the picture back on the faces.
/// <para>
/// Where every face of the surface is mapped in one UV map, the picture is that map's layout: each triangle is drawn
/// into it with the procedurals at the points across it, exactly. Otherwise it is a box map: an atlas of six cells,
/// 3 × 2, one for each side of the surface's bounding box (+X, +Y, +Z along the top, −X, −Y, −Z below), each face
/// taking the cell its normal points to most and the procedurals worked out on that side of the box. That is exact
/// for faces lying on their bounding box (a floor, a box) and an approximation for curved ones.
/// </para>
/// Pixels no face covers take the surface's own colour, so filtering at the edges doesn't bleed in black.
/// </summary>
public sealed class ProceduralBake
{
    /// <summary>The picture: <see cref="Width"/> × <see cref="Height"/>, RGBA, top row first.</summary>
    public required byte[] Rgba { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Each polygon's corners' coordinates in the picture (u across, v up), by polygon index.</summary>
    public required Dictionary<int, Vec2[]> CornerUVs { get; init; }
    /// <summary>The UV map the picture follows, or null for the box map.</summary>
    public string? UVMap { get; init; }

    /// <summary>The box map's cells' size in pixels (the atlas is 3 × 2 of them).</summary>
    public const int Cell = 256;

    /// <summary>The size of a UV-map bake.</summary>
    public const int MapSize = 512;

    /// <summary>A surface's colour procedurals (those this draws, with some opacity), or none.</summary>
    public static List<(TextureLayer Layer, ProceduralTexture Texture)> ColourLayers(Surface s) =>
        s.TexturesOf(TextureChannel.Color).Where(t => t.Opacity > 0).Select(t => (t, Procedurals.Prepare(t)))
            .Where(x => x.Item2 != null).Select(x => (x.t, x.Item2!)).ToList();

    /// <summary>The colour at a point: the surface's own with each procedural laid over it, by its amount and blend.</summary>
    public static Vec3 ColourAt(Vec3 color, IReadOnlyList<(TextureLayer Layer, ProceduralTexture Texture)> layers, Vec3 point)
    {
        foreach (var (t, p) in layers)
            color = TextureMapping.Blend(color, p.Color, Math.Clamp(t.Opacity, 0, 1) * p.Amount(point), t.Blend);
        return new Vec3(Math.Clamp(color.X, 0, 1), Math.Clamp(color.Y, 0, 1), Math.Clamp(color.Z, 0, 1));
    }

    /// <summary>
    /// Bakes a surface's colour procedurals for some of a mesh's polygons (its faces of that surface), or null if it
    /// has none.
    /// </summary>
    public static ProceduralBake? Of(Surface surface, Mesh mesh, IReadOnlyList<int> polygons)
    {
        var layers = ColourLayers(surface);
        if (layers.Count == 0 || polygons.Count == 0) return null;
        var baseColor = surface.Color.ToVec();
        string? map = mesh.UVMaps.Select(m => m.Name).FirstOrDefault(m => polygons.All(p => mesh.IsMapped(m, mesh.Polygons[p])));
        return map != null ? FromMap(mesh, polygons, map, baseColor, layers) : BoxMap(mesh, polygons, baseColor, layers);
    }

    private static byte[] Filled(int width, int height, Vec3 color)
    {
        var rgba = new byte[width * height * 4];
        byte r = B(color.X), g = B(color.Y), b = B(color.Z);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    private static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private static void Put(byte[] rgba, int width, int x, int y, Vec3 c)
    {
        int i = (y * width + x) * 4;
        rgba[i] = B(c.X);
        rgba[i + 1] = B(c.Y);
        rgba[i + 2] = B(c.Z);
        rgba[i + 3] = 255;
    }

    /// <summary>The UV map's layout, each triangle drawn into it (a pixel's centre inside it, or within half a pixel of its edge).</summary>
    private static ProceduralBake FromMap(Mesh mesh, IReadOnlyList<int> polygons, string map, Vec3 baseColor,
        IReadOnlyList<(TextureLayer, ProceduralTexture)> layers)
    {
        const int n = MapSize;
        var rgba = Filled(n, n, baseColor);
        var corners = new Dictionary<int, Vec2[]>();
        foreach (int pi in polygons)
        {
            var poly = mesh.Polygons[pi];
            var uv = new Vec2[poly.Count];
            for (int c = 0; c < poly.Count; c++) mesh.TryGetUV(map, poly, poly.Points[c], out uv[c]);
            corners[pi] = uv;
            var tris = Triangulator.TriangulatePolygon(mesh, poly);
            for (int k = 0; k + 2 < tris.Length; k += 3)
            {
                int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                // Pixel space: x across, y down from the top row.
                Vec2 P(int i) => new(uv[i].X * n, (1 - uv[i].Y) * n);
                Vec2 pa = P(a), pb = P(b), pc = P(c);
                double area = Vec2.Cross(pb - pa, pc - pa);
                if (Math.Abs(area) < 1e-12) continue;
                Vec3 A = mesh.Points[poly.Points[a]], Bp = mesh.Points[poly.Points[b]], C = mesh.Points[poly.Points[c]];
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(pa.X, Math.Min(pb.X, pc.X)) - 1)), x1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(pa.X, Math.Max(pb.X, pc.X)) + 1));
                int y0 = Math.Max(0, (int)Math.Floor(Math.Min(pa.Y, Math.Min(pb.Y, pc.Y)) - 1)), y1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(pa.Y, Math.Max(pb.Y, pc.Y)) + 1));
                // Half a pixel's slack along each edge, in barycentric terms, so neighbouring triangles leave no gap.
                double slack = 0.5 * Math.Max(Math.Max((pb - pa).Length, (pc - pb).Length), (pa - pc).Length) / Math.Abs(area);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var q = new Vec2(x + 0.5, y + 0.5);
                        double wb = Vec2.Cross(q - pa, pc - pa) / Vec2.Cross(pb - pa, pc - pa);
                        double wc = Vec2.Cross(pb - pa, q - pa) / Vec2.Cross(pb - pa, pc - pa);
                        double wa = 1 - wb - wc;
                        if (wa < -slack || wb < -slack || wc < -slack) continue;
                        Put(rgba, n, x, y, ColourAt(baseColor, layers, A * wa + Bp * wb + C * wc));
                    }
            }
        }
        return new ProceduralBake { Rgba = rgba, Width = n, Height = n, CornerUVs = corners, UVMap = map };
    }

    /// <summary>The cell a side of the box is drawn in: +X, +Y, +Z along the top row; −X, −Y, −Z below.</summary>
    private static (int Column, int Row) CellOf(Axis axis, bool positive) => ((int)axis, positive ? 0 : 1);

    /// <summary>The box map: six cells, one per side of the surface's bounds, each a planar view of that side.</summary>
    private static ProceduralBake BoxMap(Mesh mesh, IReadOnlyList<int> polygons, Vec3 baseColor, IReadOnlyList<(TextureLayer, ProceduralTexture)> layers)
    {
        var bounds = Box3.Empty;
        foreach (int pi in polygons)
            foreach (int p in mesh.Polygons[pi].Points) bounds = bounds.Include(mesh.Points[p]);
        var (center, size) = TextureMapping.AutoSize(bounds);
        var min = center - size / 2;
        int width = 3 * Cell, height = 2 * Cell;
        var rgba = Filled(width, height, baseColor);
        // An inset of a pixel and a half keeps filtering inside each cell.
        const double inset = 1.5 / Cell;
        double Inside(double f) => inset + Math.Clamp(f, 0, 1) * (1 - 2 * inset);
        foreach (var axis in new[] { Axis.X, Axis.Y, Axis.Z })
        {
            var (right, up) = TextureMapping.ViewAxes(axis);
            foreach (bool positive in new[] { true, false })
            {
                var (col, row) = CellOf(axis, positive);
                double depth = positive ? bounds.Max[axis] : bounds.Min[axis];
                for (int y = 0; y < Cell; y++)
                    for (int x = 0; x < Cell; x++)
                    {
                        // The cell's pixel back to the side's point (the inverse of Inside).
                        double fu = ((x + 0.5) / Cell - inset) / (1 - 2 * inset), fv = 1 - ((y + 0.5) / Cell - inset) / (1 - 2 * inset);
                        var p = Point(axis, right, up, depth, min[right] + fu * size[right], min[up] + fv * size[up]);
                        Put(rgba, width, col * Cell + x, row * Cell + y, ColourAt(baseColor, layers, p));
                    }
            }
        }
        var corners = new Dictionary<int, Vec2[]>();
        foreach (int pi in polygons)
        {
            var poly = mesh.Polygons[pi];
            var n = poly.Count >= 3 ? mesh.Normal(pi) : Vec3.UnitY;
            var axis = TextureMapping.Dominant(n, Axis.Y);
            bool positive = n[axis] >= 0;
            var (col, row) = CellOf(axis, positive);
            var (right, up) = TextureMapping.ViewAxes(axis);
            var uv = new Vec2[poly.Count];
            for (int c = 0; c < poly.Count; c++)
            {
                var p = mesh.Points[poly.Points[c]];
                double u = Inside((p[right] - min[right]) / size[right]), v = Inside((p[up] - min[up]) / size[up]);
                // The atlas's v runs up from its bottom: the top row of cells is the upper half.
                uv[c] = new Vec2((col + u) / 3, (1 - row + v) / 2);
            }
            corners[pi] = uv;
        }
        return new ProceduralBake { Rgba = rgba, Width = width, Height = height, CornerUVs = corners };
    }

    private static Vec3 Point(Axis axis, Axis right, Axis up, double depth, double r, double u)
    {
        double x = 0, y = 0, z = 0;
        void Set(Axis a, double v)
        {
            if (a == Axis.X) x = v;
            else if (a == Axis.Y) y = v;
            else z = v;
        }
        Set(axis, depth);
        Set(right, r);
        Set(up, u);
        return new Vec3(x, y, z);
    }
}
