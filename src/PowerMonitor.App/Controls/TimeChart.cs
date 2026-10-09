using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace PowerMonitor.App.Controls;

public sealed record ChartSeries(string Name, string BrushKey, double?[] Values);

/// <summary>
/// Time-series chart drawn with XAML shapes: stacked areas or lines, one y axis,
/// recessive grid, legend row, and a crosshair tooltip on hover. Points further
/// apart than <see cref="GapSeconds"/> are not joined (sleep, collector off).
/// </summary>
public sealed class TimeChart : Grid
{
    readonly WrapPanel _legend = new() { HorizontalSpacing = 16, VerticalSpacing = 4, Margin = new Thickness(0, 0, 0, 8) };
    readonly Canvas _plot = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    readonly Canvas _overlay = new() { IsHitTestVisible = false };
    readonly Border _tooltip = new()
    {
        Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
        Visibility = Visibility.Collapsed,
    };
    readonly StackPanel _tooltipText = new() { Spacing = 2 };

    DateTimeOffset[] _times = [];
    List<ChartSeries> _series = [];
    double _x0, _x1, _yMax, _left, _top, _width, _height;

    public bool Stacked { get; set; } = true;
    public string Unit { get; set; } = "W";
    public string ValueFormat { get; set; } = "0.0";
    public double? FixedMax { get; set; }
    public double GapSeconds { get; set; } = 120;
    public bool ShowTotal { get; set; } = true;
    /// <summary>Time-axis label format; chosen from the span when null.</summary>
    public string? TimeFormat { get; set; }

    public TimeChart()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(_legend);
        SetRow(_plot, 1);
        SetRow(_overlay, 1);
        Children.Add(_plot);
        Children.Add(_overlay);
        _tooltip.Child = _tooltipText;
        _overlay.Children.Add(_tooltip);
        _plot.SizeChanged += (_, _) => Redraw();
        _plot.PointerMoved += OnPointerMoved;
        _plot.PointerExited += (_, _) => HideHover();
        ActualThemeChanged += (_, _) => { BuildLegend(); Redraw(); };
    }

    public void SetData(DateTimeOffset[] times, IEnumerable<ChartSeries> series)
    {
        _times = times;
        _series = series.ToList();
        BuildLegend();
        Redraw();
    }

    Brush Res(string key) => ChartBrushes.Get(key, ActualTheme);

    static Brush Theme(string key) => (Brush)Application.Current.Resources[key];

    void BuildLegend()
    {
        _legend.Children.Clear();
        _legend.Visibility = _series.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var s in _series)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            item.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = Res(s.BrushKey), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = s.Name, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Theme("TextFillColorSecondaryBrush") });
            _legend.Children.Add(item);
        }
    }

    double Total(int i) => Stacked ? _series.Sum(s => s.Values[i] ?? 0) : _series.Max(s => s.Values[i] ?? 0);
    bool Has(int i) => _series.Any(s => s.Values[i] is not null);

    void Redraw()
    {
        _plot.Children.Clear();
        HideHover();
        double w = _plot.ActualWidth, h = _plot.ActualHeight;
        if (w < 50 || h < 50) return;
        var muted = Res("ChartMuted");
        var grid = Res("ChartGrid");
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

        if (_times.Length < 2 || _series.Count == 0)
        {
            var empty = new TextBlock { Text = "这段时间没有数据", Style = caption, Foreground = muted };
            Canvas.SetLeft(empty, w / 2 - 50); Canvas.SetTop(empty, h / 2 - 10);
            _plot.Children.Add(empty);
            return;
        }

        double max = FixedMax ?? Math.Max(0.001, Enumerable.Range(0, _times.Length).Where(Has).Select(Total).DefaultIfEmpty(1).Max());
        var ticks = NiceTicks(max);
        _yMax = FixedMax ?? ticks[^1];
        _left = 44; _top = 6; _width = w - _left - 8; _height = h - _top - 24;
        _x0 = _times[0].ToUnixTimeMilliseconds();
        _x1 = Math.Max(_x0 + 1, _times[^1].ToUnixTimeMilliseconds());

        foreach (var t in ticks)
        {
            double y = Y(t);
            _plot.Children.Add(new Line { X1 = _left, X2 = _left + _width, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1 });
            var label = new TextBlock { Text = $"{t:0.##}{(t == ticks[^1] ? " " + Unit : "")}", Style = caption, Foreground = muted, FontSize = 11 };
            Canvas.SetLeft(label, 0); Canvas.SetTop(label, y - 8);
            _plot.Children.Add(label);
        }

        // Time axis: about one label per 110 px.
        var span = _times[^1] - _times[0];
        int nLabels = Math.Max(2, (int)(_width / 110));
        string fmt = TimeFormat ?? (span > TimeSpan.FromDays(2) ? "M/d" : span > TimeSpan.FromHours(20) ? "M/d HH:mm" : "HH:mm");
        // Few points (daily data): label each point instead of even steps.
        var labelTimes = _times.Length <= nLabels + 1
            ? _times
            : Enumerable.Range(0, nLabels + 1).Select(k => _times[0] + span * k / nLabels).ToArray();
        foreach (var t in labelTimes)
        {
            var label = new TextBlock { Text = t.ToLocalTime().ToString(fmt), Style = caption, Foreground = muted, FontSize = 11 };
            double x = X(t.ToUnixTimeMilliseconds());
            Canvas.SetLeft(label, Math.Clamp(x - 18, _left - 4, w - 60)); Canvas.SetTop(label, _top + _height + 4);
            _plot.Children.Add(label);
        }

        foreach (var (a, b) in Runs())
        {
            if (Stacked)
            {
                var lower = new double[b - a + 1];
                foreach (var s in _series)
                {
                    var fig = new PathFigure { IsClosed = true, StartPoint = new Point(X(_times[a]), Y(lower[0])) };
                    var upper = new double[lower.Length];
                    for (int i = a; i <= b; i++) upper[i - a] = lower[i - a] + (s.Values[i] ?? 0);
                    for (int i = a; i <= b; i++) fig.Segments.Add(new LineSegment { Point = new Point(X(_times[i]), Y(upper[i - a])) });
                    for (int i = b; i >= a; i--) fig.Segments.Add(new LineSegment { Point = new Point(X(_times[i]), Y(lower[i - a])) });
                    var geo = new PathGeometry(); geo.Figures.Add(fig);
                    // Surface-colored hairline separates adjacent bands.
                    _plot.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                    {
                        Data = geo, Fill = Res(s.BrushKey), Opacity = 0.9,
                        Stroke = Theme("SolidBackgroundFillColorBaseBrush"), StrokeThickness = 1,
                    });
                    lower = upper;
                }
            }
            else
            {
                foreach (var s in _series)
                {
                    var line = new Polyline { Stroke = Res(s.BrushKey), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                    for (int i = a; i <= b; i++)
                        if (s.Values[i] is double v) line.Points.Add(new Point(X(_times[i]), Y(v)));
                    _plot.Children.Add(line);
                }
            }
        }

        // Sparse line data (e.g. one point per day): mark every point, so lone points show.
        if (!Stacked && _times.Length <= 60 && _width / _times.Length >= 16)
            foreach (var s in _series)
                for (int i = 0; i < _times.Length; i++)
                    if (s.Values[i] is double v)
                    {
                        var dot = new Ellipse { Width = 8, Height = 8, Fill = Res(s.BrushKey), Stroke = Theme("SolidBackgroundFillColorBaseBrush"), StrokeThickness = 2 };
                        Canvas.SetLeft(dot, X(_times[i]) - 4); Canvas.SetTop(dot, Y(v) - 4);
                        _plot.Children.Add(dot);
                    }
    }

    IEnumerable<(int, int)> Runs()
    {
        // A gap is longer than GapSeconds and than 2.5x the typical spacing (bucketed data).
        var deltas = Enumerable.Range(1, _times.Length - 1).Select(i => (_times[i] - _times[i - 1]).TotalSeconds).Order().ToArray();
        double gapSeconds = Math.Max(GapSeconds, deltas.Length > 0 ? deltas[deltas.Length / 2] * 2.5 : 0);
        int start = -1;
        for (int i = 0; i < _times.Length; i++)
        {
            bool ok = Has(i);
            bool gap = i > 0 && (_times[i] - _times[i - 1]).TotalSeconds > gapSeconds;
            if (start >= 0 && (!ok || gap)) { if (i - 1 > start) yield return (start, i - 1); start = -1; }
            if (ok && start < 0) start = i;
        }
        if (start >= 0 && _times.Length - 1 > start) yield return (start, _times.Length - 1);
    }

    double X(DateTimeOffset t) => X(t.ToUnixTimeMilliseconds());
    double X(double ms) => _left + (ms - _x0) / (_x1 - _x0) * _width;
    double Y(double v) => _top + _height - Math.Min(v, _yMax) / _yMax * _height;

    static double[] NiceTicks(double max)
    {
        double raw = max / 4, mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(s => s >= raw);
        int n = (int)Math.Ceiling(max / step - 1e-9);
        return Enumerable.Range(0, n + 1).Select(i => i * step).ToArray();
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_times.Length < 2 || _width <= 0) return;
        var p = e.GetCurrentPoint(_plot).Position;
        if (p.X < _left || p.X > _left + _width) { HideHover(); return; }
        double ms = _x0 + (p.X - _left) / _width * (_x1 - _x0);
        int idx = Array.BinarySearch(_times.Select(t => (double)t.ToUnixTimeMilliseconds()).ToArray(), ms);
        if (idx < 0) idx = Math.Clamp(~idx, 0, _times.Length - 1);
        if (idx > 0 && Math.Abs(_times[idx - 1].ToUnixTimeMilliseconds() - ms) < Math.Abs(_times[idx].ToUnixTimeMilliseconds() - ms)) idx--;
        if (!Has(idx)) { HideHover(); return; }

        // Crosshair + tooltip live in the overlay so the plot keeps receiving pointer events.
        for (int i = _overlay.Children.Count - 1; i >= 0; i--)
            if (_overlay.Children[i] != _tooltip) _overlay.Children.RemoveAt(i);
        double x = X(_times[idx]);
        _overlay.Children.Insert(0, new Line { X1 = x, X2 = x, Y1 = _top, Y2 = _top + _height, Stroke = Res("ChartMuted"), StrokeThickness = 1 });

        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _tooltipText.Children.Clear();
        _tooltipText.Children.Add(new TextBlock { Text = _times[idx].ToLocalTime().ToString("M/d HH:mm:ss"), Style = caption, Foreground = Theme("TextFillColorSecondaryBrush") });
        foreach (var s in Enumerable.Reverse(_series))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Rectangle { Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Fill = Res(s.BrushKey), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"{s.Name}  {(s.Values[idx] is double v ? v.ToString(ValueFormat) + " " + Unit : "—")}", Style = caption });
            _tooltipText.Children.Add(row);
        }
        if (Stacked && ShowTotal && _series.Count > 1)
            _tooltipText.Children.Add(new TextBlock { Text = $"合计  {Total(idx).ToString(ValueFormat)} {Unit}", Style = caption, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        _tooltip.Background = Theme("AcrylicInAppFillColorDefaultBrush");
        _tooltip.BorderBrush = Theme("SurfaceStrokeColorFlyoutBrush");
        _tooltip.Visibility = Visibility.Visible;
        _tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = _tooltip.DesiredSize.Width;
        Canvas.SetLeft(_tooltip, x + 12 + tw > _plot.ActualWidth ? x - 12 - tw : x + 12);
        Canvas.SetTop(_tooltip, Math.Clamp(p.Y - 20, 0, Math.Max(0, _plot.ActualHeight - _tooltip.DesiredSize.Height)));
    }

    void HideHover()
    {
        _tooltip.Visibility = Visibility.Collapsed;
        for (int i = _overlay.Children.Count - 1; i >= 0; i--)
            if (_overlay.Children[i] != _tooltip) _overlay.Children.RemoveAt(i);
    }
}
