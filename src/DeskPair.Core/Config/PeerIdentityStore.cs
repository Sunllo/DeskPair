using System.Text;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Protocol.Crypto;

namespace DeskPair.Core.Config;

/// <summary>The device's long-term identity: signing key, server-assigned id and machine uuid.</summary>
public sealed class PeerIdentityStore : IDisposable
{
    private const string KeyName = "identity-key";
    private const string IdName = "peer-id";
    private const string LocalIdPrefix = "LAN-";
    private readonly ISecretStore _store;

    private PeerIdentityStore(ISecretStore store, IdentityKey key, string id, byte[] machineId)
    {
        _store = store;
        Key = key;
        Id = id;
        MachineId = machineId;
    }

    public IdentityKey Key { get; }

    /// <summary>
    /// The id viewers see. A rendezvous server assigns the real one; until then (or on a site with no server at
    /// all) this is a local id derived from the identity key, so direct and local-network connections still work.
    /// The handshake refuses an empty id, and direct connections verify the key itself, not this string.
    /// </summary>
    public string Id { get; private set; }

    /// <summary>True while the id is the locally derived one rather than a server-assigned number.</summary>
    public bool IsLocalId => IsLocal(Id);

    /// <summary>
    /// True for the placeholder id a host uses before a rendezvous server assigns it one.
    /// </summary>
    /// <remarks>
    /// Static as well as instance because callers that only hold the string need the same answer — a UI
    /// showing a pairing code, for instance, must not offer one for an id nothing outside the LAN can dial.
    /// </remarks>
    public static bool IsLocal(string id) => id.StartsWith(LocalIdPrefix, StringComparison.Ordinal);

    public byte[] MachineId { get; }

    /// <summary>
    /// The identity this machine already has, or null when it has none yet.
    ///
    /// For every caller that is not the engine. Minting a key is how a machine gets its name, and the
    /// engine is the only thing entitled to do it -- everything else asking for one is asking about a
    /// machine that already exists.
    ///
    /// This is not a nicety. The settings page and the address-book sync both used to call
    /// <see cref="LoadOrCreateAsync"/> on the way up, in parallel with the engine starting, and on a
    /// machine with no identity yet the one that got there first decided what the machine was. Watched
    /// happening: a Mac whose identity was being carried over from the product's previous name had a
    /// fresh key written by the address-book sync in the same second the app opened, at which point the
    /// carry-over correctly declined to overwrite it -- and the machine kept its old id with a new key,
    /// which the rendezvous server had not signed. Every connection then failed at the handshake with
    /// "host identity key does not match the rendezvous-signed key", which is a true statement about a
    /// situation nothing should have been able to create.
    /// </summary>
    public static async Task<PeerIdentityStore?> LoadAsync(ISecretStore store, IMachineIdProvider machineId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(machineId);

        if (await store.GetAsync(KeyName, ct).ConfigureAwait(false) is not { } pkcs8)
        {
            return null;
        }

        IdentityKey key = IdentityKey.FromPkcs8(pkcs8);
        byte[]? idBytes = await store.GetAsync(IdName, ct).ConfigureAwait(false);
        string id = idBytes is null ? string.Empty : Encoding.UTF8.GetString(idBytes);
        return new PeerIdentityStore(store, key, id.Length > 0 ? id : LocalId(key), machineId.GetStableMachineId());
    }

    /// <summary>
    /// The identity this machine has, minting one if it has none.
    ///
    /// Only the engine, and the headless tools that stand in for one, may call this: see
    /// <see cref="LoadAsync"/> for why anything else calling it is a race over what this machine is.
    /// </summary>
    public static async Task<PeerIdentityStore> LoadOrCreateAsync(ISecretStore store, IMachineIdProvider machineId, CancellationToken ct = default)
    {
        byte[]? pkcs8 = await store.GetAsync(KeyName, ct).ConfigureAwait(false);
        IdentityKey key;
        if (pkcs8 is null)
        {
            key = IdentityKey.Create();
            await store.SetAsync(KeyName, key.ExportPkcs8(), ct).ConfigureAwait(false);
        }
        else
        {
            key = IdentityKey.FromPkcs8(pkcs8);
        }

        byte[]? idBytes = await store.GetAsync(IdName, ct).ConfigureAwait(false);
        string id = idBytes is null ? string.Empty : Encoding.UTF8.GetString(idBytes);
        return new PeerIdentityStore(store, key, id.Length > 0 ? id : LocalId(key), machineId.GetStableMachineId());
    }

    /// <summary>Short, stable and obviously not a server id: the first 8 hex characters of the key fingerprint.</summary>
    private static string LocalId(IdentityKey key) => LocalIdPrefix + key.Fingerprint()[..8];

    public async Task SetIdAsync(string id, CancellationToken ct = default)
    {
        if (string.Equals(Id, id, StringComparison.Ordinal))
        {
            return;
        }

        Id = id.Length > 0 ? id : LocalId(Key);
        if (id.Length == 0)
        {
            await _store.RemoveAsync(IdName, ct).ConfigureAwait(false);
        }
        else
        {
            await _store.SetAsync(IdName, Encoding.UTF8.GetBytes(id), ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => Key.Dispose();
}
