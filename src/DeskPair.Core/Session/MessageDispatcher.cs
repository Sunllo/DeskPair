using Microsoft.Extensions.Logging;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Session;

/// <summary>Handles one or more top-level message cases (and/or Misc sub-cases) for a session.</summary>
public interface ISessionHandler<in TContext>
{
    IEnumerable<Message.UnionOneofCase> Handles => [];

    IEnumerable<Misc.UnionOneofCase> HandlesMisc => [];

    ValueTask HandleAsync(TContext context, Message message, CancellationToken ct);
}

/// <summary>Routes messages to handlers by oneof case; unknown cases are logged and ignored.</summary>
public sealed class MessageDispatcher<TContext>
{
    private readonly Dictionary<Message.UnionOneofCase, ISessionHandler<TContext>> _top = new();
    private readonly Dictionary<Misc.UnionOneofCase, ISessionHandler<TContext>> _misc = new();
    private readonly ILogger _log;

    public MessageDispatcher(IEnumerable<ISessionHandler<TContext>> handlers, ILogger log)
    {
        _log = log;
        foreach (ISessionHandler<TContext> handler in handlers)
        {
            foreach (Message.UnionOneofCase c in handler.Handles)
            {
                if (!_top.TryAdd(c, handler))
                {
                    throw new InvalidOperationException($"Duplicate handler for {c}.");
                }
            }

            foreach (Misc.UnionOneofCase c in handler.HandlesMisc)
            {
                if (!_misc.TryAdd(c, handler))
                {
                    throw new InvalidOperationException($"Duplicate handler for Misc.{c}.");
                }
            }
        }
    }

    public ValueTask DispatchAsync(TContext context, Message message, CancellationToken ct)
    {
        if (message.UnionCase == Message.UnionOneofCase.Misc)
        {
            if (_misc.TryGetValue(message.Misc.UnionCase, out ISessionHandler<TContext>? miscHandler))
            {
                return miscHandler.HandleAsync(context, message, ct);
            }

            _log.LogDebug("No handler for Misc.{Case}", message.Misc.UnionCase);
            return ValueTask.CompletedTask;
        }

        if (_top.TryGetValue(message.UnionCase, out ISessionHandler<TContext>? handler))
        {
            return handler.HandleAsync(context, message, ct);
        }

        _log.LogDebug("No handler for {Case}", message.UnionCase);
        return ValueTask.CompletedTask;
    }
}
