using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Platform.MacOS.Native;
using DeskPair.Platform.Unix.Terminal;

namespace DeskPair.Platform.MacOS.Terminal;

/// <summary>
/// The engine's terminal on macOS: a shell on a pseudo-terminal, as the signed-in user the engine runs
/// as. macOS has no root path in this product -- the engine is a per-user agent, because screen-recording
/// consent belongs to a session -- so "highest" and "user" are the same account here, and the approval
/// card says which.
/// </summary>
public static class MacTerminal
{
    public static ITerminalHost Create(ILogger log) => new UnixTerminalHost(new MacPtySpawner(), log);
}

/// <summary>The shim does the start (see pty.m for why it has to be C); this marshals the strings.</summary>
internal sealed unsafe class MacPtySpawner : IPtySpawner
{
    public (int Master, int Pid) Spawn(string path, string[] argv, string[] env, string directory, int columns, int rows)
    {
        var owned = new List<nint>();
        try
        {
            nint* args = Strings(argv, owned);
            nint* envp = Strings(env, owned);
            byte[] file = Encoding.UTF8.GetBytes(path + '\0');
            byte[] cwd = Encoding.UTF8.GetBytes(directory + '\0');
            int error;
            int master;
            int pid;
            fixed (byte* f = file)
            fixed (byte* d = cwd)
            {
                error = MacShim.fd_pty_spawn(f, args, envp, d, columns, rows, out master, out pid);
            }

            return error == 0
                ? (master, pid)
                : throw new TerminalStartException($"The shell {path} could not be started: {Marshal.GetPInvokeErrorMessage(error)}.");
        }
        finally
        {
            foreach (nint s in owned)
            {
                Marshal.FreeCoTaskMem(s);
            }
        }
    }

    public void Resize(int master, int columns, int rows) => _ = MacShim.fd_pty_resize(master, columns, rows);

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
}
