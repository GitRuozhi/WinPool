using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPool.App.ViewModels;
using WinPool.Core;

namespace WinPool_App;

public sealed partial class TestPage : Page
{
    public TestPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var viewModel = (WorkspaceViewModel)e.Parameter;
        var zh = viewModel.Localization.Language == LanguagePreference.ZhCn;
        TestTitle.Text = viewModel.Localization["Test"];
        TestIntro.Text = viewModel.Localization["TestPageIntro"];
        TestStatus.Title = zh ? "开发中" : "Under development";
    }
}
