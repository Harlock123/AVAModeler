using System.Text.Json;
using System.Text.Json.Nodes;

namespace AVAModeler.Core;

/// <summary>The flat views a reference image can sit behind.</summary>
public enum BackdropView { Top, Front, Side }

/// <summary>
/// A reference image (a blueprint, a photo) behind one of the Modeler's flat views, as classic Modeler's Display
/// Options › Backdrop had. It lies in world space on the view's plane, so it pans and zooms with the view and lines up
/// across views: its <see cref="Center"/> is on the view's two axes (Top: X and Z, Front: X and Y, Side: Z and Y), and
/// it is <see cref="Width"/> metres across, as high as the image's shape makes it unless <see cref="Height"/> is given.
/// The picture is adjusted (inverted, then contrast about mid grey, then brightness) and laid over the view's ground at
/// <see cref="Opacity"/>, under the grid and the wireframe. Immutable: change one with <c>with</c>.
/// </summary>
public sealed record Backdrop
{
    /// <summary>The image file: absolute, or relative to the object's folder.</summary>
    public string Image { get; init; } = "";
    /// <summary>The image's centre on the view's right and up axes, in metres.</summary>
    public Vec2 Center { get; init; }
    /// <summary>How wide the image is in the world, in metres.</summary>
    public double Width { get; init; } = 1;
    /// <summary>How high it is in metres; null keeps the image's own shape.</summary>
    public double? Height { get; init; }
    /// <summary>Added to every channel, -1 to 1 (0 leaves it).</summary>
    public double Brightness { get; init; }
    /// <summary>Scales each channel about mid grey, 0 (flat grey) to 4 (1 leaves it).</summary>
    public double Contrast { get; init; } = 1;
    /// <summary>How much of the image covers the view's ground, 0 to 1 (a half by default, so the wireframe stands out).</summary>
    public double Opacity { get; init; } = DefaultOpacity;
    /// <summary>Shows the image as a negative (white lines on a dark blueprint become dark lines on light).</summary>
    public bool Invert { get; init; }
    /// <summary>Whether it's shown.</summary>
    public bool Visible { get; init; } = true;

    public const double DefaultOpacity = 0.5;

    /// <summary>This backdrop if every setting is in range; otherwise an <see cref="ArgumentException"/> saying which isn't.</summary>
    public Backdrop Checked()
    {
        static bool Finite(double v) => double.IsFinite(v);
        if (string.IsNullOrWhiteSpace(Image)) throw new ArgumentException("A backdrop needs an image file.");
        if (!Finite(Center.X) || !Finite(Center.Y)) throw new ArgumentException("The backdrop's centre must be two finite numbers (metres).");
        if (!Finite(Width) || Width <= 0) throw new ArgumentException("The backdrop's width must be more than 0 metres.");
        if (Height is { } h && (!Finite(h) || h <= 0)) throw new ArgumentException("The backdrop's height must be more than 0 metres (or left out to keep the image's shape).");
        if (!Finite(Brightness) || Brightness < -1 || Brightness > 1) throw new ArgumentException("Brightness must be from -1 to 1 (0 leaves the image as it is).");
        if (!Finite(Contrast) || Contrast < 0 || Contrast > 4) throw new ArgumentException("Contrast must be from 0 to 4 (1 leaves the image as it is).");
        if (!Finite(Opacity) || Opacity < 0 || Opacity > 1) throw new ArgumentException("Opacity must be from 0 to 1.");
        return this;
    }

    /// <summary>Whether the picture is changed at all by brightness, contrast or inverting.</summary>
    public bool Adjusted => Brightness != 0 || Contrast != 1 || Invert;
}

/// <summary>The backdrops of the three flat views (each may be none). Immutable.</summary>
public sealed record BackdropSet
{
    public Backdrop? Top { get; init; }
    public Backdrop? Front { get; init; }
    public Backdrop? Side { get; init; }

    public static readonly BackdropSet Empty = new();

    public bool IsEmpty => Top == null && Front == null && Side == null;

    public Backdrop? this[BackdropView view] => view switch { BackdropView.Top => Top, BackdropView.Front => Front, _ => Side };

    /// <summary>This set with one view's backdrop replaced (null clears it).</summary>
    public BackdropSet With(BackdropView view, Backdrop? backdrop) => view switch
    {
        BackdropView.Top => this with { Top = backdrop },
        BackdropView.Front => this with { Front = backdrop },
        _ => this with { Side = backdrop },
    };

    /// <summary>Each view with its backdrop, for the views that have one.</summary>
    public IEnumerable<(BackdropView View, Backdrop Backdrop)> All()
    {
        foreach (var v in Enum.GetValues<BackdropView>())
            if (this[v] is { } b) yield return (v, b);
    }

    /// <summary>The set with every image path made absolute (relative ones from <paramref name="baseDirectory"/>), as kept in a session.</summary>
    public BackdropSet Rooted(string? baseDirectory)
    {
        var s = this;
        foreach (var (v, b) in All())
            if (!Path.IsPathRooted(b.Image) && baseDirectory != null)
                s = s.With(v, b with { Image = Path.GetFullPath(Path.Combine(baseDirectory, b.Image.Replace('\\', '/'))) });
        return s;
    }
}

/// <summary>
/// The maths of placing a backdrop: each flat view's axes (as the views' cameras have them), the image's size and
/// corners in the world, where a world point falls in the image, fitting it round an object, and sizing it from its
/// pixels.
/// </summary>
public static class BackdropMapping
{
    /// <summary>A view's right and up directions in the world: Top X and Z, Front X and Y, Side Z and Y (as the views look).</summary>
    public static (Vec3 Right, Vec3 Up) Axes(BackdropView view) => view switch
    {
        BackdropView.Top => (Vec3.UnitX, Vec3.UnitZ),
        BackdropView.Front => (Vec3.UnitX, Vec3.UnitY),
        _ => (Vec3.UnitZ, Vec3.UnitY),
    };

    /// <summary>The names of a view's two axes, right then up ("X", "Z" for the Top view).</summary>
    public static (string Right, string Up) AxisNames(BackdropView view) => view switch
    {
        BackdropView.Top => ("X", "Z"),
        BackdropView.Front => ("X", "Y"),
        _ => ("Z", "Y"),
    };

    /// <summary>A world point's coordinates on a view's two axes.</summary>
    public static Vec2 OnView(BackdropView view, Vec3 world)
    {
        var (r, u) = Axes(view);
        return new Vec2(Vec3.Dot(world, r), Vec3.Dot(world, u));
    }

    /// <summary>A point on a view's two axes as a world point on the view's plane through the origin.</summary>
    public static Vec3 ToWorld(BackdropView view, Vec2 onView)
    {
        var (r, u) = Axes(view);
        return r * onView.X + u * onView.Y;
    }

    /// <summary>The image's width and height in metres: the height from the image's shape unless the backdrop gives one.</summary>
    public static (double Width, double Height) Size(Backdrop b, int pixelsWide, int pixelsHigh) =>
        (b.Width, b.Height ?? b.Width * Math.Max(1, pixelsHigh) / Math.Max(1, pixelsWide));

    /// <summary>
    /// The image's four corners in the world, on the view's plane through the origin: bottom left, bottom right, top
    /// right, top left (image coordinates (0, 0), (1, 0), (1, 1), (0, 1), v up from the bottom).
    /// </summary>
    public static Vec3[] Corners(BackdropView view, Backdrop b, int pixelsWide, int pixelsHigh)
    {
        var (w, h) = Size(b, pixelsWide, pixelsHigh);
        double x0 = b.Center.X - w / 2, x1 = b.Center.X + w / 2, y0 = b.Center.Y - h / 2, y1 = b.Center.Y + h / 2;
        return [ToWorld(view, new Vec2(x0, y0)), ToWorld(view, new Vec2(x1, y0)), ToWorld(view, new Vec2(x1, y1)), ToWorld(view, new Vec2(x0, y1))];
    }

    /// <summary>Where a world point falls in the image: u across from its left edge, v up from its bottom (0 to 1 inside it).</summary>
    public static Vec2 ImagePoint(BackdropView view, Backdrop b, int pixelsWide, int pixelsHigh, Vec3 world)
    {
        var (w, h) = Size(b, pixelsWide, pixelsHigh);
        var p = OnView(view, world);
        return new Vec2((p.X - b.Center.X) / w + 0.5, (p.Y - b.Center.Y) / h + 0.5);
    }

    /// <summary>The image's extent as a box (flat along the view's depth), for fitting a view round it.</summary>
    public static Box3 Bounds(BackdropView view, Backdrop b, int pixelsWide, int pixelsHigh) => Box3.Of(Corners(view, b, pixelsWide, pixelsHigh));

    /// <summary>
    /// A centre and width that put the whole of <paramref name="box"/> (as the view sees it) inside the image, with a
    /// margin on each side as a fraction of its size, keeping the image's shape (or the backdrop's own height-to-width).
    /// </summary>
    public static (Vec2 Center, double Width) FitTo(BackdropView view, Box3 box, Backdrop b, int pixelsWide, int pixelsHigh, double margin = 0.05)
    {
        if (box.IsEmpty) throw new InvalidOperationException("There's nothing to fit the backdrop to: the object is empty.");
        var (r, u) = Axes(view);
        double bw = Math.Abs(Vec3.Dot(box.Size, r)), bh = Math.Abs(Vec3.Dot(box.Size, u));
        var (w0, h0) = Size(b, pixelsWide, pixelsHigh);
        double aspect = h0 / w0;
        double width = Math.Max(bw, bh / aspect) * (1 + 2 * margin);
        if (width <= 0) width = 1;
        return (OnView(view, box.Center), width);
    }

    /// <summary>A width in metres from the image's pixels at a size per pixel (metres a pixel covers).</summary>
    public static double WidthFromPixels(int pixelsWide, double metresPerPixel)
    {
        if (!double.IsFinite(metresPerPixel) || metresPerPixel <= 0) throw new ArgumentException("The size of a pixel must be more than 0 metres.");
        return pixelsWide * metresPerPixel;
    }

    /// <summary>A note for each shown backdrop whose image can't be found or read (a checker stands in for it).</summary>
    public static List<string> Problems(BackdropSet set, string? baseDirectory)
    {
        var problems = new List<string>();
        foreach (var (v, b) in set.All())
            if (b.Visible && TextureImages.Load(b.Image, baseDirectory) is { IsPlaceholder: true, Problem: { } p })
                problems.Add($"{v} backdrop: {p}");
        return problems;
    }

    /// <summary>
    /// The picture as it shows: inverted, then its contrast scaled about mid grey, then its brightness added (alpha
    /// kept). Placeholders and unadjusted pictures come back as they are. Made once per picture and settings, so a
    /// renderer sees the same image (and an OpenGL texture made from it lasts) from frame to frame.
    /// </summary>
    public static TextureImage Adjusted(TextureImage image, Backdrop b)
    {
        if (image.IsPlaceholder || !b.Adjusted) return image;
        var key = (b.Brightness, b.Contrast, b.Invert);
        lock (AdjustedCache)
        {
            var byImage = AdjustedCache.GetOrCreateValue(image);
            if (byImage.TryGetValue(key, out var made)) return made;
            var src = image.Rgba;
            var rgba = new byte[src.Length];
            // A table per setting: the same sum for every channel value.
            var table = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                if (b.Invert) c = 1 - c;
                c = (c - 0.5) * b.Contrast + 0.5 + b.Brightness;
                table[i] = (byte)Math.Round(Math.Clamp(c, 0, 1) * 255);
            }
            for (int i = 0; i < src.Length; i += 4)
            {
                rgba[i] = table[src[i]];
                rgba[i + 1] = table[src[i + 1]];
                rgba[i + 2] = table[src[i + 2]];
                rgba[i + 3] = src[i + 3];
            }
            made = new TextureImage(image.Width, image.Height, rgba) { Path = image.Path };
            if (byImage.Count > 8) byImage.Clear();
            byImage[key] = made;
            return made;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextureImage, Dictionary<(double, double, bool), TextureImage>> AdjustedCache = new();
}

/// <summary>
/// Backdrop setups as JSON: the "Save/Load backdrop setup" files, and what <see cref="BackdropMemory"/> keeps for each
/// object. Image paths are written relative to the file's folder where they can be, so a folder of blueprints and its
/// setup can be moved together; settings at their defaults are left out.
/// </summary>
public static class BackdropFiles
{
    public const int Version = 1;

    /// <summary>A setup as JSON, its image paths relative to <paramref name="baseDirectory"/> where they can be.</summary>
    public static JsonObject ToJson(BackdropSet set, string? baseDirectory)
    {
        var o = new JsonObject { ["format"] = "AVAModeler backdrops", ["version"] = Version };
        foreach (var (v, b) in set.All())
        {
            var j = new JsonObject
            {
                ["image"] = Relative(b.Image, baseDirectory),
                ["center"] = new JsonArray(b.Center.X, b.Center.Y),
                ["width"] = b.Width,
            };
            if (b.Height is { } h) j["height"] = h;
            if (b.Brightness != 0) j["brightness"] = b.Brightness;
            if (b.Contrast != 1) j["contrast"] = b.Contrast;
            if (b.Opacity != Backdrop.DefaultOpacity) j["opacity"] = b.Opacity;
            if (b.Invert) j["invert"] = true;
            if (!b.Visible) j["visible"] = false;
            o[v.ToString().ToLowerInvariant()] = j;
        }
        return o;
    }

    /// <summary>A setup from JSON, its relative image paths made absolute from <paramref name="baseDirectory"/>. Bad settings throw a <see cref="FormatException"/>.</summary>
    public static BackdropSet FromJson(JsonNode? node, string? baseDirectory)
    {
        if (node is not JsonObject o) throw new FormatException("A backdrop setup is a JSON object.");
        var set = BackdropSet.Empty;
        foreach (var v in Enum.GetValues<BackdropView>())
        {
            if (o[v.ToString().ToLowerInvariant()] is not JsonObject j) continue;
            try
            {
                string image = j["image"]?.GetValue<string>() ?? throw new FormatException($"The {v} backdrop has no image.");
                var c = j["center"] as JsonArray;
                var b = new Backdrop
                {
                    Image = image,
                    Center = c is { Count: 2 } ? new Vec2(c[0]!.GetValue<double>(), c[1]!.GetValue<double>()) : Vec2.Zero,
                    Width = j["width"]?.GetValue<double>() ?? 1,
                    Height = j["height"]?.GetValue<double>(),
                    Brightness = j["brightness"]?.GetValue<double>() ?? 0,
                    Contrast = j["contrast"]?.GetValue<double>() ?? 1,
                    Opacity = j["opacity"]?.GetValue<double>() ?? Backdrop.DefaultOpacity,
                    Invert = j["invert"]?.GetValue<bool>() ?? false,
                    Visible = j["visible"]?.GetValue<bool>() ?? true,
                }.Checked();
                set = set.With(v, b);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or JsonException)
            {
                throw new FormatException($"The {v} backdrop's settings aren't right: {e.Message}", e);
            }
        }
        return set.Rooted(baseDirectory);
    }

    /// <summary>Writes a setup file (images relative to its folder).</summary>
    public static void Save(BackdropSet set, string path)
    {
        string full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(full, ToJson(set, Path.GetDirectoryName(full)).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Reads a setup file (images from its folder).</summary>
    public static BackdropSet Load(string path)
    {
        string full = Path.GetFullPath(path);
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(full)); }
        catch (JsonException e) { throw new FormatException($"{Path.GetFileName(path)} isn't a backdrop setup (not JSON: {e.Message}).", e); }
        return FromJson(node, Path.GetDirectoryName(full));
    }

    /// <summary>An image path relative to a folder (forward slashes) when it's on the same drive; otherwise as it is.</summary>
    public static string Relative(string image, string? baseDirectory)
    {
        if (baseDirectory == null || !Path.IsPathRooted(image)) return image.Replace('\\', '/');
        try
        {
            string rel = Path.GetRelativePath(baseDirectory, image);
            return Path.IsPathRooted(rel) ? image : rel.Replace('\\', '/');
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return image; }
    }
}

/// <summary>
/// The backdrops remembered for each object file, as classic Modeler kept its backdrops in its own settings rather than
/// in the object: a JSON file in the app's settings folder, keyed by the object's full path, each setup's images
/// relative to its object's folder. The most recent <see cref="Capacity"/> objects are kept. A file that can't be read
/// or written is passed over quietly: backdrops are a convenience, never a reason to fail.
/// </summary>
public sealed class BackdropMemory(string file)
{
    public string File { get; } = file;
    public const int Capacity = 100;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The setup remembered for an object file, or null.</summary>
    public BackdropSet? Recall(string objectPath)
    {
        string key = Path.GetFullPath(objectPath);
        foreach (var (path, setup) in Read())
            if (PathComparer.Equals(path, key))
            {
                try { return BackdropFiles.FromJson(setup, Path.GetDirectoryName(key)); }
                catch (FormatException) { return null; }
            }
        return null;
    }

    /// <summary>Remembers an object's setup (an empty one forgets it), most recent first.</summary>
    public void Remember(string objectPath, BackdropSet set)
    {
        string key = Path.GetFullPath(objectPath);
        var entries = Read().Where(e => !PathComparer.Equals(e.Path, key)).ToList();
        if (!set.IsEmpty) entries.Insert(0, (key, BackdropFiles.ToJson(set, Path.GetDirectoryName(key))));
        if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
        var objects = new JsonArray();
        foreach (var (path, setup) in entries)
        {
            var copy = (JsonObject)setup.DeepClone();
            copy.Remove("format");
            copy.Remove("version");
            objects.Add(new JsonObject { ["object"] = path, ["backdrops"] = copy });
        }
        var root = new JsonObject { ["format"] = "AVAModeler backdrop memory", ["version"] = BackdropFiles.Version, ["objects"] = objects };
        try
        {
            if (Path.GetDirectoryName(File) is { } dir) Directory.CreateDirectory(dir);
            string temp = File + ".tmp";
            System.IO.File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(temp, File, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private List<(string Path, JsonObject Setup)> Read()
    {
        var list = new List<(string, JsonObject)>();
        try
        {
            if (!System.IO.File.Exists(File)) return list;
            if (JsonNode.Parse(System.IO.File.ReadAllText(File))?["objects"] is not JsonArray objects) return list;
            foreach (var o in objects.OfType<JsonObject>())
                if (o["object"]?.GetValue<string>() is { } path && o["backdrops"] is JsonObject setup)
                    list.Add((path, (JsonObject)setup.DeepClone()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        return list;
    }
}
