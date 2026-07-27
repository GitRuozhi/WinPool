using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Core;
using WinPool.Infrastructure.Windows;

namespace WinPool_App;

public sealed partial class MonitorPage : Page
{
    private DiskPerformanceSampler? _sampler;
    private DispatcherQueueTimer? _timer;

    public MonitorPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var viewModel = (WorkspaceViewModel)e.Parameter;
        MonitorTitle.Text = viewModel.Localization["Monitor"];
        MonitorIntro.Text = viewModel.Localization["MonitorIntro"];
        HeaderDisk.Text = viewModel.Localization["Disk"];
        HeaderActivity.Text = viewModel.Localization.Language == LanguagePreference.ZhCn ? "活动时间" : "Active time";
        HeaderRead.Text = viewModel.Localization.Language == LanguagePreference.ZhCn ? "读取速度" : "Read";
        HeaderWrite.Text = viewModel.Localization.Language == LanguagePreference.ZhCn ? "写入速度" : "Write";

        _sampler = new DiskPerformanceSampler();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        _sampler?.Dispose();
        _sampler = null;
        base.OnNavigatedFrom(e);
    }

    private void Refresh()
    {
        if (_sampler is null)
        {
            return;
        }
        DiskRows.ItemsSource = _sampler.Sample()
            .Select(x => new MonitorRow(
                x.InstanceName,
                x.ActivityPercent,
                $"{x.ActivityPercent:F0}%",
                FormatRate(x.ReadBytesPerSecond),
                FormatRate(x.WriteBytesPerSecond)))
            .ToArray();
    }

    private static string FormatRate(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024
            ? $"{bytesPerSecond / 1024 / 1024:F1} MB/s"
            : $"{bytesPerSecond / 1024:F0} KB/s";

    private sealed record MonitorRow(
        string InstanceName,
        double ActivityPercent,
        string ActivityText,
        string ReadText,
        string WriteText);
}
