using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PowerMonitor.App.Controls;

/// <summary>Left-to-right panel that wraps to a new line (chart legends).</summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, line = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            var d = child.DesiredSize;
            if (x > 0 && x + d.Width > available.Width) { y += line + VerticalSpacing; x = 0; line = 0; }
            x += d.Width + HorizontalSpacing;
            line = Math.Max(line, d.Height);
            width = Math.Max(width, x - HorizontalSpacing);
        }
        return new Size(double.IsInfinity(available.Width) ? width : Math.Min(width, available.Width), y + line);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;
            if (x > 0 && x + d.Width > final.Width) { y += line + VerticalSpacing; x = 0; line = 0; }
            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width + HorizontalSpacing;
            line = Math.Max(line, d.Height);
        }
        return final;
    }
}
