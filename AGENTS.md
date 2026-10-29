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
AVAMODELER_SHOTS=$PWD/Images dotnet test tests/AVAModeler.UI.Tests --filter Screenshot   # refresh the README's pictures
python3 samples/images/make_textures.py           # redraws the sample texture images
```

`Directory.Build.props` sets `TreatWarningsAsErrors`, nullable reference types, implicit usings and
`InvariantGlobalization` for every project: a warning fails the build, so keep it at zero.

## Layout

- `src/AVAModeler.Core/`: maths (`Vec`, `Mat4`), the n-gon `Mesh` with its UV maps, surfaces and textures.
  - `Document.cs` holds the layers, the selection and the `UndoStack`.
- `src/AVAModeler.Formats/`: LWOB/LWLO read and write, LWO2 read, VideoScape GEO and OBJ, on a shared `Iff` reader.
- `src/AVAModeler.Geometry/`: triangulation (LibTessDotNet), BSP booleans and drills, subdivision, and the
  extrude, lathe and bevel family.
- `src/AVAModeler.Tools/`: the tools and the scripting around them.
  - `Tool.cs` is the base class and `[Param]`. `Commands.cs` has the `ToolRegistry` and the command API.
  - `Macros.cs` runs Lua; the other files hold the tools, grouped by kind (`ModifyTools.cs`, `UVTools.cs` and so on).
- `src/AVAModeler.Rendering/`: `IRenderer` with the software and OpenGL (Silk.NET) renderers, view cameras and
  picking.
- `src/AVAModeler.Scene/`: Layout's model: items, envelopes, bones, LWS and JSON scenes, and the ray tracer.
  - `SceneSnapshot.cs` (`Scene.Snapshot()`) puts a scene back as it was, items by reference (with every setting an
    edit can change, morphs' and displacements' included: extend it when you add one); `Scene.Copy` makes one
    that shares nothing that changes, to render on another thread; `Scene.Batch` raises `Changed` once for many edits.
- `src/AVAModeler.Layout/`: Layout's state without its controls, so the workspace and the MCP server share it.
  - `LayoutSession.cs`: the scene being worked on, its frame, chosen item and auto-key, the Modeler's object in the
    scene, loading and saving scenes (with an object loader passed in), the modified flag, and the scene's undo
    (`Edit`, `BeginEdit`/`EndEdit`, `Undo`, `Redo`).
  - `SceneUndo.cs`: Layout's undo history (`LayoutSession.History`), `SceneSnapshot`s shared by the person and agents.
  - `SceneDrawing.cs` (what Layout's views draw, and looking through a scene camera) and `Keying.cs` (how values become keys:
    one rule for Layout's controls and agents alike).
- `src/AVAModeler.Mcp/`: the MCP server for AI agents (on the official C# SDK, `ModelContextProtocol.Core`). No UI.
  - `ModelerTools.cs` is every Modeler tool and `LayoutTools.cs` every Layout (scene) tool, one partial class in the
    SDK's attribute model; `ModelerServer.cs` builds the server and its instructions to agents. `ToolCatalog.cs`
    describes the Modeler's tools from the registry and `[Param]`s; `SceneCensus.cs` describes the scene and reads
    agents' item and channel names.
  - `ModelerHost.cs`: `IModelerHost` runs the handlers' work on the document's thread, with the command API
    (`RunAsync`) or with Layout's `LayoutSession` beside it (`RunSceneAsync`). `HeadlessHost` owns a document and a
    `LayoutSession`; `LiveHost` runs on the app's UI thread through `IUiThread`, against `ILiveModeler.Commands` and
    `ILiveModeler.Layout`, and waits while `ILiveModeler.Busy` says the person is busy.
  - `LiveListener.cs` (the app's local pipe, with a one-line handshake), `LiveBridge.cs` (`--mcp --attach`, an MCP
    proxy to that pipe), `McpCommandLine.cs` (`--mcp`), `Workspace.cs` (the working folder's file rules),
    `Pictures.cs` (render_view and render), `ScenePictures.cs` (render_scene_view, render_scene, render_animation).
- `src/AVAModeler.Modeler.UI/` and `src/AVAModeler.Layout.UI/`: the two workspaces. `Session.cs` holds
  `Preferences`. The Layout workspace shows a `LayoutSession` (from `AVAModeler.Layout`) and keeps no state of its own.
- `src/AVAModeler/`: the application, one window hosting both workspaces. `Program.cs` sends `--mcp` to the MCP
  server before Avalonia starts; `LiveAgents.cs` is the app's side of live mode (it hands the live host both the
  Modeler's session and the window's `LayoutSession`, which exists before Layout is first shown).
- `samples/`: `StarPlugin` (an example plugin), Lua macro scripts, texture images, and `mcp/carbon_atom.py` (a
  scene built entirely through the MCP server, with its saved object, scene and render).
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
  `AVAModeler.Formats.Tests` checks this.
- **Repeatable randomness.** Tools such as Jitter hash a `Seed` parameter rather than using `Random`, so the same
  seed gives the same result.
- **OpenGL tests may skip.** `OffscreenGl` makes a windowless EGL context where Mesa offers one. Without it, the
  OpenGL comparison tests skip rather than fail.
- **Agents use the command layer, never the mouse.** MCP handlers reach the document only through
  `IModelerHost.RunAsync` and `Commands` (tools by name, `Macros`), and the scene only through
  `IModelerHost.RunSceneAsync` and `LayoutSession`, so the same handlers serve headless and live mode. Every change an
  agent makes to the object is one undo step, and a failed call leaves the document as it was: `Commands.Run` cancels
  a tool that throws, and `Document.Group` makes several edits one step (or none, if they fail). Every Layout tool that
  changes the scene runs through `DoScene`, which makes the call one step on Layout's shared history
  (`LayoutSession.Edit`, named "Agent: …"): if the call throws, the scene is put back as it was and no step is left
  (all or nothing). `undo_scene` and `redo_scene` work on that same history, as the person's Ctrl+Z does. Errors go
  back as `McpException`s whose messages say what to do instead.
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
- **Preferences live in the app-data folder.** `preferences.json`, `plugins/` and `autosave/` sit under
  `AVAModeler` in `SpecialFolder.ApplicationData`. Tests use temporary folders and never touch them.

## Making a change

- Add or update tests in the matching test project with every change. `dotnet test` must pass, with no warnings.
- A new tool goes in the right file of `AVAModeler.Tools`. Its key, tab and numeric panel then come from its
  `Name`, `Tab` and `[Param]` properties. Give it a `Summary` (what it does, for AI agents and the reference) and a
  `Description` on any `[Param]` whose label doesn't say enough; a test checks every tool has a summary.
- A new command an agent should have goes in `ModelerTools.cs` (or, for the scene, `LayoutTools.cs`) as an
  `[McpServerTool]` method with careful `[Description]`s (units, axes, conventions), working through `Do(...)` (or
  `DoScene(...)`) so it runs on the host and its errors are explained. Test it in `AVAModeler.Mcp.Tests` (the handler directly, and through a client where the wiring matters).
  A few of those tests start the built app as a real `--mcp` process (the test project builds it).
- If the change shows on screen, refresh the screenshots with the command above and look at them.
- Update `README.md` (features, keys, the test table, known limits) to match.

## CI and releases

`.github/workflows/build.yml` builds and tests every push and pull request. It then publishes self-contained
single-file builds for win-x64/x86/arm64, linux-x64/arm64/arm and osx-x64/arm64. Every run keeps the builds as
workflow artifacts. Only pushes to `main` replace the assets of the `latest` pre-release, and a `v*` tag makes a
proper release. After a release it asks the portfolio site to rebuild, if the `SITE_REFRESH_TOKEN` secret is set.
