using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Modeler.UI;

/// <summary>
/// Photo Match (Tools tab): photos matched to the model. Open a photo, then for each of six or more of the model's
/// points select it in a view (Points mode) and click where it shows in the photo; Solve finds the camera. With two
/// matched photos, Add Point takes a click in each on the same feature and adds the point it finds.
/// </summary>
public sealed class PhotoMatchWindow : Window
{
    private readonly ModelerSession _session;
    private readonly ComboBox _photos = new() { MinWidth = 160 };
    private readonly Canvas _canvas = new();
    private readonly Image _image = new() { Stretch = Stretch.None };
    private readonly TextBlock _status = new() { Foreground = Look.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    private (string Photo, Vec2 Pixel)? _firstClick;
    private bool _adding;

    /// <summary>What the window says, for tests.</summary>
    public string Status => _status.Text ?? "";

    public PhotoMatchWindow(ModelerSession session)
    {
        _session = session;
        Title = "Photo Match";
        Width = 900;
        Height = 700;
        Background = Look.PanelDark;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6) };
        bar.Children.Add(Look.MakeButton("Open Photo…", () => _ = OpenPhoto(), "Add a photo to match"));
        bar.Children.Add(_photos);
        bar.Children.Add(Look.MakeButton("Solve", Solve, "Find the camera from the marks (six or more)"));
        bar.Children.Add(Look.MakeButton("Clear Marks", ClearMarks, "Take this photo's marks away"));
        bar.Children.Add(Look.MakeButton("Add Point", () => { _adding = true; _firstClick = null; Say("Click the feature in this photo, then choose the other matched photo and click it there."); }, "A new point from the same feature in two matched photos"));
        _photos.SelectionChanged += (_, _) => ShowPhoto();
        _canvas.Children.Add(_image);
        _canvas.PointerPressed += Clicked;
        var scroll = new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_status);
        root.Children.Add(scroll);
        Content = root;
        Refill();
        Say("Open a photo. Then select one of the model's points (Points mode) and click where it shows; six or more, then Solve.");
    }

    private PhotoSetup? Current => _photos.SelectedItem is string n && _session.Document.Photos.TryGetValue(n, out var s) ? s : null;

    private void Refill(string? select = null)
    {
        var names = _session.Document.Photos.Keys.Order().ToList();
        _photos.ItemsSource = names;
        _photos.SelectedItem = select ?? names.FirstOrDefault();
    }

    private async Task OpenPhoto()
    {
        var path = await Dialogs.Open(this, "Open a photo to match", Dialogs.Images);
        if (path != null) AddPhoto(path);
    }

    /// <summary>Adds a photo by its file (named after it), and shows it.</summary>
    public void AddPhoto(string path)
    {
        var pic = TextureImages.Load(path);
        if (pic.IsPlaceholder) { Say(pic.Problem ?? "Can't read that picture."); return; }
        string name = Path.GetFileNameWithoutExtension(path);
        _session.Document.Photos[name] = new PhotoSetup { Image = path, Width = pic.Width, Height = pic.Height };
        Refill(name);
    }

    private void ShowPhoto()
    {
        foreach (var c in _canvas.Children.Where(c => c != _image).ToList()) _canvas.Children.Remove(c);
        if (Current is not { } photo) { _image.Source = null; return; }
        var pic = TextureImages.Load(photo.Image);
        _image.Source = pic.IsPlaceholder ? null : TextureEditor.Bitmap(pic);
        _canvas.Width = photo.Width;
        _canvas.Height = photo.Height;
        var cam = PhotoMatch.CameraOf(photo);
        foreach (var m in photo.Marks)
        {
            Dot(m.Pixel, Avalonia.Media.Brushes.Gold, 4);
            if (cam != null && m.Layer < _session.Document.Layers.Count && m.Point < _session.Document.Layers[m.Layer].Mesh.Points.Count
                && cam.Project(_session.Document.Layers[m.Layer].Mesh.Points[m.Point]) is { } q)
                Dot(q, Avalonia.Media.Brushes.DeepSkyBlue, 2.5);
        }
    }

    private void Dot(Vec2 at, IBrush brush, double r)
    {
        var e = new Avalonia.Controls.Shapes.Ellipse { Width = r * 2, Height = r * 2, Fill = brush, Stroke = Avalonia.Media.Brushes.Black, StrokeThickness = 1, IsHitTestVisible = false };
        Canvas.SetLeft(e, at.X - r);
        Canvas.SetTop(e, at.Y - r);
        _canvas.Children.Add(e);
    }

    private void Clicked(object? sender, PointerPressedEventArgs e)
    {
        if (Current is not { } photo || _photos.SelectedItem is not string name) return;
        var p = e.GetPosition(_canvas);
        Click(name, new Vec2(p.X, p.Y));
    }

    /// <summary>A click on a photo at a pixel: a mark for the selected point, or a step of Add Point.</summary>
    public void Click(string name, Vec2 pixel)
    {
        var doc = _session.Document;
        if (!doc.Photos.TryGetValue(name, out var photo)) return;
        if (_adding)
        {
            if (_firstClick is not { } first) { _firstClick = (name, pixel); Say($"Now the same feature in another matched photo."); return; }
            if (first.Photo == name) { _firstClick = (name, pixel); Say("That's the same photo: choose the other one, and click the feature there."); return; }
            var a = PhotoMatch.CameraOf(doc.Photos[first.Photo]);
            var b = PhotoMatch.CameraOf(photo);
            if (a == null || b == null) { Say("Both photos need solving first."); return; }
            try
            {
                var (found, gap) = PhotoMatch.Triangulate(a, first.Pixel, b, pixel);
                int layer = doc.CurrentLayer;
                doc.Edit("Point from Photos", () => doc.Layers[layer].Mesh.AddPoint(found), [layer]);
                Say($"Added a point at {found}; the rays pass {AVAModeler.Geometry.SelectionMeasure.Length(gap)} apart there.");
            }
            catch (ArgumentException ex) { Say(ex.Message); }
            _adding = false;
            _firstClick = null;
            return;
        }
        var chosen = doc.ForegroundLayers.SelectMany(l => doc.Selection.PointsIn(l).Select(p => (l, p))).ToList();
        if (doc.Selection.Mode != AVAModeler.Core.SelectionMode.Points || chosen.Count != 1) { Say("Select one point of the model (Points mode) first, then click where it shows."); return; }
        var (layerOf, point) = chosen[0];
        photo.Marks.RemoveAll(m => m.Layer == layerOf && m.Point == point);
        photo.Marks.Add(new PhotoMarkAt(layerOf, point, pixel));
        Say($"{photo.Marks.Count} mark{(photo.Marks.Count == 1 ? "" : "s")}{(photo.Marks.Count >= PhotoMatch.MinimumMarks ? ": Solve when ready." : $"; {PhotoMatch.MinimumMarks - photo.Marks.Count} more to go.")}");
        ShowPhoto();
    }

    /// <summary>Solves the shown photo's camera from its marks.</summary>
    public void Solve()
    {
        if (Current is not { } photo) return;
        try
        {
            var cam = PhotoMatch.Solve(_session.Document, photo);
            Say($"Camera at {cam.Position}, {cam.VerticalFov:0.#}° high; the marks miss by {cam.Error:0.##} pixels on average (gold marks, blue where the camera puts them).");
        }
        catch (ArgumentException ex) { Say(ex.Message); }
        ShowPhoto();
    }

    private void ClearMarks()
    {
        if (Current is not { } photo) return;
        photo.Marks.Clear();
        photo.Projection = null;
        ShowPhoto();
        Say("Marks cleared.");
    }

    private void Say(string text) => _status.Text = text;
}
