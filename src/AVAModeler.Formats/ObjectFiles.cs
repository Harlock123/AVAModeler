using System.Text;
using AVAModeler.Core;

namespace AVAModeler.Formats;

/// <summary>Loads and saves objects in any supported format, choosing by the file's contents or extension.</summary>
public static class ObjectFiles
{
    /// <summary>Extensions that can be opened, for open dialogs.</summary>
    public static readonly string[] LoadExtensions = [".lwo", ".lw", ".geo", ".obj"];

    /// <summary>Extensions that can be saved, for save dialogs (the first is the native one).</summary>
    public static readonly string[] SaveExtensions = [".lwo", ".obj", ".geo"];

    /// <summary>A short description of a format by extension, for dialog filters.</summary>
    public static string Describe(string extension) => extension.ToLowerInvariant() switch
    {
        ".lwo" or ".lw" => "LightWave object (LWOB/LWLO; LWO2 to open)",
        ".geo" => "VideoScape object",
        ".obj" => "Wavefront OBJ",
        _ => throw new NotSupportedException($"No object format uses {extension}."),
    };

    /// <summary>
    /// Loads an object. IFF files are recognised by their FORM type (LWOB, LWLO or LWO2) whatever they're called;
    /// otherwise a "3DG1" header means VideoScape, and failing that the extension decides. An OBJ file's .mtl
    /// libraries are looked for beside it. Sets <see cref="Document.FilePath"/>.
    /// </summary>
    public static Document Load(string path)
    {
        var data = File.ReadAllBytes(path);
        Document doc;
        if (data.Length >= 12 && Encoding.Latin1.GetString(data, 0, 4) == "FORM")
            doc = Encoding.Latin1.GetString(data, 8, 4) switch
            {
                "LWOB" or "LWLO" => Lwob.Read(data),
                "LWO2" => Lwo2.Read(data),
                var type => throw new FormatException($"{Path.GetFileName(path)} is an IFF file of type {type}, not an object."),
            };
        else if (data.Length >= 4 && Encoding.Latin1.GetString(data, 0, 4) == "3DG1")
            doc = VideoScape.Read(Text(data));
        else
            doc = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".lwo" or ".lw" => throw new FormatException($"{Path.GetFileName(path)} isn't a LightWave object: it doesn't start with FORM."),
                ".geo" => VideoScape.Read(Text(data)),
                ".obj" => Obj.Read(Text(data), name => OpenBeside(path, name)),
                var ext => throw new NotSupportedException($"Can't open {ext} files."),
            };
        doc.FilePath = path;
        doc.Modified = false;
        return doc;
    }

    /// <summary>
    /// Saves an object by extension: .lwo (or .lw) as LWOB/LWLO, .obj as OBJ with a .mtl of the same name beside it,
    /// .geo as VideoScape. Each file is written in full to a temporary file first, so a failure leaves the old one.
    /// </summary>
    public static void Save(Document doc, string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".lwo" or ".lw":
                Replace(path, s => Lwob.Write(doc, s));
                break;
            case ".obj":
                string mtlPath = Path.ChangeExtension(path, ".mtl");
                var mtl = new StringWriter();
                Replace(path, s =>
                {
                    using var w = new StreamWriter(s, new UTF8Encoding(false));
                    Obj.Write(doc, w, mtl, Path.GetFileName(mtlPath));
                });
                Replace(mtlPath, s =>
                {
                    using var w = new StreamWriter(s, new UTF8Encoding(false));
                    w.Write(mtl.ToString());
                });
                break;
            case ".geo":
                Replace(path, s =>
                {
                    using var w = new StreamWriter(s, new UTF8Encoding(false));
                    VideoScape.Write(doc, w);
                });
                break;
            case var ext:
                throw new NotSupportedException($"Can't save {(ext.Length > 0 ? ext : "extensionless")} files.");
        }
    }

    private static TextReader Text(byte[] data) => new StreamReader(new MemoryStream(data), Encoding.UTF8);

    private static TextReader? OpenBeside(string path, string name)
    {
        string file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", name);
        return File.Exists(file) ? new StreamReader(file, Encoding.UTF8) : null;
    }

    private static void Replace(string path, Action<Stream> write)
    {
        string temp = path + ".tmp";
        try
        {
            using (var s = File.Create(temp)) write(s);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
