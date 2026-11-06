using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AVAModeler.Hub;

/// <summary>
/// The hub's wire: each app that takes part listens on a local pipe of its own (a Unix socket on Linux and macOS, made
/// with <see cref="PipeOptions.CurrentUserOnly"/>, so only the same user's programs can open it and nothing outside the
/// machine can). A message is one line of JSON, answered with one line, then the connection closes. Objects travel as
/// files (their paths), never as data on the pipe.
/// <para>The messages (<see cref="HubMessage"/>): <c>hello</c> (who's there); <c>edit_object</c> (AVALightWave to AVA
/// Modeler: open this object to model it); <c>object_saved</c> (AVA Modeler to AVALightWave: this object was saved or
/// synced, so update it in the scene); <c>add_object</c> (AVA Modeler to AVALightWave: put this object in the scene);
/// <c>object_changed</c> (AVA Modeler to AVALightWave, live sync: the linked object changed in AVA Modeler and isn't
/// saved; a copy is at the path, rewritten in place each time, so show it in the scene as it is now). A link id ties an
/// object in AVALightWave's scene to the document AVA Modeler has open, so a save under a new name still finds its way
/// back.</para>
/// </summary>
public static class HubProtocol
{
    public const string Name = "ava-hub";
    public const int Version = 1;

    /// <summary>The apps that take part, as the hub names them.</summary>
    public const string Modeler = "AVAModeler", LightWave = "AVALightWave";

    /// <summary>
    /// An app's hub pipe: per user, so two people on one machine each reach their own. AVAMODELER_HUB_PIPE and
    /// AVALIGHTWAVE_HUB_PIPE can name others (both apps read both).
    /// </summary>
    public static string PipeName(string app) =>
        Environment.GetEnvironmentVariable(app.ToUpperInvariant() + "_HUB_PIPE") is { Length: > 0 } custom ? custom
            : app + "-hub-" + new string(Environment.UserName.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray());

    /// <summary>Reads one line (up to a newline), byte by byte, at most 64 KB.</summary>
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
            if (bytes.Count > 65536) throw new IOException("The hub message is too long.");
        }
    }

    public static async Task WriteLineAsync(Stream stream, JsonObject message, CancellationToken cancel)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"), cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>A reply saying it worked, with anything else to say.</summary>
    public static JsonObject Ok(JsonObject? more = null)
    {
        var o = new JsonObject { ["ok"] = true };
        if (more != null)
            foreach (var (k, v) in more) o[k] = v?.DeepClone();
        return o;
    }

    /// <summary>A reply saying why not.</summary>
    public static JsonObject Error(string why) => new() { ["ok"] = false, ["error"] = why };
}

/// <summary>One hub message: what it is, which app sent it, and (for objects) the file, the link and a name to show.</summary>
public sealed record HubMessage(string Type, string From, string? Path = null, string? Link = null, string? Name = null)
{
    public const string Hello = "hello", EditObject = "edit_object", ObjectSaved = "object_saved", AddObject = "add_object";

    /// <summary>
    /// Live sync (AVA Modeler to AVALightWave): the linked object (<see cref="Link"/>) changed and isn't saved; a copy is
    /// at <see cref="Path"/> (an LWOB in AVA Modeler's hub folder, one file per link, replaced whole each time), with the
    /// object's <see cref="Name"/>. Sent a moment after the changes stop, never more than one at a time.
    /// </summary>
    public const string ObjectChanged = "object_changed";

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["protocol"] = HubProtocol.Name, ["version"] = HubProtocol.Version, ["type"] = Type, ["from"] = From };
        if (Path != null) o["path"] = Path;
        if (Link != null) o["link"] = Link;
        if (Name != null) o["name"] = Name;
        return o;
    }

    /// <summary>A message from its line, or why it can't be taken.</summary>
    public static HubMessage Parse(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { throw new FormatException("The hub message wasn't JSON."); }
        if (node is not JsonObject o || (string?)o["protocol"] != HubProtocol.Name) throw new FormatException("This pipe speaks the AVA hub protocol only.");
        if ((int?)o["version"] != HubProtocol.Version) throw new FormatException($"This app speaks version {HubProtocol.Version} of the hub protocol; update the other to match.");
        string type = (string?)o["type"] ?? throw new FormatException("The hub message has no type.");
        string from = (string?)o["from"] ?? "";
        string? path = (string?)o["path"];
        if (path != null && !System.IO.Path.IsPathFullyQualified(path)) throw new FormatException("A hub message names files by their full paths.");
        return new HubMessage(type, from, path, (string?)o["link"], (string?)o["name"]);
    }
}

/// <summary>
/// An app's end of the hub: it listens on its pipe and answers each message with <paramref name="handle"/> (which may
/// run on any thread; an app hands the work to its UI thread). Like the MCP live listener, it refuses to start if another
/// copy of the app already has the pipe.
/// </summary>
public sealed class HubListener(string pipeName, Func<HubMessage, Task<JsonObject>> handle) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<Task> _served = [];
    private NamedPipeServerStream? _waiting;
    private Task? _loop;

    public string PipeName => pipeName;

    public bool Running => _loop != null && !_stop.IsCancellationRequested;

    /// <summary>Starts listening. Throws <see cref="IOException"/> if something already answers on the pipe.</summary>
    public void Start()
    {
        if (_loop != null) return;
        if (HubClient.Answers(pipeName)) throw new IOException("Another copy is already on the hub on this desktop.");
        var first = Next() ?? throw new ObjectDisposedException(nameof(HubListener));
        _loop = Task.Run(() => AcceptLoop(first, _stop.Token));
    }

    private NamedPipeServerStream? Next()
    {
        lock (_lock)
        {
            if (_stop.IsCancellationRequested) return null;
            return _waiting = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
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
                _served.RemoveAll(t => t.IsCompleted);
                _served.Add(Task.Run(() => Serve(connected, cancel)));
            }
            pipe = Next();
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        await using (pipe)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await HubProtocol.ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
                JsonObject reply;
                try
                {
                    reply = await handle(HubMessage.Parse(line)).ConfigureAwait(false);
                }
                catch (FormatException e) { reply = HubProtocol.Error(e.Message); }
                catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    reply = HubProtocol.Error(e.Message);
                }
                await HubProtocol.WriteLineAsync(pipe, reply, cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The other end went away, or this app is closing.
            }
        }
    }

    /// <summary>Stops listening; the pipe is gone when this returns.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_stop.IsCancellationRequested) _stop.Cancel();
            _waiting?.Dispose();
            _waiting = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_loop != null) await _loop.ConfigureAwait(false);
        Task[] served;
        lock (_lock) served = [.. _served];
        await Task.WhenAny(Task.WhenAll(served), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        _stop.Dispose();
    }
}

/// <summary>The other app wasn't there (not running, or not on the hub), or didn't answer.</summary>
public sealed class HubUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sends hub messages to another app's pipe.</summary>
public static class HubClient
{
    /// <summary>Whether something answers on a pipe (tried for a moment).</summary>
    public static bool Answers(string pipeName, int milliseconds = 150)
    {
        using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            probe.Connect(milliseconds);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends one message and returns the answer. Throws <see cref="HubUnavailableException"/> if nothing answers within
    /// <paramref name="timeout"/>, and <see cref="InvalidOperationException"/> with the other app's reason if it said no.
    /// </summary>
    public static async Task<JsonObject> SendAsync(string pipeName, HubMessage message, TimeSpan timeout, CancellationToken cancel = default)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        wait.CancelAfter(timeout);
        string answer;
        try
        {
            await pipe.ConnectAsync(wait.Token).ConfigureAwait(false);
            await HubProtocol.WriteLineAsync(pipe, message.ToJson(), wait.Token).ConfigureAwait(false);
            answer = await HubProtocol.ReadLineAsync(pipe, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancel.IsCancellationRequested)
        {
            throw new HubUnavailableException("The other app didn't answer on the hub.", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            throw new HubUnavailableException($"The other app isn't on the hub ({e.Message}).", e);
        }
        JsonObject reply;
        try { reply = JsonNode.Parse(answer) as JsonObject ?? throw new HubUnavailableException("The other app's answer wasn't understood."); }
        catch (JsonException e) { throw new HubUnavailableException("The other app's answer wasn't understood.", e); }
        if ((bool?)reply["ok"] != true) throw new InvalidOperationException((string?)reply["error"] ?? "The other app said no.");
        return reply;
    }
}

/// <summary>
/// Where an app puts an object it hands over that has no file of its own (or unsaved changes): a folder only the user
/// can open, in the app's settings folder. Files are named after the object and its link, so a second hand-over of the
/// same object replaces the first.
/// </summary>
public static class HubFiles
{
    /// <summary>The hand-over folder in an app's settings folder, made if need be (owner-only on Linux and macOS).</summary>
    public static string Folder(string settingsFolder)
    {
        var folder = Path.Combine(settingsFolder, "hub");
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return folder;
    }

    /// <summary>The file for an object handed over: its name made safe, the link's first characters, and the extension.</summary>
    public static string PathFor(string folder, string name, string link, string extension = ".lwo")
    {
        var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()).Trim('_');
        if (safe.Length == 0) safe = "object";
        if (safe.Length > 40) safe = safe[..40];
        return Path.Combine(folder, $"{safe}-{link[..Math.Min(8, link.Length)]}{extension}");
    }

    /// <summary>A new link id.</summary>
    public static string NewLink() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The file live sync keeps a linked object's copy in (<see cref="HubMessage.ObjectChanged"/>): one per link, named
    /// after the link alone (made safe), so every sync replaces the same file.
    /// </summary>
    public static string LivePathFor(string folder, string link, string extension = ".lwo")
    {
        var safe = new string(link.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe.Length == 0) safe = "object";
        if (safe.Length > 40) safe = safe[..40];
        return Path.Combine(folder, $"live-{safe}{extension}");
    }

    /// <summary>
    /// Writes a file so that a reader never sees half of it: <paramref name="write"/> writes a temporary file beside
    /// <paramref name="path"/> (with the same extension, which picks the format), which is then renamed over it in one
    /// step. If writing fails, the temporary file is removed and <paramref name="path"/> is left as it was.
    /// </summary>
    public static void WriteWhole(string path, Action<string> write)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            write(temp);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

/// <summary>
/// Finds and starts the other app when it isn't on the hub: a path the person set, an environment variable
/// (AVAMODELER_PATH, AVALIGHTWAVE_PATH), beside this program, in a sibling folder named after it, or on the PATH. An
/// object file and its link go on the command line (<c>file --hub-link id</c>).
/// </summary>
public sealed class AppLauncher(string app)
{
    /// <summary>The program's file name on this system (".exe" on Windows).</summary>
    public string FileName => OperatingSystem.IsWindows() ? app + ".exe" : app;

    /// <summary>The folder this program runs from (tests give their own).</summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>The PATH to look along (tests give their own).</summary>
    public string? SearchPath { get; init; } = Environment.GetEnvironmentVariable("PATH");

    /// <summary>The environment variable naming the program (AVAMODELER_PATH or AVALIGHTWAVE_PATH).</summary>
    public string Variable => app.ToUpperInvariant() + "_PATH";

    /// <summary>Starts a process (tests give their own, which starts nothing).</summary>
    public Func<System.Diagnostics.ProcessStartInfo, bool> Start { get; init; } = info =>
    {
        using var p = System.Diagnostics.Process.Start(info);
        return p != null;
    };

    /// <summary>The program, or null if it can't be found. <paramref name="setting"/> is a path the person gave (a file or its folder).</summary>
    public string? Find(string? setting = null)
    {
        IEnumerable<string?> Candidates()
        {
            yield return setting;
            yield return Environment.GetEnvironmentVariable(Variable);
            yield return BaseDirectory;
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(BaseDirectory));
            if (parent != null) yield return Path.Combine(parent, app);
            foreach (var dir in (SearchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) yield return dir;
        }
        foreach (var c in Candidates())
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            if (File.Exists(c) && (Path.GetFileName(c).Equals(FileName, StringComparison.OrdinalIgnoreCase) || c.EndsWith(app + ".dll", StringComparison.OrdinalIgnoreCase))) return Path.GetFullPath(c);
            if (Directory.Exists(c))
            {
                var exe = Path.Combine(c, FileName);
                if (File.Exists(exe)) return Path.GetFullPath(exe);
                var dll = Path.Combine(c, app + ".dll");
                if (File.Exists(dll)) return Path.GetFullPath(dll);
            }
        }
        return null;
    }

    /// <summary>
    /// Starts the program with <paramref name="file"/> to open and its <paramref name="link"/>. Returns the program
    /// started, or null if it couldn't be found or started. A .dll is run with dotnet.
    /// </summary>
    public string? Launch(string? setting, string? file, string? link)
    {
        var program = Find(setting);
        if (program == null) return null;
        var info = program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new System.Diagnostics.ProcessStartInfo("dotnet") { ArgumentList = { program } }
            : new System.Diagnostics.ProcessStartInfo(program);
        if (file != null) info.ArgumentList.Add(file);
        if (link != null) { info.ArgumentList.Add("--hub-link"); info.ArgumentList.Add(link); }
        info.UseShellExecute = false;
        try
        {
            return Start(info) ? program : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>The link a program was started with (<c>--hub-link id</c>), if any.</summary>
    public static string? LinkArgument(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
            if (args[i] == "--hub-link") return args[i + 1];
        return null;
    }
}
