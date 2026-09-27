using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Session.Host.Handlers;

/// <summary>Routes the controller's UDP media channel answer/ready/close to the session that made the offer.</summary>
public sealed class MediaChannelHandler : ISessionHandler<HostSessionContext>
{
    public IEnumerable<Misc.UnionOneofCase> HandlesMisc =>
        [Misc.UnionOneofCase.MediaAnswer, Misc.UnionOneofCase.MediaReady, Misc.UnionOneofCase.MediaClose];

    public ValueTask HandleAsync(HostSessionContext context, Message message, CancellationToken ct) =>
        context.MediaMisc?.Invoke(message.Misc) ?? ValueTask.CompletedTask;
}
