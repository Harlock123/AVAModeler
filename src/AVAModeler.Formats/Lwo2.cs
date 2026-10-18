using AVAModeler.Core;

namespace AVAModeler.Formats;

/// <summary>
/// Reads LightWave 6+ objects (FORM/LWO2) for compatibility: layers, points, FACE and PTCH polygons (patches come in
/// as their cage faces), surface tags, and each surface's basic settings. Vertex maps, curves, images, envelopes and
/// textures are skipped. Writing is LWOB/LWLO only (<see cref="Lwob"/>).
/// </summary>
public static class Lwo2
{
    public static Document Read(Stream stream) => Read(Loading.ReadAll(stream));

    /// <summary>
    /// Reads an LWO2 file. Layer number n goes to <c>Layers[n]</c> (the list grows as needed) with its name and
    /// pivot; the first layer with anything in it is put in the foreground and the rest turned off. Polygons without
    /// a SURF tag get the surface "Default".
    /// </summary>
    public static Document Read(byte[] data)
    {
        var form = IffReader.Form(data, out string type);
        if (type != "LWO2") throw new FormatException($"Not an LWO2 object: the FORM type is {type}.");
        var doc = new Document { Surfaces = [] };
        var tags = new List<string>();
        var mesh = doc.Layers[0].Mesh;
        int pointBase = 0;
        // Where the polygons of the last POLS chunk start in the mesh, for PTAG; -1 if it was skipped.
        int polygonsStart = -1, polygonsEnd = -1;
        var untagged = new HashSet<Polygon>();

        while (!form.AtEnd)
        {
            var body = form.Chunk(false, out string id);
            switch (id)
            {
                case "TAGS":
                    while (!body.AtEnd) tags.Add(body.S0());
                    break;
                case "LAYR":
                    int number = body.U2();
                    if (number >= 4096) throw body.Error($"layer number {number} is too high");
                    body.U2();
                    var layer = Loading.LayerAt(doc, number);
                    layer.Pivot = body.Vec12();
                    if (!body.AtEnd) layer.Name = body.S0();
                    mesh = layer.Mesh;
                    pointBase = mesh.Points.Count;
                    polygonsStart = -1;
                    break;
                case "PNTS":
                    if (body.Remaining % 12 != 0) throw body.Error("length isn't a multiple of 12");
                    pointBase = mesh.Points.Count;
                    while (!body.AtEnd) mesh.Points.Add(body.Vec12());
                    break;
                case "POLS":
                    string kind = body.Id4();
                    if (kind is not ("FACE" or "PTCH"))
                    {
                        polygonsStart = -1;
                        break;
                    }
                    polygonsStart = mesh.Polygons.Count;
                    while (!body.AtEnd)
                    {
                        int n = body.U2() & 0x3FF;
                        var points = new int[n];
                        for (int i = 0; i < n; i++)
                        {
                            int p = pointBase + body.VX();
                            if (p >= mesh.Points.Count) throw body.Error($"polygon uses point {p} but there are only {mesh.Points.Count}");
                            points[i] = p;
                        }
                        var poly = new Polygon(points);
                        mesh.Polygons.Add(poly);
                        untagged.Add(poly);
                    }
                    polygonsEnd = mesh.Polygons.Count;
                    break;
                case "PTAG":
                    if (body.Id4() != "SURF" || polygonsStart < 0) break;
                    while (!body.AtEnd)
                    {
                        int p = polygonsStart + body.VX();
                        int tag = body.U2();
                        if (p >= polygonsEnd) throw body.Error($"tags polygon {p - polygonsStart} but the POLS chunk has only {polygonsEnd - polygonsStart}");
                        if (tag >= tags.Count) throw body.Error($"uses tag {tag} but TAGS has only {tags.Count}");
                        mesh.Polygons[p].Surface = doc.SurfaceIndex(tags[tag]);
                        untagged.Remove(mesh.Polygons[p]);
                    }
                    break;
                case "SURF":
                    ReadSurface(doc, body);
                    break;
            }
        }

        if (untagged.Count > 0)
        {
            int fallback = doc.SurfaceIndex("Default");
            foreach (var p in untagged) p.Surface = fallback;
        }
        if (doc.Surfaces.Count == 0) doc.Surfaces.Add(new Surface("Default"));
        foreach (var l in doc.Layers) l.Mesh.Touch();
        Loading.ShowFirstLayer(doc);
        return doc;
    }

    /// <summary>
    /// A SURF chunk: name, source surface, then sub-chunks. GLOS (0–1) becomes LWOB-style glossiness as 2^(10g), so
    /// 40% is 16 (low), 60% is 64, 80% is 256 and 100% is 1024, which is how LightWave 6 converted old surfaces.
    /// Smoothing is on when SMAN is above zero; SIDE 3 means double-sided.
    /// </summary>
    private static void ReadSurface(Document doc, IffReader body)
    {
        string name = body.S0();
        body.S0();
        var s = doc.Surfaces[doc.SurfaceIndex(name)];
        var r = body.Named($"SURF \"{name}\"");
        while (!r.AtEnd)
        {
            var c = r.Chunk(true, out string id);
            switch (id)
            {
                case "COLR": s.Color = Rgb.FromVec(c.Vec12()); break;
                case "DIFF": s.Diffuse = c.F4(); break;
                case "SPEC": s.Specular = c.F4(); break;
                case "LUMI": s.Luminosity = c.F4(); break;
                case "TRAN": s.Transparency = c.F4(); break;
                case "REFL": s.Reflection = c.F4(); break;
                case "GLOS": s.Glossiness = Math.Pow(2, 10 * Math.Clamp(c.F4(), 0, 1)); break;
                case "SMAN":
                    double angle = c.F4() * 180.0 / Math.PI;
                    s.Smoothing = angle > 0;
                    if (s.Smoothing) s.SmoothingAngle = angle;
                    break;
                case "SIDE": s.DoubleSided = c.U2() == 3; break;
            }
        }
    }
}
