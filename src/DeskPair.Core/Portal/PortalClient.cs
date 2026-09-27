using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DeskPair.Core.Portal;

/// <summary>
/// Raised for anything the caller should show a person.
///
/// <c>Message</c> is a finished English sentence and is the fallback, not the answer: a user
/// interface translates <see cref="Code"/> and falls back to the sentence for a code it has never heard
/// of, which is what lets the portal add one without every client having to ship first.
/// </summary>
public sealed class PortalException(string message, string? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>
    /// The machine-readable half: <c>bad_credentials</c>, <c>unlinked</c>, <c>bad_code</c> and the rest of
    /// what the portal sends, plus this client's own <c>unreachable</c>, <c>timeout</c>, <c>empty</c> and
    /// <c>http</c> for the failures that never reach it.
    /// </summary>
    public string? Code { get; } = code;
}

/// <summary>An email and a password, from an app that would rather not send somebody to a browser.</summary>
public sealed record SignInRequest(string Email, string Password);

/// <summary>The same, plus what a new account needs. An empty name means the address, as the website does.</summary>
public sealed record RegisterRequest(string Email, string Password, string Name, string Language);

/// <summary>
/// A linking code, which is what a password buys.
///
/// Signing in does not link the device, deliberately: linking means signing a challenge derived from the
/// code with the device key, and the portal has exactly one endpoint that checks that. <see cref="Verify"/>
/// comes back instead of a code when the account exists but has to confirm its address first.
/// </summary>
public sealed record CodeResponse(string Code, DateTimeOffset Expires, bool Verify);

/// <summary>What this portal will let the app offer, so it does not show a button the portal refuses.</summary>
public sealed record AccountOptionsResponse(bool SelfRegistration);

public sealed record LinkRequest(string Code, string PublicKey, string Signature, string PeerId, string Alias, string Platform);

public sealed record LinkResponse(string DeviceId, string Token, DateTimeOffset Expires, string Account, string Alias);

public sealed record WhoAmIResponse(string DeviceId, string Account, string Alias, IReadOnlyList<PortalScope> Scopes);

/// <summary>An address book this device may sync: the account's own list, or a team's.</summary>
public sealed record PortalScope(string Kind, string Id, string Name);

public sealed record ApiError(string Error, string Message);

/// <summary>One saved computer on the wire.</summary>
/// <param name="Target">A peer id, or an address. This is the entry's identity.</param>
/// <param name="Alias">The name the user gave it.</param>
/// <param name="Folder">The list folder; called a <c>Group</c> locally, because here a group is a team of people.</param>
/// <param name="Note">Whatever the user wrote about it.</param>
/// <param name="LastConnected">Unix milliseconds, 0 for never.</param>
/// <param name="Platform">What it was running when last reached. Reported by the machine, never typed.</param>
/// <param name="Deleted">A removal other devices still have to hear about.</param>
/// <param name="Rev">The portal's revision. Set on the way out, ignored on the way in.</param>
public sealed record EntryChange(
    string Target,
    string Alias,
    string Folder,
    string Note,
    long LastConnected,
    string Platform,
    bool Deleted,
    long Rev);

public sealed record FolderChange(string Name, bool Deleted);

/// <summary>
/// One connection this computer had, on its way to the account's record.
///
/// Shaped by hand rather than reusing the engine's own record, because what leaves the machine is a
/// deliberate subset: everything here is about who was here and what they were allowed to do, and the
/// wire is where that promise has to be visible.
/// </summary>
/// <param name="Id">The id the host minted, so uploading twice writes one row.</param>
/// <param name="StartedUtc">Unix milliseconds.</param>
/// <param name="EndedUtc">Unix milliseconds; 0 when the host never saw it end.</param>
/// <param name="PeerId">Who connected, as they identified themselves.</param>
/// <param name="PeerName">What they call their computer.</param>
/// <param name="PeerPlatform">What they were running.</param>
/// <param name="Address">Where they came from.</param>
/// <param name="AddressReported">The address is the signalling server's word rather than what the socket showed.</param>
/// <param name="Transport">"DirectTcp", "Lan", "PunchedTcp" or "Relay".</param>
/// <param name="Kind">"remote", "file-transfer", "terminal" or "cleared".</param>
/// <param name="Authenticated">"temporary", "permanent", "approval" or "none".</param>
/// <param name="Granted">What they were allowed to do.</param>
/// <param name="Reason">Why it ended.</param>
/// <param name="TerminalOpens">How many shells the connection opened.</param>
/// <param name="TerminalIdentity">The account those shells ran as, when there were any.</param>
public sealed record ConnectionUpload(
    string Id,
    long StartedUtc,
    long EndedUtc,
    string PeerId,
    string PeerName,
    string PeerPlatform,
    string Address,
    bool AddressReported,
    string Transport,
    string Kind,
    string Authenticated,
    IReadOnlyList<string> Granted,
    string Reason,
    int TerminalOpens = 0,
    string TerminalIdentity = "");

/// <summary>How many rows the portal kept.</summary>
public sealed record RecordedResponse(int Recorded);

/// <summary>One sync: what this client changed and the revision it last saw, in one request with the answer.</summary>
/// <param name="ScopeKind">"personal" or "team".</param>
/// <param name="ScopeId">The account id for a personal book, the team id for a shared one.</param>
/// <param name="Entries">Saved computers this client has added, changed or removed.</param>
/// <param name="Folders">List folders this client has added or removed.</param>
/// <param name="Since">
/// The last revision this client applied. 0 asks for the whole book, tombstones included — which is what a
/// reinstall needs, or it would quietly bring back everything the account had ever deleted.
/// </param>
public sealed record SyncRequest(
    string ScopeKind,
    string ScopeId,
    long Since,
    IReadOnlyList<EntryChange> Entries,
    IReadOnlyList<FolderChange> Folders);

public sealed record SyncResponse(long Rev, IReadOnlyList<EntryChange> Entries, IReadOnlyList<FolderChange> Folders);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncRequest))]
[JsonSerializable(typeof(SyncResponse))]
[JsonSerializable(typeof(SignInRequest))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(CodeResponse))]
[JsonSerializable(typeof(AccountOptionsResponse))]
[JsonSerializable(typeof(LinkRequest))]
[JsonSerializable(typeof(LinkResponse))]
[JsonSerializable(typeof(WhoAmIResponse))]
[JsonSerializable(typeof(ConnectionUpload[]))]
[JsonSerializable(typeof(RecordedResponse))]
[JsonSerializable(typeof(ApiError))]
internal sealed partial class PortalJson : JsonSerializerContext;

/// <summary>
/// Talks to the portal's client API.
///
/// One <see cref="HttpClient"/> for the life of the object rather than one per call, which is what the only
/// other HTTP in this product did. That mattered less there — it was a single key fetch on a settings page —
/// but this one is called on a timer, and a new client per call leaks sockets under exactly that pattern.
///
/// Plain http only where the address says so, which in practice is a self-hosted portal on a local network:
/// the official one is https and is what an install with nothing configured uses. That distinction now
/// matters more than it did. Linking carries a signature over a short-lived code and everything after
/// carries a revocable device token, so an eavesdropper on those gets one device's access, which the
/// console can end -- but <see cref="SignInAsync"/> and <see cref="RegisterAsync"/> carry the account
/// password itself, and nothing about them is bounded that way.
/// </summary>
public sealed class PortalClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public PortalClient(string baseUrl, HttpClient? http = null)
    {
        BaseUrl = Normalise(baseUrl);
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>The portal's address, without a trailing slash: also the audience a link signature is bound to.</summary>
    public string BaseUrl { get; }

    /// <summary>Accepts "portal.example.com", "portal.example.com:21120" or a full URL.</summary>
    public static string Normalise(string baseUrl)
    {
        string value = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return value.Contains("://", StringComparison.Ordinal)
            ? value
            : "http://" + (value.Contains(':', StringComparison.Ordinal) ? value : $"{value}:{Protocol.ProtocolConstants.PortalPort}");
    }

    /// <summary>
    /// An email and a password for a linking code.
    ///
    /// The password is sent and never stored: what comes back is a code, which buys a device token, and
    /// the token is the only credential this machine keeps. So a machine somebody walks away from holds
    /// something the account holder can revoke from the console, not the account itself.
    /// </summary>
    public Task<CodeResponse> SignInAsync(SignInRequest request, CancellationToken ct) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v1/account/signin")
            {
                Content = JsonContent.Create(request, PortalJson.Default.SignInRequest),
            },
            token: null,
            PortalJson.Default.CodeResponse,
            ct);

    /// <summary>A new account. The code comes back empty with <c>Verify</c> set when the address must be confirmed.</summary>
    public Task<CodeResponse> RegisterAsync(RegisterRequest request, CancellationToken ct) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v1/account/register")
            {
                Content = JsonContent.Create(request, PortalJson.Default.RegisterRequest),
            },
            token: null,
            PortalJson.Default.CodeResponse,
            ct);

    /// <summary>Whether this portal takes new accounts at all; a self-hoster may have turned that off.</summary>
    public Task<AccountOptionsResponse> AccountOptionsAsync(CancellationToken ct) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/api/v1/account/options"),
            token: null,
            PortalJson.Default.AccountOptionsResponse,
            ct);

    public Task<LinkResponse> LinkAsync(LinkRequest request, CancellationToken ct) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v1/device/link")
            {
                Content = JsonContent.Create(request, PortalJson.Default.LinkRequest),
            },
            token: null,
            PortalJson.Default.LinkResponse,
            ct);

    public Task<WhoAmIResponse> WhoAmIAsync(string deviceToken, CancellationToken ct) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/api/v1/device/me"),
            deviceToken,
            PortalJson.Default.WhoAmIResponse,
            ct);

    public Task<SyncResponse> SyncBookAsync(string deviceToken, SyncRequest request, CancellationToken ct) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v1/book/sync")
            {
                Content = JsonContent.Create(request, PortalJson.Default.SyncRequest),
            },
            deviceToken,
            PortalJson.Default.SyncResponse,
            ct);

    /// <summary>Files this computer's connection record with the account. Returns how many rows were kept.</summary>
    public async Task<int> UploadConnectionsAsync(string deviceToken, IReadOnlyList<ConnectionUpload> events, CancellationToken ct)
    {
        RecordedResponse answer = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v1/device/connections")
            {
                Content = JsonContent.Create(events.ToArray(), PortalJson.Default.ConnectionUploadArray),
            },
            deviceToken,
            PortalJson.Default.RecordedResponse,
            ct).ConfigureAwait(false);
        return answer.Recorded;
    }

    public async Task UnlinkAsync(string deviceToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v1/device/unlink");
        using HttpResponseMessage response = await SendRawAsync(request, deviceToken, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, string? token, JsonTypeInfo<T> shape, CancellationToken ct)
        where T : class
    {
        using (request)
        {
            using HttpResponseMessage response = await SendRawAsync(request, token, ct).ConfigureAwait(false);
            await ThrowIfFailedAsync(response, ct).ConfigureAwait(false);
            T? value = await response.Content.ReadFromJsonAsync(shape, ct).ConfigureAwait(false);
            return value ?? throw new PortalException("The portal sent an empty answer.", "empty");
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, string? token, CancellationToken ct)
    {
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new PortalException($"Could not reach the portal at {BaseUrl}.", "unreachable", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new PortalException($"The portal at {BaseUrl} did not answer in time.", "timeout", e);
        }
    }

    /// <summary>Turns the portal's error body into a message worth showing, falling back to the status code.</summary>
    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync(PortalJson.Default.ApiError, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or HttpRequestException)
        {
            // A proxy or a stray 404 answers in HTML. The status code still says something useful.
        }

        if (error is not null)
        {
            throw new PortalException(error.Message, error.Error);
        }

        throw response.StatusCode == HttpStatusCode.Unauthorized
            ? new PortalException("This device is not linked to an account.", "unlinked")
            : new PortalException($"The portal answered {(int)response.StatusCode}.", "http");
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
