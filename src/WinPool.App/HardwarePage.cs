using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App;

/// <summary>Read-only projection and source details. No provider matching or field selection occurs here.</summary>
public sealed partial class HardwarePage : Page
{
    private WorkspaceViewModel viewModel = null!;
    private readonly TextBlock title = new() { FontSize = 22, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Button refresh = new();
    private readonly Button cancel = new();
    private readonly ComboBox categories = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListView devices = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly StackPanel properties = new() { Spacing = 8 };
    private readonly Grid body = new() { ColumnSpacing = 12 };
    private WinPoolSystem? system;
    private CancellationTokenSource? capture;
    private bool active;

    public HardwarePage()
    {
        InitializeComponent();
        AutomationProperties.SetAutomationId(categories, "HardwareCategories");
        AutomationProperties.SetAutomationId(devices, "HardwareDevices");
        AutomationProperties.SetAutomationId(refresh, "HardwareRefresh");
        var root = new Grid { Padding = new Thickness(12), RowSpacing = 10 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(title);
        header.Children.Add(refresh);
        header.Children.Add(cancel);
        root.Children.Add(header);
        Grid.SetRow(status, 1); root.Children.Add(status);
        body.ColumnDefinitions.Add(new() { Width = new GridLength(230) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var selector = new Grid { RowSpacing = 8 };
        selector.RowDefinitions.Add(new() { Height = GridLength.Auto });
        selector.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        selector.Children.Add(categories);
        Grid.SetRow(devices, 1); selector.Children.Add(devices);
        body.Children.Add(selector);
        var scroll = new ScrollViewer { Content = properties, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
        Grid.SetRow(body, 2); root.Children.Add(body);
        Content = root;
        categories.SelectionChanged += (_, _) => ShowDevices();
        devices.SelectionChanged += (_, _) => ShowProperties();
        refresh.Click += Refresh_Click;
        cancel.Click += (_, _) => capture?.Cancel();
        SizeChanged += (_, _) => body.ColumnDefinitions[0].Width = new GridLength(ActualWidth < 700 ? 170 : 230);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        viewModel = (WorkspaceViewModel)e.Parameter;
        active = true;
        viewModel.PropertyChanged += Changed;
        viewModel.WorkspaceSelectionChanged += SelectionChanged;
        viewModel.Localization.PropertyChanged += Changed;
        Rebuild();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        capture?.Cancel();
        viewModel.PropertyChanged -= Changed;
        viewModel.WorkspaceSelectionChanged -= SelectionChanged;
        viewModel.Localization.PropertyChanged -= Changed;
        base.OnNavigatedFrom(e);
    }

    private void SelectionChanged(object? sender, EventArgs e) => Rebuild();
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, viewModel.Localization) || e.PropertyName is nameof(WorkspaceViewModel.SelectedSystem)
            or nameof(WorkspaceViewModel.Snapshot) or nameof(WorkspaceViewModel.IsScanning)) Rebuild();
    }

    private void Rebuild()
    {
        title.Text = viewModel.Localization["HardwareReadOnly"];
        refresh.Content = viewModel.Localization["HardwareRefresh"];
        cancel.Content = viewModel.Localization["Cancel"];
        refresh.IsEnabled = viewModel.SelectedSystem.IsLocal && !viewModel.IsScanning;
        cancel.IsEnabled = capture is not null;
        AutomationProperties.SetName(categories, viewModel.Localization["Hardware"]);
        AutomationProperties.SetName(devices, viewModel.Localization["HardwareReadOnly"]);
        system = viewModel.SelectedSystem.SourceFacts is { } facts ? new WinPoolSystem(facts) : null;
        status.Text = system is null ? viewModel.Localization["HardwareEmpty"] : string.Join(" · ", system.Collections.Select(x =>
            $"{(x.Purpose == CollectionPurpose.Storage ? viewModel.Localization["Manage"] : viewModel.Localization["Hardware"])}: {x.CompletedAt.LocalDateTime:G} ({ReadState(x.State)})"));
        var selected = (categories.SelectedItem as ComboBoxItem)?.Tag;
        categories.Items.Clear();
        categories.Items.Add(new ComboBoxItem { Content = viewModel.Localization.IsChinese ? "采集状态与来源" : "Collection status and sources", Tag = "sources" });
        foreach (var type in (system?.Objects.Select(x => x.ObjectType).Distinct() ?? Enumerable.Empty<FactObjectType>()).Order())
            categories.Items.Add(new ComboBoxItem { Content = TypeName(type), Tag = type });
        categories.SelectedItem = categories.Items.OfType<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, selected))
            ?? categories.Items.FirstOrDefault();
        ShowDevices();
    }

    private void ShowDevices()
    {
        var id = (devices.SelectedItem as ListViewItem)?.Tag is WinPoolObject previous ? previous.Id : null;
        devices.Items.Clear();
        if (categories.SelectedItem is ComboBoxItem { Tag: "sources" } && system is not null)
        {
            foreach (var row in WinPoolHardwarePresentation.SourceRows(system))
                devices.Items.Add(new ListViewItem { Content = $"{row.Source.ClassName} ({ReadState(row.Source.ReadState)})", Tag = row });
            devices.SelectedItem = devices.Items.FirstOrDefault();
            ShowProperties();
            return;
        }
        if (categories.SelectedItem is not ComboBoxItem { Tag: FactObjectType type } || system is null) { properties.Children.Clear(); return; }
        foreach (var item in system.Objects.Where(x => x.ObjectType == type))
            devices.Items.Add(new ListViewItem { Content = new TextBlock { Text = item.DisplayName, TextWrapping = TextWrapping.Wrap }, Tag = item });
        devices.SelectedItem = devices.Items.OfType<ListViewItem>().FirstOrDefault(x => x.Tag is WinPoolObject item && item.Id == id)
            ?? devices.Items.FirstOrDefault();
    }

    private void ShowProperties()
    {
        properties.Children.Clear();
        if (devices.SelectedItem is ListViewItem { Tag: WinPoolHardwareSourceRow row })
        {
            properties.Children.Add(new TextBlock { Text = $"{row.Source.Namespace} / {row.Source.ClassName}\n{ReadState(row.Source.ReadState)}\n"
                + $"{row.Source.CapturedAt.LocalDateTime:G}\n{row.Source.ReasonCode}\n"
                + (viewModel.Localization.IsChinese ? $"对象数：{row.ObjectCount}" : $"Objects: {row.ObjectCount}")
                + (row.RetainedAt is { } retained ? "\n" + (viewModel.Localization.IsChinese ? "保留证据：" : "Retained evidence: ") + retained.LocalDateTime.ToString("G") : ""),
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            return;
        }
        if (devices.SelectedItem is not ListViewItem { Tag: WinPoolObject item } || system is null) return;
        foreach (var observation in item.Sources)
        foreach (var field in observation.Fields)
        {
            var source = system.Sources.First(x => x.Id == field.SourceRef);
            var value = field.DisplayValue();
            if (field.Unit == "bytes" && field.TryGetInt64(out var bytes)) value = TopologyProjector.FormatBytes(bytes);
            var label = WinPoolHardwarePresentation.FieldName(field.Name, viewModel.Localization.IsChinese);
            var state = ReadState(field.ReadState);
            var selection = WinPoolSourceDetails.Select(item, field.Name);
            var details = $"{source.ClassName}.{field.Name}\n{viewModel.Localization["SourceRawValue"]}: {field.DisplayValue()} {field.Unit}\n{state}\n"
                + $"{source.Origin}: {source.Namespace} / {source.ClassName}\n{source.CapturedAt.LocalDateTime:G}"
                + (field.ReasonCode is null ? "" : $"\n{field.ReasonCode}")
                + (selection.Candidates.Length > 1 ? $"\nWinPool.{field.Name}: {selection.Reason}" : "");
            properties.Children.Add(new Expander
            {
                Header = new TextBlock { Text = $"{label} — {value} ({state})", TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
            });
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.SelectedSystem.IsLocal || capture is not null) return;
        capture = new CancellationTokenSource();
        Rebuild();
        try { await viewModel.RefreshHardwareAsync(capture.Token); }
        finally { capture.Dispose(); capture = null; if (active) Rebuild(); }
    }

    private string ReadState(FieldReadState state) => viewModel.Localization["Source" + state];
    private string TypeName(FactObjectType type) => !viewModel.Localization.IsChinese ? type.ToString() : type switch
    {
        FactObjectType.Computer => "计算机", FactObjectType.OperatingSystem => "操作系统", FactObjectType.StorageSubsystem => "存储子系统",
        FactObjectType.StoragePool => "存储池", FactObjectType.StorageTier => "存储层", FactObjectType.PhysicalDisk => "物理磁盘",
        FactObjectType.VirtualDisk => "虚拟磁盘", FactObjectType.Disk => "操作系统磁盘", FactObjectType.Partition => "分区",
        FactObjectType.Volume => "卷", FactObjectType.NetworkDisk => "网络磁盘", FactObjectType.LogicalDisk => "逻辑盘观察", FactObjectType.BaseBoard => "主板",
        FactObjectType.Bios => "固件", FactObjectType.Processor => "处理器", FactObjectType.CpuCache => "处理器缓存",
        FactObjectType.MemoryArray => "内存阵列", FactObjectType.MemoryModule => "内存", FactObjectType.PageFileSetting => "分页配置",
        FactObjectType.PageFileUsage => "分页使用", FactObjectType.VideoController => "显卡", FactObjectType.Monitor => "显示器",
        FactObjectType.NetworkAdapter => "网络适配器", FactObjectType.Battery => "电池", FactObjectType.HardwareSupplement => "硬件补充来源", _ => type.ToString()
    };
}
