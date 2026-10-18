using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WinPool.App.Services;

namespace WinPool_App.Controls;

public sealed class DiskGraphSeries
{
    public required string InstanceName { get; init; }

    public required string DisplayName { get; init; }

    public required Color Color { get; init; }

    public required IReadOnlyList<MonitorSamplePoint> Points { get; init; }
}

public sealed partial class DiskActivityGraphControl : UserControl
{
    private const double MinSpeedScale = 100.0 * 1024;
    private const double LabelGutter = 96;

    private IReadOnlyList<DiskGraphSeries> _series = [];
    private long _revision = -1, _displayRevision = -1;
    private (long Revision, long Display, double Width, double Height, ElementTheme Theme)? _rendered;
    private readonly List<Line> _gridLines = [];
    private readonly Dictionary<(string Instance, int Segment), SegmentVisual> _visuals = new();

    public DiskActivityGraphControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
        ActualThemeChanged += (_, _) => Render();
    }

    public void SetSeries(IReadOnlyList<DiskGraphSeries> series, long revision, long displayRevision)
    {
        if (_revision == revision && _displayRevision == displayRevision) return;
        _series = series; _revision = revision; _displayRevision = displayRevision;
        Render();
    }
    private void PlotCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Render();

    internal static string FormatRate(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024
            ? $"{bytesPerSecond / 1024 / 1024:F1} MiB/s"
            : $"{bytesPerSecond / 1024:F0} KiB/s";

    private void Render()
    {
        try
        {
            RenderCore();
        }
        catch (Exception ex)
        {
            WinPool_App.MonitorPage.LogMonitorFailure("GraphRender", ex);
        }
    }

    private void RenderCore()
    {
        var width = PlotCanvas.ActualWidth;
        var height = PlotCanvas.ActualHeight;

        var plotWidth = width - LabelGutter;
        if (plotWidth < 40 || height < 40)
        {
            return;
        }

        if (!IsLoaded || Visibility != Visibility.Visible || !App.Window.AppWindow.IsVisible
            || App.Window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized }) return;
        var key = (_revision, _displayRevision, width, height, ActualTheme);
        if (_rendered == key) return;

        var now = DateTimeOffset.Now;
        var latestSample = _series
            .Where(series => series.Points.Count > 0)
            .Select(series => (DateTimeOffset?)series.Points[^1].Timestamp)
            .DefaultIfEmpty(now)
            .Max()!.Value;
        var windowEnd = latestSample > now ? now : latestSample;
        var windowStart = windowEnd - TimeSpan.FromSeconds(60);
        var speedMax = MinSpeedScale;
        foreach (var series in _series)
        {
            foreach (var point in series.Points)
            {
                speedMax = Math.Max(speedMax, Math.Max(point.ReadBytesPerSecond, point.WriteBytesPerSecond));
            }
        }
        SpeedMaxLabel.Text = FormatRate(speedMax);

        var gridBrush = ProbeGridline.Background;
        while (_gridLines.Count < 3)
        {
            var line = new Line { StrokeThickness = 1 };
            _gridLines.Add(line); PlotCanvas.Children.Add(line);
        }
        for (var index = 0; index < _gridLines.Count; index++)
        {
            var line = _gridLines[index]; line.X1 = 0; line.X2 = width;
            line.Y1 = line.Y2 = height * (index + 1) / 4; line.Stroke = gridBrush;
        }
        var retained = new HashSet<(string Instance, int Segment)>();
        foreach (var series in _series)
        {
            var segmentIndex = 0;
            foreach (var (start, count) in Segments(series.Points))
            {
                if (count < 2) continue;
                var identity = (series.InstanceName, segmentIndex++); retained.Add(identity);
                if (!_visuals.TryGetValue(identity, out var visual))
                {
                    visual = new SegmentVisual(this, series.Color); _visuals.Add(identity, visual);
                    foreach (var shape in visual.Elements) PlotCanvas.Children.Add(shape);
                }
                visual.SetColor(series.Color);
                var write = BuildPoints(series.Points, start, count, windowStart, windowEnd, plotWidth, height, speedMax, point => point.WriteBytesPerSecond, visual.Write.Points);
                var read = BuildPoints(series.Points, start, count, windowStart, windowEnd, plotWidth, height, speedMax, point => point.ReadBytesPerSecond, visual.Read.Points);
                var activity = BuildPoints(series.Points, start, count, windowStart, windowEnd, plotWidth, height, 100, point => point.ActivityPercent, visual.Activity.Points);
                var fill = visual.Fill.Points; fill.Clear(); foreach (var point in write) fill.Add(point);
                fill.Add(new Point(write[^1].X, height)); fill.Add(new Point(write[0].X, height));
                var latest = start + count == series.Points.Count;
                var last = series.Points[start + count - 1];
                UpdateFlag(visual.ActivityFlag, latest, $"{last.ActivityPercent:F0}%", activity[^1].Y, plotWidth, height);
                UpdateFlag(visual.ReadFlag, latest, $"R {FormatRate(last.ReadBytesPerSecond)}", read[^1].Y, plotWidth, height);
                UpdateFlag(visual.WriteFlag, latest, $"W {FormatRate(last.WriteBytesPerSecond)}", write[^1].Y, plotWidth, height);
            }
        }
        foreach (var identity in _visuals.Keys.Where(key => !retained.Contains(key)).ToArray())
        {
            foreach (var shape in _visuals[identity].Elements) PlotCanvas.Children.Remove(shape);
            _visuals.Remove(identity);
        }
        _rendered = key;
    }

    private static void UpdateFlag(Grid flag, bool visible, string text, double y, double plotWidth, double height)
    {
        flag.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        ((TextBlock)flag.Children[1]).Text = text;
        Canvas.SetLeft(flag, plotWidth + 2);
        Canvas.SetTop(flag, Math.Clamp(y - 11, 0, Math.Max(0, height - 18)));
    }

    private sealed class SegmentVisual
    {
        public Polygon Fill { get; } = new() { Points = new PointCollection() };
        public Polyline Write { get; } = new() { StrokeThickness = 1.6, Points = new PointCollection() };
        public Polyline Read { get; } = new() { StrokeThickness = 1.2, Points = new PointCollection(), StrokeDashArray = new DoubleCollection { 4, 2.5 } };
        public Polyline Activity { get; } = new() { StrokeThickness = 1.2, Points = new PointCollection(), StrokeDashArray = new DoubleCollection { 0.4, 2.2 }, StrokeDashCap = PenLineCap.Round, Opacity = 0.85 };
        public Grid ActivityFlag { get; }
        public Grid ReadFlag { get; }
        public Grid WriteFlag { get; }
        public FrameworkElement[] Elements { get; }
        private readonly SolidColorBrush stroke = new();
        private readonly SolidColorBrush fill = new();
        private Color? color;
        public SegmentVisual(DiskActivityGraphControl owner, Color color)
        {
            ActivityFlag = (Grid)owner.CreateFlag(color, ""); ReadFlag = (Grid)owner.CreateFlag(color, ""); WriteFlag = (Grid)owner.CreateFlag(color, "");
            Elements = [Fill, Write, Read, Activity, ActivityFlag, ReadFlag, WriteFlag];
            Fill.Fill = fill; Write.Stroke = Read.Stroke = Activity.Stroke = stroke; SetColor(color);
        }
        public void SetColor(Color next)
        {
            if (color == next) return;
            color = next; stroke.Color = next; fill.Color = Color.FromArgb(0x2E, next.R, next.G, next.B);
            foreach (var flag in new[] { ActivityFlag, ReadFlag, WriteFlag })
            {
                ((Polygon)flag.Children[0]).Fill = stroke;
                ((TextBlock)flag.Children[1]).Foreground = new SolidColorBrush(RelativeLuminance(next) > 0.48
                    ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255));
            }
        }
    }
    private FrameworkElement CreateFlag(Color color, string text)
    {
        const double w = LabelGutter - 10;
        const double h = 18;
        var flag = new Polygon
        {
            Fill = new SolidColorBrush(color),
            Points = ToPointCollection(
            [
                new Point(0, h / 2),
                new Point(8, 0),
                new Point(w, 0),
                new Point(w, h),
                new Point(8, h)
            ])
        };
        var foreground = RelativeLuminance(color) > 0.48
            ? new SolidColorBrush(Color.FromArgb(255, 0, 0, 0))
            : new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        var grid = new Grid { Width = w, Height = h };
        grid.Children.Add(flag);
        grid.Children.Add(new TextBlock
        {
            Margin = new Thickness(10, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Foreground = foreground,
            Text = text
        });
        return grid;
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R))
             + (0.7152 * Linearize(color.G))
             + (0.0722 * Linearize(color.B));
    }

    private static IEnumerable<(int Start, int Count)> Segments(IReadOnlyList<MonitorSamplePoint> points)
    {
        var start = 0;
        for (var index = 1; index < points.Count; index++)
        {
            if (!points[index].GapBefore) continue;
            yield return (start, index - start); start = index;
        }
        if (start < points.Count) yield return (start, points.Count - start);
    }
    private static PointCollection ToPointCollection(List<Point> points)
    {
        var collection = new PointCollection();
        foreach (var point in points)
        {
            collection.Add(point);
        }
        return collection;
    }

    private static PointCollection BuildPoints(
        IReadOnlyList<MonitorSamplePoint> points, int start, int count,
        DateTimeOffset windowStart,
        DateTimeOffset now,
        double width,
        double height,
        double scaleMax,
        Func<MonitorSamplePoint, double> selector, PointCollection result)
    {
        var span = (now - windowStart).TotalSeconds;
        var maxPoints = Math.Max(2, (int)width);
        var step = Math.Max(1, (count + maxPoints - 1) / maxPoints);
        result.Clear();
        for (var i = start; i < start + count; i += step)
        {
            var point = points[i];
            var seconds = (point.Timestamp - windowStart).TotalSeconds;
            var x = Math.Clamp(seconds / span, 0, 1) * width;
            var y = height - (Math.Clamp(selector(point) / scaleMax, 0, 1) * height);
            result.Add(new Point(x, y));
        }

        if (result.Count > 0 && count > 0)
        {
            var last = points[start + count - 1];
            var seconds = (last.Timestamp - windowStart).TotalSeconds;
            result[^1] = new Point(
                Math.Clamp(seconds / span, 0, 1) * width,
                height - (Math.Clamp(selector(last) / scaleMax, 0, 1) * height));
        }
        return result;
    }
}
