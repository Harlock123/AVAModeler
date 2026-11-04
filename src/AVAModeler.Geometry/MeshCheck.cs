using System.Globalization;
using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>The kinds of trouble <see cref="MeshCheck"/> finds, each something that can be selected to be seen.</summary>
public enum MeshProblem
{
    /// <summary>Edges used by one face only: the rims of holes (selected as edges).</summary>
    BoundaryEdges,
    /// <summary>Edges used by three faces or more (selected as edges).</summary>
    NonManifoldEdges,
    /// <summary>Faces that disagree with their neighbours' winding, or whose closed shell faces inward: what Align turns (selected as polygons).</summary>
    FlippedPolygons,
    /// <summary>Faces with no area to speak of, or that use one point twice (selected as polygons).</summary>
    DegeneratePolygons,
    /// <summary>Faces of four or more points that aren't flat (selected as polygons).</summary>
    NonPlanarPolygons,
    /// <summary>Points in the same place as another (selected as points, every copy).</summary>
    DuplicatePoints,
    /// <summary>Points no polygon uses (selected as points).</summary>
    UnusedPoints,
    /// <summary>1-point and 2-point polygons: dots and lines, not part of any solid (selected as polygons).</summary>
    PointsAndLines,
}

/// <summary>How close is "the same" for <see cref="MeshCheck"/>.</summary>
public sealed record MeshCheckOptions
{
    /// <summary>Points closer than this (metres) count as duplicates, and faces thinner than it as degenerate.</summary>
    public double PointTolerance { get; init; } = 1e-6;

    /// <summary>A face is non-planar when a point is further from its plane than this fraction of the face's size.</summary>
    public double PlanarTolerance { get; init; } = 1e-3;

    public static MeshCheckOptions Default { get; } = new();
}

/// <summary>One shell (a piece connected through shared edges) of a layer's faces.</summary>
public sealed class ShellInfo
{
    /// <summary>The faces in it, by polygon index.</summary>
    public required List<int> Faces { get; init; }
    public required Box3 Bounds { get; init; }
    /// <summary>Every edge of the shell is used by exactly two of its faces.</summary>
    public bool Closed { get; init; }
    /// <summary>Every edge's two faces run along it in opposite directions.</summary>
    public bool Consistent { get; init; }
    /// <summary>The signed enclosed volume (m³), when the shell is closed and consistent: positive facing out.</summary>
    public double? Volume { get; init; }
    /// <summary>A closed shell facing inward that sits inside another closed one: a hollow, not a mistake.</summary>
    public bool Cavity { get; internal set; }
    /// <summary>A closed shell facing inward that is not a cavity: it's inside out.</summary>
    public bool Inward => Volume < 0 && !Cavity;
}

/// <summary>What <see cref="MeshCheck"/> found in one layer (or one mesh).</summary>
public sealed class LayerCheck
{
    /// <summary>The layer, from 0 (-1 for a mesh checked on its own).</summary>
    public int Layer { get; init; } = -1;
    public int Points { get; init; }
    public int Polygons { get; init; }
    public (int Points, int Lines, int Triangles, int Quads, int NGons) Types { get; init; }
    public Box3 Bounds { get; init; } = Box3.Empty;
    /// <summary>The faces' total area, m².</summary>
    public double Area { get; init; }
    public List<Edge> BoundaryEdges { get; } = [];
    public List<Edge> NonManifoldEdges { get; } = [];
    /// <summary>Edges whose two faces run along them the same way: one of the two is flipped.</summary>
    public List<Edge> InconsistentEdges { get; } = [];
    /// <summary>How many separate loops the boundary edges make: the holes.</summary>
    public int Holes { get; internal set; }
    public List<ShellInfo> Shells { get; } = [];
    public List<int> FlippedPolygons { get; } = [];
    public List<int> DegeneratePolygons { get; } = [];
    public List<int> NonPlanarPolygons { get; } = [];
    public List<int> PointsAndLines { get; } = [];
    /// <summary>Every point that has another in the same place (all the copies).</summary>
    public List<int> DuplicatePoints { get; } = [];
    /// <summary>How many points merging would remove (the copies beyond the first of each place).</summary>
    public int DuplicateExtras { get; internal set; }
    public List<int> UnusedPoints { get; } = [];

    public int Faces => Types.Triangles + Types.Quads + Types.NGons;
    /// <summary>No holes and no edge shared by more than two faces (there are faces).</summary>
    public bool Watertight => Faces > 0 && BoundaryEdges.Count == 0 && NonManifoldEdges.Count == 0;
    /// <summary>Watertight with every edge's faces agreeing, so the volume means something.</summary>
    public bool Solid => Watertight && InconsistentEdges.Count == 0;
    public int InwardShells => Shells.Count(s => s.Inward);
    public int Cavities => Shells.Count(s => s.Cavity);
    /// <summary>The enclosed volume (m³) when the layer is <see cref="Solid"/>: cavities take theirs away.</summary>
    public double? Volume { get; internal set; }
    /// <summary>The centre of mass of the enclosed volume, at even density, when there's a volume.</summary>
    public Vec3? CenterOfMass { get; internal set; }

    /// <summary>The elements a problem concerns: point or polygon indices, or (for edge problems) none; see <see cref="Edges"/>.</summary>
    public IReadOnlyList<int> Elements(MeshProblem problem) => problem switch
    {
        MeshProblem.FlippedPolygons => FlippedPolygons,
        MeshProblem.DegeneratePolygons => DegeneratePolygons,
        MeshProblem.NonPlanarPolygons => NonPlanarPolygons,
        MeshProblem.DuplicatePoints => DuplicatePoints,
        MeshProblem.UnusedPoints => UnusedPoints,
        MeshProblem.PointsAndLines => PointsAndLines,
        _ => [],
    };

    /// <summary>The edges an edge problem concerns (none for the others).</summary>
    public IReadOnlyList<Edge> Edges(MeshProblem problem) => problem switch
    {
        MeshProblem.BoundaryEdges => BoundaryEdges,
        MeshProblem.NonManifoldEdges => NonManifoldEdges,
        _ => [],
    };
}

/// <summary>What <see cref="MeshCheck"/> found in the layers it looked at, layer by layer and in all.</summary>
public sealed class MeshReport
{
    public required List<LayerCheck> Layers { get; init; }

    public int Points => Layers.Sum(l => l.Points);
    public int Polygons => Layers.Sum(l => l.Polygons);
    public int Faces => Layers.Sum(l => l.Faces);
    public (int Points, int Lines, int Triangles, int Quads, int NGons) Types => (
        Layers.Sum(l => l.Types.Points), Layers.Sum(l => l.Types.Lines), Layers.Sum(l => l.Types.Triangles),
        Layers.Sum(l => l.Types.Quads), Layers.Sum(l => l.Types.NGons));
    public Box3 Bounds => Layers.Aggregate(Box3.Empty, (b, l) => b.Include(l.Bounds));
    public double Area => Layers.Sum(l => l.Area);
    public int Shells => Layers.Sum(l => l.Shells.Count);
    public int Holes => Layers.Sum(l => l.Holes);
    public int BoundaryEdges => Layers.Sum(l => l.BoundaryEdges.Count);
    public int NonManifoldEdges => Layers.Sum(l => l.NonManifoldEdges.Count);
    public int InconsistentEdges => Layers.Sum(l => l.InconsistentEdges.Count);
    public int FlippedPolygons => Layers.Sum(l => l.FlippedPolygons.Count);
    public int InwardShells => Layers.Sum(l => l.InwardShells);
    public int Cavities => Layers.Sum(l => l.Cavities);
    public int DegeneratePolygons => Layers.Sum(l => l.DegeneratePolygons.Count);
    public int NonPlanarPolygons => Layers.Sum(l => l.NonPlanarPolygons.Count);
    public int DuplicatePoints => Layers.Sum(l => l.DuplicateExtras);
    public int UnusedPoints => Layers.Sum(l => l.UnusedPoints.Count);
    public int PointsAndLines => Layers.Sum(l => l.PointsAndLines.Count);

    /// <summary>No holes and no edge shared by three faces or more, in any layer with faces (and there are faces).</summary>
    public bool Watertight => Faces > 0 && Layers.Where(l => l.Faces > 0).All(l => l.Watertight);
    /// <summary>Watertight, every edge's faces agreeing, every shell facing out and no degenerate faces: ready to print.</summary>
    public bool Printable => Watertight && InconsistentEdges == 0 && InwardShells == 0 && DegeneratePolygons == 0;

    /// <summary>The enclosed volume (m³), when every layer with faces is solid.</summary>
    public double? Volume => Faces > 0 && Layers.Where(l => l.Faces > 0).All(l => l.Volume.HasValue) ? Layers.Sum(l => l.Volume ?? 0) : null;

    /// <summary>The centre of mass of the volume (even density), when there is one.</summary>
    public Vec3? CenterOfMass
    {
        get
        {
            if (Volume is not { } v || Math.Abs(v) < 1e-300) return null;
            var s = Vec3.Zero;
            foreach (var l in Layers)
                if (l.Volume is { } lv && l.CenterOfMass is { } c) s += c * lv;
            return s / v;
        }
    }

    /// <summary>How many there are of one kind of problem (for duplicates, the copies merging would remove).</summary>
    public int Count(MeshProblem problem) => problem switch
    {
        MeshProblem.BoundaryEdges => BoundaryEdges,
        MeshProblem.NonManifoldEdges => NonManifoldEdges,
        MeshProblem.FlippedPolygons => FlippedPolygons,
        MeshProblem.DegeneratePolygons => DegeneratePolygons,
        MeshProblem.NonPlanarPolygons => NonPlanarPolygons,
        MeshProblem.DuplicatePoints => DuplicatePoints,
        MeshProblem.UnusedPoints => UnusedPoints,
        _ => PointsAndLines,
    };

    /// <summary>One kind of problem in a few words with how many there are ("4 boundary edges (1 hole)").</summary>
    public string Describe(MeshProblem problem) => problem switch
    {
        MeshProblem.BoundaryEdges => $"{Plural(BoundaryEdges, "boundary edge")} ({Plural(Holes, "hole")})",
        MeshProblem.NonManifoldEdges => Plural(NonManifoldEdges, "non-manifold edge"),
        MeshProblem.FlippedPolygons => Plural(FlippedPolygons, "flipped polygon") + (InwardShells > 0 ? $" ({Plural(InwardShells, "shell")} inside out)" : ""),
        MeshProblem.DegeneratePolygons => Plural(DegeneratePolygons, "degenerate polygon"),
        MeshProblem.NonPlanarPolygons => Plural(NonPlanarPolygons, "non-planar polygon"),
        MeshProblem.DuplicatePoints => Plural(DuplicatePoints, "duplicate point"),
        MeshProblem.UnusedPoints => Plural(UnusedPoints, "unused point"),
        _ => Plural(PointsAndLines, "1- or 2-point polygon"),
    };

    /// <summary>What stops the mesh being a clean solid, worst first, one phrase each (empty when there's nothing).</summary>
    public List<string> Issues()
    {
        var list = new List<string>();
        if (BoundaryEdges > 0) list.Add($"open: {Plural(Holes, "hole")} along {Plural(BoundaryEdges, "edge")}");
        if (NonManifoldEdges > 0) list.Add($"not manifold: {Plural(NonManifoldEdges, "edge")} shared by 3 or more polygons");
        if (InconsistentEdges > 0) list.Add($"inconsistent winding: {Plural(InconsistentEdges, "edge")} between neighbours facing opposite ways");
        if (InwardShells > 0) list.Add($"inside out: {Plural(InwardShells, "closed shell")} facing inward");
        if (DegeneratePolygons > 0) list.Add($"{Plural(DegeneratePolygons, "degenerate polygon")}");
        return list;
    }

    /// <summary>Things worth knowing that don't stop it being a solid.</summary>
    public List<string> Notes()
    {
        var list = new List<string>();
        if (DuplicatePoints > 0) list.Add($"{Plural(DuplicatePoints, "duplicate point")} (Merge Points welds them)");
        if (UnusedPoints > 0) list.Add($"{Plural(UnusedPoints, "unused point")}");
        if (NonPlanarPolygons > 0) list.Add($"{Plural(NonPlanarPolygons, "non-planar polygon")} (Triple makes them flat)");
        if (PointsAndLines > 0) list.Add($"{Plural(PointsAndLines, "1- or 2-point polygon")} (not part of a solid; STL leaves them out)");
        if (Cavities > 0) list.Add($"{Plural(Cavities, "cavity", "cavities")} (inward shells inside others)");
        return list;
    }

    /// <summary>One line that sums it up: "Closed and printable", "Open: 4 holes along 32 edges" and so on.</summary>
    public string Verdict
    {
        get
        {
            if (Faces == 0) return Polygons > 0 ? "No faces: only points and lines" : Points > 0 ? "No polygons, only points" : "Empty: nothing in the foreground";
            var issues = Issues();
            if (issues.Count == 0) return $"Closed and printable ({Plural(Shells, "shell")})";
            string text = string.Join("; ", issues);
            return char.ToUpperInvariant(text[0]) + text[1..];
        }
    }

    /// <summary>The report as lines of text, for people and macros: counts, size, area, volume, then what's wrong.</summary>
    public List<string> Lines()
    {
        var t = Types;
        var lines = new List<string>
        {
            Verdict,
            $"{Points} points, {Polygons} polygons: {t.Triangles} triangles, {t.Quads} quads, {t.NGons} n-gons, {t.Lines} lines, {t.Points} dots",
        };
        var b = Bounds;
        if (!b.IsEmpty)
        {
            lines.Add($"Size {Size(b.Size)}");
            lines.Add($"From {Vector(b.Min)} to {Vector(b.Max)}");
        }
        if (Faces > 0)
        {
            lines.Add($"Area {Num(Area)} m²");
            lines.Add($"{Plural(Shells, "shell")}, {(Watertight ? "watertight" : $"{Plural(Holes, "hole")}")}");
            if (Volume is { } v)
            {
                lines.Add($"Volume {Num(v)} m³" + (Math.Abs(v) < 1e-3 ? $" ({Num(v * 1e6)} cm³)" : ""));
                if (CenterOfMass is { } c) lines.Add($"Centre of mass {Vector(c)}");
            }
        }
        foreach (var n in Notes()) lines.Add(char.ToUpperInvariant(n[0]) + n[1..]);
        return lines;
    }

    internal static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";

    /// <summary>A number to six significant figures, in plain digits.</summary>
    public static string Num(double v)
    {
        if (v == 0 || !double.IsFinite(v)) return v.ToString(CultureInfo.InvariantCulture);
        int digits = Math.Clamp(5 - (int)Math.Floor(Math.Log10(Math.Abs(v))), 0, 12);
        return Math.Round(v, digits).ToString("0.############", CultureInfo.InvariantCulture);
    }

    private static string Vector(Vec3 v) => $"[{Num(v.X)}, {Num(v.Y)}, {Num(v.Z)}]";

    /// <summary>
    /// A size in the unit the views' grid would use for it (µm, mm, m or km, by its longest side), with metres beside it
    /// when that isn't metres.
    /// </summary>
    private static string Size(Vec3 s)
    {
        double longest = Math.Max(s.X, Math.Max(s.Y, s.Z));
        var (scale, unit) = longest switch { < 1e-3 => (1e6, "µm"), < 1 => (1e3, "mm"), < 1000 => (1.0, "m"), _ => (1e-3, "km") };
        string m = $"{Num(s.X)} × {Num(s.Y)} × {Num(s.Z)} m";
        return unit == "m" ? m : $"{Num(s.X * scale)} × {Num(s.Y * scale)} × {Num(s.Z * scale)} {unit} ({m})";
    }
}

/// <summary>
/// Checks meshes for what stops them being clean solids (for 3D printing, booleans and rendering): holes, edges shared
/// by too many faces, faces wound against their neighbours or shells inside out, degenerate and non-planar faces,
/// duplicate and unused points; and measures them: counts, bounds, area, and for closed meshes the enclosed volume
/// and centre of mass. It changes nothing.
/// </summary>
public static class MeshCheck
{
    /// <summary>Checks the foreground layers (the whole layers: a hole belongs to the whole mesh, not to a selection).</summary>
    public static MeshReport Document(Document doc, MeshCheckOptions? options = null) => Layers(doc, doc.ForegroundLayers, options);

    /// <summary>Checks some layers (indexed from 0), each on its own.</summary>
    public static MeshReport Layers(Document doc, IEnumerable<int> layers, MeshCheckOptions? options = null) =>
        new() { Layers = layers.Distinct().Order().Select(l => Mesh(doc.Layers[l].Mesh, options, l)).ToList() };

    /// <summary>Checks one mesh.</summary>
    public static LayerCheck Mesh(Mesh mesh, MeshCheckOptions? options = null, int layer = -1)
    {
        var o = options ?? MeshCheckOptions.Default;
        var faces = Enumerable.Range(0, mesh.Polygons.Count).Where(i => mesh.Polygons[i].Count >= 3).ToList();
        var check = new LayerCheck
        {
            Layer = layer,
            Points = mesh.Points.Count,
            Polygons = mesh.Polygons.Count,
            Types = mesh.PolygonTypes(),
            Bounds = mesh.Bounds(),
            Area = faces.Sum(f => mesh.Area(mesh.Polygons[f])),
        };
        // A face that uses a point twice has no proper edges: it is reported as degenerate and left out of the topology.
        var proper = faces.Where(f => mesh.Polygons[f].Points.Distinct().Count() == mesh.Polygons[f].Count).ToList();
        Edges(mesh, proper, check);
        Shells(mesh, proper, check);
        Polygons(mesh, faces, check, o);
        PointsCheck(mesh, check, o);
        return check;
    }

    /// <summary>Counts each edge's faces and the directions they run along it: boundary, non-manifold and inconsistent edges, and the holes.</summary>
    private static void Edges(Mesh mesh, List<int> faces, LayerCheck check)
    {
        var uses = new Dictionary<Edge, (int Count, int Forward)>();
        foreach (int f in faces)
            foreach (var (a, b) in mesh.Polygons[f].Edges())
            {
                if (a == b) continue;
                var e = Edge.Of(a, b);
                var (count, forward) = uses.GetValueOrDefault(e);
                uses[e] = (count + 1, forward + (a == e.A ? 1 : 0));
            }
        foreach (var (e, (count, forward)) in uses.OrderBy(kv => kv.Key.A).ThenBy(kv => kv.Key.B))
        {
            if (count == 1) check.BoundaryEdges.Add(e);
            else if (count >= 3) check.NonManifoldEdges.Add(e);
            else if (forward != 1) check.InconsistentEdges.Add(e);
        }
        // The holes: the boundary edges joined into loops through their points.
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            if (!parent.TryGetValue(x, out int p)) return parent[x] = x;
            while (p != x)
            {
                int up = parent[p];
                parent[x] = up;
                x = p;
                p = up;
            }
            return x;
        }
        foreach (var e in check.BoundaryEdges) parent[Find(e.A)] = Find(e.B);
        check.Holes = check.BoundaryEdges.SelectMany(e => new[] { e.A, e.B }).Select(Find).Distinct().Count();
    }

    /// <summary>The shells, each closed or not, and its volume; the cavities; the faces Align would turn; the volume in all.</summary>
    private static void Shells(Mesh mesh, List<int> faces, LayerCheck check)
    {
        var inconsistent = check.InconsistentEdges.ToHashSet();
        foreach (var piece in Topology.Components(mesh, faces))
        {
            bool closed = Topology.IsClosed(mesh, piece);
            bool consistent = piece.All(f => mesh.Polygons[f].Edges().All(e => e.A == e.B || !inconsistent.Contains(Edge.Of(e.A, e.B))));
            check.Shells.Add(new ShellInfo
            {
                Faces = piece,
                Bounds = mesh.Bounds(mesh.PointsOf(piece)),
                Closed = closed,
                Consistent = consistent,
                Volume = closed && consistent ? MeshMeasure.Volume(mesh, piece) : null,
            });
        }
        // An inward shell inside another closed one is a hollow (a cavity), as a boolean subtracting a ball from inside a
        // box makes, whichever way the outer one faces now (Align turns that one out and leaves the cavity be).
        foreach (var s in check.Shells.Where(s => s.Volume < 0))
            s.Cavity = check.Shells.Any(o => o != s && o.Closed && o.Bounds != s.Bounds && o.Bounds.Contains(s.Bounds.Min) && o.Bounds.Contains(s.Bounds.Max));
        // The faces Align would turn, found by letting it work on a copy; a cavity's faces are right as they are.
        var copy = mesh.Clone();
        var cavityFaces = check.Shells.Where(s => s.Cavity).SelectMany(s => s.Faces).ToHashSet();
        Topology.Orient(copy, faces.Where(f => !cavityFaces.Contains(f)));
        foreach (int f in faces)
            if (!copy.Polygons[f].Points.SequenceEqual(mesh.Polygons[f].Points)) check.FlippedPolygons.Add(f);
        if (check.Solid)
        {
            check.Volume = check.Shells.Sum(s => s.Volume ?? 0);
            check.CenterOfMass = CenterOfMass(mesh, faces, check.Bounds.Center);
        }
    }

    /// <summary>
    /// The centre of the volume the faces enclose, at even density: each face is fanned into triangles and each
    /// triangle makes a signed tetrahedron with a point inside the bounds (as <see cref="MeshMeasure.Volume"/> does with
    /// the origin), whose centroids, weighted by volume, average to the centre.
    /// </summary>
    public static Vec3? CenterOfMass(Mesh mesh, IEnumerable<int> faces, Vec3 origin)
    {
        double total = 0;
        var sum = Vec3.Zero;
        foreach (int f in faces)
        {
            var p = mesh.Polygons[f].Points;
            if (p.Count < 3) continue;
            var a = mesh.Points[p[0]] - origin;
            for (int i = 1; i + 1 < p.Count; i++)
            {
                var b = mesh.Points[p[i]] - origin;
                var c = mesh.Points[p[i + 1]] - origin;
                double v = Vec3.Dot(a, Vec3.Cross(b, c)) / 6;
                total += v;
                sum += (a + b + c) * (v / 4);
            }
        }
        return Math.Abs(total) < 1e-300 ? null : origin + sum / total;
    }

    /// <summary>Degenerate, non-planar and 1- and 2-point polygons.</summary>
    private static void Polygons(Mesh mesh, List<int> faces, LayerCheck check, MeshCheckOptions o)
    {
        for (int i = 0; i < mesh.Polygons.Count; i++)
            if (mesh.Polygons[i].Count < 3) check.PointsAndLines.Add(i);
        foreach (int f in faces)
        {
            var poly = mesh.Polygons[f];
            if (poly.Points.Distinct().Count() != poly.Count || MeshCleanup.IsDegenerate(mesh, poly.Points, o.PointTolerance))
            {
                check.DegeneratePolygons.Add(f);
                continue;
            }
            if (poly.Count < 4) continue;
            double size = mesh.Bounds(poly.Points).Size.Length;
            if (mesh.NonPlanarity(poly) > Math.Max(o.PlanarTolerance * size, o.PointTolerance)) check.NonPlanarPolygons.Add(f);
        }
    }

    /// <summary>Duplicate points (within the tolerance, bucketed on a grid) and unused ones.</summary>
    private static void PointsCheck(Mesh mesh, LayerCheck check, MeshCheckOptions o)
    {
        var used = new bool[mesh.Points.Count];
        foreach (var p in mesh.Polygons)
        {
            foreach (int i in p.Points) if (i >= 0 && i < used.Length) used[i] = true;
            if (p.Details != null)
                foreach (var d in p.Details)
                    foreach (int i in d.Points) if (i >= 0 && i < used.Length) used[i] = true;
        }
        for (int i = 0; i < used.Length; i++)
            if (!used[i]) check.UnusedPoints.Add(i);

        double tol = Math.Max(o.PointTolerance, 0);
        double cell = Math.Max(tol, 1e-12) * 2;
        var grid = new Dictionary<(long, long, long), List<int>>();
        var dupes = new SortedSet<int>();
        int extras = 0;
        for (int i = 0; i < mesh.Points.Count; i++)
        {
            var p = mesh.Points[i];
            var key = ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
            int found = -1;
            for (long dx = -1; dx <= 1 && found < 0; dx++)
            for (long dy = -1; dy <= 1 && found < 0; dy++)
            for (long dz = -1; dz <= 1 && found < 0; dz++)
                if (grid.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                    foreach (int j in bucket)
                        if (Vec3.Near(mesh.Points[j], p, tol)) { found = j; break; }
            if (found >= 0)
            {
                extras++;
                dupes.Add(found);
                dupes.Add(i);
                continue;
            }
            if (!grid.TryGetValue(key, out var own)) grid[key] = own = [];
            own.Add(i);
        }
        check.DuplicatePoints.AddRange(dupes);
        check.DuplicateExtras = extras;
    }
}
