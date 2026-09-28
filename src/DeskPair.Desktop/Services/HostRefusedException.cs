using DeskPair.Desktop.Localization;

namespace DeskPair.Desktop.Services;

/// <summary>
/// The engine declined a request because this window's account is not the owner of this computer: neither signed in
/// at its own screen nor an administrator of it. An <see cref="InvalidOperationException"/>, like the engine not being
/// there at all, so a page that already copes with that copes with this; its message is for the person.
/// </summary>
public sealed class HostRefusedException(string reason) : InvalidOperationException(Strings.Get("host.notOwner"))
{
    /// <summary>The engine's reason, in English, for the log.</summary>
    public string Reason { get; } = reason;
}
