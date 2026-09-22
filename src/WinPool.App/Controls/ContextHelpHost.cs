using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinPool.App.Services;

namespace WinPool_App.Controls;

/// <summary>
/// Keeps a tooltip hit target alive when its child is disabled. WinUI disables
/// pointer interaction on the child itself, so a tooltip attached directly to
/// a disabled button or input is not reliably reachable with the mouse.
/// </summary>
public sealed class ContextHelpHost : Grid
{
    private FrameworkElement? _target;
    private long _isEnabledChangedToken;
    private bool _loaded;
    private string _purpose = string.Empty;
    private string _disabledReason = string.Empty;

    public ContextHelpHost()
    {
        // An explicit transparent background makes the wrapper, rather than a
        // disabled child, the pointer target without changing the visual chrome.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Loaded += ContextHelpHost_Loaded;
        Unloaded += ContextHelpHost_Unloaded;
    }

    public void SetTarget(FrameworkElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(_target, target))
        {
            return;
        }

        DetachTarget();
        _target = target;
        if (_loaded)
        {
            AttachTarget();
        }
    }

    public void SetHelp(string? purpose, string? disabledReason)
    {
        _purpose = Normalize(purpose);
        _disabledReason = Normalize(disabledReason);
        RefreshToolTip();
    }

    private void ContextHelpHost_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        if (_target is null)
        {
            foreach (var child in Children)
            {
                if (child is FrameworkElement target)
                {
                    _target = target;
                    break;
                }
            }
        }
        AttachTarget();
    }

    private void ContextHelpHost_Unloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        DetachTarget();
    }

    private void AttachTarget()
    {
        if (_target is not Control control || _isEnabledChangedToken != 0)
        {
            RefreshToolTip();
            return;
        }

        _isEnabledChangedToken = control.RegisterPropertyChangedCallback(
            Control.IsEnabledProperty,
            (_, _) => RefreshToolTip());
        RefreshToolTip();
    }

    private void DetachTarget()
    {
        if (_target is Control control && _isEnabledChangedToken != 0)
        {
            control.UnregisterPropertyChangedCallback(Control.IsEnabledProperty, _isEnabledChangedToken);
        }

        _isEnabledChangedToken = 0;
    }

    private void RefreshToolTip()
    {
        var text = _target is Control { IsEnabled: false } && !string.IsNullOrEmpty(_disabledReason)
            ? _disabledReason
            : string.Empty;
        AutomationProperties.SetHelpText(this, string.IsNullOrEmpty(text) ? _purpose : text);
        ToolTipService.SetToolTip(this, string.IsNullOrEmpty(text) ? null : ContextHelp.CreateToolTip(text));
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
