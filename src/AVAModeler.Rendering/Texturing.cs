using AVAModeler.Core;

namespace AVAModeler.Rendering;

/// <summary>
/// A surface's colour textures made ready to draw: its colour, and up to <see cref="Texturing.MaxLayers"/> image
/// layers with their images (placeholders for missing ones).
/// </summary>
public sealed class TexturedSurface
{
    public required Vec3 Color { get; init; }
    public required IReadOnlyList<(TextureLayer Layer, TextureImage Image)> Layers { get; init; }
}

/// <summary>
/// What a textured face carries to each pixel, worked out at its corners and blended across it (in perspective):
/// the lighting as a multiplier and an offset (so the textured colour can go under it), each layer's image
/// coordinates, and whether each layer reaches that corner (a UV layer doesn't where the face isn't mapped).
/// </summary>
public struct TexturedCorner
{
    public double Mul;
    public Vec3 Add;
    public Vec2 Uv0, Uv1, Uv2, Uv3;
    public double On0, On1, On2, On3;

    public readonly Vec2 Uv(int i) => i switch { 0 => Uv0, 1 => Uv1, 2 => Uv2, _ => Uv3 };
    public readonly double On(int i) => i switch { 0 => On0, 1 => On1, 2 => On2, _ => On3 };

    public void Set(int i, Vec2 uv, double on)
    {
        switch (i)
        {
            case 0: Uv0 = uv; On0 = on; break;
            case 1: Uv1 = uv; On1 = on; break;
            case 2: Uv2 = uv; On2 = on; break;
            default: Uv3 = uv; On3 = on; break;
        }
    }

    /// <summary>Three corners blended by weights (that sum to 1).</summary>
    public static TexturedCorner Blend(in TexturedCorner a, in TexturedCorner b, in TexturedCorner c, double wa, double wb, double wc) => new()
    {
        Mul = a.Mul * wa + b.Mul * wb + c.Mul * wc,
        Add = a.Add * wa + b.Add * wb + c.Add * wc,
        Uv0 = a.Uv0 * wa + b.Uv0 * wb + c.Uv0 * wc,
        Uv1 = a.Uv1 * wa + b.Uv1 * wb + c.Uv1 * wc,
        Uv2 = a.Uv2 * wa + b.Uv2 * wb + c.Uv2 * wc,
        Uv3 = a.Uv3 * wa + b.Uv3 * wb + c.Uv3 * wc,
        On0 = a.On0 * wa + b.On0 * wb + c.On0 * wc,
        On1 = a.On1 * wa + b.On1 * wb + c.On1 * wc,
        On2 = a.On2 * wa + b.On2 * wb + c.On2 * wc,
        On3 = a.On3 * wa + b.On3 * wb + c.On3 * wc,
    };
}

/// <summary>
/// Image textures in the views, shared by both renderers so they draw the same picture: which layers a surface
/// shows, each face corner's lighting and image coordinates, and the colour of a pixel from them (the OpenGL
/// renderer's fragment shader does the very same sums). The views show colour textures; the ray tracer does the rest.
/// </summary>
public static class Texturing
{
    /// <summary>The most colour layers a surface shows in a view.</summary>
    public const int MaxLayers = 4;

    /// <summary>A surface's colour image layers and their images, or null if it has none to show.</summary>
    public static TexturedSurface? Prepare(Surface? surface, string? imageBase)
    {
        if (surface == null || surface.Textures.Count == 0) return null;
        var layers = new List<(TextureLayer, TextureImage)>();
        foreach (var t in surface.Textures)
        {
            if (t.Channel != TextureChannel.Color || !t.IsImage || t.Opacity <= 0) continue;
            layers.Add((t, TextureImages.Load(t.Image, imageBase)));
            if (layers.Count == MaxLayers) break;
        }
        return layers.Count == 0 ? null : new TexturedSurface { Color = surface.Color.ToVec(), Layers = layers };
    }

    /// <summary>
    /// The corners of one triangle of a face: lighting from <paramref name="light"/> (see <see cref="Shading.FaceLight"/>)
    /// and each layer's image coordinates, from the face's points in <paramref name="space"/> (object coordinates,
    /// index for index with the drawn mesh; the drawn mesh's own for a layer in world coordinates) or its UV map.
    /// Corners across a wrapping projection's seam are brought to the same side.
    /// </summary>
    public static void Corners(TexturedSurface ts, Mesh mesh, Mesh space, int polygon, int a, int b, int c,
        Func<int, (double Mul, Vec3 Add)> light, Span<TexturedCorner> corners)
    {
        var poly = mesh.Polygons[polygon];
        Span<int> pts = [a, b, c];
        for (int k = 0; k < 3; k++)
        {
            var (mul, add) = light(pts[k]);
            corners[k] = new TexturedCorner { Mul = mul, Add = add };
        }
        var normal = space.Normal(poly);
        Span<Vec2> uv = stackalloc Vec2[3];
        Span<Vec3> at = stackalloc Vec3[3];
        for (int i = 0; i < ts.Layers.Count; i++)
        {
            var t = ts.Layers[i].Layer;
            bool on = true;
            if (t.Projection == TextureProjection.UV)
            {
                for (int k = 0; k < 3; k++)
                    if (mesh.TryGetUV(t.UVMap, poly, pts[k], out var u)) uv[k] = u;
                    else on = false;
            }
            else
            {
                int n = space.Points.Count;
                bool world = t.WorldCoordinates || pts[0] >= n || pts[1] >= n || pts[2] >= n;
                for (int k = 0; k < 3; k++) at[k] = world ? mesh.Points[pts[k]] : space.Points[pts[k]];
                TextureMapping.ProjectFace(t, at, world ? mesh.Normal(poly) : normal, uv);
            }
            for (int k = 0; k < 3; k++) corners[k].Set(i, on ? uv[k] : Vec2.Zero, on ? 1 : 0);
        }
    }

    /// <summary>
    /// A pixel's colour: the surface's colour with each layer laid over it (normal, additive or multiply, at the
    /// layer's opacity times the image's alpha and how much of the layer reaches here), then lit and clamped.
    /// </summary>
    public static Vec3 Fragment(TexturedSurface ts, in TexturedCorner at)
    {
        var col = ts.Color;
        for (int i = 0; i < ts.Layers.Count; i++)
        {
            var (t, img) = ts.Layers[i];
            double on = at.On(i);
            if (on <= 0) continue;
            var uv = at.Uv(i);
            var (c, alpha) = img.Sample(uv.X, uv.Y, t.WidthTiling, t.HeightTiling, t.PixelBlending);
            if (t.Negative) c = Vec3.One - c;
            double w = Math.Clamp(t.Opacity, 0, 1) * alpha * Math.Min(on, 1);
            col = TextureMapping.Blend(col, c, w, t.Blend);
        }
        var lit = col * at.Mul + at.Add;
        return new Vec3(Math.Clamp(lit.X, 0, 1), Math.Clamp(lit.Y, 0, 1), Math.Clamp(lit.Z, 0, 1));
    }

    /// <summary>
    /// The image behind the UV view: a surface showing it on the 0–1 square, dimmed a little so the map's lines
    /// stand out on it.
    /// </summary>
    public static TexturedSurface Backdrop(TextureImage image) => new()
    {
        Color = Vec3.Zero,
        Layers = [(new TextureLayer { WidthTiling = TextureTiling.Edge, HeightTiling = TextureTiling.Edge }, image)],
    };

    /// <summary>The backdrop's lighting: dimmed to 80%.</summary>
    public const double BackdropBrightness = 0.8;
}
