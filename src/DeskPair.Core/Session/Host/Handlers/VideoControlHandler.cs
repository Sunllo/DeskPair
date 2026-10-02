using DeskPair.Core.Services;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Session.Host.Handlers;

/// <summary>Keyframe requests, acknowledgements and display switching, forwarded to the media module.</summary>
public sealed class VideoControlHandler : ISessionHandler<HostSessionContext>
{
    private readonly HostMediaModule _media;

    public VideoControlHandler(HostMediaModule media)
    {
        _media = media;
    }

    public IEnumerable<Misc.UnionOneofCase> HandlesMisc =>
        [Misc.UnionOneofCase.RefreshVideo, Misc.UnionOneofCase.VideoAck, Misc.UnionOneofCase.SwitchDisplay, Misc.UnionOneofCase.DisplaySubscription, Misc.UnionOneofCase.DisplayResolution, Misc.UnionOneofCase.VirtualDisplayRequest, Misc.UnionOneofCase.ElevationRequest];

    public async ValueTask HandleAsync(HostSessionContext context, Message message, CancellationToken ct)
    {
        Misc misc = message.Misc;
        switch (misc.UnionCase)
        {
            case Misc.UnionOneofCase.RefreshVideo:
                context.RefreshRequests++;
                _media.RequestKeyFrame(context.ConnectionId, misc.RefreshVideo.Display, "viewer asked for a refresh");
                break;
            case Misc.UnionOneofCase.VideoAck:
                _media.Qos.FrameAcked(context.ConnectionId, misc.VideoAck.Display, misc.VideoAck.Seq);
                break;
            case Misc.UnionOneofCase.SwitchDisplay:
                await _media.SwitchDisplayAsync(context, misc.SwitchDisplay.Display, ct).ConfigureAwait(false);
                break;
            case Misc.UnionOneofCase.DisplaySubscription:
                await _media.SubscribeAsync(context, misc.DisplaySubscription, ct).ConfigureAwait(false);
                break;
            case Misc.UnionOneofCase.VirtualDisplayRequest:
                await _media.VirtualDisplayAsync(context, misc.VirtualDisplayRequest, ct).ConfigureAwait(false);
                break;
            case Misc.UnionOneofCase.DisplayResolution:
                await _media.SetResolutionAsync(context, misc.DisplayResolution, ct).ConfigureAwait(false);
                break;
            case Misc.UnionOneofCase.ElevationRequest:
                await _media.RequestElevationAsync(context, ct).ConfigureAwait(false);
                break;
        }
    }
}
