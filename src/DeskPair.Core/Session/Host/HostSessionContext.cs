using System.Net;
using DeskPair.Core.Services;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Session.Host;

public enum HostSessionState
{
    Handshake,
    Authenticating,
    AwaitingApproval,
    Authorized,
    Closing,
    Closed,
}

public sealed record PeerDescriptor(string Id, string Name, string Platform, string Version, EndPoint? RemoteEndPoint);

/// <summary>Per-connection state shared by the host session and its handlers; also the session's face toward publisher services.</summary>
public sealed class HostSessionContext : IServiceSubscriber
{
    private readonly Func<Message, MessagePriority, CancellationToken, ValueTask> _send;
    private readonly Func<Message, bool> _sendVideo;
    private readonly Func<string, Task> _close;

    internal HostSessionContext(int connectionId, HostRuntime runtime, Func<Message, MessagePriority, CancellationToken, ValueTask> send, Func<Message, bool> sendVideo, Func<string, Task> close)
    {
        ConnectionId = connectionId;
        Runtime = runtime;
        Permissions = new PermissionSet(runtime.Policy);
        _send = send;
        _sendVideo = sendVideo;
        _close = close;
    }

    ValueTask IServiceSubscriber.PublishAsync(Message message, MessagePriority priority, CancellationToken ct) => _send(message, priority, ct);

    bool IServiceSubscriber.TryPublishVideo(Message frame) => _sendVideo(frame);

    bool IServiceSubscriber.SupportsLosslessTiles => Options.SupportedDecoding?.Tiles == true;

    Platform.Abstractions.Codec.SupportedCodecs IServiceSubscriber.DecodableCodecs =>
        Services.CodecWire.Decodable(Options.SupportedDecoding);

    // VC_VP8 is the proto's zero value, so it is also what an unset "prefer" field arrives as. Nothing in
    // this codebase encodes or decodes VP8, so reading it as "no preference" costs nothing and saves adding
    // a field that would only ever say the same thing.
    Platform.Abstractions.Codec.VideoCodec? IServiceSubscriber.PreferredCodec =>
        Options.SupportedDecoding is { Prefer: not Protocol.Messages.VideoCodec.VcVp8 } d
            ? Services.VideoService.FromWire(d.Prefer)
            : null;

    public int ConnectionId { get; }

    public HostRuntime Runtime { get; }

    public HostSessionState State { get; internal set; } = HostSessionState.Handshake;

    public ConnType ConnType { get; internal set; }

    public PeerDescriptor Peer { get; internal set; } = new(string.Empty, string.Empty, string.Empty, string.Empty, null);

    public PermissionSet Permissions { get; }

    public SessionOptions Options { get; internal set; } = new();

    /// <summary>Keyframe requests received from this viewer (diagnostics).</summary>
    public long RefreshRequests { get; internal set; }

    /// <summary>What the controller declared at login for media outside the TCP stream.</summary>
    public MediaCapabilities? PeerMedia { get; internal set; }

    /// <summary>The verified UDP media path, once the channel is up; null while video rides the TCP session.</summary>
    public Transport.Udp.MediaPath? MediaPath { get; internal set; }

    /// <summary>Set by the session to receive the controller's media channel answer/ready/close messages.</summary>
    internal Func<Misc, ValueTask>? MediaMisc { get; set; }

    public int CurrentDisplay { get; internal set; }

    public PasswordMatchKind AuthenticatedWith { get; internal set; }

    /// <summary>
    /// Whether this session has held the terminal permission at any point. A viewer that never had it and
    /// sends a terminal action is a violation; one that had it and lost it is still sending the acks and
    /// keystrokes that were in flight when the owner switched it off, and those are dropped, not fatal.
    /// </summary>
    public bool TerminalWasGranted { get; internal set; }

    /// <summary>Shells this session opened, for the connection record; written by the terminal module.</summary>
    public int TerminalOpens { get; internal set; }

    /// <summary>The account the last shell ran as, for the connection record.</summary>
    public string TerminalIdentity { get; internal set; } = string.Empty;

    public ValueTask SendAsync(Message message, MessagePriority priority = MessagePriority.Control, CancellationToken ct = default) =>
        _send(message, priority, ct);

    public Task CloseAsync(string reason) => _close(reason);

    public ConnectionSummary Summary => new(ConnectionId, Peer.Id, Peer.Name, Peer.Platform, Peer.RemoteEndPoint, ConnType);
}

public enum PasswordMatchKind
{
    None,
    Temporary,
    Permanent,
    Approval,
}
