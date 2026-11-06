using System.Text.Json;
using System.Text.Json.Serialization;
using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// The native scene format (".avascene"): JSON holding everything in a <see cref="Scene"/>. Object geometry is either
/// referenced by its file path (read back through a loader callback, like LWS objects) or embedded: layers, points,
/// polygons (with their UV maps) and surfaces (with their texture layers). An object with no file path is always
/// embedded; its image paths are as the object had them, found from the folder of the file it came from. Numbers are
/// written so they read back exactly. Surfaces' raw LWOB chunks aren't kept.
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

    /// <summary>
    /// Writes a scene. Object files in <paramref name="baseDirectory"/> or below it are written relative to it, so a
    /// scene and its objects can be moved or shared together; others keep their full paths.
    /// </summary>
    public static string Write(Scene scene, bool embedMeshes = false, string? baseDirectory = null) =>
        JsonSerializer.Serialize(ToDto(scene, embedMeshes, baseDirectory), Options);

    public static void WriteFile(Scene scene, string path, bool embedMeshes = false) =>
        File.WriteAllText(path, Write(scene, embedMeshes, Path.GetDirectoryName(Path.GetFullPath(path))));

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

    private static SceneDto ToDto(Scene scene, bool embed, string? baseDir)
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
                Occlusion = scene.Occlusion == new AmbientOcclusion() ? null : scene.Occlusion,
                Environment = scene.Environment == new EnvironmentSettings() ? null : scene.Environment with { Image = EnvironmentPath(scene.Environment.Image, baseDir, Relative) },
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
                Locked = item.Locked ? true : null,
                Channels = item.Channels.Select(Env).ToList(),
            };
            if (item.Path is { } path)
            {
                d.Path = new PathDto
                {
                    Points = path.Points.Select(p => items.IndexOf(p)).Where(i => i >= 0).ToArray(), Closed = path.Closed,
                    AlignToPath = path.AlignToPath, LookAhead = path.LookAhead, Banking = path.Banking,
                };
                d.PathPosition = Env(item.PathPosition);
            }
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
                                ? Embed(o.Document, baseDir)
                                : new DocumentDto { Path = Relative(o.SourcePath, baseDir) });
                            di = docs.Count - 1;
                        }
                        d.Document = di;
                    }
                    d.Layer = o.Layer;
                    d.Source = Relative(o.SourcePath, baseDir);
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
                    if (b.IKGoal != null && items.IndexOf(b.IKGoal) is var gi and >= 0)
                    {
                        d.IkGoal = gi;
                        d.FullTimeIk = b.FullTimeIK;
                    }
                    d.IkAnchor = b.IKAnchor ? true : null;
                    d.IkLocked = b.IKLocked == IKChannels.None ? null : b.IKLocked;
                    d.IkLimits = b.IKLimits.Any ? b.IKLimits : null;
                    break;
                case LightItem l:
                    d.LightType = l.Type;
                    d.Color = Arr(l.Color);
                    d.Intensity = Env(l.Intensity);
                    d.Range = l.Range;
                    d.ConeAngle = l.ConeAngle;
                    d.EdgeAngle = l.EdgeAngle;
                    d.Shadows = l.Shadows;
                    d.AreaSize = l.AreaWidth == 1 && l.AreaHeight == 1 ? null : [l.AreaWidth, l.AreaHeight];
                    d.Radius = l.Radius == 0 ? null : l.Radius;
                    d.ShadowSamples = l.ShadowSamples == 16 ? null : l.ShadowSamples;
                    break;
                case CameraItem c:
                    d.Zoom = Env(c.Zoom);
                    d.Width = c.Width;
                    d.Height = c.Height;
                    d.PixelAspect = c.PixelAspect;
                    d.ActionSafe = c.ActionSafe;
                    d.TitleSafe = c.TitleSafe;
                    d.DepthOfField = c.DepthOfField == new DepthOfField() ? null : c.DepthOfField;
                    d.MotionBlur = c.MotionBlur == new MotionBlur() ? null : c.MotionBlur;
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
            Occlusion = s.Occlusion ?? new AmbientOcclusion(),
            Environment = s.Environment is { } env ? env with { Image = EnvironmentPath(env.Image ?? "", baseDir, Absolute) } : new EnvironmentSettings(),
        };
        var docs = dto.Documents.Select(d => LoadDocument(d, load, baseDir, warnings)).ToList();
        var items = new List<SceneItem?>();
        foreach (var d in dto.Items)
        {
            SceneItem? item = d.Type switch
            {
                "Object" => MakeObject(d, docs, baseDir),
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
            item.Locked = d.Locked == true;
            for (int c = 0; c < Math.Min(9, d.Channels.Count); c++) SetEnv(item.Channels[c], d.Channels[c]);
            SetEnv(item.PathPosition, d.PathPosition);
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
        for (int i = 0; i < dto.Items.Count; i++)
        {
            if (items[i] is not { } follower || dto.Items[i].Path is not { } pd) continue;
            var points = pd.Points.Where(p => p >= 0 && p < items.Count && items[p] != null).Select(p => items[p]!).ToList();
            var path = new FollowPath
            {
                Points = points, Closed = pd.Closed, AlignToPath = pd.AlignToPath,
                LookAhead = double.IsFinite(pd.LookAhead) ? Math.Max(0, pd.LookAhead) : 0, Banking = double.IsFinite(pd.Banking) ? pd.Banking : 0,
            };
            if (points.Count < pd.Points.Length) warnings.Add($"Some of {follower.Name}'s path points aren't in the scene.");
            if (FollowPath.Problem(follower, points) is { } problem) warnings.Add(problem);
            else if (points.Count < path.Fewest) warnings.Add($"{follower.Name}'s path has too few points left, so it no longer follows it.");
            else follower.Path = path;
        }
        for (int i = 0; i < dto.Items.Count; i++)
        {
            if (items[i] is not Bone bone || dto.Items[i].IkGoal is not int g) continue;
            if (g < 0 || g >= items.Count || items[g] is not { } goal)
            {
                warnings.Add($"{bone.Name}'s IK goal isn't in the scene.");
                continue;
            }
            if (InverseKinematics.GoalProblem(bone, goal) is { } problem) warnings.Add(problem);
            else bone.IKGoal = goal;
        }
        if (dto.CurrentCamera is int ci && ci >= 0 && ci < items.Count && items[ci] is CameraItem cam) scene.CurrentCamera = cam;
        return scene;
    }

    private static ObjectItem MakeObject(ItemDto d, List<Document?> docs, string? baseDir)
    {
        var doc = d.Document is int i && i >= 0 && i < docs.Count ? docs[i] : null;
        var o = new ObjectItem(d.Name, doc, d.Layer)
        {
            SourcePath = Absolute(d.Source, baseDir),
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
            AreaWidth = d.AreaSize is { Length: >= 2 } a ? a[0] : 1,
            AreaHeight = d.AreaSize is { Length: >= 2 } b ? b[1] : 1,
            Radius = d.Radius ?? 0,
            ShadowSamples = d.ShadowSamples ?? 16,
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
            DepthOfField = d.DepthOfField ?? new DepthOfField(),
            MotionBlur = d.MotionBlur ?? new MotionBlur(),
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
        FullTimeIK = d.FullTimeIk ?? true,
        IKAnchor = d.IkAnchor ?? false,
        IKLocked = d.IkLocked ?? IKChannels.None,
        IKLimits = Limits(d.IkLimits),
    };

    /// <summary>Limits read from a file, any that make no sense (least above most) left off.</summary>
    private static IKLimits Limits(IKLimits? l)
    {
        if (l == null) return new IKLimits();
        static IKLimit? Ok(IKLimit? x) => x is { } v && double.IsFinite(v.Min) && double.IsFinite(v.Max) && v.Min <= v.Max ? v : null;
        return new IKLimits(Ok(l.Heading), Ok(l.Pitch), Ok(l.Bank));
    }

    private static Document? LoadDocument(DocumentDto d, Func<string, Document?>? load, string? baseDir, List<string> warnings)
    {
        if (d.Layers != null)
        {
            var doc = new Document { Layers = d.Layers.Select(l => l.ToLayer()).ToList() };
            if (d.Surfaces is { Count: > 0 }) doc.Surfaces = d.Surfaces.Select(x => x.ToSurface()).ToList();
            doc.FilePath = Absolute(d.Path, baseDir);
            return doc;
        }
        if (d.Path == null) return null;
        string path = Absolute(d.Path, baseDir)!;
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

    /// <summary>A path relative to the scene's folder when the file is in it or below it; otherwise as it is.</summary>
    private static string? Relative(string? path, string? baseDir)
    {
        if (path == null || baseDir == null || !Path.IsPathRooted(path)) return path;
        string rel = Path.GetRelativePath(baseDir, path);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : rel.Replace('\\', '/');
    }

    /// <summary>An environment image's path as written or read: a file's made relative or absolute, a built-in name as it is.</summary>
    private static string EnvironmentPath(string image, string? baseDir, Func<string?, string?, string?> convert) =>
        image.Length == 0 || image.StartsWith(EnvironmentMap.BuiltinPrefix, StringComparison.OrdinalIgnoreCase) ? image : convert(image, baseDir) ?? image;

    /// <summary>A path read from a scene, taken from the scene's folder if it's relative.</summary>
    private static string? Absolute(string? path, string? baseDir) =>
        path != null && baseDir != null && !Path.IsPathRooted(path) ? Path.Combine(baseDir, path) : path;

    private static DocumentDto Embed(Document doc, string? baseDir) => new()
    {
        Path = Relative(doc.FilePath, baseDir),
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
            Frame = k.Frame, Value = k.Value, Tension = k.Tension, Continuity = k.Continuity, Bias = k.Bias, Linear = k.Kind == SpanKind.Linear,
            // Written only when not the defaults, so files without stepped or Bezier keys read as before.
            Span = k.Kind is SpanKind.Stepped or SpanKind.Bezier ? k.Kind : null,
            InSlope = k.InSlope != 0 ? k.InSlope : null,
            OutSlope = k.OutSlope != 0 ? k.OutSlope : null,
        }).ToList(),
        Pre = e.PreBehavior,
        Post = e.PostBehavior,
    };

    private static void SetEnv(Envelope target, EnvelopeDto? d)
    {
        if (d == null) return;
        var e = new Envelope { PreBehavior = d.Pre, PostBehavior = d.Post };
        foreach (var k in d.Keys)
        {
            var key = new EnvelopeKey(k.Frame, k.Value, k.Tension, k.Continuity, k.Bias, k.Linear, InSlope: k.InSlope ?? 0, OutSlope: k.OutSlope ?? 0);
            e.AddKey(k.Span is { } span ? key.WithSpan(span) : key);
        }
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
        public AmbientOcclusion? Occlusion { get; set; }
        public EnvironmentSettings? Environment { get; set; }
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

    /// <summary>Points as a flat x, y, z list, polygons, and UV maps.</summary>
    private sealed class MeshDto
    {
        public double[] Points { get; set; } = [];
        public List<PolygonDto> Polygons { get; set; } = [];
        public List<UVMapDto>? UVMaps { get; set; }

        public static MeshDto Of(Mesh m) => new()
        {
            Points = m.Points.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(),
            Polygons = m.Polygons.Select(PolygonDto.Of).ToList(),
            UVMaps = m.UVMaps.Count == 0 ? null : m.UVMaps.Select(u => new UVMapDto { Name = u.Name, Points = Flat(u.Points) }).ToList(),
        };

        public Mesh ToMesh()
        {
            var m = new Mesh();
            for (int i = 0; i + 2 < Points.Length; i += 3) m.Points.Add(new Vec3(Points[i], Points[i + 1], Points[i + 2]));
            m.Polygons.AddRange(Polygons.Select(p => p.ToPolygon()));
            foreach (var u in UVMaps ?? [])
            {
                var map = m.AddUVMap(u.Name);
                foreach (var (i, uv) in Unflat(u.Points))
                    if (i >= 0 && i < m.Points.Count) map.Points[i] = uv;
            }
            m.Touch();
            return m;
        }
    }

    /// <summary>A UV map: its points as a flat index, u, v list.</summary>
    private sealed class UVMapDto
    {
        public string Name { get; set; } = "";
        public double[] Points { get; set; } = [];
    }

    /// <summary>UVs by point as a flat index, u, v list.</summary>
    private static double[] Flat(Dictionary<int, Vec2> uvs) => uvs.SelectMany(kv => new[] { kv.Key, kv.Value.X, kv.Value.Y }).ToArray();

    private static IEnumerable<(int, Vec2)> Unflat(double[] flat)
    {
        for (int i = 0; i + 2 < flat.Length; i += 3) yield return ((int)flat[i], new Vec2(flat[i + 1], flat[i + 2]));
    }

    private sealed class PolygonDto
    {
        public int[] Points { get; set; } = [];
        public int Surface { get; set; }
        public List<PolygonDto>? Details { get; set; }
        /// <summary>The corners' own UVs (seams), by map name, each a flat point, u, v list.</summary>
        public Dictionary<string, double[]>? CornerUVs { get; set; }

        public static PolygonDto Of(Polygon p) => new()
        {
            Points = [.. p.Points], Surface = p.Surface, Details = p.Details?.Select(Of).ToList(),
            CornerUVs = p.CornerUVs?.ToDictionary(kv => kv.Key, kv => Flat(kv.Value)),
        };

        public Polygon ToPolygon() => new(Points, Surface)
        {
            Details = Details?.Select(d => d.ToPolygon()).ToList(),
            CornerUVs = CornerUVs?.ToDictionary(kv => kv.Key, kv => Unflat(kv.Value).ToDictionary(x => x.Item1, x => x.Item2)),
        };
    }

    private sealed class TextureDto
    {
        public TextureChannel Channel { get; set; }
        public TextureProjection Projection { get; set; }
        public string Image { get; set; } = "";
        public string? UVMap { get; set; }
        public Axis Axis { get; set; } = Axis.Z;
        public double[]? Center { get; set; }
        public double[]? Size { get; set; }
        public TextureTiling WidthTiling { get; set; } = TextureTiling.Repeat;
        public TextureTiling HeightTiling { get; set; } = TextureTiling.Repeat;
        public double WidthWrap { get; set; } = 1;
        public double HeightWrap { get; set; } = 1;
        public double Opacity { get; set; } = 1;
        public TextureBlend Blend { get; set; }
        public double Amplitude { get; set; } = 1;
        public bool PixelBlending { get; set; } = true;
        public bool Negative { get; set; }
        public bool WorldCoordinates { get; set; }
        public string? Procedural { get; set; }
        public double Value { get; set; } = 1;
        public int[]? TextureColor { get; set; }
        public double[]? Parameters { get; set; }
        public double[]? Falloff { get; set; }

        public static TextureDto Of(TextureLayer t) => new()
        {
            Channel = t.Channel, Projection = t.Projection, Image = t.Image, UVMap = t.UVMap.Length > 0 ? t.UVMap : null, Axis = t.Axis,
            Center = Arr(t.Center), Size = Arr(t.Size), WidthTiling = t.WidthTiling, HeightTiling = t.HeightTiling,
            WidthWrap = t.WidthWrap, HeightWrap = t.HeightWrap, Opacity = t.Opacity, Blend = t.Blend, Amplitude = t.Amplitude,
            PixelBlending = t.PixelBlending, Negative = t.Negative, WorldCoordinates = t.WorldCoordinates, Procedural = t.Procedural,
            Value = t.Value,
            TextureColor = t.Procedural == null ? null : [t.TextureColor.R, t.TextureColor.G, t.TextureColor.B],
            Parameters = t.Procedural == null ? null : (double[])t.Parameters.Clone(),
            Falloff = t.Falloff == Vec3.Zero ? null : Arr(t.Falloff),
        };

        public TextureLayer ToLayer() => new()
        {
            Channel = Channel, Projection = Projection, Image = Image, UVMap = UVMap ?? "", Axis = Axis, Center = Vec(Center),
            Size = Size == null ? Vec3.One : Vec(Size), WidthTiling = WidthTiling, HeightTiling = HeightTiling, WidthWrap = WidthWrap,
            HeightWrap = HeightWrap, Opacity = Opacity, Blend = Blend, Amplitude = Amplitude, PixelBlending = PixelBlending,
            Negative = Negative, WorldCoordinates = WorldCoordinates, Procedural = Procedural, Value = Value,
            TextureColor = TextureColor is { Length: >= 3 } c ? new Rgb((byte)Math.Clamp(c[0], 0, 255), (byte)Math.Clamp(c[1], 0, 255), (byte)Math.Clamp(c[2], 0, 255)) : new Rgb(0, 0, 0),
            Parameters = Parameters is { } p ? [.. p.Concat(new double[Procedurals.Slots.Length]).Take(Procedurals.Slots.Length)] : new double[Procedurals.Slots.Length],
            Falloff = Falloff == null ? Vec3.Zero : Vec(Falloff),
        };
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
        public List<TextureDto>? Textures { get; set; }

        public static SurfaceDto Of(Surface s) => new()
        {
            Name = s.Name, Color = [s.Color.R, s.Color.G, s.Color.B], Diffuse = s.Diffuse, Specular = s.Specular,
            Glossiness = s.Glossiness, Luminosity = s.Luminosity, Reflection = s.Reflection, Transparency = s.Transparency,
            SmoothingAngle = s.SmoothingAngle, Smoothing = s.Smoothing, DoubleSided = s.DoubleSided,
            Textures = s.Textures.Count == 0 ? null : s.Textures.Select(TextureDto.Of).ToList(),
        };

        private static byte Channel(int v) => (byte)Math.Clamp(v, 0, 255);

        public Surface ToSurface() => new(Name)
        {
            Color = Color.Length >= 3 ? new Rgb(Channel(Color[0]), Channel(Color[1]), Channel(Color[2])) : Rgb.Grey,
            Diffuse = Diffuse, Specular = Specular, Glossiness = Glossiness, Luminosity = Luminosity, Reflection = Reflection,
            Transparency = Transparency, SmoothingAngle = SmoothingAngle, Smoothing = Smoothing, DoubleSided = DoubleSided,
            Textures = Textures?.Select(t => t.ToLayer()).ToList() ?? [],
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
        public SpanKind? Span { get; set; }
        public double? InSlope { get; set; }
        public double? OutSlope { get; set; }
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

    /// <summary>A path an item follows: its points by item index, and its settings.</summary>
    private sealed class PathDto
    {
        public int[] Points { get; set; } = [];
        public bool Closed { get; set; }
        public bool AlignToPath { get; set; } = true;
        public double LookAhead { get; set; }
        public double Banking { get; set; }
    }

    private sealed class ItemDto
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public int? Parent { get; set; }
        public double[]? Pivot { get; set; }
        public bool Visible { get; set; } = true;
        /// <summary>Written only when locked, so files of unlocked scenes are as they were.</summary>
        public bool? Locked { get; set; }
        public List<EnvelopeDto> Channels { get; set; } = [];
        public PathDto? Path { get; set; }
        public EnvelopeDto? PathPosition { get; set; }

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
        public int? IkGoal { get; set; }
        public bool? FullTimeIk { get; set; }
        public bool? IkAnchor { get; set; }
        public IKChannels? IkLocked { get; set; }
        public IKLimits? IkLimits { get; set; }

        public LightType? LightType { get; set; }
        public double[]? Color { get; set; }
        public EnvelopeDto? Intensity { get; set; }
        public double? Range { get; set; }
        public double? ConeAngle { get; set; }
        public double? EdgeAngle { get; set; }
        public bool? Shadows { get; set; }
        public double[]? AreaSize { get; set; }
        public double? Radius { get; set; }
        public int? ShadowSamples { get; set; }

        public EnvelopeDto? Zoom { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? PixelAspect { get; set; }
        public double? ActionSafe { get; set; }
        public double? TitleSafe { get; set; }
        public DepthOfField? DepthOfField { get; set; }
        public MotionBlur? MotionBlur { get; set; }
    }
}
