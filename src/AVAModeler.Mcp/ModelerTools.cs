using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// The MCP tools: what an agent can do with AVA Modeler. Each handler reaches the document only through
/// <see cref="IModelerHost.RunAsync{T}"/> and the command API (<see cref="Commands"/>, the Modeler's tools by name,
/// <see cref="Macros"/>), as the app's own menus and macros do, so the same code serves headless and live. Every
/// change is one undo step, and a call that fails leaves the document as it was. Failures come back as MCP tool
/// errors (<see cref="McpException"/>) with a message that says what to do instead.
/// </summary>
/// <remarks>The parameter names are the JSON names agents see, hence snake_case.</remarks>
public sealed class ModelerTools(IModelerHost host)
{
    private Workspace? _workspace;

    /// <summary>How long run_lua lets a macro run, at most.</summary>
    public const double MaxLuaSeconds = 300;

    // ───────────────────────────── discovery ─────────────────────────────

    [McpServerTool(Name = "list_tools", ReadOnly = true, Idempotent = true)]
    [Description("Lists every modeling tool AVA Modeler has (built in and from plugins): its name, its tab (Objects, Modify, Multiply, Polygon, Tools) and a one-line summary. Run one with run_tool; describe_tool gives its parameters.")]
    public Task<string> ListTools(CancellationToken cancel) =>
        Do("list_tools", c => ToolCatalog.List(c.Registry), cancel);

    [McpServerTool(Name = "describe_tool", ReadOnly = true, Idempotent = true)]
    [Description("Describes one modeling tool: what it does and each parameter's name, type, default, unit (metres or degrees), limits or choices, and meaning, plus an example run_tool call with the defaults. Vectors are [x, y, z].")]
    public Task<string> DescribeTool(
        [Description("The tool's name as list_tools gives it, e.g. \"Box\" or \"Lathe\" (any case).")] string name,
        CancellationToken cancel) =>
        Do("describe_tool", c => ToolCatalog.Describe(c.Registry, name), cancel);

    // ───────────────────────────── acting ─────────────────────────────

    [McpServerTool(Name = "run_tool", Destructive = true)]
    [Description("Runs a modeling tool with parameters by name, as one undo step, exactly as the app's numeric panel does. It acts on the selection in the foreground layers; with nothing selected it acts on everything there. New shapes (Box, Ball, Disc, Cone, Text) go into the current layer. Parameters you leave out keep their defaults (see describe_tool): numbers, true/false, choices by name, vectors as [x, y, z] in metres (angles in degrees). Returns the point and polygon counts before and after, and the selection. A bad name or value changes nothing and says why.")]
    public Task<string> RunTool(
        [Description("The tool's name, e.g. \"Box\", \"Extrude\", \"Lathe\".")] string name,
        [Description("Parameters by name, e.g. {\"Size\": [2, 0.1, 1], \"Center\": [0, 0.75, 0]}. Optional.")] Dictionary<string, JsonElement>? @params = null,
        CancellationToken cancel = default) =>
        Do($"ran {name}", c =>
        {
            if (!c.Registry.Has(name)) throw new ArgumentException(ToolCatalog.UnknownTool(c.Registry, name));
            var probe = c.Registry.Create(name);
            if (!probe.Scriptable)
                throw new ArgumentException($"{probe.Name} only works with the mouse in the app. {(probe.Name == "Points" ? "Use add_points instead." : "Select the point and use Move instead.")}");
            var args = @params?.ToDictionary(kv => kv.Key, kv => Json.Plain(kv.Value, $"{probe.Name}'s {kv.Key}"), StringComparer.OrdinalIgnoreCase);
            var before = Census.Of(c.Document);
            var tool = c.Run(name, args);
            return Census.Changes(c.Document, before, tool.Name);
        }, cancel);

    [McpServerTool(Name = "add_points", Destructive = true)]
    [Description("Adds points to the current layer (the lowest-numbered foreground layer) in one undo step, and returns their indices to build polygons from with add_polygons. Coordinates are in metres: +X right, +Y up, +Z away from the viewer in the Front view.")]
    public Task<string> AddPoints(
        [Description("The points as [x, y, z] triples, e.g. [[0, 0, 0], [1, 0, 0], [1, 1, 0]].")] double[][] points,
        CancellationToken cancel) =>
        Do("added points", c =>
        {
            if (points.Length == 0) throw new ArgumentException("Give at least one point.");
            for (int i = 0; i < points.Length; i++)
                if (points[i] is not { Length: 3 }) throw new ArgumentException($"Point {i} needs three numbers [x, y, z].");
            var before = Census.Of(c.Document);
            var added = c.AddPoints(points.Select(p => new Vec3(p[0], p[1], p[2])));
            var result = Census.Changes(c.Document, before, "Add points");
            result["layer"] = c.Document.CurrentLayer;
            result["indices"] = Census.Range(added);
            return result;
        }, cancel);

    [McpServerTool(Name = "add_polygons", Destructive = true)]
    [Description("Adds polygons through existing points of the current layer, in one undo step, all on one surface (made if new). A polygon of one point is a dot, two a line, three or more a face. A face is seen from the side where its points run clockwise, and its normal points out of that side: list them clockwise as seen from outside the shape. Returns the new polygons' indices.")]
    public Task<string> AddPolygons(
        [Description("Each polygon as a list of point indices (from add_points or get_points), e.g. [[0, 1, 2, 3]].")] int[][] polygons,
        [Description("The surface the polygons get (made if new). Default: \"Default\".")] string? surface = null,
        CancellationToken cancel = default) =>
        Do("added polygons", c =>
        {
            if (polygons.Length == 0) throw new ArgumentException("Give at least one polygon.");
            var before = Census.Of(c.Document);
            var added = c.AddPolygons(polygons, string.IsNullOrWhiteSpace(surface) ? null : surface);
            var result = Census.Changes(c.Document, before, polygons.Length == 1 ? "Add polygon" : "Add polygons");
            result["layer"] = c.Document.CurrentLayer;
            result["indices"] = Census.Range(added);
            return result;
        }, cancel);

    [McpServerTool(Name = "set_selection_mode", Idempotent = true)]
    [Description("Sets what the selection holds and tools act on: \"points\", \"polygons\", \"edges\" or \"volume\" (a box: in it are the points inside, and the polygons with any point inside). Each mode keeps its own selection. Not an undo step.")]
    public Task<string> SetSelectionMode(
        [Description("points, polygons, edges or volume.")] string mode,
        CancellationToken cancel) =>
        Do($"selection mode {mode}", c =>
        {
            c.SelectMode(Json.Choice<SelectionMode>(mode, "mode"));
            return Census.Selection(c.Document);
        }, cancel);

    [McpServerTool(Name = "select_points", Idempotent = true)]
    [Description("Selects points of the current layer by index and switches to Points mode. Replaces the point selection unless add is true. The order they're given is the order Make Polygon joins them in. Not an undo step.")]
    public Task<string> SelectPoints(
        [Description("Point indices in the current layer, from 0.")] int[] indices,
        [Description("Add to the points already selected rather than replacing them.")] bool add = false,
        CancellationToken cancel = default) =>
        Do("selected points", c =>
        {
            Census.CheckIndices(indices, c.Document.Current.Mesh.Points.Count, "point");
            c.SelectMode(SelectionMode.Points);
            if (!add) c.SelectNone();
            c.SelectPoints(indices);
            return Census.Selection(c.Document);
        }, cancel);

    [McpServerTool(Name = "select_polygons", Idempotent = true)]
    [Description("Selects polygons of the current layer by index and switches to Polygons mode. Replaces the polygon selection unless add is true. Not an undo step.")]
    public Task<string> SelectPolygons(
        [Description("Polygon indices in the current layer, from 0.")] int[] indices,
        [Description("Add to the polygons already selected rather than replacing them.")] bool add = false,
        CancellationToken cancel = default) =>
        Do("selected polygons", c =>
        {
            Census.CheckIndices(indices, c.Document.Current.Mesh.Polygons.Count, "polygon");
            c.SelectMode(SelectionMode.Polygons);
            if (!add) c.SelectNone();
            c.SelectPolygons(indices);
            return Census.Selection(c.Document);
        }, cancel);

    [McpServerTool(Name = "select_box", Idempotent = true)]
    [Description("Selects, in the current selection mode, what lies inside a box (corners in metres) in the foreground layers: in Points mode the points inside, in Polygons mode the polygons wholly inside, in Volume mode the box itself becomes the volume. Replaces the selection unless add is true. Not an undo step.")]
    public Task<string> SelectBox(
        [Description("One corner [x, y, z].")] double[] min,
        [Description("The opposite corner [x, y, z].")] double[] max,
        [Description("Add to the current selection rather than replacing it.")] bool add = false,
        CancellationToken cancel = default) =>
        Do("selected a box", c =>
        {
            var a = Json.Vector(min, "min");
            var b = Json.Vector(max, "max");
            if (!add) c.SelectNone();
            c.SelectInside(Box3.FromCorners(a, b));
            return Census.Selection(c.Document);
        }, cancel);

    [McpServerTool(Name = "select_all", Idempotent = true)]
    [Description("Selects everything in the foreground layers in the current mode (points, polygons or edges). Not an undo step.")]
    public Task<string> SelectAll(CancellationToken cancel) =>
        Do("selected all", c => { c.SelectAll(); return Census.Selection(c.Document); }, cancel);

    [McpServerTool(Name = "select_none", Idempotent = true)]
    [Description("Clears the selection in the current mode, so tools act on everything in the foreground layers again. Not an undo step.")]
    public Task<string> SelectNone(CancellationToken cancel) =>
        Do("deselected", c => { c.SelectNone(); return Census.Selection(c.Document); }, cancel);

    [McpServerTool(Name = "select_invert")]
    [Description("Inverts the selection in the current mode: what was selected isn't, and the rest is. Not an undo step.")]
    public Task<string> SelectInvert(CancellationToken cancel) =>
        Do("inverted the selection", c => { c.Invert(); return Census.Selection(c.Document); }, cancel);

    [McpServerTool(Name = "select_connected", Idempotent = true)]
    [Description("Adds to the selection everything connected to it through shared points (a whole separate piece, such as one leg of a table). Not an undo step.")]
    public Task<string> SelectConnected(CancellationToken cancel) =>
        Do("selected connected", c => { SelectionOps.Connected(c.Document); return Census.Selection(c.Document); }, cancel);

    [McpServerTool(Name = "set_layers", Idempotent = true)]
    [Description("Chooses the foreground layers (being edited; new geometry goes into the lowest-numbered one) and the background layers (shown dimmed, and the other operand for Boolean, Drill, Path Extrude and Rail Extrude). The rest are turned off. Layers are numbered from 0 (the app's layer buttons show them from 1). Not an undo step.")]
    public Task<string> SetLayers(
        [Description("Foreground layer numbers, from 0, e.g. [0]. At least one.")] int[] foreground,
        [Description("Background layer numbers, from 0, e.g. [1]. Optional.")] int[]? background = null,
        CancellationToken cancel = default) =>
        Do("set the layers", c =>
        {
            if (foreground.Length == 0) throw new ArgumentException("Give at least one foreground layer.");
            var bg = background ?? [];
            foreach (int l in foreground.Concat(bg))
                if (l < 0 || l >= 100) throw new ArgumentException($"There's no layer {l}: layers are numbered from 0 (and at most 99).");
            if (foreground.Intersect(bg).FirstOrDefault(-1) is var both and >= 0) throw new ArgumentException($"Layer {both} can't be in both the foreground and the background.");
            c.SetLayers(foreground, bg);
            return Census.Document(c.Document, brief: true);
        }, cancel);

    [McpServerTool(Name = "undo", Destructive = true)]
    [Description("Undoes the last change (an agent's or, in the live app, the person's), as Ctrl+Z does. Returns what was undone.")]
    public Task<string> Undo(CancellationToken cancel) =>
        Do("undid", c =>
        {
            string? label = c.Document.Undo.UndoLabel;
            bool done = c.Undo();
            var result = Census.Document(c.Document, brief: true);
            result["undone"] = done ? label : null;
            if (!done) result["note"] = "There was nothing to undo.";
            return result;
        }, cancel);

    [McpServerTool(Name = "redo", Destructive = true)]
    [Description("Redoes the last change undone. Returns what was redone.")]
    public Task<string> Redo(CancellationToken cancel) =>
        Do("redid", c =>
        {
            string? label = c.Document.Undo.RedoLabel;
            bool done = c.Redo();
            var result = Census.Document(c.Document, brief: true);
            result["redone"] = done ? label : null;
            if (!done) result["note"] = "There was nothing to redo.";
            return result;
        }, cancel);

    [McpServerTool(Name = "new", Destructive = true)]
    [Description("Starts a new, empty object (ten empty layers, layer 0 in the foreground, one surface \"Default\"), clearing the undo history. Refuses if the object has unsaved changes, unless discard_changes is true.")]
    public Task<string> New(
        [Description("Throw away unsaved changes.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        Do("started a new object", c =>
        {
            using (Files(c).Confine(c, allowDiscard: discard_changes)) c.New();
            return Census.Document(c.Document, brief: true);
        }, cancel);

    [McpServerTool(Name = "open", Destructive = true)]
    [Description("Opens an object file in place of the current object: LightWave .lwo (LWOB, LWLO and LWO2), Wavefront .obj or VideoScape .geo, plus any plugin format. The path is relative to the working folder and must stay inside it. Refuses if the current object has unsaved changes, unless discard_changes is true.")]
    public Task<string> Open(
        [Description("The file, relative to the working folder, e.g. \"models/table.lwo\".")] string path,
        [Description("Throw away the current object's unsaved changes.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        Do($"opened {Path.GetFileName(path)}", c =>
        {
            using (Files(c).Confine(c, allowDiscard: discard_changes)) c.Load(path);
            var result = Census.Document(c.Document, brief: true);
            if (TextureImages.Problems(c.Document.Surfaces, c.Document.ImageBase) is { Count: > 0 } problems)
                result["warnings"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray());
            return result;
        }, cancel);

    [McpServerTool(Name = "save", Destructive = true)]
    [Description("Saves the object, the format chosen by the extension: .lwo (LightWave LWOB/LWLO, the native format: keeps every layer, surface, texture and UV map), .obj (Wavefront, with a .mtl beside it) or .geo (VideoScape), plus any plugin format. The path is relative to the working folder and must stay inside it; folders are made as needed. An existing file is only replaced when overwrite is true.")]
    public Task<string> Save(
        [Description("The file, relative to the working folder, e.g. \"table.lwo\".")] string path,
        [Description("Replace the file if it already exists.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Do($"saved {Path.GetFileName(path)}", c =>
        {
            string full = Files(c).Save(c.Document, path, overwrite);
            return new JsonObject { ["saved"] = full, ["bytes"] = new FileInfo(full).Length };
        }, cancel);

    [McpServerTool(Name = "set_surface", Destructive = true)]
    [Description("Sets how a surface looks, making it if new, and (unless assign is false) gives it to the selected polygons in the foreground layers — to every polygon there if none are selected. One undo step. Leave out what you don't want to change. Colour is [r, g, b], 0 to 255; diffuse, specular, luminosity, reflection and transparency are fractions from 0 to 1.")]
    public Task<string> SetSurface(
        [Description("The surface's name, e.g. \"Wood\".")] string name,
        [Description("Colour [r, g, b], 0 to 255 each.")] int[]? color = null,
        [Description("How much light it scatters, 0 to 1 (default 1).")] double? diffuse = null,
        [Description("Strength of highlights, 0 to 1.")] double? specular = null,
        [Description("Highlight tightness as LightWave stores it: 16 low, 64 medium, 256 high, 1024 very high.")] double? glossiness = null,
        [Description("Self-illumination, 0 to 1.")] double? luminosity = null,
        [Description("Mirror reflection, 0 to 1 (seen in render).")] double? reflection = null,
        [Description("0 opaque to 1 clear (seen in render).")] double? transparency = null,
        [Description("Smooth-shade it (faces meeting at under smoothing_angle look like one curved surface).")] bool? smoothing = null,
        [Description("Smoothing angle in degrees, 0 to 180 (default 89.5).")] double? smoothing_angle = null,
        [Description("Draw and render both sides of its faces.")] bool? double_sided = null,
        [Description("Give the surface to the selected polygons (all of the foreground's if none are selected). False only changes the look.")] bool assign = true,
        CancellationToken cancel = default) =>
        Do($"set the {name} surface", c =>
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A surface needs a name.");
            Rgb? rgb = null;
            if (color != null)
            {
                if (color.Length != 3 || color.Any(v => v < 0 || v > 255)) throw new ArgumentException("color is [r, g, b], three whole numbers from 0 to 255.");
                rgb = new Rgb((byte)color[0], (byte)color[1], (byte)color[2]);
            }
            Json.Fraction(diffuse, "diffuse");
            Json.Fraction(specular, "specular");
            Json.Fraction(luminosity, "luminosity");
            Json.Fraction(reflection, "reflection");
            Json.Fraction(transparency, "transparency");
            if (glossiness is { } g && (g < 1 || g > 1024 || !double.IsFinite(g))) throw new ArgumentException("glossiness must be from 1 to 1024 (16 low, 64 medium, 256 high, 1024 very high).");
            if (smoothing_angle is { } a && (a < 0 || a > 180 || !double.IsFinite(a))) throw new ArgumentException("smoothing_angle must be from 0 to 180 degrees.");
            var before = Census.Of(c.Document);
            c.Group($"Surface {name}", () =>
            {
                if (assign) c.Run("Surface", new Dictionary<string, object?> { ["SurfaceName"] = name });
                var doc = c.Document;
                doc.Edit($"Surface {name}", () =>
                {
                    var s = doc.Surfaces[doc.SurfaceIndex(name)];
                    if (rgb is { } col) s.Color = col;
                    if (diffuse is { } d) s.Diffuse = d;
                    if (specular is { } sp) s.Specular = sp;
                    if (glossiness is { } gl) s.Glossiness = gl;
                    if (luminosity is { } lu) s.Luminosity = lu;
                    if (reflection is { } re) s.Reflection = re;
                    if (transparency is { } tr) s.Transparency = tr;
                    if (smoothing is { } sm) s.Smoothing = sm;
                    if (smoothing_angle is { } an) s.SmoothingAngle = an;
                    if (double_sided is { } ds) s.DoubleSided = ds;
                }, []);
                return 0;
            });
            var result = Census.Changes(c.Document, before, $"Surface {name}");
            var surf = c.Document.Surfaces.First(s => s.Name == name);
            result["surface"] = Census.Surface(c.Document, surf);
            return result;
        }, cancel);

    [McpServerTool(Name = "set_texture", Destructive = true)]
    [Description("Lays an image texture on a surface's channel (its first image layer there, or a new one), making the surface if new. One undo step. The projection is planar (flat along axis), cylindrical or spherical (wrapped round axis), cubic (each face along the axis it faces most) or uv (through a UV map made with make_uvs). With fit (the default when center and size are left out) the image is sized to the surface's polygons, as LightWave's Automatic Sizing does. The image path is relative to the working folder (PNG, JPEG, BMP and more). The perspective view shows colour textures in mode textured; render shows every channel.")]
    public Task<string> SetTexture(
        [Description("The surface's name.")] string surface,
        [Description("The image file, relative to the working folder, e.g. \"images/wood.png\".")] string image,
        [Description("color (default), diffuse, specular, luminosity, reflection, transparency or bump.")] string channel = "color",
        [Description("planar (default), cylindrical, spherical, cubic or uv.")] string projection = "planar",
        [Description("x, y or z (default z): the axis a planar image faces along, or a cylinder or sphere wraps round.")] string axis = "z",
        [Description("For projection uv: the UV map's name (default: the current one).")] string? uv_map = null,
        [Description("The projection's centre [x, y, z] in metres (with size, instead of fit).")] double[]? center = null,
        [Description("The projection's size [x, y, z] in metres: the image spans this (with center, instead of fit).")] double[]? size = null,
        [Description("Size the projection to the surface's polygons. Default: true unless center or size is given.")] bool? fit = null,
        [Description("What the image does past its edges across: repeat (default), mirror, reset (stops) or edge (stretches the edge).")] string? width_tiling = null,
        [Description("The same, up and down.")] string? height_tiling = null,
        [Description("Cylindrical and spherical: how many times the image wraps round.")] double? width_wrap = null,
        [Description("Spherical: how many times the image wraps pole to pole.")] double? height_wrap = null,
        [Description("0 to 1 (default 1).")] double? opacity = null,
        [Description("normal (default), additive or multiply.")] string? blend = null,
        [Description("Bump only: the bump's strength (default 1).")] double? amplitude = null,
        CancellationToken cancel = default) =>
        Do($"textured {surface}", c =>
        {
            if (string.IsNullOrWhiteSpace(surface)) throw new ArgumentException("A surface name is needed.");
            if (string.IsNullOrWhiteSpace(image)) throw new ArgumentException("An image path is needed.");
            var ch = Json.Choice<TextureChannel>(channel, "channel");
            var proj = Json.Choice<TextureProjection>(projection, "projection");
            var ax = Json.Choice<Axis>(axis, "axis");
            var cen = center == null ? (Vec3?)null : Json.Vector(center, "center");
            var siz = size == null ? (Vec3?)null : Json.Vector(size, "size");
            bool autoFit = fit ?? (cen == null && siz == null);
            var wt = width_tiling == null ? (TextureTiling?)null : Json.Choice<TextureTiling>(width_tiling, "width_tiling");
            var ht = height_tiling == null ? (TextureTiling?)null : Json.Choice<TextureTiling>(height_tiling, "height_tiling");
            var bl = blend == null ? (TextureBlend?)null : Json.Choice<TextureBlend>(blend, "blend");
            Json.Fraction(opacity, "opacity");
            if (proj == TextureProjection.UV && uv_map == null && c.Document.ActiveUVMap == null)
                throw new ArgumentException("Projection uv needs a UV map: make one with make_uvs first.");
            string full = Path.GetFullPath(Path.Combine(host.Root, image));
            var before = Census.Of(c.Document);
            var layer = c.SetTexture(surface, full, ch, t =>
            {
                t.Projection = proj;
                t.Axis = ax;
                if (uv_map != null) t.UVMap = uv_map;
                if (cen is { } cc) t.Center = cc;
                if (siz is { } ss) t.Size = ss;
                if (wt is { } w1) t.WidthTiling = w1;
                if (ht is { } h1) t.HeightTiling = h1;
                if (width_wrap is { } ww) t.WidthWrap = ww;
                if (height_wrap is { } hw) t.HeightWrap = hw;
                if (opacity is { } op) t.Opacity = op;
                if (bl is { } b1) t.Blend = b1;
                if (amplitude is { } am) t.Amplitude = am;
                if (autoFit)
                    (t.Center, t.Size) = TextureMapping.AutoSize(TextureMapping.SurfaceBounds(c.Document.Layers.Select(l => l.Mesh), c.Document.SurfaceIndex(surface)));
            });
            var result = Census.Changes(c.Document, before, "Texture");
            result["texture"] = Census.Texture(layer);
            if (!File.Exists(full)) result["warning"] = $"There's no image at {full} yet: the views show a magenta checker until there is.";
            return result;
        }, cancel);

    [McpServerTool(Name = "remove_textures", Destructive = true)]
    [Description("Removes a surface's texture layers: those of one channel, or all of them. One undo step.")]
    public Task<string> RemoveTextures(
        [Description("The surface's name.")] string surface,
        [Description("color, diffuse, specular, luminosity, reflection, transparency or bump; leave out for all.")] string? channel = null,
        CancellationToken cancel = default) =>
        Do($"removed textures from {surface}", c =>
        {
            int gone = c.ClearTextures(surface, channel == null ? null : Json.Choice<TextureChannel>(channel, "channel"));
            return new JsonObject { ["removed"] = gone, ["undo"] = Census.UndoState(c.Document) };
        }, cancel);

    [McpServerTool(Name = "make_uvs", Destructive = true)]
    [Description("Lays the selected polygons (all of the foreground's if none are selected) out in a UV map, made if new, by a projection: planar (flat along axis), cylindrical or spherical (round axis), cubic (each face along the axis it faces most, sharing one square) or atlas (like cubic, each side its own cell). With fit (the default) the layout fills the 0-1 square. Makes the map the current one. One undo step; the Make UVs tool with run_tool has every setting.")]
    public Task<string> MakeUVs(
        [Description("The UV map's name, e.g. \"UV\".")] string map,
        [Description("planar (default), cylindrical, spherical, cubic or atlas.")] string projection = "planar",
        [Description("x, y or z (default z).")] string axis = "z",
        [Description("Fill the 0-1 square (default true); false places it by center and size.")] bool fit = true,
        [Description("With fit false: the projection's centre [x, y, z] in metres.")] double[]? center = null,
        [Description("With fit false: the projection's size [x, y, z] in metres.")] double[]? size = null,
        [Description("Cylindrical and spherical: how many times the layout goes round (default 1).")] double? width_wrap = null,
        CancellationToken cancel = default) =>
        Do($"made UVs in {map}", c =>
        {
            var args = new Dictionary<string, object?>
            {
                ["MapName"] = map,
                ["Projection"] = Json.Choice<UVProjection>(projection, "projection").ToString(),
                ["Axis"] = Json.Choice<Axis>(axis, "axis").ToString(),
                ["Fit"] = fit,
            };
            if (center != null) args["Center"] = Json.Vector(center, "center");
            if (size != null) args["Size"] = Json.Vector(size, "size");
            if (width_wrap is { } w) args["WidthWrap"] = w;
            var before = Census.Of(c.Document);
            c.Run("Make UVs", args);
            var result = Census.Changes(c.Document, before, "Make UVs");
            result["uv_maps"] = new JsonArray(c.Document.UVMapNames().Select(n => (JsonNode)n).ToArray());
            result["current_uv_map"] = c.Document.ActiveUVMap;
            return result;
        }, cancel);

    [McpServerTool(Name = "set_uv_map", Idempotent = true)]
    [Description("Chooses the UV map to work on (the app's UV view shows it, and set_texture's uv projection uses it by default). Not an undo step.")]
    public Task<string> SetUVMap(
        [Description("The UV map's name, as get_document lists them.")] string name,
        CancellationToken cancel) =>
        Do($"chose the {name} UV map", c =>
        {
            var names = c.UVMapNames;
            if (!names.Contains(name))
                throw new ArgumentException(names.Count == 0 ? "There are no UV maps yet: make one with make_uvs." : $"There's no UV map \"{name}\". There are: {string.Join(", ", names)}.");
            c.SetUVMap(name);
            return new JsonObject { ["current_uv_map"] = name, ["uv_maps"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()) };
        }, cancel);

    [McpServerTool(Name = "run_lua", Destructive = true)]
    [Description("Runs a Lua macro, as Macros › Run Macro does, as one undo step: if it fails, everything it did is put back. Macros number layers, points and polygons from 1 (unlike these tools). Functions: tool(name, {Param = value}), addpoint(x, y, z), addpolygon({p1, p2, ...}, surface), pointcount(), polycount(), getpoint(n), polypoints(n), selmode(\"points\"|\"polygons\"|\"edges\"|\"volume\"), selpoints({...}), selpolygons({...}), selbox(x0, y0, z0, x1, y1, z1), selectall(), deselect(), invert(), setlayers({fg}, {bg}), makeuvs(map, projection, axis), uvmap(name), uvmaps(), setuv(n, u, v), getuv(n), texture(surface, image, {options}), notexture(surface, channel), new(), load(path), save(path) (paths inside the working folder; save won't replace a file it didn't write), print(...). undo() and redo() can't be used here. Returns what it printed.")]
    public Task<string> RunLua(
        [Description("The Lua source.")] string script,
        [Description("Stop the macro after this many seconds (default 30, at most 300).")] double time_limit_seconds = 30,
        CancellationToken cancel = default) =>
        Do("ran a macro", c =>
        {
            if (time_limit_seconds <= 0 || time_limit_seconds > MaxLuaSeconds || !double.IsFinite(time_limit_seconds))
                throw new ArgumentException($"time_limit_seconds must be more than 0 and at most {MaxLuaSeconds}.");
            var before = Census.Of(c.Document);
            c.Output.Clear();
            try
            {
                c.Group("Macro", () => { Macros.Run(script, c, TimeSpan.FromSeconds(time_limit_seconds)); return 0; });
            }
            catch (MacroException e)
            {
                var printed = c.Output.Count > 0 ? "\nIt printed:\n" + string.Join("\n", c.Output) : "";
                throw new McpException($"The macro stopped: {e.Message}{printed}\nNothing it did was kept.", e);
            }
            var result = Census.Changes(c.Document, before, "Macro");
            result["output"] = new JsonArray(c.Output.Select(l => (JsonNode)l).ToArray());
            return result;
        }, cancel);

    // ───────────────────────────── seeing ─────────────────────────────

    [McpServerTool(Name = "get_document", ReadOnly = true, Idempotent = true)]
    [Description("Describes the object: its file, every layer (state foreground, background or off; point and polygon counts; bounds in metres), the visible bounds, the surfaces (colour, settings, textures, how many polygons use each), the UV maps, the selection and the undo history.")]
    public Task<string> GetDocument(CancellationToken cancel) =>
        Do("read the object", c => Census.Document(c.Document, brief: false), cancel);

    [McpServerTool(Name = "get_points", ReadOnly = true, Idempotent = true)]
    [Description("Lists a layer's points as rows [index, x, y, z] (metres), a page at a time. With selected_only, only the points the selection makes active (selected points, or the points of selected polygons, edges or volume).")]
    public Task<string> GetPoints(
        [Description("The layer, from 0. Default: the current layer.")] int? layer = null,
        [Description("The first index to list (default 0).")] int offset = 0,
        [Description("How many to list, 1 to 5000 (default 500).")] int limit = 500,
        [Description("Only the selected ones.")] bool selected_only = false,
        CancellationToken cancel = default) =>
        Do("read points", c =>
        {
            var (l, mesh) = Census.Layer(c.Document, layer);
            Census.CheckPage(offset, limit);
            IEnumerable<int> which = selected_only ? Census.Active(c.Document, l, points: true) : Enumerable.Range(0, mesh.Points.Count);
            var all = which.ToList();
            var rows = new JsonArray();
            foreach (int i in all.Skip(offset).Take(limit))
            {
                var p = mesh.Points[i];
                rows.Add(new JsonArray(i, Census.Round(p.X), Census.Round(p.Y), Census.Round(p.Z)));
            }
            return new JsonObject { ["layer"] = l, ["total"] = all.Count, ["offset"] = offset, ["count"] = rows.Count, ["points"] = rows, ["more"] = offset + rows.Count < all.Count };
        }, cancel);

    [McpServerTool(Name = "get_polygons", ReadOnly = true, Idempotent = true)]
    [Description("Lists a layer's polygons, a page at a time: each with its index, its point indices (in winding order: clockwise seen from the side it faces), its surface and its normal (unit vector out of its visible side; [0, 0, 0] for dots and lines). With selected_only, only the polygons the selection makes active.")]
    public Task<string> GetPolygons(
        [Description("The layer, from 0. Default: the current layer.")] int? layer = null,
        [Description("The first index to list (default 0).")] int offset = 0,
        [Description("How many to list, 1 to 5000 (default 500).")] int limit = 500,
        [Description("Only the selected ones.")] bool selected_only = false,
        CancellationToken cancel = default) =>
        Do("read polygons", c =>
        {
            var doc = c.Document;
            var (l, mesh) = Census.Layer(doc, layer);
            Census.CheckPage(offset, limit);
            IEnumerable<int> which = selected_only ? Census.Active(doc, l, points: false) : Enumerable.Range(0, mesh.Polygons.Count);
            var all = which.ToList();
            var rows = new JsonArray();
            foreach (int i in all.Skip(offset).Take(limit))
            {
                var poly = mesh.Polygons[i];
                var n = mesh.Normal(poly);
                rows.Add(new JsonObject
                {
                    ["index"] = i,
                    ["points"] = new JsonArray(poly.Points.Select(p => (JsonNode)p).ToArray()),
                    ["surface"] = poly.Surface < doc.Surfaces.Count ? doc.Surfaces[poly.Surface].Name : $"#{poly.Surface}",
                    ["normal"] = new JsonArray(Census.Round(n.X, 4), Census.Round(n.Y, 4), Census.Round(n.Z, 4)),
                });
            }
            return new JsonObject { ["layer"] = l, ["total"] = all.Count, ["offset"] = offset, ["count"] = rows.Count, ["polygons"] = rows, ["more"] = offset + rows.Count < all.Count };
        }, cancel);

    [McpServerTool(Name = "render_view", ReadOnly = true, Idempotent = true)]
    [Description("Draws one of the Modeler's views of the object as a PNG, framed to fit the visible layers, as the app draws it: grey grid, foreground in black wire, background layers dimmed, selection in yellow, faces seen from behind tinted blue. top looks down -Y (X right, Z up the picture); front looks along +Z (X right, Y up); side looks along -X (Z right, Y up); perspective orbits the object. Only perspective shades: mode wireframe, shaded (lit faces with wire) or textured (also image textures). Use it to check your work.")]
    public async Task<CallToolResult> RenderView(
        [Description("perspective (default), top, front or side.")] string view = "perspective",
        [Description("Width in pixels, 16 to 2048 (default 640).")] int width = 640,
        [Description("Height in pixels, 16 to 2048 (default 480).")] int height = 480,
        [Description("wireframe, shaded (default) or textured.")] string mode = "shaded",
        [Description("Perspective only: degrees round the Y axis; 0 looks along +Z like the front view, 90 along +X (default 35).")] double heading = 35,
        [Description("Perspective only: degrees looking down (default 25; negative looks up).")] double pitch = 25,
        CancellationToken cancel = default)
    {
        var kind = Json.Choice<ViewKind>(view, "view", ViewKind.UV);
        var style = Json.Choice<ViewMode>(mode, "mode");
        Json.Size(width, height, 2048);
        var copy = await Do("looked at the object", c => c.Document.Copy(), cancel).ConfigureAwait(false);
        var png = await Task.Run(() => Pictures.View(copy, kind, width, height, style, heading, pitch), cancel).ConfigureAwait(false);
        var b = Pictures.VisibleBounds(copy);
        string about = $"{kind} view, {width}×{height}, {(kind == ViewKind.Perspective ? $"{style.ToString().ToLowerInvariant()}, heading {heading:0.#}°, pitch {pitch:0.#}°, " : "wireframe, ")}framing {Census.Box(b)}.";
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = about }] };
    }

    [McpServerTool(Name = "render", ReadOnly = true, Idempotent = true)]
    [Description("Ray-traces a still of the object (its visible layers) as a PNG, as Layout renders: every surface setting and texture, shadows from a key light over the camera's left shoulder, a fill light and a gradient backdrop. The camera frames the object from heading and pitch, as render_view's perspective does. Slower than render_view: start small.")]
    public async Task<CallToolResult> Render(
        [Description("Width in pixels, 16 to 1920 (default 640).")] int width = 640,
        [Description("Height in pixels, 16 to 1920 (default 480).")] int height = 480,
        [Description("Rays per pixel for antialiasing, 1 to 16 (default 4).")] int samples = 4,
        [Description("Degrees round the Y axis; 0 looks along +Z (default 35).")] double heading = 35,
        [Description("Degrees looking down (default 25).")] double pitch = 25,
        CancellationToken cancel = default)
    {
        Json.Size(width, height, 1920);
        if (samples < 1 || samples > 16) throw new McpException("samples must be from 1 to 16.");
        var copy = await Do("rendered the object", c => c.Document.Copy(), cancel).ConfigureAwait(false);
        var png = await Task.Run(() => Pictures.RayTrace(copy, width, height, samples, heading, pitch, cancel), cancel).ConfigureAwait(false);
        return new CallToolResult
        {
            Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = $"Ray-traced, {width}×{height}, {samples} samples a pixel, heading {heading:0.#}°, pitch {pitch:0.#}°." }],
        };
    }

    // ───────────────────────────── plumbing ─────────────────────────────

    /// <summary>The working folder's file rules, made once (it remembers which files it wrote).</summary>
    private Workspace Files(Commands c) => _workspace ??= new Workspace(host.Root, c.Registry);

    /// <summary>
    /// Runs a handler's work on the host, with file access confined to the working folder, and turns any failure into
    /// an MCP tool error with its message (the document is left as it was: the command API undoes a failed step).
    /// </summary>
    private async Task<T> Do<T>(string action, Func<Commands, T> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, c =>
            {
                using (Files(c).Confine(c)) return work(c);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    private Task<string> Do(string action, Func<Commands, JsonObject> work, CancellationToken cancel) =>
        Do<string>(action, c => work(c).ToJsonString(), cancel);

    /// <summary>A failure's message for the agent: the message alone for the expected kinds, else with its type.</summary>
    internal static string Explain(Exception e) => e switch
    {
        ArgumentException or InvalidOperationException or FormatException or NotSupportedException or IOException
            or UnauthorizedAccessException or MacroException or ModelerBusyException or JsonException => e.Message,
        _ => $"Something went wrong ({e.GetType().Name}): {e.Message}",
    };
}
