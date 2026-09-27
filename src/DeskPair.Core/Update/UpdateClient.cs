using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskPair.Core.Portal;

namespace DeskPair.Core.Update;

/// <summary>
/// What the portal says the newest version is.
///
/// Four fields, and no URL among them on purpose. The client opens a download page it builds from the
/// portal address it was configured with, so the worst a forged manifest achieves is claiming a version
/// that does not exist -- after which the reader lands on the real page anyway. That is why there is no
/// signature here, and why adding a URL field would quietly undo the reasoning.
/// </summary>
public sealed record UpdateManifest(string Channel, string Version, DateTimeOffset Released, string Notes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(ApiError))]
internal sealed partial class UpdateJson : JsonSerializerContext;

/// <summary>
/// Asks a portal what it is publishing.
///
/// Modelled on <see cref="PortalClient"/>, including one long-lived <see cref="HttpClient"/> rather than
/// one per call, and the two catches that tell "could not reach" apart from "did not answer in time".
///
/// It differs in two ways, and both follow from what this is for. The address is an argument rather than
/// something bound at construction, because the user can change the portal while the app is running and a
/// client holding the old one would go on asking it forever. And no <c>Authorization</c> header is ever
/// sent: the check has to work on an install that has never linked an account, and a per-device token
/// would turn "is there an update" into an authenticated ping.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    /// <summary>Longer than a page of release notes ever needs, and short enough to render.</summary>
    public const int MaximumNotesLength = 2000;

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public UpdateClient(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<UpdateManifest> FetchAsync(string? portal, string channel, CancellationToken ct = default)
    {
        string url = UpdateEndpoints.ManifestFor(portal, channel);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await FailureAsync(url, response, ct).ConfigureAwait(false);
            }

            UpdateManifest? manifest = await response.Content
                .ReadFromJsonAsync(UpdateJson.Default.UpdateManifest, ct).ConfigureAwait(false);

            if (manifest is null)
            {
                throw new PortalException($"{url} answered with nothing.");
            }

            // Trimmed here rather than in a view, so no screen can forget that these are somebody else's
            // words. They are shown as text and never as markup.
            return manifest with { Notes = Tidy(manifest.Notes) };
        }
        catch (HttpRequestException e)
        {
            throw new PortalException($"Could not reach {url}.", inner: e);
        }
        catch (JsonException e)
        {
            // A captive portal answering a login page looks exactly like this.
            throw new PortalException($"{url} did not answer with an update manifest.", inner: e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            // The guard matters: without it a real cancellation is reported to the user as a timeout.
            throw new PortalException($"{url} did not answer in time.", inner: e);
        }
    }

    /// <summary>The signed manifest is never bigger than this; anything larger is not one.</summary>
    public const int MaximumManifestBytes = 1 << 20;

    /// <summary>
    /// The raw bytes of <c>release.json</c> and the signature published beside it, or null when the portal
    /// publishes no signature -- an unsigned release, which is news and never an install. The bytes are
    /// returned as they came, because the signature is over exactly those and a re-serialisation would
    /// never verify.
    /// </summary>
    public async Task<(byte[] Manifest, string Signature)?> FetchSignedAsync(string? portal, CancellationToken ct = default)
    {
        string baseUrl = UpdateEndpoints.PortalFor(portal) + "/downloads/";
        try
        {
            using HttpResponseMessage sig = await _http.GetAsync(baseUrl + SignedRelease.SignatureFileName, ct).ConfigureAwait(false);
            if (!sig.IsSuccessStatusCode || (sig.Content.Headers.ContentLength ?? 0) > 4096)
            {
                return null;
            }

            string signature = await sig.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using HttpResponseMessage manifest = await _http.GetAsync(baseUrl + SignedRelease.ManifestFileName, ct).ConfigureAwait(false);
            if (!manifest.IsSuccessStatusCode || (manifest.Content.Headers.ContentLength ?? 0) > MaximumManifestBytes)
            {
                return null;
            }

            byte[] bytes = await manifest.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return bytes.Length > MaximumManifestBytes ? null : (bytes, signature.Trim());
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<PortalException> FailureAsync(
        string url, HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            ApiError? error = await response.Content
                .ReadFromJsonAsync(UpdateJson.Default.ApiError, ct).ConfigureAwait(false);
            if (error is not null && error.Error.Length > 0)
            {
                return new PortalException(error.Message.Length > 0 ? error.Message : error.Error, error.Error);
            }
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or NotSupportedException)
        {
            // Not JSON. The status is all there is to report, which is what happens below.
        }

        return new PortalException($"{url} answered {(int)response.StatusCode}.");
    }

    private static string Tidy(string? notes)
    {
        if (string.IsNullOrEmpty(notes))
        {
            return string.Empty;
        }

        string trimmed = notes.Length > MaximumNotesLength ? notes[..MaximumNotesLength] : notes;

        // Control characters other than a newline have no business in text somebody is shown.
        return string.Concat(trimmed.Where(c => c == '\n' || !char.IsControl(c)));
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
