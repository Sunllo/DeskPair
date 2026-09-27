using DeskPair.Protocol;

namespace DeskPair.Rendezvous;

public sealed class RendezvousOptions
{
    public const string Section = "Rendezvous";

    public int UdpPort { get; set; } = ProtocolConstants.RendezvousPort;

    public int TcpPort { get; set; } = ProtocolConstants.RendezvousPort;

    public int NatTestPort { get; set; } = ProtocolConstants.NatTestPort;

    public int HttpPort { get; set; } = ProtocolConstants.HttpApiPort;

    /// <summary>PKCS#8 DER file holding the server signing key; generated when missing. Empty = in-memory key (tests).</summary>
    public string KeyPath { get; set; } = "data/server.key";

    /// <summary>SQLite file; empty = no persistence.</summary>
    public string DatabasePath { get; set; } = "data/peers.db";

    /// <summary>
    /// Relay servers as <c>host:port</c>. Kept for configurations written before relays had regions; each
    /// entry becomes a <see cref="RelayEndpoint"/> with no region and no admin key, so it is chosen on
    /// health alone and its load is unknown.
    /// </summary>
    public List<string> RelayServers { get; set; } = [];

    /// <summary>
    /// Relay servers with everything the chooser needs. Merged with <see cref="RelayServers"/>; an address
    /// appearing in both is taken from here.
    /// </summary>
    public List<RelayEndpoint> Relays { get; set; } = [];

    /// <summary>How long a relay ticket this server issues stays valid; both peers dial within it.</summary>
    public TimeSpan RelayTicketLifetime { get; set; } = ProtocolConstants.RelayTicketLifetime;

    public TimeSpan PeerOfflineAfter { get; set; } = ProtocolConstants.PeerOfflineAfter;

    public int KeepAliveSeconds { get; set; } = (int)ProtocolConstants.RegisterInterval.TotalSeconds;

    public TimeSpan PunchPendingTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Skip the same-public-IP LAN shortcut and always punch (tests exercise the punch path on loopback).</summary>
    public bool AlwaysPunch { get; set; }

    public int MaxPeers { get; set; } = 100_000;

    public int RegisterRateLimitPerMinute { get; set; } = 10;

    public int PunchRateLimitPerMinute { get; set; } = 30;

    public TimeSpan TcpIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RelayHealthInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Length of server-assigned numeric ids.</summary>
    public int IdDigits { get; set; } = 9;

    /// <summary>Where this server reports its numbers once a minute, for a dashboard. Off until a URL is set.</summary>
    public StatsReportOptions Report { get; set; } = new();
}

/// <summary>
/// A dashboard's feed: once a minute this server posts its counts -- hosts online, connections, relays -- to
/// <see cref="Url"/>, which keeps them to draw.
///
/// Pushed rather than fetched because the admin port this server would otherwise be asked on is closed to the
/// internet, deliberately, and whatever draws the numbers is a machine on the other side of it. Counts only: the report has no
/// field that could carry an ID, an address or a name.
/// </summary>
public sealed class StatsReportOptions
{
    /// <summary>Where the reports go, <c>https://dashboard.example/report</c>. Empty: report nothing.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Sent as a bearer token with every report.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// One relay this rendezvous may hand out.
///
/// The rendezvous polls these rather than letting relays register themselves. A relay holds no state and no
/// credentials of its own, and giving it an outbound call and a secret so it could announce itself would be
/// a new thing to configure on every relay. Polling keeps the list of who counts as a relay in one file on
/// one machine, and means adding a relay does not mean opening an endpoint anyone can post to.
/// </summary>
public sealed class RelayEndpoint
{
    /// <summary>Where peers reach it: <c>host</c> or <c>host:port</c>, port defaulting to 21117.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Its admin API, when that is not simply the relay address on the default HTTP port. Set it when the
    /// relay's HTTP port differs from its relay port's host, or when health is reached over a private
    /// address the peers never see.
    /// </summary>
    public string StatsAddress { get; set; } = string.Empty;

    /// <summary>
    /// The relay's <c>Admin:ApiKey</c>. Empty works only while the relay leaves its own key empty, which
    /// ships every endpoint unauthenticated and is not a thing to do across a network.
    /// </summary>
    public string AdminKey { get; set; } = string.Empty;

    /// <summary>
    /// A short region tag matched against the connecting address's country, or empty for "anywhere".
    ///
    /// Read from here rather than from what the relay reports about itself: the rendezvous is choosing, and
    /// a relay that could rename its own region could pull traffic to itself.
    /// </summary>
    public string Region { get; set; } = string.Empty;
}
