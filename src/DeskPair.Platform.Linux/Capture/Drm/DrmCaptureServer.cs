using DeskPair.Platform.Linux.Hosting;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Capture.Drm;

/// <summary>
/// The daemon's end of the socketpair: answers each poll with what the screen is, and the descriptor
/// for it the first time that framebuffer is seen.
///
/// Deliberately a loop a reader can hold in their head. The root process's job is to be small; every
/// wait, every retry and every pixel is on the other side, where a fault costs one user's engine and not
/// the machine's login screen.
/// </summary>
public static class DrmCaptureServer
{
    /// <summary>
    /// Serves one engine until it closes its end or the token is cancelled. With no <paramref name="reader"/>
    /// -- a machine with no display hardware -- every poll is answered "no display, and none coming", and
    /// the channel still carries the lock request; a server with no screen is still a machine to reach.
    /// </summary>
    public static void Run(int socket, DrmScanoutReader? reader, ILogger log, CancellationToken ct) =>
        Run(socket, reader, log, ct, terminals: null);

    /// <summary>
    /// As above, also answering the engine's terminal requests through <paramref name="terminals"/>; when the
    /// engine closes the channel -- it exited, it crashed -- every shell it had is stopped. With
    /// <paramref name="agents"/>, a session agent's socket goes to the engine that asks for it.
    /// </summary>
    public static void Run(int socket, DrmScanoutReader? reader, ILogger log, CancellationToken ct, Terminal.DaemonTerminals? terminals, Wayland.Agent.AgentHandoff? agents = null)
    {
        try
        {
            Serve(socket, reader, log, ct, terminals, agents);
        }
        finally
        {
            terminals?.StopAll();
        }
    }

    private static void Serve(int socket, DrmScanoutReader? reader, ILogger log, CancellationToken ct, Terminal.DaemonTerminals? terminals, Wayland.Agent.AgentHandoff? agents)
    {
        Span<byte> request = stackalloc byte[64];

        // Room for a frame record and for the sentences: an error, or a terminal's account and shell.
        Span<byte> reply = stackalloc byte[512];
        Span<int> incoming = stackalloc int[UnixSocketMsg.MaxFds];
        Span<uint> known = stackalloc uint[DrmWire.KnownFbSlots];

        while (!ct.IsCancellationRequested)
        {
            int length;
            try
            {
                length = UnixSocketMsg.Receive(socket, request, incoming, out int fdCount);
                for (int i = 0; i < fdCount; i++)
                {
                    _ = UnixSocketMsg.close(incoming[i]); // the engine sends none; anything here is a mistake, not a gift
                }
            }
            catch (IOException e)
            {
                log.LogInformation("The engine's capture channel closed: {Reason}", e.Message);
                return;
            }

            if (length == 0)
            {
                log.LogInformation("The engine closed its capture channel");
                return;
            }

            if (DrmWire.TryReadLock(request[..length]))
            {
                int n = Lock(reply, log);
                try
                {
                    UnixSocketMsg.Send(socket, reply[..n], []);
                }
                catch (IOException)
                {
                    return;
                }

                continue;
            }

            if (DrmWire.TryReadAgentPoll(request[..length]))
            {
                Wayland.Agent.AgentHandoff.Offered? agent = agents?.Take();
                int n = DrmWire.WriteAgent(reply, agent?.Uid ?? 0, agent?.Session ?? string.Empty, fdAttached: agent is not null);
                try
                {
                    UnixSocketMsg.Send(socket, reply[..n], agent is not null ? [agent.Socket] : []);
                }
                catch (IOException)
                {
                    return;
                }
                finally
                {
                    if (agent is not null)
                    {
                        _ = UnixSocketMsg.close(agent.Socket); // the engine has its own copy now
                        log.LogInformation("Handed the session agent for uid {Uid} (session {Session}) to the engine", agent.Uid, agent.Session);
                    }
                }

                continue;
            }

            byte kind = DrmWire.KindOf(request[..length]);
            if (kind is DrmWire.KindOpenTerminal or DrmWire.KindTerminalSignal)
            {
                int fd = -1;
                int n = terminals is null
                    ? DrmWire.WriteRefusal(reply, "this daemon gives no terminals")
                    : terminals.Handle(request[..length], reply, out fd);
                try
                {
                    UnixSocketMsg.Send(socket, reply[..n], fd >= 0 ? [fd] : []);
                }
                catch (IOException)
                {
                    return;
                }
                finally
                {
                    if (fd >= 0)
                    {
                        _ = UnixSocketMsg.close(fd); // the engine has its own copy now; the daemon keeps none
                    }
                }

                continue;
            }

            if (!DrmWire.TryReadPoll(request[..length], known))
            {
                log.LogWarning("Ignoring a {Length}-byte message of kind {Kind} on the capture channel", length, DrmWire.KindOf(request[..length]));
                continue;
            }

            if (reader is null)
            {
                int n = DrmWire.WriteNoScanout(reply, generation: 0, crtcOff: false, noHardware: true);
                try
                {
                    UnixSocketMsg.Send(socket, reply[..n], []);
                }
                catch (IOException)
                {
                    return;
                }

                continue;
            }

            try
            {
                DrmFrameInfo? frame = reader.Read(known, out int fd, out bool crtcOff);
                if (frame is null)
                {
                    int n = DrmWire.WriteNoScanout(reply, reader.Generation, crtcOff);
                    UnixSocketMsg.Send(socket, reply[..n], []);
                    continue;
                }

                int size = DrmWire.WriteFrame(reply, frame.Value, fdAttached: fd >= 0);
                UnixSocketMsg.Send(socket, reply[..size], fd >= 0 ? [fd] : []);
            }
            catch (IOException e)
            {
                // The engine's log and the viewer's notice get the same sentence.
                log.LogError(e, "Reading the scanout failed");
                int n = DrmWire.WriteError(reply, e.Message);
                try
                {
                    UnixSocketMsg.Send(socket, reply[..n], []);
                }
                catch (IOException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Locks the seat's signed-in session for the engine. The login screen is not lockable, and a seat with
    /// nobody on it has nothing to lock; both are answered in words rather than with a false "done".
    /// </summary>
    private static int Lock(Span<byte> reply, ILogger log)
    {
        ActiveSession? active = LinuxSessions.Active();
        if (active is not { Role: SessionRole.User })
        {
            return DrmWire.WriteError(reply, "nobody is signed in on the seat, so there is nothing to lock");
        }

        if (!LinuxSessions.Lock(active.Id))
        {
            log.LogWarning("logind refused to lock session {Session} (uid {Uid})", active.Id, active.Uid);
            return DrmWire.WriteError(reply, $"logind refused to lock session {active.Id}");
        }

        log.LogInformation("Locked session {Session} (uid {Uid}) for the viewer", active.Id, active.Uid);
        return DrmWire.WriteLocked(reply);
    }
}
