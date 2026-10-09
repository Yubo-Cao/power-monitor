using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using PowerMonitor.Core;

namespace PowerMonitor.App.Controls;

/// <summary>
/// Ranked apps: name, a CPU / iGPU / dGPU bar scaled to the top app, energy and the
/// average draw over the window. Hover a row for the exact split.
/// </summary>
public sealed class AppEnergyList : StackPanel
{
    IReadOnlyList<AppEnergy> _apps = [];
    double _hours = 1;

    public AppEnergyList()
    {
        Spacing = 2;
        ActualThemeChanged += (_, _) => Rebuild();
    }

    /// <param name="hours">Length of the window, for the average draw.</param>
    public void SetData(IReadOnlyList<AppEnergy> apps, double hours)
    {
        _apps = apps;
        _hours = Math.Max(1e-6, hours);
        Rebuild();
    }

    void Rebuild()
    {
        Children.Clear();
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var body = (Style)Application.Current.Resources["BodyTextBlockStyle"];
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        Children.Add(Row(
            new TextBlock { Text = "应用", Style = caption, Foreground = secondary },
            new TextBlock { Text = "能耗构成", Style = caption, Foreground = secondary },
            new TextBlock { Text = "能耗", Style = caption, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Right },
            new TextBlock { Text = "平均功耗", Style = caption, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Right },
            new TextBlock { Text = "CPU 时间", Style = caption, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Right }));

        if (_apps.Count == 0)
        {
            Children.Add(new TextBlock { Text = "这段时间没有记录", Style = caption, Foreground = secondary, Margin = new Thickness(0, 8, 0, 0) });
            return;
        }

        double top = Math.Max(1e-9, _apps.Max(a => a.TotalWh));
        foreach (var a in _apps)
        {
            var bar = new Grid { Height = 10, VerticalAlignment = VerticalAlignment.Center };
            double rest = top - a.TotalWh;
            foreach (var (wh, key) in new[] { (a.CpuWh, "SeriesCpu"), (a.IGpuWh, "SeriesIGpu"), (a.DGpuWh, "SeriesDGpu") })
            {
                if (wh <= 0) continue;
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wh, GridUnitType.Star) });
                var r = new Rectangle { Fill = ChartBrushes.Get(key, ActualTheme), RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 2, 0) };
                Grid.SetColumn(r, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(r);
            }
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(rest, top * 1e-6), GridUnitType.Star) });

            var row = Row(
                new TextBlock { Text = a.Name, Style = body, TextTrimming = TextTrimming.CharacterEllipsis },
                bar,
                new TextBlock { Text = FormatWh(a.TotalWh), Style = body, HorizontalAlignment = HorizontalAlignment.Right },
                new TextBlock { Text = $"{a.TotalWh / _hours:0.00} W", Style = body, HorizontalAlignment = HorizontalAlignment.Right },
                new TextBlock { Text = FormatHours(a.CpuHours), Style = body, HorizontalAlignment = HorizontalAlignment.Right, Foreground = secondary });
            row.Padding = new Thickness(0, 5, 0, 5);
            ToolTipService.SetToolTip(row,
                $"{a.Name}\nCPU  {FormatWh(a.CpuWh)}\n核显  {FormatWh(a.IGpuWh)}\n独显  {FormatWh(a.DGpuWh)}\n" +
                $"CPU 时间 {FormatHours(a.CpuHours)} · GPU 时间 {FormatHours(a.GpuHours)}");
            Children.Add(row);
        }
    }

    static Grid Row(params FrameworkElement[] cells)
    {
        var g = new Grid { ColumnSpacing = 16 };
        foreach (var w in new[] { new GridLength(200), new GridLength(1, GridUnitType.Star), new GridLength(80), new GridLength(80), new GridLength(80) })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        for (int i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]); }
        return g;
    }

    public static string FormatWh(double wh) => wh >= 1 ? $"{wh:0.0} Wh" : $"{wh * 1000:0} mWh";

    public static string FormatHours(double h) =>
        h >= 1 ? $"{(int)h} 小时 {(int)(h * 60 % 60)} 分" : h * 60 >= 1 ? $"{h * 60:0} 分" : $"{h * 3600:0} 秒";
}
