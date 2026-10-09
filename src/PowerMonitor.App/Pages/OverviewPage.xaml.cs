using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PowerMonitor.App.Controls;
using PowerMonitor.Core;

namespace PowerMonitor.App.Pages;

public sealed partial class OverviewPage : Page
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };

    public OverviewPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { Refresh(); _timer.Start(); }
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    void Range_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e) { if (IsLoaded) Refresh(); }

    double RangeHours => Range.SelectedItem?.Tag is string t ? double.Parse(t) : 1;

    void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        double hours = RangeHours;
        var (rows, recent, apps, session) = Data.Use(s => (
            s.Samples(now.AddHours(-hours), now),
            s.Samples(now.AddMinutes(-30), now),
            s.Apps(now.AddHours(-hours), now, batteryOnly: false, limit: 12),
            s.Sessions(now.AddDays(-2)).FirstOrDefault(x => x.Ongoing)),
            (new List<SampleRow>(), new List<SampleRow>(), new List<AppEnergy>(), (DischargeSession?)null));

        var latest = recent.LastOrDefault();
        UpdateTiles(latest, recent, session);

        var (times, series) = Series.PowerBreakdown(rows);
        PowerChart.SetData(times, series);
        AppsTitle.Text = $"耗电最多的应用 · 近 {(hours < 1 ? $"{hours * 60:0} 分钟" : $"{hours:0} 小时")}";
        AppList.SetData(apps, Math.Max(Series.CoveredHours(rows, batteryOnly: false), 1.0 / 60));
    }

    void UpdateTiles(SampleRow? latest, List<SampleRow> recent, DischargeSession? session)
    {
        if (latest is null)
        {
            DrawValue.Text = RemainValue.Text = ChargeValue.Text = FullValue.Text = "—";
            DrawNote.Text = "采集器还没有写入数据";
            return;
        }

        ChargeValue.Text = $"{latest.Percent:0}%";
        ChargeBar.Value = latest.Percent;
        ChargeNote.Text = $"{latest.CapacityMwh / 1000.0:0.0} / {latest.FullMwh / 1000.0:0.0} Wh";

        var last2 = recent.Where(r => r.Time > latest.Time.AddMinutes(-2)).ToList();
        var last5 = recent.Where(r => r.Time > latest.Time.AddMinutes(-5)).ToList();
        double? now2 = Series.AverageDrainW(last2), now5 = Series.AverageDrainW(last5), avg30 = Series.AverageDrainW(recent);

        if (!latest.OnAc && now2 is double w)
        {
            DrawLabel.Text = "当前功耗";
            DrawValue.Text = $"{w:0.0} W";
            DrawNote.Text = $"CPU {latest.PackageMw / 1000.0:0.0} W · 屏幕与其他 {(latest.RestMw ?? 0) / 1000.0:0.0} W" +
                            (latest.DGpuDState == 1 ? " · 独显醒着" : "");
            RemainLabel.Text = "预计剩余";
            double drain = now5 ?? w;
            RemainValue.Text = Series.Duration(latest.CapacityMwh / 1000.0 / drain);
            RemainNote.Text = $"按近 5 分钟平均 {drain:0.0} W" +
                (session is { AverageW: > 0 } ? $"；按本次放电平均 {session.AverageW:0.0} W 为 {Series.Duration(latest.CapacityMwh / 1000.0 / session.AverageW)}" : "");
        }
        else
        {
            DrawLabel.Text = latest.Charging ? "正在充电" : "已接通电源";
            DrawValue.Text = latest.Charging ? $"+{latest.RateMw / 1000.0:0.0} W" : $"{latest.PackageMw / 1000.0:0.0} W";
            DrawNote.Text = latest.Charging ? $"CPU 封装 {latest.PackageMw / 1000.0:0.0} W；插电时测不到整机功耗" : "CPU 封装功耗；插电时测不到整机功耗";
            RemainLabel.Text = latest.Charging ? "预计充满" : "预计剩余";
            RemainValue.Text = latest.Charging && latest.RateMw > 0
                ? Series.Duration((latest.FullMwh - latest.CapacityMwh) / (double)latest.RateMw)
                : "插电中";
            RemainNote.Text = latest.Charging ? $"按当前 {latest.RateMw / 1000.0:0.0} W 充电速度" : "";
        }

        // Full-charge runtime: last 30 min of battery use, else the most recent session.
        double? basis = avg30 ?? session?.AverageW;
        string basisText = avg30 is not null ? "近 30 分钟" : "本次放电";
        if (basis is null)
        {
            var last = Data.Use(s => s.Sessions(DateTimeOffset.UtcNow.AddDays(-30)).FirstOrDefault(), null);
            basis = last?.AverageW;
            basisText = last is null ? "" : $"上次放电（{last.Start.ToLocalTime():M/d HH:mm}）";
        }
        FullValue.Text = basis is double b and > 0 ? Series.Duration(latest.FullMwh / 1000.0 / b) : "—";
        FullNote.Text = basis is double b2 and > 0 ? $"按{basisText}平均 {b2:0.0} W，满充 {latest.FullMwh / 1000.0:0.0} Wh" : "还没有电池使用记录";
    }
}
