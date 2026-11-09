using System.Globalization;
using System.Text.RegularExpressions;

namespace AVAModeler.Slicing;

/// <summary>The dialect of G-code a printer's firmware reads. Marlin's is the common one (and what Klipper and RepRap read too).</summary>
public enum GCodeFlavor { Marlin }

/// <summary>
/// A printer: its build volume, nozzle and filament, and the G-code it starts and ends a print with. Sizes are in
/// millimetres, as printers and slicers use them. The slicer works with the bed's front-left corner at (0, 0), X to the
/// right and Y to the back, Z up from the bed; a printer whose firmware puts (0, 0) at the middle of the bed
/// (<see cref="OriginAtCentre"/>: deltas, MakerBot-style machines) gets its moves shifted to that as they're written.
/// Immutable: change one with <c>with</c>, then <see cref="Checked"/> it.
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
    /// <summary>The firmware's (0, 0) is the middle of the bed, not its front-left corner.</summary>
    public bool OriginAtCentre { get; init; }
    /// <summary>A round bed (a delta's), <see cref="BedWidth"/> across; the object must fit inside the circle.</summary>
    public bool RoundBed { get; init; }
    /// <summary>
    /// How far this printer's extruder pulls the filament back (mm), in place of the material's: a Bowden tube needs
    /// several millimetres where a direct drive needs under one. Null keeps the material's.
    /// </summary>
    public double? RetractLength { get; init; }
    /// <summary>
    /// The Sailfish machine (<see cref="SailfishMachine.ById"/>, e.g. "fcp") whose .x3g this printer reads, if it's a
    /// MakerBot-style printer: then the Print window and slice() can save .x3g as well as G-code.
    /// </summary>
    public string? X3gMachine { get; init; }
    /// <summary>How fast the printer speeds up and slows down (mm/s²), for the print's time.</summary>
    public double Acceleration { get; init; } = 1000;
    /// <summary>The speed it takes a 90° corner at (mm/s: Klipper's square corner velocity, much like Marlin's jerk), for the print's time.</summary>
    public double CornerSpeed { get; init; } = 8;
    /// <summary>What to know before printing from this profile (its firmware's quirks), shown under the printer's name.</summary>
    public string Notes { get; init; } = "";

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
        if (RetractLength is { } r) Range(r, 0, 15, "The printer's retraction");
        Range(Acceleration, 50, 100000, "The acceleration", " mm/s²");
        Range(CornerSpeed, 0.5, 100, "The corner speed", " mm/s");
        if (X3gMachine != null && !SailfishMachine.ById.ContainsKey(X3gMachine))
            throw new ArgumentException($"There's no Sailfish machine \"{X3gMachine}\" for .x3g: {string.Join(", ", SailfishMachine.ById.Keys)}.");
        if (RoundBed && Math.Abs(BedWidth - BedDepth) > 1e-9) throw new ArgumentException("A round bed is as deep as it's wide: give its diameter as both.");
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
    /// <summary>A layer quicker than this (seconds) is slowed to take it, so it cools before the next goes on top.</summary>
    public double MinLayerSeconds { get; init; } = 8;
    /// <summary>The slowest that slowing down for cooling may go (mm/s).</summary>
    public double MinSpeed { get; init; } = 10;
    /// <summary>What to know before printing it (an enclosure, a dry spool, a hardened nozzle), shown under its name.</summary>
    public string Notes { get; init; } = "";

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
        PrinterProfile.Range(MinLayerSeconds, 0, 120, "The shortest layer time", " s");
        PrinterProfile.Range(MinSpeed, 1, 100, "The slowest cooling speed", " mm/s");
        return this;
    }
}

/// <summary>The printers and materials the app starts with; the person's own are kept in the preferences.</summary>
public static class Presets
{
    /// <summary>The generic sizes, then the printers on the market (<see cref="PrinterPresets"/>), maker by maker.</summary>
    public static readonly IReadOnlyList<PrinterProfile> Printers =
    [
        new PrinterProfile(),
        new PrinterProfile { Name = "Generic 250 × 210", BedWidth = 250, BedDepth = 210, MaxHeight = 210 },
        new PrinterProfile { Name = "Generic 300 × 300", BedWidth = 300, BedDepth = 300, MaxHeight = 400 },
        new PrinterProfile { Name = "Generic 180 × 180", BedWidth = 180, BedDepth = 180, MaxHeight = 180 },
        .. PrinterPresets.All,
    ];

    public static readonly IReadOnlyList<MaterialProfile> Materials =
    [
        new MaterialProfile(),
        new MaterialProfile { Name = "PETG", NozzleTemp = 235, FirstLayerNozzleTemp = 240, BedTemp = 80, RetractLength = 1.0, RetractSpeed = 30, FanPercent = 50, FanFromLayer = 3, Density = 1.27 },
        new MaterialProfile { Name = "ABS", NozzleTemp = 245, FirstLayerNozzleTemp = 250, BedTemp = 100, FanPercent = 0, Density = 1.04 },
        new MaterialProfile { Name = "TPU", NozzleTemp = 225, FirstLayerNozzleTemp = 225, BedTemp = 50, RetractLength = 0.4, RetractSpeed = 20, FanPercent = 60, Density = 1.21,
            Notes = "Flexible: print slowly (20–30 mm/s), and a direct drive feeds it best; a Bowden tube may let it buckle." },
        new MaterialProfile { Name = "PLA+", NozzleTemp = 215, FirstLayerNozzleTemp = 220, BedTemp = 60, Density = 1.23 },
        new MaterialProfile { Name = "Silk PLA", NozzleTemp = 215, FirstLayerNozzleTemp = 220, BedTemp = 60, RetractLength = 1.0, Density = 1.24,
            Notes = "The shine is best a little slower and hotter; it's weaker between layers than plain PLA." },
        new MaterialProfile { Name = "Wood-fill PLA", NozzleTemp = 200, FirstLayerNozzleTemp = 205, BedTemp = 60, RetractLength = 1.0, Density = 1.15,
            Notes = "Its wood fibres can clog a 0.4 mm nozzle: 0.5 mm or more is safer. Hotter prints come out darker." },
        new MaterialProfile { Name = "ASA", NozzleTemp = 250, FirstLayerNozzleTemp = 255, BedTemp = 100, FanPercent = 30, FanFromLayer = 3, Density = 1.07,
            Notes = "Like ABS but weatherproof: an enclosure keeps it from warping and cracking; it gives off fumes, so ventilate." },
        new MaterialProfile { Name = "Nylon (PA)", NozzleTemp = 255, FirstLayerNozzleTemp = 260, BedTemp = 70, RetractLength = 1.2, FanPercent = 0, Density = 1.14,
            Notes = "Soaks up water from the air: dry the spool first and keep it dry. Sticks best to garolite or a glue stick." },
        new MaterialProfile { Name = "Polycarbonate (PC)", NozzleTemp = 270, FirstLayerNozzleTemp = 275, BedTemp = 110, FanPercent = 0, Density = 1.20,
            Notes = "Needs an all-metal hotend for 270 °C and an enclosure; dry it first. Very strong and heat-resistant." },
        new MaterialProfile { Name = "CF-PETG", NozzleTemp = 245, FirstLayerNozzleTemp = 250, BedTemp = 80, RetractLength = 1.0, FanPercent = 40, FanFromLayer = 3, Density = 1.30,
            Notes = "Carbon fibre wears a brass nozzle out quickly: use a hardened steel one, 0.4 mm or more." },
    ];
}

/// <summary>The braced names the start and end G-code may use, and filling them in.</summary>
public static class Placeholders
{
    public static readonly IReadOnlyList<string> Names =
        ["nozzle_temp", "first_layer_nozzle_temp", "bed_temp", "bed_width", "bed_depth", "max_height", "nozzle_diameter", "filament_diameter", "layer_height",
         "bed_width_half", "bed_depth_half", "bed_width_third", "prime_end"];

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
