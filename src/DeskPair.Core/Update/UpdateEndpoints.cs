namespace DeskPair.Core.Update;

/// <summary>
/// Where the update check asks, and where the button goes.
///
/// The download page address is built here, from the portal the user configured, and is never read from
/// the manifest. That is the whole reason the manifest needs no signature: a forged one can claim a
/// version that does not exist, and the button still opens the real download page. Add a URL field to the
/// manifest and honour it, and that argument collapses -- <c>UpdateClientTests</c> asserts it does not.
/// </summary>
public static class UpdateEndpoints
{
    /// <summary>What an install with no portal configured asks. HTTPS, and a constant.</summary>
    public const string OfficialPortal = "https://deskpair.app";

    public const string StableChannel = "stable";

    /// <summary>
    /// The portal to ask, given what the user configured.
    ///
    /// A self-hosted address is honoured as typed and never quietly replaced by the official one: doing
    /// that would tell a third party that a private deployment exists, and would answer a question the
    /// user did not ask.
    /// </summary>
    public static string PortalFor(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? OfficialPortal : Portal.PortalClient.Normalise(configured);

    public static string ManifestFor(string? portal, string channel) =>
        PortalFor(portal) + "/api/v1/update/" + channel;

    public static string DownloadPageFor(string? portal) => PortalFor(portal) + "/download";
}
