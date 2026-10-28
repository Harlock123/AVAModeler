namespace AVAModeler.Core;

/// <summary>Which of a surface's settings a texture layer changes.</summary>
public enum TextureChannel { Color, Diffuse, Specular, Luminosity, Reflection, Transparency, Bump }

/// <summary>
/// How an image is laid onto a surface, as LightWave 5 offered: flat along an axis, wrapped round it as a cylinder or
/// a sphere, from all six sides (cubic, each face taking the axis it faces most), or by a UV map.
/// </summary>
public enum TextureProjection { Planar, Cylindrical, Spherical, Cubic, UV }

/// <summary>
/// What an image does past its edges, across or up: nothing (the surface shows through), tile, tile mirrored, or
/// stretch its edge pixels.
/// </summary>
public enum TextureTiling { Reset, Repeat, Mirror, Edge }

/// <summary>How a texture layer combines with what's under it, at its opacity.</summary>
public enum TextureBlend { Normal, Additive, Multiply }

/// <summary>
/// A texture layer on a surface: an image laid on by a projection, changing one channel (colour, diffuse, specular,
/// luminosity, reflection, transparency or bump). Layers apply in order, each over the ones before it. For a value
/// channel the image's brightness is the value; for bump, its brightness is the height.
/// <para>
/// The projections work in object coordinates from <see cref="Center"/>, scaled by <see cref="Size"/>. Planar, cubic
/// and cylindrical heights show the image as the view looking down that axis shows the object (Top: X right, Z up;
/// Front: X right, Y up; Side: Z right, Y up), one image per <see cref="Size"/>. Cylindrical and spherical wrap the
/// image round the axis <see cref="WidthWrap"/> times (spherical also <see cref="HeightWrap"/> times pole to pole),
/// with the seam at the back.
/// </para>
/// A layer read from an LWOB file keeps its sub-chunks (<see cref="Raw"/>), so an unchanged layer writes back exactly,
/// and a procedural texture this program doesn't draw (<see cref="Procedural"/>) is still kept.
/// </summary>
public sealed class TextureLayer
{
    public TextureChannel Channel;
    public TextureProjection Projection = TextureProjection.Planar;
    /// <summary>The image file, as given (relative paths are found from the object's folder; see <see cref="TextureImages"/>).</summary>
    public string Image = "";
    /// <summary>The UV map a UV projection uses.</summary>
    public string UVMap = "";
    public Axis Axis = Axis.Z;
    public Vec3 Center;
    public Vec3 Size = Vec3.One;
    public TextureTiling WidthTiling = TextureTiling.Repeat, HeightTiling = TextureTiling.Repeat;
    /// <summary>How many times a cylindrical or spherical map goes round (width) and pole to pole (height).</summary>
    public double WidthWrap = 1, HeightWrap = 1;
    /// <summary>How much of the layer shows over what's under it (0 to 1).</summary>
    public double Opacity = 1;
    public TextureBlend Blend = TextureBlend.Normal;
    /// <summary>A bump layer's strength: 1 tilts the surface 45° where the image steps from black to white in one pixel.</summary>
    public double Amplitude = 1;
    /// <summary>Blend between pixels (bilinear filtering); off shows each pixel as a square.</summary>
    public bool PixelBlending = true;
    public bool Negative, Antialiasing, WorldCoordinates;
    /// <summary>LWOB's antialiasing strength, kept for writing back.</summary>
    public double AntialiasingStrength = 1;
    /// <summary>LWOB's texture value (for procedural textures), kept for writing back.</summary>
    public double Value = 1;
    /// <summary>The LWOB type name of a procedural texture (such as "Fractal Noise"), which is kept but not drawn; null for an image map.</summary>
    public string? Procedural;
    /// <summary>The layer's sub-chunks as read from an LWOB file (its xTEX first), for writing back unchanged.</summary>
    public List<RawChunk>? Raw;
    /// <summary>The settings <see cref="Raw"/> was read with: while they still match, the raw sub-chunks are written back.</summary>
    public string? RawSignature;

    public TextureLayer Clone()
    {
        var t = (TextureLayer)MemberwiseClone();
        t.Raw = Raw == null ? null : [.. Raw];
        return t;
    }

    /// <summary>Whether this layer is an image the program can draw.</summary>
    public bool IsImage => Procedural == null;

    /// <summary>The layer's settings in one string, to tell whether it has changed since it was read.</summary>
    public string Signature() => FormattableString.Invariant(
        $"{Channel}|{Projection}|{Image}|{UVMap}|{Axis}|{Center}|{Size.X:R},{Size.Y:R},{Size.Z:R},{Center.X:R},{Center.Y:R},{Center.Z:R}|{WidthTiling}|{HeightTiling}|{WidthWrap:R}|{HeightWrap:R}|{Opacity:R}|{Blend}|{Amplitude:R}|{PixelBlending}|{Negative}|{Antialiasing}|{WorldCoordinates}|{AntialiasingStrength:R}|{Value:R}|{Procedural}");

    public override string ToString() => $"{Channel}: {(Procedural ?? Projection.ToString())} {Image}";
}

/// <summary>The maths of the projections: where a point on a surface falls on a texture layer's image.</summary>
public static class TextureMapping
{
    /// <summary>
    /// The image coordinates (u across from the left, v up from the bottom; 0 to 1 is one image) of a point in object
    /// coordinates, for every projection but UV (which reads the mesh's UV map instead). <paramref name="normal"/> is
    /// the face's normal, which picks a cubic map's side.
    /// </summary>
    public static Vec2 Project(TextureLayer t, Vec3 point, Vec3 normal = default) =>
        Project(t.Projection, t.Axis, t.Center, t.Size, t.WidthWrap, t.HeightWrap, point, normal);

    /// <inheritdoc cref="Project(TextureLayer, Vec3, Vec3)"/>
    public static Vec2 Project(TextureProjection projection, Axis axis, Vec3 center, Vec3 size, double widthWrap, double heightWrap, Vec3 point, Vec3 normal)
    {
        var d = point - center;
        switch (projection)
        {
            case TextureProjection.Cubic:
                return Planar(Dominant(normal, axis), d, size);
            case TextureProjection.Cylindrical:
            {
                var (s, t, h) = Around(axis, d);
                double u = Angle(s, t) * widthWrap;
                return new Vec2(u, h / Safe(size[axis]) + 0.5);
            }
            case TextureProjection.Spherical:
            {
                var (s, t, h) = Around(axis, d);
                double u = Angle(s, t) * widthWrap;
                double latitude = Math.Atan2(h, Math.Sqrt(s * s + t * t));
                return new Vec2(u, (latitude / Math.PI + 0.5) * heightWrap);
            }
            default:
                return Planar(axis, d, size);
        }
    }

    /// <summary>
    /// The planar map along an axis: the image as the view looking down that axis shows it, one image per size,
    /// centred on the centre.
    /// </summary>
    private static Vec2 Planar(Axis axis, Vec3 d, Vec3 size)
    {
        var (right, up) = ViewAxes(axis);
        return new Vec2(d[right] / Safe(size[right]) + 0.5, d[up] / Safe(size[up]) + 0.5);
    }

    /// <summary>The axes the view down an axis shows across and up: Top X and Z, Front X and Y, Side Z and Y.</summary>
    public static (Axis Right, Axis Up) ViewAxes(Axis axis) => axis switch
    {
        Axis.Y => (Axis.X, Axis.Z),
        Axis.X => (Axis.Z, Axis.Y),
        _ => (Axis.X, Axis.Y),
    };

    /// <summary>
    /// A point relative to the centre as (across, toward the front, along the axis). Round Y the front is toward the
    /// Front view (−Z) with X across; round Z it is toward the Top view (+Y) with X across; round X it is toward the
    /// Front view (−Z) with Y across.
    /// </summary>
    private static (double Across, double Toward, double Along) Around(Axis axis, Vec3 d) => axis switch
    {
        Axis.X => (d.Y, -d.Z, d.X),
        Axis.Z => (d.X, d.Y, d.Z),
        _ => (d.X, -d.Z, d.Y),
    };

    /// <summary>The angle round the axis as a fraction of a turn: 0.5 straight toward the front, 0 and 1 at the back.</summary>
    private static double Angle(double across, double toward)
    {
        if (across == 0 && toward == 0) return 0.5;
        return Math.Atan2(across, toward) / (2 * Math.PI) + 0.5;
    }

    /// <summary>The axis a normal points along most (the given one when it has no length).</summary>
    public static Axis Dominant(Vec3 n, Axis fallback = Axis.Z)
    {
        double x = Math.Abs(n.X), y = Math.Abs(n.Y), z = Math.Abs(n.Z);
        if (x == 0 && y == 0 && z == 0) return fallback;
        return x >= y && x >= z ? Axis.X : y >= z ? Axis.Y : Axis.Z;
    }

    private static double Safe(double size) => Math.Abs(size) < 1e-12 ? 1 : size;

    /// <summary>
    /// LightWave's Automatic Sizing: the centre and size of a box (such as a surface's bounds), with sides of no
    /// thickness given the size of the largest side so the image isn't squeezed to nothing.
    /// </summary>
    public static (Vec3 Center, Vec3 Size) AutoSize(Box3 bounds)
    {
        if (bounds.IsEmpty) return (Vec3.Zero, Vec3.One);
        var s = bounds.Size;
        double big = Math.Max(bounds.Extent, 1e-9);
        double Fix(double v) => v > big * 1e-6 ? v : big;
        return (bounds.Center, new Vec3(Fix(s.X), Fix(s.Y), Fix(s.Z)));
    }

    /// <summary>
    /// For a face's corners' coordinates from a wrapping projection: moves corners across the seam to the same side,
    /// so the face doesn't stretch the whole image back across itself. Corners lying on the seam itself go to whichever
    /// end keeps the face within the image; otherwise the tiling carries the far ones round.
    /// </summary>
    public static void JoinSeam(Span<Vec2> uvs, TextureLayer t) => JoinSeam(uvs, t.Projection, t.WidthWrap);

    /// <inheritdoc cref="JoinSeam(Span{Vec2}, TextureLayer)"/>
    public static void JoinSeam(Span<Vec2> uvs, TextureProjection projection, double widthWrap)
    {
        if (projection is not (TextureProjection.Cylindrical or TextureProjection.Spherical) || uvs.Length < 2) return;
        double period = Math.Abs(widthWrap) < 1e-12 ? 1 : Math.Abs(widthWrap);
        double max = double.NegativeInfinity, min = double.PositiveInfinity;
        foreach (var uv in uvs)
        {
            max = Math.Max(max, uv.X);
            min = Math.Min(min, uv.X);
        }
        if (max - min <= period / 2) return;
        // A corner exactly on the seam can be either end of the image. If the face's far-side corners all sit on it,
        // they're the ones to move (to 0), so the face stays within the image rather than running a column past its end.
        double tolerance = 1e-9 * period;
        bool farOnSeam = true;
        foreach (var uv in uvs)
            if (max - uv.X <= period / 2 && Math.Abs(uv.X - period) > tolerance) farOnSeam = false;
        for (int i = 0; i < uvs.Length; i++)
        {
            if (farOnSeam && max - uvs[i].X <= period / 2) uvs[i] = uvs[i] with { X = uvs[i].X - period };
            else if (!farOnSeam && max - uvs[i].X > period / 2) uvs[i] = uvs[i] with { X = uvs[i].X + period };
        }
    }

    /// <summary>A colour layer over what's under it, at a weight (its opacity times the image's alpha), by its blend mode.</summary>
    public static Vec3 Blend(Vec3 under, Vec3 over, double weight, TextureBlend mode) => mode switch
    {
        TextureBlend.Additive => under + over * weight,
        TextureBlend.Multiply => Vec3.Scale(under, Vec3.Lerp(Vec3.One, over, weight)),
        _ => Vec3.Lerp(under, over, weight),
    };

    /// <summary>A value layer (its image's brightness) over a value, at a weight, by its blend mode.</summary>
    public static double Blend(double under, double over, double weight, TextureBlend mode) => mode switch
    {
        TextureBlend.Additive => under + over * weight,
        TextureBlend.Multiply => under * (1 - weight + over * weight),
        _ => under + (over - under) * weight,
    };

    /// <summary>
    /// The image coordinates of a face's corners (points in object coordinates, and the face's normal), for every
    /// projection but UV: each corner projected, those across a wrapping projection's seam brought to the same side, and
    /// a corner on the axis itself (a pole, where the way round means nothing) given the middle of the others' u, so a
    /// face meeting at a pole isn't pulled to the image's middle.
    /// </summary>
    public static void ProjectFace(TextureProjection projection, Axis axis, Vec3 center, Vec3 size, double widthWrap, double heightWrap,
        ReadOnlySpan<Vec3> points, Vec3 normal, Span<Vec2> result)
    {
        bool wraps = projection is TextureProjection.Cylindrical or TextureProjection.Spherical;
        Span<bool> pole = stackalloc bool[points.Length];
        int poles = 0;
        for (int i = 0; i < points.Length; i++)
        {
            result[i] = Project(projection, axis, center, size, widthWrap, heightWrap, points[i], normal);
            if (!wraps) continue;
            var (across, toward, _) = Around(axis, points[i] - center);
            double far = Math.Max(1, (points[i] - center).Length);
            if (Math.Sqrt(across * across + toward * toward) <= 1e-9 * far) { pole[i] = true; poles++; }
        }
        if (!wraps) return;
        if (poles == 0 || poles == points.Length)
        {
            JoinSeam(result, projection, widthWrap);
            return;
        }
        // Join the seam among the corners off the axis, then put the poles' u in their middle.
        Span<Vec2> off = stackalloc Vec2[points.Length - poles];
        for (int i = 0, k = 0; i < points.Length; i++)
            if (!pole[i]) off[k++] = result[i];
        JoinSeam(off, projection, widthWrap);
        double sum = 0;
        for (int i = 0, k = 0; i < points.Length; i++)
            if (!pole[i])
            {
                result[i] = off[k++];
                sum += result[i].X;
            }
        double middle = sum / off.Length;
        for (int i = 0; i < points.Length; i++)
            if (pole[i]) result[i] = result[i] with { X = middle };
    }

    /// <inheritdoc cref="ProjectFace(TextureProjection, Axis, Vec3, Vec3, double, double, ReadOnlySpan{Vec3}, Vec3, Span{Vec2})"/>
    public static void ProjectFace(TextureLayer t, ReadOnlySpan<Vec3> points, Vec3 normal, Span<Vec2> result) =>
        ProjectFace(t.Projection, t.Axis, t.Center, t.Size, t.WidthWrap, t.HeightWrap, points, normal, result);

    /// <summary>The bounds of the polygons of a surface across some meshes (for Automatic Sizing).</summary>
    public static Box3 SurfaceBounds(IEnumerable<Mesh> meshes, int surface)
    {
        var b = Box3.Empty;
        foreach (var m in meshes)
            foreach (var p in m.Polygons)
                if (p.Surface == surface)
                    foreach (int i in p.Points)
                        if (i >= 0 && i < m.Points.Count) b = b.Include(m.Points[i]);
        return b;
    }
}
