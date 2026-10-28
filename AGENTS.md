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
dotnet test tests/AVAModeler.Mcp.Tests            # the MCP server: handlers, a whole agent session, live mode, processes
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
- `src/AVAModeler.Mcp/`: the MCP server for AI agents (on the official C# SDK, `ModelContextProtocol.Core`). No UI.
  - `ModelerTools.cs` is every MCP tool, in the SDK's attribute model; `ModelerServer.cs` builds the server and its
    instructions to agents. `ToolCatalog.cs` describes the Modeler's tools from the registry and `[Param]`s.
  - `ModelerHost.cs`: `IModelerHost` runs the handlers' work on the document's thread. `HeadlessHost` owns a document;
    `LiveHost` runs on the app's UI thread through `IUiThread` and waits while `ILiveModeler.Busy` says the person is
    busy.
  - `LiveListener.cs` (the app's local pipe, with a one-line handshake), `LiveBridge.cs` (`--mcp --attach`, an MCP
    proxy to that pipe), `McpCommandLine.cs` (`--mcp`), `Workspace.cs` (the working folder's file rules),
    `Pictures.cs` (render_view and render).
- `src/AVAModeler.Modeler.UI/` and `src/AVAModeler.Layout.UI/`: the two workspaces. `Session.cs` holds
  `Preferences`.
- `src/AVAModeler/`: the application, one window hosting both workspaces. `Program.cs` sends `--mcp` to the MCP
  server before Avalonia starts; `LiveAgents.cs` is the app's side of live mode.
- `samples/`: `StarPlugin` (an example plugin), Lua macro scripts and texture images.
- `tests/`: one xUnit v3 project per source project. `AVAModeler.UI.Tests` uses `Avalonia.Headless.XUnit` with
  Skia drawing, so it can capture screenshots.

## Rules that keep it working

- **No UI below the workspaces.** Core, Formats, Geometry, Tools, Rendering and Scene carry no Avalonia, so tools
  run from tests and macros without a window. Put new behaviour there and keep the UI projects thin.
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
  `IModelerHost.RunAsync` and `Commands` (tools by name, `Macros`), so the same handlers serve headless and live mode.
  Every change an agent makes is one undo step, and a failed call leaves the document as it was: `Commands.Run`
  cancels a tool that throws, and `Document.Group` makes several edits one step (or none, if they fail). Errors go
  back as `McpException`s whose messages say what to do instead.
- **Live mode runs on the UI thread.** `LiveHost` marshals every call through `IUiThread` (the Avalonia dispatcher in
  the app), so the views redraw through the usual `Document.Changed` and the person's Ctrl+Z undoes agents' steps. It
  waits while a tool is armed or a modal dialog is open (`Dialogs.ShowModal` counts them); pictures are drawn from a
  `Document.Copy` off that thread.
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
- A new command an agent should have goes in `ModelerTools.cs` as an `[McpServerTool]` method with careful
  `[Description]`s (units, axes, conventions), working through `Do(...)` so it runs on the host and its errors are
  explained. Test it in `AVAModeler.Mcp.Tests` (the handler directly, and through a client where the wiring matters).
  A few of those tests start the built app as a real `--mcp` process (the test project builds it).
- If the change shows on screen, refresh the screenshots with the command above and look at them.
- Update `README.md` (features, keys, the test table, known limits) to match.

## CI and releases

`.github/workflows/build.yml` builds and tests every push and pull request. It then publishes self-contained
single-file builds for win-x64/x86/arm64, linux-x64/arm64/arm and osx-x64/arm64. Every run keeps the builds as
workflow artifacts. Only pushes to `main` replace the assets of the `latest` pre-release, and a `v*` tag makes a
proper release. After a release it asks the portfolio site to rebuild, if the `SITE_REFRESH_TOKEN` secret is set.
