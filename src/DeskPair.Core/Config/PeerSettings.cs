using DeskPair.Protocol;

namespace DeskPair.Core.Config;

/// <summary>Network settings shared by host and controller roles.</summary>
public sealed record PeerSettings
{
    /// <summary>Rendezvous server as host[:port]; port defaults to 21116.</summary>
    public required string RendezvousServer { get; init => field = value ?? string.Empty; }

    /// <summary>Base64 SPKI of the rendezvous server signing key. Empty disables identity verification (insecure; logged).</summary>
    public string ServerPublicKeyBase64 { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>What this build calls itself, as <c>App.Version</c> reports it. Empty when nobody said.</summary>
    public string Version { get; init => field = value ?? string.Empty; } = string.Empty;

    public TimeSpan ConnectTimeout { get; init; } = ProtocolConstants.ConnectTimeout;

    /// <summary>Second rendezvous TCP port used for NAT classification.</summary>
    public int NatTestPort { get; init; } = ProtocolConstants.NatTestPort;

    /// <summary>Skip NAT detection and direct attempts; always ask for a relay.</summary>
    public bool ForceRelay { get; init; }

    /// <summary>Offer/accept a UDP media channel (video with FEC) next to the TCP session.</summary>
    public bool UdpMedia { get; init; } = true;

    /// <summary>Extra time a punch request may wait for the host's answer on top of the base deadline.</summary>
    public TimeSpan PunchSignallingTimeout { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>Decoded SPKI; whitespace, quotes and a "key=" prefix from careless pasting are tolerated.</summary>
    /// <exception cref="FormatException">The configured key is not base64.</exception>
    public byte[]? ServerPublicKey
    {
        get
        {
            string cleaned = CleanBase64(ServerPublicKeyBase64);
            if (cleaned.Length == 0)
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(cleaned);
            }
            catch (FormatException e)
            {
                throw new FormatException("The rendezvous server public key is not valid base64. Fetch it from http://<server>:21114/key.", e);
            }
        }
    }

    public static string CleanBase64(string value)
    {
        string s = value.Trim().Trim('"', '\'', '`');
        int eq = s.IndexOf(':');
        if (eq >= 0 && eq < 12 && !s[..eq].Any(c => c is '+' or '/' or '='))
        {
            s = s[(eq + 1)..].Trim(); // "key: ..." style prefixes
        }

        return new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }
}
