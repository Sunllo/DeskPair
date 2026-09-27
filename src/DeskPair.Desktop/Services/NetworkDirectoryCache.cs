using DeskPair.Core.Portal;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Where the signalling servers are, for the half of this program that connects out.
///
/// The host has always asked: <c>ServerRole</c> fetches the portal's directory at start-up and hands the
/// address it names to the engine. The controller never did, and with the settings everybody has out of
/// the box -- no server configured, "use the published directory" on -- that left it with an empty string
/// to dial. An empty host is not an error anywhere along the way: ParseHostPort returns it unchanged, and
/// Dns.GetHostAddressesAsync("") answers with this machine's own addresses, so the connection attempt went
/// to localhost and came back "connection refused".
///
/// Which is why this was mistaken for a server being down for so long. The same machine registered as a
/// host perfectly, against the same server, in the same process -- and could not reach it to place a call.
///
/// Cached, because a connection attempt is not the moment to make a web request, and because the device
/// list asks the same question every time it refreshes presence. Failure is cached only briefly: a portal
/// that was unreachable while the laptop was waking up should not keep this program offline for an hour.
/// </summary>
public sealed class NetworkDirectoryCache(
    Func<string, CancellationToken, Task<NetworkDirectory>>? fetch = null,
    TimeProvider? time = null)
{
    private static readonly TimeSpan GoodFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(20);

    private readonly Func<string, CancellationToken, Task<NetworkDirectory>> _fetch = fetch ?? FetchAsync;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NetworkDirectory _directory = NetworkDirectory.None;
    private DateTimeOffset _until;

    /// <summary>What was last fetched, without going and looking. <see cref="NetworkDirectory.None"/> until something has.</summary>
    public NetworkDirectory Last => _directory;

    /// <summary>
    /// The directory, fetching it if what is held has expired.
    ///
    /// Never throws. A portal that is unreachable, slow or answering nonsense means the same thing to
    /// every caller -- there is no directory, carry on with whatever is configured -- and this is called
    /// from the path that opens a session window.
    /// </summary>
    public async Task<NetworkDirectory> GetAsync(string portal, CancellationToken ct = default)
    {
        if (_time.GetUtcNow() < _until)
        {
            return _directory;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Somebody else may have fetched it while this was waiting for the gate.
            if (_time.GetUtcNow() < _until)
            {
                return _directory;
            }

            NetworkDirectory fetched;
            try
            {
                fetched = await _fetch(portal, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                fetched = NetworkDirectory.None;
            }

            _directory = fetched;
            _until = _time.GetUtcNow() + (fetched.Rendezvous.Length > 0 ? GoodFor : RetryAfter);
            return _directory;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<NetworkDirectory> FetchAsync(string portal, CancellationToken ct)
    {
        using var client = new NetworkDirectoryClient();
        return await client.FetchAsync(portal, ct).ConfigureAwait(false);
    }
}
