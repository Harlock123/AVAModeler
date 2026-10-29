using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// Saved recipes, served as MCP prompts: short step-by-step plans for things agents often make, written with the real
/// tool names and arguments. Each step that calls a tool shows the call on a line of its own, "→ tool {arguments}", so
/// an agent can follow it as it is (and the tests run every recipe through the server, call by call, so a typo in one
/// fails the build). Prompt arguments come as text, as MCP clients send them; numbers and colours are read from it.
/// </summary>
[McpServerPromptType]
public static class Recipes
{
    /// <summary>The marker that starts a step's tool call in a recipe's text.</summary>
    public const string CallMarker = "→ ";

    // ───────────────────────────── modeling ─────────────────────────────

    [McpServerPrompt(Name = "lathed_vase", Title = "Lathed vase")]
    [Description("Models a vase by lathing a profile: the outline of its wall, drawn with add_points and add_polygons, swept round the Y axis.")]
    public static string LathedVase(
        [Description("Height in metres (default 0.4).")] string? height = null,
        [Description("Widest radius in metres (default 0.12).")] string? radius = null,
        [Description("Colour as r, g, b from 0 to 255, or a name such as blue (default [70, 110, 170]).")] string? color = null,
        [Description("Steps round the lathe, 8 to 96 (default 32).")] string? sides = null)
    {
        double h = Number(height, 0.4, "height", 0.02, 20), r = Number(radius, 0.12, "radius", 0.01, 10);
        int n = Whole(sides, 32, "sides", 8, 96);
        var c = Colour(color, [70, 110, 170]);
        // The wall's outline (x out from the axis, y up), outside from the foot to the lip, then inside back down to
        // the floor; the first and last points are on the axis, closing the bottom.
        (double X, double Y)[] outline =
        [
            (0, 0), (0.55, 0), (0.62, 0.04), (0.9, 0.2), (1, 0.38), (0.85, 0.6), (0.5, 0.8), (0.45, 0.9), (0.6, 1),
            (0.55, 1), (0.4, 0.9), (0.45, 0.8), (0.8, 0.6), (0.94, 0.38), (0.84, 0.2), (0.56, 0.06), (0, 0.06),
        ];
        var points = outline.Select(p => V(p.X * r, p.Y * h, 0)).ToArray();
        var recipe = new Recipe($"Model a vase {F(h)} m tall and {F(2 * r)} m across by lathing its profile round the Y axis, as LightWave modelers do.");
        recipe.Step("Start an empty object. If new refuses because the object has unsaved work, save it first (or work in an empty layer with set_layers and leave new out).", "new", new { });
        recipe.Step("Make the surface first, so the profile can take it.", "set_surface", new { name = "Vase", color = c, specular = 0.4, glossiness = 128, smoothing = true, assign = false });
        recipe.Step("Add the profile's points in the XY plane (x out from the axis, y up): up the outside from the foot to the lip, then down the inside to the floor. The first and last are on the axis, so the bottom closes.", "add_points", new { points });
        recipe.Step($"Join them into one closed outline on the Vase surface. add_points returned their indices; in an empty layer they are 0 to {points.Length - 1}.", "add_polygons", new { polygons = new[] { Enumerable.Range(0, points.Length).ToArray() }, surface = "Vase" });
        recipe.Step("Lathe the outline round the Y axis through the origin: a full turn makes the solid wall, merges the points on the axis and faces every polygon outward.", "run_tool", new { name = "Lathe", @params = new Dictionary<string, object> { ["Axis"] = "Y", ["Center"] = V(0, 0, 0), ["Sides"] = n } });
        recipe.Step("Look at it from the front to check the shape.", "render_view", new { view = "front", width = 320, height = 320 });
        recipe.Step("Render it.", "render", new { width = 320, height = 320, samples = 2 });
        return recipe.Text("Save it with save (e.g. path \"vase.lwo\") if you want to keep it.");
    }

    [McpServerPrompt(Name = "table", Title = "Table")]
    [Description("Models a table: four lathed legs (one lathed, then cloned to the other corners) under a box top.")]
    public static string Table(
        [Description("Width along X in metres (default 1.2).")] string? width = null,
        [Description("Depth along Z in metres (default 0.8).")] string? depth = null,
        [Description("Height in metres (default 0.75).")] string? height = null,
        [Description("Colour as r, g, b from 0 to 255, or a name (default wood brown [150, 100, 60]).")] string? color = null)
    {
        double w = Number(width, 1.2, "width", 0.3, 20), d = Number(depth, 0.8, "depth", 0.3, 20), h = Number(height, 0.75, "height", 0.2, 10);
        var c = Colour(color, [150, 100, 60]);
        const double top = 0.04, inset = 0.08;
        double lx = w / 2 - inset, lz = d / 2 - inset, legH = h - top;
        (double X, double Y)[] leg = [(0, 0), (0.025, 0), (0.035, 0.05), (0.022, 0.25), (0.03, 0.7), (0.035, 1), (0, 1)];
        var points = leg.Select(p => V(lx + p.X, p.Y * legH, lz)).ToArray();
        var recipe = new Recipe($"Model a table {F(w)} m wide (X), {F(d)} m deep (Z) and {F(h)} m tall, standing on y = 0, centred on the origin.");
        recipe.Step("Start an empty object (save the current one first if new refuses).", "new", new { });
        recipe.Step("Make the wood surface.", "set_surface", new { name = "Wood", color = c, specular = 0.2, glossiness = 64, smoothing = true, assign = false });
        recipe.Step($"Add one leg's profile at the corner x = {F(lx)}, z = {F(lz)}: x out from the leg's own axis, from the floor to the underside of the top. The first and last points are on that axis.", "add_points", new { points });
        recipe.Step($"Join them into a closed outline (indices 0 to {points.Length - 1} in an empty layer: use the ones add_points returned).", "add_polygons", new { polygons = new[] { Enumerable.Range(0, points.Length).ToArray() }, surface = "Wood" });
        recipe.Step("Lathe it round a vertical axis through the leg's corner. Lathe acts on everything in the layer while nothing is selected, and clears the selection.", "run_tool", new { name = "Lathe", @params = new Dictionary<string, object> { ["Axis"] = "Y", ["Center"] = V(lx, 0, lz), ["Sides"] = 16 } });
        recipe.Step("Clone the leg across to the other side in X (one copy, moved by the width between legs).", "run_tool", new { name = "Clone", @params = new Dictionary<string, object> { ["Copies"] = 1, ["Offset"] = V(-2 * lx, 0, 0) } });
        recipe.Step("Clone both legs across in Z, which makes four.", "run_tool", new { name = "Clone", @params = new Dictionary<string, object> { ["Copies"] = 1, ["Offset"] = V(0, 0, -2 * lz) } });
        recipe.Step("Add the top, last, so the clones didn't copy it.", "run_tool", new { name = "Box", @params = new Dictionary<string, object> { ["Center"] = V(0, h - top / 2, 0), ["Size"] = V(w, top, d), ["Surface"] = "Wood" } });
        recipe.Step("Check the legs sit under the corners from the top, then look at it in perspective.", "render_view", new { view = "top", width = 320, height = 240 });
        recipe.Step("Render it.", "render", new { width = 320, height = 240, samples = 2 });
        return recipe.Text("Save it with save (e.g. path \"table.lwo\") if you want to keep it.");
    }

    [McpServerPrompt(Name = "chess_pawn", Title = "Chess pawn")]
    [Description("Models a chess pawn by lathing one profile: base, collar, neck and round head.")]
    public static string ChessPawn(
        [Description("Height in metres (default 0.06, a real pawn's).")] string? height = null,
        [Description("Colour as r, g, b from 0 to 255, or a name (default ivory [235, 225, 200]).")] string? color = null)
    {
        double h = Number(height, 0.06, "height", 0.01, 10);
        var c = Colour(color, [235, 225, 200]);
        var outline = new List<(double X, double Y)>
        {
            (0, 0), (0.3, 0), (0.3, 0.05), (0.26, 0.08), (0.27, 0.11), (0.2, 0.14), (0.14, 0.2), (0.1, 0.45), (0.17, 0.5), (0.17, 0.53), (0.09, 0.56),
        };
        // The head: an arc from just above the neck round to the top, on the axis.
        foreach (double a in new[] { -60.0, -30, 0, 30, 60, 90 })
            outline.Add((Math.Round(0.16 * Math.Cos(a * Math.PI / 180), 4), Math.Round(0.72 + 0.16 * Math.Sin(a * Math.PI / 180), 4)));
        double scale = h / outline[^1].Y;
        var points = outline.Select(p => V(p.X * scale, p.Y * scale, 0)).ToArray();
        var recipe = new Recipe($"Model a chess pawn {F(h)} m tall, standing on y = 0, by lathing its profile round the Y axis.");
        recipe.Step("Start an empty object (save the current one first if new refuses).", "new", new { });
        recipe.Step("Make its surface: smooth and glossy, like turned and polished wood.", "set_surface", new { name = "Pawn", color = c, specular = 0.5, glossiness = 256, smoothing = true, assign = false });
        recipe.Step("Add the profile in the XY plane, from the axis at the bottom out round the base, collar and neck, and over the round head to the axis at the top.", "add_points", new { points });
        recipe.Step($"Join them into one closed outline (indices 0 to {points.Length - 1} in an empty layer).", "add_polygons", new { polygons = new[] { Enumerable.Range(0, points.Length).ToArray() }, surface = "Pawn" });
        recipe.Step("Lathe it a full turn round Y.", "run_tool", new { name = "Lathe", @params = new Dictionary<string, object> { ["Axis"] = "Y", ["Sides"] = 32 } });
        recipe.Step("Check the silhouette from the side.", "render_view", new { view = "side", width = 240, height = 320 });
        recipe.Step("Render it.", "render", new { width = 240, height = 320, samples = 2 });
        return recipe.Text("For the other side's pawns, run it again with color 30, 30, 30.");
    }

    // ───────────────────────────── scenes ─────────────────────────────

    [McpServerPrompt(Name = "three_point_lighting", Title = "Three-point lighting")]
    [Description("Lights a scene item the classic way: a key light in front and above to one side, a soft fill from the other side, and a rim light from behind.")]
    public static string ThreePointLighting(
        [Description("The scene item to light, by name (default \"Modeler object\", the name add_object gives the Modeler's object).")] string? target = null,
        [Description("How far the lights stand from it, metres (default 4; about twice the subject's size or more).")] string? distance = null,
        [Description("Width of the check render in pixels (default 320).")] string? width = null)
    {
        string t = Text(target, "Modeler object");
        double d = Number(distance, 4, "distance", 0.1, 1000);
        int w = Whole(width, 320, "width", 16, 1920);
        var recipe = new Recipe($"Light \"{t}\" with three lights {F(d)} m away: a key, a fill and a rim. It assumes the item is in the scene (add_object) and the camera looks at it from the front (-Z).");
        LightRig(recipe, t, d);
        recipe.Step("Check the lighting with a small render.", "render_scene", new { width = w, samples = 2 });
        return recipe.Text("Make the key brighter or the fill weaker (set_item intensity) for more contrast.");
    }

    private static void LightRig(Recipe recipe, string target, double d)
    {
        recipe.Step("Remove Layout's default light (\"Light\"), if the scene still has it, so it doesn't flatten the others; skip this if there's none.", "remove_item", new { item = "Light" });
        recipe.Step("The key light: a warm spot in front, above and to the left, casting the shadows.", "add_light", new { type = "spot", name = "Key", position = V(-0.8 * d, 0.9 * d, -0.8 * d), color = new[] { 255, 244, 229 }, intensity = 1.1, cone_angle = 35, edge_angle = 10 });
        recipe.Step("Aim it at the subject (without keys, so it holds).", "look_at", new { item = "Key", target, key = false });
        recipe.Step("The fill: a weaker, cooler distant light from the right, without shadows, to lift the key's shadows.", "add_light", new { type = "distant", name = "Fill", position = V(d, 0.3 * d, -0.6 * d), color = new[] { 200, 215, 255 }, intensity = 0.4, shadows = false });
        recipe.Step("Aim the fill.", "look_at", new { item = "Fill", target, key = false });
        recipe.Step("The rim: a distant light from behind and above, to edge the subject against the backdrop.", "add_light", new { type = "distant", name = "Rim", position = V(0.3 * d, 0.8 * d, d), intensity = 0.8, shadows = false });
        recipe.Step("Aim the rim.", "look_at", new { item = "Rim", target, key = false });
        recipe.Step("Lower the ambient light so the shadows stay shaped.", "set_scene", new { ambient_intensity = 0.08 });
    }

    [McpServerPrompt(Name = "turntable_render", Title = "Turntable render")]
    [Description("Stages the Modeler's object on a turntable: on a null spinning once round over the frames, with a camera, three-point lighting and a backdrop, then renders the run of frames (and a movie, with ffmpeg).")]
    public static string TurntableRender(
        [Description("Frames for one full turn, 2 to 120 (default 60: two seconds).")] string? frames = null,
        [Description("The camera's distance from the object, metres (default 4; about 2.5 times the object's size).")] string? distance = null,
        [Description("Width of the frames in pixels (default 320).")] string? width = null,
        [Description("The movie to make: mp4, gif, both or none (default mp4; needs ffmpeg).")] string? movie = null)
    {
        int n = Whole(frames, 60, "frames", 2, ModelerTools.MaxAnimationFrames);
        double d = Number(distance, 4, "distance", 0.1, 1000);
        int w = Whole(width, 320, "width", 16, ModelerTools.MaxAnimationSize);
        string m = Choice(movie, "mp4", "movie", "mp4", "gif", "both", "none");
        var recipe = new Recipe($"Stage the Modeler's object on a turntable that turns once in {n} frames, light it, and render the turn. Model the object first, around the origin; get_document's bounds tell you its size, and distance should be about 2.5 times it.");
        recipe.Step("Start a new scene (if it refuses, save the scene with save_scene, or give discard_changes true to throw it away).", "new_scene", new { });
        recipe.Step($"Frames 1 to {n} at 30 a second, and a dark gradient backdrop.", "set_scene", new { first_frame = 1, last_frame = n, fps = 30, gradient = true, gradient_top = new[] { 40, 48, 64 }, gradient_bottom = new[] { 8, 8, 12 }, frame = 1 });
        recipe.Step("The turntable: a null at the origin.", "add_null", new { name = "Turntable" });
        recipe.Step("Put the Modeler's object on it (live: later modeling shows at once).", "add_object", new { name = "Subject", parent = "Turntable" });
        recipe.Step("Key the turntable's heading at 0 on the first frame, linear so it turns steadily.", "set_key", new { item = "Turntable", channel = "heading", frame = 1, value = 0, linear = true });
        recipe.Step($"And at the last frame, a frame's turn short of 360°, so a looping movie doesn't show the same view twice: 360 × ({n} − 1) / {n}.", "set_key", new { item = "Turntable", channel = "heading", frame = n, value = R(360.0 * (n - 1) / n), linear = true });
        recipe.Step("Place the camera in front and a little above.", "set_transform", new { item = "Camera", frame = 1, position = V(0, 0.4 * d, -d), key = false });
        recipe.Step("Aim it at the object.", "look_at", new { item = "Camera", target = "Subject", frame = 1, key = false });
        LightRig(recipe, "Subject", d);
        recipe.Step("Check the framing through the camera.", "render_scene_view", new { view = "camera", width = 320 });
        recipe.Step("Check a quarter of the way round, ray-traced and small.", "render_scene", new { frame = 1 + n / 4, width = 160, samples = 1 });
        object animation = m == "none"
            ? new { folder = "turntable", name = "turntable", first = 1, last = n, width = w, samples = 1 }
            : new { folder = "turntable", name = "turntable", first = 1, last = n, width = w, samples = 1, movie = m };
        recipe.Step("Render every frame into the turntable folder" + (m == "none" ? "" : $", and join them into turntable/turntable.{(m == "both" ? "mp4 and turntable/turntable.gif" : m)}") + ". render_animation does at most 20 million rays (width × height × samples × frames) a call: render bigger in parts if you need to.", "render_animation", animation);
        return recipe.Text("Save the scene with save_scene (e.g. \"turntable.avascene\") to keep it.");
    }

    [McpServerPrompt(Name = "bouncing_ball", Title = "Bouncing ball")]
    [Description("Animates a ball bouncing on a floor, with squash and stretch through its scale keys, and renders it.")]
    public static string BouncingBall(
        [Description("How high it bounces, metres (default 2).")] string? height = null,
        [Description("Frames for one bounce, top to top, 8 to 120 (default 24).")] string? frames = null,
        [Description("The ball's colour as r, g, b from 0 to 255, or a name (default red [220, 40, 30]).")] string? color = null,
        [Description("Width of the frames in pixels (default 240).")] string? width = null)
    {
        double h = Number(height, 2, "height", 0.2, 100);
        int n = Whole(frames, 24, "frames", 8, ModelerTools.MaxAnimationFrames);
        int w = Whole(width, 240, "width", 16, ModelerTools.MaxAnimationSize);
        var c = Colour(color, [220, 40, 30]);
        const double r = 0.25;
        int hit = 1 + (n - 1) / 2;
        var recipe = new Recipe($"Animate a ball dropping from {F(h)} m, bouncing on the floor at frame {hit} and back up at frame {n}, squashing as it lands and stretching as it falls and rises.");
        recipe.Step("Start an empty object (save the current one first if new refuses).", "new", new { });
        recipe.Step("The ball's surface.", "set_surface", new { name = "Ball", color = c, specular = 0.5, glossiness = 128, smoothing = true, assign = false });
        recipe.Step("The floor's surface.", "set_surface", new { name = "Floor", color = new[] { 170, 170, 165 }, assign = false });
        recipe.Step("The ball in layer 1, resting on y = 0: its own origin is at its bottom, so scaling it squashes it onto the floor, not into it.", "run_tool", new { name = "Ball", @params = new Dictionary<string, object> { ["Center"] = V(0, r, 0), ["Radius"] = V(r, r, r), ["Sides"] = 24, ["Segments"] = 12, ["Surface"] = "Ball" } });
        recipe.Step("Work in layer 2 for the floor.", "set_layers", new { foreground = new[] { 2 } });
        recipe.Step("The floor: a thin slab whose top is y = 0.", "run_tool", new { name = "Box", @params = new Dictionary<string, object> { ["Center"] = V(0, -0.025, 1), ["Size"] = V(6, 0.05, 4), ["Surface"] = "Floor" } });
        recipe.Step("Back to layer 1.", "set_layers", new { foreground = new[] { 1 } });
        recipe.Step("Start a new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step($"Frames 1 to {n}.", "set_scene", new { first_frame = 1, last_frame = n, frame = 1 });
        recipe.Step("The ball, by its layer.", "add_object", new { layer = 1, name = "Ball" });
        recipe.Step("The floor.", "add_object", new { layer = 2, name = "Floor" });
        recipe.Step("Height at the top: tension 1 eases out of the top of the arc.", "set_key", new { item = "Ball", channel = "position.y", frame = 1, value = R(h), tension = 1 });
        recipe.Step("On the floor: continuity -1 makes a sharp corner, so it bounces rather than floats.", "set_key", new { item = "Ball", channel = "position.y", frame = hit, value = 0, continuity = -1 });
        recipe.Step("Back at the top.", "set_key", new { item = "Ball", channel = "position.y", frame = n, value = R(h), tension = 1 });
        recipe.Step("Squash and stretch, through scale keys: round at the top...", "set_transform", new { item = "Ball", frame = 1, scale = V(1, 1, 1), key = true });
        recipe.Step("...stretched tall and thin just before it lands, as it moves fastest...", "set_transform", new { item = "Ball", frame = hit - 2, scale = V(0.88, 1.2, 0.88), key = true });
        recipe.Step("...squashed flat and wide on the floor (keep the volume about the same: x × y × z near 1)...", "set_transform", new { item = "Ball", frame = hit, scale = V(1.25, 0.65, 1.25), key = true });
        recipe.Step("...stretched again as it leaves...", "set_transform", new { item = "Ball", frame = hit + 2, scale = V(0.88, 1.2, 0.88), key = true });
        recipe.Step("...and round at the top.", "set_transform", new { item = "Ball", frame = n, scale = V(1, 1, 1), key = true });
        recipe.Step("The camera: back and level with the middle of the bounce.", "set_transform", new { item = "Camera", frame = 1, position = V(0, 0.5 * h + 0.3, -2.2 * h - 1.5), key = false });
        recipe.Step("Aim it.", "look_at", new { item = "Camera", point = V(0, 0.45 * h, 0), frame = 1, key = false });
        recipe.Step("A sun from above to cast the ball's shadow on the floor.", "add_light", new { type = "distant", name = "Sun", position = V(-2, 6, -3), look_at = V(0, 0, 0), intensity = 1 });
        recipe.Step("Turn the default light down to a fill.", "set_item", new { item = "Light", intensity = 0.35, shadows = false });
        recipe.Step("Check the motion: the keys, then the frame it lands on.", "get_keys", new { item = "Ball" });
        recipe.Step("Render the landing frame.", "render_scene", new { frame = hit, width = 160, samples = 1 });
        recipe.Step("Render the bounce, every frame, and a looping GIF.", "render_animation", new { folder = "bounce", name = "bounce", first = 1, last = n, width = w, samples = 1, movie = "gif" });
        return recipe.Text("For bounces that die away, key lower tops later (or set_end_behavior post repeat for a ball that bounces for ever).");
    }

    [McpServerPrompt(Name = "carbon_atom", Title = "Carbon atom")]
    [Description("Builds the carbon atom of samples/mcp/carbon_atom.py: a nucleus of six protons and six neutrons, and six electrons on three crossed, spinning orbits, modeled, staged, lit and animated.")]
    public static string CarbonAtom(
        [Description("The electrons' colour as r, g, b from 0 to 255, or a name (default green [60, 255, 90]).")] string? color = null,
        [Description("Width of the check render in pixels (default 640).")] string? width = null)
    {
        var c = Colour(color, [60, 255, 90]);
        int w = Whole(width, 640, "width", 16, 1920);
        const int frames = 120;
        var recipe = new Recipe("Build a stylized carbon atom, the approach of samples/mcp/carbon_atom.py: the nucleus in layer 1 and each orbit (a thin lathed ring with two electrons) in layers 2 to 4, so Layout can put each layer in the scene as its own object and spin it. Everything repeats every 120 frames, so the animation loops.");
        recipe.Step("Start an empty object (save the current one first if new refuses).", "new", new { });
        recipe.Step("Surfaces: protons...", "set_surface", new { name = "Proton", color = new[] { 245, 245, 240 }, specular = 0.5, glossiness = 128, smoothing = true, assign = false });
        recipe.Step("...neutrons...", "set_surface", new { name = "Neutron", color = new[] { 120, 122, 128 }, specular = 0.3, glossiness = 64, smoothing = true, assign = false });
        recipe.Step("...electrons, glowing...", "set_surface", new { name = "Electron", color = c, luminosity = 0.7, specular = 0.6, glossiness = 256, smoothing = true, assign = false });
        recipe.Step("...and the orbit rings.", "set_surface", new { name = "Orbit", color = new[] { 150, 200, 255 }, luminosity = 0.5, smoothing = true, double_sided = true, assign = false });
        recipe.Step("The nucleus goes in layer 1.", "set_layers", new { foreground = new[] { 1 } });
        // Twelve balls on an icosahedron's corners pack evenly; opposite corners get opposite kinds.
        double phi = (1 + Math.Sqrt(5)) / 2, spread = 0.513 / Math.Sqrt(1 + phi * phi);
        var corners = new List<double[]>();
        foreach (var (a, b) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) }) corners.Add([0, a, b * phi]);
        foreach (var (a, b) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) }) corners.Add([a, b * phi, 0]);
        foreach (var (a, b) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) }) corners.Add([b * phi, 0, a]);
        int[] protons = [0, 3, 5, 6, 8, 11];
        for (int i = 0; i < corners.Count; i++)
            recipe.Step(i == 0 ? "Twelve balls on the corners of an icosahedron, so they pack evenly; opposite corners get opposite kinds." : null, "run_tool",
                new { name = "Ball", @params = new Dictionary<string, object> { ["Center"] = V(corners[i][0] * spread, corners[i][1] * spread, corners[i][2] * spread), ["Radius"] = V(0.21, 0.21, 0.21), ["Sides"] = 32, ["Segments"] = 16, ["Surface"] = protons.Contains(i) ? "Proton" : "Neutron" } });
        const double radius = 1.6;
        var circle = Enumerable.Range(0, 8).Select(a => V(radius + 0.018 * Math.Cos(a * Math.PI / 4), 0.018 * Math.Sin(a * Math.PI / 4), 0)).ToArray();
        foreach (int layer in new[] { 2, 3, 4 })
        {
            recipe.Step($"Orbit {layer - 1} goes in layer {layer}.", "set_layers", new { foreground = new[] { layer } });
            recipe.Step("A small circle (the ring's cross-section) at the orbit's radius...", "add_points", new { points = circle });
            recipe.Step("...joined into a face (indices 0 to 7 in the empty layer)...", "add_polygons", new { polygons = new[] { Enumerable.Range(0, 8).ToArray() }, surface = "Orbit" });
            recipe.Step("...lathed round Y into a thin ring.", "run_tool", new { name = "Lathe", @params = new Dictionary<string, object> { ["Axis"] = "Y", ["Sides"] = 96 } });
            foreach (double x in new[] { radius, -radius })
                recipe.Step(x > 0 ? "An electron on each side of the ring." : null, "run_tool", new { name = "Ball", @params = new Dictionary<string, object> { ["Center"] = V(x, 0, 0), ["Radius"] = V(0.1, 0.1, 0.1), ["Sides"] = 16, ["Segments"] = 8, ["Surface"] = "Electron" } });
        }
        recipe.Step("Show all four layers.", "set_layers", new { foreground = new[] { 1, 2, 3, 4 } });
        recipe.Step("A new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step("Four seconds at 30 frames a second, a deep blue gradient, and little ambient light.", "set_scene", new { first_frame = 1, last_frame = frames, fps = 30, gradient = true, gradient_top = new[] { 12, 16, 40 }, gradient_bottom = new[] { 0, 0, 0 }, ambient_intensity = 0.15, frame = 1 });
        recipe.Step("The nucleus, by its layer.", "add_object", new { layer = 1, name = "Nucleus" });
        recipe.Step("It turns once in the 120 frames: frame 120 is a frame's turn short of 360, which frame 1 completes...", "set_key", new { item = "Nucleus", channel = "heading", frame = 1, value = 0, linear = true });
        recipe.Step(null, "set_key", new { item = "Nucleus", channel = "heading", frame = frames, value = R(360.0 * (frames - 1) / frames), linear = true });
        recipe.Step("...and repeats.", "set_end_behavior", new { item = "Nucleus", channel = "heading", post = "repeat" });
        foreach (var (k, layer, period, phase) in new[] { (1, 2, 30, 0), (2, 3, 24, 60), (3, 4, 40, 120) })
        {
            recipe.Step(k == 1 ? "Each orbit hangs from a Spin null turning about its own Y, under a Tilt null that tips its plane: three planes 120° apart, each 62° from level." : null, "add_null", new { name = $"Tilt {k}", rotation = V((k - 1) * 120, 62, 0) });
            recipe.Step(null, "add_null", new { name = $"Spin {k}", parent = $"Tilt {k}" });
            recipe.Step(null, "add_object", new { layer, name = $"Orbit {k}", parent = $"Spin {k}" });
            recipe.Step(k == 1 ? "A whole number of turns in 120 frames, with linear keys and a repeating end, keeps each spin steady and looping." : null, "set_key", new { item = $"Spin {k}", channel = "heading", frame = 1, value = phase, linear = true });
            recipe.Step(null, "set_key", new { item = $"Spin {k}", channel = "heading", frame = 1 + period, value = phase + 360, linear = true });
            recipe.Step(null, "set_end_behavior", new { item = $"Spin {k}", channel = "heading", post = "repeat" });
        }
        recipe.Step("A warm key light (Layout's default one)...", "set_item", new { item = "Light", intensity = 0.9, color = new[] { 255, 245, 235 } });
        recipe.Step("...and a blue rim light from behind.", "add_light", new { type = "distant", name = "Rim", rotation = V(200, 25, 0), color = new[] { 120, 150, 255 }, intensity = 0.5 });
        recipe.Step("The camera, a little above, looking at the nucleus.", "set_transform", new { item = "Camera", frame = 1, position = V(0, 1.6, -6.8), key = false });
        recipe.Step(null, "look_at", new { item = "Camera", point = V(0, 0, 0), frame = 1, key = false });
        recipe.Step("Render frame 10 to check it.", "render_scene", new { frame = 10, width = w, height = (int)Math.Round(w * 9.0 / 16), samples = 2 });
        return recipe.Text("For the movie, render_animation the 120 frames in batches that fit its 20 million ray limit (the last one with movie \"mp4\" joins only its own frames, so for one movie of all 120 frames use a size that fits in one call, e.g. width 320 and height 180 with 2 samples), or run samples/mcp/carbon_atom.py --movie.");
    }

    [McpServerPrompt(Name = "morph_smile", Title = "Morph: smile")]
    [Description("Shows morph targets: a smiley badge whose mouth changes from straight to a smile, through a morph made from a copy of the face in another layer, keyed with set_key on morph.Smile.")]
    public static string MorphSmile(
        [Description("Frames for the smile to grow, 2 to 120 (default 30).")] string? frames = null,
        [Description("The face's colour as r, g, b from 0 to 255, or a name (default yellow [255, 210, 40]).")] string? color = null,
        [Description("Width of the check renders in pixels (default 320).")] string? width = null)
    {
        int n = Whole(frames, 30, "frames", 2, 120);
        int w = Whole(width, 320, "width", 16, 1920);
        var c = Colour(color, [255, 210, 40]);
        var recipe = new Recipe("Make a morph target as LightWave makes endomorphs: model the base in layer 1, copy it to layer 2, move points in the copy (never adding or removing any), then give the scene's object a morph from layer 2 and key its weight.");
        recipe.Step("Start an empty object (save the current one first if new refuses).", "new", new { });
        recipe.Step("Surfaces: the face...", "set_surface", new { name = "Face", color = c, specular = 0.3, smoothing = true, assign = false });
        recipe.Step("...and the features.", "set_surface", new { name = "Features", color = new[] { 40, 30, 20 }, specular = 0.5, glossiness = 128, smoothing = true, assign = false });
        recipe.Step("The face: a round badge facing the camera (-Z).", "run_tool", new { name = "Disc", @params = new Dictionary<string, object> { ["Center"] = V(0, 0, 0.03), ["Radius"] = V(0.5, 0.5, 0.5), ["Height"] = 0.04, ["Axis"] = "Z", ["Sides"] = 48, ["Surface"] = "Face" } });
        foreach (double x in new[] { -0.15, 0.15 })
            recipe.Step(x < 0 ? "Two eyes in front of it." : null, "run_tool", new { name = "Ball", @params = new Dictionary<string, object> { ["Center"] = V(x, 0.15, -0.02), ["Radius"] = V(0.06, 0.06, 0.06), ["Sides"] = 12, ["Segments"] = 6, ["Surface"] = "Features" } });
        recipe.Step("A straight mouth: a bar cut into 8 along X, so it has points to bend.", "run_tool", new { name = "Box", @params = new Dictionary<string, object> { ["Center"] = V(0, -0.2, -0.02), ["Size"] = V(0.4, 0.05, 0.04), ["Segments X"] = 8, ["Surface"] = "Features" } });
        recipe.Step("Copy the whole face to layer 2: the morph target starts as an exact copy, the same points in the same order.", "copy_layer", new { from = 1, to = 2 });
        recipe.Step("Work on the copy.", "set_layers", new { foreground = new[] { 2 } });
        recipe.Step("Select points.", "set_selection_mode", new { mode = "points" });
        // The mouth's columns of points are at x = -0.2, -0.15 … 0.2: lift the outer three on each side, then the outer
        // two again, then the ends again, for a curve.
        foreach (var (from, rise) in new[] { (0.09, "the outer three columns of the mouth's points on the left"), (0.14, "the outer two"), (0.19, "the ends") })
        {
            recipe.Step($"Select {rise}...", "select_box", new { min = V(-0.26, -0.3, -0.1), max = V(-from, -0.1, 0.005) });
            recipe.Step("...and the same on the right...", "select_box", new { min = V(from, -0.3, -0.1), max = V(0.26, -0.1, 0.005), add = true });
            recipe.Step("...and lift them.", "run_tool", new { name = "Move", @params = new Dictionary<string, object> { ["Offset"] = V(0, 0.03, 0) } });
        }
        recipe.Step("Clear the selection.", "select_none", new { });
        recipe.Step("Look at the smile in layer 2.", "render_view", new { view = "front", width = 240, height = 240 });
        recipe.Step("Back to layer 1, the base.", "set_layers", new { foreground = new[] { 1 } });
        recipe.Step("A new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step("Put just layer 1 in the scene: the Modeler's object without a layer would merge layer 2 in too.", "add_object", new { layer = 1, name = "Smiley" });
        recipe.Step("Give it the morph from layer 2 (a copy taken now), starting at weight 0, the base shape.", "add_morph", new { @object = "Smiley", name = "Smile", layer = 2, weight = 0 });
        recipe.Step("Key the weight: 0 (straight) at frame 1...", "set_key", new { item = "Smiley", channel = "morph.Smile", frame = 1, value = 0 });
        recipe.Step($"...to 1 (the full smile) at frame {n}.", "set_key", new { item = "Smiley", channel = "morph.Smile", frame = n, value = 1 });
        recipe.Step("Bring the camera closer.", "set_transform", new { item = "Camera", frame = 1, position = V(0, 0, -2.5), key = false });
        recipe.Step("Render the start...", "render_scene", new { frame = 1, width = w, samples = 2 });
        recipe.Step("...and the smile.", "render_scene", new { frame = n, width = w, samples = 2 });
        return recipe.Text("Weights between 0 and 1 blend; more than 1 exaggerates. get_scene lists the object's morphs and get_keys the weight's keys.");
    }

    [McpServerPrompt(Name = "wave_flag", Title = "Waving flag")]
    [Description("Shows displacements: a flag on a pole rippling in the wind, through a travelling wave displacement whose amplitude is keyed.")]
    public static string WaveFlag(
        [Description("The flag's colour as r, g, b from 0 to 255, or a name (default red [200, 30, 40]).")] string? color = null,
        [Description("Metres from crest to crest (default 0.8).")] string? wavelength = null,
        [Description("The waves' height, metres (default 0.08).")] string? amplitude = null,
        [Description("Frames, 2 to 120 (default 60).")] string? frames = null,
        [Description("Width of the frames in pixels (default 240).")] string? width = null)
    {
        var c = Colour(color, [200, 30, 40]);
        double l = Number(wavelength, 0.8, "wavelength", 0.05, 100), a = Number(amplitude, 0.08, "amplitude", 0, 10);
        int n = Whole(frames, 60, "frames", 2, ModelerTools.MaxAnimationFrames);
        int w = Whole(width, 240, "width", 16, ModelerTools.MaxAnimationSize);
        var recipe = new Recipe("Make a flag wave with a displacement: a wave that moves the flag's points along Z, with crests that travel along X, away from the pole.");
        recipe.Step("Start an empty object (save the current one first if new refuses).", "new", new { });
        recipe.Step("The flag's surface, double-sided so it shows from behind.", "set_surface", new { name = "Flag", color = c, specular = 0.2, smoothing = true, double_sided = true, assign = false });
        recipe.Step("The pole's.", "set_surface", new { name = "Pole", color = new[] { 200, 200, 210 }, specular = 0.6, glossiness = 256, smoothing = true, assign = false });
        recipe.Step("The flag in layer 1: thin, and cut into 32 along X so the wave has points to move.", "run_tool", new { name = "Box", @params = new Dictionary<string, object> { ["Center"] = V(0.8, 1.6, 0), ["Size"] = V(1.6, 1, 0.01), ["Segments X"] = 32, ["Segments Y"] = 4, ["Surface"] = "Flag" } });
        recipe.Step("The pole in layer 2.", "set_layers", new { foreground = new[] { 2 } });
        recipe.Step(null, "run_tool", new { name = "Disc", @params = new Dictionary<string, object> { ["Center"] = V(-0.03, 1.25, 0), ["Radius"] = V(0.03, 0.03, 0.03), ["Height"] = 2.5, ["Axis"] = "Y", ["Sides"] = 12, ["Surface"] = "Pole" } });
        recipe.Step("Back to layer 1.", "set_layers", new { foreground = new[] { 1 } });
        recipe.Step("A new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step($"Frames 1 to {n}.", "set_scene", new { first_frame = 1, last_frame = n, frame = 1 });
        recipe.Step("The flag and the pole as objects of their own.", "add_object", new { layer = 1, name = "Flag" });
        recipe.Step(null, "add_object", new { layer = 2, name = "Pole" });
        recipe.Step($"The wave: points move along z, the crests run along x, {F(l)} m apart, travelling a wavelength every 20 frames.", "add_displacement", new { @object = "Flag", shape = "wave", direction = "z", along = "x", wavelength = R(l), speed = R(l / 20), amplitude = R(a) });
        recipe.Step("Key its amplitude so the wind picks up: low at the start...", "set_key", new { item = "Flag", channel = "displacement.1.amplitude", frame = 1, value = R(a * 0.3) });
        recipe.Step("...full by the end.", "set_key", new { item = "Flag", channel = "displacement.1.amplitude", frame = n, value = R(a) });
        recipe.Step("The camera, in front and to the left of the flag.", "set_transform", new { item = "Camera", frame = 1, position = V(-0.6, 1.5, -3.2), key = false });
        recipe.Step(null, "look_at", new { item = "Camera", point = V(0.7, 1.4, 0), frame = 1, key = false });
        recipe.Step("Check a frame.", "render_scene", new { frame = n, width = w, samples = 1 });
        recipe.Step("Render it, every other frame, with a looping GIF.", "render_animation", new { folder = "flag", name = "flag", first = 1, last = n, step = 2, width = w, samples = 1, movie = "gif" });
        return recipe.Text("For a ripple instead (a pond's surface), add_displacement with shape ripple, direction y and a center.");
    }

    // ───────────────────────────── writing them ─────────────────────────────

    /// <summary>A recipe's text being built: what it makes, then numbered steps, each with its tool call.</summary>
    private sealed class Recipe(string intro)
    {
        private readonly StringBuilder _text = new(intro + "\n\nFollow these steps in order. A line starting with \"" + CallMarker.Trim() + "\" is one tool call, with its arguments as JSON. If a step fails, read the error, fix the cause and carry on.\n");
        private int _number;

        public void Step(string? says, string tool, object args)
        {
            _number++;
            _text.Append('\n').Append(_number).Append('.').Append(says != null ? " " : "");
            if (says != null) _text.Append(says);
            _text.Append("\n   ");
            _text.Append(CallMarker).Append(tool).Append(' ').Append(JsonSerializer.Serialize(args));
        }

        public string Text(string closing) => _text.Append("\n\n").Append(closing).ToString();
    }

    private static double R(double v) => Math.Round(v, 4) + 0.0;

    private static double[] V(double x, double y, double z) => [R(x), R(y), R(z)];

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Text(string? given, string fallback) => string.IsNullOrWhiteSpace(given) ? fallback : given.Trim();

    /// <summary>A number argument, as text (MCP prompt arguments are), within limits.</summary>
    internal static double Number(string? given, double fallback, string what, double min, double max)
    {
        if (string.IsNullOrWhiteSpace(given)) return fallback;
        if (!double.TryParse(given.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new McpException($"{what} must be a number, such as {F(fallback)}, not \"{given}\".");
        if (v < min || v > max) throw new McpException($"{what} must be from {F(min)} to {F(max)}.");
        return v;
    }

    internal static int Whole(string? given, int fallback, string what, int min, int max)
    {
        double v = Number(given, fallback, what, min, max);
        if (v != Math.Round(v)) throw new McpException($"{what} must be a whole number.");
        return (int)v;
    }

    private static string Choice(string? given, string fallback, string what, params string[] choices)
    {
        if (string.IsNullOrWhiteSpace(given)) return fallback;
        string v = given.Trim().ToLowerInvariant();
        return choices.Contains(v) ? v : throw new McpException($"{what} must be {string.Join(", ", choices[..^1])} or {choices[^1]}, not \"{given}\".");
    }

    private static readonly Dictionary<string, int[]> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = [245, 245, 245], ["black"] = [25, 25, 25], ["grey"] = [128, 128, 128], ["gray"] = [128, 128, 128],
        ["red"] = [200, 30, 40], ["green"] = [40, 160, 60], ["blue"] = [40, 80, 200], ["yellow"] = [255, 210, 40],
        ["orange"] = [240, 130, 30], ["purple"] = [120, 50, 160], ["brown"] = [120, 75, 40], ["gold"] = [212, 175, 55],
        ["silver"] = [192, 192, 200], ["ivory"] = [235, 225, 200], ["wood"] = [150, 100, 60],
    };

    /// <summary>A colour argument: "r, g, b" (0 to 255, brackets optional) or a name such as blue.</summary>
    internal static int[] Colour(string? given, int[] fallback)
    {
        if (string.IsNullOrWhiteSpace(given)) return fallback;
        if (Named.TryGetValue(given.Trim(), out var named)) return named;
        var parts = given.Trim().Trim('[', ']', '(', ')').Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) && x is >= 0 and <= 255))
            return parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        throw new McpException($"A colour is r, g, b, whole numbers from 0 to 255 (e.g. 200, 80, 60), or one of {string.Join(", ", Named.Keys)}; not \"{given}\".");
    }
}
