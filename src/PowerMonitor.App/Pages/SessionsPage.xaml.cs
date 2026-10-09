using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PowerMonitor.App.Controls;
using PowerMonitor.Core;

namespace PowerMonitor.App.Pages;

public sealed record SessionItem(DischargeSession Session, string Title, string Detail, string Projected);

public sealed partial class SessionsPage : Page
{
    public SessionsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var sessions = Data.Use(s => s.Sessions(DateTimeOffset.UtcNow.AddDays(-120)), new List<DischargeSession>());
        List.ItemsSource = sessions.Select(ToItem).ToList();

        var real = sessions.Where(x => x.ActiveHours >= 0.25).ToList();
        if (real.Count == 0)
            Summary.Text = "还没有超过 15 分钟的放电记录。拔掉电源用一会儿，这里就会出现每一次放电的记录。";
        else
        {
            double hours = real.Sum(x => x.ActiveHours), wh = real.Sum(x => x.AverageW * x.ActiveHours);
            double avg = wh / hours, full = real[0].FullWh;
            Summary.Text = $"最近 {real.Count} 次放电，共用电池 {AppEnergyList.FormatHours(hours)}，平均 {avg:0.0} W。" +
                           $"按这个平均功耗，满充 {full:0.0} Wh 大约能用 {Series.Duration(full / avg)}。";
        }
        if (List.Items.Count > 0) List.SelectedIndex = 0;
    }

    static SessionItem ToItem(DischargeSession s)
    {
        var start = s.Start.ToLocalTime();
        string title = $"{start:M月d日 HH:mm}" + (s.Ongoing ? " · 进行中" : $" – {s.End.ToLocalTime():HH:mm}");
        string detail = $"{s.StartPercent:0}% → {s.EndPercent:0}% · 使用 {AppEnergyList.FormatHours(s.ActiveHours)} · 平均 {s.AverageW:0.0} W";
        return new SessionItem(s, title, detail, Series.Duration(s.ProjectedFullHours));
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not SessionItem item) return;
        var s = item.Session;
        var (rows, apps) = Data.Use(st => (st.Samples(s.Start, s.End), st.Apps(s.Start, s.End, batteryOnly: true, limit: 15)),
            (new List<SampleRow>(), new List<AppEnergy>()));

        DetailTitle.Text = $"电量 · 用掉 {s.UsedWh:0.0} Wh（{s.StartPercent - s.EndPercent:0}%）";
        var groups = Series.Bucket(rows, 300);
        ChargeChart.SetData(groups.Select(g => g[^1].Time).ToArray(),
            [new ChartSeries("电量", "SeriesCpu", groups.Select(g => (double?)(100.0 * g[^1].CapacityMwh / Math.Max(1, s.FullWh * 1000))).ToArray())]);
        var (times, series) = Series.PowerBreakdown(rows, 300);
        PowerChart.SetData(times, series);
        AppList.SetData(apps, Math.Max(s.ActiveHours, 1.0 / 60));
    }
}
