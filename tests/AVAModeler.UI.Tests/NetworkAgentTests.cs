using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVAModeler.Mcp;
using AVAModeler.Modeler.UI;
using ModelContextProtocol.Client;

namespace AVAModeler.UI.Tests;

/// <summary>
/// The window's network door: the preference opens it on a port (the system's choice here, on localhost), an MCP
/// client with the token builds in the open object as a local agent does (Ctrl+Z undoes it), a new token or a
/// change of settings reopens it, and turning it off closes it. The Preferences window shows its settings.
/// </summary>
public class NetworkAgentTests
{
    private static McpClient Connect(int port, string token) =>
        Agent.Wait(McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }), new McpClientOptions { ProtocolVersion = "2025-11-25" }, null, TestContext.Current.CancellationToken));

    private static void Sync(MainWindow w) => Agent.Wait(w.SyncNetworkAsync().ContinueWith(_ => 0, TaskScheduler.Default));

    [AvaloniaFact]
    public void An_agent_on_the_network_builds_in_the_open_window_with_the_token()
    {
        var folder = Directory.CreateTempSubdirectory("avamodeler-net-").FullName;
        var w = Ui.Open();
        try
        {
            var p = w.Session.Preferences;
            p.AllowNetworkAgents = true;
            p.NetworkAgentsAddress = "localhost";
            p.NetworkAgentsPort = 0;
            p.NetworkAgentsFolder = folder;
            Sync(w);
            Assert.True(w.Agents.NetworkRunning, w.Session.Status);
            Assert.Contains("AI agents on other computers may connect", w.Session.Status);
            Assert.Contains($":{w.Agents.NetworkPort}/mcp", Assert.Single(w.Network.Urls));

            var client = Connect(w.Agents.NetworkPort, w.Network.Token);
            Agent.Json(client, "run_tool", new { name = "Box" });
            Ui.Pump();
            Assert.Equal(6, w.Session.Document.Current.Mesh.Polygons.Count);
            w.Modeler.Execute("Undo");
            Assert.True(w.Session.Document.Current.Mesh.IsEmpty);
            Agent.Wait(client.DisposeAsync().AsTask().ContinueWith(_ => 0, TaskScheduler.Default));

            // A new token reopens the door: the old one no longer gets in, the new one does.
            string old = w.Network.Token;
            w.Network.RenewToken();
            Sync(w);
            Assert.True(w.Agents.NetworkRunning);
            Assert.NotEqual(old, w.Network.Token);
            Assert.ThrowsAny<Exception>(() => Connect(w.Agents.NetworkPort, old));
            var again = Connect(w.Agents.NetworkPort, w.Network.Token);
            Agent.Json(again, "get_document");
            Agent.Wait(again.DisposeAsync().AsTask().ContinueWith(_ => 0, TaskScheduler.Default));

            p.AllowNetworkAgents = false;
            Sync(w);
            Assert.False(w.Agents.NetworkRunning);
            Assert.Contains("can no longer connect", w.Session.Status);
        }
        finally
        {
            w.Close();
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public void A_door_that_cant_open_says_why()
    {
        var w = Ui.Open();
        var p = w.Session.Preferences;
        p.AllowNetworkAgents = true;
        p.NetworkAgentsAddress = "localhost";
        p.NetworkAgentsFolder = Path.Combine(Path.GetTempPath(), "avamodeler-no-such-folder-" + Guid.NewGuid().ToString("N"));
        Sync(w);
        Assert.False(w.Agents.NetworkRunning);
        Assert.Contains("can't connect", w.Session.Status);
        Assert.Contains("doesn't exist", w.Session.Status);
        w.Close();
    }

    [AvaloniaFact]
    public void Preferences_show_the_network_settings_and_keep_them()
    {
        var w = Ui.Open();
        var prefs = new PreferencesWindow(w.Session, () => { });
        prefs.Show();
        Ui.Pump();
        var boxes = prefs.GetLogicalDescendants().OfType<CheckBox>().ToList();
        var network = boxes.Single(b => (b.Content as string)?.StartsWith("Allow AI agents on other computers") == true);
        Assert.False(network.IsChecked);
        var texts = prefs.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        string token = w.Network.Token;
        Assert.Contains(PreferencesWindow.MaskToken(token), texts);
        Assert.DoesNotContain(token, texts);

        network.IsChecked = true;
        var port = prefs.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Text == "8765");
        port.Text = "9123";
        prefs.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Save")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(w.Session.Preferences.AllowNetworkAgents);
        Assert.Equal(9123, w.Session.Preferences.NetworkAgentsPort);
        Agent.Wait(w.Agents.StopNetworkAsync().ContinueWith(_ => 0, TaskScheduler.Default));
        w.Close();
    }
}
