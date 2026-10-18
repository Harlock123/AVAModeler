-- A classic spaceship from scratch, the way Modeler tutorials built them: half a hull, a wing pulled out of its
-- side, mirrored into a whole; then engines and a canopy. Run it from Macros > Run Macro.

-- Half the hull, from the centre line out to the right.
tool("Box", { Center = {0.25, 0, 0}, Size = {0.5, 0.4, 3}, SegmentsY = 1, SegmentsZ = 6 })
-- Narrow and flatten it toward the nose (the nose points down +Z).
tool("Taper", { Axis = "Z", Factor = 25, Center = {0, 0, 0} })

-- The wing: pull the two rearmost faces of the right-hand side out and back.
selmode("polygons")
deselect()
selbox(0.3, -1, -1.6, 0.6, 1, -0.4)
tool("Extrude", { Offset = {1.4, 0, -0.6}, Segments = 2 })
-- Thin the wing toward its tip.
tool("Taper", { Axis = "X", Factor = 30, Center = {1.2, 0, -1.2} })

-- Take off the faces on the centre line, then mirror the half across it: one closed hull.
deselect()
selbox(-0.01, -1, -2, 0.01, 1, 2)
tool("Remove")
deselect()
tool("Mirror", { Axis = "X", Center = 0 })

-- Two engines at the back and a canopy on top.
tool("Disc", { Center = {0.3, 0, -1.65}, Radius = {0.13, 0.13, 0.13}, Height = 0.45, Axis = "Z", Sides = 16, Surface = "Engine" })
tool("Disc", { Center = {-0.3, 0, -1.65}, Radius = {0.13, 0.13, 0.13}, Height = 0.45, Axis = "Z", Sides = 16, Surface = "Engine" })
tool("Ball", { Center = {0, 0.2, 0.3}, Radius = {0.16, 0.12, 0.45}, Sides = 16, Segments = 8, Surface = "Canopy" })

deselect()
print("Spaceship: " .. pointcount() .. " points, " .. polycount() .. " polygons")
