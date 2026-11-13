# Milestones: modeling aids, maps, sculpting and agents

The user asked for all of these on 13 November 2026, after a review of what AVA Modeler lacks. They are built in this
order, one commit per stage, with tests and docs (README, AGENTS.md), and pushed only when asked. AVALightWave uses
this engine as a submodule, so every change is additive: existing files, tools and MCP calls behave as before.

## Milestone A: symmetry and the knife

**Status:** done. `Core/Symmetry.cs`, `Selection.Symmetry`, `Tool.MapPoints`; `Geometry/Knife.cs` and `KnifeTool` (K).

1. **Symmetry.** `Document.Symmetry` is Off, X, Y or Z (View › Symmetry, Alt+Y to step through, a button in the
   Display tab). With it on:
   - the selection a tool acts on includes each selected point's and polygon's mirror partner (the point at the
     mirrored place, within a small tolerance), so Extrude, Bevel, Smooth and the rest work on both sides;
   - tools that move points (Move, Rotate, Size, Stretch, Shear, Twist, Taper, Bend, Magnet, Jitter, Drag and the
     sculpt brushes) move the partner to the mirror of where its point went, so the two sides stay matched;
   - points on the mirror plane stay on it;
   - picking in a view selects the partner too.

   The mirror pairs are found once a stroke (`Symmetry.Pairs`). Agents set it with `set_symmetry`; Lua with
   `symmetry("x")`.
2. **Knife.** A Polygon-tab tool. Drag a line across a view and the cut is the plane through that line along the
   view's depth. Every active face it crosses, between the line's ends, is split along it. Neighbouring faces get the
   new points on their shared edges, so a closed mesh stays closed. UVs and point maps are interpolated along the cut
   edges. It's scriptable with Start, End and Axis.

## Milestone B: weight maps and bones

**Status:** done. `Core/PointMaps.cs`, `Geometry/WeightMaps.cs` and `Brushes.cs`, `Tools/MapTools.cs` (the Maps tab),
`Bone.WeightMap`, `ViewLayer.PointColors`; agents' `set_weights`, `get_weights`, `auto_weights`. Show Map is Alt+W.

1. **Point maps.** Weight maps (LWO2 VMAP `WGHT`, one value a point) and colour maps (`RGB `/`RGBA`) live on the mesh
   as `PointMap`s. Like UV maps, they follow the edits that renumber points (removing, merging, appending, copying).
   They're read from LWO2 and written to and read from LWOB as VMAP chunks, and kept in .avascene meshes.
2. **Weight tools** (a new Maps tab):
   - Paint Weights: a brush with Radius, Strength, Falloff and Add/Subtract/Set/Smooth modes; it respects symmetry;
   - Set Weight: the selected points' value in the current map;
   - Smooth Weights;
   - Normalize Weights: makes the chosen maps add to 1 at every point;
   - Mirror Weights: copies one side's weights to the other, and Left/Right names to their opposite map;
   - Weights from Skelegons: one map per skelegon, from the distance to the bone with a smooth falloff, normalised.
     That's an automatic rig.
3. **Showing them.** With a weight map current, View › Weight Shading colours the shaded views blue (0) through green
   to red (1). Both renderers do it, through `Shading`.
4. **Bones use them.** `Bone.WeightMap` and `WeightMapOnly` change how a bone weighs a point: by its map's value (only
   that, or times the distance falloff), as LightWave 6 does. Skelegons with a map of their name get it. .lws keeps
   them as `BoneWeightMapName` and `BoneWeightMapOnly`, and .avascene keeps them too. The MCP `set_item` (bone)
   gets `weight_map` and `weight_map_only`.
5. **Agents.** `set_weights`, `get_weights`, `auto_weights` (from skelegons), and `set_symmetry` from stage A if it
   isn't already there.

## Milestone C: sculpting and vertex colours

**Status:** done. `Geometry/Sculpting.cs`, `SculptTool` and its brushes (the Sculpt tab), Paint Colors and Set Color,
`Surface.VertexColorMap`; agents' `sculpt`.

1. **Sculpt brushes** (a Sculpt tab): Draw, Inflate, Smooth, Grab, Pinch, Flatten and Crease. Each has Radius,
   Strength, Falloff, Invert and Pen Pressure. A stroke is a run of dabs where the mouse ray meets the mesh, spaced at
   a share of the radius. The whole stroke is one undo step and respects symmetry. With MetaNURBS on, the brushes
   move the cage. The engine is `Sculpting` in Geometry, so agents get the same brushes through `sculpt`, a stroke of
   surface points.
2. **Vertex colours.**
   - Paint Colours is the same brush as Paint Weights, on an RGB map.
   - Set Colour fills the selection.
   - The shaded views show the current colour map.
   - A surface can take a colour map (`Surface.VertexColorMap`, LWO2's VCOL), multiplied into its colour in the ray
     tracer.
   - glTF's `COLOR_0` is read and written; OBJ's `v x y z r g b` too.

## Milestone D: mesh processing and measuring

**Status:** not started.

1. **Reduce Polygons.** Quadric-error decimation to a target share or count. It works on triangles (it triangulates
   first), keeps open borders, surface borders and UV seams, carries UVs and point maps onto the merged points, and
   never flips a face.
2. **Remesh.** Voxel remeshing by surface nets: an even, all-quad skin of a closed mesh at a voxel size, with
   optional smoothing passes. It's a clean base for sculpting, and it repairs broken Boolean results.
3. **Sturdier Booleans.** Faces whose planes nearly match are welded to one plane before the BSP splits. Points are
   classified with a tolerance scaled to the model. Slivers and near-zero faces are removed and nearby points merged
   after. These are the near-coplanar cases Known limits lists.
4. **Measure.** A ruler tool: drag between two places (snapping to points) to see the length and its X/Y/Z parts. It
   can be kept as a dimension drawn in the views (Document.Dimensions, saved with the object's setup). Mesh Info
   gains a Measure section for the selection: distance between two points, angle at the middle of three, area of the
   polygons and bounds. The `measure` MCP tool gives the same.

## Milestone E: baking, normal maps and glTF

**Status:** not started.

1. **Normal maps.** A Normal texture channel: an image of tangent-space normals through a UV map. The ray tracer
   bends the shading normal with each triangle's tangent frame. The views leave it out, as they do bumps.
2. **Bake Maps** (Maps tab): bakes into a new PNG through the current UV map:
   - normal (from the background layers' detailed mesh onto the foreground's low one, by rays along the low mesh's
     normals, or the low mesh's own smoothed normals);
   - ambient occlusion;
   - curvature;
   - colour (the surface's colour and procedurals).

   It has a size, a padding to bleed the edges, and the option to hook the result up as the surface's texture.
3. **glTF, more of it.**
   - Normal and occlusion maps are written and read.
   - Metallic-roughness and emissive maps are read into specular, glossiness and luminosity textures.
   - Skins are read as skelegons (one per joint, from the joint hierarchy) and weight maps (from `JOINTS_0`/`WEIGHTS_0`).
   - Morph targets are read as extra layers named after the target.

   A rigged glTF model comes in ready for Layout.

## Milestone F: workflow

**Status:** not started.

1. **Undo history.** A History panel (View › History, H) lists the undo steps by name, newest at the top. Clicking
   one goes back (or forward) to it. Agents get `get_history`, and `undo` and `redo` take `steps`.
2. **Linked copies.** Instance (Multiply tab) puts copies of the current layer's mesh in another layer as instances:
   each a position, rotation and scale, with Number, Offset, Rotation and Scale steps as Clone has. They show in the
   views, follow every change to the source, and are rendered and exported to glTF as nodes sharing one mesh. Freeze
   Instances makes them real geometry. LWOB keeps them in an AVIN chunk, which LightWave skips.
3. **Photo match.** Place a photo in the perspective view and match its camera. Mark six or more of the model's points
   on the photo and the camera is solved by the direct linear transform, with focal length and pose shown. With two
   matched photos, clicking the same feature in each triangulates a new point. Agents get `match_photo` and
   `point_from_photos`.

## Milestone G: agents

**Status:** not started.

1. **`compare_to_reference`.** Renders the object's silhouette from a reference picture's view (front, side, top, or
   a matched photo camera). It returns the overlap score (intersection over union), the parts too big and too small
   as a picture, and where they are (top, left and so on), so an agent can refine against the reference.
2. **`model_from_images`.** A recipe (MCP prompt): set the pictures as backdrops, block out the main forms with
   symmetry on, compare with `compare_to_reference`, refine, then check the mesh.
3. **More recipes:**
   - a rigged character (skelegons, auto weights, painted fixes, a test pose in Layout);
   - a low-poly game asset (model, reduce, UVs, bake normal and AO from the high version, export glTF);
   - a printable part to dimensions (measure, check, slice).
4. **AVALightWave.** Once this is done, move AVALightWave's submodule to it. Its `make_object` engine calls then reach
   the new tools, and its characters get weight-mapped bones.

## What's left out

- A true quad-flow retopology (field-guided quad remeshing). Remesh's voxel quads are even but follow the grid.
- Exact-arithmetic Booleans. Stage D's welding and cleanup handle the near-coplanar cases, not every degenerate one.
- A lighting bake (light maps).
- Photo match from vanishing lines alone. It needs six known points.
