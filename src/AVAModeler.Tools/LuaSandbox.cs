using MoonSharp.Interpreter;

namespace AVAModeler.Tools;

/// <summary>
/// The Lua sandbox AVA Modeler's macros run in, for other apps on this engine (AVALightWave's scene macros) to run their
/// own Lua the same way: MoonSharp with <see cref="CoreModules.Preset_SoftSandbox"/> (no <c>os</c>, <c>io</c>,
/// <c>load</c> of files or other way out), an optional time limit (the code runs as a coroutine that yields every
/// thousand instructions, so a loop that never ends is stopped), and failures as <see cref="MacroException"/>. The
/// caller puts its own functions in <see cref="Script.Globals"/> and gives a sink for <c>print</c>.
/// <code>
/// LuaSandbox.Run(code, lua => lua.Globals["light"] = (Action&lt;string&gt;)(name => ...), output.Add, TimeSpan.FromSeconds(30));
/// </code>
/// </summary>
public static class LuaSandbox
{
    /// <summary>A fresh sandboxed interpreter, its <c>print</c> going to <paramref name="print"/> (nowhere if null).</summary>
    public static Script Create(Action<string>? print = null)
    {
        var lua = new Script(CoreModules.Preset_SoftSandbox);
        lua.Options.DebugPrint = print ?? (_ => { });
        return lua;
    }

    /// <summary>
    /// Runs <paramref name="code"/> in a fresh sandbox: <paramref name="register"/> puts the caller's functions in
    /// <c>lua.Globals</c> first. See <see cref="Run(Script, string, TimeSpan?)"/> for the time limit and errors.
    /// </summary>
    public static void Run(string code, Action<Script>? register, Action<string>? print = null, TimeSpan? timeLimit = null)
    {
        var lua = Create(print);
        register?.Invoke(lua);
        Run(lua, code, timeLimit);
    }

    /// <summary>
    /// Runs <paramref name="code"/> in an interpreter made by <see cref="Create"/>. Errors come back as
    /// <see cref="MacroException"/> with the Lua message (where and why), as do the ArgumentException,
    /// InvalidOperationException, FormatException and index exceptions the caller's functions throw. With a
    /// <paramref name="timeLimit"/>, code still running after that long is stopped with a <see cref="MacroException"/>
    /// (its inner exception a <see cref="TimeoutException"/>).
    /// </summary>
    public static void Run(Script lua, string code, TimeSpan? timeLimit = null)
    {
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

    /// <summary>A Lua list (a table numbered from 1) of plain values (numbers, strings, booleans, tables).</summary>
    public static Table List(Script lua, IEnumerable<object> items)
    {
        var t = new Table(lua);
        foreach (var i in items) t.Append(DynValue.FromObject(lua, i));
        return t;
    }

    /// <inheritdoc cref="List(Script, IEnumerable{object})"/>
    public static Table List(Script lua, IEnumerable<int> items) => List(lua, items.Cast<object>());

    /// <inheritdoc cref="List(Script, IEnumerable{object})"/>
    public static Table List(Script lua, IEnumerable<double> items) => List(lua, items.Cast<object>());

    /// <inheritdoc cref="List(Script, IEnumerable{object})"/>
    public static Table List(Script lua, IEnumerable<string> items) => List(lua, items.Cast<object>());

    /// <summary>A Lua list of 1-based numbers as 0-based indices.</summary>
    public static List<int> Indices(Table t) => t.Values.Select(v => (int)v.Number - 1).ToList();

    /// <summary>A Lua list's numbers (anything else counts as 0).</summary>
    public static double[] Numbers(Table t) => t.Values.Select(v => v.Type == DataType.Number ? v.Number : 0).ToArray();

    /// <summary>A Lua list's strings, in order (anything else as Lua prints it).</summary>
    public static List<string> Strings(Table t) => t.Values.Select(v => v.Type == DataType.String ? v.String : v.ToPrintString()).ToList();

    /// <summary>
    /// A Lua table of options as plain values, by name (any case): numbers as double, strings, booleans, and a table
    /// (such as {x, y, z}) as a double[] of its numbers. Anything else is null.
    /// </summary>
    public static Dictionary<string, object?> ToArgs(Table args)
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
