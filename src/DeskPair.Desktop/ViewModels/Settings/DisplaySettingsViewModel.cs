using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Config;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.ViewModels.Settings;

/// <summary>Picture defaults for new sessions, plus the host's encoder preference and the displays it may add.</summary>
public partial class DisplaySettingsViewModel : SettingsSectionBase
{
    /// <summary>Quality names in the order the picker shows them.</summary>
    private static readonly string[] Qualities = ["low", "balanced", "best", "custom"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomQuality))]
    public partial int QualityIndex { get; set; }

    public NumericField CustomBitrateKbps { get; } = new(100, 200_000, 8000, "settings.rangeInvalid");

    public NumericField CustomFps { get; } = new(5, 120, 30, "settings.rangeInvalid");

    [ObservableProperty]
    public partial bool ShowRemoteCursor { get; set; }

    [ObservableProperty]
    public partial bool LosslessRefinement { get; set; }

    [ObservableProperty]
    public partial bool FitToWindow { get; set; }

    [ObservableProperty]
    public partial bool SmoothPlayback { get; set; }

    [ObservableProperty]
    public partial int CodecIndex { get; set; }

    public bool IsCustomQuality => QualityIndex == 3;

    /// <summary>Viewers may ask this computer for a display it does not have (<see cref="HostConfig.AllowVirtualDisplay"/>).</summary>
    [ObservableProperty]
    public partial bool AllowVirtualDisplay { get; set; }

    /// <summary>Only Windows can add a display; elsewhere the section is not shown.</summary>
    public bool VirtualDisplaysApply => OperatingSystem.IsWindows();

    [ObservableProperty]
    public partial bool DriverInstalled { get; set; }

    /// <summary>Whether a display can be added right now, and if not what is missing.</summary>
    [ObservableProperty]
    public partial string DriverStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DriverNotice { get; set; } = string.Empty;

    public override void Load(DesktopConfig desktop, HostConfig? host)
    {
        using IDisposable loading = Loading();
        CustomBitrateKbps.ValueChanged -= RaiseChanged;
        CustomBitrateKbps.ValueChanged += RaiseChanged;
        CustomFps.ValueChanged -= RaiseChanged;
        CustomFps.ValueChanged += RaiseChanged;
        QualityIndex = Math.Max(0, Array.IndexOf(Qualities, desktop.DefaultQuality));
        CustomBitrateKbps.Reset(desktop.CustomBitrateKbps);
        CustomFps.Reset(desktop.CustomFps);
        ShowRemoteCursor = desktop.ShowRemoteCursor;
        LosslessRefinement = desktop.LosslessRefinement;
        FitToWindow = desktop.FitToWindow;
        SmoothPlayback = desktop.SmoothPlayback;
        CodecIndex = host?.CodecPreference switch { "h264" => 1, "h265" => 2, "av1" => 3, "vp9" => 4, _ => 0 };
        AllowVirtualDisplay = host?.AllowVirtualDisplay ?? false;
        RefreshDriver();
    }

    /// <summary>Reads back what is installed: the driver, and the service whose engine is the only one able to use it.</summary>
    public void RefreshDriver()
    {
#if WINDOWS
        DriverInstalled = Platform.Windows.Capture.VirtualDisplayDriver.Installed(Engine.ServerRole.DefaultDataDir()) is not null;
        DriverStatus = !DriverInstalled
            ? Strings.Get("settings.displayDriver.missing")
            : UnattendedInstall.IsInstalled()
                ? Strings.Get("settings.displayDriver.ready")
                : Strings.Format("settings.displayDriver.needsService", Strings.Get("settings.unattended"), Strings.Get("settings.tab.security"));
#endif
    }

    [RelayCommand]
    private Task InstallDisplayDriverAsync() => RunDriverSetupAsync("--install-virtual-display");

    [RelayCommand]
    private Task RemoveDisplayDriverAsync() => RunDriverSetupAsync("--remove-virtual-display");

    /// <summary>Relaunches this program elevated for the one step that needs it, and says how it went.</summary>
    private async Task RunDriverSetupAsync(string argument)
    {
        DriverNotice = Strings.Get("settings.displayDriver.requested");
        int? code = await SecuritySettingsViewModel.ElevateAsync(argument).ConfigureAwait(true);
        DriverNotice = code switch
        {
            null => Strings.Get("settings.displayDriver.cancelled"),
            0 => string.Empty,
            _ => Strings.Get("settings.displayDriver.failed"),
        };
        RefreshDriver();
    }

    public override DesktopConfig Apply(DesktopConfig config) => config with
    {
        DefaultQuality = Qualities[Math.Clamp(QualityIndex, 0, Qualities.Length - 1)],
        CustomBitrateKbps = CustomBitrateKbps.Value,
        CustomFps = CustomFps.Value,
        ShowRemoteCursor = ShowRemoteCursor,
        LosslessRefinement = LosslessRefinement,
        FitToWindow = FitToWindow,
        SmoothPlayback = SmoothPlayback,
    };

    protected override bool IsTransient(string propertyName) =>
        propertyName is "IsCustomQuality" or nameof(DriverInstalled) or nameof(DriverStatus) or nameof(DriverNotice);

    public override HostConfig Apply(HostConfig config) => config with
    {
        CodecPreference = CodecIndex switch { 1 => "h264", 2 => "h265", 3 => "av1", 4 => "vp9", _ => "auto" },
        AllowVirtualDisplay = AllowVirtualDisplay,
    };
}
