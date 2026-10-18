using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

public class VideoScapeTests
{
    internal const string Sample = """
        3DG1
        5
        0 0 0
        0 1 0
        1 1 0
        1 0 0
        0.5 0.5 -2.5
        4 0 1 2 3 2
        3 0 1 4 12
        -4 0 1 2 3 2
        2
        3 0 1 2 15
        2 2 3 15
        1 4 2
        """;

    [Fact]
    public void ReadsPointsPolygonsColoursAndDetails()
    {
        var doc = VideoScape.Read(new StringReader(Sample));
        var mesh = doc.Layers[0].Mesh;
        Assert.Equal(5, mesh.Points.Count);
        Assert.Equal(new Vec3(0.5, 0.5, -2.5), mesh.Points[4]);
        Assert.Equal(["VideoScape 2", "VideoScape 12", "VideoScape 15"], doc.Surfaces.Select(s => s.Name));
        Assert.Equal(VideoScape.Palette[12], doc.Surfaces[1].Color);
        Assert.Equal([4, 3, 4, 1], mesh.Polygons.Select(p => p.Count));
        Assert.Equal([0, 1, 0, 0], mesh.Polygons.Select(p => p.Surface));
        var details = mesh.Polygons[2].Details!;
        Assert.Equal([[0, 1, 2], [2, 3]], details.Select(d => d.Points));
        Assert.All(details, d => Assert.Equal(2, d.Surface));
    }

    [Fact]
    public void WritesAndReadsBack()
    {
        var doc = VideoScape.Read(new StringReader(Sample));
        doc.Surfaces.Add(new Surface("Reddish") { Color = new Rgb(250, 90, 80) });
        doc.Layers[3].Mesh.AddFace(3, new Vec3(9, 9, 9), new Vec3(9, 8, 9), new Vec3(8, 9, 9));
        var text = new StringWriter();
        VideoScape.Write(doc, text);
        var back = VideoScape.Read(new StringReader(text.ToString()));
        var mesh = back.Layers[0].Mesh;
        Assert.Equal(8, mesh.Points.Count);
        Assert.Equal(new Vec3(8, 9, 9), mesh.Points[7]);
        Assert.Equal([[0, 1, 2], [2, 3]], mesh.Polygons[2].Details!.Select(d => d.Points));
        Assert.Equal("VideoScape 12", back.Surfaces[mesh.Polygons[4].Surface].Name);
    }

    [Theory]
    [InlineData("3DG2\n0\n", "3DG2")]
    [InlineData("GOUR\n0\n", "GOUR")]
    [InlineData("HELLO\n", "HELLO")]
    [InlineData("3DG1\n2\n0 0 0\n1 1\n", "ends")]
    [InlineData("3DG1\n1\n0 0 zero\n", "line 3")]
    [InlineData("3DG1\n1\n0 0 0\n2 0 1 3\n", "point 1")]
    [InlineData("3DG1\n1\n0 0 0\n1 0 -3\n", "colour")]
    public void RejectsBadFiles(string text, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => VideoScape.Read(new StringReader(text))).Message);
}
