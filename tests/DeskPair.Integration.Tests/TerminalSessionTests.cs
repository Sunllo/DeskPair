using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Integration.Tests;

/// <summary>
/// The security story of the terminal, end to end, against a fake shell: who may open one, what ends
/// it, and that nothing a viewer sends can turn the permission on. These are green before any real
/// shell exists, which is the point -- the real shell only changes what is behind the pty.
/// </summary>
public class TerminalSessionTests
{
    private static async Task<(ControllerSession Session, TestCallbacks Cb)> ConnectAsync(Testbed bed, HostRuntime host, string password, ConnType connType = ConnType.ConnTerminal)
    {
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(connType: connType);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        LoginResult login = await session.LoginAsync(password, CancellationToken.None);
        login.Success.ShouldBeTrue(login.Error?.Message);
        return (session, cb);
    }

    private static Task WaitForAsync(TestCallbacks cb, Func<TerminalResponse, bool> what, string why) =>
        Testbed.WaitUntilAsync(() =>
        {
            lock (cb.Terminal)
            {
                return cb.Terminal.Any(what);
            }
        }, why);

    [Fact]
    public async Task A_terminal_connection_opens_a_shell_types_into_it_and_the_record_says_who_it_ran_as()
    {
        await using Testbed bed = await Testbed.StartAsync();
        var shells = new FakeTerminalHost { Identity = "root" };
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(new HostPolicy { TerminalEnabled = true }, terminal: shells);
        string journalPath = Path.Combine(bed.NewTempDir(), "connections.jsonl");
        var journal = new ConnectionJournal(journalPath, TimeProvider.System, NullLogger.Instance);
        journal.Attach(host);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        session.PeerInfo!.Granted.ShouldContain(Permission.PermTerminal, "the owner switched it on");

        await session.OpenTerminalAsync(1, 100, 30);

        await WaitForAsync(cb, r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened && r.Opened.Identity == "root", "the shell to open as root");
        await Testbed.WaitUntilAsync(() => cb.TerminalText(1).Contains("$ "), "the prompt");
        shells.Started.ShouldHaveSingleItem().Columns.ShouldBe(100);

        await session.SendTerminalInputAsync(1, "apt update\n"u8.ToArray());

        await Testbed.WaitUntilAsync(() => cb.TerminalText(1).Contains("apt: ok"), "the shell's answer");
        HostSessionContext ctx = host.Sessions.Single().Context;
        ctx.TerminalOpens.ShouldBe(1);
        ctx.TerminalIdentity.ShouldBe("root");

        await session.CloseTerminalAsync(1);
        await WaitForAsync(cb, r => r.UnionCase == TerminalResponse.UnionOneofCase.Exit && r.Exit.Reason == "closed by the viewer", "the exit notice");
        shells.Started[0].Disposed.ShouldBeTrue();

        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 0, "the session to end");
        await Testbed.WaitUntilAsync(() => journal.Read(10).Any(r => r.EndedUtc is not null), "the record to close");
        ConnectionRecord record = journal.Read(10).Single();
        record.Kind.ShouldBe("terminal");
        record.TerminalOpens.ShouldBe(1);
        record.TerminalIdentity.ShouldBe("root");
    }

    /// <summary>Off by default; a viewer that tries anyway loses the session, not just the request.</summary>
    [Fact]
    public async Task Without_the_permission_a_terminal_action_ends_the_session_and_no_shell_starts()
    {
        await using Testbed bed = await Testbed.StartAsync();
        var shells = new FakeTerminalHost();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(terminal: shells);
        (ControllerSession session, _) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        session.PeerInfo!.Granted.ShouldNotContain(Permission.PermTerminal);

        await session.OpenTerminalAsync(1, 80, 24);

        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        shells.Started.ShouldBeEmpty();
    }

    /// <summary>Stricter than file transfer on purpose: the permission alone is not enough, the connection has to be a terminal one.</summary>
    [Fact]
    public async Task A_remote_control_session_cannot_open_a_terminal_even_with_the_permission()
    {
        await using Testbed bed = await Testbed.StartAsync();
        var shells = new FakeTerminalHost();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(new HostPolicy { TerminalEnabled = true }, terminal: shells);
        (ControllerSession session, _) = await ConnectAsync(bed, host, passwords.TemporaryPassword, ConnType.ConnRemote);
        session.PeerInfo!.Granted.ShouldContain(Permission.PermTerminal);

        await session.OpenTerminalAsync(1, 80, 24);

        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        shells.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_terminal_response_from_the_viewer_is_a_violation()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(new HostPolicy { TerminalEnabled = true }, terminal: new FakeTerminalHost());
        (ControllerSession session, _) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await session.SendAsync(new Message { TerminalResponse = new TerminalResponse { Exit = new TerminalExit { Id = 1, Code = 0 } } });

        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>The connection manager's switch: flipping it off kills every shell, it does not merely stop forwarding output.</summary>
    [Fact]
    public async Task Withdrawing_the_permission_kills_every_shell()
    {
        await using Testbed bed = await Testbed.StartAsync();
        var shells = new FakeTerminalHost();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(new HostPolicy { TerminalEnabled = true }, terminal: shells);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await session.OpenTerminalAsync(1, 80, 24);
        await session.OpenTerminalAsync(2, 80, 24);
        await Testbed.WaitUntilAsync(() =>
        {
            lock (cb.Terminal)
            {
                return cb.Terminal.Count(r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened) == 2;
            }
        }, "two shells opened");

        host.Sessions.Single().Context.Permissions.SetOverride(Permission.PermTerminal, false);

        await Testbed.WaitUntilAsync(() => shells.Started.All(t => t.Disposed), "both shells killed");
        List<string> Exits()
        {
            lock (cb.Terminal)
            {
                return [.. cb.Terminal.Where(r => r.UnionCase == TerminalResponse.UnionOneofCase.Exit).Select(r => $"{r.Exit.Id}:{r.Exit.Reason}").Order(StringComparer.Ordinal)];
            }
        }

        try
        {
            await Testbed.WaitUntilAsync(() => Exits().Count >= 2, "both exits reported");
        }
        catch (TimeoutException)
        {
            // Say what did arrive; "timed out" alone does not tell a lost message from a wrong reason.
        }

        Exits().ShouldBe(["1:terminal permission was withdrawn", "2:terminal permission was withdrawn"]);

        // The session survives it: typing that was in flight is dropped, not treated as an attack, and a
        // new shell is refused in words rather than by hanging up.
        await session.SendTerminalInputAsync(1, "late keystroke\n"u8.ToArray());
        await session.OpenTerminalAsync(3, 80, 24);
        await WaitForAsync(cb, r => r.UnionCase == TerminalResponse.UnionOneofCase.Error && r.Error.Id == 3, "the refusal of a new shell");
        shells.Started.Count.ShouldBe(2);
        session.Completion.IsCompleted.ShouldBeFalse("withdrawing a permission is not hanging up");
        bed.Terminals!.GetSession(host.Sessions.Single().Context.ConnectionId)!.Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_dropped_session_takes_its_shells_with_it()
    {
        await using Testbed bed = await Testbed.StartAsync();
        var shells = new FakeTerminalHost();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(new HostPolicy { TerminalEnabled = true }, terminal: shells);
        (ControllerSession session, _) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await session.OpenTerminalAsync(1, 80, 24);
        await Testbed.WaitUntilAsync(() => shells.Started.Count == 1, "a shell");

        await session.CloseAsync("connection lost");

        await Testbed.WaitUntilAsync(() => shells.Started[0].Disposed, "the shell killed");
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 0, "the session gone");
    }

    /// <summary>A viewer's options can disable permissions, never ask for one; the terminal must not become the exception.</summary>
    [Fact]
    public void A_viewer_cannot_ask_for_the_terminal_permission_in_its_options()
    {
        SessionOptions.Descriptor.Fields.InDeclarationOrder().ShouldNotContain(f => f.Name.Contains("terminal", StringComparison.OrdinalIgnoreCase));
        LoginRequest.Descriptor.Fields.InDeclarationOrder().ShouldNotContain(f => f.Name.Contains("terminal", StringComparison.OrdinalIgnoreCase));
    }
}
