using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Linux.Input;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// What the virtual keyboard and pointer are told, for each thing the protocol can ask. The device is
/// replaced by a list, so this runs anywhere; what it pins is the translation, which is where a login
/// typed remotely goes wrong quietly.
/// </summary>
public class UinputInputInjectorTests
{
    private const int Keyboard = 10;
    private const int Pointer = 11;
    private const int Relative = 12;

    private readonly List<(int Fd, ushort Type, ushort Code, int Value)> _events = [];

    private readonly List<TimeSpan> _pauses = [];

    private UinputInputInjector Create() =>
        new(Keyboard, Pointer, Relative, NullLogger.Instance, (fd, type, code, value) => _events.Add((fd, type, code, value)), () => null, _pauses.Add);

    private static readonly VirtualScreenRect Screen = new(0, 0, 1280, 800);

    /// <summary>
    /// The daemon on a machine without /dev/uinput hands over no devices. Every event is then dropped
    /// rather than written to descriptor -1, and the nudge that wakes a sleeping display does nothing.
    /// </summary>
    [Fact]
    public void Without_devices_every_event_is_dropped()
    {
        var injector = new UinputInputInjector(-1, -1, -1, NullLogger.Instance, (fd, type, code, value) => _events.Add((fd, type, code, value)), () => null);

        injector.HasDevices.ShouldBeFalse();
        Create().HasDevices.ShouldBeTrue();
        injector.InjectMouse(new MouseInput(MouseAction.Down, MouseButtons.Left, 10, 10, 0), Screen);
        injector.InjectKey(new KeyInput(KeyInputMode.Map, Down: true, Code: 0, ControlKey.Return, null));
        injector.Nudge();
        injector.ReleaseAll();

        _events.ShouldBeEmpty();
    }

    [Fact]
    public void It_claims_exactly_what_it_can_do()
    {
        InputCapabilities caps = Create().Capabilities;

        caps.ShouldBe(InputCapabilities.Mouse | InputCapabilities.Keyboard | InputCapabilities.LockScreen);
        caps.HasFlag(InputCapabilities.SecureAttention).ShouldBeFalse("Ctrl+Alt+Del through uinput reboots a Linux console");
        caps.HasFlag(InputCapabilities.LockKeys).ShouldBeFalse("the lamps cannot be read back");
    }

    /// <summary>A pixel position becomes the axis value of the pixel's centre.</summary>
    [Theory]
    [InlineData(0, 0, 25, 40)]
    [InlineData(1279, 799, 65510, 65495)]
    [InlineData(640, 400, 32793, 32808)]
    public void A_move_places_the_pointer_absolutely(int x, int y, int ax, int ay)
    {
        Create().InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, x, y, 0), Screen);

        _events.ShouldBe(
        [
            (Pointer, Uinput.EvAbs, Uinput.AbsX, ax),
            (Pointer, Uinput.EvAbs, Uinput.AbsY, ay),
            (Pointer, Uinput.EvSyn, Uinput.SynReport, 0),
        ]);
    }

    /// <summary>
    /// Every pixel comes back as itself when the value is scaled as libinput scales it for the compositor, (value -
    /// minimum) x size / (maximum - minimum + 1): the edges, and the first pixels, which the old aim at the pixel's
    /// edge brought back one short.
    /// </summary>
    [Theory]
    [InlineData(800)]
    [InlineData(1280)]
    [InlineData(1600)]
    [InlineData(7680)]
    public void Every_pixel_comes_back_as_itself_through_libinputs_scaling(int size)
    {
        for (int pixel = 0; pixel < size; pixel++)
        {
            long value = UinputInputInjector.Axis(pixel, size);
            value.ShouldBeInRange(0, Uinput.AbsMax);
            ((int)(value * size / (Uinput.AbsMax + 1L))).ShouldBe(pixel);
        }
    }

    [Fact]
    public void A_click_is_placed_then_pressed_then_released()
    {
        UinputInputInjector injector = Create();
        injector.InjectMouse(new MouseInput(MouseAction.Down, MouseButtons.Left, 10, 10, 0), Screen);
        injector.InjectMouse(new MouseInput(MouseAction.Up, MouseButtons.Left, 10, 10, 0), Screen);

        _events.Where(e => e.Type == Uinput.EvKey).ShouldBe(
        [
            (Pointer, Uinput.EvKey, Uinput.BtnLeft, 1),
            (Pointer, Uinput.EvKey, Uinput.BtnLeft, 0),
        ]);
    }

    [Fact]
    public void A_wheel_delta_is_one_notch_per_unit_in_its_direction()
    {
        Create().InjectMouse(new MouseInput(MouseAction.Wheel, MouseButtons.None, 0, 0, -3), Screen);

        _events.Where(e => e.Type == Uinput.EvRel).ShouldBe(
        [
            (Relative, Uinput.EvRel, Uinput.RelWheel, -1),
            (Relative, Uinput.EvRel, Uinput.RelWheel, -1),
            (Relative, Uinput.EvRel, Uinput.RelWheel, -1),
        ]);
    }

    /// <summary>The protocol's Map code is the evdev code: no +8, no table, exactly what X11 undoes.</summary>
    [Fact]
    public void A_positional_key_is_sent_as_its_own_code()
    {
        Create().InjectKey(new KeyInput(KeyInputMode.Map, Down: true, Code: 30, ControlKey.None, null));

        _events.ShouldBe([(Keyboard, Uinput.EvKey, 30, 1), (Keyboard, Uinput.EvSyn, Uinput.SynReport, 0)]);
    }

    [Fact]
    public void A_control_key_is_looked_up()
    {
        Create().InjectKey(new KeyInput(KeyInputMode.Map, Down: true, Code: 0, ControlKey.Return, null));

        _events[0].ShouldBe((Keyboard, Uinput.EvKey, Evdev.KeyEnter, 1));
    }

    /// <summary>Typing "A1" is Shift down, a down, a up, Shift up, then 1 down, 1 up: each character complete on its own.</summary>
    [Fact]
    public void Typed_text_presses_and_releases_each_character_with_shift_where_needed()
    {
        Create().InjectKey(new KeyInput(KeyInputMode.Translate, Down: true, Code: 0, ControlKey.None, "A1"));

        _events.Where(e => e.Type == Uinput.EvKey).ShouldBe(
        [
            (Keyboard, Uinput.EvKey, Evdev.KeyLeftShift, 1),
            (Keyboard, Uinput.EvKey, 30, 1),
            (Keyboard, Uinput.EvKey, 30, 0),
            (Keyboard, Uinput.EvKey, Evdev.KeyLeftShift, 0),
            (Keyboard, Uinput.EvKey, 2, 1),
            (Keyboard, Uinput.EvKey, 2, 0),
        ]);
    }

    /// <summary>
    /// A long string is paced, one pause between each two characters and none before the first: written in one
    /// burst it overflowed the compositor's event queue, and the lab machine received "abcdefg" of the alphabet.
    /// </summary>
    [Fact]
    public void Typed_text_leaves_the_compositor_time_to_read_between_characters()
    {
        UinputInputInjector injector = Create();
        injector.InjectKey(new KeyInput(KeyInputMode.Translate, Down: true, Code: 0, ControlKey.None, "abcdefghijklmnopqrstuvwxyz"));

        _events.Count(e => e.Type == Uinput.EvKey && e.Value == 1).ShouldBe(26);
        _pauses.ShouldBe(Enumerable.Repeat(UinputInputInjector.CharacterInterval, 25));

        _pauses.Clear();
        injector.InjectKey(new KeyInput(KeyInputMode.Translate, Down: true, Code: 0, ControlKey.None, "x"));
        _pauses.ShouldBeEmpty("a single keystroke, which is what typing sends, waits for nothing");
    }

    /// <summary>A character the US table cannot type is dropped, never replaced with a different one.</summary>
    [Fact]
    public void A_character_off_the_layout_is_not_typed_as_something_else()
    {
        Create().InjectKey(new KeyInput(KeyInputMode.Translate, Down: true, Code: 0, ControlKey.None, "é"));

        _events.ShouldBeEmpty();
    }

    /// <summary>Whatever is still down when the session ends is lifted, so the physical keyboard is usable afterwards.</summary>
    [Fact]
    public async Task Disposing_lifts_every_key_and_button_still_down()
    {
        UinputInputInjector injector = Create();
        injector.InjectKey(new KeyInput(KeyInputMode.Map, Down: true, Code: 0, ControlKey.Control, null));
        injector.InjectMouse(new MouseInput(MouseAction.Down, MouseButtons.Right, 5, 5, 0), Screen);
        _events.Clear();

        await injector.DisposeAsync();

        _events.Where(e => e.Type == Uinput.EvKey).ShouldBe(
        [
            (Keyboard, Uinput.EvKey, Evdev.KeyLeftCtrl, 0),
            (Pointer, Uinput.EvKey, Uinput.BtnRight, 0),
        ]);
    }

    /// <summary>A nudge is a move and its exact undo: enough to wake a display, not enough to be seen.</summary>
    [Fact]
    public void A_nudge_moves_the_pointer_and_puts_it_back()
    {
        Create().Nudge();

        _events.Where(e => e.Type == Uinput.EvRel).Sum(e => e.Value).ShouldBe(0);
        _events.Count(e => e.Type == Uinput.EvRel).ShouldBe(2);
    }

    /// <summary>
    /// Absolute placement and buttons on one device, deltas and wheels on another. Measured: a device
    /// carrying both kinds of axis is a relative mouse to libinput, and its absolute axes are ignored --
    /// so a click sent through it landed nowhere on the login screen.
    /// </summary>
    [Fact]
    public void Placement_and_buttons_go_to_the_absolute_device_and_deltas_to_the_relative_one()
    {
        UinputInputInjector injector = Create();
        injector.InjectMouse(new MouseInput(MouseAction.Down, MouseButtons.Left, 10, 10, 0), Screen);
        injector.InjectMouse(new MouseInput(MouseAction.MoveRelative, MouseButtons.None, 3, -2, 0), Screen);
        injector.Nudge();

        _events.Where(e => e.Type is Uinput.EvAbs or Uinput.EvKey).Select(e => e.Fd).Distinct().ShouldBe([Pointer]);
        _events.Where(e => e.Type == Uinput.EvRel).Select(e => e.Fd).Distinct().ShouldBe([Relative]);
    }

    [Fact]
    public void Ctrl_alt_del_does_nothing_here()
    {
        Create().SendCtrlAltDel();

        _events.ShouldBeEmpty();
    }

    [Fact]
    public void Locking_is_asked_of_the_seat_each_time_and_a_refusal_sends_no_key()
    {
        // The engine outlives sessions: started at the login screen, still running after sign-in. So the
        // lock is a request made per call, never a session remembered from start-up; and a refusal is a
        // log line, not a keystroke the greeter would see.
        int asked = 0;
        var injector = new UinputInputInjector(Keyboard, Pointer, Relative, NullLogger.Instance,
            (fd, type, code, value) => _events.Add((fd, type, code, value)), () => { asked++; return "nobody is signed in"; });

        injector.LockWorkstation();
        injector.LockWorkstation();

        asked.ShouldBe(2);
        _events.ShouldBeEmpty();
    }
}
