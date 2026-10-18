-- A spiral staircase: a box per step, each turned and raised a little more than the last.
-- Run it from Macros > Run Macro.
local steps = 16
for i = 0, steps - 1 do
  deselect()
  tool("Box", { Center = {1.2, i * 0.2, 0}, Size = {1.6, 0.08, 0.5} })
  -- Select just the new step (its six faces) and turn it into place.
  local n = polycount()
  selpolygons({ n - 5, n - 4, n - 3, n - 2, n - 1, n })
  tool("Rotate", { Axis = "Y", Angle = i * 22.5, Center = {0, 0, 0} })
end
deselect()
tool("Disc", { Center = {0, steps * 0.1, 0}, Radius = {0.12, 0.12, 0.12}, Height = steps * 0.2 + 0.2, Sides = 16 })
print("Made " .. steps .. " steps: " .. pointcount() .. " points, " .. polycount() .. " polygons")
