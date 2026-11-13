using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>How the model's outline compares with a reference picture's.</summary>
public sealed record SilhouetteComparison(
    double Overlap, double Missing, double Extra, string MissingWhere, string ExtraWhere, int Width, int Height, byte[] Picture)
{
    /// <summary>A sentence on what to change.</summary>
    public string Advice =>
        Overlap >= 0.97 ? "The outlines match closely."
        : Missing >= Extra
            ? $"The model is short of the reference mostly at the {MissingWhere} ({Missing:P0} of the reference isn't covered); {(Extra > 0.02 ? $"it also sticks out at the {ExtraWhere} ({Extra:P0})." : "little sticks out.")}"
            : $"The model sticks out past the reference mostly at the {ExtraWhere} ({Extra:P0} of the model); {(Missing > 0.02 ? $"it's also short at the {MissingWhere} ({Missing:P0})." : "little is missing.")}";
}

/// <summary>
/// Outlines: the model's, drawn as a mask by projecting its faces into a picture, and a reference picture's (what
/// differs from its background, or what isn't transparent), compared by how much they overlap.
/// </summary>
public static class Silhouettes
{
    /// <summary>The pixels a mesh's faces cover, projected by <paramref name="toPixel"/> (x right, y down; null: not seen).</summary>
    public static bool[] Model(Mesh mesh, int width, int height, Func<Vec3, Vec2?> toPixel)
    {
        var mask = new bool[width * height];
        var projected = mesh.Points.Select(toPixel).ToArray();
        foreach (var poly in mesh.Polygons)
        {
            if (poly.Count < 3) continue;
            for (int k = 1; k + 1 < poly.Count; k++)
            {
                if (projected[poly.Points[0]] is not { } a || projected[poly.Points[k]] is not { } b || projected[poly.Points[k + 1]] is not { } c) continue;
                Fill(mask, width, height, a, b, c);
            }
        }
        return mask;
    }

    private static void Fill(bool[] mask, int w, int h, Vec2 a, Vec2 b, Vec2 c)
    {
        double area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (Math.Abs(area) < 1e-12) return;
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))), x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))), y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double w0 = ((b.X - px) * (c.Y - py) - (c.X - px) * (b.Y - py)) / area;
                double w1 = ((c.X - px) * (a.Y - py) - (a.X - px) * (c.Y - py)) / area;
                double w2 = 1 - w0 - w1;
                if (w0 >= -1e-9 && w1 >= -1e-9 && w2 >= -1e-9) mask[y * w + x] = true;
            }
    }

    /// <summary>
    /// A reference picture's subject: where it's opaque, if it has transparency; otherwise where its colour differs from
    /// the background (its corners' average) by more than <paramref name="threshold"/> (0 to 1).
    /// </summary>
    public static bool[] Reference(TextureImage img, double threshold = 0.15)
    {
        int w = img.Width, h = img.Height;
        var mask = new bool[w * h];
        bool alpha = false;
        for (int i = 3; i < img.Rgba.Length; i += 4) if (img.Rgba[i] < 250) { alpha = true; break; }
        if (alpha)
        {
            for (int i = 0; i < w * h; i++) mask[i] = img.Rgba[i * 4 + 3] >= 128;
            return mask;
        }
        var bg = Vec3.Zero;
        int n = 0;
        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
            for (int dy = 0; dy < Math.Min(3, h); dy++)
                for (int dx = 0; dx < Math.Min(3, w); dx++)
                {
                    int xx = Math.Clamp(x + (x == 0 ? dx : -dx), 0, w - 1), yy = Math.Clamp(y + (y == 0 ? dy : -dy), 0, h - 1);
                    bg += img.Pixel(xx, yy).Color;
                    n++;
                }
        bg /= n;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = img.Pixel(x, y).Color;
                mask[y * w + x] = Math.Max(Math.Abs(c.X - bg.X), Math.Max(Math.Abs(c.Y - bg.Y), Math.Abs(c.Z - bg.Z))) > threshold;
            }
        return mask;
    }

    /// <summary>The bounds of a mask's set pixels (empty: null).</summary>
    public static (int X0, int Y0, int X1, int Y1)? Bounds(bool[] mask, int w, int h)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x]) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        return x1 < 0 ? null : (x0, y0, x1, y1);
    }

    /// <summary>
    /// How two masks overlap: intersection over union, the share of the reference the model misses, the share of the
    /// model outside the reference, where those mostly are, and a picture: both grey, the model only red, the
    /// reference only blue.
    /// </summary>
    public static SilhouetteComparison Compare(bool[] model, bool[] reference, int w, int h)
    {
        int both = 0, onlyModel = 0, onlyRef = 0;
        double mx = 0, my = 0, ex = 0, ey = 0;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                (byte r, byte g, byte b) = (255, 255, 255);
                if (model[i] && reference[i]) { both++; (r, g, b) = (150, 150, 150); }
                else if (model[i]) { onlyModel++; ex += x; ey += y; (r, g, b) = (220, 40, 40); }
                else if (reference[i]) { onlyRef++; mx += x; my += y; (r, g, b) = (40, 80, 220); }
                rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255;
            }
        int union = both + onlyModel + onlyRef;
        var box = Bounds(reference, w, h) ?? (0, 0, w - 1, h - 1);
        string Where(double sx, double sy, int count)
        {
            if (count == 0) return "nowhere";
            double fx = (sx / count - box.X0) / Math.Max(1, box.X1 - box.X0), fy = (sy / count - box.Y0) / Math.Max(1, box.Y1 - box.Y0);
            string v = fy < 0.33 ? "top" : fy > 0.67 ? "bottom" : "middle";
            string hz = fx < 0.33 ? "left" : fx > 0.67 ? "right" : "centre";
            return v == "middle" && hz == "centre" ? "centre" : v == "middle" ? $"{hz} side" : hz == "centre" ? v : $"{v} {hz}";
        }
        return new SilhouetteComparison(
            union == 0 ? 0 : (double)both / union,
            both + onlyRef == 0 ? 0 : (double)onlyRef / (both + onlyRef),
            both + onlyModel == 0 ? 0 : (double)onlyModel / (both + onlyModel),
            Where(mx, my, onlyRef), Where(ex, ey, onlyModel), w, h, rgba);
    }
}
