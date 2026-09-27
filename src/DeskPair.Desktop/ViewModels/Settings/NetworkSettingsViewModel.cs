using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Config;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.ViewModels.Settings;

/// <summary>Servers, direct connections and how media travels.</summary>
public partial class NetworkSettingsViewModel : SettingsSectionBase
{
    /// <summary>
    /// Whether this installation runs its own servers.
    ///
    /// The inverse of the stored flag, because that is the way round the question reads on screen: the
    /// choice somebody is making is "I run my own", not "do not use the directory". Off, the fields below
    /// are empty and disabled, and nothing about where the official servers are appears on this page --
    /// which is not concealment, it is that there is nothing here for that person to decide.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesDirectory))]
    public partial bool SelfHosted { get; set; }

    public bool UsesDirectory => !SelfHosted;

    [ObservableProperty]
    public partial string RendezvousServer { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ServerPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Where the account lives. Empty is the official portal.
    ///
    /// It used to be asked for on the account screen, above the fields somebody was trying to sign in
    /// with, where it read as something they had to fill in. It is an address, it belongs with the other
    /// addresses, and almost nobody ever changes it.
    /// </summary>
    [ObservableProperty]
    public partial string PortalServer { get; set; } = string.Empty;

    public NumericField DirectAccessPort { get; } = new(1, 65535, 21118, "settings.rangeInvalid");

    [ObservableProperty]
    public partial bool HostUdpMedia { get; set; }

    [ObservableProperty]
    public partial bool ViewerUdpMedia { get; set; }

    [ObservableProperty]
    public partial bool HostForceRelay { get; set; }

    [ObservableProperty]
    public partial bool ViewerForceRelay { get; set; }


    public override void Load(DesktopConfig desktop, HostConfig? host)
    {
        using IDisposable loading = Loading();
        DirectAccessPort.ValueChanged -= RaiseChanged;
        DirectAccessPort.ValueChanged += RaiseChanged;
        RendezvousServer = desktop.RendezvousServer.Length > 0 ? desktop.RendezvousServer : host?.RendezvousServer ?? string.Empty;
        ServerPublicKey = desktop.ServerPublicKeyBase64.Length > 0 ? desktop.ServerPublicKeyBase64 : host?.ServerPublicKeyBase64 ?? string.Empty;
        PortalServer = desktop.PortalServer;

        // An address already typed in means self-hosted, whatever the flag says. That covers every
        // installation configured before there was a directory to ask, which would otherwise come back
        // from an update looking like it had been switched to servers it never agreed to.
        SelfHosted = RendezvousServer.Length > 0 || !(host?.UseDirectoryServers ?? true);
        ViewerUdpMedia = desktop.UdpMedia;
        ViewerForceRelay = desktop.ForceRelay;
        DirectAccessPort.Reset(host?.DirectAccessPort ?? 21118);
        HostUdpMedia = host?.UdpMedia ?? true;
        HostForceRelay = host?.ForceRelay ?? false;
    }

    protected override bool IsTransient(string propertyName) => propertyName is "Notice";

    /// <summary>A key that is not valid base64 is kept on screen but never stored.</summary>
    private string StorableKey(string stored)
    {
        string key = PeerSettings.CleanBase64(ServerPublicKey);
        if (key.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            Convert.FromBase64String(key);
            return key;
        }
        catch (FormatException)
        {
            Notice = Strings.Get("settings.keyInvalid");
            return stored;
        }
    }

    public override DesktopConfig Apply(DesktopConfig config) => config with
    {
        // Cleared rather than kept when somebody switches back to the directory. A stored address would
        // silently win over whatever the portal answers, and the field it came from is no longer on screen
        // for them to see why.
        RendezvousServer = SelfHosted ? RendezvousServer.Trim() : string.Empty,
        ServerPublicKeyBase64 = SelfHosted ? StorableKey(config.ServerPublicKeyBase64) : string.Empty,

        // Not cleared with the others when the self-hosted switch goes off: a device already linked to a
        // private portal keeps syncing with it, and dropping the address here would point the next sign-in
        // at the official one without saying so.
        PortalServer = PortalServer.Trim(),
        UdpMedia = ViewerUdpMedia,
        ForceRelay = ViewerForceRelay,
    };

    public override HostConfig Apply(HostConfig config) => config with
    {
        UseDirectoryServers = !SelfHosted,
        RendezvousServer = SelfHosted ? RendezvousServer.Trim() : string.Empty,
        ServerPublicKeyBase64 = SelfHosted ? StorableKey(config.ServerPublicKeyBase64) : string.Empty,
        DirectAccessPort = DirectAccessPort.Value,
        UdpMedia = HostUdpMedia,
        ForceRelay = HostForceRelay,
    };

    /// <summary>Downloads the signing key from the rendezvous server's HTTP port (21114 by default).</summary>
    [RelayCommand]
    private async Task FetchKeyAsync()
    {
        string server = RendezvousServer.Trim();
        if (server.Length == 0)
        {
            Notice = Strings.Get("settings.serverRequired");
            return;
        }

        try
        {
            ServerPublicKey = await SettingsViewModel.FetchKeyFromServerAsync(server, CancellationToken.None);
            Notice = Strings.Format("settings.keyFetched", SettingsViewModel.KeyFingerprint(ServerPublicKey));
        }
        catch (Exception e)
        {
            Notice = Strings.Format("settings.keyFetchFailed", e.Message);
        }
    }
}
