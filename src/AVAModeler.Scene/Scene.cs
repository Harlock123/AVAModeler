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
/// ambient light, background, fog, and the camera to render through). Colours are RGB from 0 to 1.
/// <para>Envelope edits and adding, removing or reparenting items bump <see cref="Version"/> and raise
/// <see cref="Changed"/> on their own; after changing a field directly, call <see cref="Notify"/>.</para>
/// </summary>
public sealed class Scene
{
    private readonly List<SceneItem> _items = [];
    private CameraItem? _camera;

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

    public IReadOnlyList<SceneItem> Items => _items;
    public IEnumerable<ObjectItem> Objects => _items.OfType<ObjectItem>();
    public IEnumerable<NullItem> Nulls => _items.OfType<NullItem>();
    public IEnumerable<LightItem> Lights => _items.OfType<LightItem>();
    public IEnumerable<CameraItem> Cameras => _items.OfType<CameraItem>();
    public IEnumerable<Bone> Bones => _items.OfType<Bone>();

    /// <summary>Goes up on every change, so views know to redraw.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after any change.</summary>
    public event Action? Changed;

    public void Notify()
    {
        Version++;
        Changed?.Invoke();
    }

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
    /// else parented to it or them is unparented).
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
        Notify();
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
            objects.Add(new ObjectFrame(o, world[o], mesh));
            warnings.AddRange(o.MorphProblems());
            if (o.IsPlaceholder) warnings.Add($"{o.Name} has no geometry (its file wasn't loaded).");
            else warnings.AddRange(TextureImages.Problems(o.Surfaces, TextureImages.BaseOf(o.SourcePath ?? o.Document?.FilePath)).Select(p => $"{o.Name}, {p}"));
        }
        return new SceneFrame(frame, world, objects, warnings);
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
public sealed class ObjectFrame(ObjectItem item, Mat4 world, Mesh mesh)
{
    private Mesh? _worldMesh;

    public ObjectItem Item { get; } = item;
    public Mat4 World { get; } = world;
    /// <summary>The mesh after deformation, in object coordinates (read-only).</summary>
    public Mesh Mesh { get; } = mesh;
    public IReadOnlyList<Surface> Surfaces => Item.Surfaces;

    /// <summary>The deformed mesh placed in the world (made on first use).</summary>
    public Mesh WorldMesh => _worldMesh ??= new Mesh { Points = Mesh.Points.Select(World.TransformPoint).ToList(), Polygons = Mesh.Polygons, UVMaps = Mesh.UVMaps };
}
