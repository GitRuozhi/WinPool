using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;
using WinPool.Domain;
using SimulationEditRequest = WinPool.Application.SimulationEditRequest;
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
    protected SimulationEditingSession EditingSession { get; } = new();
    protected StorageSnapshot _working { get => EditingSession.Working; set => EditingSession.Working = value; }

    protected const double MinTopologyWidth = 320;
    protected const double TopologyWidthMargin = 20;

    protected long UnallocatedIgnoreBytes =>
        Math.Max(0, ViewModel.CurrentPreferences.PartitionIgnoreSizeBytes);

    protected string Text(string zh, string en) =>
        ViewModel.Localization.EffectiveLanguage == LanguagePreference.ZhCn ? zh : en;

    protected static long ParseSize(string token) =>
        token.Replace("KiB", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('K', 'k') is var digits && long.TryParse(digits, out var value)
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

    protected async Task<SimulationOperationResult?> ApplyAsync(SimulationEditRequest request)
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
            PublishOperationException(
                Text("操作失败", "Operation failed"),
                "editor",
                exception,
                "editor.apply.exception");
            return null;
        }

        if (!result.IsSuccess || result.Value is null)
        {
            PublishOperationResult(
                result.Status,
                result.Messages,
                result.CorrelationId,
                Text("操作未完成", "Operation did not complete"),
                "editor");
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
        return await DialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary
            ? input.Text.Trim()
            : null;
    }

    protected async Task<bool> ConfirmAsync(string title, string message)
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
            PrimaryButtonText = Text("确定", "OK"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await DialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary;
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
        await DialogCoordinator.ShowAsync(dialog);
    }

    /// <summary>Target text accompanies user-visible operation feedback.</summary>
    protected string OperationTarget => ViewModel.IsUsingSimulatedInventory
        ? Text($"模拟目标：{ViewModel.SelectedSystem.DisplayName}",
            $"Simulated target: {ViewModel.SelectedSystem.DisplayName}")
        : Text($"本机目标：{ViewModel.SelectedSystem.DisplayName}",
            $"Local target: {ViewModel.SelectedSystem.DisplayName}");

    protected void PublishOperationException(
        string title,
        string source,
        Exception exception,
        string code,
        bool showNotification = true)
    {
        PublishOperationFeedback(
            GlobalNotificationSeverity.Error,
            title,
            Text(
                "操作未完成。请检查当前选择、连接或权限后重试。",
                "The operation did not complete. Check the current selection, connection, or permissions, then try again."),
            source,
            code,
            $"{exception.GetType().Name}: {exception.Message}",
            showNotification);
    }

    protected void PublishOperationResult(
        ApplicationStatus status,
        IReadOnlyList<ApplicationMessage> messages,
        CorrelationId correlationId,
        string title,
        string source,
        bool showNotification = true)
    {
        var first = messages.FirstOrDefault();
        var outcomeUnknown = status == ApplicationStatus.OutcomeUnknown;
        var message = outcomeUnknown
            ? Text(
                "操作结果尚未确认。请刷新当前模拟状态后再决定是否重试。",
                "The operation outcome is not confirmed. Refresh the current simulation state before deciding whether to retry.")
            : Text(
                "操作未完成。请检查当前选择或条件后重试。",
                "The operation did not complete. Check the current selection or conditions, then try again.");
        var detail = first is null
            ? $"status={status}; correlation={correlationId.Value}"
            : $"status={status}; correlation={correlationId.Value}; {first.Code}: {first.DiagnosticText}";
        PublishOperationFeedback(
            outcomeUnknown ? GlobalNotificationSeverity.Warning : GlobalNotificationSeverity.Error,
            title,
            message,
            source,
            first?.Code is { Length: > 0 } value ? value : $"{source}.{status}",
            detail,
            showNotification,
            occurrenceKey: outcomeUnknown ? $"{source}:outcome-unknown:{correlationId.Value}" : null);
    }

    protected void PublishOperationFeedback(
        GlobalNotificationSeverity severity,
        string title,
        string message,
        string source,
        string code,
        string? detail = null,
        bool showNotification = true,
        string? occurrenceKey = null)
    {
        ViewModel.NotificationService.Publish(
            severity,
            title,
            $"{OperationTarget}{Environment.NewLine}{message}",
            source,
            new GlobalNotificationOptions
            {
                OccurrenceKey = occurrenceKey,
                ShowNotification = showNotification,
                RecordInHistory = true,
                Code = code,
                SystemId = ViewModel.SelectedSystem.Id,
                Target = OperationTarget,
                Detail = detail ?? string.Empty
            });
    }
}
