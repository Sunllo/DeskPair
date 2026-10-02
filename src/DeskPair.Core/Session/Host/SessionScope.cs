using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Session.Host;

/// <summary>Data-driven allow-list of which messages a session may send in its current state and connection type.</summary>
public sealed class SessionScope
{
    private static readonly HashSet<Message.UnionOneofCase> AlwaysAllowed =
    [
        Message.UnionOneofCase.TestDelay,
    ];

    private static readonly HashSet<Misc.UnionOneofCase> AlwaysAllowedMisc =
    [
        Misc.UnionOneofCase.CloseReason,
        Misc.UnionOneofCase.Chat,
        Misc.UnionOneofCase.Options,
    ];

    private static readonly HashSet<Message.UnionOneofCase> RemoteOnly =
    [
        Message.UnionOneofCase.MouseEvent,
        Message.UnionOneofCase.KeyEvent,
        Message.UnionOneofCase.Clipboard,
    ];

    private static readonly HashSet<Misc.UnionOneofCase> RemoteOnlyMisc =
    [
        Misc.UnionOneofCase.SwitchDisplay,
        Misc.UnionOneofCase.DisplaySubscription,
        Misc.UnionOneofCase.VirtualDisplayRequest,
        Misc.UnionOneofCase.DisplayResolution,
        Misc.UnionOneofCase.RefreshVideo,
        Misc.UnionOneofCase.VideoAck,
        Misc.UnionOneofCase.RestartRemoteDevice,
        Misc.UnionOneofCase.SupportedEncoding,
        Misc.UnionOneofCase.MediaAnswer,
        Misc.UnionOneofCase.MediaReady,
        Misc.UnionOneofCase.MediaClose,
        Misc.UnionOneofCase.ElevationRequest,
    ];

    public bool IsAllowed(HostSessionContext ctx, Message message)
    {
        if (ctx.State != HostSessionState.Authorized)
        {
            return false;
        }

        if (message.UnionCase == Message.UnionOneofCase.Misc)
        {
            Misc.UnionOneofCase mc = message.Misc.UnionCase;
            if (AlwaysAllowedMisc.Contains(mc))
            {
                return true;
            }

            return RemoteOnlyMisc.Contains(mc) && ctx.ConnType == ConnType.ConnRemote;
        }

        if (AlwaysAllowed.Contains(message.UnionCase))
        {
            return true;
        }

        if (message.UnionCase is Message.UnionOneofCase.FileAction or Message.UnionOneofCase.FileResponse)
        {
            return ctx.ConnType == ConnType.ConnFileTransfer || ctx.Permissions.Has(Permission.PermFile);
        }

        // Deliberately stricter than file transfer, where the connection type implies the permission: a
        // shell needs both the terminal connection type and the permission the owner switched on. And a
        // TerminalResponse is what the host says; from a viewer it is never anything but a violation.
        if (message.UnionCase == Message.UnionOneofCase.TerminalAction)
        {
            // After a withdrawal the module drops what is still in flight; see TerminalWasGranted.
            return ctx.ConnType == ConnType.ConnTerminal && (ctx.Permissions.Has(Permission.PermTerminal) || ctx.TerminalWasGranted);
        }

        if (message.UnionCase == Message.UnionOneofCase.TerminalResponse)
        {
            return false;
        }

        return RemoteOnly.Contains(message.UnionCase) && ctx.ConnType == ConnType.ConnRemote;
    }
}
