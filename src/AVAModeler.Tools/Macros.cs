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
        ("selbox(x0, y0, z0, x1, y1, z1)", "selects (in the current mode) the points inside a box, or the polygons wholly inside it"),
        ("selectall() / deselect() / invert()", "selection"),
        ("setlayers({fg...}, {bg...})", "which layers are foreground and background"),
        ("undo() / redo()", "the history"),
        ("new() / load(path) / save(path)", "files"),
        ("makeuvs(map, projection, axis)", "lays the selection (or all) out in a UV map: \"planar\", \"cylindrical\", \"spherical\", \"cubic\" or \"atlas\", along \"x\", \"y\" or \"z\" (or tool(\"Make UVs\", {...}) for every setting)"),
        ("uvmap(name) / uvmap()", "chooses the UV map to work on (the UV view shows it), or returns the one chosen"),
        ("uvmaps()", "the names of the UV maps"),
        ("setuv(n, u, v) / getuv(n)", "a point's UVs in the chosen UV map"),
        ("texture(surface, image, {Option = value, ...})", "gives a surface an image texture; options: Channel (\"color\", \"diffuse\", \"specular\", \"luminosity\", \"reflection\", \"transparency\", \"bump\"), Projection (\"planar\", \"cylindrical\", \"spherical\", \"cubic\", \"uv\"), Axis, UVMap, Center, Size, Fit (automatic sizing), WidthTiling/HeightTiling (\"reset\", \"repeat\", \"mirror\", \"edge\"), WidthWrap/HeightWrap, Opacity, Blend, Amplitude"),
        ("notexture(surface, channel)", "removes a surface's textures (for one channel, or all)"),
        ("print(...)", "writes to the macro's output"),
    ];

    /// <summary>
    /// Runs a macro against the command API. Errors come back as <see cref="MacroException"/> with the Lua message.
    /// With a <paramref name="timeLimit"/>, a macro still running after that long is stopped (with a
    /// <see cref="MacroException"/>), so a script that never ends can't hang its caller.
    /// </summary>
    public static void Run(string code, Commands commands, TimeSpan? timeLimit = null)
    {
        var lua = new Script(CoreModules.Preset_SoftSandbox);
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
            commands.SetTexture(surface, image, channel, t => SetTextureOptions(t, o, commands.Document, surface));
        });
        g["notexture"] = (Func<string, string?, int>)((surface, channel) =>
            commands.ClearTextures(surface, channel == null ? null : Enum.Parse<TextureChannel>(channel, ignoreCase: true)));
        lua.Options.DebugPrint = commands.Print;
        try
        {
            if (timeLimit is { } limit) RunLimited(lua, code, limit);
            else lua.DoString(code);
        }
        catch (InterpreterException e)
        {
            throw new MacroException(e.DecoratedMessage ?? e.Message, e);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new MacroException(e.Message, e);
        }
    }

    /// <summary>Runs the code as a coroutine that yields every so many instructions, so the clock can be checked between.</summary>
    private static void RunLimited(Script lua, string code, TimeSpan limit)
    {
        var co = lua.CreateCoroutine(lua.LoadString(code)).Coroutine;
        co.AutoYieldCounter = 1000;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        co.Resume();
        while (co.State == CoroutineState.ForceSuspended)
        {
            if (clock.Elapsed > limit)
                throw new MacroException($"The macro was stopped after running for {limit.TotalSeconds:0.#} seconds.", new TimeoutException());
            co.Resume();
        }
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

    private static Table List(Script lua, IEnumerable<object> items)
    {
        var t = new Table(lua);
        foreach (var i in items) t.Append(DynValue.FromObject(lua, i));
        return t;
    }

    private static Table List(Script lua, IEnumerable<int> items) => List(lua, items.Cast<object>());

    private static Table List(Script lua, IEnumerable<string> items) => List(lua, items.Cast<object>());

    /// <summary>A Lua list of 1-based numbers as 0-based indices.</summary>
    private static IEnumerable<int> Indices(Table t) => t.Values.Select(v => (int)v.Number - 1).ToList();

    /// <summary>A Lua table of parameters as plain values: numbers, strings, booleans, and {x, y, z} as a vector.</summary>
    private static Dictionary<string, object?> ToArgs(Table args)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in args.Pairs)
        {
            object? v = pair.Value.Type switch
            {
                DataType.Number => pair.Value.Number,
                DataType.String => pair.Value.String,
                DataType.Boolean => pair.Value.Boolean,
                DataType.Table => pair.Value.Table.Values.Select(x => x.Number).ToArray(),
                _ => null,
            };
            d[pair.Key.String ?? pair.Key.ToPrintString()] = v;
        }
        return d;
    }
}

/// <summary>A macro failed; the message says where and why.</summary>
public sealed class MacroException(string message, Exception inner) : Exception(message, inner);
