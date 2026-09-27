using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace DeskPair.Platform.Linux.Wayland.Agent;

/// <summary>
/// GNOME's arrangement of monitors, from mutter's <c>org.gnome.Mutter.DisplayConfig</c> on the user's session bus:
/// what the session agent tells the unattended engine, so that its clicks on the lock screen -- made through the
/// daemon's virtual pointer, whose axes mutter spreads over every monitor -- land where they were aimed.
///
/// <c>GetCurrentState</c> answers anybody on the session bus; <c>MonitorsChanged</c> says when to ask again. The
/// reading is kept apart from what the answer means (<see cref="Interpret"/>), which is tested without a bus.
/// </summary>
internal static class MutterDisplayConfig
{
    public const string BusName = "org.gnome.Mutter.DisplayConfig";
    public const string ObjectPath = "/org/gnome/Mutter/DisplayConfig";
    public const string Interface = "org.gnome.Mutter.DisplayConfig";

    /// <summary><c>layout-mode</c>: monitors laid out in logical pixels, a scaled monitor taking less room than its mode.</summary>
    public const uint LayoutLogical = 1;

    /// <summary><c>layout-mode</c>: monitors laid out in the pixels of their modes, whatever their scale.</summary>
    public const uint LayoutPhysical = 2;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often a layout that could not be read is asked for again: mutter may still be starting when the agent does.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>How many times it is asked before only a change of monitors prompts another try.</summary>
    private const int Attempts = 15;

    /// <summary>A monitor as mutter lists it: its connector, and its current mode's size (0 by 0 when it has none, being off).</summary>
    internal readonly record struct PhysicalMonitor(string Connector, int Width, int Height);

    /// <summary>A place on the desktop: where, at what scale, turned how, and which monitors show it (more than one when mirrored).</summary>
    internal sealed record LogicalMonitor(int X, int Y, double Scale, uint Transform, bool Primary, IReadOnlyList<string> Connectors);

    /// <summary>What <c>GetCurrentState</c> answered, as far as a layout needs it.</summary>
    internal sealed record State(IReadOnlyList<PhysicalMonitor> Monitors, IReadOnlyList<LogicalMonitor> LogicalMonitors, uint LayoutMode);

    /// <summary>
    /// Each monitor's rectangle on the desktop, as mutter derives it: the current mode's size, turned for a monitor on
    /// its side, and divided by the scale when the layout is logical (rounded as mutter's <c>roundf</c> does).
    /// </summary>
    public static DesktopLayout Interpret(State state)
    {
        var monitors = new List<LayoutMonitor>();
        foreach (LogicalMonitor logical in state.LogicalMonitors)
        {
            foreach (string connector in logical.Connectors)
            {
                if (ModeOf(state, connector) is not (int modeWidth, int modeHeight))
                {
                    continue;
                }

                bool onItsSide = (logical.Transform & 1) != 0;
                double width = onItsSide ? modeHeight : modeWidth;
                double height = onItsSide ? modeWidth : modeHeight;
                if (state.LayoutMode != LayoutPhysical)
                {
                    if (!double.IsFinite(logical.Scale) || logical.Scale <= 0)
                    {
                        continue;
                    }

                    width /= logical.Scale;
                    height /= logical.Scale;
                }

                monitors.Add(new LayoutMonitor(
                    connector,
                    logical.X,
                    logical.Y,
                    (int)Math.Round(width, MidpointRounding.AwayFromZero),
                    (int)Math.Round(height, MidpointRounding.AwayFromZero)));
            }
        }

        return new DesktopLayout(monitors);
    }

    private static (int Width, int Height)? ModeOf(State state, string connector)
    {
        foreach (PhysicalMonitor monitor in state.Monitors)
        {
            if (monitor.Connector == connector)
            {
                return monitor.Width > 0 && monitor.Height > 0 ? (monitor.Width, monitor.Height) : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Reports the layout now and whenever mutter says the monitors changed, until <paramref name="ct"/>. A desktop
    /// with no mutter is asked a few times -- it may be one still starting -- and then only when told of a change;
    /// the engine does without meanwhile, as it does at the login screen.
    /// </summary>
    public static async Task WatchAsync(Action<DesktopLayout> report, ILogger log, CancellationToken ct)
    {
        string? address = Address.Session;
        if (address is null)
        {
            return;
        }

        using var bus = new Connection(address);
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        try
        {
            await PortalSession.LeaveReaderAsync(bus.ConnectAsync().AsTask().WaitAsync(ct)).ConfigureAwait(false);
            using IDisposable watch = await PortalSession.LeaveReaderAsync(WatchChangesAsync(bus, changes.Writer).AsTask()).ConfigureAwait(false);
            int failures = 0;
            while (true)
            {
                // Whatever changed before this read is covered by it.
                _ = changes.Reader.TryRead(out _);
                try
                {
                    State state = await PortalSession.LeaveReaderAsync(
                        bus.CallMethodAsync(GetCurrentState(bus), static (m, _) => Read(m)).WaitAsync(RequestTimeout, ct)).ConfigureAwait(false);
                    failures = 0;
                    report(Interpret(state));
                }
                catch (Exception e) when (e is DBusException or TimeoutException or ProtocolException or InvalidOperationException or InvalidCastException)
                {
                    failures++;
                    log.LogDebug(e, "Mutter's display configuration could not be read (attempt {Attempt})", failures);
                    if (failures == Attempts)
                    {
                        log.LogInformation(
                            "No monitor layout from mutter ({Error}). Only GNOME's lock screen on several monitors needs it, " +
                            "and without it clicks there land beside their target", e.Message);
                    }
                }

                Task<bool> changed = changes.Reader.WaitToReadAsync(ct).AsTask();
                if (failures is > 0 and < Attempts)
                {
                    await Task.WhenAny(changed, Task.Delay(RetryInterval, ct)).ConfigureAwait(false);
                }
                else if (!await changed.ConfigureAwait(false))
                {
                    return;
                }

                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is ConnectException or DisconnectedException or ObjectDisposedException)
        {
            log.LogDebug(e, "The session bus went; the monitor layout is no longer watched");
        }
    }

    private static ValueTask<IDisposable> WatchChangesAsync(Connection bus, ChannelWriter<bool> changes) =>
        bus.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Sender = BusName, Path = ObjectPath, Interface = Interface, Member = "MonitorsChanged" },
            static (_, _) => true,
            static (e, _, _, state) =>
            {
                var writer = (ChannelWriter<bool>)state!;
                if (e is null)
                {
                    _ = writer.TryWrite(true);
                }
                else
                {
                    _ = writer.TryComplete();
                }
            },
            ObserverFlags.EmitOnConnectionDispose,
            null,
            changes,
            false);

    private static MessageBuffer GetCurrentState(Connection bus)
    {
        MessageWriter writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(BusName, ObjectPath, Interface, "GetCurrentState");
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Reads <c>(u a((ssss) a(siiddada{sv}) a{sv}) a(iiduba(ssss)a{sv}) a{sv})</c>: the serial, the monitors with
    /// their modes, the logical monitors, and the properties, of which only <c>layout-mode</c> matters here.
    /// </summary>
    private static State Read(Message message)
    {
        Reader reader = message.GetBodyReader();
        _ = reader.ReadUInt32();

        var monitors = new List<PhysicalMonitor>();
        ArrayEnd monitorsEnd = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(monitorsEnd))
        {
            // (connector, vendor, product, serial)
            reader.AlignStruct();
            string connector = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadString();

            (int Width, int Height) current = (0, 0);
            ArrayEnd modesEnd = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(modesEnd))
            {
                // (id, width, height, refresh rate, preferred scale, supported scales, properties)
                _ = reader.ReadString();
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                _ = reader.ReadDouble();
                _ = reader.ReadDouble();
                _ = reader.ReadArrayOfDouble();
                Dictionary<string, VariantValue> properties = reader.ReadDictionaryOfStringToVariantValue();
                if (properties.TryGetValue("is-current", out VariantValue isCurrent) && Portal.Unwrap(isCurrent).GetBool())
                {
                    current = (width, height);
                }
            }

            _ = reader.ReadDictionaryOfStringToVariantValue();
            monitors.Add(new PhysicalMonitor(connector, current.Width, current.Height));
        }

        var logical = new List<LogicalMonitor>();
        ArrayEnd logicalEnd = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(logicalEnd))
        {
            // (x, y, scale, transform, primary, monitors, properties)
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            double scale = reader.ReadDouble();
            uint transform = reader.ReadUInt32();
            bool primary = reader.ReadBool();
            var connectors = new List<string>();
            ArrayEnd specsEnd = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(specsEnd))
            {
                connectors.Add(reader.ReadString());
                _ = reader.ReadString();
                _ = reader.ReadString();
                _ = reader.ReadString();
            }

            _ = reader.ReadDictionaryOfStringToVariantValue();
            logical.Add(new LogicalMonitor(x, y, scale, transform, primary, connectors));
        }

        Dictionary<string, VariantValue> state = reader.ReadDictionaryOfStringToVariantValue();
        uint layoutMode = state.TryGetValue("layout-mode", out VariantValue mode) ? Portal.Unwrap(mode).GetUInt32() : LayoutLogical;
        return new State(monitors, logical, layoutMode);
    }
}
