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

    public DiskActivityGraphControl()
    {
        InitializeComponent();
    }

    public void SetSeries(IReadOnlyList<DiskGraphSeries> series)
    {
        _series = series;
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
        PlotCanvas.Children.Clear();
        var plotWidth = width - LabelGutter;
        if (plotWidth < 40 || height < 40)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var windowStart = now - TimeSpan.FromSeconds(60);
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
        for (var i = 1; i <= 3; i++)
        {
            var y = height * i / 4;
            PlotCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1
            });
        }

        var activityLabels = new List<(FrameworkElement Label, double Y)>();
        var readLabels = new List<(FrameworkElement Label, double Y)>();
        var writeLabels = new List<(FrameworkElement Label, double Y)>();

        foreach (var series in _series)
        {
            if (series.Points.Count < 2)
            {
                continue;
            }

            var brush = new SolidColorBrush(series.Color);
            var fillBrush = new SolidColorBrush(Color.FromArgb(0x2E, series.Color.R, series.Color.G, series.Color.B));
            var writePoints = BuildPoints(series.Points, windowStart, now, plotWidth, height, speedMax, x => x.WriteBytesPerSecond);
            var readPoints = BuildPoints(series.Points, windowStart, now, plotWidth, height, speedMax, x => x.ReadBytesPerSecond);
            var activityPoints = BuildPoints(series.Points, windowStart, now, plotWidth, height, 100.0, x => x.ActivityPercent);

            var fill = new Polygon
            {
                Fill = fillBrush,
                Points = ToPointCollection(writePoints)
            };
            fill.Points.Add(new Point(writePoints[^1].X, height));
            fill.Points.Add(new Point(writePoints[0].X, height));
            PlotCanvas.Children.Add(fill);

            PlotCanvas.Children.Add(new Polyline
            {
                Stroke = brush,
                StrokeThickness = 1.6,
                Points = ToPointCollection(writePoints)
            });
            PlotCanvas.Children.Add(new Polyline
            {
                Stroke = brush,
                StrokeDashArray = new DoubleCollection { 4, 2.5 },
                StrokeThickness = 1.2,
                Points = ToPointCollection(readPoints)
            });
            PlotCanvas.Children.Add(new Polyline
            {
                Stroke = brush,
                StrokeDashArray = new DoubleCollection { 0.4, 2.2 },
                StrokeDashCap = PenLineCap.Round,
                StrokeThickness = 1.2,
                Opacity = 0.85,
                Points = ToPointCollection(activityPoints)
            });

            var last = series.Points[^1];
            activityLabels.Add((CreateFlag(series.Color, $"{last.ActivityPercent:F0}%"), activityPoints[^1].Y));
            readLabels.Add((CreateFlag(series.Color, $"R {FormatRate(last.ReadBytesPerSecond)}"), readPoints[^1].Y));
            writeLabels.Add((CreateFlag(series.Color, $"W {FormatRate(last.WriteBytesPerSecond)}"), writePoints[^1].Y));
        }

        foreach (var group in new[] { activityLabels, readLabels, writeLabels })
        {
            foreach (var (label, y) in group)
            {
                PlotCanvas.Children.Add(label);
                Canvas.SetLeft(label, plotWidth + 2);
                Canvas.SetTop(label, Math.Clamp(y - 11, 0, Math.Max(0, height - 18)));
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

    private static List<Point> BuildPoints(
        IReadOnlyList<MonitorSamplePoint> points,
        DateTimeOffset windowStart,
        DateTimeOffset now,
        double width,
        double height,
        double scaleMax,
        Func<MonitorSamplePoint, double> selector)
    {
        var span = (now - windowStart).TotalSeconds;
        var maxPoints = Math.Max(2, (int)width);
        var step = Math.Max(1, points.Count / maxPoints);
        var result = new List<Point>(points.Count / step + 1);
        for (var i = 0; i < points.Count; i += step)
        {
            var point = points[i];
            var seconds = (point.Timestamp - windowStart).TotalSeconds;
            var x = Math.Clamp(seconds / span, 0, 1) * width;
            var y = height - (Math.Clamp(selector(point) / scaleMax, 0, 1) * height);
            result.Add(new Point(x, y));
        }

        if (result.Count > 0 && points.Count > 0)
        {
            var last = points[^1];
            var seconds = (last.Timestamp - windowStart).TotalSeconds;
            result[^1] = new Point(
                Math.Clamp(seconds / span, 0, 1) * width,
                height - (Math.Clamp(selector(last) / scaleMax, 0, 1) * height));
        }
        return result;
    }
}
