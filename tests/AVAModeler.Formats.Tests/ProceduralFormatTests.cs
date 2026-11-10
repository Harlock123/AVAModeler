using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

public class LwobProceduralTests
{
    /// <summary>
    /// A LightWave 5 object with procedural textures as LightWave wrote them: a marble colour texture with its colour,
    /// falloff and settings, a ripples bump, a fractal-noise diffuse texture with a value, and a kind this doesn't know.
    /// </summary>
    internal static byte[] Procedural() => Bytes.Form("LWOB", f => f
        .Chunk("PNTS", b => b.Vec(0, 0, 0).Vec(1, 0, 0).Vec(1, 1, 0).Vec(0, 1, 0))
        .Chunk("SRFS", b => b.S0("Stone"))
        .Chunk("POLS", b => b.U2(4).U2(0).U2(1).U2(2).U2(3).I2(1))
        .Chunk("SURF", b => b.S0("Stone")
            .Sub("COLR", s => s.U1(230, 228, 220, 0))
            .Sub("FLAG", s => s.U2(4))
            .Sub("CTEX", s => s.S0("Marble"))
            .Sub("TFLG", s => s.U2(1 | 32))
            .Sub("TSIZ", s => s.Vec(0.25f, 0.25f, 0.25f))
            .Sub("TCTR", s => s.Vec(0, 0.5f, 0))
            .Sub("TFAL", s => s.Vec(0.5f, 0, 0))
            .Sub("TVEL", s => s.Vec(0, 0, 0))
            .Sub("TCLR", s => s.U1(60, 80, 70, 0))
            .Sub("TFP0", s => s.F4(1.5f))
            .Sub("TFP1", s => s.F4(0.75f))
            .Sub("TFP2", s => s.F4(6))
            .Sub("TIP0", s => s.I2(5))
            .Sub("BTEX", s => s.S0("Ripples"))
            .Sub("TFLG", s => s.U2(2))
            .Sub("TSIZ", s => s.Vec(1, 1, 1))
            .Sub("TCTR", s => s.Vec(0, 0, 0))
            .Sub("TAMP", s => s.F4(0.1f))
            .Sub("TFP0", s => s.F4(0.2f))
            .Sub("TIP0", s => s.I2(2))
            .Sub("DTEX", s => s.S0("Fractal Noise"))
            .Sub("TFLG", s => s.U2(4 | 16))
            .Sub("TSIZ", s => s.Vec(0.5f, 0.5f, 0.5f))
            .Sub("TCTR", s => s.Vec(0, 0, 0))
            .Sub("TVAL", s => s.U2(64))
            .Sub("TFP0", s => s.F4(2))
            .Sub("TFP1", s => s.F4(0.25f))
            .Sub("TIP0", s => s.I2(4))
            .Sub("STEX", s => s.S0("Brick"))
            .Sub("TFLG", s => s.U2(4))
            .Sub("TSIZ", s => s.Vec(1, 1, 1))
            .Sub("TCLR", s => s.U1(1, 2, 3, 0))
            .Sub("TFP0", s => s.F4(0.1f))
            .Sub("EDGE", s => s.F4(0.5f))));

    [Fact]
    public void Procedural_texture_sub_chunks_are_read_and_written_back_byte_identical()
    {
        var data = Procedural();
        var doc = Lwob.Read(data);
        Assert.Equal(data, Lwob.Write(doc));
        var t = doc.Surfaces[0].Textures;
        Assert.Equal(4, t.Count);

        var marble = t[0];
        Assert.True(marble.IsProcedural);
        Assert.Equal((TextureChannel.Color, "Marble", Axis.X), (marble.Channel, marble.Procedural, marble.Axis));
        Assert.Equal((new Vec3(0.25, 0.25, 0.25), new Vec3(0, 0.5, 0), new Vec3(0.5, 0, 0)), (marble.Size, marble.Center, marble.Falloff));
        Assert.Equal(new Rgb(60, 80, 70), marble.TextureColor);
        Assert.Equal((5.0, 1.5, 0.75, 6.0), (Procedurals.Get(marble, "Frequencies"), Procedurals.Get(marble, "Turbulence"),
            Procedurals.Get(marble, "Vein Spacing"), Procedurals.Get(marble, "Vein Sharpness")));

        var ripples = t[1];
        Assert.Equal((TextureChannel.Bump, Axis.Y), (ripples.Channel, ripples.Axis));
        Assert.Equal(0.1f, (float)ripples.Amplitude);
        Assert.Equal((2.0, 0.2f), (Procedurals.Get(ripples, "Wave Sources"), (float)Procedurals.Get(ripples, "Wavelength")));
        // A setting the file leaves out has LightWave's default.
        Assert.Equal(0.05, Procedurals.Get(ripples, "Wave Speed"));

        var noise = t[2];
        Assert.Equal((TextureChannel.Diffuse, 0.25, true), (noise.Channel, noise.Value, noise.Negative));
        Assert.Equal((4.0, 2.0, 0.25), (Procedurals.Get(noise, "Frequencies"), Procedurals.Get(noise, "Contrast"), Procedurals.Get(noise, "Small Power")));

        // A kind this doesn't draw is kept as it was.
        var brick = t[3];
        Assert.Equal(("Brick", false, true), (brick.Procedural, brick.IsProcedural, brick.IsImage == false));
        Assert.Equal(new Rgb(1, 2, 3), brick.TextureColor);
    }

    [Fact]
    public void Editing_a_procedural_rewrites_only_what_changed()
    {
        var doc = Lwob.Read(Procedural());
        var marble = doc.Surfaces[0].Textures[0];
        List<RawChunk> Of(TextureLayer t) => t.Raw!;
        var before = Of(marble).Select(c => c.Id).ToList();
        Procedurals.Set(marble, "Vein Spacing", 0.4);
        marble.TextureColor = new Rgb(10, 20, 30);
        var back = Lwob.Read(Lwob.Write(doc)).Surfaces[0].Textures;
        Assert.Equal(before, Of(back[0]).Select(c => c.Id));
        Assert.Equal(0.4f, (float)Procedurals.Get(back[0], "Vein Spacing"));
        Assert.Equal(new Rgb(10, 20, 30), back[0].TextureColor);
        Assert.Equal(1.5, Procedurals.Get(back[0], "Turbulence"));
        Assert.Equal(Of(Lwob.Read(Procedural()).Surfaces[0].Textures[0]).Single(c => c.Id == "TVEL").Data, Of(back[0]).Single(c => c.Id == "TVEL").Data);

        // A setting moved off its default is added; one left at it isn't.
        var ripples = doc.Surfaces[0].Textures[1];
        Procedurals.Set(ripples, "Wave Speed", 0.5);
        ripples.Center = new Vec3(1, 0, 0);
        var again = Lwob.Read(Lwob.Write(doc)).Surfaces[0].Textures[1];
        Assert.Equal(["BTEX", "TFLG", "TSIZ", "TCTR", "TAMP", "TFP0", "TIP0", "TFP1"], Of(again).Select(c => c.Id));
        Assert.Equal(0.5f, (float)Procedurals.Get(again, "Wave Speed"));

        // Turning a procedural into an image map drops its procedural sub-chunks.
        Procedurals.SetType(marble, "Planar Image Map");
        marble.Image = "veins.png";
        var image = Lwob.Read(Lwob.Write(doc)).Surfaces[0].Textures[0];
        Assert.True(image.IsImage);
        Assert.Equal("veins.png", image.Image);
        Assert.DoesNotContain(Of(image), c => c.Id is "TCLR" or "TIP0" or "TFP2");
    }

    [Fact]
    public void New_procedurals_of_every_kind_write_and_read_back()
    {
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, Vec3.Zero, Vec3.UnitX, Vec3.UnitY);
        var channels = Enum.GetValues<TextureChannel>();
        int i = 0;
        foreach (var kind in Procedurals.All)
        {
            var t = TextureLayer.NewProcedural(kind.Kind, channels[i++ % channels.Length]);
            t.Size = new Vec3(0.5, 0.25, 2);
            t.Center = new Vec3(1, 2, 3);
            t.Axis = Axis.Y;
            t.TextureColor = new Rgb(10, 200, 30);
            t.Value = 0.75;
            t.Amplitude = 0.5;
            t.Falloff = kind.Kind == ProceduralKind.Value ? new Vec3(0.25, 0, 0) : Vec3.Zero;
            foreach (var p in kind.Parameters) Procedurals.Set(t, p.Name, p.Whole ? p.Default + 1 : (float)(p.Default * 0.75 + p.Min * 0.25));
            doc.Surfaces[0].Textures.Add(t);
        }
        var data = Lwob.Write(doc);
        var back = Lwob.Read(data);
        Assert.Equal(data, Lwob.Write(back));
        var read = back.Surfaces[0].Textures;
        Assert.Equal(Procedurals.All.Count, read.Count);
        for (int k = 0; k < read.Count; k++)
        {
            var (want, got) = (doc.Surfaces[0].Textures[k], read[k]);
            Assert.Equal((want.Procedural, want.Channel, want.Axis), (got.Procedural, got.Channel, got.Axis));
            Assert.Equal((want.Size, want.Center, want.Falloff), (got.Size, got.Center, got.Falloff));
            Assert.Equal(want.Parameters.Select(v => (float)v), got.Parameters.Select(v => (float)v));
            if (want.Channel == TextureChannel.Color) Assert.Equal(want.TextureColor, got.TextureColor);
            else if (want.Channel != TextureChannel.Bump) Assert.Equal(0.75, got.Value);
            else Assert.Equal(0.5, got.Amplitude);
        }
    }
}

public class Lwo2ProceduralTests
{
    [Fact]
    public void Procedural_blocks_of_known_kinds_are_read_and_others_skipped()
    {
        var data = Bytes.Form("LWO2", f => f
            .Chunk("TAGS", b => b.S0("Floor"))
            .Chunk("LAYR", b => b.U2(0).U2(0).Vec(0, 0, 0).S0(""))
            .Chunk("PNTS", b => b.Vec(0, 0, 0).Vec(1, 0, 0).Vec(0, 0, 1))
            .Chunk("POLS", b => b.Id("FACE").U2(3).U2(0).U2(1).U2(2))
            .Chunk("PTAG", b => b.Id("SURF").U2(0).U2(0))
            .Chunk("SURF", b => b.S0("Floor").S0("")
                .Sub("COLR", s => s.Vec(1, 1, 1).U2(0))
                .Sub("BLOK", s => s
                    .Sub("PROC", i => i.S0("\x80").Sub("CHAN", c => c.Id("COLR")).Sub("OPAC", c => c.U2(0).F4(0.5f).U2(0)).Sub("ENAB", c => c.U2(1)))
                    .Sub("TMAP", t => t.Sub("CNTR", c => c.Vec(0, 1, 0).U2(0)).Sub("SIZE", c => c.Vec(0.5f, 0.5f, 0.5f).U2(0))
                        .Sub("FALL", c => c.U2(0).Vec(0.25f, 0, 0).U2(0)).Sub("CSYS", c => c.U2(0)))
                    .Sub("AXIS", c => c.U2(1))
                    .Sub("VALU", c => c.F4(0.2f).F4(0.4f).F4(0.6f).U2(0))
                    .Sub("FUNC", c => c.S0("Checkerboard")))
                .Sub("BLOK", s => s
                    .Sub("PROC", i => i.S0("\x81").Sub("CHAN", c => c.Id("DIFF")))
                    .Sub("VALU", c => c.F4(0.3f).U2(0))
                    .Sub("FUNC", c => c.S0("Fractal Noise").F4(1).F4(2)))
                .Sub("BLOK", s => s
                    .Sub("PROC", i => i.S0("\x82").Sub("CHAN", c => c.Id("COLR")))
                    .Sub("FUNC", c => c.S0("Brick")))));
        var t = Lwo2.Read(data).Surfaces[0].Textures;
        Assert.Equal(2, t.Count);
        var checks = t[0];
        Assert.Equal(("Checkerboard", TextureChannel.Color, Axis.Y), (checks.Procedural, checks.Channel, checks.Axis));
        Assert.Equal((new Vec3(0, 1, 0), new Vec3(0.5, 0.5, 0.5), new Vec3(0.25, 0, 0)), (checks.Center, checks.Size, checks.Falloff));
        Assert.Equal(new Rgb(51, 102, 153), checks.TextureColor);
        Assert.Equal(0.5, checks.Opacity);
        Assert.Equal("", checks.Image);
        var noise = t[1];
        Assert.Equal(("Fractal Noise", TextureChannel.Diffuse), (noise.Procedural, noise.Channel));
        Assert.Equal(0.3f, (float)noise.Value);
        Assert.Equal(3, Procedurals.Get(noise, "Frequencies"));
    }
}

public class GltfProceduralTests
{
    private static (double R, double G, double B) Sample(TextureImage img, Vec2 uv)
    {
        int x = Math.Clamp((int)(uv.X * img.Width), 0, img.Width - 1), y = Math.Clamp((int)((1 - uv.Y) * img.Height), 0, img.Height - 1);
        var (c, _) = img.Pixel(x, y);
        return (Math.Round(c.X * 255), Math.Round(c.Y * 255), Math.Round(c.Z * 255));
    }

    /// <summary>The baked image of the first primitive's material, and each vertex's position and UV (glTF's v turned back up).</summary>
    private static (TextureImage Image, Vec3[] Positions, Vec2[] UVs) Baked(Document doc)
    {
        var glb = Glb.Parse(Gltf.Write(doc, GltfPacking.Binary).Main);
        var primitive = glb.Json["meshes"]![0]!["primitives"]![0]!;
        var material = glb.Json["materials"]![(int)primitive["material"]!]!;
        int texture = (int)material["pbrMetallicRoughness"]!["baseColorTexture"]!["index"]!;
        int source = (int)glb.Json["textures"]![texture]!["source"]!;
        var image = glb.Json["images"]![source]!;
        Assert.Equal("image/png", (string?)image["mimeType"]);
        var view = glb.Json["bufferViews"]![(int)image["bufferView"]!]!;
        var png = glb.Bin.AsSpan((int)view["byteOffset"]!, (int)view["byteLength"]!).ToArray();
        var p = glb.Read((int)primitive["attributes"]!["POSITION"]!);
        var uv = glb.Read((int)primitive["attributes"]!["TEXCOORD_0"]!);
        var positions = Enumerable.Range(0, p.Length / 3).Select(i => GltfBuilder.FromGltf(new Vec3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]))).ToArray();
        var uvs = Enumerable.Range(0, uv.Length / 2).Select(i => new Vec2(uv[i * 2], 1 - uv[i * 2 + 1])).ToArray();
        return (TextureImages.Decode(png)!, positions, uvs);
    }

    [Fact]
    public void A_colour_procedural_without_a_uv_map_is_baked_into_a_box_map()
    {
        // A box with checks a metre across on every side: each side's cell shows the side's own checks.
        var doc = new Document();
        Models.Box(doc.Layers[0].Mesh, new Vec3(0, 0, 0), new Vec3(2, 2, 2));
        var checks = TextureLayer.NewProcedural(ProceduralKind.Checkerboard);
        checks.TextureColor = new Rgb(0, 0, 255);
        doc.Surfaces[0].Color = new Rgb(255, 255, 0);
        doc.Surfaces[0].Textures.Add(checks);
        var (image, positions, uvs) = Baked(doc);
        Assert.Equal((3 * ProceduralBake.Cell, 2 * ProceduralBake.Cell), (image.Width, image.Height));
        var p = Procedurals.Prepare(checks)!;
        // Each side's four corners come one after another (flat sides share no vertices): at points across each side,
        // the picture shows the check that is there.
        Assert.Equal(24, positions.Length);
        for (int f = 0; f < 6; f++)
        {
            var corners = positions.Skip(4 * f).Take(4).ToArray();
            var cornerUVs = uvs.Skip(4 * f).Take(4).ToArray();
            var flat = Enumerable.Range(0, 3).Single(a => corners.All(c => c[a] == corners[0][a]));
            var (a1, a2) = flat switch { 0 => (1, 2), 1 => (0, 2), _ => (0, 1) };
            for (double s1 = 0.1; s1 < 2; s1 += 0.2)
                for (double s2 = 0.1; s2 < 2; s2 += 0.2)
                {
                    var point = Point(flat, corners[0][flat], a1, s1, a2, s2);
                    var want = p.Amount(point) > 0.5 ? (0.0, 0.0, 255.0) : (255.0, 255.0, 0.0);
                    Assert.Equal(want, Sample(image, UvAt(corners, cornerUVs, point, a1, a2)));
                }
        }
    }

    private static Vec3 Point(int flat, double depth, int a1, double v1, int a2, double v2)
    {
        var v = new double[3];
        v[flat] = depth;
        v[a1] = v1;
        v[a2] = v2;
        return new Vec3(v[0], v[1], v[2]);
    }

    [Fact]
    public void A_floor_bakes_exactly_its_checks()
    {
        // A 2 × 2 floor at y = 0 with half-metre checks: every texel on the top side is the check at its point.
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, new Vec3(-1, 0, -1), new Vec3(-1, 0, 1), new Vec3(1, 0, 1), new Vec3(1, 0, -1));
        var checks = TextureLayer.NewProcedural(ProceduralKind.Checkerboard);
        checks.Size = new Vec3(0.5, 0.5, 0.5);
        checks.TextureColor = new Rgb(0, 0, 0);
        doc.Surfaces[0].Color = Rgb.White;
        doc.Surfaces[0].Textures.Add(checks);
        var (image, positions, uvs) = Baked(doc);
        var p = Procedurals.Prepare(checks)!;
        // Interpolate a few points across the face from its corners (a quad split into two triangles shares 0 and 2).
        for (double s = 0.06; s < 1; s += 0.125)
            for (double t = 0.06; t < 1; t += 0.125)
            {
                var point = new Vec3(-1 + 2 * s, 0, -1 + 2 * t);
                // The face's corners in the export: find the UV of the point by its position on the face.
                var uv = UvAt(positions, uvs, point, 0, 2);
                double want = p.Amount(point) > 0.5 ? 0 : 255;
                Assert.Equal((want, want, want), Sample(image, uv));
            }
    }

    /// <summary>
    /// A point's UV on an axis-aligned rectangular face, from its corners' positions and UVs (the UVs are linear across
    /// it along its two axes, a1 and a2).
    /// </summary>
    private static Vec2 UvAt(Vec3[] positions, Vec2[] uvs, Vec3 point, int a1, int a2)
    {
        int i1 = Enumerable.Range(1, positions.Length - 1).First(i => positions[i][a1] != positions[0][a1] && positions[i][a2] == positions[0][a2]);
        int i2 = Enumerable.Range(1, positions.Length - 1).First(i => positions[i][a2] != positions[0][a2] && positions[i][a1] == positions[0][a1]);
        double f1 = (point[a1] - positions[0][a1]) / (positions[i1][a1] - positions[0][a1]), f2 = (point[a2] - positions[0][a2]) / (positions[i2][a2] - positions[0][a2]);
        return uvs[0] + (uvs[i1] - uvs[0]) * f1 + (uvs[i2] - uvs[0]) * f2;
    }

    [Fact]
    public void A_uv_mapped_surface_bakes_its_procedural_into_its_own_layout()
    {
        var doc = new Document();
        var mesh = doc.Layers[0].Mesh;
        mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0), new Vec3(1, 0, 0));
        // The map turned a quarter: u runs up the face, v across it.
        for (int i = 0; i < 4; i++) mesh.SetPointUV("Turned", i, new Vec2(mesh.Points[i].Y, 1 - mesh.Points[i].X));
        var grid = TextureLayer.NewProcedural(ProceduralKind.Value);
        grid.TextureColor = new Rgb(255, 0, 0);
        grid.Falloff = new Vec3(1, 0, 0);
        doc.Surfaces[0].Color = new Rgb(0, 0, 0);
        doc.Surfaces[0].Textures.Add(grid);
        var (image, _, uvs) = Baked(doc);
        Assert.Equal((ProceduralBake.MapSize, ProceduralBake.MapSize), (image.Width, image.Height));
        // Red fades from x = 0 to x = 1, which runs down v.
        Assert.Contains(new Vec2(0, 1), uvs);
        Assert.InRange(Sample(image, new Vec2(0.5, 0.98)).R, 245, 255);
        Assert.InRange(Sample(image, new Vec2(0.5, 0.5)).R, 120, 135);
        Assert.InRange(Sample(image, new Vec2(0.5, 0.02)).R, 0, 10);
        Assert.Equal(0, Sample(image, new Vec2(0.5, 0.5)).G);
    }

    [Fact]
    public void An_image_wins_over_procedurals_and_value_channels_are_not_baked()
    {
        using var dir = new TempDir();
        var doc = Models.Sample(dir.Path);
        doc.Surfaces[1].Textures.Add(TextureLayer.NewProcedural(ProceduralKind.Checkerboard));
        doc.Surfaces[0].Textures.Add(TextureLayer.NewProcedural(ProceduralKind.Marble, TextureChannel.Diffuse));
        var glb = Glb.Parse(Gltf.Write(doc, GltfPacking.Binary).Main);
        Assert.Single(glb.Json["images"]!.AsArray());
        Assert.Null(glb.Json["materials"]![0]!["pbrMetallicRoughness"]!["baseColorTexture"]);
    }
}

public class LwobNodeTests
{
    [Fact]
    public void A_surfaces_node_graph_is_kept_in_its_own_subchunk()
    {
        var doc = Lwob.Read(LwobProceduralTests.Procedural());
        var stone = doc.Surfaces[0];
        Assert.Null(stone.Nodes);
        var g = SurfaceNodeGraph.New();
        var noise = g.Add(NodeKind.Noise, 10, 20);
        noise.Numbers["Scale"] = 0.25;
        var ramp = g.Add(NodeKind.Gradient);
        ramp.Stops = [new(0.2, new Rgb(10, 20, 30)), new(0.9, new Rgb(200, 190, 180))];
        g.Link(noise.Id, "Value", ramp.Id, "Value");
        g.Link(ramp.Id, "Color", g.Output!.Id, "Color");
        stone.Nodes = g.Checked();
        var bytes = Lwob.Write(doc);
        var back = Lwob.Read(bytes).Surfaces[0];
        Assert.Equal(g.ToJson(), back.Nodes!.ToJson());
        // Its textures and settings are kept beside it, and a file read and written back is the same.
        Assert.Equal(stone.Textures.Count, back.Textures.Count);
        Assert.Equal(bytes, Lwob.Write(Lwob.Read(bytes)));
        // Taken away again, the sub-chunk goes.
        var again = Lwob.Read(bytes);
        again.Surfaces[0].Nodes = null;
        Assert.Null(Lwob.Read(Lwob.Write(again)).Surfaces[0].Nodes);
        Assert.DoesNotContain("AVND", System.Text.Encoding.ASCII.GetString(Lwob.Write(again)));
        // A broken one says which surface.
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        int at = text.IndexOf("AVND", StringComparison.Ordinal) + 6;
        var bad = (byte[])bytes.Clone();
        bad[at] = (byte)'?';
        Assert.Contains("Stone", Assert.ThrowsAny<FormatException>(() => Lwob.Read(bad)).Message);
    }
}
