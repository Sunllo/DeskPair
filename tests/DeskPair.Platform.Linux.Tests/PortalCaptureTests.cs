using System.Runtime.InteropServices;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Linux.Native;
using DeskPair.Platform.Linux.Wayland;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The capturer over a PipeWire stream, with the stream replaced by memory this test owns: when a picture is handed
/// on uncopied and when it is copied, that a borrowed one cannot be read once it has gone back, that a picture of
/// another size asks for a rebuild rather than reaching an encoder built for the old one. The real stream was run on
/// the lab machine's GNOME (docs/architecture.md, section 8, Linux Wayland).
/// </summary>
public sealed class PortalCaptureTests : IDisposable
{
    private static readonly DisplayDescriptor Display = new(0, "wayland@0,0", 0, 0, 8, 4, 1.0, FrameRotation.None, true, 55);

    private readonly FakeStream _stream = new();
    private readonly List<(DisplayDescriptor Display, int Width, int Height)> _sizes = [];

    public void Dispose() => _stream.Free();

    [Fact]
    public async Task A_picture_the_stream_lends_is_handed_on_uncopied_with_its_own_stride()
    {
        PipeWireFrame lent = _stream.Add(width: 8, height: 4, stride: 48, fill: 7);
        await using PortalScreenCapturer capturer = Create();

        CaptureResult result = await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Status.ShouldBe(CaptureStatus.Frame);
        result.Frame.Stride.ShouldBe(48);
        result.Frame.Cpu.Length.ShouldBe(48 * 4);
        unsafe
        {
            fixed (byte* p = result.Frame.Cpu.Span)
            {
                ((nint)p).ShouldBe(lent.Data, "the stream's own memory, not a copy of it");
            }
        }

        _stream.Releases.ShouldBe(0, "a lent picture is kept until the next one is taken");
        capturer.LastFrameTiming.ReadbackMs.ShouldBe(0);
    }

    [Fact]
    public async Task A_lent_picture_cannot_be_read_once_the_next_one_is_taken()
    {
        _stream.Add(8, 4, 32, fill: 1);
        await using PortalScreenCapturer capturer = Create();
        CaptureFrame first = (await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Frame;
        first.Cpu.Span[0].ShouldBe((byte)1);

        _stream.Add(8, 4, 32, fill: 2);
        CaptureFrame second = (await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Frame;

        second.Cpu.Span[0].ShouldBe((byte)2);
        Should.Throw<ArgumentOutOfRangeException>(() => first.Cpu.Span[0], "the first went back to the compositor with the second's lock");
    }

    [Fact]
    public async Task A_picture_that_cannot_be_lent_is_copied_out_tight_and_given_back_at_once()
    {
        _stream.Add(8, 4, stride: 40, fill: 3, borrowable: false);
        await using PortalScreenCapturer capturer = Create();

        CaptureResult result = await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Frame.Stride.ShouldBe(8 * 4);
        result.Frame.Cpu.Length.ShouldBe(8 * 4 * 4);
        result.Frame.Cpu.ToArray().ShouldAllBe(b => b == 3, "rows without the stream's padding");
        _stream.Releases.ShouldBe(1);
    }

    [Fact]
    public async Task A_buffer_too_short_for_the_last_rows_padding_is_copied_rather_than_lent()
    {
        // Readable up to the last pixel, but not to stride * height: the engine slices Cpu[..(Stride * Height)].
        _stream.Add(8, 4, stride: 40, fill: 4, readable: (40 * 3) + (8 * 4));
        await using PortalScreenCapturer capturer = Create();

        CaptureResult result = await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Frame.Stride.ShouldBe(32);
        _stream.Releases.ShouldBe(1);
    }

    [Fact]
    public async Task After_the_fallback_every_picture_is_copied()
    {
        _stream.Add(8, 4, 32, fill: 5);
        await using PortalScreenCapturer capturer = Create();
        capturer.ForceFallbackPath();

        CaptureResult result = await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Frame.Cpu.Span[0].ShouldBe((byte)5);
        _stream.Releases.ShouldBe(1);
    }

    [Fact]
    public async Task A_copied_picture_is_not_written_over_by_the_next_one()
    {
        _stream.Add(8, 4, 32, fill: 1, borrowable: false);
        await using PortalScreenCapturer capturer = Create();
        CaptureFrame first = (await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Frame;

        _stream.Add(8, 4, 32, fill: 2, borrowable: false);
        CaptureFrame second = (await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Frame;

        first.Cpu.ToArray().ShouldAllBe(b => b == 1, "the caller may still be encoding it until this one is returned");
        second.Cpu.ToArray().ShouldAllBe(b => b == 2);
    }

    [Theory]
    [InlineData(WaylandShim.FormatBgrx, PixelFormat.Bgra32)]
    [InlineData(WaylandShim.FormatRgbx, PixelFormat.Rgba32)]
    public async Task The_byte_order_is_the_streams(int format, PixelFormat expected)
    {
        _stream.Add(8, 4, 32, fill: 0, format: format);
        await using PortalScreenCapturer capturer = Create();

        (await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Frame.Format.ShouldBe(expected);
    }

    [Fact]
    public async Task A_format_this_build_cannot_read_is_an_error_and_goes_back()
    {
        _stream.Add(8, 4, 32, fill: 0, format: 99);
        await using PortalScreenCapturer capturer = Create();

        CaptureResult result = await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Status.ShouldBe(CaptureStatus.Error);
        _stream.Releases.ShouldBe(1);
    }

    [Fact]
    public async Task A_picture_of_another_size_rebuilds_the_capturer_and_says_the_new_size()
    {
        _stream.Add(16, 8, 64, fill: 0);
        await using PortalScreenCapturer capturer = Create();

        CaptureResult result = await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Status.ShouldBe(CaptureStatus.DesktopSwitched);
        _sizes.ShouldBe([(Display, 16, 8)]);
        _stream.Releases.ShouldBe(1);
    }

    [Fact]
    public async Task A_stream_that_is_over_fails_with_its_reason()
    {
        _stream.Over = "the compositor closed the screen-sharing stream";
        await using PortalScreenCapturer capturer = Create();

        CaptureResult result = await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Status.ShouldBe(CaptureStatus.Error);
        result.Error!.Message.ShouldContain("closed the screen-sharing stream");
    }

    [Fact]
    public async Task Nothing_new_is_a_timeout_and_a_cancellation_is_noticed()
    {
        await using PortalScreenCapturer capturer = Create();

        (await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(30), CancellationToken.None)).Status.ShouldBe(CaptureStatus.Timeout);
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(30), new CancellationTokenSource(TimeSpan.FromMilliseconds(50)).Token));
    }

    [Fact]
    public async Task Disposing_the_capturer_closes_its_stream()
    {
        PortalScreenCapturer capturer = Create();

        await capturer.DisposeAsync();

        _stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void The_monitor_at_the_origin_is_primary_and_sizes_are_pixels()
    {
        PortalStream left = new(61, "0", -1920, 0, 1920, 1080, true, 1, null);
        PortalStream origin = new(62, "1", 0, 0, 1280, 800, true, 1, null);

        List<DisplayDescriptor> displays = PortalCapture.Describe([left, origin], [(1920, 1080), (2560, 1600)]);

        displays.Select(d => (d.Index, d.Name, d.Width, d.Height, d.Scale, d.IsPrimary, d.AdapterLuid)).ShouldBe(
        [
            (0, "wayland@0,0", 2560, 1600, 2.0, true, 62L),
            (1, "wayland@-1920,0", 1920, 1080, 1.0, false, 61L),
        ]);
        DisplayOrdering.IsConsistent(displays).ShouldBeTrue();
    }

    [Fact]
    public void Without_positions_the_first_monitor_is_primary_and_named_by_its_place()
    {
        List<DisplayDescriptor> displays = PortalCapture.Describe([new PortalStream(70, null, 0, 0, 800, 600, false, 1, null)], [(800, 600)]);

        displays.ShouldHaveSingleItem().IsPrimary.ShouldBeTrue();
        displays[0].Name.ShouldBe("wayland#1", "the node id is new every session; the place in the portal's order is not");
    }

    [Fact]
    public void The_same_monitor_brought_back_twice_is_one_display()
    {
        // GNOME restoring a remembered choice of two identical monitors without serial numbers: the lab VM's two
        // virtual monitors came back as [node 62 800x600@0,0; node 70 800x600@0,0], the first one twice.
        PortalStream first = new(62, "0", 0, 0, 800, 600, true, 1, null);
        PortalStream again = new(70, "1", 0, 0, 800, 600, true, 1, null);

        List<DisplayDescriptor> displays = PortalCapture.Describe([first, again], [(800, 600), (800, 600)]);

        displays.Select(d => (d.Index, d.Name, d.AdapterLuid, d.IsPrimary)).ShouldBe([(0, "wayland@0,0", 62L, true)]);
    }

    [Fact]
    public void Monitors_of_one_size_in_different_places_are_all_kept()
    {
        PortalStream left = new(62, "0", 0, 0, 800, 600, true, 1, null);
        PortalStream right = new(60, "1", 800, 0, 800, 600, true, 1, null);

        PortalCapture.Describe([left, right], [(800, 600), (800, 600)]).Select(d => d.Name).ShouldBe(["wayland@0,0", "wayland@800,0"]);
    }

    [Fact]
    public void Monitors_without_positions_are_laid_side_by_side_so_a_point_lands_on_one()
    {
        // As KDE 5.27 sends them: two monitors, no position for either.
        PortalStream first = new(71, null, 0, 0, 1920, 1080, false, 1, null);
        PortalStream second = new(72, null, 0, 0, 1280, 800, false, 1, null);

        List<DisplayDescriptor> displays = PortalCapture.Describe([first, second], [(1920, 1080), (1280, 800)]);

        displays.Select(d => (d.Name, d.X, d.Y, d.IsPrimary, d.AdapterLuid)).ShouldBe(
        [
            ("wayland#1", 0, 0, true, 71L),
            ("wayland#2", 1920, 0, false, 72L),
        ]);
        PortalInputInjector.ToStream(displays, 2000, 100).ShouldBe((72u, 80.0, 100.0), "a point on the second goes to the second's stream");
        PortalInputInjector.ToStream(displays, 100, 100).ShouldBe((71u, 100.0, 100.0));
    }

    [Fact]
    public async Task The_pointer_over_this_monitor_is_placed_on_the_virtual_screen_with_its_shape()
    {
        DisplayDescriptor second = Display with { Index = 1, X = 1280, Y = 40 };
        await using var capturer = new PortalScreenCapturer(_stream, second, NullLogger.Instance);
        _stream.Cursor = new PipeWireCursor(true, 10, 20, 1, 2, 2, 1, WaylandShim.FormatRgba, ShapeSerial: 7);
        _stream.CursorBitmap = [10, 20, 30, 255, 40, 50, 60, 128];

        (Abstractions.Cursor.CursorImage shape, int x, int y) = capturer.Cursor()!.Value;

        (x, y).ShouldBe((1290, 60));
        (shape.HotX, shape.HotY, shape.Width, shape.Height).ShouldBe((1, 2, 2, 1));
        shape.Bgra.ToArray().ShouldBe(new byte[] { 30, 20, 10, 255, 60, 50, 40, 128 }, "RGBA becomes BGRA, alpha untouched");
    }

    [Fact]
    public async Task The_bitmap_is_copied_only_when_the_shape_changes_and_its_id_follows_its_content()
    {
        await using PortalScreenCapturer capturer = Create();
        _stream.Cursor = new PipeWireCursor(true, 0, 0, 0, 0, 1, 1, WaylandShim.FormatBgra, ShapeSerial: 1);
        _stream.CursorBitmap = [1, 2, 3, 4];

        ulong first = capturer.Cursor()!.Value.Shape.Id;
        _ = capturer.Cursor();
        _stream.BitmapCopies.ShouldBe(1, "the pointer moving is not a new shape");

        _stream.Cursor = _stream.Cursor with { ShapeSerial = 2 };
        _stream.CursorBitmap = [9, 9, 9, 9];
        ulong second = capturer.Cursor()!.Value.Shape.Id;

        _stream.Cursor = _stream.Cursor with { ShapeSerial = 3 };
        _stream.CursorBitmap = [1, 2, 3, 4];
        ulong third = capturer.Cursor()!.Value.Shape.Id;

        second.ShouldNotBe(first);
        third.ShouldBe(first, "the same shape again has the id a viewer cached it under");
        first.ShouldNotBe(0UL);
    }

    [Fact]
    public async Task A_pointer_elsewhere_or_a_closed_stream_has_no_position()
    {
        PortalScreenCapturer capturer = Create();
        _stream.Cursor = new PipeWireCursor(false, 5, 5, 0, 0, 1, 1, WaylandShim.FormatBgra, 1);
        capturer.Cursor().ShouldBeNull();

        _stream.Cursor = _stream.Cursor with { Visible = true };
        _stream.CursorBitmap = [1, 1, 1, 1];
        capturer.Cursor().ShouldNotBeNull();

        await capturer.DisposeAsync();
        capturer.Cursor().ShouldBeNull();
    }

    [Fact]
    public void A_bitmap_in_a_format_this_cannot_read_is_no_shape()
    {
        PortalScreenCapturer.ToImage(new PipeWireCursor(true, 0, 0, 0, 0, 1, 1, 99, 1), new byte[4]).ShouldBeNull();
        PortalScreenCapturer.ToImage(new PipeWireCursor(true, 0, 0, 0, 0, 2, 2, WaylandShim.FormatBgra, 1), new byte[4]).ShouldBeNull("shorter than the size says");
    }

    private PortalScreenCapturer Create() =>
        new(_stream, Display, NullLogger.Instance, (display, width, height) => _sizes.Add((display, width, height)));

    /// <summary>A stream whose pictures are unmanaged memory, locked and given back the way the shim does it.</summary>
    private sealed class FakeStream : IPipeWireStream
    {
        private readonly Queue<PipeWireFrame> _pending = new();
        private readonly List<nint> _memory = [];

        public int Releases { get; private set; }

        public bool Disposed { get; private set; }

        public string? Over { get; set; }

        public string? Failure => Over;

        public unsafe PipeWireFrame Add(int width, int height, int stride, byte fill, bool borrowable = true, int format = WaylandShim.FormatBgrx, long? readable = null)
        {
            nint data = (nint)NativeMemory.Alloc((nuint)(stride * height));
            _memory.Add(data);
            var bytes = new Span<byte>((void*)data, stride * height);
            bytes.Fill(0xEE);
            for (int y = 0; y < height; y++)
            {
                bytes.Slice(y * stride, width * 4).Fill(fill);
            }

            var frame = new PipeWireFrame(data, width, height, stride, format, (ulong)_memory.Count, 4, borrowable, readable ?? (long)stride * height);
            _pending.Enqueue(frame);
            return frame;
        }

        public int Wait(int timeoutMs)
        {
            if (Over is not null)
            {
                return -1;
            }

            if (_pending.Count > 0)
            {
                return 1;
            }

            Thread.Sleep(Math.Min(timeoutMs, 5));
            return 0;
        }

        public bool TryLock(out PipeWireFrame frame) => _pending.TryDequeue(out frame);

        public PipeWireCursor Cursor { get; set; }

        public byte[] CursorBitmap { get; set; } = [];

        public int BitmapCopies { get; private set; }

        public PipeWireCursor ReadCursor(Span<byte> bitmap)
        {
            if (bitmap.Length >= CursorBitmap.Length && CursorBitmap.Length > 0)
            {
                CursorBitmap.CopyTo(bitmap);
                BitmapCopies++;
            }

            return Cursor;
        }

        public void Release() => Releases++;

        public void Dispose() => Disposed = true;

        public unsafe void Free()
        {
            foreach (nint p in _memory)
            {
                NativeMemory.Free((void*)p);
            }

            _memory.Clear();
        }
    }
}
