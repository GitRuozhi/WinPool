using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App;

public sealed partial class DevelopmentPage : Page
{
    private readonly ObservableCollection<SessionMessageItem> _messages = [];
    private bool _subscribed;
    private GlobalNotification? _displayedMessage;

    private sealed record SessionMessageItem(
        string Id,
        string CreatedAt,
        string Severity,
        string Source,
        string Title,
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

        CloseMessageDetails();

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
        EmptyMessageListText.Text = Text("本次运行没有消息。", "No messages in this run.");
        MessageTimeHeader.Text = Text("时间", "Time");
        MessageSeverityHeader.Text = Text("级别", "Level");
        MessageSourceHeader.Text = Text("来源", "Source");
        MessageTitleHeader.Text = Text("信息标题", "Message title");
        AiEntryHint.Text = Text("人工智能入口，功能正在开发中。", "AI entry — feature in development.");
        AutomationProperties.SetName(MessageListArea, Text("消息列表", "Message list"));
        AutomationProperties.SetName(MessageList, Text("消息列表", "Message list"));
        AutomationProperties.SetName(EmptyMessageListText, Text("空消息列表", "Empty message list"));
        AutomationProperties.SetName(
            TopAreaColumnSplitter,
            Text("调整消息列表和右侧区域宽度", "Resize the message list and right area"));
        AutomationProperties.SetName(
            TopBottomAreaSplitter,
            Text("调整上方与下方区域高度", "Resize the upper and lower areas"));

        if (_displayedMessage is { } message && MessageDetailOverlay.Visibility == Visibility.Visible)
        {
            MessageDetailText.Text = FormatMessageDetails(message);
            UpdateMessageDetailSize();
        }
    }

    private void DevelopmentLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 760;
        RightTopEmptyArea.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        TopAreaColumnSplitter.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        MessageListColumn.MinWidth = isNarrow ? 0 : 280;
        TopColumnSplitterColumn.Width = new GridLength(isNarrow ? 0 : 8);
        RightAreaColumn.MinWidth = isNarrow ? 0 : 240;
        RightAreaColumn.Width = isNarrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumnSpan(MessageListArea, isNarrow ? 3 : 1);

        if (MessageDetailOverlay.Visibility == Visibility.Visible)
        {
            UpdateMessageDetailSize();
        }
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
                SingleLine(message.Title),
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

        EmptyMessageListText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMessageRowSelectionVisuals();
    }

    private void MessageList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var row = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (row?.Content is not SessionMessageItem item)
        {
            return;
        }

        e.Handled = true;
        ShowMessageDetails(item.Notification);
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

    private void ShowMessageDetails(GlobalNotification message)
    {
        _displayedMessage = message;
        MessageDetailText.Text = FormatMessageDetails(message);
        AutomationProperties.SetName(MessageDetailText, Text("消息详情文本", "Message details text"));
        UpdateMessageDetailSize();
        MessageDetailOverlay.Visibility = Visibility.Visible;
        MessageDetailText.Focus(FocusState.Programmatic);
    }

    private void UpdateMessageDetailSize()
    {
        var width = Math.Clamp(DevelopmentLayout.ActualWidth - 48, 160, 640);
        var maxHeight = Math.Clamp(DevelopmentLayout.ActualHeight - 48, 96, 380);
        var textMeasure = new TextBlock
        {
            FontFamily = MessageDetailText.FontFamily,
            FontSize = MessageDetailText.FontSize,
            Text = MessageDetailText.Text,
            TextWrapping = TextWrapping.Wrap
        };
        var contentWidth = Math.Max(96, width - 32);
        textMeasure.Measure(new Size(contentWidth, double.PositiveInfinity));

        MessageDetailText.Width = width;
        MessageDetailText.Height = Math.Min(
            maxHeight,
            Math.Max(96, Math.Ceiling(textMeasure.DesiredSize.Height + 40)));
    }

    private void MessageDetailOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        CloseMessageDetails();
        e.Handled = true;
    }

    private void CloseMessageDetails()
    {
        MessageDetailOverlay.Visibility = Visibility.Collapsed;
        _displayedMessage = null;
        MessageList.Focus(FocusState.Programmatic);
    }

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateMessageRowSelectionVisuals();

    private void MessageRowVisual_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid row)
        {
            return;
        }

        var isSelected = FindAncestor<ListViewItem>(row)?.IsSelected == true;
        if (FindNamedElement<Border>(row, "MessageRowHoverFill") is { } hoverFill)
        {
            hoverFill.Opacity = isSelected ? 0 : 1;
        }
    }

    private void MessageRowVisual_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid row && FindNamedElement<Border>(row, "MessageRowHoverFill") is { } hoverFill)
        {
            hoverFill.Opacity = 0;
        }
    }

    private void UpdateMessageRowSelectionVisuals()
    {
        foreach (var item in MessageList.Items)
        {
            if (MessageList.ContainerFromItem(item) is not ListViewItem container)
            {
                continue;
            }

            var isSelected = ReferenceEquals(container.Content, MessageList.SelectedItem);
            if (FindNamedElement<Border>(container, "MessageRowSelectedFill") is { } selectedFill)
            {
                selectedFill.Opacity = isSelected ? 1 : 0;
            }

            if (FindNamedElement<Border>(container, "MessageRowSelectionIndicator") is { } indicator)
            {
                indicator.Opacity = isSelected ? 1 : 0;
            }

            if (isSelected
                && FindNamedElement<Border>(container, "MessageRowHoverFill") is { } hoverFill)
            {
                hoverFill.Opacity = 0;
            }
        }
    }

    private static T? FindNamedElement<T>(DependencyObject? element, string name)
        where T : FrameworkElement
    {
        if (element is T match && match.Name == name)
        {
            return match;
        }

        if (element is not null)
        {
            var childCount = VisualTreeHelper.GetChildrenCount(element);
            for (var index = 0; index < childCount; index++)
            {
                if (FindNamedElement<T>(VisualTreeHelper.GetChild(element, index), name) is { } childMatch)
                {
                    return childMatch;
                }
            }
        }

        return null;
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
