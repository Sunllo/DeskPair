using DeskPair.Protocol.Ipc;

namespace DeskPair.Core.Ipc;

/// <summary>Who has to be asking for a command to be carried out.</summary>
public enum IpcAuthority
{
    /// <summary>
    /// Anything that got through the pipe. Reading the host's own id, a ping, chat into a session the
    /// caller can already see -- nothing here grants access to this machine or changes who can reach it.
    /// </summary>
    Any,

    /// <summary>
    /// Only the person at this computer, or an administrator of it.
    ///
    /// Everything that decides who may connect, or on what terms: the passwords, the configuration, and
    /// the answer to an approval prompt. Reading a password is on this list beside setting one, because a
    /// temporary password read is a connection granted.
    ///
    /// This mattered much less when the engine ran as the user who started it -- the worst a caller could
    /// do was what that user could already do. With the host service the engine is LocalSystem and sees
    /// the lock screen, so the same commands hand a caller remote control of a desk they may not be
    /// entitled to sit at.
    /// </summary>
    Owner,
}

/// <summary>
/// What each IPC command means, in terms of who may ask for it.
///
/// It classifies both directions, and that is not tidiness. The first version classified only requests,
/// and left a hole big enough to walk through: a client that was refused <c>GetTempPassword</c> could
/// simply connect and wait, because the engine pushes the password to every client whenever it changes.
/// A push carrying something is the same disclosure as an answer carrying it.
///
/// This exists as a table rather than as checks scattered through the handler for one reason: a table can
/// be tested for completeness. <c>IpcAuthorityTests</c> walks every case of the protocol's oneof and fails
/// if one of them is missing here, so a command added next year cannot quietly arrive unclassified -- and
/// unclassified, on a LocalSystem engine, means a stranger's command carried out as SYSTEM.
///
/// The classification is not the enforcement. It is the statement of intent the enforcement is checked
/// against, and the thing a reviewer reads instead of reconstructing the handler in their head.
/// </summary>
public static class IpcAuthorities
{
    private static readonly Dictionary<IpcMessage.UnionOneofCase, IpcAuthority> Table = new()
    {
        // ---- the handshake and the plumbing ----
        [IpcMessage.UnionOneofCase.None] = IpcAuthority.Any,
        [IpcMessage.UnionOneofCase.Hello] = IpcAuthority.Any,
        [IpcMessage.UnionOneofCase.HelloAck] = IpcAuthority.Any,
        [IpcMessage.UnionOneofCase.Ping] = IpcAuthority.Any,
        [IpcMessage.UnionOneofCase.Pong] = IpcAuthority.Any,

        // The id is printed on the host's own screen and handed out to anyone being invited to connect.
        [IpcMessage.UnionOneofCase.GetId] = IpcAuthority.Any,
        [IpcMessage.UnionOneofCase.IdChanged] = IpcAuthority.Any,

        // Whether the server is answering is as public as the id it goes with.
        [IpcMessage.UnionOneofCase.GetServerState] = IpcAuthority.Any,
        [IpcMessage.UnionOneofCase.ServerState] = IpcAuthority.Any,

        // The answer to somebody who may not ask: it carries nothing but the fact of the refusal, and it is the one
        // reply a caller who is not the owner has to be able to receive.
        [IpcMessage.UnionOneofCase.Refused] = IpcAuthority.Any,

        // ---- pushes that carry what the requests above are protecting ----
        // Refusing to answer GetTempPassword while broadcasting the password to every attached client
        // would have been a lock on a door with no wall beside it.
        [IpcMessage.UnionOneofCase.TempPassword] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.PasswordState] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.PasswordAck] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.ConfigSnapshot] = IpcAuthority.Owner,

        // Who is connecting to this computer, from where, and what they are moving. Not a way in, but a
        // feed of it is not something a bystander on another session is owed.
        [IpcMessage.UnionOneofCase.ConnectionOpened] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.ApprovalRequest] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.ConnectionClosed] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.FileJobProgress] = IpcAuthority.Owner,

        // ---- who may reach this computer, and on what terms ----
        [IpcMessage.UnionOneofCase.GetTempPassword] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.GetPasswordState] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.SetPermanentPassword] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.SetTemporaryPassword] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.RotateTemporaryPassword] = IpcAuthority.Owner,

        // The configuration decides whether a connection needs approving at all, and which directories a
        // peer may read. It is the single most valuable thing on this list to be able to write.
        [IpcMessage.UnionOneofCase.GetConfig] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.SetConfig] = IpcAuthority.Owner,

        // Answering the prompt is the decision the prompt exists for. The handler also requires the
        // connection-manager role, but a role is a word the client says about itself, so it keeps the
        // decision in one window rather than keeping anybody out.
        [IpcMessage.UnionOneofCase.ApprovalDecision] = IpcAuthority.Owner,

        // ---- a session that is already open ----
        // Not about who may connect, but about what a connection that exists may do, and ending it. The
        // person at the machine owns those; a bystander widening a stranger's permissions is the same
        // problem in a smaller frame.
        [IpcMessage.UnionOneofCase.PermissionChange] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.CloseConnection] = IpcAuthority.Owner,

        // Chat goes both ways: out to the peer, and in to be shown. Nothing is granted by it, but words
        // sent this way arrive at the far end as the words of the person at this machine, and putting
        // them in somebody else's mouth is its own kind of access.
        [IpcMessage.UnionOneofCase.Chat] = IpcAuthority.Owner,

        // Ctrl+Alt+Del on behalf of a viewer. Declared in the protocol and implemented nowhere: the engine
        // has no handler for it and falls through to "unhandled". Classified as Owner so that whoever
        // writes that handler starts from the right side -- sending a secure-attention sequence is exactly
        // the kind of thing the lock screen exists to protect.
        [IpcMessage.UnionOneofCase.SendSas] = IpcAuthority.Owner,

        // Who has connected to this machine, and forgetting it. Reading the record names every peer that
        // has ever been let in and where from; clearing it is how somebody would cover their tracks. Both
        // belong to the person at the machine.
        [IpcMessage.UnionOneofCase.ConnectionHistoryRequest] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.ConnectionHistory] = IpcAuthority.Owner,

        // Letting the engine share a desktop without asking decides what a viewer sees; it is for the person at the
        // screen, and it is asked of them on it. The answer is for the caller's own account only.
        [IpcMessage.UnionOneofCase.DesktopSharingRequest] = IpcAuthority.Owner,
        [IpcMessage.UnionOneofCase.DesktopSharingState] = IpcAuthority.Owner,
    };

    /// <summary>Who must be asking. Anything unlisted is treated as <see cref="IpcAuthority.Owner"/>.</summary>
    public static IpcAuthority For(IpcMessage.UnionOneofCase request) =>
        Table.TryGetValue(request, out IpcAuthority authority) ? authority : IpcAuthority.Owner;

    /// <summary>Whether this case is classified at all. For the completeness test, not for the engine.</summary>
    public static bool IsClassified(IpcMessage.UnionOneofCase request) => Table.ContainsKey(request);
}
