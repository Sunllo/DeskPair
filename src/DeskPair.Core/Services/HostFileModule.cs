using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using DeskPair.Core.FileTransfer;
using DeskPair.Core.Session;
using DeskPair.Core.Session.Host;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Services;

/// <summary>
/// Gives every authorized session its own <see cref="FileTransferEngine"/> over the host file system and
/// routes file messages to it. File permission is enforced by <see cref="SessionScope"/> before dispatch.
/// </summary>
public sealed class HostFileModule : ISessionHandler<HostSessionContext>, IAsyncDisposable
{
    private readonly IFileSystem _fs;
    private readonly ILoggerFactory _logs;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<int, FileTransferEngine> _engines = new();

    public HostFileModule(IFileSystem fs, ILoggerFactory logs, TimeProvider? time = null)
    {
        _fs = fs;
        _logs = logs;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Optional allow-list for peer-accessible paths.</summary>
    public Func<string, bool>? IsPathAllowed { get; set; }

    /// <summary>Progress of every job on the host side, tagged with the connection id (for the connection manager UI).</summary>
    public event Action<int, TransferJobSnapshot>? Progress;

    public IEnumerable<Message.UnionOneofCase> Handles => [Message.UnionOneofCase.FileAction, Message.UnionOneofCase.FileResponse];

    public void Attach(HostRuntime runtime)
    {
        runtime.SessionAuthorized += (session, _) =>
        {
            int id = session.Context.ConnectionId;
            var engine = new FileTransferEngine(_fs, session.Context.SendAsync, _logs.CreateLogger($"DeskPair.Core.FileTransfer[{id}]"), time: _time, side: FileTransferSide.Host) { IsPathAllowed = IsPathAllowed };
            engine.Progress += s => Progress?.Invoke(id, s);
            _engines[id] = engine;
            return Task.CompletedTask;
        };
        runtime.SessionClosing += async session =>
        {
            if (_engines.TryRemove(session.Context.ConnectionId, out FileTransferEngine? engine))
            {
                await engine.DisposeAsync().ConfigureAwait(false);
            }
        };
    }

    public FileTransferEngine? GetEngine(int connectionId) => _engines.GetValueOrDefault(connectionId);

    public ValueTask HandleAsync(HostSessionContext context, Message message, CancellationToken ct) =>
        _engines.TryGetValue(context.ConnectionId, out FileTransferEngine? engine) ? engine.HandleAsync(message, ct) : ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (FileTransferEngine e in _engines.Values)
        {
            await e.DisposeAsync().ConfigureAwait(false);
        }

        _engines.Clear();
    }
}
