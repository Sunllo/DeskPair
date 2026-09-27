using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DeskPair.Core.Session.Host;

/// <summary>One connection, as the journal remembers it.</summary>
public sealed record ConnectionRecord
{
    /// <summary>Unique to this connection on this machine, so a row can be merged and uploaded exactly once.</summary>
    public string Id { get; init => field = value ?? string.Empty; } = string.Empty;

    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Null while the connection is still open, and for one the host never saw end.</summary>
    public DateTimeOffset? EndedUtc { get; init; }

    public string PeerId { get; init => field = value ?? string.Empty; } = string.Empty;
    public string PeerName { get; init => field = value ?? string.Empty; } = string.Empty;
    public string PeerPlatform { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Where it came from, as a person would read it; empty when nothing said.</summary>
    public string Address { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>The address came from the rendezvous server rather than from the socket (a relayed connection).</summary>
    public bool AddressReported { get; init; }

    /// <summary>"DirectTcp", "Lan", "PunchedTcp" or "Relay".</summary>
    public string Transport { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>"remote", "file-transfer" or "terminal".</summary>
    public string Kind { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>"temporary", "permanent", "approval" or "none".</summary>
    public string Authenticated { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>What was allowed at the moment the connection was authorised.</summary>
    public IReadOnlyList<string> Granted { get; init => field = value ?? []; } = [];

    /// <summary>Why it ended; empty while it is open.</summary>
    public string Reason { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>How many terminals this connection opened, and as whom. Filled once terminals exist.</summary>
    public int TerminalOpens { get; init; }

    public string TerminalIdentity { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Null while it is open; a connection the host never saw end has no duration either.</summary>
    public TimeSpan? Duration => EndedUtc is { } ended ? ended - StartedUtc : null;

    /// <summary>A row the host never saw end: it was open when the engine stopped, or it is open now.</summary>
    public bool Unfinished => EndedUtc is null;
}

/// <summary>
/// Who connected to this computer, kept on this computer.
///
/// Append-only, one JSON object per line, two lines per connection: one when it is authorised and one when
/// it ends. Reading folds them back together. Two lines rather than one because a host that is killed
/// mid-session must still leave a record that somebody was connected -- an audit whose first act is to wait
/// until the end is an audit that loses exactly the connections worth asking about.
///
/// What is deliberately not here: nothing that was typed, copied, or transferred. The journal answers "who
/// was here, when, from where, and what were they allowed to do", and answering more than that would make it
/// a surveillance file rather than a security record.
/// </summary>
public sealed class ConnectionJournal
{
    /// <summary>Lines kept in the file. Two per connection, so this is roughly the last 5,000 of them.</summary>
    public const int DefaultKeepLines = 10_000;

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly int _keepLines;
    private readonly object _gate = new();
    private int _lines = -1; // counted lazily on the first write

    public ConnectionJournal(string path, TimeProvider time, ILogger log, int keepLines = DefaultKeepLines)
    {
        _path = path;
        _time = time;
        _log = log;
        _keepLines = Math.Max(2, keepLines);
    }

    /// <summary>
    /// Records every authorised connection of this runtime and every one that ends. One call in the engine,
    /// and the journal keeps its own bookkeeping -- which is also what lets a test drive it through a real
    /// session rather than by calling <see cref="Started"/> by hand.
    /// </summary>
    public void Attach(HostRuntime runtime)
    {
        runtime.SessionAuthorized += (session, _) =>
        {
            HostSessionContext ctx = session.Context;
            string id = Started(new ConnectionRecord
            {
                StartedUtc = _time.GetUtcNow(),
                PeerId = ctx.Peer.Id,
                PeerName = ctx.Peer.Name,
                PeerPlatform = ctx.Peer.Platform,
                Address = session.AllowlistAddress?.ToString() ?? string.Empty,
                AddressReported = session.TransportKind == Transport.TransportKind.Relay,
                Transport = session.TransportKind.ToString(),
                Kind = ctx.ConnType switch
                {
                    Protocol.Rendezvous.ConnType.ConnFileTransfer => "file-transfer",
                    Protocol.Rendezvous.ConnType.ConnTerminal => "terminal",
                    _ => "remote",
                },
                Authenticated = ctx.AuthenticatedWith switch
                {
                    PasswordMatchKind.Temporary => "temporary",
                    PasswordMatchKind.Permanent => "permanent",
                    PasswordMatchKind.Approval => "approval",
                    _ => "none",
                },
                Granted = [.. ctx.Permissions.Granted.Select(p => p.ToString())],
            });

            lock (_open)
            {
                _open[ctx.ConnectionId] = id;
            }

            return Task.CompletedTask;
        };

        runtime.SessionEnded += (session, reason) =>
        {
            string? id;
            lock (_open)
            {
                if (!_open.Remove(session.Context.ConnectionId, out id))
                {
                    // Never authorised -- a refused password, a scope violation before login. The journal is
                    // about who got in, and the login failures already have their own log lines.
                    return;
                }
            }

            Ended(id, reason, session.Context.TerminalOpens, session.Context.TerminalIdentity);
        };
    }

    private readonly Dictionary<int, string> _open = [];

    /// <summary>A connection was authorised. Returns the id its closing line must carry.</summary>
    public string Started(ConnectionRecord record)
    {
        string id = string.IsNullOrEmpty(record.Id) ? Guid.NewGuid().ToString("N") : record.Id;
        Append(new JournalLine { Kind = "start", Record = record with { Id = id, StartedUtc = record.StartedUtc == default ? _time.GetUtcNow() : record.StartedUtc } });
        return id;
    }

    /// <summary>A connection ended. Only the fields that are known at the end are written.</summary>
    public void Ended(string id, string reason, int terminalOpens = 0, string terminalIdentity = "")
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        Append(new JournalLine
        {
            Kind = "end",
            Record = new ConnectionRecord
            {
                Id = id,
                EndedUtc = _time.GetUtcNow(),
                Reason = reason,
                TerminalOpens = terminalOpens,
                TerminalIdentity = terminalIdentity,
            },
        });
    }

    /// <summary>
    /// The connections the file remembers, newest first. Lines that cannot be read are skipped rather than
    /// failing the whole journal: a truncated last line after a power cut must not hide everything before it.
    /// </summary>
    public IReadOnlyList<ConnectionRecord> Read(int limit = 500)
    {
        lock (_gate)
        {
            var byId = new Dictionary<string, ConnectionRecord>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (string line in ReadLines())
            {
                JournalLine? parsed = Parse(line);
                if (parsed?.Record is not { } record || record.Id.Length == 0)
                {
                    continue;
                }

                if (!byId.TryGetValue(record.Id, out ConnectionRecord? existing))
                {
                    byId[record.Id] = record;
                    order.Add(record.Id);
                    continue;
                }

                byId[record.Id] = parsed.Kind == "end"
                    ? existing with
                    {
                        EndedUtc = record.EndedUtc,
                        Reason = record.Reason,
                        TerminalOpens = record.TerminalOpens,
                        TerminalIdentity = record.TerminalIdentity,
                    }
                    // A start after an end can only mean a reused id, which nothing here generates; take the
                    // newer one whole rather than inventing a merge nobody can reason about.
                    : record;
            }

            var newestFirst = new List<ConnectionRecord>(Math.Min(order.Count, limit));
            for (int i = order.Count - 1; i >= 0 && newestFirst.Count < limit; i--)
            {
                newestFirst.Add(byId[order[i]]);
            }

            return newestFirst;
        }
    }

    /// <summary>
    /// Forgets everything, and records that it was forgotten. A journal that can be emptied without trace is
    /// not a journal, so the clearing is the one thing clearing does not remove.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(e, "Could not clear the connection journal");
                return;
            }

            _lines = 0;
        }

        DateTimeOffset now = _time.GetUtcNow();
        Append(new JournalLine
        {
            Kind = "start",
            Record = new ConnectionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                StartedUtc = now,
                EndedUtc = now,
                Kind = "cleared",
                Reason = "the record was cleared on this computer",
            },
        });
    }

    private void Append(JournalLine line)
    {
        lock (_gate)
        {
            try
            {
                if (_lines < 0)
                {
                    _lines = CountLines();
                }

                if (_lines >= _keepLines)
                {
                    Compact();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, JsonSerializer.Serialize(line, ConnectionJournalJson.Default.JournalLine) + Environment.NewLine);
                _lines++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A journal that cannot be written must not take the session down with it.
                _log.LogWarning(e, "Could not write to the connection journal");
            }
        }
    }

    /// <summary>Keeps the newest half, so compaction happens once every few thousand connections rather than on every write.</summary>
    private void Compact()
    {
        string[] lines = ReadLines();
        int keep = _keepLines / 2;
        string[] newest = lines.Length <= keep ? lines : lines[^keep..];
        string temp = _path + ".tmp";
        File.WriteAllLines(temp, newest);
        File.Move(temp, _path, overwrite: true);
        _lines = newest.Length;
    }

    private string[] ReadLines()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllLines(_path) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Could not read the connection journal");
            return [];
        }
    }

    private int CountLines() => ReadLines().Length;

    private static JournalLine? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line, ConnectionJournalJson.Default.JournalLine);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed record JournalLine
    {
        /// <summary>"start" or "end".</summary>
        public string Kind { get; init => field = value ?? string.Empty; } = string.Empty;

        public ConnectionRecord? Record { get; init; }
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConnectionJournal.JournalLine))]
internal sealed partial class ConnectionJournalJson : JsonSerializerContext
{
}
