using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Windows.Security;

namespace DeskPair.Desktop.Engine.Service;

/// <summary>
/// Installing, starting, stopping and removing the DeskPair host service.
///
/// The service exists for one capability the app cannot have: reading and driving the desk when nobody is
/// signed in to it. Windows draws the lock screen, the sign-in screen and every UAC prompt on the Winlogon
/// desktop, which a process running as an ordinary user may neither read nor type into -- so a session
/// opened while the machine is locked shows a black picture for as long as it stays locked. Only something
/// running as LocalSystem can put the engine where those pixels are.
///
/// That is a real increase in what this program can do while nobody is watching it, so it is never
/// installed by simply running DeskPair. It is installed when somebody asks for it, from an elevated
/// process, and removing it is a first-class operation rather than an afterthought -- a machine that has
/// been handed on should be able to stop being reachable without reinstalling Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HostServiceInstaller
{
    /// <summary>The key the service control manager knows it by. Never shown to anybody.</summary>
    public const string ServiceName = "SunlloDeskPair";

    /// <summary>What services.msc shows.</summary>
    public const string DisplayName = "Sunllo DeskPair";

    private const string Description =
        "Keeps Sunllo DeskPair able to show and control this computer while it is locked or nobody is signed in. " +
        "Removing this service leaves DeskPair working normally for whoever is signed in.";

    /// <summary>Whether the service is registered at all.</summary>
    public static bool IsInstalled()
    {
        nint manager = Advapi32.OpenSCManager(null, null, Advapi32.ScManagerConnect);
        if (manager == 0)
        {
            return false;
        }

        try
        {
            nint service = Advapi32.OpenService(manager, ServiceName, Advapi32.ServiceQueryStatus);
            if (service == 0)
            {
                return false;
            }

            Advapi32.CloseServiceHandle(service);
            return true;
        }
        finally
        {
            Advapi32.CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Whether it is registered and running, or on its way: registered is not enough, since a stopped service puts
    /// no engine anywhere and an app attached to it is attached to nothing.
    /// </summary>
    public static bool IsRunning()
    {
        nint manager = Advapi32.OpenSCManager(null, null, Advapi32.ScManagerConnect);
        if (manager == 0)
        {
            return false;
        }

        try
        {
            nint service = Advapi32.OpenService(manager, ServiceName, Advapi32.ServiceQueryStatus);
            if (service == 0)
            {
                return false;
            }

            try
            {
                var status = default(Advapi32.ServiceStatus);
                return Advapi32.QueryServiceStatus(service, ref status)
                    && status.CurrentState is Advapi32.StateRunning or Advapi32.StateStartPending;
            }
            finally
            {
                Advapi32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Whether installing the service would actually make this desk reachable.
    ///
    /// The point of the service is to be there when nobody is. Both of the ways in that work while
    /// somebody is sitting here stop working when nobody is: the temporary password rotates and is read
    /// off the screen, which is exactly what a locked machine does not show, and click-to-accept needs a
    /// person and a connection manager, which is the app, which is not running when nobody is signed in.
    ///
    /// So without a permanent password the service is not a weaker arrangement, it is an inert one -- it
    /// would sit there, start at boot, hold an engine on the lock screen, and refuse every connection.
    /// Saying so now is better than installing something that looks like it worked.
    /// </summary>
    private static bool CanBeReachedUnattended(string dataDir, ILogger log)
    {
        // Readable, not merely present. A file that is there and cannot be opened is not a password this
        // machine has -- and a permanent hash is computed against the salt beside it, so one that
        // outlived its salt would never match anything either.
        try
        {
            var store = new WindowsSecretStore(Path.Combine(dataDir, "secrets"), machineScope: false);
            if (store.GetAsync("password-permanent-h1").AsTask().GetAwaiter().GetResult() is not null
                && store.GetAsync("password-salt").AsTask().GetAwaiter().GetResult() is not null)
            {
                return true;
            }
        }
        catch (SecretUnreadableException e)
        {
            log.LogError("This computer's {Key} belongs to another account; sort that out first.", e.Key);
            return false;
        }

        log.LogError(
            "This computer has no permanent password, so the service would have no way to let anybody in "
            + "while it is locked: the temporary password is read off the screen, and click-to-accept "
            + "needs somebody signed in. Set one in Settings, then install the service.");
        return false;
    }

    /// <summary>
    /// Hands this machine's secrets from the signed-in user to LocalSystem, once, before the service that
    /// will need them exists.
    ///
    /// The engine's identity key, its password salt and its permanent password hash are DPAPI-protected.
    /// Under the user scope LocalSystem cannot read them -- not "reads an older format", cannot decrypt
    /// them at all -- so a service started against a user's store finds nothing and, before this was
    /// understood, wrote its own over the top. That cost a host the identity it was reachable by: its id
    /// at the rendezvous server, its saved fingerprint on every device that had trusted it, and its
    /// permanent password, which is keyed to the salt that went with it.
    ///
    /// This runs elevated but still as that user, which is the one moment both halves are possible: read
    /// under their scope, write under the machine's. Re-reading is how it checks, rather than trusting
    /// that the write succeeded.
    /// </summary>
    private static void HandOverSecrets(string dataDir, ILogger log)
    {
        string directory = Path.Combine(dataDir, "secrets");
        if (!Directory.Exists(directory))
        {
            return;
        }

        // Read as ourselves, write for the machine, and do not try to work out which are already done.
        //
        // There is no way to ask: DPAPI's Unprotect ignores the scope it is given and works out which
        // master key a blob needs from the blob, so "can I read this as the machine scope" is answered
        // yes for anything this user can read at all. The first version asked exactly that, concluded
        // every secret had already been handed over, migrated nothing, and left the service's engine
        // restarting every six seconds because it could not read one of them.
        //
        // Rewriting one that was already machine-scoped costs a read and a write and changes nothing.
        var user = new WindowsSecretStore(directory, machineScope: false);
        var machine = new WindowsSecretStore(directory, machineScope: true);
        foreach (string file in Directory.GetFiles(directory, "*.dpapi"))
        {
            string key = Path.GetFileNameWithoutExtension(file);
            byte[]? value;
            try
            {
                value = user.GetAsync(key).AsTask().GetAwaiter().GetResult();
            }
            catch (SecretUnreadableException)
            {
                // Not ours at all -- another account's. Leave it alone and say so: overwriting somebody
                // else's secret is exactly the damage this whole method exists to undo.
                log.LogWarning("The secret {Key} belongs to another account; leaving it alone", key);
                continue;
            }

            if (value is null)
            {
                continue;
            }

            machine.SetAsync(key, value).AsTask().GetAwaiter().GetResult();
            log.LogInformation("Handed the secret {Key} to the service", key);
        }
    }

    /// <summary>
    /// Registers the service against this executable and, unless <paramref name="startNow"/> is false, starts it.
    /// Needs administrator rights.
    ///
    /// <paramref name="startNow"/> is false for the "make a device permanent" flow: a connection is being served
    /// right now by the app's own engine (with a temporary helper on the secure desktop), and starting the service
    /// here would stand a second engine up beside it -- two engines sharing the data directory take the same
    /// identity, want the same direct-access port and race for the same pipe, and <see cref="EngineHost"/> would
    /// hand the engine over mid-connection, dropping it. So the service is registered auto-start and left stopped;
    /// it takes over cleanly at the next restart, when the app's engine is gone. It still comes up on its own after
    /// a reboot, which is what "unattended" means.
    /// </summary>
    /// <returns>A process exit code: 0 installed, 2 refused or failed.</returns>
    public static int Install(string dataDir, ILogger log, bool startNow = true)
    {
        string exe = Environment.ProcessPath ?? string.Empty;
        if (exe.Length == 0)
        {
            log.LogError("Could not determine the program path.");
            return 2;
        }

        // Elevation first, before anything with a side effect. A run that is going to be refused for
        // being unprivileged should not have rewritten this machine's secrets on the way to finding out.
        nint manager = Advapi32.OpenSCManager(null, null, Advapi32.ScManagerConnect | Advapi32.ScManagerCreateService);
        if (manager == 0)
        {
            return Refuse(log, "open the service control manager");
        }

        try
        {
            if (!CanBeReachedUnattended(dataDir, log))
            {
                return 2;
            }

            // Before the service exists, because afterwards it is LocalSystem asking and the answer is no.
            HandOverSecrets(dataDir, log);

            // The quotes matter. Without them Windows reads a path containing a space as a program name
            // followed by arguments, and will run whatever sits at the first token -- the unquoted service
            // path that still turns up in every security audit.
            string binary = "\"" + exe + "\" --service";
            nint service = Advapi32.CreateService(
                manager,
                ServiceName,
                DisplayName,
                Advapi32.ServiceQueryConfig | Advapi32.ServiceChangeConfig | Advapi32.ServiceQueryStatus | Advapi32.ServiceStart,
                Advapi32.ServiceWin32OwnProcess,
                Advapi32.ServiceAutoStart,
                Advapi32.ServiceErrorNormal,
                binary,
                null,
                0,
                null,
                null, // LocalSystem
                null);

            if (service == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == Advapi32.ErrorServiceExists)
                {
                    if (!startNow)
                    {
                        // Already registered; leave its state alone. Deferred means "do not start a second engine
                        // now" -- a running service keeps running, a stopped one takes over at the next restart.
                        log.LogInformation("The {Service} service is already installed; it takes over at the next restart", ServiceName);
                        return 0;
                    }

                    // Already registered, which is not the same as already working: a service left
                    // stopped by a previous run would be reported as installed and do nothing.
                    return StartExisting(manager, log);
                }

                if (error == Advapi32.ErrorServiceMarkedForDelete)
                {
                    log.LogError(
                        "The {Service} service is still being removed. Restart the computer and install it again.",
                        ServiceName);
                    return 2;
                }

                return Refuse(log, "create the service", error);
            }

            try
            {
                Describe(service, log);
                RestartOnFailure(service, log);
                AllowSoftwareSas(allow: true, log);
                if (!startNow)
                {
                    // Registered auto-start but deliberately not started: the app's engine is serving a connection
                    // right now, and the service takes over at the next restart rather than fighting it.
                    log.LogInformation("The {Service} service is installed; it takes over at the next restart", ServiceName);
                    return 0;
                }

                if (!Advapi32.StartService(service, 0, 0))
                {
                    return Refuse(log, "start the service");
                }

                log.LogInformation("The {Service} service is installed and running", ServiceName);
                return 0;
            }
            finally
            {
                Advapi32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Stops the service and unregisters it. Needs administrator rights. Removing something that is not
    /// there succeeds: the caller asked for it to be gone, and it is.
    /// </summary>
    public static int Uninstall(ILogger log)
    {
        nint manager = Advapi32.OpenSCManager(null, null, Advapi32.ScManagerConnect);
        if (manager == 0)
        {
            return Refuse(log, "open the service control manager");
        }

        try
        {
            nint service = Advapi32.OpenService(
                manager,
                ServiceName,
                Advapi32.ServiceStop | Advapi32.ServiceQueryStatus | Advapi32.Delete);
            if (service == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == Advapi32.ErrorServiceDoesNotExist)
                {
                    log.LogInformation("The {Service} service is not installed", ServiceName);
                    return 0;
                }

                return Refuse(log, "open the service", error);
            }

            try
            {
                // Stop it, and wait until it has actually stopped.
                //
                // ControlService only asks; the service goes to STOP_PENDING and gets on with it. Deleting
                // a service that is still running marks it for deletion and leaves it registered and its
                // executable locked until it exits -- which looked, from outside, exactly like a removal
                // that silently did not happen. Waiting is also what the caller wants: the next thing
                // somebody does after removing this is replace or delete the program.
                Stop(service, log);

                if (!Advapi32.DeleteService(service))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == Advapi32.ErrorServiceMarkedForDelete)
                    {
                        log.LogInformation("The {Service} service is already being removed", ServiceName);
                        return 0;
                    }

                    return Refuse(log, "remove the service", error);
                }

                AllowSoftwareSas(allow: false, log);
                log.LogInformation("The {Service} service has been removed", ServiceName);
                return 0;
            }
            finally
            {
                Advapi32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(manager);
        }
    }

    /// <summary>Starts a service that is already registered, so that "install" always means "running".</summary>
    private static int StartExisting(nint manager, ILogger log)
    {
        nint service = Advapi32.OpenService(manager, ServiceName, Advapi32.ServiceQueryStatus | Advapi32.ServiceStart);
        if (service == 0)
        {
            return Refuse(log, "open the service that is already installed");
        }

        try
        {
            var status = default(Advapi32.ServiceStatus);
            if (Advapi32.QueryServiceStatus(service, ref status) && status.CurrentState == Advapi32.StateRunning)
            {
                log.LogInformation("The {Service} service is already installed and running", ServiceName);
                return 0;
            }

            if (!Advapi32.StartService(service, 0, 0))
            {
                return Refuse(log, "start the service that is already installed");
            }

            log.LogInformation("The {Service} service was already installed, and is now running", ServiceName);
            return 0;
        }
        finally
        {
            Advapi32.CloseServiceHandle(service);
        }
    }

    /// <summary>
    /// Asks the service to stop and waits for it, up to <see cref="StopTimeout"/>.
    /// </summary>
    private static void Stop(nint service, ILogger log)
    {
        var status = default(Advapi32.ServiceStatus);
        if (!Advapi32.QueryServiceStatus(service, ref status))
        {
            log.LogWarning("Could not read the service state (error {Error})", Marshal.GetLastPInvokeError());
            return;
        }

        if (status.CurrentState == Advapi32.StateStopped)
        {
            return;
        }

        if (!Advapi32.ControlService(service, Advapi32.ControlStop, ref status))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != Advapi32.ErrorServiceNotActive)
            {
                log.LogWarning("Could not stop the service (error {Error}); removing it anyway", error);
            }

            return;
        }

        DateTime deadline = DateTime.UtcNow + StopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
            if (!Advapi32.QueryServiceStatus(service, ref status) || status.CurrentState == Advapi32.StateStopped)
            {
                return;
            }
        }

        log.LogWarning("The service is still stopping after {Timeout}; removing it anyway", StopTimeout);
    }

    /// <summary>
    /// Long enough for the service's own ten-second grace for the engine, and a little over. Shorter and
    /// this would routinely give up on a stop that was going to succeed.
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    private const string SasPolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string SasPolicyValue = "SoftwareSASGeneration";

    /// <summary>
    /// Lets a service generate Ctrl+Alt+Del, and takes the permission away again when it is removed.
    ///
    /// The secure attention sequence cannot be synthesised with SendInput -- that is the point of it, and
    /// why the sign-in screen asks for it. <c>SendSAS</c> exists for programs that have a reason, and
    /// Windows only honours it when this policy says a service may: value 1 is services, 2 is ordinary
    /// applications, 3 is both. Only 1 is wanted here, and only while the service exists.
    ///
    /// It is a real, if narrow, loosening of a protection, so it is tied to the service's lifetime rather
    /// than set once and left behind on a machine that no longer runs this.
    /// </summary>
    private static void AllowSoftwareSas(bool allow, ILogger log)
    {
        try
        {
            using Microsoft.Win32.RegistryKey key =
                Microsoft.Win32.Registry.LocalMachine.CreateSubKey(SasPolicyKey, writable: true);
            if (allow)
            {
                key.SetValue(SasPolicyValue, 1, Microsoft.Win32.RegistryValueKind.DWord);
                log.LogInformation("Ctrl+Alt+Del may now be sent by this service");
            }
            else if (key.GetValue(SasPolicyValue) is int existing)
            {
                // Only the setting this put there. An administrator who wanted 2 or 3 for their own
                // reasons chose that, and removing this service is not an argument against it.
                if (existing == 1)
                {
                    key.DeleteValue(SasPolicyValue, throwOnMissingValue: false);
                    log.LogInformation("Ctrl+Alt+Del may no longer be sent by this service");
                }
                else
                {
                    log.LogInformation(
                        "Leaving {Value} at {Existing}: it was not set to what this service asked for",
                        SasPolicyValue,
                        existing);
                }
            }
        }
        catch (Exception e)
        {
            // Not fatal in either direction. Without it Ctrl+Alt+Del fails and says so, which is a
            // smaller problem than a service that will not install.
            log.LogWarning(e, "Could not change the {Value} policy", SasPolicyValue);
        }
    }

    /// <summary>The sentence services.msc shows, which is the only place most people will meet this.</summary>
    private static void Describe(nint service, ILogger log)
    {
        nint text = Marshal.StringToHGlobalUni(Description);
        nint block = Marshal.AllocHGlobal(Marshal.SizeOf<Advapi32.ServiceDescription>());
        try
        {
            Marshal.StructureToPtr(new Advapi32.ServiceDescription { Description = text }, block, false);
            if (!Advapi32.ChangeServiceConfig2(service, Advapi32.ConfigDescription, block))
            {
                log.LogDebug("Could not set the service description (error {Error})", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
            Marshal.FreeHGlobal(text);
        }
    }

    /// <summary>
    /// Come back after a crash.
    ///
    /// A service whose whole purpose is to be there when nobody is looking cannot be left stopped by one
    /// bad frame. Three attempts at widening intervals, then leave it alone: something that has failed four
    /// times in a row will not be fixed by a fifth start, and a tight restart loop hides the fault that
    /// caused it.
    /// </summary>
    private static void RestartOnFailure(nint service, ILogger log)
    {
        Advapi32.ScAction[] actions =
        [
            new() { Type = Advapi32.ActionRestart, Delay = 5_000 },
            new() { Type = Advapi32.ActionRestart, Delay = 30_000 },
            new() { Type = Advapi32.ActionRestart, Delay = 120_000 },
        ];

        int stride = Marshal.SizeOf<Advapi32.ScAction>();
        nint list = Marshal.AllocHGlobal(stride * actions.Length);
        nint block = Marshal.AllocHGlobal(Marshal.SizeOf<Advapi32.ServiceFailureActions>());
        try
        {
            for (int i = 0; i < actions.Length; i++)
            {
                Marshal.StructureToPtr(actions[i], list + (i * stride), false);
            }

            Marshal.StructureToPtr(
                new Advapi32.ServiceFailureActions
                {
                    ResetPeriod = 86_400,
                    RebootMessage = 0,
                    Command = 0,
                    ActionCount = (uint)actions.Length,
                    Actions = list,
                },
                block,
                false);

            if (!Advapi32.ChangeServiceConfig2(service, Advapi32.ConfigFailureActions, block))
            {
                log.LogDebug("Could not set the service recovery actions (error {Error})", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
            Marshal.FreeHGlobal(list);
        }
    }

    private static int Refuse(ILogger log, string what, int? error = null)
    {
        int code = error ?? Marshal.GetLastPInvokeError();
        if (code == Advapi32.ErrorAccessDenied)
        {
            log.LogError("Could not {What}: this has to be run as an administrator.", what);
        }
        else
        {
            log.LogError("Could not {What} (error {Error}).", what, code);
        }

        return 2;
    }
}
