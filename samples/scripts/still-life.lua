-- A textured still life: a crate, a planet and a tiled floor, each textured a different way. Run it from
-- Macros > Run Macro; the images are in samples/images (made by make_textures.py there).
tool("Box", { Size = {1, 1, 1}, Center = {-0.75, 0.5, 0.1}, Surface = "Crate" })
tool("Ball", { Radius = {0.6, 0.6, 0.6}, Center = {0.75, 0.6, -0.1}, Sides = 32, Segments = 16, Surface = "Planet" })
tool("Box", { Size = {6, 0, 6}, Surface = "Floor" })

-- The crate: the same picture on all six sides by a cubic map, fitted to it, and bumped by it.
texture("Crate", "../images/crate.png", { Projection = "cubic", Fit = true })
texture("Crate", "../images/crate.png", { Channel = "bump", Projection = "cubic", Fit = true, Amplitude = 4 })

-- The planet: a UV map made by a spherical projection, and the map laid on through it.
selmode("polygons")
selbox(0.1, -0.1, -0.8, 1.4, 1.3, 0.6)
makeuvs("Planet", "spherical", "y")
tool("Surface", { SurfaceName = "Planet", Smoothing = true, SetLook = true })
deselect()
texture("Planet", "../images/planet.png", { Projection = "uv", UVMap = "Planet" })

-- The floor: tiles a metre square, repeating, laid flat from above.
texture("Floor", "../images/tiles.png", { Projection = "planar", Axis = "y", Size = {1, 1, 1} })
uvmap("Planet")
print("Still life: " .. pointcount() .. " points, " .. polycount() .. " polygons, UV maps: " .. table.concat(uvmaps(), ", "))
