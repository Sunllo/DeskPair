using DeskPair.Platform.Linux.Capture.Drm;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The records that cross between the daemon and the engine survive the trip unchanged, and a message of
/// one kind is never mistaken for another. Both ends are this code, but they run in different processes
/// as different users, and a field written at the wrong offset is a picture with the wrong pitch.
/// </summary>
public class DrmWireTests
{
    private static readonly DrmFrameInfo Sample = new()
    {
        Generation = 7,
        FbId = 113,
        CrtcId = 45,
        ConnectorId = 47,
        ConnectorType = 11,
        ConnectorTypeId = 2,
        Fourcc = 0x34325258,
        Modifier = 0x0100000000000002,
        PlaneCount = 1,
        Width = 1280,
        Height = 800,
        SrcX = 0,
        SrcY = 0,
        SrcWidth = 1280,
        SrcHeight = 800,
        Rotation = 1,
        Pitch = 5120,
        Offset = 0,
        Sequence = 123456789012,
        TimestampNs = 1_700_000_000_000_000_000,
    };

    /// <summary>
    /// A machine with no card answers every poll with this. It must read as "no scanout" to an engine
    /// that predates the flag, and as "and do not bother waking the display" to one that knows it.
    /// </summary>
    [Fact]
    public void No_display_hardware_is_a_no_scanout_that_says_so()
    {
        Span<byte> buffer = stackalloc byte[DrmWire.FrameSize];
        DrmWire.WriteNoScanout(buffer, generation: 0, crtcOff: false, noHardware: true).ShouldBe(8);

        DrmWire.TryReadNoScanout(buffer, out uint generation, out bool off, out bool none).ShouldBeTrue();
        generation.ShouldBe(0u);
        off.ShouldBeFalse();
        none.ShouldBeTrue();
        DrmWire.TryReadNoScanout(buffer, out _, out _).ShouldBeTrue("the older reading still accepts it");

        DrmWire.WriteNoScanout(buffer, generation: 3, crtcOff: true);
        DrmWire.TryReadNoScanout(buffer, out _, out _, out none).ShouldBeTrue();
        none.ShouldBeFalse("a display that is merely off is not a machine without one");
    }

    [Fact]
    public void An_agent_answer_round_trips_and_says_whether_a_socket_came_with_it()
    {
        Span<byte> buffer = stackalloc byte[DrmWire.FrameSize];
        DrmWire.WriteAgentPoll(buffer).ShouldBe(4);
        DrmWire.TryReadAgentPoll(buffer[..4]).ShouldBeTrue();
        DrmWire.TryReadPoll(buffer[..4], stackalloc uint[DrmWire.KnownFbSlots]).ShouldBeFalse("not a screen poll");

        int n = DrmWire.WriteAgent(buffer, 1000, "65", fdAttached: true);
        DrmWire.TryReadAgent(buffer[..n], out uint uid, out string session, out bool attached).ShouldBeTrue();
        (uid, session, attached).ShouldBe((1000u, "65", true));

        n = DrmWire.WriteAgent(buffer, 0, string.Empty, fdAttached: false);
        DrmWire.TryReadAgent(buffer[..n], out _, out _, out attached).ShouldBeTrue();
        attached.ShouldBeFalse("none waits");
    }

    [Fact]
    public void A_frame_round_trips_with_its_descriptor_flag()
    {
        Span<byte> buffer = stackalloc byte[DrmWire.FrameSize];
        DrmWire.WriteFrame(buffer, Sample, fdAttached: true).ShouldBe(DrmWire.FrameSize);

        DrmWire.TryReadFrame(buffer, out DrmFrameInfo frame, out bool fd).ShouldBeTrue();

        fd.ShouldBeTrue();
        frame.ShouldBe(Sample);
    }

    [Fact]
    public void A_poll_carries_the_descriptors_the_engine_already_holds()
    {
        Span<byte> buffer = stackalloc byte[DrmWire.PollSize];
        DrmWire.WritePoll(buffer, [112u, 113u]);

        Span<uint> known = stackalloc uint[DrmWire.KnownFbSlots];
        DrmWire.TryReadPoll(buffer, known).ShouldBeTrue();

        known.ToArray().ShouldBe([112u, 113u, 0u, 0u]);
    }

    [Fact]
    public void No_scanout_says_whether_the_display_is_off()
    {
        Span<byte> buffer = stackalloc byte[8];
        DrmWire.WriteNoScanout(buffer, generation: 3, crtcOff: true);

        DrmWire.TryReadNoScanout(buffer, out uint generation, out bool off).ShouldBeTrue();
        generation.ShouldBe(3u);
        off.ShouldBeTrue();
    }

    [Fact]
    public void An_error_carries_the_daemons_own_words()
    {
        Span<byte> buffer = stackalloc byte[256];
        int n = DrmWire.WriteError(buffer, "DRM-FORMAT: eDP-1 scanout is AR24 modifier I915_FORMAT_MOD_Y_TILED");

        DrmWire.TryReadError(buffer[..n], out string message).ShouldBeTrue();
        message.ShouldStartWith("DRM-FORMAT: eDP-1");
    }

    /// <summary>A message is read as what it is, and as nothing else.</summary>
    [Fact]
    public void One_kind_is_not_read_as_another()
    {
        Span<byte> buffer = stackalloc byte[DrmWire.FrameSize];
        DrmWire.WriteFrame(buffer, Sample, fdAttached: false);

        DrmWire.KindOf(buffer).ShouldBe(DrmWire.KindFrame);
        DrmWire.TryReadPoll(buffer, stackalloc uint[4]).ShouldBeFalse();
        DrmWire.TryReadNoScanout(buffer, out _, out _).ShouldBeFalse();
        DrmWire.TryReadError(buffer, out _).ShouldBeFalse();
    }

    /// <summary>A datagram from a build with a different first byte is refused, not misread.</summary>
    [Fact]
    public void Another_version_is_refused()
    {
        Span<byte> buffer = stackalloc byte[DrmWire.FrameSize];
        DrmWire.WriteFrame(buffer, Sample, fdAttached: false);
        buffer[0] = (byte)(DrmWire.Version + 1);

        DrmWire.TryReadFrame(buffer, out _, out _).ShouldBeFalse();
        DrmWire.KindOf(buffer).ShouldBe((byte)0);
    }

    [Fact]
    public void A_lock_request_and_its_answer_are_four_bytes_each_and_nothing_else_reads_as_them()
    {
        Span<byte> buffer = stackalloc byte[DrmWire.FrameSize];
        DrmWire.WriteLock(buffer).ShouldBe(4);
        DrmWire.KindOf(buffer).ShouldBe(DrmWire.KindLock);
        DrmWire.TryReadLock(buffer[..4]).ShouldBeTrue();
        DrmWire.TryReadLocked(buffer[..4]).ShouldBeFalse();
        DrmWire.TryReadPoll(buffer[..4], stackalloc uint[4]).ShouldBeFalse();

        DrmWire.WriteLocked(buffer).ShouldBe(4);
        DrmWire.KindOf(buffer).ShouldBe(DrmWire.KindLocked);
        DrmWire.TryReadLocked(buffer[..4]).ShouldBeTrue();
        DrmWire.TryReadLock(buffer[..4]).ShouldBeFalse();

        int n = DrmWire.WriteError(buffer, "nobody is signed in on the seat, so there is nothing to lock");
        DrmWire.TryReadLocked(buffer[..n]).ShouldBeFalse();
        DrmWire.TryReadError(buffer[..n], out string why).ShouldBeTrue();
        why.ShouldContain("nothing to lock");
    }
}
