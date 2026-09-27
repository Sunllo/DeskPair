using DeskPair.Core.Config;
using DeskPair.Core.Portal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DeskPair.Desktop.Services;

/// <summary>Controller-side settings (per user). Host settings live with the service and travel over IPC.</summary>
public sealed record DesktopConfig
{
    // Setters coalesce null so hand-edited or partial JSON never yields null strings.
    public string RendezvousServer { get; init => field = value ?? string.Empty; } = string.Empty;

    public string ServerPublicKeyBase64 { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>The portal this device syncs with: host, host:port or a full URL. Empty means not configured.</summary>
    public string PortalServer { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>
    /// A self-hosted portal's release signing key (base64 SPKI), so its updates can be installed and not
    /// only announced. Empty for the official portal, whose key is built in.
    /// </summary>
    public string UpdatePublicKeyBase64 { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>"system", "en" or "zh-TW".</summary>
    public string Language { get; init => field = value ?? "system"; } = "system";

    public bool KeyboardTranslateMode { get; init; }

    // ---- start-up and window behaviour ----

    /// <summary>Desired state of the per-user login entry; the registry is the authority (see StartupEntry).</summary>
    public bool StartWithSystem { get; init; }

    public bool StartMinimised { get; init; }

    /// <summary>Closing the main window hides it to the tray instead of quitting.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Where the file transfer window opens locally; empty means the user profile folder.</summary>
    public string FileTransferFolder { get => field; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>How many recent connections to remember.</summary>
    public int RecentLimit { get; init; } = 20;

    // ---- defaults for a new remote session ----

    /// <summary>"low", "balanced", "best" or "custom".</summary>
    public string DefaultQuality { get => field; init => field = value ?? "balanced"; } = "balanced";

    public int CustomBitrateKbps { get; init; } = 8000;

    public int CustomFps { get; init; } = 30;

    /// <summary>
    /// Draw the far end's pointer as well as the local one. Off by default: two pointers on one screen, a
    /// few pixels apart and a round trip out of step, is more confusing than helpful, and the one that
    /// matters is the one under the user's hand. The session toolbar turns it on when it is wanted, for
    /// watching someone else work.
    /// </summary>
    public bool ShowRemoteCursor { get; init; }

    public bool LosslessRefinement { get; init; } = true;

    public bool LockAfterSessionEnd { get; init; }

    public bool FitToWindow { get; init; } = true;

    public bool SmoothPlayback { get; init; }

    /// <summary>
    /// Play the host's sound here. Off to begin with: most sessions are work on someone else's machine, where
    /// their sound arriving in your room is a surprise rather than a feature, and it costs bandwidth the whole
    /// time. The session toolbar turns it on when it is wanted.
    /// </summary>
    public bool AudioEnabled { get; init; }

    /// <summary>Accept the host's UDP media channel (video with forward error correction).</summary>
    public bool UdpMedia { get; init; } = true;

    /// <summary>Always connect through a relay instead of trying a direct path.</summary>
    public bool ForceRelay { get; init; }

    // ---- updates ----

    /// <summary>
    /// Ask the portal now and then whether a newer DeskPair has been published.
    ///
    /// Nothing is ever downloaded or installed: the answer is a version number, and the only action
    /// offered is opening the download page in a browser. On by default, because an update nobody hears
    /// about is not shipped.
    /// </summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>
    /// The version whose notice was waved away. A newer one shows again; the same one does not.
    ///
    /// Dismissing silences the banner, not the truth: the About tab still says what is available.
    /// </summary>
    public string DismissedUpdateVersion { get => field; init => field = value ?? string.Empty; } = string.Empty;

    // ---- sound ----

    /// <summary>Playback device; empty follows whatever Windows is using.</summary>
    public string AudioPlaybackDeviceId { get => field; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Exclusive playback hands the device to this app alone.</summary>
    public bool AudioExclusive { get; init; }

    // ---- recording ----

    /// <summary>Where recordings are written; empty means Videos\Sunllo DeskPair.</summary>
    public string RecordingFolder { get => field; init => field = value ?? string.Empty; } = string.Empty;

    public bool RecordAudio { get; init; } = true;

    /// <summary>Null-tolerant: hand-edited files may omit the array.</summary>
    public List<RecentPeer> Recent { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Send this computer's record of who connected to it up to the linked account, so several machines can
    /// be read from one page. Nothing leaves a machine that is not signed in -- linking the account is the
    /// opt-in, and this switch is how to change your mind afterwards.
    /// </summary>
    public bool UploadConnectionHistory { get; init; } = true;

    /// <summary>
    /// The resolution chosen for a remote display, put back the next time that machine is reached. Local on
    /// purpose: what a viewer likes a screen at is this viewer's business, not the address book's, so it does
    /// not travel with the saved devices.
    /// </summary>
    public List<PeerResolution> PeerResolutions { get; init => field = value ?? []; } = [];

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sunllo", "DeskPair", "desktop.json");

    public static DesktopConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                return FromJson(File.ReadAllText(path));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new DesktopConfig();
    }

    /// <summary>
    /// Reads settings over the defaults, so a file written before a setting existed keeps that setting's default
    /// rather than reading it as "off".
    /// </summary>
    /// <summary>
    /// The connection settings these describe.
    /// </summary>
    /// <param name="version">This build, which the peer is told about during the handshake.</param>
    /// <param name="serverKey">
    /// Replaces the stored key, for the first-run case where it has just been fetched from the server but
    /// not written back yet.
    /// </param>
    /// <param name="directory">
    /// Where the published directory says the servers are, used when nothing is configured here.
    ///
    /// The same shape as <c>HostConfig.ToPeerSettings</c>, and it was missing. A configured server always
    /// wins; with none, this used to hand out an empty string, and an empty host is not an error anywhere
    /// along the way -- it resolves to this machine, so every call went to localhost and came back
    /// "connection refused". The host half of the same process asked the directory and worked, which is
    /// what made it look like a server outage rather than a program that could not dial.
    /// </param>
    public PeerSettings ToPeerSettings(string version, string? serverKey = null, NetworkDirectory? directory = null) => new()
    {
        RendezvousServer = RendezvousServer.Length > 0 ? RendezvousServer : directory?.Rendezvous ?? string.Empty,
        ServerPublicKeyBase64 = RendezvousServer.Length > 0
            ? serverKey ?? PeerSettings.CleanBase64(ServerPublicKeyBase64)
            : serverKey is { Length: > 0 } fetched ? fetched
            : PeerSettings.CleanBase64(ServerPublicKeyBase64) is { Length: > 0 } stored ? stored
            : directory?.PublicKey ?? string.Empty,
        Version = version,
        UdpMedia = UdpMedia,
        ForceRelay = ForceRelay,
    };

    public static DesktopConfig FromJson(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject stored)
        {
            return new DesktopConfig();
        }

        JsonObject merged = JsonSerializer.SerializeToNode(new DesktopConfig(), DesktopConfigJson.Default.DesktopConfig)!.AsObject();
        foreach (KeyValuePair<string, JsonNode?> setting in stored)
        {
            merged[setting.Key] = setting.Value?.DeepClone();
        }

        return merged.Deserialize(DesktopConfigJson.Default.DesktopConfig) ?? new DesktopConfig();
    }

    /// <summary>What <see cref="Save"/> writes. Public so a test can round-trip without touching a disk.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, DesktopConfigJson.Default.DesktopConfig);

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>The resolution remembered for one display of one peer, or null when there is none.</summary>
    public PeerResolution? ResolutionFor(string peer, string display) =>
        PeerResolutions.FirstOrDefault(r => r.Peer == peer && r.Display == display);

    /// <summary>
    /// Remembers <paramref name="chosen"/> for one display of one peer, or forgets that display when
    /// <paramref name="chosen"/> is null ("original" is not a choice to reapply).
    /// </summary>
    public DesktopConfig WithPeerResolution(string peer, string display, PeerResolution? chosen)
    {
        var list = PeerResolutions.Where(r => !(r.Peer == peer && r.Display == display)).ToList();
        if (chosen is not null)
        {
            list.Add(chosen with { Peer = peer, Display = display });
        }

        return this with { PeerResolutions = list };
    }

    public DesktopConfig WithRecent(string id, string? name, string? platform = null)
    {
        int keep = Math.Clamp(RecentLimit, 1, 200) - 1;
        RecentPeer? previous = Recent.FirstOrDefault(r => r.Id == id);
        var list = Recent.Where(r => r.Id != id).Take(keep).ToList();

        // A peer that reports nothing keeps what it last said, so its logo does not come and go.
        string known = string.IsNullOrEmpty(platform) ? previous?.Platform ?? string.Empty : platform;
        list.Insert(0, new RecentPeer(id, name ?? string.Empty, DateTimeOffset.UtcNow) { Platform = known });
        return this with { Recent = list };
    }
}

/// <summary>A resolution chosen for a remote display: the peer's id, the display's name on the host, and the mode's pixels and scale.</summary>
public sealed record PeerResolution(string Peer, string Display, int Width, int Height, double Scale = 0);

public sealed record RecentPeer(string Id, string Name, DateTimeOffset LastConnected)
{
    /// <summary>What it was running when it was last reached, for the logo. Empty shows none.</summary>
    public string Platform { get => field; init => field = value ?? string.Empty; } = string.Empty;
}

// Metadata mode keeps property initializers, so settings missing from an older file keep their defaults.
//
// Every property is written, including the ones whose value is default(T). WhenWritingDefault was here
// and was silently wrong for every bool that starts out true: "default" means false, so turning
// CloseToTray off omitted it from the file, and FromJson then overlaid a file that no longer mentioned it
// onto a fresh instance where it was true. The setting came back on by itself, and the same went for
// UdpMedia, FitToWindow, LosslessRefinement and RecordAudio. A settings file people occasionally read by
// hand is also better off listing what it holds.
[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DesktopConfig))]
internal sealed partial class DesktopConfigJson : JsonSerializerContext
{
}
