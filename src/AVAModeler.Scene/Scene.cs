using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>How fog thickens with distance from the camera.</summary>
public enum FogType
{
    Off,
    /// <summary>None up to the minimum distance, rising evenly to full at the maximum.</summary>
    Linear,
}

/// <summary>
/// A Layout scene: objects, nulls, lights, cameras and bones, plus the scene-wide settings (frame range and rate,
/// ambient light, background, fog, ambient occlusion, the environment, and the camera to render through). Colours are
/// RGB from 0 to 1.
/// <para>Envelope edits and adding, removing or reparenting items bump <see cref="Version"/> and raise
/// <see cref="Changed"/> on their own; after changing a field directly, call <see cref="Notify"/>.</para>
/// </summary>
public sealed partial class Scene
{
    private readonly List<SceneItem> _items = [];
    private CameraItem? _camera;
    private int _quiet;
    private bool _missed;

    public double FramesPerSecond = 30;
    public int FirstFrame = 1, LastFrame = 60, FrameStep = 1;
    public Vec3 AmbientColor = Vec3.One;
    public double AmbientIntensity = 0.25;
    /// <summary>The backdrop colour, used unless <see cref="GradientBackground"/> is set.</summary>
    public Vec3 BackgroundColor = Vec3.Zero;
    /// <summary>When set, the backdrop blends from <see cref="GradientBottom"/> straight down to <see cref="GradientTop"/> straight up.</summary>
    public bool GradientBackground;
    public Vec3 GradientTop = new(0.2, 0.4, 0.8), GradientBottom = new(0.3, 0.25, 0.2);
    public FogType Fog;
    public double FogMinDistance, FogMaxDistance = 100;
    public Vec3 FogColor = Vec3.Zero;
    /// <summary>Ambient occlusion (off by default).</summary>
    public AmbientOcclusion Occlusion = new();
    /// <summary>The environment picture round the scene (none by default).</summary>
    public EnvironmentSettings Environment = new();
    /// <summary>The background and foreground images laid behind and over the render (none by default).</summary>
    public CompositingSettings Compositing = new();

    private IReadOnlyList<SceneImage> _images = [];

    /// <summary>
    /// The scene's image list (Layout's Images panel): stills and numbered sequences that compositing and image textures
    /// use. Setting it copies the list, so the one set can't change underneath the scene (or its snapshots).
    /// </summary>
    public IReadOnlyList<SceneImage> Images
    {
        get => _images;
        set => _images = [.. value];
    }

    private IReadOnlyDictionary<string, string> _extras = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Data a program keeps with the scene that the engine doesn't use itself (camera bookmarks, action clips…): JSON
    /// text by name, kept in .avascene files, copied with the scene and put back by undo. Setting it copies it, so the
    /// one set can't change underneath the scene (or its snapshots). Name keys after the program, e.g.
    /// "avalightwave.bookmarks".
    /// </summary>
    public IReadOnlyDictionary<string, string> Extras
    {
        get => _extras;
        set => _extras = new Dictionary<string, string>(value, StringComparer.Ordinal);
    }

    /// <summary>One of <see cref="Extras"/>, or null.</summary>
    public string? Extra(string key) => _extras.TryGetValue(key, out var v) ? v : null;

    /// <summary>Sets (or, with null, removes) one of <see cref="Extras"/>, as JSON text, and notifies.</summary>
    /// <exception cref="ArgumentException">The text isn't JSON.</exception>
    public void SetExtra(string key, string? json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (json != null)
        {
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(json);
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new ArgumentException($"The scene's extra \"{key}\" must be JSON: {e.Message}", nameof(json), e);
            }
        }
        var d = new Dictionary<string, string>(_extras, StringComparer.Ordinal);
        if (json == null) d.Remove(key);
        else d[key] = json;
        _extras = d;
        Notify();
    }

    public IReadOnlyList<SceneItem> Items => _items;
    public IEnumerable<ObjectItem> Objects => _items.OfType<ObjectItem>();
    public IEnumerable<NullItem> Nulls => _items.OfType<NullItem>();
    public IEnumerable<LightItem> Lights => _items.OfType<LightItem>();
    public IEnumerable<CameraItem> Cameras => _items.OfType<CameraItem>();
    public IEnumerable<Bone> Bones => _items.OfType<Bone>();

    private MotionSolver? _motion;

    /// <summary>What moves items beyond their keys (path following, full-time IK), worked out on demand and remembered until the scene changes.</summary>
    public MotionSolver Motion => _motion ??= new MotionSolver(this);

    /// <summary>Goes up on every change, so views know to redraw.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after any change.</summary>
    public event Action? Changed;

    public void Notify()
    {
        Version++;
        if (_quiet > 0) _missed = true;
        else Changed?.Invoke();
    }

    /// <summary>
    /// Makes many changes count as one: <see cref="Changed"/> is held back while <paramref name="change"/> runs, then
    /// raised once if anything changed (<see cref="Version"/> still goes up with each).
    /// </summary>
    public void Batch(Action change)
    {
        _quiet++;
        try
        {
            change();
        }
        finally
        {
            if (--_quiet == 0 && _missed)
            {
                _missed = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>The scene as it is now, to put back with <see cref="SceneSnapshot.Restore"/>.</summary>
    public SceneSnapshot Snapshot() => new(this);

    // For SceneSnapshot: the item list and current camera as they are, and putting them back.
    internal List<SceneItem> ItemList => _items;
    internal CameraItem? CameraField { get => _camera; set => _camera = value; }

    /// <summary>The camera to look and render through: the one set, or else the first camera.</summary>
    public CameraItem? CurrentCamera
    {
        get => _camera != null && _camera.Scene == this ? _camera : Cameras.FirstOrDefault();
        set
        {
            if (value != null && value.Scene != this) throw new InvalidOperationException("The current camera must be in the scene.");
            _camera = value;
            Notify();
        }
    }

    /// <summary>A scene as Layout starts: a camera five units back and a distant light from the upper left.</summary>
    public static Scene CreateDefault()
    {
        var s = new Scene();
        var cam = s.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -5));
        var light = s.AddLight("Light", LightType.Distant);
        light.SetPosition(0, new Vec3(-2, 2, -2));
        light.SetRotation(0, new Vec3(45, 35, 0));
        return s;
    }

    /// <summary>Adds an item. A bone's object must already be in the scene.</summary>
    public T Add<T>(T item) where T : SceneItem
    {
        if (item.Scene != null) throw new InvalidOperationException($"{item.Name} is already in a scene.");
        if (item is Bone b)
        {
            if (b.Owner.Scene != this) throw new InvalidOperationException("A bone's object must be in the scene first.");
            b.Owner.AttachBone(b);
        }
        item.Scene = this;
        _items.Add(item);
        Notify();
        return item;
    }

    public ObjectItem AddObject(string name, Document? document = null, int? layer = null) => Add(new ObjectItem(name, document, layer));
    public NullItem AddNull(string name) => Add(new NullItem(name));
    public LightItem AddLight(string name, LightType type = LightType.Distant) => Add(new LightItem(name, type));
    public CameraItem AddCamera(string name) => Add(new CameraItem(name));

    /// <summary>Adds a bone to an object, at rest where given, with its channels set to the rest pose.</summary>
    public Bone AddBone(ObjectItem owner, string name, Vec3 restPosition = default, Vec3 restRotation = default, double restLength = 1)
    {
        var b = Add(new Bone(name, owner) { RestPosition = restPosition, RestRotation = restRotation, RestLength = restLength });
        b.ResetPose();
        return b;
    }

    /// <summary>
    /// Removes an item. Its children move up to its parent (a removed object takes its bones with it, and anything
    /// else parented to it or them is unparented). IK goals on it are cleared, paths leave it out, and motion modifiers
    /// that take their motion from it (a lag behind it, a cycle it drives) are dropped.
    /// </summary>
    public void Remove(SceneItem item)
    {
        if (item.Scene != this) return;
        var gone = new List<SceneItem> { item };
        if (item is ObjectItem o) gone.AddRange(o.Bones);
        foreach (var x in _items)
        {
            if (gone.Contains(x) || x.Parent == null || !gone.Contains(x.Parent)) continue;
            var up = x.Parent;
            while (up != null && gone.Contains(up)) up = up.Parent;
            x.Parent = up;
        }
        foreach (var x in gone)
        {
            _items.Remove(x);
            x.Scene = null;
            if (x is Bone b) b.Owner.DetachBone(b);
        }
        if (_camera != null && gone.Contains(_camera)) _camera = null;
        // Nothing reaches for an item that has gone, and paths leave it out (a path left with too few points stops).
        foreach (var b in Bones)
            if (b.IKGoal != null && gone.Contains(b.IKGoal)) b.IKGoal = null;
        foreach (var x in _items)
            if (x.Path is { } path && path.Points.Any(gone.Contains))
            {
                var left = path.Points.Where(p => !gone.Contains(p)).ToList();
                x.Path = left.Count >= path.Fewest ? path with { Points = left } : null;
            }
        // A modifier that takes its motion from an item that has gone (a lag's leader, a cycle's driver) is dropped.
        foreach (var x in _items)
            if (x.Modifiers.Any(m => m.Reference is { } r && gone.Contains(r)))
                x.RestoreModifiers([.. x.Modifiers.Where(m => m.Reference is not { } r || !gone.Contains(r))]);
        Notify();
    }

    /// <summary>
    /// A copy of the scene that shares nothing that can change with it, so it can be rendered on another thread while
    /// this one is edited: the settings, every item with its channels and settings, the parents and the current
    /// camera (IK goals, paths' points and modifiers' leaders and drivers pointing at the copies). Each object's document is copied once by <paramref name="copyDocument"/> (shared, as it is, if that is
    /// null); morph target meshes are shared, as nothing edits them in place.
    /// </summary>
    public Scene Copy(Func<Document, Document>? copyDocument = null)
    {
        var s = new Scene
        {
            FramesPerSecond = FramesPerSecond, FirstFrame = FirstFrame, LastFrame = LastFrame, FrameStep = FrameStep,
            AmbientColor = AmbientColor, AmbientIntensity = AmbientIntensity, BackgroundColor = BackgroundColor,
            GradientBackground = GradientBackground, GradientTop = GradientTop, GradientBottom = GradientBottom,
            Fog = Fog, FogMinDistance = FogMinDistance, FogMaxDistance = FogMaxDistance, FogColor = FogColor,
            Occlusion = Occlusion, Environment = Environment, Compositing = Compositing, _images = _images, _extras = _extras,
        };
        var docs = new Dictionary<Document, Document>(ReferenceEqualityComparer.Instance);
        var map = new Dictionary<SceneItem, SceneItem>();
        foreach (var item in _items)
        {
            var c = CopyOf(item, map, docs, copyDocument);
            s.Add(c);
            map[item] = c;
        }
        foreach (var item in _items) Relink(item, map, _ => null);
        if (_camera != null && map.TryGetValue(_camera, out var current)) s._camera = (CameraItem)current;
        return s;
    }

    /// <summary>
    /// Copies items into this scene, from this scene (Clone Item) or another (Load from Scene), each with its channels
    /// and own settings as <see cref="Copy"/> copies them; an object brings its bones with it. A copy's parent, IK goal
    /// and path points are the copies of the originals' when those were copied too, else the originals themselves when
    /// they're in this scene, else none (a path left with too few points is dropped). Documents are shared unless
    /// <paramref name="copyDocument"/> copies them (each once); names are kept, for the caller to change. A bone copied
    /// without its object goes on the same object, which must be in this scene. One <see cref="Changed"/> for them all.
    /// Returns each original with its copy, in the order they were added.
    /// </summary>
    /// <exception cref="ArgumentException">A bone's object is neither copied nor in this scene.</exception>
    public IReadOnlyDictionary<SceneItem, SceneItem> CopyItems(IEnumerable<SceneItem> items, Func<Document, Document>? copyDocument = null)
    {
        var chosen = new List<SceneItem>();
        foreach (var item in items)
        {
            if (!chosen.Contains(item)) chosen.Add(item);
            if (item is ObjectItem o)
                foreach (var b in o.Bones)
                    if (!chosen.Contains(b)) chosen.Add(b);
        }
        // Objects before bones, so a bone's object is there to put it on; otherwise in the order given.
        var ordered = chosen.Where(i => i is not Bone).Concat(chosen.Where(i => i is Bone)).ToList();
        foreach (var b in ordered.OfType<Bone>())
            if (!chosen.Contains(b.Owner) && b.Owner.Scene != this)
                throw new ArgumentException($"{b.Name}'s object, {b.Owner.Name}, isn't in this scene: copy it with its bones.");
        var docs = new Dictionary<Document, Document>(ReferenceEqualityComparer.Instance);
        var map = new Dictionary<SceneItem, SceneItem>();
        Batch(() =>
        {
            foreach (var item in ordered)
            {
                var c = CopyOf(item, map, docs, copyDocument);
                Add(c);
                map[item] = c;
            }
            foreach (var item in ordered) Relink(item, map, x => x.Scene == this ? x : null);
            Notify();
        });
        return map;
    }

    /// <summary>
    /// A new item with an item's channels and own settings, not yet in a scene: a bone goes on its object's copy in
    /// <paramref name="map"/> if there is one, else on its own object. Documents are copied once each through
    /// <paramref name="docs"/> (shared if <paramref name="copyDocument"/> is null). Extend it when an item gets a new setting.
    /// </summary>
    private static SceneItem CopyOf(SceneItem item, Dictionary<SceneItem, SceneItem> map, Dictionary<Document, Document> docs, Func<Document, Document>? copyDocument)
    {
        SceneItem c;
        switch (item)
        {
            case ObjectItem o:
                Document? doc = null;
                if (o.Document is { } d && !docs.TryGetValue(d, out doc)) docs[d] = doc = copyDocument?.Invoke(d) ?? d;
                var obj = new ObjectItem(o.Name, doc, o.Layer)
                {
                    SourcePath = o.SourcePath, CastShadows = o.CastShadows, ReceiveShadows = o.ReceiveShadows, BoneFalloffExponent = o.BoneFalloffExponent,
                };
                obj.Dissolve.CopyFrom(o.Dissolve);
                (obj.Particles, obj.Hair, obj.SoftBody) = (o.Particles, o.Hair, o.SoftBody);
                foreach (var m in o.Morphs) obj.AddMorph(new MorphTarget(m.Name, m.Target, m.Weight.Clone()));
                foreach (var x in o.Displacements)
                {
                    var w = new WaveDisplacement
                    {
                        Shape = x.Shape, Direction = x.Direction, Along = x.Along, Center = x.Center,
                        Wavelength = x.Wavelength, Speed = x.Speed, Phase = x.Phase,
                    };
                    w.Amplitude.CopyFrom(x.Amplitude);
                    obj.AddDisplacement(w);
                }
                c = obj;
                break;
            case LightItem l:
                var light = new LightItem(l.Name, l.Type)
                {
                    Color = l.Color, Range = l.Range, ConeAngle = l.ConeAngle, EdgeAngle = l.EdgeAngle, Shadows = l.Shadows,
                    AreaWidth = l.AreaWidth, AreaHeight = l.AreaHeight, Radius = l.Radius, ShadowSamples = l.ShadowSamples,
                    Flare = l.Flare, Volumetric = l.Volumetric, ShadowMap = l.ShadowMap,
                };
                light.Intensity.CopyFrom(l.Intensity);
                light.FlareIntensity.CopyFrom(l.FlareIntensity);
                c = light;
                break;
            case CameraItem cam:
                var camera = new CameraItem(cam.Name)
                {
                    Width = cam.Width, Height = cam.Height, PixelAspect = cam.PixelAspect, ActionSafe = cam.ActionSafe, TitleSafe = cam.TitleSafe,
                    DepthOfField = cam.DepthOfField, MotionBlur = cam.MotionBlur,
                };
                camera.Zoom.CopyFrom(cam.Zoom);
                c = camera;
                break;
            case Bone b:
                c = new Bone(b.Name, map.TryGetValue(b.Owner, out var owner) ? (ObjectItem)owner : b.Owner)
                {
                    RestPosition = b.RestPosition, RestRotation = b.RestRotation, RestLength = b.RestLength, Strength = b.Strength, Active = b.Active,
                    FullTimeIK = b.FullTimeIK, IKAnchor = b.IKAnchor, IKLocked = b.IKLocked, IKLimits = b.IKLimits,
                };
                break;
            default:
                c = new NullItem(item.Name);
                break;
        }
        c.Pivot = item.Pivot;
        c.Visible = item.Visible;
        c.Locked = item.Locked;
        for (int i = 0; i < 9; i++) c.Channels[i].CopyFrom(item.Channels[i]);
        c.PathPosition.CopyFrom(item.PathPosition);
        return c;
    }

    /// <summary>
    /// Points a copy's parent, IK goal, path and motion modifiers at the copies of what the original's pointed at (in <paramref name="map"/>),
    /// or at what <paramref name="outside"/> gives for one that wasn't copied (null: none). Extend it when an item gets
    /// a new reference to another.
    /// </summary>
    private static void Relink(SceneItem item, Dictionary<SceneItem, SceneItem> map, Func<SceneItem, SceneItem?> outside)
    {
        var copy = map[item];
        SceneItem? To(SceneItem x) => map.TryGetValue(x, out var c) ? c : outside(x);
        if (item.Parent is { } p) copy.RestoreParent(To(p) ?? (copy is Bone cb ? cb.Owner : null));
        if (item is Bone { IKGoal: { } goal } && To(goal) is { } g) ((Bone)copy).IKGoal = g;
        if (item.Path is { } path)
        {
            var points = path.Points.Select(To).Where(x => x != null).Cast<SceneItem>().ToList();
            copy.Path = points.Count >= path.Fewest || points.Count == path.Points.Count ? path with { Points = points } : null;
        }
        // Modifiers referring to another item refer to its copy, or to what's outside; one left with nothing to refer to is dropped.
        if (item.Modifiers.Count > 0) copy.RestoreModifiers([.. item.Modifiers.Select(m => m.Relinked(To)).Where(m => m != null).Cast<MotionModifier>()]);
    }

    /// <summary>The first item with a name, or null.</summary>
    public SceneItem? Find(string name) => _items.FirstOrDefault(i => i.Name == name);

    public double FrameToSeconds(double frame) => frame / FramesPerSecond;
    public double SecondsToFrame(double seconds) => seconds * FramesPerSecond;

    /// <summary>
    /// Works out the scene at a frame: every item's world transform and every object's deformed mesh, in object and
    /// world coordinates.
    /// </summary>
    public SceneFrame Evaluate(double frame)
    {
        var world = new Dictionary<SceneItem, Mat4>();
        foreach (var item in _items) item.WorldMatrix(frame, world);
        var objects = new List<ObjectFrame>();
        var warnings = new List<string>();
        foreach (var o in Objects)
        {
            var mesh = o.DeformedMesh(frame);
            if (!o.IsPlaceholder && o.SoftBody is { } soft) mesh = SoftBodies.At(this, o, soft, mesh, frame);
            var grown = !o.IsPlaceholder && (o.Particles != null || o.Hair != null) ? Grown(o, mesh, world[o], frame) : null;
            objects.Add(grown == null ? new ObjectFrame(o, world[o], mesh)
                : new ObjectFrame(o, world[o], grown.Mesh, grown.Surfaces) { Soft = grown.Soft, ViewOnlySurface = grown.ViewOnly });
            warnings.AddRange(o.MorphProblems());
            if (o.IsPlaceholder) warnings.Add($"{o.Name} has no geometry (its file wasn't loaded).");
            else warnings.AddRange(TextureImages.Problems(o.Surfaces, TextureImages.BaseOf(o.SourcePath ?? o.Document?.FilePath)).Select(p => $"{o.Name}, {p}"));
        }
        if (EnvironmentMap.Problem(Environment) is { } env) warnings.Add(env + " The backdrop shows instead.");
        warnings.AddRange(SceneImages.Problems(this, frame));
        return new SceneFrame(frame, world, objects, warnings);
    }
}

public sealed partial class Scene
{
    /// <summary>
    /// An object's frame with its hair and particles: the mesh (in object coordinates) and its surfaces, the puffs
    /// and sprites the ray tracer draws itself (world), and the surface of the diamonds the views show for them (which
    /// the ray tracer leaves out), or -1.
    /// </summary>
    internal sealed record GrownFrame(Mesh Mesh, IReadOnlyList<Surface> Surfaces, IReadOnlyList<SoftParticle> Soft, int ViewOnly);

    /// <summary>
    /// The object's mesh with its hair grown on it and its particles added (each on a surface of its own after the
    /// object's, then an instanced object's), in object coordinates; with Hide emitter, the particles alone.
    /// </summary>
    private GrownFrame Grown(ObjectItem o, Mesh deformed, Mat4 world, double frame)
    {
        // The same frame of the same scene and mesh with the same settings grows the same: kept until any of them changes.
        var key = (deformed, o.Particles, o.Hair, frame, Version, o.Surfaces.Count);
        lock (o.GrownLock)
            if (o.GrownCache is { } c && c.Key.Equals(key)) return c.Frame;
        var result = Grow(o, deformed, world, frame);
        lock (o.GrownLock) o.GrownCache = (key, result);
        return result;
    }

    private GrownFrame Grow(ObjectItem o, Mesh deformed, Mat4 world, double frame)
    {
        IReadOnlyList<SoftParticle> soft = [];
        int viewOnly = -1;
        var mesh = new Mesh { Points = [.. deformed.Points], Polygons = [.. deformed.Polygons], UVMaps = deformed.UVMaps };
        var surfaces = new List<Surface>(o.Surfaces.Count > 0 ? o.Surfaces : [new Surface("Default")]);
        var toLocal = world.Inverted();
        if (o.Hair is { } hair)
        {
            int index = surfaces.Count;
            surfaces.Add(new Surface($"{o.Name} hair") { Color = hair.Color, Diffuse = 0.9, Specular = 0.3, Glossiness = 32, DoubleSided = true, Smoothing = true });
            int? only = hair.OnSurface.Length == 0 ? null : surfaces.FindIndex(x => x.Name == hair.OnSurface) is >= 0 and var i && i < index ? i : -1;
            Func<Vec3, Vec3>? swing = null;
            IReadOnlyList<Vec3>? rest = null;
            if (hair.Dynamics)
            {
                Mat4 WorldAt(double f) => f == frame ? world : o.WorldMatrix(f);
                var bones = BoneDeformer.Moving(o);
                // With bones, each bone's motion swings the hair it moves; without, the object's.
                if (bones.Count > 0 && o.SoftBody == null)
                {
                    rest = o.BaseMesh().Points;
                    swing = HairSwing.Lag(hair, frame, FramesPerSecond, FirstFrame, WorldAt, bones.Count, f => BoneDeformer.Moves(bones, f), BoneDeformer.Weights(o, bones));
                }
                else swing = HairSwing.Lag(hair, frame, FramesPerSecond, FirstFrame, WorldAt);
            }
            HairGrowth.Grow(mesh, hair, toLocal.TransformVector(new Vec3(0, -1, 0)), index, only, swing, rest);
        }
        if (o.Particles is { } ps)
        {
            if (ps.HideEmitter) mesh.Polygons = [.. mesh.Polygons.Skip(deformed.Polygons.Count)];
            int index = surfaces.Count;
            surfaces.Add(new Surface($"{o.Name} particles") { Color = ps.Color, Luminosity = ps.Glow, Diffuse = 0.8, DoubleSided = true });
            var rest = o.BaseMesh();
            var source = ps.Look == ParticleLook.Object ? Objects.FirstOrDefault(x => x.Name == ps.InstanceOf && x != o && !x.IsPlaceholder) : null;
            double step = ps.Fluid ? 1 / (FramesPerSecond * Fluids.StepsPerFrame(FramesPerSecond)) : ParticleSystem.StepOf(ps, FramesPerSecond);
            var colliders = ps.Collide ? Colliders.Of(this, source == null ? [o] : [o, source], frame, step) : null;
            var alive = ps.Fluid
                ? Fluids.At(this, o, ps, frame, rest, f => f == frame ? world : o.WorldMatrix(f), colliders)
                : ParticleSystem.At(ps, frame, FramesPerSecond, rest, f => f == frame ? world : o.WorldMatrix(f), colliders);
            switch (ps.Look)
            {
                case ParticleLook.Blob:
                    surfaces[index].Smoothing = true;
                    surfaces[index].SmoothingAngle = 179;
                    surfaces[index].Specular = 0.5;
                    surfaces[index].DoubleSided = false;
                    // A fluid's particles sit about a size apart: meshed twice as big, they melt into one surface.
                    ParticleLooks.AddBlobs(mesh, ps.Fluid ? [.. alive.Select(q => q with { Size = q.Size * 2 })] : alive, toLocal, index);
                    break;
                case ParticleLook.Puff or ParticleLook.Sprite:
                    soft = ParticleLooks.Soft(alive, ps);
                    viewOnly = index;
                    ParticleSystem.AddGeometry(mesh, alive.Select(q => q with { Size = q.Size / 2 }), toLocal, index);
                    break;
                case ParticleLook.Object when source != null:
                    int first = surfaces.Count;
                    surfaces.AddRange(source.Surfaces.Count > 0 ? source.Surfaces : [new Surface("Default")]);
                    ParticleLooks.AddInstances(mesh, alive, ps, source.DeformedMesh(frame), first, toLocal);
                    break;
                default:
                    // Diamonds (and, with the object to copy missing, diamonds in its place).
                    ParticleSystem.AddGeometry(mesh, alive, toLocal, index);
                    break;
            }
        }
        mesh.Touch();
        return new GrownFrame(mesh, surfaces, soft, viewOnly);
    }
}

/// <summary>A scene worked out at one frame.</summary>
public sealed class SceneFrame(double frame, IReadOnlyDictionary<SceneItem, Mat4> world, IReadOnlyList<ObjectFrame> objects, IReadOnlyList<string> warnings)
{
    public double Frame { get; } = frame;
    /// <summary>Every item's world transform.</summary>
    public IReadOnlyDictionary<SceneItem, Mat4> World { get; } = world;
    /// <summary>Every object (visible or not; check <see cref="SceneItem.Visible"/>) with its deformed mesh.</summary>
    public IReadOnlyList<ObjectFrame> Objects { get; } = objects;
    /// <summary>Problems found on the way, such as morph targets with the wrong point count.</summary>
    public IReadOnlyList<string> Warnings { get; } = warnings;

    public Mat4 WorldOf(SceneItem item) => World[item];
}

/// <summary>An object at one frame: its world transform and its deformed mesh.</summary>
public sealed class ObjectFrame(ObjectItem item, Mat4 world, Mesh mesh, IReadOnlyList<Surface>? surfaces = null)
{
    private Mesh? _worldMesh;

    public ObjectItem Item { get; } = item;
    public Mat4 World { get; } = world;
    /// <summary>The mesh after deformation, in object coordinates (read-only).</summary>
    public Mesh Mesh { get; } = mesh;
    /// <summary>The surfaces the mesh's polygons index: the object's, and its hair's and particles' after them.</summary>
    public IReadOnlyList<Surface> Surfaces { get; } = surfaces ?? item.Surfaces;

    /// <summary>Its particles' puffs and sprites, which the ray tracer draws itself (world coordinates).</summary>
    public IReadOnlyList<SoftParticle> Soft { get; init; } = [];

    /// <summary>The surface whose faces only the views draw (the diamonds standing in for puffs and sprites), or -1.</summary>
    public int ViewOnlySurface { get; init; } = -1;

    /// <summary>The deformed mesh placed in the world (made on first use).</summary>
    public Mesh WorldMesh => _worldMesh ??= new Mesh { Points = Mesh.Points.Select(World.TransformPoint).ToList(), Polygons = Mesh.Polygons, UVMaps = Mesh.UVMaps };
}
