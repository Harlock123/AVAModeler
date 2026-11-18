using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVAModeler.Mcp;

/// <summary>
/// Checkpoints kept on disk, to outlive the session: a folder per checkpoint with the object (object.lwo), the scene
/// with its meshes inside it (scene.avascene) and checkpoint.json (its name, time, note, the layers' names and states,
/// and the points' and polygons' ids). An agent's go in the working folder's .avacheckpoints (never deleted by the app);
/// the in-app chat's in its saved chat's folder (<see cref="CheckpointFolder"/>, the newest kept).
/// </summary>
public sealed partial class ModelerTools
{
    /// <summary>The working folder's folder for agents' kept checkpoints.</summary>
    public const string KeptCheckpoints = ".avacheckpoints";

    /// <summary>
    /// Where this session keeps checkpoints asked to persist, chosen by the app (the in-app chat's saved chat's folder),
    /// or null for the working folder's .avacheckpoints. Checkpoints there beyond the newest
    /// <see cref="MaxCheckpoints"/> are deleted; the working folder's never are.
    /// </summary>
    public string? CheckpointFolder { get; set; }

    private string KeptFolder => CheckpointFolder ?? Path.Combine(host.Root, KeptCheckpoints);

    /// <summary>A checkpoint's folder name: its name with anything a file name can't hold replaced.</summary>
    private static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim()) sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ' ' or '.' ? ch : '_');
        var s = sb.ToString().Trim('.', ' ');
        return s.Length == 0 ? "checkpoint" : s.Length > 80 ? s[..80] : s;
    }

    /// <summary>Writes a checkpoint just taken to its folder (replacing one of the same name): the scene is still as it was then.</summary>
    private void Persist(Checkpoint cp)
    {
        var dir = Path.Combine(KeptFolder, Slug(cp.Name));
        Directory.CreateDirectory(dir);
        var doc = DocumentOf(cp);
        // LWOB keeps the layers' meshes, surfaces and maps; the ids, names and states go in checkpoint.json.
        if (doc.Layers.Any(l => !l.Mesh.IsEmpty)) ObjectFiles.Save(doc, Path.Combine(dir, "object.lwo"));
        else File.Delete(Path.Combine(dir, "object.lwo"));
        SceneJson.WriteFile(cp.Scene, Path.Combine(dir, "scene.avascene"), embedMeshes: true);
        var layers = new JsonArray();
        for (int i = 0; i < cp.Layers; i++)
        {
            var layer = cp.Object.Layers.TryGetValue(i, out var l) ? l : new Layer();
            var ids = i < cp.Ids.Layers.Count ? cp.Ids.Layers[i] : new GeometryIds.LayerIds();
            layers.Add(new JsonObject
            {
                ["name"] = layer.Name,
                ["state"] = layer.State.ToString(),
                ["point_ids"] = new JsonArray([.. ids.PointIds.Select(x => (JsonNode)x)]),
                ["polygon_ids"] = new JsonArray([.. ids.PolygonIds.Select(x => (JsonNode)x)]),
                ["next_point"] = ids.NextPoint,
                ["next_polygon"] = ids.NextPolygon,
            });
        }
        var meta = new JsonObject
        {
            ["format"] = "avamodeler-checkpoint",
            ["version"] = 1,
            ["name"] = cp.Name,
            ["taken"] = cp.Taken.ToString("O", CultureInfo.InvariantCulture),
            ["note"] = cp.Note,
            ["layers"] = layers,
        };
        File.WriteAllText(Path.Combine(dir, "checkpoint.json"), meta.ToJsonString(), new UTF8Encoding(false));
        if (CheckpointFolder != null) Prune();
    }

    /// <summary>The app's own checkpoint folder keeps the newest only.</summary>
    private void Prune()
    {
        try
        {
            var dirs = new DirectoryInfo(KeptFolder).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(MaxCheckpoints);
            foreach (var d in dirs) d.Delete(recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The checkpoints kept on disk, by name (newest first).</summary>
    private List<(string Name, DateTime Taken, string? Note, string Folder)> KeptOnDisk()
    {
        var list = new List<(string, DateTime, string?, string)>();
        if (!Directory.Exists(KeptFolder)) return list;
        foreach (var dir in Directory.GetDirectories(KeptFolder))
        {
            try
            {
                var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "checkpoint.json")));
                if ((string?)meta?["format"] != "avamodeler-checkpoint") continue;
                var taken = DateTime.TryParse((string?)meta["taken"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : Directory.GetLastWriteTimeUtc(dir);
                list.Add(((string?)meta["name"] ?? Path.GetFileName(dir), taken, (string?)meta["note"], dir));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return [.. list.OrderByDescending(x => x.Item2)];
    }

    /// <summary>A checkpoint read back from disk, or null if there's none of that name.</summary>
    private Checkpoint? LoadKept(string name, Document doc, LayoutSession l)
    {
        var found = KeptOnDisk().FirstOrDefault(x => x.Name == name.Trim());
        if (found.Folder == null) return null;
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(found.Folder, "checkpoint.json")))!;
        var layers = meta["layers"] as JsonArray ?? [];
        var objectPath = Path.Combine(found.Folder, "object.lwo");
        var loaded = File.Exists(objectPath) ? ObjectFiles.Load(objectPath) : new Document();
        while (loaded.Layers.Count < layers.Count) loaded.Layers.Add(new Layer());
        var states = new List<GeometryIds.LayerIds>();
        for (int i = 0; i < layers.Count; i++)
        {
            var info = layers[i]!;
            var layer = loaded.Layers[i];
            layer.Name = (string?)info["name"] ?? "";
            layer.State = Enum.TryParse<LayerState>((string?)info["state"], out var st) ? st : LayerState.Off;
            var mesh = layer.Mesh;
            int[] pointIds = [.. (info["point_ids"] as JsonArray ?? []).Select(x => (int)x!)];
            int[] polygonIds = [.. (info["polygon_ids"] as JsonArray ?? []).Select(x => (int)x!)];
            var ids = new GeometryIds.LayerIds { NextPoint = (int?)info["next_point"] ?? 1, NextPolygon = (int?)info["next_polygon"] ?? 1 };
            if (pointIds.Length == mesh.Points.Count && polygonIds.Length == mesh.Polygons.Count)
            {
                ids.Points = [.. mesh.Points];
                ids.PointIds = pointIds;
                ids.Polygons = [.. mesh.Polygons.Select(p => p.Points.ToArray())];
                ids.Surfaces = [.. mesh.Polygons.Select(p => p.Surface)];
                ids.PolygonIds = polygonIds;
            }
            states.Add(ids);
        }
        loaded.Selection = new Selection();
        var scenePath = Path.Combine(found.Folder, "scene.avascene");
        var scene = File.Exists(scenePath) ? SceneJson.ReadFile(scenePath).Scene : AVAModeler.Scene.Scene.CreateDefault();
        return new Checkpoint(found.Name, found.Taken, found.Note, loaded,
            loaded.Snapshot(found.Name, Enumerable.Range(0, loaded.Layers.Count)), loaded.Layers.Count,
            new GeometryIds.State(states), scene, scene.Snapshot(), SceneText(scene), [.. scene.Items.Select(i => i.Name)])
        { Kept = true };
    }
}
