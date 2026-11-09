using System.Globalization;
using System.Text;
using AVAModeler.Core;

namespace AVAModeler.Slicing;

/// <summary>What a print will take, worked out while its G-code is written.</summary>
public sealed record PrintStats
{
    public int Layers { get; init; }
    /// <summary>Seconds, with each move speeding up and slowing down at the printer's acceleration and corners taken at its corner speed; heating isn't counted.</summary>
    public double Seconds { get; init; }
    /// <summary>Filament pushed through the nozzle, in mm of filament.</summary>
    public double FilamentMm { get; init; }
    public double Grams { get; init; }
    /// <summary>Moves with the nozzle printing, and moves between.</summary>
    public int ExtrusionMoves { get; init; }
    public int TravelMoves { get; init; }
    public int Retractions { get; init; }
    /// <summary>Of <see cref="FilamentMm"/>, what the support's extruder used (0 unless supports are in a second material).</summary>
    public double SupportFilamentMm { get; init; }
    /// <summary>Travel moves kept inside the object (no retraction), and layers slowed down to cool.</summary>
    public int CombedTravels { get; init; }
    public int SlowedLayers { get; init; }

    /// <summary>The time as hours and minutes ("1 h 05 min", "12 min").</summary>
    public string Time
    {
        get
        {
            int minutes = (int)Math.Ceiling(Seconds / 60);
            return minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min" : $"{minutes} min";
        }
    }

    /// <summary>One line for a status bar: "100 layers · 23 min · 1.92 m of filament · 5.7 g".</summary>
    public string Summary => $"{Layers} layers · {Time} · {(FilamentMm / 1000).ToString("0.00", CultureInfo.InvariantCulture)} m of filament · {Grams.ToString("0.0", CultureInfo.InvariantCulture)} g";
}

/// <summary>
/// Writes a <see cref="PrintPlan"/> as G-code for Marlin-style firmware (Marlin, Klipper, RepRapFirmware read it):
/// millimetres (<c>G21</c>), absolute X, Y and Z (<c>G90</c>) and relative extrusion (<c>M83</c>), so each move's E is
/// the filament it uses. The header has Cura-style comments (<c>;FLAVOR</c>, <c>;TIME</c>, <c>;LAYER_COUNT</c>,
/// <c>;MINX</c>…), and each layer and line type is marked (<c>;LAYER:n</c>, <c>;TYPE:WALL-OUTER</c>…), so G-code
/// previewers show it. A line of width w and height h uses w·h less the corners a squashed round line doesn't fill:
/// (w − h)·h + π(h/2)² mm² of plastic per mm, which is that over the filament's cross-section in filament.
/// </summary>
public static class GCode
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string TypeName(PathKind kind) => kind switch
    {
        PathKind.OuterWall => "WALL-OUTER",
        PathKind.InnerWall => "WALL-INNER",
        PathKind.Skin => "SKIN",
        PathKind.Infill => "FILL",
        PathKind.Support => "SUPPORT",
        PathKind.SupportInterface => "SUPPORT-INTERFACE",
        PathKind.Skirt or PathKind.Brim => "SKIRT",
        PathKind.GapFill => "FILL",
        PathKind.Bridge => "SKIN",
        PathKind.Ironing => "IRONING",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The filament (mm) a line of this width and height uses per mm of its length.</summary>
    public static double FilamentPerMm(double width, double height, double filamentDiameter)
    {
        double area = (width - height) * height + Math.PI * height * height / 4;
        return area / (Math.PI * filamentDiameter * filamentDiameter / 4);
    }

    /// <summary>The placeholders' values for the start and end G-code.</summary>
    public static Dictionary<string, double> Values(PrinterProfile printer, MaterialProfile material, SliceSettings settings) => new()
    {
        ["nozzle_temp"] = material.NozzleTemp,
        ["first_layer_nozzle_temp"] = material.FirstLayerNozzleTemp,
        ["bed_temp"] = material.BedTemp,
        ["bed_width"] = printer.BedWidth,
        ["bed_depth"] = printer.BedDepth,
        ["max_height"] = printer.MaxHeight,
        ["nozzle_diameter"] = printer.NozzleDiameter,
        ["filament_diameter"] = printer.FilamentDiameter,
        ["layer_height"] = settings.LayerHeight,
        ["bed_width_half"] = printer.BedWidth / 2,
        ["bed_depth_half"] = printer.BedDepth / 2,
        ["bed_width_third"] = Math.Round(printer.BedWidth / 3, 1),
        ["prime_end"] = Math.Min(200, printer.BedDepth - 20),
    };

    /// <summary>The G-code for a plan, and what the print will take.</summary>
    public static (string Text, PrintStats Stats) Write(PrintPlan plan, MaterialProfile material, MaterialProfile? supportMaterial = null)
    {
        material.Checked();
        supportMaterial?.Checked();
        var printer = plan.Printer;
        var s = plan.Settings;
        // Which extruder prints what: the part on one, supports on the same or (a second material) the other.
        int modelTool = s.ModelExtruder, supportTool = s.SupportExtruder < 0 ? s.ModelExtruder : s.SupportExtruder;
        bool dual = supportTool != modelTool && plan.Layers.Any(l => l.Paths.Any(p => p.Kind is PathKind.Support or PathKind.SupportInterface));
        if (!dual) supportTool = modelTool;
        var mats = new MaterialProfile[Math.Max(2, printer.Extruders)];
        mats[modelTool] = material;
        mats[supportTool] = dual ? supportMaterial ?? material : material;
        int[] used = dual ? [modelTool, supportTool] : [modelTool];
        // Temperatures name their extruder whenever more than the first is in use.
        bool tagged = dual || modelTool != 0;
        string T(int t) => tagged ? $" T{t}" : "";
        int tool = modelTool;
        var retractedBy = new bool[mats.Length];
        var filamentBy = new double[mats.Length];
        var body = new StringBuilder();
        var w = new Writer(body);
        double seconds = 0, filament = 0;
        int extrusions = 0, travels = 0, retractions = 0, combed = 0, slowed = 0;
        var pos = new Vec2(double.NaN, double.NaN);
        double z = 0;
        bool retracted = false;
        int fan = (int)Math.Round(255 * material.FanPercent / 100.0);
        // The firmware's origin: the bed's front-left corner, or its middle (deltas, Sailfish machines).
        double ox = printer.OriginAtCentre ? printer.BedWidth / 2 : 0, oy = printer.OriginAtCentre ? printer.BedDepth / 2 : 0;
        // How the printer speeds up and takes corners, for the time it'll take.
        double accel = printer.Acceleration, corner = printer.CornerSpeed;
        int fanNow = 0;

        double RetractLength() => printer.RetractLength ?? mats[tool].RetractLength;

        void Retract()
        {
            double retractLength = RetractLength();
            if (retracted || retractLength <= 0) return;
            w.Line($"G1 E{F(-retractLength, 5)} F{F(mats[tool].RetractSpeed * 60, 0)}");
            seconds += retractLength / mats[tool].RetractSpeed;
            retractions++;
            retracted = true;
        }

        void Travel(Vec2 to, IReadOnlyList<Island> comb)
        {
            double d = double.IsNaN(pos.X) ? double.PositiveInfinity : (to - pos).Length;
            if (d < 1e-6) return;
            // Inside the object: round what's in the way, without retracting.
            if (s.Combing && d >= s.RetractMinTravel && double.IsFinite(d) && Combing.Route(comb, pos, to, 1.5 * s.LineWidth) is { } route)
            {
                seconds += MotionTime.Path([pos, .. route], s.TravelSpeed, accel, corner);
                foreach (var q in route)
                {
                    w.Line($"G0 X{F(q.X - ox, 3)} Y{F(q.Y - oy, 3)} F{F(s.TravelSpeed * 60, 0)}");
                    pos = q;
                }
                travels++;
                combed++;
                pos = to;
                return;
            }
            bool hop = false;
            // The first move of all comes straight from the start G-code's prime line: nothing to pull back from.
            if (d >= s.RetractMinTravel && !double.IsNaN(pos.X))
            {
                Retract();
                hop = retracted && s.ZHop > 0;
            }
            if (hop) w.Line($"G1 Z{F(z + s.ZHop, 3)} F600");
            w.Line($"G0 X{F(to.X - ox, 3)} Y{F(to.Y - oy, 3)} F{F(s.TravelSpeed * 60, 0)}");
            if (double.IsFinite(d)) seconds += MotionTime.Line(d, s.TravelSpeed, accel);
            travels++;
            if (hop) w.Line($"G1 Z{F(z, 3)} F600");
            pos = to;
        }

        void Unretract()
        {
            if (!retracted) return;
            double retractLength = RetractLength();
            w.Line($"G1 E{F(retractLength, 5)} F{F(mats[tool].RetractSpeed * 60, 0)}");
            seconds += retractLength / mats[tool].RetractSpeed;
            retracted = false;
        }

        // To the other extruder: this one's filament pulled back first, the other's left as it was.
        void Switch(int to)
        {
            if (to == tool) return;
            Retract();
            retractedBy[tool] = retracted;
            w.Line($"T{to}");
            tool = to;
            retracted = retractedBy[tool];
        }

        PathKind? type = null;
        foreach (var layer in plan.Layers)
        {
            int number = layer.Layer.Index + 1;
            z = layer.Layer.Top;
            w.Line($";LAYER:{layer.Layer.Index}");
            // Pull the filament back before rising to the next layer, so it doesn't string up.
            if (!double.IsNaN(pos.X)) Retract();
            w.Line($"G0 Z{F(z, 3)} F600");
            if (layer.Layer.Index > 0) seconds += layer.Layer.Height / 10;
            if (number == 2)
                foreach (int t in used)
                    if (mats[t].NozzleTemp != mats[t].FirstLayerNozzleTemp) w.Line($"M104{T(t)} S{mats[t].NozzleTemp}");
            if (number == material.FanFromLayer && fan > 0)
            {
                w.Line($"M106 S{fan}");
                fanNow = fan;
            }
            double Speed(Extrusion path) => layer.Layer.Index == 0 ? s.FirstLayerSpeed : path.Kind switch
            {
                PathKind.OuterWall => s.OuterWallSpeed,
                PathKind.Infill => s.InfillSpeed,
                PathKind.Bridge => s.BridgeSpeed,
                PathKind.GapFill => s.GapFillSpeed,
                PathKind.Ironing => s.IroningSpeed,
                _ => s.PrintSpeed,
            };
            // Cooling: a layer quicker than the material's shortest layer time is slowed to take it (down to its slowest speed).
            double PathSeconds(Extrusion p, double speed) => MotionTime.Path(p.Closed ? [.. p.Points, p.Points[0]] : p.Points, speed, accel, corner);
            double layerSeconds = layer.Paths.Sum(p => PathSeconds(p, Speed(p)));
            double slow = material.MinLayerSeconds > 0 && layerSeconds > 0 && layerSeconds < material.MinLayerSeconds ? layerSeconds / material.MinLayerSeconds : 1;
            if (slow < 1)
            {
                w.Line($";Slowed to {F(slow * 100, 0)}% to cool: the layer would take {F(layerSeconds, 1)} s");
                slowed++;
            }
            type = null;
            foreach (var path in layer.Paths)
            {
                Switch(path.Kind is PathKind.Support or PathKind.SupportInterface ? supportTool : modelTool);
                if (path.Kind != type)
                {
                    // Bridges with the fan full, to set them hard over the air; back as it was after.
                    if (path.Kind == PathKind.Bridge && fanNow < 255) w.Line("M106 S255");
                    if (type == PathKind.Bridge && fanNow < 255) w.Line($"M106 S{fanNow}");
                    w.Line($";TYPE:{TypeName(path.Kind)}");
                    type = path.Kind;
                }
                double normal = Speed(path);
                double speed = slow < 1 ? Math.Max(normal * slow, Math.Min(normal, material.MinSpeed)) : normal;
                double perMm = FilamentPerMm(path.Width > 0 ? path.Width : s.LineWidth, layer.Layer.Height, printer.FilamentDiameter) * path.Flow;
                Travel(path.Points[0], layer.Comb);
                Unretract();
                seconds += PathSeconds(path, speed);
                string feed = $" F{F(speed * 60, 0)}";
                IEnumerable<Vec2> points = path.Closed ? path.Points.Skip(1).Append(path.Points[0]) : path.Points.Skip(1);
                foreach (var p in points)
                {
                    double d = (p - pos).Length;
                    if (d < 1e-4) continue;
                    double e = d * perMm;
                    w.Line($"G1 X{F(p.X - ox, 3)} Y{F(p.Y - oy, 3)} E{F(e, 5)}{feed}");
                    feed = "";
                    filament += e;
                    filamentBy[tool] += e;
                    extrusions++;
                    pos = p;
                }
            }
            if (type == PathKind.Bridge && fanNow < 255) w.Line($"M106 S{fanNow}");
        }

        var stats = new PrintStats
        {
            Layers = plan.Layers.Count,
            Seconds = seconds,
            FilamentMm = filament,
            Grams = used.Sum(t => filamentBy[t] * mats[t].Density) * Math.PI * printer.FilamentDiameter * printer.FilamentDiameter / 4 / 1000,
            SupportFilamentMm = dual ? filamentBy[supportTool] : 0,
            ExtrusionMoves = extrusions,
            TravelMoves = travels,
            Retractions = retractions,
            CombedTravels = combed,
            SlowedLayers = slowed,
        };

        var values = Values(printer, material, s);
        var all = new StringBuilder();
        var h = new Writer(all);
        var bounds = plan.Sliced.Model.Bounds;
        h.Line(";FLAVOR:Marlin");
        h.Line($";TIME:{(int)Math.Ceiling(seconds)}");
        h.Line($";Filament used: {F(filament / 1000, 4)}m");
        h.Line($";Filament weight: {F(stats.Grams, 2)}g");
        h.Line($";Layer height: {F(s.LayerHeight, 3)}");
        h.Line($";LAYER_COUNT:{plan.Layers.Count}");
        if (!bounds.IsEmpty)
        {
            h.Line($";MINX:{F(bounds.Min.X - ox, 3)}");
            h.Line($";MINY:{F(bounds.Min.Y - oy, 3)}");
            h.Line($";MINZ:{F(bounds.Min.Z, 3)}");
            h.Line($";MAXX:{F(bounds.Max.X - ox, 3)}");
            h.Line($";MAXY:{F(bounds.Max.Y - oy, 3)}");
            h.Line($";MAXZ:{F(bounds.Max.Z, 3)}");
        }
        h.Line($";Generated with AVA Modeler for {Comment(printer.Name)} and {Comment(material.Name)}");
        h.Line($";Nozzle {F(printer.NozzleDiameter, 2)} mm, line width {F(s.LineWidth, 2)} mm, {s.Walls} walls, {F(s.InfillPercent, 0)}% {s.Infill.ToString().ToLowerInvariant()} infill{(s.Supports ? ", supports" : "")}");
        h.Line("G21 ; millimetres");
        h.Line("G90 ; absolute positions");
        h.Line("M83 ; relative extrusion");
        h.Line("M107 ; fan off");
        h.Line($"M140 S{material.BedTemp} ; heat the bed");
        foreach (int t in used) h.Line($"M104{T(t)} S{mats[t].FirstLayerNozzleTemp} ; heat the nozzle{(tagged ? $" (extruder {t + 1})" : "")}");
        h.Line($"M190 S{material.BedTemp} ; wait for the bed");
        foreach (int t in used) h.Line($"M109{T(t)} S{mats[t].FirstLayerNozzleTemp} ; wait for the nozzle{(tagged ? $" (extruder {t + 1})" : "")}");
        foreach (var line in Lines(Placeholders.Fill(printer.StartGCode, values))) h.Line(line);
        h.Line("G90");
        h.Line("M83 ; relative extrusion, whatever the start G-code chose");
        if (tagged) h.Line($"T{modelTool} ; the part's extruder");
        all.Append(body);
        h.Line(";TYPE:END");
        // The end G-code turns off the extruder in use; the other one too.
        foreach (int t in used)
            if (t != tool) h.Line($"M104 T{t} S0 ; extruder {t + 1} off");
        foreach (var line in Lines(Placeholders.Fill(printer.EndGCode, values))) h.Line(line);
        h.Line(";End of print");
        return (all.ToString(), stats);
    }

    /// <summary>Writes the plan's G-code to a file (in full to a temporary file first, so a failure leaves the old one).</summary>
    public static PrintStats Save(PrintPlan plan, MaterialProfile material, string path)
    {
        var (text, stats) = Write(plan, material);
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return stats;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0);

    /// <summary>Text for a comment: one line, plain ASCII (some firmware and SD-card readers trip on anything else).</summary>
    private static string Comment(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text.Replace("×", "x"))
            sb.Append(c is '\n' or '\r' ? ' ' : c == ';' ? ',' : c is >= ' ' and <= '~' ? c : '?');
        return sb.ToString();
    }

    /// <summary>A number to so many decimal places, trailing zeros dropped, never "-0".</summary>
    public static string F(double v, int places)
    {
        double r = Math.Round(v, places);
        if (r == 0) r = 0;
        return r.ToString("0." + new string('#', Math.Max(places, 1)), Inv);
    }

    private sealed class Writer(StringBuilder sb)
    {
        public void Line(string line) => sb.Append(line).Append('\n');
    }
}
