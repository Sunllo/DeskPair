using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Portal;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.ViewModels;

/// <summary>The main window: a navigation rail on the left, one screen at a time on the right.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// What just happened, shown over the content for a few seconds. One for the whole window, because a
    /// message about the machine is not about the page it was set from.
    /// </summary>
    public Services.Toasts Toasts => Services.Toasts.Current;

    public MainWindowViewModel(HostLink host)
    {
        Home = new HomeViewModel(host);
        Devices = new DeviceListViewModel();
        Incoming = new IncomingConnectionsViewModel(host);
        History = new ConnectionHistoryViewModel(host);
        Settings = new SettingsViewModel(host);
        Update = new UpdateNoticeViewModel();

        // The device list is the account's list: it lives in the portal's database, folders and all, and
        // follows the person to whatever else they sign in on. So the page asks once, and hears about it
        // when the answer changes on the settings screen rather than only when the window is reopened.
        Show(ViewModels.Settings.AccountSettingsViewModel.Current);
        _ = RefreshAccountAsync();
        Settings.AccountLinkChanged += state =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Show(state));

        // The window outlives every check, so subscribing here leaks nothing -- unlike the settings
        // screen, which is built and thrown away and therefore only reads the state when it opens.
        //
        // The event arrives on a pool thread: the timer's. A binding mutated from one fails
        // intermittently, hours into a session, which is the worst kind to reproduce.
        if (App.Updates is { } updates)
        {
            Update.Show(updates.Current);
            updates.Changed += state => Avalonia.Threading.Dispatcher.UIThread.Post(() => Update.Show(state));
        }
    }

    private async Task RefreshAccountAsync()
    {
        LinkState state = await SettingsViewModel.CurrentAccountAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Show(state));
    }

    /// <summary>Who is signed in, said in the two places that show it: the rail's foot and the device list.</summary>
    private void Show(LinkState state)
    {
        IsSignedIn = state.IsLinked;
        AccountLine = state.IsLinked && state.Account.Length > 0
            ? state.Account
            : Strings.Get("nav.account.signIn");
        AccountDetail = state.IsLinked
            ? (state.Alias.Length > 0 ? state.Alias : Environment.MachineName)
            : string.Empty;
        Devices.IsSignedIn = state.IsLinked;
    }

    /// <summary>
    /// The account, along the bottom of the rail.
    ///
    /// It sits there rather than on a page because it is about the app rather than about any one screen,
    /// and because somebody looking for who they are signed in as looks at the bottom of the rail --
    /// which is where every other program of this shape puts it.
    /// </summary>
    [ObservableProperty]
    public partial string AccountLine { get; set; } = string.Empty;

    /// <summary>The name this computer goes by, under the account. Empty when nobody is signed in.</summary>
    [ObservableProperty]
    public partial string AccountDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSignedIn { get; set; }

    /// <summary>
    /// Asked for when somebody clicks the account. The window puts it on screen.
    ///
    /// An event rather than a window built here: this class is what the window shows, and a view model that
    /// constructs windows cannot be exercised without a running display.
    /// </summary>
    public event Action? SignInRequested;

    /// <summary>Clicking the account opens the sign-in window, from whichever page is on screen.</summary>
    [RelayCommand]
    private void OpenAccount() => SignInRequested?.Invoke();

    public HomeViewModel Home { get; }

    public DeviceListViewModel Devices { get; }

    /// <summary>The strip along the bottom that says who is connecting to this computer.</summary>
    public IncomingConnectionsViewModel Incoming { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>Who has connected to this computer.</summary>
    public ConnectionHistoryViewModel History { get; }

    /// <summary>The line across the top that says a newer version exists.</summary>
    public UpdateNoticeViewModel Update { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSelected))]
    [NotifyPropertyChangedFor(nameof(IsDevicesSelected))]
    [NotifyPropertyChangedFor(nameof(IsHistorySelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    public partial int SelectedSection { get; set; }

    /// <summary>The rail's pages, by index, so callers outside the window can ask for one by name.</summary>
    public const int HomeSection = 0;

    public const int DevicesSection = 1;

    public const int HistorySection = 2;

    public const int SettingsSection = 3;

    public bool IsHomeSelected => SelectedSection == HomeSection;

    public bool IsDevicesSelected => SelectedSection == DevicesSection;

    public bool IsHistorySelected => SelectedSection == HistorySection;

    public bool IsSettingsSelected => SelectedSection == SettingsSection;

    /// <summary>The device list only asks who is online while it is the page on screen.</summary>
    partial void OnSelectedSectionChanged(int value)
    {
        if (value == DevicesSection)
        {
            Devices.Activate();
        }
        else
        {
            Devices.Deactivate();
        }

        // The record is read when it is looked at rather than kept live: it changes when somebody connects,
        // which is exactly when nobody is reading this page.
        if (value == HistorySection)
        {
            _ = History.RefreshAsync();
        }
    }
}
