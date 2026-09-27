using DeskPair.Core.Ipc;
using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Linux.Input;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Engine.LinuxService;

/// <summary>
/// The <c>--service</c> role on Linux: root, small, and for ever.
///
/// It does the three things that need root and hands the results to an engine that does not have it:
/// it opens the card and exports what is on screen, it creates the virtual keyboard and pointer, and it
/// starts the engine with all of that inherited. Then it watches. It links no GPU library and speaks to
/// no network; the engine on the other end of the socketpair does both, as an ordinary account.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class LinuxHostDaemon
{
    /// <summary>Where the engine's socket and the per-user token directories live; tmpfs, gone at boot.</summary>
    public const string RuntimeDirectory = "/run/deskpair";

    public static async Task<int> RunAsync(string dataDir, ILoggerFactory logs, CancellationToken ct)
    {
        ILogger log = logs.CreateLogger("daemon");
        if (NativeUser.Geteuid() != 0)
        {
            log.LogError("The daemon has to run as root: it reads the display hardware and creates input devices. Start it through systemd.");
            return 2;
        }

        (uint Uid, uint Gid)? account = LinuxAccounts.Lookup(LinuxAccounts.EngineUser);
        if (account is null)
        {
            log.LogError("There is no '{User}' account to run the engine as. `--install-service` creates it.", LinuxAccounts.EngineUser);
            return 2;
        }

        // The engine listens under /run/deskpair, and it is the engine's account that has to be able to
        // create the socket there. systemd makes the directory for root; the daemon hands it over.
        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            File.SetUnixFileMode(RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            _ = LinuxAccounts.Chown(RuntimeDirectory, account.Value.Uid, account.Value.Gid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogError(e, "Could not prepare {Directory}; the engine will not be able to listen for the app", RuntimeDirectory);
            return 2;
        }

        // Neither the card nor the input devices are a condition of running. A server with no GPU and no
        // /dev/uinput is exactly the machine somebody wants a terminal on, and it has to be reachable
        // before it can be given one; the engine says "no screen" to whoever connects.
        DrmScanoutReader? reader = null;
        string? card = FindCard(log);
        if (card is null)
        {
            log.LogWarning("No DRM card with a connected display under /dev/dri: this machine has no screen to show. It stays reachable; viewers are told there is no display.");
        }
        else
        {
            reader = new DrmScanoutReader(card, logs.CreateLogger("scanout"));
            log.LogInformation("Reading the screen from {Card} ({Driver})", card, reader.Driver);
        }

        using (reader)
        {
            UinputDevice? input = null;
            try
            {
                input = UinputDevice.Create(logs.CreateLogger("uinput"));
            }
            catch (IOException e)
            {
                log.LogWarning("No virtual input devices ({Reason}): keyboard and pointer from viewers are ignored on this machine. `modprobe uinput` gives it some.", e.Message);
            }

            using (input)
            {
                string token = Convert.ToHexString(IpcServer.NewToken());
                var launcher = new SessionEngineLauncher(Environment.ProcessPath!, dataDir, reader, input, account.Value, token, logs, new Platform.Linux.Wayland.Agent.AgentHandoff());
                using var supervisor = new SessionEngineSupervisor(launcher, logs.CreateLogger("supervisor"));
                log.LogInformation("Daemon {Version} running as pid {Pid}", App.Version, Environment.ProcessId);
                await supervisor.RunAsync(ct).ConfigureAwait(false);
                return 0;
            }
        }
    }


    /// <summary>The first card with a connected connector: on the machines this ships to there is one.</summary>
    private static string? FindCard(ILogger log)
    {
        if (!Directory.Exists("/dev/dri"))
        {
            return null;
        }

        foreach (string node in Directory.GetFiles("/dev/dri", "card*").Order(StringComparer.Ordinal))
        {
            try
            {
                using var probe = new DrmScanoutReader(node, log);
                if (probe.HasConnectedDisplay())
                {
                    return node;
                }
            }
            catch (IOException e)
            {
                log.LogDebug("Skipping {Node}: {Reason}", node, e.Message);
            }
        }

        return null;
    }
}
