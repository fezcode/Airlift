using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Airlift.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace Airlift.Desktop;
internal static class Ui
{
    // Width kept clear for the scrollbar that floats over scrolled content.
    public const double ScrollGutter = 18;
    // Resolved per call so a theme change is picked up without rebuilding these.
    public static IBrush Lime => Themes.Brush(p => p.Accent);
    public static IBrush Muted => Themes.Brush(p => p.Muted);
    public static IBrush Line => Themes.Brush(p => p.Line);
    private static readonly Dictionary<string, Bitmap> Icons = [];
    public static TextBlock Text(string text, double size = 13, Func<Palette, string>? role = null, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = Themes.Brush(role ?? (p => p.Text)), FontWeight = weight ?? FontWeight.Normal,
        FontFamily = new FontFamily(size >= 18 ? "avares://Airlift/Assets/Fonts#Manrope" : "avares://Airlift/Assets/Fonts#DM Sans"),
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };
    public static TextBlock MutedText(string text, double size = 12) => Text(text, size, p => p.Muted);
    public static StackPanel Stack(double spacing = 12, params Control[] children)
    {
        var panel = new StackPanel { Spacing = spacing }; foreach (var child in children) panel.Children.Add(child); return panel;
    }
    public static StackPanel Row(double spacing = 10, params Control[] children)
    {
        var panel = Stack(spacing, children); panel.Orientation = Orientation.Horizontal; panel.VerticalAlignment = VerticalAlignment.Center; return panel;
    }
    public static Border Card(Control child, double padding = 22, Func<Palette, string>? background = null) => new()
    { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = Line, Background = Themes.Brush(background ?? (p => p.Card)) };
    public static Button Button(string text, Action action, string style = "", bool enabled = true)
    {
        var button = new Button { Content = text, IsEnabled = enabled }; if (style.Length > 0) button.Classes.Add(style);
        if (style == "nav") button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Click += (_, _) => action(); return button;
    }
    public static Button AsyncButton(string text, Func<Task> action, string style = "", bool enabled = true) => Button(text, async () =>
    {
        try { await action(); } catch (Exception e) { System.Diagnostics.Trace.WriteLine(e); }
    }, style, enabled);
    public static Grid Between(Control left, Control right)
    {
        right.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 }; grid.Children.Add(left); grid.Children.Add(right); Grid.SetColumn(right, 1); return grid;
    }
    public static Control Icon(CatalogApp app, double size = 48)
    {
        if (!Icons.TryGetValue(app.Id, out var bitmap))
        {
            var uri = new Uri($"avares://Airlift/Assets/Icons/{app.Id}.png");
            if (AssetLoader.Exists(uri)) { using var stream = AssetLoader.Open(uri); bitmap = new Bitmap(stream); Icons[app.Id] = bitmap; }
        }
        Control content = bitmap != null ? new Image { Source = bitmap, Width = size * .72, Height = size * .72 } : Text(app.Name[..1], size * .5);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 4), Background = Themes.Brush(p => p.Raised), BorderBrush = Brush.Parse(app.Color), BorderThickness = new Thickness(.4), Child = content };
    }
    // A stroked vector glyph. Callers bind its Stroke so it follows the host control's Foreground.
    public static Path Glyph(string data, double size = 10, double thickness = 1.1) => new()
    {
        Data = Geometry.Parse(data), Width = size, Height = size, StrokeThickness = thickness,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    public static Control Separator() => new Border { Height = 1, Background = Line, Margin = new Thickness(0, 6) };
    public static string Bytes(long bytes) => $"{bytes / 1048576d:F1} MB";
}
