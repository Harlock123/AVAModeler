-- Blueprints behind the views: a small rocket's side, front and top drawings (samples/images, drawn by
-- make_blueprints.py there) as backdrops, placed in metres so they line up with each other and the model, and the
-- start of a model over them: the body lathed from its profile, traced off the side view. The fins are left to you.
-- Run it from Macros > Run Macro; D (View > Backdrop) shows each view's backdrop settings.

-- Each image's centre is on its view's two axes: side Z and Y, front X and Y, top X and Z. All three drawings are
-- 1.2 m wide at 300 pixels a metre; the elevations run from 0.1 m below the ground to 2.2 m up.
backdrop("side", "../images/rocket-side.png", { Center = {0, 1.05}, Size = 1.2, Opacity = 0.7 })
backdrop("front", "../images/rocket-front.png", { Center = {0, 1.05}, Size = 1.2, Opacity = 0.7 })
backdrop("top", "../images/rocket-top.png", { Center = {0, 0}, Size = 1.2, Opacity = 0.7 })

-- The profile: radius against height, from the nozzle's mouth up to the tip of the nose, as lines in the XY plane.
setlayers({1}, {})
local profile = { {0.09, 0}, {0.05, 0.13}, {0.11, 0.16}, {0.15, 0.3}, {0.15, 1.0}, {0.15, 1.5} }
for i = 1, 8 do
  local t = i / 8
  profile[#profile + 1] = { 0.15 * math.sqrt(math.max(0, 1 - t * t)), 1.5 + 0.6 * t }
end
local points = {}
for i, p in ipairs(profile) do points[i] = addpoint(p[1], p[2], 0) end
for i = 1, #points - 1 do addpolygon({ points[i], points[i + 1] }, "Hull") end

-- Round the Y axis in 24 steps: the body, the skirt, the nozzle and the nose in one go.
selectall()
tool("Lathe", { Axis = "y", Center = {0, 0, 0}, Sides = 24 })
tool("Surface", { SurfaceName = "Hull", Color = {220, 222, 228}, Smoothing = true, SetLook = true })

print("Rocket blueprints: the side, front and top backdrops, and a lathed body with " .. polycount() .. " polygons")
