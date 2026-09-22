using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinPool.Application;
using Windows.System;

namespace WinPool_App.Controls;

/// <summary>
/// A compact notification surface. Completed messages leave automatically;
/// tapping a normal card dismisses it, while an error asks the window to show
/// its readable message without exposing diagnostic metadata outside the
/// Developer page.
/// </summary>
public sealed partial class NotificationCard : UserControl
{
    private const int MaximumCardMessageLength = 280;

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

    public event EventHandler<NotificationCardEventArgs>? Invoked;

    private static void OnNotificationChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((NotificationCard)dependencyObject).UpdateNotification();

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
            return Shorten(notification.Message, MaximumCardMessageLength);
        }

        // Keep the required route to details visible even when an error body is
        // long. The original, untruncated message is available on click and in
        // the Developer page history.
        var prompt = Text("进入开发页查看详情", "Open Developer features for details");
        var messageLength = Math.Max(0, MaximumCardMessageLength - prompt.Length - 1);
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
