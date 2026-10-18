using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

public class Lwo2Tests
{
    /// <summary>Two layers, tagged and untagged polygons, a skipped CURV chunk, and 4-byte VX indices.</summary>
    internal static byte[] TwoLayers() => Bytes.Form("LWO2", f => f
        .Chunk("TAGS", b => b.S0("Red").S0("Blue"))
        .Chunk("LAYR", b => b.U2(0).U2(0).Vec(1, 2, 3).S0("Base"))
        .Chunk("PNTS", b => b.Vec(0, 0, 0).Vec(0, 1, 0).Vec(1, 1, 0).Vec(1, 0, 0).Vec(2, 2, 2))
        .Chunk("BBOX", b => b.Vec(0, 0, 0).Vec(2, 2, 2))
        .Chunk("VMAP", b => b.Id("TXUV").U2(2).S0("uv").U2(0).F4(0).F4(0))
        .Chunk("POLS", b => b.Id("FACE")
            .U2(4).U2(0).U2(1).U2(2).U2(3)
            .U2(3).U2(0).U2(2).U2(4)
            .U2(1).U2(4))
        .Chunk("PTAG", b => b.Id("SURF").U2(0).U2(0).U2(1).U2(1))
        .Chunk("LAYR", b => b.U2(2).U2(1).Vec(0, 0, 0).S0("Top").I2(0))
        .Chunk("PNTS", b => b.Vec(5, 0, 0).Vec(5, 1, 0).Vec(6, 0, 0))
        .Chunk("POLS", b => b.Id("PTCH").U2(3).U1(0xFF, 0, 0, 0).U2(1).U1(0xFF, 0, 0, 2))
        .Chunk("PTAG", b => b.Id("SURF").U1(0xFF, 0, 0, 0).U2(1))
        .Chunk("POLS", b => b.Id("CURV").U2(2).U2(0).U2(1))
        .Chunk("PTAG", b => b.Id("SURF").U2(0).U2(0))
        .Chunk("SURF", b => b.S0("Red").S0("")
            .Sub("COLR", s => s.Vec(1, 0, 0).U2(0))
            .Sub("DIFF", s => s.F4(0.5f).U2(0))
            .Sub("SPEC", s => s.F4(0.25f).U2(0))
            .Sub("GLOS", s => s.F4(0.6f).U2(0))
            .Sub("SMAN", s => s.F4((float)(Math.PI / 2)))
            .Sub("SIDE", s => s.U2(3))
            .Sub("BLOK", s => s.Sub("IMAP", i => i.S0("x"))))
        .Chunk("SURF", b => b.S0("Blue").S0("")
            .Sub("COLR", s => s.Vec(0, 0, 1).U2(0))
            .Sub("LUMI", s => s.F4(0.75f).U2(0))
            .Sub("TRAN", s => s.F4(0.5f).U2(0))
            .Sub("REFL", s => s.F4(0.125f).U2(0))));

    [Fact]
    public void ReadsLayersPolygonsAndSurfaces()
    {
        var doc = Lwo2.Read(new MemoryStream(TwoLayers()));
        Assert.Equal(10, doc.Layers.Count);
        var base0 = doc.Layers[0];
        Assert.Equal(("Base", new Vec3(1, 2, 3), LayerState.Foreground), (base0.Name, base0.Pivot, base0.State));
        Assert.Equal(5, base0.Mesh.Points.Count);
        Assert.Equal([4, 3, 1], base0.Mesh.Polygons.Select(p => p.Count));
        Assert.Equal(["Red", "Blue", "Default"], doc.Surfaces.Select(s => s.Name));
        Assert.Equal([0, 1, 2], base0.Mesh.Polygons.Select(p => p.Surface));

        Assert.True(doc.Layers[1].Mesh.IsEmpty);
        var top = doc.Layers[2];
        Assert.Equal(("Top", LayerState.Off), (top.Name, top.State));
        var patch = Assert.Single(top.Mesh.Polygons);
        Assert.Equal([0, 1, 2], patch.Points);
        Assert.Equal(1, patch.Surface);

        var red = doc.Surfaces[0];
        Assert.Equal(new Rgb(255, 0, 0), red.Color);
        Assert.Equal((0.5, 0.25, true, true), (red.Diffuse, red.Specular, red.Smoothing, red.DoubleSided));
        Assert.Equal(64, red.Glossiness, 3);
        Assert.Equal(90, red.SmoothingAngle, 4);
        var blue = doc.Surfaces[1];
        Assert.Equal((0.75, 0.5, 0.125, false), (blue.Luminosity, blue.Transparency, blue.Reflection, blue.DoubleSided));
    }

    [Fact]
    public void ReadObjectSavesAsLwlo()
    {
        var doc = Lwo2.Read(TwoLayers());
        var back = Lwob.Read(Lwob.Write(doc));
        Assert.Equal("LWLO", Lwob.InfoOf(back)!.FormType);
        Assert.Equal(doc.Layers[2].Mesh.Points, back.Layers[2].Mesh.Points);
        Assert.Equal(doc.Surfaces.Select(s => s.Name), back.Surfaces.Select(s => s.Name));
    }

    [Fact]
    public void RejectsBadTagsAndIndices()
    {
        var badTag = Bytes.Form("LWO2", f => f
            .Chunk("TAGS", b => b.S0("A"))
            .Chunk("PNTS", b => b.Vec(0, 0, 0))
            .Chunk("POLS", b => b.Id("FACE").U2(1).U2(0))
            .Chunk("PTAG", b => b.Id("SURF").U2(0).U2(5)));
        Assert.Contains("PTAG", Assert.Throws<FormatException>(() => Lwo2.Read(badTag)).Message);

        var badPoint = Bytes.Form("LWO2", f => f
            .Chunk("PNTS", b => b.Vec(0, 0, 0))
            .Chunk("POLS", b => b.Id("FACE").U2(2).U2(0).U1(0xFF, 0, 1, 0)));
        Assert.Contains("POLS", Assert.Throws<FormatException>(() => Lwo2.Read(badPoint)).Message);

        Assert.Throws<FormatException>(() => Lwo2.Read(LwobTests.Legacy()));
    }
}
