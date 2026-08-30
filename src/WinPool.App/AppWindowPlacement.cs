using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace WinPool_App;

internal static class AppWindowPlacement
{
    private const double DefaultDpi = 96.0;

    public static double GetWindowScale(Window window)
    {
        var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(window));
        return dpi == 0 ? 1 : dpi / DefaultDpi;
    }

    public static SizeInt32 ScaleLogicalSize(
        SizeInt32 logicalSize,
        double rasterizationScale)
    {
        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
        {
            rasterizationScale = 1;
        }

        return new SizeInt32(
            Math.Max(1, (int)Math.Round(logicalSize.Width * rasterizationScale)),
            Math.Max(1, (int)Math.Round(logicalSize.Height * rasterizationScale)));
    }

    public static void CenterOnWorkArea(AppWindow window)
    {
        var display = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var size = window.Size;
        window.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);
}
