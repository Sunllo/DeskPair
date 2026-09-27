using System.Buffers;
using System.Net.Sockets;

namespace DeskPair.Relay.Core;

/// <summary>Splices two paired sockets byte-for-byte in both directions until either side closes or the session idles out.</summary>
internal static class RelaySession
{
    private const int BufferBytes = 64 * 1024;

    public static async Task RunAsync(
        string uuid, string idA, Socket a, string idB, Socket b,
        RelayOptions options, RelayStats stats, RelayThrottle throttle, TimeProvider time, ILogger log, CancellationToken ct)
    {
        long started = time.GetTimestamp();
        long lastActivity = started;
        long bytesAtoB = 0;
        long bytesBtoA = 0;
        string closeReason = "peer closed";

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stats.SessionStarted();
        try
        {
            // Counted twice on purpose: once for this session's end-of-life log line, and once into the
            // relay's rolling throughput window, which is what tells a rendezvous whether to send the next
            // connection here or somewhere else. The second is only useful while the session is running.
            Task copyAb = CopyAsync(a, b, throttle, v => { Interlocked.Add(ref bytesAtoB, v); stats.Forwarded(v); }, () => Volatile.Write(ref lastActivity, time.GetTimestamp()), sessionCts.Token);
            Task copyBa = CopyAsync(b, a, throttle, v => { Interlocked.Add(ref bytesBtoA, v); stats.Forwarded(v); }, () => Volatile.Write(ref lastActivity, time.GetTimestamp()), sessionCts.Token);
            Task<string> watchdog = WatchdogAsync();

            // The session ends as soon as either direction closes (a peer disconnecting tears down both)
            // or the watchdog fires; a lingering half-open direction would otherwise hold the session open.
            Task first = await Task.WhenAny(copyAb, copyBa, watchdog).ConfigureAwait(false);
            if (first == watchdog)
            {
                closeReason = await watchdog.ConfigureAwait(false);
            }

            await sessionCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Swallow(copyAb), Swallow(copyBa), Swallow(watchdog)).ConfigureAwait(false);

            async Task<string> WatchdogAsync()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
                while (await timer.WaitForNextTickAsync(sessionCts.Token).ConfigureAwait(false))
                {
                    if (time.GetElapsedTime(Volatile.Read(ref lastActivity)) > options.IdleTimeout)
                    {
                        return "idle timeout";
                    }

                    if (options.MaxSessionDuration > TimeSpan.Zero && time.GetElapsedTime(started) > options.MaxSessionDuration)
                    {
                        return "max duration";
                    }
                }

                return "cancelled";
            }
        }
        finally
        {
            stats.SessionEnded();
            stats.AddBytes(bytesAtoB + bytesBtoA);
            SafeClose(a);
            SafeClose(b);
            log.LogInformation(
                "Relay session {Uuid} ended: {IdA} <-> {IdB}, {Duration:F1}s, {AtoB} B / {BtoA} B, reason: {Reason}",
                Short(uuid), idA, idB, time.GetElapsedTime(started).TotalSeconds, bytesAtoB, bytesBtoA, closeReason);
        }
    }

    private static async Task CopyAsync(Socket src, Socket dst, RelayThrottle throttle, Action<long> count, Action touch, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            while (true)
            {
                int n = await src.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    try
                    {
                        dst.Shutdown(SocketShutdown.Send);
                    }
                    catch (SocketException)
                    {
                    }

                    return;
                }

                // The relay's ceiling, paid for before the bytes go out. Waiting here rather than after
                // means the sender's own congestion control sees the delay as the link being full, which
                // it is.
                await throttle.WaitAsync(n, ct).ConfigureAwait(false);
                int sent = 0;
                while (sent < n)
                {
                    sent += await dst.SendAsync(buffer.AsMemory(sent, n - sent), SocketFlags.None, ct).ConfigureAwait(false);
                }

                count(n);
                touch();
            }
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Either side went away; the session ends and both sockets are closed by the caller.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task Swallow(Task t)
    {
        try
        {
            await t.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    internal static void SafeClose(Socket s)
    {
        try
        {
            s.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }

        s.Dispose();
    }

    internal static string Short(string uuid) => uuid.Length <= 8 ? uuid : uuid[..8];
}
