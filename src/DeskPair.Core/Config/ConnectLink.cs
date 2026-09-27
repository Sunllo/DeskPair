using System.Text;

namespace DeskPair.Core.Config;

/// <summary>
/// What a desk puts in a QR code so a phone can reach it.
/// </summary>
/// <remarks>
/// <para>
/// The format is
/// <c>sunllo://connect?v=1&amp;id=123456789&amp;rs=host:port&amp;k=&lt;base64url SPKI&gt;&amp;n=&lt;name&gt;&amp;p=&lt;one-time password&gt;</c>.
/// </para>
/// <para>
/// It carries the rendezvous server and its public key as well as the id, because that is the barrier that
/// actually stops people: nobody is going to type a ninety-one byte SPKI into a phone with their thumbs, and
/// without it a connection by id verifies nothing at all (see <see cref="PeerSettings.ServerPublicKey"/>).
/// One scan configures the phone and names the desk.
/// </para>
/// <para>
/// The password it carries is never the one on screen. A QR code gets photographed, screen-shared and left
/// up, so the code holds a secret of its own — the host's link password — which the first connection that
/// uses it spends. The host issues another immediately and the code redraws, so a photograph of it is worth
/// nothing by the time anybody gets round to using it. The temporary password the user reads aloud is not in
/// here and does not move when somebody scans. See <c>HostPasswords.LinkPassword</c>.
/// </para>
/// <para>
/// That still leaves a window between a code being displayed and being used, which is why the host asks
/// before letting a scan in unless the user has turned that off, and why the link password is sixty bits
/// rather than the six characters a person could retype.
/// </para>
/// <para>
/// <c>v</c> is mandatory and checked. Without it, a build that predates a format change would try to read
/// the new one and fail in whatever way it happened to fail; with it, it can say "this needs a newer
/// version" instead.
/// </para>
/// </remarks>
public sealed record ConnectLink
{
    /// <summary>The only version this build writes, and the only one it reads.</summary>
    public const int Version = 1;

    public const string Scheme = "sunllo";

    private const string Prefix = $"{Scheme}://connect?";

    /// <summary>The nine-digit peer id. Always present.</summary>
    public required string Id { get; init => field = value ?? string.Empty; }

    /// <summary>Rendezvous server as host[:port]; empty means "use whatever the phone already has".</summary>
    public string RendezvousServer { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Base64 (standard, not url-safe) SPKI of the rendezvous signing key; empty if not shared.</summary>
    public string ServerPublicKeyBase64 { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>What the desk calls itself, so the phone can label it before it has ever connected.</summary>
    public string DeviceName { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>
    /// The host's one-time link password, or empty when the desk is not offering one — which is what a host
    /// with temporary passwords switched off looks like. A phone that scans a code without it has to ask.
    /// </summary>
    public string Password { get; init => field = value ?? string.Empty; } = string.Empty;

    public override string ToString()
    {
        var text = new StringBuilder(Prefix);
        text.Append("v=").Append(Version);
        text.Append("&id=").Append(Uri.EscapeDataString(Id));

        if (RendezvousServer.Length > 0)
        {
            text.Append("&rs=").Append(Uri.EscapeDataString(RendezvousServer));
        }

        if (ServerPublicKeyBase64.Length > 0)
        {
            // Base64url in the link: a standard-base64 '+' and '/' would each have to be percent-encoded,
            // which makes the code a fifth larger for no gain. Converted back on the way out.
            text.Append("&k=").Append(ToBase64Url(ServerPublicKeyBase64));
        }

        if (DeviceName.Length > 0)
        {
            text.Append("&n=").Append(Uri.EscapeDataString(DeviceName));
        }

        if (Password.Length > 0)
        {
            text.Append("&p=").Append(Uri.EscapeDataString(Password));
        }

        return text.ToString();
    }

    /// <summary>Reads a scanned string, or returns null with a reason the UI can show.</summary>
    public static bool TryParse(string? text, out ConnectLink? link, out string? problem)
    {
        link = null;
        problem = null;

        string value = (text ?? string.Empty).Trim();
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            problem = "That is not a DeskPair code.";
            return false;
        }

        Dictionary<string, string> fields = ParseQuery(value[Prefix.Length..]);

        if (!fields.TryGetValue("v", out string? version) || !int.TryParse(version, out int parsed))
        {
            problem = "That code is missing its version.";
            return false;
        }

        if (parsed != Version)
        {
            // Deliberately not "best effort": a code written by a newer build may mean something different
            // by the same field, and guessing is how a phone ends up dialling the wrong machine.
            problem = $"That code was made by a newer version of DeskPair (v{parsed}). Get the latest version at {Update.UpdateEndpoints.OfficialPortal}/download.";
            return false;
        }

        if (!fields.TryGetValue("id", out string? id) || id.Length == 0)
        {
            problem = "That code names no computer.";
            return false;
        }

        string key = fields.GetValueOrDefault("k", string.Empty);
        if (key.Length > 0)
        {
            key = FromBase64Url(key);
            try
            {
                Convert.FromBase64String(PeerSettings.CleanBase64(key));
            }
            catch (FormatException)
            {
                problem = "The key in that code is damaged.";
                return false;
            }
        }

        link = new ConnectLink
        {
            Id = id,
            RendezvousServer = fields.GetValueOrDefault("rs", string.Empty),
            ServerPublicKeyBase64 = key,
            DeviceName = fields.GetValueOrDefault("n", string.Empty),
            Password = fields.GetValueOrDefault("p", string.Empty),
        };
        return true;
    }

    /// <summary>Unknown keys are kept rather than rejected, so an older build can read a newer code's basics.</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string name = pair[..equals];
            string value = Uri.UnescapeDataString(pair[(equals + 1)..]);
            fields[name] = value;
        }

        return fields;
    }

    private static string ToBase64Url(string base64) =>
        PeerSettings.CleanBase64(base64).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string FromBase64Url(string value)
    {
        string standard = value.Replace('-', '+').Replace('_', '/');
        return standard.PadRight(standard.Length + ((4 - (standard.Length % 4)) % 4), '=');
    }
}
