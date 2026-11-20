using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// A baked light map laid on an object's surfaces (<see cref="RayTracer.BakeLighting"/>'s picture, through its UV map),
/// so the object shows its baked lighting without the scene lighting it again: the map is the colour (or, baked
/// without colour, multiplied into it), luminosity 1 and diffuse 0. Baking again first takes that map off a copy
/// (<see cref="Unhook"/>), so an old bake never lights a new one.
/// </summary>
public static class LightMaps
{
    /// <summary>What hooking a map onto a surface did, and what the surface had before.</summary>
    public sealed record Hooked(string Surface, double DiffuseWas, double LuminosityWas);

    /// <summary>The surfaces an object's faces use (in its layer, or all of them), by index into its document's surfaces.</summary>
    public static IReadOnlyList<int> SurfacesOf(ObjectItem item)
    {
        if (item.Document is not { } doc) return [];
        var used = new SortedSet<int>();
        foreach (var p in item.BaseMesh().Polygons)
            if (p.Count >= 3 && p.Surface >= 0 && p.Surface < doc.Surfaces.Count) used.Add(p.Surface);
        return [.. used];
    }

    /// <summary>
    /// Lays the light map at <paramref name="imagePath"/> (a full path) on the surfaces, in place: a UV image layer on
    /// the colour channel through <paramref name="uvMap"/>, replacing the colour (<paramref name="withColor"/>) or
    /// multiplied into it (a light-only bake over the surface's own colour and textures), and the surface made to glow
    /// with it (luminosity 1, diffuse 0). The same map laid on before is replaced, not doubled.
    /// </summary>
    public static List<Hooked> HookUp(Document doc, IEnumerable<int> surfaces, string imagePath, string uvMap, bool withColor)
    {
        string image = TextureImages.RelativePath(imagePath, doc.FilePath);
        var done = new List<Hooked>();
        foreach (int si in surfaces)
        {
            if (si < 0 || si >= doc.Surfaces.Count) continue;
            var s = doc.Surfaces[si];
            bool again = s.Textures.RemoveAll(t => IsMap(t, imagePath, doc)) > 0;
            done.Add(new Hooked(s.Name, again ? 1 : s.Diffuse, again ? 0 : s.Luminosity));
            s.Textures.Add(new TextureLayer
            {
                Channel = TextureChannel.Color, Image = image, Projection = TextureProjection.UV, UVMap = uvMap,
                Blend = withColor ? TextureBlend.Normal : TextureBlend.Multiply,
            });
            s.Diffuse = 0;
            s.Luminosity = 1;
            s.Raw = null;
        }
        return done;
    }

    /// <summary>
    /// Takes the light map at <paramref name="imagePath"/> off the document's surfaces, in place, so they're lit again
    /// for a new bake: its layers go and, where a surface still glows as a hooked one does (luminosity 1, diffuse 0),
    /// it's back at diffuse 1 and luminosity 0. Returns whether there was one.
    /// </summary>
    public static bool Unhook(Document doc, string imagePath)
    {
        bool any = false;
        foreach (var s in doc.Surfaces)
        {
            if (s.Textures.RemoveAll(t => IsMap(t, imagePath, doc)) == 0) continue;
            any = true;
            if (s.Diffuse == 0 && s.Luminosity == 1) (s.Diffuse, s.Luminosity) = (1, 0);
            s.Raw = null;
        }
        return any;
    }

    private static bool IsMap(TextureLayer t, string imagePath, Document doc)
    {
        if (!t.IsImage || t.Channel != TextureChannel.Color || t.Projection != TextureProjection.UV || t.Image.Length == 0) return false;
        string? full = TextureImages.Resolve(t.Image, doc.ImageBase);
        return string.Equals(full ?? t.Image, Path.GetFullPath(imagePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
