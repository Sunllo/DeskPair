using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskPair.Core.Config;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Platform.Abstractions.Audio;

namespace DeskPair.Desktop.ViewModels.Settings;

/// <summary>Sound in both directions: what this desk sends out, and how a session is played back here.</summary>
public partial class AudioSettingsViewModel : SettingsSectionBase
{
    public AudioSettingsViewModel()
    {
        foreach (AudioDeviceInfo device in DesktopPlatform.PlaybackDevices())
        {
            PlaybackDevices.Add(device);
        }
    }

    /// <summary>Speakers and headphones on this computer; the host captures what plays on one of them.</summary>
    public ObservableCollection<AudioDeviceInfo> PlaybackDevices { get; } = [];

    public bool HasDevices => PlaybackDevices.Count > 0;

    // ---- this computer sends sound out ----

    /// <summary>0 disabled, 1 send what plays on the chosen device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransmitting))]
    public partial int TransmitIndex { get; set; }

    [ObservableProperty]
    public partial int CaptureDeviceIndex { get; set; }

    public bool IsTransmitting => TransmitIndex == 1;

    // ---- sound coming from the other side ----

    /// <summary>0 disabled, 1 the standard device, 2 a chosen device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingPlayback))]
    public partial int PlaybackIndex { get; set; }

    [ObservableProperty]
    public partial int PlaybackDeviceIndex { get; set; }

    [ObservableProperty]
    public partial bool Exclusive { get; set; }

    public bool IsChoosingPlayback => PlaybackIndex == 2;

    public string DeviceHint => Strings.Get("settings.audioDeviceHint");

    public override void Load(DesktopConfig desktop, HostConfig? host)
    {
        using IDisposable loading = Loading();
        TransmitIndex = host?.AudioEnabled ?? true ? 1 : 0;
        CaptureDeviceIndex = IndexOf(host?.AudioCaptureDeviceId);
        PlaybackIndex = !desktop.AudioEnabled ? 0 : desktop.AudioPlaybackDeviceId.Length > 0 ? 2 : 1;
        PlaybackDeviceIndex = IndexOf(desktop.AudioPlaybackDeviceId);
        Exclusive = desktop.AudioExclusive;
    }

    public override DesktopConfig Apply(DesktopConfig config) => config with
    {
        AudioEnabled = PlaybackIndex != 0,
        AudioPlaybackDeviceId = PlaybackIndex == 2 ? DeviceId(PlaybackDeviceIndex) : string.Empty,
        AudioExclusive = Exclusive,
    };

    public override HostConfig Apply(HostConfig config) => config with
    {
        AudioEnabled = TransmitIndex == 1,
        AudioCaptureDeviceId = TransmitIndex == 1 ? DeviceId(CaptureDeviceIndex) : string.Empty,
    };

    protected override bool IsTransient(string propertyName) => propertyName is "IsTransmitting" or "IsChoosingPlayback" or "HasDevices" or "DeviceHint";

    private int IndexOf(string? id) =>
        id is { Length: > 0 } ? Math.Max(0, PlaybackDevices.ToList().FindIndex(d => d.Id == id)) : DefaultIndex();

    private int DefaultIndex()
    {
        int index = PlaybackDevices.ToList().FindIndex(d => d.IsDefault);
        return Math.Max(0, index);
    }

    private string DeviceId(int index) =>
        index >= 0 && index < PlaybackDevices.Count ? PlaybackDevices[index].Id : string.Empty;
}
