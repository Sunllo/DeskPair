using Microsoft.Extensions.Logging;
using DeskPair.Core.Portal;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Desktop.Services;

/// <summary>What one upload did, for the log and for a test to assert on.</summary>
public sealed record ConnectionUploadReport(bool Linked, int Sent, int Recorded, string? Problem)
{
    public static ConnectionUploadReport NotLinked { get; } = new(false, 0, 0, null);

    public static ConnectionUploadReport Off { get; } = new(false, 0, 0, null);
}

/// <summary>
/// Sends this computer's connection record to the account, so somebody with several machines can see who has
/// been on them from one page.
///
/// Only when an account is linked, which is the opt-in: a machine that has never signed in uploads nothing
/// and has nowhere to upload to. What goes is who connected, when, from where, and what they were allowed to
/// do -- never what they saw or did, which is the same promise the local journal makes and the reason the
/// wire shape is written out by hand rather than reusing the engine's record.
/// </summary>
public sealed class ConnectionHistoryUpload(AccountLink link, HostLink host, ILogger<ConnectionHistoryUpload> log)
{
    /// <summary>
    /// Rows to re-offer around the watermark. The portal merges on the host's own id, so sending a row twice
    /// costs nothing and is what closes a connection that was still open when it was last uploaded.
    /// </summary>
    public const int Overlap = 50;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The newest start time already sent, in unix milliseconds. Held here rather than in the settings file
    /// because losing it costs one redundant upload, and the alternative is a setting nobody can read.
    /// </summary>
    public long Watermark { get; set; }

    /// <summary>Which of the engine's rows are worth sending, given what has already gone.</summary>
    public static IReadOnlyList<ConnectionUpload> ToSend(IEnumerable<ConnectionHistoryEntry> newestFirst, long watermark)
    {
        var send = new List<ConnectionUpload>();
        int seen = 0;
        foreach (ConnectionHistoryEntry entry in newestFirst)
        {
            // Rows newer than the watermark are new. Below it, a few are re-offered: one that was still open
            // last time has an ending now, and only re-sending it records that.
            if (entry.StartedUtcMs <= watermark && ++seen > Overlap)
            {
                break;
            }

            if (entry.Kind == "cleared")
            {
                // Clearing is a fact about this machine's own file, not about a connection to it.
                continue;
            }

            send.Add(new ConnectionUpload(
                entry.Id,
                entry.StartedUtcMs,
                entry.EndedUtcMs,
                entry.PeerId,
                entry.PeerName,
                entry.PeerPlatform,
                entry.Address,
                entry.AddressReported,
                entry.Transport,
                entry.Kind,
                entry.Authenticated,
                [.. entry.Granted],
                entry.Reason,
                entry.TerminalOpens,
                entry.TerminalIdentity));
        }

        return send;
    }

    /// <summary>
    /// Runs one upload. Safe to call unlinked, offline, switched off or already running: each does nothing
    /// and says so, rather than throwing at a caller that is a timer.
    /// </summary>
    public async Task<ConnectionUploadReport> RunAsync(bool enabled, CancellationToken ct = default)
    {
        if (!enabled)
        {
            return ConnectionUploadReport.Off;
        }

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return ConnectionUploadReport.NotLinked with { Linked = true };
        }

        try
        {
            (string PortalUrl, string Token)? credential = await link.CredentialAsync(ct).ConfigureAwait(false);
            if (credential is null)
            {
                return ConnectionUploadReport.NotLinked;
            }

            IpcMessage reply = await host.RequestAsync(new IpcMessage
            {
                ConnectionHistoryRequest = new ConnectionHistoryRequest { Limit = 500 },
            }, ct).ConfigureAwait(false);

            IReadOnlyList<ConnectionUpload> send = ToSend(reply.ConnectionHistory?.Entries ?? [], Watermark);
            if (send.Count == 0)
            {
                return new ConnectionUploadReport(true, 0, 0, null);
            }

            using var client = new PortalClient(credential.Value.PortalUrl);
            int recorded = await client.UploadConnectionsAsync(credential.Value.Token, send, ct).ConfigureAwait(false);
            Watermark = Math.Max(Watermark, send.Max(e => e.StartedUtc));
            log.LogDebug("Uploaded {Sent} connection records, portal kept {Recorded}", send.Count, recorded);
            return new ConnectionUploadReport(true, send.Count, recorded, null);
        }
        catch (PortalException e)
        {
            // A laptop in a bag is the ordinary case, not an error.
            log.LogDebug(e, "Connection history was not uploaded");
            return new ConnectionUploadReport(true, 0, 0, e.Message);
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            log.LogDebug(e, "The engine could not be asked for the connection record");
            return new ConnectionUploadReport(true, 0, 0, e.Message);
        }
        finally
        {
            _gate.Release();
        }
    }
}
