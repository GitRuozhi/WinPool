namespace WinPool_App.Controls;

/// <summary>Shared default dimensions for the two editor property panels.</summary>
internal static class EditorFormMetrics
{
    public const double MinimumValueWidth = 112;
    public const double PageMargin = 8;
    public const double CompactActionButtonWidth = 24;
    public const double UnitColumnWidth = 28;
    public const double MaximumButtonWidth = 40;

    public static double SidebarWidth(bool isChinese) => isChinese ? 380 : 420;
    public static double LabelWidth(bool isChinese) => isChinese ? 84 : 112;
}
