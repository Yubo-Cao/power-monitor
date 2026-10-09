using PowerMonitor.App.Controls;
using PowerMonitor.Core;

namespace PowerMonitor.App;

static class Series
{
    /// <summary>
    /// Power breakdown of samples, averaged into at most <paramref name="buckets"/> points
    /// (a bucket never spans a gap, so sleep stays visible as a gap).
    /// On AC the whole-system draw is unknown, so only CPU and iGPU are shown there.
    /// </summary>
    public static (DateTimeOffset[] Times, List<ChartSeries> Series) PowerBreakdown(List<SampleRow> rows, int buckets = 360)
    {
        var groups = Bucket(rows, buckets);
        var times = groups.Select(g => g[^1].Time).ToArray();
        double?[] Avg(Func<SampleRow, double?> f) => groups.Select(g =>
        {
            var vals = g.Select(f).Where(v => v is not null).Select(v => v!.Value).ToList();
            return vals.Count == 0 ? (double?)null : vals.Average();
        }).ToArray();
        // Imported battery-watch rows (PackageMw 0 with a system draw) have no split.
        static bool Split(SampleRow r) => r.PackageMw > 0 || r.SystemMw is null;
        var series = new List<ChartSeries>
        {
            new("CPU", "SeriesCpu", Avg(r => Split(r) ? (r.PackageMw - r.IGpuMw) / 1000.0 : null)),
            new("核显", "SeriesIGpu", Avg(r => Split(r) ? r.IGpuMw / 1000.0 : null)),
            new("独显（估算）", "SeriesDGpu", Avg(r => r.DGpuMw / 1000.0)),
            new("屏幕与其他", "SeriesRest", Avg(r => Split(r) ? r.RestMw / 1000.0 : null)),
        };
        if (rows.Any(r => !Split(r)))
            series.Add(new("整机（旧记录，未拆分）", "ChartMuted", Avg(r => Split(r) ? null : r.SystemMw / 1000.0)));
        return (times, series);
    }

    public static List<List<SampleRow>> Bucket(List<SampleRow> rows, int buckets)
    {
        var result = new List<List<SampleRow>>();
        if (rows.Count == 0) return result;
        double span = (rows[^1].Time - rows[0].Time).TotalSeconds;
        double width = Math.Max(1, span / buckets);
        List<SampleRow>? cur = null;
        DateTimeOffset start = default;
        foreach (var r in rows)
        {
            bool gap = cur is not null && (r.Time - cur[^1].Time).TotalSeconds > Math.Max(120, r.IntervalSec * 3);
            if (cur is null || gap || (r.Time - start).TotalSeconds >= width)
            {
                cur = [r];
                start = r.Time;
                result.Add(cur);
            }
            else cur.Add(r);
        }
        return result;
    }

    /// <summary>Mean whole-system draw (W) over on-battery samples, time-weighted.</summary>
    public static double? AverageDrainW(IEnumerable<SampleRow> rows)
    {
        double j = 0, s = 0;
        foreach (var r in rows)
            if (r is { OnAc: false, SystemMw: int mw } && r.IntervalSec <= 120) { j += mw / 1000.0 * r.IntervalSec; s += r.IntervalSec; }
        return s > 0 ? j / s : null;
    }

    /// <summary>
    /// Hours covered by the collector's own samples (imported watcher rows carry no
    /// per-app data), for averaging per-app energy into watts.
    /// </summary>
    public static double CoveredHours(IEnumerable<SampleRow> rows, bool batteryOnly) =>
        rows.Where(r => r.PackageMw > 0 && r.IntervalSec <= 120 && (!batteryOnly || !r.OnAc)).Sum(r => r.IntervalSec) / 3600;

    public static string Duration(double hours) =>
        double.IsFinite(hours) && hours > 0 ? $"{(int)hours}:{(int)(hours * 60 % 60):00}" : "—";
}
