using AVAModeler.Core;
using AVAModeler.Rendering;

namespace AVAModeler.Layout.Tests;

public class ProceduralViewTests
{
    [Fact]
    public void Layouts_view_shows_colour_procedurals_moving_with_their_object()
    {
        // A 2 m square facing the camera, luminous grey, with a red checkerboard of one-metre squares.
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, new Vec3(-1, -1, 0), new Vec3(-1, 1, 0), new Vec3(1, 1, 0), new Vec3(1, -1, 0));
        var checks = TextureLayer.NewProcedural(ProceduralKind.Checkerboard);
        checks.TextureColor = new Rgb(255, 0, 0);
        doc.Surfaces[0].Color = new Rgb(128, 128, 128);
        doc.Surfaces[0].Luminosity = 1;
        doc.Surfaces[0].Textures.Add(checks);
        var s = new LayoutSession();
        var obj = s.Scene.AddObject("Board", doc);
        obj.SetPosition(1, new Vec3(5.5, 0, 0));
        var view = SceneDrawing.Build(s.Scene.Evaluate(1), s.Scene.Items, shaded: true);
        view.ShowGrid = false;
        view.BackfaceTint = false;
        view.Style = DisplayStyle.Shaded;
        var cam = new ViewCamera(ViewKind.Perspective) { Heading = 0, Pitch = 0, Distance = 2.5, Center = new Vec3(5.5, 0, 0) };
        cam.Resize(200, 200);
        var r = new SoftwareRenderer();
        r.Render(view, cam);
        (int, int, int) At(int x, int y) { uint p = r.Pixels[y * 200 + x]; return ((int)(p >> 16) & 0xFF, (int)(p >> 8) & 0xFF, (int)p & 0xFF); }
        // In the object's own coordinates, so the squares are where they are on the object, 5.5 m away or not.
        Assert.Equal((255, 0, 0), At(70, 70));
        Assert.Equal((128, 128, 128), At(130, 70));
        Assert.Equal((255, 0, 0), At(130, 130));
        // In world coordinates the board slides under it: x from 4.5 to 6.5 crosses the squares half a square off, so
        // the top row is red from x = 5 to 6, either side of the board's middle.
        checks.WorldCoordinates = true;
        view = SceneDrawing.Build(s.Scene.Evaluate(1), s.Scene.Items, shaded: true);
        view.ShowGrid = false;
        view.Style = DisplayStyle.Shaded;
        r.Render(view, cam);
        Assert.Equal((255, 0, 0), At(70, 70));
        Assert.Equal((255, 0, 0), At(130, 70));
        Assert.Equal((128, 128, 128), At(130, 130));
    }
}
