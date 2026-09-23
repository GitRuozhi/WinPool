using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool_App.Controls;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App;

public sealed partial class DevelopmentPage : Page
{
    private readonly ObservableCollection<SessionMessageItem> _messages = [];
    private bool _subscribed;

    private sealed record SessionMessageItem(
        string Id,
        string CreatedAt,
        string Severity,
        string Source,
        string Title,
        string Message,
        GlobalNotification Notification);

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
            RefreshMessages();
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
        SessionMessagesTitle.Text = Text("日志", "Logs");
        SessionMessagesDescription.Text = Text(
            "仅保留本次运行最近 200 条内存消息。双击条目查看详情；退出 WinPool 后记录清空。",
            "Shows up to 200 in-memory messages from this run. Double-click an entry for details; history clears when WinPool exits.");
        DiagnosticsTitle.Text = Text("Diagnostics 路径", "Diagnostics path");
        CopyAllMessagesButtonText.Text = Text("复制全部", "Copy all");
        ClearMessagesButtonText.Text = Text("清空消息", "Clear messages");
        CopyDiagnosticsPathButtonText.Text = Text("复制路径", "Copy path");
        NoMessagesText.Text = Text("本次运行还没有消息。", "There are no messages in this run yet.");
        AiEntryHint.Text = Text("人工智能入口，功能正在开发中。", "AI entry — feature in development.");
        AutomationProperties.SetName(DiagnosticsPathText, Text("诊断日志路径", "Diagnostics log path"));
        AutomationProperties.SetName(MessageList, Text("本次运行日志", "Current-session logs"));
        AutomationProperties.SetName(CopyDiagnosticsPathButton, CopyDiagnosticsPathButtonText.Text);
        AutomationProperties.SetName(CopyAllMessagesButton, CopyAllMessagesButtonText.Text);
        AutomationProperties.SetName(ClearMessagesButton, ClearMessagesButtonText.Text);
        ContextHelp.Set(CopyAllMessagesButton, Text("复制当前保留的全部本次运行消息", "Copy all retained messages from this run"));
        ContextHelp.Set(CopyDiagnosticsPathButton, Text("复制当前 Diagnostics 路径", "Copy the current Diagnostics path"));
        ContextHelp.Set(ClearMessagesButton, Text("只清空本次运行消息，不会停止或修复持续异常。", "Clear only current-session messages; ongoing issues are not stopped or fixed."));
        ContextHelp.Set(
            DiagnosticsPathText,
            Text("只显示当前数据根中的路径，不读取日志正文。", "Shows only the current data-root path; log contents are not read."));
    }

    private void DevelopmentLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 760;
        RightTopEmptyArea.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(LogArea, isNarrow ? 2 : 1);
    }

    private void RefreshDiagnosticsPath()
    {
        DiagnosticsPathText.Text = ViewModel.DiagnosticsDirectoryPath;
        SetEnabledWithReason(
            CopyDiagnosticsPathButton,
            !string.IsNullOrWhiteSpace(DiagnosticsPathText.Text),
            Text("当前没有可复制的 Diagnostics 路径。", "There is no Diagnostics path to copy yet."));
        DiagnosticsStatusText.Text = ViewModel.DiagnosticsDirectoryExists
            ? Text("目录已存在", "Directory exists")
            : Text(
                "尚未生成",
                "Not generated yet");
    }

    private void RefreshMessages()
    {
        var selectedId = (MessageList.SelectedItem as SessionMessageItem)?.Id;
        var messages = ViewModel.NotificationService.History
            .OrderByDescending(message => message.CreatedAt)
            .Select(message => new SessionMessageItem(
                message.Id,
                message.CreatedAt.LocalDateTime.ToString("g"),
                FormatSeverity(message.Severity),
                message.Source,
                message.Title,
                message.Message,
                message))
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
        NoMessagesText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private async void MessageList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.Content
            is not SessionMessageItem item)
        {
            return;
        }

        e.Handled = true;
        await ShowMessageDetailsAsync(item.Notification);
    }

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private async Task ShowMessageDetailsAsync(GlobalNotification message)
    {
        var details = FormatMessageDetails(message);
        var detailTextBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 280,
            IsReadOnly = true,
            IsSpellCheckEnabled = false,
            Text = details,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(detailTextBox, Text("消息详情", "Message details"));

        var copyButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { FontSize = 14, Glyph = "\uE8C8" },
                    new TextBlock
                    {
                        Text = Text("复制详情", "Copy details"),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        AutomationProperties.SetName(copyButton, Text("复制详情", "Copy details"));
        ContextHelp.Set(copyButton, Text("复制完整消息详情到剪贴板", "Copy the complete message details to the clipboard"));
        copyButton.Click += (_, _) => CopyToClipboard(details);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(detailTextBox);
        content.Children.Add(copyButton);
        var dialog = new ContentDialog
        {
            RequestedTheme = DevelopmentLayout.RequestedTheme,
            Title = message.Title,
            Content = content,
            CloseButtonText = Text("关闭", "Close"),
            DefaultButton = ContentDialogButton.Close
        };
        await DialogCoordinator.ShowAsync(dialog, DevelopmentLayout.XamlRoot);
    }

    private void CopyAllMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        var separator = Environment.NewLine + Environment.NewLine + "――――" + Environment.NewLine + Environment.NewLine;
        var text = string.Join(separator, _messages.Select(item => FormatMessageDetails(item.Notification)));
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
