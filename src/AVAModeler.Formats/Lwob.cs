using System.Runtime.CompilerServices;
using AVAModeler.Core;

namespace AVAModeler.Formats;

/// <summary>
/// What an LWOB or LWLO file held beyond the document itself: its form type, the order of its chunks, the top-level
/// chunks this reader doesn't understand, and the layers' flag words. <see cref="Lwob.Read(byte[])"/> attaches one to
/// the document it returns (see <see cref="Lwob.InfoOf"/>), and <see cref="Lwob.Write(Document)"/> uses it to write
/// the file back unchanged. While the document's layers and surface list still fit the original layout, chunks are
/// written in their original order (points and polygons regenerated from the document); otherwise the standard
/// order is used, with the unknown chunks at the end.
/// </summary>
public sealed class LwoInfo
{
    /// <summary>"LWOB" or "LWLO".</summary>
    public string FormType { get; }
    /// <summary>Top-level chunks kept as read: unknown IDs, and SURF chunks for names the SRFS list doesn't have.</summary>
    public IReadOnlyList<RawChunk> UnknownChunks => Unknown;
    /// <summary>Each LWLO layer's flag word, by layer number, written back as read.</summary>
    public IReadOnlyDictionary<int, ushort> LayerFlags => Flags;

    internal readonly List<RawChunk> Unknown = [];
    internal readonly Dictionary<int, ushort> Flags = [];
    /// <summary>The chunks in file order: Layer is set for LAYR, PNTS and POLS, Surface for SURF, Raw for unknown chunks.</summary>
    internal readonly List<Entry> Layout = [];
    /// <summary>False when the layout can't be replayed (a chunk that should appear once per layer appeared twice).</summary>
    internal bool LayoutUsable = true;
    internal string[] SurfaceNames = [];

    internal readonly record struct Entry(string Id, int Layer = -1, int Surface = -1, RawChunk? Raw = null);

    internal LwoInfo(string formType) => FormType = formType;
}

/// <summary>
/// LightWave 3.x–5.x objects: LWOB, and the LWLO variant with layers from 5.5. Reads and writes them so that a file
/// read and written back without changes is byte-identical (see <see cref="LwoInfo"/> and <see cref="Surface.Raw"/>).
/// Surfaces' textures are read into texture layers (see <see cref="LwobTextures"/>). UV maps, which LWOB didn't have,
/// are written as LWO2-style VMAP and VMAD chunks after each layer's polygons, and read back (see <see cref="VertexMaps"/>).
/// </summary>
public static class Lwob
{
    private const ushort LuminousFlag = 1, SmoothingFlag = 4, DoubleSidedFlag = 256;
    private const int MaxLayers = 4096;

    /// <summary>The SURF sub-chunks that map onto <see cref="Surface"/>'s typed fields, in the order they're written.</summary>
    private static readonly string[] TypedIds =
        ["COLR", "FLAG", "LUMI", "VLUM", "DIFF", "VDIF", "SPEC", "VSPC", "GLOS", "REFL", "VRFL", "TRAN", "VTRN", "SMAN"];

    private static readonly ConditionalWeakTable<Document, LwoInfo> Infos = new();

    /// <summary>What was kept from the LWOB/LWLO file a document was read from, or null if it wasn't read from one.</summary>
    public static LwoInfo? InfoOf(Document doc) => Infos.TryGetValue(doc, out var info) ? info : null;

    public static Document Read(Stream stream) => Read(Loading.ReadAll(stream));

    public static Document Read(byte[] data)
    {
        var form = IffReader.Form(data, out string type);
        if (type is not ("LWOB" or "LWLO")) throw new FormatException($"Not an LWOB or LWLO object: the FORM type is {type}.");
        var doc = new Document { Surfaces = [] };
        var info = new LwoInfo(type);
        var names = new List<string>();
        var defined = new Dictionary<string, List<RawChunk>>();
        var once = new HashSet<(string, int, int)>();
        int layer = 0, pointBase = 0, polygonBase = -1;
        var uvChunks = new Dictionary<(string, int), int>();

        void Note(string id, int layerIndex = -1, int surface = -1)
        {
            if (!once.Add((id, layerIndex, surface))) info.LayoutUsable = false;
            info.Layout.Add(new LwoInfo.Entry(id, layerIndex, surface));
        }

        void Keep(string id, IffReader body)
        {
            var raw = new RawChunk(id, body.Rest());
            info.Unknown.Add(raw);
            info.Layout.Add(new LwoInfo.Entry(id, Raw: raw));
        }

        while (!form.AtEnd)
        {
            var body = form.Chunk(false, out string id);
            var mesh = doc.Layers[layer].Mesh;
            switch (id)
            {
                case "LAYR" when type == "LWLO":
                    layer = body.U2();
                    if (layer >= MaxLayers) throw body.Error($"layer number {layer} is too high");
                    var l = Loading.LayerAt(doc, layer);
                    info.Flags[layer] = body.U2();
                    l.Name = body.AtEnd ? "" : body.S0();
                    pointBase = l.Mesh.Points.Count;
                    polygonBase = -1;
                    Note(id, layer);
                    break;
                case "PNTS":
                    if (body.Remaining % 12 != 0) throw body.Error("length isn't a multiple of 12");
                    pointBase = mesh.Points.Count;
                    while (!body.AtEnd) mesh.Points.Add(body.Vec12());
                    Note(id, layer);
                    break;
                case "SRFS":
                    while (!body.AtEnd) names.Add(body.S0());
                    Note(id);
                    break;
                case "POLS":
                    polygonBase = mesh.Polygons.Count;
                    while (!body.AtEnd) mesh.Polygons.Add(ReadPolygon(body, mesh.Points.Count, pointBase));
                    Note(id, layer);
                    break;
                case "VMAP":
                case "VMAD":
                    var whole = body.Named(id);
                    try
                    {
                        if (id == "VMAP") VertexMaps.ReadVmap(body, mesh, pointBase);
                        else VertexMaps.ReadVmad(body, mesh, pointBase, polygonBase);
                    }
                    catch (FormatException)
                    {
                        // UVs that don't fit the geometry are kept as an unknown chunk rather than losing the object.
                        Keep(id, whole);
                        break;
                    }
                    // Several of each per layer are usual (one per map), so each is numbered to keep the layout replayable.
                    int n = uvChunks.GetValueOrDefault((id, layer));
                    uvChunks[(id, layer)] = n + 1;
                    Note(id, layer, n);
                    break;
                case "SURF":
                    var surf = body.Named("SURF");
                    string name = surf.S0();
                    if (!names.Contains(name) || defined.ContainsKey(name))
                    {
                        Keep(id, body);
                        break;
                    }
                    defined[name] = ReadSubchunks(surf.Named($"SURF \"{name}\""));
                    Note(id, surface: names.IndexOf(name));
                    break;
                default:
                    Keep(id, body);
                    break;
            }
        }

        doc.Surfaces = names.Select(n => new Surface(n)).ToList();
        int highest = doc.Layers.SelectMany(l => l.Mesh.Polygons).SelectMany(Loading.Flatten).Select(p => p.Surface).DefaultIfEmpty(-1).Max();
        while (doc.Surfaces.Count <= highest) doc.Surfaces.Add(new Surface($"Surface {doc.Surfaces.Count + 1}"));
        if (doc.Surfaces.Count == 0) doc.Surfaces.Add(new Surface("Default"));
        foreach (var (name, chunks) in defined)
        {
            var s = doc.Surfaces[names.IndexOf(name)];
            Apply(s, chunks, $"SURF \"{name}\"");
            s.Raw = chunks;
            s.RawSignature = s.Signature();
        }
        info.SurfaceNames = doc.Surfaces.Select(s => s.Name).ToArray();
        foreach (var l in doc.Layers) l.Mesh.Touch();
        Loading.ShowFirstLayer(doc);
        Infos.AddOrUpdate(doc, info);
        return doc;
    }

    /// <summary>One POLS polygon: a point count, the points, a surface (1-based; negative when detail polygons follow).</summary>
    private static Polygon ReadPolygon(IffReader r, int pointCount, int pointBase)
    {
        int n = r.U2();
        var points = new int[n];
        for (int i = 0; i < n; i++)
        {
            int p = pointBase + r.U2();
            if (p >= pointCount) throw r.Error($"polygon uses point {p} but there are only {pointCount}");
            points[i] = p;
        }
        int surface = r.I2();
        // A surface of 0 is invalid (they count from 1); such files are read as if it were 1.
        var poly = new Polygon(points, Math.Max(0, Math.Abs(surface) - 1));
        if (surface < 0)
        {
            int details = r.I2();
            if (details < 0) throw r.Error($"negative detail polygon count {details}");
            poly.Details = [];
            for (int i = 0; i < details; i++) poly.Details.Add(ReadPolygon(r, pointCount, pointBase));
        }
        return poly;
    }

    private static List<RawChunk> ReadSubchunks(IffReader r)
    {
        var list = new List<RawChunk>();
        while (!r.AtEnd)
        {
            var body = r.Chunk(true, out string id);
            list.Add(new RawChunk(id, body.Rest()));
        }
        return list;
    }

    /// <summary>
    /// Sets a surface's typed fields from its sub-chunks (the floating-point V* forms win over the fixed-point ones), and
    /// its texture layers.
    /// </summary>
    private static void Apply(Surface s, List<RawChunk> chunks, string context)
    {
        s.Textures = LwobTextures.Read(chunks, context);
        foreach (var c in chunks.Where(c => TypedIds.Contains(c.Id)).OrderBy(c => c.Id == "FLAG" ? 0 : c.Id[0] == 'V' ? 2 : 1))
        {
            var r = new IffReader(c.Data, 0, c.Data.Length, $"{context} {c.Id}");
            switch (c.Id)
            {
                case "COLR": s.Color = new Rgb(r.U1(), r.U1(), r.U1()); break;
                case "FLAG":
                    ushort flags = r.U2();
                    s.Smoothing = (flags & SmoothingFlag) != 0;
                    s.DoubleSided = (flags & DoubleSidedFlag) != 0;
                    // Before LUMI existed, the luminous flag alone meant fully luminous.
                    if ((flags & LuminousFlag) != 0) s.Luminosity = 1;
                    break;
                case "LUMI": s.Luminosity = r.U2() / 256.0; break;
                case "DIFF": s.Diffuse = r.U2() / 256.0; break;
                case "SPEC": s.Specular = r.U2() / 256.0; break;
                case "REFL": s.Reflection = r.U2() / 256.0; break;
                case "TRAN": s.Transparency = r.U2() / 256.0; break;
                case "VLUM": s.Luminosity = r.F4(); break;
                case "VDIF": s.Diffuse = r.F4(); break;
                case "VSPC": s.Specular = r.F4(); break;
                case "VRFL": s.Reflection = r.F4(); break;
                case "VTRN": s.Transparency = r.F4(); break;
                case "GLOS": s.Glossiness = r.U2(); break;
                case "SMAN": s.SmoothingAngle = r.F4() * 180.0 / Math.PI; break;
            }
        }
    }

    /// <summary>
    /// The sub-chunks to write for a surface: the ones read, unchanged, if the surface hasn't been edited; otherwise
    /// those with the typed fields' sub-chunks replaced (or added before any others), and the texture layers' sub-chunks
    /// (see <see cref="LwobTextures"/>) where the first texture was, or at the end. Sub-chunks this doesn't model are kept.
    /// </summary>
    internal static List<RawChunk> SurfaceChunks(Surface s)
    {
        if (s.Raw != null && s.RawSignature == s.Signature()) return s.Raw;
        var list = s.Raw == null ? new List<RawChunk>() : [.. s.Raw];
        var oldFlag = list.Find(c => c.Id == "FLAG");
        int flags = oldFlag is { Data.Length: >= 2 } ? oldFlag.Data[0] << 8 | oldFlag.Data[1] : 0;
        flags &= ~(LuminousFlag | SmoothingFlag | DoubleSidedFlag);
        if (s.Luminosity > 0) flags |= LuminousFlag;
        if (s.Smoothing) flags |= SmoothingFlag;
        if (s.DoubleSided) flags |= DoubleSidedFlag;

        void Set(string id, bool wanted, Action<IffWriter> data)
        {
            int at = list.FindIndex(c => c.Id == id);
            if (at >= 0) list[at] = new RawChunk(id, IffWriter.Build(data));
            else if (wanted)
            {
                int before = list.FindIndex(c => !TypedIds.Contains(c.Id));
                list.Insert(before < 0 ? list.Count : before, new RawChunk(id, IffWriter.Build(data)));
            }
        }

        Set("COLR", true, w => { w.U1(s.Color.R); w.U1(s.Color.G); w.U1(s.Color.B); w.U1(0); });
        Set("FLAG", true, w => w.U2((ushort)flags));
        Set("LUMI", s.Luminosity > 0, w => w.U2(Fixed(s.Luminosity)));
        Set("VLUM", s.Luminosity > 0, w => w.F4((float)s.Luminosity));
        Set("DIFF", true, w => w.U2(Fixed(s.Diffuse)));
        Set("VDIF", true, w => w.F4((float)s.Diffuse));
        Set("SPEC", s.Specular > 0, w => w.U2(Fixed(s.Specular)));
        Set("VSPC", s.Specular > 0, w => w.F4((float)s.Specular));
        Set("GLOS", s.Specular > 0, w => w.U2((ushort)Math.Clamp(Math.Round(s.Glossiness), 0, ushort.MaxValue)));
        Set("REFL", s.Reflection > 0, w => w.U2(Fixed(s.Reflection)));
        Set("VRFL", s.Reflection > 0, w => w.F4((float)s.Reflection));
        Set("TRAN", s.Transparency > 0, w => w.U2(Fixed(s.Transparency)));
        Set("VTRN", s.Transparency > 0, w => w.F4((float)s.Transparency));
        Set("SMAN", s.Smoothing, w => w.F4((float)(s.SmoothingAngle * Math.PI / 180.0)));
        return WithTextures(list, s.Textures);
    }

    /// <summary>A surface's sub-chunks with the texture runs taken out and the layers' put back where the first was.</summary>
    private static List<RawChunk> WithTextures(List<RawChunk> chunks, List<TextureLayer> layers)
    {
        if (layers.Count == 0 && !chunks.Any(c => LwobTextures.IsHeader(c.Id))) return chunks;
        var kept = new List<RawChunk>(chunks.Count);
        int at = -1;
        bool inTexture = false;
        foreach (var c in chunks)
        {
            if (LwobTextures.IsHeader(c.Id))
            {
                if (at < 0) at = kept.Count;
                inTexture = true;
                continue;
            }
            if (inTexture && LwobTextures.IsSetting(c.Id)) continue;
            inTexture = false;
            kept.Add(c);
        }
        kept.InsertRange(at < 0 ? kept.Count : at, layers.SelectMany(LwobTextures.Write));
        return kept;
    }

    private static ushort Fixed(double v) => (ushort)Math.Clamp(Math.Round(v * 256), 0, ushort.MaxValue);

    public static void Write(Document doc, Stream stream) => stream.Write(Write(doc));

    /// <summary>
    /// Writes a document as LWOB when at most one layer has anything in it, otherwise as LWLO with a LAYR chunk per
    /// non-empty layer. A document read from an LWOB/LWLO file keeps its original form and chunk order while it fits.
    /// Every surface is written, used or not.
    /// </summary>
    public static byte[] Write(Document doc)
    {
        foreach (var l in doc.Layers) CheckPolygons(doc, l.Mesh);
        var used = Enumerable.Range(0, doc.Layers.Count).Where(i => !doc.Layers[i].Mesh.IsEmpty).ToList();
        var info = InfoOf(doc);
        bool replay = info != null && Fits(doc, info, used);
        string type = replay ? info!.FormType : used.Count > 1 ? "LWLO" : "LWOB";
        var w = new IffWriter();
        w.Chunk("FORM", () =>
        {
            w.Id4(type);
            if (replay)
            {
                var written = new HashSet<(string, int)>();
                bool Has(string id, int layer) => info!.Layout.Any(x => x.Id == id && x.Layer == layer && x.Raw == null);
                // A layer's UV chunks go where they were, or after its polygons if it had none.
                void UVs(string at, int layer)
                {
                    bool vmap = Has("VMAP", layer), vmad = Has("VMAD", layer);
                    bool maps, corners;
                    if (at == "POLS") maps = corners = !vmap && !vmad;
                    else if (at == "VMAP")
                    {
                        maps = written.Add((at, layer));
                        corners = maps && !vmad;
                    }
                    else
                    {
                        corners = written.Add((at, layer));
                        maps = corners && !vmap;
                    }
                    if (maps) VertexMaps.WriteVmaps(w, doc.Layers[layer].Mesh);
                    if (corners) VertexMaps.WriteVmads(w, doc.Layers[layer].Mesh);
                }
                foreach (var e in info!.Layout)
                    switch (e.Id)
                    {
                        case "LAYR" when e.Raw == null: WriteLayer(w, doc, e.Layer, info); break;
                        case "PNTS" when e.Raw == null: WritePoints(w, doc.Layers[e.Layer].Mesh); break;
                        case "POLS" when e.Raw == null:
                            WritePolygons(w, doc.Layers[e.Layer].Mesh);
                            UVs(e.Id, e.Layer);
                            break;
                        case "VMAP" or "VMAD" when e.Raw == null: UVs(e.Id, e.Layer); break;
                        case "SRFS" when e.Raw == null: WriteNames(w, doc); break;
                        case "SURF" when e.Raw == null: WriteSurface(w, doc.Surfaces[e.Surface]); break;
                        default: w.Chunk(e.Raw!); break;
                    }
                return;
            }
            if (type == "LWOB")
            {
                var mesh = doc.Layers[used.DefaultIfEmpty(0).First()].Mesh;
                WritePoints(w, mesh);
                WriteNames(w, doc);
                WritePolygons(w, mesh);
                VertexMaps.WriteVmaps(w, mesh);
                VertexMaps.WriteVmads(w, mesh);
            }
            else
                for (int i = 0; i < used.Count; i++)
                {
                    var mesh = doc.Layers[used[i]].Mesh;
                    WriteLayer(w, doc, used[i], info);
                    WritePoints(w, mesh);
                    if (i == 0) WriteNames(w, doc);
                    WritePolygons(w, mesh);
                    VertexMaps.WriteVmaps(w, mesh);
                    VertexMaps.WriteVmads(w, mesh);
                }
            foreach (var s in doc.Surfaces) WriteSurface(w, s);
            if (info != null) foreach (var c in info.Unknown) w.Chunk(c);
        });
        return w.ToArray();
    }

    /// <summary>Whether the original chunk layout still describes the document, so it can be replayed.</summary>
    private static bool Fits(Document doc, LwoInfo info, List<int> used)
    {
        bool Has(string id, int layer) => info.Layout.Any(e => e.Id == id && e.Layer == layer && e.Raw == null);
        return info.LayoutUsable
            && doc.Surfaces.Select(s => s.Name).SequenceEqual(info.SurfaceNames)
            && info.Layout.All(e => e.Layer < doc.Layers.Count)
            && used.All(i => (doc.Layers[i].Mesh.Points.Count == 0 || Has("PNTS", i)) && (doc.Layers[i].Mesh.Polygons.Count == 0 || Has("POLS", i))
                && (doc.Layers[i].Mesh.UVMaps.Count == 0 || Has("POLS", i) || Has("VMAP", i) || Has("VMAD", i)))
            && (doc.Layers.All(l => l.Mesh.Polygons.Count == 0) || Has("SRFS", -1));
    }

    private static void CheckPolygons(Document doc, Mesh mesh)
    {
        if (mesh.Points.Count > ushort.MaxValue + 1)
            throw new InvalidOperationException($"LWOB holds at most 65536 points per layer; a layer has {mesh.Points.Count}.");
        foreach (var p in mesh.Polygons.SelectMany(Loading.Flatten))
        {
            if (p.Count > ushort.MaxValue) throw new InvalidOperationException($"A polygon has {p.Count} points; LWOB holds at most 65535.");
            if (p.Surface < 0 || p.Surface >= doc.Surfaces.Count || p.Surface >= short.MaxValue)
                throw new InvalidOperationException($"A polygon uses surface {p.Surface}, which isn't in the surface list.");
        }
    }

    private static void WriteLayer(IffWriter w, Document doc, int layer, LwoInfo? info) => w.Chunk("LAYR", () =>
    {
        w.U2((ushort)layer);
        w.U2(info != null && info.Flags.TryGetValue(layer, out var flags) ? flags : (ushort)0);
        w.S0(doc.Layers[layer].Name);
    });

    private static void WritePoints(IffWriter w, Mesh mesh) => w.Chunk("PNTS", () =>
    {
        foreach (var p in mesh.Points)
        {
            w.F4((float)p.X);
            w.F4((float)p.Y);
            w.F4((float)p.Z);
        }
    });

    private static void WriteNames(IffWriter w, Document doc) => w.Chunk("SRFS", () =>
    {
        foreach (var s in doc.Surfaces) w.S0(s.Name);
    });

    private static void WritePolygons(IffWriter w, Mesh mesh) => w.Chunk("POLS", () =>
    {
        foreach (var p in mesh.Polygons) WritePolygon(w, p);
    });

    private static void WritePolygon(IffWriter w, Polygon p)
    {
        w.U2((ushort)p.Count);
        foreach (int i in p.Points) w.U2((ushort)i);
        short surface = (short)(p.Surface + 1);
        if (p.Details == null)
        {
            w.I2(surface);
            return;
        }
        w.I2((short)-surface);
        if (p.Details.Count > short.MaxValue) throw new InvalidOperationException($"A polygon has {p.Details.Count} detail polygons; LWOB holds at most 32767.");
        w.I2((short)p.Details.Count);
        foreach (var d in p.Details) WritePolygon(w, d);
    }

    private static void WriteSurface(IffWriter w, Surface s) => w.Chunk("SURF", () =>
    {
        w.S0(s.Name);
        foreach (var c in SurfaceChunks(s)) w.Chunk(c, shortLength: true);
    });
}
