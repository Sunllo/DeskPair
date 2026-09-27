using System.Text;

namespace DeskPair.Protocol.Crypto;

/// <summary>
/// The exact bytes a device signs to prove it holds its identity key when linking to an account.
///
/// Here, rather than in the portal or the client, because both ends have to agree byte for byte and there is
/// no way to discover a disagreement other than by failing to link. The mobile client reimplements this in
/// Kotlin; <c>DeviceLinkTests</c> pins a vector so the two cannot drift apart silently.
///
/// What it binds, and why each part is there:
///
///  - A fixed prefix, so a signature made for this purpose can never be read as a signature made for
///    another. The identity key also signs session handshakes; nothing else it signs starts with this.
///  - The link code, which is single use, short lived and belongs to one account. This is what ties the
///    signature to "the person who was looking at the console a minute ago".
///  - The portal's own base URL, so a signature captured by one portal cannot be replayed at another.
///
/// There is deliberately no server nonce, which would cost a round trip and a table. It would buy replay
/// protection that the single-use code already gives: a captured request replayed against the same portal
/// finds the code gone. What none of this survives is an active attacker on the wire, who can simply
/// substitute their own public key and signature — that is what TLS is for, and this deployment does not
/// have it yet.
/// </summary>
public static class DeviceLink
{
    private const string Prefix = "deskpair-device-link\n";

    /// <summary>How long a code stays usable. Long enough to walk to the other machine, short enough to matter.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Characters in a link code: the readable alphabet, so a code read off a screen can be typed.</summary>
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public const int CodeLength = 8;

    /// <summary>
    /// The message to sign. <paramref name="audience"/> is the portal's public base URL, exactly as the
    /// portal is configured with it.
    /// </summary>
    public static byte[] Challenge(string code, string audience) =>
        Encoding.UTF8.GetBytes(Prefix + Normalise(code) + "\n" + audience.TrimEnd('/'));

    /// <summary>
    /// Codes are compared in one form, so someone typing lower case or leaving the separator in still links.
    /// The console prints them as two groups of four; people type back what they see.
    /// </summary>
    public static string Normalise(string code)
    {
        Span<char> buffer = stackalloc char[code.Length];
        int n = 0;
        foreach (char c in code)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                buffer[n++] = char.ToUpperInvariant(c);
            }
        }

        return new string(buffer[..n]);
    }

    /// <summary>The code as the console shows it: <c>ABCD-EFGH</c>.</summary>
    public static string Format(string code) =>
        code.Length == CodeLength ? string.Concat(code[..4], "-", code[4..]) : code;
}
