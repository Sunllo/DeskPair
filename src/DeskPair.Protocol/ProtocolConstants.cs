namespace DeskPair.Protocol;

public static class ProtocolConstants
{
    /// <summary>
    /// The newest protocol this build speaks. 2 (2026-09) added version negotiation, relay tickets and
    /// PBKDF2 password derivation; 1 is refused outright, since a protocol-1 peer cannot use a relay any
    /// more and would only get as far as a session that fails on its first fallback.
    /// </summary>
    public const uint ProtocolVersion = 2;

    /// <summary>The oldest protocol this build still speaks.</summary>
    public const uint MinProtocolVersion = 2;

    /// <summary>
    /// What ControllerHello.protocol_version carries. Kept at 1 so a protocol-1 host answers with its
    /// hello -- which this side then refuses with a message naming the host -- instead of closing the socket
    /// without a word. The range a controller actually speaks is in min/max_protocol_version.
    /// </summary>
    public const uint LegacyHelloVersion = 1;

    /// <summary>How long a relay ticket is good for: both peers must dial within it, and no longer.</summary>
    public static readonly TimeSpan RelayTicketLifetime = TimeSpan.FromMinutes(2);

    public const int HttpApiPort = 21114;
    public const int NatTestPort = 21115;
    public const int RendezvousPort = 21116;
    public const int RelayPort = 21117;
    public const int DirectAccessPort = 21118;
    public const int LanDiscoveryPort = 21119;

    /// <summary>
    /// The portal: accounts, the address book and the update feed. Not part of the peer protocol, and named
    /// here only so the one list of ports this project uses stays in one place. Behind a reverse proxy on
    /// 80/443 once there is a domain; until then it is reached directly.
    /// </summary>
    public const int PortalPort = 21120;

    public const int MaxPeerFrameBytes = 32 * 1024 * 1024;
    public const int MaxControlFrameBytes = 16 * 1024;
    public const int MaxUdpDatagramBytes = 1200;

    public const int NonceBytes = 32;
    public const int SaltBytes = 16;
    public const int ChallengeBytes = 32;

    public static readonly TimeSpan RegisterInterval = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan PeerOfflineAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TcpMediatorHeartbeat = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PunchRequestBaseDeadline = TimeSpan.FromSeconds(3);
    public const int PunchRequestAttempts = 3;
    public static readonly TimeSpan RelayPairingTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RelayIdleTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SessionHeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SessionReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Silence long enough to tell the user about, well before it is long enough to give up on. A healthy
    /// session is never quiet for this long: the host sends a delay probe every second and the framing layer
    /// a heartbeat every five, so this is more than one missed heartbeat and the session still has most of
    /// <see cref="SessionReadTimeout"/> left to recover in.
    /// </summary>
    public static readonly TimeSpan SessionStalledAfter = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan TestDelayInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(18);
    public static readonly TimeSpan VideoSendTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BulkSendTimeout = TimeSpan.FromSeconds(60);

    public const int FileBlockBytes = 128 * 1024;

    // ---- UDP media channel ----
    public const int RelayUdpPort = RelayPort;
    public const byte MediaMagic = 0xA5;
    public const int MediaCommonHeaderBytes = 12;
    public const int MediaHeaderBytes = 32;
    public const int MediaTagBytes = 16;
    /// <summary>Largest payload of a media datagram: 1200 - common header - media header - tag.</summary>
    public const int MaxShardBytes = MaxUdpDatagramBytes - MediaCommonHeaderBytes - MediaHeaderBytes - MediaTagBytes;
    public static readonly TimeSpan MediaOfferTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MediaKeepalive = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MediaDeadAfter = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan UdpRelayPairingTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UdpRelayIdleTimeout = TimeSpan.FromSeconds(60);
    public const int MaxClipboardBytes = 16 * 1024 * 1024;
}
