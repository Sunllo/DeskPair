using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;

namespace DeskPair.Integration.Tests;

/// <summary>
/// The journal over a real session. The unit tests say what a row looks like; this says that the row is
/// actually produced by connecting, with the facts the session really had rather than the ones a test made up.
/// </summary>
public sealed class ConnectionJournalSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sunllo-journal-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private ConnectionJournal Journal() =>
        new(Path.Combine(_dir, "connections.jsonl"), TimeProvider.System, NullLogger.Instance);

    [Fact]
    public async Task A_real_connection_leaves_a_row_with_what_the_session_actually_had()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, HostPasswords passwords, _) = await bed.StartHostAsync(directPort: -1, settings: Testbed.NoServerSettings);
        ConnectionJournal journal = Journal();
        journal.Attach(host);

        (ControllerSession session, TestCallbacks _, DeskPair.Core.Transport.PeerConnector connector) = bed.CreateController(myId: "123456789", settings: Testbed.NoServerSettings);
        await session.ConnectAsync(connector, $"127.0.0.1:{host.BoundDirectAccessPort}", CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        // Written the moment it is authorised, not when it ends. The host raises that event just after the
        // login response goes out, so the controller can be back before the line is on disk.
        await Testbed.WaitUntilAsync(() => journal.Read().Count == 1, "the opening line");
        ConnectionRecord open = journal.Read().ShouldHaveSingleItem();
        open.PeerId.ShouldBe("123456789");
        open.Authenticated.ShouldBe("temporary");
        open.Transport.ShouldBe("DirectTcp");
        open.Kind.ShouldBe("remote");
        open.Address.ShouldNotBeNullOrEmpty();
        open.Granted.ShouldContain("PermKeyboard");
        open.Unfinished.ShouldBeTrue();

        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => journal.Read().Count == 1 && !journal.Read()[0].Unfinished, "the closing line");

        ConnectionRecord closed = journal.Read().ShouldHaveSingleItem();
        closed.Id.ShouldBe(open.Id, "the two lines fold back into one row");
        closed.Reason.ShouldNotBeNullOrEmpty();
        closed.Duration.ShouldNotBeNull();
    }

    /// <summary>
    /// The journal is about who got in. A refused login already has its own log line, and recording every
    /// wrong password here would turn a security record into a scanner's guestbook.
    /// </summary>
    [Fact]
    public async Task A_connection_that_was_refused_is_not_in_the_journal()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, _, _) = await bed.StartHostAsync(directPort: -1, settings: Testbed.NoServerSettings);
        ConnectionJournal journal = Journal();
        journal.Attach(host);

        (ControllerSession session, TestCallbacks _, DeskPair.Core.Transport.PeerConnector connector) = bed.CreateController(settings: Testbed.NoServerSettings);
        await session.ConnectAsync(connector, $"127.0.0.1:{host.BoundDirectAccessPort}", CancellationToken.None);
        (await session.LoginAsync("not-the-password", CancellationToken.None)).Success.ShouldBeFalse();
        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 0, "the session gone");

        journal.Read().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_transfer_connection_is_recorded_as_one()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, HostPasswords passwords, _) = await bed.StartHostAsync(directPort: -1, settings: Testbed.NoServerSettings);
        ConnectionJournal journal = Journal();
        journal.Attach(host);

        (ControllerSession session, TestCallbacks _, DeskPair.Core.Transport.PeerConnector connector) =
            bed.CreateController(connType: DeskPair.Protocol.Rendezvous.ConnType.ConnFileTransfer, settings: Testbed.NoServerSettings);
        await session.ConnectAsync(connector, $"127.0.0.1:{host.BoundDirectAccessPort}", CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        await Testbed.WaitUntilAsync(() => journal.Read().Count == 1, "the opening line");
        journal.Read().ShouldHaveSingleItem().Kind.ShouldBe("file-transfer");
    }
}
