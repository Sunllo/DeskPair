#if WINDOWS
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;

namespace DeskPair.Desktop.Engine.Elevation;

/// <summary>
/// The Windows elevator: on request it raises a SYSTEM helper that can see and drive the secure desktop, and it
/// wires the engine's switchable capture, input and cursor to it. The person at the host still completes the real
/// UAC -- this launches an elevated <c>--elevate</c> that stands up a throwaway SYSTEM service, which puts the
/// helper into the interactive session and then deletes itself. No UAC is bypassed; nothing here clicks it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSessionElevator : ISessionElevator
{
    // ERROR_CANCELLED: the person clicked No on the UAC prompt.
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan HelperConnectTimeout = TimeSpan.FromSeconds(20);
    // The token exchange is a handful of small messages; anything longer is a wedged peer, and we must not wait
    // on it forever (a hang here would leave the SYSTEM helper up and the engine unable to lower it).
    private static readonly TimeSpan HelperHandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly SwitchableScreenCapturerFactory _capturers;
    private readonly SwitchableInputInjector _injector;
    private readonly SwitchableCursorProvider _cursor;
    private readonly string _dataDir;
    private readonly ILogger _log;
    private readonly object _lock = new();

    private HelperClientLink? _link;
    private string? _tokenFile;

    public WindowsSessionElevator(SwitchableScreenCapturerFactory capturers, SwitchableInputInjector injector, SwitchableCursorProvider cursor, string dataDir, ILogger log)
    {
        _capturers = capturers;
        _injector = injector;
        _cursor = cursor;
        _dataDir = dataDir;
        _log = log;
    }

    public event Action? Ended;

    public async Task<bool> ElevateAsync(bool permanent, string? peerId, CancellationToken ct)
    {
        string pipeName = "deskpair-helper-" + Guid.NewGuid().ToString("N");
        byte[] token = RandomNumberGenerator.GetBytes(32);
        string tokenFile = WriteTokenFile(token);

        NamedPipeServerStream? server = null;
        try
        {
            server = CreatePipe(pipeName);
            Task connected = server.WaitForConnectionAsync(ct);

            // The elevated launcher stands up the SYSTEM service, which launches the helper, then deletes itself and
            // exits; a null exit code is the person cancelling the UAC, which is a plain "no".
            // The same UAC that raises the helper can also set this device up for unattended access, so the person
            // confirms once. The id is quoted and the elevated role validates it; the service install and the
            // config change are a best effort there and never fail the elevation.
            string install = permanent && !string.IsNullOrEmpty(peerId) ? $" --install-service --peer \"{peerId}\"" : string.Empty;
            int? exit = await RunElevatedAsync($"--elevate {pipeName} \"{tokenFile}\"{install}").ConfigureAwait(false);
            if (exit is null)
            {
                _log.LogInformation("The person at the host did not allow elevation (UAC cancelled)");
                return false;
            }

            if (exit != 0)
            {
                _log.LogWarning("The elevation launcher failed with exit code {Exit}", exit);
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HelperConnectTimeout);
            try
            {
                await connected.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("The elevation helper did not connect within {Seconds}s", HelperConnectTimeout.TotalSeconds);
                return false;
            }

            var channel = new HelperChannel(server);
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeout.CancelAfter(HelperHandshakeTimeout);
            bool proven;
            try
            {
                proven = await HelperHandshake.RunAsync(channel, token, handshakeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (handshakeTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _log.LogWarning("The elevation helper did not finish the token handshake within {Seconds}s", HelperHandshakeTimeout.TotalSeconds);
                return false;
            }

            if (!proven)
            {
                _log.LogWarning("The elevation helper failed the token handshake");
                return false;
            }

            var link = new HelperClientLink(channel, _log);
            link.Faulted += OnHelperGone;
            link.Start();
            lock (_lock)
            {
                _link = link;
            }

            _capturers.Attach(link);
            _injector.Attach(link);
            _cursor.Attach(link);
            server = null; // the link owns the pipe now
            _log.LogInformation("Elevation helper is up; capture, input and cursor now go through it");
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "Could not raise the elevation helper");
            return false;
        }
        finally
        {
            server?.Dispose();
            DeleteFile(tokenFile);
            lock (_lock)
            {
                if (_link is null)
                {
                    _tokenFile = null;
                }
            }
        }
    }

    public async Task LowerAsync()
    {
        HelperClientLink? link;
        lock (_lock)
        {
            link = _link;
            _link = null;
        }

        if (link is null)
        {
            return;
        }

        // Detach before the link is disposed: as its proxy capturers report the desktop switching, the engine
        // rebuilds the streams, and by then the factory hands out local capturers again.
        _capturers.Detach();
        _injector.Detach();
        _cursor.Detach();
        link.Faulted -= OnHelperGone;
        await link.DisposeAsync().ConfigureAwait(false);
        DeleteFile(_tokenFile);
        _tokenFile = null;
    }

    private void OnHelperGone()
    {
        HelperClientLink? link;
        lock (_lock)
        {
            link = _link;
            _link = null;
        }

        if (link is null)
        {
            return; // already lowered
        }

        _capturers.Detach();
        _injector.Detach();
        _cursor.Detach();
        _ = link.DisposeAsync();
        DeleteFile(_tokenFile);
        _tokenFile = null;
        Ended?.Invoke();
    }

    private static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID for the engine process");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Non-zero buffers matter for correctness, not just speed: with a zero-size buffer a WriteAsync blocks until
        // the peer reads, and the handshake has both ends write their nonce before either reads -- so zero buffers
        // deadlock it exactly like a flush would. Room for the helper->engine frames (in) and the small
        // engine->helper input (out); anything larger just streams through with back-pressure.
        const int frameBuffer = 1024 * 1024;
        const int inputBuffer = 64 * 1024;
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, frameBuffer, inputBuffer, security);
    }

    private string WriteTokenFile(byte[] token)
    {
        string dir = Path.Combine(_dataDir, "elevation");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".token");
        File.WriteAllBytes(file, token);
        lock (_lock)
        {
            _tokenFile = file;
        }

        return file;
    }

    private void DeleteFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Could not delete the elevation token file");
        }
    }

    private static async Task<int?> RunElevatedAsync(string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? string.Empty, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
    }
}
#endif
