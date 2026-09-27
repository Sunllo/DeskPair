using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Protocol;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Config;

/// <summary>User-editable host settings; the service owns the file and pushes snapshots to the UI over IPC.</summary>
public sealed record HostConfig
{
    /// <summary>Explicit so the JSON source generator builds the object through it and property initializers run.</summary>
    [JsonConstructor]
    public HostConfig()
    {
    }

    /// <summary>
    /// Ask the portal which signalling server to use, instead of naming one here.
    ///
    /// On by default, because most people do not run their own and an empty settings field is the right
    /// thing for them to see. Turning it off is what somebody self-hosting does, and then
    /// <see cref="RendezvousServer"/> below is theirs to fill in and nothing overwrites it.
    ///
    /// The address that comes back is used, never stored: writing it into this file would freeze today's
    /// answer into every installation, which is the problem asking was meant to solve.
    /// </summary>
    /// <summary>
    /// The portal this host asks for its directory. Empty means the official one.
    ///
    /// Its own field rather than the one the window keeps, because the engine runs as a separate process
    /// and may run with no window at all. Somebody running their own portal sets it here as well.
    /// </summary>
    public string PortalServer { get; init => field = value ?? string.Empty; } = string.Empty;

    public bool UseDirectoryServers { get; init; } = true;

    public string RendezvousServer { get; init => field = value ?? string.Empty; } = string.Empty;

    public string ServerPublicKeyBase64 { get; init => field = value ?? string.Empty; } = string.Empty;

    public ApproveMode ApproveMode { get; init; } = ApproveMode.ApprovePassword;

    public bool KeyboardEnabled { get; init; } = true;

    public bool ClipboardEnabled { get; init; } = true;

    public bool AudioEnabled { get; init; } = true;

    public bool FileTransferEnabled { get; init; } = true;

    public bool RestartEnabled { get; init; }

    /// <summary>
    /// Whether a viewer may open a shell on this computer. Off by default: a shell is the whole machine,
    /// and on an unattended host it runs as SYSTEM or root. Never requested by a viewer's options.
    /// </summary>
    public bool TerminalEnabled { get; init; }

    /// <summary>
    /// Whose shell: "system" (the most this host can give -- SYSTEM under the Windows service, root where
    /// the Linux daemon's own gate allows it, the signed-in user on macOS) or "user" (the engine's own
    /// account, or the signed-in user's where the engine can take their token).
    /// </summary>
    public string TerminalRunsAs { get; init => field = value ?? "system"; } = "system";

    /// <summary>Sound device the desk captures from; empty follows whatever Windows is using.</summary>
    public string AudioCaptureDeviceId { get => field; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Shown to viewers instead of the machine name; empty means the machine name.</summary>
    public string DeviceName { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Whether the rotating temporary password may be used to log in at all.</summary>
    public bool TemporaryPasswordEnabled { get; init; } = true;

    public int TemporaryPasswordLength { get; init; } = 6;

    /// <summary>Consecutive wrong temporary passwords before a new one is generated.</summary>
    public int TemporaryRotationThreshold { get; init; } = 10;

    /// <summary>New temporary password after every session, so a shared one cannot be reused.</summary>
    public bool RotateTemporaryAfterSession { get; init; }

    /// <summary>Answer every hole punch with a relay instead of a direct path.</summary>
    public bool ForceRelay { get; init; }

    /// <summary>Manual approval window before the connection is refused.</summary>
    public int ApprovalTimeoutSeconds { get; init; } = 30;

    /// <summary>In manual-approval mode, a correct password still lets a viewer in without asking.</summary>
    public bool AllowPasswordInClickMode { get; init; } = true;

    /// <summary>Video over a UDP media channel with forward error correction when the viewer supports it.</summary>
    public bool UdpMedia { get; init; } = true;

    public int DirectAccessPort { get; init; } = ProtocolConstants.DirectAccessPort;

    public bool DirectAccessEnabled { get; init; } = true;

    public string CodecPreference { get; init => field = value ?? "auto"; } = "auto";

    /// <summary>At most this many of this computer's displays streamed to one viewer at once. Applies to the next request.</summary>
    public int MaxDisplaysPerViewer { get; init; } = Services.HostMediaModule.DefaultMaxDisplaysPerViewer;

    /// <summary>At most this many display streams (each its own encoder) at once, over every viewer.</summary>
    public int MaxConcurrentStreams { get; init; } = Services.HostMediaModule.DefaultMaxConcurrentStreams;

    /// <summary>
    /// A viewer with keyboard permission may plug in displays that do not exist, and a host with no screen gets
    /// one for each session. Off by default: it changes the desk, and on Windows it needs the display driver.
    /// </summary>
    public bool AllowVirtualDisplay { get; init; }

    /// <summary>
    /// Absolute directories that file transfer is confined to. Empty (the default) means unrestricted: a peer
    /// with file permission may reach any path the host process can. When set, a peer may only list, read or
    /// write paths inside one of these roots and everything else is refused; traversal is resolved first, so
    /// ".." cannot climb out of a root.
    /// </summary>
    /// <remarks>
    /// Backed by a NUL-joined string (a file path cannot contain NUL) so the record keeps value equality: a
    /// list field would compare by reference and two round-tripped configs would never be equal.
    /// </remarks>
    public IReadOnlyList<string> FileTransferRoots
    {
        get => _fileTransferRoots.Length == 0 ? [] : _fileTransferRoots.Split('\0');
        init => _fileTransferRoots = value is null or { Count: 0 } ? string.Empty : string.Join('\0', value);
    }

    private readonly string _fileTransferRoots = string.Empty;

    // ---- who may connect at all ----

    /// <summary>
    /// Refuse every peer that is not in <see cref="AllowedPeers"/>. Off by default: the password is what
    /// normally decides. Turning it on with an empty list refuses everybody, which is a deliberate answer
    /// and not a misconfiguration to paper over.
    /// </summary>
    public bool AllowlistEnabled { get; init; }

    /// <summary>
    /// Addresses, ranges and ids that may connect: "203.0.113.5", "192.168.1.0/24", "2001:db8::/32",
    /// "id:123456789". Only consulted when <see cref="AllowlistEnabled"/> is set.
    /// </summary>
    /// <remarks>NUL-joined for the reason <see cref="FileTransferRoots"/> gives: the record keeps value equality.</remarks>
    public IReadOnlyList<string> AllowedPeers
    {
        get => _allowedPeers.Length == 0 ? [] : _allowedPeers.Split('\0');
        init => _allowedPeers = value is null or { Count: 0 } ? string.Empty : string.Join('\0', value);
    }

    private readonly string _allowedPeers = string.Empty;

    /// <summary>
    /// Refuse anything arriving through a relay, whatever the allowlist says.
    ///
    /// A relayed connection's socket belongs to the relay, not to the peer, so the address the allowlist is
    /// matched against is the one the rendezvous server reports -- true, but asserted by a third party rather
    /// than observed. Setting this trusts only what the socket itself shows. The cost is that a viewer whose
    /// network cannot be punched through cannot reach this machine at all.
    /// </summary>
    public bool RefuseRelayed { get; init; }

    /// <summary>
    /// The allowlist as the runtime consults it, or <see cref="PeerAllowlist.Off"/> when it is not in
    /// use. Entries that could not be read are returned in <paramref name="rejected"/> so the caller can say
    /// so once at startup rather than silently refusing everyone.
    /// </summary>
    public PeerAllowlist BuildAllowlist(out IReadOnlyList<string> rejected)
    {
        if (!AllowlistEnabled)
        {
            rejected = [];
            return PeerAllowlist.Off;
        }

        return PeerAllowlist.Create(true, AllowedPeers, out rejected);
    }

    /// <summary>
    /// The path guard for <see cref="Services.HostFileModule.IsPathAllowed"/>, or null when
    /// <see cref="FileTransferRoots"/> is empty (no restriction). A path is allowed when, fully resolved, it
    /// equals or sits under one of the configured roots. Case sensitivity follows the host file system.
    /// </summary>
    public Func<string, bool>? BuildFileTransferGuard()
    {
        string[] roots = FileTransferRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(TryFullPath)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToArray();
        if (roots.Length == 0)
        {
            return null;
        }

        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path =>
        {
            string? full = TryFullPath(path);
            if (full is null)
            {
                return false;
            }

            foreach (string root in roots)
            {
                if (string.Equals(full, root, cmp) || full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, cmp))
                {
                    return true;
                }
            }

            return false;
        };
    }

    private static string? TryFullPath(string path)
    {
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public HostPolicy ToPolicy() => new()
    {
        ApproveMode = ApproveMode,
        AllowPasswordInClickMode = AllowPasswordInClickMode,
        ApprovalTimeout = TimeSpan.FromSeconds(Math.Clamp(ApprovalTimeoutSeconds, 5, 300)),
        HostName = DeviceName.Length > 0 ? DeviceName : Environment.MachineName,
        KeyboardEnabled = KeyboardEnabled,
        ClipboardEnabled = ClipboardEnabled,
        AudioEnabled = AudioEnabled,
        FileTransferEnabled = FileTransferEnabled,
        RestartEnabled = RestartEnabled,
        TerminalEnabled = TerminalEnabled,
        TerminalRunAs = TerminalRunsAs.Equals("user", StringComparison.OrdinalIgnoreCase) ? TerminalRunAs.User : TerminalRunAs.Highest,
    };

    /// <summary>
    /// The settings the engine runs on.
    ///
    /// <paramref name="rendezvous"/> and <paramref name="serverKey"/> are what the portal's directory
    /// answered, resolved before this is built. Everything downstream reads one address off
    /// <see cref="PeerSettings.RendezvousServer"/> and knows nothing about where it came from, which is why
    /// asking a portal was a change to one file rather than to the transport.
    ///
    /// A value configured here always wins. Somebody who typed an address meant it.
    /// </summary>
    public PeerSettings ToPeerSettings(string version, string? rendezvous = null, string? serverKey = null) => new()
    {
        RendezvousServer = RendezvousServer.Length > 0 ? RendezvousServer : rendezvous ?? string.Empty,
        ServerPublicKeyBase64 = RendezvousServer.Length > 0
            ? ServerPublicKeyBase64
            : ServerPublicKeyBase64.Length > 0 ? ServerPublicKeyBase64 : serverKey ?? string.Empty,
        Version = version,
        UdpMedia = UdpMedia,
    };

    /// <summary>
    /// True when a change cannot reach the running engine: these are baked into <see cref="PeerSettings"/>, the
    /// direct-access listener or the media module at start-up, so the UI has to say "restart the host service".
    /// Everything else (approval, permissions, passwords, relay preference) is applied live by the service.
    /// </summary>
    public static bool RequiresEngineRestart(HostConfig before, HostConfig after) =>
        before.UseDirectoryServers != after.UseDirectoryServers
        || before.RendezvousServer != after.RendezvousServer
        || before.ServerPublicKeyBase64 != after.ServerPublicKeyBase64
        || before.DirectAccessEnabled != after.DirectAccessEnabled
        || before.DirectAccessPort != after.DirectAccessPort
        || before.UdpMedia != after.UdpMedia
        || before.CodecPreference != after.CodecPreference;

    public string ToJson() => JsonSerializer.Serialize(this, HostConfigJson.Default.HostConfig);

    /// <summary>
    /// Reads settings over the defaults: a file written before a setting existed, or edited by hand, keeps the
    /// default for everything it does not mention instead of reading every missing switch as "off".
    /// </summary>
    public static HostConfig FromJson(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject stored)
        {
            return new HostConfig();
        }

        JsonObject merged = JsonSerializer.SerializeToNode(new HostConfig(), HostConfigJson.Default.HostConfig)!.AsObject();
        foreach (KeyValuePair<string, JsonNode?> setting in stored)
        {
            merged[setting.Key] = setting.Value?.DeepClone();
        }

        return merged.Deserialize(HostConfigJson.Default.HostConfig) ?? new HostConfig();
    }
}

// Metadata mode: the fast path builds the object without running property initializers, so a file that predates
// a setting (or one edited by hand) would read every missing bool as false and silently withdraw permissions.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(HostConfig))]
internal partial class HostConfigJson : JsonSerializerContext;

/// <summary>JSON file store with atomic replace.</summary>
public sealed class HostConfigStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public HostConfigStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public HostConfig Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return new HostConfig();
            }

            try
            {
                return HostConfig.FromJson(File.ReadAllText(_path));
            }
            catch (JsonException)
            {
                return new HostConfig();
            }
        }
    }

    public void Save(HostConfig config)
    {
        lock (_lock)
        {
            string? dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, config.ToJson());
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
