using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Hub;
using AVAModeler.Tools;

namespace AVAModeler.UI.Tests;

/// <summary>
/// Live sync with AVALightWave and the hub's status line, in a test window on pipes of the tests' own with a stand-in
/// AVALightWave and a temporary settings folder: a linked object's edits reach it as <c>object_changed</c> with a
/// readable copy, a burst of edits as one or two messages; nothing when unlinked or with the preference off; the status
/// line following the stand-in coming and going and each sync; and the UI thread never waiting on a slow answer.
/// </summary>
public sealed class HubLiveSyncUITests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "avamodeler-live-ui-" + Guid.NewGuid().ToString("N"));
    private static string Pipe() => "avamodeler-live-" + Guid.NewGuid().ToString("N")[..12];

    public HubLiveSyncUITests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    /// <summary>A window on the hub (short waits for tests), its pipes the test's own.</summary>
    private MainWindow Window()
    {
        var w = Ui.Open();
        w.Hub.Pipe = Pipe();
        w.Hub.LightWavePipe = Pipe();
        w.Hub.SettingsFolder = _folder;
        w.Hub.LiveSyncDelay = TimeSpan.FromMilliseconds(150);
        w.Hub.PresenceInterval = TimeSpan.FromMilliseconds(100);
        w.Session.Preferences.HubEnabled = true;
        w.SyncHub();
        Assert.True(w.Hub.Running);
        return w;
    }

    /// <summary>A stand-in AVALightWave on the window's pipe, recording what it's sent (and reading each live copy as it comes).</summary>
    private static HubListener StandIn(MainWindow w, List<(HubMessage Message, int Points)> got, int delayMs = 0)
    {
        var listener = new HubListener(w.Hub.LightWavePipe, async m =>
        {
            int points = -1;
            if (m.Type == HubMessage.ObjectChanged && m.Path != null) points = ObjectFiles.Load(m.Path).Layers.Sum(l => l.Mesh.Points.Count);
            if (delayMs > 0) await Task.Delay(delayMs);
            lock (got) got.Add((m, points));
            return HubProtocol.Ok(new JsonObject { ["updated"] = 1 });
        });
        listener.Start();
        return listener;
    }

    private static void PumpFor(int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            Ui.Pump();
            Thread.Sleep(2);
        }
    }

    private static void PumpUntil(Func<bool> done, int ms = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < ms)
        {
            Ui.Pump();
            Thread.Sleep(2);
        }
        Ui.Pump();
    }

    private static List<(HubMessage Message, int Points)> Changes(List<(HubMessage Message, int Points)> got)
    {
        lock (got) return [.. got.Where(g => g.Message.Type == HubMessage.ObjectChanged)];
    }

    private static void Move(MainWindow w, double y) =>
        w.Session.Commands.Run("Move", new Dictionary<string, object?> { ["Offset"] = new Vec3(0, y, 0) });

    [AvaloniaFact]
    public async Task A_linked_objects_edits_reach_lightwave_as_one_copy_after_the_burst()
    {
        var w = Window();
        var got = new List<(HubMessage Message, int Points)>();
        await using var lightWave = StandIn(w, got);
        w.Session.Commands.Run("Ball");
        w.Modeler.Execute("Send to AVALightWave");
        PumpUntil(() => { lock (got) return got.Count > 0; });
        var link = w.Hub.LinkOf(w.Session.Document);
        Assert.NotNull(link);
        Assert.Empty(Changes(got));

        // A burst of edits: one message (two at most), after it, with the object as it ends up.
        for (int i = 0; i < 10; i++)
        {
            Move(w, 0.1);
            Ui.Pump();
        }
        w.Session.Commands.Run("Box");
        int points = w.Session.Document.Layers.Sum(l => l.Mesh.Points.Count);
        PumpUntil(() => Changes(got).Count > 0 && Changes(got)[^1].Points == points);
        PumpFor(400);
        var changes = Changes(got);
        Assert.InRange(changes.Count, 1, 2);
        var (message, seen) = changes[^1];
        Assert.Equal(HubMessage.ObjectChanged, message.Type);
        Assert.Equal(HubProtocol.Modeler, message.From);
        Assert.Equal(link, message.Link);
        Assert.Equal(points, seen);
        Assert.Equal(HubFiles.LivePathFor(Path.Combine(_folder, "hub"), link!), message.Path);
        Assert.Equal(points, ObjectFiles.Load(message.Path!).Layers.Sum(l => l.Mesh.Points.Count));
        // Only the one file, and the person's object still unsaved with no file of its own.
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "hub"), "live-*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "hub"), ".*"));
        Assert.Null(w.Session.Document.FilePath);
        Assert.True(w.Session.Document.Modified);
        Assert.True(w.Hub.LiveSyncs >= 1);

        // A selection change alone sends nothing; nor does a quiet moment.
        int before = Changes(got).Count;
        w.Session.Commands.SelectAll();
        PumpFor(500);
        Assert.Equal(before, Changes(got).Count);
        await w.Hub.StopAsync();
    }

    [AvaloniaFact]
    public async Task Nothing_is_sent_when_unlinked_or_with_live_sync_off()
    {
        var w = Window();
        var got = new List<(HubMessage Message, int Points)>();
        await using var lightWave = StandIn(w, got);
        // Not linked: edits stay here.
        w.Session.Commands.Run("Ball");
        Move(w, 1);
        PumpFor(500);
        Assert.Empty(Changes(got));

        // Linked, but live sync turned off.
        w.Session.Preferences.HubLiveSync = false;
        w.Modeler.Execute("Send to AVALightWave");
        PumpUntil(() => { lock (got) return got.Count > 0; });
        Move(w, 1);
        PumpFor(500);
        Assert.Empty(Changes(got));
        Assert.False(w.Hub.LiveSyncing);

        // On again: the next edit goes.
        w.Session.Preferences.HubLiveSync = true;
        Move(w, 1);
        PumpUntil(() => Changes(got).Count > 0);
        Assert.Single(Changes(got));

        // A new object isn't linked: its edits stay here.
        w.Session.Replace(new Document());
        w.Session.Commands.Run("Box");
        PumpFor(500);
        Assert.Single(Changes(got));
        await w.Hub.StopAsync();
    }

    [AvaloniaFact]
    public async Task Saving_a_linked_object_sends_object_saved_and_no_extra_live_copy()
    {
        var w = Window();
        var got = new List<(HubMessage Message, int Points)>();
        await using var lightWave = StandIn(w, got);
        var doc = new Document();
        new BoxTool().Run(new ToolContext(doc));
        var path = Path.Combine(_folder, "crate.lwo");
        ObjectFiles.Save(doc, path);
        // AVALightWave's Edit in Modeler (answered on the UI thread: pump until it has been).
        var edit = HubClient.SendAsync(w.Hub.Pipe, new HubMessage(HubMessage.EditObject, HubProtocol.LightWave, path, "link-9", "Crate"), TimeSpan.FromSeconds(10));
        PumpUntil(() => edit.IsCompleted);
        Assert.True((bool?)(await edit)["ok"]);
        Assert.Equal("link-9", w.Hub.LinkOf(w.Session.Document));
        Move(w, 1);
        w.Modeler.Execute("Save");
        PumpUntil(() => { lock (got) return got.Any(g => g.Message.Type == HubMessage.ObjectSaved); });
        PumpFor(500);
        lock (got)
        {
            Assert.Single(got, g => g.Message.Type == HubMessage.ObjectSaved && g.Message.Link == "link-9");
            Assert.DoesNotContain(got, g => g.Message.Type == HubMessage.ObjectChanged);
        }
        Assert.Contains("linked Crate", w.Hub.Status.Text);
        Assert.Contains("synced", w.Hub.Status.Text);
        await w.Hub.StopAsync();
    }

    [AvaloniaFact]
    public async Task The_status_line_follows_lightwave_coming_and_going_and_each_sync()
    {
        var w = Ui.Open();
        Assert.Equal("Hub: off", w.Hub.Status.Text);
        Assert.Equal("Hub: off", w.Modeler.StatusBar.HubText);
        w.Hub.Pipe = Pipe();
        w.Hub.LightWavePipe = Pipe();
        w.Hub.SettingsFolder = _folder;
        w.Hub.LiveSyncDelay = TimeSpan.FromMilliseconds(150);
        w.Hub.PresenceInterval = TimeSpan.FromMilliseconds(100);
        w.Session.Preferences.HubEnabled = true;
        w.SyncHub();
        PumpFor(300);
        Assert.Equal("Hub: on (AVALightWave not running)", w.Modeler.StatusBar.HubText);

        // AVALightWave appears: seen within a check or two.
        var got = new List<(HubMessage Message, int Points)>();
        var lightWave = StandIn(w, got);
        PumpUntil(() => w.Modeler.StatusBar.HubText.StartsWith("Hub: AVALightWave connected", StringComparison.Ordinal));
        Assert.Equal("Hub: AVALightWave connected", w.Modeler.StatusBar.HubText);
        // The presence check is a connection only: AVALightWave isn't sent anything for it.
        lock (got) Assert.Empty(got);

        // Linked and synced: the object's name and the time.
        w.Session.Commands.Run("Ball");
        w.Modeler.Execute("Send to AVALightWave");
        PumpUntil(() => w.Modeler.StatusBar.HubText.Contains("synced"));
        Assert.Matches(@"^Hub: AVALightWave connected · linked Modeler object · synced \d\d:\d\d:\d\d$", w.Modeler.StatusBar.HubText);
        int syncs = w.Hub.LiveSyncs;
        Move(w, 1);
        PumpUntil(() => w.Hub.LiveSyncs > syncs);
        Assert.True(w.Hub.LiveSyncs > syncs);

        // Layout shows the same line.
        w.ShowLayout();
        Ui.Pump();
        Assert.Equal(w.Modeler.StatusBar.HubText, LayoutHub(w));
        w.ShowModeler();

        // AVALightWave goes: said within a check or two, and an edit then costs nothing and isn't retried.
        await lightWave.DisposeAsync();
        PumpUntil(() => w.Modeler.StatusBar.HubText.StartsWith("Hub: on (AVALightWave not running)", StringComparison.Ordinal));
        Assert.StartsWith("Hub: on (AVALightWave not running) · linked Modeler object", w.Modeler.StatusBar.HubText);
        syncs = w.Hub.LiveSyncs;
        Move(w, 1);
        PumpFor(600);
        Assert.Equal(syncs, w.Hub.LiveSyncs);

        // Off the hub.
        await w.Hub.StopAsync();
        Ui.Pump();
        Assert.Equal("Hub: off", w.Modeler.StatusBar.HubText);
    }

    private static string LayoutHub(MainWindow w) =>
        ((AVAModeler.Layout.UI.LayoutWorkspace)w.Content!).HubText;

    [AvaloniaFact]
    public async Task A_slow_lightwave_never_holds_up_editing()
    {
        var w = Window();
        var got = new List<(HubMessage Message, int Points)>();
        // The stand-in takes two seconds over each answer (within the time a live sync waits, here).
        w.Hub.LiveSyncTimeout = TimeSpan.FromSeconds(10);
        await using var lightWave = StandIn(w, got, delayMs: 2000);
        w.Session.Commands.Run("Ball");
        w.Modeler.Execute("Send to AVALightWave");
        PumpUntil(() => { lock (got) return got.Count > 0; });
        Move(w, 1);
        // Wait until the live copy is on its way (being answered slowly).
        PumpFor(300);
        // Edits while it's sending go through at once, on the UI thread.
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            Move(w, 0.1);
            Ui.Pump();
        }
        Assert.True(clock.ElapsedMilliseconds < 1000, $"editing took {clock.ElapsedMilliseconds} ms");
        // Then those edits go too, once more after the first: two messages, the last with the object as it ended.
        PumpUntil(() => Changes(got).Count >= 2, 10000);
        PumpFor(2500);
        Assert.Equal(2, Changes(got).Count);
        await w.Hub.StopAsync();
    }

    [AvaloniaFact]
    public async Task A_tool_being_dragged_holds_the_sync_until_it_lets_go()
    {
        var w = Window();
        var got = new List<(HubMessage Message, int Points)>();
        await using var lightWave = StandIn(w, got);
        w.Session.Commands.Run("Ball");
        w.Modeler.Execute("Send to AVALightWave");
        PumpUntil(() => { lock (got) return got.Count > 0; });
        w.Session.ToolDragging = true;
        Move(w, 1);
        PumpFor(600);
        Assert.Empty(Changes(got));
        w.Session.ToolDragging = false;
        PumpUntil(() => Changes(got).Count > 0);
        Assert.Single(Changes(got));
        await w.Hub.StopAsync();
    }

    [AvaloniaFact]
    public void Live_sync_is_a_preference_beside_link_with_lightwave_and_kept()
    {
        var p = new AVAModeler.Modeler.UI.Preferences();
        Assert.True(p.HubLiveSync);
        var file = Path.Combine(_folder, "preferences.json");
        p.HubLiveSync = false;
        p.Save(file);
        Assert.False(AVAModeler.Modeler.UI.Preferences.Load(file).HubLiveSync);
        var w = Ui.Open();
        var window = new AVAModeler.Modeler.UI.PreferencesWindow(w.Session, () => { });
        var boxes = window.GetLogicalDescendants().OfType<Avalonia.Controls.CheckBox>().Select(c => c.Content as string).ToList();
        int hub = boxes.FindIndex(b => b != null && b.StartsWith("Link with AVALightWave", StringComparison.Ordinal));
        Assert.True(hub >= 0);
        Assert.StartsWith("Live sync with AVALightWave", boxes[hub + 1]);
    }
}
