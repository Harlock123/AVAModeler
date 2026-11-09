using AVAModeler.Core;

namespace AVAModeler.Slicing.Tests;

/// <summary>Solids to print, built in the Modeler's space (metres, Y up) with faces wound LightWave's way.</summary>
internal static class Shapes
{
    /// <summary>A document whose first layer (in the foreground) holds the mesh <paramref name="build"/> makes.</summary>
    public static Document Doc(Action<Mesh> build)
    {
        var doc = new Document();
        build(doc.Layers[0].Mesh);
        doc.Layers[0].Mesh.Touch();
        return doc;
    }

    /// <summary>A box from min to max: six quads, each turned to face away from the centre.</summary>
    public static void Box(Mesh mesh, Vec3 min, Vec3 max)
    {
        int first = mesh.Points.Count;
        for (int i = 0; i < 8; i++)
            mesh.Points.Add(new Vec3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z));
        int[][] faces = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
        Turn(mesh, faces.Select(f => f.Select(i => i + first).ToArray()), (min + max) / 2);
    }

    /// <summary>A cube <paramref name="size"/> metres on a side, its base centred on the origin.</summary>
    public static Document Cube(double size = 0.02) => Doc(m => Box(m, new Vec3(-size / 2, 0, -size / 2), new Vec3(size / 2, size, size / 2)));

    /// <summary>
    /// A square tube standing on the ground: outside <paramref name="outer"/> wide, a square hole
    /// <paramref name="inner"/> wide through it, <paramref name="height"/> tall, as one closed shell of quads.
    /// </summary>
    public static Document Tube(double outer = 0.02, double inner = 0.01, double height = 0.01) => Doc(m =>
    {
        double o = outer / 2, i = inner / 2;
        (double X, double Z)[] ring = [(-o, -o), (o, -o), (o, o), (-o, o)], hole = [(-i, -i), (i, -i), (i, i), (-i, i)];
        foreach (var y in (double[])[0, height])
        {
            foreach (var (x, z) in ring) m.Points.Add(new Vec3(x, y, z));
            foreach (var (x, z) in hole) m.Points.Add(new Vec3(x, y, z));
        }
        // Points: bottom outer 0-3, bottom inner 4-7, top outer 8-11, top inner 12-15.
        var faces = new List<int[]>();
        for (int k = 0; k < 4; k++)
        {
            int n = (k + 1) % 4;
            faces.Add([k, n, 8 + n, 8 + k]);                     // outer wall
            faces.Add([4 + k, 4 + n, 12 + n, 12 + k]);           // inner wall
            faces.Add([k, n, 4 + n, 4 + k]);                     // bottom
            faces.Add([8 + k, 8 + n, 12 + n, 12 + k]);           // top
        }
        foreach (var f in faces) m.Polygons.Add(new Polygon(f));
        m.Touch();
        // Face each polygon outward: walls away from (or, inside, toward) the axis, the caps down and up.
        for (int k = 0; k < m.Polygons.Count; k++)
        {
            var p = m.Polygons[k];
            var c = m.Centroid(p);
            var n = m.Normal(p);
            bool inner = k % 4 == 1, bottom = k % 4 == 2, top = k % 4 == 3;
            var outward = bottom ? new Vec3(0, -1, 0) : top ? new Vec3(0, 1, 0) : new Vec3(c.X, 0, c.Z) * (inner ? -1 : 1);
            if (Vec3.Dot(n, outward) < 0) p.Points.Reverse();
        }
    });

    private static void Turn(Mesh mesh, IEnumerable<int[]> faces, Vec3 centre)
    {
        foreach (var f in faces)
        {
            var poly = new Polygon(f);
            mesh.Polygons.Add(poly);
            mesh.Touch();
            if (Vec3.Dot(mesh.Normal(poly), mesh.Centroid(poly) - centre) < 0) poly.Points.Reverse();
        }
        mesh.Touch();
    }
}
