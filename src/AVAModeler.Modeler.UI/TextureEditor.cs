using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using AVAModeler.Core;
using AVAModeler.Rendering;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace AVAModeler.Modeler.UI;

/// <summary>
/// The texture panel for one of a surface's channels, as LightWave 5's "T" buttons opened it: the channel's layers
/// (add, remove), and for the chosen one its image, projection, axis, centre and size (with Automatic Sizing to fit
/// the surface), UV map, tiling across and up, wrap amounts, opacity and blending, and a picture of the image. It
/// changes the surface live; the surface editor that opened it makes the visit one undo step.
/// </summary>
public sealed class TextureEditor : Window
{
    private readonly ModelerSession _session;
    private readonly Surface _surface;
    private readonly TextureChannel _channel;
    private readonly Action _changed;
    private readonly ListBox _list = new() { Height = 110 };
    private readonly StackPanel _fields = new() { Spacing = 3 };
    private readonly TextBlock _imageNote = new() { Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Image _preview = new() { Width = 128, Height = 128, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private bool _loading;

    public TextureEditor(ModelerSession session, Surface surface, TextureChannel channel, Action changed)
    {
        _session = session;
        _surface = surface;
        _channel = channel;
        _changed = changed;
        Title = $"{surface.Name}: {Describe(channel)} texture";
        Width = 380;
        Height = 760;
        Background = Look.Panel;
        _list.SelectionChanged += (_, _) => ShowLayer();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Look.MakeButton("Add Layer", AddLayer, "A new image layer over the others"),
                Look.MakeButton("Remove Layer", RemoveLayer, "Take the chosen layer off"),
            },
        };
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(10),
                Spacing = 4,
                Children = { Label("Layers (applied top to bottom)"), _list, buttons, _fields },
            },
        };
        Refill();
    }

    /// <summary>A channel as LightWave named it.</summary>
    public static string Describe(TextureChannel c) => c == TextureChannel.Color ? "Colour" : c.ToString();

    /// <summary>The channel's layers, in order.</summary>
    public List<TextureLayer> Layers => _surface.TexturesOf(_channel).ToList();

    /// <summary>The layer being edited, if any.</summary>
    public TextureLayer? Layer => _list.SelectedIndex >= 0 && _list.SelectedIndex < Layers.Count ? Layers[_list.SelectedIndex] : null;

    private void Changed()
    {
        if (_loading) return;
        _changed();
        int i = _list.SelectedIndex;
        _loading = true;
        _list.ItemsSource = Layers.Select(Caption).ToList();
        _list.SelectedIndex = i;
        _loading = false;
        ShowImage();
    }

    private static string Caption(TextureLayer t, int i) =>
        $"{i + 1}. {(t.Procedural != null ? t.Procedural + " (not drawn)" : t.Projection + (t.Image.Length > 0 ? ": " + Path.GetFileName(t.Image) : ": (no image)"))}";

    private void Refill()
    {
        _loading = true;
        _list.ItemsSource = Layers.Select(Caption).ToList();
        _loading = false;
        _list.SelectedIndex = Layers.Count > 0 ? 0 : -1;
        if (Layers.Count == 0) ShowLayer();
    }

    /// <summary>
    /// Adds an image layer for the channel: planar along the axis the surface is thinnest along, sized to it; a UV
    /// layer on the current UV map if there is one.
    /// </summary>
    public void AddLayer()
    {
        var doc = _session.Document;
        var t = new TextureLayer { Channel = _channel, UVMap = doc.ActiveUVMap ?? "" };
        if (doc.ActiveUVMap != null) t.Projection = TextureProjection.UV;
        var bounds = SurfaceBounds();
        if (!bounds.IsEmpty)
        {
            var s = bounds.Size;
            t.Axis = s.Z <= s.X && s.Z <= s.Y ? Axis.Z : s.Y <= s.X ? Axis.Y : Axis.X;
        }
        (t.Center, t.Size) = TextureMapping.AutoSize(bounds);
        // After the channel's last layer, so layers of a channel stay together.
        int at = _surface.Textures.FindLastIndex(x => x.Channel == _channel);
        _surface.Textures.Insert(at + 1, t);
        Refill();
        _list.SelectedIndex = Layers.Count - 1;
        Changed();
    }

    private void RemoveLayer()
    {
        if (Layer is not { } t) return;
        _surface.Textures.Remove(t);
        Refill();
        Changed();
    }

    /// <summary>The bounds of the polygons with this surface, in every layer.</summary>
    private Box3 SurfaceBounds()
    {
        int index = _session.Document.Surfaces.IndexOf(_surface);
        return TextureMapping.SurfaceBounds(_session.Document.Layers.Select(l => l.Mesh), index);
    }

    /// <summary>LightWave's Automatic Sizing: the centre and size fitted to the surface's polygons.</summary>
    public void AutomaticSizing()
    {
        if (Layer is not { } t) return;
        (t.Center, t.Size) = TextureMapping.AutoSize(SurfaceBounds());
        ShowLayer();
        Changed();
    }

    /// <summary>Uses an image file for the chosen layer (stored relative to the object where it can be).</summary>
    public void SetImage(string path)
    {
        if (Layer is not { } t) return;
        t.Image = TextureImages.RelativePath(path, _session.Document.FilePath);
        ShowLayer();
        Changed();
    }

    private void ShowLayer()
    {
        if (_loading) return;
        _fields.Children.Clear();
        if (Layer is not { } t)
        {
            _fields.Children.Add(Note("No layers: Add Layer lays an image on this channel."));
            return;
        }
        _loading = true;
        if (t.Procedural != null)
            _fields.Children.Add(Note($"A procedural texture (\"{t.Procedural}\") from the file. It's kept and written back, but not drawn."));
        else
        {
            var image = new TextBox { Text = t.Image };
            image.LostFocus += (_, _) => { if (image.Text != t.Image) { t.Image = image.Text ?? ""; Changed(); } };
            var browse = Look.MakeButton("Browse…", () => _ = Browse(), "Choose an image file");
            _fields.Children.Add(Label("Image"));
            _fields.Children.Add(new DockPanel { Children = { Docked(browse, Dock.Right), image } });
            _fields.Children.Add(_imageNote);
            _fields.Children.Add(_preview);
        }
        _fields.Children.Add(Choice("Type", t.Projection, v => t.Projection = v));
        var maps = _session.Document.UVMapNames();
        if (t.UVMap.Length > 0 && !maps.Contains(t.UVMap)) maps.Add(t.UVMap);
        var uvMap = new ComboBox { ItemsSource = maps, SelectedItem = t.UVMap, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = maps.Count > 0 };
        uvMap.SelectionChanged += (_, _) => { if (uvMap.SelectedItem is string m) { t.UVMap = m; Changed(); } };
        _fields.Children.Add(Label("UV map (for the UV type)"));
        _fields.Children.Add(uvMap);
        _fields.Children.Add(Choice("Texture axis", t.Axis, v => t.Axis = v));
        _fields.Children.Add(Vector("Texture center", () => t.Center, v => t.Center = v));
        _fields.Children.Add(Vector("Texture size", () => t.Size, v => t.Size = v));
        _fields.Children.Add(Look.MakeButton("Automatic Sizing", AutomaticSizing, "Fit the centre and size to the surface's polygons"));
        _fields.Children.Add(Choice("Width tile", t.WidthTiling, v => t.WidthTiling = v));
        _fields.Children.Add(Choice("Height tile", t.HeightTiling, v => t.HeightTiling = v));
        _fields.Children.Add(Number("Width wrap (cylindrical, spherical)", () => t.WidthWrap, v => t.WidthWrap = v));
        _fields.Children.Add(Number("Height wrap (spherical)", () => t.HeightWrap, v => t.HeightWrap = v));
        _fields.Children.Add(Number("Opacity %", () => t.Opacity * 100, v => t.Opacity = Math.Clamp(v, 0, 100) / 100));
        _fields.Children.Add(Choice("Blending", t.Blend, v => t.Blend = v));
        if (_channel == TextureChannel.Bump) _fields.Children.Add(Number("Bump amplitude", () => t.Amplitude, v => t.Amplitude = v));
        _fields.Children.Add(Check("Pixel blending", t.PixelBlending, v => t.PixelBlending = v));
        _fields.Children.Add(Check("Negative image", t.Negative, v => t.Negative = v));
        _fields.Children.Add(Check("World coordinates", t.WorldCoordinates, v => t.WorldCoordinates = v));
        _loading = false;
        ShowImage();
    }

    /// <summary>The chosen layer's image: a picture of it, its size, or why it can't be had.</summary>
    private void ShowImage()
    {
        if (Layer is not { IsImage: true } t) return;
        if (t.Image.Length == 0)
        {
            _imageNote.Text = "No image yet.";
            _preview.Source = null;
            return;
        }
        var img = TextureImages.Load(t.Image, _session.Document.ImageBase);
        _imageNote.Text = img.IsPlaceholder ? img.Problem : $"{img.Width} × {img.Height}";
        _imageNote.Foreground = img.IsPlaceholder ? Look.ButtonOn : Look.Text;
        _preview.Source = Bitmap(img);
    }

    /// <summary>A texture image as a bitmap to show.</summary>
    public static WriteableBitmap Bitmap(TextureImage img)
    {
        var bmp = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        for (int y = 0; y < img.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(img.Rgba, y * img.Width * 4, fb.Address + y * fb.RowBytes, img.Width * 4);
        return bmp;
    }

    private async Task Browse()
    {
        var path = await Dialogs.Open(this, "Choose an image", Dialogs.Images);
        if (path != null) SetImage(path);
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Look.Text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private static TextBlock Note(string text) => new() { Text = text, Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };

    private static Control Docked(Control c, Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }

    private Control Choice<T>(string label, T value, Action<T> set) where T : struct, Enum
    {
        var combo = new ComboBox { ItemsSource = Enum.GetNames<T>(), SelectedItem = value.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedItem is not string s) return;
            set(Enum.Parse<T>(s));
            Changed();
        };
        return new StackPanel { Children = { Label(label), combo } };
    }

    private Control Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Foreground = Look.Text };
        box.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            set(box.IsChecked == true);
            Changed();
        };
        return box;
    }

    private TextBox Box(Func<double> get, Action<double> set)
    {
        string Format(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        var box = new TextBox { Text = Format(get()), FontSize = 11, Margin = new Thickness(1, 0), MinWidth = 0 };
        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v != get())
            {
                set(v);
                Changed();
            }
            box.Text = Format(get());
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };
        return box;
    }

    private Control Number(string label, Func<double> get, Action<double> set) => new StackPanel { Children = { Label(label), Box(get, set) } };

    private Control Vector(string label, Func<Vec3> get, Action<Vec3> set)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        for (int i = 0; i < 3; i++)
        {
            int axis = i;
            var box = Box(() => get()[axis], v => set(axis switch { 0 => get() with { X = v }, 1 => get() with { Y = v }, _ => get() with { Z = v } }));
            Grid.SetColumn(box, i);
            grid.Children.Add(box);
        }
        return new StackPanel { Children = { Label(label + " (X, Y, Z)"), grid } };
    }
}
