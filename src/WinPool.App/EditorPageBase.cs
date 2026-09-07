using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using SimulationOperationRequest = WinPool.Application.SimulationEditRequest;
using SimulationOperationResult = WinPool.Application.SimulationEditReceipt;

namespace WinPool_App;

/// <summary>
/// Shared plumbing for the two V0.47 editor pages: the working simulation
/// snapshot, typed simulation-operation submission, small dialogs, and the
/// size parsing helpers used by both editors.
/// </summary>
public class EditorPageBase : Page
{
    protected WorkspaceViewModel ViewModel { get; set; } = null!;
    protected StorageSnapshot _working = StorageSnapshot.Empty("editor");

    protected const double MinTopologyWidth = 320;
    protected const double TopologyWidthMargin = 20;

    protected long UnallocatedIgnoreBytes =>
        Math.Max(0, ViewModel.CurrentPreferences.PartitionIgnoreSizeBytes);

    protected string Text(string zh, string en) =>
        ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? zh : en;

    protected static long ParseSize(string token) =>
        token.TrimEnd('K', 'k') is var digits && long.TryParse(digits, out var value)
            ? value * 1024
            : 65536;

    protected static int? ParseInt(string text) =>
        int.TryParse(text, out var value) ? value : null;

    protected static long? ParseGigabytes(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !TryParseGigabytes(text, out var gb))
        {
            return null;
        }

        return (long)(gb * 1024L * 1024L * 1024L);
    }

    protected static bool TryParseGigabytes(string text, out double gigabytes)
    {
        gigabytes = 0;
        if (!double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || !double.IsFinite(parsed)
            || parsed <= 0)
        {
            return false;
        }

        gigabytes = parsed;
        return true;
    }

    protected async Task<SimulationOperationResult?> ApplyAsync(SimulationOperationRequest request)
    {
        WinPool.Application.ApplicationResult<SimulationOperationResult> result;
        try
        {
            result = await ViewModel.ApplySimulationOperationAsync(request);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException)
        {
            await ShowMessageAsync(Text("操作失败", "Operation failed"), exception.Message);
            return null;
        }

        if (!result.IsSuccess || result.Value is null)
        {
            await ShowMessageAsync(
                Text("操作不可用", "Operation unavailable"),
                result.Messages.FirstOrDefault()?.UserTextKey
                    ?? Text("模拟操作未完成。", "The simulation operation did not complete."));
            return null;
        }

        return result.Value;
    }

    protected async Task<string?> PromptAsync(string title, string value)
    {
        var input = new TextBox { Text = value, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = Text("确定", "OK"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    protected async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = Text("确定", "OK"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    protected async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 500,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
            },
            CloseButtonText = Text("关闭", "Close")
        };
        await dialog.ShowAsync();
    }
}
