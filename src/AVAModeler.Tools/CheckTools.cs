using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Tools;

/// <summary>
/// Select Problems: selects what <see cref="MeshCheck"/> finds wrong in the foreground layers, of one kind, so it can be
/// seen (edges in Edges mode, polygons in Polygons mode, points in Points mode). It's one undo step, so Ctrl+Z puts the
/// selection back as it was; the object itself doesn't change.
/// </summary>
public sealed class SelectProblemsTool : Tool
{
    public override string Name => "Select Problems";
    public override string Tab => "Polygon";
    public override string Summary => "Selects one kind of problem the mesh check (Mesh Info, check_mesh) finds in the foreground layers, replacing that mode's selection and switching to its mode: BoundaryEdges (the rims of holes) and NonManifoldEdges (shared by 3 or more faces) as edges; FlippedPolygons (what Align would turn), DegeneratePolygons, NonPlanarPolygons and PointsAndLines (1- and 2-point polygons) as polygons; DuplicatePoints (every copy) and UnusedPoints as points. Changes no geometry; one undo step.";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Help => "Choose the kind of problem, then Apply: the views show it selected.";
    protected override bool SelectsOnly => true;

    [Param("Problem", Description = "Which kind of problem to select.")] public MeshProblem Problem { get; set; } = MeshProblem.BoundaryEdges;

    /// <summary>How many elements the last run selected.</summary>
    public int Selected { get; private set; }

    /// <summary>The selection mode a kind of problem is selected in.</summary>
    public static SelectionMode ModeFor(MeshProblem problem) => problem switch
    {
        MeshProblem.BoundaryEdges or MeshProblem.NonManifoldEdges => SelectionMode.Edges,
        MeshProblem.DuplicatePoints or MeshProblem.UnusedPoints => SelectionMode.Points,
        _ => SelectionMode.Polygons,
    };

    /// <summary>
    /// The tool that already fixes a kind of problem across the foreground, safely and in one step, or null where it
    /// takes judgement (an extra face to find). Holes go to Fill Hole, which closes each with a face wound to match its
    /// neighbours (a rim lying on top of another is duplicate points, for Merge Points, rather than a hole to fill).
    /// </summary>
    public static string? FixFor(MeshProblem problem) => problem switch
    {
        MeshProblem.BoundaryEdges => "Fill Hole",
        MeshProblem.FlippedPolygons => "Align",
        MeshProblem.DegeneratePolygons or MeshProblem.UnusedPoints => "Remove Degenerate",
        MeshProblem.DuplicatePoints => "Merge Points",
        _ => null,
    };

    protected override void Apply()
    {
        var report = MeshCheck.Document(Doc);
        var sel = Doc.Selection;
        sel.Mode = ModeFor(Problem);
        sel.Clear();
        Selected = 0;
        foreach (var layer in report.Layers)
        {
            int l = layer.Layer;
            switch (sel.Mode)
            {
                case SelectionMode.Edges:
                    sel.EdgesIn(l).UnionWith(layer.Edges(Problem));
                    Selected += layer.Edges(Problem).Count;
                    break;
                case SelectionMode.Points:
                    foreach (int p in layer.Elements(Problem)) sel.SelectPoint(l, p);
                    Selected += layer.Elements(Problem).Count;
                    break;
                default:
                    sel.PolygonsIn(l).UnionWith(layer.Elements(Problem));
                    Selected += layer.Elements(Problem).Count;
                    break;
            }
        }
    }
}

/// <summary>Remove Degenerate: deletes faces with no area to speak of (and, optionally, points nothing uses).</summary>
public sealed class RemoveDegenerateTool : Tool
{
    public override string Name => "Remove Degenerate";
    public override string Tab => "Polygon";
    public override string Summary => "Tidies the selected faces (all of them if none are selected): drops a point repeated straight after itself, then deletes faces left with fewer than three distinct points, faces that use a point twice, and faces whose points all lie within Tolerance of a line (no area). 1- and 2-point polygons stay. With Remove unused points, also deletes every point in the foreground layers that no polygon uses. Clears the selection.";
    public override ToolKind Kind => ToolKind.Immediate;

    [Param("Tolerance", Distance = true, Min = 0, Description = "A face whose points all lie within this distance of a line has no area and goes.")] public double Tolerance { get; set; } = 1e-6;
    [Param("Remove unused points", Description = "Also delete points no polygon uses, anywhere in the foreground layers.")] public bool UnusedPoints { get; set; } = true;

    /// <summary>How many faces and points the last run removed.</summary>
    public int RemovedPolygons { get; private set; }
    public int RemovedPoints { get; private set; }

    protected override void Apply()
    {
        RemovedPolygons = RemovedPoints = 0;
        foreach (var (_, mesh, polys) in ActivePolygons().ToList())
        {
            RemovedPolygons += PolygonOps.RemoveDegenerate(mesh, polys.ToList(), Tolerance);
            if (UnusedPoints) RemovedPoints += mesh.RemoveUnusedPoints();
        }
        Doc.Selection.Clear(allModes: true);
    }
}
