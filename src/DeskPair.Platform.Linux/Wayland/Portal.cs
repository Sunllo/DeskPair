using Tmds.DBus.Protocol;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>
/// The names and numbers of xdg-desktop-portal's ScreenCast and RemoteDesktop interfaces, and the parts of talking to
/// them that need no bus: where a request's answer will appear, and what <c>Start</c> answered. Kept apart from
/// <see cref="PortalSession"/> so all of it can be tested on a machine that has never seen D-Bus.
/// </summary>
internal static class Portal
{
    public const string BusName = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    public const string ScreenCast = "org.freedesktop.portal.ScreenCast";
    public const string RemoteDesktop = "org.freedesktop.portal.RemoteDesktop";
    public const string Request = "org.freedesktop.portal.Request";
    public const string Session = "org.freedesktop.portal.Session";
    public const string Properties = "org.freedesktop.DBus.Properties";

    /// <summary>ScreenCast source type: a whole monitor.</summary>
    public const uint SourceMonitor = 1;

    public const uint CursorHidden = 1;
    public const uint CursorEmbedded = 2;

    /// <summary>The cursor travels beside the picture (position and bitmap in the stream's metadata), never painted into it.</summary>
    public const uint CursorMetadata = 4;

    public const uint DeviceKeyboard = 1;
    public const uint DevicePointer = 2;

    /// <summary>Keep the permission until the person revokes it, rather than until this process exits.</summary>
    public const uint PersistUntilRevoked = 2;

    /// <summary>The first RemoteDesktop version whose <c>SelectDevices</c> takes <c>persist_mode</c> and <c>restore_token</c>.</summary>
    public const uint RemoteDesktopPersistsFrom = 2;

    /// <summary>
    /// The first ScreenCast version whose <c>SelectSources</c> takes them, which is where a session with no
    /// RemoteDesktop behind it asks to be remembered.
    /// </summary>
    public const uint ScreenCastPersistsFrom = 4;

    /// <summary><c>Response</c> codes: done, refused by the person, or ended some other way.</summary>
    public const uint ResponseSuccess = 0;
    public const uint ResponseCancelled = 1;

    /// <summary>
    /// The sender part of a request or session path: the connection's unique name without its leading colon, dots
    /// made underscores (<c>:1.234</c> becomes <c>1_234</c>).
    /// </summary>
    public static string SenderToken(string uniqueName) => uniqueName.TrimStart(':').Replace('.', '_');

    /// <summary>
    /// Where the <c>Response</c> to a call made with <paramref name="token"/> as its <c>handle_token</c> will be
    /// signalled. Known before the call, so the signal can be subscribed to first: a portal that answers without a
    /// dialog can answer before the method call's own reply arrives, and a subscription made after would miss it.
    /// </summary>
    public static string RequestPath(string uniqueName, string token) => $"{ObjectPath}/request/{SenderToken(uniqueName)}/{token}";

    /// <summary>The session object <c>CreateSession</c> makes for <paramref name="token"/> as its <c>session_handle_token</c>.</summary>
    public static string SessionPath(string uniqueName, string token) => $"{ObjectPath}/session/{SenderToken(uniqueName)}/{token}";

    /// <summary>The cursor mode to ask for: beside the picture if the portal can, painted in if not, otherwise none.</summary>
    public static uint ChooseCursorMode(uint available) =>
        (available & CursorMetadata) != 0 ? CursorMetadata :
        (available & CursorEmbedded) != 0 ? CursorEmbedded :
        CursorHidden;

    /// <summary>Reads what <c>Start</c> answered: the devices granted, the streams, and the token for next time.</summary>
    public static PortalStart ParseStart(IReadOnlyDictionary<string, VariantValue> results)
    {
        uint devices = results.TryGetValue("devices", out VariantValue d) ? Unwrap(d).GetUInt32() : 0;
        string? token = results.TryGetValue("restore_token", out VariantValue t) ? NonEmpty(Unwrap(t).GetString()) : null;
        bool clipboard = results.TryGetValue("clipboard_enabled", out VariantValue c) && Unwrap(c).GetBool();

        var streams = new List<PortalStream>();
        if (results.TryGetValue("streams", out VariantValue s))
        {
            // a(ua{sv}): each entry is the PipeWire node id and what the portal knows about it.
            VariantValue array = Unwrap(s);
            for (int i = 0; i < array.Count; i++)
            {
                VariantValue entry = Unwrap(array.GetItem(i));
                uint node = Unwrap(entry.GetItem(0)).GetUInt32();
                streams.Add(ParseStream(node, Unwrap(entry.GetItem(1))));
            }
        }

        return new PortalStart(devices, streams, token, clipboard);
    }

    private static PortalStream ParseStream(uint node, VariantValue properties)
    {
        string? id = null;
        string? mapping = null;
        uint sourceType = 0;
        (int X, int Y)? position = null;
        (int Width, int Height) size = (0, 0);
        for (int i = 0; i < properties.Count; i++)
        {
            KeyValuePair<VariantValue, VariantValue> entry = properties.GetDictionaryEntry(i);
            VariantValue value = Unwrap(entry.Value);
            switch (entry.Key.GetString())
            {
                case "id":
                    id = NonEmpty(value.GetString());
                    break;
                case "mapping_id":
                    mapping = NonEmpty(value.GetString());
                    break;
                case "source_type":
                    sourceType = value.GetUInt32();
                    break;
                case "position":
                    position = (Unwrap(value.GetItem(0)).GetInt32(), Unwrap(value.GetItem(1)).GetInt32());
                    break;
                case "size":
                    size = (Unwrap(value.GetItem(0)).GetInt32(), Unwrap(value.GetItem(1)).GetInt32());
                    break;
                default:
                    // Keys a newer portal adds; nothing here depends on them.
                    break;
            }
        }

        return new PortalStream(node, id, position?.X ?? 0, position?.Y ?? 0, size.Width, size.Height, position is not null, sourceType, mapping);
    }

    /// <summary>A value that arrived inside a <c>v</c> comes out of the reader still wrapped, one level per variant.</summary>
    internal static VariantValue Unwrap(VariantValue value)
    {
        while (value.Type == VariantValueType.Variant)
        {
            value = value.GetVariantValue();
        }

        return value;
    }

    private static string? NonEmpty(string value) => value.Length == 0 ? null : value;
}

/// <summary>What the portal's <c>Start</c> answered.</summary>
/// <param name="Devices">The input devices granted (<see cref="Portal.DeviceKeyboard"/>, <see cref="Portal.DevicePointer"/>).</param>
/// <param name="Streams">One PipeWire stream per shared monitor.</param>
/// <param name="RestoreToken">The token that skips the dialog next time, or null when the portal gave none.</param>
/// <param name="ClipboardEnabled">Whether the session may use the portal's clipboard.</param>
internal sealed record PortalStart(uint Devices, IReadOnlyList<PortalStream> Streams, string? RestoreToken, bool ClipboardEnabled);

/// <summary>One shared monitor: a PipeWire node and where it sits on the compositor's desktop.</summary>
/// <param name="NodeId">The PipeWire node to connect a stream to.</param>
/// <param name="Id">The portal's own identifier for the stream, stable within the session; null before ScreenCast 4.</param>
/// <param name="X">Left edge in the compositor's logical coordinates.</param>
/// <param name="Y">Top edge in the compositor's logical coordinates.</param>
/// <param name="Width">Logical width; the pixel size comes with the first buffer and may differ under scaling.</param>
/// <param name="Height">Logical height.</param>
/// <param name="HasPosition">Whether the portal said where the monitor is (it does not for windows).</param>
/// <param name="SourceType">What the person picked: a monitor, a window, or a virtual monitor.</param>
/// <param name="MappingId">What ties this stream to input regions (ScreenCast 5); null when the portal gave none.</param>
public sealed record PortalStream(
    uint NodeId, string? Id, int X, int Y, int Width, int Height, bool HasPosition, uint SourceType, string? MappingId);
