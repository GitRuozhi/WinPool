using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace WinPool_App.Controls;

/// <summary>
/// Animates two complementary pairs of outline segments around a measured
/// host. The stable object key gives each outline a slow, repeatable duration.
/// </summary>
internal sealed class PendingBorderAnimation : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly Shape _defaultColorOutline;
    private readonly Shape _themeColorOutline;
    private readonly double _cornerRadius;
    private readonly TimeSpan _duration;
    private Storyboard? _storyboard;
    private bool _isActive;
    private bool _isDisposed;
    private double _lastWidth = double.NaN;
    private double _lastHeight = double.NaN;
    private double _cycleLength;
    private double _segmentLength;
    private long _phaseStartedAt;

    public PendingBorderAnimation(
        FrameworkElement measuredHost,
        Shape defaultColorOutline,
        Shape themeColorOutline,
        string stableObjectId,
        double cornerRadius = 4)
    {
        _host = measuredHost;
        _defaultColorOutline = defaultColorOutline;
        _themeColorOutline = themeColorOutline;
        _cornerRadius = Math.Max(0, cornerRadius);
        _duration = ResolveDuration(stableObjectId);
        _host.SizeChanged += Host_SizeChanged;
        _host.Loaded += Host_Loaded;
        _host.Unloaded += Host_Unloaded;
    }

    public void SetActive(bool active)
    {
        if (_isDisposed || _isActive == active)
        {
            return;
        }

        _isActive = active;
        if (!_isActive)
        {
            StopStoryboard();
            _defaultColorOutline.StrokeDashOffset = 0;
            _themeColorOutline.StrokeDashOffset = 0;
            return;
        }

        UpdateGeometry(force: true);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _host.SizeChanged -= Host_SizeChanged;
        _host.Loaded -= Host_Loaded;
        _host.Unloaded -= Host_Unloaded;
        StopStoryboard();
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGeometry();

    private void Host_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isActive)
        {
            UpdateGeometry(force: true);
        }
    }

    private void Host_Unloaded(object sender, RoutedEventArgs e) => StopStoryboard();

    private void UpdateGeometry(bool force = false)
    {
        if (!_isActive || !_host.IsLoaded)
        {
            return;
        }

        var stroke = Math.Max(0.25, _themeColorOutline.StrokeThickness);
        var width = Math.Max(0, _host.ActualWidth - stroke);
        var height = Math.Max(0, _host.ActualHeight - stroke);
        if (width <= 8 || height <= 8)
        {
            StopStoryboard();
            return;
        }

        if (!force && Math.Abs(width - _lastWidth) < 0.5 && Math.Abs(height - _lastHeight) < 0.5)
        {
            return;
        }

        var progress = CurrentProgress();
        StopStoryboard();
        _lastWidth = width;
        _lastHeight = height;

        var radius = Math.Min(_cornerRadius, Math.Min(width, height) / 2);
        var perimeter = 2 * (width + height) - 8 * radius + 2 * Math.PI * radius;
        _segmentLength = perimeter / (4 * stroke);
        _cycleLength = _segmentLength * 2;
        var dashPattern = new DoubleCollection { _segmentLength, _segmentLength };
        _defaultColorOutline.StrokeDashArray = dashPattern;
        _themeColorOutline.StrokeDashArray = new DoubleCollection { _segmentLength, _segmentLength };

        var themeOffset = progress * _cycleLength;
        var defaultOffset = themeOffset + _segmentLength;
        _themeColorOutline.StrokeDashOffset = themeOffset;
        _defaultColorOutline.StrokeDashOffset = defaultOffset;

        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            return;
        }

        _phaseStartedAt = Stopwatch.GetTimestamp() - (long)(progress * _duration.TotalSeconds * Stopwatch.Frequency);
        var themeMovement = CreateMovement(_themeColorOutline, themeOffset, _cycleLength);
        var defaultMovement = CreateMovement(_defaultColorOutline, defaultOffset, _cycleLength);
        _storyboard = new Storyboard();
        _storyboard.Children.Add(themeMovement);
        _storyboard.Children.Add(defaultMovement);
        _storyboard.Begin();
    }

    private DoubleAnimation CreateMovement(Shape target, double from, double distance)
    {
        var movement = new DoubleAnimation
        {
            From = from,
            To = from + distance,
            Duration = new Duration(_duration),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(movement, target);
        Storyboard.SetTargetProperty(movement, "StrokeDashOffset");
        return movement;
    }

    private double CurrentProgress()
    {
        if (_phaseStartedAt == 0 || _duration <= TimeSpan.Zero)
        {
            return 0;
        }

        var elapsed = (Stopwatch.GetTimestamp() - _phaseStartedAt) / (double)Stopwatch.Frequency;
        return (elapsed / _duration.TotalSeconds) % 1;
    }

    private void StopStoryboard()
    {
        _storyboard?.Stop();
        _storyboard = null;
    }

    private static TimeSpan ResolveDuration(string stableObjectId)
    {
        // FNV-1a gives each stable object a repeatable pseudo-random speed,
        // independent of resize or redraw events.
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var character in stableObjectId)
        {
            hash ^= character;
            hash *= prime;
        }

        return TimeSpan.FromSeconds(9 + hash % 10);
    }
}
