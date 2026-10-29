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
/// auto-key) through <see cref="IModelerHost.RunSceneAsync{T}"/>. Layout has no undo, so every call that changes the
/// scene takes a <see cref="SceneSnapshot"/> first and puts it back if the call fails part way (all or nothing), and
/// keeps it for undo_scene (<see cref="SceneHistory"/>).
/// </summary>
public sealed partial class ModelerTools
{
    private readonly SceneHistory _sceneHistory = new();

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
    [Description("Describes Layout's scene: the frame range, frames per second, current frame, current camera, auto-key, ambient light, background and fog; then every item (object, null, light, camera, bone) with its #index, kind, name, parent, visibility, position (metres), rotation [heading, pitch, bank] (degrees), scale, which channels are animated, and its own settings (a light's type, colour, intensity, direction; a camera's zoom, field of view and frame size; an object's source, point and polygon counts and world bounds), all at one frame. Also any warnings (files that couldn't be loaded, missing images) and what undo_scene can undo.")]
    public Task<string> GetScene(
        [Description("The frame to describe positions and values at (default: the current frame).")] double? frame = null,
        CancellationToken cancel = default) =>
        DoScene("read the scene", (c, l) => SceneCensus.Scene(l, c.Document, SceneCensus.Frame(l, frame), _sceneHistory), cancel, changes: false);

    [McpServerTool(Name = "get_keys", ReadOnly = true, Idempotent = true)]
    [Description("Lists an item's keys, channel by channel: each key's frame, value, and tension, continuity and bias (Kochanek–Bartels TCB, -1 to 1; left out when 0) and linear flag, plus what the channel does before its first key and after its last (pre and post). A channel never keyed shows its constant value. Channels: position.x/y/z (metres), heading, pitch, bank (degrees), scale.x/y/z, and a light's intensity or a camera's zoom.")]
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
                ["frames_with_keys"] = new JsonArray(it.KeyFrames().Where(f => f >= l.Scene.FirstFrame && f <= l.Scene.LastFrame).Select(f => (JsonNode)Census.Round(f)).ToArray()),
                ["channels"] = new JsonArray(channels.Select(ch => (JsonNode)Keys(ch.Name, ch.Envelope, l.Scene)).ToArray()),
            };
        }, cancel, changes: false);

    // ───────────────────────────── scene files ─────────────────────────────

    [McpServerTool(Name = "new_scene", Destructive = true)]
    [Description("Starts a new scene as Layout does: a camera at [0, 0, -5] looking along +Z and a distant light, frames 1 to 60 at 30 frames a second, nothing else (add the object with add_object). Refuses if the scene has changes that haven't been saved, unless discard_changes is true. undo_scene can't take this back.")]
    public Task<string> NewScene(
        [Description("Throw away the scene's unsaved changes.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        DoScene("started a new scene", (c, l) =>
        {
            CheckDiscard(l, discard_changes);
            l.Replace(SceneModel.CreateDefault());
            l.Warnings.Clear();
            return SceneCensus.Scene(l, c.Document, l.Frame, _sceneHistory);
        }, cancel);

    [McpServerTool(Name = "open_scene", Destructive = true)]
    [Description("Opens a scene in place of the current one: .avascene (AVA Modeler's own, keeps everything, meshes usually inside it) or LightWave .lws (LightWave 4 and 5, and later). The path is relative to the working folder and must stay inside it; objects the scene names are loaded from inside the working folder too (one that can't be is a placeholder, and a warning says why). Refuses if the scene has unsaved changes, unless discard_changes is true. undo_scene can't take this back.")]
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
            return SceneCensus.Scene(l, c.Document, l.Frame, _sceneHistory);
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

    // ───────────────────────────── adding items ─────────────────────────────

    [McpServerTool(Name = "add_object", Destructive = true)]
    [Description("Puts an object in the scene. With no file, the Modeler's object (in the live app, the one open in the Modeler) goes in live: the scene shows each change made to it later, as Layout's Add › The Modeler's Object does; if it's already there that item is returned, not added again. With file, an object file from the working folder (.lwo, .obj, .geo or a plugin format). layer (1 to 10, as the app's layer buttons number them) takes just that layer; otherwise every non-empty layer, merged. The object's own coordinates are kept: position, rotation and scale (constants, not keys) then place it. Returns the item, with its world bounds.")]
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
    [Description("Adds a light. distant: parallel rays along its +Z axis from infinitely far (like the sun; its position doesn't matter). point: shines every way from its position. spot: shines along its +Z axis inside a cone. Unrotated, +Z points forward (away from the viewer in the Front view); turn it with rotation [heading, pitch, bank] in degrees (heading 90 shines along +X, pitch 90 straight down), or give look_at to aim it at a point. Colour is [r, g, b], whole numbers from 0 to 255 like surfaces'; intensity 1 is LightWave's 100% (more is brighter). Shadows are ray-traced. Values are constants, not keys.")]
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
        CancellationToken cancel = default) =>
        DoScene("added a light", (c, l) =>
        {
            var kind = Json.Choice<LightType>(type, "type");
            var s = l.Scene;
            var parentItem = parent == null ? null : Item(s, parent, "parent");
            var light = s.AddLight(FreeName(s, name, $"{kind} Light"), kind);
            SetLight(light, color, intensity, cone_angle, edge_angle, range, shadows);
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
    [Description("Changes an item's settings, leaving out what you don't give: its name, parent (the item it moves with; \"\" for none), pivot (the point in its own coordinates it turns and scales about, placed at its position) and visibility. A light's type, colour (0 to 255), intensity (1 is 100%), cone_angle and edge_angle (spot, degrees), range (metres; 0 none) and shadows. A camera's zoom or fov (vertical, degrees), width and height (pixels), and make_current. An object's cast_shadows and receive_shadows. A bone's rest_length, strength and active. Intensity and zoom set the value throughout; when they're animated, use set_key.")]
    public Task<string> SetItem(
        [Description("The item's name, or #index from get_scene.")] string item,
        [Description("A new name.")] string? name = null,
        [Description("The item to move with, by name; \"\" for none.")] string? parent = null,
        [Description("[x, y, z] in the item's own coordinates.")] double[]? pivot = null,
        [Description("Shown and rendered.")] bool? visible = null,
        [Description("Light: distant, point or spot.")] string? type = null,
        [Description("Light: [r, g, b], each 0 to 255.")] double[]? color = null,
        [Description("Light: brightness, 1 is 100%.")] double? intensity = null,
        [Description("Spot light: the cone's half-angle in degrees.")] double? cone_angle = null,
        [Description("Spot light: the soft edge in degrees.")] double? edge_angle = null,
        [Description("Point or spot light: metres to fade to nothing; 0 for no falloff.")] double? range = null,
        [Description("Light: cast shadows.")] bool? shadows = null,
        [Description("Camera: zoom factor (1 / tan(vertical fov / 2)).")] double? zoom = null,
        [Description("Camera: vertical field of view in degrees (instead of zoom).")] double? fov = null,
        [Description("Camera: picture width in pixels.")] int? width = null,
        [Description("Camera: picture height in pixels.")] int? height = null,
        [Description("Camera: make it the current camera.")] bool? make_current = null,
        [Description("Object: cast shadows on other things.")] bool? cast_shadows = null,
        [Description("Object: show shadows cast on it.")] bool? receive_shadows = null,
        [Description("Bone: its length at rest, metres.")] double? rest_length = null,
        [Description("Bone: how strongly it pulls.")] double? strength = null,
        [Description("Bone: whether it deforms the object.")] bool? active = null,
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
            Only<CameraItem>(zoom, "zoom"); Only<CameraItem>(fov, "fov"); Only<CameraItem>(width, "width"); Only<CameraItem>(height, "height"); Only<CameraItem>(make_current, "make_current");
            Only<ObjectItem>(cast_shadows, "cast_shadows"); Only<ObjectItem>(receive_shadows, "receive_shadows");
            Only<Bone>(rest_length, "rest_length"); Only<Bone>(strength, "strength"); Only<Bone>(active, "active");

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
            switch (it)
            {
                case LightItem light:
                    if (type != null) light.Type = Json.Choice<LightType>(type, "type");
                    SetLight(light, color, intensity, cone_angle, edge_angle, range, shadows);
                    break;
                case CameraItem cam:
                    SetCamera(cam, zoom, fov, width, height);
                    if (make_current == true) s.CurrentCamera = cam;
                    else if (make_current == false && s.CurrentCamera == cam)
                        throw new ArgumentException($"To stop using {cam.Name}, make another camera current instead.");
                    break;
                case ObjectItem obj:
                    if (cast_shadows is { } cs) obj.CastShadows = cs;
                    if (receive_shadows is { } rs) obj.ReceiveShadows = rs;
                    break;
                case Bone bone:
                    if (rest_length is { } rl)
                    {
                        if (!(rl > 0) || !double.IsFinite(rl)) throw new ArgumentException("rest_length must be more than 0 metres.");
                        bone.RestLength = rl;
                    }
                    if (strength is { } st) bone.Strength = st;
                    if (active is { } ac) bone.Active = ac;
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
    [Description("Makes or changes one key on one channel: position.x, position.y, position.z (metres), heading, pitch, bank (degrees), scale.x, scale.y, scale.z, or a light's intensity or a camera's zoom. The curve through keys is Kochanek–Bartels: tension (1 slows into and out of the key, -1 overshoots), continuity (sharp corners when away from 0) and bias (-1 leans the curve toward the previous key, 1 toward the next), each -1 to 1, default 0; linear makes the stretch leading into this key a straight line. A channel never keyed before has its constant replaced by its first key. A key already at that frame is replaced (keeping its tension, continuity and bias unless given).")]
    public Task<string> SetKey(
        [Description("The item's name, or #index from get_scene.")] string item,
        [Description("The channel, e.g. \"position.y\" or \"heading\".")] string channel,
        [Description("The frame, within the scene's frames.")] double frame,
        [Description("The value: metres, degrees, a scale factor, an intensity (1 is 100%) or a zoom factor.")] double value,
        [Description("-1 to 1 (default 0, or the existing key's).")] double? tension = null,
        [Description("-1 to 1 (default 0, or the existing key's).")] double? continuity = null,
        [Description("-1 to 1 (default 0, or the existing key's).")] double? bias = null,
        [Description("Straight line from the previous key into this one.")] bool? linear = null,
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
            int i = Keying.IsConstant(env, s) ? -1 : env.IndexOf(f);
            var old = i >= 0 ? env.Keys[i] : new EnvelopeKey(f, value);
            Keying.Key(env, s, new EnvelopeKey(f, value, tension ?? old.Tension, continuity ?? old.Continuity, bias ?? old.Bias, linear ?? old.Linear), keepShape: false);
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
                throw new ArgumentException($"{it.Name} has no key at frame {frame:0.###}{(channel != null ? $" on {channels[0].Name}" : "")}. Its keys are at frames {string.Join(", ", it.KeyFrames().Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)))}.");
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

    [McpServerTool(Name = "set_scene", Destructive = true)]
    [Description("Changes the scene's settings, leaving out what you don't give: the frame range (first_frame, last_frame, whole frames; 1 to 60 by default) and frames per second; ambient light (colour 0 to 255, intensity as a fraction, 0.25 by default); the backdrop: a colour, or a gradient from gradient_bottom (straight down) to gradient_top (straight up); fog (linear: none up to fog_min metres from the camera, full at fog_max); the current camera (the one render_scene uses); and the current frame (where Layout's timeline stands, and the default for the other tools).")]
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
            bool settings = first_frame != null || last_frame != null || fps != null || ambient_color != null || ambient_intensity != null || background_color != null
                || gradient != null || gradient_top != null || gradient_bottom != null || fog != null || fog_min != null || fog_max != null || fog_color != null || camera != null;
            // Only the frame: that isn't a change to the scene (nor an undo_scene step).
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
                s.Notify();
            });
            l.SetFrame(frame ?? l.Frame);
            var result = SceneCensus.Scene(l, c.Document, l.Frame, _sceneHistory);
            result.Remove("items");
            return result;
        }, cancel);

    [McpServerTool(Name = "undo_scene", Destructive = true)]
    [Description("Takes back your last change to the scene (Layout has no undo of its own, so this covers only your scene calls, one call at a time, up to 50). It works while your changes are the latest: once the person changes the scene in the app, or it's replaced (new_scene, open_scene), the earlier steps are forgotten rather than undone over their work. It doesn't touch the object (that's undo) or the current frame.")]
    public Task<string> UndoScene(CancellationToken cancel = default) =>
        DoScene("undid a scene change", (c, l) =>
        {
            string label = _sceneHistory.Undo(l);
            var result = SceneCensus.Scene(l, c.Document, l.Frame, _sceneHistory);
            result["undone"] = label;
            return result;
        }, cancel, changes: false);

    // ───────────────────────────── seeing ─────────────────────────────

    [McpServerTool(Name = "render_scene", ReadOnly = true, Idempotent = true)]
    [Description("Ray-traces a frame of the scene through a camera as a PNG, as Layout's Render Frame does: every surface setting and texture, the scene's lights with ray-traced shadows, ambient light, backdrop and fog, reflections and transparency. The size defaults to the camera's picture shape at up to 640 across. Slower than render_scene_view: start small and with few samples, then go up.")]
    public async Task<CallToolResult> RenderScene(
        [Description("The frame (default: the current frame).")] double? frame = null,
        [Description("Width in pixels, 16 to 1920 (default: the camera's, at most 640).")] int? width = null,
        [Description("Height in pixels, 16 to 1920 (default: to keep the camera's shape).")] int? height = null,
        [Description("The camera, by name (default: the current camera).")] string? camera = null,
        [Description("Rays per pixel for antialiasing, 1 to 16 (default 4).")] int samples = 4,
        [Description("Ray-traced shadows (default true).")] bool shadows = true,
        [Description("Mirror reflections (default true).")] bool reflections = true,
        [Description("Transparency and refraction (default true).")] bool transparency = true,
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
        var image = await Rendering(t => ScenePictures.Render(copy, f, cam, w, h, samples, shadows, reflections, transparency, t), RenderTimeLimit,
            "The render took longer than {0} and was stopped: try a smaller size or fewer samples.", cancel).ConfigureAwait(false);
        string about = FormattableString.Invariant($"Frame {f:0.###} through {cam.Name}, {w}×{h}, {samples} sample{(samples == 1 ? "" : "s")} a pixel, in {clock.Elapsed.TotalSeconds:0.0} s.");
        if (problems.Count > 0) about += " " + string.Join(" ", problems);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(image.ToPng(alpha: false), "image/png"), new TextContentBlock { Text = about }] };
    }

    [McpServerTool(Name = "render_scene_view", ReadOnly = true, Idempotent = true)]
    [Description("Draws the scene quickly as Layout's view does (software, not ray-traced), as a PNG: objects shaded or in wireframe, and every light (a cross, with an arrow the way it shines), camera (a box with a lens), null (a cross) and bone (a diamond) as a little drawing. view camera looks through the current camera (its heading and pitch; bank isn't shown), framed as render_scene frames it; perspective orbits the scene from heading and pitch; top looks down -Y, front along +Z, side along -X, each framing everything. Also lists where each item landed in the picture. Use it to check placement and aim before rendering.")]
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
    [Description("Ray-traces a run of frames into numbered PNG files in a folder inside the working folder (e.g. frames/frame0001.png, numbered by frame), and returns a contact sheet of them (up to eight a row) and the list of files. Bounded so it can't run away: at most 120 frames, 960 pixels each way and 4 samples a pixel, and width × height × samples × frames at most 20 million; it stops after 5 minutes. An existing file is only replaced when overwrite is true.")]
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
        CancellationToken cancel = default)
    {
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
            long work = (long)pw * ph * samples * frames.Count;
            if (work > MaxAnimationWork)
                throw new ArgumentException(FormattableString.Invariant($"That's {work / 1e6:0.#} million rays (width × height × samples × frames); at most {MaxAnimationWork / 1e6:0} million at a time. Render smaller, with fewer samples or frames."));
            var files = Files(c);
            string dir = Path.GetDirectoryName(files.Resolve(Path.Combine(folder, "frame.png")))!;
            var paths = frames.Select(fr => Path.Combine(dir, FormattableString.Invariant($"{name}{fr:0000}.png"))).ToList();
            files.CheckWritable(paths, overwrite);
            var copy = ScenePictures.Copy(s);
            return (copy, frames, cam: (CameraItem)copy.Items[IndexOf(s, cm)], pw, ph, paths, files);
        }, cancel, changes: false).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var images = await Rendering(t =>
        {
            var list = new List<RenderImage>();
            for (int i = 0; i < job.frames.Count; i++)
            {
                var img = ScenePictures.Render(job.copy, job.frames[i], job.cam, job.pw, job.ph, samples, true, true, true, t);
                File.WriteAllBytes(job.paths[i], img.ToPng(alpha: false));
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
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(sheet, "image/png"), new TextContentBlock { Text = info.ToJsonString() }] };
    }

    // ───────────────────────────── plumbing ─────────────────────────────

    /// <summary>
    /// Runs a Layout handler's work on the host, as <see cref="Do{T}"/> does. When it <paramref name="changes"/> the
    /// scene, a snapshot is taken first: if the work fails part way the scene is put back as it was (Layout has no undo
    /// to lean on), and if it succeeds the snapshot becomes an undo_scene step.
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
                    var scene = l.Scene;
                    var before = scene.Snapshot();
                    try
                    {
                        var result = work(c, l);
                        _sceneHistory.Done(before, l, action);
                        if (result is JsonObject json) json["undo_scene"] = _sceneHistory.State(l);
                        return result;
                    }
                    catch
                    {
                        // Some settings (a name, a pivot) are plain fields that don't bump the version, so put it back
                        // whether or not the version moved: only what changed is copied back.
                        if (ReferenceEquals(l.Scene, scene))
                        {
                            before.Restore();
                            l.Revalidate();
                            _sceneHistory.Restored(before, l);
                        }
                        throw;
                    }
                }
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

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

    /// <summary>An item at a frame, as get_scene describes it (what undo_scene can do is added once the step is recorded).</summary>
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
