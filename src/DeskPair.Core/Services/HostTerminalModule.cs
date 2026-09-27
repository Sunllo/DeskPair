using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Session;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Terminal;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Services;

/// <summary>
/// Gives every terminal connection its own <see cref="TerminalSession"/> and routes terminal messages
/// to it. Permission is enforced by <see cref="SessionScope"/> before dispatch -- a <c>TerminalAction</c>
/// only reaches here on a <see cref="ConnType.ConnTerminal"/> session that holds
/// <see cref="Permission.PermTerminal"/> -- and this module handles the two ways that stops being true:
/// the permission withdrawn mid-session, and the session ending. Both kill every shell, because a shell
/// with nobody attached is the one outcome worse than no shell.
///
/// There is deliberately no way in from IPC: the app can watch and withdraw, never open. The one path to
/// <see cref="ITerminalHost.StartAsync"/> is a viewer's own request over its authorized session.
/// </summary>
public sealed class HostTerminalModule : ISessionHandler<HostSessionContext>, IAsyncDisposable
{
    private readonly ITerminalHost _host;
    private readonly ILoggerFactory _logs;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<int, TerminalSession> _sessions = new();

    public HostTerminalModule(ITerminalHost host, ILoggerFactory logs, TimeProvider? time = null)
    {
        _host = host;
        _logs = logs;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whose shell a viewer gets; from the host's configuration, applied to the next terminal opened.</summary>
    public TerminalRunAs RunAs { get; set; } = TerminalRunAs.Highest;

    /// <summary>The account a shell would run as right now, for the approval card.</summary>
    public string DescribeIdentity() => _host.IsAvailable ? _host.DescribeIdentity(RunAs) : string.Empty;

    public IEnumerable<Message.UnionOneofCase> Handles => [Message.UnionOneofCase.TerminalAction];

    public void Attach(HostRuntime runtime)
    {
        runtime.SessionAuthorized += (session, cancel) =>
        {
            HostSessionContext ctx = session.Context;
            if (ctx.ConnType != ConnType.ConnTerminal)
            {
                return Task.CompletedTask;
            }

            int id = ctx.ConnectionId;
            var terminals = new TerminalSession(_host, RunAs, ctx.SendAsync, _logs.CreateLogger($"DeskPair.Core.Terminal[{id}]"), _time);
            terminals.Opened += identity =>
            {
                ctx.TerminalOpens = terminals.Opens;
                ctx.TerminalIdentity = identity;
            };
            ctx.TerminalWasGranted = ctx.Permissions.Has(Permission.PermTerminal);
            ctx.Permissions.Changed += (permission, enabled) =>
            {
                if (permission != Permission.PermTerminal)
                {
                    return;
                }

                if (enabled)
                {
                    ctx.TerminalWasGranted = true;
                }
                else
                {
                    _ = terminals.CloseAllAsync("terminal permission was withdrawn");
                }
            };
            _sessions[id] = terminals;
            return Task.CompletedTask;
        };
        runtime.SessionClosing += async session =>
        {
            if (_sessions.TryRemove(session.Context.ConnectionId, out TerminalSession? terminals))
            {
                await terminals.DisposeAsync().ConfigureAwait(false);
            }
        };
    }

    public TerminalSession? GetSession(int connectionId) => _sessions.GetValueOrDefault(connectionId);

    public async ValueTask HandleAsync(HostSessionContext context, Message message, CancellationToken ct)
    {
        if (message.UnionCase != Message.UnionOneofCase.TerminalAction || !_sessions.TryGetValue(context.ConnectionId, out TerminalSession? terminals))
        {
            return;
        }

        // The scope let this through only because the permission was held once. Without it now, nothing
        // reaches a shell: keystrokes and acks still in flight are dropped, and a new shell is refused in words.
        if (!context.Permissions.Has(Permission.PermTerminal))
        {
            if (message.TerminalAction.UnionCase == TerminalAction.UnionOneofCase.Open)
            {
                await context.SendAsync(new Message { TerminalResponse = new TerminalResponse { Error = new TerminalError { Id = message.TerminalAction.Open.Id, Message = "Terminal permission was withdrawn on this computer." } } }, MessagePriority.Control, ct).ConfigureAwait(false);
            }

            return;
        }

        await terminals.HandleAsync(message.TerminalAction, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (TerminalSession s in _sessions.Values)
        {
            await s.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }
}
