using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WinPool.App.Services;

/// <summary>
/// Keeps short, accessible contextual help consistent across the desktop UI.
/// The displayed text is deliberately bounded by the layout instead of relying
/// on a one-line native tooltip.
/// </summary>
public static class ContextHelp
{
    public static void Set(FrameworkElement element, string? text)
    {
        ArgumentNullException.ThrowIfNull(element);

        var normalized = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        AutomationProperties.SetHelpText(element, normalized);
        ToolTipService.SetToolTip(
            element,
            string.IsNullOrEmpty(normalized)
                ? null
                : new ToolTip
                {
                    Content = new TextBlock
                    {
                        Text = normalized,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 360
                    }
                });
    }
}
