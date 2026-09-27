using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;

namespace DeskPair.Integration.Tests;

/// <summary>
/// Hardware encoders on Windows are asynchronous: a frame that takes longer than the host waits for it comes out
/// later. With lossless tiles a still screen submits nothing more, so "later" has to be the host asking for it --
/// seen live as a second display whose refresh never arrived until something on it moved.
/// </summary>
public class LateEncoderTests
{
    [Fact]
    public async Task A_still_screen_gets_its_keyframes_from_an_encoder_that_answers_late()
    {
        var encoders = new FakeVideoEncoderFactory
        {
            Names = ["Asynchronous encoder"],
            Breaks = new Dictionary<string, EncoderFault> { ["Asynchronous encoder"] = EncoderFault.OneFrameLate },
        };
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, encoders: encoders);
        bed.Capturers!.UnchangedEvery = 0;
        bed.Capturers.Generator = (frame, bgra, _, _, _) =>
        {
            if (frame > 1)
            {
                return false; // one picture, then nothing moves
            }

            bgra.AsSpan().Fill(0x80);
            return true;
        };

        // TCP only: moving to UDP asks for a keyframe of its own, and that submission would flush the waiting one.
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(losslessTiles: true, udpMedia: false);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        await Testbed.WaitUntilAsync(() => cb.KeyFrames >= 1, "the first picture of a still screen", 15_000);

        await Task.Delay(1500);
        int before = cb.KeyFrames;
        await session.RefreshVideoAsync(0);
        await Testbed.WaitUntilAsync(() => cb.KeyFrames > before, "a refresh of a screen that has long been still", 15_000);
    }
}
