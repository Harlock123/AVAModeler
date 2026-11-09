namespace AVAModeler.Slicing;

/// <summary>
/// A named set of print settings ("Fine 0.12", "Strong"), to pick in one go. Choosing one changes how the print is
/// laid down (layers, walls, infill, speeds…) but keeps what belongs to the object in hand: supports and where they
/// stand, the brim and skirt, and which extruder prints what.
/// </summary>
public sealed record PrintProfile(string Name, SliceSettings Settings)
{
    /// <summary>The profile's settings over <paramref name="current"/>, keeping the object's own choices.</summary>
    public SliceSettings ApplyTo(SliceSettings current) => Settings with
    {
        Supports = current.Supports,
        SupportOn = current.SupportOn,
        BrimLines = current.BrimLines,
        SkirtLoops = current.SkirtLoops,
        ModelExtruder = current.ModelExtruder,
        SupportExtruder = current.SupportExtruder,
    };

    public PrintProfile Checked(PrinterProfile printer)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("A print profile needs a name.");
        Settings.Checked(printer);
        return this;
    }

    /// <summary>The profiles the app starts with (for a 0.4 mm nozzle; finer or coarser nozzles want their own).</summary>
    public static readonly IReadOnlyList<PrintProfile> Presets =
    [
        new("Standard 0.20", new SliceSettings()),
        new("Draft 0.28", new SliceSettings
        {
            LayerHeight = 0.28, FirstLayerHeight = 0.28, Walls = 2, TopLayers = 3, BottomLayers = 3, InfillPercent = 15,
            PrintSpeed = 60, OuterWallSpeed = 40, InfillSpeed = 80,
        }),
        new("Fine 0.12", new SliceSettings
        {
            LayerHeight = 0.12, FirstLayerHeight = 0.2, Walls = 3, TopLayers = 7, BottomLayers = 5, InfillPercent = 20,
            PrintSpeed = 40, OuterWallSpeed = 25, InfillSpeed = 50,
        }),
        new("Strong", new SliceSettings
        {
            LayerHeight = 0.2, Walls = 4, TopLayers = 5, BottomLayers = 5, InfillPercent = 40, Infill = InfillPattern.Gyroid,
        }),
    ];
}
