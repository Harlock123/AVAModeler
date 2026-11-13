using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Geometry;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>compare_to_reference: the model's outline against a reference picture's.</summary>
public sealed partial class ModelerTools
{
    [McpServerTool(Name = "compare_to_reference", ReadOnly = true, Idempotent = true)]
    [Description("Compares the model's outline (the foreground layers, instances included) with a reference picture's, so you can refine the model toward it: from the front, side or top view, or from a photo matched with match_photo (view \"photo:<name>\"). In a flat view with a backdrop (set_backdrop) the backdrop's placement lines the picture up with the world exactly; without one, the model's outline is scaled to the picture's subject by height and centred on it (then only the shape counts, not size or place). The reference's subject is what isn't transparent, or what differs from its corners' colour by more than threshold. Returns overlap (intersection over union, 1 is a perfect match), missing (the share of the reference the model doesn't cover) and extra (the share of the model outside it), where each mostly is (top left, right side, …), advice, and a picture: grey where both are, red the model only, blue the reference only.")]
    public async Task<CallToolResult> CompareToReference(
        [Description("front, side, top, or photo:<name> for a matched photo.")] string view = "front",
        [Description("The reference picture in the working folder (default: the view's backdrop, or the matched photo).")] string? image = null,
        [Description("How different from the background a pixel must be to count as the subject, 0 to 1 (default 0.15).")] double threshold = 0.15,
        CancellationToken cancel = default)
    {
        if (!(threshold > 0 && threshold < 1)) throw new McpException("threshold must be between 0 and 1.");
        var (picture, json) = await Do<(byte[], JsonObject)>("compared with the reference", c =>
        {
            var doc = c.Document;
            var mesh = new Mesh();
            foreach (int l in doc.ForegroundLayers) mesh.Append(Instancing.WithInstances(doc, l));
            if (mesh.Polygons.All(p => p.Count < 3)) throw new ArgumentException("The foreground has no faces to compare: model something first.");
            string? path = image != null ? Files(c).Resolve(image) : null;
            Func<Vec3, Vec2?>? project = null;
            bool fitted = false;
            BackdropView? flat = null;
            string v = view.Trim().ToLowerInvariant();
            if (v.StartsWith("photo:"))
            {
                string name = view.Trim()[6..].Trim();
                if (!doc.Photos.TryGetValue(name, out var photo) || PhotoMatch.CameraOf(photo) is not { } cam)
                    throw new ArgumentException(doc.Photos.Count == 0 ? "No photo is matched yet: match_photo first." : $"There's no matched photo \"{name}\". There are: {string.Join(", ", doc.Photos.Keys)}.");
                path ??= photo.Image is { Length: > 0 } pi ? pi : throw new ArgumentException("That photo was matched without its picture: give image.");
                project = cam.Project;
            }
            else
            {
                flat = v switch { "front" => BackdropView.Front, "side" => BackdropView.Side, "top" => BackdropView.Top, _ => throw new ArgumentException("view must be front, side, top or photo:<name>.") };
                var backdrop = doc.Backdrops[flat.Value];
                string? backdropFile = backdrop is { Image.Length: > 0 } bd ? TextureImages.Resolve(bd.Image, doc.ImageBase) : null;
                path ??= backdropFile ?? throw new ArgumentException($"Give image, or set a {v} backdrop first (set_backdrop).");
                if (backdrop != null && backdropFile != null && Path.GetFullPath(backdropFile) == Path.GetFullPath(path))
                {
                    var pic0 = TextureImages.Load(path);
                    project = p =>
                    {
                        var uv = BackdropMapping.ImagePoint(flat.Value, backdrop, pic0.Width, pic0.Height, p);
                        return new Vec2(uv.X * pic0.Width, (1 - uv.Y) * pic0.Height);
                    };
                }
            }
            var pic = TextureImages.Load(path);
            if (pic.IsPlaceholder) throw new ArgumentException(pic.Problem ?? $"Can't read {path}.");
            var reference = Silhouettes.Reference(pic, threshold);
            if (Silhouettes.Bounds(reference, pic.Width, pic.Height) is not { } rb) throw new ArgumentException("The reference picture has no subject against its background: try a lower threshold, or a picture with a plain background.");
            if (project == null)
            {
                // No placement: fit the model's outline to the subject by height, centred on it.
                fitted = true;
                var (right, up) = BackdropMapping.Axes(flat!.Value);
                var pts = mesh.Points.Select(p => new Vec2(Vec3.Dot(p, right), Vec3.Dot(p, up))).ToList();
                double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
                double scale = (rb.Y1 - rb.Y0 + 1) / Math.Max(maxY - minY, 1e-12);
                double cx = (rb.X0 + rb.X1 + 1) / 2.0, cy = (rb.Y0 + rb.Y1 + 1) / 2.0;
                double mx = (minX + maxX) / 2, my = (minY + maxY) / 2;
                project = p => new Vec2(cx + (Vec3.Dot(p, right) - mx) * scale, cy - (Vec3.Dot(p, up) - my) * scale);
            }
            var model = Silhouettes.Model(mesh, pic.Width, pic.Height, project);
            var cmp = Silhouettes.Compare(model, reference, pic.Width, pic.Height);
            var result = new JsonObject
            {
                ["view"] = view,
                ["reference"] = path,
                ["overlap"] = Math.Round(cmp.Overlap, 4),
                ["missing"] = Math.Round(cmp.Missing, 4),
                ["missing_where"] = cmp.MissingWhere,
                ["extra"] = Math.Round(cmp.Extra, 4),
                ["extra_where"] = cmp.ExtraWhere,
                ["fitted"] = fitted,
                ["advice"] = cmp.Advice,
            };
            if (fitted && Silhouettes.Bounds(model, pic.Width, pic.Height) is { } mb)
                result["width_to_height"] = new JsonObject
                {
                    ["model"] = Math.Round((mb.X1 - mb.X0 + 1) / (double)(mb.Y1 - mb.Y0 + 1), 3),
                    ["reference"] = Math.Round((rb.X1 - rb.X0 + 1) / (double)(rb.Y1 - rb.Y0 + 1), 3),
                };
            return (Png.Encode(cmp.Width, cmp.Height, cmp.Picture), result);
        }, cancel).ConfigureAwait(false);
        return new CallToolResult
        {
            Content = [ImageContentBlock.FromBytes(picture, "image/png"), new TextContentBlock { Text = json.ToJsonString() }],
        };
    }
}
