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
- `src/AVAModeler.Modeler.UI/` and `src/AVAModeler.Layout.UI/`: the two workspaces. `Session.cs` holds
  `Preferences`.
- `src/AVAModeler/`: the application, one window hosting both workspaces.
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
- **Preferences live in the app-data folder.** `preferences.json`, `plugins/` and `autosave/` sit under
  `AVAModeler` in `SpecialFolder.ApplicationData`. Tests use temporary folders and never touch them.

## Making a change

- Add or update tests in the matching test project with every change. `dotnet test` must pass, with no warnings.
- A new tool goes in the right file of `AVAModeler.Tools`. Its key, tab and numeric panel then come from its
  `Name`, `Tab` and `[Param]` properties.
- If the change shows on screen, refresh the screenshots with the command above and look at them.
- Update `README.md` (features, keys, the test table, known limits) to match.

## CI and releases

`.github/workflows/build.yml` builds and tests every push and pull request. It then publishes self-contained
single-file builds for win-x64/x86/arm64, linux-x64/arm64/arm and osx-x64/arm64. Every run keeps the builds as
workflow artifacts. Only pushes to `main` replace the assets of the `latest` pre-release, and a `v*` tag makes a
proper release. After a release it asks the portfolio site to rebuild, if the `SITE_REFRESH_TOKEN` secret is set.
