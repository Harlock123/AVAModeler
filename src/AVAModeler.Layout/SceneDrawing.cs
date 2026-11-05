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
    /// (<paramref name="selected"/> boxed in yellow), and lines for lights, cameras, nulls and bones (with a line from a
    /// bone's tip to its IK goal), and the paths items follow.
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
            // A bone reaching for an IK goal: a line from its tip to the goal.
            if (item is Bone { IKGoal: { } goal } bone && frame.World.TryGetValue(goal, out var g))
                scene.Overlay.Add(new OverlayLine(m.TransformPoint(new Vec3(0, 0, bone.RestLength)), g.TransformPoint(goal.Pivot), IKLink));
        }
        // Paths items follow, as curves through their points.
        foreach (var item in items)
            if (item.Path != null && PathLine(item, frame.Frame) is { } line)
            {
                uint color = item == selected ? Palette.Selected : PathColor;
                for (int i = 1; i < line.Count; i++) scene.Overlay.Add(new OverlayLine(line[i - 1], line[i], color));
            }
        return scene;
    }

    /// <summary>The colour of a path an item follows (the chosen item's path is drawn in the selection's yellow).</summary>
    public const uint PathColor = 0xFFFF70D0;

    /// <summary>The path an item follows at a frame, as points along it in the world for drawing (null if it has none it can use).</summary>
    public static IReadOnlyList<Vec3>? PathLine(SceneItem item, double frame)
    {
        if (item.Path is not { } path || path.Points.Count < path.Fewest || path.Points.Any(p => p.Scene != item.Scene)) return null;
        var curve = new MotionPath(path.Points.Select(p => p.WorldPosition(frame)).ToList(), path.Closed);
        return curve.Polyline(48 * path.Points.Count);
    }

    /// <summary>The colour of the line from a bone's tip to its IK goal.</summary>
    public const uint IKLink = 0xFFFFC030;

    /// <summary>The colour an item's drawing has when it isn't chosen.</summary>
    public static uint Color(SceneItem item) => item switch
    {
        LightItem => 0xFFFFF0A0, CameraItem => 0xFF60E0FF, Bone => 0xFFFF8060, _ => 0xFF202020,
    };

    /// <summary>
    /// Points a perspective view camera through a scene camera at a frame: its position, heading, pitch and bank (so a
    /// banked camera's view tilts as its ray-traced picture does) and its vertical field of view.
    /// </summary>
    public static void LookThrough(ViewCamera view, CameraItem camera, double frame)
    {
        var world = camera.WorldMatrix(frame);
        var eye = world.TransformPoint(Vec3.Zero);
        var hpb = Rotations.FromMatrix(world);
        view.Heading = hpb.X;
        view.Pitch = hpb.Y;
        view.Bank = hpb.Z;
        view.Distance = 1;
        view.FieldOfView = camera.VerticalFov(frame);
        view.Center = eye + view.Basis().Forward;
    }

    /// <summary>
    /// Looks through a light, as classic Layout's Light view did for aiming: from where it is, along its +Z (the way
    /// distant and spot lights shine), a spot's cone wide (twice its cone angle, its soft edge included), else 60°.
    /// </summary>
    public static void LookThrough(ViewCamera view, LightItem light, double frame)
    {
        var world = light.WorldMatrix(frame);
        var eye = world.TransformPoint(light.Pivot);
        var hpb = Rotations.FromMatrix(world);
        view.Heading = hpb.X;
        view.Pitch = hpb.Y;
        view.Bank = hpb.Z;
        view.Distance = 1;
        view.FieldOfView = light.Type == LightType.Spot ? Math.Clamp(2 * (light.ConeAngle + light.EdgeAngle), 5, 170) : 60;
        view.Center = eye + view.Basis().Forward;
    }

    /// <summary>Where an item is in the world at a frame: an object's middle (its placed mesh's bounds' centre), else its pivot.</summary>
    public static Vec3 WorldPoint(SceneFrame sf, SceneItem item)
    {
        if (item is ObjectItem obj && sf.Objects.FirstOrDefault(x => x.Item == obj) is { Mesh.IsEmpty: false } of) return of.WorldMesh.Bounds().Center;
        return sf.WorldOf(item).TransformPoint(item.Pivot);
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

    /// <summary>
    /// A little drawing of an item in its own space: a cross for a null, a cross for a light (with an arrow the way it
    /// shines, an area light's rectangle at its size, and a ring of a soft light's radius), a box and lens for a camera,
    /// a diamond for a bone.
    /// </summary>
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
                if (l.Type == LightType.Area)
                {
                    // Its shape, with a cross from corner to corner.
                    double w = l.AreaWidth / 2, h = l.AreaHeight / 2;
                    var c = new[] { new Vec3(-w, -h, 0), new Vec3(w, -h, 0), new Vec3(w, h, 0), new Vec3(-w, h, 0) };
                    for (int i = 0; i < 4; i++) yield return (c[i], c[(i + 1) % 4]);
                    yield return (c[0], c[2]);
                    yield return (c[1], c[3]);
                }
                else if (l.IsSoft)
                {
                    // Its size: rings round it in its three planes.
                    const int n = 24;
                    for (int i = 0; i < n; i++)
                    {
                        double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n, r = l.Radius;
                        var (c0, s0, c1, s1) = (Math.Cos(a0) * r, Math.Sin(a0) * r, Math.Cos(a1) * r, Math.Sin(a1) * r);
                        yield return (new Vec3(c0, s0, 0), new Vec3(c1, s1, 0));
                        yield return (new Vec3(c0, 0, s0), new Vec3(c1, 0, s1));
                        yield return (new Vec3(0, c0, s0), new Vec3(0, c1, s1));
                    }
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
