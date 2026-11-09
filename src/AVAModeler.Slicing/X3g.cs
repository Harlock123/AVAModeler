using System.Buffers.Binary;
using System.Globalization;

namespace AVAModeler.Slicing;

/// <summary>
/// A Sailfish (MakerBot-style) machine's numbers for .x3g: steps per millimetre on each axis, the fastest each may
/// go and home, and which way X and Y home. The Flashforge Creator Pro's are those of the Replicator 1 Dual it copies.
/// </summary>
public sealed record SailfishMachine(
    string Id, double XSteps, double YSteps, double ZSteps, double ASteps,
    double XYMaxFeed, double ZMaxFeed, double AMaxFeed, double XYHomeFeed, double ZHomeFeed, int HomeTimeout)
{
    /// <summary>The Flashforge Creator Pro (GPX's "fcp", a Replicator 1 Dual): 94.117647 steps/mm in X and Y, 400 in Z, 96.2752 on the extruders.</summary>
    public static readonly SailfishMachine CreatorPro = new("fcp", 94.117647, 94.117647, 400, 96.275201870333662468889989185642, 18000, 1170, 1600, 2500, 1100, 20);

    public static readonly IReadOnlyDictionary<string, SailfishMachine> ById = new Dictionary<string, SailfishMachine>(StringComparer.OrdinalIgnoreCase)
    {
        ["fcp"] = CreatorPro,
    };
}

/// <summary>
/// Turns G-code (the kind this slicer writes, with a Sailfish start and end) into .x3g, the binary file Sailfish
/// firmware prints from, as GPX does for the same commands: MakerBot's s3g/x3g command set, little-endian.
/// <list type="bullet">
/// <item>G0/G1: command 155 (queue extended point): X, Y and Z as absolute steps, the extruder as relative steps
/// (negative to push filament, as MakerBot extruders turn), the step rate of the longest axis, the distance in mm and
/// the feedrate × 64. Each axis is held to its fastest. A move just after homing, when the position isn't known, is
/// command 139 (absolute point at a fixed step rate) and must give X, Y and Z.</item>
/// <item>G161/G162: home to the minimum (131) or maximum (132); G92: set position (140); M132: recall the stored home
/// offsets (144); M104/M109 and M140/M190: temperatures (136 actions 3 and 31) and waits for them (135, 141) with
/// no timeout; M106/M107: the fan (136 action 12); M18/M84: steppers off (137).</item>
/// <item>Comments, G21, G90 and M83 (it's always millimetres, absolute XYZ, relative E), M82, G91 for the end
/// G-code's lift, and M73/M117 are understood; anything else is refused with its line.</item>
/// </list>
/// Rounding of the extruder's steps carries over from move to move, so no filament is lost to it.
/// </summary>
public static class X3g
{
    private const byte X = 1, Y = 2, Z = 4, A = 8, B = 16;
    private const ushort NoTimeout = 0xFFFF;

    public static byte[] Convert(string gcode, SailfishMachine machine, string buildName = "AVA Modeler")
    {
        var o = new List<byte>(gcode.Length / 2);
        void U8(int v) => o.Add((byte)v);
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); o.AddRange(b.ToArray()); }
        void I32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, (int)v); o.AddRange(b.ToArray()); }
        void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); o.AddRange(b.ToArray()); }
        void F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); o.AddRange(b.ToArray()); }

        // Build start (153): reserved, then the name, at most 24 characters, null-terminated.
        U8(153);
        U32(0);
        foreach (char c in buildName.Length > 24 ? buildName[..24] : buildName) U8(c is >= ' ' and <= '~' ? c : '?');
        U8(0);

        double x = 0, y = 0, z = 0, a = 0, feed = 0, excess = 0;
        bool relativeXyz = false, relativeE = true;
        byte known = 0;
        int lineNo = 0;
        foreach (var raw in gcode.Split('\n'))
        {
            lineNo++;
            string line = raw;
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi];
            int paren = line.IndexOf('(');
            if (paren >= 0) line = line[..paren];
            line = line.Trim();
            if (line.Length == 0) continue;
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double? Arg(char k)
            {
                foreach (var w in words.Skip(1))
                    if (char.ToUpperInvariant(w[0]) == k)
                        return w.Length == 1 ? 0 : double.Parse(w[1..], NumberStyles.Float, CultureInfo.InvariantCulture);
                return null;
            }
            bool Has(char k) => words.Skip(1).Any(w => char.ToUpperInvariant(w[0]) == k);
            byte Axes() => (byte)((Has('X') ? X : 0) | (Has('Y') ? Y : 0) | (Has('Z') ? Z : 0) | (Has('A') ? A : 0) | (Has('B') ? B : 0));
            int Tool() => (int)(Arg('T') ?? 0);
            string code = words[0].ToUpperInvariant();
            switch (code)
            {
                case "G21": break;
                case "G91": relativeXyz = true; break;
                case "M83": relativeE = true; break;
                case "M82": relativeE = false; break;
                case "M73": case "M117": break;
                case "G0": case "G1":
                {
                    if (Arg('F') is { } f) feed = f;
                    double tx = Arg('X') is { } ax ? (relativeXyz ? x + ax : ax) : x;
                    double ty = Arg('Y') is { } ay ? (relativeXyz ? y + ay : ay) : y;
                    double tz = Arg('Z') is { } az ? (relativeXyz ? z + az : az) : z;
                    double de = Arg('E') is { } e ? (relativeE ? e : e - a) : 0;
                    byte moving = (byte)((Has('X') ? X : 0) | (Has('Y') ? Y : 0) | (Has('Z') ? Z : 0) | (Has('E') ? A : 0));
                    if (moving == 0) break;
                    if ((known & (X | Y | Z)) != (X | Y | Z))
                    {
                        // Just homed: the firmware knows where it is, the file doesn't. An absolute point (139) at a
                        // fixed step rate needs every axis given.
                        if ((moving & (X | Y | Z)) != (X | Y | Z))
                            throw new FormatException($"Line {lineNo}: after homing, the first move must give X, Y and Z (\"{raw.Trim()}\").");
                        double mmPerMin = Math.Min(feed > 0 ? feed : machine.XYHomeFeed, machine.XYMaxFeed);
                        a += de;
                        U8(139);
                        I32((long)Math.Round(tx * machine.XSteps));
                        I32((long)Math.Round(ty * machine.YSteps));
                        I32((long)Math.Round(tz * machine.ZSteps));
                        I32((long)Math.Round(-a * machine.ASteps));
                        I32(0);
                        // Microseconds a step on the finest axis at that feedrate.
                        U32((long)Math.Round(60_000_000 / (Math.Max(machine.XSteps, machine.ZSteps) * mmPerMin)));
                        (x, y, z) = (tx, ty, tz);
                        known |= X | Y | Z;
                        break;
                    }
                    double dx = tx - x, dy = ty - y, dz = tz - z;
                    long sx = (long)Math.Round(Math.Abs(dx) * machine.XSteps), sy = (long)Math.Round(Math.Abs(dy) * machine.YSteps);
                    long sz = (long)Math.Round(Math.Abs(dz) * machine.ZSteps), sa = (long)Math.Round(Math.Abs(de) * machine.ASteps);
                    (x, y, z) = (tx, ty, tz);
                    a += de;
                    if (sx + sy + sz + sa == 0) break;
                    double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    // The feedrate, held to each axis's fastest along this move.
                    double rate = feed > 0 ? feed : machine.XYMaxFeed;
                    if (distance > 0)
                    {
                        if (rate * Math.Abs(dx) / distance > machine.XYMaxFeed) rate = machine.XYMaxFeed * distance / Math.Abs(dx);
                        if (rate * Math.Abs(dy) / distance > machine.XYMaxFeed) rate = machine.XYMaxFeed * distance / Math.Abs(dy);
                        if (rate * Math.Abs(dz) / distance > machine.ZMaxFeed) rate = machine.ZMaxFeed * distance / Math.Abs(dz);
                        if (rate * Math.Abs(de) / distance > machine.AMaxFeed) rate = machine.AMaxFeed * distance / Math.Abs(de);
                    }
                    else rate = Math.Min(rate, machine.AMaxFeed);
                    double minutes = distance / rate;
                    if (minutes == 0)
                    {
                        distance = Math.Abs(de);
                        minutes = distance / rate;
                    }
                    // The extruder's steps, the rounding carried over.
                    double aSteps = -de * machine.ASteps + excess;
                    long aWhole = (long)Math.Round(aSteps);
                    excess = aSteps - aWhole;
                    long longest = Math.Max(Math.Max(sx, sy), Math.Max(sz, sa));
                    double ddaRate = 1_000_000.0 / (60_000_000.0 * minutes / longest);
                    U8(155);
                    I32((long)Math.Round(x * machine.XSteps));
                    I32((long)Math.Round(y * machine.YSteps));
                    I32((long)Math.Round(z * machine.ZSteps));
                    I32(aWhole);
                    I32(0);
                    U32((long)Math.Round(ddaRate));
                    U8(A | B);
                    F32((float)distance);
                    U16((int)(rate / 60 * 64));
                    break;
                }
                case "G92":
                {
                    if (Arg('X') is { } gx) x = gx;
                    if (Arg('Y') is { } gy) y = gy;
                    if (Arg('Z') is { } gz) z = gz;
                    if (Arg('E') is { } ge) a = ge;
                    if (Arg('A') is { } ga) a = ga;
                    known |= Axes();
                    if (Has('E')) known |= A;
                    U8(140);
                    I32((long)Math.Round(x * machine.XSteps));
                    I32((long)Math.Round(y * machine.YSteps));
                    I32((long)Math.Round(z * machine.ZSteps));
                    I32((long)Math.Round(a * machine.ASteps));
                    I32(0);
                    break;
                }
                case "G161": case "G162":
                {
                    byte axes = (byte)(Axes() & (X | Y | Z));
                    double mmPerMin = Arg('F') ?? (axes == Z ? machine.ZHomeFeed : machine.XYHomeFeed);
                    if ((axes & (X | Y)) != 0) mmPerMin = Math.Min(mmPerMin, machine.XYHomeFeed);
                    if ((axes & Z) != 0) mmPerMin = Math.Min(mmPerMin, machine.ZHomeFeed);
                    double finest = Math.Max((axes & X) != 0 ? machine.XSteps : 0, Math.Max((axes & Y) != 0 ? machine.YSteps : 0, (axes & Z) != 0 ? machine.ZSteps : 0));
                    double length = Math.Sqrt(((axes & X) != 0 ? 1 : 0) + ((axes & Y) != 0 ? 1 : 0) + ((axes & Z) != 0 ? 1 : 0));
                    U8(code == "G161" ? 131 : 132);
                    U8(axes);
                    U32((long)Math.Round(length / mmPerMin * 60_000_000.0 / finest));
                    U16(machine.HomeTimeout);
                    known = (byte)(known & ~axes);
                    break;
                }
                case "M132":
                    U8(144);
                    U8(Axes() == 0 ? X | Y | Z | A | B : Axes());
                    known = (byte)(known & ~(Axes() == 0 ? X | Y | Z | A | B : Axes()));
                    break;
                case "M104": case "M109":
                    U8(136); U8(Tool()); U8(3); U8(2); U16((int)(Arg('S') ?? 0));
                    if (code == "M109") { U8(135); U8(Tool()); U16(100); U16(NoTimeout); }
                    break;
                case "M140": case "M190":
                    U8(136); U8(Tool()); U8(31); U8(2); U16((int)(Arg('S') ?? 0));
                    if (code == "M190") { U8(141); U8(Tool()); U16(100); U16(NoTimeout); }
                    break;
                case "M106": case "M107":
                    U8(136); U8(0); U8(12); U8(1); U8(code == "M106" && (Arg('S') ?? 255) > 0 ? 1 : 0);
                    break;
                case "M18": case "M84":
                    U8(137); U8(X | Y | Z | A | B);
                    break;
                case "G90": relativeXyz = false; break;
                default:
                    throw new FormatException($"Line {lineNo}: {code} isn't a command Sailfish's .x3g has (\"{raw.Trim()}\"). Use the Flashforge Creator Pro's start and end G-code.");
            }
        }
        // Build end (154).
        U8(154);
        U8(0);
        return o.ToArray();
    }

    /// <summary>Writes the .x3g (in full to a temporary file first, so a failure leaves the old one).</summary>
    public static void Save(string gcode, SailfishMachine machine, string path)
    {
        var bytes = Convert(gcode, machine, Path.GetFileNameWithoutExtension(path));
        string temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
