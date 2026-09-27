using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Wayland.Agent;

/// <summary>
/// The daemon's hand-off of a session agent's socket to the engine.
///
/// The daemon starts the agent as the signed-in user with one end of a socketpair and keeps the other end here until
/// the engine asks for it (<see cref="Capture.Drm.DrmWire.KindAgentPoll"/>): the engine was running before anybody
/// signed in, so the socket cannot be handed over at its start. At most one waits; a newer agent's replaces an older
/// one's, which is closed, and one whose agent has gone is withdrawn. Once taken, the descriptor is the engine's.
/// </summary>
public sealed class AgentHandoff
{
    private readonly object _gate = new();
    private Offered? _waiting;

    /// <summary>A session agent's socket, and whose session the agent is in.</summary>
    /// <param name="Socket">The engine's end of the agent's socketpair.</param>
    /// <param name="Uid">The session's user, which the agent runs as.</param>
    /// <param name="Session">logind's session id.</param>
    public sealed record Offered(int Socket, uint Uid, string Session);

    /// <summary>Leaves <paramref name="socket"/> for the engine, closing any older one it never took.</summary>
    public void Offer(int socket, uint uid, string session)
    {
        Offered? older;
        lock (_gate)
        {
            older = _waiting;
            _waiting = new Offered(socket, uid, session);
        }

        Close(older);
    }

    /// <summary>The waiting socket, now the caller's; null when none waits.</summary>
    public Offered? Take()
    {
        lock (_gate)
        {
            Offered? taken = _waiting;
            _waiting = null;
            return taken;
        }
    }

    /// <summary>Closes <paramref name="socket"/> if it is still waiting: its agent has gone before the engine asked.</summary>
    public void Withdraw(int socket)
    {
        Offered? withdrawn = null;
        lock (_gate)
        {
            if (_waiting?.Socket == socket)
            {
                withdrawn = _waiting;
                _waiting = null;
            }
        }

        Close(withdrawn);
    }

    private static void Close(Offered? offered)
    {
        if (offered is not null)
        {
            _ = UnixSocketMsg.close(offered.Socket);
        }
    }
}
