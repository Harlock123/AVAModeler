using MoonSharp.Interpreter;

namespace AVAModeler.Tools.Tests;

/// <summary>
/// The macro sandbox for other apps (AVALightWave's scene macros): its own globals and print sink, the same time limit,
/// no way out to the system, and errors as <see cref="MacroException"/>; and the table helpers.
/// </summary>
public class LuaSandboxTests
{
    [Fact]
    public void A_callers_own_globals_are_called_and_print_goes_to_its_sink()
    {
        var lights = new List<(string, double)>();
        var printed = new List<string>();
        LuaSandbox.Run("""
            light("Key", 0.8)
            light("Fill", 0.3)
            print("two lights", #items())
            """, lua =>
        {
            lua.Globals["light"] = (Action<string, double>)((name, intensity) => lights.Add((name, intensity)));
            lua.Globals["items"] = (Func<Table>)(() => LuaSandbox.List(lua, lights.Select(l => l.Item1)));
        }, printed.Add, TimeSpan.FromSeconds(10));
        Assert.Equal([("Key", 0.8), ("Fill", 0.3)], lights);
        Assert.Equal(["two lights\t2"], printed);
    }

    [Fact]
    public void The_time_limit_stops_a_loop_that_never_ends()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var e = Assert.Throws<MacroException>(() => LuaSandbox.Run("while true do end", null, null, TimeSpan.FromMilliseconds(200)));
        Assert.IsType<TimeoutException>(e.InnerException);
        Assert.Contains("stopped after running", e.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        // Without a limit, code that ends runs straight through.
        int n = 0;
        LuaSandbox.Run("for i = 1, 10 do bump() end", lua => lua.Globals["bump"] = (Action)(() => n++));
        Assert.Equal(10, n);
    }

    [Theory]
    [InlineData("os.execute('true')")]
    [InlineData("io.open('/etc/passwd')")]
    [InlineData("require('os')")]
    [InlineData("dofile('x.lua')")]
    public void There_is_no_way_out_to_the_system(string code) =>
        Assert.Throws<MacroException>(() => LuaSandbox.Run(code, null, null, TimeSpan.FromSeconds(10)));

    [Fact]
    public void Errors_come_back_as_macro_exceptions_with_the_reason()
    {
        // A Lua error says where.
        var syntax = Assert.Throws<MacroException>(() => LuaSandbox.Run("x = = 1", null));
        Assert.Contains("chunk", syntax.Message);
        Assert.Contains("boom", Assert.Throws<MacroException>(() => LuaSandbox.Run("error('boom')", null, null, TimeSpan.FromSeconds(10))).Message);
        // What a caller's function throws comes back with its own message.
        var refused = Assert.Throws<MacroException>(() => LuaSandbox.Run("pick('nobody')",
            lua => lua.Globals["pick"] = (Action<string>)(name => throw new ArgumentException($"There's no item \"{name}\"."))));
        Assert.Contains("There's no item \"nobody\"", refused.Message);
        var busy = Assert.Throws<MacroException>(() => LuaSandbox.Run("go()",
            lua => lua.Globals["go"] = (Action)(() => throw new InvalidOperationException("Not now.")), null, TimeSpan.FromSeconds(10)));
        Assert.Contains("Not now.", busy.Message);
    }

    [Fact]
    public void Tables_convert_to_plain_values()
    {
        var lua = LuaSandbox.Create();
        Dictionary<string, object?>? args = null;
        double[]? numbers = null;
        List<int>? indices = null;
        List<string>? names = null;
        lua.Globals["take"] = (Action<Table, Table, Table, Table>)((a, n, i, s) =>
        {
            args = LuaSandbox.ToArgs(a);
            numbers = LuaSandbox.Numbers(n);
            indices = LuaSandbox.Indices(i);
            names = LuaSandbox.Strings(s);
        });
        LuaSandbox.Run(lua, "take({ Size = 2, Name = 'box', On = true, Center = {1, 2, 3} }, {0.5, 1.5}, {1, 3}, {'a', 'b'})");
        Assert.Equal(2.0, args!["size"]);
        Assert.Equal("box", args["NAME"]);
        Assert.Equal(true, args["On"]);
        Assert.Equal([1.0, 2.0, 3.0], (double[])args["center"]!);
        Assert.Equal([0.5, 1.5], numbers!);
        Assert.Equal([0, 2], indices!);
        Assert.Equal(["a", "b"], names!);
        var list = LuaSandbox.List(lua, new[] { 1.5, 2.5 });
        Assert.Equal(2, list.Length);
        Assert.Equal(2.5, list.Get(2).Number);
    }
}
