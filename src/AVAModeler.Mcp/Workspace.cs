using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Tools;

namespace AVAModeler.Mcp;

/// <summary>
/// The files an agent may open and save: paths are resolved against a root folder and must stay inside it, only object
/// formats the registry knows (built in or from a plugin) are read or written, and an existing file is only written
/// over when the caller says so. Nothing is ever deleted.
/// </summary>
public sealed class Workspace(string root, ToolRegistry registry)
{
    private readonly HashSet<string> _written = new(PathComparer);

    public string Root { get; } = Path.GetFullPath(root);
    public ToolRegistry Registry { get; } = registry;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The extensions that can be opened: the built-in formats and any plugin's.</summary>
    public IEnumerable<string> LoadExtensions => ObjectFiles.LoadExtensions.Concat(Registry.Formats.Where(f => f.CanLoad).SelectMany(f => f.Extensions)).Distinct();

    /// <summary>The extensions that can be saved: the built-in formats and any plugin's.</summary>
    public IEnumerable<string> SaveExtensions => ObjectFiles.SaveExtensions.Concat(Registry.Formats.Where(f => f.CanSave).SelectMany(f => f.Extensions)).Distinct();

    /// <summary>A path made absolute against the root; one that leads outside it throws.</summary>
    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is needed.");
        string full = Path.GetFullPath(Path.Combine(Root, path));
        string rootWithSlash = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSlash, cmp))
            throw new ArgumentException($"{path} is outside the working folder ({Root}). Give a path inside it, such as \"models/{Path.GetFileName(full)}\".");
        return full;
    }

    /// <summary>Opens an object file inside the root, through the built-in formats or a plugin's.</summary>
    public Document Load(string path)
    {
        string full = Resolve(path);
        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (!LoadExtensions.Contains(ext))
            throw new NotSupportedException($"AVA Modeler can't open {ext} files. It opens {string.Join(", ", LoadExtensions)}.");
        if (!File.Exists(full)) throw new FileNotFoundException($"There's no file {path} in the working folder ({Root}).", full);
        var plugin = Registry.Formats.FirstOrDefault(f => f.CanLoad && f.Extensions.Contains(ext));
        var doc = plugin != null && !ObjectFiles.LoadExtensions.Contains(ext) ? plugin.Load(full) : ObjectFiles.Load(full);
        doc.FilePath ??= full;
        doc.Modified = false;
        return doc;
    }

    /// <summary>
    /// Saves an object inside the root, choosing the format by extension. An existing file is only replaced when
    /// <paramref name="overwrite"/> is set or this workspace wrote it earlier. Saving as LightWave (.lwo) makes the
    /// file the document's own, as File › Save does.
    /// </summary>
    public string Save(Document doc, string path, bool overwrite)
    {
        string full = Resolve(path);
        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (!SaveExtensions.Contains(ext))
            throw new NotSupportedException($"AVA Modeler can't save {(ext == "" ? "files without an extension" : ext + " files")}. It saves {string.Join(", ", SaveExtensions)}.");
        string[] targets = ext == ".obj" ? [full, Path.ChangeExtension(full, ".mtl")] : [full];
        foreach (var t in targets)
            if (File.Exists(t) && !overwrite && !_written.Contains(t))
                throw new IOException($"{Path.GetFileName(t)} already exists. Save with overwrite set to replace it, or choose another name.");
        if (Path.GetDirectoryName(full) is { } dir) Directory.CreateDirectory(dir);
        var plugin = Registry.Formats.FirstOrDefault(f => f.CanSave && f.Extensions.Contains(ext));
        if (plugin != null && !ObjectFiles.SaveExtensions.Contains(ext)) plugin.Save(doc, full);
        else ObjectFiles.Save(doc, full);
        foreach (var t in targets) _written.Add(t);
        if (ext is ".lwo" or ".lw")
        {
            ObjectFiles.RebaseImages(doc, full);
            doc.FilePath = full;
            doc.Modified = false;
        }
        return full;
    }

    /// <summary>
    /// Points a command API's file loading and saving (and a macro's relative image paths) at this workspace until
    /// the returned scope ends, so a Lua macro's load() and save() keep to the same rules.
    /// </summary>
    /// <remarks>
    /// Unless <paramref name="allowDiscard"/> is set, replacing a document that has unsaved changes (a macro's new()
    /// or load()) is refused, so an agent can't throw away the person's work by accident.
    /// </remarks>
    public IDisposable Confine(Commands commands, bool allowDiscard = false)
    {
        var (loader, saver, baseDir, guard) = (commands.Loader, commands.Saver, commands.BaseDirectory, commands.ReplaceGuard);
        commands.Loader = Load;
        commands.Saver = (doc, path) => Save(doc, path, overwrite: false);
        commands.BaseDirectory = Root;
        commands.ReplaceGuard = allowDiscard ? null : () => commands.Document.Modified
            ? "The document has changes that haven't been saved. Save it first, or call new or open with discard_changes set to throw them away."
            : null;
        return new Scope(() => (commands.Loader, commands.Saver, commands.BaseDirectory, commands.ReplaceGuard) = (loader, saver, baseDir, guard));
    }

    private sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}
