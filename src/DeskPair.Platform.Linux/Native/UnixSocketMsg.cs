using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// Passing file descriptors between processes: <c>sendmsg</c>/<c>recvmsg</c> with <c>SCM_RIGHTS</c>.
///
/// This is how a dma-buf the root daemon exported, and the uinput device it created, reach the engine
/// that runs as an ordinary user. .NET's sockets have no ancillary-data API, so the three calls and the
/// cmsg arithmetic are done here by hand, for x86-64 and AArch64 Linux where <c>struct cmsghdr</c> is
/// <c>{ size_t len; int level; int type; }</c> — sixteen bytes, eight-aligned.
///
/// SEQPACKET, not STREAM. On a stream socket a short read can separate a message from the descriptors
/// attached to it, and the descriptors then arrive with whichever bytes happened to be read — a fault
/// that appears only under load. A sequenced-packet socket delivers one message as one message, with
/// its descriptors, or nothing.
/// </summary>
public static unsafe partial class UnixSocketMsg
{
    private const string Lib = "libc";

    public const int AfUnix = 1;
    public const int SockSeqPacket = 5;
    public const int SockCloexec = 0x80000;
    public const int SolSocket = 1;
    public const int ScmRights = 1;
    public const int MsgCmsgCloexec = 0x40000000;
    public const int MsgNoSignal = 0x4000;

    /// <summary>fcntl(F_SETFD) with no flags: the descriptor survives exec and reaches the child.</summary>
    public const int FSetFd = 2;

    public const int MaxFds = 8;

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int socketpair(int domain, int type, int protocol, int* sv);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint sendmsg(int socket, MsgHdr* message, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint recvmsg(int socket, MsgHdr* message, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int fcntl(int fd, int cmd, int arg);

    /// <summary><c>how</c> for <see cref="shutdown"/>: both directions, so a thread blocked in recvmsg gets its zero and returns.</summary>
    public const int ShutRdWr = 2;

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int shutdown(int socket, int how);

    [StructLayout(LayoutKind.Sequential)]
    public struct IoVec
    {
        public void* Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MsgHdr
    {
        public void* Name;
        public uint NameLength;
        public IoVec* Iov;
        public nuint IovLength;
        public void* Control;
        public nuint ControlLength;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CmsgHdr
    {
        public nuint Length;
        public int Level;
        public int Type;
    }

    /// <summary>CMSG_SPACE: header plus payload, rounded to the header's alignment.</summary>
    public static int CmsgSpace(int payload) => sizeof(CmsgHdr) + ((payload + sizeof(nuint) - 1) & ~(sizeof(nuint) - 1));

    /// <summary>CMSG_LEN: header plus payload, not rounded — what goes in <c>cmsg_len</c>.</summary>
    public static int CmsgLen(int payload) => sizeof(CmsgHdr) + payload;

    /// <summary>A connected pair, both ends close-on-exec. The caller clears the flag on the end a child gets.</summary>
    public static (int Parent, int Child) Pair()
    {
        int* sv = stackalloc int[2];
        if (socketpair(AfUnix, SockSeqPacket | SockCloexec, 0, sv) != 0)
        {
            throw new IOException($"socketpair failed: errno {Marshal.GetLastPInvokeError()}");
        }

        return (sv[0], sv[1]);
    }

    /// <summary>Lets a descriptor survive exec, so the process started next finds it at the same number.</summary>
    public static void Inherit(int fd)
    {
        if (fcntl(fd, FSetFd, 0) != 0)
        {
            throw new IOException($"fcntl(F_SETFD) failed: errno {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>One message and up to <see cref="MaxFds"/> descriptors, delivered together or not at all.</summary>
    public static void Send(int socket, ReadOnlySpan<byte> payload, ReadOnlySpan<int> fds)
    {
        if (fds.Length > MaxFds)
        {
            throw new ArgumentOutOfRangeException(nameof(fds), $"at most {MaxFds} descriptors per message");
        }

        int controlSize = fds.Length == 0 ? 0 : CmsgSpace(fds.Length * sizeof(int));
        byte* control = stackalloc byte[Math.Max(controlSize, 1)];
        fixed (byte* body = payload)
        {
            var iov = new IoVec { Base = body, Length = (nuint)payload.Length };
            var header = new MsgHdr { Iov = &iov, IovLength = 1 };
            if (fds.Length > 0)
            {
                var cmsg = (CmsgHdr*)control;
                cmsg->Length = (nuint)CmsgLen(fds.Length * sizeof(int));
                cmsg->Level = SolSocket;
                cmsg->Type = ScmRights;
                var slot = (int*)(control + sizeof(CmsgHdr));
                for (int i = 0; i < fds.Length; i++)
                {
                    slot[i] = fds[i];
                }

                header.Control = control;
                header.ControlLength = (nuint)controlSize;
            }

            nint sent = sendmsg(socket, &header, MsgNoSignal);
            if (sent < 0)
            {
                throw new IOException($"sendmsg failed: errno {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    /// <summary>
    /// One message. Returns the payload length, and fills <paramref name="fds"/> with any descriptors that
    /// came with it — already close-on-exec, so a child started later does not inherit them by accident.
    /// Zero bytes and zero descriptors means the other end has gone.
    /// </summary>
    public static int Receive(int socket, Span<byte> payload, Span<int> fds, out int fdCount)
    {
        int controlSize = CmsgSpace(MaxFds * sizeof(int));
        byte* control = stackalloc byte[controlSize];
        fdCount = 0;
        fixed (byte* body = payload)
        {
            var iov = new IoVec { Base = body, Length = (nuint)payload.Length };
            var header = new MsgHdr { Iov = &iov, IovLength = 1, Control = control, ControlLength = (nuint)controlSize };
            nint got = recvmsg(socket, &header, MsgCmsgCloexec);
            if (got < 0)
            {
                throw new IOException($"recvmsg failed: errno {Marshal.GetLastPInvokeError()}");
            }

            if (header.ControlLength >= (nuint)sizeof(CmsgHdr))
            {
                var cmsg = (CmsgHdr*)control;
                if (cmsg->Level == SolSocket && cmsg->Type == ScmRights)
                {
                    int count = (int)(cmsg->Length - (nuint)sizeof(CmsgHdr)) / sizeof(int);
                    var slot = (int*)(control + sizeof(CmsgHdr));
                    for (int i = 0; i < count; i++)
                    {
                        if (i < fds.Length)
                        {
                            fds[i] = slot[i];
                            fdCount++;
                        }
                        else
                        {
                            // More than the caller can take: closed rather than leaked as unnamed open files.
                            close(slot[i]);
                        }
                    }
                }
            }

            return (int)got;
        }
    }
}
