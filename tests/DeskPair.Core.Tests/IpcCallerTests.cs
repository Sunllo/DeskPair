using System.IO.Pipes;
using DeskPair.Core.Ipc;

namespace DeskPair.Core.Tests;

/// <summary>
/// Asking Windows who opened a pipe, over a real pipe.
///
/// The classification table can be tested with no operating system in sight. This cannot: the whole value
/// of <see cref="IpcCaller"/> is that it reads something only the kernel knows, so a test that stubbed
/// that out would be testing nothing.
/// </summary>
public class IpcCallerTests
{
    /// <summary>
    /// The case that must never break, and the one a security check is most likely to break: an engine
    /// being asked something by its own user.
    ///
    /// Getting this wrong locks somebody out of their own settings, and the obvious first implementation
    /// did exactly that -- it trusted the console session, so anybody working over remote desktop was
    /// refused their own passwords by their own app.
    /// </summary>
    [Fact]
    public async Task The_engine_owns_a_connection_from_its_own_account()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // pipe identities are a Windows arrangement
        }

        string name = "deskpair-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        Task accepting = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accepting;

        IpcCaller caller = IpcCaller.Of(server);

        caller.Known.ShouldBeTrue();
        caller.IsOwner.ShouldBeTrue(caller.Because);
        caller.Name.ShouldNotBeEmpty();
    }

    /// <summary>
    /// The service's case: the engine runs as LocalSystem and the caller is somebody else, so the answer comes
    /// from further down -- an administrator, or the person at the console. Asking whether the account is an
    /// administrator needs more of the token than asking who it is, and with less than that every caller of
    /// the service's engine came out unidentified: on a real machine, the app refused its own settings and
    /// crashed on the first one it tried. Judged here against an engine account that is not this one.
    /// </summary>
    [Fact]
    public void A_caller_on_another_account_is_still_identified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // process tokens are a Windows arrangement
        }

        var service = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalServiceSid, null);

        IpcCaller caller = IpcCaller.OfProcess((uint)Environment.ProcessId, service);

        caller.Known.ShouldBeTrue("the caller's token was readable; only the question asked of it failed");
        caller.Because.ShouldNotBe("the account this engine runs as");
    }

    /// <summary>
    /// An administrator whose programs are not elevated -- which is how an administrator's programs run under UAC --
    /// owns this machine as much as an elevated one: a consent prompt is all that stands between the two. Refused, the
    /// owner of a machine working over remote desktop could not see their own password in the service's engine, whose
    /// console session is not theirs. Asked of this test's own token, so it says something only when that token is
    /// such an administrator's, as a developer's own window usually is.
    /// </summary>
    [Fact]
    public void An_administrator_who_is_not_elevated_owns_the_machine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // process tokens are a Windows arrangement
        }

        using (var me = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (!IpcCaller.IsFilteredAdministrator(me))
            {
                return; // elevated, a standard user, or UAC off: no filtered token here to ask about
            }
        }

        var service = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalServiceSid, null);

        IpcCaller caller = IpcCaller.OfProcess((uint)Environment.ProcessId, service);

        caller.IsOwner.ShouldBeTrue(caller.Because);
        caller.Because.ShouldBe("an administrator (not elevated)");
    }

    /// <summary>
    /// Reading the engine's own account has to happen before impersonating the client, and nothing in the
    /// type's shape enforces that. The first version read it lazily from inside the impersonation block,
    /// where WindowsIdentity.GetCurrent() answers with the *caller's* account -- which would have made
    /// the first client to connect the account the engine runs as, and every later check against it say
    /// yes to everybody.
    ///
    /// Two connections in a row from this same account cannot catch that on their own, so this asserts
    /// the reason as well as the answer: the second one must still be recognised as the engine's own
    /// account, not merely allowed for some other reason.
    /// </summary>
    [Fact]
    public async Task A_second_connection_is_judged_the_same_way_as_the_first()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // pipe identities are a Windows arrangement
        }

        string first = await BecauseOfOneConnection();
        string second = await BecauseOfOneConnection();

        first.ShouldBe("the account this engine runs as");
        second.ShouldBe(first);
    }

    private static async Task<string> BecauseOfOneConnection()
    {
        string name = "deskpair-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        Task accepting = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accepting;

        return IpcCaller.Of(server).Because;
    }
}
