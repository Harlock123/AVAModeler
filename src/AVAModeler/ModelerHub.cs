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
/// <para>Live sync (<see cref="Preferences.HubLiveSync"/>): while the open document is linked, each change restarts a
/// short wait (<see cref="LiveSyncDelay"/>); when the changes stop (and no tool is being dragged), the document is copied
/// on the UI thread and, on a worker thread, written whole into the hub folder (one file per link) and sent as
/// <c>object_changed</c>. One send at a time: changes during a send are sent once more after it. If AVALightWave isn't
/// there, nothing more is tried until the next change. <see cref="Status"/> says how the hub stands (on or off,
/// AVALightWave there or not, checked every few seconds, the linked object, the last sync or save sent) and
/// <see cref="StatusChanged"/> is raised on the UI thread when it changes.</para>
/// </summary>
internal sealed class ModelerHub : IAsyncDisposable
{
    private readonly ModelerSession session;
    private readonly Func<string, Task> open;
    private readonly Action shown;
    private HubListener? _listener;
    private (Document Document, string Link)? _link;
    private string? _linkName;
    private Document? _watched;

    // Live sync: the last change's time, whether a wait or a send is under way, and what was last sent.
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _lastChange;
    private bool _waiting, _sending, _again;
    private object[]? _sent;
    private CancellationTokenSource? _presence;
    private HubStatus _status = new(false, false, null, null);

    public ModelerHub(ModelerSession session, Func<string, Task> open, Action shown)
    {
        this.session = session;
        this.open = open;
        this.shown = shown;
        session.DocumentReplaced += Watch;
        Watch();
    }

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

    /// <summary>How long live sync waits after the last change before sending (tests give a shorter one).</summary>
    public TimeSpan LiveSyncDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How long a live sync waits for AVALightWave to answer.</summary>
    public TimeSpan LiveSyncTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often, while on the hub, whether AVALightWave is there is checked (tests give a shorter one).</summary>
    public TimeSpan PresenceInterval { get; set; } = TimeSpan.FromSeconds(3);

    public bool Running => _listener != null;

    /// <summary>How the hub stands now (shown at the right of the status bars).</summary>
    public HubStatus Status => _status;

    /// <summary>Raised on the UI thread when <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    /// <summary>How many live syncs have been sent and taken (for tests).</summary>
    public int LiveSyncs { get; private set; }

    /// <summary>The link the open document has with an object in AVALightWave's scene, if any.</summary>
    public string? LinkOf(Document document) => _link is { } l && ReferenceEquals(l.Document, document) ? l.Link : null;

    /// <summary>Links the open document to an object in AVALightWave's scene (when it was started for one, with --hub-link).</summary>
    public void Adopt(Document document, string link) => Link(document, link, null);

    /// <summary>Links a document; what it is now counts as sent, so live sync sends only what changes from here.</summary>
    private void Link(Document document, string link, string? name)
    {
        _link = (document, link);
        _linkName = name ?? (document.FilePath != null ? Path.GetFileNameWithoutExtension(document.FilePath) : "Modeler object");
        _sent = Fingerprint(document);
        Watch();
        UpdateStatus();
    }

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
        StartPresence();
        UpdateStatus();
        return null;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        _presence?.Cancel();
        _presence = null;
        UpdateStatus(_status with { LightWave = false });
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
            Link(doc, link, message.Name);
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
            Link(session.Document, link, message.Name);
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
        // The save carries the object as it is: live sync has nothing more to send for it.
        if (message.Link != null)
        {
            _sent = Fingerprint(document);
            // An object sent before it had a file takes the file's name.
            if (_linkName is null or "Modeler object") _linkName = Path.GetFileNameWithoutExtension(path);
            UpdateStatus();
        }
        try
        {
            var reply = await Task.Run(() => HubClient.SendAsync(LightWavePipe, message, Timeout));
            int updated = (int?)reply["updated"] ?? 0;
            if (updated > 0) session.SetStatus($"Saved {Path.GetFileName(path)}; AVALightWave updated {updated} object(s) in its scene.");
            Heard(true, synced: true);
            return true;
        }
        catch (HubUnavailableException)
        {
            Heard(false);
            return false;
        }
        catch (InvalidOperationException)
        {
            Heard(true);
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
        string name = doc.FilePath != null ? Path.GetFileNameWithoutExtension(doc.FilePath) : "Modeler object";
        Link(doc, link, name);
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
            Heard(true, synced: true);
            return (bool?)reply["updated"] == true ? $"Updated {name} in AVALightWave's scene." : $"Sent {name} to AVALightWave's scene.";
        }
        catch (InvalidOperationException e)
        {
            Heard(true);
            return $"AVALightWave didn't take it: {e.Message}";
        }
        catch (HubUnavailableException)
        {
            Heard(false);
            var started = Launcher.Launch(session.Preferences.LightWavePath, path, link);
            return started != null
                ? $"Started AVALightWave with {name}."
                : "AVALightWave isn't running and couldn't be found: set where it is in Edit › Preferences (or AVALIGHTWAVE_PATH), or start it and try again.";
        }
    }

    // ───────────────────────────── live sync ─────────────────────────────

    /// <summary>Follows the open document's changes (a new or opened one replaces it).</summary>
    private void Watch()
    {
        if (ReferenceEquals(_watched, session.Document)) return;
        if (_watched != null) _watched.Changed -= Changed;
        _watched = session.Document;
        _watched.Changed += Changed;
        UpdateStatus();
    }

    /// <summary>Whether live sync is on for the open document: on the hub, allowed, and the document linked.</summary>
    public bool LiveSyncing => Running && session.Preferences.HubLiveSync && LinkOf(session.Document) != null;

    /// <summary>A change to the document: the wait starts again (cheap: no copy here).</summary>
    private void Changed()
    {
        if (!LiveSyncing) return;
        _lastChange = _clock.Elapsed;
        if (!_waiting && !_sending) Wait(LiveSyncDelay);
        else if (_sending) _again = true;
    }

    private void Wait(TimeSpan delay)
    {
        _waiting = true;
        _ = Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1))
            .ContinueWith(_ => Dispatcher.UIThread.Post(Fire), TaskScheduler.Default);
    }

    /// <summary>The wait is over: send if the changes have stopped, the person isn't mid-drag and something did change.</summary>
    private void Fire()
    {
        _waiting = false;
        if (!LiveSyncing || _sending) return;
        var quiet = _clock.Elapsed - _lastChange;
        if (quiet < LiveSyncDelay)
        {
            Wait(LiveSyncDelay - quiet);
            return;
        }
        // Mid-drag the object is changing under the mouse: wait for a quiet moment.
        if (session.ToolDragging)
        {
            Wait(LiveSyncDelay);
            return;
        }
        var doc = session.Document;
        var print = Fingerprint(doc);
        if (_sent != null && print.SequenceEqual(_sent)) return;
        if (SettingsFolder == null || LinkOf(doc) is not { } link) return;
        var copy = doc.Copy();
        string name = _linkName ?? "Modeler object";
        string folder = SettingsFolder, pipe = LightWavePipe;
        var timeout = LiveSyncTimeout;
        _sending = true;
        _again = false;
        _ = Task.Run(async () =>
        {
            try
            {
                var path = HubFiles.LivePathFor(HubFiles.Folder(folder), link);
                HubFiles.WriteWhole(path, temp => ObjectFiles.Save(copy, temp));
                await HubClient.SendAsync(pipe, new HubMessage(HubMessage.ObjectChanged, HubProtocol.Modeler, path, link, name), timeout).ConfigureAwait(false);
                return (bool?)true;
            }
            catch (HubUnavailableException)
            {
                return false;
            }
            catch (Exception)
            {
                // AVALightWave said no, or the copy couldn't be written: this change isn't taken (a later one may be).
                return null;
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() => Sent(print, t.IsCompletedSuccessfully ? t.Result : null)), TaskScheduler.Default);
    }

    /// <summary>A live sync came back: taken (true), AVALightWave not there (false), or not taken (null).</summary>
    private void Sent(object[] print, bool? taken)
    {
        _sending = false;
        if (taken == true)
        {
            _sent = print;
            LiveSyncs++;
        }
        Heard(taken != false, synced: taken == true);
        // Changes made while it was sending go once more; with AVALightWave gone, nothing more until the next change.
        if (_again && taken != false && LiveSyncing) Wait(LiveSyncDelay);
        _again = false;
    }

    /// <summary>
    /// What the object is, cheaply: each layer's mesh (by reference and version), state, name and pivot, and the
    /// surfaces' settings. Selection changes leave it alone, so they send nothing.
    /// </summary>
    private static object[] Fingerprint(Document doc)
    {
        var parts = new List<object>(doc.Layers.Count * 5 + doc.Surfaces.Count + 1);
        foreach (var l in doc.Layers)
        {
            parts.Add(l.Mesh);
            parts.Add(l.Mesh.Version);
            parts.Add(l.State);
            parts.Add(l.Name);
            parts.Add(l.Pivot);
        }
        parts.Add(doc.Surfaces.Count);
        foreach (var s in doc.Surfaces) parts.Add(s.Name + "|" + s.Signature() + "|" + s.Textures.Count);
        return [.. parts];
    }

    // ───────────────────────────── status ─────────────────────────────

    /// <summary>Checks every <see cref="PresenceInterval"/> whether AVALightWave is on the hub (a connection only, no message).</summary>
    private void StartPresence()
    {
        _presence?.Cancel();
        var stop = _presence = new CancellationTokenSource();
        string pipe = LightWavePipe;
        var every = PresenceInterval;
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                bool there = HubClient.Answers(pipe);
                Dispatcher.UIThread.Post(() => { if (!stop.IsCancellationRequested) Heard(there); });
                try { await Task.Delay(every, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    /// <summary>What was learnt of AVALightWave (there or not, and whether a sync or save just went).</summary>
    private void Heard(bool there, bool synced = false) =>
        UpdateStatus(_status with { LightWave = there && Running, LastSync = synced ? DateTime.Now : _status.LastSync });

    private void UpdateStatus(HubStatus? from = null)
    {
        var next = (from ?? _status) with { OnHub = Running, LinkedName = LinkOf(session.Document) != null ? _linkName : null };
        if (!Running) next = next with { LightWave = false };
        bool changed = next != _status || !_told;
        _status = next;
        _told = true;
        if (changed) StatusChanged?.Invoke();
    }

    private bool _told;
}

/// <summary>How the hub stands, for the status bars: on it or not, AVALightWave there or not, the linked object and the last sync or save sent.</summary>
internal sealed record HubStatus(bool OnHub, bool LightWave, string? LinkedName, DateTime? LastSync)
{
    /// <summary>"Hub: AVALightWave connected · linked pawn · synced 14:02:11", "Hub: on (AVALightWave not running)", "Hub: off".</summary>
    public string Text
    {
        get
        {
            if (!OnHub) return "Hub: off";
            var text = LightWave ? "Hub: AVALightWave connected" : "Hub: on (AVALightWave not running)";
            if (LinkedName != null) text += $" · linked {LinkedName}";
            if (LastSync is { } at) text += $" · synced {at:HH:mm:ss}";
            return text;
        }
    }
}
