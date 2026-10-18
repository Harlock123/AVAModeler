# AVA Modeler

[![Build](https://github.com/Harlock123/AVAModeler/actions/workflows/build.yml/badge.svg)](https://github.com/Harlock123/AVAModeler/actions/workflows/build.yml)

A cross-platform recreation of the classic standalone 3D modeler of the mid-1990s, **inspired by classic LightWave
Modeler** (versions 4 and 5), built with .NET 10 and Avalonia. It keeps the classic workflow (the quad view, points
and n-gon polygons, foreground and background layers, tabbed tool panels, numeric requesters for everything, a
keyboard-driven, grid-snapped way of working) and adds modern touches: shaded and smooth-shaded views, multi-level
undo, edge selection, and Lua macros. A **Layout** workspace in the same window animates and renders what you model.

It follows the plan in [Classic 3D Modeler Recreation Plan.md](Classic%203D%20Modeler%20Recreation%20Plan.md), Modeler
phases 0 to 7 and Layout phases L1 to L6.

![The quad view](Images/AVAModeler-spaceship.png)

## Features

### The Modeler

- **The quad view**: Top, Front, Side and Perspective, laid out as classic Modeler had them. Double-click a view (or
  press 0) to fill the window with it. The flat views pan and zoom, with a grid that adapts to the zoom and shows its
  size; the perspective view turns, and is shaded with the wireframe on top. Fit all (A) and fit the selection
  (Shift+A).
- **Classic colours**: black wireframe on grey, background layers dimmed, selections in yellow. The modern touches
  are smooth and flat shading, faces seen from behind tinted blue so flipped polygons stand out, and anti-aliased
  lines.
- **Two renderers behind one interface**: a software rasterizer (the default, works everywhere) and OpenGL 3.3 /
  GLES 3 (in Preferences). Both draw the same picture.
- **The mesh**: points and polygons of any number of points, including 1-point polygons (dots) and 2-point polygons
  (lines). LightWave's left-handed space and clockwise winding are kept.
- **Ten layers**: each can be off, in the foreground (being edited) or in the background (shown, and the operand for
  booleans and drills). The buttons along the bottom set them: the top half for the foreground, the bottom half for
  the background, with Shift to add. The number keys do the same.
- **Selection**: Points, Polygons, Volume and Edges modes. Click to pick, drag a box or right-drag a lasso; Shift
  adds and Ctrl removes. You can also invert, select connected, grow and shrink. In Volume mode, drag a box in two
  views to build a 3D volume. As in the original, a tool given an empty selection works on everything. The status
  bar counts points and polygons by type, and selections survive undo.
- **Tools**: each tool has typed parameters, which feed both the numeric panel and dragging in a view, so the two
  stay in step. The preview is live while you drag; Enter keeps the result as one undo step, and Esc cancels. The
  action centre is the selection, the mouse or the origin, and grid snapping is available. Every key can be
  remapped.
- **The classic tabs**:
  - **Objects**: Box, Ball (globe or tessellated), Disc, Cone, Points, Make Polygon, and Text (font outlines, with
    the letters' holes cut in).
  - **Modify**: Move, Rotate, Size, Stretch, Shear, Bend, Twist, Taper, Magnet, Drag, Jitter, Smooth, Center and
    Quantize.
  - **Multiply**: Extrude, Lathe, Mirror, Clone, Array (rectangular and radial), Path Extrude, Rail Extrude, Bevel and
    Smooth Shift.
  - **Polygon**: Flip, Triple, Merge Points, Align, Unify, Subdivide (faceted or smooth), Metaform, Surface, Merge
    Polygons, Split and Remove.
  - **Tools**:
    - Boolean against the background layers: Union, Subtract, Intersect and Add.
    - Drill with the background's outline: Core, Tunnel, Stencil and Slice.
    - Cut, copy and paste between layers, and swapping the foreground and background.
- **MetaNURBS**: Tab shows the foreground as the smooth (Catmull–Clark) surface of its cage, with the cage drawn in
  blue. Metaform bakes the smoothing into the mesh.
- **Surfaces**: named polygon groups with colour, diffuse, specular, glossiness, luminosity, transparency,
  reflection, smoothing and smoothing angle. They are edited live in the surface editor, and smooth surfaces are
  smooth-shaded in the views.
- **Files**:
  - LightWave objects: LWOB (and the LWLO layered form) read and written; an unchanged object writes back
    byte for byte. LWO2 (LightWave 6 and later) is read only.
  - VideoScape GEO is read and written.
  - Wavefront OBJ is read and written, with its .mtl.
  - Recent files, auto-save, and crash recovery: if the app didn't close cleanly, it offers the auto-save back.

![MetaNURBS](Images/AVAModeler-metanurbs.png)

### Macros and plugins

- **Macros** are Lua scripts (through MoonSharp), in the spirit of classic Modeler's ARexx macros.
  - What they can do: run any tool with any parameters, build geometry point by point, select, set layers, undo,
    and load and save. Macros › Macro Reference lists every command.
  - Samples: `samples/scripts/spaceship.lua` models a classic spaceship from scratch, and `logo.lua` makes bevelled
    flying-logo type.
  ```lua
  tool("Box", { Size = {2, 1, 1}, SegmentsX = 4 })
  selmode("polygons"); selbox(-1, 0.4, -1, 1, 1, 1)
  tool("Extrude", { Offset = {0, 0.5, 0} })
  ```
- **Plugins** are .NET assemblies.
  - Any public `Tool` class in one becomes a tool in its tab. An `IModelerPlugin` class can also add file formats
    (loaders and savers).
  - Drop the .dll into the plugins folder (`~/.config/AVAModeler/plugins` on Linux, `%APPDATA%\AVAModeler\plugins` on
    Windows), or load one from Macros › Load Plugin.
  - `samples/StarPlugin` is a working example: a Star generator tool and a point-cloud (.xyz) format.

![The logo and the numeric panel](Images/AVAModeler-logo.png)

### Layout

Layout (F2, or View › Layout) is the second workspace. It shares the object you're modeling live: change it in the
Modeler and Layout shows the change at once.

- **The scene**: objects, nulls, lights, cameras and bones, each with a parent and a pivot. Objects can also be
  loaded from files.
- **Animation**: a key per channel, interpolated by Kochanek–Bartels TCB splines as classic LightWave did.
  - With Auto key on, moving or turning an item in the view keys it at the current frame.
  - The timeline plays, steps, and shows the chosen item's keys.
  - The graph editor shows the channels as curves. Choose a key to set its value, tension, continuity, bias and
    linear flag, and set what happens before the first key and after the last.
- **Cameras and lights**: the camera view with its picture shape and its action- and title-safe areas; zoom factor
  and field of view. Distant, point and spot lights have colour, intensity, cone, soft edge, range and shadows.
- **Scene files**: classic LightWave .lws scenes (LightWave 4 and 5, and later versions) are read and written, and
  there is a native .avascene format (JSON) that keeps everything, meshes included.
- **Rendering**: an offline CPU ray tracer, multithreaded and deterministic.
  - Shading: Phong with glossiness, luminosity, smoothing within each surface's angle, reflection, and transparency
    with refraction.
  - Lights and backdrop: ray-traced shadows with soft-edged spots, ambient light, a gradient backdrop, and fog.
  - Antialiasing, and saving to PNG with or without alpha.
- **Deformation**: bones bend objects, weighted by distance as classic LightWave's were. Skelegons in the Modeler
  turn into bones. Morph targets blend by envelope, and there is wave and ripple displacement.

![Layout](Images/AVAModeler-layout.png)

![A ray-traced frame](Images/AVAModeler-render.png)

## Download and run

Ready-to-run builds for Windows, macOS and Linux, on both Intel/AMD and ARM, are on the
[Releases page](https://github.com/Harlock123/AVAModeler/releases/tag/latest). **Latest build** is rebuilt on every
push to `main`. Each download is one self-contained executable: no .NET install is needed.

| Platform | Archive |
|---|---|
| Windows | `AVAModeler-win-x64.zip`, `AVAModeler-win-x86.zip`, `AVAModeler-win-arm64.zip` |
| macOS | `AVAModeler-osx-arm64.tar.gz` (Apple silicon), `AVAModeler-osx-x64.tar.gz` (Intel) |
| Linux | `AVAModeler-linux-x64.tar.gz`, `AVAModeler-linux-arm64.tar.gz`, `AVAModeler-linux-arm.tar.gz` |

```bash
tar -xzf AVAModeler-linux-x64.tar.gz     # or linux-arm64, linux-arm, osx-arm64, osx-x64
./AVAModeler                             # or ./AVAModeler spaceship.lwo
```

On Windows, unzip and run `AVAModeler.exe`. On macOS, if Gatekeeper refuses to open it, run
`xattr -d com.apple.quarantine AVAModeler` once.

## Using it

| | Mouse | Keys |
|---|---|---|
| Select | Click; drag a box; right-drag a lasso (Shift adds, Ctrl removes) | Space cycles Points / Polygons / Volume / Edges; `/` deselect; `'` invert; `]` connected |
| Use a tool | Pick it from the tab (or its key), then drag in a view; Shift keeps to one axis | Enter keeps the result, Esc throws it away |
| Navigate | Wheel zooms; middle-drag or Alt-drag pans; drag the perspective view to turn it; double-click to maximize | A fit all, Shift+A fit selected, `.` and `,` zoom, 0 maximize |
| Layers | The layer buttons (top half foreground, bottom half background) | 1–9 foreground, Shift adds, Alt background, `\` swaps |
| Edit | | Ctrl+Z undo, Ctrl+Shift+Z redo, X cut, C copy, Ctrl+V paste, Delete remove |
| Workspaces | | F1 Modeler, F2 Layout, F9 render (in Layout) |
| Files | | Ctrl+N new, Ctrl+O open, Ctrl+S save, Ctrl+Q quit (File › Quit) |

The default tool keys follow the classic layout: T Move, Y Rotate, H Size, Shift+H Stretch, Shift+X Box, Shift+O
Ball, Shift+C Disc, P Make Polygon, F Flip, M Merge Points, E Extrude, L Lathe, V Mirror, B Bevel, Tab MetaNURBS.
Help › Keyboard lists them all, and Edit › Preferences changes any of them.

## Building and testing

```bash
dotnet build AVAModeler.slnx
dotnet run --project src/AVAModeler
dotnet test AVAModeler.slnx
```

It needs the .NET 10 SDK. 357 tests cover every layer:

| Tests | Count | What they cover |
|---|---|---|
| Core | 17 | Math, the mesh and its edge maps, layers, selection modes, undo |
| Formats | 56 | LWOB byte-identical round trips, LWLO layers, detail polygons, LWO2, GEO, OBJ, malformed files |
| Geometry | 76 | Booleans and drills (exact volumes, watertight results), subdivision, the Multiply algorithms, triangulation |
| Rendering | 23 | The four views' projections; the OpenGL camera matrix landing every point where the software one does; shading, smoothing, picking |
| Tools | 38 | Every tool, the numeric parameters, the command API, Lua macros, a plugin loaded from its own assembly, the spaceship and logo built from scratch |
| Scene | 126 | Transforms, TCB envelopes, cameras, lights, LWS v1/v3 and JSON scenes, bones, morphs, the ray tracer |
| UI | 21 | The window, mouse and keyboard driven through a headless window, Layout, and the screenshots |

The screenshots are drawn by the tests in a real (headless) window:

```bash
AVAMODELER_SHOTS=$PWD/Images dotnet test tests/AVAModeler.UI.Tests --filter Screenshot
```

## How it's built

Core, Geometry and Tools carry no UI, so tools are tested without a window, scripts drive them, and Layout reuses them.

| Project | What's in it |
|---|---|
| `AVAModeler.Core` | Double-precision math, the mesh (indexed points and n-gon polygons, with lazy edge and adjacency maps), layers, surfaces, selection, undo (copy-on-write snapshots of the layers an edit changes), the document |
| `AVAModeler.Formats` | LWOB/LWLO read and write, LWO2 read, VideoScape GEO, OBJ |
| `AVAModeler.Geometry` | Triangulation (LibTessDotNet), BSP booleans and drills, Catmull–Clark subdivision, the extrude/lathe/bevel family |
| `AVAModeler.Tools` | The tool framework and every tool, the command API, Lua macros (MoonSharp), plugins |
| `AVAModeler.Rendering` | `IRenderer`, the software and OpenGL renderers, the view cameras, picking |
| `AVAModeler.Scene` | Layout: the scene graph, envelopes, cameras, lights, bones, LWS and JSON scenes, the ray tracer |
| `AVAModeler.Modeler.UI` | The Modeler workspace: the quad view, tool tabs, numeric panel, layers, surface editor, preferences |
| `AVAModeler.Layout.UI` | The Layout workspace: the scene view, items, timeline, graph editor, render window |
| `AVAModeler` | The application: one window hosting both workspaces |

## Known limits

- **The OpenGL renderer** is off by default. The software renderer draws the same picture and is fast enough for
  models of tens of thousands of polygons. The OpenGL path's camera is tested against the software one point for
  point, but it hasn't been tried on every GPU and driver; if a context can't be had, the views fall back to software.
  Layout's view is drawn in software.
- **Booleans** use floating-point BSP: reliable on everyday shapes, but faces that are nearly (not exactly) coplanar
  can leave slivers. Both solids should be closed and face outward. Coplanar pieces of different faces stay separate
  (Merge Polygons tidies them).
- **Byte-identical LWOB round trips** are tested on hand-built files covering every chunk type. Files with more than
  one SRFS chunk, or a repeated PNTS/POLS in a layer, read correctly but are rewritten in the standard order.
- **Bevel** works per face, as LightWave 5's did (there's no group bevel). The camera view in Layout shows heading and
  pitch, not bank.

## About and licence

AVA Modeler © 2026 Lonnie Watson, under the [MIT License](LICENSE).

LightWave is a trademark of its current owner, LightWave Digital. AVA Modeler is an independent project inspired by
classic LightWave Modeler; it isn't affiliated with or endorsed by its owners. It reads and writes the classic object
and scene formats for compatibility. Lua support uses MoonSharp; polygon triangulation uses LibTessDotNet; the views
use Avalonia, SkiaSharp and Silk.NET.
