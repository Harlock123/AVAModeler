using AVAModeler.Core;
using MoonSharp.Interpreter;

namespace AVAModeler.Tools;

/// <summary>
/// Lua macros (through MoonSharp), in the spirit of classic Modeler's ARexx macros. A macro sees the command API as
/// plain functions. Layers, points and polygons are numbered from 1, as Lua counts. The macro runs in a sandbox:
/// no file or OS access beyond the load/save commands.
/// <code>
/// tool("Box", { Size = {2, 1, 1}, SegmentsX = 4 })
/// local a = addpoint(0, 0, 0)
/// selmode("polygons"); selectall(); tool("Flip")
/// </code>
/// </summary>
public static class Macros
{
    private static readonly string ProceduralKinds = string.Join(", ", Procedurals.All.Select(t => "\"" + t.Key + "\""));

    /// <summary>The functions a macro can call, with one line each, for the Help.</summary>
    public static readonly (string Signature, string Does)[] Reference =
    [
        ("tool(name, {Param = value, ...})", "runs a tool; vectors are {x, y, z}"),
        ("tools()", "the names of every tool"),
        ("params(name)", "a tool's parameter names"),
        ("addpoint(x, y, z)", "adds a point to the current layer; returns its number"),
        ("addpolygon({p1, p2, ...}, surface)", "adds a polygon through points; returns its number"),
        ("pointcount() / polycount()", "how many points and polygons the current layer has"),
        ("getpoint(n)", "a point's x, y, z"),
        ("polypoints(n)", "a polygon's point numbers"),
        ("selmode(\"points\" | \"polygons\" | \"edges\" | \"volume\")", "the selection mode"),
        ("selpoints({...}) / selpolygons({...})", "selects points or polygons by number"),
        ("seledges({{p1, p2}, ...})", "selects edges by their two points' numbers (Edges mode)"),
        ("selloop() / selring()", "adds the edge loop or edge ring through each selected edge (or the edge between two selected points), in Edges mode; returns how many edges are selected"),
        ("selbox(x0, y0, z0, x1, y1, z1)", "selects (in the current mode) the points inside a box, or the polygons wholly inside it"),
        ("selectall() / deselect() / invert()", "selection"),
        ("setlayers({fg...}, {bg...})", "which layers are foreground and background"),
        ("undo() / redo()", "the history"),
        ("new() / load(path) / save(path)", "files; the extension picks the format: .lwo, .obj, .geo, .glb, .gltf, .stl or .3mf (load also reads .lw and any plugin format)"),
        ("makeuvs(map, projection, axis)", "lays the selection (or all) out in a UV map: \"planar\", \"cylindrical\", \"spherical\", \"cubic\" or \"atlas\", along \"x\", \"y\" or \"z\" (or tool(\"Make UVs\", {...}) for every setting)"),
        ("uvmap(name) / uvmap()", "chooses the UV map to work on (the UV view shows it), or returns the one chosen"),
        ("uvmaps()", "the names of the UV maps"),
        ("setuv(n, u, v) / getuv(n)", "a point's UVs in the chosen UV map"),
        ("texture(surface, image, {Option = value, ...})", "gives a surface an image texture; options: Channel (\"color\", \"diffuse\", \"specular\", \"luminosity\", \"reflection\", \"transparency\", \"bump\"), Projection (\"planar\", \"cylindrical\", \"spherical\", \"cubic\", \"uv\"), Axis, UVMap, Center, Size, Fit (automatic sizing), WidthTiling/HeightTiling (\"reset\", \"repeat\", \"mirror\", \"edge\"), WidthWrap/HeightWrap, Opacity, Blend, Amplitude. With a procedural's name in place of the image (\"checkerboard\", no extension) it does what proctex() does"),
        ("proctex(surface, kind, {Option = value, ...})", "gives a surface a LightWave 5 procedural texture (no image needed): kind is " + ProceduralKinds + "; it sets the channel's first layer of that kind (NewLayer = true adds another). Options: Channel, Color ({r, g, b}, 0 to 255: the texture colour, for the colour channel), Value (0 to 1: the texture value, for the other channels), Size (one pattern unit, e.g. a check's width, in metres), Center, Axis, Fit, Falloff ({x, y, z}, fraction per metre), Opacity, Blend, Amplitude (bump), Negative, World (world coordinates), and the kind's settings by name: Frequencies, Contrast, SmallPower, Turbulence, VeinSpacing, VeinSharpness, RingSpacing, RingSharpness, LineThickness, DotDiameter, Fuzziness, WaveSources, Wavelength, WaveSpeed, BandSharpness, BumpRadius, BumpSpacing"),
        ("notexture(surface, channel)", "removes a surface's textures (for one channel, or all)"),
        ("slice(path [, options])", "slices the foreground layers for 3D printing and saves the G-code (.gcode, or .x3g for a Sailfish printer such as the Flashforge Creator Pro), changing nothing in the object; options: profile, printer and material (by name: Draft 0.28, Standard 0.20, Fine 0.12, Strong or your own; the rest change it), layer (mm), infill (%), pattern (\"lines\", \"grid\" or \"gyroid\"), walls, supports, bedonly, extruder and supportextruder (1 or 2, for a dual printer), supportmaterial (the other extruder's), brim (lines), scale (%), rotate (°), copies, gap (mm), layflat (\"modelled\", \"largest\" or \"selected\"); the rest as the 3D Print window has them. Returns a table: file, layers, time, minutes, grams, filament_m, size {x, y, z} in mm, printable, warnings"),
        ("checkmesh()", "checks the foreground layers as Mesh Info does, changing nothing; returns a table: verdict, printable, watertight, points, polygons, faces, area (m²), volume (m³, nil unless closed), center (centre of mass {x, y, z}), size, min, max, shells, holes, boundary_edges, nonmanifold_edges, inconsistent_edges, flipped_polygons, inward_shells, cavities, degenerate_polygons, nonplanar_polygons, duplicate_points, unused_points, points_and_lines, and report (the lines of text). tool(\"Select Problems\", {Problem = \"BoundaryEdges\"}) selects them"),
        ("backdrop(view, image, {Option = value, ...})", "puts a reference image (a blueprint or photo) behind the \"top\", \"front\" or \"side\" view, in world space, so it pans and zooms with the view and lines up across views; not saved in the object or undone (the app remembers it per object). Options: Center ({a, b}: the image's centre in metres on the view's two axes, top X and Z, front X and Y, side Z and Y), Size (its width in metres; its height follows the image's shape), Height (metres, to stretch it), Fit (true: sized round the foreground), PixelSize (metres a pixel covers: the width from the image's pixels), Brightness (-1 to 1), Contrast (0 to 4, 1 leaves it), Opacity (0 to 1, default 0.5), Invert, Visible. With nil as the image it changes the view's backdrop settings. Returns a note if the image can't be found (a magenta checker shows)"),
        ("nobackdrop(view)", "takes the backdrop from a view (\"top\", \"front\" or \"side\"), or from every view with none given; returns how many went"),
        ("print(...)", "writes to the macro's output"),
    ];

    /// <summary>
    /// Runs a macro against the command API. Errors come back as <see cref="MacroException"/> with the Lua message.
    /// With a <paramref name="timeLimit"/>, a macro still running after that long is stopped (with a
    /// <see cref="MacroException"/>), so a script that never ends can't hang its caller.
    /// </summary>
    public static void Run(string code, Commands commands, TimeSpan? timeLimit = null)
    {
        var lua = LuaSandbox.Create(commands.Print);
        var g = lua.Globals;
        g["tool"] = (Action<string, Table?>)((name, args) => commands.Run(name, args == null ? null : ToArgs(args)));
        g["tools"] = (Func<Table>)(() => List(lua, commands.Registry.Names.Order()));
        g["params"] = (Func<string, Table>)(name => List(lua, commands.Registry.Create(name).Parameters.Select(p => p.Name)));
        g["addpoint"] = (Func<double, double, double, int>)((x, y, z) => commands.AddPoint(new Vec3(x, y, z)) + 1);
        g["addpolygon"] = (Func<Table, string?, int>)((pts, surface) => commands.AddPolygon(Indices(pts), surface) + 1);
        g["pointcount"] = (Func<int>)(() => commands.PointCount);
        g["polycount"] = (Func<int>)(() => commands.PolygonCount);
        g["getpoint"] = (Func<int, DynValue>)(n =>
        {
            var p = commands.Point(n - 1);
            return DynValue.NewTuple(DynValue.NewNumber(p.X), DynValue.NewNumber(p.Y), DynValue.NewNumber(p.Z));
        });
        g["polypoints"] = (Func<int, Table>)(n => List(lua, commands.PolygonPoints(n - 1).Select(i => i + 1)));
        g["selmode"] = (Action<string>)(m => commands.SelectMode(Enum.Parse<SelectionMode>(m, ignoreCase: true)));
        g["selpoints"] = (Action<Table>)(t => commands.SelectPoints(Indices(t)));
        g["selpolygons"] = (Action<Table>)(t => commands.SelectPolygons(Indices(t)));
        g["seledges"] = (Action<Table>)(t => commands.SelectEdges(t.Values.Select(v =>
        {
            var pair = v.Table?.Values.Select(x => (int)x.Number - 1).ToArray();
            return pair is { Length: 2 } ? (pair[0], pair[1]) : throw new ArgumentException("seledges wants pairs of point numbers: {{1, 2}, {2, 3}}.");
        }).ToList()));
        g["selloop"] = (Func<int>)commands.SelectLoop;
        g["selring"] = (Func<int>)commands.SelectRing;
        g["selbox"] = (Action<double, double, double, double, double, double>)((x0, y0, z0, x1, y1, z1) =>
            commands.SelectInside(Box3.FromCorners(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1))));
        g["selectall"] = (Action)commands.SelectAll;
        g["deselect"] = (Action)commands.SelectNone;
        g["invert"] = (Action)commands.Invert;
        g["setlayers"] = (Action<Table, Table?>)((fg, bg) => commands.SetLayers(Indices(fg), bg == null ? null : Indices(bg)));
        g["undo"] = (Func<bool>)commands.Undo;
        g["redo"] = (Func<bool>)commands.Redo;
        g["new"] = (Action)commands.New;
        g["load"] = (Action<string>)commands.Load;
        g["save"] = (Action<string>)commands.Save;
        g["makeuvs"] = (Action<string, string?, string?>)((map, projection, axis) => commands.MakeUVs(map,
            projection == null ? UVProjection.Planar : Enum.Parse<UVProjection>(projection, ignoreCase: true),
            axis == null ? Axis.Z : Enum.Parse<Axis>(axis, ignoreCase: true)));
        g["uvmap"] = (Func<string?, string?>)(name =>
        {
            if (name != null) commands.SetUVMap(name);
            return commands.Document.ActiveUVMap;
        });
        g["uvmaps"] = (Func<Table>)(() => List(lua, commands.UVMapNames));
        g["setuv"] = (Action<int, double, double>)((n, u, v) => commands.SetUV(n - 1, new Vec2(u, v)));
        g["getuv"] = (Func<int, DynValue>)(n => commands.GetUV(n - 1) is { } uv
            ? DynValue.NewTuple(DynValue.NewNumber(uv.X), DynValue.NewNumber(uv.Y))
            : DynValue.Nil);
        g["texture"] = (Action<string, string, Table?>)((surface, image, options) =>
        {
            var o = options == null ? [] : ToArgs(options);
            var channel = o.TryGetValue("Channel", out var c) && c is string cs ? Enum.Parse<TextureChannel>(cs, ignoreCase: true) : TextureChannel.Color;
            if (!Path.HasExtension(image) && Procedurals.Find(image) is { } type)
            {
                SetProcedural(commands, surface, type, channel, o);
                return;
            }
            commands.SetTexture(surface, image, channel, t => SetTextureOptions(t, o, commands.Document, surface));
        });
        g["proctex"] = (Action<string, string, Table?>)((surface, kind, options) =>
        {
            var o = options == null ? [] : ToArgs(options);
            var channel = o.TryGetValue("Channel", out var c) && c is string cs ? Enum.Parse<TextureChannel>(cs, ignoreCase: true) : TextureChannel.Color;
            var type = Procedurals.Find(kind) ?? throw new ArgumentException($"There's no procedural texture \"{kind}\": the kinds are {Procedurals.Keys}.");
            SetProcedural(commands, surface, type, channel, o);
        });
        g["notexture"] = (Func<string, string?, int>)((surface, channel) =>
            commands.ClearTextures(surface, channel == null ? null : Enum.Parse<TextureChannel>(channel, ignoreCase: true)));
        g["checkmesh"] = (Func<Table>)(() => CheckMesh(lua, commands.Document));
        g["slice"] = (Func<string, Table?, Table>)((path, options) =>
        {
            var job = commands.Slice(path, SliceOptions(commands.PrintSetup, options == null ? [] : ToArgs(options)));
            var t = new Table(lua);
            var size = job.Model.Bounds.Size;
            t["file"] = path;
            t["layers"] = job.Stats.Layers;
            t["time"] = job.Stats.Time;
            t["minutes"] = Math.Round(job.Stats.Seconds / 60, 1);
            t["grams"] = Math.Round(job.Stats.Grams, 1);
            t["filament_m"] = Math.Round(job.Stats.FilamentMm / 1000, 3);
            t["size"] = List(lua, new object[] { size.X, size.Y, size.Z });
            t["printable"] = job.Model.Printable;
            t["warnings"] = List(lua, job.Warnings.Cast<object>().ToArray());
            return t;
        });
        g["backdrop"] = (Func<string, string?, Table?, string?>)((view, image, options) =>
        {
            var v = BackdropViewOf(view);
            var o = options == null ? [] : ToArgs(options);
            if (image == null)
            {
                commands.ChangeBackdrop(v, b => BackdropOptions(b, v, o, commands));
                return null;
            }
            var (_, problem) = commands.SetBackdrop(v, image, b => BackdropOptions(b, v, o, commands));
            if (problem != null) commands.Print(problem);
            return problem;
        });
        g["nobackdrop"] = (Func<string?, int>)(view => commands.ClearBackdrop(view == null ? null : BackdropViewOf(view)));
        LuaSandbox.Run(lua, code, timeLimit);
    }

    /// <summary>A texture layer's settings from a macro's options table (names as in the Macro Reference, any case).</summary>
    private static void SetTextureOptions(TextureLayer t, Dictionary<string, object?> o, Document doc, string surface)
    {
        T Enum<T>(object? v) where T : struct, Enum => v is string s ? System.Enum.Parse<T>(s, ignoreCase: true) : throw new ArgumentException($"{typeof(T).Name} must be a name.");
        double Num(object? v) => v is double d ? d : throw new ArgumentException("A number is needed.");
        Vec3 Vec(object? v) => v is double[] a && a.Length >= 3 ? new Vec3(a[0], a[1], a[2]) : throw new ArgumentException("A vector is needed: {x, y, z}.");
        bool fit = false;
        foreach (var (key, v) in o)
            switch (key.ToLowerInvariant())
            {
                case "channel": break;
                case "projection": t.Projection = Enum<TextureProjection>(v); break;
                case "axis": t.Axis = Enum<Axis>(v); break;
                case "uvmap": t.UVMap = v as string ?? throw new ArgumentException("UVMap must be a name."); break;
                case "center": t.Center = Vec(v); break;
                case "size": t.Size = Vec(v); break;
                case "fit": fit = v is true; break;
                case "widthtiling": t.WidthTiling = Enum<TextureTiling>(v); break;
                case "heighttiling": t.HeightTiling = Enum<TextureTiling>(v); break;
                case "widthwrap": t.WidthWrap = Num(v); break;
                case "heightwrap": t.HeightWrap = Num(v); break;
                case "opacity": t.Opacity = Math.Clamp(Num(v), 0, 1); break;
                case "blend": t.Blend = Enum<TextureBlend>(v); break;
                case "amplitude": t.Amplitude = Num(v); break;
                case "pixelblending": t.PixelBlending = v is true; break;
                case "negative": t.Negative = v is true; break;
                default: throw new ArgumentException($"texture() has no option \"{key}\".");
            }
        if (fit)
            (t.Center, t.Size) = TextureMapping.AutoSize(TextureMapping.SurfaceBounds(doc.Layers.Select(l => l.Mesh), doc.SurfaceIndex(surface)));
    }

    /// <summary>A procedural texture layer from a macro's options, checked on a spare layer first so a bad option changes nothing.</summary>
    private static void SetProcedural(Commands commands, string surface, ProceduralType type, TextureChannel channel, Dictionary<string, object?> o)
    {
        var probe = new TextureLayer { Channel = channel };
        Procedurals.Apply(probe, type);
        SetProceduralOptions(probe, type, o, null, surface);
        bool newLayer = o.TryGetValue("NewLayer", out var n) && n is true;
        commands.SetProcedural(surface, type, channel, newLayer, t => SetProceduralOptions(t, type, o, commands.Document, surface));
    }

    /// <summary>A procedural layer's settings from a macro's options table (names as in the Macro Reference, any case).</summary>
    private static void SetProceduralOptions(TextureLayer t, ProceduralType type, Dictionary<string, object?> o, Document? doc, string surface)
    {
        T Enum<T>(object? v) where T : struct, Enum => v is string s ? System.Enum.Parse<T>(s, ignoreCase: true) : throw new ArgumentException($"{typeof(T).Name} must be a name.");
        double Num(object? v) => v is double d ? d : throw new ArgumentException("A number is needed.");
        Vec3 Vec(object? v) => v is double[] a && a.Length >= 3 ? new Vec3(a[0], a[1], a[2]) : throw new ArgumentException("A vector is needed: {x, y, z}.");
        bool fit = false;
        foreach (var (key, v) in o)
            switch (key.ToLowerInvariant())
            {
                case "channel" or "newlayer": break;
                case "axis": t.Axis = Enum<Axis>(v); break;
                case "center": t.Center = Vec(v); break;
                case "size": t.Size = Vec(v); break;
                case "fit": fit = v is true; break;
                case "opacity": t.Opacity = Math.Clamp(Num(v), 0, 1); break;
                case "blend": t.Blend = Enum<TextureBlend>(v); break;
                case "amplitude": t.Amplitude = Num(v); break;
                case "negative": t.Negative = v is true; break;
                case "world" or "worldcoordinates": t.WorldCoordinates = v is true; break;
                case "color" or "colour" or "texturecolor":
                    var c = Vec(v);
                    if (new[] { c.X, c.Y, c.Z }.Any(x => x < 0 || x > 255)) throw new ArgumentException("Color is {r, g, b}, each 0 to 255.");
                    t.TextureColor = new Rgb((byte)Math.Round(c.X), (byte)Math.Round(c.Y), (byte)Math.Round(c.Z));
                    break;
                case "value": t.Value = Num(v); break;
                case "falloff": t.Falloff = Vec(v); break;
                case "projection" or "uvmap" or "widthtiling" or "heighttiling" or "widthwrap" or "heightwrap" or "pixelblending":
                    throw new ArgumentException($"{key} is for image textures; a procedural is worked out in 3D from Center, Size and Axis.");
                default:
                    if (type.Find(key) is { } p) Procedurals.Set(t, p.Name, Num(v));
                    else throw new ArgumentException(type.Parameters.Count == 0
                        ? $"proctex() has no option \"{key}\" (and {type.Name} has no settings of its own)."
                        : $"proctex() has no option \"{key}\"; {type.Name}'s settings are {string.Join(", ", type.Parameters.Select(x => x.Name.Replace(" ", "")))}.");
                    break;
            }
        if (fit && doc != null)
            (t.Center, t.Size) = TextureMapping.AutoSize(TextureMapping.SurfaceBounds(doc.Layers.Select(l => l.Mesh), doc.SurfaceIndex(surface)));
    }

    private static BackdropView BackdropViewOf(string view) => Enum.TryParse<BackdropView>(view, ignoreCase: true, out var v) && Enum.IsDefined(v)
        ? v : throw new ArgumentException($"There's no \"{view}\" view for a backdrop: use \"top\", \"front\" or \"side\".");

    /// <summary>A backdrop's settings from a macro's options table (names as in the Macro Reference, any case).</summary>
    private static Backdrop BackdropOptions(Backdrop b, BackdropView view, Dictionary<string, object?> o, Commands commands)
    {
        double Num(object? v, string name) => v is double d ? d : throw new ArgumentException($"backdrop()'s {name} must be a number.");
        bool fit = false;
        double? pixel = null;
        foreach (var (key, v) in o)
            switch (key.ToLowerInvariant())
            {
                case "center" or "centre":
                    b = b with { Center = v is double[] { Length: 2 } a ? new Vec2(a[0], a[1]) : throw new ArgumentException("backdrop()'s Center is {a, b}: metres on the view's two axes.") };
                    break;
                case "size" or "width": b = b with { Width = Num(v, key) }; break;
                case "height": b = b with { Height = v == null ? null : Num(v, key) }; break;
                case "brightness": b = b with { Brightness = Num(v, key) }; break;
                case "contrast": b = b with { Contrast = Num(v, key) }; break;
                case "opacity": b = b with { Opacity = Num(v, key) }; break;
                case "invert": b = b with { Invert = v is true }; break;
                case "visible": b = b with { Visible = v is not false }; break;
                case "fit": fit = v is true; break;
                case "pixelsize": pixel = Num(v, key); break;
                default: throw new ArgumentException($"backdrop() has no option \"{key}\". It has Center, Size, Height, Fit, PixelSize, Brightness, Contrast, Opacity, Invert and Visible.");
            }
        if (pixel is { } m) b = b with { Width = commands.BackdropWidthFromPixels(b, m), Height = null };
        if (fit)
        {
            var (center, width) = commands.BackdropFit(view, b);
            b = b with { Center = center, Width = width };
        }
        return b.Checked();
    }

    /// <summary>The mesh check of the foreground layers as a Lua table (see the Macro Reference).</summary>
    /// <summary>A print setup from slice()'s options table (names as in the Macro Reference, any case), over the one given.</summary>
    private static Slicing.PrintSetup SliceOptions(Slicing.PrintSetup setup, Dictionary<string, object?> o)
    {
        double Num(string key, object? v) => v is double d ? d : throw new ArgumentException($"slice's {key} must be a number.");
        int Whole(string key, object? v) => Num(key, v) is var d && d == Math.Floor(d) ? (int)d : throw new ArgumentException($"slice's {key} must be a whole number.");
        bool Yes(string key, object? v) => v is bool b ? b : throw new ArgumentException($"slice's {key} must be true or false.");
        string Text(string key, object? v) => v is string t ? t : throw new ArgumentException($"slice's {key} must be text.");
        // A profile first, so the other options change it.
        if (o.FirstOrDefault(kv => kv.Key.Equals("profile", StringComparison.OrdinalIgnoreCase)) is { Key: not null } prof)
            setup = setup.WithProfile(Text("profile", prof.Value));
        var s = setup.Settings;
        var p = setup.Placement;
        foreach (var (key, v) in o)
            switch (key.ToLowerInvariant())
            {
                case "printer":
                    string pn = Text(key, v);
                    setup = setup with { Printer = setup.AllPrinters.FirstOrDefault(x => string.Equals(x.Name, pn, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"There's no printer \"{pn}\": {string.Join(", ", setup.AllPrinters.Select(x => x.Name))}.") };
                    break;
                case "material":
                    string mn = Text(key, v);
                    setup = setup with { Material = setup.AllMaterials.FirstOrDefault(x => string.Equals(x.Name, mn, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"There's no material \"{mn}\": {string.Join(", ", setup.AllMaterials.Select(x => x.Name))}.") };
                    break;
                case "profile": break;
                case "layer": s = s with { LayerHeight = Num(key, v) }; break;
                case "infill": s = s with { InfillPercent = Num(key, v) }; break;
                case "pattern":
                    s = s with { Infill = Enum.TryParse<Slicing.InfillPattern>(Text(key, v), ignoreCase: true, out var ip) ? ip : throw new ArgumentException($"slice's pattern is \"lines\", \"grid\" or \"gyroid\", not \"{v}\".") };
                    break;
                case "walls": s = s with { Walls = Whole(key, v) }; break;
                case "supports": s = s with { Supports = Yes(key, v) }; break;
                case "extruder": s = s with { ModelExtruder = Whole(key, v) - 1 }; break;
                case "supportextruder": s = s with { SupportExtruder = Whole(key, v) - 1 }; break;
                case "supportmaterial":
                    string sm = Text(key, v);
                    setup = setup with { SupportMaterial = setup.AllMaterials.FirstOrDefault(x => string.Equals(x.Name, sm, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"There's no material \"{sm}\": {string.Join(", ", setup.AllMaterials.Select(x => x.Name))}.") };
                    break;
                case "bedonly": s = s with { SupportOn = Yes(key, v) ? Slicing.SupportPlacement.BedOnly : Slicing.SupportPlacement.Everywhere }; break;
                case "brim": s = s with { BrimLines = Whole(key, v) }; break;
                case "scale": p = p with { ScalePercent = Num(key, v) }; break;
                case "rotate": p = p with { RotateDegrees = Num(key, v) }; break;
                case "copies": p = p with { Copies = Whole(key, v) }; break;
                case "gap": p = p with { CopyGap = Num(key, v) }; break;
                case "layflat":
                    p = p with
                    {
                        LayFlat = Text(key, v).ToLowerInvariant() switch
                        {
                            "modelled" or "none" => Slicing.LayFlat.AsModelled,
                            "largest" => Slicing.LayFlat.LargestFace,
                            "selected" => Slicing.LayFlat.SelectedFace,
                            var x => throw new ArgumentException($"slice's layflat is \"modelled\", \"largest\" or \"selected\", not \"{x}\"."),
                        },
                    };
                    break;
                default: throw new ArgumentException($"slice has no option \"{key}\": profile, printer, material, layer, infill, pattern, walls, supports, bedonly, extruder, supportextruder, supportmaterial, brim, scale, rotate, copies, gap, layflat.");
            }
        return setup with { Settings = s, Placement = p };
    }

    private static Table CheckMesh(Script lua, Document doc)
    {
        var r = Geometry.MeshCheck.Document(doc);
        Table V(Vec3 v) => List(lua, new object[] { v.X, v.Y, v.Z });
        var t = new Table(lua);
        t["verdict"] = r.Verdict;
        t["printable"] = r.Printable;
        t["watertight"] = r.Watertight;
        t["points"] = r.Points;
        t["polygons"] = r.Polygons;
        t["faces"] = r.Faces;
        t["area"] = r.Area;
        if (r.Volume is { } v) t["volume"] = v;
        if (r.CenterOfMass is { } c) t["center"] = V(c);
        var b = r.Bounds;
        if (!b.IsEmpty)
        {
            t["size"] = V(b.Size);
            t["min"] = V(b.Min);
            t["max"] = V(b.Max);
        }
        t["shells"] = r.Shells;
        t["holes"] = r.Holes;
        t["boundary_edges"] = r.BoundaryEdges;
        t["nonmanifold_edges"] = r.NonManifoldEdges;
        t["inconsistent_edges"] = r.InconsistentEdges;
        t["flipped_polygons"] = r.FlippedPolygons;
        t["inward_shells"] = r.InwardShells;
        t["cavities"] = r.Cavities;
        t["degenerate_polygons"] = r.DegeneratePolygons;
        t["nonplanar_polygons"] = r.NonPlanarPolygons;
        t["duplicate_points"] = r.DuplicatePoints;
        t["unused_points"] = r.UnusedPoints;
        t["points_and_lines"] = r.PointsAndLines;
        t["report"] = List(lua, r.Lines());
        return t;
    }

    private static Table List(Script lua, IEnumerable<object> items) => LuaSandbox.List(lua, items);

    private static Table List(Script lua, IEnumerable<int> items) => LuaSandbox.List(lua, items);

    private static Table List(Script lua, IEnumerable<string> items) => LuaSandbox.List(lua, items);

    private static IEnumerable<int> Indices(Table t) => LuaSandbox.Indices(t);

    private static Dictionary<string, object?> ToArgs(Table args) => LuaSandbox.ToArgs(args);
}

/// <summary>A macro failed; the message says where and why.</summary>
public sealed class MacroException(string message, Exception inner) : Exception(message, inner);
