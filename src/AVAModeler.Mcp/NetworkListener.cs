using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// Where and how the network door listens: an address (<c>0.0.0.0</c> or <c>*</c> for every network the computer is
/// on, <c>localhost</c>, or one of its own addresses) and a port, the bearer token every request must carry, and
/// optionally a certificate and its key (PEM files) to serve HTTPS rather than plain HTTP.
/// </summary>
public sealed record NetworkSettings(string Address, int Port, string Token, string? CertificateFile = null, string? KeyFile = null)
{
    /// <summary>Whether it serves HTTPS.</summary>
    public bool Secure => !string.IsNullOrWhiteSpace(CertificateFile);

    /// <summary>
    /// Why these settings can't be used, or null: a port out of range, an address that isn't one, a token too short to
    /// be safe, or a certificate without its key or a file that isn't there.
    /// </summary>
    public string? Problem()
    {
        if (Port is < 0 or > 65535) return $"The port must be from 1 to 65535 (not {Port}).";
        if (NetworkListener.ParseAddress(Address) == null) return $"\"{Address}\" isn't an address to listen on: use 0.0.0.0 (every network), localhost, or one of this computer's addresses.";
        if (Token.Length < 16) return "The access token must be at least 16 characters long.";
        if (Secure)
        {
            if (string.IsNullOrWhiteSpace(KeyFile)) return "HTTPS needs the certificate's private key too (a PEM file).";
            if (!File.Exists(CertificateFile)) return $"The certificate file {CertificateFile} doesn't exist.";
            if (!File.Exists(KeyFile)) return $"The key file {KeyFile} doesn't exist.";
        }
        return null;
    }
}

/// <summary>
/// The door for AI agents on other computers: the MCP Streamable HTTP transport (the official SDK's ASP.NET Core
/// handler on Kestrel) at <c>/mcp</c>, so any MCP client on the network can connect with just a URL and a token.
/// <para>
/// Unlike the local pipe, which only the current user can open, anything on the network can reach a port, so every
/// request must carry <c>Authorization: Bearer &lt;token&gt;</c> (compared in constant time); a request from a web page
/// on another site (an <c>Origin</c> header naming another host) is refused, so a browser can't be used to reach it.
/// Clients using the initialize handshake get a session of their own, with everything the pipe gives (locks, saved
/// selections, checkpoints, elicitation, subscriptions); clients on the 2026-07-28 revision or later are served each
/// request on its own, as that revision has no sessions.
/// </para>
/// It knows nothing of AVA Modeler: <c>configure</c> fills each new session's server options and <c>end</c> is told when
/// that session is over.
/// </summary>
public sealed class NetworkListener : IAsyncDisposable
{
    /// <summary>The path the MCP endpoint is served at.</summary>
    public const string Path = "/mcp";

    private readonly NetworkSettings _settings;
    private readonly string _serverName;
    private readonly Action<McpServerOptions> _configure;
    private readonly Func<McpServerOptions, Task>? _end;
    private readonly byte[] _token;
    private WebApplication? _app;
    private int _count;

    /// <param name="settings">Where to listen and the token to ask for.</param>
    /// <param name="serverName">The server's name, for the WWW-Authenticate realm.</param>
    /// <param name="configure">Fills a new session's server options (its info, tools, handlers and filters).</param>
    /// <param name="end">Told when a session made with those options is over.</param>
    public NetworkListener(NetworkSettings settings, string serverName, Action<McpServerOptions> configure, Func<McpServerOptions, Task>? end = null)
    {
        _settings = settings;
        _serverName = serverName;
        _configure = configure;
        _end = end;
        _token = Encoding.UTF8.GetBytes(settings.Token);
    }

    /// <summary>Raised (on a worker thread) when a session starts or ends, with the number now running.</summary>
    public event Action<int>? ConnectionsChanged;

    public int Connections => Volatile.Read(ref _count);

    /// <summary>The port it listens on (the one asked for, or the one the system chose for port 0).</summary>
    public int Port { get; private set; }

    /// <summary>The addresses agents can use, once started: <c>http://192.168.1.20:8765/mcp</c> for each of the computer's own.</summary>
    public IReadOnlyList<string> Urls { get; private set; } = [];

    /// <summary>A new random access token: 32 bytes, base64url (43 characters).</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>An address to listen on, or null: <c>0.0.0.0</c>, <c>*</c> or empty is every network (IPv4 and IPv6).</summary>
    public static IPAddress? ParseAddress(string address)
    {
        var a = address.Trim();
        if (a is "" or "*" or "0.0.0.0" or "::" or "[::]") return IPAddress.IPv6Any;
        if (a.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        return IPAddress.TryParse(a.Trim('[', ']'), out var ip) ? ip : null;
    }

    /// <summary>
    /// Starts listening. Throws <see cref="IOException"/> with a plain reason when it can't: bad settings, the port
    /// already taken, or a certificate that won't load.
    /// </summary>
    public async Task StartAsync(CancellationToken cancel = default)
    {
        if (_app != null) return;
        if (_settings.Problem() is { } problem) throw new IOException(problem);
        X509Certificate2? certificate = null;
        if (_settings.Secure)
        {
            try
            {
                using var pem = X509Certificate2.CreateFromPemFile(_settings.CertificateFile!, _settings.KeyFile);
                // SslStream on Windows wants the key in a store-backed form; a PKCS#12 round trip gives it one.
                certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            }
            catch (Exception e) when (e is CryptographicException or IOException or ArgumentException)
            {
                throw new IOException($"The certificate couldn't be loaded: {e.Message}", e);
            }
        }

        var address = ParseAddress(_settings.Address)!;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(NetworkListener).Assembly.GetName().Name, ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
            void Https(ListenOptions l)
            {
                if (certificate != null) l.UseHttps(certificate);
            }
            if (address.Equals(IPAddress.IPv6Any)) k.ListenAnyIP(_settings.Port, Https);
            else k.Listen(address, _settings.Port, Https);
        });
        builder.Services.AddMcpServer().WithHttpTransport(t =>
        {
            t.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
            t.ConfigureSessionOptions = (_, options, _) =>
            {
                _configure(options);
                return Task.CompletedTask;
            };
            // Experimental in the SDK (MCPEXP002), and the only hook that sees a session end, which ends its locks.
#pragma warning disable MCPEXP002
            t.RunSessionHandler = async (_, server, token) =>
            {
                // Counted apart from the event: ?.Invoke would skip the count with no one listening.
                int now = Interlocked.Increment(ref _count);
                ConnectionsChanged?.Invoke(now);
                try
                {
                    await server.RunAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    if (_end != null) await _end(server.ServerOptions).ConfigureAwait(false);
                    int left = Interlocked.Decrement(ref _count);
                    ConnectionsChanged?.Invoke(left);
                }
            };
#pragma warning restore MCPEXP002
        });
        var app = builder.Build();
        app.Use(Guard);
        app.MapMcp(Path);
        try
        {
            await app.StartAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidOperationException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            var why = InUse(e) ? $"port {_settings.Port} is already in use (another program, or another copy of this one?)" : e.Message;
            throw new IOException($"Couldn't listen on {_settings.Address}:{_settings.Port}: {why}", e);
        }
        _app = app;
        Port = app.Urls.Select(u => Uri.TryCreate(u.Replace("://[::]", "://localhost").Replace("://+", "://localhost").Replace("://*", "://localhost"), UriKind.Absolute, out var uri) ? uri.Port : 0)
            .FirstOrDefault(p => p > 0, _settings.Port);
        Urls = UrlsFor(address, Port, _settings.Secure);
    }

    /// <summary>Whether a failure to listen is the port being taken (Kestrel's AddressInUseException, or the socket's own error).</summary>
    private static bool InUse(Exception? e)
    {
        for (; e != null; e = e.InnerException)
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } || e.GetType().Name == "AddressInUseException") return true;
        return false;
    }

    /// <summary>The URLs agents can be given for where it listens: each of the computer's own addresses for "every network".</summary>
    internal static IReadOnlyList<string> UrlsFor(IPAddress address, int port, bool secure)
    {
        string scheme = secure ? "https" : "http";
        string Url(string host) => $"{scheme}://{host}:{port}{Path}";
        if (!address.Equals(IPAddress.IPv6Any)) return [Url(address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString())];
        var own = new List<string>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)) own.Add(Url(a.Address.ToString()));
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        string name = Dns.GetHostName();
        if (name.Length > 0) own.Add(Url(name));
        return own.Count > 0 ? own.Distinct().ToList() : [Url("localhost")];
    }

    /// <summary>Every request: from no other site's web page, and with the token.</summary>
    private async Task Guard(HttpContext context, Func<Task> next)
    {
        var request = context.Request;
        if (request.Headers.Origin is [{ Length: > 0 } origin] && !SameHost(origin, request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Requests from web pages on other sites aren't accepted.").ConfigureAwait(false);
            return;
        }
        if (!Authorized(request.Headers.Authorization.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = $"Bearer realm=\"{_serverName}\"";
            await context.Response.WriteAsync("This MCP server needs its access token: send it as \"Authorization: Bearer <token>\".").ConfigureAwait(false);
            return;
        }
        await next().ConfigureAwait(false);
    }

    internal bool Authorized(string header)
    {
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var given = Encoding.UTF8.GetBytes(header[scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, _token);
    }

    private static bool SameHost(string origin, HostString host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var o) && string.Equals(o.Host.Trim('[', ']'), host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops listening and ends every session.</summary>
    public async ValueTask DisposeAsync()
    {
        var app = _app;
        _app = null;
        if (app == null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await app.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// The network door's access token, kept in a file of its own beside the preferences (readable only by the user on
/// Linux and macOS), made the first time it's asked for. Never in the preferences file, so sharing that gives nothing away.
/// </summary>
public sealed class NetworkToken(string path)
{
    public const string FileName = "mcp-network-token";

    public string Path { get; } = path;

    public static NetworkToken In(string folder) => new(System.IO.Path.Combine(folder, FileName));

    /// <summary>The token, made and saved if there isn't one yet.</summary>
    public string Get()
    {
        try
        {
            if (File.Exists(Path) && File.ReadAllText(Path).Trim() is { Length: >= 16 } saved) return saved;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Renew();
    }

    /// <summary>Makes a new token (the old one stops working when the door next opens), saves it and returns it.</summary>
    public string Renew()
    {
        var token = NetworkListener.NewToken();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (File.Exists(Path)) File.Delete(Path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(Path, options))
        using (var writer = new StreamWriter(stream)) writer.Write(token);
        return token;
    }
}
