using System.Globalization;
using AVAModeler.Core;

namespace AVAModeler.Formats;

/// <summary>
/// Wavefront OBJ objects, with materials from .mtl files. OBJ space is right-handed with faces wound
/// anticlockwise, so Z is negated and face winding reversed both ways: a model looks and faces the same as it did in
/// the program that wrote it.
/// </summary>
public static class Obj
{
    /// <summary>
    /// Reads an OBJ file. <c>v</c> become points; <c>f</c> faces (any of the v, v/vt, v//vn, v/vt/vn forms, with
    /// negative indices counting back from the latest vertex); <c>l</c> polylines become 2-point polygons and
    /// <c>p</c> 1-point polygons. Each <c>o</c> or <c>g</c> name gets a layer of its own, in order, adding layers past
    /// the usual ten as needed; a name seen again goes back to its layer, and a name given before any geometry renames
    /// the empty layer instead of starting another. A vertex goes into every layer that uses it, or the layer it was
    /// declared in if none does, and each layer's points keep the file's order. <c>usemtl</c> picks the surface by name; <c>mtllib</c> files are opened through
    /// <paramref name="mtl"/> (which gets the name as written and returns null if it can't be found) and their
    /// Kd, Ks, Ns, d/Tr and Ke set the surfaces of the same name. Readers it returns are disposed.
    /// </summary>
    public static Document Read(TextReader reader, Func<string, TextReader?>? mtl = null)
    {
        var doc = new Document { Surfaces = [] };
        var vertices = new List<Vec3>();
        var declaredIn = new List<int>();
        var referenced = new List<bool>();
        // Whether each layer has been given anything yet (a vertex or an element), so a new name renames it instead.
        var occupied = new List<bool> { false };
        var layerOf = new Dictionary<string, int>();
        var libraries = new List<string>();
        int layer = 0, surface = -1, lineNumber = 0;

        FormatException Error(string message) => new($"OBJ line {lineNumber}: {message}");

        // Polygons hold OBJ's global vertex indices until the end, when each layer gets its own points.
        int Point(string token)
        {
            string number = token.Split('/')[0];
            if (!int.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i) || i == 0)
                throw Error($"\"{token}\" isn't a vertex index");
            int global = i > 0 ? i - 1 : vertices.Count + i;
            if (global < 0 || global >= vertices.Count) throw Error($"vertex {i} doesn't exist (there are {vertices.Count} so far)");
            referenced[global] = true;
            return global;
        }

        void Add(IEnumerable<int> points)
        {
            if (surface < 0) surface = doc.SurfaceIndex("Default");
            doc.Layers[layer].Mesh.Polygons.Add(new Polygon(points, surface));
            occupied[layer] = true;
        }

        void SwitchLayer(string name)
        {
            if (layerOf.TryGetValue(name, out int existing)) { layer = existing; return; }
            if (!occupied[layer])
            {
                foreach (var old in layerOf.Where(kv => kv.Value == layer).Select(kv => kv.Key).ToList()) layerOf.Remove(old);
            }
            else
            {
                layer = occupied.Count;
                occupied.Add(false);
                Loading.LayerAt(doc, layer);
            }
            layerOf[name] = layer;
            doc.Layers[layer].Name = name;
        }

        foreach (string line in Lines(reader, () => lineNumber++))
        {
            int space = line.IndexOfAny([' ', '\t']);
            string keyword = space < 0 ? line : line[..space];
            string rest = space < 0 ? "" : line[(space + 1)..].Trim();
            string[] words = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (keyword)
            {
                case "v":
                    if (words.Length < 3) throw Error("a vertex needs x, y and z");
                    vertices.Add(new Vec3(Number(words[0], Error), Number(words[1], Error), Negate(Number(words[2], Error))));
                    declaredIn.Add(layer);
                    referenced.Add(false);
                    occupied[layer] = true;
                    break;
                case "f":
                    if (words.Length == 0) throw Error("a face needs points");
                    var face = words.Select(Point).ToList();
                    if (face.Count >= 3) face.Reverse();
                    Add(face);
                    break;
                case "l":
                    if (words.Length < 2) throw Error("a line needs at least two points");
                    var polyline = words.Select(Point).ToList();
                    for (int i = 0; i + 1 < polyline.Count; i++) Add([polyline[i], polyline[i + 1]]);
                    break;
                case "p":
                    foreach (string w in words) Add([Point(w)]);
                    break;
                case "o":
                case "g":
                    SwitchLayer(rest.Length > 0 ? rest : "default");
                    break;
                case "usemtl":
                    surface = doc.SurfaceIndex(rest.Length > 0 ? rest : "Default");
                    break;
                case "mtllib":
                    if (rest.Length > 0) libraries.Add(rest);
                    break;
            }
        }

        var loose = Enumerable.Range(0, vertices.Count).Where(v => !referenced[v]).ToLookup(v => declaredIn[v]);
        for (int i = 0; i < occupied.Count; i++) GivePoints(doc.Layers[i].Mesh, vertices, loose[i]);
        if (mtl != null) ReadLibraries(doc, libraries, mtl);
        if (doc.Surfaces.Count == 0) doc.Surfaces.Add(new Surface("Default"));
        Loading.ShowFirstLayer(doc);
        return doc;
    }

    /// <summary>
    /// Turns a layer's polygons from global vertex indices into its own points: the vertices it uses plus any extra
    /// ones, kept in file order.
    /// </summary>
    private static void GivePoints(Mesh mesh, List<Vec3> vertices, IEnumerable<int> extra)
    {
        var used = mesh.Polygons.SelectMany(p => p.Points).Concat(extra).Distinct().Order().ToList();
        var local = new Dictionary<int, int>(used.Count);
        foreach (int v in used) local[v] = local.Count;
        mesh.Points = used.Select(v => vertices[v]).ToList();
        foreach (var p in mesh.Polygons)
            for (int i = 0; i < p.Points.Count; i++) p.Points[i] = local[p.Points[i]];
        mesh.Touch();
    }

    private static void ReadLibraries(Document doc, List<string> libraries, Func<string, TextReader?> open)
    {
        var materials = new Dictionary<string, Surface>();
        foreach (string library in libraries)
        {
            // "mtllib" may list several files; a single name with spaces in it is tried first.
            string[] names = [library, .. library.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];
            foreach (string name in names.Distinct())
            {
                using var reader = open(name);
                if (reader == null) continue;
                ReadMaterials(reader, name, materials);
                if (name == library) break;
            }
        }
        for (int i = 0; i < doc.Surfaces.Count; i++)
            if (materials.TryGetValue(doc.Surfaces[i].Name, out var m)) doc.Surfaces[i] = m;
    }

    /// <summary>
    /// Reads an .mtl file: Kd is the colour, the largest of Ks the specular level, Ns the glossiness, 1 − d (or Tr)
    /// the transparency, and the largest of Ke the luminosity.
    /// </summary>
    private static void ReadMaterials(TextReader reader, string fileName, Dictionary<string, Surface> materials)
    {
        Surface? current = null;
        int lineNumber = 0;
        FormatException Error(string message) => new($"{fileName} line {lineNumber}: {message}");
        Vec3 Colour(string[] w) => w.Length >= 3
            ? new Vec3(Number(w[0], Error), Number(w[1], Error), Number(w[2], Error))
            : w.Length == 1 ? Vec3.One * Number(w[0], Error) : throw Error("a colour needs r, g and b");
        double Single(string[] w) => w.Length >= 1 ? Number(w[0], Error) : throw Error("a value is missing");

        foreach (string line in Lines(reader, () => lineNumber++))
        {
            int space = line.IndexOfAny([' ', '\t']);
            string keyword = space < 0 ? line : line[..space];
            string rest = space < 0 ? "" : line[(space + 1)..].Trim();
            if (keyword == "newmtl")
            {
                materials[rest] = current = new Surface(rest);
                continue;
            }
            if (current == null) continue;
            string[] w = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (keyword)
            {
                case "Kd": current.Color = Rgb.FromVec(Colour(w)); break;
                case "Ks": var ks = Colour(w); current.Specular = Math.Max(ks.X, Math.Max(ks.Y, ks.Z)); break;
                case "Ke": var ke = Colour(w); current.Luminosity = Math.Max(ke.X, Math.Max(ke.Y, ke.Z)); break;
                case "Ns": current.Glossiness = Math.Max(1, Single(w)); break;
                case "d": current.Transparency = 1 - Single(w); break;
                case "Tr": current.Transparency = Single(w); break;
            }
        }
    }

    /// <summary>
    /// Writes an OBJ file: an <c>o</c> per non-empty layer (named after it, or "Layer n"), its points, then its
    /// polygons in order with a <c>usemtl</c> wherever the surface changes. Faces become <c>f</c>, 2-point polygons
    /// <c>l</c> and 1-point polygons <c>p</c>, with 1-based indices; detail polygons are written as ordinary polygons
    /// after their parent. With <paramref name="mtl"/>, every surface is written there (Kd, Ks, Ns, d, and Ke when
    /// luminous), and <paramref name="mtlName"/>, if given, is named in an <c>mtllib</c> line.
    /// </summary>
    public static void Write(Document doc, TextWriter obj, TextWriter? mtl = null, string? mtlName = null)
    {
        obj.Write("# Written by AVAModeler\n");
        if (mtlName != null) obj.Write($"mtllib {mtlName}\n");
        int offset = 1;
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            var mesh = doc.Layers[i].Mesh;
            if (mesh.IsEmpty) continue;
            obj.Write($"o {(doc.Layers[i].Name.Length > 0 ? doc.Layers[i].Name : $"Layer {i + 1}")}\n");
            foreach (var p in mesh.Points) obj.Write(Inv($"v {p.X:R} {p.Y:R} {Negate(p.Z):R}\n"));
            int surface = -1;
            foreach (var poly in mesh.Polygons.SelectMany(Loading.Flatten))
            {
                if (poly.Count == 0) continue;
                if (poly.Surface != surface && poly.Surface >= 0 && poly.Surface < doc.Surfaces.Count)
                {
                    surface = poly.Surface;
                    obj.Write($"usemtl {doc.Surfaces[surface].Name}\n");
                }
                var points = poly.Points.Select(p => (p + offset).ToString(CultureInfo.InvariantCulture));
                obj.Write(poly.Count switch
                {
                    1 => $"p {points.First()}\n",
                    2 => $"l {string.Join(' ', points)}\n",
                    _ => $"f {string.Join(' ', points.Reverse())}\n",
                });
            }
            offset += mesh.Points.Count;
        }
        if (mtl == null) return;
        mtl.Write("# Written by AVAModeler\n");
        foreach (var s in doc.Surfaces)
        {
            var c = s.Color.ToVec();
            mtl.Write($"\nnewmtl {s.Name}\n");
            mtl.Write(Inv($"Kd {c.X:0.######} {c.Y:0.######} {c.Z:0.######}\n"));
            mtl.Write(Inv($"Ks {s.Specular:R} {s.Specular:R} {s.Specular:R}\n"));
            mtl.Write(Inv($"Ns {s.Glossiness:R}\n"));
            mtl.Write(Inv($"d {1 - s.Transparency:R}\n"));
            if (s.Luminosity > 0) mtl.Write(Inv($"Ke {s.Luminosity:R} {s.Luminosity:R} {s.Luminosity:R}\n"));
            mtl.Write("illum 2\n");
        }
    }

    /// <summary>Flips Z between the two spaces, without turning 0 into -0.</summary>
    private static double Negate(double z) => z == 0 ? 0 : -z;

    private static double Number(string word, Func<string, FormatException> error) =>
        double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : throw error($"\"{word}\" isn't a number");

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>The non-blank lines with comments removed and backslash-continued lines joined.</summary>
    private static IEnumerable<string> Lines(TextReader reader, Action counted)
    {
        string pending = "";
        while (reader.ReadLine() is { } raw)
        {
            counted();
            int hash = raw.IndexOf('#');
            string line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.EndsWith('\\'))
            {
                pending += line[..^1] + " ";
                continue;
            }
            line = (pending + line).Trim();
            pending = "";
            if (line.Length > 0) yield return line;
        }
        if (pending.Trim().Length > 0) yield return pending.Trim();
    }
}
