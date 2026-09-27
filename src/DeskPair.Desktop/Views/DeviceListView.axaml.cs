using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Views;

/// <summary>
/// The device list. Two things live here rather than in the view model: the editors are popovers that float
/// beside the button that opened them, which means tracking that button; and a device is moved between groups
/// by dragging it, which is pointer work the view model should not know about.
/// </summary>
public partial class DeviceListView : UserControl
{
    /// <summary>How far the pointer travels before a press on a device counts as a drag rather than a click.</summary>
    private const double DragThreshold = 6;

    /// <summary>Private to this app, so a device can never be dropped somewhere that misreads it.</summary>
    private static readonly DataFormat<string> DeviceFormat = DataFormat.CreateStringApplicationFormat("sunllo-device");

    private DeviceListViewModel? _vm;
    private Control? _anchor;
    private DeviceRowViewModel? _pressed;
    private Point _pressedAt;
    private DeviceGroupViewModel? _hovered;

    public DeviceListView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(PointerPressedEvent, OnPointerPressedPreview, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedPreview, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => _pressed = null, RoutingStrategies.Tunnel);

        // Clicking away closes a popover; the view model has to hear about it or its flag would stay set.
        DevicePopup.Closed += (_, _) => _vm?.CancelEditCommand.Execute(null);
        GroupPopup.Closed += (_, _) => _vm?.CancelGroupEditCommand.Execute(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as DeviceListViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }
    }

    // ---- the editors are popovers ---------------------------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(DeviceListViewModel.IsEditorOpen):
                Toggle(DevicePopup, _vm.IsEditorOpen, TargetBox);
                break;
            case nameof(DeviceListViewModel.IsGroupEditorOpen):
                Toggle(GroupPopup, _vm.IsGroupEditorOpen, GroupNameBox);
                break;
        }
    }

    /// <summary>Opens a popover beside the button that was just pressed, or closes it.</summary>
    private void Toggle(Popup popup, bool open, Control focus)
    {
        if (!open)
        {
            popup.IsOpen = false;
            return;
        }

        popup.PlacementTarget = _anchor ?? this;
        popup.IsOpen = true;
        focus.Focus();
    }

    // ---- dragging a device onto a group ---------------------------------------------------------

    private void OnPointerPressedPreview(object? sender, PointerPressedEventArgs e)
    {
        // The button being pressed is what a popover floats beside, and a press on one never starts a drag.
        Button? button = (e.Source as Visual)?.FindLogicalAncestorOfType<Button>(includeSelf: true);
        if (button is not null)
        {
            _anchor = button;
        }

        _pressed = button is null ? FindDataContext<DeviceRowViewModel>(e.Source as Visual) : null;
        _pressedAt = e.GetPosition(this);
    }

    private async void OnPointerMovedPreview(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } row || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _pressedAt.X) < DragThreshold && Math.Abs(now.Y - _pressedAt.Y) < DragThreshold)
        {
            return;
        }

        _pressed = null;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DeviceFormat, row.Target));
        ShowGhost(row, now);
        try
        {
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        finally
        {
            // The drag is over however it ended: dropped, cancelled, or the window lost it.
            HideGhost(row);
            Highlight(null);
        }
    }

    /// <summary>
    /// The card that follows the pointer. The platform has no drag image of its own, so the list draws one and
    /// moves it on each DragOver, which is the only pointer report that arrives while a drag is running.
    /// </summary>
    private void ShowGhost(DeviceRowViewModel row, Point at)
    {
        row.IsDragging = true;
        DragGhost.DataContext = row;
        GhostLayer.IsVisible = true;
        MoveGhost(at);
    }

    private void HideGhost(DeviceRowViewModel row)
    {
        row.IsDragging = false;
        GhostLayer.IsVisible = false;
        DragGhost.DataContext = null;
    }

    private void MoveGhost(Point at)
    {
        Canvas.SetLeft(DragGhost, at.X + 14);
        Canvas.SetTop(DragGhost, at.Y + 10);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        string? target = e.DataTransfer?.TryGetValue(DeviceFormat);
        DeviceGroupViewModel? group = FindDataContext<DeviceGroupViewModel>(e.Source as Visual);
        bool allowed = target is not null && _vm?.CanDropOn(target, group) == true;
        MoveGhost(e.GetPosition(this));
        GhostLayer.IsVisible = DragGhost.DataContext is not null;
        Highlight(allowed ? group : null);
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        // Off the list entirely: stop drawing the card here, but the drag itself is still running.
        GhostLayer.IsVisible = false;
        Highlight(null);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        Highlight(null);
        if (e.DataTransfer?.TryGetValue(DeviceFormat) is { } target)
        {
            _vm?.MoveToGroup(target, FindDataContext<DeviceGroupViewModel>(e.Source as Visual));
        }

        e.Handled = true;
    }

    private void Highlight(DeviceGroupViewModel? group)
    {
        if (ReferenceEquals(_hovered, group))
        {
            return;
        }

        if (_hovered is not null)
        {
            _hovered.IsDropTarget = false;
        }

        _hovered = group;
        if (_hovered is not null)
        {
            _hovered.IsDropTarget = true;
        }
    }

    /// <summary>The nearest thing up the tree that is bound to a <typeparamref name="T"/>.</summary>
    private static T? FindDataContext<T>(Visual? from)
        where T : class
    {
        for (Visual? visual = from; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is StyledElement { DataContext: T found })
            {
                return found;
            }
        }

        return null;
    }
}
