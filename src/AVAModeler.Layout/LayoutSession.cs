using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>
/// Layout's state apart from its controls: the scene, the frame, the chosen item, and the object being modeled
/// (which Layout shares live with the Modeler, in place of classic LightWave's link between the two programs). The
/// Layout workspace shows it; AI agents (the MCP server) work on it too, headless or in the open app.
/// </summary>
public sealed class LayoutSession
{
    private int _savedVersion;

    public Scene.Scene Scene { get; private set; } = AVAModeler.Scene.Scene.CreateDefault();
    public double Frame { get; private set; } = 1;
    public SceneItem? Selected { get; private set; }
    /// <summary>Setting an item's position, rotation or scale makes a key at the frame (otherwise it changes the key there or the constant).</summary>
    public bool AutoKey { get; set; } = true;
    public List<string> Warnings { get; } = [];
    public string Status { get; private set; } = "";
    public string? ScenePath { get; private set; }

    /// <summary>Whether the scene has changed since it was made, loaded or saved.</summary>
    public bool Modified => Scene.Version != _savedVersion;

    public event Action? Changed;
    public event Action? SceneReplaced;
    /// <summary>
    /// Raised when something other than Layout's own controls (an AI agent, in the live app) has changed the scene, so
    /// the item list and the chosen item's settings are shown afresh.
    /// </summary>
    public event Action? ChangedElsewhere;

    public LayoutSession()
    {
        Hook();
        _savedVersion = Scene.Version;
    }

    private void Hook() => Scene.Changed += () => Changed?.Invoke();

    public void Replace(Scene.Scene scene, string? path = null)
    {
        Scene = scene;
        ScenePath = path;
        Selected = scene.Items.FirstOrDefault();
        Frame = scene.FirstFrame;
        _savedVersion = scene.Version;
        Hook();
        SceneReplaced?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Says the scene was changed from outside Layout's controls (see <see cref="ChangedElsewhere"/>).</summary>
    public void NotifyChangedElsewhere() => ChangedElsewhere?.Invoke();

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
        var existing = Scene.Objects.FirstOrDefault(o => ReferenceEquals(o.Document, document) && o.Layer == null);
        if (existing != null) return existing;
        var name = document.FilePath != null ? Path.GetFileNameWithoutExtension(document.FilePath) : "Modeler object";
        var item = Scene.AddObject(name, document);
        item.SourcePath = document.FilePath;
        Selected = item;
        Changed?.Invoke();
        return item;
    }

    /// <summary>Loads an object file into the scene, through <paramref name="load"/> if given (else the built-in formats).</summary>
    public ObjectItem LoadObject(string path, Func<string, Document>? load = null)
    {
        var doc = (load ?? ObjectFiles.Load)(path);
        var item = Scene.AddObject(Path.GetFileNameWithoutExtension(path), doc);
        item.SourcePath = path;
        Selected = item;
        Changed?.Invoke();
        return item;
    }

    /// <summary>
    /// Loads a scene: classic .lws or the native .avascene. Its objects come through <paramref name="load"/> if given
    /// (null for one that can't be had: a placeholder stands in), else the built-in formats. The scene is only replaced
    /// once the file has been read.
    /// </summary>
    public void LoadScene(string path, Func<string, Document?>? load = null)
    {
        load ??= p =>
        {
            try { return ObjectFiles.Load(p); }
            catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException) { return null; }
        };
        var result = path.EndsWith(SceneJson.Extension, StringComparison.OrdinalIgnoreCase)
            ? SceneJson.ReadFile(path, load)
            : LwsReader.ReadFile(path, load);
        Warnings.Clear();
        Warnings.AddRange(result.Warnings);
        Replace(result.Scene, path);
        SetStatus($"Loaded {Path.GetFileName(path)}" + (result.Warnings.Count > 0 ? $" with {result.Warnings.Count} warnings." : "."));
    }

    /// <summary>
    /// Saves the scene: LightWave .lws (objects by their files) or the native .avascene, with the objects' meshes in
    /// it unless <paramref name="embedMeshes"/> is false (then objects that have a file are referenced by it). Returns
    /// what the format couldn't keep (also in <see cref="Warnings"/>).
    /// </summary>
    public IReadOnlyList<string> SaveScene(string path, bool embedMeshes = true)
    {
        var warnings = new List<string>();
        if (path.EndsWith(".lws", StringComparison.OrdinalIgnoreCase)) LwsWriter.WriteFile(Scene, path, warnings);
        else SceneJson.WriteFile(Scene, path, embedMeshes);
        Warnings.Clear();
        Warnings.AddRange(warnings);
        ScenePath = path;
        _savedVersion = Scene.Version;
        SetStatus($"Saved {Path.GetFileName(path)}.");
        return warnings;
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

    /// <summary>Makes sure the chosen item is still in the scene (after the scene was put back as it was, say).</summary>
    public void Revalidate()
    {
        if (Selected != null && Selected.Scene != Scene) Selected = Scene.Items.FirstOrDefault();
        Changed?.Invoke();
    }

    // ───────────────────────────── drawing ─────────────────────────────

    /// <summary>
    /// What a view draws at the current frame: every visible object's deformed, placed mesh as a layer (the chosen
    /// one's polygons highlighted), and lines for lights, cameras, nulls and bones.
    /// </summary>
    public ViewScene BuildScene(SceneFrame frame, bool shaded) => SceneDrawing.Build(frame, Scene.Items, shaded, Selected);

    /// <summary>A note for each texture image the scene's objects use that can't be had (a checker stands in for it).</summary>
    public List<string> ImageProblems() => Scene.Objects.Where(o => !o.IsPlaceholder)
        .SelectMany(o => TextureImages.Problems(o.Surfaces, ImageBase(o)).Select(p => $"{o.Name}, {p}")).ToList();

    /// <summary>The folder an object's relative image paths start from: its file's, or its document's.</summary>
    public static string? ImageBase(ObjectItem o) => SceneDrawing.ImageBase(o);
}
