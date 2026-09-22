using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool_App.Controls;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App;

public sealed partial class DevelopmentPage : Page
{
    private readonly ObservableCollection<GlobalNotification> _messages = [];
    private bool _subscribed;

    public DevelopmentPage()
    {
        InitializeComponent();
        MessageList.ItemsSource = _messages;
    }

    public WorkspaceViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (WorkspaceViewModel)e.Parameter;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ((INotifyCollectionChanged)ViewModel.NotificationService.History).CollectionChanged += History_CollectionChanged;
        _subscribed = true;

        // This only asks the ViewModel to raise path-state notifications. It
        // deliberately does not enumerate, read, or create Diagnostics.
        ViewModel.RefreshDiagnosticsDirectoryState();
        UpdateText();
        RefreshDiagnosticsPath();
        RefreshMessages();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_subscribed)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ((INotifyCollectionChanged)ViewModel.NotificationService.History).CollectionChanged -= History_CollectionChanged;
            _subscribed = false;
        }

        // History intentionally remains in the process-local notification
        // service while the page is hidden. It is never backed by a file or DB.
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.CurrentPreferences))
        {
            UpdateText();
            RefreshDiagnosticsPath();
        }
        else if (e.PropertyName is nameof(WorkspaceViewModel.DiagnosticsDirectoryPath)
            or nameof(WorkspaceViewModel.DiagnosticsDirectoryExists))
        {
            RefreshDiagnosticsPath();
        }
    }

    private void History_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshMessages);

    private void UpdateText()
    {
        PageTitle.Text = Text("开发者功能", "Developer features");
        RoadmapTitle.Text = "WinPool 2.0";
        RoadmapText.Text = Text(
            "完整开发者与 AI Agent 工作区不属于 WinPool 1.x，计划作为 WinPool 2.0 功能推出。",
            "The complete developer and AI Agent workspace is outside WinPool 1.x and is planned for WinPool 2.0.")
            + Environment.NewLine
            + Text(
                "WinPool 1.x 仅提供诊断路径和本次运行消息；不提供自由命令界面、公共自动化契约或完整开发者工作区。",
                "WinPool 1.x provides only a diagnostics path and current-session messages; it does not expose a free-form command surface, public automation contract, or complete developer workspace.");
        DiagnosticsTitle.Text = Text("诊断日志路径", "Diagnostics log path");
        DiagnosticsDescription.Text = Text(
            "此处只显示当前数据根的 Diagnostics 路径。可选择并复制；WinPool 不会读取日志正文或为显示该路径创建目录。",
            "This shows only the Diagnostics path under the current data root. It can be selected and copied; WinPool does not read log content or create the directory for this display.");
        SessionMessagesTitle.Text = Text("本次运行消息", "Current-session messages");
        SessionMessagesDescription.Text = Text(
            "仅保留内存中的最近 200 条消息。退出 WinPool 后清空；开发者页面隐藏时仍会继续记录。",
            "Only the latest 200 in-memory messages are retained. They clear when WinPool exits and continue recording while this developer page is hidden.");
        CopyAllMessagesButton.Content = Text("复制全部", "Copy all");
        ClearMessagesButton.Content = Text("清空消息", "Clear messages");
        CopySelectedMessageButton.Content = Text("复制所选详情", "Copy selected details");
        CopyDiagnosticsPathButton.Content = Text("复制路径", "Copy path");
        AutomationProperties.SetName(DiagnosticsPathText, Text("诊断日志路径", "Diagnostics log path"));
        AutomationProperties.SetName(MessageList, Text("本次运行消息", "Current-session messages"));
        AutomationProperties.SetName(SelectedMessageDetail, Text("所选消息详情", "Selected message details"));
        AutomationProperties.SetName(CopyDiagnosticsPathButton, (string)CopyDiagnosticsPathButton.Content);
        AutomationProperties.SetName(CopyAllMessagesButton, (string)CopyAllMessagesButton.Content);
        AutomationProperties.SetName(ClearMessagesButton, (string)ClearMessagesButton.Content);
        AutomationProperties.SetName(CopySelectedMessageButton, (string)CopySelectedMessageButton.Content);
        ContextHelp.Set(CopyAllMessagesButton, Text("复制当前显示的全部消息", "Copy all currently displayed messages"));
        ContextHelp.Set(CopyDiagnosticsPathButton, Text("复制当前 Diagnostics 路径", "Copy the current Diagnostics path"));
        ContextHelp.Set(ClearMessagesButton, Text("只清空本次运行消息。", "Clear only current-session messages."));
        ContextHelp.Set(CopySelectedMessageButton, Text("复制完整所选消息", "Copy the complete selected message"));
        // Keep the labels and the selected detail in the current UI language.
        UpdateSelectedMessage();
    }

    private void RefreshDiagnosticsPath()
    {
        DiagnosticsPathText.Text = ViewModel.DiagnosticsDirectoryPath;
        SetEnabledWithReason(
            CopyDiagnosticsPathButton,
            !string.IsNullOrWhiteSpace(DiagnosticsPathText.Text),
            Text("当前没有可复制的 Diagnostics 路径。", "There is no Diagnostics path to copy yet."));
        DiagnosticsStatusText.Text = ViewModel.DiagnosticsDirectoryExists
            ? Text("诊断目录已存在。", "The Diagnostics directory exists.")
            : Text(
                "尚未生成日志。显示该路径不会读取日志正文，也不会创建目录。",
                "No diagnostics directory has been generated yet. Displaying this path does not read log content or create the directory.");
    }

    private void RefreshMessages()
    {
        var selectedId = (MessageList.SelectedItem as GlobalNotification)?.Id;
        var messages = ViewModel.NotificationService.History
            .OrderByDescending(message => message.CreatedAt)
            .ToArray();
        if (!_messages.SequenceEqual(messages))
        {
            _messages.Clear();
            foreach (var message in messages)
            {
                _messages.Add(message);
            }
        }

        MessageList.SelectedItem = selectedId is null
            ? _messages.FirstOrDefault()
            : _messages.FirstOrDefault(message => message.Id.Equals(selectedId, StringComparison.Ordinal));
        UpdateSelectedMessage();
        var hasMessages = _messages.Count > 0;
        SetEnabledWithReason(
            CopyAllMessagesButton,
            hasMessages,
            Text("本次运行还没有可复制的消息。", "There are no current-session messages to copy."));
        SetEnabledWithReason(
            ClearMessagesButton,
            hasMessages,
            Text("本次运行还没有可清空的消息。", "There are no current-session messages to clear."));
    }

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectedMessage();

    private void UpdateSelectedMessage()
    {
        if (MessageList.SelectedItem is not GlobalNotification message)
        {
            SelectedMessageTitle.Text = Text("选择一条消息以查看完整详情", "Select a message to view its full details");
            SelectedMessageDetail.Text = string.Empty;
            SetEnabledWithReason(
                CopySelectedMessageButton,
                false,
                Text("请先选择一条消息。", "Select a message first."));
            return;
        }

        SelectedMessageTitle.Text = message.Title;
        SelectedMessageDetail.Text = FormatMessageDetails(message);
        SetEnabledWithReason(CopySelectedMessageButton, true, string.Empty);
    }

    private void CopyAllMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        var separator = Environment.NewLine + Environment.NewLine + "――――" + Environment.NewLine + Environment.NewLine;
        var text = string.Join(separator, _messages.Select(FormatMessageDetails));
        if (!string.IsNullOrWhiteSpace(text))
        {
            CopyToClipboard(text);
        }
    }

    private void CopyDiagnosticsPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(DiagnosticsPathText.Text))
        {
            CopyToClipboard(DiagnosticsPathText.Text);
        }
    }

    private void CopySelectedMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is GlobalNotification message)
        {
            CopyToClipboard(FormatMessageDetails(message));
        }
    }

    private void ClearMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NotificationService.ClearHistory();
        RefreshMessages();
    }

    private string FormatMessageDetails(GlobalNotification message)
    {
        var lines = new List<string>
        {
            message.Title,
            string.Empty
        };
        if (!string.IsNullOrWhiteSpace(message.Message))
        {
            lines.Add(message.Message);
        }
        if (!string.IsNullOrWhiteSpace(message.Detail)
            && !message.Detail.Equals(message.Message, StringComparison.Ordinal))
        {
            lines.Add(string.Empty);
            lines.Add(message.Detail);
        }

        AddDetailLine(lines, Text("来源", "Source"), message.Source);
        AddDetailLine(lines, Text("级别", "Severity"), FormatSeverity(message.Severity));
        AddDetailLine(lines, Text("代码", "Code"), message.Code);
        AddDetailLine(lines, Text("相关对象", "Related object"), message.Target);
        AddDetailLine(lines, Text("系统标识", "System ID"), message.SystemId);
        if (message.CreatedAt != default)
        {
            AddDetailLine(
                lines,
                Text("首次发生", "First occurred"),
                message.CreatedAt.LocalDateTime.ToString("g"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static void AddDetailLine(ICollection<string> lines, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add(label + ": " + value);
        }
    }

    private static void SetEnabledWithReason(Button button, bool enabled, string disabledReason)
    {
        button.IsEnabled = enabled;
        ContextHelp.SetDisabledReason(button, enabled ? null : disabledReason);
    }

    private static void CopyToClipboard(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private bool IsChinese => ViewModel.Localization.IsChinese;

    private string Text(string chinese, string english) => IsChinese ? chinese : english;

    private string FormatSeverity(GlobalNotificationSeverity severity) =>
        severity switch
        {
            GlobalNotificationSeverity.Error => Text("错误", "Error"),
            GlobalNotificationSeverity.Warning => Text("警告", "Warning"),
            _ => Text("信息", "Information")
        };
}
