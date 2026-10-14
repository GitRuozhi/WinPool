namespace WinPool_App.Controls;

/// <summary>Shared default dimensions for the two editor property panels.</summary>
internal static class EditorFormMetrics
{
    public const double MinimumValueWidth = 144;
    public const double PageMargin = 12;
    public static double SidebarWidth(bool isChinese) => isChinese ? 520 : 568;
    public static double LabelWidth(bool isChinese) => isChinese ? 112 : 160;
}
