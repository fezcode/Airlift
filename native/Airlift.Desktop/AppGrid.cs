using Avalonia;
using Avalonia.Controls;

namespace Airlift.Desktop;

/// <summary>Sizes cards from the actual viewport, including its scrollbar and padding.</summary>
public sealed class AppGrid : Panel
{
    private const double Gap = 14;
    private const double MinimumCardWidth = 300;
    private static int Columns(double width) => Math.Clamp((int)((width + Gap) / (MinimumCardWidth + Gap)), 1, 3);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MinimumCardWidth;
        var columns = Columns(width);
        var cardWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var height = 0d;
        for (var row = 0; row < Children.Count; row += columns)
        {
            var rowHeight = 0d;
            for (var i = row; i < Math.Min(row + columns, Children.Count); i++)
            {
                Children[i].Measure(new Size(cardWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            }
            height += rowHeight + (row == 0 ? 0 : Gap);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var width = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        var y = 0d;
        for (var row = 0; row < Children.Count; row += columns)
        {
            var count = Math.Min(columns, Children.Count - row);
            var height = Enumerable.Range(row, count).Max(i => Children[i].DesiredSize.Height);
            for (var col = 0; col < count; col++)
                Children[row + col].Arrange(new Rect(col * (width + Gap), y, width, height));
            y += height + Gap;
        }
        return finalSize;
    }
}
