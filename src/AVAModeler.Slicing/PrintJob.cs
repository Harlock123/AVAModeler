using AVAModeler.Core;

namespace AVAModeler.Slicing;

/// <summary>A document sliced with a setup: every stage's result, the G-code and what the print will take.</summary>
public sealed record PrintJob(PrintSetup Setup, PrintModel Model, SlicedModel Sliced, PrintPlan Plan, string GCode, PrintStats Stats)
{
    /// <summary>
    /// What the person should know before printing: what doesn't fit, the mesh check's verdict when it isn't
    /// printable, and parts of layers left out because the mesh is open there.
    /// </summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var list = new List<string>(Model.Problems);
            if (Model.Triangles.Count > 0 && !Model.Printable) list.Add($"The mesh check says: {Model.Verdict}. Parts may print wrongly or be missing.");
            if (Sliced.OpenChains > 0)
                list.Add($"{Sliced.OpenChains} cut outline{(Sliced.OpenChains == 1 ? "" : "s")} didn't close (the mesh has holes there) and {(Sliced.OpenChains == 1 ? "was" : "were")} left out.");
            return list;
        }
    }

    /// <summary>Places, slices, plans and writes the G-code for the document's foreground layers.</summary>
    public static PrintJob Run(Document doc, PrintSetup setup)
    {
        setup.Checked();
        var model = Placement.Place(doc, setup.Printer, setup.Placement);
        var sliced = Slicer.Slice(model, setup.Settings, setup.Printer);
        var plan = Planner.Plan(sliced, setup.Printer);
        var (text, stats) = Slicing.GCode.Write(plan, setup.Material);
        return new PrintJob(setup, model, sliced, plan, text, stats);
    }
}
