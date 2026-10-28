using AVAModeler.Core;

namespace AVAModeler.Formats;

/// <summary>
/// Reads LightWave 6+ objects (FORM/LWO2) for compatibility: layers, points, FACE and PTCH polygons (patches come in
/// as their cage faces), surface tags, each surface's basic settings, UV maps (VMAP and VMAD of type TXUV), and image
/// map texture blocks (BLOK with an IMAP header) for colour, diffuse, specular, luminosity, reflection, transparency
/// and bump, with their images from the CLIP chunks. Other vertex maps, curves, envelopes, procedural and gradient
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
        var clips = new Dictionary<uint, string>();
        var clipped = new List<(TextureLayer Layer, int Clip)>();

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
                // UV maps and images are extras: one that can't be read is skipped (with what came before it kept).
                case "VMAP":
                    Lenient(() => VertexMaps.ReadVmap(body, mesh, pointBase));
                    break;
                case "VMAD":
                    if (polygonsStart >= 0) Lenient(() => VertexMaps.ReadVmad(body, mesh, pointBase, polygonsStart));
                    break;
                case "CLIP":
                    Lenient(() => ReadClip(body, clips));
                    break;
                case "SURF":
                    ReadSurface(doc, body, clipped);
                    break;
            }
        }
        foreach (var (layer, clip) in clipped) layer.Image = clips.GetValueOrDefault((uint)clip, "");

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
    private static void ReadSurface(Document doc, IffReader body, List<(TextureLayer, int)> clipped)
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
                case "BLOK":
                    (TextureLayer, int)? block = null;
                    Lenient(() => block = ReadBlock(c));
                    if (block is var (layer, clip))
                    {
                        s.Textures.Add(layer);
                        clipped.Add((layer, clip));
                    }
                    break;
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

    private static void Lenient(Action read)
    {
        try { read(); }
        catch (FormatException) { }
    }

    /// <summary>A CLIP: its index and, for a still image, the file name.</summary>
    private static void ReadClip(IffReader body, Dictionary<uint, string> clips)
    {
        uint index = body.U4();
        while (!body.AtEnd)
        {
            var c = body.Chunk(true, out string id);
            if (id == "STIL") clips[index] = c.S0();
        }
    }

    private static readonly Dictionary<string, TextureChannel> Channels = new()
    {
        ["COLR"] = TextureChannel.Color, ["DIFF"] = TextureChannel.Diffuse, ["SPEC"] = TextureChannel.Specular,
        ["LUMI"] = TextureChannel.Luminosity, ["REFL"] = TextureChannel.Reflection, ["TRAN"] = TextureChannel.Transparency,
        ["BUMP"] = TextureChannel.Bump,
    };

    /// <summary>
    /// A surface BLOK: an image map's texture layer and the CLIP index of its image, or null for blocks this doesn't
    /// draw (procedurals, gradients, shaders, disabled layers, and channels such as glossiness).
    /// </summary>
    private static (TextureLayer, int)? ReadBlock(IffReader block)
    {
        var header = block.Chunk(true, out string kind);
        if (kind != "IMAP") return null;
        header.S0();
        var t = new TextureLayer();
        bool known = true, enabled = true;
        while (!header.AtEnd)
        {
            var c = header.Chunk(true, out string id);
            switch (id)
            {
                case "CHAN":
                    known = Channels.TryGetValue(c.Id4(), out var channel);
                    t.Channel = channel;
                    break;
                case "ENAB": enabled = c.U2() != 0; break;
                case "NEGA": t.Negative = c.U2() != 0; break;
                case "OPAC":
                    t.Blend = c.U2() switch { 3 => TextureBlend.Multiply, 7 => TextureBlend.Additive, _ => TextureBlend.Normal };
                    t.Opacity = c.F4();
                    break;
            }
        }
        int clip = -1;
        while (!block.AtEnd)
        {
            var c = block.Chunk(true, out string id);
            switch (id)
            {
                case "TMAP":
                    while (!c.AtEnd)
                    {
                        var m = c.Chunk(true, out string mid);
                        if (mid == "CNTR") t.Center = m.Vec12();
                        else if (mid == "SIZE") t.Size = m.Vec12();
                        else if (mid == "CSYS") t.WorldCoordinates = m.U2() == 1;
                    }
                    break;
                case "PROJ":
                    int proj = c.U2();
                    t.Projection = proj switch
                    {
                        1 => TextureProjection.Cylindrical, 2 => TextureProjection.Spherical, 3 => TextureProjection.Cubic,
                        5 => TextureProjection.UV, _ => TextureProjection.Planar,
                    };
                    // Front projection is a Layout trick this doesn't do.
                    if (proj == 4) known = false;
                    break;
                case "AXIS": t.Axis = (Axis)Math.Clamp((int)c.U2(), 0, 2); break;
                case "IMAG": clip = c.VX(); break;
                case "WRAP":
                    t.WidthTiling = Tiling(c.U2());
                    t.HeightTiling = Tiling(c.U2());
                    break;
                case "WRPW": t.WidthWrap = c.F4(); break;
                case "WRPH": t.HeightWrap = c.F4(); break;
                case "VMAP": t.UVMap = c.S0(); break;
                case "PIXB": t.PixelBlending = c.U2() != 0; break;
                case "AAST":
                    t.Antialiasing = c.U2() != 0;
                    t.AntialiasingStrength = c.F4();
                    break;
                case "TAMP": t.Amplitude = c.F4(); break;
            }
        }
        return known && enabled ? (t, clip) : null;
    }

    /// <summary>LWO2's WRAP codes: 0 reset, 1 repeat, 2 mirror, 3 edge.</summary>
    private static TextureTiling Tiling(int code) => code switch
    {
        0 => TextureTiling.Reset, 2 => TextureTiling.Mirror, 3 => TextureTiling.Edge, _ => TextureTiling.Repeat,
    };
}
