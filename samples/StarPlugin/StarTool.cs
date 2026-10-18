using AVAModeler.Core;
using AVAModeler.Tools;

namespace StarPlugin;

/// <summary>The plugin: its name, as the Plugins list shows it, and the file format it adds.</summary>
public sealed class Star : IModelerPlugin
{
    public string Name => "Star";
    public IEnumerable<IObjectFormat> Formats => [new PointCloud()];
}

/// <summary>A sample loader and saver: a point cloud as text, one "x y z" line per point (each a 1-point polygon).</summary>
public sealed class PointCloud : IObjectFormat
{
    public string Name => "Point cloud";
    public IReadOnlyList<string> Extensions => [".xyz"];
    public bool CanLoad => true;
    public bool CanSave => true;

    public Document Load(string path)
    {
        var doc = new Document { FilePath = path };
        var mesh = doc.Current.Mesh;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || line.TrimStart().StartsWith('#')) continue;
            double P(int i) => double.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
            mesh.AddPolygon([mesh.AddPoint(new Vec3(P(0), P(1), P(2)))]);
        }
        return doc;
    }

    public void Save(Document document, string path) =>
        File.WriteAllLines(path, document.Layers.SelectMany(l => l.Mesh.Points).Select(p => FormattableString.Invariant($"{p.X} {p.Y} {p.Z}")));
}

/// <summary>A sample generator tool from a plugin: a flat star with any number of points, in the Objects tab.</summary>
public sealed class StarTool : Tool
{
    public override string Name => "Star";
    public override string Tab => "Objects";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Points", Min = 3, Max = 100, Step = 1)] public int Points { get; set; } = 5;
    [Param("Outer radius", Distance = true, Min = 0)] public double Outer { get; set; } = 1;
    [Param("Inner radius", Distance = true, Min = 0)] public double Inner { get; set; } = 0.4;
    [Param("Center", Distance = true)] public Vec3 Center { get; set; }

    protected override void Apply()
    {
        var mesh = Doc.Current.Mesh;
        var ring = new List<int>();
        // Clockwise as the Front view sees it, so the star faces you there.
        for (int i = 0; i < Points * 2; i++)
        {
            double a = Math.PI / 2 - Math.PI * i / Points, r = i % 2 == 0 ? Outer : Inner;
            ring.Add(mesh.AddPoint(Center + new Vec3(Math.Cos(a) * r, Math.Sin(a) * r, 0)));
        }
        mesh.AddPolygon(ring);
    }

    protected override IEnumerable<int> Layers => [Doc.CurrentLayer];
}
