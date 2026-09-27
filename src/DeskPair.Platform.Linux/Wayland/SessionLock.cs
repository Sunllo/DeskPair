using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>
/// Whether this account's graphical session is locked, as logind records it: the session's <c>LockedHint</c>, which
/// GNOME and KDE set while their lock screen is up.
///
/// It matters because GNOME ends every portal screen share the moment it locks. Such a share was not stopped by the
/// person at the machine, and can come back by itself once the screen is unlocked, so <see cref="PortalHost"/> asks
/// here before saying which it was. The session is found through the user rather than through this process: an engine
/// started from a shell belongs to that shell's session, and the one that locks is the desktop's.
/// </summary>
internal static class SessionLock
{
    private const string Login1 = "org.freedesktop.login1";
    private const string Properties = "org.freedesktop.DBus.Properties";

    /// <summary>True or false as logind says; null when there is no system bus, no logind or no graphical session.</summary>
    public static async Task<bool?> IsLockedAsync(uint uid, ILogger log, CancellationToken ct = default)
    {
        string? address = Address.System;
        if (address is null)
        {
            return null;
        }

        using var bus = new Connection(address);
        try
        {
            await PortalSession.LeaveReaderAsync(bus.ConnectAsync().AsTask().WaitAsync(ct)).ConfigureAwait(false);

            string user = await PortalSession.LeaveReaderAsync(bus.CallMethodAsync(
                GetUser(bus, uid), static (m, _) => m.GetBodyReader().ReadObjectPathAsString()).WaitAsync(ct)).ConfigureAwait(false);

            // User.Display is the user's graphical session, as (id, path); "/" when there is none.
            VariantValue display = await PortalSession.LeaveReaderAsync(bus.CallMethodAsync(
                Get(bus, user, "org.freedesktop.login1.User", "Display"), static (m, _) => m.GetBodyReader().ReadVariantValue()).WaitAsync(ct)).ConfigureAwait(false);
            string session = Portal.Unwrap(Portal.Unwrap(display).GetItem(1)).GetObjectPathAsString();
            if (session == "/")
            {
                return null;
            }

            VariantValue locked = await PortalSession.LeaveReaderAsync(bus.CallMethodAsync(
                Get(bus, session, "org.freedesktop.login1.Session", "LockedHint"), static (m, _) => m.GetBodyReader().ReadVariantValue()).WaitAsync(ct)).ConfigureAwait(false);
            return Portal.Unwrap(locked).GetBool();
        }
        catch (Exception e) when (e is DBusException or ConnectException or DisconnectedException or ProtocolException
                                   or InvalidOperationException or InvalidCastException or ObjectDisposedException)
        {
            log.LogDebug(e, "logind cannot say whether the session is locked");
            return null;
        }
    }

    private static MessageBuffer GetUser(Connection bus, uint uid)
    {
        MessageWriter writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Login1, "/org/freedesktop/login1", "org.freedesktop.login1.Manager", "GetUser", "u");
            writer.WriteUInt32(uid);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static MessageBuffer Get(Connection bus, string path, string iface, string property)
    {
        MessageWriter writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Login1, path, Properties, "Get", "ss");
            writer.WriteString(iface);
            writer.WriteString(property);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }
}
