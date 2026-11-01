using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ProxySentryCsv;

public sealed class AdaptiveWrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return Layout(availableSize.Width, false);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, true);
        return finalSize;
    }
    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
        var row = new List<(UIElement Child, double X, double Width)>();
        void ArrangeRow()
        {
            if (arrange)
                foreach (var item in row)
                    item.Child.Arrange(new Rect(item.X, y, item.Width, rowHeight));
            row.Clear();
        }
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                ArrangeRow();
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }
            row.Add((child, x, Math.Min(size.Width, width)));
            usedWidth = Math.Max(usedWidth, x + size.Width);
            x += size.Width + Spacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        ArrangeRow();
        return new Size(Math.Min(usedWidth, width), y + rowHeight);
    }
}
