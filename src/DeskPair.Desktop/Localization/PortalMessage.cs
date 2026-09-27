using DeskPair.Core.Portal;

namespace DeskPair.Desktop.Localization;

/// <summary>
/// What a portal failure says to the person in front of the screen, in their language.
///
/// The portal answers in English and always will: it serves every copy of the app in the world and has no
/// idea who is reading. <see cref="PortalException.Code"/> is the part that is meant to be acted on, so
/// that is what gets translated here, and the English sentence stays as the fallback. A code this version
/// has never heard of therefore still says something true rather than nothing -- which is what lets the
/// portal add one without every installed client having to be updated first.
/// </summary>
public static class PortalMessage
{
    /// <param name="portal">The address that was being talked to, for the failures that are about reaching it.</param>
    public static string For(PortalException failure, string portal)
    {
        ArgumentNullException.ThrowIfNull(failure);

        string key = "portal." + (failure.Code ?? string.Empty);
        if (!Strings.Has(key))
        {
            return failure.Message;
        }

        // Only the two that are about the address say it; the rest read worse with a URL stapled on.
        return failure.Code is "unreachable" or "timeout"
            ? Strings.Format(key, Core.Update.UpdateEndpoints.PortalFor(portal))
            : Strings.Get(key);
    }
}
