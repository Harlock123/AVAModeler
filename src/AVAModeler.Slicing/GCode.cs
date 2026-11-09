using System.Globalization;
using System.Text;
using AVAModeler.Core;

namespace AVAModeler.Slicing;

/// <summary>What a print will take, worked out while its G-code is written.</summary>
public sealed record PrintStats
{
    public int Layers { get; init; }
    /// <summary>Seconds, from the moves' lengths and speeds alone: acceleration and heating aren't counted, so a real print takes somewhat longer.</summary>
    public double Seconds { get; init; }
    /// <summary>Filament pushed through the nozzle, in mm of filament.</summary>
    public double FilamentMm { get; init; }
    public double Grams { get; init; }
    /// <summary>Moves with the nozzle printing, and moves between.</summary>
    public int ExtrusionMoves { get; init; }
    public int TravelMoves { get; init; }
    public int Retractions { get; init; }

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
        PathKind.Skirt or PathKind.Brim => "SKIRT",
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
    };

    /// <summary>The G-code for a plan, and what the print will take.</summary>
    public static (string Text, PrintStats Stats) Write(PrintPlan plan, MaterialProfile material)
    {
        material.Checked();
        var printer = plan.Printer;
        var s = plan.Settings;
        var body = new StringBuilder();
        var w = new Writer(body);
        double seconds = 0, filament = 0;
        int extrusions = 0, travels = 0, retractions = 0;
        var pos = new Vec2(double.NaN, double.NaN);
        double z = 0;
        bool retracted = false;
        int fan = (int)Math.Round(255 * material.FanPercent / 100.0);

        void Retract()
        {
            if (retracted || material.RetractLength <= 0) return;
            w.Line($"G1 E{F(-material.RetractLength, 5)} F{F(material.RetractSpeed * 60, 0)}");
            seconds += material.RetractLength / material.RetractSpeed;
            retractions++;
            retracted = true;
        }

        void Travel(Vec2 to)
        {
            double d = double.IsNaN(pos.X) ? double.PositiveInfinity : (to - pos).Length;
            if (d < 1e-6) return;
            bool hop = false;
            if (d >= s.RetractMinTravel)
            {
                Retract();
                hop = retracted && s.ZHop > 0;
            }
            if (hop) w.Line($"G1 Z{F(z + s.ZHop, 3)} F600");
            w.Line($"G0 X{F(to.X, 3)} Y{F(to.Y, 3)} F{F(s.TravelSpeed * 60, 0)}");
            if (double.IsFinite(d)) seconds += d / s.TravelSpeed;
            travels++;
            if (hop) w.Line($"G1 Z{F(z, 3)} F600");
            pos = to;
        }

        void Unretract()
        {
            if (!retracted) return;
            w.Line($"G1 E{F(material.RetractLength, 5)} F{F(material.RetractSpeed * 60, 0)}");
            seconds += material.RetractLength / material.RetractSpeed;
            retracted = false;
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
            if (number == 2 && material.NozzleTemp != material.FirstLayerNozzleTemp) w.Line($"M104 S{material.NozzleTemp}");
            if (number == material.FanFromLayer && fan > 0) w.Line($"M106 S{fan}");
            double perMm = FilamentPerMm(s.LineWidth, layer.Layer.Height, printer.FilamentDiameter);
            type = null;
            foreach (var path in layer.Paths)
            {
                if (path.Kind != type)
                {
                    w.Line($";TYPE:{TypeName(path.Kind)}");
                    type = path.Kind;
                }
                double speed = layer.Layer.Index == 0 ? s.FirstLayerSpeed : path.Kind switch
                {
                    PathKind.OuterWall => s.OuterWallSpeed,
                    PathKind.Infill => s.InfillSpeed,
                    _ => s.PrintSpeed,
                };
                Travel(path.Points[0]);
                Unretract();
                string feed = $" F{F(speed * 60, 0)}";
                IEnumerable<Vec2> points = path.Closed ? path.Points.Skip(1).Append(path.Points[0]) : path.Points.Skip(1);
                foreach (var p in points)
                {
                    double d = (p - pos).Length;
                    if (d < 1e-4) continue;
                    double e = d * perMm;
                    w.Line($"G1 X{F(p.X, 3)} Y{F(p.Y, 3)} E{F(e, 5)}{feed}");
                    feed = "";
                    filament += e;
                    seconds += d / speed;
                    extrusions++;
                    pos = p;
                }
            }
        }

        var stats = new PrintStats
        {
            Layers = plan.Layers.Count,
            Seconds = seconds,
            FilamentMm = filament,
            Grams = filament * Math.PI * printer.FilamentDiameter * printer.FilamentDiameter / 4 / 1000 * material.Density,
            ExtrusionMoves = extrusions,
            TravelMoves = travels,
            Retractions = retractions,
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
            h.Line($";MINX:{F(bounds.Min.X, 3)}");
            h.Line($";MINY:{F(bounds.Min.Y, 3)}");
            h.Line($";MINZ:{F(bounds.Min.Z, 3)}");
            h.Line($";MAXX:{F(bounds.Max.X, 3)}");
            h.Line($";MAXY:{F(bounds.Max.Y, 3)}");
            h.Line($";MAXZ:{F(bounds.Max.Z, 3)}");
        }
        h.Line($";Generated with AVA Modeler for {Comment(printer.Name)} and {Comment(material.Name)}");
        h.Line($";Nozzle {F(printer.NozzleDiameter, 2)} mm, line width {F(s.LineWidth, 2)} mm, {s.Walls} walls, {F(s.InfillPercent, 0)}% {s.Infill.ToString().ToLowerInvariant()} infill{(s.Supports ? ", supports" : "")}");
        h.Line("G21 ; millimetres");
        h.Line("G90 ; absolute positions");
        h.Line("M83 ; relative extrusion");
        h.Line("M107 ; fan off");
        h.Line($"M140 S{material.BedTemp} ; heat the bed");
        h.Line($"M104 S{material.FirstLayerNozzleTemp} ; heat the nozzle");
        h.Line($"M190 S{material.BedTemp} ; wait for the bed");
        h.Line($"M109 S{material.FirstLayerNozzleTemp} ; wait for the nozzle");
        foreach (var line in Lines(Placeholders.Fill(printer.StartGCode, values))) h.Line(line);
        h.Line("G90");
        h.Line("M83 ; relative extrusion, whatever the start G-code chose");
        all.Append(body);
        h.Line(";TYPE:END");
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

    private static string Comment(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Replace(';', ',');

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
