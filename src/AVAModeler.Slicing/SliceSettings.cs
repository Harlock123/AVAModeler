namespace AVAModeler.Slicing;

/// <summary>How sparse infill is laid.</summary>
public enum InfillPattern
{
    /// <summary>Parallel lines, turned 90° each layer.</summary>
    Lines,
    /// <summary>Lines both ways in every layer, half as dense each way: stiffer, a little slower.</summary>
    Grid,
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

    /// <summary>Loops of skirt round the first layer (to prime the nozzle); 0 for none. A brim replaces it.</summary>
    public int SkirtLoops { get; init; } = 1;
    /// <summary>The gap between the skirt and the object.</summary>
    public double SkirtDistance { get; init; } = 3;
    /// <summary>Lines of brim round the first layer, touching the object, to hold it down; 0 for none.</summary>
    public int BrimLines { get; init; }

    /// <summary>Build supports under overhangs.</summary>
    public bool Supports { get; init; }
    /// <summary>Overhangs steeper than this from the vertical get support (0 would support every slope, 90 nothing).</summary>
    public double SupportAngle { get; init; } = 50;
    /// <summary>How densely support is filled, 5 to 100%.</summary>
    public double SupportPercent { get; init; } = 15;
    /// <summary>The gap between support and the object, sideways.</summary>
    public double SupportGap { get; init; } = 0.7;

    public double PrintSpeed { get; init; } = 50;
    public double OuterWallSpeed { get; init; } = 30;
    public double InfillSpeed { get; init; } = 60;
    public double FirstLayerSpeed { get; init; } = 20;
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
        foreach (var (v, what) in (ReadOnlySpan<(double, string)>)[(PrintSpeed, "The print speed"), (OuterWallSpeed, "The outer wall speed"),
                     (InfillSpeed, "The infill speed"), (FirstLayerSpeed, "The first layer speed"), (TravelSpeed, "The travel speed")])
            PrinterProfile.Range(v, 1, 1000, what, " mm/s");
        PrinterProfile.Range(RetractMinTravel, 0, 100, "The shortest retracted travel");
        PrinterProfile.Range(ZHop, 0, 10, "The Z hop");
        return this;
    }
}
