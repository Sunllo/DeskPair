using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Session.Host.Handlers;

/// <summary>Chat text from the controller is forwarded to the local user through the approver bridge.</summary>
public sealed class ChatHandler : ISessionHandler<HostSessionContext>
{
    public IEnumerable<Misc.UnionOneofCase> HandlesMisc => [Misc.UnionOneofCase.Chat];

    public ValueTask HandleAsync(HostSessionContext context, Message message, CancellationToken ct)
    {
        context.Runtime.Approver.Notify(new HostSessionEvent(context.ConnectionId, HostEventKind.ChatReceived, message.Misc.Chat.Text));
        return ValueTask.CompletedTask;
    }
}

public sealed class CloseReasonHandler : ISessionHandler<HostSessionContext>
{
    public IEnumerable<Misc.UnionOneofCase> HandlesMisc => [Misc.UnionOneofCase.CloseReason];

    public async ValueTask HandleAsync(HostSessionContext context, Message message, CancellationToken ct) =>
        await context.CloseAsync($"peer: {message.Misc.CloseReason.Reason}").ConfigureAwait(false);
}

/// <summary>Echoes controller probes and records the round trip of our own probes.</summary>
public sealed class TestDelayHandler : ISessionHandler<HostSessionContext>
{
    private readonly TimeProvider _time;

    public TestDelayHandler(TimeProvider time)
    {
        _time = time;
    }

    public IEnumerable<Message.UnionOneofCase> Handles => [Message.UnionOneofCase.TestDelay];

    public async ValueTask HandleAsync(HostSessionContext context, Message message, CancellationToken ct)
    {
        TestDelay td = message.TestDelay;
        if (td.FromController)
        {
            await context.SendAsync(message, MessagePriority.Control, ct).ConfigureAwait(false);
            return;
        }

        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        context.Runtime.RecordRoundTrip(context.ConnectionId, TimeSpan.FromMilliseconds(Math.Max(0, now - td.TimeMs)));
    }
}

public sealed class OptionsHandler : ISessionHandler<HostSessionContext>
{
    public IEnumerable<Misc.UnionOneofCase> HandlesMisc => [Misc.UnionOneofCase.Options];

    public ValueTask HandleAsync(HostSessionContext context, Message message, CancellationToken ct)
    {
        SessionOptions incoming = message.Misc.Options;
        // Capabilities are declared once at login; a later options update (quality, cursor, audio) must not erase them,
        // or the lossless tile path silently switches off for the rest of the session.
        if (incoming.SupportedDecoding is null && context.Options.SupportedDecoding is not null)
        {
            incoming.SupportedDecoding = context.Options.SupportedDecoding.Clone();
        }

        context.Options = incoming;
        context.Permissions.ApplyOptions(context.Options);
        context.Runtime.OptionsUpdated?.Invoke(context.ConnectionId, context.Options);
        return ValueTask.CompletedTask;
    }
}
