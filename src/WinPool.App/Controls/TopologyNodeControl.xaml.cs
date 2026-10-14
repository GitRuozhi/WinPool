using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
    private Popup? _dragPreview;
    private TranslateTransform? _dragPreviewTransform;
    private PendingBorderAnimation? _pendingBorderAnimation;
    private Brush? _pendingAccentBrush;
    private SvgImageSource? _chainTileSource;
    private double _chainWidth = double.NaN;
    private double _chainHeight = double.NaN;

    private const double AvailabilityTileWidth = 160;
    private const double AvailabilityTileHeight = 64;

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(TopologyNodeViewModel),
        typeof(TopologyNodeControl),
        new PropertyMetadata(null, OnViewModelChanged));

    public TopologyNodeControl()
    {
        InitializeComponent();
        _pendingAccentBrush = PendingMark.Fill;
        ContextHelp.Set(ExpandButton, "展开或折叠此结构节点。 / Expand or collapse this structure node.");
        ContextHelp.SetDisabledReason(ExpandButton, "当前节点没有可展开的子项。 / This node has no child items to expand.");
        Loaded += (_, _) => { UpdateSelectionVisual(); UpdateAvailabilityGeometry(); UpdatePendingOutline(); };
        ActualThemeChanged += (_, _) => UpdateSelectionVisual();
        PointerPressed += TopologyNodeControl_PointerPressed;
        PointerMoved += TopologyNodeControl_PointerMoved;
        PointerReleased += TopologyNodeControl_PointerReleased;
        PointerCanceled += (_, _) => CancelInternalDrag();
        PointerCaptureLost += (_, _) => CancelInternalDrag();
        Unloaded += (_, _) =>
        {
            CancelInternalDrag();
            _pendingBorderAnimation?.SetActive(false);
            if (ReferenceEquals(s_hoveredControl, this))
            {
                s_hoveredControl = null;
            }
            _isPointerOver = false;
            _hasKeyboardFocus = false;
        };
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
        control._pendingBorderAnimation?.Dispose();
        control._pendingBorderAnimation = null;
        if (args.OldValue is TopologyNodeViewModel oldValue)
        {
            oldValue.PropertyChanged -= control.ViewModel_PropertyChanged;
        }
        if (args.NewValue is TopologyNodeViewModel newValue)
        {
            newValue.PropertyChanged += control.ViewModel_PropertyChanged;
        }
        control.Bindings.Update();
        if (args.NewValue is TopologyNodeViewModel currentValue)
        {
            control._pendingBorderAnimation = new PendingBorderAnimation(
                control.NodeSurface,
                control.PendingRestOutline,
                control.PendingOutline,
                currentValue.Unit.StableId);
        }
        // This editor transfers a relationship inside this XamlRoot, never
        // data to the shell. Native data-transfer dragging fails elevated.
        control.CanDrag = false;
        control.AllowDrop = false;
        control.UpdateSelectionVisual();
        control.UpdateAvailabilityGeometry();
        control.UpdatePendingOutline();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TopologyNodeViewModel.HasPendingModifications))
            UpdatePendingOutline();
        if (e.PropertyName is nameof(TopologyNodeViewModel.IsSelected)
            or nameof(TopologyNodeViewModel.IsExpanded))
        {
            UpdateSelectionVisual();
        }
    }

    private void NodeSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAvailabilityGeometry();
        UpdatePendingOutline();
    }

    private void UpdateAvailabilityGeometry()
    {
        // Decorations use the measured card size and never enlarge its layout.
        if (UnavailableOverlay is null || ViewModel is null) return;
        var width = Math.Max(0, NodeSurface.ActualWidth);
        var height = Math.Max(0, NodeSurface.ActualHeight);
        UnavailableOverlay.Width = width;
        UnavailableOverlay.Height = height;
        UnavailableOverlay.Clip = CreateCardClip(width, height);
        UnavailableOverlaySurface.Width = width;
        UnavailableOverlaySurface.Height = height;
        UnavailableChainCanvas.Width = width;
        UnavailableChainCanvas.Height = height;
        UnavailableChainCanvas.Clip = CreateCardClip(width, height);

        var compact = width < 180;
        // Short, wide cards have room beside the icon, not below it.
        // Keeping a vertical stack here clipped the reason on 82-DIP VD cards.
        var horizontal = height < 112;
        var iconSize = compact ? 32 : 52;
        iconSize = (int)Math.Min(iconSize, Math.Max(16, height - 24));
        UnavailableIconImage.Width = iconSize;
        UnavailableIconImage.Height = iconSize;
        UnavailableStatusText.FontSize = compact ? 14 : 20;
        UnavailableStatusPanel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        UnavailableStatusPanel.Spacing = compact ? 4 : 8;
        UnavailableStatusText.MaxWidth = Math.Max(32,
            width - 24 - (horizontal ? iconSize + UnavailableStatusPanel.Spacing : 0));

        if (ViewModel.UnavailableVisibility == Visibility.Visible)
        {
            UnavailableIconImage.Source = ViewModel.UnavailableIconSource;
            UpdateUnavailableChainTiles(width, height);
        }
        else
        {
            UnavailableIconImage.Source = null;
            UnavailableChainCanvas.Children.Clear();
            _chainWidth = double.NaN;
            _chainHeight = double.NaN;
        }
    }

    private void UpdatePendingOutline()
    {
        _pendingBorderAnimation?.SetActive(
            IsLoaded
            && ViewModel is { HasPendingModifications: true, IsInvisibleLayoutContainer: false });
    }

    private void UpdateUnavailableChainTiles(double width, double height)
    {
        if (Math.Abs(width - _chainWidth) < 1 && Math.Abs(height - _chainHeight) < 1)
        {
            return;
        }

        _chainWidth = width;
        _chainHeight = height;
        UnavailableChainCanvas.Children.Clear();

        var columns = (int)Math.Ceiling(width / AvailabilityTileWidth) + 2;
        var rows = (int)Math.Ceiling(height / AvailabilityTileHeight) + 1;
        for (var row = 0; row < rows; row++)
        {
            var leftOffset = (row & 1) == 0 ? -AvailabilityTileWidth / 2 : 0;
            for (var column = -1; column < columns; column++)
            {
                var tile = new Image
                {
                    Width = AvailabilityTileWidth,
                    Height = AvailabilityTileHeight,
                    Stretch = Stretch.Fill,
                    Opacity = 0.24,
                    IsHitTestVisible = false,
                    Source = _chainTileSource ??= new SvgImageSource(
                        new Uri("ms-appx:///Assets/StorageStatus/chains.svg"))
                };
                Canvas.SetLeft(tile, column * AvailabilityTileWidth + leftOffset);
                Canvas.SetTop(tile, row * AvailabilityTileHeight - AvailabilityTileHeight / 2);
                UnavailableChainCanvas.Children.Add(tile);
            }
        }
    }

    // UIElement.Clip accepts RectangleGeometry only in this WinUI version.
    private static RectangleGeometry CreateCardClip(double width, double height) =>
        new() { Rect = new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)) };

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
        NodeBorder.CornerRadius = new CornerRadius(4);

        var isEmphasized = ViewModel.IsSelected || _isPointerOver || _hasKeyboardFocus;
        var state = ViewModel.IsSelected ? "Selected"
            : isEmphasized ? "PointerOver" : "Normal";
        // The XAML states resolve theme resources in the control's actual
        // theme context. Looking up ThemeResource keys through
        // Application.Current.Resources can throw when a key exists only in
        // WinUI's theme dictionaries.
        var stateApplied = VisualStateManager.GoToState(this, state, false);
        if (!stateApplied)
        {
            System.Diagnostics.Debug.WriteLine(
                $"TopologyNodeControl could not apply interaction state '{state}'.");
        }

        var primaryBrush = DisplayNameText.Foreground;
        var secondaryBrush = TypeLabelText.Foreground;
        var iconBrush = isEmphasized ? primaryBrush : secondaryBrush;
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
        SingleLineDisplayNameText.Foreground = primaryBrush;
        SingleLineSummaryText.Foreground = isEmphasized ? primaryBrush : secondaryBrush;
        StoredDataMark.Foreground = iconBrush;
        CannotLeaveMark.Foreground = iconBrush;
        PendingMark.Fill = ViewModel.IsSelected ? primaryBrush : _pendingAccentBrush ?? PendingMark.Fill;
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
        // This disk owns the captured gesture. Letting its press bubble to
        // the ScrollViewer can hand the gesture to scrolling before a move.
        // Tapped remains a separate routed event for an ordinary click.
        e.Handled = true;
    }

    private void TopologyNodeControl_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer is null || e.Pointer.PointerId != _dragPointer.PointerId || _dragRoot is null) return;
        if (!CanStartInternalDrag || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            CancelInternalDrag();
            return;
        }
        var rootPoint = e.GetCurrentPoint(_dragRoot).Position;
        var hostPoint = e.GetCurrentPoint(null).Position;
        if (!_isDragging)
        {
            var dx = rootPoint.X - _dragStart.X;
            var dy = rootPoint.Y - _dragStart.Y;
            if (dx * dx + dy * dy < 64) return; // Eight DIPs; clicks remain clicks.
            _isDragging = true;
            _suppressNextTap = true;
            Focus(FocusState.Programmatic); // Escape now belongs to this drag.
            ShowDragPreview(hostPoint);
        }
        else
        {
            MoveDragPreview(hostPoint);
        }
        e.Handled = true;
        var target = HitTestDropTarget(hostPoint);
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
        // This API uses app-window coordinates even when a subtree is supplied.
        // Keep overlays, panels and scrollbars in front of targets.
        var hit = VisualTreeHelper.FindElementsInHostCoordinates(point, _dragRoot).FirstOrDefault();
        var target = NearestTopologyNode(hit)?.FindPoolDropTargetControl();
        return target is { IsLoaded: true, IsEnabled: true, ViewModel: { } targetModel }
            && ReferenceEquals(target.XamlRoot, XamlRoot)
            && ReferenceEquals(targetModel.EditInteraction, _dragInteraction)
            && (targetModel.EditInteraction?.CanEditNow?.Invoke() ?? true)
            && (_dragInteraction?.CanDropDisk?.Invoke(ViewModel.Unit.StableId, targetModel.Unit.StableId) ?? true)
                ? target : null;
    }

    private void ShowDragPreview(Point hostPoint)
    {
        if (XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(new FontIcon
        {
            Glyph = ViewModel.TypeGlyph,
            FontSize = 14,
            Foreground = DisplayNameText.Foreground
        });
        heading.Children.Add(new TextBlock
        {
            Text = ViewModel.Unit.DisplayName,
            MaxWidth = Math.Clamp(ActualWidth, 180, 300) - 52,
            Foreground = DisplayNameText.Foreground,
            TextWrapping = TextWrapping.Wrap
        });
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(heading);
        content.Children.Add(new TextBlock
        {
            Text = ViewModel.Summary,
            Foreground = SummaryText.Foreground,
            TextWrapping = TextWrapping.Wrap
        });

        _dragPreviewTransform = new TranslateTransform
        {
            X = hostPoint.X + 14,
            Y = hostPoint.Y + 14
        };
        var card = new Border
        {
            Child = content,
            Width = Math.Clamp(ActualWidth, 180, 300),
            Padding = new Thickness(10),
            Background = NodeBorder.Background,
            BorderBrush = InteractionBorder.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Opacity = 0.9,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = _dragPreviewTransform,
            IsHitTestVisible = false
        };
        var overlay = new Grid
        {
            Width = xamlRoot.Size.Width,
            Height = xamlRoot.Size.Height,
            IsHitTestVisible = false
        };
        overlay.Children.Add(card);
        _dragPreview = new Popup
        {
            XamlRoot = xamlRoot,
            Child = overlay,
            IsHitTestVisible = false,
            HorizontalOffset = 0,
            VerticalOffset = 0
        };
        // Open once. Pointer movement below only translates the card, so the
        // source subtree and the overlay's layout do not participate in drag.
        _dragPreview.IsOpen = true;
    }

    private void MoveDragPreview(Point hostPoint)
    {
        if (_dragPreviewTransform is null)
        {
            return;
        }

        _dragPreviewTransform.X = hostPoint.X + 14;
        _dragPreviewTransform.Y = hostPoint.Y + 14;
    }

    private void TopologyNodeControl_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer is null || e.Pointer.PointerId != _dragPointer.PointerId) return;
        // Another mouse button can release while the captured left button is held.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var dragging = _isDragging;
        var target = dragging && CanStartInternalDrag && _dragRoot is not null
            ? HitTestDropTarget(e.GetCurrentPoint(null).Position) : null;
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
        if (_dragPreview is not null)
        {
            _dragPreview.IsOpen = false;
            _dragPreview.Child = null;
            _dragPreview = null;
        }
        _dragPreviewTransform = null;
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
