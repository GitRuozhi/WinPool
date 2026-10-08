using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App.Controls;

public sealed partial class TopologyNodeControl : UserControl
{
    private static TopologyNodeControl? s_hoveredControl;
    private bool _wasSelected;
    private bool _isPointerOver;
    private bool _hasKeyboardFocus;
    private static TopologyNodeControl? s_activeDrag;
    private static TopologyNodeControl? s_highlightedDropTarget;
    private static long s_suppressTapUntil;
    private Pointer? _dragPointer;
    private Point _dragStart;
    private UIElement? _dragRoot;
    private TopologyEditInteraction? _dragInteraction;
    private bool _isDragging;
    private bool _suppressNextTap;

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(TopologyNodeViewModel),
        typeof(TopologyNodeControl),
        new PropertyMetadata(null, OnViewModelChanged));

    public TopologyNodeControl()
    {
        InitializeComponent();
        ContextHelp.Set(ExpandButton, "展开或折叠此结构节点。 / Expand or collapse this structure node.");
        ContextHelp.SetDisabledReason(ExpandButton, "当前节点没有可展开的子项。 / This node has no child items to expand.");
        Loaded += (_, _) => UpdateSelectionVisual();
        ActualThemeChanged += (_, _) => UpdateSelectionVisual();
        PointerPressed += TopologyNodeControl_PointerPressed;
        PointerMoved += TopologyNodeControl_PointerMoved;
        PointerReleased += TopologyNodeControl_PointerReleased;
        PointerCanceled += (_, _) => CancelInternalDrag();
        PointerCaptureLost += (_, _) => CancelInternalDrag();
        Unloaded += (_, _) => CancelInternalDrag();
    }

    public TopologyNodeViewModel ViewModel
    {
        get => (TopologyNodeViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (TopologyNodeControl)dependencyObject;
        control.CancelInternalDrag();
        if (args.OldValue is TopologyNodeViewModel oldValue)
        {
            oldValue.PropertyChanged -= control.ViewModel_PropertyChanged;
        }
        if (args.NewValue is TopologyNodeViewModel newValue)
        {
            newValue.PropertyChanged += control.ViewModel_PropertyChanged;
        }
        control.Bindings.Update();
        // This editor transfers a relationship inside this XamlRoot, never
        // data to the shell. Native data-transfer dragging fails elevated.
        control.CanDrag = false;
        control.AllowDrop = false;
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
        if (_suppressNextTap || Environment.TickCount64 < s_suppressTapUntil)
        {
            _suppressNextTap = false;
            e.Handled = true;
            return;
        }
        if (ViewModel?.IsSelectable == true)
        {
            ViewModel.SelectCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void NodeBorder_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (s_activeDrag is not null || Environment.TickCount64 < s_suppressTapUntil)
        {
            e.Handled = true;
            return;
        }
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

    private bool CanStartInternalDrag => IsLoaded && IsEnabled
        && ViewModel?.IsDragSource == true
        && ViewModel.EditInteraction is { OnDiskDropped: not null } interaction
        && (interaction.CanEditNow?.Invoke() ?? true);

    private static TopologyNodeControl? NearestTopologyNode(DependencyObject? element)
    {
        while (element is not null && element is not TopologyNodeControl)
            element = VisualTreeHelper.GetParent(element);
        return element as TopologyNodeControl;
    }

    private void TopologyNodeControl_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer is not null) return;
        _suppressNextTap = false;
        var buttons = e.GetCurrentPoint(this).Properties;
        if (!CanStartInternalDrag || ViewModel is not { } viewModel || XamlRoot?.Content is not UIElement root
            || !buttons.IsLeftButtonPressed || buttons.IsRightButtonPressed || buttons.IsMiddleButtonPressed
            || !ReferenceEquals(NearestTopologyNode(e.OriginalSource as DependencyObject), this)) return;
        // Expander/buttons keep their existing click behavior.
        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, this);
             element = VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        s_activeDrag?.CancelInternalDrag();
        if (!CapturePointer(e.Pointer)) return;
        _dragPointer = e.Pointer;
        _dragStart = e.GetCurrentPoint(root).Position;
        _dragRoot = root;
        root.KeyDown += DragRoot_KeyDown;
        _dragInteraction = viewModel.EditInteraction;
        s_activeDrag = this;
    }

    private void TopologyNodeControl_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer is null || e.Pointer.PointerId != _dragPointer.PointerId || _dragRoot is null) return;
        if (!CanStartInternalDrag || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            CancelInternalDrag();
            return;
        }
        var point = e.GetCurrentPoint(_dragRoot).Position;
        if (!_isDragging)
        {
            var dx = point.X - _dragStart.X;
            var dy = point.Y - _dragStart.Y;
            if (dx * dx + dy * dy < 64) return; // Eight DIPs; clicks remain clicks.
            _isDragging = true;
            _suppressNextTap = true;
            Focus(FocusState.Programmatic); // Escape now belongs to this drag.
        }
        e.Handled = true;
        var target = HitTestDropTarget(point);
        if (!ReferenceEquals(s_highlightedDropTarget, target))
        {
            s_highlightedDropTarget?.ShowDropTargetVisual(false);
            target?.ShowDropTargetVisual(true);
            s_highlightedDropTarget = target;
        }
    }

    private TopologyNodeControl? HitTestDropTarget(Point point)
    {
        if (_dragRoot is null || !ReferenceEquals(XamlRoot?.Content, _dragRoot)) return null;
        // Topmost hit only: never drop through overlays, panels or scrollbars.
        var hit = VisualTreeHelper.FindElementsInHostCoordinates(point, _dragRoot).FirstOrDefault();
        var target = NearestTopologyNode(hit)?.FindPoolDropTargetControl();
        return target is { IsLoaded: true, IsEnabled: true }
            && ReferenceEquals(target.XamlRoot, XamlRoot)
            && ReferenceEquals(target.ViewModel?.EditInteraction, _dragInteraction)
            && (target.ViewModel?.EditInteraction?.CanEditNow?.Invoke() ?? true) ? target : null;
    }

    private void TopologyNodeControl_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer is null || e.Pointer.PointerId != _dragPointer.PointerId) return;
        // Another mouse button can release while the captured left button is held.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var dragging = _isDragging;
        var target = dragging && CanStartInternalDrag && _dragRoot is not null
            ? HitTestDropTarget(e.GetCurrentPoint(_dragRoot).Position) : null;
        var diskId = ViewModel.Unit.StableId;
        var targetId = target?.ViewModel?.Unit.StableId;
        var callback = _dragInteraction?.OnDiskDropped;
        if (dragging)
        {
            e.Handled = true;
            s_suppressTapUntil = Environment.TickCount64 + 250;
        }
        // Clear capture/state before the callback can rebuild/unload this card.
        CancelInternalDrag();
        if (dragging && targetId is not null) callback?.Invoke(diskId, targetId);
    }

    private void CancelInternalDrag()
    {
        if (_dragPointer is null) return;
        var pointer = _dragPointer;
        _dragPointer = null;
        if (_isDragging)
        {
            _suppressNextTap = true;
            s_suppressTapUntil = Environment.TickCount64 + 250;
        }
        _isDragging = false;
        if (_dragRoot is not null) _dragRoot.KeyDown -= DragRoot_KeyDown;
        _dragRoot = null;
        _dragInteraction = null;
        if (ReferenceEquals(s_activeDrag, this)) s_activeDrag = null;
        s_highlightedDropTarget?.ShowDropTargetVisual(false);
        s_highlightedDropTarget = null;
        ReleasePointerCapture(pointer);
    }

    private void DragRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || _dragPointer is null) return;
        CancelInternalDrag();
        e.Handled = true;
    }

    private void ShowDropTargetVisual(bool on)
    {
        if (DropTargetBorder is not null)
        {
            DropTargetBorder.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Resolves an explicit editor drop target under the pointer or its parent.
    /// Pool and simulation retired/hot-spare roles retain their existing gates.
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
        if (e.Key == Windows.System.VirtualKey.Escape && _dragPointer is not null)
        {
            CancelInternalDrag();
            e.Handled = true;
            return;
        }
        if (_isDragging)
        {
            e.Handled = true;
            return;
        }
        if (ViewModel?.IsSelectable != true
            || e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space))
        {
            return;
        }

        ViewModel.SelectCommand.Execute(null);
        e.Handled = true;
    }
}
