using System.Runtime.InteropServices;
using System.Text;
using DeskPair.Platform.Linux.Native;
using Microsoft.Win32.SafeHandles;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// A descriptor goes through a socketpair and comes out the other side as the same file.
///
/// This is the channel the daemon hands the engine its picture and its keyboard on, and the cmsg
/// arithmetic behind it is exactly the kind of thing that works on the machine it was written on. The
/// test is real: a real socketpair, a real file, and the received descriptor is read to prove it is the
/// same one. It runs on any Linux, with no display and no root.
/// </summary>
public class UnixFdPassingTests
{
    [Fact]
    public void A_descriptor_arrives_as_the_same_file()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // SCM_RIGHTS is a Unix thing; on Windows this file has nothing to say.
        }

        string path = Path.Combine(Path.GetTempPath(), "deskpair-fd-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "the same file on both sides");
        try
        {
            using SafeFileHandle file = File.OpenHandle(path);
            (int a, int b) = UnixSocketMsg.Pair();
            try
            {
                UnixSocketMsg.Send(a, Encoding.ASCII.GetBytes("frame"), [(int)file.DangerousGetHandle()]);

                Span<byte> payload = stackalloc byte[64];
                Span<int> fds = stackalloc int[UnixSocketMsg.MaxFds];
                int length = UnixSocketMsg.Receive(b, payload, fds, out int count);

                Encoding.ASCII.GetString(payload[..length]).ShouldBe("frame");
                count.ShouldBe(1);
                fds[0].ShouldNotBe((int)file.DangerousGetHandle(), "a passed descriptor is a new number in the receiver");

                using var received = new FileStream(new SafeFileHandle((nint)fds[0], ownsHandle: true), FileAccess.Read);
                using var reader = new StreamReader(received);
                reader.ReadToEnd().ShouldBe("the same file on both sides");
            }
            finally
            {
                UnixSocketMsg.close(a);
                UnixSocketMsg.close(b);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A message with no descriptors is still one message, and says so with a count of zero.</summary>
    [Fact]
    public void A_message_without_descriptors_is_delivered_whole()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        (int a, int b) = UnixSocketMsg.Pair();
        try
        {
            UnixSocketMsg.Send(a, Encoding.ASCII.GetBytes("poll"), []);
            Span<byte> payload = stackalloc byte[8];
            Span<int> fds = stackalloc int[UnixSocketMsg.MaxFds];
            int length = UnixSocketMsg.Receive(b, payload, fds, out int count);

            length.ShouldBe(4);
            count.ShouldBe(0);
        }
        finally
        {
            UnixSocketMsg.close(a);
            UnixSocketMsg.close(b);
        }
    }

    /// <summary>The other end closing is zero bytes and zero descriptors, not an exception and not a hang.</summary>
    [Fact]
    public void The_other_end_going_away_reads_as_nothing()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        (int a, int b) = UnixSocketMsg.Pair();
        UnixSocketMsg.close(a);
        try
        {
            Span<byte> payload = stackalloc byte[8];
            Span<int> fds = stackalloc int[UnixSocketMsg.MaxFds];
            UnixSocketMsg.Receive(b, payload, fds, out int count).ShouldBe(0);
            count.ShouldBe(0);
        }
        finally
        {
            UnixSocketMsg.close(b);
        }
    }

    // struct cmsghdr { size_t cmsg_len; int cmsg_level; int cmsg_type; } and struct msghdr on LP64
    [Fact]
    public void The_cmsg_and_msghdr_layouts_are_lp64()
    {
        Marshal.SizeOf<UnixSocketMsg.CmsgHdr>().ShouldBe(16);
        Marshal.SizeOf<UnixSocketMsg.MsgHdr>().ShouldBe(56);
        Marshal.SizeOf<UnixSocketMsg.IoVec>().ShouldBe(16);
        UnixSocketMsg.CmsgSpace(4).ShouldBe(24);   // 16 + 4 rounded up to 8
        UnixSocketMsg.CmsgLen(4).ShouldBe(20);     // 16 + 4, not rounded
        UnixSocketMsg.CmsgSpace(8 * 4).ShouldBe(48);
    }
}
