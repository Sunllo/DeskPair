using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Terminal;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Platform.Linux.Terminal;
using DeskPair.Platform.Unix.Terminal;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>The rules for a Unix shell, which run anywhere, and a real one, which runs on Linux.</summary>
public class UnixTerminalTests
{
    [Theory]
    [InlineData("/bin/zsh", "/bin/zsh")]
    [InlineData("/usr/sbin/nologin", "/bin/bash")]
    [InlineData("/bin/false", "/bin/bash")]
    [InlineData("", "/bin/bash")]
    [InlineData(null, "/bin/bash")]
    [InlineData("/bin/fish-that-is-not-installed", "/bin/bash")]
    public void The_account_shell_unless_it_is_not_a_shell(string? account, string expected) =>
        UnixShell.Choose(account, p => p is "/bin/zsh" or "/bin/bash" or "/bin/sh" or "/usr/sbin/nologin" or "/bin/false").ShouldBe(expected);

    [Fact]
    public void Without_bash_it_is_sh() =>
        UnixShell.Choose("/usr/sbin/nologin", p => p == "/bin/sh").ShouldBe("/bin/sh");

    [Fact]
    public void It_starts_as_a_login_shell() =>
        UnixShell.Arguments("/usr/bin/bash").ShouldBe(["-bash"]);

    /// <summary>Built from nothing: the engine's own environment -- its token, its switches -- is not a shell's to see.</summary>
    [Fact]
    public void The_environment_is_built_not_inherited()
    {
        string[] env = UnixShell.Environment("deskpair", "/var/lib/deskpair", "/bin/bash", lang: null, mac: false);

        env.ShouldContain("HOME=/var/lib/deskpair");
        env.ShouldContain("USER=deskpair");
        env.ShouldContain("TERM=xterm-256color");
        env.ShouldContain("LANG=C.UTF-8");
        env.ShouldAllBe(e => !e.StartsWith("SUNLLO_", StringComparison.Ordinal));
        UnixShell.Environment("alice", "/Users/alice", "/bin/zsh", lang: null, mac: true).ShouldContain("LANG=en_US.UTF-8");
        UnixShell.Environment("alice", "/home/alice", "/bin/bash", lang: "zh_TW.UTF-8", mac: false).ShouldContain("LANG=zh_TW.UTF-8");
    }

    [Theory]
    [InlineData(0x0000, 0)]
    [InlineData(0x0300, 3)]
    [InlineData(0x0009, 137)] // SIGKILL
    [InlineData(0x0002, 130)] // SIGINT
    [InlineData(0x0089, 137)] // SIGKILL with the core-dump bit
    public void A_wait_status_reads_as_a_shell_would_report_it(int status, int code) =>
        UnixShell.ExitCode(status).ShouldBe(code);

    private static async Task<string> ScreenAfterAsync(ITerminal shell, Func<string, bool> until)
    {
        var screen = new TerminalScreen(100, 30);
        byte[] buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            int n = await shell.Output.ReadAsync(buffer, timeout.Token);
            if (n == 0)
            {
                break;
            }

            screen.Feed(buffer.AsSpan(0, n));
            string text = string.Join('\n', Enumerable.Range(0, screen.Rows).Select(screen.RowText));
            if (until(text))
            {
                return text;
            }
        }

        return string.Join('\n', Enumerable.Range(0, screen.Rows).Select(screen.RowText));
    }

    /// <summary>
    /// A real pty on Linux: the command runs, the shell has a controlling terminal (that is what `tty`
    /// and job control need), the size is what was asked for, and the exit code comes back.
    /// </summary>
    [Fact]
    public async Task A_real_shell_on_linux_has_a_controlling_terminal()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ITerminalHost host = LinuxTerminal.Create(NullLogger.Instance);
        await using ITerminal shell = await host.StartAsync(TerminalRunAs.Highest, 100, 30, CancellationToken.None);
        shell.Identity.ShouldBe(host.DescribeIdentity(TerminalRunAs.Highest));

        await shell.WriteAsync(Encoding.UTF8.GetBytes("echo ans=$((6*7)) tty=$(tty) size=$(stty size)\n"), CancellationToken.None);
        string text = await ScreenAfterAsync(shell, t => t.Contains("size=30 100", StringComparison.Ordinal));
        text.ShouldContain("ans=42");
        text.ShouldContain("tty=/dev/pts/");
        text.ShouldContain("size=30 100");

        await shell.WriteAsync("exit 5\n"u8.ToArray(), CancellationToken.None);
        (await shell.Exited.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(5);
    }

    /// <summary>Ctrl+C reaches the foreground job, which only happens when the pty is the controlling terminal.</summary>
    [Fact]
    public async Task Interrupt_ends_the_foreground_job_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using ITerminal shell = await LinuxTerminal.Create(NullLogger.Instance).StartAsync(TerminalRunAs.Highest, 100, 30, CancellationToken.None);
        await shell.WriteAsync("sleep 60; echo slept\n"u8.ToArray(), CancellationToken.None);
        await Task.Delay(500);
        shell.Signal(TerminalSignalKind.Interrupt);
        await shell.WriteAsync("echo after=$((5*5))\n"u8.ToArray(), CancellationToken.None);

        string text = await ScreenAfterAsync(shell, t => t.Contains("after=25", StringComparison.Ordinal));
        text.ShouldContain("after=25");

        // The command as typed is on the screen, "echo slept" and all; what must not be is the line it would print.
        text.Split('\n').Select(line => line.TrimEnd()).ShouldNotContain("slept");
    }

    /// <summary>Everything the shell started goes with it, not just the shell.</summary>
    [Fact]
    public async Task Disposing_kills_the_whole_session_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ITerminal shell = await LinuxTerminal.Create(NullLogger.Instance).StartAsync(TerminalRunAs.Highest, 100, 30, CancellationToken.None);
        await shell.WriteAsync("trap '' HUP; (trap '' HUP; sleep 300) & echo child=$!\n"u8.ToArray(), CancellationToken.None);
        string text = await ScreenAfterAsync(shell, t => System.Text.RegularExpressions.Regex.IsMatch(t, @"child=\d+"));
        int child = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"child=(\d+)").Groups[1].Value);

        await shell.DisposeAsync();

        await Task.Delay(300);
        Directory.Exists($"/proc/{child}").ShouldBeFalse("a child that ignored the hangup was killed with the session");
    }
}
