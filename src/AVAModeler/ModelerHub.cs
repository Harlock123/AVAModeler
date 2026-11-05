using System.Text.Json.Nodes;
using Avalonia.Threading;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Hub;
using AVAModeler.Modeler.UI;

namespace AVAModeler;

/// <summary>
/// AVA Modeler's end of the hub, the live link with AVALightWave (the standalone Layout). While the preference allows it
/// (<see cref="Preferences.HubEnabled"/>), it listens on its pipe: AVALightWave's Edit in Modeler sends
/// <c>edit_object</c>, and the object opens here (not over unsaved work: that's refused, saying why), linked to the
/// object in AVALightWave's scene. Saving a linked object (or any object) tells AVALightWave (<c>object_saved</c>), which
/// updates it in its scene; File › Send to AVALightWave sends the object there (<c>add_object</c>: a new object, or the
/// linked one updated), starting AVALightWave if it isn't running. Either app works alone: when the other isn't there,
/// nothing waits more than a moment.
/// </summary>
internal sealed class ModelerHub(ModelerSession session, Func<string, Task> open, Action shown) : IAsyncDisposable
{
    private HubListener? _listener;
    private (Document Document, string Link)? _link;

    /// <summary>The pipe AVA Modeler listens on (tests give their own).</summary>
    public string Pipe { get; set; } = HubProtocol.PipeName(HubProtocol.Modeler);

    /// <summary>The pipe AVALightWave listens on (tests give their own).</summary>
    public string LightWavePipe { get; set; } = HubProtocol.PipeName(HubProtocol.LightWave);

    /// <summary>The settings folder, whose hub folder holds objects sent without a file (tests give a temporary one; null, none).</summary>
    public string? SettingsFolder { get; set; }

    /// <summary>Finds and starts AVALightWave (tests give one that starts nothing).</summary>
    public AppLauncher Launcher { get; set; } = new(HubProtocol.LightWave);

    /// <summary>How long a message waits for AVALightWave to answer.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    public bool Running => _listener != null;

    /// <summary>The link the open document has with an object in AVALightWave's scene, if any.</summary>
    public string? LinkOf(Document document) => _link is { } l && ReferenceEquals(l.Document, document) ? l.Link : null;

    /// <summary>Links the open document to an object in AVALightWave's scene (when it was started for one, with --hub-link).</summary>
    public void Adopt(Document document, string link) => _link = (document, link);

    /// <summary>Starts listening; returns why it couldn't (another AVA Modeler has the hub), or null.</summary>
    public string? Start()
    {
        if (_listener != null) return null;
        var listener = new HubListener(Pipe, Handle);
        try
        {
            listener.Start();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = listener.DisposeAsync();
            return e.Message;
        }
        _listener = listener;
        return null;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener != null) await listener.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>A message from AVALightWave, answered on the UI thread.</summary>
    internal Task<JsonObject> Handle(HubMessage message) => message.Type switch
    {
        HubMessage.Hello => Task.FromResult(HubProtocol.Ok(new JsonObject { ["app"] = HubProtocol.Modeler, ["pid"] = Environment.ProcessId })),
        HubMessage.EditObject => Dispatcher.UIThread.InvokeAsync(() => Edit(message)),
        _ => Task.FromResult(HubProtocol.Error($"AVA Modeler doesn't take \"{message.Type}\" messages.")),
    };

    /// <summary>Opens an object AVALightWave wants modeled, linked to its scene object.</summary>
    private async Task<JsonObject> Edit(HubMessage message)
    {
        if (message.Path is not { } path || !File.Exists(path)) return HubProtocol.Error($"There's no object file {message.Path} to open.");
        if (message.Link is not { Length: > 0 } link) return HubProtocol.Error("An object to edit needs its link.");
        var doc = session.Document;
        bool same = doc.FilePath != null && Path.GetFullPath(doc.FilePath) == Path.GetFullPath(path);
        if (same)
        {
            // Already open (with any changes kept): just linked.
            _link = (doc, link);
        }
        else
        {
            if (doc.Modified && session.Document.Layers.Any(l => !l.Mesh.IsEmpty))
            {
                var why = $"AVA Modeler has unsaved changes to {Name(doc)}: save or discard them there, then use Edit in Modeler again.";
                session.SetStatus($"AVALightWave asked to edit {message.Name ?? Path.GetFileName(path)}, but {Name(doc)} has unsaved changes. Save them, then try again.");
                return HubProtocol.Error(why);
            }
            session.CommitTool();
            await open(path);
            if (session.Document.FilePath is not { } opened || Path.GetFullPath(opened) != Path.GetFullPath(path))
                return HubProtocol.Error($"AVA Modeler couldn't open {Path.GetFileName(path)}.");
            _link = (session.Document, link);
        }
        shown();
        session.SetStatus($"Editing {message.Name ?? Path.GetFileName(path)} for AVALightWave: save it (Ctrl+S) or File › Send to AVALightWave to update its scene.");
        return HubProtocol.Ok(new JsonObject { ["app"] = HubProtocol.Modeler });
    }

    private static string Name(Document doc) => doc.FilePath != null ? Path.GetFileName(doc.FilePath) : "the open object";

    /// <summary>
    /// Tells AVALightWave an object was saved, so it updates that object in its scene (by link, else by file). Doesn't
    /// wait for an answer on the UI thread; returns whether AVALightWave took it.
    /// </summary>
    public async Task<bool> NotifySaved(Document document, string path)
    {
        var message = new HubMessage(HubMessage.ObjectSaved, HubProtocol.Modeler, Path.GetFullPath(path), LinkOf(document), Path.GetFileNameWithoutExtension(path));
        try
        {
            var reply = await Task.Run(() => HubClient.SendAsync(LightWavePipe, message, Timeout));
            int updated = (int?)reply["updated"] ?? 0;
            if (updated > 0) session.SetStatus($"Saved {Path.GetFileName(path)}; AVALightWave updated {updated} object(s) in its scene.");
            return true;
        }
        catch (Exception e) when (e is HubUnavailableException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// File › Send to AVALightWave: the open object to AVALightWave's scene, by its file if saved, else as a copy in the
    /// hub folder. A linked object updates its scene object; another is added. Starts AVALightWave with it if it isn't
    /// running. Returns what happened, for the status bar.
    /// </summary>
    public async Task<string> SendToLightWave()
    {
        var doc = session.Document;
        if (doc.Layers.All(l => l.Mesh.IsEmpty)) return "There's nothing to send: the object is empty.";
        var link = LinkOf(doc) ?? HubFiles.NewLink();
        _link = (doc, link);
        string name = doc.FilePath != null ? Path.GetFileNameWithoutExtension(doc.FilePath) : "Modeler object";
        string path;
        if (doc.FilePath != null && !doc.Modified && File.Exists(doc.FilePath)) path = Path.GetFullPath(doc.FilePath);
        else
        {
            if (SettingsFolder == null) return "Save the object first: there's no folder to hand an unsaved one over in.";
            path = HubFiles.PathFor(HubFiles.Folder(SettingsFolder), name, link);
            var keep = (doc.FilePath, doc.Modified);
            try
            {
                ObjectFiles.Save(doc, path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return $"The object couldn't be handed over: {e.Message}";
            }
            finally
            {
                // The person's own file and its unsaved state are untouched by the copy.
                (doc.FilePath, doc.Modified) = keep;
            }
        }
        var message = new HubMessage(HubMessage.AddObject, HubProtocol.Modeler, path, link, name);
        try
        {
            var reply = await Task.Run(() => HubClient.SendAsync(LightWavePipe, message, Timeout));
            return (bool?)reply["updated"] == true ? $"Updated {name} in AVALightWave's scene." : $"Sent {name} to AVALightWave's scene.";
        }
        catch (InvalidOperationException e)
        {
            return $"AVALightWave didn't take it: {e.Message}";
        }
        catch (HubUnavailableException)
        {
            var started = Launcher.Launch(session.Preferences.LightWavePath, path, link);
            return started != null
                ? $"Started AVALightWave with {name}."
                : "AVALightWave isn't running and couldn't be found: set where it is in Edit › Preferences (or AVALIGHTWAVE_PATH), or start it and try again.";
        }
    }
}
