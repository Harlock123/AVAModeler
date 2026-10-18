using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Geometry.Tests;

/// <summary>Meshes to test with, all with outward faces wound as LightWave winds them.</summary>
internal static class TestShapes
{
    /// <summary>An axis-aligned box of six quads.</summary>
    public static Mesh Box(Vec3 min, Vec3 max, int surface = 0)
    {
        var m = new Mesh();
        for (int i = 0; i < 8; i++)
            m.Points.Add(new Vec3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
        int[][] faces =
        [
            [0, 4, 6, 2], // -X
            [1, 3, 7, 5], // +X
            [0, 1, 5, 4], // -Y
            [2, 6, 7, 3], // +Y
            [0, 2, 3, 1], // -Z
            [4, 5, 7, 6], // +Z
        ];
        foreach (var f in faces) m.Polygons.Add(new Polygon(f, surface));
        m.Touch();
        return m;
    }

    public static Mesh Box(double size = 2, int surface = 0) => Box(Vec3.One * (-size / 2), Vec3.One * (size / 2), surface);

    /// <summary>A globe: quads, with triangles round the poles on the Y axis.</summary>
    public static Mesh Sphere(Vec3 center, double radius, int sides = 24, int bands = 12, int surface = 0)
    {
        var m = new Mesh();
        m.Points.Add(center + new Vec3(0, -radius, 0));
        for (int b = 1; b < bands; b++)
        {
            double phi = Math.PI * b / bands - Math.PI / 2;
            for (int s = 0; s < sides; s++)
            {
                double th = 2 * Math.PI * s / sides;
                m.Points.Add(center + new Vec3(Math.Cos(phi) * Math.Cos(th), Math.Sin(phi), Math.Cos(phi) * Math.Sin(th)) * radius);
            }
        }
        m.Points.Add(center + new Vec3(0, radius, 0));
        int top = m.Points.Count - 1;
        int Ring(int b, int s) => 1 + (b - 1) * sides + (s % sides);
        for (int s = 0; s < sides; s++)
        {
            m.Polygons.Add(new Polygon([0, Ring(1, s), Ring(1, s + 1)], surface));
            for (int b = 1; b < bands - 1; b++)
                m.Polygons.Add(new Polygon([Ring(b, s), Ring(b + 1, s), Ring(b + 1, s + 1), Ring(b, s + 1)], surface));
            m.Polygons.Add(new Polygon([top, Ring(bands - 1, s + 1), Ring(bands - 1, s)], surface));
        }
        m.Touch();
        // Face each polygon away from the centre, whichever way round it was written.
        foreach (var p in m.Polygons)
            if (Vec3.Dot(m.Normal(p), m.Centroid(p) - center) < 0) p.Points.Reverse();
        m.Touch();
        return m;
    }

    /// <summary>A flat grid of n×n quads in the XZ plane (y = 0) from -size/2 to size/2, facing up (+Y).</summary>
    public static Mesh Grid(int n, double size, int surface = 0)
    {
        var m = new Mesh();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
                m.Points.Add(new Vec3(-size / 2 + size * i / n, 0, -size / 2 + size * j / n));
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i;
                var p = new Polygon([a, a + n + 1, a + n + 2, a + 1], surface);
                m.Polygons.Add(p);
            }
        m.Touch();
        foreach (var p in m.Polygons)
            if (m.Normal(p).Y < 0) p.Points.Reverse();
        m.Touch();
        return m;
    }

    /// <summary>A flat regular polygon (a disc) in the plane at right angles to an axis, through a centre.</summary>
    public static Mesh Disc(Vec3 center, double radius, int sides, Axis axis = Axis.Y, int surface = 0)
    {
        var m = new Mesh();
        for (int i = 0; i < sides; i++)
        {
            double a = 2 * Math.PI * i / sides, c = Math.Cos(a) * radius, s = Math.Sin(a) * radius;
            m.Points.Add(center + axis switch { Axis.X => new Vec3(0, c, s), Axis.Y => new Vec3(c, 0, s), _ => new Vec3(c, s, 0) });
        }
        m.Polygons.Add(new Polygon(Enumerable.Range(0, sides), surface));
        m.Touch();
        return m;
    }

    /// <summary>The area of a regular polygon of n sides inscribed in a circle.</summary>
    public static double RegularPolygonArea(double radius, int sides) => 0.5 * sides * radius * radius * Math.Sin(2 * Math.PI / sides);

    /// <summary>Whether every face's normal points away from a point inside (for convex solids).</summary>
    public static bool FacesPointAwayFrom(Mesh m, Vec3 inside) =>
        m.Polygons.Where(p => p.Count >= 3).All(p => Vec3.Dot(m.Normal(p), m.Centroid(p) - inside) > 0);

    public static void AssertClose(Vec3 expected, Vec3 actual, double tol = 1e-9) =>
        Assert.True(Vec3.Near(expected, actual, tol), $"expected {expected}, got {actual}");
}
