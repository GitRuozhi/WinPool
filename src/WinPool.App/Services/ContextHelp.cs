using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinPool_App.Controls;

namespace WinPool.App.Services;

/// <summary>
/// Keeps short, accessible contextual help consistent across the desktop UI.
/// The displayed text is deliberately bounded by the layout instead of relying
/// on a one-line native tooltip.
/// </summary>
public static class ContextHelp
{
    private sealed class HelpState
    {
        public string Purpose { get; set; } = string.Empty;
        public string DisabledReason { get; set; } = string.Empty;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, HelpState> States = new();

    public static void Set(FrameworkElement element, string? text)
    {
        ArgumentNullException.ThrowIfNull(element);

        var normalized = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        var state = States.GetValue(element, _ => new HelpState());
        state.Purpose = normalized;
        Apply(element, state);
    }

    /// <summary>
    /// Supplies the explanation shown by a <see cref="ContextHelpHost"/> while
    /// an otherwise correctly disabled control is hovered. The control remains
    /// disabled; the wrapper is only a pointer surface for the tooltip.
    /// </summary>
    public static void SetDisabledReason(FrameworkElement element, string? reason)
    {
        ArgumentNullException.ThrowIfNull(element);

        var state = States.GetValue(element, _ => new HelpState());
        state.DisabledReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        Apply(element, state);
    }

    /// <summary>Creates a transparent host for runtime-built controls.</summary>
    public static ContextHelpHost Wrap(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var host = new ContextHelpHost
        {
            HorizontalAlignment = element.HorizontalAlignment,
            VerticalAlignment = element.VerticalAlignment,
            Width = element.Width,
            Height = element.Height,
            MinWidth = element.MinWidth,
            MinHeight = element.MinHeight,
            MaxWidth = element.MaxWidth,
            MaxHeight = element.MaxHeight,
            Margin = element.Margin
        };
        element.HorizontalAlignment = HorizontalAlignment.Stretch;
        element.VerticalAlignment = VerticalAlignment.Stretch;
        element.Width = double.NaN;
        element.Height = double.NaN;
        element.Margin = new Thickness(0);
        host.Children.Add(element);
        host.SetTarget(element);
        return host;
    }

    internal static ToolTip CreateToolTip(string text) => new()
    {
        Content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360
        }
    };

    private static void Apply(FrameworkElement element, HelpState state)
    {
        var directText = element is not Control { IsEnabled: false } || string.IsNullOrEmpty(state.DisabledReason)
            ? state.Purpose
            : state.DisabledReason;
        AutomationProperties.SetHelpText(element, directText);
        ToolTipService.SetToolTip(element, string.IsNullOrEmpty(state.Purpose) ? null : CreateToolTip(state.Purpose));

        var host = FindHost(element);
        host?.SetHelp(state.Purpose, state.DisabledReason);
        if (host is null)
        {
            element.Loaded -= Element_Loaded;
            element.Loaded += Element_Loaded;
        }
    }

    private static void Element_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || !States.TryGetValue(element, out var state))
        {
            return;
        }

        FindHost(element)?.SetHelp(state.Purpose, state.DisabledReason);
    }

    private static ContextHelpHost? FindHost(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ContextHelpHost host)
            {
                return host;
            }
        }

        return null;
    }
}
