using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinPool.Application;

namespace WinPool_App.Controls;

/// <summary>
/// Small, UI-only projection of one active application notification. The
/// window owns dismissal and lifetime policy; this control only reports user
/// interaction so short-notification timers can be paused safely.
/// </summary>
public sealed partial class NotificationCard : UserControl
{
    private const int MaximumCardMessageLength = 280;
    private bool _hasKeyboardFocus;
    private bool _isPointerInside;
    private bool? _lastPauseState;

    public static readonly DependencyProperty NotificationProperty = DependencyProperty.Register(
        nameof(Notification),
        typeof(GlobalNotification),
        typeof(NotificationCard),
        new PropertyMetadata(null, OnNotificationChanged));

    public static readonly DependencyProperty IsChineseProperty = DependencyProperty.Register(
        nameof(IsChinese),
        typeof(bool),
        typeof(NotificationCard),
        new PropertyMetadata(false, OnDisplayLanguageChanged));

    public NotificationCard()
    {
        InitializeComponent();
        Loaded += NotificationCard_Loaded;
        Unloaded += NotificationCard_Unloaded;
        UpdateLocalizedText();
    }

    public GlobalNotification? Notification
    {
        get => (GlobalNotification?)GetValue(NotificationProperty);
        set => SetValue(NotificationProperty, value);
    }

    public bool IsChinese
    {
        get => (bool)GetValue(IsChineseProperty);
        set => SetValue(IsChineseProperty, value);
    }

    public event EventHandler<NotificationCardEventArgs>? DismissRequested;

    public event EventHandler<NotificationCardEventArgs>? DetailsRequested;

    public event EventHandler<NotificationCardPauseRequestedEventArgs>? PauseRequested;

    private static void OnNotificationChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var control = (NotificationCard)dependencyObject;
        if (args.OldValue is GlobalNotification oldNotification)
        {
            var keepsSameNotification = args.NewValue is GlobalNotification replacementNotification
                && oldNotification.Id.Equals(replacementNotification.Id, StringComparison.Ordinal);
            if (!keepsSameNotification)
            {
                control.ReleasePause(oldNotification);
            }
        }
        control._lastPauseState = null;
        control.UpdateNotification();
    }

    private static void OnDisplayLanguageChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((NotificationCard)dependencyObject).UpdateNotification();

    private void UpdateNotification()
    {
        if (Notification is not { } notification)
        {
            return;
        }

        NotificationInfoBar.Severity = NotificationSeverityConverter.ToInfoBarSeverity(notification.Severity);
        NotificationInfoBar.Title = notification.Title;
        NotificationInfoBar.Message = Shorten(notification.Message);
        OccurrenceText.Text = notification.OccurrenceCount > 1
            ? $"×{notification.OccurrenceCount:N0}"
            : string.Empty;
        OccurrenceText.Visibility = notification.OccurrenceCount > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutomationProperties.SetName(
            this,
            string.IsNullOrWhiteSpace(notification.Message)
                ? notification.Title
                : $"{notification.Title}: {Shorten(notification.Message)}");
        UpdateLocalizedText();
        UpdatePauseState();
    }

    private void NotificationCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = true;
        UpdatePauseState();
    }

    private void NotificationCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = false;
        UpdatePauseState();
    }

    private void NotificationCard_GotFocus(object sender, RoutedEventArgs e)
    {
        _hasKeyboardFocus = true;
        UpdatePauseState();
    }

    private void NotificationCard_LostFocus(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            _hasKeyboardFocus = HasFocusedDescendant();
            UpdatePauseState();
        });

    private void NotificationCard_Loaded(object sender, RoutedEventArgs e) => UpdatePauseState();

    private void NotificationCard_Unloaded(object sender, RoutedEventArgs e)
    {
        _isPointerInside = false;
        _hasKeyboardFocus = false;
        if (Notification is { } notification)
        {
            ReleasePause(notification);
        }
    }

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Notification is { } notification)
        {
            DetailsRequested?.Invoke(this, new NotificationCardEventArgs(notification));
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Notification is { } notification)
        {
            DismissRequested?.Invoke(this, new NotificationCardEventArgs(notification));
        }
    }

    private void UpdateLocalizedText()
    {
        DetailsButtonText.Text = IsChinese ? "详情" : "Details";
        CloseButtonText.Text = IsChinese ? "关闭" : "Close";
        ToolTipService.SetToolTip(
            DetailsButton,
            IsChinese ? "查看及复制完整详情" : "View and copy full details");
        ToolTipService.SetToolTip(
            CloseButton,
            IsChinese ? "关闭通知" : "Dismiss notification");
        AutomationProperties.SetName(DetailsButton, IsChinese ? "通知详情" : "Notification details");
        AutomationProperties.SetName(CloseButton, IsChinese ? "关闭通知" : "Dismiss notification");
    }

    private void UpdatePauseState()
    {
        var isPaused = _isPointerInside || _hasKeyboardFocus;
        if (_lastPauseState == isPaused)
        {
            return;
        }

        _lastPauseState = isPaused;
        if (Notification is { AutoDismiss: true } notification)
        {
            PauseRequested?.Invoke(this, new NotificationCardPauseRequestedEventArgs(notification, isPaused));
        }
    }

    private void ReleasePause(GlobalNotification notification)
    {
        if (notification.AutoDismiss)
        {
            PauseRequested?.Invoke(this, new NotificationCardPauseRequestedEventArgs(notification, false));
        }

        _lastPauseState = false;
    }

    private bool HasFocusedDescendant()
    {
        if (XamlRoot is null)
        {
            return false;
        }

        for (var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }

        return false;
    }

    private static string Shorten(string text)
    {
        if (text.Length <= MaximumCardMessageLength)
        {
            return text;
        }

        return text[..(MaximumCardMessageLength - 1)] + "…";
    }
}

public sealed class NotificationCardEventArgs(GlobalNotification notification) : EventArgs
{
    public GlobalNotification Notification { get; } = notification;
}

public sealed class NotificationCardPauseRequestedEventArgs(
    GlobalNotification notification,
    bool isPaused) : EventArgs
{
    public GlobalNotification Notification { get; } = notification;

    public bool IsPaused { get; } = isPaused;
}
