using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PowerMonitor.App.Controls;
using PowerMonitor.Core;

namespace PowerMonitor.App.Pages;

public sealed partial class AppsPage : Page
{
    public AppsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();
    void Range_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e) { if (IsLoaded) Refresh(); }
    void BatteryOnly_Toggled(object sender, RoutedEventArgs e) { if (IsLoaded) Refresh(); }

    void Refresh()
    {
        double hours = Range.SelectedItem?.Tag is string t ? double.Parse(t) : 24;
        bool batteryOnly = BatteryOnly.IsOn;
        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-hours);
        var (apps, rows) = Data.Use(s => (s.Apps(from, to, batteryOnly, limit: 40), s.Samples(from, to)),
            (new List<AppEnergy>(), new List<SampleRow>()));

        // Average draw is over the time actually covered (battery time when filtered).
        double covered = Series.CoveredHours(rows, batteryOnly);
        AppList.SetData(apps, Math.Max(covered, 1.0 / 60));

        double total = apps.Sum(a => a.TotalWh);
        TotalValue.Text = AppEnergyList.FormatWh(total);
        TotalNote.Text = covered > 0 ? $"覆盖 {AppEnergyList.FormatHours(covered)}{(batteryOnly ? "电池使用" : "")}，平均 {total / covered:0.0} W" : "这段时间没有记录";

        var top = apps.FirstOrDefault();
        TopValue.Text = top?.Name ?? "—";
        TopNote.Text = top is null ? "" : $"{AppEnergyList.FormatWh(top.TotalWh)}，占应用能耗的 {top.TotalWh / Math.Max(total, 1e-9):0%}";

        var dgpuRows = rows.Where(r => !batteryOnly || !r.OnAc).ToList();
        double awake = dgpuRows.Where(r => r.DGpuDState == 1).Sum(r => Math.Min(r.IntervalSec, 120)) / 3600;
        DGpuValue.Text = awake > 0 ? $"醒了 {AppEnergyList.FormatHours(awake)}" : "一直在休眠";
        var users = apps.Where(a => a.DGpuWh > 0).OrderByDescending(a => a.DGpuWh).Take(3).Select(a => a.Name).ToList();
        double dgpuWh = apps.Sum(a => a.DGpuWh);
        DGpuNote.Text = awake > 0
            ? (dgpuWh > 0 ? $"约 {AppEnergyList.FormatWh(dgpuWh)}" : "能耗未记录") +
              (users.Count > 0 ? "，主要是 " + string.Join("、", users) : "") + "。独显醒着大约多耗 10–15 W。"
            : "独显休眠（D3）时几乎不耗电。";
    }
}
