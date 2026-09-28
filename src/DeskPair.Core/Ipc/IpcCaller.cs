using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DeskPair.Core.Ipc;

/// <summary>
/// Who is on the other end of an IPC connection, according to the operating system.
///
/// Everything else the engine knows about a client is something the client said about itself: the role in
/// its hello, and a token read from a file. Both were adequate while the engine ran as the user who
/// started it, because the worst a caller could do was what that user could already do. They stopped being
/// adequate the moment the engine could run as LocalSystem: the token lives in ProgramData, where the
/// Users group has read access by default, and the pipe admits Interactive -- so on a machine with the
/// host service installed, any signed-in user could read a password, write the configuration, or answer an
/// approval prompt, against an engine that sees the lock screen.
///
/// This is the one fact about a client that the client cannot choose.
/// </summary>
public sealed record IpcCaller
{
    /// <summary>The caller could not be identified. Treated as not being the owner.</summary>
    public static readonly IpcCaller Unknown = new() { Known = false };

    /// <summary>
    /// Used where there is nothing to ask: the in-process tests, which built the server themselves.
    ///
    /// This used to be the answer for every Unix connection as well, on the grounds that a socket in the
    /// user's own runtime directory is a single-user situation by construction. That was true, and it
    /// stops being true the moment root owns the engine: the machine-wide socket has to be reachable by
    /// whoever is at the screen, so the file mode no longer answers the question and the kernel has to.
    /// </summary>
    public static readonly IpcCaller Local = new() { Known = true, IsOwner = true, Name = "local" };

    /// <summary>
    /// Who is sitting at the screen, when something knows; null when nothing does.
    ///
    /// Windows asks the session manager directly, which is a call any process can make. Unix has no such
    /// call in the base system: on Linux the answer comes from logind, and on macOS from the console
    /// user, and neither of those may be reached from here -- this assembly sees the platform contracts
    /// and nothing that implements them. So the composition root fills this in, and until it does the
    /// question simply has no answer, which costs nothing while the engine runs as the user themselves.
    ///
    /// Not cached, for the reason the Windows one is not: a user switch changes it, and a check that
    /// answers last week's question goes on trusting a screen that now belongs to somebody else.
    /// </summary>
    public static Func<uint?> ConsoleUser { get; set; } = static () => null;

    private IpcCaller()
    {
    }

    /// <summary>Whether the operating system gave an answer at all.</summary>
    public bool Known { get; private init; }

    /// <summary>Whether this caller may give the commands classified <see cref="IpcAuthority.Owner"/>.</summary>
    public bool IsOwner { get; private init; }

    /// <summary>The account name, for the log. Never used to decide anything.</summary>
    public string Name { get; private init; } = string.Empty;

    /// <summary>
    /// The calling process's uid, where the kernel said (a Unix socket); null elsewhere. Used for what belongs to
    /// the caller's own account -- whose desktop a permission is for -- never to decide who is the owner.
    /// </summary>
    public uint? Uid { get; private init; }

    /// <summary>Why it was or was not the owner, for the log.</summary>
    public string Because { get; private init; } = string.Empty;

    /// <summary>
    /// Asks Windows who opened this pipe, by reading the connecting process's own token.
    ///
    /// The obvious way is <c>RunAsClient</c>, and it does not work here: impersonation is something the
    /// *client* grants, and a client that opens a pipe without asking for it -- which is the default --
    /// makes that call fail. Every caller would then be unidentified, and every command that needs the
    /// owner refused, which is the app locking itself out of its own engine.
    ///
    /// Going to the process instead asks nothing of the client, so a legitimate one cannot fail the check
    /// by accident and a hostile one cannot dodge it by declining to cooperate.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IpcCaller Of(NamedPipeServerStream pipe)
    {
        return GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint processId)
            ? OfProcess(processId, EngineAccount())
            : Unknown;
    }

    /// <summary>The account running <paramref name="processId"/>, judged against an engine running as <paramref name="engine"/>.</summary>
    [SupportedOSPlatform("windows")]
    internal static IpcCaller OfProcess(uint processId, SecurityIdentifier? engine)
    {
        uint? session = ProcessIdToSessionId(processId, out uint id) ? id : null;
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return Unknown;
        }

        try
        {
            // Duplicate as well as query: asking whether the account is an administrator makes an impersonation
            // copy of the token, which a handle opened for query alone may not. An engine running as the user
            // never gets that far -- the account is its own -- so without this every caller of the service's
            // engine was unidentified, and the app refused its own settings.
            if (!OpenProcessToken(process, TokenQuery | TokenDuplicate, out nint token))
            {
                return Unknown;
            }

            try
            {
                using var client = new WindowsIdentity(token);
                return Describe(client, session, engine);
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception)
        {
            // An identity that cannot be read is not an identity to trust, and refusing is the safe
            // direction. A client that has already exited between connecting and being asked about lands
            // here too, and refusing a caller that is gone costs nothing.
            return Unknown;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// Asks the kernel who opened this socket.
    ///
    /// Three ways to be the owner here, against Windows's five, and the missing ones are deliberate. There
    /// is no "administrator" on Unix that is not either root or somebody who can become root at will, and
    /// a group membership check would be a guess about a policy this program does not own -- sudo, wheel,
    /// admin and polkit all disagree. Somebody who can become root is already covered by being able to
    /// become root.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static IpcCaller Of(System.Net.Sockets.Socket socket)
    {
        if (UnixPeer.Of(socket) is not { } peer)
        {
            return Unknown;
        }

        string name = UnixPeer.NameOf(peer.Uid);

        // The account the engine itself runs as, first, for the reason it is first on Windows: without the
        // daemon that is the person who opened the app, and the client asking is their own window.
        if (peer.Uid == UnixPeer.Self)
        {
            return Owned(name, "the account this engine runs as") with { Uid = peer.Uid };
        }

        if (peer.Uid == 0)
        {
            return Owned(name, "root") with { Uid = peer.Uid };
        }

        // The person at the screen this engine is capturing. There is nothing to keep from somebody who
        // can already see it and type on it.
        if (ConsoleUser() is { } console && peer.Uid == console)
        {
            return Owned(name, "signed in at the screen") with { Uid = peer.Uid };
        }

        return new IpcCaller
        {
            Known = true,
            IsOwner = false,
            Name = name,
            Because = $"uid {peer.Uid} is neither root nor the user at the screen",
            Uid = peer.Uid,
        };
    }

    private static IpcCaller Owned(string name, string because) => new()
    {
        Known = true,
        IsOwner = true,
        Name = name,
        Because = because,
    };

    [SupportedOSPlatform("windows")]
    private static IpcCaller Describe(WindowsIdentity client, uint? session, SecurityIdentifier? engine)
    {
        var principal = new WindowsPrincipal(client);

        // Five ways to be the owner of this machine, and the same reasoning for each: they can already do
        // this without asking the engine.
        //
        // The first is the one that keeps the ordinary case working, and it is easy to leave out: the
        // account the engine itself runs as. Without the service that is the person who opened the app,
        // and the client asking is their own window. Leaving it to the console check would have locked
        // somebody out of their own settings for the crime of working over remote desktop -- their
        // session is not the console one, and their own app would have been refused its own passwords.
        //
        // Then: LocalSystem, which is the service's arrangements talking to themselves. An administrator,
        // who can install a service and read any file on the disk, so withholding a password from one is
        // theatre -- elevated or not, since the difference is one click on a consent prompt. And the user in
        // the console session, who is sitting at the screen this engine is capturing -- there is nothing to
        // keep from somebody who can already see it and type on it.
        //
        // The session comparison is what the obvious check gets wrong: the INTERACTIVE group is in the
        // token of a remote desktop logon exactly as it is in a console one, so trusting it would have
        // left the case this was written for wide open -- a second signed-in account reading the
        // passwords of a desk it cannot see.
        if (engine is not null && client.User is { } who && who == engine)
        {
            return Owned(client, "the account this engine runs as");
        }

        if (client.IsSystem)
        {
            return Owned(client, "LocalSystem");
        }

        if (principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            return Owned(client, "an administrator");
        }

        // An administrator whose window is not elevated. Under UAC the Administrators group is in such a token for
        // deny only, so the check above says no -- but the person behind it is the one who can install a service and
        // read any file on the disk with one click on a consent prompt, which is why administrators are owners at
        // all. Left out, the owner of the machine working over remote desktop was refused their own passwords and
        // settings by the service's engine, whose console session is not theirs.
        if (IsFilteredAdministrator(client))
        {
            return Owned(client, "an administrator (not elevated)");
        }

        if (session is { } id && id == ConsoleSession())
        {
            return Owned(client, "signed in at the console");
        }

        return new IpcCaller
        {
            Known = true,
            IsOwner = false,
            Name = client.Name,
            Because = session is { } other
                ? $"signed in to session {other}, which is not the console"
                : "in an unknown session",
        };
    }

    [SupportedOSPlatform("windows")]
    private static IpcCaller Owned(WindowsIdentity client, string because) => Owned(client.Name, because);

    /// <summary>
    /// Whether this is the filtered half of an administrator's split token: what UAC gives an administrator's
    /// programs until one is elevated. Only a member of Administrators ever has one.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsFilteredAdministrator(WindowsIdentity client) =>
        GetTokenInformation(client.Token, TokenElevationType, out int type, sizeof(int), out _) && type == TokenElevationTypeLimited;

    /// <summary>
    /// The account this engine is running as. Only ever called from outside an impersonation block.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? EngineAccount()
    {
        try
        {
            using WindowsIdentity me = WindowsIdentity.GetCurrent();
            return me.User;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The session on the physical screen, or null when there is none.
    ///
    /// Not cached. A user switch changes it, and a check that answers last week's question is worse than
    /// no check: it would go on trusting a session that has since been handed to somebody else.
    /// </summary>
    private static uint? ConsoleSession()
    {
        uint session = WTSGetActiveConsoleSessionId();
        return session == 0xFFFFFFFF ? null : session;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    private const int TokenElevationType = 18;

    private const int TokenElevationTypeLimited = 3;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int informationClass, out int information, int length, out int returnLength);

    /// <summary>Enough to be allowed to open the token, and no more than that.</summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private const uint TokenQuery = 0x0008;

    private const uint TokenDuplicate = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
