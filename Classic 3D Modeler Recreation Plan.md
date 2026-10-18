# Classic 3D Modeler Recreation Plan

Oct 18, 2026 · @Lonnie Watson

## Overview

The goal is a cross-platform recreation of the classic standalone LightWave Modeler (versions 4–5, around 1995–1999), built on .NET 10 and Avalonia. It keeps the classic workflow, adds modern touches, and gains a Layout (animation and rendering) counterpart later.

**Lineage.** Allen Hastings wrote VideoScape 3D (Aegis, 1986) for the Amiga. Stuart Ferguson wrote its companion modeler, Modeler 3D (Aegis, around 1987). NewTek hired both, and LightWave 3D shipped with the Video Toaster in 1990: Ferguson's Modeler plus Hastings's Layout. It went standalone around 1994 and moved to Windows NT, Alpha, MIPS, SGI and Mac. LightWave 6 (2000) rewrote the interface and file format, so the pre-6 Modeler is the target.

**Decisions made**

| Question | Decision |
| --- | --- |
| Platform | .NET 10 + Avalonia, on x64 and ARM64 for Windows, macOS and Linux |
| Fidelity | Classic workflow with modern touches (shaded views, multi-level undo, edge selection) |
| Reference version | Standalone LightWave 4.x–5.x Modeler, including MetaNURBS |
| Scope | Modeler first, then a Layout equivalent |

**Naming.** The LightWave name and trademark are still owned by LightWave Digital. The project needs an original name and should describe itself as "inspired by classic LightWave Modeler." Reading and writing the file formats for compatibility is fine.

**Classic traits to preserve:** a quad view (Top, Front, Side, Perspective); a mesh of points and n-gon polygons, including 1-point and 2-point polygons; Points, Polygons and Volume selection modes; about 10 layers that can each be foreground or background, with the background used as the operand for booleans and drills; tabbed tool panels (Objects, Modify, Multiply, Polygon, Tools, Display); numeric requesters for every tool; a grid-snapped, keyboard-driven workflow; surfaces as named polygon groups; macro scripting in the spirit of ARexx.

## Technology stack

The stack is .NET 10 and Avalonia, with OpenGL viewports behind a renderer interface so another graphics backend can replace it later.

| Concern | Choice | Why |
| --- | --- | --- |
| Runtime | .NET 10 (LTS) | Runs on x64 and ARM64 for Windows, macOS and Linux |
| UI | Avalonia (current stable) | The same XAML and controls on every platform |
| 3D viewports | Avalonia `OpenGlControlBase` + Silk.NET.OpenGL (GL 3.3 core / GLES 3) | Built into Avalonia; an `IRenderer` interface leaves room for Vulkan or Metal |
| 2D overlays, text primitive | SkiaSharp (already in Avalonia) | `SKPath` turns font glyphs into outlines for the Text tool |
| Polygon triangulation | LibTessDotNet | Draws concave n-gons correctly and helps the boolean code |
| Math | Custom double-precision vectors; System.Numerics where float is fine | Avoids drift during modeling; LWOB stores float32 on disk |
| Tests | xUnit + mesh snapshot tests | Round-trip tests catch subtle geometry bugs |
| Scripting (stands in for ARexx) | C# scripts via Roslyn, or Lua via MoonSharp | Plugins load through `AssemblyLoadContext` |

## Architecture and data model

Core, Geometry and Tools carry no UI code. Then tools can be tested without a window, scripts can drive them, and Layout can reuse them later.

| Project | Contents |
| --- | --- |
| `Core` | Math, Mesh, Layer, Surface, Selection, Undo, Document |
| `Formats` | LWOB read/write, LWO2 read, VideoScape GEO, OBJ, later LWS scenes |
| `Geometry` | Triangulation, CSG/booleans, subdivision, bevel and extrude algorithms |
| `Tools` | The tool framework and every Modeler tool, with no UI dependency |
| `Rendering` | `IRenderer`, the GL backend, wireframe/shaded/selection passes |
| `Modeler.UI` | Avalonia views, quad viewport, tool panels, numeric panel |
| `Scene` | Layout phase: scene graph, envelopes, cameras, lights, bones |
| `Layout.UI` | Layout phase: Avalonia views for scenes |
| `App` | One process hosting both the Modeler and Layout workspaces |

**Data model decisions**

- **Mesh:** an indexed point list plus a polygon list; each polygon holds point indices and a surface ID. Edge and adjacency maps are built lazily when a tool needs them and discarded when the mesh changes. A strict half-edge structure doesn't fit 1-point and 2-point polygons or non-manifold geometry, so this keeps the classic model while still allowing edge tools.
- **Document:** 10 or more layers, each holding a mesh. A layer can be off, foreground or background. One surface table is shared by all layers.
- **Undo:** copy-on-write snapshots of each changed layer to start, since they're simple and reliable. Move to command diffs only if memory becomes a problem.
- **Tools:** a class per tool with typed parameters. The same parameters feed the numeric panel and interactive dragging. Lifecycle: `Begin`, `Update(drag)`, `Commit` or `Cancel`, with a live preview while dragging.

## Modeler phases

The Modeler takes roughly 23–32 weeks for one developer across eight phases. Phase 5, the classic tool set, is the largest. Durations are estimates.

![Modeler roadmap · 8 phases, then Layout](modeler-roadmap.png)

Phase 5 is the long pole. Layout starts once the Phase 6 alpha is out, while Phase 7 scripting can run alongside it.

### Phase 0: Foundations (1–2 weeks)

- Solution structure, code style, and CI building Windows, macOS and Linux on x64 and ARM64
- Math library: vectors, matrices, planes, bounding boxes, rays
- **Milestone:** an empty Avalonia window opens on all three operating systems

### Phase 1: Data model and file I/O (2–3 weeks)

- Mesh, Layer, Surface, Document and the undo stack
- LWOB reader and writer (`FORM`/`LWOB`, `PNTS`, `SRFS`, `POLS`, `SURF`, detail polygons)
- LWO2 reader, VideoScape GEO import, OBJ import and export
- **Milestone:** a legacy `.lwo` reads and writes back byte-identical and opens in Blender

### Phase 2: Viewports (3–4 weeks)

- Quad layout of Top (XZ), Front (XY), Side (ZY) and Perspective; maximize any view
- Orthographic pan and zoom, a grid that adapts to zoom with its size shown, fit all and fit selected
- Wireframe in a classic color scheme, dimmed background layers, point markers
- Modern touches: shaded perspective view, backface tinting, anti-aliased lines
- Picking by screen-space nearest element and CPU ray casts
- **Milestone:** a loaded model navigates smoothly in all four views

### Phase 3: Selection (2 weeks)

- Points, Polygons and Volume modes with click, box and lasso; add, subtract, invert
- Status bar with point and polygon counts and polygon types
- Modern touch: Edges mode built on the lazy edge map
- **Milestone:** every selection mode works and selections survive undo

### Phase 4: Tool framework and numeric panel (2 weeks)

- Modal tool controller that behaves the same in every viewport
- Action center (mouse, selection, origin) and grid snapping
- Numeric panel generated from tool parameters, synced during live drags
- Remappable shortcut table with a classic-style default layout
- **Milestone:** Move, Rotate and Size work by drag and numerically, with undo

### Phase 5: Classic tool tabs (8–12 weeks)

1. **Objects:** Box, Ball (globe and tessellated), Disc, Cone, Points, Make Polygon, Text
2. **Modify:** Move, Rotate, Size, Stretch, Shear, Bend, Twist, Taper, Magnet, Drag, Jitter, Smooth, Center, Quantize
3. **Polygon:** Flip, Triple, Merge Points, Align, Unify, Subdivide (faceted and smooth), Surface assignment, Merge Polygons, Split
4. **Multiply:** Extrude, Lathe, Mirror, Clone, Array, Path Extrude, Rail Extrude, Bevel, Smooth Shift
5. **Layer operations:** cut and paste between layers, swap foreground and background
6. **Tools:** Boolean (Union, Subtract, Intersect, Add) and Drill (Core, Tunnel, Stencil, Slice), using the background layer as operand

**Milestone:** a classic logo or spaceship can be modeled from scratch.

### Phase 6: Subdivision and polish (3–4 weeks)

- MetaNURBS: a Catmull-Clark-style live subdivision cage toggled with Tab; Metaform to bake it
- Basic surface editor (color, diffuse, specular, smoothing angle) shown in the shaded view
- Preferences, recent files, auto-save and crash recovery
- **Milestone:** a feature-complete standalone Modeler (alpha)

### Phase 7: Scripting and plugins (2–3 weeks)

- Command API exposing every tool, the ARexx-macro equivalent
- Plugin interface for tools, loaders and savers
- **Milestone:** a generator script and a tool plugin from a separate assembly both run

## Layout phases (after Modeler ships)

Layout runs as a second workspace in the same app and shares the Document and Core in memory. That replaces the old Modeler-to-Layout link with something simpler.

1. **L1 Scene graph:** objects that reference meshes, parenting, pivots, a null object
2. **L2 Animation:** per-channel keyframes with TCB (Kochanek–Bartels) splines like classic LightWave, a graph editor, a timeline/dope sheet
3. **L3 Cameras and lights:** distant, point and spot lights; camera zoom/FOV and safe areas; OpenGL preview playback
4. **L4 Scene files:** an LWS reader (a text format) for old scenes, plus a modern native format
5. **L5 Rendering:** an offline CPU ray tracer first, in LightWave's spirit; a GPU path tracer later
6. **L6 Deformation:** bones and skelegons, morphs, displacement

Layout is roughly as large as Modeler again, so it starts only after the Modeler alpha.

## Risks and next steps

Booleans are the biggest technical risk; scope is the biggest schedule risk.

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Robust booleans and drills on n-gon meshes | Broken or leaky geometry | Start with BSP-based CSG; move to exact predicates or mesh arrangements if precision problems appear |
| OpenGL deprecated on macOS | Future macOS releases may break viewports | Keep rendering behind `IRenderer`; add Vulkan (MoltenVK) or Metal if needed |
| Drifting from the original feel | Loses the point of the project | Collect the LightWave 4/5 manuals and screenshots early; run the original in FS-UAE or WinUAE as a reference |
| Scope (Layout is as large as Modeler) | Nothing ships | Ship the Modeler alpha before starting Layout |

**Next steps**

- [ ] Choose an original project name
- [ ] Gather the LightWave 4/5 manuals, screenshots and sample `.lwo` files
- [ ] Scaffold the solution and CI (Phase 0)
- [ ] Build the mesh model and LWOB reader/writer with round-trip tests (Phase 1)
