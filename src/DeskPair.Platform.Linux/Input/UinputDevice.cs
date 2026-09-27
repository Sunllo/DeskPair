using System.Runtime.InteropServices;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Input;

/// <summary>
/// The keyboard and pointer the daemon creates once and keeps for the life of the machine.
///
/// Three devices rather than one, and the split was measured, not guessed. A node that advertises keys
/// as well as absolute axes is a tablet to libinput, not a keyboard plus a mouse; and a pointer that
/// advertises both absolute and relative axes is treated as a relative mouse, its absolute axes ignored
/// -- a click sent to (640,350) through such a device landed nowhere, while the same click through an
/// absolute-only device opened the login screen's password field. So: a keyboard, an absolute pointer
/// that carries the buttons, and a relative pointer that carries the wheel and the nudge that wakes a
/// sleeping display. Created at daemon start rather than per connection because udev and libinput take
/// hundreds of milliseconds to notice a new device and the compositor has to enumerate it, so a device
/// made when a viewer connects loses that viewer's first keystrokes.
///
/// The daemon holds these descriptors open for ever: closing the last descriptor on a created uinput
/// device destroys it. Copies go to the engine over the socketpair; the engine writing events on its
/// copy is the same as the daemon writing them.
/// </summary>
public sealed class UinputDevice : IDisposable
{
    private readonly ILogger _log;

    private UinputDevice(int keyboard, int pointer, int relative, ILogger log)
    {
        Keyboard = keyboard;
        Pointer = pointer;
        Relative = relative;
        _log = log;
    }

    public int Keyboard { get; }

    /// <summary>The absolute pointer: placement and buttons.</summary>
    public int Pointer { get; }

    /// <summary>The relative pointer: deltas, wheels, and the nudge.</summary>
    public int Relative { get; }

    /// <summary>Creates both devices. Root only, and <c>/dev/uinput</c> has to exist (the module is built in on Ubuntu).</summary>
    public static UinputDevice Create(ILogger log)
    {
        int keyboard = Open();
        try
        {
            Set(keyboard, Uinput.UiSetEvBit, Uinput.EvKey);
            for (int code = 1; code <= Evdev.MaxKey; code++)
            {
                Set(keyboard, Uinput.UiSetKeyBit, code);
            }

            Setup(keyboard, "DeskPair keyboard", product: 1);
        }
        catch
        {
            _ = UnixSocketMsg.close(keyboard);
            throw;
        }

        int pointer = Open();
        try
        {
            Set(pointer, Uinput.UiSetEvBit, Uinput.EvKey);
            foreach (ushort button in (ushort[])[Uinput.BtnLeft, Uinput.BtnRight, Uinput.BtnMiddle, Uinput.BtnSide, Uinput.BtnExtra])
            {
                Set(pointer, Uinput.UiSetKeyBit, button);
            }

            // Absolute, and only absolute: placement the engine cannot otherwise know, and the buttons,
            // so a click happens where the pointer was just put.
            Set(pointer, Uinput.UiSetEvBit, Uinput.EvAbs);
            foreach (ushort axis in (ushort[])[Uinput.AbsX, Uinput.AbsY])
            {
                Set(pointer, Uinput.UiSetAbsBit, axis);
                var abs = new Uinput.AbsSetup { Code = axis, Info = new Uinput.AbsInfo { Minimum = 0, Maximum = Uinput.AbsMax } };
                if (Uinput.ioctl(pointer, Uinput.UiAbsSetup, ref abs) != 0)
                {
                    throw new IOException($"UI_ABS_SETUP failed: errno {Marshal.GetLastPInvokeError()}");
                }
            }

            Setup(pointer, "DeskPair pointer", product: 2);
        }
        catch
        {
            _ = UnixSocketMsg.close(pointer);
            _ = Uinput.ioctl(keyboard, Uinput.UiDevDestroy);
            _ = UnixSocketMsg.close(keyboard);
            throw;
        }

        int relative = Open();
        try
        {
            // Relative, and only relative: the protocol's relative moves, the wheels, and the one-pixel
            // nudge that wakes a display the compositor has switched off.
            Set(relative, Uinput.UiSetEvBit, Uinput.EvKey);
            Set(relative, Uinput.UiSetKeyBit, Uinput.BtnLeft); // a mouse with no button at all is not a mouse to libinput
            Set(relative, Uinput.UiSetEvBit, Uinput.EvRel);
            foreach (ushort axis in (ushort[])[Uinput.RelX, Uinput.RelY, Uinput.RelWheel, Uinput.RelHWheel])
            {
                Set(relative, Uinput.UiSetRelBit, axis);
            }

            Setup(relative, "DeskPair wheel", product: 3);
        }
        catch
        {
            _ = UnixSocketMsg.close(relative);
            _ = Uinput.ioctl(pointer, Uinput.UiDevDestroy);
            _ = UnixSocketMsg.close(pointer);
            _ = Uinput.ioctl(keyboard, Uinput.UiDevDestroy);
            _ = UnixSocketMsg.close(keyboard);
            throw;
        }

        log.LogInformation("Virtual keyboard, pointer and wheel created through {Path}", Uinput.DevicePath);
        return new UinputDevice(keyboard, pointer, relative, log);
    }

    /// <summary>
    /// Lifts every modifier and button, for when the engine died without doing so itself. A held Ctrl
    /// left behind by a dropped connection makes the physical keyboard unusable until something releases
    /// it, and the daemon cannot know what the engine had down; so it lifts the bounded set that matters.
    /// </summary>
    public void PanicRelease()
    {
        int released = 0;
        foreach (ushort key in (ushort[])[Evdev.KeyLeftCtrl, Evdev.KeyRightCtrl, Evdev.KeyLeftAlt, Evdev.KeyRightAlt,
                     Evdev.KeyLeftShift, Evdev.KeyRightShift, Evdev.KeyLeftMeta, Evdev.KeyRightMeta])
        {
            if (Uinput.Emit(Keyboard, Uinput.EvKey, key, 0))
            {
                released++;
            }
        }

        _ = Uinput.Emit(Keyboard, Uinput.EvSyn, Uinput.SynReport, 0);
        foreach (ushort button in (ushort[])[Uinput.BtnLeft, Uinput.BtnRight, Uinput.BtnMiddle, Uinput.BtnSide, Uinput.BtnExtra])
        {
            if (Uinput.Emit(Pointer, Uinput.EvKey, button, 0))
            {
                released++;
            }
        }

        _ = Uinput.Emit(Pointer, Uinput.EvSyn, Uinput.SynReport, 0);
        _log.LogInformation("Released {Count} keys and buttons the engine may have left down", released);
    }

    private static int Open()
    {
        int fd = LibC.open(Uinput.DevicePath, LibC.O_WRONLY | LibC.O_NONBLOCK | LibC.O_CLOEXEC);
        return fd >= 0 ? fd : throw new IOException($"Could not open {Uinput.DevicePath}: errno {Marshal.GetLastPInvokeError()}");
    }

    private static void Set(int fd, uint request, int value)
    {
        if (Uinput.ioctl(fd, request, value) != 0)
        {
            throw new IOException($"uinput ioctl 0x{request:x} ({value}) failed: errno {Marshal.GetLastPInvokeError()}");
        }
    }

    private static void Setup(int fd, string name, ushort product)
    {
        Uinput.DeviceSetup setup = Uinput.DeviceSetup.For(name, product);
        if (Uinput.ioctl(fd, Uinput.UiDevSetup, ref setup) != 0)
        {
            throw new IOException($"UI_DEV_SETUP failed for {name}: errno {Marshal.GetLastPInvokeError()}");
        }

        if (Uinput.ioctl(fd, Uinput.UiDevCreate) != 0)
        {
            throw new IOException($"UI_DEV_CREATE failed for {name}: errno {Marshal.GetLastPInvokeError()}");
        }
    }

    public void Dispose()
    {
        foreach (int fd in (int[])[Keyboard, Pointer, Relative])
        {
            _ = Uinput.ioctl(fd, Uinput.UiDevDestroy);
            _ = UnixSocketMsg.close(fd);
        }
    }
}
