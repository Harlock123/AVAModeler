using AVAModeler.Core;

namespace AVAModeler.Scene.Tests;

/// <summary>Meshes, documents and helpers the tests share.</summary>
internal static class TestData
{
    public static void AssertNear(double expected, double actual, double tolerance = 1e-9) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}.");

    public static void AssertNear(Vec3 expected, Vec3 actual, double tolerance = 1e-9) =>
        Assert.True(Vec3.Near(expected, actual, tolerance), $"Expected {expected}, got {actual}.");

    /// <summary>Adds a face through some points, wound so its normal points along <paramref name="facing"/>.</summary>
    public static void Face(Mesh mesh, int surface, Vec3 facing, params Vec3[] points)
    {
        int i = mesh.AddFace(surface, points);
        if (Vec3.Dot(mesh.Normal(i), facing) < 0) mesh.Flip(i);
    }

    /// <summary>A UV sphere with outward-facing faces.</summary>
    public static Mesh Sphere(Vec3 center, double radius, int segments = 24, int rings = 12, int surface = 0)
    {
        var m = new Mesh();
        Vec3 At(int ring, int seg)
        {
            double theta = Math.PI * ring / rings, phi = 2 * Math.PI * seg / segments;
            return center + new Vec3(Math.Sin(theta) * Math.Cos(phi), Math.Cos(theta), Math.Sin(theta) * Math.Sin(phi)) * radius;
        }
        for (int r = 0; r < rings; r++)
        for (int s = 0; s < segments; s++)
        {
            var pts = new List<Vec3> { At(r, s), At(r, s + 1), At(r + 1, s + 1), At(r + 1, s) };
            if (r == 0) pts.RemoveAt(0);
            else if (r == rings - 1) pts.RemoveAt(2);
            var c = pts.Aggregate(Vec3.Zero, (a, b) => a + b) / pts.Count;
            Face(m, surface, c - center, [.. pts]);
        }
        m.MergePoints(1e-9);
        return m;
    }

    /// <summary>A square of a size centred on a point, facing along an axis direction.</summary>
    public static Mesh Square(Vec3 center, double size, Vec3 facing, int surface = 0)
    {
        var m = new Mesh();
        var n = facing.Normalized();
        var u = n.AnyPerpendicular();
        var v = Vec3.Cross(n, u);
        double h = size / 2;
        Face(m, surface, n, center + (-u - v) * h, center + (u - v) * h, center + (u + v) * h, center + (-u + v) * h);
        return m;
    }

    /// <summary>A document whose first layer holds a mesh, with one surface set up by the caller.</summary>
    public static Document Doc(Mesh mesh, Action<Surface>? surface = null)
    {
        var d = new Document();
        d.Layers[0].Mesh = mesh;
        surface?.Invoke(d.Surfaces[0]);
        return d;
    }

    /// <summary>A unique temporary folder, deleted on dispose.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AVAModeler.Scene.Tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
