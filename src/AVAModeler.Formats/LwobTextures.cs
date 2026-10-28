using AVAModeler.Core;

namespace AVAModeler.Formats;

/// <summary>
/// LightWave 5's surface textures in LWOB: each texture starts with a sub-chunk naming its channel and type (CTEX for
/// colour, DTEX diffuse, STEX specular, RTEX reflection, TTEX transparency, LTEX luminosity, BTEX bump, holding
/// "Planar Image Map" and so on), followed by its settings: TFLG (axis and flags), TSIZ, TCTR, TIMG (the image), TWRP
/// (width and height tiling), TFP0 and TFP1 (the cylindrical and spherical wrap amounts), TAAS, TOPC (opacity), TAMP
/// (bump amplitude), TVAL and the rest. Every texture sub-chunk is kept with its layer, so a layer reads and writes back
/// byte for byte, and changes rewrite only the sub-chunks they touch. A UV-mapped layer, which LightWave 5 didn't have,
/// is written as a planar image map with a TVMP sub-chunk naming its UV map (skipped by LightWave, read back here).
/// </summary>
internal static class LwobTextures
{
    private static readonly Dictionary<string, TextureChannel> Channels = new()
    {
        ["CTEX"] = TextureChannel.Color, ["DTEX"] = TextureChannel.Diffuse, ["STEX"] = TextureChannel.Specular,
        ["RTEX"] = TextureChannel.Reflection, ["TTEX"] = TextureChannel.Transparency, ["LTEX"] = TextureChannel.Luminosity,
        ["BTEX"] = TextureChannel.Bump,
    };

    private static readonly Dictionary<string, TextureProjection> Types = new()
    {
        ["Planar Image Map"] = TextureProjection.Planar, ["Cylindrical Image Map"] = TextureProjection.Cylindrical,
        ["Spherical Image Map"] = TextureProjection.Spherical, ["Cubic Image Map"] = TextureProjection.Cubic,
    };

    private const ushort AxisX = 1, AxisY = 2, AxisZ = 4, WorldCoordinates = 8, NegativeImage = 16, PixelBlending = 32, Antialiasing = 64;

    public static bool IsHeader(string id) => Channels.ContainsKey(id);

    /// <summary>Whether a sub-chunk belongs to the texture before it (T-something, but not the surface's own TRAN or TRNL).</summary>
    public static bool IsSetting(string id) => id[0] == 'T' && id is not ("TRAN" or "TRNL") && !IsHeader(id);

    private static string HeaderId(TextureChannel c) => Channels.First(kv => kv.Value == c).Key;

    /// <summary>The texture layers in a surface's sub-chunks, in order, each with its own sub-chunks kept.</summary>
    public static List<TextureLayer> Read(List<RawChunk> chunks, string context)
    {
        var layers = new List<TextureLayer>();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (!IsHeader(chunks[i].Id)) continue;
            var raw = new List<RawChunk> { chunks[i] };
            while (i + 1 < chunks.Count && IsSetting(chunks[i + 1].Id)) raw.Add(chunks[++i]);
            TextureLayer t;
            try
            {
                t = Parse(raw, context);
            }
            catch (FormatException)
            {
                // A texture this can't make sense of is still kept, as it was, like a procedural one.
                t = new TextureLayer { Channel = Channels[raw[0].Id], Procedural = "(unreadable)" };
            }
            t.Raw = raw;
            t.RawSignature = t.Signature();
            layers.Add(t);
        }
        return layers;
    }

    private static TextureLayer Parse(List<RawChunk> raw, string context)
    {
        var t = new TextureLayer { Channel = Channels[raw[0].Id] };
        string type = Reader(raw[0], context).S0();
        if (Types.TryGetValue(type, out var projection)) t.Projection = projection;
        else t.Procedural = type;
        foreach (var c in raw.Skip(1))
        {
            var r = Reader(c, context);
            switch (c.Id)
            {
                case "TFLG":
                    ushort flags = r.U2();
                    t.Axis = (flags & AxisX) != 0 ? Axis.X : (flags & AxisY) != 0 ? Axis.Y : Axis.Z;
                    t.WorldCoordinates = (flags & WorldCoordinates) != 0;
                    t.Negative = (flags & NegativeImage) != 0;
                    t.PixelBlending = (flags & PixelBlending) != 0;
                    t.Antialiasing = (flags & Antialiasing) != 0;
                    break;
                case "TSIZ": t.Size = r.Vec12(); break;
                case "TCTR": t.Center = r.Vec12(); break;
                case "TIMG": t.Image = r.S0(); break;
                case "TWRP":
                    t.WidthTiling = Tiling(r.U2());
                    t.HeightTiling = Tiling(r.U2());
                    break;
                case "TFP0" when t.Projection is TextureProjection.Cylindrical or TextureProjection.Spherical && t.IsImage: t.WidthWrap = r.F4(); break;
                case "TFP1" when t.Projection == TextureProjection.Spherical && t.IsImage: t.HeightWrap = r.F4(); break;
                case "TAAS": t.AntialiasingStrength = r.F4(); break;
                case "TOPC": t.Opacity = r.F4(); break;
                case "TAMP": t.Amplitude = r.F4(); break;
                case "TVAL": t.Value = r.U2() / 256.0; break;
                case "TVMP":
                    t.UVMap = r.S0();
                    if (t.IsImage) t.Projection = TextureProjection.UV;
                    break;
            }
        }
        return t;
    }

    private static IffReader Reader(RawChunk c, string context) => new(c.Data, 0, c.Data.Length, $"{context} {c.Id}");

    /// <summary>LWOB's TWRP codes: 0 black (reset), 1 clamp (edge), 2 repeat, 3 mirror.</summary>
    private static TextureTiling Tiling(int code) => code switch
    {
        0 => TextureTiling.Reset, 1 => TextureTiling.Edge, 3 => TextureTiling.Mirror, _ => TextureTiling.Repeat,
    };

    private static ushort Code(TextureTiling t) => t switch
    {
        TextureTiling.Reset => 0, TextureTiling.Edge => 1, TextureTiling.Mirror => 3, _ => 2,
    };

    private static ushort Fixed(double v) => (ushort)Math.Clamp(Math.Round(v * 256), 0, ushort.MaxValue);

    /// <summary>
    /// The sub-chunks to write for a layer: as read, if it hasn't changed; otherwise those read with the settings it
    /// models rewritten in place (or added after them), the rest left as they were.
    /// </summary>
    public static List<RawChunk> Write(TextureLayer t)
    {
        if (t.Raw != null && t.RawSignature == t.Signature()) return t.Raw;
        var list = t.Raw == null ? new List<RawChunk>() : [.. t.Raw];

        void Set(string id, bool wanted, Action<IffWriter> data)
        {
            int at = list.FindIndex(c => c.Id == id);
            if (at >= 0 && !wanted) list.RemoveAt(at);
            else if (at >= 0) list[at] = new RawChunk(id, IffWriter.Build(data));
            else if (wanted) list.Add(new RawChunk(id, IffWriter.Build(data)));
        }

        string type = t.Procedural ?? (t.Projection == TextureProjection.UV ? "Planar Image Map" : Types.First(kv => kv.Value == t.Projection).Key);
        var header = new RawChunk(HeaderId(t.Channel), IffWriter.Build(w => w.S0(type)));
        if (list.Count > 0 && IsHeader(list[0].Id)) list[0] = header;
        else list.Insert(0, header);

        int flags = list.Find(c => c.Id == "TFLG") is { Data.Length: >= 2 } old ? old.Data[0] << 8 | old.Data[1] : 0;
        flags &= ~(AxisX | AxisY | AxisZ | WorldCoordinates | NegativeImage | PixelBlending | Antialiasing);
        flags |= t.Axis switch { Axis.X => AxisX, Axis.Y => AxisY, _ => AxisZ };
        if (t.WorldCoordinates) flags |= WorldCoordinates;
        if (t.Negative) flags |= NegativeImage;
        if (t.PixelBlending) flags |= PixelBlending;
        if (t.Antialiasing) flags |= Antialiasing;
        bool wraps = t.IsImage && t.Projection is TextureProjection.Cylindrical or TextureProjection.Spherical;

        Set("TFLG", true, w => w.U2((ushort)flags));
        Set("TSIZ", true, w => { w.F4((float)t.Size.X); w.F4((float)t.Size.Y); w.F4((float)t.Size.Z); });
        Set("TCTR", true, w => { w.F4((float)t.Center.X); w.F4((float)t.Center.Y); w.F4((float)t.Center.Z); });
        if (t.IsImage)
        {
            Set("TIMG", true, w => w.S0(t.Image.Length > 0 ? t.Image : "(none)"));
            Set("TWRP", true, w => { w.U2(Code(t.WidthTiling)); w.U2(Code(t.HeightTiling)); });
            Set("TFP0", wraps, w => w.F4((float)t.WidthWrap));
            Set("TFP1", wraps && t.Projection == TextureProjection.Spherical, w => w.F4((float)t.HeightWrap));
            Set("TVMP", t.Projection == TextureProjection.UV, w => w.S0(t.UVMap));
        }
        Set("TAAS", t.Antialiasing || list.Any(c => c.Id == "TAAS"), w => w.F4((float)t.AntialiasingStrength));
        Set("TOPC", t.Opacity != 1 || list.Any(c => c.Id == "TOPC"), w => w.F4((float)t.Opacity));
        Set("TAMP", t.Channel == TextureChannel.Bump || list.Any(c => c.Id == "TAMP"), w => w.F4((float)t.Amplitude));
        Set("TVAL", list.Any(c => c.Id == "TVAL"), w => w.U2(Fixed(t.Value)));
        return list;
    }
}
