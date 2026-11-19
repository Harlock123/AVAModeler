using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>
/// The network door: the MCP server over Streamable HTTP with a token, reached by the SDK's own HTTP client on
/// localhost (port 0, so the system picks a free one): every tool there, sessions of their own sharing one document,
/// a session's locks going when it ends, and requests without the token or from another site's page refused.
/// </summary>
public sealed class NetworkTests : IAsyncLifetime
{
    private const string Token = "a-test-token-0123456789";
    private readonly TempFolder _folder = new();
    private HeadlessHost _host = null!;
    private NetworkListener _listener = null!;
    private readonly List<McpClient> _clients = [];

    public async ValueTask InitializeAsync()
    {
        _host = new HeadlessHost(_folder.Path);
        _listener = Listener(new NetworkSettings("localhost", 0, Token));
        await _listener.StartAsync(Cancel);
    }

    private NetworkListener Listener(NetworkSettings settings) =>
        new(settings, ServerProfile.Modeler.Name, o => ModelerServer.Configure(o, _host, ServerProfile.Modeler), ModelerServer.EndSessionAsync);

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync();
        await _listener.DisposeAsync();
        _folder.Dispose();
    }

    private string Url => $"http://localhost:{_listener.Port}{NetworkListener.Path}";

    private async Task<McpClient> Client(string token = Token, string? protocol = "2025-11-25", string? url = null, HttpClient? http = null)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(url ?? Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        };
        var transport = http != null ? new HttpClientTransport(options, http) : new HttpClientTransport(options);
        var client = await McpClient.CreateAsync(transport, protocol == null ? null : new McpClientOptions { ProtocolVersion = protocol }, null, Cancel);
        _clients.Add(client);
        return client;
    }

    [Fact]
    public async Task An_agent_on_the_network_gets_every_tool_and_builds_in_the_document()
    {
        var client = await Client();
        var tools = await client.ListToolsAsync(cancellationToken: Cancel);
        Assert.Equal(ModelerServer.Definitions().Select(t => t.Name).Order(), tools.Select(t => t.Name).Order());
        Assert.Equal(ServerProfile.Modeler.Name, client.ServerInfo?.Name);
        await client.Json("run_tool", new { name = "Box" });
        var doc = await client.Json("get_document");
        Assert.Equal(6, (int)doc["layers"]![0]!["polygons"]!);
        Assert.Equal(1, _listener.Connections);
    }

    [Fact]
    public async Task A_client_on_the_newest_protocol_is_served_without_a_session()
    {
        var client = await Client(protocol: null);
        await client.Json("run_tool", new { name = "Ball" });
        Assert.True((await _host.RunAsync("count", c => c.Document.Current.Mesh.Polygons.Count, Cancel)) > 20);
    }

    [Fact]
    public async Task Two_agents_have_sessions_of_their_own_and_a_lock_goes_when_its_session_ends()
    {
        var first = await Client();
        var second = await Client();
        Assert.Equal(2, _listener.Connections);
        await first.Json("run_tool", new { name = "Box" });
        await first.Json("select_all");
        await first.Json("lock", new { name = "box" });
        var refused = await second.Call("run_tool", new { name = "Move", @params = new { Offset = new[] { 0, 1, 0 } } });
        Assert.True(refused.IsError);
        Assert.Contains("\"box\"", Calls.Text(refused));
        var agents = await second.Json("list_agents");
        Assert.Equal(2, agents["agents"]!.AsArray().Count);

        // The first agent goes: its session ends and its lock with it.
        _clients.Remove(first);
        await first.DisposeAsync();
        for (int i = 0; i < 200 && _listener.Connections > 1; i++) await Task.Delay(20, Cancel);
        Assert.Equal(1, _listener.Connections);
        await second.Json("select_all");
        await second.Json("run_tool", new { name = "Move", @params = new { Offset = new[] { 0, 1, 0 } } });
        Assert.Empty((await second.Json("list_locks"))["locks"]!.AsArray());
    }

    [Fact]
    public async Task Without_the_right_token_nothing_gets_in()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Client("not-the-token-at-all"));
        using var http = new HttpClient();
        var initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";
        var bare = await http.SendAsync(Post(initialize), Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
        Assert.StartsWith("Bearer", bare.Headers.WwwAuthenticate.ToString());
        var wrong = Post(initialize);
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong, Cancel)).StatusCode);

        // A web page on another site, even one that somehow had the token, is refused.
        var page = Post(initialize);
        page.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        page.Headers.Add("Origin", "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(page, Cancel)).StatusCode);

        var right = Post(initialize);
        right.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(right, Cancel)).StatusCode);
    }

    private HttpRequestMessage Post(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    [Fact]
    public async Task Bad_settings_and_a_port_in_use_are_said_plainly()
    {
        Assert.Contains("16 characters", new NetworkSettings("0.0.0.0", 8765, "short").Problem());
        Assert.Contains("1 to 65535", new NetworkSettings("0.0.0.0", 70000, Token).Problem());
        Assert.Contains("isn't an address", new NetworkSettings("not an address", 8765, Token).Problem());
        Assert.Contains("private key", new NetworkSettings("0.0.0.0", 8765, Token, _folder.File("c.pem")).Problem());
        Assert.Null(new NetworkSettings("*", 8765, Token).Problem());

        await using var second = Listener(new NetworkSettings("localhost", _listener.Port, Token));
        var e = await Assert.ThrowsAsync<IOException>(() => second.StartAsync(Cancel));
        Assert.Contains($"port {_listener.Port} is already in use", e.Message);
    }

    [Fact]
    public async Task With_a_certificate_it_serves_https()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        File.WriteAllText(_folder.File("cert.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(_folder.File("key.pem"), key.ExportPkcs8PrivateKeyPem());
        await using var secure = Listener(new NetworkSettings("localhost", 0, Token, _folder.File("cert.pem"), _folder.File("key.pem")));
        await secure.StartAsync(Cancel);
        Assert.StartsWith("https://", Assert.Single(secure.Urls));

        // The test trusts its own self-signed certificate (a real client would be given it, or a CA's).
        var thumbprint = certificate.Thumbprint;
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == thumbprint });
        var client = await Client(url: $"https://localhost:{secure.Port}{NetworkListener.Path}", http: http);
        await client.Json("run_tool", new { name = "Box" });
    }

    [Fact]
    public void Endpoints_on_the_command_line_are_read_as_address_and_port()
    {
        Assert.True(McpCommandLine.TryParseEndpoint("9000", 8765, out var a, out var p));
        Assert.Equal(("0.0.0.0", 9000), (a, p));
        Assert.True(McpCommandLine.TryParseEndpoint("localhost:9001", 8765, out a, out p));
        Assert.Equal(("localhost", 9001), (a, p));
        Assert.True(McpCommandLine.TryParseEndpoint("192.168.1.20", 8765, out a, out p));
        Assert.Equal(("192.168.1.20", 8765), (a, p));
        Assert.True(McpCommandLine.TryParseEndpoint("[::1]:9002", 8765, out a, out p));
        Assert.Equal(("[::1]", 9002), (a, p));
        Assert.False(McpCommandLine.TryParseEndpoint("localhost:99999", 8765, out _, out _));
        Assert.False(McpCommandLine.TryParseEndpoint("no such place", 8765, out _, out _));
    }

    /// <summary>The built app as <c>AVAModeler --mcp --http</c>: it says where it listens, serves a client there, and stops.</summary>
    [Fact]
    public async Task The_built_app_serves_over_http_until_stopped()
    {
        string dll = EndToEndTests.BuiltApp();
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = _folder.Path, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var a in new[] { dll, "--mcp", "--no-plugins", "--http", $"localhost:{port}" }) start.ArgumentList.Add(a);
        start.Environment["AVAMODELER_MCP_TOKEN"] = Token;
        using var process = Process.Start(start)!;
        var said = new List<string>();
        var ready = new TaskCompletionSource();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (said) said.Add(e.Data);
            if (e.Data.Contains("Ctrl+C stops it")) ready.TrySetResult();
        };
        process.BeginErrorReadLine();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), Cancel);
            lock (said)
            {
                Assert.Contains(said, l => l.Contains($"http://127.0.0.1:{port}/mcp"));
                Assert.DoesNotContain(said, l => l.Contains(Token));
            }
            var client = await Client(url: $"http://127.0.0.1:{port}/mcp");
            await client.Json("run_tool", new { name = "Box" });
            Assert.Equal(6, (int)(await client.Json("get_document"))["layers"]![0]!["polygons"]!);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }

    [Fact]
    public void The_token_is_kept_in_an_owner_only_file_and_renewed_on_asking()
    {
        var store = NetworkToken.In(_folder.Path);
        var token = store.Get();
        Assert.True(token.Length >= 43);
        Assert.Equal(token, store.Get());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
        Assert.NotEqual(token, store.Renew());
        Assert.NotEqual(NetworkListener.NewToken(), NetworkListener.NewToken());
    }
}
