using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinPool.Application;

namespace WinPool.App.ViewModels;

public sealed record CategoryItem(WorkspaceCategory Category, string Title, string Glyph);

public sealed record WorkspaceItem(
    string Key,
    string Title,
    StorageUnitRef? Unit = null,
    bool IsAction = false,
    string? StorageSystemId = null,
    ManageObjectListItemView? Projection = null);

public sealed record DetailRow(string Label, string Value);

public sealed record ComparisonColumn(
    string Key,
    string Name,
    IReadOnlyList<DetailRow> Rows);

public sealed record EditNavigationParameter(
    WorkspaceViewModel ViewModel,
    string? TargetStableId);

public enum ShellPageKind
{
    Manage,
    Create,
    Test,
    Monitor,
    Development,
    Settings
}

public sealed partial class ShellNavigationItem : ObservableObject
{
    public ShellNavigationItem(ShellPageKind page, string title, string glyph)
    {
        Page = page;
        Title = title;
        Glyph = glyph;
    }

    public ShellPageKind Page { get; }
    public string Glyph { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private Visibility _textVisibility = Visibility.Visible;

    [ObservableProperty]
    private double _itemWidth = double.NaN;

    [ObservableProperty]
    private Brush? _background;

    [ObservableProperty]
    private Brush? _foreground;
}
