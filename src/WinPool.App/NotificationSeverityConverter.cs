using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using WinPool.Core;

namespace WinPool_App;

public sealed class NotificationSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            GlobalNotificationSeverity.Error => InfoBarSeverity.Error,
            GlobalNotificationSeverity.Warning => InfoBarSeverity.Warning,
            GlobalNotificationSeverity.Info => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Informational
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
