using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace WinPool_App;

internal static class AppWindowPlacement
{
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
}
