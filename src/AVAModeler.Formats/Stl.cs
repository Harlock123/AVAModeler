using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Formats;

/// <summary>
/// STL (stereolithography) meshes, binary and ASCII, for 3D printing. STL keeps only triangles with facet normals: no
/// surfaces, UVs or layers. Its space is right-handed with Z up, as slicers expect, so LightWave's (X right, Y up, Z
/// away) maps to STL's X = X, Y = Z, Z = Y: the model stands on the print bed the way it stands on the ground in the
/// Modeler, its front toward the viewer. Swapping two axes mirrors the space, so each triangle's winding is reversed
/// to keep it facing out (anticlockwise seen from outside, as STL wants).
/// </summary>
public static class Stl
{
    /// <summary>LightWave's space to STL's (Z up): (x, y, z) → (x, z, y).</summary>
    public static Vec3 ToStl(Vec3 p) => new(p.X, p.Z, p.Y);

    /// <summary>STL's space to LightWave's: the same swap.</summary>
    public static Vec3 FromStl(Vec3 p) => new(p.X, p.Z, p.Y);

    /// <summary>Every face of every layer as triangles in STL's space, wound for it, with their facet normals.</summary>
    public static List<(Vec3 Normal, Vec3 A, Vec3 B, Vec3 C)> Triangles(Document doc)
    {
        var result = new List<(Vec3, Vec3, Vec3, Vec3)>();
        foreach (var layer in doc.Layers)
            foreach (var t in Triangulator.TriangulateMesh(layer.Mesh))
            {
                var pts = layer.Mesh.Points;
                // Reversed: (a, c, b), as the axis swap mirrors the space.
                Vec3 a = ToStl(pts[t.A]), b = ToStl(pts[t.C]), c = ToStl(pts[t.B]);
                var n = Vec3.Cross(b - a, c - a).Normalized();
                result.Add((n, a, b, c));
            }
        return result;
    }

    /// <summary>Writes a binary STL: an 80-byte header, the triangle count, then 50 bytes a triangle.</summary>
    public static void WriteBinary(Document doc, Stream stream)
    {
        var tris = Triangles(doc);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("Binary STL written by AVA Modeler (Z up)").CopyTo(header, 0);
        stream.Write(header);
        Span<byte> buffer = stackalloc byte[50];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)tris.Count);
        stream.Write(buffer[..4]);
        foreach (var (n, a, b, c) in tris)
        {
            int at = 0;
            foreach (var v in (Vec3[])[n, a, b, c])
            {
                BinaryPrimitives.WriteSingleLittleEndian(buffer[at..], (float)v.X);
                BinaryPrimitives.WriteSingleLittleEndian(buffer[(at + 4)..], (float)v.Y);
                BinaryPrimitives.WriteSingleLittleEndian(buffer[(at + 8)..], (float)v.Z);
                at += 12;
            }
            buffer[48] = buffer[49] = 0;
            stream.Write(buffer);
        }
    }

    /// <summary>Writes an ASCII STL: <c>solid name</c>, a <c>facet normal … outer loop … endloop endfacet</c> per triangle, <c>endsolid name</c>.</summary>
    public static void WriteAscii(Document doc, TextWriter w, string name = "AVAModeler")
    {
        string solid = name.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (solid.Length == 0) solid = "AVAModeler";
        w.Write($"solid {solid}\n");
        static string F(double v) => ((float)v).ToString("R", CultureInfo.InvariantCulture);
        foreach (var (n, a, b, c) in Triangles(doc))
        {
            w.Write($"  facet normal {F(n.X)} {F(n.Y)} {F(n.Z)}\n    outer loop\n");
            foreach (var v in (Vec3[])[a, b, c]) w.Write($"      vertex {F(v.X)} {F(v.Y)} {F(v.Z)}\n");
            w.Write("    endloop\n  endfacet\n");
        }
        w.Write($"endsolid {solid}\n");
    }

    /// <summary>Whether bytes are a binary STL: exactly 84 + 50 bytes a triangle long (ASCII ones start "solid", but some binary ones do too).</summary>
    public static bool IsBinary(byte[] data)
    {
        if (data.Length < 84) return false;
        long count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80));
        if (84 + count * 50 == data.Length) return true;
        return !Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 5)).Equals("solid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads an STL file, binary or ASCII (told apart by <see cref="IsBinary"/>), into layer 1 with one surface,
    /// "Default". Points at exactly the same place are merged, so the triangles join up again; triangles that collapse
    /// to a line are left out. The facet normals aren't needed: the winding says which way each face looks.
    /// </summary>
    public static Document Read(byte[] data)
    {
        var doc = new Document();
        var mesh = doc.Layers[0].Mesh;
        var pointOf = new Dictionary<Vec3, int>();
        int Point(Vec3 stl)
        {
            var p = FromStl(stl);
            if (!pointOf.TryGetValue(p, out int i))
            {
                pointOf[p] = i = mesh.Points.Count;
                mesh.Points.Add(p);
            }
            return i;
        }
        void Add(Vec3 a, Vec3 b, Vec3 c)
        {
            int ia = Point(a), ib = Point(b), ic = Point(c);
            if (ia == ib || ib == ic || ia == ic) return;
            // Back to LightWave's clockwise winding.
            mesh.Polygons.Add(new Polygon([ia, ic, ib]));
        }
        if (IsBinary(data))
        {
            long count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80));
            if (84 + count * 50 > data.Length) throw new FormatException($"The binary STL says it has {count} triangles, but it's too short for them.");
            for (long t = 0; t < count; t++)
            {
                int at = (int)(84 + t * 50);
                Add(Vertex(data, at + 12), Vertex(data, at + 24), Vertex(data, at + 36));
            }
        }
        else
        {
            var words = Encoding.ASCII.GetString(data).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var loop = new List<Vec3>();
            for (int i = 0; i < words.Length; i++)
            {
                switch (words[i].ToLowerInvariant())
                {
                    case "vertex":
                        if (i + 3 >= words.Length) throw new FormatException("An STL vertex needs x, y and z.");
                        loop.Add(new Vec3(Number(words[i + 1]), Number(words[i + 2]), Number(words[i + 3])));
                        i += 3;
                        break;
                    case "endloop":
                        // A loop of more than three points (which some writers make) is fanned into triangles.
                        for (int k = 1; k + 1 < loop.Count; k++) Add(loop[0], loop[k], loop[k + 1]);
                        loop.Clear();
                        break;
                }
            }
        }
        mesh.Touch();
        Loading.ShowFirstLayer(doc);
        return doc;
    }

    private static Vec3 Vertex(byte[] data, int at) => new(
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at)),
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + 4)),
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + 8)));

    private static double Number(string word) =>
        double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : throw new FormatException($"\"{word}\" isn't a number in the STL file.");
}
