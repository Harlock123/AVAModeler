using System.Text.Json.Nodes;
using AVAModeler.Core;

namespace AVAModeler.Core.Tests;

/// <summary>Reference images behind the flat views: their placing maths, the picture's adjustments, and keeping setups.</summary>
public class BackdropTests
{
    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "avamodeler-backdrop-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) { }
        }
    }

    private static void Near(Vec3 want, Vec3 got) =>
        Assert.True((want - got).Length < 1e-9, $"wanted {want}, got {got}");

    [Fact]
    public void Each_flat_view_has_the_axes_its_camera_looks_along()
    {
        Assert.Equal((Vec3.UnitX, Vec3.UnitZ), BackdropMapping.Axes(BackdropView.Top));
        Assert.Equal((Vec3.UnitX, Vec3.UnitY), BackdropMapping.Axes(BackdropView.Front));
        Assert.Equal((Vec3.UnitZ, Vec3.UnitY), BackdropMapping.Axes(BackdropView.Side));
        Assert.Equal(("Z", "Y"), BackdropMapping.AxisNames(BackdropView.Side));
        var p = new Vec3(1, 2, 3);
        Assert.Equal(new Vec2(1, 3), BackdropMapping.OnView(BackdropView.Top, p));
        Assert.Equal(new Vec2(1, 2), BackdropMapping.OnView(BackdropView.Front, p));
        Assert.Equal(new Vec2(3, 2), BackdropMapping.OnView(BackdropView.Side, p));
        Near(new Vec3(0, 2, 3), BackdropMapping.ToWorld(BackdropView.Side, new Vec2(3, 2)));
    }

    [Fact]
    public void The_image_keeps_its_shape_unless_a_height_is_given_and_its_corners_sit_round_its_centre()
    {
        var b = new Backdrop { Image = "x.png", Center = new Vec2(1, 2), Width = 4 };
        // 400 × 200 pixels: 4 m wide, 2 m high.
        Assert.Equal((4.0, 2.0), BackdropMapping.Size(b, 400, 200));
        Assert.Equal((4.0, 3.0), BackdropMapping.Size(b with { Height = 3 }, 400, 200));
        var c = BackdropMapping.Corners(BackdropView.Side, b, 400, 200);
        // Bottom left, bottom right, top right, top left, on the side view's Z (across) and Y (up), at X = 0.
        Near(new Vec3(0, 1, -1), c[0]);
        Near(new Vec3(0, 1, 3), c[1]);
        Near(new Vec3(0, 3, 3), c[2]);
        Near(new Vec3(0, 3, -1), c[3]);
        var top = BackdropMapping.Corners(BackdropView.Top, b, 400, 200);
        Near(new Vec3(-1, 0, 1), top[0]);
        Near(new Vec3(3, 0, 3), top[2]);
        // Where world points fall in the image: the corners at 0 and 1, the centre at a half, depth ignored.
        Assert.Equal(new Vec2(0.5, 0.5), BackdropMapping.ImagePoint(BackdropView.Front, b, 400, 200, new Vec3(1, 2, 7)));
        Assert.Equal(new Vec2(0, 0), BackdropMapping.ImagePoint(BackdropView.Front, b, 400, 200, new Vec3(-1, 1, 0)));
        Assert.Equal(new Vec2(1, 1), BackdropMapping.ImagePoint(BackdropView.Front, b, 400, 200, new Vec3(3, 3, 0)));
        var box = BackdropMapping.Bounds(BackdropView.Front, b, 400, 200);
        Assert.Equal((new Vec3(-1, 1, 0), new Vec3(3, 3, 0)), (box.Min, box.Max));
    }

    [Fact]
    public void Fit_puts_the_object_inside_the_image_with_a_margin_keeping_its_shape()
    {
        var b = new Backdrop { Image = "x.png" };
        var box = new Box3(new Vec3(-1, 0, -0.5), new Vec3(3, 1, 0.5));
        // A square image round a 4 × 1 front view: 4 m plus 5% each side, centred on it.
        var (center, width) = BackdropMapping.FitTo(BackdropView.Front, box, b, 100, 100);
        Assert.Equal(new Vec2(1, 0.5), center);
        Assert.Equal(4.4, width, 9);
        // A tall image (1 wide, 4 high) round the same box seen from the side (1 across, 1 up): its width is the limit.
        var (c2, w2) = BackdropMapping.FitTo(BackdropView.Side, box, b, 100, 400);
        Assert.Equal(new Vec2(0, 0.5), c2);
        Assert.Equal(1.1, w2, 9);
        // A wide one round something tall: the height decides.
        var tall = new Box3(new Vec3(0, 0, 0), new Vec3(1, 2, 1));
        Assert.Equal(4.4, BackdropMapping.FitTo(BackdropView.Front, tall, b, 200, 100).Width, 9);
        Assert.Throws<InvalidOperationException>(() => BackdropMapping.FitTo(BackdropView.Top, Box3.Empty, b, 1, 1));
        // Sized by its pixels: 300 pixels at 5 mm each.
        Assert.Equal(1.5, BackdropMapping.WidthFromPixels(300, 0.005), 12);
        Assert.Throws<ArgumentException>(() => BackdropMapping.WidthFromPixels(300, 0));
    }

    [Fact]
    public void Settings_out_of_range_are_refused_saying_which()
    {
        var b = new Backdrop { Image = "x.png" };
        Assert.Same(b, b.Checked());
        Assert.Contains("image", Assert.Throws<ArgumentException>(() => new Backdrop().Checked()).Message);
        Assert.Contains("width", Assert.Throws<ArgumentException>(() => (b with { Width = 0 }).Checked()).Message);
        Assert.Contains("height", Assert.Throws<ArgumentException>(() => (b with { Height = -1 }).Checked()).Message);
        Assert.Contains("Opacity", Assert.Throws<ArgumentException>(() => (b with { Opacity = 2 }).Checked()).Message);
        Assert.Contains("Contrast", Assert.Throws<ArgumentException>(() => (b with { Contrast = 5 }).Checked()).Message);
        Assert.Contains("Brightness", Assert.Throws<ArgumentException>(() => (b with { Brightness = -2 }).Checked()).Message);
        Assert.Contains("centre", Assert.Throws<ArgumentException>(() => (b with { Center = new Vec2(double.NaN, 0) }).Checked()).Message);
        // A document refuses a bad one and keeps what it had.
        var doc = new Document();
        Assert.Throws<ArgumentException>(() => doc.SetBackdrops(BackdropSet.Empty.With(BackdropView.Top, b with { Width = -1 })));
        Assert.True(doc.Backdrops.IsEmpty);
    }

    [Fact]
    public void A_document_keeps_its_backdrops_in_copies_without_marking_itself_modified()
    {
        var doc = new Document();
        int version = doc.Version;
        var b = new Backdrop { Image = "side.png", Width = 2 };
        doc.SetBackdrops(doc.Backdrops.With(BackdropView.Side, b));
        Assert.False(doc.Modified);
        Assert.True(doc.Version > version);
        Assert.Equal(0, doc.Undo.Count);
        Assert.Same(b, doc.Copy().Backdrops.Side);
        Assert.Equal([(BackdropView.Side, b)], doc.Backdrops.All());
        Assert.Null(doc.Backdrops.With(BackdropView.Side, null).Side);
    }

    [Fact]
    public void The_picture_is_inverted_then_contrasted_then_brightened_keeping_alpha_and_made_once()
    {
        var image = new TextureImage(2, 1, [0, 128, 255, 255, 200, 50, 100, 40]);
        var b = new Backdrop { Image = "x.png" };
        Assert.Same(image, BackdropMapping.Adjusted(image, b));
        var inv = BackdropMapping.Adjusted(image, b with { Invert = true });
        Assert.Equal([255, 127, 0, 255, 55, 205, 155, 40], inv.Rgba);
        // Contrast 2 about mid grey, then +0.1: 0 → 0, 128 → ~154, 255 → 255 (clamped).
        var c = BackdropMapping.Adjusted(image, b with { Contrast = 2, Brightness = 0.1 });
        Assert.Equal(0, c.Rgba[0]);
        Assert.InRange(c.Rgba[1], 153, 155);
        Assert.Equal(255, c.Rgba[2]);
        Assert.Equal(40, c.Rgba[7]);
        // The same settings give the same picture back (so a GL texture of it lasts).
        Assert.Same(c, BackdropMapping.Adjusted(image, b with { Contrast = 2, Brightness = 0.1, Width = 9 }));
        var checker = TextureImage.Placeholder("missing");
        Assert.Same(checker, BackdropMapping.Adjusted(checker, b with { Invert = true }));
    }

    [Fact]
    public void A_missing_image_is_reported_by_view()
    {
        var set = BackdropSet.Empty.With(BackdropView.Front, new Backdrop { Image = "/nowhere/blueprint-" + Guid.NewGuid().ToString("N") + ".png" });
        var problem = Assert.Single(BackdropMapping.Problems(set, null));
        Assert.StartsWith("Front backdrop: Can't find the image blueprint-", problem);
        Assert.Empty(BackdropMapping.Problems(set with { Front = set.Front! with { Visible = false } }, null));
    }

    [Fact]
    public void A_setup_file_round_trips_with_its_images_relative_to_it()
    {
        using var t = new Temp();
        string images = Directory.CreateDirectory(t.File("blueprints")).FullName;
        var set = BackdropSet.Empty
            .With(BackdropView.Side, new Backdrop { Image = Path.Combine(images, "side.png"), Center = new Vec2(0.5, 1.25), Width = 1.2 })
            .With(BackdropView.Top, new Backdrop { Image = Path.Combine(images, "top.png"), Width = 3, Height = 2, Brightness = -0.2, Contrast = 1.5, Opacity = 0.8, Invert = true, Visible = false });
        string file = t.File("rocket.backdrops.json");
        BackdropFiles.Save(set, file);
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("blueprints/side.png", (string?)json["side"]!["image"]);
        Assert.Null(json["side"]!["opacity"]); // defaults are left out
        Assert.Null(json["front"]);
        Assert.Equal(set, BackdropFiles.Load(file));

        // Moved with its folder, it still finds its images.
        string moved = t.File("moved");
        Directory.CreateDirectory(moved);
        File.Copy(file, Path.Combine(moved, "setup.json"));
        Assert.Equal(Path.Combine(moved, "blueprints", "side.png"), BackdropFiles.Load(Path.Combine(moved, "setup.json")).Side!.Image);

        File.WriteAllText(t.File("bad.json"), """{"side": {"image": "s.png", "width": -3}}""");
        Assert.Contains("Side backdrop", Assert.Throws<FormatException>(() => BackdropFiles.Load(t.File("bad.json"))).Message);
        File.WriteAllText(t.File("junk.json"), "not json");
        Assert.Throws<FormatException>(() => BackdropFiles.Load(t.File("junk.json")));
    }

    [Fact]
    public void The_memory_keeps_each_objects_backdrops_by_its_path_and_forgets_empty_ones()
    {
        using var t = new Temp();
        var memory = new BackdropMemory(t.File("settings/backdrops.json"));
        string ship = t.File("objects/ship.lwo"), car = t.File("objects/car.lwo");
        Assert.Null(memory.Recall(ship));
        var shipSet = BackdropSet.Empty.With(BackdropView.Front, new Backdrop { Image = t.File("objects/front.png"), Width = 2 });
        var carSet = BackdropSet.Empty.With(BackdropView.Top, new Backdrop { Image = t.File("images/top.png"), Opacity = 0.3 });
        memory.Remember(ship, shipSet);
        memory.Remember(car, carSet);
        Assert.Equal(shipSet, memory.Recall(ship));
        Assert.Equal(carSet, new BackdropMemory(memory.File).Recall(car));
        // Images are kept relative to the object's folder.
        var kept = JsonNode.Parse(File.ReadAllText(memory.File))!["objects"]!.AsArray();
        Assert.Equal(["../images/top.png", "front.png"], kept.Select(o => (string)(o!["backdrops"]!["top"] ?? o["backdrops"]!["front"])!["image"]!));
        // Remembering an empty set forgets the object; a damaged file is passed over.
        memory.Remember(ship, BackdropSet.Empty);
        Assert.Null(memory.Recall(ship));
        Assert.NotNull(memory.Recall(car));
        File.WriteAllText(memory.File, "{ broken");
        Assert.Null(memory.Recall(car));
        memory.Remember(car, carSet);
        Assert.Equal(carSet, memory.Recall(car));
        // Only the most recent are kept.
        for (int i = 0; i < BackdropMemory.Capacity + 5; i++) memory.Remember(t.File($"o{i}.lwo"), carSet);
        Assert.Null(memory.Recall(car));
        Assert.NotNull(memory.Recall(t.File($"o{BackdropMemory.Capacity + 4}.lwo")));
    }
}
