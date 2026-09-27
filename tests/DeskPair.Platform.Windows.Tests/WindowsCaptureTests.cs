using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Capture;

namespace DeskPair.Platform.Windows.Tests;

public class WindowsCaptureTests
{
    /// <summary>
    /// The one thing a screen capturer must do: produce a picture of the screen. Desktop Duplication can
    /// initialise happily and then report only "nothing has changed", for as long as the session lasts —
    /// a desktop nobody is attached to, a sleeping monitor, an output the compositor is not presenting to.
    /// The viewer then sees a black window and no error, which is the worst kind of failure because
    /// everything else in the session looks healthy.
    ///
    /// Whether duplication works here or not is a property of the machine, so this asserts the outcome
    /// rather than the path: within a few seconds there is a frame, by whichever route.
    /// </summary>
    [Fact]
    public async Task A_picture_arrives_even_when_duplication_never_presents_one()
    {
        if (!InteractiveDesktop.IsReadable)
        {
            return;
        }

        var displays = new WindowsDisplayEnumerator();
        IReadOnlyList<DisplayDescriptor> all = displays.GetDisplays();
        if (all.Count == 0)
        {
            return; // a machine with no display at all is not this test's business
        }

        var capturers = new WindowsScreenCapturerFactory(displays, NullLoggerFactory.Instance);
        await using IScreenCapturer capturer = capturers.Create(all[0], preferGpu: false);

        CaptureFrame frame = default;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            CaptureResult result = await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(33), CancellationToken.None);
            if (result.Status == CaptureStatus.Frame)
            {
                frame = result.Frame;
                break;
            }
        }

        frame.Width.ShouldBe(all[0].Width);
        frame.Height.ShouldBe(all[0].Height);
        frame.Format.ShouldBe(PixelFormat.Bgra32);
        frame.Cpu.Length.ShouldBeGreaterThanOrEqualTo(frame.Stride * frame.Height);
    }

    /// <summary>
    /// The fallback is one-way and one-time: a capturer that has given up on duplication stays on GDI for the
    /// rest of its life rather than rebuilding the D3D device on every timeout.
    /// </summary>
    [Fact]
    public async Task Falling_back_to_gdi_happens_once_and_sticks()
    {
        if (!InteractiveDesktop.IsReadable)
        {
            return;
        }

        var displays = new WindowsDisplayEnumerator();
        IReadOnlyList<DisplayDescriptor> all = displays.GetDisplays();
        if (all.Count == 0)
        {
            return;
        }

        var capturers = new WindowsScreenCapturerFactory(displays, NullLoggerFactory.Instance);
        await using IScreenCapturer capturer = capturers.Create(all[0], preferGpu: false);
        capturer.ShouldBeOfType<DxgiScreenCapturer>();
        var dxgi = (DxgiScreenCapturer)capturer;

        dxgi.ForceFallbackPath();
        dxgi.IsGdi.ShouldBeTrue();

        dxgi.ForceFallbackPath();
        dxgi.IsGdi.ShouldBeTrue();

        CaptureResult result = await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        result.Status.ShouldBeOneOf(CaptureStatus.Frame, CaptureStatus.Timeout);
    }
}
