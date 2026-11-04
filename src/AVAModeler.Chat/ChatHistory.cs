using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVAModeler.Chat;

/// <summary>What one line of a saved chat showed: the person's words, the model's, a tool call, or a note.</summary>
public sealed class StoredEntry
{
    /// <summary>"user", "assistant", "tool" or "notice".</summary>
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>A tool call's tool.</summary>
    public string? Name { get; set; }
    /// <summary>A tool call's input, as JSON text.</summary>
    public string? Input { get; set; }
    public bool Error { get; set; }
    /// <summary>A tool call's pictures, by file name in the chat's folder (pictures over the size limits aren't kept).</summary>
    public List<string>? Images { get; set; }
    /// <summary>How many of the call's pictures weren't kept (too big to save).</summary>
    public int ImagesLeftOut { get; set; }
}

/// <summary>A tool call in a saved conversation, as the model asked for it.</summary>
public sealed class StoredCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, JsonElement> Input { get; set; } = [];
    /// <summary>Why it wasn't run as sent (its arguments weren't JSON), if so.</summary>
    public string? Problem { get; set; }
}

/// <summary>A tool call's outcome in a saved conversation.</summary>
public sealed class StoredResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Error { get; set; }
    public List<string>? Images { get; set; }
    public int ImagesLeftOut { get; set; }
}

/// <summary>
/// One turn of a saved conversation in a shape neither provider owns, so it can be sent again in the provider's own
/// format: the person's words ("user"), the model's reply with its tool calls ("assistant"), or the calls' outcomes
/// ("results").
/// </summary>
public sealed class StoredTurn
{
    public string Role { get; set; } = "";
    public string? Text { get; set; }
    public List<StoredCall>? Calls { get; set; }
    public List<StoredResult>? Results { get; set; }
}

/// <summary>
/// A saved chat: what the panel showed (<see cref="Entries"/>) and the conversation to carry on with
/// (<see cref="Turns"/>), with the provider and model it was with. Never a key: there's no place for one.
/// </summary>
public sealed class StoredChat
{
    public const string FormatName = "avachat";

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public ChatProvider Provider { get; set; }
    /// <summary>The model: a Claude model's id, or the model's name on the OpenAI-compatible server.</summary>
    public string Model { get; set; } = "";
    /// <summary>The OpenAI-compatible server it talked to, by host and port only (no path, no key), or null for Claude.</summary>
    public string? Server { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public List<StoredEntry> Entries { get; set; } = [];
    public List<StoredTurn> Turns { get; set; } = [];
}

/// <summary>A saved chat as the history list shows it.</summary>
public sealed record ChatSummary(string Id, string Title, ChatProvider Provider, string Model, DateTimeOffset Updated, int Messages);

/// <summary>
/// The AI Chat's saved conversations, one JSON file each (<c>&lt;id&gt;.json</c>) in a folder (<c>chats</c> beside
/// <c>preferences.json</c> in the app), with each chat's pictures in a folder of its own beside it
/// (<c>&lt;id&gt;/</c>), each named by its contents so it's written once. A picture over <see cref="MaxImageBytes"/>, or
/// past <see cref="MaxChatImageBytes"/> for the chat, isn't kept (the chat notes it). Files and folders are readable by
/// their owner only on Linux and macOS. No key is ever written: a saved chat holds the provider, the model and the
/// server's host and port, never a key or an Authorization header.
/// </summary>
public sealed class ChatHistory(string folder)
{
    public const string FolderName = "chats";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The folder the chats are kept in.</summary>
    public string Folder { get; } = folder;

    /// <summary>The biggest picture kept (a render is usually far less).</summary>
    public long MaxImageBytes { get; set; } = 2_000_000;

    /// <summary>The most picture bytes kept for one chat; later pictures beyond it aren't.</summary>
    public long MaxChatImageBytes { get; set; } = 16_000_000;

    /// <summary>The saved chats, newest first. Files that can't be read are skipped.</summary>
    public IReadOnlyList<ChatSummary> List()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<ChatSummary>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
            if (Read(file) is { } c)
                list.Add(new ChatSummary(c.Id, c.Title, c.Provider, c.Model, c.Updated, c.Entries.Count(e => e.Kind is "user" or "assistant")));
        return [.. list.OrderByDescending(c => c.Updated).ThenByDescending(c => c.Id, StringComparer.Ordinal)];
    }

    /// <summary>A saved chat by its id, or null if there's none (or it can't be read).</summary>
    public StoredChat? Load(string id) => ValidId(id) ? Read(Path.Combine(Folder, id + ".json")) : null;

    private static StoredChat? Read(string path)
    {
        try
        {
            var chat = JsonSerializer.Deserialize<StoredChat>(File.ReadAllText(path), Json);
            return chat is { Format: StoredChat.FormatName } && ValidId(chat.Id) && Path.GetFileNameWithoutExtension(path) == chat.Id ? chat : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes a chat (its pictures should already be kept with <see cref="KeepImage"/>).</summary>
    public void Save(StoredChat chat)
    {
        if (!ValidId(chat.Id)) throw new ArgumentException("A chat's id is letters, digits and dashes.");
        MakeFolder(Folder);
        var path = Path.Combine(Folder, chat.Id + ".json");
        var temp = path + ".tmp";
        WritePrivate(temp, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(chat, Json)));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Keeps a picture for a chat, once, and returns its file name; or null when it's too big, or the chat has used its
    /// share (<paramref name="used"/> counts the chat's picture bytes so far, and grows).
    /// </summary>
    public string? KeepImage(string chatId, ToolImage image, ref long used)
    {
        if (image.Data.Length > MaxImageBytes || used + image.Data.Length > MaxChatImageBytes) return null;
        var name = ImageName(image);
        var dir = Path.Combine(Folder, chatId);
        MakeFolder(Folder);
        MakeFolder(dir);
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) WritePrivate(path, image.Data);
        used += image.Data.Length;
        return name;
    }

    /// <summary>A picture's file name: from its contents, so the same picture is kept once.</summary>
    public static string ImageName(ToolImage image) => Convert.ToHexString(SHA256.HashData(image.Data))[..24].ToLowerInvariant() + Extension(image.MediaType);

    /// <summary>A kept picture, or null if it's gone.</summary>
    public ToolImage? ReadImage(string chatId, string name)
    {
        if (!ValidId(chatId) || name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;
        var path = Path.Combine(Folder, chatId, name);
        try
        {
            return File.Exists(path) ? new ToolImage(File.ReadAllBytes(path), MediaTypeOf(name)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Deletes a saved chat and its pictures.</summary>
    public void Delete(string id)
    {
        if (!ValidId(id)) return;
        try
        {
            File.Delete(Path.Combine(Folder, id + ".json"));
            var dir = Path.Combine(Folder, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes every saved chat (Clear History).</summary>
    public void Clear()
    {
        foreach (var c in List()) Delete(c.Id);
        // And anything left of chats that couldn't be read.
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
            if (ValidId(Path.GetFileNameWithoutExtension(file))) Delete(Path.GetFileNameWithoutExtension(file));
    }

    /// <summary>A new chat id from a time: sortable, unique enough, and safe as a file name.</summary>
    public static string NewId(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    private static bool ValidId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string Extension(string mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".png",
    };

    private static string MediaTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/png",
    };

    private static void MakeFolder(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Writes a file readable and writable by its owner only (on Linux and macOS).</summary>
    private static void WritePrivate(string path, byte[] data)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options)) stream.Write(data);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>A chat written out as Markdown (the chat panel's Export): its title, the provider and model, then each line.</summary>
public static class ChatMarkdown
{
    /// <summary>
    /// The chat as Markdown. Pictures are linked as <paramref name="imageFolder"/>/name when given (the files written
    /// beside the Markdown), else mentioned in words.
    /// </summary>
    public static string Write(StoredChat chat, string? imageFolder = null)
    {
        var b = new StringBuilder();
        string who = chat.Provider == ChatProvider.Anthropic ? "Claude" : "The model";
        b.Append("# ").Append(Line(chat.Title.Length > 0 ? chat.Title : "AI Chat")).Append("\n\n");
        b.Append('_').Append(chat.Provider == ChatProvider.Anthropic ? $"{ChatModels.Label(chat.Model)} (Anthropic API)" : $"{chat.Model} on {chat.Server ?? "an OpenAI-compatible server"}")
            .Append(", ").Append(chat.Created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" (AVA Modeler)_\n\n");
        foreach (var e in chat.Entries)
        {
            switch (e.Kind)
            {
                case "user":
                    b.Append("**You:** ").Append(e.Text.Trim()).Append("\n\n");
                    break;
                case "assistant":
                    b.Append("**").Append(who).Append(":** ").Append(e.Text.Trim()).Append("\n\n");
                    break;
                case "tool":
                    b.Append("- `").Append(e.Name).Append("` ").Append(e.Error ? "failed: " : "→ ").Append(Line(FirstLine(e.Text))).Append('\n');
                    if (e.Input is { Length: > 2 } input) b.Append("\n  ```json\n  ").Append(input.Replace("\n", "\n  ")).Append("\n  ```\n\n");
                    foreach (var image in e.Images ?? [])
                        b.Append(imageFolder != null ? $"  ![a picture from {e.Name}]({imageFolder}/{image})\n\n" : $"  (a picture from {e.Name})\n\n");
                    if (e.ImagesLeftOut > 0) b.Append($"  ({e.ImagesLeftOut} picture(s) too big to keep)\n\n");
                    break;
                case "notice":
                    b.Append("> ").Append(Line(e.Text)).Append("\n\n");
                    break;
            }
        }
        return b.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Writes the chat as a Markdown file, its kept pictures copied into a folder beside it (<c>name-images</c>), and
    /// returns the files written.
    /// </summary>
    public static IReadOnlyList<string> Save(StoredChat chat, Func<string, ToolImage?>? pictures, string path)
    {
        var written = new List<string>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var folderName = Path.GetFileNameWithoutExtension(path) + "-images";
        bool any = false;
        if (pictures != null)
            foreach (var name in chat.Entries.SelectMany(e => e.Images ?? []).Distinct())
                if (pictures(name) is { } image)
                {
                    var target = Path.Combine(dir, folderName, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, image.Data);
                    written.Add(target);
                    any = true;
                }
        File.WriteAllText(path, Write(chat, any ? folderName : null));
        written.Insert(0, path);
        return written;
    }

    private static string FirstLine(string s) => s.Split('\n', 2)[0].Trim() is { Length: > 0 } l ? (l.Length > 200 ? l[..200] + "…" : l) : "done";

    private static string Line(string s) => s.Replace('\n', ' ').Trim();
}
