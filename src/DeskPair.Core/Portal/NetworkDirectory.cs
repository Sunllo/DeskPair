using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskPair.Core.Portal;

/// <summary>Where to signal, and the key to expect when we get there.</summary>
public sealed record NetworkDirectory(string Rendezvous, string PublicKey)
{
    public static readonly NetworkDirectory None = new(string.Empty, string.Empty);

    public bool IsEmpty => Rendezvous.Length == 0;
}

/// <summary>
/// Asks the portal which signalling server to use.
///
/// This exists so the servers can move. An address compiled into the application is an address that cannot
/// change until everybody has updated, and "everybody" includes the copies nobody updates.
///
/// **It is not a secret and it is not a gate.** A running app has to connect to the address, so anybody who
/// wants it can read it off one connection; and a check performed by the application on itself proves
/// nothing to a server, because whoever removed the check is the one asking. What is here is a
/// convenience: people who do not run their own servers get a settings page with nothing to fill in, and
/// the operator gets to move a server without shipping a release.
///
/// Modelled on <see cref="Update.UpdateClient"/>: one long-lived HttpClient, the address passed per call
/// because the configured portal can change, and never an Authorization header -- an install with no
/// account still has to be able to connect, which is what the front page promises.
/// </summary>
public sealed class NetworkDirectoryClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public NetworkDirectoryClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// The directory this portal publishes, or <see cref="NetworkDirectory.None"/> when it publishes none.
    ///
    /// Never throws for a portal that is unreachable or answers something unexpected. The caller's next
    /// move is the same in every one of those cases -- carry on with direct connections only -- and an
    /// exception here would have to be caught on the path that opens the application's window.
    /// </summary>
    public async Task<NetworkDirectory> FetchAsync(string portal, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(Update.UpdateEndpoints.PortalFor(portal) + "/", UriKind.Absolute, out Uri? baseUri))
        {
            return NetworkDirectory.None;
        }

        try
        {
            var url = new Uri(baseUri, "api/v1/network");
            using HttpResponseMessage response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return NetworkDirectory.None;
            }

            NetworkPayload? payload = await response.Content
                .ReadFromJsonAsync<NetworkPayload>(Json, ct).ConfigureAwait(false);

            // Everything below is somebody else's data. A portal that answers with a host containing a
            // newline, or a key that is not base64, gets treated as a portal that answered nothing: the
            // address goes into a socket and the key into a signature check, and neither is a place to
            // find out that a string was not what it claimed.
            string host = Clean(payload?.Rendezvous);
            string key = (payload?.PublicKey ?? string.Empty).Trim();

            // A key that is absent and a key that is malformed are not the same thing. Absent means the
            // portal pins nothing, which is allowed. Malformed means somebody is confused or interfering,
            // and quietly continuing without a pin would answer that by verifying less -- so the whole
            // answer is discarded and this install stays on direct connections.
            return host.Length == 0 || !IsBase64(key) ? NetworkDirectory.None : new NetworkDirectory(host, key);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return NetworkDirectory.None;
        }
    }

    private static string Clean(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        return trimmed.Length is 0 or > 253 || trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            ? string.Empty
            : trimmed;
    }

    /// <summary>An empty key is allowed -- it means "do not pin one" -- but a malformed one is not.</summary>
    private static bool IsBase64(string value) =>
        value.Length == 0 || Convert.TryFromBase64String(value, new byte[value.Length], out _);

    private sealed record NetworkPayload(
        [property: JsonPropertyName("rendezvous")] string? Rendezvous,
        [property: JsonPropertyName("publicKey")] string? PublicKey);

    public void Dispose() => _http.Dispose();
}
