using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PowerMonitor.App.Controls;
using PowerMonitor.Core;

namespace PowerMonitor.App.Pages;

public sealed partial class HealthPage : Page
{
    public HealthPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        // Imported battery-watch rows have no design capacity or cycle count.
        var points = Data.Use(s => s.Health(), new List<HealthPoint>());
        var withDesign = points.Where(p => p.DesignMwh > 0).ToList();
        var last = withDesign.LastOrDefault() ?? points.LastOrDefault();
        if (last is null) return;

        FullValue.Text = $"{last.FullMwh / 1000.0:0.0} Wh";
        if (last.DesignMwh > 0)
        {
            double health = (double)last.FullMwh / last.DesignMwh;
            HealthValue.Text = $"{health:0.0%}";
            DesignValue.Text = $"{last.DesignMwh / 1000.0:0.0} Wh";
            FullNote.Text = $"比出厂少 {(last.DesignMwh - last.FullMwh) / 1000.0:0.0} Wh";
        }
        if (last.CycleCount > 0)
        {
            CycleValue.Text = $"{last.CycleCount}";
            var first = withDesign.FirstOrDefault(p => p.CycleCount > 0);
            if (first is not null && first.Day < last.Day)
            {
                double perMonth = (last.CycleCount - first.CycleCount) / Math.Max(1.0, last.Day.DayNumber - first.Day.DayNumber) * 30;
                CycleNote.Text = $"近期约每月 {perMonth:0.0} 次";
            }
            else CycleNote.Text = "一次循环 = 累计放掉一整块电池的电量";
        }

        var times = points.Select(p => new DateTimeOffset(p.Day.ToDateTime(new TimeOnly(12, 0)))).ToArray();
        double max = Math.Max(points.Max(p => p.FullMwh), points.Max(p => p.DesignMwh)) / 1000.0;
        CapacityChart.FixedMax = Math.Ceiling(max / 10) * 10;
        CapacityChart.SetData(times,
        [
            new ChartSeries("满充容量", "SeriesCpu", points.Select(p => (double?)(p.FullMwh / 1000.0)).ToArray()),
            new ChartSeries("设计容量", "ChartMuted", points.Select(p => p.DesignMwh > 0 ? (double?)(p.DesignMwh / 1000.0) : null).ToArray()),
        ]);
    }
}
