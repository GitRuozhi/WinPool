using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
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
    private InputNonClientPointerSource? _nonClientPointerSource;
    private string _mascotKey = "00";

    public WelcomeWindow(
        LocalizationService localization,
        IWelcomeMascotSelector? mascotSelector = null)
    {
        ArgumentNullException.ThrowIfNull(localization);
        InitializeComponent();

        Title = localization["WelcomeTitle"];
        WelcomeTitleText.Text = localization["WelcomeTitle"];
        SetMessageText(WelcomeMessageText, localization["WelcomeMessage"]);
        ConfirmButton.Content = localization["WelcomeConfirm"];
        ApplyMascot((mascotSelector ?? new WelcomeMascotSelector()).SelectAssetKey());

        AppWindow.SetIcon("Assets/CAppIcon.ico");
        AppWindow.Resize(DefaultSize);
        AppWindowPlacement.CenterOnWorkArea(AppWindow);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        HideSystemWindowBorder();
        AppWindow.Changed += AppWindow_Changed;
    }

    private void HideSystemWindowBorder()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        const int gwlStyle = -16;
        const int gwlExStyle = -20;
        const long wsCaption = 0x00C00000L;
        const long wsThickFrame = 0x00040000L;
        const long wsBorder = 0x00800000L;
        const long wsDlgFrame = 0x00400000L;
        const long wsExWindowEdge = 0x00000100L;
        const long wsExClientEdge = 0x00000200L;
        const long wsExStaticEdge = 0x00020000L;
        const int dwmwaBorderColor = 34;
        const int dwmwaCaptionColor = 35;
        const int dwmwaWindowCornerPreference = 33;
        const uint dwmwaColorNone = 0xFFFFFFFE;
        const uint dwmwcpRound = 2;

        var style = GetWindowLongPtr(hwnd, gwlStyle);
        style = (nint)((long)style & ~(wsCaption | wsThickFrame | wsBorder | wsDlgFrame));
        SetWindowLongPtr(hwnd, gwlStyle, style);

        var exStyle = GetWindowLongPtr(hwnd, gwlExStyle);
        exStyle = (nint)((long)exStyle & ~(wsExWindowEdge | wsExClientEdge | wsExStaticEdge));
        SetWindowLongPtr(hwnd, gwlExStyle, exStyle);

        var none = dwmwaColorNone;
        var round = dwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, dwmwaBorderColor, ref none, sizeof(uint));
        _ = DwmSetWindowAttribute(hwnd, dwmwaCaptionColor, ref none, sizeof(uint));
        _ = DwmSetWindowAttribute(hwnd, dwmwaWindowCornerPreference, ref round, sizeof(uint));
        _ = SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange)
        {
            var size = sender.Size;
            if (size.Width < MinimumSize.Width || size.Height < MinimumSize.Height)
            {
                sender.Resize(new SizeInt32(
                    Math.Max(size.Width, MinimumSize.Width),
                    Math.Max(size.Height, MinimumSize.Height)));
            }
        }

        UpdateNonClientRegions();
    }

    private void RootLayout_Loaded(object sender, RoutedEventArgs e)
    {
        HideSystemWindowBorder();
        UpdateNonClientRegions();
    }

    private void RootLayout_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateNonClientRegions();

    private void UpdateNonClientRegions()
    {
        if (RootLayout.XamlRoot is null)
        {
            return;
        }

        _nonClientPointerSource ??= InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        var scale = RootLayout.XamlRoot.RasterizationScale;
        var client = AppWindow.ClientSize;
        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.Caption,
            [new RectInt32(0, 0, Math.Max(1, client.Width), Math.Max(1, client.Height))]);
        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.Passthrough,
            [
                GetPhysicalRect(CloseButton, scale),
                GetPhysicalRect(CycleButton, scale),
                GetPhysicalRect(ConfirmButton, scale)
            ]);
    }

    private void ApplyMascot(string key)
    {
        var asset = WelcomeMascotCatalog.ByKey(key);
        _mascotKey = asset.Key;
        MascotImage.Source = new BitmapImage(new Uri(
            Path.Combine(AppContext.BaseDirectory, "Assets", "Welcome", $"{asset.Key}.png")))
        {
            DecodePixelWidth = 1440
        };
        CycleButton.Content = asset.Title;
    }

    private void CycleButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyMascot(WelcomeMascotCatalog.RandomKey(_mascotKey));
        CycleButton.UpdateLayout();
        UpdateNonClientRegions();
    }

    private static void SetMessageText(TextBlock target, string text)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(
            text ?? string.Empty,
            @"(\*\*|~~)");
        var bold = false;
        var strike = false;
        foreach (var part in parts)
        {
            if (part == "**")
            {
                bold = !bold;
                continue;
            }
            if (part == "~~")
            {
                strike = !strike;
                continue;
            }
            if (part.Length == 0)
            {
                continue;
            }
            target.Inlines.Add(new Run
            {
                Text = part,
                FontWeight = bold
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                TextDecorations = strike
                    ? Windows.UI.Text.TextDecorations.Strikethrough
                    : Windows.UI.Text.TextDecorations.None
            });
        }
    }

    private static RectInt32 GetPhysicalRect(FrameworkElement element, double scale)
    {
        var bounds = element.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            Math.Max(1, (int)Math.Round(bounds.Width * scale)),
            Math.Max(1, (int)Math.Round(bounds.Height * scale)));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref uint attributeValue,
        int attributeSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

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
