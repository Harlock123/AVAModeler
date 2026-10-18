using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVAModeler.Layout.UI;

/// <summary>
/// Layout's state apart from its controls: the scene, the frame, the chosen item, and the object being modeled
/// (which Layout shares live with the Modeler, in place of classic LightWave's link between the two programs).
/// </summary>
public sealed class LayoutSession
{
    public Scene.Scene Scene { get; private set; } = AVAModeler.Scene.Scene.CreateDefault();
    public double Frame { get; private set; } = 1;
    public SceneItem? Selected { get; private set; }
    /// <summary>Setting an item's position, rotation or scale makes a key at the frame (otherwise it changes the key there or the constant).</summary>
    public bool AutoKey { get; set; } = true;
    public List<string> Warnings { get; } = [];
    public string Status { get; private set; } = "";
    public string? ScenePath { get; private set; }

    public event Action? Changed;
    public event Action? SceneReplaced;

    public LayoutSession() => Hook();

    private void Hook() => Scene.Changed += () => Changed?.Invoke();

    public void Replace(Scene.Scene scene, string? path = null)
    {
        Scene = scene;
        ScenePath = path;
        Selected = scene.Items.FirstOrDefault();
        Frame = scene.FirstFrame;
        Hook();
        SceneReplaced?.Invoke();
        Changed?.Invoke();
    }

    public void SetStatus(string text)
    {
        Status = text;
        Changed?.Invoke();
    }

    public void SetFrame(double frame)
    {
        Frame = Math.Clamp(frame, Scene.FirstFrame, Math.Max(Scene.FirstFrame, Scene.LastFrame));
        Changed?.Invoke();
    }

    public void Select(SceneItem? item)
    {
        Selected = item;
        Changed?.Invoke();
    }

    /// <summary>
    /// Puts the Modeler's object in the scene (once): an object item that shows whatever is being modeled, as it
    /// changes.
    /// </summary>
    public ObjectItem ShowModelerObject(Document document)
    {
        var existing = Scene.Objects.FirstOrDefault(o => ReferenceEquals(o.Document, document));
        if (existing != null) return existing;
        var name = document.FilePath != null ? Path.GetFileNameWithoutExtension(document.FilePath) : "Modeler object";
        var item = Scene.AddObject(name, document);
        item.SourcePath = document.FilePath;
        Selected = item;
        Changed?.Invoke();
        return item;
    }

    /// <summary>Loads an object file into the scene.</summary>
    public ObjectItem LoadObject(string path)
    {
        var doc = ObjectFiles.Load(path);
        var item = Scene.AddObject(Path.GetFileNameWithoutExtension(path), doc);
        item.SourcePath = path;
        Selected = item;
        Changed?.Invoke();
        return item;
    }

    /// <summary>Loads a scene: classic .lws or the native .avascene.</summary>
    public void LoadScene(string path)
    {
        Document? Load(string p)
        {
            try { return ObjectFiles.Load(p); }
            catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException) { return null; }
        }
        var result = path.EndsWith(SceneJson.Extension, StringComparison.OrdinalIgnoreCase)
            ? SceneJson.ReadFile(path, Load)
            : LwsReader.ReadFile(path, Load);
        Warnings.Clear();
        Warnings.AddRange(result.Warnings);
        Replace(result.Scene, path);
        SetStatus($"Loaded {Path.GetFileName(path)}" + (result.Warnings.Count > 0 ? $" with {result.Warnings.Count} warnings." : "."));
    }

    public void SaveScene(string path)
    {
        if (path.EndsWith(".lws", StringComparison.OrdinalIgnoreCase))
        {
            var warnings = new List<string>();
            LwsWriter.WriteFile(Scene, path, warnings);
            Warnings.Clear();
            Warnings.AddRange(warnings);
        }
        else SceneJson.WriteFile(Scene, path, embedMeshes: true);
        ScenePath = path;
        SetStatus($"Saved {Path.GetFileName(path)}.");
    }

    // ───────────────────────────── editing items ─────────────────────────────

    /// <summary>Sets a channel at the current frame: a key with auto-key on, otherwise the key here (if any) or the whole channel.</summary>
    public void SetChannel(SceneItem item, Channel channel, double value)
    {
        var env = item[channel];
        if (AutoKey || env.IndexOf(Frame) >= 0 || env.Keys.Count > 1) env.AddKey(Frame, value);
        else env.SetConstant(value);
        Scene.Notify();
    }

    public void SetVector(SceneItem item, int first, Vec3 v)
    {
        SetChannel(item, (Channel)first, v.X);
        SetChannel(item, (Channel)(first + 1), v.Y);
        SetChannel(item, (Channel)(first + 2), v.Z);
    }

    public void CreateKey()
    {
        if (Selected == null) return;
        Selected.CreateKey(Frame);
        SetStatus($"Key made at frame {Frame:0}.");
    }

    public void DeleteKey()
    {
        if (Selected == null) return;
        Selected.DeleteKey(Frame);
        SetStatus($"Key at frame {Frame:0} deleted.");
    }

    public void Remove(SceneItem item)
    {
        Scene.Remove(item);
        if (Selected == item) Selected = Scene.Items.FirstOrDefault();
        Changed?.Invoke();
    }

    // ───────────────────────────── drawing ─────────────────────────────

    /// <summary>
    /// What a view draws at the current frame: every visible object's deformed, placed mesh as a layer (the chosen
    /// one's polygons highlighted), and lines for lights, cameras, nulls and bones.
    /// </summary>
    public ViewScene BuildScene(SceneFrame frame, bool shaded)
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
            var mesh = new Mesh { Points = world.Points };
            foreach (var p in world.Polygons) mesh.Polygons.Add(new Polygon(p.Points, p.Surface + offset));
            mesh.Touch();
            scene.Layers.Add(new ViewLayer { Mesh = mesh });
            // The chosen object is boxed in yellow, so its own colours still show.
            if (o.Item == Selected && !mesh.IsEmpty)
                scene.Overlay.AddRange(Modeler.UI.ModelerSession.BoxLines(mesh.Bounds().Expanded(mesh.Bounds().Extent * 0.02), Palette.Selected));
        }
        scene.Surfaces = surfaces;
        foreach (var item in Scene.Items)
        {
            if (item is ObjectItem || !frame.World.TryGetValue(item, out var m)) continue;
            uint color = item == Selected ? Palette.Selected : item switch
            {
                LightItem => 0xFFFFF0A0, CameraItem => 0xFF60E0FF, Bone => 0xFFFF8060, _ => 0xFF202020,
            };
            foreach (var (a, b) in Glyph(item)) scene.Overlay.Add(new OverlayLine(m.TransformPoint(a), m.TransformPoint(b), color));
        }
        return scene;
    }

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
