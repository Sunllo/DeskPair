using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskPair.Core.Config;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Protocol.Crypto;

namespace DeskPair.Core.Portal;

/// <summary>What this installation knows about the account it belongs to, if any.</summary>
/// <summary>
/// The end of signing in: either this device is linked, or the account still has to confirm its address.
///
/// The second is not a failure and must not be reported as one. The account was created; there is simply
/// no code to spend until somebody opens the email.
/// </summary>
public sealed record AccountSignIn(LinkState State, bool NeedsVerification)
{
    public static readonly AccountSignIn Unverified = new(LinkState.Unlinked, true);
}

public sealed record LinkState
{
    public static readonly LinkState Unlinked = new();

    public bool IsLinked => DeviceId.Length > 0;

    public string DeviceId { get; init; } = string.Empty;

    public string Account { get; init; } = string.Empty;

    public string Alias { get; init; } = string.Empty;

    /// <summary>The portal this device is linked to, which may differ from what settings currently point at.</summary>
    public string PortalUrl { get; init; } = string.Empty;

    /// <summary>Address books this device may sync. Empty until the portal has been asked.</summary>
    public IReadOnlyList<PortalScope> Scopes { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredLink))]
internal sealed partial class LinkJson : JsonSerializerContext;

/// <summary>The part of the link that has to survive a restart. Kept in the platform keystore, not in config.</summary>
internal sealed record StoredLink(string PortalUrl, string DeviceId, string Token, string Account, string Alias);

/// <summary>
/// Binds this installation to a portal account, using the identity key it already has.
///
/// The app has generated an ECDSA P-256 key on first run since before any of this existed, and keeps it in
/// the platform keystore through <see cref="ISecretStore"/>. Linking signs a challenge with it. So no account
/// password is ever typed into the app, nothing but a revocable token is stored, and the key that proves who
/// this device is turns out to be the one it was already using to prove that to peers.
///
/// The token lives beside the private key in the keystore rather than in <c>config.json</c>, because it is a
/// credential and the config file is a plain-text file people edit by hand.
/// </summary>
/// <param name="identity">The ECDSA P-256 key this machine already proves itself to peers with.</param>
/// <param name="secrets">The platform keystore; the device token is kept beside the private key.</param>
/// <param name="http">
/// The transport, for a test that needs to answer as a portal would. Null means one of our own per call,
/// which is what the product does: these calls are minutes apart at most, so a client per call costs
/// nothing here -- unlike the address-book sync, which runs on a timer and shares one.
/// </param>
public sealed class AccountLink(PeerIdentityStore identity, ISecretStore secrets, HttpClient? http = null)
{
    private const string StorageKey = "portal-link";

    /// <summary>
    /// Sends the code and a signature over <see cref="DeviceLink.Challenge"/>, and keeps what comes back.
    ///
    /// The audience in the challenge is the portal's own base URL as this client computed it, which must
    /// match what the portal is configured with. A mismatch fails as a bad signature, and the message says
    /// so — which is the one confusing failure here and is worth knowing about.
    /// </summary>
    public async Task<LinkState> LinkAsync(string portalUrl, string code, string alias, CancellationToken ct = default)
    {
        using PortalClient client = Client(portalUrl);
        byte[] challenge = DeviceLink.Challenge(DeviceLink.Normalise(code), client.BaseUrl);
        var request = new LinkRequest(
            Code: DeviceLink.Normalise(code),
            PublicKey: Convert.ToBase64String(identity.Key.PublicKeySpki),
            Signature: Convert.ToBase64String(identity.Key.Sign(challenge)),
            PeerId: identity.IsLocalId ? string.Empty : identity.Id,
            Alias: alias,
            Platform: Session.Host.PlatformName.Current);

        LinkResponse response = await client.LinkAsync(request, ct).ConfigureAwait(false);
        await SaveAsync(new StoredLink(client.BaseUrl, response.DeviceId, response.Token, response.Account, response.Alias), ct)
            .ConfigureAwait(false);

        return new LinkState
        {
            DeviceId = response.DeviceId,
            Account = response.Account,
            Alias = response.Alias,
            PortalUrl = client.BaseUrl,
        };
    }

    /// <summary>
    /// An email and a password: signs in for a linking code, then spends it.
    ///
    /// Two calls rather than one, because the portal only checks a device's signature in one place and
    /// this keeps it that way. The password is never written down here -- the code it buys is spent
    /// immediately and the device token is what is kept.
    /// </summary>
    public async Task<AccountSignIn> SignInAsync(
        string portalUrl, string email, string password, string alias, CancellationToken ct = default)
    {
        string portal = Update.UpdateEndpoints.PortalFor(portalUrl);
        CodeResponse code;
        using (PortalClient client = Client(portal))
        {
            code = await client.SignInAsync(new SignInRequest(email.Trim(), password), ct).ConfigureAwait(false);
        }

        return await SpendAsync(portal, code, alias, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A new account, and this device linked to it in the same breath.
    ///
    /// Unless the portal wants the address confirmed first, in which case there is no code to spend and
    /// the caller is told to say so. That is a success, not a failure: the account exists.
    /// </summary>
    public async Task<AccountSignIn> RegisterAsync(
        string portalUrl, string email, string password, string language, string alias, CancellationToken ct = default)
    {
        string portal = Update.UpdateEndpoints.PortalFor(portalUrl);
        CodeResponse code;
        using (PortalClient client = Client(portal))
        {
            code = await client
                .RegisterAsync(new RegisterRequest(email.Trim(), password, Name: string.Empty, language), ct)
                .ConfigureAwait(false);
        }

        return await SpendAsync(portal, code, alias, ct).ConfigureAwait(false);
    }

    /// <summary>Whether this portal takes new accounts.</summary>
    /// <returns>
    /// Null when the portal could not be asked, which is not the same as no. A caller that treats it as no
    /// hides the button at exactly the moment somebody needs it: the first thing a new install does is
    /// fail to reach something, and being told "no account for you" rather than "could not reach the
    /// portal" sends them looking for the wrong problem.
    /// </returns>
    public static async Task<bool?> TakesNewAccountsAsync(
        string portalUrl, HttpClient? http = null, CancellationToken ct = default)
    {
        try
        {
            using var client = new PortalClient(Update.UpdateEndpoints.PortalFor(portalUrl), http);
            return (await client.AccountOptionsAsync(ct).ConfigureAwait(false)).SelfRegistration;
        }
        catch (PortalException)
        {
            return null;
        }
    }

    private async Task<AccountSignIn> SpendAsync(string portal, CodeResponse code, string alias, CancellationToken ct)
    {
        if (code.Verify || code.Code.Length == 0)
        {
            return AccountSignIn.Unverified;
        }

        return new AccountSignIn(await LinkAsync(portal, code.Code, alias, ct).ConfigureAwait(false), NeedsVerification: false);
    }

    /// <summary>What is stored, without asking the portal. Returns <see cref="LinkState.Unlinked"/> when there is none.</summary>
    public async Task<LinkState> CurrentAsync(CancellationToken ct = default)
    {
        StoredLink? stored = await ReadAsync(ct).ConfigureAwait(false);
        return stored is null
            ? LinkState.Unlinked
            : new LinkState { DeviceId = stored.DeviceId, Account = stored.Account, Alias = stored.Alias, PortalUrl = stored.PortalUrl };
    }

    /// <summary>
    /// Asks the portal whether this device is still linked, and what it may sync.
    ///
    /// A revoked device learns it here, and forgets its token: leaving a dead token in the keystore means
    /// every later call fails the same way and the app can never tell "revoked" from "portal is down".
    /// </summary>
    public async Task<LinkState> RefreshAsync(CancellationToken ct = default)
    {
        StoredLink? stored = await ReadAsync(ct).ConfigureAwait(false);
        if (stored is null)
        {
            return LinkState.Unlinked;
        }

        using PortalClient client = Client(stored.PortalUrl);
        WhoAmIResponse response;
        try
        {
            response = await client.WhoAmIAsync(stored.Token, ct).ConfigureAwait(false);
        }
        catch (PortalException e) when (e.Code == "unlinked")
        {
            await secrets.RemoveAsync(StorageKey, ct).ConfigureAwait(false);
            return LinkState.Unlinked;
        }

        if (!string.Equals(response.Account, stored.Account, StringComparison.Ordinal) ||
            !string.Equals(response.Alias, stored.Alias, StringComparison.Ordinal))
        {
            // Renamed in the console, or the device was re-linked to a different account from elsewhere.
            stored = stored with { Account = response.Account, Alias = response.Alias };
            await SaveAsync(stored, ct).ConfigureAwait(false);
        }

        return new LinkState
        {
            DeviceId = response.DeviceId,
            Account = response.Account,
            Alias = response.Alias,
            PortalUrl = stored.PortalUrl,
            Scopes = response.Scopes,
        };
    }

    /// <summary>
    /// Drops the link from this end, telling the portal first so the device stops appearing as live.
    ///
    /// The local token goes whether or not the portal could be reached. Someone unlinking a machine they are
    /// about to hand over cares that it stops working here; leaving the credential behind because the
    /// network was down would be the wrong way round.
    /// </summary>
    public async Task<PortalException?> UnlinkAsync(CancellationToken ct = default)
    {
        StoredLink? stored = await ReadAsync(ct).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        PortalException? failure = null;
        try
        {
            using PortalClient client = Client(stored.PortalUrl);
            await client.UnlinkAsync(stored.Token, ct).ConfigureAwait(false);
        }
        catch (PortalException e)
        {
            failure = e;
        }

        await secrets.RemoveAsync(StorageKey, ct).ConfigureAwait(false);
        return failure;
    }

    /// <summary>The bearer token for API calls, or null when this device is not linked.</summary>
    public async Task<(string PortalUrl, string Token)?> CredentialAsync(CancellationToken ct = default)
    {
        StoredLink? stored = await ReadAsync(ct).ConfigureAwait(false);
        return stored is null ? null : (stored.PortalUrl, stored.Token);
    }

    private PortalClient Client(string portalUrl) => new(Update.UpdateEndpoints.PortalFor(portalUrl), http);

    private async Task<StoredLink?> ReadAsync(CancellationToken ct)
    {
        byte[]? bytes = await secrets.GetAsync(StorageKey, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(bytes, LinkJson.Default.StoredLink);
        }
        catch (JsonException)
        {
            // A keystore entry from an older shape, or a truncated write. Treat it as not linked rather than
            // failing every call: linking again is one code away, and there is nothing here to lose.
            return null;
        }
    }

    private ValueTask SaveAsync(StoredLink link, CancellationToken ct) =>
        secrets.SetAsync(StorageKey, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(link, LinkJson.Default.StoredLink)), ct);
}
