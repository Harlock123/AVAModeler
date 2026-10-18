using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

public class ObjectFilesTests
{
    private static Document Sample()
    {
        var doc = new Document();
        doc.Surfaces.Add(new Surface("Paint") { Color = new Rgb(255, 85, 85) });
        doc.Layers[0].Mesh.AddFace(1, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0), new Vec3(1, 0, 0));
        doc.Layers[0].Mesh.AddPolygon([0, 2]);
        return doc;
    }

    [Theory]
    [InlineData("thing.lwo")]
    [InlineData("thing.LW")]
    [InlineData("thing.obj")]
    [InlineData("thing.geo")]
    public void SavesAndLoadsByExtension(string name)
    {
        using var dir = new TempDir();
        string path = dir.File(name);
        var doc = Sample();
        ObjectFiles.Save(doc, path);
        var back = ObjectFiles.Load(path);
        Assert.Equal(path, back.FilePath);
        Assert.Equal(doc.Layers[0].Mesh.Points, back.Layers[0].Mesh.Points);
        Assert.Equal(doc.Layers[0].Mesh.Polygons.Select(p => p.Points), back.Layers[0].Mesh.Polygons.Select(p => p.Points));
        Assert.Equal(new Rgb(255, 85, 85), back.Surfaces[back.Layers[0].Mesh.Polygons[0].Surface].Color);
        Assert.DoesNotContain(Directory.GetFiles(dir.Path), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void ObjSaveWritesMaterialsBeside()
    {
        using var dir = new TempDir();
        ObjectFiles.Save(Sample(), dir.File("ship.obj"));
        Assert.Contains("newmtl Paint", File.ReadAllText(dir.File("ship.mtl")));
        Assert.StartsWith("# Written by AVAModeler\nmtllib ship.mtl\n", File.ReadAllText(dir.File("ship.obj")));
    }

    [Fact]
    public void LoadGoesByContentsBeforeExtension()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("misnamed.obj"), LwobTests.Legacy());
        Assert.Equal(10, ObjectFiles.Load(dir.File("misnamed.obj")).Layers[0].Mesh.Points.Count);
        File.WriteAllBytes(dir.File("new.dat"), Lwo2Tests.TwoLayers());
        Assert.Equal("Top", ObjectFiles.Load(dir.File("new.dat")).Layers[2].Name);
        File.WriteAllText(dir.File("scape.txt"), VideoScapeTests.Sample);
        Assert.Equal(5, ObjectFiles.Load(dir.File("scape.txt")).Layers[0].Mesh.Points.Count);
    }

    [Fact]
    public void UnknownOrWrongFilesFailClearly()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("notes.txt"), "hello");
        Assert.Throws<NotSupportedException>(() => ObjectFiles.Load(dir.File("notes.txt")));
        File.WriteAllText(dir.File("fake.lwo"), "hello");
        Assert.Contains("FORM", Assert.Throws<FormatException>(() => ObjectFiles.Load(dir.File("fake.lwo"))).Message);
        File.WriteAllBytes(dir.File("picture.lwo"), Bytes.Form("ILBM", _ => { }));
        Assert.Contains("ILBM", Assert.Throws<FormatException>(() => ObjectFiles.Load(dir.File("picture.lwo"))).Message);
        Assert.Throws<NotSupportedException>(() => ObjectFiles.Save(new Document(), dir.File("out.3ds")));
    }

    [Fact]
    public void ExtensionListsCoverTheFormats()
    {
        Assert.Equal([".lwo", ".lw", ".geo", ".obj"], ObjectFiles.LoadExtensions);
        Assert.Equal(".lwo", ObjectFiles.SaveExtensions[0]);
        Assert.All(ObjectFiles.LoadExtensions.Concat(ObjectFiles.SaveExtensions), e => Assert.NotEmpty(ObjectFiles.Describe(e)));
    }
}
