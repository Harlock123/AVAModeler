using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AVAModeler.Hub.Tests;

/// <summary>
/// The hub's wire, in one process on pipes of the tests' own: messages and answers, refusals, a second listener, an app
/// that isn't there, the hand-over folder, and finding the other program (never started for real).
/// </summary>
public class HubTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;
    private static string Pipe() => "avahub-test-" + Guid.NewGuid().ToString("N")[..12];
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avahub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task A_message_is_answered_by_the_listener()
    {
        var pipe = Pipe();
        var got = new List<HubMessage>();
        await using var listener = new HubListener(pipe, m =>
        {
            lock (got) got.Add(m);
            return Task.FromResult(HubProtocol.Ok(new JsonObject { ["app"] = HubProtocol.LightWave }));
        });
        listener.Start();
        Assert.True(listener.Running);
        var path = Path.Combine(Path.GetTempPath(), "ship.lwo");
        var reply = await HubClient.SendAsync(pipe, new HubMessage(HubMessage.ObjectSaved, HubProtocol.Modeler, path, "abc123", "Ship"), Wait, Cancel);
        Assert.Equal(HubProtocol.LightWave, (string?)reply["app"]);
        var m = Assert.Single(got);
        Assert.Equal(new HubMessage(HubMessage.ObjectSaved, HubProtocol.Modeler, path, "abc123", "Ship"), m);
    }

    [Fact]
    public async Task A_refusal_comes_back_as_its_reason()
    {
        var pipe = Pipe();
        await using var listener = new HubListener(pipe, _ => Task.FromResult(HubProtocol.Error("AVA Modeler has unsaved changes.")));
        listener.Start();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => HubClient.SendAsync(pipe, new HubMessage(HubMessage.Hello, "test"), Wait, Cancel));
        Assert.Equal("AVA Modeler has unsaved changes.", e.Message);
        // A handler's own failure is an answer too, not a dropped connection.
        await using var failing = new HubListener(Pipe(), _ => throw new IOException("disk full"));
        failing.Start();
        Assert.Equal("disk full", (await Assert.ThrowsAsync<InvalidOperationException>(() => HubClient.SendAsync(failing.PipeName, new HubMessage(HubMessage.Hello, "test"), Wait, Cancel))).Message);
    }

    [Fact]
    public async Task Bad_messages_are_refused_without_reaching_the_app()
    {
        int calls = 0;
        var pipe = Pipe();
        await using var listener = new HubListener(pipe, _ => { Interlocked.Increment(ref calls); return Task.FromResult(HubProtocol.Ok()); });
        listener.Start();
        var relative = new HubMessage(HubMessage.EditObject, "test", "ship.lwo");
        Assert.Contains("full paths", (await Assert.ThrowsAsync<InvalidOperationException>(() => HubClient.SendAsync(pipe, relative, Wait, Cancel))).Message);
        Assert.Throws<FormatException>(() => HubMessage.Parse("not json"));
        Assert.Throws<FormatException>(() => HubMessage.Parse("{\"protocol\":\"avamodeler-mcp\",\"version\":1}"));
        Assert.Contains("version", Assert.Throws<FormatException>(() => HubMessage.Parse("{\"protocol\":\"ava-hub\",\"version\":99,\"type\":\"hello\"}")).Message);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Only_one_listener_takes_a_pipe_and_it_can_start_again_after_stopping()
    {
        var pipe = Pipe();
        var first = new HubListener(pipe, _ => Task.FromResult(HubProtocol.Ok()));
        first.Start();
        await using (var second = new HubListener(pipe, _ => Task.FromResult(HubProtocol.Ok())))
            Assert.Throws<IOException>(second.Start);
        await first.DisposeAsync();
        Assert.False(HubClient.Answers(pipe));
        await using var again = new HubListener(pipe, _ => Task.FromResult(HubProtocol.Ok()));
        again.Start();
        Assert.True(HubClient.Answers(pipe));
    }

    [Fact]
    public async Task An_app_that_isnt_there_is_said_plainly_and_quickly()
    {
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<HubUnavailableException>(() => HubClient.SendAsync(Pipe(), new HubMessage(HubMessage.Hello, "test"), TimeSpan.FromMilliseconds(300), Cancel));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(HubClient.Answers(Pipe()));
    }

    [Fact]
    public void Pipe_names_are_per_app_and_per_user()
    {
        var m = HubProtocol.PipeName(HubProtocol.Modeler);
        var l = HubProtocol.PipeName(HubProtocol.LightWave);
        Assert.NotEqual(m, l);
        Assert.StartsWith("AVAModeler-hub-", m);
        Assert.StartsWith("AVALightWave-hub-", l);
        Assert.All(m + l, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    [Fact]
    public void Handed_over_objects_go_in_an_owner_only_folder_named_by_object_and_link()
    {
        var settings = Temp();
        try
        {
            var folder = HubFiles.Folder(settings);
            Assert.True(Directory.Exists(folder));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
            var link = HubFiles.NewLink();
            Assert.Equal(32, link.Length);
            var path = HubFiles.PathFor(folder, "My ship/../x", link);
            Assert.Equal(folder, Path.GetDirectoryName(path));
            Assert.Equal($"My_ship____x-{link[..8]}.lwo", Path.GetFileName(path));
            Assert.Equal($"object-{link[..8]}.lwo", Path.GetFileName(HubFiles.PathFor(folder, "///", link)));
        }
        finally { Directory.Delete(settings, true); }
    }

    [Fact]
    public void The_other_program_is_found_by_setting_beside_this_one_or_on_the_path_and_started_with_its_file()
    {
        var root = Temp();
        try
        {
            var me = Directory.CreateDirectory(Path.Combine(root, "AVALightWave")).FullName;
            var name = OperatingSystem.IsWindows() ? "AVAModeler.exe" : "AVAModeler";
            var started = new List<ProcessStartInfo>();
            var launcher = new AppLauncher(HubProtocol.Modeler) { BaseDirectory = me, SearchPath = "", Start = i => { started.Add(i); return true; } };
            Assert.Null(launcher.Find());
            Assert.Null(launcher.Launch(null, "/tmp/x.lwo", "abc"));
            Assert.Empty(started);

            // In a sibling folder named after it.
            var sibling = Directory.CreateDirectory(Path.Combine(root, "AVAModeler")).FullName;
            File.WriteAllText(Path.Combine(sibling, name), "");
            Assert.Equal(Path.Combine(sibling, name), launcher.Find());
            // Beside this program, first.
            File.WriteAllText(Path.Combine(me, name), "");
            Assert.Equal(Path.Combine(me, name), launcher.Find());
            // A setting, before both: the file, or its folder.
            var chosen = Directory.CreateDirectory(Path.Combine(root, "chosen")).FullName;
            File.WriteAllText(Path.Combine(chosen, name), "");
            Assert.Equal(Path.Combine(chosen, name), launcher.Find(chosen));
            Assert.Equal(Path.Combine(chosen, name), launcher.Find(Path.Combine(chosen, name)));

            var file = Path.Combine(root, "ship.lwo");
            Assert.Equal(Path.Combine(chosen, name), launcher.Launch(chosen, file, "abc"));
            var info = Assert.Single(started);
            Assert.Equal(Path.Combine(chosen, name), info.FileName);
            Assert.Equal([file, "--hub-link", "abc"], info.ArgumentList);
            Assert.False(info.UseShellExecute);
            Assert.Equal("abc", AppLauncher.LinkArgument(info.ArgumentList.ToList()));
            Assert.Null(AppLauncher.LinkArgument(["ship.lwo"]));

            // A development build: the .dll, run with dotnet.
            var dev = Directory.CreateDirectory(Path.Combine(root, "dev")).FullName;
            File.WriteAllText(Path.Combine(dev, "AVAModeler.dll"), "");
            var onPath = new AppLauncher(HubProtocol.Modeler) { BaseDirectory = Path.Combine(dev, "elsewhere", "bin"), SearchPath = dev, Start = i => { started.Add(i); return true; } };
            Assert.Equal(Path.Combine(dev, "AVAModeler.dll"), onPath.Find());
            onPath.Launch(null, null, null);
            Assert.Equal("dotnet", started[^1].FileName);
            Assert.Equal([Path.Combine(dev, "AVAModeler.dll")], started[^1].ArgumentList);
        }
        finally { Directory.Delete(root, true); }
    }
}
