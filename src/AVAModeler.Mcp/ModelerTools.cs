using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Rendering;
using AVAModeler.Slicing;
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
public sealed partial class ModelerTools(IModelerHost host)
{
    private Workspace? _workspace;

    /// <summary>The server these tools are served in (it decides, for one, whether there's a Modeler's object to stage).</summary>
    public ServerProfile Profile { get; init; } = ServerProfile.Modeler;

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
            result["layer"] = c.Document.CurrentLayer + 1;
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
            result["layer"] = c.Document.CurrentLayer + 1;
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

    [McpServerTool(Name = "select_edges", Idempotent = true)]
    [Description("Selects edges of the current layer, each given by its two point indices (an edge of some polygon there), and switches to Edges mode. Replaces the edge selection unless add is true. Edges are what Loop Cut, Chamfer, Bridge and Fill Hole work from, and what select_loop and select_ring start from. Not an undo step.")]
    public Task<string> SelectEdges(
        [Description("Edges as pairs of point indices in the current layer, from 0, e.g. [[0, 1], [1, 3]]. get_polygons shows which points polygons share.")] int[][] edges,
        [Description("Add to the edges already selected rather than replacing them.")] bool add = false,
        CancellationToken cancel = default) =>
        Do("selected edges", c =>
        {
            if (edges.Length == 0) throw new ArgumentException("Give at least one edge as [a, b].");
            for (int i = 0; i < edges.Length; i++)
                if (edges[i] is not { Length: 2 }) throw new ArgumentException($"Edge {i} needs two point indices [a, b].");
            Census.CheckIndices(edges.SelectMany(e => e).ToArray(), c.Document.Current.Mesh.Points.Count, "point");
            var pairs = edges.Select(e => (e[0], e[1])).ToList();
            var map = c.Document.Current.Mesh.EdgeMap();
            foreach (var (x, y) in pairs)
                if (!map.ContainsKey(Edge.Of(x, y)))
                    throw new ArgumentException($"Points {x} and {y} aren't joined by an edge of any polygon in the current layer: pick two points that follow each other round a polygon (get_polygons lists them).");
            c.SelectMode(SelectionMode.Edges);
            if (!add) c.SelectNone();
            c.SelectEdges(pairs);
            return Census.Selection(c.Document);
        }, cancel);

    [McpServerTool(Name = "select_loop", Idempotent = true)]
    [Description("Adds the edge loop through each selected edge (in Points mode, each edge between two selected points) and switches to Edges mode: the loop runs straight on across points where four quads meet (a row of a grid, a ring round a cylinder) and stops at triangles, n-gons and poles; an edge on a hole's rim selects the whole rim, ready for Bridge or Fill Hole. Select an edge first with select_edges. Not an undo step.")]
    public Task<string> SelectLoop(CancellationToken cancel) =>
        Do("selected loops", c => { RequireEdges(c, "select_loop"); c.SelectLoop(); return Census.Selection(c.Document); }, cancel);

    [McpServerTool(Name = "select_ring", Idempotent = true)]
    [Description("Adds the edge ring through each selected edge (in Points mode, each edge between two selected points) and switches to Edges mode: the parallel edges across the strip of quads it crosses (round a box or a cylinder it closes on itself), stopping at anything that isn't a quad. Loop Cut cuts across exactly this ring. Select an edge first with select_edges. Not an undo step.")]
    public Task<string> SelectRing(CancellationToken cancel) =>
        Do("selected rings", c => { RequireEdges(c, "select_ring"); c.SelectRing(); return Census.Selection(c.Document); }, cancel);

    private static void RequireEdges(Commands c, string tool)
    {
        var doc = c.Document;
        if (doc.Selection.Mode is SelectionMode.Edges or SelectionMode.Points && doc.Selection.Any
            && doc.ForegroundLayers.Any(l => doc.Selection.ActiveEdges(l, doc.Layers[l].Mesh).Count > 0)) return;
        throw new ArgumentException($"{tool} starts from a selected edge: select one with select_edges (or two neighbouring points with select_points) first.");
    }

    [McpServerTool(Name = "set_layers", Idempotent = true)]
    [Description("Chooses the foreground layers (being edited; new geometry goes into the lowest-numbered one) and the background layers (shown dimmed, and the other operand for Boolean, Drill, Path Extrude and Rail Extrude). The rest are turned off. Layers are numbered 1 to 10, as the app's layer buttons show them. Not an undo step.")]
    public Task<string> SetLayers(
        [Description("Foreground layer numbers, from 1, e.g. [1]. At least one.")] int[] foreground,
        [Description("Background layer numbers, from 1, e.g. [2]. Optional.")] int[]? background = null,
        CancellationToken cancel = default) =>
        Do("set the layers", c =>
        {
            if (foreground.Length == 0) throw new ArgumentException("Give at least one foreground layer.");
            var bg = background ?? [];
            foreach (int l in foreground.Concat(bg))
                if (l < 1 || l > 100) throw new ArgumentException($"There's no layer {l}: layers are numbered from 1 (and at most 100).");
            if (foreground.Intersect(bg).FirstOrDefault(-1) is var both and >= 1) throw new ArgumentException($"Layer {both} can't be in both the foreground and the background.");
            c.SetLayers(foreground.Select(l => l - 1), bg.Select(l => l - 1));
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
    [Description("Starts a new, empty object (ten empty layers, layer 1 in the foreground, one surface \"Default\"), clearing the undo history. Refuses if the object has unsaved changes, unless discard_changes is true.")]
    public Task<string> New(
        [Description("Throw away unsaved changes.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        Do("started a new object", c =>
        {
            using (Files(c).Confine(c, allowDiscard: discard_changes)) c.New();
            return Census.Document(c.Document, brief: true);
        }, cancel);

    [McpServerTool(Name = "open", Destructive = true)]
    [Description("Opens an object file in place of the current object: LightWave .lwo (LWOB, LWLO and LWO2), Wavefront .obj, VideoScape .geo, glTF 2.0 .glb or .gltf (meshes, each node with a mesh becoming a layer, base colours and base colour textures, TEXCOORD_0 as the UV map \"UV\"; converted to LightWave's left-handed space) or STL .stl (binary or ASCII, into layer 1, duplicate points merged; STL's Z up becomes +Y and its millimetres metres), plus any plugin format. The path is relative to the working folder and must stay inside it. Refuses if the current object has unsaved changes, unless discard_changes is true.")]
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
    [Description("Saves the object, the format chosen by the extension: .lwo (LightWave LWOB/LWLO, the native format: keeps every layer, surface, texture and UV map), .obj (Wavefront, with a .mtl beside it), .geo (VideoScape), .glb (glTF 2.0 binary, one file with its images inside) or .gltf (glTF 2.0 JSON, its data inside it as base64 unless separate_bin), or .stl (for 3D printing: triangles only, binary unless ascii, Z up: LightWave's +Y becomes STL's +Z; in millimetres, as slicers read STL: metres × 1000), plus any plugin format. glTF and STL are exports: faces are triangulated, points and lines left out, and the space converted (glTF is right-handed +Y up with Z flipped); glTF keeps each layer as a node, each surface as a PBR material (colour × diffuse as base colour, glossiness as roughness, luminosity as emission, transparency as alpha), smooth or flat normals by the surface's smoothing, and the colour image with its UVs. The path is relative to the working folder and must stay inside it; folders are made as needed. An existing file is only replaced when overwrite is true.")]
    public Task<string> Save(
        [Description("The file, relative to the working folder, e.g. \"table.lwo\", \"table.glb\" or \"table.stl\".")] string path,
        [Description("Replace the file (and the .mtl or .bin beside it) if it already exists.")] bool overwrite = false,
        [Description(".stl only: write ASCII text rather than binary.")] bool ascii = false,
        [Description(".gltf only: write the geometry and images to a .bin of the same name beside it, rather than inside the .gltf.")] bool separate_bin = false,
        CancellationToken cancel = default) =>
        Do($"saved {Path.GetFileName(path)}", c =>
        {
            var options = new SaveOptions { AsciiStl = ascii, SeparateGltfBuffer = separate_bin };
            string full = Files(c).Save(c.Document, path, overwrite, options);
            var result = new JsonObject { ["saved"] = full, ["bytes"] = new FileInfo(full).Length };
            if (ObjectFiles.Targets(full, options).Skip(1).ToArray() is { Length: > 0 } beside)
                result["also"] = new JsonArray([.. beside.Select(f => (JsonNode)f)]);
            return result;
        }, cancel);

    [McpServerTool(Name = "set_surface", Destructive = true)]
    [Description("Sets how a surface looks, making it if new, and (unless assign is false) gives it to the selected polygons in the foreground layers — to every polygon there if none are selected. One undo step. Leave out what you don't want to change. Colour is [r, g, b], 0 to 255; diffuse, specular, luminosity, reflection and transparency are fractions from 0 to 1.")]
    public Task<string> SetSurface(
        [Description("The surface's name, e.g. \"Wood\".")] string name,
        [Description("Colour [r, g, b], whole numbers from 0 to 255.")] double[]? color = null,
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
                rgb = Json.Colour(color, "color");
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

    /// <summary>The image files set_backdrop reads.</summary>
    internal static readonly string[] BackdropImageTypes = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    [McpServerTool(Name = "set_backdrop", Idempotent = true)]
    [Description("Puts a reference image (a blueprint, sketch or photo) behind one of the flat views, as LightWave Modeler's Display Options › Backdrop did, to model over it; or changes or clears one. The image lies in world space on the view's plane, so it lines up with the model and across views: center is the image's centre in metres on the view's two axes (top: [x, z]; front: [x, y]; side: [z, y]), width its width in metres, and its height follows the image's shape unless height is given. fit sizes and centres it round the foreground layers; pixel_size (metres one pixel covers) sizes it from its pixels. It is drawn under the grid and the wireframe, laid over the grey at opacity (default 0.5), adjusted by invert, contrast (about mid grey) and brightness. render_view's top, front and side views show it (framed to include it), so you can see the person's reference and check your model against it. It's a display setting: not saved in the object, not an undo step (the app remembers it per object). Leave image out to change the view's current backdrop's settings. The image is only read, and must be inside the working folder (PNG, JPEG, BMP, GIF or WebP); get_document lists the backdrops.")]
    public Task<string> SetBackdrop(
        [Description("top, front or side.")] string view,
        [Description("The image file, relative to the working folder, e.g. \"blueprints/side.png\". Leave out to change the current backdrop's settings.")] string? image = null,
        [Description("The image's centre in metres on the view's two axes: top [x, z], front [x, y], side [z, y]. Default [0, 0] for a new backdrop.")] double[]? center = null,
        [Description("Its width in metres (default 1 for a new backdrop).")] double? width = null,
        [Description("Its height in metres, to stretch it; 0 goes back to the image's own shape. Default: from the image's shape.")] double? height = null,
        [Description("Centre and size it round the foreground layers (everything shown if they're empty), keeping its shape.")] bool fit = false,
        [Description("Size it from its pixels: the metres one pixel covers (e.g. 0.005 for 200 pixels a metre).")] double? pixel_size = null,
        [Description("-1 to 1, added (default 0).")] double? brightness = null,
        [Description("0 to 4, about mid grey (default 1).")] double? contrast = null,
        [Description("0 to 1: how much it covers the view's grey (default 0.5, so the wireframe stays readable).")] double? opacity = null,
        [Description("Show it as a negative (for white-on-blue blueprints).")] bool? invert = null,
        [Description("Show or hide it, keeping its settings.")] bool? visible = null,
        [Description("Take the view's backdrop away (the other settings are ignored).")] bool clear = false,
        CancellationToken cancel = default) =>
        Do($"set the {view} backdrop", c =>
        {
            var v = Json.Choice<BackdropView>(view, "view");
            if (clear)
            {
                int gone = c.ClearBackdrop(v);
                var cleared = new JsonObject { ["cleared"] = gone > 0, ["view"] = v.ToString().ToLowerInvariant() };
                if (!c.Document.Backdrops.IsEmpty) cleared["backdrops"] = Census.Backdrops(c.Document);
                return cleared;
            }
            if (center is not null && (center.Length != 2 || center.Any(x => !double.IsFinite(x))))
                throw new ArgumentException($"center is two numbers in metres on the {v} view's axes: [{string.Join(", ", AxisPair(v))}].");
            Json.Fraction(opacity, "opacity");
            Backdrop Apply(Backdrop b)
            {
                if (center is { } cc) b = b with { Center = new Vec2(cc[0], cc[1]) };
                if (width is { } w) b = b with { Width = w };
                if (height is { } h) b = b with { Height = h == 0 ? null : h };
                if (brightness is { } br) b = b with { Brightness = br };
                if (contrast is { } co) b = b with { Contrast = co };
                if (opacity is { } op) b = b with { Opacity = op };
                if (invert is { } inv) b = b with { Invert = inv };
                if (visible is { } vis) b = b with { Visible = vis };
                if (pixel_size is { } px) b = b with { Width = c.BackdropWidthFromPixels(b, px), Height = null };
                if (fit)
                {
                    var (centre, size) = c.BackdropFit(v, b);
                    b = b with { Center = centre, Width = size };
                }
                return b.Checked();
            }
            string? problem = null;
            if (image is null)
            {
                if (c.Document.Backdrops[v] is null) throw new ArgumentException($"The {v.ToString().ToLowerInvariant()} view has no backdrop yet: give an image.");
                c.ChangeBackdrop(v, Apply);
            }
            else
            {
                string full = Files(c).Resolve(image);
                if (!BackdropImageTypes.Contains(Path.GetExtension(full).ToLowerInvariant()))
                    throw new NotSupportedException($"A backdrop is a picture: {string.Join(", ", BackdropImageTypes)}, not {Path.GetFileName(full)}.");
                if (!File.Exists(full)) throw new FileNotFoundException($"There's no image {image} in the working folder ({host.Root}).", full);
                // Check the settings on a spare backdrop first, so a bad one changes nothing.
                Apply(new Backdrop { Image = full });
                (_, problem) = c.SetBackdrop(v, full, Apply);
            }
            var result = new JsonObject { ["view"] = v.ToString().ToLowerInvariant(), ["backdrops"] = Census.Backdrops(c.Document) };
            if (problem != null) result["warning"] = problem;
            return result;
        }, cancel);

    private static string[] AxisPair(BackdropView v)
    {
        var (a, b) = BackdropMapping.AxisNames(v);
        return [a.ToLowerInvariant(), b.ToLowerInvariant()];
    }

    /// <summary>What set_procedural says about the kinds and their settings (a test checks it against <see cref="Procedurals.All"/>).</summary>
    internal const string ProceduralKinds =
        "checkerboard (alternating cubes one size across; no settings), " +
        "grid (lines one size apart, seen down axis; line_thickness 0.1, a fraction of the spacing), " +
        "dots (round dots one size apart, seen down axis; dot_diameter 0.5, fuzziness 0), " +
        "fractal_noise (cloudy patches over about half the surface; frequencies 3, contrast 1, small_power 0.5), " +
        "marble (thin veins layered along axis; frequencies 4, turbulence 0.5, vein_spacing 0.5, vein_sharpness 4), " +
        "wood (rings round axis; frequencies 3, turbulence 0.25, ring_spacing 0.1, ring_sharpness 2), " +
        "ripples (circular waves, best on bump; wave_sources 3, wavelength 0.3, wave_speed 0.05), " +
        "underwater (the bright web on a pool's floor; wave_sources 4, wavelength 0.4, wave_speed 0.05, band_sharpness 4), " +
        "crumple (creased folds, good on bump; frequencies 3, small_power 0.5), " +
        "turbulence (billowing noise with sharp creases; frequencies 3, contrast 1, small_power 0.5), " +
        "value (the colour or value everywhere: with falloff, a gradient from center; no settings), " +
        "bump_array (rows of round bumps seen down axis, good on bump; bump_radius 0.25, bump_spacing 0.5)";

    [McpServerTool(Name = "set_procedural", Destructive = true)]
    [Description("Lays a LightWave 5 procedural texture on a surface's channel: a pattern worked out in 3D from where each point is, so it needs no image file and no UV map. Sets the channel's first layer of that kind (or a new one; new_layer always adds one), making the surface if new. One undo step. On the color channel the texture color is laid over the surface's own colour by the pattern's amount (0 to 1 at each point); on diffuse, specular, luminosity, reflection or transparency the texture value is; on bump the amount is the height (amplitude sets the strength). The pattern is in the object's coordinates from center, one pattern unit per size (in metres along x, y, z: a check's width, the spacing of grid lines or dots; vein and ring spacings are fractions of it). Kinds, with their settings and defaults (settings are numbers by these names): " + ProceduralKinds + ". render shows every channel; render_view's textured mode and Layout's view show color ones.")]
    public Task<string> SetProcedural(
        [Description("The surface's name.")] string surface,
        [Description("The procedural: checkerboard, grid, dots, fractal_noise, marble, wood, ripples, underwater, crumple, turbulence, value or bump_array.")] string kind,
        [Description("color (default), diffuse, specular, luminosity, reflection, transparency or bump.")] string channel = "color",
        [Description("Color channel: the texture color [r, g, b], whole numbers 0 to 255 (a new layer starts black).")] double[]? color = null,
        [Description("Other channels but bump: the texture value, 0 to 1 (default 1), laid over the surface's own value.")] double? value = null,
        [Description("One pattern unit along [x, y, z], in metres (a new layer starts at [1, 1, 1]).")] double[]? size = null,
        [Description("The pattern's centre [x, y, z] in metres (a new layer starts at the origin).")] double[]? center = null,
        [Description("x, y or z: the axis grid, dots, bump_array and wood's rings are seen down, and marble's veins are layered along (a new layer starts at z).")] string? axis = null,
        [Description("The kind's settings by name, e.g. {\"frequencies\": 4, \"vein_spacing\": 0.2}; those left out keep their values (defaults for a new layer).")] Dictionary<string, JsonElement>? settings = null,
        [Description("0 to 1 (default 1): how much of the pattern shows.")] double? opacity = null,
        [Description("normal (default), additive or multiply.")] string? blend = null,
        [Description("Bump only: the bump's strength; 1 tilts the surface 45 degrees where the amount rises by 1 over one size. Ripples and other busy patterns want 0.02 to 0.2.")] double? amplitude = null,
        [Description("How fast the pattern fades away from center along [x, y, z], as a fraction per metre (0 doesn't fade).")] double[]? falloff = null,
        [Description("Turn the amount round (1 - amount).")] bool? negative = null,
        [Description("Work in world coordinates instead of the object's (the pattern stays put as the object moves in Layout).")] bool? world_coordinates = null,
        [Description("Always add a new layer, even if the channel has one of this kind.")] bool new_layer = false,
        CancellationToken cancel = default) =>
        Do($"set a {kind} procedural on {surface}", c =>
        {
            if (string.IsNullOrWhiteSpace(surface)) throw new ArgumentException("A surface name is needed.");
            var type = Procedurals.Find(kind) ?? throw new McpException($"kind must be one of {Procedurals.Keys}, not \"{kind}\".");
            var ch = Json.Choice<TextureChannel>(channel, "channel");
            Rgb? rgb = color == null ? null : Json.Colour(color, "color");
            var siz = size == null ? (Vec3?)null : Json.Vector(size, "size");
            if (siz is { } sz && (sz.X <= 0 || sz.Y <= 0 || sz.Z <= 0)) throw new McpException("size must be three positive numbers [x, y, z] in metres.");
            var cen = center == null ? (Vec3?)null : Json.Vector(center, "center");
            var fal = falloff == null ? (Vec3?)null : Json.Vector(falloff, "falloff");
            var ax = axis == null ? (Axis?)null : Json.Choice<Axis>(axis, "axis");
            var bl = blend == null ? (TextureBlend?)null : Json.Choice<TextureBlend>(blend, "blend");
            Json.Fraction(opacity, "opacity");
            Json.Fraction(value, "value");
            if (rgb != null && ch != TextureChannel.Color) throw new McpException("color is the texture colour of the color channel; for other channels give value.");
            if (value != null && ch is TextureChannel.Color or TextureChannel.Bump) throw new McpException("value is for the diffuse, specular, luminosity, reflection and transparency channels; on color give color, on bump amplitude.");
            // The settings are checked on a spare layer first, so a bad one changes nothing.
            var values = new List<(string Name, double Value)>();
            var probe = new TextureLayer { Channel = ch };
            Procedurals.Apply(probe, type);
            foreach (var (name, v) in settings ?? [])
            {
                if (v.ValueKind != JsonValueKind.Number) throw new McpException($"settings.{name} must be a number.");
                if (type.Find(name) is not { } p)
                    throw new McpException(type.Parameters.Count == 0 ? $"{type.Key} has no settings." : $"{type.Key} has no setting \"{name}\"; its settings are {string.Join(", ", type.Parameters.Select(x => x.Key))}.");
                Procedurals.Set(probe, p.Name, v.GetDouble());
                values.Add((p.Name, v.GetDouble()));
            }
            var before = Census.Of(c.Document);
            var layer = c.SetProcedural(surface, type, ch, new_layer, t =>
            {
                if (rgb is { } col) t.TextureColor = col;
                if (value is { } va) t.Value = va;
                if (siz is { } s1) t.Size = s1;
                if (cen is { } c1) t.Center = c1;
                if (ax is { } a1) t.Axis = a1;
                if (fal is { } f1) t.Falloff = f1;
                if (opacity is { } op) t.Opacity = op;
                if (bl is { } b1) t.Blend = b1;
                if (amplitude is { } am) t.Amplitude = am;
                if (negative is { } ne) t.Negative = ne;
                if (world_coordinates is { } wc) t.WorldCoordinates = wc;
                foreach (var (name, v) in values) Procedurals.Set(t, name, v);
            });
            var result = Census.Changes(c.Document, before, "Procedural texture");
            result["texture"] = Census.Texture(layer);
            return result;
        }, cancel);

    [McpServerTool(Name = "remove_textures", Destructive = true)]
    [Description("Removes a surface's texture layers, image and procedural: those of one channel, or all of them. One undo step.")]
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
    [Description("Runs a Lua macro, as Macros › Run Macro does, as one undo step: if it fails, everything it did is put back. Macros number points and polygons from 1 (these tools number them from 0); layers are 1 to 10 in both. Functions: tool(name, {Param = value}), addpoint(x, y, z), addpolygon({p1, p2, ...}, surface), pointcount(), polycount(), getpoint(n), polypoints(n), selmode(\"points\"|\"polygons\"|\"edges\"|\"volume\"), selpoints({...}), selpolygons({...}), selbox(x0, y0, z0, x1, y1, z1), selectall(), deselect(), invert(), setlayers({fg}, {bg}), makeuvs(map, projection, axis), uvmap(name), uvmaps(), setuv(n, u, v), getuv(n), texture(surface, image, {options}), proctex(surface, kind, {options}) (a procedural texture, as set_procedural), notexture(surface, channel), new(), load(path), save(path) (paths inside the working folder, the format by extension as open and save choose it; save won't replace a file it didn't write), print(...). undo() and redo() can't be used here. Returns what it printed.")]
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
        [Description("The layer, from 1. Default: the current layer.")] int? layer = null,
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
            return new JsonObject { ["layer"] = l + 1, ["total"] = all.Count, ["offset"] = offset, ["count"] = rows.Count, ["points"] = rows, ["more"] = offset + rows.Count < all.Count };
        }, cancel);

    [McpServerTool(Name = "get_polygons", ReadOnly = true, Idempotent = true)]
    [Description("Lists a layer's polygons, a page at a time: each with its index, its point indices (in winding order: clockwise seen from the side it faces), its surface and its normal (unit vector out of its visible side; [0, 0, 0] for dots and lines). With selected_only, only the polygons the selection makes active.")]
    public Task<string> GetPolygons(
        [Description("The layer, from 1. Default: the current layer.")] int? layer = null,
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
            return new JsonObject { ["layer"] = l + 1, ["total"] = all.Count, ["offset"] = offset, ["count"] = rows.Count, ["polygons"] = rows, ["more"] = offset + rows.Count < all.Count };
        }, cancel);

    [McpServerTool(Name = "check_mesh", ReadOnly = true, Idempotent = true)]
    [Description("Checks the object numerically, as the app's Mesh Info panel does, and changes nothing: is it a clean closed solid (watertight, consistently wound, facing outward) ready for 3D printing, booleans and rendering? Returns a verdict (\"Closed and printable (1 shell)\", \"Open: 2 holes along 8 edges\"...), printable and watertight flags, counts (points; polygons by type), bounds (min, max, size in metres), area_m2, and when closed volume_m3 and center_of_mass (metres, even density); then the problems: holes and boundary_edges (edges of one polygon), non_manifold_edges (3 or more polygons), shells (separate connected pieces), inconsistent_edges and flipped_polygons (faces wound against their neighbours, or closed shells inside out: what Align would turn), inward_shells, cavities (an inward shell inside another is a hollow, not a mistake), degenerate_polygons (no area, or a point twice), non_planar_polygons, duplicate_points, unused_points, points_and_lines (1- and 2-point polygons, not part of a solid). how_to_fix names the tool for each, and per_layer gives a few example indices (from 0). Check your model after booleans, drills, Make Polygon or hand-built faces, and before saving for 3D printing; to see a problem, run_tool \"Select Problems\" with its Problem, then render_view.")]
    public Task<string> CheckMesh(
        [Description("The layers to check, numbered from 1, each on its own. Default: the foreground layers (whole layers, not the selection).")] int[]? layers = null,
        [Description("Points closer than this, in metres, count as duplicates, and faces thinner than it as degenerate (default 0.000001).")] double tolerance = 1e-6,
        [Description("How many example indices of each problem to list per layer, 0 to 200 (default 20).")] int examples = 20,
        CancellationToken cancel = default) =>
        Do("checked the mesh", c =>
        {
            if (tolerance < 0 || !double.IsFinite(tolerance)) throw new ArgumentException("tolerance must be 0 or more, in metres.");
            if (examples is < 0 or > 200) throw new ArgumentException("examples must be from 0 to 200.");
            var doc = c.Document;
            List<int> which;
            if (layers is { Length: > 0 })
            {
                foreach (int l in layers)
                    if (l < 1 || l > doc.Layers.Count) throw new ArgumentException($"There's no layer {l}: layers are numbered 1 to {doc.Layers.Count}.");
                which = layers.Select(l => l - 1).ToList();
            }
            else which = doc.ForegroundLayers.ToList();
            var report = AVAModeler.Geometry.MeshCheck.Layers(doc, which, new AVAModeler.Geometry.MeshCheckOptions { PointTolerance = tolerance });
            return CheckReport.Json(report, examples);
        }, cancel);

    [McpServerTool(Name = "render_view", ReadOnly = true, Idempotent = true)]
    [Description("Draws one of the Modeler's views of the object as a PNG, framed to fit the visible layers, as the app draws it: grey grid, foreground in black wire, background layers dimmed, selection in yellow, faces seen from behind tinted blue. top looks down -Y (X right, Z up the picture); front looks along +Z (X right, Y up); side looks along -X (Z right, Y up); perspective orbits the object. Only perspective shades: mode wireframe, shaded (lit faces with wire) or textured (also colour textures, image and procedural). A flat view with a backdrop (set_backdrop, or one the person loaded) shows it under the grid, and the framing takes it in. Use it to check your work.")]
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
        var b = Pictures.Framing(copy, kind);
        string backdrop = BackdropDrawing.ViewOf(kind) is { } bv && copy.Backdrops[bv] is { Visible: true } bd ? $", over the backdrop {Path.GetFileName(bd.Image)}" : "";
        string about = $"{kind} view, {width}×{height}, {(kind == ViewKind.Perspective ? $"{style.ToString().ToLowerInvariant()}, heading {heading:0.#}°, pitch {pitch:0.#}°, " : "wireframe, ")}framing {Census.Box(b)}{backdrop}.";
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

    /// <summary>The extensions a G-code file may have.</summary>
    public static readonly string[] GCodeExtensions = [".gcode", ".gco", ".g", ".x3g"];

    [McpServerTool(Name = "slice_to_gcode")]
    [Description("Slices the object for 3D printing and saves the G-code, changing nothing in the object: the foreground layers' faces are put on the printer's bed (Y up becomes Z up; 1 m of the object is 1000 mm printed, times scale_percent), cut into layers, and printed as walls, solid top and bottom layers, sparse infill, an optional brim and supports, then written as Marlin-style G-code (G21, G90, M83 relative extrusion; heats and homes first; ;LAYER and ;TYPE comments for previewers). Returns the file, the print's size in mm, layers, an estimated time (each move speeding up and slowing down at the printer's acceleration, corners at its corner speed), filament in metres and grams, and warnings (it doesn't fit the bed, the mesh isn't printable, outlines left open by holes in the mesh). Run check_mesh first and fix what it finds: an open or inside-out mesh slices badly. Printers and materials are presets by name (the list is in the error if a name is wrong).")]
    public async Task<string> SliceToGCode(
        [Description("The G-code file, relative to the working folder, e.g. \"part.gcode\" (.gcode, .gco or .g), or \"part.x3g\" for a Sailfish printer (the Flashforge Creator Pro), which reads .x3g rather than G-code.")] string path,
        [Description("The printer preset by name, any case: a generic size (default \"Generic 220 × 220\": a 220 × 220 mm bed, 250 mm tall, 0.4 mm nozzle, 1.75 mm filament) or a printer on the market with its bed, height, filament, retraction, origin and start G-code, such as \"Prusa MK4S\", \"Prusa MINI+\", \"Creality Ender 3 / 3 Pro / 3 V2\", \"Bambu Lab P1S / P1P\", \"Voron 2.4 (300)\", \"Flashforge Creator Pro\" or \"Ultimaker S5\" (a wrong name lists them all). Its notes, such as converting for Sailfish firmware, come back as a warning.")] string? printer = null,
        [Description("The material preset: PLA (default), PETG, ABS or TPU.")] string? material = null,
        [Description("Layer height in mm, 0.04 to 0.8 × the nozzle (default 0.2).")] double layer_height = 0.2,
        [Description("Sparse infill, 0 to 100% (default 20).")] double infill_percent = 20,
        [Description("The infill's pattern: lines (default, turned 90° each layer), grid (both ways every layer) or gyroid (wavy curves that shift with height: about as strong every way).")] string infill_pattern = "lines",
        [Description("Wall loops round each outline and hole, 1 to 20 (default 2).")] int walls = 2,
        [Description("Build supports under overhangs steeper than 50° from vertical (default false), topped by two dense interface layers that come away cleanly.")] bool supports = false,
        [Description("With supports: only columns that stand on the bed, none on the object (default false).")] bool supports_bed_only = false,
        [Description("Lines of brim round the first layer to hold it down, 0 for none (default 0: a skirt instead).")] int brim_lines = 0,
        [Description("Scale in percent: 100 prints at the modelled size, 1 m = 1000 mm (default 100).")] double scale_percent = 100,
        [Description("Centre the object on the bed (default true); false puts the Modeler's origin at the bed's centre.")] bool centre = true,
        [Description("Which way up: as_modelled (default: the Modeler's +Y is up), largest_face (turned so its largest flat outside face lies on the bed), or selected_face (the first selected polygon in the foreground lies on the bed).")] string lay_flat = "as_modelled",
        [Description("Degrees to turn it about the bed's up axis, anticlockwise seen from above, after laying it flat (default 0).")] double rotate = 0,
        [Description("How many copies to print, 1 to 100, in rows centred on the bed (default 1).")] int copies = 1,
        [Description("The gap between copies in mm (default 5).")] double copy_gap = 5,
        [Description("Replace the file if it already exists.")] bool overwrite = false,
        CancellationToken cancel = default)
    {
        PrintSetup setup;
        try
        {
            var p = printer == null ? Presets.Printers[0] : Presets.Printers.FirstOrDefault(x => string.Equals(x.Name, printer, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"There's no printer preset \"{printer}\". Choose one of: {string.Join(", ", Presets.Printers.Select(x => x.Name))}.");
            var m = material == null ? Presets.Materials[0] : Presets.Materials.FirstOrDefault(x => string.Equals(x.Name, material, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"There's no material preset \"{material}\". Choose one of: {string.Join(", ", Presets.Materials.Select(x => x.Name))}.");
            setup = new PrintSetup
            {
                Printer = p,
                Material = m,
                Placement = new PlacementOptions
                {
                    Position = centre ? BedPosition.Centre : BedPosition.AsModelled, ScalePercent = scale_percent, RotateDegrees = rotate, Copies = copies, CopyGap = copy_gap,
                    LayFlat = lay_flat.ToLowerInvariant() switch
                    {
                        "as_modelled" => LayFlat.AsModelled,
                        "largest_face" => LayFlat.LargestFace,
                        "selected_face" => LayFlat.SelectedFace,
                        _ => throw new ArgumentException($"lay_flat is as_modelled, largest_face or selected_face, not \"{lay_flat}\"."),
                    },
                },
                Settings = new SliceSettings { LayerHeight = layer_height, InfillPercent = infill_percent,
                    Infill = Enum.TryParse<InfillPattern>(infill_pattern, ignoreCase: true, out var ip) ? ip : throw new ArgumentException($"infill_pattern is lines, grid or gyroid, not \"{infill_pattern}\"."), Walls = walls, Supports = supports, BrimLines = brim_lines, SupportOn = supports_bed_only ? SupportPlacement.BedOnly : SupportPlacement.Everywhere },
            }.Checked();
        }
        catch (ArgumentException e)
        {
            throw new McpException(e.Message, e);
        }
        var (copy, full) = await Do($"sliced the object into {Path.GetFileName(path)}", c =>
        {
            var files = Files(c);
            string f = files.Resolve(path);
            if (!GCodeExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                throw new ArgumentException($"A G-code file ends in {string.Join(", ", GCodeExtensions)}, such as \"{Path.GetFileNameWithoutExtension(f)}.gcode\".");
            if (Path.GetExtension(f).Equals(".x3g", StringComparison.OrdinalIgnoreCase) && setup.Printer.X3gMachine == null)
                throw new ArgumentException($"{setup.Printer.Name} doesn't read .x3g: save .gcode, or choose a Sailfish printer such as \"Flashforge Creator Pro\".");
            files.CheckWritable([f], overwrite);
            return (c.Document.Copy(), f);
        }, cancel).ConfigureAwait(false);
        try
        {
            var result = await Task.Run(() =>
            {
                var job = PrintJob.Run(copy, setup);
                if (job.Model.Triangles.Count == 0) throw new InvalidOperationException("There's nothing to print: the foreground layers have no faces.");
                cancel.ThrowIfCancellationRequested();
                string temp = full + ".tmp";
                try
                {
                    if (Path.GetExtension(full).Equals(".x3g", StringComparison.OrdinalIgnoreCase))
                        File.WriteAllBytes(temp, X3g.Convert(job.GCode, SailfishMachine.ById[setup.Printer.X3gMachine!], Path.GetFileNameWithoutExtension(full)));
                    else
                        File.WriteAllText(temp, job.GCode, new System.Text.UTF8Encoding(false));
                    File.Move(temp, full, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                return job;
            }, cancel).ConfigureAwait(false);
            _workspace!.Wrote(full);
            var size = result.Model.Bounds.Size;
            var st = result.Stats;
            return new JsonObject
            {
                ["saved"] = full,
                ["printer"] = setup.Printer.Name,
                ["material"] = setup.Material.Name,
                ["size_mm"] = new JsonArray(Math.Round(size.X, 2), Math.Round(size.Y, 2), Math.Round(size.Z, 2)),
                ["layers"] = st.Layers,
                ["time"] = st.Time,
                ["minutes"] = Math.Round(st.Seconds / 60, 1),
                ["filament_m"] = Math.Round(st.FilamentMm / 1000, 3),
                ["grams"] = Math.Round(st.Grams, 1),
                ["printable"] = result.Model.Printable,
                ["warnings"] = new JsonArray([.. result.Warnings.Concat(setup.Printer.Notes.Length > 0 ? [setup.Printer.Notes] : []).Select(w => (JsonNode)w)]),
            }.ToJsonString();
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
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
            or UnauthorizedAccessException or MacroException or ModelerBusyException or JsonException or InvalidDataException => e.Message,
        _ => $"Something went wrong ({e.GetType().Name}): {e.Message}",
    };
}
