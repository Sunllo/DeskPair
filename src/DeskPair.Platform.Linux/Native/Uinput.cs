using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// <c>/dev/uinput</c>: a keyboard and a pointer the kernel believes in.
///
/// XTest reaches only an X server, and at the login screen and the lock screen there is none to reach —
/// GNOME's greeter is a Wayland compositor, and its lock screen is drawn inside the session. A uinput
/// device sits below all of that: to libinput it is hardware, so whatever is on screen receives the
/// events the way it would from a real keyboard. The device is root-only to create, which is why it is
/// made once by the daemon and its descriptor is passed down.
///
/// Numbers are the kernel's, computed the way <c>linux/ioctl.h</c> computes them, and checked against
/// the values measured on a real machine in UinputLayoutTests.
/// </summary>
internal static partial class Uinput
{
    private const string Lib = "libc";

    public const string DevicePath = "/dev/uinput";

    public const ushort EvSyn = 0;
    public const ushort EvKey = 1;
    public const ushort EvRel = 2;
    public const ushort EvAbs = 3;

    public const ushort SynReport = 0;
    public const ushort RelX = 0;
    public const ushort RelY = 1;
    public const ushort RelHWheel = 6;
    public const ushort RelWheel = 8;
    public const ushort AbsX = 0;
    public const ushort AbsY = 1;

    public const ushort BtnLeft = 0x110;
    public const ushort BtnRight = 0x111;
    public const ushort BtnMiddle = 0x112;
    public const ushort BtnSide = 0x113;
    public const ushort BtnExtra = 0x114;

    /// <summary>The absolute axis range: full-scale, so one unit is finer than any screen.</summary>
    public const int AbsMax = 65535;

    public const ushort BusUsb = 3;

    // _IOC(dir, type, nr, size) = dir << 30 | size << 16 | type << 8 | nr, with 'U' = 0x55.
    public const uint UiDevCreate = 0x5501;
    public const uint UiDevDestroy = 0x5502;
    public const uint UiDevSetup = 0x405C5503;
    public const uint UiAbsSetup = 0x401C5504;
    public const uint UiSetEvBit = 0x40045564;
    public const uint UiSetKeyBit = 0x40045565;
    public const uint UiSetRelBit = 0x40045566;
    public const uint UiSetAbsBit = 0x40045567;

    /// <summary>The generic ioctl encoding, so a test can derive each number above from its C definition.</summary>
    public static uint Ioc(uint direction, uint type, uint number, uint size) =>
        (direction << 30) | (size << 16) | (type << 8) | number;

    public const uint IocNone = 0;
    public const uint IocWrite = 1;
    public const uint IocRead = 2;

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int ioctl(int fd, nuint request, int argument);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int ioctl(int fd, nuint request, ref DeviceSetup argument);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int ioctl(int fd, nuint request, ref AbsSetup argument);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int ioctl(int fd, nuint request);

    [LibraryImport(Lib, SetLastError = true)]
    public static unsafe partial nint write(int fd, void* buffer, nuint count);

    /// <summary><c>struct input_id</c>: who the kernel says made this device.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InputId
    {
        public ushort BusType;
        public ushort Vendor;
        public ushort Product;
        public ushort Version;
    }

    /// <summary><c>struct uinput_setup</c>: the id, an 80-byte name, and a count of force-feedback effects.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct DeviceSetup
    {
        public InputId Id;
        public fixed byte Name[80];
        public uint FfEffectsMax;

        public static DeviceSetup For(string name, ushort product)
        {
            var setup = new DeviceSetup { Id = new InputId { BusType = BusUsb, Vendor = 0x5344, Product = product, Version = 1 } };
            int n = Math.Min(System.Text.Encoding.ASCII.GetByteCount(name), 79);
            Span<byte> bytes = stackalloc byte[80];
            System.Text.Encoding.ASCII.GetBytes(name.AsSpan(0, Math.Min(name.Length, 79)), bytes);
            for (int i = 0; i < n; i++)
            {
                setup.Name[i] = bytes[i];
            }

            return setup;
        }
    }

    /// <summary><c>struct input_absinfo</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AbsInfo
    {
        public int Value;
        public int Minimum;
        public int Maximum;
        public int Fuzz;
        public int Flat;
        public int Resolution;
    }

    /// <summary><c>struct uinput_abs_setup</c>: the axis code, two bytes of padding, and its range.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AbsSetup
    {
        public ushort Code;
        public ushort Pad;
        public AbsInfo Info;
    }

    /// <summary><c>struct input_event</c> on a 64-bit kernel: a timeval the kernel fills in, then type, code, value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InputEvent
    {
        public long Seconds;
        public long Microseconds;
        public ushort Type;
        public ushort Code;
        public int Value;
    }

    /// <summary>Writes one event. A batch ends with a SYN_REPORT, or the kernel holds it.</summary>
    public static unsafe bool Emit(int fd, ushort type, ushort code, int value)
    {
        var e = new InputEvent { Type = type, Code = code, Value = value };
        return write(fd, &e, (nuint)sizeof(InputEvent)) == sizeof(InputEvent);
    }
}
