using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>
/// Layout's state apart from its controls: the scene, the frame, the chosen item, and the object being modeled
/// (which Layout shares live with the Modeler, in place of classic LightWave's link between the two programs). The
/// Layout workspace shows it; AI agents (the MCP server) work on it too, headless or in the open app.
/// <para>Every change to the scene is an undo step on <see cref="History"/>, which the person and agents share: make
/// changes inside <see cref="Edit"/> (or between <see cref="BeginEdit"/> and <see cref="EndEdit"/> for a drag). The
/// session's own editing methods (<see cref="SetVector"/>, <see cref="CreateKey"/>, <see cref="Remove"/> and the
/// rest) are steps already.</para>
/// </summary>
public sealed class LayoutSession
{
    private int _savedVersion, _savedState;
    private int _editDepth;
    private (SceneSnapshot Before, string Label)? _pending;

    public Scene.Scene Scene { get; private set; } = AVAModeler.Scene.Scene.CreateDefault();
    public double Frame { get; private set; } = 1;
    public SceneItem? Selected { get; private set; }
    /// <summary>Setting an item's position, rotation or scale makes a key at the frame (otherwise it changes the key there or the constant).</summary>
    public bool AutoKey { get; set; } = true;
    public List<string> Warnings { get; } = [];
    public string Status { get; private set; } = "";
    public string? ScenePath { get; private set; }

    /// <summary>Whether the scene has changed since it was made, loaded or saved (undoing back to that counts as unchanged).</summary>
    public bool Modified => Scene.Version != _savedVersion;

    /// <summary>The scene's undo history, shared by Layout's controls and AI agents. A new or loaded scene clears it.</summary>
    public SceneUndo History { get; } = new();

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
        MarkSaved();
    }

    private void MarkSaved()
    {
        _savedVersion = Scene.Version;
        _savedState = History.StateOf(Scene);
    }

    private void Hook() => Scene.Changed += () => Changed?.Invoke();

    public void Replace(Scene.Scene scene, string? path = null)
    {
        Scene = scene;
        ScenePath = path;
        Selected = scene.Items.FirstOrDefault();
        Frame = scene.FirstFrame;
        _pending = null;
        History.Clear();
        MarkSaved();
        Hook();
        SceneReplaced?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>
    /// Starts a new scene as Layout does (a camera and a light), with the Modeler's object in it if given. Like loading
    /// one, it clears the undo history.
    /// </summary>
    public void NewScene(Document? modelerDocument = null)
    {
        var scene = AVAModeler.Scene.Scene.CreateDefault();
        var item = modelerDocument != null ? AddModelerObject(scene, modelerDocument) : null;
        Warnings.Clear();
        Replace(scene);
        if (item != null) Select(item);
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
        var item = Edit("Add Modeler Object", () => AddModelerObject(Scene, document));
        Selected = item;
        Changed?.Invoke();
        return item;
    }

    /// <summary>
    /// What switching to Layout does: puts the Modeler's object in the scene unless some of it is there already, whole
    /// or as layers (as an agent or the AI Chat stages it), so the switch never piles the whole object on top of them.
    /// </summary>
    public void OfferModelerObject(Document document)
    {
        if (!Scene.Objects.Any(o => ReferenceEquals(o.Document, document))) ShowModelerObject(document);
    }

    private static ObjectItem AddModelerObject(Scene.Scene scene, Document document)
    {
        var name = document.FilePath != null ? Path.GetFileNameWithoutExtension(document.FilePath) : "Modeler object";
        var item = scene.AddObject(name, document);
        item.SourcePath = document.FilePath;
        return item;
    }

    /// <summary>Loads an object file into the scene, through <paramref name="load"/> if given (else the built-in formats).</summary>
    public ObjectItem LoadObject(string path, Func<string, Document>? load = null)
    {
        var doc = (load ?? ObjectFiles.Load)(path);
        var item = Edit("Add Object", () =>
        {
            var o = Scene.AddObject(Path.GetFileNameWithoutExtension(path), doc);
            o.SourcePath = path;
            return o;
        });
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
        MarkSaved();
        SetStatus($"Saved {Path.GetFileName(path)}.");
        return warnings;
    }

    // ───────────────────────────── undo ─────────────────────────────

    /// <summary>
    /// Makes a change to the scene one undo step, named <paramref name="label"/> ("Move Camera"). If
    /// <paramref name="change"/> throws, the scene is put back as it was, no step is left, and the exception goes on
    /// (all or nothing). Inside another edit, or while a drag's edit is under way, it is part of that one; with
    /// <paramref name="apart"/> (an AI agent's call, which may come in the middle of the person's drag) it is a step of
    /// its own even then: the drag so far becomes a step, and the rest of the drag another. A change that leaves the
    /// scene's <see cref="Scene.Scene.Version"/> as it was is no step. Replacing the scene inside it leaves no step
    /// either (a new scene starts a fresh history).
    /// </summary>
    public T Edit<T>(string label, Func<T> change, bool apart = false)
    {
        if (_editDepth > 0 || _pending != null && !apart) return change();
        var resume = _pending?.Label;
        if (resume != null) EndEdit();
        var scene = Scene;
        var before = scene.Snapshot();
        T result;
        _editDepth++;
        try
        {
            result = change();
        }
        catch
        {
            _editDepth--;
            if (ReferenceEquals(Scene, scene))
            {
                // Some settings (a name, a pivot) are plain fields that don't bump the version, so put it back whether
                // or not the version moved: only what changed is copied back.
                before.Restore();
                History.Restored(before);
                if (before.Version == _savedVersion) _savedVersion = scene.Version;
                Revalidate();
            }
            if (resume != null) BeginEdit(resume);
            throw;
        }
        _editDepth--;
        if (ReferenceEquals(Scene, scene)) History.Record(before, label);
        if (resume != null) BeginEdit(resume);
        return result;
    }

    /// <inheritdoc cref="Edit{T}(string, Func{T}, bool)"/>
    public void Edit(string label, Action change, bool apart = false) => Edit(label, () => { change(); return 0; }, apart);

    /// <summary>Whether a drag's edit is under way (<see cref="BeginEdit"/>).</summary>
    public bool Editing => _pending != null;

    /// <summary>
    /// Starts an edit that lasts, such as a drag from press to release: every change until <see cref="EndEdit"/> is one
    /// undo step (none if nothing changed).
    /// </summary>
    public void BeginEdit(string label)
    {
        if (_pending != null) EndEdit();
        _pending = (Scene.Snapshot(), label);
    }

    /// <summary>Ends the edit <see cref="BeginEdit"/> started, recording it as one step if the scene changed.</summary>
    public void EndEdit()
    {
        if (_pending is not { } p) return;
        _pending = null;
        if (ReferenceEquals(p.Before.Scene, Scene)) History.Record(p.Before, p.Label);
    }

    /// <summary>Undoes the last step to the scene (the person's or an agent's); returns its name, or null if there was none.</summary>
    public string? Undo() => Step(undo: true);

    /// <summary>Redoes the last step undone; returns its name, or null if there was none.</summary>
    public string? Redo() => Step(undo: false);

    private string? Step(bool undo)
    {
        if (_editDepth > 0) throw new InvalidOperationException($"{(undo ? "Undo" : "Redo")} can't be used in the middle of an edit to the scene.");
        EndEdit();
        var label = undo ? History.Undo(Scene) : History.Redo(Scene);
        if (label == null) return null;
        if (History.StateOf(Scene) == _savedState) _savedVersion = Scene.Version;
        Revalidate();
        SetStatus($"{(undo ? "Undid" : "Redid")} {label}.");
        return label;
    }

    // ───────────────────────────── editing items ─────────────────────────────

    /// <summary>
    /// Sets a channel at the current frame by the rule AI agents' keys follow (<see cref="Keying"/>). With auto-key on,
    /// a key here; the first one replaces the constant of a channel never keyed, so its animation starts at the first
    /// frame keyed, not at a key at frame 0 nobody chose. With auto-key off, the value throughout for a channel with
    /// one key or none, or the key here on an animated channel (one with no key here gets one, so the change isn't lost).
    /// </summary>
    public void SetChannel(SceneItem item, Channel channel, double value) => Edit($"Set {item.Name}'s {channel}", () =>
    {
        var env = item[channel];
        bool key = AutoKey || Keying.IsAnimated(env) && env.IndexOf(Frame) < 0;
        Keying.Set(env, Scene, Frame, value, key, $"{item.Name}'s {channel}");
        Scene.Notify();
    });

    /// <summary>Sets an item's position (<paramref name="first"/> 0), rotation (3) or scale (6) as one undo step, by <see cref="SetChannel"/>'s rule.</summary>
    public void SetVector(SceneItem item, int first, Vec3 v) => Edit($"{VectorVerb(first)} {item.Name}", () =>
    {
        SetChannel(item, (Channel)first, v.X);
        SetChannel(item, (Channel)(first + 1), v.Y);
        SetChannel(item, (Channel)(first + 2), v.Z);
    });

    /// <summary>What setting a position, rotation or scale is called as an undo step.</summary>
    public static string VectorVerb(int first) => first switch { 0 => "Move", 3 => "Rotate", _ => "Size" };

    /// <summary>Keys every channel of the chosen item at this frame with its value here, by the same rule as <see cref="SetChannel"/>.</summary>
    public void CreateKey()
    {
        if (Selected is not { } item) return;
        Edit("Create Key", () =>
        {
            var values = item.Channels.Select(c => c.Evaluate(Frame)).ToArray();
            for (int i = 0; i < values.Length; i++) Keying.Key(item.Channels[i], Scene, new EnvelopeKey(Frame, values[i]), keepShape: true);
            Scene.Notify();
        });
        SetStatus($"Key made at frame {Frame:0}.");
    }

    /// <summary>
    /// Removes the chosen item's keys at this frame, but not a channel's only key, which holds its value (as agents'
    /// delete_key does): without it the channel would fall to 0.
    /// </summary>
    public void DeleteKey()
    {
        if (Selected is not { } item) return;
        int kept = 0;
        Edit("Delete Key", () =>
        {
            foreach (var c in item.Channels)
            {
                if (c.IndexOf(Frame) < 0) continue;
                if (c.Keys.Count == 1) kept++;
                else c.RemoveKeyAt(Frame);
            }
        });
        SetStatus($"Key at frame {Frame:0} deleted." + (kept > 0 ? $" {kept} channel(s) kept it: it's their only key." : ""));
    }

    /// <summary>Adds an item as one undo step ("Add Light") and chooses it.</summary>
    public T Add<T>(T item) where T : SceneItem
    {
        Edit($"Add {item.Kind}", () => Scene.Add(item));
        Select(item);
        return item;
    }

    /// <summary>Removes an item (an object takes its bones) as one undo step.</summary>
    public void Remove(SceneItem item)
    {
        if (item.Scene != Scene) return;
        Edit($"Remove {item.Name}", () => Scene.Remove(item));
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
