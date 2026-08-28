using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace WinPool_App;

internal static class AppWindowPlacement
{
    public static void CenterOnWorkArea(AppWindow window)
    {
        var display = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var size = window.Size;
        window.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
    }
}
