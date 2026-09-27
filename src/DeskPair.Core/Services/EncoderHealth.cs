using DeskPair.Platform.Abstractions.Codec;

namespace DeskPair.Core.Services;

/// <summary>
/// Thrown out of the capture loop when the encoder in use has to be abandoned. Carries the descriptor so the
/// caller can exclude that specific encoder rather than the whole codec: a machine usually has several, and
/// the one below the broken one is often fine.
/// </summary>
public sealed class EncoderUnusableException(EncoderDescriptor encoder, string reason, Exception? inner = null)
    : Exception($"{encoder} is unusable: {reason}", inner)
{
    public EncoderDescriptor Encoder { get; } = encoder;

    public string Reason { get; } = reason;
}

/// <summary>
/// Watches one encoder and decides when to stop trusting it. Two failure modes, both learned from RustDesk's
/// hardware-encoding history rather than invented here:
///
/// A driver that throws. Vendor encoders fail at run time for reasons that have nothing to do with the frame
/// being submitted — a driver update mid-session, a GPU reset, an exhausted encode session limit. One throw
/// is worth retrying, because the next frame often works; a run of them is the encoder telling us it is gone.
///
/// A driver that lies. AMD's encoder has been seen to keep returning success with a short, byte-identical
/// packet while the picture on the wire stops moving. Nothing reports an error; the viewer simply sees a
/// frozen screen. The only signal is the output itself, so the output is what gets checked — but only for
/// frames that were actually new, because re-submitting a still picture to a hardware encoder legitimately
/// produces tiny repeated packets, and calling that corruption would break every idle session.
/// </summary>
public sealed class EncoderHealth
{
    /// <summary>Consecutive throws before the encoder is abandoned. One is noise; three is a pattern.</summary>
    public const int FailuresBeforeSwitch = 3;

    /// <summary>
    /// Consecutive identical short packets on changed pictures before the encoder is called corrupt. Held
    /// high deliberately: a false positive restarts a working stream, so this must not fire on a quiet one.
    /// </summary>
    public const int RepeatsBeforeCorrupt = 30;

    /// <summary>A packet this small cannot carry a changed 1080p picture; it is a skip frame or nothing.</summary>
    public const int SuspiciousPacketBytes = 100;

    private byte[] _previous = [];
    private int _previousLength = -1;
    private int _failures;
    private int _repeats;

    /// <summary>Why the encoder was abandoned, once it has been.</summary>
    public string? Verdict { get; private set; }

    /// <summary>A frame encoded without throwing: the driver is answering, so the failure run is over.</summary>
    public void RecordSuccess() => _failures = 0;

    /// <summary>
    /// The encoder threw. Returns true when it has thrown often enough in a row to be abandoned; false means
    /// the caller should drop this frame and try the next one.
    /// </summary>
    public bool RecordFailure(Exception error)
    {
        if (++_failures < FailuresBeforeSwitch)
        {
            return false;
        }

        Verdict = $"{_failures} consecutive encode failures ({error.GetType().Name}: {error.Message})";
        return true;
    }

    /// <summary>
    /// A packet the encoder produced. <paramref name="pictureWasNew"/> is false when the same picture was
    /// re-submitted (hardware encoders want a steady feed), and such packets are not evidence of anything.
    /// Returns true when the output has looked frozen for long enough to call the encoder corrupt.
    /// </summary>
    public bool RecordPacket(ReadOnlySpan<byte> packet, bool pictureWasNew)
    {
        if (!pictureWasNew || packet.Length >= SuspiciousPacketBytes)
        {
            _repeats = 0;
            _previousLength = -1;
            return false;
        }

        if (_previousLength == packet.Length && packet.SequenceEqual(_previous.AsSpan(0, _previousLength)))
        {
            if (++_repeats < RepeatsBeforeCorrupt)
            {
                return false;
            }

            Verdict = $"{_repeats} identical {packet.Length}-byte packets while the picture kept changing";
            return true;
        }

        _repeats = 0;
        _previousLength = packet.Length;
        Remember(packet);
        return false;
    }

    private void Remember(ReadOnlySpan<byte> packet)
    {
        if (_previous.Length < packet.Length)
        {
            _previous = new byte[SuspiciousPacketBytes];
        }

        packet.CopyTo(_previous);
    }
}
