using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels.Settings;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The buttons on the security page have to be pressable.
///
/// Obvious, until the first version of the unattended-access switch shipped two buttons that were both
/// permanently grey. [RelayCommand] on a method taking a bool generates a command with a bool parameter,
/// and CommandParameter="True" in XAML is the string "True": the types do not match, CanExecute answers
/// no, and the result looks exactly like a computer that cannot do the thing. Nothing failed, nothing was
/// logged, and the build was clean.
/// </summary>
[Collection("ProcessState")] // reads its notices in English, and they go to the one toast
public class SecuritySettingsViewModelTests
{
    private static SecuritySettingsViewModel Create() =>
        new(new HostLink("ui", NullLogger.Instance));

    [Fact]
    public void Turning_unattended_access_on_is_offered_when_it_is_off()
    {
        SecuritySettingsViewModel vm = Create();
        vm.UnattendedAccessInstalled = false;

        vm.TurnOnUnattendedAccessCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void Turning_unattended_access_off_is_offered_when_it_is_on()
    {
        SecuritySettingsViewModel vm = Create();
        vm.UnattendedAccessInstalled = true;

        vm.TurnOffUnattendedAccessCommand.CanExecute(null).ShouldBeTrue();
    }

    /// <summary>
    /// Seen on a real machine: the service just installed, the app between engines, and the first thing clicked
    /// was setting a password. The exception went up through the command to the UI thread and took the app
    /// down, three restarts in a row.
    /// </summary>
    [Fact]
    public async Task Setting_a_password_with_no_engine_to_ask_says_so_instead_of_crashing()
    {
        SecuritySettingsViewModel vm = Create();
        vm.PermanentPassword = "a-long-enough-password";

        await vm.SetPermanentPasswordCommand.ExecuteAsync(null);
        vm.Notice.ShouldContain("not connected");

        await vm.ClearPermanentPasswordCommand.ExecuteAsync(null);
        vm.Notice.ShouldContain("not connected");
    }
}
