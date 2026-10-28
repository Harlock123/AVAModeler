using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

public class LwobTextureTests
{
    /// <summary>
    /// A LightWave 5 object whose surface has three textures: a cylindrical colour image map with every setting, a
    /// procedural diffuse texture, and a planar bump map, with sub-chunks this reader doesn't model mixed in.
    /// </summary>
    internal static byte[] Textured() => Bytes.Form("LWOB", f => f
        .Chunk("PNTS", b => b.Vec(0, 0, 0).Vec(1, 0, 0).Vec(1, 1, 0).Vec(0, 1, 0))
        .Chunk("SRFS", b => b.S0("Can"))
        .Chunk("POLS", b => b.U2(4).U2(0).U2(1).U2(2).U2(3).I2(1))
        .Chunk("SURF", b => b.S0("Can")
            .Sub("COLR", s => s.U1(200, 200, 200, 0))
            .Sub("FLAG", s => s.U2(4))
            .Sub("DIFF", s => s.U2(230))
            .Sub("CTEX", s => s.S0("Cylindrical Image Map"))
            .Sub("TFLG", s => s.U2(2 | 32))
            .Sub("TSIZ", s => s.Vec(0.5f, 2, 0.5f))
            .Sub("TCTR", s => s.Vec(0, 1, 0))
            .Sub("TFAL", s => s.Vec(0, 0, 0))
            .Sub("TVEL", s => s.Vec(0, 0, 0))
            .Sub("TIMG", s => s.S0("Images/label.png"))
            .Sub("TWRP", s => s.U2(2).U2(1))
            .Sub("TFP0", s => s.F4(2))
            .Sub("TAAS", s => s.F4(1))
            .Sub("TOPC", s => s.F4(0.75f))
            .Sub("DTEX", s => s.S0("Fractal Noise"))
            .Sub("TFLG", s => s.U2(4))
            .Sub("TSIZ", s => s.Vec(0.1f, 0.1f, 0.1f))
            .Sub("TFP0", s => s.F4(0.5f))
            .Sub("TIP0", s => s.U2(3))
            .Sub("TVAL", s => s.U2(128))
            .Sub("BTEX", s => s.S0("Planar Image Map"))
            .Sub("TFLG", s => s.U2(4 | 16))
            .Sub("TSIZ", s => s.Vec(1, 1, 1))
            .Sub("TCTR", s => s.Vec(0.5f, 0.5f, 0))
            .Sub("TIMG", s => s.S0("dents.bmp"))
            .Sub("TWRP", s => s.U2(3).U2(0))
            .Sub("TAMP", s => s.F4(2.5f))
            .Sub("EDGE", s => s.F4(0.5f))));

    [Fact]
    public void Texture_sub_chunks_read_into_layers_and_write_back_byte_identical()
    {
        var data = Textured();
        var doc = Lwob.Read(data);
        Assert.Equal(data, Lwob.Write(doc));
        var t = doc.Surfaces[0].Textures;
        Assert.Equal([TextureChannel.Color, TextureChannel.Diffuse, TextureChannel.Bump], t.Select(x => x.Channel));

        var label = t[0];
        Assert.Equal(TextureProjection.Cylindrical, label.Projection);
        Assert.Equal(Axis.Y, label.Axis);
        Assert.True(label.PixelBlending);
        Assert.False(label.Negative);
        Assert.Equal(new Vec3(0.5, 2, 0.5), label.Size);
        Assert.Equal(new Vec3(0, 1, 0), label.Center);
        Assert.Equal("Images/label.png", label.Image);
        Assert.Equal((TextureTiling.Repeat, TextureTiling.Edge), (label.WidthTiling, label.HeightTiling));
        Assert.Equal(2, label.WidthWrap);
        Assert.Equal(0.75, label.Opacity);
        Assert.Equal(["CTEX", "TFLG", "TSIZ", "TCTR", "TFAL", "TVEL", "TIMG", "TWRP", "TFP0", "TAAS", "TOPC"], label.Raw!.Select(c => c.Id));

        var noise = t[1];
        Assert.Equal("Fractal Noise", noise.Procedural);
        Assert.False(noise.IsImage);
        Assert.Equal(0.5, noise.Value);

        var dents = t[2];
        Assert.Equal((TextureProjection.Planar, Axis.Z, true), (dents.Projection, dents.Axis, dents.Negative));
        Assert.False(dents.PixelBlending);
        Assert.Equal((TextureTiling.Mirror, TextureTiling.Reset), (dents.WidthTiling, dents.HeightTiling));
        Assert.Equal(2.5, dents.Amplitude);
    }

    [Fact]
    public void Editing_a_surface_keeps_its_textures_sub_chunks_and_editing_a_texture_keeps_what_it_doesnt_model()
    {
        var doc = Lwob.Read(Textured());
        var original = Lwob.Read(Textured()).Surfaces[0].Raw!.SkipWhile(c => c.Id != "CTEX").ToList();
        var can = doc.Surfaces[0];
        List<RawChunk> Textures(Surface s) => s.Raw!.SkipWhile(c => c.Id != "CTEX").ToList();

        // A colour change rewrites the surface's own settings only: every texture sub-chunk comes back as it was.
        can.Color = new Rgb(1, 2, 3);
        var back = Lwob.Read(Lwob.Write(doc)).Surfaces[0];
        Assert.Equal(new Rgb(1, 2, 3), back.Color);
        Assert.Equal(original.Select(c => (c.Id, c.Data)), Textures(back).Select(c => (c.Id, c.Data)));

        // A texture change rewrites that texture's settings in place, keeping TFAL and TVEL.
        can.Textures[0].Size = new Vec3(1, 3, 1);
        can.Textures[0].Image = "Images/other.png";
        can.Textures[0].WidthTiling = TextureTiling.Mirror;
        var again = Lwob.Read(Lwob.Write(doc)).Surfaces[0];
        Assert.Equal(original.Select(c => c.Id), Textures(again).Select(c => c.Id));
        Assert.Equal(new Vec3(1, 3, 1), again.Textures[0].Size);
        Assert.Equal("Images/other.png", again.Textures[0].Image);
        Assert.Equal(TextureTiling.Mirror, again.Textures[0].WidthTiling);
        foreach (var id in new[] { "TFAL", "TVEL", "DTEX", "TIP0", "TVAL", "BTEX", "TAMP" })
            Assert.Equal(original.First(c => c.Id == id).Data, Textures(again).First(c => c.Id == id).Data);

        // A procedural texture's centre is added to its sub-chunks, the rest left alone.
        can.Textures[1].Center = new Vec3(0, 0, 5);
        var third = Lwob.Read(Lwob.Write(doc)).Surfaces[0];
        Assert.Equal(new Vec3(0, 0, 5), third.Textures[1].Center);
        Assert.Equal("Fractal Noise", third.Textures[1].Procedural);
        Assert.Equal(["DTEX", "TFLG", "TSIZ", "TFP0", "TIP0", "TVAL", "TCTR"], third.Textures[1].Raw!.Select(c => c.Id));
        Assert.Equal(2.5, third.Textures[2].Amplitude);

        // Removing a texture takes all its sub-chunks with it.
        can.Textures.RemoveAt(1);
        var fewer = Lwob.Read(Lwob.Write(doc)).Surfaces[0];
        Assert.Equal([TextureChannel.Color, TextureChannel.Bump], fewer.Textures.Select(t => t.Channel));
        Assert.DoesNotContain(fewer.Raw!, c => c.Id is "TIP0" or "DTEX");
        Assert.Equal("EDGE", fewer.Raw![^1].Id);
    }

    [Fact]
    public void New_textures_and_uv_maps_write_and_read_back()
    {
        var doc = new Document();
        var mesh = doc.Layers[0].Mesh;
        mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(0, 1, 0));
        mesh.AddFace(0, new Vec3(1, 0, 0), new Vec3(2, 0, 0), new Vec3(2, 1, 0), new Vec3(1, 1, 0));
        for (int i = 0; i < 8; i++) mesh.SetPointUV("UV", i, new Vec2(mesh.Points[i].X / 2, mesh.Points[i].Y));
        mesh.SetCornerUV("UV", mesh.Polygons[1], 4, new Vec2(0.25, 0.75));
        mesh.SetPointUV("Decal", 2, new Vec2(1, 1));
        doc.Surfaces[0].Textures.Add(new TextureLayer { Channel = TextureChannel.Color, Projection = TextureProjection.UV, UVMap = "UV", Image = "brick.png", Opacity = 0.5 });
        doc.Surfaces[0].Textures.Add(new TextureLayer { Channel = TextureChannel.Bump, Projection = TextureProjection.Spherical, Axis = Axis.X, WidthWrap = 2, HeightWrap = 3, Image = "bumps.png", Amplitude = 0.5 });

        var data = Lwob.Write(doc);
        var back = Lwob.Read(data);
        Assert.Equal(data, Lwob.Write(back));
        var bm = back.Layers[0].Mesh;
        Assert.Equal(["UV", "Decal"], bm.UVMaps.Select(m => m.Name));
        for (int i = 0; i < 8; i++) Assert.Equal(mesh.FindUVMap("UV")!.Points[i], bm.FindUVMap("UV")!.Points[i]);
        Assert.Equal(new Vec2(0.25, 0.75), bm.Polygons[1].CornerUVs!["UV"][4]);
        var t = back.Surfaces[0].Textures;
        Assert.Equal((TextureProjection.UV, "UV", "brick.png", 0.5), (t[0].Projection, t[0].UVMap, t[0].Image, t[0].Opacity));
        Assert.Equal((TextureProjection.Spherical, Axis.X, 2.0, 3.0, 0.5), (t[1].Projection, t[1].Axis, t[1].WidthWrap, t[1].HeightWrap, t[1].Amplitude));
    }

    [Fact]
    public void Uv_maps_in_layered_objects_follow_their_layer_and_a_read_object_keeps_its_layout()
    {
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, Vec3.Zero, Vec3.UnitX, Vec3.UnitY);
        doc.Layers[2].Mesh.AddFace(0, Vec3.Zero, Vec3.UnitZ, Vec3.UnitY, Vec3.One);
        doc.Layers[2].Mesh.SetPointUV("UV", 3, new Vec2(0.5, 0.5));
        var data = Lwob.Write(doc);
        var back = Lwob.Read(data);
        Assert.Empty(back.Layers[0].Mesh.UVMaps);
        Assert.Equal(new Vec2(0.5, 0.5), back.Layers[2].Mesh.FindUVMap("UV")!.Points[3]);
        Assert.Equal(data, Lwob.Write(back));

        // A seam added to a read object goes in after the layer's maps.
        var m = back.Layers[2].Mesh;
        m.SetCornerUV("UV", m.Polygons[0], 3, new Vec2(0.875, 0.125));
        m.SetPointUV("UV", 0, new Vec2(0, 0));
        var again = Lwob.Read(Lwob.Write(back)).Layers[2].Mesh;
        Assert.Equal(new Vec2(0.875, 0.125), again.Polygons[0].CornerUVs!["UV"][3]);
        Assert.Equal(new Vec2(0, 0), again.FindUVMap("UV")!.Points[0]);

        // And a read object without UVs that gets some writes them after its polygons.
        var plain = Lwob.Read(LwobTests.Legacy());
        plain.Layers[0].Mesh.SetPointUV("UV", 1, new Vec2(1, 0));
        var withUVs = Lwob.Read(Lwob.Write(plain));
        Assert.Equal(new Vec2(1, 0), withUVs.Layers[0].Mesh.FindUVMap("UV")!.Points[1]);
        Assert.Equal(["ZZZZ", "SURF", "CRVS"], Lwob.InfoOf(withUVs)!.UnknownChunks.Select(c => c.Id));
    }

    [Fact]
    public void Textures_and_uvs_that_cant_be_read_are_kept_or_left_out_but_the_object_opens()
    {
        var data = Bytes.Form("LWOB", f => f
            .Chunk("PNTS", b => b.Vec(0, 0, 0).Vec(1, 0, 0).Vec(0, 1, 0))
            .Chunk("SRFS", b => b.S0("Odd"))
            .Chunk("POLS", b => b.U2(3).U2(0).U2(1).U2(2).I2(1))
            .Chunk("VMAP", b => b.Id("TXUV").U2(2).S0("UV").U2(9).F4(0).F4(0))
            .Chunk("SURF", b => b.S0("Odd").Sub("COLR", s => s.U1(1, 2, 3, 0)).Sub("CTEX", s => s.U1(65, 66)).Sub("TSIZ", s => s.U1(1))));
        var doc = Lwob.Read(data);
        Assert.Equal(data, Lwob.Write(doc));
        Assert.Equal("(unreadable)", Assert.Single(doc.Surfaces[0].Textures).Procedural);
        Assert.Empty(doc.Layers[0].Mesh.UVMaps);
        Assert.Equal(["VMAP"], Lwob.InfoOf(doc)!.UnknownChunks.Select(c => c.Id));
    }

    [Fact]
    public void The_legacy_objects_planar_map_is_read()
    {
        var hull = Lwob.Read(LwobTests.Legacy()).Surfaces[0];
        var t = Assert.Single(hull.Textures);
        Assert.Equal((TextureChannel.Color, TextureProjection.Planar, Axis.Y, "AB"), (t.Channel, t.Projection, t.Axis, t.Image));
    }
}

public class Lwo2TextureTests
{
    /// <summary>A quad with a UV map, a seam on one corner, an image clip, and colour and bump image blocks.</summary>
    internal static byte[] Mapped() => Bytes.Form("LWO2", f => f
        .Chunk("TAGS", b => b.S0("Label"))
        .Chunk("LAYR", b => b.U2(0).U2(0).Vec(0, 0, 0).S0("Can"))
        .Chunk("PNTS", b => b.Vec(0, 0, 0).Vec(1, 0, 0).Vec(1, 1, 0).Vec(0, 1, 0))
        .Chunk("VMAP", b => b.Id("TXUV").U2(2).S0("Wrap").U2(0).F4(0).F4(0).U2(1).F4(1).F4(0).U2(2).F4(1).F4(1).U2(3).F4(0).F4(1))
        .Chunk("VMAP", b => b.Id("WGHT").U2(1).S0("Weight").U2(0).F4(1))
        .Chunk("POLS", b => b.Id("FACE").U2(4).U2(0).U2(1).U2(2).U2(3).U2(3).U2(0).U2(1).U2(2))
        .Chunk("PTAG", b => b.Id("SURF").U2(0).U2(0).U2(1).U2(0))
        .Chunk("VMAD", b => b.Id("TXUV").U2(2).S0("Wrap").U2(1).U2(1).F4(0.5f).F4(0.25f))
        .Chunk("CLIP", b => b.U4(7).Sub("STIL", s => s.S0("Images/label.png")))
        .Chunk("CLIP", b => b.U4(8).Sub("STIL", s => s.S0("C:/maps/dents.jpg")))
        .Chunk("SURF", b => b.S0("Label").S0("")
            .Sub("COLR", s => s.Vec(1, 1, 1).U2(0))
            .Sub("BLOK", s => s
                .Sub("IMAP", i => i.S0("\x80").Sub("CHAN", c => c.Id("COLR")).Sub("OPAC", c => c.U2(3).F4(0.5f).U2(0)).Sub("ENAB", c => c.U2(1)))
                .Sub("TMAP", t => t.Sub("CNTR", c => c.Vec(0, 1, 0).U2(0)).Sub("SIZE", c => c.Vec(2, 2, 2).U2(0)).Sub("CSYS", c => c.U2(0)))
                .Sub("PROJ", c => c.U2(5))
                .Sub("AXIS", c => c.U2(1))
                .Sub("IMAG", c => c.U2(7))
                .Sub("WRAP", c => c.U2(2).U2(3))
                .Sub("VMAP", c => c.S0("Wrap"))
                .Sub("PIXB", c => c.U2(1)))
            .Sub("BLOK", s => s
                .Sub("IMAP", i => i.S0("\x81").Sub("CHAN", c => c.Id("BUMP")))
                .Sub("PROJ", c => c.U2(1))
                .Sub("AXIS", c => c.U2(0))
                .Sub("IMAG", c => c.U2(8))
                .Sub("WRPW", c => c.F4(3).U2(0))
                .Sub("TAMP", c => c.F4(0.4f).U2(0)))
            .Sub("BLOK", s => s.Sub("PROC", i => i.S0("\x82").Sub("CHAN", c => c.Id("COLR"))))
            .Sub("BLOK", s => s
                .Sub("IMAP", i => i.S0("\x83").Sub("CHAN", c => c.Id("COLR")).Sub("ENAB", c => c.U2(0)))
                .Sub("IMAG", c => c.U2(7)))));

    [Fact]
    public void A_broken_uv_map_or_block_is_skipped()
    {
        var data = Bytes.Form("LWO2", f => f
            .Chunk("LAYR", b => b.U2(0).U2(0).Vec(0, 0, 0).S0(""))
            .Chunk("PNTS", b => b.Vec(0, 0, 0).Vec(1, 0, 0).Vec(0, 1, 0))
            .Chunk("VMAP", b => b.Id("TXUV").U2(2).S0("UV").U2(0).F4(0.5f).F4(0.5f).U2(7).F4(0).F4(0))
            .Chunk("POLS", b => b.Id("FACE").U2(3).U2(0).U2(1).U2(2))
            .Chunk("SURF", b => b.S0("Default").S0("").Sub("BLOK", s => s.Sub("IMAP", i => i.U1(1, 2, 3)))));
        var doc = Lwo2.Read(data);
        Assert.Single(doc.Layers[0].Mesh.Polygons);
        Assert.Empty(doc.Layers[0].Mesh.UVMaps);
        Assert.Empty(doc.Surfaces[0].Textures);
    }

    [Fact]
    public void Uv_maps_seams_and_image_blocks_are_read()
    {
        var doc = Lwo2.Read(Mapped());
        var mesh = doc.Layers[0].Mesh;
        var uv = Assert.Single(mesh.UVMaps);
        Assert.Equal("Wrap", uv.Name);
        Assert.Equal(new Vec2(1, 1), uv.Points[2]);
        Assert.Null(mesh.Polygons[0].CornerUVs);
        Assert.Equal(new Vec2(0.5, 0.25), mesh.Polygons[1].CornerUVs!["Wrap"][1]);
        Assert.True(mesh.TryGetUV("Wrap", mesh.Polygons[0], 1, out var shared));
        Assert.Equal(new Vec2(1, 0), shared);

        var t = doc.Surfaces[0].Textures;
        Assert.Equal(2, t.Count);
        var colour = t[0];
        Assert.Equal((TextureChannel.Color, TextureProjection.UV, "Wrap", "Images/label.png"), (colour.Channel, colour.Projection, colour.UVMap, colour.Image));
        Assert.Equal((TextureBlend.Multiply, 0.5), (colour.Blend, colour.Opacity));
        Assert.Equal((TextureTiling.Mirror, TextureTiling.Edge), (colour.WidthTiling, colour.HeightTiling));
        Assert.Equal((new Vec3(0, 1, 0), new Vec3(2, 2, 2), Axis.Y), (colour.Center, colour.Size, colour.Axis));
        var bump = t[1];
        Assert.Equal((TextureChannel.Bump, TextureProjection.Cylindrical, Axis.X, "C:/maps/dents.jpg"), (bump.Channel, bump.Projection, bump.Axis, bump.Image));
        Assert.Equal(3, bump.WidthWrap);
        Assert.Equal(0.4f, (float)bump.Amplitude);
    }
}

public class ObjTextureTests
{
    private static Document Read(string text, Func<string, TextReader?>? mtl = null) => Obj.Read(new StringReader(text), mtl);

    [Fact]
    public void Texture_coordinates_go_into_a_uv_map_with_seams_where_a_point_has_two()
    {
        var doc = Read("""
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            v 2 0 0
            v 2 1 0
            vt 0 0
            vt 0.5 0
            vt 0.5 1
            vt 0 1
            vt 1 0
            vt 1 1
            vt 0.6 0
            f 1/1 2/2 3/3 4/4
            f 2/7 5/5 6/6 3/3
            """);
        var mesh = doc.Layers[0].Mesh;
        Assert.Equal("UV", Assert.Single(mesh.UVMaps).Name);
        Assert.Equal(new Vec2?[] { new Vec2(0, 1), new Vec2(0.5, 1), new Vec2(0.5, 0), new Vec2(0, 0) }, mesh.CornerUVsOf("UV", mesh.Polygons[0]));
        Assert.True(mesh.TryGetUV("UV", mesh.Polygons[1], 1, out var seam));
        Assert.Equal(new Vec2(0.6, 0), seam);
        Assert.NotNull(mesh.Polygons[1].CornerUVs);
    }

    [Fact]
    public void Uvs_and_texture_maps_write_and_read_back()
    {
        var doc = new Document();
        var mesh = doc.Layers[0].Mesh;
        mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0), new Vec3(1, 0, 0));
        mesh.AddFace(0, new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(2, 1, 0), new Vec3(2, 0, 0));
        for (int i = 0; i < 8; i++) mesh.SetPointUV("UV", i, new Vec2(mesh.Points[i].X * 0.5, mesh.Points[i].Y));
        mesh.SetCornerUV("UV", mesh.Polygons[1], 4, new Vec2(0.75, 0.125));
        mesh.AddPolygon([0, 6]);
        doc.Surfaces[0].Textures.Add(new TextureLayer { Projection = TextureProjection.UV, UVMap = "UV", Image = "maps/brick wall.png" });
        doc.Surfaces[0].Textures.Add(new TextureLayer { Channel = TextureChannel.Bump, Projection = TextureProjection.UV, UVMap = "UV", Image = "dents.png", Amplitude = 0.25 });
        var obj = new StringWriter();
        var mtl = new StringWriter();
        Obj.Write(doc, obj, mtl, "x.mtl");
        Assert.Contains("vt 0.75 0.125", obj.ToString());
        Assert.Contains("f 4/4 3/3 2/2 1/1", obj.ToString());
        Assert.Contains("l 1 7", obj.ToString());
        Assert.Contains("map_Kd maps/brick wall.png", mtl.ToString());
        Assert.Contains("map_Bump -bm 0.25 dents.png", mtl.ToString());

        var back = Read(obj.ToString(), _ => new StringReader(mtl.ToString()));
        var bm = back.Layers[0].Mesh;
        for (int p = 0; p < 2; p++)
            Assert.Equal(mesh.CornerUVsOf("UV", mesh.Polygons[p]), bm.CornerUVsOf("UV", bm.Polygons[p]));
        var t = back.Surfaces[0].Textures;
        Assert.Equal((TextureChannel.Color, TextureProjection.UV, "UV", "maps/brick wall.png"), (t[0].Channel, t[0].Projection, t[0].UVMap, t[0].Image));
        Assert.Equal((TextureChannel.Bump, "dents.png", 0.25), (t[1].Channel, t[1].Image, t[1].Amplitude));
    }

    [Fact]
    public void Map_options_are_skipped_or_understood()
    {
        var doc = Read("mtllib a.mtl\nusemtl M\nv 0 0 0\np 1\n", _ => new StringReader("newmtl M\nmap_Kd -s 2 2 1 -o 0.5 0.5 -clamp on tex.png\nbump -imfchan l -bm 3 bump map.png\n"));
        var t = doc.Surfaces[0].Textures;
        Assert.Equal(("tex.png", TextureTiling.Edge), (t[0].Image, t[0].WidthTiling));
        Assert.Equal(("bump map.png", 3.0), (t[1].Image, t[1].Amplitude));
        // A face with only some corners mapped has no UVs; and no UVs at all writes as before.
        var plain = Read("v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nf 1/1 2 3\n");
        Assert.Empty(plain.Layers[0].Mesh.FindUVMap("UV")?.Points ?? []);
        var w = new StringWriter();
        Obj.Write(plain, w);
        Assert.DoesNotContain("vt", w.ToString());
        Assert.Contains("f 1 2 3", w.ToString());
    }
}

public class ImagePathTests
{
    [Fact]
    public void Saving_stores_image_paths_relative_to_the_object_without_changing_the_document()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("Objects"));
        Directory.CreateDirectory(dir.File("Images"));
        string image = dir.File(Path.Combine("Images", "brick.png"));
        File.WriteAllBytes(image, [1]);
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, Vec3.Zero, Vec3.UnitX, Vec3.UnitY);
        doc.Surfaces[0].Textures.Add(new TextureLayer { Image = image });
        doc.Surfaces[0].Textures.Add(new TextureLayer { Image = "missing/wherever.png" });
        string path = dir.File(Path.Combine("Objects", "wall.lwo"));
        ObjectFiles.Save(doc, path);
        Assert.Equal(image, doc.Surfaces[0].Textures[0].Image);
        var back = ObjectFiles.Load(path);
        Assert.Equal("../Images/brick.png", back.Surfaces[0].Textures[0].Image);
        Assert.Equal("missing/wherever.png", back.Surfaces[0].Textures[1].Image);
        Assert.Equal(Path.GetFullPath(image), TextureImages.Resolve(back.Surfaces[0].Textures[0].Image, back.ImageBase));

        // Saved somewhere else, a relative path is rebased to still find the image.
        Directory.CreateDirectory(dir.File("Elsewhere"));
        string moved = dir.File(Path.Combine("Elsewhere", "wall.obj"));
        ObjectFiles.Save(back, moved);
        Assert.Contains("map_Kd ../Images/brick.png", File.ReadAllText(Path.ChangeExtension(moved, ".mtl")));
        Assert.Equal("../Images/brick.png", back.Surfaces[0].Textures[0].Image);
    }
}
