namespace AVAModeler.Core;

/// <summary>
/// A layer's linked copies of another layer's mesh: each a placement (a transform), all following the source as it
/// changes, drawn and rendered as if they were there but kept as one mesh and the places (LWOB's AVIN chunk).
/// </summary>
public sealed class LayerInstances
{
    /// <summary>The layer whose mesh is copied (from 0).</summary>
    public int Source;
    public List<Mat4> Places = [];

    public LayerInstances Clone() => new() { Source = Source, Places = [.. Places] };
}

/// <summary>Instances' meshes, and documents with them made real.</summary>
public static class Instancing
{
    /// <summary>Whether a layer's instances can be drawn: a source that exists, isn't the layer itself and has no instances of its own.</summary>
    public static bool Valid(Document doc, int layer) =>
        layer >= 0 && layer < doc.Layers.Count && doc.Layers[layer].Instances is { } inst
        && inst.Source >= 0 && inst.Source < doc.Layers.Count && inst.Source != layer && doc.Layers[inst.Source].Instances == null;

    /// <summary>A layer's instances as one mesh: the source's mesh at each place (empty if it has none).</summary>
    private sealed record Cached(int Layer, LayerInstances Instances, Mesh Source, int Version, int Count, Mesh Copies);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document, List<Cached>> Cache = new();

    public static Mesh Copies(Document doc, int layer)
    {
        if (!Valid(doc, layer)) return new Mesh();
        var inst = doc.Layers[layer].Instances!;
        var source = doc.Layers[inst.Source].Mesh;
        var list = Cache.GetOrCreateValue(doc);
        lock (list)
        {
            if (list.Find(c => c.Layer == layer) is { } hit && ReferenceEquals(hit.Instances, inst) && hit.Count == inst.Places.Count
                && ReferenceEquals(hit.Source, source) && hit.Version == source.Version)
                return hit.Copies;
        }
        var made = Make(inst, source);
        lock (list)
        {
            list.RemoveAll(c => c.Layer == layer);
            list.Add(new Cached(layer, inst, source, source.Version, inst.Places.Count, made));
        }
        return made;
    }

    private static Mesh Make(LayerInstances inst, Mesh source)
    {
        var m = new Mesh();
        foreach (var place in inst.Places)
        {
            int first = m.Append(source);
            for (int i = first; i < m.Points.Count; i++) m.Points[i] = place.TransformPoint(m.Points[i]);
            if (place.Determinant() < 0)
                for (int p = m.Polygons.Count - source.Polygons.Count; p < m.Polygons.Count; p++) m.Polygons[p].Points.Reverse();
        }
        m.Touch();
        return m;
    }

    /// <summary>A layer's own mesh with its instances made real after it (the layer's own mesh when it has none).</summary>
    public static Mesh WithInstances(Document doc, int layer)
    {
        var own = doc.Layers[layer].Mesh;
        if (!Valid(doc, layer)) return own;
        var m = own.Clone();
        m.Append(Copies(doc, layer));
        return m;
    }

    /// <summary>Whether any layer has instances.</summary>
    public static bool Any(Document doc) => doc.Layers.Any(l => l.Instances is { Places.Count: > 0 });

    /// <summary>
    /// The document with every layer's instances made real geometry (for exports, printing and anything else that reads
    /// meshes): itself, when there are none.
    /// </summary>
    public static Document Frozen(Document doc)
    {
        if (!Any(doc)) return doc;
        var copy = doc.Copy();
        for (int i = 0; i < copy.Layers.Count; i++)
            if (Valid(doc, i))
            {
                copy.Layers[i].Mesh = WithInstances(doc, i);
                copy.Layers[i].Instances = null;
            }
        foreach (var l in copy.Layers) l.Instances = null;
        return copy;
    }
}
