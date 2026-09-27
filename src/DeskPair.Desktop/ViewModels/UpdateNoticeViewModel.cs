using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Update;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.Services.Update;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// The one line that says a newer version exists, over whatever page is open.
///
/// It floats over the content rather than living on the home view because the reader may be sitting in
/// Settings or on the device list, and the incoming-connection overlay beside it already establishes that
/// a notice in this application is something that appears over the page rather than in it.
///
/// It decides what to show; <see cref="UpdateCheckService"/> only reports facts. Dismissal in particular
/// belongs here: the service has no business remembering what somebody waved away.
/// </summary>
public sealed partial class UpdateNoticeViewModel : ObservableObject
{
    private readonly Func<string> _dismissed;
    private readonly Action<string> _remember;
    private readonly Func<string, ReleaseFile, IProgress<UpdateProgress>, CancellationToken, Task> _install;
    private readonly Action _quit;
    private UpdateState _state = UpdateState.Unknown;

    public UpdateNoticeViewModel(Func<string> dismissed, Action<string> remember)
        : this(dismissed, remember, InstallForReal, App.Quit)
    {
    }

    /// <param name="install">Downloads, verifies and stages the file; returns when the finishing script is running.</param>
    /// <param name="quit">Ends this process so the finishing script can replace it.</param>
    public UpdateNoticeViewModel(
        Func<string> dismissed,
        Action<string> remember,
        Func<string, ReleaseFile, IProgress<UpdateProgress>, CancellationToken, Task> install,
        Action quit)
    {
        _dismissed = dismissed;
        _remember = remember;
        _install = install;
        _quit = quit;
    }

    /// <summary>For the designer and for tests that do not want the whole application behind them.</summary>
    public UpdateNoticeViewModel()
        : this(() => App.Config.DismissedUpdateVersion, RememberDismissal)
    {
    }

    /// <summary>
    /// True when the portal's manifest was signed by the key this install trusts and names a file for
    /// this machine. Then the button installs; otherwise it opens the download page.
    /// </summary>
    [ObservableProperty]
    public partial bool CanInstall { get; private set; }

    [ObservableProperty]
    public partial bool IsInstalling { get; private set; }

    /// <summary>The one line under the button while an install runs.</summary>
    [ObservableProperty]
    public partial string Progress { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    [ObservableProperty]
    public partial string Text { get; private set; } = string.Empty;

    /// <summary>Shown when opening the browser failed, so the address can at least be read.</summary>
    [ObservableProperty]
    public partial string Notice { get; private set; } = string.Empty;

    public void Show(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;

        IsVisible = state.IsUpdateAvailable
            && state.LatestVersion.Length > 0
            // Dismissing silences this version, not every future one.
            && !string.Equals(state.LatestVersion, _dismissed(), StringComparison.Ordinal);

        Text = IsVisible ? Strings.Format("update.available", state.LatestVersion, App.Version) : string.Empty;
        CanInstall = IsVisible && state.CanInstall;
        Notice = string.Empty;
    }

    /// <summary>
    /// Downloads and installs, then ends the process so the finishing script can replace it. Nothing is
    /// installed unless the file's hash is the signed manifest's; a failure is a sentence under the button
    /// and the old version keeps running.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken ct)
    {
        if (_state.Install is not { } file || IsInstalling)
        {
            return;
        }

        IsInstalling = true;
        Notice = string.Empty;
        Progress = Strings.Format("update.downloading", 0);
        var progress = new Progress<UpdateProgress>(p => Progress = p.Stage switch
        {
            UpdateStage.Downloading when p.Total > 0 => Strings.Format("update.downloading", (int)(100 * p.Done / p.Total)),
            UpdateStage.Downloading => Strings.Format("update.downloading", 0),
            UpdateStage.Verifying => Strings.Get("update.verifying"),
            _ => Strings.Get("update.installing"),
        });
        try
        {
            await _install(_state.Portal, file, progress, ct);
            Progress = Strings.Get("update.restarting");
            _quit();
        }
        catch (OperationCanceledException)
        {
            Progress = string.Empty;
            IsInstalling = false;
        }
        catch (Exception e) when (e is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Notice = Strings.Format("update.failed", e.Message);
            Progress = string.Empty;
            IsInstalling = false;
        }
    }

    private static Task InstallForReal(string portal, ReleaseFile file, IProgress<UpdateProgress> progress, CancellationToken ct) =>
        new UpdateInstaller(new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, App.Logs.CreateLogger("update"))
            .InstallAsync(portal, file, progress, ct);

    [RelayCommand]
    private void Get()
    {
        string url = _state.DownloadUrl;
        if (Launcher.TryOpenUrl(url))
        {
            // Leave the notice up. The page is open in a browser; the update has not happened yet, and
            // claiming otherwise by hiding this would be a small lie.
            return;
        }

        Notice = Strings.Format("settings.openFailed", url);
    }

    [RelayCommand]
    private void Dismiss()
    {
        IsVisible = false;
        if (_state.LatestVersion.Length > 0)
        {
            _remember(_state.LatestVersion);
        }
    }

    private static void RememberDismissal(string version)
    {
        try
        {
            App.Config = App.Config with { DismissedUpdateVersion = version };
            App.Config.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The banner is already gone for this run. A settings file that could not be written means it
            // comes back next launch, which is a worse nag than a silent failure is a bug.
        }
    }
}
