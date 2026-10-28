using AVAModeler.Core;

namespace AVAModeler.Formats;

/// <summary>
/// LWO2's vertex map chunks for UVs: VMAP (type TXUV) gives points their coordinates in a named map, and VMAD gives
/// polygon corners coordinates of their own (along a seam). LWOB files written here carry the same chunks, after
/// each layer's polygons, so UV maps survive in the classic format too (LightWave 5 skips chunks it doesn't know).
/// Point and polygon numbers count from the layer's first.
/// </summary>
internal static class VertexMaps
{
    private const int Dimension = 2;

    /// <summary>
    /// Reads a VMAP into a mesh; maps other than UVs (weights, morphs, colours) are skipped. The whole chunk is read
    /// before anything changes, so one that doesn't fit the mesh throws and leaves it as it was.
    /// </summary>
    public static void ReadVmap(IffReader body, Mesh mesh, int pointBase)
    {
        if (body.Id4() != "TXUV") return;
        int dim = body.U2();
        string name = body.S0();
        if (dim < 2) return;
        var read = new List<(int, Vec2)>();
        while (!body.AtEnd)
        {
            int p = pointBase + body.VX();
            var uv = new Vec2(body.F4(), body.F4());
            for (int i = 2; i < dim; i++) body.F4();
            if (p >= mesh.Points.Count) throw body.Error($"maps point {p - pointBase} but the layer has only {mesh.Points.Count - pointBase}");
            read.Add((p, uv));
        }
        var map = mesh.AddUVMap(name);
        foreach (var (p, uv) in read) map.Points[p] = uv;
    }

    /// <summary>
    /// Reads a VMAD into a mesh's polygons (counted from <paramref name="polygonBase"/>); non-UV maps are skipped. As
    /// with <see cref="ReadVmap"/>, nothing changes unless the whole chunk fits.
    /// </summary>
    public static void ReadVmad(IffReader body, Mesh mesh, int pointBase, int polygonBase)
    {
        if (body.Id4() != "TXUV") return;
        int dim = body.U2();
        string name = body.S0();
        if (dim < 2) return;
        var read = new List<(int, int, Vec2)>();
        while (!body.AtEnd)
        {
            int p = pointBase + body.VX();
            int pi = polygonBase + body.VX();
            var uv = new Vec2(body.F4(), body.F4());
            for (int i = 2; i < dim; i++) body.F4();
            if (p >= mesh.Points.Count) throw body.Error($"maps point {p - pointBase} but the layer has only {mesh.Points.Count - pointBase}");
            if (polygonBase < 0 || pi >= mesh.Polygons.Count) throw body.Error($"maps polygon {pi - polygonBase}, which doesn't exist");
            read.Add((p, pi, uv));
        }
        mesh.AddUVMap(name);
        foreach (var (p, pi, uv) in read)
        {
            var poly = mesh.Polygons[pi];
            poly.CornerUVs ??= [];
            if (!poly.CornerUVs.TryGetValue(name, out var corners)) poly.CornerUVs[name] = corners = [];
            corners[p] = uv;
        }
    }

    /// <summary>Writes a VMAP for each of a mesh's UV maps (in the order its points were given UVs).</summary>
    public static void WriteVmaps(IffWriter w, Mesh mesh)
    {
        foreach (var map in mesh.UVMaps)
            w.Chunk("VMAP", () =>
            {
                w.Id4("TXUV");
                w.U2(Dimension);
                w.S0(map.Name);
                foreach (var (p, uv) in map.Points)
                {
                    if (p < 0 || p >= mesh.Points.Count) continue;
                    w.VX(p);
                    w.F4((float)uv.X);
                    w.F4((float)uv.Y);
                }
            });
    }

    /// <summary>Writes a VMAD for each UV map any polygon has corner UVs in (polygons counted from the layer's first).</summary>
    public static void WriteVmads(IffWriter w, Mesh mesh)
    {
        foreach (var map in mesh.UVMaps)
        {
            if (!mesh.Polygons.Any(p => p.CornerUVs?.ContainsKey(map.Name) == true)) continue;
            w.Chunk("VMAD", () =>
            {
                w.Id4("TXUV");
                w.U2(Dimension);
                w.S0(map.Name);
                for (int pi = 0; pi < mesh.Polygons.Count; pi++)
                {
                    var poly = mesh.Polygons[pi];
                    if (poly.CornerUVs == null || !poly.CornerUVs.TryGetValue(map.Name, out var corners)) continue;
                    foreach (var (p, uv) in corners)
                    {
                        if (!poly.Points.Contains(p)) continue;
                        w.VX(p);
                        w.VX(pi);
                        w.F4((float)uv.X);
                        w.F4((float)uv.Y);
                    }
                }
            });
        }
    }
}
