namespace DeskPair.Platform.Abstractions.Hosting;

/// <summary>
/// This machine has no desktop that can be captured and controlled, and the facts that say why.
///
/// It exists because the alternative was worse. When the native host could not be built, the composition
/// root used to log one warning and stream a synthetic desktop instead: a moving picture of a screen that
/// is not this computer's. The connection succeeded, the viewer saw something, and nothing anywhere said
/// the picture was made up. A daemon with no DISPLAY lands in exactly that case, so the arrangement this
/// whole phase is built on would have failed by looking like it worked.
///
/// So the host refuses to start, and says what it found rather than what it wanted. <see cref="Diagnostics"/>
/// carries the observed state -- the environment variables, who we are running as, what loaded -- because
/// every one of these failures has been diagnosed by asking for precisely those, and asking costs a remote
/// round trip to a machine whose whole problem is that it cannot be reached.
/// </summary>
public sealed class HostPlatformUnavailableException(string reason, string diagnostics, Exception? cause = null)
    : Exception(diagnostics.Length > 0 ? reason + " -- " + diagnostics : reason, cause)
{
    /// <summary>What went wrong, in one sentence, without the machine state.</summary>
    public string Reason { get; } = reason;

    /// <summary>The observed state the reason was drawn from, as <c>NAME=value</c> pairs.</summary>
    public string Diagnostics { get; } = diagnostics;
}
