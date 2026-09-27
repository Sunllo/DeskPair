using Microsoft.Extensions.Logging;
using DeskPair.Core.Portal;
using DeskPair.Core.Update;

namespace DeskPair.Desktop.Services;

/// <summary>
/// What the last check found. Everything here is a fact; what to show is the view models' decision.
/// </summary>
public sealed record UpdateState
{
    public static readonly UpdateState Unknown = new();

    /// <summary>When the portal last answered, whatever it said. Null means it never has.</summary>
    public DateTimeOffset? LastChecked { get; init; }

    public string LatestVersion { get; init; } = string.Empty;

    /// <summary>Somebody else's words. Shown as text, never as markup, and already length-capped.</summary>
    public string Notes { get; init; } = string.Empty;

    public DateTimeOffset? Released { get; init; }

    public bool IsUpdateAvailable { get; init; }

    /// <summary>Derived from the configured portal. Never read from the manifest.</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>Why the last check did not work, for the About tab. Empty when it did.</summary>
    public string Problem { get; init; } = string.Empty;

    /// <summary>
    /// The file this machine can install, when the portal published a manifest signed by the key this
    /// install trusts and it names one for this platform. Null is "tell, do not install": an unsigned
    /// release, a self-hosted portal with no key configured, or no file for this machine.
    /// </summary>
    public ReleaseFile? Install { get; init; }

    /// <summary>The portal the install would download from; the one the check asked.</summary>
    public string Portal { get; init; } = string.Empty;

    public bool CanInstall => IsUpdateAvailable && Install is not null;
}

/// <summary>
/// Asks the portal what the newest version is, and never does anything about it but say so.
///
/// Modelled on <see cref="AddressBookSyncService"/>: a <see cref="TimeProvider"/> timer, every exception
/// swallowed in the callback, and started fire-and-forget from <c>App</c>. A timer callback that throws
/// takes the process with it, and this one runs on a laptop in a bag.
///
/// Where it differs, and why. Six hours rather than fifteen minutes, because a release happens weekly at
/// best; with jitter, because a fixed period is a crowd arriving at the portal the moment one is
/// published. Twenty seconds before the first ask rather than a debounce, because there is no edit to
/// debounce and a DNS stall has no business on the path that draws the first window. And the opt-out is
/// checked inside the tick rather than by stopping the timer: a timer is free, the request is the thing
/// worth suppressing, and turning the setting back on then needs no plumbing at all.
/// </summary>
public sealed class UpdateCheckService : IDisposable
{
    private static readonly TimeSpan AfterStart = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Regularly = TimeSpan.FromHours(6);

    private readonly UpdateClient _client;
    private readonly string _runningVersion;
    private readonly Func<bool> _enabled;
    private readonly Func<string> _portal;
    private readonly Func<string> _publicKey;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private ITimer? _timer;

    public UpdateCheckService(
        UpdateClient client,
        string runningVersion,
        Func<bool> enabled,
        Func<string> portal,
        ILogger<UpdateCheckService> log,
        TimeProvider? time = null,
        Func<string>? publicKey = null)
    {
        _client = client;
        _runningVersion = runningVersion;
        _enabled = enabled;
        _portal = portal;
        _publicKey = publicKey ?? (() => string.Empty);
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public UpdateState Current { get; private set; } = UpdateState.Unknown;

    /// <summary>Raised on a pool thread. A view model handling it has to get back to the UI thread itself.</summary>
    public event Action<UpdateState>? Changed;

    public static UpdateCheckService Create(ILoggerFactory logs)
    {
        ArgumentNullException.ThrowIfNull(logs);

        // Nothing here can fail: no keystore, no identity, no token. Unlike the address book sync, which
        // is why App needs no try/catch around this one.
        return new UpdateCheckService(
            new UpdateClient(),
            App.Version,
            () => App.Config.CheckForUpdates,
            () => App.Config.PortalServer,
            logs.CreateLogger<UpdateCheckService>(),
            publicKey: () => App.Config.UpdatePublicKeyBase64);
    }

    public void Start()
    {
        // Up to a tenth of the period, spread per installation, so a release does not bring everybody at
        // once. Random.Shared is fine: nothing here is a secret.
        TimeSpan jitter = TimeSpan.FromMilliseconds(Random.Shared.Next((int)(Regularly.TotalMilliseconds / 10)));
        _timer = _time.CreateTimer(_ => Run(), null, AfterStart, Regularly + jitter);
    }

    /// <summary>The About tab's button. Asks even when the setting is off: a click is consent.</summary>
    public Task CheckNowAsync(CancellationToken ct = default) => CheckAsync(manual: true, ct);

    private void Run() => _ = CheckAsync(manual: false, _stopping.Token);

    private async Task CheckAsync(bool manual, CancellationToken ct)
    {
        try
        {
            if (!manual && !_enabled())
            {
                return;
            }

            string portal = _portal();
            UpdateManifest manifest = await _client
                .FetchAsync(portal, UpdateEndpoints.StableChannel, ct).ConfigureAwait(false);

            bool newer = AppVersion.IsNewer(manifest.Version, _runningVersion);
            ReleaseFile? install = newer ? await InstallableAsync(portal, manifest.Version, ct).ConfigureAwait(false) : null;

            // One line per check, at Information when there is news and Debug otherwise. A feature whose
            // whole job is to tell somebody something should not be silent to the log as well -- when it
            // does nothing, "did it ask, and what did it hear" is the only question worth answering.
            _log.Log(
                newer ? LogLevel.Information : LogLevel.Debug,
                "Update check: {Portal} offers {Offered}, running {Running}{Verdict}",
                portal.Length > 0 ? portal : UpdateEndpoints.OfficialPortal,
                manifest.Version,
                _runningVersion,
                newer ? " (newer)" : string.Empty);

            Publish(Current with
            {
                LastChecked = _time.GetUtcNow(),
                LatestVersion = manifest.Version,
                Notes = manifest.Notes,
                Released = manifest.Released == DateTimeOffset.MinValue ? null : manifest.Released,
                IsUpdateAvailable = newer,
                // Built here, from configuration. The manifest has no URL in it to take one from.
                DownloadUrl = UpdateEndpoints.DownloadPageFor(portal),
                Install = install,
                Portal = portal,
                Problem = string.Empty,
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Keep whatever the last successful check found. A flaky network must not make a notice the
            // reader has already seen flicker in and out.
            _log.LogDebug(e, "Update check failed against {Portal}", _portal());
            Publish(Current with
            {
                LastChecked = _time.GetUtcNow(),
                Problem = e is PortalException ? e.Message : e.GetType().Name,
            });
        }
    }

    /// <summary>
    /// Whether the newer version can be installed from here, which takes a signed manifest: the bytes of
    /// release.json verified against the key this install trusts, naming the version the check heard and
    /// a file for this platform. Every other outcome is null and a reason in the log -- the button then
    /// opens the download page, which needs none of this.
    /// </summary>
    private async Task<ReleaseFile?> InstallableAsync(string portal, string version, CancellationToken ct)
    {
        byte[]? key = ReleaseSigning.KeyFor(portal, _publicKey());
        if (key is null)
        {
            _log.LogDebug("No release signing key for {Portal}; updates from it are announced, not installed", UpdateEndpoints.PortalFor(portal));
            return null;
        }

        (byte[] Manifest, string Signature)? signed = await _client.FetchSignedAsync(portal, ct).ConfigureAwait(false);
        if (signed is null)
        {
            _log.LogInformation("{Portal} publishes no signed release manifest; the update can be downloaded but not installed from here", UpdateEndpoints.PortalFor(portal));
            return null;
        }

        if (!SignedRelease.Verify(signed.Value.Manifest, signed.Value.Signature, key))
        {
            _log.LogWarning("The release manifest at {Portal} is not signed by the key this install trusts; not installing from it", UpdateEndpoints.PortalFor(portal));
            return null;
        }

        SignedReleaseManifest? release = SignedRelease.Parse(signed.Value.Manifest);
        if (release is null || !string.Equals(release.Version, version, StringComparison.Ordinal))
        {
            _log.LogWarning("The signed manifest says {Signed} but the update check heard {Heard}; not installing", release?.Version ?? "(unreadable)", version);
            return null;
        }

        ReleaseFile? file = release.FileForThisMachine();
        if (file is null)
        {
            _log.LogInformation("Release {Version} has no file for {Platform}/{Arch}", version, ReleaseFile.ThisPlatform, ReleaseFile.ThisArch);
        }
        else
        {
            _log.LogInformation("Release {Version} is signed by the trusted key and can be installed from here: {File} ({Size} bytes)", version, file.Name, file.Size);
        }

        return file;
    }

    private void Publish(UpdateState state)
    {
        Current = state;
        Changed?.Invoke(state);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _timer?.Dispose();
        _client.Dispose();
        _stopping.Dispose();
    }
}
