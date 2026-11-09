using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AVAModeler.Slicing;

/// <summary>What a print is sent to over the network.</summary>
public enum PrintHostKind
{
    /// <summary>Nothing: save files to copy over yourself.</summary>
    None,
    /// <summary>OctoPrint (a Raspberry Pi beside many printers): its <c>/api/files/local</c> upload.</summary>
    OctoPrint,
    /// <summary>Moonraker, Klipper's API (Mainsail and Fluidd use it): its <c>/server/files/upload</c>.</summary>
    Moonraker,
}

/// <summary>
/// A printer on the network to send G-code to: the kind of server, its address, and whether to start printing once the
/// file is there. The API key isn't kept here (it isn't in the preferences): see <see cref="PrintHostKey"/>.
/// </summary>
public sealed record PrintHost
{
    public PrintHostKind Kind { get; init; } = PrintHostKind.None;
    /// <summary>The server's address, such as http://octopi.local or http://192.168.1.50:7125.</summary>
    public string Url { get; init; } = "";
    public bool StartPrint { get; init; }

    public PrintHost Checked()
    {
        if (Kind == PrintHostKind.None) return this;
        if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"The printer's address must be an http:// or https:// address, such as {(Kind == PrintHostKind.OctoPrint ? "http://octopi.local" : "http://192.168.1.50:7125")}.");
        return this;
    }
}

/// <summary>A failure sending to a printer, worded for the person (the API key is never in it).</summary>
public sealed class PrintHostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Sends a file to OctoPrint or Moonraker: one multipart upload to the address the person set, and nothing else (no
/// other host, no telemetry). The key goes only in the <c>X-Api-Key</c> header.
/// </summary>
public static class PrintHostClient
{
    /// <summary>Uploads the file (and starts it printing if the host says so). Returns a line saying what happened.</summary>
    public static async Task<string> SendAsync(PrintHost host, string? apiKey, string fileName, byte[] contents, HttpClient http, CancellationToken cancel = default)
    {
        host.Checked();
        if (host.Kind == PrintHostKind.None) throw new PrintHostException("Choose OctoPrint or Moonraker and give the printer's address first.");
        var baseUri = new Uri(host.Url.Trim().TrimEnd('/') + "/");
        var target = new Uri(baseUri, host.Kind == PrintHostKind.OctoPrint ? "api/files/local" : "server/files/upload");
        string where = $"{baseUri.Host}{(baseUri.IsDefaultPort ? "" : ":" + baseUri.Port)}";
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(contents);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(host.StartPrint ? "true" : "false"), "print");
        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = form };
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Add("X-Api-Key", apiKey.Trim());
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new PrintHostException($"Can't reach the printer at {where}: is it on, and is the address right?", e);
        }
        catch (TaskCanceledException e) when (!cancel.IsCancellationRequested)
        {
            throw new PrintHostException($"The printer at {where} didn't answer in time.", e);
        }
        using (response)
        {
            if (response.IsSuccessStatusCode)
                return host.StartPrint
                    ? $"Sent {fileName} to {where}, and it's printing."
                    : $"Sent {fileName} to {where}: start it there when you're ready.";
            string detail = await Detail(response, apiKey).ConfigureAwait(false);
            throw new PrintHostException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{where} refused the API key: copy it again from {(host.Kind == PrintHostKind.OctoPrint ? "OctoPrint's Settings › Application Keys" : "Moonraker's [authorization] or Mainsail's settings")}.",
                HttpStatusCode.NotFound => $"{where} has no {(host.Kind == PrintHostKind.OctoPrint ? "OctoPrint" : "Moonraker")} upload there: check the address and the kind of server.",
                HttpStatusCode.Conflict => $"{where} is busy (printing already, or not connected to the printer), so it didn't start: {detail}",
                _ => $"{where} said {(int)response.StatusCode} {response.ReasonPhrase}{(detail.Length > 0 ? ": " + detail : "")}.",
            });
        }
    }

    /// <summary>A short reason from the server's answer, with any copy of the key masked.</summary>
    private static async Task<string> Detail(HttpResponseMessage response, string? apiKey)
    {
        string body;
        try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or IOException) { return ""; }
        string text = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                text = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : err.ToString();
        }
        catch (JsonException) { }
        if (!string.IsNullOrWhiteSpace(apiKey)) text = text.Replace(apiKey.Trim(), "••••");
        text = text.Replace('\n', ' ').Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }
}

/// <summary>
/// The printer's API key, kept in a file of its own beside the preferences (owner-only on Linux and macOS), never in
/// preferences.json, a status line or an exception. <see cref="Mask"/> is all that's shown of it.
/// </summary>
public sealed class PrintHostKey(string? path)
{
    public const string FileName = "print-host-api-key";

    /// <summary>The file (null: nowhere, as in tests).</summary>
    public string? Path { get; } = path;

    /// <summary>A key for this run only (tests); it wins over the file.</summary>
    public string? Override { get; set; }

    public static PrintHostKey In(string? folder) => new(folder == null ? null : System.IO.Path.Combine(folder, FileName));

    public string? Get()
    {
        if (Override != null) return Override.Length > 0 ? Override : null;
        try { return Path != null && File.Exists(Path) ? File.ReadAllText(Path).Trim() is { Length: > 0 } k ? k : null : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Keeps the key (an empty one removes the file); with no file to keep it in, keeps it for this run.</summary>
    public void Set(string? key)
    {
        key = key?.Trim() ?? "";
        if (Path == null)
        {
            Override = key;
            return;
        }
        if (key.Length == 0)
        {
            if (File.Exists(Path)) File.Delete(Path);
            return;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var s = new FileStream(Path, options))
        using (var w = new StreamWriter(s)) w.Write(key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The key as it may be shown: its last four characters.</summary>
    public static string Mask(string? key) => string.IsNullOrEmpty(key) ? "(none)" : key.Length <= 4 ? "••••" : "••••" + key[^4..];
}
