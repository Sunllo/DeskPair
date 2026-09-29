using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// Displays that do not exist, through DeskPair's display driver (<see cref="DisplayDriver"/>, <c>native/idd</c>). Each
/// is plugged into a slot of its own and unplugged alone, with the sizes the viewer who asked for it is likely to want
/// (<see cref="VirtualDisplaySizes"/>): up to 199, fixed when it is plugged in, and switched between in tens of
/// milliseconds without moving a window. <see cref="WindowsDisplayModes"/> lists and sets those sizes through here.
///
/// Opening the driver's control interface takes SYSTEM or an administrator, so this is the service's engine's; the
/// app's reports itself unavailable. While a display is plugged in, a thread feeds the driver's watchdog every second,
/// so an engine that dies leaves no display behind for longer than <see cref="WatchdogTimeout"/>. The one that starts
/// next unplugs whatever is still there, and first brings the driver up to date (<see cref="DisplayDriverInstaller.Update"/>).
///
/// The last slot is the private session screen's (<see cref="ISessionScreen"/>): plugged in at the size of the viewers'
/// window and made the only display on (<see cref="DisplayTopology"/>). A file in the data directory says it is open
/// until it is closed again, so that an engine that died meanwhile has the next one make sure a display came back on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVirtualDisplays : IVirtualDisplayProvider, ISessionScreen, IDisposable
{
    /// <summary>The slots displays viewers ask for go in; the one after them is the private session screen's.</summary>
    public const int ViewerSlots = IddControl.Slots - 1;

    /// <summary>The private session screen's slot: a monitor of its own, so what Windows remembers for it is its own.</summary>
    public const int SessionSlot = IddControl.Slots - 1;

    private const string SessionMarker = "session-screen";

    internal static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WatchdogFeed = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AppearTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SwitchTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>What a display comes up at when the viewer named no size.</summary>
    private static readonly DisplayMode DefaultSize = new(1920, 1080);

    private readonly string _dataDir;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private readonly Plugged?[] _slots = new Plugged?[IddControl.Slots];
    private readonly Task _setup;
    private CancellationTokenSource? _feeding;
    private bool _disposed;

    /// <summary>A display in a slot: every size it can have, and what finds it in the display configuration.</summary>
    private sealed record Plugged(IReadOnlyList<DisplayMode> Modes, User32.LUID Adapter, uint TargetId);

    public WindowsVirtualDisplays(string dataDir, ILogger log)
    {
        _dataDir = dataDir;
        _log = log;
        _setup = Environment.IsPrivilegedProcess ? Task.Run(() => Setup(dataDir)) : Task.CompletedTask;
    }

    public string? UnavailableReason =>
        !Environment.IsPrivilegedProcess
            ? "This computer can add a display only while DeskPair runs as its service (unattended access)."
            : DisplayDriver.IsRunning
                ? null
                : DisplayDriver.Installed(_dataDir) is null
                    ? "DeskPair's virtual display driver is not installed on this computer."
                    : "DeskPair's virtual display driver is installed on this computer but not running.";

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _slots.Take(ViewerSlots).Count(s => s is not null);
            }
        }
    }

    public bool IsVirtual(DisplayDescriptor display) => SlotOf(display.Name) is < ViewerSlots;

    public async Task<DisplayActionResult> AddAsync(DisplayMode? mode, IReadOnlyList<DisplayMode> sizes, CancellationToken ct)
    {
        await _setup.WaitAsync(ct).ConfigureAwait(false);
        if (UnavailableReason is { } why)
        {
            return DisplayActionResult.Refused(why);
        }

        int slot;
        lock (_lock)
        {
            slot = Array.FindIndex(_slots, 0, ViewerSlots, s => s is null);
        }

        if (slot < 0)
        {
            return DisplayActionResult.Refused($"This computer already has as many added displays as it will ({ViewerSlots}).");
        }

        return await PlugAsync(slot, mode ?? DefaultSize, sizes, ct).ConfigureAwait(false) is { } failure
            ? Failed(failure)
            : DisplayActionResult.Done;
    }

    public Task<DisplayActionResult> RemoveAsync(DisplayDescriptor display, CancellationToken ct)
    {
        if (SlotOf(display.Name) is not { } slot || slot >= ViewerSlots)
        {
            return Task.FromResult(DisplayActionResult.Refused("Only a display that was added from here can be removed."));
        }

        Unplug(slot);
        _log.LogInformation("Virtual display {Name} in slot {Slot} unplugged", display.Name, slot);
        return Task.FromResult(DisplayActionResult.Done);
    }

    public Task RemoveAllAsync(CancellationToken ct)
    {
        UnplugAll();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // No waiting: this is the engine stopping. Nothing it plugged in outlives it.
        lock (_lock)
        {
            _disposed = true;
        }

        UnplugAll();
    }

    // ---- the private session screen ----

    string? ISessionScreen.UnavailableReason => UnavailableReason;

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _slots[SessionSlot] is not null;
            }
        }
    }

    public bool IsSessionScreen(DisplayDescriptor display) => SlotOf(display.Name) == SessionSlot;

    public async Task<DisplayActionResult> OpenAsync(DisplayMode size, IReadOnlyList<DisplayMode> sizes, CancellationToken ct)
    {
        await _setup.WaitAsync(ct).ConfigureAwait(false);
        if (UnavailableReason is { } why)
        {
            return DisplayActionResult.Refused(why);
        }

        if (IsOpen)
        {
            return DisplayActionResult.Done;
        }

        // Written before anything changes: an engine that dies from here on leaves the next one a reason to look.
        File.WriteAllText(Path.Combine(_dataDir, SessionMarker), DateTimeOffset.Now.ToString("O"));
        if (await PlugAsync(SessionSlot, size, sizes, ct).ConfigureAwait(false) is { } failure)
        {
            AfterSessionScreen();
            return Failed(failure);
        }

        Plugged plugged;
        lock (_lock)
        {
            plugged = _slots[SessionSlot]!;
        }

        int isolated = DisplayTopology.OnlyThis(plugged.Adapter, plugged.TargetId);
        if (isolated != User32.ERROR_SUCCESS)
        {
            // Private or not at all: a session screen beside a lit physical one is not what the owner allowed.
            Unplug(SessionSlot);
            return Failed($"The other displays could not be turned off (SetDisplayConfig {isolated}).");
        }

        _log.LogInformation("Private session screen {Name}: {Width}x{Height}, the only display on", NameOf(SessionSlot), plugged.Modes[0].Width, plugged.Modes[0].Height);
        return DisplayActionResult.Done;
    }

    public bool IsAlone()
    {
        Plugged? plugged;
        lock (_lock)
        {
            plugged = _slots[SessionSlot];
        }

        return plugged is not null && DisplayTopology.IsOnlyOne(plugged.Adapter, plugged.TargetId);
    }

    public Task CloseAsync(CancellationToken ct)
    {
        if (IsOpen)
        {
            Unplug(SessionSlot);
            _log.LogInformation("Private session screen closed");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The session screen has gone: Windows brings back what it remembers for the displays left, and only when it has
    /// not -- nothing on at all -- is it asked to. Then the file saying a session screen was open goes.
    /// </summary>
    private void AfterSessionScreen()
    {
        string marker = Path.Combine(_dataDir, SessionMarker);
        if (!File.Exists(marker))
        {
            return;
        }

        long start = Environment.TickCount64;
        while (!DisplayTopology.AnyOn() && Environment.TickCount64 - start < 2000)
        {
            Thread.Sleep(50);
        }

        if (!DisplayTopology.AnyOn())
        {
            int restored = DisplayTopology.RestoreRemembered();
            _log.LogWarning("No display came back on after the private session screen; asked Windows for the ones it remembers ({Result})", restored);
        }

        try
        {
            File.Delete(marker);
        }
        catch (IOException e)
        {
            _log.LogWarning(e, "The private session screen's marker could not be removed");
        }
    }

    /// <summary>The sizes a display of ours can be switched to, largest first; null when it is not one of ours.</summary>
    internal IReadOnlyList<DisplayMode>? ModesOf(string gdiName)
    {
        if (SlotOf(gdiName) is not { } slot)
        {
            return null;
        }

        lock (_lock)
        {
            return _slots[slot] is { } plugged
                ? [.. plugged.Modes.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Width)]
                : null;
        }
    }

    /// <summary>
    /// Switches a display of ours to one of its sizes, and waits for Windows to follow, so that whatever reads the display
    /// next reads the new size. Null when the display is not one of ours.
    /// </summary>
    internal bool? TrySelect(string gdiName, DisplayMode mode, out string? failure)
    {
        failure = null;
        if (SlotOf(gdiName) is not { } slot)
        {
            return null;
        }

        int index;
        lock (_lock)
        {
            index = _slots[slot] is { } plugged ? plugged.Modes.ToList().FindIndex(m => m.Width == mode.Width && m.Height == mode.Height) : -1;
        }

        if (index < 0)
        {
            failure = $"{mode.Width}x{mode.Height} is not a size this display can show.";
            return false;
        }

        // Windows moves the display itself as soon as its old size is no longer on offer; once more if it has not.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            lock (_lock)
            {
                try
                {
                    DisplayDriver.Select(slot, index);
                }
                catch (Win32Exception e)
                {
                    failure = $"The virtual display driver refused the size ({e.Message}).";
                    return false;
                }
            }

            if (WaitForSize(gdiName, mode))
            {
                return true;
            }
        }

        failure = "The display did not switch.";
        return false;
    }

    private void Setup(string dataDir)
    {
        try
        {
            DisplayDriverInstaller.Update(dataDir, DisplayDriver.PackageDirectory, _log);
            if (!DisplayDriver.IsRunning)
            {
                return;
            }

            // An engine that died leaves its displays to the watchdog; the next one does not wait for it.
            IddControl.DESKPAIR_DISPLAY_INFO info = DisplayDriver.Info();
            for (int slot = 0; slot < IddControl.Slots; slot++)
            {
                if ((info.PluggedMask & (1u << slot)) != 0)
                {
                    _log.LogInformation("Virtual displays: slot {Slot} was left plugged in; unplugging it", slot);
                    DisplayDriver.Unplug(slot);
                }
            }

            DisplayDriver.Watchdog(TimeSpan.Zero);

            // A private session screen was open when the last engine stopped: it is gone now (the watchdog, or the
            // unplugging above), and a display has to be on again.
            if (File.Exists(Path.Combine(dataDir, SessionMarker)))
            {
                _log.LogInformation("Virtual displays: the last engine stopped with a private session screen open; checking the displays came back");
                AfterSessionScreen();
            }
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Virtual displays: setting up the driver failed");
        }
    }

    /// <summary>
    /// Plugs a display into <paramref name="slot"/> with the sizes chosen for the viewer, at <paramref name="start"/>, and
    /// waits for Windows to put it on the desktop. Null when it is there; otherwise why not, with the slot free again.
    /// </summary>
    private async Task<string?> PlugAsync(int slot, DisplayMode start, IReadOnlyList<DisplayMode> sizes, CancellationToken ct)
    {
        List<DisplayMode> modes = VirtualDisplaySizes.For(start, sizes);
        lock (_lock)
        {
            if (_disposed || _slots[slot] is not null)
            {
                return "That display is being plugged in already.";
            }

            try
            {
                (User32.LUID adapter, uint target) = DisplayDriver.Plug(slot, 0, modes);
                _slots[slot] = new Plugged(modes, adapter, target);
                StartFeeding();
            }
            catch (Win32Exception e)
            {
                return $"The virtual display could not be plugged in ({e.Message}).";
            }
        }

        // Arrived is not on the desktop yet: Windows gives it a source a moment later -- or never, when it is showing
        // one screen only (Win+P), which leaves nothing to stream.
        long started = Environment.TickCount64;
        string? name;
        while ((name = NameOf(slot)) is null && Environment.TickCount64 - started < AppearTimeout.TotalMilliseconds)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        if (name is null)
        {
            Unplug(slot);
            return "The display was plugged in but Windows did not put it on the desktop; with \"PC screen only\" chosen (Win+P) it shows one screen only.";
        }

        _log.LogInformation("Virtual display {Name} in slot {Slot}: {Width}x{Height}, {Count} sizes on offer", name, slot, modes[0].Width, modes[0].Height, modes.Count);
        return null;
    }

    private void UnplugAll()
    {
        for (int slot = 0; slot < IddControl.Slots; slot++)
        {
            Unplug(slot);
        }
    }

    private void Unplug(int slot)
    {
        lock (_lock)
        {
            if (_slots[slot] is null)
            {
                return;
            }

            try
            {
                DisplayDriver.Unplug(slot);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == DisplayDriver.ErrorNotFound)
            {
                // Gone already: the watchdog, or the driver restarted.
            }
            catch (Win32Exception e)
            {
                _log.LogWarning(e, "Virtual displays: unplugging slot {Slot} failed", slot);
            }

            _slots[slot] = null;
            if (Array.TrueForAll(_slots, s => s is null))
            {
                StopFeeding();
            }
        }

        if (slot == SessionSlot)
        {
            AfterSessionScreen();
        }
    }

    /// <summary>The slot whose display has this GDI name, or null.</summary>
    private int? SlotOf(string gdiName)
    {
        Plugged?[] slots;
        lock (_lock)
        {
            if (Array.TrueForAll(_slots, s => s is null))
            {
                return null;
            }

            slots = (Plugged?[])_slots.Clone();
        }

        DisplayConfiguration? configuration = DisplayConfiguration.Query();
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (slots[slot] is { } plugged
                && string.Equals(configuration?.NameOfTarget(plugged.Adapter, plugged.TargetId), gdiName, StringComparison.OrdinalIgnoreCase))
            {
                return slot;
            }
        }

        return null;
    }

    private string? NameOf(int slot)
    {
        Plugged? plugged;
        lock (_lock)
        {
            plugged = _slots[slot];
        }

        return plugged is null ? null : DisplayConfiguration.Query()?.NameOfTarget(plugged.Adapter, plugged.TargetId);
    }

    private static bool WaitForSize(string gdiName, DisplayMode mode)
    {
        long start = Environment.TickCount64;
        do
        {
            User32.DEVMODEW current = User32.DEVMODEW.Create();
            if (User32.EnumDisplaySettingsEx(gdiName, User32.ENUM_CURRENT_SETTINGS, ref current, 0) != 0
                && current.dmPelsWidth == (uint)mode.Width && current.dmPelsHeight == (uint)mode.Height)
            {
                return true;
            }

            Thread.Sleep(10);
        }
        while (Environment.TickCount64 - start < SwitchTimeout.TotalMilliseconds);

        return false;
    }

    /// <summary>Arms the watchdog and starts feeding it. Under the lock, with a display just plugged in.</summary>
    private void StartFeeding()
    {
        if (_feeding is not null)
        {
            return;
        }

        try
        {
            DisplayDriver.Watchdog(WatchdogTimeout);
        }
        catch (Win32Exception e)
        {
            _log.LogWarning(e, "Virtual displays: the driver's watchdog could not be armed");
        }

        var feeding = new CancellationTokenSource();
        _feeding = feeding;
        new Thread(() => Feed(feeding)) { IsBackground = true, Name = "Virtual display watchdog" }.Start();
    }

    /// <summary>Stops feeding the watchdog and disarms it. Under the lock, with nothing plugged in.</summary>
    private void StopFeeding()
    {
        if (_feeding is not { } feeding)
        {
            return;
        }

        _feeding = null;
        feeding.Cancel();
        try
        {
            DisplayDriver.Watchdog(TimeSpan.Zero);
        }
        catch (Win32Exception)
        {
            // Nothing is plugged in for it to unplug anyway.
        }
    }

    /// <summary>
    /// Its own thread rather than a timer: a thread pool too busy to run a timer on time must not look like an engine
    /// that died. Each feed is under the lock, so none lands after <see cref="StopFeeding"/> has disarmed it.
    /// </summary>
    private void Feed(CancellationTokenSource feeding)
    {
        bool warned = false;
        try
        {
            while (!feeding.Token.WaitHandle.WaitOne(WatchdogFeed))
            {
                lock (_lock)
                {
                    if (feeding.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        DisplayDriver.Watchdog(WatchdogTimeout);
                        warned = false;
                    }
                    catch (Win32Exception e)
                    {
                        if (!warned)
                        {
                            _log.LogWarning(e, "Virtual displays: feeding the driver's watchdog failed");
                            warned = true;
                        }
                    }
                }
            }
        }
        finally
        {
            feeding.Dispose();
        }
    }

    private DisplayActionResult Failed(string why)
    {
        _log.LogWarning("Virtual displays: {Why}", why);
        return DisplayActionResult.Refused(why);
    }
}
