-- LightWave 5's procedural textures: patterns worked out in 3D from where each point is, so no image files or UV
-- maps are needed. A marble ball, a block of wood, a rock with a fractal-noise bump, on a checkerboard floor.
-- Run it from Macros > Run Macro, then look in the perspective view (textured) or render it in Layout (F2, F9).

-- Each object in a layer of its own, so the Surface tool can give it a colour and smoothing.
setlayers({1}, {})
tool("Box", { Size = {6, 0, 6}, Surface = "Floor" })
tool("Surface", { SurfaceName = "Floor", Color = {225, 215, 190}, SetLook = true })

setlayers({2}, {})
tool("Ball", { Radius = {0.5, 0.5, 0.5}, Center = {-0.95, 0.5, 0.2}, Sides = 48, Segments = 24, Surface = "Marble" })
tool("Surface", { SurfaceName = "Marble", Color = {236, 232, 224}, Smoothing = true, SetLook = true })

setlayers({3}, {})
tool("Box", { Size = {0.8, 0.6, 0.8}, Center = {0.2, 0.3, 0.75}, Surface = "Wood" })
tool("Surface", { SurfaceName = "Wood", Color = {196, 140, 82}, SetLook = true })

setlayers({4}, {})
tool("Ball", { Radius = {0.45, 0.45, 0.45}, Center = {1.05, 0.45, -0.45}, Sides = 48, Segments = 24, Surface = "Rock" })
tool("Surface", { SurfaceName = "Rock", Color = {150, 140, 128}, Smoothing = true, SetLook = true })
setlayers({1, 2, 3, 4}, {})

-- The floor: checks half a metre across in a dark slate.
proctex("Floor", "checkerboard", { Color = {48, 52, 62}, Size = {0.5, 0.5, 0.5} })

-- The ball: grey-green veins layered along X, swirled by turbulence.
proctex("Marble", "marble", { Color = {64, 84, 80}, Axis = "x", Size = {0.3, 0.3, 0.3}, Center = {-0.95, 0.5, 0.2},
    VeinSpacing = 0.7, Turbulence = 2, VeinSharpness = 6, Frequencies = 5 })

-- The block: growth rings round its vertical axis, as if cut from a log standing up.
proctex("Wood", "wood", { Color = {110, 62, 28}, Axis = "y", Center = {-0.4, 0, 1.4}, Size = {1, 1, 1},
    RingSpacing = 0.08, Turbulence = 0.15, RingSharpness = 3 })

-- The rock: mottled colour and a fractal-noise bump, the same noise at two sizes.
proctex("Rock", "fractal_noise", { Color = {92, 80, 70}, Size = {0.3, 0.3, 0.3}, Contrast = 2 })
proctex("Rock", "fractal_noise", { Channel = "bump", Size = {0.12, 0.12, 0.12}, Frequencies = 5, Amplitude = 0.25 })

print("Procedurals: a marble ball, a block of wood and a bumpy rock on a checkerboard floor, " .. #uvmaps() .. " UV maps and no images")
