using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Linux.Native;
using Microsoft.Win32.SafeHandles;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The engine's end of the scanout channel, against a daemon played by the test over a real socketpair, with real
/// files standing in for the dma-bufs. A map is only read inside the poll that named it, and no more maps are kept
/// than a poll can name. Linux only: descriptors cross by SCM_RIGHTS and are mapped with mmap.
/// </summary>
public sealed class DrmCaptureChannelTests
{
    private const int Width = 16;
    private const int Height = 4;
    private const int Pitch = Width * 4;

    [Fact]
    public async Task A_buffer_is_read_inside_the_poll_so_no_other_poll_unmaps_it_meanwhile()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var daemon = new FakeDaemon();
        using var channel = new DrmCaptureChannel(daemon.EngineEnd);

        // Every answer names framebuffer 1 with a new descriptor, as the daemon does when the id has come to name a
        // different buffer: each poll replaces the map the one before it made. On a two-monitor GNOME desktop the
        // enumerator's poll did that while the capturer was still copying, and the copy ran into unmapped memory.
        daemon.Answer = poll => (1, (byte)poll, true);

        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        byte[] seen = [];
        Task<DrmPollResult> reading = Task.Run(() => channel.Poll((in DrmFrameInfo frame, DrmMappedBuffer map) =>
        {
            inside.TrySetResult();
            release.Wait();
            seen = Contents(map);
            return true;
        }));
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task<DrmPollResult> other = Task.Run(() => channel.Poll());
        (await Task.WhenAny(other, Task.Delay(300))).ShouldNotBe(other, "another poll waits until the read is done");
        release.Set();

        (await reading).Read.ShouldBeTrue();
        seen.ShouldAllBe(b => b == 1, "the first answer's buffer, whole, although the second answer replaces it");
        DrmPollResult after = await other;
        after.Kind.ShouldBe(DrmPollKind.Frame);
        after.Read.ShouldBeFalse("nothing reads without a reader");
    }

    [Fact]
    public void No_more_buffers_are_mapped_than_a_poll_can_name()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var daemon = new FakeDaemon();
        using var channel = new DrmCaptureChannel(daemon.EngineEnd);
        daemon.Answer = poll => ((uint)poll, (byte)poll, false); // a buffer never shown before, every time

        for (int i = 0; i < 10; i++)
        {
            channel.Poll().Kind.ShouldBe(DrmPollKind.Frame);
        }

        channel.MapCount.ShouldBe(DrmWire.KnownFbSlots);
        channel.Poll();
        daemon.LastKnown.ShouldBe([10u, 9u, 8u, 7u], "the ones shown last, the latest first");
    }

    [Fact]
    public void A_buffer_still_mapped_is_named_and_read_without_being_sent_again()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var daemon = new FakeDaemon();
        using var channel = new DrmCaptureChannel(daemon.EngineEnd);

        // A compositor flipping between two buffers, 1 and 2, then showing one it dropped long ago.
        uint[] shown = [1, 2, 1, 2, 1, 3, 4, 5, 6, 1];
        daemon.Answer = poll => (shown[poll - 1], (byte)shown[poll - 1], false);

        var read = new List<byte>();
        for (int i = 0; i < shown.Length; i++)
        {
            DrmPollResult result = channel.Poll((in DrmFrameInfo frame, DrmMappedBuffer map) =>
            {
                read.Add(Contents(map)[0]);
                return true;
            });
            result.Read.ShouldBeTrue();
        }

        read.ShouldBe([1, 2, 1, 2, 1, 3, 4, 5, 6, 1]);
        daemon.DescriptorsSent.ShouldBe(7, "1 and 2 once each while they were flipped, 3 to 6, and 1 again after it fell out");
    }

    /// <summary>Which monitor the picture is of, for placing clicks: asked for every click, so it is kept where no poll holds it up.</summary>
    [Fact]
    public void The_connector_of_the_last_picture_is_known()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var daemon = new FakeDaemon();
        using var channel = new DrmCaptureChannel(daemon.EngineEnd);
        channel.Connector.ShouldBeNull("no picture yet");

        channel.Poll().Kind.ShouldBe(DrmPollKind.Frame);

        channel.Connector.ShouldBe((15u, 2u), "Virtual-2");
    }

    private static unsafe byte[] Contents(DrmMappedBuffer map) => new ReadOnlySpan<byte>(map.Base, Pitch * Height).ToArray();

    /// <summary>
    /// The daemon's side of the channel: answers each poll with the framebuffer <see cref="Answer"/> names for that
    /// poll (counted from 1), attaching a descriptor -- a file filled with the answer's byte -- when the engine did
    /// not name the id as known, or when the answer says the id is a new buffer.
    /// </summary>
    private sealed class FakeDaemon : IDisposable
    {
        private readonly int _end;
        private readonly Thread _thread;
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "deskpair-drm-" + Guid.NewGuid().ToString("N"));

        public FakeDaemon()
        {
            Directory.CreateDirectory(_folder);
            (EngineEnd, _end) = UnixSocketMsg.Pair();
            _thread = new Thread(Serve) { IsBackground = true, Name = "fake-daemon" };
            _thread.Start();
        }

        public int EngineEnd { get; }

        public Func<int, (uint FbId, byte Fill, bool Replace)> Answer { get; set; } = _ => (1, 0, false);

        public uint[] LastKnown { get; private set; } = [];

        public int DescriptorsSent { get; private set; }

        private void Serve()
        {
            byte[] request = new byte[DrmWire.PollSize];
            byte[] reply = new byte[DrmWire.FrameSize];
            Span<int> fds = stackalloc int[UnixSocketMsg.MaxFds];
            uint[] known = new uint[DrmWire.KnownFbSlots];
            for (int poll = 1; ; poll++)
            {
                int length;
                try
                {
                    length = UnixSocketMsg.Receive(_end, request, fds, out _);
                }
                catch (IOException)
                {
                    return;
                }

                if (length == 0 || !DrmWire.TryReadPoll(request.AsSpan(0, length), known))
                {
                    return;
                }

                LastKnown = [.. known.Where(id => id != 0)];
                (uint fbId, byte fill, bool replace) = Answer(poll);
                bool attach = replace || !LastKnown.Contains(fbId);
                var frame = new DrmFrameInfo
                {
                    Generation = 1,
                    FbId = fbId,
                    CrtcId = 40,
                    ConnectorId = 41,
                    ConnectorType = 15,
                    ConnectorTypeId = 2,
                    Fourcc = 0x34325258, // XR24
                    PlaneCount = 1,
                    Width = Width,
                    Height = Height,
                    SrcWidth = Width,
                    SrcHeight = Height,
                    Pitch = Pitch,
                    Sequence = (ulong)poll,
                };
                int n = DrmWire.WriteFrame(reply, frame, fdAttached: attach);
                if (!attach)
                {
                    UnixSocketMsg.Send(_end, reply.AsSpan(0, n), []);
                    continue;
                }

                string path = Path.Combine(_folder, $"fb-{poll}");
                using (SafeFileHandle file = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite))
                {
                    RandomAccess.Write(file, Enumerable.Repeat(fill, Pitch * Height).ToArray(), 0);
                    UnixSocketMsg.Send(_end, reply.AsSpan(0, n), [(int)file.DangerousGetHandle()]);
                }

                DescriptorsSent++;
            }
        }

        public void Dispose()
        {
            _ = UnixSocketMsg.close(_end);
            _thread.Join(TimeSpan.FromSeconds(5));
            Directory.Delete(_folder, recursive: true);
        }
    }
}
