#if WINDOWS
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using DeskPair.Desktop.Engine.Service;

namespace DeskPair.Desktop.Engine.Elevation;

/// <summary>
/// The three command-line roles the elevation flow uses, in order:
/// <list type="number">
/// <item><c>--elevate</c>: runs elevated (the person completed the UAC). It stands up a throwaway LocalSystem
///   service and starts it, waits for it to stop, and deletes it. Nothing else -- an admin process cannot put a
///   process into the interactive session itself, so it borrows SYSTEM for the one call that can.</item>
/// <item><c>--helper-launch</c>: the throwaway service, running as SYSTEM in session 0. It reuses the proven
///   <see cref="DesktopEngineLauncher"/> to put <c>--session-helper</c> into the interactive session, then stops
///   itself so <c>--elevate</c> can delete it.</item>
/// <item><c>--session-helper</c>: SYSTEM, in the interactive session, where the secure desktop actually is. It
///   connects back to the engine's pipe, proves it holds the token, and captures and injects on the engine's
///   behalf until the pipe closes.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ElevationRoles
{
    private const string DisplayName = "Sunllo DeskPair elevation helper";
    private const uint ServiceDemandStart = 0x00000003;

    public static int RunElevate(string pipe, string tokenFile, string? permanentPeerId, ILogger log)
    {
        int raised = RaiseHelperService(pipe, tokenFile, log);
        if (raised != 0)
        {
            return raised; // the temporary elevation failed; do not touch the machine's permanent state.
        }

        // Same UAC, so the person confirmed once: set this device up for unattended access. Best effort -- the
        // temporary elevation already succeeded, so a failure here is logged, not surfaced as a failed elevation.
        if (!string.IsNullOrEmpty(permanentPeerId))
        {
            MakePermanent(permanentPeerId, log);
        }

        return 0;
    }

    /// <summary>
    /// Installs the unattended SYSTEM service and turns on the "secure desktop for listed devices only" policy with
    /// this device on the list, so it keeps unattended access to the secure desktop after this connection ends.
    /// Already elevated here (the UAC that raised the helper), and both config and the service live where SYSTEM and
    /// the signed-in engine already share them.
    ///
    /// The service is installed but <b>not started</b>: a connection is being served right now by the app's own
    /// engine (over the temporary helper), and standing a second engine up beside it would make the two fight over
    /// the shared identity, port and pipe and drop the connection. Registered auto-start and left stopped, it takes
    /// over cleanly at the next restart -- which is what the person chose, "remembered even after a restart". Until
    /// then this connection stays seamless on the helper.
    /// </summary>
    private static void MakePermanent(string peerId, ILogger log)
    {
        try
        {
            string dataDir = ServerRole.DefaultDataDir();

            var store = new Core.Config.HostConfigStore(System.IO.Path.Combine(dataDir, "config.json"));
            Core.Config.HostConfig config = store.Load();
            var peers = config.AllowedPeers.ToList();
            string entry = "id:" + peerId;
            if (!peers.Any(p => string.Equals(p, entry, StringComparison.OrdinalIgnoreCase)))
            {
                peers.Add(entry);
            }

            store.Save(config with { AllowedPeers = peers, SecureDesktopForListedOnly = true });
            log.LogInformation("{Peer} may now see the secure desktop unattended; the listed-only policy is on", peerId);

            int code = HostServiceInstaller.Install(dataDir, log, startNow: false);
            if (code != 0)
            {
                log.LogWarning("The unattended service install returned {Code}; the policy is set but the service is not installed", code);
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not finish the permanent unattended setup for {Peer}", peerId);
        }
    }

    private static int RaiseHelperService(string pipe, string tokenFile, ILogger log)
    {
        string exe = Environment.ProcessPath ?? string.Empty;
        if (exe.Length == 0)
        {
            log.LogError("Could not determine the program path");
            return 2;
        }

        string serviceName = "SunlloDeskPairHelper_" + Guid.NewGuid().ToString("N")[..12];
        // The service name travels down so the helper-launch role can set ServiceBase.ServiceName to match what SCM
        // registered -- a mismatch is why a service "does not respond in a timely fashion".
        string binaryPath = "\"" + exe + "\" --helper-launch " + serviceName + " " + pipe + " \"" + tokenFile + "\"";

        nint manager = Advapi32.OpenSCManager(null, null, Advapi32.ScManagerConnect | Advapi32.ScManagerCreateService);
        if (manager == 0)
        {
            log.LogError("Could not open the service manager (error {Error})", Marshal.GetLastPInvokeError());
            return 2;
        }

        try
        {
            nint service = Advapi32.CreateService(
                manager, serviceName, DisplayName,
                Advapi32.ServiceStart | Advapi32.ServiceQueryStatus | Advapi32.ServiceStop | Advapi32.Delete,
                Advapi32.ServiceWin32OwnProcess, ServiceDemandStart, Advapi32.ServiceErrorNormal,
                binaryPath, null, 0, null, null /* LocalSystem */, null);
            if (service == 0)
            {
                log.LogError("Could not create the elevation helper service (error {Error})", Marshal.GetLastPInvokeError());
                return 2;
            }

            try
            {
                if (!Advapi32.StartService(service, 0, 0))
                {
                    log.LogError("Could not start the elevation helper service (error {Error})", Marshal.GetLastPInvokeError());
                    return 2;
                }

                WaitForStopped(service, TimeSpan.FromSeconds(30));
                log.LogInformation("Elevation helper launched into the interactive session");
                return 0;
            }
            finally
            {
                Advapi32.DeleteService(service);
                Advapi32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(manager);
        }
    }

    private static void WaitForStopped(nint service, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = default(Advapi32.ServiceStatus);
            if (!Advapi32.QueryServiceStatus(service, ref status) || status.CurrentState == Advapi32.StateStopped)
            {
                return;
            }

            Thread.Sleep(200);
        }
    }

    public static int RunHelperLaunch(string serviceName, string pipe, string tokenFile, ILogger log)
    {
        try
        {
            using var service = new HelperLaunchService(serviceName, pipe, tokenFile, log);
            ServiceBase.Run(service);
            return 0;
        }
        catch (Exception e)
        {
            log.LogCritical(e, "The elevation helper launch service stopped");
            return 1;
        }
    }

    /// <summary>One-shot SYSTEM service: put the helper into the interactive session, then stop so it can be deleted.</summary>
    private sealed class HelperLaunchService : ServiceBase
    {
        private readonly string _pipe;
        private readonly string _tokenFile;
        private readonly ILogger _log;

        public HelperLaunchService(string serviceName, string pipe, string tokenFile, ILogger log)
        {
            _pipe = pipe;
            _tokenFile = tokenFile;
            _log = log;
            ServiceName = serviceName; // must match the name SCM created the service under
            CanStop = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            // On its own thread so RUNNING is reported first; the launch is quick, and stopping ourselves is how
            // the elevated caller knows the helper is up and the service can go.
            _ = Task.Run(() =>
            {
                try
                {
                    uint? session = DesktopEngineLauncher.ConsoleSession();
                    if (session is null)
                    {
                        _log.LogError("No console session to put the helper in");
                    }
                    else
                    {
                        DesktopEngineLauncher.Engine? engine = DesktopEngineLauncher.Launch(session.Value, $"--session-helper {_pipe} \"{_tokenFile}\"", _log);
                        if (engine is null)
                        {
                            _log.LogError("Could not launch the session helper");
                        }
                        else
                        {
                            // The engine<->helper pipe is the helper's lifeline; this service does not supervise it.
                            engine.Dispose();
                        }
                    }
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Launching the session helper failed");
                }
                finally
                {
                    Stop();
                }
            });
        }
    }

    public static int RunSessionHelper(string pipe, string tokenFile, ILoggerFactory logs)
    {
        ILogger log = logs.CreateLogger("session-helper");
        try
        {
            return RunSessionHelperAsync(pipe, tokenFile, logs, log).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            log.LogError(e, "The session helper stopped");
            return 1;
        }
    }

    private static async Task<int> RunSessionHelperAsync(string pipe, string tokenFile, ILoggerFactory logs, ILogger log)
    {
        byte[] token;
        try
        {
            token = await File.ReadAllBytesAsync(tokenFile).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not read the elevation token");
            return 2;
        }

        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(15_000).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not connect back to the engine");
            return 2;
        }

        var channel = new HelperChannel(client);
        using (var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            bool proven;
            try
            {
                proven = await HelperHandshake.RunAsync(channel, token, handshakeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A wedged handshake must not leave this SYSTEM process running; exit so nothing lingers.
                log.LogError("The token handshake did not finish in time");
                return 2;
            }

            if (!proven)
            {
                log.LogError("The engine and the helper could not prove the shared token");
                return 2;
            }
        }

        var displays = new Platform.Windows.Capture.WindowsDisplayEnumerator();
        var capturers = new Platform.Windows.Capture.WindowsScreenCapturerFactory(displays, logs);
        var injector = new Platform.Windows.Input.WindowsInputInjector(logs.CreateLogger("helper-input"));
        var cursor = new Platform.Windows.Input.WindowsCursorProvider();
        try
        {
            log.LogInformation("Session helper connected; serving the secure desktop");
            var server = new SessionHelperServer(channel, displays, capturers, injector, cursor, log);
            await server.RunAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await injector.DisposeAsync().ConfigureAwait(false);
            await cursor.DisposeAsync().ConfigureAwait(false);
        }
    }
}
#endif
