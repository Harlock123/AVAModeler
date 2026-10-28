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
}

/// <summary>A line in world space a tool wants drawn (handles, the action centre, a volume's box).</summary>
public readonly record struct OverlayLine(Vec3 A, Vec3 B, uint Color);

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
