using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using WinPool.Application;

namespace WinPool_App;

public sealed class NotificationSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is GlobalNotificationSeverity severity
            ? ToInfoBarSeverity(severity)
            : InfoBarSeverity.Informational;

    public static InfoBarSeverity ToInfoBarSeverity(GlobalNotificationSeverity severity) =>
        severity switch
        {
            GlobalNotificationSeverity.Error => InfoBarSeverity.Error,
            GlobalNotificationSeverity.Warning => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational
        };

    public static IconSource ToIconSource(GlobalNotificationSeverity severity) => new FontIconSource
    {
        Glyph = severity switch
        {
            GlobalNotificationSeverity.Warning => "\uE7BA",
            GlobalNotificationSeverity.Error => "\uEA39",
            _ => "\uE946"
        }
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
