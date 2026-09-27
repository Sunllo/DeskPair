using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Linux.Terminal;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The root terminal's rules, which run anywhere: what the engine may ask the daemon, where the daemon's
/// permission comes from, and exactly what it hands to systemd.
/// </summary>
public class DaemonTerminalTests
{
    [Fact]
    public void A_shell_request_round_trips_and_carries_only_numbers()
    {
        Span<byte> buffer = stackalloc byte[8];
        DrmWire.WriteOpenTerminal(buffer, runAs: 1, columns: 120, rows: 40, describeOnly: true).ShouldBe(8);

        DrmWire.TryReadOpenTerminal(buffer, out byte runAs, out int columns, out int rows, out bool describe).ShouldBeTrue();
        (runAs, columns, rows, describe).ShouldBe(((byte)1, 120, 40, true));
    }

    /// <summary>The daemon is root and the engine is not: anything malformed is refused at the wire, not trusted further in.</summary>
    [Fact]
    public void A_malformed_shell_request_is_not_read()
    {
        byte[] good = new byte[8];
        DrmWire.WriteOpenTerminal(good, 0, 80, 24, false);

        DrmWire.TryReadOpenTerminal(good.AsSpan(0, 7), out _, out _, out _, out _).ShouldBeFalse("too short");
        DrmWire.TryReadOpenTerminal([.. good, 0], out _, out _, out _, out _).ShouldBeFalse("anything appended, a path say, is not ignored but refused");
        byte[] identity = [.. good];
        identity[3] = 2;
        DrmWire.TryReadOpenTerminal(identity, out _, out _, out _, out _).ShouldBeFalse("only two identities exist");
        byte[] zero = [.. good];
        zero[4] = zero[5] = 0;
        DrmWire.TryReadOpenTerminal(zero, out _, out _, out _, out _).ShouldBeFalse("a zero-column terminal");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(9, true)]
    [InlineData(15, true)]
    [InlineData(2, false)]
    [InlineData(19, false)]
    public void Only_the_signals_a_terminal_needs_are_carried(int signal, bool carried)
    {
        byte[] buffer = new byte[8];
        DrmWire.WriteTerminalSignal(buffer, handle: 3, signal);

        DrmWire.TryReadTerminalSignal(buffer, out int handle, out int read).ShouldBe(carried);
        if (carried)
        {
            (handle, read).ShouldBe((3, signal));
        }
    }

    [Fact]
    public void The_answers_round_trip()
    {
        byte[] buffer = new byte[512];
        int n = DrmWire.WriteTerminal(buffer, 5, "root\n/bin/bash", fdAttached: true);
        DrmWire.TryReadTerminal(buffer.AsSpan(0, n), out int handle, out string identity, out bool fd).ShouldBeTrue();
        (handle, identity, fd).ShouldBe((5, "root\n/bin/bash", true));

        n = DrmWire.WriteTerminalState(buffer, 5, exited: true, code: 7);
        DrmWire.TryReadTerminalState(buffer.AsSpan(0, n), out handle, out bool exited, out int code).ShouldBeTrue();
        (handle, exited, code).ShouldBe((5, true, 7));

        n = DrmWire.WriteRefusal(buffer, "no");
        DrmWire.IsRefusal(buffer.AsSpan(0, n)).ShouldBeTrue();
        DrmWire.TryReadError(buffer.AsSpan(0, n), out string message).ShouldBeTrue();
        message.ShouldBe("no");
        n = DrmWire.WriteError(buffer, "failed");
        DrmWire.IsRefusal(buffer.AsSpan(0, n)).ShouldBeFalse("a failure is not a refusal: the engine does not fall back on it");
    }

    /// <summary>A long reason used to throw inside the daemon's reply and take the channel down with it.</summary>
    [Fact]
    public void A_long_error_is_cut_to_fit_rather_than_thrown()
    {
        byte[] small = new byte[96];
        int n = DrmWire.WriteError(small, new string('x', 500) + "中文");

        n.ShouldBeLessThanOrEqualTo(96);
        DrmWire.TryReadError(small.AsSpan(0, n), out string message).ShouldBeTrue();
        message.Length.ShouldBe(92);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("terminal-root = yes", true)]
    [InlineData("terminal-root=YES", true)]
    [InlineData("terminal-root = no", false)]
    [InlineData("# terminal-root = yes", false)]
    [InlineData("terminal-root = yes\nterminal-root = no", false)]
    [InlineData("terminal-root = 1", false)]
    [InlineData("terminal-rooted = yes", false)]
    public void The_gate_opens_only_on_an_explicit_yes(string? text, bool allowed) =>
        DaemonTerminalConfig.AllowsRoot(text).ShouldBe(allowed);

    /// <summary>
    /// The one line of this feature that must never change: the gate is a root-owned file under /etc, not
    /// anything in the engine's data directory, which the deskpair account can write.
    /// </summary>
    [Fact]
    public void The_gate_is_a_file_the_engine_cannot_write()
    {
        DaemonTerminalConfig.Path.ShouldBe("/etc/deskpair/daemon.conf");
        DaemonTerminalConfig.Path.ShouldNotStartWith("/var/lib/deskpair");
        DaemonTerminalConfig.AllowsRoot(DaemonTerminalConfig.Default).ShouldBeTrue("the install writes yes, the owner's decision");
    }

    [Fact]
    public void Systemd_is_handed_constants_numbers_and_passwd_values_only()
    {
        string[] args = TerminalUnit.Arguments("deskpair-shell-1-2", "/dev/pts/4", "root", "/root", "/bin/bash", "C.UTF-8");

        args.ShouldContain("--unit=deskpair-shell-1-2");
        args.ShouldContain("--property=TTYPath=/dev/pts/4");
        args.ShouldContain("--property=StandardInput=tty", "the pty becomes the controlling terminal");
        args.ShouldContain("--property=KillMode=control-group", "stopping the unit ends everything the shell started");
        args.ShouldContain("--uid=root");
        args.ShouldContain("--working-directory=/root");
        args[^3..].ShouldBe(["--", "/bin/bash", "-l"], "the program comes last, after --, as a login shell");
        args.ShouldAllBe(a => !a.Contains('$'), "systemd expands $ in a command line; nothing here may carry one");
    }

    [Theory]
    [InlineData("LoadState=loaded\nActiveState=active\nExecMainStatus=0", false, 0)]
    [InlineData("LoadState=loaded\nActiveState=failed\nExecMainStatus=7", true, 7)]
    [InlineData("LoadState=loaded\nActiveState=inactive\nExecMainStatus=0", true, 0)]
    [InlineData("LoadState=not-found\nActiveState=inactive\nExecMainStatus=0", true, 0)]
    public void A_unit_state_reads_as_ended_or_not(string show, bool exited, int code) =>
        TerminalUnit.ParseState(show).ShouldBe((exited, code));

    [Fact]
    public void With_the_gate_closed_the_answer_is_a_refusal_and_nothing_starts()
    {
        var terminals = new DaemonTerminals(NullLogger.Instance, gate: () => false, seat: () => null);
        byte[] request = new byte[8];
        DrmWire.WriteOpenTerminal(request, 0, 80, 24, describeOnly: false);
        byte[] reply = new byte[512];

        int n = terminals.Handle(request, reply, out int fd);

        fd.ShouldBe(-1);
        DrmWire.IsRefusal(reply.AsSpan(0, n)).ShouldBeTrue();
    }

    [Fact]
    public void An_unknown_handle_is_reported_as_ended_not_signalled()
    {
        var terminals = new DaemonTerminals(NullLogger.Instance, gate: () => true, seat: () => null);
        byte[] request = new byte[8];
        DrmWire.WriteTerminalSignal(request, handle: 42, signal: 9);
        byte[] reply = new byte[512];

        int n = terminals.Handle(request, reply, out _);

        DrmWire.TryReadTerminalState(reply.AsSpan(0, n), out int handle, out bool exited, out _).ShouldBeTrue();
        (handle, exited).ShouldBe((42, true));
    }
}
