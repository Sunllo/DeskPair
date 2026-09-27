using System.Text;
using DeskPair.Platform.Abstractions.Security;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>
/// Where the portal's restore token is kept between sessions: the one thing that lets the second connection skip the
/// consent dialog the first one needed.
///
/// Keyed by the account the engine runs as. The root daemon and a signed-in user's engine share one data directory,
/// and a portal permission belongs to a user's session: a token stored under a fixed key would be one account's,
/// overwritten by the other's (<see cref="ISecretStore"/> tells the story of the time that happened to an identity).
/// A token is also single-use -- the portal consumes it on <c>Start</c> and hands back a new one -- so it is saved
/// again after every session, and forgotten when none comes back.
/// </summary>
public sealed class PortalTokens
{
    private readonly ISecretStore _store;
    private readonly string _key;
    private readonly string _devicesKey;
    private readonly ILogger _log;
    private bool _unreadable;

    public PortalTokens(ISecretStore store, uint uid, ILogger log)
    {
        _store = store;
        _key = KeyFor(uid);
        _devicesKey = DevicesKeyFor(uid);
        _log = log;
    }

    /// <summary>The secret-store key for <paramref name="uid"/>'s token.</summary>
    internal static string KeyFor(uint uid) => $"wayland-portal-{uid}";

    /// <summary>
    /// The devices the remembered permission covers: the token says nothing about whether the person turned on "allow
    /// remote interaction", and the settings page has to be able to say "watch only".
    /// </summary>
    internal static string DevicesKeyFor(uint uid) => $"wayland-portal-{uid}.devices";

    /// <summary>The token saved last time, or null when there is none or it cannot be read.</summary>
    public async ValueTask<string?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            byte[]? stored = await _store.GetAsync(_key, ct).ConfigureAwait(false);
            return stored is null || stored.Length == 0 ? null : Encoding.UTF8.GetString(stored);
        }
        catch (SecretUnreadableException e)
        {
            // Somebody else's, which is not supposed to happen with the uid in the key. Ask the person again, and
            // leave the file alone: writing over what could not be read is how the identity was lost.
            _unreadable = true;
            _log.LogWarning("The saved screen-sharing permission cannot be read ({Reason}); the portal will ask again", e.Message);
            return null;
        }
    }

    /// <summary>
    /// The devices the remembered permission covers (<see cref="Portal.DeviceKeyboard"/>, <see cref="Portal.DevicePointer"/>),
    /// or null when nothing is remembered or it was remembered before this was.
    /// </summary>
    public async ValueTask<uint?> LoadDevicesAsync(CancellationToken ct = default)
    {
        try
        {
            byte[]? stored = await _store.GetAsync(_devicesKey, ct).ConfigureAwait(false);
            return stored is { Length: 4 } ? BitConverter.ToUInt32(stored) : null;
        }
        catch (SecretUnreadableException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps <paramref name="token"/> for next time, with the <paramref name="devices"/> it covers, or forgets the old
    /// one when the portal gave none.
    /// </summary>
    public async ValueTask SaveAsync(string? token, uint devices = 0, CancellationToken ct = default)
    {
        if (_unreadable)
        {
            return;
        }

        if (token is null)
        {
            await _store.RemoveAsync(_key, ct).ConfigureAwait(false);
            await _store.RemoveAsync(_devicesKey, ct).ConfigureAwait(false);
        }
        else
        {
            await _store.SetAsync(_key, Encoding.UTF8.GetBytes(token), ct).ConfigureAwait(false);
            await _store.SetAsync(_devicesKey, BitConverter.GetBytes(devices), ct).ConfigureAwait(false);
        }
    }
}
