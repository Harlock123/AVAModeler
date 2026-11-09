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
    /// <summary>The material in the other extruder, for supports printed with it (<see cref="SliceSettings.SupportExtruder"/>).</summary>
    public MaterialProfile SupportMaterial { get; init; } = Presets.Materials[0];
    public PlacementOptions Placement { get; init; } = new();
    public SliceSettings Settings { get; init; } = new();
    /// <summary>The printer on the network to send prints to (its API key is kept apart, in <see cref="PrintHostKey"/>).</summary>
    public PrintHost Host { get; init; } = new();
    /// <summary>Printers the person saved, shown after the presets.</summary>
    public IReadOnlyList<PrinterProfile> MyPrinters { get; init; } = [];
    /// <summary>Print profiles the person saved, shown after <see cref="PrintProfile.Presets"/>.</summary>
    public IReadOnlyList<PrintProfile> MyProfiles { get; init; } = [];
    /// <summary>The profile last chosen (its name), if any.</summary>
    public string? ProfileName { get; init; }
    /// <summary>Materials the person saved, shown after the presets.</summary>
    public IReadOnlyList<MaterialProfile> MyMaterials { get; init; } = [];

    /// <summary>The presets, then the person's own.</summary>
    [JsonIgnore] public IEnumerable<PrinterProfile> AllPrinters => Presets.Printers.Concat(MyPrinters);
    [JsonIgnore] public IEnumerable<MaterialProfile> AllMaterials => Presets.Materials.Concat(MyMaterials);
    [JsonIgnore] public IEnumerable<PrintProfile> AllProfiles => PrintProfile.Presets.Concat(MyProfiles);

    /// <summary>The setup with the profile's settings (keeping supports, brim, skirt and extruders) and its name.</summary>
    public PrintSetup WithProfile(string name)
    {
        var profile = AllProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There's no print profile \"{name}\": {string.Join(", ", AllProfiles.Select(p => p.Name))}.");
        return this with { Settings = profile.ApplyTo(Settings), ProfileName = profile.Name };
    }

    /// <summary>The current settings saved as a profile of the person's own (replacing one of the same name).</summary>
    public PrintSetup SaveProfile(string name)
    {
        var profile = new PrintProfile(name.Trim(), Settings).Checked(Printer);
        if (PrintProfile.Presets.Any(p => p.Name == profile.Name))
            throw new ArgumentException($"\"{profile.Name}\" is a preset's name: give your profile a name of its own.");
        return this with { MyProfiles = [.. MyProfiles.Where(p => p.Name != profile.Name), profile], ProfileName = profile.Name };
    }

    /// <summary>The setup with every part checked, or an <see cref="ArgumentException"/> saying what's wrong.</summary>
    public PrintSetup Checked()
    {
        Printer.Checked();
        Material.Checked();
        SupportMaterial.Checked();
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
