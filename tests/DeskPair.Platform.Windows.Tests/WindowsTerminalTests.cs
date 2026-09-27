using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Terminal;
using DeskPair.Core.Transport;
using DeskPair.Integration.Tests;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Platform.Windows.Terminal;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// A real shell behind ConPTY. A pseudo console needs no desktop, so unlike capture these run in a
/// disconnected session and under a service too. The output is read the way the viewer will read it --
/// through <see cref="TerminalScreen"/> -- because ConPTY re-renders what the shell writes, and matching
/// its raw bytes would test ConPTY's rendering rather than ours.
/// </summary>
public class WindowsTerminalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>Everything the shell has shown so far, fed into a screen, read back as text.</summary>
    private sealed class Watcher
    {
        private readonly TerminalScreen _screen;
        private readonly Task _pump;

        public Watcher(ITerminal terminal, int columns = 120, int rows = 40)
        {
            _screen = new TerminalScreen(columns, rows);
            _pump = Task.Run(async () =>
            {
                byte[] buffer = new byte[8192];
                int n;
                while ((n = await terminal.Output.ReadAsync(buffer)) > 0)
                {
                    lock (_screen)
                    {
                        _screen.Feed(buffer.AsSpan(0, n));
                    }
                }
            });
        }

        public Task Finished => _pump;

        public string Text
        {
            get
            {
                lock (_screen)
                {
                    var sb = new StringBuilder();
                    foreach (TerminalCell[] line in _screen.Scrollback)
                    {
                        sb.AppendLine(string.Concat(line.Select(c => c.Text)).TrimEnd());
                    }

                    for (int r = 0; r < _screen.Rows; r++)
                    {
                        sb.AppendLine(_screen.RowText(r));
                    }

                    return sb.ToString();
                }
            }
        }

        public async Task WaitForAsync(string text)
        {
            DateTime deadline = DateTime.UtcNow + Patience;
            while (!Text.Contains(text, StringComparison.Ordinal))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"The shell never showed \"{text}\". It showed:\n{Text}");
                }

                await Task.Delay(50);
            }
        }
    }

    private static Task SendAsync(ITerminal t, string text) => t.WriteAsync(Encoding.UTF8.GetBytes(text), CancellationToken.None).AsTask();

    [Fact]
    public async Task A_shell_runs_a_command_and_its_exit_code_comes_back()
    {
        var host = new WindowsTerminalHost(NullLogger.Instance);
        await using ITerminal shell = await host.StartAsync(TerminalRunAs.Highest, 120, 40, CancellationToken.None);
        var watcher = new Watcher(shell);
        shell.Identity.ShouldBe(host.DescribeIdentity(TerminalRunAs.Highest));
        shell.Shell.ShouldEndWith(".exe");

        // The answer is computed, so seeing it proves the command ran; the echo of the command line alone would not.
        await SendAsync(shell, "echo (\"dp-\" + 6*7)\r");
        await watcher.WaitForAsync("dp-42");

        await SendAsync(shell, "exit 3\r");
        (await shell.Exited.WaitAsync(Patience)).ShouldBe(3);
        await watcher.Finished.WaitAsync(Patience); // output ends once the shell has gone
    }

    [Fact]
    public async Task Resizing_reaches_the_shell()
    {
        var host = new WindowsTerminalHost(NullLogger.Instance);
        await using ITerminal shell = await host.StartAsync(TerminalRunAs.Highest, 80, 24, CancellationToken.None);
        var watcher = new Watcher(shell);

        shell.Resize(117, 33);
        await SendAsync(shell, "echo (\"cols-\" + [Console]::WindowWidth + \"x\" + [Console]::WindowHeight)\r");

        await watcher.WaitForAsync("cols-117x33");
    }

    /// <summary>Disposing kills the shell and what it started: the job object, not a hope.</summary>
    [Fact]
    public async Task Disposing_ends_the_shell_and_its_children()
    {
        var host = new WindowsTerminalHost(NullLogger.Instance);
        ITerminal shell = await host.StartAsync(TerminalRunAs.Highest, 120, 40, CancellationToken.None);
        var watcher = new Watcher(shell);

        // A child that would outlive its shell if it could: ping for a minute. The pid is printed between
        // markers that the command line itself does not contain, so the echo of what was typed cannot match.
        await SendAsync(shell, "$p = Start-Process -PassThru -WindowStyle Hidden ping.exe -ArgumentList '-n','60','127.0.0.1'; echo ('pid' + '=' + $p.Id + '=')\r");
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Match.Empty;
        await Testbed.WaitUntilAsync(() => (m = System.Text.RegularExpressions.Regex.Match(watcher.Text, @"pid=(\d+)=")).Success, "the child's pid", 20_000);
        int pid = int.Parse(m.Groups[1].Value);
        using System.Diagnostics.Process child = System.Diagnostics.Process.GetProcessById(pid);

        await shell.DisposeAsync();

        child.WaitForExit((int)Patience.TotalMilliseconds).ShouldBeTrue("the job took the child with it");
        shell.Exited.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Interrupt_stops_the_running_command_and_keeps_the_shell()
    {
        var host = new WindowsTerminalHost(NullLogger.Instance);
        await using ITerminal shell = await host.StartAsync(TerminalRunAs.Highest, 120, 40, CancellationToken.None);
        var watcher = new Watcher(shell);

        await SendAsync(shell, "ping -n 60 127.0.0.1\r");
        // A reply, not the echo of the command line: interrupting before ping is running interrupts nothing.
        await watcher.WaitForAsync("TTL=");
        shell.Signal(TerminalSignalKind.Interrupt);
        await watcher.WaitForAsync("Control-C");

        // The console empties its input buffer when it handles Ctrl+C, so typing is for after the prompt is back.
        await Testbed.WaitUntilAsync(() => watcher.Text.TrimEnd().EndsWith('>'), "the prompt after the interrupt", 20_000);
        await SendAsync(shell,"echo (\"after-\" + 5*5)\r");

        await watcher.WaitForAsync("after-25");
        shell.Exited.IsCompleted.ShouldBeFalse();
    }

    /// <summary>
    /// Whose shell a request gets. Under the service the engine is SYSTEM and "user" means the person signed
    /// in; with nobody signed in there is nobody to be, and the identity is empty so the start is refused
    /// rather than quietly handing out SYSTEM instead.
    /// </summary>
    [Theory]
    [InlineData(TerminalRunAs.Highest, true, "alice", @"NT AUTHORITY\SYSTEM", false)]
    [InlineData(TerminalRunAs.User, true, @"HOMEPC\alice", @"HOMEPC\alice", true)]
    [InlineData(TerminalRunAs.User, true, null, "", true)]
    [InlineData(TerminalRunAs.User, false, null, @"HOMEPC\alice", false)]
    [InlineData(TerminalRunAs.Highest, false, null, @"HOMEPC\alice", false)]
    public void The_identity_follows_the_request_and_the_engine(TerminalRunAs runAs, bool engineIsSystem, string? signedIn, string expected, bool usesToken)
    {
        string engine = engineIsSystem ? @"NT AUTHORITY\SYSTEM" : @"HOMEPC\alice";

        (string identity, bool token) = WindowsShellIdentity.Plan(runAs, engineIsSystem, engine, signedIn);

        identity.ShouldBe(expected);
        token.ShouldBe(usesToken);
    }

    [Fact]
    public void PowerShell_is_preferred_and_cmd_is_the_fallback()
    {
        WindowsShell.Choose(@"C:\Windows", @"C:\Windows\system32\cmd.exe", _ => true).CommandLine
            .ShouldBe("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo");
        WindowsShell.Choose(@"C:\Windows", @"C:\Windows\system32\cmd.exe", p => p.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)).Path
            .ShouldBe(@"C:\Windows\system32\cmd.exe");
    }

    /// <summary>The whole path: viewer, protocol, host module, ConPTY, and back.</summary>
    [Fact]
    public async Task A_viewer_opens_a_real_shell_over_a_session()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(new HostPolicy { TerminalEnabled = true }, terminal: new WindowsTerminalHost(NullLogger.Instance));
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(connType: ConnType.ConnTerminal);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        await session.OpenTerminalAsync(1, 120, 40);
        await Testbed.WaitUntilAsync(() => { lock (cb.Terminal) { return cb.Terminal.Any(r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened); } }, "the shell to open", 20_000);
        await session.SendTerminalInputAsync(1, "echo (\"over-\" + 11*11)\r"u8.ToArray());

        var screen = new TerminalScreen(120, 40);
        await Testbed.WaitUntilAsync(() =>
        {
            screen = new TerminalScreen(120, 40);
            screen.Feed(Encoding.UTF8.GetBytes(cb.TerminalText(1)));
            return Enumerable.Range(0, screen.Rows).Any(r => screen.RowText(r).Contains("over-121", StringComparison.Ordinal));
        }, "the answer on the viewer's screen", 20_000);
    }
}
