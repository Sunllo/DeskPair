using DeskPair.Core.Services;

namespace DeskPair.Core.Tests;

/// <summary>
/// Both rules are thresholds, so both are worth pinning exactly: one frame too eager restarts a working
/// stream, one too patient leaves the viewer looking at a frozen picture.
/// </summary>
public class EncoderHealthTests
{
    private static Exception Died() => new InvalidOperationException("the driver went away");

    [Fact]
    public void One_failure_is_a_dropped_frame_and_three_is_a_broken_encoder()
    {
        var health = new EncoderHealth();

        health.RecordFailure(Died()).ShouldBeFalse();
        health.RecordFailure(Died()).ShouldBeFalse();
        health.Verdict.ShouldBeNull();
        health.RecordFailure(Died()).ShouldBeTrue();
        health.Verdict.ShouldNotBeNull();
    }

    /// <summary>A driver that stumbles and recovers is not a driver to abandon.</summary>
    [Fact]
    public void A_frame_that_encodes_clears_the_failure_run()
    {
        var health = new EncoderHealth();

        health.RecordFailure(Died()).ShouldBeFalse();
        health.RecordFailure(Died()).ShouldBeFalse();
        health.RecordSuccess();

        health.RecordFailure(Died()).ShouldBeFalse();
        health.RecordFailure(Died()).ShouldBeFalse();
        health.Verdict.ShouldBeNull();
    }

    [Fact]
    public void A_short_packet_repeated_while_the_picture_changes_is_corruption()
    {
        var health = new EncoderHealth();
        byte[] frozen = new byte[40];

        for (int i = 0; i < EncoderHealth.RepeatsBeforeCorrupt; i++)
        {
            health.RecordPacket(frozen, pictureWasNew: true).ShouldBeFalse($"only {i} repeats so far");
        }

        health.RecordPacket(frozen, pictureWasNew: true).ShouldBeTrue();
        health.Verdict.ShouldNotBeNull();
    }

    /// <summary>
    /// The case that makes a naive version of this rule unusable: a hardware encoder wants a steady feed, so
    /// an idle desk re-submits the same picture and gets back the same tiny packet, forever, correctly.
    /// </summary>
    [Fact]
    public void A_still_desktop_is_never_called_corrupt()
    {
        var health = new EncoderHealth();
        byte[] frozen = new byte[40];

        for (int i = 0; i < EncoderHealth.RepeatsBeforeCorrupt * 10; i++)
        {
            health.RecordPacket(frozen, pictureWasNew: false).ShouldBeFalse();
        }

        health.Verdict.ShouldBeNull();
    }

    [Fact]
    public void One_different_packet_clears_the_suspicion()
    {
        var health = new EncoderHealth();
        byte[] frozen = new byte[40];
        byte[] other = new byte[40];
        other[7] = 1;

        for (int i = 0; i < EncoderHealth.RepeatsBeforeCorrupt - 1; i++)
        {
            health.RecordPacket(frozen, pictureWasNew: true).ShouldBeFalse();
        }

        health.RecordPacket(other, pictureWasNew: true).ShouldBeFalse();

        for (int i = 0; i < EncoderHealth.RepeatsBeforeCorrupt - 1; i++)
        {
            health.RecordPacket(other, pictureWasNew: true).ShouldBeFalse();
        }

        health.Verdict.ShouldBeNull();
    }

    /// <summary>A packet big enough to be a real picture says nothing about corruption, however often it repeats.</summary>
    [Fact]
    public void A_full_size_packet_is_never_evidence()
    {
        var health = new EncoderHealth();
        byte[] real = new byte[EncoderHealth.SuspiciousPacketBytes];

        for (int i = 0; i < EncoderHealth.RepeatsBeforeCorrupt * 3; i++)
        {
            health.RecordPacket(real, pictureWasNew: true).ShouldBeFalse();
        }

        health.Verdict.ShouldBeNull();
    }
}
