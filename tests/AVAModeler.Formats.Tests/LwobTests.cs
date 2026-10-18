using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

public class LwobTests
{
    /// <summary>
    /// A legacy object as LightWave 5 might have written it, with things this reader only carries along: unknown
    /// top-level chunks (one odd-length, one between known chunks), texture sub-chunks, an odd-length sub-chunk,
    /// a SURF for a name not in SRFS, and an unused surface.
    /// </summary>
    internal static byte[] Legacy() => Bytes.Form("LWOB", f => f
        .Chunk("PNTS", b =>
        {
            for (int i = 0; i < 10; i++) b.Vec(i * 0.5f, i % 3, -i * 0.25f);
        })
        .Chunk("ZZZZ", b => b.U1(1, 2, 3, 4, 5))
        .Chunk("SRFS", b => b.S0("Hull").S0("Glass").S0("Unused").S0("Odd"))
        .Chunk("POLS", b => b
            .U2(4).U2(0).U2(1).U2(2).U2(3).I2(1)
            .U2(3).U2(4).U2(5).U2(6).I2(2)
            .U2(1).U2(7).I2(1)
            .U2(2).U2(8).U2(9).I2(4)
            // A quad with two detail polygons: a triangle and a line.
            .U2(4).U2(0).U2(3).U2(6).U2(9).I2(-1).I2(2)
            .U2(3).U2(0).U2(3).U2(6).I2(2)
            .U2(2).U2(3).U2(6).I2(4))
        .Chunk("SURF", b => b.S0("Hull")
            .Sub("COLR", s => s.U1(200, 100, 50, 0))
            .Sub("FLAG", s => s.U2(4 | 256 | 8))
            .Sub("DIFF", s => s.U2(128))
            .Sub("VDIF", s => s.F4(0.625f))
            .Sub("SPEC", s => s.U2(64))
            .Sub("GLOS", s => s.U2(256))
            .Sub("SMAN", s => s.F4((float)(Math.PI / 4)))
            .Sub("CTEX", s => s.S0("Planar Image Map"))
            .Sub("TFLG", s => s.U2(2))
            .Sub("TIMG", s => s.U1(65, 66, 0)))
        .Chunk("SURF", b => b.S0("Glass")
            .Sub("COLR", s => s.U1(10, 20, 30, 0))
            .Sub("FLAG", s => s.U2(0))
            .Sub("LUMI", s => s.U2(32))
            .Sub("TRAN", s => s.U2(192))
            .Sub("VTRN", s => s.F4(0.8f))
            .Sub("REFL", s => s.U2(25))
            .Sub("XYZW", s => s.U1(9, 9)))
        .Chunk("SURF", b => b.S0("Ghost").Sub("COLR", s => s.U1(1, 1, 1, 0)))
        .Chunk("SURF", b => b.S0("Unused").Sub("COLR", s => s.U1(0, 255, 0, 0)))
        .Chunk("CRVS", b => b.U2(7)));

    [Fact]
    public void LegacyObjectWritesBackByteIdentical()
    {
        var data = Legacy();
        var doc = Lwob.Read(data);
        Assert.Equal(data, Lwob.Write(doc));
    }

    [Fact]
    public void LegacyObjectReadsGeometryAndSurfaces()
    {
        var doc = Lwob.Read(Legacy());
        var mesh = doc.Layers[0].Mesh;
        Assert.Equal(LayerState.Foreground, doc.Layers[0].State);
        Assert.Equal(10, mesh.Points.Count);
        Assert.Equal(new Vec3(1.5, 0, -0.75), mesh.Points[3]);
        Assert.Equal(["Hull", "Glass", "Unused", "Odd"], doc.Surfaces.Select(s => s.Name));
        Assert.Equal([4, 3, 1, 2, 4], mesh.Polygons.Select(p => p.Count));
        Assert.Equal([0, 1, 0, 3, 0], mesh.Polygons.Select(p => p.Surface));

        var info = Lwob.InfoOf(doc)!;
        Assert.Equal("LWOB", info.FormType);
        Assert.Equal(["ZZZZ", "SURF", "CRVS"], info.UnknownChunks.Select(c => c.Id));

        var hull = doc.Surfaces[0];
        Assert.Equal(new Rgb(200, 100, 50), hull.Color);
        Assert.True(hull.Smoothing);
        Assert.True(hull.DoubleSided);
        Assert.Equal(0.625, hull.Diffuse);
        Assert.Equal(0.25, hull.Specular);
        Assert.Equal(256, hull.Glossiness);
        Assert.Equal(45, hull.SmoothingAngle, 4);
        Assert.Equal(["COLR", "FLAG", "DIFF", "VDIF", "SPEC", "GLOS", "SMAN", "CTEX", "TFLG", "TIMG"], hull.Raw!.Select(c => c.Id));
        Assert.Equal(hull.Signature(), hull.RawSignature);

        var glass = doc.Surfaces[1];
        Assert.Equal(0.125, glass.Luminosity);
        Assert.Equal(0.8f, (float)glass.Transparency);
        Assert.Equal(25 / 256.0, glass.Reflection);
        Assert.False(glass.Smoothing);
        Assert.Null(doc.Surfaces[3].Raw);
    }

    [Fact]
    public void DetailPolygonsAreKept()
    {
        var doc = Lwob.Read(Legacy());
        var parent = doc.Layers[0].Mesh.Polygons[4];
        Assert.Null(doc.Layers[0].Mesh.Polygons[0].Details);
        Assert.NotNull(parent.Details);
        Assert.Equal(2, parent.Details.Count);
        Assert.Equal([0, 3, 6], parent.Details[0].Points);
        Assert.Equal(1, parent.Details[0].Surface);
        Assert.Equal([3, 6], parent.Details[1].Points);
        Assert.Equal(3, parent.Details[1].Surface);

        var built = new Document();
        var mesh = built.Layers[0].Mesh;
        for (int i = 0; i < 4; i++) mesh.AddPoint(new Vec3(i, i * i, 1));
        mesh.AddPolygon([0, 1, 2, 3]);
        mesh.Polygons[0].Details = [new Polygon([0, 1, 2]), new Polygon([2, 3])];
        mesh.Polygons.Add(new Polygon([3, 2, 1]) { Details = [] });
        var back = Lwob.Read(Lwob.Write(built)).Layers[0].Mesh;
        Assert.Equal([[0, 1, 2], [2, 3]], back.Polygons[0].Details!.Select(d => d.Points));
        Assert.Empty(back.Polygons[1].Details!);
    }

    [Fact]
    public void DocumentFromScratchWritesValidLwob()
    {
        var doc = new Document();
        doc.Surfaces[0].Color = new Rgb(1, 2, 3);
        doc.Surfaces.Add(new Surface("Shiny") { Specular = 0.5, Glossiness = 1024, Smoothing = true, SmoothingAngle = 30, DoubleSided = true });
        doc.Surfaces.Add(new Surface("Spare"));
        var mesh = doc.Layers[0].Mesh;
        mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0), new Vec3(1, 0, 0));
        mesh.AddFace(1, new Vec3(0, 0, 1), new Vec3(0.5, 1, 1), new Vec3(1, 0, 1));
        mesh.AddPolygon([0, 4], 1);
        mesh.AddPolygon([6]);

        var data = Lwob.Write(doc);
        Assert.Equal("FORM", System.Text.Encoding.ASCII.GetString(data, 0, 4));
        Assert.Equal(data.Length - 8, data[4] << 24 | data[5] << 16 | data[6] << 8 | data[7]);
        Assert.Equal("LWOB", System.Text.Encoding.ASCII.GetString(data, 8, 4));
        Assert.Equal(0, data.Length % 2);

        var back = Lwob.Read(data);
        Assert.Equal(mesh.Points, back.Layers[0].Mesh.Points);
        Assert.Equal(mesh.Polygons.Select(p => (p.Points, p.Surface)), back.Layers[0].Mesh.Polygons.Select(p => (p.Points, p.Surface)));
        Assert.Equal(["Default", "Shiny", "Spare"], back.Surfaces.Select(s => s.Name));
        Assert.Equal(new Rgb(1, 2, 3), back.Surfaces[0].Color);
        var shiny = back.Surfaces[1];
        Assert.Equal((0.5, 1024.0, true, true), (shiny.Specular, shiny.Glossiness, shiny.Smoothing, shiny.DoubleSided));
        Assert.Equal(30, shiny.SmoothingAngle, 4);
        // Writing what was read changes nothing.
        Assert.Equal(data, Lwob.Write(back));
    }

    [Fact]
    public void SurfaceFieldsMapToSubchunksBothWays()
    {
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, Vec3.Zero, Vec3.UnitY, Vec3.UnitX);
        var s = doc.Surfaces[0];
        (s.Color, s.Diffuse, s.Specular, s.Luminosity, s.Reflection, s.Transparency) = (new Rgb(255, 128, 0), 0.75, 0.5, 0.25, 0.125, 0.375);
        (s.Glossiness, s.Smoothing, s.SmoothingAngle, s.DoubleSided) = (16, true, 60, true);

        var back = Lwob.Read(Lwob.Write(doc)).Surfaces[0];
        var raw = back.Raw!.ToDictionary(c => c.Id, c => c.Data);
        Assert.Equal(["COLR", "FLAG", "LUMI", "VLUM", "DIFF", "VDIF", "SPEC", "VSPC", "GLOS", "REFL", "VRFL", "TRAN", "VTRN", "SMAN"], back.Raw!.Select(c => c.Id));
        Assert.Equal(new byte[] { 255, 128, 0, 0 }, raw["COLR"]);
        Assert.Equal(new byte[] { 1, 5 }, raw["FLAG"]);
        Assert.Equal(new byte[] { 0, 64 }, raw["LUMI"]);
        Assert.Equal(new byte[] { 0, 192 }, raw["DIFF"]);
        Assert.Equal(new byte[] { 0, 16 }, raw["GLOS"]);
        Assert.Equal((s.Color, s.Diffuse, s.Specular, s.Luminosity, s.Reflection, s.Transparency),
            (back.Color, back.Diffuse, back.Specular, back.Luminosity, back.Reflection, back.Transparency));
        Assert.Equal((16.0, true, true), (back.Glossiness, back.Smoothing, back.DoubleSided));
        Assert.Equal(60, back.SmoothingAngle, 4);

        // A surface with nothing special leaves out the optional sub-chunks.
        var plain = new Document();
        Assert.Equal(["COLR", "FLAG", "DIFF", "VDIF"], Lwob.Read(Lwob.Write(plain)).Surfaces[0].Raw!.Select(c => c.Id));
    }

    [Fact]
    public void EditedSurfaceIsRegeneratedKeepingOtherSubchunks()
    {
        var doc = Lwob.Read(Legacy());
        var hull = doc.Surfaces[0];
        hull.Color = new Rgb(9, 8, 7);
        hull.Smoothing = false;
        hull.Luminosity = 0.5;
        var glassRaw = doc.Surfaces[1].Raw!;

        var back = Lwob.Read(Lwob.Write(doc));
        var newHull = back.Surfaces[0];
        Assert.Equal(new Rgb(9, 8, 7), newHull.Color);
        Assert.False(newHull.Smoothing);
        Assert.Equal(0.5, newHull.Luminosity);
        Assert.Equal(0.625, newHull.Diffuse);
        Assert.Equal(
            ["COLR", "FLAG", "DIFF", "VDIF", "SPEC", "GLOS", "SMAN", "LUMI", "VLUM", "VSPC", "CTEX", "TFLG", "TIMG"],
            newHull.Raw!.Select(c => c.Id));
        // FLAG bits the typed fields don't cover (8, colour highlights) survive.
        Assert.Equal(new byte[] { 1, 9 }, newHull.Raw!.Single(c => c.Id == "FLAG").Data);
        Assert.Equal(new byte[] { 65, 66, 0 }, newHull.Raw!.Single(c => c.Id == "TIMG").Data);
        Assert.Equal(glassRaw.Select(c => (c.Id, Convert.ToHexString(c.Data))), back.Surfaces[1].Raw!.Select(c => (c.Id, Convert.ToHexString(c.Data))));
    }

    [Fact]
    public void GeometryEditsKeepChunkOrderAndUnknownChunks()
    {
        var doc = Lwob.Read(Legacy());
        doc.Layers[0].Mesh.AddFace(2, new Vec3(5, 5, 5), new Vec3(5, 6, 5), new Vec3(6, 5, 5));
        var data = Lwob.Write(doc);
        var back = Lwob.Read(data);
        Assert.Equal(13, back.Layers[0].Mesh.Points.Count);
        Assert.Equal(["ZZZZ", "SURF", "CRVS"], Lwob.InfoOf(back)!.UnknownChunks.Select(c => c.Id));
        Assert.Equal(data, Lwob.Write(back));
    }

    [Fact]
    public void LayoutThatNoLongerFitsFallsBackToStandardOrder()
    {
        var doc = Lwob.Read(Legacy());
        doc.Layers[3].Mesh.AddFace(0, Vec3.Zero, Vec3.UnitY, Vec3.UnitX);
        doc.Surfaces.Add(new Surface("New"));
        var back = Lwob.Read(Lwob.Write(doc));
        Assert.Equal("LWLO", Lwob.InfoOf(back)!.FormType);
        Assert.Equal(10, back.Layers[0].Mesh.Points.Count);
        Assert.Equal(3, back.Layers[3].Mesh.Points.Count);
        Assert.Equal(["Hull", "Glass", "Unused", "Odd", "New"], back.Surfaces.Select(s => s.Name));
        Assert.Equal(["ZZZZ", "SURF", "CRVS"], Lwob.InfoOf(back)!.UnknownChunks.Select(c => c.Id));
    }

    internal static byte[] Layered() => Bytes.Form("LWLO", f => f
        .Chunk("LAYR", b => b.U2(1).U2(0).S0("Body"))
        .Chunk("PNTS", b => b.Vec(0, 0, 0).Vec(0, 1, 0).Vec(1, 0, 0))
        .Chunk("SRFS", b => b.S0("A").S0("B"))
        .Chunk("POLS", b => b.U2(3).U2(0).U2(1).U2(2).I2(1))
        .Chunk("LAYR", b => b.U2(3).U2(1).S0("Wing"))
        .Chunk("PNTS", b => b.Vec(2, 0, 0).Vec(2, 1, 0))
        .Chunk("POLS", b => b.U2(2).U2(0).U2(1).I2(2))
        .Chunk("SURF", b => b.S0("A").Sub("COLR", s => s.U1(1, 2, 3, 0)))
        .Chunk("SURF", b => b.S0("B").Sub("COLR", s => s.U1(4, 5, 6, 0))));

    [Fact]
    public void LwloLayersReadAndWriteBack()
    {
        var data = Layered();
        var doc = Lwob.Read(data);
        Assert.Equal(10, doc.Layers.Count);
        Assert.True(doc.Layers[0].Mesh.IsEmpty);
        Assert.Equal(("Body", 3, LayerState.Foreground), (doc.Layers[1].Name, doc.Layers[1].Mesh.Points.Count, doc.Layers[1].State));
        Assert.Equal(("Wing", 2, LayerState.Off), (doc.Layers[3].Name, doc.Layers[3].Mesh.Points.Count, doc.Layers[3].State));
        Assert.Equal([0, 1], doc.Layers[3].Mesh.Polygons[0].Points);
        Assert.Equal(1, doc.Layers[3].Mesh.Polygons[0].Surface);
        Assert.Equal((ushort)1, Lwob.InfoOf(doc)!.LayerFlags[3]);
        Assert.Equal(data, Lwob.Write(doc));
    }

    [Fact]
    public void LayerCountChoosesLwobOrLwlo()
    {
        var doc = new Document();
        doc.Layers[2].Mesh.AddFace(0, Vec3.Zero, Vec3.UnitY, Vec3.UnitX);
        doc.Layers[2].Name = "Only";
        var one = Lwob.Read(Lwob.Write(doc));
        Assert.Equal("LWOB", Lwob.InfoOf(one)!.FormType);
        Assert.Equal(3, one.Layers[0].Mesh.Points.Count);

        doc.Layers[5].Mesh.AddFace(0, Vec3.UnitZ, Vec3.One, Vec3.UnitX);
        doc.Layers[5].Name = "Second";
        var two = Lwob.Read(Lwob.Write(doc));
        Assert.Equal("LWLO", Lwob.InfoOf(two)!.FormType);
        Assert.Equal(("Only", 3), (two.Layers[2].Name, two.Layers[2].Mesh.Points.Count));
        Assert.Equal(("Second", Vec3.One), (two.Layers[5].Name, two.Layers[5].Mesh.Points[1]));
        Assert.Equal(LayerState.Foreground, two.Layers[2].State);
    }

    [Fact]
    public void WriterRejectsSurfacesOutOfRange()
    {
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(3, Vec3.Zero, Vec3.UnitY, Vec3.UnitX);
        Assert.Throws<InvalidOperationException>(() => Lwob.Write(doc));
    }
}
