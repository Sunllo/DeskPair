namespace DeskPair.Platform.Abstractions.Terminal;

/// <summary>
/// Whose shell a viewer gets. The host's configuration asks for one of these; what it actually gets is
/// whatever the platform can give, which <see cref="ITerminal.Identity"/> says in words.
/// </summary>
public enum TerminalRunAs
{
    /// <summary>
    /// The most the host can offer: SYSTEM under the Windows service, root where the Linux daemon's gate
    /// allows it, the signed-in user on macOS. The default, by the owner's decision.
    /// </summary>
    Highest,

    /// <summary>The engine's own account, or the signed-in user's where the engine can take their token.</summary>
    User,
}

/// <summary>What a viewer may send a shell besides bytes. Windows has no signals; the implementation maps as best it can.</summary>
public enum TerminalSignalKind
{
    /// <summary>Ctrl+C's meaning: SIGINT, or a console control event.</summary>
    Interrupt,

    /// <summary>Ask the process to end: SIGTERM.</summary>
    Terminate,

    /// <summary>End it whether it likes it or not: SIGKILL, TerminateProcess.</summary>
    Kill,

    /// <summary>The terminal went away: SIGHUP.</summary>
    Hangup,
}

/// <summary>
/// The platform's way of starting a shell behind a pseudo-terminal.
///
/// The one contract for ConPTY, posix_openpt under the engine's own account, and the Linux daemon's root
/// path; the engine's <c>TerminalSession</c> drives all three the same way and never learns which it has.
/// Nothing here takes a program name, arguments, a directory or an environment: the request is "a shell
/// as this identity", the platform decides the rest, and on Linux that request crosses into a root process
/// where the difference between "run a shell" and "run this string" is the whole security story.
/// </summary>
public interface ITerminalHost
{
    /// <summary>False when this machine cannot give a shell at all; <see cref="UnavailableReason"/> says why in words the viewer sees.</summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>
    /// The account a shell would run as for <paramref name="runAs"/>, before one is started: "root",
    /// "NT AUTHORITY\SYSTEM", "alice". The approval dialog asks this, so a person is told "a root terminal"
    /// and not "a terminal".
    /// </summary>
    string DescribeIdentity(TerminalRunAs runAs);

    /// <summary>Starts a shell. Throws <see cref="TerminalStartException"/> with a reason fit for the viewer when it cannot.</summary>
    ValueTask<ITerminal> StartAsync(TerminalRunAs runAs, int columns, int rows, CancellationToken ct);
}

/// <summary>One running shell. Disposing it ends the process, always: a shell nobody is attached to is not left behind.</summary>
public interface ITerminal : IAsyncDisposable
{
    /// <summary>The account the shell actually runs as, in words.</summary>
    string Identity { get; }

    /// <summary>The program that was started, for the title bar.</summary>
    string Shell { get; }

    /// <summary>The pseudo-terminal's output; reads until the shell exits.</summary>
    Stream Output { get; }

    /// <summary>Bytes typed at the shell, exactly as the viewer sent them.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    void Resize(int columns, int rows);

    void Signal(TerminalSignalKind signal);

    /// <summary>Completes with the exit code once the shell has ended.</summary>
    Task<int> Exited { get; }
}

/// <summary>A shell could not be started; the message is meant for the viewer.</summary>
public sealed class TerminalStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A platform that has no terminal to offer, saying why.</summary>
public sealed class UnavailableTerminalHost(string reason) : ITerminalHost
{
    public bool IsAvailable => false;

    public string? UnavailableReason => reason;

    public string DescribeIdentity(TerminalRunAs runAs) => string.Empty;

    public ValueTask<ITerminal> StartAsync(TerminalRunAs runAs, int columns, int rows, CancellationToken ct) =>
        ValueTask.FromException<ITerminal>(new TerminalStartException(reason));
}
