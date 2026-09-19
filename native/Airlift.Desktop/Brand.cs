using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform;
using Path = Avalonia.Controls.Shapes.Path;

namespace Airlift.Desktop;

internal static class Brand
{
    // The SVG is shared by the vector UI, desktop icon generator and web preview.
    public static Control Mark(double size)
    {
        using var stream = AssetLoader.Open(new Uri("avares://Airlift/Assets/airlift.svg"));
        var svg = XDocument.Load(stream); XNamespace ns = "http://www.w3.org/2000/svg";
        var tile = svg.Root!.Element(ns + "rect")!;
        var canvas = new Canvas { Width = 64, Height = 64 };
        var background = new Border { Width = 62, Height = 62, CornerRadius = new CornerRadius(16),
            Background = Brush.Parse(tile.Attribute("fill")!.Value), BorderBrush = Brush.Parse(tile.Attribute("stroke")!.Value), BorderThickness = new Thickness(1) };
        Canvas.SetLeft(background, 1); Canvas.SetTop(background, 1); canvas.Children.Add(background);
        foreach (var polygon in svg.Root.Elements(ns + "polygon"))
            canvas.Children.Add(new Path { Data = Geometry.Parse("M " + polygon.Attribute("points")!.Value.Replace(" ", " L ") + " Z"), Fill = Brush.Parse(polygon.Attribute("fill")!.Value) });
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }
    public static Button Navigation(string name, Action action)
    {
        var data = name switch
        {
            "Discover" => "M9 1 L11.6 6.4 L17 9 L11.6 11.6 L9 17 L6.4 11.6 L1 9 L6.4 6.4 Z",
            "My library" => "M2 3 L2 16 M6 3 L6 16 M10 3 L10 16 M13 3 L17 15",
            "Updates" => "M15 6 A7 7 0 1 0 16 11 M15 1 L15 6 L10 6",
            "Downloads" => "M9 1 L9 12 M5 8 L9 12 L13 8 M2 12 L2 16 L16 16 L16 12",
            "Collections" => "M2 2 L7 2 L7 7 L2 7 Z M11 2 L16 2 L16 7 L11 7 Z M2 11 L7 11 L7 16 L2 16 Z M11 11 L16 11 L16 16 L11 16 Z",
            "Sources" => "M7 12 L5 14 A3.5 3.5 0 0 1 0 9 L4 5 A3.5 3.5 0 0 1 9 5 M9 6 L11 4 A3.5 3.5 0 0 1 16 9 L12 13 A3.5 3.5 0 0 1 7 13 M5 11 L11 5",
            _ => "M3 1 L3 17 M9 1 L9 17 M15 1 L15 17 M0 5 L6 5 M6 12 L12 12 M12 7 L18 7"
        };
        var button = Ui.Button("", action, "nav");
        var icon = new Path { Data = Geometry.Parse(data), StrokeThickness = 1.4, Width = 18, Height = 18, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
        icon.Bind(Shape.StrokeProperty, new Avalonia.Data.Binding("Foreground") { Source = button });
        var label = new TextBlock { Text = name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        button.Content = Ui.Row(12, icon, label);
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        return button;
    }
}
