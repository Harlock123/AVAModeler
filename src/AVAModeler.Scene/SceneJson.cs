using System.Text.Json;
using System.Text.Json.Serialization;
using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// The native scene format (".avascene"): JSON holding everything in a <see cref="Scene"/>. Object geometry is either
/// referenced by its file path (read back through a loader callback, like LWS objects) or embedded: layers, points,
/// polygons and surfaces. An object with no file path is always embedded. Numbers are written so they read back
/// exactly. Surfaces' raw LWOB chunks aren't kept.
/// </summary>
public static class SceneJson
{
    public const string Extension = ".avascene";
    private const string FormatName = "avascene";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write(Scene scene, bool embedMeshes = false) => JsonSerializer.Serialize(ToDto(scene, embedMeshes), Options);

    public static void WriteFile(Scene scene, string path, bool embedMeshes = false) => File.WriteAllText(path, Write(scene, embedMeshes));

    public static SceneLoadResult ReadFile(string path, Func<string, Document?>? loadObject = null) =>
        Read(File.ReadAllText(path), loadObject, Path.GetDirectoryName(Path.GetFullPath(path)));

    /// <summary>
    /// Reads a scene. Referenced objects are loaded through <paramref name="loadObject"/>, with relative paths taken
    /// from <paramref name="baseDirectory"/>; one that can't be loaded becomes a placeholder and a warning. Throws
    /// <see cref="InvalidDataException"/> if the text isn't an avascene file.
    /// </summary>
    public static SceneLoadResult Read(string json, Func<string, Document?>? loadObject = null, string? baseDirectory = null)
    {
        SceneDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SceneDto>(json, Options);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Not a readable {FormatName} file: {e.Message}", e);
        }
        if (dto == null || dto.Format != FormatName) throw new InvalidDataException($"Not an {FormatName} file.");
        var warnings = new List<string>();
        return new SceneLoadResult(FromDto(dto, loadObject, baseDirectory, warnings), warnings);
    }

    private static SceneDto ToDto(Scene scene, bool embed)
    {
        var docs = new List<Document>();
        var docDtos = new List<DocumentDto>();
        var items = scene.Items.ToList();
        var dto = new SceneDto
        {
            Settings = new SettingsDto
            {
                FramesPerSecond = scene.FramesPerSecond,
                FirstFrame = scene.FirstFrame,
                LastFrame = scene.LastFrame,
                FrameStep = scene.FrameStep,
                AmbientColor = Arr(scene.AmbientColor),
                AmbientIntensity = scene.AmbientIntensity,
                BackgroundColor = Arr(scene.BackgroundColor),
                GradientBackground = scene.GradientBackground,
                GradientTop = Arr(scene.GradientTop),
                GradientBottom = Arr(scene.GradientBottom),
                Fog = scene.Fog,
                FogMinDistance = scene.FogMinDistance,
                FogMaxDistance = scene.FogMaxDistance,
                FogColor = Arr(scene.FogColor),
            },
            CurrentCamera = scene.CurrentCamera is { } cam ? items.IndexOf(cam) : null,
            Documents = docDtos,
        };
        foreach (var item in items)
        {
            var d = new ItemDto
            {
                Type = item.Kind,
                Name = item.Name,
                Parent = item.Parent == null ? null : items.IndexOf(item.Parent),
                Pivot = Arr(item.Pivot),
                Visible = item.Visible,
                Channels = item.Channels.Select(Env).ToList(),
            };
            switch (item)
            {
                case ObjectItem o:
                    if (o.Document != null)
                    {
                        int di = docs.IndexOf(o.Document);
                        if (di < 0)
                        {
                            docs.Add(o.Document);
                            docDtos.Add(embed || o.SourcePath == null
                                ? Embed(o.Document)
                                : new DocumentDto { Path = o.SourcePath });
                            di = docs.Count - 1;
                        }
                        d.Document = di;
                    }
                    d.Layer = o.Layer;
                    d.Source = o.SourcePath;
                    d.CastShadows = o.CastShadows;
                    d.ReceiveShadows = o.ReceiveShadows;
                    d.BoneFalloffExponent = o.BoneFalloffExponent;
                    d.Morphs = o.Morphs.Select(m => new MorphDto { Name = m.Name, Target = MeshDto.Of(m.Target), Weight = Env(m.Weight) }).ToList();
                    d.Displacements = o.Displacements.Select(x => new DisplacementDto
                    {
                        Shape = x.Shape, Direction = x.Direction, Along = x.Along, Center = Arr(x.Center),
                        Wavelength = x.Wavelength, Speed = x.Speed, Phase = x.Phase, Amplitude = Env(x.Amplitude),
                    }).ToList();
                    break;
                case Bone b:
                    d.Owner = items.IndexOf(b.Owner);
                    d.RestPosition = Arr(b.RestPosition);
                    d.RestRotation = Arr(b.RestRotation);
                    d.RestLength = b.RestLength;
                    d.Strength = b.Strength;
                    d.Active = b.Active;
                    break;
                case LightItem l:
                    d.LightType = l.Type;
                    d.Color = Arr(l.Color);
                    d.Intensity = Env(l.Intensity);
                    d.Range = l.Range;
                    d.ConeAngle = l.ConeAngle;
                    d.EdgeAngle = l.EdgeAngle;
                    d.Shadows = l.Shadows;
                    break;
                case CameraItem c:
                    d.Zoom = Env(c.Zoom);
                    d.Width = c.Width;
                    d.Height = c.Height;
                    d.PixelAspect = c.PixelAspect;
                    d.ActionSafe = c.ActionSafe;
                    d.TitleSafe = c.TitleSafe;
                    break;
            }
            dto.Items.Add(d);
        }
        return dto;
    }

    private static Scene FromDto(SceneDto dto, Func<string, Document?>? load, string? baseDir, List<string> warnings)
    {
        var s = dto.Settings ?? new SettingsDto();
        var scene = new Scene
        {
            FramesPerSecond = s.FramesPerSecond,
            FirstFrame = s.FirstFrame,
            LastFrame = s.LastFrame,
            FrameStep = s.FrameStep,
            AmbientColor = Vec(s.AmbientColor),
            AmbientIntensity = s.AmbientIntensity,
            BackgroundColor = Vec(s.BackgroundColor),
            GradientBackground = s.GradientBackground,
            GradientTop = Vec(s.GradientTop),
            GradientBottom = Vec(s.GradientBottom),
            Fog = s.Fog,
            FogMinDistance = s.FogMinDistance,
            FogMaxDistance = s.FogMaxDistance,
            FogColor = Vec(s.FogColor),
        };
        var docs = dto.Documents.Select(d => LoadDocument(d, load, baseDir, warnings)).ToList();
        var items = new List<SceneItem?>();
        foreach (var d in dto.Items)
        {
            SceneItem? item = d.Type switch
            {
                "Object" => MakeObject(d, docs),
                "Null" => new NullItem(d.Name),
                "Light" => MakeLight(d),
                "Camera" => MakeCamera(d),
                "Bone" => d.Owner is int oi && oi >= 0 && oi < items.Count && items[oi] is ObjectItem owner ? MakeBone(d, owner) : null,
                _ => null,
            };
            if (item == null)
            {
                warnings.Add($"Item \"{d.Name}\" ({d.Type}) couldn't be read and is left out.");
                items.Add(null);
                continue;
            }
            item.Pivot = Vec(d.Pivot);
            item.Visible = d.Visible;
            for (int c = 0; c < Math.Min(9, d.Channels.Count); c++) SetEnv(item.Channels[c], d.Channels[c]);
            scene.Add(item);
            items.Add(item);
        }
        for (int i = 0; i < dto.Items.Count; i++)
        {
            if (items[i] is not { } item || dto.Items[i].Parent is not int p) continue;
            if (p < 0 || p >= items.Count || items[p] == null)
            {
                warnings.Add($"{item.Name}'s parent isn't in the scene.");
                continue;
            }
            try
            {
                item.Parent = items[p];
            }
            catch (InvalidOperationException e)
            {
                warnings.Add(e.Message);
            }
        }
        if (dto.CurrentCamera is int ci && ci >= 0 && ci < items.Count && items[ci] is CameraItem cam) scene.CurrentCamera = cam;
        return scene;
    }

    private static ObjectItem MakeObject(ItemDto d, List<Document?> docs)
    {
        var doc = d.Document is int i && i >= 0 && i < docs.Count ? docs[i] : null;
        var o = new ObjectItem(d.Name, doc, d.Layer)
        {
            SourcePath = d.Source,
            CastShadows = d.CastShadows ?? true,
            ReceiveShadows = d.ReceiveShadows ?? true,
            BoneFalloffExponent = d.BoneFalloffExponent ?? 4,
        };
        foreach (var m in d.Morphs ?? [])
        {
            var morph = new MorphTarget(m.Name, m.Target?.ToMesh() ?? new Mesh());
            SetEnv(morph.Weight, m.Weight);
            o.AddMorph(morph);
        }
        foreach (var x in d.Displacements ?? [])
        {
            var w = new WaveDisplacement
            {
                Shape = x.Shape, Direction = x.Direction, Along = x.Along, Center = Vec(x.Center),
                Wavelength = x.Wavelength, Speed = x.Speed, Phase = x.Phase,
            };
            SetEnv(w.Amplitude, x.Amplitude);
            o.AddDisplacement(w);
        }
        return o;
    }

    private static LightItem MakeLight(ItemDto d)
    {
        var l = new LightItem(d.Name, d.LightType ?? LightType.Distant)
        {
            Color = d.Color == null ? Vec3.One : Vec(d.Color),
            Range = d.Range ?? 0,
            ConeAngle = d.ConeAngle ?? 30,
            EdgeAngle = d.EdgeAngle ?? 5,
            Shadows = d.Shadows ?? true,
        };
        SetEnv(l.Intensity, d.Intensity);
        return l;
    }

    private static CameraItem MakeCamera(ItemDto d)
    {
        var c = new CameraItem(d.Name)
        {
            Width = d.Width ?? 640,
            Height = d.Height ?? 480,
            PixelAspect = d.PixelAspect ?? 1,
            ActionSafe = d.ActionSafe ?? 0.9,
            TitleSafe = d.TitleSafe ?? 0.8,
        };
        SetEnv(c.Zoom, d.Zoom);
        return c;
    }

    private static Bone MakeBone(ItemDto d, ObjectItem owner) => new(d.Name, owner)
    {
        RestPosition = Vec(d.RestPosition),
        RestRotation = Vec(d.RestRotation),
        RestLength = d.RestLength ?? 1,
        Strength = d.Strength ?? 1,
        Active = d.Active ?? true,
    };

    private static Document? LoadDocument(DocumentDto d, Func<string, Document?>? load, string? baseDir, List<string> warnings)
    {
        if (d.Layers != null)
        {
            var doc = new Document { Layers = d.Layers.Select(l => l.ToLayer()).ToList() };
            if (d.Surfaces is { Count: > 0 }) doc.Surfaces = d.Surfaces.Select(x => x.ToSurface()).ToList();
            doc.FilePath = d.Path;
            return doc;
        }
        if (d.Path == null) return null;
        string path = baseDir != null && !Path.IsPathRooted(d.Path) ? Path.Combine(baseDir, d.Path) : d.Path;
        if (load == null)
        {
            warnings.Add($"No object loader was given, so {d.Path} is a placeholder.");
            return null;
        }
        try
        {
            var doc = load(path);
            if (doc == null) warnings.Add($"Couldn't load object {d.Path}; a placeholder stands in for it.");
            return doc;
        }
        catch (Exception e)
        {
            warnings.Add($"Couldn't load object {d.Path} ({e.Message}); a placeholder stands in for it.");
            return null;
        }
    }

    private static DocumentDto Embed(Document doc) => new()
    {
        Path = doc.FilePath,
        Layers = doc.Layers.Select(l => new LayerDto
        {
            Name = l.Name, State = l.State, Pivot = Arr(l.Pivot), Mesh = MeshDto.Of(l.Mesh),
        }).ToList(),
        Surfaces = doc.Surfaces.Select(SurfaceDto.Of).ToList(),
    };

    private static double[] Arr(Vec3 v) => [v.X, v.Y, v.Z];
    private static Vec3 Vec(double[]? a) => a is { Length: >= 3 } ? new Vec3(a[0], a[1], a[2]) : Vec3.Zero;

    private static EnvelopeDto Env(Envelope e) => new()
    {
        Keys = e.Keys.Select(k => new KeyDto
        {
            Frame = k.Frame, Value = k.Value, Tension = k.Tension, Continuity = k.Continuity, Bias = k.Bias, Linear = k.Linear,
        }).ToList(),
        Pre = e.PreBehavior,
        Post = e.PostBehavior,
    };

    private static void SetEnv(Envelope target, EnvelopeDto? d)
    {
        if (d == null) return;
        var e = new Envelope { PreBehavior = d.Pre, PostBehavior = d.Post };
        foreach (var k in d.Keys) e.AddKey(new EnvelopeKey(k.Frame, k.Value, k.Tension, k.Continuity, k.Bias, k.Linear));
        target.CopyFrom(e);
    }

    private sealed class SceneDto
    {
        public string Format { get; set; } = FormatName;
        public int Version { get; set; } = 1;
        public SettingsDto? Settings { get; set; }
        public int? CurrentCamera { get; set; }
        public List<DocumentDto> Documents { get; set; } = [];
        public List<ItemDto> Items { get; set; } = [];
    }

    private sealed class SettingsDto
    {
        public double FramesPerSecond { get; set; } = 30;
        public int FirstFrame { get; set; } = 1;
        public int LastFrame { get; set; } = 60;
        public int FrameStep { get; set; } = 1;
        public double[]? AmbientColor { get; set; }
        public double AmbientIntensity { get; set; }
        public double[]? BackgroundColor { get; set; }
        public bool GradientBackground { get; set; }
        public double[]? GradientTop { get; set; }
        public double[]? GradientBottom { get; set; }
        public FogType Fog { get; set; }
        public double FogMinDistance { get; set; }
        public double FogMaxDistance { get; set; }
        public double[]? FogColor { get; set; }
    }

    private sealed class DocumentDto
    {
        public string? Path { get; set; }
        public List<LayerDto>? Layers { get; set; }
        public List<SurfaceDto>? Surfaces { get; set; }
    }

    private sealed class LayerDto
    {
        public string Name { get; set; } = "";
        public LayerState State { get; set; }
        public double[]? Pivot { get; set; }
        public MeshDto? Mesh { get; set; }

        public Layer ToLayer() => new() { Name = Name, State = State, Pivot = Vec(Pivot), Mesh = Mesh?.ToMesh() ?? new Mesh() };
    }

    /// <summary>Points as a flat x, y, z list, and polygons.</summary>
    private sealed class MeshDto
    {
        public double[] Points { get; set; } = [];
        public List<PolygonDto> Polygons { get; set; } = [];

        public static MeshDto Of(Mesh m) => new()
        {
            Points = m.Points.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(),
            Polygons = m.Polygons.Select(PolygonDto.Of).ToList(),
        };

        public Mesh ToMesh()
        {
            var m = new Mesh();
            for (int i = 0; i + 2 < Points.Length; i += 3) m.Points.Add(new Vec3(Points[i], Points[i + 1], Points[i + 2]));
            m.Polygons.AddRange(Polygons.Select(p => p.ToPolygon()));
            m.Touch();
            return m;
        }
    }

    private sealed class PolygonDto
    {
        public int[] Points { get; set; } = [];
        public int Surface { get; set; }
        public List<PolygonDto>? Details { get; set; }

        public static PolygonDto Of(Polygon p) => new() { Points = [.. p.Points], Surface = p.Surface, Details = p.Details?.Select(Of).ToList() };

        public Polygon ToPolygon() => new(Points, Surface) { Details = Details?.Select(d => d.ToPolygon()).ToList() };
    }

    private sealed class SurfaceDto
    {
        public string Name { get; set; } = "";
        public int[] Color { get; set; } = [200, 200, 200];
        public double Diffuse { get; set; }
        public double Specular { get; set; }
        public double Glossiness { get; set; }
        public double Luminosity { get; set; }
        public double Reflection { get; set; }
        public double Transparency { get; set; }
        public double SmoothingAngle { get; set; }
        public bool Smoothing { get; set; }
        public bool DoubleSided { get; set; }

        public static SurfaceDto Of(Surface s) => new()
        {
            Name = s.Name, Color = [s.Color.R, s.Color.G, s.Color.B], Diffuse = s.Diffuse, Specular = s.Specular,
            Glossiness = s.Glossiness, Luminosity = s.Luminosity, Reflection = s.Reflection, Transparency = s.Transparency,
            SmoothingAngle = s.SmoothingAngle, Smoothing = s.Smoothing, DoubleSided = s.DoubleSided,
        };

        private static byte Channel(int v) => (byte)Math.Clamp(v, 0, 255);

        public Surface ToSurface() => new(Name)
        {
            Color = Color.Length >= 3 ? new Rgb(Channel(Color[0]), Channel(Color[1]), Channel(Color[2])) : Rgb.Grey,
            Diffuse = Diffuse, Specular = Specular, Glossiness = Glossiness, Luminosity = Luminosity, Reflection = Reflection,
            Transparency = Transparency, SmoothingAngle = SmoothingAngle, Smoothing = Smoothing, DoubleSided = DoubleSided,
        };
    }

    private sealed class EnvelopeDto
    {
        public List<KeyDto> Keys { get; set; } = [];
        public EndBehavior Pre { get; set; } = EndBehavior.Constant;
        public EndBehavior Post { get; set; } = EndBehavior.Constant;
    }

    private sealed class KeyDto
    {
        public double Frame { get; set; }
        public double Value { get; set; }
        public double Tension { get; set; }
        public double Continuity { get; set; }
        public double Bias { get; set; }
        public bool Linear { get; set; }
    }

    private sealed class MorphDto
    {
        public string Name { get; set; } = "";
        public MeshDto? Target { get; set; }
        public EnvelopeDto? Weight { get; set; }
    }

    private sealed class DisplacementDto
    {
        public WaveShape Shape { get; set; }
        public Axis Direction { get; set; }
        public Axis Along { get; set; }
        public double[]? Center { get; set; }
        public double Wavelength { get; set; }
        public double Speed { get; set; }
        public double Phase { get; set; }
        public EnvelopeDto? Amplitude { get; set; }
    }

    private sealed class ItemDto
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public int? Parent { get; set; }
        public double[]? Pivot { get; set; }
        public bool Visible { get; set; } = true;
        public List<EnvelopeDto> Channels { get; set; } = [];

        public int? Document { get; set; }
        public int? Layer { get; set; }
        public string? Source { get; set; }
        public bool? CastShadows { get; set; }
        public bool? ReceiveShadows { get; set; }
        public double? BoneFalloffExponent { get; set; }
        public List<MorphDto>? Morphs { get; set; }
        public List<DisplacementDto>? Displacements { get; set; }

        public int? Owner { get; set; }
        public double[]? RestPosition { get; set; }
        public double[]? RestRotation { get; set; }
        public double? RestLength { get; set; }
        public double? Strength { get; set; }
        public bool? Active { get; set; }

        public LightType? LightType { get; set; }
        public double[]? Color { get; set; }
        public EnvelopeDto? Intensity { get; set; }
        public double? Range { get; set; }
        public double? ConeAngle { get; set; }
        public double? EdgeAngle { get; set; }
        public bool? Shadows { get; set; }

        public EnvelopeDto? Zoom { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? PixelAspect { get; set; }
        public double? ActionSafe { get; set; }
        public double? TitleSafe { get; set; }
    }
}
