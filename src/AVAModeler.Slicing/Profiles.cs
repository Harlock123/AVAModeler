using System.Globalization;
using System.Text.RegularExpressions;

namespace AVAModeler.Slicing;

/// <summary>The dialect of G-code a printer's firmware reads. Marlin's is the common one (and what Klipper and RepRap read too).</summary>
public enum GCodeFlavor { Marlin }

/// <summary>
/// A printer: its build volume, nozzle and filament, and the G-code it starts and ends a print with. Sizes are in
/// millimetres, as printers and slicers use them. The bed's origin (0, 0) is its front-left corner, X to the right and
/// Y to the back, Z up from the bed. Immutable: change one with <c>with</c>, then <see cref="Checked"/> it.
/// </summary>
public sealed record PrinterProfile
{
    public string Name { get; init; } = "Generic 220 × 220";
    public double BedWidth { get; init; } = 220;
    public double BedDepth { get; init; } = 220;
    public double MaxHeight { get; init; } = 250;
    public double NozzleDiameter { get; init; } = 0.4;
    public double FilamentDiameter { get; init; } = 1.75;
    public GCodeFlavor Flavor { get; init; } = GCodeFlavor.Marlin;

    /// <summary>
    /// The G-code before the print, after the temperatures are set. Placeholders in braces are filled in (see
    /// <see cref="Placeholders.Names"/>): <c>{nozzle_temp}</c>, <c>{bed_temp}</c> and so on.
    /// </summary>
    public string StartGCode { get; init; } = """
        G28 ; home all axes
        G92 E0
        G1 Z5 F3000 ; lift the nozzle
        G1 X5 Y20 Z0.3 F5000 ; to the front-left edge
        G1 X5 Y200 E15 F1500 ; prime a line
        G92 E0
        """;

    /// <summary>The G-code after the print, placeholders filled in as for <see cref="StartGCode"/>.</summary>
    public string EndGCode { get; init; } = """
        G91 ; relative moves
        G1 E-2 F2700 ; pull the filament back
        G1 Z10 F3000 ; lift off the print
        G90
        G1 X0 Y{bed_depth} F3000 ; present the print
        M106 S0 ; fan off
        M104 S0 ; nozzle off
        M140 S0 ; bed off
        M84 ; motors off
        """;

    /// <summary>The profile, or an <see cref="ArgumentException"/> saying what's wrong with it.</summary>
    public PrinterProfile Checked()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("A printer needs a name.");
        Range(BedWidth, 10, 2000, "The bed width");
        Range(BedDepth, 10, 2000, "The bed depth");
        Range(MaxHeight, 10, 2000, "The build height");
        Range(NozzleDiameter, 0.1, 2, "The nozzle diameter");
        Range(FilamentDiameter, 1, 3.5, "The filament diameter");
        Placeholders.Check(StartGCode, "The start G-code");
        Placeholders.Check(EndGCode, "The end G-code");
        return this;
    }

    internal static void Range(double v, double min, double max, string what, string unit = " mm")
    {
        if (!double.IsFinite(v) || v < min || v > max)
            throw new ArgumentException($"{what} must be from {Num(min)} to {Num(max)}{unit} (it's {Num(v)}).");
    }

    internal static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// A filament: its temperatures, how far and fast to pull it back on long travel moves, the part-cooling fan, and its
/// density for weighing the print. Temperatures in °C, lengths in mm, speeds in mm/s.
/// </summary>
public sealed record MaterialProfile
{
    public string Name { get; init; } = "PLA";
    public int NozzleTemp { get; init; } = 205;
    public int FirstLayerNozzleTemp { get; init; } = 210;
    public int BedTemp { get; init; } = 60;
    public double RetractLength { get; init; } = 0.8;
    public double RetractSpeed { get; init; } = 35;
    /// <summary>The part-cooling fan, 0 to 100%.</summary>
    public int FanPercent { get; init; } = 100;
    /// <summary>The first layer (counted from 1) the fan runs on: off for the first layer, for adhesion.</summary>
    public int FanFromLayer { get; init; } = 2;
    /// <summary>Grams per cubic centimetre.</summary>
    public double Density { get; init; } = 1.24;

    public MaterialProfile Checked()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("A material needs a name.");
        PrinterProfile.Range(NozzleTemp, 150, 350, "The nozzle temperature", " °C");
        PrinterProfile.Range(FirstLayerNozzleTemp, 150, 350, "The first layer's nozzle temperature", " °C");
        PrinterProfile.Range(BedTemp, 0, 150, "The bed temperature", " °C");
        PrinterProfile.Range(RetractLength, 0, 10, "The retraction length");
        PrinterProfile.Range(RetractSpeed, 1, 150, "The retraction speed", " mm/s");
        PrinterProfile.Range(FanPercent, 0, 100, "The fan speed", "%");
        PrinterProfile.Range(FanFromLayer, 1, 1000, "The fan's first layer", "");
        PrinterProfile.Range(Density, 0.5, 10, "The density", " g/cm³");
        return this;
    }
}

/// <summary>The printers and materials the app starts with; the person's own are kept in the preferences.</summary>
public static class Presets
{
    public static readonly IReadOnlyList<PrinterProfile> Printers =
    [
        new PrinterProfile(),
        new PrinterProfile { Name = "Generic 250 × 210", BedWidth = 250, BedDepth = 210, MaxHeight = 210 },
        new PrinterProfile { Name = "Generic 300 × 300", BedWidth = 300, BedDepth = 300, MaxHeight = 400 },
        new PrinterProfile { Name = "Generic 180 × 180", BedWidth = 180, BedDepth = 180, MaxHeight = 180 },
    ];

    public static readonly IReadOnlyList<MaterialProfile> Materials =
    [
        new MaterialProfile(),
        new MaterialProfile { Name = "PETG", NozzleTemp = 235, FirstLayerNozzleTemp = 240, BedTemp = 80, RetractLength = 1.0, RetractSpeed = 30, FanPercent = 50, FanFromLayer = 3, Density = 1.27 },
        new MaterialProfile { Name = "ABS", NozzleTemp = 245, FirstLayerNozzleTemp = 250, BedTemp = 100, FanPercent = 0, Density = 1.04 },
        new MaterialProfile { Name = "TPU", NozzleTemp = 225, FirstLayerNozzleTemp = 225, BedTemp = 50, RetractLength = 0.4, RetractSpeed = 20, FanPercent = 60, Density = 1.21 },
    ];
}

/// <summary>The braced names the start and end G-code may use, and filling them in.</summary>
public static class Placeholders
{
    public static readonly IReadOnlyList<string> Names =
        ["nozzle_temp", "first_layer_nozzle_temp", "bed_temp", "bed_width", "bed_depth", "max_height", "nozzle_diameter", "filament_diameter", "layer_height"];

    private static readonly Regex Brace = new(@"\{([A-Za-z_]+)\}", RegexOptions.CultureInvariant);

    /// <summary>Throws an <see cref="ArgumentException"/> naming a placeholder that isn't one of <see cref="Names"/>.</summary>
    public static void Check(string gcode, string what)
    {
        foreach (Match m in Brace.Matches(gcode ?? ""))
            if (!Names.Contains(m.Groups[1].Value))
                throw new ArgumentException($"{what} uses {{{m.Groups[1].Value}}}, which isn't a placeholder: use {string.Join(", ", Names.Select(n => "{" + n + "}"))}.");
    }

    /// <summary>The G-code with its placeholders replaced by the values.</summary>
    public static string Fill(string gcode, IReadOnlyDictionary<string, double> values) =>
        Brace.Replace(gcode ?? "", m => values.TryGetValue(m.Groups[1].Value, out double v) ? PrinterProfile.Num(v) : m.Value);
}
