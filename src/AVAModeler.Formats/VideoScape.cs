using System.Globalization;
using System.Text;
using AVAModeler.Core;

namespace AVAModeler.Formats;

/// <summary>
/// VideoScape 3D's ASCII objects ("3DG1"): a point count, the points, then polygons as
/// <c>count index… colour</c> until the end of the file. Indices count from 0. A negative count marks a polygon with
/// detail polygons: the next number is how many follow, each in the same form. Colour c becomes the surface
/// "VideoScape c", coloured from <see cref="Palette"/>. The 3DG2/3DG3 and GOUR variants aren't supported.
/// </summary>
public static class VideoScape
{
    /// <summary>
    /// The 16 colours, in the spirit of VideoScape's Amiga palette: black, the dark and bright primaries and their
    /// mixes, two greys and white. Colours above 15 wrap round this table.
    /// </summary>
    public static readonly IReadOnlyList<Rgb> Palette =
    [
        new(0, 0, 0), new(0, 0, 170), new(0, 170, 0), new(0, 170, 170),
        new(170, 0, 0), new(170, 0, 170), new(170, 85, 0), new(170, 170, 170),
        new(85, 85, 85), new(85, 85, 255), new(85, 255, 85), new(85, 255, 255),
        new(255, 85, 85), new(255, 85, 255), new(255, 255, 85), new(255, 255, 255),
    ];

    private const string SurfacePrefix = "VideoScape ";

    /// <summary>Reads a 3DG1 file into layer 0 of a new document.</summary>
    public static Document Read(TextReader reader)
    {
        var tokens = new Tokens(reader);
        string magic = tokens.Next("the 3DG1 header");
        if (magic != "3DG1")
            throw new FormatException(magic is "3DG2" or "3DG3" or "GOUR"
                ? $"VideoScape {magic} files aren't supported, only 3DG1."
                : $"Not a VideoScape file: it starts with \"{magic}\" rather than 3DG1.");
        var doc = new Document { Surfaces = [] };
        var mesh = doc.Layers[0].Mesh;
        int count = tokens.Int("the point count");
        if (count < 0) throw tokens.Error($"negative point count {count}");
        for (int i = 0; i < count; i++)
            mesh.Points.Add(new Vec3(tokens.Number("a point"), tokens.Number("a point"), tokens.Number("a point")));
        while (tokens.More()) mesh.Polygons.Add(ReadPolygon(tokens, doc, mesh.Points.Count));
        if (doc.Surfaces.Count == 0) doc.Surfaces.Add(new Surface("Default"));
        mesh.Touch();
        Loading.ShowFirstLayer(doc);
        return doc;
    }

    private static Polygon ReadPolygon(Tokens tokens, Document doc, int pointCount)
    {
        int n = tokens.Int("a polygon's point count");
        var points = new int[Math.Abs(n)];
        for (int i = 0; i < points.Length; i++)
        {
            int p = tokens.Int("a polygon's point");
            if (p < 0 || p >= pointCount) throw tokens.Error($"polygon uses point {p} but there are only {pointCount}");
            points[i] = p;
        }
        int colour = tokens.Int("a polygon's colour");
        if (colour < 0) throw tokens.Error($"negative colour {colour}");
        var poly = new Polygon(points, SurfaceFor(doc, colour));
        if (n < 0)
        {
            int details = tokens.Int("a detail polygon count");
            if (details < 0) throw tokens.Error($"negative detail polygon count {details}");
            poly.Details = [];
            for (int i = 0; i < details; i++) poly.Details.Add(ReadPolygon(tokens, doc, pointCount));
        }
        return poly;
    }

    private static int SurfaceFor(Document doc, int colour)
    {
        string name = SurfacePrefix + colour;
        int i = doc.Surfaces.FindIndex(s => s.Name == name);
        if (i >= 0) return i;
        doc.Surfaces.Add(new Surface(name) { Color = Palette[colour % Palette.Count] });
        return doc.Surfaces.Count - 1;
    }

    /// <summary>
    /// Writes every layer's geometry as one 3DG1 object. A surface named "VideoScape c" keeps colour c; any other
    /// gets the palette colour nearest its own.
    /// </summary>
    public static void Write(Document doc, TextWriter writer)
    {
        var mesh = new Mesh();
        foreach (var l in doc.Layers) mesh.Append(l.Mesh);
        var colours = doc.Surfaces.Select(ColourOf).ToArray();
        var text = new StringBuilder();
        text.Append("3DG1\n").Append(mesh.Points.Count).Append('\n');
        foreach (var p in mesh.Points) text.Append(Inv($"{p.X:R} {p.Y:R} {p.Z:R}\n"));
        foreach (var poly in mesh.Polygons) WritePolygon(text, poly, colours);
        writer.Write(text.ToString());
    }

    private static void WritePolygon(StringBuilder text, Polygon poly, int[] colours)
    {
        text.Append(poly.Details == null ? poly.Count : -poly.Count);
        foreach (int i in poly.Points) text.Append(' ').Append(i);
        int colour = poly.Surface >= 0 && poly.Surface < colours.Length ? colours[poly.Surface] : 0;
        text.Append(' ').Append(colour).Append('\n');
        if (poly.Details == null) return;
        text.Append(poly.Details.Count).Append('\n');
        foreach (var d in poly.Details) WritePolygon(text, d, colours);
    }

    private static int ColourOf(Surface s)
    {
        if (s.Name.StartsWith(SurfacePrefix, StringComparison.Ordinal)
            && int.TryParse(s.Name.AsSpan(SurfacePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int c))
            return c;
        var v = s.Color.ToVec();
        return Enumerable.Range(0, Palette.Count).MinBy(i => (Palette[i].ToVec() - v).LengthSquared);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>The file as whitespace-separated words, with line numbers for error messages.</summary>
    private sealed class Tokens
    {
        private readonly TextReader _reader;
        private readonly Queue<string> _words = new();
        private int _line;

        public Tokens(TextReader reader) => _reader = reader;

        public FormatException Error(string message) => new($"VideoScape line {_line}: {message}");

        public bool More()
        {
            while (_words.Count == 0)
            {
                string? line = _reader.ReadLine();
                if (line == null) return false;
                _line++;
                foreach (var w in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) _words.Enqueue(w);
            }
            return true;
        }

        public string Next(string what)
        {
            if (!More()) throw new FormatException($"VideoScape: the file ends where {what} should be.");
            return _words.Dequeue();
        }

        public int Int(string what)
        {
            string w = Next(what);
            return int.TryParse(w, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v)
                ? v : throw Error($"\"{w}\" isn't a whole number ({what})");
        }

        public double Number(string what)
        {
            string w = Next(what);
            return double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v : throw Error($"\"{w}\" isn't a number ({what})");
        }
    }
}
