using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>A mark on a photo: a place on the model and where it shows in the picture (pixels, x right and y down).</summary>
public readonly record struct PhotoMark(Vec3 World, Vec2 Pixel);

/// <summary>
/// A photo's camera, solved from marks: its 3 × 4 projection (world to pixels), where it stands, which way it looks,
/// its focal length in pixels and vertical field of view, and how far the marks miss (root mean square, pixels).
/// </summary>
public sealed record CameraMatch(double[] P, Vec3 Position, Vec3 Forward, double FocalPixels, double VerticalFov, double Error, int Width, int Height)
{
    /// <summary>Where a place shows in the photo, in pixels (null behind the camera).</summary>
    public Vec2? Project(Vec3 p)
    {
        double x = P[0] * p.X + P[1] * p.Y + P[2] * p.Z + P[3];
        double y = P[4] * p.X + P[5] * p.Y + P[6] * p.Z + P[7];
        double w = P[8] * p.X + P[9] * p.Y + P[10] * p.Z + P[11];
        if (w <= 1e-12) return null;
        return new Vec2(x / w, y / w);
    }

    /// <summary>The ray from the camera through a pixel.</summary>
    public Ray RayThrough(Vec2 pixel)
    {
        // Solve M d = (x, y, 1) for the direction, M the projection's left 3 × 3.
        var m = new double[9] { P[0], P[1], P[2], P[4], P[5], P[6], P[8], P[9], P[10] };
        var d = Solve3(m, [pixel.X, pixel.Y, 1]);
        var dir = new Vec3(d[0], d[1], d[2]).Normalized();
        if (Vec3.Dot(dir, Forward) < 0) dir = -dir;
        return new Ray(Position, dir);
    }

    internal static double[] Solve3(double[] m, double[] b)
    {
        double det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
        if (Math.Abs(det) < 1e-300) return [0, 0, 0];
        double D(double[] c) => c[0] * (c[4] * c[8] - c[5] * c[7]) - c[1] * (c[3] * c[8] - c[5] * c[6]) + c[2] * (c[3] * c[7] - c[4] * c[6]);
        var r = new double[3];
        for (int k = 0; k < 3; k++)
        {
            var c = (double[])m.Clone();
            for (int row = 0; row < 3; row++) c[row * 3 + k] = b[row];
            r[k] = D(c) / det;
        }
        return r;
    }
}

/// <summary>
/// Matching photos to the model: a camera solved by the direct linear transform from six or more marks (normalised
/// as Hartley advises, the least-squares answer from the smallest eigenvector), and places found from two matched
/// photos by triangulation.
/// </summary>
public static class PhotoMatch
{
    public const int MinimumMarks = 6;

    public static CameraMatch Solve(IReadOnlyList<PhotoMark> marks, int width, int height)
    {
        if (marks.Count < MinimumMarks) throw new ArgumentException($"Matching a photo needs at least {MinimumMarks} marks (places on the model and where they show), not {marks.Count}.");
        if (width <= 0 || height <= 0) throw new ArgumentException("The photo needs a size.");
        // Normalise: world points about their centre to an average distance of √3, pixels to √2.
        var wc = marks.Aggregate(Vec3.Zero, (s, m) => s + m.World) / marks.Count;
        double ws = Math.Sqrt(3) / Math.Max(marks.Average(m => (m.World - wc).Length), 1e-12);
        var pc = marks.Aggregate(Vec2.Zero, (s, m) => s + m.Pixel) / marks.Count;
        double ps = Math.Sqrt(2) / Math.Max(marks.Average(m => (m.Pixel - pc).Length), 1e-12);
        var ata = new double[12, 12];
        foreach (var mk in marks)
        {
            var X = (mk.World - wc) * ws;
            var u = (mk.Pixel - pc) * ps;
            double[] r1 = [X.X, X.Y, X.Z, 1, 0, 0, 0, 0, -u.X * X.X, -u.X * X.Y, -u.X * X.Z, -u.X];
            double[] r2 = [0, 0, 0, 0, X.X, X.Y, X.Z, 1, -u.Y * X.X, -u.Y * X.Y, -u.Y * X.Z, -u.Y];
            for (int i = 0; i < 12; i++)
                for (int j = 0; j < 12; j++) ata[i, j] += r1[i] * r1[j] + r2[i] * r2[j];
        }
        var h = SmallestEigenvector(ata);
        if (CoplanarOrDegenerate(marks)) throw new ArgumentException("The marks all lie in one plane (or a line): mark places at different depths too, such as corners on two faces of a box.");
        // Undo the normalisation: P = Tp⁻¹ · P̃ · Tw.
        var P = new double[12];
        double[] tw = [ws, 0, 0, -ws * wc.X, 0, ws, 0, -ws * wc.Y, 0, 0, ws, -ws * wc.Z, 0, 0, 0, 1];
        double[] tpInv = [1 / ps, 0, pc.X, 0, 1 / ps, pc.Y, 0, 0, 1];
        var mid = new double[12];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += h[r * 4 + k] * tw[k * 4 + c];
                mid[r * 4 + c] = s;
            }
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += tpInv[r * 3 + k] * mid[k * 4 + c];
                P[r * 4 + c] = s;
            }
        // Scale so the third row's direction has length 1 and points the way the camera looks (marks in front).
        double n3 = Math.Sqrt(P[8] * P[8] + P[9] * P[9] + P[10] * P[10]);
        if (n3 < 1e-300) throw new ArgumentException("The marks don't pin a camera down: spread them over the picture and over the model.");
        double front = marks.Sum(m => P[8] * m.World.X + P[9] * m.World.Y + P[10] * m.World.Z + P[11]);
        double scale = (front < 0 ? -1 : 1) / n3;
        for (int i = 0; i < 12; i++) P[i] *= scale;
        var match = FromProjection(P, width, height, 0);
        double err = Math.Sqrt(marks.Average(m => match.Project(m.World) is { } q ? (q - m.Pixel).Length * (q - m.Pixel).Length : 1e12));
        return match with { Error = err };
    }

    /// <summary>A camera from its projection (scaled so its third row's direction has length 1, pointing ahead).</summary>
    public static CameraMatch FromProjection(double[] P, int width, int height, double error)
    {
        var m1 = new Vec3(P[0], P[1], P[2]);
        var m2 = new Vec3(P[4], P[5], P[6]);
        var m3 = new Vec3(P[8], P[9], P[10]);
        double u0 = Vec3.Dot(m1, m3), v0 = Vec3.Dot(m2, m3);
        double fx = (m1 - m3 * u0).Length, fy = (m2 - m3 * v0).Length;
        var centre = CameraMatch.Solve3([P[0], P[1], P[2], P[4], P[5], P[6], P[8], P[9], P[10]], [-P[3], -P[7], -P[11]]);
        return new CameraMatch(P, new Vec3(centre[0], centre[1], centre[2]), m3, (fx + fy) / 2, 2 * Math.Atan(height / 2.0 / Math.Max(fy, 1e-9)) * 180 / Math.PI, error, width, height);
    }

    /// <summary>A photo setup's camera, solved from its marks on the document's points (and kept on it), or null when it can't be.</summary>
    public static CameraMatch Solve(Document doc, PhotoSetup photo)
    {
        var marks = photo.Marks.Where(m => m.Layer >= 0 && m.Layer < doc.Layers.Count && m.Point >= 0 && m.Point < doc.Layers[m.Layer].Mesh.Points.Count)
            .Select(m => new PhotoMark(doc.Layers[m.Layer].Mesh.Points[m.Point], m.Pixel)).ToList();
        var cam = Solve(marks, photo.Width, photo.Height);
        photo.Projection = cam.P;
        photo.Error = cam.Error;
        return cam;
    }

    /// <summary>A photo setup's solved camera, or null when it hasn't one.</summary>
    public static CameraMatch? CameraOf(PhotoSetup photo) => photo.Projection is { Length: 12 } p ? FromProjection(p, photo.Width, photo.Height, photo.Error) : null;

    /// <summary>
    /// The place two matched photos see at two pixels: the point nearest both rays (least squares on the projections),
    /// and how far apart the rays pass there.
    /// </summary>
    public static (Vec3 Point, double Gap) Triangulate(CameraMatch a, Vec2 pa, CameraMatch b, Vec2 pb)
    {
        var ata = new double[4, 4];
        void Row(double[] r) { for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) ata[i, j] += r[i] * r[j]; }
        foreach (var (cam, px) in new[] { (a, pa), (b, pb) })
        {
            var P = cam.P;
            Row([px.X * P[8] - P[0], px.X * P[9] - P[1], px.X * P[10] - P[2], px.X * P[11] - P[3]]);
            Row([px.Y * P[8] - P[4], px.Y * P[9] - P[5], px.Y * P[10] - P[6], px.Y * P[11] - P[7]]);
        }
        var x = SmallestEigenvector(ata);
        if (Math.Abs(x[3]) < 1e-300) throw new ArgumentException("Those two marks see the place from the same direction: use photos taken from different sides.");
        var p = new Vec3(x[0] / x[3], x[1] / x[3], x[2] / x[3]);
        var ra = a.RayThrough(pa);
        var rb = b.RayThrough(pb);
        double Gap(Ray r) { var v = p - r.Origin; return (v - r.Direction * Vec3.Dot(v, r.Direction)).Length; }
        return (p, Gap(ra) + Gap(rb));
    }

    private static bool CoplanarOrDegenerate(IReadOnlyList<PhotoMark> marks)
    {
        var pts = marks.Select(m => m.World).ToList();
        var c = pts.Aggregate(Vec3.Zero, (s, p) => s + p) / pts.Count;
        double size = pts.Max(p => (p - c).Length);
        if (size < 1e-12) return true;
        // The smallest spread of the points about their centre, against the largest.
        var cov = new double[3, 3];
        foreach (var p in pts)
        {
            var d = p - c;
            double[] v = [d.X, d.Y, d.Z];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) cov[i, j] += v[i] * v[j];
        }
        var (values, _) = Jacobi(cov);
        return values.Min() < values.Max() * 1e-8;
    }

    /// <summary>The unit eigenvector of a symmetric matrix's smallest eigenvalue.</summary>
    public static double[] SmallestEigenvector(double[,] a)
    {
        var (values, vectors) = Jacobi(a);
        int n = values.Length, best = 0;
        for (int i = 1; i < n; i++) if (values[i] < values[best]) best = i;
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = vectors[i, best];
        return v;
    }

    /// <summary>Eigenvalues and eigenvectors (as columns) of a symmetric matrix, by cyclic Jacobi rotations.</summary>
    public static (double[] Values, double[,] Vectors) Jacobi(double[,] input)
    {
        int n = input.GetLength(0);
        var a = (double[,])input.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int p = 0; p < n; p++) for (int q = p + 1; q < n; q++) off += a[p, q] * a[p, q];
            if (off < 1e-30) break;
            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = a[i, i];
        return (values, v);
    }
}
