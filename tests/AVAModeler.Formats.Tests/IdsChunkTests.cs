using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

/// <summary>AVA's AVID chunk: agents' ids for a layer's points and polygons, kept only when asked for.</summary>
public class IdsChunkTests
{
    private static Document Box(int layers = 1)
    {
        var doc = new Document();
        for (int l = 0; l < layers; l++)
        {
            var m = doc.Layers[l].Mesh;
            int a = m.AddPoint(new Vec3(l, 0, 0)), b = m.AddPoint(new Vec3(l + 1, 0, 0)), c = m.AddPoint(new Vec3(l + 1, 1, 0)), d = m.AddPoint(new Vec3(l, 1, 0));
            m.AddPolygon([a, b, c, d]);
            m.AddPolygon([a, c, b]);
        }
        return doc;
    }

    [Fact]
    public void Ids_round_trip_in_one_layer_and_several()
    {
        foreach (int layers in new[] { 1, 2 })
        {
            var doc = Box(layers);
            for (int l = 0; l < layers; l++) doc.Layers[l].Ids = new GeometryIdTable([10 + l, 11, 12, 40], [7, 9 + l]);
            var back = Lwob.Read(Lwob.Write(doc));
            for (int l = 0; l < layers; l++)
            {
                Assert.Equal(doc.Layers[l].Ids!.Points, back.Layers[l].Ids!.Points);
                Assert.Equal(doc.Layers[l].Ids!.Polygons, back.Layers[l].Ids!.Polygons);
            }
        }
    }

    [Fact]
    public void Ids_that_no_longer_fit_and_objects_without_them_write_no_chunk()
    {
        var plain = Box();
        var bytes = Lwob.Write(plain);
        Assert.DoesNotContain("AVID", System.Text.Encoding.ASCII.GetString(bytes));
        Assert.Null(Lwob.Read(bytes).Layers[0].Ids);

        var stale = Box();
        stale.Layers[0].Ids = new GeometryIdTable([1, 2, 3], [1, 2]);   // one point short
        Assert.Null(Lwob.Read(Lwob.Write(stale)).Layers[0].Ids);
    }

    [Fact]
    public void A_file_without_ids_still_writes_back_byte_for_byte()
    {
        var data = LwobTests.Legacy();
        Assert.Equal(data, Lwob.Write(Lwob.Read(data)));
    }
}
