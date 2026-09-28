using DeskPair.Desktop.Services.Update;

namespace DeskPair.Desktop.Tests;

/// <summary>Each script is read here for the line that would leave a machine broken if it were missing.</summary>
public class UpdateScriptsTests
{
    [Fact]
    public void Windows_waits_for_the_process_copies_over_the_install_and_relaunches()
    {
        string script = UpdateScripts.Windows(4242, @"C:\Temp\deskpair-update\stage", @"C:\Program Files\DeskPair", @"C:\Program Files\DeskPair\DeskPair.exe");

        // Copying over a running executable fails on Windows, so the wait is the line that matters most.
        script.ShouldContain("PID eq 4242");
        script.ShouldContain("goto wait");
        script.ShouldContain("robocopy \"C:\\Temp\\deskpair-update\\stage\" \"C:\\Program Files\\DeskPair\"");
        script.ShouldContain("start \"\" \"C:\\Program Files\\DeskPair\\DeskPair.exe\"");
        // robocopy exits 1 for "files copied", so only 8 and above are failures.
        script.ShouldContain("if errorlevel 8");
        script.ShouldContain("del \"%~f0\"");
        script.ShouldContain("\r\n");
    }

    [Fact]
    public void An_installed_copy_waits_for_the_process_has_windows_installer_upgrade_it_and_relaunches()
    {
        string script = UpdateScripts.WindowsInstaller(
            4242,
            @"C:\Temp\deskpair-update\DeskPair-0.4.4-win-x64.msi",
            @"C:\Program Files\Sunllo\DeskPair\DeskPair.exe",
            @"C:\Temp\deskpair-update\install.log");

        // Windows Installer cannot replace a program that is running, so the wait comes first here too.
        script.ShouldContain("PID eq 4242");
        script.ShouldContain("goto wait");
        // /wait, or the program would be started again while the installer is still replacing it; /passive shows
        // progress and asks nothing; /norestart leaves a restart to whoever is at the computer.
        script.ShouldContain("start \"\" /wait msiexec.exe /i \"C:\\Temp\\deskpair-update\\DeskPair-0.4.4-win-x64.msi\" /passive /norestart /l*v \"C:\\Temp\\deskpair-update\\install.log\"");
        script.IndexOf("msiexec", StringComparison.Ordinal).ShouldBeLessThan(script.IndexOf("start \"\" \"C:\\Program Files", StringComparison.Ordinal));
        // Never robocopy over what Windows Installer owns.
        script.ShouldNotContain("robocopy");
        script.ShouldContain("del \"%~f0\"");
        script.ShouldContain("\r\n");
    }

    [Fact]
    public void MacOS_replaces_the_bundle_with_ditto_detaches_the_image_and_opens_the_new_one()
    {
        string script = UpdateScripts.MacOS(7, "/tmp/mnt/DeskPair.app", "/Applications/DeskPair.app", "/tmp/mnt");

        script.ShouldStartWith("#!/bin/sh\n");
        script.ShouldContain("while kill -0 7 2>/dev/null; do sleep 1; done");
        script.ShouldContain("rm -rf '/Applications/DeskPair.app'");
        // ditto, not cp: it keeps the signature and the notarisation staple, which are the app's identity to TCC.
        script.ShouldContain("ditto '/tmp/mnt/DeskPair.app' '/Applications/DeskPair.app'");
        script.ShouldContain("hdiutil detach '/tmp/mnt'");
        script.ShouldContain("open -n '/Applications/DeskPair.app'");
        script.ShouldNotContain("\r");
    }

    [Fact]
    public void Linux_renames_the_new_file_over_the_old_and_starts_it()
    {
        string script = UpdateScripts.Linux(9, "/tmp/deskpair-update/extracted/DeskPair-0.2.2-linux-x64/DeskPair", "/home/alice/deskpair-run/DeskPair");

        // mv, not cp: a rename is atomic, so there is never a half-written program at the path.
        script.ShouldContain("mv -f '/tmp/deskpair-update/extracted/DeskPair-0.2.2-linux-x64/DeskPair' '/home/alice/deskpair-run/DeskPair'");
        script.ShouldContain("chmod +x");
        script.ShouldContain("'/home/alice/deskpair-run/DeskPair' >/dev/null 2>&1 &");
    }

    [Fact]
    public void Paths_with_quotes_in_them_cannot_break_out_of_the_shell_quoting()
    {
        UpdateScripts.Quote("it's here").ShouldBe("'it'\\''s here'");
        string script = UpdateScripts.Linux(1, "/a/it's/DeskPair", "/b/DeskPair");
        script.ShouldContain("'/a/it'\\''s/DeskPair'");
    }
}
