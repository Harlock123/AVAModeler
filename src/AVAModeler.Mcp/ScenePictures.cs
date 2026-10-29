using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Rendering;
using AVAModeler.Scene;
using SceneModel = AVAModeler.Scene.Scene;

namespace AVAModeler.Mcp;

/// <summary>What render_scene_view looks through: the scene's camera, a free perspective, or a flat view.</summary>
public enum SceneViewKind { Camera, Perspective, Top, Front, Side }

/// <summary>An item's place in a picture of the scene, in pixels from the top left.</summary>
public readonly record struct ItemMark(string Name, string Kind, double X, double Y);

/// <summary>
/// Pictures of a Layout scene for an agent: Layout's quick software view (the items' little drawings included) and the
/// ray tracer's frames. Both work on a <see cref="SceneModel.Copy"/> of the scene whose documents are copies too, so
/// they run away from the thread that owns the scene.
/// </summary>
public static class ScenePictures
{
    /// <summary>A copy of a scene to draw on another thread: nothing it holds is shared with the original.</summary>
    public static SceneModel Copy(SceneModel scene) => scene.Copy(d => d.Copy());

    /// <summary>
    /// The scene at a frame as Layout's view draws it: through its current camera (heading and pitch; bank isn't
    /// shown), or a perspective view turned by <paramref name="heading"/> and <paramref name="pitch"/>, or the top,
    /// front or side view, each framing everything in the scene. Also says where each item landed in the picture.
    /// </summary>
    public static (byte[] Png, List<ItemMark> Marks) View(SceneModel scene, double frame, SceneViewKind kind, int width, int height, bool shaded, double heading = 35, double pitch = 25)
    {
        var sf = scene.Evaluate(frame);
        var view = new ViewCamera(kind switch
        {
            SceneViewKind.Top => ViewKind.Top,
            SceneViewKind.Front => ViewKind.Front,
            SceneViewKind.Side => ViewKind.Side,
            _ => ViewKind.Perspective,
        }) { Heading = heading, Pitch = Math.Clamp(pitch, -89.9, 89.9) };
        view.Resize(width, height);
        if (kind == SceneViewKind.Camera)
            SceneDrawing.LookThrough(view, scene.CurrentCamera ?? throw new InvalidOperationException("The scene has no camera to look through. Add one with add_camera, or use view perspective."), frame);
        else
        {
            var box = SceneDrawing.Bounds(sf);
            view.Fit(box.IsEmpty ? new Box3(-Vec3.One, Vec3.One) : box.Expanded(Math.Max(box.Extent * 0.05, 0.25)));
        }
        using var renderer = new SoftwareRenderer();
        renderer.Render(SceneDrawing.Build(sf, scene.Items, shaded), view);
        var marks = new List<ItemMark>();
        foreach (var item in scene.Items)
        {
            var at = SceneCensus.WorldPoint(sf, item);
            var p = view.Project(at);
            if (p.Visible && p.X >= 0 && p.Y >= 0 && p.X < width && p.Y < height) marks.Add(new ItemMark(item.Name, item.Kind.ToLowerInvariant(), Math.Round(p.X), Math.Round(p.Y)));
        }
        return (Png.Encode(width, height, Rgba(renderer.Pixels)), marks);
    }

    /// <summary>The software renderer's ARGB pixels as RGBA bytes.</summary>
    private static byte[] Rgba(uint[] pixels)
    {
        var rgba = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            rgba[i * 4] = (byte)(p >> 16);
            rgba[i * 4 + 1] = (byte)(p >> 8);
            rgba[i * 4 + 2] = (byte)p;
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    /// <summary>A ray-traced frame of a scene (a copy), through one of its cameras.</summary>
    public static RenderImage Render(SceneModel scene, double frame, CameraItem camera, int width, int height, int samples, bool shadows, bool reflections, bool transparency, CancellationToken cancel) =>
        RayTracer.Render(scene, frame, new RenderSettings
        {
            Width = width, Height = height, Camera = camera, SamplesPerPixel = samples,
            Shadows = shadows, Reflections = reflections, Transparency = transparency,
        }, null, cancel);

    /// <summary>Frames side by side, scaled down to <paramref name="cellWidth"/> pixels across, up to eight a row: a contact sheet.</summary>
    public static byte[] ContactSheet(IReadOnlyList<RenderImage> frames, int cellWidth)
    {
        int columns = Math.Min(8, frames.Count), rows = (frames.Count + columns - 1) / columns;
        var first = frames[0];
        int cw = Math.Min(cellWidth, first.Width), ch = Math.Max(1, (int)Math.Round((double)cw * first.Height / first.Width));
        const int gap = 2;
        int width = columns * cw + (columns + 1) * gap, height = rows * ch + (rows + 1) * gap;
        var rgba = new byte[width * height * 4];
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        for (int n = 0; n < frames.Count; n++)
        {
            var img = frames[n];
            int ox = gap + n % columns * (cw + gap), oy = gap + n / columns * (ch + gap);
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    // The average of the source pixels this one covers.
                    int x0 = x * img.Width / cw, x1 = Math.Max(x0 + 1, (x + 1) * img.Width / cw);
                    int y0 = y * img.Height / ch, y1 = Math.Max(y0 + 1, (y + 1) * img.Height / ch);
                    int r = 0, g = 0, b = 0, count = 0;
                    for (int sy = y0; sy < y1; sy++)
                        for (int sx = x0; sx < x1; sx++)
                        {
                            var (pr, pg, pb, _) = img.Pixel(sx, sy);
                            r += pr; g += pg; b += pb; count++;
                        }
                    int o = ((oy + y) * width + ox + x) * 4;
                    rgba[o] = (byte)(r / count);
                    rgba[o + 1] = (byte)(g / count);
                    rgba[o + 2] = (byte)(b / count);
                }
        }
        return Png.Encode(width, height, rgba);
    }
}
