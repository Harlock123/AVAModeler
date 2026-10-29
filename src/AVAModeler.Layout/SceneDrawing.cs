using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>
/// What Layout's views draw, without the views: a scene at a frame as a <see cref="ViewScene"/> for the software
/// renderer (each visible object's deformed, placed mesh, and a little drawing for every light, camera, null and bone),
/// and the view camera that looks through a scene camera. The Layout workspace and the MCP server's pictures share it.
/// </summary>
public static class SceneDrawing
{
    /// <summary>
    /// The scene at a worked-out frame, as a view draws it: every visible object's deformed, placed mesh as a layer
    /// (<paramref name="selected"/> boxed in yellow), and lines for lights, cameras, nulls and bones.
    /// </summary>
    public static ViewScene Build(SceneFrame frame, IEnumerable<SceneItem> items, bool shaded, SceneItem? selected = null)
    {
        var scene = new ViewScene { Style = shaded ? DisplayStyle.ShadedWireframe : DisplayStyle.Wireframe, ShowGrid = true };
        var surfaces = new List<Surface>();
        foreach (var o in frame.Objects)
        {
            if (!o.Item.Visible) continue;
            var world = o.WorldMesh;
            // Each object has its own surfaces: renumber into one list for the view.
            int offset = surfaces.Count;
            surfaces.AddRange(o.Surfaces);
            var mesh = new Mesh { Points = world.Points, UVMaps = world.UVMaps };
            foreach (var p in world.Polygons) mesh.Polygons.Add(new Polygon(p.Points, p.Surface + offset) { CornerUVs = p.CornerUVs });
            mesh.Touch();
            // Textures are laid on in the object's own coordinates, so they move with it.
            scene.Layers.Add(new ViewLayer { Mesh = mesh, TextureSpace = o.Mesh, ImageBase = ImageBase(o.Item) });
            // The chosen object is boxed in yellow, so its own colours still show.
            if (o.Item == selected && !mesh.IsEmpty)
                scene.Overlay.AddRange(OverlayLine.Box(mesh.Bounds().Expanded(mesh.Bounds().Extent * 0.02), Palette.Selected));
        }
        scene.Surfaces = surfaces;
        foreach (var item in items)
        {
            if (item is ObjectItem || !frame.World.TryGetValue(item, out var m)) continue;
            uint color = item == selected ? Palette.Selected : Color(item);
            foreach (var (a, b) in Glyph(item)) scene.Overlay.Add(new OverlayLine(m.TransformPoint(a), m.TransformPoint(b), color));
        }
        return scene;
    }

    /// <summary>The colour an item's drawing has when it isn't chosen.</summary>
    public static uint Color(SceneItem item) => item switch
    {
        LightItem => 0xFFFFF0A0, CameraItem => 0xFF60E0FF, Bone => 0xFFFF8060, _ => 0xFF202020,
    };

    /// <summary>
    /// Points a perspective view camera through a scene camera at a frame: its position, heading and pitch (bank isn't
    /// shown) and its vertical field of view.
    /// </summary>
    public static void LookThrough(ViewCamera view, CameraItem camera, double frame)
    {
        var world = camera.WorldMatrix(frame);
        var eye = world.TransformPoint(Vec3.Zero);
        var hpb = Rotations.FromMatrix(world);
        view.Heading = hpb.X;
        view.Pitch = hpb.Y;
        view.Distance = 1;
        view.FieldOfView = camera.VerticalFov(frame);
        view.Center = eye + view.Basis().Forward;
    }

    /// <summary>What there is to see at a frame: the objects' placed meshes and every other item's position.</summary>
    public static Box3 Bounds(SceneFrame frame)
    {
        var box = Box3.Empty;
        foreach (var o in frame.Objects) box = box.Include(o.WorldMesh.Bounds());
        foreach (var (item, m) in frame.World) if (item is not ObjectItem) box = box.Include(m.TransformPoint(Vec3.Zero));
        return box;
    }

    /// <summary>The folder an object's relative image paths start from: its file's, or its document's.</summary>
    public static string? ImageBase(ObjectItem o) => TextureImages.BaseOf(o.SourcePath ?? o.Document?.FilePath);

    /// <summary>A little drawing of an item in its own space: a cross for a null, a cone for a light, a box and lens for a camera, a diamond for a bone.</summary>
    public static IEnumerable<(Vec3, Vec3)> Glyph(SceneItem item)
    {
        double s = 0.25;
        switch (item)
        {
            case Bone b:
            {
                double l = b.RestLength, w = l * 0.1;
                var tip = new Vec3(0, 0, l);
                var ring = new[] { new Vec3(w, 0, l * 0.2), new Vec3(0, w, l * 0.2), new Vec3(-w, 0, l * 0.2), new Vec3(0, -w, l * 0.2) };
                for (int i = 0; i < 4; i++)
                {
                    yield return (Vec3.Zero, ring[i]);
                    yield return (ring[i], tip);
                    yield return (ring[i], ring[(i + 1) % 4]);
                }
                break;
            }
            case CameraItem:
            {
                var c = new[] { new Vec3(-s, -s * 0.75, -s), new Vec3(s, -s * 0.75, -s), new Vec3(s, s * 0.75, -s), new Vec3(-s, s * 0.75, -s) };
                for (int i = 0; i < 4; i++)
                {
                    yield return (c[i], c[(i + 1) % 4]);
                    yield return (c[i], c[i] + new Vec3(0, 0, s * 1.4));
                    yield return (Vec3.Zero, new Vec3(c[i].X * 1.4, c[i].Y * 1.4, s * 1.4));
                }
                yield return (Vec3.Zero, new Vec3(0, 0, s * 3));
                break;
            }
            case LightItem l:
            {
                yield return (new Vec3(-s, 0, 0), new Vec3(s, 0, 0));
                yield return (new Vec3(0, -s, 0), new Vec3(0, s, 0));
                yield return (new Vec3(0, 0, -s), new Vec3(0, 0, s));
                if (l.Type != LightType.Point)
                {
                    // The way it shines.
                    yield return (Vec3.Zero, new Vec3(0, 0, s * 4));
                    yield return (new Vec3(0, 0, s * 4), new Vec3(s * 0.4, 0, s * 3.3));
                    yield return (new Vec3(0, 0, s * 4), new Vec3(-s * 0.4, 0, s * 3.3));
                }
                break;
            }
            default:
                yield return (new Vec3(-s, 0, 0), new Vec3(s, 0, 0));
                yield return (new Vec3(0, -s, 0), new Vec3(0, s, 0));
                yield return (new Vec3(0, 0, -s), new Vec3(0, 0, s));
                break;
        }
    }
}
