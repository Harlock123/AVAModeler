using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Layout;
using AVAModeler.Scene;
using ModelContextProtocol.Server;
using static AVAModeler.Mcp.SceneCensus;

namespace AVAModeler.Mcp;

/// <summary>
/// The scene's images and compositing (Layout's Images panel and Effects › Compositing), and objects' dissolve. Images
/// agents give are files inside the working folder (<see cref="Workspace.Resolve"/>), only read. Each call that changes
/// the scene is one undo step, and a failed one changes nothing.
/// </summary>
public sealed partial class ModelerTools
{
    [McpServerTool(Name = "load_image", Destructive = true)]
    [Description("Adds a picture to the scene's image list (Layout's Images panel), for compositing (set_compositing) and for animated textures. A still is one file; a sequence (sequence true) is numbered files, one a frame: give any one of them, such as frames/sky0001.png, and the digits before the extension are the frame number (the file for frame f is number f + offset, with the same digit count, sky0002.png and so on). Outside the numbers on disk a sequence holds its first or last file, or with loop true starts again. An image texture whose file is a sequence in the list shows the sequence's file for the frame rendered (render_scene; the app's views keep showing the texture's own file). The file must be inside the working folder (.png, .jpg, .bmp…); it is only read. Loading an image already in the list changes its sequence, offset and loop. Returns the list.")]
    public Task<string> LoadImage(
        [Description("The image file, relative to the working folder (a sequence: any one of its numbered files).")] string path,
        [Description("A numbered sequence, one file a frame (default false: a still).")] bool? sequence = null,
        [Description("A sequence's number at frame f is f + offset (whole frames, default 0).")] int? offset = null,
        [Description("A sequence starts again after its last file (default false: the first and last hold).")] bool? loop = null,
        CancellationToken cancel = default) =>
        DoScene("loaded an image", (c, l) =>
        {
            string full = Files(c).Resolve(path);
            if (!BackdropImageTypes.Contains(Path.GetExtension(full).ToLowerInvariant()))
                throw new NotSupportedException($"An image is a picture: {string.Join(", ", BackdropImageTypes)}, not {Path.GetFileName(full)}.");
            var s = l.Scene;
            var had = SceneImages.Find(s, full);
            var image = (had ?? new SceneImage { Path = full }) with
            {
                Sequence = sequence ?? had?.Sequence ?? false,
                Offset = offset ?? had?.Offset ?? 0,
                Loop = loop ?? had?.Loop ?? false,
            };
            image.Checked();
            if (!image.Sequence && !File.Exists(full)) throw new FileNotFoundException($"There's no image {path} in the working folder ({host.Root}).", full);
            if (image.Sequence && ImageSequence.Range(image) == null)
                throw new FileNotFoundException($"There are no numbered files like {Path.GetFileName(full)} in {Path.GetDirectoryName(full)}: a sequence needs its files (sky0001.png, sky0002.png…).", full);
            var list = s.Images.Select(i => i == had ? image : i).ToList();
            if (had == null) list.Add(image);
            l.SetImages(list);
            return ImagesNow(l);
        }, cancel);

    [McpServerTool(Name = "remove_image", Destructive = true)]
    [Description("Takes a picture out of the scene's image list, by its file (as load_image or get_scene gives it) or its number from 1. One the compositing uses can't be removed: set_compositing it to \"\" first. Textures that used it as a sequence go back to their own file. Returns the list.")]
    public Task<string> RemoveImage(
        [Description("The image's file (relative to the working folder) or its number in the list, from 1.")] string image,
        CancellationToken cancel = default) =>
        DoScene("removed an image", (c, l) =>
        {
            var s = l.Scene;
            var entry = ImageIn(c, s, image);
            var comp = s.Compositing;
            foreach (var (p, what) in new[] { (comp.Background, "background"), (comp.Foreground, "foreground"), (comp.ForegroundAlpha, "foreground alpha") })
                if (p.Length > 0 && SceneImages.Same(p, entry.Path))
                    throw new ArgumentException($"{Path.GetFileName(entry.Path)} is the compositing's {what} image: set_compositing it to \"\" first, then remove it.");
            l.SetImages(s.Images.Where(i => i != entry).ToList());
            return ImagesNow(l);
        }, cancel);

    [McpServerTool(Name = "set_compositing", Destructive = true)]
    [Description("LightWave's Effects › Compositing, leaving out what you don't give. background: an image from the scene's list (load_image it first) shown where camera rays meet nothing, stretched to the frame, in place of the backdrop, gradient or environment (reflections still see those; the picture's alpha stays the objects'). foreground: an image from the list laid over the finished picture, stretched to the frame, by its alpha; key cuts it out: \"none\" (default: laid by its own alpha), \"color\" (left out where its colour lies between key_low and key_high, [r, g, b] 0 to 255 each, channel by channel; e.g. key_low [0, 200, 0] key_high [80, 255, 80] keys out green), or \"alpha\" (by its alpha channel, or foreground_alpha's brightness if given, also from the list). foreground_dissolve fades it, 0 (default) to 1. Where the foreground is laid the alpha channel becomes covered. A sequence in the list plays one file a frame. \"\" turns an image off. Off by default.")]
    public Task<string> SetCompositing(
        [Description("The background image, a file in the scene's image list (relative to the working folder) or its number from 1; \"\" for none.")] string? background = null,
        [Description("The foreground image, a file in the list or its number; \"\" for none.")] string? foreground = null,
        [Description("How the foreground is cut out: none, color or alpha (default none).")] string? key = null,
        [Description("The colour key's low colour [r, g, b], 0 to 255 (default [0, 0, 0]).")] double[]? key_low = null,
        [Description("The colour key's high colour [r, g, b], 0 to 255 (default [0, 0, 0]).")] double[]? key_high = null,
        [Description("For the alpha key: an image from the list whose brightness is the foreground's alpha; \"\" for the foreground's own alpha (the default).")] string? foreground_alpha = null,
        [Description("How far the foreground is faded out, 0 (default) to 1.")] double? foreground_dissolve = null,
        CancellationToken cancel = default) =>
        DoScene("changed the compositing", (c, l) =>
        {
            var s = l.Scene;
            string? Pick(string? given) => given == null ? null : given.Trim().Length == 0 ? "" : ImageIn(c, s, given).Path;
            var comp = s.Compositing;
            if (Pick(background) is { } bg) comp = comp with { Background = bg };
            if (Pick(foreground) is { } fg) comp = comp with { Foreground = fg };
            if (Pick(foreground_alpha) is { } fa) comp = comp with { ForegroundAlpha = fa };
            if (key != null) comp = comp with { Key = Json.Choice<ForegroundKey>(key, "key") };
            if (key_low != null) comp = comp with { KeyLow = Colour(key_low, "key_low") };
            if (key_high != null) comp = comp with { KeyHigh = Colour(key_high, "key_high") };
            if (foreground_dissolve is { } fd) comp = comp with { ForegroundDissolve = fd };
            // A foreground taken away takes its key with it.
            if (comp.Foreground.Length == 0 && key == null) comp = comp with { Key = ForegroundKey.None, ForegroundAlpha = "" };
            l.SetCompositing(comp.Checked());
            var result = new JsonObject { ["compositing"] = CompositingJson(s.Compositing), ["images"] = Images(s, l.Frame) };
            if (SceneImages.Problems(s, l.Frame) is { Count: > 0 } problems) result["warnings"] = Strings(problems);
            return result;
        }, cancel);

    [McpServerTool(Name = "set_dissolve", Destructive = true)]
    [Description("Sets an object's dissolve (LightWave's Object Dissolve) at a frame: 0 is solid (the default), 1 gone, and between it fades, letting that share of what's behind it show through (the render's alpha falls with it, and its shadows lighten); wholly dissolved it isn't rendered and casts no shadow. The app's views draw it with a screen-door dither. It's the keyable channel \"dissolve\" (set_key, get_keys, delete_key and the dope sheet see it). " + KeyRule)]
    public Task<string> SetDissolve(
        [Description("The object's name, or #index from get_scene.")] string item,
        [Description("0 (solid) to 1 (gone).")] double value,
        [Description("The frame (default: the current frame).")] double? frame = null,
        [Description("Key (true) or just set (false); default: Layout's Auto key (on unless the person turned it off).")] bool? key = null,
        CancellationToken cancel = default) =>
        DoScene("set a dissolve", (c, l) =>
        {
            var obj = SceneObject(l.Scene, item);
            double f = SceneCensus.Frame(l, frame);
            if (!(value >= 0 && value <= 1)) throw new ArgumentException("value must be from 0 (solid) to 1 (gone).");
            bool k = key ?? l.AutoKey;
            Keying.Set(obj.Dissolve, l.Scene, f, value, k, $"{obj.Name}'s dissolve");
            l.Scene.Notify();
            var result = ItemNow(c, l, obj, f);
            result["dissolve"] = Census.Round(obj.DissolveAt(f));
            result["set"] = k ? $"a key at frame {f:0.###}" : "the value (no new key)";
            return result;
        }, cancel);

    /// <summary>An image in the scene's list, by its file (relative to the working folder) or its number from 1.</summary>
    private SceneImage ImageIn(AVAModeler.Tools.Commands c, AVAModeler.Scene.Scene s, string given)
    {
        string g = given.Trim();
        if (s.Images.Count == 0) throw new ArgumentException("The scene's image list is empty: add the picture with load_image first.");
        if (int.TryParse(g.TrimStart('#'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
        {
            if (n < 1 || n > s.Images.Count) throw new ArgumentException($"There's no image {n}: the list's images are 1 to {s.Images.Count}.");
            return s.Images[n - 1];
        }
        string full = Files(c).Resolve(g);
        return SceneImages.Find(s, full) ?? throw new ArgumentException(
            $"{g} isn't in the scene's image list ({string.Join(", ", s.Images.Select(i => Path.GetFileName(i.Path)))}): add it with load_image first.");
    }

    private static JsonObject ImagesNow(LayoutSession l)
    {
        var o = new JsonObject { ["images"] = Images(l.Scene, l.Frame) };
        if (l.Scene.Compositing.On) o["compositing"] = CompositingJson(l.Scene.Compositing);
        return o;
    }
}
