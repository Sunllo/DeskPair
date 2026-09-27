using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Views;

/// <summary>
/// One session: its toolbar, its picture, its chat and its login overlay. This used to be the body of
/// <see cref="RemoteSessionWindow"/>, and became a control of its own when the window learned to hold
/// several sessions at once. Everything it needs it takes from its own view model, so two of these side by
/// side are two independent sessions.
/// </summary>
public partial class RemoteSessionView : UserControl
{
    private RemoteSessionViewModel? _vm;

    public RemoteSessionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as RemoteSessionViewModel);
        PasswordBox.AttachedToVisualTree += (_, _) => PasswordBox.Focus();
        DisplayWindowButton.Click += (_, _) => OpenOrOfferDisplayWindow();
        AddDisplayButton.Click += (_, _) => AddDisplay();
    }

    /// <summary>Asks for a display as big as the screen this tab is on: the new window is meant to fill one like it.</summary>
    private void AddDisplay()
    {
        PixelSize size = TopLevel.GetTopLevel(this)?.Screens?.ScreenFromVisual(this)?.Bounds.Size ?? default;
        _vm?.AddDisplay(size.Width, size.Height);
    }

    private void Bind(RemoteSessionViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm) || vm is null)
        {
            return;
        }

        _vm = vm;
        vm.Attach(Display);
        ApplyScrollMode(vm);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemoteSessionViewModel.FitToWindow))
            {
                ApplyScrollMode(vm);
            }
        };
    }

    /// <summary>
    /// The toolbar's display-window button. With two displays -- by far the usual case -- there is exactly one
    /// other to open, and it opens at once: a menu holding a single entry read as a button that did nothing.
    /// Several are offered in a menu built from what can be opened now, with items made here rather than
    /// templated in XAML (an item bound back to the session from inside a popup needs a theme lookup and an
    /// ancestor binding; a plain item with its own command needs neither).
    /// </summary>
    private void OpenOrOfferDisplayWindow()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        IReadOnlyList<DisplayChoice> offer = vm.OpenOrOfferDisplayWindow();
        if (offer.Count == 0)
        {
            return;
        }

        var menu = new MenuFlyout();
        foreach (DisplayChoice choice in offer)
        {
            menu.Items.Add(new MenuItem { Header = choice.Label, Command = vm.OpenDisplayWindowCommand, CommandParameter = choice.Index });
        }

        menu.ShowAt(DisplayWindowButton);
        OfferedMenu = menu;
    }

    /// <summary>The menu the display-window button last offered, for tests.</summary>
    internal MenuFlyout? OfferedMenu { get; private set; }

    /// <summary>Fit needs a finite viewport; 1:1 needs scrollbars.</summary>
    private void ApplyScrollMode(RemoteSessionViewModel vm)
    {
        ScrollBarVisibility mode = vm.FitToWindow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        DisplayScroller.HorizontalScrollBarVisibility = mode;
        DisplayScroller.VerticalScrollBarVisibility = mode;
    }
}
