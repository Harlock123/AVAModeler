using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

public class ThreeMfTests
{
    private static Document TwoColouredBoxes()
    {
        var doc = new Document();
        int red = doc.SurfaceIndex("Red"), blue = doc.SurfaceIndex("Blue");
        doc.Surfaces[red].Color = new Rgb(200, 30, 30);
        doc.Surfaces[blue].Color = new Rgb(30, 60, 220);
        Models.Box(doc.Layers[0].Mesh, new Vec3(-0.01, 0, -0.01), new Vec3(0.01, 0.02, 0.01), red);
        Models.Box(doc.Layers[1].Mesh, new Vec3(0.03, 0, 0), new Vec3(0.04, 0.01, 0.03), blue);
        doc.Layers[0].Name = "Body";
        return doc;
    }

    [Fact]
    public void A_3mf_is_a_package_with_its_model_in_millimetres_an_object_per_layer_and_its_colours()
    {
        using var dir = new TempDir();
        string path = dir.File("boxes.3mf");
        ObjectFiles.Save(TwoColouredBoxes(), path);
        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.Contains("3dmodel", new StreamReader(zip.GetEntry("_rels/.rels")!.Open()).ReadToEnd());
        var model = XDocument.Load(zip.GetEntry("3D/3dmodel.model")!.Open());
        XNamespace ns = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
        Assert.Equal("millimeter", (string?)model.Root!.Attribute("unit"));
        var objects = model.Descendants(ns + "object").ToList();
        Assert.Equal(2, objects.Count);
        Assert.Equal("Body", (string?)objects[0].Attribute("name"));
        // Every surface a base material with its colour, in the document's order ("Default" first).
        var bases = model.Descendants(ns + "base").Select(b => ((string)b.Attribute("name")!, (string)b.Attribute("displaycolor")!)).ToList();
        Assert.Equal(("Red", "#C81E1E"), bases[1]);
        Assert.Equal(("Blue", "#1E3CDC"), bases[2]);
        Assert.Equal(2, model.Descendants(ns + "item").Count());
        // The first box is 20 mm tall, Z up.
        var zs = objects[0].Descendants(ns + "vertex").Select(v => double.Parse((string)v.Attribute("z")!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(20, zs.Max(), 4);
        // Faces wind anticlockwise from outside, as 3MF wants: every triangle's normal points away from its box's middle.
        var v = objects[0].Descendants(ns + "vertex").Select(e => new Vec3(F(e, "x"), F(e, "y"), F(e, "z"))).ToList();
        var mid = new Vec3(0, 0, 10);
        foreach (var t in objects[0].Descendants(ns + "triangle"))
        {
            Vec3 a = v[(int)t.Attribute("v1")!], b = v[(int)t.Attribute("v2")!], c = v[(int)t.Attribute("v3")!];
            Assert.True(Vec3.Dot(Vec3.Cross(b - a, c - a), (a + b + c) / 3 - mid) > 0);
            Assert.Equal("1", (string?)t.Attribute("p1"));
        }
    }

    private static double F(XElement e, string n) => double.Parse((string)e.Attribute(n)!, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Round_trips_layers_surfaces_and_colours()
    {
        using var dir = new TempDir();
        string path = dir.File("boxes.3mf");
        var doc = TwoColouredBoxes();
        ObjectFiles.Save(doc, path);
        var back = ObjectFiles.Load(path);
        Assert.Equal(12, back.Layers[0].Mesh.Polygons.Count);
        Assert.Equal(8, back.Layers[0].Mesh.Points.Count);
        Assert.Equal(12, back.Layers[1].Mesh.Polygons.Count);
        Assert.Equal("Body", back.Layers[0].Name);
        Assert.Equal(new Rgb(200, 30, 30), back.Surfaces[back.Layers[0].Mesh.Polygons[0].Surface].Color);
        Assert.Equal("Blue", back.Surfaces[back.Layers[1].Mesh.Polygons[0].Surface].Name);
        foreach (var p in doc.Layers[0].Mesh.Points) Assert.Contains(back.Layers[0].Mesh.Points, q => Vec3.Near(p, q, 1e-6));
        // Facing out of their boxes, the Modeler's way.
        var m = back.Layers[0].Mesh;
        foreach (var poly in m.Polygons) Assert.True(Vec3.Dot(m.Normal(poly), m.Centroid(poly) - new Vec3(0, 0.01, 0)) > 0);
        Assert.All(back.Layers.Take(2), l => Assert.Equal(LayerState.Foreground, l.State));
    }

    [Fact]
    public void Files_from_elsewhere_in_inches_with_components_and_transforms_are_read()
    {
        using var dir = new TempDir();
        string path = dir.File("part.3mf");
        // One triangle in inches, used twice through components, the second moved 1 inch along X; the build lifts it 1 inch.
        string model = """
            <?xml version="1.0" encoding="UTF-8"?>
            <model unit="inch" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
              <resources>
                <object id="1" type="model"><mesh>
                  <vertices><vertex x="0" y="0" z="0"/><vertex x="1" y="0" z="0"/><vertex x="0" y="1" z="0"/></vertices>
                  <triangles><triangle v1="0" v2="1" v3="2"/></triangles>
                </mesh></object>
                <object id="2" type="model" name="Pair"><components>
                  <component objectid="1"/>
                  <component objectid="1" transform="1 0 0 0 1 0 0 0 1 1 0 0"/>
                </components></object>
              </resources>
              <build><item objectid="2" transform="1 0 0 0 1 0 0 0 1 0 0 1"/></build>
            </model>
            """;
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("3D/3dmodel.model").Open(), new UTF8Encoding(false)))
            w.Write(model);
        var doc = ObjectFiles.Load(path);
        var mesh = doc.Layers[0].Mesh;
        Assert.Equal("Pair", doc.Layers[0].Name);
        Assert.Equal(2, mesh.Polygons.Count);
        // An inch is 25.4 mm, 0.0254 m; lifted an inch, which is the Modeler's Y.
        Assert.All(mesh.Points, p => Assert.Equal(0.0254, p.Y, 9));
        Assert.Contains(mesh.Points, p => Vec3.Near(p, new Vec3(0.0508, 0.0254, 0), 1e-9));
        // Facing up (3MF's +Z, the Modeler's +Y).
        Assert.All(mesh.Polygons, p => Assert.True(mesh.Normal(p).Y > 0.99));

        File.WriteAllText(dir.File("bad.3mf"), "not a zip");
        Assert.ThrowsAny<Exception>(() => ObjectFiles.Load(dir.File("bad.3mf")));
    }
}
