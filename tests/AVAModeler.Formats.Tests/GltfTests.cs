using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Formats.Tests;

/// <summary>Shapes for the interchange-format tests, built by hand in LightWave's space.</summary>
internal static class Models
{
    /// <summary>
    /// A box from min to max on a surface: six quads wound clockwise as seen from outside (each face is turned until
    /// its Newell normal points away from the centre, which is LightWave's rule).
    /// </summary>
    public static void Box(Mesh mesh, Vec3 min, Vec3 max, int surface = 0)
    {
        int first = mesh.Points.Count;
        for (int i = 0; i < 8; i++)
            mesh.Points.Add(new Vec3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z));
        int[][] faces = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
        var centre = (min + max) / 2;
        foreach (var f in faces)
        {
            var poly = new Polygon(f.Select(i => i + first), surface);
            mesh.Polygons.Add(poly);
            mesh.Touch();
            if (Vec3.Dot(mesh.Normal(poly), mesh.Centroid(poly) - centre) < 0) poly.Points.Reverse();
        }
        mesh.Touch();
    }

    /// <summary>A small RGBA test picture, written as a PNG file.</summary>
    public static (string Path, byte[] Rgba) Picture(string folder, string name = "tiles.png")
    {
        var rgba = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++)
        {
            rgba[i * 4] = (byte)(i * 16);
            rgba[i * 4 + 1] = (byte)(255 - i * 16);
            rgba[i * 4 + 2] = (byte)(i % 2 * 255);
            rgba[i * 4 + 3] = 255;
        }
        string path = System.IO.Path.Combine(folder, name);
        File.WriteAllBytes(path, Png.Encode(4, 4, rgba));
        return (path, rgba);
    }

    /// <summary>
    /// A two-layer object: a red box on a flat surface, and a textured, smooth box in layer 2 whose top face has a UV
    /// map the colour image uses.
    /// </summary>
    public static Document Sample(string? imageFolder)
    {
        var doc = new Document();
        doc.Surfaces[0] = new Surface("Paint") { Color = new Rgb(200, 40, 40), Diffuse = 0.8, Specular = 0.5, Glossiness = 64 };
        doc.Surfaces.Add(new Surface("Tiles") { Color = Rgb.White, Smoothing = true, SmoothingAngle = 120, Luminosity = 0.25, Transparency = 0.5, DoubleSided = true });
        Box(doc.Layers[0].Mesh, new Vec3(-1, 0, -1), new Vec3(1, 2, 1));
        doc.Layers[0].Name = "Body";
        var m = doc.Layers[1].Mesh;
        Box(m, new Vec3(2, 0, 0), new Vec3(3, 1, 1), surface: 1);
        foreach (var poly in m.Polygons)
            foreach (int p in poly.Points)
                m.SetCornerUV("Wrap", poly, p, new Vec2(m.Points[p].X - 2, m.Points[p].Z));
        if (imageFolder != null)
            doc.Surfaces[1].Textures.Add(new TextureLayer { Channel = TextureChannel.Color, Projection = TextureProjection.UV, UVMap = "Wrap", Image = Picture(imageFolder).Path, HeightTiling = TextureTiling.Mirror });
        return doc;
    }
}

/// <summary>Parses a .glb's chunks by hand, independently of the reader under test.</summary>
internal sealed record Glb(JsonObject Json, byte[] Bin, uint Length, uint JsonLength, uint BinLength)
{
    public static Glb Parse(byte[] data)
    {
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(data)); // "glTF"
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        uint jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        Assert.Equal(0x4E4F534Au, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16))); // "JSON"
        var json = JsonNode.Parse(Encoding.UTF8.GetString(data, 20, (int)jsonLength))!.AsObject();
        int at = 20 + (int)jsonLength;
        uint binLength = 0;
        byte[] bin = [];
        if (at < data.Length)
        {
            binLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            Assert.Equal(0x004E4942u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4))); // "BIN\0"
            bin = data.AsSpan(at + 8, (int)binLength).ToArray();
        }
        return new Glb(json, bin, length, jsonLength, binLength);
    }

    /// <summary>An accessor's floats (or integers), read straight from the buffer.</summary>
    public double[] Read(int accessor)
    {
        var a = Json["accessors"]![accessor]!;
        var view = Json["bufferViews"]![(int)a["bufferView"]!]!;
        int offset = (int)view["byteOffset"]! + ((int?)a["byteOffset"] ?? 0);
        int components = (string)a["type"]! switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, _ => 4 };
        int type = (int)a["componentType"]!;
        int size = type == 5123 ? 2 : 4;
        var result = new double[(int)a["count"]! * components];
        for (int i = 0; i < result.Length; i++)
            result[i] = type switch
            {
                5126 => BinaryPrimitives.ReadSingleLittleEndian(Bin.AsSpan(offset + i * size)),
                5125 => BinaryPrimitives.ReadUInt32LittleEndian(Bin.AsSpan(offset + i * size)),
                _ => BinaryPrimitives.ReadUInt16LittleEndian(Bin.AsSpan(offset + i * size)),
            };
        return result;
    }
}

public class GltfTests
{
    private static int TriangleCount(Document doc) => doc.Layers.Sum(l => Triangulator.TriangulateMesh(l.Mesh).Count);

    [Fact]
    public void A_glb_has_a_valid_structure()
    {
        using var dir = new TempDir();
        var doc = Models.Sample(dir.Path);
        string path = dir.File("sample.glb");
        ObjectFiles.Save(doc, path);
        var data = File.ReadAllBytes(path);
        var glb = Glb.Parse(data);

        Assert.Equal((uint)data.Length, glb.Length);
        Assert.Equal(0u, glb.JsonLength % 4);
        Assert.Equal(0u, glb.BinLength % 4);
        Assert.Equal(0, data.Length % 4);
        var json = glb.Json;
        Assert.Equal("2.0", (string?)json["asset"]!["version"]);
        Assert.Equal(0, (int)json["scene"]!);

        // One buffer, the BIN chunk, with no uri; every buffer view inside it, aligned to 4.
        var buffer = json["buffers"]!.AsArray().Single()!;
        Assert.Null(buffer["uri"]);
        int byteLength = (int)buffer["byteLength"]!;
        Assert.True(byteLength <= glb.Bin.Length && glb.Bin.Length - byteLength < 4);
        foreach (var view in json["bufferViews"]!.AsArray())
        {
            int offset = (int)view!["byteOffset"]!, length = (int)view["byteLength"]!;
            Assert.Equal(0, offset % 4);
            Assert.True(offset + length <= byteLength);
        }
        // Every accessor fits its view; every POSITION accessor has the min and max the spec requires, and they are right.
        foreach (var a in json["accessors"]!.AsArray())
        {
            var view = json["bufferViews"]![(int)a!["bufferView"]!]!;
            int components = (string)a["type"]! switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, _ => 4 };
            int size = (int)a["componentType"]! == 5123 ? 2 : 4;
            Assert.True((int)a["count"]! * components * size <= (int)view["byteLength"]!);
        }
        int meshes = 0;
        foreach (var mesh in json["meshes"]!.AsArray())
            foreach (var primitive in mesh!["primitives"]!.AsArray())
            {
                meshes++;
                Assert.Equal(4, (int)primitive!["mode"]!);
                int position = (int)primitive["attributes"]!["POSITION"]!;
                var accessor = json["accessors"]![position]!;
                Assert.Equal("VEC3", (string?)accessor["type"]);
                Assert.Equal(34962, (int)json["bufferViews"]![(int)accessor["bufferView"]!]!["target"]!);
                var values = glb.Read(position);
                for (int c = 0; c < 3; c++)
                {
                    var column = values.Where((_, i) => i % 3 == c).ToList();
                    Assert.Equal(column.Min(), (double)accessor["min"]![c]!, 6);
                    Assert.Equal(column.Max(), (double)accessor["max"]![c]!, 6);
                }
                int vertices = values.Length / 3;
                var indices = glb.Read((int)primitive["indices"]!);
                Assert.Equal(0, indices.Length % 3);
                Assert.All(indices, i => Assert.InRange(i, 0, vertices - 1));
                Assert.Equal(vertices, (int)json["accessors"]![(int)primitive["attributes"]!["NORMAL"]!]!["count"]!);
            }
        Assert.Equal(2, meshes);
        Assert.Equal(["Body", "Layer 2"], json["nodes"]!.AsArray().Select(n => (string)n!["name"]!));

        // The image is inside the buffer, with its type.
        var image = json["images"]!.AsArray().Single()!;
        Assert.Equal("image/png", (string?)image["mimeType"]);
        Assert.NotNull(image["bufferView"]);
        Assert.Null(image["uri"]);
    }

    [Fact]
    public void Materials_map_the_surfaces_to_PBR()
    {
        using var dir = new TempDir();
        var (main, _, warnings) = Gltf.Write(Models.Sample(dir.Path), GltfPacking.Binary);
        Assert.Empty(warnings);
        var materials = Glb.Parse(main).Json["materials"]!.AsArray();
        var paint = materials.Single(m => (string?)m!["name"] == "Paint")!;
        var factor = paint["pbrMetallicRoughness"]!["baseColorFactor"]!.AsArray().Select(v => (double)v!).ToArray();
        // Colour (sRGB) to linear, times diffuse 0.8; opaque.
        Assert.Equal(GltfBuilder.ToLinear(200 / 255.0) * 0.8, factor[0], 6);
        Assert.Equal(GltfBuilder.ToLinear(40 / 255.0) * 0.8, factor[1], 6);
        Assert.Equal(1, factor[3]);
        Assert.Null(paint["alphaMode"]);
        Assert.Equal(0, (double)paint["pbrMetallicRoughness"]!["metallicFactor"]!);
        double roughness = (double)paint["pbrMetallicRoughness"]!["roughnessFactor"]!;
        Assert.InRange(roughness, 0.3, 0.6);
        Assert.Equal(64, GltfBuilder.Glossiness(roughness), 0);
        Assert.Null(paint["emissiveFactor"]);

        var tiles = materials.Single(m => (string?)m!["name"] == "Tiles")!;
        Assert.Equal("BLEND", (string?)tiles["alphaMode"]);
        Assert.Equal(0.5, (double)tiles["pbrMetallicRoughness"]!["baseColorFactor"]![3]!, 6);
        Assert.True((bool)tiles["doubleSided"]!);
        Assert.Equal(0.25, (double)tiles["emissiveFactor"]![0]!, 6);
        Assert.Equal(1, (double)tiles["pbrMetallicRoughness"]!["roughnessFactor"]!);
        Assert.NotNull(tiles["pbrMetallicRoughness"]!["baseColorTexture"]);
        var sampler = Glb.Parse(main).Json["samplers"]![0]!;
        Assert.Equal(10497, (int)sampler["wrapS"]!);
        Assert.Equal(33648, (int)sampler["wrapT"]!);
    }

    [Fact]
    public void A_triangle_faces_the_same_way_after_conversion()
    {
        // Clockwise as the Front view sees it (X right, Y up), so it faces the viewer: its normal is -Z in LightWave.
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        Assert.Equal(new Vec3(0, 0, -1), doc.Layers[0].Mesh.Normal(0));

        var glb = Glb.Parse(Gltf.Write(doc, GltfPacking.Binary).Main);
        var primitive = glb.Json["meshes"]![0]!["primitives"]![0]!;
        var p = glb.Read((int)primitive["attributes"]!["POSITION"]!);
        var idx = glb.Read((int)primitive["indices"]!).Select(v => (int)v).ToArray();
        Vec3 V(int i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
        // In glTF's right-handed space the front face is anticlockwise: the right-hand rule gives its normal, which
        // must be LightWave's -Z with Z flipped: +Z, toward a glTF camera at +Z looking down -Z.
        var normal = Vec3.Cross(V(idx[1]) - V(idx[0]), V(idx[2]) - V(idx[0])).Normalized();
        Assert.True(Vec3.Near(new Vec3(0, 0, 1), normal, 1e-9), normal.ToString());
        var n = glb.Read((int)primitive["attributes"]!["NORMAL"]!);
        Assert.Equal([0, 0, 1], n[..3]);

        // And back: the same LightWave face, facing -Z.
        var back = Gltf.Read(Gltf.Write(doc, GltfPacking.Binary).Main, null, "t");
        Assert.True(Vec3.Near(new Vec3(0, 0, -1), back.Layers[0].Mesh.Normal(0), 1e-9));
    }

    [Theory]
    [InlineData(GltfPacking.Binary)]
    [InlineData(GltfPacking.Embedded)]
    [InlineData(GltfPacking.Separate)]
    public void Objects_round_trip(GltfPacking packing)
    {
        using var dir = new TempDir();
        var doc = Models.Sample(dir.Path);
        string ext = packing == GltfPacking.Binary ? ".glb" : ".gltf";
        string path = dir.File("round" + ext);
        ObjectFiles.Save(doc, path, new SaveOptions { SeparateGltfBuffer = packing == GltfPacking.Separate });
        Assert.Equal(packing == GltfPacking.Separate, File.Exists(dir.File("round.bin")));
        if (packing == GltfPacking.Embedded) Assert.Contains("data:application/octet-stream;base64,", File.ReadAllText(path));
        if (packing == GltfPacking.Separate) Assert.Contains("\"uri\": \"round.bin\"", File.ReadAllText(path));

        var back = ObjectFiles.Load(path);
        Assert.Equal(path, back.FilePath);
        Assert.Equal(TriangleCount(doc), TriangleCount(back));
        Assert.Equal(["Body", "Layer 2"], back.Layers.Where(l => !l.Mesh.IsEmpty).Select(l => l.Name));
        for (int layer = 0; layer < 2; layer++)
        {
            var a = doc.Layers[layer].Mesh;
            var b = back.Layers[layer].Mesh;
            // Points shared by faces are merged again: the boxes get their eight corners back.
            Assert.Equal(a.Points.Count, b.Points.Count);
            foreach (var p in a.Points) Assert.Contains(b.Points, q => Vec3.Near(p, q, 1e-6));
            // Every face still faces out.
            var centre = a.Bounds().Center;
            Assert.All(b.Polygons, poly => Assert.True(Vec3.Dot(b.Normal(poly), b.Centroid(poly) - centre) > 0));
        }

        // UVs: each corner of the textured box keeps its coordinates.
        var m = back.Layers[1].Mesh;
        Assert.NotNull(m.FindUVMap(Gltf.UVMapName));
        foreach (var poly in m.Polygons)
            foreach (int p in poly.Points)
            {
                Assert.True(m.TryGetUV(Gltf.UVMapName, poly, p, out var uv));
                Assert.Equal(m.Points[p].X - 2, uv.X, 6);
                Assert.Equal(m.Points[p].Z, uv.Y, 6);
            }

        // Surfaces: colour, transparency, smoothing, double-sided and the colour image.
        var paint = back.Surfaces.Single(s => s.Name == "Paint");
        // 200 × diffuse 0.8 in linear light, back to sRGB.
        Assert.Equal((int)Math.Round(GltfBuilder.FromLinear(GltfBuilder.ToLinear(200 / 255.0) * 0.8) * 255), paint.Color.R);
        Assert.False(paint.Smoothing);
        Assert.Equal(64, paint.Glossiness, 0);
        var tiles = back.Surfaces.Single(s => s.Name == "Tiles");
        Assert.Equal(0.5, tiles.Transparency, 6);
        Assert.Equal(0.25, tiles.Luminosity, 6);
        Assert.True(tiles.Smoothing);
        Assert.True(tiles.DoubleSided);
        var texture = Assert.Single(tiles.Textures);
        Assert.Equal(TextureProjection.UV, texture.Projection);
        Assert.Equal(Gltf.UVMapName, texture.UVMap);
        Assert.Equal(TextureTiling.Mirror, texture.HeightTiling);
        var image = TextureImages.Load(texture.Image, back.ImageBase);
        Assert.False(image.IsPlaceholder, image.Problem);
        Assert.Equal(Models.Picture(dir.Path, "again.png").Rgba, image.Rgba);
    }

    [Fact]
    public void Smooth_surfaces_share_normals_and_flat_ones_split()
    {
        var doc = new Document();
        doc.Surfaces.Add(new Surface("Smooth") { Smoothing = true, SmoothingAngle = 120 });
        doc.Surfaces.Add(new Surface("Sharp") { Smoothing = true, SmoothingAngle = 89.5 });
        Models.Box(doc.Layers[0].Mesh, -Vec3.One, Vec3.One, 0); // Default: smoothing off
        Models.Box(doc.Layers[1].Mesh, -Vec3.One, Vec3.One, 1);
        Models.Box(doc.Layers[2].Mesh, -Vec3.One, Vec3.One, 2);
        var glb = Glb.Parse(Gltf.Write(doc, GltfPacking.Binary).Main);
        int Vertices(int mesh) => (int)glb.Json["accessors"]![(int)glb.Json["meshes"]![mesh]!["primitives"]![0]!["attributes"]!["POSITION"]!]!["count"]!;
        Assert.Equal(24, Vertices(0)); // flat: each face its own corners
        Assert.Equal(8, Vertices(1));  // smooth past the 90° edges: one vertex a corner
        Assert.Equal(24, Vertices(2)); // smooth, but the edges are sharper than its angle
        var normals = glb.Read((int)glb.Json["meshes"]![1]!["primitives"]![0]!["attributes"]!["NORMAL"]!);
        Assert.All(normals, v => Assert.Equal(1 / Math.Sqrt(3), Math.Abs(v), 6));

        // Read back, the smooth box is smooth and the others flat.
        var back = Gltf.Read(Gltf.Write(doc, GltfPacking.Binary).Main, null, "t");
        Assert.Equal([false, true, false], back.Layers.Take(3).Select(l => back.Surfaces[l.Mesh.Polygons[0].Surface].Smoothing));
    }

    [Fact]
    public void Projected_colour_images_are_baked_into_texture_coordinates()
    {
        using var dir = new TempDir();
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 2, 0), new Vec3(2, 2, 0), new Vec3(2, 0, 0));
        doc.Surfaces[0].Textures.Add(new TextureLayer { Image = Models.Picture(dir.Path).Path, Axis = Axis.Z, Center = new Vec3(1, 1, 0), Size = new Vec3(2, 2, 2) });
        var glb = Glb.Parse(Gltf.Write(doc, GltfPacking.Binary).Main);
        var attributes = glb.Json["meshes"]![0]!["primitives"]![0]!["attributes"]!;
        var p = glb.Read((int)attributes["POSITION"]!);
        var uv = glb.Read((int)attributes["TEXCOORD_0"]!);
        for (int i = 0; i < p.Length / 3; i++)
        {
            Assert.Equal(p[i * 3] / 2, uv[i * 2], 6);         // u across, from the left
            Assert.Equal(1 - p[i * 3 + 1] / 2, uv[i * 2 + 1], 6); // glTF's v runs down
        }
    }

    [Fact]
    public void A_missing_image_is_left_out_with_a_warning()
    {
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, Vec3.Zero, Vec3.UnitY, Vec3.UnitX);
        doc.Surfaces[0].Textures.Add(new TextureLayer { Image = "nowhere/missing.png" });
        var (main, _, warnings) = Gltf.Write(doc, GltfPacking.Binary);
        Assert.Contains(warnings, w => w.Contains("missing.png"));
        var json = Glb.Parse(main).Json;
        Assert.Null(json["images"]);
        Assert.Null(json["meshes"]![0]!["primitives"]![0]!["attributes"]!["TEXCOORD_0"]);
    }

    [Fact]
    public void Points_and_lines_are_left_out_and_an_empty_object_is_still_valid()
    {
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, Vec3.Zero, Vec3.UnitY);
        var glb = Glb.Parse(Gltf.Write(doc, GltfPacking.Binary).Main);
        Assert.Null(glb.Json["meshes"]);
        Assert.Null(glb.Json["buffers"]);
        Assert.Empty(glb.Json["scenes"]![0]!["nodes"]!.AsArray());
        Assert.True(Gltf.Read(Gltf.Write(doc, GltfPacking.Binary).Main, null, "t").Layers.All(l => l.Mesh.IsEmpty));
    }

    [Fact]
    public void Reads_node_transforms_strips_and_external_files()
    {
        using var dir = new TempDir();
        // A hand-written .gltf: one triangle strip (a quad) in a .bin beside it, under a node moved 5 along +X and
        // turned 90° about +Y, and an image file beside it.
        var bin = new byte[4 * 3 * 4];
        float[] v = [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(i * 4), v[i]);
        File.WriteAllBytes(dir.File("quad data.bin"), bin);
        Models.Picture(dir.Path, "skin.png");
        double s = Math.Sqrt(0.5);
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["nodes"] = new JsonArray(new JsonObject { ["name"] = "Parent", ["translation"] = new JsonArray(5, 0, 0), ["children"] = new JsonArray(1) },
                new JsonObject { ["mesh"] = 0, ["rotation"] = new JsonArray(0, s, 0, s) }),
            ["meshes"] = new JsonArray(new JsonObject { ["name"] = "Quad", ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = 0 }, ["mode"] = 5, ["material"] = 0 }) }),
            ["materials"] = new JsonArray(new JsonObject { ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = 0 } } }),
            ["textures"] = new JsonArray(new JsonObject { ["source"] = 0 }),
            ["images"] = new JsonArray(new JsonObject { ["uri"] = "skin.png" }),
            ["accessors"] = new JsonArray(new JsonObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = 4, ["type"] = "VEC3" }),
            ["bufferViews"] = new JsonArray(new JsonObject { ["buffer"] = 0, ["byteLength"] = bin.Length }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length, ["uri"] = "quad%20data.bin" }),
        };
        File.WriteAllText(dir.File("quad.gltf"), json.ToJsonString());
        var doc = ObjectFiles.Load(dir.File("quad.gltf"));
        var layer = doc.Layers[0];
        Assert.Equal("Quad", layer.Name); // the node has no name, so its mesh's
        Assert.Equal(2, layer.Mesh.Polygons.Count);
        Assert.Equal(4, layer.Mesh.Points.Count);
        // glTF (1, 0, 0) turned 90° about +Y is (0, 0, -1), moved to (5, 0, -1); in LightWave Z flips: (5, 0, 1).
        Assert.Contains(layer.Mesh.Points, p => Vec3.Near(p, new Vec3(5, 0, 1), 1e-6));
        Assert.Contains(layer.Mesh.Points, p => Vec3.Near(p, new Vec3(5, 1, 1), 1e-6));
        // The strip faces +Z in glTF before turning, so +X after it, which is +X in LightWave too.
        Assert.All(layer.Mesh.Polygons, poly => Assert.True(Vec3.Near(new Vec3(1, 0, 0), layer.Mesh.Normal(poly), 1e-6)));
        var texture = Assert.Single(doc.Surfaces[0].Textures);
        Assert.Equal("skin.png", texture.Image);
        Assert.False(TextureImages.Load(texture.Image, doc.ImageBase).IsPlaceholder);
    }

    [Fact]
    public void Malformed_files_are_refused_with_a_reason()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("bad.gltf"), "{ not json");
        Assert.Contains("JSON", Assert.Throws<FormatException>(() => ObjectFiles.Load(dir.File("bad.gltf"))).Message);
        var glb = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 1);
        File.WriteAllBytes(dir.File("old.glb"), glb);
        Assert.Contains("version 1", Assert.Throws<FormatException>(() => ObjectFiles.Load(dir.File("old.glb"))).Message);
        File.WriteAllText(dir.File("nobuffer.gltf"), """{"asset":{"version":"2.0"},"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"nodes":[{"mesh":0}],"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}],"bufferViews":[{"buffer":0,"byteLength":36}],"buffers":[{"byteLength":36,"uri":"gone.bin"}]}""");
        Assert.Throws<FileNotFoundException>(() => ObjectFiles.Load(dir.File("nobuffer.gltf")));
    }
}
