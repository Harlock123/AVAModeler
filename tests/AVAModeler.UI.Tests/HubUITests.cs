using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Hub;
using AVAModeler.Tools;

namespace AVAModeler.UI.Tests;

/// <summary>
/// AVA Modeler's end of the hub, in a test window on pipes of the tests' own with a stand-in for AVALightWave: Edit in
/// Modeler opening an object (not over unsaved work), saves going back with the link, Send to AVALightWave (and
/// starting it, faked, when it isn't there). Nothing reaches a real AVALightWave or the person's settings.
/// </summary>
public sealed class HubUITests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "avamodeler-hub-ui-" + Guid.NewGuid().ToString("N"));
    private static string Pipe() => "avamodeler-hubui-" + Guid.NewGuid().ToString("N")[..12];

    public HubUITests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private static T Wait<T>(Task<T> task)
    {
        for (int i = 0; i < 10000 && !task.IsCompleted; i++)
        {
            Ui.Pump();
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "The hub didn't answer.");
        Ui.Pump();
        return task.GetAwaiter().GetResult();
    }

    /// <summary>A window on the hub, its pipes the test's own, and a stand-in AVALightWave that records what it's sent.</summary>
    private (MainWindow Window, List<HubMessage> Got, HubListener LightWave) Linked(Func<HubMessage, JsonObject>? answer = null)
    {
        var w = Ui.Open();
        w.Hub.Pipe = Pipe();
        w.Hub.LightWavePipe = Pipe();
        w.Hub.SettingsFolder = _folder;
        w.Session.Preferences.HubEnabled = true;
        w.SyncHub();
        Assert.True(w.Hub.Running);
        var got = new List<HubMessage>();
        var lightWave = new HubListener(w.Hub.LightWavePipe, m =>
        {
            lock (got) got.Add(m);
            return Task.FromResult(answer?.Invoke(m) ?? HubProtocol.Ok(new JsonObject { ["updated"] = 1 }));
        });
        lightWave.Start();
        return (w, got, lightWave);
    }

    private string SavedBox(string name)
    {
        var doc = new Document();
        new BoxTool().Run(new ToolContext(doc));
        var path = Path.Combine(_folder, name);
        ObjectFiles.Save(doc, path);
        return path;
    }

    private static List<HubMessage> Snapshot(List<HubMessage> got)
    {
        for (int i = 0; i < 2000; i++)
        {
            lock (got) if (got.Count > 0) return [.. got];
            Ui.Pump();
            Thread.Sleep(1);
        }
        lock (got) return [.. got];
    }

    [AvaloniaFact]
    public async Task Edit_in_modeler_opens_the_object_and_saving_tells_lightwave_with_the_link()
    {
        var (w, got, lightWave) = Linked();
        await using var _ = lightWave;
        var path = SavedBox("crate.lwo");
        var reply = Wait(HubClient.SendAsync(w.Hub.Pipe, new HubMessage(HubMessage.EditObject, HubProtocol.LightWave, path, "link-1", "Crate"), TimeSpan.FromSeconds(10)));
        Assert.True((bool?)reply["ok"]);
        Assert.Equal(path, w.Session.Document.FilePath);
        Assert.Equal("link-1", w.Hub.LinkOf(w.Session.Document));
        Assert.Contains("Editing Crate for AVALightWave", w.Session.Status);
        Assert.Same(w.Modeler, w.Content);

        // Model a little, save: AVALightWave hears which object, by its link.
        w.Session.Commands.Run("Move", new Dictionary<string, object?> { ["Offset"] = new Vec3(0, 1, 0) });
        w.Modeler.Execute("Save");
        var saved = Assert.Single(Snapshot(got));
        Assert.Equal(new HubMessage(HubMessage.ObjectSaved, HubProtocol.Modeler, path, "link-1", "crate"), saved);
        await w.Hub.StopAsync();
    }

    [AvaloniaFact]
    public async Task Edit_in_modeler_doesnt_open_over_unsaved_work()
    {
        var (w, _, lightWave) = Linked();
        await using var __ = lightWave;
        w.Session.Commands.Run("Ball");
        Assert.True(w.Session.Document.Modified);
        var before = w.Session.Document;
        var path = SavedBox("crate.lwo");
        var e = Assert.Throws<InvalidOperationException>(() => Wait(HubClient.SendAsync(w.Hub.Pipe, new HubMessage(HubMessage.EditObject, HubProtocol.LightWave, path, "link-2"), TimeSpan.FromSeconds(10))));
        Assert.Contains("unsaved changes", e.Message);
        Assert.Same(before, w.Session.Document);
        Assert.Null(w.Hub.LinkOf(before));
        Assert.Contains("unsaved changes", w.Session.Status);
        // Hello is answered; what AVA Modeler doesn't take is refused.
        Assert.Equal(HubProtocol.Modeler, (string?)Wait(HubClient.SendAsync(w.Hub.Pipe, new HubMessage(HubMessage.Hello, "test"), TimeSpan.FromSeconds(10)))["app"]);
        Assert.Throws<InvalidOperationException>(() => Wait(HubClient.SendAsync(w.Hub.Pipe, new HubMessage(HubMessage.AddObject, "test", path), TimeSpan.FromSeconds(10))));
        await w.Hub.StopAsync();
    }

    [AvaloniaFact]
    public async Task Send_to_lightwave_hands_over_an_unsaved_object_as_a_copy_and_keeps_its_link()
    {
        int sends = 0;
        var (w, got, lightWave) = Linked(m => HubProtocol.Ok(new JsonObject { ["updated"] = Interlocked.Increment(ref sends) > 1 }));
        await using var _ = lightWave;
        w.Session.Commands.Run("Ball");
        w.Modeler.Execute("Send to AVALightWave");
        var sent = Assert.Single(Snapshot(got));
        Assert.Equal(HubMessage.AddObject, sent.Type);
        Assert.NotNull(sent.Link);
        Assert.StartsWith(Path.Combine(_folder, "hub"), sent.Path);
        Assert.True(File.Exists(sent.Path));
        Assert.Equal(w.Session.Document.Current.Mesh.Points.Count, ObjectFiles.Load(sent.Path!).Current.Mesh.Points.Count);
        // The person's object is as it was: no file of its own, still unsaved.
        Assert.Null(w.Session.Document.FilePath);
        Assert.True(w.Session.Document.Modified);
        for (int i = 0; i < 200 && !w.Session.Status.Contains("AVALightWave"); i++) { Ui.Pump(); Thread.Sleep(1); }
        Assert.Contains("Sent Modeler object to AVALightWave", w.Session.Status);

        // Again: the same link, so AVALightWave updates the object rather than adding another.
        lock (got) got.Clear();
        w.Modeler.Execute("Send to AVALightWave");
        Assert.Equal(sent.Link, Assert.Single(Snapshot(got)).Link);
        await w.Hub.StopAsync();
    }

    [AvaloniaFact]
    public async Task Send_to_lightwave_starts_it_when_it_isnt_running()
    {
        var w = Ui.Open();
        w.Hub.Pipe = Pipe();
        w.Hub.LightWavePipe = Pipe();
        var started = new List<ProcessStartInfo>();
        var program = Path.Combine(_folder, OperatingSystem.IsWindows() ? "AVALightWave.exe" : "AVALightWave");
        File.WriteAllText(program, "");
        w.Hub.Launcher = new AppLauncher(HubProtocol.LightWave) { BaseDirectory = _folder, SearchPath = "", Start = i => { started.Add(i); return true; } };
        var path = SavedBox("crate.lwo");
        await w.Modeler.Open(path);
        var said = Wait(w.Hub.SendToLightWave());
        Assert.Equal("Started AVALightWave with crate.", said);
        var info = Assert.Single(started);
        Assert.Equal(program, info.FileName);
        Assert.Equal(path, info.ArgumentList[0]);
        Assert.Equal(w.Hub.LinkOf(w.Session.Document), AppLauncher.LinkArgument([.. info.ArgumentList]));

        // Nothing to start: it says how to set it up.
        w.Hub.Launcher = new AppLauncher(HubProtocol.LightWave) { BaseDirectory = Path.Combine(_folder, "none", "x"), SearchPath = "", Start = _ => true };
        Assert.Contains("couldn't be found", Wait(w.Hub.SendToLightWave()));
    }
}
