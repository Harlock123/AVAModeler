namespace AVAModeler.Slicing;

/// <summary>How sparse infill is laid.</summary>
public enum InfillPattern
{
    /// <summary>Parallel lines, turned 90° each layer.</summary>
    Lines,
    /// <summary>Lines both ways in every layer, half as dense each way: stiffer, a little slower.</summary>
    Grid,
    /// <summary>
    /// The gyroid: wavy lines whose shape shifts with height, so the infill is one curved surface through the print,
    /// about as strong every way. A little slower to print than lines.
    /// </summary>
    Gyroid,
}

/// <summary>Where supports may stand.</summary>
public enum SupportPlacement
{
    /// <summary>On the bed, or on the object where it's below (they come away from it, leaving a rougher patch).</summary>
    Everywhere,
    /// <summary>Only columns that reach the bed: nothing stands on the object.</summary>
    BedOnly,
}

/// <summary>Where each closed loop starts and ends (the seam, where a small mark is left).</summary>
public enum SeamPosition
{
    /// <summary>At the back of each loop (its point furthest along +Y), so seams line up out of sight.</summary>
    Rear,
    /// <summary>At the point nearest the nozzle: less travel, scattered seams.</summary>
    Nearest,
}

/// <summary>
/// The settings that decide how a print is sliced and laid down. Lengths in mm, speeds in mm/s, angles in degrees.
/// Immutable: change one with <c>with</c> and <see cref="Checked"/> it against the printer.
/// </summary>
public sealed record SliceSettings
{
    /// <summary>The height of each layer after the first.</summary>
    public double LayerHeight { get; init; } = 0.2;
    /// <summary>The first layer's height: a little thicker sticks better to the bed.</summary>
    public double FirstLayerHeight { get; init; } = 0.24;
    /// <summary>
    /// Choose each layer's height from the slopes it crosses (between <see cref="AdaptiveMin"/> and
    /// <see cref="AdaptiveMax"/>), so curves and gentle slopes print fine and straight walls quick; <see cref="LayerHeight"/>
    /// is then unused after the first layer.
    /// </summary>
    public bool AdaptiveLayers { get; init; }
    public double AdaptiveMin { get; init; } = 0.08;
    public double AdaptiveMax { get; init; } = 0.28;
    /// <summary>How far a sloped surface may step from one layer to the next (mm): less is finer and slower.</summary>
    public double AdaptiveCusp { get; init; } = 0.06;
    /// <summary>The width of a printed line: usually a little wider than the nozzle.</summary>
    public double LineWidth { get; init; } = 0.45;

    /// <summary>How many loops make the walls round each outline and hole.</summary>
    public int Walls { get; init; } = 2;
    /// <summary>Solid layers under the top surfaces.</summary>
    public int TopLayers { get; init; } = 4;
    /// <summary>Solid layers over the bottom surfaces (the first layers on the bed among them).</summary>
    public int BottomLayers { get; init; } = 3;
    /// <summary>How much of the inside is filled, 0 to 100%.</summary>
    public double InfillPercent { get; init; } = 20;
    public InfillPattern Infill { get; init; } = InfillPattern.Lines;
    /// <summary>The angle of the infill and solid lines from the X axis; each layer turns it 90°.</summary>
    public double InfillAngle { get; init; } = 45;
    /// <summary>How far, as a fraction of the line width, the infill reaches into the walls so the two bond.</summary>
    public double InfillOverlap { get; init; } = 0.15;
    public SeamPosition Seam { get; init; } = SeamPosition.Rear;
    /// <summary>Fill the gaps the walls leave where there's no room for another loop, and fins too thin for a wall, with narrower lines.</summary>
    public bool GapFill { get; init; } = true;
    /// <summary>Print solid areas over air as bridges: straight lines across at the best-anchored angle, slower, the fan full.</summary>
    public bool Bridging { get; init; } = true;
    /// <summary>Iron top surfaces: a last pass over them, close lines with a trickle of plastic, to smooth them.</summary>
    public bool Ironing { get; init; }
    /// <summary>Ironing's lines, this far apart (mm).</summary>
    public double IroningSpacing { get; init; } = 0.1;
    /// <summary>The plastic ironing lays, as a percentage of a line's.</summary>
    public double IroningFlowPercent { get; init; } = 10;
    public double IroningSpeed { get; init; } = 15;
    /// <summary>Keep travel inside the object where it can (no wall crossed, no retraction).</summary>
    public bool Combing { get; init; } = true;

    /// <summary>Loops of skirt round the first layer (to prime the nozzle); 0 for none. A brim replaces it.</summary>
    public int SkirtLoops { get; init; } = 1;
    /// <summary>The gap between the skirt and the object.</summary>
    public double SkirtDistance { get; init; } = 3;
    /// <summary>Lines of brim round the first layer, touching the object, to hold it down; 0 for none.</summary>
    public int BrimLines { get; init; }

    /// <summary>The extruder that prints the part, from 0 (the first: on the Creator Pro the right one).</summary>
    public int ModelExtruder { get; init; }
    /// <summary>The extruder for supports: -1 for the part's, or the other, for a second (support) material.</summary>
    public int SupportExtruder { get; init; } = -1;
    /// <summary>Build supports under overhangs.</summary>
    public bool Supports { get; init; }
    /// <summary>Overhangs steeper than this from the vertical get support (0 would support every slope, 90 nothing).</summary>
    public double SupportAngle { get; init; } = 50;
    /// <summary>How densely support is filled, 5 to 100%.</summary>
    public double SupportPercent { get; init; } = 15;
    /// <summary>The gap between support and the object, sideways.</summary>
    public double SupportGap { get; init; } = 0.7;
    public SupportPlacement SupportOn { get; init; } = SupportPlacement.Everywhere;
    /// <summary>Dense layers at the top of the support, under the object, so it's held flat and comes away clean; 0 for none.</summary>
    public int SupportInterfaceLayers { get; init; } = 2;
    /// <summary>How densely the interface layers are filled, 10 to 100%.</summary>
    public double SupportInterfacePercent { get; init; } = 70;

    public double PrintSpeed { get; init; } = 50;
    public double OuterWallSpeed { get; init; } = 30;
    public double InfillSpeed { get; init; } = 60;
    public double FirstLayerSpeed { get; init; } = 20;
    public double BridgeSpeed { get; init; } = 25;
    /// <summary>Gap fill's speed: its lines are short and narrow.</summary>
    public double GapFillSpeed { get; init; } = 25;
    public double TravelSpeed { get; init; } = 150;
    /// <summary>Travel shorter than this doesn't retract.</summary>
    public double RetractMinTravel { get; init; } = 1.5;
    /// <summary>How far the nozzle lifts on a retracted travel move; 0 for none.</summary>
    public double ZHop { get; init; }

    /// <summary>The settings, or an <see cref="ArgumentException"/> saying what's wrong (some limits depend on the nozzle).</summary>
    public SliceSettings Checked(PrinterProfile printer)
    {
        double nozzle = printer.NozzleDiameter;
        double most = Math.Round(nozzle * 0.8, 3);
        PrinterProfile.Range(LayerHeight, 0.04, most, "The layer height");
        PrinterProfile.Range(FirstLayerHeight, 0.04, most, "The first layer's height");
        PrinterProfile.Range(AdaptiveMin, 0.04, most, "The thinnest adaptive layer");
        PrinterProfile.Range(AdaptiveMax, AdaptiveMin, most, "The thickest adaptive layer");
        PrinterProfile.Range(AdaptiveCusp, 0.005, 1, "The adaptive step");
        PrinterProfile.Range(LineWidth, Math.Round(nozzle * 0.6, 3), Math.Round(nozzle * 2, 3), "The line width");
        PrinterProfile.Range(Walls, 1, 20, "The walls", " loops");
        PrinterProfile.Range(TopLayers, 0, 100, "The top layers", "");
        PrinterProfile.Range(BottomLayers, 0, 100, "The bottom layers", "");
        PrinterProfile.Range(InfillPercent, 0, 100, "The infill", "%");
        PrinterProfile.Range(InfillAngle, -360, 360, "The infill angle", "°");
        PrinterProfile.Range(InfillOverlap, 0, 0.5, "The infill overlap", " of a line width");
        PrinterProfile.Range(SkirtLoops, 0, 20, "The skirt", " loops");
        PrinterProfile.Range(SkirtDistance, 0, 50, "The skirt distance");
        PrinterProfile.Range(BrimLines, 0, 100, "The brim", " lines");
        PrinterProfile.Range(SupportAngle, 0, 90, "The support angle", "°");
        PrinterProfile.Range(SupportPercent, 5, 100, "The support density", "%");
        PrinterProfile.Range(SupportGap, 0, 5, "The support gap");
        PrinterProfile.Range(SupportInterfaceLayers, 0, 20, "The support interface layers", "");
        PrinterProfile.Range(SupportInterfacePercent, 10, 100, "The support interface density", "%");
        foreach (var (v, what) in (ReadOnlySpan<(double, string)>)[(PrintSpeed, "The print speed"), (OuterWallSpeed, "The outer wall speed"),
                     (InfillSpeed, "The infill speed"), (FirstLayerSpeed, "The first layer speed"), (TravelSpeed, "The travel speed"),
                     (BridgeSpeed, "The bridge speed"), (GapFillSpeed, "The gap fill speed")])
            PrinterProfile.Range(v, 1, 1000, what, " mm/s");
        PrinterProfile.Range(RetractMinTravel, 0, 100, "The shortest retracted travel");
        PrinterProfile.Range(ZHop, 0, 10, "The Z hop");
        PrinterProfile.Range(IroningSpacing, 0.02, LineWidth, "The ironing spacing");
        PrinterProfile.Range(IroningFlowPercent, 0, 50, "The ironing flow", "%");
        PrinterProfile.Range(IroningSpeed, 1, 1000, "The ironing speed", " mm/s");
        if (ModelExtruder < 0 || ModelExtruder >= printer.Extruders)
            throw new ArgumentException(printer.Extruders == 1 ? $"{printer.Name} has one extruder: print with it." : $"{printer.Name}'s extruders are 1 to {printer.Extruders}.");
        if (SupportExtruder < -1 || SupportExtruder >= printer.Extruders)
            throw new ArgumentException(printer.Extruders == 1 ? $"{printer.Name} has one extruder: supports print with it too." : $"{printer.Name}'s extruders are 1 to {printer.Extruders}.");
        return this;
    }
}
