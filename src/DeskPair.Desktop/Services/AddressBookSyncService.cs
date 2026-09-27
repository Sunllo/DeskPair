using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Core.Portal;
using DeskPair.Desktop.Engine;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Decides when the address book syncs: at start-up, a moment after any local edit, and every so often.
///
/// A timer as well as a nudge, because the other half of a sync is what somebody else changed, and nothing
/// on this machine knows that happened. Fifteen minutes rather than seconds: a saved-computer list is not a
/// chat, and a poll that is cheap for one machine is not cheap for a portal with a few hundred.
///
/// Everything here is best-effort and silent. A failed sync is the normal state of a laptop in a bag, and
/// the list on screen keeps working either way — the whole point of syncing a file that was already local.
/// </summary>
public sealed class AddressBookSyncService : IDisposable
{
    /// <summary>Long enough that a burst of edits is one sync, short enough to feel like it happened at once.</summary>
    private static readonly TimeSpan AfterEdit = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan Regularly = TimeSpan.FromMinutes(15);

    private readonly AddressBookSync _sync;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private ITimer? _debounce;
    private ITimer? _periodic;

    public AddressBookSyncService(AddressBookSync sync, ILogger<AddressBookSyncService> log, TimeProvider? time = null)
    {
        _sync = sync;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a sync that brought something in, so a list on screen can reload itself.</summary>
    public event Action? Pulled;

    public void Start()
    {
        _periodic = _time.CreateTimer(_ => Run(), null, Regularly, Regularly);
        Nudge();
    }

    /// <summary>Something changed here. Syncs shortly, and collapses a burst of edits into one exchange.</summary>
    public void Nudge()
    {
        _debounce?.Dispose();
        _debounce = _time.CreateTimer(_ => Run(), null, AfterEdit, Timeout.InfiniteTimeSpan);
    }

    private void Run() => _ = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            SyncReport report = await _sync.SyncAsync(_stopping.Token).ConfigureAwait(false);
            if (report.Changed)
            {
                Pulled?.Invoke();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A timer callback that throws takes the process with it, and this one runs on a laptop that is
            // asleep, on a network that is not there, against a portal that may be being restarted.
            _log.LogDebug(e, "Address book sync failed");
        }
    }

    /// <summary>
    /// The sync for this installation, or null when this machine has no identity yet.
    ///
    /// The same store the engine uses, by path rather than by sharing an object: the engine may be a
    /// different process altogether when this copy adopted one that was already listening.
    ///
    /// Read, never created. This used to call LoadOrCreateAsync, which meant that on a machine without an
    /// identity the address-book sync and the engine raced on the way up and whichever got there first
    /// decided what the machine was. It was watched happening: on a Mac whose identity was being carried
    /// over from the product's previous name, this minted a fresh key in the same second the app opened,
    /// the carry-over then correctly declined to overwrite it, and the machine ended up with its old id
    /// and a key the rendezvous server had never signed. Every connection failed at the handshake.
    ///
    /// Null is the honest answer here anyway: an address book belongs to a machine, and until the engine
    /// has made one there is no machine for it to belong to.
    /// </summary>
    public static async Task<AddressBookSyncService?> CreateAsync(ILoggerFactory logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ISecretStore secrets = PlatformServices.SecretStoreFor(ServerRole.DefaultDataDir());
        if (await PeerIdentityStore.LoadAsync(secrets, new NoMachineId()).ConfigureAwait(false) is not { } identity)
        {
            return null;
        }

        var sync = new AddressBookSync(new AccountLink(identity, secrets), logs.CreateLogger<AddressBookSync>());
        return new AddressBookSyncService(sync, logs.CreateLogger<AddressBookSyncService>());
    }

    /// <summary>Only the signing key is wanted here; the machine id belongs to the engine's registration.</summary>
    private sealed class NoMachineId : IMachineIdProvider
    {
        public byte[] GetStableMachineId() => [];
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _debounce?.Dispose();
        _periodic?.Dispose();
        _stopping.Dispose();
    }
}
