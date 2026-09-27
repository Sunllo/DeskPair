using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Core.Portal;
using DeskPair.Desktop.Engine;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Runs <see cref="ConnectionHistoryUpload"/> on a timer. Shaped like <see cref="AddressBookSyncService"/>
/// and for the same reasons: best-effort, silent, and harmless when there is no account, no network or no
/// engine to ask.
/// </summary>
public sealed class ConnectionHistoryUploadService : IDisposable
{
    /// <summary>Often enough that a page opened later is current; rare enough to cost a sleeping laptop nothing.</summary>
    private static readonly TimeSpan Regularly = TimeSpan.FromMinutes(15);

    /// <summary>After start-up, so the engine has answered its first request before this asks for another.</summary>
    private static readonly TimeSpan FirstRun = TimeSpan.FromSeconds(20);

    private readonly ConnectionHistoryUpload _upload;
    private readonly Func<bool> _enabled;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private ITimer? _timer;

    public ConnectionHistoryUploadService(ConnectionHistoryUpload upload, Func<bool> enabled, TimeProvider? time = null)
    {
        _upload = upload;
        _enabled = enabled;
        _time = time ?? TimeProvider.System;
    }

    public static ConnectionHistoryUploadService? Create(HostLink host, ILoggerFactory logs, Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ISecretStore secrets = PlatformServices.SecretStoreFor(ServerRole.DefaultDataDir());

        // Read, never created -- the same rule the address book follows: until the engine has made this
        // machine an identity there is no machine for a record to belong to.
        if (PeerIdentityStore.LoadAsync(secrets, new NoMachineId()).GetAwaiter().GetResult() is not { } identity)
        {
            return null;
        }

        var upload = new ConnectionHistoryUpload(new AccountLink(identity, secrets), host, logs.CreateLogger<ConnectionHistoryUpload>());
        return new ConnectionHistoryUploadService(upload, enabled);
    }

    public void Start() => _timer = _time.CreateTimer(_ => _ = RunAsync(), null, FirstRun, Regularly);

    private async Task RunAsync()
    {
        try
        {
            await _upload.RunAsync(_enabled(), _stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _timer?.Dispose();
        _stopping.Dispose();
    }

    /// <summary>Only the signing key is wanted; the machine id belongs to the engine's registration.</summary>
    private sealed class NoMachineId : IMachineIdProvider
    {
        public byte[] GetStableMachineId() => [];
    }
}
