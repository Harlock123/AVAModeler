using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// An object: a mesh from a Modeler document (one layer, or every non-empty layer merged), with optional bones,
/// morph targets and displacements that deform it over time. Deformation happens in the object's own coordinates in
/// this order: morphs, then bones, then displacements; the item's transform then places the result in the world.
/// </summary>
public sealed class ObjectItem : SceneItem
{
    private readonly List<Bone> _bones = [];
    private readonly List<MorphTarget> _morphs = [];
    private readonly List<WaveDisplacement> _displacements = [];
    private readonly Lock _lock = new();
    private Mesh? _base;
    private int _baseStamp;
    private readonly Dictionary<double, Mesh> _frames = [];
    private (int Geometry, int Scene) _framesStamp;

    /// <summary>The document the geometry comes from; null for a placeholder (an object whose file couldn't be loaded).</summary>
    public Document? Document;
    /// <summary>The one layer to use (an index into the document's layers), or null for every non-empty layer.</summary>
    public int? Layer;
    /// <summary>The file the object was loaded from, if any.</summary>
    public string? SourcePath;
    public bool CastShadows = true, ReceiveShadows = true;
    /// <summary>
    /// How fast a bone's pull fades with distance: a bone's influence on a point is its strength divided by the
    /// point's distance from the bone's rest segment raised to this power (LightWave's "inverse distance ^ n"
    /// falloff; its file value k means 2^k).
    /// </summary>
    public double BoneFalloffExponent = 4;

    public ObjectItem(string name, Document? document = null, int? layer = null) : base(name)
    {
        Document = document;
        Layer = layer;
    }

    public override string Kind => "Object";

    public bool IsPlaceholder => Document == null;

    public IReadOnlyList<Bone> Bones => _bones;
    public IReadOnlyList<MorphTarget> Morphs => _morphs;
    public IReadOnlyList<WaveDisplacement> Displacements => _displacements;

    /// <summary>The document's surface table, which the mesh's polygons index.</summary>
    public IReadOnlyList<Surface> Surfaces => Document?.Surfaces ?? [];

    internal void AttachBone(Bone b) => _bones.Add(b);
    internal void DetachBone(Bone b) => _bones.Remove(b);

    public MorphTarget AddMorph(MorphTarget morph)
    {
        Watch(morph.Weight);
        _morphs.Add(morph);
        Scene?.Notify();
        return morph;
    }

    public void RemoveMorph(MorphTarget morph)
    {
        if (_morphs.Remove(morph)) Scene?.Notify();
    }

    public WaveDisplacement AddDisplacement(WaveDisplacement displacement)
    {
        Watch(displacement.Amplitude);
        _displacements.Add(displacement);
        Scene?.Notify();
        return displacement;
    }

    public void RemoveDisplacement(WaveDisplacement displacement)
    {
        if (_displacements.Remove(displacement)) Scene?.Notify();
    }

    private int GeometryStamp()
    {
        var h = new HashCode();
        h.Add(Document);
        h.Add(Layer);
        if (Document != null)
        {
            h.Add(Document.Version);
            foreach (var l in Document.Layers)
            {
                h.Add(l.Mesh);
                h.Add(l.Mesh.Version);
            }
        }
        foreach (var m in _morphs)
        {
            h.Add(m.Target);
            h.Add(m.Target.Version);
        }
        return h.ToHashCode();
    }

    /// <summary>The undeformed mesh in object coordinates: a copy of the chosen layer, or the non-empty layers merged.</summary>
    public Mesh BaseMesh()
    {
        lock (_lock) return CachedBase(GeometryStamp()).Clone();
    }

    private Mesh CachedBase(int stamp)
    {
        if (_base != null && _baseStamp == stamp) return _base;
        var m = new Mesh();
        if (Document != null)
        {
            if (Layer is int i)
            {
                if (i >= 0 && i < Document.Layers.Count) m.Append(Document.Layers[i].Mesh);
            }
            else
                foreach (var l in Document.Layers)
                    if (!l.Mesh.IsEmpty) m.Append(l.Mesh);
        }
        _base = m;
        _baseStamp = stamp;
        return m;
    }

    /// <summary>Morph targets that can't be used because their point count differs from the object's.</summary>
    public IReadOnlyList<string> MorphProblems()
    {
        int count;
        lock (_lock) count = CachedBase(GeometryStamp()).Points.Count;
        return _morphs.Where(m => m.Target.Points.Count != count)
            .Select(m => $"Morph target \"{m.Name}\" of {Name} has {m.Target.Points.Count} points, but the object has {count}; it is ignored.")
            .ToList();
    }

    /// <summary>
    /// The mesh at a frame in object coordinates, after morphs, bones and displacements. Results are cached by frame
    /// until the scene or the geometry changes. The mesh shares its polygon list with the cache: treat it as read-only.
    /// </summary>
    public Mesh DeformedMesh(double frame)
    {
        lock (_lock)
        {
            int geometry = GeometryStamp();
            var stamp = (geometry, Scene?.Version ?? -1);
            if (Scene == null || stamp != _framesStamp)
            {
                _frames.Clear();
                _framesStamp = stamp;
            }
            if (_frames.TryGetValue(frame, out var cached)) return cached;
            var mesh = Deform(CachedBase(geometry), frame);
            if (Scene != null) _frames[frame] = mesh;
            return mesh;
        }
    }

    private Mesh Deform(Mesh b, double frame)
    {
        if (_morphs.Count == 0 && _displacements.Count == 0 && !_bones.Any(x => x.Active))
            return new Mesh { Points = b.Points, Polygons = b.Polygons, UVMaps = b.UVMaps };
        var pts = b.Points.ToArray();
        foreach (var m in _morphs)
        {
            if (m.Target.Points.Count != pts.Length) continue;
            double w = m.Weight.Evaluate(frame);
            if (w == 0) continue;
            for (int i = 0; i < pts.Length; i++) pts[i] += (m.Target.Points[i] - b.Points[i]) * w;
        }
        BoneDeformer.Apply(this, pts, frame);
        foreach (var d in _displacements)
            for (int i = 0; i < pts.Length; i++) pts[i] = d.Displace(pts[i], frame);
        return new Mesh { Points = [.. pts], Polygons = b.Polygons, UVMaps = b.UVMaps };
    }
}

/// <summary>A shape an object can blend toward: a mesh with the object's point count, and an envelope for the blend (0 none, 1 all the way).</summary>
public sealed class MorphTarget(string name, Mesh target, Envelope? weight = null)
{
    public string Name = name;
    public Mesh Target = target;
    public Envelope Weight { get; } = weight ?? new Envelope(1);
}

/// <summary>The shape of a <see cref="WaveDisplacement"/>.</summary>
public enum WaveShape
{
    /// <summary>Straight wave fronts travelling along an axis.</summary>
    Wave,
    /// <summary>Circular wave fronts spreading out from a centre.</summary>
    Ripple,
}

/// <summary>
/// A procedural displacement: points move along <see cref="Direction"/> by
/// amplitude · sin(2π (s − speed · frame) / wavelength + phase), where s is the point's coordinate along
/// <see cref="Along"/> (for a wave) or its distance from <see cref="Center"/> across the displacement axis (for a
/// ripple). The amplitude is an envelope, so the effect can be animated.
/// </summary>
public sealed class WaveDisplacement
{
    public WaveShape Shape = WaveShape.Wave;
    /// <summary>The axis points move along.</summary>
    public Axis Direction = Axis.Y;
    /// <summary>The axis a wave travels along (unused by a ripple).</summary>
    public Axis Along = Axis.X;
    /// <summary>A ripple's centre, in object coordinates.</summary>
    public Vec3 Center;
    public double Wavelength = 1;
    /// <summary>How far the wave travels per frame.</summary>
    public double Speed;
    /// <summary>A phase offset in degrees.</summary>
    public double Phase;
    public Envelope Amplitude { get; } = new(0.1);

    public Vec3 Displace(Vec3 p, double frame)
    {
        double a = Amplitude.Evaluate(frame);
        if (a == 0 || Wavelength == 0) return p;
        double s;
        if (Shape == WaveShape.Wave) s = p[Along];
        else
        {
            var d = p - Center;
            s = d.With(Direction, 0).Length;
        }
        double offset = a * Math.Sin(2 * Math.PI * (s - Speed * frame) / Wavelength + Phase * Math.PI / 180);
        return p.With(Direction, p[Direction] + offset);
    }
}
