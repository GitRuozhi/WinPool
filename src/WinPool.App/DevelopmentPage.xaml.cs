using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App;

public sealed partial class DevelopmentPage : Page
{
    private readonly ObservableCollection<SessionMessageItem> _messages = [];
    private bool _subscribed;
    private Flyout? _detailFlyout;

    private sealed record SessionMessageItem(
        string Id,
        string CreatedAt,
        string Severity,
        string Source,
        string Summary,
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

        UpdateText();
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

        _detailFlyout?.Hide();
        _detailFlyout = null;

        // History intentionally remains in the process-local notification
        // service while the page is hidden. It is never backed by a file or DB.
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.CurrentPreferences))
        {
            UpdateText();
            RefreshMessages();
        }
    }

    private void History_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshMessages);

    private void UpdateText()
    {
        NoMessagesText.Text = Text("本次运行没有日志。", "No log entries in this run.");
        AiEntryHint.Text = Text("人工智能入口，功能正在开发中。", "AI entry — feature in development.");
        AutomationProperties.SetName(MessageList, Text("本次运行日志", "Current-session logs"));
    }

    private void DevelopmentLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 760;
        RightTopEmptyArea.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(LogArea, isNarrow ? 2 : 1);
    }

    private void RefreshMessages()
    {
        var messages = ViewModel.NotificationService.History
            .OrderByDescending(message => message.CreatedAt)
            .Select(message => new SessionMessageItem(
                message.Id,
                message.CreatedAt.LocalDateTime.ToString("HH:mm:ss"),
                FormatSeverity(message.Severity),
                message.Source,
                SingleLine(string.IsNullOrWhiteSpace(message.Message)
                    ? message.Title
                    : $"{message.Title} — {message.Message}"),
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

        NoMessagesText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MessageList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var row = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (row?.Content is not SessionMessageItem item)
        {
            return;
        }

        e.Handled = true;
        ShowMessageDetails(row, item.Notification);
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

    private void ShowMessageDetails(ListViewItem row, GlobalNotification message)
    {
        _detailFlyout?.Hide();
        var detailTextBox = new TextBox
        {
            AcceptsReturn = true,
            Height = Math.Clamp(DevelopmentLayout.ActualHeight - 80, 180, 320),
            Width = Math.Clamp(DevelopmentLayout.ActualWidth - 80, 300, 560),
            IsReadOnly = true,
            IsSpellCheckEnabled = false,
            Text = FormatMessageDetails(message),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(detailTextBox, Text("消息详情", "Message details"));
        var flyout = new Flyout
        {
            Content = detailTextBox,
            XamlRoot = DevelopmentLayout.XamlRoot
        };
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_detailFlyout, flyout))
            {
                _detailFlyout = null;
            }
        };
        _detailFlyout = flyout;
        flyout.ShowAt(row);
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

    private static string SingleLine(string text) =>
        string.Join(" ", text.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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
