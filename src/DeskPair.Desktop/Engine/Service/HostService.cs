using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Engine.Service;

/// <summary>
/// The service itself: the thing the service control manager starts, stops, and expects to answer.
///
/// It is deliberately not the engine. The engine has to run inside the interactive session, on whichever
/// desktop the person at the keyboard is actually looking at, and a service never is -- it runs in session
/// 0, where there are no pixels at all. What this does instead is keep an engine alive in the right place,
/// which is <see cref="EngineSupervisor"/>'s whole job.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HostService : ServiceBase
{
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _supervising;

    public HostService(ILogger log)
    {
        _log = log;
        ServiceName = HostServiceInstaller.ServiceName;

        // Stopping is the only control it accepts. Pause and continue would have to mean something for a
        // remote session in progress, and there is no honest answer to "paused" for one.
        CanStop = true;
        CanPauseAndContinue = false;
        CanShutdown = true;
        AutoLog = false;
    }

    /// <summary>Runs until the service control manager says to stop. Blocks, as a service main must.</summary>
    public static int Run(ILogger log)
    {
        try
        {
            using var service = new HostService(log);
            ServiceBase.Run(service);
            return 0;
        }
        catch (Exception e)
        {
            // A service that throws out of its main leaves error 1067 and nothing else. This at least
            // leaves a sentence in a file somebody can find.
            log.LogCritical(e, "The host service stopped");
            return 1;
        }
    }

    protected override void OnStart(string[] args)
    {
        _log.LogInformation(
            "Host service starting (version {Version}, process {Process})",
            App.Version,
            Environment.ProcessId);

        // OnStart has to return promptly or the service control manager calls the start failed, so the
        // watching happens on its own task and the handle is kept only to wait on at stop.
        var supervisor = new EngineSupervisor(_log);
        _supervising = Task.Run(() => supervisor.RunAsync(_stopping.Token), CancellationToken.None);
    }

    protected override void OnStop()
    {
        _log.LogInformation("Host service stopping");
        Settle(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The machine is going down. The same work as a stop, but Windows gives far less time for it, so
    /// anything slow belongs in neither.
    /// </summary>
    protected override void OnShutdown()
    {
        _log.LogInformation("Host service stopping: the computer is shutting down");
        Settle(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Asks the supervisor to stop and gives it a moment to end the engine it started.
    ///
    /// Bounded, because an engine that will not die must not keep the machine from shutting down, and a
    /// service that hangs in OnStop is the reason a restart takes five minutes. Whatever is left when the
    /// time runs out is Windows's to clean up.
    /// </summary>
    private void Settle(TimeSpan grace)
    {
        _stopping.Cancel();
        if (_supervising is { } supervising && !supervising.Wait(grace))
        {
            _log.LogWarning("The engine did not stop within {Grace}; leaving it to Windows", grace);
        }

        _supervising = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}
