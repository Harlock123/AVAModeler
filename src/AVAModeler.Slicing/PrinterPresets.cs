namespace AVAModeler.Slicing;

/// <summary>
/// Printers on the market, by maker: their build volumes as the makers give them, the nozzle and filament they ship
/// with, whether their extruder is a Bowden or a direct drive (which sets the retraction), and start and end G-code
/// for their kind of firmware (Marlin, Prusa's, Klipper, Sailfish). The sizes are the makers' published figures:
/// check your own printer's manual, and change a copy (Save as my printer) to suit it. These names are the printers',
/// used to say which machine a profile is for; AVA Modeler isn't made or endorsed by any of their makers.
/// </summary>
public static class PrinterPresets
{
    private const double Bowden = 5, DirectDrive = 0.8;

    /// <summary>Marlin with no bed probe: home, prime a line down the left edge.</summary>
    private const string MarlinStart = """
        G28 ; home all axes
        G92 E0
        G1 Z5 F3000 ; lift the nozzle
        G1 X5 Y20 Z0.3 F5000 ; to the front-left edge
        G1 X5 Y{prime_end} E15 F1500 ; prime a line
        G92 E0
        """;

    /// <summary>Marlin with a bed probe: home, probe the bed (G29), prime.</summary>
    private const string MarlinProbeStart = """
        G28 ; home all axes
        G29 ; probe the bed
        G92 E0
        G1 Z5 F3000 ; lift the nozzle
        G1 X5 Y20 Z0.3 F5000 ; to the front-left edge
        G1 X5 Y{prime_end} E15 F1500 ; prime a line
        G92 E0
        """;

    private const string MarlinEnd = """
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

    /// <summary>Prusa's MK3 and MINI firmware: home, mesh bed levelling (G80), the purge line in front of the bed.</summary>
    private const string PrusaMeshStart = """
        G28 W ; home all axes without mesh levelling
        G80 ; mesh bed levelling
        G1 Y-3.0 Z0.2 F1000 ; to the purge line, in front of the bed
        G92 E0
        G1 X60 E9 F1000 ; purge
        G1 X100 E12.5 F1000
        G92 E0
        """;

    /// <summary>Prusa's MK4, XL and CORE One firmware: home, probe (G29), the purge line in front of the bed.</summary>
    private const string PrusaProbeStart = """
        G28 ; home all axes
        G29 ; probe the bed
        G1 Y-4 Z0.2 F3000 ; to the purge line, in front of the bed
        G92 E0
        G1 X60 E9 F1000 ; purge
        G1 X100 E12.5 F1000
        G92 E0
        """;

    private const string PrusaEnd = """
        G91 ; relative moves
        G1 E-1 F2100 ; pull the filament back
        G1 Z10 F720 ; lift off the print
        G90
        G1 X0 Y200 F3600 ; present the print
        M106 S0 ; fan off
        M104 S0 ; nozzle off
        M140 S0 ; bed off
        M84 ; motors off
        """;

    /// <summary>Klipper (Voron, Creality K1, Sovol SV07/SV08, Neptune 4…): plain G-code; most configs also have a PRINT_START macro.</summary>
    private const string KlipperStart = """
        G28 ; home all axes
        G92 E0
        G1 Z5 F3000 ; lift the nozzle
        G1 X5 Y20 Z0.3 F6000 ; to the front-left edge
        G1 X5 Y{prime_end} E15 F1500 ; prime a line
        G92 E0
        """;

    private const string KlipperEnd = """
        G91 ; relative moves
        G1 E-2 F2700 ; pull the filament back
        G1 Z10 F3000 ; lift off the print
        G90
        G1 X{bed_width_half} Y{bed_depth} F6000 ; present the print
        M106 S0 ; fan off
        M104 S0 ; nozzle off
        M140 S0 ; bed off
        M84 ; motors off
        """;

    /// <summary>Sailfish (MakerBot-style: the Flashforge Creator Pro): home X and Y to their maximum and Z to its minimum, recall the offsets, prime along the front.</summary>
    private const string SailfishStart = """
        G162 X Y F2000 ; home X and Y to their maximum
        G161 Z F900 ; home Z to its minimum
        G92 X0 Y0 Z-5 A0 B0
        G1 Z0.0 F900
        G161 Z F100 ; home Z again, slowly
        M132 X Y Z A B ; recall the stored home offsets
        G92 A0 B0
        G1 X-{bed_width_half} Y-{bed_depth_half} Z5 F3000 ; over the front-left corner (every axis: the first move after recalling)
        G1 Z0.3 F900
        G1 X0 Y-{bed_depth_half} E10 F1200 ; prime a line along the front
        G92 A0 B0
        """;

    private const string SailfishEnd = """
        M104 S0 T0 ; nozzle off
        M140 S0 T0 ; bed off
        G162 Z F900 ; the bed down
        G162 X Y F2000 ; the head to the back
        M18 ; motors off
        """;

    /// <summary>A delta (Marlin): homing goes to the top, the origin is the middle of the round bed.</summary>
    private const string DeltaStart = """
        G28 ; home all axes (to the top)
        G92 E0
        G1 X-{bed_width_third} Y0 Z0.3 F4000 ; to the left of the bed
        G1 X-{bed_width_third} Y{bed_width_third} E10 F1500 ; prime an arc's chord
        G92 E0
        """;

    private const string DeltaEnd = """
        G91 ; relative moves
        G1 E-2 F2700 ; pull the filament back
        G90
        G28 ; home to the top, off the print
        M106 S0 ; fan off
        M104 S0 ; nozzle off
        M140 S0 ; bed off
        M84 ; motors off
        """;

    private static PrinterProfile Marlin(string name, double w, double d, double h, double retract, bool probe = false, string notes = "", double accel = 500) => new()
    {
        Name = name, Acceleration = accel, BedWidth = w, BedDepth = d, MaxHeight = h, RetractLength = retract, Notes = notes,
        StartGCode = probe ? MarlinProbeStart : MarlinStart, EndGCode = MarlinEnd,
    };

    private static PrinterProfile Klipper(string name, double w, double d, double h, string notes = "", double accel = 5000) => new()
    {
        Name = name, Acceleration = accel, CornerSpeed = 5, BedWidth = w, BedDepth = d, MaxHeight = h, RetractLength = DirectDrive,
        Notes = "Klipper. If your printer's configuration has a PRINT_START macro, its start G-code can call that instead." + (notes.Length > 0 ? " " + notes : ""),
        StartGCode = KlipperStart, EndGCode = KlipperEnd,
    };

    private static PrinterProfile Prusa(string name, double w, double d, double h, double retract, bool mesh) => new()
    {
        Name = name, Acceleration = mesh ? 1250 : 4000, BedWidth = w, BedDepth = d, MaxHeight = h, RetractLength = retract,
        Notes = "Prusa firmware. It may ask to confirm the G-code is for this printer: PrusaSlicer adds a printer check this doesn't.",
        StartGCode = mesh ? PrusaMeshStart : PrusaProbeStart, EndGCode = PrusaEnd,
    };

    private static PrinterProfile Bambu(string name, double size) => new()
    {
        Name = name, Acceleration = 10000, CornerSpeed = 9, BedWidth = size, BedDepth = size, MaxHeight = size, RetractLength = DirectDrive,
        Notes = "Bambu Lab printers expect Bambu Studio's own start G-code and a .gcode.3mf; plain G-code from here is for the sizes, and may need their start and end G-code pasted in.",
        StartGCode = KlipperStart, EndGCode = KlipperEnd,
    };

    private static PrinterProfile Delta(string name, double diameter, double h, string notes = "") => new()
    {
        Name = name, Acceleration = 1500, BedWidth = diameter, BedDepth = diameter, MaxHeight = h, RoundBed = true, OriginAtCentre = true,
        RetractLength = Bowden, Notes = "A delta: round bed, origin at its middle." + (notes.Length > 0 ? " " + notes : ""),
        StartGCode = DeltaStart, EndGCode = DeltaEnd,
    };

    private static PrinterProfile Ultimaker(string name, double w, double d, double h, string notes) => new()
    {
        Name = name, Acceleration = 3000, CornerSpeed = 20, BedWidth = w, BedDepth = d, MaxHeight = h, FilamentDiameter = 2.85, RetractLength = 6.5, Notes = notes,
        StartGCode = MarlinStart, EndGCode = MarlinEnd,
    };

    public static readonly IReadOnlyList<PrinterProfile> All =
    [
        // Prusa Research
        Prusa("Prusa i3 MK3S+", 250, 210, 210, DirectDrive, mesh: true),
        Prusa("Prusa MK4S", 250, 210, 220, DirectDrive, mesh: false),
        Prusa("Prusa MINI+", 180, 180, 180, 3.2, mesh: true),
        Prusa("Prusa XL (one head)", 360, 360, 360, DirectDrive, mesh: false),
        Prusa("Prusa CORE One", 250, 220, 270, DirectDrive, mesh: false),

        // Creality
        Marlin("Creality Ender 3 / 3 Pro / 3 V2", 220, 220, 250, Bowden),
        Marlin("Creality Ender 3 V3 SE", 220, 220, 250, DirectDrive, probe: true),
        Marlin("Creality Ender 3 S1 / S1 Pro", 220, 220, 270, DirectDrive, probe: true),
        Marlin("Creality Ender 5 Plus", 350, 350, 400, Bowden, probe: true),
        Marlin("Creality CR-10", 300, 300, 400, Bowden),
        Marlin("Creality CR-10 S5", 500, 500, 500, Bowden),
        Klipper("Creality K1", 220, 220, 250, accel: 12000),
        Klipper("Creality K1 Max", 300, 300, 300, accel: 12000),

        // Bambu Lab
        Bambu("Bambu Lab X1 Carbon", 256),
        Bambu("Bambu Lab P1S / P1P", 256),
        Bambu("Bambu Lab A1", 256),
        Bambu("Bambu Lab A1 mini", 180),

        // Anycubic
        Marlin("Anycubic i3 Mega", 210, 210, 205, Bowden),
        Marlin("Anycubic Vyper", 245, 245, 260, Bowden, probe: true),
        Marlin("Anycubic Kobra 2", 220, 220, 250, DirectDrive, probe: true),
        Marlin("Anycubic Kobra 2 Max", 420, 420, 500, DirectDrive, probe: true),

        // Elegoo
        Marlin("Elegoo Neptune 3 Pro", 225, 225, 280, DirectDrive, probe: true),
        Klipper("Elegoo Neptune 4 Pro", 225, 225, 265),
        Klipper("Elegoo Neptune 4 Max", 420, 420, 480),

        // Flashforge
        new PrinterProfile
        {
            Name = "Flashforge Creator Pro", BedWidth = 227, BedDepth = 148, MaxHeight = 150, OriginAtCentre = true, RetractLength = 1.0, X3gMachine = "fcp",
            Notes = "Sailfish firmware reads .x3g, not G-code: save .x3g here (or convert the G-code with GPX: gpx -m fcp part.gcode part.x3g). Origin at the bed's middle; prints with the right extruder.",
            StartGCode = SailfishStart, EndGCode = SailfishEnd,
        },
        Marlin("Flashforge Adventurer 3", 150, 150, 150, DirectDrive, notes: "Its own firmware: it may want Flashforge's .gx file from FlashPrint rather than plain G-code."),
        Klipper("Flashforge Adventurer 5M", 220, 220, 220),

        // Ultimaker
        Ultimaker("Ultimaker 2+", 223, 223, 205, "2.85 mm filament. Set the printer's G-code flavour to RepRap (Marlin) so it takes temperatures from the file."),
        Ultimaker("Ultimaker S3", 230, 190, 200, "2.85 mm filament. The S line expects Cura's Griffin header; plain G-code may be refused."),
        Ultimaker("Ultimaker S5", 330, 240, 300, "2.85 mm filament. The S line expects Cura's Griffin header; plain G-code may be refused."),

        // Voron
        Klipper("Voron 2.4 (250)", 250, 250, 250),
        Klipper("Voron 2.4 (300)", 300, 300, 300),
        Klipper("Voron 2.4 (350)", 350, 350, 350),
        Klipper("Voron Trident (300)", 300, 300, 250),

        // Sovol
        Marlin("Sovol SV06", 220, 220, 250, DirectDrive, probe: true),
        Klipper("Sovol SV07", 220, 220, 250),
        Klipper("Sovol SV08", 350, 350, 345),

        // Artillery
        Marlin("Artillery Genius Pro", 220, 220, 250, DirectDrive, probe: true),
        Marlin("Artillery Sidewinder X2", 300, 300, 400, DirectDrive, probe: true),

        // LulzBot
        new PrinterProfile
        {
            Name = "LulzBot TAZ 6", BedWidth = 280, BedDepth = 280, MaxHeight = 250, FilamentDiameter = 2.85, RetractLength = 1.0,
            Notes = "2.85 mm filament. Cura LulzBot Edition's start G-code also wipes the nozzle before probing: paste it in for the full routine.",
            StartGCode = MarlinProbeStart, EndGCode = MarlinEnd,
        },
        new PrinterProfile
        {
            Name = "LulzBot Mini 2", BedWidth = 160, BedDepth = 160, MaxHeight = 180, FilamentDiameter = 2.85, RetractLength = 1.0,
            Notes = "2.85 mm filament. Cura LulzBot Edition's start G-code also wipes the nozzle before probing: paste it in for the full routine.",
            StartGCode = MarlinProbeStart, EndGCode = MarlinEnd,
        },

        // QIDI
        Klipper("QIDI X-Plus 3", 280, 280, 270),
        Klipper("QIDI Q1 Pro", 245, 245, 240),

        // Raise3D and Snapmaker
        Marlin("Raise3D Pro2 (one head)", 305, 305, 300, DirectDrive, notes: "Its own firmware: ideaMaker's start G-code may be wanted for the full routine."),
        Marlin("Snapmaker 2.0 A350", 320, 350, 330, DirectDrive, probe: true, notes: "Snapmaker's firmware may want Luban's header at the top of the file."),

        // Deltas
        Delta("Anycubic Kossel Linear Plus", 230, 300),
        Delta("FLSUN Q5", 200, 200),
        Delta("FLSUN V400", 300, 410, "Klipper: its own start macro may be wanted."),
    ];
}
