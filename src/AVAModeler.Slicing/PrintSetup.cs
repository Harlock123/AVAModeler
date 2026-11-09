using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVAModeler.Slicing;

/// <summary>
/// Everything a print is set up with, as the app keeps it between sessions (in the preferences): the printer and
/// material chosen, the person's own printers and materials beside the presets, and the placement. Plain JSON through
/// System.Text.Json, enums by name.
/// </summary>
public sealed record PrintSetup
{
    public PrinterProfile Printer { get; init; } = Presets.Printers[0];
    public MaterialProfile Material { get; init; } = Presets.Materials[0];
    public PlacementOptions Placement { get; init; } = new();
    public SliceSettings Settings { get; init; } = new();
    /// <summary>Printers the person saved, shown after the presets.</summary>
    public IReadOnlyList<PrinterProfile> MyPrinters { get; init; } = [];
    /// <summary>Materials the person saved, shown after the presets.</summary>
    public IReadOnlyList<MaterialProfile> MyMaterials { get; init; } = [];

    /// <summary>The presets, then the person's own.</summary>
    [JsonIgnore] public IEnumerable<PrinterProfile> AllPrinters => Presets.Printers.Concat(MyPrinters);
    [JsonIgnore] public IEnumerable<MaterialProfile> AllMaterials => Presets.Materials.Concat(MyMaterials);

    /// <summary>The setup with every part checked, or an <see cref="ArgumentException"/> saying what's wrong.</summary>
    public PrintSetup Checked()
    {
        Printer.Checked();
        Material.Checked();
        Placement.Checked();
        Settings.Checked(Printer);
        foreach (var p in MyPrinters) p.Checked();
        foreach (var m in MyMaterials) m.Checked();
        return this;
    }

    /// <summary>The setup with this printer saved among the person's own (replacing one of the same name) and chosen.</summary>
    public PrintSetup SavePrinter(PrinterProfile printer)
    {
        printer.Checked();
        if (Presets.Printers.Any(p => p.Name == printer.Name))
            throw new ArgumentException($"\"{printer.Name}\" is a preset's name: give your printer a name of its own.");
        return this with { Printer = printer, MyPrinters = [.. MyPrinters.Where(p => p.Name != printer.Name), printer] };
    }

    /// <summary>The setup with this material saved among the person's own (replacing one of the same name) and chosen.</summary>
    public PrintSetup SaveMaterial(MaterialProfile material)
    {
        material.Checked();
        if (Presets.Materials.Any(m => m.Name == material.Name))
            throw new ArgumentException($"\"{material.Name}\" is a preset's name: give your material a name of its own.");
        return this with { Material = material, MyMaterials = [.. MyMaterials.Where(m => m.Name != material.Name), material] };
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A setup from JSON; anything missing takes its default, and a setup that doesn't check out is refused.</summary>
    public static PrintSetup FromJson(string json) => (JsonSerializer.Deserialize<PrintSetup>(json, Json) ?? new PrintSetup()).Checked();
}
