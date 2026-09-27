using DeskPair.Protocol;

namespace DeskPair.Relay;

public sealed class RelayOptions
{
    public const string Section = "Relay";

    public int Port { get; set; } = ProtocolConstants.RelayPort;

    public int HttpPort { get; set; } = ProtocolConstants.HttpApiPort;

    /// <summary>
    /// Where this relay is, as a short tag the rendezvous matches against a connecting address's country
    /// (<c>tw</c>, <c>jp</c>, <c>us</c>). Empty means "anywhere", and is the right answer while there is one
    /// relay: a region nobody matches would take it out of consideration entirely.
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// What this machine is willing to carry, in kbps. 0 means unmetered, which is honest only on a link
    /// nobody is paying for.
    ///
    /// <see cref="MaxSessions"/> alone cannot express this. One 1080p session is about 4 Mb/s
    /// (<c>VideoQosController.BaseBitrateKbps</c>), so the default thousand sessions is four gigabits — more
    /// than any single machine has. Without a bitrate ceiling a busy relay does not refuse anything; it
    /// saturates its link, every session's QoS collapses together, and the stats endpoint goes on reporting
    /// a comfortable session count. That is worse than a refusal, because nobody can see it.
    /// </summary>
    public int MaxBitrateKbps { get; set; }

    public int MaxSessions { get; set; } = 1000;

    public int MaxPendingPerIp { get; set; } = 5;

    public int MaxSessionsPerIp { get; set; } = 20;

    public TimeSpan PairingTimeout { get; set; } = ProtocolConstants.RelayPairingTimeout;

    public TimeSpan IdleTimeout { get; set; } = ProtocolConstants.RelayIdleTimeout;

    /// <summary>Zero means unlimited.</summary>
    public TimeSpan MaxSessionDuration { get; set; } = TimeSpan.Zero;

    public TimeSpan FirstFrameTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>UDP media relay port; 0 (default) reuses the TCP port number so peers address one host:port.</summary>
    public int UdpPort { get; set; }

    public TimeSpan UdpPairingTimeout { get; set; } = ProtocolConstants.UdpRelayPairingTimeout;

    public TimeSpan UdpIdleTimeout { get; set; } = ProtocolConstants.UdpRelayIdleTimeout;

    /// <summary>
    /// Base64 SPKI of the rendezvous server that brokers sessions for this relay: the key its relay
    /// tickets are checked against. The same value clients configure as the "server key".
    /// </summary>
    public string RendezvousPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Whether a request must carry a verifying ticket. On by default: a relay that pairs any two sockets
    /// agreeing on a uuid is a free TCP proxy for whoever can reach its port. Off is for the window during
    /// which peers that predate tickets are still being updated -- and for nothing after that.
    /// </summary>
    public bool RequireTicket { get; set; } = true;

    /// <summary>Decoded <see cref="RendezvousPublicKey"/>, or null when none is configured or it is not base64.</summary>
    public byte[]? RendezvousPublicKeySpki
    {
        get
        {
            string cleaned = new(RendezvousPublicKey.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (cleaned.Length == 0)
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(cleaned);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
