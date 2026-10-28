using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// The live app's door for agents: a local named pipe (a Unix domain socket on Linux and macOS) that only the current
/// user can open, so no token is needed and nothing outside the machine can connect. Each connection begins with a
/// one-line handshake (<see cref="LiveProtocol"/>) carrying the agent's working folder, then carries an MCP session
/// served by <see cref="ModelerServer"/> against the open document through a <see cref="LiveHost"/>.
/// </summary>
public sealed class LiveListener : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<string, IModelerHost> _hostFor;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _sessions = [];
    private readonly Lock _lock = new();
    private Task? _loop;
    private NamedPipeServerStream? _waiting;
    private readonly HashSet<NamedPipeServerStream> _connected = [];
    private int _count;

    /// <param name="pipeName">The pipe's name (see <see cref="LiveProtocol.PipeName"/>).</param>
    /// <param name="hostFor">Makes the host for a connection, given the working folder its agent asked for.</param>
    public LiveListener(string pipeName, Func<string, IModelerHost> hostFor)
    {
        _pipeName = pipeName;
        _hostFor = hostFor;
    }

    /// <summary>Raised (on a worker thread) when an agent connects or goes, with the number now connected.</summary>
    public event Action<int>? ConnectionsChanged;

    public int Connections => Volatile.Read(ref _count);

    /// <summary>
    /// Starts listening. Throws <see cref="IOException"/> if another AVA Modeler is already listening on the pipe (a
    /// second server on a Unix socket would quietly take its place) or the pipe can't be made.
    /// </summary>
    public void Start()
    {
        if (_loop != null) return;
        if (Answers(_pipeName)) throw new IOException("Another AVA Modeler is already taking AI agents on this desktop.");
        var first = Next() ?? throw new ObjectDisposedException(nameof(LiveListener));
        _loop = Task.Run(() => AcceptLoop(first, _stop.Token));
    }

    /// <summary>Whether something is listening on a pipe already (tried for a moment, then let go).</summary>
    public static bool Answers(string pipeName)
    {
        using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            probe.Connect(150);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// A new server end of the pipe to wait for the next agent on, or null once stopped. It's made under the lock
    /// <see cref="Stop"/> takes, so no pipe is left listening after a stop.
    /// </summary>
    private NamedPipeServerStream? Next()
    {
        lock (_lock)
        {
            if (_stop.IsCancellationRequested) return null;
            return _waiting = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
    }

    private async Task AcceptLoop(NamedPipeServerStream? pipe, CancellationToken cancel)
    {
        while (pipe != null)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                pipe = Next();
                continue;
            }
            var connected = pipe;
            lock (_lock)
            {
                if (_waiting == connected) _waiting = null;
                if (_stop.IsCancellationRequested) { connected.Dispose(); break; }
                _connected.Add(connected);
                _sessions.RemoveAll(t => t.IsCompleted);
                _sessions.Add(Task.Run(() => Serve(connected, cancel)));
            }
            pipe = Next();
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        try
        {
            await ServeOne(pipe, cancel).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _connected.Remove(pipe);
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeOne(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        string root;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var hello = await LiveProtocol.ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (LiveProtocol.Check(hello, out root) is { } refusal)
            {
                await LiveProtocol.WriteLineAsync(pipe, new JsonObject { ["error"] = refusal }, cancel).ConfigureAwait(false);
                return;
            }
            await LiveProtocol.WriteLineAsync(pipe, new JsonObject { ["ok"] = true, ["server"] = ModelerServer.Name, ["version"] = LiveProtocol.Version }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        ConnectionsChanged?.Invoke(Interlocked.Increment(ref _count));
        try
        {
            var transport = new StreamServerTransport(pipe, pipe, ModelerServer.Name);
            await ModelerServer.RunAsync(transport, _hostFor(root), cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The agent went away, or the app is closing (the pipe may have been closed under the session).
        }
        finally
        {
            ConnectionsChanged?.Invoke(Interlocked.Decrement(ref _count));
        }
    }

    /// <summary>
    /// Stops taking connections and ends every session at once: the pipe is closed before this returns, so another
    /// listener can start on it straight away.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_stop.IsCancellationRequested) _stop.Cancel();
            _waiting?.Dispose();
            _waiting = null;
            // Every end of the pipe goes: on Unix the listening socket stays open while any of them is alive.
            foreach (var pipe in _connected) pipe.Dispose();
            _connected.Clear();
        }
    }

    /// <summary>Stops listening and ends every session.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_loop != null) await _loop.ConfigureAwait(false);
        Task[] sessions;
        lock (_lock) sessions = [.. _sessions];
        await Task.WhenAny(Task.WhenAll(sessions), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        _stop.Dispose();
    }
}

/// <summary>
/// The live connection's handshake: before the MCP stream, the bridge sends one line of JSON naming the protocol and
/// its working folder; the app answers with one line, {"ok": true} or {"error": "why"}.
/// </summary>
public static class LiveProtocol
{
    public const int Version = 1;

    /// <summary>
    /// The pipe the live app listens on: per user, so two people on one machine each reach their own app. The
    /// AVAMODELER_MCP_PIPE environment variable can name another (both the app and the bridge read it).
    /// </summary>
    public static string PipeName =>
        Environment.GetEnvironmentVariable("AVAMODELER_MCP_PIPE") is { Length: > 0 } custom ? custom
            : "AVAModeler-mcp-" + new string(Environment.UserName.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray());

    public static JsonObject Hello(string root) => new() { ["protocol"] = "avamodeler-mcp", ["version"] = Version, ["root"] = root };

    /// <summary>Why a hello can't be accepted, or null (with the working folder it names).</summary>
    public static string? Check(string line, out string root)
    {
        root = "";
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return "The handshake wasn't JSON."; }
        if (node is not JsonObject o || (string?)o["protocol"] != "avamodeler-mcp") return "This pipe speaks the AVA Modeler MCP protocol only.";
        if ((int?)o["version"] != Version) return $"This AVA Modeler speaks version {Version} of the live protocol; update the one running the bridge to match.";
        root = (string?)o["root"] ?? "";
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return $"The working folder \"{root}\" doesn't exist.";
        return null;
    }

    /// <summary>Reads one line (up to a newline), byte by byte so nothing after it is taken from the stream.</summary>
    public static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancel)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one, cancel).ConfigureAwait(false);
            if (n == 0) throw new IOException("The other end closed the connection.");
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
            if (bytes.Count > 65536) throw new IOException("The handshake line is too long.");
        }
    }

    public static async Task WriteLineAsync(Stream stream, JsonObject message, CancellationToken cancel)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"), cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }
}
