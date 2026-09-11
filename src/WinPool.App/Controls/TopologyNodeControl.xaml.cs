using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App.Controls;

public sealed partial class TopologyNodeControl : UserControl
{
    private static TopologyNodeControl? s_hoveredControl;
    private bool _wasSelected;
    private bool _isPointerOver;
    private bool _hasKeyboardFocus;

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(TopologyNodeViewModel),
        typeof(TopologyNodeControl),
        new PropertyMetadata(null, OnViewModelChanged));

    public TopologyNodeControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateSelectionVisual();
        ActualThemeChanged += (_, _) => UpdateSelectionVisual();
        DragStarting += TopologyNodeControl_DragStarting;
        DragOver += TopologyNodeControl_DragOver;
        DragLeave += TopologyNodeControl_DragLeave;
        Drop += TopologyNodeControl_Drop;
    }

    public TopologyNodeViewModel ViewModel
    {
        get => (TopologyNodeViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (TopologyNodeControl)dependencyObject;
        if (args.OldValue is TopologyNodeViewModel oldValue)
        {
            oldValue.PropertyChanged -= control.ViewModel_PropertyChanged;
        }
        if (args.NewValue is TopologyNodeViewModel newValue)
        {
            newValue.PropertyChanged += control.ViewModel_PropertyChanged;
        }
        control.Bindings.Update();
        control.CanDrag = args.NewValue is TopologyNodeViewModel drag && drag.IsDragSource;
        control.AllowDrop = true;
        control.UpdateSelectionVisual();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TopologyNodeViewModel.IsSelected)
            or nameof(TopologyNodeViewModel.IsExpanded))
        {
            UpdateSelectionVisual();
        }
    }

    private void UpdateSelectionVisual()
    {
        if (NodeBorder is null || ViewModel is null)
        {
            return;
        }

        var isContainer = ViewModel.IsInvisibleLayoutContainer;
        var childrenMargin = isContainer ? new Thickness(0) : new Thickness(6, 5, 6, 0);
        FlowChildren.Margin = childrenMargin;
        WeightedChildren.Margin = childrenMargin;
        StackChildren.Margin = childrenMargin;

        ApplyInteractionAppearance();

        if (ViewModel.IsSelected && !_wasSelected)
        {
            StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                HorizontalAlignmentRatio = 0.5,
                // A system root spans the whole system, so centering it would
                // push its header out of the viewport; align it to the top.
                VerticalAlignmentRatio =
                    ViewModel.Unit.Kind == WinPool.Application.StorageUnitKind.System ? 0 : 0.5
            });
        }

        _wasSelected = ViewModel.IsSelected;
        if (!IsLoaded)
        {
            // The control was created before it entered the tree (for example
            // after a system switch rebuilds the topology), so the
            // bring-into-view above cannot scroll yet. Retry on Loaded.
            _wasSelected = false;
        }
    }

    private void ApplyInteractionAppearance()
    {
        if (ViewModel.IsInvisibleLayoutContainer)
        {
            NodeBorder.Background = new SolidColorBrush(Colors.Transparent);
            NodeBorder.BorderBrush = new SolidColorBrush(Colors.Transparent);
            NodeBorder.BorderThickness = new Thickness(0);
            NodeBorder.Padding = new Thickness(0);
            NodeBorder.CornerRadius = new CornerRadius(0);
            InteractionBorder.Visibility = Visibility.Collapsed;
            return;
        }

        NodeBorder.BorderThickness = new Thickness(1);
        NodeBorder.Padding = new Thickness(6);
        NodeBorder.CornerRadius = new CornerRadius(2);

        if (ViewModel.IsSelected)
        {
            NodeBorder.Background = Brush("WinPoolAccentBrush");
            NodeBorder.BorderBrush = Brush("CardStrokeColorDefaultBrush");
            InteractionBorder.BorderBrush = Brush("WinPoolAccentBorderBrush");
            InteractionBorder.Visibility = Visibility.Visible;
            SetTextBrush(Brush("WinPoolAccentForegroundBrush"));
            return;
        }

        if (_isPointerOver || _hasKeyboardFocus)
        {
            NodeBorder.Background = Brush("WinPoolAccentHoverBrush");
            NodeBorder.BorderBrush = Brush("CardStrokeColorDefaultBrush");
            InteractionBorder.BorderBrush = Brush("WinPoolAccentBorderBrush");
            InteractionBorder.Visibility = Visibility.Visible;
            SetTextBrush(Brush("TextFillColorPrimaryBrush"));
            return;
        }

        NodeBorder.Background = Brush("LayerFillColorDefaultBrush");
        NodeBorder.BorderBrush = Brush("WinPoolTopologyRestStrokeBrush");
        InteractionBorder.Visibility = Visibility.Collapsed;
        DisplayNameText.Foreground = Brush("TextFillColorPrimaryBrush");
        TypeLabelText.Foreground = Brush("TextFillColorSecondaryBrush");
        SummaryText.Foreground = Brush("TextFillColorSecondaryBrush");
        var iconBrush = Brush("TextFillColorSecondaryBrush");
        TypeIcon.Foreground = iconBrush;
        SingleLineTypeIcon.Foreground = iconBrush;
        ExpandIcon.Foreground = iconBrush;
        WindowsMarkerSquare1.Fill = iconBrush;
        WindowsMarkerSquare2.Fill = iconBrush;
        WindowsMarkerSquare3.Fill = iconBrush;
        WindowsMarkerSquare4.Fill = iconBrush;
        SingleLineWindowsSquare1.Fill = iconBrush;
        SingleLineWindowsSquare2.Fill = iconBrush;
        SingleLineWindowsSquare3.Fill = iconBrush;
        SingleLineWindowsSquare4.Fill = iconBrush;
        SingleLineDisplayNameText.Foreground = Brush("TextFillColorPrimaryBrush");
        SingleLineSummaryText.Foreground = Brush("TextFillColorSecondaryBrush");
        ApplyStatusMarkBrushes(iconBrush, Brush("WinPoolAccentBrush"));
    }

    private static Brush Brush(string key) =>
        (Brush)Application.Current.Resources[key];

    private void SetTextBrush(Brush brush)
    {
        DisplayNameText.Foreground = brush;
        TypeLabelText.Foreground = brush;
        SummaryText.Foreground = brush;
        TypeIcon.Foreground = brush;
        SingleLineTypeIcon.Foreground = brush;
        SingleLineDisplayNameText.Foreground = brush;
        SingleLineSummaryText.Foreground = brush;
        ExpandIcon.Foreground = brush;
        WindowsMarkerSquare1.Fill = brush;
        WindowsMarkerSquare2.Fill = brush;
        WindowsMarkerSquare3.Fill = brush;
        WindowsMarkerSquare4.Fill = brush;
        SingleLineWindowsSquare1.Fill = brush;
        SingleLineWindowsSquare2.Fill = brush;
        SingleLineWindowsSquare3.Fill = brush;
        SingleLineWindowsSquare4.Fill = brush;
        ApplyStatusMarkBrushes(
            brush,
            ViewModel.IsSelected ? brush : Brush("WinPoolAccentBrush"));
    }

    private void ApplyStatusMarkBrushes(Brush presenceBrush, Brush pendingBrush)
    {
        StoredDataMark.Foreground = presenceBrush;
        CannotLeaveMark.Foreground = presenceBrush;
        PendingMark.Fill = pendingBrush;
    }

    private void TopologyNodeControl_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel?.IsInvisibleLayoutContainer == true)
        {
            return;
        }
        SetAsHovered();
        e.Handled = true;
    }

    private void TopologyNodeControl_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel?.IsInvisibleLayoutContainer == true)
        {
            return;
        }
        if (ReferenceEquals(s_hoveredControl, this))
        {
            s_hoveredControl = null;
            _isPointerOver = false;
            UpdateSelectionVisual();
            TransferHoverToAncestor(e);
        }

        e.Handled = true;
    }

    private void SetAsHovered()
    {
        if (ViewModel?.IsInvisibleLayoutContainer == true)
        {
            return;
        }
        if (s_hoveredControl is not null && !ReferenceEquals(s_hoveredControl, this))
        {
            s_hoveredControl._isPointerOver = false;
            s_hoveredControl.UpdateSelectionVisual();
        }

        s_hoveredControl = this;
        _isPointerOver = true;
        UpdateSelectionVisual();
    }

    private void TransferHoverToAncestor(PointerRoutedEventArgs e)
    {
        // PointerEntered does not fire again on an ancestor that already contains
        // the pointer, so leaving a child would otherwise leave no node hovered.
        for (var ancestor = FindParentTopologyNode();
             ancestor is not null;
             ancestor = ancestor.FindParentTopologyNode())
        {
            if (IsPointerInside(ancestor, e))
            {
                ancestor.SetAsHovered();
                return;
            }
        }
    }

    private TopologyNodeControl? FindParentTopologyNode()
    {
        DependencyObject? current = VisualTreeHelper.GetParent(this);
        while (current is not null)
        {
            if (current is TopologyNodeControl parent)
            {
                return parent;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsPointerInside(TopologyNodeControl control, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(control).Position;
        return position.X >= 0
            && position.Y >= 0
            && position.X < control.ActualWidth
            && position.Y < control.ActualHeight;
    }

    private void NodeBorder_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ViewModel?.IsSelectable == true)
        {
            ViewModel.SelectCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void NodeBorder_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (ViewModel?.IsSelectable == true && sender is FrameworkElement element)
        {
            ViewModel.RequestContextMenu(element, e.GetPosition(element));
        }
        e.Handled = true;
    }

    private void ExpandButton_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        e.Handled = true;

    private void TopologyNodeControl_GotFocus(object sender, RoutedEventArgs e)
    {
        _hasKeyboardFocus = true;
        UpdateSelectionVisual();
    }

    private void TopologyNodeControl_LostFocus(object sender, RoutedEventArgs e)
    {
        _hasKeyboardFocus = false;
        UpdateSelectionVisual();
    }

    private void TopologyNodeControl_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if (ViewModel?.IsDragSource != true)
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetText(ViewModel.Unit.StableId);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private static TopologyNodeControl? s_highlightedDropTarget;

    private void TopologyNodeControl_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        var target = FindPoolDropTargetControl();
        if (target is null)
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
        if (!ReferenceEquals(s_highlightedDropTarget, target))
        {
            s_highlightedDropTarget?.ShowDropTargetVisual(false);
            target.ShowDropTargetVisual(true);
            s_highlightedDropTarget = target;
        }
    }

    private void TopologyNodeControl_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(s_highlightedDropTarget, this))
        {
            ShowDropTargetVisual(false);
            s_highlightedDropTarget = null;
        }
    }

    private void ShowDropTargetVisual(bool on)
    {
        if (DropTargetBorder is not null)
        {
            DropTargetBorder.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void TopologyNodeControl_Drop(object sender, DragEventArgs e)
    {
        var poolId = FindPoolDropTarget();
        if (poolId is null || !e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        var diskId = await e.DataView.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(diskId))
        {
            ViewModel.EditInteraction?.OnDiskDropped?.Invoke(diskId, poolId);
        }

        s_highlightedDropTarget?.ShowDropTargetVisual(false);
        s_highlightedDropTarget = null;
        e.Handled = true;
    }

    private string? FindPoolDropTarget()
    {
        var target = FindPoolDropTargetControl();
        return target?.ViewModel?.Unit.StableId;
    }

    /// <summary>
    /// Resolves the pool card under the pointer. Only pool-kind nodes are
    /// drop targets — never the source pool via a fallback, and never a
    /// tier card (which previously lit up as a false target).
    /// </summary>
    private TopologyNodeControl? FindPoolDropTargetControl()
    {
        if (ViewModel?.IsDropTarget == true)
        {
            return this;
        }

        for (var ancestor = FindParentTopologyNode();
             ancestor is not null;
             ancestor = ancestor.FindParentTopologyNode())
        {
            if (ancestor.ViewModel?.IsDropTarget == true)
            {
                return ancestor;
            }
        }

        return null;
    }

    private void TopologyNodeControl_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel?.IsSelectable != true
            || e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space))
        {
            return;
        }

        ViewModel.SelectCommand.Execute(null);
        e.Handled = true;
    }
}
