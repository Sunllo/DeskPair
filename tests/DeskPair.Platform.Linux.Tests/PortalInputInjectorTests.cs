using System.Text;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Linux.Native;
using DeskPair.Platform.Linux.Wayland;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Testing;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// What the portal is told for each thing the protocol can ask, with the portal replaced by a list. The two places
/// this goes wrong first are pinned here: a point on the engine's pixel screen has to become a point on one stream in
/// that stream's logical units, and a positional key is an evdev code, not an X keycode eight higher.
/// </summary>
public class PortalInputInjectorTests
{
    private static readonly VirtualScreenRect Screen = new(0, 0, 1280, 800);

    private readonly FakePortal _portal = new();

    /// <summary>One monitor at scale 1: the engine's point is the stream's point.</summary>
    private static readonly DisplayDescriptor Single = new(0, "wayland@0,0", 0, 0, 1280, 800, 1.0, FrameRotation.None, true, 55);

    [Fact]
    public void A_move_goes_to_the_stream_under_the_point_in_its_own_units()
    {
        Create([Single]).InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, 640, 400, 0), Screen);

        _portal.Calls.ShouldBe(["motion 55 640 400"]);
    }

    [Fact]
    public void On_a_scaled_monitor_pixels_become_logical_units()
    {
        // A 1280x800 logical monitor at scale 2 shows 2560x1600 pixels; its neighbour sits at logical 1280, laid out
        // at twice that because 2 is the largest scale.
        List<DisplayDescriptor> displays = PortalCapture.Describe(
            [new PortalStream(61, "0", 0, 0, 1280, 800, true, 1, null), new PortalStream(62, "1", 1280, 0, 1920, 1080, true, 1, null)],
            [(2560, 1600), (1920, 1080)]);
        displays.Select(d => (d.X, d.Y, d.Width, d.Scale)).ShouldBe([(0, 0, 2560, 2.0), (2560, 0, 1920, 1.0)]);

        PortalInputInjector injector = Create(displays);
        injector.InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, 2000, 1000, 0), Screen);
        injector.InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, 2560 + 100, 50, 0), Screen);

        _portal.Calls.ShouldBe(["motion 61 1000 500", "motion 62 100 50"], "the right half of the scaled monitor is not its neighbour");
    }

    [Theory]
    [InlineData(-50, 400, "motion 55 0 400")]
    [InlineData(5000, 900, "motion 55 1279 799")]
    public void A_point_off_every_monitor_goes_to_the_nearest_edge(int x, int y, string expected)
    {
        Create([Single]).InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, x, y, 0), Screen);

        _portal.Calls.ShouldBe([expected]);
    }

    [Fact]
    public void A_point_in_the_gap_between_two_monitors_goes_to_the_nearer_one()
    {
        DisplayDescriptor left = Single;
        DisplayDescriptor right = new(1, "wayland@1400,0", 1400, 0, 1920, 1080, 1.0, FrameRotation.None, false, 56);

        Create([left, right]).InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, 1390, 10, 0), Screen);

        _portal.Calls.ShouldBe(["motion 56 0 10"]);
    }

    [Fact]
    public void No_monitor_means_no_motion()
    {
        PortalInputInjector.ToStream([], 10, 10).ShouldBeNull();
    }

    [Fact]
    public void A_click_is_placed_then_pressed_then_released_with_evdev_buttons()
    {
        PortalInputInjector injector = Create([Single]);
        injector.InjectMouse(new MouseInput(MouseAction.Down, MouseButtons.Right, 10, 20, 0), Screen);
        injector.InjectMouse(new MouseInput(MouseAction.Up, MouseButtons.Right, 10, 20, 0), Screen);

        _portal.Calls.ShouldBe(["motion 55 10 20", $"button {Uinput.BtnRight} down", "motion 55 10 20", $"button {Uinput.BtnRight} up"]);
    }

    [Fact]
    public void A_relative_move_is_a_delta()
    {
        Create([Single]).InjectMouse(new MouseInput(MouseAction.MoveRelative, MouseButtons.None, -3, 7, 0), Screen);

        _portal.Calls.ShouldBe(["relative -3 7"]);
    }

    /// <summary>The protocol's wheel is positive away from the user; the portal's axis is positive down and right.</summary>
    [Fact]
    public void The_wheel_turns_the_way_the_portal_counts()
    {
        PortalInputInjector injector = Create([Single]);
        injector.InjectMouse(new MouseInput(MouseAction.Wheel, MouseButtons.None, 0, 0, 3), Screen);
        injector.InjectMouse(new MouseInput(MouseAction.Wheel, MouseButtons.None, 0, 0, -1), Screen);
        injector.InjectMouse(new MouseInput(MouseAction.HorizontalWheel, MouseButtons.None, 0, 0, 2), Screen);
        injector.InjectMouse(new MouseInput(MouseAction.Wheel, MouseButtons.None, 0, 0, 0), Screen);

        _portal.Calls.ShouldBe(["axis 0 -3", "axis 0 1", "axis 1 2"]);
    }

    /// <summary>The letter of Ctrl+C arrives as a positional code; the portal takes the evdev code as it is.</summary>
    [Fact]
    public void A_positional_key_is_its_evdev_code_not_an_x_keycode()
    {
        PortalInputInjector injector = Create([Single]);
        injector.InjectKey(new KeyInput(KeyInputMode.Translate, Down: true, Code: 0, ControlKey.Control, null));
        injector.InjectKey(new KeyInput(KeyInputMode.Map, Down: true, Code: 46, ControlKey.None, null));
        injector.InjectKey(new KeyInput(KeyInputMode.Map, Down: false, Code: 46, ControlKey.None, null));
        injector.InjectKey(new KeyInput(KeyInputMode.Translate, Down: false, Code: 0, ControlKey.Control, null));

        _portal.Calls.ShouldBe([$"key {Evdev.KeyLeftCtrl} down", "key 46 down", "key 46 up", $"key {Evdev.KeyLeftCtrl} up"]);
    }

    [Fact]
    public void Typed_text_is_keysyms_so_the_layout_needs_no_key_for_it()
    {
        Create([Single]).InjectKey(new KeyInput(KeyInputMode.Translate, Down: true, Code: 0, ControlKey.None, "Aé中😀\n"));

        _portal.Calls.ShouldBe(
        [
            "keysym 0x41 down", "keysym 0x41 up",
            "keysym 0xE9 down", "keysym 0xE9 up",
            "keysym 0x1004E2D down", "keysym 0x1004E2D up",
            "keysym 0x101F600 down", "keysym 0x101F600 up",
            "keysym 0xFF0D down", "keysym 0xFF0D up",
        ]);
    }

    [Theory]
    [InlineData(0x09, 0xFF09)]
    [InlineData(0x08, 0xFF08)]
    [InlineData(0x0D, 0xFF0D)]
    [InlineData(0x07, 0)]
    [InlineData(0x7F, 0)]
    [InlineData(0x85, 0)]
    [InlineData(0x20, 0x20)]
    [InlineData(0xFF, 0xFF)]
    [InlineData(0x100, 0x1000100)]
    public void A_character_is_the_keysym_x_would_give_it(int codePoint, int keysym) =>
        PortalInputInjector.KeysymOf(new Rune(codePoint)).ShouldBe(keysym);

    [Fact]
    public void Everything_still_held_is_let_go()
    {
        PortalInputInjector injector = Create([Single]);
        injector.InjectKey(new KeyInput(KeyInputMode.Translate, Down: true, Code: 0, ControlKey.Shift, null));
        injector.InjectMouse(new MouseInput(MouseAction.Down, MouseButtons.Left, 1, 1, 0), Screen);
        _portal.Calls.Clear();

        injector.ReleaseAll();
        injector.ReleaseAll();

        _portal.Calls.ShouldBe([$"key {Evdev.KeyLeftShift} up", $"button {Uinput.BtnLeft} up"], "once, and only what was held");
    }

    [Fact]
    public void Without_the_persons_permission_nothing_is_sent_and_nothing_is_claimed()
    {
        _portal.Pointer = false;
        _portal.Keyboard = false;
        PortalInputInjector injector = Create([Single]);

        injector.InjectMouse(new MouseInput(MouseAction.Down, MouseButtons.Left, 1, 1, 0), Screen);
        injector.InjectKey(new KeyInput(KeyInputMode.Translate, Down: true, Code: 0, ControlKey.None, "x"));
        injector.SendCtrlAltDel();

        _portal.Calls.ShouldBeEmpty();
        injector.Capabilities.ShouldBe(InputCapabilities.LockScreen);
    }

    [Fact]
    public void It_claims_what_the_person_allowed_and_no_lock_keys()
    {
        InputCapabilities caps = Create([Single]).Capabilities;

        caps.ShouldBe(InputCapabilities.Mouse | InputCapabilities.Keyboard | InputCapabilities.LockScreen);
    }

    /// <summary>
    /// Queued behind a slow portal, pointer motion collapses: only where it ends matters. Nothing else does, because a
    /// click or a key between two moves has to land where it happened and in the order it happened.
    /// </summary>
    [Fact]
    public void Only_pointer_motion_is_merged_while_it_waits()
    {
        PortalSession.InputEvent Absolute(double x) => new(PortalSession.InputKind.MotionAbsolute, 55, 0, x, 1);
        PortalSession.InputEvent Relative(double dx) => new(PortalSession.InputKind.Motion, 0, 0, dx, 2);
        PortalSession.InputEvent Button() => new(PortalSession.InputKind.Button, 1, Uinput.BtnLeft, 0, 0);

        PortalSession.Merge(Absolute(1), Absolute(9)).ShouldBe(Absolute(9));
        PortalSession.Merge(Relative(3), Relative(4)).ShouldBe(new PortalSession.InputEvent(PortalSession.InputKind.Motion, 0, 0, 7, 4));
        PortalSession.Merge(Absolute(1), Relative(4)).ShouldBeNull();
        PortalSession.Merge(Absolute(1), Button()).ShouldBeNull();
        PortalSession.Merge(Button(), Button()).ShouldBeNull();
        PortalSession.Merge(new(PortalSession.InputKind.Keysym, 1, 0x61, 0, 0), new(PortalSession.InputKind.Keysym, 0, 0x61, 0, 0)).ShouldBeNull();
    }

    [Theory]
    [InlineData("plain ascii ~!@#", false)]
    [InlineData("tab\tand\nnewline", false)]
    [InlineData("caf\u00e9", true)]
    [InlineData("\u4e2d\u6587", true)]
    [InlineData("\U0001F600", true)]
    public void Text_beyond_printable_ascii_is_pasted(string text, bool paste) => PortalInputInjector.NeedsPaste(text).ShouldBe(paste);

    /// <summary>
    /// GNOME drops a keysym the layout has no key for, so text like that is pasted: the clipboard holds it for a moment,
    /// Ctrl+V goes as positional keys, and what was on the clipboard before is put back.
    /// </summary>
    [Fact]
    public void Text_the_layout_cannot_type_is_pasted_and_the_clipboard_put_back()
    {
        var clipboard = new FakeClipboard();
        clipboard.SetText("before");
        var pasted = new List<string?>();
        var injector = new PortalInputInjector(_portal, () => [Single], NullLogger.Instance, clipboard, _ => pasted.Add(clipboard.Text));

        injector.InjectKey(new KeyInput(KeyInputMode.Translate, Down: true, Code: 0, ControlKey.None, "\u4e2d\u6587"));

        pasted[0].ShouldBe("\u4e2d\u6587", "the text is on the clipboard when Ctrl+V goes");
        _portal.Calls.ShouldBe([$"key {Evdev.KeyLeftCtrl} down", "key 47 down", "key 47 up", $"key {Evdev.KeyLeftCtrl} up"]);
        clipboard.Text.ShouldBe("before");
    }

    [Fact]
    public void Without_a_clipboard_the_text_goes_as_keysyms_anyway()
    {
        Create([Single]).InjectKey(new KeyInput(KeyInputMode.Translate, Down: true, Code: 0, ControlKey.None, "\u00e9"));

        _portal.Calls.ShouldBe(["keysym 0xE9 down", "keysym 0xE9 up"]);
    }

    [Theory]
    [InlineData(PortalFailure.Refused, "declined")]
    [InlineData(PortalFailure.TimedOut, "nobody answered")]
    [InlineData(PortalFailure.Unavailable, "xdg-desktop-portal-gnome")]
    [InlineData(PortalFailure.Failed, "did not start")]
    public void A_failed_start_is_told_to_viewers_in_words(PortalFailure reason, string expected) =>
        PortalHost.Describe(new PortalException(reason, "the portal backend is missing (xdg-desktop-portal-gnome on GNOME)")).ShouldContain(expected);

    private PortalInputInjector Create(IReadOnlyList<DisplayDescriptor> displays) => new(_portal, () => displays, NullLogger.Instance);

    private sealed class FakePortal : IPortalInput
    {
        public List<string> Calls { get; } = [];

        public bool Pointer { get; set; } = true;

        public bool Keyboard { get; set; } = true;

        public void PointerMotionAbsolute(uint stream, double x, double y) => Calls.Add(FormattableString.Invariant($"motion {stream} {x} {y}"));

        public void PointerMotion(double dx, double dy) => Calls.Add(FormattableString.Invariant($"relative {dx} {dy}"));

        public void PointerButton(int button, bool pressed) => Calls.Add($"button {button} {(pressed ? "down" : "up")}");

        public void PointerAxisDiscrete(uint axis, int steps) => Calls.Add($"axis {axis} {steps}");

        public void KeyboardKeycode(int keycode, bool pressed) => Calls.Add($"key {keycode} {(pressed ? "down" : "up")}");

        public void KeyboardKeysym(int keysym, bool pressed) => Calls.Add($"keysym 0x{keysym:X} {(pressed ? "down" : "up")}");
    }
}
