-- A flying-logo classic: bevelled, extruded type. Run it from Macros > Run Macro.
tool("Text", { Text = "AVA", Height = 1, Axis = "Z", Surface = "Gold" })
selmode("polygons")
selectall()
-- Push the letters back into solid type (keeping the front), then bevel the front faces for that 1990s chrome edge.
tool("Extrude", { Offset = {0, 0, 0.25}, CapBase = true })
deselect()
selbox(-10, -10, -0.001, 10, 10, 0.001)
tool("Bevel", { Inset = 0.02, Shift = 0.04 })
deselect()
print("Logo: " .. pointcount() .. " points, " .. polycount() .. " polygons")
