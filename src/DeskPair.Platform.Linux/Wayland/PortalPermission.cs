using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>What the settings page can say about the portal's permission for this account.</summary>
public enum PortalPermissionState
{
    /// <summary>Nothing remembered: the first viewer to connect makes the machine ask its person.</summary>
    NotYet,

    /// <summary>Remembered without "allow remote interaction": viewers can watch but not use the keyboard or mouse.</summary>
    WatchOnly,

    /// <summary>Remembered with the keyboard and the pointer.</summary>
    Allowed,
}

/// <summary>How asking from the settings page ended.</summary>
public enum PortalAskOutcome
{
    Allowed,
    WatchOnly,
    Declined,
    Unanswered,
    Failed,
}

/// <summary>
/// The portal's permission for this account, as the settings page shows it and asks for it: a deliberate step at the
/// machine, so that the question is not first put to whoever happens to be sitting there when a viewer connects.
///
/// "Remembered" is as much as can be known from here. The person can revoke it in the desktop's own settings, and
/// then the next viewer's connection asks again; nothing tells this process that it happened.
/// </summary>
public static class PortalPermission
{
    public static Task<PortalPermissionState> StateAsync(ISecretStore store, ILogger log, CancellationToken ct = default) =>
        StateAsync(store, LibC.geteuid(), log, ct);

    internal static async Task<PortalPermissionState> StateAsync(ISecretStore store, uint uid, ILogger log, CancellationToken ct = default)
    {
        var tokens = new PortalTokens(store, uid, log);
        if (await tokens.LoadAsync(ct).ConfigureAwait(false) is null)
        {
            return PortalPermissionState.NotYet;
        }

        // Remembered before the devices were: say "allowed", which is what was asked for then.
        uint devices = await tokens.LoadDevicesAsync(ct).ConfigureAwait(false) ?? (Portal.DeviceKeyboard | Portal.DevicePointer);
        return StateOf(devices);
    }

    /// <summary>
    /// Asks now, on this screen: a session is started without any remembered permission, which shows the dialog, and
    /// closed again at once. The answer is remembered for the engine's sessions.
    /// </summary>
    public static async Task<(PortalAskOutcome Outcome, string? Detail)> AskAsync(ISecretStore store, ILoggerFactory logs, CancellationToken ct = default)
    {
        ILogger log = logs.CreateLogger(typeof(PortalPermission));
        var tokens = new PortalTokens(store, LibC.geteuid(), log);
        try
        {
            await using PortalSession session = await PortalSession.OpenAsync(tokens, log, null, ct, offerRestoreToken: false).ConfigureAwait(false);
            return (StateOf(session.Devices) == PortalPermissionState.Allowed ? PortalAskOutcome.Allowed : PortalAskOutcome.WatchOnly, null);
        }
        catch (PortalException e)
        {
            log.LogInformation("Screen sharing was not allowed from the settings: {Reason}: {Message}", e.Reason, e.Message);
            return OutcomeOf(e);
        }
    }

    internal static PortalPermissionState StateOf(uint devices) =>
        (devices & (Portal.DeviceKeyboard | Portal.DevicePointer)) == (Portal.DeviceKeyboard | Portal.DevicePointer)
            ? PortalPermissionState.Allowed
            : PortalPermissionState.WatchOnly;

    internal static (PortalAskOutcome Outcome, string? Detail) OutcomeOf(PortalException e) => e.Reason switch
    {
        PortalFailure.Refused => (PortalAskOutcome.Declined, null),
        PortalFailure.TimedOut => (PortalAskOutcome.Unanswered, null),
        _ => (PortalAskOutcome.Failed, e.Message),
    };
}
