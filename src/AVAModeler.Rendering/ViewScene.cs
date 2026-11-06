using AVAModeler.Core;

namespace AVAModeler.Rendering;

/// <summary>How a view draws polygons: as lines, filled and lit, or both.</summary>
public enum DisplayStyle { Wireframe, Shaded, ShadedWireframe }

/// <summary>One layer as a view draws it: its mesh, whether it's in the background, and what's selected in it.</summary>
public sealed class ViewLayer
{
    public required Mesh Mesh;
    public bool Background;
    public IReadOnlySet<int>? SelectedPoints;
    public IReadOnlySet<int>? SelectedPolygons;
    public IReadOnlySet<Edge>? SelectedEdges;
    /// <summary>A smoothed version of the mesh (the MetaNURBS surface) to draw in place of its faces, with the cage as lines.</summary>
    public Mesh? Subdivided;
    /// <summary>
    /// The mesh in its own (object) coordinates, point for point with <see cref="Mesh"/>, where textures are projected
    /// from; null when the mesh is drawn where it is (as in the Modeler).
    /// </summary>
    public Mesh? TextureSpace;
    /// <summary>The folder this layer's relative image paths start from; null for the scene's.</summary>
    public string? ImageBase;
    /// <summary>
    /// How far the layer has faded away (a Layout object's dissolve), 0 (drawn as ever, the default) to 1 (not drawn):
    /// its faces and wire are drawn with screen-door transparency, that share of its pixels left out by an ordered
    /// dither (<see cref="Dither"/>), so the depth buffer stays as it is and nothing needs sorting.
    /// </summary>
    public double Dissolve;
}

/// <summary>
/// Screen-door transparency, the same in both renderers: an 8 × 8 ordered (Bayer) dither of the screen's pixels. A pixel
/// of a layer dissolved by d is left out when its threshold, (index + 0.5) / 64, is d or less, so d of every 8 × 8 block
/// goes (to the nearest 64th). Pixels count from the picture's top left, as the software renderer's do; the GL shaders
/// turn <c>gl_FragCoord</c> round to match.
/// </summary>
public static class Dither
{
    /// <summary>The 8 × 8 Bayer matrix, row by row (each 0 to 63 once).</summary>
    public static readonly int[] Bayer =
    [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    /// <summary>A pixel's threshold, 0 to 1.</summary>
    public static float Threshold(int x, int y) => (Bayer[(y & 7) * 8 + (x & 7)] + 0.5f) / 64f;

    /// <summary>Whether a pixel (from the top left) of a layer dissolved by <paramref name="dissolve"/> is drawn.</summary>
    public static bool Keeps(int x, int y, double dissolve) => dissolve <= 0 || Threshold(x, y) > (float)dissolve;

    /// <summary>The matrix as a GLSL array's contents ("0, 32, 8, …").</summary>
    public static string Glsl => string.Join(", ", Bayer);
}

/// <summary>A line in world space a tool wants drawn (handles, the action centre, a volume's box).</summary>
public readonly record struct OverlayLine(Vec3 A, Vec3 B, uint Color)
{
    /// <summary>The twelve edges of a box.</summary>
    public static IEnumerable<OverlayLine> Box(Box3 b, uint color)
    {
        var c = new Vec3[8];
        for (int i = 0; i < 8; i++) c[i] = new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
        int[][] edges = [[0, 1], [2, 3], [4, 5], [6, 7], [0, 2], [1, 3], [4, 6], [5, 7], [0, 4], [1, 5], [2, 6], [3, 7]];
        return edges.Select(e => new OverlayLine(c[e[0]], c[e[1]], color));
    }
}

/// <summary>Everything a view draws: the layers, the surfaces they use, and how.</summary>
public sealed class ViewScene
{
    public List<ViewLayer> Layers = [];
    public IReadOnlyList<Surface> Surfaces = [];
    public DisplayStyle Style = DisplayStyle.Wireframe;
    /// <summary>Show every point as a dot (Points mode), not just the selected ones.</summary>
    public bool ShowPoints;
    /// <summary>Tint faces seen from behind, so flipped polygons stand out.</summary>
    public bool BackfaceTint = true;
    public bool ShowGrid = true;
    /// <summary>Show image textures on shaded faces.</summary>
    public bool ShowTextures = true;
    /// <summary>The folder relative image paths start from (the object file's).</summary>
    public string? ImageBase;
    /// <summary>An image drawn on the 0–1 square of the XY plane, behind everything (the UV view's texture).</summary>
    public TextureImage? Backdrop;
    /// <summary>The reference images behind the flat views (blueprints), drawn under the grid; relative paths start from <see cref="ImageBase"/>.</summary>
    public BackdropSet? Backdrops;
    public List<OverlayLine> Overlay = [];

    public int Version;

    /// <summary>
    /// A document as the Modeler's views draw it: its foreground and background layers (layers that are off or empty
    /// are left out), with the foreground's selection in the current mode. <paramref name="smoothed"/> gives a layer's
    /// MetaNURBS surface, if any.
    /// </summary>
    public static ViewScene FromDocument(Document document, DisplayStyle style, bool backfaceTint = true, Func<int, Mesh?>? smoothed = null)
    {
        var sel = document.Selection;
        var scene = new ViewScene
        {
            Surfaces = document.Surfaces,
            Style = style,
            ShowPoints = sel.Mode == SelectionMode.Points,
            BackfaceTint = backfaceTint,
            ImageBase = document.ImageBase,
            Backdrops = document.Backdrops,
            Version = document.Version,
        };
        for (int i = 0; i < document.Layers.Count; i++)
        {
            var layer = document.Layers[i];
            if (layer.State == LayerState.Off || layer.Mesh.IsEmpty) continue;
            bool fg = layer.State == LayerState.Foreground;
            scene.Layers.Add(new ViewLayer
            {
                Mesh = layer.Mesh,
                Background = !fg,
                SelectedPoints = fg && sel.Mode == SelectionMode.Points && sel.Points.TryGetValue(i, out var p) ? p : null,
                SelectedPolygons = fg && sel.Mode == SelectionMode.Polygons && sel.Polygons.TryGetValue(i, out var q) ? q : null,
                SelectedEdges = fg && sel.Mode == SelectionMode.Edges && sel.Edges.TryGetValue(i, out var e) ? e : null,
                Subdivided = fg ? smoothed?.Invoke(i) : null,
            });
        }
        return scene;
    }
}

/// <summary>The classic Modeler colours (as 0xAARRGGBB), with a few for the modern touches.</summary>
public static class Palette
{
    public const uint Background = 0xFF707070;
    public const uint PerspectiveBackground = 0xFF5E6672;
    public const uint Grid = 0xFF7C7C7C;
    public const uint GridMajor = 0xFF888888;
    public const uint Axis = 0xFF545454;
    public const uint Wire = 0xFF101010;
    public const uint BackgroundWire = 0xFF9A9A9A;
    public const uint Selected = 0xFFFFE34A;
    public const uint SelectedFill = 0xFFFFE34A;
    public const uint Point = 0xFF000000;
    public const uint Cage = 0xFF2850B0;
    public const uint Backface = 0xFF6A78B8;
    public const uint Volume = 0xFFFF7020;
    public const uint ActionCenter = 0xFF20D0FF;
    /// <summary>The edge of the UV view's 0–1 square.</summary>
    public const uint UVSquare = 0xFFE8E8E8;
}
