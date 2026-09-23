using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinPool.Application;
using Windows.System;
using Windows.UI.ViewManagement;

namespace WinPool_App.Controls;

/// <summary>
/// A compact notification surface. Completed messages leave automatically;
/// tapping a normal card dismisses it, while an error asks the window to show
/// its readable message without exposing diagnostic metadata outside the
/// Developer page.
/// </summary>
public sealed partial class NotificationCard : UserControl
{
    private const int MaximumChineseCardTitleLength = 18;
    private const int MaximumEnglishCardTitleLength = 30;
    private bool _dismissAnimationStarted;
    private readonly UISettings _uiSettings = new();

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

    public static readonly DependencyProperty IsDismissingProperty = DependencyProperty.Register(
        nameof(IsDismissing),
        typeof(bool),
        typeof(NotificationCard),
        new PropertyMetadata(false, OnIsDismissingChanged));

    public NotificationCard()
    {
        InitializeComponent();
        UpdateNotification();
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

    public bool IsDismissing
    {
        get => (bool)GetValue(IsDismissingProperty);
        set => SetValue(IsDismissingProperty, value);
    }

    public event EventHandler<NotificationCardEventArgs>? Invoked;

    public event EventHandler? ExitAnimationCompleted;

    private static void OnNotificationChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((NotificationCard)dependencyObject).UpdateNotification();

    private static void OnDisplayLanguageChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((NotificationCard)dependencyObject).UpdateNotification();

    private static void OnIsDismissingChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((NotificationCard)dependencyObject).StartDismissAnimation();

    private void StartDismissAnimation()
    {
        if (!IsDismissing || _dismissAnimationStarted)
        {
            return;
        }

        _dismissAnimationStarted = true;
        IsHitTestVisible = false;
        IsTabStop = false;
        if (!_uiSettings.AnimationsEnabled)
        {
            NotificationCardTranslation.X = 420;
            QueueExitAnimationCompleted();
            return;
        }

        try
        {
            DismissStoryboard.Begin();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException)
        {
            NotificationCardTranslation.X = 420;
            QueueExitAnimationCompleted();
        }
    }

    private void QueueExitAnimationCompleted() =>
        DispatcherQueue.TryEnqueue(() => ExitAnimationCompleted?.Invoke(this, EventArgs.Empty));

    private void DismissStoryboard_Completed(object? sender, object e) =>
        ExitAnimationCompleted?.Invoke(this, EventArgs.Empty);

    private void UpdateNotification()
    {
        if (Notification is not { } notification)
        {
            return;
        }

        NotificationInfoBar.Severity = NotificationSeverityConverter.ToInfoBarSeverity(notification.Severity);
        NotificationInfoBar.Title = Shorten(
            notification.Title,
            IsChinese ? MaximumChineseCardTitleLength : MaximumEnglishCardTitleLength);
        NotificationInfoBar.Message = DisplayMessage(notification);
        var action = notification.Severity == GlobalNotificationSeverity.Error
            ? Text("单击查看此错误消息；完整详情在开发页。", "Click to view this error message; full details are on the Developer page.")
            : Text("单击关闭此消息。", "Click to dismiss this message.");
        ToolTipService.SetToolTip(this, action);
        AutomationProperties.SetName(
            this,
            string.IsNullOrWhiteSpace(NotificationInfoBar.Message)
                ? notification.Title
                : $"{notification.Title}: {NotificationInfoBar.Message}");
        AutomationProperties.SetHelpText(this, action);
    }

    private void NotificationCard_Tapped(object sender, TappedRoutedEventArgs e) => Invoke();

    private void NotificationCard_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            e.Handled = true;
            Invoke();
        }
    }

    private void Invoke()
    {
        if (Notification is { } notification)
        {
            Invoked?.Invoke(this, new NotificationCardEventArgs(notification));
        }
    }

    private string DisplayMessage(GlobalNotification notification)
    {
        if (notification.Severity != GlobalNotificationSeverity.Error)
        {
            return Shorten(notification.Message, IsChinese ? 44 : 80);
        }

        // Keep the required route to details visible even when an error body is
        // long. The original, untruncated message is available on click and in
        // the Developer page history.
        var prompt = Text("进入开发页查看详情", "Open Developer features for details");
        var maximumErrorTextLength = IsChinese ? 58 : 108;
        var messageLength = Math.Max(0, maximumErrorTextLength - prompt.Length - 1);
        return string.IsNullOrWhiteSpace(notification.Message)
            ? prompt
            : $"{Shorten(notification.Message, messageLength)}\n{prompt}";
    }

    private static string Shorten(string? text, int maximum)
    {
        text ??= string.Empty;
        if (text.Length <= maximum)
        {
            return text;
        }

        return maximum <= 1 ? "…" : text[..(maximum - 1)] + "…";
    }

    private string Text(string chinese, string english) => IsChinese ? chinese : english;
}

public sealed class NotificationCardEventArgs(GlobalNotification notification) : EventArgs
{
    public GlobalNotification Notification { get; } = notification;
}
