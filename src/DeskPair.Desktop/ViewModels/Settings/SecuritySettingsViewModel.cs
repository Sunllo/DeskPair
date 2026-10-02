using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Config;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Desktop.ViewModels.Settings;

/// <summary>Who may connect: approval, passwords and the host keys this computer has trusted.</summary>
public partial class SecuritySettingsViewModel : SettingsSectionBase
{
    /// <summary>Temporary password lengths offered in the picker.</summary>
    private static readonly int[] Lengths = [6, 8, 10, 12];

    private readonly HostLink _host;

    public SecuritySettingsViewModel(HostLink host)
    {
        _host = host;
        LoadKnownHosts();
        RefreshPlatformPermissions();
        RefreshUnattendedAccess();
        if (IsWaylandSession)
        {
            _ = RefreshWaylandSharingAsync();
        }
    }

    /// <summary>The firewall rules are a Windows concept; the button has no meaning anywhere else.</summary>
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// Whether this computer can be reached while it is locked or signed out.
    ///
    /// Windows only, and not because of a missing implementation: the lock screen is drawn on a desktop
    /// that only a LocalSystem process may read, so the capability is a service or it is nothing. macOS
    /// and Linux have their own arrangements and neither is this one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWaylandSharing))]
    public partial bool UnattendedAccessInstalled { get; set; }

    /// <summary>macOS gates capture and input behind consents the user grants in System Settings.</summary>
    public bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>Linux installs a root daemon and a second account, which is worth a sentence before the password prompt.</summary>
    public bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>
    /// A Wayland desktop: the screen is shared through the desktop's portal, which asks the person at this computer.
    /// Asked here, deliberately, it is not first asked of whoever happens to be sitting there when a viewer connects.
    /// </summary>
    public bool IsWaylandSession => DesktopPlatform.IsWaylandSession;

    /// <summary>
    /// Either engine's: this app's own, which asks the portal itself, or -- with unattended access installed -- the
    /// daemon's, which reads the display hardware without anybody's permission but shares the desktop through the
    /// portal whenever it is allowed to without asking (every monitor, the pointer); it is asked through the daemon.
    /// </summary>
    public bool ShowWaylandSharing => IsWaylandSession;

    /// <summary>How long the daemon's engine is given to ask: the portal gives up on a dialog nobody answers at 45 seconds.</summary>
    private static readonly TimeSpan AskThroughTheDaemon = TimeSpan.FromSeconds(60);

    [ObservableProperty]
    public partial bool WaylandSharingAllowed { get; set; }

    [ObservableProperty]
    public partial bool WaylandSharingWatchOnly { get; set; }

    [ObservableProperty]
    public partial bool WaylandSharingNotYet { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AllowWaylandSharingCommand))]
    public partial bool WaylandSharingAsking { get; set; }

    /// <summary>How the last "Allow…" ended, or what to do while it is under way.</summary>
    [ObservableProperty]
    public partial string WaylandSharingNotice { get; set; } = string.Empty;

    /// <summary>
    /// Whether this system has a way to be reached while nobody is signed in.
    ///
    /// Three, and none of them the same shape. Windows registers a service that creates an engine in the
    /// session holding the screen; macOS registers one launchd agent limited to the login window and the
    /// desktop, and launchd does the moving; Linux registers a root daemon that reads the screen straight
    /// from the display hardware and hands it to an engine running as its own account, so nothing moves
    /// at all.
    /// </summary>
    public bool SupportsUnattendedAccess => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    [ObservableProperty]
    public partial bool ScreenRecordingGranted { get; set; }

    [ObservableProperty]
    public partial bool AccessibilityGranted { get; set; }

    /// <summary>
    /// True when the consents could not be read at all (no platform implementation, or the macOS shim did
    /// not load). Without this the page would report "not granted" for a permission it simply never checked.
    /// </summary>
    [ObservableProperty]
    public partial bool PermissionsUnknown { get; set; }

    /// <summary>Checked and refused — as opposed to never checked, which <see cref="PermissionsUnknown"/> covers.</summary>
    [ObservableProperty]
    public partial bool ScreenRecordingDenied { get; set; }

    [ObservableProperty]
    public partial bool AccessibilityDenied { get; set; }

    [ObservableProperty]
    public partial int ApproveModeIndex { get; set; }

    public NumericField ApprovalTimeout { get; } = new(5, 300, 30, "settings.rangeInvalid");

    [ObservableProperty]
    public partial bool AllowPasswordInClickMode { get; set; }

    [ObservableProperty]
    public partial bool TemporaryPasswordEnabled { get; set; }

    [ObservableProperty]
    public partial int TemporaryLengthIndex { get; set; }

    public NumericField RotationThreshold { get; } = new(1, 100, 10, "settings.rangeInvalid");

    [ObservableProperty]
    public partial bool RotateAfterSession { get; set; }

    [ObservableProperty]
    public partial string PermanentPassword { get; set; } = string.Empty;

    // ASCII only, as the text changes: see PasswordText.
    partial void OnPermanentPasswordChanged(string value)
    {
        string ascii = Services.PasswordText.Ascii(value);
        if (ascii != value)
        {
            PermanentPassword = ascii;
        }
    }

    [ObservableProperty]
    public partial bool HasPermanentPassword { get; set; }

    [ObservableProperty]
    public partial bool FileTransferEnabled { get; set; }

    // ---- the terminal ----

    /// <summary>
    /// Whether a viewer may open a shell here. Not a checkbox: turning it on goes through
    /// <see cref="TerminalConfirming"/>, which says what it means, because on an unattended machine it is
    /// SYSTEM or root for whoever holds the password.
    /// </summary>
    [ObservableProperty]
    public partial bool TerminalEnabled { get; set; }

    /// <summary>0: the most this computer allows (SYSTEM, root); 1: the signed-in user.</summary>
    [ObservableProperty]
    public partial int TerminalRunsAsIndex { get; set; }

    /// <summary>The explanation and the confirm button are showing. Transient: only the confirmation is saved.</summary>
    [ObservableProperty]
    public partial bool TerminalConfirming { get; set; }

    /// <summary>
    /// Unattended access is installed and there is no permanent password: the terminal would be reachable
    /// with nobody at the desk and nothing but a one-time password standing in front of it.
    /// </summary>
    public bool TerminalNeedsPassword => UnattendedAccessInstalled && !HasPermanentPassword;

    partial void OnHasPermanentPasswordChanged(bool value) => OnPropertyChanged(nameof(TerminalNeedsPassword));

    partial void OnUnattendedAccessInstalledChanged(bool value) => OnPropertyChanged(nameof(TerminalNeedsPassword));

    [RelayCommand]
    private void BeginEnableTerminal() => TerminalConfirming = true;

    [RelayCommand]
    private void CancelEnableTerminal() => TerminalConfirming = false;

    [RelayCommand]
    private void ConfirmEnableTerminal()
    {
        if (TerminalNeedsPassword)
        {
            return;
        }

        TerminalConfirming = false;
        TerminalEnabled = true;
    }

    [RelayCommand]
    private void DisableTerminal() => TerminalEnabled = false;

    /// <summary>Accept connections straight from the local network, with no server in between.</summary>
    [ObservableProperty]
    public partial bool LanAccessEnabled { get; set; }


    // ---- who may connect at all ----

    /// <summary>
    /// Refuse every peer that is not in <see cref="AllowedPeers"/>. Applies to the running engine at once:
    /// a machine that has just been locked down is locked down now, not after a restart.
    /// </summary>
    [ObservableProperty]
    public partial bool AllowlistEnabled { get; set; }

    /// <summary>Refuse anything arriving through a relay, whatever the list says.</summary>
    [ObservableProperty]
    public partial bool RefuseRelayed { get; set; }

    /// <summary>
    /// Windows, unattended only: while the host service runs, let only the devices in <see cref="AllowedPeers"/>
    /// see and drive the secure desktop -- the administrator (UAC) prompt and the lock/sign-in screen. Any other
    /// authorised connection gets the banner there, with no picture and no input accepted. Off (the default) means
    /// every authorised connection sees it, which is how turning on "reachable while locked" by hand has always
    /// worked. Set here so a trusted device can be granted it ahead of time, rather than only when a UAC appears
    /// and somebody has to approve it at the connection manager.
    /// </summary>
    [ObservableProperty]
    public partial bool SecureDesktopForListedOnly { get; set; }

    /// <summary>What is in the list: addresses, ranges and ids, in the order they were added.</summary>
    public ObservableCollection<string> AllowedPeers { get; } = [];

    [ObservableProperty]
    public partial bool HasAllowedPeers { get; set; }

    /// <summary>What is being typed into the box. Transient: adding it is the deliberate act, not typing it.</summary>
    [ObservableProperty]
    public partial string NewAllowedPeer { get; set; } = string.Empty;

    /// <summary>Hosts this computer has pinned on first connection (trust on first use).</summary>
    public ObservableCollection<KnownHostRow> KnownHosts { get; } = [];

    [ObservableProperty]
    public partial bool HasKnownHosts { get; set; }

    public override void Load(DesktopConfig desktop, HostConfig? host)
    {
        using IDisposable loading = Loading();
        ApprovalTimeout.ValueChanged -= RaiseChanged;
        ApprovalTimeout.ValueChanged += RaiseChanged;
        RotationThreshold.ValueChanged -= RaiseChanged;
        RotationThreshold.ValueChanged += RaiseChanged;
        LanAccessEnabled = host?.DirectAccessEnabled ?? true;
        ApproveModeIndex = host?.ApproveMode switch
        {
            Protocol.Messages.ApproveMode.ApproveClick => 1,
            Protocol.Messages.ApproveMode.ApproveBoth => 2,
            _ => 0,
        };
        ApprovalTimeout.Reset(host?.ApprovalTimeoutSeconds ?? 30);
        AllowPasswordInClickMode = host?.AllowPasswordInClickMode ?? true;
        TemporaryPasswordEnabled = host?.TemporaryPasswordEnabled ?? true;
        TemporaryLengthIndex = Math.Max(0, Array.IndexOf(Lengths, host?.TemporaryPasswordLength ?? 6));
        RotationThreshold.Reset(host?.TemporaryRotationThreshold ?? 10);
        RotateAfterSession = host?.RotateTemporaryAfterSession ?? false;
        FileTransferEnabled = host?.FileTransferEnabled ?? true;
        TerminalEnabled = host?.TerminalEnabled ?? false;
        TerminalRunsAsIndex = string.Equals(host?.TerminalRunsAs, "user", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        TerminalConfirming = false;
        AllowlistEnabled = host?.AllowlistEnabled ?? false;
        RefuseRelayed = host?.RefuseRelayed ?? false;
        SecureDesktopForListedOnly = host?.SecureDesktopForListedOnly ?? false;
        AllowedPeers.Clear();
        foreach (string entry in host?.AllowedPeers ?? [])
        {
            AllowedPeers.Add(entry);
        }

        HasAllowedPeers = AllowedPeers.Count > 0;
        NewAllowedPeer = string.Empty;
        HasPermanentPassword = _host.PasswordState?.HasPermanent ?? false;
    }

    protected override bool IsTransient(string propertyName) =>
        propertyName is "Notice" or "PermanentPassword" or "HasPermanentPassword" or "HasKnownHosts"
            or "HasAllowedPeers" or "NewAllowedPeer" or "TerminalConfirming" or "TerminalNeedsPassword";

    public override DesktopConfig Apply(DesktopConfig config) => config;

    public override HostConfig Apply(HostConfig config) => config with
    {
        ApproveMode = ApproveModeIndex switch
        {
            1 => Protocol.Messages.ApproveMode.ApproveClick,
            2 => Protocol.Messages.ApproveMode.ApproveBoth,
            _ => Protocol.Messages.ApproveMode.ApprovePassword,
        },
        ApprovalTimeoutSeconds = ApprovalTimeout.Value,
        AllowPasswordInClickMode = AllowPasswordInClickMode,
        TemporaryPasswordEnabled = TemporaryPasswordEnabled,
        TemporaryPasswordLength = Lengths[Math.Clamp(TemporaryLengthIndex, 0, Lengths.Length - 1)],
        TemporaryRotationThreshold = RotationThreshold.Value,
        RotateTemporaryAfterSession = RotateAfterSession,
        FileTransferEnabled = FileTransferEnabled,
        TerminalEnabled = TerminalEnabled,
        TerminalRunsAs = TerminalRunsAsIndex == 1 ? "user" : "system",
        DirectAccessEnabled = LanAccessEnabled,
        AllowlistEnabled = AllowlistEnabled,
        AllowedPeers = AllowedPeers.ToList(),
        RefuseRelayed = RefuseRelayed,
        SecureDesktopForListedOnly = SecureDesktopForListedOnly,
    };

    /// <summary>
    /// Adds what was typed, if it can be read. A rejected entry says so and stays in the box rather than
    /// being dropped: an allowlist that silently ignored a typo would lock somebody out and look correct.
    /// </summary>
    [RelayCommand]
    private void AddAllowedPeer()
    {
        string entry = NewAllowedPeer.Trim();
        if (entry.Length == 0)
        {
            return;
        }

        if (!Core.Session.Host.Auth.PeerAllowlist.IsValidEntry(entry))
        {
            Notice = Strings.Get("settings.allowlistBadEntry");
            return;
        }

        if (!AllowedPeers.Contains(entry, StringComparer.OrdinalIgnoreCase))
        {
            AllowedPeers.Add(entry);
            HasAllowedPeers = true;
            RaiseChanged();
        }

        NewAllowedPeer = string.Empty;
    }

    [RelayCommand]
    private void RemoveAllowedPeer(string? entry)
    {
        if (entry is null || !AllowedPeers.Remove(entry))
        {
            return;
        }

        HasAllowedPeers = AllowedPeers.Count > 0;
        RaiseChanged();
    }

    /// <summary>Setting a password is deliberate, so it stays a button rather than saving as it is typed.</summary>
    [RelayCommand]
    private async Task SetPermanentPasswordAsync()
    {
        if (PermanentPassword.Length < HostPasswords.MinTemporaryLength)
        {
            Notice = Strings.Format("settings.passwordTooShort", HostPasswords.MinTemporaryLength);
            return;
        }

        // Back on the UI thread for the answer, and an engine that is not there is a notice, not a crash: with
        // the service just installed the app can be between engines, and this is what somebody clicks first.
        try
        {
            bool ok = await _host.SetPermanentPasswordAsync(PermanentPassword).ConfigureAwait(true);
            PermanentPassword = string.Empty;
            HasPermanentPassword = ok;
            Notice = ok ? Strings.Get("home.passwordSet") : Strings.Get("settings.passwordFailed");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Notice = Strings.Format("settings.applyFailed", e.Message);
        }
    }

    /// <summary>
    /// Asks the person at this computer now: the desktop's "Remote Desktop" dialog opens on this screen, and the answer
    /// is remembered for the engine, so that viewers are let in without the question.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAllowWaylandSharing))]
    private async Task AllowWaylandSharingAsync()
    {
        WaylandSharingAsking = true;
        WaylandSharingNotice = Strings.Get("settings.waylandSharingAsking");
        try
        {
            (WaylandAskOutcome outcome, string? detail) = UnattendedAccessInstalled
                ? await AskThroughDaemonAsync().ConfigureAwait(true)
                : await DesktopPlatform.AskWaylandSharingAsync(
                    Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, CancellationToken.None).ConfigureAwait(true);
            WaylandSharingNotice = WaylandNotice(outcome, detail);
        }
        catch (Exception e)
        {
            WaylandSharingNotice = Strings.Format("settings.waylandSharingFailed", e.Message);
        }
        finally
        {
            WaylandSharingAsking = false;
        }

        await RefreshWaylandSharingAsync().ConfigureAwait(true);
    }

    private bool CanAllowWaylandSharing() => !WaylandSharingAsking;

    /// <summary>What to tell the person after asking: each ending in their language, the unforeseen ones with the portal's words.</summary>
    internal static string WaylandNotice(WaylandAskOutcome outcome, string? detail) => outcome switch
    {
        WaylandAskOutcome.Allowed => Strings.Get("settings.waylandSharingDone"),
        WaylandAskOutcome.WatchOnly => Strings.Get("settings.waylandSharingWatchOnly"),
        WaylandAskOutcome.Declined => Strings.Get("settings.waylandSharingDeclined"),
        WaylandAskOutcome.Unanswered => Strings.Get("settings.waylandSharingUnanswered"),
        _ => Strings.Format("settings.waylandSharingFailed", detail ?? string.Empty),
    };

    /// <summary>The daemon's engine asks, on this screen, through this account's session agent.</summary>
    private async Task<(WaylandAskOutcome Outcome, string? Detail)> AskThroughDaemonAsync()
    {
        IpcMessage reply = await _host.RequestAsync(
            new IpcMessage { DesktopSharingRequest = new DesktopSharingRequest { Ask = true } }, AskThroughTheDaemon).ConfigureAwait(true);
        return OutcomeOf(reply.DesktopSharingState);
    }

    /// <summary>The daemon's answer to asking: its outcome is the platform's plus one, zero meaning it did not ask.</summary>
    internal static (WaylandAskOutcome Outcome, string? Detail) OutcomeOf(DesktopSharingState? answer) => answer?.Outcome switch
    {
        1 => (WaylandAskOutcome.Allowed, null),
        2 => (WaylandAskOutcome.WatchOnly, null),
        3 => (WaylandAskOutcome.Declined, null),
        4 => (WaylandAskOutcome.Unanswered, null),
        _ => (WaylandAskOutcome.Failed, answer?.Detail is { Length: > 0 } detail ? detail : "DeskPair's service did not answer"),
    };

    /// <summary>What the daemon's engine has remembered for this account; not yet when it does not know or say.</summary>
    internal static WaylandSharingState StateOf(DesktopSharingState? answer) => answer?.State switch
    {
        2 => WaylandSharingState.WatchOnly,
        3 => WaylandSharingState.Allowed,
        _ => WaylandSharingState.NotYet,
    };

    private async Task RefreshWaylandSharingAsync()
    {
        WaylandSharingState state;
        try
        {
            state = UnattendedAccessInstalled
                ? StateOf((await _host.RequestAsync(new IpcMessage { DesktopSharingRequest = new DesktopSharingRequest() }).ConfigureAwait(true)).DesktopSharingState)
                : await DesktopPlatform.WaylandSharingStateAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // An unreadable store says nothing either way; "not yet" is what the first connection will act on.
            state = WaylandSharingState.NotYet;
        }

        WaylandSharingAllowed = state == WaylandSharingState.Allowed;
        WaylandSharingWatchOnly = state == WaylandSharingState.WatchOnly;
        WaylandSharingNotYet = state == WaylandSharingState.NotYet;
    }

    /// <summary>Re-reads the platform consents so the page reflects what the user just changed.</summary>
    [RelayCommand]
    private void RefreshPlatformPermissions()
    {
        Platform.Abstractions.Hosting.PlatformPermissions granted;
        try
        {
            Platform.Abstractions.Hosting.IPlatformInfo? info = DesktopPlatform.CreatePlatformInfo();
            if (info is null)
            {
                SetUnknown();
                return;
            }

            granted = info.GrantedPermissions();
        }
        catch (Exception)
        {
            // A shim that did not load cannot tell us anything — say so rather than claiming "not granted".
            SetUnknown();
            return;
        }

        PermissionsUnknown = false;
        ScreenRecordingGranted = granted.HasFlag(Platform.Abstractions.Hosting.PlatformPermissions.ScreenRecording);
        AccessibilityGranted = granted.HasFlag(Platform.Abstractions.Hosting.PlatformPermissions.Accessibility);
        ScreenRecordingDenied = !ScreenRecordingGranted;
        AccessibilityDenied = !AccessibilityGranted;
        return;

        void SetUnknown()
        {
            PermissionsUnknown = true;
            ScreenRecordingGranted = false;
            AccessibilityGranted = false;
            ScreenRecordingDenied = false;
            AccessibilityDenied = false;
        }
    }

    /// <summary>
    /// Asks macOS for a consent. The prompt only appears the first time; afterwards macOS just records the
    /// request, so the buttons below also open the matching System Settings pane where it can be toggled.
    /// </summary>
    [RelayCommand]
    private void RequestScreenRecording() => RequestPermission(Platform.Abstractions.Hosting.PlatformPermissions.ScreenRecording, "Privacy_ScreenCapture");

    [RelayCommand]
    private void RequestAccessibility() => RequestPermission(Platform.Abstractions.Hosting.PlatformPermissions.Accessibility, "Privacy_Accessibility");

    /// <summary>
    /// Local network access has neither a preflight API nor a System Settings anchor — macOS ships no
    /// <c>Privacy_LocalNetwork</c> pane id, and an unknown anchor just lands on Privacy &amp; Security anyway.
    /// Open that page deliberately and let the hint tell the user to pick "Local Network" from the list.
    /// </summary>
    [RelayCommand]
    private void OpenLocalNetworkSettings() => OpenPrivacyPane(null);

    private void RequestPermission(Platform.Abstractions.Hosting.PlatformPermissions permission, string pane)
    {
        try
        {
            DesktopPlatform.CreatePlatformInfo()?.RequestPermissions(permission);
        }
        catch (Exception e)
        {
            Notice = Strings.Format("settings.applyFailed", e.Message);
        }

        OpenPrivacyPane(pane);
        RefreshPlatformPermissions();
    }

    /// <summary>Opens a Privacy &amp; Security pane, or the page itself when <paramref name="pane"/> is null.</summary>
    private void OpenPrivacyPane(string? pane)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string url = pane is null
            ? "x-apple.systempreferences:com.apple.preference.security"
            : $"x-apple.systempreferences:com.apple.preference.security?{pane}";
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", url)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e)
        {
            Notice = Strings.Format("settings.applyFailed", e.Message);
        }
    }

    /// <summary>
    /// Turns being reachable while locked on or off.
    ///
    /// Both directions relaunch this program elevated, for the same reason the firewall button does: only
    /// an administrator may register a service, and this app is not asking to be one.
    ///
    /// Turning it on is a real widening of what this computer does while nobody is watching it, so it is
    /// never implied by anything else -- not by installing DeskPair, not by opening it. Turning it off is
    /// a first-class operation for the same reason: a machine being handed on should be able to stop
    /// being reachable without being reinstalled.
    /// </summary>
    [RelayCommand]
    private Task TurnOnUnattendedAccessAsync() => SetUnattendedAccessAsync(wanted: true);

    [RelayCommand]
    private Task TurnOffUnattendedAccessAsync() => SetUnattendedAccessAsync(wanted: false);

    /// <summary>
    /// Two commands rather than one with a parameter.
    ///
    /// [RelayCommand] on a method taking a bool generates an IAsyncRelayCommand&lt;bool&gt;, and
    /// CommandParameter="True" in XAML is the string "True". The types do not match, CanExecute answers
    /// no, and both buttons sit there greyed out -- which reads as "this computer cannot do that" rather
    /// than as a mistake in the binding.
    /// </summary>
    private async Task SetUnattendedAccessAsync(bool wanted)
    {
        // Said before it happens, so a password box is never a surprise. The dialog names DeskPair and
        // carries its own sentence: see MacAuthorization for why that took raising it ourselves rather
        // than going through osascript, which would have named a scripting tool instead.
        if (OperatingSystem.IsMacOS())
        {
            Notice = Strings.Get("settings.unattendedMacPrompt");
        }

        int? code = await ElevateAsync(wanted ? "--install-service" : "--uninstall-service").ConfigureAwait(true);
        if (code is not { } exit || exit == UnattendedDeclined)
        {
            Notice = Strings.Get("settings.unattendedCancelled");
        }
        else if (exit == UnattendedNoElevation)
        {
            // Nothing on this machine can put up a password prompt for us -- no polkit agent, or no
            // pkexec at all. The command that does the same thing by hand is the useful answer.
            Notice = Strings.Format("settings.unattendedNoPrompt", ManualInstallCommand(wanted));
        }
        else
        {
            Notice = Strings.Get(exit == 0
                ? (wanted ? "settings.unattendedOn" : "settings.unattendedOff")
                : (wanted ? "settings.unattendedOnFailed" : "settings.unattendedOffFailed"));
        }

        RefreshUnattendedAccess();
    }

    /// <summary>
    /// Reads back what is actually running, rather than what the switch was moved to. A service that is installed
    /// and stopped reads as off, and turning the switch on starts it: the install role starts one that exists.
    /// </summary>
    public void RefreshUnattendedAccess() => UnattendedAccessInstalled = UnattendedInstall.IsActive();

    /// <summary>The Linux installer's "declined at the prompt" exit code; the same outcome as a Windows UAC No.</summary>
    private const int UnattendedDeclined = 3;

    /// <summary>The Linux installer's "no way to ask" exit code.</summary>
    private const int UnattendedNoElevation = 4;

    private static string ManualInstallCommand(bool install)
    {
#if !WINDOWS
        if (OperatingSystem.IsLinux())
        {
            return Engine.LinuxService.LinuxUnattendedInstaller.SudoCommand(install, Engine.ServerRole.DefaultDataDir());
        }
#endif
        _ = install;
        return string.Empty;
    }

    /// <summary>
    /// Relaunches this program with one argument and waits for it, elevated where that is how it is done.
    /// </summary>
    /// <returns>Its exit code, or null when the user declined the elevation prompt.</returns>
    internal static async Task<int?> ElevateAsync(string argument)
    {
        const int ErrorCancelled = 1223; // the user said No to the elevation prompt

        // Not on macOS, and this is a trap worth naming: Verb = "runas" is silently ignored off Windows,
        // so asking for it there produces an ordinary unprivileged process that fails for a reason nobody
        // could guess from the message. macOS is also the one platform where the first stage *must not*
        // be root -- it reads the login keychain, which root cannot open -- so the install elevates its
        // own middle step through the authorisation dialog and this just starts it as the person asking.
        if (!OperatingSystem.IsWindows())
        {
            return await RunAsSelfAsync(argument).ConfigureAwait(false);
        }

        try
        {
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? string.Empty, argument)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                });
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync().ConfigureAwait(true);
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts this program again as the same user and waits for it.
    ///
    /// Null when it could not be started at all, which is the same thing the caller shows for "cancelled":
    /// both mean nothing was changed. A cancel at the macOS authorisation dialog comes back as a non-zero
    /// exit code instead, with the reason already in the install log.
    /// </summary>
    private static async Task<int?> RunAsSelfAsync(string argument)
    {
        try
        {
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? string.Empty, argument)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync().ConfigureAwait(true);
            return process.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds the Windows Firewall rules, which needs administrator rights once. The rule names this program,
    /// so it is this same executable that is relaunched to write it.
    /// </summary>
    [RelayCommand]
    private async Task AllowThroughFirewallAsync()
    {
        const int ErrorCancelled = 1223; // the user said No to the elevation prompt
        try
        {
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? string.Empty, "--allow-firewall")
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return;
            }

            Notice = Strings.Get("settings.firewallRequested");
            await process.WaitForExitAsync().ConfigureAwait(true);
            Notice = Strings.Get(process.ExitCode == 0 ? "settings.firewallAdded" : "settings.firewallFailed");
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            // Saying "requested" after the user declined the prompt was a lie the old code told.
            Notice = Strings.Get("settings.firewallCancelled");
        }
        catch (Exception e)
        {
            Notice = Strings.Format("settings.applyFailed", e.Message);
        }
    }

    [RelayCommand]
    private async Task ClearPermanentPasswordAsync()
    {
        try
        {
            await _host.SetPermanentPasswordAsync(string.Empty).ConfigureAwait(true);
            PermanentPassword = string.Empty;
            HasPermanentPassword = false;
            Notice = Strings.Get("settings.passwordCleared");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Notice = Strings.Format("settings.applyFailed", e.Message);
        }
    }

    [RelayCommand]
    private void ForgetHost(KnownHostRow? row)
    {
        if (row is null)
        {
            return;
        }

        App.KnownHosts.Remove(row.Target);
        KnownHosts.Remove(row);
        HasKnownHosts = KnownHosts.Count > 0;
    }

    [RelayCommand]
    private void ForgetAllHosts()
    {
        foreach (KnownHostRow row in KnownHosts.ToList())
        {
            App.KnownHosts.Remove(row.Target);
        }

        KnownHosts.Clear();
        HasKnownHosts = false;
    }

    private void LoadKnownHosts()
    {
        KnownHosts.Clear();
        foreach ((string target, byte[] fingerprint) in App.KnownHosts.All())
        {
            KnownHosts.Add(new KnownHostRow(target, Convert.ToHexString(fingerprint.AsSpan(0, Math.Min(8, fingerprint.Length)))));
        }

        HasKnownHosts = KnownHosts.Count > 0;
    }
}

/// <summary>One pinned host: the address it was first seen at and the start of its key fingerprint.</summary>
public sealed record KnownHostRow(string Target, string Fingerprint);
