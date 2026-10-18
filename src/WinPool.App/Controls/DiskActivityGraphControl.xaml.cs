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
    private readonly HashSet<(string Instance, int Segment)> _retained = [];
    private readonly List<(string Instance, int Segment)> _removed = [];

    public DiskActivityGraphControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
        ActualThemeChanged += (_, _) => Render();
    }

    public void SetSeries(IReadOnlyList<DiskGraphSeries> series, long revision, long displayRevision)
    {
        if (_revision == revision && _displayRevision == displayRevision)
        {
            if (MonitorUiMeasurement.IsEnabled)
                MonitorUiMeasurement.GraphSetSeriesSameRevision(IsGraphVisible);
            return;
        }
        _series = series; _revision = revision; _displayRevision = displayRevision;
        Render();
    }
    private void PlotCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Render();

    private bool IsGraphVisible => IsLoaded && Visibility == Visibility.Visible && App.Window.AppWindow.IsVisible
        && App.Window.AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };

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

        if (!IsGraphVisible)
        {
            MonitorUiMeasurement.GraphHidden();
            return;
        }
        var key = (_revision, _displayRevision, width, height, ActualTheme);
        if (_rendered == key)
        {
            MonitorUiMeasurement.GraphRenderedKeyUnchanged();
            return;
        }
        MonitorUiMeasurement.GraphRender();

        var now = DateTimeOffset.Now;
        DateTimeOffset? latestSample = null;
        var speedMax = MinSpeedScale;
        for (var seriesIndex = 0; seriesIndex < _series.Count; seriesIndex++)
        {
            var points = _series[seriesIndex].Points;
            if (points.Count > 0)
            {
                var candidate = points[^1].Timestamp;
                if (latestSample is null || candidate > latestSample.Value) latestSample = candidate;
            }
            for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                var point = points[pointIndex];
                speedMax = Math.Max(speedMax, Math.Max(point.ReadBytesPerSecond, point.WriteBytesPerSecond));
            }
        }
        var windowEnd = latestSample is { } lastSampleTime && lastSampleTime < now ? lastSampleTime : now;
        var windowStart = windowEnd - TimeSpan.FromSeconds(60);
        SpeedMaxLabel.Text = FormatRate(speedMax);

        var gridBrush = ProbeGridline.Background;
        while (_gridLines.Count < 3)
        {
            var line = new Line { StrokeThickness = 1 };
            _gridLines.Add(line); PlotCanvas.Children.Add(line);
            MonitorUiMeasurement.GridLineCreated();
        }
        for (var index = 0; index < _gridLines.Count; index++)
        {
            var line = _gridLines[index]; line.X1 = 0; line.X2 = width;
            line.Y1 = line.Y2 = height * (index + 1) / 4; line.Stroke = gridBrush;
        }
        _retained.Clear();
        for (var seriesIndex = 0; seriesIndex < _series.Count; seriesIndex++)
        {
            var series = _series[seriesIndex];
            var segmentIndex = 0;
            var segmentStart = 0;
            for (var boundary = 1; boundary <= series.Points.Count; boundary++)
            {
                if (boundary < series.Points.Count && !series.Points[boundary].GapBefore) continue;
                var start = segmentStart;
                var count = boundary - start;
                segmentStart = boundary;
                if (count < 2) continue;
                var identity = (series.InstanceName, segmentIndex++); _retained.Add(identity);
                if (!_visuals.TryGetValue(identity, out var visual))
                {
                    visual = new SegmentVisual(this, series.Color); _visuals.Add(identity, visual);
                    foreach (var shape in visual.Elements) PlotCanvas.Children.Add(shape);
                    MonitorUiMeasurement.SegmentCreated();
                }
                visual.SetColor(series.Color);
                var write = BuildPoints(series.Points, start, count, windowStart, windowEnd, plotWidth, height, speedMax, point => point.WriteBytesPerSecond, visual.WritePoints, visual.FillPoints);
                var read = BuildPoints(series.Points, start, count, windowStart, windowEnd, plotWidth, height, speedMax, point => point.ReadBytesPerSecond, visual.ReadPoints);
                var activity = BuildPoints(series.Points, start, count, windowStart, windowEnd, plotWidth, height, 100, point => point.ActivityPercent, visual.ActivityPoints);
                var latest = start + count == series.Points.Count;
                var last = series.Points[start + count - 1];
                UpdateFlag(visual.ActivityFlag, latest, $"{last.ActivityPercent:F0}%", activity.Y, plotWidth, height);
                UpdateFlag(visual.ReadFlag, latest, $"R {FormatRate(last.ReadBytesPerSecond)}", read.Y, plotWidth, height);
                UpdateFlag(visual.WriteFlag, latest, $"W {FormatRate(last.WriteBytesPerSecond)}", write.Y, plotWidth, height);
            }
        }
        _removed.Clear();
        foreach (var identity in _visuals.Keys)
            if (!_retained.Contains(identity)) _removed.Add(identity);
        foreach (var identity in _removed)
        {
            foreach (var shape in _visuals[identity].Elements) PlotCanvas.Children.Remove(shape);
            _visuals.Remove(identity);
            MonitorUiMeasurement.SegmentRemoved();
        }
        _removed.Clear();
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
        public PointCollection FillPoints { get; } = new();
        public PointCollection WritePoints { get; } = new();
        public PointCollection ReadPoints { get; } = new();
        public PointCollection ActivityPoints { get; } = new();
        public Polygon Fill { get; } = new();
        public Polyline Write { get; } = new() { StrokeThickness = 1.6 };
        public Polyline Read { get; } = new() { StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 2.5 } };
        public Polyline Activity { get; } = new() { StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 0.4, 2.2 }, StrokeDashCap = PenLineCap.Round, Opacity = 0.85 };
        public Grid ActivityFlag { get; }
        public Grid ReadFlag { get; }
        public Grid WriteFlag { get; }
        public FrameworkElement[] Elements { get; }
        private readonly SolidColorBrush stroke = new();
        private readonly SolidColorBrush fill = new();
        private Color? color;
        public SegmentVisual(DiskActivityGraphControl owner, Color color)
        {
            Fill.Points = FillPoints; Write.Points = WritePoints; Read.Points = ReadPoints; Activity.Points = ActivityPoints;
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

    private static PointCollection ToPointCollection(List<Point> points)
    {
        var collection = new PointCollection();
        foreach (var point in points)
        {
            collection.Add(point);
        }
        return collection;
    }

    private static Point BuildPoints(
        IReadOnlyList<MonitorSamplePoint> points, int start, int count,
        DateTimeOffset windowStart,
        DateTimeOffset now,
        double width,
        double height,
        double scaleMax,
        Func<MonitorSamplePoint, double> selector, PointCollection result, PointCollection? fill = null)
    {
        var span = (now - windowStart).TotalSeconds;
        var maxPoints = Math.Max(2, (int)width);
        var step = Math.Max(1, (count + maxPoints - 1) / maxPoints);
        result.Clear();
        fill?.Clear();
        var first = default(Point);
        var last = default(Point);
        var resultCount = 0;
        for (var i = start; i < start + count; i += step)
        {
            var point = points[i];
            var seconds = (point.Timestamp - windowStart).TotalSeconds;
            var x = Math.Clamp(seconds / span, 0, 1) * width;
            var y = height - (Math.Clamp(selector(point) / scaleMax, 0, 1) * height);
            var plotted = new Point(x, y);
            if (resultCount == 0) first = plotted;
            result.Add(plotted);
            fill?.Add(plotted);
            resultCount++;
        }

        if (resultCount > 0)
        {
            var point = points[start + count - 1];
            var seconds = (point.Timestamp - windowStart).TotalSeconds;
            last = new Point(
                Math.Clamp(seconds / span, 0, 1) * width,
                height - (Math.Clamp(selector(point) / scaleMax, 0, 1) * height));
            result[resultCount - 1] = last;
            if (fill is not null)
            {
                fill[resultCount - 1] = last;
                fill.Add(new Point(last.X, height));
                fill.Add(new Point(first.X, height));
            }
        }
        return last;
    }
}
