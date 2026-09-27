using CommunityToolkit.Mvvm.ComponentModel;
using DeskPair.Core.Config;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.ViewModels.Settings;

/// <summary>Keyboard and mouse: how keys are sent out, and what visitors may use on this desk.</summary>
public partial class InputSettingsViewModel : SettingsSectionBase
{
    [ObservableProperty]
    public partial bool KeyboardTranslate { get; set; }

    [ObservableProperty]
    public partial bool LockAfterSessionEnd { get; set; }

    [ObservableProperty]
    public partial bool KeyboardEnabled { get; set; }

    [ObservableProperty]
    public partial bool ClipboardEnabled { get; set; }

    [ObservableProperty]
    public partial bool RestartEnabled { get; set; }

    public override void Load(DesktopConfig desktop, HostConfig? host)
    {
        using IDisposable loading = Loading();
        KeyboardTranslate = desktop.KeyboardTranslateMode;
        LockAfterSessionEnd = desktop.LockAfterSessionEnd;
        KeyboardEnabled = host?.KeyboardEnabled ?? true;
        ClipboardEnabled = host?.ClipboardEnabled ?? true;
        RestartEnabled = host?.RestartEnabled ?? false;
    }

    public override DesktopConfig Apply(DesktopConfig config) => config with
    {
        KeyboardTranslateMode = KeyboardTranslate,
        LockAfterSessionEnd = LockAfterSessionEnd,
    };

    public override HostConfig Apply(HostConfig config) => config with
    {
        KeyboardEnabled = KeyboardEnabled,
        ClipboardEnabled = ClipboardEnabled,
        RestartEnabled = RestartEnabled,
    };
}
