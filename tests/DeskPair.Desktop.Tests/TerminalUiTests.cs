using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Terminal;
using DeskPair.Desktop.Controls;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Desktop.ViewModels.Settings;
using DeskPair.Protocol.Ipc;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The two deliberate acts a shell on this computer takes -- the owner turning terminals on, and the
/// person at the desk ticking the box on a request -- and how the terminal is coloured.
/// </summary>
[Collection("ProcessState")] // the connection manager's labels read the string table
public class TerminalUiTests : IDisposable
{
    private readonly string _was = Strings.Language;

    public TerminalUiTests() => Strings.Language = AppLanguages.En;

    public void Dispose() => Strings.Language = _was;

    private static SecuritySettingsViewModel Settings() => new(new HostLink("ui", NullLogger.Instance));

    [Fact]
    public void Turning_the_terminal_on_takes_a_confirmation()
    {
        SecuritySettingsViewModel vm = Settings();
        vm.Load(new DesktopConfig(), new Core.Config.HostConfig());
        vm.TerminalEnabled.ShouldBeFalse("off unless the owner turned it on");

        vm.BeginEnableTerminalCommand.Execute(null);
        vm.TerminalConfirming.ShouldBeTrue();
        vm.TerminalEnabled.ShouldBeFalse("asking is not turning on");

        vm.ConfirmEnableTerminalCommand.Execute(null);
        vm.TerminalEnabled.ShouldBeTrue();
        vm.TerminalConfirming.ShouldBeFalse();
        vm.Apply(new Core.Config.HostConfig()).TerminalEnabled.ShouldBeTrue();
    }

    /// <summary>With unattended access and only a one-time password, a terminal would be open to whoever reads that password off the screen.</summary>
    [Fact]
    public void An_unattended_machine_needs_a_permanent_password_first()
    {
        SecuritySettingsViewModel vm = Settings();
        vm.Load(new DesktopConfig(), new Core.Config.HostConfig());
        vm.UnattendedAccessInstalled = true;
        vm.HasPermanentPassword = false;

        vm.TerminalNeedsPassword.ShouldBeTrue();
        vm.BeginEnableTerminalCommand.Execute(null);
        vm.ConfirmEnableTerminalCommand.Execute(null);
        vm.TerminalEnabled.ShouldBeFalse();

        vm.HasPermanentPassword = true;
        vm.TerminalNeedsPassword.ShouldBeFalse();
        vm.ConfirmEnableTerminalCommand.Execute(null);
        vm.TerminalEnabled.ShouldBeTrue();
    }

    [Fact]
    public void The_identity_choice_round_trips()
    {
        SecuritySettingsViewModel vm = Settings();
        vm.Load(new DesktopConfig(), new Core.Config.HostConfig { TerminalEnabled = true, TerminalRunsAs = "user" });

        vm.TerminalRunsAsIndex.ShouldBe(1);
        vm.Apply(new Core.Config.HostConfig()).TerminalRunsAs.ShouldBe("user");
        vm.TerminalRunsAsIndex = 0;
        vm.Apply(new Core.Config.HostConfig()).TerminalRunsAs.ShouldBe("system");
        vm.DisableTerminalCommand.Execute(null);
        vm.Apply(new Core.Config.HostConfig()).TerminalEnabled.ShouldBeFalse();
    }

    [Fact]
    public void A_terminal_request_cannot_be_accepted_until_its_box_is_ticked()
    {
        var owner = new ConnectionManagerViewModel(new HostLink("cm", NullLogger.Instance));
        var request = new CmConnection(owner, 7) { IsTerminal = true, TerminalIdentity = "root" };

        request.Terminal.ShouldBeFalse();
        request.CanAccept.ShouldBeFalse();
        request.TerminalLabel.ShouldContain("root", customMessage: "the box says whose shell it is");
        ConnectionManagerViewModel.Decision(request, accept: true).Accept.ShouldBeFalse("accepting without the box is a refusal");

        request.Terminal = true;
        request.CanAccept.ShouldBeTrue();
        ApprovalDecision yes = ConnectionManagerViewModel.Decision(request, accept: true);
        yes.Accept.ShouldBeTrue();
        yes.Granted.ShouldContain(Permission.PermTerminal);
    }

    [Fact]
    public void A_desktop_request_never_grants_a_terminal()
    {
        var owner = new ConnectionManagerViewModel(new HostLink("cm", NullLogger.Instance));
        var request = new CmConnection(owner, 8);

        request.CanAccept.ShouldBeTrue();
        request.ShowStandardPermissions.ShouldBeTrue();
        ConnectionManagerViewModel.Decision(request, accept: true).Granted.ShouldNotContain(Permission.PermTerminal);
    }

    [Theory]
    [InlineData(16, 0, 0, 0)]
    [InlineData(21, 0, 0, 255)]
    [InlineData(196, 255, 0, 0)]
    [InlineData(231, 255, 255, 255)]
    [InlineData(232, 8, 8, 8)]
    [InlineData(255, 238, 238, 238)]
    public void The_256_colour_cube_and_grey_ramp(int index, byte r, byte g, byte b) =>
        TerminalPalette.Indexed(index).ShouldBe(Color.FromRgb(r, g, b));

    [Fact]
    public void Attributes_change_colour_as_xterm_does()
    {
        (Color fg, Color bg) = TerminalPalette.Resolve(new CellColors(CellColors.Palette(1), CellColors.Default, CellAttributes.Bold));
        fg.ShouldBe(TerminalPalette.Indexed(9), "bold draws the first eight bright");
        bg.ShouldBe(TerminalPalette.Background);

        (fg, bg) = TerminalPalette.Resolve(new CellColors(CellColors.Rgb(1, 2, 3), CellColors.Palette(4), CellAttributes.Inverse));
        fg.ShouldBe(TerminalPalette.Indexed(4));
        bg.ShouldBe(Color.FromRgb(1, 2, 3));

        (fg, bg) = TerminalPalette.Resolve(new CellColors(CellColors.Default, CellColors.Default, CellAttributes.Invisible));
        fg.ShouldBe(bg);
    }
}
