using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinPool.App.Services;
using WinPool.Application;
using WinRT.Interop;

namespace WinPool_App;

public sealed partial class WelcomeWindow : Window
{
    public static readonly SizeInt32 DefaultSize = new(720, 480);
    public static readonly SizeInt32 MinimumSize = new(720, 480);

    public WelcomeWindow(
        LocalizationService localization,
        IWelcomeMascotSelector? mascotSelector = null)
    {
        ArgumentNullException.ThrowIfNull(localization);
        InitializeComponent();

        Title = localization["WelcomeTitle"];
        WelcomeTitleText.Text = localization["WelcomeTitle"];
        WelcomeMessageText.Text = localization["WelcomeMessage"];
        ConfirmButton.Content = localization["WelcomeConfirm"];
        MascotImage.Source = new BitmapImage(new Uri(
            Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Welcome",
                $"{(mascotSelector ?? new WelcomeMascotSelector()).SelectAssetKey()}.png")))
        {
            DecodePixelWidth = 1440,
            DecodePixelHeight = 900
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(null);
        AppWindow.SetIcon("Assets/CAppIcon.ico");
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.Resize(DefaultSize);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        HideMinimizeMaximizeButtons();

        AppWindow.Changed += AppWindow_Changed;
    }

    private void HideMinimizeMaximizeButtons()
    {
        const int gwlStyle = -16;
        const long wsMinimizeBox = 0x00020000L;
        const long wsMaximizeBox = 0x00010000L;
        var handle = WindowNative.GetWindowHandle(this);
        var style = GetWindowLongPtr(handle, gwlStyle);
        style = (nint)((long)style & ~(wsMinimizeBox | wsMaximizeBox));
        SetWindowLongPtr(handle, gwlStyle, style);
        SetWindowPos(
            handle,
            nint.Zero,
            0,
            0,
            0,
            0,
            0x0020 | 0x0001 | 0x0002 | 0x0004);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        var size = sender.Size;
        if (size.Width >= MinimumSize.Width && size.Height >= MinimumSize.Height)
        {
            return;
        }

        sender.Resize(new SizeInt32(
            Math.Max(size.Width, MinimumSize.Width),
            Math.Max(size.Height, MinimumSize.Height)));
    }

    private void RootLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MascotImage.Width = RootLayout.ActualWidth;
        MascotImage.Height = RootLayout.ActualHeight;
    }

    private void RootLayout_PointerPressed(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootLayout);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void DragMove()
    {
        var handle = WindowNative.GetWindowHandle(this);
        ReleaseCapture();
        SendMessage(handle, 0x00A1, 0x0002, 0);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(
        nint windowHandle,
        int index,
        nint newValue);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);
}
