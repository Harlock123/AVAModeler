namespace AVAModeler.Chat;

/// <summary>Where the chat's Anthropic API key came from.</summary>
public enum KeySource { None, Environment, File }

/// <summary>
/// The person's Anthropic API key: the <c>ANTHROPIC_API_KEY</c> environment variable if it is set, otherwise a key they
/// entered in the chat's settings, kept in a file of its own (owner-only on Linux and macOS). The key is never logged,
/// shown again in full, or put in an exception or status text: <see cref="Mask"/> is all anyone sees of it.
/// </summary>
public sealed class ApiKeyStore(string? path, Func<string?>? environment)
{
    public const string EnvironmentVariable = "ANTHROPIC_API_KEY";

    /// <summary>The key file's name, beside <c>preferences.json</c> in the app-data folder.</summary>
    public const string FileName = "anthropic-api-key";

    /// <summary>The app's store: the environment variable, then the file in <paramref name="folder"/>.</summary>
    public static ApiKeyStore ForApp(string folder) =>
        new(System.IO.Path.Combine(folder, FileName), () => System.Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>The file a key the person enters is kept in (null: nowhere, as in tests).</summary>
    public string? Path { get; } = path;

    /// <summary>A key set for this run only (tests); it wins over the environment and the file.</summary>
    public string? Override { get; set; }

    public KeySource Source => Read().Source;

    /// <summary>The key to use, or null when there is none.</summary>
    public string? Get() => Read().Key;

    private (string? Key, KeySource Source) Read()
    {
        if (Override is { Length: > 0 } o) return (o, KeySource.File);
        if (environment?.Invoke()?.Trim() is { Length: > 0 } env) return (env, KeySource.Environment);
        if (Path != null)
        {
            try
            {
                if (File.Exists(Path) && File.ReadAllText(Path).Trim() is { Length: > 0 } saved) return (saved, KeySource.File);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return (null, KeySource.None);
    }

    /// <summary>Keeps a key in the file, readable and writable by its owner only (on Linux and macOS).</summary>
    public void Save(string key)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ArgumentException("The key is empty.");
        if (Path == null) { Override = key; return; }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (File.Exists(Path)) File.Delete(Path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(Path, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Forgets the saved key (the environment variable, if set, still counts).</summary>
    public void Remove()
    {
        Override = null;
        if (Path == null) return;
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>All of a key anyone is shown: its kind and last four characters.</summary>
    public static string Mask(string? key) =>
        key is not { Length: > 0 } ? "none" : key.Length <= 12 ? "••••" : key[..Math.Min(7, key.Length - 8)] + "…" + key[^4..];
}
