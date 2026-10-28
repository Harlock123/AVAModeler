using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Scene;
using SceneModel = AVAModeler.Scene.Scene;

namespace AVAModeler.Mcp;

/// <summary>How <see cref="Pictures.View"/> draws the perspective view.</summary>
public enum ViewMode { Wireframe, Shaded, Textured }

/// <summary>
/// Pictures of a document for an agent to look at, as PNGs: one of the Modeler's four views through the software
/// renderer, or a ray-traced still. Both work on a copy of the document (see <see cref="Document.Copy"/>), so they can
/// run away from the thread that owns it.
/// </summary>
public static class Pictures
{
    /// <summary>
    /// One of the Modeler's views, framed to fit the visible layers, drawn as the app draws it: the grid, the
    /// background layers dimmed, the foreground in black wire, the selection in yellow. Only the perspective view
    /// shades; the flat views are always wireframe. For the perspective view, <paramref name="heading"/> turns the
    /// camera round the Y axis (0 looks along +Z, as the Front view does; 90 looks along +X) and
    /// <paramref name="pitch"/> tips it down (degrees).
    /// </summary>
    public static byte[] View(Document doc, ViewKind kind, int width, int height, ViewMode mode, double heading = 35, double pitch = 25)
    {
        var camera = new ViewCamera(kind) { Heading = heading, Pitch = Math.Clamp(pitch, -89.9, 89.9) };
        camera.Resize(width, height);
        camera.Fit(VisibleBounds(doc));
        var scene = ViewScene.FromDocument(doc, mode == ViewMode.Wireframe ? DisplayStyle.Wireframe : DisplayStyle.ShadedWireframe);
        scene.ShowTextures = mode == ViewMode.Textured;
        using var renderer = new SoftwareRenderer();
        renderer.Render(scene, camera);
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < renderer.Pixels.Length; i++)
        {
            uint p = renderer.Pixels[i];
            rgba[i * 4] = (byte)(p >> 16);
            rgba[i * 4 + 1] = (byte)(p >> 8);
            rgba[i * 4 + 2] = (byte)p;
            rgba[i * 4 + 3] = 255;
        }
        return Png.Encode(width, height, rgba);
    }

    /// <summary>
    /// A ray-traced still of the visible layers, as Layout would render it: a camera at <paramref name="heading"/> and
    /// <paramref name="pitch"/> (as for the perspective view) far enough back to frame the object, a key light over the
    /// camera's left shoulder casting shadows, a soft fill light, and a gradient backdrop. Every surface setting and
    /// texture is used. <paramref name="samples"/> rays per pixel smooth the edges.
    /// </summary>
    public static byte[] RayTrace(Document doc, int width, int height, int samples = 4, double heading = 35, double pitch = 25, CancellationToken cancel = default)
    {
        // Only what the person would see: layers that are off are left out.
        foreach (var layer in doc.Layers.Where(l => l.State == LayerState.Off)) layer.Mesh = new Mesh();
        var bounds = VisibleBounds(doc);
        var scene = new SceneModel { AmbientIntensity = 0.2, GradientBackground = true, GradientTop = new Vec3(0.32, 0.38, 0.5), GradientBottom = new Vec3(0.12, 0.12, 0.14) };
        scene.AddObject("Object", doc);
        pitch = Math.Clamp(pitch, -89, 89);
        var camera = scene.AddCamera("Camera");
        camera.Width = width;
        camera.Height = height;
        var aim = new Vec3(heading, pitch, 0);
        var forward = Rotations.Forward(aim);
        double radius = Math.Max(bounds.Size.Length / 2, 1e-3);
        double tanV = 1 / CameraItem.DefaultZoom, tanH = tanV * width / height;
        double halfFov = Math.Atan(Math.Min(tanV, tanH));
        double distance = radius / Math.Sin(halfFov) * 1.05;
        camera.SetPosition(0, bounds.Center - forward * distance);
        camera.SetRotation(0, aim);
        var key = scene.AddLight("Key", LightType.Distant);
        key.SetRotation(0, new Vec3(heading + 35, Math.Clamp(pitch + 25, 30, 75), 0));
        var fill = scene.AddLight("Fill", LightType.Distant);
        fill.SetRotation(0, new Vec3(heading - 70, 15, 0));
        fill.Intensity.SetConstant(0.35);
        fill.Shadows = false;
        var image = RayTracer.Render(scene, 0, new RenderSettings { Width = width, Height = height, Camera = camera, SamplesPerPixel = Math.Max(1, samples) }, null, cancel);
        return image.ToPng(alpha: false);
    }

    /// <summary>The bounds of the foreground and background layers, or a unit box round the origin when they're empty.</summary>
    public static Box3 VisibleBounds(Document doc)
    {
        var b = doc.Bounds();
        return b.IsEmpty ? new Box3(-Vec3.One, Vec3.One) : b;
    }
}
