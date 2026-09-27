using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Linux.Capture;
using DeskPair.Platform.Linux.Input;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The one genuinely end-to-end check that needs no second machine: inject through XTest, read back through
/// the X server. These move the real pointer of whoever is using the machine, so they are opt-in behind
/// SUNLLO_TEST_INPUT and sit in their own collection — an injected move changes the screen, which would
/// perturb the capture assertions if they ran alongside.
/// </summary>
[Collection("x11-input")]
public class X11InputTests
{
    private static bool Skip => !X11Session.CanInject || !X11Session.AllowInput || !X11Session.HasCursor;

    [Fact]
    public async Task An_injected_move_lands_where_it_was_aimed()
    {
        if (Skip)
        {
            return;
        }

        using var displays = new X11DisplayEnumerator(NullLogger<X11DisplayEnumerator>.Instance);
        DisplayDescriptor screen = displays.GetDisplays()[0];
        await using var cursor = new X11CursorProvider(NullLogger<X11CursorProvider>.Instance);
        await using var injector = new X11InputInjector(NullLogger<X11InputInjector>.Instance, null);

        (int X, int Y)? before = cursor.GetCursorPosition();
        before.ShouldNotBeNull();
        var bounds = new VirtualScreenRect(0, 0, screen.Width, screen.Height);
        try
        {
            // Deliberately not a round number and well inside the screen, so "it was already there" cannot pass.
            foreach ((int x, int y) in new[] { (screen.Width / 2 + 37, screen.Height / 2 + 23), (screen.Width / 3 + 11, screen.Height / 3 + 7) })
            {
                injector.InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, x, y, 0), bounds);
                (int X, int Y) landed = await SettleAsync(cursor, x, y);
                landed.X.ShouldBeInRange(x - 1, x + 1);
                landed.Y.ShouldBeInRange(y - 1, y + 1);

                // A second, independent readback: XFixes and the server agreeing rules out the cursor provider.
                QueryPointer(out int qx, out int qy, out _);
                qx.ShouldBeInRange(x - 1, x + 1);
                qy.ShouldBeInRange(y - 1, y + 1);
            }
        }
        finally
        {
            injector.InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, before!.Value.X, before.Value.Y, 0), bounds);
        }
    }

    /// <summary>A button left down on someone's desk is a severe bug, and ReleaseAll had no coverage at all.</summary>
    [Fact]
    public async Task Release_all_leaves_no_button_held()
    {
        if (Skip)
        {
            return;
        }

        using var displays = new X11DisplayEnumerator(NullLogger<X11DisplayEnumerator>.Instance);
        DisplayDescriptor screen = displays.GetDisplays()[0];
        await using var injector = new X11InputInjector(NullLogger<X11InputInjector>.Instance, null);
        var bounds = new VirtualScreenRect(0, 0, screen.Width, screen.Height);
        const uint Button1Mask = 1 << 8;

        try
        {
            injector.InjectMouse(new MouseInput(MouseAction.Down, MouseButtons.Left, screen.Width / 2, screen.Height / 2, 0), bounds);
            await Task.Delay(120);
            QueryPointer(out _, out _, out uint held);
            (held & Button1Mask).ShouldBe(Button1Mask, "the press did not register, so the release proves nothing");
        }
        finally
        {
            injector.ReleaseAll();
        }

        await Task.Delay(120);
        QueryPointer(out _, out _, out uint after);
        (after & Button1Mask).ShouldBe(0u);
    }

    /// <summary>Needs no injection, so it runs wherever there is a display: the mapping the typing path depends on.</summary>
    [Fact]
    public void Every_printable_ascii_character_maps_to_a_keycode()
    {
        if (!X11Session.IsAvailable)
        {
            return;
        }

        nint dpy = Xlib.XOpenDisplay(null);
        dpy.ShouldNotBe(0);
        try
        {
            for (char c = ' '; c <= '~'; c++)
            {
                XTest.XKeysymToKeycode(dpy, c).ShouldNotBe((byte)0, $"no keycode for '{c}'");
            }

            XTest.XKeysymToKeycode(dpy, 0xFFE1).ShouldNotBe((byte)0, "no keycode for Shift_L");
        }
        finally
        {
            Xlib.XCloseDisplay(dpy);
        }
    }

    private static async Task<(int X, int Y)> SettleAsync(X11CursorProvider cursor, int x, int y)
    {
        (int X, int Y) last = default;
        for (int i = 0; i < 50; i++)
        {
            if (cursor.GetCursorPosition() is { } p)
            {
                last = p;
                if (Math.Abs(p.X - x) <= 1 && Math.Abs(p.Y - y) <= 1)
                {
                    return p;
                }
            }

            await Task.Delay(10);
        }

        return last;
    }

    private static void QueryPointer(out int x, out int y, out uint mask)
    {
        nint dpy = Xlib.XOpenDisplay(null);
        try
        {
            Xlib.XQueryPointer(dpy, Xlib.XDefaultRootWindow(dpy), out _, out _, out x, out y, out _, out _, out mask);
        }
        finally
        {
            Xlib.XCloseDisplay(dpy);
        }
    }
}
