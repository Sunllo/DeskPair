using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Unix.Terminal;

namespace DeskPair.Platform.Linux.Terminal;

/// <summary>What the daemon said to a request for a shell.</summary>
public sealed record DaemonTerminalReply(bool IsGranted, bool IsRefused, int Handle, string Identity, string Shell, int Master, string Message)
{
    public static DaemonTerminalReply Granted(int handle, string identity, string shell, int master) => new(true, false, handle, identity, shell, master, string.Empty);

    /// <summary>The daemon's own configuration says no; not a failure.</summary>
    public static DaemonTerminalReply Refused(string message) => new(false, true, 0, string.Empty, string.Empty, -1, message);

    public static DaemonTerminalReply Failed(string message) => new(false, false, 0, string.Empty, string.Empty, -1, message);
}

/// <summary>
/// The engine's terminal under the Linux daemon. The engine runs as <c>deskpair</c> and has given root up
/// for good, so a root shell -- or one as the person signed in -- can only come from the daemon, which
/// starts it and hands back the pty. Where the daemon's gate says no, the shell is the engine's own
/// account, and the identity says so: an honest <c>deskpair</c> rather than a failure.
/// </summary>
public sealed class DaemonTerminalHost(DrmCaptureChannel channel, ILogger log) : ITerminalHost
{
    private readonly ITerminalHost _own = LinuxTerminal.Create(log);

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public string DescribeIdentity(TerminalRunAs runAs)
    {
        DaemonTerminalReply reply = channel.OpenTerminal(Wire(runAs), 80, 24, describeOnly: true);
        return reply.IsGranted ? reply.Identity : reply.IsRefused ? _own.DescribeIdentity(runAs) : string.Empty;
    }

    public ValueTask<ITerminal> StartAsync(TerminalRunAs runAs, int columns, int rows, CancellationToken ct)
    {
        DaemonTerminalReply reply = channel.OpenTerminal(Wire(runAs), columns, rows, describeOnly: false);
        if (reply.IsGranted)
        {
            log.LogInformation("The daemon started a shell as {Identity} ({Shell})", reply.Identity, reply.Shell);
            return ValueTask.FromResult<ITerminal>(
                new UnixPtyTerminal(new LinuxPtySpawner(), reply.Master, new DaemonPtyProcess(channel, reply.Handle), reply.Identity, reply.Shell, log));
        }

        if (reply.IsRefused)
        {
            log.LogInformation("The daemon does not give shells here ({Why}); starting one as this engine's own account", reply.Message);
            return _own.StartAsync(runAs, columns, rows, ct);
        }

        throw new TerminalStartException(reply.Message);
    }

    private static byte Wire(TerminalRunAs runAs) => runAs == TerminalRunAs.User ? (byte)1 : (byte)0;
}

/// <summary>A shell the daemon started: this engine may not signal it or wait for it, so it asks.</summary>
internal sealed class DaemonPtyProcess(DrmCaptureChannel channel, int handle) : IPtyProcess
{
    /// <summary>How often to ask whether it has ended. Each question holds the capture channel for a systemctl call.</summary>
    private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(1);

    public string Description => $"daemon terminal {handle}";

    public void Signal(int signal) => _ = channel.TerminalSignal(handle, signal);

    public int WaitForExit(Func<bool> stop)
    {
        while (true)
        {
            if (channel.TerminalSignal(handle, 0) is not { } state)
            {
                return -1; // the channel is gone, and with it the daemon's answer
            }

            if (state.Exited)
            {
                return state.Code;
            }

            for (int waited = 0; waited < AskEvery.TotalMilliseconds; waited += 100)
            {
                if (stop())
                {
                    return -1;
                }

                Thread.Sleep(100);
            }
        }
    }

    public int KillEverything()
    {
        _ = channel.TerminalSignal(handle, 9);
        return -1;
    }
}
