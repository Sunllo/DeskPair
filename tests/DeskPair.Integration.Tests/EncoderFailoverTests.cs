using DeskPair.Core.Services;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;

namespace DeskPair.Integration.Tests;

/// <summary>
/// A vendor encoder that stops working mid-session used to end the video: the exception came out of the
/// capture loop, the publisher guard swallowed it, and the viewer sat on a connected session whose picture
/// had stopped. These check that the host drops that encoder and comes back on the next one instead.
/// </summary>
public class EncoderFailoverTests
{
    private static async Task<(ControllerSession Session, TestCallbacks Cb)> ConnectAsync(Testbed bed, HostRuntime host, string password)
    {
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, cb);
    }

    [Fact]
    public async Task A_driver_that_throws_is_dropped_and_the_next_encoder_takes_over()
    {
        var encoders = new FakeVideoEncoderFactory
        {
            Names = ["Broken vendor encoder", "Working software encoder"],
            Breaks = new Dictionary<string, EncoderFault> { ["Broken vendor encoder"] = EncoderFault.Throws },
        };
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, encoders: encoders);

        (_, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 10, "video after the failover", 20_000);
        encoders.LastCreated.ShouldBe("Working software encoder");
    }

    /// <summary>
    /// The AMD failure mode: the encoder keeps saying yes with the same short packet, nothing reports an
    /// error, and the viewer's picture stops. Only the shape of the output gives it away.
    /// </summary>
    [Fact]
    public async Task An_encoder_that_freezes_without_saying_so_is_dropped_too()
    {
        var encoders = new FakeVideoEncoderFactory
        {
            Names = ["Silently corrupt encoder", "Working software encoder"],
            Breaks = new Dictionary<string, EncoderFault> { ["Silently corrupt encoder"] = EncoderFault.SilentlyFreezes },
        };
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, encoders: encoders);

        (_, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await Testbed.WaitUntilAsync(() => encoders.LastCreated == "Working software encoder", "the failover", 30_000);
        int before = cb.VideoFrames;
        await Testbed.WaitUntilAsync(() => cb.VideoFrames > before + 5, "the picture moving again", 20_000);
    }

    /// <summary>
    /// When every encoder has failed there is nothing left to fall to, and the viewer is told rather than
    /// left watching a session that will never produce another frame.
    /// </summary>
    [Fact]
    public async Task When_nothing_is_left_the_viewer_is_told()
    {
        var encoders = new FakeVideoEncoderFactory
        {
            Names = ["Only encoder"],
            Breaks = new Dictionary<string, EncoderFault> { ["Only encoder"] = EncoderFault.Throws },
        };
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, encoders: encoders);

        (_, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await Testbed.WaitUntilAsync(() => cb.CloseReason is not null, "a reason for the viewer", 30_000);
        cb.CloseReason.ShouldContain("encoder");
    }
}
