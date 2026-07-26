using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using WinPool.Core;

namespace WinPool_App;

public sealed class NotificationSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is GlobalNotificationSeverity.Error
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Warning;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
