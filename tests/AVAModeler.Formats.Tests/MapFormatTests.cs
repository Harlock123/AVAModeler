using System.Text;
using System.Text.Json.Nodes;
using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

/// <summary>Normal maps in LWOB and glTF, glTF's other maps, and skins and morph targets read from glTF.</summary>
public class MapFormatTests
{
    private static Document Mapped(string image)
    {
        var doc = new Document();
        var m = doc.Layers[0].Mesh;
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0));
        var poly = m.Polygons[0];
        for (int i = 0; i < 3; i++) m.SetCornerUV("UV", poly, poly.Points[i], new Vec2(m.Points[i].X, m.Points[i].Y));
        doc.Surfaces[0].Textures.Add(new TextureLayer { Channel = TextureChannel.Normal, Image = image, Projection = TextureProjection.UV, UVMap = "UV" });
        return doc;
    }

    [Fact]
    public void Lwob_keeps_a_normal_map_layer()
    {
        var bytes = Lwob.Write(Mapped("normal.png"));
        Assert.Contains("NTEX", Encoding.ASCII.GetString(bytes));
        var read = Lwob.Read(bytes);
        var layer = read.Surfaces[0].Textures.Single();
        Assert.Equal(TextureChannel.Normal, layer.Channel);
        Assert.Equal("normal.png", layer.Image);
        Assert.Equal(bytes, Lwob.Write(read));
    }

    [Fact]
    public void Gltf_writes_and_reads_a_normal_map()
    {
        string image = Path.Combine(Path.GetTempPath(), $"ava-n-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(image, Png.Encode(2, 2, [128, 128, 255, 255, 128, 128, 255, 255, 128, 128, 255, 255, 128, 128, 255, 255]));
        try
        {
            var (json, _, _) = Gltf.Write(Mapped(image), GltfPacking.Embedded);
            var root = JsonNode.Parse(json)!;
            Assert.NotNull(root["materials"]![0]!["normalTexture"]);
            Assert.NotNull(root["meshes"]![0]!["primitives"]![0]!["attributes"]!["TEXCOORD_0"]);
            var read = Gltf.Read(json, null, "mapped.gltf");
            Assert.Contains(read.Surfaces.SelectMany(s => s.Textures), t => t.Channel == TextureChannel.Normal);
        }
        finally { File.Delete(image); }
    }

    /// <summary>A glTF of two triangles with a two-joint skin (a hip and a knee) and one morph target, made by hand.</summary>
    private static byte[] Skinned()
    {
        float[] pos = [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0];
        byte[] joints = [0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        float[] weights = [1, 0, 0, 0, 0.5f, 0.5f, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        float[] delta = [0, 0, 0, 0, 0, 0, 0, 0.5f, 0, 0, 0.5f, 0];
        ushort[] idx = [0, 1, 2, 2, 1, 3];
        var buf = new List<byte>();
        int Add(byte[] b) { int at = buf.Count; buf.AddRange(b); while (buf.Count % 4 != 0) buf.Add(0); return at; }
        int pOff = Add(pos.SelectMany(BitConverter.GetBytes).ToArray());
        int jOff = Add(joints);
        int wOff = Add(weights.SelectMany(BitConverter.GetBytes).ToArray());
        int dOff = Add(delta.SelectMany(BitConverter.GetBytes).ToArray());
        int iOff = Add(idx.SelectMany(BitConverter.GetBytes).ToArray());
        string uri = "data:application/octet-stream;base64," + Convert.ToBase64String(buf.ToArray());
        var json = $$"""
        {
          "asset": {"version": "2.0"}, "scene": 0,
          "scenes": [{"nodes": [0, 1]}],
          "nodes": [
            {"name": "Leg", "mesh": 0, "skin": 0},
            {"name": "Hip", "translation": [0, 1, 0], "children": [2]},
            {"name": "Knee", "translation": [0, -1, 0]}
          ],
          "skins": [{"joints": [1, 2]}],
          "meshes": [{"name": "Leg", "extras": {"targetNames": ["Bend"]}, "primitives": [{
            "attributes": {"POSITION": 0, "JOINTS_0": 1, "WEIGHTS_0": 2}, "indices": 4, "targets": [{"POSITION": 3}]}]}],
          "buffers": [{"byteLength": {{buf.Count}}, "uri": "{{uri}}"}],
          "bufferViews": [
            {"buffer": 0, "byteOffset": {{pOff}}, "byteLength": 48},
            {"buffer": 0, "byteOffset": {{jOff}}, "byteLength": 16},
            {"buffer": 0, "byteOffset": {{wOff}}, "byteLength": 64},
            {"buffer": 0, "byteOffset": {{dOff}}, "byteLength": 48},
            {"buffer": 0, "byteOffset": {{iOff}}, "byteLength": 12}
          ],
          "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": 4, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0]},
            {"bufferView": 1, "componentType": 5121, "count": 4, "type": "VEC4"},
            {"bufferView": 2, "componentType": 5126, "count": 4, "type": "VEC4"},
            {"bufferView": 3, "componentType": 5126, "count": 4, "type": "VEC3"},
            {"bufferView": 4, "componentType": 5123, "count": 6, "type": "SCALAR"}
          ]
        }
        """;
        return Encoding.UTF8.GetBytes(json);
    }

    [Fact]
    public void A_skin_comes_in_as_skelegons_and_weight_maps_and_a_morph_target_as_a_layer()
    {
        var doc = Gltf.Read(Skinned(), null, "leg.gltf");
        var leg = doc.Layers[0];
        var m = leg.Mesh;
        var hip = m.FindPointMap("Hip", PointMapKind.Weight)!;
        var knee = m.FindPointMap("Knee", PointMapKind.Weight)!;
        int origin = m.Points.FindIndex(p => Vec3.Near(p, Vec3.Zero, 1e-6));
        int right = m.Points.FindIndex(p => Vec3.Near(p, new Vec3(1, 0, 0), 1e-6));
        Assert.Equal(1, hip.Weight(origin), 6);
        Assert.Equal(0.5, knee.Weight(right), 6);
        // Skelegons: the hip from (0, 1, 0) down to the knee, the knee on half as far again.
        var skel = SkelegonNames.Of(m, doc.Surfaces);
        Assert.Equal(["Hip", "Knee"], skel.Select(s => s.Name));
        Assert.True(Vec3.Near(skel[0].Start, new Vec3(0, 1, 0), 1e-6) && Vec3.Near(skel[0].End, Vec3.Zero, 1e-6));
        Assert.True(Vec3.Near(skel[1].End, new Vec3(0, -0.5, 0), 1e-6));
        // The morph target: a layer of its own with the top points raised.
        var bend = doc.Layers.Single(l => l.Name == "Bend");
        Assert.Equal(m.Points.Count, bend.Mesh.Points.Count);
        int top = m.Points.FindIndex(p => Vec3.Near(p, new Vec3(0, 1, 0), 1e-6));
        Assert.True(Vec3.Near(bend.Mesh.Points[top], new Vec3(0, 1.5, 0), 1e-6));
    }
}
