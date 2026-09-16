using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace WinPool_App.Controls;

public sealed record PropertyTableCopyRow(string Label, string Value);

public sealed record PropertyTableContextMenuRequest(
    string CurrentValue,
    string GroupTitle,
    IReadOnlyList<PropertyTableCopyRow> GroupRows,
    string RawFields,
    string CopyCurrentValueText,
    string CopyGroupText,
    string RawFieldsText,
    string CloseText);

/// <summary>Provides the same explicit value, group, and raw-field actions for property tables.</summary>
public static class PropertyTableContextMenu
{
    public static void Show(
        FrameworkElement target,
        Windows.Foundation.Point pointerPosition,
        XamlRoot xamlRoot,
        PropertyTableContextMenuRequest request)
    {
        var flyout = new MenuFlyout();
        var copyCurrentValue = new MenuFlyoutItem { Text = request.CopyCurrentValueText };
        copyCurrentValue.Click += (_, _) => Copy(request.CurrentValue);
        flyout.Items.Add(copyCurrentValue);

        var copyGroup = new MenuFlyoutItem { Text = request.CopyGroupText };
        copyGroup.Click += (_, _) => Copy(FormatGroup(request.GroupRows));
        flyout.Items.Add(copyGroup);

        var rawFields = new MenuFlyoutItem { Text = request.RawFieldsText };
        rawFields.Click += async (_, _) => await ShowRawFieldsAsync(xamlRoot, request);
        flyout.Items.Add(rawFields);

        flyout.ShowAt(target, new FlyoutShowOptions { Position = pointerPosition });
    }

    public static string FormatGroup(IEnumerable<PropertyTableCopyRow> rows) =>
        string.Join(Environment.NewLine, rows.Select(row => $"{row.Label}: {row.Value}"));

    private static void Copy(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
    }

    private static async Task ShowRawFieldsAsync(
        XamlRoot xamlRoot,
        PropertyTableContextMenuRequest request)
    {
        await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"{request.GroupTitle} — {request.RawFieldsText}",
            CloseButtonText = request.CloseText,
            Content = new ScrollViewer
            {
                MaxHeight = 560,
                Content = new TextBlock
                {
                    Text = request.RawFields,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            }
        }.ShowAsync();
    }
}
