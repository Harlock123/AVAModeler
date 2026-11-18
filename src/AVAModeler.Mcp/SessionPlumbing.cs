using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// What kind of failure a call had, given to agents as the error's <c>kind</c> (in its structured content and
/// <c>_meta</c>) so a script can branch on it without reading the words.
/// </summary>
public static class ErrorKinds
{
    public const string BadParameter = "bad_parameter";
    public const string NotFound = "not_found";
    public const string Busy = "busy";
    public const string Locked = "locked";
    public const string ExpectationFailed = "expectation_failed";
    public const string NotAllowed = "not_allowed";
    public const string InvalidState = "invalid_state";
    public const string Declined = "declined";
    public const string File = "file";
    public const string Internal = "internal";

    /// <summary>Every kind, for the instructions and the tests.</summary>
    public static readonly string[] All = [BadParameter, NotFound, Busy, Locked, ExpectationFailed, NotAllowed, InvalidState, Declined, File, Internal];

    public static string Of(Exception e) => e switch
    {
        McpException { InnerException: { } inner } => Of(inner),
        LockedException => Locked,
        ExpectationFailedException => ExpectationFailed,
        DeclinedException => Declined,
        ModelerBusyException => Busy,
        FileNotFoundException or DirectoryNotFoundException => NotFound,
        UnauthorizedAccessException => NotAllowed,
        _ when Missing(e.Message) => NotFound,
        ArgumentException or FormatException or JsonException => BadParameter,
        IOException or InvalidDataException => File,
        InvalidOperationException or NotSupportedException => InvalidState,
        _ => Internal,
    };

    /// <summary>The wording the tools use for something that isn't there ("There's no item called…").</summary>
    private static bool Missing(string message) =>
        message.StartsWith("There's no ", StringComparison.Ordinal) || message.StartsWith("No ", StringComparison.Ordinal)
        || message.Contains(" isn't one of ", StringComparison.Ordinal) || message.Contains("has no tool called", StringComparison.Ordinal)
        || message.Contains("couldn't be found", StringComparison.Ordinal);
}

/// <summary>A call's own state while it runs: set by the server's call filter, read by the handlers.</summary>
internal sealed class CallScope
{
    /// <summary>The failure's kind, set where it's caught (the first one wins).</summary>
    public string? ErrorKind;

    /// <summary>Where the call's progress goes (the client's progress token, a job's status), or null.</summary>
    public IProgress<ProgressNotificationValue>? Progress;

    /// <summary>The person agreed to the change this call asked about, so it goes ahead without asking again.</summary>
    public bool Confirmed;

    /// <summary>The call's own server (for asking the person: on a multi-round-trip session it must be this request's).</summary>
    public McpServer? Server;
}

/// <summary>A change locked against agents would be made (by a tool, or a batch's step).</summary>
public sealed class LockedException(string message) : InvalidOperationException(message);

/// <summary>The person was asked about a change and said no.</summary>
public sealed class DeclinedException(string message) : InvalidOperationException(message);

public sealed partial class ModelerTools
{
    internal static readonly AsyncLocal<CallScope?> Scope = new();

    /// <summary>The session's server, once it has had a call: for asking the person (elicitation) and notifications.</summary>
    internal McpServer? Server { get; private set; }

    /// <summary>The client's name ("claude-code", "AVA Modeler Chat"), for the session list and locks' owners.</summary>
    public string ClientName { get; private set; } = "an agent";

    /// <summary>This session's name among the others on the same host ("claude-code #2"): locks it takes say it.</summary>
    public string SessionName => _session?.Name ?? ClientName;

    private AgentSessions.Session? _session;

    /// <summary>Called by the server's call filter before each call.</summary>
    internal void Attach(McpServer server, string tool)
    {
        if (Server == null)
        {
            Server = server;
            ClientName = server.ClientInfo?.Name is { Length: > 0 } n ? n : "an agent";
            _session = AgentSessions.For(host).Join(ClientName);
        }
        _session?.Called(tool);
    }

    /// <summary>The session is over: its locks go and it leaves the session list.</summary>
    internal async Task EndAsync()
    {
        StopWatching();
        if (_session == null) return;
        AgentSessions.For(host).Leave(_session);
        try
        {
            await host.RunSceneAsync("ended its session", (c, l) =>
            {
                c.Document.AgentLocks.RemoveAll(x => x.Owner == _session.Name);
                l.AgentLocks.RemoveAll(x => x.Owner == _session.Name);
                return 0;
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ModelerBusyException or InvalidOperationException or ObjectDisposedException or OperationCanceledException) { }
    }

    /// <summary>Notes a failure's kind for the call under way (its answer carries it).</summary>
    private static void NoteError(Exception e)
    {
        if (Scope.Value is { } s) s.ErrorKind ??= ErrorKinds.Of(e);
    }

    /// <summary>Reports progress of the call under way, to the client and to a job's status (0 to 1, with a line).</summary>
    internal static void Report(double done, string message)
    {
        if (Scope.Value?.Progress is { } p) p.Report(new ProgressNotificationValue { Progress = (float)Math.Clamp(done, 0, 1) * 100, Total = 100, Message = message });
    }

    /// <summary>
    /// After a call: an error gets its kind (structured content and <c>_meta</c>); a JSON answer is also given as
    /// structured content; a successful change is recorded for get_recording.
    /// </summary>
    internal void Finish(CallToolRequestParams request, Tool? tool, CallToolResult result, CallScope scope)
    {
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        if (result.IsError == true)
        {
            string kind = scope.ErrorKind ?? (text.Contains(" has no argument ", StringComparison.Ordinal) || text.Contains(" must be ", StringComparison.Ordinal) ? ErrorKinds.BadParameter : ErrorKinds.Internal);
            var error = new JsonObject { ["error"] = text, ["kind"] = kind };
            result.StructuredContent = JsonSerializer.SerializeToElement(error);
            result.Meta = new JsonObject { ["kind"] = kind };
            return;
        }
        if (result.StructuredContent == null && text.Length > 0 && text[0] == '{')
        {
            try { result.StructuredContent = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { }
        }
        if (tool != null) Record(request, tool);
    }
}

/// <summary>
/// The agent sessions working on one host (the open window's, in the live app): who they are, what each did last, so
/// two agents working together can see each other (list_agents) and keep off each other's work (locks).
/// </summary>
public sealed class AgentSessions
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IModelerHost, AgentSessions> _hosts = new();
    private readonly List<Session> _sessions = [];
    private int _count;

    public static AgentSessions For(IModelerHost host) => _hosts.GetValue(host, _ => new AgentSessions());

    public sealed class Session(string name, string client)
    {
        public string Name { get; } = name;
        public string Client { get; } = client;
        public DateTime Joined { get; } = DateTime.UtcNow;
        public string? LastTool { get; private set; }
        public DateTime? LastAt { get; private set; }
        public int Calls { get; private set; }

        public void Called(string tool)
        {
            LastTool = tool;
            LastAt = DateTime.UtcNow;
            Calls++;
        }
    }

    public Session Join(string client)
    {
        lock (_sessions)
        {
            var s = new Session($"{client} #{++_count}", client);
            _sessions.Add(s);
            return s;
        }
    }

    public void Leave(Session s)
    {
        lock (_sessions) _sessions.Remove(s);
    }

    public IReadOnlyList<Session> All
    {
        get { lock (_sessions) return [.. _sessions]; }
    }
}
