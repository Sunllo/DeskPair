using DeskPair.Core.Ipc;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Core.Tests;

/// <summary>
/// The IPC surface is the engine's control panel, and with the Windows host service the engine is
/// LocalSystem. A command that arrives unclassified is a stranger's command carried out as SYSTEM, so the
/// interesting property is not that today's table is right -- it is that tomorrow's cannot be incomplete.
/// </summary>
public class IpcAuthorityTests
{
    private static IEnumerable<IpcMessage.UnionOneofCase> EveryCase() =>
        Enum.GetValues<IpcMessage.UnionOneofCase>();

    /// <summary>
    /// Adding a message to ipc.proto and forgetting this table is the failure this exists to catch. It is
    /// a quiet failure otherwise: the handler gets a case it does not know, logs "unhandled" at debug, and
    /// whoever added it sees their feature working because they wired the handler at the same time.
    /// </summary>
    [Fact]
    public void Every_ipc_command_is_classified()
    {
        IpcMessage.UnionOneofCase[] unclassified = EveryCase()
            .Where(c => !IpcAuthorities.IsClassified(c))
            .ToArray();

        unclassified.ShouldBeEmpty(
            "every IPC message must be listed in IpcAuthorities, including the ones the engine only "
            + "sends: " + string.Join(", ", unclassified));
    }

    /// <summary>
    /// An unlisted command must not be treated as harmless. The completeness test above should make this
    /// unreachable; it is here because "should" is doing a lot of work in that sentence, and the cost of
    /// being wrong is a SYSTEM engine taking orders from anybody.
    /// </summary>
    [Fact]
    public void An_unlisted_command_needs_the_owner()
    {
        IpcAuthorities.For((IpcMessage.UnionOneofCase)9999).ShouldBe(IpcAuthority.Owner);
    }

    /// <summary>
    /// Reading a password is granting access with it. Classifying the getters as harmless because they
    /// only read would hand this machine to anyone who can open the pipe.
    /// </summary>
    [Theory]
    [InlineData(IpcMessage.UnionOneofCase.GetTempPassword)]
    [InlineData(IpcMessage.UnionOneofCase.GetPasswordState)]
    [InlineData(IpcMessage.UnionOneofCase.SetPermanentPassword)]
    [InlineData(IpcMessage.UnionOneofCase.SetTemporaryPassword)]
    [InlineData(IpcMessage.UnionOneofCase.RotateTemporaryPassword)]
    [InlineData(IpcMessage.UnionOneofCase.SetConfig)]
    [InlineData(IpcMessage.UnionOneofCase.GetConfig)]
    [InlineData(IpcMessage.UnionOneofCase.ApprovalDecision)]
    public void The_commands_that_decide_who_may_connect_need_the_owner(IpcMessage.UnionOneofCase request)
    {
        IpcAuthorities.For(request).ShouldBe(IpcAuthority.Owner);
    }
}
