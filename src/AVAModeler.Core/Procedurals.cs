namespace AVAModeler.Core;

/// <summary>
/// One setting of a procedural texture, as LightWave 5's texture panel showed it, and the LWOB sub-chunk it's kept in
/// (TIP0 for the whole-number one, TFP0 to TFP3 for the others).
/// </summary>
public sealed record ProceduralParameter(string Name, string Slot, double Default, double Min, double Max, string Description)
{
    /// <summary>Whether it is a whole number (TIP0).</summary>
    public bool Whole => Slot == "TIP0";

    /// <summary>The name an agent or macro uses: lower case with underscores ("small_power").</summary>
    public string Key => Procedurals.KeyOf(Name);
}

/// <summary>
/// A kind of procedural texture: its LightWave name (as LWOB's CTEX and the other channel sub-chunks name it), what
/// it draws, and its settings.
/// </summary>
public sealed record ProceduralType(ProceduralKind Kind, string Name, string Summary, IReadOnlyList<ProceduralParameter> Parameters)
{
    /// <summary>The name an agent or macro uses: lower case with underscores ("fractal_noise").</summary>
    public string Key => Procedurals.KeyOf(Name);

    public ProceduralParameter? Find(string name)
    {
        string key = Procedurals.KeyOf(name), squeezed = key.Replace("_", "");
        return Parameters.FirstOrDefault(p => p.Key == key || p.Key.Replace("_", "") == squeezed || Procedurals.KeyOf(p.Slot) == key);
    }
}

/// <summary>The procedural textures this draws (the numbers are what the OpenGL shader is told).</summary>
public enum ProceduralKind
{
    Checkerboard = 1, Grid, Dots, FractalNoise, Marble, Wood, Ripples, Underwater, Crumple, Turbulence, Value, BumpArray,
}

/// <summary>
/// LightWave 5's procedural textures: patterns worked out from where a point is, in 3D, rather than read from an
/// image. Each gives every point an amount from 0 to 1; the layer then lays its texture colour (on the colour channel)
/// or its texture value (on diffuse, specular, luminosity, reflection or transparency) over the surface by that amount,
/// or (on bump) uses it as the height. Like the image projections they work in the object's coordinates from the
/// layer's centre, one pattern unit per <see cref="TextureLayer.Size"/> along each axis (the world's, for a layer in
/// world coordinates); Negative turns the amount round and Falloff fades it away from the centre.
/// <para>
/// The noisy ones are built on <see cref="Noise"/> (Perlin's improved noise with a fixed table), so every render is the
/// same. The patterns that are flat by nature (Grid, Dots, Bump Array, Wood's rings) are seen looking down the texture
/// axis, as a planar image is, so a floor gets the same pattern at any height; Marble's veins are layered along the
/// axis; the rest are fully 3D. Ripples' and Underwater's wave speed is kept for LightWave but not animated.
/// </para>
/// </summary>
public static class Procedurals
{
    private static ProceduralParameter P(string name, string slot, double def, double min, double max, string description) =>
        new(name, slot, def, min, max, description);

    private static readonly ProceduralParameter Frequencies3 = P("Frequencies", "TIP0", 3, 1, 8, "How many octaves of noise are added up: more gives finer detail.");
    private static readonly ProceduralParameter SmallPowerP = P("Small Power", "TFP1", 0.5, 0, 1, "How strong each finer octave is next to the one before (0 to 1): higher is rougher.");
    private static readonly ProceduralParameter ContrastP = P("Contrast", "TFP0", 1, 0, 10, "How far the amounts spread from the middle: 1 is natural, higher is starker.");

    /// <summary>Every procedural this draws, in the order the texture panel lists them.</summary>
    public static readonly IReadOnlyList<ProceduralType> All =
    [
        new(ProceduralKind.Checkerboard, "Checkerboard", "Alternating cubes one texture size across: the texture colour on every other one.", []),
        new(ProceduralKind.Grid, "Grid", "Lines one texture size apart, seen down the texture axis, in the texture colour.",
            [P("Line Thickness", "TFP0", 0.1, 0.001, 1, "The lines' width as a fraction of the grid's spacing.")]),
        new(ProceduralKind.Dots, "Dots", "Round dots one texture size apart, seen down the texture axis.",
            [P("Dot Diameter", "TFP0", 0.5, 0, 1, "The dots' width as a fraction of their spacing."),
             P("Fuzziness", "TFP1", 0, 0, 1, "How soft the dots' edges are: 0 sharp, 1 fading all the way in.")]),
        new(ProceduralKind.FractalNoise, "Fractal Noise", "Cloudy noise: the texture colour in drifts and patches, about half the surface.",
            [Frequencies3, ContrastP, SmallPowerP]),
        new(ProceduralKind.Marble, "Marble", "Thin veins of the texture colour layered along the texture axis, swirled by turbulence.",
            [P("Frequencies", "TIP0", 4, 1, 8, "Octaves of turbulence: more gives finer swirls."),
             P("Turbulence", "TFP0", 0.5, 0, 10, "How far the veins are swirled, in texture sizes."),
             P("Vein Spacing", "TFP1", 0.5, 0.001, 100, "How far apart the veins are, in texture sizes."),
             P("Vein Sharpness", "TFP2", 4, 0.1, 100, "How thin the veins are: higher is finer.")]),
        new(ProceduralKind.Wood, "Wood", "Growth rings round the texture axis, in the texture colour, made irregular by turbulence.",
            [Frequencies3,
             P("Turbulence", "TFP0", 0.25, 0, 10, "How far the rings wander, in texture sizes."),
             P("Ring Spacing", "TFP1", 0.1, 0.001, 100, "How far apart the rings are, in texture sizes."),
             P("Ring Sharpness", "TFP2", 2, 0.1, 100, "How sharply each ring ends: higher gives thinner, darker rings.")]),
        new(ProceduralKind.Ripples, "Ripples", "Circular waves spreading from a few sources, best as a bump (water, ripples in sand).",
            [P("Wave Sources", "TIP0", 3, 1, 16, "How many centres the waves spread from."),
             P("Wavelength", "TFP0", 0.3, 0.001, 100, "Crest to crest, in texture sizes."),
             P("Wave Speed", "TFP1", 0.05, 0, 10, "LightWave's speed for animated waves; kept, but the waves here don't move.")]),
        new(ProceduralKind.Underwater, "Underwater", "The bright web of light seen on the floor of a pool, where waves from a few sources cross.",
            [P("Wave Sources", "TIP0", 4, 1, 16, "How many centres the waves spread from."),
             P("Wavelength", "TFP0", 0.4, 0.001, 100, "Crest to crest, in texture sizes."),
             P("Wave Speed", "TFP1", 0.05, 0, 10, "LightWave's speed for animated waves; kept, but the waves here don't move."),
             P("Band Sharpness", "TFP2", 4, 0.1, 100, "How thin the bright bands are.")]),
        new(ProceduralKind.Crumple, "Crumple", "Creased, folded patches like crumpled paper; good as a bump.",
            [Frequencies3, P("Small Power", "TFP0", 0.5, 0, 1, "How strong each finer octave is next to the one before (0 to 1): higher is rougher.")]),
        new(ProceduralKind.Turbulence, "Turbulence", "Billowing noise with sharp creases (like smoke or fire), the texture colour where it's strong.",
            [Frequencies3, ContrastP, SmallPowerP]),
        new(ProceduralKind.Value, "Value", "The texture colour or value everywhere: with Falloff, a gradient fading from the centre.", []),
        new(ProceduralKind.BumpArray, "Bump Array", "Rows of round bumps, seen down the texture axis; good as a bump (rivets, studs).",
            [P("Bump Radius", "TFP0", 0.25, 0.001, 100, "Each bump's radius, in texture sizes."),
             P("Bump Spacing", "TFP1", 0.5, 0.001, 100, "How far apart the bumps are, in texture sizes.")]),
    ];

    /// <summary>The LWOB sub-chunks the settings live in, in the order of <see cref="TextureLayer.Parameters"/>.</summary>
    public static readonly string[] Slots = ["TFP0", "TFP1", "TFP2", "TFP3", "TIP0"];

    /// <summary>Where a slot's value is in <see cref="TextureLayer.Parameters"/>.</summary>
    public static int SlotIndex(string slot) => Array.IndexOf(Slots, slot);

    /// <summary>A name made easy to match: lower case, spaces and dashes as underscores.</summary>
    public static string KeyOf(string name) => name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    /// <summary>A procedural by its LightWave name or key, in any case ("Fractal Noise", "fractal_noise", "fractalnoise"); null if unknown.</summary>
    public static ProceduralType? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = KeyOf(name), squeezed = key.Replace("_", "");
        return All.FirstOrDefault(t => t.Key == key || t.Key.Replace("_", "") == squeezed);
    }

    public static ProceduralType Of(ProceduralKind kind) => All.First(t => t.Kind == kind);

    /// <summary>The names, for messages: "Checkerboard, Grid, …".</summary>
    public static string Names => string.Join(", ", All.Select(t => t.Name));

    /// <summary>The keys, for agents: "checkerboard, grid, …".</summary>
    public static string Keys => string.Join(", ", All.Select(t => t.Key));

    /// <summary>Makes a layer a procedural of a kind with its default settings (keeping its channel, centre, size and so on).</summary>
    public static void Apply(TextureLayer t, ProceduralType type)
    {
        t.Procedural = type.Name;
        t.Parameters = new double[Slots.Length];
        foreach (var p in type.Parameters) t.Parameters[SlotIndex(p.Slot)] = p.Default;
    }

    /// <summary>A layer's setting by name (or slot), or its default if the layer doesn't have one.</summary>
    public static double Get(TextureLayer t, string name)
    {
        var type = Find(t.Procedural) ?? throw new InvalidOperationException($"\"{t.Procedural ?? "an image"}\" isn't a procedural this draws.");
        var p = type.Find(name) ?? throw new ArgumentException(Unknown(type, name));
        return t.Parameters[SlotIndex(p.Slot)];
    }

    /// <summary>Sets a layer's setting by name (or slot), checked against its limits (a whole number for TIP0).</summary>
    public static void Set(TextureLayer t, string name, double value)
    {
        var type = Find(t.Procedural) ?? throw new InvalidOperationException($"\"{t.Procedural ?? "an image"}\" isn't a procedural this draws.");
        var p = type.Find(name) ?? throw new ArgumentException(Unknown(type, name));
        if (!double.IsFinite(value) || value < p.Min || value > p.Max)
            throw new ArgumentException($"{type.Name}'s {p.Name} must be from {p.Min:0.###} to {p.Max:0.###}; {value:0.###} was given.");
        t.Parameters[SlotIndex(p.Slot)] = p.Whole ? Math.Round(value) : value;
    }

    private static string Unknown(ProceduralType type, string name) => type.Parameters.Count == 0
        ? $"{type.Name} has no settings (\"{name}\" was given)."
        : $"{type.Name} has no setting \"{name}\"; it has {string.Join(", ", type.Parameters.Select(p => p.Key))}.";

    /// <summary>
    /// The texture types as LightWave 5's Texture Type list had them: the image maps by projection ("Planar Image
    /// Map"…, and "UV Image Map"), then the procedurals.
    /// </summary>
    public static IReadOnlyList<string> TextureTypes { get; } =
        [.. Enum.GetValues<TextureProjection>().Select(ImageType), .. All.Select(t => t.Name)];

    /// <summary>An image map's type name: "Planar Image Map" and so on.</summary>
    public static string ImageType(TextureProjection p) => $"{p} Image Map";

    /// <summary>A layer's type name, as <see cref="TextureTypes"/> lists it (or the file's name for a procedural this doesn't know).</summary>
    public static string TypeOf(TextureLayer t) => t.Procedural == null ? ImageType(t.Projection) : Find(t.Procedural)?.Name ?? t.Procedural;

    /// <summary>
    /// Changes a layer's type: an image map by projection, or a procedural (with that kind's default settings, unless
    /// it is that kind already). The channel, centre, size, axis, opacity and the rest stay.
    /// </summary>
    public static void SetType(TextureLayer t, string type)
    {
        foreach (var p in Enum.GetValues<TextureProjection>())
            if (string.Equals(ImageType(p), type, StringComparison.OrdinalIgnoreCase))
            {
                t.Procedural = null;
                t.Projection = p;
                return;
            }
        var kind = Find(type) ?? throw new ArgumentException($"There's no texture type \"{type}\": the types are {string.Join(", ", TextureTypes)}.");
        if (Find(t.Procedural) != kind) Apply(t, kind);
    }

    /// <summary>A layer's procedural, ready to evaluate, or null for an image layer or a procedural this doesn't draw.</summary>
    public static ProceduralTexture? Prepare(TextureLayer t) => Find(t.Procedural) is { } type ? new ProceduralTexture(t, type) : null;
}

/// <summary>
/// A procedural texture layer ready to evaluate: <see cref="Amount"/> gives the 0-to-1 amount at a point, taking the
/// layer's centre, size, axis, settings, Negative and Falloff into account. It copies what it needs from the layer, so
/// it can be used from several threads while the layer is edited. The OpenGL renderer's shader repeats these sums.
/// </summary>
public sealed class ProceduralTexture
{
    public ProceduralType Type { get; }
    public ProceduralKind Kind => Type.Kind;
    public Vec3 Center { get; }
    public Vec3 Size { get; }
    public Vec3 Falloff { get; }
    public Axis Axis { get; }
    public bool Negative { get; }
    public bool WorldCoordinates { get; }
    /// <summary>TFP0 to TFP3 and TIP0, as in <see cref="TextureLayer.Parameters"/>.</summary>
    public double[] Parameters { get; }
    /// <summary>The colour laid on the colour channel, 0 to 1.</summary>
    public Vec3 Color { get; }
    /// <summary>The value laid on the other channels.</summary>
    public double Value { get; }

    /// <summary>A small shift of the checkerboard's cubes so a face lying exactly on a cube's side (a floor at y = 0) doesn't speckle.</summary>
    public const double LatticeShift = 1e-4;

    public ProceduralTexture(TextureLayer t, ProceduralType type)
    {
        Type = type;
        Center = t.Center;
        Size = new Vec3(Safe(t.Size.X), Safe(t.Size.Y), Safe(t.Size.Z));
        Falloff = t.Falloff;
        Axis = t.Axis;
        Negative = t.Negative;
        WorldCoordinates = t.WorldCoordinates;
        Parameters = t.Parameters.Length >= Procedurals.Slots.Length ? (double[])t.Parameters.Clone() : new double[Procedurals.Slots.Length];
        Color = t.TextureColor.ToVec();
        Value = t.Value;
    }

    private static double Safe(double s) => Math.Abs(s) < 1e-12 ? 1 : s;

    private double F(int i) => Parameters[i];

    /// <summary>The whole-number setting (TIP0): octaves 1 to 8, or wave sources 1 to 16.</summary>
    public int Count => (int)Math.Clamp(Math.Round(Parameters[4]), 1, Kind is ProceduralKind.Ripples or ProceduralKind.Underwater ? 16 : 8);

    /// <summary>How much of the layer shows at a point (object coordinates, or world ones for a world-coordinates layer), 0 to 1.</summary>
    public double Amount(Vec3 point)
    {
        var d = point - Center;
        var q = new Vec3(d.X / Size.X, d.Y / Size.Y, d.Z / Size.Z);
        double f = Math.Clamp(Pattern(q), 0, 1);
        if (Negative) f = 1 - f;
        if (Falloff != Vec3.Zero)
            f *= Math.Max(0, 1 - Falloff.X * Math.Abs(d.X)) * Math.Max(0, 1 - Falloff.Y * Math.Abs(d.Y)) * Math.Max(0, 1 - Falloff.Z * Math.Abs(d.Z));
        return f;
    }

    /// <summary>
    /// The pattern at a point in texture units (one unit per texture size, from the centre), before Negative and
    /// Falloff.
    /// </summary>
    public double Pattern(Vec3 q)
    {
        var (s, t, along) = Cross(q);
        switch (Kind)
        {
            case ProceduralKind.Checkerboard:
            {
                long sum = (long)Math.Floor(q.X + LatticeShift) + (long)Math.Floor(q.Y + LatticeShift) + (long)Math.Floor(q.Z + LatticeShift);
                return (sum & 1) == 0 ? 0 : 1;
            }
            case ProceduralKind.Grid:
            {
                double ds = Math.Abs(s - Math.Round(s)), dt = Math.Abs(t - Math.Round(t));
                return Math.Min(ds, dt) < F(0) / 2 ? 1 : 0;
            }
            case ProceduralKind.Dots:
            {
                double ds = s - Math.Round(s), dt = t - Math.Round(t);
                double dist = Math.Sqrt(ds * ds + dt * dt), r = F(0) / 2, soft = F(1) * r;
                if (soft <= 0) return dist < r ? 1 : 0;
                return 1 - SmoothStep(r - soft, r, dist);
            }
            case ProceduralKind.FractalNoise:
                return 0.5 + F(0) * Noise.Fractal(q, Count, F(1));
            case ProceduralKind.Turbulence:
                return F(0) * 2.5 * Noise.Turbulence(q, Count, F(1));
            case ProceduralKind.Crumple:
            {
                int octaves = Count;
                var warp = new Vec3(Noise.Fractal(q + new Vec3(5.2, 1.3, 7.7), octaves, F(0)),
                    Noise.Fractal(q + new Vec3(1.7, 9.2, 3.4), octaves, F(0)), Noise.Fractal(q + new Vec3(8.3, 2.8, 4.1), octaves, F(0)));
                return 2.5 * Noise.Turbulence(q + warp, octaves, F(0));
            }
            case ProceduralKind.Marble:
            {
                double v = (along + F(0) * Noise.Turbulence(q, Count, 0.5)) / Math.Max(F(1), 1e-6);
                return Math.Pow(1 - Math.Abs(Math.Sin(Math.PI * v)), Math.Max(F(2), 1e-3));
            }
            case ProceduralKind.Wood:
            {
                double r = Math.Sqrt(s * s + t * t);
                double v = (r + F(0) * Noise.Turbulence(q, Count, 0.5)) / Math.Max(F(1), 1e-6);
                return Math.Pow(v - Math.Floor(v), Math.Max(F(2), 1e-3));
            }
            case ProceduralKind.Ripples:
            {
                int n = Count;
                double sum = 0, k = 2 * Math.PI / Math.Max(F(0), 1e-6);
                for (int i = 0; i < n; i++) sum += Math.Cos((q - Source(i)).Length * k);
                return 0.5 + 0.5 * sum / n;
            }
            case ProceduralKind.Underwater:
            {
                int n = Count;
                double sum = 0, k = 2 * Math.PI / Math.Max(F(0), 1e-6);
                for (int i = 0; i < n; i++) sum += Math.Sin((q - Source(i)).Length * k + Phase(i));
                return Math.Pow(1 - Math.Abs(sum / n), Math.Max(F(2), 1e-3));
            }
            case ProceduralKind.BumpArray:
            {
                double spacing = Math.Max(F(1), 1e-6);
                double cs = s / spacing, ct = t / spacing;
                double ds = (cs - Math.Round(cs)) * spacing, dt = (ct - Math.Round(ct)) * spacing;
                double r = Math.Max(F(0), 1e-6), x = (ds * ds + dt * dt) / (r * r);
                return x >= 1 ? 0 : Math.Sqrt(1 - x);
            }
            default:
                return 1;
        }
    }

    /// <summary>A point as (across, up, along) the texture axis: what the view down that axis shows, and the depth.</summary>
    private (double S, double T, double Along) Cross(Vec3 q) => Axis switch
    {
        Axis.X => (q.Z, q.Y, q.X),
        Axis.Y => (q.X, q.Z, q.Y),
        _ => (q.X, q.Y, q.Z),
    };

    /// <summary>Where wave source i is, from the noise's fixed table: within a texture size of the centre.</summary>
    public static Vec3 Source(int i) => new(
        Noise.Perm(3 * i) / 127.5 - 1, Noise.Perm(3 * i + 1) / 127.5 - 1, Noise.Perm(3 * i + 2) / 127.5 - 1);

    /// <summary>Wave source i's phase, from the same table.</summary>
    public static double Phase(int i) => Noise.Perm(3 * i + 100) / 255.0 * 2 * Math.PI;

    private static double SmoothStep(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
