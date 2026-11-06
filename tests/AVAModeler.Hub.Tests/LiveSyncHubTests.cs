using System.Text.Json.Nodes;

namespace AVAModeler.Hub.Tests;

/// <summary>
/// Live sync's part of the wire: the <c>object_changed</c> message on a pipe and as a line of JSON (files by full path
/// only), the one file per link, and writing it whole so the other app never reads half of it.
/// </summary>
public class LiveSyncHubTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avahub-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Object_changed_goes_over_the_pipe_with_its_file_link_and_name()
    {
        var pipe = "avahub-live-" + Guid.NewGuid().ToString("N")[..12];
        var got = new List<HubMessage>();
        await using var listener = new HubListener(pipe, m =>
        {
            lock (got) got.Add(m);
            return Task.FromResult(HubProtocol.Ok(new JsonObject { ["updated"] = 1 }));
        });
        listener.Start();
        var path = Path.Combine(Path.GetTempPath(), "hub", "live-abc.lwo");
        var sent = new HubMessage(HubMessage.ObjectChanged, HubProtocol.Modeler, path, "abc", "Pawn");
        var reply = await HubClient.SendAsync(pipe, sent, TimeSpan.FromSeconds(5), Cancel);
        Assert.Equal(1, (int?)reply["updated"]);
        Assert.Equal(sent, Assert.Single(got));
    }

    [Fact]
    public void Object_changed_parses_and_round_trips_and_names_files_by_full_path_only()
    {
        Assert.Equal("object_changed", HubMessage.ObjectChanged);
        var path = Path.Combine(Path.GetTempPath(), "live-abc.lwo");
        var m = new HubMessage(HubMessage.ObjectChanged, HubProtocol.Modeler, path, "abc", "Pawn");
        var json = m.ToJson();
        Assert.Equal("object_changed", (string?)json["type"]);
        Assert.Equal(m, HubMessage.Parse(json.ToJsonString()));
        // Nothing but the file, the link and the name: no geometry on the pipe.
        Assert.Equal(["protocol", "version", "type", "from", "path", "link", "name"], json.Select(kv => kv.Key));
        var relative = new JsonObject { ["protocol"] = HubProtocol.Name, ["version"] = HubProtocol.Version, ["type"] = HubMessage.ObjectChanged, ["from"] = HubProtocol.Modeler, ["path"] = "live-abc.lwo" };
        Assert.Throws<FormatException>(() => HubMessage.Parse(relative.ToJsonString()));
    }

    [Fact]
    public void Each_link_has_one_live_file_named_safely()
    {
        var folder = Temp();
        try
        {
            var a = HubFiles.LivePathFor(folder, "0123456789abcdef0123456789abcdef");
            Assert.Equal(Path.Combine(folder, "live-0123456789abcdef0123456789abcdef.lwo"), a);
            Assert.Equal(a, HubFiles.LivePathFor(folder, "0123456789abcdef0123456789abcdef"));
            Assert.NotEqual(a, HubFiles.LivePathFor(folder, "another"));
            // A link from the other app can't climb out of the folder.
            var odd = HubFiles.LivePathFor(folder, "../../etc/passwd");
            Assert.Equal(folder, Path.GetDirectoryName(odd));
            Assert.Equal("live-etcpasswd.lwo", Path.GetFileName(odd));
            Assert.Equal("live-object.lwo", Path.GetFileName(HubFiles.LivePathFor(folder, "///")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_file_is_written_whole_or_not_at_all()
    {
        var folder = Temp();
        try
        {
            var path = Path.Combine(folder, "live-x.lwo");
            string? temp = null;
            HubFiles.WriteWhole(path, t =>
            {
                temp = t;
                // Written beside it, with the same extension (which picks the format), under another name.
                Assert.Equal(folder, Path.GetDirectoryName(t));
                Assert.Equal(".lwo", Path.GetExtension(t));
                Assert.False(File.Exists(path));
                File.WriteAllText(t, "first");
            });
            Assert.Equal("first", File.ReadAllText(path));
            Assert.False(File.Exists(temp));
            HubFiles.WriteWhole(path, t => File.WriteAllText(t, "second"));
            Assert.Equal("second", File.ReadAllText(path));
            // A write that fails leaves the last whole file, and no temporary one.
            Assert.Throws<IOException>(() => HubFiles.WriteWhole(path, t =>
            {
                File.WriteAllText(t, "half");
                throw new IOException("disk full");
            }));
            Assert.Equal("second", File.ReadAllText(path));
            Assert.Equal([path], Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }
}
