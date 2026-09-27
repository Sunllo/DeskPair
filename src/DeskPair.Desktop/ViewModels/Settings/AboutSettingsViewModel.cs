using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Config;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.ViewModels.Settings;

/// <summary>What this build is, whether it is current, and how to reach its logs.</summary>
public partial class AboutSettingsViewModel : SettingsSectionBase
{
    [ObservableProperty]
    public partial string ServerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceIdText { get; set; } = string.Empty;

    public string VersionText => Strings.Format("settings.versionValue", App.Version);

    /// <summary>
    /// When this build was made, taken from the executable's own timestamp. Deliberately not
    /// <c>Assembly.Location</c>, which is empty in a single-file build.
    /// </summary>
    public string BuildDateText
    {
        get
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeskPair.exe");
            string when = File.Exists(exe) ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd") : "—";
            return Strings.Format("settings.buildDate", when);
        }
    }

    /// <summary>Where the crash log goes. Reached by the button, deliberately not printed beside it.</summary>
    public string LogFolder => CrashReporter.LogDirectory;

    /// <summary>The software this build carries that has its own licence terms.</summary>
    public string Components => Strings.Get("settings.components");

    // ---- updates ----

    /// <summary>The one thing on this tab that is a setting rather than a readout.</summary>
    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; } = true;

    [ObservableProperty]
    public partial bool Checking { get; set; }

    /// <summary>The last thing that went wrong on screen. Transient by the rule below.</summary>

    [ObservableProperty]
    public partial UpdateState Update { get; set; } = UpdateState.Unknown;

    /// <summary>What the last check found, or why it did not work.</summary>
    public string UpdateText => Update switch
    {
        { Problem.Length: > 0 } => Strings.Format("settings.updateCheckFailed", Update.Problem),
        { IsUpdateAvailable: true } => Strings.Format("settings.updateAvailable", Update.LatestVersion),
        { LastChecked: not null } => Strings.Get("settings.upToDate"),
        _ => Strings.Get("settings.neverChecked"),
    };

    /// <summary>
    /// When the portal last answered.
    ///
    /// Shown because an unsigned manifest cannot detect suppression: somebody quietly withholding updates
    /// looks exactly like being up to date. "Last checked six weeks ago" is the only thing on this screen
    /// that makes that visible.
    /// </summary>
    public string LastCheckedText => Update.LastChecked is { } when
        ? Strings.Format("settings.lastChecked", when.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
        : Strings.Get("settings.neverChecked");

    public string UpdateNotes => Update.Notes;

    public bool HasUpdateNotes => Update.Notes.Length > 0;

    public override void Load(DesktopConfig desktop, HostConfig? host)
    {
        using IDisposable loading = Loading();
        ServerText = desktop.RendezvousServer.Length > 0 ? desktop.RendezvousServer : Strings.Get("settings.noServer");
        DeviceIdText = host is null ? Strings.Get("settings.hostOffline") : string.Empty;
        CheckForUpdates = desktop.CheckForUpdates;

        // Read on open rather than subscribed to. UpdateCheckService outlives every settings screen, and
        // this view model has no Dispose to unhook from it, so a subscription here would accumulate one
        // handler per visit. What it costs is a background result arriving while the tab is already open,
        // which happens at most every six hours and is one button press away.
        ShowUpdate(App.Updates?.Current ?? UpdateState.Unknown);
    }

    public override DesktopConfig Apply(DesktopConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config with { CheckForUpdates = CheckForUpdates };
    }

    /// <summary>
    /// A whitelist, not a blacklist.
    ///
    /// Only the toggle is stored; everything else here is a readout, including the ones a six-hourly
    /// background check pushes in. A property changed outside <c>Loading()</c> is taken as a change the
    /// user made, so a blacklist would go stale the first time somebody added a field and the tab would
    /// write settings.json on its own with nobody at the keyboard.
    /// </summary>
    protected override bool IsTransient(string propertyName) => propertyName is not nameof(CheckForUpdates);

    /// <summary>Takes a state from the check service and tells the screen what moved.</summary>
    public void ShowUpdate(UpdateState state)
    {
        Update = state;
        OnPropertyChanged(nameof(UpdateText));
        OnPropertyChanged(nameof(LastCheckedText));
        OnPropertyChanged(nameof(UpdateNotes));
        OnPropertyChanged(nameof(HasUpdateNotes));
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (App.Updates is not { } updates)
        {
            return;
        }

        Checking = true;
        try
        {
            await updates.CheckNowAsync().ConfigureAwait(true);
        }
        finally
        {
            Checking = false;
            ShowUpdate(updates.Current);
        }
    }

    [RelayCommand]
    private void OpenDownloadPage()
    {
        string url = Update.DownloadUrl.Length > 0
            ? Update.DownloadUrl
            : Core.Update.UpdateEndpoints.DownloadPageFor(App.Config.PortalServer);

        if (!Launcher.TryOpenUrl(url))
        {
            // Better than the silence a failed launch used to produce: the address is on screen to copy.
            Notice = Strings.Format("settings.openFailed", url);
        }
    }

    [RelayCommand]
    private void OpenLogs() => Launcher.TryOpenFolder(LogFolder);
}
