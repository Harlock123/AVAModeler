using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using AVAModeler.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SceneModel = AVAModeler.Scene.Scene;
using static AVAModeler.Mcp.SceneCensus;

namespace AVAModeler.Mcp;

/// <summary>
/// The Layout tools: staging, lighting, animating and rendering a scene, on Layout's session (its scene, frame and
/// auto-key) through <see cref="IModelerHost.RunSceneAsync{T}"/>. Every call that changes the scene is one step on
/// Layout's undo history (<see cref="LayoutSession.Edit"/>), the one the person's Ctrl+Z works on, and a call that fails
/// part way is put back as it was (all or nothing) and leaves no step.
/// </summary>
public sealed partial class ModelerTools
{

    /// <summary>The biggest render_scene picture, each way.</summary>
    public const int MaxRenderSize = 1920;
    /// <summary>The most rays a pixel for render_scene.</summary>
    public const int MaxSamples = 16;
    /// <summary>How long one ray-traced picture may take before it's stopped.</summary>
    public static readonly TimeSpan RenderTimeLimit = TimeSpan.FromMinutes(2);
    /// <summary>render_animation's bounds: frames, size each way, rays a pixel, and pixels × rays over all frames.</summary>
    public const int MaxAnimationFrames = 120, MaxAnimationSize = 960, MaxAnimationSamples = 4;
    public const long MaxAnimationWork = 20_000_000;
    /// <summary>How long a whole render_animation may take.</summary>
    public static readonly TimeSpan AnimationTimeLimit = TimeSpan.FromMinutes(5);

    private const string KeyRule = "With key true each channel given gets a key at frame (a channel never keyed before has its constant replaced by this first key, so its animation starts here; a key already at that frame keeps its tension, continuity and bias). With key false the value changes without a new key: a channel with one key or none takes the value throughout, and an animated channel changes its key at frame (an error if it has none there). key defaults to Layout's Auto key setting, which is on unless the person turned it off.";

    // ───────────────────────────── reading the scene ─────────────────────────────

    [McpServerTool(Name = "get_scene", ReadOnly = true, Idempotent = true)]
    [Description("Describes Layout's scene: the frame range, frames per second, current frame, current camera, auto-key, ambient light, background, fog, ambient occlusion and environment; then every item (object, null, light, camera, bone) with its #index, kind, name, parent, visibility, position (metres), rotation [heading, pitch, bank] (degrees), scale, which channels are animated, and its own settings (a light's type, colour, intensity, direction, area size or radius and shadow samples; a camera's zoom, field of view, frame size, depth of field and motion blur; an object's source, point and polygon counts, world bounds, bones, morphs (name, weight channel and weight) and displacements (numbered from 1, with their settings and amplitude); a bone's ik (goal, chain, reach, tip_to_goal, ik_rotation, limits); an item's follow_path (points, length, path_position, moved_to, turned_to); an item's motion modifiers (numbered from 1, with their settings) and modified_motion (the position, rotation and scale they give it)), all at one frame. Also any warnings (files that couldn't be loaded, missing images) and the scene's undo history (undo_scene: its depth, and what undo_scene and redo_scene would do next).")]
    public Task<string> GetScene(
        [Description("The frame to describe positions and values at (default: the current frame).")] double? frame = null,
        CancellationToken cancel = default) =>
        DoScene("read the scene", (c, l) => SceneCensus.Scene(l, c.Document, SceneCensus.Frame(l, frame)), cancel, changes: false);

    [McpServerTool(Name = "get_keys", ReadOnly = true, Idempotent = true)]
    [Description("Lists an item's keys, channel by channel: each key's frame, value, and tension, continuity and bias (Kochanek–Bartels TCB, -1 to 1; left out when 0) and linear flag, its span when it isn't the default TCB (span: linear, stepped or bezier: how the stretch into the key is drawn; see set_key) and its in_slope and out_slope (units per frame) when not 0, plus what the channel does before its first key and after its last (pre and post). A channel never keyed shows its constant value. Channels: position.x/y/z (metres), heading, pitch, bank (degrees), scale.x/y/z, a light's intensity or a camera's zoom, an object's morph weights (morph.<name>) and displacement amplitudes (displacement.<number>.amplitude), and path.position for an item following a path.")]
    public Task<string> GetKeys(
        [Description("The item's name, or #index from get_scene.")] string item,
        [Description("Just this channel (default: all of them).")] string? channel = null,
        CancellationToken cancel = default) =>
        DoScene("read keys", (c, l) =>
        {
            var it = Item(l.Scene, item);
            var channels = channel == null ? Channels(it).ToList() : [Channel(it, channel)];
            return new JsonObject
            {
                ["item"] = it.Name,
                ["frames_with_keys"] = new JsonArray(SceneCensus.KeyFrames(it).Where(f => f >= l.Scene.FirstFrame && f <= l.Scene.LastFrame).Select(f => (JsonNode)Census.Round(f)).ToArray()),
                ["channels"] = new JsonArray(channels.Select(ch => (JsonNode)Keys(ch.Name, ch.Envelope, l.Scene)).ToArray()),
            };
        }, cancel, changes: false);

    // ───────────────────────────── scene files ─────────────────────────────

    [McpServerTool(Name = "new_scene", Destructive = true)]
    [Description("Starts a new scene as Layout does: a camera at [0, 0, -5] looking along +Z and a distant light, frames 1 to 60 at 30 frames a second, nothing else (add the object with add_object). Refuses if the scene has changes that haven't been saved, unless discard_changes is true. It clears the scene's undo history, so undo_scene can't take this back.")]
    public Task<string> NewScene(
        [Description("Throw away the scene's unsaved changes.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        DoScene("started a new scene", (c, l) =>
        {
            CheckDiscard(l, discard_changes);
            l.Replace(SceneModel.CreateDefault());
            l.Warnings.Clear();
            return SceneCensus.Scene(l, c.Document, l.Frame);
        }, cancel);

    [McpServerTool(Name = "open_scene", Destructive = true)]
    [Description("Opens a scene in place of the current one: .avascene (AVA Modeler's own, keeps everything, meshes usually inside it) or LightWave .lws (LightWave 4 and 5, and later). The path is relative to the working folder and must stay inside it; objects the scene names are loaded from inside the working folder too (one that can't be is a placeholder, and a warning says why). Refuses if the scene has unsaved changes, unless discard_changes is true. It clears the scene's undo history, so undo_scene can't take this back.")]
    public Task<string> OpenScene(
        [Description("The scene file, relative to the working folder, e.g. \"scenes/table.avascene\".")] string path,
        [Description("Throw away the current scene's unsaved changes.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        DoScene($"opened {Path.GetFileName(path)}", (c, l) =>
        {
            var files = Files(c);
            string full = files.ScenePath(path);
            if (!File.Exists(full)) throw new FileNotFoundException($"There's no scene {path} in the working folder ({files.Root}).", full);
            CheckDiscard(l, discard_changes);
            var notes = new List<string>();
            l.LoadScene(full, p => files.LoadForScene(p, notes));
            l.Warnings.AddRange(notes);
            return SceneCensus.Scene(l, c.Document, l.Frame);
        }, cancel);

    [McpServerTool(Name = "save_scene", Destructive = true)]
    [Description("Saves the scene, the format chosen by the extension. .avascene (AVA Modeler's own) keeps everything; with embed_objects (the default) the objects' meshes, surfaces and textures go inside it, so it needs no other files; without, objects that came from a file are referenced by it (the Modeler's unsaved changes are then lost). .lws (LightWave 3 scene) keeps no geometry: objects are referenced by their files (save the object first with save, as .lwo), and visibility, morphs, displacements and the camera's safe areas are left out; the warnings say what was. The path is relative to the working folder and must stay inside it; an existing file is only replaced when overwrite is true.")]
    public Task<string> SaveScene(
        [Description("The file, relative to the working folder, e.g. \"table.avascene\" or \"table.lws\".")] string path,
        [Description("Replace the file if it already exists.")] bool overwrite = false,
        [Description(".avascene only: put the objects' meshes in the file (default true).")] bool embed_objects = true,
        CancellationToken cancel = default) =>
        DoScene($"saved {Path.GetFileName(path)}", (c, l) =>
        {
            var files = Files(c);
            string full = files.ScenePath(path);
            files.CheckWritable([full], overwrite);
            bool lws = full.EndsWith(".lws", StringComparison.OrdinalIgnoreCase);
            var warnings = l.SaveScene(full, embed_objects).ToList();
            files.Wrote(full);
            foreach (var o in l.Scene.Objects)
                if ((lws || !embed_objects) && o.Document is { Modified: true } doc && (o.SourcePath ?? doc.FilePath) != null)
                    warnings.Add($"{o.Name}'s object has changes not saved to {Path.GetFileName(o.SourcePath ?? doc.FilePath)}: the scene refers to the file, so save the object too.");
            if (lws && l.Scene.Items.Any(i => !i.Visible)) warnings.Add("Hidden items are saved as visible: .lws has no place for that.");
            return new JsonObject { ["saved"] = full, ["bytes"] = new FileInfo(full).Length, ["warnings"] = Strings(warnings) };
        }, cancel);

    [McpServerTool(Name = "export_scene", Destructive = true)]
    [Description("Exports the scene as glTF 2.0 for other programs (game engines, three.js, Blender): .glb (binary, one file) or .gltf (JSON, its data inside it unless separate_bin). Objects (their undeformed meshes, surfaces as PBR materials and colour images), nulls, cameras (perspective, the first frame's field of view) and lights (KHR_lights_punctual: distant → directional, point, spot with its cone) become nodes in their parent hierarchy, converted to glTF's right-handed +Y up space; keyed position, rotation and scale become one animation, sampled at every frame from the first to the last (by the frame step). Bones, morphs and displacements aren't exported (objects are in their rest shape), nor animated zoom or intensity; the warnings say what was left out. It doesn't change the scene or its file (save_scene keeps the scene itself). The path is relative to the working folder and must stay inside it; an existing file is only replaced when overwrite is true.")]
    public Task<string> ExportScene(
        [Description("The file, relative to the working folder, e.g. \"scene.glb\" or \"scene.gltf\".")] string path,
        [Description("Replace the file (and the .bin beside it) if it already exists.")] bool overwrite = false,
        [Description(".gltf only: write the geometry, images and animation to a .bin of the same name beside it.")] bool separate_bin = false,
        CancellationToken cancel = default) =>
        DoScene($"exported {Path.GetFileName(path)}", (c, l) =>
        {
            var files = Files(c);
            string full = files.Resolve(path);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (!SceneGltf.Extensions.Contains(ext))
                throw new NotSupportedException($"export_scene writes glTF: .glb or .gltf, not {(ext == "" ? "a file without an extension" : ext)}. To save the scene itself, use save_scene with .avascene or .lws.");
            var targets = SceneGltf.Targets(full, separate_bin);
            files.CheckWritable(targets, overwrite);
            var warnings = SceneGltf.Save(l.Scene, full, separate_bin);
            files.Wrote(targets);
            var result = new JsonObject { ["exported"] = full, ["bytes"] = new FileInfo(full).Length, ["frames"] = SceneGltf.Frames(l.Scene).Count, ["warnings"] = Strings(warnings) };
            if (targets.Length > 1) result["also"] = new JsonArray([.. targets.Skip(1).Select(t => (JsonNode)t)]);
            return result;
        }, cancel, changes: false);

    // ───────────────────────────── adding items ─────────────────────────────

    [McpServerTool(Name = "add_object", Destructive = true)]
    [Description("Puts an object in the scene. With no file, the Modeler's object (in the live app, the one open in the Modeler) goes in live: the scene shows each change made to it later, as Layout's Add › The Modeler's Object does; if it's already there that item is returned, not added again. With file, an object file from the working folder (.lwo, .obj, .geo, .glb, .gltf, .stl, .3mf or a plugin format). layer (1 to 10, as the app's layer buttons number them) takes just that layer; otherwise every non-empty layer, merged. The object's own coordinates are kept: position, rotation and scale (constants, not keys) then place it. Returns the item, with its world bounds.")]
    public Task<string> AddObject(
        [Description("An object file relative to the working folder, e.g. \"table.lwo\". Leave out for the Modeler's object.")] string? file = null,
        [Description("Just this layer, 1 to 10 as the app's layer buttons number them. Default: every non-empty layer.")] int? layer = null,
        [Description("The item's name (default: the file's name, or \"Modeler object\"). Made unique if taken.")] string? name = null,
        [Description("Where it goes [x, y, z], metres (default [0, 0, 0]).")] double[]? position = null,
        [Description("[heading, pitch, bank] in degrees (default [0, 0, 0]).")] double[]? rotation = null,
        [Description("[x, y, z] scale (default [1, 1, 1]).")] double[]? scale = null,
        [Description("An item to move with (its parent), by name.")] string? parent = null,
        CancellationToken cancel = default) =>
        DoScene("added an object", (c, l) =>
        {
            var s = l.Scene;
            var parentItem = parent == null ? null : Item(s, parent, "parent");
            ObjectItem obj;
            bool existed = false;
            if (file != null)
            {
                var doc = Files(c).Load(file);
                CheckLayer(doc, layer);
                obj = s.AddObject(FreeName(s, name, Path.GetFileNameWithoutExtension(file)), doc, layer - 1);
                obj.SourcePath = doc.FilePath;
            }
            else if (!Profile.HasModelerObject)
                throw new McpException($"{Profile.Title} has no Modeler object to stage: give file, an object file in the working folder (.lwo, .obj, .geo, .glb, .gltf or .stl).");
            else if (layer != null)
            {
                CheckLayer(c.Document, layer);
                obj = s.AddObject(FreeName(s, name, $"Modeler layer {layer}"), c.Document, layer - 1);
                obj.SourcePath = c.Document.FilePath;
            }
            else
            {
                existed = s.Objects.Any(o => ReferenceEquals(o.Document, c.Document) && o.Layer == null);
                obj = l.ShowModelerObject(c.Document);
                if (!existed && name != null) obj.Name = FreeName(s, name, name);
            }
            if (!existed) Place(l, obj, position, rotation, scale, parentItem);
            var result = ItemNow(c, l, obj, l.Frame);
            if (existed) result["note"] = "The Modeler's object was already in the scene: this is that item, unchanged. Move it with set_transform.";
            return result;
        }, cancel);

    [McpServerTool(Name = "add_light", Destructive = true)]
    [Description("Adds a light. distant: parallel rays along its +Z axis from infinitely far (like the sun; its position doesn't matter). point: shines every way from its position. spot: shines along its +Z axis inside a cone. area: a rectangle area_size [width, height] metres in its own XY plane, shining from its front along +Z, brightest straight ahead, casting soft shadows (a big area light makes soft ones, a small one sharper); a point or spot light with a radius (metres) is a ball of light with soft shadows too. Soft lights take shadow_samples rays a pixel (16 by default; more is smoother and slower). Unrotated, +Z points forward (away from the viewer in the Front view); turn it with rotation [heading, pitch, bank] in degrees (heading 90 shines along +X, pitch 90 straight down), or give look_at to aim it at a point. Colour is [r, g, b], whole numbers from 0 to 255 like surfaces'; intensity 1 is LightWave's 100% (more is brighter). Shadows are ray-traced. Values are constants, not keys.")]
    public Task<string> AddLight(
        [Description("distant (default), point or spot.")] string type = "distant",
        [Description("The light's name (default: \"Distant Light\", \"Point Light\" or \"Spot Light\"). Made unique if taken.")] string? name = null,
        [Description("[x, y, z] in metres (default [0, 0, 0]).")] double[]? position = null,
        [Description("[heading, pitch, bank] in degrees (default [0, 0, 0]: shining along +Z).")] double[]? rotation = null,
        [Description("A point [x, y, z] to aim at (sets heading and pitch; instead of rotation).")] double[]? look_at = null,
        [Description("[r, g, b], each 0 to 255 (default white [255, 255, 255]).")] double[]? color = null,
        [Description("Brightness: 1 is 100% (default 1).")] double? intensity = null,
        [Description("Spot only: the cone's half-angle in degrees, 0.1 to 90 (default 30).")] double? cone_angle = null,
        [Description("Spot only: the soft edge in degrees, inside the rim, over which it fades (default 5).")] double? edge_angle = null,
        [Description("Point and spot: the distance in metres at which it has faded (linearly) to nothing; 0 (default) for no falloff.")] double? range = null,
        [Description("Cast ray-traced shadows (default true).")] bool shadows = true,
        [Description("An item to move with (its parent), by name.")] string? parent = null,
        [Description("Area only: [width, height] in metres along its X and Y axes (default [1, 1]).")] double[]? area_size = null,
        [Description("Point and spot: the light's radius in metres, for soft shadows (default 0: a point, with hard shadows).")] double? radius = null,
        [Description("Area, or point and spot with a radius: rays a pixel for its light and soft shadows, 1 to 256 (default 16).")] int? shadow_samples = null,
        CancellationToken cancel = default) =>
        DoScene("added a light", (c, l) =>
        {
            var kind = Json.Choice<LightType>(type, "type");
            var s = l.Scene;
            var parentItem = parent == null ? null : Item(s, parent, "parent");
            var light = s.AddLight(FreeName(s, name, $"{kind} Light"), kind);
            SetLight(light, color, intensity, cone_angle, edge_angle, range, shadows);
            SetSoftLight(light, area_size, radius, shadow_samples);
            Place(l, light, position, rotation, null, parentItem, look_at);
            return ItemNow(c, l, light, l.Frame);
        }, cancel);

    [McpServerTool(Name = "add_camera", Destructive = true)]
    [Description("Adds a camera. Unrotated it looks along +Z with +Y up; turn it with rotation [heading, pitch, bank] in degrees (heading 90 looks along +X, positive pitch looks down), or give look_at to aim it at a point. Its lens is LightWave's zoom factor, measured against the picture's height: zoom = 1 / tan(vertical field of view / 2); the default 3.2 is 34.7° high (45.2° across at 4:3). Give zoom or fov, not both. It becomes the current camera (the one render_scene uses) unless make_current is false. Values are constants, not keys.")]
    public Task<string> AddCamera(
        [Description("The camera's name (default \"Camera\"). Made unique if taken.")] string? name = null,
        [Description("[x, y, z] in metres (default [0, 0, 0]).")] double[]? position = null,
        [Description("[heading, pitch, bank] in degrees (default [0, 0, 0]: looking along +Z).")] double[]? rotation = null,
        [Description("A point [x, y, z] to aim at (sets heading and pitch; instead of rotation).")] double[]? look_at = null,
        [Description("Zoom factor, more than 0 (default 3.2).")] double? zoom = null,
        [Description("Vertical field of view in degrees, more than 0 and less than 180 (instead of zoom).")] double? fov = null,
        [Description("Picture width in pixels (default 640).")] int width = 640,
        [Description("Picture height in pixels (default 480).")] int height = 480,
        [Description("Make it the camera render_scene and the camera view use (default true).")] bool make_current = true,
        [Description("An item to move with (its parent), by name.")] string? parent = null,
        CancellationToken cancel = default) =>
        DoScene("added a camera", (c, l) =>
        {
            var s = l.Scene;
            var parentItem = parent == null ? null : Item(s, parent, "parent");
            var cam = s.AddCamera(FreeName(s, name, "Camera"));
            SetCamera(cam, zoom, fov, width, height);
            Place(l, cam, position, rotation, null, parentItem, look_at);
            if (make_current) s.CurrentCamera = cam;
            return ItemNow(c, l, cam, l.Frame);
        }, cancel);

    [McpServerTool(Name = "add_null", Destructive = true)]
    [Description("Adds a null: an invisible item to parent other items to and animate. Useful for orbits: put a null at the centre, parent the camera to it (set_item parent), and key the null's heading from 0 to 360. Values are constants, not keys.")]
    public Task<string> AddNull(
        [Description("The null's name (default \"Null\"). Made unique if taken.")] string? name = null,
        [Description("[x, y, z] in metres (default [0, 0, 0]).")] double[]? position = null,
        [Description("[heading, pitch, bank] in degrees (default [0, 0, 0]).")] double[]? rotation = null,
        [Description("An item to move with (its parent), by name.")] string? parent = null,
        CancellationToken cancel = default) =>
        DoScene("added a null", (c, l) =>
        {
            var s = l.Scene;
            var parentItem = parent == null ? null : Item(s, parent, "parent");
            var n = s.AddNull(FreeName(s, name, "Null"));
            Place(l, n, position, rotation, null, parentItem);
            return ItemNow(c, l, n, l.Frame);
        }, cancel);

    [McpServerTool(Name = "add_bone", Destructive = true)]
    [Description("Adds a bone to an object, to bend it. At rest the bone starts at rest_position and runs rest_length along its +Z axis turned by rest_rotation [heading, pitch, bank] (degrees), all in the object's own coordinates, or its parent bone's (parent_bone: the bone's start is then measured from the parent bone's start, along its axes; put it at [0, 0, parent length] to chain them). The bone's channels start at its rest pose; key its rotation (set_transform, set_key) to bend the object. Each point follows every bone, weighted by the bone's strength over its distance to the bone's rest segment to the power 4.")]
    public Task<string> AddBone(
        [Description("The object the bone deforms, by name.")] string @object,
        [Description("The bone's name (default \"Bone\"). Made unique if taken.")] string? name = null,
        [Description("Where it starts at rest [x, y, z], metres (default [0, 0, 0]).")] double[]? rest_position = null,
        [Description("Its direction at rest [heading, pitch, bank] in degrees (default [0, 0, 0]: along +Z; pitch -90 points up +Y).")] double[]? rest_rotation = null,
        [Description("Its length in metres, more than 0 (default 1).")] double rest_length = 1,
        [Description("Another bone of the same object to hang from.")] string? parent_bone = null,
        [Description("How strongly it pulls (default 1).")] double strength = 1,
        CancellationToken cancel = default) =>
        DoScene("added a bone", (c, l) =>
        {
            var s = l.Scene;
            if (Item(s, @object, "object") is not ObjectItem owner) throw new ArgumentException($"{@object} isn't an object: bones belong to objects.");
            Bone? up = null;
            if (parent_bone != null)
            {
                up = Item(s, parent_bone, "parent bone") as Bone;
                if (up == null || up.Owner != owner) throw new ArgumentException($"{parent_bone} isn't a bone of {owner.Name}.");
            }
            if (!(rest_length > 0) || !double.IsFinite(rest_length)) throw new ArgumentException("rest_length must be more than 0 metres.");
            if (!double.IsFinite(strength)) throw new ArgumentException("strength must be a number.");
            var bone = s.AddBone(owner, FreeName(s, name, "Bone"), rest_position == null ? Vec3.Zero : Vector(rest_position, "rest_position"),
                rest_rotation == null ? Vec3.Zero : Vector(rest_rotation, "rest_rotation"), rest_length);
            bone.Strength = strength;
            if (up != null) bone.Parent = up;
            s.Notify();
            return ItemNow(c, l, bone, l.Frame);
        }, cancel);

    [McpServerTool(Name = "remove_item", Destructive = true)]
    [Description("Removes an item from the scene. Its children move up to its parent (and keep their own motion); an object takes its bones with it. Removing an object only takes it out of the scene: the Modeler's object and files are untouched.")]
    public Task<string> RemoveItem(
        [Description("The item's name, or #index from get_scene.")] string item,
        CancellationToken cancel = default) =>
        DoScene("removed an item", (c, l) =>
        {
            var it = Item(l.Scene, item);
            var gone = new List<string> { it.Name };
            if (it is ObjectItem o) gone.AddRange(o.Bones.Select(b => b.Name));
            l.Remove(it);
            return new JsonObject
            {
                ["removed"] = Strings(gone),
                ["items"] = Strings(l.Scene.Items.Select(i => $"{i.Kind.ToLowerInvariant()} {i.Name}")),
                ["camera"] = l.Scene.CurrentCamera?.Name,
            };
        }, cancel);

    // ───────────────────────────── arranging items ─────────────────────────────

    [McpServerTool(Name = "set_item", Destructive = true)]
    [Description("Changes an item's settings, leaving out what you don't give: its name, parent (the item it moves with; \"\" for none), pivot (the point in its own coordinates it turns and scales about, placed at its position), visibility and locked (a lock against the mouse in the app's views: it isn't picked or dragged there; numbers and agents still change it). A light's type (distant, point, spot or area), colour (0 to 255), intensity (1 is 100%), cone_angle and edge_angle (spot, degrees), range (metres; 0 none), shadows, area_size (area, metres), radius (point and spot, metres: soft shadows) and shadow_samples. A camera's zoom or fov (vertical, degrees), width and height (pixels), make_current, depth of field (depth_of_field on/off, focal_distance in metres along its view or focus_on an item to put it in focus, and f_stop as LightWave's lens: the focal length is zoom × 7.5 mm and the opening that over the f-stop, so 4 is barely any blur at scene scale and 0.1 to 0.5 a strong, miniature-like one; or aperture, the opening's radius in metres, instead of f_stop; dof_samples rays a pixel) and motion blur (motion_blur on/off, shutter as a fraction of a frame centred on it, 0.5 by default; blur_samples rays a pixel). Depth of field and motion blur raise the rays a pixel to their samples. An object's cast_shadows and receive_shadows. A bone's rest pose (rest_position, rest_rotation, rest_length: where it binds to the object; changing it leaves the bone's pose, its channels, where it was, so the object bends by the difference, unless reset_pose is true), rest_at_frame (Layout's Rest Here: makes its pose at that frame its rest pose), reset_pose (puts its channels back at its rest pose, unanimated), strength, active, and the weight_map it follows (weight_map_only). Intensity and zoom set the value throughout; when they're animated, use set_key.")]
    public Task<string> SetItem(
        [Description("The item's name, or #index from get_scene.")] string item,
        [Description("A new name.")] string? name = null,
        [Description("The item to move with, by name; \"\" for none.")] string? parent = null,
        [Description("[x, y, z] in the item's own coordinates.")] double[]? pivot = null,
        [Description("Shown and rendered.")] bool? visible = null,
        [Description("Locked against the mouse in the app's views (not picked or dragged there).")] bool? locked = null,
        [Description("Light: distant, point or spot.")] string? type = null,
        [Description("Light: [r, g, b], each 0 to 255.")] double[]? color = null,
        [Description("Light: brightness, 1 is 100%.")] double? intensity = null,
        [Description("Spot light: the cone's half-angle in degrees.")] double? cone_angle = null,
        [Description("Spot light: the soft edge in degrees.")] double? edge_angle = null,
        [Description("Point or spot light: metres to fade to nothing; 0 for no falloff.")] double? range = null,
        [Description("Light: cast shadows.")] bool? shadows = null,
        [Description("Area light: [width, height] in metres.")] double[]? area_size = null,
        [Description("Point or spot light: radius in metres for soft shadows; 0 for a point.")] double? radius = null,
        [Description("Soft light: rays a pixel for its light and shadows, 1 to 256.")] int? shadow_samples = null,
        [Description("Camera: zoom factor (1 / tan(vertical fov / 2)).")] double? zoom = null,
        [Description("Camera: vertical field of view in degrees (instead of zoom).")] double? fov = null,
        [Description("Camera: picture width in pixels.")] int? width = null,
        [Description("Camera: picture height in pixels.")] int? height = null,
        [Description("Camera: make it the current camera.")] bool? make_current = null,
        [Description("Camera: depth of field on or off.")] bool? depth_of_field = null,
        [Description("Camera: the distance in focus, metres in front of it along its view (default 5).")] double? focal_distance = null,
        [Description("Camera: an item to bring into focus at the current frame (sets focal_distance to its depth, and turns depth of field on).")] string? focus_on = null,
        [Description("Camera: LightWave's lens f-stop, more than 0 (default 4); smaller blurs more.")] double? f_stop = null,
        [Description("Camera: the lens opening's radius in metres (instead of f_stop, worked out at the current zoom).")] double? aperture = null,
        [Description("Camera: depth of field rays a pixel, 1 to 256 (default 16).")] int? dof_samples = null,
        [Description("Camera: motion blur on or off.")] bool? motion_blur = null,
        [Description("Camera: how long the shutter is open, as a fraction of a frame, more than 0 and at most 1 (default 0.5).")] double? shutter = null,
        [Description("Camera: motion blur rays a pixel, 1 to 256 (default 16).")] int? blur_samples = null,
        [Description("Object: cast shadows on other things.")] bool? cast_shadows = null,
        [Description("Object: show shadows cast on it.")] bool? receive_shadows = null,
        [Description("Bone: where it starts at rest [x, y, z], metres, in the object's coordinates (or its parent bone's: measured from the parent's start along its axes).")] double[]? rest_position = null,
        [Description("Bone: its direction at rest [heading, pitch, bank], degrees (0, 0, 0 points along +Z; pitch -90 points up +Y), in the object's axes (or its parent bone's).")] double[]? rest_rotation = null,
        [Description("Bone: its length at rest, metres.")] double? rest_length = null,
        [Description("Bone: Layout's Rest Here: make its pose (position and rotation) at this frame its rest pose, so it no longer bends the object there.")] double? rest_at_frame = null,
        [Description("Bone: put its channels (position, rotation, scale) back at its rest pose, unanimated (after rest_position, rest_rotation and rest_at_frame).")] bool? reset_pose = null,
        [Description("Bone: how strongly it pulls.")] double? strength = null,
        [Description("Bone: whether it deforms the object.")] bool? active = null,
        [Description("Bone: the object's weight map it follows (\"\" for none): a point moves with it by its weight there, 0 where it isn't in the map. Make weight maps in the Modeler with auto_weights or set_weights.")] string? weight_map = null,
        [Description("Bone with a weight_map: weigh points by the map alone (true, the default) or by the map times the distance falloff (false).")] bool? weight_map_only = null,
        [Description("Light: light linking: all (the default: every object), only (just light_objects) or except (every object but light_objects). Shadows, flares and beams are as without it.")] string? light_link = null,
        [Description("Light: the objects light_link names, by name.")] string[]? light_objects = null,
        CancellationToken cancel = default) =>
        DoScene("changed an item", (c, l) =>
        {
            var s = l.Scene;
            var it = Item(s, item);
            void Only<T>(object? given, string what) where T : SceneItem
            {
                if (given != null && it is not T)
                    throw new ArgumentException($"{what} is a setting of {typeof(T).Name.Replace("Item", "").ToLowerInvariant()}s, and {it.Name} is {(it is ObjectItem ? "an" : "a")} {it.Kind.ToLowerInvariant()}.");
            }
            Only<LightItem>(type, "type"); Only<LightItem>(color, "color"); Only<LightItem>(intensity, "intensity"); Only<LightItem>(cone_angle, "cone_angle");
            Only<LightItem>(edge_angle, "edge_angle"); Only<LightItem>(range, "range"); Only<LightItem>(shadows, "shadows");
            Only<LightItem>(area_size, "area_size"); Only<LightItem>(radius, "radius"); Only<LightItem>(shadow_samples, "shadow_samples");
            Only<CameraItem>(depth_of_field, "depth_of_field"); Only<CameraItem>(focal_distance, "focal_distance"); Only<CameraItem>(focus_on, "focus_on");
            Only<CameraItem>(f_stop, "f_stop"); Only<CameraItem>(aperture, "aperture"); Only<CameraItem>(dof_samples, "dof_samples");
            Only<CameraItem>(motion_blur, "motion_blur"); Only<CameraItem>(shutter, "shutter"); Only<CameraItem>(blur_samples, "blur_samples");
            Only<CameraItem>(zoom, "zoom"); Only<CameraItem>(fov, "fov"); Only<CameraItem>(width, "width"); Only<CameraItem>(height, "height"); Only<CameraItem>(make_current, "make_current");
            Only<ObjectItem>(cast_shadows, "cast_shadows"); Only<ObjectItem>(receive_shadows, "receive_shadows");
            Only<LightItem>(light_link, "light_link"); Only<LightItem>(light_objects, "light_objects");
            if (it is LightItem linked && (light_link != null || light_objects != null))
            {
                var mode = light_link == null ? linked.LinkMode : Json.Choice<LightLinkMode>(light_link, "light_link");
                var objects = light_objects == null ? linked.LinkedObjects
                    : light_objects.Select(n => Item(s, n, "light_objects") as ObjectItem ?? throw new ArgumentException($"{n} isn't an object: light linking names objects.")).Distinct().ToList();
                if (mode != LightLinkMode.All && objects.Count == 0) throw new ArgumentException($"light_link {mode.ToString().ToLowerInvariant()} needs light_objects: the objects it {(mode == LightLinkMode.Only ? "lights" : "leaves unlit")}.");
                linked.LinkMode = mode;
                linked.LinkedObjects = mode == LightLinkMode.All ? [] : objects;
                s.Notify();
            }
            Only<Bone>(rest_length, "rest_length"); Only<Bone>(strength, "strength"); Only<Bone>(active, "active"); Only<Bone>(weight_map, "weight_map"); Only<Bone>(weight_map_only, "weight_map_only");
            Only<Bone>(rest_position, "rest_position"); Only<Bone>(rest_rotation, "rest_rotation"); Only<Bone>(rest_at_frame, "rest_at_frame"); Only<Bone>(reset_pose, "reset_pose");

            // Names are looked up before the item is renamed.
            var p = parent == null || parent.Trim() is "" or "(none)" ? null : Item(s, parent, "parent");
            if (name != null)
            {
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A name can't be empty.");
                it.Name = name.Trim();
            }
            if (parent != null)
            {
                if (it is Bone b && p == null) p = b.Owner;
                it.Parent = p;
            }
            if (pivot != null) it.Pivot = Vector(pivot, "pivot");
            if (visible is { } v) it.Visible = v;
            if (locked is { } lk) it.Locked = lk;
            switch (it)
            {
                case LightItem light:
                    if (type != null) light.Type = Json.Choice<LightType>(type, "type");
                    SetLight(light, color, intensity, cone_angle, edge_angle, range, shadows);
                    SetSoftLight(light, area_size, radius, shadow_samples);
                    break;
                case CameraItem cam:
                    SetCamera(cam, zoom, fov, width, height);
                    var dof = cam.DepthOfField;
                    if (depth_of_field is { } dofOn) dof = dof with { On = dofOn };
                    if (focal_distance != null && focus_on != null) throw new ArgumentException("Give focal_distance or focus_on, not both.");
                    if (focal_distance is { } fd) dof = dof with { FocalDistance = fd };
                    if (f_stop != null && aperture != null) throw new ArgumentException("Give f_stop or aperture, not both.");
                    if (f_stop is { } fs) dof = dof with { FStop = fs };
                    if (aperture is { } ap)
                    {
                        if (!double.IsFinite(ap) || ap <= 0) throw new ArgumentException("aperture must be more than 0 metres (the lens opening's radius).");
                        dof = dof with { FStop = DepthOfField.FStopFor(ap, cam.Zoom.Evaluate(l.Frame)) };
                    }
                    if (dof_samples is { } ds) dof = dof with { Samples = ds };
                    cam.DepthOfField = dof.Checked();
                    if (focus_on != null) l.FocusOn(cam, Item(s, focus_on, "focus_on"));
                    var blur = cam.MotionBlur;
                    if (motion_blur is { } mb) blur = blur with { On = mb };
                    if (shutter is { } sh) blur = blur with { Shutter = sh };
                    if (blur_samples is { } bs) blur = blur with { Samples = bs };
                    cam.MotionBlur = blur.Checked();
                    if (make_current == true) s.CurrentCamera = cam;
                    else if (make_current == false && s.CurrentCamera == cam)
                        throw new ArgumentException($"To stop using {cam.Name}, make another camera current instead.");
                    break;
                case ObjectItem obj:
                    if (cast_shadows is { } cs) obj.CastShadows = cs;
                    if (receive_shadows is { } rs) obj.ReceiveShadows = rs;
                    break;
                case Bone bone:
                    if (rest_at_frame is { } raf)
                    {
                        if (rest_position != null || rest_rotation != null) throw new ArgumentException("Give rest_at_frame or rest_position and rest_rotation, not both.");
                        bone.RestFromPose(SceneCensus.Frame(l, raf));
                    }
                    if (rest_position != null) bone.RestPosition = Vector(rest_position, "rest_position");
                    if (rest_rotation != null) bone.RestRotation = Vector(rest_rotation, "rest_rotation");
                    if (rest_length is { } rl)
                    {
                        if (!(rl > 0) || !double.IsFinite(rl)) throw new ArgumentException("rest_length must be more than 0 metres.");
                        bone.RestLength = rl;
                    }
                    if (strength is { } st)
                    {
                        if (!double.IsFinite(st)) throw new ArgumentException("strength must be a number.");
                        bone.Strength = st;
                    }
                    if (active is { } ac) bone.Active = ac;
                    if (weight_map != null) bone.WeightMap = weight_map.Trim() is { Length: > 0 } wm ? wm : null;
                    if (weight_map_only is { } wmo) bone.WeightMapOnly = wmo;
                    if (reset_pose == true) bone.ResetPose();
                    break;
            }
            s.Notify();
            return ItemNow(c, l, it, l.Frame);
        }, cancel);

    // ───────────────────────────── animating ─────────────────────────────

    [McpServerTool(Name = "set_transform", Destructive = true)]
    [Description("Sets an item's position [x, y, z] (metres), rotation [heading, pitch, bank] (degrees) and/or scale [x, y, z] at a frame, in its parent's space (the world's if it has none). Heading turns about +Y (positive turns +Z toward +X: to the right seen from above), then pitch about the turned X axis (positive tips +Z down), then bank about the turned Z axis (positive turns +X toward +Y). Frames run from the scene's first to last frame (1 to 60 by default) and between keys the motion follows TCB splines. " + KeyRule + " To animate: set key true at the first frame, then at later frames.")]
    public Task<string> SetTransform(
        [Description("The item's name, or #index from get_scene.")] string item,
        [Description("The frame (default: the current frame). Must be within the scene's frames.")] double? frame = null,
        [Description("[x, y, z] in metres.")] double[]? position = null,
        [Description("[heading, pitch, bank] in degrees.")] double[]? rotation = null,
        [Description("[x, y, z] scale factors (1 is unscaled).")] double[]? scale = null,
        [Description("Key (true) or just set (false); default: Layout's Auto key (on unless the person turned it off).")] bool? key = null,
        CancellationToken cancel = default) =>
        DoScene("moved an item", (c, l) =>
        {
            var it = Item(l.Scene, item);
            double f = SceneCensus.Frame(l, frame);
            if (position == null && rotation == null && scale == null) throw new ArgumentException("Give position, rotation or scale (or several).");
            var vectors = new (double[]? Values, int First, string What)[] { (position, 0, "position"), (rotation, 3, "rotation"), (scale, 6, "scale") };
            foreach (var (values, _, what) in vectors) if (values != null) Vector(values, what);
            bool k = key ?? l.AutoKey;
            l.Scene.Batch(() =>
            {
                foreach (var (values, first, _) in vectors)
                    if (values != null)
                        for (int i = 0; i < 3; i++)
                            Keying.Set(it.Channels[first + i], l.Scene, f, values[i], k, $"{it.Name}'s {ChannelNames[first + i]}");
            });
            var result = ItemNow(c, l, it, f);
            result["set"] = k ? $"keys at frame {f:0.###}" : "values (no new keys)";
            return result;
        }, cancel);

    [McpServerTool(Name = "look_at", Destructive = true)]
    [Description("Turns an item (a camera, light, bone, or anything) so its +Z axis faces a point or another item at a frame: sets its heading and pitch from where it is then, allowing for its parent's turn, and keeps its bank. Facing an object means facing the middle of its bounds; another item, its pivot. The heading chosen is the nearest to its heading at that frame, so successive keys don't spin it round. Keys or not by the same rule as set_transform. " + KeyRule)]
    public Task<string> LookAt(
        [Description("The item to turn, by name or #index.")] string item,
        [Description("The item to face, by name or #index (or give point).")] string? target = null,
        [Description("The point to face [x, y, z], metres (or give target).")] double[]? point = null,
        [Description("The frame (default: the current frame).")] double? frame = null,
        [Description("Key (true) or just set (false); default: Layout's Auto key.")] bool? key = null,
        CancellationToken cancel = default) =>
        DoScene("aimed an item", (c, l) =>
        {
            var s = l.Scene;
            var it = Item(s, item);
            double f = SceneCensus.Frame(l, frame);
            if ((target == null) == (point == null)) throw new ArgumentException("Give target (an item) or point ([x, y, z]), one of them.");
            Vec3 at;
            if (point != null) at = Vector(point, "point");
            else
            {
                var t = Item(s, target, "target");
                if (t == it) throw new ArgumentException($"{it.Name} can't face itself.");
                at = WorldPoint(s.Evaluate(f), t);
            }
            var hpb = it.RotationToward(at, f);
            bool k = key ?? l.AutoKey;
            s.Batch(() =>
            {
                Keying.Set(it[AVAModeler.Scene.Channel.Heading], s, f, hpb.X, k, $"{it.Name}'s heading");
                Keying.Set(it[AVAModeler.Scene.Channel.Pitch], s, f, hpb.Y, k, $"{it.Name}'s pitch");
            });
            var result = ItemNow(c, l, it, f);
            result["facing"] = Census.Vec(at);
            return result;
        }, cancel);

    [McpServerTool(Name = "set_key", Destructive = true)]
    [Description("Makes or changes one key on one channel: position.x, position.y, position.z (metres), heading, pitch, bank (degrees), scale.x, scale.y, scale.z, a light's intensity or a camera's zoom, or an object's morph weight (morph.<name>, e.g. morph.Smile: 0 is the base shape, 1 the target, and values between blend; beyond 1 exaggerates) or displacement amplitude (displacement.<number>.amplitude, metres; numbered from 1 as get_scene lists them), or path.position for an item following a path (0 the start, 1 the end; set_follow_path), a light's flare.intensity while its lens flare is on (1 is 100%; set_lens_flare), or an object's dissolve (0 solid, 1 gone). The curve through keys is Kochanek–Bartels: tension (1 slows into and out of the key, -1 overshoots), continuity (sharp corners when away from 0) and bias (-1 leans the curve toward the previous key, 1 toward the next), each -1 to 1, default 0; linear makes the stretch leading into this key a straight line. span sets how the stretch leading into this key (from the key before) is drawn: tcb (the smooth curve above, the default), linear (a straight line; the same as linear true), stepped (holds the previous key's value until this frame, then jumps) or bezier (a smooth curve with explicit slopes: it leaves the previous key at that key's out_slope and arrives here at this key's in_slope). Slopes are in the channel's units per frame (metres per frame, degrees per frame…); 0 is flat. Making a key bezier without slopes fills in the slopes the curve has there now (and the previous key's out_slope), so the curve keeps its shape until you give new ones; a bezier key's out_slope also starts a smooth curve after it. A channel never keyed before has its constant replaced by its first key. A key already at that frame is replaced (keeping its tension, continuity, bias, span and slopes unless given).")]
    public Task<string> SetKey(
        [Description("The item's name, or #index from get_scene.")] string item,
        [Description("The channel, e.g. \"position.y\", \"heading\", \"morph.Smile\" or \"displacement.1.amplitude\".")] string channel,
        [Description("The frame, within the scene's frames.")] double frame,
        [Description("The value: metres, degrees, a scale factor, an intensity (1 is 100%), a zoom factor or a morph weight (0 to 1).")] double value,
        [Description("-1 to 1 (default 0, or the existing key's).")] double? tension = null,
        [Description("-1 to 1 (default 0, or the existing key's).")] double? continuity = null,
        [Description("-1 to 1 (default 0, or the existing key's).")] double? bias = null,
        [Description("Straight line from the previous key into this one.")] bool? linear = null,
        [Description("How the stretch into this key from the previous one is drawn: \"tcb\" (default for a new key), \"linear\", \"stepped\" (hold, then jump) or \"bezier\" (explicit slopes). Default: the existing key's.")] string? span = null,
        [Description("For bezier: the slope arriving at this key, in the channel's units per frame (e.g. metres per frame; 0 is flat). Default: the existing key's, or the curve's slope there when the key becomes bezier.")] double? in_slope = null,
        [Description("The slope leaving this key, in units per frame; starts the next stretch when that one is bezier, or when this key is bezier. Default: the existing key's, or the curve's slope there when the key becomes bezier.")] double? out_slope = null,
        CancellationToken cancel = default) =>
        DoScene("set a key", (c, l) =>
        {
            var s = l.Scene;
            var it = Item(s, item);
            var (name, env) = Channel(it, channel);
            double f = SceneCensus.Frame(l, frame);
            if (!double.IsFinite(value)) throw new ArgumentException("value must be a number.");
            foreach (var (v, what) in new[] { (tension, "tension"), (continuity, "continuity"), (bias, "bias") })
                if (v is { } x && (!double.IsFinite(x) || x < -1 || x > 1)) throw new ArgumentException($"{what} must be from -1 to 1.");
            if (name == "zoom" && !(value > 0)) throw new ArgumentException("A camera's zoom must be more than 0.");
            foreach (var (v, what) in new[] { (in_slope, "in_slope"), (out_slope, "out_slope") })
                if (v is { } x && !double.IsFinite(x)) throw new ArgumentException($"{what} must be a number: the slope in the channel's units per frame (0 is flat).");
            SpanKind? kind = span == null ? null : span.Trim().ToLowerInvariant() switch
            {
                "tcb" => SpanKind.Tcb,
                "linear" => SpanKind.Linear,
                "stepped" or "step" => SpanKind.Stepped,
                "bezier" or "hermite" => SpanKind.Bezier,
                _ => throw new ArgumentException($"span \"{span}\" isn't one of tcb, linear, stepped or bezier."),
            };
            if (kind is { } sk && linear is { } lin && lin != (sk == SpanKind.Linear))
                throw new ArgumentException($"linear {(lin ? "true" : "false")} contradicts span \"{span}\": give one of them.");
            int i = Keying.IsConstant(env, s) ? -1 : env.IndexOf(f);
            var old = i >= 0 ? env.Keys[i] : new EnvelopeKey(f, value);
            var key = old with { Frame = f, Value = value, Tension = tension ?? old.Tension, Continuity = continuity ?? old.Continuity, Bias = bias ?? old.Bias };
            if (linear is { } ln && kind == null) key = ln ? key.WithSpan(SpanKind.Linear) : key.Kind == SpanKind.Linear ? key.WithSpan(SpanKind.Tcb) : key;
            Keying.Key(env, s, key, keepShape: false);
            if (kind != null || in_slope != null || out_slope != null)
                Keying.SetSpan(env, env.IndexOf(f), kind ?? env.Keys[env.IndexOf(f)].Kind, in_slope, out_slope);
            s.Notify();
            var result = Keys(name, env, s);
            result["item"] = it.Name;
            return result;
        }, cancel);

    [McpServerTool(Name = "delete_key", Destructive = true)]
    [Description("Removes an item's key at a frame: on one channel, or on every channel that has one there (Layout's Delete Key). A channel's last key stays, as it holds the channel's value.")]
    public Task<string> DeleteKey(
        [Description("The item's name, or #index from get_scene.")] string item,
        [Description("The frame of the key.")] double frame,
        [Description("Just this channel (default: all the item's channels).")] string? channel = null,
        CancellationToken cancel = default) =>
        DoScene("deleted a key", (c, l) =>
        {
            var s = l.Scene;
            var it = Item(s, item);
            var channels = channel == null ? Channels(it).ToList() : [Channel(it, channel)];
            var removed = new List<string>();
            var kept = new List<string>();
            foreach (var (name, env) in channels)
            {
                if (env.IndexOf(frame) < 0) continue;
                if (env.Keys.Count == 1) kept.Add(name);
                else
                {
                    env.RemoveKeyAt(frame);
                    removed.Add(name);
                }
            }
            if (removed.Count == 0 && kept.Count == 0)
                throw new ArgumentException($"{it.Name} has no key at frame {frame:0.###}{(channel != null ? $" on {channels[0].Name}" : "")}. Its keys are at frames {string.Join(", ", SceneCensus.KeyFrames(it).Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)))}.");
            var result = new JsonObject { ["item"] = it.Name, ["frame"] = Census.Round(frame), ["removed"] = Strings(removed) };
            if (kept.Count > 0) result["kept"] = $"{string.Join(", ", kept)}: the channel's only key, which holds its value; change it with set_key instead.";
            return result;
        }, cancel);

    [McpServerTool(Name = "set_end_behavior", Destructive = true)]
    [Description("Sets what an animated channel does before its first key (pre) and after its last (post): constant (holds the end key's value; the default), reset (0), repeat (loops the keyed span), oscillate (plays it forwards, then backwards), offset_repeat (repeats, each time shifted by the change over the span: good for steady spins) or linear (carries on at the end key's slope). On one channel, or all of the item's channels.")]
    public Task<string> SetEndBehavior(
        [Description("The item's name, or #index from get_scene.")] string item,
        [Description("The channel, e.g. \"heading\" (default: all the item's channels).")] string? channel = null,
        [Description("Before the first key: constant, reset, repeat, oscillate, offset_repeat or linear.")] string? pre = null,
        [Description("After the last key: the same choices.")] string? post = null,
        CancellationToken cancel = default) =>
        DoScene("set end behaviours", (c, l) =>
        {
            var it = Item(l.Scene, item);
            if (pre == null && post == null) throw new ArgumentException("Give pre, post or both.");
            var p = pre == null ? (EndBehavior?)null : Behavior(pre, "pre");
            var q = post == null ? (EndBehavior?)null : Behavior(post, "post");
            var channels = channel == null ? Channels(it).ToList() : [Channel(it, channel)];
            l.Scene.Batch(() =>
            {
                foreach (var (_, env) in channels)
                {
                    if (p is { } a) env.PreBehavior = a;
                    if (q is { } b) env.PostBehavior = b;
                }
            });
            return new JsonObject
            {
                ["item"] = it.Name,
                ["channels"] = Strings(channels.Select(x => x.Name)),
                ["pre"] = p is { } pa ? SceneCensus.Behavior(pa) : null,
                ["post"] = q is { } qa ? SceneCensus.Behavior(qa) : null,
                ["note"] = channels.Any(x => !Keying.IsAnimated(x.Envelope)) ? "Behaviours only show on channels with two keys or more." : null,
            };
        }, cancel);

    [McpServerTool(Name = "set_follow_path", Destructive = true)]
    [Description("Makes an item follow a path: a smooth curve (a centripetal Catmull–Rom spline, no overshoot) through other items (usually nulls: add_null at each point; animate them and the path changes), in the order given, open or closed into a loop. Where along it the item is comes from its keyable channel path.position: 0 is the start (the first point), 1 the end (back at the first point on a closed path, whose positions wrap: 2 is twice round), evenly by length. from_frame and to_frame key it for you: 0 at from_frame to laps (default 1) at to_frame, with linear keys, so it moves at an even speed; or key path.position yourself with set_key for speeding up and slowing down. With align_to_path (default true) its heading and pitch turn its +Z axis along the path (model the item facing +Z), looking look_ahead metres ahead (0: along the tangent); banking leans it into turns (degrees of bank on a 1 m radius turn, proportionally less on wider ones: try 30 to 60 for a plane, 0 for a car). While it follows, its position keys (and, aligned, its heading and pitch) are ignored; its bank key adds to the banking. Give points [] to stop following. Layout's views draw the path. get_scene shows follow_path with the path's length, the item's place and turn. bake_path turns it into keys.")]
    public Task<string> SetFollowPath(
        [Description("The item that follows, by name or #index (not a bone).")] string item,
        [Description("The items the path runs through, in order, by name (at least 2; 3 for a closed path); [] to stop following. Leave out to keep them.")] string[]? points = null,
        [Description("Close the path into a loop (default: as it was, false for a new path).")] bool? closed = null,
        [Description("Turn the item to face along the path (default: as it was, true for a new path).")] bool? align_to_path = null,
        [Description("Metres ahead along the path an aligned item looks, 0 or more (0: the tangent).")] double? look_ahead = null,
        [Description("Degrees of bank on a 1 m radius turn (bank = banking ÷ radius, at most 90°, into the turn); 0 for none.")] double? banking = null,
        [Description("With to_frame: key path.position 0 here (linear keys, an even speed).")] double? from_frame = null,
        [Description("With from_frame: key path.position laps here.")] double? to_frame = null,
        [Description("With from_frame and to_frame: how far along it gets by to_frame, 1 = once to the end (default 1; a closed path can go round several times).")] double laps = 1,
        CancellationToken cancel = default) =>
        DoScene("set an item's path", (c, l) =>
        {
            var s = l.Scene;
            var it = Item(s, item);
            if (it is Bone) throw new ArgumentException($"{it.Name} is a bone: bones can't follow paths (give the object, or a null the bone's chain reaches for with set_ik, the path instead).");
            if (points == null && closed == null && align_to_path == null && look_ahead == null && banking == null && from_frame == null && to_frame == null)
                throw new ArgumentException("Give points (to start following), or closed, align_to_path, look_ahead, banking or from_frame and to_frame to change a path.");
            if (points is { Length: 0 })
            {
                it.Path = null;
                s.Notify();
                var stopped = ItemNow(c, l, it, l.Frame);
                stopped["note"] = "It no longer follows a path; it moves by its own keys again.";
                return stopped;
            }
            var path = it.Path ?? new FollowPath();
            if (points == null && it.Path == null) throw new ArgumentException($"{it.Name} doesn't follow a path yet: give points, the items it runs through.");
            if (points != null)
            {
                var chosen = points.Select(p => Item(s, p, "point")).ToList();
                if (FollowPath.Problem(it, chosen) is { } problem) throw new ArgumentException(problem);
                path = path with { Points = chosen };
            }
            if (closed is { } cl) path = path with { Closed = cl };
            if (align_to_path is { } al) path = path with { AlignToPath = al };
            if (look_ahead is { } la) path = path with { LookAhead = la };
            if (banking is { } bk) path = path with { Banking = bk };
            it.Path = path.Checked();
            if ((from_frame == null) != (to_frame == null)) throw new ArgumentException("Give from_frame and to_frame together (or neither, and key path.position with set_key).");
            if (from_frame is { } ff && to_frame is { } tf) l.TimePath(it, SceneCensus.Frame(l, ff), SceneCensus.Frame(l, tf), laps);
            s.Notify();
            return ItemNow(c, l, it, l.Frame);
        }, cancel);

    [McpServerTool(Name = "bake_path", Destructive = true)]
    [Description("Bakes an item's path following into keys, as one undo step: its position (and, when aligned to the path, heading, pitch and bank) keyed every step frames from first to last, as the path moves it, then it stops following, so the keys play. For programs without path following (LightWave scenes, glTF), or to edit the motion key by key.")]
    public Task<string> BakePath(
        [Description("The item following a path, by name or #index.")] string item,
        [Description("The first frame (default: the scene's first).")] double? first = null,
        [Description("The last frame (default: the scene's last).")] double? last = null,
        [Description("Frames between keys, 1 or more (default 1: every frame).")] double step = 1,
        CancellationToken cancel = default) =>
        DoScene("baked a path into keys", (c, l) =>
        {
            var s = l.Scene;
            var it = Item(s, item);
            if (it.Path == null) throw new ArgumentException($"{it.Name} doesn't follow a path. set_follow_path gives it one.");
            double f0 = SceneCensus.Frame(l, first ?? s.FirstFrame), f1 = SceneCensus.Frame(l, last ?? s.LastFrame);
            if (!(step >= 1) || !double.IsFinite(step)) throw new ArgumentException("step must be 1 frame or more.");
            if (f1 < f0) throw new ArgumentException($"last ({f1:0.###}) is before first ({f0:0.###}).");
            var frames = l.BakePath(it, f0, f1, step);
            var result = ItemNow(c, l, it, l.Frame);
            result["keyed_frames"] = frames.Count;
            return result;
        }, cancel);

    [McpServerTool(Name = "shift_keys", Destructive = true)]
    [Description("Moves keys in time, as dragging them in Layout's dope sheet does: every key between from and to (on one item or all, one channel or all) shifts by offset whole frames (negative is earlier), keeping its value and its tension, continuity, bias and linear flag. A key landing where an unmoved one is replaces it. Channels never keyed (holding a constant) aren't touched. One undo step. Use it to start an animation later, or to slide one item's motion against another's.")]
    public Task<string> ShiftKeys(
        [Description("Whole frames to move the keys by; negative moves them earlier.")] double offset,
        [Description("The item, by name or #index (default: every item).")] string? item = null,
        [Description("Just this channel, e.g. \"heading\" or \"morph.Smile\" (default: all the item's channels; needs item).")] string? channel = null,
        [Description("Only keys at this frame or later (default: all).")] double? from = null,
        [Description("Only keys at this frame or earlier (default: all).")] double? to = null,
        CancellationToken cancel = default) =>
        DoScene("moved keys in time", (c, l) =>
        {
            var keys = KeysToRetime(l, item, channel, from, to);
            if (!double.IsFinite(offset) || Math.Abs(offset - Math.Round(offset)) > 1e-9) throw new ArgumentException("offset is a whole number of frames, like 10 or -5.");
            var (moved, replaced) = KeyTiming.Shift(l, keys, offset, "Shift Keys");
            return Retimed(l, keys, moved, replaced);
        }, cancel);

    [McpServerTool(Name = "scale_keys", Destructive = true)]
    [Description("Scales keys' timing, as the dope sheet's Scale does: every key between from and to (on one item or all, one channel or all) moves to pivot + (frame - pivot) × factor, snapped to a whole frame, so factor 2 makes the motion take twice as long and 0.5 half as long. Values, tension, continuity, bias and linear flags are kept; where two keys end up on one frame the later one wins, and a key landing on an unmoved one replaces it. One undo step. To retime a whole animation, scale its keys about the first frame, then set_scene last_frame to fit.")]
    public Task<string> ScaleKeys(
        [Description("How much longer the keyed motion takes: more than 0 (2 doubles it, 0.5 halves it).")] double factor,
        [Description("The frame the keys scale about, which stays put (default: the earliest key being scaled).")] double? pivot = null,
        [Description("The item, by name or #index (default: every item).")] string? item = null,
        [Description("Just this channel (default: all the item's channels; needs item).")] string? channel = null,
        [Description("Only keys at this frame or later (default: all).")] double? from = null,
        [Description("Only keys at this frame or earlier (default: all).")] double? to = null,
        CancellationToken cancel = default) =>
        DoScene("scaled keys in time", (c, l) =>
        {
            var keys = KeysToRetime(l, item, channel, from, to);
            if (!(factor > 0) || !double.IsFinite(factor)) throw new ArgumentException("factor must be more than 0 (2 makes the keyed motion twice as long, 0.5 half as long).");
            var (moved, replaced) = KeyTiming.Scale(l, keys, factor, pivot ?? keys.Min(k => k.Frame), "Scale Keys");
            return Retimed(l, keys, moved, replaced);
        }, cancel);

    /// <summary>The keys shift_keys and scale_keys act on: each keyed channel's keys between two frames, on one item or every one.</summary>
    private static List<KeyId> KeysToRetime(LayoutSession l, string? item, string? channel, double? from, double? to)
    {
        var s = l.Scene;
        if (channel != null && item == null) throw new ArgumentException("Give item with channel: the channel is one of that item's.");
        foreach (var (v, what) in new[] { (from, "from"), (to, "to") })
            if (v is { } x && !double.IsFinite(x)) throw new ArgumentException($"{what} must be a frame.");
        if (from > to) throw new ArgumentException($"from ({from:0.###}) is after to ({to:0.###}).");
        var items = item == null ? s.Items.ToList() : [Item(s, item)];
        string? name = channel == null ? null : Channel(items[0], channel).Name;
        var keys = new List<KeyId>();
        foreach (var it in items)
            foreach (var (n, env) in SceneChannels.Keyed(it, s))
            {
                if (name != null && n != name) continue;
                foreach (var k in env.Keys)
                    if ((from == null || k.Frame >= from - 1e-9) && (to == null || k.Frame <= to + 1e-9)) keys.Add(new KeyId(it, n, k.Frame));
            }
        if (keys.Count == 0)
            throw new ArgumentException($"There are no keys {(item == null ? "in the scene" : $"on {items[0].Name}{(name != null ? $"'s {name}" : "")}")}{(from != null || to != null ? $" between frames {from ?? s.FirstFrame:0.###} and {to ?? s.LastFrame:0.###}" : "")} to move. get_keys lists them.");
        return keys;
    }

    private static JsonObject Retimed(LayoutSession l, List<KeyId> keys, int moved, int replaced)
    {
        var items = keys.Select(k => k.Item).Distinct().ToList();
        return new JsonObject
        {
            ["moved"] = moved,
            ["replaced"] = replaced,
            ["items"] = new JsonArray(items.Select(it => (JsonNode)new JsonObject
            {
                ["item"] = it.Name,
                ["frames_with_keys"] = new JsonArray(SceneChannels.Keyed(it, l.Scene).SelectMany(ch => ch.Envelope.Keys.Select(k => k.Frame)).Distinct().Order().Select(f => (JsonNode)Census.Round(f)).ToArray()),
            }).ToArray()),
            ["note"] = moved == 0 ? "Nothing moved: the keys were already there." : null,
        };
    }

    [McpServerTool(Name = "set_scene", Destructive = true)]
    [Description("Changes the scene's settings, leaving out what you don't give: the frame range (first_frame, last_frame, whole frames; 1 to 60 by default) and frames per second; ambient light (colour 0 to 255, intensity as a fraction, 0.25 by default); the backdrop: a colour, or a gradient from gradient_bottom (straight down) to gradient_top (straight up); fog (linear: none up to fog_min metres from the camera, full at fog_max); the current camera (the one render_scene uses); and the current frame (where Layout's timeline stands, and the default for the other tools). Render effects, all off by default: ambient occlusion (ao: darkens creases, corners and contact areas by dimming the ambient light where nearby surfaces hide it; raise ambient_intensity to see it) and an environment picture round the scene (environment: a lat-long .hdr, .png or .jpg file, or builtin:sky / builtin:studio; it replaces the backdrop and shows in reflections, and with environment_lighting it lights the scene too, with soft shadows). Sample counts are rays a pixel, shared among the pixel's antialiasing rays: more is smoother and slower.")]
    public Task<string> SetScene(
        [Description("The first frame, 0 or more.")] int? first_frame = null,
        [Description("The last frame, not before the first.")] int? last_frame = null,
        [Description("Frames per second, more than 0 (default 30).")] double? fps = null,
        [Description("[r, g, b], each 0 to 255.")] double[]? ambient_color = null,
        [Description("0 or more (default 0.25).")] double? ambient_intensity = null,
        [Description("The backdrop colour [r, g, b], 0 to 255 (used when gradient is false).")] double[]? background_color = null,
        [Description("Use the gradient backdrop instead of the colour.")] bool? gradient = null,
        [Description("The gradient's colour straight up [r, g, b], 0 to 255.")] double[]? gradient_top = null,
        [Description("The gradient's colour straight down [r, g, b], 0 to 255.")] double[]? gradient_bottom = null,
        [Description("off or linear.")] string? fog = null,
        [Description("Where fog starts, metres from the camera.")] double? fog_min = null,
        [Description("Where fog is full, metres from the camera.")] double? fog_max = null,
        [Description("The fog's colour [r, g, b], 0 to 255.")] double[]? fog_color = null,
        [Description("The camera to render through, by name.")] string? camera = null,
        [Description("The current frame.")] double? frame = null,
        [Description("Ambient occlusion on or off (default off).")] bool? ao = null,
        [Description("Ambient occlusion rays a pixel, 1 to 256 (default 16).")] int? ao_samples = null,
        [Description("How far, in metres, a surface still shades a point (default 1; about the size of the gaps to darken).")] double? ao_distance = null,
        [Description("How much of the hidden ambient light is taken away, 0 to 1 (default 1).")] double? ao_strength = null,
        [Description("The environment picture: a latitude-longitude (equirectangular) image file relative to the working folder (.hdr Radiance for high dynamic range, or .png/.jpg/.bmp), or builtin:sky or builtin:studio (made up, no file needed); \"\" for none (the default). Its middle looks along +Z, straight up is its top edge.")] string? environment = null,
        [Description("The environment's brightness, 0 or more (default 1).")] double? environment_intensity = null,
        [Description("Degrees the environment is turned about Y (default 0; positive turns what was ahead toward +X).")] double? environment_heading = null,
        [Description("Whether the environment lights the scene too, as soft light from every direction with shadows (default off: only seen).")] bool? environment_lighting = null,
        [Description("Environment lighting rays a pixel, 1 to 256 (default 16).")] int? environment_samples = null,
        CancellationToken cancel = default) =>
        DoScene("changed the scene's settings", (c, l) =>
        {
            var s = l.Scene;
            int first = first_frame ?? s.FirstFrame, last = last_frame ?? s.LastFrame;
            if (first < 0) throw new ArgumentException("first_frame must be 0 or more.");
            if (last < first) throw new ArgumentException($"last_frame ({last}) can't be before first_frame ({first}).");
            if (fps is { } r && (!(r > 0) || r > 1000)) throw new ArgumentException("fps must be more than 0 (and at most 1000).");
            var ambientColor = ambient_color == null ? (Vec3?)null : Colour(ambient_color, "ambient_color");
            if (ambient_intensity is { } ai && (!double.IsFinite(ai) || ai < 0)) throw new ArgumentException("ambient_intensity must be 0 or more.");
            var back = background_color == null ? (Vec3?)null : Colour(background_color, "background_color");
            var top = gradient_top == null ? (Vec3?)null : Colour(gradient_top, "gradient_top");
            var bottom = gradient_bottom == null ? (Vec3?)null : Colour(gradient_bottom, "gradient_bottom");
            var fogType = fog == null ? (FogType?)null : Json.Choice<FogType>(fog, "fog");
            var fogColour = fog_color == null ? (Vec3?)null : Colour(fog_color, "fog_color");
            double fogMin = fog_min ?? s.FogMinDistance, fogMax = fog_max ?? s.FogMaxDistance;
            if (!double.IsFinite(fogMin) || !double.IsFinite(fogMax) || fogMin < 0 || fogMax <= fogMin) throw new ArgumentException("fog_min must be 0 or more and fog_max more than fog_min (metres).");
            CameraItem? cam = null;
            if (camera != null && (cam = Item(s, camera, "camera") as CameraItem) == null) throw new ArgumentException($"{camera} isn't a camera.");
            if (frame is { } fr && (!double.IsFinite(fr) || fr < first || fr > last)) throw new ArgumentException($"frame must be within the frames, {first} to {last}.");
            var occlusion = s.Occlusion;
            if (ao is { } aoOn) occlusion = occlusion with { On = aoOn };
            if (ao_samples is { } aos) occlusion = occlusion with { Samples = aos };
            if (ao_distance is { } aod) occlusion = occlusion with { Distance = aod };
            if (ao_strength is { } aost) occlusion = occlusion with { Strength = aost };
            occlusion.Checked();
            var env = s.Environment;
            if (environment != null) env = env with { Image = EnvironmentImage(environment) };
            if (environment_intensity is { } ei) env = env with { Intensity = ei };
            if (environment_heading is { } eh) env = env with { Heading = eh };
            if (environment_lighting is { } el) env = env with { Lighting = el };
            if (environment_samples is { } es) env = env with { Samples = es };
            env.Checked();
            if (env.Lighting && !env.On) throw new ArgumentException("environment_lighting needs an environment: give environment too (a file, or builtin:sky, builtin:studio or builtin:sun).");
            bool settings = first_frame != null || last_frame != null || fps != null || ambient_color != null || ambient_intensity != null || background_color != null
                || gradient != null || gradient_top != null || gradient_bottom != null || fog != null || fog_min != null || fog_max != null || fog_color != null || camera != null
                || occlusion != s.Occlusion || env != s.Environment;
            // Only the frame: that isn't a change to the scene (nor an undo step).
            if (settings) s.Batch(() =>
            {
                (s.FirstFrame, s.LastFrame) = (first, last);
                if (fps is { } rate) s.FramesPerSecond = rate;
                if (ambientColor is { } ac) s.AmbientColor = ac;
                if (ambient_intensity is { } ai2) s.AmbientIntensity = ai2;
                if (back is { } bc) s.BackgroundColor = bc;
                if (gradient is { } g) s.GradientBackground = g;
                if (top is { } t) s.GradientTop = t;
                if (bottom is { } b) s.GradientBottom = b;
                if (fogType is { } ft) s.Fog = ft;
                (s.FogMinDistance, s.FogMaxDistance) = (fogMin, fogMax);
                if (fogColour is { } fc) s.FogColor = fc;
                if (cam != null) s.CurrentCamera = cam;
                (s.Occlusion, s.Environment) = (occlusion, env);
                s.Notify();
            });
            l.SetFrame(frame ?? l.Frame);
            var result = SceneCensus.Scene(l, c.Document, l.Frame);
            result.Remove("items");
            return result;
        }, cancel);

    [McpServerTool(Name = "undo_scene", Destructive = true)]
    [Description("Undoes the last change to the scene, as Ctrl+Z does in Layout: yours or, in the live app, the person's (they share one history, up to 100 steps; each of your scene calls is one step, named \"Agent: …\"). Returns what was undone. It doesn't touch the object (that's undo) or the current frame, and can't go back past new_scene or open_scene, which clear the history.")]
    public Task<string> UndoScene(CancellationToken cancel = default) =>
        DoScene("undid a scene change", (c, l) =>
        {
            string? label = l.Undo();
            var result = SceneCensus.Scene(l, c.Document, l.Frame);
            result["undone"] = label;
            if (label == null) result["note"] = "There was nothing to undo in the scene.";
            return result;
        }, cancel, changes: false);

    [McpServerTool(Name = "redo_scene", Destructive = true)]
    [Description("Redoes the last scene change undone (by undo_scene or the person's Ctrl+Z), as Ctrl+Shift+Z does in Layout. Returns what was redone. A new change to the scene clears what can be redone.")]
    public Task<string> RedoScene(CancellationToken cancel = default) =>
        DoScene("redid a scene change", (c, l) =>
        {
            string? label = l.Redo();
            var result = SceneCensus.Scene(l, c.Document, l.Frame);
            result["redone"] = label;
            if (label == null) result["note"] = "There was nothing to redo in the scene.";
            return result;
        }, cancel, changes: false);

    // ───────────────────────────── seeing ─────────────────────────────

    [McpServerTool(Name = "render_scene", ReadOnly = true, Idempotent = true)]
    [Description("Ray-traces a frame of the scene through a camera as a PNG, as Layout's Render Frame does: every surface setting and texture, the scene's lights with ray-traced shadows (soft from area lights and lights with a radius), ambient light (with ambient occlusion if set_scene turned it on), backdrop or environment (and its lighting) and fog, reflections and transparency, and the camera's depth of field and motion blur if set_item turned them on (they raise the rays a pixel to their samples). The size defaults to the camera's picture shape at up to 640 across. Slower than render_scene_view: start small and with few samples, then go up.")]
    public async Task<CallToolResult> RenderScene(
        [Description("The frame (default: the current frame).")] double? frame = null,
        [Description("Width in pixels, 16 to 1920 (default: the camera's, at most 640).")] int? width = null,
        [Description("Height in pixels, 16 to 1920 (default: to keep the camera's shape).")] int? height = null,
        [Description("The camera, by name (default: the current camera).")] string? camera = null,
        [Description("Rays per pixel for antialiasing, 1 to 16 (default 4).")] int samples = 4,
        [Description("Ray-traced shadows (default true).")] bool shadows = true,
        [Description("Mirror reflections (default true).")] bool reflections = true,
        [Description("Transparency and refraction (default true).")] bool transparency = true,
        [Description("Smooth away the noise of few rays (soft shadows, area lights, ambient occlusion), guided by the frame's depth, normals, objects and colours, so edges and textures stay sharp (default false).")] bool denoise = false,
        CancellationToken cancel = default)
    {
        if (samples < 1 || samples > MaxSamples) throw new McpException($"samples must be from 1 to {MaxSamples}.");
        var (copy, f, cam, w, h, problems) = await DoScene("rendered a frame", (c, l) =>
        {
            double fr = SceneCensus.Frame(l, frame);
            var cm = RenderCamera(l.Scene, camera);
            var (pw, ph) = PictureSize(cm, width, height, 640, MaxRenderSize);
            var copy = ScenePictures.Copy(l.Scene);
            return (copy, fr, (CameraItem)copy.Items[IndexOf(l.Scene, cm)], pw, ph, l.ImageProblems());
        }, cancel, changes: false).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var image = await Rendering(t => ScenePictures.Render(copy, f, cam, w, h, samples, shadows, reflections, transparency, t, denoise), RenderTimeLimit,
            "The render took longer than {0} and was stopped: try a smaller size or fewer samples.", cancel).ConfigureAwait(false);
        int rays = RayTracer.RaysPerPixel(new RenderSettings { SamplesPerPixel = samples }, cam);
        string about = FormattableString.Invariant($"Frame {f:0.###} through {cam.Name}, {w}×{h}, {rays} ray{(rays == 1 ? "" : "s")} a pixel{(rays != samples ? " (raised by depth of field or motion blur)" : "")}, in {clock.Elapsed.TotalSeconds:0.0} s.");
        if (problems.Count > 0) about += " " + string.Join(" ", problems);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(image.ToPng(alpha: false), "image/png"), new TextContentBlock { Text = about }] };
    }

    [McpServerTool(Name = "render_scene_view", ReadOnly = true, Idempotent = true)]
    [Description("Draws the scene quickly as Layout's view does (software, not ray-traced), as a PNG: objects shaded or in wireframe, and every light (a cross, with an arrow the way it shines), camera (a box with a lens), null (a cross) and bone (a diamond) as a little drawing. view camera looks through the current camera (its heading, pitch and bank, so a banked camera's picture tilts as render_scene's does), framed as render_scene frames it; perspective orbits the scene from heading and pitch; top looks down -Y, front along +Z, side along -X, each framing everything. Also lists where each item landed in the picture. Use it to check placement and aim before rendering.")]
    public async Task<CallToolResult> RenderSceneView(
        [Description("camera (default), perspective, top, front or side.")] string view = "camera",
        [Description("The frame (default: the current frame).")] double? frame = null,
        [Description("Width in pixels, 16 to 2048 (default 640).")] int width = 640,
        [Description("Height in pixels, 16 to 2048 (default: the camera's shape for view camera, else 480).")] int? height = null,
        [Description("Camera and perspective only: shade the objects (default true); false draws wireframe. Top, front and side are always wireframe, as in Layout.")] bool shaded = true,
        [Description("Perspective only: degrees round the Y axis (default 35).")] double heading = 35,
        [Description("Perspective only: degrees looking down (default 25).")] double pitch = 25,
        CancellationToken cancel = default)
    {
        var kind = Json.Choice<SceneViewKind>(view, "view");
        var (copy, f, h) = await DoScene("looked at the scene", (c, l) =>
        {
            double fr = SceneCensus.Frame(l, frame);
            int ph = height ?? (kind == SceneViewKind.Camera && l.Scene.CurrentCamera is { } cm ? (int)Math.Round(width / cm.FrameAspect) : 480);
            Json.Size(width, ph, 2048);
            return (ScenePictures.Copy(l.Scene), fr, ph);
        }, cancel, changes: false).ConfigureAwait(false);
        var (png, marks) = await Task.Run(() => ScenePictures.View(copy, f, kind, width, h, shaded, heading, pitch), cancel).ConfigureAwait(false);
        string where = marks.Count == 0 ? "No items are in sight."
            : "Items in the picture (pixels from the top left): " + string.Join("; ", marks.Select(m => FormattableString.Invariant($"{m.Kind} {m.Name} at ({m.X:0}, {m.Y:0})"))) + ".";
        string about = FormattableString.Invariant($"{kind} view of frame {f:0.###}{(kind == SceneViewKind.Camera ? $" through {copy.CurrentCamera?.Name}" : "")}, {width}×{h}, {(shaded && kind is SceneViewKind.Camera or SceneViewKind.Perspective ? "shaded" : "wireframe")}. {where}");
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = about }] };
    }

    [McpServerTool(Name = "render_animation", Destructive = true)]
    [Description("Ray-traces a run of frames into numbered PNG files in a folder inside the working folder (e.g. frames/frame0001.png, numbered by frame), and returns a contact sheet of them (up to eight a row) and the list of files. With movie (mp4, gif or both) it then joins this run's frames, in order, into frames/frame.mp4 (H.264) and/or a looping frames/frame.gif (at most 640 across), playing at the scene's frames per second divided by step, so it runs at the scene's speed; that needs ffmpeg installed (without it the frames are still saved and the reply says so). Bounded so it can't run away: at most 120 frames, 960 pixels each way and 4 samples a pixel, and width × height × rays a pixel × frames at most 20 million (the camera's depth of field and motion blur, which render_animation honours, count their samples as the rays a pixel); it stops after 5 minutes. For a longer movie, render the frames in parts (first/last) into one folder, then join them all with make_movie. An existing file is only replaced when overwrite is true.")]
    public async Task<CallToolResult> RenderAnimation(
        [Description("The folder for the files, relative to the working folder (default \"frames\"); made if needed.")] string folder = "frames",
        [Description("The files' names before the frame number (default \"frame\").")] string name = "frame",
        [Description("The first frame (default: the scene's first).")] int? first = null,
        [Description("The last frame (default: the scene's last).")] int? last = null,
        [Description("Render every step-th frame (default 1).")] int step = 1,
        [Description("Width in pixels, 16 to 960 (default: the camera's shape at 320 across).")] int? width = null,
        [Description("Height in pixels, 16 to 960 (default: to keep the camera's shape).")] int? height = null,
        [Description("Rays per pixel, 1 to 4 (default 1).")] int samples = 1,
        [Description("The camera, by name (default: the current camera).")] string? camera = null,
        [Description("Replace files that already exist.")] bool overwrite = false,
        [Description("Also make a movie of the frames: mp4, gif or both (default: none). Needs ffmpeg.")] string? movie = null,
        [Description("Smooth away the noise of few rays, guided by each frame's depth, normals, objects and colours (default false).")] bool denoise = false,
        [Description("Passes to save beside each frame as <name>_<pass><n>.png: depth (white near), normal, object_id (flat colours), albedo, emission, shadow (white: in shadow), reflection.")] string[]? passes = null,
        CancellationToken cancel = default)
    {
        var formats = MovieFormats(movie);
        var wanted = RenderPass.None;
        foreach (var p in passes ?? [])
            wanted |= PassImages.Names.TryGetValue((p ?? "").Trim(), out var pass) ? pass
                : throw new McpException($"There's no \"{p}\" pass: they are {string.Join(", ", PassImages.Names.Keys)}.");
        if (samples < 1 || samples > MaxAnimationSamples) throw new McpException($"samples must be from 1 to {MaxAnimationSamples}.");
        if (step < 1) throw new McpException("step must be 1 or more.");
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new McpException("name must be a plain file name, such as \"frame\".");
        var job = await DoScene("rendered an animation", (c, l) =>
        {
            var s = l.Scene;
            int a = first ?? s.FirstFrame, b = last ?? s.LastFrame;
            SceneCensus.Frame(l, a);
            SceneCensus.Frame(l, b);
            if (b < a) throw new ArgumentException($"last ({b}) can't be before first ({a}).");
            var frames = Enumerable.Range(0, (b - a) / step + 1).Select(i => a + i * step).ToList();
            if (frames.Count > MaxAnimationFrames) throw new ArgumentException($"That's {frames.Count} frames; render_animation does at most {MaxAnimationFrames} at a time. Use a bigger step, or render it in parts.");
            var cm = RenderCamera(s, camera);
            var (pw, ph) = PictureSize(cm, width, height, 320, MaxAnimationSize);
            int rays = RayTracer.RaysPerPixel(new RenderSettings { SamplesPerPixel = samples }, cm);
            long work = (long)pw * ph * rays * frames.Count;
            if (work > MaxAnimationWork)
                throw new ArgumentException(FormattableString.Invariant($"That's {work / 1e6:0.#} million rays (width × height × rays a pixel × frames, with {rays} rays a pixel{(rays != samples ? " from the camera's depth of field or motion blur samples" : "")}); at most {MaxAnimationWork / 1e6:0} million at a time. Render smaller, with fewer samples (dof_samples, blur_samples) or frames."));
            var files = Files(c);
            string dir = Path.GetDirectoryName(files.Resolve(Path.Combine(folder, "frame.png")))!;
            var paths = frames.Select(fr => Path.Combine(dir, FormattableString.Invariant($"{name}{fr:0000}.png"))).ToList();
            var passPaths = PassImages.Names.Where(kv => wanted.HasFlag(kv.Value))
                .SelectMany(kv => frames.Select(fr => Path.Combine(dir, FormattableString.Invariant($"{name}_{kv.Key}{fr:0000}.png")))).ToList();
            var movies = Movies.Available ? formats.Select(f => MovieEncoder.MoviePath(dir, name, f)).ToList() : [];
            files.CheckWritable(paths.Concat(passPaths).Concat(movies), overwrite);
            var copy = ScenePictures.Copy(s);
            return (copy, frames, cam: (CameraItem)copy.Items[IndexOf(s, cm)], pw, ph, paths, files, movies, fps: s.FramesPerSecond / step);
        }, cancel, changes: false).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var images = await Rendering(t =>
        {
            var list = new List<RenderImage>();
            for (int i = 0; i < job.frames.Count; i++)
            {
                var img = ScenePictures.Render(job.copy, job.frames[i], job.cam, job.pw, job.ph, samples, true, true, true, t, denoise);
                File.WriteAllBytes(job.paths[i], img.ToPng(alpha: false));
                if (wanted != RenderPass.None)
                {
                    var p = ScenePictures.Passes(job.copy, job.frames[i], job.cam, job.pw, job.ph, wanted, t);
                    string dir = Path.GetDirectoryName(job.paths[i])!;
                    foreach (var (passName, pass) in PassImages.Names.Where(kv => wanted.HasFlag(kv.Value)))
                    {
                        string file = Path.Combine(dir, FormattableString.Invariant($"{name}_{passName}{job.frames[i]:0000}.png"));
                        File.WriteAllBytes(file, p.ToImage(pass).ToPng(alpha: pass == RenderPass.Depth));
                        job.files.Wrote(file);
                    }
                }
                job.files.Wrote(job.paths[i]);
                list.Add(img);
            }
            return list;
        }, AnimationTimeLimit, "The animation took longer than {0} and was stopped (the frames done so far are saved): render fewer frames, smaller, or with fewer samples.", cancel).ConfigureAwait(false);
        var sheet = ScenePictures.ContactSheet(images, Math.Min(160, job.pw));
        var info = new JsonObject
        {
            ["frames"] = new JsonArray(job.frames.Select(x => (JsonNode)x).ToArray()),
            ["files"] = Strings(job.paths),
            ["camera"] = job.cam.Name,
            ["size"] = new JsonArray(job.pw, job.ph),
            ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
            ["contact_sheet"] = "The picture: the frames in order, left to right and top to bottom, up to eight a row.",
        };
        if (formats.Count > 0)
        {
            var (made, problem) = await MakeMovies(job.paths, job.movies, formats, job.fps, job.pw, job.ph, job.files, RenderTimeLimit, cancel, Sound(job.copy, job.frames[0])).ConfigureAwait(false);
            info["movies"] = Strings(made);
            info["movie_fps"] = Census.Round(job.fps, 3);
            if (problem != null) info["movie_problem"] = problem;
        }
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(sheet, "image/png"), new TextContentBlock { Text = info.ToJsonString() }] };
    }

    [McpServerTool(Name = "make_movie", Destructive = true)]
    [Description("Joins numbered PNG frames already in a folder inside the working folder (as render_animation writes them: frames/frame0001.png, numbered by frame) into an MP4 (H.264) and/or a looping GIF (at most 640 across), saved beside them as name.mp4 and name.gif. It renders nothing, so it has no frame limit: render a long animation in parts with render_animation (each call within its limit, into the same folder), then call this once to join every frame from first to last. Every frame in the range must be there, all the same size. It plays at the scene's frames per second divided by step unless fps is given. Needs ffmpeg installed. An existing movie is only replaced when overwrite is true.")]
    public async Task<CallToolResult> MakeMovie(
        [Description("The folder the frames are in, relative to the working folder (default \"frames\").")] string folder = "frames",
        [Description("The frames' names before the frame number (default \"frame\"); the movies are named after it too.")] string name = "frame",
        [Description("The first frame (default: the scene's first).")] int? first = null,
        [Description("The last frame (default: the scene's last).")] int? last = null,
        [Description("Use every step-th frame (default 1), as rendered with the same step.")] int step = 1,
        [Description("mp4, gif or both (default mp4).")] string movie = "mp4",
        [Description("Frames per second to play at (default: the scene's, divided by step).")] double? fps = null,
        [Description("Replace movies that already exist.")] bool overwrite = false,
        CancellationToken cancel = default)
    {
        var formats = MovieFormats(movie);
        if (formats.Count == 0) throw new McpException("movie must be mp4, gif or both.");
        if (step < 1) throw new McpException("step must be 1 or more.");
        if (fps is { } given && (!double.IsFinite(given) || given <= 0 || given > 240)) throw new McpException("fps must be more than 0 and at most 240.");
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new McpException("name must be a plain file name, such as \"frame\".");
        var job = await DoScene("made a movie", (c, l) =>
        {
            var s = l.Scene;
            int a = first ?? s.FirstFrame, b = last ?? s.LastFrame;
            if (a < 0) throw new ArgumentException($"first ({a}) can't be below 0.");
            if (b < a) throw new ArgumentException($"last ({b}) can't be before first ({a}).");
            var files = Files(c);
            string dir = Path.GetDirectoryName(files.Resolve(Path.Combine(folder, "frame.png")))!;
            var paths = Enumerable.Range(0, (b - a) / step + 1).Select(i => Path.Combine(dir, FormattableString.Invariant($"{name}{a + i * step:0000}.png"))).ToList();
            var missing = paths.Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList();
            if (missing.Count > 0)
                throw new ArgumentException($"{missing.Count} of the {paths.Count} frames aren't in {folder}: {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? "…" : "")}. Render them with render_animation first.");
            var movies = formats.Select(f => MovieEncoder.MoviePath(dir, name, f)).ToList();
            files.CheckWritable(movies, overwrite);
            return (paths, movies, files, fps: fps ?? s.FramesPerSecond / step, audio: Sound(s, a));
        }, cancel, changes: false).ConfigureAwait(false);
        var (w, h) = PngSize(job.paths[0]);
        var odd = job.paths.Skip(1).FirstOrDefault(p => PngSize(p) != (w, h));
        if (odd != null) throw new McpException($"{Path.GetFileName(odd)} isn't {w}×{h} like {Path.GetFileName(job.paths[0])}; every frame of a movie must be the same size.");
        var (made, problem) = await MakeMovies(job.paths, job.movies, formats, job.fps, w, h, job.files, AnimationTimeLimit, cancel, job.audio).ConfigureAwait(false);
        var info = new JsonObject
        {
            ["frames"] = job.paths.Count,
            ["movies"] = Strings(made),
            ["movie_fps"] = Census.Round(job.fps, 3),
            ["seconds_long"] = Census.Round(job.paths.Count / job.fps, 2),
            ["size"] = new JsonArray(w, h),
        };
        if (problem != null) info["movie_problem"] = problem;
        return new CallToolResult { Content = [new TextContentBlock { Text = info.ToJsonString() }], IsError = made.Count == 0 };
    }

    /// <summary>A PNG's width and height, from its header.</summary>
    private static (int W, int H) PngSize(string path)
    {
        Span<byte> head = stackalloc byte[24];
        using (var f = File.OpenRead(path))
            if (f.ReadAtLeast(head, 24, throwOnEndOfStream: false) < 24 || head[12] != (byte)'I' || head[13] != (byte)'H')
                throw new McpException($"{Path.GetFileName(path)} isn't a PNG.");
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[16..]), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[20..]));
    }

    private MovieEncoder? _movies;

    /// <summary>What render_animation makes movies with: the ffmpeg on the PATH, unless a test sets another.</summary>
    internal MovieEncoder Movies
    {
        get => _movies ??= MovieEncoder.FromPath();
        set => _movies = value;
    }

    /// <summary>The scene's sound for a movie whose first frame is <paramref name="first"/>, or null for none.</summary>
    private static MovieAudio? Sound(AVAModeler.Scene.Scene s, double first) =>
        s.Audio.On ? new MovieAudio(s.Audio.Path, s.Audio.SecondsAt(first, s.FirstFrame, s.FramesPerSecond), s.Audio.Volume) : null;

    private static List<MovieFormat> MovieFormats(string? movie)
    {
        string key = new((movie ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return key switch
        {
            "" or "none" => [],
            "mp4" => [MovieFormat.Mp4],
            "gif" => [MovieFormat.Gif],
            "both" or "mp4gif" or "gifmp4" or "mp4andgif" => [MovieFormat.Mp4, MovieFormat.Gif],
            _ => throw new McpException($"movie must be mp4, gif or both, not \"{movie}\"."),
        };
    }

    /// <summary>
    /// Joins a run's frames into movies beside them. The frames are numbered by scene frame (with gaps when stepping),
    /// and <see cref="MovieEncoder"/> reads name0001.png onwards, so copies numbered 1, 2, 3… go in a temporary folder
    /// outside the working folder, which is deleted afterwards; the movies are then copied into place.
    /// </summary>
    private async Task<(List<string> Made, string? Problem)> MakeMovies(List<string> frames, List<string> targets, List<MovieFormat> formats, double fps, int width, int height, Workspace files, TimeSpan limit, CancellationToken cancel, MovieAudio? audio = null)
    {
        if (!Movies.Available) return ([], MovieEncoder.NotInstalled);
        string temp = Path.Combine(Path.GetTempPath(), "avamodeler-movie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            for (int i = 0; i < frames.Count; i++) File.Copy(frames[i], Path.Combine(temp, FormattableString.Invariant($"frame{i + 1:0000}.png")));
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(limit);
            var result = await Movies.EncodeAsync(temp, "frame", fps, frames.Count, width, height, formats, "movie", audio, timer.Token).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            List<string> made = [];
            foreach (var file in result.Files)
            {
                string target = targets.First(t => string.Equals(Path.GetExtension(t), Path.GetExtension(file), StringComparison.OrdinalIgnoreCase));
                File.Copy(file, target, overwrite: true);
                files.Wrote(target);
                made.Add(target);
            }
            return (made, result.Problem);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ([], $"The movie couldn't be made: {e.Message} The PNG frames are all saved.");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ───────────────────────────── plumbing ─────────────────────────────

    /// <summary>
    /// Runs a Layout handler's work on the host, as <see cref="Do{T}"/> does. When it <paramref name="changes"/> the
    /// scene, the work is one step on Layout's shared undo history, named "Agent: " and the action
    /// (<see cref="LayoutSession.Edit"/>, apart from any drag the person is in the middle of): if it fails part way the
    /// scene is put back as it was and no step is left.
    /// </summary>
    private async Task<T> DoScene<T>(string action, Func<Commands, LayoutSession, T> work, CancellationToken cancel, bool changes = true)
    {
        try
        {
            return await host.RunSceneAsync(action, (c, l) =>
            {
                using (Files(c).Confine(c))
                {
                    if (!changes) return work(c, l);
                    var result = l.Edit(AgentStep(action), () => work(c, l), apart: true);
                    if (result is JsonObject json) json["undo_scene"] = SceneCensus.UndoState(l);
                    return result;
                }
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>An agent's scene call's name on the undo history ("Agent: added a light").</summary>
    public static string AgentStep(string action) => "Agent: " + action;

    private async Task<string> DoScene(string action, Func<Commands, LayoutSession, JsonObject> work, CancellationToken cancel, bool changes = true) =>
        (await DoScene<JsonObject>(action, work, cancel, changes).ConfigureAwait(false)).ToJsonString();

    /// <summary>Runs a render away from the scene's thread, stopping it after a time limit.</summary>
    private static async Task<T> Rendering<T>(Func<CancellationToken, T> render, TimeSpan limit, string tooLong, CancellationToken cancel)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timer.CancelAfter(limit);
        try
        {
            return await Task.Run(() => render(timer.Token), timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new McpException(string.Format(CultureInfo.InvariantCulture, tooLong, limit.TotalMinutes >= 1 ? $"{limit.TotalMinutes:0} minutes" : $"{limit.TotalSeconds:0} seconds"));
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>An item at a frame, as get_scene describes it (the undo history is added once the step is recorded).</summary>
    private static JsonObject ItemNow(Commands c, LayoutSession l, SceneItem item, double frame) =>
        SceneCensus.Item(l, item, l.Scene.Evaluate(frame), c.Document);

    private static void CheckDiscard(LayoutSession l, bool discard)
    {
        if (l.Modified && !discard)
            throw new InvalidOperationException("The scene has changes that haven't been saved. Save it first with save_scene, or set discard_changes to throw them away.");
    }

    private static void CheckLayer(Document doc, int? layer)
    {
        if (layer is int n && (n < 1 || n > doc.Layers.Count))
            throw new ArgumentException($"There's no layer {n}: the object's layers are 1 to {doc.Layers.Count}.");
        if (layer is int m && doc.Layers[m - 1].Mesh.IsEmpty) throw new ArgumentException($"Layer {m} is empty.");
    }

    /// <summary>A new item's place: constants for position, rotation (or a direction to face) and scale, and its parent.</summary>
    private static void Place(LayoutSession l, SceneItem item, double[]? position, double[]? rotation, double[]? scale, SceneItem? parent, double[]? lookAt = null)
    {
        if (rotation != null && lookAt != null) throw new ArgumentException("Give rotation or look_at, not both.");
        if (parent != null) item.Parent = parent;
        var s = l.Scene;
        void Set(double[]? v, int first, string what)
        {
            if (v == null) return;
            var x = Vector(v, what);
            for (int i = 0; i < 3; i++) Keying.Set(item.Channels[first + i], s, l.Frame, x[i], key: false, what);
        }
        Set(position, 0, "position");
        Set(rotation, 3, "rotation");
        Set(scale, 6, "scale");
        if (lookAt != null)
        {
            var hpb = item.RotationToward(Vector(lookAt, "look_at"), l.Frame);
            Set([hpb.X, hpb.Y, hpb.Z], 3, "rotation");
        }
    }

    private static void SetLight(LightItem light, double[]? color, double? intensity, double? cone, double? edge, double? range, bool? shadows)
    {
        if (color != null) light.Color = Colour(color, "color");
        if (intensity is { } i)
        {
            if (!double.IsFinite(i) || i < 0) throw new ArgumentException("intensity must be 0 or more (1 is 100%).");
            if (Keying.IsAnimated(light.Intensity)) throw new ArgumentException($"{light.Name}'s intensity is animated: change its keys with set_key.");
            light.Intensity.SetConstant(i);
        }
        if (cone is { } c)
        {
            if (!double.IsFinite(c) || c < 0.1 || c > 90) throw new ArgumentException("cone_angle must be from 0.1 to 90 degrees (it's the cone's half-angle).");
            light.ConeAngle = c;
        }
        if (edge is { } e)
        {
            if (!double.IsFinite(e) || e < 0 || e > light.ConeAngle) throw new ArgumentException($"edge_angle must be from 0 to the cone angle ({light.ConeAngle:0.##} degrees).");
            light.EdgeAngle = e;
        }
        if (range is { } r)
        {
            if (!double.IsFinite(r) || r < 0) throw new ArgumentException("range must be 0 (no falloff) or more, in metres.");
            light.Range = r;
        }
        if (shadows is { } sh) light.Shadows = sh;
    }

    private static void SetSoftLight(LightItem light, double[]? areaSize, double? radius, int? samples)
    {
        if (areaSize != null)
        {
            if (light.Type != LightType.Area) throw new ArgumentException($"area_size is for area lights, and {light.Name} is a {light.Type.ToString().ToLowerInvariant()} light (set type area).");
            if (areaSize.Length != 2 || areaSize.Any(v => !double.IsFinite(v) || v <= 0)) throw new ArgumentException("area_size must be [width, height], each more than 0 metres.");
            (light.AreaWidth, light.AreaHeight) = (areaSize[0], areaSize[1]);
        }
        if (radius is { } r)
        {
            if (light.Type is not (LightType.Point or LightType.Spot)) throw new ArgumentException($"radius is for point and spot lights (an area light's size is area_size), and {light.Name} is a {light.Type.ToString().ToLowerInvariant()} light.");
            if (!double.IsFinite(r) || r < 0) throw new ArgumentException("radius must be 0 (a point, hard shadows) or more, in metres.");
            light.Radius = r;
        }
        if (samples is { } n)
        {
            if (n < 1 || n > RenderEffects.MaxSamples) throw new ArgumentException($"shadow_samples must be from 1 to {RenderEffects.MaxSamples}.");
            light.ShadowSamples = n;
        }
    }

    private static void SetCamera(CameraItem cam, double? zoom, double? fov, int? width, int? height)
    {
        if (zoom != null && fov != null) throw new ArgumentException("Give zoom or fov, not both.");
        double? z = zoom;
        if (fov is { } f)
        {
            if (!double.IsFinite(f) || f <= 0 || f >= 180) throw new ArgumentException("fov must be more than 0 and less than 180 degrees.");
            z = CameraItem.VerticalFovToZoom(f);
        }
        if (z is { } zz)
        {
            if (!double.IsFinite(zz) || zz <= 0) throw new ArgumentException("zoom must be more than 0.");
            if (Keying.IsAnimated(cam.Zoom)) throw new ArgumentException($"{cam.Name}'s zoom is animated: change its keys with set_key.");
            cam.Zoom.SetConstant(zz);
        }
        if (width is { } w)
        {
            if (w < 16 || w > 8192) throw new ArgumentException("width must be from 16 to 8192 pixels.");
            cam.Width = w;
        }
        if (height is { } h)
        {
            if (h < 16 || h > 8192) throw new ArgumentException("height must be from 16 to 8192 pixels.");
            cam.Height = h;
        }
    }

    /// <summary>
    /// An environment image an agent gave: "" for none, a built-in name as it is, or a file in (or below) the working
    /// folder, made absolute, which must be there and readable.
    /// </summary>
    private string EnvironmentImage(string given)
    {
        string image = given.Trim();
        if (image.Length == 0) return "";
        if (image.StartsWith(EnvironmentMap.BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (EnvironmentMap.Load(image, null, out var builtinProblem) == null) throw new ArgumentException(builtinProblem);
            return image.ToLowerInvariant();
        }
        string full = Path.GetFullPath(Path.Combine(host.Root, image));
        if (EnvironmentMap.Load(full, null, out var problem) == null)
            throw new ArgumentException($"{problem} The path is relative to the working folder; or use builtin:sky, builtin:studio or builtin:sun?heading=…&elevation=…&turbidity=…, which need no file.");
        return full;
    }

    private static CameraItem RenderCamera(SceneModel s, string? camera)
    {
        if (camera == null) return s.CurrentCamera ?? throw new InvalidOperationException("The scene has no camera. Add one with add_camera.");
        return Item(s, camera, "camera") as CameraItem ?? throw new ArgumentException($"{camera} isn't a camera.");
    }

    /// <summary>A picture's size: as given, or the camera's shape at up to <paramref name="across"/> pixels wide.</summary>
    private static (int Width, int Height) PictureSize(CameraItem cam, int? width, int? height, int across, int max)
    {
        double aspect = cam.FrameAspect;
        int w = width ?? (height is { } h0 ? (int)Math.Round(h0 * aspect) : Math.Min(across, cam.Width));
        int h = height ?? (int)Math.Round(w / aspect);
        if (w < 16 || w > max || h < 16 || h > max) throw new ArgumentException($"width and height must be from 16 to {max} pixels (they were {w} and {h}).");
        return (w, h);
    }

    private static EndBehavior Behavior(string value, string what)
    {
        string key = new(value.Where(char.IsLetter).ToArray());
        if (Enum.TryParse<EndBehavior>(key, ignoreCase: true, out var b) && Enum.IsDefined(b)) return b;
        throw new ArgumentException($"{what} must be one of constant, reset, repeat, oscillate, offset_repeat or linear, not \"{value}\".");
    }
}
