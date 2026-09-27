using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;

namespace DeskPair.Core.Ipc;

/// <summary>
/// Where the host service listens for its user-facing processes: a named pipe on Windows, a Unix domain
/// socket elsewhere. The name carries the product and an instance tag so tests and multiple installs do not collide.
/// </summary>
public sealed record IpcEndpoint(string Name)
{
    public static IpcEndpoint Default { get; } = new("DeskPair");

    public static IpcEndpoint ForTest() => new("DeskPair-test-" + Guid.NewGuid().ToString("N")[..8]);

    public string PipeName => Name;

    /// <summary>
    /// The per-user socket: one engine, started by the person using the machine, answering only to them.
    ///
    /// On macOS XDG_RUNTIME_DIR is never set by anything, so this is always the temporary directory -- and
    /// under launchd that is a per-user /var/folders sandbox at mode 0700, which is invisible to every
    /// other account including root. Which is the whole reason there is a second path below.
    /// </summary>
    public string UserSocketPath
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } xdg
                ? Path.Combine(xdg, "deskpair")
                : Path.Combine(Path.GetTempPath(), "deskpair-" + Environment.UserName);
            return Path.Combine(dir, Name + ".sock");
        }
    }

    /// <summary>
    /// The machine-wide socket, used when the engine runs as root.
    ///
    /// A root engine in the user's runtime directory is a contradiction: /run/user/0 is root's own, and the
    /// signed-in user cannot see into it, so the app and the engine would sit a metre apart and never find
    /// each other. Both of these are places root can create and everyone can reach.
    ///
    /// Well under the 104 bytes sun_path allows, on both. That limit is not a formality: a path over it is
    /// silently truncated, and the bind then succeeds against a name nobody will ever connect to.
    /// </summary>
    public string SystemSocketPath => OperatingSystem.IsMacOS()
        ? Path.Combine("/Library/Application Support/Sunllo/DeskPair", Name + ".sock")
        : Path.Combine("/run/deskpair", Name + ".sock");

    /// <summary>
    /// Whether this process listens machine-wide. Root does by default; the daemon's engine child does
    /// too, and says so with <c>--ipc-system</c>, because it is not root -- it has given that up before
    /// opening anything -- and would otherwise bind under a runtime directory it does not have, at a path
    /// on nobody's <see cref="ConnectPaths"/>. Explicit rather than probed so Windows and macOS keep the
    /// behaviour they have.
    /// </summary>
    public bool UseSystemPath { get; init; } = UnixPeer.IsRoot;

    /// <summary>Where this process listens, which depends on who it is.</summary>
    public string ListenPath => UseSystemPath ? SystemSocketPath : UserSocketPath;

    /// <summary>
    /// Where a client looks, in order: the machine-wide engine first, then one of this user's own.
    ///
    /// Both, always, and in that order, so that installing or removing the daemon does not need the app to
    /// be told. The daemon is the one that sees the login screen, so when both are listening it is the one
    /// worth attaching to.
    /// </summary>
    public IReadOnlyList<string> ConnectPaths => [SystemSocketPath, UserSocketPath];

    /// <summary>Accepts connections; each accepted <see cref="Stream"/> is owned by the caller.</summary>
    public IIpcListener Listen() => OperatingSystem.IsWindows() ? new PipeListener(this) : new SocketListener(this);

    public async Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        Exception? last = null;
        foreach (string path in ConnectPaths)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                // Nothing listening there, or nothing we may reach. Try the next one; the caller is told
                // about the last failure only if none of them answered.
                socket.Dispose();
                last = e;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.AddressNotAvailable);
    }

    private sealed class PipeListener(IpcEndpoint endpoint) : IIpcListener
    {
        public async Task<Stream> AcceptAsync(CancellationToken ct)
        {
            PipeSecurity? security = null;
            if (OperatingSystem.IsWindows())
            {
                // Only the current user (or SYSTEM when running as a service) and administrators may connect.
                security = new PipeSecurity();
                using WindowsIdentity me = WindowsIdentity.GetCurrent();
                security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
            }

            NamedPipeServerStream pipe = OperatingSystem.IsWindows() && security is not null
                ? NamedPipeServerStreamAcl.Create(endpoint.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security)
                : new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SocketListener : IIpcListener
    {
        private readonly Socket _socket;
        private readonly string _path;

        /// <summary>
        /// Binds where this process belongs, with the permissions that arrangement needs.
        ///
        /// A per-user socket is 0700/0600 and that is the whole of its security: only the account that owns
        /// the directory can open it, and that account is the one the engine is running as.
        ///
        /// The machine-wide socket cannot be. The person sitting at the screen has to reach an engine that
        /// root started, so the file mode stops being the gate and has to let them through. What replaces
        /// it is the kernel answering who connected -- see <see cref="IpcCaller"/> -- which is a stronger
        /// check than the mode ever was, because it identifies the caller rather than the directory they
        /// managed to get into. The token in the hello still has to be right as well.
        /// </summary>
        public SocketListener(IpcEndpoint endpoint)
        {
            _path = endpoint.ListenPath;
            bool shared = _path == endpoint.SystemSocketPath;
            string dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    dir,
                    shared
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _socket.Bind(new UnixDomainSocketEndPoint(_path));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    _path,
                    shared
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            _socket.Listen(16);
        }

        public async Task<Stream> AcceptAsync(CancellationToken ct)
        {
            Socket client = await _socket.AcceptAsync(ct).ConfigureAwait(false);
            return new NetworkStream(client, ownsSocket: true);
        }

        public ValueTask DisposeAsync()
        {
            _socket.Dispose();
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}

public interface IIpcListener : IAsyncDisposable
{
    Task<Stream> AcceptAsync(CancellationToken ct);
}
