using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Core.Ipc;
using DeskPair.Core.Services;
using DeskPair.Core.Session;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Desktop.Engine;

/// <summary>
/// The host engine: loads identity and config, wires the platform module, registers with the rendezvous
/// server, listens for direct connections and serves the UI over IPC.
///
/// It runs inside the app rather than beside it. macOS grants Screen Recording and Accessibility per
/// executable, so a second binary is a second consent the user would have to find and grant — and until they
/// did, capture produced nothing and the viewer saw a black screen. One process means one identity and one
/// set of permissions on every platform.
/// </summary>
public static class ServerRole
{
    /// <param name="connectionManagerLauncher">
    /// Opens the connection manager when a connection needs approving and none is attached. Null (the
    /// headless <c>--server</c> role) means there is nobody to ask, so click-to-accept fails closed.
    /// </param>
    public static async Task<int> RunAsync(string[] args, ILoggerFactory logs, CancellationToken ct, Func<CancellationToken, Task>? connectionManagerLauncher = null)
    {
        ILogger log = logs.CreateLogger("server");
        string dataDir = Arg(args, "--data") ?? DefaultDataDir();
        string configPath = Arg(args, "--config") ?? Path.Combine(dataDir, "config.json");
        var configStore = new HostConfigStore(configPath);
        HostConfig config = configStore.Load();

        // Where to signal. A configured address always wins; otherwise, unless the operator turned it off,
        // ask the portal. The answer is used and not written back: storing it would freeze today's address
        // into this installation, which is the thing asking was meant to avoid.
        //
        // A portal that does not answer is not an error. It means direct connections only -- the same state
        // this ran in before there was a directory -- so the host still starts, still has an id, and still
        // works on a local network.
        var directory = Core.Portal.NetworkDirectory.None;
        if (config.RendezvousServer.Length == 0 && config.UseDirectoryServers)
        {
            using var client = new Core.Portal.NetworkDirectoryClient();
            directory = await client.FetchAsync(Arg(args, "--portal") ?? config.PortalServer, ct);
        }

        string rendezvous = config.RendezvousServer.Length > 0 ? config.RendezvousServer : directory.Rendezvous;
        if (rendezvous.Length == 0)
        {
            log.LogWarning(
                config.UseDirectoryServers
                    ? "No signalling server: none is configured in {Path} and the portal did not name one. Direct connections only."
                    : "No signalling server configured in {Path}, and the directory is turned off. Direct connections only.",
                configPath);
        }
        else if (config.RendezvousServer.Length == 0)
        {
            log.LogInformation("Signalling server {Server}, from the portal's directory", rendezvous);
        }

        // Before anything reads a secret, and before anything decides there is none. An upgraded machine
        // has its identity under the product's previous name, and "there is none" is what makes a host
        // mint a new one and become a different machine.
        Services.LegacyIdentity.Migrate(dataDir, log);

        PlatformServices platform = PlatformServices.Create(dataDir, logs, PlatformServices.SyntheticAllowed(args), PlatformServices.X11Requested(args));
        ISecretStore secrets = platform.SecretStore;
        using PeerIdentityStore identity = await PeerIdentityStore.LoadOrCreateAsync(secrets, platform.MachineId, ct);
        HostPasswords passwords = await HostPasswords.LoadAsync(secrets, ct);

        passwords.Configure(config.TemporaryPasswordLength, config.TemporaryRotationThreshold, config.TemporaryPasswordEnabled);

        var bridge = new HostIpcBridge(configStore, logs.CreateLogger<HostIpcBridge>());
        await using var media = new HostMediaModule(platform.Host, logs)
        {
            // "auto" is now a real answer rather than a synonym for H.264: it leaves the codec to negotiation
            // between what this machine can encode and what each viewer says it can decode.
            Codec = config.CodecPreference.ToLowerInvariant() switch
            {
                "h264" => Platform.Abstractions.Codec.VideoCodec.H264,
                "h265" => Platform.Abstractions.Codec.VideoCodec.H265,
                "av1" => Platform.Abstractions.Codec.VideoCodec.Av1,
                "vp9" => Platform.Abstractions.Codec.VideoCodec.Vp9,
                _ => null,
            },
            MaxDisplaysPerViewer = config.MaxDisplaysPerViewer,
            MaxConcurrentStreams = config.MaxConcurrentStreams,
            AllowVirtualDisplays = config.AllowVirtualDisplay,
            AllowSessionScreen = config.PrivateSessionScreen,
        };
        await using var files = new HostFileModule(Core.FileTransfer.LocalFileSystem.Instance, logs)
        {
            // Confine file transfer to the configured roots (null when unrestricted). Read per session when the
            // engine is built, so a change applies to the next transfer.
            IsPathAllowed = config.BuildFileTransferGuard(),
        };
        // The terminal, where the platform has one; where it does not yet, a module that says so to the viewer.
        await using var terminals = new HostTerminalModule(
            platform.Terminal ?? new Platform.Abstractions.Terminal.UnavailableTerminalHost("This computer's DeskPair cannot open a terminal on this platform yet."), logs)
        {
            RunAs = config.ToPolicy().TerminalRunAs,
        };
        bridge.TerminalIdentity = terminals.DescribeIdentity;
        bridge.DesktopSharing = platform.Host.DisplaySession as Platform.Abstractions.Capture.IDesktopSharingConsent;
        var handlers = new List<ISessionHandler<HostSessionContext>>(media.Handlers) { files, terminals };
        string version = typeof(ServerRole).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        await using var runtime = new HostRuntime(config.ToPeerSettings(version, directory.Rendezvous, directory.PublicKey), identity, passwords, config.ToPolicy(), bridge, logs, extraHandlers: handlers)
        {
            DirectAccessPort = config.DirectAccessEnabled ? config.DirectAccessPort : 0,
        };
        media.EnableFilePromises(files);
        media.Attach(runtime);
        files.Attach(runtime);
        terminals.Attach(runtime);
        runtime.PreferRelay = config.ForceRelay;
        ApplyAllowlist(runtime, config, log);

        // Who connected to this computer, kept beside the engine's own data rather than in the user's
        // profile: the record belongs to the machine, and under the service the engine is not the user.
        var journal = new ConnectionJournal(Path.Combine(dataDir, "connections.jsonl"), TimeProvider.System, logs.CreateLogger("journal"));
        journal.Attach(runtime);
        bridge.Journal = journal;

        // Settings saved from the UI apply to the running engine; only the fields baked into the transport at
        // start-up (servers, direct-access port, UDP media, codec) need a restart, and the UI says so.
        HostConfig current = config;
        runtime.SessionClosing += session =>
        {
            // A password that was shared for one session should not open the next one.
            if (current.RotateTemporaryAfterSession && !passwords.TemporaryPinned && session.Context.ConnType == Protocol.Rendezvous.ConnType.ConnRemote)
            {
                passwords.RotateTemporary();
            }

            return Task.CompletedTask;
        };

        bridge.ConfigChanged += updated =>
        {
            current = updated;
            runtime.Policy = updated.ToPolicy();
            runtime.PreferRelay = updated.ForceRelay;
            ApplyAllowlist(runtime, updated, log);
            files.IsPathAllowed = updated.BuildFileTransferGuard();
            terminals.RunAs = runtime.Policy.TerminalRunAs;
            media.MaxDisplaysPerViewer = updated.MaxDisplaysPerViewer;
            media.MaxConcurrentStreams = updated.MaxConcurrentStreams;
            media.AllowVirtualDisplays = updated.AllowVirtualDisplay;
            media.AllowSessionScreen = updated.PrivateSessionScreen;
            passwords.Configure(updated.TemporaryPasswordLength, updated.TemporaryRotationThreshold, updated.TemporaryPasswordEnabled);
            if (HostConfig.RequiresEngineRestart(config, updated))
            {
                log.LogInformation("Settings changed that need DeskPair restarted to take effect");
            }
        };

        string tokenHex = Arg(args, "--ipc-token") ?? Environment.GetEnvironmentVariable(PlatformServices.IpcTokenVariable) ?? Convert.ToHexString(IpcServer.NewToken());
        byte[] token = Convert.FromHexString(tokenHex);

        // Read once and gone: the daemon hands the token over in the environment because the command line
        // is public, and nothing this engine starts (the connection manager, a script) needs to inherit it.
        Environment.SetEnvironmentVariable(PlatformServices.IpcTokenVariable, null);
        // --ipc-system: the daemon's child listens where the daemon would, since the app looks there first.
        IpcEndpoint endpoint = Array.IndexOf(args, "--ipc-system") >= 0
            ? IpcEndpoint.Default with { UseSystemPath = true }
            : IpcEndpoint.Default;
        await using var ipc = new IpcServer(endpoint, token, bridge, logs.CreateLogger<IpcServer>());
        bridge.Attach(runtime, ipc);
        bridge.AttachFileModule(files);
        bridge.ConnectionManagerLauncher = connectionManagerLauncher;
        ipc.Start();
        await WriteTokenAsync(Arg(args, "--token-path") ?? Path.Combine(dataDir, "ipc.token"), tokenHex, log, ct);

        runtime.Rendezvous.IdAssigned += id => log.LogInformation("ID: {Id}", id);
        await runtime.StartAsync(ct);
        // The temporary password is not logged. server.log is world-readable under the default ACL and is
        // kept through eight rotations; the password belongs on the screen and nowhere else.
        log.LogInformation("Host running: id={Id} data={Data}", identity.IsLocalId ? $"{identity.Id} (local, no server id yet)" : identity.Id, dataDir);

        // The engine lives and dies with the process that hosts it, so there is nothing to watch: cancelling
        // is the only way out, whether that is Ctrl+C in the headless role or the app shutting down.
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    /// <summary>
    /// Leaves the IPC token where the clients of this engine will look for it.
    ///
    /// The path is an argument because the data directory is not always somewhere the engine may write.
    /// A supervisor that drops its engine to the session user hands it --data /var/lib/deskpair, which is
    /// root's and mode 0700, and an engine that threw here would be restarted by that same supervisor a
    /// few seconds later, for ever, with the reason buried in a log nobody is reading. Windows has already
    /// been through one restart loop of exactly that shape.
    ///
    /// Deleted before it is written, rather than overwritten. Overwriting needs write permission on the
    /// file, which is the wrong question: a token root left behind at the login window has to be replaced
    /// by the user's own engine once somebody signs in, and that user owns the directory rather than the
    /// file. Deleting and creating puts the new file in the hands of whoever wrote it.
    /// </summary>
    internal static async Task WriteTokenAsync(string path, string tokenHex, ILogger log, CancellationToken ct)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
            }

            File.Delete(path);
            await File.WriteAllTextAsync(path, tokenHex, ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The engine still works; what stops working is anything that finds it by reading this file.
            // Said out loud, because the symptom is a UI that never connects to an engine that is running.
            log.LogError(e, "Could not write the IPC token to {Path}; nothing will be able to attach to this engine unless it is given the token another way", path);
        }
    }

    internal static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    internal static string DefaultDataDir()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sunllo", "DeskPair");
        }

        // On macOS/Linux the system-wide location needs root, and DeskPair normally runs as the logged-in
        // user, so the default belongs under their home — writing to /Library/Application Support (or
        // /var/lib) there crashed with an access error. Only an actual root process keeps the system path.
        bool root = NativeUser.Geteuid() == 0;
        if (OperatingSystem.IsMacOS())
        {
            return root
                ? "/Library/Application Support/Sunllo/DeskPair"
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Sunllo", "DeskPair");
        }

        if (root)
        {
            return "/var/lib/deskpair";
        }

        string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(xdg, "deskpair");
    }

    /// <summary>
    /// Puts the allowlist in force and says out loud what it means, because the failure mode of getting this
    /// wrong is a machine nobody can reach, with nothing in the log to explain it.
    /// </summary>
    private static void ApplyAllowlist(HostRuntime runtime, HostConfig config, ILogger log)
    {
        Core.Session.Host.Auth.PeerAllowlist list = config.BuildAllowlist(out IReadOnlyList<string> rejected);
        runtime.Allowlist = list;
        runtime.RefuseRelayed = config.RefuseRelayed;

        if (rejected.Count > 0)
        {
            log.LogWarning("Allowlist entries that could not be read and are being ignored: {Entries}", string.Join(", ", rejected));
        }

        if (!list.Enabled)
        {
            return;
        }

        if (list.IsEmpty)
        {
            log.LogWarning("The allowlist is on and empty: every incoming connection will be refused");
        }
        else
        {
            log.LogInformation("Allowlist on with {Count} entr(ies){Relay}", list.Count, config.RefuseRelayed ? ", relayed connections refused" : string.Empty);
        }
    }
}

/// <summary>The effective user id, used to tell a root service from a per-user process on Unix.</summary>
internal static class NativeUser
{
    public static uint Geteuid() => OperatingSystem.IsWindows() ? 1 : geteuid();

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();
}
