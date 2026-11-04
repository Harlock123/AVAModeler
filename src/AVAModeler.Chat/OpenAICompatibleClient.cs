using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AVAModeler.Chat;

/// <summary>
/// A failed request to an OpenAI-compatible server, already put in words that never hold the key: the server's own
/// short message is quoted with any copy of the key masked out.
/// </summary>
public sealed class OpenAICompatibleException(string message, HttpStatusCode? status, string server, OpenAICompatibleProblem problem, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>The HTTP status, when the server answered.</summary>
    public HttpStatusCode? Status { get; } = status;

    /// <summary>The server as the person can be told about it: scheme, host and port (never a user name or key).</summary>
    public string Server { get; } = server;

    public OpenAICompatibleProblem Problem { get; } = problem;
}

/// <summary>What kind of failure an <see cref="OpenAICompatibleException"/> is.</summary>
public enum OpenAICompatibleProblem { Status, Unreachable, Timeout, BrokenStream, BadUrl }

/// <summary>
/// A client for the OpenAI Chat Completions API as local and self-hosted servers speak it (Ollama's <c>/v1</c>,
/// LM Studio, vLLM, llama.cpp's server, a company gateway): <c>POST {base}/chat/completions</c> streamed as server-sent
/// events, and <c>GET {base}/models</c>. Plain <see cref="HttpClient"/> and System.Text.Json: it talks to the one base URL
/// the person set and nothing else. The key, if there is one, goes only in the Authorization header.
/// </summary>
public sealed class OpenAICompatibleClient
{
    private readonly HttpClient _http;
    private readonly string? _key;

    /// <param name="baseUrl">The API's base, e.g. <c>http://localhost:11434/v1</c> (the paths are added to it).</param>
    /// <param name="key">A key for a gateway (none for a local server).</param>
    public OpenAICompatibleClient(HttpClient http, string baseUrl, string? key = null)
    {
        _http = http;
        _key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        BaseUrl = baseUrl.Trim().TrimEnd('/');
        Server = Describe(BaseUrl);
    }

    /// <summary>Ollama's OpenAI-compatible API on this computer.</summary>
    public const string DefaultBaseUrl = "http://localhost:11434/v1";

    public string BaseUrl { get; }

    /// <summary>The server in a few words for the person: <c>localhost:11434</c>.</summary>
    public string Server { get; }

    /// <summary>Whether the base URL is this computer (no key is needed, and nothing leaves the machine).</summary>
    public bool IsLocal => IsLocalUrl(BaseUrl);

    /// <summary>Whether a base URL is on this computer (localhost or a loopback address).</summary>
    public static bool IsLocalUrl(string baseUrl) => Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");

    /// <summary>A base URL as the person may be told it: host and port only, no user name or password in it.</summary>
    public static string Describe(string baseUrl) =>
        Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}")
            : "an unusable address";

    private Uri Endpoint(string path)
    {
        if (!Uri.TryCreate(BaseUrl + path, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new OpenAICompatibleException("The model server's base URL must start with http:// or https:// (for Ollama, http://localhost:11434/v1).", null, Server, OpenAICompatibleProblem.BadUrl);
        return uri;
    }

    private HttpRequestMessage Message(HttpMethod method, string path, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, Endpoint(path));
        if (_key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(body != null ? "text/event-stream" : "application/json"));
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>The models the server offers (<c>GET {base}/models</c>), by id, sorted.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancel = default)
    {
        using var request = Message(HttpMethod.Get, "/models");
        using var response = await SendAsync(request, cancel).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        try
        {
            return ParseModels(text);
        }
        catch (JsonException e)
        {
            throw new OpenAICompatibleException($"The model server at {Server} sent a model list that isn't JSON. Check that the base URL ends in /v1.", response.StatusCode, Server, OpenAICompatibleProblem.BrokenStream, e);
        }
    }

    /// <summary>Model ids from a <c>/models</c> answer: OpenAI's <c>data[].id</c>, or Ollama's own <c>models[].name</c>.</summary>
    internal static IReadOnlyList<string> ParseModels(string json)
    {
        var root = JsonNode.Parse(json);
        var ids = new List<string>();
        if (root?["data"] is JsonArray data)
            ids.AddRange(data.Select(m => m?["id"]?.GetValueKind() == JsonValueKind.String ? (string?)m["id"] : null).OfType<string>());
        else if (root?["models"] is JsonArray models)
            ids.AddRange(models.Select(m => (m?["name"] ?? m?["model"])?.GetValueKind() == JsonValueKind.String ? (string?)(m!["name"] ?? m["model"]) : null).OfType<string>());
        return ids.Where(i => i.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Streams a chat completion: each server-sent event's JSON in turn (a server that answers without streaming gives
    /// its one whole answer). An error the server reports in the stream is thrown.
    /// </summary>
    public async IAsyncEnumerable<JsonObject> StreamAsync(JsonObject body, [EnumeratorCancellation] CancellationToken cancel = default)
    {
        using var request = Message(HttpMethod.Post, "/chat/completions", body);
        using var response = await SendAsync(request, cancel).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            // Not streamed after all: one whole answer.
            var whole = await reader.ReadToEndAsync(cancel).ConfigureAwait(false);
            yield return Chunk(whole);
            yield break;
        }
        var data = new StringBuilder();
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or HttpRequestException && !cancel.IsCancellationRequested)
            {
                throw new OpenAICompatibleException($"The reply from {Server} broke off. Retry to try again.", null, Server, OpenAICompatibleProblem.BrokenStream, e);
            }
            if (line == null || line.Length == 0)
            {
                // A blank line ends an event; the end of the stream ends the last one.
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();
                    if (payload.Trim() == "[DONE]") yield break;
                    yield return Chunk(payload);
                }
                if (line == null) yield break;
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
            // "event:", "id:" and "retry:" lines say nothing the chat needs.
        }
    }

    private JsonObject Chunk(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new OpenAICompatibleException($"The reply from {Server} wasn't understood (it isn't JSON). Check that the base URL ends in /v1, then Retry.", null, Server, OpenAICompatibleProblem.BrokenStream, e);
        }
        if (node is not JsonObject o)
            throw new OpenAICompatibleException($"The reply from {Server} wasn't understood. Retry to try again.", null, Server, OpenAICompatibleProblem.BrokenStream);
        if (o["error"] is { } error)
            throw new OpenAICompatibleException($"The model server at {Server} reported a problem: {Redact(ErrorText(error))}", null, Server, OpenAICompatibleProblem.BrokenStream);
        return o;
    }

    /// <summary>Sends a request, turning every failure into an <see cref="OpenAICompatibleException"/>.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is TaskCanceledException or TimeoutException)
        {
            throw new OpenAICompatibleException($"The model server at {Server} took too long to answer (a large model can take a while to load). Retry to try again.", null, Server, OpenAICompatibleProblem.Timeout, e);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new OpenAICompatibleException(Unreachable(), null, Server, OpenAICompatibleProblem.Unreachable, e);
        }
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            string body;
            try { body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or HttpRequestException) { body = ""; }
            throw new OpenAICompatibleException(StatusText(response.StatusCode, ServerMessage(body)), response.StatusCode, Server, OpenAICompatibleProblem.Status);
        }
    }

    private string Unreachable() => IsLocal
        ? $"Couldn't reach the model server at {Server}. Is it running (for Ollama, start it or run \"ollama serve\"), and is the base URL right? Then Retry."
        : $"Couldn't reach the model server at {Server}. Check the base URL and the network, then Retry.";

    /// <summary>The line for an HTTP error, with the server's own words where they help.</summary>
    private string StatusText(HttpStatusCode status, string said)
    {
        int code = (int)status;
        var quote = said.Length > 0 ? $" It said: {said}" : "";
        return code switch
        {
            401 or 403 => $"The model server at {Server} refused the request ({code}): it wants an API key, or doesn't accept this one. Set it with the Key… button, then Retry.{quote}",
            404 => $"The model server at {Server} said 404 (not found): the model name may be wrong or not downloaded yet (for Ollama, \"ollama pull <model>\"; Refresh lists the server's models), or the base URL should end in /v1.{quote}",
            429 => $"The model server at {Server} is limiting requests (429). Wait a little, then Retry.{quote}",
            >= 500 => $"The model server at {Server} had a problem ({code}). Retry in a moment.{quote}",
            _ when said.Contains("tool", StringComparison.OrdinalIgnoreCase) =>
                $"The model server at {Server} couldn't take the request ({code}): this model may not support tool calls. Choose a model that does (qwen3, llama3.1, mistral-small…).{quote}",
            _ when said.Contains("image", StringComparison.OrdinalIgnoreCase) || said.Contains("vision", StringComparison.OrdinalIgnoreCase) =>
                $"The model server at {Server} couldn't take the request ({code}): this model may not take pictures. Untick \"Model sees pictures\", then Retry.{quote}",
            _ => $"The model server at {Server} couldn't take the request ({code}).{quote}",
        };
    }

    /// <summary>The server's error message from its JSON (OpenAI's, Ollama's, vLLM's and llama.cpp's shapes), shortened and masked.</summary>
    private string ServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        string text;
        try
        {
            var node = JsonNode.Parse(body);
            text = node?["error"] is { } e ? ErrorText(e) : node?["message"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? (string)m! : node?["detail"]?.ToJsonString() ?? "";
        }
        catch (JsonException)
        {
            text = body.TrimStart().StartsWith('<') ? "" : body;
        }
        return Redact(Short(text));
    }

    private static string ErrorText(JsonNode error) =>
        error.GetValueKind() == JsonValueKind.String ? (string)error! : error["message"]?.GetValueKind() == JsonValueKind.String ? (string)error["message"]! : error.ToJsonString();

    private static string Short(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    /// <summary>A server that echoes the key back never gets it shown: every copy becomes its mask.</summary>
    private string Redact(string text) => _key != null ? text.Replace(_key, ApiKeyStore.Mask(_key), StringComparison.Ordinal) : text;
}
