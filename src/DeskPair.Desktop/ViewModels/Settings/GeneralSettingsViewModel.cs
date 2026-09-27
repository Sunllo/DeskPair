using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Config;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.ViewModels.Settings;

/// <summary>One row of the language menu: a code from <see cref="AppLanguages"/>, or empty for "follow the system".</summary>
public sealed record LanguageOption(string Code, string Name);

/// <summary>Language, start-up behaviour, the name other people see, and where the files live.</summary>
public partial class GeneralSettingsViewModel : SettingsSectionBase
{
    private string _languageWas = Strings.Language;

    /// <summary>"System" first, then every published language under its own name, in the website's order.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(string.Empty, Strings.Get("settings.language.system")),
        .. AppLanguages.All.Select(l => new LanguageOption(l.Code, l.NativeName)),
    ];

    [ObservableProperty]
    public partial LanguageOption? SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial bool StartWithSystem { get; set; }

    [ObservableProperty]
    public partial bool StartMinimised { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial string DeviceName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FileTransferFolder { get; set; } = string.Empty;

    public NumericField RecentLimit { get; } = new(1, 200, 20, "settings.rangeInvalid");

    /// <summary>Where recordings are written; empty means the Videos folder.</summary>
    [ObservableProperty]
    public partial string RecordingFolder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RecordAudio { get; set; }

    /// <summary>
    /// Whether the last thing said was a complaint. Set before the message, because setting the message
    /// is what shows it.
    /// </summary>
    [ObservableProperty]
    public partial bool NoticeIsFailure { get; set; }

    protected override bool NoticeIsProblem => NoticeIsFailure;

    public bool IsStartupSupported => StartupEntry.IsSupported;

    public string VersionText => Strings.Format("settings.versionValue", App.Version);

    public string LogFolder => CrashReporter.LogDirectory;

    public override void Load(DesktopConfig desktop, HostConfig? host)
    {
        using IDisposable loading = Loading();
        RecentLimit.ValueChanged -= RaiseChanged;
        RecentLimit.ValueChanged += RaiseChanged;
        RecordingFolder = desktop.RecordingFolder;
        RecordAudio = desktop.RecordAudio;
        string configured = AppLanguages.IsKnown(desktop.Language) ? desktop.Language : string.Empty;
        SelectedLanguage = Languages.First(o => o.Code == configured);
        _languageWas = Strings.Language;
        StartWithSystem = StartupEntry.IsSupported ? StartupEntry.IsEnabled() : desktop.StartWithSystem;
        StartMinimised = desktop.StartMinimised;
        CloseToTray = desktop.CloseToTray;
        FileTransferFolder = desktop.FileTransferFolder;
        RecentLimit.Reset(desktop.RecentLimit);
        DeviceName = host?.DeviceName ?? string.Empty;
    }

    public override DesktopConfig Apply(DesktopConfig config) => config with
    {
        Language = SelectedLanguage is { Code.Length: > 0 } chosen ? chosen.Code : "system",
        StartWithSystem = StartWithSystem,
        StartMinimised = StartMinimised,
        CloseToTray = CloseToTray,
        FileTransferFolder = FileTransferFolder.Trim(),
        RecentLimit = RecentLimit.Value,
        RecordingFolder = RecordingFolder.Trim(),
        RecordAudio = RecordAudio,
    };

    public override HostConfig Apply(HostConfig config) => config with { DeviceName = DeviceName.Trim() };

    protected override bool IsTransient(string propertyName) =>
        propertyName is "NoticeIsFailure" or "VersionText" or "LogFolder";

    /// <summary>The language applies at once; windows opened from here on are in the new one.</summary>
    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (value is null)
        {
            return;
        }

        Strings.Language = value.Code.Length > 0 ? value.Code : "system";
        if (Strings.Language != _languageWas)
        {
            _languageWas = Strings.Language;

            // A toast, not a line on the page. It used to be written into Problem, which nothing ever
            // cleared: the page was one somebody sets once and leaves, so "saved, the new language applies
            // to windows opened from now on" sat under the recording settings in red until the window was
            // closed, reading as a complaint about them.
            NoticeIsFailure = false;
            Notice = Strings.Get("settings.languageRestart");
        }
    }

    /// <summary>The registry entry is the truth for "start when I sign in", so write it as the box is ticked.</summary>
    partial void OnStartWithSystemChanged(bool value) => ApplyStartup();

    partial void OnStartMinimisedChanged(bool value) => ApplyStartup();

    private void ApplyStartup()
    {
        if (StartupEntry.IsSupported && !StartupEntry.TryApply(StartWithSystem, StartMinimised, out string error))
        {
            NoticeIsFailure = true;
            Notice = Strings.Format("settings.startupFailed", error);
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            Process.Start(new ProcessStartInfo(LogFolder) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening a folder is a convenience; a shell that refuses is not worth reporting.
        }
    }
}
