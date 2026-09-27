using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Platform.Unix.Terminal;

namespace DeskPair.Platform.Linux.Terminal;

/// <summary>The engine's terminal on Linux: a shell on a pseudo-terminal, as the account the engine runs as.</summary>
public static class LinuxTerminal
{
    public static ITerminalHost Create(ILogger log) => new UnixTerminalHost(new LinuxPtySpawner(), log);
}

/// <summary>
/// Starts a shell with <c>posix_spawn</c>, never <c>fork</c> from managed code: a forked copy of the
/// runtime has one thread and every lock of the others, and can hang in malloc or the GC before it
/// reaches exec -- rarely, and never reproducibly. glibc's posix_spawn runs the child on its own stack
/// with CLONE_VFORK and touches nothing of ours.
///
/// The controlling terminal comes from order, which glibc guarantees: POSIX_SPAWN_SETSID makes the child
/// a session leader before the file actions run, and a session leader that opens a terminal without
/// O_NOCTTY acquires it. So the open of the slave onto fd 0 is what gives the shell job control and lets
/// Ctrl+C become SIGINT.
/// </summary>
internal sealed unsafe partial class LinuxPtySpawner : IPtySpawner
{
    private const string Lib = "libc";
    private const int O_RDWR = 2;
    private const int O_NOCTTY = 0x100;
    private const int O_CLOEXEC = 0x80000;
    private const nuint TIOCSWINSZ = 0x5414;
    private const short POSIX_SPAWN_SETSIGDEF = 0x04;
    private const short POSIX_SPAWN_SETSIGMASK = 0x08;
    private const short POSIX_SPAWN_SETSID = 0x80;

    /// <summary>Generous: glibc's are 80 and 336 bytes and opaque, so the buffers are sized for any version.</summary>
    private const int OpaqueSize = 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort XPixels;
        public ushort YPixels;
    }

    /// <summary>A new pseudo-terminal of that size: the master, close-on-exec, and the slave's path.</summary>
    internal static (int Master, string SlavePath) OpenPty(int columns, int rows)
    {
        int master = open("/dev/ptmx", O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (master < 0)
        {
            throw Failed("open /dev/ptmx");
        }

        byte* name = stackalloc byte[128];
        if (grantpt(master) != 0 || unlockpt(master) != 0 || ptsname_r(master, name, 128) != 0)
        {
            TerminalStartException e = Failed("preparing the pseudo-terminal");
            _ = close(master);
            throw e;
        }

        SetSize(master, columns, rows);
        return (master, Marshal.PtrToStringUTF8((nint)name) ?? string.Empty);
    }

    internal static void CloseFd(int fd) => _ = close(fd);

    public (int Master, int Pid) Spawn(string path, string[] argv, string[] env, string directory, int columns, int rows)
    {
        (int master, string slavePath) = OpenPty(columns, rows);
        byte[] slave = Encoding.UTF8.GetBytes(slavePath + '\0');

        void* actions = NativeMemory.AllocZeroed(OpaqueSize);
        void* attributes = NativeMemory.AllocZeroed(OpaqueSize);
        void* allSignals = NativeMemory.AllocZeroed(OpaqueSize);
        void* noSignals = NativeMemory.AllocZeroed(OpaqueSize);
        var strings = new List<nint>();
        try
        {
            _ = posix_spawn_file_actions_init(actions);
            _ = posix_spawnattr_init(attributes);

            // The slave onto 0, 1 and 2. Opened after setsid and without O_NOCTTY, it becomes the controlling terminal.
            fixed (byte* name = slave)
            {
                _ = posix_spawn_file_actions_addopen(actions, 0, name, O_RDWR, 0);
            }

            _ = posix_spawn_file_actions_adddup2(actions, 0, 1);
            _ = posix_spawn_file_actions_adddup2(actions, 0, 2);

            // Everything else closed, where glibc can (2.34+). Without it, descriptors this process opened
            // without O_CLOEXEC would reach the shell -- the engine clears that flag's absence on what the
            // daemon hands it, but this is the belt to that brace.
            try
            {
                _ = posix_spawn_file_actions_addclosefrom_np(actions, 3);
            }
            catch (EntryPointNotFoundException)
            {
            }

            byte[] cwd = Encoding.UTF8.GetBytes(directory + '\0');
            fixed (byte* dir = cwd)
            {
                try
                {
                    _ = posix_spawn_file_actions_addchdir_np(actions, dir);
                }
                catch (EntryPointNotFoundException)
                {
                    // glibc before 2.29: the shell starts in the engine's directory, and its profile usually cds home.
                }
            }

            // The runtime ignores SIGPIPE and may block others; a shell must start with everything at default.
            _ = sigfillset(allSignals);
            _ = sigemptyset(noSignals);
            _ = posix_spawnattr_setsigdefault(attributes, allSignals);
            _ = posix_spawnattr_setsigmask(attributes, noSignals);
            _ = posix_spawnattr_setflags(attributes, POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK);

            nint* args = Strings(argv, strings);
            nint* envp = Strings(env, strings);
            byte[] file = Encoding.UTF8.GetBytes(path + '\0');
            int pid;
            int error;
            fixed (byte* f = file)
            {
                error = posix_spawn(&pid, f, actions, attributes, args, envp);
            }

            if (error != 0)
            {
                _ = close(master);
                throw new TerminalStartException($"The shell {path} could not be started: {Marshal.GetPInvokeErrorMessage(error)}.");
            }

            return (master, pid);
        }
        finally
        {
            _ = posix_spawn_file_actions_destroy(actions);
            _ = posix_spawnattr_destroy(attributes);
            NativeMemory.Free(actions);
            NativeMemory.Free(attributes);
            NativeMemory.Free(allSignals);
            NativeMemory.Free(noSignals);
            foreach (nint s in strings)
            {
                Marshal.FreeCoTaskMem(s);
            }
        }
    }

    public void Resize(int master, int columns, int rows) => SetSize(master, columns, rows);

    private static void SetSize(int master, int columns, int rows)
    {
        var size = new WinSize { Rows = (ushort)rows, Columns = (ushort)columns };
        _ = ioctl(master, TIOCSWINSZ, &size);
    }

    /// <summary>A NULL-terminated array of UTF-8 strings, every allocation recorded for the caller to free.</summary>
    private static nint* Strings(string[] values, List<nint> owned)
    {
        nint array = Marshal.AllocCoTaskMem((values.Length + 1) * sizeof(nint));
        owned.Add(array);
        var p = (nint*)array;
        for (int i = 0; i < values.Length; i++)
        {
            p[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
            owned.Add(p[i]);
        }

        p[values.Length] = 0;
        return p;
    }

    private static TerminalStartException Failed(string what) =>
        new($"The shell could not be started: {what} failed ({Marshal.GetLastPInvokeErrorMessage()}).");

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport(Lib, SetLastError = true)]
    private static partial int grantpt(int fd);

    [LibraryImport(Lib, SetLastError = true)]
    private static partial int unlockpt(int fd);

    [LibraryImport(Lib, SetLastError = true)]
    private static partial int ptsname_r(int fd, byte* buffer, nuint length);

    [LibraryImport(Lib, SetLastError = true)]
    private static partial int ioctl(int fd, nuint request, void* argument);

    [LibraryImport(Lib)]
    private static partial int sigfillset(void* set);

    [LibraryImport(Lib)]
    private static partial int sigemptyset(void* set);

    [LibraryImport(Lib)]
    private static partial int posix_spawn_file_actions_init(void* actions);

    [LibraryImport(Lib)]
    private static partial int posix_spawn_file_actions_destroy(void* actions);

    [LibraryImport(Lib)]
    private static partial int posix_spawn_file_actions_addopen(void* actions, int fd, byte* path, int flags, uint mode);

    [LibraryImport(Lib)]
    private static partial int posix_spawn_file_actions_adddup2(void* actions, int fd, int newFd);

    [LibraryImport(Lib)]
    private static partial int posix_spawn_file_actions_addclosefrom_np(void* actions, int from);

    [LibraryImport(Lib)]
    private static partial int posix_spawn_file_actions_addchdir_np(void* actions, byte* path);

    [LibraryImport(Lib)]
    private static partial int posix_spawnattr_init(void* attributes);

    [LibraryImport(Lib)]
    private static partial int posix_spawnattr_destroy(void* attributes);

    [LibraryImport(Lib)]
    private static partial int posix_spawnattr_setflags(void* attributes, short flags);

    [LibraryImport(Lib)]
    private static partial int posix_spawnattr_setsigdefault(void* attributes, void* set);

    [LibraryImport(Lib)]
    private static partial int posix_spawnattr_setsigmask(void* attributes, void* set);

    [LibraryImport(Lib)]
    private static partial int posix_spawn(int* pid, byte* path, void* actions, void* attributes, nint* argv, nint* envp);
}
