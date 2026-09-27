using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Linux.Capture;
using DeskPair.Platform.Linux.Input;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>Read-only against a live X session; safe to run while someone is using the machine.</summary>
public class X11DisplayTests
{
    /// <summary>
    /// The index travels on every frame and every mouse event and is resolved by position on the host;
    /// a primary output that XRandR lists second must not break that.
    /// </summary>
    [Fact]
    public void Indices_are_positions_with_the_primary_first()
    {
        if (!X11Session.IsAvailable)
        {
            return;
        }

        using var displays = new X11DisplayEnumerator(NullLogger<X11DisplayEnumerator>.Instance);
        IReadOnlyList<DisplayDescriptor> all = displays.GetDisplays();

        DisplayOrdering.IsConsistent(all).ShouldBeTrue();
        if (all.Any(d => d.IsPrimary))
        {
            all[0].IsPrimary.ShouldBeTrue();
        }
    }

    [Fact]
    public void Every_display_has_a_usable_descriptor()
    {
        if (!X11Session.IsAvailable)
        {
            return;
        }

        using var displays = new X11DisplayEnumerator(NullLogger<X11DisplayEnumerator>.Instance);
        IReadOnlyList<DisplayDescriptor> all = displays.GetDisplays();

        all.ShouldNotBeEmpty();
        foreach (DisplayDescriptor d in all)
        {
            d.Width.ShouldBeGreaterThan(0);
            d.Height.ShouldBeGreaterThan(0);
            d.Name.ShouldNotBeNullOrWhiteSpace();
        }

        all.Select(d => d.Index).ShouldBeUnique();
    }

    /// <summary>Callers take the head of the list as the display to capture, so the ordering carries meaning.</summary>
    [Fact]
    public void The_first_display_is_the_primary_one()
    {
        if (!X11Session.IsAvailable)
        {
            return;
        }

        using var displays = new X11DisplayEnumerator(NullLogger<X11DisplayEnumerator>.Instance);
        displays.GetDisplays()[0].IsPrimary.ShouldBeTrue();
    }

    /// <summary>It holds a display connection; building several in a row must not leak or throw.</summary>
    [Fact]
    public void Enumerators_can_be_built_and_disposed_repeatedly()
    {
        if (!X11Session.IsAvailable)
        {
            return;
        }

        for (int i = 0; i < 3; i++)
        {
            using var displays = new X11DisplayEnumerator(NullLogger<X11DisplayEnumerator>.Instance);
            displays.GetDisplays().ShouldNotBeEmpty();
        }
    }
}

public class X11CaptureTests
{
    [Fact]
    public async Task A_frame_arrives_with_the_geometry_the_descriptor_promised()
    {
        if (!X11Session.CanCapture)
        {
            return;
        }

        using var displays = new X11DisplayEnumerator(NullLogger<X11DisplayEnumerator>.Instance);
        DisplayDescriptor display = displays.GetDisplays()[0];
        var factory = new X11ScreenCapturerFactory(NullLoggerFactory.Instance);
        await using IScreenCapturer capturer = factory.Create(display, preferGpu: false);

        // A still screen reports Timeout, which is correct and not a failure, so keep asking until something
        // moves. Fifteen seconds is long enough for a clock to tick on any desktop.
        CaptureResult result = default;
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            result = await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
            if (result.Status == CaptureStatus.Frame)
            {
                break;
            }
        }

        result.Status.ShouldBe(CaptureStatus.Frame, "no frame in 15 s on a desktop that should be changing");
        CaptureFrame frame = result.Frame;
        frame.Width.ShouldBe(display.Width);
        frame.Height.ShouldBe(display.Height);
        frame.Format.ShouldBe(PixelFormat.Bgra32);
        frame.Stride.ShouldBe(display.Width * 4);
        frame.Cpu.Length.ShouldBe(frame.Stride * frame.Height);
    }

    /// <summary>The shm attach/detach ordering in Release is delicate; a regression shows up here or in ipcs.</summary>
    [Fact]
    public async Task Capturers_can_be_built_and_disposed_repeatedly()
    {
        if (!X11Session.CanCapture)
        {
            return;
        }

        using var displays = new X11DisplayEnumerator(NullLogger<X11DisplayEnumerator>.Instance);
        DisplayDescriptor display = displays.GetDisplays()[0];
        var factory = new X11ScreenCapturerFactory(NullLoggerFactory.Instance);
        for (int i = 0; i < 5; i++)
        {
            await using IScreenCapturer capturer = factory.Create(display, preferGpu: false);
            capturer.Display.Width.ShouldBe(display.Width);
        }
    }
}

public class X11CursorTests
{
    [Fact]
    public async Task The_pointer_reports_a_shape_and_a_position()
    {
        if (!X11Session.HasCursor)
        {
            return;
        }

        await using var cursor = new X11CursorProvider(NullLogger<X11CursorProvider>.Instance);
        ulong id = cursor.GetCurrentCursorId();
        id.ShouldNotBe(0ul);

        CursorImage? image = cursor.GetCursorImage(id);
        image.ShouldNotBeNull();
        image!.Id.ShouldBe(id);
        image.Width.ShouldBeInRange(1, 256);
        image.Height.ShouldBeInRange(1, 256);
        image.Bgra.Length.ShouldBe(image.Width * image.Height * 4);
        image.HotX.ShouldBeLessThanOrEqualTo(image.Width);
        image.HotY.ShouldBeLessThanOrEqualTo(image.Height);

        cursor.GetCursorPosition().ShouldNotBeNull();
    }

    /// <summary>
    /// The two assertions that tell "read something" apart from "read the right thing". XFixes hands back
    /// premultiplied ARGB in long-sized words, and the unpack narrows each to 32 bits — get that wrong and
    /// the pixels come back as zeros or as interleaved garbage, both of which these catch.
    /// </summary>
    [Fact]
    public async Task The_shape_is_premultiplied_and_not_blank()
    {
        if (!X11Session.HasCursor)
        {
            return;
        }

        await using var cursor = new X11CursorProvider(NullLogger<X11CursorProvider>.Instance);
        CursorImage? image = cursor.GetCursorImage(cursor.GetCurrentCursorId());
        image.ShouldNotBeNull();

        ReadOnlySpan<byte> bgra = image!.Bgra.Span;
        bool anyVisible = false;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
            anyVisible |= a != 0;
            b.ShouldBeLessThanOrEqualTo(a, $"pixel {i / 4} is not premultiplied");
            g.ShouldBeLessThanOrEqualTo(a, $"pixel {i / 4} is not premultiplied");
            r.ShouldBeLessThanOrEqualTo(a, $"pixel {i / 4} is not premultiplied");
        }

        anyVisible.ShouldBeTrue("every pixel was fully transparent, so nothing was really read");
    }
}
