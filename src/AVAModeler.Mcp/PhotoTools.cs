using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Geometry;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>Photo match: a photo's camera from marks, and new points from two matched photos.</summary>
public sealed partial class ModelerTools
{
    [McpServerTool(Name = "match_photo", Idempotent = true)]
    [Description("Matches a photo to the model: give at least six marks, each a point of the current layer (by index) and where it shows in the photo (pixels from its top left, x right and y down), spread over the picture and over the model at different depths (corners of a box you've modeled to the photo's proportions). The camera is solved (the direct linear transform) and kept under the name: its position, the way it looks, its focal length and vertical field of view, and how far each mark misses (pixels; a few is good, tens means a mark is wrong). Then point_from_photos finds new points from two matched photos, and compare_to_reference can view the model from this camera. Not an undo step; not kept in the file.")]
    public Task<string> MatchPhoto(
        [Description("A name for the photo, e.g. \"front-left\".")] string name,
        [Description("The marks: [point_index, x, y] each, at least six.")] double[][] marks,
        [Description("The photo file in the working folder (its size is read from it).")] string? image = null,
        [Description("Without image: the photo's width in pixels.")] int? width = null,
        [Description("Without image: the photo's height in pixels.")] int? height = null,
        CancellationToken cancel = default) =>
        Do($"matched the {name} photo", c =>
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("The photo needs a name.");
            int w = width ?? 0, h = height ?? 0;
            string path = "";
            if (image != null)
            {
                path = Files(c).Resolve(image);
                var pic = TextureImages.Load(path);
                if (pic.IsPlaceholder) throw new ArgumentException(pic.Problem ?? $"Can't read {image}.");
                (w, h) = (pic.Width, pic.Height);
            }
            if (w <= 0 || h <= 0) throw new ArgumentException("Give the photo (image) or its width and height.");
            int layer = c.Document.CurrentLayer;
            var mesh = c.Document.Layers[layer].Mesh;
            var setup = new PhotoSetup { Image = path, Width = w, Height = h };
            foreach (var m in marks)
            {
                if (m.Length != 3) throw new ArgumentException("Each mark is [point_index, x, y].");
                int p = (int)m[0];
                if (p != m[0] || p < 0 || p >= mesh.Points.Count) throw new ArgumentException($"There's no point {m[0]}: the current layer's points are numbered 0 to {mesh.Points.Count - 1}.");
                setup.Marks.Add(new PhotoMarkAt(layer, p, new Vec2(m[1], m[2])));
            }
            var cam = PhotoMatch.Solve(c.Document, setup);
            c.Document.Photos[name.Trim()] = setup;
            return CameraJson(c.Document, name.Trim(), setup, cam);
        }, cancel);

    [McpServerTool(Name = "point_from_photos", Destructive = true)]
    [Description("Finds a place from two matched photos (match_photo): give where the same feature shows in each (pixels from the top left). The place nearest both rays is found and, unless add is false, added as a point to the current layer (one undo step). Returns it, its index, and how far the two rays miss each other there (metres; much more than the model's detail means the marks don't show the same feature).")]
    public Task<string> PointFromPhotos(
        [Description("The first photo's name.")] string photo_a,
        [Description("Where the feature shows in it, [x, y] pixels.")] double[] at_a,
        [Description("The second photo's name (taken from another side).")] string photo_b,
        [Description("Where the same feature shows in it, [x, y].")] double[] at_b,
        [Description("Add the point to the current layer (default true).")] bool add = true,
        CancellationToken cancel = default) =>
        Do("found a point from photos", c =>
        {
            CameraMatch Cam(string n) => c.Document.Photos.TryGetValue(n.Trim(), out var s) && PhotoMatch.CameraOf(s) is { } cm ? cm
                : throw new ArgumentException(c.Document.Photos.Count == 0 ? "No photo is matched yet: match_photo first." : $"There's no matched photo \"{n}\". There are: {string.Join(", ", c.Document.Photos.Keys)}.");
            Vec2 Px(double[] v, string what) => v.Length == 2 ? new Vec2(v[0], v[1]) : throw new ArgumentException($"{what} is [x, y] in pixels.");
            var (p, gap) = PhotoMatch.Triangulate(Cam(photo_a), Px(at_a, "at_a"), Cam(photo_b), Px(at_b, "at_b"));
            var result = new JsonObject { ["point"] = Census.Vec(p), ["gap"] = Census.Round(gap) };
            if (add) result["index"] = c.AddPoint(p);
            return result;
        }, cancel);

    private static JsonObject CameraJson(Document doc, string name, PhotoSetup setup, CameraMatch cam)
    {
        var misses = new JsonArray();
        foreach (var m in setup.Marks)
        {
            var at = doc.Layers[m.Layer].Mesh.Points[m.Point];
            misses.Add(cam.Project(at) is { } q ? Census.Round((q - m.Pixel).Length, 2) : (JsonNode?)null);
        }
        return new JsonObject
        {
            ["photo"] = name,
            ["size"] = new JsonArray(setup.Width, setup.Height),
            ["camera_position"] = Census.Vec(cam.Position),
            ["looking"] = Census.Vec(cam.Forward),
            ["focal_pixels"] = Census.Round(cam.FocalPixels, 2),
            ["vertical_fov"] = Census.Round(cam.VerticalFov, 3),
            ["error_pixels"] = Census.Round(cam.Error, 3),
            ["mark_misses"] = misses,
        };
    }
}
