# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers the features, the keys, the project
table and the known limits; this file covers how the code fits together and the rules that keep it working.

## What this is

AVA Modeler recreates the classic mid-1990s standalone 3D modeler, inspired by LightWave Modeler 4 and 5. It has a
Layout workspace that animates and ray-traces scenes. It is C# on .NET 10 and Avalonia 12, with Lua macros
(MoonSharp) and .NET plugins. The original plan is `Classic 3D Modeler Recreation Plan.md`.

## Commands

```bash
dotnet build AVAModeler.slnx                      # the SDK version is in global.json (10.0.400 or later)
dotnet test AVAModeler.slnx                       # every test runs headless: no window opens
dotnet run --project src/AVAModeler               # run it; an object file as the argument opens in the Modeler
dotnet src/AVAModeler/bin/Debug/net10.0/AVAModeler.dll --mcp   # the MCP server on stdio, no window (not via dotnet run)
dotnet test tests/AVAModeler.Mcp.Tests            # the MCP server: handlers, whole agent sessions, live mode, processes
dotnet test tests/AVAModeler.Chat.Tests           # the AI chat's agent loop, against a fake Anthropic API and a fake OpenAI-compatible server
AVAMODELER_SHOTS=$PWD/Images dotnet test tests/AVAModeler.UI.Tests --filter Screenshot   # refresh the README's pictures
python3 samples/images/make_textures.py           # redraws the sample texture images
python3 samples/images/make_environment.py        # redraws the sample environment, studio.hdr
python3 samples/images/make_blueprints.py         # redraws the rocket blueprints (rocket-side/front/top.png)
AVAMODELER_REGOLD=1 dotnet test tests/AVAModeler.Slicing.Tests   # rewrite the reference G-code after a meant change
```

`Directory.Build.props` sets `TreatWarningsAsErrors`, nullable reference types, implicit usings and
`InvariantGlobalization` for every project: a warning fails the build, so keep it at zero.

## Layout

- `src/AVAModeler.Core/`: maths (`Vec`, `Mat4`), the n-gon `Mesh` with its UV maps, surfaces and textures.
  - `Document.cs` holds the layers, the selection and the `UndoStack`. `Png.cs` writes PNGs (renders, glTF images).
    `Hdr.cs` is `HdrImage` (float RGB pictures) and the Radiance RGBE reader (flat, old and new run-length scanlines,
    `+Y`/`-Y`, `EXPOSURE`) and writer (flat), for environment maps.
  - `Texture.cs` is `TextureLayer` (an image map, or a procedural: `Procedural` names the kind, `Parameters` holds
    TFP0 to TFP3 and TIP0, with `TextureColor`, `Value` and `Falloff`) and the projections' maths. `Procedurals.cs`
    is LightWave 5's procedural textures: the catalog (`Procedurals.All`, each kind's settings with their LWOB slot,
    default and limits; `Find`, `Get`, `Set`, `SetType`, the Texture Type list) and `ProceduralTexture`, a layer ready
    to evaluate (`Amount` at a point, 0 to 1). `Noise.cs` is Perlin's improved noise with his fixed permutation table,
    and fractal and turbulence sums over it.
  - `Backdrop.cs` is the reference images behind the flat views: `Backdrop` (one view's image, centre on its two axes,
    width, optional height, brightness, contrast, opacity, invert, visible; immutable, with `Checked()`), `BackdropSet`
    (Top, Front, Side; `Document.Backdrops`, changed only through `Document.SetBackdrops`), `BackdropMapping` (each
    view's axes, the image's size and world corners, `ImagePoint`, `FitTo`, `WidthFromPixels`, `Problems`, and
    `Adjusted`, the picture with its brightness, contrast and invert baked in, cached per picture and settings),
    `BackdropFiles` (setup JSON, images relative to the file) and `BackdropMemory` (each object's setup by its path, in
    `backdrops.json` in the settings folder).
- `src/AVAModeler.Formats/`: LWOB/LWLO read and write, LWO2 read, VideoScape GEO and OBJ, on a shared `Iff` reader;
  glTF 2.0 and STL. It references `AVAModeler.Geometry` for triangulation.
  - `ObjectFiles.cs` is the one registry of object formats: `LoadExtensions`, `SaveExtensions`, `Describe`, `Load`,
    `Save` (with `SaveOptions` for ASCII STL and a .gltf's .bin) and `Targets` (the files a save writes). The open
    dialogs, Lua's load and save, the MCP `Workspace` and Layout's object loading all go through it, so a new format
    is added there once.
  - `ProceduralBake.cs` bakes a surface's colour procedurals into a picture (through its UV map, or as a six-cell box
    map) for glTF's base colour texture.
  - `GltfBuilder.cs` builds glTF by hand (System.Text.Json and one binary buffer): meshes from LightWave meshes and
    surfaces, nodes, cameras, lights, animations, and .glb/.gltf packing. `Gltf.cs` writes and reads objects with it;
    `Stl.cs` is STL, binary and ASCII.
- `src/AVAModeler.Geometry/`: triangulation (LibTessDotNet), BSP booleans and drills, subdivision, and the
  extrude, lathe and bevel family.
  - `MeshCheck.cs` is the mesh check (Mesh Info, `check_mesh`, Lua's `checkmesh()`): `MeshCheck.Document` gives a
    `MeshReport` of `LayerCheck`s (counts, bounds, area, shells, holes, boundary, non-manifold and inconsistent edges,
    the faces Align would turn, cavities, degenerate and non-planar polygons, duplicate and unused points, volume and
    centre of mass), its verdict, issues, notes and text lines. It changes nothing; the volume is
    `MeshMeasure.Volume`'s, so a closed shell wound LightWave's way (clockwise from outside) is positive.
  - The edge and face tools' algorithms: `EdgeLoops.cs` (edge loops and rings, the strip of quads a ring crosses, and
    Loop Cut; `FaceUVs` there captures a face's corner UVs by point for tools that rebuild faces), `Holes.cs` (holes as
    loops from the mesh check's boundary edges via `Topology.ChainBoundary`, Fill Hole, and Bridge between edge runs,
    faces or holes), `Inset.cs` (`Insets.Apply`, individual or group, the group bevel) and `Chamfer.cs` (edge bevel,
    closing corners with `Holes.Fill`). New UVs come from `UVTransfer.Interpolant` (a face's UVs as a function of
    position) or from interpolating along edges, face by face so seams stay seams.
- `src/AVAModeler.Slicing/`: 3D printing, from the object to G-code (Clipper2 for the 2D polygon work). No UI.
  - `Profiles.cs`: `PrinterProfile` (bed, height, nozzle, filament, flavour, start and end G-code with `Placeholders`)
    and `MaterialProfile` (temperatures, retraction, fan, density), immutable with `Checked()`, and the `Presets`.
    A printer may have its own `RetractLength` (over the material's), `OriginAtCentre` (the writer shifts every move
    by half the bed), `RoundBed` (Placement checks the circle; the previews draw it) and `Notes`. `PrinterPresets.cs`
    is the printers on the market, built by firmware (`Marlin`, `Prusa`, `Klipper`, `Bambu`, `Delta`, Sailfish for
    the Creator Pro) from start and end templates; `PresetTests` slices a cube on every one of them, so a new preset is
    checked by adding it there.
    `PrintSetup.cs` is what the preferences keep (the chosen profiles, the person's own, the placement) as JSON.
  - `Placement.cs`: `Placement.Place` turns the foreground layers into a `PrintModel`, triangles in the printer's space
    (through `Stl.Triangles` and `Stl.ToStl`, so millimetres and Z up as STL export has them) welded into shared
    points, laid flat (`LayFlat`: as modelled, `LargestFlatFace`, or the selected polygon) and turned with `Mat3`,
    dropped on the bed and centred (or the origin at the bed's centre), copied into rows, with what doesn't fit and
    the mesh check's verdict.
  - `Slicer.cs`: `Slicer.Slice` cuts a `PrintModel` into `SliceLayer`s halfway up each layer (`SliceSettings`: layer
    and first-layer height). A point at or above the plane counts as above, so no point lies on it; each edge's cut
    point is worked out once and shared, so segments join edge to edge with no tolerance (outsides anticlockwise, holes
    clockwise). Chains that don't close (holes in the mesh) are counted in `OpenChains` and left out. `Geometry2D` has
    the plain 2D sums (area, containment, `Clean`).
  - `Polygons2D.cs`: the region work on Clipper2 (non-zero fill, 4 decimal places): `Union`, `Difference`,
    `Intersect`, `Offset` (sharp or round corners), `ClipLines` (open lines inside a region) and `Islands` (a region
    as outsides with their holes, through a PolyTree, specks dropped). The slicer's loops become islands through it,
    so overlapping shells merge. Don't hand-roll offsets or clipping elsewhere.
  - `SliceSettings.cs` (layer heights, line width, walls, top and bottom layers, infill and its pattern and angle,
    seam, skirt or brim, supports, speeds, retraction travel, Z hop; `Checked(printer)`) and `Toolpaths.cs`:
    `Planner.Plan` makes a `PrintPlan` of `ToolpathLayer`s, each a list of `Extrusion`s (`PathKind`, points, closed)
    in print order. Walls are offsets of each island; skin is the inside not covered by every layer within
    `TopLayers` above or `BottomLayers` below; lines are on one grid fixed to the bed so layers stack; support is
    carried down from each overhang (one layer of air under it, `SupportGap` from the object) until it meets the
    object or the bed (`SupportPlacement.BedOnly` keeps only what stands on support down to the bed); its top
    `SupportInterfaceLayers` (not covered by the support above, as skin is found) are `SupportInterface` lines at 90°. Islands go nearest first; inner walls, outer wall, gap fill, bridges, skin, infill; `Order`
    and `Seam` place the lines. Lines side by side are `Planner.Spacing` apart (w − h(1 − π/4)), never a full width.
    A wall loop is kept only where it has half a line of room. `GapFill` covers what walls and the inside leave
    (cut into 3 mm pieces, lines along each piece's long axis, `Extrusion.Width` narrower); `Bridges` takes the solid
    over air (beyond the layer below by a line) and picks the angle that anchors most length at both ends. Each
    `ToolpathLayer.Comb` is the islands a line in from their walls, for `Combing.Route` (travel round holes inside one
    island, stepping in from a wall; null means straight with a retraction). The writer slows a layer quicker than
    `MaterialProfile.MinLayerSeconds` and runs the fan full for bridges.
  - `GCode.cs`: `GCode.Write` turns a plan into Marlin-style G-code (G21, G90, M83 relative extrusion; heating and
    homing first, the start and end G-code with placeholders filled, `;LAYER:n` and `;TYPE:` comments, retraction
    before long travel and layer changes, Z hop, the fan from its layer) and `PrintStats` (time from lengths and
    speeds, filament, grams). `FilamentPerMm` is the one extrusion rule. `Save` writes through a temporary file.
    `PrintJob.Run(doc, setup)` runs every stage and gives the `Warnings` (doesn't fit, not printable, open outlines):
    the UI and the MCP tool go through it.
  - `tests/AVAModeler.Slicing.Tests`: `Playback` plays G-code back as a printer would; `SafetyTests` runs it over
    shapes and settings (on the bed, under the height, hot before printing, layers rising, retractions put back);
    `ReferenceTests` compares a small print with `Reference/tube-10mm.gcode`, numbers within a unit in their last
    place. A change to what the slicer writes that's meant: `AVAMODELER_REGOLD=1 dotnet test tests/AVAModeler.Slicing.Tests`,
    then read the difference before committing it.
- `src/AVAModeler.Tools/`: the tools and the scripting around them.
  - `Tool.cs` is the base class and `[Param]`. `Commands.cs` has the `ToolRegistry` and the command API.
  - `Macros.cs` runs Lua; the other files hold the tools, grouped by kind (`ModifyTools.cs`, `UVTools.cs` and so on).
    `CheckTools.cs` has Select Problems (selects one kind of problem the mesh check finds, as an undo step that changes
    only the selection: `Tool.SelectsOnly` keeps the object unmodified) and Remove Degenerate; `SelectProblemsTool.FixFor`
    names the existing tool that fixes each kind (Fill Hole, Align, Merge Points, Remove Degenerate). Don't add a second
    tool for a fix one of those already makes.
  - `Commands.Slice` (Lua's `slice()`) runs `PrintJob` from `Commands.PrintSetup` (the app sets it from the 3D
    Print window's) and writes through `Commands.TextSaver`, which the MCP `Workspace.Confine` keeps inside the
    working folder as it does `Saver`: a new kind of file a macro writes goes through a hook like it.
  - `LuaSandbox.cs`: the macro sandbox for any Lua (`Create`, `Run` with a caller's globals, print sink and time
    limit; `ToArgs`, `List`, `Indices`, `Numbers`, `Strings`). `Macros.Run` is built on it; another app's Lua
    (AVALightWave's scene macros) goes through it, never a bare MoonSharp `Script`.
  - `EdgeTools.cs` has Loop Cut, Bridge, Fill Hole, Inset and Chamfer; Select Loop and Select Ring are `SelectionOps`
    commands (in `PolygonTools.cs`, beside Select Connected), with `Commands.SelectEdges`, `SelectLoop` and
    `SelectRing` for macros (`seledges`, `selloop`, `selring`) and agents (`select_edges`, `select_loop`, `select_ring`).
    Edge tools read their edges through `Selection.ActiveEdges` (Core), which follows the app's selection rules.
- `src/AVAModeler.Rendering/`: `IRenderer` with the software and OpenGL (Silk.NET) renderers, view cameras and
  picking. `Texturing.cs` is what both renderers' textured faces share (`Texturing.Fragment` is the C# twin of the GL
  fragment shader): image layers by their corners' UVs, procedural layers from each pixel's interpolated point
  (`TexturedCorner.Local` and `World`), evaluated with `ProceduralTexture`; the shader in `GlRenderer.cs` repeats
  `Noise` and `ProceduralTexture.Pattern` in GLSL, with the permutation table as a 256 × 1 texture. `BackdropDrawing`
  (in `Texturing.cs`) makes a flat view's backdrop a `BackdropQuad`: a textured surface whose colour is the view's grey
  with the adjusted picture laid over it at its opacity, and its four world corners; both renderers draw its two
  triangles first, before the grid (`ViewScene.Backdrops`, filled by `ViewScene.FromDocument`).
- `src/AVAModeler.Scene/`: Layout's model: items, envelopes, bones, LWS and JSON scenes, and the ray tracer.
  - `RenderEffects.cs`: the render effects' settings as immutable records, `AmbientOcclusion` and
    `EnvironmentSettings` on the `Scene` (`Occlusion`, `Environment`), `DepthOfField` and `MotionBlur` on each
    `CameraItem`, each with `Checked()` (throws an `ArgumentException` saying what's wrong, used by the UI and the MCP
    tools alike). Change one by setting a new record (`with`). Soft lights are `LightItem`'s `Area` type
    (`AreaWidth`, `AreaHeight`) and `Radius`, with `ShadowSamples`.
  - `LightEffects.cs`: `LensFlare`, `VolumetricLight` and `ShadowMapSettings`, records on `LightItem` (`Flare`,
    `Volumetric`, `ShadowMap`; the `FlareIntensity` envelope; `UsesShadowMap`). `Rendering/RayTracer.Lights.cs` is the
    partial of `RayTracer` and its `Tracer` for flares (`FlareCanvas`, `DrawFlares`), volumetric beams (`Volumetric`,
    `Beam`, `Cone`) and `Visibility`; `Rendering/ShadowMaps.cs` is `DepthView`, `LightShadowMap` and `RenderCounters`
    (work counts the tests read through `RenderSettings.Counters`). A new light effect gets its own branch in the
    `Tracer`, built in `PrepareLightEffects`; count expensive work through `RenderCounters` so tests assert work, not time.
    `Lws/LwsLightBits.cs` holds the flare bits for .lws.
  - `Compositing.cs`: `SceneImage` (an image-list entry), `CompositingSettings` and `ForegroundKey`, `ImageSequence`
    (numbered files by frame from a cached folder scan, `Resolve`, `Problem`) and `SceneImages` (list lookup,
    `TextureFile` for sequence textures, `Picture`, and `Problems`, which feeds `Scene.Evaluate`'s warnings and
    `LayoutSession.ImageProblems`). `Scene.Images` (set a new list; the setter copies it) and `Scene.Compositing` are in
    `Copy` and `SceneSnapshot`. `ObjectItem.Dissolve` is the `dissolve` channel; the ray tracer mixes what's behind a
    dissolved hit and leaves wholly dissolved objects out of the BVH.
  - `Envelope.cs`: `SpanKind` (Tcb, Linear, Stepped, Bezier) on `EnvelopeKey.Span` with `InSlope` and `OutSlope` in
    value per frame. `Kind` is the span that counts (the `Linear` flag on a TCB key means Linear; `WithSpan` keeps the
    flag in step). A span belongs to the key it ends at: a Bezier span uses the previous key's `OutSlope` and its own
    `InSlope`; a TCB span leaves a Bezier key at its `OutSlope` and a stepped key flat. `Slopes(i)` gives the curve's
    slopes at a key; `ScaleTime` divides slopes.
  - `MotionModifiers.cs`: `MotionModifier` records (`LagModifier`, `JitterModifier`, `CycleModifier`,
    `OscillateModifier`), `MotionPose`, `Checked` and `Problem`, on `SceneItem.Modifiers` (immutable: set a new list).
    `MotionSolver` applies them after the path and IK (`Unmodified`, `Scale`); `LocalMatrix` uses `MotionScaleAt`.
  - `Lws/LwText.cs` is the line and envelope parser `LwsReader` and `Lws/MotionFile.cs` (.mot read, write and apply)
    share.
  - `Rendering/EnvironmentMap.cs`: an environment picture by direction (lat-long, turned by its heading), its 64 × 32
    blurred copy and irradiance table for lighting, loading and caching (`.hdr` through `Hdr`, else `TextureImages`),
    and the built-in `builtin:sky` and `builtin:studio`. `Rendering/RayTracer.cs` does every effect: depth of field and
    motion blur in the pixel loop (one `Tracer` per moment across the shutter), ambient occlusion, environment
    lighting (only the hidden light is sampled, against the irradiance table) and soft lights where rays hit.
  - `InverseKinematics.cs`: IK for bones (`Bone.IKGoal`, `FullTimeIK`, `IKAnchor`, `IKLocked`, `IKLimits` with
    `IKLimit` records), the chain (`Chain`, `Reach`), what can be a goal (`GoalProblem`), the CCD solver (`Solve`,
    in each bone's own heading, pitch and bank, from the keyed pose every frame) and `Bake`. `Motion.cs` is
    `MotionSolver` (`Scene.Motion`): what moves items beyond their keys, remembered per frame until the scene's version
    changes, safe across threads. `SceneItem.LocalMatrix` uses `MotionRotationAt` (keys, or the solved IK rotation);
    `RotationAt` stays the keys' value, which keying, the panels and `look_at` use.
  - `FollowPath.cs`: path following. `FollowPath` (on `SceneItem.Path`: the points, closed, align, look-ahead,
    banking; immutable, `Checked`, `Problem`, `Bake`), the keyable `SceneItem.PathPosition` envelope
    ("path.position"), and `MotionPath`, the centripetal Catmull–Rom curve measured by length (`At`, `PointAt`,
    `TangentAt`, `TurnAt`, `Polyline`). `MotionSolver` puts a following item where its path says
    (`MotionPositionAt`, used by `LocalMatrix`; `PositionAt` stays the keys').
  - `SceneSnapshot.cs` (`Scene.Snapshot()`) puts a scene back as it was, items by reference (with every setting an
    edit can change, morphs' and displacements' included: extend it when you add one); `Scene.Copy` makes one
    that shares nothing that changes, to render on another thread; `Scene.CopyItems` copies chosen items into a scene,
    from it (cloning) or another (merging), an object with its bones and the links among the copies kept (both go
    through `CopyOf` and `Relink` in `Scene.cs`: extend those when an item gets a new setting or a reference to another); `Scene.Batch` raises `Changed` once for many edits.
- `src/AVAModeler.Layout/`: Layout's state without its controls, so the workspace and the MCP server share it.
  - `LayoutSession.cs`: the scene being worked on, its frame, chosen item and auto-key, the Modeler's object in the
    scene, loading and saving scenes (with an object loader passed in), the modified flag, and the scene's undo
    (`Edit`, `BeginEdit`/`EndEdit`, `Undo`, `Redo`).
  - `SceneUndo.cs`: Layout's undo history (`LayoutSession.History`), `SceneSnapshot`s shared by the person and agents.
  - `SceneChannels.cs`: every animatable channel an item has, by name (`Of`, `Find`, `Keyed`): motion, a light's
    intensity and flare intensity, a camera's zoom, an object's dissolve, morph weights, displacement amplitudes. The dope sheet, the graph editor and the MCP census read it,
    so a new animatable setting is added there once.
  - `CurveEditor.cs`: the graph editor without its control (shown curves from `SceneChannels`, chosen `KeyId`s, `Set`,
    `SetSpan`, `SetBehavior`, drag and nudge, copy and paste, delete and scale through `KeyTiming`). `Keying.SetSpan` is
    the one rule for changing a key's span (Bezier fills in the curve's slopes); `KeyTiming.Retime`'s `timeFactor`
    divides slopes when scaling. `LightEffectsEditing.cs` holds the session's one-step setters for a lights panel
    (`SetLensFlare`, `SetFlareIntensity`, `SetVolumetric`, `SetShadows`); `LayoutSession` has `SetModifiers`,
    `LoadMotion`, `SaveMotion`, `SetImages`, `SetCompositing` and `SetDissolve`, each one undo step.
  - `DopeSheet.cs`: the dope sheet without its control (rows, `KeyId`s, choosing keys by click, Shift and box, copy and
    paste) and `KeyTiming` (resolving keys to envelopes, `Retime`, `Shift`, `Scale` and `Delete`, snapped to whole frames,
    each one undo step), which agents' `shift_keys` and `scale_keys` use too.
  - `SceneDrawing.cs` (what Layout's views draw, and looking through a scene camera: `LookThrough` copies its heading,
    pitch and bank into the `ViewCamera`, whose `Bank` rolls its basis, so the view tilts as the ray tracer's picture
    does) and `Keying.cs` (how values become keys: one rule for Layout's controls and agents alike).
  - `SceneGltf.cs`: Scene › Export glTF and the `export_scene` tool: the scene's items as nodes in their hierarchy,
    cameras, KHR_lights_punctual lights and motion sampled per frame, through `GltfBuilder`.
  - `AnimationRender.cs` (Render Animation's work: a run of frames from a `Scene.Copy` into numbered PNGs on a worker
    thread, with progress and cancelling) and `MovieEncoder.cs` (joins the frames into MP4 and GIF with the ffmpeg on
    the PATH, using `carbon_atom.py`'s arguments; render_animation's movie parameter and make_movie use it too).
- `src/AVAModeler.Mcp/`: the MCP server for AI agents (on the official C# SDK, `ModelContextProtocol.Core`). No UI.
  - `ModelerTools.cs` is every Modeler tool, `LayoutTools.cs` every Layout (scene) tool and `DeformTools.cs` the
    tools that deform scene objects (`copy_layer`, morphs, displacements, `skelegons_to_bones`), one partial class in
    the SDK's attribute model; `ModelerServer.cs` builds the server, its instructions to agents and its prompts.
    `Recipes.cs` holds the prompts (saved recipes), one static method each in the SDK's `[McpServerPrompt]` model. `ToolCatalog.cs`
    describes the Modeler's tools from the registry and `[Param]`s; `SceneCensus.cs` describes the scene and reads
    agents' item and channel names (`morph.<name>` and `displacement.<number>.amplitude` among them; the list itself is
    `SceneChannels.Of` in AVAModeler.Layout: extend it, and `Channel` here if the name needs parsing, when an item gets
    a new animatable setting, and get_keys, set_key, delete_key, set_end_behavior, shift_keys, scale_keys and the dope
    sheet pick it up).
  - `ModelerHost.cs`: `IModelerHost` runs the handlers' work on the document's thread, with the command API
    (`RunAsync`) or with Layout's `LayoutSession` beside it (`RunSceneAsync`). `HeadlessHost` owns a document and a
    `LayoutSession`; `LiveHost` runs on the app's UI thread through `IUiThread`, against `ILiveModeler.Commands` and
    `ILiveModeler.Layout`, and waits while `ILiveModeler.Busy` says the person is busy.
  - `ServerProfile.cs`: which server an app serves. `ServerProfile.Modeler` is AVA Modeler's own (every tool and
    recipe, `ModelerServer.Instructions`); another app (AVALightWave, the standalone Layout) gives its own name,
    instructions, a tool and prompt filter (`ToolGroups.Scene`, the tools that need only the scene), description
    changes, tools and recipes of its own, its live pipe and `HasModelerObject = false` (add_object then needs a file).
    `ModelerServer.Options`, `Definitions`, `Prompts`, `RunAsync`, `LiveListener`, `LiveBridge`, `McpCommandLine` and
    the chat's `McpToolBridge` all take a profile, AVA Modeler's unless given. A new tool goes in `ToolGroups.Scene` or
    `ToolGroups.Modeling` (a test checks each tool is in one); the `Recipes.Recipe` builder and `Recipes.Number`,
    `Whole`, `Colour` and `LightRig` are public so another app's recipes are written the same way.
  - `LiveListener.cs` (the app's local pipe, with a one-line handshake), `LiveBridge.cs` (`--mcp --attach`, an MCP
    proxy to that pipe), `McpCommandLine.cs` (`--mcp`), `Workspace.cs` (the working folder's file rules),
    `Pictures.cs` (render_view and render), `CheckReport.cs` (check_mesh's JSON, with `how_to_fix`), `ScenePictures.cs` (render_scene_view, render_scene, render_animation; make_movie is in LayoutTools.cs).
- `src/AVAModeler.Mcp/`'s newer tool files: `MotionModifierTools.cs` (add, set and remove_motion_modifier),
  `LightEffectsTools.cs` (set_lens_flare, set_volumetric_light, set_shadow_map, and `LightEffectsCensus`) and
  `CompositingTools.cs` (load_image, remove_image, set_compositing, set_dissolve), all in `ToolGroups.Scene`.
- `src/AVAModeler.Hub/`: the hub, the live link with AVALightWave (the standalone Layout, which builds on this engine
  as a submodule), no UI. `HubProtocol` (each app's per-user pipe, `PipeName`; one JSON line asked, one answered),
  `HubMessage` (`hello`, `edit_object`, `object_saved`, `add_object`, `object_changed` for live sync; files by full path, a link id tying a scene object
  to the document modeled), `HubListener` (an app's end; refuses a second copy), `HubClient` (sends, with a short
  timeout: `HubUnavailableException` when the other app isn't there), `HubFiles` (the owner-only `hub/` folder in the
  settings folder for objects handed over without a file) and `AppLauncher` (finds the other program: a setting,
  `AVAMODELER_PATH`/`AVALIGHTWAVE_PATH`, beside this one, a sibling folder, the PATH; starts it with
  `file --hub-link id`).
- `src/AVAModeler.Chat/`: the AI Chat panel's logic, no UI, with two providers (`ChatProvider`): Claude on the
  official Anthropic C# SDK (NuGet `Anthropic`), and any OpenAI-compatible Chat Completions server (Ollama's `/v1`, LM
  Studio, vLLM, llama.cpp, a gateway) on plain `HttpClient` and System.Text.Json (no OpenAI SDK).
  - `ChatSession.cs` is the agent loop, shared by both providers: a streamed request with the MCP server's tools,
    each tool call run through the bridge, results back, until the model stops, 60 tool calls (carrying on by itself
    `AutoContinues` times; the panel's Auto-continue box sets 9 and is remembered in Preferences), or Stop. Changing
    `Provider` starts a new chat. `ChatEntries.cs` is what the panel shows; `ChatErrors.cs` turns failures into one
    plain line.
  - `ChatBackend.cs` is `IChatBackend`, one provider's conversation in its API's shape and one streamed request.
    `AnthropicBackend.cs` is Claude's (content blocks, pictures as image blocks in each `tool_result`, failures as
    `is_error`, cache breakpoints on the system prompt, the last tool and the conversation: Anthropic only).
    `OpenAICompatibleBackend.cs` is the other: tools as `function` definitions from the MCP schemas,
    `tool_choice: "auto"`, `delta.content` and `delta.tool_calls` fragments put together by index, results as
    `role: "tool"` messages, pictures as `image_url` data-URL parts in a user message after them (or a line of text
    when `ModelSeesPictures` is off), arguments that aren't JSON sent back as an error without running, and
    `ChatTools.Core` (in `ChatModels.cs`) when `CoreToolsOnly` is on. `OpenAICompatibleClient.cs` is its HTTP:
    `POST {base}/chat/completions` as server-sent events (or one whole JSON answer), `GET {base}/models`, and every
    failure as an `OpenAICompatibleException` already worded for the person (server named by host and port only).
  - `ChatHistory.cs`: saved chats. `StoredChat` (the panel's lines as `StoredEntry`s and the conversation as
    provider-neutral `StoredTurn`s: user words, a reply with its `StoredCall`s, the calls' `StoredResult`s), `ChatHistory`
    (one `<id>.json` a chat in a folder, pictures by content hash in `<id>/`, size caps, owner-only files, `List`,
    `Load`, `Save`, `KeepImage`, `ReadImage`, `Delete`, `Clear`) and `ChatMarkdown` (export). `ChatSession` keeps the
    neutral log as it talks (`ToStored`, `Open`): opening a chat with the provider in use gives its backend the turns
    again (`IChatBackend.AddAssistant` puts back a reply in that API's shape); with the other provider it's
    `ReadOnly` until that provider is chosen again. Calls left without results are answered "not run" on opening.
  - `ToolBridge.cs`: `McpToolBridge` is an in-process MCP client talking to `ModelerServer` over in-memory pipes, built
    over the app's `LiveHost`, so the chat and outside agents share one code path. `ChatModels.cs` has the model ids
    and `ChatPrompt` (the server's instructions plus a few in-app lines, and notes for the core tool set and for a model
    that can't see pictures), used by both providers. `ApiKeyStore.cs` is the keys: `ForApp` (Anthropic's) and
    `ForServer` (an OpenAI-compatible gateway's, its own file and no environment variable).
- `src/AVAModeler.Modeler.UI/` and `src/AVAModeler.Layout.UI/`: the two workspaces. `Session.cs` holds
  `Preferences`. The Layout workspace shows a `LayoutSession` (from `AVAModeler.Layout`) and keeps no state of its own.
  - `AnimationWindow.cs` is Render › Render Animation (F10), a thin window over `AnimationRender` and `MovieEncoder`.
  - `DopeSheetPanel.cs` is the dope sheet (D, View › Dope Sheet), shown in the graph editor's place: a bar (Channels,
    Copy, Paste, Delete, Scale ×) and `DopeSheetView`, which draws the rows and diamonds and turns clicks, boxes and
    drags into `DopeSheet` calls (`PointOf` gives a key's diamond for tests).
  - `SceneSettingsPanel.cs` is Scene › Scene Settings' contents (frames, ambient, backdrop, ambient occlusion and
    the environment), one undo step on OK; the camera's depth of field and motion blur and the lights' soft-shadow
    settings are in `LayoutWorkspace`'s item panel, with Focus on calling `LayoutSession.FocusOn`.
  - `ItemPropertiesPanel.cs` is that item panel (the chosen item's settings, each change one undo step), with `Extras`
    for a host's own controls; `SceneView.cs` is the view, whose `DragMode` (`ItemDrag`: the default MoveOrRotate,
    or Move, Rotate, Size, Stretch), `Axes` and `CanPick` let a standalone Layout (AVALightWave) have classic Layout's
    mouse modes, axis buttons and edit modes; it never picks or drags a `SceneItem.Locked` item.
  - `MeshInfoPanel.cs` is Mesh Info (W), docked beside the numeric panel and hidden until asked for: a thin view of
    `MeshCheck`, checking again (on the dispatcher, after a burst of changes) whenever the document changes while it's open.
  - `PrintWindow.cs` is File › 3D Print (and the Objects tab's 3D Print…): a thin window over `PrintJob` (the settings
    read into a `PrintSetup` and kept in `Preferences.Print`, slicing a `Document.Copy` on a worker, `LayerPreview`
    drawing one layer on the bed, `PrintPreview3D` the print stacked up to it in 3D through the software renderer's
    overlay lines (bed millimetres to the renderer's metres with `ToScene`), Save G-code). Agents slice with `slice_to_gcode` in `ModelerTools.cs`, which works
    the same way (copy on the host, slice and write off it, inside the working folder).
  - `BackdropPanel.cs` is Backdrop (D), docked the same way: a view chooser, Browse, the numbers and check boxes,
    Fit to Object, Size from Pixels, and Save and Load Setup, each change going straight to `ModelerSession.SetBackdrop`.
    `ModelerSession` keeps backdrops across New, gives an opened object the ones `BackdropMemory` has for its file, and
    writes them there when they change (`RememberBackdrops`); `MainWindow` gives it the real memory only outside tests.
- `src/AVAModeler.Chat.UI/`: `ChatPanel.cs`, the AI Chat panel's controls (a library so another app on this engine,
  AVALightWave, docks the same panel; `ChatWording` names the app in its lines, AVA Modeler's by default, and
  `ChatSession.SystemPrompt`, `CoreTools` and `CoreToolsNote` let that app give its own prompt and core tools).
- `src/AVAModeler/`: the application, one window hosting both workspaces. `Program.cs` sends `--mcp` to the MCP
  server before Avalonia starts; `LiveAgents.cs` is the app's side of live mode (it hands the live host both the
  Modeler's session and the window's `LayoutSession`, which exists before Layout is first shown, and gives the chat
  its own `LiveHost`). The AI Chat panel (`AVAModeler.Chat.UI`'s `ChatPanel`) has its controls (the line saying which provider and model it
  uses, Setup… with the provider, base URL, model and Refresh, Core tools only and Model sees pictures, all kept in
  `Preferences.Chat*`; Edit › Preferences has them too and `ModelerWorkspace.PreferencesApplied` hands them back);
  `MainWindow` docks it on the right of whichever workspace is on screen (F4). Its History (Keep chat history,
  `Preferences.ChatKeepHistory`, off by default; the list, Open, Export…, Delete, Clear History, and the read-only bar)
  works on `ChatPanel.History`, which `MainWindow` sets to `Preferences.ChatsFolder` only outside tests, saving after
  each request, on New Chat and on closing, and opening the last chat on start.
  `ModelerHub.cs` is the app's end of the hub (`MainWindow.Hub`, joined at start while `Preferences.HubEnabled`):
  `edit_object` opens the object (refused over unsaved work) and links the document; live sync (`object_changed`) is a
  debounce on `Document.Changed` (`LiveSyncDelay`), held back while `ModelerSession.ToolDragging`, then a
  `Document.Copy` on the UI thread and the save and send on a worker (`HubFiles.LivePathFor`, `HubFiles.WriteWhole`),
  one send in flight with one more after, a geometry and surface fingerprint so selection-only changes send nothing, and
  no retry while AVALightWave is away; `Status` and `StatusChanged` come from a connection-only presence check every
  `PresenceInterval` plus each send, shown on `StatusBar.HubText` and `LayoutWorkspace.HubText`; `ModelerWorkspace.Saved` sends
  `object_saved`; File › Send to AVALightWave (`ModelerWorkspace.SendToLightWave`) sends `add_object`, or starts
  AVALightWave with the file and `--hub-link`. A window made for tests never joins the hub or reaches a real
  AVALightWave: tests give it pipes, a settings folder and a launcher of their own.
- `samples/`: `StarPlugin` (an example plugin), Lua macro scripts (`rocket-blueprints.lua` sets up backdrops), texture
  images and the rocket blueprints (each drawn by a standard-library Python script beside them), and
  `mcp/carbon_atom.py` (a scene built entirely through the MCP server, with its saved object, scene and render).
- `tests/`: one xUnit v3 project per source project. `AVAModeler.UI.Tests` uses `Avalonia.Headless.XUnit` with
  Skia drawing, so it can capture screenshots.

## Rules that keep it working

- **No UI below the workspaces.** Core, Formats, Geometry, Tools, Rendering, Scene and Layout carry no Avalonia, so
  tools run from tests and macros without a window. Put new behaviour there and keep the UI projects thin: scene
  editing, scene files and what Layout's views draw live in `AVAModeler.Layout` and `AVAModeler.Scene`, not in
  `AVAModeler.Layout.UI`.
- **Tools follow one lifecycle.** A tool's parameters are properties marked `[Param]`, which feed the numeric panel,
  dragging and scripts alike. `Begin`, then `Update` (recomputed from the untouched originals each time), then
  `Commit` (one undo step) or `Cancel`. Any public `Tool` with a parameterless constructor in `AVAModeler.Tools`
  or in a plugin is registered by reflection.
- **Edits go through the document.** Change a mesh through `Document.Edit` (or `Snapshot` and `Commit`) so the
  edit is one undo step and the selection is pruned.
- **LightWave conventions.** The space is left-handed with clockwise winding. Keep LWOB round trips byte-identical:
  `AVAModeler.Formats.Tests` checks this. Right-handed formats convert at the file's edge, never inside: OBJ and glTF
  negate Z (`GltfBuilder.ToGltf`/`FromGltf`), STL swaps Y and Z for Z up and writes millimetres (`Stl.ToStl`/`FromStl`, × and ÷ 1000), and each reverses
  every face's winding, since both are mirrorings. A transform goes over as `F · M · F` with F the Z flip
  (`GltfBuilder.Decompose`). Tests check a known triangle faces the same way after each conversion.
- **Edge and face tools leave a valid mesh.** New faces wind against the faces beside them (LightWave's clockwise way),
  so a closed mesh stays closed with consistent winding and a positive volume; their tests run `MeshCheck` after each
  tool on closed inputs, check UVs on the new points and that undo puts the mesh back exactly. Find holes with the mesh
  check's boundary edges (`Holes.BoundaryLoops`), not a second detector.
- **A tool that can't work on the selection throws.** It throws an `InvalidOperationException` (or
  `ArgumentException`) saying what to select; `Commands.Run` cancels it, agents get the message, and the UI shows it in
  the status bar, also when the error comes from a drag or a numeric-panel edit of an armed tool.
- **Repeatable randomness.** Tools such as Jitter hash a `Seed` parameter rather than using `Random`, so the same
  seed gives the same result. The ray tracer's samples (antialiasing jitter, lens, shutter, occlusion, environment and
  soft-light rays) are hashes of the pixel, the ray and a "dimension" per effect, so a render is the same on any
  number of threads. Procedural textures use `Noise` (Perlin's improved noise with his fixed table, whose
  reference values a test pins), never `Random`, so renders are the same every time; wave sources and phases come
  from the same table.
- **Procedurals have three twins.** `ProceduralTexture.Pattern` (C#), the GLSL `pattern()` in `GlRenderer`'s texture
  shader, and the settings catalog in `Procedurals.All`. Change a pattern's sums in both places (the Rendering tests
  compare the OpenGL picture with the software one, through the shader's C# twin and on a real context); a new kind
  gets a `ProceduralKind` number for the shader, its settings in LWOB slots (TFP0 to TFP3, TIP0) and an entry in
  `ModelerTools.ProceduralKinds` (a test checks the description lists every kind and setting with its default).
- **Render effects are off by default and change nothing when off.** `EffectsBaselineTests` pins the hashes of a busy
  scene rendered before the effects existed: keep every off path's arithmetic as it was (a new effect gets its own
  branch). A new effect setting goes in its record (or the light or camera), `Scene.Copy`, `SceneSnapshot`,
  `SceneJson` (written only when not the default), `.lws` where LightWave has a place for it, `SceneCensus` and the
  MCP tools' descriptions with its units and default, and the README's known limits say where it isn't kept.
- **OpenGL tests may skip, and run one at a time.** `OffscreenGl` (public, in AVAModeler.Rendering, so other apps' tests
  use it too) makes and closes its contexts one at a time under a process-wide lock and terminates the shared EGL
  display only after the last one; the test classes that use it share `[Collection("OpenGL")]`. `OffscreenGl` makes a windowless EGL context where Mesa offers one. Without it, the
  OpenGL comparison tests skip rather than fail.
- **Agents use the command layer, never the mouse.** MCP handlers reach the document only through
  `IModelerHost.RunAsync` and `Commands` (tools by name, `Macros`), and the scene only through
  `IModelerHost.RunSceneAsync` and `LayoutSession`, so the same handlers serve headless and live mode. Every change an
  agent makes to the object is one undo step, and a failed call leaves the document as it was: `Commands.Run` cancels
  a tool that throws, and `Document.Group` makes several edits one step (or none, if they fail). Every Layout tool that
  changes the scene runs through `DoScene`, which makes the call one step on Layout's shared history
  (`LayoutSession.Edit`, named "Agent: …"): if the call throws, the scene is put back as it was and no step is left
  (all or nothing). `undo_scene` and `redo_scene` work on that same history, as the person's Ctrl+Z does. Snapshots
  keep morph targets by reference, so a tool that changes a target puts a new mesh in (`add_morph` and `set_morph`
  copy the layer) and never edits one in place. Errors go
  back as `McpException`s whose messages say what to do instead.
- **A key's new member** goes in `EnvelopeKey` with a default that leaves evaluation unchanged, in SceneJson's `KeyDto`
  (written only when not the default), in `.lws` where LightWave has a place for it, and in `SceneCensus.Keys`.
- **Motion beyond keys goes through `Scene.Motion`** (motion modifiers included: a new modifier kind needs a record with
  `Checked`, `Apply` and `Relinked` if it refers to another item, the `ModifierDto` in SceneJson, the census's
  `Modifier()` and the MCP builder; with no modifiers `MotionSolver` takes its old path unchanged). Anything that moves an item other than its own keys (path
  following, full-time IK) is worked out by `MotionSolver` and reached through `SceneItem.LocalMatrix`, so views, the ray tracer,
  deformation, exports and the MCP census all see it; never write solved values into envelopes except as an explicit
  bake. An item that refers to another (a bone's `IKGoal`, a path's points) has the reference remapped in `Scene.Copy`, kept in
  `SceneSnapshot`, written by index in `.avascene`, and cleared by `Scene.Remove` when that item goes; check what it
  may refer to (`InverseKinematics.GoalProblem`, `FollowPath.Problem`) so it can't chase itself.
- **Agents see the app's numbers.** MCP tools number layers 1 to 10, as the layer buttons and Lua macros do, and give
  every colour as [r, g, b] whole numbers from 0 to 255, as surfaces and the Layout panel do (`Json.Colour`,
  `Json.SceneColour` and `Json.ColourJson`). Convert at the MCP edge: inside, layers are indexed from 0 and Layout's
  colours are 0 to 1. Points and polygons stay indexed from 0.
- **Scene edits are undo steps.** Change the scene inside `LayoutSession.Edit(label, …)` (or between `BeginEdit` and
  `EndEdit` for a drag, press to release), or through the session's own methods (`SetVector`, `CreateKey`,
  `DeleteKey`, `Add`, `Remove`, which are steps already), so the change is one named step on `History` and undo stays
  consistent: a change made straight on the `Scene` isn't a step, and undoing an older step would take it back too.
  Edits nest (an inner one is part of the outer), a failed one is put back, and new or loaded scenes clear the history.
- **Live mode runs on the UI thread.** `LiveHost` marshals every call through `IUiThread` (the Avalonia dispatcher in
  the app), so the views redraw through the usual `Document.Changed` and the person's Ctrl+Z undoes agents' steps (in
  Layout, their scene steps). It waits while a tool is armed or a modal dialog is open (`Dialogs.ShowModal` counts
  them); pictures are drawn from a
  `Document.Copy` (or, for the scene, a `Scene.Copy` with copied documents) off that thread. Scene calls don't switch
  the window to Layout; `LiveAgents.Report` tells Layout to show its item list afresh (`ChangedElsewhere`).
- **The live listener is local and per user.** It listens on a named pipe opened with `PipeOptions.CurrentUserOnly`
  (a Unix socket on Linux and macOS), so no token is needed; it is off until the person allows agents. Don't add a
  network listener. Paths agents give stay inside the working folder (`Workspace`), and nothing is deleted.
- **Merged undo steps.** `LayoutSession.Edit(..., merge: true)` (`SceneUndo.Record(before, label, merge)`, `CanMerge`)
  folds a step into the one before when it has the same label, the last thing that happened to the history was that
  step being recorded, the scene is exactly as that step left it, and it isn't the saved state: for repeated updates
  such as AVALightWave applying `object_changed`. Use it for nothing a person would want to undo piece by piece.
- **OpenGL views size from the framebuffer.** `GlSurface` asks `GlFramebuffer` (Rendering) for the bound
  framebuffer's colour attachment size, then the viewport, and only then the bounds times the scaling
  (`GlFramebuffer.Choose`), and sizes the camera from that: the toolkit's framebuffer can differ from bounds × scaling
  (at 2× on Wayland the picture filled only the bottom-left). `LastFramebuffer` and `Description` say which it used.
- **SceneView's OpenGL hook.** `SceneView.DrawsScene` false leaves the scene to a surface beneath it (`CurrentView()`
  gives what it would draw, `Redrawn` says when); it still draws its guides and label and takes the mouse. `GlSurface`
  has `CameraSource`, `Ready` and `Initialised`; where OpenGL can't start, Avalonia never calls `OnOpenGlInit` (no
  `Failed`), so a host checks `Ready` and goes back to software.
- **The hub is local and per user too.** Each app listens on its own pipe (`HubProtocol.PipeName`), opened with
  `PipeOptions.CurrentUserOnly`; messages name files by full path and never carry geometry or anything else; a
  missing app costs a short timeout, never a hang. Don't add a network listener. Tests use pipe names of their own.
- **The chat never calls the real API or a real model server in tests, and never shows a key.**
  `AVAModeler.Chat.Tests` and the UI tests give the SDK a fake `HttpMessageHandler` (`FakeClaude`, scripted
  server-sent events) and a made-up key, and the OpenAI-compatible client another (`FakeModelServer`, scripted chunks,
  errors and refused connections; the panel's `ServerFactory` takes it); a window made for tests
  (`loadPlugins: false`) reads no environment variable and no key file. The Anthropic key comes from
  `ANTHROPIC_API_KEY`, else the `anthropic-api-key` file beside `preferences.json`; a gateway's key only from the
  `openai-compatible-api-key` file beside it (both written owner-only, chmod 600, on Linux and macOS; never in
  `preferences.json`). Never log either, put it in an exception, a status line, a chat line or a saved chat, or show more than
  `ApiKeyStore.Mask`: `OpenAICompatibleClient` masks any copy a server echoes back, and sends the key only in the
  Authorization header. The OpenAI-compatible provider contacts only the base URL the person set (`…/chat/completions`,
  and `…/models` on Refresh): no other network call, no telemetry. The chat's tools are the MCP server's, unchanged: add a tool to `ModelerTools`/`LayoutTools` and
  the chat has it too (a test checks the lists match).
- **Preferences live in the app-data folder.** `preferences.json`, `backdrops.json`, `chats/`, `plugins/` and
  `autosave/` sit under `AVAModeler` in `SpecialFolder.ApplicationData`. Tests use temporary folders and never touch
  them (a test that needs the backdrop memory gives the session a `BackdropMemory` in a temporary folder, and one that
  needs chat history gives the panel a `ChatHistory` in one; a test window's panel has none). `Preferences.Save()`
  writes only to the file the preferences were loaded from (`Location`): ones made with `new`, as test windows' are,
  save nowhere, so a test that opens or saves a file can't add it to the person's recent files.
- **The screen door has twins.** `Dither.Keeps` (C#, in ViewScene.cs) and the GLSL `dissolved()` in `GlRenderer`
  share `Dither.Bayer` (`Dither.Glsl`); pixels count from the top left and the shader flips `gl_FragCoord.y` with
  `viewHeight`. Change the dither in one place.
- **Never block the UI thread on the pipe or the disk** (live sync, presence checks): copy on the UI thread, work on a
  worker.
- **Backdrops are a display setting, not the object.** They're never written into object or scene files (LWOB round
  trips stay byte-identical) and never undo steps; `Document.Copy` carries them, so pictures drawn off the UI thread
  show them. Both renderers draw them from the same `BackdropQuad` (the software one with `FillTextured`, OpenGL
  through the texture shader), so change how they look in `BackdropDrawing` and `BackdropMapping.Adjusted`, not in a
  renderer. A missing image draws the placeholder checker at full strength and `BackdropMapping.Problems` words the
  status-bar note. Agents' backdrop images come through `Workspace.Resolve` (inside the working folder, image types
  only, read and never written).

## Making a change

- Add or update tests in the matching test project with every change. `dotnet test` must pass, with no warnings.
- A new tool goes in the right file of `AVAModeler.Tools`. Its key, tab and numeric panel then come from its
  `Name`, `Tab` and `[Param]` properties. Give it a `Summary` (what it does, for AI agents and the reference) and a
  `Description` on any `[Param]` whose label doesn't say enough; a test checks every tool has a summary.
- A new command an agent should have goes in `ModelerTools.cs` (or, for the scene, `LayoutTools.cs`) as an
  `[McpServerTool]` method with careful `[Description]`s (units, axes, conventions), working through `Do(...)` (or
  `DoScene(...)`) so it runs on the host and its errors are explained. Test it in `AVAModeler.Mcp.Tests` (the handler directly, and through a client where the wiring matters).
  A few of those tests start the built app as a real `--mcp` process (the test project builds it).
- Agents lay procedural textures with `set_procedural` (`Commands.SetProcedural`), macros with `proctex()`; both check
  every setting on a spare layer first, so a bad one changes nothing.
- A new MCP tool also goes in `ToolGroups.Scene` (it needs only the scene, so AVALightWave serves it) or
  `ToolGroups.Modeling` (in `ServerProfile.cs`); `ProfileTests` fails until it's in one.
- A recipe (an MCP prompt) goes in `Recipes.cs`. Write each step's tool call on a line of its own after `→ `,
  with its arguments as JSON (the `Recipe` builder does this). `RecipeTests` fetches every prompt through a client and
  runs its calls through the server, which checks the arguments against each tool's schema, then checks the result:
  add the new one to its cases and to its checks. Prompt arguments arrive as text: read numbers and colours with
  `Recipes.Number`, `Whole` and `Colour`.
- When an agent should check its own work, say so in `ModelerServer.Instructions` and give it something to measure:
  `check_mesh` is how an agent learns that a solid is closed, faces outward and has the volume it meant.
- If the change shows on screen, refresh the screenshots with the command above and look at them.
- Update `README.md` (features, keys, the test table, known limits) to match.

## CI and releases

`.github/workflows/build.yml` builds and tests every push and pull request. It then publishes self-contained
single-file builds for win-x64/x86/arm64, linux-x64/arm64/arm and osx-x64/arm64. Every run keeps the builds as
workflow artifacts. Only pushes to `main` replace the assets of the `latest` pre-release, and a `v*` tag makes a
proper release. After a release it asks the portfolio site to rebuild, if the `SITE_REFRESH_TOKEN` secret is set.
