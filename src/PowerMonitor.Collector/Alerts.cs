using PowerMonitor.Core;

namespace PowerMonitor.Collector;

/// <summary>
/// Battery alerts, checked on every sample (sampling thread):
///   the dGPU awake (D0) for 3 minutes on battery, naming the apps with engine time on it;
///   the draw averaging 30 W or more over 5 minutes, naming the apps that used the most energy.
/// Plugging in resets both; each repeats at most every 30 minutes.
/// </summary>
sealed class Alerts
{
    static readonly TimeSpan DGpuAfter = TimeSpan.FromMinutes(3), DrainWindow = TimeSpan.FromMinutes(5), Repeat = TimeSpan.FromMinutes(30);
    const int DrainAlertMw = 30_000;

    readonly SortedSet<string> _dgpuUsers = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<Sample> _window = new(); // on-battery samples with a known draw, last 5 min
    DateTimeOffset? _dgpuSince;
    DateTimeOffset _lastDGpu = DateTimeOffset.MinValue, _lastDrain = DateTimeOffset.MinValue;

    public (string Title, string Body)? Check(Sample s, IReadOnlyList<string> dgpuUsers)
    {
        if (s.OnAc)
        {
            _dgpuSince = null;
            _dgpuUsers.Clear();
            _window.Clear();
            return null;
        }

        if (s.DGpuDState == 1)
        {
            _dgpuSince ??= s.Time;
            _dgpuUsers.UnionWith(dgpuUsers);
        }
        else
        {
            _dgpuSince = null;
            _dgpuUsers.Clear();
        }
        if (_dgpuSince is { } since && s.Time - since >= DGpuAfter && s.Time - _lastDGpu >= Repeat)
        {
            _lastDGpu = s.Time;
            string cost = s.DGpuMw is int mw and > 0 ? $"估计多耗 {mw / 1000.0:0} W" : "大约多耗 10-15 W";
            string who = _dgpuUsers.Count > 0
                ? "在用它的应用：" + string.Join("、", _dgpuUsers)
                : "没有应用在用它（外接显示器？）";
            return ("用电池时独显醒着", $"{cost}。{who}");
        }

        if (s.SystemMw is null) { _window.Clear(); return null; }
        _window.Enqueue(s);
        while (s.Time - _window.Peek().Time > DrainWindow) _window.Dequeue();
        var first = _window.Peek();
        double seconds = (s.Time - first.Time).TotalSeconds + first.IntervalSec;
        if (seconds < DrainWindow.TotalSeconds - 15 || s.Time - _lastDrain < Repeat) return null;
        double avgMw = _window.Average(x => x.SystemMw!.Value);
        if (avgMw < DrainAlertMw) return null;

        _lastDrain = s.Time;
        var top = _window.SelectMany(x => x.Processes)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, W: g.Sum(p => p.TotalJ) / seconds))
            .OrderByDescending(p => p.W).Take(3)
            .Select(p => $"{p.Name} {p.W:0.0} W");
        return ($"耗电偏高：{avgMw / 1000:0} W", $"5 分钟平均。耗电最多：{string.Join("、", top)}");
    }
}
