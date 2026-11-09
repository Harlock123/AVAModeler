using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Formats;

/// <summary>
/// 3MF (3D Manufacturing Format), the zip of XML slicers now prefer to STL: it says its unit (millimetres here), keeps
/// each layer as an object of its own with its name, and each surface as a base material with its colour. Like STL
/// its space is right-handed with Z up, so it takes STL's mapping (<see cref="Stl.ToStl"/>: Y up becomes Z up, metres
/// millimetres) and its winding (anticlockwise from outside). Reading gives each object a layer and each base
/// material a surface; meshes are triangles, with points at the same place merged.
/// </summary>
public static class ThreeMf
{
    private const string CoreNs = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private const string ModelPath = "3D/3dmodel.model";

    public static void Write(Document doc, Stream stream)
    {
        XNamespace ns = CoreNs;
        var used = doc.Layers.Select((l, i) => (Layer: l, Index: i)).Where(x => x.Layer.Mesh.Polygons.Any(p => p.Count >= 3)).ToList();
        var surfaces = doc.Surfaces;
        var materials = new XElement(ns + "basematerials", new XAttribute("id", 1),
            surfaces.Count > 0
                ? surfaces.Select(s => new XElement(ns + "base", new XAttribute("name", s.Name), new XAttribute("displaycolor", s.Color.ToString())))
                : [new XElement(ns + "base", new XAttribute("name", "Default"), new XAttribute("displaycolor", Rgb.Grey.ToString()))]);
        var resources = new XElement(ns + "resources", materials);
        var build = new XElement(ns + "build");
        int id = 2;
        foreach (var (layer, index) in used)
        {
            var mesh = layer.Mesh;
            var vertices = new XElement(ns + "vertices");
            foreach (var p in mesh.Points)
            {
                var q = Stl.ToStl(p);
                vertices.Add(new XElement(ns + "vertex", new XAttribute("x", N(q.X)), new XAttribute("y", N(q.Y)), new XAttribute("z", N(q.Z))));
            }
            var triangles = new XElement(ns + "triangles");
            foreach (var t in Triangulator.TriangulateMesh(mesh))
            {
                int surface = Math.Clamp(mesh.Polygons[t.Polygon].Surface, 0, Math.Max(0, surfaces.Count - 1));
                // Reversed (a, c, b): the axis swap mirrors the space, as for STL.
                triangles.Add(new XElement(ns + "triangle", new XAttribute("v1", t.A), new XAttribute("v2", t.C), new XAttribute("v3", t.B),
                    new XAttribute("pid", 1), new XAttribute("p1", surface)));
            }
            string name = string.IsNullOrWhiteSpace(layer.Name) ? $"Layer {index + 1}" : layer.Name;
            resources.Add(new XElement(ns + "object", new XAttribute("id", id), new XAttribute("type", "model"), new XAttribute("name", name),
                new XAttribute("pid", 1), new XAttribute("pindex", 0), new XElement(ns + "mesh", vertices, triangles)));
            build.Add(new XElement(ns + "item", new XAttribute("objectid", id)));
            id++;
        }
        var model = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "model", new XAttribute("unit", "millimeter"), new XAttribute(XNamespace.Xml + "lang", "en-US"),
                new XElement(ns + "metadata", new XAttribute("name", "Application"), "AVA Modeler"),
                resources, build));

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Entry(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>
            """);
        Entry(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>
            """);
        var entry = zip.CreateEntry(ModelPath, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var w = XmlWriter.Create(s, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        model.Save(w);
    }

    private static void Entry(ZipArchive zip, string name, string text)
    {
        using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        var bytes = new UTF8Encoding(false).GetBytes(text.Trim());
        s.Write(bytes);
    }

    private static string N(double v) => ((float)v).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a 3MF: its model part (found through the package's relationships, or at 3D/3dmodel.model), each object
    /// with a mesh as a layer, each base material as a surface, in the file's unit (micron to metre all understood).
    /// Objects made of other objects (components) are read as the meshes they place. The build's item transforms
    /// are applied.
    /// </summary>
    public static Document Read(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        string path = ModelPath;
        if (zip.GetEntry("_rels/.rels") is { } rels)
        {
            using var r = rels.Open();
            var x = XDocument.Load(r);
            var target = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Relationship" && ((string?)e.Attribute("Type") ?? "").EndsWith("/3dmodel"));
            if (target != null) path = ((string?)target.Attribute("Target") ?? path).TrimStart('/');
        }
        var entry = zip.GetEntry(path) ?? throw new FormatException($"The 3MF has no model at {path}.");
        XDocument model;
        using (var s = entry.Open()) model = XDocument.Load(s);
        var root = model.Root ?? throw new FormatException("The 3MF's model is empty.");
        XNamespace ns = root.Name.Namespace;
        double scale = ((string?)root.Attribute("unit") ?? "millimeter") switch
        {
            "micron" => 1e-3,
            "millimeter" => 1,
            "centimeter" => 10,
            "inch" => 25.4,
            "foot" => 304.8,
            "meter" => 1000,
            var u => throw new FormatException($"The 3MF's unit \"{u}\" isn't one 3MF has."),
        };
        var doc = new Document();
        var resources = root.Element(ns + "resources") ?? throw new FormatException("The 3MF has no resources.");
        // Base materials as surfaces, by (group id, index).
        var surfaceOf = new Dictionary<(string, int), int>();
        foreach (var group in resources.Elements(ns + "basematerials"))
        {
            string gid = (string?)group.Attribute("id") ?? "";
            int k = 0;
            foreach (var b in group.Elements(ns + "base"))
            {
                string name = (string?)b.Attribute("name") is { Length: > 0 } n ? n : $"Material {gid}.{k}";
                int si = doc.SurfaceIndex(name);
                if (ParseColour((string?)b.Attribute("displaycolor")) is { } c) doc.Surfaces[si].Color = c;
                surfaceOf[(gid, k++)] = si;
            }
        }
        var objects = resources.Elements(ns + "object").ToDictionary(o => (string?)o.Attribute("id") ?? "");
        int layerIndex = 0;
        var build = root.Element(ns + "build");
        var items = build?.Elements(ns + "item").Select(i => ((string?)i.Attribute("objectid") ?? "", Matrix((string?)i.Attribute("transform")))).ToList()
            ?? objects.Keys.Select(k => (k, Identity)).ToList();
        foreach (var (objectId, transform) in items)
        {
            if (!objects.TryGetValue(objectId, out var obj)) throw new FormatException($"The 3MF's build names object {objectId}, which isn't there.");
            var layer = Loading.LayerAt(doc, layerIndex++);
            layer.Name = (string?)obj.Attribute("name") ?? "";
            AddObject(obj, transform, layer.Mesh, objects, surfaceOf, ns, scale, 0);
            layer.Mesh.Touch();
        }
        Loading.ShowFirstLayer(doc);
        foreach (var l in doc.Layers.Where(l => !l.Mesh.IsEmpty)) l.State = LayerState.Foreground;
        return doc;
    }

    private static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];

    /// <summary>A 3MF transform ("m00 m01 m02 m10 … m32", rows of a 4 × 3 matrix applied to row vectors).</summary>
    private static double[] Matrix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Identity;
        var v = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        return v.Length == 12 ? v : throw new FormatException("A 3MF transform has 12 numbers.");
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        // Row vector p · A then · B: rows of A times B's 3 × 3, plus B's translation.
        var r = new double[12];
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 3; col++)
                r[row * 3 + col] = a[row * 3] * b[col] + a[row * 3 + 1] * b[3 + col] + a[row * 3 + 2] * b[6 + col] + (row == 3 ? b[9 + col] : 0);
        return r;
    }

    private static void AddObject(XElement obj, double[] m, Mesh mesh, Dictionary<string, XElement> objects, Dictionary<(string, int), int> surfaceOf, XNamespace ns, double scale, int depth)
    {
        if (depth > 16) throw new FormatException("The 3MF's components nest too deep (or round in a circle).");
        string pid = (string?)obj.Attribute("pid") ?? "";
        int pindex = (int?)obj.Attribute("pindex") ?? 0;
        if (obj.Element(ns + "mesh") is { } m3)
        {
            var index = new Dictionary<Vec3, int>();
            var local = new List<int>();
            foreach (var v in m3.Element(ns + "vertices")?.Elements(ns + "vertex") ?? [])
            {
                double x = D(v, "x"), y = D(v, "y"), z = D(v, "z");
                var p = new Vec3(x * m[0] + y * m[3] + z * m[6] + m[9], x * m[1] + y * m[4] + z * m[7] + m[10], x * m[2] + y * m[5] + z * m[8] + m[11]);
                var q = Stl.FromStl(p * scale);
                if (!index.TryGetValue(q, out int i))
                {
                    index[q] = i = mesh.Points.Count;
                    mesh.Points.Add(q);
                }
                local.Add(i);
            }
            foreach (var t in m3.Element(ns + "triangles")?.Elements(ns + "triangle") ?? [])
            {
                int a = local[(int)t.Attribute("v1")!], b = local[(int)t.Attribute("v2")!], c = local[(int)t.Attribute("v3")!];
                if (a == b || b == c || a == c) continue;
                string tp = (string?)t.Attribute("pid") ?? pid;
                int ti = (int?)t.Attribute("p1") ?? pindex;
                int surface = surfaceOf.TryGetValue((tp, ti), out int si) ? si : 0;
                // Back to LightWave's clockwise winding.
                mesh.Polygons.Add(new Polygon([a, c, b], surface));
            }
        }
        foreach (var comp in obj.Element(ns + "components")?.Elements(ns + "component") ?? [])
        {
            string cid = (string?)comp.Attribute("objectid") ?? "";
            if (!objects.TryGetValue(cid, out var child)) throw new FormatException($"A 3MF component names object {cid}, which isn't there.");
            AddObject(child, Multiply(Matrix((string?)comp.Attribute("transform")), m), mesh, objects, surfaceOf, ns, scale, depth + 1);
        }
    }

    private static double D(XElement e, string name) =>
        double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : throw new FormatException($"A 3MF vertex's {name} isn't a number.");

    private static Rgb? ParseColour(string? text)
    {
        if (text is not { Length: >= 7 } || text[0] != '#') return null;
        try
        {
            return new Rgb(System.Convert.ToByte(text[1..3], 16), System.Convert.ToByte(text[3..5], 16), System.Convert.ToByte(text[5..7], 16));
        }
        catch (FormatException) { return null; }
    }
}
