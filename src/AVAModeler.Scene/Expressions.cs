using System.Collections.Concurrent;
using System.Globalization;
using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// An expression: one of the item's nine motion channels set by a formula at every frame, after its keys, path and
/// IK and the modifiers before it. The formula can read <c>frame</c>, <c>time</c> (seconds), <c>fps</c>, <c>value</c>
/// (the channel's value before it), <c>pi</c> and <c>e</c>; other items' channels as <c>[Name.position.x]</c>,
/// <c>[Name.heading]</c>, <c>[Name.scale.y]</c> (in the item's parent's space) or <c>[Name.world.y]</c> (its pivot in
/// the world); and the usual maths (+ − × ÷, ^ for powers, % for remainders, comparisons giving 1 or 0, and sin, cos,
/// tan, asin, acos, atan, atan2 in radians, deg, rad, sqrt, abs, sign, min, max, floor, ceil, round, clamp, lerp, step,
/// smoothstep, pow, exp, log, if, noise). It can only read: nothing it does changes the scene.
/// </summary>
public sealed record ExpressionModifier : MotionModifier
{
    public override string Kind => "expression";

    /// <summary>The channel it sets.</summary>
    public Channel Channel { get; init; } = Channel.PositionY;

    /// <summary>The formula, e.g. <c>[Wheel.position.x] / 0.3 * 57.2958</c>.</summary>
    public string Formula { get; init; } = "value";

    public override MotionModifier Checked()
    {
        if (!Enum.IsDefined(Channel)) throw new ArgumentException("An expression's channel must be one of the nine motion channels.");
        if (string.IsNullOrWhiteSpace(Formula)) throw new ArgumentException("An expression needs a formula, e.g. \"value + sin(time * 2) * 0.5\".");
        if (Formula.Length > 2000) throw new ArgumentException("An expression's formula is at most 2000 characters.");
        Expressions.Compile(Formula);
        return this;
    }

    /// <summary>The items its formula reads, by name.</summary>
    public IReadOnlyList<string> ReadsFrom => Expressions.Compile(Formula).References;

    internal override MotionPose Apply(SceneItem item, double frame, MotionPose pose)
    {
        double before = pose[Channel];
        double v = Expressions.Compile(Formula).Evaluate(item, frame, before);
        return double.IsFinite(v) ? pose.Add(Channel, v - before) : pose;
    }
}

/// <summary>Formulas for <see cref="ExpressionModifier"/>: parsed once into a tree, then evaluated at any frame.</summary>
public static class Expressions
{
    private static readonly ConcurrentDictionary<string, Compiled> Cache = new(StringComparer.Ordinal);

    /// <summary>The channels a reference may read, after the item's name.</summary>
    public static readonly IReadOnlyList<string> Readable =
        ["position.x", "position.y", "position.z", "heading", "pitch", "bank", "scale.x", "scale.y", "scale.z", "world.x", "world.y", "world.z"];

    public static readonly IReadOnlyList<string> Functions =
    [
        "sin", "cos", "tan", "asin", "acos", "atan", "atan2", "deg", "rad", "sqrt", "abs", "sign", "min", "max", "floor", "ceil", "round",
        "clamp", "lerp", "step", "smoothstep", "pow", "exp", "log", "if", "noise",
    ];

    /// <summary>A formula ready to evaluate, or an <see cref="ArgumentException"/> saying where it's wrong.</summary>
    public static Compiled Compile(string formula)
    {
        if (Cache.TryGetValue(formula, out var c)) return c;
        c = new Parser(formula).Parse();
        if (Cache.Count > 4096) Cache.Clear();
        Cache[formula] = c;
        return c;
    }

    /// <summary>A formula as a tree.</summary>
    public sealed class Compiled
    {
        internal Compiled(Node root, IReadOnlyList<string> references) => (Root, References) = (root, references);

        internal Node Root { get; }
        public IReadOnlyList<string> References { get; }

        /// <summary>Its value for an item at a frame, with <paramref name="value"/> the channel's value before it.</summary>
        public double Evaluate(SceneItem item, double frame, double value) => Root.Eval(new Context(item, frame, value));
    }

    internal readonly record struct Context(SceneItem Item, double Frame, double Value);

    internal abstract class Node
    {
        public abstract double Eval(Context c);
    }

    private sealed class Number(double v) : Node
    {
        public override double Eval(Context c) => v;
    }

    private sealed class Variable(string name) : Node
    {
        public override double Eval(Context c) => name switch
        {
            "frame" => c.Frame,
            "time" => c.Frame / Fps(c),
            "fps" => Fps(c),
            "value" => c.Value,
            "pi" => Math.PI,
            _ => Math.E,
        };

        private static double Fps(Context c) => c.Item.Scene is { FramesPerSecond: > 0 } s ? s.FramesPerSecond : 30;
    }

    private sealed class Reference(string item, string channel) : Node
    {
        public override double Eval(Context c)
        {
            if (c.Item.Scene?.Find(item) is not { } it) return double.NaN;
            return channel switch
            {
                "position.x" => it.MotionPositionAt(c.Frame).X,
                "position.y" => it.MotionPositionAt(c.Frame).Y,
                "position.z" => it.MotionPositionAt(c.Frame).Z,
                "heading" => it.MotionRotationAt(c.Frame).X,
                "pitch" => it.MotionRotationAt(c.Frame).Y,
                "bank" => it.MotionRotationAt(c.Frame).Z,
                "scale.x" => it.MotionScaleAt(c.Frame).X,
                "scale.y" => it.MotionScaleAt(c.Frame).Y,
                "scale.z" => it.MotionScaleAt(c.Frame).Z,
                "world.x" => it.WorldPosition(c.Frame).X,
                "world.y" => it.WorldPosition(c.Frame).Y,
                _ => it.WorldPosition(c.Frame).Z,
            };
        }
    }

    private sealed class Unary(char op, Node a) : Node
    {
        public override double Eval(Context c) => op == '-' ? -a.Eval(c) : a.Eval(c) == 0 ? 1 : 0;
    }

    private sealed class Binary(string op, Node a, Node b) : Node
    {
        public override double Eval(Context c)
        {
            double x = a.Eval(c), y = b.Eval(c);
            return op switch
            {
                "+" => x + y, "-" => x - y, "*" => x * y, "/" => x / y, "%" => x - y * Math.Floor(x / y), "^" => Math.Pow(x, y),
                "<" => x < y ? 1 : 0, ">" => x > y ? 1 : 0, "<=" => x <= y ? 1 : 0, ">=" => x >= y ? 1 : 0,
                "==" => x == y ? 1 : 0, "!=" => x != y ? 1 : 0, "&&" => x != 0 && y != 0 ? 1 : 0, _ => x != 0 || y != 0 ? 1 : 0,
            };
        }
    }

    private sealed class Call(string name, Node[] args) : Node
    {
        public override double Eval(Context c)
        {
            if (name == "if") return args[0].Eval(c) != 0 ? args[1].Eval(c) : args[2].Eval(c);
            var v = new double[args.Length];
            for (int i = 0; i < v.Length; i++) v[i] = args[i].Eval(c);
            return name switch
            {
                "sin" => Math.Sin(v[0]), "cos" => Math.Cos(v[0]), "tan" => Math.Tan(v[0]), "asin" => Math.Asin(v[0]), "acos" => Math.Acos(v[0]),
                "atan" => Math.Atan(v[0]), "atan2" => Math.Atan2(v[0], v[1]), "deg" => v[0] * 180 / Math.PI, "rad" => v[0] * Math.PI / 180,
                "sqrt" => Math.Sqrt(v[0]), "abs" => Math.Abs(v[0]), "sign" => Math.Sign(v[0]), "min" => v.Min(), "max" => v.Max(),
                "floor" => Math.Floor(v[0]), "ceil" => Math.Ceiling(v[0]), "round" => Math.Round(v[0], MidpointRounding.AwayFromZero),
                "clamp" => Math.Clamp(v[0], Math.Min(v[1], v[2]), Math.Max(v[1], v[2])), "lerp" => v[0] + (v[1] - v[0]) * v[2],
                "step" => v[1] >= v[0] ? 1 : 0,
                "smoothstep" => Smooth(v[0], v[1], v[2]), "pow" => Math.Pow(v[0], v[1]), "exp" => Math.Exp(v[0]), "log" => Math.Log(v[0]),
                _ => Noise.Perlin(v[0], v.Length > 1 ? v[1] : 0.5, v.Length > 2 ? v[2] : 0.5),
            };
        }

        private static double Smooth(double a, double b, double x)
        {
            if (a == b) return x >= b ? 1 : 0;
            double t = Math.Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }
    }

    private static readonly Dictionary<string, (int Min, int Max)> Arity = new()
    {
        ["atan2"] = (2, 2), ["min"] = (2, 16), ["max"] = (2, 16), ["clamp"] = (3, 3), ["lerp"] = (3, 3), ["step"] = (2, 2),
        ["smoothstep"] = (3, 3), ["pow"] = (2, 2), ["if"] = (3, 3), ["noise"] = (1, 3),
    };

    /// <summary>A recursive-descent parser: or, and, comparison, sum, product, power, unary, atom.</summary>
    private sealed class Parser(string text)
    {
        private int _at;
        private readonly List<string> _refs = [];

        public Compiled Parse()
        {
            var root = Or();
            Space();
            if (_at < text.Length) throw Wrong($"\"{text[_at]}\" isn't expected here");
            return new Compiled(root, _refs.Distinct(StringComparer.Ordinal).ToList());
        }

        private ArgumentException Wrong(string what) =>
            new($"The formula \"{text}\" has a mistake at character {_at + 1}: {what}. A formula looks like \"value + sin(time * 3) * 0.2\" or \"[Wheel.position.x] / 0.3 * 57.3\".");

        private void Space()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at])) _at++;
        }

        private bool Take(string s)
        {
            Space();
            if (string.CompareOrdinal(text, _at, s, 0, s.Length) != 0) return false;
            _at += s.Length;
            return true;
        }

        private Node Or()
        {
            var a = And();
            while (Take("||")) a = new Binary("||", a, And());
            return a;
        }

        private Node And()
        {
            var a = Compare();
            while (Take("&&")) a = new Binary("&&", a, Compare());
            return a;
        }

        private Node Compare()
        {
            var a = Sum();
            foreach (var op in new[] { "<=", ">=", "==", "!=", "<", ">" })
                if (Take(op)) return new Binary(op, a, Sum());
            return a;
        }

        private Node Sum()
        {
            var a = Product();
            while (true)
            {
                if (Take("+")) a = new Binary("+", a, Product());
                else if (Take("-")) a = new Binary("-", a, Product());
                else return a;
            }
        }

        private Node Product()
        {
            var a = Power();
            while (true)
            {
                if (Take("*")) a = new Binary("*", a, Power());
                else if (Take("/")) a = new Binary("/", a, Power());
                else if (Take("%")) a = new Binary("%", a, Power());
                else return a;
            }
        }

        private Node Power()
        {
            var a = UnaryOp();
            return Take("^") ? new Binary("^", a, Power()) : a;
        }

        private Node UnaryOp()
        {
            if (Take("-")) return new Unary('-', UnaryOp());
            if (Take("+")) return UnaryOp();
            if (Take("!")) return new Unary('!', UnaryOp());
            return Atom();
        }

        private Node Atom()
        {
            Space();
            if (_at >= text.Length) throw Wrong("it ends too soon");
            char ch = text[_at];
            if (ch == '(')
            {
                _at++;
                var inner = Or();
                if (!Take(")")) throw Wrong("a \")\" is missing");
                return inner;
            }
            if (ch == '[')
            {
                int close = text.IndexOf(']', _at);
                if (close < 0) throw Wrong("a \"]\" is missing after an item's channel");
                string inside = text[(_at + 1)..close].Trim();
                var channel = Readable.FirstOrDefault(r => inside.EndsWith("." + r, StringComparison.OrdinalIgnoreCase));
                if (channel == null)
                    throw Wrong($"[{inside}] should be [item.channel], the channel one of {string.Join(", ", Readable)}");
                string item = inside[..^(channel.Length + 1)].Trim();
                if (item.Length == 0) throw Wrong("an item's name is missing before its channel");
                _at = close + 1;
                _refs.Add(item);
                return new Reference(item, channel);
            }
            if (char.IsDigit(ch) || ch == '.')
            {
                int start = _at;
                while (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] == '.')) _at++;
                if (_at < text.Length && text[_at] is 'e' or 'E' && _at + 1 < text.Length && (char.IsDigit(text[_at + 1]) || text[_at + 1] is '-' or '+'))
                {
                    _at += 2;
                    while (_at < text.Length && char.IsDigit(text[_at])) _at++;
                }
                if (!double.TryParse(text[start.._at], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) throw Wrong($"\"{text[start.._at]}\" isn't a number");
                return new Number(v);
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                int start = _at;
                while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == '_')) _at++;
                string name = text[start.._at].ToLowerInvariant();
                if (name is "frame" or "time" or "fps" or "value" or "pi" or "e") return new Variable(name);
                if (!Functions.Contains(name))
                    throw Wrong($"\"{name}\" isn't known (variables: frame, time, fps, value, pi, e; functions: {string.Join(", ", Functions)}; another item's channel goes in brackets: [Name.position.y])");
                if (!Take("(")) throw Wrong($"{name} needs its arguments in brackets, e.g. {name}(…)");
                var args = new List<Node>();
                if (!Take(")"))
                {
                    do args.Add(Or());
                    while (Take(","));
                    if (!Take(")")) throw Wrong($"a \")\" is missing after {name}'s arguments");
                }
                var (min, max) = Arity.TryGetValue(name, out var a) ? a : (1, 1);
                if (args.Count < min || args.Count > max)
                    throw Wrong($"{name} takes {(min == max ? min.ToString(CultureInfo.InvariantCulture) : $"{min} to {max}")} argument{(max == 1 ? "" : "s")}, not {args.Count}");
                return new Call(name, [.. args]);
            }
            throw Wrong($"\"{ch}\" isn't expected here");
        }
    }
}
