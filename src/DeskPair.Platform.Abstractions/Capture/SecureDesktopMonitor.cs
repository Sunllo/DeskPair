namespace DeskPair.Platform.Abstractions.Capture;

/// <summary>What a host's engine is facing when it cannot read or drive the desktop the machine is showing.</summary>
public enum SecureDesktopKind
{
    /// <summary>An ordinary desktop the engine can capture and inject into.</summary>
    None,

    /// <summary>A UAC / consent prompt on the Windows secure desktop, which only SYSTEM may read.</summary>
    Uac,

    /// <summary>The lock or sign-in screen.</summary>
    Locked,
}

/// <summary>
/// What the input desktop is right now, from the engine's point of view.
/// </summary>
/// <param name="Kind">
/// What to tell viewers, from whether this engine can read the input desktop: an engine that cannot (an app-mode
/// engine on the Windows secure desktop) reports <see cref="SecureDesktopKind.Uac"/> or
/// <see cref="SecureDesktopKind.Locked"/> so the frozen picture is explained; one that can (a SYSTEM service)
/// reports <see cref="SecureDesktopKind.None"/>, because it has nothing to warn about.
/// </param>
/// <param name="OnSecureDesktop">
/// Whether the input desktop is a secure one (the UAC/consent or lock desktop) at all -- told apart by name and
/// by the processes that draw it, so it is true even for a SYSTEM engine that <em>can</em> read it. That engine
/// needs to know, because it only shows and drives the secure desktop for devices allowed to see it.
/// </param>
public readonly record struct SecureDesktopState(SecureDesktopKind Kind, bool OnSecureDesktop);

/// <summary>
/// Tells whether the host's engine is looking at a desktop it can neither show the viewer nor drive -- on
/// Windows the secure desktop a UAC prompt is drawn on, or the lock / sign-in screen. The host polls it on
/// its own cadence and tells connected viewers when the answer changes, so the frozen picture is explained
/// rather than left to look like a dead screen. A platform or engine that can always read the input desktop
/// (a SYSTEM service, or a system with no secure desktop of its own) reports <see cref="SecureDesktopKind.None"/>
/// for <see cref="SecureDesktopState.Kind"/>, but still reports <see cref="SecureDesktopState.OnSecureDesktop"/>.
/// </summary>
public interface ISecureDesktopMonitor
{
    /// <summary>Reads the state now. Cheap, non-blocking, and safe to call from a timer thread.</summary>
    SecureDesktopState Poll();
}
