using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using DeskPair.Relay.Core;

namespace DeskPair.Relay.Net;

public sealed class RelayListener : BackgroundService
{
    private readonly RelayPairing _pairing;
    private readonly RelayOptions _options;
    private readonly ILogger<RelayListener> _log;
    private readonly TaskCompletionSource<int> _boundPort = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RelayListener(RelayPairing pairing, IOptions<RelayOptions> options, ILogger<RelayListener> log)
    {
        _pairing = pairing;
        _options = options.Value;
        _log = log;
    }

    /// <summary>Resolves to the actual port once the socket is bound (tests use port 0).</summary>
    public Task<int> BoundPort => _boundPort.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        listener.DualMode = true;
        try
        {
            listener.Bind(new IPEndPoint(IPAddress.IPv6Any, _options.Port));
            listener.Listen(512);
        }
        catch (SocketException e)
        {
            _boundPort.TrySetException(e);
            throw;
        }

        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        _boundPort.TrySetResult(port);
        _log.LogInformation("Relay listening on tcp/{Port}", port);

        while (!stoppingToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException e)
            {
                _log.LogWarning(e, "Accept failed");
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _pairing.HandleAsync(client, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogError(e, "Unhandled error in relay connection");
                    RelaySession.SafeClose(client);
                }
            }, stoppingToken);
        }
    }
}
