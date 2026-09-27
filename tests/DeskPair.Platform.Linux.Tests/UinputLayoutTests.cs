using System.Runtime.InteropServices;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The uinput ABI, derived from the kernel's declarations, and the key tables that sit on top of it.
///
/// The ioctl numbers were confirmed on a real machine on 2026-09-23 — a keyboard and two pointers made
/// with exactly these values woke the display, clicked the login screen's user tile and typed into its
/// password field. This file keeps them from drifting.
/// </summary>
public class UinputLayoutTests
{
    // struct input_event on a 64-bit kernel: struct timeval { long, long } + u16 + u16 + s32
    [Fact]
    public void input_event_is_24_bytes() => Marshal.SizeOf<Uinput.InputEvent>().ShouldBe(24);

    // struct uinput_setup { struct input_id (4 x u16); char name[80]; u32 ff_effects_max; }
    [Fact]
    public void uinput_setup_is_92_bytes() => Marshal.SizeOf<Uinput.DeviceSetup>().ShouldBe(92);

    // struct uinput_abs_setup { u16 code; (2 pad) struct input_absinfo (6 x s32) }
    [Fact]
    public void uinput_abs_setup_is_28_bytes() => Marshal.SizeOf<Uinput.AbsSetup>().ShouldBe(28);

    [Theory]
    [InlineData(Uinput.IocNone, 1, 0u, Uinput.UiDevCreate)]
    [InlineData(Uinput.IocNone, 2, 0u, Uinput.UiDevDestroy)]
    [InlineData(Uinput.IocWrite, 3, 92u, Uinput.UiDevSetup)]
    [InlineData(Uinput.IocWrite, 4, 28u, Uinput.UiAbsSetup)]
    [InlineData(Uinput.IocWrite, 100, 4u, Uinput.UiSetEvBit)]
    [InlineData(Uinput.IocWrite, 101, 4u, Uinput.UiSetKeyBit)]
    [InlineData(Uinput.IocWrite, 102, 4u, Uinput.UiSetRelBit)]
    [InlineData(Uinput.IocWrite, 103, 4u, Uinput.UiSetAbsBit)]
    public void Each_ioctl_number_is_its_definition(uint direction, uint number, uint size, uint expected)
    {
        Uinput.Ioc(direction, 'U', number, size).ShouldBe(expected);
    }

    [Fact]
    public void The_device_name_fits_the_80_byte_field_and_is_terminated()
    {
        Uinput.DeviceSetup setup = Uinput.DeviceSetup.For(new string('x', 200), 1);

        unsafe
        {
            setup.Name[78].ShouldBe((byte)'x');
            setup.Name[79].ShouldBe((byte)0);
        }
    }

    /// <summary>
    /// Every named key the protocol can send is either delivered or deliberately not. A key that falls
    /// through to 0 by accident is a key that silently does nothing on the login screen.
    /// </summary>
    [Fact]
    public void Every_control_key_is_mapped_or_on_the_list_of_ones_that_are_not()
    {
        ControlKey[] unmapped = [ControlKey.None];

        foreach (ControlKey key in Enum.GetValues<ControlKey>())
        {
            ushort code = Evdev.ForControl(key);
            if (unmapped.Contains(key))
            {
                code.ShouldBe((ushort)0, $"{key} is listed as unmapped");
            }
            else
            {
                code.ShouldNotBe((ushort)0, $"{key} has no evdev code");
                code.ShouldBeLessThanOrEqualTo(Evdev.MaxKey, $"{key} is beyond what the keyboard advertises");
            }
        }
    }

    /// <summary>The same physical key on X and on uinput: X numbers it eight higher, nothing else differs.</summary>
    [Theory]
    [InlineData(ControlKey.Return, 0xFF0D)]
    [InlineData(ControlKey.Escape, 0xFF1B)]
    [InlineData(ControlKey.Tab, 0xFF09)]
    public void A_control_key_names_a_real_key(ControlKey key, uint keysym)
    {
        Keysyms.ForControl(key).ShouldBe((nuint)keysym);
        Evdev.ForControl(key).ShouldNotBe((ushort)0);
    }

    [Theory]
    [InlineData('a', 30, false)]
    [InlineData('A', 30, true)]
    [InlineData('1', 2, false)]
    [InlineData('!', 2, true)]
    [InlineData('/', 53, false)]
    [InlineData('?', 53, true)]
    [InlineData('\'', 40, false)]
    [InlineData('"', 40, true)]
    [InlineData('\\', 43, false)]
    [InlineData('|', 43, true)]
    [InlineData(' ', 57, false)]
    [InlineData('\n', 28, false)]
    public void A_character_is_a_key_and_a_shift_on_the_us_layout(char c, int code, bool shift)
    {
        Evdev.ForChar(c).ShouldBe(((ushort)code, shift));
    }

    /// <summary>Anything outside printable ASCII has no key on this table, and says so rather than guessing.</summary>
    [Theory]
    [InlineData('é')]
    [InlineData('中')]
    [InlineData('\u0001')]
    public void A_character_off_the_us_layout_is_not_typed(char c)
    {
        Evdev.ForChar(c).ShouldBeNull();
    }

    [Fact]
    public void Every_printable_ascii_character_is_typeable()
    {
        for (char c = ' '; c <= '~'; c++)
        {
            Evdev.ForChar(c).ShouldNotBeNull($"'{c}' (0x{(int)c:x2}) has no key");
        }
    }
}
